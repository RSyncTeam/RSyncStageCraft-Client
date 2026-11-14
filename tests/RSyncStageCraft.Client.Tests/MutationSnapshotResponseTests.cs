#nullable enable

using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class MutationSnapshotResponseTests
{

    public static IEnumerable<object[]> Responses()
    {
        var state = Furniture(new(1));
        var id = Guid.NewGuid();
        yield return [ObjectProtocolMessageType.ObjectMutationCompleted, new ObjectMutationCompletion(id, ObjectMutationCompletionKind.Create, false, new(1), state, null)];
        yield return [ObjectProtocolMessageType.ObjectMutationCompleted, new ObjectMutationCompletion(id, ObjectMutationCompletionKind.Create, true, new(5), state with { Revision = new(3) }, null)];
        yield return [ObjectProtocolMessageType.ObjectMutationCompleted, new ObjectMutationCompletion(id, ObjectMutationCompletionKind.Update, false, new(5), state with { Revision = new(2) }, null)];
        yield return [ObjectProtocolMessageType.ObjectMutationCompleted, new ObjectMutationCompletion(id, ObjectMutationCompletionKind.Delete, false, new(5), null, new(state.ObjectId, state.Kind, new(2)))];
        yield return [ObjectProtocolMessageType.ObjectSnapshotCompleted, new ObjectSnapshotCompletion(id, new(new(0), [], []))];
        yield return [ObjectProtocolMessageType.ObjectSnapshotCompleted, new ObjectSnapshotCompletion(id, new(new(5), [state], [new(new(Guid.NewGuid()), RscObjectKind.Light, new(2))]))];
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public void RepliesRoundTripOnlyOnExplicitCandidateCodec(ObjectProtocolMessageType type, object payload)
    {
        var id = payload is ObjectMutationCompletion mutation ? mutation.RequestId : ((ObjectSnapshotCompletion)payload).RequestId;
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, id, 1, 2, 3);
        var codec = new ObjectControlMessageCodec(protocolVersion: ProtocolConstants.CurrentVersion);
        Assert.True(codec.TryEncode(envelope, payload, out var bytes, out _));
        Assert.True(codec.TryDecode(bytes, out var message, out _));
        Assert.True(codec.TryEncode(message!.Envelope, message.Payload, out var encoded, out _));
        Assert.Equal(bytes, encoded);
        Assert.DoesNotContain("isValid", Encoding.UTF8.GetString(bytes));
        Assert.True(new ObjectControlMessageCodec().TryDecode(bytes, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 1 }, payload, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 2 }, payload, out _, out _));
        Assert.False(codec.TryEncode(envelope with { MessageId = Guid.NewGuid() }, payload, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 3 }, payload, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 4 }, payload, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 5 }, payload, out _, out _));
        Assert.False(new ObjectControlMessageCodec().TryEncode(envelope with { ProtocolVersion = 6 }, payload, out _, out _));
        Assert.Equal(7, ProtocolConstants.CurrentVersion);
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("split-id")]
    [InlineData("unknown-field")]
    [InlineData("numeric-kind")]
    [InlineData("missing-idempotent")]
    [InlineData("unknown-kind")]
    [InlineData("both-results")]
    [InlineData("no-results")]
    [InlineData("zero-watermark")]
    [InlineData("invalid-state")]
    public void MalformedMutationReplyIsRejectedBeforeTypedMatching(string fault)
    {
        var payload = (ObjectMutationCompletion)Responses().First()[1];
        var codec = new ObjectControlMessageCodec(protocolVersion: ProtocolConstants.CurrentVersion);
        Assert.True(codec.TryEncode(new(ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.ObjectMutationCompleted, payload.RequestId, 1, 2, 3), payload, out var bytes, out _));
        var json = JsonNode.Parse(bytes)!.AsObject();
        var body = json["payload"]!;
        switch (fault)
        {
            case "missing-id": body.AsObject().Remove("requestId"); break;
            case "split-id": body["requestId"] = Guid.NewGuid(); break;
            case "unknown-field": body["error"] = "private data"; break;
            case "numeric-kind": body["kind"] = 0; break;
            case "missing-idempotent": body.AsObject().Remove("idempotent"); break;
            case "unknown-kind": body["kind"] = "future"; break;
            case "both-results": body["tombstone"] = JsonNode.Parse("{\"objectId\":{\"value\":\"" + Guid.NewGuid() + "\"},\"kind\":\"Furniture\",\"deletionRevision\":{\"value\":2}}"); break;
            case "no-results": body["state"] = null; break;
            case "zero-watermark": body["watermark"]!["value"] = 0; break;
            case "invalid-state": body["state"]!["revision"]!["value"] = 2; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(json.ToJsonString()), out var message, out _));
        Assert.Null(message);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("kind")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("request")]
    [InlineData("scene")]
    [InlineData("overflow")]
    public void MutationCompletionMustMatchRetainedScalarIdentity(string fault)
    {
        var fixture = new ClientControlResponseCorrelationTests.Fixture();
        var state = Furniture(new(2));
        var kind = fault == "scene" ? ClientControlRequestKind.CreateObject : ClientControlRequestKind.UpdateObject;
        var metadata = kind == ClientControlRequestKind.CreateObject
            ? new ClientObjectMutationRequestMetadata(state.Kind, state.SceneId, null, null)
            : new ClientObjectMutationRequestMetadata(state.Kind, null, state.ObjectId, new(fault == "overflow" ? long.MaxValue : 1));
        var receipt = fixture.Registry.Register(Guid.NewGuid(), kind, null, mutationRequest: metadata).Receipt!;
        var success = new ObjectMutationCompletion(receipt.RequestId, kind == ClientControlRequestKind.CreateObject
            ? ObjectMutationCompletionKind.Create : ObjectMutationCompletionKind.Update, kind == ClientControlRequestKind.CreateObject, new(8), state, null);
        switch (fault)
        {
            case "object": success = success with { State = state with { ObjectId = new(Guid.NewGuid()) } }; break;
            case "kind": success = success with { State = new LightState(state.ObjectId, state.Revision, state.CreatorId, state.SceneId, LightIdentity.Point, new(0, 0, 0), new(1, 1, 1)) }; break;
            case "revision": success = success with { State = state with { Revision = new(3) } }; break;
            case "operation": success = success with { Kind = ObjectMutationCompletionKind.Create, Idempotent = true }; break;
            case "request": success = success with { RequestId = Guid.NewGuid() }; break;
            case "scene": success = success with { State = state with { SceneId = null } }; break;
            case "overflow": break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var response = fixture.Receive(receipt, fixture.Message(receipt, ObjectProtocolMessageType.ObjectMutationCompleted, success));
        Assert.Equal(fault switch
        {
            "object" => ClientControlResponseStatus.ObjectMismatch,
            "kind" => ClientControlResponseStatus.ObjectKindMismatch,
            "revision" or "overflow" => ClientControlResponseStatus.RevisionMismatch,
            "operation" => ClientControlResponseStatus.WrongResponse,
            "request" => ClientControlResponseStatus.InvalidMessage,
            _ => ClientControlResponseStatus.SceneMismatch,
        }, response.Status);
        Assert.Null(response.CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(1, 1, false), fixture.Registry.Snapshot);
    }

    [Theory]
    [InlineData(ObjectMutationCompletionKind.Create)]
    [InlineData(ObjectMutationCompletionKind.Update)]
    [InlineData(ObjectMutationCompletionKind.Delete)]
    public void ExactMutationSuccessCompletesOnceWithoutRetainingAResult(ObjectMutationCompletionKind kind)
    {
        var fixture = new ClientControlResponseCorrelationTests.Fixture();
        var objectId = new RscObjectId(Guid.NewGuid());
        object request = kind switch
        {
            ObjectMutationCompletionKind.Create => Create(),
            ObjectMutationCompletionKind.Update => new UpdateObjectRequest(Guid.NewGuid(), objectId, new(1), new FurnitureTransformPatch(new(9, 8, 7), null)),
            _ => new DeleteObjectRequest(Guid.NewGuid(), objectId, new(1)),
        };
        var payload = Completion(request);
        var receipt = fixture.Registry.Register(payload.RequestId, kind switch
        {
            ObjectMutationCompletionKind.Create => ClientControlRequestKind.CreateObject,
            ObjectMutationCompletionKind.Update => ClientControlRequestKind.UpdateObject,
            _ => ClientControlRequestKind.DeleteObject,
        }, null, mutationRequest: ClientObjectMutationRequestMetadata.FromRequest(request)).Receipt!;
        var message = fixture.Message(receipt, ObjectProtocolMessageType.ObjectMutationCompleted, payload);
        Assert.Equal(payload, fixture.Receive(receipt, message).CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), fixture.Registry.Snapshot);
        Assert.Equal(ClientControlResponseStatus.InactiveRequest, fixture.Receive(receipt, message).Status);
    }

    [Fact]
    public void BroadcastAndUncorrelatedSnapshotCannotCompleteCommands()
    {
        var fixture = new ClientControlResponseCorrelationTests.Fixture();
        var create = fixture.Register(ClientControlRequestKind.CreateObject);
        Assert.Equal(ClientControlResponseStatus.WrongResponse, fixture.Receive(create, fixture.Event(create, 1)).Status);
        var snapshot = fixture.Register(ClientControlRequestKind.Snapshot);
        Assert.Equal(ClientControlResponseStatus.InvalidMessage, fixture.Receive(snapshot,
            fixture.Message(snapshot, ObjectProtocolMessageType.ObjectSnapshot, new ObjectSnapshotMessage(new(0), [], []))).Status);
        Assert.Equal(2, fixture.Registry.Snapshot.PendingRequests);
    }

    [Theory]
    [InlineData(ClientControlRequestKind.CreateObject)]
    [InlineData(ClientControlRequestKind.UpdateObject)]
    [InlineData(ClientControlRequestKind.DeleteObject)]
    public void MutationAdmissionRequiresMetadataAndOtherKindsCannotBorrowIt(ClientControlRequestKind kind)
    {
        var fixture = new ClientControlResponseCorrelationTests.Fixture();
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest, fixture.Registry.Register(Guid.NewGuid(), kind, null).Status);
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest,
            fixture.Registry.Register(Guid.NewGuid(), kind, null, mutationRequest: default(ClientObjectMutationRequestMetadata)).Status);
        var retained = fixture.Register(kind);
        Assert.True(retained.MutationRequest!.Value.IsValidFor(kind));
        Assert.Equal(ClientControlRequestRegistrationStatus.InvalidRequest, fixture.Registry.Register(Guid.NewGuid(),
            ClientControlRequestKind.Snapshot, null, mutationRequest: retained.MutationRequest).Status);
        Assert.Equal(1, fixture.Registry.Snapshot.PendingRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotNormalizationRunsOutsideLocksAndCannotCommitAfterAuthorityLoss(bool directLeaseLoss)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        var records = new ReentrantStates(() =>
        {
            var mutation = Task.Run(() =>
            {
                if (directLeaseLoss) fixture.Runtime.Connection.Invalidate(fixture.Runtime.Connection.CurrentLease);
                else owner.Terminate();
            });
            Assert.True(mutation.Wait(TimeSpan.FromSeconds(3)), "Snapshot normalization held an authority lock.");
        });
        var payload = new ObjectSnapshotCompletion(request.RequestId, new(new(0), records, []));
        var result = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, sent.RequestReceipt!,
            Message(authenticated.Result.Binding!, sent.RequestReceipt!, ObjectProtocolMessageType.ObjectSnapshotCompleted, payload));
        Assert.Equal(1, records.Calls);
        Assert.Equal(ClientControlResponseStatus.InactiveConnection, result.Status);
        Assert.Null(result.CommandResult);
        Assert.Equal(new ClientControlRequestSnapshot(0, 0, true), owner.GetControlRequestState(authenticated.Connection));
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectStore!.Count);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(authenticated.Result.Session!.Value));
    }

    [Fact]
    public async Task MutationMayCompleteDuringSendWithoutApplyingOrLosingRegistration()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var owner = fixture.Runtime.Authentication;
        var request = Create();
        var payload = Completion(request);
        ClientControlResponseResult completed = default;
        socket.SendStep = (_, _) =>
        {
            Assert.True(owner.TryFindControlRequest(authenticated.Connection, authenticated.Result.Binding!, request.ClientRequestId, out var receipt));
            Assert.Equal(ClientObjectMutationRequestMetadata.FromRequest(request), receipt!.MutationRequest);
            completed = owner.ReceiveCandidateControlResponse(authenticated.Connection, authenticated.Result.Binding!, receipt,
                Message(authenticated.Result.Binding!, receipt, ObjectProtocolMessageType.ObjectMutationCompleted, payload));
            return ValueTask.CompletedTask;
        };
        var sent = await owner.SendTrackedControlAsync(authenticated.Connection, socket, request);
        Assert.True(sent.Sent);
        Assert.True(completed.Completed);
        Assert.Same(sent.RequestReceipt, completed.Receipt);
        Assert.Equal(new ClientControlRequestSnapshot(0, 1, false), owner.GetControlRequestState(authenticated.Connection));
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectStore!.Count);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(authenticated.Result.Session!.Value));
    }

    /// <summary>The server's successful reply to a furniture mutation request.</summary>
    private static ObjectMutationCompletion Completion(object request)
    {
        var completion = request switch
        {
            CreateObjectRequest create => new ObjectMutationCompletion(create.ClientRequestId, ObjectMutationCompletionKind.Create,
                false, new(1), Furniture(new(1)) with { SceneId = create.Payload.SceneId }, null),
            UpdateObjectRequest update => new ObjectMutationCompletion(update.RequestId, ObjectMutationCompletionKind.Update,
                false, new(2), Furniture(new(update.BaseRevision.Value + 1)) with { ObjectId = update.ObjectId, Position = new(9, 8, 7) }, null),
            DeleteObjectRequest delete => new ObjectMutationCompletion(delete.RequestId, ObjectMutationCompletionKind.Delete,
                false, new(2), null, new ObjectTombstone(delete.ObjectId, RscObjectKind.Furniture, new(delete.BaseRevision.Value + 1))),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        Assert.True(completion.IsValid);
        return completion;
    }

    private static CreateObjectRequest Create(RscSceneId? scene = null) =>
        new(Guid.NewGuid(), new FurnitureCreatePayload(scene ?? TestLocations.SharedScene, "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1), TestLocations.Default));

    private static FurnitureState Furniture(ObjectRevision revision) =>
        ControlResponseContractTests.Furniture(new(Guid.NewGuid()), Guid.NewGuid()) with { Revision = revision };

    private static ObjectControlMessage Message(ReceiverControlBinding binding, ClientControlRequestReceipt receipt,
        ObjectProtocolMessageType type, object payload) => new(new(ProtocolConstants.CurrentVersion, type, receipt.RequestId,
            binding.Remote.SessionId, binding.Remote.SessionGeneration, binding.Remote.ConnectionEpoch), payload);

    private sealed class ReentrantStates(Action callback) : IReadOnlyList<AuthoritativeObjectState>
    {
        public int Calls { get; private set; }
        public int Count { get { if (Calls++ == 0) callback(); return 0; } }
        public AuthoritativeObjectState this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<AuthoritativeObjectState> GetEnumerator() => Enumerable.Empty<AuthoritativeObjectState>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
