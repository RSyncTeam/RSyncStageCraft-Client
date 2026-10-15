using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverActorEventIngressTests
{
    [Fact]
    public void RequiresSnapshotInitializationAndExactNextSequence()
    {
        var fixture = new Fixture(maximumActors: 2);
        var actor = Actor(new(Guid.NewGuid()), revision: 1);

        Assert.Equal(
            ReceiverActorIngressStatus.NotInitialized,
            fixture.Ingress.AcceptCreated(
                fixture.Session,
                new ObjectCreatedMessage(new ServerSequence(1), actor)).Status);
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));

        var accepted = fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), actor));

        Assert.True(accepted.Accepted);
        Assert.Equal(new ServerSequence(1), accepted.CurrentSequence);
        Assert.True(accepted.LocalWorkToken.IsValid);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
        Assert.Equal(
            ReceiverActorIngressStatus.StaleSequence,
            fixture.Ingress.AcceptCreated(
                fixture.Session,
                new ObjectCreatedMessage(new ServerSequence(1), actor)).Status);
        Assert.Equal(
            ReceiverActorIngressStatus.SequenceGap,
            fixture.Ingress.AcceptUpdated(
                fixture.Session,
                new ObjectUpdatedMessage(
                    new ServerSequence(3),
                    actor with { Revision = new ObjectRevision(2) })).Status);
        Assert.Equal(new ServerSequence(1), fixture.Ingress.CurrentSequence);
    }

    [Fact]
    public void AdmissionBackpressureDoesNotConsumeServerSequence()
    {
        var fixture = new Fixture(maximumActors: 2, maximumTrackedActors: 1);
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var firstActor = Actor(new(Guid.NewGuid()), revision: 1);
        var secondActor = Actor(new(Guid.NewGuid()), revision: 1);
        Assert.True(fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), firstActor)).Accepted);

        var backpressure = fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(2), secondActor));

        Assert.Equal(ReceiverActorIngressStatus.AdmissionBackpressure, backpressure.Status);
        Assert.Equal(new ServerSequence(1), fixture.Ingress.CurrentSequence);
        Assert.True(fixture.Store.TryGetCurrent(
            fixture.Session,
            secondActor.ObjectId,
            out _));

        Assert.True(fixture.Coordinator.TryBeginNext(out var work));
        Assert.True(fixture.Coordinator.Complete(work.Token));
        var retried = fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(2), secondActor));

        Assert.True(retried.Accepted);
        Assert.Equal(new ServerSequence(2), fixture.Ingress.CurrentSequence);
        Assert.Equal(1, fixture.Coordinator.QueuedCount);
    }

    [Fact]
    public void TombstoneInvalidatesActiveApplyAndQueuesRemovalAtSameIdentity()
    {
        var fixture = new Fixture(maximumActors: 1);
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var actorId = new RscObjectId(Guid.NewGuid());
        Assert.True(fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(
                new ServerSequence(1),
                Actor(actorId, revision: 1))).Accepted);
        Assert.True(fixture.Coordinator.TryBeginNext(out var apply));

        var deleted = fixture.Ingress.AcceptDeleted(
            fixture.Session,
            new ObjectDeletedMessage(
                new ServerSequence(2),
                new ObjectTombstone(
                    actorId,
                    RscObjectKind.Actor,
                    new ObjectRevision(2))));

        Assert.True(deleted.Accepted);
        Assert.False(fixture.Store.IsCurrent(apply.Token));
        Assert.True(fixture.Store.IsCurrent(deleted.LocalWorkToken));
        Assert.True(fixture.Coordinator.Complete(apply.Token));
        Assert.True(fixture.Coordinator.TryBeginNext(out var removal));
        Assert.True(removal.IsRemoval);
        Assert.Equal(deleted.LocalWorkToken, removal.Token);
    }

    [Fact]
    public void RevisionOnlyActorEventRetainsApplyGeneration()
    {
        var fixture = new Fixture(maximumActors: 1);
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(0)));
        var actor = Actor(new(Guid.NewGuid()), revision: 1);
        var first = fixture.Ingress.AcceptCreated(
            fixture.Session,
            new ObjectCreatedMessage(new ServerSequence(1), actor));
        Assert.True(fixture.Coordinator.TryBeginNext(out var work));
        Assert.True(fixture.Coordinator.Complete(work.Token));

        var revisionOnly = fixture.Ingress.AcceptUpdated(
            fixture.Session,
            new ObjectUpdatedMessage(
                new ServerSequence(2),
                actor with { Revision = new ObjectRevision(2) }));

        Assert.True(revisionOnly.Accepted);
        Assert.Equal(first.LocalWorkToken, revisionOnly.LocalWorkToken);
        Assert.True(fixture.Store.TryGetCurrent(
            fixture.Session,
            actor.ObjectId,
            out var retained));
        Assert.Equal(new ObjectRevision(2), retained.AuthoritativeRevision);
        Assert.Equal(new ApplyGeneration(1), retained.ApplyGeneration);
    }

    [Fact]
    public void WithoutALocalObjectPipelineEventsStillAdvanceSharedServerSequence()
    {
        var fixture = new Fixture(maximumActors: 1);
        var ingress = new ReceiverActorEventIngress(fixture.Authority);
        Assert.True(ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(4)));
        var furniture = new FurnitureState(
            new RscObjectId(Guid.NewGuid()),
            new ObjectRevision(1),
            new RscUserId("creator"),
            null,
            "furniture:indoor:6000",
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1));

        var result = ingress.AcceptUpdated(
            fixture.Session,
            new ObjectUpdatedMessage(new ServerSequence(5), furniture));

        Assert.Equal(ReceiverActorIngressStatus.IgnoredUnsupportedKind, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(new ServerSequence(5), ingress.CurrentSequence);
        Assert.Equal(0, fixture.Store.Count);
        Assert.Equal(0, fixture.Coordinator.TrackedCount);
    }

    [Fact]
    public void SessionCursorCleanupIsExactAndOldEventsCannotMutateReconnect()
    {
        var fixture = new Fixture(maximumActors: 2);
        var oldSession = fixture.Session;
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            oldSession,
            new ServerSequence(10)));
        Assert.True(fixture.Authority.Invalidate(oldSession));
        var newSession = new ReceiverSessionKey(2, 1, 2);
        Assert.True(fixture.Authority.TryActivate(newSession));
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            newSession,
            new ServerSequence(20)));

        Assert.False(fixture.Ingress.ClearSession(oldSession));
        Assert.Equal(
            ReceiverActorIngressStatus.InactiveSession,
            fixture.Ingress.AcceptCreated(
                oldSession,
                new ObjectCreatedMessage(
                    new ServerSequence(11),
                    Actor(new(Guid.NewGuid()), revision: 1))).Status);
        Assert.Equal(new ServerSequence(20), fixture.Ingress.CurrentSequence);
        Assert.True(fixture.Ingress.ClearSession(newSession));
        Assert.Equal(default, fixture.Ingress.CurrentSequence);
    }

    [Fact]
    public async Task ReceiverSessionCleanupClearsSequenceCursorWithSessionState()
    {
        var fixture = new Fixture(maximumActors: 1);
        Assert.True(fixture.Ingress.InitializeFromSnapshotWatermark(
            fixture.Session,
            new ServerSequence(7)));
        var retries = new LocalObjectApplyRetryQueue(1);
        var cleanup = new ReceiverSessionCleanup(
            fixture.Authority,
            fixture.Ingress,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: fixture.Coordinator,
            localObjectRetries: retries,
            localObjectStore: fixture.Store);

        cleanup.Cleanup(fixture.Session);
        await cleanup.CleanupCompletion;

        Assert.Equal(default, fixture.Ingress.CurrentSequence);
        Assert.Equal(
            ReceiverActorIngressStatus.InactiveSession,
            fixture.Ingress.AcceptCreated(
                fixture.Session,
                new ObjectCreatedMessage(
                    new ServerSequence(8),
                    Actor(new(Guid.NewGuid()), revision: 1))).Status);
    }

    [Fact]
    public async Task SlowLocalFingerprintDoesNotHoldAuthorityOrPublishAfterInvalidation()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var localStore = new DesiredLocalObjectStore(authority, maximumObjects: 1);
        var localCoordinator = new LocalObjectApplyCoordinator(
            localStore,
            maximumTrackedObjects: 1);
        using var fingerprint = new BlockingLocalFingerprint();
        var localIngress = new ReceiverLocalObjectIngress(
            localStore,
            localCoordinator,
            fingerprint);
        var ingress = new ReceiverActorEventIngress(
            authority,
            localObjectIngress: localIngress);
        Assert.True(ingress.InitializeFromSnapshotWatermark(session, new ServerSequence(0)));
        var furniture = new FurnitureState(
            new RscObjectId(Guid.NewGuid()),
            new ObjectRevision(1),
            new RscUserId("owner"),
            null,
            "furniture:indoor:6000",
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1));

        var acceptance = Task.Run(() => ingress.AcceptCreated(
            session,
            new ObjectCreatedMessage(new ServerSequence(1), furniture)));
        try
        {
            Assert.True(fingerprint.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(await Task.Run(() => authority.Invalidate(session))
                .WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            fingerprint.Release.Set();
        }

        Assert.Equal(
            ReceiverActorIngressStatus.InactiveSession,
            (await acceptance.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, localStore.Count);
        Assert.Equal(0, localCoordinator.TrackedCount);
    }

    private sealed class Fixture
    {
        public Fixture(
            int maximumActors,
            int? maximumTrackedActors = null)
        {
            Authority = new ReceiverApplyAuthority();
            Session = new ReceiverSessionKey(1, 1, 1);
            Assert.True(Authority.TryActivate(Session));
            Store = new DesiredLocalObjectStore(Authority, maximumActors);
            Coordinator = new LocalObjectApplyCoordinator(
                Store,
                maximumTrackedActors ?? maximumActors);
            Ingress = new ReceiverActorEventIngress(
                Authority,
                localObjectIngress: new ReceiverLocalObjectIngress(Store, Coordinator));
        }

        public ReceiverApplyAuthority Authority { get; }
        public ReceiverSessionKey Session { get; }
        public DesiredLocalObjectStore Store { get; }
        public LocalObjectApplyCoordinator Coordinator { get; }
        public ReceiverActorEventIngress Ingress { get; }
    }

    private sealed class BlockingLocalFingerprint : ILocalObjectStateFingerprint, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public string Compute(AuthoritativeObjectState state)
        {
            Entered.Set();
            Release.Wait();
            return "blocked-local-fingerprint";
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

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
