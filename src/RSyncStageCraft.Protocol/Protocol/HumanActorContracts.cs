namespace RSyncStageCraft.Core.Protocol;

/// <summary>
/// Protocol 7: the shared human actor's bounds. The server and every client
/// validate against the same values.
/// </summary>
public static class HumanActorLimits
{
    /// <summary>
    /// Candidate capacity rule: at most this many human actors in one scene,
    /// and no base-game (BNpc) actor in a scene that holds one, or the
    /// reverse. Humans also count towards
    /// <see cref="ActorCapacityPolicy.ActiveMaximumActorsPerScene"/>.
    /// </summary>
    public const int MaximumHumansPerScene = 1;

    /// <summary>
    /// The bundle id is the bundle's content hash: exactly 64 lowercase
    /// hexadecimal characters, never normalized.
    /// </summary>
    public static bool IsValidBundleId(string? value)
    {
        if (value is not { Length: 64 })
            return false;
        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Protocol 7: a shared human actor. It names one immutable appearance bundle
/// by its id (the SHA-256 of the canonical manifest); the bundle id never
/// changes after creation and has no patch path. It carries no label, name,
/// synthetic puppet name, object index, collection or receipt; every client
/// derives those locally. Position and yaw only: no scale and no controls.
/// Owner-only editing, as for every actor.
/// </summary>
public sealed record HumanActorState(
    RscObjectId ObjectId,
    ObjectRevision Revision,
    RscUserId OwnerId,
    RscSceneId? SceneId,
    string AppearanceBundleId,
    Vector3State Position,
    float Yaw)
    : AuthoritativeObjectState(ObjectId, Revision, SceneId)
{
    public override RscObjectKind Kind => RscObjectKind.Human;

    public override bool IsValid =>
        CommonIsValid &&
        OwnerId.IsValid &&
        HumanActorLimits.IsValidBundleId(AppearanceBundleId) &&
        ActorLimits.IsValidPosition(Position) &&
        ActorLimits.IsValidYaw(Yaw);
}

/// <summary>
/// Protocol 7: creates a shared human actor from one of the caller's own,
/// group-published, format-3 appearance bundles. The server checks the
/// bundle; a create never carries appearance content.
/// </summary>
public sealed record HumanActorCreatePayload(
    RscSceneId? SceneId,
    string AppearanceBundleId,
    Vector3State Position,
    float Yaw,
    SceneLocation? Location = null)
    : ObjectCreatePayload(SceneId, Location)
{
    public override RscObjectKind Kind => RscObjectKind.Human;

    public override bool IsValid =>
        SceneIsValid &&
        HumanActorLimits.IsValidBundleId(AppearanceBundleId) &&
        ActorLimits.IsValidPosition(Position) &&
        ActorLimits.IsValidYaw(Yaw);
}

/// <summary>
/// Protocol 7: the owner's human actor transform change, position and yaw
/// only. Separate from <see cref="ActorTransformPatch"/>, which is
/// actor-kind and carries a scale.
/// </summary>
public sealed record HumanTransformPatch(
    Vector3State? Position,
    float? Yaw) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Human;
    public override bool IsValid =>
        (Position is not null || Yaw is not null) &&
        (Position is not { } position || ActorLimits.IsValidPosition(position)) &&
        (Yaw is not { } yaw || ActorLimits.IsValidYaw(yaw));
}
