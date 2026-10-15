using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

public sealed record DesiredLocalObjectState(
    ReceiverSessionKey Session,
    AuthoritativeObjectState State,
    string Fingerprint,
    ApplyGeneration ApplyGeneration)
{
    public RscObjectId ObjectId => State.ObjectId;
    public ObjectRevision AuthoritativeRevision => State.Revision;
    public LocalObjectApplyToken Token => new(Session, ObjectId, ApplyGeneration);

    public bool IsValid =>
        Session.IsValid &&
        State is ActorState or FurnitureState or LightState &&
        State.IsValid &&
        !string.IsNullOrWhiteSpace(Fingerprint) &&
        Fingerprint.Length <= ClientLimits.MaximumDesiredStateFingerprintLength &&
        ApplyGeneration.IsValid;
}

public sealed record DesiredLocalObjectRemoval(
    ReceiverSessionKey Session,
    ObjectTombstone Tombstone,
    ApplyGeneration ApplyGeneration,
    bool Completed)
{
    public RscObjectId ObjectId => Tombstone.ObjectId;
    public ObjectRevision AuthoritativeRevision => Tombstone.DeletionRevision;
    public LocalObjectApplyToken Token => new(Session, ObjectId, ApplyGeneration);

    public bool IsValid =>
        Session.IsValid &&
        Tombstone.IsValid &&
        Tombstone.Kind is RscObjectKind.Actor or RscObjectKind.Furniture or RscObjectKind.Light &&
        ApplyGeneration.IsValid;
}

public readonly record struct DesiredLocalObjectSnapshotState(
    AuthoritativeObjectState State,
    string Fingerprint);

public readonly record struct LocalObjectApplyWorkItem
{
    public LocalObjectApplyWorkItem(DesiredLocalObjectState desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        Desired = desired;
        Removal = null;
    }

    public LocalObjectApplyWorkItem(DesiredLocalObjectRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        Desired = null;
        Removal = removal;
    }

    public DesiredLocalObjectState? Desired { get; }
    public DesiredLocalObjectRemoval? Removal { get; }
    public LocalObjectApplyToken Token => Desired?.Token ?? Removal?.Token ?? default;
    public bool IsRemoval => Removal is not null;
}

public enum DesiredLocalObjectAdvanceStatus
{
    Advanced,
    RevisionAdvanced,
    Unchanged,
    StaleRevision,
    RevisionConflict,
    ObjectKindConflict,
    Invalid,
    InactiveSession,
    CapacityExceeded,
    GenerationExhausted,
}

public readonly record struct DesiredLocalObjectAdvanceResult(
    DesiredLocalObjectAdvanceStatus Status,
    DesiredLocalObjectState? Current)
{
    public bool Accepted => Status is
        DesiredLocalObjectAdvanceStatus.Advanced or
        DesiredLocalObjectAdvanceStatus.RevisionAdvanced or
        DesiredLocalObjectAdvanceStatus.Unchanged;

    public bool RequiresApply => Status == DesiredLocalObjectAdvanceStatus.Advanced;
}

public enum DesiredLocalObjectRemovalStatus
{
    Advanced,
    Unchanged,
    StaleRevision,
    ObjectKindConflict,
    Invalid,
    InactiveSession,
    CapacityExceeded,
    GenerationExhausted,
}

public readonly record struct DesiredLocalObjectRemovalResult(
    DesiredLocalObjectRemovalStatus Status,
    DesiredLocalObjectRemoval? Current)
{
    public bool Accepted => Status is
        DesiredLocalObjectRemovalStatus.Advanced or
        DesiredLocalObjectRemovalStatus.Unchanged;
}

public enum DesiredLocalObjectSnapshotStatus
{
    Applied,
    Invalid,
    InactiveSession,
    CapacityExceeded,
    StaleRevision,
    RevisionConflict,
    ObjectKindConflict,
    IncompleteSnapshot,
    GenerationExhausted,
}

public readonly record struct DesiredLocalObjectSnapshotResult(
    DesiredLocalObjectSnapshotStatus Status,
    IReadOnlyList<LocalObjectApplyWorkItem> Work)
{
    public bool Applied => Status == DesiredLocalObjectSnapshotStatus.Applied;
}
