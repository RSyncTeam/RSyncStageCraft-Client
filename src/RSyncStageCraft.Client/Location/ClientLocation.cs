using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Location;

/// <summary>
/// The house the player is inside, as the game client's own HouseId reports
/// it (FFXIVClientStructs HousingManager.GetCurrentIndoorHouseId). Indices are
/// the raw 0-based values; RoomNumber 0x3FF marks a workshop.
/// </summary>
public readonly record struct IndoorHouseIdentity(
    ushort DistrictTerritoryId,
    ushort WorldId,
    int WardIndex,
    int PlotIndex,
    bool IsApartment,
    int ApartmentDivision,
    int RoomNumber,
    bool IsWorkshop);

/// <summary>
/// Display names for a location, looked up from the game's own data sheets
/// that ship with the client (no network). Display only: IDs stay the key
/// for matching, because names differ by client language and can change.
/// </summary>
public sealed record LocationNames(string? Zone, string? District, string? World)
{
    public static LocationNames None { get; } = new(null, null, null);
}

/// <summary>
/// Where the local player is, read only from the local game client's own
/// memory (no packet is sent to obtain it). Ward and plot are 1-based as the
/// game shows them. Missing values are null rather than guessed.
/// </summary>
public sealed record ClientLocation(
    uint TerritoryId,
    ushort? WorldId,
    ushort? Ward,
    ushort? Plot,
    ushort? Room,
    byte? Division,
    bool Indoors)
{
    /// <summary>Plots 31-60 (indices 30-59) are in the subdivision.</summary>
    public const int PlotsPerDivision = 30;
    public const int MaximumWards = 30;

    /// <summary>The housing district a house interior belongs to.</summary>
    public ushort? DistrictTerritoryId { get; init; }

    /// <summary>Inside a free company workshop (a separate interior of the house).</summary>
    public bool Workshop { get; init; }

    /// <summary>
    /// Inside an apartment building, as the house identity says. With no
    /// <see cref="Room"/> this is the building's lobby.
    /// </summary>
    public bool Apartment { get; init; }

    public bool IsApartmentLobby => Apartment && Room is null;

    public bool IsHousing => Ward is not null;

    /// <summary>
    /// Outdoors (open world or a housing district). Values follow
    /// FFXIVClientStructs' HousingManager: ward and plot 0-based with negative
    /// meaning none, division 1 = main, 2 = subdivision, 0 = unknown.
    /// </summary>
    public static ClientLocation? FromOutdoor(
        uint territoryId,
        uint worldId,
        int rawWard,
        int rawPlot,
        int rawDivision)
    {
        if (territoryId == 0)
            return null;
        ushort? world = worldId is > 0 and <= ushort.MaxValue ? (ushort)worldId : null;
        ushort? ward = rawWard is >= 0 and < MaximumWards ? (ushort)(rawWard + 1) : null;
        ushort? plot = ward is not null && rawPlot is >= 0 and < 2 * PlotsPerDivision
            ? (ushort)(rawPlot + 1)
            : null;
        byte? division = ward is not null && rawDivision is 1 or 2 ? (byte)rawDivision : null;
        return new(territoryId, world, ward, plot, null, division, Indoors: false);
    }

    /// <summary>
    /// Inside a house, apartment or workshop. HousingManager's plot and
    /// division getters do not describe an interior (division is 0 indoors,
    /// plot is negative for apartments), so everything comes from the house's
    /// own identity. The territory is the original (pre-renovation) interior
    /// territory so a renovated house keeps the same location.
    /// </summary>
    public static ClientLocation? FromIndoor(
        uint originalTerritoryId,
        uint currentTerritoryId,
        IndoorHouseIdentity house)
    {
        var territory = originalTerritoryId != 0 ? originalTerritoryId : currentTerritoryId;
        if (territory == 0 || house.DistrictTerritoryId == 0 || house.WorldId == 0 ||
            house.WardIndex is < 0 or >= MaximumWards)
            return null;

        var ward = (ushort)(house.WardIndex + 1);
        if (house.IsApartment)
        {
            // Room 0 is the building's lobby; otherwise it is the apartment.
            if (house.ApartmentDivision is not (0 or 1) || house.RoomNumber is < 0 or >= 0x3FF)
                return null;
            ushort? apartmentRoom = house.RoomNumber == 0 ? null : (ushort)house.RoomNumber;
            return new(territory, house.WorldId, ward, Plot: null, apartmentRoom,
                (byte)(house.ApartmentDivision + 1), Indoors: true)
            {
                DistrictTerritoryId = house.DistrictTerritoryId,
                Apartment = true,
            };
        }

        if (house.PlotIndex is < 0 or >= 2 * PlotsPerDivision)
            return null;
        ushort? room = !house.IsWorkshop && house.RoomNumber is > 0 and < 0x3FF
            ? (ushort)house.RoomNumber
            : null;
        return new(territory, house.WorldId, ward, (ushort)(house.PlotIndex + 1), room,
            (byte)(house.PlotIndex < PlotsPerDivision ? 1 : 2), Indoors: true)
        {
            DistrictTerritoryId = house.DistrictTerritoryId,
            Workshop = house.IsWorkshop,
        };
    }

    /// <summary>
    /// The protocol form (protocol 3), or null when the client cannot say
    /// exactly where it is; callers must then neither place nor draw anything.
    /// Outdoors in a ward the location is the ward's division (main or
    /// subdivision), so crossing plots or the street does not change it; an
    /// unknown division fails closed. Indoors it names the exact
    /// house, private chamber, workshop, apartment or apartment lobby.
    /// </summary>
    public SceneLocation? ToSceneLocation()
    {
        if (Ward is not { } ward)
            return new SceneLocation(TerritoryId, null);
        if (WorldId is not { } world)
            return null;
        HousingLocation? housing;
        if (!Indoors)
            housing = Division is { } outdoorDivision
                ? new HousingLocation(world, ward, 0, 0, outdoorDivision)
                : null;
        else if (Apartment)
            housing = Division is { } apartmentDivision
                ? new HousingLocation(world, ward, 0, Room ?? 0, apartmentDivision)
                : null;
        else if (Plot is { } plot && Division is { } division)
            housing = new HousingLocation(world, ward, plot, Workshop ? (ushort)0 : Room ?? 0, division, Workshop);
        else
            housing = null;
        return housing is { IsValid: true } exact ? new SceneLocation(TerritoryId, exact) : null;
    }

    /// <summary>
    /// Readable lines for the window: place, world, housing detail (if any),
    /// then the raw IDs. Missing names fall back to the ID.
    /// </summary>
    public IReadOnlyList<string> DescribeLines(LocationNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var lines = new List<string>(4);
        var zone = Named(names.Zone) ?? $"Zone {TerritoryId}";
        var district = Named(names.District);
        lines.Add(Indoors && district is not null && district != zone
            ? $"{zone} ({district})"
            : zone);
        if (WorldId is { } world)
            lines.Add($"World: {Named(names.World) ?? world.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (Ward is { } ward)
        {
            var parts = new List<string>(6) { $"Ward {ward}" };
            if (Division is { } division) parts.Add(division == 2 ? "subdivision" : "main division");
            if (Plot is { } plot) parts.Add($"plot {plot}");
            if (Apartment)
                parts.Add(Room is { } apartment ? $"apartment {apartment}" : "apartment lobby");
            else if (Room is { } room)
                parts.Add($"room {room}");
            if (Workshop) parts.Add("workshop");
            parts.Add(Indoors ? "indoors" : "outdoors");
            lines.Add(string.Join(", ", parts));
        }
        var ids = new List<string>(3) { $"zone {TerritoryId}" };
        if (DistrictTerritoryId is { } districtId && districtId != TerritoryId) ids.Add($"district {districtId}");
        if (WorldId is { } worldId) ids.Add($"world {worldId}");
        lines.Add("IDs: " + string.Join(", ", ids));
        return lines;
    }

    private static string? Named(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public string Describe()
    {
        if (Ward is not { } ward)
            return $"Zone {TerritoryId}" + (WorldId is { } world ? $", world {world}" : string.Empty);
        var parts = new List<string>(8) { $"Zone {TerritoryId}" };
        if (DistrictTerritoryId is { } district && district != TerritoryId)
            parts.Add($"district {district}");
        if (WorldId is { } housingWorld) parts.Add($"world {housingWorld}");
        parts.Add($"ward {ward}");
        if (Division is { } division) parts.Add(division == 2 ? "subdivision" : "main division");
        if (Plot is { } plot) parts.Add($"plot {plot}");
        if (Apartment)
            parts.Add(Room is { } apartment ? $"apartment {apartment}" : "apartment lobby");
        else if (Room is { } room)
            parts.Add($"room {room}");
        if (Workshop) parts.Add("workshop");
        parts.Add(Indoors ? "indoors" : "outdoors");
        return string.Join(", ", parts);
    }
}
