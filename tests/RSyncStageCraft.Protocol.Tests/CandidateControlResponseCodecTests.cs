#nullable enable

using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

public sealed class CandidateControlResponseCodecTests
{
    public static IEnumerable<object[]> Responses()
    {
        var id = Guid.NewGuid();
        var scene = new RscSceneId(Guid.NewGuid());
        yield return [ObjectProtocolMessageType.ObjectEventBatchCompleted,
            new ObjectEventBatchCompletion(id, ObjectEventBatchCompletionStatus.Advanced,
                new(0), new(2), new(1), 1, true)];
        yield return [ObjectProtocolMessageType.ObjectEventBatchCompleted,
            new ObjectEventBatchCompletion(id, ObjectEventBatchCompletionStatus.UpToDate,
                new(0), new(0), new(0), 0, false)];
        yield return [ObjectProtocolMessageType.ObjectEventBatchCompleted,
            new ObjectEventBatchCompletion(id, ObjectEventBatchCompletionStatus.SnapshotRequired,
                new(0), new(2), new(0), 0, false)];
        yield return [ObjectProtocolMessageType.ObjectEventBatchCompleted,
            new ObjectEventBatchCompletion(id, ObjectEventBatchCompletionStatus.InvalidCursor,
                new(3), new(2), new(3), 0, false)];
        foreach (var status in Enum.GetValues<SceneSaveCompletionStatus>())
            yield return [ObjectProtocolMessageType.SceneSaveCompleted, new SceneSaveCompletion(id, scene, status)];
        foreach (var status in Enum.GetValues<SceneReadCompletionStatus>())
            yield return [ObjectProtocolMessageType.SceneReadCompleted, new SceneReadCompletion(id, scene, status,
                status == SceneReadCompletionStatus.Loaded ? ControlResponseContractTests.Document(scene,
                    [SceneObjectRecord.FromState(ControlResponseContractTests.Furniture(scene, Guid.NewGuid()))]) : null)];
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public void CandidateResponsesRoundTripOnlyWithExplicitVersionTwo(ObjectProtocolMessageType type, object payload)
    {
        var codec = Candidate();
        var envelope = Envelope(type, RequestId(payload));
        Assert.True(codec.TryEncode(envelope, payload, out var bytes, out var error), error.ToString());
        Assert.True(codec.TryDecode(bytes, out var decoded, out error), error.ToString());
        Assert.Equal(envelope, decoded!.Envelope);
        Assert.Equal(payload.GetType(), decoded.Payload.GetType());
        Assert.True(codec.TryEncode(decoded.Envelope, decoded.Payload, out var roundTrip, out _));
        Assert.Equal(bytes, roundTrip);
        Assert.DoesNotContain("isValid", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("RSyncStageCraft.Core", Encoding.UTF8.GetString(bytes));

        var standard = new ObjectControlMessageCodec();
        Assert.True(standard.TryDecode(bytes, out var standardDecoded, out error), error.ToString());
        Assert.Equal(envelope, standardDecoded!.Envelope);
        Assert.False(standard.TryEncode(envelope with { ProtocolVersion = 1 }, payload, out var refused, out error));
        Assert.Empty(refused);
        Assert.Equal(ObjectControlCodecError.InvalidEnvelope, error);
        Assert.False((envelope with { ProtocolVersion = 1 }).IsValid);
    }

    [Theory]
    [MemberData(nameof(ObjectControlMessageCodecTests.Messages), MemberType = typeof(ObjectControlMessageCodecTests))]
    public void ExistingFamiliesAlsoRequireTheExactlySelectedVersion(ObjectProtocolMessageType type, object payload)
    {
        var id = payload is ObjectRequestRejected rejected ? rejected.RequestId : Guid.NewGuid();
        var candidate = Candidate();
        Assert.True(candidate.TryEncode(Envelope(type, id), payload, out var bytes, out _));
        Assert.True(candidate.TryDecode(bytes, out _, out _));
        Assert.True(new ObjectControlMessageCodec().TryDecode(bytes, out _, out _));
        Assert.False(candidate.TryEncode(Envelope(type, id) with { ProtocolVersion = 1 }, payload, out _, out _));
    }

    [Theory]
    [InlineData("unknown-outcome")]
    [InlineData("numeric-outcome")]
    [InlineData("missing-document")]
    [InlineData("missing-scene-generation")]
    [InlineData("missing-null-union-field")]
    [InlineData("unknown-document-property")]
    [InlineData("private-error-field")]
    [InlineData("duplicate-scene-id")]
    [InlineData("request-id-mismatch")]
    [InlineData("wrong-document-scene")]
    [InlineData("failure-with-document")]
    [InlineData("missing-object-identity")]
    [InlineData("furniture-scale")]
    [InlineData("unknown-object-kind")]
    public void AmbiguousOrUnsafeSceneFramesAreRejected(string mutation)
    {
        var scene = new RscSceneId(Guid.NewGuid());
        var response = new SceneReadCompletion(Guid.NewGuid(), scene, SceneReadCompletionStatus.Loaded,
            ControlResponseContractTests.Document(scene,
                [SceneObjectRecord.FromState(ControlResponseContractTests.Furniture(scene, Guid.NewGuid()))]));
        var codec = Candidate();
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.SceneReadCompleted, response.RequestId),
            response, out var bytes, out _));
        var root = JsonNode.Parse(bytes)!.AsObject();
        var payload = root["payload"]!.AsObject();
        var document = payload["document"]!.AsObject();
        var item = document["objects"]![0]!.AsObject();
        switch (mutation)
        {
            case "unknown-outcome": payload["status"] = "successProbably"; break;
            case "numeric-outcome": payload["status"] = 0; break;
            case "missing-document": payload.Remove("document"); break;
            case "missing-scene-generation": document.Remove("sceneGeneration"); break;
            case "missing-null-union-field": item.Remove("actor"); break;
            case "unknown-document-property": document["path"] = "C:/private"; break;
            case "private-error-field": payload["error"] = "private exception"; break;
            case "request-id-mismatch": payload["requestId"] = Guid.NewGuid().ToString(); break;
            case "wrong-document-scene": document["sceneId"]!["value"] = Guid.NewGuid().ToString(); break;
            case "failure-with-document": payload["status"] = "failed"; break;
            case "missing-object-identity": item["furniture"]!.AsObject().Remove("objectId"); break;
            case "furniture-scale": item["furniture"]!["scale"] = new JsonObject { ["x"] = 1 }; break;
            case "unknown-object-kind": item["kind"] = "executeClrType"; break;
            case "duplicate-scene-id": break;
        }
        var json = root.ToJsonString();
        if (mutation == "duplicate-scene-id")
            json = json.Replace("\"sceneGeneration\":1", "\"sceneGeneration\":1,\"sceneGeneration\":1");
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(json), out var decoded, out _));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(ObjectProtocolMessageType.ObjectEventBatchCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneSaveCompleted)]
    [InlineData(ObjectProtocolMessageType.SceneReadCompleted)]
    [InlineData(ObjectProtocolMessageType.Rejected)]
    public void CandidateCorrelationRejectsSplitEnvelopeAndPayloadIdsBeforeEncoding(ObjectProtocolMessageType type)
    {
        var id = Guid.NewGuid();
        object payload = type switch
        {
            ObjectProtocolMessageType.ObjectEventBatchCompleted => new ObjectEventBatchCompletion(id,
                ObjectEventBatchCompletionStatus.UpToDate, new(0), new(0), new(0), 0, false),
            ObjectProtocolMessageType.SceneSaveCompleted => new SceneSaveCompletion(id, new(Guid.NewGuid()),
                SceneSaveCompletionStatus.Failed),
            ObjectProtocolMessageType.SceneReadCompleted => new SceneReadCompletion(id, new(Guid.NewGuid()),
                SceneReadCompletionStatus.NotFound, null),
            ObjectProtocolMessageType.Rejected => new ObjectRequestRejected(id, ProtocolRejectReason.NotOwner, null),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        Assert.False(Candidate().TryEncode(Envelope(type, Guid.NewGuid()), payload, out var bytes, out var error));
        Assert.Empty(bytes);
        Assert.Equal(ObjectControlCodecError.InvalidPayload, error);
    }

    [Fact]
    public void CandidateRetainsByteBoundsAndDoesNotChangeNegotiation()
    {
        var payload = new SceneSaveCompletion(Guid.NewGuid(), new(Guid.NewGuid()), SceneSaveCompletionStatus.Saved);
        Assert.False(new ObjectControlMessageCodec(64, ProtocolConstants.CurrentVersion)
            .TryEncode(Envelope(ObjectProtocolMessageType.SceneSaveCompleted, payload.RequestId), payload,
                out var bytes, out var error));
        Assert.Empty(bytes);
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, error);
        Assert.False(new ObjectControlMessageCodec(64, ProtocolConstants.CurrentVersion)
            .TryDecode(new byte[65], out _, out error));
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, error);
        // Protocol 7: one active version; older and newer ones are refused.
        Assert.Equal(7, ProtocolConstants.CurrentVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectControlMessageCodec(protocolVersion: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectControlMessageCodec(protocolVersion: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectControlMessageCodec(protocolVersion: 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectControlMessageCodec(protocolVersion: 8));
    }

    private static ObjectControlMessageCodec Candidate() => new(protocolVersion: ProtocolConstants.CurrentVersion);
    private static SessionMessageEnvelope Envelope(ObjectProtocolMessageType type, Guid id) =>
        new(ProtocolConstants.CurrentVersion, type, id, 1, 2, 3);
    private static Guid RequestId(object payload) => payload switch
    {
        ObjectEventBatchCompletion completion => completion.RequestId,
        SceneSaveCompletion completion => completion.RequestId,
        SceneReadCompletion completion => completion.RequestId,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };
}
