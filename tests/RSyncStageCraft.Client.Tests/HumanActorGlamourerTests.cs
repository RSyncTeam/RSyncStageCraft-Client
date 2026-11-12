#if RSC_DEVELOPMENT
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-3: HV-2's visible human with one equipment-only Glamourer state, by
/// object index only: gated on the checked builds, probed before the
/// Penumbra mod and again before the apply, read back, reverted and checked
/// for a lock before the Penumbra release, and the collection deleted with
/// its mod still in it while the actor may resolve to it. Every applied,
/// uncertain or left state is a cleanup failure. Synthetic identities, a fake
/// Glamourer and a fake Penumbra only: no game, no IPC.
/// </summary>
public sealed class HumanActorGlamourerTests
{
    private const uint Key = 0x5EED1234;
    private const string Prefix = "RSC HV-3 ";

    private readonly FakeHumanActorScene scene = new();
    private readonly FakeHumanActorPenumbra penumbra;
    private readonly FakeHumanActorGlamourer glamourer;
    private readonly List<string> lines = [];
    private HumanActorGlamourerStage? stage;

    public HumanActorGlamourerTests()
    {
        penumbra = new FakeHumanActorPenumbra(scene);
        glamourer = new FakeHumanActorGlamourer(scene);
    }

    private HumanActorGlamourerStage Stage => stage!;

    private const string FirstName = "Rsc Ccccccccccccccc";
    private const string SecondName = "Rsc Ddddddddddddddd";

    private HumanActorBaseline Baseline()
    {
        stage = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix) { KeySource = () => Key };
        var penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix)
        {
            Contained = FakeHumanActorPenumbra.Contained,
            ModFolderReadable = folder => folder == FakeHumanActorPenumbra.ModRoot,
            ReleaseByDeletion = true,
        };
        return new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, stage, Prefix);
    }

    private static void Run(HumanActorBaseline baseline, int updates)
    {
        for (var i = 0; i < updates; i++)
            baseline.OnFrameworkUpdate();
    }

    /// <summary>HV-2's spawn, Penumbra stage and redraw: the actor is visible after these updates.</summary>
    private const int ToVisible = 6;

    private int IndexOf(string call)
    {
        var index = scene.Calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private HumanActorBaseline Visible(Func<IReadOnlySet<char>, string>? names = null)
    {
        var baseline = Baseline();
        if (names is not null)
            baseline.NameSource = names;
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Run(baseline, ToVisible);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        return baseline;
    }

    /// <summary>Visible, then the update on which the state is applied and read back.</summary>
    private HumanActorBaseline Applied(Func<IReadOnlySet<char>, string>? names = null)
    {
        var baseline = Visible(names);
        Run(baseline, 1);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanGlamourerState.Applied, Stage.State);
        return baseline;
    }

    private IEnumerable<string> GlamourerCalls => scene.Calls.Where(call => call.StartsWith("glamourer", StringComparison.Ordinal));

    [Fact]
    public void ACleanRunAppliesReadsBackRevertsAndDeletesTheCollectionWithItsMod()
    {
        penumbra.Parents[200] = 200;
        var baseline = Applied();
        // The early probe: after the parent reads -1, before the collection exists.
        Assert.True(IndexOf("penumbra parent 200 -1") < IndexOf("glamourer unlock 200"));
        Assert.True(IndexOf("glamourer unlock 200") < IndexOf("penumbra create"));
        // The second probe, the apply and the readback, after the actor is visible and back to back.
        var alphaOne = scene.Calls.LastIndexOf("alpha 200 1");
        Assert.Equal(new[] { "glamourer unlock 200", "glamourer apply 200", "glamourer get 200" }, scene.Calls.Skip(alphaOne + 1).ToArray());
        Assert.Contains(Prefix + "glamourer: API 1.8, Glamourer 1.7.1.3, Penumbra 1.7.2.1; parent -1; probe: no state (twice); " +
            "apply Success (equipment only, unlocked); readback 10 of 10 slot(s).", lines);

        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        // Hidden, reverted and checked while the actor exists, then the collection deleted with its mod: no mod removal first.
        Assert.Equal(new[] { "alpha 200 0", "glamourer revert 200", "glamourer canunlock 200", "penumbra delete", "draw off 200", "delete 200" },
            scene.Calls.Skip(from).ToArray());
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.Contains(Prefix + "glamourer cleanup: revert Success; locked no.", lines);
        Assert.Contains(Prefix + "glamourer residue: 1 in-memory state for this spawn's synthetic name, unlocked, reverted (revert answered Success); " +
            "1 this load; cleared when Glamourer unloads; nothing deleted.", lines);
        Assert.Contains(Prefix + "Penumbra released: mod removed with the collection, collection deleted yes, actor off it yes, " +
            "player's collection unchanged yes.", lines);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanGlamourerState.Reverted, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Equal(Prefix + "status: None, stop none, residue 0, temporary collection none, collection residue 0, " +
            "glamourer Reverted, reverted states 1, cleanup failures 0.", baseline.Status());
    }

    [Fact]
    public void EveryCallIsByIndexWithThisSpawnsKeyAndTheEquipmentFlagAlone()
    {
        var baseline = Applied();
        Assert.True(baseline.Remove());
        Assert.All(glamourer.Log, call => Assert.Equal(200, call.Index));
        Assert.All(glamourer.Log.Where(call => call.Call != "canunlock"), call => Assert.Equal(Key, call.Key));
        // The lock check uses no key: it reports whether anyone holds a lock.
        Assert.Equal(0u, glamourer.Log.Single(call => call.Call == "canunlock").Key);
        Assert.Equal(HumanActorGlamourerStage.EquipmentOnly, glamourer.Log.Single(call => call.Call == "apply").Flags);
        Assert.Equal(HumanActorGlamourerStage.EquipmentOnly, glamourer.Log.Single(call => call.Call == "revert").Flags);
        Assert.Equal(0x02ul, HumanActorGlamourerStage.EquipmentOnly);
        Assert.Equal(new[] { "unlock", "unlock", "apply", "get", "revert", "canunlock" }, glamourer.Log.Select(call => call.Call).ToArray());
        Assert.Equal(HumanActorGlamourerStage.Payload, glamourer.LastPayload);
    }

    [Fact]
    public void ThePayloadIsAFixedEquipmentOnlyDesignWithNoIdentifier()
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(HumanActorGlamourerStage.Payload));
        Assert.Equal(HumanActorGlamourerStage.PayloadJson, text);
        Assert.Equal('{', text[0]);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(new[] { "FileVersion", "Equipment", "Customize" }, root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("FileVersion").GetInt32());
        // Glamourer's "Nothing" item for each armour and accessory slot: uint.MaxValue - 128 - EquipSlot.ToSlot();
        // ToSlot maps LFinger (14) to RFinger (12), so both rings share one id. Written out, not derived from the stage.
        var expected = new (string Slot, ulong Item)[]
        {
            ("Head", 4294967164), ("Body", 4294967163), ("Hands", 4294967162), ("Legs", 4294967160), ("Feet", 4294967159),
            ("Ears", 4294967158), ("Neck", 4294967157), ("Wrists", 4294967156), ("RFinger", 4294967155), ("LFinger", 4294967155),
        };
        Assert.Equal(expected, HumanActorGlamourerStage.Slots.ToArray());
        var equipment = root.GetProperty("Equipment");
        Assert.Equal(expected.Select(slot => slot.Slot).Concat(new[] { "Hat", "VieraEars", "Visor", "Weapon" }),
            equipment.EnumerateObject().Select(property => property.Name));
        foreach (var (slot, item) in expected)
        {
            var entry = equipment.GetProperty(slot);
            Assert.Equal(item, entry.GetProperty("ItemId").GetUInt64());
            Assert.True(entry.GetProperty("Apply").GetBoolean());
            Assert.False(entry.GetProperty("ApplyStain").GetBoolean());
            Assert.Equal(0, entry.GetProperty("Stain").GetInt32());
            Assert.Equal(0, entry.GetProperty("Stain2").GetInt32());
        }
        // No weapon, no meta toggle, no customization is applied.
        foreach (var meta in new[] { "Hat", "VieraEars", "Visor", "Weapon" })
            Assert.False(equipment.GetProperty(meta).GetProperty("Apply").GetBoolean());
        Assert.DoesNotContain("MainHand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OffHand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Identifier", text, StringComparison.Ordinal);
        // Customize: model 0, and race and clan stated as Hyur (1) and Midlander (1) so that Glamourer 1.7.1.3's loader has
        // nothing to default; neither is applied, and no other customize value is present.
        var customize = root.GetProperty("Customize");
        Assert.Equal(new[] { "ModelId", "Race", "Clan" }, customize.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(0, customize.GetProperty("ModelId").GetInt32());
        foreach (var name in new[] { "Race", "Clan" })
        {
            var entry = customize.GetProperty(name);
            Assert.Equal(new[] { "Value", "Apply" }, entry.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(1, entry.GetProperty("Value").GetInt32());
            Assert.False(entry.GetProperty("Apply").GetBoolean());
        }
        Assert.DoesNotContain("\"Apply\":true", customize.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api 1.7", "API 1.7 is not 1.8 or later")]
    [InlineData("api 2.0", "API 2.0 is not 1.8 or later")]
    [InlineData("api none", "API unanswered is not 1.8 or later")]
    [InlineData("glamourer 1.7.1.4", "Glamourer 1.7.1.4 is not 1.7.1.3")]
    [InlineData("glamourer none", "Glamourer not loaded is not 1.7.1.3")]
    [InlineData("penumbra 1.7.2.0", "Penumbra 1.7.2.0 is not 1.7.2.1")]
    [InlineData("penumbra none", "Penumbra not loaded is not 1.7.2.1")]
    public void AnUncheckedBuildRefusesBeforeAnyCollectionOrGlamourerCall(string change, string why)
    {
        switch (change)
        {
            case "api 1.7":
                glamourer.Api = (1, 7);
                break;
            case "api 2.0":
                glamourer.Api = (2, 0);
                break;
            case "api none":
                glamourer.Api = null;
                break;
            case "glamourer 1.7.1.4":
                glamourer.GlamourerVersionAnswer = "1.7.1.4";
                break;
            case "glamourer none":
                glamourer.GlamourerVersionAnswer = null;
                break;
            case "penumbra 1.7.2.0":
                glamourer.PenumbraVersionAnswer = "1.7.2.0";
                break;
            default:
                glamourer.PenumbraVersionAnswer = null;
                break;
        }
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 1);
        Assert.Equal("glamourer: " + why, baseline.Stop);
        Assert.Empty(GlamourerCalls);
        Assert.Empty(penumbra.Created);
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanGlamourerState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
    }

    [Theory]
    [InlineData(HumanActorGlamourerStage.Success, "a Glamourer state already exists for the actor (before the Penumbra mod); not touched")]
    [InlineData(HumanActorGlamourerStage.InvalidKey, "a Glamourer state locked by someone else exists for the actor (before the Penumbra mod); not touched")]
    [InlineData(HumanActorGlamourerStage.ActorNotFound, "the probe before the Penumbra mod answered ActorNotFound")]
    public void AnExistingStateAtTheEarlyProbeRefusesWithNothingMadeOrTouched(int answer, string why)
    {
        glamourer.ProbeAnswers.Enqueue(answer);
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 1);
        Assert.Equal("glamourer: " + why, baseline.Stop);
        Assert.Equal(new[] { "glamourer unlock 200" }, GlamourerCalls.ToArray());
        Assert.Empty(penumbra.Created);
        Assert.False(scene.Exists(200));
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(Prefix + "glamourer cleanup: nothing applied; no Glamourer state created by RSC.", lines);
    }

    [Fact]
    public void AnEarlyProbeThatDoesNotAnswerRefusesWithNothingMade()
    {
        glamourer.Throws.Add("unlock");
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 1);
        Assert.Equal("glamourer: the probe before the Penumbra mod did not answer (InvalidOperationException)", baseline.Stop);
        Assert.Empty(penumbra.Created);
        Assert.DoesNotContain("glamourer apply 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AReservedKeyRefusesBeforeAnyCollection()
    {
        var baseline = Baseline();
        Stage.KeySource = () => HumanPuppetKey.Reserved;
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 1);
        Assert.Equal("glamourer: the key source returned a reserved key", baseline.Stop);
        Assert.Empty(GlamourerCalls);
        Assert.Empty(penumbra.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStateThatAppearsBeforeTheApplyIsNeverTouched(bool lockedBySomeoneElse)
    {
        var baseline = Visible();
        // Another plugin made a state for the actor while it was being shown. The identity is read now:
        // the stop deletes the actor, and the state outlives it.
        var identity = glamourer.IdentityAt(200)!.Value;
        glamourer.States[identity] = new FakeHumanActorGlamourer.State(lockedBySomeoneElse ? 77u : 0u);
        Run(baseline, 1);
        Assert.StartsWith("glamourer: a Glamourer state ", baseline.Stop, StringComparison.Ordinal);
        Assert.EndsWith("(before the apply); not touched", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain(scene.Calls, call => call is "glamourer apply 200" or "glamourer revert 200" or "glamourer get 200");
        Assert.Equal(HumanGlamourerState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.False(glamourer.States[identity].Reverted);
        // The actor still resolves to the collection: deleted with its mod, never the mod first.
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData(HumanActorGlamourerStage.InvalidState, "InvalidState")]
    [InlineData(HumanActorGlamourerStage.ActorNotFound, "ActorNotFound")]
    public void AnApplyRefusedBeforeAnyStateIsNotACleanupFailure(int answer, string name)
    {
        glamourer.ApplyAnswer = answer;
        var baseline = Visible();
        Run(baseline, 1);
        Assert.Equal($"glamourer: the apply was refused ({name}); no state was created", baseline.Stop);
        Assert.Equal(HumanGlamourerState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.DoesNotContain("glamourer revert 200", scene.Calls);
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData(HumanActorGlamourerStage.ActorNotHuman, "the apply answered ActorNotHuman; a state may exist")]
    [InlineData(HumanActorGlamourerStage.InvalidKey, "the apply answered InvalidKey; a state may exist")]
    [InlineData(HumanActorGlamourerStage.NothingDone, "the apply answered NothingDone; a state may exist")]
    [InlineData(99, "the apply answered code 99; a state may exist")]
    [InlineData(-1, "the apply did not answer (InvalidOperationException); a state may exist")]
    public void AnAmbiguousApplyIsRevertedAndStillACleanupFailure(int answer, string why)
    {
        if (answer < 0)
            glamourer.Throws.Add("apply");
        else
            glamourer.ApplyAnswer = answer;
        var baseline = Visible();
        Run(baseline, 1);
        // The stop is the apply's; the revert is still attempted, before the Penumbra release, and the run is not clean.
        Assert.Equal("glamourer: " + why, baseline.Stop);
        Assert.True(IndexOf("glamourer revert 200") < IndexOf("glamourer canunlock 200"));
        Assert.True(IndexOf("glamourer canunlock 200") < IndexOf("penumbra delete"));
        Assert.DoesNotContain("glamourer get 200", scene.Calls);
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Equal(HumanGlamourerState.Reverted, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Contains(Prefix + "glamourer cleanup failure: the apply's outcome was uncertain; the state was reverted, but the run is not clean.", lines);
        Assert.EndsWith("glamourer Reverted, reverted states 1, cleanup failures 1.", baseline.Status(), StringComparison.Ordinal);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData("mismatch", "the readback matched 9 of 10 slot(s); the state is uncertain")]
    [InlineData("code", "the readback answered ActorNotFound; the state is uncertain")]
    [InlineData("null", "the readback answered Success; the state is uncertain")]
    [InlineData("throw", "the readback did not answer (InvalidOperationException); the state is uncertain")]
    public void AReadbackThatDoesNotMatchIsUncertainAndACleanupFailure(string fault, string why)
    {
        switch (fault)
        {
            case "mismatch":
                // Another plugin's handler, run inside the apply, changed one slot.
                glamourer.DuringApply = () => glamourer.States[glamourer.IdentityAt(200)!.Value].Items["Head"] = 1;
                break;
            case "code":
                glamourer.GetAnswer = HumanActorGlamourerStage.ActorNotFound;
                break;
            case "null":
                glamourer.GetReturnsNoItems = true;
                break;
            default:
                glamourer.Throws.Add("get");
                break;
        }
        var baseline = Visible();
        Run(baseline, 1);
        Assert.Equal("glamourer: " + why, baseline.Stop);
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.Equal(HumanGlamourerState.Reverted, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.DoesNotContain(lines, line => line.Contains("readback 10 of 10", StringComparison.Ordinal));
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData("answer", "glamourer cleanup: the revert answered ActorNotFound")]
    [InlineData("throw", "glamourer cleanup: the revert did not answer (InvalidOperationException)")]
    [InlineData("locked", "glamourer cleanup: the state is still locked")]
    [InlineData("lockcode", "glamourer cleanup: the lock check answered ActorNotFound")]
    [InlineData("lockthrow", "glamourer cleanup: the lock check did not answer (InvalidOperationException)")]
    public void AFailedRevertIsLeftAndReportedAndTheActorIsStillDeleted(string fault, string stop)
    {
        var baseline = Applied();
        switch (fault)
        {
            case "answer":
                glamourer.RevertAnswer = HumanActorGlamourerStage.ActorNotFound;
                break;
            case "throw":
                glamourer.Throws.Add("revert");
                break;
            case "locked":
                glamourer.StillLocked = true;
                break;
            case "lockcode":
                glamourer.CanUnlockAnswer = HumanActorGlamourerStage.ActorNotFound;
                break;
            default:
                glamourer.Throws.Add("canunlock");
                break;
        }
        Assert.True(baseline.Remove());
        Assert.Equal(stop, baseline.Stop);
        Assert.Equal(HumanGlamourerState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Contains(Prefix + "glamourer residue: an applied or uncertain state for this spawn's synthetic name is left in Glamourer's memory; " +
            "CLEANUP FAILURE.", lines);
        Assert.DoesNotContain(lines, line => line.Contains("nothing deleted", StringComparison.Ordinal));
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
        Assert.False(baseline.Spawn());
    }

    [Fact]
    public void APluginChangeSinceTheGateStopsBeforeTheApply()
    {
        var baseline = Visible();
        glamourer.Generation++;
        Run(baseline, 1);
        Assert.Equal("glamourer: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.DoesNotContain("glamourer apply 200", scene.Calls);
        Assert.Single(GlamourerCalls);
        Assert.Equal(HumanGlamourerState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
    }

    [Fact]
    public void APluginChangeAfterTheApplyLeavesTheStateWithoutACall()
    {
        var baseline = Applied();
        glamourer.Generation++;
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.Equal("glamourer cleanup: revert not called: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.Equal(new[] { "alpha 200 0", "penumbra delete", "draw off 200", "delete 200" }, scene.Calls.Skip(from).ToArray());
        Assert.Equal(HumanGlamourerState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
    }

    [Fact]
    public void ASecondCarrierOfTheNameBeforeTheApplyRefusesIt()
    {
        var baseline = Visible();
        scene.Add(201, -1, kind: 1).Name = scene.At(200).Name!.ToUpperInvariant();
        Run(baseline, 1);
        Assert.Equal("glamourer: another object carries the actor's name", baseline.Stop);
        Assert.DoesNotContain("glamourer apply 200", scene.Calls);
        Assert.Equal(HumanGlamourerState.None, Stage.State);
        Assert.False(scene.Exists(200));
        Assert.True(scene.Exists(201));
    }

    [Fact]
    public void ASecondCarrierOfTheNameBeforeTheRevertLeavesTheState()
    {
        var baseline = Applied();
        scene.Add(201, -1, kind: 1).Name = scene.At(200).Name;
        Assert.True(baseline.Remove());
        Assert.Equal("glamourer cleanup: revert not called: another object carries the actor's name", baseline.Stop);
        Assert.DoesNotContain("glamourer revert 200", scene.Calls);
        Assert.Equal(HumanGlamourerState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AParentThatNoLongerReadsDetachedLeavesTheState()
    {
        var baseline = Applied();
        penumbra.Parents[200] = 0;
        Assert.True(baseline.Remove());
        Assert.Equal("glamourer cleanup: revert not called: the actor's Penumbra parent does not read -1", baseline.Stop);
        Assert.DoesNotContain("glamourer revert 200", scene.Calls);
        Assert.Equal(1, Stage.CleanupFailures);
    }

    [Fact]
    public void ALostActorGetsNoGlamourerCallAndItsStateIsReported()
    {
        var baseline = Applied();
        scene.Replace(200);
        var from = scene.Calls.Count;
        baseline.OnFrameworkUpdate();
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        // No Glamourer call and no mod removal: the collection is deleted with its mod, and the mod's removal is reported as not provable.
        Assert.Equal(new[] { "penumbra delete" }, scene.Calls.Skip(from).ToArray());
        Assert.Equal(HumanGlamourerState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "glamourer cleanup failure: no call after the loss of ownership: ", StringComparison.Ordinal));
        Assert.Contains(Prefix + "Penumbra released: mod removed with the collection only if it was still assigned (not provable), " +
            "collection deleted yes, actor off it not read, player's collection unchanged yes.", lines);
        Assert.Equal("Someone Else", scene.At(200).Name);
        baseline.Unload();
        Assert.Equal(from + 1, scene.Calls.Count);
    }

    [Fact]
    public void UnloadRevertsAndReleasesInTheSameOrder()
    {
        var baseline = Applied();
        var from = scene.Calls.Count;
        baseline.Unload();
        Assert.Equal(new[] { "alpha 200 0", "glamourer revert 200", "glamourer canunlock 200", "penumbra delete", "draw off 200", "delete 200" },
            scene.Calls.Skip(from).ToArray());
        Assert.Equal(HumanGlamourerState.Reverted, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(Prefix + "unload: deleted, slot empty yes, residue 0.", lines);
    }

    [Fact]
    public void AFailureBeforeTheApplyWhileAssignedDeletesTheCollectionWithItsMod()
    {
        // HV-2's redraw stop, with the actor still resolving to the collection.
        penumbra.ActorResolvesElsewhere = FakeHumanActorPenumbra.Skeleton;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible);
        Assert.StartsWith("redraw: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(new[] { "glamourer unlock 200" }, GlamourerCalls.ToArray());
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.True(IndexOf("penumbra delete") < IndexOf("delete 200"));
        Assert.Empty(penumbra.Live);
        Assert.Contains(Prefix + "Penumbra released: mod removed with the collection, collection deleted yes, actor off it yes, " +
            "player's collection unchanged yes.", lines);
    }

    [Fact]
    public void AnAssignmentThatNeverTookUsesHv2sOrder()
    {
        // No actor resolves to the collection: the mod can be removed first without any Glamourer reapply.
        penumbra.AssignAnswer = HumanAssignmentResult.Failed;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible);
        Assert.Equal("penumbra: the assignment failed", baseline.Stop);
        Assert.True(IndexOf("penumbra unmod") < IndexOf("penumbra delete"));
        Assert.Contains(Prefix + "Penumbra released: mod removed yes, collection deleted yes, actor off it yes, player's collection unchanged yes.", lines);
        Assert.Empty(penumbra.Live);
    }

    [Fact]
    public void AnAssignmentThatCannotBeReadIsDeletedAndReportedAsNotProvable()
    {
        // The slot changes hands inside the assignment call.
        penumbra.ReplaceActorOnAssign = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible);
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Contains("penumbra delete", scene.Calls);
        Assert.Contains(lines, line => line.Contains("mod removed with the collection only if it was still assigned (not provable)", StringComparison.Ordinal));
        Assert.Equal(new[] { "glamourer unlock 200" }, GlamourerCalls.ToArray());
        Assert.Equal(0, Stage.CleanupFailures);
    }

    [Fact]
    public void AnUnconfirmedDeletionIsCollectionResidue()
    {
        var baseline = Applied();
        penumbra.DeleteAnswer = false;
        Assert.True(baseline.Remove());
        Assert.Equal("penumbra release: the temporary collection's deletion was not confirmed", baseline.Stop);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "Penumbra released: mod removed no, collection deleted no", StringComparison.Ordinal));
        Assert.DoesNotContain("penumbra unmod", scene.Calls);
        Assert.Contains("collection residue 1", baseline.Status(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStageRefusesToBeEnteredFromInsideAGlamourerCall()
    {
        var answers = new List<string?>();
        glamourer.DuringApply = () =>
        {
            answers.Add(Stage.EarlyProbe(200, null));
            answers.Add(Stage.Apply(200, null));
            answers.Add(Stage.Revert(200, null));
        };
        var baseline = Visible();
        Run(baseline, 1);
        Assert.Equal(new[]
        {
            "glamourer: the stage was entered again during a Glamourer call",
            "glamourer: the stage was entered again during a Glamourer call",
            "glamourer cleanup: the stage was entered again during a Glamourer call",
        }, answers);
        // Nothing reentered reached Glamourer; the reentry itself is a reported failure.
        Assert.Equal(new[] { "unlock", "unlock", "apply", "get" }, glamourer.Log.Select(call => call.Call).ToArray());
        Assert.Equal(HumanGlamourerState.Applied, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
    }

    [Fact]
    public void TheApplyIsMadeOncePerSpawn()
    {
        var baseline = Applied();
        Run(baseline, 5);
        Assert.Single(scene.Calls, call => call == "glamourer apply 200");
        Assert.True(baseline.Remove());
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void ASecondSpawnAfterACleanRunGetsItsOwnKeyAndAddsOneRevertedState()
    {
        // Two distinct synthetic identities at the same object index: the first one's reverted state stays in
        // Glamourer's memory under its own name and world, and the second identity has none.
        var names = new Queue<string>([FirstName, SecondName]);
        var baseline = Applied(_ => names.Dequeue());
        Assert.Equal(FirstName, scene.At(200).Name);
        Stage.KeySource = () => Key + 1;
        Assert.True(baseline.Remove());
        Assert.Null(baseline.Stop);
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Run(baseline, ToVisible + 1);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanGlamourerState.Applied, Stage.State);
        Assert.Equal(Key + 1, glamourer.Log.Last(call => call.Call == "apply").Key);
        Assert.Equal(2, scene.Calls.Count(call => call == "glamourer apply 200"));
        Assert.True(baseline.Remove());
        Assert.Equal(2, Stage.RevertedStates);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(lines, line => line.Contains("2 this load; cleared when Glamourer unloads", StringComparison.Ordinal));
        // Both identities keep their own reverted state; neither inherited the other's.
        Assert.True(glamourer.States[(FirstName, FakeHumanActorGlamourer.World)].Reverted);
        Assert.True(glamourer.States[(SecondName, FakeHumanActorGlamourer.World)].Reverted);
        Assert.Equal(2, glamourer.States.Count);
    }

    [Fact]
    public void AReusedIdentityWithRetainedStateIsRefusedBeforeAnyCollection()
    {
        // The same synthetic name is drawn again after a clean run: Glamourer still holds that identity's
        // reverted state, so the early probe answers Success and the new spawn stops with nothing made or touched.
        var baseline = Applied(_ => FirstName);
        Assert.True(baseline.Remove());
        Assert.Null(baseline.Stop);
        var retained = glamourer.States[(FirstName, FakeHumanActorGlamourer.World)];
        Assert.True(retained.Reverted);
        var from = scene.Calls.Count;
        var created = penumbra.Created.Count;
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Run(baseline, ToVisible + 1);
        Assert.Equal("glamourer: a Glamourer state already exists for the actor (before the Penumbra mod); not touched", baseline.Stop);
        var calls = scene.Calls.Skip(from).ToArray();
        Assert.Equal(new[] { "glamourer unlock 200" }, calls.Where(call => call.StartsWith("glamourer", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain("penumbra create", calls);
        Assert.Equal(created, penumbra.Created.Count);
        Assert.Same(retained, glamourer.States[(FirstName, FakeHumanActorGlamourer.World)]);
        Assert.True(retained.Reverted);
        Assert.Single(glamourer.States);
        Assert.Equal(1, Stage.RevertedStates);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AStateLeftByAnEarlierSpawnRefusesTheNextBeforeAnyCollection()
    {
        // The stage alone: a later spawn after a left state (the baseline also latches a stop then).
        var alone = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix) { KeySource = () => Key };
        // An object with a synthetic identity at the index; the scene has no spawn here.
        scene.Add(200, -1, kind: 1).Name = FirstName;
        Assert.Null(alone.EarlyProbe(200, null));
        alone.Abandon("synthetic loss");
        Assert.Equal(HumanGlamourerState.None, alone.State);
        Assert.Null(alone.Apply(200, null));
        alone.Abandon("synthetic loss");
        Assert.Equal(HumanGlamourerState.Left, alone.State);
        var calls = glamourer.Log.Count;
        Assert.Equal("glamourer: the last spawn's state is Left, not cleaned up", alone.EarlyProbe(200, null));
        Assert.Equal(calls, glamourer.Log.Count);
        Assert.Equal(1, alone.CleanupFailures);
    }

    [Fact]
    public void TheGlamourerStageNeedsThePenumbraStage()
    {
        var alone = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix);
        Assert.Throws<ArgumentException>(() => new HumanActorBaseline(scene, () => null, lines.Add, null, alone, Prefix));
    }

    [Fact]
    public void WithoutTheGlamourerStageHv2IsUnchanged()
    {
        var hv2 = new HumanActorBaseline(scene, () => null, lines.Add,
            new HumanActorPenumbraStage(penumbra, lines.Add, "RSC HV-2 ")
            {
                Contained = FakeHumanActorPenumbra.Contained,
                ModFolderReadable = folder => folder == FakeHumanActorPenumbra.ModRoot,
            }, "RSC HV-2 ");
        Assert.True(hv2.Spawn());
        Run(hv2, ToVisible + 1);
        Assert.Equal(HumanActorPhase.Visible, hv2.Phase);
        var from = scene.Calls.Count;
        Assert.True(hv2.Remove());
        Assert.Equal(new[] { "alpha 200 0", "penumbra unmod", "penumbra delete", "draw off 200", "delete 200" }, scene.Calls.Skip(from).ToArray());
        Assert.Empty(GlamourerCalls);
        Assert.Equal("RSC HV-2 status: None, stop none, residue 0, temporary collection none, collection residue 0.", hv2.Status());
    }

    [Fact]
    public void ThePortHasNoByNameDeleteLockAllOrDesignCall()
    {
        var methods = typeof(IHumanActorGlamourer).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "ApplyState", "CanUnlock", "GetState", "GetStateJson", "RevertState", "UnlockState",
            "get_ApiVersion", "get_Generation", "get_GlamourerVersion", "get_PenumbraVersion",
        }.Order(StringComparer.Ordinal), methods);
        Assert.Empty(typeof(IHumanActorGlamourer).GetEvents());
    }
}

/// <summary>
/// A synthetic Glamourer for HV-3. As in Glamourer, a state belongs to the
/// actor's identity, not to its object index: for this player-kind actor, its
/// name and world. Every scene object shares one fixed synthetic world, so the
/// name decides. A state outlives the object, so a reused index with a fresh
/// name has no state, and a reused name finds its retained one. An index with
/// no object answers ActorNotFound. Each state has its lock key, items and
/// whether it was reverted. Answer codes follow Glamourer's (UnlockState
/// NothingDone without a state, InvalidKey under another key). Calls are
/// logged into the scene's list, in order.
/// </summary>
public sealed class FakeHumanActorGlamourer : IHumanActorGlamourer
{
    private readonly FakeHumanActorScene scene;

    internal FakeHumanActorGlamourer(FakeHumanActorScene scene) => this.scene = scene;

    public sealed class State(uint lockKey)
    {
        public uint LockKey { get; set; } = lockKey;
        public Dictionary<string, ulong> Items { get; } = new(StringComparer.Ordinal);
        public bool Reverted { get; set; }
    }

    public (int Major, int Minor)? Api { get; set; } = (1, 8);
    public string? GlamourerVersionAnswer { get; set; } = "1.7.1.3";
    public string? PenumbraVersionAnswer { get; set; } = "1.7.2.1";
    public long Generation { get; set; } = 41;
    /// <summary>The fixed synthetic world every scene object's identity uses.</summary>
    public const ushort World = 9000;

    public Dictionary<(string Name, ushort World), State> States { get; } = [];

    /// <summary>The identity Glamourer would resolve at the index: the object's name and the fixed world; null without an object.</summary>
    public (string Name, ushort World)? IdentityAt(int objectIndex) =>
        scene.Exists(objectIndex) && scene.At(objectIndex).Name is { Length: > 0 } name ? (name, World) : null;
    public Queue<int> ProbeAnswers { get; } = new();
    public HashSet<string> Throws { get; } = new(StringComparer.Ordinal);
    public int? ApplyAnswer { get; set; }
    public Action? DuringApply { get; set; }
    public int? GetAnswer { get; set; }
    public bool GetReturnsNoItems { get; set; }
    public int? RevertAnswer { get; set; }
    public int? CanUnlockAnswer { get; set; }
    public bool StillLocked { get; set; }
    public string? LastPayload { get; private set; }
    public List<(string Call, int Index, uint Key, ulong Flags)> Log { get; } = [];

    public (int Major, int Minor)? ApiVersion => Api;
    public string? GlamourerVersion => GlamourerVersionAnswer;
    public string? PenumbraVersion => PenumbraVersionAnswer;

    public int UnlockState(int objectIndex, uint key)
    {
        Record("unlock", objectIndex, key, 0);
        if (Throws.Contains("unlock"))
            throw new InvalidOperationException();
        if (ProbeAnswers.TryDequeue(out var answer))
            return answer;
        if (IdentityAt(objectIndex) is not { } identity)
            return HumanActorGlamourerStage.ActorNotFound;
        if (!States.TryGetValue(identity, out var state))
            return HumanActorGlamourerStage.NothingDone;
        if (state.LockKey != 0 && state.LockKey != key)
            return HumanActorGlamourerStage.InvalidKey;
        state.LockKey = 0;
        return HumanActorGlamourerStage.Success;
    }

    public int ApplyState(string base64State, int objectIndex, uint key, ulong flags)
    {
        Record("apply", objectIndex, key, flags);
        LastPayload = base64State;
        // Glamourer creates the state before it answers ActorNotHuman or InvalidKey; InvalidState and ActorNotFound come first.
        if (ApplyAnswer is HumanActorGlamourerStage.InvalidState or HumanActorGlamourerStage.ActorNotFound)
            return ApplyAnswer.Value;
        if (IdentityAt(objectIndex) is not { } identity)
            return HumanActorGlamourerStage.ActorNotFound;
        var state = new State(0);
        foreach (var (slot, item) in HumanActorGlamourerStage.Slots)
            state.Items[slot] = item;
        States[identity] = state;
        DuringApply?.Invoke();
        if (Throws.Contains("apply"))
            throw new InvalidOperationException();
        return ApplyAnswer ?? HumanActorGlamourerStage.Success;
    }

    public (int Code, IReadOnlyDictionary<string, ulong>? Items) GetState(int objectIndex, uint key)
    {
        Record("get", objectIndex, key, 0);
        if (Throws.Contains("get"))
            throw new InvalidOperationException();
        if (GetAnswer is { } answer)
            return (answer, null);
        if (GetReturnsNoItems)
            return (HumanActorGlamourerStage.Success, null);
        if (IdentityAt(objectIndex) is not { } identity)
            return (HumanActorGlamourerStage.ActorNotFound, null);
        if (!States.TryGetValue(identity, out var state))
            States[identity] = state = new State(0);
        return (HumanActorGlamourerStage.Success, new Dictionary<string, ulong>(state.Items, StringComparer.Ordinal));
    }

    /// <summary>HV-5: the whole state's JSON the readback returns for an identity with a state.</summary>
    public string StateJson { get; set; } = "{}";
    public int? GetJsonAnswer { get; set; }

    public (int Code, string? Json) GetStateJson(int objectIndex, uint key)
    {
        Record("getjson", objectIndex, key, 0);
        if (Throws.Contains("getjson"))
            throw new InvalidOperationException();
        if (GetJsonAnswer is { } answer)
            return (answer, null);
        if (IdentityAt(objectIndex) is not { } identity)
            return (HumanActorGlamourerStage.ActorNotFound, null);
        return (HumanActorGlamourerStage.Success, States.ContainsKey(identity) ? StateJson : "{}");
    }

    public int RevertState(int objectIndex, uint key, ulong flags)
    {
        Record("revert", objectIndex, key, flags);
        if (Throws.Contains("revert"))
            throw new InvalidOperationException();
        if (RevertAnswer is { } answer)
            return answer;
        if (IdentityAt(objectIndex) is not { } identity)
            return HumanActorGlamourerStage.ActorNotFound;
        // Glamourer answers NothingDone when the actor has no state.
        if (!States.TryGetValue(identity, out var state))
            return HumanActorGlamourerStage.NothingDone;
        state.Items.Clear();
        state.Reverted = true;
        return HumanActorGlamourerStage.Success;
    }

    public (int Code, bool IsLocked, bool CanUnlock) CanUnlock(int objectIndex, uint key)
    {
        Record("canunlock", objectIndex, key, 0);
        if (Throws.Contains("canunlock"))
            throw new InvalidOperationException();
        if (CanUnlockAnswer is { } answer)
            return (answer, false, false);
        if (IdentityAt(objectIndex) is not { } identity)
            return (HumanActorGlamourerStage.ActorNotFound, false, false);
        var locked = StillLocked || (States.TryGetValue(identity, out var state) && state.LockKey != 0);
        return (HumanActorGlamourerStage.Success, locked, !locked);
    }

    private void Record(string call, int index, uint key, ulong flags)
    {
        Log.Add((call, index, key, flags));
        scene.Calls.Add($"glamourer {call} {index}");
    }
}
#endif
