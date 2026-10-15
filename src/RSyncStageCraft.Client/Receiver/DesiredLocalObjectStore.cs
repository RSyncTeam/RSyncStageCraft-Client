using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Bounded desired-state store for receiver-side furniture and lights. It is
/// also the final generation fence used inside dispatched local runtime calls.
/// </summary>
public sealed class DesiredLocalObjectStore : ILocalObjectDesiredFence
{
    private readonly object gate = new();
    private readonly ReceiverApplyAuthority authority;
    private readonly int maximumObjects;
    private readonly Dictionary<RscObjectId, DesiredLocalObjectState> states = new();
    private readonly Dictionary<RscObjectId, DesiredLocalObjectRemoval> removals = new();
    private readonly Dictionary<ReceiverSessionKey, HashSet<RscObjectId>> sessionObjects = new();

    public DesiredLocalObjectStore(
        ReceiverApplyAuthority authority,
        int maximumObjects = ProtocolLimits.MaximumObjectsPerSnapshot)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (maximumObjects <= 0 || maximumObjects > ProtocolLimits.MaximumObjectsPerSnapshot)
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));

        this.authority = authority;
        this.maximumObjects = maximumObjects;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return states.Count + removals.Count;
        }
    }

    public DesiredLocalObjectAdvanceResult Advance(
        ReceiverSessionKey session,
        AuthoritativeObjectState state,
        string fingerprint)
    {
        if (!session.IsValid ||
            state is not (ActorState or FurnitureState or LightState) ||
            !state.IsValid ||
            string.IsNullOrWhiteSpace(fingerprint) ||
            fingerprint.Length > ClientLimits.MaximumDesiredStateFingerprintLength)
        {
            return new(DesiredLocalObjectAdvanceStatus.Invalid, null);
        }

        return authority.TryExecute(
            session,
            () => AdvanceUnsafe(session, state, fingerprint),
            out var result)
            ? result
            : new(DesiredLocalObjectAdvanceStatus.InactiveSession, null);
    }

    public DesiredLocalObjectRemovalResult AdvanceRemoval(
        ReceiverSessionKey session,
        ObjectTombstone tombstone)
    {
        if (!session.IsValid ||
            !tombstone.IsValid ||
            tombstone.Kind is not (RscObjectKind.Actor or RscObjectKind.Furniture or RscObjectKind.Light))
        {
            return new(DesiredLocalObjectRemovalStatus.Invalid, null);
        }

        return authority.TryExecute(
            session,
            () => AdvanceRemovalUnsafe(session, tombstone),
            out var result)
            ? result
            : new(DesiredLocalObjectRemovalStatus.InactiveSession, null);
    }

    public DesiredLocalObjectSnapshotResult ApplySnapshot(
        ReceiverSessionKey session,
        IReadOnlyList<DesiredLocalObjectSnapshotState> objectStates,
        IReadOnlyList<ObjectTombstone> objectTombstones)
    {
        if (!TryPrepareSnapshotInput(
                session,
                objectStates,
                objectTombstones,
                out var boundedStates,
                out var boundedTombstones))
            return FailedSnapshot(DesiredLocalObjectSnapshotStatus.Invalid);

        var identities = new HashSet<RscObjectId>();
        if (!boundedStates.All(entry => identities.Add(entry.State.ObjectId)) ||
            !boundedTombstones.All(tombstone => identities.Add(tombstone.ObjectId)))
        {
            return FailedSnapshot(DesiredLocalObjectSnapshotStatus.Invalid);
        }

        return authority.TryExecute(
            session,
            () => ApplySnapshotUnsafe(
                session,
                boundedStates,
                boundedTombstones,
                identities,
                commit: true),
            out var result)
            ? result
            : FailedSnapshot(DesiredLocalObjectSnapshotStatus.InactiveSession);
    }

    internal DesiredLocalObjectSnapshotResult ValidateSnapshot(
        ReceiverSessionKey session,
        IReadOnlyList<DesiredLocalObjectSnapshotState> objectStates,
        IReadOnlyList<ObjectTombstone> objectTombstones)
    {
        if (!TryPrepareSnapshotInput(
                session,
                objectStates,
                objectTombstones,
                out var boundedStates,
                out var boundedTombstones))
            return FailedSnapshot(DesiredLocalObjectSnapshotStatus.Invalid);

        var identities = new HashSet<RscObjectId>();
        if (!boundedStates.All(entry => identities.Add(entry.State.ObjectId)) ||
            !boundedTombstones.All(tombstone => identities.Add(tombstone.ObjectId)))
        {
            return FailedSnapshot(DesiredLocalObjectSnapshotStatus.Invalid);
        }

        return authority.TryExecute(
            session,
            () => ApplySnapshotUnsafe(
                session,
                boundedStates,
                boundedTombstones,
                identities,
                commit: false),
            out var result)
            ? result
            : FailedSnapshot(DesiredLocalObjectSnapshotStatus.InactiveSession);
    }

    public bool TryGetCurrent(
        ReceiverSessionKey session,
        RscObjectId objectId,
        out DesiredLocalObjectState state)
    {
        if (!objectId.IsValid)
        {
            state = default!;
            return false;
        }

        var found = authority.TryExecute(
            session,
            () =>
            {
                lock (gate)
                    return states.TryGetValue(objectId, out var value) && value.Session == session
                        ? value
                        : null;
            },
            out DesiredLocalObjectState? current);

        state = current!;
        return found && current is not null;
    }

    public bool TryGetRemoval(
        ReceiverSessionKey session,
        RscObjectId objectId,
        out DesiredLocalObjectRemoval removal)
    {
        if (!objectId.IsValid)
        {
            removal = default!;
            return false;
        }

        var found = authority.TryExecute(
            session,
            () =>
            {
                lock (gate)
                    return removals.TryGetValue(objectId, out var value) && value.Session == session
                        ? value
                        : null;
            },
            out DesiredLocalObjectRemoval? current);

        removal = current!;
        return found && current is not null;
    }

    public bool TryMarkRemovalCompleted(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        return authority.TryExecute(
            token.Session,
            () =>
            {
                lock (gate)
                {
                    if (!removals.TryGetValue(token.ObjectId, out var removal) ||
                        removal.Token != token)
                    {
                        return false;
                    }

                    removals[token.ObjectId] = removal with { Completed = true };
                    return true;
                }
            },
            out var completed) && completed;
    }

    public bool IsCurrent(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        return authority.TryExecute(
            token.Session,
            () =>
            {
                lock (gate)
                    return IsCurrentUnsafe(token);
            },
            out var current) && current;
    }

    internal bool TryExecuteCurrent<T>(
        LocalObjectApplyToken token,
        Func<T> operation,
        out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!token.IsValid)
        {
            result = default!;
            return false;
        }

        var executed = authority.TryExecute(
            token.Session,
            () =>
            {
                lock (gate)
                {
                    if (!IsCurrentUnsafe(token))
                        return (Current: false, Value: default(T)!);

                    return (Current: true, Value: operation());
                }
            },
            out var guarded);

        result = guarded.Value;
        return executed && guarded.Current;
    }

    bool ILocalObjectDesiredFence.TryExecuteCurrent<T>(
        LocalObjectApplyToken token,
        Func<T> operation,
        out T result) => TryExecuteCurrent(token, operation, out result);

    public int RemoveSession(ReceiverSessionKey session, int maximumEntries)
    {
        if (!session.IsValid)
            return 0;
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        lock (gate)
        {
            if (!sessionObjects.TryGetValue(session, out var objectIds))
                return 0;

            var selected = objectIds.Take(maximumEntries).ToArray();
            var removed = 0;
            foreach (var objectId in selected)
            {
                objectIds.Remove(objectId);
                var removedState = states.TryGetValue(objectId, out var state) &&
                    state.Session == session &&
                    states.Remove(objectId);
                var removedTombstone = removals.TryGetValue(objectId, out var removal) &&
                    removal.Session == session &&
                    removals.Remove(objectId);
                if (removedState || removedTombstone)
                    removed++;
            }

            if (objectIds.Count == 0)
                sessionObjects.Remove(session);

            return removed;
        }
    }

    /// <summary>Objects of the session that are desired (not removed), for re-admission after a domain reset.</summary>
    internal IReadOnlyList<RscObjectId> GetSessionStateIds(ReceiverSessionKey session)
    {
        lock (gate)
        {
            return sessionObjects.TryGetValue(session, out var ids)
                ? ids.Where(id => states.TryGetValue(id, out var value) && value.Session == session).ToArray()
                : [];
        }
    }

    /// <summary>The desired (not removed) states of the session, for display.</summary>
    internal IReadOnlyList<AuthoritativeObjectState> GetSessionStates(ReceiverSessionKey session)
    {
        lock (gate)
        {
            return sessionObjects.TryGetValue(session, out var ids)
                ? ids.Select(id => states.TryGetValue(id, out var value) && value.Session == session ? value.State : null)
                    .OfType<AuthoritativeObjectState>()
                    .ToArray()
                : [];
        }
    }

    public bool HasSessionEntries(ReceiverSessionKey session)
    {
        lock (gate)
            return sessionObjects.TryGetValue(session, out var objects) && objects.Count > 0;
    }

    private DesiredLocalObjectAdvanceResult AdvanceUnsafe(
        ReceiverSessionKey session,
        AuthoritativeObjectState state,
        string fingerprint)
    {
        lock (gate)
        {
            if (removals.TryGetValue(state.ObjectId, out var removed))
            {
                if (removed.Session == session)
                    return new(DesiredLocalObjectAdvanceStatus.StaleRevision, null);

                removals.Remove(state.ObjectId);
                RemoveSessionObjectUnsafe(removed.Session, removed.ObjectId);
            }

            if (!states.TryGetValue(state.ObjectId, out var current))
            {
                if (states.Count + removals.Count >= maximumObjects)
                    return new(DesiredLocalObjectAdvanceStatus.CapacityExceeded, null);

                var added = new DesiredLocalObjectState(
                    session,
                    state,
                    fingerprint,
                    new ApplyGeneration(1));
                states.Add(state.ObjectId, added);
                AddSessionObjectUnsafe(session, state.ObjectId);
                return new(DesiredLocalObjectAdvanceStatus.Advanced, added);
            }

            if (current.Session != session)
            {
                RemoveSessionObjectUnsafe(current.Session, current.ObjectId);
                var replacement = new DesiredLocalObjectState(
                    session,
                    state,
                    fingerprint,
                    new ApplyGeneration(1));
                states[state.ObjectId] = replacement;
                AddSessionObjectUnsafe(session, state.ObjectId);
                return new(DesiredLocalObjectAdvanceStatus.Advanced, replacement);
            }
            if (current.State.Kind != state.Kind)
                return new(DesiredLocalObjectAdvanceStatus.ObjectKindConflict, current);
            if (state.Revision.Value < current.AuthoritativeRevision.Value)
                return new(DesiredLocalObjectAdvanceStatus.StaleRevision, current);
            if (state.Revision == current.AuthoritativeRevision)
            {
                return string.Equals(fingerprint, current.Fingerprint, StringComparison.Ordinal)
                    ? new(DesiredLocalObjectAdvanceStatus.Unchanged, current)
                    : new(DesiredLocalObjectAdvanceStatus.RevisionConflict, current);
            }
            if (string.Equals(fingerprint, current.Fingerprint, StringComparison.Ordinal))
            {
                var revisionOnly = current with { State = state };
                states[state.ObjectId] = revisionOnly;
                return new(DesiredLocalObjectAdvanceStatus.RevisionAdvanced, revisionOnly);
            }
            if (!current.ApplyGeneration.TryAdvance(out var nextGeneration))
                return new(DesiredLocalObjectAdvanceStatus.GenerationExhausted, current);

            var advanced = new DesiredLocalObjectState(
                session,
                state,
                fingerprint,
                nextGeneration);
            states[state.ObjectId] = advanced;
            return new(DesiredLocalObjectAdvanceStatus.Advanced, advanced);
        }
    }

    private DesiredLocalObjectRemovalResult AdvanceRemovalUnsafe(
        ReceiverSessionKey session,
        ObjectTombstone tombstone)
    {
        lock (gate)
        {
            if (removals.TryGetValue(tombstone.ObjectId, out var currentRemoval))
            {
                if (currentRemoval.Session != session)
                {
                    RemoveSessionObjectUnsafe(currentRemoval.Session, currentRemoval.ObjectId);
                    var replacement = new DesiredLocalObjectRemoval(
                        session,
                        tombstone,
                        new ApplyGeneration(1),
                        Completed: false);
                    removals[tombstone.ObjectId] = replacement;
                    AddSessionObjectUnsafe(session, tombstone.ObjectId);
                    return new(DesiredLocalObjectRemovalStatus.Advanced, replacement);
                }
                if (currentRemoval.Tombstone.Kind != tombstone.Kind)
                    return new(DesiredLocalObjectRemovalStatus.ObjectKindConflict, currentRemoval);
                if (tombstone.DeletionRevision.Value < currentRemoval.AuthoritativeRevision.Value)
                    return new(DesiredLocalObjectRemovalStatus.StaleRevision, currentRemoval);
                if (tombstone.DeletionRevision == currentRemoval.AuthoritativeRevision)
                    return new(DesiredLocalObjectRemovalStatus.Unchanged, currentRemoval);
                if (!currentRemoval.ApplyGeneration.TryAdvance(out var nextGeneration))
                    return new(DesiredLocalObjectRemovalStatus.GenerationExhausted, currentRemoval);

                var advanced = new DesiredLocalObjectRemoval(
                    session,
                    tombstone,
                    nextGeneration,
                    Completed: false);
                removals[tombstone.ObjectId] = advanced;
                return new(DesiredLocalObjectRemovalStatus.Advanced, advanced);
            }

            if (states.TryGetValue(tombstone.ObjectId, out var currentState))
            {
                if (currentState.Session == session && currentState.State.Kind != tombstone.Kind)
                    return new(DesiredLocalObjectRemovalStatus.ObjectKindConflict, null);
                if (currentState.Session == session &&
                    tombstone.DeletionRevision.Value <= currentState.AuthoritativeRevision.Value)
                {
                    return new(DesiredLocalObjectRemovalStatus.StaleRevision, null);
                }

                var nextGeneration = new ApplyGeneration(1);
                if (currentState.Session == session &&
                    !currentState.ApplyGeneration.TryAdvance(out nextGeneration))
                {
                    return new(DesiredLocalObjectRemovalStatus.GenerationExhausted, null);
                }

                states.Remove(tombstone.ObjectId);
                RemoveSessionObjectUnsafe(currentState.Session, currentState.ObjectId);
                var removed = new DesiredLocalObjectRemoval(
                    session,
                    tombstone,
                    nextGeneration,
                    Completed: false);
                removals.Add(tombstone.ObjectId, removed);
                AddSessionObjectUnsafe(session, tombstone.ObjectId);
                return new(DesiredLocalObjectRemovalStatus.Advanced, removed);
            }

            if (states.Count + removals.Count >= maximumObjects)
                return new(DesiredLocalObjectRemovalStatus.CapacityExceeded, null);

            var added = new DesiredLocalObjectRemoval(
                session,
                tombstone,
                new ApplyGeneration(1),
                Completed: false);
            removals.Add(tombstone.ObjectId, added);
            AddSessionObjectUnsafe(session, tombstone.ObjectId);
            return new(DesiredLocalObjectRemovalStatus.Advanced, added);
        }
    }

    private DesiredLocalObjectSnapshotResult ApplySnapshotUnsafe(
        ReceiverSessionKey session,
        IReadOnlyList<DesiredLocalObjectSnapshotState> objectStates,
        IReadOnlyList<ObjectTombstone> objectTombstones,
        HashSet<RscObjectId> incomingIdentities,
        bool commit)
    {
        lock (gate)
        {
            if (sessionObjects.TryGetValue(session, out var currentSessionObjects) &&
                currentSessionObjects.Any(objectId => !incomingIdentities.Contains(objectId)))
            {
                return FailedSnapshot(DesiredLocalObjectSnapshotStatus.IncompleteSnapshot);
            }

            var replacedCount = incomingIdentities.Count(objectId =>
                states.ContainsKey(objectId) || removals.ContainsKey(objectId));
            var resultingCount = states.Count + removals.Count - replacedCount + incomingIdentities.Count;
            if (resultingCount > maximumObjects)
                return FailedSnapshot(DesiredLocalObjectSnapshotStatus.CapacityExceeded);

            var plannedStates = new Dictionary<RscObjectId, DesiredLocalObjectState>();
            var plannedRemovals = new Dictionary<RscObjectId, DesiredLocalObjectRemoval>();

            foreach (var incoming in objectStates)
            {
                var objectId = incoming.State.ObjectId;
                if (removals.TryGetValue(objectId, out var currentRemoval) &&
                    currentRemoval.Session == session)
                {
                    return FailedSnapshot(DesiredLocalObjectSnapshotStatus.RevisionConflict);
                }

                if (states.TryGetValue(objectId, out var currentState) &&
                    currentState.Session == session)
                {
                    if (currentState.State.Kind != incoming.State.Kind)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.ObjectKindConflict);
                    if (incoming.State.Revision.Value < currentState.AuthoritativeRevision.Value)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.StaleRevision);
                    if (incoming.State.Revision == currentState.AuthoritativeRevision)
                    {
                        if (!string.Equals(
                            incoming.Fingerprint,
                            currentState.Fingerprint,
                            StringComparison.Ordinal))
                        {
                            return FailedSnapshot(DesiredLocalObjectSnapshotStatus.RevisionConflict);
                        }

                        plannedStates.Add(objectId, currentState);
                        continue;
                    }
                    if (string.Equals(
                        incoming.Fingerprint,
                        currentState.Fingerprint,
                        StringComparison.Ordinal))
                    {
                        plannedStates.Add(objectId, currentState with { State = incoming.State });
                        continue;
                    }
                    if (!currentState.ApplyGeneration.TryAdvance(out var nextGeneration))
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.GenerationExhausted);

                    plannedStates.Add(
                        objectId,
                        new DesiredLocalObjectState(
                            session,
                            incoming.State,
                            incoming.Fingerprint,
                            nextGeneration));
                    continue;
                }

                plannedStates.Add(
                    objectId,
                    new DesiredLocalObjectState(
                        session,
                        incoming.State,
                        incoming.Fingerprint,
                        new ApplyGeneration(1)));
            }

            foreach (var tombstone in objectTombstones)
            {
                var objectId = tombstone.ObjectId;
                if (removals.TryGetValue(objectId, out var currentRemoval) &&
                    currentRemoval.Session == session)
                {
                    if (currentRemoval.Tombstone.Kind != tombstone.Kind)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.ObjectKindConflict);
                    if (tombstone.DeletionRevision.Value < currentRemoval.AuthoritativeRevision.Value)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.StaleRevision);
                    if (tombstone.DeletionRevision == currentRemoval.AuthoritativeRevision)
                    {
                        plannedRemovals.Add(objectId, currentRemoval);
                        continue;
                    }
                    if (!currentRemoval.ApplyGeneration.TryAdvance(out var nextGeneration))
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.GenerationExhausted);

                    plannedRemovals.Add(
                        objectId,
                        new DesiredLocalObjectRemoval(
                            session,
                            tombstone,
                            nextGeneration,
                            Completed: false));
                    continue;
                }

                if (states.TryGetValue(objectId, out var currentState) &&
                    currentState.Session == session)
                {
                    if (currentState.State.Kind != tombstone.Kind)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.ObjectKindConflict);
                    if (tombstone.DeletionRevision.Value <= currentState.AuthoritativeRevision.Value)
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.StaleRevision);
                    if (!currentState.ApplyGeneration.TryAdvance(out var nextGeneration))
                        return FailedSnapshot(DesiredLocalObjectSnapshotStatus.GenerationExhausted);

                    plannedRemovals.Add(
                        objectId,
                        new DesiredLocalObjectRemoval(
                            session,
                            tombstone,
                            nextGeneration,
                            Completed: false));
                    continue;
                }

                plannedRemovals.Add(
                    objectId,
                    new DesiredLocalObjectRemoval(
                        session,
                        tombstone,
                        new ApplyGeneration(1),
                        Completed: false));
            }

            var work = plannedStates.Values
                .Select(state => new LocalObjectApplyWorkItem(state))
                .Concat(plannedRemovals.Values
                    .Where(removal => !removal.Completed)
                    .Select(removal => new LocalObjectApplyWorkItem(removal)))
                .ToArray();
            if (!commit)
                return new(DesiredLocalObjectSnapshotStatus.Applied, work);

            foreach (var objectId in incomingIdentities)
            {
                if (states.Remove(objectId, out var oldState))
                    RemoveSessionObjectUnsafe(oldState.Session, objectId);
                if (removals.Remove(objectId, out var oldRemoval))
                    RemoveSessionObjectUnsafe(oldRemoval.Session, objectId);
            }

            foreach (var pair in plannedStates)
            {
                states.Add(pair.Key, pair.Value);
                AddSessionObjectUnsafe(session, pair.Key);
            }
            foreach (var pair in plannedRemovals)
            {
                removals.Add(pair.Key, pair.Value);
                AddSessionObjectUnsafe(session, pair.Key);
            }

            return new(DesiredLocalObjectSnapshotStatus.Applied, work);
        }
    }

    private bool TryPrepareSnapshotInput(
        ReceiverSessionKey session,
        IReadOnlyList<DesiredLocalObjectSnapshotState> objectStates,
        IReadOnlyList<ObjectTombstone> objectTombstones,
        out DesiredLocalObjectSnapshotState[] boundedStates,
        out ObjectTombstone[] boundedTombstones)
    {
        boundedStates = [];
        boundedTombstones = [];
        return session.IsValid &&
            ProtocolLimits.TryCopyBoundedPair(
            objectStates,
            objectTombstones,
            maximumObjects,
            out boundedStates,
            out boundedTombstones) &&
            boundedStates.All(entry =>
                entry.State is ActorState or FurnitureState or LightState &&
                entry.State.IsValid &&
                !string.IsNullOrWhiteSpace(entry.Fingerprint) &&
                entry.Fingerprint.Length <= ClientLimits.MaximumDesiredStateFingerprintLength) &&
            boundedTombstones.All(tombstone =>
                tombstone.IsValid &&
                tombstone.Kind is RscObjectKind.Actor or RscObjectKind.Furniture or RscObjectKind.Light);
    }

    private static DesiredLocalObjectSnapshotResult FailedSnapshot(
        DesiredLocalObjectSnapshotStatus status) =>
        new(status, Array.Empty<LocalObjectApplyWorkItem>());

    private bool IsCurrentUnsafe(LocalObjectApplyToken token) =>
        (states.TryGetValue(token.ObjectId, out var state) &&
         state.Session == token.Session &&
         state.ApplyGeneration == token.Generation) ||
        (removals.TryGetValue(token.ObjectId, out var removal) &&
         removal.Session == token.Session &&
         removal.ApplyGeneration == token.Generation);

    private void AddSessionObjectUnsafe(
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        if (!sessionObjects.TryGetValue(session, out var objectIds))
        {
            objectIds = new HashSet<RscObjectId>();
            sessionObjects.Add(session, objectIds);
        }

        objectIds.Add(objectId);
    }

    private void RemoveSessionObjectUnsafe(
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        if (!sessionObjects.TryGetValue(session, out var objectIds))
            return;

        objectIds.Remove(objectId);
        if (objectIds.Count == 0)
            sessionObjects.Remove(session);
    }
}
