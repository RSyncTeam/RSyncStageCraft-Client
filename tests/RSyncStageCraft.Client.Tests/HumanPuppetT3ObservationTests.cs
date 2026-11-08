#if RSC_DEVELOPMENT
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// T3-1M's observation shaping (P1-M): skeleton-resource entries from a
/// synthetic per-object resource map, hashed path strings, and the puppet and
/// player comparisons. Synthetic paths only: no game, no IPC, no file read.
/// </summary>
public sealed class HumanPuppetT3ObservationTests
{
    private const string Base = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    private const string HairPhysics = "chara/human/c0101/skeleton/hair/h0115/phy_c0101h0115.phyb";
    private const string ReportedDeformer = "chara/xls/boneDeformer/human.pbd";
    private const string ModFile = @"D:\SyntheticMods\ModA\skeleton.sklb";

    private static Dictionary<string, string> Lines(IReadOnlyList<KeyValuePair<string, string>> lines) =>
        lines.ToDictionary(line => line.Key, line => line.Value, StringComparer.Ordinal);

    [Fact]
    public void AHashedPathIsTheShortSha256OfTheExactReportedString()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModFile)))[..16].ToLowerInvariant();
        Assert.Equal(expected, HumanPuppetT3Observations.HashPath(ModFile));
        Assert.Equal(16, expected.Length);
        // No normalization: a change of case or separator is a different string, so a different hash.
        Assert.NotEqual(expected, HumanPuppetT3Observations.HashPath(ModFile.ToLowerInvariant()));
        Assert.NotEqual(expected, HumanPuppetT3Observations.HashPath(ModFile.Replace('\\', '/')));
    }

    [Fact]
    public void EachResourceIsClassifiedAsRedirectedVanillaOrSwapped()
    {
        Assert.Equal("redirected", HumanPuppetT3Observations.Kind(ModFile, Base));
        Assert.Equal("redirected", HumanPuppetT3Observations.Kind(@"\\share\mods\a.sklb", Base));
        Assert.Equal("redirected", HumanPuppetT3Observations.Kind("Z:/mods/a.sklb", Base));
        Assert.Equal("vanilla", HumanPuppetT3Observations.Kind(Base, Base));
        Assert.Equal("vanilla", HumanPuppetT3Observations.Kind("chara/xls/bonedeformer/human.pbd", ReportedDeformer));
        Assert.Equal("swapped", HumanPuppetT3Observations.Kind("chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb", Base));
    }

    [Fact]
    public void OnlySkeletonResourcesAreDescribedWithTheirFamiliesAndNoFileSystemPath()
    {
        var map = new Dictionary<string, HashSet<string>>
        {
            [ModFile] = [Base],
            [@"D:\SyntheticMods\ModA\human.pbd"] = [ReportedDeformer],
            [HairPhysics] = [HairPhysics],
            ["chara/human/c0101/skeleton/base/b0001/swap.skp"] = ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.SKP"],
            [@"D:\SyntheticMods\ModA\body.mdl"] = ["chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl"],
            [@"D:\SyntheticMods\ModA\body.tex"] = ["chara/human/c0101/obj/body/b0001/texture/c0101b0001_d.tex"],
        };
        var lines = HumanPuppetT3Observations.DescribeSkeletonResources("puppet", map);
        var byKey = Lines(lines);
        Assert.Equal("4 game paths, 4 actual paths", byKey["puppet skeleton resources"]);
        Assert.Equal($"redirected, actual {HumanPuppetT3Observations.HashPath(ModFile)}", byKey["puppet skeleton resource skeleton base c0101 0001"]);
        Assert.StartsWith("redirected, actual ", byKey["puppet skeleton resource deformer"], StringComparison.Ordinal);
        Assert.StartsWith("vanilla, actual ", byKey["puppet skeleton resource physics hair c0101 0115"], StringComparison.Ordinal);
        // .skp is counted but not a classified family; mixed case is not lowercased.
        Assert.Single(byKey.Keys, key => key.StartsWith("puppet skeleton resource other .skp ", StringComparison.Ordinal));
        Assert.Equal(5, lines.Count);
        Assert.DoesNotContain(lines, line => line.Value.Contains("SyntheticMods", StringComparison.Ordinal) ||
            line.Value.Contains('\\') || line.Key.Contains('\\') || line.Value.Contains("chara/", StringComparison.Ordinal));
        // Ordinal order after the count line.
        var keys = lines.Skip(1).Select(line => line.Key).ToArray();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Fact]
    public void NoMapIsNoneAndAMapWithoutSkeletonResourcesIsCountedAsZero()
    {
        Assert.Equal("none", Assert.Single(HumanPuppetT3Observations.DescribeSkeletonResources("puppet", null)).Value);
        var empty = HumanPuppetT3Observations.DescribeSkeletonResources("puppet",
            new Dictionary<string, HashSet<string>> { ["a.tex"] = ["chara/common/texture/a.tex"] });
        Assert.Equal("0 game paths, 0 actual paths", Assert.Single(empty).Value);
    }

    [Fact]
    public void SeveralActualPathsForOneResourceAreSortedOnOneLine()
    {
        var map = new Dictionary<string, HashSet<string>>
        {
            [@"D:\SyntheticMods\B\human.pbd"] = [ReportedDeformer],
            ["chara/xls/bonedeformer/human.pbd"] = ["chara/xls/bonedeformer/human.pbd"],
        };
        var value = Lines(HumanPuppetT3Observations.DescribeSkeletonResources("player", map))["player skeleton resource deformer"];
        var parts = value.Split("; ");
        Assert.Equal(2, parts.Length);
        Assert.Equal(parts.Order(StringComparer.Ordinal), parts);
    }

    [Fact]
    public void ResourcesAreComparedByFamilyBetweenThePuppetAndThePlayer()
    {
        var puppet = HumanPuppetT3Observations.DescribeSkeletonResources("puppet", new Dictionary<string, HashSet<string>>
        {
            [ModFile] = [Base],
            [ReportedDeformer] = [ReportedDeformer],
            [HairPhysics] = [HairPhysics],
        });
        var player = HumanPuppetT3Observations.DescribeSkeletonResources("player", new Dictionary<string, HashSet<string>>
        {
            [ModFile] = [Base],
            [@"D:\SyntheticMods\ModA\human.pbd"] = [ReportedDeformer],
        });
        Assert.Equal("skeleton resources compared with the player: same 1, different 1, puppet only 1, player only 0; " +
            "different: deformer; puppet only: physics hair c0101 0115.",
            HumanPuppetT3Observations.CompareSkeletonResources([.. puppet, .. player]));
        Assert.EndsWith("not comparable (the puppet's report has no map from Penumbra: none, unanswered or missing).", HumanPuppetT3Observations.CompareSkeletonResources(
            HumanPuppetT3Observations.DescribeSkeletonResources("puppet", null), player), StringComparison.Ordinal);
    }

    [Fact]
    public void SkeletonsAreComparedPerPartialByField()
    {
        const string a = "file 0000000000000001, bones 60, names 0000000000000002, parents 0000000000000003, reference pose 0000000000000004";
        var puppet = new KeyValuePair<string, string>[] { new("puppet skeleton partials", "2"), new("puppet skeleton 0", a),
            new("puppet skeleton 1", a.Replace("bones 60", "bones 61", StringComparison.Ordinal).Replace("pose 0000000000000004", "pose 0000000000000005", StringComparison.Ordinal)) };
        var player = new KeyValuePair<string, string>[] { new("player skeleton partials", "3"), new("player skeleton 0", a),
            new("player skeleton 1", a), new("player skeleton 2", "no resource") };
        Assert.Equal("skeleton compared with the player: partials 2 and 3; partial 0 same; partial 1 differs in bones, reference pose; partial 2 player only.",
            HumanPuppetT3Observations.CompareSkeletons(puppet, player));
        Assert.Equal("skeleton compared with the player: not comparable (puppet no human draw object, player read).",
            HumanPuppetT3Observations.CompareSkeletons([new("puppet skeleton", "no human draw object")], player));
    }
}
#endif
