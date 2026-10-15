using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverSessionCleanupTests
{
    [Fact]
    public void CallbackBearingReceiverFencesAreAssemblyOwned()
    {
        const System.Reflection.BindingFlags publicInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public;
        const System.Reflection.BindingFlags internalInstance =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;

        AssertAssemblyOnlyCallback(
            typeof(ReceiverApplyAuthority),
            nameof(ReceiverApplyAuthority.TryExecute),
            publicInstance,
            internalInstance);
        AssertAssemblyOnlyCallback(
            typeof(DesiredLocalObjectStore),
            nameof(DesiredLocalObjectStore.TryExecuteCurrent),
            publicInstance,
            internalInstance);
        Assert.False(typeof(ILocalObjectDesiredFence).IsPublic);
        Assert.DoesNotContain(
            typeof(LocalObjectReconciler).GetConstructors(publicInstance),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ILocalObjectDesiredFence)));
        var rawRuntimeProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ReceiverActorRuntime.Authority),
            nameof(ReceiverActorRuntime.Events),
            nameof(ReceiverActorRuntime.Snapshots),
            nameof(ReceiverActorRuntime.LocalObjectStore),
            nameof(ReceiverActorRuntime.LocalObjectCoordinator),
            nameof(ReceiverActorRuntime.LocalObjectRetries),
            nameof(ReceiverActorRuntime.LocalObjectLedger),
        };
        Assert.DoesNotContain(
            typeof(ReceiverActorRuntime).GetProperties(publicInstance),
            property => rawRuntimeProperties.Contains(property.Name));
    }

    [Fact]
    public async Task CleanupInvalidatesBeforeBoundedWorkAndStateRemoval()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 8);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 8);
        var desired = Enumerable.Range(0, 5)
            .Select(index => store.Advance(
                session,
                Actor(new RscObjectId(Guid.NewGuid()), index + 1),
                $"fingerprint-{index}").Current!)
            .ToArray();
        foreach (var actor in desired)
            Assert.True(coordinator.TryAdmit(session, actor.ObjectId));
        Assert.True(coordinator.TryBeginNext(out var active));
        var cleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 2,
            localObjectCoordinator: coordinator,
            localObjectStore: store);

        cleanup.Cleanup(session);

        Assert.False(authority.IsCurrent(session));
        Assert.False(store.TryExecuteCurrent(active.Token, () => true, out _));
        Assert.True(coordinator.TrackedCount + store.Count > 0);
        await cleanup.CleanupCompletion;
        Assert.False(coordinator.HasSessionWork(session));
        Assert.False(store.HasSessionEntries(session));
        Assert.Equal(0, coordinator.TrackedCount);
        Assert.Equal(0, store.Count);
        Assert.False(coordinator.Complete(active.Token));
    }

    [Fact]
    public async Task OldSessionContinuationCannotRemoveReconnectState()
    {
        var authority = new ReceiverApplyAuthority();
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        Assert.True(authority.TryActivate(oldSession));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 8);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 8);
        var sharedActor = new RscObjectId(Guid.NewGuid());
        for (var index = 0; index < 5; index++)
        {
            var actorId = index == 0 ? sharedActor : new RscObjectId(Guid.NewGuid());
            var desired = store.Advance(
                oldSession,
                Actor(actorId, index + 1),
                $"old-{index}").Current!;
            Assert.True(coordinator.TryAdmit(oldSession, desired.ObjectId));
        }
        var cleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: coordinator,
            localObjectStore: store);

        cleanup.Cleanup(oldSession);
        Assert.True(authority.TryActivate(newSession));
        var current = store.Advance(
            newSession,
            Actor(sharedActor, revision: 1),
            "new").Current!;
        Assert.True(coordinator.TryAdmit(newSession, sharedActor));

        await cleanup.CleanupCompletion;

        Assert.True(authority.IsCurrent(newSession));
        Assert.True(store.TryGetCurrent(newSession, sharedActor, out var retained));
        Assert.Equal(current, retained);
        Assert.True(coordinator.HasSessionWork(newSession));
        Assert.False(store.HasSessionEntries(oldSession));
        Assert.False(coordinator.HasSessionWork(oldSession));
    }

    [Fact]
    public async Task TokenFencedRetryAdmissionCompletesBeforeInvalidationAndIsDrained()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 1);
        var desired = store.Advance(
            session,
            Actor(new RscObjectId(Guid.NewGuid()), revision: 1),
            "actor").Current!;
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 1);
        var retries = new LocalObjectApplyRetryQueue(maximumObjects: 1);
        var cleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: coordinator,
            localObjectRetries: retries,
            localObjectStore: store);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var admission = Task.Run(() => store.TryExecuteCurrent(
            desired.Token,
            () =>
            {
                entered.Set();
                release.Wait();
                return retries.TrySchedule(desired.Token, DateTimeOffset.UtcNow);
            },
            out var scheduled) && scheduled.Scheduled);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var cleanupTask = Task.Run(() => cleanup.Cleanup(session));
        await Task.Delay(20);
        Assert.False(cleanupTask.IsCompleted);

        release.Set();
        Assert.True(await admission);
        await cleanupTask;
        await cleanup.CleanupCompletion;

        Assert.Equal(0, retries.Count);
        Assert.False(store.HasSessionEntries(session));
        Assert.False(authority.IsCurrent(session));
    }

    [Fact]
    public async Task RepeatedCleanupCoalescesOneExactSessionContinuation()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 8);
        var coordinator = new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 8);
        for (var index = 0; index < 8; index++)
        {
            var desired = store.Advance(
                session,
                Actor(new RscObjectId(Guid.NewGuid()), index + 1),
                $"actor-{index}").Current!;
            Assert.True(coordinator.TryAdmit(session, desired.ObjectId));
        }

        var cleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: coordinator,
            localObjectStore: store);
        cleanup.Cleanup(session);
        var continuation = cleanup.CleanupCompletion;

        cleanup.Cleanup(session);
        cleanup.Cleanup(session);

        Assert.Same(continuation, cleanup.CleanupCompletion);
        await continuation;
        Assert.False(coordinator.HasSessionWork(session));
        Assert.False(store.HasSessionEntries(session));
    }

    [Fact]
    public async Task ExplicitRetryReplacesCancelledCleanupContinuation()
    {
        var authority = new ReceiverApplyAuthority();
        var session = new ReceiverSessionKey(1, 1, 1);
        Assert.True(authority.TryActivate(session));
        var store = new DesiredLocalObjectStore(authority, maximumObjects: 1);
        var cleanup = new ReceiverSessionCleanup(
            authority,
            eventIngress: null,
            snapshotIngress: null,
            maximumEntriesPerPass: 1,
            localObjectCoordinator: new LocalObjectApplyCoordinator(store, maximumTrackedObjects: 1),
            localObjectStore: store);
        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        var completionField = typeof(ReceiverSessionCleanup).GetField(
            "cleanupCompletion",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(completionField);
        completionField.SetValue(cleanup, cancelled);

        cleanup.Cleanup(session);
        var recovered = cleanup.CleanupCompletion;

        Assert.NotSame(cancelled, recovered);
        await recovered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(recovered.IsCanceled);
    }

    private static void AssertAssemblyOnlyCallback(
        Type type,
        string methodName,
        System.Reflection.BindingFlags publicInstance,
        System.Reflection.BindingFlags internalInstance)
    {
        Assert.DoesNotContain(
            type.GetMethods(publicInstance),
            method => method.Name == methodName);
        Assert.Contains(
            type.GetMethods(internalInstance),
            method => method.Name == methodName && method.IsAssembly);
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
