namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ReceiverSessionKey(
    long SessionId,
    long SessionGeneration,
    long ConnectionEpoch)
{
    public bool IsValid =>
        SessionId > 0 &&
        SessionGeneration > 0 &&
        ConnectionEpoch > 0;
}

/// <summary>
/// Owns the single receiver session that may admit or apply desired state.
/// Operations executed through <see cref="TryExecute{T}"/> cannot race a
/// successful invalidation of the same session.
/// </summary>
public sealed class ReceiverApplyAuthority
{
    private readonly object gate = new();
    private ReceiverSessionKey? current;
    private long lastActivatedConnectionEpoch;

    public ReceiverSessionKey? Current
    {
        get
        {
            lock (gate)
                return current;
        }
    }

    public bool TryActivate(ReceiverSessionKey session)
    {
        if (!session.IsValid)
            return false;

        lock (gate)
        {
            if (current is not null)
                return current.Value == session;

            // The connection epoch is the process-local token namespace for
            // every actor apply generation. Reusing a retired epoch could
            // recreate an old (session, actor, generation) token after state
            // cleanup and let a delayed worker complete newer coordinator
            // work. Activation therefore advances monotonically even when
            // callers use this lower-level authority without SyncConnectionLock.
            if (session.ConnectionEpoch <= lastActivatedConnectionEpoch)
                return false;

            current = session;
            lastActivatedConnectionEpoch = session.ConnectionEpoch;
            return true;
        }
    }

    public bool IsCurrent(ReceiverSessionKey session)
    {
        lock (gate)
            return current == session;
    }

    internal bool TryExecute<T>(
        ReceiverSessionKey session,
        Func<T> operation,
        out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (gate)
        {
            if (current != session)
            {
                result = default!;
                return false;
            }

            result = operation();
            return true;
        }
    }

    public bool Invalidate(ReceiverSessionKey session)
    {
        lock (gate)
        {
            if (current != session)
                return false;

            current = null;
            return true;
        }
    }
}
