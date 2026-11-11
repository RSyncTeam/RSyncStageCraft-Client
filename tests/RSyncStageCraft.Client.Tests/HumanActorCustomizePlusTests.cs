#if RSC_DEVELOPMENT
using System.Text.Json;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-4: HV-3's visible human with one Customize+ temporary profile: gated on
/// the checked build, the actor's own Customize+ parent entry set to −1 and
/// read back before the collection, applied on the update after HV-3's
/// readback, and deleted by its receipt first in every cleanup. An ambiguous
/// apply is never cleaned by object index (owner decision D4). Synthetic
/// identities, a fake Customize+, a fake Glamourer and a fake Penumbra only:
/// no game, no IPC.
/// </summary>
public sealed class HumanActorCustomizePlusTests
{
    private const uint Key = 0x5EED1234;
    private const string Prefix = "RSC HV-4 ";
    private const ushort World = 9000;

    private readonly FakeHumanActorScene scene = new();
    private readonly FakeHumanActorPenumbra penumbra;
    private readonly FakeHumanActorGlamourer glamourer;
    private readonly FakeHumanActorCustomizePlus customizePlus;
    private readonly FakeHumanActorWorldSource worlds = new();
    private readonly List<string> lines = [];
    private HumanActorGlamourerStage? glamourerStage;
    private HumanActorCustomizePlusStage? stage;

    public HumanActorCustomizePlusTests()
    {
        penumbra = new FakeHumanActorPenumbra(scene);
        glamourer = new FakeHumanActorGlamourer(scene);
        customizePlus = new FakeHumanActorCustomizePlus(scene);
        scene.Player.World = World;
        // HV-2's game run: after the self-copy both parent tables name the actor itself.
        penumbra.Parents[200] = 200;
        customizePlus.Parents[200] = 200;
    }

    private HumanActorCustomizePlusStage Stage => stage!;

    private HumanActorBaseline Baseline()
    {
        glamourerStage = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix) { KeySource = () => Key };
        stage = new HumanActorCustomizePlusStage(customizePlus, lines.Add, Prefix);
        var penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix)
        {
            Contained = FakeHumanActorPenumbra.Contained,
            ModFolderReadable = folder => folder == FakeHumanActorPenumbra.ModRoot,
            ReleaseByDeletion = true,
        };
        return new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, glamourerStage, stage, worlds, Prefix);
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

    /// <summary>Visible, then HV-3's update: the Glamourer state applied and read back, no Customize+ profile yet.</summary>
    private HumanActorBaseline GlamourerApplied()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Run(baseline, ToVisible + 1);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanGlamourerState.Applied, glamourerStage!.State);
        Assert.Equal(HumanCustomizePlusState.None, Stage.State);
        return baseline;
    }

    /// <summary>The update after HV-3's readback: the profile applied.</summary>
    private HumanActorBaseline Applied()
    {
        var baseline = GlamourerApplied();
        Run(baseline, 1);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Applied, Stage.State);
        return baseline;
    }

    private IEnumerable<string> CustomizePlusCalls => scene.Calls.Where(call => call.StartsWith("customize+", StringComparison.Ordinal));

    [Fact]
    public void ACleanRunDetachesAppliesOnTheNextUpdateAndRemovesByReceiptFirst()
    {
        var baseline = GlamourerApplied();
        // The detach: after the Penumbra parent reads -1, before HV-3's early probe and before the collection.
        Assert.True(IndexOf("penumbra parent 200 -1") < IndexOf("customize+ setparent 200 -1"));
        Assert.True(IndexOf("customize+ setparent 200 -1") < IndexOf("glamourer unlock 200"));
        Assert.True(IndexOf("glamourer unlock 200") < IndexOf("penumbra create"));
        Assert.Equal(-1, customizePlus.ParentOf(200));
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);

        var before = scene.Calls.Count;
        Run(baseline, 1);
        // One update later: the parent check, the owner's normal profiles, the apply, and the parent again.
        Assert.Equal(new[] { "customize+ getparent 200", "customize+ owner", "customize+ apply 200", "customize+ getparent 200" },
            scene.Calls.Skip(before).ToArray());
        Assert.Equal(HumanCustomizePlusState.Applied, Stage.State);
        // The profile is keyed to the actor's synthetic name, never the player's.
        var profile = Assert.Single(customizePlus.Profiles);
        Assert.Equal(scene.At(200).Name, profile.Value.Name);
        Assert.NotEqual(FakeHumanActorScene.PlayerName, profile.Value.Name);
        Assert.Equal(HumanActorCustomizePlusStage.PayloadJson, profile.Value.Json);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "customize+: temporary profile Success, receipt ", StringComparison.Ordinal) &&
            line.EndsWith("; parent -1; owner's normal profiles read (temporary profiles cannot be read).", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(profile.Key.ToString(), StringComparison.Ordinal) ||
            line.Contains(profile.Key.ToString("N"), StringComparison.Ordinal));

        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        // Last in, first out: the profile by its receipt, then HV-3's revert, then HV-2's release, then the actor.
        Assert.Equal(new[]
        {
            "alpha 200 0", "customize+ delete", "customize+ owner", "glamourer revert 200", "glamourer canunlock 200", "penumbra delete",
            "draw off 200", "delete 200",
        }, scene.Calls.Skip(from).ToArray());
        Assert.Empty(customizePlus.Profiles);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "customize+ cleanup: removed by receipt ", StringComparison.Ordinal) &&
            line.EndsWith(" (Success).", StringComparison.Ordinal));
        Assert.Contains(Prefix + "customize+: owner's normal profiles unchanged (temporary profiles cannot be read).", lines);
        Assert.Equal(Prefix + "status: None, stop none, residue 0, temporary collection none, collection residue 0, " +
            "glamourer Reverted, reverted states 1, cleanup failures 0, customize+ Removed, removed profiles 1, cleanup failures 0.", baseline.Status());
    }

    [Fact]
    public void AParentNamingThePlayerIsClearedSoTheProfileNeverReachesThePlayer()
    {
        // Between the copy from the player and the self-copy, Customize+'s entry names the player (index 0).
        customizePlus.Parents[200] = 0;
        Applied();
        Assert.Contains("customize+ setparent 200 -1", scene.Calls);
        Assert.Contains(Prefix + "customize+: Customize+ 2.2.1.3, API 6.4, valid; parent 0, now -1.", lines);
        var profile = Assert.Single(customizePlus.Profiles);
        Assert.Equal(scene.At(200).Name, profile.Value.Name);
        Assert.DoesNotContain(customizePlus.Profiles, entry => entry.Value.Name == FakeHumanActorScene.PlayerName);
    }

    [Fact]
    public void AParentAlreadyMinusOneIsNotWritten()
    {
        customizePlus.Parents[200] = -1;
        Applied();
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("customize+ setparent", StringComparison.Ordinal));
        Assert.Contains(Prefix + "customize+: Customize+ 2.2.1.3, API 6.4, valid; parent -1.", lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AParentThatCannotBeClearedStopsBeforeAnyCollectionOrGlamourerCall(bool refused)
    {
        if (refused)
            customizePlus.SetParentAnswer = HumanActorCustomizePlusStage.InvalidArgument;
        else
            customizePlus.ParentWriteIgnored = true;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 2);
        Assert.StartsWith("customize+: the actor's Customize+ parent (200) could not be cleared", baseline.Stop, StringComparison.Ordinal);
        Assert.Empty(penumbra.Created);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("glamourer", StringComparison.Ordinal));
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);
        Assert.False(scene.Exists(200));
        Assert.Equal(0, Stage.CleanupFailures);
    }

    [Theory]
    [InlineData("version 2.2.1.2", "Customize+ 2.2.1.2 is not 2.2.1.3")]
    [InlineData("version none", "Customize+ not loaded is not 2.2.1.3")]
    [InlineData("api 6.5", "API 6.5 is not 6.4")]
    [InlineData("api 7.0", "API 7.0 is not 6.4")]
    [InlineData("api none", "API unanswered is not 6.4")]
    [InlineData("invalid", "Customize+ does not report itself valid")]
    public void AnUncheckedBuildRefusesBeforeAnyParentWriteCollectionOrGlamourerCall(string change, string why)
    {
        switch (change)
        {
            case "version 2.2.1.2":
                customizePlus.Version = "2.2.1.2";
                break;
            case "version none":
                customizePlus.Version = null;
                break;
            case "api 6.5":
                customizePlus.Api = (6, 5);
                break;
            case "api 7.0":
                customizePlus.Api = (7, 0);
                break;
            case "api none":
                customizePlus.Api = null;
                break;
            default:
                customizePlus.Valid = false;
                break;
        }
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 2);
        Assert.Equal("customize+: " + why, baseline.Stop);
        Assert.Empty(CustomizePlusCalls);
        Assert.Empty(penumbra.Created);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("glamourer", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanCustomizePlusState.None, Stage.State);
    }

    [Theory]
    [InlineData(HumanActorCustomizePlusStage.InvalidCharacter, "InvalidCharacter")]
    [InlineData(HumanActorCustomizePlusStage.CorruptedProfile, "CorruptedProfile")]
    public void AnApplyRefusedBeforeAnyProfileIsNotACleanupFailure(int answer, string name)
    {
        customizePlus.ApplyAnswer = answer;
        var baseline = GlamourerApplied();
        Run(baseline, 1);
        Assert.Equal($"customize+: the apply was refused ({name}); no profile was created", baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.DoesNotContain("customize+ delete", scene.Calls);
        // HV-3's cleanup is unchanged.
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Theory]
    [InlineData("unknown", "the apply answered UnknownError; a profile may exist; CLEANUP UNCERTAIN")]
    [InlineData("code 99", "the apply answered code 99; a profile may exist; CLEANUP UNCERTAIN")]
    [InlineData("no receipt", "the apply answered Success without a new receipt; a profile may exist; CLEANUP UNCERTAIN")]
    [InlineData("empty receipt", "the apply answered Success without a new receipt; a profile may exist; CLEANUP UNCERTAIN")]
    [InlineData("throws", "the apply did not answer (InvalidOperationException); a profile may exist; CLEANUP UNCERTAIN")]
    public void AnAmbiguousApplyStopsAndIsReportedUncertainWithNoDeletionByIndex(string fault, string why)
    {
        switch (fault)
        {
            case "unknown":
                customizePlus.ApplyAnswer = HumanActorCustomizePlusStage.UnknownError;
                break;
            case "code 99":
                customizePlus.ApplyAnswer = 99;
                break;
            case "no receipt":
                customizePlus.ReceiptOverride = () => null;
                break;
            case "empty receipt":
                customizePlus.ReceiptOverride = () => Guid.Empty;
                break;
            default:
                customizePlus.ApplyThrows = true;
                break;
        }
        var baseline = GlamourerApplied();
        var from = scene.Calls.Count;
        Run(baseline, 1);
        Assert.Equal("customize+: " + why, baseline.Stop);
        // Owner decision D4: no deletion of any kind; the profile, if any, is left and reported.
        Assert.DoesNotContain(scene.Calls.Skip(from), call => call.StartsWith("customize+ delete", StringComparison.Ordinal));
        Assert.Equal(HumanCustomizePlusState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Contains(Prefix + "customize+ residue: an applied or uncertain temporary profile for this spawn's synthetic name may remain in " +
            "Customize+'s memory until Customize+ drops it or unloads; CLEANUP UNCERTAIN.", lines);
        Assert.Contains(Prefix + "customize+ cleanup failure: the apply was ambiguous and RSC holds no receipt; no deletion by object index.", lines);
        // HV-3 and HV-2 still clean up, and the actor is deleted.
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
        // A later spawn is refused: the stop latched.
        Assert.False(baseline.Spawn());
    }

    [Fact]
    public void AReusedReceiptIsAmbiguous()
    {
        var baseline = Applied();
        var first = Assert.Single(customizePlus.Profiles).Key;
        Assert.True(baseline.Remove());
        customizePlus.ReceiptOverride = () => first;
        // A fresh baseline over the same stage: a second spawn whose receipt repeats the first.
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 2);
        Assert.Equal("customize+: the apply answered Success without a new receipt; a profile may exist; CLEANUP UNCERTAIN", baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Left, Stage.State);
    }

    [Theory]
    [InlineData("parent", "the actor's Customize+ parent does not read -1")]
    [InlineData("world 0", "the actor's home world reads 0, which is not a real world")]
    [InlineData("world any", "the actor's home world reads 65535, which is not a real world")]
    [InlineData("world other", "the actor's home world is not the selected world")]
    [InlineData("player world", "the actor's home world is the player's")]
    [InlineData("penumbra parent", "the actor's Penumbra parent does not read -1")]
    [InlineData("second carrier", "another object carries the actor's name")]
    public void AFailedCheckBeforeTheApplyRefusesItWithNoProfileCall(string change, string why)
    {
        var baseline = GlamourerApplied();
        switch (change)
        {
            case "parent":
                customizePlus.Parents[200] = 0;
                break;
            case "world 0":
                scene.At(200).World = 0;
                break;
            case "world any":
                scene.At(200).World = ushort.MaxValue;
                break;
            case "world other":
                scene.At(200).World = HumanActorW3.WorldId + 1;
                break;
            case "player world":
                scene.Player.World = HumanActorW3.WorldId;
                break;
            case "penumbra parent":
                penumbra.Parents[200] = 0;
                break;
            default:
                scene.Add(201, -1, kind: 1).Name = scene.At(200).Name!.ToLowerInvariant();
                break;
        }
        Run(baseline, 1);
        Assert.Equal("customize+: " + why, baseline.Stop);
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);
        Assert.DoesNotContain("customize+ owner", scene.Calls);
        Assert.Equal(HumanCustomizePlusState.None, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void APluginChangeBeforeTheApplyStopsWithNoProfileCall()
    {
        var baseline = GlamourerApplied();
        customizePlus.Generation++;
        Run(baseline, 1);
        Assert.Equal("customize+: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);
        Assert.Equal(HumanCustomizePlusState.None, Stage.State);
    }

    [Fact]
    public void AProviderChangeWithCustomizePlusStillLoadedRegatesAndDeletesByReceiptWhileHv3sRuleLeavesItsState()
    {
        // The fault path (D3): the generation moved. Customize+ is gated again and its profile deleted by receipt;
        // HV-3 keeps its rule and makes no revert, so its state is left and reported.
        var baseline = Applied();
        customizePlus.Generation++;
        glamourer.Generation++;
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.Equal(new[] { "alpha 200 0", "customize+ delete", "customize+ owner", "penumbra delete", "draw off 200", "delete 200" },
            scene.Calls.Skip(from).ToArray());
        Assert.Empty(customizePlus.Profiles);
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.Contains(Prefix + "customize+ cleanup: a plugin loaded or unloaded since the gate; Customize+ gated again.", lines);
        Assert.Equal("glamourer cleanup: revert not called: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.Equal(HumanGlamourerState.Left, glamourerStage!.State);
        Assert.Equal(1, glamourerStage.CleanupFailures);
    }

    [Fact]
    public void AProviderChangeThatUnloadedCustomizePlusMakesNoCallAndReportsTheProfileEndedWithIt()
    {
        var baseline = Applied();
        customizePlus.Unload();
        glamourer.Generation++;
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.DoesNotContain(scene.Calls.Skip(from), call => call.StartsWith("customize+", StringComparison.Ordinal));
        Assert.Equal(HumanCustomizePlusState.Gone, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(lines, line => line.StartsWith(Prefix + "customize+ cleanup: Customize+ not loaded; its in-memory temporary profile (receipt ",
            StringComparison.Ordinal) && line.EndsWith(") ended with it; no call made.", StringComparison.Ordinal));
        // HV-3's rule is unchanged: its state is left and reported.
        Assert.Equal("glamourer cleanup: revert not called: a plugin loaded or unloaded since the gate", baseline.Stop);
        Assert.Equal(HumanGlamourerState.Left, glamourerStage!.State);
    }

    [Fact]
    public void AnUnreadablePluginListAfterAProviderChangeIsNeverTakenAsUnloaded()
    {
        var baseline = Applied();
        customizePlus.Generation++;
        customizePlus.PluginListThrows = true;
        Assert.True(baseline.Remove());
        Assert.DoesNotContain("customize+ delete", scene.Calls);
        Assert.Equal(HumanCustomizePlusState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Equal("customize+ cleanup: the plugin list could not be read after a plugin change (InvalidOperationException)", baseline.Stop);
        Assert.DoesNotContain(lines, line => line.Contains("ended with it", StringComparison.Ordinal));
    }

    [Fact]
    public void AProviderChangeToAnotherCustomizePlusBuildLeavesTheProfileWithNoCall()
    {
        var baseline = Applied();
        customizePlus.Generation++;
        customizePlus.Version = "2.2.1.4";
        Assert.True(baseline.Remove());
        Assert.DoesNotContain("customize+ delete", scene.Calls);
        Assert.Equal(HumanCustomizePlusState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Equal("customize+ cleanup: no deletion after a plugin change: Customize+ 2.2.1.4 is not 2.2.1.3", baseline.Stop);
    }

    [Fact]
    public void AReceiptAlreadyGoneIsNotAFailureAndNothingElseIsTouched()
    {
        var baseline = Applied();
        // Another plugin replaced RSC's profile on the actor: the receipt no longer exists, and the replacement is not RSC's.
        var foreign = customizePlus.Replace(200);
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        Assert.Equal(new[] { "customize+ delete", "customize+ owner" }, scene.Calls.Skip(from).Where(call => call.StartsWith("customize+", StringComparison.Ordinal)));
        Assert.Equal(HumanCustomizePlusState.Gone, Stage.State);
        Assert.Equal(0, Stage.CleanupFailures);
        Assert.Contains(foreign, customizePlus.Profiles.Keys);
        Assert.Contains(lines, line => line.EndsWith(" already gone (ProfileNotFound); no other call.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HumanActorCustomizePlusStage.UnknownError, "the deletion answered UnknownError")]
    [InlineData(HumanActorCustomizePlusStage.InvalidArgument, "the deletion answered InvalidArgument")]
    [InlineData(-1, "the deletion did not answer (InvalidOperationException)")]
    public void AFailedDeletionIsLeftAndReportedAndTheCleanupGoesOn(int answer, string why)
    {
        if (answer < 0)
            customizePlus.DeleteThrows = true;
        else
            customizePlus.DeleteAnswer = answer;
        var baseline = Applied();
        Assert.True(baseline.Remove());
        Assert.Equal("customize+ cleanup: " + why, baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Left, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
        Assert.Contains(Prefix + "customize+ residue: an applied or uncertain temporary profile for this spawn's synthetic name may remain in " +
            "Customize+'s memory until Customize+ drops it or unloads; CLEANUP FAILURE.", lines);
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.Empty(penumbra.Live);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void TheDeletionByReceiptIsNotGatedOnTheObject()
    {
        // A second carrier of the name stops HV-3's revert; the receipt names RSC's profile, so it is still deleted.
        var baseline = Applied();
        scene.Add(201, -1, kind: 1).Name = scene.At(200).Name;
        Assert.True(baseline.Remove());
        Assert.Contains("customize+ delete", scene.Calls);
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.Equal("glamourer cleanup: revert not called: another object carries the actor's name", baseline.Stop);
    }

    [Fact]
    public void ALostActorStillHasItsProfileDeletedByReceiptAndGetsNoGlamourerCall()
    {
        var baseline = Applied();
        scene.Replace(200);
        var from = scene.Calls.Count;
        baseline.OnFrameworkUpdate();
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(new[] { "customize+ delete", "customize+ owner", "penumbra delete" }, scene.Calls.Skip(from).ToArray());
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.Equal(HumanGlamourerState.Left, glamourerStage!.State);
        Assert.Equal("Someone Else", scene.At(200).Name);
    }

    [Fact]
    public void UnloadRemovesInTheSameOrder()
    {
        var baseline = Applied();
        var from = scene.Calls.Count;
        baseline.Unload();
        Assert.Equal(new[]
        {
            "alpha 200 0", "customize+ delete", "customize+ owner", "glamourer revert 200", "glamourer canunlock 200", "penumbra delete",
            "draw off 200", "delete 200",
        }, scene.Calls.Skip(from).ToArray());
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.Contains(Prefix + "unload: deleted, slot empty yes, residue 0.", lines);
    }

    [Fact]
    public void AChangeInTheOwnersNormalProfilesIsAFindingAndNothingIsCorrected()
    {
        var baseline = Applied();
        customizePlus.OwnerFingerprint = "active 0:changed; list 2:changed";
        Assert.True(baseline.Remove());
        Assert.Equal("customize+ finding: the owner's normal profiles changed", baseline.Stop);
        Assert.Contains(Prefix + "customize+ finding: the owner's normal profiles changed between the apply and the removal; nothing corrected.", lines);
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
    }

    [Fact]
    public void TheStageRefusesToBeEnteredFromInsideACustomizePlusCall()
    {
        var answers = new List<string?>();
        customizePlus.DuringApply = () =>
        {
            answers.Add(Stage.Detach(200, null));
            answers.Add(Stage.Apply(200, null));
            answers.Add(Stage.Remove());
        };
        var baseline = GlamourerApplied();
        Run(baseline, 1);
        Assert.Equal(new[]
        {
            "customize+: the stage was entered again during a Customize+ call",
            "customize+: the stage was entered again during a Customize+ call",
            "customize+ cleanup: the stage was entered again during a Customize+ call",
        }, answers);
        Assert.Equal(1, customizePlus.Applies);
        Assert.Equal(HumanCustomizePlusState.Applied, Stage.State);
        Assert.Equal(1, Stage.CleanupFailures);
    }

    [Fact]
    public void TheApplyIsMadeOncePerSpawn()
    {
        var baseline = Applied();
        Run(baseline, 20);
        Assert.Equal(1, customizePlus.Applies);
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void ThePayloadIsTheFixedRootScaleWithEveryFieldExplicit()
    {
        using var json = JsonDocument.Parse(HumanActorCustomizePlusStage.PayloadJson);
        var bones = json.RootElement.GetProperty("Bones");
        Assert.Equal(new[] { "Bones" }, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        var root = Assert.Single(bones.EnumerateObject());
        Assert.Equal("n_root", root.Name);
        Assert.Equal(new[]
        {
            "Translation", "Rotation", "Scaling", "ChildScaling", "PropagateTranslation", "PropagateRotation", "PropagateScale", "ChildScaleIndependent",
        }, root.Value.EnumerateObject().Select(property => property.Name).ToArray());
        void Vector(string name, double value)
        {
            var vector = root.Value.GetProperty(name);
            Assert.Equal(new[] { "X", "Y", "Z" }, vector.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.All(vector.EnumerateObject(), axis => Assert.Equal(value, axis.Value.GetDouble()));
        }
        Vector("Translation", 0.0);
        Vector("Rotation", 0.0);
        Vector("Scaling", 1.15);
        Vector("ChildScaling", 1.0);
        foreach (var flag in new[] { "PropagateTranslation", "PropagateRotation", "PropagateScale", "ChildScaleIndependent" })
            Assert.False(root.Value.GetProperty(flag).GetBoolean());
    }

    [Fact]
    public void TheCustomizePlusStageNeedsTheGlamourerStage()
    {
        var penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix);
        Assert.Throws<ArgumentException>(() => new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, null,
            new HumanActorCustomizePlusStage(customizePlus, lines.Add, Prefix), worlds, Prefix));
    }

    [Fact]
    public void TheWorldSourceComesWithTheCustomizePlusStageAndOnlyWithIt()
    {
        var penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix);
        var hv3 = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix);
        Assert.Throws<ArgumentException>(() => new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, hv3,
            new HumanActorCustomizePlusStage(customizePlus, lines.Add, Prefix), null, Prefix));
        Assert.Throws<ArgumentException>(() => new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, hv3, null, worlds, Prefix));
    }

    [Fact]
    public void WithoutTheCustomizePlusStageHv3IsUnchanged()
    {
        var hv3 = new HumanActorGlamourerStage(glamourer, lines.Add, Prefix) { KeySource = () => Key };
        var penumbraStage = new HumanActorPenumbraStage(penumbra, lines.Add, Prefix)
        {
            Contained = FakeHumanActorPenumbra.Contained,
            ModFolderReadable = folder => folder == FakeHumanActorPenumbra.ModRoot,
            ReleaseByDeletion = true,
        };
        var baseline = new HumanActorBaseline(scene, () => null, lines.Add, penumbraStage, hv3, Prefix);
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible + 5);
        // r1c: HV-3 reads no World row and writes no home world.
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("world ", StringComparison.Ordinal));
        Assert.Equal(scene.CreatedWorld, scene.At(200).World);
        Assert.Empty(worlds.Reads);
        Assert.True(baseline.Remove());
        Assert.Empty(CustomizePlusCalls);
        Assert.Null(baseline.Stop);
        Assert.DoesNotContain("customize+", baseline.Status(), StringComparison.Ordinal);
    }

    // r1c: the home world, written once on RSC's own proven actor, after the copies and before
    // the placement, the draw and every provider call; W-3 checked before anything is created.

    [Theory]
    [InlineData(ushort.MaxValue)]
    [InlineData((ushort)0)]
    public void TheHomeWorldIsWrittenAloneAfterTheCopiesAndBeforeThePlacementTheDrawAndEveryProviderCall(ushort created)
    {
        scene.CreatedWorld = created;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        // English first, then the client's language; both read before the create.
        Assert.Equal(new ushort[] { HumanActorW3.WorldId, HumanActorW3.WorldId }, worlds.Reads.ToArray());
        Assert.True(IndexOf("copy 200<-200") < IndexOf("world 200 412"));
        Assert.True(IndexOf("world 200 412") < IndexOf("place 200"));
        Assert.Single(scene.Calls, call => call.StartsWith("world ", StringComparison.Ordinal));
        Assert.DoesNotContain(scene.Calls.Take(IndexOf("world 200 412")), call => call.StartsWith("penumbra", StringComparison.Ordinal) ||
            call.StartsWith("glamourer", StringComparison.Ordinal) || call.StartsWith("customize+", StringComparison.Ordinal) ||
            call.StartsWith("draw", StringComparison.Ordinal));
        Assert.Equal(HumanActorW3.WorldId, scene.At(200).World);
        // The player is never written.
        Assert.Equal(World, scene.Player.World);
        Assert.Contains(Prefix + $"home world: after the copies {created}, written 412 (W-3), read back 412.", lines);

        // The rest of HV-4 runs under W-3: HV-3's state, then the profile, then the removal.
        Run(baseline, ToVisible + 2);
        Assert.Null(baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Applied, Stage.State);
        Assert.True(baseline.Remove());
        Assert.Contains("glamourer revert 200", scene.Calls);
        Assert.Null(baseline.Stop);
        Assert.Single(scene.Calls, call => call.StartsWith("world ", StringComparison.Ordinal));
    }

    [Fact]
    public void AWorldEqualToThePlayersIsNeverLoggedAsANumber()
    {
        scene.CreatedWorld = World;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Assert.Contains(Prefix + "home world: after the copies the player's world, written 412 (W-3), read back 412.", lines);
        Assert.DoesNotContain(lines, line => line.Contains(World.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherRealWorldLeftByTheCopiesIsNeverLoggedAsANumber()
    {
        scene.CreatedWorld = 21;
        var baseline = Baseline();
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Assert.Contains(Prefix + "home world: after the copies another world, written 412 (W-3), read back 412.", lines);
    }

    [Fact]
    public void AHomeWorldThatDoesNotReadBackStopsBeforeThePlacementTheDrawAndEveryProviderCall()
    {
        scene.WorldWriteIgnored = true;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.Equal("home world: the home world did not read back", baseline.Stop);
        Assert.Contains("world 200 412", scene.Calls);
        Assert.DoesNotContain("place 200", scene.Calls);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("draw on", StringComparison.Ordinal) ||
            call.StartsWith("penumbra", StringComparison.Ordinal) || call.StartsWith("glamourer", StringComparison.Ordinal) ||
            call.StartsWith("customize+", StringComparison.Ordinal));
        // RSC's own proven actor is deleted; nothing is retried.
        Assert.False(scene.Exists(200));
        Assert.Equal(0, baseline.Residue);
        Assert.False(baseline.Spawn());
    }

    [Fact]
    public void ASlotLostDuringTheHomeWorldWriteIsNotTouchedAgain()
    {
        scene.DuringWorldWrite = () => scene.Replace(200);
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.StartsWith("ownership: after the home world: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(1, baseline.Residue);
        var stranger = scene.At(200);
        Assert.Equal("Someone Else", stranger.Name);
        Assert.Equal(0, stranger.World);
        Assert.Equal(1f, stranger.Alpha);
        Assert.DoesNotContain("place 200", scene.Calls);
        Assert.DoesNotContain("delete 200", scene.Calls);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("draw", StringComparison.Ordinal) ||
            call.StartsWith("glamourer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("english missing", "the selected World row could not be read")]
    [InlineData("english row", "the World row read is not the selected row")]
    [InlineData("internal name", "the selected World row's internal name changed")]
    [InlineData("name", "the selected World row's name changed")]
    [InlineData("data centre 12", "the selected World row's data centre changed")]
    [InlineData("user type", "the selected World row's user type changed")]
    [InlineData("region", "the selected World row's region changed")]
    [InlineData("public", "the selected World row is public")]
    [InlineData("client missing", "the selected World row could not be read in the client's language")]
    [InlineData("client row", "the World row read in the client's language is not the selected row")]
    [InlineData("client lower case", "the selected World row's name does not start with A-Z in the client's language")]
    [InlineData("client macro byte", "the selected World row's name does not start with A-Z in the client's language")]
    [InlineData("client empty", "the selected World row's name does not start with A-Z in the client's language")]
    [InlineData("client name", "the selected World row's name differs in the client's language")]
    [InlineData("client data centre 0", "the selected World row has no data centre in the client's language")]
    [InlineData("client data centre 12", "the selected World row's data centre differs in the client's language")]
    [InlineData("client public", "the selected World row is public in the client's language")]
    [InlineData("player home w3", "the player's home world is the selected World row")]
    [InlineData("player home 0", "the player's home world cannot be read")]
    [InlineData("player home any", "the player's home world cannot be read")]
    [InlineData("current w3", "the player's current world is the selected World row")]
    [InlineData("current none", "the player's current world cannot be read")]
    [InlineData("current 0", "the player's current world cannot be read")]
    [InlineData("throws", "the World row or the player's worlds could not be read (InvalidOperationException)")]
    public void AWorldCheckThatFailsRefusesBeforeAnythingIsCreated(string change, string why)
    {
        var row = FakeHumanActorWorldSource.W3Row;
        switch (change)
        {
            case "english missing": worlds.English = null; break;
            case "english row": worlds.English = row with { RowId = 413 }; break;
            case "internal name": worlds.English = row with { InternalName = "Innocence" }; break;
            case "name": worlds.English = row with { Name = "Pixie" }; break;
            case "data centre 12": worlds.English = row with { DataCenter = 12 }; break;
            case "user type": worlds.English = row with { UserType = 4 }; break;
            case "region": worlds.English = row with { Region = 2 }; break;
            case "public": worlds.English = row with { IsPublic = true }; break;
            case "client missing": worlds.Client = null; break;
            case "client row": worlds.Client = row with { RowId = 1 }; break;
            case "client lower case": worlds.Client = row with { FirstNameByte = 't' }; break;
            case "client macro byte": worlds.Client = row with { FirstNameByte = 0x02 }; break;
            case "client empty": worlds.Client = row with { FirstNameByte = -1, Name = "" }; break;
            case "client name": worlds.Client = row with { Name = "Tycoon" }; break;
            case "client data centre 0": worlds.Client = row with { DataCenter = 0 }; break;
            case "client data centre 12": worlds.Client = row with { DataCenter = 12 }; break;
            case "client public": worlds.Client = row with { IsPublic = true }; break;
            case "player home w3": scene.Player.World = HumanActorW3.WorldId; break;
            case "player home 0": scene.Player.World = 0; break;
            case "player home any": scene.Player.World = ushort.MaxValue; break;
            case "current w3": worlds.CurrentWorld = HumanActorW3.WorldId; break;
            case "current none": worlds.CurrentWorld = null; break;
            case "current 0": worlds.CurrentWorld = 0; break;
            default: worlds.Throws = true; break;
        }
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.Contains(Prefix + "refused: " + why + ".", lines);
        // Nothing created, written or called; a refusal does not latch.
        Assert.DoesNotContain("create", scene.Calls);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("world ", StringComparison.Ordinal));
        Assert.Empty(CustomizePlusCalls);
        Assert.Null(baseline.Stop);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AWorldChangedBeforeTheGlamourerApplyRefusesItWithNoStateCreated()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, ToVisible);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        scene.At(200).World = HumanActorW3.WorldId + 1;
        Run(baseline, 1);
        Assert.Equal("glamourer: the actor's home world is not the selected world", baseline.Stop);
        Assert.DoesNotContain("glamourer apply 200", scene.Calls);
        Assert.DoesNotContain("customize+ apply 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AWorldChangedBeforeTheRemovalLeavesTheGlamourerStateUnrevertedAndRecorded()
    {
        var baseline = Applied();
        scene.At(200).World = HumanActorW3.WorldId + 1;
        var from = scene.Calls.Count;
        Assert.True(baseline.Remove());
        // The profile still goes by its receipt; the revert is not called under another world; the actor is deleted.
        Assert.Contains("customize+ delete", scene.Calls.Skip(from));
        Assert.DoesNotContain("glamourer revert 200", scene.Calls.Skip(from));
        Assert.Contains("penumbra delete", scene.Calls.Skip(from));
        Assert.Equal("glamourer cleanup: revert not called: the actor's home world is not the selected world", baseline.Stop);
        Assert.Equal(HumanCustomizePlusState.Removed, Stage.State);
        Assert.False(scene.Exists(200));
    }


    [Fact]
    public void ThePortHasNoDeletionByIndexAndNoPersistentProfileCall()
    {
        var members = typeof(IHumanActorCustomizePlus).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "DeleteTemporaryProfile", "GetParent", "OwnerProfiles", "SetParent", "SetTemporaryProfile", "get_ApiVersion", "get_Generation",
            "get_IsValid", "get_PluginVersion", "ApiVersion", "Generation", "IsValid", "PluginVersion",
        }.Order(StringComparer.Ordinal), members);
        var delete = typeof(IHumanActorCustomizePlus).GetMethod(nameof(IHumanActorCustomizePlus.DeleteTemporaryProfile))!;
        Assert.Equal(typeof(Guid), Assert.Single(delete.GetParameters()).ParameterType);
    }
}

/// <summary>
/// A synthetic World source for HV-4 r1c: W-3's row as F11 re-read it on
/// 2026-11-11 (game 2026.11.03), in English and in the client's language, and
/// a current world other than W-3. Every row read is recorded.
/// </summary>
public sealed class FakeHumanActorWorldSource : IHumanActorWorldSource
{
    public static readonly HumanActorWorldRow W3Row = new(412, "Titania", "Titania", 'T', 6, 5, 1, false);

    public HumanActorWorldRow? English { get; set; } = W3Row;
    public HumanActorWorldRow? Client { get; set; } = W3Row;
    public ushort? CurrentWorld { get; set; } = 21;
    public bool Throws { get; set; }
    public List<ushort> Reads { get; } = [];

    public HumanActorWorldRow? EnglishRow(ushort rowId)
    {
        Reads.Add(rowId);
        return Throws ? throw new InvalidOperationException() : English;
    }

    public HumanActorWorldRow? ClientRow(ushort rowId)
    {
        Reads.Add(rowId);
        return Client;
    }

    public ushort? PlayerCurrentWorld() => CurrentWorld;
}

/// <summary>
/// A synthetic Customize+ for HV-4: its own copy-parent table, and temporary
/// profiles keyed to the identity the object at an index resolves to (its
/// parent's when the entry names a live object), with replacement on the same
/// identity, as in Customize+ 2.2.1.3. Every call is logged into the scene's
/// list, in order.
/// </summary>
public sealed class FakeHumanActorCustomizePlus : IHumanActorCustomizePlus
{
    private readonly FakeHumanActorScene scene;

    internal FakeHumanActorCustomizePlus(FakeHumanActorScene scene) => this.scene = scene;

    public string? Version { get; set; } = "2.2.1.3";
    public (int Major, int Minor)? Api { get; set; } = (6, 4);
    public bool Valid { get; set; } = true;
    public long Generation { get; set; } = 41;
    public Dictionary<int, int> Parents { get; } = [];
    public bool ParentWriteIgnored { get; set; }
    public int? SetParentAnswer { get; set; }
    public Dictionary<Guid, (string Name, string Json)> Profiles { get; } = [];
    public int? ApplyAnswer { get; set; }
    public bool ApplyThrows { get; set; }
    public Func<Guid?>? ReceiptOverride { get; set; }
    public Action? DuringApply { get; set; }
    public int? DeleteAnswer { get; set; }
    public bool DeleteThrows { get; set; }
    public string OwnerFingerprint { get; set; } = "active 3:none; list 1:abc";
    public int Applies { get; private set; }

    public bool PluginListThrows { get; set; }
    public string? PluginVersion => PluginListThrows ? throw new InvalidOperationException() : Version;
    public (int Major, int Minor)? ApiVersion => Api;
    public bool IsValid => Valid;

    public int ParentOf(int index) => Parents.TryGetValue(index, out var parent) ? parent : -1;

    /// <summary>Customize+ unloaded: its in-memory profiles and its table end with it.</summary>
    public void Unload()
    {
        Version = null;
        Api = null;
        Profiles.Clear();
        Parents.Clear();
        Generation++;
    }

    /// <summary>Another plugin sets its own temporary profile on the actor: RSC's is replaced (deleted), and the new receipt is returned.</summary>
    public Guid Replace(int objectIndex)
    {
        var identity = Identity(objectIndex)!;
        foreach (var entry in Profiles.Where(entry => entry.Value.Name == identity).ToArray())
            Profiles.Remove(entry.Key);
        var foreign = Guid.NewGuid();
        Profiles[foreign] = (identity, "{}");
        return foreign;
    }

    public int GetParent(int objectIndex)
    {
        scene.Calls.Add($"customize+ getparent {objectIndex}");
        return ParentOf(objectIndex);
    }

    public int SetParent(int objectIndex, int parent)
    {
        scene.Calls.Add($"customize+ setparent {objectIndex} {parent}");
        if (SetParentAnswer is { } answer)
            return answer;
        if (!ParentWriteIgnored)
            Parents[objectIndex] = parent;
        return HumanActorCustomizePlusStage.Success;
    }

    public (int Code, Guid? Profile) SetTemporaryProfile(ushort objectIndex, string profileJson)
    {
        scene.Calls.Add($"customize+ apply {objectIndex}");
        Applies++;
        if (ApplyAnswer is HumanActorCustomizePlusStage.InvalidCharacter or HumanActorCustomizePlusStage.CorruptedProfile)
            return (ApplyAnswer.Value, null);
        if (Identity(objectIndex) is not { } identity)
            return (HumanActorCustomizePlusStage.InvalidCharacter, null);
        // Inserted (replacing any temporary profile for the same identity) before the events run.
        foreach (var entry in Profiles.Where(entry => entry.Value.Name == identity).ToArray())
            Profiles.Remove(entry.Key);
        var receipt = ReceiptOverride is { } source ? source() : Guid.NewGuid();
        Profiles[receipt ?? Guid.NewGuid()] = (identity, profileJson);
        DuringApply?.Invoke();
        if (ApplyThrows)
            throw new InvalidOperationException();
        if (ApplyAnswer is { } answer)
            return (answer, null);
        return (HumanActorCustomizePlusStage.Success, receipt);
    }

    public int DeleteTemporaryProfile(Guid profile)
    {
        scene.Calls.Add("customize+ delete");
        if (DeleteThrows)
            throw new InvalidOperationException();
        if (DeleteAnswer is { } answer)
            return answer;
        return Profiles.Remove(profile) ? HumanActorCustomizePlusStage.Success : HumanActorCustomizePlusStage.ProfileNotFound;
    }

    public string OwnerProfiles()
    {
        scene.Calls.Add("customize+ owner");
        return OwnerFingerprint;
    }

    /// <summary>The name Customize+ would key a profile to: the parent's when the entry names a live object (the copy-parent rule).</summary>
    private string? Identity(int objectIndex)
    {
        if (!scene.Exists(objectIndex))
            return null;
        var parent = ParentOf(objectIndex);
        var resolved = parent >= 0 && scene.Exists(parent) ? scene.At(parent) : scene.At(objectIndex);
        return resolved.Name;
    }
}
#endif
