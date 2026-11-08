#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// T3-1's candidate against the synthetic scene: the W-2 row-1 checks, the
/// host amendments H1-a to H1-e (hiding, provider generation, live exits,
/// port faults, the targetable read-back) and the fixed step sequence.
/// Synthetic identities and fakes only: no game, no IPC.
/// </summary>
public sealed class HumanPuppetT3Tests
{
    private static readonly HumanPuppetWorldRow RowOne = new(1, "reserved1", "Dev", 0, IsPublic: false);
    private static readonly HumanPuppetWorld W2 = HumanPuppetW2.World(RowOne);

    private readonly FakeHumanScene scene = new();
    private long generation;
    private string? exit;

    private HumanPuppetHost Host(HumanPuppetWorld? world = null) =>
        new(scene, scene, scene, scene, world ?? W2, HumanPuppetKind.BattleNpc, () => 0, () => generation, () => exit);

    private HumanPuppetHost Ready()
    {
        var host = Host();
        Assert.True(host.CheckCustomizePlus().Done);
        host.OnFrameworkUpdate(1);
        return host;
    }

    private (HumanPuppetHost Host, HumanPuppetHandle Handle) Spawned()
    {
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        return (host, result.Handle!.Value);
    }

    private static int IndexOfCall(List<string> calls, string call)
    {
        var index = calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    // ---- W-2: World row 1, checked at start-up; no fallback ----

    [Fact]
    public void RowOneWithTheSelectedIdentityIsTheVerifiedWorld()
    {
        Assert.Equal((ushort)1, HumanPuppetW2.WorldId);
        Assert.Null(HumanPuppetW2.RowMismatch(RowOne));
        Assert.True(W2.IsUsable);
        Assert.Equal((ushort)1, W2.WorldId);
        var (host, _) = Spawned();
        Assert.Equal((ushort)1, scene.At(host.Snapshot()[0].GlobalIndex).World);
    }

    public static TheoryData<HumanPuppetWorldRow?> MismatchedRows() => new()
    {
        null,
        RowOne with { RowId = 2 },
        RowOne with { InternalName = "reserved2" },
        RowOne with { InternalName = "Reserved1" },
        RowOne with { Name = "dev" },
        RowOne with { Name = "" },
        RowOne with { DataCenter = 3 },
        RowOne with { IsPublic = true },
    };

    [Theory]
    [MemberData(nameof(MismatchedRows))]
    public void AnyDifferenceInTheSelectedRowRefusesWithoutFallback(HumanPuppetWorldRow? row)
    {
        Assert.NotNull(HumanPuppetW2.RowMismatch(row));
        var world = HumanPuppetW2.World(row);
        Assert.False(world.IsUsable);
        Assert.Equal(HumanPuppetW2.WorldId, world.WorldId);
        var host = Host(world);
        Assert.True(host.CheckCustomizePlus().Done);
        Assert.Equal(HumanPuppetRefusal.WorldUnverified, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Fact]
    public void ThePlayersOwnWorldMustDifferFromRowOne()
    {
        Assert.Null(HumanPuppetW2.PlayerWorldMismatch(FakeHumanScene.PlayerWorld));
        Assert.NotNull(HumanPuppetW2.PlayerWorldMismatch(1));
        Assert.NotNull(HumanPuppetW2.PlayerWorldMismatch(null));
        scene.Player.World = 1;
        var host = Host();
        Assert.True(host.CheckCustomizePlus().Done);
        Assert.Equal(HumanPuppetRefusal.WorldUnverified, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    // ---- The construction sequence (M-1 with H1-a) ----

    [Fact]
    public void SpawnHidesAfterTheLastCopyAndBeforeTheDetach()
    {
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned);
        var calls = scene.Calls;
        var order = new[]
        {
            IndexOfCall(calls, "snapshot"),
            IndexOfCall(calls, "create"),
            IndexOfCall(calls, "alpha 200 0"),
            IndexOfCall(calls, "copy 0->200"),
            IndexOfCall(calls, "copy 200->200"),
            IndexOfCall(calls, "hide prepare 200"),
            IndexOfCall(calls, "penumbra set 200 -1"),
            IndexOfCall(calls, "customize set 200 -1"),
            IndexOfCall(calls, "write identity kind=False clear=False"),
        };
        Assert.Equal(order.Order(), order);
        // By-index IPC inside M-1: two sets, and gets only in the read-backs; no draw.
        Assert.Equal(2, calls.Count(call => call.StartsWith("penumbra set", StringComparison.Ordinal) || call.StartsWith("customize set", StringComparison.Ordinal)));
        Assert.DoesNotContain(calls, call => call.StartsWith("draw on", StringComparison.Ordinal));
        Assert.Single(calls, call => call.StartsWith("hide prepare", StringComparison.Ordinal));
        var puppet = scene.At(200);
        Assert.False(puppet.Targetable);
        Assert.True(puppet.NameplateHidden);
        Assert.Equal(0f, puppet.Alpha);
        Assert.Equal(2, puppet.Kind);
        // F0's record carries the targetable bit after each copy.
        Assert.All(result.Copies, copy => Assert.True(copy.Targetable));
        // The player is only read: it is neither hidden nor made untargetable.
        Assert.True(scene.Player.Targetable);
        Assert.False(scene.Player.NameplateHidden);
    }

    [Fact]
    public void AWrongKindAfterTheCopiesStopsWithNoKindWrite()
    {
        // R-8 is not approved: the copies leaving the player kind is a stop.
        scene.CopyCopiesKind = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.S5Identity, result.Stop!.Rule);
        Assert.Equal("copy", result.Stop.Step);
        // Stopped straight after the copies: no kind, hiding, map or identity write.
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal) ||
            call.StartsWith("hide prepare", StringComparison.Ordinal) || call.StartsWith("penumbra set", StringComparison.Ordinal) ||
            call.StartsWith("customize set", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    // ---- Hiding (H1-a, H1-e) ----

    [Fact]
    public void APuppetThatStaysTargetableAfterHidingIsStoppedAndDeleted()
    {
        scene.HidingLeavesTargetable = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.Hiding, result.Stop!.Rule);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("penumbra set", StringComparison.Ordinal));
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("write identity", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ATargetableBitSetLaterStopsOnTheNextUpdate()
    {
        var (host, _) = Spawned();
        scene.At(200).Targetable = true;
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Hiding, host.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ATargetableBitBeforeTheDrawStopsWithoutDrawing()
    {
        var (host, handle) = Spawned();
        scene.At(200).Targetable = true;
        var outcome = host.RequestDraw(handle);
        Assert.Equal(HumanPuppetStopRule.Hiding, outcome.Stop!.Rule);
        Assert.DoesNotContain("draw on 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    // ---- Fail-closed providers (H1-b, CP-3, P16) ----

    [Fact]
    public void ACustomizePlusThatIsNotValidFailsCp3()
    {
        scene.CustomizePlusValid = false;
        var host = Host();
        var outcome = host.CheckCustomizePlus();
        Assert.Equal(HumanPuppetStopRule.CustomizePlus, outcome.Stop!.Rule);
        Assert.Equal(HumanPuppetRefusal.Stopped, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Fact]
    public void ACustomizePlusThatStopsBeingValidStopsTheLivePuppet()
    {
        var (host, _) = Spawned();
        scene.CustomizePlusValid = false;
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Provider, host.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AProviderGenerationChangeAfterCp3RefusesTheSpawnUntilCp3IsRunAgain()
    {
        var host = Ready();
        generation++;
        Assert.Equal(HumanPuppetRefusal.CustomizePlusNotChecked, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("ipc")]
    public void ACustomizePlusThatChangesAfterCp3RefusesTheSpawn(string change)
    {
        var host = Ready();
        if (change == "version")
            scene.CustomizePlusApiVersion = (6, 5);
        else
            scene.CustomizePlusParentIpcPresent = false;
        Assert.Equal(HumanPuppetRefusal.Unavailable, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Fact]
    public void ACustomizePlusVersionChangeWhileLiveIsAProviderStop()
    {
        var (host, _) = Spawned();
        scene.CustomizePlusApiVersion = (6, 5);
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Provider, host.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AGlamourerThatDoesNotAnswerItsVersionRefusesTheSpawn()
    {
        var host = Ready();
        scene.ApiVersion = null;
        Assert.Equal(HumanPuppetRefusal.Unavailable, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
    }

    [Fact]
    public void AThrowBeforeTheCreationIsProvedLeavesResidueNeverAnUntrackedObject()
    {
        var host = Ready();
        scene.ThrowWhen = call => call == "com index" && scene.Calls.Contains("create");
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.Provider, result.Stop!.Rule);
        Assert.Contains(host.Residue, item => item.Reason.Contains("before its creation was proved", StringComparison.Ordinal));
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public void AProviderGenerationChangeWhileLiveIsAProviderStop()
    {
        var (host, _) = Spawned();
        host.OnFrameworkUpdate(2);
        Assert.Null(host.Stop);
        generation++;
        host.OnFrameworkUpdate(3);
        Assert.Equal(HumanPuppetStopRule.Provider, host.Stop!.Rule);
        Assert.Equal(3, host.Stop.Update);
        // Clean-up still ran: hidden, maps read and the object deleted natively.
        Assert.False(scene.Exists(200));
        Assert.Contains(host.Readings, reading => reading.Phase == "P13 step 3");
    }

    [Fact]
    public void AProviderGenerationChangeWithNoPuppetIsNotAStop()
    {
        var host = Ready();
        generation++;
        host.OnFrameworkUpdate(2);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void AnUnreadableCollectionTypeIsAProviderStopNeverNotSet()
    {
        var (host, handle) = Spawned();
        scene.UnreadableRoles.Add(HumanCollectionRoleKind.Gender);
        var outcome = host.VerifyIsolation(handle);
        Assert.Equal(HumanPuppetStopRule.Provider, outcome.Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ADeletedSlotIsNotMeasuredWhenAProviderDoesNotAnswer()
    {
        var (host, handle) = Spawned();
        Assert.True(host.Remove(handle).Done);
        scene.PenumbraAvailable = false;
        host.OnFrameworkUpdate(2);
        Assert.Contains(host.Readings, reading => reading.Phase == HumanPuppetHost.NotMeasuredPhase);
        Assert.DoesNotContain(host.Readings, reading => reading.Phase == "F2-R item 1");
        Assert.Null(host.Stop);
    }

    // ---- Live exits (H1-c) ----

    [Fact]
    public void ASceneThatIsNotQuietRefusesASpawnWithoutASideEffect()
    {
        var host = Ready();
        exit = "GPose is active";
        Assert.Equal(HumanPuppetRefusal.SceneNotQuiet, host.Spawn().Refusal);
        Assert.DoesNotContain("create", scene.Calls);
        Assert.Null(host.Stop);
    }

    [Fact]
    public void ALiveExitStopsAndCleansUpInTheSameUpdate()
    {
        var (host, _) = Spawned();
        exit = "a loading screen is active";
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.LiveExit, host.Stop!.Rule);
        Assert.Equal("a loading screen is active", host.Stop.Detail);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void ALiveExitBeforeTheDrawStopsWithoutDrawing()
    {
        var (host, handle) = Spawned();
        exit = "a cutscene is active";
        Assert.Equal(HumanPuppetStopRule.LiveExit, host.RequestDraw(handle).Stop!.Rule);
        Assert.DoesNotContain("draw on 200", scene.Calls);
    }

    // ---- Port faults (H1-d) ----

    [Fact]
    public void AFirstReadThatThrowsIsAProviderStopAndTheObjectIsDeletedWithoutAWrite()
    {
        var host = Ready();
        scene.ThrowWhen = call => call == "read" && scene.Calls.Contains("create");
        var result = host.Spawn();
        Assert.False(result.Spawned);
        Assert.Equal(HumanPuppetStopRule.Provider, result.Stop!.Rule);
        Assert.Contains("threw", result.Stop.Detail, StringComparison.Ordinal);
        // Tracked before its first read: proved by its creation slot and deleted, with no write.
        Assert.Contains("delete 200", scene.Calls);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("alpha 200", StringComparison.Ordinal) || call == "draw off 200");
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanPuppetPhase.Deleted, Assert.Single(host.Snapshot()).Phase);
    }

    [Fact]
    public void ACopyThatThrowsIsAProviderStopWithCleanUp()
    {
        var host = Ready();
        scene.ThrowWhen = call => call == "copy";
        var result = host.Spawn();
        Assert.Equal(HumanPuppetStopRule.Provider, result.Stop!.Rule);
        Assert.Contains("draw off 200", scene.Calls);
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanPuppetRefusal.Stopped, host.Spawn().Refusal);
    }

    [Fact]
    public void AMapReadThatThrowsOnAnUpdateIsAStopAndTheObjectIsStillAccountedFor()
    {
        var (host, _) = Spawned();
        scene.ThrowWhen = call => call == "penumbra get";
        host.OnFrameworkUpdate(2);
        Assert.Equal(HumanPuppetStopRule.Provider, host.Stop!.Rule);
        // Clean-up could not read the map either: the hidden puppet is recorded as residue, never silently dropped.
        Assert.False(scene.At(200).DrawEnabled);
        Assert.Contains(host.Residue, item => item.What.Contains("clean-up did not finish", StringComparison.Ordinal));
    }

    [Fact]
    public void ACollectionReadThatThrowsIsAProviderStop()
    {
        var (host, handle) = Spawned();
        scene.ThrowWhen = call => call == "collection";
        Assert.Equal(HumanPuppetStopRule.Provider, host.VerifyIsolation(handle).Stop!.Rule);
        Assert.False(scene.Exists(200));
    }

    // ---- The exact clean-up path ----

    [Fact]
    public void RemovalHidesChecksBothMapsAndDeletesWithoutD2ThenReadsTheSlot()
    {
        var (host, handle) = Spawned();
        Assert.True(host.RequestDraw(handle).Done);
        host.OnFrameworkUpdate(2);
        var before = scene.Calls.Count;
        Assert.True(host.Remove(handle).Done);
        var removal = scene.Calls.Skip(before).ToList();
        Assert.True(IndexOfCall(removal, "draw off 200") < IndexOfCall(removal, "delete 200"));
        Assert.DoesNotContain(removal, call => call.Contains("collection", StringComparison.Ordinal) || call.Contains("assign", StringComparison.Ordinal));
        Assert.Equal(HumanPuppetPhase.Deleted, Assert.Single(host.Snapshot()).Phase);
        host.OnFrameworkUpdate(3);
        Assert.Contains(host.Readings, reading => reading.Phase == "F2-R item 1" && reading.Penumbra == -1 && reading.CustomizePlus == -1);
        Assert.Null(host.Stop);
    }

    // ---- The step sequence ----

    private sealed class FakeProbe(FakeHumanScene scene) : IHumanT3Probe
    {
        public Dictionary<string, string> Facts { get; } = new() { ["customize+ active profile on 0"] = "code 0, profile none" };

        public IReadOnlyList<KeyValuePair<string, string>> ReadRunFacts() => Facts.ToArray();

        public bool HomeWorldThrows { get; set; }

        public ushort? PlayerHomeWorld() => HomeWorldThrows ? throw new InvalidOperationException("synthetic probe fault") : scene.Player.World;
    }

    private (HumanPuppetT3Sequence Sequence, List<string> Log, FakeProbe Probe) Sequence(HumanPuppetWorldRow? row = null, SceneLocation? place = null)
    {
        var log = new List<string>();
        var probe = new FakeProbe(scene);
        var current = place;
        var sequence = new HumanPuppetT3Sequence(scene, scene, scene, scene, probe, row ?? RowOne, () => 0, () => generation, () => exit,
            () => current, log.Add);
        return (sequence, log, probe);
    }

    private long tick;

    private void Run(HumanPuppetT3Sequence sequence, string step)
    {
        Assert.True(sequence.Submit(step));
        sequence.OnFrameworkUpdate(++tick, 16);
    }

    [Fact]
    public void TheStepsRunInOrderAndReportBothF2RItems()
    {
        var (sequence, log, _) = Sequence();
        long drawnOn = 0;
        foreach (var step in HumanPuppetT3Sequence.Steps.Take(6))
        {
            Run(sequence, step);
            if (step == "draw")
                drawnOn = tick;
        }
        var removedOn = tick;
        Assert.Equal(6, sequence.NextStep);
        // The host ticks before the step: the draw and the deletion belong to
        // the update they ran in, and the slot is read on a later update.
        Assert.Contains(log, line => line.Contains($"draw requested on update {drawnOn},", StringComparison.Ordinal));
        Assert.Equal(removedOn, sequence.Host.Snapshot()[0].DeletedUpdate);
        Assert.DoesNotContain(sequence.Host.Readings, reading => reading.Phase == "F2-R item 1");
        // The slot is read on the update after the deletion; then the report.
        Run(sequence, "report");
        Assert.Equal(7, sequence.NextStep);
        Assert.Null(sequence.Host.Stop);
        Assert.True(Assert.Single(sequence.Host.Readings, reading => reading.Phase == "F2-R item 1").Update > removedOn);
        Assert.Contains(log, line => line.Contains("F2-R item 1: Penumbra -1, Customize+ -1", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("F2-R item 4: no other slot changed", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("isolation held", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("draw observed", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
        // The log holds no puppet name and no address.
        var name = sequence.Host.NameOf(sequence.Host.Snapshot()[0].Handle)!;
        Assert.DoesNotContain(log, line => line.Contains(name, StringComparison.OrdinalIgnoreCase) || line.Contains("0x", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepOutOfOrderIsRefusedWithoutASideEffect()
    {
        var (sequence, log, _) = Sequence();
        Run(sequence, "spawn");
        Assert.Equal(0, sequence.NextStep);
        Assert.DoesNotContain("create", scene.Calls);
        Assert.Contains(log, line => line.Contains("spawn refused: the next step is cp3", StringComparison.Ordinal));
        Assert.False(sequence.Submit("anything else"));
    }

    [Fact]
    public void ARefusedW2RowRefusesEveryStep()
    {
        var (sequence, log, _) = Sequence(RowOne with { IsPublic = true });
        Assert.NotNull(sequence.WorldRefusal);
        Assert.Contains(log, line => line.Contains("W-2 refused", StringComparison.Ordinal) && line.Contains("no fallback", StringComparison.Ordinal));
        Run(sequence, "cp3");
        Assert.Equal(0, sequence.NextStep);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("customize", StringComparison.Ordinal));
    }

    [Fact]
    public void APlayerOnRowOneStopsTheRunAtStartUpWithoutFallback()
    {
        scene.Player.World = 1;
        var (sequence, log, _) = Sequence();
        Run(sequence, "cp3");
        Assert.Equal(0, sequence.NextStep);
        Assert.NotNull(sequence.WorldRefusal);
        Run(sequence, "cp3");
        Assert.Equal(0, sequence.NextStep);
        Assert.Contains(log, line => line.Contains("there is no fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void AProviderChangeSinceTheBaselineRefusesTheSpawn()
    {
        var (sequence, log, _) = Sequence();
        Run(sequence, "cp3");
        Run(sequence, "baseline");
        generation++;
        Run(sequence, "spawn");
        Assert.Equal(2, sequence.NextStep);
        Assert.DoesNotContain("create", scene.Calls);
        Assert.Contains(log, line => line.Contains("a provider changed since the baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void ALocationChangeSinceTheBaselineIsALiveExit()
    {
        var place = new SceneLocation(132, null);
        var log = new List<string>();
        var current = (SceneLocation?)place;
        var sequence = new HumanPuppetT3Sequence(scene, scene, scene, scene, new FakeProbe(scene), RowOne, () => 0, () => generation,
            () => exit, () => current, log.Add);
        foreach (var step in new[] { "cp3", "baseline", "spawn" })
            Run(sequence, step);
        Assert.Null(sequence.Host.Stop);
        current = new SceneLocation(133, null);
        sequence.OnFrameworkUpdate(++tick, 16);
        Assert.Equal(HumanPuppetStopRule.LiveExit, sequence.Host.Stop!.Rule);
        Assert.False(scene.Exists(200));
        Assert.Contains(log, line => line.Contains("STOP LiveExit", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterAStopEveryStepIsRefusedAndNothingIsRetried()
    {
        var (sequence, log, _) = Sequence();
        foreach (var step in new[] { "cp3", "baseline", "spawn" })
            Run(sequence, step);
        scene.At(200).Targetable = true;
        sequence.OnFrameworkUpdate(++tick, 16);
        Assert.Equal(HumanPuppetStopRule.Hiding, sequence.Host.Stop!.Rule);
        var calls = scene.Calls.Count;
        Run(sequence, "draw");
        Assert.Equal(3, sequence.NextStep);
        Assert.Equal(calls, scene.Calls.Count);
        Assert.Contains(log, line => line.Contains("draw refused: the run has stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void AtMostEightStepsAreQueuedAndOneRunsPerUpdate()
    {
        var (sequence, _, _) = Sequence();
        for (var i = 0; i < 8; i++)
            Assert.True(sequence.Submit("cp3"));
        Assert.False(sequence.Submit("cp3"));
        sequence.OnFrameworkUpdate(++tick, 16);
        Assert.Equal(1, sequence.NextStep);
        // The other seven are refused one per update, out of order, with no side effect.
        for (var i = 0; i < 7; i++)
            sequence.OnFrameworkUpdate(++tick, 16);
        Assert.Equal(1, sequence.NextStep);
        Assert.True(sequence.Submit("baseline"));
    }

    [Fact]
    public void AProfileChangeOnThePlayerIsReportedAtIsolation()
    {
        var (sequence, log, probe) = Sequence();
        foreach (var step in new[] { "cp3", "baseline", "spawn", "draw" })
            Run(sequence, step);
        probe.Facts["customize+ active profile on 0"] = "code 0, profile 0123456789abcdef";
        Run(sequence, "isolation");
        Assert.Contains(log, line => line.Contains("isolation: run facts that differ from the baseline: customize+ active profile on 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AHitchWhileLiveIsCounted()
    {
        var (sequence, log, _) = Sequence();
        foreach (var step in new[] { "cp3", "baseline", "spawn", "draw" })
            Run(sequence, step);
        sequence.OnFrameworkUpdate(++tick, 16);
        sequence.OnFrameworkUpdate(++tick, 250);
        foreach (var step in new[] { "isolation", "remove", "report" })
            Run(sequence, step);
        Assert.Contains(log, line => line.Contains("longest 250.0 ms, 1 over 100 ms, 0 of them on the update after a step", StringComparison.Ordinal));
    }

    [Fact]
    public void AHitchOnTheUpdateAfterAStepIsCountedApart()
    {
        var (sequence, log, _) = Sequence();
        foreach (var step in new[] { "cp3", "baseline", "spawn", "draw" })
            Run(sequence, step);
        sequence.OnFrameworkUpdate(++tick, 250);
        foreach (var step in new[] { "isolation", "remove", "report" })
            Run(sequence, step);
        Assert.Contains(log, line => line.Contains("1 over 100 ms, 1 of them on the update after a step", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnansweredFactIsNotMeasuredEvenWhenTheBaselineWasUnansweredToo()
    {
        var (sequence, log, probe) = Sequence();
        probe.Facts["resource paths"] = "unanswered (IpcNotReadyError)";
        foreach (var step in new[] { "cp3", "baseline", "spawn", "draw", "isolation" })
            Run(sequence, step);
        Assert.Contains(log, line => line.Contains("isolation: run facts not measured (a read was unanswered): resource paths", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("isolation: every run fact equals the baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void AProbeReadThatThrowsBeforeAStepRefusesItAndTheHostStillTicks()
    {
        var (sequence, log, probe) = Sequence();
        foreach (var step in new[] { "cp3", "baseline", "spawn" })
            Run(sequence, step);
        probe.HomeWorldThrows = true;
        Run(sequence, "draw");
        Assert.Equal(3, sequence.NextStep);
        Assert.Contains(log, line => line.Contains("draw refused: a read failed (InvalidOperationException)", StringComparison.Ordinal));
        Assert.DoesNotContain("draw on 200", scene.Calls);
        // The host ticked first, so this update's F2 check still ran.
        Assert.Contains(sequence.Host.Readings, reading => reading.Phase == "F2 tick" && reading.Update == tick);
        Assert.Null(sequence.Host.Stop);
    }
}
#endif
