using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverLocalObjectIngressTests
{
    [Fact]
    public void OneSequenceCursorRoutesActorFurnitureAndLightEvents()
    {
        var fixture = new Fixture(maximumTrackedLocalObjects: 3);
        Assert.True(fixture.Events.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var furniture = Furniture(ObjectId(), 1, "furniture:indoor:6000", 1);
        var actor = Actor(ObjectId(), 1);
        var lightId = ObjectId();
        var light = Light(lightId, 1, "point", 1);

        var furnitureResult = fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), furniture));
        var actorResult = fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(2), actor));
        var lightResult = fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(3), light));
        var lightRemoval = fixture.Events.AcceptDeleted(
            fixture.Session,
            new ObjectDeletedMessage(
                new ServerSequence(4),
                new ObjectTombstone(
                    lightId,
                    RscObjectKind.Light,
                    new ObjectRevision(2))));

        Assert.True(furnitureResult.Accepted);
        Assert.True(furnitureResult.LocalWorkToken.IsValid);
        Assert.True(actorResult.LocalWorkToken.IsValid);
        Assert.True(lightResult.LocalWorkToken.IsValid);
        Assert.True(lightRemoval.LocalWorkToken.IsValid);
        Assert.Equal(new ServerSequence(4), fixture.Events.CurrentSequence);
        Assert.Equal(3, fixture.LocalCoordinator.QueuedCount);
        Assert.True(fixture.LocalStore.TryGetRemoval(
            fixture.Session,
            lightId,
            out var removal));
        Assert.Equal(lightRemoval.LocalWorkToken, removal.Token);
    }

    [Fact]
    public void LocalBackpressureDoesNotConsumeSharedSequenceAndRetryConverges()
    {
        var fixture = new Fixture(maximumTrackedLocalObjects: 1);
        Assert.True(fixture.Events.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var first = Furniture(ObjectId(), 1, "furniture:indoor:6000", 1);
        var second = Light(ObjectId(), 1, "point", 1);
        Assert.True(fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), first)).Accepted);

        var blocked = fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(2), second));

        Assert.Equal(ReceiverActorIngressStatus.AdmissionBackpressure, blocked.Status);
        Assert.Equal(new ServerSequence(1), fixture.Events.CurrentSequence);
        Assert.True(fixture.LocalStore.TryGetCurrent(
            fixture.Session,
            second.ObjectId,
            out _));

        Assert.True(fixture.LocalCoordinator.TryBeginNext(out var work));
        Assert.True(fixture.LocalCoordinator.Complete(work.Token));
        var retried = fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(2), second));

        Assert.True(retried.Accepted);
        Assert.Equal(new ServerSequence(2), fixture.Events.CurrentSequence);
        Assert.Equal(1, fixture.LocalCoordinator.QueuedCount);
    }

    [Fact]
    public void RejectedLocalStateDoesNotConsumeSharedSequence()
    {
        var fixture = new Fixture(maximumTrackedLocalObjects: 1);
        Assert.True(fixture.Events.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var objectId = ObjectId();
        Assert.True(fixture.Events.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(
                new ServerSequence(1),
                Furniture(objectId, 1, "furniture:indoor:6000", 1))).Accepted);
        Assert.True(fixture.LocalCoordinator.TryBeginNext(out var work));
        Assert.True(fixture.LocalCoordinator.Complete(work.Token));

        var conflict = fixture.Events.AcceptUpdated(
            fixture.Session,
            new ObjectUpdatedMessage(
                new ServerSequence(2),
                Furniture(objectId, 1, "furniture:indoor:6002", 1)));

        Assert.Equal(ReceiverActorIngressStatus.StateRejected, conflict.Status);
        Assert.Equal(new ServerSequence(1), fixture.Events.CurrentSequence);
    }

    [Fact]
    public void FingerprintExcludesRevisionButIncludesSettledKindSpecificState()
    {
        var fingerprint = new Sha256LocalObjectStateFingerprint();
        var furniture = Furniture(ObjectId(), 1, "furniture:indoor:6000", 1);
        var revisionOnly = furniture with { Revision = new ObjectRevision(2) };
        var moved = furniture with { Position = new Vector3State(9, 2, 3) };
        var light = Light(ObjectId(), 1, "point", 1);
        // Protocol 5: colour, brightness and range are settled light state.
        var resized = light with { Range = 20f };
        var recoloured = light with { Color = new LightColor(1f, 0f, 0f) };
        var brighter = light with { Intensity = 4f };

        Assert.Equal(fingerprint.Compute(furniture), fingerprint.Compute(revisionOnly));
        Assert.NotEqual(fingerprint.Compute(furniture), fingerprint.Compute(moved));
        Assert.NotEqual(fingerprint.Compute(light), fingerprint.Compute(resized));
        Assert.NotEqual(fingerprint.Compute(light), fingerprint.Compute(recoloured));
        Assert.NotEqual(fingerprint.Compute(light), fingerprint.Compute(brighter));
        Assert.Equal(fingerprint.Compute(light), fingerprint.Compute(light with { Revision = new ObjectRevision(5) }));
    }

    private sealed class Fixture
    {
        public Fixture(int maximumTrackedLocalObjects)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            LocalStore = new DesiredLocalObjectStore(Authority, 4);
            LocalCoordinator = new LocalObjectApplyCoordinator(
                LocalStore,
                maximumTrackedLocalObjects);
            LocalIngress = new ReceiverLocalObjectIngress(
                LocalStore,
                LocalCoordinator);
            Events = new ReceiverActorEventIngress(
                Authority,
                localObjectIngress: LocalIngress);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore LocalStore { get; }
        public LocalObjectApplyCoordinator LocalCoordinator { get; }
        public ReceiverLocalObjectIngress LocalIngress { get; }
        public ReceiverActorEventIngress Events { get; }
    }

    private static RscObjectId ObjectId() => new(Guid.NewGuid());

    private static FurnitureState Furniture(
        RscObjectId id,
        long revision,
        string identity,
        float x) => new(
            id,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity,
            new Vector3State(x, 2, 3),
            new RotationState(0, 0, 0, 1));

    private static LightState Light(
        RscObjectId id,
        long revision,
        string identity,
        float scale) => new(
            id,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity == "point" ? LightIdentity.Point : identity,
            new Vector3State(1, 2, 3),
            new Vector3State(scale, scale, scale));

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
}
