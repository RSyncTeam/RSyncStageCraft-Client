using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct LocalObjectApplyToken(
    ReceiverSessionKey Session,
    RscObjectId ObjectId,
    ApplyGeneration Generation)
{
    public bool IsValid =>
        Session.IsValid &&
        ObjectId.IsValid &&
        Generation.IsValid;
}

/// <summary>
/// The portion of authoritative state that determines whether an existing
/// local object may be updated or must first be released and respawned.
/// For furniture the stain is a separate typed part (never folded into the
/// wire identity): a stain-only change replaces the exact owned instance,
/// because a live object's stain cannot be cleared without hooks.
/// A protocol 6 actor's identity is its BNpcBase row: any other change
/// (position, yaw, scale, controls) updates the exact owned instance.
/// </summary>
public readonly record struct LocalObjectSpawnIdentity(
    RscObjectKind Kind,
    string Value,
    byte? Stain = null,
    uint? BNpcBaseId = null)
{
    public bool IsValid =>
        Enum.IsDefined(Kind) &&
        !string.IsNullOrWhiteSpace(Value) &&
        Value.Length <= ProtocolLimits.MaximumIdentifierLength &&
        (Kind == RscObjectKind.Actor
            ? BNpcBaseId is > 0 && Value == ActorValue(BNpcBaseId.Value)
            : BNpcBaseId is null) &&
        (Stain is null || Kind == RscObjectKind.Furniture);

    /// <summary>The spawn identity of a protocol 6 actor.</summary>
    public static LocalObjectSpawnIdentity ForActor(uint bnpcBaseId) =>
        new(RscObjectKind.Actor, ActorValue(bnpcBaseId), BNpcBaseId: bnpcBaseId);

    private static string ActorValue(uint bnpcBaseId) =>
        "bnpc:" + bnpcBaseId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static LocalObjectSpawnIdentity FromState(AuthoritativeObjectState state) =>
        state switch
        {
            ActorState actor => ForActor(actor.BNpcBaseId),
            FurnitureState furniture => new(furniture.Kind, furniture.FurnitureIdentity, Stain: furniture.Stain),
            LightState light => new(light.Kind, light.LightIdentity),
            _ => default,
        };
}

public enum LocalObjectReconcileAction
{
    Spawn,
    Update,
    Replace,
}

public sealed record LocalObjectReservation(
    LocalObjectApplyToken Token,
    LocalObjectSpawnIdentity DesiredIdentity,
    LocalObjectSpawnIdentity? OwnedIdentity,
    LocalObjectInstanceHandle? Handle,
    LocalObjectInstanceHandle? Compensation,
    long NextSpawnGeneration,
    bool SpawnGenerationExhausted)
{
    public LocalObjectReconcileAction Action => Handle is null
        ? LocalObjectReconcileAction.Spawn
        : OwnedIdentity == DesiredIdentity
            ? LocalObjectReconcileAction.Update
            : LocalObjectReconcileAction.Replace;

    public bool IsValid =>
        Token.IsValid &&
        DesiredIdentity.IsValid &&
        NextSpawnGeneration > 0 &&
        (Compensation is null || Compensation.Value.IsValid) &&
        (Handle is null || Compensation is null ||
         Handle.Value.OwnershipId != Compensation.Value.OwnershipId) &&
        ((Handle is null && OwnedIdentity is null) ||
         (Handle is { IsValid: true } owned &&
          OwnedIdentity is { IsValid: true } identity &&
          owned.ObjectId == Token.ObjectId &&
          owned.Kind == identity.Kind));
}

public enum LocalObjectReservationStatus
{
    Advanced,
    Unchanged,
    Invalid,
    CapacityExceeded,
    StaleGeneration,
    GenerationConflict,
    SessionConflict,
    ObjectKindConflict,
    ReleaseInProgress,
    MutationInProgress,
}

public readonly record struct LocalObjectReservationResult(
    LocalObjectReservationStatus Status,
    LocalObjectReservation? Reservation)
{
    public bool Accepted => Status is
        LocalObjectReservationStatus.Advanced or
        LocalObjectReservationStatus.Unchanged;
}

public enum LocalObjectReconcileStatus
{
    Applied,
    Stale,
    CapacityExceeded,
    Unavailable,
    TransientFailure,
    PermanentFailure,
    Cancelled,
}

public readonly record struct LocalObjectReconcileResult(
    LocalObjectReconcileStatus Status,
    LocalObjectInstanceHandle? Handle = null,
    string? Error = null)
{
    public bool Succeeded => Status == LocalObjectReconcileStatus.Applied;
    public bool Retryable => Status is
        LocalObjectReconcileStatus.CapacityExceeded or
        LocalObjectReconcileStatus.Unavailable or
        LocalObjectReconcileStatus.TransientFailure;
}
