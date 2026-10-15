using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverLocalObjectRuntimeTests
{
    [Fact]
    public async Task SnapshotUpdateReplacementRemovalAndDisconnectConvergeEndToEnd()
    {
        var ports = new RuntimePorts();
        var runtime = Runtime(ports);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        var furnitureId = ObjectId();
        var lightId = ObjectId();
        var furniture = Furniture(furnitureId, 1, "furniture:indoor:6000", 1);
        var light = Light(lightId, 1, "point", 1);
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                new ObjectSnapshotMessage(
                    new ServerSequence(1),
                    new AuthoritativeObjectState[] { furniture, light },
                    Array.Empty<ObjectTombstone>())).Status);

        var initial = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);

        Assert.Equal(2, initial.LocalProcessed);
        Assert.All(initial.LocalWork!, work => Assert.True(work.Outcome.Succeeded));
        Assert.Equal(2, runtime.State.OwnedLocalObjects);
        Assert.Equal(2, ports.SpawnCalls);
        Assert.Equal(0, ports.ApplyCalls);

        var moved = runtime.AcceptUpdated(
            session,
            new ObjectUpdatedMessage(
                new ServerSequence(2),
                furniture with
                {
                    Revision = new ObjectRevision(2),
                    Position = new Vector3State(9, 2, 3),
                }));
        Assert.True(moved.Accepted);
        Assert.Equal(
            1,
            (await runtime.PumpAsync(session, DateTimeOffset.UtcNow)).LocalProcessed);
        Assert.Equal(1, ports.ApplyCalls);
        Assert.Equal(2, ports.SpawnCalls);

        var replacement = runtime.AcceptUpdated(
            session,
            new ObjectUpdatedMessage(
                new ServerSequence(3),
                furniture with
                {
                    Revision = new ObjectRevision(3),
                    FurnitureIdentity = "furniture:indoor:6002",
                }));
        Assert.True(replacement.Accepted);
        var replaced = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, replaced.LocalProcessed);
        Assert.Equal(3, ports.SpawnCalls);
        Assert.Equal(1, ports.ReleaseCalls);
        Assert.Contains("release:Furniture:1", ports.Events);
        Assert.Contains("spawn:Furniture:furniture:indoor:6002:2", ports.Events);

        var deleted = runtime.AcceptDeleted(
            session,
            new ObjectDeletedMessage(
                new ServerSequence(4),
                new ObjectTombstone(
                    furnitureId,
                    RscObjectKind.Furniture,
                    new ObjectRevision(4))));
        Assert.True(deleted.Accepted);
        var removed = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, removed.LocalProcessed);
        Assert.Equal(2, ports.ReleaseCalls);
        Assert.Equal(1, runtime.State.OwnedLocalObjects);
        Assert.True(runtime.LocalObjectStore!.TryGetRemoval(
            session,
            furnitureId,
            out var tombstone));
        Assert.True(tombstone.Completed);

        var cleanup = await runtime.DisconnectAsync(session);

        Assert.False(cleanup.HasRemainingOwnership);
        Assert.Equal(3, ports.ReleaseCalls);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.Equal(0, runtime.State.DesiredLocalObjects);
        Assert.Equal(0, runtime.State.TrackedLocalWork);
        Assert.Equal(0, runtime.State.DelayedLocalRetries);
        Assert.True(runtime.TryActivate(new ReceiverSessionKey(2, 1, 2)));
    }

    [Fact]
    public async Task TransientSpawnUsesBoundedSharedPumpRetry()
    {
        var ports = new RuntimePorts();
        ports.SpawnStatuses.Enqueue(ApplyPortStatus.TransientFailure);
        ports.SpawnStatuses.Enqueue(ApplyPortStatus.Succeeded);
        var runtime = Runtime(ports);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        var start = DateTimeOffset.UtcNow;
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(
                session,
                new ObjectSnapshotMessage(
                    new ServerSequence(1),
                    new AuthoritativeObjectState[]
                    {
                        Furniture(ObjectId(), 1, "furniture:indoor:6000", 1),
                    },
                    Array.Empty<ObjectTombstone>())).Status);

        var failed = await runtime.PumpAsync(session, start);

        Assert.Equal(1, failed.LocalProcessed);
        Assert.Equal(LocalObjectReconcileStatus.TransientFailure, failed.LocalWork![0].Outcome.Status);
        Assert.Equal(1, runtime.State.DelayedLocalRetries);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);

        var retried = await runtime.PumpAsync(session, start.AddMilliseconds(2));

        Assert.Equal(1, retried.PromotedLocalRetries);
        Assert.Equal(1, retried.LocalProcessed);
        Assert.True(retried.LocalWork![0].Outcome.Succeeded);
        Assert.Equal(0, runtime.State.DelayedLocalRetries);
        Assert.Equal(1, runtime.State.OwnedLocalObjects);
        Assert.Equal(2, ports.SpawnCalls);
    }

    [Fact]
    public async Task FailedLocalReleaseBlocksReconnectUntilExactRetrySucceeds()
    {
        var ports = new RuntimePorts();
        var runtime = Runtime(ports, maximumCleanupEntriesPerPass: 1);
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(oldSession));
        runtime.StageSnapshot(
            oldSession,
            new ObjectSnapshotMessage(
                new ServerSequence(1),
                new AuthoritativeObjectState[]
                {
                    Furniture(ObjectId(), 1, "furniture:indoor:6000", 1),
                },
                Array.Empty<ObjectTombstone>()));
        Assert.Equal(
            1,
            (await runtime.PumpAsync(oldSession, DateTimeOffset.UtcNow)).LocalProcessed);
        ports.ReleaseStatus = ApplyPortStatus.TransientFailure;

        var failed = await runtime.DisconnectAsync(oldSession);
        var newSession = new ReceiverSessionKey(2, 1, 2);

        Assert.True(failed.HasRemainingOwnership);
        Assert.Equal(1, runtime.LocalObjectLedger!.Count);
        Assert.False(runtime.TryActivate(newSession));

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retried = await runtime.RetryCleanupAsync(oldSession);

        Assert.False(retried.HasRemainingOwnership);
        Assert.Equal(0, runtime.LocalObjectLedger.Count);
        Assert.Equal(2, ports.ReleaseCalls);
        Assert.True(runtime.TryActivate(newSession));
    }

    [Fact]
    public async Task Protocol6ActorsTakeTheLocalPipeline()
    {
        // Actor states and events from the wire are local
        // objects (spawn identity: the BNpcBase row).
        var ports = new RuntimePorts();
        var runtime = Runtime(ports);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        var actor = new ActorState(ObjectId(), new ObjectRevision(1), new RscUserId("owner"), null, 4242,
            new Vector3State(1, 2, 3), 0, 1, ActorControlState.Default);
        Assert.Equal(
            ReceiverActorSnapshotStatus.Staged,
            runtime.StageSnapshot(session, new ObjectSnapshotMessage(
                new ServerSequence(1), new AuthoritativeObjectState[] { actor }, Array.Empty<ObjectTombstone>())).Status);
        var initial = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, initial.LocalProcessed);
        Assert.Equal(1, runtime.State.OwnedLocalObjects);
        Assert.Equal(1, ports.SpawnCalls);

        // A transform change updates the same instance; the identity is unchanged.
        Assert.True(runtime.AcceptUpdated(session, new ObjectUpdatedMessage(new ServerSequence(2),
            actor with { Revision = new ObjectRevision(2), Scale = 2 })).Accepted);
        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, ports.SpawnCalls);
        Assert.Equal(1, ports.ApplyCalls);

        // Its tombstone goes to the local pipeline and releases that receipt.
        Assert.True(runtime.AcceptDeleted(session, new ObjectDeletedMessage(new ServerSequence(3),
            new ObjectTombstone(actor.ObjectId, RscObjectKind.Actor, new ObjectRevision(3)))).Accepted);
        var removal = await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(1, removal.LocalProcessed);
        Assert.Equal(0, runtime.State.OwnedLocalObjects);
        Assert.Equal(1, ports.ReleaseCalls);
    }

    [Fact]
    public async Task AKindScopedResetReleasesOnlyThatKindAndKeepsTheOthersDrawn()
    {
        // GPose or a cutscene redraws actors only; furniture keeps its receipt.
        var ports = new RuntimePorts();
        var runtime = Runtime(ports);
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(runtime.TryActivate(session));
        var actor = new ActorState(ObjectId(), new ObjectRevision(1), new RscUserId("owner"), null, 4242,
            new Vector3State(1, 2, 3), 0, 1, ActorControlState.Default);
        var furniture = Furniture(ObjectId(), 1, "furniture:indoor:6000", 1);
        runtime.StageSnapshot(session, new ObjectSnapshotMessage(
            new ServerSequence(1), new AuthoritativeObjectState[] { actor, furniture }, Array.Empty<ObjectTombstone>()));
        await runtime.PumpAsync(session, DateTimeOffset.UtcNow);
        Assert.Equal(2, ports.SpawnCalls);

        runtime.ResetLocalObjects(RscObjectKind.Actor);
        for (var pass = 0; pass < 4; pass++)
            await runtime.PumpAsync(session, DateTimeOffset.UtcNow);

        Assert.Equal(1, ports.ReleaseCalls);
        Assert.Equal(3, ports.SpawnCalls);
        Assert.Equal(2, runtime.State.OwnedLocalObjects);
        Assert.Equal(ApplyDomainPhase.Open, runtime.LocalObjectDomain.Phase);
    }

    private static ReceiverActorRuntime Runtime(
        RuntimePorts ports,
        int maximumCleanupEntriesPerPass = 4,
        int maximumWorkers = 2)
    {
        var limits = new ReceiverActorRuntimeLimits(
            MaximumActors: 4,
            MaximumTrackedActors: 4,
            MaximumRetryActors: 4,
            MaximumConcurrentWorkers: maximumWorkers,
            MaximumSnapshotAdmissionsPerPump: 4,
            MaximumRetryPromotionsPerPump: 4,
            MaximumCleanupEntriesPerPass: maximumCleanupEntriesPerPass);
        return new ReceiverActorRuntime(
            ports,
            ports,
            limits,
            localObjectRuntime: ports,
            localObjectRetryPolicy: new(
                MaximumAttempts: 2,
                InitialDelay: TimeSpan.FromMilliseconds(1),
                MaximumDelay: TimeSpan.FromMilliseconds(2),
                JitterRatio: 0));
    }

    private sealed class RuntimePorts :
        IGameThreadDispatcher,
        IReceiverReadinessPort,
        ILocalObjectRuntimePort
    {
        private readonly object dispatcherGate = new();
        private bool inDispatcher;
        private readonly Dictionary<Guid, LocalObjectInstanceHandle> localObjects = new();

        public bool IsReady => true;
        public Queue<ApplyPortStatus> SpawnStatuses { get; } = new();
        public ApplyPortStatus ReleaseStatus { get; set; } = ApplyPortStatus.Succeeded;
        public int SpawnCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public List<string> Events { get; } = new();

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (dispatcherGate)
            {
                Assert.False(inDispatcher);
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
        }

        public ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(
            LocalObjectSpawnRequest request)
        {
            Assert.True(inDispatcher);
            SpawnCalls++;
            var status = SpawnStatuses.TryDequeue(out var configured)
                ? configured
                : ApplyPortStatus.Succeeded;
            if (status != ApplyPortStatus.Succeeded)
                return new(status, Error: "spawn failed");

            var handle = new LocalObjectInstanceHandle(
                Guid.NewGuid(),
                request.Token.ObjectId,
                request.Identity.Kind,
                request.SpawnGeneration);
            localObjects.Add(handle.OwnershipId, handle);
            Events.Add($"spawn:{handle.Kind}:{request.Identity.Value}:{handle.SpawnGeneration}");
            return new(ApplyPortStatus.Succeeded, handle);
        }

        public ApplyPortResult ApplyOwned(
            LocalObjectInstanceHandle handle,
            AuthoritativeObjectState desiredState)
        {
            Assert.True(inDispatcher);
            ApplyCalls++;
            Assert.True(localObjects.ContainsKey(handle.OwnershipId));
            Events.Add($"apply:{handle.Kind}:{handle.SpawnGeneration}");
            return new(ApplyPortStatus.Succeeded);
        }

        public ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle)
        {
            Assert.True(inDispatcher);
            ReleaseCalls++;
            Events.Add($"release:{handle.Kind}:{handle.SpawnGeneration}");
            if (ReleaseStatus == ApplyPortStatus.Succeeded)
                localObjects.Remove(handle.OwnershipId);
            return new(
                ReleaseStatus,
                ReleaseStatus == ApplyPortStatus.Succeeded ? null : "release failed");
        }
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
}
