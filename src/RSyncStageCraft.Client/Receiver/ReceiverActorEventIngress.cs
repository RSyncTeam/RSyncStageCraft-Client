using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public enum ReceiverActorIngressStatus
{
    Accepted,
    IgnoredUnsupportedKind,
    Invalid,
    InactiveSession,
    NotInitialized,
    StaleSequence,
    SequenceGap,
    StateRejected,
    AdmissionBackpressure,
}

public readonly record struct ReceiverActorIngressResult(
    ReceiverActorIngressStatus Status,
    ServerSequence CurrentSequence)
{
    public LocalObjectApplyToken LocalWorkToken { get; init; }

    public bool Accepted => Status is
        ReceiverActorIngressStatus.Accepted or
        ReceiverActorIngressStatus.IgnoredUnsupportedKind;
}

/// <summary>
/// Admits authoritative incremental object events only after a validated
/// snapshot watermark has initialized the sequence cursor. Sequence advances
/// only after desired state and bounded work admission both succeed, so
/// capacity pressure is retryable without losing an event.
/// </summary>
public sealed class ReceiverActorEventIngress
{
    private readonly object gate = new();
    private readonly ReceiverApplyAuthority authority;
    private readonly ReceiverLocalObjectIngress? localObjectIngress;
    private readonly IReceiverReadinessPort? readiness;
    private readonly IReceiverReadinessAdmissionFence? readinessAdmissionFence;
    private ReceiverSessionKey? session;
    private ServerSequence currentSequence;

    public ReceiverActorEventIngress(
        ReceiverApplyAuthority authority,
        ReceiverLocalObjectIngress? localObjectIngress = null,
        IReceiverReadinessPort? readiness = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        this.authority = authority;
        this.localObjectIngress = localObjectIngress;
        this.readiness = readiness;
        readinessAdmissionFence = readiness as IReceiverReadinessAdmissionFence;
    }

    public ServerSequence CurrentSequence
    {
        get
        {
            lock (gate)
                return currentSequence;
        }
    }

    public bool IsInitializedFor(
        ReceiverSessionKey receiverSession,
        ServerSequence watermark)
    {
        lock (gate)
            return session == receiverSession && currentSequence == watermark;
    }

    public bool IsInitializedForSession(ReceiverSessionKey receiverSession)
    {
        lock (gate)
            return session == receiverSession;
    }

    /// <summary>
    /// Initializes incremental intake only after the caller has validated and
    /// atomically staged the corresponding authoritative snapshot.
    /// </summary>
    internal bool InitializeFromSnapshotWatermark(
        ReceiverSessionKey receiverSession,
        ServerSequence watermark)
    {
        if (!receiverSession.IsValid || !watermark.IsValid)
            return false;

        return authority.TryExecute(
            receiverSession,
            () =>
            {
                lock (gate)
                {
                    if (session == receiverSession)
                        return currentSequence == watermark;

                    session = receiverSession;
                    currentSequence = watermark;
                    return true;
                }
            },
            out var initialized) && initialized;
    }

    public ReceiverActorIngressResult AcceptCreated(
        ReceiverSessionKey receiverSession,
        ObjectCreatedMessage message) =>
        AcceptState(receiverSession, message.Sequence, message.State, message.IsValid);

    public ReceiverActorIngressResult AcceptUpdated(
        ReceiverSessionKey receiverSession,
        ObjectUpdatedMessage message) =>
        AcceptState(receiverSession, message.Sequence, message.State, message.IsValid);

    public ReceiverActorIngressResult AcceptDeleted(
        ReceiverSessionKey receiverSession,
        ObjectDeletedMessage message)
    {
        if (!message.IsValid)
            return Result(ReceiverActorIngressStatus.Invalid);

        ReceiverActorIngressResult Commit()
        {
            var authorized = authority.TryExecute(
                receiverSession,
                () =>
                {
                    lock (gate)
                    {
                        var sequenceStatus = ValidateSequenceUnsafe(
                            receiverSession,
                            message.Sequence);
                        if (sequenceStatus is not null)
                            return ResultUnsafe(sequenceStatus.Value);

                        if (localObjectIngress is null)
                        {
                            currentSequence = message.Sequence;
                            return ResultUnsafe(
                                ReceiverActorIngressStatus.IgnoredUnsupportedKind);
                        }

                        var local = localObjectIngress.AcceptRemoval(
                            receiverSession,
                            message.Tombstone);
                        if (!local.Accepted)
                            return FromLocalUnsafe(local);

                        currentSequence = message.Sequence;
                        return FromLocalUnsafe(local);
                    }
                },
                out var result);

            return authorized
                ? result
                : Result(ReceiverActorIngressStatus.InactiveSession);
        }

        return TryExecuteIfReady(Commit, out var admitted)
            ? admitted
            : Result(ReceiverActorIngressStatus.InactiveSession);
    }

    public bool ClearSession(ReceiverSessionKey receiverSession)
    {
        lock (gate)
        {
            if (session != receiverSession)
                return false;

            session = null;
            currentSequence = default;
            return true;
        }
    }

    private ReceiverActorIngressResult AcceptState(
        ReceiverSessionKey receiverSession,
        ServerSequence sequence,
        AuthoritativeObjectState state,
        bool messageIsValid)
    {
        if (!messageIsValid || state is null)
            return Result(ReceiverActorIngressStatus.Invalid);

        if (!authority.IsCurrent(receiverSession))
            return Result(ReceiverActorIngressStatus.InactiveSession);

        // Reject obviously stale/gapped work before the potentially
        // adapter-supplied fingerprint call. This is only a point-in-time
        // read; the sequence is checked again inside the final authority
        // transaction below.
        lock (gate)
        {
            var preliminaryStatus = ValidateSequenceUnsafe(
                receiverSession,
                sequence);
            if (preliminaryStatus is not null)
                return ResultUnsafe(preliminaryStatus.Value);
        }

        string? localFingerprint = null;
        try
        {
            if (state is ActorState or FurnitureState or LightState &&
                localObjectIngress is not null &&
                !localObjectIngress.TryComputeFingerprint(state, out localFingerprint))
            {
                return Result(ReceiverActorIngressStatus.StateRejected);
            }
        }
        catch
        {
            return Result(ReceiverActorIngressStatus.StateRejected);
        }

        ReceiverActorIngressResult Commit()
        {
            var authorized = authority.TryExecute(
                receiverSession,
                () =>
                {
                    lock (gate)
                    {
                        var sequenceStatus = ValidateSequenceUnsafe(
                            receiverSession,
                            sequence);
                        if (sequenceStatus is not null)
                            return ResultUnsafe(sequenceStatus.Value);

                        if (localObjectIngress is null)
                        {
                            currentSequence = sequence;
                            return ResultUnsafe(
                                ReceiverActorIngressStatus.IgnoredUnsupportedKind);
                        }

                        var local = localFingerprint is null
                            ? localObjectIngress.AcceptState(receiverSession, state)
                            : localObjectIngress.AcceptState(
                                receiverSession,
                                state,
                                localFingerprint);
                        if (!local.Accepted)
                            return FromLocalUnsafe(local);

                        currentSequence = sequence;
                        return FromLocalUnsafe(local);
                    }
                },
                out var result);

            return authorized
                ? result
                : Result(ReceiverActorIngressStatus.InactiveSession);
        }

        return TryExecuteIfReady(Commit, out var admitted)
            ? admitted
            : Result(ReceiverActorIngressStatus.InactiveSession);
    }

    private bool TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (readinessAdmissionFence is not null)
            return readinessAdmissionFence.TryExecuteIfReady(operation, out result);

        if (readiness is not null)
        {
            try
            {
                if (!readiness.IsReady)
                {
                    result = default!;
                    return false;
                }
            }
            catch
            {
                result = default!;
                return false;
            }
        }

        result = operation();
        return true;
    }

    private ReceiverActorIngressStatus? ValidateSequenceUnsafe(
        ReceiverSessionKey receiverSession,
        ServerSequence sequence)
    {
        // Mutation callers hold the exact receiver authority boundary before
        // the event lock. Preparation may use this as a read-only preliminary
        // check after a separate point-in-time authority check; final mutation
        // always repeats it under authority. Snapshot initialization uses the
        // authority -> event ordering, avoiding a lock inversion on reconnect.
        if (!receiverSession.IsValid)
            return ReceiverActorIngressStatus.InactiveSession;
        if (session is null || session != receiverSession)
            return ReceiverActorIngressStatus.NotInitialized;
        if (sequence.Value <= currentSequence.Value)
            return ReceiverActorIngressStatus.StaleSequence;
        if (sequence.Value != currentSequence.Value + 1)
            return ReceiverActorIngressStatus.SequenceGap;

        return null;
    }

    private ReceiverActorIngressResult Result(ReceiverActorIngressStatus status)
    {
        lock (gate)
            return ResultUnsafe(status);
    }

    private ReceiverActorIngressResult ResultUnsafe(
        ReceiverActorIngressStatus status) =>
        new(status, currentSequence);

    private ReceiverActorIngressResult FromLocalUnsafe(
        ReceiverLocalObjectIngressResult local)
    {
        var status = local.Status switch
        {
            ReceiverLocalObjectIngressStatus.Accepted => ReceiverActorIngressStatus.Accepted,
            ReceiverLocalObjectIngressStatus.UnsupportedKind =>
                ReceiverActorIngressStatus.IgnoredUnsupportedKind,
            ReceiverLocalObjectIngressStatus.Invalid => ReceiverActorIngressStatus.Invalid,
            ReceiverLocalObjectIngressStatus.InactiveSession =>
                ReceiverActorIngressStatus.InactiveSession,
            ReceiverLocalObjectIngressStatus.AdmissionBackpressure =>
                ReceiverActorIngressStatus.AdmissionBackpressure,
            _ => ReceiverActorIngressStatus.StateRejected,
        };
        return ResultUnsafe(status) with { LocalWorkToken = local.WorkToken };
    }
}
