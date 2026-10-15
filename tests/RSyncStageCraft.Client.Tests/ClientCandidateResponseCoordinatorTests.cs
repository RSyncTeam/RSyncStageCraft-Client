#nullable enable

using System.Collections;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Scenes;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientCandidateResponseCoordinatorTests
{
    [Fact]
    public async Task SynchronousCandidateReplyUsesPublishedReceiptWithoutActorDispatch()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ReadSceneRequest(Guid.NewGuid(), new(Guid.NewGuid()));
        ClientControlResponseResult observed = default;
        socket.SendStep = (_, _) =>
        {
            Assert.True(owner.TryFindControlRequest(authenticated.Connection, authenticated.Result.Binding!, request.RequestId, out var receipt));
            observed = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt!,
                Message(authenticated.Result.Binding!, receipt!, ObjectProtocolMessageType.SceneReadCompleted,
                    new SceneReadCompletion(request.RequestId, request.SceneId, SceneReadCompletionStatus.Loaded,
                        ControlResponseContractTests.Document(request.SceneId,
                            [SceneObjectRecord.FromState(ControlResponseContractTests.Furniture(request.SceneId, Guid.NewGuid()))]))));
            return ValueTask.CompletedTask;
        };

        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);

        Assert.True(sent.Sent);
        Assert.True(observed.Completed);
        Assert.Same(sent.RequestReceipt, observed.Receipt);
        Assert.Single(Assert.IsType<SceneReadCompletion>(observed.CommandResult).Document!.Objects);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), owner.GetControlRequestState(authenticated.Connection));
        AssertEmptyReceiver(fixture, authenticated.Result.Session!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizationRunsOutsideAuthorityLocksAndRechecksRetirement(bool directLeaseLoss)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ReadSceneRequest(Guid.NewGuid(), new(Guid.NewGuid()));
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        var source = new ReentrantRecords(() =>
        {
            var mutation = Task.Run(() =>
            {
                if (directLeaseLoss) fixture.Runtime.Connection.Invalidate(fixture.Runtime.Connection.CurrentLease);
                else owner.Terminate();
            });
            Assert.True(mutation.Wait(TimeSpan.FromSeconds(3)), "Normalization held an authority lock across collection access.");
        });
        var response = new SceneReadCompletion(request.RequestId, request.SceneId, SceneReadCompletionStatus.Loaded,
            ControlResponseContractTests.Document(request.SceneId, source));

        var result = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!,
            sent.RequestReceipt!, Message(authenticated.Result.Binding!, sent.RequestReceipt!, ObjectProtocolMessageType.SceneReadCompleted, response));

        Assert.Equal(1, source.Calls);
        Assert.Equal(ClientControlResponseStatus.InactiveConnection, result.Status);
        Assert.Null(result.CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true), owner.GetControlRequestState(authenticated.Connection));
        AssertEmptyReceiver(fixture, authenticated.Result.Session!.Value);
    }

    [Theory]
    [InlineData(0)] // Foreign authentication capability.
    [InlineData(1)] // Numerically equivalent foreign binding.
    [InlineData(2)] // Forged local receipt.
    public async Task InvalidCapabilityNeverTouchesCallerCollections(int mode)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        await using var foreign = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var foreignSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var other = await foreign.AuthenticateAsync(foreignSocket);
        var owner = fixture.Runtime.Authentication;
        var request = new ReadSceneRequest(Guid.NewGuid(), new(Guid.NewGuid()));
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        var source = new ReentrantRecords(() => throw new InvalidOperationException("unreachable private callback"));
        var payload = new SceneReadCompletion(request.RequestId, request.SceneId, SceneReadCompletionStatus.Loaded,
            ControlResponseContractTests.Document(request.SceneId, source));
        var receipt = sent.RequestReceipt!;
        var forged = new ClientControlRequestReceipt(receipt.RequestId, receipt.Kind, receipt.SceneId,
            authenticated.Result.Binding!, receipt.Generation, receipt.EventRequest);

        var result = owner.ReceiveCandidateControlResponse(mode == 0 ? other.Connection : authenticated.Connection,
            mode == 1 ? other.Result.Binding! : authenticated.Result.Binding!, mode == 2 ? forged : receipt,
            Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.SceneReadCompleted, payload));

        Assert.Equal(ClientControlResponseStatus.InactiveConnection, result.Status);
        Assert.Equal(0, source.Calls);
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), owner.GetControlRequestState(authenticated.Connection));
    }

    [Fact]
    public async Task ReentrantExactCompletionCannotPublishSameResultTwice()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ReadSceneRequest(Guid.NewGuid(), new(Guid.NewGuid()));
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        var receipt = sent.RequestReceipt!;
        ClientControlResponseResult first = default;
        var source = new ReentrantRecords(() =>
        {
            first = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt,
                Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.SceneReadCompleted,
                    new SceneReadCompletion(request.RequestId, request.SceneId, SceneReadCompletionStatus.NotFound, null)));
        });

        var second = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt,
            Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.SceneReadCompleted,
                new SceneReadCompletion(request.RequestId, request.SceneId, SceneReadCompletionStatus.Loaded,
                    ControlResponseContractTests.Document(request.SceneId, source))));

        Assert.True(first.Completed);
        Assert.Equal(ClientControlResponseStatus.InactiveRequest, second.Status);
        Assert.Null(second.CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), owner.GetControlRequestState(authenticated.Connection));
    }

    [Theory]
    [InlineData(0)] // Disconnect.
    [InlineData(1)] // Dependency loss.
    [InlineData(2)] // Shutdown.
    public async Task PartialEvidenceDrainsWithTheExactSession(int mode)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectEventBatchRequest(Guid.NewGuid(), new(5), 2);
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        var receipt = sent.RequestReceipt!;
        var state = ControlResponseContractTests.Furniture(new(Guid.NewGuid()), Guid.NewGuid());
        Assert.Equal(ClientControlResponseStatus.EventObserved,
            owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt,
                Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.ObjectCreated,
                    new ObjectCreatedMessage(new(6), state))).Status);
        if (mode == 0) await fixture.Runtime.DisconnectAsync(authenticated.Result.Session!.Value);
        if (mode == 1) await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true));
        if (mode == 2) await fixture.Runtime.ShutdownAsync();

        var late = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt,
            Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.ObjectEventBatchCompleted,
                new ObjectEventBatchCompletion(request.RequestId, ObjectEventBatchCompletionStatus.Advanced,
                    new(5), new(6), new(6), 1, false)));
        Assert.Equal(ClientControlResponseStatus.InactiveConnection, late.Status);
        Assert.Null(late.CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true), owner.GetControlRequestState(authenticated.Connection));
    }

    [Theory]
    [InlineData("binding-alias")]
    [InlineData("unissued-lease")]
    [InlineData("lease-loss")]
    [InlineData("authority-loss")]
    [InlineData("readiness-loss")]
    public async Task AtomicCommandCommitRefusesLostOrForgedAuthorityBeforeCallback(string mutation)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var binding = authenticated.Result.Binding!;
        switch (mutation)
        {
            case "binding-alias": binding = new(binding.LocalSession, binding.Remote, binding.Connection); break;
            case "unissued-lease": binding = new(binding.LocalSession, binding.Remote, new SyncConnectionLease(binding.Connection.Epoch)); break;
            case "lease-loss": fixture.Runtime.Connection.Invalidate(binding.Connection); break;
            case "authority-loss": fixture.Runtime.Receiver.Authority.Invalidate(binding.LocalSession); break;
            case "readiness-loss": fixture.Runtime.Readiness.RequestShutdown(); break;
        }
        var calls = 0;
        Assert.False(fixture.Runtime.ControlIngress.TryExecuteCommandMetadata(binding, () => ++calls, out var result));
        Assert.Equal(0, calls);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExactCommandCommitExecutesOneBoundedMetadataOperation()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var calls = 0;
        Assert.True(fixture.Runtime.ControlIngress.TryExecuteCommandMetadata(authenticated.Result.Binding!,
            () => ++calls, out var result));
        Assert.Equal(1, result);
        Assert.Equal(1, calls);
        AssertEmptyReceiver(fixture, authenticated.Result.Session!.Value);
    }

    [Fact]
    public async Task OldCallbackAndReceiptCannotCompleteReusedIdOnReplacementTransport()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var oldSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var old = await fixture.AuthenticateAsync(oldSocket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectEventBatchRequest(Guid.NewGuid(), new(5), 1);
        var oldSent = await owner.SendTrackedControlAsync(old.Connection, oldSocket, request);
        oldSocket.Dispose();
        Assert.True((await owner.CloseTransportAsync(old.Connection)).Released);
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var fresh = await fixture.AuthenticateAsync(socket);
        var newSent = await owner.SendTrackedControlAsync(fresh.Connection, socket, request);
        Assert.True(newSent.Sent);
        Assert.NotEqual(oldSent.RequestReceipt!.LocalSession, newSent.RequestReceipt!.LocalSession);
        var state = ControlResponseContractTests.Furniture(new(Guid.NewGuid()), Guid.NewGuid());
        var created = Message(fresh.Result.Binding!, newSent.RequestReceipt, ObjectProtocolMessageType.ObjectCreated,
            new ObjectCreatedMessage(new(6), state));
        Assert.Equal(ClientControlResponseStatus.InactiveConnection,
            owner.ReceiveCandidateControlResponse(old.Connection, old.Result.Binding!, oldSent.RequestReceipt, created).Status);
        Assert.Equal(ClientControlResponseStatus.InactiveConnection,
            owner.ReceiveCandidateControlResponse(fresh.Connection, fresh.Result.Binding!, oldSent.RequestReceipt, created).Status);
        Assert.Equal(ClientControlResponseStatus.EventObserved,
            owner.ReceiveCandidateControlResponse(fresh.Connection, fresh.Result.Binding!, newSent.RequestReceipt, created).Status);
        Assert.True(owner.ReceiveCandidateControlResponse(fresh.Connection, fresh.Result.Binding!, newSent.RequestReceipt,
            Message(fresh.Result.Binding!, newSent.RequestReceipt, ObjectProtocolMessageType.ObjectEventBatchCompleted,
                new ObjectEventBatchCompletion(request.RequestId, ObjectEventBatchCompletionStatus.Advanced,
                    new(5), new(6), new(6), 1, false))).Completed);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), owner.GetControlRequestState(fresh.Connection));
        AssertEmptyReceiver(fixture, fresh.Result.Session!.Value);
    }

    [Theory]
    [InlineData(ObjectProtocolMessageType.ObjectEventBatchCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneSaveCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneReadCompleted)]
    public async Task AuthenticatedReceiverCompletesTrackedRequestsWithoutTreatingRepliesAsState(
        ObjectProtocolMessageType type)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var id = Guid.NewGuid();
        var scene = new RscSceneId(Guid.NewGuid());
        object request = type switch
        {
            ObjectProtocolMessageType.ObjectEventBatchCompleted => new ObjectEventBatchRequest(id, new(0), 1),
            ObjectProtocolMessageType.SceneSaveCompleted => new SaveSceneRequest(id, scene, new(0)),
            _ => new ReadSceneRequest(id, scene),
        };
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        Assert.True(sent.Sent);
        object response = type switch
        {
            ObjectProtocolMessageType.ObjectEventBatchCompleted => new ObjectEventBatchCompletion(id,
                ObjectEventBatchCompletionStatus.UpToDate, new(0), new(0), new(0), 0, false),
            ObjectProtocolMessageType.SceneSaveCompleted => new SceneSaveCompletion(id, scene, SceneSaveCompletionStatus.Saved),
            _ => new SceneReadCompletion(id, scene, SceneReadCompletionStatus.Loaded,
                ControlResponseContractTests.Document(scene,
                    [SceneObjectRecord.FromState(ControlResponseContractTests.Furniture(scene, Guid.NewGuid()))])),
        };
        var remote = authenticated.Result.Binding!.Remote;
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, id,
            remote.SessionId, remote.SessionGeneration, remote.ConnectionEpoch);
        Assert.True(new ObjectControlMessageCodec(protocolVersion: ProtocolConstants.CurrentVersion).TryEncode(envelope, response, out var bytes, out _));
        socket.Add(bytes);

        var received = await owner.ReceiveControlAsync(authenticated.Connection, socket);

        // The reply completes exactly its tracked request and is handed back
        // to the caller; it is never applied as desired state.
        Assert.Equal(ClientControlReceiveStatus.Accepted, received.Status);
        Assert.False(received.Terminal);
        Assert.Equal(ReceiverControlReceiveStatus.Accepted, received.Dispatch!.Value.Status);
        Assert.Equal(ObjectControlCodecError.None, received.Dispatch.Value.CodecError);
        var completed = Assert.IsType<ClientControlResponseResult>(received.Response);
        Assert.Equal(ClientControlResponseStatus.Completed, completed.Status);
        Assert.Same(sent.RequestReceipt, completed.Receipt);
        Assert.Equal(response.GetType(), completed.CommandResult!.GetType());
        Assert.Equal(id, RequestId(completed.CommandResult));
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectStore!.Count);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(authenticated.Result.Session!.Value));
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), owner.GetControlRequestState(authenticated.Connection));
        Assert.False(owner.CompleteControlRequest(authenticated.Connection, authenticated.Result.Binding!, sent.RequestReceipt!));
    }

    [Theory]
    [InlineData(ObjectProtocolMessageType.ObjectEventBatchCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneSaveCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneReadCompleted)]
    public async Task AuthenticatedReceiverRefusesRepliesWithoutAPendingRequest(
        ObjectProtocolMessageType type)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var id = Guid.NewGuid();
        var scene = new RscSceneId(Guid.NewGuid());
        object response = type switch
        {
            ObjectProtocolMessageType.ObjectEventBatchCompleted => new ObjectEventBatchCompletion(id,
                ObjectEventBatchCompletionStatus.UpToDate, new(0), new(0), new(0), 0, false),
            ObjectProtocolMessageType.SceneSaveCompleted => new SceneSaveCompletion(id, scene, SceneSaveCompletionStatus.Saved),
            _ => new SceneReadCompletion(id, scene, SceneReadCompletionStatus.Loaded,
                ControlResponseContractTests.Document(scene,
                    [SceneObjectRecord.FromState(ControlResponseContractTests.Furniture(scene, Guid.NewGuid()))])),
        };
        var remote = authenticated.Result.Binding!.Remote;
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, id,
            remote.SessionId, remote.SessionGeneration, remote.ConnectionEpoch);
        Assert.True(new ObjectControlMessageCodec(protocolVersion: ProtocolConstants.CurrentVersion).TryEncode(envelope, response, out var bytes, out _));
        socket.Add(bytes);

        var received = await owner.ReceiveControlAsync(authenticated.Connection, socket);

        // An unsolicited reply fails closed and ends the connection.
        Assert.Equal(ClientControlReceiveStatus.DispatchFailure, received.Status);
        Assert.True(received.Terminal);
        Assert.Equal(ReceiverControlReceiveStatus.UnmatchedReply, received.Dispatch!.Value.Status);
        Assert.Null(received.Response);
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectStore!.Count);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(authenticated.Result.Session!.Value));
    }

    private static Guid RequestId(object payload) => payload switch
    {
        ObjectEventBatchCompletion completion => completion.RequestId,
        SceneSaveCompletion completion => completion.RequestId,
        SceneReadCompletion completion => completion.RequestId,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };

    private static ObjectControlMessage Message(ReceiverControlBinding binding, ClientControlRequestReceipt receipt,
        ObjectProtocolMessageType type, object payload) => new(new(ProtocolConstants.CurrentVersion,
            type, receipt.RequestId, binding.Remote.SessionId, binding.Remote.SessionGeneration, binding.Remote.ConnectionEpoch), payload);

    private static void AssertEmptyReceiver(ClientAuthenticationCoordinatorTests.Fixture fixture, ReceiverSessionKey session)
    {
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectStore!.Count);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(session));
    }

    private sealed class ReentrantRecords(Action callback) : IReadOnlyList<SceneObjectRecord>
    {
        public int Calls { get; private set; }
        public int Count
        {
            get
            {
                if (Calls++ == 0) callback();
                return 0;
            }
        }
        public SceneObjectRecord this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<SceneObjectRecord> GetEnumerator() => Enumerable.Empty<SceneObjectRecord>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
