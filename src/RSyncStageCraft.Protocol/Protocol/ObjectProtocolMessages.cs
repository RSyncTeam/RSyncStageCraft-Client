namespace RSyncStageCraft.Core.Protocol;

public enum ObjectProtocolMessageType
{
    CreateObject,
    UpdateObject,
    DeleteObject,
    ObjectSnapshot,
    ObjectCreated,
    ObjectUpdated,
    ObjectDeleted,
    AcknowledgeObjects,
    Rejected,
    RequestObjectSnapshot,
    RequestObjectEvents,
    SaveScene,
    ReadScene,
    ObjectEventBatchCompleted,
    SceneSaveCompleted,
    SceneReadCompleted,
    ObjectMutationCompleted,
    ObjectSnapshotCompleted,
}

public readonly record struct SessionMessageEnvelope(
    int ProtocolVersion,
    ObjectProtocolMessageType MessageType,
    Guid MessageId,
    long SessionId,
    long SessionGeneration,
    long ConnectionEpoch)
{
    public bool IsValid => IsValidForVersion(ProtocolConstants.CurrentVersion);

    public bool IsValidForVersion(int version) =>
        version == ProtocolConstants.CurrentVersion &&
        ProtocolVersion == version &&
        Enum.IsDefined(MessageType) &&
        MessageId != Guid.Empty &&
        SessionId > 0 &&
        SessionGeneration > 0 &&
        ConnectionEpoch > 0;
}

public readonly record struct ObjectRequestRejected(
    Guid RequestId,
    ProtocolRejectReason Reason,
    ObjectRevision? CurrentRevision)
{
    public bool IsValid =>
        RequestId != Guid.Empty &&
        Reason is not ProtocolRejectReason.None &&
        Enum.IsDefined(Reason) &&
        (CurrentRevision is null || CurrentRevision.Value.IsValid);
}

public abstract record ObjectCreatePayload(RscSceneId? SceneId, SceneLocation? Location = null)
{
    public abstract RscObjectKind Kind { get; }
    public abstract bool IsValid { get; }

    // Fail closed (protocol 3): every create names its scene and carries the
    // complete location it was placed at. An object can never be unlocated.
    protected bool SceneIsValid =>
        SceneId is { IsValid: true } &&
        Location is { IsValid: true };
}

/// <summary>
/// Protocol 6: creates a base-game monster or demihuman by its BNpcBase row.
/// Controls start at their defaults; they change only by patch.
/// </summary>
public sealed record ActorCreatePayload(
    RscSceneId? SceneId,
    uint BNpcBaseId,
    Vector3State Position,
    float Yaw,
    float Scale,
    SceneLocation? Location = null)
    : ObjectCreatePayload(SceneId, Location)
{
    public override RscObjectKind Kind => RscObjectKind.Actor;

    public override bool IsValid =>
        SceneIsValid &&
        BNpcBaseId > 0 &&
        ActorLimits.IsValidPosition(Position) &&
        ActorLimits.IsValidYaw(Yaw) &&
        ActorLimits.IsValidScale(Scale);
}

public sealed record FurnitureCreatePayload(
    RscSceneId? SceneId,
    string FurnitureIdentity,
    Vector3State Position,
    RotationState Rotation,
    SceneLocation? Location = null)
    : ObjectCreatePayload(SceneId, Location)
{
    public override RscObjectKind Kind => RscObjectKind.Furniture;

    /// <summary>Protocol 4: the initial stain (0 = Default). Placed furniture starts undyed.</summary>
    public byte Stain { get; init; }

    // Protocol 3: only the canonical furniture:indoor|outdoor:<item> form.
    public override bool IsValid =>
        SceneIsValid &&
        Protocol.FurnitureIdentity.IsCanonical(FurnitureIdentity) &&
        Position.IsFinite &&
        Rotation.IsValid;
}

public sealed record LightCreatePayload(
    RscSceneId? SceneId,
    string LightIdentity,
    Vector3State Position,
    Vector3State Scale,
    SceneLocation? Location = null)
    : ObjectCreatePayload(SceneId, Location)
{
    public override RscObjectKind Kind => RscObjectKind.Light;

    /// <summary>Protocol 5: normalized display RGB.</summary>
    public LightColor Color { get; init; } = LightColor.White;

    public float Intensity { get; init; } = LightLimits.DefaultIntensity;

    /// <summary>Reach in yalms; never derived from scale.</summary>
    public float Range { get; init; } = LightLimits.DefaultRange;

    // Protocol 5: only light:point, unit scale, bounded colour/intensity/range.
    public override bool IsValid =>
        SceneIsValid &&
        Protocol.LightIdentity.IsCanonical(LightIdentity) &&
        Position.IsFinite &&
        LightLimits.IsUnitScale(Scale) &&
        Color.IsValid &&
        LightLimits.IsValidIntensity(Intensity) &&
        LightLimits.IsValidRange(Range);
}

public readonly record struct CreateObjectRequest(
    Guid ClientRequestId,
    ObjectCreatePayload Payload)
{
    public bool IsValid =>
        ClientRequestId != Guid.Empty &&
        Payload is not null &&
        Payload.IsValid;
}

public readonly record struct ObjectTombstone(
    RscObjectId ObjectId,
    RscObjectKind Kind,
    ObjectRevision DeletionRevision)
{
    public bool IsValid =>
        ObjectId.IsValid &&
        Enum.IsDefined(Kind) &&
        DeletionRevision.IsValid;
}

public readonly record struct UpdateObjectRequest(
    Guid RequestId,
    RscObjectId ObjectId,
    ObjectRevision BaseRevision,
    ObjectPatch Patch)
{
    public bool IsValid =>
        RequestId != Guid.Empty &&
        ObjectId.IsValid &&
        BaseRevision.IsValid &&
        Patch is not null &&
        Patch.IsValid;
}

public readonly record struct DeleteObjectRequest(
    Guid RequestId,
    RscObjectId ObjectId,
    ObjectRevision BaseRevision)
{
    public bool IsValid =>
        RequestId != Guid.Empty &&
        ObjectId.IsValid &&
        BaseRevision.IsValid;
}

/// <param name="OwnerNames">
/// Protocol 6, server to client, display only: the display names of the
/// actors' owners that have one (each listed owner owns an actor in this
/// snapshot, once). Never decides ownership or editing rights.
/// </param>
public readonly record struct ObjectSnapshotMessage(
    ServerSequence Watermark,
    IReadOnlyList<AuthoritativeObjectState> Objects,
    IReadOnlyList<ObjectTombstone> Tombstones,
    IReadOnlyList<OwnerDisplayName>? OwnerNames = null)
{
    public bool IsValid => TryNormalize(out _);

    public bool TryNormalize(out ObjectSnapshotMessage normalized)
    {
        normalized = default;
        if (!Watermark.IsValid ||
            !ProtocolLimits.TryCopyBoundedPair(
                Objects,
                Tombstones,
                ProtocolLimits.MaximumObjectsPerSnapshot,
                out var objects,
                out var tombstones) ||
            !objects.All(state => state is not null && state.IsValid) ||
            !tombstones.All(tombstone => tombstone.IsValid) ||
            !HasUniqueIdentities(objects, tombstones))
        {
            return false;
        }

        OwnerDisplayName[]? names = null;
        if (OwnerNames is not null)
        {
            if (!ProtocolLimits.TryCopyBounded(OwnerNames, ProtocolLimits.MaximumObjectsPerSnapshot, out var copied))
                return false;
            var owners = objects.OfType<ActorState>().Select(actor => actor.OwnerId).ToHashSet();
            var listed = new HashSet<RscUserId>();
            if (!copied.All(name => name.IsValid && owners.Contains(name.UserId) && listed.Add(name.UserId)))
                return false;
            names = copied;
        }

        normalized = new(Watermark, objects, tombstones, names);
        return true;
    }

    private static bool HasUniqueIdentities(
        IReadOnlyList<AuthoritativeObjectState> objects,
        IReadOnlyList<ObjectTombstone> tombstones)
    {
        var identities = new HashSet<RscObjectId>();
        return objects.All(state => identities.Add(state.ObjectId)) &&
            tombstones.All(tombstone => identities.Add(tombstone.ObjectId));
    }
}

/// <param name="OwnerName">
/// Protocol 6, server to client, display only: the actor owner's display name,
/// if the account has one. Never decides ownership or editing rights.
/// </param>
public readonly record struct ObjectCreatedMessage(
    ServerSequence Sequence,
    AuthoritativeObjectState State,
    OwnerDisplayName? OwnerName = null)
{
    public bool IsValid => Sequence.IsValid && State is not null && State.IsValid &&
        OwnerNameRules.Matches(State, OwnerName);
}

/// <param name="OwnerName">
/// Protocol 6, server to client, display only: the actor owner's display name,
/// if the account has one. Never decides ownership or editing rights.
/// </param>
public readonly record struct ObjectUpdatedMessage(
    ServerSequence Sequence,
    AuthoritativeObjectState State,
    OwnerDisplayName? OwnerName = null)
{
    public bool IsValid => Sequence.IsValid && State is not null && State.IsValid &&
        OwnerNameRules.Matches(State, OwnerName);
}

internal static class OwnerNameRules
{
    /// <summary>A name may accompany only an actor, and only under its own owner's id.</summary>
    internal static bool Matches(AuthoritativeObjectState? state, OwnerDisplayName? name) =>
        name is not { } value || (value.IsValid && state is ActorState actor && actor.OwnerId == value.UserId);
}

public readonly record struct ObjectDeletedMessage(
    ServerSequence Sequence,
    ObjectTombstone Tombstone)
{
    public bool IsValid => Sequence.IsValid && Tombstone.IsValid;
}

public readonly record struct ObjectStreamAcknowledgement(
    ServerSequence AppliedThrough)
{
    public bool IsValid => AppliedThrough.IsValid;
}

public readonly record struct ObjectSnapshotRequest(Guid RequestId)
{
    public bool IsValid => RequestId != Guid.Empty;
}

public readonly record struct ObjectEventBatchRequest(
    Guid RequestId,
    ServerSequence After,
    int MaximumEvents)
{
    public bool IsValid =>
        RequestId != Guid.Empty &&
        After.IsValid &&
        MaximumEvents > 0 &&
        MaximumEvents <= ProtocolLimits.MaximumAuthoritativeObjectEvents;
}

public readonly record struct SaveSceneRequest(
    Guid RequestId,
    RscSceneId SceneId,
    ServerSequence ExpectedSequence)
{
    public bool IsValid =>
        RequestId != Guid.Empty &&
        SceneId.IsValid &&
        ExpectedSequence.IsValid;
}

public readonly record struct ReadSceneRequest(
    Guid RequestId,
    RscSceneId SceneId)
{
    public bool IsValid => RequestId != Guid.Empty && SceneId.IsValid;
}
