using System.Collections;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class DesiredLocalObjectStoreTests
{
    [Fact]
    public void MaterialAdvanceIncrementsGenerationWhileRevisionOnlyAdvanceDoesNot()
    {
        var fixture = new Fixture(1);
        var objectId = ObjectId();
        var first = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, "furniture:indoor:6000", 1),
            "fingerprint-a");
        var revisionOnly = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 2, "furniture:indoor:6000", 1),
            "fingerprint-a");
        var material = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 3, "furniture:indoor:6000", 9),
            "fingerprint-b");

        Assert.Equal(DesiredLocalObjectAdvanceStatus.Advanced, first.Status);
        Assert.Equal(DesiredLocalObjectAdvanceStatus.RevisionAdvanced, revisionOnly.Status);
        Assert.Equal(first.Current!.Token, revisionOnly.Current!.Token);
        Assert.Equal(DesiredLocalObjectAdvanceStatus.Advanced, material.Status);
        Assert.Equal(new ApplyGeneration(2), material.Current!.ApplyGeneration);
        Assert.False(fixture.Store.IsCurrent(first.Current.Token));
        Assert.True(fixture.Store.IsCurrent(material.Current.Token));
    }

    [Fact]
    public void SameRevisionConflictAndObjectKindChangeAreRejected()
    {
        var fixture = new Fixture(1);
        var objectId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, "furniture:indoor:6000", 1),
            "fingerprint-a").Accepted);

        var conflict = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, "furniture:indoor:6002", 1),
            "fingerprint-b");
        var kindChange = fixture.Store.Advance(
            fixture.Session,
            Light(objectId, 2, "point", 1),
            "fingerprint-c");

        Assert.Equal(DesiredLocalObjectAdvanceStatus.RevisionConflict, conflict.Status);
        Assert.Equal(DesiredLocalObjectAdvanceStatus.ObjectKindConflict, kindChange.Status);
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, objectId, out var retained));
        Assert.IsType<FurnitureState>(retained.State);
    }

    [Fact]
    public void TombstoneFencesApplyAndCarriesNextGeneration()
    {
        var fixture = new Fixture(1);
        var objectId = ObjectId();
        var desired = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, "furniture:indoor:6000", 1),
            "fingerprint").Current!;

        var removal = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                objectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2)));

        Assert.True(removal.Accepted);
        Assert.Equal(new ApplyGeneration(2), removal.Current!.ApplyGeneration);
        Assert.False(fixture.Store.IsCurrent(desired.Token));
        Assert.True(fixture.Store.IsCurrent(removal.Current.Token));
        Assert.True(fixture.Store.TryMarkRemovalCompleted(removal.Current.Token));
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            objectId,
            out var completed));
        Assert.True(completed.Completed);
    }

    [Fact]
    public void TombstoneRejectsSameSessionLiveResurrection()
    {
        var fixture = new Fixture(1);
        var objectId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 1, "furniture:indoor:6000", 1),
            "original").Accepted);
        var removal = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(
                objectId,
                RscObjectKind.Furniture,
                new ObjectRevision(2))).Current!;

        var incremental = fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 3, "furniture:indoor:6002", 9),
            "resurrection");
        var snapshot = fixture.Store.ApplySnapshot(
            fixture.Session,
            new[]
            {
                new DesiredLocalObjectSnapshotState(
                    Furniture(objectId, 3, "furniture:indoor:6002", 9),
                    "resurrection"),
            },
            Array.Empty<ObjectTombstone>());

        Assert.Equal(DesiredLocalObjectAdvanceStatus.StaleRevision, incremental.Status);
        Assert.Equal(DesiredLocalObjectSnapshotStatus.RevisionConflict, snapshot.Status);
        Assert.False(fixture.Store.TryGetCurrent(fixture.Session, objectId, out _));
        Assert.True(fixture.Store.TryGetRemoval(
            fixture.Session,
            objectId,
            out var retained));
        Assert.Equal(removal.Token, retained.Token);
    }

    [Fact]
    public void TombstoneMustMatchExistingKindAndAdvanceRevision()
    {
        var fixture = new Fixture(1);
        var objectId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(objectId, 2, "furniture:indoor:6000", 1),
            "fingerprint").Accepted);

        var wrongKind = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(objectId, RscObjectKind.Light, new ObjectRevision(3)));
        var stale = fixture.Store.AdvanceRemoval(
            fixture.Session,
            new ObjectTombstone(objectId, RscObjectKind.Furniture, new ObjectRevision(2)));

        Assert.Equal(DesiredLocalObjectRemovalStatus.ObjectKindConflict, wrongKind.Status);
        Assert.Equal(DesiredLocalObjectRemovalStatus.StaleRevision, stale.Status);
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, objectId, out _));
    }

    [Fact]
    public void CapacityCountsLiveAndTombstonedObjects()
    {
        var fixture = new Fixture(1);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(ObjectId(), 1, "furniture:indoor:6000", 1),
            "fingerprint").Accepted);

        Assert.Equal(
            DesiredLocalObjectAdvanceStatus.CapacityExceeded,
            fixture.Store.Advance(
                fixture.Session,
                Light(ObjectId(), 1, "point", 1),
                "light").Status);
        Assert.Equal(
            DesiredLocalObjectRemovalStatus.CapacityExceeded,
            fixture.Store.AdvanceRemoval(
                fixture.Session,
                new ObjectTombstone(
                    ObjectId(),
                    RscObjectKind.Light,
                    new ObjectRevision(1))).Status);
    }

    [Fact]
    public void SnapshotRejectsAbsenceAsImplicitDeletionWithoutPartialMutation()
    {
        var fixture = new Fixture(2);
        var firstId = ObjectId();
        var secondId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(firstId, 1, "furniture:indoor:6000", 1),
            "first").Accepted);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Light(secondId, 1, "point", 1),
            "second").Accepted);

        var result = fixture.Store.ApplySnapshot(
            fixture.Session,
            new[]
            {
                new DesiredLocalObjectSnapshotState(
                    Furniture(firstId, 2, "furniture:indoor:6002", 9),
                    "changed"),
            },
            Array.Empty<ObjectTombstone>());

        Assert.Equal(DesiredLocalObjectSnapshotStatus.IncompleteSnapshot, result.Status);
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, firstId, out var retained));
        Assert.Equal(new ObjectRevision(1), retained.AuthoritativeRevision);
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, secondId, out _));
    }

    [Fact]
    public void SnapshotAtomicallyPlansLiveAndRemovalWork()
    {
        var fixture = new Fixture(2);
        var furnitureId = ObjectId();
        var lightId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(furnitureId, 1, "furniture:indoor:6000", 1),
            "old").Accepted);

        var result = fixture.Store.ApplySnapshot(
            fixture.Session,
            new[]
            {
                new DesiredLocalObjectSnapshotState(
                    Light(lightId, 1, "point", 1),
                    "light"),
            },
            new[]
            {
                new ObjectTombstone(
                    furnitureId,
                    RscObjectKind.Furniture,
                    new ObjectRevision(2)),
            });

        Assert.True(result.Applied);
        Assert.Equal(2, result.Work.Count);
        Assert.Contains(result.Work, work => work.IsRemoval && work.Token.ObjectId == furnitureId);
        Assert.Contains(result.Work, work => !work.IsRemoval && work.Token.ObjectId == lightId);
        Assert.False(fixture.Store.TryGetCurrent(fixture.Session, furnitureId, out _));
        Assert.True(fixture.Store.TryGetRemoval(fixture.Session, furnitureId, out _));
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, lightId, out _));
    }

    [Fact]
    public void SnapshotKindConflictDoesNotPartiallyAdvanceOtherObject()
    {
        var fixture = new Fixture(2);
        var firstId = ObjectId();
        var secondId = ObjectId();
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(firstId, 1, "furniture:indoor:6000", 1),
            "first").Accepted);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Light(secondId, 1, "point", 1),
            "second").Accepted);

        var result = fixture.Store.ApplySnapshot(
            fixture.Session,
            new[]
            {
                new DesiredLocalObjectSnapshotState(
                    Furniture(firstId, 2, "furniture:indoor:6002", 9),
                    "changed"),
                new DesiredLocalObjectSnapshotState(
                    Furniture(secondId, 2, "furniture:indoor:6000", 1),
                    "wrong-kind"),
            },
            Array.Empty<ObjectTombstone>());

        Assert.Equal(DesiredLocalObjectSnapshotStatus.ObjectKindConflict, result.Status);
        Assert.True(fixture.Store.TryGetCurrent(fixture.Session, firstId, out var retained));
        Assert.Equal(new ObjectRevision(1), retained.AuthoritativeRevision);
    }

    [Fact]
    public void RemoveSessionIsBoundedAndExact()
    {
        var fixture = new Fixture(3);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(ObjectId(), 1, "furniture:indoor:6000", 1),
            "first").Accepted);
        Assert.True(fixture.Store.Advance(
            fixture.Session,
            Furniture(ObjectId(), 1, "furniture:indoor:6002", 1),
            "second").Accepted);
        Assert.True(fixture.Authority.Invalidate(fixture.Session));
        Assert.True(fixture.Authority.TryActivate(newSession));
        Assert.True(fixture.Store.Advance(
            newSession,
            Light(ObjectId(), 1, "point", 1),
            "new").Accepted);

        Assert.Equal(1, fixture.Store.RemoveSession(fixture.Session, 1));
        Assert.True(fixture.Store.HasSessionEntries(fixture.Session));
        Assert.Equal(1, fixture.Store.RemoveSession(fixture.Session, 1));
        Assert.False(fixture.Store.HasSessionEntries(fixture.Session));
        Assert.True(fixture.Store.HasSessionEntries(newSession));
        Assert.Equal(1, fixture.Store.Count);
    }

    [Fact]
    public async Task AuthorityInvalidationWaitsForFinalDesiredFence()
    {
        var fixture = new Fixture(1);
        var state = fixture.Store.Advance(
            fixture.Session,
            Furniture(ObjectId(), 1, "furniture:indoor:6000", 1),
            "fingerprint").Current!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var guarded = Task.Run(() => fixture.Store.TryExecuteCurrent(
            state.Token,
            () =>
            {
                entered.Set();
                release.Wait();
                return true;
            },
            out _));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var invalidation = Task.Run(() => fixture.Authority.Invalidate(fixture.Session));
        Assert.NotSame(
            invalidation,
            await Task.WhenAny(invalidation, Task.Delay(TimeSpan.FromMilliseconds(100))));
        release.Set();

        Assert.True(await guarded);
        Assert.True(await invalidation);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidSnapshotCountRejectsBeforeEnumeratingCallerCollections(int reportedCount)
    {
        var fixture = new Fixture(1);
        var states = new NonEnumerableReadOnlyList<DesiredLocalObjectSnapshotState>(reportedCount);

        var result = fixture.Store.ApplySnapshot(fixture.Session, states, new ObjectTombstone[1]);

        Assert.Equal(DesiredLocalObjectSnapshotStatus.Invalid, result.Status);
        Assert.False(states.EnumerationAttempted);
    }

    [Fact]
    public void SnapshotReportedCountCannotHideAdditionalTombstones()
    {
        var fixture = new Fixture(4);
        var tombstone = new ObjectTombstone(ObjectId(), RscObjectKind.Furniture, new ObjectRevision(1));
        var tombstones = new OverlongReadOnlyList<ObjectTombstone>(reportedCount: 1, [tombstone, tombstone]);

        var result = fixture.Store.ApplySnapshot(
            fixture.Session,
            Array.Empty<DesiredLocalObjectSnapshotState>(),
            tombstones);

        Assert.Equal(DesiredLocalObjectSnapshotStatus.Invalid, result.Status);
        Assert.Equal(0, fixture.Store.Count);
        Assert.Equal(2, tombstones.EnumeratedItems);
    }

    private sealed class NonEnumerableReadOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count { get; } = count;
        public bool EnumerationAttempted { get; private set; }
        public T this[int index] => throw new InvalidOperationException(
            "An invalid collection count must reject before indexing.");

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempted = true;
            throw new InvalidOperationException(
                "An invalid collection count must reject before enumeration.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OverlongReadOnlyList<T>(
        int reportedCount,
        IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Count { get; } = reportedCount;
        public int EnumeratedItems { get; private set; }
        public T this[int index] => throw new InvalidOperationException(
            "Bounded snapshots must use one normalized enumeration.");

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in items)
            {
                EnumeratedItems++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        public Fixture(int maximumObjects)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            Store = new DesiredLocalObjectStore(Authority, maximumObjects);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore Store { get; }
    }

    private static RscObjectId ObjectId() => new(Guid.NewGuid());

    private static FurnitureState Furniture(
        RscObjectId objectId,
        long revision,
        string identity,
        float x) => new(
            objectId,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity,
            new Vector3State(x, 2, 3),
            new RotationState(0, 0, 0, 1));

    private static LightState Light(
        RscObjectId objectId,
        long revision,
        string identity,
        float scale) => new(
            objectId,
            new ObjectRevision(revision),
            new RscUserId("creator"),
            null,
            identity == "point" ? LightIdentity.Point : identity,
            new Vector3State(1, 2, 3),
            new Vector3State(scale, scale, scale));
}
