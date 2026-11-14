using System.Text;
using System.Text.Json.Nodes;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>Protocol 5: point light contracts, wire form and the authoritative store.</summary>
public sealed class LightProtocolTests
{
    private static readonly RscUserId Owner = new("owner");

    private static LightState State(
        string identity = LightIdentity.Point,
        Vector3State? scale = null,
        LightColor? color = null,
        float intensity = 1f,
        float range = 8f) =>
        new(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), Owner, TestLocations.SharedScene,
            identity, new Vector3State(1, 2, 3), scale ?? LightLimits.UnitScale)
        {
            Location = TestLocations.Default,
            Color = color ?? LightColor.White,
            Intensity = intensity,
            Range = range,
        };

    private static LightCreatePayload Create(string identity = LightIdentity.Point) =>
        new(TestLocations.SharedScene, identity, new Vector3State(1, 2, 3), LightLimits.UnitScale, TestLocations.Default);

    [Fact]
    public void ProtocolFiveIsCurrentAndOnlyLightPointIsCanonical()
    {
        Assert.Equal(7, ProtocolConstants.CurrentVersion);
        Assert.Equal("light:point", LightIdentity.Point);
        Assert.True(LightIdentity.IsCanonical("light:point"));
        Assert.False(LightIdentity.IsCanonical("light:spot"));
        Assert.False(LightIdentity.IsCanonical("Light:Point"));
        Assert.False(LightIdentity.IsCanonical("point"));
        Assert.False(LightIdentity.IsCanonical(null));
        Assert.False(State(identity: "light:spot").IsValid);
        Assert.True(State().IsValid);
    }

    [Theory]
    [InlineData(1f, 1f, 1f, true)]
    [InlineData(0f, 0f, 1f, true)]
    [InlineData(0f, 0f, 0f, false)]   // black: not a light
    [InlineData(1.01f, 0f, 0f, false)]
    [InlineData(-0.01f, 1f, 0f, false)]
    [InlineData(float.NaN, 1f, 1f, false)]
    [InlineData(1f, float.PositiveInfinity, 1f, false)]
    public void ColourChannelsAreBetweenZeroAndOneAndNotAllZero(float r, float g, float b, bool valid)
    {
        var color = new LightColor(r, g, b);
        Assert.Equal(valid, color.IsValid);
        Assert.Equal(valid, State(color: color).IsValid);
        Assert.Equal(valid, (Create() with { Color = color }).IsValid);
        Assert.Equal(valid, new LightPointPatch(null, color, null, null).IsValid);
    }

    [Theory]
    [InlineData(0.05f, true)]
    [InlineData(16f, true)]
    [InlineData(0.049f, false)]
    [InlineData(16.01f, false)]
    [InlineData(float.NaN, false)]
    public void IntensityIsBounded(float intensity, bool valid)
    {
        Assert.Equal(valid, LightLimits.IsValidIntensity(intensity));
        Assert.Equal(valid, State(intensity: intensity).IsValid);
        Assert.Equal(valid, (Create() with { Intensity = intensity }).IsValid);
        Assert.Equal(valid, new LightPointPatch(null, null, intensity, null).IsValid);
    }

    [Theory]
    [InlineData(0.5f, true)]
    [InlineData(64f, true)]
    [InlineData(0.49f, false)]
    [InlineData(64.1f, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void RangeIsBounded(float range, bool valid)
    {
        Assert.Equal(valid, LightLimits.IsValidRange(range));
        Assert.Equal(valid, State(range: range).IsValid);
        Assert.Equal(valid, (Create() with { Range = range }).IsValid);
        Assert.Equal(valid, new LightPointPatch(null, null, null, range).IsValid);
    }

    [Fact]
    public void ScaleIsFixedAtOneAndAPatchNeedsAtLeastOneValidField()
    {
        Assert.False(State(scale: new Vector3State(2, 2, 2)).IsValid);
        Assert.False(State(scale: new Vector3State(1, 1, 1.0001f)).IsValid);
        Assert.False((Create() with { Scale = new Vector3State(8, 8, 8) }).IsValid);
        Assert.False(Create("light:spot").IsValid);

        Assert.False(new LightPointPatch(null, null, null, null).IsValid);
        Assert.True(new LightPointPatch(new Vector3State(1, 2, 3), null, null, null).IsValid);
        Assert.False(new LightPointPatch(new Vector3State(float.NaN, 0, 0), null, null, null).IsValid);
        Assert.True(new LightPointPatch(new Vector3State(1, 2, 3), new LightColor(0.2f, 0.4f, 1f), 2f, 12f).IsValid);
    }

    [Fact]
    public void LightFieldsCrossTheWireAndAreRequired()
    {
        var codec = new ObjectControlMessageCodec();
        var state = State(color: new LightColor(1f, 0.5f, 0.25f), intensity: 2.5f, range: 12f) with
        {
            SceneId = null,
            Location = null,
        };
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.ObjectUpdated),
            new ObjectUpdatedMessage(new ServerSequence(2), state), out var bytes, out _));
        Assert.True(codec.TryDecode(bytes, out var decoded, out _));
        var back = Assert.IsType<LightState>(((ObjectUpdatedMessage)decoded.Payload).State);
        Assert.Equal((state.Color, state.Intensity, state.Range), (back.Color, back.Intensity, back.Range));

        foreach (var field in new[] { "color", "intensity", "range" })
        {
            var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
            Assert.True(root["payload"]!["state"]!.AsObject().Remove(field), field);
            Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _), field);
        }

        var patch = new UpdateObjectRequest(Guid.NewGuid(), state.ObjectId, state.Revision,
            new LightPointPatch(null, null, null, 12f));
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.UpdateObject) with { MessageId = patch.RequestId },
            patch, out bytes, out _));
        Assert.Contains("\"$kind\":\"lightPoint\"", Encoding.UTF8.GetString(bytes));
        Assert.True(codec.TryDecode(bytes, out decoded, out _));
        Assert.Equal(12f, Assert.IsType<LightPointPatch>(((UpdateObjectRequest)decoded.Payload).Patch).Range);
    }

    [Fact]
    public void OutOfBoundsOrNonCanonicalLightsCannotCrossTheWire()
    {
        var codec = new ObjectControlMessageCodec();
        var state = State() with { SceneId = null, Location = null };
        Assert.True(codec.TryEncode(Envelope(ObjectProtocolMessageType.ObjectUpdated),
            new ObjectUpdatedMessage(new ServerSequence(2), state), out var bytes, out _));

        var mutations = new (string Field, JsonNode Value)[]
        {
            ("range", JsonValue.Create(100f)),
            ("intensity", JsonValue.Create(0f)),
            ("lightIdentity", JsonValue.Create("light:spot")),
            ("scale", new JsonObject { ["x"] = 2f, ["y"] = 2f, ["z"] = 2f }),
            ("color", new JsonObject { ["r"] = 0f, ["g"] = 0f, ["b"] = 0f }),
        };
        foreach (var (field, value) in mutations)
        {
            var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
            var payload = root["payload"]!["state"]!.AsObject();
            Assert.True(payload.ContainsKey(field), field);
            payload[field] = value;
            Assert.False(codec.TryDecode(Encoding.UTF8.GetBytes(root.ToJsonString()), out _, out _), field);
        }
    }

    private static SessionMessageEnvelope Envelope(ObjectProtocolMessageType type) =>
        new(ProtocolConstants.CurrentVersion, type, Guid.NewGuid(), 1, 1, 1);
}
