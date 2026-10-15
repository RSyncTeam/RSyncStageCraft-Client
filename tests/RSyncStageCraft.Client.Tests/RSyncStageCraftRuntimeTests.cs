using System.Security.Cryptography;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class RSyncStageCraftRuntimeTests
{
    [Fact]
    public void CompositionRootExposesSnapshotsInsteadOfRawReceiverAuthorities()
    {
        const System.Reflection.BindingFlags publicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public;

        Assert.Null(typeof(RSyncStageCraftRuntime).GetProperty(
            nameof(RSyncStageCraftRuntime.Readiness),
            publicInstance));
        Assert.Null(typeof(RSyncStageCraftRuntime).GetProperty(
            nameof(RSyncStageCraftRuntime.Receiver),
            publicInstance));
        Assert.Null(typeof(RSyncStageCraftRuntime).GetProperty(
            nameof(RSyncStageCraftRuntime.Connection),
            publicInstance));
        Assert.Null(typeof(RSyncStageCraftRuntime).GetProperty(
            nameof(RSyncStageCraftRuntime.Lifecycle),
            publicInstance));
        Assert.False(typeof(IReceiverActorSessionRuntime).IsPublic);
        Assert.Empty(typeof(ReceiverControlIngress).GetConstructors(publicInstance));
        Assert.Equal(
            typeof(ReceiverDependencySnapshot),
            typeof(RSyncStageCraftRuntime).GetProperty(
                nameof(RSyncStageCraftRuntime.DependencyState),
                publicInstance)!.PropertyType);
        Assert.Equal(
            typeof(ReceiverActorRuntimeSnapshot),
            typeof(RSyncStageCraftRuntime).GetProperty(
                nameof(RSyncStageCraftRuntime.ReceiverState),
                publicInstance)!.PropertyType);
        Assert.Equal(
            typeof(SyncReceiverLifecycleSnapshot),
            typeof(RSyncStageCraftRuntime).GetProperty(
                nameof(RSyncStageCraftRuntime.LifecycleState),
                publicInstance)!.PropertyType);
    }

    [Fact]
    public async Task ActivationUsesOneReadinessAuthority()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: false,
            LocalObjectHostReady: true));

        Assert.Equal(
            SyncReceiverConnectStatus.DependenciesUnavailable,
            (await fixture.Runtime.ConnectAsync(1, 1)).Status);

        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        var connected = await fixture.Runtime.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
        var session = connected.Session!.Value;
        Assert.Equal(fixture.Runtime.Connection.CurrentLease.Epoch, session.ConnectionEpoch);
        Assert.Equal(session, fixture.Runtime.Receiver.State.Session);
        Assert.True(fixture.Runtime.Readiness.IsReady);
    }

    [Fact]
    public async Task MissingReceiptSafeHostAdmitsConnectionButNeverApplies()
    {
        // Split readiness: signing in and holding server
        // state need only the protocol. Without the receipt-safe local-object
        // host the receiver pump applies nothing.
        using var fixture = new Fixture();
        var snapshot = new ReceiverDependencySnapshot(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: true,
            LocalObjectHostReady: false);

        var readiness = await fixture.Runtime.UpdateReadinessAsync(snapshot);
        var connected = await fixture.Runtime.ConnectAsync(1, 1);

        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.CanConnect);
        Assert.Equal(SyncReceiverReadinessStatus.Inactive, readiness.Status);
        Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
        var session = connected.Session!.Value;
        Assert.True(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(session, fixture.Runtime.Receiver.State.Session);
        Assert.True(fixture.Runtime.Readiness.IsReady);
        Assert.False(fixture.Runtime.Readiness.LocalObjects.IsReady);

        // Staged server state is still admitted, but the pump applies nothing.
        var pumped = await fixture.Runtime.PumpReceiverAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(ReceiverActorPumpStatus.DependenciesUnavailable, pumped.Status);
        Assert.Empty(pumped.LocalWork ?? []);

        // A later identical observation keeps the session: nothing was applied.
        var again = await fixture.Runtime.UpdateReadinessAsync(snapshot);
        Assert.Equal(SyncReceiverReadinessStatus.Ready, again.Status);
        Assert.True(fixture.Runtime.Connection.CurrentLease.IsValid);
    }

    [Fact]
    public async Task DependencyLossSuspendsReceiver()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        var oldSession = (await fixture.Runtime.ConnectAsync(1, 1)).Session!.Value;

        var suspended = await fixture.Runtime.UpdateReadinessAsync(
            new ReceiverDependencySnapshot(
                PenumbraReady: true,
                GlamourerReady: true,
                IpcReady: true,
                ProtocolCompatible: false,
                LocalObjectHostReady: true));

        Assert.Equal(SyncReceiverReadinessStatus.Suspended, suspended.Status);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Null(fixture.Runtime.Receiver.State.Session);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, fixture.Runtime.Lifecycle.Current.State);

        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        var reconnected = await fixture.Runtime.ConnectAsync(1, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(oldSession.ConnectionEpoch + 1, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task ReceiptSafeHostLossKeepsTheSessionAndResetsLocalObjects()
    {
        // Per-domain readiness: losing the local-object
        // host releases its receipts only. The connection, the lease and the
        // session stay active.
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(
            new(true, true, true, true, true));
        var session = (await fixture.Runtime.ConnectAsync(1, 1)).Session!.Value;
        var lease = fixture.Runtime.Connection.CurrentLease;

        var result = await fixture.Runtime.UpdateReadinessAsync(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: true,
            LocalObjectHostReady: false));

        Assert.Equal(SyncReceiverReadinessStatus.Ready, result.Status);
        Assert.True(fixture.Runtime.Connection.IsCurrent(lease));
        Assert.Equal(session, fixture.Runtime.Receiver.State.Session);
        Assert.Equal(SyncReceiverLifecycleState.Active, fixture.Runtime.Lifecycle.Current.State);
        Assert.False(fixture.Runtime.Readiness.LocalObjects.IsReady);
        Assert.Equal(
            ApplyDomainPhase.Cleaning,
            fixture.Runtime.Receiver.LocalObjectDomain.Phase);
    }

    [Fact]
    public async Task ConnectionLossStillRevokesTheWholeSession()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        var session = (await fixture.Runtime.ConnectAsync(1, 1)).Session!.Value;
        var lease = fixture.Runtime.Connection.CurrentLease;

        var result = await fixture.Runtime.UpdateReadinessAsync(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: false,
            LocalObjectHostReady: true));

        Assert.Equal(SyncReceiverReadinessStatus.Suspended, result.Status);
        Assert.False(fixture.Runtime.Connection.IsCurrent(lease));
        Assert.Null(fixture.Runtime.Receiver.State.Session);
        Assert.Equal(session, result.Session);
    }

    [Fact]
    public async Task CallerCancellationCannotSuppressRequiredDependencyLossCleanup()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        await fixture.Runtime.ConnectAsync(1, 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await fixture.Runtime.UpdateReadinessAsync(
            new ReceiverDependencySnapshot(
                PenumbraReady: true,
                GlamourerReady: true,
                IpcReady: true,
                ProtocolCompatible: false,
                LocalObjectHostReady: true),
            cancelled.Token);

        Assert.Equal(SyncReceiverReadinessStatus.Suspended, result.Status);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, fixture.Runtime.Lifecycle.Current.State);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Null(fixture.Runtime.Receiver.State.Session);
    }

    [Fact]
    public async Task QueuedRecoveryCannotReopenReadinessBeforeLossRetiresOldEpoch()
    {
        using var fixture = new Fixture();
        var ready = new ReceiverDependencySnapshot(true, true, true, true, true);
        await fixture.Runtime.UpdateReadinessAsync(ready);
        var oldSession = (await fixture.Runtime.ConnectAsync(1, 1)).Session!.Value;
        using var releaseCleanup = new ManualResetEventSlim(false);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.Receiver.BeforeSessionCancellationStart = () =>
        {
            cleanupEntered.TrySetResult();
            releaseCleanup.Wait();
        };

        var lost = Task.Run(() => fixture.Runtime.UpdateReadinessAsync(
            new ReceiverDependencySnapshot(true, true, true, false, true)));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = Task.Run(() => fixture.Runtime.UpdateReadinessAsync(ready));
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => fixture.Runtime.DependencyState.IsReady,
                TimeSpan.FromSeconds(5)));
            Assert.False(fixture.Runtime.Readiness.IsReady);
        }
        finally
        {
            releaseCleanup.Set();
        }

        Assert.Equal(
            SyncReceiverReadinessStatus.Suspended,
            (await lost.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            SyncReceiverReadinessStatus.Suspended,
            (await recovered.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.True(fixture.Runtime.Readiness.IsReady);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, fixture.Runtime.Lifecycle.Current.State);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Null(fixture.Runtime.Receiver.State.Session);

        var reconnected = await fixture.Runtime.ConnectAsync(1, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(oldSession.ConnectionEpoch + 1, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task TopLevelShutdownIsTerminalAfterSharedRuntimeCleanup()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await fixture.Runtime.ConnectAsync(1, 1)).Status);

        var shutdown = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, shutdown.Status);
        Assert.Equal(SyncReceiverLifecycleState.Terminated, fixture.Runtime.Lifecycle.Current.State);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Null(fixture.Runtime.Receiver.State.Session);
        Assert.Equal(
            SyncReceiverConnectStatus.Terminated,
            (await fixture.Runtime.ConnectAsync(1, 2)).Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await fixture.Runtime.ShutdownAsync()).Status);
    }

    [Fact]
    public async Task TopLevelShutdownWaitsForResidualAdapterOwnership()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await fixture.Runtime.ConnectAsync(1, 1)).Status);
        fixture.Ports.PendingOwnerships = 1;

        var pending = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, pending.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            fixture.Runtime.LifecycleState.State);
        Assert.Equal(1, fixture.Runtime.ReceiverState.PendingLocalObjectOwnerships);

        fixture.Ports.PendingOwnerships = 0;

        var terminated = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, terminated.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            fixture.Runtime.LifecycleState.State);
    }

    [Fact]
    public async Task TopLevelShutdownWaitsForPreexistingExternalOwnershipWithoutSession()
    {
        using var fixture = new Fixture();
        fixture.Ports.PendingOwnerships = 1;

        var pending = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, pending.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            fixture.Runtime.LifecycleState.State);
        Assert.Null(fixture.Runtime.ReceiverState.Session);
        Assert.Equal(1, fixture.Runtime.ReceiverState.PendingLocalObjectOwnerships);
        Assert.Equal(
            SyncReceiverDisconnectStatus.CleanupRequired,
            (await fixture.Runtime.ShutdownAsync()).Status);

        fixture.Ports.PendingOwnerships = 0;

        var terminated = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, terminated.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            fixture.Runtime.LifecycleState.State);
    }

    [Fact]
    public async Task ConcurrentTopLevelShutdownSharesFinalOwnershipDrain()
    {
        using var fixture = new Fixture();
        fixture.Ports.BlockOwnershipCount = true;
        var first = fixture.Runtime.ShutdownAsync();
        await fixture.Ports.OwnershipCountEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        Task<SyncReceiverDisconnectResult> second;
        try
        {
            second = fixture.Runtime.ShutdownAsync();

            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            fixture.Ports.BlockOwnershipCount = false;
            fixture.Ports.ReleaseOwnershipCount.TrySetResult();
        }

        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(
            results,
            result => Assert.Equal(
                SyncReceiverDisconnectStatus.Terminated,
                result.Status));
        Assert.Same(first, fixture.Runtime.ShutdownAsync());
    }

    [Fact]
    public void TopLevelShutdownContinuationDoesNotRequireCallingSynchronizationContext()
    {
        using var fixture = new Fixture();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new NonPumpingSynchronizationContext());
        try
        {
            var shutdown = fixture.Runtime.ShutdownAsync();

#pragma warning disable xUnit1031 // This regression intentionally models synchronous host unload.
            Assert.True(shutdown.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                SyncReceiverDisconnectStatus.Terminated,
                shutdown.GetAwaiter().GetResult().Status);
#pragma warning restore xUnit1031
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task ShutdownClosesReceiverAdmissionBeforeLifecycleSerializerIsAvailable()
    {
        using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        await runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        using var releaseActivation = new ManualResetEventSlim(false);
        var activationEntered = new TaskCompletionSource<ReceiverSessionKey>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ControlIngress.BeforeLifecycleActivationCommit = session =>
        {
            activationEntered.TrySetResult(session);
            releaseActivation.Wait();
        };

        var connect = Task.Run(() => runtime.ConnectAsync(1, 1));
        var session = await activationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = Task.Run(runtime.ShutdownAsync);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => !runtime.Readiness.IsReady,
                TimeSpan.FromSeconds(5)));
            Assert.True(runtime.Receiver.Authority.IsCurrent(session));

            // Receiver admission is already closed, although the lifecycle
            // serializer is still held and the session is still current.
            var admitted = ((IReceiverReadinessAdmissionFence)runtime.Readiness)
                .TryExecuteIfReady(() => true, out _);

            Assert.False(admitted);
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            releaseActivation.Set();
        }

        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await connect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task BlockedLeaseCancellationKeepsShutdownRetryable()
    {
        using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        await runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        await runtime.ConnectAsync(1, 1);
        Assert.True(runtime.Connection.TryGetCancellationToken(
            runtime.Connection.CurrentLease, out var token));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        var shutdown = Task.Run(runtime.ShutdownAsync);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                SyncReceiverDisconnectStatus.CleanupRequired,
                (await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(SpinWait.SpinUntil(
            () => runtime.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await runtime.ShutdownAsync()).Status);
    }

    [Fact]
    public void CompositionRootRequiresAndWiresSignatureNeutralLocalObjectRuntime()
    {
        var constructor = Assert.Single(typeof(RSyncStageCraftRuntime).GetConstructors());
        var parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => candidate.ParameterType == typeof(ILocalObjectRuntimePort));
        Assert.False(parameter.IsOptional);
        Assert.False(parameter.HasDefaultValue);

        var ports = new StubPorts();
        var runtime = new RSyncStageCraftRuntime(ports, ports);

        Assert.NotNull(runtime.Receiver.LocalObjectStore);
        Assert.NotNull(runtime.Receiver.LocalObjectCoordinator);
        Assert.NotNull(runtime.Receiver.LocalObjectRetries);
        Assert.NotNull(runtime.Receiver.LocalObjectLedger);
    }

    [Fact]
    public void CompositionRootRejectsMissingLocalObjectRuntime()
    {
        var ports = new StubPorts();

        Assert.Throws<ArgumentNullException>(() => new RSyncStageCraftRuntime(
            ports,
            localObjectRuntime: null!));
    }

    [Fact]
    public void CompositionRootRejectsPortsWithoutOwnershipDiagnostics()
    {
        var ports = new StubPorts();
        var missing = new MissingOwnershipDiagnosticsPort(ports);

        var error = Assert.Throws<ArgumentException>(() =>
            new RSyncStageCraftRuntime(ports, missing));
        Assert.Equal("localObjectRuntime", error.ParamName);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Ports = new StubPorts();
            Runtime = new RSyncStageCraftRuntime(Ports, Ports);
        }

        public RSyncStageCraftRuntime Runtime { get; }
        public StubPorts Ports { get; }

        public void Dispose()
        {
        }
    }

    internal sealed class StubPorts :
        IGameThreadDispatcher,
        ILocalObjectRuntimePort,
        ILocalObjectRuntimeOwnershipDiagnostics
    {
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public bool BlockOwnershipCount { get; set; }
        public int PendingOwnerships { get; set; }

        int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount
        {
            get
            {
                if (BlockOwnershipCount)
                {
                    OwnershipCountEntered.TrySetResult();
                    ReleaseOwnershipCount.Task.GetAwaiter().GetResult();
                }

                return PendingOwnerships;
            }
        }

        public TaskCompletionSource OwnershipCountEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOwnershipCount { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken) => Task.FromResult(operation());

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) => new(
                ApplyPortStatus.Succeeded,
                new LocalObjectInstanceHandle(
                    Guid.NewGuid(),
                    request.Token.ObjectId,
                    request.Identity.Kind,
                    request.SpawnGeneration));

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) => new(ApplyPortStatus.Succeeded);

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle) =>
            new(ReleaseStatus);
    }

    private sealed class MissingOwnershipDiagnosticsPort(StubPorts inner) :
        ILocalObjectRuntimePort
    {
        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) => inner.SpawnOwned(request);

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) => inner.ApplyOwned(handle, desiredState);

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle) =>
            inner.ReleaseOwned(handle);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object state)
        {
            // Deliberately do not dispatch. Terminal cleanup must not capture
            // the initiating host thread after publishing its shared task.
        }
    }
}
