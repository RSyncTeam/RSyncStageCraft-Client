using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectApplyWorkerTests
{
    [Fact]
    public async Task UnavailableWorkerDoesNotBeginQueuedWorkOrPromoteRetries()
    {
        var fixture = new Fixture(blockAfterFirstOperation: false);
        fixture.AdmitCurrent();
        fixture.Readiness.IsReady = false;

        var result = await fixture.Worker.TryProcessNextAsync(DateTimeOffset.UtcNow);

        Assert.False(result.Processed);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.Equal(0, fixture.Coordinator.ActiveCount);
        Assert.Equal(0, fixture.Worker.PromoteDueRetries(DateTimeOffset.MaxValue, 1));
        Assert.Equal(0, fixture.Runtime.SpawnCalls);
    }

    [Fact]
    public async Task AtomicReadinessDenialCannotBeginOrPromoteLocalWork()
    {
        var queued = new Fixture(blockAfterFirstOperation: false);
        queued.AdmitCurrent();
        var readiness = new SwitchableAdmissionReadiness
        {
            PermitAdmission = false,
        };
        var worker = Worker(queued, readiness);

        var result = await worker.TryProcessNextAsync(DateTimeOffset.UtcNow);

        Assert.False(result.Processed);
        Assert.Equal(0, queued.Runtime.SpawnCalls);
        Assert.Equal(1, queued.Coordinator.QueuedCount);

        var delayed = new Fixture(blockAfterFirstOperation: false);
        Assert.True(delayed.Retries.TrySchedule(
            delayed.Current.Token,
            DateTimeOffset.UtcNow).Scheduled);
        Assert.Equal(0, Worker(delayed, readiness).PromoteDueRetries(
            DateTimeOffset.MaxValue,
            maximumEntries: 1));
        Assert.Equal(0, delayed.Coordinator.TrackedCount);
        Assert.Equal(1, delayed.Retries.Count);
    }

    private static LocalObjectApplyWorker Worker(
        Fixture fixture,
        IReceiverReadinessPort readiness) => new(
        fixture.Store,
        fixture.Coordinator,
        new LocalObjectReconciler(
            fixture.Store,
            fixture.Ledger,
            fixture.Dispatcher,
            fixture.Runtime,
            readiness),
        new LocalObjectRemovalExecutor(
            fixture.Store,
            fixture.OwnershipCleanup),
        fixture.Retries,
        readiness);

    private static readonly LocalObjectApplyRetryPolicy RetryPolicy = new(
        MaximumAttempts: 2,
        InitialDelay: TimeSpan.FromSeconds(1),
        MaximumDelay: TimeSpan.FromSeconds(4),
        JitterRatio: 0);

    [Fact]
    public async Task TransientFailureWaitsForDueTickThenRetriesOnce()
    {
        var fixture = new Fixture(
            blockAfterFirstOperation: false,
            retryPolicy: RetryPolicy);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.AdmitCurrent();
        var now = DateTimeOffset.UtcNow;

        var failed = await fixture.Worker.TryProcessNextAsync(now);

        Assert.True(failed.Outcome.Retryable);
        Assert.True(failed.Retry.Scheduled);
        Assert.Equal(1, fixture.Retries.Count);
        Assert.Equal(0, fixture.Worker.PromoteDueRetries(
            now + TimeSpan.FromMilliseconds(999),
            maximumEntries: 1));
        Assert.Equal(1, fixture.Worker.PromoteDueRetries(
            now + TimeSpan.FromSeconds(1),
            maximumEntries: 1));

        fixture.Runtime.SpawnStatus = ApplyPortStatus.Succeeded;
        var applied = await fixture.Worker.TryProcessNextAsync(
            now + TimeSpan.FromSeconds(1));

        Assert.True(applied.Outcome.Succeeded);
        Assert.Equal(2, fixture.Runtime.SpawnCalls);
        Assert.Equal(0, fixture.Retries.Count);
        Assert.Equal(0, fixture.Coordinator.TrackedCount);
    }

    [Fact]
    public async Task DuplicateAdmissionWhileRetryIsDelayedCannotProduceSecondApply()
    {
        var fixture = new Fixture(blockAfterFirstOperation: false);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.AdmitCurrent();
        var now = DateTimeOffset.UtcNow;
        Assert.True((await fixture.Worker.TryProcessNextAsync(now)).Retry.Scheduled);

        fixture.Runtime.SpawnStatus = ApplyPortStatus.Succeeded;
        fixture.AdmitCurrent();
        var applied = await fixture.Worker.TryProcessNextAsync(now);

        Assert.True(applied.Outcome.Succeeded);
        Assert.Equal(2, fixture.Runtime.SpawnCalls);
        Assert.Equal(0, fixture.Retries.Count);
        Assert.Equal(0, fixture.Worker.PromoteDueRetries(
            DateTimeOffset.MaxValue,
            maximumEntries: 1));
        Assert.False((await fixture.Worker.TryProcessNextAsync(
            DateTimeOffset.MaxValue)).Processed);
        Assert.Equal(2, fixture.Runtime.SpawnCalls);
    }

    [Fact]
    public async Task TombstoneRemovesSupersededDelayedRetryWithoutSpawningAgain()
    {
        var fixture = new Fixture(blockAfterFirstOperation: false);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.AdmitCurrent();
        Assert.True((await fixture.Worker.TryProcessNextAsync(
            DateTimeOffset.UtcNow)).Retry.Scheduled);

        var removal = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                fixture.ObjectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;
        Assert.True(fixture.Coordinator.TryAdmit(
            fixture.Session,
            fixture.ObjectId));

        var removed = await fixture.Worker.TryProcessNextAsync(DateTimeOffset.UtcNow);

        Assert.True(removed.Outcome.Succeeded);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(0, fixture.Runtime.ReleaseCalls);
        Assert.Equal(0, fixture.Retries.Count);
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            fixture.ObjectId,
            out var completed));
        Assert.Equal(removal.Token, completed.Token);
        Assert.True(completed.Completed);
    }

    [Fact]
    public async Task RetryExhaustionDropsDelayedStateAndReleasesMailboxAdmission()
    {
        var fixture = new Fixture(
            blockAfterFirstOperation: false,
            retryPolicy: RetryPolicy);
        fixture.Runtime.SpawnStatus = ApplyPortStatus.TransientFailure;
        fixture.AdmitCurrent();
        var now = DateTimeOffset.UtcNow;

        Assert.True((await fixture.Worker.TryProcessNextAsync(now)).Retry.Scheduled);
        Assert.Equal(1, fixture.Worker.PromoteDueRetries(
            now + TimeSpan.FromSeconds(1),
            maximumEntries: 1));
        Assert.True((await fixture.Worker.TryProcessNextAsync(
            now + TimeSpan.FromSeconds(1))).Retry.Scheduled);
        Assert.Equal(1, fixture.Worker.PromoteDueRetries(
            now + TimeSpan.FromSeconds(3),
            maximumEntries: 1));

        var exhausted = await fixture.Worker.TryProcessNextAsync(
            now + TimeSpan.FromSeconds(3));

        Assert.True(exhausted.Outcome.Retryable);
        Assert.Equal(
            LocalObjectApplyRetryScheduleStatus.Exhausted,
            exhausted.Retry.Status);
        Assert.Equal(3, exhausted.Retry.Entry.Attempt);
        Assert.Equal(0, fixture.Retries.Count);
        Assert.Equal(0, fixture.Coordinator.TrackedCount);
        Assert.Equal(3, fixture.Runtime.SpawnCalls);
    }

    [Fact]
    public async Task CancellationAfterSpawnPreservesPendingTombstoneAndReleasesOwnership()
    {
        var fixture = new Fixture();
        fixture.AdmitCurrent();
        using var cancellation = new CancellationTokenSource();

        var processing = fixture.Worker.TryProcessNextAsync(
            DateTimeOffset.UtcNow,
            cancellation.Token);
        await fixture.Dispatcher.OperationCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var removal = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                fixture.ObjectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;
        Assert.True(fixture.Coordinator.TryAdmit(
            fixture.Session,
            fixture.ObjectId));
        cancellation.Cancel();

        var cancelled = await processing;

        Assert.Equal(LocalObjectReconcileStatus.Cancelled, cancelled.Outcome.Status);
        Assert.False(cancelled.Retry.Scheduled);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.Equal(1, fixture.Ledger.OwnedCount);

        var removed = await fixture.Worker.TryProcessNextAsync(DateTimeOffset.UtcNow);

        Assert.True(removed.Outcome.Succeeded);
        Assert.Equal(1, fixture.Runtime.SpawnCalls);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Equal(0, fixture.Retries.Count);
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            fixture.ObjectId,
            out var completed));
        Assert.Equal(removal.Token, completed.Token);
        Assert.True(completed.Completed);
    }

    [Fact]
    public async Task CleanupDuringLateFailureCannotRecreateRetryState()
    {
        var fixture = new Fixture
        {
            Runtime = { SpawnStatus = ApplyPortStatus.TransientFailure },
        };
        fixture.AdmitCurrent();

        var processing = fixture.Worker.TryProcessNextAsync(DateTimeOffset.UtcNow);
        await fixture.Dispatcher.OperationCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var cleanup = new ReceiverSessionCleanup(
            fixture.Authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: fixture.Coordinator,
            localObjectRetries: fixture.Retries,
            localObjectStore: fixture.Store);

        cleanup.Cleanup(fixture.Session);
        await cleanup.CleanupCompletion;
        var ownershipCleanup = await fixture.OwnershipCleanup.CleanupPassAsync(
            fixture.Session,
            maximumEntries: 1);
        fixture.Dispatcher.Release.TrySetResult();

        var result = await processing;

        Assert.True(result.Processed);
        Assert.True(result.Outcome.Retryable);
        Assert.False(result.Retry.Scheduled);
        Assert.False(fixture.Authority.IsCurrent(fixture.Session));
        Assert.False(fixture.Coordinator.HasSessionWork(fixture.Session));
        Assert.False(fixture.Store.HasSessionEntries(fixture.Session));
        Assert.Equal(0, fixture.Retries.Count);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Equal(1, ownershipCleanup.Released);
        Assert.False(ownershipCleanup.HasRemaining);
    }

    private sealed class Fixture
    {
        public Fixture(
            bool blockAfterFirstOperation = true,
            LocalObjectApplyRetryPolicy? retryPolicy = null)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            ObjectId = new RscObjectId(Guid.NewGuid());
            Store = new DesiredLocalObjectStore(Authority, maximumObjects: 2);
            Current = Store.Advance(
                Session,
                Furniture(ObjectId, revision: 1),
                "furniture:indoor:6015").Current!;
            Coordinator = new LocalObjectApplyCoordinator(Store, maximumTrackedObjects: 2);
            Retries = new LocalObjectApplyRetryQueue(
                maximumObjects: 2,
                retryPolicy);
            Ledger = new LocalObjectOwnershipLedger(maximumObjects: 2);
            Dispatcher = new AfterOperationDispatcher(blockAfterFirstOperation);
            Runtime = new TestRuntime();
            Readiness = new TestReadiness();
            OwnershipCleanup = new LocalObjectOwnershipCleanup(
                Ledger,
                Dispatcher,
                Runtime);
            Worker = new LocalObjectApplyWorker(
                Store,
                Coordinator,
                new LocalObjectReconciler(
                    Store,
                    Ledger,
                    Dispatcher,
                    Runtime,
                    Readiness),
                new LocalObjectRemovalExecutor(Store, OwnershipCleanup),
                Retries,
                Readiness);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public RscObjectId ObjectId { get; }
        public DesiredLocalObjectStore Store { get; }
        public DesiredLocalObjectState Current { get; }
        public LocalObjectApplyCoordinator Coordinator { get; }
        public LocalObjectApplyRetryQueue Retries { get; }
        public LocalObjectOwnershipLedger Ledger { get; }
        public AfterOperationDispatcher Dispatcher { get; }
        public TestRuntime Runtime { get; }
        public TestReadiness Readiness { get; }
        public LocalObjectOwnershipCleanup OwnershipCleanup { get; }
        public LocalObjectApplyWorker Worker { get; }

        public void AdmitCurrent() => Assert.True(
            Coordinator.TryAdmit(Session, Current.ObjectId));
    }

    private sealed class TestReadiness : IReceiverReadinessPort
    {
        public bool IsReady { get; set; } = true;
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

    private sealed class AfterOperationDispatcher : IGameThreadDispatcher
    {
        private int blockNext;

        public AfterOperationDispatcher(bool blockNext)
        {
            this.blockNext = blockNext ? 1 : 0;
        }

        public TaskCompletionSource OperationCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = operation();
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                OperationCompleted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class TestRuntime : ILocalObjectRuntimePort
    {
        public ApplyPortStatus SpawnStatus { get; set; } = ApplyPortStatus.Succeeded;
        public int SpawnCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request)
        {
            SpawnCalls++;
            if (SpawnStatus != ApplyPortStatus.Succeeded)
                return new(SpawnStatus, Error: "spawn failed");

            return new(
                ApplyPortStatus.Succeeded,
                new LocalObjectInstanceHandle(
                    Guid.NewGuid(),
                    request.Token.ObjectId,
                    request.Identity.Kind,
                    request.SpawnGeneration));
        }

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) =>
            new(ApplyPortStatus.Succeeded);

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            ReleaseCalls++;
            return new(ApplyPortStatus.Succeeded);
        }
    }

    private static FurnitureState Furniture(RscObjectId objectId, long revision) => new(
        objectId,
        new ObjectRevision(revision),
        new RscUserId("creator"),
        null,
        "furniture:indoor:6000",
        new Vector3State(1, 2, 3),
        new RotationState(0, 0, 0, 1));
}
