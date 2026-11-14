using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Protocol 7: the receiver's ordered intake routes shared humans (events and
/// snapshots) into the human domain's desired set only. They share the one
/// sequence cursor with every other kind, carry no local-object work and
/// never reach the local-object store.
/// </summary>
public sealed class HumanActorRoutingTests
{
    private static readonly RscUserId PlayerA = new("player-a");
    private static readonly string Bundle = new('a', 64);

    internal static HumanActorState Human(RscObjectId id, long revision, float x = 1) =>
        new(id, new ObjectRevision(revision), PlayerA, null, Bundle, new Vector3State(x, 2, 3), 0.5f);

    [Fact]
    public void HumanEventsAdvanceTheSharedCursorAndReachOnlyTheHumanSet()
    {
        var fixture = new RoutingFixture();
        Assert.True(fixture.Events.InitializeFromSnapshotWatermark(fixture.Session, new ServerSequence(0)));
        var id = new RscObjectId(Guid.NewGuid());
        var furniture = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), PlayerA, null,
            "furniture:indoor:6000", new Vector3State(1, 2, 3), new RotationState(0, 0, 0, 1));

        var created = fixture.Events.AcceptCreated(fixture.Session, new ObjectCreatedMessage(new ServerSequence(1), Human(id, 1)));
        var other = fixture.Events.AcceptCreated(fixture.Session, new ObjectCreatedMessage(new ServerSequence(2), furniture));
        Assert.True(created.Accepted);
        Assert.False(created.LocalWorkToken.IsValid);
        Assert.True(other.LocalWorkToken.IsValid);
        Assert.Equal(1, fixture.LocalStore.Count);
        Assert.Equal(Human(id, 1), Assert.Single(fixture.Humans.Read().Humans));
        Assert.Equal(fixture.Session, fixture.Humans.Read().Session);

        var moved = fixture.Events.AcceptUpdated(fixture.Session, new ObjectUpdatedMessage(new ServerSequence(3), Human(id, 2, x: 7)));
        Assert.True(moved.Accepted);
        Assert.Equal(7f, Assert.Single(fixture.Humans.Read().Humans).Position.X);

        // A state naming another bundle for the same human is refused and does not consume the sequence.
        var swapped = fixture.Events.AcceptUpdated(fixture.Session,
            new ObjectUpdatedMessage(new ServerSequence(4), Human(id, 3) with { AppearanceBundleId = new string('b', 64) }));
        Assert.False(swapped.Accepted);
        Assert.Equal(new ServerSequence(3), fixture.Events.CurrentSequence);

        var deleted = fixture.Events.AcceptDeleted(fixture.Session,
            new ObjectDeletedMessage(new ServerSequence(4), new ObjectTombstone(id, RscObjectKind.Human, new ObjectRevision(3))));
        Assert.True(deleted.Accepted);
        Assert.False(deleted.LocalWorkToken.IsValid);
        Assert.Empty(fixture.Humans.Read().Humans);
        Assert.Equal(new ServerSequence(4), fixture.Events.CurrentSequence);
        Assert.Equal(1, fixture.LocalStore.Count);
        Assert.False(fixture.LocalStore.TryGetCurrent(fixture.Session, id, out _));
    }

    [Fact]
    public void ASnapshotsHumansReplaceTheHumanSetAndOnlyItsOtherKindsReachTheLocalStore()
    {
        var fixture = new RoutingFixture();
        var stale = new RscObjectId(Guid.NewGuid());
        fixture.Humans.Advance(fixture.Session, Human(stale, 1));
        var id = new RscObjectId(Guid.NewGuid());
        var furniture = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), PlayerA, null,
            "furniture:indoor:6000", new Vector3State(1, 2, 3), new RotationState(0, 0, 0, 1));
        var snapshot = new ObjectSnapshotMessage(new ServerSequence(10), new AuthoritativeObjectState[] { Human(id, 4), furniture },
            Array.Empty<ObjectTombstone>());

        var staged = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Status);
        Assert.Equal(1, fixture.LocalStore.Count);
        Assert.Equal(Human(id, 4), Assert.Single(fixture.Humans.Read().Humans));
        Assert.False(fixture.LocalStore.TryGetCurrent(fixture.Session, id, out _));
        Assert.DoesNotContain(fixture.Humans.Read().Humans, human => human.ObjectId == stale);
    }

    [Fact]
    public void WithoutAHumanSetHumanEventsAreAcceptedAndDrawnNowhere()
    {
        // A host composed without the human domain still follows the ordered stream.
        var fixture = new RoutingFixture(withHumans: false);
        Assert.True(fixture.Events.InitializeFromSnapshotWatermark(fixture.Session, new ServerSequence(0)));
        var created = fixture.Events.AcceptCreated(fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), Human(new RscObjectId(Guid.NewGuid()), 1)));
        Assert.True(created.Accepted);
        Assert.Equal(new ServerSequence(1), fixture.Events.CurrentSequence);
        Assert.Equal(0, fixture.LocalStore.Count);
    }
}

/// <summary>One synthetic client's receiver intake: authority, local-object store, human set, event and snapshot ingress.</summary>
internal sealed class RoutingFixture
{
    public RoutingFixture(bool withHumans = true, int maximumLocalObjects = 4)
    {
        Authority = new ReceiverApplyAuthority();
        Session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(Authority.TryActivate(Session));
        LocalStore = new DesiredLocalObjectStore(Authority, maximumLocalObjects);
        LocalCoordinator = new LocalObjectApplyCoordinator(LocalStore, maximumLocalObjects);
        Humans = new HumanActorDesiredSet();
        var humans = withHumans ? Humans : null;
        var local = new ReceiverLocalObjectIngress(LocalStore, LocalCoordinator, humanActors: humans);
        Events = new ReceiverActorEventIngress(Authority, localObjectIngress: local);
        Snapshots = new ReceiverActorSnapshotIngress(Authority, Events, localObjectStore: LocalStore,
            localObjectCoordinator: LocalCoordinator, humanActors: humans);
    }

    public ReceiverApplyAuthority Authority { get; }
    public ReceiverSessionKey Session { get; }
    public DesiredLocalObjectStore LocalStore { get; }
    public LocalObjectApplyCoordinator LocalCoordinator { get; }
    public HumanActorDesiredSet Humans { get; }
    public ReceiverActorEventIngress Events { get; }
    public ReceiverActorSnapshotIngress Snapshots { get; }
}
