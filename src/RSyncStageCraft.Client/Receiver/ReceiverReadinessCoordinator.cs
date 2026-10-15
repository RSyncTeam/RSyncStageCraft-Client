using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ReceiverDependencySnapshot(
    bool PenumbraReady,
    bool GlamourerReady,
    bool IpcReady,
    bool ProtocolCompatible,
    bool LocalObjectHostReady = false)
{
    /// <summary>
    /// Connection-level readiness: everything needed to sign in and hold
    /// server state. The local-object host is not required here, because
    /// nothing is rendered without it (see <see cref="IsReady"/>). Penumbra,
    /// Glamourer and their IPC are not required either: a base-game-only setup
    /// can connect. Their flags are informational.
    /// </summary>
    public bool CanConnect => ProtocolCompatible;

    /// <summary>Actors, furniture and lights may be applied: connection readiness plus the local-object host.</summary>
    public bool CanApplyLocalObjects => CanConnect && LocalObjectHostReady;

    /// <summary>Connection readiness plus the local-object host.</summary>
    public bool IsReady => CanApplyLocalObjects;
}

internal sealed class ReceiverReadinessLossObservation;

internal readonly record struct ReceiverReadinessLifecycleUpdate(
    bool Changed,
    ReceiverReadinessLossObservation? PendingLoss);

/// <summary>
/// One shared readiness authority for connection admission and receiver work.
/// Its own <see cref="IsReady"/> and admission fence are connection-level
/// (<see cref="ReceiverDependencySnapshot.CanConnect"/>): sign-in, the session
/// lifecycle and incoming server state use it, and only its loss revokes the
/// whole session. <see cref="LocalObjects"/> adds the local-object host (actors,
/// furniture and lights); work that touches the game uses that view. Losing the
/// host cleans only the local-object receipts (the receiver runtime does
/// that); the connection stays active. Host readiness means that the concrete
/// adapters satisfy the exact receipt/recovery contract; installed or
/// reachable providers alone are insufficient. Customize+ is intentionally
/// absent because it is optional.
/// </summary>
public sealed class ReceiverReadinessCoordinator :
    IReceiverReadinessPort,
    IReceiverReadinessAdmissionFence
{
    private readonly object gate = new();
    private ReceiverDependencySnapshot current;
    private ReceiverReadinessLossObservation? pendingLoss;
    private bool shutdownRequested;

    public ReceiverReadinessCoordinator()
    {
        LocalObjects = new ApplyReadinessView(this, static snapshot => snapshot.CanApplyLocalObjects);
    }

    /// <summary>Local-object apply domain, sharing this authority's lock: connection readiness plus the local-object host.</summary>
    public IReceiverReadinessPort LocalObjects { get; }

    public ReceiverDependencySnapshot Current
    {
        get
        {
            lock (gate)
                return current;
        }
    }

    public bool IsReady
    {
        get
        {
            lock (gate)
                return IsReadyUnsafe();
        }
    }

    public bool Update(ReceiverDependencySnapshot next)
    {
        lock (gate)
        {
            if (current == next)
                return false;

            current = next;
            return true;
        }
    }

    internal ReceiverReadinessLifecycleUpdate UpdateForLifecycle(
        ReceiverDependencySnapshot next)
    {
        lock (gate)
        {
            var changed = current != next;
            current = next;
            // Only losing connection readiness ends the session. A lost host
            // is handled per apply domain by the receiver runtime.
            if (!next.CanConnect)
                pendingLoss = new ReceiverReadinessLossObservation();

            return new(changed, pendingLoss);
        }
    }

    internal bool CompleteLifecycleLoss(
        ReceiverReadinessLossObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (gate)
        {
            if (!ReferenceEquals(pendingLoss, observation))
                return false;

            pendingLoss = null;
            return true;
        }
    }

    /// <summary>
    /// Permanently closes the shared receiver/transfer admission capability.
    /// The latest dependency snapshot remains diagnostic, but no later health
    /// update can reopen this process-owned runtime after terminal shutdown.
    /// </summary>
    internal void RequestShutdown()
    {
        lock (gate)
            shutdownRequested = true;
    }

    bool IReceiverReadinessAdmissionFence.TryExecuteIfReady<T>(
        Func<T> operation,
        out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (gate)
        {
            if (!IsReadyUnsafe())
            {
                result = default!;
                return false;
            }

            result = operation();
            return true;
        }
    }

    private bool IsReadyUnsafe() =>
        !shutdownRequested && current.CanConnect && pendingLoss is null;

    private sealed class ApplyReadinessView(
        ReceiverReadinessCoordinator owner,
        Func<ReceiverDependencySnapshot, bool> domainReady) :
        IReceiverReadinessPort,
        IReceiverReadinessAdmissionFence
    {
        private bool IsApplyReadyUnsafe() => owner.IsReadyUnsafe() && domainReady(owner.current);

        public bool IsReady
        {
            get
            {
                lock (owner.gate)
                    return IsApplyReadyUnsafe();
            }
        }

        bool IReceiverReadinessAdmissionFence.TryExecuteIfReady<T>(
            Func<T> operation,
            out T result)
        {
            ArgumentNullException.ThrowIfNull(operation);

            lock (owner.gate)
            {
                if (!IsApplyReadyUnsafe())
                {
                    result = default!;
                    return false;
                }

                result = operation();
                return true;
            }
        }
    }
}
