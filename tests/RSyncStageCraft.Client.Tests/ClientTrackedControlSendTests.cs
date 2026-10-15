#nullable enable

using System.Net.WebSockets;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ClientTrackedControlSendTests
{
    public static IEnumerable<object[]> Requests() =>
        ClientControlSendCoordinatorTests.OutboundMessages()
            .Where(sample => (ObjectProtocolMessageType)sample[0] !=
                ObjectProtocolMessageType.AcknowledgeObjects);

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task EveryRequestRegistersExactMetadataBeforeProviderEntry(
        ObjectProtocolMessageType type, object payload)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        ClientControlRequestReceipt? observed = null;
        socket.SendStep = (buffer, token) =>
        {
            Assert.True(new ObjectControlMessageCodec().TryDecode(buffer, out var message, out _));
            Assert.Equal(type, message!.Envelope.MessageType);
            Assert.True(owner.TryFindControlRequest(authenticated.Connection,
                authenticated.Result.Binding!, message.Envelope.MessageId, out observed));
            Assert.Equal(authenticated.Result.Session, observed!.LocalSession);
            Assert.Equal(authenticated.Result.Binding!.Remote, observed.Remote);
            Assert.Equal(payload is ObjectEventBatchRequest batch
                ? new ClientObjectEventRequestMetadata(batch.After, batch.MaximumEvents)
                : (ClientObjectEventRequestMetadata?)null, observed.EventRequest);
            Assert.Equal(ClientObjectMutationRequestMetadata.FromRequest(payload), observed.MutationRequest);
            Assert.Equal(new ClientControlRequestSnapshot(1, 1, false),
                owner.GetControlRequestState(authenticated.Connection));
            return ValueTask.CompletedTask;
        };

        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, payload);

        Assert.True(sent.Sent);
        Assert.Equal(ClientControlRequestRegistrationStatus.Registered, sent.RequestRegistration);
        Assert.Same(observed, sent.RequestReceipt);
        var kind = type switch
        {
            ObjectProtocolMessageType.CreateObject => ClientControlRequestKind.CreateObject,
            ObjectProtocolMessageType.UpdateObject => ClientControlRequestKind.UpdateObject,
            ObjectProtocolMessageType.DeleteObject => ClientControlRequestKind.DeleteObject,
            ObjectProtocolMessageType.RequestObjectSnapshot => ClientControlRequestKind.Snapshot,
            ObjectProtocolMessageType.RequestObjectEvents => ClientControlRequestKind.Events,
            ObjectProtocolMessageType.SaveScene => ClientControlRequestKind.SaveScene,
            ObjectProtocolMessageType.ReadScene => ClientControlRequestKind.ReadScene,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        Assert.Equal(kind, observed!.Kind);
        var scene = payload switch
        {
            ReadSceneRequest read => (RscSceneId?)read.SceneId,
            SaveSceneRequest save => save.SceneId,
            _ => null,
        };
        Assert.Equal(scene, observed!.SceneId);
    }

    [Fact]
    public async Task SynchronousCompletionDuringSendCannotLoseRegistrationOrPermitIdReuse()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        socket.SendStep = (_, _) =>
        {
            Assert.True(owner.TryFindControlRequest(authenticated.Connection,
                authenticated.Result.Binding!, request.RequestId, out var receipt));
            Assert.True(owner.CompleteControlRequest(authenticated.Connection,
                authenticated.Result.Binding!, receipt!));
            return ValueTask.CompletedTask;
        };
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        Assert.True(sent.Sent);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false),
            owner.GetControlRequestState(authenticated.Connection));

        var duplicate = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);

        Assert.Equal(ClientControlSendStatus.RequestRefused, duplicate.Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.DuplicateRequest, duplicate.RequestRegistration);
        Assert.Null(duplicate.RequestReceipt);
        Assert.False(duplicate.Terminal);
        Assert.Equal(2, socket.SendCalls);
    }

    [Fact]
    public async Task PendingAndLifetimeBoundsRefuseWithoutSendingAndAllowExactAbandonment()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlSendLimits: ClientControlSendLimits.Default with { Requests = new(1, 2) });
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var first = await owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectSnapshotRequest(Guid.NewGuid()));
        var secondRequest = new ObjectSnapshotRequest(Guid.NewGuid());
        var refused = await owner.SendTrackedControlAsync(authenticated.Connection, socket, secondRequest);
        Assert.Equal(ClientControlRequestRegistrationStatus.PendingCapacityExceeded, refused.RequestRegistration);
        Assert.Equal(2, socket.SendCalls);
        Assert.True(owner.AbandonControlRequest(authenticated.Connection, first.RequestReceipt));
        var second = await owner.SendTrackedControlAsync(authenticated.Connection, socket, secondRequest);
        Assert.True(second.Sent);
        Assert.True(owner.AbandonControlRequest(authenticated.Connection, second.RequestReceipt));

        var exhausted = await owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectSnapshotRequest(Guid.NewGuid()));

        Assert.Equal(ClientControlRequestRegistrationStatus.RequestBudgetExhausted, exhausted.RequestRegistration);
        Assert.Equal(3, socket.SendCalls);
        Assert.Equal(new ClientControlRequestSnapshot(0, 2, false),
            owner.GetControlRequestState(authenticated.Connection));
        Assert.True(owner.TryResolveAuthenticated(authenticated.Connection, out _, out _));
    }

    [Fact]
    public async Task InvalidCancelledAndUncorrelatedMessagesConsumeNoRequestBudget()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        Assert.Equal(ClientControlSendStatus.Cancelled, (await owner.SendTrackedControlAsync(
            authenticated.Connection, socket, request, new CancellationToken(true))).Status);
        Assert.Equal(ClientControlSendStatus.InvalidMessage, (await owner.SendTrackedControlAsync(
            authenticated.Connection, socket, new ObjectSnapshotRequest(Guid.Empty))).Status);
        var ack = await owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectStreamAcknowledgement(new(0)));
        Assert.Equal(ClientControlSendStatus.RequestRefused, ack.Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest, ack.RequestRegistration);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, false),
            owner.GetControlRequestState(authenticated.Connection));
        Assert.True((await owner.SendTrackedControlAsync(authenticated.Connection, socket, request)).Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedRequestModesCannotAliasIdsOrBypassTracking(bool trackedFirst)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        var first = trackedFirst
            ? await owner.SendTrackedControlAsync(authenticated.Connection, socket, request)
            : await owner.SendControlAsync(authenticated.Connection, socket, request);
        Assert.True(first.Sent);

        var mixed = trackedFirst
            ? await owner.SendControlAsync(authenticated.Connection, socket, request)
            : await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);

        Assert.Equal(ClientControlSendStatus.RequestRefused, mixed.Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.RequestModeConflict, mixed.RequestRegistration);
        Assert.Equal(2, socket.SendCalls);
        Assert.False(mixed.Terminal);
        Assert.Equal(trackedFirst ? 1 : 0,
            owner.GetControlRequestState(authenticated.Connection).PendingRequests);
        Assert.True((await owner.SendControlAsync(authenticated.Connection, socket,
            new ObjectStreamAcknowledgement(new(0)))).Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryConnectionRetirementDrainsExactRequestMetadata(int mode)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectSnapshotRequest(Guid.NewGuid()));
        Assert.Equal(1, owner.GetControlRequestState(authenticated.Connection).PendingRequests);

        switch (mode)
        {
            case 0: await fixture.Runtime.DisconnectAsync(authenticated.Result.Session!.Value); break;
            case 1: await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true)); break;
            case 2: await fixture.Runtime.ShutdownAsync(); break;
            case 3:
                fixture.Runtime.Connection.Invalidate(fixture.Runtime.Connection.CurrentLease);
                Assert.False(owner.TryResolveAuthenticated(authenticated.Connection, out _, out _));
                break;
        }

        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true),
            owner.GetControlRequestState(authenticated.Connection));
        Assert.False(owner.AbandonControlRequest(authenticated.Connection, sent.RequestReceipt));
        Assert.False(owner.CompleteControlRequest(authenticated.Connection,
            authenticated.Result.Binding!, sent.RequestReceipt!));
    }

    [Fact]
    public async Task ReconnectMayReuseWireIdButOldCapabilityCannotCompleteReplacement()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var oldSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var old = await fixture.AuthenticateAsync(oldSocket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        var oldSent = await owner.SendTrackedControlAsync(old.Connection, oldSocket, request);
        oldSocket.Dispose();
        Assert.True((await owner.CloseTransportAsync(old.Connection)).Released);
        using var newSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var fresh = await fixture.AuthenticateAsync(newSocket); // Same remote coordinates deliberately.
        var newSent = await owner.SendTrackedControlAsync(fresh.Connection, newSocket, request);
        Assert.True(newSent.Sent);
        Assert.NotEqual(oldSent.RequestReceipt!.LocalSession, newSent.RequestReceipt!.LocalSession);
        Assert.False(owner.CompleteControlRequest(old.Connection, old.Result.Binding!, oldSent.RequestReceipt));
        Assert.False(owner.CompleteControlRequest(fresh.Connection, old.Result.Binding!, newSent.RequestReceipt));
        Assert.False(owner.AbandonControlRequest(fresh.Connection, oldSent.RequestReceipt));
        Assert.Equal(1, owner.GetControlRequestState(fresh.Connection).PendingRequests);
        Assert.True(owner.CompleteControlRequest(fresh.Connection,
            fresh.Result.Binding!, newSent.RequestReceipt));
    }

    [Fact]
    public async Task NoncooperativeSendCannotRestoreRequestsAfterTerminalRetirement()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.SendStep = async (_, _) => { entered.SetResult(); await release.Task; };
        var pending = owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectSnapshotRequest(Guid.NewGuid()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, owner.GetControlRequestState(authenticated.Connection).PendingRequests);
            owner.Terminate();
            Assert.Equal(new ClientControlRequestSnapshot(0, 0, true),
                owner.GetControlRequestState(authenticated.Connection));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True((await pending).Terminal);
        Assert.False((await pending).Sent);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true),
            owner.GetControlRequestState(authenticated.Connection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureOrCancellationDrainsRegistrationAndBorrowedBytes(bool cancel)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        socket.SendStep = (_, _) =>
        {
            Assert.Equal(1, owner.GetControlRequestState(authenticated.Connection).PendingRequests);
            if (!cancel) throw new IOException("private send failure");
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        };

        var failed = await owner.SendTrackedControlAsync(authenticated.Connection, socket,
            new ObjectSnapshotRequest(Guid.NewGuid()), cancellation.Token);

        Assert.Equal(cancel ? ClientControlSendStatus.Cancelled : ClientControlSendStatus.TransportFailure,
            failed.Status);
        Assert.True(failed.Terminal);
        Assert.Equal(ClientControlRequestRegistrationStatus.Registered, failed.RequestRegistration);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true),
            owner.GetControlRequestState(authenticated.Connection));
        Assert.DoesNotContain("private", failed.ToString());
        Assert.All(socket.BorrowedSend.ToArray(), value => Assert.Equal(0, value));
    }
}
