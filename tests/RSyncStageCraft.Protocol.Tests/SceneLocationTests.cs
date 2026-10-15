using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Tests;

public sealed class SceneLocationTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SceneLocation Limsa = new(128, null);
    private static readonly SceneLocation MistHouse = new(339, new HousingLocation(WorldId: 40, Ward: 7, Plot: 5, Room: 0, Division: 1));

    [Fact]
    public void LocationValidityRequiresTerritoryAndCompleteHousing()
    {
        Assert.True(Limsa.IsValid);
        Assert.True(MistHouse.IsValid);
        Assert.False(new SceneLocation(0, null).IsValid);
        Assert.False(new SceneLocation(339, new HousingLocation(0, 7, 5, 0, 1)).IsValid);
        Assert.False(new SceneLocation(339, new HousingLocation(40, 0, 5, 0, 1)).IsValid);
        Assert.False(new SceneLocation(339, new HousingLocation(40, 31, 0, 0, 1)).IsValid);
        // A house's division follows from its plot; anything else is ambiguous.
        Assert.False(new SceneLocation(339, new HousingLocation(40, 7, 5, 0)).IsValid);
        Assert.False(new SceneLocation(339, new HousingLocation(40, 7, 5, 0, 2)).IsValid);
        Assert.True(new SceneLocation(339, new HousingLocation(40, 3, 42, 0, 2)).IsValid);
        // A workshop belongs to a plot and has no room; an apartment has no plot.
        Assert.True(new SceneLocation(423, new HousingLocation(40, 7, 5, 0, 1, Workshop: true)).IsValid);
        Assert.False(new SceneLocation(423, new HousingLocation(40, 7, 5, 5, 1, Workshop: true)).IsValid);
        Assert.False(new SceneLocation(423, new HousingLocation(40, 3, 0, 0, 1, Workshop: true)).IsValid);
        Assert.True(new SceneLocation(608, new HousingLocation(40, 3, 0, 42, 2)).IsValid);
        Assert.False(new SceneLocation(608, new HousingLocation(40, 3, 0, 42, 3)).IsValid);
        // Without a plot (outdoors or an apartment building) the division is
        // always named: an apartment room in division 0 is an address no
        // client could ever match.
        Assert.False(new SceneLocation(608, new HousingLocation(40, 3, 0, 42, 0)).IsValid);
        Assert.False(new SceneLocation(339, new HousingLocation(40, 3, 0, 0, 0)).IsValid);
        Assert.True(new SceneLocation(339, new HousingLocation(40, 3, 0, 0, 1)).IsValid);
        Assert.True(new SceneLocation(339, new HousingLocation(40, 3, 0, 0, 2)).IsValid);
        Assert.True(new SceneLocation(608, new HousingLocation(40, 3, 0, 0, 1)).IsValid);
        Assert.False(new SceneLocation(608, new HousingLocation(40, 3, 0, 0, 1, Workshop: true)).IsValid);
    }

    [Fact]
    public void CreatesRequireASceneAndACompleteLocation()
    {
        // Protocol 3 fails closed: an object can never be created unlocated.
        Assert.False(Furniture(sceneId: null, Limsa).IsValid);
        Assert.True(Furniture(new RscSceneId(Guid.NewGuid()), Limsa).IsValid);
        Assert.False(Furniture(new RscSceneId(Guid.NewGuid()), location: null).IsValid);
        Assert.False(Furniture(new RscSceneId(Guid.NewGuid()), new SceneLocation(0, null)).IsValid);
    }

    [Fact]
    public void DocumentsStoreTheLocationOncePerScene()
    {
        var sceneId = new RscSceneId(Guid.NewGuid());
        var state = new FurnitureState(
            new RscObjectId(Guid.NewGuid()),
            new ObjectRevision(1),
            new RscUserId("a"),
            sceneId,
            "furniture:indoor:6000",
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1));
        var document = new SceneDocument(SceneFormat.CurrentVersion, sceneId, 1, SavedAt,
            [SceneObjectRecord.FromState(state)], [], MistHouse);

        Assert.True(document.IsValid);
        Assert.False((document with { Objects = [SceneObjectRecord.FromState(state with { Location = MistHouse })] }).IsValid);
    }

    [Fact]
    public void DocumentWithInvalidLocationIsInvalidAndLocationAffectsContentEquality()
    {
        var sceneId = new RscSceneId(Guid.NewGuid());
        var document = new SceneDocument(SceneFormat.CurrentVersion, sceneId, 1, SavedAt, [], [], MistHouse);

        Assert.True(document.IsValid);
        Assert.False((document with { Location = new SceneLocation(0, null) }).IsValid);
        Assert.False(document.HasSameAuthoritativeContent(document with { Location = Limsa }));
        Assert.True(document.HasSameAuthoritativeContent(document with { SavedAtUtc = SavedAt.AddDays(1) }));
    }

    [Fact]
    public void CreateRequestWithLocationRoundTripsThroughCodec()
    {
        var codec = new ObjectControlMessageCodec();
        var envelope = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.CreateObject,
            Guid.NewGuid(),
            1,
            1,
            1);
        var request = new CreateObjectRequest(Guid.NewGuid(), Furniture(new RscSceneId(Guid.NewGuid()), MistHouse));

        Assert.True(codec.TryEncode(envelope, request, out var bytes, out var error), error.ToString());
        Assert.True(codec.TryDecode(bytes, out var decoded, out error), error.ToString());

        var payload = Assert.IsType<FurnitureCreatePayload>(Assert.IsType<CreateObjectRequest>(decoded!.Payload).Payload);
        Assert.Equal(MistHouse, payload.Location);
    }

    private static FurnitureCreatePayload Furniture(RscSceneId? sceneId, SceneLocation? location) => new(
        sceneId,
        "furniture:indoor:6000",
        new Vector3State(0, 0, 0),
        new RotationState(0, 0, 0, 1),
        location);
}
