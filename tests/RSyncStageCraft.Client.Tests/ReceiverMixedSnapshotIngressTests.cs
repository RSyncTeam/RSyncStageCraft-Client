using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverMixedSnapshotIngressTests
{
    [Fact]
    public void ActorFurnitureAndLightShareOneBoundedMailbox()
    {
        var fixture = new Fixture(
            maximumLocalObjects: 3,
            maximumTrackedLocalObjects: 1);
        var actor = Actor(ObjectId(), 1);
        var furniture = Furniture(ObjectId(), 1, "furniture:indoor:6000");
        var light = Light(ObjectId(), 1, "point");
        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(10),
            new AuthoritativeObjectState[] { actor, furniture, light },
            Array.Empty<ObjectTombstone>());

        var staged = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Status);
        Assert.Equal(3, staged.RemainingAdmissions);
        Assert.Equal(3, fixture.LocalStore.Count);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));

        var firstPass = fixture.Snapshots.AdmitPass(fixture.Session, 1);
        Assert.Equal(ReceiverActorSnapshotStatus.Staged, firstPass.Status);
        Assert.Equal(2, firstPass.RemainingAdmissions);
        Assert.Equal(1, fixture.LocalCoordinator.QueuedCount);

        var blocked = fixture.Snapshots.AdmitPass(fixture.Session, 1);
        Assert.Equal(ReceiverActorSnapshotStatus.AdmissionBackpressure, blocked.Status);
        Assert.Equal(2, blocked.RemainingAdmissions);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));
        DrainLocal(fixture.LocalCoordinator);

        var secondPass = fixture.Snapshots.AdmitPass(fixture.Session, 1);
        Assert.Equal(ReceiverActorSnapshotStatus.Staged, secondPass.Status);
        Assert.Equal(1, secondPass.RemainingAdmissions);
        DrainLocal(fixture.LocalCoordinator);

        var completed = fixture.Snapshots.AdmitPass(fixture.Session, 1);

        Assert.Equal(ReceiverActorSnapshotStatus.Completed, completed.Status);
        Assert.Equal(0, completed.RemainingAdmissions);
        Assert.Equal(new ServerSequence(10), fixture.Events.CurrentSequence);
        Assert.True(fixture.Events.IsInitializedForSession(fixture.Session));
        Assert.Equal(1, fixture.LocalCoordinator.QueuedCount);
    }

    [Fact]
    public void AnOverCapacitySnapshotCommitsNothing()
    {
        var fixture = new Fixture(
            maximumLocalObjects: 2,
            maximumTrackedLocalObjects: 1);
        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(5),
            new AuthoritativeObjectState[]
            {
                Actor(ObjectId(), 1),
                Furniture(ObjectId(), 1, "furniture:indoor:6000"),
                Light(ObjectId(), 1, "point"),
            },
            Array.Empty<ObjectTombstone>());

        var rejected = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.StoreRejected, rejected.Status);
        Assert.Equal(DesiredLocalObjectSnapshotStatus.Invalid, rejected.LocalStoreStatus);
        Assert.Equal(0, fixture.LocalStore.Count);
        Assert.Equal(0, fixture.Snapshots.PendingCount);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));
    }

    [Fact]
    public void MixedSnapshotStagesLocalTombstoneAtNextGeneration()
    {
        var fixture = new Fixture(2, 1);
        var furnitureId = ObjectId();
        Assert.True(fixture.LocalStore.Advance(
            fixture.Session,
            Furniture(furnitureId, 1, "furniture:indoor:6000"),
            "existing").Accepted);
        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(7),
            new AuthoritativeObjectState[] { Actor(ObjectId(), 1) },
            new[]
            {
                new ObjectTombstone(
                    furnitureId,
                    RscObjectKind.Furniture,
                    new ObjectRevision(2)),
            });

        var staged = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Status);
        Assert.True(fixture.LocalStore.TryGetRemoval(
            fixture.Session,
            furnitureId,
            out var removal));
        Assert.Equal(new ApplyGeneration(2), removal.ApplyGeneration);
        Assert.False(fixture.LocalStore.TryGetCurrent(fixture.Session, furnitureId, out _));
    }

    private static void DrainLocal(LocalObjectApplyCoordinator coordinator)
    {
        Assert.True(coordinator.TryBeginNext(out var work));
        Assert.True(coordinator.Complete(work.Token));
    }

    private sealed class Fixture
    {
        public Fixture(
            int maximumLocalObjects,
            int maximumTrackedLocalObjects)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            LocalStore = new DesiredLocalObjectStore(Authority, maximumLocalObjects);
            LocalCoordinator = new LocalObjectApplyCoordinator(
                LocalStore,
                maximumTrackedLocalObjects);
            var localIngress = new ReceiverLocalObjectIngress(
                LocalStore,
                LocalCoordinator);
            Events = new ReceiverActorEventIngress(
                Authority,
                localObjectIngress: localIngress);
            Snapshots = new ReceiverActorSnapshotIngress(
                Authority,
                Events,
                localObjectStore: LocalStore,
                localObjectCoordinator: LocalCoordinator);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore LocalStore { get; }
        public LocalObjectApplyCoordinator LocalCoordinator { get; }
        public ReceiverActorEventIngress Events { get; }
        public ReceiverActorSnapshotIngress Snapshots { get; }
    }

    private static RscObjectId ObjectId() => new(Guid.NewGuid());

    private static ActorState Actor(RscObjectId id, long revision) => new(
        id,
        new ObjectRevision(revision),
        new RscUserId("owner"),
        null,
        4242,
        new Vector3State(1, 2, 3),
        0,
        1,
        ActorControlState.Default);

    private static FurnitureState Furniture(
        RscObjectId id,
        long revision,
        string identity) => new(
            id,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity,
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1));

    private static LightState Light(
        RscObjectId id,
        long revision,
        string identity) => new(
            id,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity == "point" ? LightIdentity.Point : identity,
            new Vector3State(1, 2, 3),
            new Vector3State(1, 1, 1));
}
