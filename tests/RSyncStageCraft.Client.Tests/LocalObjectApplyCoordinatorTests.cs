using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectApplyCoordinatorTests
{
    [Fact]
    public void NewGenerationCoalescesBehindActiveAttempt()
    {
        var fixture = new Fixture(maximumTracked: 1);
        var objectId = ObjectId();
        var first = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, 1),
            "first").Current!;
        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, objectId));
        Assert.True(fixture.Coordinator.TryBeginNext(out var active));

        var second = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 2, 9),
            "second").Current!;
        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, objectId));

        Assert.Equal(1, fixture.Coordinator.ActiveCount);
        Assert.Equal(0, fixture.Coordinator.QueuedCount);
        Assert.True(fixture.Coordinator.Complete(active.Token));
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.True(fixture.Coordinator.TryBeginNext(out var pending));
        Assert.Equal(second.Token, pending.Token);
        Assert.NotEqual(first.Token, pending.Token);
    }

    [Fact]
    public void TombstoneReplacesPendingApplyAtSameMailboxIdentity()
    {
        var fixture = new Fixture(maximumTracked: 1);
        var objectId = ObjectId();
        var desired = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, 1),
            "desired").Current!;
        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, objectId));

        var removal = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                objectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;
        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, objectId));
        Assert.Equal(1, fixture.Coordinator.QueuedCount);

        Assert.True(fixture.Coordinator.TryBeginNext(out var work));
        Assert.True(work.IsRemoval);
        Assert.Equal(removal.Token, work.Token);
        Assert.NotEqual(desired.Token, work.Token);
    }

    [Fact]
    public void CapacityBackpressureIsBoundedByObjectIdentity()
    {
        var fixture = new Fixture(maximumTracked: 1, maximumObjects: 2);
        var firstId = ObjectId();
        var secondId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(firstId, 1, 1),
            "first").Accepted);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(secondId, 1, 1),
            "second").Accepted);

        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, firstId));
        Assert.False(fixture.Coordinator.TryAdmit(fixture.Session, secondId));
        Assert.Equal(1, fixture.Coordinator.TrackedCount);
    }

    [Fact]
    public void SessionCancellationIsBoundedAndExact()
    {
        var fixture = new Fixture(maximumTracked: 3, maximumObjects: 3);
        var oldIds = new[] { ObjectId(), ObjectId() };
        foreach (var objectId in oldIds)
        {
            Assert.True(fixture.Store.Advance(
                fixture.Session,
                Furniture(objectId, 1, 1),
                objectId.Value.ToString()).Accepted);
            Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, objectId));
        }

        Assert.Equal(1, fixture.Coordinator.CancelSession(fixture.Session, 1));
        Assert.True(fixture.Coordinator.HasSessionWork(fixture.Session));
        Assert.Equal(1, fixture.Coordinator.CancelSession(fixture.Session, 1));
        Assert.False(fixture.Coordinator.HasSessionWork(fixture.Session));
    }

    [Fact]
    public async Task StaleSweepPreservesConcurrentPendingGenerationWithinEntryBound()
    {
        var fixture = new Fixture(maximumTracked: 3, maximumObjects: 2);
        var staleObject = ObjectId();
        var otherObject = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(staleObject, 1, 1),
            "initial").Accepted);
        var staleRemoval = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                staleObject,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;
        Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, staleObject));
        Assert.True(fixture.Store.TryMarkRemovalCompleted(staleRemoval.Token));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.BeforeStaleReservationRemoval = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var sweep = Task.Run(() =>
        {
            var began = fixture.Coordinator.TryBeginNext(out var work);
            return (Began: began, Work: work);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = fixture.Store.AdvanceRemoval(
                fixture.Session,
                new ObjectTombstone(
                    staleObject,
                    RscObjectKind.Furniture,
                    new ObjectRevision(3))).Current!;
            Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, staleObject));
            var other = fixture.Store.Advance(
                fixture.Session,
                Furniture(otherObject, 1, 2),
                "other").Current!;
            Assert.True(fixture.Coordinator.TryAdmit(fixture.Session, otherObject));
            release.TrySetResult();

            var first = await sweep.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.Began);
            Assert.Equal(2, fixture.Coordinator.QueuedCount);
            Assert.True(fixture.Coordinator.TryBeginNext(out var next));
            Assert.Equal(other.Token, next.Token);
            Assert.True(fixture.Coordinator.TryBeginNext(out var pending));
            Assert.Equal(replacement.Token, pending.Token);
        }
        finally
        {
            release.TrySetResult();
            fixture.Coordinator.BeforeStaleReservationRemoval = null;
        }
    }

    private sealed class Fixture
    {
        public Fixture(int maximumTracked, int maximumObjects = 1)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            Store = new DesiredLocalObjectStore(Authority, maximumObjects);
            Coordinator = new LocalObjectApplyCoordinator(Store, maximumTracked);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore Store { get; }
        public LocalObjectApplyCoordinator Coordinator { get; }
    }

    private static RscObjectId ObjectId() => new(Guid.NewGuid());

    private static FurnitureState Furniture(
        RscObjectId objectId,
        long revision,
        float x) => new(
            objectId,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            "furniture:indoor:6000",
            new Vector3State(x, 2, 3),
            new RotationState(0, 0, 0, 1));
}
