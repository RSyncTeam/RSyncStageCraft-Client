using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>
/// One housing item row from the game's own data sheets (HousingFurniture for
/// indoor, HousingYardObject for outdoor), read locally from the client's
/// files. Only <see cref="ItemId"/> and <see cref="Source"/> ever cross the
/// wire; the model key, name and icon stay on this client.
/// <see cref="IconId"/> is the item's game icon (0 when it has none); it is
/// used for display only. <see cref="DyeCount"/> is the item's number of dye
/// channels (0 = cannot be dyed); only the primary one is used.
/// </summary>
public sealed record FurnitureCatalogRow(
    FurnitureSource Source,
    uint ItemId,
    ushort ModelKey,
    string Name,
    uint IconId = 0,
    byte DyeCount = 0)
{
    public bool IsDyeable => DyeCount > 0;

    public string Identity => FurnitureIdentity.Format(Source, ItemId);
}

public enum FurnitureLookupStatus
{
    Found,
    /// <summary>The identity is not in the canonical wire form.</summary>
    InvalidIdentity,
    /// <summary>No row for this item in this client's data.</summary>
    NotInCatalog,
    /// <summary>Several rows with different models: the item cannot be resolved safely.</summary>
    Ambiguous,
}

/// <summary>Which part of the catalogue to list.</summary>
public enum FurnitureSourceFilter
{
    All,
    Indoor,
    Outdoor,
}

public readonly record struct FurnitureLookup(FurnitureLookupStatus Status, FurnitureCatalogRow? Row);

/// <summary>
/// Maps canonical furniture identities to local catalogue rows. Resolution
/// fails closed: a missing item, or an item that maps to more than one model,
/// is never guessed.
/// </summary>
public sealed class FurnitureCatalog
{
    private readonly Dictionary<(FurnitureSource, uint), FurnitureCatalogRow?> rows = new();
    private readonly FurnitureCatalogRow[] sorted;
    private readonly IReadOnlyList<FurnitureCatalogRow> all;

    public FurnitureCatalog(IEnumerable<FurnitureCatalogRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var unique = new List<FurnitureCatalogRow>();
        foreach (var row in source)
        {
            if (row is null || row.ItemId == 0 || row.ModelKey == 0 || !Enum.IsDefined(row.Source))
                continue;
            var key = (row.Source, row.ItemId);
            if (rows.TryGetValue(key, out var existing))
            {
                // The same item listed twice with the same model is harmless;
                // with different models it is ambiguous (null marks that).
                if (existing is not null && existing.ModelKey != row.ModelKey)
                {
                    rows[key] = null;
                    unique.Remove(existing);
                }
                continue;
            }
            rows[key] = row;
            unique.Add(row);
        }

        unique.Sort((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (byName != 0)
                return byName;
            var bySource = left.Source.CompareTo(right.Source);
            return bySource != 0 ? bySource : left.ItemId.CompareTo(right.ItemId);
        });
        sorted = unique.ToArray();
        all = Array.AsReadOnly(sorted);
    }

    public int Count => sorted.Length;

    /// <summary>Every resolvable row, in name order.</summary>
    public IReadOnlyList<FurnitureCatalogRow> Rows => all;

    /// <summary>
    /// Every row matching the optional name filter and the source filter, in
    /// name order. An empty text matches everything.
    /// </summary>
    public IReadOnlyList<FurnitureCatalogRow> Filter(string? text, FurnitureSourceFilter source)
    {
        var query = text?.Trim() ?? string.Empty;
        if (query.Length == 0 && source == FurnitureSourceFilter.All)
            return all;
        var results = new List<FurnitureCatalogRow>();
        foreach (var row in sorted)
        {
            if (Matches(row, source) &&
                (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                results.Add(row);
        }
        return results.AsReadOnly();
    }

    private static bool Matches(FurnitureCatalogRow row, FurnitureSourceFilter source) => source switch
    {
        FurnitureSourceFilter.Indoor => row.Source == FurnitureSource.Indoor,
        FurnitureSourceFilter.Outdoor => row.Source == FurnitureSource.Outdoor,
        _ => true,
    };

    public FurnitureLookup Find(string? identity)
    {
        if (!FurnitureIdentity.TryParse(identity, out var source, out var itemId))
            return new(FurnitureLookupStatus.InvalidIdentity, null);
        return Find(source, itemId);
    }

    public FurnitureLookup Find(FurnitureSource source, uint itemId)
    {
        if (!rows.TryGetValue((source, itemId), out var row))
            return new(FurnitureLookupStatus.NotInCatalog, null);
        return row is null
            ? new(FurnitureLookupStatus.Ambiguous, null)
            : new(FurnitureLookupStatus.Found, row);
    }

    /// <summary>Rows whose name contains <paramref name="text"/> (case-insensitive), in name order.</summary>
    public IReadOnlyList<FurnitureCatalogRow> Search(string? text, int maximum)
    {
        if (maximum <= 0)
            return [];
        var query = text?.Trim() ?? string.Empty;
        var results = new List<FurnitureCatalogRow>(Math.Min(maximum, 64));
        foreach (var row in sorted)
        {
            if (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(row);
                if (results.Count == maximum)
                    break;
            }
        }
        return results;
    }
}
