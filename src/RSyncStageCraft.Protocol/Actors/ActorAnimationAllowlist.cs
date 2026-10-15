namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// One animation verified in game for one creature: a person played <see cref="TimelineId"/> on a client-only RSC
/// puppet of <see cref="BNpcBaseId"/> on the allowlist's game version and saw
/// it play correctly. <see cref="IsLoop"/> is the behaviour that was verified
/// and must be true: only verified loops ship (one-shots are not
/// supported). A client whose game data says otherwise refuses the row.
/// </summary>
public sealed record ActorAnimationAllowance(
    uint BNpcBaseId,
    ushort TimelineId,
    bool IsLoop);

/// <summary>
/// The curated, game-version-pinned animation allowlist. Animation is
/// never inferred: only a creature/row pair listed here, verified on
/// <see cref="GameVersion"/>, is accepted by the server and played by a
/// client, and only on a client running exactly that game version (any other
/// version disables animation only; actors with default controls still draw).
/// RSC never modifies, replaces, patches or generates <c>.tmb</c> or
/// <c>.pap</c> data, and plays rows only on its own verified client-only
/// puppets. Arbitrary animation is not supported (it needs a separate safety
/// and format review).
/// </summary>
public sealed class ActorAnimationAllowlist
{
    /// <summary>The game version the current entries were verified on (2026-10-04).</summary>
    public const string PinnedGameVersion = "2026.09.15.0000.0000";

    /// <summary>
    /// The shipped allowlist: only rows verified in game. Loops only.
    /// </summary>
    public static ActorAnimationAllowlist Current { get; } = new(PinnedGameVersion,
    [
        new(2, 34, true),
        new(71, 34, true),
    ]);

    private readonly Dictionary<(uint BNpcBaseId, ushort TimelineId), ActorAnimationAllowance> entries = new();

    public ActorAnimationAllowlist(string gameVersion, IEnumerable<ActorAnimationAllowance> allowances)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentNullException.ThrowIfNull(allowances);
        GameVersion = gameVersion;
        foreach (var allowance in allowances)
        {
            ArgumentNullException.ThrowIfNull(allowance);
            if (allowance.BNpcBaseId == 0 || allowance.TimelineId == 0)
                throw new ArgumentException("An allowance needs a nonzero creature and row.", nameof(allowances));
            // One-shots are not supported.
            if (!allowance.IsLoop)
                throw new ArgumentException("Only verified loops can be allowlisted (one-shots are deferred).", nameof(allowances));
            if (!entries.TryAdd((allowance.BNpcBaseId, allowance.TimelineId), allowance))
                throw new ArgumentException(
                    $"Duplicate allowance for creature {allowance.BNpcBaseId}, row {allowance.TimelineId}.", nameof(allowances));
        }
    }

    public string GameVersion { get; }

    public int Count => entries.Count;

    /// <summary>The allowance for this creature and row, or null.</summary>
    public ActorAnimationAllowance? Find(uint bnpcBaseId, ushort timelineId) =>
        entries.TryGetValue((bnpcBaseId, timelineId), out var allowance) ? allowance : null;

    /// <summary>The server-side rule: the pair is listed (the server has no game data or version).</summary>
    public bool Allows(uint bnpcBaseId, ushort timelineId) => Find(bnpcBaseId, timelineId) is not null;

    /// <summary>The rows allowed for one creature, by row id (what the UI may offer).</summary>
    public IReadOnlyList<ActorAnimationAllowance> ForCreature(uint bnpcBaseId) =>
        entries.Values.Where(allowance => allowance.BNpcBaseId == bnpcBaseId).OrderBy(allowance => allowance.TimelineId).ToArray();

    /// <summary>Whether this client's game version is the one the entries were verified on.</summary>
    public bool MatchesGameVersion(string? gameVersion) =>
        string.Equals(gameVersion, GameVersion, StringComparison.Ordinal);
}
