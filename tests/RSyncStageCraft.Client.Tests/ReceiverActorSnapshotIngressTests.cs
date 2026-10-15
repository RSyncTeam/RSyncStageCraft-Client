using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverActorSnapshotIngressTests
{
    [Fact]
    public void SnapshotCommitsAtomicallyBeforeBoundedMailboxAdmissionCompletes()
    {
        var fixture = new Fixture(maximumActors: 3, maximumTrackedActors: 1);
        var actors = Enumerable.Range(0, 3)
            .Select(index => Actor(new(Guid.NewGuid()), index + 1))
            .ToArray();
        var snapshot = Snapshot(new ServerSequence(10), actors);

        var staged = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Status);
        Assert.Equal(3, staged.RemainingAdmissions);
        Assert.Equal(3, fixture.Store.Count);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));

        var firstPass = fixture.Snapshots.AdmitPass(fixture.Session, maximumEntries: 3);
        Assert.Equal(ReceiverActorSnapshotStatus.AdmissionBackpressure, firstPass.Status);
        Assert.Equal(2, firstPass.RemainingAdmissions);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.Equal(
            ReceiverActorIngressStatus.NotInitialized,
            fixture.Events.AcceptUpdated(
                fixture.Session,
                new ObjectUpdatedMessage(
                    new ServerSequence(11),
                    actors[0] with { Revision = new ObjectRevision(2) })).Status);

        DrainOne(fixture.Coordinator);
        var secondPass = fixture.Snapshots.AdmitPass(fixture.Session, maximumEntries: 3);
        Assert.Equal(ReceiverActorSnapshotStatus.AdmissionBackpressure, secondPass.Status);
        Assert.Equal(1, secondPass.RemainingAdmissions);
        DrainOne(fixture.Coordinator);

        var completed = fixture.Snapshots.AdmitPass(fixture.Session, maximumEntries: 3);

        Assert.Equal(ReceiverActorSnapshotStatus.Completed, completed.Status);
        Assert.Equal(0, completed.RemainingAdmissions);
        Assert.Equal(new ServerSequence(10), fixture.Events.CurrentSequence);
        Assert.True(fixture.Events.IsInitializedForSession(fixture.Session));
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
    }

    [Fact]
    public void AdmissionPassHonorsExplicitPerPassBound()
    {
        var fixture = new Fixture(maximumActors: 3, maximumTrackedActors: 3);
        var snapshot = Snapshot(
            new ServerSequence(5),
            Enumerable.Range(0, 3)
                .Select(index => Actor(new(Guid.NewGuid()), index + 1))
                .ToArray());
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            fixture.Snapshots.Stage(fixture.Session, snapshot).Status);

        var pass = fixture.Snapshots.AdmitPass(fixture.Session, maximumEntries: 1);

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, pass.Status);
        Assert.Equal(2, pass.RemainingAdmissions);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));
    }

    [Fact]
    public void EmptySnapshotInitializesIncrementalCursorImmediately()
    {
        var fixture = new Fixture(maximumActors: 1);
        var snapshot = new ObjectSnapshotMessage(
            new ServerSequence(12),
            Array.Empty<AuthoritativeObjectState>(),
            Array.Empty<ObjectTombstone>());

        var completed = fixture.Snapshots.Stage(fixture.Session, snapshot);
        var replay = fixture.Snapshots.Stage(fixture.Session, snapshot);

        Assert.Equal(ReceiverActorSnapshotStatus.Completed, completed.Status);
        Assert.Equal(ReceiverActorSnapshotStatus.AlreadyCompleted, replay.Status);
        Assert.Equal(new ServerSequence(12), fixture.Events.CurrentSequence);
        Assert.Equal(0, fixture.Store.Count);
    }

    [Fact]
    public void SnapshotPayloadIsMaterializedExactlyOnceBeforeCommit()
    {
        var fixture = new Fixture(maximumActors: 1);
        var objects = new SingleEnumerationList<AuthoritativeObjectState>(
            Actor(new RscObjectId(Guid.NewGuid()), revision: 1));
        var tombstones = new SingleEnumerationList<ObjectTombstone>();

        var staged = fixture.Snapshots.Stage(
            fixture.Session,
            new ObjectSnapshotMessage(
                new ServerSequence(1),
                objects,
                tombstones));

        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Status);
        Assert.Equal(1, objects.EnumerationCount);
        Assert.Equal(1, tombstones.EnumerationCount);
        Assert.Equal(1, fixture.Store.Count);
    }

    [Fact]
    public void InactiveSnapshotDoesNotEnumerateCallerCollections()
    {
        var fixture = new Fixture(maximumActors: 1);
        var objects = new SingleEnumerationList<AuthoritativeObjectState>(
            Actor(new RscObjectId(Guid.NewGuid()), revision: 1));
        var tombstones = new SingleEnumerationList<ObjectTombstone>();
        Assert.True(fixture.Authority.Invalidate(fixture.Session));

        var rejected = fixture.Snapshots.Stage(
            fixture.Session,
            new ObjectSnapshotMessage(
                new ServerSequence(1),
                objects,
                tombstones));

        Assert.Equal(ReceiverActorSnapshotStatus.InactiveSession, rejected.Status);
        Assert.Equal(0, objects.EnumerationCount);
        Assert.Equal(0, tombstones.EnumerationCount);
    }

    [Fact]
    public async Task SessionCleanupClearsPendingSnapshotAdmissions()
    {
        var fixture = new Fixture(maximumActors: 2, maximumTrackedActors: 1);
        var snapshot = Snapshot(
            new ServerSequence(4),
            new[]
            {
                Actor(new(Guid.NewGuid()), 1),
                Actor(new(Guid.NewGuid()), 1),
            });
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            fixture.Snapshots.Stage(fixture.Session, snapshot).Status);
        var retries = new LocalObjectApplyRetryQueue(2);
        var cleanup = new ReceiverSessionCleanup(
            fixture.Authority,
            fixture.Events,
            fixture.Snapshots,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: fixture.Coordinator,
            localObjectRetries: retries,
            localObjectStore: fixture.Store);

        cleanup.Cleanup(fixture.Session);
        await cleanup.CleanupCompletion;

        Assert.Equal(0, fixture.Snapshots.PendingCount);
        Assert.Equal(0, fixture.Store.Count);
        Assert.False(fixture.Events.IsInitializedForSession(fixture.Session));
        Assert.Equal(
            ReceiverActorSnapshotStatus.InactiveSession,
            fixture.Snapshots.AdmitPass(fixture.Session, 1).Status);
    }

    private static void DrainOne(LocalObjectApplyCoordinator coordinator)
    {
        Assert.True(coordinator.TryBeginNext(out var work));
        Assert.True(coordinator.Complete(work.Token));
    }

    private sealed class Fixture
    {
        public Fixture(int maximumActors, int? maximumTrackedActors = null)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            Store = new DesiredLocalObjectStore(Authority, maximumActors);
            Coordinator = new LocalObjectApplyCoordinator(
                Store,
                maximumTrackedActors ?? maximumActors);
            Events = new ReceiverActorEventIngress(
                Authority,
                new ReceiverLocalObjectIngress(Store, Coordinator));
            Snapshots = new ReceiverActorSnapshotIngress(
                Authority,
                Events,
                Store,
                Coordinator);
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore Store { get; }
        public LocalObjectApplyCoordinator Coordinator { get; }
        public ReceiverActorEventIngress Events { get; }
        public ReceiverActorSnapshotIngress Snapshots { get; }
    }

    private sealed class SingleEnumerationList<T>(params T[] items) : IReadOnlyList<T>
    {
        private int enumerationCount;

        public int EnumerationCount => Volatile.Read(ref enumerationCount);
        public int Count => items.Length;
        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            if (Interlocked.Increment(ref enumerationCount) != 1)
                throw new InvalidOperationException("Snapshot input was enumerated more than once.");
            return items.AsEnumerable().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static ObjectSnapshotMessage Snapshot(
        ServerSequence watermark,
        IReadOnlyList<ActorState> actors) => new(
        watermark,
        actors.Cast<AuthoritativeObjectState>().ToArray(),
        Array.Empty<ObjectTombstone>());

    private static ActorState Actor(RscObjectId actorId, long revision) => new(
        actorId,
        new ObjectRevision(revision),
        new RscUserId("owner"),
        null,
        4242,
        new Vector3State(1, 2, 3),
        0,
        1,
        ActorControlState.Default);
}
