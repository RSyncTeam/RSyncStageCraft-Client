namespace RSyncStageCraft.Core.Protocol;

public enum RscObjectKind
{
    Actor,
    Furniture,
    Light,

    /// <summary>Protocol 7: a shared human actor drawn from an appearance bundle.</summary>
    Human,
}

public readonly record struct RscObjectId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
}

public readonly record struct RscSceneId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
}

public readonly record struct RscUserId(string Value)
{
    public bool IsValid => ContractValidation.ValidIdentifier(Value);
}

/// <summary>
/// Optional presentation text for an account (protocol 6). Never an identity:
/// names may repeat, change or be absent, and nothing decides ownership or
/// editing rights by them; <see cref="RscUserId"/> does. Valid: 1 to 64 UTF-8
/// bytes of well-formed text, no control characters, no leading or trailing
/// whitespace.
/// </summary>
public static class RscDisplayName
{
    public const int MaximumUtf8Bytes = 64;
    private static readonly System.Text.UTF8Encoding Strict = new(false, true);

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUtf8Bytes ||
            char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return false;
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return false;
        }
        try
        {
            return Strict.GetByteCount(value) <= MaximumUtf8Bytes;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false; // an unpaired surrogate
        }
    }
}

/// <summary>An account's optional display name, keyed by its stable id (server to client only, display only).</summary>
public readonly record struct OwnerDisplayName(RscUserId UserId, string DisplayName)
{
    public bool IsValid => UserId.IsValid && RscDisplayName.IsValid(DisplayName);
}

public readonly record struct ObjectRevision(long Value)
{
    public bool IsValid => Value > 0;
}

public readonly record struct ServerSequence(long Value)
{
    public bool IsValid => Value >= 0;
}

public readonly record struct Vector3State(float X, float Y, float Z)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    public bool IsPositive => IsFinite && X > 0 && Y > 0 && Z > 0;
}

public readonly record struct RotationState(float X, float Y, float Z, float W)
{
    public bool IsValid =>
        float.IsFinite(X) &&
        float.IsFinite(Y) &&
        float.IsFinite(Z) &&
        float.IsFinite(W) &&
        (X != 0 || Y != 0 || Z != 0 || W != 0);
}

public abstract record AuthoritativeObjectState(
    RscObjectId ObjectId,
    ObjectRevision Revision,
    RscSceneId? SceneId)
{
    public abstract RscObjectKind Kind { get; }
    public abstract bool IsValid { get; }

    /// <summary>
    /// The location of this object's scene, set by the server (protocol 3).
    /// Clients compare it with their own local location and draw the object
    /// only on an exact match; a missing location is never drawn. Persisted
    /// scenes store it once per scene, not per object.
    /// </summary>
    public SceneLocation? Location { get; init; }

    protected bool CommonIsValid =>
        ObjectId.IsValid &&
        Revision.IsValid &&
        (SceneId is null || SceneId.Value.IsValid) &&
        (Location is null || (SceneId is not null && Location.Value.IsValid));
}

public sealed record FurnitureState(
    RscObjectId ObjectId,
    ObjectRevision Revision,
    RscUserId CreatorId,
    RscSceneId? SceneId,
    string FurnitureIdentity,
    Vector3State Position,
    RotationState Rotation)
    : AuthoritativeObjectState(ObjectId, Revision, SceneId)
{
    public override RscObjectKind Kind => RscObjectKind.Furniture;

    /// <summary>
    /// Protocol 4: the furniture's dye, as the game's canonical stain row ID;
    /// 0 is Default (undyed). Only the ID crosses the wire. The server checks
    /// only that it is a byte; each client decides whether it can draw it.
    /// A scene file saved before protocol 4 has no stain and reads as 0.
    /// </summary>
    public byte Stain { get; init; }

    public override bool IsValid =>
        CommonIsValid &&
        CreatorId.IsValid &&
        // Protocol 3: restored and received states are held to the same
        // canonical furniture:indoor|outdoor:<item> form as creates.
        Protocol.FurnitureIdentity.IsCanonical(FurnitureIdentity) &&
        Position.IsFinite &&
        Rotation.IsValid;
}

public sealed record LightState(
    RscObjectId ObjectId,
    ObjectRevision Revision,
    RscUserId CreatorId,
    RscSceneId? SceneId,
    string LightIdentity,
    Vector3State Position,
    Vector3State Scale)
    : AuthoritativeObjectState(ObjectId, Revision, SceneId)
{
    public override RscObjectKind Kind => RscObjectKind.Light;

    /// <summary>Protocol 5: normalized display RGB.</summary>
    public LightColor Color { get; init; } = LightColor.White;

    /// <summary>Protocol 5: brightness, in [<see cref="LightLimits.MinimumIntensity"/>, <see cref="LightLimits.MaximumIntensity"/>].</summary>
    public float Intensity { get; init; } = LightLimits.DefaultIntensity;

    /// <summary>Protocol 5: reach in yalms, in [<see cref="LightLimits.MinimumRange"/>, <see cref="LightLimits.MaximumRange"/>]. Never derived from scale.</summary>
    public float Range { get; init; } = LightLimits.DefaultRange;

    // Protocol 5: only light:point, with a fixed unit scale and bounded
    // colour, intensity and range, in creates, received and restored states.
    public override bool IsValid =>
        CommonIsValid &&
        CreatorId.IsValid &&
        Protocol.LightIdentity.IsCanonical(LightIdentity) &&
        Position.IsFinite &&
        LightLimits.IsUnitScale(Scale) &&
        Color.IsValid &&
        LightLimits.IsValidIntensity(Intensity) &&
        LightLimits.IsValidRange(Range);
}

internal static class ContractValidation
{
    public static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= ProtocolLimits.MaximumIdentifierLength;
}
