using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

public sealed class FurnitureIdentityTests
{
    [Theory]
    [InlineData("furniture:indoor:6000", FurnitureSource.Indoor, 6000u)]
    [InlineData("furniture:outdoor:1", FurnitureSource.Outdoor, 1u)]
    [InlineData("furniture:indoor:4294967295", FurnitureSource.Indoor, uint.MaxValue)]
    public void CanonicalFormsParse(string identity, FurnitureSource source, uint itemId)
    {
        Assert.True(FurnitureIdentity.TryParse(identity, out var parsedSource, out var parsedItem));
        Assert.Equal(source, parsedSource);
        Assert.Equal(itemId, parsedItem);
        Assert.Equal(identity, FurnitureIdentity.Format(source, itemId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chair")]
    [InlineData("furniture:indoor:")]
    [InlineData("furniture:indoor:0")]
    [InlineData("furniture:indoor:06000")]
    [InlineData("furniture:indoor:+6000")]
    [InlineData("furniture:indoor:-6000")]
    [InlineData("furniture:indoor: 6000")]
    [InlineData("furniture:indoor:6000 ")]
    [InlineData("furniture:Indoor:6000")]
    [InlineData("FURNITURE:indoor:6000")]
    [InlineData("furniture:attic:6000")]
    [InlineData("furniture:indoor:4294967296")]
    [InlineData("furniture:indoor:60x0")]
    [InlineData("furniture:indoor:6000:1")]
    public void AnythingElseIsRefused(string? identity)
    {
        Assert.False(FurnitureIdentity.TryParse(identity, out _, out _));
        Assert.False(FurnitureIdentity.IsCanonical(identity));
    }

    [Fact]
    public void ProtocolThreeContractsRequireTheCanonicalForm()
    {
        var scene = TestLocations.NewScene();
        var payload = new FurnitureCreatePayload(scene, "chair", new(0, 0, 0), new(0, 0, 0, 1), TestLocations.Default);
        var state = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"),
            scene, "chair", new(0, 0, 0), new(0, 0, 0, 1)) { Location = TestLocations.Default };

        // Creates, received states and restored records all use one rule.
        Assert.False(payload.IsValid);
        Assert.True((payload with { FurnitureIdentity = "furniture:outdoor:7" }).IsValid);
        Assert.False(state.IsValid);
        Assert.True((state with { FurnitureIdentity = "furniture:outdoor:7" }).IsValid);

        // A client cannot even encode one, and a peer's message carrying one
        // does not decode.
        var codec = new ObjectControlMessageCodec();
        var envelope = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion, ObjectProtocolMessageType.CreateObject, Guid.NewGuid(), 1, 1, 1);
        Assert.False(codec.TryEncode(envelope, new CreateObjectRequest(Guid.NewGuid(), payload), out _, out _));
        var good = new CreateObjectRequest(Guid.NewGuid(), payload with { FurnitureIdentity = "furniture:indoor:6000" });
        Assert.True(codec.TryEncode(envelope, good, out var bytes, out _));
        var tampered = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(bytes).Replace("furniture:indoor:6000", "chair", StringComparison.Ordinal));
        Assert.False(codec.TryDecode(tampered, out _, out _));
    }
}
