using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

/// <summary>Protocol 7: the desired shared humans (the fourth apply domain) and the Release host.</summary>
public sealed class HumanActorDesiredSetTests
{
    private static readonly ReceiverSessionKey SessionA = new(1, 1, 1);
    private static readonly ReceiverSessionKey SessionB = new(1, 2, 1);
    private static readonly RscUserId PlayerA = new("player-a");
    private static readonly string Bundle = new('a', 64);

    private static HumanActorState Human(Guid? id = null, long revision = 1, string? bundle = null, RscUserId? owner = null, float x = 1) =>
        new(new RscObjectId(id ?? Guid.NewGuid()), new ObjectRevision(revision), owner ?? PlayerA, null, bundle ?? Bundle,
            new Vector3State(x, 0, 0), 0);

    [Fact]
    public void AdvanceAcceptsNewerRevisionsAndRefusesStaleOnesAndAnyBundleOrOwnerChange()
    {
        var set = new HumanActorDesiredSet();
        var id = Guid.NewGuid();
        Assert.Equal(HumanActorDesiredStatus.Accepted, set.Advance(SessionA, Human(id)));
        var first = set.Read();
        Assert.Equal(HumanActorDesiredStatus.Unchanged, set.Advance(SessionA, Human(id)));
        Assert.Equal(first.Version, set.Read().Version);
        Assert.Equal(HumanActorDesiredStatus.Accepted, set.Advance(SessionA, Human(id, 2, x: 2)));
        Assert.True(set.Read().Version > first.Version);
        Assert.Equal(HumanActorDesiredStatus.StaleRevision, set.Advance(SessionA, Human(id, 1)));
        Assert.Equal(HumanActorDesiredStatus.Invalid, set.Advance(SessionA, Human(id, 3, bundle: new string('b', 64))));
        Assert.Equal(HumanActorDesiredStatus.Invalid, set.Advance(SessionA, Human(id, 3, owner: new RscUserId("player-b"))));
        Assert.Equal(2f, Assert.Single(set.Read().Humans).Position.X);
        Assert.Equal(HumanActorDesiredStatus.Invalid, set.Advance(SessionA, Human(bundle: "A" + new string('a', 63))));
        Assert.Equal(HumanActorDesiredStatus.Invalid, set.Advance(default, Human()));
    }

    [Fact]
    public void RemoveNeedsAHumanTombstoneNewerThanTheHeldRevision()
    {
        var set = new HumanActorDesiredSet();
        var id = new RscObjectId(Guid.NewGuid());
        set.Advance(SessionA, Human(id.Value, 2));
        Assert.Equal(HumanActorDesiredStatus.Invalid, set.Remove(SessionA, new ObjectTombstone(id, RscObjectKind.Actor, new(3))));
        Assert.Equal(HumanActorDesiredStatus.StaleRevision, set.Remove(SessionA, new ObjectTombstone(id, RscObjectKind.Human, new(2))));
        Assert.Equal(HumanActorDesiredStatus.Accepted, set.Remove(SessionA, new ObjectTombstone(id, RscObjectKind.Human, new(3))));
        Assert.Empty(set.Read().Humans);
        Assert.Equal(HumanActorDesiredStatus.Unchanged, set.Remove(SessionA, new ObjectTombstone(id, RscObjectKind.Human, new(4))));
    }

    [Fact]
    public void ASnapshotReplacesTheSetAndASuccessorSessionInheritsNothing()
    {
        var set = new HumanActorDesiredSet();
        set.Advance(SessionA, Human());
        var kept = Human();
        set.ApplySnapshot(SessionA, [kept]);
        Assert.Equal(kept, Assert.Single(set.Read().Humans));
        Assert.Equal(SessionA, set.Read().Session);
        // A successor session's first change clears what the earlier one held.
        Assert.Equal(HumanActorDesiredStatus.Accepted, set.Advance(SessionB, Human()));
        Assert.DoesNotContain(kept, set.Read().Humans);
        Assert.Single(set.Read().Humans);
        Assert.Throws<ArgumentException>(() => set.ApplySnapshot(SessionB, [Human(bundle: "not-a-bundle")]));
        Assert.False(HumanActorDesiredSet.CanApplySnapshot(HumanActorDesiredSet.MaximumHumans + 1));
    }

    [Fact]
    public void SessionCleanupClearsOnlyItsOwnSession()
    {
        var set = new HumanActorDesiredSet();
        set.Advance(SessionA, Human());
        Assert.False(set.ClearSession(SessionB));
        Assert.Single(set.Read().Humans);
        var before = set.Read().Version;
        Assert.True(set.ClearSession(SessionA));
        Assert.Empty(set.Read().Humans);
        Assert.Null(set.Read().Session);
        Assert.True(set.Read().Version > before);
    }

    [Fact]
    public void TheReadIsInObjectIdOrderAndACopy()
    {
        var set = new HumanActorDesiredSet();
        var high = new Guid("ffffffff-0000-4000-8000-000000000000");
        var low = new Guid("00000001-0000-4000-8000-000000000000");
        set.Advance(SessionA, Human(high));
        set.Advance(SessionA, Human(low));
        var view = set.Read();
        Assert.Equal(new[] { low, high }, view.Humans.Select(human => human.ObjectId.Value));
        set.ClearSession(SessionA);
        Assert.Equal(2, view.Humans.Count);
    }

    [Fact]
    public void TheReleaseHostDrawsNothingAndReportsEachDesiredHumanOnce()
    {
        var lines = new List<string>();
        var host = new UnavailableHumanActorHost(lines.Add);
        var set = new HumanActorDesiredSet();
        var human = Human();
        set.Advance(SessionA, human);
        host.Tick(set.Read());
        host.Tick(set.Read());
        Assert.Equal(0, host.LiveCount);
        var line = Assert.Single(lines);
        Assert.Contains(human.ObjectId.Value.ToString("N")[..8], line);
        Assert.Contains(UnavailableHumanActorHost.NotDrawn, line);
        Assert.DoesNotContain(Bundle, line);
        Assert.Equal(1, host.NotDrawnCount);
        set.ClearSession(SessionA);
        host.Tick(set.Read());
        Assert.Equal(0, host.NotDrawnCount);
        // Shared again: reported again.
        set.Advance(SessionA, human);
        host.Tick(set.Read());
        Assert.Equal(2, lines.Count);
        host.Shutdown(() => TimeSpan.Zero);
        Assert.Equal(0, host.NotDrawnCount);
    }
}
