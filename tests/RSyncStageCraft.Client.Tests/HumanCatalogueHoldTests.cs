#if RSC_DEVELOPMENT
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-5 M2: the spawn's preparation (read only, D7, D3-a and D15), the held
/// leases on the stored files, the retention journal and the hold set
/// (amendment 1 §3, D13 a). Synthetic files and entries only: no game, no
/// IPC, and no real character, mod or profile.
/// </summary>
public sealed class HumanCatalogueHoldTests : IDisposable
{
    private readonly List<string> folders = [];
    private readonly List<string> lines = [];

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
                // A handle detached on purpose keeps its file open until the test process exits.
            }
        }
    }

    private HumanCatalogueFixture Fixture(string? customizePlus = HumanCatalogueFixture.CustomizePlusVersion, IReadOnlyList<string>? summary = null)
    {
        var fixture = HumanCatalogueFixture.Create(customizePlus, summary: summary);
        folders.Add(fixture.Folder);
        return fixture;
    }

    private static (HumanCatalogueSpawnReady? Ready, string? Refusal) Prepare(HumanCatalogueFixture fixture, HumanCatalogueSpawnRead? current = null,
        bool testScale = false, string label = HumanCatalogueFixture.Label) =>
        HumanCatalogueSpawnPreparation.PrepareAsync(label, testScale, new HumanCatalogueStore(fixture.Root), current ?? fixture.Current(), default)
            .GetAwaiter().GetResult();

    private static void Refused(string why, (HumanCatalogueSpawnReady? Ready, string? Refusal) result)
    {
        result.Ready?.Abandon();
        Assert.Null(result.Ready);
        Assert.Equal(why, result.Refusal);
    }

    /// <summary>Every file of the store with its bytes: the store is unchanged when this is.</summary>
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    // ---------- the preparation ----------

    [Fact]
    public void APreparationHoldsEveryStoredFileAndBuildsThePackageWithoutChangingTheStore()
    {
        var fixture = Fixture();
        var before = Snapshot(fixture.Root);
        var (ready, refusal) = Prepare(fixture);
        Assert.True(ready is not null, refusal);
        try
        {
            Assert.Equal(before, Snapshot(fixture.Root));
            var entry = ready!.Entry;
            Assert.Equal(HumanCatalogueFixture.Label, entry.Label);
            Assert.Equal(entry.Blobs.Count, ready.Holds.Count);
            Assert.Equal(3, ready.Holds.Count);
            Assert.All(ready.Holds, hold => Assert.True(hold.Stream.CanRead && hold.Stream.Position == 0));

            // Each stored game path to its held file; each swap as stored.
            var package = ready.Package;
            Assert.Equal((3, 1), (package.Redirects, package.Swaps));
            Assert.Equal(entry.Paths.Count, package.Paths.Count);
            foreach (var path in entry.Paths)
            {
                var target = package.Paths[path.GamePath];
                if (path.Blob is { } hash)
                    Assert.Equal(Path.Combine(fixture.Root, "blobs", hash + ".blob"), target);
                else
                    Assert.Equal(path.Swap, target);
            }
            Assert.Equal(FakeHumanActorPenumbra.SwapSource, package.Paths[FakeHumanActorPenumbra.SwapTarget]);

            // Phase 1 waits for the skeleton group, deformer included.
            Assert.True(ready.Deformer);
            Assert.Contains(FakeHumanActorPenumbra.Skeleton, ready.PhaseOneRequired);
            Assert.Contains(FakeHumanActorPenumbra.Deformer, ready.PhaseOneRequired);
            Assert.DoesNotContain(FakeHumanActorPenumbra.TopModel, ready.PhaseOneRequired);
            Assert.True(HumanCatalogueSkeletonSummary.Comparable(entry.SkeletonSummary));
        }
        finally
        {
            ready?.Abandon();
        }
        Assert.All(ready!.Holds, hold => Assert.False(hold.Stream.CanRead));
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public void AnUnknownLabelIsRefused()
    {
        var fixture = Fixture();
        Refused("the label is unknown: no entry has that label", Prepare(fixture, label: "Rsc Two"));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("Penumbra")]
    [InlineData("Glamourer")]
    [InlineData("Customize+")]
    public void AnyRecordedVersionThatDiffersIsRefused(string which)
    {
        var fixture = Fixture();
        var now = HumanCatalogueFixture.Versions;
        now = which switch
        {
            "game" => now with { Game = "2026.11.04.0000.0000" },
            "Penumbra" => now with { Penumbra = "1.7.2.2" },
            "Glamourer" => now with { Glamourer = "1.7.1.4" },
            _ => now with { CustomizePlus = null },
        };
        var (ready, refusal) = Prepare(fixture, fixture.Current() with { Versions = now });
        Assert.Null(ready);
        Assert.StartsWith($"the recorded {which} version differs (", refusal, StringComparison.Ordinal);
        Assert.EndsWith("an entry is spawned only with the versions it was captured with", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionsAreComparedExactlyAndNoneRecordedEqualsNoneRunning()
    {
        var recorded = new HumanCatalogueVersions("1", "2", "3", null);
        Assert.Null(HumanCatalogueSpawnPreparation.VersionMismatch(recorded, recorded with { }));
        Assert.Equal("the recorded Customize+ version differs (none recorded, 4 now); an entry is spawned only with the versions it was captured with",
            HumanCatalogueSpawnPreparation.VersionMismatch(recorded, recorded with { CustomizePlus = "4" }));
        Assert.NotNull(HumanCatalogueSpawnPreparation.VersionMismatch(recorded, recorded with { Game = "1 " }));
    }

    [Fact]
    public void TheTestScaleNeedsCustomizePlus()
    {
        var fixture = Fixture(customizePlus: null);
        Refused("the test scale needs Customize+ loaded", Prepare(fixture, testScale: true));
        var (ready, refusal) = Prepare(fixture);
        Assert.True(ready is not null, refusal);
        ready!.Abandon();
    }

    [Fact]
    public void AnEntryWhoseSkeletonSummaryCannotBeComparedIsRefused()
    {
        Assert.False(HumanCatalogueSkeletonSummary.Comparable([]));
        Assert.False(HumanCatalogueSkeletonSummary.Comparable(["skeleton partials: 0"]));
        Assert.False(HumanCatalogueSkeletonSummary.Comparable(["skeleton partials: 2", "skeleton 0: bones 1, names a, parents b, reference pose c"]));
        Assert.True(HumanCatalogueSkeletonSummary.Comparable(HumanCatalogueFixture.Summary));
    }

    [Fact]
    public void TheSkeletonSummaryIsNormalizedAsTheCaptureStoredItAndComparedExactly()
    {
        var facts = new[]
        {
            new KeyValuePair<string, string>("actor skeleton partials", "1"),
            new KeyValuePair<string, string>("actor skeleton 0", "file 0123456789abcdef, bones 100, names 0a, parents 0b, reference pose 0c"),
        };
        var actor = HumanCatalogueSkeletonSummary.Normalize(facts, "actor");
        Assert.Equal(HumanCatalogueFixture.Summary, actor);
        Assert.Null(HumanCatalogueSkeletonSummary.Mismatch(HumanCatalogueFixture.Summary, actor));
        var other = actor.Select(line => line.Replace("names 0a", "names 0d", StringComparison.Ordinal)).ToList();
        var why = HumanCatalogueSkeletonSummary.Mismatch(HumanCatalogueFixture.Summary, other);
        Assert.StartsWith("the actor's skeleton summary differs from the entry's (", why, StringComparison.Ordinal);
        Assert.DoesNotContain("0a", why, StringComparison.Ordinal);
        Assert.DoesNotContain("0d", why, StringComparison.Ordinal);
    }

    // ---------- r4: a partial slot the game left empty ----------

    private static readonly string[] FiveSlots = [.. HumanCatalogueFixture.FiveSlotSummary];

    /// <summary>The five-slot summary with line <paramref name="index"/> (0 is the count line) replaced, or removed when null.</summary>
    private static string[] FiveSlotsWith(int index, string? line) =>
        line is null ? FiveSlots.Where((_, at) => at != index).ToArray() : FiveSlots.Select((old, at) => at == index ? line : old).ToArray();

    public static TheoryData<string, string[]> ComparableWithEmptySlots => new()
    {
        { "the observed five slots, slot 4 empty", FiveSlots },
        {
            "three slots, slot 1 empty",
            ["skeleton partials: 3", "skeleton 0: bones 100, names 0a, parents 0b, reference pose 0c", "skeleton 1: no resource",
                "skeleton 2: bones 12, names 2a, parents 2b, reference pose 2c"]
        },
        { "five slots, slots 2 and 4 empty", FiveSlotsWith(3, "skeleton 2: no resource") },
        { "a bone count alone", ["skeleton partials: 2", "skeleton 0: bones 100", "skeleton 1: no resource"] },
    };

    [Theory]
    [MemberData(nameof(ComparableWithEmptySlots))]
    public void AnEmptyNonBaseSlotIsComparable(string shape, string[] summary)
    {
        Assert.True(HumanCatalogueSkeletonSummary.Comparable(summary), shape);
    }

    public static TheoryData<string, string[]> NotComparable => new()
    {
        { "no lines", [] },
        { "a count of zero", ["skeleton partials: 0"] },
        { "the count alone", ["skeleton partials: 5"] },
        { "the base slot empty", FiveSlotsWith(1, "skeleton 0: no resource") },
        { "a missing slot", FiveSlotsWith(4, null) },
        { "a missing last slot", FiveSlotsWith(5, null) },
        { "a repeated slot", FiveSlotsWith(5, "skeleton 3: bones 2, names 00000000000000a3, parents 00000000000000b3, reference pose 00000000000000c3") },
        { "slots out of order", [FiveSlots[0], FiveSlots[1], FiveSlots[2], FiveSlots[4], FiveSlots[3], FiveSlots[5]] },
        { "an extra slot", [.. FiveSlots, "skeleton 5: no resource"] },
        { "the count not first", [.. FiveSlots[1..], FiveSlots[0]] },
        { "a second count", [.. FiveSlots, "skeleton partials: 5"] },
        { "a count with a leading zero", FiveSlotsWith(0, "skeleton partials: 05") },
        { "a count with a sign", FiveSlotsWith(0, "skeleton partials: +5") },
        { "a count with a trailing space", FiveSlotsWith(0, "skeleton partials: 5 ") },
        { "no human draw object", ["skeleton: no human draw object"] },
        { "no skeleton", ["skeleton: none"] },
        { "unreadable after four slots", [.. FiveSlots[..5], "skeleton: unreadable (InvalidOperationException)"] },
        { "unreadable after every slot", [.. FiveSlots, "skeleton: unreadable (InvalidOperationException)"] },
        { "an empty slot spelled otherwise", FiveSlotsWith(5, "skeleton 4: no resources") },
        { "an empty slot with a trailing space", FiveSlotsWith(5, "skeleton 4: no resource ") },
        { "an empty slot in another case", FiveSlotsWith(5, "skeleton 4: No resource") },
        { "a bone count missing", FiveSlotsWith(2, "skeleton 1: bones , names 00000000000000a1") },
        { "a bone count run into the next field", FiveSlotsWith(2, "skeleton 1: bones 80names 00000000000000a1") },
        { "a file hash left in", FiveSlotsWith(2, "skeleton 1: file 0123456789abcdef, bones 80, names 00000000000000a1") },
        { "an empty value", FiveSlotsWith(5, "skeleton 4: ") },
    };

    [Theory]
    [MemberData(nameof(NotComparable))]
    public void AMissingMalformedOrUnreadableSummaryIsNotComparable(string shape, string[] summary)
    {
        Assert.False(HumanCatalogueSkeletonSummary.Comparable(summary), shape);
    }

    [Fact]
    public void AnEntryWithAnEmptyNonBaseSlotIsPreparedWithItsSlotsAsStored()
    {
        var fixture = Fixture(summary: HumanCatalogueFixture.FiveSlotSummary);
        var before = Snapshot(fixture.Root);
        var (ready, refusal) = Prepare(fixture);
        try
        {
            Assert.True(ready is not null, refusal);
            Assert.Equal(HumanCatalogueFixture.FiveSlotSummary, ready!.Entry.SkeletonSummary);
            Assert.Equal(ready.Entry.Blobs.Count, ready.Holds.Count);
        }
        finally
        {
            ready?.Abandon();
        }
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    public static TheoryData<string, string[]> RefusedEntries => new()
    {
        { "the base slot empty", FiveSlotsWith(1, "skeleton 0: no resource") },
        { "unreadable after four slots", [.. FiveSlots[..5], "skeleton: unreadable (InvalidOperationException)"] },
    };

    [Theory]
    [MemberData(nameof(RefusedEntries))]
    public void AnEntryWhoseSummaryIsNotComparableIsRefusedBeforeAnyHold(string shape, string[] summary)
    {
        var fixture = Fixture(summary: summary);
        Assert.False(HumanCatalogueSkeletonSummary.Comparable(summary), shape);
        var before = Snapshot(fixture.Root);
        Refused("the entry's skeleton summary cannot be compared", Prepare(fixture));
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public void AnEmptySlotIsNormalizedUnchangedAndComparedExactlyByItsIndex()
    {
        Assert.Equal("no resource", HumanCatalogueEntryCodec.WithoutFileHash("no resource"));
        var facts = new List<KeyValuePair<string, string>> { new("actor skeleton partials", "5") };
        for (var slot = 0; slot < 4; slot++)
            facts.Add(new($"actor skeleton {slot}", "file 0123456789abcdef, " + FiveSlots[slot + 1][$"skeleton {slot}: ".Length..]));
        facts.Add(new("actor skeleton 4", "no resource"));
        var actor = HumanCatalogueSkeletonSummary.Normalize(facts, "actor");
        Assert.Equal(HumanCatalogueFixture.FiveSlotSummary, actor);
        Assert.Null(HumanCatalogueSkeletonSummary.Mismatch(HumanCatalogueFixture.FiveSlotSummary, actor));
        Assert.Null(HumanCatalogueSkeletonSummary.BaseMismatch(HumanCatalogueFixture.FiveSlotSummary, actor));

        // A resource on the actor where the entry's slot is empty, and the reverse: the whole summary differs at that slot only.
        const string filled = "skeleton 4: bones 9, names 00000000000000a4, parents 00000000000000b4, reference pose 00000000000000c4";
        string[] withFour = [.. FiveSlots[..5], filled];
        foreach (var (stored, other) in new[] { (FiveSlots, withFour), (withFour, FiveSlots) })
        {
            var why = HumanCatalogueSkeletonSummary.Mismatch(stored, other);
            Assert.StartsWith("the actor's skeleton summary differs from the entry's (", why, StringComparison.Ordinal);
            Assert.Contains("partial 0 same; partial 1 same; partial 2 same; partial 3 same; partial 4 differs", why, StringComparison.Ordinal);
            Assert.DoesNotContain("00000000000000a4", why, StringComparison.Ordinal);
            Assert.Null(HumanCatalogueSkeletonSummary.BaseMismatch(stored, other));
        }

        // The same empty slot at another index is a difference: slots are compared by index, never compacted.
        string[] emptyAtThree = [FiveSlots[0], FiveSlots[1], FiveSlots[2], FiveSlots[3], "skeleton 3: no resource",
            "skeleton 4: bones 2, names 00000000000000a3, parents 00000000000000b3, reference pose 00000000000000c3"];
        Assert.True(HumanCatalogueSkeletonSummary.Comparable(emptyAtThree));
        Assert.NotNull(HumanCatalogueSkeletonSummary.Mismatch(HumanCatalogueFixture.FiveSlotSummary, emptyAtThree));
    }

    [Fact]
    public void ADeformerWhoseContentDiffersIsRefusedBeforeAnyHold()
    {
        var fixture = Fixture();
        var other = fixture.WriteMod("other/human.pbd", HumanCatalogueFixture.Pbd([101, 1801, 201]));
        var current = fixture.Current(resources => resources.Select(entry =>
            entry.GamePaths.Contains(FakeHumanActorPenumbra.Deformer) ? new AppearanceResourceEntry(other, entry.GamePaths) : entry).ToList());
        var before = Snapshot(fixture.Root);
        Refused("deformer differs from the current one (GAP-K4)", Prepare(fixture, current));
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public void ADeformerThePlayerNoLongerHasIsADifference()
    {
        var fixture = Fixture();
        var current = fixture.Current(resources => resources.Where(entry => !entry.GamePaths.Contains(FakeHumanActorPenumbra.Deformer)).ToList());
        Refused("deformer differs from the current one (GAP-K4)", Prepare(fixture, current));
    }

    [Fact]
    public void AVanillaDeformerOnBothSidesIsEqual()
    {
        var recorded = HumanCatalogueFixture.Create(HumanCatalogueFixture.CustomizePlusVersion, deformer: false);
        folders.Add(recorded.Folder);
        var current = recorded.Current(resources =>
            [.. resources, new AppearanceResourceEntry(SkeletonResourcePaths.ReportedDeformerGamePath, [SkeletonResourcePaths.ReportedDeformerGamePath])]);
        var (ready, refusal) = Prepare(recorded, current);
        Assert.True(ready is not null, refusal);
        Assert.False(ready!.Deformer);
        ready.Abandon();
    }

    public static TheoryData<string, string> Uncomparable => new()
    {
        { "swap", "the player's deformer is swapped to another game path, so it cannot be compared (GAP-K4)" },
        { "outside", "the player's deformer is not an existing file inside the mod folder, so it cannot be compared (GAP-K4)" },
        { "missing", "the player's deformer is not an existing file inside the mod folder, so it cannot be compared (GAP-K4)" },
        { "two", "the player's deformer has more than one source, so it cannot be compared (GAP-K4)" },
        { "empty", "the player's deformer could not be read, so it cannot be compared (GAP-K4)" },
        { "none", "the player's resource paths could not be read, so the deformer cannot be compared (GAP-K4)" },
    };

    [Theory]
    [MemberData(nameof(Uncomparable))]
    public void ADeformerThatCannotBeComparedIsRefused(string how, string why)
    {
        var fixture = Fixture();
        var outside = Path.Combine(fixture.Folder, "elsewhere", "human.pbd");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, HumanCatalogueFixture.Pbd());
        AppearanceResourceEntry Replace(AppearanceResourceEntry entry) => how switch
        {
            "swap" => new AppearanceResourceEntry("chara/xls/bonedeformer/other.pbd", entry.GamePaths),
            "outside" => new AppearanceResourceEntry(outside, entry.GamePaths),
            "empty" => new AppearanceResourceEntry(fixture.WriteMod("empty/human.pbd", []), entry.GamePaths),
            _ => new AppearanceResourceEntry(Path.Combine(fixture.Mods, "gone", "human.pbd"), entry.GamePaths),
        };
        var current = how switch
        {
            "none" => fixture.Current() with { PlayerResources = null },
            "two" => fixture.Current(resources =>
                [.. resources, new AppearanceResourceEntry(fixture.WriteMod("second/human.pbd", HumanCatalogueFixture.Pbd()), [FakeHumanActorPenumbra.Deformer])]),
            _ => fixture.Current(resources => resources.Select(entry => entry.GamePaths.Contains(FakeHumanActorPenumbra.Deformer) ? Replace(entry) : entry).ToList()),
        };
        Refused(why, Prepare(fixture, current));
    }

    [Theory]
    [InlineData("damaged", "does not verify")]
    [InlineData("missing", "is missing")]
    [InlineData("resized", "has the wrong size")]
    public void AStoredFileThatIsNotTheEntrysIsReportedAndKeptAsItIs(string how, string problem)
    {
        var fixture = Fixture();
        var (ready, _) = Prepare(fixture);
        var blob = ready!.Entry.Blobs[0];
        ready.Abandon();
        var file = Path.Combine(fixture.Root, "blobs", blob.Hash + ".blob");
        var bytes = File.ReadAllBytes(file);
        switch (how)
        {
            case "damaged":
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(file, bytes);
                break;
            case "missing":
                File.Delete(file);
                break;
            default:
                File.WriteAllBytes(file, [.. bytes, 0]);
                break;
        }
        var before = Snapshot(fixture.Root);
        Refused($"a stored file of the entry {problem} ({blob.Hash[..8]}); it was kept as it is, and nothing was created",
            Prepare(fixture));
        // Nothing replaced or deleted.
        Assert.Equal(before, Snapshot(fixture.Root));
    }

    [Fact]
    public void ARefusalAfterSomeHoldsClosesThem()
    {
        var fixture = Fixture();
        var (ready, _) = Prepare(fixture);
        var last = ready!.Entry.Blobs[^1];
        ready.Abandon();
        File.Delete(Path.Combine(fixture.Root, "blobs", last.Hash + ".blob"));
        Assert.Null(Prepare(fixture).Ready);
        // Every other stored file can be replaced again: no hold stayed open.
        foreach (var blob in ready.Entry.Blobs.Take(ready.Entry.Blobs.Count - 1))
        {
            var file = Path.Combine(fixture.Root, "blobs", blob.Hash + ".blob");
            using var write = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    // ---------- the held lease ----------

    [WindowsFact]
    public void AHeldFileCannotBeWrittenReplacedOrDeletedButCanBeRead()
    {
        var fixture = Fixture();
        var (ready, refusal) = Prepare(fixture);
        Assert.True(ready is not null, refusal);
        var file = ready!.Holds[0].FilePath;
        Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Delete(file));
        Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
        Assert.Throws<IOException>(() => File.Copy(ready.Holds[1].FilePath, file, overwrite: true));
        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(ready.Holds[0].Size, reader.Length);
        ready.Abandon();
        File.Delete(file);
    }

    [WindowsFact]
    public void ADetachedHandleKeepsTheFileProtectedAfterItsStreamIsGone()
    {
        var fixture = Fixture();
        var (ready, refusal) = Prepare(fixture);
        Assert.True(ready is not null, refusal);
        var file = ready!.Holds[0].FilePath;
        DetachAndDrop(ready.Holds[0]);
        foreach (var other in ready.Holds.Skip(1))
            other.Dispose();
        ready = null;
        // Disposed, unreferenced and finalized: the operating system still has the file open.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Throws<IOException>(() => File.Delete(file));
        Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void DetachAndDrop(HumanCatalogueHold hold)
    {
        Assert.True(HumanCatalogueHoldSet.DetachHandle(hold.Stream));
        Assert.True(hold.Stream.SafeFileHandle.IsClosed);
        Assert.False(HumanCatalogueHoldSet.DetachHandle(hold.Stream));
        hold.Dispose();
    }

    // ---------- the retention journal ----------

    private static readonly HumanCatalogueProcess Game = new(4242, 638_000_000_000_000_000);

    private string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rsc-hv5-journal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        folders.Add(folder);
        return folder;
    }

    private static string Hash(int seed) => Convert.ToHexString(SHA256.HashData([(byte)seed])).ToLowerInvariant();

    [Fact]
    public void TheJournalListsOnlyHashesAndTheGameProcess()
    {
        var root = Folder();
        Assert.True(HumanCatalogueJournal.TryWrite(root, Game, [Hash(2), Hash(1), Hash(2)]));
        var text = File.ReadAllText(Path.Combine(root, HumanCatalogueJournal.FileName));
        Assert.Contains("\"format\":\"hv5-retained-1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"processId\":4242", text, StringComparison.Ordinal);
        Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([HumanCatalogueJournal.FileName], Directory.EnumerateFiles(root).Select(file => Path.GetFileName(file)).ToList());
        Assert.Equal(HumanCatalogueJournalState.SameProcess, HumanCatalogueJournal.AtLoad(root, Game, out var listed));
        Assert.Equal(2, listed);
        Assert.True(File.Exists(Path.Combine(root, HumanCatalogueJournal.FileName)));
        Assert.False(HumanCatalogueJournal.TryWrite(root, Game, ["not-a-hash"]));
    }

    [Theory]
    [InlineData(4243, 638_000_000_000_000_000L)]
    [InlineData(4242, 638_000_000_000_000_001L)]
    public void AJournalOfAnotherGameProcessIsCleared(int id, long start)
    {
        var root = Folder();
        Assert.True(HumanCatalogueJournal.TryWrite(root, Game, [Hash(1)]));
        Assert.Equal(HumanCatalogueJournalState.Cleared, HumanCatalogueJournal.AtLoad(root, new HumanCatalogueProcess(id, start), out _));
        Assert.False(File.Exists(Path.Combine(root, HumanCatalogueJournal.FileName)));
        Assert.Equal(HumanCatalogueJournalState.None, HumanCatalogueJournal.AtLoad(root, Game, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"hv5-retained-2\",\"processId\":1,\"processStartTicksUtc\":1,\"blobs\":[]}")]
    [InlineData("{\"format\":\"hv5-retained-1\",\"processId\":1,\"processStartTicksUtc\":1,\"blobs\":[\"x\"]}")]
    [InlineData("{\"format\":\"hv5-retained-1\",\"processId\":1,\"processStartTicksUtc\":1,\"blobs\":[],\"path\":\"x\"}")]
    public void AJournalThatCannotBeReadFailsClosedAndIsKept(string text)
    {
        var root = Folder();
        var file = Path.Combine(root, HumanCatalogueJournal.FileName);
        File.WriteAllText(file, text);
        Assert.Equal(HumanCatalogueJournalState.Unreadable, HumanCatalogueJournal.AtLoad(root, Game, out _));
        Assert.True(File.Exists(file));
    }

    // ---------- the hold set ----------

    private (HumanCatalogueHoldSet Set, List<string> Order, HumanCatalogueSpawnReady Ready) HoldSet(bool journal = true, bool detach = true,
        bool marker = true)
    {
        var fixture = Fixture();
        var (ready, refusal) = Prepare(fixture);
        Assert.True(ready is not null, refusal);
        var order = new List<string>();
        var set = new HumanCatalogueHoldSet(fixture.Root, Game, line =>
        {
            order.Add("log");
            lines.Add(line);
        }, "RSC HV-5 ")
        {
            MarkerWriter = (process, count) =>
            {
                Assert.Equal(Game, process);
                order.Add("marker " + count);
                return marker;
            },
            JournalWriter = (_, _, hashes) =>
            {
                order.Add("journal " + hashes.Count());
                return journal;
            },
            Detacher = _ => detach,
        };
        return (set, order, ready!);
    }

    [Fact]
    public void RetainingWritesTheJournalFirstKeepsTheHoldsOpenAndRefusesTheNextTake()
    {
        var (set, order, ready) = HoldSet();
        Assert.True(set.Take(ready.Holds));
        Assert.False(set.Take(ready.Holds));
        set.Retain("the collection's removal was not confirmed");
        Assert.Equal(["marker 3", "journal 3", "log"], order);
        Assert.Equal((0, 3), (set.Active, set.Retained));
        Assert.All(ready.Holds, hold => Assert.True(hold.Stream.CanRead));
        Assert.Equal("RSC HV-5 holds retained: 3 file(s) stay open and protected (the collection's removal was not confirmed); retention journal " +
            "written; same-process marker set; no HV-5 spawn until unload; nothing is retried.", lines.Single());
        Assert.False(set.Take([]));
        // A release afterwards has nothing active: the retained holds stay open.
        set.ReleaseConfirmed();
        Assert.Equal(3, set.Retained);
        Assert.All(ready.Holds, hold => Assert.True(hold.Stream.CanRead));
        ready.Abandon();
    }

    [Fact]
    public void AJournalThatCannotBeWrittenIsSaidAndTheHoldsAreStillRetained()
    {
        var (set, _, ready) = HoldSet(journal: false);
        Assert.True(set.Take(ready.Holds));
        set.Retain("the actor's deletion was not confirmed");
        Assert.Contains("retention journal NOT WRITTEN", lines.Single(), StringComparison.Ordinal);
        Assert.Equal(3, set.Retained);
        ready.Abandon();
    }

    [Fact]
    public void WhenBothJournalWritesFailTheMarkerStillRecordsTheRetentionAndNeitherFailingIsSaid()
    {
        var (set, _, ready) = HoldSet(journal: false);
        Assert.True(set.Take(ready.Holds));
        set.Retain("the actor's deletion was not confirmed");
        set.DetachAll();
        Assert.Contains("retention journal NOT WRITTEN; same-process marker set", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("same-process marker set; the next load in this game process refuses HV-5 spawn and capture.", lines[1],
            StringComparison.Ordinal);

        var (none, _, other) = HoldSet(journal: false, marker: false);
        Assert.True(none.Take(other.Holds));
        none.DetachAll();
        Assert.EndsWith("NEITHER RECORD WAS KEPT: a reload in this game process would not refuse; restart the game before any HV-5 step.", lines[^1],
            StringComparison.Ordinal);
        ready.Abandon();
        other.Abandon();
    }

    [Fact]
    public void AConfirmedReleaseClosesEveryHoldAndWritesNoJournal()
    {
        var (set, order, ready) = HoldSet();
        Assert.True(set.Take(ready.Holds));
        set.ReleaseConfirmed();
        Assert.Equal(["log"], order);
        Assert.Equal((0, 0), (set.Active, set.Retained));
        Assert.All(ready.Holds, hold => Assert.False(hold.Stream.CanRead));
        Assert.True(set.Take(ready.Holds));
    }

    [Fact]
    public void AtUnloadEveryHoldIsDetachedAfterTheJournal()
    {
        var (set, order, ready) = HoldSet();
        Assert.True(set.Take(ready.Holds));
        set.Retain("the actor's deletion was not confirmed");
        set.DetachAll();
        Assert.Equal(["marker 3", "journal 3", "log", "marker 3", "journal 3", "log"], order);
        Assert.Equal((0, 0), (set.Active, set.Retained));
        Assert.StartsWith("RSC HV-5 unload: 3 held file(s) detached and left open until the game exits; retention journal written", lines[^1],
            StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("hold lost", StringComparison.Ordinal));
        ready.Abandon();
    }

    [Fact]
    public void AHandleThatCannotBeDetachedIsReportedLostAndClosed()
    {
        var (set, _, ready) = HoldSet(detach: false);
        Assert.True(set.Take(ready.Holds));
        set.DetachAll();
        Assert.Equal(ready.Holds.Select(hold => $"RSC HV-5 hold lost, file unprotected ({hold.ShortHash})."), lines.Skip(1));
        Assert.All(ready.Holds, hold => Assert.False(hold.Stream.CanRead));
    }

    [Fact]
    public void AHoldNamesItsFileByHashOnly()
    {
        var (_, _, ready) = HoldSet();
        Assert.All(ready.Holds, hold => Assert.Equal($"held file {hold.Hash[..8]}", hold.ToString()));
        Assert.DoesNotContain(ready.Holds[0].FilePath, ready.ToString(), StringComparison.Ordinal);
        ready.Abandon();
    }
}

/// <summary>
/// A synthetic catalogue for HV-5 M2: one entry, "Rsc One", captured by the
/// real capture from synthetic mod files under a temporary folder, whose game
/// paths are the fake Penumbra's: a body model, the base skeleton and the
/// deformer (redirects), and one swap. Recorded with fixed synthetic versions.
/// </summary>
internal sealed class HumanCatalogueFixture
{
    public const string Label = "Rsc One";
    public const string CustomizePlusVersion = "2.2.1.3";

    public static readonly HumanCatalogueVersions Versions = new("2026.11.03.0000.0000", "1.7.2.1", "1.7.1.3", CustomizePlusVersion);

    public static readonly IReadOnlyList<string> Summary =
        ["skeleton partials: 1", "skeleton 0: bones 100, names 0a, parents 0b, reference pose 0c"];

    /// <summary>
    /// r4: the shape of a stored summary the M2 r3 run refused, five slots
    /// with the last one empty, with the observed bone counts and synthetic
    /// hashes (no catalogue content).
    /// </summary>
    public static readonly IReadOnlyList<string> FiveSlotSummary =
    [
        "skeleton partials: 5",
        "skeleton 0: bones 168, names 00000000000000a0, parents 00000000000000b0, reference pose 00000000000000c0",
        "skeleton 1: bones 80, names 00000000000000a1, parents 00000000000000b1, reference pose 00000000000000c1",
        "skeleton 2: bones 5, names 00000000000000a2, parents 00000000000000b2, reference pose 00000000000000c2",
        "skeleton 3: bones 2, names 00000000000000a3, parents 00000000000000b3, reference pose 00000000000000c3",
        "skeleton 4: no resource",
    ];

    private HumanCatalogueFixture(string folder, HumanCatalogueVersions versions)
    {
        Folder = folder;
        Mods = Path.Combine(folder, "mods");
        Root = Path.Combine(folder, "catalogue");
        RecordedVersions = versions;
        Directory.CreateDirectory(Mods);
    }

    /// <summary>The temporary folder holding the mods and the catalogue.</summary>
    public string Folder { get; }

    public string Mods { get; }

    /// <summary>The catalogue's root (the store and its journal).</summary>
    public string Root { get; }

    public HumanCatalogueVersions RecordedVersions { get; }

    /// <summary>The player's resources at the capture (and, unchanged, now).</summary>
    public IReadOnlyList<AppearanceResourceEntry> Resources { get; private set; } = [];

    public const string HairSkeleton = "chara/human/c0101/skeleton/hair/h0001/skl_c0101h0001.sklb";

    public static HumanCatalogueFixture Create(string? customizePlus = CustomizePlusVersion, bool deformer = true, bool hair = false,
        IReadOnlyList<string>? summary = null)
    {
        var fixture = new HumanCatalogueFixture(Path.Combine(Path.GetTempPath(), "rsc-hv5-spawn", Guid.NewGuid().ToString("N")),
            Versions with { CustomizePlus = customizePlus });
        List<AppearanceResourceEntry> resources =
        [
            new(fixture.WriteMod("Body/top.mdl", RandomNumberGenerator.GetBytes(400)), [FakeHumanActorPenumbra.TopModel]),
            new(fixture.WriteMod("Skeletons/skl_c0101b0001.sklb", Sklb()), [FakeHumanActorPenumbra.Skeleton]),
            new(FakeHumanActorPenumbra.SwapSource, [FakeHumanActorPenumbra.SwapTarget]),
        ];
        if (hair)
            resources.Add(new(fixture.WriteMod("Skeletons/skl_c0101h0001.sklb", Sklb()), [HairSkeleton]));
        if (deformer)
            resources.Add(new(fixture.WriteMod("Skeletons/human.pbd", Pbd()), [FakeHumanActorPenumbra.Deformer]));
        else
            resources.Add(new(fixture.WriteMod("Body/fx.avfx", RandomNumberGenerator.GetBytes(300)), ["vfx/common/eff/sample_a1.avfx"]));
        fixture.Resources = resources;
        var read = new HumanCatalogueRead(fixture.Mods, resources, Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic glamourer state")),
            "{\"FileVersion\":1,\"Equipment\":{}}", "synthetic-manipulations", new AppearanceCharacterRace(1, 1, 0), 1, summary ?? Summary,
            fixture.RecordedVersions, "0:1:2");
        var result = HumanCatalogueCapture.CaptureAsync(Label, new HumanCatalogueStore(fixture.Root),
            _ => Task.FromResult(new HumanCatalogueReadResult(read, null)), () => new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).GetAwaiter().GetResult();
        if (result.Entry is null)
            throw new InvalidOperationException("the synthetic capture was refused: " + result.Refusal);
        return fixture;
    }

    /// <summary>Now: the recorded versions and the player's resources as captured, or as <paramref name="change"/> makes them.</summary>
    public HumanCatalogueSpawnRead Current(Func<IReadOnlyList<AppearanceResourceEntry>, IReadOnlyList<AppearanceResourceEntry>>? change = null) =>
        new(RecordedVersions, Mods, change is null ? Resources : change(Resources));

    public string WriteMod(string name, byte[] bytes)
    {
        var path = Path.Combine(Mods, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A synthetic .sklb header (version 1301), as the classifier tests build it.</summary>
    public static byte[] Sklb()
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x736B6C62);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes("1301")));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        return data;
    }

    /// <summary>A synthetic deformer with entries for the given race codes; the last has a 4-byte body after the tables.</summary>
    public static byte[] Pbd(ushort[]? races = null)
    {
        races ??= [101, 1801];
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
}

/// <summary>Runs only on Windows, where file sharing modes are enforced; reported as skipped elsewhere.</summary>
file sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows file sharing modes only; not run on this operating system.";
    }
}
#endif
