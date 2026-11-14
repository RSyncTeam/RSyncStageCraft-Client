#nullable enable
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Allowlist v2 and appearance-manifest format 2 (AM-4, step AM4-S1): format 1
/// is unchanged byte for byte and remains the only format the default reader
/// accepts; format 2 carries exactly one skeleton group for a playable race and
/// is read only on explicit opt-in. Every hash and path here is synthetic.
/// </summary>
public sealed class AppearanceManifestFormatTwoTests
{
    private static readonly RscUserId Owner = new("alice");
    private const string Top = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    private const string BaseSklb = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb";
    private const string BasePhyb = "chara/human/c1801/skeleton/base/b0001/phy_c1801b0001.phyb";
    private const string HairSklb = "chara/human/c1801/skeleton/hair/h0003/skl_c1801h0003.sklb";
    private const string Deformer = SkeletonResourcePaths.DeformerGamePath;
    private const ushort Viera = 1801;

    private static AppearanceBlobRef Blob(string content, long size) =>
        new(ContentHash.Of(Encoding.UTF8.GetBytes(content)), size);

    private static readonly AppearanceBlobRef Glamourer = Blob("g", 10);

    private static AppearanceFileEntry[] GroupFiles() =>
        [new(Top, Blob("top", 2048)), new(BaseSklb, Blob("base", 48)), new(Deformer, Blob("pbd", 88))];

    private static AppearanceBundleManifest FormatTwo(ushort race = Viera, AppearanceFileEntry[]? files = null)
    {
        var manifest = AppearanceBundleManifest.TryCreate(Owner, Glamourer, null, null, files ?? GroupFiles(), race, out var error);
        Assert.Null(error);
        return manifest!;
    }

    private static string? CreateError(ushort? race, params AppearanceFileEntry[] files)
    {
        Assert.Null(AppearanceBundleManifest.TryCreate(Owner, Glamourer, null, null, files, race, out var error));
        Assert.NotNull(error);
        return error;
    }

    // ---------- format 1 is unchanged ----------

    [Fact]
    public void AFormatOneBundleKeepsItsBytesAndItsPinnedId()
    {
        // The id was computed from the format-1 canonical text independently of
        // this code; it must never change for a bundle without a skeleton group.
        AppearanceFileEntry[] files = [new(Top, Blob("top", 2048))];
        var plain = AppearanceBundleManifest.TryCreate(Owner, Glamourer, null, null, files, out var error);
        Assert.Null(error);
        var explicitNone = AppearanceBundleManifest.TryCreate(Owner, Glamourer, null, null, files, null, out error);
        Assert.Null(error);

        Assert.Equal("5881f21b23a032ad5646ac0fe3d149eae0ff893162bbba433d3a08732cd459bd", plain!.BundleId.Value);
        Assert.Equal(plain.CanonicalBytes, explicitNone!.CanonicalBytes);
        Assert.Equal(1, plain.FormatVersion);
        Assert.Null(plain.SkeletonGroup);
        Assert.Null(plain.SkeletonGroupTargetRace);
        Assert.StartsWith("{\"formatVersion\":1,", Encoding.UTF8.GetString(plain.CanonicalBytes));
        Assert.DoesNotContain("skeletonGroup", Encoding.UTF8.GetString(plain.CanonicalBytes));
    }

    [Fact]
    public void BothReadersReadFormatOneIdentically()
    {
        var manifest = AppearanceBundleManifest.TryCreate(Owner, Glamourer, Blob("meta", 7), Blob("cplus", 9),
            [new AppearanceFileEntry(Top, Blob("top", 2048))], out var error)!;
        var byDefault = AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, out error);
        Assert.Null(error);
        var byOptIn = AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.AllowSkeletonGroups, out error);
        Assert.Null(error);
        Assert.Equal(manifest.BundleId, byDefault!.BundleId);
        Assert.Equal(manifest.BundleId, byOptIn!.BundleId);
        Assert.Equal(1, byOptIn.FormatVersion);
        Assert.Null(byOptIn.SkeletonGroup);
    }

    [Fact]
    public void AFormatOneManifestNeverCarriesASkeletonResourceOrGroup()
    {
        // Without a target race a manifest is format 1, and allowlist v1 refuses every skeleton resource.
        // The format-1 refusal text is exactly what it was before format 2 existed.
        foreach (var path in new[] { BaseSklb, BasePhyb, Deformer })
            Assert.Equal("A file type is not allowed (allowed: .mdl, .mtrl, .tex).", CreateError(null, new AppearanceFileEntry(path, Blob("x", 48))));

        // A format-1 text with a skeletonGroup member is refused by both readers.
        var text = Encoding.UTF8.GetString(AppearanceBundleManifest.TryCreate(Owner, Glamourer, null, null,
            [new AppearanceFileEntry(Top, Blob("top", 2048))], out _)!.CanonicalBytes);
        var withGroup = Encoding.UTF8.GetBytes(text[..^1] + ",\"skeletonGroup\":{\"targetRace\":1801}}");
        Assert.Null(AppearanceBundleManifest.TryParse(withGroup, out _));
        Assert.Null(AppearanceBundleManifest.TryParse(withGroup, AppearanceManifestReadOptions.AllowSkeletonGroups, out var error));
        Assert.NotNull(error);
    }

    // ---------- format 2 ----------

    [Fact]
    public void TheFormatTwoCanonicalFormIsPinned()
    {
        var manifest = FormatTwo();
        var files = string.Join(",",
            $"{{\"gamePath\":\"{Top}\",\"hash\":\"{Blob("top", 0).Hash.Value}\",\"size\":2048}}",
            $"{{\"gamePath\":\"{BaseSklb}\",\"hash\":\"{Blob("base", 0).Hash.Value}\",\"size\":48}}",
            $"{{\"gamePath\":\"{Deformer}\",\"hash\":\"{Blob("pbd", 0).Hash.Value}\",\"size\":88}}");
        var expected =
            "{\"formatVersion\":2,\"ownerId\":\"alice\"," +
            $"\"glamourer\":{{\"hash\":\"{Glamourer.Hash.Value}\",\"size\":10}}," +
            "\"penumbraMeta\":null,\"customizePlus\":null," +
            $"\"files\":[{files}],\"skeletonGroup\":{{\"targetRace\":1801}}}}";

        Assert.Equal(expected, Encoding.UTF8.GetString(manifest.CanonicalBytes));
        Assert.Equal("78ac0315fe8ade7d0776e48bcf086f96e8f2524df67ab96005bd71a31ccba91a", manifest.BundleId.Value);
        Assert.Equal(2, manifest.FormatVersion);
        Assert.Equal((ushort)Viera, manifest.SkeletonGroupTargetRace);
        Assert.Equal(new[] { BaseSklb, Deformer }, manifest.SkeletonGroup!.Members.Select(member => member.Path.GamePath));
        Assert.Equal(manifest.Files.Where(file => file.GamePath != Top).Select(file => file.Content),
            manifest.SkeletonGroup.Members.Select(member => member.Content));
    }

    [Fact]
    public void TheDefaultReaderRefusesFormatTwoAndTheOptInReadsIt()
    {
        var manifest = FormatTwo();

        Assert.Null(AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, out var error));
        Assert.Equal("Unsupported manifest format version.", error);
        Assert.Null(AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.FormatOneOnly, out error));
        Assert.Equal("Unsupported manifest format version.", error);

        var parsed = AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.AllowSkeletonGroups, out error);
        Assert.Null(error);
        Assert.Equal(manifest.BundleId, parsed!.BundleId);
        Assert.Equal(2, parsed.FormatVersion);
        Assert.Equal(manifest.SkeletonGroup!.Members, parsed.SkeletonGroup!.Members);
    }

    [Fact]
    public void NoOtherFormatTwoFormIsAccepted()
    {
        var canonical = Encoding.UTF8.GetString(FormatTwo().CanonicalBytes);
        string[] altered =
        [
            canonical.Replace("\"skeletonGroup\":{", "\"skeletonGroup\": {"),                          // whitespace
            canonical.Replace(",\"skeletonGroup\":{\"targetRace\":1801}", ""),                           // missing group
            canonical.Replace("{\"targetRace\":1801}", "null"),                                          // null group
            canonical.Replace("{\"targetRace\":1801}", "{}"),                                            // empty group
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":1801,\"extra\":1}"),            // unknown group member
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":\"1801\"}"),                    // race as text
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":1801.0}"),                      // race not an integer
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":-1}"),                          // negative race
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":1804}"),                        // NPC race
            canonical.Replace("{\"targetRace\":1801}", "{\"targetRace\":101}"),                         // race mismatch
            canonical.Replace("\"formatVersion\":2", "\"formatVersion\":1"),                             // group in format 1
            canonical.Replace("\"formatVersion\":2", "\"formatVersion\":3"),                             // unknown format
            MoveGroupFirst(canonical),                                                                   // member order
            canonical[..^1] + ",\"extra\":1}",                                                           // unknown member
            canonical.Replace(Deformer, SkeletonResourcePaths.ReportedDeformerGamePath),                 // mixed-case deformer
        ];
        foreach (var text in altered)
        {
            Assert.Null(AppearanceBundleManifest.TryParse(Encoding.UTF8.GetBytes(text), AppearanceManifestReadOptions.AllowSkeletonGroups, out var error));
            Assert.NotNull(error);
            Assert.Null(AppearanceBundleManifest.TryParse(Encoding.UTF8.GetBytes(text), out _));
        }
    }

    [Fact]
    public void AFormatTwoBundleNeedsAWholeGroupForItsRace()
    {
        AppearanceFileEntry top = new(Top, Blob("top", 2048));
        Assert.Contains("at least one skeleton resource", CreateError(Viera, top));
        Assert.Contains("at least one skeleton resource", CreateError(Viera));

        // A skeleton-looking file outside the recognised families is refused by allowlist v2.
        Assert.Contains("file type is not allowed", CreateError(Viera, top,
            new AppearanceFileEntry("chara/human/c1801/skeleton/base/b0001/skl_c1801b0002.sklb", Blob("x", 48))));
        Assert.Contains("file type is not allowed", CreateError(Viera, top, new AppearanceFileEntry("chara/human/c1801/skeleton/base/b0001/x.skp", Blob("x", 48))));
        Assert.Contains("file type is not allowed", CreateError(Viera, top, new AppearanceFileEntry("chara/x/human.pbd", Blob("x", 48))));
        Assert.Contains("file type is not allowed", CreateError(Viera, new AppearanceFileEntry("chara/action/a.pap", Blob("x", 48)),
            new AppearanceFileEntry(BaseSklb, Blob("base", 48))));
        // The mixed-case deformer spelling is never a manifest path.
        Assert.Contains("redirect is invalid", CreateError(Viera,
            new AppearanceFileEntry(SkeletonResourcePaths.ReportedDeformerGamePath, Blob("pbd", 88))));

        Assert.Contains(nameof(SkeletonGroupRefusal.RaceMismatch), CreateError(101, new AppearanceFileEntry(BaseSklb, Blob("base", 48))));
        Assert.Contains(nameof(SkeletonGroupRefusal.MoreThanOneGroup), CreateError(Viera, new AppearanceFileEntry(HairSklb, Blob("h3", 48)),
            new AppearanceFileEntry("chara/human/c1801/skeleton/hair/h0004/skl_c1801h0004.sklb", Blob("h4", 48))));
        Assert.Contains(nameof(SkeletonGroupRefusal.InconsistentSlot), CreateError(Viera, new AppearanceFileEntry(HairSklb, Blob("h3", 48)),
            new AppearanceFileEntry("chara/human/c1801/skeleton/hair/h0004/phy_c1801h0004.phyb", Blob("p4", 48))));
        Assert.Contains(nameof(SkeletonGroupRefusal.Structure), CreateError(Viera,
            new AppearanceFileEntry(BaseSklb, Blob("base", SkeletonResourceValidation.MaximumSkeletonBytes + 1))));

        var twelve = Enumerable.Range(1, 12)
            .Select(set => new AppearanceFileEntry($"chara/human/c1801/skeleton/hair/h{set:D4}/skl_c1801h{set:D4}.sklb", Blob($"h{set}", 48)))
            .ToArray();
        Assert.Contains(nameof(SkeletonGroupRefusal.TooManyMembers), CreateError(Viera, twelve));
    }

    [Fact]
    public void AGroupMayUseGearFallbackRaces()
    {
        // Head gear may resolve for the target's fallback race or c0101 (SK-P1 rules, now at manifest level).
        var manifest = FormatTwo(201, [new AppearanceFileEntry("chara/human/c0101/skeleton/met/m6020/skl_c0101m6020.sklb", Blob("m", 48))]);
        Assert.Equal(2, manifest.FormatVersion);
        Assert.Equal((ushort)201, manifest.SkeletonGroupTargetRace);
    }

    // ---------- the target race is playable only ----------

    public static TheoryData<int> PlayerRaces()
    {
        var data = new TheoryData<int>();
        foreach (var race in SkeletonResourcePaths.PlayerRaceCodes)
            data.Add(race);
        return data;
    }

    [Theory]
    [MemberData(nameof(PlayerRaces))]
    public void EveryPlayableRaceIsAValidTarget(int raceCode)
    {
        var race = (ushort)raceCode;
        var path = $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";
        var manifest = FormatTwo(race, [new AppearanceFileEntry(path, Blob(path, 48))]);
        Assert.Equal(race, manifest.SkeletonGroupTargetRace);
        Assert.NotNull(AppearanceBundleManifest.TryParse(manifest.CanonicalBytes, AppearanceManifestReadOptions.AllowSkeletonGroups, out _));
    }

    [Theory]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(202)]
    [InlineData(203)]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(1804)]
    [InlineData(9104)]
    [InlineData(9204)]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1901)]
    [InlineData(9999)]
    [InlineData(ushort.MaxValue)]
    public void NoNpcOrUnknownRaceIsATarget(int raceCode)
    {
        var race = (ushort)raceCode;
        Assert.False(SkeletonResourcePaths.IsPlayerRaceCode(race));
        // A deformer-only group has no race in its paths, so only the target rule can refuse it.
        Assert.Contains("not a playable race", CreateError(race, new AppearanceFileEntry(Deformer, Blob("pbd", 88))));
        Assert.Equal(SkeletonGroupRefusal.InvalidTargetRace,
            SkeletonResourceGroupAssembler.AssembleDeclared(race, [new AppearanceFileEntry(Deformer, Blob("pbd", 88))]).Refusal);
    }

    [Fact]
    public void EveryKnownNpcRaceCodeIsRefusedAsATarget()
    {
        for (ushort code = 0; code < 10000; code++)
        {
            if (SkeletonResourcePaths.IsKnownRaceCode(code) && !SkeletonResourcePaths.PlayerRaceCodes.Contains(code))
                Assert.Contains("not a playable race", CreateError(code, new AppearanceFileEntry(Deformer, Blob("pbd", 88))));
        }
    }

    // ---------- version-aware entry validation ----------

    [Theory]
    [InlineData(Top, true, true)]
    [InlineData("chara/x.tex", true, true)]
    [InlineData(BaseSklb, false, true)]
    [InlineData(BasePhyb, false, true)]
    [InlineData(Deformer, false, true)]
    [InlineData("chara/xls/bonedeformer/other.pbd", false, false)]
    [InlineData("chara/human/c1801/skeleton/base/b0001/x.sklb", false, false)]
    [InlineData("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.skp", false, false)]
    [InlineData("chara/x.pap", false, false)]
    public void EntryValidityFollowsTheManifestFormat(string path, bool formatOne, bool formatTwo)
    {
        var entry = new AppearanceFileEntry(path, Blob("x", 48));
        Assert.Equal(formatOne, entry.IsValid);
        Assert.Equal(formatOne, entry.IsValidFor(1));
        Assert.Equal(formatTwo, entry.IsValidFor(2));
        Assert.False(entry.IsValidFor(0));
        // HV-6: format 3 (the human package) uses exactly the v2 allowlist; format 4 is unknown.
        Assert.Equal(formatTwo, entry.IsValidFor(3));
        Assert.False(entry.IsValidFor(4));

        Assert.Equal(formatOne, AppearanceFileTypes.IsAllowed(path));
        Assert.Equal(formatOne, AppearanceFileTypes.IsAllowed(path, 1));
        Assert.Equal(formatTwo, AppearanceFileTypes.IsAllowed(path, 2));
        Assert.False(AppearanceFileTypes.IsAllowed(path, 0));
        Assert.Equal(formatTwo, AppearanceFileTypes.IsAllowed(path, 3));
        Assert.False(AppearanceFileTypes.IsAllowed(path, 4));
    }

    [Fact]
    public void AllowlistVersionNBelongsToFormatN()
    {
        Assert.Equal(1, AppearanceLimits.FirstFormatVersion);
        Assert.Equal(2, AppearanceLimits.SkeletonGroupFormatVersion);
        Assert.Equal(1, AppearanceFileTypes.VersionForFormat(1));
        Assert.Equal(2, AppearanceFileTypes.VersionForFormat(2));
        Assert.Equal(0, AppearanceFileTypes.VersionForFormat(0));
        // HV-6: format 3 (the human package) uses allowlist v3, exactly the v2 set; format 4 is unknown.
        Assert.Equal(3, AppearanceLimits.HumanPackageFormatVersion);
        Assert.Equal(3, AppearanceFileTypes.VersionForFormat(3));
        Assert.Equal(0, AppearanceFileTypes.VersionForFormat(4));
        Assert.Equal(new[] { ".mdl", ".mtrl", ".tex" }, AppearanceFileTypes.AllowedFor(1));
        Assert.Equal(new[] { ".mdl", ".mtrl", ".tex", ".sklb", ".phyb", ".pbd" }, AppearanceFileTypes.AllowedFor(2));
        Assert.Equal(AppearanceFileTypes.AllowedFor(2), AppearanceFileTypes.AllowedFor(3));
        Assert.Empty(AppearanceFileTypes.AllowedFor(4));
    }

    // ---------- the one reported deformer alias ----------

    [Fact]
    public void OnlyTheExactReportedDeformerSpellingIsMapped()
    {
        Assert.True(SkeletonResourcePaths.TryCanonicalizeReported("chara/xls/boneDeformer/human.pbd", out var mapped));
        Assert.Equal(Deformer, mapped);
        Assert.True(SkeletonResourcePaths.TryClassify(mapped, out var path));
        Assert.Equal(SkeletonResourceRole.Deformer, path.Role);
        // The reported spelling itself is never a canonical game path.
        Assert.False(AppearanceGamePath.IsValid(SkeletonResourcePaths.ReportedDeformerGamePath));
    }

    [Theory]
    [InlineData("chara/xls/bonedeformer/human.pbd")]   // already canonical: unchanged
    [InlineData("chara/xls/BoneDeformer/human.pbd")]
    [InlineData("chara/xls/boneDeformer/Human.pbd")]
    [InlineData("chara/xls/boneDeformer/human.PBD")]
    [InlineData("Chara/xls/boneDeformer/human.pbd")]
    [InlineData("chara/xls/boneDeformer/human.pbd ")]
    [InlineData(" chara/xls/boneDeformer/human.pbd")]
    [InlineData("/chara/xls/boneDeformer/human.pbd")]
    [InlineData("chara\\xls\\boneDeformer\\human.pbd")]
    [InlineData("chara/xls/boneDeformer/human.pbd/")]
    [InlineData("chara/xls/boneDeformer/other.pbd")]
    [InlineData("Chara/Equipment/e0001/model/c0101e0001_top.mdl")]
    [InlineData("")]
    public void NoOtherPathIsMappedOrChanged(string reported)
    {
        Assert.False(SkeletonResourcePaths.TryCanonicalizeReported(reported, out var unchanged));
        Assert.Same(reported, unchanged);
    }

    [Fact]
    public void ANullReportedPathStaysNull()
    {
        Assert.False(SkeletonResourcePaths.TryCanonicalizeReported(null, out var unchanged));
        Assert.Null(unchanged);
    }

    // ---------- declared and byte-level assembly agree on paths ----------

    [Fact]
    public void DeclaredAssemblyRefusesPathsAsByteAssemblyDoes()
    {
        (ushort Race, string[] Paths)[] cases =
        [
            (Viera, ["chara/human/c1801/skeleton/base/b0001/x.sklb"]),
            (Viera, ["Chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb"]),
            (Viera, [BaseSklb, BaseSklb]),
            (101, [BaseSklb]),
            (Viera, [HairSklb, "chara/human/c1801/skeleton/hair/h0004/skl_c1801h0004.sklb"]),
            (Viera, [HairSklb, "chara/human/c1801/skeleton/hair/h0004/phy_c1801h0004.phyb"]),
            (104, [Deformer]),
        ];
        foreach (var (race, paths) in cases)
        {
            var declared = SkeletonResourceGroupAssembler.AssembleDeclared(race, paths.Select(p => new AppearanceFileEntry(p, Blob(p, 48))).ToArray());
            var bytes = SkeletonResourceGroupAssembler.Assemble(race, paths.Distinct().ToArray(),
                paths.Select(p => new SkeletonGroupCandidate(p, new byte[48])).ToArray());
            Assert.False(declared.IsAccepted);
            Assert.False(bytes.IsAccepted);
            Assert.Equal(bytes.Refusal, declared.Refusal);
            Assert.Equal(bytes.GamePath, declared.GamePath);
        }
    }

    [Fact]
    public void DeclaredAssemblyChecksEachRolesSizeLimit()
    {
        foreach (var (path, role) in new[] { (BaseSklb, SkeletonResourceRole.Skeleton), (BasePhyb, SkeletonResourceRole.Physics), (Deformer, SkeletonResourceRole.Deformer) })
        {
            var limit = SkeletonResourceValidation.MaximumBytes(role);
            Assert.True(SkeletonResourceGroupAssembler.AssembleDeclared(Viera, [new AppearanceFileEntry(path, Blob(path, limit))]).IsAccepted);
            var over = SkeletonResourceGroupAssembler.AssembleDeclared(Viera, [new AppearanceFileEntry(path, Blob(path, limit + 1))]);
            Assert.Equal(SkeletonGroupRefusal.Structure, over.Refusal);
            Assert.Equal(SkeletonStructureProblem.TooLarge, over.Problem);
        }
    }

    private static string MoveGroupFirst(string canonical)
    {
        const string group = ",\"skeletonGroup\":{\"targetRace\":1801}";
        return canonical.Replace(group, "").Replace("{\"formatVersion\":2,", "{\"formatVersion\":2" + group + ",");
    }
}
