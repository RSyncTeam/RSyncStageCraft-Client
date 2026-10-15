using System.Numerics;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Lights;

public enum LightCreateStatus
{
    Created,
    /// <summary>The game returned no light; nothing was allocated.</summary>
    Failed,
    /// <summary>The game returned a light without its render part; the backend freed it at once.</summary>
    IncompleteFreed,
}

public readonly record struct LightCreateResult(LightCreateStatus Status, nint Instance)
{
    public bool Created => Status == LightCreateStatus.Created && Instance != 0;
}

/// <summary>
/// The values written to one native point light. <see cref="Color"/> is the
/// native colour (already converted, see <see cref="LightColorConversion"/>).
/// </summary>
public readonly record struct NativeLightSettings(
    Vector3 Position,
    Vector3 Color,
    float Intensity,
    float Range);

/// <summary>
/// Client-only point lights. Every call is made on the game thread. An
/// instance is an opaque, nonzero handle the backend returned from
/// <see cref="Create"/>. Kept separate from the furniture backend.
/// </summary>
public interface ILightSceneBackend
{
    /// <summary>
    /// Creates one hidden point light in client memory only. A light whose
    /// render part is missing is freed here and reported as
    /// <see cref="LightCreateStatus.IncompleteFreed"/>.
    /// </summary>
    LightCreateResult Create();

    /// <summary>
    /// Writes the complete settings and runs the native update sequence. It
    /// never changes visibility.
    /// </summary>
    void Apply(nint instance, NativeLightSettings settings);

    /// <summary>Makes a fully applied light visible.</summary>
    void Show(nint instance);

    // Freeing a created light is always these three calls, in this order,
    // whether or not it was ever shown (an apply can fault before the first
    // show): a created light always has its render part. Only the partial
    // allocation inside Create is freed without render cleanup.

    /// <summary>Makes the light invisible (first step of freeing it).</summary>
    void Hide(nint instance);

    /// <summary>Releases the light's render part (second step).</summary>
    void CleanupRender(nint instance);

    /// <summary>Frees the exact instance (last step); the handle is invalid afterwards.</summary>
    void Free(nint instance);
}

/// <summary>
/// The client-side conversion from the stored colour (normalized display
/// RGB) to the native light colour: each channel squared and scaled by
/// <see cref="NativeScale"/>, as Izzy's Furniture does to match the game's
/// own light editor. White at intensity 1 is native colour (6, 6, 6) at
/// intensity 1. Client-only: changing it never changes stored colours.
/// </summary>
public static class LightColorConversion
{
    public const float NativeScale = 6f;

    public static Vector3 ToNative(LightColor color) => new(
        NativeScale * color.R * color.R,
        NativeScale * color.G * color.G,
        NativeScale * color.B * color.B);

    public static NativeLightSettings ToNative(LightState state, Vector3? position = null) => new(
        position ?? new Vector3(state.Position.X, state.Position.Y, state.Position.Z),
        ToNative(state.Color),
        state.Intensity,
        state.Range);
}
