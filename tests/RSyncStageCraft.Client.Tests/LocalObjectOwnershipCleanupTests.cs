using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectOwnershipCleanupTests
{
    [Fact]
    public async Task CleanupPassIsBoundedAndExactSessionScoped()
    {
        var ledger = new LocalObjectOwnershipLedger(3);
        var oldSession = Session(1);
        var newSession = Session(2);
        AddOwned(ledger, oldSession, ObjectId());
        AddOwned(ledger, oldSession, ObjectId());
        AddOwned(ledger, newSession, ObjectId());
        var dispatcher = new InlineDispatcher();
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);

        var first = await cleanup.CleanupPassAsync(oldSession, maximumEntries: 1);

        Assert.Equal(1, first.Released);
        Assert.Equal(0, first.Failed);
        Assert.True(first.HasRemaining);
        Assert.Equal(1, ledger.GetSessionEntryCount(oldSession));
        Assert.Equal(1, ledger.GetSessionEntryCount(newSession));
        Assert.Equal(1, runtime.ReleaseCalls);

        var second = await cleanup.CleanupPassAsync(oldSession, maximumEntries: 1);

        Assert.Equal(1, second.Released);
        Assert.False(second.HasRemaining);
        Assert.Equal(0, ledger.GetSessionEntryCount(oldSession));
        Assert.Equal(1, ledger.GetSessionEntryCount(newSession));
        Assert.Equal(2, dispatcher.Calls);
    }

    [Fact]
    public async Task FailedReleaseRemainsTrackedForExplicitRetry()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var objectId = ObjectId();
        var original = AddOwned(ledger, session, objectId);
        var runtime = new CleanupRuntime
        {
            ReleaseStatus = ApplyPortStatus.TransientFailure,
        };
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            runtime);

        var failed = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(0, failed.Released);
        Assert.Equal(1, failed.Failed);
        Assert.True(failed.HasRemaining);
        Assert.True(ledger.TryGet(original.Token, out var retained));
        Assert.Equal(original.Handle, retained.Handle);

        runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, retried.Released);
        Assert.Equal(0, retried.Failed);
        Assert.False(retried.HasRemaining);
        Assert.Equal(2, runtime.ReleaseCalls);
    }

    [Fact]
    public async Task CompensationIsReleasedAndCheckpointedBeforePrimary()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryRecordCompensation(reservation.Token, compensation));
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            runtime);

        var result = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, result.Released);
        Assert.False(result.HasRemaining);
        Assert.Equal(
            new[] { compensation, reservation.Handle!.Value },
            runtime.Released);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task FailedCompensationRetainsPrimaryAndStopsCleanupOrdering()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryRecordCompensation(reservation.Token, compensation));
        var runtime = new CleanupRuntime
        {
            ReleaseStatus = ApplyPortStatus.TransientFailure,
        };
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            runtime);

        var failed = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, failed.Failed);
        Assert.True(failed.HasRemaining);
        Assert.Equal(new[] { compensation }, runtime.Released);
        Assert.True(ledger.TryGet(reservation.Token, out var retained));
        Assert.Equal(compensation, retained.Compensation);
        Assert.Equal(reservation.Handle, retained.Handle);

        runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, retried.Released);
        Assert.Equal(
            new[] { compensation, compensation, reservation.Handle!.Value },
            runtime.Released);
        Assert.False(retried.HasRemaining);
    }

    [Fact]
    public async Task EmptyReservationIsRemovedWithoutGameThreadWork()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var token = Token(session, ObjectId());
        Assert.True(ledger.TryReserve(token, Furniture()).Accepted);
        var dispatcher = new InlineDispatcher();
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);

        var result = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, result.Released);
        Assert.False(result.HasRemaining);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(0, runtime.ReleaseCalls);
    }

    [Fact]
    public async Task ObjectCleanupReleasesOnlyRequestedOwnedObject()
    {
        var ledger = new LocalObjectOwnershipLedger(2);
        var session = Session(1);
        var target = ObjectId();
        var other = ObjectId();
        AddOwned(ledger, session, target);
        var otherReservation = AddOwned(ledger, session, other);
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            new CleanupRuntime());

        var result = await cleanup.CleanupObjectAsync(session, target);

        Assert.Equal(1, result.Released);
        Assert.False(result.HasRemaining);
        Assert.False(ledger.TryGetObject(session, target, out _));
        Assert.True(ledger.TryGet(otherReservation.Token, out _));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public async Task PreCancelledCleanupDoesNotClaimOrReleaseReceipt()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await cleanup.CleanupPassAsync(session, 1, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.HasRemaining);
        Assert.Equal(0, runtime.ReleaseCalls);
        Assert.True(ledger.TryGet(reservation.Token, out var retained));
        Assert.Equal(reservation.Handle, retained.Handle);
    }

    [Fact]
    public async Task CancellationAfterCompensationReleaseCannotRepeatIt()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryRecordCompensation(reservation.Token, compensation));
        var dispatcher = new InlineDispatcher
        {
            BlockAfterNextOperation = true,
        };
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);
        using var cancellation = new CancellationTokenSource();

        var pending = cleanup.CleanupPassAsync(session, 1, cancellation.Token);
        await dispatcher.OperationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await pending;

        Assert.True(cancelled.Cancelled);
        Assert.True(cancelled.HasRemaining);
        Assert.Equal(new[] { compensation }, runtime.Released);
        Assert.True(ledger.TryGet(reservation.Token, out var retained));
        Assert.Null(retained.Compensation);
        Assert.Equal(reservation.Handle, retained.Handle);

        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, retried.Released);
        Assert.False(retried.HasRemaining);
        Assert.Equal(
            new[] { compensation, reservation.Handle!.Value },
            runtime.Released);
    }

    [Fact]
    public async Task CancellationAfterPrimaryReleaseCannotRepeatIt()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var dispatcher = new InlineDispatcher
        {
            BlockAfterNextOperation = true,
        };
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);
        using var cancellation = new CancellationTokenSource();

        var pending = cleanup.CleanupPassAsync(session, 1, cancellation.Token);
        await dispatcher.OperationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await pending;

        Assert.True(cancelled.Cancelled);
        Assert.True(cancelled.HasRemaining);
        Assert.Equal(new[] { reservation.Handle!.Value }, runtime.Released);
        Assert.True(ledger.TryGet(reservation.Token, out var empty));
        Assert.Null(empty.Handle);

        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, retried.Released);
        Assert.False(retried.HasRemaining);
        Assert.Equal(new[] { reservation.Handle.Value }, runtime.Released);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task ConcurrentSessionAndObjectCleanupCannotDuplicateRelease()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var objectId = ObjectId();
        var reservation = AddOwned(ledger, session, objectId);
        var dispatcher = new InlineDispatcher
        {
            BlockBeforeNextOperation = true,
        };
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);

        var sessionCleanup = cleanup.CleanupPassAsync(session, 1);
        await dispatcher.OperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var objectCleanup = await cleanup.CleanupObjectAsync(session, objectId);

        Assert.Equal(
            new LocalObjectOwnershipCleanupResult(0, 1, true, false),
            objectCleanup);
        Assert.Equal(0, runtime.ReleaseCalls);

        dispatcher.ReleaseOperation.TrySetResult();
        Assert.Equal(
            new LocalObjectOwnershipCleanupResult(1, 0, false, false),
            await sessionCleanup);
        Assert.Equal(new[] { reservation.Handle!.Value }, runtime.Released);
        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task CancellationBeforeDispatchOperationReopensClaimForRetry()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var dispatcher = new InlineDispatcher
        {
            BlockBeforeNextOperation = true,
        };
        var runtime = new CleanupRuntime();
        var cleanup = new LocalObjectOwnershipCleanup(ledger, dispatcher, runtime);
        using var cancellation = new CancellationTokenSource();

        var pending = cleanup.CleanupPassAsync(session, 1, cancellation.Token);
        await dispatcher.OperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await pending;

        Assert.True(cancelled.Cancelled);
        Assert.True(cancelled.HasRemaining);
        Assert.Equal(0, runtime.ReleaseCalls);
        Assert.True(ledger.TryGet(reservation.Token, out var retained));
        Assert.Equal(reservation.Handle, retained.Handle);

        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(1, retried.Released);
        Assert.False(retried.HasRemaining);
        Assert.Equal(new[] { reservation.Handle!.Value }, runtime.Released);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task CompensationReleaseExceptionReopensClaimForExactRetry()
    {
        var ledger = new LocalObjectOwnershipLedger(1);
        var session = Session(1);
        var reservation = AddOwned(ledger, session, ObjectId());
        var compensation = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            ObjectId(),
            RscObjectKind.Light,
            99);
        Assert.True(ledger.TryRecordCompensation(reservation.Token, compensation));
        var runtime = new CleanupRuntime
        {
            ReleaseException = new InvalidOperationException("release fault"),
        };
        var cleanup = new LocalObjectOwnershipCleanup(
            ledger,
            new InlineDispatcher(),
            runtime);

        var failed = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(new LocalObjectOwnershipCleanupResult(0, 1, true, false), failed);
        Assert.True(ledger.TryGet(reservation.Token, out var retained));
        Assert.Equal(compensation, retained.Compensation);
        Assert.Equal(reservation.Handle, retained.Handle);

        runtime.ReleaseException = null;
        var retried = await cleanup.CleanupPassAsync(session, 1);

        Assert.Equal(new LocalObjectOwnershipCleanupResult(1, 0, false, false), retried);
        Assert.Equal(
            new[] { compensation, compensation, reservation.Handle!.Value },
            runtime.Released);
        Assert.Equal(0, ledger.Count);
    }

    private static LocalObjectReservation AddOwned(
        LocalObjectOwnershipLedger ledger,
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        var token = Token(session, objectId);
        var identity = Furniture();
        var handle = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            objectId,
            RscObjectKind.Furniture,
            1);
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(token, identity, handle));
        Assert.True(ledger.TryGet(token, out var reservation));
        return reservation;
    }

    private static ReceiverSessionKey Session(long epoch) => new(epoch, 1, epoch);
    private static RscObjectId ObjectId() => new(Guid.NewGuid());
    private static LocalObjectApplyToken Token(
        ReceiverSessionKey session,
        RscObjectId objectId) => new(session, objectId, new ApplyGeneration(1));
    private static LocalObjectSpawnIdentity Furniture() =>
        new(RscObjectKind.Furniture, "furniture:indoor:6000");

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public int Calls { get; private set; }
        public bool BlockBeforeNextOperation { get; set; }
        public bool BlockAfterNextOperation { get; set; }
        public TaskCompletionSource OperationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOperation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OperationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReturn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (BlockBeforeNextOperation)
            {
                BlockBeforeNextOperation = false;
                OperationEntered.TrySetResult();
                await ReleaseOperation.Task.WaitAsync(cancellationToken);
            }

            var result = operation();
            if (BlockAfterNextOperation)
            {
                BlockAfterNextOperation = false;
                OperationCompleted.TrySetResult();
                await ReleaseReturn.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class CleanupRuntime : ILocalObjectRuntimePort
    {
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public Exception ReleaseException { get; set; }
        public int ReleaseCalls { get; private set; }
        public List<LocalObjectInstanceHandle> Released { get; } = new();

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) => throw new NotSupportedException();

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) => throw new NotSupportedException();

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            ReleaseCalls++;
            Released.Add(handle);
            if (ReleaseException is not null)
                throw ReleaseException;

            return new(
                ReleaseStatus,
                ReleaseStatus == ApplyPortStatus.Succeeded ? null : "release failed");
        }
    }
}
