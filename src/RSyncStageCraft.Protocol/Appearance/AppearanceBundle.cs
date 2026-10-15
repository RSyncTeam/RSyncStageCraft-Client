using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Appearance;

// The immutable,
// owner-authored appearance bundle as a pure contract. Nothing here touches
// Penumbra, Glamourer, Customize+, the game, the network or the disk; it is
// not on the wire yet (protocol 6 is unchanged).

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
    public bool IsValid => AppearanceGamePath.IsValid(GamePath) && AppearanceFileTypes.IsAllowed(GamePath) && Content is { IsValid: true };
}

/// <summary>
/// Provisional appearance bundle limits; the
/// server and clients enforce the same values. Changing them needs review.
/// </summary>
public static class AppearanceLimits
{
    /// <summary>The bundle manifest format this build writes and reads.</summary>
    public const int FormatVersion = 1;
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
/// </summary>
public static class AppearanceFileTypes
{
    public const int Version = 1;

    public static IReadOnlyList<string> Allowed { get; } = [".mdl", ".mtrl", ".tex"];

    /// <summary>True only when the game path's file extension is exactly one of <see cref="Allowed"/>.</summary>
    public static bool IsAllowed(string? gamePath)
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

/// <summary>
/// An immutable, owner-authored appearance bundle (manifest format 1). It is
/// identified by <see cref="BundleId"/>, the SHA-256 of its canonical bytes,
/// so it can never change without becoming a different bundle, and the
/// owner is part of that identity. Recipients may draw it but never edit it.
/// <list type="bullet">
/// <item><see cref="Glamourer"/>: the Glamourer state text (required).</item>
/// <item><see cref="PenumbraMeta"/>: Penumbra meta manipulations (optional).</item>
/// <item><see cref="CustomizePlus"/>: a Customize+ profile (optional; Customize+ stays optional).</item>
/// <item><see cref="Files"/>: Penumbra file redirects, sorted by game path (ordinal), unique.</item>
/// </list>
/// Names, labels and timestamps are catalogue metadata, never part of the bundle.
/// </summary>
public sealed record AppearanceBundleManifest
{
    private AppearanceBundleManifest(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IReadOnlyList<AppearanceFileEntry> files)
    {
        OwnerId = ownerId;
        Glamourer = glamourer;
        PenumbraMeta = penumbraMeta;
        CustomizePlus = customizePlus;
        Files = files;
        CanonicalBytes = WriteCanonical();
        BundleId = ContentHash.Of(CanonicalBytes);
    }

    public int FormatVersion => AppearanceLimits.FormatVersion;
    public RscUserId OwnerId { get; }
    public AppearanceBlobRef Glamourer { get; }
    public AppearanceBlobRef? PenumbraMeta { get; }
    public AppearanceBlobRef? CustomizePlus { get; }
    public IReadOnlyList<AppearanceFileEntry> Files { get; }

    /// <summary>The canonical UTF-8 JSON of this manifest (the only accepted form).</summary>
    public byte[] CanonicalBytes { get; }

    /// <summary>The SHA-256 of <see cref="CanonicalBytes"/>.</summary>
    public ContentHash BundleId { get; }

    /// <summary>Every distinct blob this bundle needs (each hash once).</summary>
    public IReadOnlyList<AppearanceBlobRef> Blobs =>
        AllBlobs(Glamourer, PenumbraMeta, CustomizePlus, Files)
            .GroupBy(blob => blob.Hash)
            .Select(group => group.First())
            .ToArray();

    /// <summary>Sum of the sizes of the distinct blobs.</summary>
    public long TotalBytes => Blobs.Sum(blob => blob.Size);

    /// <summary>
    /// Builds a manifest, sorting the files by game path. Returns null with a
    /// reason when anything is invalid: owner, blobs, game paths, duplicate
    /// paths, a blob hash reused with another size, or a limit.
    /// </summary>
    public static AppearanceBundleManifest? TryCreate(
        RscUserId ownerId,
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files,
        out string? error)
    {
        error = Validate(ownerId, glamourer, penumbraMeta, customizePlus, files, out var sorted);
        if (error is not null)
            return null;
        try
        {
            return new AppearanceBundleManifest(ownerId, glamourer, penumbraMeta, customizePlus, sorted!);
        }
        catch (ArgumentException)
        {
            // Text the canonical writer cannot encode (e.g. a lone surrogate in the owner id).
            error = "The owner is invalid.";
            return null;
        }
    }

    /// <summary>
    /// Reads a manifest only in its exact canonical form: the bytes must parse,
    /// validate, and re-serialize to exactly the same bytes. Any other form
    /// (whitespace, member order, unknown or missing members, unsorted files,
    /// another format version) is refused, never normalized.
    /// </summary>
    public static AppearanceBundleManifest? TryParse(ReadOnlySpan<byte> utf8, out string? error)
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
            manifest = FromDocument(document.RootElement, out error);
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

    private static AppearanceBundleManifest? FromDocument(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The manifest must be a JSON object.";
            return null;
        }
        if (root.GetProperty("formatVersion").GetInt32() != AppearanceLimits.FormatVersion)
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
        return TryCreate(owner, glamourer, meta, customize, files, out error);
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
        out IReadOnlyList<AppearanceFileEntry>? sorted)
    {
        sorted = null;
        if (!ownerId.IsValid)
            return "The owner is invalid.";
        if (glamourer is not { IsValid: true })
            return "The Glamourer state reference is invalid.";
        if (penumbraMeta is { IsValid: false })
            return "The Penumbra meta reference is invalid.";
        if (customizePlus is { IsValid: false })
            return "The Customize+ profile reference is invalid.";
        if (files is null)
            return "The file list is missing.";
        var list = files.ToList();
        if (list.Count > AppearanceLimits.MaximumFiles)
            return "Too many files.";
        foreach (var file in list)
        {
            if (file is null || !AppearanceGamePath.IsValid(file.GamePath) || file.Content is not { IsValid: true })
                return "A file redirect is invalid.";
            if (!AppearanceFileTypes.IsAllowed(file.GamePath))
                return "A file type is not allowed (allowed: " + string.Join(", ", AppearanceFileTypes.Allowed) + ").";
        }
        var ordered = list.OrderBy(file => file.GamePath, StringComparer.Ordinal).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].GamePath, ordered[index].GamePath, StringComparison.Ordinal))
                return "A game path appears more than once.";
        }
        // One hash names one content: the same hash with two sizes is inconsistent.
        var blobs = AllBlobs(glamourer, penumbraMeta, customizePlus, ordered);
        long total = 0;
        foreach (var group in blobs.GroupBy(blob => blob.Hash))
        {
            if (group.Select(blob => blob.Size).Distinct().Count() != 1)
                return "A blob hash is listed with different sizes.";
            total += group.First().Size;
            if (total > AppearanceLimits.MaximumBundleBytes)
                return "The bundle is too large.";
        }
        sorted = ordered;
        return null;
    }

    private static IEnumerable<AppearanceBlobRef> AllBlobs(
        AppearanceBlobRef glamourer,
        AppearanceBlobRef? penumbraMeta,
        AppearanceBlobRef? customizePlus,
        IEnumerable<AppearanceFileEntry> files)
    {
        yield return glamourer;
        if (penumbraMeta is not null)
            yield return penumbraMeta;
        if (customizePlus is not null)
            yield return customizePlus;
        foreach (var file in files)
            yield return file.Content;
    }

    private byte[] WriteCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", AppearanceLimits.FormatVersion);
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
