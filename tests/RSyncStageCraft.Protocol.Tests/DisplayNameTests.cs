#nullable enable
using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Protocol 6: identity is the stable <see cref="RscUserId"/>; display names
/// are optional, bounded presentation text that never decides ownership.
/// </summary>
public sealed class DisplayNameTests
{
    [Fact]
    public void DisplayNamesAreAtMost64Utf8BytesOfWellFormedText()
    {
        Assert.Equal(64, RscDisplayName.MaximumUtf8Bytes);
        Assert.True(RscDisplayName.IsValid(new string('a', 64)));
        Assert.False(RscDisplayName.IsValid(new string('a', 65)));
        // Measured in UTF-8 bytes: 2-byte and 4-byte characters.
        Assert.True(RscDisplayName.IsValid(new string('é', 32)));
        Assert.False(RscDisplayName.IsValid(new string('é', 33)));
        Assert.True(RscDisplayName.IsValid(string.Concat(Enumerable.Repeat("😀", 16))));
        Assert.False(RscDisplayName.IsValid(string.Concat(Enumerable.Repeat("😀", 17))));
        // Neutral sample names: no real person or character is meant.
        Assert.True(RscDisplayName.IsValid("Ada Example"));
        foreach (var invalid in new[] { null, "", " ", " Ada", "Ada ", "Ad\na", "Ad\u0007a", "\ud83d", "a\udc00b" })
            Assert.False(RscDisplayName.IsValid(invalid), invalid ?? "null");
    }

    [Fact]
    public void OwnerNamesAreDisplayOnlyAndNeverClientAuthoritative()
    {
        var codec = new ObjectControlMessageCodec();
        var actor = Actor("alice");

        // A name may accompany only an actor, only under its own owner's id, and only if valid.
        Assert.True(new ObjectCreatedMessage(new(1), actor, new(new("alice"), "Ada")).IsValid);
        Assert.False(new ObjectCreatedMessage(new(1), actor, new(new("bob"), "Ada")).IsValid);
        Assert.False(new ObjectUpdatedMessage(new(1), actor, new(new("alice"), new string('a', 65))).IsValid);
        Assert.False(new ObjectSnapshotMessage(new(1), [actor], [], [new(new("bob"), "Ada")]).IsValid);
        Assert.False(new ObjectSnapshotMessage(new(1), [actor], [], [new(new("alice"), "A"), new(new("alice"), "B")]).IsValid);

        // Client requests have no place for a name: one smuggled in is refused.
        var create = new CreateObjectRequest(Guid.NewGuid(),
            new ActorCreatePayload(new(Guid.NewGuid()), 4242, new(1, 2, 3), 0, 1, TestLocations.Default));
        var update = new UpdateObjectRequest(Guid.NewGuid(), actor.ObjectId, actor.Revision,
            new ActorTransformPatch(new(1, 1, 1), null, null));
        foreach (var (type, payload) in new (ObjectProtocolMessageType, object)[]
                 { (ObjectProtocolMessageType.CreateObject, create), (ObjectProtocolMessageType.UpdateObject, update) })
        {
            var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, type, Guid.NewGuid(), 1, 1, 1);
            Assert.True(codec.TryEncode(envelope, payload, out var bytes, out _));
            foreach (var field in new[] { "ownerName", "ownerNames", "ownerId", "displayName" })
            {
                var root = JsonNode.Parse(bytes)!;
                root["payload"]![field] = "Ada";
                Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _), field);
                root = JsonNode.Parse(bytes)!;
                (root["payload"]!["payload"] ?? root["payload"]!["patch"])![field] = "Ada";
                Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _), field);
            }
        }

        // The name round-trips on server messages, but the actor state itself
        // never holds one.
        var created = new ObjectCreatedMessage(new(1), actor, new(new("alice"), "Ada"));
        Assert.True(codec.TryEncode(new SessionMessageEnvelope(ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.ObjectCreated, Guid.NewGuid(), 1, 1, 1), created, out var sent, out _));
        Assert.True(codec.TryDecode(sent, out var decoded, out _));
        var received = Assert.IsType<ObjectCreatedMessage>(decoded!.Payload);
        Assert.Equal(created.OwnerName, received.OwnerName);
        Assert.Equal(actor, received.State);
    }

    private static ActorState Actor(string owner) => new(new(Guid.NewGuid()), new(1), new(owner), TestLocations.SharedScene,
        4242, new(1, 2, 3), 0, 1, ActorControlState.Default) { Location = TestLocations.Default };
}
