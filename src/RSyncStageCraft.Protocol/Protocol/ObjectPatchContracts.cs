namespace RSyncStageCraft.Core.Protocol;

public abstract record ObjectPatch
{
    public abstract RscObjectKind Kind { get; }
    public abstract bool IsValid { get; }
}

/// <summary>Protocol 6: an owner's actor transform change (yaw only, uniform scale).</summary>
public sealed record ActorTransformPatch(
    Vector3State? Position,
    float? Yaw,
    float? Scale) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Actor;
    public override bool IsValid =>
        (Position is not null || Yaw is not null || Scale is not null) &&
        (Position is not { } position || ActorLimits.IsValidPosition(position)) &&
        (Yaw is not { } yaw || ActorLimits.IsValidYaw(yaw)) &&
        (Scale is not { } scale || ActorLimits.IsValidScale(scale));
}

/// <summary>
/// Protocol 6: an owner's actor control change. Setting and clearing the same
/// field together is invalid. The animation sequence is reserved (the server
/// keeps it at 0).
/// </summary>
/// <param name="LookAt">
/// <b>Reserved compatibility field; must not be reactivated without a new
/// protocol and design review.</b> RSC has no look-at (removed). It is kept
/// only so protocol 6 does not change:
/// no UI or local code path may set it, and the server refuses any patch that
/// carries it (or <paramref name="ClearLookAt"/>) in full, together with
/// every other field in that patch.
/// </param>
/// <param name="ClearLookAt">Reserved with <paramref name="LookAt"/>; always false.</param>
/// <param name="Frozen">
/// <b>Reserved compatibility field; must not be reused without a new
/// protocol and design review.</b> RSC has no freeze (removed). No UI or local code path sets it, and the
/// server refuses a patch carrying <c>true</c> in full, with every other field
/// in that patch.
/// </param>
public sealed record ActorControlPatch(
    bool? Frozen,
    Vector3State? LookAt,
    bool ClearLookAt,
    ushort? TimelineId,
    bool ClearTimeline) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Actor;
    public override bool IsValid =>
        (Frozen is not null || LookAt is not null || ClearLookAt || TimelineId is not null || ClearTimeline) &&
        !(LookAt is not null && ClearLookAt) &&
        !(TimelineId is not null && ClearTimeline) &&
        (LookAt is not { } lookAt || ActorLimits.IsValidPosition(lookAt)) &&
        TimelineId is null or > 0;
}

public sealed record FurnitureTransformPatch(
    Vector3State? Position,
    RotationState? Rotation) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Furniture;
    public override bool IsValid =>
        (Position is not null || Rotation is not null) &&
        (Position?.IsFinite ?? true) &&
        (Rotation?.IsValid ?? true);
}

/// <summary>
/// Protocol 4: sets the furniture's stain (0 = Default, undyed). Like every
/// update it carries a base revision, so a stale change is refused.
/// </summary>
public sealed record FurnitureStainPatch(byte Stain) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Furniture;
    public override bool IsValid => true;
}

/// <summary>
/// Protocol 5: the one update for a point light; any of its position,
/// colour, intensity and range, at least one, each within the protocol 5
/// bounds, applied atomically. Replaces the protocol 4 light transform patch
/// (lights have a fixed unit scale).
/// </summary>
public sealed record LightPointPatch(
    Vector3State? Position,
    LightColor? Color,
    float? Intensity,
    float? Range) : ObjectPatch
{
    public override RscObjectKind Kind => RscObjectKind.Light;
    public override bool IsValid =>
        (Position is not null || Color is not null || Intensity is not null || Range is not null) &&
        (Position?.IsFinite ?? true) &&
        (Color?.IsValid ?? true) &&
        (Intensity is not { } intensity || LightLimits.IsValidIntensity(intensity)) &&
        (Range is not { } range || LightLimits.IsValidRange(range));
}
