using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverApplyCleanupTests
{
    [Fact]
    public async Task InvalidatesAndDrainsCoreStateBeforeReleasingOwnership()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 4);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 4);
        var objectId = new RscObjectId(Guid.NewGuid());
        Assert.True(store.Advance(session, Furniture(objectId), "desired").Accepted);
        Assert.True(coordinator.TryAdmit(session, objectId));

        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 4);
        AddLocalObject(ledger, session, objectId);
        var ports = new CleanupPorts(() =>
        {
            Assert.False(authority.IsCurrent(session));
            Assert.False(store.HasSessionEntries(session));
            Assert.False(coordinator.HasSessionWork(session));
        });
        var cleanup = CreateCleanup(authority, store, coordinator, ledger, ports, maximumPerPass: 1);

        var result = await cleanup.CleanupAsync(session);

        Assert.Equal(new ReceiverApplyCleanupResult(1, 0, false, false), result);
        Assert.Equal(new[] { "local.release" }, ports.Calls);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task LargeCleanupConvergesThroughBoundedYieldingPasses()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 8);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 8);
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 8);

        for (var index = 0; index < 5; index++)
        {
            var objectId = new RscObjectId(Guid.NewGuid());
            Assert.True(store.Advance(session, Furniture(objectId, index + 1), $"desired-{index}").Accepted);
            Assert.True(coordinator.TryAdmit(session, objectId));
            AddLocalObject(ledger, session, objectId);
        }

        var ports = new CleanupPorts();
        var cleanup = CreateCleanup(authority, store, coordinator, ledger, ports, maximumPerPass: 2);

        var result = await cleanup.CleanupAsync(session);

        Assert.Equal(new ReceiverApplyCleanupResult(5, 0, false, false), result);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, coordinator.TrackedCount);
        Assert.Equal(0, ledger.Count);
        Assert.Equal(5, ports.Calls.Count);
    }

    [Fact]
    public async Task FailedFrontBatchDoesNotStrandLaterReleasableOwnership()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 6);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 6);
        var ledger = new LocalObjectOwnershipLedger(maximumObjects: 6);
        var ports = new CleanupPorts();

        for (var index = 0; index < 3; index++)
        {
            var objectId = new RscObjectId(Guid.NewGuid());
            AddLocalObject(ledger, session, objectId);
            if (index < 2)
                ports.FailingObjects.Add(objectId);
        }

        var cleanup = CreateCleanup(authority, store, coordinator, ledger, ports, maximumPerPass: 2);

        var result = await cleanup.CleanupAsync(session);

        Assert.Equal(new ReceiverApplyCleanupResult(1, 2, true, false), result);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(3, ports.Calls.Count(call => call == "local.release"));
    }

    private static ReceiverApplyCleanup CreateCleanup(
        ReceiverApplyAuthority authority,
        DesiredLocalObjectStore store,
        LocalObjectApplyCoordinator coordinator,
        LocalObjectOwnershipLedger ledger,
        CleanupPorts ports,
        int maximumPerPass)
    {
        var sessionCleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumPerPass,
            localObjectCoordinator: coordinator,
            localObjectStore: store);
        return new ReceiverApplyCleanup(
            sessionCleanup,
            maximumPerPass,
            new LocalObjectOwnershipCleanup(ledger, ports, ports));
    }

    private sealed class CleanupPorts :
        IGameThreadDispatcher,
        ILocalObjectRuntimePort
    {
        private readonly Action beforeRelease;
        private bool inDispatcher;

        public CleanupPorts(Action beforeRelease = null)
        {
            this.beforeRelease = beforeRelease ?? (() => { });
        }

        public List<string> Calls { get; } = new();
        public HashSet<RscObjectId> FailingObjects { get; } = new();

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inDispatcher = true;
            try
            {
                return Task.FromResult(operation());
            }
            finally
            {
                inDispatcher = false;
            }
        }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request) => throw new NotSupportedException();

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState) => throw new NotSupportedException();

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            Assert.True(inDispatcher);
            beforeRelease();
            Calls.Add("local.release");
            return FailingObjects.Contains(handle.ObjectId)
                ? new(ApplyPortStatus.TransientFailure)
                : new(ApplyPortStatus.Succeeded);
        }
    }

    private static void AddLocalObject(
        LocalObjectOwnershipLedger ledger,
        ReceiverSessionKey session,
        RscObjectId objectId)
    {
        var token = new LocalObjectApplyToken(
            session,
            objectId,
            new ApplyGeneration(1));
        var identity = new LocalObjectSpawnIdentity(
            RscObjectKind.Furniture,
            "furniture:indoor:6000");
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(
            token,
            identity,
            new LocalObjectInstanceHandle(
                Guid.NewGuid(),
                objectId,
                RscObjectKind.Furniture,
                1)));
    }

    private static FurnitureState Furniture(RscObjectId id, long revision = 1) => new(
        id,
        new ObjectRevision(revision),
        new RscUserId("creator"),
        null,
        "furniture:indoor:6000",
        new Vector3State(1, 2, 3),
        new RotationState(0, 0, 0, 1));
}
