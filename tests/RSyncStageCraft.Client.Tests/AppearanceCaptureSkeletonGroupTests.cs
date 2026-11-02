using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Capturing a skeleton group (development builds only, preview only):
/// all or nothing, the deformer alias before conflicts, the target race,
/// and an unchanged capture when skeleton groups are off. Synthetic bytes only.
/// </summary>
public sealed class AppearanceCaptureSkeletonGroupTests : IDisposable
{
    private const string BaseSklb = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb";
    private const string BasePhyb = "chara/human/c1801/skeleton/base/b0001/phy_c1801b0001.phyb";
    private const string HairSklb = "chara/human/c1801/skeleton/hair/h0003/skl_c1801h0003.sklb";
    private const string Deformer = SkeletonResourcePaths.DeformerGamePath;
    private const string ReportedDeformer = SkeletonResourcePaths.ReportedDeformerGamePath;
    private const string Kdb = "chara/human/c1801/skeleton/base/b0001/kdi_c1801b0001.kdb";
    private const string Skp = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.skp";
    private const string Model = "chara/equipment/e0001/model/c1801e0001_top.mdl";
    private const ushort Hyur = 101;
    private const ushort Viera = 1801;

    private static readonly RscUserId Owner = new("alice");
    private static readonly AppearanceCharacterRace VieraRava = new(8, 15, 1);
    private static readonly AppearanceCaptureOptions On = new(AllowSkeletonGroups: true, AllowDeformer: true);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-skeleton", Guid.NewGuid().ToString("N"));
    private readonly string mods;

    public AppearanceCaptureSkeletonGroupTests()
    {
        mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
    }

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

    // ---------- a whole group ----------

    [Fact]
    public async Task ACompleteGroupIsCapturedAsFormatTwoForTheCharactersRace()
    {
        var sklb = Sklb();
        var phyb = Phyb();
        var pbd = Pbd();
        var mdl = RandomNumberGenerator.GetBytes(4000);
        var read = Read(
            Local(Write("base.sklb", sklb), BaseSklb),
            Local(Write("base.phyb", phyb), BasePhyb),
            Local(Write("human.pbd", pbd), ReportedDeformer),
            Local(Write("top.mdl", mdl), Model));

        using var plan = await Plan(read);

        Assert.True(plan.CanUpload);
        Assert.True(plan.PreviewOnly);
        var manifest = plan.Manifest!;
        Assert.Equal(AppearanceLimits.SkeletonGroupFormatVersion, manifest.FormatVersion);
        Assert.Equal(Viera, manifest.SkeletonGroupTargetRace);
        Assert.Equal(new[] { Model, BasePhyb, BaseSklb, Deformer }.Order(StringComparer.Ordinal), manifest.Files.Select(file => file.GamePath));
        Assert.Equal(new AppearanceSkeletonGroupOutcome(AppearanceSkeletonGroupState.Captured, RaceCode: Viera, Members: 3), plan.SkeletonGroup);
        Assert.Empty(plan.Excluded);
        // The same bytes were checked and hashed; each member is uploadable from its file.
        foreach (var (gamePath, bytes) in new[] { (BaseSklb, sklb), (BasePhyb, phyb), (Deformer, pbd) })
        {
            var blob = manifest.Files.Single(file => file.GamePath == gamePath).Content;
            Assert.Equal(new AppearanceBlobRef(ContentHash.Of(bytes), bytes.Length), blob);
            Assert.True(plan.Sources.ContainsKey(blob.Hash));
        }
        // Exactly the manifest a direct build gives.
        var direct = AppearanceBundleManifest.TryCreate(Owner, manifest.Glamourer, null, null, manifest.Files, Viera, out var error);
        Assert.Null(error);
        Assert.Equal(direct!.BundleId, manifest.BundleId);
        Assert.Contains(AppearanceCaptureText.Warnings(plan), line => line.StartsWith("Skeleton group for race c1801: 3 files", StringComparison.Ordinal));
        Assert.Contains(AppearanceCaptureText.Warnings(plan), line => line.Contains("uploading it is not enabled yet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("deformer")]
    [InlineData("physics")]
    public async Task ADeformerOnlyOrPhysicsOnlyGroupIsWholeWhenThatIsEverythingRedirected(string only)
    {
        var read = only == "deformer"
            ? Read(Vanilla(BaseSklb), Local(Write("human.pbd", Pbd()), ReportedDeformer))
            : Read(Vanilla(BaseSklb), Local(Write("base.phyb", Phyb()), BasePhyb));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceSkeletonGroupState.Captured, plan.SkeletonGroup.State);
        Assert.Equal(1, plan.SkeletonGroup.Members);
        Assert.Equal(new[] { only == "deformer" ? Deformer : BasePhyb }, plan.Manifest!.Files.Select(file => file.GamePath));
        Assert.Equal(2, plan.Manifest.FormatVersion);
    }

    [Fact]
    public async Task TwoIdenticalCopiesOfOneMemberAreOneMember()
    {
        var bytes = Sklb();
        var read = Read(Local(Write("a/base.sklb", bytes), BaseSklb), Local(Write("b/base.sklb", bytes), BaseSklb));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceCaptureRefusal.None, plan.Refusal);
        Assert.Equal(AppearanceSkeletonGroupState.Captured, plan.SkeletonGroup.State);
        Assert.Equal(new[] { BaseSklb }, plan.Manifest!.Files.Select(file => file.GamePath));
    }

    // ---------- the deformer alias, before conflicts (C8) ----------

    [Fact]
    public async Task AVanillaDeformerInItsReportedSpellingIsVanillaAndNotAMember()
    {
        var read = Read(Vanilla(BaseSklb), Vanilla(ReportedDeformer), Local(Write("top.mdl", RandomNumberGenerator.GetBytes(300)), Model));

        using var plan = await Plan(read);

        Assert.True(plan.CanUpload);
        Assert.Equal(AppearanceSkeletonGroupState.NotPresent, plan.SkeletonGroup.State);
        Assert.Equal(1, plan.Manifest!.FormatVersion);
        Assert.Equal(2, plan.VanillaPaths);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public async Task AVanillaDeformerInItsReportedSpellingLeavesAGroupUnaffected()
    {
        var read = Read(
            Local(Write("base.sklb", Sklb()), BaseSklb),
            Vanilla(ReportedDeformer),
            Local(Write("top.mdl", RandomNumberGenerator.GetBytes(300)), Model));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceCaptureRefusal.None, plan.Refusal);
        Assert.Null(plan.ConflictingGamePath);
        Assert.Equal(AppearanceSkeletonGroupState.Captured, plan.SkeletonGroup.State);
        Assert.Equal(1, plan.SkeletonGroup.Members);
        Assert.Equal(Viera, plan.SkeletonGroup.RaceCode);
        Assert.Equal(1, plan.VanillaPaths);
        Assert.Empty(plan.Excluded);
        Assert.Equal(new[] { Model, BaseSklb }, plan.Manifest!.Files.Select(file => file.GamePath).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.Manifest.Files, file => file.GamePath == Deformer);
    }

    [Fact]
    public async Task AMixedCaseLocalDeformerAndALowercaseVanillaOneConflict()
    {
        var read = Read(Vanilla(BaseSklb), Vanilla(Deformer), Local(Write("human.pbd", Pbd()), ReportedDeformer));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceCaptureRefusal.ConflictingGamePath, plan.Refusal);
        Assert.Equal(Deformer, plan.ConflictingGamePath);
        Assert.False(plan.CanUpload);
    }

    [Fact]
    public async Task OneFileUnderBothSpellingsIsOneEntry()
    {
        var read = Read(Vanilla(BaseSklb), Local(Write("human.pbd", Pbd()), ReportedDeformer, Deformer));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceCaptureRefusal.None, plan.Refusal);
        Assert.Equal(new[] { Deformer }, plan.Manifest!.Files.Select(file => file.GamePath));
    }

    [Fact]
    public async Task TwoDifferentLocalFilesUnderTheTwoSpellingsConflict()
    {
        var read = Read(Vanilla(BaseSklb),
            Local(Write("a/human.pbd", Pbd()), ReportedDeformer),
            Local(Write("b/human.pbd", Pbd([Hyur])), Deformer));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceCaptureRefusal.ConflictingGamePath, plan.Refusal);
        Assert.Equal(Deformer, plan.ConflictingGamePath);
    }

    [Theory]
    [InlineData("chara/xls/BoneDeformer/human.pbd")]
    [InlineData("chara/xls/boneDeformer/Human.pbd")]
    public async Task NearSpellingsAreNeverMembers(string spelling)
    {
        var read = Read(Vanilla(BaseSklb), Local(Write("human.pbd", Pbd()), spelling));

        using var plan = await Plan(read);

        Assert.Equal(AppearanceSkeletonGroupState.NotPresent, plan.SkeletonGroup.State);
        Assert.Equal(new[] { new AppearanceCaptureExcluded(spelling, AppearanceCaptureExclusion.InvalidGamePath) }, plan.Excluded);
        Assert.Equal(1, plan.Manifest!.FormatVersion);
    }

    // ---------- the whole group is refused ----------

    [Fact]
    public async Task ASwappedMemberRefusesTheWholeGroup()
    {
        var read = Read(Vanilla(BaseSklb),
            Local(Write("base.phyb", Phyb()), BasePhyb),
            new AppearanceResourceEntry("chara/human/c1801/skeleton/hair/h0001/skl_c1801h0001.sklb", [HairSklb]),
            Local(Write("top.mdl", RandomNumberGenerator.GetBytes(300)), Model));

        using var plan = await Plan(read);

        AssertRefusedGroup(plan, AppearanceSkeletonGroupReason.SwappedMember, HairSklb, [BasePhyb, HairSklb]);
        Assert.Equal(1, plan.SwappedPaths);
    }

    public static TheoryData<string, AppearanceSkeletonGroupReason, string> MemberFailures() => new()
    {
        { "outside", AppearanceSkeletonGroupReason.MemberOutsideModDirectory, HairSklb },
        { "empty", AppearanceSkeletonGroupReason.MemberEmpty, HairSklb },
        { "too-large", AppearanceSkeletonGroupReason.MemberTooLarge, HairSklb },
        { "extension", AppearanceSkeletonGroupReason.MemberExtensionMismatch, HairSklb },
        { "structure", AppearanceSkeletonGroupReason.Invalid, HairSklb },
        { "deformer-not-allowed", AppearanceSkeletonGroupReason.DeformerNotAllowed, Deformer },
        { "kdb", AppearanceSkeletonGroupReason.UnsupportedDependency, Kdb },
        { "skp", AppearanceSkeletonGroupReason.UnsupportedDependency, Skp },
    };

    [Theory]
    [MemberData(nameof(MemberFailures))]
    public async Task AnyRequiredMemberFailureRefusesTheWholeGroup(string failure, AppearanceSkeletonGroupReason reason, string gamePath)
    {
        var entries = new List<AppearanceResourceEntry>
        {
            Local(Write("base.sklb", Sklb()), BaseSklb),
            Local(Write("top.mdl", RandomNumberGenerator.GetBytes(300)), Model),
        };
        var options = On;
        string[] group = [BaseSklb, HairSklb];
        switch (failure)
        {
            case "outside":
                var outside = Path.Combine(root, "elsewhere", "hair.sklb");
                Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
                File.WriteAllBytes(outside, Sklb());
                entries.Add(Local(outside, HairSklb));
                break;
            case "empty":
                entries.Add(Local(Write("hair.sklb", []), HairSklb));
                break;
            case "too-large":
                // Over the 4 MiB skeleton limit, far under the 64 MiB file limit.
                entries.Add(Local(Write("hair.sklb", new byte[(int)SkeletonResourceValidation.MaximumSkeletonBytes + 1]), HairSklb));
                break;
            case "extension":
                entries.Add(Local(Write("hair.tex", Sklb()), HairSklb));
                break;
            case "structure":
                var bad = Sklb();
                bad[0] ^= 0xFF;
                entries.Add(Local(Write("hair.sklb", bad), HairSklb));
                break;
            case "deformer-not-allowed":
                options = On with { AllowDeformer = false };
                entries.Add(Local(Write("human.pbd", Pbd()), ReportedDeformer));
                group = [BaseSklb, Deformer];
                break;
            case "kdb":
                entries.Add(Local(Write("hair.sklb", Sklb()), HairSklb));
                entries.Add(Local(Write("base.kdb", RandomNumberGenerator.GetBytes(64)), Kdb));
                break;
            case "skp":
                entries.Add(Local(Write("hair.sklb", Sklb()), HairSklb));
                entries.Add(Local(Write("base.skp", RandomNumberGenerator.GetBytes(64)), Skp));
                break;
        }

        using var plan = await Plan(Read([.. entries]), options);

        AssertRefusedGroup(plan, reason, gamePath, group);
        if (failure == "too-large")
            Assert.Equal(SkeletonStructureProblem.TooLarge, plan.SkeletonGroup.Problem);
        if (failure == "structure")
            Assert.Equal(SkeletonGroupRefusal.Structure, plan.SkeletonGroup.Assembly);
        if (failure is "kdb" or "skp")
        {
            Assert.Contains(new AppearanceCaptureExcluded(gamePath, AppearanceCaptureExclusion.UnsupportedSkeletonDependency), plan.Excluded);
            Assert.Equal(new[] { gamePath }, plan.UnsupportedSkeletonDependencies);
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("swapped")]
    [InlineData("non-canonical")]
    public async Task UnsupportedSkeletonDataWithoutAGroupIsDisclosed(string source)
    {
        const string nonCanonical = "chara/human/c1801/skeleton/base/b0001/SKL_c1801b0001.sklb";
        var read = source switch
        {
            "local" => Read(Vanilla(BaseSklb), Local(Write("base.skp", RandomNumberGenerator.GetBytes(64)), Skp)),
            "swapped" => Read(Vanilla(BaseSklb), new AppearanceResourceEntry("chara/human/c1801/skeleton/base/b0001/kdi_c0101b0001.kdb", [Kdb])),
            _ => Read(Vanilla(BaseSklb), Local(Write("upper.sklb", Sklb()), nonCanonical)),
        };

        using var plan = await Plan(read);

        Assert.Equal(AppearanceSkeletonGroupState.NotPresent, plan.SkeletonGroup.State);
        var path = source switch { "local" => Skp, "swapped" => Kdb, _ => nonCanonical };
        Assert.Equal(new[] { path }, plan.UnsupportedSkeletonDependencies);
        Assert.DoesNotContain(plan.Excluded, entry => entry.Reason == AppearanceCaptureExclusion.InvalidGamePath);
        Assert.Equal(source == "swapped" ? 0 : 1, plan.Excluded.Count(entry => entry.Reason == AppearanceCaptureExclusion.UnsupportedSkeletonDependency));
        Assert.Contains(AppearanceCaptureText.Warnings(plan), line =>
            line.StartsWith("Skeleton data this build cannot share is in use", StringComparison.Ordinal) && line.Contains(path, StringComparison.Ordinal));
        Assert.Equal(1, plan.Manifest!.FormatVersion);
    }

    public static TheoryData<string, AppearanceSkeletonGroupReason> RaceProblems() => new()
    {
        { "not-read", AppearanceSkeletonGroupReason.RaceUnknown },
        { "not-playable", AppearanceSkeletonGroupReason.RaceUnknown },
        { "no-base", AppearanceSkeletonGroupReason.RaceUnconfirmed },
        { "two-bases", AppearanceSkeletonGroupReason.RaceUnconfirmed },
        { "other-base", AppearanceSkeletonGroupReason.RaceMismatch },
    };

    [Theory]
    [MemberData(nameof(RaceProblems))]
    public async Task AGroupWhoseRaceCannotBeConfirmedIsRefused(string problem, AppearanceSkeletonGroupReason reason)
    {
        var hair = Local(Write("hair.sklb", Sklb()), HairSklb);
        var read = problem switch
        {
            "not-read" => Read(Vanilla(BaseSklb), hair) with { CharacterRace = null },
            "not-playable" => Read(Vanilla(BaseSklb), hair) with { CharacterRace = new AppearanceCharacterRace(8, 11, 1) },
            "no-base" => Read(hair),
            "two-bases" => Read(Vanilla(BaseSklb), Vanilla("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"), hair),
            _ => Read(Vanilla("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"), hair),
        };

        using var plan = await Plan(read);

        AssertRefusedGroup(plan, reason, null, [HairSklb]);
    }

    [Fact]
    public async Task TooManyMembersAreRefusedBeforeAnyFileIsRead()
    {
        // Twelve hair skeletons whose files do not exist: reading any would refuse it differently.
        var entries = Enumerable.Range(1, 12)
            .Select(set => FormattableString.Invariant($"chara/human/c1801/skeleton/hair/h{set:D4}/skl_c1801h{set:D4}.sklb"))
            .Select(gamePath => Local(Path.Combine(mods, "missing", Path.GetFileName(gamePath)), gamePath))
            .Prepend(Vanilla(BaseSklb))
            .ToArray();

        using var plan = await Plan(Read(entries));

        Assert.Equal(AppearanceSkeletonGroupState.Refused, plan.SkeletonGroup.State);
        Assert.Equal(AppearanceSkeletonGroupReason.TooManyMembers, plan.SkeletonGroup.Reason);
        Assert.Equal(12, plan.Excluded.Count(entry => entry.Reason == AppearanceCaptureExclusion.SkeletonGroupRefused));
    }

    // ---------- preview only: refused locally, before any request ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFormatTwoUploadIsRefusedLocallyAfterTheSnapshotIsCheckedAgain(bool changeAfterCapture)
    {
        var sklb = Write("base.sklb", Sklb());
        var reader = new Reader { Read = Read(Local(sklb, BaseSklb)) };
        var service = new FakeAppearanceService();
        var cache = new AppearanceBlobCache(Path.Combine(root, "cache"));
        using var client = new AppearanceTransferClient(() => service.Settings("alice"), cache, null, service);
        var authoring = new AppearanceAuthoring(reader, client, () => Owner, cache.TemporaryDirectory, On);

        var captured = await authoring.CaptureCurrentCharacterAsync(CancellationToken.None);
        using var plan = captured.Plan!;
        Assert.True(plan.PreviewOnly);
        if (changeAfterCapture)
            File.WriteAllBytes(sklb, Sklb(payload: 64));

        var uploaded = await authoring.UploadAsync(plan, AppearanceUploadTarget.NewEntry("Look"), CancellationToken.None);

        Assert.Equal(
            changeAfterCapture ? AppearanceTransferStatus.CaptureChanged : AppearanceTransferStatus.SkeletonGroupUploadNotEnabled,
            uploaded.Result.Status);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task AFormatOneCaptureWithGroupsOnStillUploads()
    {
        var reader = new Reader { Read = Read(Vanilla(BaseSklb), Local(Write("top.mdl", RandomNumberGenerator.GetBytes(300)), Model)) };
        var service = new FakeAppearanceService();
        var cache = new AppearanceBlobCache(Path.Combine(root, "cache"));
        using var client = new AppearanceTransferClient(() => service.Settings("alice"), cache, null, service);
        var authoring = new AppearanceAuthoring(reader, client, () => Owner, cache.TemporaryDirectory, On);

        using var plan = (await authoring.CaptureCurrentCharacterAsync(CancellationToken.None)).Plan!;
        Assert.False(plan.PreviewOnly);
        var uploaded = await authoring.UploadAsync(plan, AppearanceUploadTarget.NewEntry("Look"), CancellationToken.None);

        Assert.True(uploaded.Result.Succeeded);
    }

    // ---------- unchanged when skeleton groups are off ----------

    [Fact]
    public async Task WithGroupsOffTheCaptureIsExactlyTodays()
    {
        var mdl = RandomNumberGenerator.GetBytes(700);
        var read = Read(
            Local(Write("base.sklb", Sklb()), BaseSklb),
            Local(Write("human.pbd", Pbd()), ReportedDeformer),
            Vanilla(Deformer),
            Local(Write("base.kdb", RandomNumberGenerator.GetBytes(64)), Kdb),
            new AppearanceResourceEntry("chara/human/c1801/skeleton/hair/h0001/skl_c1801h0001.sklb", [HairSklb]),
            Local(Write("top.mdl", mdl), Model));

        foreach (var options in new[] { null, AppearanceCaptureOptions.Default, new AppearanceCaptureOptions(AllowDeformer: true) })
        {
            using var plan = await AppearanceCapturePlanner.PlanAsync(Owner, read, Path.Combine(root, "work"), CancellationToken.None, null, options);

            Assert.True(plan.CanUpload);
            Assert.False(plan.PreviewOnly);
            Assert.Equal(AppearanceSkeletonGroupOutcome.NotPresent, plan.SkeletonGroup);
            Assert.Empty(plan.UnsupportedSkeletonDependencies);
            Assert.Equal(
                new AppearanceCaptureExcluded[]
                {
                    new(Kdb, AppearanceCaptureExclusion.TypeNotAllowed),
                    new(BaseSklb, AppearanceCaptureExclusion.TypeNotAllowed),
                    new(ReportedDeformer, AppearanceCaptureExclusion.InvalidGamePath),
                },
                plan.Excluded.OrderBy(entry => entry.GamePath, StringComparer.Ordinal));
            Assert.Equal(1, plan.VanillaPaths);
            Assert.Equal(1, plan.SwappedPaths);
            var manifest = plan.Manifest!;
            Assert.Equal(1, manifest.FormatVersion);
            Assert.Null(manifest.SkeletonGroupTargetRace);
            var today = AppearanceBundleManifest.TryCreate(Owner, manifest.Glamourer, null, null,
                [new AppearanceFileEntry(Model, new AppearanceBlobRef(ContentHash.Of(mdl), mdl.Length))], out _);
            Assert.Equal(today!.BundleId, manifest.BundleId);
            Assert.DoesNotContain(AppearanceCaptureText.Warnings(plan), line => line.Contains("keleton", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ASavedDesignNeverHasAGroup()
    {
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.SavedDesign, "design-state", [], "Look", CharacterRace: VieraRava);

        using var plan = await Plan(read);

        Assert.Equal(AppearanceSkeletonGroupOutcome.NotPresent, plan.SkeletonGroup);
        Assert.Equal(1, plan.Manifest!.FormatVersion);
    }

    [Fact]
    public void AReleaseSnapshotDigestsExactlyAsBeforeAndARaceChangesIt()
    {
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "state",
        [
            new("chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb", ["chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb"]),
            new("C:/mods/a.mdl", ["chara/equipment/e0001/model/c0101e0001_top.mdl"]),
        ], PenumbraModDirectory: "C:/mods", Player: new AppearancePlayer(17, 0x1000, 0xABCDEF));

        // The digest of this snapshot on the base commit, before the race existed.
        Assert.Equal("7C3DC074C6866AA8BC79B7C2C4FC02FF6A227BB98D9AB18F76C6873CC9A0F68F", AppearanceCapturePlanner.Fingerprint(read));
        Assert.Equal("544D24D1982F1C8BEE072ACE626160DD7C403228AD5F69B301B67FE56818A18C",
            AppearanceCapturePlanner.Fingerprint(read with { CharacterRace = VieraRava }));
        Assert.NotEqual(
            AppearanceCapturePlanner.Fingerprint(read with { CharacterRace = VieraRava }),
            AppearanceCapturePlanner.Fingerprint(read with { CharacterRace = VieraRava with { Sex = 0 } }));
    }

    // ---------- helpers ----------

    private static void AssertRefusedGroup(AppearanceCapturePlan plan, AppearanceSkeletonGroupReason reason, string? gamePath, string[] group)
    {
        Assert.Equal(AppearanceCaptureRefusal.None, plan.Refusal);
        Assert.Equal(AppearanceSkeletonGroupState.Refused, plan.SkeletonGroup.State);
        Assert.Equal(reason, plan.SkeletonGroup.Reason);
        if (gamePath is not null)
            Assert.Equal(gamePath, plan.SkeletonGroup.GamePath);
        var manifest = plan.Manifest!;
        Assert.Equal(1, manifest.FormatVersion);
        Assert.False(plan.PreviewOnly);
        Assert.DoesNotContain(manifest.Files, file => SkeletonResourcePaths.TryClassify(file.GamePath, out _));
        // Every path of the group, listed once.
        Assert.Equal(group.Order(StringComparer.Ordinal),
            plan.Excluded.Where(entry => entry.Reason == AppearanceCaptureExclusion.SkeletonGroupRefused).Select(entry => entry.GamePath));
        Assert.Equal(plan.Excluded.Count, plan.Excluded.Select(entry => entry.GamePath).Distinct(StringComparer.Ordinal).Count());
        var line = Assert.Single(AppearanceCaptureText.Warnings(plan), text => text.StartsWith("Skeleton group not captured: ", StringComparison.Ordinal));
        Assert.Contains(AppearanceCaptureText.SkeletonGroupReason(plan.SkeletonGroup), line, StringComparison.Ordinal);
        if (gamePath is not null)
            Assert.Contains(gamePath, line, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), line, StringComparison.OrdinalIgnoreCase);
    }

    private Task<AppearanceCapturePlan> Plan(AppearanceCaptureRead read, AppearanceCaptureOptions? options = null) =>
        AppearanceCapturePlanner.PlanAsync(Owner, read, Path.Combine(root, "work"), CancellationToken.None, null, options ?? On);

    private AppearanceCaptureRead Read(params AppearanceResourceEntry[] entries) =>
        new(AppearanceCaptureSource.CurrentCharacter, "glamourer-state", entries, PenumbraModDirectory: mods, CharacterRace: VieraRava);

    private static AppearanceResourceEntry Local(string file, params string[] gamePaths) => new(file, gamePaths);

    private static AppearanceResourceEntry Vanilla(string gamePath) => new(gamePath, [gamePath]);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(mods, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A synthetic .sklb header (version 1301), as the classifier tests build it.</summary>
    private static byte[] Sklb(int payload = 32)
    {
        var data = new byte[16 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x736B6C62);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes("1301")));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        return data;
    }

    private static byte[] Phyb()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 24);
        return data;
    }

    /// <summary>Entries for the given races; the last has a 4-byte body after the tables.</summary>
    private static byte[] Pbd(ushort[]? races = null)
    {
        races ??= [Hyur, Viera];
        var n = races.Length;
        var tablesEnd = 4 + n * 20;
        var data = new byte[tablesEnd + 4];
        BinaryPrimitives.WriteInt32LittleEndian(data, n);
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4 + i * 12), races[i]);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4 + i * 12 + 4), i == n - 1 ? tablesEnd : 0);
        }
        return data;
    }

    private sealed class Reader : IAppearanceCaptureReader
    {
        public AppearanceCaptureRead? Read { get; set; }

        public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadCurrentCharacterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Ok, Read));

        public Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>(AppearanceReadStatus.Unavailable));

        public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable));
    }
}
