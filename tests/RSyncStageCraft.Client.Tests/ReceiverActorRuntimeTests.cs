using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverActorRuntimeTests
{
    [Fact]
    public void CancellationCallbackWaitIsStrictlyBounded()
    {
        var ports = new FakePorts();

        Assert.Equal(
            ReceiverActorRuntime.DefaultCancellationCallbackWait,
            Runtime(ports, maximumActors: 1).MaximumSessionCancellationCallbackWait);
        Assert.Throws<ArgumentOutOfRangeException>(() => Runtime(
            ports,
            maximumActors: 1,
            maximumCancellationCallbackWait: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Runtime(
            ports,
            maximumActors: 1,
            maximumCancellationCallbackWait:
                ReceiverActorRuntime.MaximumCancellationCallbackWait +
                TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ActivationRequiresHealthyDependenciesAndCleanOwnership()
    {
        var ports = new FakePorts { IsReady = false };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);

        Assert.False(runtime.TryActivate(session));
        ports.IsReady = true;
        Assert.True(runtime.TryActivate(session));
        Assert.True(runtime.TryActivate(session));
        Assert.False(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
        Assert.Equal(session, runtime.State.Session);
    }

    [Fact]
    public async Task SlowOwnershipProbeDoesNotHoldSessionLifecycleLock()
    {
        var ports = new FakePorts { BlockOwnershipCount = true };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);

        var activation = Task.Run(() => runtime.TryActivate(session));
        await ports.OwnershipCountEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var inactiveCleanup = await runtime.DisconnectAsync(session)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var competing = await Task.Run(() => runtime.TryActivate(
                    new ReceiverSessionKey(2, 1, 2)))
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(inactiveCleanup.NeedsRetry);
            Assert.False(competing);
            Assert.False(activation.IsCompleted);
        }
        finally
        {
            ports.BlockOwnershipCount = false;
            ports.ReleaseOwnershipCount.TrySetResult();
        }

        Assert.True(await activation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(session, runtime.State.Session);
    }

    [Fact]
    public async Task SlowOrThrowingOwnershipSnapshotDoesNotRetainLifecycleOrEscape()
    {
        var ports = new FakePorts { BlockOwnershipCount = true };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        var snapshot = Task.Run(() => runtime.State);
        await ports.OwnershipCountEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.False(snapshot.IsCompleted);
        }
        finally
        {
            ports.BlockOwnershipCount = false;
            ports.ReleaseOwnershipCount.TrySetResult();
        }

        Assert.Equal(0, (await snapshot.WaitAsync(
            TimeSpan.FromSeconds(2))).PendingLocalObjectOwnerships);

        ports.ThrowOwnershipCount = true;
        Assert.Equal(1, runtime.State.PendingLocalObjectOwnerships);
    }

    [Fact]
    public void ThrowingOwnershipProbeFailsActivationClosedAndReleasesPumpSerializer()
    {
        var ports = new FakePorts { ThrowOwnershipCount = true };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);

        Assert.False(runtime.TryActivate(session));

        ports.ThrowOwnershipCount = false;
        Assert.Null(runtime.State.Session);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(session, runtime.State.Session);
    }

    [Fact]
    public void ThrowingReadinessProbeFailsActivationClosedAndRemainsRetryable()
    {
        var ports = new FakePorts { ThrowReadiness = true };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);

        Assert.False(runtime.TryActivate(session));

        ports.ThrowReadiness = false;
        Assert.Null(runtime.State.Session);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(session, runtime.State.Session);
    }

    [Fact]
    public async Task ReadinessLossDuringOwnershipProbePreventsActivation()
    {
        var ports = new FakePorts { BlockOwnershipCount = true };
        var readiness = new ReceiverReadinessCoordinator();
        readiness.Update(new(true, true, true, true, true));
        var runtime = Runtime(
            ports,
            maximumActors: 1,
            readiness: readiness);
        var session = new ReceiverSessionKey(1, 1, 1);

        var activation = Task.Run(() => runtime.TryActivate(session));
        await ports.OwnershipCountEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        try
        {
            Assert.True(readiness.Update(new(true, true, true, false, true)));
        }
        finally
        {
            ports.BlockOwnershipCount = false;
            ports.ReleaseOwnershipCount.TrySetResult();
        }

        Assert.False(await activation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(runtime.State.Session);
    }

    [Fact]
    public async Task EveryDesiredStateDestinationUsesAtomicReadinessAdmission()
    {
        var ports = new FakePorts();
        var readiness = new SwitchableAdmissionReadiness();
        var runtime = Runtime(
            ports,
            maximumActors: 1,
            readiness: readiness);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Completed,
            runtime.StageSnapshot(
                session,
                Snapshot(
                    new ServerSequence(0),
                    Array.Empty<AuthoritativeObjectState>())).Status);

        readiness.PermitAdmission = false;

        Assert.Equal(
            ReceiverActorIngressStatus.InactiveSession,
            runtime.AcceptCreated(
                session,
                new ObjectCreatedMessage(
                    new ServerSequence(1),
                    Chair(new(Guid.NewGuid())))).Status);
        Assert.Equal(0, runtime.State.DesiredLocalObjects);

        Assert.False((await runtime.DisconnectAsync(session)).HasRemainingOwnership);
    }

    [Fact]
    public async Task FixedWorkerPoolBoundsConcurrentDispatches()
    {
        var ports = new FakePorts { BlockDispatcher = true };
        var runtime = Runtime(
            ports,
            maximumActors: 4,
            maximumTrackedActors: 4,
            maximumWorkers: 2,
            snapshotAdmissions: 4);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        var chairs = Enumerable.Range(0, 4)
            .Select(_ => Chair(new(Guid.NewGuid())))
            .ToArray();
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                Snapshot(new ServerSequence(1), chairs)).Status);

        var firstPump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await ports.TwoDispatchesEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, ports.MaximumDispatchConcurrency);
        Assert.Equal(2, runtime.LocalObjectCoordinator!.QueuedCount);
        Assert.Equal(
            ReceiverActorPumpStatus.Busy,
            (await runtime.PumpAsync(session, DateTimeOffset.UtcNow)).Status);
        ports.ReleaseBlockedDispatch.TrySetResult();

        var first = await firstPump;
        Assert.Equal(ReceiverActorPumpStatus.Ran, first.Status);
        Assert.Equal(2, first.LocalProcessed);
        var second = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(2, second.LocalProcessed);
        Assert.Equal(2, ports.MaximumDispatchConcurrency);
        Assert.Equal(4, ports.SpawnCalls);
    }

    [Fact]
    public async Task DependencyLossPausesStagedSnapshotBeforeMailboxAdmission()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                Snapshot(
                    new ServerSequence(1),
                    new[] { Chair(new(Guid.NewGuid())) })).Status);
        ports.IsReady = false;

        var paused = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);

        Assert.Equal(ReceiverActorPumpStatus.DependenciesUnavailable, paused.Status);
        Assert.Equal(0, ports.SpawnCalls);
        Assert.Equal(0, runtime.LocalObjectCoordinator!.QueuedCount);
        Assert.Equal(1, runtime.Snapshots.PendingCount);
        ports.IsReady = true;
        Assert.Equal(
            1,
            (await runtime.PumpAsync(session, DateTimeOffset.UtcNow)).LocalProcessed);
    }

    [Fact]
    public async Task ThrowingReadinessProbePausesStagedSnapshotBeforeMailboxAdmission()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                Snapshot(
                    new ServerSequence(1),
                    new[] { Chair(new(Guid.NewGuid())) })).Status);
        ports.ThrowReadiness = true;

        var paused = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);

        Assert.Equal(ReceiverActorPumpStatus.DependenciesUnavailable, paused.Status);
        Assert.Equal(0, ports.SpawnCalls);
        Assert.Equal(0, runtime.LocalObjectCoordinator!.QueuedCount);
        Assert.Equal(1, runtime.Snapshots.PendingCount);
        ports.ThrowReadiness = false;
        Assert.Equal(
            1,
            (await runtime.PumpAsync(session, DateTimeOffset.UtcNow)).LocalProcessed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectCancelsBlockedWorkerBeforeDrainingSessionState(bool deferCallback)
    {
        var ports = new FakePorts
        {
            BlockDispatcher = true,
            BlockCancellationCallback = deferCallback,
        };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                Snapshot(
                    new ServerSequence(1),
                    new[] { Chair(new(Guid.NewGuid())) })).Status);

        var pump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await ports.BlockedDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ports.BlockDispatcher = false; // Only the first host call is slow.
        ReceiverApplyCleanupResult disconnected;
        try
        {
            disconnected = await runtime.DisconnectAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(runtime.Authority.IsCurrent(session));
            if (deferCallback)
            {
                Assert.True(disconnected.HasRemainingOwnership);
                Assert.False(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
            }
        }
        finally
        {
            ports.ReleaseCancellationCallback.TrySetResult();
        }
        var pumpResult = await pump.WaitAsync(TimeSpan.FromSeconds(5));

        // Callback scheduling may exceed the bounded first-pass grace even
        // without an explicitly blocked adapter. Observe drain, then use the
        // supported exact-session retry instead of assuming immediate cleanup.
        Assert.True(SpinWait.SpinUntil(
            () => runtime.State.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        if (disconnected.HasRemainingOwnership)
            disconnected = await runtime.RetryCleanupAsync(session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(disconnected.HasRemainingOwnership);
        Assert.False(disconnected.Cancelled);
        Assert.Equal(0, runtime.State.CancellationCallbackFailures);
        Assert.Equal(ReceiverActorPumpStatus.Cancelled, pumpResult.Status);
        Assert.Null(runtime.State.Session);
        Assert.Equal(0, runtime.State.DesiredLocalObjects);
        Assert.Equal(0, runtime.State.TrackedLocalWork);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.False(runtime.Authority.IsCurrent(session));
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotDelayAuthorityRevocationOrDisconnect()
    {
        var ports = new FakePorts
        {
            BlockDispatcher = true,
            BlockCancellationCallback = true,
        };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                Snapshot(
                    new ServerSequence(1),
                    new[] { Chair(new(Guid.NewGuid())) })).Status);
        var pump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await ports.BlockedDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ports.BlockDispatcher = false; // Only the first host call is slow.

        var disconnect = runtime.DisconnectAsync(session);
        ReceiverApplyCleanupResult first;
        try
        {
            await ports.CancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = await disconnect.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(runtime.Authority.IsCurrent(session));
            Assert.True(first.HasRemainingOwnership);
            Assert.False(first.Cancelled);
            Assert.Equal(1, first.PendingCancellationCallbacks);
            Assert.Equal(1, runtime.State.PendingCancellationCallbacks);
            Assert.False(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
        }
        finally
        {
            ports.ReleaseCancellationCallback.TrySetResult();
        }

        Assert.Equal(
            ReceiverActorPumpStatus.Cancelled,
            (await pump.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.True(SpinWait.SpinUntil(
            () => runtime.State.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        var retried = await runtime.RetryCleanupAsync(session);

        Assert.False(retried.HasRemainingOwnership);
        Assert.Equal(0, retried.PendingCancellationCallbacks);
        Assert.Equal(0, runtime.State.CancellationCallbackFailures);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsObservedAndCannotEscapeCleanup()
    {
        var ports = new FakePorts
        {
            BlockDispatcher = true,
            ThrowCancellationCallback = true,
        };
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        runtime.StageSnapshot(
            session,
            Snapshot(
                new ServerSequence(1),
                new[] { Chair(new(Guid.NewGuid())) }));
        var pump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await ports.BlockedDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ports.BlockDispatcher = false; // Only the first host call is slow.

        var first = await runtime.DisconnectAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
        var pumpResult = await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => runtime.State.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        var retried = await runtime.RetryCleanupAsync(session);

        Assert.Equal(ReceiverActorPumpStatus.Cancelled, pumpResult.Status);
        Assert.False(runtime.Authority.IsCurrent(session));
        Assert.Equal(1, runtime.State.CancellationCallbackFailures);
        Assert.Equal(0, runtime.State.PendingCancellationCallbacks);
        Assert.True(first.CancellationCallbackFailures is 0 or 1);
        Assert.False(retried.HasRemainingOwnership);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task DisconnectReleasesAppliedPluginOwnershipBeforeReconnect()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        runtime.StageSnapshot(
            session,
            Snapshot(
                new ServerSequence(1),
                new[] { Chair(new(Guid.NewGuid())) }));
        Assert.Equal(
            1,
            (await runtime.PumpAsync(session, DateTimeOffset.UtcNow)).LocalProcessed);

        var cleanup = await runtime.DisconnectAsync(session);

        Assert.Equal(1, cleanup.OwnershipReleased);
        Assert.False(cleanup.HasRemainingOwnership);
        Assert.Equal(1, ports.ReleaseCalls);
        Assert.Equal(0, runtime.LocalObjectLedger!.Count);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task PartialCleanupBlocksReconnectUntilBoundedRetryCompletes()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(oldSession));
        runtime.StageSnapshot(
            oldSession,
            Snapshot(
                new ServerSequence(1),
                new[] { Chair(new(Guid.NewGuid())) }));
        Assert.Equal(
            1,
            (await runtime.PumpAsync(oldSession, DateTimeOffset.UtcNow)).LocalProcessed);
        ports.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var first = await runtime.DisconnectAsync(oldSession);
        var newSession = new ReceiverSessionKey(2, 1, 2);

        Assert.True(first.HasRemainingOwnership);
        Assert.Equal(1, runtime.LocalObjectLedger!.Count);
        Assert.False(runtime.TryActivate(newSession));
        Assert.Equal(1, ports.ReleaseCalls);

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await runtime.RetryCleanupAsync(oldSession);

        Assert.False(retried.HasRemainingOwnership);
        Assert.Equal(0, runtime.LocalObjectLedger.Count);
        Assert.Equal(2, ports.ReleaseCalls);
        Assert.True(runtime.TryActivate(newSession));
    }

    [Fact]
    public async Task ConcurrentCleanupRetriesFailBusyInsteadOfQueueing()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(oldSession));
        runtime.StageSnapshot(
            oldSession,
            Snapshot(
                new ServerSequence(1),
                new[] { Chair(new(Guid.NewGuid())) }));
        Assert.Equal(
            1,
            (await runtime.PumpAsync(oldSession, DateTimeOffset.UtcNow)).LocalProcessed);
        ports.ReleaseStatus = ApplyPortStatus.TransientFailure;
        Assert.True((await runtime.DisconnectAsync(oldSession)).HasRemainingOwnership);

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        ports.BlockDispatcher = true;
        var activeRetry = runtime.RetryCleanupAsync(oldSession);
        await ports.BlockedDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ports.BlockDispatcher = false; // Only the first host call is slow.

        ReceiverApplyCleanupResult[] competing;
        try
        {
            competing = await Task.WhenAll(
                Enumerable.Range(0, 20)
                    .Select(_ => runtime.RetryCleanupAsync(oldSession)))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.All(competing, result =>
            {
                Assert.True(result.Busy);
                Assert.True(result.HasRemainingOwnership);
                Assert.False(result.Cancelled);
                Assert.Equal(0, result.OwnershipReleased);
                Assert.Equal(0, result.OwnershipFailed);
            });
            Assert.Equal(1, ports.ReleaseCalls);
            Assert.False(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
        }
        finally
        {
            ports.ReleaseBlockedDispatch.TrySetResult();
        }

        var completed = await activeRetry.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(completed.Busy);
        Assert.False(completed.HasRemainingOwnership);
        Assert.Equal(2, ports.ReleaseCalls);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task RetryCannotClearSessionBeforeDisconnectRegistersCancellationOwnership()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(oldSession));

        ReceiverApplyCleanupResult retry = default;
        runtime.BeforeSessionCancellationStart = () =>
        {
            retry = runtime.RetryCleanupAsync(oldSession)
                .GetAwaiter()
                .GetResult();
        };

        var disconnected = await runtime.DisconnectAsync(oldSession);

        Assert.True(retry.Busy);
        Assert.True(retry.HasRemainingOwnership);
        Assert.False(retry.Cancelled);
        Assert.False(disconnected.HasRemainingOwnership);
        Assert.Equal(0, runtime.State.PendingCancellationCallbacks);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task ActivationCannotOvertakePriorCleanupGateRelease()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        Assert.True(runtime.TryActivate(oldSession));
        runtime.StageSnapshot(
            oldSession,
            Snapshot(
                new ServerSequence(1),
                new[] { Chair(new(Guid.NewGuid())) }));
        Assert.Equal(
            1,
            (await runtime.PumpAsync(oldSession, DateTimeOffset.UtcNow)).LocalProcessed);
        ports.ReleaseStatus = ApplyPortStatus.TransientFailure;
        Assert.True((await runtime.DisconnectAsync(oldSession)).HasRemainingOwnership);

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        var cleanupCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanupGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BeforeCleanupGateRelease = () =>
        {
            cleanupCompleted.TrySetResult();
            releaseCleanupGate.Task.GetAwaiter().GetResult();
        };
        var retry = Task.Run(() => runtime.RetryCleanupAsync(oldSession));
        await cleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.Null(runtime.State.Session);
            Assert.False(runtime.TryActivate(newSession));
        }
        finally
        {
            releaseCleanupGate.TrySetResult();
        }

        Assert.False((await retry.WaitAsync(TimeSpan.FromSeconds(5))).HasRemainingOwnership);
        runtime.BeforeCleanupGateRelease = null;
        Assert.True(runtime.TryActivate(newSession));
        Assert.False((await runtime.DisconnectAsync(newSession)).HasRemainingOwnership);
    }

    [Fact]
    public async Task ActivationCannotOvertakeNonCooperativePriorSessionPump()
    {
        var ports = new FakePorts
        {
            BlockDispatcher = true,
            IgnoreDispatchCancellation = true,
        };
        var runtime = Runtime(ports, maximumActors: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        Assert.True(runtime.TryActivate(oldSession));
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                oldSession,
                Snapshot(
                    new ServerSequence(1),
                    new[] { Chair(new(Guid.NewGuid())) })).Status);
        var pump = runtime.PumpAsync(oldSession, DateTimeOffset.UtcNow);
        await ports.BlockedDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ports.BlockDispatcher = false; // Only the first host call is slow.

        try
        {
            var disconnected = await runtime.DisconnectAsync(oldSession)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(disconnected.HasRemainingOwnership);
            Assert.True(disconnected.Busy);
            Assert.False(runtime.Authority.IsCurrent(oldSession));
            Assert.Null(runtime.State.Session);
            // The ownership probe must not run while the old worker still
            // holds the pump slot.
            ports.ThrowOwnershipCountWhileDispatchActive = true;
            Assert.False(runtime.TryActivate(newSession));
        }
        finally
        {
            ports.ReleaseBlockedDispatch.TrySetResult();
        }

        Assert.Equal(
            ReceiverActorPumpStatus.Cancelled,
            (await pump.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        var retried = await runtime.RetryCleanupAsync(oldSession);
        Assert.False(retried.HasRemainingOwnership);
        Assert.Equal(ports.SpawnCalls, ports.ReleaseCalls);
        Assert.True(runtime.TryActivate(newSession));
        Assert.False((await runtime.DisconnectAsync(newSession)).HasRemainingOwnership);
    }

    [Fact]
    public void StateContainsThrowingExternalCleanupDiagnostics()
    {
        var ports = new FakePorts { ThrowOwnershipCount = true };
        var runtime = Runtime(ports, maximumActors: 1);

        var state = runtime.State;

        Assert.Equal(1, state.PendingLocalObjectOwnerships);
    }

    [Fact]
    public async Task CancelledCoreCleanupBlocksActivationUntilExactRetryConverges()
    {
        var ports = new FakePorts();
        var runtime = Runtime(
            ports,
            maximumActors: 4,
            maximumTrackedActors: 4,
            maximumCleanupEntriesPerPass: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(oldSession));
        var chairs = Enumerable.Range(0, 4)
            .Select(_ => Chair(new(Guid.NewGuid())))
            .ToArray();
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                oldSession,
                Snapshot(new ServerSequence(1), chairs)).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var interrupted = await runtime.DisconnectAsync(
            oldSession,
            cancelled.Token);
        var newSession = new ReceiverSessionKey(2, 1, 2);

        Assert.True(interrupted.Cancelled);
        Assert.True(interrupted.HasRemainingOwnership);
        Assert.Equal(0, runtime.LocalObjectLedger!.Count);
        Assert.False(runtime.TryActivate(newSession));
        Assert.Equal(
            default,
            await runtime.RetryCleanupAsync(new ReceiverSessionKey(3, 1, 3)));
        Assert.False(runtime.TryActivate(newSession));

        var retried = await runtime.RetryCleanupAsync(oldSession);

        Assert.False(retried.Cancelled);
        Assert.False(retried.HasRemainingOwnership);
        Assert.Equal(0, runtime.State.DesiredLocalObjects);
        Assert.Equal(0, runtime.State.TrackedLocalWork);
        Assert.True(runtime.TryActivate(newSession));
    }

    [Fact]
    public void PreexistingOrphanLocalObjectOwnershipFailsActivationClosed()
    {
        Assert.False(typeof(ILocalObjectRuntimeOwnershipDiagnostics).IsPublic);
        var ports = new FakePorts();
        var localObjects = new LocalObjectDiagnosticsPort
        {
            PendingOwnerships = 1,
        };
        var runtime = Runtime(
            ports,
            maximumActors: 1,
            localObjectRuntime: localObjects);
        var session = new ReceiverSessionKey(2, 1, 2);

        Assert.Equal(1, runtime.State.PendingLocalObjectOwnerships);
        Assert.False(runtime.TryActivate(session));

        localObjects.PendingOwnerships = 0;
        Assert.Equal(0, runtime.State.PendingLocalObjectOwnerships);
        Assert.True(runtime.TryActivate(session));
    }

    [Fact]
    public async Task ResidualAdapterOwnershipKeepsDisconnectCleanupRetryable()
    {
        var ports = new FakePorts();
        var localObjects = new LocalObjectDiagnosticsPort();
        var runtime = Runtime(
            ports,
            maximumActors: 1,
            localObjectRuntime: localObjects);
        var session = new ReceiverSessionKey(1, 1, 1);
        var successor = new ReceiverSessionKey(2, 1, 2);
        Assert.True(runtime.TryActivate(session));

        localObjects.PendingOwnerships = 1;

        var disconnected = await runtime.DisconnectAsync(session);

        Assert.True(disconnected.NeedsRetry);
        Assert.True(disconnected.HasRemainingOwnership);
        Assert.False(runtime.TryActivate(successor));
        Assert.True((await runtime.RetryCleanupAsync(session)).NeedsRetry);

        localObjects.PendingOwnerships = 0;
        var retried = await runtime.RetryCleanupAsync(session);

        Assert.False(retried.NeedsRetry);
        Assert.True(runtime.TryActivate(successor));
    }

    [Fact]
    public async Task UnreadableAdapterOwnershipKeepsDisconnectCleanupRetryable()
    {
        var ports = new FakePorts();
        var runtime = Runtime(ports, maximumActors: 1);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        ports.ThrowOwnershipCount = true;

        var disconnected = await runtime.DisconnectAsync(session);

        Assert.True(disconnected.NeedsRetry);
        Assert.True(disconnected.HasRemainingOwnership);
        ports.ThrowOwnershipCount = false;
        ports.PendingOwnerships = -1;
        Assert.True((await runtime.RetryCleanupAsync(session)).NeedsRetry);
        ports.PendingOwnerships = 0;
        Assert.False((await runtime.RetryCleanupAsync(session)).NeedsRetry);
    }

    private static ReceiverActorRuntime Runtime(
        FakePorts ports,
        int maximumActors,
        int? maximumTrackedActors = null,
        int maximumWorkers = 1,
        int? snapshotAdmissions = null,
        int? maximumCleanupEntriesPerPass = null,
        int? maximumRetryPromotions = null,
        TimeSpan? maximumCancellationCallbackWait = null,
        ILocalObjectRuntimePort localObjectRuntime = null,
        IReceiverReadinessPort readiness = null,
        IReceiverReadinessPort localReadiness = null,
        ILocalObjectVisibility visibility = null,
        TimeSpan? localObjectLoadTimeout = null)
    {
        var tracked = maximumTrackedActors ?? maximumActors;
        var limits = new ReceiverActorRuntimeLimits(
            MaximumActors: maximumActors,
            MaximumTrackedActors: tracked,
            MaximumRetryActors: maximumActors,
            MaximumConcurrentWorkers: maximumWorkers,
            MaximumSnapshotAdmissionsPerPump: snapshotAdmissions ?? maximumActors,
            MaximumRetryPromotionsPerPump: maximumRetryPromotions ?? maximumActors,
            MaximumCleanupEntriesPerPass:
                maximumCleanupEntriesPerPass ?? maximumActors);
        return new ReceiverActorRuntime(
            ports,
            readiness ?? ports,
            limits,
            localObjectRuntime: localObjectRuntime ?? ports,
            localObjectRetryPolicy: new LocalObjectApplyRetryPolicy(
                MaximumAttempts: 2,
                InitialDelay: TimeSpan.FromMilliseconds(1),
                MaximumDelay: TimeSpan.FromMilliseconds(2),
                JitterRatio: 0),
            maximumCancellationCallbackWait: maximumCancellationCallbackWait,
            localObjectApplyReadiness: localReadiness,
            localObjectVisibility: visibility,
            localObjectLoadTimeout: localObjectLoadTimeout);
    }

    private sealed class SwitchableAdmissionReadiness :
        IReceiverReadinessPort,
        IReceiverReadinessAdmissionFence
    {
        public bool PermitAdmission { get; set; } = true;
        public bool IsReady => true;

        bool IReceiverReadinessAdmissionFence.TryExecuteIfReady<T>(
            Func<T> operation,
            out T result)
        {
            if (!PermitAdmission)
            {
                result = default!;
                return false;
            }

            result = operation();
            return true;
        }
    }

    /// <summary>
    /// Dispatcher, readiness and local-object host in one fake. A blocked
    /// dispatch models a slow host call; its cancellation callback models
    /// adapter code that runs when the session is cancelled.
    /// </summary>
    private sealed class FakePorts :
        IGameThreadDispatcher,
        IReceiverReadinessPort,
        ILocalObjectRuntimePort,
        ILocalObjectRuntimeOwnershipDiagnostics
    {
        private readonly object gate = new();
        private readonly HashSet<Guid> owned = new();
        private int activeDispatches;
        private int maximumDispatchConcurrency;
        private int spawnCalls;
        private int applyCalls;
        private int releaseCalls;

        private bool isReady = true;
        public bool IsReady
        {
            get
            {
                if (ThrowReadiness)
                    throw new InvalidOperationException("Readiness unavailable.");

                return isReady;
            }
            set => isReady = value;
        }
        public bool ThrowReadiness { get; set; }
        public bool BlockDispatcher { get; set; }
        public bool IgnoreDispatchCancellation { get; set; }
        public bool BlockCancellationCallback { get; set; }
        public bool ThrowCancellationCallback { get; set; }
        public bool BlockOwnershipCount { get; set; }
        public bool ThrowOwnershipCount { get; set; }
        public bool ThrowOwnershipCountWhileDispatchActive { get; set; }
        public int PendingOwnerships { get; set; }
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public int SpawnCalls => Volatile.Read(ref spawnCalls);
        public int ApplyCalls => Volatile.Read(ref applyCalls);
        public int ReleaseCalls => Volatile.Read(ref releaseCalls);
        public int MaximumDispatchConcurrency => Volatile.Read(ref maximumDispatchConcurrency);
        public TaskCompletionSource BlockedDispatchEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoDispatchesEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedDispatch { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationCallbackEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellationCallback { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OwnershipCountEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOwnershipCount { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount
        {
            get
            {
                if (BlockOwnershipCount)
                {
                    OwnershipCountEntered.TrySetResult();
                    ReleaseOwnershipCount.Task.GetAwaiter().GetResult();
                }

                if (ThrowOwnershipCount)
                    throw new InvalidOperationException("Ownership diagnostics unavailable.");
                if (ThrowOwnershipCountWhileDispatchActive &&
                    Volatile.Read(ref activeDispatches) != 0)
                {
                    throw new InvalidOperationException(
                        "Ownership was probed before the prior pump was fenced.");
                }

                return PendingOwnerships;
            }
        }

        public async Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockDispatcher)
            {
                // Register the callback before waiting. Cancellation invokes
                // it before waking this call, so unwinding cannot unregister
                // the callback a test is waiting for.
                var wait = IgnoreDispatchCancellation
                    ? ReleaseBlockedDispatch.Task
                    : ReleaseBlockedDispatch.Task.WaitAsync(cancellationToken);
                using var registration = cancellationToken.Register(() =>
                {
                    if (ThrowCancellationCallback)
                    {
                        throw new InvalidOperationException(
                            "Injected receiver cancellation callback failure.");
                    }
                    if (!BlockCancellationCallback)
                        return;

                    CancellationCallbackEntered.TrySetResult();
                    ReleaseCancellationCallback.Task.GetAwaiter().GetResult();
                });
                var active = Interlocked.Increment(ref activeDispatches);
                UpdateMaximum(active);
                BlockedDispatchEntered.TrySetResult();
                if (active >= 2)
                    TwoDispatchesEntered.TrySetResult();
                try
                {
                    await wait.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref activeDispatches);
                    // A cancelled wait completes inside the cancellation
                    // callbacks. Leave that thread, as a real dispatcher would,
                    // so the whole pump never unwinds inside the cancellation.
                    await Task.Yield();
                }
            }

            lock (gate)
                return operation();
        }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request)
        {
            Interlocked.Increment(ref spawnCalls);
            var handle = new LocalObjectInstanceHandle(
                Guid.NewGuid(),
                request.Token.ObjectId,
                request.Identity.Kind,
                request.SpawnGeneration);
            owned.Add(handle.OwnershipId);
            return new(ApplyPortStatus.Succeeded, handle);
        }

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState)
        {
            Interlocked.Increment(ref applyCalls);
            return new(ApplyPortStatus.Succeeded);
        }

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            Interlocked.Increment(ref releaseCalls);
            if (ReleaseStatus != ApplyPortStatus.Succeeded)
                return new(ReleaseStatus, "release failed");

            owned.Remove(handle.OwnershipId);
            return new(ApplyPortStatus.Succeeded);
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumDispatchConcurrency);
                if (active <= current ||
                    Interlocked.CompareExchange(
                        ref maximumDispatchConcurrency,
                        active,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class LocalObjectDiagnosticsPort :
        ILocalObjectRuntimePort,
        ILocalObjectRuntimeOwnershipDiagnostics
    {
        public int PendingOwnerships { get; set; }

        int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount =>
            PendingOwnerships;

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) =>
            throw new NotSupportedException();

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) =>
            throw new NotSupportedException();

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle) =>
            throw new NotSupportedException();
    }

    private static ObjectSnapshotMessage Snapshot(
        ServerSequence watermark,
        IReadOnlyList<AuthoritativeObjectState> states) => new(
        watermark,
        states.ToArray(),
        Array.Empty<ObjectTombstone>());

    // ---- Per-domain apply readiness, visibility and bounded loading ----

    private sealed class SwitchReadiness : IReceiverReadinessPort
    {
        public bool IsReady { get; set; } = true;
    }

    private sealed class HereFilter : ILocalObjectVisibility
    {
        public HashSet<RscObjectId> Visible { get; set; }
        public bool IsVisibleHere(AuthoritativeObjectState state) =>
            Visible is null || Visible.Contains(state.ObjectId);
    }

    /// <summary>Local-object host whose objects may take a few polls to load.</summary>
    private sealed class LoadingLocalPort :
        ILocalObjectRuntimePort,
        ILocalObjectRuntimeOwnershipDiagnostics
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, (LocalObjectInstanceHandle Handle, int PollsLeft)> live = new();

        public bool SpawnLoads { get; set; }
        public int PollsToLoad { get; set; }
        public bool FailLoad { get; set; }
        public bool ThrowLoad { get; set; }
        public int SpawnCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int LoadPolls { get; private set; }
        public List<RscObjectId> Spawned { get; } = new();
        public List<LocalObjectInstanceHandle> Released { get; } = new();
        public TaskCompletionSource FirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Live { get { lock (gate) return live.Count; } }
        int ILocalObjectRuntimeOwnershipDiagnostics.PendingOwnershipCount => 0;

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request)
        {
            lock (gate)
            {
                SpawnCalls++;
                Spawned.Add(request.Token.ObjectId);
                var handle = new LocalObjectInstanceHandle(
                    Guid.NewGuid(), request.Token.ObjectId, request.Identity.Kind, request.SpawnGeneration);
                live.Add(handle.OwnershipId, (handle, SpawnLoads ? PollsToLoad : 0));
                return new(SpawnLoads ? ApplyPortStatus.AwaitingPayload : ApplyPortStatus.Succeeded, handle);
            }
        }

        public ApplyPortResult ApplyOwned(LocalObjectInstanceHandle handle, AuthoritativeObjectState desiredState)
        {
            lock (gate)
            {
                Assert.True(live.ContainsKey(handle.OwnershipId), "Applied to an instance that is not owned.");
                LoadPolls++;
                FirstPoll.TrySetResult();
                if (FailLoad)
                    return new(ApplyPortStatus.PermanentFailure, "model failed to load");
                if (ThrowLoad)
                    throw new InvalidOperationException("adapter fault");
                var entry = live[handle.OwnershipId];
                if (entry.PollsLeft > 0)
                {
                    live[handle.OwnershipId] = entry with { PollsLeft = entry.PollsLeft - 1 };
                    return new(ApplyPortStatus.AwaitingPayload);
                }
                return new(ApplyPortStatus.Succeeded);
            }
        }

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            lock (gate)
            {
                ReleaseCalls++;
                Released.Add(handle);
                live.Remove(handle.OwnershipId);
                return new(ApplyPortStatus.Succeeded);
            }
        }

        public void FinishLoading()
        {
            lock (gate)
            {
                foreach (var key in live.Keys.ToArray())
                    live[key] = live[key] with { PollsLeft = 0 };
                PollsToLoad = 0;
            }
        }
    }

    private static FurnitureState Chair(RscObjectId id, long revision = 1, float x = 1) => new(
        id,
        new ObjectRevision(revision),
        new RscUserId("creator"),
        null,
        "furniture:indoor:1",
        new Vector3State(x, 2, 3),
        new RotationState(0, 0, 0, 1));

    private static async Task<ReceiverActorPumpResult> PumpUntilAsync(
        ReceiverActorRuntime runtime,
        ReceiverSessionKey session,
        Func<bool> done,
        int maximumPumps = 12)
    {
        ReceiverActorPumpResult last = default;
        for (var pump = 0; pump < maximumPumps && !done(); pump++)
            last = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.True(done(), "The runtime did not converge.");
        return last;
    }

    private static (ReceiverActorRuntime Runtime, ReceiverSessionKey Session) LocalScene(
        FakePorts ports,
        LoadingLocalPort local,
        SwitchReadiness localReady,
        out FurnitureState chair,
        ILocalObjectVisibility visibility = null,
        TimeSpan? loadTimeout = null,
        IReadOnlyList<AuthoritativeObjectState> extraLocal = null)
    {
        var runtime = Runtime(
            ports,
            maximumActors: 4,
            maximumWorkers: 2,
            localObjectRuntime: local,
            localReadiness: localReady,
            visibility: visibility,
            localObjectLoadTimeout: loadTimeout);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        chair = Chair(new(Guid.NewGuid()));
        var states = new List<AuthoritativeObjectState> { chair };
        if (extraLocal is not null)
            states.AddRange(extraLocal);
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(session, new ObjectSnapshotMessage(new ServerSequence(1), states.ToArray(), [])).Status);
        return (runtime, session);
    }

    [Fact]
    public async Task LocalHostLossReleasesReceiptsAndRecoveryRespawnsThem()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort();
        var localReady = new SwitchReadiness();
        var (runtime, session) = LocalScene(ports, local, localReady, out var chair);
        await PumpUntilAsync(runtime, session, () => local.Live == 1);

        localReady.IsReady = false;
        runtime.ResetLocalObjects();
        await PumpUntilAsync(runtime, session, () => local.Live == 0);

        // The instance was released; the desired object stays, and the
        // session is still the active one.
        Assert.Equal(1, local.ReleaseCalls);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.Equal(1, runtime.State.DesiredLocalObjects);
        Assert.Equal(session, runtime.State.Session);
        Assert.Equal(ApplyDomainPhase.Readmitting, runtime.LocalObjectDomain.Phase);

        // While the host is gone nothing is respawned.
        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, local.SpawnCalls);

        localReady.IsReady = true;
        await PumpUntilAsync(runtime, session, () => local.Live == 1);
        Assert.Equal(2, local.SpawnCalls);
        Assert.Equal(chair.ObjectId, local.Spawned[^1]);
        Assert.Equal(ApplyDomainPhase.Open, runtime.LocalObjectDomain.Phase);
    }

    [Fact]
    public async Task LocationChangeReleasesEverythingAndSpawnsOnlyWhatIsVisibleHere()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort();
        var other = Chair(new(Guid.NewGuid()), x: 5);
        var here = new HereFilter();
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out var chair,
            visibility: here, extraLocal: [other]);
        here.Visible = [chair.ObjectId];
        await PumpUntilAsync(runtime, session, () => local.Live == 1);
        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal([chair.ObjectId], local.Spawned);

        // The player moves: only the other object belongs here now.
        here.Visible = [other.ObjectId];
        runtime.ResetLocalObjects();
        await PumpUntilAsync(runtime, session, () => local.Live == 1 && local.SpawnCalls == 2);

        Assert.Equal(1, local.ReleaseCalls);
        Assert.Equal(other.ObjectId, local.Spawned[^1]);
        Assert.Equal(2, runtime.State.DesiredLocalObjects);
    }

    [Fact]
    public async Task NothingIsDrawnWhereTheObjectDoesNotBelong()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort();
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out _,
            visibility: new HereFilter { Visible = [] });

        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);

        Assert.Equal(0, local.SpawnCalls);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.Equal(1, runtime.State.DesiredLocalObjects);
    }

    [Fact]
    public async Task CreatedButLoadingIsNotAppliedUntilTheModelLoads()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = 3 };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out var chair);

        var pump = await PumpUntilAsync(runtime, session, () => runtime.State.OwnedLocalObjects == 1);

        Assert.Equal(1, local.SpawnCalls);
        Assert.Equal(4, local.LoadPolls);
        Assert.Equal(0, local.ReleaseCalls);
        Assert.True(runtime.LocalObjectLedger!.TryGetObject(session, chair.ObjectId, out var reservation));
        Assert.True(runtime.LocalObjectLedger.IsApplied(reservation.Token));
    }

    [Fact]
    public async Task FailedLoadReleasesTheExactInstance()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = 1, FailLoad = true };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out var chair);

        await PumpUntilAsync(runtime, session, () => local.ReleaseCalls == 1);

        Assert.Equal(0, local.Live);
        Assert.Equal(chair.ObjectId, local.Released.Single().ObjectId);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
    }

    [Fact]
    public async Task AdapterExceptionDuringLoadReleasesTheExactInstance()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = 1, ThrowLoad = true };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out var chair);

        await PumpUntilAsync(runtime, session, () => local.ReleaseCalls >= 1);

        // The exact loading instance is released, not left owned while
        // retries run out; a retry may respawn, but instances never pile up.
        Assert.Equal(local.Released[0].ObjectId, chair.ObjectId);
        Assert.InRange(local.SpawnCalls - local.ReleaseCalls, 0, 1);
        Assert.InRange(local.Live, 0, 1);
        for (var pump = 0; pump < 20; pump++)
            await runtime.PumpAsync(session, DateTimeOffset.UtcNow.AddSeconds(pump * 30));
        Assert.InRange(local.SpawnCalls - local.ReleaseCalls, 0, 1);
    }

    [Fact]
    public async Task LoadThatNeverFinishesIsReleasedAfterTheTimeout()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = int.MaxValue };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out _,
            loadTimeout: TimeSpan.FromMilliseconds(200));

        await PumpUntilAsync(runtime, session, () => local.ReleaseCalls == 1);

        Assert.Equal(0, local.Live);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.InRange(local.LoadPolls, 2, 40);
    }

    [Fact]
    public async Task StaleLoadPollCannotApplyANewerGeneration()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = int.MaxValue };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out var chair);

        // The first pump starts the chair, which keeps loading.
        var loadingPump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await local.FirstPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var moved = chair with { Revision = new ObjectRevision(2), Position = new Vector3State(9, 9, 9) };
        Assert.True(runtime.AcceptUpdated(session, new ObjectUpdatedMessage(new ServerSequence(2), moved)).Accepted);
        var stale = await loadingPump;
        Assert.Contains(stale.LocalWork!, work => work.Outcome.Status == LocalObjectReconcileStatus.Stale);

        // The instance stays owned for the newer generation, which applies it
        // once loaded; nothing was released or spawned twice.
        local.FinishLoading();
        await PumpUntilAsync(runtime, session, () =>
            runtime.LocalObjectLedger!.TryGetObject(session, chair.ObjectId, out var current) &&
            runtime.LocalObjectLedger.IsApplied(current.Token));
        Assert.Equal(1, local.SpawnCalls);
        Assert.Equal(0, local.ReleaseCalls);
        Assert.True(runtime.LocalObjectLedger!.TryGetObject(session, chair.ObjectId, out var reservation));
        Assert.True(runtime.LocalObjectLedger.IsApplied(reservation.Token));
    }

    [Fact]
    public async Task DisconnectWhileLoadingReleasesTheExactInstance()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort { SpawnLoads = true, PollsToLoad = int.MaxValue };
        var (runtime, session) = LocalScene(
            ports, local, new SwitchReadiness(), out _);
        var loadingPump = runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        await local.FirstPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cleanup = await runtime.DisconnectAsync(session);
        await loadingPump;
        for (var retry = 0; cleanup.HasRemainingOwnership && retry < 5; retry++)
            cleanup = await runtime.RetryCleanupAsync(session);

        Assert.False(cleanup.HasRemainingOwnership);
        Assert.Equal(1, local.ReleaseCalls);
        Assert.Equal(0, local.Live);
        Assert.Equal(0, runtime.LocalObjectLedger!.Count);
    }

    [Fact]
    public async Task DisconnectDuringADomainResetReleasesEverythingAndClearsTheReset()
    {
        var ports = new FakePorts();
        var local = new LoadingLocalPort();
        var localReady = new SwitchReadiness();
        var (runtime, session) = LocalScene(ports, local, localReady, out _);
        await PumpUntilAsync(runtime, session, () => local.Live == 1);
        localReady.IsReady = false;
        runtime.ResetLocalObjects();

        var cleanup = await runtime.DisconnectAsync(session);

        Assert.False(cleanup.HasRemainingOwnership);
        Assert.Equal(0, local.Live);
        Assert.Equal(0, runtime.LocalObjectLedger!.Count);
        Assert.Equal(ApplyDomainPhase.Open, runtime.LocalObjectDomain.Phase);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }
}
