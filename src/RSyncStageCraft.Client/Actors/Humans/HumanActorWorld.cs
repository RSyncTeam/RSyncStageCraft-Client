#if RSC_DEVELOPMENT
namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// One <c>World</c> row as the game data has it, read without writing (HV-4's
/// start-up check). <paramref name="FirstNameByte"/> is the first raw byte of
/// the row's name, the byte the providers' world list tests, or −1 for an
/// empty name.
/// </summary>
public sealed record HumanActorWorldRow(
    uint RowId, string InternalName, string Name, int FirstNameByte, uint DataCenter, byte UserType, byte Region, bool IsPublic);

/// <summary>HV-4's reads for W-3: the row in English and in the client's language, and the player's current world. Read only.</summary>
public interface IHumanActorWorldSource
{
    /// <summary>The row in English, as F11 read it; null when it cannot be read.</summary>
    HumanActorWorldRow? EnglishRow(ushort rowId);

    /// <summary>The row in the client's language, the language the providers build their world list from; null when it cannot be read.</summary>
    HumanActorWorldRow? ClientRow(ushort rowId);

    /// <summary>The local player's current world; null when it cannot be read.</summary>
    ushort? PlayerCurrentWorld();
}

/// <summary>
/// W-3 for HV-4: the World row the owner selected (2026-11-11) from F11's
/// re-check of game 2026.11.03.0000.0000, compiled into the Debug build only.
/// The selection is experimental. Nothing here claims the row is unused,
/// reserved or isolated: isolation still rests on the unique synthetic name,
/// the parent checks and the ownership checks. Before every HV-4 spawn the
/// row is read again, in English and in the client's language. Any
/// difference from the selected identity, a public row, a name the
/// providers' world list would not hold, or a row equal to the player's home
/// or current world refuses. There is no fallback and no other row.
/// </summary>
public static class HumanActorW3
{
    public const ushort WorldId = 412;
    public const string ExpectedInternalName = "Titania";
    public const string ExpectedName = "Titania";
    public const uint ExpectedDataCenter = 6;
    public const byte ExpectedUserType = 5;
    public const byte ExpectedRegion = 1;

    /// <summary>The English row against the selected identity. Null on success; otherwise why the row is refused.</summary>
    public static string? RowMismatch(HumanActorWorldRow? row)
    {
        if (row is null)
            return "the selected World row could not be read";
        if (row.RowId != WorldId)
            return "the World row read is not the selected row";
        if (!string.Equals(row.InternalName, ExpectedInternalName, StringComparison.Ordinal))
            return "the selected World row's internal name changed";
        if (!string.Equals(row.Name, ExpectedName, StringComparison.Ordinal))
            return "the selected World row's name changed";
        if (row.DataCenter != ExpectedDataCenter)
            return "the selected World row's data centre changed";
        if (row.UserType != ExpectedUserType)
            return "the selected World row's user type changed";
        if (row.Region != ExpectedRegion)
            return "the selected World row's region changed";
        if (row.IsPublic)
            return "the selected World row is public";
        return null;
    }

    /// <summary>
    /// The client-language row: at least as strict as the providers' world
    /// list (a name, a data centre other than 0, and, for a row that is not
    /// public, an upper-case first byte), and narrower: the selected data
    /// centre and name, not public, and a first byte in <c>A</c>–<c>Z</c>.
    /// Null on success.
    /// </summary>
    public static string? ClientRowMismatch(HumanActorWorldRow? row)
    {
        if (row is null)
            return "the selected World row could not be read in the client's language";
        if (row.RowId != WorldId)
            return "the World row read in the client's language is not the selected row";
        if (row.FirstNameByte is < 0x41 or > 0x5A)
            return "the selected World row's name does not start with A-Z in the client's language";
        if (!string.Equals(row.Name, ExpectedName, StringComparison.Ordinal))
            return "the selected World row's name differs in the client's language";
        if (row.DataCenter is 0)
            return "the selected World row has no data centre in the client's language";
        if (row.DataCenter != ExpectedDataCenter)
            return "the selected World row's data centre differs in the client's language";
        if (row.IsPublic)
            return "the selected World row is public in the client's language";
        return null;
    }

    /// <summary>The player's home and current worlds must be readable and differ from W-3. Null when they do. Never names a world.</summary>
    public static string? PlayerWorldMismatch(ushort playerHomeWorld, ushort? playerCurrentWorld)
    {
        if (playerHomeWorld is 0 or ushort.MaxValue)
            return "the player's home world cannot be read";
        if (playerHomeWorld == WorldId)
            return "the player's home world is the selected World row";
        if (playerCurrentWorld is null or 0 or ushort.MaxValue)
            return "the player's current world cannot be read";
        if (playerCurrentWorld == WorldId)
            return "the player's current world is the selected World row";
        return null;
    }
}
#endif
