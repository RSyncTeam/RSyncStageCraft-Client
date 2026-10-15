namespace RSyncStageCraft.Core.Protocol;

/// <summary>
/// Protocol 5: the only light type, a point light. The server accepts
/// no other identity; any other value is never drawn.
/// </summary>
public static class LightIdentity
{
    public const string Point = "light:point";

    public static bool IsCanonical(string? identity) =>
        string.Equals(identity, Point, StringComparison.Ordinal);
}

/// <summary>
/// A light's colour as normalized display RGB, each channel in [0, 1], as
/// picked in the colour editor. This is the only stored colour; clients
/// convert it to the native light value themselves.
/// </summary>
public readonly record struct LightColor(float R, float G, float B)
{
    public static LightColor White => new(1, 1, 1);

    public bool IsValid =>
        InRange(R) && InRange(G) && InRange(B) &&
        (R > 0 || G > 0 || B > 0);

    private static bool InRange(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>Protocol 5 bounds for point lights, checked by the server and again by clients.</summary>
public static class LightLimits
{
    public const float MinimumIntensity = 0.05f;
    public const float MaximumIntensity = 16f;
    public const float MinimumRange = 0.5f;
    public const float MaximumRange = 64f;

    public const float DefaultIntensity = 1f;
    public const float DefaultRange = 8f;

    /// <summary>Point lights have no scale: it is always exactly one.</summary>
    public static readonly Vector3State UnitScale = new(1, 1, 1);

    public static bool IsValidIntensity(float value) =>
        float.IsFinite(value) && value is >= MinimumIntensity and <= MaximumIntensity;

    public static bool IsValidRange(float value) =>
        float.IsFinite(value) && value is >= MinimumRange and <= MaximumRange;

    public static bool IsUnitScale(Vector3State scale) => scale == UnitScale;
}
