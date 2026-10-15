using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class SyncReceiverLifecycleTests
{
    [Fact]
    public void LifecycleOperationAdmissionLimitIsStrictlyBounded()
    {
        var environment = new LifecycleEnvironment();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SyncReceiverLifecycle(
                environment.Connection,
                environment.Receiver,
                environment,
                maximumPendingOperations: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SyncReceiverLifecycle(
                environment.Connection,
                environment.Receiver,
                environment,
                maximumPendingOperations:
                    ClientLimits.MaximumReceiverLifecycleOperations + 1));
    }

    [Fact]
    public async Task ActivationRequiresValidIdentityReadinessAndExclusiveLeaseOwnership()
    {
        var environment = new LifecycleEnvironment { IsReady = false };

        Assert.Equal(
            SyncReceiverConnectStatus.InvalidSession,
            (await environment.Lifecycle.ConnectAsync(0, 1)).Status);
        Assert.Equal(
            SyncReceiverConnectStatus.DependenciesUnavailable,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(0, environment.Receiver.ActivationCalls);

        environment.IsReady = true;
        var activated = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, activated.Status);
        var session = Assert.IsType<ReceiverSessionKey>(activated.Session);
        Assert.Equal(1, session.ConnectionEpoch);
        Assert.Equal(session, environment.Lifecycle.Current.ActiveSession);
        Assert.Equal(
            SyncReceiverConnectStatus.AlreadyActive,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);
        Assert.Equal(
            SyncReceiverConnectStatus.ActiveSessionConflict,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
        Assert.Equal(1, environment.Receiver.ActivationCalls);
    }

    [Fact]
    public async Task ThrowingReadinessProbeRejectsConnectionWithoutPublishingAuthority()
    {
        var environment = new LifecycleEnvironment { ThrowReadiness = true };

        var rejected = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.DependenciesUnavailable, rejected.Status);
        Assert.Null(rejected.Session);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(0, environment.Receiver.ActivationCalls);

        environment.ThrowReadiness = false;
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);
    }

    [Fact]
    public async Task PreexistingLiveLeaseIsConnectionConflictNotLifecycleAuthority()
    {
        var environment = new LifecycleEnvironment();
        var existing = environment.Connection.Acquire();

        var rejected = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.ConnectionInUse, rejected.Status);
        Assert.Null(rejected.Session);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.Equal(0, environment.Receiver.ActivationCalls);
        Assert.True(environment.Connection.IsCurrent(existing));
    }

    [Fact]
    public async Task ActivationDoesNotPublishTheLeaseBeforeCommit()
    {
        var environment = new LifecycleEnvironment();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        environment.Receiver.BeforeActivation = _ =>
        {
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };

        var connectTask = Task.Run(() => environment.Lifecycle.ConnectAsync(1, 1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            var pending = environment.Lifecycle.Current;
            Assert.Equal(SyncReceiverLifecycleState.Disconnected, pending.State);
            Assert.False(pending.Lease.IsValid);
        }
        finally
        {
            resume.Set();
        }

        var connected = await connectTask;
        var committed = environment.Lifecycle.Current;
        Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
        Assert.Equal(SyncReceiverLifecycleState.Active, committed.State);
        Assert.True(committed.Lease.IsValid);
        Assert.True(environment.Connection.IsCurrent(committed.Lease));
    }

    [Fact]
    public async Task RejectedReceiverActivationRollsBackAcquiredEpochBeforeRetry()
    {
        var environment = new LifecycleEnvironment();
        environment.Receiver.RejectActivation = true;

        var rejected = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.ReceiverRejected, rejected.Status);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(0, environment.Receiver.DisconnectCalls);

        environment.Receiver.RejectActivation = false;
        var retried = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, retried.Status);
        Assert.Equal(2, retried.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task FaultedReceiverActivationDrainsPartiallyOwnedStateBeforeRetry()
    {
        var environment = new LifecycleEnvironment();
        var activationFailure = new InvalidOperationException(
            "Injected partial activation failure.");
        environment.Receiver.OwnSessionBeforeActivationFailure = true;
        environment.Receiver.ActivationException = activationFailure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Lifecycle.ConnectAsync(1, 1));

        Assert.Same(activationFailure, thrown);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.True(environment.Receiver.LeaseWasInvalidAtDisconnect);
        Assert.Null(environment.Receiver.ActiveSession);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(
            SyncReceiverLifecycleState.Disconnected,
            environment.Lifecycle.Current.State);

        environment.Receiver.ActivationException = null;
        var retried = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, retried.Status);
        Assert.Equal(2, retried.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task FaultedActivationRetainsIncompleteReceiverCleanupFence()
    {
        var environment = new LifecycleEnvironment();
        environment.Receiver.OwnSessionBeforeActivationFailure = true;
        environment.Receiver.ActivationException = new InvalidOperationException(
            "Injected partial activation failure.");
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            1,
            0,
            HasRemainingOwnership: true,
            Cancelled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Lifecycle.ConnectAsync(1, 1));

        var cleanupSession = Assert.IsType<ReceiverSessionKey>(
            environment.Lifecycle.Current.CleanupSession);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            environment.Lifecycle.Current.State);
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);

        var cleaned = await environment.Lifecycle.RetryCleanupAsync(cleanupSession);

        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected, cleaned.Status);
        Assert.Equal(1, environment.Receiver.RetryCalls);
        Assert.Equal(
            SyncReceiverLifecycleState.Disconnected,
            environment.Lifecycle.Current.State);

        environment.Receiver.ActivationException = null;
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
    }

    [Fact]
    public async Task RejectedActivationRetainsItsCancellationDrainFenceUntilRetry()
    {
        var environment = new LifecycleEnvironment();
        environment.Receiver.RejectActivation = true;
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        environment.Receiver.BeforeActivation = session =>
        {
            var lease = environment.Connection.CurrentLease;
            Assert.Equal(session.ConnectionEpoch, lease.Epoch);
            Assert.True(environment.Connection.TryGetCancellationToken(
                lease, out var token));
            registration = token.Register(() =>
            {
                entered.TrySetResult();
                release.Wait();
            });
        };

        var activation = Task.Run(() => environment.Lifecycle.ConnectAsync(1, 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                SyncReceiverConnectStatus.ReceiverRejected,
                (await activation.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(
                SyncReceiverLifecycleState.CleanupRequired,
                environment.Lifecycle.Current.State);
            Assert.Equal(
                SyncReceiverConnectStatus.CleanupRequired,
                (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
        }
        finally
        {
            release.Set();
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }

        Assert.True(SpinWait.SpinUntil(
            () => environment.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        var cleanupSession = environment.Lifecycle.Current.CleanupSession!.Value;
        Assert.Equal(
            SyncReceiverDisconnectStatus.Disconnected,
            (await environment.Lifecycle.RetryCleanupAsync(cleanupSession)).Status);
        environment.Receiver.BeforeActivation = null;
        environment.Receiver.RejectActivation = false;
        var reconnected = await environment.Lifecycle.ConnectAsync(2, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(2, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task ExternallyRetiringConnectionReportsCleanupInsteadOfEpochExhaustion()
    {
        var environment = new LifecycleEnvironment();
        var lease = environment.Connection.Acquire();
        Assert.True(environment.Connection.TryGetCancellationToken(lease, out var token));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        var invalidation = Task.Run(() => environment.Connection.Invalidate(lease));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await invalidation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                SyncReceiverConnectStatus.CleanupRequired,
                (await environment.Lifecycle.ConnectAsync(1, 1)).Status);
            Assert.Equal(0, environment.Receiver.ActivationCalls);
        }
        finally
        {
            release.Set();
            await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(SpinWait.SpinUntil(
            () => environment.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        var reconnected = await environment.Lifecycle.ConnectAsync(1, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(lease.Epoch + 1, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task InactiveLifecycleCannotClaimShutdownWhileUnownedCallbacksRemain()
    {
        var environment = new LifecycleEnvironment();
        var lease = environment.Connection.Acquire();
        Assert.True(environment.Connection.TryGetCancellationToken(lease, out var token));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        var invalidation = Task.Run(() => environment.Connection.Invalidate(lease));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await invalidation.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                environment.Lifecycle.ShutdownAsync());
            Assert.NotEqual(
                SyncReceiverLifecycleState.Terminated,
                environment.Lifecycle.Current.State);
        }
        finally
        {
            release.Set();
            await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(SpinWait.SpinUntil(
            () => environment.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await environment.Lifecycle.ShutdownAsync()).Status);
    }

    [Fact]
    public async Task ExhaustedConnectionEpochFailsClosedBeforeReceiverActivation()
    {
        var environment = new LifecycleEnvironment(
            connection: new SyncConnectionLock(long.MaxValue));

        var result = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.ConnectionEpochExhausted, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, environment.Receiver.ActivationCalls);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
    }

    [Fact]
    public async Task DisconnectRevokesLeaseAndDrainsAllBoundedCleanupBeforeReconnect()
    {
        var environment = new LifecycleEnvironment();
        var connected = await environment.Lifecycle.ConnectAsync(1, 1);
        var session = connected.Session!.Value;

        environment.Receiver.BlockDisconnect = true;
        var disconnect = environment.Lifecycle.DisconnectAsync(session);
        await environment.Receiver.DisconnectEntered.Task;

        Assert.True(environment.Receiver.LeaseWasInvalidAtDisconnect);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(SyncReceiverLifecycleState.Cleaning, environment.Lifecycle.Current.State);

        var reconnect = environment.Lifecycle.ConnectAsync(2, 1);
        Assert.False(reconnect.IsCompleted);

        environment.Receiver.ReleaseDisconnect.TrySetResult();
        Assert.Equal(
            SyncReceiverDisconnectStatus.Disconnected,
            (await disconnect).Status);
        var reconnected = await reconnect;

        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(2, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionCallbackDrainCannotHoldCleanupOrReleaseReconnectFence(
        bool shutdown)
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        var lease = environment.Connection.CurrentLease;
        Assert.True(environment.Connection.TryGetCancellationToken(lease, out var token));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });

        var disconnect = Task.Run(() => shutdown
            ? environment.Lifecycle.ShutdownAsync()
            : environment.Lifecycle.DisconnectAsync(session));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                SyncReceiverDisconnectStatus.CleanupRequired,
                (await disconnect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.False(environment.Connection.CurrentLease.IsValid);
            Assert.True(environment.Receiver.LeaseWasInvalidAtDisconnect);
            Assert.Null(environment.Receiver.ActiveSession);
            Assert.Equal(
                SyncReceiverLifecycleState.CleanupRequired,
                environment.Lifecycle.Current.State);
            Assert.Equal(
                SyncReceiverConnectStatus.CleanupRequired,
                (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
            var retryWhileBlocked = shutdown
                ? environment.Lifecycle.ShutdownAsync()
                : environment.Lifecycle.RetryCleanupAsync(session);
            Assert.Equal(
                SyncReceiverDisconnectStatus.CleanupRequired,
                (await retryWhileBlocked.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            release.Set();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(SpinWait.SpinUntil(
            () => environment.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        var completed = await (shutdown
            ? environment.Lifecycle.ShutdownAsync()
            : environment.Lifecycle.RetryCleanupAsync(session));
        Assert.Equal(
            shutdown
                ? SyncReceiverDisconnectStatus.Terminated
                : SyncReceiverDisconnectStatus.Disconnected,
            completed.Status);
        var reconnected = await environment.Lifecycle.ConnectAsync(2, 1);
        Assert.Equal(
            shutdown
                ? SyncReceiverConnectStatus.Terminated
                : SyncReceiverConnectStatus.Activated,
            reconnected.Status);
        if (!shutdown)
            Assert.Equal(lease.Epoch + 1, reconnected.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task LifecycleAdmissionRejectsExcessBeforeSerializerQueueGrowth()
    {
        var environment = new LifecycleEnvironment(maximumLifecycleOperations: 2);
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.BlockDisconnect = true;
        var disconnect = environment.Lifecycle.DisconnectAsync(session);
        await environment.Receiver.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedConnect = environment.Lifecycle.ConnectAsync(2, 1);

        SyncReceiverConnectResult excessConnect;
        SyncReceiverDisconnectResult excessDisconnect;
        SyncReceiverDisconnectResult excessRetry;
        SyncReceiverReadinessResult excessReadiness;
        try
        {
            Assert.Equal(2, environment.Lifecycle.MaximumPendingOperations);
            Assert.Equal(2, environment.Lifecycle.PendingOperationCount);
            Assert.False(queuedConnect.IsCompleted);
            excessConnect = await environment.Lifecycle.ConnectAsync(3, 1)
                .WaitAsync(TimeSpan.FromSeconds(5));
            excessDisconnect = await environment.Lifecycle.DisconnectAsync(session)
                .WaitAsync(TimeSpan.FromSeconds(5));
            excessRetry = await environment.Lifecycle.RetryCleanupAsync(session)
                .WaitAsync(TimeSpan.FromSeconds(5));
            excessReadiness = await environment.Lifecycle.ReconcileReadinessAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            environment.Receiver.ReleaseDisconnect.TrySetResult();
        }

        Assert.Equal(SyncReceiverConnectStatus.CapacityExceeded, excessConnect.Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.CapacityExceeded,
            excessDisconnect.Status);
        Assert.Equal(SyncReceiverDisconnectStatus.CapacityExceeded, excessRetry.Status);
        Assert.Equal(
            SyncReceiverReadinessStatus.CapacityExceeded,
            excessReadiness.Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Disconnected,
            (await disconnect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await queuedConnect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, environment.Lifecycle.PendingOperationCount);
    }

    [Fact]
    public async Task ShutdownIsSingleFlightAndClosesQueuedOperationAdmission()
    {
        var environment = new LifecycleEnvironment(maximumLifecycleOperations: 2);
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.BlockDisconnect = true;
        var disconnect = environment.Lifecycle.DisconnectAsync(session);
        await environment.Receiver.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstShutdown = environment.Lifecycle.ShutdownAsync();
        var secondShutdown = environment.Lifecycle.ShutdownAsync();
        try
        {
            Assert.Same(firstShutdown, secondShutdown);
            Assert.True(environment.Lifecycle.ShutdownRequested);
            Assert.False(firstShutdown.IsCompleted);
            Assert.Equal(
                SyncReceiverConnectStatus.Terminated,
                (await environment.Lifecycle.ConnectAsync(2, 1)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(
                SyncReceiverDisconnectStatus.Terminated,
                (await environment.Lifecycle.DisconnectAsync(session)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(
                SyncReceiverDisconnectStatus.Terminated,
                (await environment.Lifecycle.RetryCleanupAsync(session)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(
                SyncReceiverReadinessStatus.Terminated,
                (await environment.Lifecycle.ReconcileReadinessAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            environment.Receiver.ReleaseDisconnect.TrySetResult();
        }

        Assert.Equal(
            SyncReceiverDisconnectStatus.Disconnected,
            (await disconnect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await firstShutdown.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Same(firstShutdown, environment.Lifecycle.ShutdownAsync());
        Assert.Equal(SyncReceiverLifecycleState.Terminated, environment.Lifecycle.Current.State);
        Assert.Equal(0, environment.Lifecycle.PendingOperationCount);
    }

    [Fact]
    public async Task ShutdownRequestRevokesPublishedLeaseBeforeSerializerIsAvailable()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        using var releaseAuthoritySample = new ManualResetEventSlim(false);
        var authoritySampleEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Receiver.BeforeIsActive = () =>
        {
            authoritySampleEntered.TrySetResult();
            releaseAuthoritySample.Wait();
        };
        var serializerOwner = Task.Run(() =>
            environment.Lifecycle.ReconcileReadinessAsync(isReady: true));
        await authoritySampleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = environment.Lifecycle.ShutdownAsync();
        try
        {
            Assert.True(environment.Lifecycle.ShutdownRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.False(environment.Lifecycle.Current.Lease.IsValid);
            Assert.False(environment.Lifecycle.HasActiveAuthority(session));
        }
        finally
        {
            releaseAuthoritySample.Set();
        }

        await serializerOwner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(SyncReceiverLifecycleState.Terminated, environment.Lifecycle.Current.State);
    }

    [Fact]
    public async Task IncompleteReceiverOwnershipCleanupBlocksReconnectUntilExplicitRetry()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: true,
            Cancelled: false);

        var disconnected = await environment.Lifecycle.DisconnectAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, disconnected.Status);
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);

        environment.Receiver.RetryResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 1,
            OwnershipFailed: 0,
            HasRemainingOwnership: false,
            Cancelled: false);
        var retried = await environment.Lifecycle.RetryCleanupAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected, retried.Status);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        var reconnected = await environment.Lifecycle.ConnectAsync(2, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        Assert.Equal(2, reconnected.Session!.Value.ConnectionEpoch);
        Assert.Equal(1, environment.Receiver.RetryCalls);
    }

    [Fact]
    public async Task FailedReceiverCleanupCannotOmitRemainingOwnershipAndPermitReconnect()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: false,
            Cancelled: false);

        var disconnected = await environment.Lifecycle.DisconnectAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, disconnected.Status);
        Assert.True(disconnected.ReceiverCleanup.NeedsRetry);
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, environment.Lifecycle.Current.State);
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);

        var retried = await environment.Lifecycle.RetryCleanupAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected, retried.Status);
        Assert.Equal(1, environment.Receiver.RetryCalls);
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
    }

    [Fact]
    public async Task LateDisconnectForOldSessionCannotInvalidateReconnectedEpoch()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        Assert.Equal(
            SyncReceiverDisconnectStatus.Disconnected,
            (await environment.Lifecycle.DisconnectAsync(oldSession)).Status);
        var newSession = (await environment.Lifecycle.ConnectAsync(2, 1)).Session!.Value;

        var stale = await environment.Lifecycle.DisconnectAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.InactiveSession, stale.Status);
        var currentLease = environment.Connection.CurrentLease;
        Assert.Equal(newSession.ConnectionEpoch, currentLease.Epoch);
        Assert.True(environment.Connection.IsCurrent(currentLease));
        Assert.False(environment.Connection.IsCurrent(
            new SyncConnectionLease(newSession.ConnectionEpoch)));
        Assert.Equal(newSession, environment.Lifecycle.Current.ActiveSession);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
    }

    [Fact]
    public async Task MismatchedLiveLeaseReceiverFaultCannotLeaveOldSessionActive()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        var oldLease = environment.Connection.CurrentLease;
        Assert.True(environment.Connection.Invalidate(oldLease));
        var unrelatedLease = environment.Connection.Acquire();
        var projected = environment.Lifecycle.Current;
        Assert.Equal(SyncReceiverLifecycleState.Active, projected.State);
        Assert.False(projected.Lease.IsValid);
        environment.Receiver.DisconnectException = new InvalidOperationException(
            "Injected receiver cleanup failure.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Lifecycle.DisconnectAsync(oldSession));

        var snapshot = environment.Lifecycle.Current;
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, snapshot.State);
        Assert.Null(snapshot.ActiveSession);
        Assert.Equal(oldSession, snapshot.CleanupSession);
        Assert.True(environment.Connection.IsCurrent(unrelatedLease));
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);
    }

    [Fact]
    public async Task ConnectRetiresInvalidatedActiveLeaseBeforeReactivation()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        var oldLease = environment.Connection.CurrentLease;
        Assert.True(environment.Connection.Invalidate(oldLease));

        var reconnected = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        var newSession = Assert.IsType<ReceiverSessionKey>(reconnected.Session);
        Assert.Equal(oldSession.SessionId, newSession.SessionId);
        Assert.Equal(oldSession.SessionGeneration, newSession.SessionGeneration);
        Assert.True(newSession.ConnectionEpoch > oldSession.ConnectionEpoch);
        Assert.Equal(2, environment.Receiver.ActivationCalls);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Equal(newSession, environment.Lifecycle.Current.ActiveSession);
        Assert.True(environment.Connection.IsCurrent(environment.Lifecycle.Current.Lease));
    }

    [Fact]
    public async Task ConnectRetiresLostReceiverAuthorityBeforeReactivation()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.RevokeAuthority();

        var reconnected = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, reconnected.Status);
        var newSession = Assert.IsType<ReceiverSessionKey>(reconnected.Session);
        Assert.True(newSession.ConnectionEpoch > oldSession.ConnectionEpoch);
        Assert.Equal(2, environment.Receiver.ActivationCalls);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Equal(newSession, environment.Lifecycle.Current.ActiveSession);
        Assert.True(environment.Connection.IsCurrent(environment.Lifecycle.Current.Lease));
    }

    [Fact]
    public async Task ReadyReconciliationRetiresInvalidatedActiveLease()
    {
        var environment = new LifecycleEnvironment();
        await environment.Lifecycle.ConnectAsync(1, 1);
        Assert.True(environment.Connection.Invalidate(
            environment.Connection.CurrentLease));

        var reconciled = await environment.Lifecycle.ReconcileReadinessAsync(isReady: true);

        Assert.Equal(SyncReceiverReadinessStatus.Inactive, reconciled.Status);
        Assert.Null(reconciled.Session);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.False(environment.Lifecycle.Current.Lease.IsValid);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Null(environment.Receiver.ActiveSession);
    }

    [Fact]
    public async Task ReadyReconciliationRetiresLostReceiverAuthority()
    {
        var environment = new LifecycleEnvironment();
        await environment.Lifecycle.ConnectAsync(1, 1);
        environment.Receiver.RevokeAuthority();

        var reconciled = await environment.Lifecycle.ReconcileReadinessAsync(isReady: true);

        Assert.Equal(SyncReceiverReadinessStatus.Inactive, reconciled.Status);
        Assert.Null(reconciled.Session);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
    }

    [Fact]
    public async Task ReadyReconciliationCannotAdoptUnrelatedReplacementLease()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        var oldLease = environment.Connection.CurrentLease;
        Assert.True(environment.Connection.Invalidate(oldLease));
        var unrelatedLease = environment.Connection.Acquire();

        var reconciled = await environment.Lifecycle.ReconcileReadinessAsync(isReady: true);

        Assert.Equal(SyncReceiverReadinessStatus.CleanupRequired, reconciled.Status);
        Assert.Equal(oldSession, reconciled.Session);
        var snapshot = environment.Lifecycle.Current;
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, snapshot.State);
        Assert.Null(snapshot.ActiveSession);
        Assert.Equal(oldSession, snapshot.CleanupSession);
        Assert.False(snapshot.Lease.IsValid);
        Assert.True(environment.Connection.IsCurrent(unrelatedLease));
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
    }

    [Fact]
    public async Task DependencyLossSuspendsActiveSessionAndRecoveryRequiresNewEpoch()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.IsReady = false;

        var suspended = await environment.Lifecycle.ReconcileReadinessAsync();

        Assert.Equal(SyncReceiverReadinessStatus.Suspended, suspended.Status);
        Assert.Equal(oldSession, suspended.Session);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Equal(
            SyncReceiverReadinessStatus.Suspended,
            (await environment.Lifecycle.ReconcileReadinessAsync()).Status);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Equal(
            SyncReceiverConnectStatus.DependenciesUnavailable,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);

        environment.IsReady = true;
        Assert.Equal(
            SyncReceiverReadinessStatus.Suspended,
            (await environment.Lifecycle.ReconcileReadinessAsync()).Status);
        var resumed = await environment.Lifecycle.ConnectAsync(1, 1);

        Assert.Equal(SyncReceiverConnectStatus.Activated, resumed.Status);
        Assert.Equal(2, resumed.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task ThrowingReadinessProbeRevokesEstablishedAuthorityAndCanRecover()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.ThrowReadiness = true;

        var suspended = await environment.Lifecycle.ReconcileReadinessAsync();

        Assert.Equal(SyncReceiverReadinessStatus.Suspended, suspended.Status);
        Assert.Equal(session, suspended.Session);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Null(environment.Receiver.ActiveSession);

        environment.ThrowReadiness = false;
        Assert.Equal(
            SyncReceiverReadinessStatus.Suspended,
            (await environment.Lifecycle.ReconcileReadinessAsync()).Status);
        Assert.Equal(
            SyncReceiverConnectStatus.Activated,
            (await environment.Lifecycle.ConnectAsync(1, 2)).Status);
    }

    [Fact]
    public async Task CapturedDependencyLossCannotBeOverwrittenByQueuedRecoveryObservation()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;

        var lost = environment.Lifecycle.ReconcileReadinessAsync(isReady: false);
        var recovered = environment.Lifecycle.ReconcileReadinessAsync(isReady: true);
        await Task.WhenAll(lost, recovered);

        Assert.Contains(
            (await lost).Status,
            new[]
            {
                SyncReceiverReadinessStatus.Suspended,
                SyncReceiverReadinessStatus.CleanupRequired,
            });
        Assert.Equal(SyncReceiverReadinessStatus.Suspended, (await recovered).Status);
        Assert.Equal(session, (await lost).Session);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
    }

    [Fact]
    public async Task ReentrantReadinessReconciliationCannotWaitOnItself()
    {
        var environment = new LifecycleEnvironment();
        await environment.Lifecycle.ConnectAsync(1, 1);
        environment.IsReady = false;
        SyncReceiverReadinessResult nested = default;
        environment.BeforeReadiness = () =>
            nested = environment.Lifecycle
                .ReconcileReadinessAsync(isReady: true)
                .GetAwaiter()
                .GetResult();

        var result = await environment.Lifecycle.ReconcileReadinessAsync();

        Assert.Equal(SyncReceiverReadinessStatus.Ready, nested.Status);
        Assert.Equal(SyncReceiverReadinessStatus.Suspended, result.Status);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
    }

    [Fact]
    public async Task ReadinessCleanupRetryPreservesSuspendedDestination()
    {
        var environment = new LifecycleEnvironment();
        var oldSession = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: true,
            Cancelled: false);
        environment.IsReady = false;

        var failed = await environment.Lifecycle.ReconcileReadinessAsync();

        Assert.Equal(SyncReceiverReadinessStatus.CleanupRequired, failed.Status);
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, environment.Lifecycle.Current.State);
        environment.IsReady = true;
        Assert.Equal(
            SyncReceiverReadinessStatus.CleanupRequired,
            (await environment.Lifecycle.ReconcileReadinessAsync()).Status);
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(1, 1)).Status);

        environment.Receiver.RetryResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 1,
            OwnershipFailed: 0,
            HasRemainingOwnership: false,
            Cancelled: false);
        var retried = await environment.Lifecycle.RetryCleanupAsync(oldSession);

        Assert.Equal(SyncReceiverDisconnectStatus.Suspended, retried.Status);
        Assert.Equal(SyncReceiverLifecycleState.Suspended, environment.Lifecycle.Current.State);
        var resumed = await environment.Lifecycle.ConnectAsync(1, 1);
        Assert.Equal(SyncReceiverConnectStatus.Activated, resumed.Status);
        Assert.Equal(2, resumed.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task ShutdownCleansActiveSessionAndPermanentlyRejectsReconnect()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;

        var shutdown = await environment.Lifecycle.ShutdownAsync();
        var repeated = await environment.Lifecycle.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, shutdown.Status);
        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, repeated.Status);
        Assert.Equal(SyncReceiverLifecycleState.Terminated, environment.Lifecycle.Current.State);
        Assert.False(environment.Connection.CurrentLease.IsValid);
        Assert.Equal(1, environment.Receiver.DisconnectCalls);
        Assert.Null(environment.Receiver.ActiveSession);
        Assert.Equal(
            SyncReceiverConnectStatus.Terminated,
            (await environment.Lifecycle.ConnectAsync(
                session.SessionId,
                session.SessionGeneration)).Status);
        Assert.Equal(
            SyncReceiverReadinessStatus.Terminated,
            (await environment.Lifecycle.ReconcileReadinessAsync()).Status);
    }

    [Fact]
    public async Task FailedShutdownCleanupRetainsTerminalDestinationUntilRetry()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: true,
            Cancelled: false);

        var first = await environment.Lifecycle.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, first.Status);
        Assert.Equal(SyncReceiverLifecycleState.CleanupRequired, environment.Lifecycle.Current.State);
        Assert.Equal(
            SyncReceiverConnectStatus.CleanupRequired,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);

        environment.Receiver.RetryResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 1,
            OwnershipFailed: 0,
            HasRemainingOwnership: false,
            Cancelled: false);
        var retried = await environment.Lifecycle.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, retried.Status);
        Assert.Equal(SyncReceiverLifecycleState.Terminated, environment.Lifecycle.Current.State);
        Assert.Equal(1, environment.Receiver.RetryCalls);
        Assert.Equal(
            SyncReceiverConnectStatus.Terminated,
            (await environment.Lifecycle.ConnectAsync(2, 1)).Status);
    }

    [Fact]
    public async Task RetryableShutdownCacheClearsBeforeResultPublication()
    {
        var environment = new LifecycleEnvironment();
        await environment.Lifecycle.ConnectAsync(1, 1);
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: true,
            Cancelled: false);
        environment.Receiver.RetryResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 1,
            OwnershipFailed: 0,
            HasRemainingOwnership: false,
            Cancelled: false);
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Lifecycle.BeforeRetryableShutdownResultPublication = () =>
        {
            publicationEntered.TrySetResult();
            releasePublication.Task.GetAwaiter().GetResult();
        };

        var first = Task.Run(() => environment.Lifecycle.ShutdownAsync());
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(first.IsCompleted);
        var retry = environment.Lifecycle.ShutdownAsync();
        releasePublication.TrySetResult();

        Assert.Equal(
            SyncReceiverDisconnectStatus.CleanupRequired,
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await retry.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(1, environment.Receiver.RetryCalls);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            environment.Lifecycle.Current.State);
    }

    [Fact]
    public async Task ExactCleanupRetryAdvancesFailedTerminalShutdown()
    {
        var environment = new LifecycleEnvironment();
        var session = (await environment.Lifecycle.ConnectAsync(1, 1)).Session!.Value;
        environment.Receiver.DisconnectResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 0,
            OwnershipFailed: 1,
            HasRemainingOwnership: true,
            Cancelled: false);

        var first = await environment.Lifecycle.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, first.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            environment.Lifecycle.Current.State);

        var unrelated = session with
        {
            SessionGeneration = session.SessionGeneration + 1,
        };
        Assert.Equal(
            SyncReceiverDisconnectStatus.CleanupRequired,
            (await environment.Lifecycle.RetryCleanupAsync(unrelated)).Status);
        Assert.Equal(0, environment.Receiver.RetryCalls);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            environment.Lifecycle.Current.State);

        environment.Receiver.RetryResult = new ReceiverApplyCleanupResult(
            OwnershipReleased: 1,
            OwnershipFailed: 0,
            HasRemainingOwnership: false,
            Cancelled: false);
        var retried = await environment.Lifecycle.RetryCleanupAsync(session);

        Assert.Equal(SyncReceiverDisconnectStatus.Terminated, retried.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            environment.Lifecycle.Current.State);
        Assert.Equal(1, environment.Receiver.RetryCalls);
    }

    private sealed class LifecycleEnvironment : IReceiverReadinessPort
    {
        public LifecycleEnvironment(
            SyncConnectionLock connection = null,
            int maximumLifecycleOperations =
                ClientLimits.DefaultReceiverLifecycleOperations)
        {
            Connection = connection ?? new SyncConnectionLock();
            Receiver = new FakeReceiverRuntime(Connection);
            Lifecycle = new SyncReceiverLifecycle(
                Connection,
                Receiver,
                this,
                maximumLifecycleOperations);
        }

        private bool isReady = true;

        public Action BeforeReadiness;
        public bool ThrowReadiness { get; set; }

        public bool IsReady
        {
            get
            {
                Interlocked.Exchange(ref BeforeReadiness, null)?.Invoke();
                if (ThrowReadiness)
                    throw new InvalidOperationException("Injected readiness failure.");

                return Volatile.Read(ref isReady);
            }
            set => Volatile.Write(ref isReady, value);
        }
        public SyncConnectionLock Connection { get; }
        public FakeReceiverRuntime Receiver { get; }
        public SyncReceiverLifecycle Lifecycle { get; }
    }

    private sealed class FakeReceiverRuntime : IReceiverActorSessionRuntime
    {
        private readonly SyncConnectionLock connection;

        public FakeReceiverRuntime(SyncConnectionLock connection)
        {
            this.connection = connection;
        }

        public bool RejectActivation { get; set; }
        public bool OwnSessionBeforeActivationFailure { get; set; }
        public Action<ReceiverSessionKey> BeforeActivation { get; set; }
        public bool BlockDisconnect { get; set; }
        public bool LeaseWasInvalidAtDisconnect { get; private set; }
        public int ActivationCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public ReceiverSessionKey? ActiveSession { get; private set; }
        public ReceiverApplyCleanupResult DisconnectResult { get; set; }
        public ReceiverApplyCleanupResult RetryResult { get; set; }
        public Exception DisconnectException { get; set; }
        public Exception ActivationException { get; set; }
        public Task<ReceiverApplyCleanupResult> LastDisconnectTask { get; private set; }
        public TaskCompletionSource DisconnectEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisconnect { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Action BeforeIsActive;

        public bool IsActive(ReceiverSessionKey session)
        {
            Interlocked.Exchange(ref BeforeIsActive, null)?.Invoke();
            return ActiveSession == session;
        }

        public void RevokeAuthority() => ActiveSession = null;

        public bool TryActivate(ReceiverSessionKey session)
        {
            ActivationCalls++;
            BeforeActivation?.Invoke(session);
            if (ActivationException is not null)
            {
                if (OwnSessionBeforeActivationFailure)
                    ActiveSession = session;
                throw ActivationException;
            }
            if (RejectActivation)
                return false;

            ActiveSession = session;
            return true;
        }

        public Task<ReceiverApplyCleanupResult> DisconnectAsync(
            ReceiverSessionKey session,
            CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            LeaseWasInvalidAtDisconnect = !connection.CurrentLease.IsValid;
            if (ActiveSession == session)
                ActiveSession = null;
            DisconnectEntered.TrySetResult();
            LastDisconnectTask = CompleteDisconnectAsync(cancellationToken);
            return LastDisconnectTask;
        }

        public Task<ReceiverApplyCleanupResult> RetryCleanupAsync(
            ReceiverSessionKey session,
            CancellationToken cancellationToken = default)
        {
            RetryCalls++;
            return Task.FromResult(RetryResult);
        }

        private async Task<ReceiverApplyCleanupResult> CompleteDisconnectAsync(
            CancellationToken cancellationToken)
        {
            if (BlockDisconnect)
                await ReleaseDisconnect.Task.WaitAsync(cancellationToken);
            if (DisconnectException is not null)
                throw DisconnectException;
            return DisconnectResult;
        }
    }
}
