using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Actors;

public readonly record struct ActorDraftApply(
    FurnitureDraftApplyStatus Status,
    ActorState? Basis,
    ActorTransformPatch? Patch,
    string? Message);

/// <summary>
/// The selected actor's typed fields (X, Y, Z, Facing in degrees, Scale) as
/// one draft, like <see cref="Lights.LightDraft"/>: editing changes only the
/// draft; <see cref="TryApply"/> builds one <see cref="ActorTransformPatch"/>
/// from the revision the edits started from. Values within display rounding of
/// the shown state count as unchanged. Out-of-bounds or non-finite values are
/// refused before anything is sent.
/// </summary>
public sealed class ActorDraft
{
    private const float PositionTolerance = 0.0006f; // shown with three decimals
    private const float ScaleTolerance = 0.0006f;    // shown with three decimals

    private ActorState? basis;
    private ActorState? latest;

    public RscObjectId? ObjectId => basis?.ObjectId;
    public Vector3 Position { get; private set; }

    /// <summary>Facing in degrees, 0 (inclusive) to 360 (exclusive).</summary>
    public float Facing { get; private set; }

    public float Scale { get; private set; } = ActorLimits.DefaultScale;
    public bool IsDirty { get; private set; }

    /// <summary>Every frame, with the selected actor's authoritative state (null when none).</summary>
    public void Sync(ActorState? state, bool locked)
    {
        latest = state;
        if (state is null)
        {
            basis = null;
            IsDirty = false;
            return;
        }
        if (basis is null || basis.ObjectId != state.ObjectId)
        {
            Load(state);
            return;
        }
        if (!IsDirty && !locked)
            Load(state);
    }

    public void Edit(Vector3 position, float facingDegrees, float scale)
    {
        if (basis is null)
            return;
        var facing = float.IsFinite(facingDegrees) ? FurnitureTransformDraft.NormalizeDegrees(facingDegrees) : facingDegrees;
        if (position.Equals(Position) && facing.Equals(Facing) && scale.Equals(Scale))
            return;
        Position = position;
        Facing = facing;
        Scale = scale;
        IsDirty = true;
    }

    public void Reset()
    {
        if (latest is not null)
            Load(latest);
        else
        {
            basis = null;
            IsDirty = false;
        }
    }

    public ActorDraftApply TryApply()
    {
        if (basis is null || latest is null || latest.ObjectId != basis.ObjectId)
            return new(FurnitureDraftApplyStatus.NoItem, null, null, null);
        if (latest != basis)
        {
            Load(latest);
            return new(FurnitureDraftApplyStatus.Stale, null, null, "It changed meanwhile; showing the current values.");
        }
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z) ||
            !float.IsFinite(Facing) || !float.IsFinite(Scale))
            return Invalid("Enter numbers only.");

        var shownPosition = new Vector3(basis.Position.X, basis.Position.Y, basis.Position.Z);
        var position = new Vector3(
            Pick(Position.X, shownPosition.X, PositionTolerance),
            Pick(Position.Y, shownPosition.Y, PositionTolerance),
            Pick(Position.Z, shownPosition.Z, PositionTolerance));
        var positionState = new Vector3State(position.X, position.Y, position.Z);
        if (!ActorLimits.IsValidPosition(positionState))
            return Invalid($"Coordinates must be between -{ActorLimits.MaximumCoordinate:0} and {ActorLimits.MaximumCoordinate:0}.");
        var scale = Pick(Scale, basis.Scale, ScaleTolerance);
        if (!ActorLimits.IsValidScale(scale))
            return Invalid($"Scale must be between {ActorLimits.MinimumScale} and {ActorLimits.MaximumScale}.");
        var shownFacing = FacingOf(basis);
        float? yaw = DegreesApart(Facing, shownFacing) <= FurnitureTransformDraft.FacingStep
            ? null
            : ActorLimits.NormalizeYaw(Facing * MathF.PI / 180f);

        var patch = new ActorTransformPatch(
            position == shownPosition ? null : positionState,
            yaw,
            scale == basis.Scale ? null : scale);
        if (patch.Position is null && patch.Yaw is null && patch.Scale is null)
        {
            Load(basis);
            return new(FurnitureDraftApplyStatus.NothingChanged, null, null, null);
        }
        IsDirty = false;
        return new(FurnitureDraftApplyStatus.Ready, basis, patch, null);
    }

    /// <summary>The facing of an actor in degrees, 0 to 360, as the fields show it.</summary>
    public static float FacingOf(ActorState state) =>
        FurnitureTransformDraft.NormalizeDegrees(state.Yaw * 180f / MathF.PI);

    private ActorDraftApply Invalid(string message) =>
        new(FurnitureDraftApplyStatus.Invalid, null, null, message);

    private void Load(ActorState state)
    {
        basis = state;
        Position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
        Facing = FacingOf(state);
        Scale = state.Scale;
        IsDirty = false;
    }

    private static float DegreesApart(float a, float b)
    {
        var difference = MathF.Abs(a - b) % 360f;
        return difference > 180f ? 360f - difference : difference;
    }

    private static float Pick(float typed, float shown, float tolerance) =>
        MathF.Abs(typed - shown) <= tolerance ? shown : typed;
}
