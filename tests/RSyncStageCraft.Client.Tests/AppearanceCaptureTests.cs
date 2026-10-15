using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>Planning a capture locally (pure apart from reading the named files); nothing is sent.</summary>
public sealed class AppearanceCaptureTests : IDisposable
{
    private static readonly RscUserId Owner = new("alice");
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-capture", Guid.NewGuid().ToString("N"));

    public AppearanceCaptureTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task OnlyModdedAllowlistedFilesAreCapturedAndEverythingElseIsCountedOrExcluded()
    {
        var model = Write("model.mdl", RandomNumberGenerator.GetBytes(5000));
        var material = Write("material.mtrl", RandomNumberGenerator.GetBytes(700));
        var texture = Write("texture.tex", RandomNumberGenerator.GetBytes(9000));
        var shader = Write("shader.shpk", RandomNumberGenerator.GetBytes(300));
        var empty = Write("empty.tex", []);
        var missing = Path.Combine(root, "missing.tex");
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "glamourer-state-base64",
        [
            new(model, ["chara/equipment/e0001/model/c0101e0001_top.mdl"]),
            // One file serving two game paths: two entries, one blob.
            new(material, ["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl", "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_b.mtrl"]),
            new(texture, ["chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex", "chara/Equipment/UPPER.tex"]),
            new(shader, ["shader/sm5/shpk/character.shpk"]),
            new(empty, ["chara/x/empty.tex"]),
            new(missing, ["chara/x/missing.tex"]),
            // Vanilla (actual path is the game path) and a swap (another game path).
            new("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl", ["chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl"]),
            new("chara/equipment/e0002/model/c0101e0002_glv.mdl", ["chara/equipment/e0003/model/c0101e0003_glv.mdl"]),
        ], PenumbraModDirectory: root);

        using var plan = await AppearanceCapturePlanner.PlanAsync(Owner, read, Path.Combine(root, "work"));

        Assert.True(plan.CanUpload);
        Assert.False(plan.StateOnly);
        Assert.Equal(4, plan.FileCount);
        Assert.Equal(3, plan.DistinctFiles);
        Assert.Equal(new Dictionary<string, int> { [".mdl"] = 1, [".mtrl"] = 2, [".tex"] = 1 }, plan.FilesByType);
        Assert.Equal(1, plan.VanillaPaths);
        Assert.Equal(1, plan.SwappedPaths);
        Assert.True(plan.PenumbraMetaExcluded);
        Assert.Equal(
            new[]
            {
                ("chara/Equipment/UPPER.tex", AppearanceCaptureExclusion.InvalidGamePath),
                ("chara/x/empty.tex", AppearanceCaptureExclusion.Empty),
                ("chara/x/missing.tex", AppearanceCaptureExclusion.OutsideModDirectory),
                ("shader/sm5/shpk/character.shpk", AppearanceCaptureExclusion.TypeNotAllowed),
            },
            plan.Excluded.Select(item => (item.GamePath, item.Reason)).OrderBy(item => item.GamePath, StringComparer.Ordinal));
        Assert.Equal(Encoding.UTF8.GetByteCount("glamourer-state-base64") + 5000 + 700 + 9000, plan.TotalBytes);
        Assert.Null(plan.Manifest!.PenumbraMeta);
        Assert.Null(plan.Manifest.CustomizePlus);
        Assert.Equal(Owner, plan.Manifest.OwnerId);

        // Local disk paths never appear in anything shown or logged.
        foreach (var text in Texts(plan))
            Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
        var disclosure = AppearanceCaptureText.Disclosure(plan);
        Assert.Contains("actual contents", disclosure, StringComparison.Ordinal);
        Assert.Contains("never sent", disclosure, StringComparison.Ordinal);
        Assert.Contains(AppearanceCaptureText.Warnings(plan), warning => warning.Contains("meta changes are not included", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASavedDesignIsStateOnlyAndNeverImpliesItsModsAreIncluded()
    {
        var model = Write("model.mdl", RandomNumberGenerator.GetBytes(500));
        // Even if a reader returned resources, a saved design carries none.
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.SavedDesign, "design-base64",
            [new(model, ["chara/x/body.mdl"])], "Evening look");

        using var plan = await AppearanceCapturePlanner.PlanAsync(Owner, read, Path.Combine(root, "work"));

        Assert.True(plan.CanUpload);
        Assert.True(plan.StateOnly);
        Assert.Empty(plan.Manifest!.Files);
        Assert.Equal(0, plan.FileCount);
        Assert.False(plan.PenumbraMetaExcluded);
        Assert.Contains(AppearanceCaptureText.StateOnlyLabel, AppearanceCaptureText.Title(plan), StringComparison.Ordinal);
        var disclosure = AppearanceCaptureText.Disclosure(plan);
        Assert.Contains("No Penumbra mod files are captured or uploaded", disclosure, StringComparison.Ordinal);
        Assert.Contains("will not be reproduced", disclosure, StringComparison.Ordinal);
        Assert.DoesNotContain("modded file", disclosure, StringComparison.Ordinal);
        Assert.Contains(AppearanceCaptureText.Warnings(plan), warning => warning.Contains("state only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConflictingFilesForOneGamePathRefuseTheCaptureButIdenticalOnesCountOnce()
    {
        var content = RandomNumberGenerator.GetBytes(800);
        var first = Write("a.tex", content);
        var copy = Write("b.tex", content);
        var other = Write("c.tex", RandomNumberGenerator.GetBytes(800));

        using (var identical = await AppearanceCapturePlanner.PlanAsync(Owner, CurrentCharacter(
            new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry(copy, ["chara/x/skin.tex"])), Path.Combine(root, "work")))
        {
            Assert.True(identical.CanUpload);
            Assert.Equal(1, identical.FileCount);
            Assert.Equal(1, identical.DistinctFiles);
        }

        using var conflicting = await AppearanceCapturePlanner.PlanAsync(Owner, CurrentCharacter(
            new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry(other, ["chara/x/skin.tex"])), Path.Combine(root, "work"));
        Assert.False(conflicting.CanUpload);
        Assert.Null(conflicting.Manifest);
        Assert.Equal(AppearanceCaptureRefusal.ConflictingGamePath, conflicting.Refusal);
        Assert.Equal("chara/x/skin.tex", conflicting.ConflictingGamePath);
        Assert.Contains("chara/x/skin.tex", AppearanceCaptureText.Refusal(conflicting.Refusal, conflicting.ConflictingGamePath), StringComparison.Ordinal);
        Assert.Empty(conflicting.Sources);
    }

    [Fact]
    public async Task LimitsAndAMissingStateRefuseTheCapture()
    {
        var tiny = Write("tiny.tex", [1, 2, 3]);
        var tooMany = Enumerable.Range(0, AppearanceLimits.MaximumFiles + 1).Select(index => $"chara/x/f{index}.tex").ToArray();
        using (var plan = await AppearanceCapturePlanner.PlanAsync(Owner, CurrentCharacter(new AppearanceResourceEntry(tiny, tooMany)), Path.Combine(root, "work")))
            Assert.Equal(AppearanceCaptureRefusal.TooManyFiles, plan.Refusal);

        using (var plan = await AppearanceCapturePlanner.PlanAsync(Owner,
            new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "", []), Path.Combine(root, "work")))
            Assert.Equal(AppearanceCaptureRefusal.NoGlamourerState, plan.Refusal);

        using (var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("   "),
            new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "state", []), Path.Combine(root, "work")))
            Assert.Equal(AppearanceCaptureRefusal.InvalidManifest, plan.Refusal);

        Assert.Empty(Directory.Exists(Path.Combine(root, "work")) ? Directory.GetFiles(Path.Combine(root, "work")) : []);
    }

    [Fact]
    public async Task TheStateWorkingFileExistsOnlyForTheCapture()
    {
        var work = Path.Combine(root, "work");
        var plan = await AppearanceCapturePlanner.PlanAsync(Owner,
            new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "state text", []), work);
        var file = Assert.Single(Directory.GetFiles(work));
        Assert.Equal("state text", await File.ReadAllTextAsync(file));
        Assert.Equal(file, plan.Sources[plan.Manifest!.Glamourer.Hash]);
        plan.Dispose();
        Assert.Empty(Directory.GetFiles(work));
        Assert.Empty(plan.Sources);
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private AppearanceCaptureRead CurrentCharacter(params AppearanceResourceEntry[] resources) =>
        new(AppearanceCaptureSource.CurrentCharacter, "glamourer-state", resources, PenumbraModDirectory: root);

    private static IEnumerable<string> Texts(AppearanceCapturePlan plan) =>
        new[] { plan.ToString(), AppearanceCaptureText.Title(plan), AppearanceCaptureText.Disclosure(plan) }
            .Concat(AppearanceCaptureText.Warnings(plan))
            .Concat(plan.Excluded.Select(item => item.ToString()))
            .Concat(plan.Manifest?.Files.Select(file => file.ToString()) ?? []);
}
