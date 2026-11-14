using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Appearance;

// The immutable,
// owner-authored appearance bundle as a pure contract. Nothing here touches
// Penumbra, Glamourer, Customize+, the game, the network or the disk; it is
// not on the object wire: protocol 7 human actors name a bundle only by its id.

/// <summary>
/// A SHA-256 content hash: exactly 64 lowercase hexadecimal characters. The
/// one way blobs and bundles are named; never a path or a name.
/// </summary>
public readonly record struct ContentHash
{
    public const int HexLength = 64;

    private ContentHash(string value) => Value = value;

    public string Value { get; }

    public static ContentHash Of(ReadOnlySpan<byte> content) =>
        new(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    /// <summary>Accepts only the canonical form (64 lowercase hex characters); never normalizes.</summary>
    public static bool TryParse(string? text, out ContentHash hash)
    {
        hash = default;
        if (text is null || text.Length != HexLength)
            return false;
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        hash = new ContentHash(text);
        return true;
    }

    public bool IsValid => Value is { Length: HexLength } && TryParse(Value, out _);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>One immutable blob a bundle refers to: its content hash and exact size in bytes.</summary>
public sealed record AppearanceBlobRef(ContentHash Hash, long Size)
{
    public bool IsValid => Hash.IsValid && Size is > 0 and <= AppearanceLimits.MaximumBlobBytes;
}

/// <summary>
/// One Penumbra file redirect: a game path the bundle replaces, and the blob
/// that replaces it. The game path is a normalized relative game path (see
/// <see cref="AppearanceGamePath"/>), never a local disk path.
/// </summary>
public sealed record AppearanceFileEntry(string GamePath, AppearanceBlobRef Content)
{
    /// <summary>
    /// Valid in a format-1 manifest (allowlist v1). A skeleton-group member is
    /// never valid here; use <see cref="IsValidFor"/> with the manifest's format.
    /// </summary>
    public bool IsValid => IsValidFor(AppearanceLimits.FirstFormatVersion);

    /// <summary>
    /// Valid in a manifest of <paramref name="formatVersion"/>: a canonical
    /// game path, a file type on that format's allowlist, and a valid blob.
    /// False for any format this build does not know.
    /// </summary>
    public bool IsValidFor(int formatVersion) =>
        AppearanceGamePath.IsValid(GamePath) &&
        AppearanceFileTypes.IsAllowed(GamePath, AppearanceFileTypes.VersionForFormat(formatVersion)) &&
        Content is { IsValid: true };
}

/// <summary>
/// Provisional appearance bundle limits; the
/// server and clients enforce the same values. Changing them needs review.
/// </summary>
public static class AppearanceLimits
{
    /// <summary>
    /// The newest bundle manifest format this build understands. Each manifest
    /// carries its own format (<see cref="AppearanceBundleManifest.FormatVersion"/>):
    /// 1 for a bundle without a skeleton group, 2 only for one with a group,
    /// 3 only for a human package (HV-6).
    /// </summary>
    public const int FormatVersion = 3;

    /// <summary>The format of every bundle without a skeleton group (allowlist v1); its bytes never change.</summary>
    public const int FirstFormatVersion = 1;

    /// <summary>The format of a bundle that carries a skeleton group (allowlist v2).</summary>
    public const int SkeletonGroupFormatVersion = 2;

    /// <summary>
    /// The format of a human package (HV-6, allowlist v3 = the v2 set): it
    /// carries the <c>human</c> member (facts and Glamourer reference), and a
    /// skeleton group only when it has skeleton resources.
    /// </summary>
    public const int HumanPackageFormatVersion = 3;

    /// <summary>Format 3 declared-size bounds; formats 1 and 2 are unchanged.</summary>
    public const long MaximumHumanGlamourerBytes = 1L * 1024 * 1024;
    public const long MaximumHumanPenumbraMetaBytes = 8L * 1024 * 1024;
    public const long MaximumHumanCustomizePlusBytes = 256L * 1024;
    public const long MaximumHumanFactsBytes = 256L * 1024;
    public const long MaximumHumanGlamourerReferenceBytes = 8L * 1024 * 1024;
    public const int MaximumFiles = 4096;
    public const long MaximumBlobBytes = 64L * 1024 * 1024;
    public const long MaximumBundleBytes = 1024L * 1024 * 1024;
    public const int MaximumGamePathLength = 260;
    /// <summary>
    /// The canonical manifest size limit. 2 MiB, because the largest manifest
    /// the other limits permit (4096 files with 260-character paths) does not
    /// reliably fit in 1 MiB.
    /// </summary>
    public const int MaximumManifestBytes = 2 * 1024 * 1024;
}

/// <summary>
/// The versioned, default-deny allowlist of file types a bundle may redirect.
/// Version 1 is the minimum for a human
/// appearance: models, materials and textures. Animation, timeline, sound,
/// shader, executable, script and archive formats are refused (among them
/// .pap, .tmb, .scd, .avfx and .shpk). Adding a type is an explicit
/// compatibility and safety decision: document why faithful human appearance
/// needs it, test it separately, and bump <see cref="Version"/> together with
/// <see cref="AppearanceLimits.FormatVersion"/>.
/// <para>
/// Version 2 (AM-4) adds the skeleton classes .sklb, .phyb and the racial
/// deformer, only for the exact path families
/// <see cref="SkeletonResourcePaths.TryClassify"/> recognises; an extension
/// alone never qualifies, and they are allowed only inside a format-2
/// skeleton group. Allowlist version N belongs to manifest format N.
/// </para>
/// </summary>
public static class AppearanceFileTypes
{
    /// <summary>The newest allowlist version; equal to <see cref="AppearanceLimits.FormatVersion"/>. Version 3 is exactly the version 2 set.</summary>
    public const int Version = 3;

    /// <summary>
    /// The allowlist v1 extensions. Unchanged by version 2: the
    /// one-argument <see cref="IsAllowed(string?)"/> and format-1 manifests use exactly this list.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } = [".mdl", ".mtrl", ".tex"];

    private static readonly IReadOnlyList<string> AllowedVersion2 = [".mdl", ".mtrl", ".tex", ".sklb", ".phyb", ".pbd"];

    /// <summary>
    /// The extensions an allowlist version can accept; empty for an unknown
    /// version. For version 2 the skeleton extensions count only on recognised paths.
    /// </summary>
    public static IReadOnlyList<string> AllowedFor(int version) => version switch
    {
        1 => Allowed,
        2 or 3 => AllowedVersion2,
        _ => [],
    };

    /// <summary>The allowlist version a manifest format uses (format N uses version N); 0 for an unknown format.</summary>
    public static int VersionForFormat(int formatVersion) =>
        formatVersion is AppearanceLimits.FirstFormatVersion or AppearanceLimits.SkeletonGroupFormatVersion
            or AppearanceLimits.HumanPackageFormatVersion ? formatVersion : 0;

    /// <summary>Allowlist v1: true only when the game path's file extension is exactly one of <see cref="Allowed"/>.</summary>
    public static bool IsAllowed(string? gamePath) => IsAllowed(gamePath, 1);

    /// <summary>
    /// True when <paramref name="version"/> allows the game path: version 1 by
    /// extension (<see cref="Allowed"/>); version 2 also a skeleton resource on
    /// a recognised path. False for any other version.
    /// </summary>
    public static bool IsAllowed(string? gamePath, int version) => version switch
    {
        1 => HasV1Extension(gamePath),
        2 or 3 => HasV1Extension(gamePath) || SkeletonResourcePaths.TryClassify(gamePath, out _),
        _ => false,
    };

    private static bool HasV1Extension(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath))
            return false;
        var file = gamePath[(gamePath.LastIndexOf('/') + 1)..];
        var dot = file.LastIndexOf('.');
        if (dot <= 0)
            return false;
        var extension = file[dot..];
        foreach (var allowed in Allowed)
        {
            if (string.Equals(extension, allowed, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Game-path rules for a file redirect: lowercase ASCII letters, digits, '_',
/// '-', '.' and '/' only; relative (no leading or trailing '/'); no empty,
/// "." or ".." segment; a file name with an extension. Anything else is
/// refused, never normalized.
/// </summary>
public static class AppearanceGamePath
{
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > AppearanceLimits.MaximumGamePathLength)
            return false;
        foreach (var c in path)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.' or '/'))
                return false;
        }
        var segments = path.Split('/');
        if (segments.Length < 2)
            return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        var file = segments[^1];
        var dot = file.LastIndexOf('.');
        return dot > 0 && dot < file.Length - 1;
    }
}

/// <summary>How a manifest may be read. The default reads format 1 only.</summary>
public enum AppearanceManifestReadOptions
{
    /// <summary>Format 1 only: a format-2 manifest is refused as an unsupported format.</summary>
    FormatOneOnly = 0,

    /// <summary>Format 1, and format 2 (a bundle with a skeleton group). Only explicitly gated callers use this.</summary>
    AllowSkeletonGroups = 1,

    /// <summary>Formats 1, 2 and 3 (a human package, HV-6). Only explicitly gated callers use this.</summary>
    AllowHumanPackages = 2,
}

/// <summary>
/// Format 3 only: the human package member. <see cref="Facts"/> is the
/// canonical <c>rsc-human-facts-1</c> JSON (race, clan, gender, body type,
/// the versions at capture, the skeleton summary and the profile counts);
/// <see cref="GlamourerReference"/> is the structured Glamourer reference.
/// Both are ordinary blobs of the bundle. The server checks only their
/// declared sizes; every receiver validates their content.
/// </summary>
public sealed record AppearanceHumanPackage(AppearanceBlobRef Facts, AppearanceBlobRef GlamourerReference)
{
    public bool IsValid =>
        Facts is { IsValid: true } && Facts.Size <= AppearanceLimits.MaximumHumanFactsBytes &&
        GlamourerReference is { IsValid: true } && GlamourerReference.Size <= AppearanceLimits.MaximumHumanGlamourerReferenceBytes;
}

/// <summary>
/// An immutable, owner-authored appearance bundle (manifest format 1, or
/// format 2 when it carries a skeleton group). It is
/// identified by <see cref="BundleId"/>, the SHA-256 of its canonical bytes,
/// so it can never change without becoming a different bundle, and the
/// owner is part of that identity. Recipients may draw it but never edit it.
/// <list type="bullet">
/// <item><see cref="Glamourer"/>: the Glamourer state text (required).</item>
/// <item><see cref="PenumbraMeta"/>: Penumbra meta manipulations (optional).</item>
/// <item><see cref="CustomizePlus"/>: a Customize+ profile (optional; Customize+ stays optional).</item>
/// <item><see cref="Files"/>: Penumbra file redirects, sorted by game path (ordinal), unique.</item>
/// <item><see cref="SkeletonGroupTargetRace"/>: format 2 only, the playable race the
/// skeleton group is for. The group's members are exactly the files whose
/// paths are skeleton resources (<see cref="SkeletonResourcePaths.TryClassify"/>).</item>
/// </list>
/// A bundle without a skeleton group is always format 1, byte for byte as
/// before format 2 existed, so its <see cref="BundleId"/> never changes.
/// Names, labels and timestamps are catalogue metadata, never part of the bundle.
/// </summary>
public sealed record AppearanceBundleManifest
{
    private AppearanceBundleManifest(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IReadOnlyList<AppearanceFileEntry> files,
        SkeletonResourceGroup? skeletonGroup,
        AppearanceHumanPackage? human)
    {
        OwnerId = ownerId;
        Glamourer = glamourer;
        PenumbraMeta = penumbraMeta;
        CustomizePlus = customizePlus;
        Files = files;
        SkeletonGroup = skeletonGroup;
        Human = human;
        FormatVersion = FormatFor(skeletonGroup is not null, human is not null);
        CanonicalBytes = WriteCanonical();
        BundleId = ContentHash.Of(CanonicalBytes);
    }

    /// <summary>This manifest's own format: 1 without a skeleton group, 2 with one, 3 for a human package.</summary>
    public int FormatVersion { get; }
    public RscUserId OwnerId { get; }
    public AppearanceBlobRef Glamourer { get; }
    public AppearanceBlobRef? PenumbraMeta { get; }
    public AppearanceBlobRef? CustomizePlus { get; }
    public IReadOnlyList<AppearanceFileEntry> Files { get; }

    /// <summary>Formats 2 and 3: the skeleton group (its target race and its members, which are also in <see cref="Files"/>); null in format 1, optional in format 3.</summary>
    public SkeletonResourceGroup? SkeletonGroup { get; }

    /// <summary>Format 3 only: the human package member; null in formats 1 and 2.</summary>
    public AppearanceHumanPackage? Human { get; }

    /// <summary>Formats 2 and 3: the playable race code the skeleton group is for; null without a group.</summary>
    public ushort? SkeletonGroupTargetRace => SkeletonGroup?.RaceCode;

    /// <summary>The canonical UTF-8 JSON of this manifest (the only accepted form).</summary>
    public byte[] CanonicalBytes { get; }

    /// <summary>The SHA-256 of <see cref="CanonicalBytes"/>.</summary>
    public ContentHash BundleId { get; }

    /// <summary>Every distinct blob this bundle needs (each hash once).</summary>
    public IReadOnlyList<AppearanceBlobRef> Blobs =>
        AllBlobs(Glamourer, PenumbraMeta, CustomizePlus, Files, Human)
            .GroupBy(blob => blob.Hash)
            .Select(group => group.First())
            .ToArray();

    /// <summary>Sum of the sizes of the distinct blobs.</summary>
    public long TotalBytes => Blobs.Sum(blob => blob.Size);

    /// <summary>
    /// Builds a format-1 manifest, sorting the files by game path. Returns null with a
    /// reason when anything is invalid: owner, blobs, game paths, duplicate
    /// paths, a blob hash reused with another size, or a limit.
    /// </summary>
    public static AppearanceBundleManifest? TryCreate(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        out string? error) =>
        TryCreate(ownerId, glamourer, penumbraMeta, customizePlus, files, null, out error);

    /// <summary>
    /// Builds a manifest. Without <paramref name="skeletonGroupTargetRace"/> it
    /// is format 1, exactly as the overload without it.
    /// With it, it is format 2: the target must be a playable race code, the
    /// files must hold at least one skeleton resource, and those resources
    /// must form one whole group for that race (<see cref="SkeletonResourceGroupAssembler.AssembleDeclared"/>).
    /// </summary>
    public static AppearanceBundleManifest? TryCreate(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        ushort? skeletonGroupTargetRace,
        out string? error)
    {
        error = Validate(ownerId, glamourer, penumbraMeta, customizePlus, files, skeletonGroupTargetRace, null, out var sorted, out var group);
        if (error is not null)
            return null;
        try
        {
            return new AppearanceBundleManifest(ownerId, glamourer, penumbraMeta, customizePlus, sorted!, group, null);
        }
        catch (ArgumentException)
        {
            // Text the canonical writer cannot encode (e.g. a lone surrogate in the owner id).
            error = "The owner is invalid.";
            return null;
        }
    }

    /// <summary>
    /// Builds a format-3 human package (HV-6). The skeleton group is optional:
    /// with <paramref name="skeletonGroupTargetRace"/> the skeleton-resource
    /// files must form one whole group for that race, as in format 2; without
    /// it, no file may be a skeleton resource. Allowlist v3 (the v2 set) and
    /// the format-3 declared-size bounds apply.
    /// </summary>
    public static AppearanceBundleManifest? TryCreateHumanPackage(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        ushort? skeletonGroupTargetRace,
        AppearanceHumanPackage human,
        out string? error)
    {
        if (human is null)
        {
            error = "The human package member is missing.";
            return null;
        }
        error = Validate(ownerId, glamourer, penumbraMeta, customizePlus, files, skeletonGroupTargetRace, human, out var sorted, out var group);
        if (error is not null)
            return null;
        try
        {
            return new AppearanceBundleManifest(ownerId, glamourer, penumbraMeta, customizePlus, sorted!, group, human);
        }
        catch (ArgumentException)
        {
            // Text the canonical writer cannot encode (e.g. a lone surrogate in the owner id).
            error = "The owner is invalid.";
            return null;
        }
    }

    /// <summary>
    /// Reads a format-1 manifest only in its exact canonical form: the bytes must parse,
    /// validate, and re-serialize to exactly the same bytes. Any other form
    /// (whitespace, member order, unknown or missing members, unsorted files,
    /// another format version, including format 2) is refused, never normalized.
    /// </summary>
    public static AppearanceBundleManifest? TryParse(ReadOnlySpan<byte> utf8, out string? error) =>
        TryParse(utf8, AppearanceManifestReadOptions.FormatOneOnly, out error);

    /// <summary>
    /// As <see cref="TryParse(ReadOnlySpan{byte}, out string?)"/>; with
    /// <see cref="AppearanceManifestReadOptions.AllowSkeletonGroups"/> a
    /// canonical format-2 manifest is also read; with
    /// <see cref="AppearanceManifestReadOptions.AllowHumanPackages"/>, formats 2 and 3.
    /// </summary>
    public static AppearanceBundleManifest? TryParse(ReadOnlySpan<byte> utf8, AppearanceManifestReadOptions options, out string? error)
    {
        error = null;
        if (utf8.Length == 0 || utf8.Length > AppearanceLimits.MaximumManifestBytes)
        {
            error = "The manifest is empty or too large.";
            return null;
        }
        AppearanceBundleManifest? manifest;
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            manifest = FromDocument(document.RootElement, options, out error);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            error = "The manifest is not valid JSON of the expected shape.";
            return null;
        }
        if (manifest is null)
            return null;
        if (!utf8.SequenceEqual(manifest.CanonicalBytes))
        {
            error = "The manifest is not in canonical form.";
            return null;
        }
        return manifest;
    }

    private static AppearanceBundleManifest? FromDocument(JsonElement root, AppearanceManifestReadOptions options, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The manifest must be a JSON object.";
            return null;
        }
        var format = root.GetProperty("formatVersion").GetInt32();
        var readable = format == AppearanceLimits.FirstFormatVersion ||
            (format == AppearanceLimits.SkeletonGroupFormatVersion &&
                options is AppearanceManifestReadOptions.AllowSkeletonGroups or AppearanceManifestReadOptions.AllowHumanPackages) ||
            (format == AppearanceLimits.HumanPackageFormatVersion && options == AppearanceManifestReadOptions.AllowHumanPackages);
        if (!readable)
        {
            error = "Unsupported manifest format version.";
            return null;
        }
        var owner = new RscUserId(root.GetProperty("ownerId").GetString() ?? string.Empty);
        var glamourer = ReadBlob(root.GetProperty("glamourer"));
        var meta = ReadOptionalBlob(root.GetProperty("penumbraMeta"));
        var customize = ReadOptionalBlob(root.GetProperty("customizePlus"));
        var files = new List<AppearanceFileEntry>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
            files.Add(new AppearanceFileEntry(file.GetProperty("gamePath").GetString() ?? string.Empty,
                ReadBlob(file) ?? new AppearanceBlobRef(default, 0)));
        if (glamourer is null)
        {
            error = "The manifest's Glamourer state is missing.";
            return null;
        }
        ushort? target = null;
        if (format == AppearanceLimits.SkeletonGroupFormatVersion)
        {
            // Format 2 names its group's target race; the group itself is the
            // skeleton-resource files. A missing or malformed member is refused.
            var group = root.GetProperty("skeletonGroup");
            if (group.ValueKind != JsonValueKind.Object || !group.GetProperty("targetRace").TryGetUInt16(out var race))
            {
                error = "The manifest's skeleton group is invalid.";
                return null;
            }
            target = race;
        }
        if (format == AppearanceLimits.HumanPackageFormatVersion)
        {
            // Format 3: the group is optional (null without skeleton resources);
            // the human member is required.
            var group = root.GetProperty("skeletonGroup");
            if (group.ValueKind != JsonValueKind.Null)
            {
                if (group.ValueKind != JsonValueKind.Object || !group.GetProperty("targetRace").TryGetUInt16(out var race))
                {
                    error = "The manifest's skeleton group is invalid.";
                    return null;
                }
                target = race;
            }
            var human = root.GetProperty("human");
            var facts = human.ValueKind == JsonValueKind.Object ? ReadBlob(human.GetProperty("facts")) : null;
            var reference = human.ValueKind == JsonValueKind.Object ? ReadBlob(human.GetProperty("glamourerReference")) : null;
            if (facts is null || reference is null)
            {
                error = "The manifest's human package member is invalid.";
                return null;
            }
            return TryCreateHumanPackage(owner, glamourer, meta, customize, files, target, new AppearanceHumanPackage(facts, reference), out error);
        }
        // The canonical comparison afterwards refuses any member the format does not have.
        return TryCreate(owner, glamourer, meta, customize, files, target, out error);
    }

    private static AppearanceBlobRef? ReadOptionalBlob(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : ReadBlob(element);

    private static AppearanceBlobRef? ReadBlob(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        return ContentHash.TryParse(element.GetProperty("hash").GetString(), out var hash)
            ? new AppearanceBlobRef(hash, element.GetProperty("size").GetInt64())
            : new AppearanceBlobRef(default, 0);
    }

    private static string? Validate(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        ushort? skeletonGroupTargetRace,
        AppearanceHumanPackage? human,
        out IReadOnlyList<AppearanceFileEntry>? sorted,
        out SkeletonResourceGroup? group)
    {
        sorted = null;
        group = null;
        var format = FormatFor(skeletonGroupTargetRace is not null, human is not null);
        var allowlist = AppearanceFileTypes.VersionForFormat(format);
        if (!ownerId.IsValid)
            return "The owner is invalid.";
        if (glamourer is not { IsValid: true })
            return "The Glamourer state reference is invalid.";
        if (penumbraMeta is { IsValid: false })
            return "The Penumbra meta reference is invalid.";
        if (customizePlus is { IsValid: false })
            return "The Customize+ profile reference is invalid.";
        if (human is not null)
        {
            // Format 3 declared-size bounds. The server checks only these and the group.
            if (!human.IsValid)
                return "The human package member is invalid or too large.";
            if (glamourer.Size > AppearanceLimits.MaximumHumanGlamourerBytes)
                return "The Glamourer state is too large for a human package.";
            if (penumbraMeta is { } meta && meta.Size > AppearanceLimits.MaximumHumanPenumbraMetaBytes)
                return "The Penumbra meta manipulations are too large for a human package.";
            if (customizePlus is { } profile && profile.Size > AppearanceLimits.MaximumHumanCustomizePlusBytes)
                return "The Customize+ profile is too large for a human package.";
        }
        if (files is null)
            return "The file list is missing.";
        var list = files.ToList();
        if (list.Count > AppearanceLimits.MaximumFiles)
            return "Too many files.";
        foreach (var file in list)
        {
            if (file is null || !AppearanceGamePath.IsValid(file.GamePath) || file.Content is not { IsValid: true })
                return "A file redirect is invalid.";
            if (!AppearanceFileTypes.IsAllowed(file.GamePath, allowlist))
                return allowlist == 1
                    ? "A file type is not allowed (allowed: " + string.Join(", ", AppearanceFileTypes.Allowed) + ")."
                    : "A file type is not allowed (allowed: " + string.Join(", ", AppearanceFileTypes.AllowedFor(allowlist)) +
                      "; skeleton resources only on recognised paths).";
        }
        var ordered = list.OrderBy(file => file.GamePath, StringComparer.Ordinal).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].GamePath, ordered[index].GamePath, StringComparison.Ordinal))
                return "A game path appears more than once.";
        }
        // One hash names one content: the same hash with two sizes is inconsistent.
        var blobs = AllBlobs(glamourer, penumbraMeta, customizePlus, ordered, human);
        long total = 0;
        foreach (var blobGroup in blobs.GroupBy(blob => blob.Hash))
        {
            if (blobGroup.Select(blob => blob.Size).Distinct().Count() != 1)
                return "A blob hash is listed with different sizes.";
            total += blobGroup.First().Size;
            if (total > AppearanceLimits.MaximumBundleBytes)
                return "The bundle is too large.";
        }
        if (skeletonGroupTargetRace is { } target)
        {
            // Format 2: the skeleton-resource files are the group, whole and for one playable race.
            if (!SkeletonResourcePaths.IsPlayerRaceCode(target))
                return "The skeleton group's target race is not a playable race.";
            var members = ordered.Where(file => SkeletonResourcePaths.TryClassify(file.GamePath, out _)).ToArray();
            if (members.Length == 0)
                return "A skeleton group needs at least one skeleton resource (a bundle without one is format 1).";
            var assembled = SkeletonResourceGroupAssembler.AssembleDeclared(target, members);
            if (assembled.Group is null)
                return $"The skeleton group is invalid ({assembled.Refusal}).";
            group = assembled.Group;
        }
        else if (human is not null && ordered.Any(file => SkeletonResourcePaths.TryClassify(file.GamePath, out _)))
        {
            // Format 3 without a group: a skeleton resource never travels outside one.
            return "A skeleton resource needs a skeleton group.";
        }
        sorted = ordered;
        return null;
    }

    private static int FormatFor(bool hasSkeletonGroup, bool isHumanPackage) =>
        isHumanPackage ? AppearanceLimits.HumanPackageFormatVersion
        : hasSkeletonGroup ? AppearanceLimits.SkeletonGroupFormatVersion
        : AppearanceLimits.FirstFormatVersion;

    private static IEnumerable<AppearanceBlobRef> AllBlobs(
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        AppearanceHumanPackage? human)
    {
        yield return glamourer;
        if (penumbraMeta is not null)
            yield return penumbraMeta;
        if (customizePlus is not null)
            yield return customizePlus;
        foreach (var file in files)
            yield return file.Content;
        if (human is not null)
        {
            // Format 3: the facts and the Glamourer reference are ordinary
            // blobs, so upload, quotas, read authorization, collection and the
            // consistency check all cover them.
            yield return human.Facts;
            yield return human.GlamourerReference;
        }
    }

    private byte[] WriteCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("ownerId", OwnerId.Value);
            writer.WritePropertyName("glamourer");
            WriteBlob(writer, Glamourer);
            writer.WritePropertyName("penumbraMeta");
            WriteOptionalBlob(writer, PenumbraMeta);
            writer.WritePropertyName("customizePlus");
            WriteOptionalBlob(writer, CustomizePlus);
            writer.WriteStartArray("files");
            foreach (var file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("gamePath", file.GamePath);
                writer.WriteString("hash", file.Content.Hash.Value);
                writer.WriteNumber("size", file.Content.Size);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (Human is { } human)
            {
                // Format 3: the group member is always present (null without
                // skeleton resources), followed by the human member.
                if (SkeletonGroup is { } humanGroup)
                {
                    writer.WriteStartObject("skeletonGroup");
                    writer.WriteNumber("targetRace", humanGroup.RaceCode);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("skeletonGroup");
                }
                writer.WriteStartObject("human");
                writer.WritePropertyName("facts");
                WriteBlob(writer, human.Facts);
                writer.WritePropertyName("glamourerReference");
                WriteBlob(writer, human.GlamourerReference);
                writer.WriteEndObject();
            }
            else if (SkeletonGroup is { } group)
            {
                // Format 2 only; format 1 has no such member and its bytes are unchanged.
                writer.WriteStartObject("skeletonGroup");
                writer.WriteNumber("targetRace", group.RaceCode);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteOptionalBlob(Utf8JsonWriter writer, AppearanceBlobRef? blob)
    {
        if (blob is null)
            writer.WriteNullValue();
        else
            WriteBlob(writer, blob);
    }

    private static void WriteBlob(Utf8JsonWriter writer, AppearanceBlobRef blob)
    {
        writer.WriteStartObject();
        writer.WriteString("hash", blob.Hash.Value);
        writer.WriteNumber("size", blob.Size);
        writer.WriteEndObject();
    }
}
