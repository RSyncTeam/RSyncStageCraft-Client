using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using RSyncStageCraft.Core.Location;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Samples the local player's location from the game client's own memory on
/// the framework thread, every frame, and reports changes at once (objects of
/// the old place must disappear before the new place is drawn). While the
/// client is between areas the location is unknown. Read-only: it never calls anything
/// that sends a packet (for example, HousingManager.RequestWardInfo is never
/// used), so nothing about this reaches the game servers. Zone and world names
/// come from the game's own data sheets on disk (IDataManager), for display.
/// </summary>
internal sealed record ClientPlace(ClientLocation Location, LocationNames Names)
{
    /// <summary>The scene location of this place, or null when it is not complete.</summary>
    internal Core.Protocol.SceneLocation? Scene { get; } = Location.ToSceneLocation();
}

/// <summary>Where the local player stands and faces (radians around the vertical axis).</summary>
internal sealed record PlayerPose(System.Numerics.Vector3 Position, float Rotation);

internal sealed class DalamudLocationProvider : IDisposable
{
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager data;
    // Name lookups are cached; only the framework thread touches these.
    private readonly Dictionary<uint, string?> zoneNames = new();
    private readonly Dictionary<uint, string?> worldNames = new();
    private ClientLocation? sampled;
    private ClientPlace? current;
    private PlayerPose? pose;
    private int disposed;

    internal DalamudLocationProvider(
        IFramework framework,
        ICondition condition,
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager data)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.objects = objects ?? throw new ArgumentNullException(nameof(objects));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        framework.Update += OnUpdate;
    }

    /// <summary>The latest sample, or null when not logged in or unknown.</summary>
    internal ClientPlace? Current => Volatile.Read(ref current);

    /// <summary>The local player's position and facing, sampled every frame; null when unknown.</summary>
    internal PlayerPose? Pose => Volatile.Read(ref pose);

    /// <summary>
    /// Raised on the framework thread when the location changes, after
    /// <see cref="Current"/> was updated. The argument is the new place.
    /// </summary>
    internal event Action<ClientPlace?>? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        framework.Update -= OnUpdate;
        Volatile.Write(ref current, null);
        Volatile.Write(ref pose, null);
    }

    private void OnUpdate(IFramework _)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        Volatile.Write(ref pose, SamplePose());
        var location = Sample();
        if (location == sampled)
            return;
        sampled = location;
        var place = location is null ? null : new ClientPlace(location, NamesFor(location));
        Volatile.Write(ref current, place);
        try
        {
            Changed?.Invoke(place);
        }
        catch (Exception)
        {
            // A listener failure must not stop location sampling.
        }
    }

    private PlayerPose? SamplePose()
    {
        try
        {
            return objects.LocalPlayer is { } player
                ? new PlayerPose(player.Position, player.Rotation)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private LocationNames NamesFor(ClientLocation location)
    {
        try
        {
            return new LocationNames(
                ZoneName(location.TerritoryId),
                location.DistrictTerritoryId is { } district ? ZoneName(district) : null,
                location.WorldId is { } world ? WorldName(world) : null);
        }
        catch (Exception)
        {
            return LocationNames.None;
        }
    }

    private string? ZoneName(uint territoryId)
    {
        if (!zoneNames.TryGetValue(territoryId, out var name))
        {
            name = data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId) is { } row &&
                row.PlaceName.ValueNullable is { } place
                    ? place.Name.ExtractText()
                    : null;
            zoneNames[territoryId] = name;
        }
        return name;
    }

    private string? WorldName(uint worldId)
    {
        if (!worldNames.TryGetValue(worldId, out var name))
        {
            name = data.GetExcelSheet<World>().GetRowOrDefault(worldId) is { } row
                ? row.Name.ExtractText()
                : null;
            worldNames[worldId] = name;
        }
        return name;
    }

    private unsafe ClientLocation? Sample()
    {
        try
        {
            if (!clientState.IsLoggedIn || !playerState.IsLoaded ||
                condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
                return null;
            var territory = (uint)clientState.TerritoryType;
            var world = playerState.CurrentWorld.RowId;
            var housing = HousingManager.Instance();
            if (housing == null)
                return ClientLocation.FromOutdoor(territory, world, -1, -1, 0);

            if (housing->IsInside() || housing->IsInWorkshop())
            {
                // Interiors: the plot/division getters do not describe the
                // house, and a renovated interior has a different territory.
                return HouseIdReader.Read(housing->GetCurrentIndoorHouseId()) is { } house
                    ? ClientLocation.FromIndoor(HousingManager.GetOriginalHouseTerritoryTypeId(), territory, house)
                    : null;
            }

            return ClientLocation.FromOutdoor(
                territory,
                world,
                housing->GetCurrentWard(),
                housing->GetCurrentPlot(),
                housing->GetCurrentDivision());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads the game's own HouseId through ClientStructs' accessors, so the bit
/// layout stays ClientStructs' responsibility. Kept apart from the provider so
/// it depends on nothing but ClientStructs and can be tested with real values.
/// </summary>
internal static class HouseIdReader
{
    /// <summary>An empty or all-ones ID means the client does not know the house.</summary>
    internal static IndoorHouseIdentity? Read(HouseId house)
    {
        ulong raw = house;
        if (raw is 0 or ulong.MaxValue)
            return null;
        return new IndoorHouseIdentity(
            house.TerritoryTypeId,
            house.WorldId,
            house.WardIndex,
            house.PlotIndex,
            house.IsApartment,
            house.ApartmentDivision,
            house.RoomNumber,
            house.IsWorkshop);
    }
}
