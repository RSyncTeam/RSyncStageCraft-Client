#if RSC_DEVELOPMENT
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-5 M1: the local human catalogue's label rule, entry format, store and
/// capture. Synthetic files, paths and reads only: no game, no IPC, and no
/// real character, mod or profile.
/// </summary>
public sealed class HumanCatalogueTests : IDisposable
{
    private const string BaseSklb = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.sklb";
    private const string BasePhyb = "chara/human/c1801/skeleton/base/b0001/phy_c1801b0001.phyb";
    private const string Deformer = SkeletonResourcePaths.DeformerGamePath;
    private const string ReportedDeformer = SkeletonResourcePaths.ReportedDeformerGamePath;
    private const string Skp = "chara/human/c1801/skeleton/base/b0001/skl_c1801b0001.skp";
    private const string Model = "chara/equipment/e0001/model/c1801e0001_top.mdl";
    private const string Effect = "vfx/common/eff/sample_a1.avfx";
    private const string Animation = "chara/human/c1801/animation/a0001/bt_common/idle_sample.pap";
    private const string Hair = "chara/human/c1801/obj/hair/h0003/model/c1801h0003_hir.mdl";
    private const string OtherHair = "chara/human/c1801/obj/hair/h0005/model/c1801h0005_hir.mdl";
    private const ushort Hyur = 101;
    private const ushort Viera = 1801;

    private static readonly AppearanceCharacterRace VieraRava = new(8, 15, 1);
    private static readonly HumanCatalogueVersions Versions = new("2026.01.01.0000.0000", "1.0.0.0", "1.0.0.0", null);
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-hv5-catalogue", Guid.NewGuid().ToString("N"));
    private readonly string mods;
    private readonly string catalogue;

    public HumanCatalogueTests()
    {
        mods = Path.Combine(root, "mods");
        catalogue = Path.Combine(root, "catalogue");
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

    // ---------- the friendly label (D11, amendment 1) ----------

    [Theory]
    [InlineData("Rsc One", "Rsc One")]
    [InlineData("  Rsc One  ", "Rsc One")]
    [InlineData("a", "a")]
    [InlineData("Outfit-2 test", "Outfit-2 test")]
    public void AValidLabelIsTrimmedAndKept(string text, string expected)
    {
        Assert.Equal(expected, HumanCatalogueLabel.Normalize(text, out var refusal));
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg")]
    [InlineData("Rsc_One")]
    [InlineData("Rsc One!")]
    [InlineData("Rsc Öne")]
    [InlineData("Rsc\tOne")]
    public void AnInvalidLabelIsRefused(string? text)
    {
        Assert.Null(HumanCatalogueLabel.Normalize(text, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void ALabelInTheSyntheticNameFormIsRefusedButRscOneIsNot()
    {
        var synthetic = HumanPuppetName.Create(new HashSet<char>());
        Assert.True(HumanPuppetName.IsWellFormed(synthetic));
        Assert.Null(HumanCatalogueLabel.Normalize(synthetic, out var refusal));
        Assert.Contains("synthetic actor name", refusal, StringComparison.Ordinal);
        // Rsc One can never be a P2 name: P2 names have exactly 19 characters.
        Assert.False(HumanPuppetName.IsWellFormed("Rsc One"));
        Assert.Equal(19, HumanPuppetName.Length);
    }

    [Fact]
    public void LabelsAreComparedIgnoringCase() => Assert.True(HumanCatalogueLabel.Same("Rsc One", "rsc one"));

    // ---------- the plan (D1, D3-a, D9) ----------

    [Fact]
    public async Task TheCompleteSkeletonGroupWithTheDeformerIsPlannedWithEveryOtherFileType()
    {
        var read = Read(
            Local(Write("ivcs/base.sklb", Sklb()), BaseSklb),
            Local(Write("ivcs/base.phyb", Phyb()), BasePhyb),
            Local(Write("ivcs/human.pbd", Pbd()), ReportedDeformer),
            Local(Write("top.mdl", Bytes(400)), Model),
            Local(Write("fx.avfx", Bytes(300)), Effect),
            Local(Write("idle.pap", Bytes(200)), Animation),
            Swap(Hair, OtherHair),
            Vanilla("chara/common/texture/sample_a.tex"));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(refusal);
        Assert.NotNull(plan);
        Assert.Equal(new HumanCatalogueSkeleton(Viera, new[] { BasePhyb, BaseSklb, Deformer }.Order(StringComparer.Ordinal).ToList(), true),
            plan!.Skeleton, new SkeletonComparer());
        Assert.Equal(new[] { Animation, Hair, Model, BasePhyb, BaseSklb, Deformer, Effect }.Order(StringComparer.Ordinal),
            plan.Paths.Select(path => path.GamePath).Order(StringComparer.Ordinal));
        Assert.Equal(OtherHair, Assert.Single(plan.Paths, path => path.Swap is not null).Swap);
        Assert.Equal(6, plan.Counts.Redirects);
        Assert.Equal(1, plan.Counts.Swaps);
        Assert.Equal(1, plan.Counts.Vanilla);
        Assert.Equal((1, 1, 1, 3), (plan.Counts.Skeleton, plan.Counts.Physics, plan.Counts.Deformer, plan.Counts.Other));
        Assert.Equal(6, plan.Files.Count);
        // The deformer's stored content is the file's exact bytes, under the canonical game path.
        var pbd = File.ReadAllBytes(Path.Combine(mods, "ivcs", "human.pbd"));
        Assert.Equal(ContentHash.Of(pbd).Value, plan.Paths.Single(path => path.GamePath == Deformer).Blob);
    }

    [Fact]
    public async Task AFileOutsideTheModFolderIsLeftOutAndCounted()
    {
        var outsideFolder = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outsideFolder);
        var outsideFile = Path.Combine(outsideFolder, "top.mdl");
        File.WriteAllBytes(outsideFile, Bytes(50));
        var read = Read(Local(Write("a.mdl", Bytes(60)), Model), Local(outsideFile, "chara/equipment/e0002/model/c1801e0002_top.mdl"));

        var (plan, _) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Equal(1, plan!.Counts.Redirects);
        Assert.Equal(1, plan.Counts.Outside);
        Assert.DoesNotContain(plan.Files, file => file.Source == outsideFile);
    }

    [Fact]
    public async Task AModFileWhoseTypeDoesNotMatchItsGamePathRefusesTheWholeCapture()
    {
        const string mismatched = "chara/equipment/e0003/model/c1801e0003_top.mdl";
        var read = Read(Local(Write("a.mdl", Bytes(60)), Model), Local(Write("b.tex", Bytes(60)), mismatched));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.StartsWith("a mod file's type does not match its game path", refusal, StringComparison.Ordinal);
        Assert.Contains(mismatched, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoSpellingsOfOneGamePathRefuseTheWholeCapture()
    {
        var bytes = Bytes(60);
        var read = Read(Local(Write("a.mdl", bytes), Model), Local(Write("b.mdl", bytes), Model.ToUpperInvariant()));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.StartsWith("one game path is reported in two spellings", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkeletonDataThatIsNotAGroupMemberRefusesTheWholeCapture()
    {
        var read = Read(Local(Write("base.sklb", Sklb()), BaseSklb), Local(Write("base.skp", Bytes(40)), Skp), Local(Write("a.mdl", Bytes(9)), Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.Contains(Skp, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupTheExistingChecksRefuseRefusesTheCaptureRatherThanDroppingIt()
    {
        // A deformer with no entries is not a valid .pbd: the group checks refuse it, and nothing is dropped.
        var read = Read(Local(Write("base.sklb", Sklb()), BaseSklb), Local(Write("human.pbd", new byte[8]), ReportedDeformer),
            Local(Write("a.mdl", Bytes(9)), Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.StartsWith("the skeleton group was refused", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwappedGroupMemberRefusesTheCapture()
    {
        var read = Read(Swap(BaseSklb, "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"), Local(Write("a.mdl", Bytes(9)), Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.Contains(nameof(AppearanceSkeletonGroupReason.SwappedMember), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDifferentSourcesForOneGamePathRefuseTheCapture()
    {
        var read = Read(Local(Write("a/top.mdl", Bytes(10)), Model), Local(Write("b/top.mdl", Bytes(11)), Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.Contains("two different sources", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectAndAVanillaSourceForOneGamePathRefuseTheCapture()
    {
        var read = Read(Local(Write("top.mdl", Bytes(10)), Model), Vanilla(Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(plan);
        Assert.Contains("two different sources", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalCopiesForOneGamePathAreOneFile()
    {
        var bytes = Bytes(32);
        var read = Read(Local(Write("a/top.mdl", bytes), Model), Local(Write("b/top.mdl", bytes), Model));

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(read, CancellationToken.None);

        Assert.Null(refusal);
        Assert.Single(plan!.Files);
        Assert.Single(plan.Paths);
    }

    [Fact]
    public async Task AVanillaOnlyLookIsRefused()
    {
        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(Read(Vanilla(Model), Swap(Hair, OtherHair)), CancellationToken.None);

        Assert.Null(plan);
        Assert.StartsWith("no file beneath the mod folder is redirected", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyModFileRefusesTheCapture()
    {
        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(Read(Local(Write("top.mdl", []), Model)), CancellationToken.None);

        Assert.Null(plan);
        Assert.StartsWith("a mod file could not be stored", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThan1024GamePathsAreRefused()
    {
        var file = Write("shared.mdl", Bytes(16));
        var gamePaths = Enumerable.Range(0, HumanCatalogueEntryCodec.MaximumGamePaths + 1)
            .Select(i => $"chara/equipment/e{i:D4}/model/c1801e{i:D4}_top.mdl").ToArray();

        var (plan, refusal) = await HumanCataloguePlanner.PlanAsync(Read(Local(file, gamePaths)), CancellationToken.None);

        Assert.Null(plan);
        Assert.Contains("at most 1024", refusal, StringComparison.Ordinal);

        var (exactly, _) = await HumanCataloguePlanner.PlanAsync(Read(Local(file, gamePaths[..^1])), CancellationToken.None);
        Assert.Equal(HumanCatalogueEntryCodec.MaximumGamePaths, exactly!.Paths.Count);
    }

    // ---------- the store (D6) ----------

    [Fact]
    public async Task AFileIsStoredOnceAndVerifiedWhenItIsAlreadyThere()
    {
        var store = new HumanCatalogueStore(catalogue);
        var bytes = Bytes(500);
        var source = Write("top.mdl", bytes);
        var blob = new AppearanceBlobRef(ContentHash.Of(bytes), bytes.Length);

        Assert.Equal(HumanCatalogueImportStatus.Stored, await store.ImportAsync(source, blob, CancellationToken.None));
        Assert.Equal(HumanCatalogueImportStatus.AlreadyStored, await store.ImportAsync(source, blob, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(catalogue, "blobs", blob.Hash.Value + ".blob")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs"), "*.tmp"));
    }

    [Fact]
    public async Task ACopyThatIsNotThePlannedContentIsRemovedAndRefused()
    {
        var store = new HumanCatalogueStore(catalogue);
        var source = Write("top.mdl", Bytes(100));
        var planned = new AppearanceBlobRef(ContentHash.Of(Bytes(100)), 100);

        Assert.Equal(HumanCatalogueImportStatus.SourceChanged, await store.ImportAsync(source, planned, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")));

        var grown = new AppearanceBlobRef(ContentHash.Of(File.ReadAllBytes(source)), 50);
        Assert.Equal(HumanCatalogueImportStatus.SourceChanged, await store.ImportAsync(source, grown, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")));
    }

    [Fact]
    public async Task ADamagedStoredFileIsKeptAndRefusedNeverReplaced()
    {
        var store = new HumanCatalogueStore(catalogue);
        var bytes = Bytes(64);
        var blob = new AppearanceBlobRef(ContentHash.Of(bytes), bytes.Length);
        var stored = Path.Combine(catalogue, "blobs", blob.Hash.Value + ".blob");
        var damaged = Bytes(64);
        File.WriteAllBytes(stored, damaged);

        Assert.Equal(HumanCatalogueImportStatus.StoredFileDamaged, await store.ImportAsync(Write("top.mdl", bytes), blob, CancellationToken.None));
        Assert.Equal(damaged, File.ReadAllBytes(stored));
    }

    [Fact]
    public async Task AFullStoreRefusesAndEvictsNothing()
    {
        var store = new HumanCatalogueStore(catalogue, limitBytes: 1000);
        var first = Bytes(600);
        var second = Bytes(600);
        var firstBlob = new AppearanceBlobRef(ContentHash.Of(first), first.Length);

        Assert.Equal(HumanCatalogueImportStatus.Stored, await store.ImportAsync(Write("a.mdl", first), firstBlob, CancellationToken.None));
        Assert.Equal(HumanCatalogueImportStatus.Full,
            await store.ImportAsync(Write("b.mdl", second), new AppearanceBlobRef(ContentHash.Of(second), second.Length), CancellationToken.None));
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(catalogue, "blobs", firstBlob.Hash.Value + ".blob")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")));
    }

    [Fact]
    public async Task RoomCountsOnlyFilesNotYetStoredAndTheEntry()
    {
        var store = new HumanCatalogueStore(catalogue, limitBytes: 1000);
        var first = Bytes(600);
        var second = Bytes(300);
        var firstBlob = new AppearanceBlobRef(ContentHash.Of(first), first.Length);
        var secondBlob = new AppearanceBlobRef(ContentHash.Of(second), second.Length);
        Assert.Equal(HumanCatalogueImportStatus.Stored, await store.ImportAsync(Write("a.mdl", first), firstBlob, CancellationToken.None));

        Assert.Equal((true, 300L), store.Room([firstBlob, secondBlob, secondBlob], 100));
        Assert.Equal((false, 300L), store.Room([firstBlob, secondBlob], 101));
        Assert.Equal((true, 0L), store.Room([firstBlob], 400));
    }

    [Fact]
    public void LeftoverTemporaryFilesAreRemovedAtStartAndNothingElse()
    {
        Directory.CreateDirectory(Path.Combine(catalogue, "blobs"));
        Directory.CreateDirectory(Path.Combine(catalogue, "entries"));
        var leftover = Path.Combine(catalogue, "blobs", "x.tmp");
        var kept = Path.Combine(catalogue, "blobs", new string('a', 64) + ".blob");
        File.WriteAllBytes(leftover, [1]);
        File.WriteAllBytes(kept, [1]);

        _ = new HumanCatalogueStore(catalogue);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void TheDefaultLimitIsTwoGibibytes() => Assert.Equal(2L * 1024 * 1024 * 1024, HumanCatalogueStore.DefaultLimitBytes);

    // ---------- the capture ----------

    [Fact]
    public async Task ACaptureWritesOneImmutableEntryWithNoLocalPath()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None);

        Assert.Null(result.Refusal);
        var entry = result.Entry!;
        Assert.Equal("Rsc One", entry.Label);
        Assert.Equal(HumanCatalogueEntry.CurrentFormat, entry.Format);
        Assert.Equal(HumanCatalogueEntry.NoProfile, entry.ProfileSource);
        Assert.Equal(Now, entry.CapturedUtc);
        Assert.True(entry.Skeleton!.Deformer);
        Assert.Equal((byte)2, entry.BodyType);
        var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
        Assert.Equal(entry.EntryId.ToString("N") + ".json", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.Replace("\\", "\\\\", StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(read.PlayerObject, text, StringComparison.Ordinal);
        // Exactly the entry's own fields: no name, world, index, collection, key or receipt field.
        using var document = JsonDocument.Parse(text);
        Assert.Equal(EntryFields, document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        // Read back, it is the same entry, and every file it names is stored.
        Assert.Equal(entry.EntryId, HumanCatalogueEntryCodec.TryParse(File.ReadAllBytes(file), out _)!.EntryId);
        var listing = Assert.Single(store.List().Entries);
        Assert.True(listing.Complete);
        Assert.Equal("Rsc One", listing.Label);
        Assert.Contains(result.Lines, line => line.Contains("deformer included", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALabelAlreadyUsedIsRefusedIgnoringCase()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();
        Assert.NotNull((await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None)).Entry);

        var again = await HumanCatalogueCapture.CaptureAsync("rsc one", store, Reads(read, read), () => Now, CancellationToken.None);

        Assert.Null(again.Entry);
        Assert.Equal("that label is already used", again.Refusal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("reference")]
    [InlineData("manipulations")]
    [InlineData("resources")]
    [InlineData("summary")]
    [InlineData("body")]
    [InlineData("player")]
    public async Task AnyChangeBetweenTheTwoReadsRefusesTheEntry(string what)
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();
        var changed = what switch
        {
            "state" => read with { GlamourerState = Convert.ToBase64String(Bytes(12)) },
            "reference" => read with { GlamourerReference = "{\"Equipment\":{}}" },
            "manipulations" => read with { Manipulations = "other" },
            "resources" => read with { Resources = read.Resources.Skip(1).ToList() },
            "summary" => read with { SkeletonSummary = ["skeleton partials: 2"] },
            "body" => read with { BodyType = 3 },
            _ => read with { PlayerObject = "0:2:3" },
        };

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, changed), () => Now, CancellationToken.None);

        Assert.Null(result.Entry);
        Assert.Equal("the appearance changed during the capture", result.Refusal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
        // Copied files stay as unreferenced files; nothing is deleted.
        Assert.True(store.List().OrphanFiles > 0);
    }

    [Fact]
    public async Task AReadRefusalWritesNothing()
    {
        var store = new HumanCatalogueStore(catalogue);

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One", store,
            _ => Task.FromResult(HumanCatalogueReadResult.Refused("Glamourer is not available")), () => Now, CancellationToken.None);

        Assert.Equal("Glamourer is not available", result.Refusal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
    }

    [Fact]
    public async Task ACaptureThatDoesNotFitIsRefused()
    {
        var store = new HumanCatalogueStore(catalogue, limitBytes: 100);
        var read = FullRead();

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None);

        Assert.Null(result.Entry);
        Assert.StartsWith("the catalogue is full", result.Refusal, StringComparison.Ordinal);
        // Checked before any copy: nothing was stored, so nothing needs removing.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
    }

    [Theory]
    [InlineData("file 0123456789abcdef, bones 12, names 1111111111111111", "bones 12, names 1111111111111111")]
    [InlineData("bones 12, names 1111111111111111", "bones 12, names 1111111111111111")]
    [InlineData("file 0123456789ABCDEF, bones 12", "file 0123456789ABCDEF, bones 12")]
    [InlineData("no skeleton", "no skeleton")]
    public void TheSkeletonSummaryLeavesOutOnlyTheFileNameHash(string fact, string kept) =>
        Assert.Equal(kept, HumanCatalogueEntryCodec.WithoutFileHash(fact));

    [Fact]
    public void ALocalPathIsFoundAsWrittenOrEscaped()
    {
        const string local = @"C:\Mods\Sample\top.mdl";
        Assert.True(HumanCatalogueCapture.HoldsLocalText(Encoding.UTF8.GetBytes("{\"a\":\"C:\\\\Mods\\\\Sample\\\\top.mdl\"}"), [local]));
        Assert.True(HumanCatalogueCapture.HoldsLocalText(Encoding.UTF8.GetBytes("x C:/Mods/Sample/top.mdl"), [local]));
        Assert.False(HumanCatalogueCapture.HoldsLocalText(Encoding.UTF8.GetBytes("chara/equipment/e0001/model/top.mdl"), [local]));
    }

    // ---------- the entry format ----------

    [Fact]
    public async Task AnEntryWithAChangedFieldIsRefusedOnRead()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();
        var entry = (await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None)).Entry!;
        var bytes = HumanCatalogueEntryCodec.Serialize(entry);
        Assert.NotNull(HumanCatalogueEntryCodec.TryParse(bytes, out _));

        foreach (var tampered in new[]
        {
            entry with { Format = "hv5-local-2" },
            entry with { ProfileSource = "owner-approved" },
            entry with { GlamourerStateHash = new string('0', 64) },
            entry with { Manipulations = entry.Manipulations + "x" },
            entry with { Label = "Rsc_One" },
            entry with { Counts = entry.Counts with { Redirects = entry.Counts.Redirects + 1 } },
            entry with { Skeleton = entry.Skeleton! with { Deformer = false } },
            entry with { Paths = entry.Paths.Append(new HumanCataloguePath("C:/x.mdl", entry.Blobs[0].Hash, null)).ToList() },
        })
        {
            Assert.NotNull(HumanCatalogueEntryCodec.Problem(tampered));
            Assert.Throws<ArgumentException>(() => HumanCatalogueEntryCodec.Serialize(tampered));
        }
        // An unknown field is refused on read.
        var withExtra = Encoding.UTF8.GetString(bytes).Replace("\"format\":", "\"playerName\": \"x\", \"format\":", StringComparison.Ordinal);
        Assert.Null(HumanCatalogueEntryCodec.TryParse(Encoding.UTF8.GetBytes(withExtra), out var problem));
        Assert.Equal("not an entry", problem);
    }

    [Fact]
    public async Task AnEntryFileNotNamedByItsOwnIdIsNotListed()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();
        Assert.NotNull((await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None)).Entry);
        var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
        File.Move(file, Path.Combine(catalogue, "entries", Guid.NewGuid().ToString("N") + ".json"));

        var list = store.List();

        Assert.Empty(list.Entries);
        Assert.Equal(1, list.UnreadableEntries);
    }

    [Fact]
    public void StoredGamePathsAreRelativeAndPrintable()
    {
        Assert.True(HumanCatalogueEntryCodec.IsStoredGamePath(Model));
        Assert.True(HumanCatalogueEntryCodec.IsStoredGamePath(ReportedDeformer));
        foreach (var bad in new[] { "", "/chara/a.mdl", "C:/a.mdl", "chara\\a.mdl", "chara//a.mdl", "chara/../a.mdl", "chara/a b.mdl", new string('a', 261) })
            Assert.False(HumanCatalogueEntryCodec.IsStoredGamePath(bad), bad);
    }

    // ---------- HV-5 M3: the owner-approved Customize+ profile ----------

    /// <summary>The read with a comparable skeleton summary and the synthetic profile, as the flag's read gives it.</summary>
    private static HumanCatalogueRead WithProfile(HumanCatalogueRead read, string json = ProfileSample.Json) =>
        read with
        {
            SkeletonSummary = HumanCatalogueFixture.Summary, Profile = ProfileSample.Read(json),
            Versions = read.Versions with { CustomizePlus = HumanActorCustomizePlusStage.RequiredVersion },
        };

    [Fact]
    public async Task ACaptureWithTheFlagStoresTheCanonicalProfileInANewEntryBesideAnUnchangedOne()
    {
        var store = new HumanCatalogueStore(catalogue);
        var plain = FullRead();
        var first = (await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(plain, plain), () => Now, CancellationToken.None)).Entry!;
        var firstFile = Path.Combine(catalogue, "entries", first.EntryId.ToString("N") + ".json");
        var firstBytes = File.ReadAllBytes(firstFile);
        var read = WithProfile(plain);

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(read, read), () => Now, CancellationToken.None, withProfile: true);

        Assert.Null(result.Refusal);
        var entry = result.Entry!;
        Assert.Equal(HumanCatalogueEntry.ProfileFormat, entry.Format);
        Assert.Equal(HumanCatalogueEntry.OwnerApproved, entry.ProfileSource);
        Assert.Equal(read.Profile!.Profile, entry.Profile);
        Assert.Equal(HumanCatalogueEntry.HashOf(entry.Profile!), entry.ProfileHash);
        Assert.Equal(ProfileSample.Counts, entry.ProfileCounts);
        // The same files: shared, none stored again.
        Assert.Contains(result.Lines, line => line.StartsWith($"files {entry.Counts.Files} (0 new, {entry.Counts.Files} already stored)", StringComparison.Ordinal));
        Assert.Equal("profile: owner-approved (active), id 5a17c0de, 3 bone(s) (2 base, 0 other partial, 1 absent); stored, no name " +
            "(temporary profiles, the editor and the global default profile cannot be read)", result.Lines[^1]);
        Assert.DoesNotContain(result.Lines, line => line.Contains("j_kosi", StringComparison.Ordinal));
        // The entry without a profile is byte-identical.
        Assert.Equal(firstBytes, File.ReadAllBytes(firstFile));

        // Stored: M1's fields and the three profile fields only; no id, its hash, a name, a world or a priority.
        var text = File.ReadAllText(Path.Combine(catalogue, "entries", entry.EntryId.ToString("N") + ".json"));
        using var document = JsonDocument.Parse(text);
        Assert.Equal(EntryFields.Concat(new[] { "profile", "profileCounts", "profileHash" }).Order(StringComparer.Ordinal),
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "absent", "base", "bones", "other" },
            document.RootElement.GetProperty("profileCounts").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using var profile = JsonDocument.Parse(document.RootElement.GetProperty("profile").GetString()!);
        Assert.Equal("Bones", Assert.Single(profile.RootElement.EnumerateObject()).Name);
        Assert.DoesNotContain(FakeProfileReads.Tag, text, StringComparison.Ordinal);
        foreach (var never in new[] { "Name\"", "Character", "World", "Priority", "Template", "UniqueId", "Guid" })
            Assert.DoesNotContain(never, text, StringComparison.OrdinalIgnoreCase);

        // Read back exactly, and listed with its bone count; the entry without a profile lists none.
        var parsed = HumanCatalogueEntryCodec.TryParse(File.ReadAllBytes(Path.Combine(catalogue, "entries", entry.EntryId.ToString("N") + ".json")), out var problem);
        Assert.True(parsed is not null, problem);
        Assert.Equal(entry.Profile, parsed!.Profile);
        Assert.Equal(entry.ProfileCounts, parsed.ProfileCounts);
        Assert.Equal(HumanCatalogueEntryCodec.Serialize(entry), HumanCatalogueEntryCodec.Serialize(parsed));
        var list = store.List();
        Assert.Equal(0, list.UnreadableEntries);
        Assert.Null(list.Entries.Single(listing => listing.Label == "Rsc One").ProfileBones);
        Assert.Equal(3, list.Entries.Single(listing => listing.Label == "Rsc One CP").ProfileBones);
    }

    [Fact]
    public async Task AnEntryWithoutAProfileIsWrittenAndReadExactlyAsBefore()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = FullRead();
        var entry = (await HumanCatalogueCapture.CaptureAsync("Rsc One", store, Reads(read, read), () => Now, CancellationToken.None)).Entry!;
        var bytes = File.ReadAllBytes(Assert.Single(Directory.EnumerateFiles(Path.Combine(catalogue, "entries"))));

        var parsed = HumanCatalogueEntryCodec.TryParse(bytes, out var problem);

        Assert.True(parsed is not null, problem);
        Assert.Equal(HumanCatalogueEntry.CurrentFormat, parsed!.Format);
        Assert.Null(parsed.Profile);
        Assert.Null(parsed.ProfileHash);
        Assert.Null(parsed.ProfileCounts);
        Assert.Equal(bytes, HumanCatalogueEntryCodec.Serialize(parsed));
        Assert.Equal(bytes, HumanCatalogueEntryCodec.Serialize(entry));
        Assert.DoesNotContain("\"profile\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Null(Assert.Single(store.List().Entries).ProfileBones);
    }

    [Fact]
    public async Task TheFlagWithoutAProfileReadAndAProfileReadWithoutTheFlagAreBothRefusedBeforeAnyCopy()
    {
        var store = new HumanCatalogueStore(catalogue);
        var plain = FullRead();
        var read = WithProfile(plain);

        var missing = await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(plain, plain), () => Now, CancellationToken.None, withProfile: true);
        var unasked = await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(read, read), () => Now, CancellationToken.None);

        Assert.Equal("the Customize+ profile was not read", missing.Refusal);
        Assert.Equal("a Customize+ profile was read without the flag", unasked.Refusal);
        Assert.False(Directory.Exists(Path.Combine(catalogue, "blobs")) && Directory.EnumerateFiles(Path.Combine(catalogue, "blobs")).Any());
        Assert.False(Directory.Exists(Path.Combine(catalogue, "entries")) && Directory.EnumerateFiles(Path.Combine(catalogue, "entries")).Any());
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("lost")]
    public async Task AProfileThatChangesOrIsLostBetweenTheTwoReadsRefusesTheEntry(string how)
    {
        var store = new HumanCatalogueStore(catalogue);
        var plain = FullRead();
        var read = WithProfile(plain);
        var again = how == "changed"
            ? WithProfile(plain, ProfileSample.Json.Replace("1.05", "1.06", StringComparison.Ordinal))
            : read with { Profile = null };

        var result = await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(read, again), () => Now, CancellationToken.None, withProfile: true);

        Assert.Null(result.Entry);
        Assert.Equal(how == "changed" ? "the appearance changed during the capture" : "the Customize+ profile was not read", result.Refusal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(catalogue, "entries")));
    }

    [Fact]
    public async Task AProfileNeedsAComparableSkeletonSummaryAndAConsistentRead()
    {
        var store = new HumanCatalogueStore(catalogue);
        var plain = FullRead();
        var uncomparable = WithProfile(plain) with { SkeletonSummary = plain.SkeletonSummary };
        var good = WithProfile(plain);
        var inconsistent = good with { Profile = good.Profile! with { ProfileHash = new string('0', 64) } };
        var unversioned = good with { Versions = good.Versions with { CustomizePlus = null } };

        Assert.Equal("the Customize+ profile needs a skeleton summary the spawn can compare",
            (await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(uncomparable), () => Now, CancellationToken.None, withProfile: true)).Refusal);
        Assert.Equal("the Customize+ profile read is not consistent",
            (await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(inconsistent), () => Now, CancellationToken.None, withProfile: true)).Refusal);
        Assert.Equal("the Customize+ profile needs Customize+ 2.2.1.3 recorded",
            (await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(unversioned), () => Now, CancellationToken.None, withProfile: true)).Refusal);
    }

    [Fact]
    public async Task AProfileEntryWithAChangedFieldIsRefusedOnRead()
    {
        var store = new HumanCatalogueStore(catalogue);
        var read = WithProfile(FullRead());
        var entry = (await HumanCatalogueCapture.CaptureAsync("Rsc One CP", store, Reads(read, read), () => Now, CancellationToken.None, withProfile: true)).Entry!;
        Assert.Null(HumanCatalogueEntryCodec.Problem(entry));

        foreach (var (tampered, why) in new[]
        {
            (entry with { Format = "hv5-local-3" }, "unknown format"),
            (entry with { Versions = entry.Versions with { CustomizePlus = null } }, "a profile needs Customize+ 2.2.1.3 recorded"),
            (entry with { Format = HumanCatalogueEntry.CurrentFormat }, "unknown profile source"),
            (entry with { Format = HumanCatalogueEntry.CurrentFormat, ProfileSource = HumanCatalogueEntry.NoProfile }, "a profile in an entry without one"),
            (entry with { ProfileSource = HumanCatalogueEntry.NoProfile }, "unknown profile source"),
            (entry with { Profile = ProfileSample.Json, ProfileHash = HumanCatalogueEntry.HashOf(ProfileSample.Json) }, "invalid profile"),
            (entry with { Profile = null }, "invalid profile"),
            (entry with { ProfileHash = new string('0', 64) }, "invalid profile"),
            (entry with { ProfileCounts = null }, "invalid profile counts"),
            (entry with { ProfileCounts = entry.ProfileCounts! with { Bones = 4 } }, "invalid profile counts"),
            (entry with { ProfileCounts = entry.ProfileCounts! with { Base = 3 } }, "invalid profile counts"),
            (entry with { ProfileCounts = new HumanCatalogueProfileCounts(3, 0, 0, 3) }, "invalid profile counts"),
            (entry with { SkeletonSummary = ["skeleton partials: 1", "skeleton 0: file 0000000000000000, bones 100"] },
                "a profile needs a comparable skeleton summary"),
        })
        {
            Assert.Equal(why, HumanCatalogueEntryCodec.Problem(tampered));
            Assert.Throws<ArgumentException>(() => HumanCatalogueEntryCodec.Serialize(tampered));
        }
    }

    [Theory]
    [InlineData("capture Rsc One", true, "Rsc One", false)]
    [InlineData("capture Rsc One CP with-customize+", true, "Rsc One CP", true)]
    [InlineData("capture Rsc One with-customize", true, "Rsc One with-customize", false)]
    [InlineData("capture with-customize+", true, "with-customize+", false)]
    [InlineData("capture  with-customize+", false, "", true)]
    [InlineData("capture ", false, "", false)]
    [InlineData("capture", false, "", false)]
    [InlineData("list", false, "", false)]
    [InlineData("spawn Rsc One", false, "", false)]
    public void TheCaptureStepTakesTheFlagOnlyAsItsLastWord(string step, bool parsed, string label, bool withProfile)
    {
        Assert.Equal(parsed, HumanCatalogueCaptureCommand.TryParse(step, out var foundLabel, out var foundFlag));
        Assert.Equal(label, foundLabel);
        Assert.Equal(withProfile, foundFlag);
    }

    [Fact]
    public void TheFlagCannotBePartOfALabel() => Assert.Null(HumanCatalogueLabel.Normalize("with-customize+", out _));

    // ---------- helpers ----------

    private static readonly string[] EntryFields =
    [
        "blobs", "bodyType", "capturedUtc", "counts", "entryId", "format", "glamourerReference", "glamourerReferenceHash", "glamourerState",
        "glamourerStateHash", "label", "manipulations", "manipulationsHash", "paths", "profileSource", "race", "sex", "skeleton", "skeletonSummary",
        "tribe", "versions",
    ];

    private HumanCatalogueRead FullRead() => Read(
        Local(Write("ivcs/base.sklb", Sklb()), BaseSklb),
        Local(Write("ivcs/human.pbd", Pbd()), ReportedDeformer),
        Local(Write("top.mdl", Bytes(400)), Model),
        Local(Write("fx.avfx", Bytes(300)), Effect),
        Swap(Hair, OtherHair),
        Vanilla("chara/common/texture/sample_a.tex"));

    private HumanCatalogueRead Read(params AppearanceResourceEntry[] entries) =>
        new(mods, entries, Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic glamourer state")), "{\"FileVersion\":1,\"Equipment\":{}}",
            "synthetic-manipulations", VieraRava, 2, ["skeleton partials: 1", "skeleton 0: file 0000000000000000, bones 100"], Versions, "0:1:2");

    /// <summary>Each call returns the next read; the last one repeats.</summary>
    private static Func<CancellationToken, Task<HumanCatalogueReadResult>> Reads(params HumanCatalogueRead[] reads)
    {
        var next = 0;
        return _ => Task.FromResult(new HumanCatalogueReadResult(reads[Math.Min(next++, reads.Length - 1)], null));
    }

    private static AppearanceResourceEntry Local(string file, params string[] gamePaths) => new(file, gamePaths);

    private static AppearanceResourceEntry Vanilla(string gamePath) => new(gamePath, [gamePath]);

    private static AppearanceResourceEntry Swap(string gamePath, string target) => new(target, [gamePath]);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(mods, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

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

    private sealed class SkeletonComparer : IEqualityComparer<HumanCatalogueSkeleton?>
    {
        public bool Equals(HumanCatalogueSkeleton? x, HumanCatalogueSkeleton? y) =>
            x is not null && y is not null && x.RaceCode == y.RaceCode && x.Deformer == y.Deformer && x.Members.SequenceEqual(y.Members);

        public int GetHashCode(HumanCatalogueSkeleton? obj) => 0;
    }
}
#endif
