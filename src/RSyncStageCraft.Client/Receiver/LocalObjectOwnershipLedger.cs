using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Bounded ownership ledger for exact locally spawned overworld objects. A
/// desired apply generation may advance while retaining a compatible handle,
/// but a spawn-identity change requires an exact release checkpoint before a
/// new spawn generation can be recorded. A short mutation claim fences an
/// ownership-producing spawn through exact receipt publication.
/// </summary>
public sealed class LocalObjectOwnershipLedger
{
    private sealed record Entry(
        LocalObjectApplyToken Token,
        LocalObjectSpawnIdentity DesiredIdentity,
        LocalObjectSpawnIdentity? OwnedIdentity,
        LocalObjectInstanceHandle? Handle,
        LocalObjectInstanceHandle? Compensation,
        LocalObjectApplyToken? AppliedToken,
        long NextSpawnGeneration,
        bool SpawnGenerationExhausted,
        bool Releasing,
        bool Mutating);

    private readonly object gate = new();
    private readonly int maximumObjects;
    private readonly Dictionary<RscObjectId, Entry> entries = new();
    private readonly Dictionary<Guid, RscObjectId> ownershipIds = new();
    private readonly Dictionary<ReceiverSessionKey, LinkedList<RscObjectId>> sessionObjects = new();
    private readonly Dictionary<RscObjectId, LinkedListNode<RscObjectId>> sessionObjectNodes = new();

    public LocalObjectOwnershipLedger(int maximumObjects)
    {
        if (maximumObjects <= 0 || maximumObjects > ProtocolLimits.MaximumObjectsPerSnapshot)
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));

        this.maximumObjects = maximumObjects;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public int OwnedCount
    {
        get
        {
            lock (gate)
                return entries.Values.Count(entry => entry.Handle is not null);
        }
    }

    public int CompensationCount
    {
        get
        {
            lock (gate)
                return entries.Values.Count(entry => entry.Compensation is not null);
        }
    }

    public LocalObjectReservationResult TryReserve(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity)
    {
        if (!token.IsValid || !identity.IsValid)
            return new(LocalObjectReservationStatus.Invalid, null);

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var existing))
            {
                if (entries.Count >= maximumObjects)
                    return new(LocalObjectReservationStatus.CapacityExceeded, null);

                var added = new Entry(
                    token,
                    identity,
                    OwnedIdentity: null,
                    Handle: null,
                    Compensation: null,
                    AppliedToken: null,
                    NextSpawnGeneration: 1,
                    SpawnGenerationExhausted: false,
                    Releasing: false,
                    Mutating: false);
                entries.Add(token.ObjectId, added);
                AddSessionObjectUnsafe(token.Session, token.ObjectId);
                return new(LocalObjectReservationStatus.Advanced, Snapshot(added));
            }

            if (existing.Token.Session != token.Session)
                return new(LocalObjectReservationStatus.SessionConflict, Snapshot(existing));
            if (existing.DesiredIdentity.Kind != identity.Kind)
                return new(LocalObjectReservationStatus.ObjectKindConflict, Snapshot(existing));
            if (existing.Releasing)
                return new(LocalObjectReservationStatus.ReleaseInProgress, Snapshot(existing));
            if (existing.Mutating)
                return new(LocalObjectReservationStatus.MutationInProgress, Snapshot(existing));
            if (token.Generation.Value < existing.Token.Generation.Value)
                return new(LocalObjectReservationStatus.StaleGeneration, Snapshot(existing));

            if (token.Generation == existing.Token.Generation)
            {
                return existing.DesiredIdentity == identity
                    ? new(LocalObjectReservationStatus.Unchanged, Snapshot(existing))
                    : new(LocalObjectReservationStatus.GenerationConflict, Snapshot(existing));
            }

            var advanced = existing with
            {
                Token = token,
                DesiredIdentity = identity,
            };
            entries[token.ObjectId] = advanced;
            return new(LocalObjectReservationStatus.Advanced, Snapshot(advanced));
        }
    }

    public bool TryGet(
        LocalObjectApplyToken token,
        out LocalObjectReservation reservation)
    {
        lock (gate)
        {
            if (entries.TryGetValue(token.ObjectId, out var entry) &&
                entry.Token == token &&
                !entry.Releasing &&
                !entry.Mutating)
            {
                reservation = Snapshot(entry);
                return true;
            }

            reservation = default!;
            return false;
        }
    }

    public bool TryGetObject(
        ReceiverSessionKey session,
        RscObjectId objectId,
        out LocalObjectReservation reservation)
    {
        if (!session.IsValid || !objectId.IsValid)
        {
            reservation = default!;
            return false;
        }

        lock (gate)
        {
            if (entries.TryGetValue(objectId, out var entry) &&
                entry.Token.Session == session)
            {
                reservation = Snapshot(entry);
                return true;
            }

            reservation = default!;
            return false;
        }
    }

    public bool TryRecordSpawn(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity,
        LocalObjectInstanceHandle handle) =>
        TryRecordSpawn(token, identity, handle, mutationClaimed: false);

    public bool TryRecordSpawnMutation(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity,
        LocalObjectInstanceHandle handle) =>
        TryRecordSpawn(token, identity, handle, mutationClaimed: true);

    private bool TryRecordSpawn(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity,
        LocalObjectInstanceHandle handle,
        bool mutationClaimed)
    {
        if (!handle.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.DesiredIdentity != identity ||
                entry.Handle is not null ||
                entry.Compensation is not null ||
                entry.Releasing ||
                entry.Mutating != mutationClaimed ||
                entry.SpawnGenerationExhausted ||
                handle.ObjectId != token.ObjectId ||
                handle.Kind != identity.Kind ||
                handle.SpawnGeneration != entry.NextSpawnGeneration ||
                ownershipIds.ContainsKey(handle.OwnershipId))
            {
                return false;
            }

            ownershipIds.Add(handle.OwnershipId, token.ObjectId);
            entries[token.ObjectId] = entry with
            {
                OwnedIdentity = identity,
                Handle = handle,
            };
            return true;
        }
    }

    public bool TryMarkApplied(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle handle)
    {
        if (!token.IsValid || !handle.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Handle != handle ||
                entry.Releasing ||
                entry.Mutating)
            {
                return false;
            }

            entries[token.ObjectId] = entry with { AppliedToken = token };
            return true;
        }
    }

    /// <summary>
    /// Retains an exact newly-created receipt that could not become the stable
    /// primary. The receipt may describe a mismatched object because that is
    /// precisely the host contract failure cleanup must be able to reverse.
    /// </summary>
    public bool TryRecordCompensation(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle compensation) =>
        TryRecordCompensation(token, compensation, mutationClaimed: false);

    public bool TryRecordCompensationMutation(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle compensation) =>
        TryRecordCompensation(token, compensation, mutationClaimed: true);

    private bool TryRecordCompensation(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle compensation,
        bool mutationClaimed)
    {
        if (!token.IsValid || !compensation.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Compensation is not null ||
                entry.Releasing ||
                entry.Mutating != mutationClaimed ||
                entry.Handle?.OwnershipId == compensation.OwnershipId ||
                ownershipIds.ContainsKey(compensation.OwnershipId))
            {
                return false;
            }

            ownershipIds.Add(compensation.OwnershipId, token.ObjectId);
            entries[token.ObjectId] = entry with { Compensation = compensation };
            return true;
        }
    }

    public bool IsApplied(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        lock (gate)
        {
            return entries.TryGetValue(token.ObjectId, out var entry) &&
                entry.Token == token &&
                entry.AppliedToken == token &&
                entry.Handle is not null &&
                !entry.Releasing &&
                !entry.Mutating;
        }
    }

    public bool TryBeginRelease(
        LocalObjectApplyToken token,
        out LocalObjectInstanceHandle handle) =>
        TryBeginRelease(token, expected: null, out handle);

    public bool TryBeginRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle expected,
        out LocalObjectInstanceHandle handle)
    {
        if (!expected.IsValid)
        {
            handle = default;
            return false;
        }

        return TryBeginRelease(token, (LocalObjectInstanceHandle?)expected, out handle);
    }

    private bool TryBeginRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle? expected,
        out LocalObjectInstanceHandle handle)
    {
        if (!token.IsValid)
        {
            handle = default;
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Handle is not { } owned ||
                expected is { } required && owned != required ||
                entry.Compensation is not null ||
                entry.Releasing ||
                entry.Mutating)
            {
                handle = default;
                return false;
            }

            entries[token.ObjectId] = entry with { Releasing = true };
            handle = owned;
            return true;
        }
    }

    public bool TryBeginCompensationRelease(
        LocalObjectApplyToken token,
        out LocalObjectInstanceHandle compensation) =>
        TryBeginCompensationRelease(token, expected: null, out compensation);

    public bool TryBeginCompensationRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle expected,
        out LocalObjectInstanceHandle compensation)
    {
        if (!expected.IsValid)
        {
            compensation = default;
            return false;
        }

        return TryBeginCompensationRelease(
            token,
            (LocalObjectInstanceHandle?)expected,
            out compensation);
    }

    private bool TryBeginCompensationRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle? expected,
        out LocalObjectInstanceHandle compensation)
    {
        if (!token.IsValid)
        {
            compensation = default;
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Compensation is not { } retained ||
                expected is { } required && retained != required ||
                entry.Releasing ||
                entry.Mutating)
            {
                compensation = default;
                return false;
            }

            entries[token.ObjectId] = entry with { Releasing = true };
            compensation = retained;
            return true;
        }
    }

    public bool TryCompleteCompensationRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle compensation)
    {
        if (!compensation.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                !entry.Releasing ||
                entry.Compensation != compensation ||
                !ownershipIds.TryGetValue(compensation.OwnershipId, out var ownedObjectId) ||
                ownedObjectId != token.ObjectId)
            {
                return false;
            }

            ownershipIds.Remove(compensation.OwnershipId);
            entries[token.ObjectId] = entry with
            {
                Compensation = null,
                Releasing = false,
            };
            return true;
        }
    }

    public bool TryCompleteRelease(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle handle)
    {
        if (!handle.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                !entry.Releasing ||
                entry.Handle != handle ||
                !ownershipIds.TryGetValue(handle.OwnershipId, out var ownedObjectId) ||
                ownedObjectId != token.ObjectId)
            {
                return false;
            }

            ownershipIds.Remove(handle.OwnershipId);
            var exhausted = handle.SpawnGeneration == long.MaxValue;
            entries[token.ObjectId] = entry with
            {
                OwnedIdentity = null,
                Handle = null,
                AppliedToken = null,
                NextSpawnGeneration = exhausted
                    ? long.MaxValue
                    : handle.SpawnGeneration + 1,
                SpawnGenerationExhausted = exhausted,
                Releasing = false,
            };
            return true;
        }
    }

    public bool TryEndRelease(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                !entry.Releasing)
            {
                return false;
            }

            entries[token.ObjectId] = entry with { Releasing = false };
            return true;
        }
    }

    public bool TryBeginSpawnMutation(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity,
        long spawnGeneration,
        out LocalObjectReservation reservation)
    {
        if (!token.IsValid || !identity.IsValid || spawnGeneration <= 0)
        {
            reservation = default!;
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.DesiredIdentity != identity ||
                entry.Handle is not null ||
                entry.Compensation is not null ||
                entry.Releasing ||
                entry.Mutating ||
                entry.SpawnGenerationExhausted ||
                entry.NextSpawnGeneration != spawnGeneration)
            {
                reservation = default!;
                return false;
            }

            entries[token.ObjectId] = entry with { Mutating = true };
            reservation = Snapshot(entry);
            return true;
        }
    }

    public bool TryTransitionMutationToCompensationRelease(
        LocalObjectApplyToken token,
        out LocalObjectInstanceHandle compensation)
    {
        if (!token.IsValid)
        {
            compensation = default;
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Compensation is not { } retained ||
                entry.Releasing ||
                !entry.Mutating)
            {
                compensation = default;
                return false;
            }

            entries[token.ObjectId] = entry with
            {
                Releasing = true,
                Mutating = false,
            };
            compensation = retained;
            return true;
        }
    }

    public bool TryEndMutation(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Releasing ||
                !entry.Mutating)
            {
                return false;
            }

            entries[token.ObjectId] = entry with { Mutating = false };
            return true;
        }
    }

    public bool TryRemoveEmpty(LocalObjectApplyToken token)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(token.ObjectId, out var entry) ||
                entry.Token != token ||
                entry.Handle is not null ||
                entry.Compensation is not null ||
                entry.Releasing ||
                entry.Mutating)
            {
                return false;
            }

            entries.Remove(token.ObjectId);
            RemoveSessionObjectUnsafe(token.Session, token.ObjectId);
            return true;
        }
    }

    public IReadOnlyList<LocalObjectReservation> GetSessionBatch(
        ReceiverSessionKey session,
        int maximumEntries)
    {
        if (!session.IsValid)
            return Array.Empty<LocalObjectReservation>();
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        lock (gate)
        {
            if (!sessionObjects.TryGetValue(session, out var objectIds))
                return Array.Empty<LocalObjectReservation>();

            var selected = new List<LocalObjectReservation>(
                Math.Min(maximumEntries, objectIds.Count));
            var count = Math.Min(maximumEntries, objectIds.Count);
            for (var index = 0; index < count; index++)
            {
                var node = objectIds.First!;
                objectIds.RemoveFirst();
                objectIds.AddLast(node);

                if (entries.TryGetValue(node.Value, out var entry) &&
                    entry.Token.Session == session)
                {
                    selected.Add(Snapshot(entry));
                }
            }

            return selected;
        }
    }

    public bool HasSessionEntries(ReceiverSessionKey session)
    {
        lock (gate)
            return sessionObjects.TryGetValue(session, out var objects) && objects.Count > 0;
    }

    /// <summary>True when the session holds an entry of a kind <paramref name="kinds"/> accepts.</summary>
    public bool HasSessionEntries(ReceiverSessionKey session, Func<RscObjectKind, bool> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        lock (gate)
        {
            if (!sessionObjects.TryGetValue(session, out var objects))
                return false;
            foreach (var objectId in objects)
            {
                if (entries.TryGetValue(objectId, out var entry) &&
                    entry.Token.Session == session &&
                    kinds(entry.DesiredIdentity.Kind))
                    return true;
            }
            return false;
        }
    }

    public int GetSessionEntryCount(ReceiverSessionKey session)
    {
        if (!session.IsValid)
            return 0;

        lock (gate)
            return sessionObjects.TryGetValue(session, out var objects)
                ? objects.Count
                : 0;
    }

    private static LocalObjectReservation Snapshot(Entry entry) => new(
        entry.Token,
        entry.DesiredIdentity,
        entry.OwnedIdentity,
        entry.Handle,
        entry.Compensation,
        entry.NextSpawnGeneration,
        entry.SpawnGenerationExhausted);

    private void AddSessionObjectUnsafe(
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        if (!sessionObjects.TryGetValue(session, out var objectIds))
        {
            objectIds = new LinkedList<RscObjectId>();
            sessionObjects.Add(session, objectIds);
        }

        sessionObjectNodes.Add(objectId, objectIds.AddLast(objectId));
    }

    private void RemoveSessionObjectUnsafe(
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        if (sessionObjectNodes.Remove(objectId, out var objectNode) &&
            sessionObjects.TryGetValue(session, out var objectIds))
        {
            objectIds.Remove(objectNode);
            if (objectIds.Count == 0)
                sessionObjects.Remove(session);
        }
    }
}
