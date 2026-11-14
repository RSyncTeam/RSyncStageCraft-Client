using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>The outcome of one human actor change admitted from the ordered object stream.</summary>
public enum HumanActorDesiredStatus
{
    Accepted,
    Unchanged,
    StaleRevision,
    Invalid,
    CapacityExceeded,
}

/// <summary>
/// One read of the desired shared human actors: the receiver session they
/// belong to, a version that changes with every change, and the states in
/// object-id order.
/// </summary>
public sealed record HumanActorDesiredView(ReceiverSessionKey? Session, long Version, IReadOnlyList<HumanActorState> Humans)
{
    public static readonly HumanActorDesiredView Empty = new(null, 0, []);
}

/// <summary>
/// Protocol 7: the fourth apply domain's desired state, the shared human
/// actors of the active receiver session. It is fed only by the receiver's
/// ordered intake (snapshot staging, incremental events and session cleanup,
/// each inside the receiver authority), holds plain values, makes no callback
/// and touches nothing in the game. A human host reads it on its own tick
/// (<see cref="Read"/>) and converges on it; losing the connection clears it,
/// so the host cleans up. Furniture, lights and base-game actors never pass
/// through here, and humans never pass through their store.
/// </summary>
public sealed class HumanActorDesiredSet
{
    /// <summary>The same bound as every other desired-state store.</summary>
    public const int MaximumHumans = ProtocolLimits.MaximumObjectsPerSnapshot;

    private readonly object gate = new();
    private readonly Dictionary<RscObjectId, HumanActorState> humans = [];
    private ReceiverSessionKey? session;
    private long version;

    /// <summary>The states held now, with their session and version.</summary>
    public HumanActorDesiredView Read()
    {
        lock (gate)
        {
            return new(session, version, humans.Values.OrderBy(human => human.ObjectId.Value).ToArray());
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
                return humans.Count;
        }
    }

    /// <summary>One created or updated human state of <paramref name="receiverSession"/>.</summary>
    internal HumanActorDesiredStatus Advance(ReceiverSessionKey receiverSession, HumanActorState state)
    {
        if (!receiverSession.IsValid || state is null || !state.IsValid)
            return HumanActorDesiredStatus.Invalid;
        lock (gate)
        {
            Adopt(receiverSession);
            if (humans.TryGetValue(state.ObjectId, out var held))
            {
                if (state.Revision.Value < held.Revision.Value)
                    return HumanActorDesiredStatus.StaleRevision;
                if (state == held)
                    return HumanActorDesiredStatus.Unchanged;
                // The bundle id is immutable: a state naming another bundle for the same object is refused.
                if (!string.Equals(state.AppearanceBundleId, held.AppearanceBundleId, StringComparison.Ordinal) ||
                    state.OwnerId != held.OwnerId)
                    return HumanActorDesiredStatus.Invalid;
            }
            else if (humans.Count >= MaximumHumans)
            {
                return HumanActorDesiredStatus.CapacityExceeded;
            }
            humans[state.ObjectId] = state;
            version++;
            return HumanActorDesiredStatus.Accepted;
        }
    }

    /// <summary>One deleted human of <paramref name="receiverSession"/>.</summary>
    internal HumanActorDesiredStatus Remove(ReceiverSessionKey receiverSession, ObjectTombstone tombstone)
    {
        if (!receiverSession.IsValid || !tombstone.IsValid || tombstone.Kind != RscObjectKind.Human)
            return HumanActorDesiredStatus.Invalid;
        lock (gate)
        {
            Adopt(receiverSession);
            if (!humans.TryGetValue(tombstone.ObjectId, out var held))
                return HumanActorDesiredStatus.Unchanged;
            if (tombstone.DeletionRevision.Value <= held.Revision.Value)
                return HumanActorDesiredStatus.StaleRevision;
            humans.Remove(tombstone.ObjectId);
            version++;
            return HumanActorDesiredStatus.Accepted;
        }
    }

    /// <summary>Whether a snapshot holding <paramref name="humanCount"/> humans can replace the set.</summary>
    internal static bool CanApplySnapshot(int humanCount) => humanCount is >= 0 and <= MaximumHumans;

    /// <summary>
    /// A validated snapshot: its humans replace every human held, for
    /// <paramref name="receiverSession"/>. Its human tombstones need nothing
    /// more (a replaced set holds none of them).
    /// </summary>
    internal void ApplySnapshot(ReceiverSessionKey receiverSession, IReadOnlyList<HumanActorState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (!receiverSession.IsValid || !CanApplySnapshot(states.Count) || states.Any(state => state is null || !state.IsValid))
            throw new ArgumentException("A validated human snapshot is required.", nameof(states));
        lock (gate)
        {
            session = receiverSession;
            humans.Clear();
            foreach (var state in states)
                humans[state.ObjectId] = state;
            version++;
        }
    }

    /// <summary>The session ended (disconnect, sign-out, protocol refusal): every human is no longer desired.</summary>
    internal bool ClearSession(ReceiverSessionKey receiverSession)
    {
        lock (gate)
        {
            if (session != receiverSession)
                return false;
            session = null;
            if (humans.Count != 0)
                humans.Clear();
            version++;
            return true;
        }
    }

    private void Adopt(ReceiverSessionKey receiverSession)
    {
        if (session == receiverSession)
            return;
        // Only the current receiver session reaches here (the authority is
        // held by the caller); a successor session never inherits a state.
        session = receiverSession;
        humans.Clear();
        version++;
    }
}
