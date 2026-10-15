using System.Buffers;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverControlIngressTests
{
    [Fact]
    public async Task ReceiverActivationCannotBindControlBeforeLifecycleCommit()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        ReceiverControlBindResult premature = default;
        fixture.Ingress.BeforeLifecycleActivationCommit = session =>
        {
            premature = fixture.Ingress.BindVerifiedConnection(
                session,
                new(session.SessionId, session.SessionGeneration, 57));
        };

        var connected = await fixture.Runtime.ConnectAsync(1, 2);
        fixture.Ingress.BeforeLifecycleActivationCommit = null;

        Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
        Assert.Equal(ReceiverControlBindStatus.InactiveSession, premature.Status);
        Assert.Null(premature.Binding);
        Assert.Equal(0, fixture.Ingress.RetainedBindingCount);

        var session = connected.Session!.Value;
        var committed = fixture.Ingress.BindVerifiedConnection(
            session,
            new(session.SessionId, session.SessionGeneration, 57));
        Assert.True(committed.Bound);
    }

    [Fact]
    public async Task ReceiverActivationCannotUseRootRoutesBeforeLifecycleCommit()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
        ReceiverActorPumpResult prematurePump = default;
        fixture.Ingress.BeforeLifecycleActivationCommit = session =>
        {
            prematurePump = fixture.Runtime.PumpReceiverAsync(
                    session,
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
        };

        var connected = await fixture.Runtime.ConnectAsync(1, 2);
        fixture.Ingress.BeforeLifecycleActivationCommit = null;

        Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
        Assert.Equal(ReceiverActorPumpStatus.InactiveSession, prematurePump.Status);
    }

    [Fact]
    public async Task BindingRequiresActivationAndIsSingleUseWithIndependentEpochs()
    {
        await using var fixture = new Fixture();
        Assert.Equal(ReceiverControlBindStatus.InactiveSession,
            fixture.Ingress.BindVerifiedConnection(new(1, 2, 1), new(1, 2, 57)).Status);
        await fixture.Connect();
        Assert.NotEqual(fixture.Session.ConnectionEpoch, fixture.Binding.Remote.ConnectionEpoch);
        Assert.Equal(1, fixture.Ingress.RetainedBindingCount);
        foreach (var epoch in new[] { 57, 58 })
        {
            var result = fixture.Ingress.BindVerifiedConnection(fixture.Session, new(1, 2, epoch));
            Assert.Equal(ReceiverControlBindStatus.AlreadyBound, result.Status);
            Assert.Null(result.Binding);
        }
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Accepted);
    }

    [Theory]
    [InlineData(0, 2, 57)]
    [InlineData(1, 0, 57)]
    [InlineData(1, 2, 0)]
    [InlineData(9, 2, 57)]
    [InlineData(1, 9, 57)]
    public async Task InvalidOrDifferentLogicalIdentityCannotBind(long id, long generation, long epoch)
    {
        await using var fixture = new Fixture();
        await fixture.Connect(bind: false);
        Assert.Equal(ReceiverControlBindStatus.InvalidSession,
            fixture.Ingress.BindVerifiedConnection(fixture.Session, new(id, generation, epoch)).Status);
        Assert.Equal(0, fixture.Ingress.RetainedBindingCount);
    }

    [Theory]
    [InlineData(9, 2, 57)]
    [InlineData(1, 9, 57)]
    [InlineData(1, 2, 9)]
    public async Task EveryRemoteEnvelopeComponentMustMatch(long id, long generation, long epoch)
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var bytes = Encode(new(id, generation, epoch), ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot());
        Assert.Equal(ReceiverControlReceiveStatus.EnvelopeMismatch,
            fixture.Ingress.Receive(fixture.Binding, bytes).Status);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(fixture.Session));
    }

    [Fact]
    public async Task EqualNumbersFromAnotherIngressAreNotACapability()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        await first.Connect();
        await second.Connect();
        Assert.Equal(first.Binding.LocalSession, second.Binding.LocalSession);
        Assert.Equal(first.Binding.Remote, second.Binding.Remote);
        var bytes = Encode(first.Binding.Remote, ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot());
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding, first.Ingress.Receive(null, bytes).Status);
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding,
            first.Ingress.Receive(second.Binding, bytes).Status);
        Assert.False(first.Runtime.Receiver.Events.IsInitializedForSession(first.Session));
        Assert.True(first.Ingress.Receive(first.Binding, bytes).Accepted);
    }

    public static IEnumerable<object[]> NonReceiverMessages() => ObjectControlMessageCodecTests.Messages()
        .Where(sample => (ObjectProtocolMessageType)sample[0] is
            ObjectProtocolMessageType.CreateObject or ObjectProtocolMessageType.UpdateObject or
            ObjectProtocolMessageType.DeleteObject or ObjectProtocolMessageType.AcknowledgeObjects or
            ObjectProtocolMessageType.Rejected or ObjectProtocolMessageType.RequestObjectSnapshot or
            ObjectProtocolMessageType.RequestObjectEvents or ObjectProtocolMessageType.SaveScene or
            ObjectProtocolMessageType.ReadScene);

    [Theory]
    [MemberData(nameof(NonReceiverMessages))]
    public async Task DirectionAndUnsupportedResponsesFailClosed(ObjectProtocolMessageType type, object payload)
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var result = fixture.Receive(type, payload);
        // A rejection is a correlated reply for the request registry, never
        // desired-state intake; everything else here is client-to-server only.
        Assert.Equal(type == ObjectProtocolMessageType.Rejected
            ? ReceiverControlReceiveStatus.Reply
            : ReceiverControlReceiveStatus.WrongDirection, result.Status);
        Assert.False(result.Accepted);
        Assert.Null(result.Event);
        Assert.Equal(0, fixture.Runtime.Receiver.State.DesiredLocalObjects);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(fixture.Session));
    }

    [Fact]
    public async Task InvalidAndOversizedFramesDoNotPoisonTheReceiveSlot()
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var malformed = fixture.Ingress.Receive(fixture.Binding, "{"u8.ToArray());
        Assert.Equal(ReceiverControlReceiveStatus.InvalidMessage, malformed.Status);
        Assert.Equal(ObjectControlCodecError.InvalidJson, malformed.CodecError);
        var oversized = fixture.Ingress.Receive(fixture.Binding,
            new byte[ProtocolLimits.MaximumControlMessageBytes + 1]);
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, oversized.CodecError);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Accepted);
    }

    [Fact]
    public async Task SnapshotAndEventResultsPreserveOrderingAndBackpressure()
    {
        await using var fixture = new Fixture(limitedWork: true);
        await fixture.Connect();
        var first = Actor();
        var second = Actor();
        Assert.Equal(ReceiverActorIngressStatus.NotInitialized,
            fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(1), first)).Event!.Value.Status);
        Assert.Equal(ReceiverActorSnapshotStatus.Completed,
            fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Snapshot!.Value.Status);
        Assert.Equal(ReceiverActorSnapshotStatus.AlreadyCompleted,
            fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Snapshot!.Value.Status);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(1), first)).Accepted);
        var duplicate = fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(1), first));
        Assert.Equal(ReceiverControlReceiveStatus.RejectedByReceiver, duplicate.Status);
        Assert.Equal(ReceiverActorIngressStatus.StaleSequence, duplicate.Event!.Value.Status);
        Assert.Equal(ReceiverActorIngressStatus.SequenceGap,
            fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(3), second)).Event!.Value.Status);
        var full = fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(2), second));
        Assert.Equal(ReceiverControlReceiveStatus.RejectedByReceiver, full.Status);
        Assert.Equal(ReceiverActorIngressStatus.AdmissionBackpressure, full.Event!.Value.Status);
        Assert.Equal(new ServerSequence(1), full.Event.Value.CurrentSequence);
        // Existing ingress retains bounded desired intent while leaving the
        // stream cursor unchanged. Retry that exact event after work drains.
        Assert.Equal(2, fixture.Runtime.Receiver.State.DesiredLocalObjects);
        await fixture.Runtime.PumpReceiverAsync(fixture.Session, DateTimeOffset.UtcNow);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(2), second)).Accepted);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectUpdated,
            new ObjectUpdatedMessage(new(3), second with { Revision = new(2), Position = new(4, 5, 6) })).Accepted);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectDeleted,
            new ObjectDeletedMessage(new(4), new(second.ObjectId, RscObjectKind.Actor, new(3)))).Accepted);
        Assert.Equal(new ServerSequence(4), fixture.Runtime.Receiver.Events.CurrentSequence);
        Assert.Equal(1, fixture.Runtime.Receiver.State.TrackedLocalWork);
        Assert.False(fixture.Ports.LocalObjects.ContainsKey(second.ObjectId)); // Intake is not application.
    }

    [Fact]
    public async Task SnapshotAdmissionsStillRequireBoundedPumpingBeforeEvents()
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var first = Actor();
        var staged = fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot,
            new ObjectSnapshotMessage(new(5), new[] { first }, Array.Empty<ObjectTombstone>()));
        Assert.Equal(ReceiverActorSnapshotStatus.Staged, staged.Snapshot!.Value.Status);
        var update = new ObjectUpdatedMessage(new(6), first with { Revision = new(2) });
        Assert.Equal(ReceiverActorIngressStatus.NotInitialized,
            fixture.Receive(ObjectProtocolMessageType.ObjectUpdated, update).Event!.Value.Status);
        await fixture.Runtime.PumpReceiverAsync(fixture.Session, DateTimeOffset.UtcNow);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectUpdated, update).Accepted);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("dependency-loss")]
    [InlineData("shutdown")]
    public async Task LifecycleAutomaticallyRetiresBindingsAndLateCleanupCannotClearReplacement(string transition)
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var oldBinding = fixture.Binding;
        var oldSession = fixture.Session;
        var oldBytes = Encode(oldBinding.Remote, ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot());
        switch (transition)
        {
            case "disconnect": await fixture.Runtime.DisconnectAsync(oldSession); break;
            case "dependency-loss": await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true)); break;
            default: await fixture.Runtime.ShutdownAsync(); break;
        }
        Assert.Equal(0, fixture.Ingress.RetainedBindingCount);
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding, fixture.Ingress.Receive(oldBinding, oldBytes).Status);
        Assert.Equal(ReceiverControlBindStatus.InactiveSession,
            fixture.Ingress.BindVerifiedConnection(oldSession, oldBinding.Remote).Status);
        if (transition == "shutdown")
            return;

        await fixture.Connect(remoteEpoch: 58);
        await ((IReceiverActorSessionRuntime)fixture.Ingress).RetryCleanupAsync(oldSession);
        Assert.Equal(1, fixture.Ingress.RetainedBindingCount);
        var newBytes = Encode(fixture.Binding.Remote, ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot());
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding, fixture.Ingress.Receive(oldBinding, newBytes).Status);
        Assert.Equal(ReceiverControlReceiveStatus.EnvelopeMismatch, fixture.Ingress.Receive(fixture.Binding, oldBytes).Status);
        Assert.True(fixture.Ingress.Receive(fixture.Binding, newBytes).Accepted);
    }

    [Fact]
    public async Task ReadinessIsCheckedEvenBeforeLifecycleReconciliation()
    {
        await using var fixture = new Fixture();
        await fixture.Connect(bind: false);
        fixture.Runtime.Readiness.Update(new(true, true, true, false, true));
        Assert.Equal(
            ReceiverActorPumpStatus.DependenciesUnavailable,
            (await fixture.Runtime.PumpReceiverAsync(
                fixture.Session,
                DateTimeOffset.UtcNow)).Status);
        Assert.Equal(ReceiverControlBindStatus.DependenciesUnavailable,
            fixture.Ingress.BindVerifiedConnection(fixture.Session, new(1, 2, 57)).Status);
        fixture.Runtime.Readiness.Update(new(true, true, true, true, true));
        fixture.Bind(57);
        fixture.Runtime.Readiness.Update(new(true, true, true, false, true));
        Assert.Equal(ReceiverControlReceiveStatus.DependenciesUnavailable,
            fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Status);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(fixture.Session));
    }

    [Fact]
    public async Task ReentrantReadinessRetirementCannotPublishStaleBinding()
    {
        var connection = new SyncConnectionLock();
        var readiness = new CallbackReadiness();
        var ports = new TestGamePorts();
        var receiver = new ReceiverActorRuntime(
            ports,
            readiness,
            localObjectRuntime: ports);
        var ingress = new ReceiverControlIngress(connection, receiver, readiness);
        var lease = connection.Acquire();
        var session = new ReceiverSessionKey(1, 2, lease.Epoch);
        Assert.True(receiver.TryActivate(session));
        readiness.DuringRead = () =>
            ((IReceiverActorSessionRuntime)ingress)
                .DisconnectAsync(session, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        var result = ingress.BindVerifiedConnection(
            session,
            new ReceiverRemoteConnection(1, 2, 57));

        Assert.Equal(ReceiverControlBindStatus.InactiveSession, result.Status);
        Assert.Null(result.Binding);
        Assert.Equal(0, ingress.RetainedBindingCount);
        Assert.False(receiver.Authority.IsCurrent(session));
    }

    [Fact]
    public async Task BindingPublicationUsesAtomicReadinessAdmission()
    {
        var connection = new SyncConnectionLock();
        var readiness = new SwitchableAdmissionReadiness();
        var ports = new TestGamePorts();
        var receiver = new ReceiverActorRuntime(
            ports,
            readiness,
            localObjectRuntime: ports);
        var ingress = new ReceiverControlIngress(connection, receiver, readiness);
        var lease = connection.Acquire();
        var session = new ReceiverSessionKey(1, 2, lease.Epoch);
        Assert.True(receiver.TryActivate(session));
        readiness.PermitAdmission = false;

        var result = ingress.BindVerifiedConnection(
            session,
            new ReceiverRemoteConnection(1, 2, 57));

        Assert.Equal(ReceiverControlBindStatus.DependenciesUnavailable, result.Status);
        Assert.Null(result.Binding);
        Assert.Equal(0, ingress.RetainedBindingCount);
        Assert.True(connection.Invalidate(lease));
        Assert.False((await receiver.DisconnectAsync(session)).HasRemainingOwnership);
    }

    [Fact]
    public async Task ReentrantReadinessRetirementCannotDispatchDecodedFrame()
    {
        var connection = new SyncConnectionLock();
        var readiness = new CallbackReadiness();
        var ports = new TestGamePorts();
        var receiver = new ReceiverActorRuntime(
            ports,
            readiness,
            localObjectRuntime: ports);
        var ingress = new ReceiverControlIngress(connection, receiver, readiness);
        var lease = connection.Acquire();
        var session = new ReceiverSessionKey(1, 2, lease.Epoch);
        Assert.True(receiver.TryActivate(session));
        var remote = new ReceiverRemoteConnection(1, 2, 57);
        var bound = ingress.BindVerifiedConnection(session, remote);
        Assert.True(bound.Bound);
        using var memory = new PausedMemory(
            Encode(remote, ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()),
            throws: false);
        var receive = Task.Run(() => ingress.Receive(bound.Binding, memory.Memory));
        try
        {
            await memory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            readiness.DuringRead = () =>
                ((IReceiverActorSessionRuntime)ingress)
                    .DisconnectAsync(session, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
        }
        finally
        {
            memory.Release.Set();
        }

        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding, result.Status);
        Assert.Null(result.Snapshot);
        Assert.False(receiver.Events.IsInitializedForSession(session));
        Assert.Equal(0, ingress.RetainedBindingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherLocalFenceRejectsFramesBeforeDecoding(bool revokeReceiver)
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        if (revokeReceiver)
            Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(fixture.Session));
        else
            Assert.True(fixture.Runtime.Connection.Invalidate(fixture.Runtime.Connection.CurrentLease));
        Assert.Equal(
            ReceiverActorPumpStatus.InactiveSession,
            (await fixture.Runtime.PumpReceiverAsync(
                fixture.Session,
                DateTimeOffset.UtcNow)).Status);
        Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding,
            fixture.Ingress.Receive(fixture.Binding, "{"u8.ToArray()).Status);
        Assert.Equal(ReceiverControlBindStatus.InactiveSession,
            fixture.Ingress.BindVerifiedConnection(fixture.Session, new(1, 2, 58)).Status);
        await fixture.Runtime.DisconnectAsync(fixture.Session);
        Assert.Equal(0, fixture.Ingress.RetainedBindingCount);
    }

    [Fact]
    public async Task CleanupRequiredDoesNotRetainBindingOrPermitRebind()
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        Assert.True(fixture.Runtime.Connection.TryGetCancellationToken(
            fixture.Runtime.Connection.CurrentLease, out var token));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release connection cancellation.");
        });
        try
        {
            var disconnect = fixture.Runtime.DisconnectAsync(fixture.Session);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
                (await disconnect.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(0, fixture.Ingress.RetainedBindingCount);
            Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding,
                fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Status);
            Assert.Equal(ReceiverControlBindStatus.InactiveSession,
                fixture.Ingress.BindVerifiedConnection(fixture.Session, fixture.Binding.Remote).Status);
            Assert.Equal(SyncReceiverConnectStatus.CleanupRequired,
                (await fixture.Runtime.ConnectAsync(1, 2)).Status);
        }
        finally
        {
            release.Set();
        }
        Assert.True(SpinWait.SpinUntil(() => fixture.Runtime.Connection.PendingCancellationCallbacks == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
            (await fixture.Runtime.RetryCleanupAsync(fixture.Session)).Status);
        await fixture.Connect();
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredDecodeDoesNotBlockReplacementBindingAndRechecksBeforeDispatch(
        bool parserThrows)
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var oldBinding = fixture.Binding;
        var bytes = Encode(oldBinding.Remote, ObjectProtocolMessageType.ObjectSnapshot, LateSnapshot());
        using var memory = new PausedMemory(bytes, parserThrows);
        var receive = Task.Run(() => fixture.Ingress.Receive(oldBinding, memory.Memory));
        try
        {
            await memory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ReceiverControlReceiveStatus.Busy,
                fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Status);
            Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
                (await fixture.Runtime.DisconnectAsync(fixture.Session).WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(0, fixture.Ingress.RetainedBindingCount);
            await fixture.Connect(remoteEpoch: 58);
            Assert.True(fixture.Receive(
                ObjectProtocolMessageType.ObjectSnapshot,
                EmptySnapshot()).Accepted);
        }
        finally
        {
            memory.Release.Set();
        }
        if (parserThrows)
            await Assert.ThrowsAsync<IOException>(async () => await receive.WaitAsync(TimeSpan.FromSeconds(5)));
        else
            Assert.Equal(ReceiverControlReceiveStatus.InactiveBinding,
                (await receive.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.NotEqual(new ServerSequence(7), fixture.Runtime.Receiver.Events.CurrentSequence);
        Assert.True(fixture.Receive(ObjectProtocolMessageType.ObjectSnapshot, EmptySnapshot()).Accepted);
    }

    [Fact]
    public async Task RetiredReceiveCompletionCannotReleaseReplacementReceiveSlot()
    {
        await using var fixture = new Fixture();
        await fixture.Connect();
        var oldBinding = fixture.Binding;
        var oldBytes = Encode(
            oldBinding.Remote,
            ObjectProtocolMessageType.ObjectSnapshot,
            LateSnapshot());
        using var oldMemory = new PausedMemory(oldBytes, throws: false);
        var oldReceive = Task.Run(() => fixture.Ingress.Receive(oldBinding, oldMemory.Memory));
        try
        {
            await oldMemory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                SyncReceiverDisconnectStatus.Disconnected,
                (await fixture.Runtime.DisconnectAsync(fixture.Session)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Status);
            await fixture.Connect(remoteEpoch: 58);
            var replacementBinding = fixture.Binding;
            var replacementBytes = Encode(
                replacementBinding.Remote,
                ObjectProtocolMessageType.ObjectSnapshot,
                EmptySnapshot());
            using var replacementMemory = new PausedMemory(
                replacementBytes,
                throws: false);
            var replacementReceive = Task.Run(() => fixture.Ingress.Receive(
                replacementBinding,
                replacementMemory.Memory));
            try
            {
                await replacementMemory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                oldMemory.Release.Set();
                Assert.Equal(
                    ReceiverControlReceiveStatus.InactiveBinding,
                    (await oldReceive.WaitAsync(TimeSpan.FromSeconds(5))).Status);
                Assert.Equal(
                    ReceiverControlReceiveStatus.Busy,
                    fixture.Ingress.Receive(replacementBinding, replacementBytes).Status);
            }
            finally
            {
                replacementMemory.Release.Set();
            }

            Assert.True((await replacementReceive.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
            // The retired late frame (watermark 7) was never applied.
            Assert.NotEqual(new ServerSequence(7), fixture.Runtime.Receiver.Events.CurrentSequence);
        }
        finally
        {
            oldMemory.Release.Set();
        }
    }

    [Fact]
    public async Task SlowReceiverPreparationDoesNotHoldConnectionOrApplyAuthority()
    {
        var connection = new SyncConnectionLock();
        var readiness = new ReceiverReadinessCoordinator();
        readiness.Update(new(true, true, true, true, true));
        var ports = new TestGamePorts();
        using var fingerprint = new BlockingLocalFingerprint();
        var receiver = new ReceiverActorRuntime(
            ports,
            readiness,
            localObjectRuntime: ports,
            localObjectFingerprint: fingerprint);
        var ingress = new ReceiverControlIngress(connection, receiver, readiness);
        var lease = connection.Acquire();
        var session = new ReceiverSessionKey(1, 2, lease.Epoch);
        Assert.True(receiver.TryActivate(session));
        var remote = new ReceiverRemoteConnection(1, 2, 57);
        var bound = ingress.BindVerifiedConnection(session, remote);
        Assert.True(bound.Bound);
        var snapshot = Encode(remote, ObjectProtocolMessageType.ObjectSnapshot, new ObjectSnapshotMessage(
            new(1),
            new[] { Actor() },
            Array.Empty<ObjectTombstone>()));
        var receive = Task.Run(() => ingress.Receive(bound.Binding, snapshot));
        ReceiverApplyCleanupResult cleaned;

        try
        {
            await fingerprint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(await Task.Run(() => connection.Invalidate(lease))
                .WaitAsync(TimeSpan.FromSeconds(5)));
            var disconnect = Task.Run(() =>
                ((IReceiverActorSessionRuntime)ingress).DisconnectAsync(
                    session,
                    CancellationToken.None));
            Assert.True(SpinWait.SpinUntil(
                () => !receiver.Authority.IsCurrent(session),
                TimeSpan.FromSeconds(5)));
            cleaned = await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleaned.HasRemainingOwnership);
            Assert.Equal(0, receiver.State.DesiredLocalObjects);
            Assert.Equal(0, ingress.RetainedBindingCount);
        }
        finally
        {
            fingerprint.Release.Set();
        }

        var received = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReceiverControlReceiveStatus.RejectedByReceiver, received.Status);
        Assert.Equal(
            ReceiverActorSnapshotStatus.InactiveSession,
            received.Snapshot!.Value.Status);
    }

    [Fact]
    public async Task ReadinessLossDuringSlowReceiverPreparationRejectsFinalAdmission()
    {
        var connection = new SyncConnectionLock();
        var readiness = new ReceiverReadinessCoordinator();
        readiness.Update(new(true, true, true, true, true));
        var ports = new TestGamePorts();
        using var fingerprint = new BlockingLocalFingerprint();
        var receiver = new ReceiverActorRuntime(
            ports,
            readiness,
            localObjectRuntime: ports,
            localObjectFingerprint: fingerprint);
        var ingress = new ReceiverControlIngress(connection, receiver, readiness);
        var lease = connection.Acquire();
        var session = new ReceiverSessionKey(1, 2, lease.Epoch);
        Assert.True(receiver.TryActivate(session));
        var remote = new ReceiverRemoteConnection(1, 2, 57);
        var bound = ingress.BindVerifiedConnection(session, remote);
        Assert.True(bound.Bound);
        var snapshot = Encode(remote, ObjectProtocolMessageType.ObjectSnapshot, new ObjectSnapshotMessage(
            new(1),
            new[] { Actor() },
            Array.Empty<ObjectTombstone>()));
        var receive = Task.Run(() => ingress.Receive(bound.Binding, snapshot));

        try
        {
            await fingerprint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(readiness.Update(new(true, true, true, false, true)));
        }
        finally
        {
            fingerprint.Release.Set();
        }

        var received = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReceiverControlReceiveStatus.RejectedByReceiver, received.Status);
        Assert.Equal(
            ReceiverActorSnapshotStatus.InactiveSession,
            received.Snapshot!.Value.Status);
        Assert.Equal(0, receiver.State.DesiredLocalObjects);
        Assert.Equal(0, receiver.Snapshots.PendingCount);
        Assert.False(receiver.Events.IsInitializedForSession(session));

        Assert.True(connection.Invalidate(lease));
        var cleaned = await ((IReceiverActorSessionRuntime)ingress)
            .DisconnectAsync(session, CancellationToken.None);
        Assert.False(cleaned.HasRemainingOwnership);
        Assert.Equal(0, ingress.RetainedBindingCount);
    }

    private static ObjectSnapshotMessage EmptySnapshot() =>
        new(new(0), Array.Empty<AuthoritativeObjectState>(), Array.Empty<ObjectTombstone>());

    private static ActorState Actor() => new(new(Guid.NewGuid()), new(1), new("owner"), null,
        4242, new(0, 0, 0), 0, 1, ActorControlState.Default);

    // A late frame that must never be applied (its watermark is never reached otherwise).
    private static ObjectSnapshotMessage LateSnapshot() =>
        new(new(7), Array.Empty<AuthoritativeObjectState>(), Array.Empty<ObjectTombstone>());

    private static byte[] Encode(ReceiverRemoteConnection remote, ObjectProtocolMessageType type, object payload)
    {
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, ObjectControlMessageCodecTests.MessageIdFor(payload),
            remote.SessionId, remote.SessionGeneration, remote.ConnectionEpoch);
        Assert.True(new ObjectControlMessageCodec().TryEncode(envelope, payload, out var bytes, out var error), error.ToString());
        return bytes;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(bool limitedWork = false)
        {
            Runtime = new(Ports, Ports,
                receiverLimits: limitedWork ? new(2, 1, 1, 1, 1, 1, 2) : null);
        }

        public TestGamePorts Ports { get; } = new();
        public RSyncStageCraftRuntime Runtime { get; }
        public ReceiverControlIngress Ingress => Runtime.ControlIngress;
        public ReceiverSessionKey Session { get; private set; }
        public ReceiverControlBinding Binding { get; private set; } = null!;

        public async Task Connect(bool bind = true, long remoteEpoch = 57)
        {
            await Runtime.UpdateReadinessAsync(new(true, true, true, true, true));
            var connected = await Runtime.ConnectAsync(1, 2);
            Assert.Equal(SyncReceiverConnectStatus.Activated, connected.Status);
            Session = connected.Session!.Value;
            if (bind)
                Bind(remoteEpoch);
        }

        public void Bind(long epoch)
        {
            var bound = Ingress.BindVerifiedConnection(Session, new(1, 2, epoch));
            Assert.True(bound.Bound);
            Binding = bound.Binding!;
        }

        // Every message crosses the production codec.
        public ReceiverControlReceiveResult Receive(ObjectProtocolMessageType type, object payload) =>
            Ingress.Receive(Binding, Encode(Binding.Remote, type, payload));

        public async ValueTask DisposeAsync()
        {
            Assert.Equal(SyncReceiverDisconnectStatus.Terminated,
                (await Runtime.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(0, Ingress.RetainedBindingCount);
        }
    }

    // Pause the borrowed input exactly when parsing requests its span. This
    // is test-only scheduling, not a production decoder hook or blocking port.
    private sealed class PausedMemory(byte[] bytes, bool throws) : MemoryManager<byte>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override Memory<byte> Memory => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release decoding.");
            if (throws)
                throw new IOException("Test input provider failed.");
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => Release.Dispose();
    }

    private sealed class BlockingLocalFingerprint : ILocalObjectStateFingerprint, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public string Compute(AuthoritativeObjectState state)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release local-object fingerprinting.");

            return "fingerprint";
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class CallbackReadiness : IReceiverReadinessPort
    {
        public Action DuringRead;

        public bool IsReady
        {
            get
            {
                Interlocked.Exchange(ref DuringRead, null)?.Invoke();
                return true;
            }
        }
    }

    private sealed class SwitchableAdmissionReadiness :
        IReceiverReadinessPort,
        IReceiverReadinessAdmissionFence
    {
        public bool PermitAdmission { get; set; } = true;
        public bool IsReady => true;

        bool IReceiverReadinessAdmissionFence.TryExecuteIfReady<T>(
            Func<T> operation,
            out T result)
        {
            if (!PermitAdmission)
            {
                result = default!;
                return false;
            }

            result = operation();
            return true;
        }
    }
}
