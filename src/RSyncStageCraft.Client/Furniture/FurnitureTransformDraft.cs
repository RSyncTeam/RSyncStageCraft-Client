using System.Numerics;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

public enum FurnitureDraftApplyStatus
{
    /// <summary>A valid change: send exactly one update from <see cref="FurnitureDraftApply.Basis"/>.</summary>
    Ready,
    /// <summary>The draft equals the shown state: nothing is sent.</summary>
    NothingChanged,
    /// <summary>A value is not a number, infinite or out of range: nothing is sent.</summary>
    Invalid,
    /// <summary>The item changed since editing began: nothing is sent and the draft shows the current state.</summary>
    Stale,
    /// <summary>No item is being edited.</summary>
    NoItem,
}

public readonly record struct FurnitureDraftApply(
    FurnitureDraftApplyStatus Status,
    FurnitureState? Basis,
    Vector3 Position,
    Quaternion Rotation,
    string? Message);

/// <summary>
/// The typed X, Y, Z and Facing fields of the selected furniture, as one
/// draft transform. Typing changes only this draft: nothing is sent and
/// nothing is drawn until <see cref="TryApply"/> builds exactly one transform
/// from the revision the edits started from. While the draft is untouched it
/// follows the authoritative state; once edited it keeps the player's values
/// (and the revision they were made against) until applied or reset.
/// <para>
/// Fields are shown rounded (<see cref="PositionStep"/>, <see cref="FacingStep"/>);
/// a field within that rounding of the shown state counts as unchanged and
/// keeps the exact authoritative value, so applying an untouched draft is
/// always a no-op. Changing the facing turns the item around the vertical
/// axis only.
/// </para>
/// </summary>
public sealed class FurnitureTransformDraft
{
    /// <summary>Position fields are shown with three decimals.</summary>
    public const float PositionStep = 0.001f;

    /// <summary>Facing is shown with two decimals, in degrees.</summary>
    public const float FacingStep = 0.01f;

    // Half a display step, plus a little for float rounding of the shown text.
    private const float RoundingSlack = 0.6f;

    private FurnitureState? basis;
    private FurnitureState? latest;

    public RscObjectId? ObjectId => basis?.ObjectId;
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Z { get; private set; }

    /// <summary>Facing in degrees, normalized to 0 (inclusive) to 360 (exclusive).</summary>
    public float Facing { get; private set; }

    /// <summary>The player changed a field since the draft last showed the authoritative state.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Every frame, with the selected item's authoritative state (null when
    /// nothing is selected). Another item, or an untouched draft while not
    /// <paramref name="locked"/>, shows the authoritative values; an edited
    /// draft, or any draft while an update is saving, keeps its values.
    /// </summary>
    public void Sync(FurnitureState? state, bool locked)
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

    /// <summary>
    /// The player's field values. Invalid numbers are kept, so the player can
    /// see and correct them; <see cref="TryApply"/> refuses them. Facing is
    /// normalized when it is a finite number.
    /// </summary>
    public void Edit(float x, float y, float z, float facing)
    {
        if (basis is null)
            return;
        var normalizedFacing = float.IsFinite(facing) ? NormalizeDegrees(facing) : facing;
        if (Same(x, X) && Same(y, Y) && Same(z, Z) && Same(normalizedFacing, Facing))
            return;
        X = x;
        Y = y;
        Z = z;
        Facing = normalizedFacing;
        IsDirty = true;
    }

    /// <summary>Discards the edits and shows the latest authoritative state.</summary>
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

    /// <summary>
    /// Builds the one transform to send. Only <see cref="FurnitureDraftApplyStatus.Ready"/>
    /// sends anything. After Ready the draft stops being dirty (it then
    /// follows the authoritative state again once nothing is saving); after
    /// Stale or NothingChanged it shows the latest authoritative state.
    /// </summary>
    public FurnitureDraftApply TryApply()
    {
        if (basis is null || latest is null || latest.ObjectId != basis.ObjectId)
            return new(FurnitureDraftApplyStatus.NoItem, null, default, default, null);
        if (latest != basis)
        {
            Load(latest);
            return new(FurnitureDraftApplyStatus.Stale, null, default, default,
                "It changed meanwhile; showing the current values.");
        }
        if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z) || !float.IsFinite(Facing))
            return new(FurnitureDraftApplyStatus.Invalid, null, default, default, "Enter numbers only.");

        var shownPosition = FurnitureLocalObjectHost.ToVector(basis.Position);
        var shownRotation = FurnitureLocalObjectHost.ToQuaternion(basis.Rotation);
        var position = new Vector3(
            Pick(X, shownPosition.X, PositionStep),
            Pick(Y, shownPosition.Y, PositionStep),
            Pick(Z, shownPosition.Z, PositionStep));
        if (!FurnitureTransforms.IsValidPosition(position))
            return new(FurnitureDraftApplyStatus.Invalid, null, default, default,
                $"Coordinates must be between -{FurnitureTransforms.MaximumCoordinate:0} and {FurnitureTransforms.MaximumCoordinate:0}.");

        var shownFacing = FacingOf(basis);
        var rotation = DegreesApart(Facing, shownFacing) <= FacingStep * RoundingSlack
            ? shownRotation
            : FurnitureTransforms.WithYawOf(
                shownRotation,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, Facing * MathF.PI / 180));
        if (!FurnitureTransforms.TryNormalize(rotation, out var normalized))
            return new(FurnitureDraftApplyStatus.Invalid, null, default, default, "Invalid facing.");

        if (FurnitureTransforms.SameTransform(shownPosition, shownRotation, position, normalized))
        {
            Load(basis);
            return new(FurnitureDraftApplyStatus.NothingChanged, null, default, default, null);
        }

        IsDirty = false;
        return new(FurnitureDraftApplyStatus.Ready, basis, position, normalized, null);
    }

    /// <summary>Degrees in 0 (inclusive) to 360 (exclusive), rounded to <see cref="FacingStep"/>.</summary>
    public static float NormalizeDegrees(float degrees)
    {
        if (!float.IsFinite(degrees))
            return 0;
        var wrapped = degrees % 360f;
        if (wrapped < 0)
            wrapped += 360f;
        var rounded = MathF.Round(wrapped / FacingStep) * FacingStep;
        return rounded >= 360f - FacingStep / 2 ? 0f : rounded;
    }

    /// <summary>The shown facing of an item, in degrees (0 to 360).</summary>
    public static float FacingOf(FurnitureState state) =>
        NormalizeDegrees(FurnitureTransforms.Yaw(FurnitureLocalObjectHost.ToQuaternion(state.Rotation)) * 180 / MathF.PI);

    private void Load(FurnitureState state)
    {
        basis = state;
        X = state.Position.X;
        Y = state.Position.Y;
        Z = state.Position.Z;
        Facing = FacingOf(state);
        IsDirty = false;
    }

    /// <summary>The typed value, or the exact shown one when the typed value only differs by display rounding.</summary>
    private static float Pick(float typed, float shown, float step) =>
        MathF.Abs(typed - shown) <= step * RoundingSlack ? shown : typed;

    private static float DegreesApart(float a, float b)
    {
        var difference = MathF.Abs(a - b) % 360f;
        return difference > 180f ? 360f - difference : difference;
    }

    private static bool Same(float a, float b) => a.Equals(b);
}

/// <summary>
/// Increments of the scrub controls for the transform draft. Dragging moves
/// one increment per pixel and each mouse-wheel notch one increment; holding
/// Alt makes it ten times finer and Shift ten times coarser, the same for
/// dragging and the wheel (RSC applies both itself, not ImGui's drag). These only change the local draft.
/// </summary>
public static class FurnitureScrub
{
    /// <summary>Position increment, in yalms.</summary>
    public const float PositionStep = 0.01f;

    /// <summary>Facing increment for the wheel and the chevrons, in degrees.</summary>
    public const float FacingStep = 1f;

    /// <summary>Facing change per dragged pixel, in degrees.</summary>
    public const float FacingDragStep = 0.5f;

    public const float FineFactor = 0.1f;
    public const float CoarseFactor = 10f;

    /// <summary>The increment with the held modifiers applied (fine wins over coarse).</summary>
    public static float Step(float baseStep, bool fine, bool coarse) =>
        fine ? baseStep * FineFactor : coarse ? baseStep * CoarseFactor : baseStep;

    /// <summary>
    /// <paramref name="value"/> moved by <paramref name="notches"/> wheel
    /// notches of <paramref name="step"/>. A non-finite value or wheel input
    /// leaves the value unchanged.
    /// </summary>
    public static float Wheel(float value, float notches, float step) => Move(value, notches, step);

    /// <summary>
    /// <paramref name="value"/> moved by a horizontal drag of
    /// <paramref name="pixels"/> at <paramref name="step"/> per pixel (with
    /// the modifiers already applied through <see cref="Step"/>). A
    /// non-finite value or drag leaves the value unchanged.
    /// </summary>
    public static float Drag(float value, float pixels, float step) => Move(value, pixels, step);

    private static float Move(float value, float notches, float step)
    {
        if (!float.IsFinite(value) || !float.IsFinite(notches) || !float.IsFinite(step) || notches == 0)
            return value;
        var next = value + notches * step;
        return float.IsFinite(next) ? next : value;
    }
}
