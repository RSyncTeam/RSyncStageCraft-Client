using RSyncStageCraft.Core.Scenes;

namespace RSyncStageCraft.Core.Protocol;

public enum ObjectMutationCompletionKind
{
    Create, Update, Delete,
}

/// <summary>
/// Candidate command success, separate from ordered broadcast events. A create
/// replay returns the current object, not the original create event. Watermark
/// is an observation only; it never advances an applied acknowledgement cursor.
/// </summary>
public readonly record struct ObjectMutationCompletion(
    Guid RequestId, ObjectMutationCompletionKind Kind, bool Idempotent,
    ServerSequence Watermark, AuthoritativeObjectState? State, ObjectTombstone? Tombstone)
{
    public bool IsValid => RequestId != Guid.Empty && Watermark.Value > 0 && Kind switch
    {
        ObjectMutationCompletionKind.Create => ValidState && Tombstone is null &&
            (Idempotent || State!.Revision.Value == 1),
        ObjectMutationCompletionKind.Update => !Idempotent && ValidState &&
            Tombstone is null && State!.Revision.Value > 1,
        ObjectMutationCompletionKind.Delete => !Idempotent && State is null &&
            Tombstone is { IsValid: true } deleted && deleted.DeletionRevision.Value > 1,
        _ => false,
    };

    private bool ValidState => State is ActorState or FurnitureState or LightState && State.IsValid;
}

/// <summary>
/// Exact candidate snapshot request result. Contains a bounded owned copy,
/// never authority to apply it or acknowledge its watermark.
/// </summary>
public readonly record struct ObjectSnapshotCompletion(Guid RequestId, ObjectSnapshotMessage Snapshot)
{
    public bool IsValid => TryNormalize(out _);

    public bool TryNormalize(out ObjectSnapshotCompletion normalized)
    {
        normalized = default;
        if (RequestId == Guid.Empty || !Snapshot.TryNormalize(out var snapshot) ||
            !snapshot.Objects.All(state => state is ActorState or FurnitureState or LightState)) return false;
        normalized = new(RequestId, snapshot);
        return true;
    }
}

public enum ObjectEventBatchCompletionStatus
{
    Advanced, UpToDate, SnapshotRequired, InvalidCursor,
}

/// <summary>
/// Candidate version-2 marker after one contiguous catch-up batch. The outbound
/// owner must send those events before this marker without fan-out interleaving;
/// the receiver must verify the observed event count/order before completion.
/// This marker alone neither applies state nor advances acknowledgements.
/// </summary>
public readonly record struct ObjectEventBatchCompletion(
    Guid RequestId,
    ObjectEventBatchCompletionStatus Status,
    ServerSequence RequestedAfter,
    ServerSequence CurrentWatermark,
    ServerSequence DeliveredThrough,
    int EventCount,
    bool HasMore)
{
    public bool IsValid =>
        RequestId != Guid.Empty && Enum.IsDefined(Status) &&
        RequestedAfter.IsValid && CurrentWatermark.IsValid && DeliveredThrough.IsValid &&
        EventCount >= 0 && EventCount <= ProtocolLimits.MaximumAuthoritativeObjectEvents &&
        Status switch
        {
            ObjectEventBatchCompletionStatus.Advanced =>
                EventCount > 0 && DeliveredThrough.Value > RequestedAfter.Value &&
                DeliveredThrough.Value - RequestedAfter.Value == EventCount &&
                DeliveredThrough.Value <= CurrentWatermark.Value &&
                HasMore == (DeliveredThrough.Value < CurrentWatermark.Value),
            ObjectEventBatchCompletionStatus.UpToDate =>
                Empty && RequestedAfter == CurrentWatermark,
            ObjectEventBatchCompletionStatus.SnapshotRequired =>
                Empty && RequestedAfter.Value < CurrentWatermark.Value,
            ObjectEventBatchCompletionStatus.InvalidCursor =>
                Empty && RequestedAfter.Value > CurrentWatermark.Value,
            _ => false,
        };

    private bool Empty => EventCount == 0 && !HasMore && DeliveredThrough == RequestedAfter;
}

public enum SceneSaveCompletionStatus
{
    Saved, Unchanged, Busy, TooLarge, Conflict, InvalidDocument, Failed, Unavailable,
}

/// <summary>Sanitized storage outcome, distinct from authorization acceptance.</summary>
public readonly record struct SceneSaveCompletion(
    Guid RequestId, RscSceneId SceneId, SceneSaveCompletionStatus Status)
{
    public bool IsValid => RequestId != Guid.Empty && SceneId.IsValid && Enum.IsDefined(Status);
}

public enum SceneReadCompletionStatus
{
    Loaded, NotFound, Busy, TooLarge, InvalidDocument, Failed, Unavailable,
}

/// <summary>
/// Candidate version-2 scene read result. Document is server-owned persisted
/// content, not an instruction to import, replace desired state, or apply IPC.
/// Storage error text and local paths have no field in this wire contract.
/// </summary>
public readonly record struct SceneReadCompletion(
    Guid RequestId, RscSceneId SceneId, SceneReadCompletionStatus Status, SceneDocument? Document)
{
    public bool IsValid => TryNormalize(out _);

    public bool TryNormalize(out SceneReadCompletion normalized)
    {
        normalized = default;
        if (RequestId == Guid.Empty || !SceneId.IsValid || !Enum.IsDefined(Status)) return false;
        if (Status != SceneReadCompletionStatus.Loaded)
        {
            if (Document is not null) return false;
            normalized = this;
            return true;
        }
        if (Document is null || Document.SceneId != SceneId ||
            !Document.TryCanonicalize(out var document)) return false;
        normalized = this with { Document = document };
        return true;
    }
}
