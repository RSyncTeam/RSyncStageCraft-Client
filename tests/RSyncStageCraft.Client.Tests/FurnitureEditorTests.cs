using System.Numerics;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Location;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class LocationScenesTests
{
    private static readonly SceneLocation House = new(339, new HousingLocation(40, 7, 5, 0, 1));

    [Fact]
    public void EveryPlaceHasItsOwnStableScene()
    {
        var places = new SceneLocation[]
        {
            new(128, null),
            new(129, null),
            new(339, new HousingLocation(40, 7, 0, 0, 1)),
            new(339, new HousingLocation(40, 7, 0, 0, 2)),
            House,
            House with { Housing = new HousingLocation(74, 7, 5, 0, 1) },
            House with { Housing = new HousingLocation(40, 4, 5, 0, 1) },
            House with { Housing = new HousingLocation(40, 7, 6, 0, 1) },
            House with { Housing = new HousingLocation(40, 7, 5, 1, 1) },
            new(423, new HousingLocation(40, 7, 5, 0, 1, Workshop: true)),
            new(608, new HousingLocation(40, 7, 0, 0, 1)),
            new(608, new HousingLocation(40, 7, 0, 0, 2)),
            new(608, new HousingLocation(40, 7, 0, 42, 2)),
        };

        var scenes = places.Select(place => LocationScenes.SceneFor(place)!.Value).ToArray();

        Assert.All(scenes, scene => Assert.True(scene.IsValid));
        Assert.Equal(scenes.Length, scenes.Distinct().Count());
        Assert.Equal(scenes[Array.IndexOf(places, House)], LocationScenes.SceneFor(new SceneLocation(339, new HousingLocation(40, 7, 5, 0, 1))));
        Assert.Null(LocationScenes.SceneFor(new SceneLocation(0, null)));
        Assert.Null(LocationScenes.SceneFor(new SceneLocation(339, new HousingLocation(40, 7, 5, 0, 2))));
        Assert.Equal("t=339;w=40;ward=7;plot=5;room=0;div=1;ws=0", LocationScenes.Canonical(House));
    }

    [Fact]
    public void VisibilityRequiresTheExactKnownPlace()
    {
        SceneLocation? here = House;
        var visibility = new CurrentLocationVisibility(() => here);
        var chair = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"),
            LocationScenes.SceneFor(House), "furniture:indoor:6000", new(0, 0, 0), new(0, 0, 0, 1)) { Location = House };

        Assert.True(visibility.IsVisibleHere(chair));
        Assert.False(visibility.IsVisibleHere(chair with { Location = null }));
        Assert.False(visibility.IsVisibleHere(chair with { Location = new SceneLocation(339, new HousingLocation(40, 7, 6, 0, 1)) }));
        here = null;
        Assert.False(visibility.IsVisibleHere(chair));
        here = new SceneLocation(0, null);
        Assert.False(visibility.IsVisibleHere(chair));
        Assert.False(new CurrentLocationVisibility(() => throw new InvalidOperationException()).IsVisibleHere(chair));
    }
}

public sealed class SceneChangeWatcherTests
{
    [Fact]
    public void OnlyAChangedSceneLocationCounts()
    {
        var changes = new List<SceneLocation?>();
        var watcher = new SceneChangeWatcher(changes.Add);
        ClientLocation Outdoors(int rawPlot, int rawDivision) =>
            ClientLocation.FromOutdoor(339, 40, rawWard: 6, rawPlot, rawDivision)!;

        Assert.True(watcher.Observe(Outdoors(rawPlot: 4, rawDivision: 1)));
        // Plot 5 -> street -> plot 6 of the same ward division: same scene.
        Assert.False(watcher.Observe(Outdoors(rawPlot: -1, rawDivision: 1)));
        Assert.False(watcher.Observe(Outdoors(rawPlot: 5, rawDivision: 1)));
        Assert.Equal(new SceneLocation(339, new HousingLocation(40, 7, 0, 0, 1)), watcher.Current);
        // Another division, unknown location and back are changes.
        Assert.True(watcher.Observe(Outdoors(rawPlot: 35, rawDivision: 2)));
        Assert.True(watcher.Observe(null));
        Assert.Null(watcher.Current);
        Assert.False(watcher.Observe(null));
        Assert.True(watcher.Observe(Outdoors(rawPlot: 4, rawDivision: 1)));

        Assert.Equal(4, changes.Count);
        Assert.Equal(new SceneLocation(339, new HousingLocation(40, 7, 0, 0, 2)), changes[1]);
        Assert.Null(changes[2]);
    }

    [Fact]
    public void TheFirstSampleAlwaysCountsEvenWhenUnknown()
    {
        var changes = 0;
        var watcher = new SceneChangeWatcher(_ => changes++);

        Assert.True(watcher.Observe(null));
        Assert.Equal(1, changes);
    }
}

public sealed class FurnitureEditorTests
{
    private static readonly SceneLocation House = new(339, new HousingLocation(40, 7, 5, 0, 1));

    [Fact]
    public async Task PlaceCreatesIntoThePlaceSceneWithTheCompleteLocation()
    {
        var sent = new List<object>();
        var editor = Editor(sent);

        var result = await editor.PlaceAsync(House, "furniture:indoor:6000", new Vector3(1, 2, 3), MathF.PI);

        Assert.True(result.Completed);
        var create = Assert.IsType<CreateObjectRequest>(Assert.Single(sent));
        Assert.True(create.IsValid);
        var payload = Assert.IsType<FurnitureCreatePayload>(create.Payload);
        Assert.Equal(LocationScenes.SceneFor(House), payload.SceneId);
        Assert.Equal(House, payload.Location);
        Assert.Equal("furniture:indoor:6000", payload.FurnitureIdentity);
        Assert.Equal(new Vector3State(1, 2, 3), payload.Position);
        Assert.Equal(180, FurnitureEditor.YawDegrees(payload.Rotation), 3);
    }

    [Fact]
    public async Task NothingIsSentWithoutAKnownPlaceOrACanonicalItem()
    {
        var sent = new List<object>();
        var editor = Editor(sent);

        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceAsync(null, "furniture:indoor:6000", Vector3.Zero, 0)).Status);
        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceAsync(new SceneLocation(0, null), "furniture:indoor:6000", Vector3.Zero, 0)).Status);
        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceAsync(House, "chair", Vector3.Zero, 0)).Status);
        Assert.Equal(ServerCommandStatus.Invalid, (await editor.PlaceAsync(House, "furniture:indoor:6000", new Vector3(float.NaN, 0, 0), 0)).Status);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task NudgeAndDeleteTargetTheCurrentRevision()
    {
        var sent = new List<object>();
        var editor = Editor(sent);
        var chair = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(4), new RscUserId("a"),
            LocationScenes.SceneFor(House), "furniture:indoor:6000", new(1, 2, 3), FurnitureEditor.Yaw(0)) { Location = House };

        await editor.NudgeAsync(chair, new Vector3(0.5f, 0, 0), 0);
        await editor.NudgeAsync(chair, Vector3.Zero, MathF.PI / 2);
        var nothing = await editor.NudgeAsync(chair, Vector3.Zero, 0);
        await editor.DeleteAsync(chair);

        Assert.Equal(ServerCommandStatus.Invalid, nothing.Status);
        Assert.Equal(3, sent.Count);
        var move = Assert.IsType<UpdateObjectRequest>(sent[0]);
        Assert.Equal(chair.ObjectId, move.ObjectId);
        Assert.Equal(new ObjectRevision(4), move.BaseRevision);
        var movePatch = Assert.IsType<FurnitureTransformPatch>(move.Patch);
        Assert.Equal(new Vector3State(1.5f, 2, 3), movePatch.Position);
        Assert.Null(movePatch.Rotation);
        var turn = Assert.IsType<FurnitureTransformPatch>(Assert.IsType<UpdateObjectRequest>(sent[1]).Patch);
        Assert.Null(turn.Position);
        Assert.Equal(90, FurnitureEditor.YawDegrees(turn.Rotation!.Value), 3);
        var delete = Assert.IsType<DeleteObjectRequest>(sent[2]);
        Assert.Equal(chair.ObjectId, delete.ObjectId);
        Assert.Equal(new ObjectRevision(4), delete.BaseRevision);
    }

    [Fact]
    public void RequestIdsMatchWhatTheServerEchoes()
    {
        var create = new CreateObjectRequest(Guid.NewGuid(), new FurnitureCreatePayload(
            LocationScenes.SceneFor(House), "furniture:indoor:6000", new(0, 0, 0), new(0, 0, 0, 1), House));
        var update = new UpdateObjectRequest(Guid.NewGuid(), new RscObjectId(Guid.NewGuid()), new ObjectRevision(1),
            new FurnitureTransformPatch(new(1, 1, 1), null));
        var delete = new DeleteObjectRequest(Guid.NewGuid(), new RscObjectId(Guid.NewGuid()), new ObjectRevision(1));

        Assert.Equal(create.ClientRequestId, ServerCommandRequests.RequestIdOf(create));
        Assert.Equal(update.RequestId, ServerCommandRequests.RequestIdOf(update));
        Assert.Equal(delete.RequestId, ServerCommandRequests.RequestIdOf(delete));
        Assert.Equal(Guid.Empty, ServerCommandRequests.RequestIdOf(new ObjectSnapshotRequest(Guid.NewGuid())));
        Assert.Equal(Guid.Empty, ServerCommandRequests.RequestIdOf(create with { ClientRequestId = Guid.Empty }));
    }

    private static FurnitureEditor Editor(List<object> sent) => new((request, _) =>
    {
        sent.Add(request);
        return Task.FromResult(new ServerCommandResult(
            ServerCommandStatus.Completed,
            new ObjectMutationCompletion(Guid.NewGuid(), ObjectMutationCompletionKind.Create, false, new ServerSequence(1), null, null)));
    });
}
