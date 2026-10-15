namespace RSyncStageCraft.Core.Protocol;

/// <summary>
/// Protocol 6: base-game non-human actor bounds. The server and every
/// client validate against the same values.
/// </summary>
public static class ActorLimits
{
    /// <summary>Uniform scale is a multiplier on the creature's own default scale.</summary>
    public const float MinimumScale = 0.25f;
    public const float MaximumScale = 3f;
    public const float DefaultScale = 1f;

    /// <summary>Same coordinate bound as furniture and lights.</summary>
    public const float MaximumCoordinate = 100_000f;

    public static bool IsValidScale(float scale) =>
        float.IsFinite(scale) && scale is >= MinimumScale and <= MaximumScale;

    /// <summary>Yaw in radians, finite and normalized to (−π, π].</summary>
    public static bool IsValidYaw(float yaw) =>
        float.IsFinite(yaw) && yaw > -MathF.PI && yaw <= MathF.PI;

    public static bool IsValidPosition(Vector3State position) =>
        position.IsFinite &&
        MathF.Abs(position.X) <= MaximumCoordinate &&
        MathF.Abs(position.Y) <= MaximumCoordinate &&
        MathF.Abs(position.Z) <= MaximumCoordinate;

    /// <summary>Normalizes any finite angle to (−π, π].</summary>
    public static float NormalizeYaw(float radians)
    {
        if (!float.IsFinite(radians))
            return 0f;
        var turned = MathF.IEEERemainder(radians, MathF.Tau);
        return turned <= -MathF.PI ? turned + MathF.Tau : turned;
    }
}

/// <summary>
/// The one shared actor capacity policy. Server
/// validation, the authoritative store, receiver admission, the client draw
/// limit and the UI all read it; nothing duplicates the numbers. It is not a
/// setting: server and plugin ship from the same commit, so they always agree.
/// Raising <see cref="ActiveMaximumActorsPerScene"/> needs no protocol-format
/// change; whether to raise it is a post-release decision, and it
/// stays 8 for the initial release.
/// </summary>
public static class ActorCapacityPolicy
{
    /// <summary>Design capacity: every bounded actor structure is sized for this.</summary>
    public const int AbsoluteMaximumActorsPerScene = 30;

    /// <summary>
    /// Operational limit: the server refuses more actors
    /// in one scene, and a client draws at most this many RSC actors at once.
    /// </summary>
    public const int ActiveMaximumActorsPerScene = 8;
}

/// <summary>
/// Protocol 6 actor controls. <see cref="TimelineId"/> is a verified,
/// allowlisted looping row (loops only). <see cref="AnimationSequence"/>
/// is reserved: server-owned, never sent by clients, and with no production
/// behaviour (one-shots and replays are not supported); the
/// server keeps it at 0.
/// </summary>
/// <param name="Frozen">
/// <b>Reserved compatibility field; must not be reused without a new
/// protocol and design review.</b> RSC has no freeze (removed: the
/// per-frame overall and slot speeds were investigated and is not reliable across construction and
/// redraw). It always defaults to false and stays in protocol 6 only to avoid
/// a protocol change: the server refuses true on update and restore, and a
/// client refuses a frozen actor before touching or drawing it.
/// </param>
/// <param name="LookAt">
/// <b>Reserved compatibility field; must not be reactivated without a new
/// protocol and design review.</b> RSC has no look-at (removed). It always
/// defaults to empty and stays in
/// protocol 6 only to avoid a protocol change: the server refuses a non-empty
/// value on update and restore, and a client refuses an actor carrying one
/// before touching or drawing it.
/// </param>
public readonly record struct ActorControlState(
    bool Frozen,
    Vector3State? LookAt,
    ushort? TimelineId,
    uint AnimationSequence)
{
    public static ActorControlState Default => new(false, null, null, 0);

    public bool IsValid =>
        (LookAt is null || ActorLimits.IsValidPosition(LookAt.Value)) &&
        TimelineId is null or > 0;

    /// <summary>Not frozen, no animation, and the reserved look-at field empty.</summary>
    public bool IsDefault => !Frozen && LookAt is null && TimelineId is null;
}

/// <summary>
/// Protocol 6 actor: a base-game monster or demihuman, identified only by its
/// BNpcBase row. Model, model type, equipment and default scale are derived on
/// each client from its own game data and never travel. Owner-only editing.
/// </summary>
public sealed record ActorState(
    RscObjectId ObjectId,
    ObjectRevision Revision,
    RscUserId OwnerId,
    RscSceneId? SceneId,
    uint BNpcBaseId,
    Vector3State Position,
    float Yaw,
    float Scale,
    ActorControlState Controls)
    : AuthoritativeObjectState(ObjectId, Revision, SceneId)
{
    public override RscObjectKind Kind => RscObjectKind.Actor;

    public override bool IsValid =>
        CommonIsValid &&
        OwnerId.IsValid &&
        BNpcBaseId > 0 &&
        ActorLimits.IsValidPosition(Position) &&
        ActorLimits.IsValidYaw(Yaw) &&
        ActorLimits.IsValidScale(Scale) &&
        Controls.IsValid;
}
