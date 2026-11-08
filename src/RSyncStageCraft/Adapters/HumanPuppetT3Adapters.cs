#if RSC_DEVELOPMENT
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using Penumbra.Api.Enums;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Actors.Humans;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using GlamourerCanUnlock = Glamourer.Api.IpcSubscribers.CanUnlock;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;
using PenumbraGetCollection = Penumbra.Api.IpcSubscribers.GetCollection;
using PenumbraGetCollectionForObject = Penumbra.Api.IpcSubscribers.GetCollectionForObject;
using PenumbraGetCollections = Penumbra.Api.IpcSubscribers.GetCollections;
using PenumbraGetCutsceneParentIndex = Penumbra.Api.IpcSubscribers.GetCutsceneParentIndex;
using PenumbraGetGameObjectResourcePaths = Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths;
using PenumbraSetCutsceneParentIndex = Penumbra.Api.IpcSubscribers.SetCutsceneParentIndex;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// A1, T3-1 only (Debug build, dev plugin): the human puppet's native
/// primitives through ClientStructs members resolved by Dalamud's resolver. No
/// hooks and no RSC-owned signatures. ReadFacts and ScanEntry catch managed
/// failures and return a value that fails closed; the other members are not
/// guarded here, and a managed throw from them reaches the host's H1-d
/// handling. No member can catch an access
/// violation from a stale address: only the host's checks 1–5 before each
/// call guard against that. Game thread only. The copy flags are
/// R-3's (the position flag, which includes the rotation, for the copy from
/// the player; none for the self-copy) and the hiding writes are R-7's (the
/// targetable bit and the nameplate). There is no kind write (R-8 is not
/// approved): a request for the player kind writes nothing, so the host's
/// re-verify stops.
/// </summary>
internal sealed unsafe class HumanPuppetNativeBackend : IHumanPuppetBackend
{
    // An unreadable object: no index, and reported targetable, so every check fails closed.
    private static readonly HumanPuppetFacts None = new(-1, 0, 0, -1, null, false, 0, 0, 0, false, null, true);

    public bool IsAvailable
    {
        get
        {
            try
            {
                return ClientObjectManager.Addresses.Instance.Value != 0 &&
                    ClientObjectManager.Addresses.CreateBattleCharacter.Value != 0 &&
                    ClientObjectManager.Addresses.GetObjectByIndex.Value != 0 &&
                    ClientObjectManager.Addresses.GetIndexByObject.Value != 0 &&
                    ClientObjectManager.Addresses.DeleteObjectByIndex.Value != 0 &&
                    CharacterSetupContainer.Addresses.CopyFromCharacter.Value != 0 &&
                    GameObjectStruct.Addresses.SetName.Value != 0 &&
                    GameObjectManager.Addresses.Instance.Value != 0 &&
                    ClientObjectManager.Instance() != null &&
                    GameObjectManager.Instance() != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>R-3: the only two flag sets this candidate uses.</summary>
    internal static CharacterSetupContainer.CopyFlags FlagsFor(bool selfCopy) =>
        selfCopy ? CharacterSetupContainer.CopyFlags.None : CharacterSetupContainer.CopyFlags.Position;

    public nint[] ComEntriesSnapshot()
    {
        var entries = new nint[ActorSlotRange.ComSlotCount];
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return entries;
        var count = Math.Min(entries.Length, manager->BattleCharas.Length);
        for (var index = 0; index < count; index++)
            entries[index] = (nint)manager->BattleCharas[index].BattleChara;
        return entries;
    }

    public int CreateCharacter()
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return -1;
        var used = manager->CreateBattleCharacter();
        return used == 0xFFFFFFFF || used > int.MaxValue ? -1 : (int)used;
    }

    public nint ComEntry(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || comIndex < 0 || comIndex >= manager->BattleCharas.Length)
            return 0;
        return (nint)manager->BattleCharas[comIndex].BattleChara;
    }

    public nint ComObjectByIndex(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || comIndex < 0 || comIndex > ushort.MaxValue)
            return 0;
        return (nint)manager->GetObjectByIndex((ushort)comIndex);
    }

    public int ComIndexOf(nint address)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null || address == 0)
            return -1;
        var index = manager->GetIndexByObject((GameObjectStruct*)address);
        return index == 0xFFFFFFFF || index > int.MaxValue ? -1 : (int)index;
    }

    public nint ObjectTableEntry(int globalIndex)
    {
        var objects = GameObjectManager.Instance();
        if (objects == null || globalIndex < 0 || globalIndex >= objects->Objects.IndexSorted.Length)
            return 0;
        return (nint)objects->Objects.IndexSorted[globalIndex].Value;
    }

    public nint LocalPlayer => ObjectTableEntry(0);

    public int ObjectTableLength
    {
        get
        {
            var objects = GameObjectManager.Instance();
            return objects == null ? 0 : objects->Objects.IndexSorted.Length;
        }
    }

    public HumanPuppetFacts ReadFacts(nint address)
    {
        try
        {
            var gameObject = (GameObjectStruct*)address;
            if (gameObject == null || !gameObject->IsCharacter())
                return None;
            var character = (GameCharacter*)address;
            var name = gameObject->NameString;
            var buffer = gameObject->Name;
            var terminator = buffer.IndexOf((byte)0);
            var customize = new ReadOnlySpan<byte>((byte*)&character->DrawData.CustomizeData, HumanPuppetFacts.CustomizeLength).ToArray();
            return new(
                gameObject->ObjectIndex,
                (ulong)gameObject->GetGameObjectId(),
                gameObject->EntityId,
                (byte)gameObject->ObjectKind,
                name,
                terminator >= 0 && terminator == Encoding.UTF8.GetByteCount(name),
                character->HomeWorld,
                character->NameId,
                gameObject->OwnerId,
                IsHumanDrawObject(gameObject),
                customize,
                (gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) != 0);
        }
        catch
        {
            return None;
        }
    }

    public void CopyCharacter(nint puppet, nint source)
    {
        var target = (GameCharacter*)puppet;
        var from = (GameCharacter*)source;
        if (target == null || from == null)
            return;
        target->CharacterSetup.CopyFromCharacter(from, FlagsFor(puppet == source));
    }

    public void PrepareHidden(nint puppet)
    {
        var gameObject = (GameObjectStruct*)puppet;
        if (gameObject == null)
            return;
        gameObject->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        gameObject->RenderFlags |= VisibilityFlags.Nameplate;
    }

    public void WriteIdentity(nint puppet, string name, ushort homeWorld, bool writePlayerKind, bool clearNameIdAndOwner)
    {
        var character = (GameCharacter*)puppet;
        // No kind write in T3-1 (R-8 is not approved): write nothing, so the re-verify stops.
        if (character == null || writePlayerKind || string.IsNullOrEmpty(name))
            return;
        var gameObject = (GameObjectStruct*)puppet;
        gameObject->SetName(name);
        character->HomeWorld = homeWorld;
        if (clearNameIdAndOwner)
        {
            character->NameId = 0;
            gameObject->OwnerId = (uint)HumanPuppetFacts.NoOwner;
        }
    }

    public void SetAlpha(nint address, float alpha)
    {
        var character = (GameCharacter*)address;
        if (character != null)
            character->Alpha = alpha;
    }

    public void EnableDraw(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject != null)
            gameObject->EnableDraw();
    }

    public void DisableDraw(nint address)
    {
        var gameObject = (GameObjectStruct*)address;
        if (gameObject != null)
            gameObject->DisableDraw();
    }

    public bool HasHumanDrawObject(nint address)
    {
        try
        {
            var gameObject = (GameObjectStruct*)address;
            return gameObject != null && IsHumanDrawObject(gameObject);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The name and, for a character, the home world; special slots 440–447 report what they show.</summary>
    public HumanPuppetScanEntry? ScanEntry(int globalIndex)
    {
        try
        {
            var gameObject = (GameObjectStruct*)ObjectTableEntry(globalIndex);
            if (gameObject == null)
                return null;
            var name = gameObject->NameString;
            if (string.IsNullOrEmpty(name))
                return null;
            var world = gameObject->IsCharacter() ? ((GameCharacter*)gameObject)->HomeWorld : (ushort)0;
            return new(globalIndex, name, world);
        }
        catch
        {
            return null;
        }
    }

    public void DeleteComObject(int comIndex)
    {
        var manager = ClientObjectManager.Instance();
        if (manager != null && comIndex is >= 0 and <= ushort.MaxValue)
            manager->DeleteObjectByIndex((ushort)comIndex, 0);
    }

    private static bool IsHumanDrawObject(GameObjectStruct* gameObject)
    {
        var draw = gameObject->DrawObject;
        if (draw == null || ((SceneObject*)draw)->GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return false;
        return ((CharacterBase*)draw)->GetModelType() == CharacterBase.ModelType.Human;
    }

    /// <summary>
    /// R-6, read only: for each partial skeleton of the object's human draw
    /// object, the hashed resource file name, the bone count, and hashes of the
    /// bone names, parent indices and reference pose. No path or binary data.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> SkeletonSummary(nint address, string prefix)
    {
        var facts = new List<KeyValuePair<string, string>>();
        try
        {
            var gameObject = (GameObjectStruct*)address;
            if (gameObject == null || !IsHumanDrawObject(gameObject))
            {
                facts.Add(new(prefix + " skeleton", "no human draw object"));
                return facts;
            }
            var skeleton = (RenderSkeleton*)((CharacterBase*)gameObject->DrawObject)->Skeleton;
            if (skeleton == null)
            {
                facts.Add(new(prefix + " skeleton", "none"));
                return facts;
            }
            facts.Add(new(prefix + " skeleton partials", skeleton->PartialSkeletonCount.ToString()));
            for (var i = 0; i < skeleton->PartialSkeletonCount; i++)
            {
                var handle = skeleton->PartialSkeletons[i].SkeletonResourceHandle;
                var key = $"{prefix} skeleton {i}";
                if (handle == null)
                {
                    facts.Add(new(key, "no resource"));
                    continue;
                }
                var havok = handle->HavokSkeleton;
                var names = new StringBuilder();
                var parents = Array.Empty<byte>();
                var pose = Array.Empty<byte>();
                if (havok != null)
                {
                    for (var bone = 0; bone < havok->Bones.Length; bone++)
                        names.Append(havok->Bones.Data[bone].Name.String).Append('\n');
                    parents = new ReadOnlySpan<byte>(havok->ParentIndices.Data, havok->ParentIndices.Length * sizeof(short)).ToArray();
                    pose = new ReadOnlySpan<byte>(havok->ReferencePose.Data, havok->ReferencePose.Length * sizeof(hkQsTransformf)).ToArray();
                }
                facts.Add(new(key, $"file {Hash(Encoding.UTF8.GetBytes(handle->FileName.ToString()))}, bones {handle->BoneCount}, " +
                    $"names {Hash(Encoding.UTF8.GetBytes(names.ToString()))}, parents {Hash(parents)}, reference pose {Hash(pose)}"));
            }
        }
        catch (Exception exception)
        {
            facts.Add(new(prefix + " skeleton", $"unreadable ({exception.GetType().Name})"));
        }
        return facts;
    }

    /// <summary>A short SHA-256 prefix: enough to compare, nothing to reconstruct.</summary>
    internal static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();
}

/// <summary>
/// A2, T3-1 only: both cutscene-parent maps through their owners' public IPC.
/// Penumbra through the pinned Penumbra.Api subscribers
/// (<c>Penumbra.ApiVersion.V5</c>, <c>Penumbra.SetCutsceneParentIndex.V5</c>,
/// <c>Penumbra.GetCutsceneParentIndex</c>); Customize+ by label
/// (<c>CustomizePlus.General.GetApiVersion</c>, <c>CustomizePlus.General.IsValid</c>,
/// <c>CustomizePlus.GameState.Set/GetCutsceneParentIndex</c>). Every member is
/// total: a provider that does not answer gives false or null.
/// </summary>
internal sealed class HumanParentMapIpc : IHumanParentMapPort
{
    internal const string CustomizePlusInternalName = "CustomizePlus";
    private const string CustomizePlusPrefix = "CustomizePlus.";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly PenumbraApiVersion penumbraVersion;
    private readonly PenumbraSetCutsceneParentIndex penumbraSet;
    private readonly PenumbraGetCutsceneParentIndex penumbraGet;
    private readonly ICallGateSubscriber<(int, int)> customizeVersion;
    private readonly ICallGateSubscriber<bool> customizeValid;
    private readonly ICallGateSubscriber<int, int, int> customizeSet;
    private readonly ICallGateSubscriber<int, int> customizeGet;

    internal HumanParentMapIpc(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        penumbraVersion = new PenumbraApiVersion(pluginInterface);
        penumbraSet = new PenumbraSetCutsceneParentIndex(pluginInterface);
        penumbraGet = new PenumbraGetCutsceneParentIndex(pluginInterface);
        customizeVersion = pluginInterface.GetIpcSubscriber<(int, int)>(CustomizePlusPrefix + "General.GetApiVersion");
        customizeValid = pluginInterface.GetIpcSubscriber<bool>(CustomizePlusPrefix + "General.IsValid");
        customizeSet = pluginInterface.GetIpcSubscriber<int, int, int>(CustomizePlusPrefix + "GameState.SetCutsceneParentIndex");
        customizeGet = pluginInterface.GetIpcSubscriber<int, int>(CustomizePlusPrefix + "GameState.GetCutsceneParentIndex");
    }

    /// <summary>Penumbra answers a compatible version and every bound subscriber is valid.</summary>
    public bool PenumbraAvailable
    {
        get
        {
            try
            {
                if (!penumbraVersion.Valid || !penumbraSet.Valid || !penumbraGet.Valid)
                    return false;
                var (breaking, feature) = penumbraVersion.Invoke();
                return breaking == PenumbraDependencyAdapter.RequiredBreakingVersion && feature >= PenumbraDependencyAdapter.MinimumFeatureVersion;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CustomizePlusLoaded
    {
        get
        {
            try
            {
                return pluginInterface.InstalledPlugins.Any(plugin => plugin.IsLoaded &&
                    string.Equals(plugin.InternalName, CustomizePlusInternalName, StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }
    }

    public (int Major, int Minor)? CustomizePlusApiVersion
    {
        get
        {
            try
            {
                return customizeVersion.HasFunction ? customizeVersion.InvokeFunc() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool CustomizePlusParentIpcPresent
    {
        get
        {
            try
            {
                return customizeSet.HasFunction && customizeGet.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CustomizePlusValid
    {
        get
        {
            try
            {
                return customizeValid.HasFunction && customizeValid.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool SetPenumbraParent(int globalIndex, int parent)
    {
        try
        {
            return penumbraSet.Invoke(globalIndex, parent) == PenumbraApiEc.Success;
        }
        catch
        {
            return false;
        }
    }

    public int? GetPenumbraParent(int globalIndex)
    {
        try
        {
            return penumbraGet.Invoke(globalIndex);
        }
        catch
        {
            return null;
        }
    }

    public bool SetCustomizePlusParent(int globalIndex, int parent)
    {
        try
        {
            // Customize+'s success value is 0.
            return customizeSet.HasFunction && customizeSet.InvokeFunc(globalIndex, parent) == 0;
        }
        catch
        {
            return false;
        }
    }

    public int? GetCustomizePlusParent(int globalIndex)
    {
        try
        {
            return customizeGet.HasFunction ? customizeGet.InvokeFunc(globalIndex) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// A3, T3-1 only: Penumbra's read-only collection calls
/// (<c>Penumbra.GetCollectionForObject.V5</c>, <c>Penumbra.GetCollection</c>).
/// No temporary-collection, assignment or redraw subscriber exists: the four
/// write members throw, and no plugin code calls the host member that reaches
/// them (a build gate checks this).
/// </summary>
internal sealed class HumanPenumbraReadIpc : IHumanPenumbraPort
{
    private static readonly string[] Clans =
    [
        "Midlander", "Highlander", "Wildwood", "Duskwight", "Plainsfolk", "Dunesfolk", "SeekerOfTheSun", "KeeperOfTheMoon",
        "Seawolf", "Hellsguard", "Raen", "Xaela", "Helion", "Lost", "Rava", "Veena",
    ];

    private readonly PenumbraGetCollectionForObject collectionForObject;
    private readonly PenumbraGetCollection collection;

    internal HumanPenumbraReadIpc(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        collectionForObject = new PenumbraGetCollectionForObject(pluginInterface);
        collection = new PenumbraGetCollection(pluginInterface);
    }

    public HumanCollectionReading? GetCollectionForObject(int globalIndex)
    {
        try
        {
            var (valid, individual, effective) = collectionForObject.Invoke(globalIndex);
            return new(valid, individual, effective.Id);
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetCollection(HumanCollectionRole role, out Guid? set)
    {
        set = null;
        if (TypeFor(role) is not { } type)
            return false;
        try
        {
            set = collection.Invoke(type)?.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The collection type for a role, by Penumbra's own names; null for a role that has none (gender 1 male, 2 female; clans 1–16).</summary>
    internal static ApiCollectionType? TypeFor(HumanCollectionRole role)
    {
        string? gender = role.Gender switch
        {
            1 => "Male",
            2 => "Female",
            _ => null,
        };
        string? name = role.Kind switch
        {
            HumanCollectionRoleKind.Default => nameof(ApiCollectionType.Default),
            HumanCollectionRoleKind.NonPlayerChild => nameof(ApiCollectionType.NonPlayerChild),
            HumanCollectionRoleKind.NonPlayerElderly => nameof(ApiCollectionType.NonPlayerElderly),
            HumanCollectionRoleKind.Gender when gender is not null => gender + (role.IsNpc ? "NonPlayerCharacter" : "PlayerCharacter"),
            HumanCollectionRoleKind.ClanGender when gender is not null && role.Clan is >= 1 and <= 16 =>
                gender + Clans[role.Clan - 1] + (role.IsNpc ? "Npc" : ""),
            _ => null,
        };
        return name is not null && Enum.TryParse<ApiCollectionType>(name, ignoreCase: false, out var type) && Enum.IsDefined(type) ? type : null;
    }

    public Guid? CreateTemporaryCollection(string name) => throw new NotSupportedException("T3-1 makes no temporary collection.");

    public HumanAssignmentResult AssignTemporaryCollection(Guid collectionId, int globalIndex) =>
        throw new NotSupportedException("T3-1 makes no assignment.");

    public bool RemoveAssignment(Guid collectionId, int globalIndex) => throw new NotSupportedException("T3-1 makes no assignment.");

    public bool DeleteTemporaryCollection(Guid collectionId) => throw new NotSupportedException("T3-1 makes no temporary collection.");
}

/// <summary>A4, T3-1 only: Glamourer's version and <c>Glamourer.CanUnlock(0, 0)</c>, both read-only.</summary>
internal sealed class HumanGlamourerReadIpc : IHumanGlamourerReadPort
{
    private readonly GlamourerApiVersion version;
    private readonly GlamourerCanUnlock canUnlock;

    internal HumanGlamourerReadIpc(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        version = new GlamourerApiVersion(pluginInterface);
        canUnlock = new GlamourerCanUnlock(pluginInterface);
    }

    public (int Major, int Minor)? ApiVersion
    {
        get
        {
            try
            {
                return version.Invoke();
            }
            catch
            {
                return null;
            }
        }
    }

    public (bool IsLocked, bool CanUnlock)? CanUnlockBaseline()
    {
        try
        {
            // Key 0 on the player's index: a read; Glamourer creates no state for it.
            return canUnlock.Invoke(0, 0, out var locked, out var unlockable) == Glamourer.Api.Enums.GlamourerApiEc.Success
                ? (locked, unlockable)
                : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// R-5 and R-6, T3-1 only: the run's read-only facts, each a count, a flag or
/// a hash prefix. Penumbra's collection list and the resource paths of every
/// object in the table that is not the puppet; Customize+'s profile list and
/// the player's active profile; the player's skeleton summary and kind.
/// </summary>
internal sealed class HumanPuppetT3Probe : IHumanT3Probe
{
    private readonly HumanPuppetNativeBackend backend;
    private readonly Func<int?> puppetSlot;
    private readonly PenumbraGetCollections collections;
    private readonly PenumbraGetGameObjectResourcePaths resourcePaths;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> profiles;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> activeProfile;

    internal HumanPuppetT3Probe(IDalamudPluginInterface pluginInterface, HumanPuppetNativeBackend backend, Func<int?> puppetSlot)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.puppetSlot = puppetSlot ?? throw new ArgumentNullException(nameof(puppetSlot));
        collections = new PenumbraGetCollections(pluginInterface);
        resourcePaths = new PenumbraGetGameObjectResourcePaths(pluginInterface);
        profiles = pluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>(
            "CustomizePlus.Profile.GetList");
        activeProfile = pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
    }

    public ushort? PlayerHomeWorld()
    {
        var player = backend.LocalPlayer;
        if (player == 0)
            return null;
        var facts = backend.ReadFacts(player);
        return facts.GlobalIndex == 0 ? facts.HomeWorld : null;
    }

    public IReadOnlyList<KeyValuePair<string, string>> ReadRunFacts()
    {
        var facts = new List<KeyValuePair<string, string>>();
        var player = backend.LocalPlayer;
        var playerFacts = player == 0 ? null : backend.ReadFacts(player);
        facts.Add(new("player", playerFacts is null || playerFacts.GlobalIndex != 0
            ? "unreadable"
            : $"kind {playerFacts.RawKind}, human draw object {playerFacts.HasHumanModel}, customize {HumanPuppetNativeBackend.Hash(playerFacts.Customize ?? Array.Empty<byte>())}"));
        facts.AddRange(HumanPuppetNativeBackend.SkeletonSummary(player, "player"));
        facts.Add(new("penumbra collections", Read(() =>
        {
            var list = collections.Invoke();
            return $"{list.Count}, {HashLines(list.Select(entry => entry.Key.ToString("N") + "|" + entry.Value))}";
        })));
        facts.AddRange(ResourcePaths());
        facts.Add(new("customize+ profiles", Read(() =>
        {
            if (!profiles.HasFunction)
                return "unanswered";
            var list = profiles.InvokeFunc();
            return $"{list.Count}, {HashLines(list.Select(entry => $"{entry.Item1:N}|{entry.Item2}|{entry.Item3}|" +
                string.Join(";", entry.Item4.Select(character => $"{character.Item1}/{character.Item2}/{character.Item3}/{character.Item4}")) +
                $"|{entry.Item5}|{entry.Item6}"))}";
        })));
        facts.Add(new("customize+ active profile on 0", Read(() =>
        {
            if (!activeProfile.HasFunction)
                return "unanswered";
            var (code, profile) = activeProfile.InvokeFunc(0);
            return $"code {code}, profile {(profile is { } id ? HumanPuppetNativeBackend.Hash(Encoding.UTF8.GetBytes(id.ToString("N"))) : "none")}";
        })));
        return facts;
    }

    /// <summary>R-5: every occupied object-table index except the puppet's, as one count and one hash per index.</summary>
    private IEnumerable<KeyValuePair<string, string>> ResourcePaths()
    {
        var slot = puppetSlot();
        var indices = Enumerable.Range(0, Math.Min(backend.ObjectTableLength, ushort.MaxValue))
            .Where(index => index != slot && backend.ObjectTableEntry(index) != 0)
            .Select(index => (ushort)index).ToArray();
        Dictionary<string, HashSet<string>>?[]? paths = null;
        string? failure = null;
        try
        {
            paths = indices.Length == 0 ? Array.Empty<Dictionary<string, HashSet<string>>?>() : resourcePaths.Invoke(indices);
        }
        catch (Exception exception)
        {
            failure = $"unanswered ({exception.GetType().Name})";
        }
        if (paths is null || paths.Length != indices.Length)
        {
            yield return new("resource paths", failure ?? "unanswered");
            yield break;
        }
        yield return new("resource paths objects", indices.Length.ToString());
        for (var i = 0; i < indices.Length; i++)
            yield return new($"resource paths {indices[i]}", paths[i] is { } map
                ? HashLines(map.SelectMany(entry => entry.Value.Select(game => entry.Key + "|" + game)))
                : "none");
    }

    private static string Read(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            return $"unanswered ({exception.GetType().Name})";
        }
    }

    private static string HashLines(IEnumerable<string> lines) =>
        HumanPuppetNativeBackend.Hash(Encoding.UTF8.GetBytes(string.Join("\n", lines.Order(StringComparer.Ordinal))));
}
#endif
