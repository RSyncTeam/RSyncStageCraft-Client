using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Actors;

/// <summary>One ActionTimeline row as this client's game data describes it.</summary>
/// <param name="IsLoop">Game data (<c>ActionTimeline.IsLoop</c>): a looping row is standing state, a one-shot a live event.</param>
public sealed record ActorTimelineRow(ushort Id, string Key, bool IsLoop);

/// <summary>Read-only access to the player's own ActionTimeline sheet. No network.</summary>
public interface IActorTimelineData
{
    bool TryGetTimeline(ushort timelineId, out ActorTimelineRow row);

    /// <summary>
    /// Looping rows (game data <c>IsLoop</c>; one-shots are not supported) whose key
    /// contains <paramref name="text"/> (case-insensitive) or whose id is it,
    /// at most <paramref name="limit"/>.
    /// </summary>
    IReadOnlyList<ActorTimelineRow> Search(string text, int limit);
}

/// <summary>
/// Which control paths this client can apply, each with the reason it cannot
/// (null when it can). A path is available only when every member it uses
/// resolved and its in-game check passed; anything unverified fails closed.
/// </summary>
public readonly record struct ActorControlAvailability(string? AnimationUnavailable)
{
    public static ActorControlAvailability None(string reason) => new(reason);
}

/// <summary>
/// What the player-facing controls panel offers for one creature: the
/// allowlisted loops for that creature; the reason is null when animation is
/// available. There is no freeze and no look-at (both removed; their protocol
/// fields are reserved).
/// </summary>
public sealed record ActorControlOffer(
    string? AnimationUnavailable,
    IReadOnlyList<ActorAnimationAllowance> Animations)
{
    public static ActorControlOffer None(string reason) => new(reason, []);
}

/// <summary>
/// The production composition of the actor host: the verified control
/// paths (<see cref="ActorControlVerification.Current"/>) and the shipped
/// animation allowlist (<see cref="ActorAnimationAllowlist.Current"/>). The
/// plugin builds its host only through this, and the composition tests use
/// the same call with fake native backends.
/// </summary>
public static class ActorHostComposition
{
    public static ActorLocalObjectHost Create(
        IActorSceneBackend backend,
        Func<uint, ActorDrawPlanResult> resolve,
        IActorControlBackend controls,
        IActorTimelineData timelines,
        string? gameVersion,
        bool developmentTools,
        int? activeLimit = null) =>
        new(backend, resolve, activeLimit: activeLimit, controls: controls, timelines: timelines,
            verification: ActorControlVerification.Current, allowlist: ActorAnimationAllowlist.Current,
            gameVersion: gameVersion)
        {
            DevelopmentToolsEnabled = developmentTools,
        };
}

/// <summary>
/// Which control paths have passed their in-game check. A path that resolved but is not verified still
/// fails closed for synchronized actors.
/// </summary>
public readonly record struct ActorControlVerification(bool Animation)
{
    /// <summary>
    /// This build: animation is verified, but only ever plays the exact
    /// creature/row pairs in <see cref="ActorAnimationAllowlist"/> (loops
    /// only). RSC has no freeze and no look-at: the investigated freeze
    /// approach is not reliable across construction and redraw.
    /// </summary>
    public static ActorControlVerification Current => new(Animation: true);

    /// <summary>No path verified (tests).</summary>
    public static ActorControlVerification None => new(false);

    /// <summary>Every path verified (tests).</summary>
    public static ActorControlVerification All => new(true);
}

/// <summary>
/// The native animation primitives for RSC's own puppets (typed
/// ClientStructs members only, no hooks and no RSC-owned signatures).
/// <b>Each method performs exactly one native write or call</b>, so
/// <see cref="ActorLocalObjectHost"/> can run the full ownership proof
/// immediately before every one of them (a loop is a mode call, a
/// base-override write and a play call). Every decision (whether a control is
/// wanted, ownership, ordering, failing closed) is made by the host. There is
/// no freeze and no look-at member. Game thread only.
/// </summary>
public interface IActorControlBackend
{
    ActorControlAvailability Availability { get; }

    /// <summary>One call: animation-lock mode (true) or normal mode (false), parameter 0.</summary>
    void SetAnimationLock(nint address, bool locked);

    /// <summary>One write: the base-animation override (0 clears it).</summary>
    void SetBaseOverride(nint address, ushort timelineId);

    /// <summary>One call: plays a row (the game picks its slot).</summary>
    void PlayTimeline(nint address, ushort timelineId);

    /// <summary>The row in each timeline slot now, in slot order (0 = none). Read-only.</summary>
    IReadOnlyList<ushort> ReadActiveTimelines(nint address);

    /// <summary>
    /// Read-only: the creature's own animation has started (a timeline slot
    /// holds a row and the body has a bound animation control), so standing
    /// controls applied now act on a running actor, as a live change does.
    /// </summary>
    bool IsAnimationReady(nint address);

    /// <summary>
    /// Read-only diagnostics per timeline slot (public typed members only): the
    /// row, whether a scheduler timeline exists, its key, timestamp and
    /// resource with its load state.
    /// </summary>
    IReadOnlyList<ActorTimelineSlotState> ReadTimelineSlots(nint address);

    /// <summary>
    /// Read-only: the Havok animation controls of the body's main skeleton
    /// (public typed members only): local time, weight, speed and whether an
    /// animation is bound. Used to see whether any animation actually advances.
    /// </summary>
    ActorAnimationSample ReadAnimationSample(nint address);
}

/// <summary>One timeline slot, read for diagnostics (no address).</summary>
public sealed record ActorTimelineSlotState(
    int Slot,
    ushort TimelineId,
    bool HasSchedulerTimeline,
    string? Key,
    float? Timestamp,
    string? Resource,
    byte? ResourceLoadState,
    bool? ResourceLoaded = null)
{
    public string Describe() =>
        $"slot {Slot}: row {TimelineId}, scheduler timeline {(HasSchedulerTimeline ? "yes" : "no")}" +
        (Key is null ? "" : $", key {Key}") +
        (Timestamp is { } time ? $", time {time:0.###}" : "") +
        (Resource is null ? "" : $", resource {Resource}") +
        (ResourceLoadState is { } state ? $" (load state {state})" : "") +
        (ResourceLoaded is { } loaded ? $", resource data {(loaded ? "present" : "absent")}" : "");
}

/// <summary>
/// One Havok animation control of the main skeleton. <paramref name="Identity"/>
/// is an opaque, in-memory key for that control (so samples are matched per
/// control, never by list position); it is never shown or logged.
/// </summary>
public readonly record struct ActorAnimationControlState(
    ulong Identity, float LocalTime, float Weight, float PlaybackSpeed, bool Bound, float? Duration)
{
    /// <summary>A bound animation with weight: it can move the body.</summary>
    public bool IsLive => Bound && Weight > 0f;

    public string Describe() =>
        $"t {LocalTime:0.###}, weight {Weight:0.##}, speed {PlaybackSpeed:0.##}, " +
        (Bound ? $"bound ({(Duration is { } duration ? $"{duration:0.##} s" : "no animation")})" : "unbound");
}

/// <summary>
/// The main skeleton's animation controls at one moment: diagnostics only.
/// Body movement is never proof that a requested row plays (in-game testing
/// saw the idle advance while the requested row sat in a slot at timestamp
/// 0); no public typed member links a control to a row.
/// </summary>
public sealed record ActorAnimationSample(bool Readable, IReadOnlyList<ActorAnimationControlState> Controls)
{
    public static ActorAnimationSample Unreadable { get; } = new(false, []);

    public int LiveControls => Controls.Count(control => control.IsLive);

    public string Describe() =>
        !Readable ? "unreadable"
        : Controls.Count == 0 ? "no animation controls"
        : $"{Controls.Count} control(s), {LiveControls} live: " + string.Join("; ", Controls.Select(control => control.Describe()));
}

/// <summary>
/// Standing controls this client could not apply to an actor (no address):
/// the actor is not drawn with <c>StandingControlsFailedReason</c>; this is
/// the detail (ids, numbers and flags only), logged on every build.
/// </summary>
public sealed record ActorControlFailure(long Sequence, RscObjectId ObjectId, uint BNpcBaseId, ActorControlState Desired, bool WasShown, string Detail)
{
    /// <summary>What the release did: receipt match, control reset, hide, undraw, delete (or where the proof failed).</summary>
    public string? Release { get; init; }

    /// <summary>Hidden, undrawn and deleted with the proof holding throughout; only then is it listed as not drawn.</summary>
    public bool Released { get; init; }

    /// <summary>Which draw of this actor with these controls failed (1: drawn once more; then refused).</summary>
    public int Attempt { get; init; } = 1;

    public string Describe() =>
        $"#{Sequence} actor {ObjectId.Value:N} (BNpcBase {BNpcBaseId}): desired " +
        $"loop {Desired.TimelineId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; " +
        $"{(WasShown ? "was shown" : "never shown")}, attempt {Attempt}: {Detail}" +
        (Release is null ? "" : $" | release: {Release}");
}
