#if RSC_DEVELOPMENT
namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// What a live object says about itself, read without writing anything.
/// <see cref="Customize"/> is the 26-byte customize array, or null when it
/// cannot be read. <see cref="NameTerminated"/>: the name buffer holds a NUL
/// directly after <see cref="Name"/>.
/// </summary>
public sealed record HumanPuppetFacts(
    int GlobalIndex,
    ulong GameObjectId,
    uint EntityId,
    int RawKind,
    string? Name,
    bool NameTerminated,
    ushort HomeWorld,
    uint NameId,
    ulong OwnerId,
    bool HasHumanModel,
    byte[]? Customize)
{
    /// <summary>The game's "no owner" object id.</summary>
    public const ulong NoOwner = 0xE0000000;

    public const int CustomizeLength = 26;
}

/// <summary>One object-table entry's identity for the O1 scan, as all three plugins see it.</summary>
public readonly record struct HumanPuppetScanEntry(int GlobalIndex, string Name, ushort HomeWorld);

/// <summary>
/// The native primitives a Debug human puppet needs. Every call is made on the
/// game thread, and every decision is the host's. Nothing here sends anything
/// to the game server. No implementation exists in the plugin: this candidate
/// composes nothing.
/// </summary>
public interface IHumanPuppetBackend
{
    bool IsAvailable { get; }

    /// <summary>Every raw COM entry (index 0–248), read without writing, immediately before creating.</summary>
    nint[] ComEntriesSnapshot();

    /// <summary><c>CreateBattleCharacter()</c> with its default arguments: the COM index, or −1.</summary>
    int CreateCharacter();

    nint ComEntry(int comIndex);

    nint ComObjectByIndex(int comIndex);

    int ComIndexOf(nint address);

    nint ObjectTableEntry(int globalIndex);

    nint LocalPlayer { get; }

    HumanPuppetFacts ReadFacts(nint address);

    /// <summary>
    /// B-2: copies <paramref name="source"/>'s character into the puppet
    /// (the source is only read). With the puppet as its own source this is
    /// the self-copy. Both parent-map copy hooks run inside it.
    /// </summary>
    void CopyCharacter(nint puppet, nint source);

    /// <summary>
    /// SI-1's identity write, and nothing else: the name buffer (name and NUL)
    /// and the home world; the player kind only when <paramref name="writePlayerKind"/>;
    /// name ID 0 and owner "none" only when <paramref name="clearNameIdAndOwner"/>.
    /// Never the content ID, entity ID, customize array or any server-visible field.
    /// </summary>
    void WriteIdentity(nint puppet, string name, ushort homeWorld, bool writePlayerKind, bool clearNameIdAndOwner);

    void SetAlpha(nint address, float alpha);

    void EnableDraw(nint address);

    void DisableDraw(nint address);

    /// <summary>A human draw object exists.</summary>
    bool HasHumanDrawObject(nint address);

    /// <summary>The object table's length (the O1 scan's bound).</summary>
    int ObjectTableLength { get; }

    /// <summary>
    /// The identity the three plugins see at a global index, or null for an
    /// empty or nameless slot. Special slots 440–447 report the player they show.
    /// </summary>
    HumanPuppetScanEntry? ScanEntry(int globalIndex);

    /// <summary><c>DeleteObjectByIndex(comIndex, 0)</c>.</summary>
    void DeleteComObject(int comIndex);
}

/// <summary>
/// Both cutscene-parent maps (r5 §7.2), each through its owner's public call.
/// Getters return null when the provider cannot answer. This candidate has no
/// IPC binding: runtime IPC stays separately gated.
/// </summary>
public interface IHumanParentMapPort
{
    bool PenumbraAvailable { get; }

    bool CustomizePlusLoaded { get; }

    /// <summary>The Customize+ IPC API version, or null.</summary>
    (int Major, int Minor)? CustomizePlusApiVersion { get; }

    /// <summary>Both <c>CustomizePlus.GameState.Set/GetCutsceneParentIndex</c> subscribers resolve.</summary>
    bool CustomizePlusParentIpcPresent { get; }

    bool SetPenumbraParent(int globalIndex, int parent);

    int? GetPenumbraParent(int globalIndex);

    bool SetCustomizePlusParent(int globalIndex, int parent);

    int? GetCustomizePlusParent(int globalIndex);
}

/// <summary>Penumbra's answer for one object (<c>GetCollectionForObject</c>).</summary>
public readonly record struct HumanCollectionReading(bool ObjectValid, bool IndividualSet, Guid EffectiveCollection);

/// <summary>A collection type of P16's expected-collection order.</summary>
public enum HumanCollectionRoleKind
{
    NonPlayerElderly,
    NonPlayerChild,
    ClanGender,
    Gender,
    Default,
}

/// <summary>One collection type, with the clan, gender and NPC flag the types that need them carry.</summary>
public readonly record struct HumanCollectionRole(HumanCollectionRoleKind Kind, byte Clan = 0, byte Gender = 0, bool IsNpc = false);

/// <summary>Penumbra's assignment answer: only <see cref="Success"/> assigned anything.</summary>
public enum HumanAssignmentResult
{
    Success,

    /// <summary>An exact temporary or permanent entry exists (the non-forced call refused).</summary>
    Refused,

    Failed,
}

/// <summary>
/// H-5's route, candidate code only: a per-puppet temporary collection and a
/// non-forced assignment. There is no <c>SetCollectionForObject</c> and no
/// forced assignment. No temporary mod is added in this candidate.
/// </summary>
public interface IHumanPenumbraPort
{
    HumanCollectionReading? GetCollectionForObject(int globalIndex);

    /// <summary>The collection set for a type, or null when none is set.</summary>
    Guid? GetCollection(HumanCollectionRole role);

    /// <summary>A new temporary collection, or null when its id did not come back.</summary>
    Guid? CreateTemporaryCollection(string name);

    /// <summary>Always non-forced.</summary>
    HumanAssignmentResult AssignTemporaryCollection(Guid collection, int globalIndex);

    bool RemoveAssignment(Guid collection, int globalIndex);

    bool DeleteTemporaryCollection(Guid collection);
}

/// <summary>
/// H-12: the candidate's whole Glamourer surface. Read-only: the API version
/// and <c>CanUnlock(0, 0)</c> for the baseline. No state is created, applied,
/// unlocked or deleted by anything here.
/// </summary>
public interface IHumanGlamourerReadPort
{
    (int Major, int Minor)? ApiVersion { get; }

    /// <summary><c>CanUnlock(0, 0)</c> as (isLocked, canUnlock), or null.</summary>
    (bool IsLocked, bool CanUnlock)? CanUnlockBaseline();
}
#endif
