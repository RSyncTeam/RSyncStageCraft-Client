using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver.Ports;

/// <summary>
/// Opaque receipt for one exact object created by an RSC runtime adapter. The
/// adapter may map <see cref="OwnershipId"/> to a game object index, pointer,
/// or another version-specific identity without exposing that contract here.
/// </summary>
public readonly record struct LocalObjectInstanceHandle(
    Guid OwnershipId,
    RscObjectId ObjectId,
    RscObjectKind Kind,
    long SpawnGeneration)
{
    public bool IsValid =>
        OwnershipId != Guid.Empty &&
        ObjectId.IsValid &&
        Enum.IsDefined(Kind) &&
        SpawnGeneration > 0;
}

public readonly record struct LocalObjectSpawnRequest(
    LocalObjectApplyToken Token,
    LocalObjectSpawnIdentity Identity,
    long SpawnGeneration,
    AuthoritativeObjectState DesiredState)
{
    public bool IsValid =>
        Token.IsValid &&
        Identity.IsValid &&
        SpawnGeneration > 0 &&
        DesiredState is not null &&
        DesiredState.IsValid &&
        DesiredState.ObjectId == Token.ObjectId &&
        DesiredState.Kind == Identity.Kind &&
        LocalObjectSpawnIdentity.FromState(DesiredState) == Identity;
}

/// <summary>
/// Signature-neutral boundary for concrete overworld actor, furniture, and
/// light implementations. Calls are made only through the game-thread
/// dispatcher. Spawn and apply receive the settled authoritative state shapes:
/// furniture has no scale and lights have no rotation.
/// </summary>
public interface ILocalObjectRuntimePort
{
    /// <summary>
    /// Creates one owned instance and applies the complete desired state in
    /// the request before returning its receipt. A successful result must mean
    /// the concrete object was observed as created and fully applied; accepting
    /// or parsing a host definition alone is not success. If the host may have
    /// mutated before returning a failure, the result must carry the exact
    /// preallocated receipt so the reconciler can retain and compensate it.
    /// A host whose objects finish loading later returns
    /// <see cref="ApplyPortStatus.AwaitingPayload"/> with the receipt: the
    /// instance is owned but not yet applied, and the reconciler then calls
    /// <see cref="ApplyOwned"/> until it succeeds, within a bounded time.
    /// </summary>
    ApplyPortResult<LocalObjectInstanceHandle> SpawnOwned(LocalObjectSpawnRequest request);

    /// <summary>
    /// Applies the complete desired state to the exact owned receipt. Success
    /// requires host acknowledgement or observation of the concrete applied
    /// object, not merely acceptance of an update request. Returns
    /// <see cref="ApplyPortStatus.AwaitingPayload"/> while the instance is
    /// still loading. While the reconciler waits for a load, any other failure
    /// (or the load time running out) means the instance is unusable and the
    /// reconciler releases its exact receipt.
    /// </summary>
    ApplyPortResult ApplyOwned(
        LocalObjectInstanceHandle handle,
        AuthoritativeObjectState desiredState);

    /// <summary>
    /// Releases only the exact receipt supplied by RSC. Implementations must
    /// be idempotent because a process interruption may occur after external
    /// deletion but before its ledger checkpoint becomes observable.
    /// </summary>
    ApplyPortResult ReleaseOwned(LocalObjectInstanceHandle handle);
}

/// <summary>
/// Decides whether a furniture/light object belongs where the player is now.
/// An object not visible here is desired but has no local instance; nothing
/// is spawned for it. Implementations must fail closed: an unknown local
/// location or an object without a scene location is never visible.
/// </summary>
public interface ILocalObjectVisibility
{
    bool IsVisibleHere(AuthoritativeObjectState state);
}

/// <summary>
/// Assembly-owned reconstruction diagnostic for a concrete local-object host.
/// It exposes no release authority: a nonzero count blocks successor activation
/// until the adapter's exact retained receipts have been drained.
/// </summary>
internal interface ILocalObjectRuntimeOwnershipDiagnostics
{
    int PendingOwnershipCount { get; }
}

/// <summary>
/// Desired-state fence used by the local-object reconciler. A successful
/// TryExecuteCurrent call must keep the token current for the whole callback.
/// </summary>
internal interface ILocalObjectDesiredFence
{
    bool IsCurrent(LocalObjectApplyToken token);

    bool TryExecuteCurrent<T>(
        LocalObjectApplyToken token,
        Func<T> operation,
        out T result);
}
