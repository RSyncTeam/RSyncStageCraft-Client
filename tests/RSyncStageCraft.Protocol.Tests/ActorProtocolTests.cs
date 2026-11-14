#nullable enable
using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

/// <summary>Protocol 6 actor contract, codec and capacity policy.</summary>
public sealed class ActorProtocolTests
{
    private static readonly RscUserId Owner = new("owner");

    [Fact]
    public void Protocol7IsCurrentAndProtocols5And6AreRefused()
    {
        Assert.Equal(7, ProtocolConstants.CurrentVersion);
        var codec = new ObjectControlMessageCodec();
        var envelope = new SessionMessageEnvelope(5, ObjectProtocolMessageType.CreateObject, Guid.NewGuid(), 1, 1, 1);
        Assert.False(codec.TryEncode(envelope, Create(), out _, out _));
        Assert.False(codec.TryEncode(envelope with { ProtocolVersion = 6 }, Create(), out _, out _));
    }

    [Fact]
    public void EveryActorFieldAndPatchRoundTrips()
    {
        var codec = new ObjectControlMessageCodec();
        var state = new ActorState(new(Guid.NewGuid()), new(3), Owner, TestLocations.NewScene(), 4242,
            new(1.5f, -2, 3), 0.75f, 2.5f, new ActorControlState(true, new(4, 5, 6), 31, 7))
        {
            Location = TestLocations.Default,
        };
        object[] payloads =
        [
            new ObjectCreatedMessage(new(1), state),
            Create(),
            new UpdateObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision, new ActorTransformPatch(new(9, 8, 7), -1.25f, 0.5f)),
            new UpdateObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision, new ActorControlPatch(false, null, true, 12, false)),
        ];
        ObjectProtocolMessageType[] types =
        [
            ObjectProtocolMessageType.ObjectCreated,
            ObjectProtocolMessageType.CreateObject,
            ObjectProtocolMessageType.UpdateObject,
            ObjectProtocolMessageType.UpdateObject,
        ];
        for (var index = 0; index < payloads.Length; index++)
        {
            var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, types[index], Guid.NewGuid(), 1, 1, 1);
            Assert.True(codec.TryEncode(envelope, payloads[index], out var bytes, out var error), error.ToString());
            Assert.True(codec.TryDecode(bytes, out var decoded, out error), error.ToString());
            Assert.Equal(payloads[index].ToString(), decoded!.Payload.ToString());
        }
    }

    [Theory]
    [InlineData("actorType")]
    [InlineData("modelIdentity")]
    [InlineData("resourceManifest")]
    [InlineData("humanAppearance")]
    [InlineData("rotation")]
    public void RemovedActorFieldsCannotCrossTheWire(string removedField)
    {
        var codec = new ObjectControlMessageCodec();
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.CreateObject,
            Guid.NewGuid(), 1, 1, 1);
        Assert.True(codec.TryEncode(envelope, Create(), out var bytes, out _));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain($"\"{removedField}\"", text, StringComparison.Ordinal);
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["payload"]!["payload"]![removedField] = "smuggled";
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Theory]
    [InlineData("actorType")]
    [InlineData("modelIdentity")]
    [InlineData("resourceManifest")]
    [InlineData("humanAppearance")]
    [InlineData("appearance")]
    [InlineData("rotation")]
    public void RemovedActorFieldsCannotBeDecodedInAStateOrAPatch(string removedField)
    {
        var codec = new ObjectControlMessageCodec();
        var state = new ActorState(new(Guid.NewGuid()), new(3), Owner, TestLocations.NewScene(), 4242,
            new(1, 2, 3), 0.5f, 1f, ActorControlState.Default) { Location = TestLocations.Default };
        (ObjectProtocolMessageType Type, object Payload, string Marker)[] cases =
        [
            (ObjectProtocolMessageType.ObjectCreated, new ObjectCreatedMessage(new(1), state), "bNpcBaseId"),
            (ObjectProtocolMessageType.UpdateObject,
                new UpdateObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision, new ActorTransformPatch(new(9, 8, 7), 1f, 2f)),
                "yaw"),
        ];
        foreach (var (type, payload, marker) in cases)
        {
            var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, Guid.NewGuid(), 1, 1, 1);
            Assert.True(codec.TryEncode(envelope, payload, out var bytes, out var error), error.ToString());
            Assert.DoesNotContain($"\"{removedField}\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            var root = JsonNode.Parse(bytes)!;
            var holder = FindObjectWith(root, marker);
            Assert.NotNull(holder);
            holder![removedField] = "smuggled";
            Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
        }
    }

    private static JsonObject? FindObjectWith(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject value:
                if (value.ContainsKey(key))
                    return value;
                foreach (var child in value)
                {
                    if (FindObjectWith(child.Value, key) is { } found)
                        return found;
                }
                return null;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (FindObjectWith(child, key) is { } found)
                        return found;
                }
                return null;
            default:
                return null;
        }
    }

    [Theory]
    [InlineData("bNpcBaseId")]
    [InlineData("position")]
    [InlineData("yaw")]
    [InlineData("scale")]
    public void EveryActorCreateFieldIsRequired(string field)
    {
        var codec = new ObjectControlMessageCodec();
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.CreateObject,
            Guid.NewGuid(), 1, 1, 1);
        Assert.True(codec.TryEncode(envelope, Create(), out var bytes, out _));
        var root = JsonNode.Parse(bytes)!.AsObject();
        Assert.True(root["payload"]!["payload"]!.AsObject().Remove(field));
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Fact]
    public void BoundsAreEnforcedByTheContract()
    {
        Assert.True(ActorLimits.IsValidScale(0.25f));
        Assert.True(ActorLimits.IsValidScale(3f));
        Assert.False(ActorLimits.IsValidScale(0.249f));
        Assert.False(ActorLimits.IsValidScale(3.01f));
        Assert.False(ActorLimits.IsValidScale(float.NaN));
        Assert.True(ActorLimits.IsValidYaw(MathF.PI));
        Assert.False(ActorLimits.IsValidYaw(-MathF.PI));
        Assert.False(ActorLimits.IsValidYaw(4f));
        Assert.False(ActorLimits.IsValidPosition(new(100_001, 0, 0)));
        Assert.False(new ActorControlState(false, null, 0, 0).IsValid);
        Assert.False(new ActorControlState(false, new(float.NaN, 0, 0), null, 0).IsValid);
        Assert.Equal(-MathF.PI / 2, ActorLimits.NormalizeYaw(3 * MathF.PI / 2), 4);
        Assert.False(new ActorTransformPatch(null, null, 4f).IsValid);
        Assert.False(new ActorControlPatch(null, null, false, null, false).IsValid);
        Assert.False(new ActorCreatePayload(TestLocations.NewScene(), 1, new(0, 0, 0), 0, 0.1f, TestLocations.Default).IsValid);
    }

    [Fact]
    public void TheReservedLookAtFieldDefaultsToEmpty()
    {
        Assert.Null(ActorControlState.Default.LookAt);
        Assert.True(ActorControlState.Default.IsDefault);
    }

    [Theory]
    [InlineData("isLoop", "false")]
    [InlineData("loop", "false")]
    [InlineData("oneShot", "true")]
    [InlineData("mode", "\"once\"")]
    public void AClientCannotRedefineAnAllowlistEntrysBehaviour(string field, string value)
    {
        // The wire carries a row id only; its loop behaviour comes from the
        // compiled allowlist entry. Any extra field is refused at decode.
        var codec = new ObjectControlMessageCodec();
        var request = new UpdateObjectRequest(Guid.NewGuid(), new(Guid.NewGuid()), new(1),
            new ActorControlPatch(null, null, false, 600, false));
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.UpdateObject,
            request.RequestId, 1, 1, 1);
        Assert.True(codec.TryEncode(envelope, request, out var bytes, out _));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["payload"]!["patch"]![field] = JsonNode.Parse(value);
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Fact]
    public void ClientCannotSetTheAnimationSequence()
    {
        var codec = new ObjectControlMessageCodec();
        var request = new UpdateObjectRequest(Guid.NewGuid(), new(Guid.NewGuid()), new(1),
            new ActorControlPatch(null, null, false, 5, false));
        var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.UpdateObject,
            request.RequestId, 1, 1, 1);
        Assert.True(codec.TryEncode(envelope, request, out var bytes, out _));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["payload"]!["patch"]!["animationSequence"] = 99;
        Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _));
    }

    [Fact]
    public void ActorsOfTheAbsoluteCapacityFitTheStructures()
    {
        // The design capacity (30) fits every bounded structure, so raising
        // the active limit later needs no format change (a post-release
        // decision).
        var scene = TestLocations.NewScene();
        var actors = Enumerable.Range(0, ActorCapacityPolicy.AbsoluteMaximumActorsPerScene)
            .Select(index => (AuthoritativeObjectState)new ActorState(new(Guid.NewGuid()), new(1), Owner, scene, (uint)index + 1,
                new(index, 0, 0), 0, 1, ActorControlState.Default))
            .ToArray();
        var snapshot = new ObjectSnapshotMessage(new(1), actors, Array.Empty<ObjectTombstone>());
        Assert.True(snapshot.IsValid);
        var document = new SceneDocument(SceneFormat.CurrentVersion, scene, 1, DateTimeOffset.UtcNow,
            actors.Select(SceneObjectRecord.FromState).ToArray(), Array.Empty<ObjectTombstone>());
        Assert.True(document.IsValid);
        var codec = new ObjectControlMessageCodec();
        Assert.True(codec.TryEncode(new SessionMessageEnvelope(ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.ObjectSnapshot, Guid.NewGuid(), 1, 1, 1), snapshot, out _, out var error), error.ToString());
    }

    [Fact]
    public void AuthorizationCarriesTheSignedInUserAndStaysCompatibleWithoutIt()
    {
        var withUser = new AuthorizeMessage(3, 4, 5, "alice");
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(AuthenticationMessageCodec.EncodeAuthorization(withUser), out var read));
        Assert.Equal(withUser, read);
        var withoutUser = new AuthorizeMessage(3, 4, 5);
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(AuthenticationMessageCodec.EncodeAuthorization(withoutUser), out read));
        Assert.Equal(withoutUser, read);
        var longest = new AuthorizeMessage(3, 4, 5, new string('"', ProtocolLimits.MaximumIdentifierLength),
            new string('"', RscDisplayName.MaximumUtf8Bytes));
        var bytes = AuthenticationMessageCodec.EncodeAuthorization(longest);
        Assert.InRange(bytes.Length, 1, AuthenticationMessageCodec.MaximumResponseBytes);
        Assert.True(AuthenticationMessageCodec.TryReadAuthorization(bytes, out read));
        Assert.Equal(longest, read);
        Assert.Throws<ArgumentException>(() => AuthenticationMessageCodec.EncodeAuthorization(
            new AuthorizeMessage(3, 4, 5, "alice", new string('x', RscDisplayName.MaximumUtf8Bytes + 1))));
        Assert.False(AuthenticationMessageCodec.TryReadAuthorization(
            Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"authorized\",\"sessionId\":1,\"generation\":1,\"connectionEpoch\":1,\"userId\":\" \"}"),
            out _));
    }

    private static CreateObjectRequest Create(RscSceneId? scene = null, uint bnpcBaseId = 4242) => new(Guid.NewGuid(),
        new ActorCreatePayload(scene ?? TestLocations.NewScene(), bnpcBaseId, new(1, 2, 3), 0.5f, 1.25f, TestLocations.Default));
}
