namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Performs receiver cleanup in authority, work, state order. Each pass is
/// bounded; continuation work is exact-session scoped so reconnect state is
/// never removed by an older cleanup.
/// </summary>
public sealed class ReceiverSessionCleanup
{
    private readonly object continuationGate = new();
    private readonly ReceiverApplyAuthority authority;
    private readonly ReceiverActorEventIngress? eventIngress;
    private readonly ReceiverActorSnapshotIngress? snapshotIngress;
    private readonly LocalObjectApplyCoordinator? localObjectCoordinator;
    private readonly LocalObjectApplyRetryQueue? localObjectRetries;
    private readonly DesiredLocalObjectStore? localObjectStore;
    private readonly int maximumEntriesPerPass;
    private readonly HashSet<ReceiverSessionKey> scheduledSessions = new();
    private Task cleanupCompletion = Task.CompletedTask;

    public ReceiverSessionCleanup(
        ReceiverApplyAuthority authority,
        ReceiverActorEventIngress? eventIngress,
        ReceiverActorSnapshotIngress? snapshotIngress,
        int maximumEntriesPerPass = 32,
        LocalObjectApplyCoordinator? localObjectCoordinator = null,
        LocalObjectApplyRetryQueue? localObjectRetries = null,
        DesiredLocalObjectStore? localObjectStore = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (maximumEntriesPerPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntriesPerPass));

        this.authority = authority;
        this.eventIngress = eventIngress;
        this.snapshotIngress = snapshotIngress;
        this.localObjectCoordinator = localObjectCoordinator;
        this.localObjectRetries = localObjectRetries;
        this.localObjectStore = localObjectStore;
        this.maximumEntriesPerPass = maximumEntriesPerPass;
    }

    public Task CleanupCompletion
    {
        get
        {
            lock (continuationGate)
                return cleanupCompletion;
        }
    }

    public void Cleanup(ReceiverSessionKey session)
    {
        if (!session.IsValid)
            return;

        // Invalidation is first. It waits for any short operation already
        // executing through the final apply-token fence.
        authority.Invalidate(session);
        eventIngress?.ClearSession(session);
        snapshotIngress?.ClearSession(session);

        RunBoundedPass(session);
        if (HasRemaining(session) || CleanupContinuationNeedsRecovery())
            ScheduleContinuation(session);
    }

    private bool CleanupContinuationNeedsRecovery()
    {
        lock (continuationGate)
            return cleanupCompletion.IsFaulted || cleanupCompletion.IsCanceled;
    }

    private void ScheduleContinuation(ReceiverSessionKey session)
    {
        lock (continuationGate)
        {
            if (!scheduledSessions.Add(session))
                return;

            var previousCleanup = cleanupCompletion;
            if (previousCleanup.IsCompleted)
            {
                // Preserve only genuinely concurrent ordering. Completed
                // fault/cancellation retry cycles must not form an unbounded
                // task chain.
                _ = previousCleanup.Exception;
                previousCleanup = Task.CompletedTask;
            }

            cleanupCompletion = ContinueCleanupAsync(previousCleanup, session);
        }
    }

    private async Task ContinueCleanupAsync(
        Task previousCleanup,
        ReceiverSessionKey session)
    {
        try
        {
            try
            {
                await previousCleanup.ConfigureAwait(false);
            }
            catch
            {
                // The caller that owned the previous cleanup still observes
                // its failure. An explicit retry must not inherit that fault
                // forever after the failing adapter or dependency recovers.
            }

            while (HasRemaining(session))
            {
                await Task.Delay(1).ConfigureAwait(false);
                var processed = RunBoundedPass(session);
                if (processed == 0 && HasRemaining(session))
                {
                    throw new InvalidOperationException(
                        "Receiver session cleanup made no bounded progress.");
                }
            }
        }
        finally
        {
            lock (continuationGate)
                scheduledSessions.Remove(session);
        }
    }

    private int RunBoundedPass(ReceiverSessionKey session)
    {
        var remainingCapacity = maximumEntriesPerPass;
        if (localObjectCoordinator is not null)
        {
            var cancelledLocal = localObjectCoordinator.CancelSession(
                session,
                remainingCapacity);
            ConsumeBudget(ref remainingCapacity, cancelledLocal);
        }

        if (remainingCapacity > 0 && localObjectRetries is not null)
        {
            var cancelledLocalRetries = localObjectRetries.CancelSession(
                session,
                remainingCapacity);
            ConsumeBudget(ref remainingCapacity, cancelledLocalRetries);
        }

        if (remainingCapacity > 0 && localObjectStore is not null)
        {
            var removedLocalObjects = localObjectStore.RemoveSession(
                session,
                remainingCapacity);
            ConsumeBudget(ref remainingCapacity, removedLocalObjects);
        }

        return maximumEntriesPerPass - remainingCapacity;
    }

    private static void ConsumeBudget(ref int remainingCapacity, int processed)
    {
        if (processed < 0 || processed > remainingCapacity)
        {
            throw new InvalidOperationException(
                "Receiver session cleanup reported an invalid bounded result.");
        }

        remainingCapacity -= processed;
    }

    private bool HasRemaining(ReceiverSessionKey session) =>
        (localObjectCoordinator?.HasSessionWork(session) ?? false) ||
        (localObjectRetries?.HasSessionEntries(session) ?? false) ||
        (localObjectStore?.HasSessionEntries(session) ?? false);
}
