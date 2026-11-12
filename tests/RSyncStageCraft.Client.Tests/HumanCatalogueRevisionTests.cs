#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-5 M2 revision r1: phase 1's base-only scope (U-1), the readback's
/// applied-field comparison (U-2), the deformer re-check before the show
/// (U-5), the spawn steps (U-6) and the same-process marker (item 7).
/// Synthetic data only: no game, no IPC, no owner data.
/// </summary>
public sealed class HumanCatalogueRevisionTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------- U-1: phase 1 ----------

    [Fact]
    public async Task PhaseOneRequiresTheBaseSkeletonAndTheDeformerButNotTheHair()
    {
        var fixture = HumanCatalogueFixture.Create(hair: true);
        folders.Add(fixture.Folder);
        var (ready, refusal) = await HumanCatalogueSpawnPreparation.PrepareAsync(HumanCatalogueFixture.Label, false, new HumanCatalogueStore(fixture.Root),
            fixture.Current(), default);
        Assert.True(ready is not null, refusal);
        try
        {
            Assert.Contains(HumanCatalogueFixture.HairSkeleton, ready!.Entry.Skeleton!.Members);
            Assert.Equal(2, ready.PhaseOneRequired.Count);
            Assert.Contains(FakeHumanActorPenumbra.Skeleton, ready.PhaseOneRequired);
            Assert.Contains(SkeletonResourcePaths.DeformerGamePath, ready.PhaseOneRequired);
            Assert.DoesNotContain(HumanCatalogueFixture.HairSkeleton, ready.PhaseOneRequired);
            // The hair redirect is still packaged, so phase 2 requires it.
            Assert.True(ready.Package.Paths.ContainsKey(HumanCatalogueFixture.HairSkeleton));
        }
        finally
        {
            ready?.Abandon();
        }
    }

    private static readonly IReadOnlyList<string> Stored =
    [
        "skeleton partials: 3",
        "skeleton 0: bones 100, names 0a, parents 0b, reference pose 0c",
        "skeleton 1: bones 40, names 1a, parents 1b, reference pose 1c",
        "skeleton 2: bones 12, names 2a, parents 2b, reference pose 2c",
    ];

    [Fact]
    public void ThePhaseOneSkeletonCheckIsTheBasePartialOnly()
    {
        // The player's current face and hair differ from the saved ones, and there is one partial fewer.
        IReadOnlyList<string> current =
        [
            "skeleton partials: 2",
            "skeleton 0: bones 100, names 0a, parents 0b, reference pose 0c",
            "skeleton 1: bones 41, names 1d, parents 1e, reference pose 1f",
        ];
        Assert.Null(HumanCatalogueSkeletonSummary.BaseMismatch(Stored, current));
        Assert.NotNull(HumanCatalogueSkeletonSummary.Mismatch(Stored, current));
        Assert.Null(HumanCatalogueSkeletonSummary.Mismatch(Stored, Stored));

        var otherBase = current.Select(line => line.Replace("names 0a", "names 0d", StringComparison.Ordinal)).ToList();
        var why = HumanCatalogueSkeletonSummary.BaseMismatch(Stored, otherBase);
        Assert.StartsWith("the actor's base skeleton differs from the entry's (", why, StringComparison.Ordinal);
        Assert.Contains("partial 0 differs in names", why, StringComparison.Ordinal);
        Assert.DoesNotContain("0d", why, StringComparison.Ordinal);
        Assert.Equal("the actor's base skeleton is not read yet", HumanCatalogueSkeletonSummary.BaseMismatch(Stored, ["skeleton partials: 0"]));
    }

    // ---------- U-2: the readback ----------

    private const string Reference =
        "{\"FileVersion\":1," +
        "\"Equipment\":{\"Head\":{\"ItemId\":7,\"Crest\":true,\"Apply\":true,\"ApplyStain\":true,\"ApplyCrest\":true,\"Stain\":3,\"Stain2\":0}," +
        "\"Hands\":{\"ItemId\":8,\"Apply\":true,\"ApplyStain\":true,\"Stain\":0,\"Stain2\":0}," +
        "\"Hat\":{\"Show\":true,\"Apply\":true}}," +
        "\"Bonus\":{\"Glasses\":{\"BonusId\":0,\"Apply\":true}}," +
        "\"Customize\":{\"Race\":{\"Value\":1},\"Clan\":{\"Value\":1,\"Apply\":true},\"Hairstyle\":{\"Value\":5,\"Apply\":true}," +
        "\"Wetness\":{\"Value\":false,\"Apply\":true}}," +
        "\"Parameters\":{\"SkinDiffuse\":{\"Red\":1,\"Green\":0.5,\"Blue\":0.25,\"Apply\":true}}," +
        "\"Materials\":{\"00010203\":{\"Enabled\":true,\"Mode\":\"Legacy\",\"DiffuseR\":1},\"00010204\":{\"Mode\":\"Legacy\",\"DiffuseR\":0}}}";

    private static string With(string from, string to)
    {
        Assert.Contains(from, Reference, StringComparison.Ordinal);
        return Reference.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdenticalReadbackIsEqualAndCountsTheUnappliedFields()
    {
        var result = HumanGlamourerComparison.Compare(Reference, Reference)!;
        Assert.Empty(result.Differing);
        // Race's value (no Apply marker) and the disabled material.
        Assert.Equal(2, result.Unapplied);
    }

    public static TheoryData<string, string> Unapplied => new()
    {
        // The race's value is not applied (the match covers it).
        { "\"Race\":{\"Value\":1}", "\"Race\":{\"Value\":2}" },
        // A crest the slot does not apply (no ApplyCrest marker).
        { "\"Hands\":{\"ItemId\":8,\"Apply\":true", "\"Hands\":{\"ItemId\":8,\"Crest\":true,\"Apply\":true" },
        // A material the entry does not enable.
        { "\"00010204\":{\"Mode\":\"Legacy\",\"DiffuseR\":0}", "\"00010204\":{\"Mode\":\"Legacy\",\"DiffuseR\":0.75}" },
    };

    [Theory]
    [MemberData(nameof(Unapplied))]
    public void AFieldTheEntryDoesNotApplyNeedNotBeEqual(string from, string to)
    {
        var result = HumanGlamourerComparison.Compare(Reference, With(from, to))!;
        Assert.Empty(result.Differing);
    }

    public static TheoryData<string, string, string> Applied => new()
    {
        { "\"ItemId\":7", "\"ItemId\":9", "Equipment" },
        { "\"Stain\":3", "\"Stain\":4", "Equipment" },
        { "\"Crest\":true,\"Apply\":true,\"ApplyStain\":true,\"ApplyCrest\":true", "\"Apply\":true,\"ApplyStain\":true,\"ApplyCrest\":true", "Equipment" },
        { "\"Hat\":{\"Show\":true", "\"Hat\":{\"Show\":false", "Equipment" },
        { "\"Hairstyle\":{\"Value\":5", "\"Hairstyle\":{\"Value\":6", "Customize" },
        { "\"Wetness\":{\"Value\":false", "\"Wetness\":{\"Value\":true", "Customize" },
        { "\"BonusId\":0", "\"BonusId\":4", "Bonus" },
        { "\"Green\":0.5", "\"Green\":0.6", "Parameters" },
        { "\"DiffuseR\":1}", "\"DiffuseR\":0.5}", "Materials" },
        // An application marker that differs: the readback applies something else.
        { "\"Clan\":{\"Value\":1,\"Apply\":true}", "\"Clan\":{\"Value\":1}", "Customize" },
        // A material the readback enables that the entry does not.
        { "\"00010204\":{\"Mode\"", "\"00010204\":{\"Enabled\":true,\"Mode\"", "Materials" },
        { "\"FileVersion\":1", "\"FileVersion\":2", "FileVersion" },
        // An entry present on one side only.
        { "\"Glasses\":{\"BonusId\":0,\"Apply\":true}", "\"Glasses\":{\"BonusId\":0,\"Apply\":true},\"Mask\":{\"BonusId\":0,\"Apply\":true}", "Bonus" },
    };

    [Theory]
    [MemberData(nameof(Applied))]
    public void AnAppliedFieldThatDiffersNamesItsComponent(string from, string to, string component)
    {
        var result = HumanGlamourerComparison.Compare(Reference, With(from, to))!;
        Assert.Equal([component], result.Differing);
    }

    [Fact]
    public void AComponentOnOneSideOnlyDiffersExceptMaterialsWithNothingEnabled()
    {
        Assert.Equal(["Unknown"], HumanGlamourerComparison.Compare(Reference, With("\"FileVersion\":1,", "\"FileVersion\":1,\"Unknown\":{},"))!.Differing);
        var noMaterials = Reference[..Reference.IndexOf(",\"Materials\"", StringComparison.Ordinal)] + "}";
        Assert.Equal(["Materials"], HumanGlamourerComparison.Compare(Reference, noMaterials)!.Differing);
        var disabledOnly = With("\"00010203\":{\"Enabled\":true,", "\"00010203\":{");
        Assert.Empty(HumanGlamourerComparison.Compare(disabledOnly, noMaterials)!.Differing);
        Assert.Null(HumanGlamourerComparison.Compare("[]", Reference));
        Assert.Null(HumanGlamourerComparison.Compare(Reference, "not json"));
    }

    [Fact]
    public void AnEntryIsAppliedOnlyWithTheVerifiedFlags()
    {
        var stage = new HumanActorGlamourerStage(new FakeHumanActorGlamourer(new FakeHumanActorScene()), _ => { }, "RSC HV-5 ");
        Assert.Equal("glamourer: an entry is applied with flags 0x06 only, not 0x02", stage.UseEntry(new HumanGlamourerPayload("c3RhdGU=", 0x02, "{}")));
        Assert.Null(stage.UseEntry(new HumanGlamourerPayload("c3RhdGU=", 0x06, "{}")));
    }

    // ---------- U-5: the deformer re-check ----------

    private static string? Answer(HumanCatalogueDeformerRecheck check)
    {
        string? answer = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            answer = check.Poll(out var pending);
            return !pending;
        }, TimeSpan.FromSeconds(10)));
        return answer;
    }

    private HumanCatalogueFixture Fixture()
    {
        var fixture = HumanCatalogueFixture.Create();
        folders.Add(fixture.Folder);
        return fixture;
    }

    private static HumanCatalogueEntry EntryOf(HumanCatalogueFixture fixture) =>
        new HumanCatalogueStore(fixture.Root).FindEntry(HumanCatalogueFixture.Label, out _)!;

    private static string DeformerFile(HumanCatalogueFixture fixture) =>
        fixture.Resources.Single(resource => resource.GamePaths.Contains(FakeHumanActorPenumbra.Deformer)).ActualPath;

    [Fact]
    public void AnUnchangedDeformerPassesTheReCheck()
    {
        var fixture = Fixture();
        var check = new HumanCatalogueDeformerRecheck(EntryOf(fixture), () => fixture.Current(), default);
        Assert.Equal("the deformer re-check was not begun", check.Poll(out _));
        check.Begin();
        Assert.Null(Answer(check));
    }

    [Fact]
    public void ADeformerWhoseContentChangedBeforeTheReCheckIsRefused()
    {
        var fixture = Fixture();
        File.WriteAllBytes(DeformerFile(fixture), HumanCatalogueFixture.Pbd([101, 1801, 201]));
        var check = new HumanCatalogueDeformerRecheck(EntryOf(fixture), () => fixture.Current(), default);
        check.Begin();
        Assert.Equal("deformer differs from the current one (GAP-K4)", Answer(check));
    }

    [Fact]
    public void ADeformerThatChangesDuringTheReCheckIsRefused()
    {
        var fixture = Fixture();
        var file = DeformerFile(fixture);
        var reads = 0;
        var check = new HumanCatalogueDeformerRecheck(EntryOf(fixture), () =>
        {
            // The second read (after the comparison) sees a newer file.
            if (++reads == 2)
            {
                File.WriteAllBytes(file, HumanCatalogueFixture.Pbd([101, 1801, 201]));
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
            }
            return fixture.Current();
        }, default);
        check.Begin();
        Assert.Equal("the player's deformer changed after the re-check began (GAP-K4)", Answer(check));
    }

    [Fact]
    public void EachBeginStartsANewReCheckAndThePollAnswersTheLatest()
    {
        var fixture = Fixture();
        var check = new HumanCatalogueDeformerRecheck(EntryOf(fixture), () => fixture.Current(), default);
        check.Begin();
        Assert.Null(Answer(check));
        // U-4: after a redraw the check is begun again, on the deformer as it is then.
        File.WriteAllBytes(DeformerFile(fixture), HumanCatalogueFixture.Pbd([101, 1801, 201]));
        check.Begin();
        Assert.Equal("deformer differs from the current one (GAP-K4)", Answer(check));
    }

    [Fact]
    public void AResourceReadThatFailsRefusesTheReCheck()
    {
        var fixture = Fixture();
        var check = new HumanCatalogueDeformerRecheck(EntryOf(fixture), () => null, default);
        check.Begin();
        Assert.Equal("the player's resources could not be read again, so the deformer cannot be compared (GAP-K4)", Answer(check));
    }

    // ---------- U-6: the spawn steps ----------

    [Theory]
    [InlineData("spawn Rsc One", "Rsc One", false)]
    [InlineData("spawn Rsc One test-scale", "Rsc One test-scale", false)]
    [InlineData("spawn test-scale", "test-scale", false)]
    [InlineData("spawn-test-scale Rsc One", "Rsc One", true)]
    [InlineData("spawn-test-scale Rsc One test-scale", "Rsc One test-scale", true)]
    [InlineData("spawn-test-scale spawn-test-scale", "spawn-test-scale", true)]
    public void EveryLabelIsAddressableAndTheTestScaleIsItsOwnStep(string step, string label, bool testScale)
    {
        Assert.True(HumanCatalogueSpawnCommand.TryParse(step, out var parsed, out var scaled));
        Assert.Equal((label, testScale), (parsed, scaled));
        // Each parsed label is valid under the unchanged label rule.
        Assert.Equal(label, HumanCatalogueLabel.Normalize(parsed, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("spawn")]
    [InlineData("spawn ")]
    [InlineData("spawn-test-scale")]
    [InlineData("spawnRsc One")]
    [InlineData("capture Rsc One")]
    public void OtherTextIsNotASpawnStep(string? step) => Assert.False(HumanCatalogueSpawnCommand.TryParse(step, out _, out _));

    // ---------- item 7: the same-process marker ----------

    [Fact]
    public void TheMarkerNamesOnlyThisGameProcessAndNeverAPath()
    {
        var earlier = Environment.GetEnvironmentVariable(HumanCatalogueRetentionMarker.Variable);
        try
        {
            var game = new HumanCatalogueProcess(91_001, 638_000_000_000_000_123);
            Environment.SetEnvironmentVariable(HumanCatalogueRetentionMarker.Variable, null);
            Assert.False(HumanCatalogueRetentionMarker.IsSet(game, out _));
            Assert.True(HumanCatalogueRetentionMarker.TrySet(game, 3));
            Assert.True(HumanCatalogueRetentionMarker.IsSet(game, out var count));
            Assert.Equal(3, count);
            Assert.Equal("hv5-retained-1 91001 638000000000000123 3", Environment.GetEnvironmentVariable(HumanCatalogueRetentionMarker.Variable));
            // A later, smaller retention keeps the larger count.
            Assert.True(HumanCatalogueRetentionMarker.TrySet(game, 1));
            Assert.True(HumanCatalogueRetentionMarker.IsSet(game, out count));
            Assert.Equal(3, count);
            // Another process (an inherited environment) is not this one.
            Assert.False(HumanCatalogueRetentionMarker.IsSet(game with { Id = 91_002 }, out _));
            Assert.False(HumanCatalogueRetentionMarker.IsSet(game with { StartTicksUtc = 1 }, out _));
            // A marker that cannot be read fails closed.
            Environment.SetEnvironmentVariable(HumanCatalogueRetentionMarker.Variable, "something else");
            Assert.True(HumanCatalogueRetentionMarker.IsSet(game, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HumanCatalogueRetentionMarker.Variable, earlier);
        }
    }
}
#endif
