using System.Globalization;

namespace RSyncStageCraft.Core.Protocol;

public enum FurnitureSource
{
    /// <summary>Indoor furnishing (game sheet HousingFurniture).</summary>
    Indoor,
    /// <summary>Outdoor furnishing (game sheet HousingYardObject).</summary>
    Outdoor,
}

/// <summary>
/// The canonical wire identity of a furniture model:
/// <c>furniture:indoor:&lt;Item row ID&gt;</c> or <c>furniture:outdoor:&lt;Item row ID&gt;</c>.
/// Only the game's item row ID crosses the wire; model keys, names and file
/// paths are resolved locally by each client, which fails closed when the
/// item is missing or ambiguous. The form is strict: lowercase, no spaces,
/// no sign or leading zeros, and a nonzero 32-bit row ID.
/// </summary>
public static class FurnitureIdentity
{
    private const string IndoorPrefix = "furniture:indoor:";
    private const string OutdoorPrefix = "furniture:outdoor:";

    public static string Format(FurnitureSource source, uint itemId)
    {
        if (itemId == 0)
            throw new ArgumentOutOfRangeException(nameof(itemId));
        return source switch
        {
            FurnitureSource.Indoor => IndoorPrefix,
            FurnitureSource.Outdoor => OutdoorPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        } + itemId.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? identity, out FurnitureSource source, out uint itemId)
    {
        source = default;
        itemId = 0;
        if (identity is null)
            return false;
        ReadOnlySpan<char> digits;
        if (identity.StartsWith(IndoorPrefix, StringComparison.Ordinal))
        {
            source = FurnitureSource.Indoor;
            digits = identity.AsSpan(IndoorPrefix.Length);
        }
        else if (identity.StartsWith(OutdoorPrefix, StringComparison.Ordinal))
        {
            source = FurnitureSource.Outdoor;
            digits = identity.AsSpan(OutdoorPrefix.Length);
        }
        else
        {
            return false;
        }

        if (digits.Length is 0 or > 10 || digits[0] == '0')
            return false;
        foreach (var digit in digits)
        {
            if (digit is < '0' or > '9')
                return false;
        }
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out itemId) &&
            itemId != 0;
    }

    public static bool IsCanonical(string? identity) => TryParse(identity, out _, out _);
}
