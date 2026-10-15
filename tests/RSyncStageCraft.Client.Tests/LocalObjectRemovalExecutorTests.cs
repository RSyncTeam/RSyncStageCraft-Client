using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectRemovalExecutorTests
{
    [Fact]
    public async Task StaleReleaseCompletionCannotCompleteNewerTombstone()
    {
        var fixture = new Fixture(blockDispatch: true);
        var firstRemoval = fixture.Remove(revision: 2);

        var removing = fixture.Executor.RemoveAsync(firstRemoval);
        await fixture.Dispatcher.Entered.Task;
        var newer = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                fixture.ObjectId,
                RscObjectKind.Furniture,
                new ObjectRevision(3))).Current!;
        fixture.Dispatcher.Release.TrySetResult();

        var stale = await removing;

        Assert.Equal(LocalObjectReconcileStatus.Stale, stale.Status);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            fixture.ObjectId,
            out var retained));
        Assert.Equal(newer.Token, retained.Token);
        Assert.False(retained.Completed);

        var completed = await fixture.Executor.RemoveAsync(newer);

        Assert.True(completed.Succeeded);
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            fixture.ObjectId,
            out retained));
        Assert.True(retained.Completed);
        Assert.Equal(1, fixture.Runtime.ReleaseCalls);
    }

    [Fact]
    public async Task FailedExactReleaseRemainsOwnedForRetry()
    {
        var fixture = new Fixture();
        var removal = fixture.Remove(revision: 2);
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var failed = await fixture.Executor.RemoveAsync(removal);

        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.Status);
        Assert.Equal(1, fixture.Ledger.Count);
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            fixture.ObjectId,
            out var retained));
        Assert.False(retained.Completed);

        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await fixture.Executor.RemoveAsync(removal);

        Assert.True(retried.Succeeded);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
    }

    [Fact]
    public async Task NewerTombstoneDuringFailedReleaseOverridesObsoleteRetry()
    {
        var fixture = new Fixture(blockDispatch: true);
        var removal = fixture.Remove(revision: 2);
        fixture.Runtime.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var removing = fixture.Executor.RemoveAsync(removal);
        await fixture.Dispatcher.Entered.Task;
        var newer = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                fixture.ObjectId,
                RscObjectKind.Furniture,
                new ObjectRevision(3))).Current!;
        fixture.Dispatcher.Release.TrySetResult();

        Assert.Equal(LocalObjectReconcileStatus.Stale, (await removing).Status);
        Assert.Equal(1, fixture.Ledger.Count);
        Assert.False(newer.Completed);

        fixture.Runtime.ReleaseStatus = ApplyPortStatus.Succeeded;
        Assert.True((await fixture.Executor.RemoveAsync(newer)).Succeeded);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
    }

    [Fact]
    public async Task NewerTombstoneDuringThrowingReleaseOverridesObsoleteRetry()
    {
        var fixture = new Fixture(blockDispatch: true);
        var removal = fixture.Remove(revision: 2);
        fixture.Runtime.ThrowOnRelease = true;

        var removing = fixture.Executor.RemoveAsync(removal);
        await fixture.Dispatcher.Entered.Task;
        var newer = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                fixture.ObjectId,
                RscObjectKind.Furniture,
                new ObjectRevision(3))).Current!;
        fixture.Dispatcher.Release.TrySetResult();

        Assert.Equal(LocalObjectReconcileStatus.Stale, (await removing).Status);
        Assert.Equal(1, fixture.Ledger.Count);
        Assert.False(newer.Completed);

        fixture.Runtime.ThrowOnRelease = false;
        Assert.True((await fixture.Executor.RemoveAsync(newer)).Succeeded);
        Assert.Equal(0, fixture.Ledger.Count);
        Assert.Equal(2, fixture.Runtime.ReleaseCalls);
    }

    [Fact]
    public async Task OldSessionRemovalCannotReleaseReplacementSessionOwnership()
    {
        var authority = new ReceiverApplyAuthority();
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        var objectId = new RscObjectId(Guid.NewGuid());
        Assert.True(authority.TryActivate(oldSession));
        var store = new DesiredLocalObjectStore(authority, 1);
        Assert.True(store.Advance(
            oldSession,
            Furniture(objectId, 1, "furniture:indoor:6000"),
            "old").Accepted);
        var staleRemoval = store.AdvanceRemoval(
            oldSession,
            new ObjectTombstone(
                objectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;
        Assert.True(authority.Invalidate(oldSession));
        Assert.True(authority.TryActivate(newSession));
        var replacement = store.Advance(
            newSession,
            Furniture(objectId, 1, "furniture:indoor:6002"),
            "new").Current!;
        var ledger = new LocalObjectOwnershipLedger(1);
        var identity = LocalObjectSpawnIdentity.FromState(replacement.State);
        var handle = new LocalObjectInstanceHandle(
            Guid.NewGuid(),
            objectId,
            RscObjectKind.Furniture,
            1);
        Assert.True(ledger.TryReserve(replacement.Token, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(replacement.Token, identity, handle));
        var runtime = new RemovalRuntime();
        var executor = new LocalObjectRemovalExecutor(
            store,
            new LocalObjectOwnershipCleanup(
                ledger,
                new RemovalDispatcher(block: false),
                runtime));

        var result = await executor.RemoveAsync(staleRemoval);

        Assert.Equal(LocalObjectReconcileStatus.Stale, result.Status);
        Assert.Equal(0, runtime.ReleaseCalls);
        Assert.True(ledger.TryGet(replacement.Token, out var retained));
        Assert.Equal(handle, retained.Handle);
    }

    private sealed class Fixture
    {
        public Fixture(bool blockDispatch = false)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            ObjectId = new RscObjectId(Guid.NewGuid());
            Store = new DesiredLocalObjectStore(Authority, 1);
            var desired = Store.Advance(
                Session,
                new FurnitureState(
                    ObjectId,
                    new ObjectRevision(1),
                    new RscUserId("creator"),
                    null,
                    "furniture:indoor:6000",
                    new Vector3State(1, 2, 3),
                    new RotationState(0, 0, 0, 1)),
                "fingerprint").Current!;
            Ledger = new LocalObjectOwnershipLedger(1);
            var identity = LocalObjectSpawnIdentity.FromState(desired.State);
            Assert.True(Ledger.TryReserve(desired.Token, identity).Accepted);
            Assert.True(Ledger.TryRecordSpawn(
                desired.Token,
                identity,
                new LocalObjectInstanceHandle(
                    Guid.NewGuid(),
                    ObjectId,
                    RscObjectKind.Furniture,
                    1)));
            Dispatcher = new RemovalDispatcher(blockDispatch);
            Runtime = new RemovalRuntime();
            var cleanup = new LocalObjectOwnershipCleanup(
                Ledger,
                Dispatcher,
                Runtime);
            Executor = new LocalObjectRemovalExecutor(Store, cleanup);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public RscObjectId ObjectId { get; }
        public DesiredLocalObjectStore Store { get; }
        public LocalObjectOwnershipLedger Ledger { get; }
        public RemovalDispatcher Dispatcher { get; }
        public RemovalRuntime Runtime { get; }
        public LocalObjectRemovalExecutor Executor { get; }

        public DesiredLocalObjectRemoval Remove(long revision) =>
            Store.AdvanceRemoval(
                Session,
                new ObjectTombstone(
                    ObjectId,
                    RscObjectKind.Furniture,
                    new ObjectRevision(revision))).Current!;
    }

    private sealed class RemovalDispatcher : IGameThreadDispatcher
    {
        private readonly bool block;

        public RemovalDispatcher(bool block)
        {
            this.block = block;
            if (!block)
                Release.TrySetResult();
        }

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (block)
                await Release.Task.WaitAsync(cancellationToken);
            return operation();
        }
    }

    private sealed class RemovalRuntime : ILocalObjectRuntimePort
    {
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public bool ThrowOnRelease { get; set; }
        public int ReleaseCalls { get; private set; }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) => throw new NotSupportedException();
        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) => throw new NotSupportedException();
        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            ReleaseCalls++;
            if (ThrowOnRelease)
                throw new InvalidOperationException("local release failed");

            return new(ReleaseStatus);
        }
    }

    private static FurnitureState Furniture(
        RscObjectId objectId,
        long revision,
        string identity) => new(
        objectId,
        new ObjectRevision(revision),
        new RscUserId("creator"),
        null,
        identity,
        new Vector3State(1, 2, 3),
        new RotationState(0, 0, 0, 1));
}
