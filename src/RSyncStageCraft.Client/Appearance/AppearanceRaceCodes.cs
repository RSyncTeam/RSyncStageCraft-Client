namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// The character's race, clan and gender exactly as the game's customize
/// data holds them (indices 0x00, 0x04 and 0x01): raw bytes, not yet mapped.
/// </summary>
public readonly record struct AppearanceCharacterRace(byte Race, byte Tribe, byte Sex);

/// <summary>Whether a skeleton group's target race could be established.</summary>
public enum AppearanceRaceCheck
{
    /// <summary>The customize data maps to a playable race code and Penumbra's base skeleton has exactly that code.</summary>
    Confirmed,
    /// <summary>No race was read, or it is not a playable race, clan and gender.</summary>
    Unknown,
    /// <summary>Penumbra reported no base skeleton, or base skeletons of more than one race.</summary>
    Unconfirmed,
    /// <summary>Penumbra's base skeleton is for a different race code.</summary>
    Mismatch,
}

/// <summary>
/// The capture contract's target race (CC-1): an explicit table from the
/// customize race, clan and gender to the playable race code, and a check
/// that Penumbra resolved the character's base skeleton for that same code.
/// </summary>
public static class AppearanceRaceCodes
{
    /// <summary>
    /// (race byte, clan byte) to the male race code. The female code is the
    /// next hundred. Written out because the codes do not follow the race
    /// byte order (Lalafell is c11xx) and only Hyur clans differ in model.
    /// </summary>
    private static readonly Dictionary<(byte Race, byte Tribe), ushort> MaleCodes = new()
    {
        [(1, 1)] = 101,   // Hyur, Midlander
        [(1, 2)] = 301,   // Hyur, Highlander
        [(2, 3)] = 501,   // Elezen, Wildwood
        [(2, 4)] = 501,   // Elezen, Duskwight
        [(3, 5)] = 1101,  // Lalafell, Plainsfolk
        [(3, 6)] = 1101,  // Lalafell, Dunesfolk
        [(4, 7)] = 701,   // Miqo'te, Seeker of the Sun
        [(4, 8)] = 701,   // Miqo'te, Keeper of the Moon
        [(5, 9)] = 901,   // Roegadyn, Sea Wolf
        [(5, 10)] = 901,  // Roegadyn, Hellsguard
        [(6, 11)] = 1301, // Au Ra, Raen
        [(6, 12)] = 1301, // Au Ra, Xaela
        [(7, 13)] = 1501, // Hrothgar, Helion
        [(7, 14)] = 1501, // Hrothgar, The Lost
        [(8, 15)] = 1701, // Viera, Rava
        [(8, 16)] = 1701, // Viera, Veena
    };

    /// <summary>
    /// The playable race code for a race, clan and gender (gender 0 male,
    /// 1 female). False for any other race or gender, or a clan of another race.
    /// </summary>
    public static bool TryFromCustomize(byte race, byte tribe, byte sex, out ushort code)
    {
        code = 0;
        if (sex > 1 || !MaleCodes.TryGetValue((race, tribe), out var male))
            return false;
        code = (ushort)(male + sex * 100);
        return true;
    }

    public static bool TryFromCustomize(AppearanceCharacterRace race, out ushort code) =>
        TryFromCustomize(race.Race, race.Tribe, race.Sex, out code);

    /// <summary>
    /// Establishes the group's target race: the customize data must map to a
    /// playable code, and the base skeletons among
    /// <paramref name="reportedGamePaths"/> (every source: vanilla, swapped or
    /// local) must name exactly that one code.
    /// </summary>
    public static AppearanceRaceCheck Confirm(AppearanceCharacterRace? race, IEnumerable<string> reportedGamePaths, out ushort code)
    {
        ArgumentNullException.ThrowIfNull(reportedGamePaths);
        code = 0;
        if (race is not { } read || !TryFromCustomize(read, out var mapped))
            return AppearanceRaceCheck.Unknown;
        var bases = new HashSet<ushort>();
        foreach (var gamePath in reportedGamePaths)
        {
            if (SkeletonResourcePaths.TryClassify(gamePath, out var path) &&
                path is { Role: SkeletonResourceRole.Skeleton, Slot: SkeletonPartialSlot.Base })
                bases.Add(path.RaceCode);
        }
        if (bases.Count != 1)
            return AppearanceRaceCheck.Unconfirmed;
        if (bases.Single() != mapped)
            return AppearanceRaceCheck.Mismatch;
        code = mapped;
        return AppearanceRaceCheck.Confirmed;
    }
}
