#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-2: HV-1's visible human with one actor-specific Penumbra temporary
/// collection: the player's resolved resources as one ordinary temporary mod
/// (any file type, so a custom skeleton package is no different), a
/// non-forced assignment to the actor alone, RSC's own undraw and draw (the
/// draw object seen released, then rebuilt; no Penumbra redraw), and the mod
/// and the collection removed before the actor is undrawn and deleted.
/// Synthetic identities, paths and a fake Penumbra only: no game, no IPC.
/// </summary>
public sealed class HumanActorPenumbraTests
{
    private readonly FakeHumanActorScene scene = new();
    private readonly FakeHumanActorPenumbra penumbra;
    private readonly List<string> lines = [];

    public HumanActorPenumbraTests() => penumbra = new FakeHumanActorPenumbra(scene);

    /// <summary>The stage with the fake's containment and mod folder (the real folder check is exercised once, by its own test).</summary>
    private static HumanActorPenumbraStage Stage(IHumanActorPenumbra fake, Action<string> log) =>
        new(fake, log, "RSC HV-2 ")
        {
            Contained = FakeHumanActorPenumbra.Contained,
            ModFolderReadable = folder => folder == FakeHumanActorPenumbra.ModRoot,
        };

    private HumanActorBaseline Baseline(IHumanActorBackend? backend = null) =>
        new(backend ?? scene, () => null, lines.Add, Stage(penumbra, lines.Add), "RSC HV-2 ");

    private int IndexOf(string call) => IndexIn(scene.Calls, call);

    private static int IndexIn(List<string> calls, string call)
    {
        var index = calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private static int LastIndexIn(List<string> calls, string call)
    {
        var index = calls.LastIndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private static void Run(HumanActorBaseline baseline, int updates)
    {
        for (var i = 0; i < updates; i++)
            baseline.OnFrameworkUpdate();
    }

    /// <summary>Spawn, ready, draw, and the update on which the stage runs.</summary>
    private const int ToApply = 3;

    /// <summary>After the stage: one update sees the draw object released, the next draws again, the next confirms.</summary>
    private const int ToShow = 3;

    private HumanActorBaseline Applied(IHumanActorBackend? backend = null)
    {
        var baseline = Baseline(backend);
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Run(baseline, ToApply);
        Assert.Equal(HumanActorPhase.WaitingForRelease, baseline.Phase);
        return baseline;
    }

    private HumanActorBaseline Shown()
    {
        var baseline = Applied();
        Run(baseline, ToShow);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        return baseline;
    }

    [Fact]
    public void TheCollectionIsAssignedToTheActorAloneAndShownOnlyAfterTheRebuild()
    {
        // Penumbra's copy hook records the last copy's source: the self-copy.
        penumbra.Parents[200] = 200;
        var baseline = Applied();
        var actor = scene.At(200);
        // HV-1's sequence first, then Penumbra, still at alpha 0, then RSC's own undraw.
        Assert.True(IndexOf("draw on 200") < IndexOf("penumbra parent 200 -1"));
        Assert.True(IndexOf("penumbra parent 200 -1") < IndexOf("penumbra create"));
        Assert.True(IndexOf("penumbra create") < IndexOf("penumbra mod"));
        Assert.True(IndexOf("penumbra mod") < IndexOf("penumbra assign 200"));
        Assert.True(IndexOf("penumbra assign 200") < IndexOf("draw off 200"));
        Assert.Equal("draw off 200", scene.Calls[^1]);
        Assert.Equal(0f, actor.Alpha);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        // The assignment reached the actor's own identity, not the player's.
        Assert.Equal(-1, penumbra.ParentOf(200));
        Assert.Equal(penumbra.Created.Single(), penumbra.AssignedTo(200));
        Assert.Null(penumbra.AssignedTo(0));
        Assert.Equal(FakeHumanActorPenumbra.PlayerCollection, penumbra.CollectionFor(0)!.Value.EffectiveCollection);

        // The release is seen, then the draw is requested again no sooner than two updates after the undraw.
        Run(baseline, 1);
        Assert.Equal(HumanActorPhase.WaitingForRelease, baseline.Phase);
        Assert.Equal("draw off 200", scene.Calls[^1]);
        Run(baseline, 1);
        Assert.Equal(HumanActorPhase.WaitingForRebuild, baseline.Phase);
        Assert.Equal(new[] { "draw off 200", "alpha 200 0", "draw on 200" }, scene.Calls.TakeLast(3).ToArray());
        Assert.Equal(0f, actor.Alpha);
        Assert.Contains("RSC HV-2 undrawn: no human draw object after the undraw, ready again after 2 update(s); draw requested again at alpha 0.", lines);

        Run(baseline, 1);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Equal(1f, actor.Alpha);
        Assert.True(IndexOf("draw off 200") < LastIndexIn(scene.Calls, "draw on 200"));
        Assert.True(LastIndexIn(scene.Calls, "draw on 200") < LastIndexIn(scene.Calls, "alpha 200 1"));
        Assert.Equal(2, scene.Calls.Count(call => call == "draw on 200"));
        Assert.Contains("RSC HV-2 Penumbra confirmed: 5 of 5 packaged path(s) resolved to the package, 0 elsewhere, 0 redirect(s) missing, " +
            "0 swap(s) not reported, 0 other redirect(s); parent -1, temporary collection effective, player's collection unchanged.", lines);
        Assert.Contains("RSC HV-2 visible: redrawn after 3 update(s), alpha 1.", lines);
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void WithoutTheStageTheSequenceIsHv1sExactly()
    {
        var plain = new HumanActorBaseline(scene, () => null, lines.Add);
        Assert.True(plain.Spawn());
        Run(plain, ToApply);
        Assert.Equal(HumanActorPhase.Visible, plain.Phase);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("penumbra", StringComparison.Ordinal) || call == "draw off 200");
        Assert.Single(scene.Calls, call => call == "draw on 200");
        Assert.True(plain.Remove());
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ThePackageIsThePlayersResolvedResourcesWithEveryFileTypeAsAnOrdinaryMod()
    {
        Applied();
        var paths = penumbra.ModPaths!;
        // Redirects to files beneath the mod folder, whatever their type: a skeleton and a bone deformer are ordinary files.
        Assert.Equal(FakeHumanActorPenumbra.File("Body", "top.mdl"), paths[FakeHumanActorPenumbra.TopModel]);
        Assert.Equal(FakeHumanActorPenumbra.File("Skeletons", "skl_c0101b0001.sklb"), paths[FakeHumanActorPenumbra.Skeleton]);
        Assert.Equal(FakeHumanActorPenumbra.File("Skeletons", "human.pbd"), paths[FakeHumanActorPenumbra.Deformer]);
        Assert.Equal(FakeHumanActorPenumbra.File("Skeletons", "human.pbd"), paths[FakeHumanActorPenumbra.DeformerSecond]);
        // A swap stays a swap; vanilla needs nothing; a file outside the mod folder is left out.
        Assert.Equal(FakeHumanActorPenumbra.SwapSource, paths[FakeHumanActorPenumbra.SwapTarget]);
        Assert.False(paths.ContainsKey(FakeHumanActorPenumbra.Vanilla));
        Assert.False(paths.ContainsKey(FakeHumanActorPenumbra.OutsideTarget));
        Assert.Equal(5, paths.Count);
        Assert.Equal(FakeHumanActorPenumbra.PlayerMeta, penumbra.ModMeta);
        Assert.Contains(lines, line => line.Contains("package 4 redirect(s), 1 swap(s), 1 vanilla, 1 outside the mod folder left out, manipulations yes",
            StringComparison.Ordinal));
    }

    [Fact]
    public void TwoSourcesForOneGamePathRefuseThePackage()
    {
        var (package, refusal) = HumanActorPenumbraPackage.Build(
        [
            new AppearanceResourceEntry(FakeHumanActorPenumbra.File("A", "top.mdl"), [FakeHumanActorPenumbra.TopModel]),
            new AppearanceResourceEntry(FakeHumanActorPenumbra.File("B", "top.mdl"), [FakeHumanActorPenumbra.TopModel.ToUpperInvariant()]),
        ], FakeHumanActorPenumbra.ModRoot, FakeHumanActorPenumbra.Contained);
        Assert.Null(package);
        Assert.Equal("one game path has two different sources", refusal);
    }

    [Fact]
    public void AParentFromTheCopyIsClearedSoThePlayersIdentityIsNeverAssigned()
    {
        // Penumbra's copy hook maps the actor to the player it was copied from.
        penumbra.Parents[200] = 0;
        Shown();
        Assert.Equal(-1, penumbra.ParentOf(200));
        Assert.Null(penumbra.AssignedTo(0));
        Assert.Contains(lines, line => line.Contains("parent 0, now -1", StringComparison.Ordinal));
    }

    [Fact]
    public void AParentThatCannotBeClearedStopsBeforeAnyCollection()
    {
        penumbra.Parents[200] = 0;
        penumbra.ParentWriteIgnored = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the actor's Penumbra parent (0) could not be cleared", baseline.Stop);
        Assert.Empty(penumbra.Created);
        Assert.False(scene.Exists(200));
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
    }

    [Fact]
    public void ARefusedAssignmentDeletesTheCollectionBeforeTheActor()
    {
        penumbra.IndividualFor.Add(200);
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the actor's identity already has an individual collection", baseline.Stop);
        Assert.Empty(penumbra.Created);

        var refused = new FakeHumanActorPenumbra(new FakeHumanActorScene()) { AssignAnswer = HumanAssignmentResult.Refused };
        AssertAssignmentFailureCleansUp(refused, "penumbra: the assignment was refused: the actor's identity already has a collection");
        var failed = new FakeHumanActorPenumbra(new FakeHumanActorScene()) { AssignAnswer = HumanAssignmentResult.Failed };
        AssertAssignmentFailureCleansUp(failed, "penumbra: the assignment failed");
    }

    private static void AssertAssignmentFailureCleansUp(FakeHumanActorPenumbra fake, string stop)
    {
        var log = new List<string>();
        var stage = Stage(fake, log.Add);
        var baseline = new HumanActorBaseline(fake.Scene, () => null, log.Add, stage, "RSC HV-2 ");
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal(stop, baseline.Stop);
        var calls = fake.Scene.Calls;
        Assert.True(IndexIn(calls, "penumbra unmod") < IndexIn(calls, "penumbra delete"));
        Assert.True(IndexIn(calls, "penumbra delete") < IndexIn(calls, "draw off 200"));
        Assert.True(IndexIn(calls, "draw off 200") < IndexIn(calls, "delete 200"));
        // The undraw here is the deletion's, never a rebuild.
        Assert.Single(calls, call => call == "draw off 200");
        Assert.Empty(fake.Live);
        Assert.DoesNotContain("alpha 200 1", calls);
        Assert.Equal(0, stage.Residue);
        // Never retried.
        Assert.False(baseline.Spawn());
    }

    [Fact]
    public void AnAssignmentThatReachesThePlayerIsUndoneAtOnce()
    {
        // The parent reads back -1 but Penumbra still groups the actor with the player.
        penumbra.AssignmentReachesPlayer = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the player's collection changed with the assignment", baseline.Stop);
        Assert.Empty(penumbra.Live);
        Assert.Null(penumbra.AssignedTo(0));
        Assert.Equal(FakeHumanActorPenumbra.PlayerCollection, penumbra.CollectionFor(0)!.Value.EffectiveCollection);
        Assert.Single(scene.Calls, call => call == "draw off 200");
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ARefusedModOrAMissingIdMakesNoAssignment()
    {
        penumbra.ModAnswer = false;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the temporary mod was refused", baseline.Stop);
        Assert.DoesNotContain("penumbra assign 200", scene.Calls);
        // The removal is still attempted, and finding nothing is a clean answer for a refused add.
        Assert.True(IndexOf("penumbra unmod") < IndexOf("penumbra delete"));
        Assert.True(IndexOf("penumbra delete") < IndexOf("delete 200"));
        Assert.Contains("RSC HV-2 Penumbra released: mod removed none present, collection deleted yes, actor off it yes, " +
            "player's collection unchanged yes.", lines);
        Assert.Empty(penumbra.Live);

        var noId = new FakeHumanActorPenumbra(new FakeHumanActorScene()) { CreateAnswers = false };
        var other = new HumanActorBaseline(noId.Scene, () => null, _ => { }, Stage(noId, _ => { }), "RSC HV-2 ");
        Assert.True(other.Spawn());
        Run(other, ToApply);
        Assert.Equal("penumbra: no temporary collection id came back", other.Stop);
        Assert.DoesNotContain(noId.Scene.Calls, call => call is "penumbra mod" or "penumbra delete" or "penumbra assign 200");
        Assert.False(noId.Scene.Exists(200));
    }

    [Fact]
    public void AModAddThatFailsAfterAddingIsStillRemoved()
    {
        // An ambiguous answer: Penumbra kept the mod but the call reported failure.
        penumbra.AddsModButReportsFailure = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the temporary mod was refused", baseline.Stop);
        Assert.DoesNotContain("penumbra assign 200", scene.Calls);
        Assert.True(IndexOf("penumbra mod") < IndexOf("penumbra unmod"));
        Assert.True(IndexOf("penumbra unmod") < IndexOf("penumbra delete"));
        Assert.Contains("RSC HV-2 Penumbra released: mod removed yes, collection deleted yes, actor off it yes, " +
            "player's collection unchanged yes.", lines);
        Assert.Empty(penumbra.Live);
        Assert.Equal(0, penumbra.ModsLeft);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AModRemovalThatIsNotConfirmedIsReported()
    {
        var baseline = Shown();
        penumbra.RemoveAnswer = HumanModRemoval.NotPresent;
        Assert.True(baseline.Remove());
        Assert.Equal("penumbra release: the temporary mod's removal was not confirmed", baseline.Stop);
        Assert.Contains(lines, line => line.StartsWith("RSC HV-2 Penumbra released: mod removed no, collection deleted yes", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AnUnreadableModFolderRefusesBeforeAnyCollection()
    {
        // The production check, on an absolute folder that cannot exist.
        penumbra.ModRootAnswer = Path.Combine(Path.GetTempPath(), "rsc-hv2-" + Guid.NewGuid().ToString("N"));
        var stage = new HumanActorPenumbraStage(penumbra, lines.Add, "RSC HV-2 ") { Contained = FakeHumanActorPenumbra.Contained };
        var baseline = new HumanActorBaseline(scene, () => null, lines.Add, stage, "RSC HV-2 ");
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the Penumbra mod folder could not be read", baseline.Stop);
        Assert.Empty(penumbra.Created);
        Assert.DoesNotContain("penumbra create", scene.Calls);
        Assert.False(scene.Exists(200));

        foreach (var folder in new string?[] { null, "", "mods", Path.Combine("relative", "mods") })
        {
            var fake = new FakeHumanActorPenumbra(new FakeHumanActorScene()) { ModRootAnswer = folder };
            var other = new HumanActorBaseline(fake.Scene, () => null, _ => { },
                new HumanActorPenumbraStage(fake, _ => { }, "RSC HV-2 ") { Contained = FakeHumanActorPenumbra.Contained }, "RSC HV-2 ");
            Assert.True(other.Spawn());
            Run(other, ToApply);
            Assert.Equal("penumbra: the Penumbra mod folder could not be read", other.Stop);
            Assert.Empty(fake.Created);
        }
    }

    [Fact]
    public void APackageWithNoRedirectRefusesBeforeAnyCollection()
    {
        penumbra.PlayerHasNoModFiles = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.Equal("penumbra: the package has no redirect to a file beneath the mod folder (1 left out), so nothing could be confirmed", baseline.Stop);
        Assert.Empty(penumbra.Created);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AMissingPackagedRedirectRefusesWithCounts()
    {
        // The rebuilt actor reports every packaged path except the skeleton.
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.Skeleton);
        var baseline = Applied();
        // Read again, still hidden, for the settle window after the new draw object appears; then refused.
        Run(baseline, ToShow + HumanActorBaseline.SettleLimit - 1);
        Assert.Equal(HumanActorPhase.WaitingForRebuild, baseline.Phase);
        Assert.Null(baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Run(baseline, 1);
        Assert.Equal("redraw: 1 of 4 packaged redirect(s) were not reported for the actor after the redraw (0 swap(s) not reported)", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));

        var all = new FakeHumanActorPenumbra(new FakeHumanActorScene());
        all.ActorOmits.UnionWith(new[]
        {
            FakeHumanActorPenumbra.TopModel, FakeHumanActorPenumbra.Skeleton, FakeHumanActorPenumbra.Deformer,
            FakeHumanActorPenumbra.DeformerSecond, FakeHumanActorPenumbra.SwapTarget,
        });
        var other = new HumanActorBaseline(all.Scene, () => null, _ => { }, Stage(all, _ => { }), "RSC HV-2 ");
        Assert.True(other.Spawn());
        Run(other, ToApply + ToShow + HumanActorBaseline.SettleLimit);
        Assert.Equal("redraw: 4 of 4 packaged redirect(s) were not reported for the actor after the redraw (1 swap(s) not reported)", other.Stop);
        Assert.DoesNotContain("alpha 200 1", all.Scene.Calls);
    }

    [Fact]
    public void RedirectsReportedLateWithinTheWindowAreConfirmed()
    {
        // The first reads after the new draw object appears lack the skeleton; a later one has it.
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.Skeleton);
        penumbra.ActorOmitsReads = 5;
        var baseline = Applied();
        Run(baseline, ToShow + 4);
        Assert.Equal(HumanActorPhase.WaitingForRebuild, baseline.Phase);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Run(baseline, 1);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Contains(lines, line => line.StartsWith("RSC HV-2 Penumbra confirmed: 5 of 5 packaged path(s)", StringComparison.Ordinal));
        Assert.Contains("RSC HV-2 visible: redrawn after 8 update(s), alpha 1.", lines);
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void AMissingSwapIsOnlyCounted()
    {
        penumbra.ActorOmits.Add(FakeHumanActorPenumbra.SwapTarget);
        Shown();
        Assert.Contains("RSC HV-2 Penumbra confirmed: 4 of 5 packaged path(s) resolved to the package, 0 elsewhere, 0 redirect(s) missing, " +
            "1 swap(s) not reported, 0 other redirect(s); parent -1, temporary collection effective, player's collection unchanged.", lines);
    }

    [Fact]
    public void ADrawObjectThatIsNeverReleasedStopsHiddenWithCleanup()
    {
        var sticky = new StickyModelBackend(scene);
        var baseline = Applied(sticky);
        sticky.Sticky = true;
        Run(baseline, HumanActorBaseline.RedrawLimit);
        Assert.Equal(HumanActorPhase.WaitingForRelease, baseline.Phase);
        Run(baseline, 1);
        Assert.Equal("redraw: a human draw object was still reported after the undraw; the rebuild cannot be proved", baseline.Stop);
        // Never drawn again, never shown; the collection goes before the actor.
        Assert.Single(scene.Calls, call => call == "draw on 200");
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.True(IndexOf("penumbra delete") < IndexOf("delete 200"));
        Assert.DoesNotContain(lines, line => line.Contains("Penumbra confirmed", StringComparison.Ordinal));
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AnActorThatIsNotReadyAgainStopsHidden()
    {
        var baseline = Applied();
        scene.ReadyAfter = int.MaxValue;
        Run(baseline, HumanActorBaseline.RedrawLimit + 1);
        Assert.Equal($"redraw: not ready to draw again within {HumanActorBaseline.RedrawLimit} updates of the undraw", baseline.Stop);
        Assert.Single(scene.Calls, call => call == "draw on 200");
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ARebuildThatNeverComesStopsAndCleansUpWithTheActorHidden()
    {
        var baseline = Applied();
        scene.DrawAppears = false;
        Run(baseline, 2);
        Assert.Equal(HumanActorPhase.WaitingForRebuild, baseline.Phase);
        Run(baseline, HumanActorBaseline.RedrawLimit + 1);
        Assert.Equal($"redraw: no new human draw object within {HumanActorBaseline.RedrawLimit} updates of the draw", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.DoesNotContain(lines, line => line.Contains("Penumbra confirmed", StringComparison.Ordinal));
        Assert.True(IndexOf("penumbra delete") < IndexOf("delete 200"));
        Assert.Empty(penumbra.Live);
    }

    [Fact]
    public void ARebuildThatResolvesElsewhereIsNeverShown()
    {
        penumbra.ActorResolvesElsewhere = FakeHumanActorPenumbra.Skeleton;
        var baseline = Applied();
        Run(baseline, ToShow);
        Assert.Equal("redraw: 1 packaged game path(s) resolved elsewhere after the redraw", baseline.Stop);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData(1, HumanActorPhase.WaitingForRelease)]
    [InlineData(2, HumanActorPhase.WaitingForRebuild)]
    public void AnActorLostDuringTheRedrawIsNeverTouchedButItsCollectionIsDeleted(int updates, HumanActorPhase phase)
    {
        var baseline = Applied();
        Run(baseline, updates);
        Assert.Equal(phase, baseline.Phase);
        scene.Replace(200);
        var from = scene.Calls.Count;
        baseline.OnFrameworkUpdate();
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(new[] { "penumbra unmod", "penumbra delete" }, scene.Calls.Skip(from).ToArray());
        Assert.Empty(penumbra.Live);
        Assert.Equal("Someone Else", scene.At(200).Name);
    }

    [Fact]
    public void AnActorLostDuringTheAssignmentIsNeverUndrawn()
    {
        // The slot changes hands while Penumbra is being called: the undraw is never sent to the new object.
        penumbra.ReplaceActorOnAssign = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToApply);
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("draw off 200", scene.Calls);
        Assert.True(IndexOf("penumbra assign 200") < IndexOf("penumbra unmod"));
        Assert.True(IndexOf("penumbra unmod") < IndexOf("penumbra delete"));
        Assert.Empty(penumbra.Live);
        Assert.Contains(lines, line => line.Contains("actor off it not read", StringComparison.Ordinal));
        Assert.Equal("Someone Else", scene.At(200).Name);
    }

    [Fact]
    public void RemovalRemovesTheModAndCollectionBeforeTheActorIsUndrawnAndDeleted()
    {
        var baseline = Shown();
        var hideFrom = scene.Calls.Count;
        Assert.True(baseline.Remove());
        var calls = scene.Calls.Skip(hideFrom).ToList();
        Assert.Equal(new[] { "alpha 200 0", "penumbra unmod", "penumbra delete", "draw off 200", "delete 200" }, calls);
        Assert.Empty(penumbra.Live);
        Assert.Null(penumbra.AssignedTo(200));
        Assert.Contains("RSC HV-2 Penumbra released: mod removed yes, collection deleted yes, actor off it yes, player's collection unchanged yes.", lines);
        Assert.Contains("RSC HV-2 removed: deleted, slot empty yes.", lines);
        Assert.Null(baseline.Stop);
        Assert.Equal("RSC HV-2 status: None, stop none, residue 0, temporary collection none, collection residue 0.", baseline.Status());
    }

    [Fact]
    public void UnloadReleasesInTheSameOrder()
    {
        var baseline = Shown();
        var from = scene.Calls.Count;
        baseline.Unload();
        Assert.Equal(new[] { "alpha 200 0", "penumbra unmod", "penumbra delete", "draw off 200", "delete 200" }, scene.Calls.Skip(from).ToList());
        Assert.Empty(penumbra.Live);
        Assert.Contains("RSC HV-2 unload: deleted, slot empty yes, residue 0.", lines);
    }

    [Fact]
    public void ALostActorIsNeverTouchedButItsCollectionIsDeleted()
    {
        var baseline = Shown();
        scene.Replace(200);
        var from = scene.Calls.Count;
        baseline.OnFrameworkUpdate();
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(new[] { "penumbra unmod", "penumbra delete" }, scene.Calls.Skip(from).ToList());
        Assert.Empty(penumbra.Live);
        Assert.Contains(lines, line => line.Contains("actor off it not read", StringComparison.Ordinal));
        Assert.Equal("Someone Else", scene.At(200).Name);
        baseline.Unload();
        Assert.Equal(from + 2, scene.Calls.Count);
    }

    [Fact]
    public void ADeletionThatIsNotConfirmedIsResidueAndStillDeletesTheActor()
    {
        var baseline = Shown();
        penumbra.DeleteAnswer = false;
        Assert.True(baseline.Remove());
        Assert.False(scene.Exists(200));
        Assert.Equal("penumbra release: the temporary collection's deletion was not confirmed", baseline.Stop);
        Assert.EndsWith("collection residue 1.", baseline.Status(), StringComparison.Ordinal);
        Assert.False(baseline.Spawn());
    }

    [Fact]
    public void AnUnavailablePenumbraRefusesTheSpawnWithNothingCreated()
    {
        penumbra.IsAvailable = false;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.Empty(scene.Calls);
        Assert.Null(baseline.Stop);
        Assert.Contains("RSC HV-2 refused: Penumbra is unavailable or incompatible.", lines);
    }

    [Fact]
    public void ThePlayerIsOnlyReadAndThePortHasNoForcedOrCollectionWideWrite()
    {
        var baseline = Shown();
        Assert.True(baseline.Remove());
        Assert.DoesNotContain(scene.Calls, call => call.EndsWith(" 0", StringComparison.Ordinal) && call.StartsWith("penumbra", StringComparison.Ordinal));
        Assert.Equal(FakeHumanActorPenumbra.PlayerCollection, penumbra.CollectionFor(0)!.Value.EffectiveCollection);
        var members = typeof(IHumanActorPenumbra).GetMembers().Select(member => member.Name).ToArray();
        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, name => name.Contains("Force", StringComparison.Ordinal) || name.Contains("SetCollection", StringComparison.Ordinal) ||
            name.Contains("Redraw", StringComparison.Ordinal) ||
            name.Contains("All", StringComparison.Ordinal) || name.Contains("Glamourer", StringComparison.Ordinal) ||
            name.Contains("Customize", StringComparison.Ordinal));
    }
}

/// <summary>
/// A synthetic Penumbra for HV-2: cutscene parents, collection readings that
/// follow the parent the way Penumbra's identification does, and temporary
/// collections and mods. It has no redraw: the actor is undrawn and drawn
/// again through the scene. Calls are logged into the scene's list, in order.
/// </summary>
internal sealed class FakeHumanActorPenumbra(FakeHumanActorScene scene) : IHumanActorPenumbra
{
    public static readonly Guid PlayerCollection = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DefaultCollection = new("22222222-2222-2222-2222-222222222222");
    public const string PlayerMeta = "TUVUQQ==";

    public static readonly string ModRoot = OperatingSystem.IsWindows() ? @"X:\rsc-hv2\mods" : "/rsc-hv2/mods";
    private static readonly string OutsideRoot = OperatingSystem.IsWindows() ? @"X:\rsc-hv2\elsewhere" : "/rsc-hv2/elsewhere";

    public const string TopModel = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    public const string Skeleton = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    public const string Deformer = "chara/xls/boneDeformer/human.pbd";
    public const string DeformerSecond = "chara/xls/bonedeformer/human_alt.pbd";
    public const string SwapTarget = "chara/equipment/e0002/texture/v01_c0101e0002_top_n.tex";
    public const string SwapSource = "chara/equipment/e0003/texture/v01_c0101e0003_top_n.tex";
    public const string Vanilla = "chara/common/texture/skin_m.tex";
    public const string OutsideTarget = "chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_fac_a.mtrl";

    public static string File(string folder, string name) => Path.Combine(ModRoot, folder, name);

    public static string? Contained(string? root, string actual) =>
        root is not null && actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? actual : null;

    private readonly Dictionary<Guid, Dictionary<string, string>?> collections = [];
    private readonly Dictionary<int, Guid> assigned = [];

    public FakeHumanActorScene Scene { get; } = scene;
    public bool IsAvailable { get; set; } = true;
    public Dictionary<int, int> Parents { get; } = [];
    public bool ParentWriteIgnored { get; set; }
    public HashSet<int> IndividualFor { get; } = [];
    public bool CreateAnswers { get; set; } = true;
    public bool ModAnswer { get; set; } = true;
    public HumanAssignmentResult? AssignAnswer { get; set; }
    public bool AssignmentReachesPlayer { get; set; }
    public bool AddsModButReportsFailure { get; set; }
    public HumanModRemoval? RemoveAnswer { get; set; }
    public string? ModRootAnswer { get; set; } = ModRoot;
    public bool PlayerHasNoModFiles { get; set; }
    public string? ActorResolvesElsewhere { get; set; }
    public HashSet<string> ActorOmits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads of the actor's resources that still omit <see cref="ActorOmits"/>.</summary>
    public int ActorOmitsReads { get; set; } = int.MaxValue;
    public bool ReplaceActorOnAssign { get; set; }
    public bool DeleteAnswer { get; set; } = true;
    public List<Guid> Created { get; } = [];
    public IReadOnlyCollection<Guid> Live => collections.Keys;
    public int ModsLeft => collections.Values.Count(mod => mod is not null);
    public IReadOnlyDictionary<string, string>? ModPaths { get; private set; }
    public string? ModMeta { get; private set; }

    public int ParentOf(int index) => Parents.TryGetValue(index, out var parent) ? parent : -1;

    public Guid? AssignedTo(int index) => assigned.TryGetValue(index, out var id) ? id : null;

    public bool Available => IsAvailable;

    public string? ModDirectory() => ModRootAnswer;

    public IReadOnlyList<AppearanceResourceEntry>? ResourcePaths(int globalIndex)
    {
        IReadOnlyList<AppearanceResourceEntry> player =
        [
            new(File("Body", "top.mdl"), [TopModel]),
            new(File("Skeletons", "skl_c0101b0001.sklb"), [Skeleton]),
            new(File("Skeletons", "human.pbd"), [Deformer, DeformerSecond]),
            new(SwapSource, [SwapTarget]),
            new(Vanilla, [Vanilla]),
            new(Path.Combine(OutsideRoot, "face.mtrl"), [OutsideTarget]),
        ];
        if (PlayerHasNoModFiles)
            player = player.Where(entry => !entry.ActualPath.StartsWith(ModRoot, StringComparison.Ordinal)).ToList();
        if (globalIndex == 0)
            return player;
        if (!Scene.Exists(globalIndex))
            return null;
        // Every game path the player's tree has, resolved through the actor's own collection.
        IReadOnlySet<string> omits = ActorOmitsReads > 0 ? ActorOmits : new HashSet<string>();
        if (ActorOmitsReads is > 0 and < int.MaxValue)
            ActorOmitsReads--;
        var mod = Effective(globalIndex) is var id && collections.TryGetValue(id, out var paths) ? paths : null;
        return player.SelectMany(entry => entry.GamePaths)
            .Where(gamePath => !omits.Contains(gamePath))
            .Select(gamePath => (GamePath: gamePath, Actual: gamePath == ActorResolvesElsewhere ? File("Other", "other.file")
                : mod is not null && mod.TryGetValue(gamePath, out var target) ? target : gamePath))
            .GroupBy(pair => pair.Actual)
            .Select(group => new AppearanceResourceEntry(group.Key, group.Select(pair => pair.GamePath).ToArray()))
            .ToList();
    }

    public string? MetaManipulations(int globalIndex) => globalIndex == 0 ? PlayerMeta : null;

    public int? GetParent(int globalIndex) => ParentOf(globalIndex);

    public bool SetParent(int globalIndex, int parent)
    {
        Scene.Calls.Add($"penumbra parent {globalIndex} {parent}");
        if (!ParentWriteIgnored)
            Parents[globalIndex] = parent;
        return true;
    }

    public HumanCollectionReading? CollectionFor(int globalIndex) =>
        Scene.Exists(globalIndex)
            ? new HumanCollectionReading(true, IndividualFor.Contains(Identity(globalIndex)), Effective(globalIndex))
            : new HumanCollectionReading(false, false, DefaultCollection);

    public Guid? CreateTemporaryCollection(string name)
    {
        Scene.Calls.Add("penumbra create");
        if (!CreateAnswers)
            return null;
        var id = Guid.NewGuid();
        collections[id] = null;
        Created.Add(id);
        return id;
    }

    public bool AddTemporaryMod(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, string meta, int priority)
    {
        Scene.Calls.Add("penumbra mod");
        if (!ModAnswer || !collections.ContainsKey(collection))
            return false;
        collections[collection] = new Dictionary<string, string>(paths, StringComparer.OrdinalIgnoreCase);
        ModPaths = paths;
        ModMeta = meta;
        return !AddsModButReportsFailure;
    }

    public HumanAssignmentResult Assign(Guid collection, int globalIndex)
    {
        Scene.Calls.Add($"penumbra assign {globalIndex}");
        if (ReplaceActorOnAssign)
            Scene.Replace(globalIndex);
        if (AssignAnswer is { } answer)
            return answer;
        var identity = AssignmentReachesPlayer ? 0 : Identity(globalIndex);
        if (assigned.ContainsKey(identity) || IndividualFor.Contains(identity))
            return HumanAssignmentResult.Refused;
        assigned[identity] = collection;
        return HumanAssignmentResult.Success;
    }

    public HumanModRemoval RemoveTemporaryMod(string tag, Guid collection, int priority)
    {
        Scene.Calls.Add("penumbra unmod");
        if (!collections.TryGetValue(collection, out var mod))
            return HumanModRemoval.Failed;
        if (RemoveAnswer is { } answer)
            return answer;
        if (mod is null)
            return HumanModRemoval.NotPresent;
        collections[collection] = null;
        return HumanModRemoval.Removed;
    }

    public bool DeleteTemporaryCollection(Guid collection)
    {
        Scene.Calls.Add("penumbra delete");
        if (!DeleteAnswer)
            return false;
        if (!collections.Remove(collection))
            return false;
        // Deleting a temporary collection removes its assignments, as Penumbra's does.
        foreach (var identity in assigned.Where(pair => pair.Value == collection).Select(pair => pair.Key).ToList())
            assigned.Remove(identity);
        return true;
    }

    /// <summary>Penumbra identifies an actor in the cutscene range by its parent while one is set.</summary>
    private int Identity(int globalIndex) => ParentOf(globalIndex) is >= 0 and var parent && globalIndex >= 200 ? parent : globalIndex;

    private Guid Effective(int globalIndex)
    {
        var identity = Identity(globalIndex);
        if (assigned.TryGetValue(identity, out var id))
            return id;
        return identity == 0 ? PlayerCollection : DefaultCollection;
    }
}

/// <summary>The scene, except that once <see cref="Sticky"/> is set the actors' draw object is reported whether drawn or not.</summary>
internal sealed class StickyModelBackend(FakeHumanActorScene scene) : IHumanActorBackend
{
    public bool Sticky { get; set; }

    public bool IsAvailable => scene.IsAvailable;
    public nint LocalPlayer => scene.LocalPlayer;
    public int ObjectTableLength => scene.ObjectTableLength;

    public HumanPuppetFacts ReadFacts(nint address)
    {
        var facts = scene.ReadFacts(address);
        return Sticky && facts.GlobalIndex >= 200 ? facts with { HasHumanModel = true } : facts;
    }

    public nint[] ComEntriesSnapshot() => scene.ComEntriesSnapshot();
    public int CreateCharacter() => scene.CreateCharacter();
    public nint ComEntry(int comIndex) => scene.ComEntry(comIndex);
    public nint ComObjectByIndex(int comIndex) => scene.ComObjectByIndex(comIndex);
    public int ComIndexOf(nint address) => scene.ComIndexOf(address);
    public nint ObjectTableEntry(int globalIndex) => scene.ObjectTableEntry(globalIndex);
    public HumanPuppetScanEntry? ScanEntry(int globalIndex) => scene.ScanEntry(globalIndex);
    public void WriteName(nint actor, string name) => scene.WriteName(actor, name);
    public void CopyCharacter(nint actor, nint source) => scene.CopyCharacter(actor, source);
    public HumanActorPlacement? ReadPlacement(nint address) => scene.ReadPlacement(address);
    public void Place(nint actor, HumanActorPlacement placement) => scene.Place(actor, placement);
    public void MakeUntargetable(nint actor) => scene.MakeUntargetable(actor);
    public void SetAlpha(nint actor, float alpha) => scene.SetAlpha(actor, alpha);
    public bool IsReadyToDraw(nint actor) => scene.IsReadyToDraw(actor);
    public void EnableDraw(nint actor) => scene.EnableDraw(actor);
    public void DisableDraw(nint actor) => scene.DisableDraw(actor);
    public void DeleteComObject(int comIndex) => scene.DeleteComObject(comIndex);
}
#endif
