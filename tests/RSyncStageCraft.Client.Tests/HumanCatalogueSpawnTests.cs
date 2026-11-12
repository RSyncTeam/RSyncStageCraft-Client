#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-5 M2: one spawn from a catalogue entry through HV-4's composition with
/// the entry's plan: the match, the split Penumbra apply, phase 1 and the
/// skeleton, the entry's Glamourer state at alpha 0, phase 2, the optional
/// test scale and the show last; and the holds tied to the cleanup.
/// Synthetic identities, fake providers and synthetic files only: no game,
/// no IPC, no owner data.
/// </summary>
public sealed class HumanCatalogueSpawnTests : IDisposable
{
    private const uint Key = 0x5EED1234;
    private const string Prefix = "RSC HV-5 ";
    private const string EntryState = "c3RhdGU=";
    private const string EntryMeta = "RU5UUlk=";
    private const string Reference = "{\"FileVersion\":1,\"Equipment\":{\"Body\":{\"ItemId\":7,\"Apply\":true}},\"Customize\":{\"Race\":{\"Value\":1}}}";

    private static readonly string BlobRoot = OperatingSystem.IsWindows() ? @"X:\rsc-hv5\blobs" : "/rsc-hv5/blobs";

    private readonly FakeHumanActorScene scene = new();
    private readonly FakeHumanActorPenumbra penumbra;
    private readonly FakeHumanActorGlamourer glamourer;
    private readonly FakeHumanActorCustomizePlus customizePlus;
    private readonly FakeHumanActorWorldSource worlds = new();
    private readonly List<string> lines = [];
    private readonly List<string> folders = [];
    private HumanActorPenumbraStage? penumbraStage;
    private HumanActorGlamourerStage? glamourerStage;
    private HumanActorCustomizePlusStage? customizePlusStage;
    private Func<nint, string?> skeleton = _ => null;
    private Func<nint, string?> baseSkeleton = _ => null;
    private IHumanActorShowCheck? showCheck;
    /// <summary>The actor's synthetic human draw object (U-4): a new value is a replacement.</summary>
    private nint drawObject = 0x7000;

    public HumanCatalogueSpawnTests()
    {
        penumbra = new FakeHumanActorPenumbra(scene);
        glamourer = new FakeHumanActorGlamourer(scene) { StateJson = Reference };
        customizePlus = new FakeHumanActorCustomizePlus(scene);
        scene.Player.World = 9000;
        penumbra.Parents[200] = 200;
        customizePlus.Parents[200] = 200;
    }

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

    private static string Blob(string name) => Path.Combine(BlobRoot, name + ".blob");

    /// <summary>The entry's package: three stored files (one each of the group's skeleton and deformer) and one swap.</summary>
    private static HumanActorPenumbraPackage Package() => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FakeHumanActorPenumbra.TopModel] = Blob("top"),
            [FakeHumanActorPenumbra.Skeleton] = Blob("skl"),
            [FakeHumanActorPenumbra.Deformer] = Blob("pbd"),
            [FakeHumanActorPenumbra.SwapTarget] = FakeHumanActorPenumbra.SwapSource,
        }, 3, 1, 0, 0);

    private static readonly IReadOnlySet<string> Group =
        new HashSet<string>([FakeHumanActorPenumbra.Skeleton, FakeHumanActorPenumbra.Deformer], StringComparer.OrdinalIgnoreCase);

    private HumanActorSpawnPlan Plan(bool testScale = false, byte race = 1, byte tribe = 1, byte sex = 0, byte body = 1) =>
        new(race, tribe, sex, body, Group, address => baseSkeleton(address), address => skeleton(address),
            new HumanGlamourerPayload(EntryState, 0x06, Reference), true, testScale, showCheck, _ => drawObject);

    private HumanActorBaseline Baseline(IHumanActorBackend? backend = null, Func<(HumanActorPenumbraPackage?, string?, string?)>? package = null)
    {
        glamourerStage = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix) { KeySource = () => Key };
        customizePlusStage = new HumanActorCustomizePlusStage(customizePlus, lines.Add, Prefix) { AllowAbsent = true };
        penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix)
        {
            ReleaseByDeletion = true,
            ModTag = "RSC HV-5",
            TemporaryCollectionName = "RSC HV-5",
            Split = true,
            PackageSource = package ?? (() => (Package(), EntryMeta, null)),
        };
        return new HumanActorBaseline(backend ?? scene, () => null, lines.Add, penumbraStage, glamourerStage, customizePlusStage, worlds, Prefix);
    }

    /// <summary>Updates until the phase is reached or the actor is gone; the number of updates run.</summary>
    private static int RunUntil(HumanActorBaseline baseline, HumanActorPhase phase, int limit = 600)
    {
        for (var i = 1; i <= limit; i++)
        {
            baseline.OnFrameworkUpdate();
            if (baseline.Phase == phase || baseline.LiveCount == 0)
                return i;
        }
        return limit;
    }

    private HumanActorBaseline Visible(bool testScale = false)
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan(testScale)), string.Join(" | ", lines));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        return baseline;
    }

    private int IndexOf(string call, int from = 0)
    {
        var index = scene.Calls.IndexOf(call, from);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private int LastIndexOf(string call) => scene.Calls.LastIndexOf(call);

    [Fact]
    public void ACleanSpawnSplitsTheApplyConfirmsTwiceAndShowsLast()
    {
        var check = new FakeShowCheck();
        showCheck = check;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        // The collection, the mod and the assignment each on its own update (BC-8).
        var counts = new List<int>();
        for (var i = 0; i < 100 && baseline.Phase != HumanActorPhase.Visible; i++)
        {
            baseline.OnFrameworkUpdate();
            counts.Add(scene.Calls.Count);
        }
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        int UpdateOf(string call) => counts.FindIndex(count => count > scene.Calls.IndexOf(call));
        Assert.True(UpdateOf("penumbra create") < UpdateOf("penumbra mod"));
        Assert.True(UpdateOf("penumbra mod") < UpdateOf("penumbra assign 200"));

        // BC-1 and BC-2: W-3 and the Customize+ detach for every HV-5 spawn; no profile without the test scale.
        Assert.Contains($"world 200 {HumanActorW3.WorldId}", scene.Calls);
        Assert.True(IndexOf("customize+ setparent 200 -1") < IndexOf("penumbra create"));
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);
        // BC-3 and BC-9: the entry's package and manipulations, under HV-5's own names.
        Assert.Equal(Package().Paths.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            penumbra.ModPaths!.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(EntryMeta, penumbra.ModMeta);
        Assert.Equal("RSC HV-5", penumbra.ModTag);
        Assert.Equal("RSC HV-5", penumbra.CollectionName);

        // The entry's state, Equipment and Customization, after the rebuild and while still hidden.
        var apply = Assert.Single(glamourer.Log, entry => entry.Call == "apply");
        Assert.Equal((200, Key, 0x06UL), (apply.Index, apply.Key, apply.Flags));
        Assert.Equal(EntryState, glamourer.LastPayload);
        Assert.True(LastIndexOf("draw on 200") < IndexOf("glamourer apply 200"));
        // Show last (BC-7): alpha 1 once, after the apply and every confirmation.
        Assert.Single(scene.Calls, call => call == "alpha 200 1");
        Assert.True(IndexOf("glamourer apply 200") < IndexOf("alpha 200 1"));
        Assert.Equal("alpha 200 1", scene.Calls.Last(call => call.StartsWith("alpha 200", StringComparison.Ordinal)));
        var phaseOne = lines.FindIndex(line => line.StartsWith(Prefix + "Penumbra phase 1 confirmed: 2 of 2 required redirect(s) reported", StringComparison.Ordinal));
        var skeletonLine = lines.IndexOf(Prefix + "phase 1: the actor's base skeleton equals the entry's; the rest of the look is checked after " +
            "Glamourer; still alpha 0; Glamourer next.");
        var readback = lines.FindIndex(line => line.Contains("readback equal to the stored reference in every applied field (FileVersion, Equipment, " +
            "Customize; 1 field(s) the entry does not apply not compared)", StringComparison.Ordinal));
        var phaseTwo = lines.FindIndex(line => line.StartsWith(Prefix + "Penumbra phase 2 confirmed: 4 of 4 packaged path(s) resolved to the package", StringComparison.Ordinal));
        var shown = lines.FindIndex(line => line.StartsWith(Prefix + "visible: shown last", StringComparison.Ordinal) && line.EndsWith(
            "Skeleton matched on the actor; deformer resolved for its collection and equal to the player's, checked again before the show; " +
            "use of the deformer unproven (GAP-K4 open).",
            StringComparison.Ordinal));
        Assert.True(phaseOne >= 0 && phaseOne < skeletonLine && skeletonLine < readback && readback < phaseTwo && phaseTwo < shown,
            string.Join(" | ", lines));
        Assert.Equal(1, check.Begun);
    }

    [Fact]
    public void RemovalFollowsHv4sOrderAndRevertsWithTheEntrysFlags()
    {
        var baseline = Visible();
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.Equal(new[] { "alpha 200 0", "glamourer revert 200", "glamourer canunlock 200", "penumbra delete", "draw off 200", "delete 200" },
            scene.Calls.Skip(from).ToArray());
        Assert.Equal(0x06UL, glamourer.Log.Single(entry => entry.Call == "revert").Flags);
        Assert.Null(baseline.Stop);
        Assert.Empty(penumbra.Live);
    }

    [Theory]
    [InlineData(2, 1, 0, 1, "the actor's race is not the entry's")]
    [InlineData(1, 2, 0, 1, "the actor's clan is not the entry's")]
    [InlineData(1, 1, 1, 1, "the actor's gender is not the entry's")]
    [InlineData(1, 1, 0, 3, "the actor's body type is not the entry's")]
    public void TheMatchHaltsBeforeAnyProviderCallOrWorldWrite(byte race, byte tribe, byte sex, byte body, string why)
    {
        var baseline = Baseline();
        Assert.False(baseline.Spawn(Plan(race: race, tribe: tribe, sex: sex, body: body)));
        Assert.Equal("match: " + why, baseline.Stop);
        Assert.False(scene.Exists(200));
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("penumbra", StringComparison.Ordinal) ||
            call.StartsWith("glamourer", StringComparison.Ordinal) || call.StartsWith("customize+", StringComparison.Ordinal) ||
            call.StartsWith("world", StringComparison.Ordinal));
    }

    [Fact]
    public void PhaseOneWaitsOnlyForTheSkeletonGroupAndHaltsAfterTheBound()
    {
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.Skeleton);
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Dressing);
        Assert.StartsWith("phase 1: 1 of 2 required redirect(s) were not reported", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain(glamourer.Log, entry => entry.Call == "apply");
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.False(scene.Exists(200));
        Assert.Empty(penumbra.Live);
    }

    [Fact]
    public void PhaseOneDefersOtherRedirectsAndPhaseTwoRequiresEveryOne()
    {
        // The first read of the actor's resources omits the body model: phase 1 passes, phase 2 sees it.
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.TopModel);
        penumbra.ActorOmitsReads = 1;
        Visible();
        Assert.Contains(lines, line => line.StartsWith(Prefix + "Penumbra phase 1 confirmed: 2 of 2 required redirect(s) reported", StringComparison.Ordinal) &&
            line.Contains("1 redirect(s) not yet reported (checked again after Glamourer)", StringComparison.Ordinal));
    }

    [Fact]
    public void PhaseTwoHaltsWhenAPackagedRedirectIsNeverReported()
    {
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.TopModel);
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.StartsWith("phase 2: 1 of 3 packaged redirect(s) were not reported", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void APackagedPathResolvedElsewhereHaltsAtOnce()
    {
        penumbra.ActorResolvesElsewhere = FakeHumanActorPenumbra.TopModel;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Dressing);
        Assert.Equal("phase 1: 1 packaged game path(s) resolved elsewhere after the redraw", baseline.Stop);
        Assert.DoesNotContain(glamourer.Log, entry => entry.Call == "apply");
    }

    [Fact]
    public void ABaseSkeletonThatNeverMatchesHaltsBeforeGlamourerAndOneThatSettlesPasses()
    {
        baseSkeleton = _ => "the actor's base skeleton differs from the entry's (partial 0 differs in names)";
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Dressing);
        Assert.Equal("skeleton: the actor's base skeleton differs from the entry's (partial 0 differs in names)", baseline.Stop);
        Assert.DoesNotContain(glamourer.Log, entry => entry.Call == "apply");
        Assert.DoesNotContain("alpha 200 1", scene.Calls);

        // A base skeleton still loading for a few updates is read again within the bound.
        var reads = 0;
        using var calm = new HumanCatalogueSpawnTests();
        calm.baseSkeleton = _ => ++reads <= 3 ? "the actor's base skeleton is not read yet" : null;
        calm.Visible();
        Assert.True(reads > 3);
    }

    [Fact]
    public void AWholeSkeletonThatNeverMatchesAfterGlamourerHaltsHiddenAndReverted()
    {
        skeleton = _ => "the actor's skeleton summary differs from the entry's (partial 2 differs in names)";
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal("skeleton: the actor's skeleton summary differs from the entry's (partial 2 differs in names)", baseline.Stop);
        Assert.Single(glamourer.Log, entry => entry.Call == "apply");
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ASavedHairAndGearThatDifferFromThePlayersCurrentOnesPassPhaseOneAndAreConfirmedAfterGlamourer()
    {
        // U-1: before Glamourer the copy wears the player's current hair and gear: its whole skeleton
        // differs (hair partial) and the entry's gear redirect is not reported yet; only the base matches.
        var applied = false;
        glamourer.DuringApply = () => applied = true;
        skeleton = _ => applied ? null : "the actor's skeleton summary differs from the entry's (partial 2 differs in names)";
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.TopModel);
        penumbra.ActorOmitsReads = 1;
        var baseline = Visible();
        Assert.Contains(lines, line => line.StartsWith(Prefix + "Penumbra phase 1 confirmed: 2 of 2 required redirect(s) reported", StringComparison.Ordinal) &&
            line.Contains("1 redirect(s) not yet reported (checked again after Glamourer)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(Prefix + "Penumbra phase 2 confirmed: 4 of 4 packaged path(s) resolved to the package",
            StringComparison.Ordinal));
        Assert.True(IndexOf("glamourer apply 200") < IndexOf("alpha 200 1"));
        Assert.Null(baseline.Stop);
    }

    /// <summary>A show check whose answers the test sets.</summary>
    private sealed class FakeShowCheck : IHumanActorShowCheck
    {
        public int Begun { get; private set; }
        public bool Pending { get; set; }
        public string? Answer { get; set; }
        public Action? DuringPoll { get; set; }

        public void Begin() => Begun++;

        public string? Poll(out bool pending)
        {
            DuringPoll?.Invoke();
            pending = Pending;
            return Pending ? null : Answer;
        }
    }

    [Fact]
    public void ADeformerChangeFoundBeforeTheShowHaltsTheActorWhileHidden()
    {
        var check = new FakeShowCheck { Answer = "deformer differs from the current one (GAP-K4)" };
        showCheck = check;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal(1, check.Begun);
        Assert.Equal("show check: deformer differs from the current one (GAP-K4)", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AShowCheckStillRunningDelaysTheShowAndIsBounded()
    {
        var check = new FakeShowCheck { Pending = true };
        showCheck = check;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Settling);
        for (var i = 0; i < HumanActorBaseline.GlamourerQuiet + 10; i++)
            baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Settling, baseline.Phase);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        check.Pending = false;
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);

        using var stuck = new HumanCatalogueSpawnTests();
        stuck.showCheck = new FakeShowCheck { Pending = true };
        var never = stuck.Baseline();
        Assert.True(never.Spawn(stuck.Plan()));
        RunUntil(never, HumanActorPhase.Visible, 1000);
        Assert.Equal($"show check: no answer within {HumanActorBaseline.RedrawLimit} updates", never.Stop);
        Assert.DoesNotContain("alpha 200 1", stuck.scene.Calls);
    }

    [Fact]
    public void AReadbackThatDiffersIsUncertainNeverShownAndRevertedWithTheEntrysFlags()
    {
        glamourer.StateJson = "{\"FileVersion\":1,\"Equipment\":{\"Body\":{\"ItemId\":8,\"Apply\":true}},\"Customize\":{\"Race\":{\"Value\":1}}}";
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal("glamourer: the readback differs from the stored reference in Equipment; the state is uncertain", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Equal(0x06UL, glamourer.Log.Single(entry => entry.Call == "revert").Flags);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void TheTestScaleAppliesHv4sProfileBeforeTheShowAndIsDeletedFirst()
    {
        var baseline = Visible(testScale: true);
        var profile = Assert.Single(customizePlus.Profiles);
        Assert.Equal(HumanActorCustomizePlusStage.PayloadJson, profile.Value.Json);
        Assert.Equal(scene.At(200).Name, profile.Value.Name);
        Assert.True(IndexOf("customize+ apply 200") < IndexOf("alpha 200 1"));
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.Equal("customize+ delete", scene.Calls[from + 1]);
        Assert.Empty(customizePlus.Profiles);
    }

    [Fact]
    public void WithoutCustomizePlusTheDetachIsRecordedAndAnyPluginChangeHaltsTheActor()
    {
        customizePlus.Version = null;
        customizePlus.Api = null;
        var baseline = Visible();
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("customize+ setparent", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(Prefix + "customize+: not loaded; no copy-parent entry to clear", StringComparison.Ordinal));
        customizePlus.Generation++;
        baseline.OnFrameworkUpdate();
        Assert.Equal("customize+: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AGlamourerRebuildBetweenThePhasesIsWaitedFor()
    {
        glamourer.DuringApply = () => scene.DrawAppears = false;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Settling);
        Assert.Equal(HumanActorPhase.Settling, baseline.Phase);
        for (var i = 0; i < 5; i++)
            baseline.OnFrameworkUpdate();
        // No new draw object yet: phase 2 is not read, and the actor is not shown.
        Assert.Equal(HumanActorPhase.Settling, baseline.Phase);
        Assert.DoesNotContain(lines, line => line.Contains("Penumbra phase 2 confirmed", StringComparison.Ordinal));
        scene.DrawAppears = true;
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Contains(lines, line => line.Contains("Penumbra phase 2 confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void PhaseTwoIsNotReadUntilTheGlamourerQuietWindowHasPassed()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan()));
        RunUntil(baseline, HumanActorPhase.Settling);
        for (var i = 1; i < HumanActorBaseline.GlamourerQuiet; i++)
            baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Settling, baseline.Phase);
        Assert.DoesNotContain(lines, line => line.Contains("Penumbra phase 2 confirmed", StringComparison.Ordinal));
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
    }

    [Fact]
    public void ARebuildAfterPhaseTwoClearsItAndItIsReadAgainOnTheNewDrawObject()
    {
        // The draw object is lost after phase 2 (at the test-scale apply); on the new one a redirect is never reported.
        customizePlus.DuringApply = () => scene.DrawAppears = false;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(Plan(testScale: true)));
        RunUntil(baseline, HumanActorPhase.Visible, 200);
        Assert.Contains(lines, line => line.Contains("Penumbra phase 2 confirmed", StringComparison.Ordinal));
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.TopModel);
        scene.DrawAppears = true;
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.StartsWith("phase 2: 1 of 3 packaged redirect(s) were not reported", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void TheLabelIsNotPartOfThePlanAndTheActorCarriesAFreshSyntheticName()
    {
        Assert.DoesNotContain(typeof(HumanActorSpawnPlan).GetProperties(), property => property.PropertyType == typeof(string));
        Visible();
        var name = scene.At(200).Name!;
        Assert.True(HumanPuppetName.IsWellFormed(name));
        Assert.NotEqual(FakeHumanActorScene.PlayerName, name);
        Assert.DoesNotContain(scene.Calls, call => call.Contains("Rsc One", StringComparison.Ordinal));
    }

    [Fact]
    public void ASpawnWithAPlanNeedsEveryStage()
    {
        var hv1 = new HumanActorBaseline(scene, () => null, lines.Add);
        Assert.False(hv1.Spawn(Plan()));
        Assert.Contains("RSC HV-1 refused: a spawn from an entry needs the Penumbra, Glamourer and Customize+ stages and W-3.", lines);
        Assert.DoesNotContain("create", scene.Calls);
    }

    // ---- The holds, tied to the cleanup (amendment 1 §3) ----

    private (HumanCatalogueSpawnSession Session, HumanCatalogueHoldSet Holds, HumanCatalogueSpawnReady Ready, List<string> Journal) Session(
        IHumanActorBackend? backend = null)
    {
        var fixture = HumanCatalogueFixture.Create();
        folders.Add(fixture.Folder);
        var store = new HumanCatalogueStore(fixture.Root);
        var (ready, refusal) = HumanCatalogueSpawnPreparation.PrepareAsync(HumanCatalogueFixture.Label, false, store, fixture.Current(), default)
            .GetAwaiter().GetResult();
        Assert.True(ready is not null, refusal);
        var journal = new List<string>();
        var holds = new HumanCatalogueHoldSet(fixture.Root, new HumanCatalogueProcess(4242, 99), lines.Add, Prefix)
        {
            MarkerWriter = (_, _) => true,
            JournalWriter = (_, _, hashes) =>
            {
                journal.Add(string.Join(",", hashes));
                return true;
            },
        };
        HumanCatalogueSpawnSession? made = null;
        var baseline = Baseline(backend, () => made!.CurrentPackage());
        made = new HumanCatalogueSpawnSession(baseline, penumbraStage!, holds);
        return (made, holds, ready!, journal);
    }

    private HumanActorBaseline BaselineOf(HumanCatalogueSpawnSession spawn) =>
        (HumanActorBaseline)typeof(HumanCatalogueSpawnSession).GetField("baseline", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!.GetValue(spawn)!;

    private static void Tick(HumanActorBaseline baseline, HumanCatalogueSpawnSession spawn, int updates)
    {
        for (var i = 0; i < updates; i++)
        {
            baseline.OnFrameworkUpdate();
            spawn.Settle();
        }
    }

    [Fact]
    public void ACleanRemovalReleasesTheHoldsLastAfterBothConfirmations()
    {
        var (spawn, holds, ready, journal) = Session();
        var baseline = BaselineOf(spawn);
        Assert.True(spawn.Start(ready, Plan()));
        Tick(baseline, spawn, 100);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Equal(ready.Holds.Count, holds.Active);
        Assert.All(ready.Holds, hold => Assert.True(hold.Stream.CanRead));
        Assert.True(baseline.Remove());
        spawn.Settle();
        Assert.Equal(0, holds.Active);
        Assert.Equal(0, holds.Retained);
        Assert.All(ready.Holds, hold => Assert.False(hold.Stream.CanRead));
        Assert.Empty(journal);
        var removed = lines.FindIndex(line => line.StartsWith(Prefix + "removed: deleted", StringComparison.Ordinal));
        var released = lines.IndexOf(Prefix + $"holds released last: {ready.Holds.Count} file(s), after the actor's deletion and the collection's removal were confirmed.");
        Assert.True(removed >= 0 && released > removed);
    }

    [Fact]
    public async Task AnUnconfirmedCollectionRemovalRetainsTheHoldsWithTheJournalAndRefusesTheNextSpawn()
    {
        var (spawn, holds, ready, journal) = Session();
        var baseline = BaselineOf(spawn);
        Assert.True(spawn.Start(ready, Plan()));
        Tick(baseline, spawn, 100);
        penumbra.DeleteAnswer = false;
        baseline.Remove();
        spawn.Settle();
        Assert.Equal(0, holds.Active);
        Assert.Equal(ready.Holds.Count, holds.Retained);
        Assert.All(ready.Holds, hold => Assert.True(hold.Stream.CanRead));
        Assert.Equal(string.Join(",", ready.Holds.Select(hold => hold.Hash).Order(StringComparer.Ordinal)),
            string.Join(",", journal.Single().Split(',').Order(StringComparer.Ordinal)));
        Assert.Contains(lines, line => line.StartsWith(Prefix + $"holds retained: {ready.Holds.Count} file(s) stay open and protected " +
            "(the collection's removal was not confirmed); retention journal written", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("holds released last", StringComparison.Ordinal));

        // No new spawn while holds are retained; the new spawn's own holds are closed.
        var fixture = HumanCatalogueFixture.Create();
        folders.Add(fixture.Folder);
        var (again, _) = await HumanCatalogueSpawnPreparation.PrepareAsync(HumanCatalogueFixture.Label, false, new HumanCatalogueStore(fixture.Root),
            fixture.Current(), default);
        Assert.False(spawn.Start(again!, Plan()));
        Assert.All(again!.Holds, hold => Assert.False(hold.Stream.CanRead));
        Assert.Equal(ready.Holds.Count, holds.Retained);
    }

    [Fact]
    public void ALostActorRetainsTheHolds()
    {
        var (spawn, holds, ready, journal) = Session();
        var baseline = BaselineOf(spawn);
        Assert.True(spawn.Start(ready, Plan()));
        Tick(baseline, spawn, 100);
        scene.Replace(200);
        Tick(baseline, spawn, 1);
        Assert.Equal(0, baseline.LiveCount);
        Assert.Equal(ready.Holds.Count, holds.Retained);
        Assert.Single(journal);
        Assert.Contains(lines, line => line.Contains("(the actor's deletion was not confirmed)", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActorDeletionThatDoesNotReadBackRetainsTheHolds()
    {
        var (spawn, holds, ready, _) = Session(new UndeletableBackend(scene));
        var baseline = BaselineOf(spawn);
        Assert.True(spawn.Start(ready, Plan()));
        Tick(baseline, spawn, 100);
        Assert.False(baseline.Remove());
        spawn.Settle();
        Assert.Equal(1, baseline.UnconfirmedDeletions);
        Assert.Equal(ready.Holds.Count, holds.Retained);
    }

    [Fact]
    public void AHaltBeforePenumbraReleasesTheHolds()
    {
        var (spawn, holds, ready, journal) = Session();
        Assert.False(spawn.Start(ready, Plan(race: 2)));
        Assert.Equal(0, holds.Active + holds.Retained);
        Assert.Empty(journal);
        Assert.All(ready.Holds, hold => Assert.False(hold.Stream.CanRead));
    }

    // ---------- U-4 (r2): a redraw after the show ----------

    private int Shows() => scene.Calls.Count(call => call == "alpha 200 1");

    [Theory]
    [InlineData("replaced")]
    [InlineData("lost")]
    public void ARedrawAfterTheShowHidesTheActorOnTheUpdateItIsSeen(string how)
    {
        var baseline = Visible();
        baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        var before = scene.Calls.Count;
        if (how == "replaced")
            drawObject = 0x7100;
        else
            scene.At(200).DrawEnabled = false;
        baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Reconfirming, baseline.Phase);
        Assert.Equal(["alpha 200 0"], scene.Calls.Skip(before).Where(call => call.StartsWith("alpha 200", StringComparison.Ordinal)));
        Assert.Equal(0f, scene.At(200).Alpha);
        Assert.Contains(lines, line => line.StartsWith(Prefix + $"redraw seen while visible: the human draw object was {how} 2 update(s) after the show; " +
            "alpha 0 on the update it was seen", StringComparison.Ordinal));
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void ASuccessfulReconfirmationShowsTheActorAgainWithNothingAppliedAgain()
    {
        var check = new FakeShowCheck();
        showCheck = check;
        var baseline = Visible();
        var applies = glamourer.Log.Count(entry => entry.Call == "apply");
        var reads = glamourer.Log.Count(entry => entry.Call == "getjson");
        var mods = penumbra.Created.Count;
        drawObject = 0x7100;
        baseline.OnFrameworkUpdate();
        var hidden = scene.Calls.Count;
        // Replaced again while hidden: everything is read on the newest one.
        baseline.OnFrameworkUpdate();
        drawObject = 0x7200;
        RunUntil(baseline, HumanActorPhase.Visible);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Equal("alpha 200 1", scene.Calls.Skip(hidden).Last(call => call.StartsWith("alpha 200", StringComparison.Ordinal)));
        Assert.Equal(2, Shows());
        // Confirmed again, never applied again.
        Assert.Equal(applies, glamourer.Log.Count(entry => entry.Call == "apply"));
        Assert.Equal(reads + 1, glamourer.Log.Count(entry => entry.Call == "getjson"));
        Assert.Equal(mods, penumbra.Created.Count);
        Assert.DoesNotContain(glamourer.Log, entry => entry.Call == "revert");
        Assert.Equal(2, check.Begun);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "Penumbra reconfirmed after the redraw", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(Prefix + "glamourer: read back again after the redraw (no apply)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(Prefix + "visible again: reconfirmation 1 held on the current draw object", StringComparison.Ordinal) &&
            line.EndsWith("nothing applied again; alpha 1.", StringComparison.Ordinal));
        // The newest draw object is now the one watched.
        baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
    }

    [Theory]
    [InlineData("skeleton", "reconfirm skeleton: the actor's skeleton differs", true)]
    [InlineData("glamourer", "reconfirm glamourer: the readback differs from the stored reference in Equipment; the state is uncertain", true)]
    [InlineData("deformer", "reconfirm show check: deformer differs from the current one (GAP-K4)", true)]
    [InlineData("resolution", "reconfirm phase 2: ", true)]
    [InlineData("penumbra parent", "reconfirm phase 2: the actor's Penumbra parent is no longer -1", false)]
    [InlineData("customize+ parent", "reconfirm customize+: the actor's Customize+ parent does not read -1", true)]
    [InlineData("home world", "reconfirm show: the actor's home world is not the selected world", false)]
    [InlineData("generation", "reconfirm glamourer: a plugin loaded or unloaded since the gate", false)]
    [InlineData("no draw object", "reconfirm: no human draw object within 300 updates of the redraw", true)]
    public void AFailedReconfirmationNeverShowsTheActorAgainAndFollowsTheCleanup(string how, string stop, bool reverted)
    {
        var check = new FakeShowCheck();
        showCheck = check;
        var baseline = Visible();
        drawObject = 0x7100;
        baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Reconfirming, baseline.Phase);
        var hidden = scene.Calls.Count;
        switch (how)
        {
            case "skeleton":
                skeleton = _ => "the actor's skeleton differs from the entry's (synthetic)";
                break;
            case "glamourer":
                glamourer.StateJson = Reference.Replace("\"ItemId\":7", "\"ItemId\":8", StringComparison.Ordinal);
                break;
            case "deformer":
                check.Answer = "deformer differs from the current one (GAP-K4)";
                break;
            case "resolution":
                penumbra.ActorResolvesElsewhere = FakeHumanActorPenumbra.TopModel;
                break;
            case "penumbra parent":
                penumbra.Parents[200] = 5;
                break;
            case "customize+ parent":
                customizePlus.Parents[200] = 5;
                break;
            case "home world":
                scene.At(200).World = 1234;
                break;
            case "generation":
                glamourer.Generation++;
                break;
            case "no draw object":
                scene.At(200).DrawEnabled = false;
                break;
        }
        RunUntil(baseline, HumanActorPhase.Visible, 1000);
        Assert.NotNull(baseline.Stop);
        Assert.StartsWith(stop, baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha 200 1", scene.Calls.Skip(hidden));
        Assert.Equal(1, Shows());
        Assert.False(scene.Exists(200));
        Assert.Equal(reverted ? 1 : 0, glamourer.Log.Count(entry => entry.Call == "revert"));
        if (reverted)
            Assert.Equal(0x06UL, glamourer.Log.Single(entry => entry.Call == "revert").Flags);
        Assert.Equal(1, glamourer.Log.Count(entry => entry.Call == "apply"));
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("with the redraw")]
    [InlineData("reconfirming")]
    public void AnOwnershipLossIsNeverWrittenAndFollowsTheLossPath(string when)
    {
        var baseline = Visible();
        if (when == "reconfirming")
        {
            drawObject = 0x7100;
            baseline.OnFrameworkUpdate();
            Assert.Equal(HumanActorPhase.Reconfirming, baseline.Phase);
        }
        else if (when == "with the redraw")
        {
            drawObject = 0x7100;
        }
        var before = scene.Calls.Count;
        scene.Replace(200);
        baseline.OnFrameworkUpdate();
        // Another object now sits at index 200: nothing is written to it (the collection is still released by its id).
        Assert.DoesNotContain(scene.Calls.Skip(before), call => call.Contains(" 200", StringComparison.Ordinal));
        Assert.Equal(1f, scene.At(200).Alpha);
        Assert.NotNull(baseline.Stop);
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(1, baseline.Residue);
        Assert.Equal(0, baseline.LiveCount);
        Assert.DoesNotContain(glamourer.Log, entry => entry.Call == "revert");
        Assert.DoesNotContain(lines, line => line.Contains("redraw seen while visible", StringComparison.Ordinal) && when != "reconfirming");
    }

    [Fact]
    public void ADrawObjectReplacedJustBeforeEveryShowIsReadAgainAndBounded()
    {
        var check = new FakeShowCheck();
        showCheck = check;
        var baseline = Visible();
        drawObject = 0x7100;
        baseline.OnFrameworkUpdate();
        var hidden = scene.Calls.Count;
        // Each time the show is about to happen, the draw object has been replaced again.
        check.DuringPoll = () => drawObject += 0x100;
        RunUntil(baseline, HumanActorPhase.Visible, 1000);
        Assert.NotNull(baseline.Stop);
        Assert.Equal($"reconfirm: the human draw object was still being replaced {HumanActorBaseline.RedrawLimit} updates after the redraw", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls.Skip(hidden));
        Assert.False(scene.Exists(200));
        Assert.Equal(0x06UL, glamourer.Log.Single(entry => entry.Call == "revert").Flags);
    }

    [Fact]
    public void AnUnloadDuringReconfirmationUsesTheExistingCleanupWithoutWaiting()
    {
        var baseline = Visible();
        drawObject = 0x7100;
        baseline.OnFrameworkUpdate();
        Assert.Equal(HumanActorPhase.Reconfirming, baseline.Phase);
        baseline.Unload();
        Assert.False(scene.Exists(200));
        Assert.Equal(0, baseline.LiveCount);
        Assert.Equal(0x06UL, glamourer.Log.Single(entry => entry.Call == "revert").Flags);
        Assert.Equal(1, Shows());
    }

    [Fact]
    public void ASpawnFromAnEntryNeedsTheDrawObjectRead()
    {
        var baseline = Baseline();
        Assert.False(baseline.Spawn(Plan() with { DrawObject = null }));
        Assert.Contains(Prefix + "refused: a spawn from an entry needs the draw-object read.", lines);
        Assert.DoesNotContain("create", scene.Calls);
    }

    /// <summary>The scene, except that a deletion is recorded and not done: the slot never reads back empty.</summary>
    private sealed class UndeletableBackend(FakeHumanActorScene scene) : IHumanActorBackend
    {
        public bool IsAvailable => scene.IsAvailable;
        public nint LocalPlayer => scene.LocalPlayer;
        public int ObjectTableLength => scene.ObjectTableLength;
        public HumanPuppetFacts ReadFacts(nint address) => scene.ReadFacts(address);
        public nint[] ComEntriesSnapshot() => scene.ComEntriesSnapshot();
        public int CreateCharacter() => scene.CreateCharacter();
        public nint ComEntry(int comIndex) => scene.ComEntry(comIndex);
        public nint ComObjectByIndex(int comIndex) => scene.ComObjectByIndex(comIndex);
        public int ComIndexOf(nint address) => scene.ComIndexOf(address);
        public nint ObjectTableEntry(int globalIndex) => scene.ObjectTableEntry(globalIndex);
        public HumanPuppetScanEntry? ScanEntry(int globalIndex) => scene.ScanEntry(globalIndex);
        public void WriteName(nint actor, string name) => scene.WriteName(actor, name);
        public void WriteHomeWorld(nint actor, ushort homeWorld) => scene.WriteHomeWorld(actor, homeWorld);
        public void CopyCharacter(nint actor, nint source) => scene.CopyCharacter(actor, source);
        public HumanActorPlacement? ReadPlacement(nint address) => scene.ReadPlacement(address);
        public void Place(nint actor, HumanActorPlacement placement) => scene.Place(actor, placement);
        public void MakeUntargetable(nint actor) => scene.MakeUntargetable(actor);
        public void SetAlpha(nint actor, float alpha) => scene.SetAlpha(actor, alpha);
        public bool IsReadyToDraw(nint actor) => scene.IsReadyToDraw(actor);
        public void EnableDraw(nint actor) => scene.EnableDraw(actor);
        public void DisableDraw(nint actor) => scene.DisableDraw(actor);
        public void DeleteComObject(int comIndex) => scene.Calls.Add($"delete ignored {comIndex}");
    }
}
#endif
