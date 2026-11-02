using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace RSyncStageCraft.Core.Appearance;

// Skeleton-resource groups (human plan stage 2, steps SK-P1 and AM4-S1). This
// file contains only a pure path classifier, the one reported-path alias,
// bounded header-and-shape validators and an all-or-nothing group assembler.
// Allowlist v2 and manifest format 2 (AppearanceBundle.cs) use its path rules;
// nothing else calls it yet: capture, transfer, cache and apply are unchanged,
// and nothing here is on the wire. It touches no game, plugin, network or disk.

/// <summary>The role a skeleton resource plays in a group.</summary>
public enum SkeletonResourceRole
{
    /// <summary>A partial skeleton, <c>.sklb</c>.</summary>
    Skeleton,

    /// <summary>A partial skeleton's physics module, <c>.phyb</c>.</summary>
    Physics,

    /// <summary>The racial bone deformer, <c>chara/xls/bonedeformer/human.pbd</c>.</summary>
    Deformer,
}

/// <summary>Which partial skeleton a <c>.sklb</c> or <c>.phyb</c> belongs to. The deformer has none.</summary>
public enum SkeletonPartialSlot
{
    None,

    /// <summary><c>skeleton/base/b0001</c>: always the character's own race.</summary>
    Base,

    /// <summary><c>skeleton/face/fNNNN</c>: the character's own race.</summary>
    Face,

    /// <summary><c>skeleton/hair/hNNNN</c>: the character's own race.</summary>
    Hair,

    /// <summary><c>skeleton/met/mNNNN</c> (head gear): the race the gear resolves for.</summary>
    Head,

    /// <summary><c>skeleton/top/tNNNN</c> (body gear): the race the gear resolves for.</summary>
    Body,
}

/// <summary>A game path recognised as a skeleton resource.</summary>
/// <param name="GamePath">The canonical game path (see <see cref="AppearanceGamePath"/>).</param>
/// <param name="Role">Skeleton, physics or deformer.</param>
/// <param name="Slot">The partial skeleton; <see cref="SkeletonPartialSlot.None"/> for the deformer.</param>
/// <param name="RaceCode">The four-digit race code in the path (for example 101 for c0101); 0 for the deformer.</param>
/// <param name="SetId">The partial skeleton's set id (1 to 9999); 0 for the deformer.</param>
public readonly record struct SkeletonResourcePath(
    string GamePath, SkeletonResourceRole Role, SkeletonPartialSlot Slot, ushort RaceCode, ushort SetId);

/// <summary>
/// Recognises the evidence-confirmed skeleton path families and nothing else.
/// A path is first held to the canonical game-path rules of
/// <see cref="AppearanceGamePath"/> (lowercase, relative, no empty, "." or
/// ".." segment); an extension alone never qualifies. <c>.skp</c>,
/// <c>.kdb</c>, <c>.bnmb</c> and every other path are not skeleton resources
/// in this step.
/// </summary>
public static class SkeletonResourcePaths
{
    /// <summary>The one deformer path, in canonical (lowercase) form.</summary>
    public const string DeformerGamePath = "chara/xls/bonedeformer/human.pbd";

    /// <summary>
    /// The deformer path as Penumbra reports it (mixed case). The only
    /// reported spelling that is ever mapped to a canonical path (AM-4).
    /// </summary>
    public const string ReportedDeformerGamePath = "chara/xls/boneDeformer/human.pbd";

    /// <summary>
    /// Maps exactly <see cref="ReportedDeformerGamePath"/> (ordinal) to
    /// <see cref="DeformerGamePath"/> and returns true. Every other input is
    /// returned unchanged with false: no other path is lowercased or otherwise
    /// normalized, so it still meets the canonical rules or fails them as before.
    /// Capture applies this to every reported game path once, before any
    /// conflict detection (AM-4 corrigendum C8).
    /// </summary>
    public static bool TryCanonicalizeReported(string? reportedGamePath, [NotNullWhen(true)] out string? gamePath)
    {
        if (string.Equals(reportedGamePath, ReportedDeformerGamePath, StringComparison.Ordinal))
        {
            gamePath = DeformerGamePath;
            return true;
        }
        gamePath = reportedGamePath;
        return false;
    }

    /// <summary>The playable race codes a group may be for (c0101 to c1801; never an NPC code).</summary>
    public static IReadOnlyList<ushort> PlayerRaceCodes { get; } =
        [101, 201, 301, 401, 501, 601, 701, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801];

    /// <summary>
    /// Every race code a skeleton path may carry: the playable codes, their
    /// NPC variants (xx04) and the two unknown NPC codes. Anything else is refused.
    /// </summary>
    private static readonly HashSet<ushort> KnownRaceCodes =
    [
        101, 102, 103, 104, 201, 202, 203, 204, 301, 304, 401, 404, 501, 504, 601, 604,
        701, 704, 801, 804, 901, 904, 1001, 1004, 1101, 1104, 1201, 1204, 1301, 1304, 1401, 1404,
        1501, 1504, 1601, 1604, 1701, 1704, 1801, 1804, 9104, 9204,
    ];

    public static bool IsPlayerRaceCode(ushort raceCode) => PlayerRaceCodes.Contains(raceCode);

    public static bool IsKnownRaceCode(ushort raceCode) => KnownRaceCodes.Contains(raceCode);

    /// <summary>
    /// The playable race whose models a playable race falls back to when gear
    /// has none of its own: c0201 to c0101, the other female races to c0201,
    /// male races to c0101, with c1201 to c1101 and c1501 to c0901. c0101 has
    /// no fallback (returns 101).
    /// </summary>
    public static ushort GearFallback(ushort playerRaceCode) => playerRaceCode switch
    {
        201 => 101,
        1201 => 1101,
        1501 => 901,
        _ when (playerRaceCode / 100 & 1) == 0 => 201,
        _ => 101,
    };

    /// <summary>
    /// Classifies a game path. Returns false for any path that is not
    /// canonical or not exactly one of the recognised families:
    /// <list type="bullet">
    /// <item><c>chara/human/cRRRR/skeleton/{base|face|hair|met|top}/{b|f|h|m|t}SSSS/skl_cRRRR{b|f|h|m|t}SSSS.sklb</c></item>
    /// <item>the same folder with <c>phy_cRRRR{b|f|h|m|t}SSSS.phyb</c></item>
    /// <item><c>chara/xls/bonedeformer/human.pbd</c></item>
    /// </list>
    /// The race code and set id in the folder and the file name must agree;
    /// the race code must be known, the set id 0001 to 9999, and a base
    /// skeleton's set id exactly 0001.
    /// </summary>
    public static bool TryClassify(string? gamePath, out SkeletonResourcePath path)
    {
        path = default;
        if (!AppearanceGamePath.IsValid(gamePath))
            return false;
        if (string.Equals(gamePath, DeformerGamePath, StringComparison.Ordinal))
        {
            path = new SkeletonResourcePath(gamePath!, SkeletonResourceRole.Deformer, SkeletonPartialSlot.None, 0, 0);
            return true;
        }

        var segments = gamePath!.Split('/');
        if (segments.Length != 7 || segments[0] != "chara" || segments[1] != "human" || segments[3] != "skeleton")
            return false;
        if (!TryRace(segments[2], out var race))
            return false;
        var (slot, letter) = segments[4] switch
        {
            "base" => (SkeletonPartialSlot.Base, 'b'),
            "face" => (SkeletonPartialSlot.Face, 'f'),
            "hair" => (SkeletonPartialSlot.Hair, 'h'),
            "met" => (SkeletonPartialSlot.Head, 'm'),
            "top" => (SkeletonPartialSlot.Body, 't'),
            _ => (SkeletonPartialSlot.None, '\0'),
        };
        if (slot == SkeletonPartialSlot.None)
            return false;
        var folder = segments[5];
        if (folder.Length != 5 || folder[0] != letter || !TryFourDigits(folder.AsSpan(1), out var set) || set == 0)
            return false;
        if (slot == SkeletonPartialSlot.Base && set != 1)
            return false;

        var stem = $"c{segments[2][1..]}{folder}";
        SkeletonResourceRole role;
        if (segments[6] == $"skl_{stem}.sklb")
            role = SkeletonResourceRole.Skeleton;
        else if (segments[6] == $"phy_{stem}.phyb")
            role = SkeletonResourceRole.Physics;
        else
            return false;

        path = new SkeletonResourcePath(gamePath, role, slot, race, set);
        return true;
    }

    private static bool TryRace(string segment, out ushort race)
    {
        race = 0;
        if (segment.Length != 5 || segment[0] != 'c' || !TryFourDigits(segment.AsSpan(1), out race))
            return false;
        return IsKnownRaceCode(race);
    }

    private static bool TryFourDigits(ReadOnlySpan<char> text, out ushort value)
    {
        value = 0;
        if (text.Length != 4)
            return false;
        var v = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
            v = v * 10 + (c - '0');
        }
        value = (ushort)v;
        return true;
    }
}

/// <summary>Why a skeleton resource's bytes were refused. Header and shape checks only.</summary>
public enum SkeletonStructureProblem
{
    /// <summary>Empty, or shorter than the role's fixed header.</summary>
    TooShort,

    /// <summary>Larger than <see cref="SkeletonResourceValidation.MaximumBytes"/> for the role.</summary>
    TooLarge,

    /// <summary>A <c>.sklb</c> without the "blks" magic.</summary>
    BadMagic,

    /// <summary>A <c>.sklb</c> header version this check does not know.</summary>
    UnknownVersion,

    /// <summary>An offset or extent in the header points outside the file or out of order.</summary>
    BadOffsets,

    /// <summary>A <c>.pbd</c> entry count outside 1 to 64.</summary>
    BadCount,

    /// <summary>A <c>.pbd</c> entry with an unknown or repeated race code.</summary>
    BadRaceCode,

    /// <summary>A <c>.pbd</c> PACK trailer whose sizes do not fit the file.</summary>
    BadTrailer,
}

/// <summary>
/// Bounded header-and-shape checks for each role. They read fixed-size
/// headers and tables and compare offsets with the file length; they never
/// interpret skeleton, physics or deformation data (human plan SS2-a).
/// </summary>
public static class SkeletonResourceValidation
{
    /// <summary>Provisional per-role size limit (well under <see cref="AppearanceLimits.MaximumBlobBytes"/>). Changing it needs review.</summary>
    public const long MaximumSkeletonBytes = 4L * 1024 * 1024;

    /// <inheritdoc cref="MaximumSkeletonBytes"/>
    public const long MaximumPhysicsBytes = 4L * 1024 * 1024;

    /// <inheritdoc cref="MaximumSkeletonBytes"/>
    public const long MaximumDeformerBytes = 4L * 1024 * 1024;

    /// <summary>The most deformer entries a <c>.pbd</c> may declare.</summary>
    public const int MaximumDeformerEntries = 64;

    private const uint SklbMagic = 0x736B6C62; // the bytes "blks", read as a little-endian u32

    // .sklb header versions, read as a little-endian u32 like the magic (on
    // disk the bytes are reversed: 0x31333030 is stored as "0031").
    private const uint SklbVersion1100 = 0x31313030;
    private const uint SklbVersion1110 = 0x31313130;
    private const uint SklbVersion1200 = 0x31323030;
    private const uint SklbVersion1300 = 0x31333030;
    private const uint SklbVersion1301 = 0x31333031;

    private const uint PackType = 0x4B434150; // the bytes "PACK", read as a little-endian u32
    private const int PackFooterBytes = 24; // type u32, version u16, count u16, prior offset i64, total size u64
    private const int PackHeaderBytes = 16;

    public static long MaximumBytes(SkeletonResourceRole role) => role switch
    {
        SkeletonResourceRole.Skeleton => MaximumSkeletonBytes,
        SkeletonResourceRole.Physics => MaximumPhysicsBytes,
        SkeletonResourceRole.Deformer => MaximumDeformerBytes,
        _ => 0,
    };

    /// <summary>Returns null when the bytes pass the role's checks, otherwise the first problem found.</summary>
    public static SkeletonStructureProblem? Check(SkeletonResourceRole role, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes(role))
            return SkeletonStructureProblem.TooLarge;
        return role switch
        {
            SkeletonResourceRole.Skeleton => CheckSkeleton(data),
            SkeletonResourceRole.Physics => CheckPhysics(data),
            SkeletonResourceRole.Deformer => CheckDeformer(data),
            _ => SkeletonStructureProblem.TooShort,
        };
    }

    // .sklb: magic "blks"; a u32 version (1100, 1110, 1200, 1300 or 1301); then either
    // (i16 layer offset, u16 skeleton offset) for 1100, 1110 and 1200, or
    // (u32 layer offset, u32 skeleton offset) for 1300 and 1301. The skeleton
    // data must start after that header and inside the file.
    private static SkeletonStructureProblem? CheckSkeleton(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            return SkeletonStructureProblem.TooShort;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != SklbMagic)
            return SkeletonStructureProblem.BadMagic;
        bool oldHeader;
        switch (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]))
        {
            case SklbVersion1100 or SklbVersion1110 or SklbVersion1200:
                oldHeader = true;
                break;
            case SklbVersion1300 or SklbVersion1301:
                oldHeader = false;
                break;
            default:
                return SkeletonStructureProblem.UnknownVersion;
        }
        var headerEnd = oldHeader ? 12 : 16;
        long skeletonOffset = oldHeader
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[10..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        return skeletonOffset >= headerEnd && skeletonOffset < data.Length ? null : SkeletonStructureProblem.BadOffsets;
    }

    // .phyb: u32 version; when the version is not 0, a u32 data type follows;
    // then u32 collision offset and u32 simulator offset. Both offsets must lie
    // after that header, in order, and inside the file.
    private static SkeletonStructureProblem? CheckPhysics(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return SkeletonStructureProblem.TooShort;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var headerEnd = version == 0 ? 12 : 16;
        if (data.Length < headerEnd)
            return SkeletonStructureProblem.TooShort;
        long collision = BinaryPrimitives.ReadUInt32LittleEndian(data[(headerEnd - 8)..]);
        long simulator = BinaryPrimitives.ReadUInt32LittleEndian(data[(headerEnd - 4)..]);
        return collision >= headerEnd && collision <= simulator && simulator <= data.Length
            ? null
            : SkeletonStructureProblem.BadOffsets;
    }

    // .pbd: i32 entry count n (1 to 64); n entries of 12 bytes (u16 race code,
    // i16 tree index, i32 body offset, f32 scale); n tree entries of 8 bytes.
    // Each body offset is 0 or lies after the tables with room for its i32
    // bone count, before any PACK trailer. A trailer, when the last 24 bytes
    // start with "PACK", must declare a total size that fits the file.
    private static SkeletonStructureProblem? CheckDeformer(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return SkeletonStructureProblem.TooShort;
        var core = data.Length;
        if (data.Length >= PackFooterBytes && BinaryPrimitives.ReadUInt32LittleEndian(data[^PackFooterBytes..]) == PackType)
        {
            var total = BinaryPrimitives.ReadUInt64LittleEndian(data[^8..]);
            if (total < PackFooterBytes + PackHeaderBytes || total > (ulong)(data.Length - 4))
                return SkeletonStructureProblem.BadTrailer;
            core = data.Length - (int)total;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 1 || count > MaximumDeformerEntries)
            return SkeletonStructureProblem.BadCount;
        var tablesEnd = 4 + count * 20;
        if (tablesEnd > core)
            return SkeletonStructureProblem.BadOffsets;

        var seen = new HashSet<ushort>();
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(4 + i * 12, 12);
            var race = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (!SkeletonResourcePaths.IsKnownRaceCode(race) || !seen.Add(race))
                return SkeletonStructureProblem.BadRaceCode;
            long offset = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            if (offset != 0 && (offset < tablesEnd || offset > core - 4))
                return SkeletonStructureProblem.BadOffsets;
        }
        return null;
    }
}

/// <summary>One skeleton resource offered to a group: its game path and its exact bytes.</summary>
public readonly record struct SkeletonGroupCandidate(string GamePath, ReadOnlyMemory<byte> Content);

/// <summary>One accepted member of a group.</summary>
public sealed record SkeletonGroupMember(SkeletonResourcePath Path, AppearanceBlobRef Content);

/// <summary>
/// An accepted skeleton-resource group for one playable race. Members are
/// sorted by game path (ordinal). A group holds what its required set holds:
/// a <c>.sklb</c> with <c>human.pbd</c> and no <c>.phyb</c> is a whole group
/// when that is what the character resolved.
/// </summary>
public sealed record SkeletonResourceGroup(ushort RaceCode, IReadOnlyList<SkeletonGroupMember> Members);

/// <summary>Why a group was refused. The whole group is refused; no member is ever accepted alone.</summary>
public enum SkeletonGroupRefusal
{
    /// <summary>The target is not a playable race code.</summary>
    InvalidTargetRace,

    /// <summary>The required set is empty.</summary>
    Empty,

    /// <summary>More than <see cref="SkeletonResourceGroupAssembler.MaximumMembers"/> paths were required or offered.</summary>
    TooManyMembers,

    /// <summary>A path is not canonical (see <see cref="AppearanceGamePath"/>).</summary>
    InvalidGamePath,

    /// <summary>A canonical path that is not a recognised skeleton resource.</summary>
    NotASkeletonResource,

    /// <summary>The same game path was offered twice.</summary>
    DuplicateGamePath,

    /// <summary>A required member was not offered: the group is incomplete.</summary>
    MissingRequiredMember,

    /// <summary>A member was offered that the required set does not contain.</summary>
    UnexpectedMember,

    /// <summary>Two skeletons, or two physics modules, for one partial slot: more than one group.</summary>
    MoreThanOneGroup,

    /// <summary>A member's race code cannot belong to the target race's character.</summary>
    RaceMismatch,

    /// <summary>A slot's skeleton and physics module name different races or set ids.</summary>
    InconsistentSlot,

    /// <summary>A member's bytes failed the role's checks (see <see cref="SkeletonGroupResult.Problem"/>).</summary>
    Structure,
}

/// <summary>The outcome of assembly: exactly one of <see cref="Group"/> and <see cref="Refusal"/> is set.</summary>
public sealed record SkeletonGroupResult(
    SkeletonResourceGroup? Group,
    SkeletonGroupRefusal? Refusal,
    string? GamePath = null,
    SkeletonStructureProblem? Problem = null)
{
    public bool IsAccepted => Group is not null;

    internal static SkeletonGroupResult Refuse(SkeletonGroupRefusal refusal, string? gamePath = null, SkeletonStructureProblem? problem = null) =>
        new(null, refusal, gamePath, problem);
}

/// <summary>
/// Assembles one skeleton-resource group, all or nothing (human plan D-a to
/// D-e, A13). Completeness follows the group's required dependencies, not a
/// fixed list of extensions: the caller passes the skeleton-resource game
/// paths the character actually resolved to non-game files, and the group
/// must offer exactly those, each with valid bytes. Every check runs before
/// any result is returned; a refusal names the first failing path.
/// </summary>
public static class SkeletonResourceGroupAssembler
{
    /// <summary>The most members one group may hold (five partial slots times two roles, plus the deformer).</summary>
    public const int MaximumMembers = 11;

    public static SkeletonGroupResult Assemble(
        ushort targetRaceCode,
        IReadOnlyCollection<string> requiredGamePaths,
        IReadOnlyList<SkeletonGroupCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(requiredGamePaths);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!SkeletonResourcePaths.IsPlayerRaceCode(targetRaceCode))
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.InvalidTargetRace);
        if (requiredGamePaths.Count == 0)
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.Empty);
        if (requiredGamePaths.Count > MaximumMembers || candidates.Count > MaximumMembers)
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.TooManyMembers);

        // The required set: canonical, recognised and unique.
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gamePath in requiredGamePaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (Classify(gamePath, out _) is { } refusal)
                return SkeletonGroupResult.Refuse(refusal, gamePath);
            if (!required.Add(gamePath))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.DuplicateGamePath, gamePath);
        }

        // The offered members: canonical, recognised, unique, and exactly the required set.
        var offered = new SortedDictionary<string, (SkeletonResourcePath Path, ReadOnlyMemory<byte> Content)>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (Classify(candidate.GamePath, out var path) is { } refusal)
                return SkeletonGroupResult.Refuse(refusal, candidate.GamePath);
            if (!offered.TryAdd(candidate.GamePath, (path, candidate.Content)))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.DuplicateGamePath, candidate.GamePath);
        }
        foreach (var gamePath in required.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!offered.ContainsKey(gamePath))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.MissingRequiredMember, gamePath);
        }
        foreach (var gamePath in offered.Keys)
        {
            if (!required.Contains(gamePath))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.UnexpectedMember, gamePath);
        }

        if (CheckOneGroup(targetRaceCode, offered.Values.Select(value => value.Path)) is { } shape)
            return shape;

        // Every member's bytes, checked and hashed; nothing is accepted unless all pass.
        var members = new List<SkeletonGroupMember>(offered.Count);
        foreach (var (gamePath, (path, content)) in offered)
        {
            if (SkeletonResourceValidation.Check(path.Role, content.Span) is { } problem)
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.Structure, gamePath, problem);
            members.Add(new SkeletonGroupMember(path, new AppearanceBlobRef(ContentHash.Of(content.Span), content.Length)));
        }
        return new SkeletonGroupResult(new SkeletonResourceGroup(targetRaceCode, members), null);
    }

    /// <summary>
    /// Checks a group a manifest declares, without its bytes (format 2, AM-4):
    /// <paramref name="members"/> are the manifest's skeleton-resource entries
    /// and are the whole group. The same rules as <see cref="Assemble"/> apply
    /// to the paths (a playable target, at most <see cref="MaximumMembers"/>,
    /// canonical recognised unique paths, one group, race fit, consistent
    /// slots), and each declared size must be within the role's limit. The
    /// bytes are checked wherever they are present, with <see cref="Assemble"/>.
    /// </summary>
    public static SkeletonGroupResult AssembleDeclared(ushort targetRaceCode, IReadOnlyList<AppearanceFileEntry> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (!SkeletonResourcePaths.IsPlayerRaceCode(targetRaceCode))
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.InvalidTargetRace);
        if (members.Count == 0)
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.Empty);
        if (members.Count > MaximumMembers)
            return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.TooManyMembers);

        var declared = new SortedDictionary<string, (SkeletonResourcePath Path, AppearanceBlobRef Content)>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is null)
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.InvalidGamePath);
            if (Classify(member.GamePath, out var path) is { } refusal)
                return SkeletonGroupResult.Refuse(refusal, member.GamePath);
            if (!declared.TryAdd(member.GamePath, (path, member.Content)))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.DuplicateGamePath, member.GamePath);
        }
        if (CheckOneGroup(targetRaceCode, declared.Values.Select(value => value.Path)) is { } shape)
            return shape;
        foreach (var (gamePath, (path, content)) in declared)
        {
            if (content is not { IsValid: true })
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.Structure, gamePath, SkeletonStructureProblem.TooShort);
            if (content.Size > SkeletonResourceValidation.MaximumBytes(path.Role))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.Structure, gamePath, SkeletonStructureProblem.TooLarge);
        }
        var accepted = declared.Values.Select(value => new SkeletonGroupMember(value.Path, value.Content)).ToArray();
        return new SkeletonGroupResult(new SkeletonResourceGroup(targetRaceCode, accepted), null);
    }

    // One group: at most one skeleton and one physics module per partial slot,
    // agreeing with each other, and every race code possible for the target.
    // The paths arrive sorted by game path; the first failing one is named.
    private static SkeletonGroupResult? CheckOneGroup(ushort targetRaceCode, IEnumerable<SkeletonResourcePath> paths)
    {
        var skeletons = new Dictionary<SkeletonPartialSlot, SkeletonResourcePath>();
        var physics = new Dictionary<SkeletonPartialSlot, SkeletonResourcePath>();
        foreach (var path in paths)
        {
            if (path.Role == SkeletonResourceRole.Deformer)
                continue;
            var bySlot = path.Role == SkeletonResourceRole.Skeleton ? skeletons : physics;
            if (!bySlot.TryAdd(path.Slot, path))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.MoreThanOneGroup, path.GamePath);
            if (!RaceFits(targetRaceCode, path))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.RaceMismatch, path.GamePath);
        }
        foreach (var (slot, skeleton) in skeletons)
        {
            if (physics.TryGetValue(slot, out var module) && (module.RaceCode != skeleton.RaceCode || module.SetId != skeleton.SetId))
                return SkeletonGroupResult.Refuse(SkeletonGroupRefusal.InconsistentSlot, module.GamePath);
        }
        return null;
    }

    private static SkeletonGroupRefusal? Classify(string? gamePath, out SkeletonResourcePath path)
    {
        path = default;
        if (!AppearanceGamePath.IsValid(gamePath))
            return SkeletonGroupRefusal.InvalidGamePath;
        return SkeletonResourcePaths.TryClassify(gamePath, out path) ? null : SkeletonGroupRefusal.NotASkeletonResource;
    }

    // Base, face and hair use the character's own race. Head and body gear use
    // the race the gear resolves for: the character's own, its fallback, or c0101.
    private static bool RaceFits(ushort target, SkeletonResourcePath path) => path.Slot switch
    {
        SkeletonPartialSlot.Base or SkeletonPartialSlot.Face or SkeletonPartialSlot.Hair => path.RaceCode == target,
        SkeletonPartialSlot.Head or SkeletonPartialSlot.Body =>
            path.RaceCode == target || path.RaceCode == SkeletonResourcePaths.GearFallback(target) || path.RaceCode == 101,
        _ => false,
    };
}
