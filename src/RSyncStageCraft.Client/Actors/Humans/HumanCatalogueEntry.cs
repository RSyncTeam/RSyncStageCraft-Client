#if RSC_DEVELOPMENT
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Core.Actors.Humans;

// HV-5 M1 (Debug only): the local human catalogue's entry, its friendly
// label and its file format. An entry is a local snapshot of the owner's own
// appearance; it is never uploaded, exported or synchronized, and it never
// holds a local path, the mod root, a name, a world, an object index, a
// collection id, a key or a receipt.

/// <summary>
/// The friendly label (owner decision D11, as revised by amendment 1): 1 to
/// 32 ASCII letters, digits, spaces and hyphens, trimmed, unique
/// case-insensitively, and never a well-formed synthetic actor name
/// (<see cref="HumanPuppetName.IsWellFormed"/>). A label is catalogue data
/// only: it is never written to a game object, passed to a provider, or used
/// for ownership or lookup of an actor.
/// </summary>
public static class HumanCatalogueLabel
{
    public const int MaximumLength = 32;

    /// <summary>The trimmed label, or null with the reason.</summary>
    public static string? Normalize(string? text, out string? refusal)
    {
        var label = text?.Trim() ?? string.Empty;
        refusal = null;
        if (label.Length == 0)
            refusal = "a label is required";
        else if (label.Length > MaximumLength)
            refusal = $"a label has at most {MaximumLength} characters";
        else if (label.Any(character => character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or '-')))
            refusal = "a label has only ASCII letters, digits, spaces and hyphens";
        else if (HumanPuppetName.IsWellFormed(label))
            refusal = "a label may not have the form of a synthetic actor name";
        return refusal is null ? label : null;
    }

    /// <summary>Labels are unique ignoring case.</summary>
    public static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The game and provider versions at capture (C9), as reported locally:
/// the game's version string and the installed plugins' manifest versions.
/// Recorded only; whether a mismatch refuses a spawn is decision D7 (M2).
/// </summary>
public sealed record HumanCatalogueVersions(string? Game, string? Penumbra, string? Glamourer, string? CustomizePlus)
{
    public const int MaximumLength = 96;

    /// <summary>Null, or the value if it is printable ASCII within the limit; anything else is not recorded.</summary>
    public static string? Bound(string? value) =>
        value is { Length: > 0 and <= MaximumLength } && value.All(character => character is >= ' ' and <= '~') ? value : null;

    public HumanCatalogueVersions Bounded() => new(Bound(Game), Bound(Penumbra), Bound(Glamourer), Bound(CustomizePlus));
}

/// <summary>One game path of the package: a stored file (by content hash) or a swap to another game path.</summary>
public sealed record HumanCataloguePath(string GamePath, string? Blob, string? Swap);

/// <summary>One stored file: its content hash and exact size.</summary>
public sealed record HumanCatalogueBlob(string Hash, long Size);

/// <summary>The skeleton group (IVCS and the like): all or nothing, for one race code, with the deformer when it is redirected (D3-a).</summary>
public sealed record HumanCatalogueSkeleton(ushort RaceCode, IReadOnlyList<string> Members, bool Deformer);

/// <summary>What the capture saw, by kind. Game paths only are counted; no path is stored here.</summary>
public sealed record HumanCatalogueCounts(
    int Redirects, int Swaps, int Vanilla, int Outside, int Files, long Bytes,
    int Skeleton, int Physics, int Deformer, int Other);

/// <summary>
/// One immutable catalogue entry, local format <see cref="CurrentFormat"/>
/// (no profile) or, from HV-5 M3, <see cref="ProfileFormat"/> (the same
/// fields plus the owner-approved Customize+ profile).
/// The Glamourer state and the manipulations are stored as Penumbra and
/// Glamourer returned them (opaque); the structured Glamourer reference is
/// kept for the later spawn's readback (D8 option b) and is not decoded here.
/// <para>HV-5 M3: <paramref name="Profile"/> is the canonical profile text
/// (<see cref="HumanCatalogueProfile.Canonicalize"/>), with its hash and its
/// counts against the captured skeleton. All three are null in a
/// <see cref="CurrentFormat"/> entry and are then not written, so such an
/// entry's file is exactly as before M3.</para>
/// </summary>
public sealed record HumanCatalogueEntry(
    string Format,
    Guid EntryId,
    string Label,
    DateTimeOffset CapturedUtc,
    HumanCatalogueVersions Versions,
    byte Race,
    byte Tribe,
    byte Sex,
    byte BodyType,
    string GlamourerState,
    string GlamourerStateHash,
    string GlamourerReference,
    string GlamourerReferenceHash,
    string Manipulations,
    string ManipulationsHash,
    IReadOnlyList<HumanCataloguePath> Paths,
    IReadOnlyList<HumanCatalogueBlob> Blobs,
    HumanCatalogueSkeleton? Skeleton,
    IReadOnlyList<string> SkeletonSummary,
    HumanCatalogueCounts Counts,
    string ProfileSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HumanCatalogueProfileCounts? ProfileCounts = null)
{
    /// <summary>An entry with no Customize+ profile (M1 and M2; still written by a capture without the flag).</summary>
    public const string CurrentFormat = "hv5-local-1";

    /// <summary>HV-5 M3: an entry with the owner-approved Customize+ profile.</summary>
    public const string ProfileFormat = "hv5-local-2";

    /// <summary>No Customize+ profile was read (every <see cref="CurrentFormat"/> entry).</summary>
    public const string NoProfile = "none";

    /// <summary>D2 (M3): the normal profile active on the owner's character at capture, read with the explicit flag.</summary>
    public const string OwnerApproved = "owner-approved";

    /// <summary>The 8-character id used in log lines (not stored).</summary>
    [JsonIgnore]
    public string ShortId => EntryId.ToString("N")[..8];

    /// <summary>Never the stored data.</summary>
    public override string ToString() => $"catalogue entry {ShortId} ({Paths.Count} game path(s))";

    /// <summary>A short SHA-256 of a text, as stored beside it.</summary>
    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>The entry file: UTF-8 JSON, written and read with fixed options; every field is checked on read.</summary>
public static class HumanCatalogueEntryCodec
{
    /// <summary>The largest entry file read or written.</summary>
    public const int MaximumEntryBytes = 16 * 1024 * 1024;

    /// <summary>The longest game path or swap target stored.</summary>
    public const int MaximumGamePathLength = 260;

    /// <summary>The largest package (owner decision D9).</summary>
    public const int MaximumGamePaths = 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        // Only quotes, backslashes and control characters are escaped, so a
        // local path would appear as written (the capture's check relies on it).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly System.Text.RegularExpressions.Regex FileHashPrefix =
        new("^file [0-9a-f]{16}, ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// One skeleton summary fact without its leading hash of the resource
    /// file name (<c>file &lt;16 hex&gt;, </c>), which is derived from a name
    /// that can be a local path. Any other text is returned unchanged.
    /// </summary>
    public static string WithoutFileHash(string fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return FileHashPrefix.Replace(fact, string.Empty, 1);
    }

    public static byte[] Serialize(HumanCatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Problem(entry) is { } problem)
            throw new ArgumentException("not a valid catalogue entry: " + problem, nameof(entry));
        return JsonSerializer.SerializeToUtf8Bytes(entry, Options);
    }

    /// <summary>The entry, or null with the reason. Never throws for bad input.</summary>
    public static HumanCatalogueEntry? TryParse(ReadOnlySpan<byte> utf8, out string? problem)
    {
        if (utf8.Length is 0 or > MaximumEntryBytes)
        {
            problem = "empty or too large";
            return null;
        }
        HumanCatalogueEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<HumanCatalogueEntry>(utf8, Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            problem = "not an entry";
            return null;
        }
        if (entry is null)
        {
            problem = "not an entry";
            return null;
        }
        problem = Problem(entry);
        return problem is null ? entry : null;
    }

    /// <summary>Null when every field is valid; otherwise the first problem (no stored data in it).</summary>
    public static string? Problem(HumanCatalogueEntry entry)
    {
        if (entry.Format is not (HumanCatalogueEntry.CurrentFormat or HumanCatalogueEntry.ProfileFormat))
            return "unknown format";
        if (entry.EntryId == Guid.Empty)
            return "no entry id";
        if (entry.Label is null || HumanCatalogueLabel.Normalize(entry.Label, out _) != entry.Label)
            return "invalid label";
        if (entry.Versions is null || entry.Versions != entry.Versions.Bounded())
            return "invalid versions";
        if (ProfileProblem(entry) is { } profile)
            return profile;
        if (string.IsNullOrEmpty(entry.GlamourerState) || !GameAppearanceCaptureReader.IsCanonicalBase64(entry.GlamourerState) ||
            entry.GlamourerStateHash != HumanCatalogueEntry.HashOf(entry.GlamourerState))
            return "invalid Glamourer state";
        if (!IsJsonObject(entry.GlamourerReference) || entry.GlamourerReferenceHash != HumanCatalogueEntry.HashOf(entry.GlamourerReference))
            return "invalid Glamourer reference";
        if (entry.Manipulations is null || entry.ManipulationsHash != HumanCatalogueEntry.HashOf(entry.Manipulations))
            return "invalid manipulations";
        if (entry.Paths is null || entry.Blobs is null || entry.SkeletonSummary is null || entry.Counts is null)
            return "missing data";
        if (entry.Paths.Count is 0 or > MaximumGamePaths)
            return "invalid package size";
        var blobs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var blob in entry.Blobs)
        {
            if (blob is null || !ContentHash.TryParse(blob.Hash, out _) || blob.Size is <= 0 or > AppearanceLimits.MaximumBlobBytes ||
                !blobs.TryAdd(blob.Hash, blob.Size))
                return "invalid file list";
        }
        var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var redirects = 0;
        foreach (var path in entry.Paths)
        {
            if (path is null || !IsStoredGamePath(path.GamePath) || !gamePaths.Add(path.GamePath))
                return "invalid game path";
            if ((path.Blob is null) == (path.Swap is null))
                return "a game path needs exactly one source";
            if (path.Blob is { } hash)
            {
                if (!blobs.ContainsKey(hash))
                    return "a game path names an unlisted file";
                used.Add(hash);
                redirects++;
            }
            else if (!IsStoredGamePath(path.Swap) || string.Equals(path.Swap, path.GamePath, StringComparison.OrdinalIgnoreCase))
                return "invalid swap";
        }
        if (redirects == 0)
            return "no stored file";
        if (used.Count != blobs.Count)
            return "an unused file is listed";
        if (entry.Skeleton is { } skeleton)
        {
            if (skeleton.Members is not { Count: > 0 } members || members.Distinct(StringComparer.Ordinal).Count() != members.Count)
                return "invalid skeleton group";
            foreach (var member in members)
            {
                if (!SkeletonResourcePaths.TryClassify(member, out var classified) ||
                    entry.Paths.FirstOrDefault(path => path.GamePath == member) is not { Blob: not null })
                    return "invalid skeleton group";
                if ((classified.Role == SkeletonResourceRole.Deformer) != (member == SkeletonResourcePaths.DeformerGamePath))
                    return "invalid skeleton group";
            }
            if (skeleton.Deformer != members.Contains(SkeletonResourcePaths.DeformerGamePath, StringComparer.Ordinal))
                return "invalid skeleton group";
        }
        if (entry.SkeletonSummary.Count > 64 || entry.SkeletonSummary.Any(line => line is null || line.Length > 512))
            return "invalid skeleton summary";
        var counts = entry.Counts;
        if (counts.Redirects != redirects || counts.Swaps != entry.Paths.Count - redirects || counts.Files != blobs.Count ||
            counts.Bytes != blobs.Values.Sum() || counts.Vanilla < 0 || counts.Outside < 0 ||
            counts.Skeleton + counts.Physics + counts.Deformer + counts.Other != redirects)
            return "invalid counts";
        return null;
    }

    /// <summary>
    /// The profile fields by format: none for <see cref="HumanCatalogueEntry.CurrentFormat"/>
    /// (source <c>none</c>, as in M1); for <see cref="HumanCatalogueEntry.ProfileFormat"/>,
    /// source <c>owner-approved</c>, Customize+ 2.2.1.3 recorded (the version the read was gated on), canonical text (canonicalizing it again
    /// changes nothing), its hash, consistent counts with at least one bone on
    /// the captured skeleton (D-M3-10), and a skeleton summary the spawn can compare.
    /// </summary>
    private static string? ProfileProblem(HumanCatalogueEntry entry)
    {
        if (entry.Format == HumanCatalogueEntry.CurrentFormat)
        {
            if (entry.ProfileSource != HumanCatalogueEntry.NoProfile)
                return "unknown profile source";
            return entry.Profile is null && entry.ProfileHash is null && entry.ProfileCounts is null ? null : "a profile in an entry without one";
        }
        if (entry.ProfileSource != HumanCatalogueEntry.OwnerApproved)
            return "unknown profile source";
        if (entry.Versions?.CustomizePlus != HumanActorCustomizePlusStage.RequiredVersion)
            return $"a profile needs Customize+ {HumanActorCustomizePlusStage.RequiredVersion} recorded";
        if (entry.Profile is null || HumanCatalogueProfile.Canonicalize(entry.Profile, out var bones, out _) != entry.Profile ||
            entry.ProfileHash != HumanCatalogueEntry.HashOf(entry.Profile))
            return "invalid profile";
        if (entry.ProfileCounts is not { } counts || counts.Bones != bones.Count || counts.Base < 0 || counts.Other < 0 || counts.Absent < 0 ||
            counts.Base + counts.Other + counts.Absent != counts.Bones || counts.Base + counts.Other == 0)
            return "invalid profile counts";
        if (entry.SkeletonSummary is null || !HumanCatalogueSkeletonSummary.Comparable(entry.SkeletonSummary))
            return "a profile needs a comparable skeleton summary";
        return null;
    }

    /// <summary>A stored game path: 1 to 260 printable ASCII characters, relative, with no backslash or empty, "." or ".." segment.</summary>
    public static bool IsStoredGamePath(string? gamePath)
    {
        if (gamePath is not { Length: > 0 and <= MaximumGamePathLength } ||
            gamePath.Any(character => character is < '!' or > '~' or '\\') || gamePath[0] == '/' || gamePath.Contains(':'))
            return false;
        return gamePath.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumEntryBytes / 2)
            return false;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
#endif
