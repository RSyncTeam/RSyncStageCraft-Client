#if RSC_DEVELOPMENT
using System.Numerics;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-1: one regular, visible, client-only human: named before the copies and
/// before any draw, initialized by the copy from the player and the
/// self-copy, the player's kind left as the copies leave it (never written),
/// drawn when ready, shown at alpha 1 only with a human draw object, and
/// deleted. Synthetic identities and a fake scene only: no game, no IPC.
/// </summary>
public sealed class HumanActorBaselineTests
{
    private readonly FakeHumanActorScene scene = new();
    private readonly List<string> lines = [];

    private HumanActorBaseline Baseline(Func<string?>? sceneExit = null) => new(scene, sceneExit ?? (() => null), lines.Add);

    private int IndexOf(string call)
    {
        var index = scene.Calls.IndexOf(call);
        Assert.True(index >= 0, $"missing call: {call}");
        return index;
    }

    private static void Run(HumanActorBaseline baseline, int updates)
    {
        for (var i = 0; i < updates; i++)
            baseline.OnFrameworkUpdate();
    }

    [Fact]
    public void TheActorIsNamedCopiedPlacedDrawnAndShownInTheGamesOwnOrder()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn(), string.Join(" | ", lines));
        Assert.Equal(HumanActorPhase.WaitingForReady, baseline.Phase);
        var actor = scene.At(200);
        Assert.True(HumanPuppetName.IsWellFormed(actor.Name));
        // Alpha 0, then the name, before both copies and before any draw.
        Assert.True(IndexOf("alpha 200 0") < IndexOf($"name 200"));
        Assert.True(IndexOf("name 200") < IndexOf("copy 200<-0"));
        Assert.True(IndexOf("copy 200<-0") < IndexOf("copy 200<-200"));
        Assert.True(IndexOf("copy 200<-200") < IndexOf("place 200"));
        Assert.True(IndexOf("place 200") < IndexOf("untargetable 200"));
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("draw on", StringComparison.Ordinal));
        // The kind is the copies' own result: the player's kind, never written.
        Assert.Equal(1, actor.Kind);
        Assert.False(actor.Targetable);
        Assert.Equal(0f, actor.Alpha);

        // The draw waits for readiness and never happens on the spawn's own update.
        scene.ReadyAfter = 1;
        Run(baseline, 1);
        Assert.DoesNotContain("draw on 200", scene.Calls);
        Run(baseline, 2);
        Assert.Contains("draw on 200", scene.Calls);
        Assert.Equal(HumanActorPhase.WaitingForModel, baseline.Phase);
        // Still invisible until the human draw object exists.
        Assert.Equal(0f, actor.Alpha);
        Run(baseline, 1);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.Equal(1f, actor.Alpha);
        Assert.True(IndexOf("draw on 200") < scene.Calls.LastIndexOf("alpha 200 1"));
        Assert.Contains(lines, line => line.Contains("visible: human model after", StringComparison.Ordinal));
        Assert.Null(baseline.Stop);
    }

    [Fact]
    public void ThePlayerIsOnlyRead()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, 5);
        Assert.True(baseline.Remove());
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("copy 0<-", StringComparison.Ordinal) || call.StartsWith("name 0", StringComparison.Ordinal) ||
            call.StartsWith("place 0", StringComparison.Ordinal) || call == "alpha 0 0" || call == "alpha 0 1");
        Assert.Equal(FakeHumanActorScene.PlayerName, scene.Player.Name);
        Assert.True(scene.Player.Targetable);
    }

    [Fact]
    public void TheActorStandsTwoYalmsInFrontOfThePlayerFacingThem()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        var actor = scene.At(200);
        var expected = HumanActorBaseline.InFrontOf(new(FakeHumanActorScene.PlayerPosition, FakeHumanActorScene.PlayerRotation));
        Assert.Equal(expected.Position, actor.Position);
        Assert.Equal(expected.Rotation, actor.Rotation);
        Assert.Equal((double)HumanActorBaseline.Distance, (double)Vector3.Distance(FakeHumanActorScene.PlayerPosition, actor.Position), 3);
        Assert.InRange(expected.Rotation, -MathF.PI, MathF.PI);
    }

    [Fact]
    public void ANameAnObjectAlreadyCarriesIsNeverUsed()
    {
        const string taken = "Rsc Aaaaaaaaaaaaaaa";
        const string fresh = "Rsc Bbbbbbbbbbbbbbb";
        scene.Add(5, -1, kind: 1).Name = "RSC AAAAAAAAAAAAAAA"; // the scan ignores case
        var names = new Queue<string>([taken, fresh]);
        var baseline = Baseline();
        baseline.NameSource = _ => names.Dequeue();
        Assert.True(baseline.Spawn());
        Assert.Equal(fresh, scene.At(200).Name);
    }

    [Fact]
    public void CopiesThatDoNotLeaveThePlayersKindStopAndDeleteWithNoKindWrite()
    {
        scene.CopyCopiesKind = false;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.StartsWith("copy: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Contains("delete 200", scene.Calls);
        Assert.False(scene.Exists(200));
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("place", StringComparison.Ordinal) || call.StartsWith("draw on", StringComparison.Ordinal));
        // Never retried.
        Assert.False(baseline.Spawn());
        Assert.Single(scene.Calls, call => call == "create");
    }

    [Fact]
    public void ANameThatDoesNotReadBackStopsBeforeTheCopies()
    {
        scene.NameWriteFails = true;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.StartsWith("name: ", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain(scene.Calls, call => call.StartsWith("copy", StringComparison.Ordinal));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void CopiesThatOverwriteTheNameStopAndDelete()
    {
        scene.CopyCopiesName = true;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.Equal("copy: the copies changed the name", baseline.Stop);
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void AnActorNeverReadyToDrawIsDeletedAtTheLimit()
    {
        scene.ReadyAfter = int.MaxValue;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, HumanActorBaseline.ReadyLimit + 1);
        Assert.StartsWith("draw: not ready", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("draw on 200", scene.Calls);
        Assert.False(scene.Exists(200));
        Assert.Equal(HumanActorPhase.None, baseline.Phase);
    }

    [Fact]
    public void AnActorWithNoHumanDrawObjectIsNeverShownAndIsDeleted()
    {
        scene.DrawAppears = false;
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, HumanActorBaseline.FirstReadyCheck + HumanActorBaseline.ModelLimit + 2);
        Assert.StartsWith("draw: no human draw object", baseline.Stop, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha 200 1", scene.Calls);
        Assert.True(IndexOf("draw off 200") < IndexOf("delete 200"));
        Assert.False(scene.Exists(200));
    }

    [Fact]
    public void RemoveHidesUndrawsDeletesAndAllowsANewSpawn()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, 4);
        Assert.Equal(HumanActorPhase.Visible, baseline.Phase);
        Assert.True(baseline.Remove());
        var hide = scene.Calls.LastIndexOf("alpha 200 0");
        Assert.True(hide < IndexOf("draw off 200"));
        Assert.True(IndexOf("draw off 200") < IndexOf("delete 200"));
        Assert.False(scene.Exists(200));
        Assert.Equal(0, baseline.LiveCount);
        Assert.Contains(lines, line => line.EndsWith("removed: deleted, slot empty yes.", StringComparison.Ordinal));
        Assert.True(baseline.Spawn());
    }

    [Fact]
    public void AnActorThatChangesHandsIsForgottenAndNeverTouched()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        scene.Replace(200);
        var before = scene.Calls.Count;
        baseline.OnFrameworkUpdate();
        Assert.Equal(1, baseline.Residue);
        Assert.StartsWith("ownership: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(before, scene.Calls.Count);
        Assert.False(baseline.Remove());
        baseline.Unload();
        Assert.Equal(before, scene.Calls.Count);
        Assert.Equal("Someone Else", scene.At(200).Name);
    }

    [Fact]
    public void AnObjectThatWasNotProvablyNewIsNeverWrittenOrDeleted()
    {
        // The allocator reports a slot whose object was there before the call.
        scene.Add(205, 5, kind: 2);
        scene.CreateReturnsIndex = 5;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.StartsWith("creation: ", baseline.Stop, StringComparison.Ordinal);
        Assert.Equal(new[] { "create" }, scene.Calls);
    }

    [Fact]
    public void AnObjectOutsideTheRangeIsDeletedUnwritten()
    {
        scene.NextGlobalIndex = 440;
        var baseline = Baseline();
        Assert.False(baseline.Spawn());
        Assert.Equal(new[] { "create", "delete 440" }, scene.Calls);
    }

    [Fact]
    public void SpawnIsRefusedWhileTheSceneIsNotQuietOrAnActorExists()
    {
        var busy = Baseline(() => "GPose is active");
        Assert.False(busy.Spawn());
        Assert.Empty(scene.Calls);
        Assert.Null(busy.Stop);

        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        var calls = scene.Calls.Count;
        Assert.False(baseline.Spawn());
        Assert.Equal(calls, scene.Calls.Count);
    }

    [Fact]
    public void UnloadDeletesTheActor()
    {
        var baseline = Baseline();
        Assert.True(baseline.Spawn());
        Run(baseline, 4);
        baseline.Unload();
        Assert.False(scene.Exists(200));
        Assert.Contains(lines, line => line.EndsWith("unload: deleted, slot empty yes, residue 0.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBackendHasNoKindWriteAndNoProviderCall()
    {
        var members = typeof(IHumanActorBackend).GetMembers().Select(member => member.Name).ToArray();
        Assert.DoesNotContain(members, name => name.Contains("Kind", StringComparison.Ordinal));
        Assert.DoesNotContain(members, name => name.Contains("Penumbra", StringComparison.Ordinal) ||
            name.Contains("Glamourer", StringComparison.Ordinal) || name.Contains("Customize", StringComparison.Ordinal));
    }
}

/// <summary>A synthetic scene for HV-1: the object table, the COM array and the copy, logged in order.</summary>
internal sealed class FakeHumanActorScene : IHumanActorBackend
{
    public const string PlayerName = "Test Player";
    public static readonly Vector3 PlayerPosition = new(10f, 0f, -4f);
    public const float PlayerRotation = 2.5f;

    private readonly nint[] com = new nint[ActorSlotRange.ComSlotCount];
    private readonly Dictionary<int, Obj> table = [];
    private readonly Dictionary<nint, Obj> byAddress = [];
    private nint nextAddress = 0x1000;

    public FakeHumanActorScene()
    {
        Player = Add(0, -1, kind: 1);
        Player.Name = PlayerName;
        Player.Targetable = true;
        Player.Customize = [1, 0, 1, 0, 1, .. new byte[21]];
        Player.Position = PlayerPosition;
        Player.Rotation = PlayerRotation;
        Player.DrawEnabled = true;
    }

    public List<string> Calls { get; } = [];
    public Obj Player { get; }

    public int NextGlobalIndex { get; set; } = -1;
    /// <summary>The COM index the allocator reports instead of the first free one.</summary>
    public int? CreateReturnsIndex { get; set; }
    public bool CopyCopiesKind { get; set; } = true;
    public bool CopyCopiesName { get; set; }
    public bool NameWriteFails { get; set; }
    public bool DrawAppears { get; set; } = true;
    /// <summary>Ready-to-draw reads before the first true one.</summary>
    public int ReadyAfter { get; set; }

    public bool IsAvailable => true;
    public nint LocalPlayer => table[0].Address;
    public int ObjectTableLength => 449;

    public Obj Add(int globalIndex, int comIndex, int kind)
    {
        var obj = new Obj(nextAddress, globalIndex) { Kind = kind, EntityId = (uint)(0x40000000 + globalIndex) };
        nextAddress += 0x100;
        table[globalIndex] = obj;
        byAddress[obj.Address] = obj;
        if (comIndex >= 0)
            com[comIndex] = obj.Address;
        return obj;
    }

    public Obj At(int globalIndex) => table[globalIndex];

    public bool Exists(int globalIndex) => table.ContainsKey(globalIndex);

    /// <summary>The slot changes hands: another object now sits at the same indices.</summary>
    public void Replace(int globalIndex)
    {
        var old = table[globalIndex];
        var comIndex = Array.IndexOf(com, old.Address);
        byAddress.Remove(old.Address);
        var obj = Add(globalIndex, comIndex, kind: 1);
        obj.Name = "Someone Else";
        obj.Alpha = 1f;
    }

    public nint[] ComEntriesSnapshot() => (nint[])com.Clone();

    public int CreateCharacter()
    {
        Calls.Add("create");
        if (CreateReturnsIndex is { } reported)
            return reported;
        var comIndex = Array.IndexOf(com, 0);
        var globalIndex = NextGlobalIndex >= 0 ? NextGlobalIndex : ActorSlotRange.MinimumGlobalIndex + comIndex;
        Add(globalIndex, comIndex, kind: 2);
        return comIndex;
    }

    public nint ComEntry(int comIndex) => comIndex is >= 0 and < ActorSlotRange.ComSlotCount ? com[comIndex] : 0;

    public nint ComObjectByIndex(int comIndex) => ComEntry(comIndex);

    public int ComIndexOf(nint address) => address == 0 ? -1 : Array.IndexOf(com, address);

    public nint ObjectTableEntry(int globalIndex) => table.TryGetValue(globalIndex, out var obj) ? obj.Address : 0;

    public HumanPuppetScanEntry? ScanEntry(int globalIndex) =>
        table.TryGetValue(globalIndex, out var obj) && !string.IsNullOrEmpty(obj.Name) ? new(globalIndex, obj.Name, 0) : null;

    public HumanPuppetFacts ReadFacts(nint address)
    {
        if (!byAddress.TryGetValue(address, out var obj))
            return new(-1, 0, 0, -1, null, false, 0, 0, 0, false, null, true);
        return new(obj.GlobalIndex, (ulong)obj.EntityId, obj.EntityId, obj.Kind, obj.Name, obj.Name is not null, 0, 0,
            HumanPuppetFacts.NoOwner, obj.DrawEnabled && (DrawAppears || obj == Player), obj.Customize, obj.Targetable);
    }

    public void WriteName(nint actor, string name)
    {
        var obj = byAddress[actor];
        Calls.Add($"name {obj.GlobalIndex}");
        if (!NameWriteFails)
            obj.Name = name;
    }

    public void CopyCharacter(nint actor, nint source)
    {
        var target = byAddress[actor];
        var from = byAddress[source];
        Calls.Add($"copy {target.GlobalIndex}<-{from.GlobalIndex}");
        if (target == from)
            return;
        target.Customize = (byte[]?)from.Customize?.Clone();
        target.Position = from.Position;
        target.Rotation = from.Rotation;
        if (CopyCopiesKind)
            target.Kind = from.Kind;
        if (CopyCopiesName)
            target.Name = from.Name;
    }

    public HumanActorPlacement? ReadPlacement(nint address) =>
        byAddress.TryGetValue(address, out var obj) ? new HumanActorPlacement(obj.Position, obj.Rotation) : null;

    public void Place(nint actor, HumanActorPlacement placement)
    {
        var obj = byAddress[actor];
        Calls.Add($"place {obj.GlobalIndex}");
        obj.Position = placement.Position;
        obj.Rotation = placement.Rotation;
    }

    public void MakeUntargetable(nint actor)
    {
        var obj = byAddress[actor];
        Calls.Add($"untargetable {obj.GlobalIndex}");
        obj.Targetable = false;
    }

    public void SetAlpha(nint actor, float alpha)
    {
        var obj = byAddress[actor];
        Calls.Add($"alpha {obj.GlobalIndex} {alpha}");
        obj.Alpha = alpha;
    }

    public bool IsReadyToDraw(nint actor)
    {
        if (ReadyAfter > 0)
        {
            ReadyAfter--;
            return false;
        }
        return byAddress.ContainsKey(actor);
    }

    public void EnableDraw(nint actor)
    {
        var obj = byAddress[actor];
        Calls.Add($"draw on {obj.GlobalIndex}");
        obj.DrawEnabled = true;
    }

    public void DisableDraw(nint actor)
    {
        var obj = byAddress[actor];
        Calls.Add($"draw off {obj.GlobalIndex}");
        obj.DrawEnabled = false;
    }

    public void DeleteComObject(int comIndex)
    {
        var address = com[comIndex];
        var obj = byAddress[address];
        Calls.Add($"delete {obj.GlobalIndex}");
        com[comIndex] = 0;
        byAddress.Remove(address);
        table.Remove(obj.GlobalIndex);
    }

    internal sealed class Obj(nint address, int globalIndex)
    {
        public nint Address { get; } = address;
        public int GlobalIndex { get; } = globalIndex;
        public int Kind { get; set; }
        public uint EntityId { get; set; }
        public string? Name { get; set; }
        public byte[]? Customize { get; set; }
        public Vector3 Position { get; set; }
        public float Rotation { get; set; }
        public bool Targetable { get; set; } = true;
        public bool DrawEnabled { get; set; }
        public float Alpha { get; set; } = 1f;
    }
}
#endif
