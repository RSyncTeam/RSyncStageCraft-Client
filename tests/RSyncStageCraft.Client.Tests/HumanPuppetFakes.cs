#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Actors.Humans;

namespace RSyncStageCraft.Tests;

/// <summary>
/// A synthetic scene for the Debug human puppet host: an object table, the
/// COM array, both parent maps with their copy and destructor hooks, Penumbra's
/// collections and Glamourer's read-only surface. Every call is logged in
/// order. No game, no IPC; every identity is synthetic.
/// </summary>
internal sealed class FakeHumanScene : IHumanPuppetBackend, IHumanParentMapPort, IHumanPenumbraPort, IHumanGlamourerReadPort
{
    public const ushort PlayerWorld = 7001;
    public const string PlayerName = "Test Player";
    public static readonly Guid DefaultCollection = new("00000000-0000-0000-0000-00000000d0f1");

    private readonly nint[] com = new nint[ActorSlotRange.ComSlotCount];
    private readonly Dictionary<int, Obj> table = [];
    private readonly Dictionary<nint, Obj> byAddress = [];
    private readonly int[] penumbraMap = Enumerable.Repeat(-1, 449).ToArray();
    private readonly int[] customizeMap = Enumerable.Repeat(-1, 449).ToArray();
    private nint nextAddress = 0x1000;

    public FakeHumanScene()
    {
        Player = Add(0, -1, kind: 1);
        Player.Name = PlayerName;
        Player.World = PlayerWorld;
        Player.HasHumanModel = true;
        // Race 1, gender 0, body type 1 (adult), clan 1.
        Player.Customize = [1, 0, 1, 0, 1, .. new byte[21]];
    }

    public List<string> Calls { get; } = [];
    public Obj Player { get; }
    public Dictionary<int, HumanPuppetScanEntry> Injected { get; } = [];
    public Dictionary<HumanCollectionRoleKind, Guid> Roles { get; } = new() { [HumanCollectionRoleKind.Default] = DefaultCollection };
    public Dictionary<int, Guid> Assigned { get; } = [];
    public HashSet<Guid> TemporaryCollections { get; } = [];

    // Behaviour switches.
    public bool Available { get; set; } = true;
    public int NextGlobalIndex { get; set; } = -1;
    public bool CreateReturnsNothingButAllocates { get; set; }
    public bool CopyCopiesKind { get; set; }
    public bool CopyChangesEntityId { get; set; }
    public uint CopiedNameId { get; set; }
    public ulong CopiedOwner { get; set; } = HumanPuppetFacts.NoOwner;
    public bool WriteLeavesNoTerminator { get; set; }
    public bool DeleteFails { get; set; }
    public bool DestructorKeepsMaps { get; set; }
    public bool DrawAppears { get; set; } = true;
    public bool FailPenumbraSet { get; set; }
    public Func<int, int?>? CustomizePlusReadOverride { get; set; }
    public bool LoseCollectionId { get; set; }
    public HumanAssignmentResult AssignResult { get; set; } = HumanAssignmentResult.Success;
    public bool RemoveAssignmentFails { get; set; }
    public bool IndividualSet { get; set; }
    public Guid? EffectiveOverride { get; set; }
    public Action? OnScan { get; set; }

    // IHumanParentMapPort.
    public bool PenumbraAvailable { get; set; } = true;
    public bool CustomizePlusLoaded { get; set; } = true;
    public (int Major, int Minor)? CustomizePlusApiVersion { get; set; } = (6, 4);
    public bool CustomizePlusParentIpcPresent { get; set; } = true;

    // IHumanGlamourerReadPort.
    public (int Major, int Minor)? ApiVersion { get; set; } = (1, 7);

    public bool IsAvailable => Available;
    public nint LocalPlayer => Player.Address;
    public int ObjectTableLength => 449;

    public int ScanCount { get; private set; }

    public sealed class Obj
    {
        public nint Address;
        public int GlobalIndex;
        public int ComIndex;
        public ulong GameObjectId;
        public uint EntityId;
        public int Kind;
        public string? Name;
        public bool Terminated = true;
        public ushort World;
        public uint NameId;
        public ulong Owner = HumanPuppetFacts.NoOwner;
        public bool HasHumanModel;
        public byte[]? Customize;
        public float Alpha = 1f;
        public bool DrawEnabled;
    }

    public Obj Add(int globalIndex, int comIndex, int kind)
    {
        var obj = new Obj
        {
            Address = nextAddress += 0x100,
            GlobalIndex = globalIndex,
            ComIndex = comIndex,
            GameObjectId = 0x4000_0000u + (ulong)globalIndex,
            EntityId = 0xE000_0000u,
            Kind = kind,
        };
        table[globalIndex] = obj;
        byAddress[obj.Address] = obj;
        if (comIndex >= 0)
            com[comIndex] = obj.Address;
        return obj;
    }

    public Obj At(int globalIndex) => table[globalIndex];

    public bool Exists(int globalIndex) => table.ContainsKey(globalIndex);

    public int PenumbraEntry(int globalIndex) => penumbraMap[globalIndex];

    public int CustomizePlusEntry(int globalIndex) => customizeMap[globalIndex];

    public void SetEntries(int globalIndex, int parent)
    {
        penumbraMap[globalIndex] = parent;
        customizeMap[globalIndex] = parent;
    }

    /// <summary>Replaces the object at a slot with another one (ownership lost).</summary>
    public void Replace(int globalIndex)
    {
        var old = table[globalIndex];
        byAddress.Remove(old.Address);
        var replacement = Add(globalIndex, old.ComIndex, kind: 2);
        replacement.Name = "Someone Else";
    }

    // IHumanPuppetBackend.
    public nint[] ComEntriesSnapshot()
    {
        Calls.Add("snapshot");
        return (nint[])com.Clone();
    }

    public int CreateCharacter()
    {
        Calls.Add("create");
        var slot = Array.IndexOf(com, (nint)0);
        var global = NextGlobalIndex >= 0 ? NextGlobalIndex : ActorSlotRange.MinimumGlobalIndex + slot;
        var obj = Add(global, slot, kind: 2);
        obj.Alpha = 1f;
        return CreateReturnsNothingButAllocates ? -1 : slot;
    }

    public nint ComEntry(int comIndex) => com[comIndex];

    public nint ComObjectByIndex(int comIndex) => ActorSlotRange.IsComIndex(comIndex) ? com[comIndex] : 0;

    public int ComIndexOf(nint address) => Array.IndexOf(com, address);

    public nint ObjectTableEntry(int globalIndex) => table.TryGetValue(globalIndex, out var obj) ? obj.Address : 0;

    public HumanPuppetFacts ReadFacts(nint address)
    {
        var obj = byAddress[address];
        return new(obj.GlobalIndex, obj.GameObjectId, obj.EntityId, obj.Kind, obj.Name, obj.Terminated, obj.World, obj.NameId,
            obj.Owner, obj.HasHumanModel, obj.Customize?.ToArray());
    }

    public void CopyCharacter(nint puppet, nint source)
    {
        var target = byAddress[puppet];
        var from = byAddress[source];
        Calls.Add($"copy {from.GlobalIndex}->{target.GlobalIndex}");
        target.Name = from.Name;
        target.World = from.World;
        target.Terminated = from.Terminated;
        target.HasHumanModel = from.HasHumanModel;
        target.Customize = from.Customize?.ToArray();
        target.NameId = puppet == source ? target.NameId : CopiedNameId;
        target.Owner = puppet == source ? target.Owner : CopiedOwner;
        if (CopyCopiesKind)
            target.Kind = from.Kind;
        if (CopyChangesEntityId)
            target.EntityId++;
        // Both copy hooks: the child's parent is the source's index.
        if (HumanPuppetSlotRange.Contains(target.GlobalIndex))
            SetEntries(target.GlobalIndex, from.GlobalIndex);
    }

    public void WriteIdentity(nint puppet, string name, ushort homeWorld, bool writePlayerKind, bool clearNameIdAndOwner)
    {
        Calls.Add($"write identity kind={writePlayerKind} clear={clearNameIdAndOwner}");
        var obj = byAddress[puppet];
        obj.Name = name;
        obj.World = homeWorld;
        obj.Terminated = !WriteLeavesNoTerminator;
        if (writePlayerKind)
            obj.Kind = 1;
        if (clearNameIdAndOwner)
        {
            obj.NameId = 0;
            obj.Owner = HumanPuppetFacts.NoOwner;
        }
    }

    public void SetAlpha(nint address, float alpha)
    {
        Calls.Add($"alpha {byAddress[address].GlobalIndex} {alpha}");
        byAddress[address].Alpha = alpha;
    }

    public void EnableDraw(nint address)
    {
        Calls.Add($"draw on {byAddress[address].GlobalIndex}");
        byAddress[address].DrawEnabled = true;
    }

    public void DisableDraw(nint address)
    {
        Calls.Add($"draw off {byAddress[address].GlobalIndex}");
        byAddress[address].DrawEnabled = false;
    }

    public bool HasHumanDrawObject(nint address) => DrawAppears && byAddress[address].DrawEnabled;

    public HumanPuppetScanEntry? ScanEntry(int globalIndex)
    {
        if (globalIndex == 0)
        {
            ScanCount++;
            OnScan?.Invoke();
        }
        if (Injected.TryGetValue(globalIndex, out var injected))
            return injected;
        return table.TryGetValue(globalIndex, out var obj) && obj.Name is { } name ? new(globalIndex, name, obj.World) : null;
    }

    public void DeleteComObject(int comIndex)
    {
        var obj = byAddress[com[comIndex]];
        Calls.Add($"delete {obj.GlobalIndex}");
        if (DeleteFails)
            return;
        com[comIndex] = 0;
        table.Remove(obj.GlobalIndex);
        byAddress.Remove(obj.Address);
        // Both destructor hooks reset the slot's own entry.
        if (!DestructorKeepsMaps && HumanPuppetSlotRange.Contains(obj.GlobalIndex))
            SetEntries(obj.GlobalIndex, -1);
    }

    // IHumanParentMapPort.
    public bool SetPenumbraParent(int globalIndex, int parent)
    {
        Calls.Add($"penumbra set {globalIndex} {parent}");
        if (FailPenumbraSet)
            return false;
        penumbraMap[globalIndex] = parent;
        return true;
    }

    public int? GetPenumbraParent(int globalIndex) => PenumbraAvailable ? penumbraMap[globalIndex] : null;

    public bool SetCustomizePlusParent(int globalIndex, int parent)
    {
        Calls.Add($"customize set {globalIndex} {parent}");
        customizeMap[globalIndex] = parent;
        return true;
    }

    public int? GetCustomizePlusParent(int globalIndex) =>
        !CustomizePlusLoaded ? null : CustomizePlusReadOverride?.Invoke(globalIndex) ?? customizeMap[globalIndex];

    // IHumanPenumbraPort.
    public HumanCollectionReading? GetCollectionForObject(int globalIndex)
    {
        Calls.Add($"collection for {globalIndex}");
        if (!table.ContainsKey(globalIndex))
            return new(false, false, Guid.Empty);
        var effective = Assigned.TryGetValue(globalIndex, out var temporary) ? temporary : EffectiveOverride ?? DefaultCollection;
        return new(true, IndividualSet, effective);
    }

    public Guid? GetCollection(HumanCollectionRole role)
    {
        Calls.Add($"collection {role.Kind}");
        return Roles.TryGetValue(role.Kind, out var collection) ? collection : null;
    }

    public Guid? CreateTemporaryCollection(string name)
    {
        Calls.Add("create collection");
        if (LoseCollectionId)
            return null;
        var id = Guid.NewGuid();
        TemporaryCollections.Add(id);
        return id;
    }

    public HumanAssignmentResult AssignTemporaryCollection(Guid collection, int globalIndex)
    {
        Calls.Add($"assign {globalIndex}");
        if (AssignResult == HumanAssignmentResult.Success)
            Assigned[globalIndex] = collection;
        return AssignResult;
    }

    public bool RemoveAssignment(Guid collection, int globalIndex)
    {
        Calls.Add($"unassign {globalIndex}");
        if (RemoveAssignmentFails)
            return false;
        return Assigned.Remove(globalIndex);
    }

    public bool DeleteTemporaryCollection(Guid collection)
    {
        Calls.Add("delete collection");
        foreach (var entry in Assigned.Where(entry => entry.Value == collection).ToArray())
            Assigned.Remove(entry.Key);
        return TemporaryCollections.Remove(collection);
    }

    // IHumanGlamourerReadPort.
    public (bool IsLocked, bool CanUnlock)? CanUnlockBaseline()
    {
        Calls.Add("glamourer can unlock 0 0");
        return (false, true);
    }
}
#endif
