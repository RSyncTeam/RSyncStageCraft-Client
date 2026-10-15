namespace RSyncStageCraft.Core.Furniture;

/// <summary>
/// The catalogue list's state: an optional name filter, an indoor/outdoor
/// filter, the matching rows in name order and the selected row. The list is
/// recomputed only when a filter changes, never per frame. An empty search
/// lists the whole catalogue. The selection survives filtering, so the
/// selected item's properties stay visible while the list changes.
/// </summary>
public sealed class FurnitureCatalogBrowser
{
    private readonly FurnitureCatalog catalog;

    public FurnitureCatalogBrowser(FurnitureCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Rows = catalog.Rows;
    }

    public string Text { get; private set; } = string.Empty;

    public FurnitureSourceFilter Source { get; private set; } = FurnitureSourceFilter.All;

    /// <summary>The rows matching the current filters, in name order.</summary>
    public IReadOnlyList<FurnitureCatalogRow> Rows { get; private set; }

    public FurnitureCatalogRow? Selected { get; private set; }

    /// <summary>
    /// Applies new filters. Returns true when the list was recomputed, false
    /// when nothing that affects it changed (surrounding whitespace is ignored).
    /// </summary>
    public bool SetFilter(string? text, FurnitureSourceFilter source)
    {
        var normalized = text?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(source))
            source = FurnitureSourceFilter.All;
        if (string.Equals(normalized, Text, StringComparison.OrdinalIgnoreCase) && source == Source)
            return false;
        Text = normalized;
        Source = source;
        Rows = catalog.Filter(normalized, source);
        return true;
    }

    /// <summary>
    /// Selects a row of this catalogue, or clears the selection with null.
    /// A row that is not in this catalogue (for example from older data) is
    /// refused and the selection is left unchanged.
    /// </summary>
    public bool Select(FurnitureCatalogRow? row)
    {
        if (row is null)
        {
            Selected = null;
            return true;
        }
        if (catalog.Find(row.Source, row.ItemId).Row != row)
            return false;
        Selected = row;
        return true;
    }
}
