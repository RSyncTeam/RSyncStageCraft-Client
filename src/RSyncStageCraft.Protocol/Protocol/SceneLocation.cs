namespace RSyncStageCraft.Core.Protocol;

/// <summary>
/// Housing address of a scene. Wards and interiors share coordinates across
/// worlds, wards and same-size houses, so a housing scene needs every part to
/// be unambiguous. Values are read from the local client only.
/// <list type="bullet">
/// <item>Outdoors (a ward): <see cref="Plot"/> and <see cref="Room"/> are 0
/// and <see cref="Division"/> is 1 (main) or 2 (subdivision). Each division of
/// a ward is one place outdoors, so crossing plot boundaries or the street does
/// not change the location.</item>
/// <item>A house: <see cref="Plot"/> 1-60, <see cref="Room"/> 0 or a private
/// chamber, <see cref="Division"/> 1 (plots 1-30) or 2 (plots 31-60).</item>
/// <item>A workshop: as a house, with <see cref="Workshop"/> set and Room 0.</item>
/// <item>An apartment building: <see cref="Plot"/> 0, <see cref="Room"/> the
/// apartment (0 = the lobby), <see cref="Division"/> 1 or 2.</item>
/// </list>
/// </summary>
public readonly record struct HousingLocation(
    ushort WorldId,
    ushort Ward,
    ushort Plot,
    ushort Room,
    byte Division = 0,
    bool Workshop = false)
{
    public const int MaximumWards = 30;
    public const int MaximumPlots = 60;
    public const int MaximumRoom = 0x3FE;

    public bool IsValid =>
        WorldId > 0 &&
        Ward is > 0 and <= MaximumWards &&
        Plot <= MaximumPlots &&
        Room <= MaximumRoom &&
        (Plot > 0
            // A house (or its workshop): the division follows from the plot,
            // and a workshop has no room.
            ? Division == (Plot <= MaximumPlots / 2 ? 1 : 2) && (!Workshop || Room == 0)
            // No plot: the ward outdoors (room 0) or an apartment building
            // (room 0 = lobby); both always name their division. The
            // territory tells a ward from an apartment building.
            : !Workshop && Division is 1 or 2);
}

/// <summary>
/// Where a scene lives. Objects inside a scene keep plain XYZ; a client draws
/// a scene only when its current location equals the scene's location.
/// Captured automatically from local client state, never from the game server.
/// </summary>
public readonly record struct SceneLocation(
    uint TerritoryId,
    HousingLocation? Housing)
{
    public bool IsValid =>
        TerritoryId > 0 &&
        (Housing is null || Housing.Value.IsValid);
}
