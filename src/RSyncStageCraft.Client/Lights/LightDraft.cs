using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Lights;

public readonly record struct LightDraftApply(
    FurnitureDraftApplyStatus Status,
    LightState? Basis,
    LightPointPatch? Patch,
    string? Message);

/// <summary>
/// The selected light's typed fields (position, colour, brightness, range)
/// as one draft, like <see cref="FurnitureTransformDraft"/> for furniture:
/// editing changes only the draft; <see cref="TryApply"/> builds one atomic
/// <see cref="LightPointPatch"/> from the revision the edits started from.
/// Values within display rounding of the shown state count as unchanged, so
/// applying an untouched draft is a no-op. Out-of-bounds or non-finite
/// values are refused before anything is sent.
/// </summary>
public sealed class LightDraft
{
    private const float PositionTolerance = 0.0006f;  // shown with three decimals
    private const float ValueTolerance = 0.006f;      // intensity and range, two decimals
    private const float ColorTolerance = 0.0005f;

    private LightState? basis;
    private LightState? latest;

    public RscObjectId? ObjectId => basis?.ObjectId;
    public Vector3 Position { get; private set; }
    public LightColor Color { get; private set; } = LightColor.White;
    public float Intensity { get; private set; }
    public float Range { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>Every frame, with the selected light's authoritative state (null when none).</summary>
    public void Sync(LightState? state, bool locked)
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

    public void Edit(Vector3 position, LightColor color, float intensity, float range)
    {
        if (basis is null)
            return;
        if (position.Equals(Position) && color.Equals(Color) && intensity.Equals(Intensity) && range.Equals(Range))
            return;
        Position = position;
        Color = color;
        Intensity = intensity;
        Range = range;
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

    public LightDraftApply TryApply()
    {
        if (basis is null || latest is null || latest.ObjectId != basis.ObjectId)
            return new(FurnitureDraftApplyStatus.NoItem, null, null, null);
        if (latest != basis)
        {
            Load(latest);
            return new(FurnitureDraftApplyStatus.Stale, null, null, "It changed meanwhile; showing the current values.");
        }
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z) ||
            !float.IsFinite(Color.R) || !float.IsFinite(Color.G) || !float.IsFinite(Color.B) ||
            !float.IsFinite(Intensity) || !float.IsFinite(Range))
            return Invalid("Enter numbers only.");

        var shownPosition = new Vector3(basis.Position.X, basis.Position.Y, basis.Position.Z);
        var position = new Vector3(
            Pick(Position.X, shownPosition.X, PositionTolerance),
            Pick(Position.Y, shownPosition.Y, PositionTolerance),
            Pick(Position.Z, shownPosition.Z, PositionTolerance));
        if (!FurnitureTransforms.IsValidPosition(position))
            return Invalid($"Coordinates must be between -{FurnitureTransforms.MaximumCoordinate:0} and {FurnitureTransforms.MaximumCoordinate:0}.");
        var color = new LightColor(
            Pick(Color.R, basis.Color.R, ColorTolerance),
            Pick(Color.G, basis.Color.G, ColorTolerance),
            Pick(Color.B, basis.Color.B, ColorTolerance));
        if (!color.IsValid)
            return Invalid("The colour must not be black, and each channel must be between 0 and 1.");
        var intensity = Pick(Intensity, basis.Intensity, ValueTolerance);
        if (!LightLimits.IsValidIntensity(intensity))
            return Invalid($"Brightness must be between {LightLimits.MinimumIntensity} and {LightLimits.MaximumIntensity}.");
        var range = Pick(Range, basis.Range, ValueTolerance);
        if (!LightLimits.IsValidRange(range))
            return Invalid($"Range must be between {LightLimits.MinimumRange} and {LightLimits.MaximumRange} yalms.");

        var patch = new LightPointPatch(
            position == shownPosition ? null : new Vector3State(position.X, position.Y, position.Z),
            color == basis.Color ? null : color,
            intensity == basis.Intensity ? null : intensity,
            range == basis.Range ? null : range);
        if (patch.Position is null && patch.Color is null && patch.Intensity is null && patch.Range is null)
        {
            Load(basis);
            return new(FurnitureDraftApplyStatus.NothingChanged, null, null, null);
        }
        IsDirty = false;
        return new(FurnitureDraftApplyStatus.Ready, basis, patch, null);
    }

    private LightDraftApply Invalid(string message) =>
        new(FurnitureDraftApplyStatus.Invalid, null, null, message);

    private void Load(LightState state)
    {
        basis = state;
        Position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
        Color = state.Color;
        Intensity = state.Intensity;
        Range = state.Range;
        IsDirty = false;
    }

    private static float Pick(float typed, float shown, float tolerance) =>
        MathF.Abs(typed - shown) <= tolerance ? shown : typed;
}
