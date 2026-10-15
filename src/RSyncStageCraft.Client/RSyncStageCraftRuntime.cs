using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Core;

/// <summary>
/// Signature-neutral application composition root. The concrete Dalamud,
/// local-object runtime and network adapters are supplied as ports.
/// Component-level receiver tests may omit a local-object port or ownership
/// diagnostics, but this full application root never accepts a partial or
/// ownership-unobservable receiver.
/// </summary>
public sealed class RSyncStageCraftRuntime
{
    private readonly object shutdownGate = new();
    private Task<SyncReceiverDisconnectResult>? shutdownTask;

    public RSyncStageCraftRuntime(
        IGameThreadDispatcher dispatcher,
        ILocalObjectRuntimePort localObjectRuntime,
        ReceiverActorRuntimeLimits? receiverLimits = null,
        LocalObjectApplyRetryPolicy? localObjectRetryPolicy = null,
        ClientAuthenticationLimits? authenticationLimits = null,
        TimeProvider? authenticationTimeProvider = null,
        ClientControlReceiveLimits? controlReceiveLimits = null,
        ClientControlSendLimits? controlSendLimits = null,
        ILocalObjectVisibility? localObjectVisibility = null,
        TimeSpan? localObjectLoadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(localObjectRuntime);

        RequireOwnershipDiagnostics<ILocalObjectRuntimeOwnershipDiagnostics>(
            localObjectRuntime,
            nameof(localObjectRuntime));

        Readiness = new ReceiverReadinessCoordinator();
        Connection = new SyncConnectionLock();
        Receiver = new ReceiverActorRuntime(
            dispatcher,
            Readiness,
            receiverLimits,
            localObjectRuntime: localObjectRuntime,
            localObjectRetryPolicy: localObjectRetryPolicy,
            localObjectApplyReadiness: Readiness.LocalObjects,
            localObjectVisibility: localObjectVisibility,
            localObjectLoadTimeout: localObjectLoadTimeout);
        ControlIngress = new ReceiverControlIngress(Connection, Receiver, Readiness);
        Lifecycle = new SyncReceiverLifecycle(
            Connection,
            ControlIngress,
            Readiness);
        ControlIngress.AttachLifecycle(Lifecycle);
        Authentication = new(
            Lifecycle,
            ControlIngress,
            Readiness,
            authenticationLimits ?? ClientAuthenticationLimits.Default,
            authenticationTimeProvider ?? TimeProvider.System,
            controlReceiveLimits,
            controlSendLimits);
        Lifecycle.OnSessionRevoked += Authentication.RetireSession;
    }

    private static void RequireOwnershipDiagnostics<TDiagnostics>(
        object port,
        string parameterName)
    {
        if (port is not TDiagnostics)
        {
            throw new ArgumentException(
                "Application-owned receiver ports must expose bounded ownership diagnostics.",
                parameterName);
        }
    }

    internal ReceiverReadinessCoordinator Readiness { get; }
    internal SyncConnectionLock Connection { get; }
    internal ReceiverActorRuntime Receiver { get; }
    public ReceiverControlIngress ControlIngress { get; }
    internal SyncReceiverLifecycle Lifecycle { get; }
    public ClientAuthenticationCoordinator Authentication { get; }
    public ReceiverDependencySnapshot DependencyState => Readiness.Current;
    public ReceiverActorRuntimeSnapshot ReceiverState => Receiver.State;
    public SyncReceiverLifecycleSnapshot LifecycleState => Lifecycle.Current;

    public Task<SyncReceiverConnectResult> ConnectAsync(
        long sessionId,
        long sessionGeneration,
        CancellationToken cancellationToken = default)
        => Lifecycle.ConnectAsync(
            sessionId,
            sessionGeneration,
            cancellationToken);

    public async Task<SyncReceiverDisconnectResult> DisconnectAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Lifecycle.DisconnectAsync(
                session,
                cancellationToken).ConfigureAwait(false);
            if (result.Status is not (
                SyncReceiverDisconnectStatus.Cancelled or
                SyncReceiverDisconnectStatus.CapacityExceeded))
            {
                Authentication.RetireSession(session);
            }

            return result;
        }
        catch
        {
            // A disconnect fault may occur after lease invalidation. Ensure
            // the matching transport cannot retain admission or buffered work.
            Authentication.RetireSession(session);
            throw;
        }
    }

    public Task<SyncReceiverDisconnectResult> RetryCleanupAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
        => Lifecycle.RetryCleanupAsync(session, cancellationToken);

    public Task<SyncReceiverDisconnectResult> ShutdownAsync()
    {
        Authentication.Terminate();
        // Close the shared short receiver admission transaction
        // before either serialized cleanup path can wait. An operation that
        // already won remains exact-session cleanup work; once this request
        // returns, delayed intake or pump work cannot publish new state.
        Readiness.RequestShutdown();

        TaskCompletionSource<SyncReceiverDisconnectResult> completion;
        lock (shutdownGate)
        {
            if (shutdownTask is not null)
                return shutdownTask;

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownTask = completion.Task;
        }

        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(
        TaskCompletionSource<SyncReceiverDisconnectResult> completion)
    {
        // Admission is already closed and the reference-identical task is
        // published. Yield before any adapter-backed diagnostics so even a slow
        // in-process counter cannot synchronously hold the initiating caller or
        // prevent concurrent callers from observing the shared flight.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var result = await ShutdownCoreAsync().ConfigureAwait(false);
            if (result.Status != SyncReceiverDisconnectStatus.Terminated)
                ClearRetryableShutdown(completion.Task);

            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            ClearRetryableShutdown(completion.Task);
            completion.TrySetException(exception);
        }
    }

    private async Task<SyncReceiverDisconnectResult> ShutdownCoreAsync()
    {
        var result = await Lifecycle.ShutdownAsync().ConfigureAwait(false);
        if (result.Status != SyncReceiverDisconnectStatus.Terminated)
            return result;

        // A terminated lifecycle is not a complete root drain while an exact
        // client transport still owns provider buffers, or any receiver adapter
        // still retains an external mutation. The latter check is deliberately
        // independent of a cleanup session so pre-activation orphans cannot be
        // hidden by an otherwise-idle lifecycle.
        if (Authentication.Count != 0 ||
            Receiver.HasResidualExternalOwnershipForShutdown())
            return new(SyncReceiverDisconnectStatus.CleanupRequired, result.ReceiverCleanup);

        return result;
    }

    private void ClearRetryableShutdown(Task<SyncReceiverDisconnectResult> completed)
    {
        lock (shutdownGate)
        {
            if (ReferenceEquals(shutdownTask, completed))
                shutdownTask = null;
        }
    }

    public async Task<SyncReceiverReadinessResult> UpdateReadinessAsync(
        ReceiverDependencySnapshot readiness,
        CancellationToken cancellationToken = default)
    {
        var previous = Readiness.Current;
        var observation = Readiness.UpdateForLifecycle(readiness);
        var reconcileLoss = observation.PendingLoss is not null;
        // A lost local-object host cleans only the local objects; the
        // connection stays active. Losing the connection ends the whole
        // session below, which cleans everything.
        if (!reconcileLoss)
        {
            if (previous.CanApplyLocalObjects && !readiness.CanApplyLocalObjects)
                Receiver.ResetLocalObjects();
        }
        if (reconcileLoss)
            Authentication.RetireForObservedReadinessLoss();
        // Once required health is known to be lost, caller cancellation and a
        // newer healthy observation must not leave the old connection/session
        // authoritative. Every later observation helps advance the retained
        // exact loss until ordinary lifecycle cleanup has fenced that epoch.
        var result = await Lifecycle.ReconcileReadinessAsync(
                isReady: !reconcileLoss && readiness.CanConnect,
                reconcileLoss ? CancellationToken.None : cancellationToken)
            .ConfigureAwait(false);
        if (observation.PendingLoss is { } loss &&
            result.Status is SyncReceiverReadinessStatus.Inactive or
                SyncReceiverReadinessStatus.Suspended or
                SyncReceiverReadinessStatus.CleanupRequired or
                SyncReceiverReadinessStatus.Terminated)
        {
            Readiness.CompleteLifecycleLoss(loss);
        }

        return result;
    }

    /// <summary>
    /// The player's location changed: release every furniture/light instance
    /// and respawn the ones that belong to the new location. Actors and all
    /// desired state are untouched. A zone change wipes local objects.
    /// </summary>
    public void ResetLocalObjects() => Receiver.ResetLocalObjects();

    /// <summary>Releases and re-admits only the local objects of one kind (used for actors).</summary>
    public void ResetLocalObjects(Protocol.RscObjectKind kind) => Receiver.ResetLocalObjects(kind);

    /// <summary>The actors, furniture and lights the server holds for this session, for display.</summary>
    public IReadOnlyList<Protocol.AuthoritativeObjectState> DesiredLocalObjects() => Receiver.GetDesiredLocalObjects();

    public Task<ReceiverActorPumpResult> PumpReceiverAsync(
        ReceiverSessionKey session,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!HasCommittedReceiverAuthority(session))
        {
            return Task.FromResult(new ReceiverActorPumpResult(
                ReceiverActorPumpStatus.InactiveSession,
                Snapshot: null));
        }
        // Connection readiness admits staged server state and runs the pump;
        // local objects then apply only while their own host is ready.
        if (!Readiness.IsReady)
        {
            return Task.FromResult(new ReceiverActorPumpResult(
                ReceiverActorPumpStatus.DependenciesUnavailable,
                Snapshot: null));
        }

        return Receiver.PumpAsync(session, now, cancellationToken);
    }

    private bool HasCommittedReceiverAuthority(ReceiverSessionKey session) =>
        Lifecycle.HasActiveAuthority(session) &&
        Receiver.Authority.IsCurrent(session);
}
