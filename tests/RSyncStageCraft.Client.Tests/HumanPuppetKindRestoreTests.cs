#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// R-8a (D4 as amended): the kind and sub-kind recorded at creation are
/// written back once after the copies, only on a BattleNpc request whose
/// creation produced BattleNpc and only when the copies changed them, after
/// checks 1–5 and with both bytes read back before any hiding, map or
/// identity write. Synthetic identities and fakes only: no game, no IPC.
/// </summary>
public sealed class HumanPuppetKindRestoreTests
{
    private static readonly HumanPuppetWorld World = new(9000, F11Verified: true);
    private static readonly HumanPuppetKindPair Created = new(2, FakeHumanScene.CreatedSubKind);
    private static readonly HumanPuppetKindPair PlayerPair = new(1, FakeHumanScene.PlayerSubKind);

    private readonly FakeHumanScene scene = new();

    private HumanPuppetHost Ready(HumanPuppetKind kind = HumanPuppetKind.BattleNpc)
    {
        var host = new HumanPuppetHost(scene, scene, scene, scene, World, kind, () => 0, () => 0, () => null, 1);
        Assert.True(host.CheckCustomizePlus().Done);
        host.OnFrameworkUpdate(1);
        return host;
    }

    private int IndexOfCall(string call)
    {
        var index = scene.Calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private static bool IsLaterWrite(string call) =>
        call.StartsWith("hide prepare", StringComparison.Ordinal) || call.StartsWith("penumbra set", StringComparison.Ordinal) ||
        call.StartsWith("customize set", StringComparison.Ordinal) || call.StartsWith("write identity", StringComparison.Ordinal);

    private static bool IsRestore(string call) => call.StartsWith("restore kind", StringComparison.Ordinal);

    // Case 1.
    [Fact]
    public void CopiesThatLeaveThePlayerKindAreRestoredOnceAfterTheSelfCopyAndBeforeHiding()
    {
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        // F0's records show what the copies left: the player's kind, and no
        // human model while no draw object exists; the customize array is filled.
        Assert.All(result.Copies, copy =>
        {
            Assert.Equal(1, copy.RawKind);
            Assert.False(copy.HasHumanModel);
            Assert.True(copy.CustomizeFilled);
        });
        var restore = Assert.IsType<HumanPuppetKindRestore>(result.KindRestore);
        Assert.Equal(Created, restore.Created);
        Assert.Equal(PlayerPair, restore.AfterCopies);
        Assert.Equal(Created, restore.ReadBack);
        Assert.True(restore.Restored);
        // Exactly one write, of the created pair, on the puppet.
        var write = Assert.Single(scene.Calls, IsRestore);
        Assert.Equal($"restore kind 200 2/{FakeHumanScene.CreatedSubKind}", write);
        var at = scene.Calls.IndexOf(write);
        Assert.True(IndexOfCall("copy 200->200") < at);
        Assert.True(at < IndexOfCall("hide prepare 200"));
        Assert.True(at < IndexOfCall("penumbra set 200 -1"));
        Assert.True(at < IndexOfCall("customize set 200 -1"));
        Assert.True(at < IndexOfCall("write identity kind=False clear=False"));
        var puppet = scene.At(200);
        Assert.Equal(2, puppet.Kind);
        Assert.Equal(FakeHumanScene.CreatedSubKind, puppet.SubKind);
        // The player is only read.
        Assert.Equal(1, scene.Player.Kind);
        Assert.Equal(FakeHumanScene.PlayerSubKind, scene.Player.SubKind);
        host.OnFrameworkUpdate(2);
        Assert.Null(host.Stop);
    }

    // Case 2.
    [Fact]
    public void AnOwnershipMismatchBeforeTheRestoreWritesNothingAndLeavesTheObjectUntouched()
    {
        // The second kind read is the one after the copies; the slot changes hands straight after it.
        scene.OnReadKind = count =>
        {
            if (count == 2)
                scene.Replace(200);
        };
        var host = Ready();
        var result = host.Spawn();
        Assert.False(result.Spawned);
        Assert.Equal(HumanPuppetStopRule.S6Residue, result.Stop!.Rule);
        Assert.StartsWith("before the kind restore: ", result.Stop.Detail, StringComparison.Ordinal);
        Assert.Null(result.KindRestore);
        Assert.DoesNotContain(scene.Calls, IsRestore);
        Assert.DoesNotContain(scene.Calls, IsLaterWrite);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("delete", StringComparison.Ordinal) || call == "draw off 200");
        // The object now at the slot is not RSC's: never written, hidden or deleted.
        Assert.True(scene.Exists(200));
        Assert.Equal("Someone Else", scene.At(200).Name);
        Assert.Equal(1f, scene.At(200).Alpha);
        Assert.Contains(host.Residue, item => item.What.Contains("no longer provably", StringComparison.Ordinal));
    }

    // Case 3 (replaces the r5 tests that stopped on the copies' kind).
    [Fact]
    public void ARestoreThatDoesNotReadBackStopsAndDeletesBeforeAnyLaterWrite()
    {
        scene.RestoreKindFails = true;
        var host = Ready();
        var result = host.Spawn();
        Assert.False(result.Spawned);
        Assert.Equal(HumanPuppetStopRule.S5Identity, result.Stop!.Rule);
        Assert.Equal("copy", result.Stop.Step);
        Assert.Single(scene.Calls, IsRestore);
        var restore = Assert.IsType<HumanPuppetKindRestore>(result.KindRestore);
        Assert.Equal(PlayerPair, restore.ReadBack);
        Assert.False(restore.Restored);
        // Stopped straight after the restore: no hiding, map or identity write.
        Assert.DoesNotContain(scene.Calls, IsLaterWrite);
        // Still provably RSC's, so hidden and deleted.
        Assert.True(IndexOfCall("draw off 200") < IndexOfCall("delete 200"));
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanPuppetPhase.Deleted, Assert.Single(host.Snapshot()).Phase);
    }

    // Case 4 (replaces the r5 tests that stopped on the copies' kind).
    [Fact]
    public void ACreationThatDidNotProduceBattleNpcGetsNoKindWriteAndStopsAtTheCopies()
    {
        scene.CreatedKind = 3;
        var host = Ready();
        var result = host.Spawn();
        Assert.False(result.Spawned);
        Assert.Equal(HumanPuppetStopRule.S5Identity, result.Stop!.Rule);
        Assert.Equal("copy", result.Stop.Step);
        Assert.Null(result.KindRestore);
        Assert.DoesNotContain(scene.Calls, IsRestore);
        Assert.DoesNotContain(scene.Calls, IsLaterWrite);
        Assert.Contains("delete 200", scene.Calls);
        Assert.False(scene.Exists(200));
    }

    // Case 5.
    [Fact]
    public void CopiesThatKeepBattleNpcGetNoKindWrite()
    {
        scene.CopyCopiesKind = false;
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        Assert.Null(result.KindRestore);
        Assert.DoesNotContain(scene.Calls, IsRestore);
        Assert.Equal(2, scene.At(200).Kind);
        Assert.Equal(FakeHumanScene.CreatedSubKind, scene.At(200).SubKind);
    }

    // Review addition: an unreadable pair, at creation (1) or after the copies (2), gets no write; D4 stops.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AnUnreadableKindPairGetsNoWriteAndTheKindCheckStops(int unreadableRead)
    {
        scene.UnreadableKindRead = unreadableRead;
        var host = Ready();
        var result = host.Spawn();
        Assert.False(result.Spawned);
        Assert.Equal(HumanPuppetStopRule.S5Identity, result.Stop!.Rule);
        Assert.Equal("copy", result.Stop.Step);
        Assert.Null(result.KindRestore);
        Assert.DoesNotContain(scene.Calls, IsRestore);
        Assert.DoesNotContain(scene.Calls, IsLaterWrite);
        Assert.False(scene.Exists(200));
    }

    // Review addition: a copy that changes only the sub-kind is restored too (the pair is compared).
    [Fact]
    public void ACopyThatChangesOnlyTheSubKindIsRestored()
    {
        scene.CopyCopiesKind = false;
        scene.CopySetsSubKind = FakeHumanScene.PlayerSubKind;
        var host = Ready();
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        var restore = Assert.IsType<HumanPuppetKindRestore>(result.KindRestore);
        Assert.Equal(new HumanPuppetKindPair(2, FakeHumanScene.PlayerSubKind), restore.AfterCopies);
        Assert.True(restore.Restored);
        Assert.Single(scene.Calls, IsRestore);
        Assert.Equal(FakeHumanScene.CreatedSubKind, scene.At(200).SubKind);
    }

    // Case 6.
    [Fact]
    public void APlayerKindRequestGetsNoKindRestore()
    {
        var host = Ready(HumanPuppetKind.Player);
        var result = host.Spawn();
        Assert.True(result.Spawned, result.Stop?.Detail);
        Assert.Null(result.KindRestore);
        Assert.DoesNotContain(scene.Calls, IsRestore);
        Assert.Contains("write identity kind=True clear=False", scene.Calls);
    }
}
#endif
