using System.Globalization;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>Game file paths of a housing item, derived from its local model key.</summary>
public static class FurnitureModelPaths
{
    public static string Folder(FurnitureSource source, ushort modelKey) =>
        $"bgcommon/hou/{Area(source)}/general/{Key(modelKey)}";

    /// <summary>The item's layout (shared group): every part the game places for it.</summary>
    public static string Layout(FurnitureSource source, ushort modelKey) =>
        $"{Folder(source, modelKey)}/asset/{Prefix(source)}_b0_m{Key(modelKey)}.sgb";

    /// <summary>The item's main model.</summary>
    public static string MainModel(FurnitureSource source, ushort modelKey) =>
        $"{Folder(source, modelKey)}/bgparts/{Prefix(source)}_b0_m{Key(modelKey)}.mdl";

    private static string Area(FurnitureSource source) =>
        source == FurnitureSource.Outdoor ? "outdoor" : "indoor";

    private static string Prefix(FurnitureSource source) =>
        source == FurnitureSource.Outdoor ? "gar" : "fun";

    private static string Key(ushort modelKey) =>
        modelKey.ToString("D4", CultureInfo.InvariantCulture);
}

public enum FurnitureLayoutInstanceKind
{
    /// <summary>A plain background model (an .mdl).</summary>
    Model,
    /// <summary>
    /// A non-visual helper the game uses for interaction or collision (sit
    /// and target markers, collision boxes, clickable ranges, ...). Nothing
    /// is drawn for it, so leaving it out loses nothing visible.
    /// </summary>
    Helper,
    /// <summary>Anything that is or may be visible or audible: a light, effect, sound, nested group, animation, ...</summary>
    Other,
}

/// <summary>One entry of an item's layout file, with its local transform.</summary>
public readonly record struct FurnitureLayoutInstance(
    FurnitureLayoutInstanceKind Kind,
    string? AssetPath,
    Vector3State Translation,
    Vector3State Rotation,
    Vector3State Scale,
    string Type = "")
{
    /// <summary>The entry's type as the layout names it (for reports).</summary>
    public string TypeName => Type.Length > 0 ? Type : Kind.ToString();
}

/// <summary>Local, read-only access to the client's own game files.</summary>
public interface IFurnitureAssetReader
{
    bool FileExists(string gamePath);

    /// <summary>The layout's instances, or null if it cannot be read.</summary>
    IReadOnlyList<FurnitureLayoutInstance>? ReadLayout(string gamePath);

    /// <summary>
    /// The item's default housing stain from its layout (the stain the game
    /// shows when the item is not dyed), or 0 when it has none.
    /// </summary>
    byte ReadDefaultStain(string gamePath) => 0;
}

public enum FurnitureSupportStatus
{
    Supported,
    InvalidIdentity,
    NotInCatalog,
    Ambiguous,
    LayoutUnreadable,
    /// <summary>More than one part, or parts other than one model (lights, effects, ...).</summary>
    Multipart,
    /// <summary>The single model is offset, rotated or scaled within the item.</summary>
    TransformedPart,
    ModelMissing,
}

public sealed record FurnitureSupport(
    FurnitureSupportStatus Status,
    FurnitureCatalogRow? Row = null,
    string? ModelPath = null,
    string? Layout = null)
{
    /// <summary>What blocks the item, e.g. "2 BG, LayLight" (empty when supported).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The item's own default stain from its layout (0 = none): what the game
    /// shows for the undyed item. RSC's Default (stain 0) draws with it.
    /// </summary>
    public byte DefaultStain { get; init; }

    public bool IsSupported => Status == FurnitureSupportStatus.Supported && ModelPath is not null;

    /// <summary>The multipart text a player sees; the parts go to the log.</summary>
    public const string MultipartText =
        "Has several parts (for example a built-in light); this version draws single-model items only.";

    /// <summary>The missing-model text a player sees.</summary>
    public const string ModelMissingText =
        "Its model file is missing on this client; repair the game files in the launcher.";

    /// <summary>
    /// For the log: the player text, except that a multipart item names its
    /// parts (technical detail kept out of the player text).
    /// </summary>
    public string DescribeForLog() => Status == FurnitureSupportStatus.Multipart && Detail is { Length: > 0 } parts
        ? $"Multipart item: {parts}"
        : Status == FurnitureSupportStatus.Multipart
            ? "Multipart item"
            : Describe();

    public string Describe() => Status switch
    {
        FurnitureSupportStatus.Supported => "Supported",
        FurnitureSupportStatus.InvalidIdentity => "Not a furniture identity",
        FurnitureSupportStatus.NotInCatalog => "Unknown item on this client",
        FurnitureSupportStatus.Ambiguous => "Item maps to several models",
        FurnitureSupportStatus.LayoutUnreadable => "Item layout could not be read",
        FurnitureSupportStatus.Multipart => MultipartText,
        FurnitureSupportStatus.TransformedPart => "Item model is offset within the item (not supported yet)",
        FurnitureSupportStatus.ModelMissing => ModelMissingText,
        _ => Status.ToString(),
    };
}

/// <summary>
/// Decides whether an item can be drawn as exactly one model without losing
/// anything the game would show. Only such items are spawned: the item's layout
/// must hold exactly one plain model at the item's origin (no other parts, no
/// lights or effects, no offset, rotation or scale). Anything else is
/// reported as unsupported instead of silently drawing only part of it.
/// Results are cached per identity (the game files do not change while the
/// client runs).
/// </summary>
public sealed class FurnitureSupportResolver
{
    public const int MaximumCachedResults = 8192;
    private const float Tolerance = 1e-4f;

    private readonly FurnitureCatalog catalog;
    private readonly IFurnitureAssetReader assets;
    private readonly object gate = new();
    private readonly Dictionary<string, FurnitureSupport> cache = new(StringComparer.Ordinal);

    public FurnitureSupportResolver(FurnitureCatalog catalog, IFurnitureAssetReader assets)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assets);
        this.catalog = catalog;
        this.assets = assets;
    }

    public FurnitureCatalog Catalog => catalog;

    public FurnitureSupport Resolve(string? identity)
    {
        if (identity is null || !FurnitureIdentity.IsCanonical(identity))
            return new(FurnitureSupportStatus.InvalidIdentity);
        lock (gate)
        {
            if (cache.TryGetValue(identity, out var cached))
                return cached;
        }

        var resolved = ResolveUncached(identity);
        lock (gate)
        {
            if (cache.Count < MaximumCachedResults)
                cache.TryAdd(identity, resolved);
        }
        return resolved;
    }

    private FurnitureSupport ResolveUncached(string identity)
    {
        var lookup = catalog.Find(identity);
        switch (lookup.Status)
        {
            case FurnitureLookupStatus.Found:
                break;
            case FurnitureLookupStatus.Ambiguous:
                return new(FurnitureSupportStatus.Ambiguous);
            case FurnitureLookupStatus.NotInCatalog:
                return new(FurnitureSupportStatus.NotInCatalog);
            default:
                return new(FurnitureSupportStatus.InvalidIdentity);
        }

        var row = lookup.Row!;
        IReadOnlyList<FurnitureLayoutInstance>? layout;
        try
        {
            layout = assets.ReadLayout(FurnitureModelPaths.Layout(row.Source, row.ModelKey));
        }
        catch (Exception)
        {
            layout = null;
        }
        if (layout is null)
            return new(FurnitureSupportStatus.LayoutUnreadable, row);
        var summary = Summarize(layout);
        // Non-visual helpers (sit markers, collision, ...) draw nothing and
        // are left out; every other entry counts as a part.
        var parts = layout.Where(entry => entry.Kind != FurnitureLayoutInstanceKind.Helper).ToArray();
        if (parts.Length != 1 || parts[0].Kind != FurnitureLayoutInstanceKind.Model)
        {
            return new(FurnitureSupportStatus.Multipart, row, Layout: summary)
            {
                Detail = parts.Length == 0 ? "no model" : Summarize(parts),
            };
        }

        var part = parts[0];
        var path = part.AssetPath?.Trim();
        if (string.IsNullOrEmpty(path) ||
            !path.StartsWith("bgcommon/hou/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return new(FurnitureSupportStatus.Multipart, row, Layout: summary)
            {
                Detail = "model outside the housing folder",
            };
        }
        if (!IsIdentity(part))
            return new(FurnitureSupportStatus.TransformedPart, row, Layout: summary);

        bool exists;
        try
        {
            exists = assets.FileExists(path);
        }
        catch (Exception)
        {
            exists = false;
        }
        if (!exists)
            return new(FurnitureSupportStatus.ModelMissing, row, Layout: summary);
        byte defaultStain;
        try
        {
            defaultStain = assets.ReadDefaultStain(FurnitureModelPaths.Layout(row.Source, row.ModelKey));
        }
        catch (Exception)
        {
            defaultStain = 0;
        }
        return new(FurnitureSupportStatus.Supported, row, path, summary) { DefaultStain = defaultStain };
    }

    /// <summary>"BG, 2 ChairMarker": entry types with counts, in first-seen order.</summary>
    private static string Summarize(IEnumerable<FurnitureLayoutInstance> entries) =>
        string.Join(", ", entries
            .GroupBy(entry => entry.TypeName)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Count()} {group.Key}"));

    private static bool IsIdentity(FurnitureLayoutInstance part) =>
        Near(part.Translation, 0) && Near(part.Rotation, 0) && Near(part.Scale, 1);

    private static bool Near(Vector3State value, float expected) =>
        value.IsFinite &&
        MathF.Abs(value.X - expected) <= Tolerance &&
        MathF.Abs(value.Y - expected) <= Tolerance &&
        MathF.Abs(value.Z - expected) <= Tolerance;
}
