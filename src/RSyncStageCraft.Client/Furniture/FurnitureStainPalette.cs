namespace RSyncStageCraft.Core.Furniture;

/// <summary>An sRGB colour as the game's object-stain table returns it (passed unchanged to the native stain call).</summary>
public readonly record struct FurnitureStainColor(byte R, byte G, byte B, byte A);

/// <summary>
/// One housing stain this client can draw. <see cref="Name"/> and
/// <see cref="Swatch"/> (the <c>Stain</c> sheet's packed 0xRRGGBB display
/// colour) are for the selector only; <see cref="Color"/> is the game's own
/// housing-object colour from <c>SharedGroupLayoutInstance.GetObjectStainColorByIndex</c>
/// and is the only colour ever applied.
/// </summary>
public sealed record FurnitureStainEntry(
    byte Id,
    string Name,
    uint Swatch,
    FurnitureStainColor Color,
    int SortKey = 0);

/// <summary>A row of the game's <c>Stain</c> sheet, as read from local game data.</summary>
public readonly record struct FurnitureStainSheetRow(
    uint RowId,
    string Name,
    uint Swatch,
    bool IsHousingApplicable,
    int SortKey);

/// <summary>
/// The housing stains this client can draw (Default, stain 0, is not one of
/// them: it means undyed). Built with the newer Izzy's Furniture rules: a
/// stain is listed only if its row ID is nonzero and below the game's object
/// stain count, the sheet marks it housing-applicable, it has a name, and the
/// game's native housing-colour lookup returns a colour for it. Anything else
/// is unknown here, and an object with an unknown stain is not drawn.
/// </summary>
public sealed class FurnitureStainPalette
{
    private readonly Dictionary<byte, FurnitureStainEntry> byId = new();
    private readonly Func<byte, FurnitureStainColor?> nativeColor;
    private readonly int objectStainCount;

    /// <param name="nativeColor">
    /// The game's object-stain colour lookup, used for an item's own default
    /// stain (which need not be a selectable dye). Null: no default stains.
    /// </param>
    public FurnitureStainPalette(
        IEnumerable<FurnitureStainEntry> entries,
        Func<byte, FurnitureStainColor?>? nativeColor = null,
        int objectStainCount = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.nativeColor = nativeColor ?? (_ => null);
        this.objectStainCount = Math.Clamp(objectStainCount, 0, 256);
        foreach (var entry in entries)
        {
            if (entry is null || entry.Id == 0 || string.IsNullOrWhiteSpace(entry.Name))
                continue;
            byId.TryAdd(entry.Id, entry);
        }
        Entries = byId.Values
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Id)
            .ToArray();
    }

    public static FurnitureStainPalette Empty { get; } = new([]);

    /// <summary>The selectable stains in the game's dye order (Default not included).</summary>
    public IReadOnlyList<FurnitureStainEntry> Entries { get; }

    /// <summary>
    /// The game's colour for an item's own default stain (from its layout),
    /// for any index of the object stain table. False for 0, out of range, or
    /// when the native lookup gives nothing.
    /// </summary>
    public bool TryGetDefaultColor(byte defaultStain, out FurnitureStainColor color)
    {
        color = default;
        if (defaultStain == 0 || defaultStain >= objectStainCount)
            return false;
        try
        {
            if (nativeColor(defaultStain) is { } found)
            {
                color = found;
                return true;
            }
        }
        catch
        {
            // A failed lookup only loses the default stain.
        }
        return false;
    }

    public bool TryGet(byte id, out FurnitureStainEntry entry)
    {
        if (id != 0 && byId.TryGetValue(id, out var found))
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    /// <summary>
    /// Applies the housing-stain rules to the <c>Stain</c> sheet.
    /// <paramref name="nativeColor"/> is the game's object-stain lookup; it
    /// is called only for rows that passed every other rule, and a null
    /// result (or an exception) leaves the stain out.
    /// </summary>
    public static FurnitureStainPalette Build(
        IEnumerable<FurnitureStainSheetRow> sheet,
        int objectStainCount,
        Func<byte, FurnitureStainColor?> nativeColor)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(nativeColor);
        var entries = new List<FurnitureStainEntry>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId >= (uint)Math.Clamp(objectStainCount, 0, 256) ||
                !row.IsHousingApplicable || string.IsNullOrWhiteSpace(row.Name))
                continue;
            var id = (byte)row.RowId;
            FurnitureStainColor? color;
            try
            {
                color = nativeColor(id);
            }
            catch
            {
                color = null;
            }
            if (color is { } found)
                entries.Add(new FurnitureStainEntry(id, row.Name.Trim(), row.Swatch & 0xFFFFFF, found, row.SortKey));
        }
        return new FurnitureStainPalette(entries, nativeColor, objectStainCount);
    }
}
