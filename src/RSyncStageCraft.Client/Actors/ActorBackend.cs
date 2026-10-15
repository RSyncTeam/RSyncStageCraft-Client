using System.Numerics;

namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// The client-only object range: ClientObjectManager owns global object-table
/// indices 200–448 inclusive (249 non-networked objects), and RSC accepts all
/// of them. In-game testing showed the automatic allocator legitimately returns
/// COM slot 0 at global index 200. The COM-to-global mapping is never
/// assumed: the actual global index is read from the created object and
/// checked against this range.
/// </summary>
public static class ActorSlotRange
{
    public const int MinimumGlobalIndex = 200;
    public const int MaximumGlobalIndex = 448;

    /// <summary>ClientObjectManager slots (COM indices 0–248).</summary>
    public const int ComSlotCount = 249;

    public static bool IsAcceptedGlobalIndex(int globalIndex) =>
        globalIndex is >= MinimumGlobalIndex and <= MaximumGlobalIndex;

    public static bool IsComIndex(int comIndex) => comIndex is >= 0 and < ComSlotCount;
}

/// <summary>
/// What a live object says about itself, read without writing anything.
/// <see cref="KindUnset"/>: a freshly created COM object whose object kind is
/// not set yet (None); <see cref="IActorSceneBackend.Setup"/> makes it a
/// battle NPC. Any other kind (a player above all) is never RSC's.
/// <see cref="Tag"/>: the object's name as read back (RSC writes a per-spawn
/// nonce there in <see cref="IActorSceneBackend.Setup"/>), or null.
/// </summary>
/// <see cref="RawKind"/>: the object's raw <c>ObjectKind</c> byte, or −1 when unknown.
public readonly record struct ActorObjectFacts(
    int GlobalIndex,
    ulong GameObjectId,
    uint EntityId,
    bool IsBattleNpc,
    bool KindUnset = false,
    string? Tag = null,
    int RawKind = -1);

/// <summary>
/// The game's <c>ObjectKind</c> names, for diagnostics only (ClientStructs
/// <c>FFXIV.Client.Game.Object.ObjectKind</c> at commit <c>b7aa79c4</c>,
/// 2026-10-04). Which kinds a fresh object may have is decided only by
/// <see cref="IsApprovedFreshKind"/>.
/// </summary>
public static class ActorObjectKinds
{
    private static readonly string[] Names =
    [
        "None", "Pc", "BattleNpc", "EventNpc", "Treasure", "Aetheryte", "GatheringPoint", "EventObj", "Mount",
        "Companion", "Retainer", "AreaObject", "HousingEventObject", "Cutscene", "ReactionEventObject", "Ornament",
        "CardStand", "FollowMount",
    ];

    public const int None = 0;
    public const int BattleNpc = 2;
    public const int EventNpc = 3;

    /// <summary>"Name (value)", e.g. "BattleNpc (2)"; an unnamed value is "unknown (n)".</summary>
    public static string Describe(int rawKind) => rawKind < 0
        ? "unknown"
        : $"{(rawKind < Names.Length ? Names[rawKind] : "unknown")} ({rawKind})";

    /// <summary>
    /// The kinds the exact object just returned by <c>CreateBattleCharacter()</c>
    /// may have before setup, in the same game-thread creation call:
    /// BattleNpc, None (kind not set yet), or EventNpc (observed in game as
    /// the fresh kind of a client-only battle character). Never used after setup: every later check requires a
    /// tagged BattleNpc, so RSC never adopts or manages ordinary event NPCs.
    /// </summary>
    public static bool IsApprovedFreshKind(ActorObjectFacts facts) =>
        facts.IsBattleNpc || facts.KindUnset || facts.RawKind == EventNpc;

    /// <summary>A fresh kind that setup changes to BattleNpc (None or EventNpc).</summary>
    public static bool BecomesBattleNpcAtSetup(int rawKind) => rawKind is None or EventNpc;
}

/// <summary>
/// The per-spawn ownership tag RSC writes as the object's name (the ownership
/// proof): "RSC#" and 32
/// lowercase hex digits, 128 bits from <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// 36 ASCII bytes plus the terminator fit the game's 64-byte name buffer
/// (ClientStructs <c>GameObject._name</c>, <c>FixedSizeArray64&lt;byte&gt;</c>),
/// written through ClientStructs' resolved <c>GameObject.SetName</c> and read
/// back through its bounded <c>NameString</c>. It is one fact of the ownership
/// proof, never sufficient alone, and never shown to players.
/// </summary>
public static class ActorOwnershipTag
{
    public const string Prefix = "RSC#";
    public const int RandomBits = 128;
    public const int Length = 36;

    /// <summary>The game's name buffer, including its terminator (ClientStructs <c>FixedSizeArray64&lt;byte&gt;</c>).</summary>
    public const int GameNameBufferBytes = 64;

    public static string Create() =>
        Prefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(RandomBits / 8)).ToLowerInvariant();

    public static bool IsWellFormed(string? tag) =>
        tag is { Length: Length } && tag.StartsWith(Prefix, StringComparison.Ordinal) &&
        tag.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}

/// <summary>
/// Client-only battle characters, through ClientStructs'
/// ClientObjectManager. Every call is made on the game thread. Methods are
/// primitive: every decision (which slot, whether a created object is RSC's,
/// when it is ready, what to free) is made by <see cref="ActorLocalObjectHost"/>
/// so it can be tested with a fake. Nothing here sends anything to the game
/// server.
/// </summary>
public interface IActorSceneBackend
{
    /// <summary>
    /// Every ClientStructs address this stage uses is resolved (nonzero).
    /// Otherwise no actor is ever spawned ("Actors are unavailable on this
    /// game version").
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The raw COM array entry (<c>BattleCharas[index].BattleChara</c>), or 0.
    /// At creation it must name the same object as <see cref="ComObjectByIndex"/>;
    /// later ownership checks use the game's own <see cref="ComObjectByIndex"/>
    /// and <see cref="ComIndexOf"/>.
    /// </summary>
    nint ComEntry(int comIndex);

    /// <summary>
    /// Every raw COM array entry (index 0–248), read without writing, taken
    /// immediately before creating: the returned slot must have been empty,
    /// so a pre-existing object is never adopted.
    /// </summary>
    nint[] ComEntriesSnapshot();

    /// <summary>
    /// The game's automatic allocator, <c>CreateBattleCharacter()</c> with its
    /// default arguments: the COM index it created the object in, or −1.
    /// </summary>
    int CreateBattleCharacter();

    /// <summary><c>GetObjectByIndex(index)</c>, or 0.</summary>
    nint ComObjectByIndex(int comIndex);

    /// <summary><c>GetIndexByObject(address)</c>: the object's COM index now, or −1.</summary>
    int ComIndexOf(nint address);

    /// <summary><c>DeleteObjectByIndex(index, 0)</c>.</summary>
    void DeleteComObject(int comIndex);

    /// <summary>The object-table entry at a global index, or 0.</summary>
    nint ObjectTableEntry(int globalIndex);

    /// <summary>The local player's address (object-table index 0), or 0.</summary>
    nint LocalPlayer { get; }

    ActorObjectFacts ReadFacts(nint address);

    /// <summary>
    /// Sets the object up as the creature while it is still undrawn and
    /// invisible: alpha 0 first, then the BNpc setup, the model (and a
    /// demihuman's parts), the ownership <paramref name="tag"/> as the name
    /// (read back by <see cref="ReadFacts"/>) and a hidden nameplate. An unset
    /// object kind becomes BattleNpc. Never draws.
    /// </summary>
    void Setup(nint address, ActorDrawPlan plan, string tag);

    /// <summary>Writes position, facing and scale (row scale × wire scale), and the draw-object transform if drawn.</summary>
    void SetTransform(nint address, Vector3 position, float yaw, float scale);

    void SetAlpha(nint address, float alpha);

    bool IsReadyToDraw(nint address);

    void EnableDraw(nint address);

    void DisableDraw(nint address);

    /// <summary>The draw object exists and was built from <paramref name="modelCharaId"/>.</summary>
    bool HasDrawnModel(nint address, uint modelCharaId);
}
