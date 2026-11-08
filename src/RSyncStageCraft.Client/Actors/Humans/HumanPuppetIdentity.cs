#if RSC_DEVELOPMENT
using System.Security.Cryptography;

namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>
/// P2 synthetic names for Debug human puppets: the forename <c>Rsc</c> and a
/// surname of one uppercase letter and 14 lowercase letters, drawn from a
/// CSPRNG for every spawn attempt (about 70 bits). The 19-character name fits
/// the game's 64-byte name buffer with its NUL. Puppets that share a run get
/// different surname initials, so they stay distinguishable in Glamourer's
/// shortened log form (forename initial, surname initial, world; erratum E3).
/// The scheme is probabilistic: every guarantee comes from the host's checks,
/// never from the name. Debug only: compiled out of Release.
/// </summary>
public static class HumanPuppetName
{
    public const string Forename = "Rsc";
    public const int SurnameLowercaseLetters = 14;

    /// <summary>"Rsc " plus a 15-letter surname.</summary>
    public const int Length = 19;

    /// <summary>The game's name buffer, including its terminator.</summary>
    public const int GameNameBufferBytes = ActorOwnershipTag.GameNameBufferBytes;

    /// <summary>
    /// A fresh name whose surname initial is not in <paramref name="initialsInUse"/>.
    /// Throws when all 26 initials are in use (never within the eight-actor total).
    /// </summary>
    public static string Create(IReadOnlySet<char> initialsInUse)
    {
        ArgumentNullException.ThrowIfNull(initialsInUse);
        var free = Enumerable.Range('A', 26).Select(value => (char)value).Where(letter => !initialsInUse.Contains(letter)).ToArray();
        if (free.Length == 0)
            throw new InvalidOperationException("every surname initial is in use");
        Span<char> surname = stackalloc char[1 + SurnameLowercaseLetters];
        surname[0] = free[RandomNumberGenerator.GetInt32(free.Length)];
        for (var i = 1; i < surname.Length; i++)
            surname[i] = (char)('a' + RandomNumberGenerator.GetInt32(26));
        return Forename + " " + new string(surname);
    }

    /// <summary>The surname initial of a well-formed name.</summary>
    public static char SurnameInitial(string name) =>
        IsWellFormed(name) ? name[Forename.Length + 1] : throw new ArgumentException("not a P2 name", nameof(name));

    public static bool IsWellFormed(string? name) =>
        name is { Length: Length } &&
        name.StartsWith(Forename + " ", StringComparison.Ordinal) &&
        name[Forename.Length + 1] is >= 'A' and <= 'Z' &&
        name.AsSpan(Forename.Length + 2).IndexOfAnyExceptInRange('a', 'z') < 0;
}

/// <summary>
/// The per-spawn key (P7 as amended by r5a part 10): a CSPRNG <c>uint</c>,
/// redrawn until it is neither 0 nor 1337, Glamourer's internal temporary-lock
/// value. This candidate makes no Glamourer state call; the key is recorded
/// in the spawn's in-memory receipt for the later candidate.
/// </summary>
public static class HumanPuppetKey
{
    public const uint Reserved = 1337;

    public static uint Create() => Create(static () => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));

    /// <summary>Test seam: draws from <paramref name="next"/> until a permitted key appears.</summary>
    internal static uint Create(Func<uint> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        while (true)
        {
            var key = next();
            if (IsPermitted(key))
                return key;
        }
    }

    public static bool IsPermitted(uint key) => key is not 0 and not Reserved;
}

/// <summary>
/// P3's home world for Debug human puppets. Only W-2 is offered: a world row
/// that F11 (a read-only row check) recorded as present and not public. Without
/// that record the host refuses to spawn; it never falls back to W-1 (the local
/// player's world) or to AnyWorld.
/// </summary>
/// <param name="WorldId">The F11-recorded row.</param>
/// <param name="F11Verified">True only when F11's record for this row is in hand.</param>
public readonly record struct HumanPuppetWorld(ushort WorldId, bool F11Verified)
{
    /// <summary>Penumbra's and Glamourer's AnyWorld value; never a puppet's world.</summary>
    public const ushort AnyWorld = ushort.MaxValue;

    public bool IsUsable => F11Verified && WorldId is not 0 and not AnyWorld;
}

/// <summary>One <c>World</c> row as the game data has it, read without writing (W-2's start-up check).</summary>
public sealed record HumanPuppetWorldRow(uint RowId, string InternalName, string Name, uint DataCenter, bool IsPublic);

/// <summary>
/// W-2 for T3-1: the World row the owner selected after F11 (game version
/// 2026.10.16.0000.0000), compiled into the Debug build only. The selection is
/// experimental: nothing here claims the row behaves well at run time or that
/// a later game version keeps it. At start-up the row is read again; any
/// difference from the selected identity, a public row, or a row equal to the
/// player's own world refuses. There is no fallback.
/// </summary>
public static class HumanPuppetW2
{
    public const ushort WorldId = 1;
    public const string ExpectedInternalName = "reserved1";
    public const string ExpectedName = "Dev";
    public const uint ExpectedDataCenter = 0;

    /// <summary>
    /// The start-up check of the row (null when the row could not be read).
    /// Null on success; otherwise why the row is refused.
    /// </summary>
    public static string? RowMismatch(HumanPuppetWorldRow? row)
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
        if (row.IsPublic)
            return "the selected World row is public";
        return null;
    }

    /// <summary>The player's own world must differ from W-2 (H-3: never a silent W-1). Null when it does.</summary>
    public static string? PlayerWorldMismatch(ushort? playerHomeWorld) => playerHomeWorld switch
    {
        null => "the player's home world could not be read",
        WorldId => "the player's home world is the selected World row",
        _ => null,
    };

    /// <summary>The host's world: F11-verified only when the row check passed.</summary>
    public static HumanPuppetWorld World(HumanPuppetWorldRow? row) => new(WorldId, RowMismatch(row) is null);
}

/// <summary>The object kind a spawn records at setup (P5-6): F1 decides whether the player kind is needed.</summary>
public enum HumanPuppetKind
{
    /// <summary>Kind 2. R-8a: if the copies change it, the kind and sub-kind recorded at creation are written back once.</summary>
    BattleNpc = 2,

    /// <summary>Kind 1, written by SI-1's kind field (F1 only).</summary>
    Player = 1,
}
#endif
