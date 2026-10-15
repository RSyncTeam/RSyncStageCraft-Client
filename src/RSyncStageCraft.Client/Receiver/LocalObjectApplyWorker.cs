using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct LocalObjectApplyWorkResult(
    bool Processed,
    LocalObjectReconcileResult Outcome,
    LocalObjectApplyRetryScheduleResult Retry);

/// <summary>
/// Runs one coalesced furniture/light apply or removal attempt. Retry state is
/// bounded and promoted only by the caller's shared pump; no per-object task or
/// timer is created.
/// </summary>
public sealed class LocalObjectApplyWorker
{
    private readonly DesiredLocalObjectStore desiredStore;
    private readonly LocalObjectApplyCoordinator coordinator;
    private readonly LocalObjectReconciler reconciler;
    private readonly LocalObjectRemovalExecutor removalExecutor;
    private readonly LocalObjectApplyRetryQueue retries;
    private readonly IReceiverReadinessPort readiness;
    private readonly IReceiverReadinessAdmissionFence? readinessAdmissionFence;

    public LocalObjectApplyWorker(
        DesiredLocalObjectStore desiredStore,
        LocalObjectApplyCoordinator coordinator,
        LocalObjectReconciler reconciler,
        LocalObjectRemovalExecutor removalExecutor,
        LocalObjectApplyRetryQueue retries,
        IReceiverReadinessPort readiness)
    {
        ArgumentNullException.ThrowIfNull(desiredStore);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(removalExecutor);
        ArgumentNullException.ThrowIfNull(retries);
        ArgumentNullException.ThrowIfNull(readiness);

        this.desiredStore = desiredStore;
        this.coordinator = coordinator;
        this.reconciler = reconciler;
        this.removalExecutor = removalExecutor;
        this.retries = retries;
        this.readiness = readiness;
        readinessAdmissionFence = readiness as IReceiverReadinessAdmissionFence;
    }

    public int PromoteDueRetries(DateTimeOffset now, int maximumEntries)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        int Promote()
        {
            var promoted = 0;
            foreach (var retry in retries.GetDueBatch(now, maximumEntries))
            {
                var current = desiredStore.TryExecuteCurrent(
                    retry.Token,
                    () => coordinator.TryAdmit(
                        retry.Token.Session,
                        retry.Token.ObjectId) &&
                        retries.TryMarkPromoted(retry.Token),
                    out var admitted);
                if (!current)
                {
                    retries.TryRemove(retry.Token);
                    continue;
                }

                if (admitted)
                    promoted++;
            }

            return promoted;
        }

        return TryExecuteIfReady(Promote, out var admittedPromotions)
            ? admittedPromotions
            : 0;
    }

    public async Task<LocalObjectApplyWorkResult> TryProcessNextAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        var work = default(LocalObjectApplyWorkItem);
        if (!TryExecuteIfReady(
                () => coordinator.TryBeginNext(out work),
                out var began) ||
            !began)
        {
            return default;
        }

        retries.TryRemoveSuperseded(work.Token);
        LocalObjectReconcileResult outcome;
        try
        {
            outcome = work.Removal is { } removal
                ? await removalExecutor.RemoveAsync(removal, cancellationToken)
                    .ConfigureAwait(false)
                : await reconciler.ReconcileAsync(
                    work.Token,
                    work.Desired!.State,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new(LocalObjectReconcileStatus.Cancelled);
        }
        catch (Exception ex)
        {
            outcome = new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: ReceiverFailureDiagnostic.From(ex));
        }

        if (!coordinator.Complete(
            work.Token,
            retryCurrent: outcome.Status == LocalObjectReconcileStatus.Cancelled))
        {
            return new(Processed: true, outcome, default);
        }

        if (outcome.Retryable)
        {
            var current = desiredStore.TryExecuteCurrent(
                work.Token,
                () => retries.TrySchedule(work.Token, now),
                out var retry);
            if (!current)
                retries.TryRemove(work.Token);

            return new(Processed: true, outcome, current ? retry : default);
        }

        retries.TryRemove(work.Token);
        return new(Processed: true, outcome, default);
    }

    private bool TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (readinessAdmissionFence is not null)
            return readinessAdmissionFence.TryExecuteIfReady(operation, out result);

        try
        {
            if (!readiness.IsReady)
            {
                result = default!;
                return false;
            }
        }
        catch
        {
            result = default!;
            return false;
        }

        result = operation();
        return true;
    }
}
