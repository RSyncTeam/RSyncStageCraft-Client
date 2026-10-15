using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class SceneObjectLoadProgressTests
{
    private static FurnitureState At(SceneLocation location) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"), TestLocations.SharedScene,
        "furniture:indoor:6000", new Vector3State(0, 0, 0), new RotationState(0, 0, 0, 1)) { Location = location };

    private static LightState LightAt(SceneLocation location) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"), TestLocations.SharedScene,
        LightIdentity.Point, new Vector3State(0, 0, 0), LightLimits.UnitScale) { Location = location };

    private static FurnitureUnsupportedReport Report(AuthoritativeObjectState state, LocalObjectReportKind kind) =>
        new(state.ObjectId, "x", null, "reason", 1, kind);

    [Fact]
    public void CountsOnlyDrawableFurnitureHereAndHowMuchIsDrawn()
    {
        var here = TestLocations.Default;
        var elsewhere = new SceneLocation(here.TerritoryId + 1, null);
        var first = At(here);
        var second = At(here);
        var third = At(here);
        var refused = At(here);
        var away = At(elsewhere);
        var drawn = new HashSet<RscObjectId> { first.ObjectId };

        var progress = SceneObjectLoadProgress.Compute(
            [first, second, third, refused, away], here, drawn.Contains, [refused.ObjectId]);

        Assert.Equal(new SceneObjectLoadProgress(1, 3), progress);
        Assert.True(progress.IsLoading);
        Assert.Equal("Loading scene objects: 1/3 drawn", progress.Describe());

        drawn.Add(second.ObjectId);
        drawn.Add(third.ObjectId);
        Assert.False(SceneObjectLoadProgress.Compute([first, second, third, refused], here, drawn.Contains, [refused.ObjectId]).IsLoading);
    }

    [Fact]
    public void FurnitureAndLightsAreCountedTogether()
    {
        var here = TestLocations.Default;
        var furniture = At(here);
        var light = LightAt(here);
        var awayLight = LightAt(new SceneLocation(here.TerritoryId + 1, null));
        var drawn = new HashSet<RscObjectId> { furniture.ObjectId };

        var progress = SceneObjectLoadProgress.Compute([furniture, light, awayLight], here, drawn.Contains, []);
        Assert.Equal(new SceneObjectLoadProgress(1, 2), progress);
        Assert.Equal("Loading scene objects: 1/2 drawn", progress.Describe());

        drawn.Add(light.ObjectId);
        Assert.False(SceneObjectLoadProgress.Compute([furniture, light, awayLight], here, drawn.Contains, []).IsLoading);
    }

    [Fact]
    public void RetryingLightStaysPendingWhileFailedAndUnsupportedAreExcluded()
    {
        var here = TestLocations.Default;
        var furniture = At(here);
        var unsupported = At(here);
        var retrying = LightAt(here);
        var failed = LightAt(here);
        var drawn = new HashSet<RscObjectId> { furniture.ObjectId };
        var reports = new[]
        {
            Report(unsupported, LocalObjectReportKind.NotSupported),
            Report(retrying, LocalObjectReportKind.Retrying),
            Report(failed, LocalObjectReportKind.Failed),
        };

        var excluded = SceneObjectReports.Excluded(reports);
        Assert.Equal(2, excluded.Count);
        Assert.Contains(unsupported.ObjectId, excluded);
        Assert.Contains(failed.ObjectId, excluded);
        Assert.DoesNotContain(retrying.ObjectId, excluded);

        var all = new AuthoritativeObjectState[] { furniture, unsupported, retrying, failed };
        var progress = SceneObjectLoadProgress.Compute(all, here, drawn.Contains, excluded);
        // The retrying light counts in the total, not as drawn.
        Assert.Equal(new SceneObjectLoadProgress(1, 2), progress);
        Assert.True(progress.IsLoading);

        // Once its retries are exhausted it is excluded, so the count completes.
        reports[1] = Report(retrying, LocalObjectReportKind.Failed);
        var done = SceneObjectLoadProgress.Compute(all, here, drawn.Contains, SceneObjectReports.Excluded(reports));
        Assert.Equal(new SceneObjectLoadProgress(1, 1), done);
        Assert.False(done.IsLoading);
    }

    [Fact]
    public void NothingIsShownWhileThePlaceIsUnknownOrEmpty()
    {
        var state = At(TestLocations.Default);
        Assert.False(SceneObjectLoadProgress.Compute([state], null, _ => false, []).IsLoading);
        Assert.False(SceneObjectLoadProgress.Compute([], TestLocations.Default, _ => false, []).IsLoading);
    }
}
