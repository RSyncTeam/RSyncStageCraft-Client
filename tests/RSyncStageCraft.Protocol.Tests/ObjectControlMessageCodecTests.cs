using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class ObjectControlMessageCodecTests
{
    public static IEnumerable<object[]> Messages()
    {
        var id = Guid.NewGuid();
        var actor = Actor();
        var furniture = new FurnitureState(new(Guid.NewGuid()), new(1), new("creator"), null,
            "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1));
        var light = new LightState(new(Guid.NewGuid()), new(1), new("creator"), null,
            LightIdentity.Point, new(1, 2, 3), new(1, 1, 1));
        var tombstone = new ObjectTombstone(new(Guid.NewGuid()), RscObjectKind.Actor, new(2));
        yield return [ObjectProtocolMessageType.CreateObject, CreatePayload()];
        yield return [ObjectProtocolMessageType.UpdateObject, new UpdateObjectRequest(id, actor.ObjectId, new(1),
            new ActorTransformPatch(new(0, 0, 0), null, null))];
        yield return [ObjectProtocolMessageType.DeleteObject, new DeleteObjectRequest(id, actor.ObjectId, new(1))];
        yield return [ObjectProtocolMessageType.ObjectSnapshot, new ObjectSnapshotMessage(new(4),
            new AuthoritativeObjectState[] { actor, furniture, light }, new[] { tombstone })];
        yield return [ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(1), actor)];
        yield return [ObjectProtocolMessageType.ObjectUpdated, new ObjectUpdatedMessage(new(2), furniture)];
        yield return [ObjectProtocolMessageType.ObjectDeleted, new ObjectDeletedMessage(new(3), tombstone)];
        yield return [ObjectProtocolMessageType.AcknowledgeObjects, new ObjectStreamAcknowledgement(new(0))];
        yield return [ObjectProtocolMessageType.Rejected, new ObjectRequestRejected(id, ProtocolRejectReason.NotOwner, null)];
        yield return [ObjectProtocolMessageType.RequestObjectSnapshot, new ObjectSnapshotRequest(id)];
        yield return [ObjectProtocolMessageType.RequestObjectEvents, new ObjectEventBatchRequest(id, new(0), 8)];
        yield return [ObjectProtocolMessageType.SaveScene, new SaveSceneRequest(id, new(Guid.NewGuid()), new(0))];
        yield return [ObjectProtocolMessageType.ReadScene, new ReadSceneRequest(id, new(Guid.NewGuid()))];
    }

    public static IEnumerable<object[]> Unions()
    {
        yield return [ObjectProtocolMessageType.CreateObject, CreatePayload(2)];
        yield return [ObjectProtocolMessageType.CreateObject, CreatePayload(3)];
        yield return [ObjectProtocolMessageType.CreateObject, new CreateObjectRequest(Guid.NewGuid(),
            new FurnitureCreatePayload(TestLocations.NewScene(), "furniture:indoor:6000", new(0, 0, 0), new(0, 0, 0, 1), TestLocations.Default))];
        yield return [ObjectProtocolMessageType.CreateObject, new CreateObjectRequest(Guid.NewGuid(),
            new FurnitureCreatePayload(TestLocations.NewScene(), "furniture:indoor:6000", new(0, 0, 0), new(0, 0, 0, 1), TestLocations.Default)
            { Stain = 36 })];
        yield return [ObjectProtocolMessageType.ObjectUpdated, new ObjectUpdatedMessage(new(2),
            new FurnitureState(new(Guid.NewGuid()), new(2), new("creator"), null,
                "furniture:outdoor:7000", new(1, 2, 3), new(0, 0, 0, 1)) { Stain = 255 })];
        yield return [ObjectProtocolMessageType.CreateObject, new CreateObjectRequest(Guid.NewGuid(),
            new LightCreatePayload(TestLocations.NewScene(), LightIdentity.Point, new(0, 0, 0), new(1, 1, 1), TestLocations.Default))];
        ObjectPatch[] patches =
        [
            new ActorTransformPatch(new(2, 3, 4), 0.5f, 1.5f),
            new ActorControlPatch(null, new(1, 2, 3), false, 7, false),
            new ActorControlPatch(true, null, true, null, true),
            new FurnitureTransformPatch(new(2, 3, 4), null),
            new FurnitureStainPatch(0),
            new FurnitureStainPatch(101),
            new LightPointPatch(null, new LightColor(1, 0.5f, 0.25f), 2f, 12f),
            new LightPointPatch(new(1, 2, 3), null, null, null),
        ];
        foreach (var patch in patches)
            yield return [ObjectProtocolMessageType.UpdateObject,
                new UpdateObjectRequest(Guid.NewGuid(), new(Guid.NewGuid()), new(1), patch)];
    }

    [Fact]
    public void Protocol4FurnitureCarriesTheStainOnTheWireAndRequiresIt()
    {
        var codec = new ObjectControlMessageCodec();
        Assert.Equal(6, ProtocolConstants.CurrentVersion);
        var state = new FurnitureState(new(Guid.NewGuid()), new(2), new("creator"), null,
            "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1)) { Stain = 36 };
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.ObjectUpdated),
            new ObjectUpdatedMessage(new(2), state), out var bytes, out _));
        Assert.True(codec.TryDecode(bytes, out var decoded, out _));
        Assert.Equal(36, Assert.IsType<FurnitureState>(((ObjectUpdatedMessage)decoded.Payload).State).Stain);

        var patch = new UpdateObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision, new FurnitureStainPatch(5));
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.UpdateObject) with { MessageId = patch.RequestId },
            patch, out bytes, out _));
        Assert.Contains("\"$kind\":\"furnitureStain\"", Encoding.UTF8.GetString(bytes));

        // On the wire a missing stain is not an implicit Default.
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.ObjectUpdated),
            new ObjectUpdatedMessage(new(2), state), out bytes, out _));
        var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
        var furniture = root["payload"]!["state"]!.AsObject();
        Assert.True(furniture.Remove("stain"));
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    [MemberData(nameof(Unions))]
    public void EveryMessageAndClosedUnionRoundTrips(ObjectProtocolMessageType type, object payload)
    {
        var codec = new ObjectControlMessageCodec();
        var envelope = Envelope(type) with { MessageId = MessageIdFor(payload) };

        Assert.True(codec.TryEncode(envelope, payload, out var bytes, out var error), error.ToString());
        Assert.True(codec.TryDecode(bytes, out var decoded, out error), error.ToString());

        Assert.Equal(envelope, decoded.Envelope);
        Assert.Equal(payload.GetType(), decoded.Payload.GetType());
        Assert.True(codec.TryEncode(decoded.Envelope, decoded.Payload, out var roundTrip, out error));
        Assert.Equal(bytes, roundTrip);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("isValid", json);
        Assert.DoesNotContain("isFinite", json);
        Assert.DoesNotContain("estimatedCharacters", json);
        Assert.DoesNotContain("RSyncStageCraft.Core", json);
    }

    [Theory]
    [InlineData("duplicate-envelope")]
    [InlineData("duplicate-escaped-name")]
    [InlineData("duplicate-payload")]
    [InlineData("unknown-root")]
    [InlineData("unknown-payload")]
    [InlineData("missing-envelope-field")]
    [InlineData("missing-payload-field")]
    [InlineData("numeric-enum")]
    [InlineData("unknown-enum")]
    [InlineData("wrong-case")]
    [InlineData("trailing-document")]
    [InlineData("comment")]
    [InlineData("trailing-comma")]
    [InlineData("empty")]
    [InlineData("depth")]
    public void AmbiguousOrMalformedFramesAreRejected(string mutation)
    {
        var codec = new ObjectControlMessageCodec();
        var request = new DeleteObjectRequest(Guid.NewGuid(), new(Guid.NewGuid()), new(1));
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.DeleteObject), request, out var bytes, out _));
        var json = Encoding.UTF8.GetString(bytes);
        var root = JsonNode.Parse(json).AsObject();
        switch (mutation)
        {
            case "duplicate-envelope": json = json.Replace("\"sessionId\":1", "\"sessionId\":1,\"sessionId\":2"); break;
            case "duplicate-escaped-name": json = json.Replace("\"sessionId\":1", "\"sessionId\":1,\"\\u0073essionId\":2"); break;
            case "duplicate-payload": json = json.Replace("\"baseRevision\":", "\"baseRevision\":{\"value\":1},\"baseRevision\":"); break;
            case "unknown-root": root["extra"] = true; json = root.ToJsonString(); break;
            case "unknown-payload": root["payload"]["ownerId"] = "forged"; json = root.ToJsonString(); break;
            case "missing-envelope-field": root["envelope"].AsObject().Remove("connectionEpoch"); json = root.ToJsonString(); break;
            case "missing-payload-field": root["payload"].AsObject().Remove("baseRevision"); json = root.ToJsonString(); break;
            case "numeric-enum": root["envelope"]["messageType"] = 2; json = root.ToJsonString(); break;
            case "unknown-enum": root["envelope"]["messageType"] = "loadClrType"; json = root.ToJsonString(); break;
            case "wrong-case": json = json.Replace("\"sessionId\"", "\"SessionId\""); break;
            case "trailing-document": json += "{}"; break;
            case "comment": json = "/* ignored? */" + json; break;
            case "trailing-comma": json = json[..^1] + ",}"; break;
            case "empty": json = ""; break;
            case "depth": json = new string('[', 33) + "0" + new string(']', 33); break;
        }

        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(json), out var message, out var error));
        Assert.Null(message);
        Assert.NotEqual(ObjectControlCodecError.None, error);
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("missing-kind")]
    [InlineData("computed-property")]
    [InlineData("missing-zero-coordinate")]
    [InlineData("missing-null-field")]
    [InlineData("zero-identity")]
    [InlineData("scale-out-of-range")]
    [InlineData("non-finite")]
    public void PolymorphicActorDecodeRequiresCompleteValidContract(string mutation)
    {
        var codec = new ObjectControlMessageCodec();
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.CreateObject),
            CreatePayload(), out var bytes, out _));
        var root = JsonNode.Parse(bytes).AsObject();
        var actor = root["payload"]["payload"].AsObject();
        switch (mutation)
        {
            case "unknown-kind": actor["$kind"] = "System.IO.FileInfo"; break;
            case "missing-kind": actor.Remove("$kind"); break;
            case "computed-property": actor["isValid"] = true; break;
            case "missing-zero-coordinate": actor["position"].AsObject().Remove("x"); break;
            case "missing-null-field": actor.Remove("sceneId"); break;
            case "zero-identity": actor["bNpcBaseId"] = 0; break;
            case "scale-out-of-range": actor["scale"] = 4; break;
            case "non-finite": actor["position"]["x"] = "NaN"; break;
        }

        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out var message, out _));
        Assert.Null(message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenFurnitureScaleAndLightRotationCannotCrossWire(bool light)
    {
        var payload = light
            ? new CreateObjectRequest(Guid.NewGuid(), new LightCreatePayload(TestLocations.NewScene(), LightIdentity.Point, new(0, 0, 0), new(1, 1, 1), TestLocations.Default))
            : new CreateObjectRequest(Guid.NewGuid(), new FurnitureCreatePayload(TestLocations.NewScene(), "furniture:indoor:6000", new(0, 0, 0), new(0, 0, 0, 1), TestLocations.Default));
        var codec = new ObjectControlMessageCodec();
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.CreateObject), payload, out var bytes, out _));
        var root = JsonNode.Parse(bytes);
        root["payload"]["payload"][light ? "rotation" : "scale"] = new JsonObject { ["x"] = 1 };

        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Fact]
    public void LegacyResourceAndAppearancePayloadsHaveNoRouteInProtocol6()
    {
        var codec = new ObjectControlMessageCodec();

        // A hand-built frame with a retired type name, or the protocol 5 names, is refused.
        foreach (var type in new[] { "retiredResourceManifest", "retiredHumanAppearancePayload", "resourceManifest", "humanAppearancePayload" })
        {
            Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.AcknowledgeObjects),
                new ObjectStreamAcknowledgement(new(0)), out var bytes, out _));
            var root = JsonNode.Parse(bytes)!.AsObject();
            root["envelope"]!["messageType"] = type;
            root["payload"] = new JsonObject
            {
                ["reference"] = new JsonObject { ["appearanceId"] = "appearance" },
                ["serializedState"] = "opaque",
            };
            Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out var message, out _));
            Assert.Null(message);
        }
    }

    [Fact]
    public void ByteLimitAppliesBeforeDecodeAndDuringEncodeWithoutPartialOutput()
    {
        var envelope = Envelope(ObjectProtocolMessageType.ObjectSnapshot);
        var payload = new ObjectSnapshotMessage(new(1), Enumerable.Range(0, 20)
            .Select(index => (AuthoritativeObjectState)new FurnitureState(new(Guid.NewGuid()), new(1), new("creator"), null,
                "furniture:indoor:6000", new(index, 2, 3), new(0, 0, 0, 1)))
            .ToArray(), Array.Empty<ObjectTombstone>());
        Assert.True(new ObjectControlMessageCodec().TryEncode(envelope, payload, out var bytes, out _));
        var exact = new ObjectControlMessageCodec(bytes.Length);
        var shortLimit = new ObjectControlMessageCodec(bytes.Length - 1);

        Assert.True(exact.TryEncode(envelope, payload, out var exactBytes, out _));
        Assert.Equal(bytes, exactBytes);
        Assert.True(exact.TryDecode(bytes, out _, out _));
        Assert.False(shortLimit.TryEncode(envelope, payload, out var rejectedBytes, out var error));
        Assert.Empty(rejectedBytes);
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, error);
        Assert.False(shortLimit.TryDecode(bytes, out var rejected, out error));
        Assert.Null(rejected);
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16777217)]
    public void InvalidByteLimitsAreRejected(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectControlMessageCodec(limit));

    [Fact]
    public void InvalidEnvelopeOrMismatchedPayloadCannotBeEncoded()
    {
        var codec = new ObjectControlMessageCodec();
        var envelope = Envelope(ObjectProtocolMessageType.CreateObject);
        Assert.False(codec.TryEncode(envelope with { ProtocolVersion = 99 }, CreatePayload(), out _, out var error));
        Assert.Equal(ObjectControlCodecError.InvalidEnvelope, error);
        Assert.False(codec.TryEncode(envelope, new ObjectSnapshotRequest(Guid.NewGuid()), out _, out error));
        Assert.Equal(ObjectControlCodecError.InvalidPayload, error);
        Assert.False(codec.TryEncode(envelope, default(CreateObjectRequest), out _, out _));
    }

    [Fact]
    public void DecodeDoesNotRetainBorrowedInputBuffer()
    {
        var codec = new ObjectControlMessageCodec();
        var payload = new CreateObjectRequest(Guid.NewGuid(), new FurnitureCreatePayload(TestLocations.NewScene(),
            "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1), TestLocations.Default));
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.CreateObject), payload, out var bytes, out _));
        Assert.True(codec.TryDecode(bytes, out var message, out _));

        Array.Fill(bytes, (byte)0);

        Assert.Equal(payload.ToString(), Assert.IsType<CreateObjectRequest>(message.Payload).ToString());
    }

    [Fact]
    public async Task CodecCanBeSharedByConcurrentConnections()
    {
        var codec = new ObjectControlMessageCodec();
        await Task.WhenAll(Enumerable.Range(1, 16).Select(sessionId => Task.Run(() =>
        {
            foreach (var sample in Messages())
            {
                var envelope = Envelope((ObjectProtocolMessageType)sample[0]) with { SessionId = sessionId, MessageId = MessageIdFor(sample[1]) };
                Assert.True(codec.TryEncode(envelope, sample[1], out var bytes, out _));
                Assert.True(codec.TryDecode(bytes, out var message, out _));
                Assert.Equal(envelope, message.Envelope);
            }
        })));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InvalidUtf8AndUnpairedSurrogatesFailWithoutEscapingParser(bool surrogate, bool propertyName)
    {
        var codec = new ObjectControlMessageCodec();
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.CreateObject),
            new CreateObjectRequest(Guid.NewGuid(), new FurnitureCreatePayload(TestLocations.NewScene(),
                "furniture:indoor:6000", new(1, 2, 3), new(0, 0, 0, 1), TestLocations.Default)), out var bytes, out _));
        var text = Encoding.UTF8.GetString(bytes);
        var target = propertyName ? "furnitureIdentity" : "furniture:indoor:6000";
        if (surrogate)
            bytes = Encoding.UTF8.GetBytes(text.Replace(target, "\\uD800"));
        else
            bytes[Encoding.UTF8.GetByteCount(text[..text.IndexOf(target, StringComparison.Ordinal)])] = 0xFF;

        Assert.False(codec.TryDecode(bytes, out var message, out _));
        Assert.Null(message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerShapeBoundsRejectBeforeContractMaterialization(bool array)
    {
        var codec = new ObjectControlMessageCodec();
        var json = array
            ? "[" + string.Join(',', Enumerable.Repeat("0", ProtocolLimits.MaximumObjectsPerSnapshot + 1)) + "]"
            : "{" + string.Join(',', Enumerable.Range(0, 33).Select(index => $"\"p{index}\":0")) + "}";

        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(json), out var message, out var error));
        Assert.Null(message);
        Assert.Equal(ObjectControlCodecError.InvalidJson, error);
    }

    [Fact]
    public void DuplicateSnapshotIdentityIsRejectedAfterDeserialization()
    {
        var codec = new ObjectControlMessageCodec();
        var snapshot = new ObjectSnapshotMessage(new(1), new[] { Actor() }, Array.Empty<ObjectTombstone>());
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.ObjectSnapshot), snapshot, out var bytes, out _));
        var root = JsonNode.Parse(bytes);
        var objects = root["payload"]["objects"].AsArray();
        objects.Add(objects[0].DeepClone());

        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out var message, out var error));
        Assert.Null(message);
        Assert.Equal(ObjectControlCodecError.InvalidPayload, error);
    }

    // Correlated replies must use their request ID as the envelope message ID.
    internal static Guid MessageIdFor(object payload) =>
        payload is ObjectRequestRejected rejected ? rejected.RequestId : Guid.NewGuid();

    private static SessionMessageEnvelope Envelope(ObjectProtocolMessageType type) =>
        new(ProtocolConstants.CurrentVersion, type, Guid.NewGuid(), 1, 1, 1);

    private static CreateObjectRequest CreatePayload(uint bnpcBaseId = 1) => new(Guid.NewGuid(),
        new ActorCreatePayload(TestLocations.NewScene(), bnpcBaseId, new(0, 0, 0), 0, 1, TestLocations.Default));

    private static ActorState Actor() => new(new(Guid.NewGuid()), new(1), new("owner"), null,
        1, new(0, 0, 0), 0, 1, ActorControlState.Default);
}
