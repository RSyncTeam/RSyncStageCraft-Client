using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct LocalObjectOwnershipCleanupResult(
    int Released,
    int Failed,
    bool HasRemaining,
    bool Cancelled);

/// <summary>
/// Releases exact local-object receipts in bounded, fair batches. The external
/// release and ledger checkpoint run in one game-thread callback. Failed
/// receipts remain owned for an explicit later retry and are not hot-looped.
/// </summary>
public sealed class LocalObjectOwnershipCleanup
{
    private readonly LocalObjectOwnershipLedger ledger;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly ILocalObjectRuntimePort runtime;

    public LocalObjectOwnershipCleanup(
        LocalObjectOwnershipLedger ledger,
        IGameThreadDispatcher dispatcher,
        ILocalObjectRuntimePort runtime)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(runtime);

        this.ledger = ledger;
        this.dispatcher = dispatcher;
        this.runtime = runtime;
    }

    public Task<LocalObjectOwnershipCleanupResult> CleanupPassAsync(
        ReceiverSessionKey session,
        int maximumEntries,
        CancellationToken cancellationToken = default) =>
        CleanupPassAsync(session, maximumEntries, releases: null, cancellationToken);

    /// <summary>
    /// One bounded pass that releases only receipts whose kind
    /// <paramref name="releases"/> accepts (every kind when null). The batch
    /// rotates fairly, so a filtered pass still reaches every matching
    /// receipt; <see cref="LocalObjectOwnershipCleanupResult.HasRemaining"/>
    /// counts matching receipts only.
    /// </summary>
    public async Task<LocalObjectOwnershipCleanupResult> CleanupPassAsync(
        ReceiverSessionKey session,
        int maximumEntries,
        Func<RscObjectKind, bool>? releases,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsValid)
            return default;
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        bool Remaining() => releases is null
            ? ledger.HasSessionEntries(session)
            : ledger.HasSessionEntries(session, releases);
        var batch = ledger.GetSessionBatch(session, maximumEntries)
            .Where(reservation => releases is null || releases(reservation.DesiredIdentity.Kind))
            .ToArray();
        var released = 0;
        var failed = 0;

        foreach (var reservation in batch)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new(
                    released,
                    failed,
                    Remaining(),
                    Cancelled: true);
            }

            try
            {
                if (await ReleaseReservationAsync(reservation, cancellationToken)
                    .ConfigureAwait(false))
                {
                    released++;
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(
                    released,
                    failed,
                    Remaining(),
                    Cancelled: true);
            }
            catch
            {
                failed++;
            }
        }

        return new(
            released,
            failed,
            Remaining(),
            Cancelled: false);
    }

    public async Task<LocalObjectOwnershipCleanupResult> CleanupObjectAsync(
        ReceiverSessionKey session,
        RscObjectId objectId,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsValid || !objectId.IsValid)
            return default;
        if (!ledger.TryGetObject(session, objectId, out var reservation))
            return default;
        if (cancellationToken.IsCancellationRequested)
            return new(0, 0, HasRemaining: true, Cancelled: true);

        try
        {
            var released = await ReleaseReservationAsync(reservation, cancellationToken)
                .ConfigureAwait(false);
            return released
                ? new(1, 0, HasRemaining: false, Cancelled: false)
                : new(0, 1, HasRemaining: true, Cancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(0, 0, HasRemaining: true, Cancelled: true);
        }
        catch
        {
            return new(0, 1, HasRemaining: true, Cancelled: false);
        }
    }

    public int GetSessionEntryCount(ReceiverSessionKey session) =>
        ledger.GetSessionEntryCount(session);

    private async Task<bool> ReleaseReservationAsync(
        LocalObjectReservation reservation,
        CancellationToken cancellationToken)
    {
        if (reservation.Compensation is { } compensation)
        {
            if (!ledger.TryBeginCompensationRelease(
                    reservation.Token,
                    compensation,
                    out var claimed))
            {
                return false;
            }

            var compensationCheckpointed = false;
            try
            {
                compensationCheckpointed = await dispatcher.InvokeAsync(
                    () =>
                    {
                        var released = runtime.ReleaseOwned(claimed);
                        return released.Succeeded &&
                            ledger.TryCompleteCompensationRelease(reservation.Token, claimed);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!compensationCheckpointed)
                    return false;
            }
            finally
            {
                if (!compensationCheckpointed)
                    ledger.TryEndRelease(reservation.Token);
            }

            if (!ledger.TryGet(reservation.Token, out reservation))
                return false;
        }

        if (reservation.Handle is null)
            return ledger.TryRemoveEmpty(reservation.Token);

        if (!ledger.TryBeginRelease(
                reservation.Token,
                reservation.Handle.Value,
                out var primaryClaimed))
        {
            return false;
        }

        var checkpointed = false;
        try
        {
            checkpointed = await dispatcher.InvokeAsync(
                () =>
                {
                    var released = runtime.ReleaseOwned(primaryClaimed);
                    return released.Succeeded &&
                        ledger.TryCompleteRelease(reservation.Token, primaryClaimed);
                },
                cancellationToken).ConfigureAwait(false);

            if (!checkpointed)
                return false;

            return ledger.TryRemoveEmpty(reservation.Token);
        }
        finally
        {
            if (!checkpointed)
                ledger.TryEndRelease(reservation.Token);
        }
    }
}
