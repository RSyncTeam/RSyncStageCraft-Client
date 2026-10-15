namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Reconciles one exact furniture/light tombstone by releasing the ledger-owned
/// receipt and fencing completion on the current removal generation.
/// </summary>
public sealed class LocalObjectRemovalExecutor
{
    private readonly DesiredLocalObjectStore desiredStore;
    private readonly LocalObjectOwnershipCleanup ownershipCleanup;

    public LocalObjectRemovalExecutor(
        DesiredLocalObjectStore desiredStore,
        LocalObjectOwnershipCleanup ownershipCleanup)
    {
        ArgumentNullException.ThrowIfNull(desiredStore);
        ArgumentNullException.ThrowIfNull(ownershipCleanup);
        this.desiredStore = desiredStore;
        this.ownershipCleanup = ownershipCleanup;
    }

    public async Task<LocalObjectReconcileResult> RemoveAsync(
        DesiredLocalObjectRemoval removal,
        CancellationToken cancellationToken = default)
    {
        if (removal is null || !removal.IsValid)
        {
            return new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: "Desired local object removal is invalid.");
        }
        if (cancellationToken.IsCancellationRequested)
            return new(LocalObjectReconcileStatus.Cancelled);
        if (!desiredStore.TryGetRemoval(removal.Session, removal.ObjectId, out var current) ||
            current.Token != removal.Token)
        {
            return new(LocalObjectReconcileStatus.Stale);
        }
        if (current.Completed)
            return new(LocalObjectReconcileStatus.Applied);

        var cleanup = await ownershipCleanup.CleanupObjectAsync(
            removal.Session,
            removal.ObjectId,
            cancellationToken).ConfigureAwait(false);
        if (cleanup.Cancelled)
            return new(LocalObjectReconcileStatus.Cancelled);
        if (!desiredStore.TryGetRemoval(removal.Session, removal.ObjectId, out current) ||
            current.Token != removal.Token)
        {
            return new(LocalObjectReconcileStatus.Stale);
        }
        if (cleanup.Failed > 0 || cleanup.HasRemaining)
        {
            return new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Owned local object could not be fully released.");
        }

        return desiredStore.TryMarkRemovalCompleted(removal.Token)
            ? new(LocalObjectReconcileStatus.Applied)
            : new(
                LocalObjectReconcileStatus.Stale,
                Error: "Removal generation changed before finalization.");
    }
}
