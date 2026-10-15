using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ReceiverActorRuntimeLimits(
    int MaximumActors,
    int MaximumTrackedActors,
    int MaximumRetryActors,
    int MaximumConcurrentWorkers,
    int MaximumSnapshotAdmissionsPerPump,
    int MaximumRetryPromotionsPerPump,
    int MaximumCleanupEntriesPerPass)
{
    public const int MaximumConcurrentWorkersHardLimit = 16;

    public static ReceiverActorRuntimeLimits Default => new(
        MaximumActors: ProtocolLimits.MaximumObjectsPerSnapshot,
        MaximumTrackedActors: 128,
        MaximumRetryActors: 128,
        MaximumConcurrentWorkers: 2,
        MaximumSnapshotAdmissionsPerPump: 32,
        MaximumRetryPromotionsPerPump: 32,
        MaximumCleanupEntriesPerPass: 32);

    public bool IsValid =>
        MaximumActors > 0 &&
        MaximumActors <= ProtocolLimits.MaximumObjectsPerSnapshot &&
        MaximumTrackedActors > 0 &&
        MaximumTrackedActors <= MaximumActors &&
        MaximumRetryActors > 0 &&
        MaximumRetryActors <= MaximumActors &&
        MaximumConcurrentWorkers > 0 &&
        MaximumConcurrentWorkers <= MaximumConcurrentWorkersHardLimit &&
        MaximumConcurrentWorkers <= MaximumTrackedActors &&
        MaximumSnapshotAdmissionsPerPump > 0 &&
        MaximumSnapshotAdmissionsPerPump <= MaximumActors &&
        MaximumRetryPromotionsPerPump > 0 &&
        MaximumRetryPromotionsPerPump <= MaximumRetryActors &&
        MaximumCleanupEntriesPerPass > 0 &&
        MaximumCleanupEntriesPerPass <= MaximumActors;
}

public enum ReceiverActorPumpStatus
{
    Ran,
    InactiveSession,
    DependenciesUnavailable,
    Busy,
    Cancelled,
}

public readonly record struct ReceiverActorPumpResult(
    ReceiverActorPumpStatus Status,
    ReceiverActorSnapshotResult? Snapshot)
{
    public int PromotedLocalRetries { get; init; }
    public IReadOnlyList<LocalObjectApplyWorkResult>? LocalWork { get; init; }
    public int LocalProcessed => LocalWork?.Count(result => result.Processed) ?? 0;
}

public readonly record struct ReceiverActorRuntimeSnapshot(
    ReceiverSessionKey? Session,
    int PendingSnapshotAdmissions)
{
    public int DesiredLocalObjects { get; init; }
    public int TrackedLocalWork { get; init; }
    public int DelayedLocalRetries { get; init; }
    public int OwnedLocalObjects { get; init; }
    public int PendingCancellationCallbacks { get; init; }
    public int CancellationCallbackFailures { get; init; }
    public int PendingLocalObjectOwnerships { get; init; }
}

/// <summary>
/// Signature-neutral receiver composition root. It owns one active receiver
/// session, one fixed-size worker pool, and all bounded admission/retry/cleanup
/// state. The host drives <see cref="PumpAsync"/> from one shared tick; this
/// service creates no background loop and no task or timer per actor.
/// </summary>
public sealed class ReceiverActorRuntime : IReceiverActorSessionRuntime
{
    public static readonly TimeSpan DefaultCancellationCallbackWait =
        TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaximumCancellationCallbackWait =
        TimeSpan.FromSeconds(1);

    private readonly object sessionGate = new();
    private readonly SemaphoreSlim pumpGate = new(1, 1);
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private readonly ReceiverActorRuntimeLimits limits;
    private readonly IReceiverReadinessPort readiness;
    private readonly IReceiverReadinessAdmissionFence? readinessAdmissionFence;
    private readonly DomainGatedReadiness localObjectReadiness;
    private readonly LocalObjectOwnershipCleanup? localObjectOwnershipCleanup;
    private readonly LocalObjectApplyWorker? localObjectWorker;
    private readonly ReceiverApplyCleanup cleanup;
    private readonly ILocalObjectRuntimeOwnershipDiagnostics?
        localObjectOwnershipDiagnostics;
    private readonly TimeSpan maximumCancellationCallbackWait;
    private ReceiverSessionKey? currentSession;
    private ReceiverSessionKey? cleanupSession;
    private CancellationTokenSource? sessionCancellation;
    private SessionCancellationState? cleanupCancellation;
    private int cancellationCallbackFailures;

    // Deterministic concurrency seam for proving that disconnect owns the
    // cleanup serializer before its retained cancellation task is published.
    // It is assembly-only and does not participate in host composition.
    internal Action? BeforeSessionCancellationStart { get; set; }

    // Deterministic concurrency seam for proving that successor activation
    // cannot overtake the final release of the prior cleanup serializer.
    internal Action? BeforeCleanupGateRelease { get; set; }

    public ReceiverActorRuntime(
        IGameThreadDispatcher dispatcher,
        IReceiverReadinessPort readiness,
        ReceiverActorRuntimeLimits? limits = null,
        ILocalObjectRuntimePort? localObjectRuntime = null,
        LocalObjectApplyRetryPolicy? localObjectRetryPolicy = null,
        ILocalObjectStateFingerprint? localObjectFingerprint = null,
        TimeSpan? maximumCancellationCallbackWait = null,
        IReceiverReadinessPort? localObjectApplyReadiness = null,
        ILocalObjectVisibility? localObjectVisibility = null,
        TimeSpan? localObjectLoadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(readiness);

        this.limits = limits ?? ReceiverActorRuntimeLimits.Default;
        if (!this.limits.IsValid)
            throw new ArgumentException("Receiver actor runtime limits are invalid.", nameof(limits));

        this.maximumCancellationCallbackWait =
            maximumCancellationCallbackWait ?? DefaultCancellationCallbackWait;
        if (this.maximumCancellationCallbackWait <= TimeSpan.Zero ||
            this.maximumCancellationCallbackWait > MaximumCancellationCallbackWait)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCancellationCallbackWait));
        }

        this.readiness = readiness;
        readinessAdmissionFence = readiness as IReceiverReadinessAdmissionFence;
        // Intake (activation, snapshots, events) follows connection readiness.
        // Work that touches the game follows its apply domain, closed while
        // that domain is being reset (host loss, or a location change).
        localObjectReadiness = new DomainGatedReadiness(
            localObjectApplyReadiness ?? readiness,
            LocalObjectDomain);
        localObjectOwnershipDiagnostics =
            localObjectRuntime as ILocalObjectRuntimeOwnershipDiagnostics;
        Authority = new ReceiverApplyAuthority();
        ReceiverLocalObjectIngress? localObjectIngress = null;
        if (localObjectRuntime is not null)
        {
            LocalObjectStore = new DesiredLocalObjectStore(
                Authority,
                this.limits.MaximumActors);
            LocalObjectCoordinator = new LocalObjectApplyCoordinator(
                LocalObjectStore,
                this.limits.MaximumTrackedActors);
            LocalObjectRetries = new LocalObjectApplyRetryQueue(
                this.limits.MaximumRetryActors,
                localObjectRetryPolicy);
            LocalObjectLedger = new LocalObjectOwnershipLedger(
                this.limits.MaximumActors);
            localObjectOwnershipCleanup = new LocalObjectOwnershipCleanup(
                LocalObjectLedger,
                dispatcher,
                localObjectRuntime);
            var localObjectReconciler = new LocalObjectReconciler(
                LocalObjectStore,
                LocalObjectLedger,
                dispatcher,
                localObjectRuntime,
                localObjectReadiness,
                localObjectVisibility,
                localObjectLoadTimeout);
            var localObjectRemoval = new LocalObjectRemovalExecutor(
                LocalObjectStore,
                localObjectOwnershipCleanup);
            localObjectWorker = new LocalObjectApplyWorker(
                LocalObjectStore,
                LocalObjectCoordinator,
                localObjectReconciler,
                localObjectRemoval,
                LocalObjectRetries,
                localObjectReadiness);
            localObjectIngress = new ReceiverLocalObjectIngress(
                LocalObjectStore,
                LocalObjectCoordinator,
                localObjectFingerprint);
        }

        Events = new ReceiverActorEventIngress(
            Authority,
            localObjectIngress,
            readiness);
        Snapshots = new ReceiverActorSnapshotIngress(
            Authority,
            Events,
            LocalObjectStore,
            LocalObjectCoordinator,
            localObjectFingerprint,
            readiness);

        var sessionCleanup = new ReceiverSessionCleanup(
            Authority,
            Events,
            Snapshots,
            this.limits.MaximumCleanupEntriesPerPass,
            LocalObjectCoordinator,
            LocalObjectRetries,
            LocalObjectStore);
        cleanup = new ReceiverApplyCleanup(
            sessionCleanup,
            this.limits.MaximumCleanupEntriesPerPass,
            localObjectOwnershipCleanup);
    }

    internal ReceiverApplyAuthority Authority { get; }
    public TimeSpan MaximumSessionCancellationCallbackWait =>
        maximumCancellationCallbackWait;
    internal ReceiverActorEventIngress Events { get; }
    internal ReceiverActorSnapshotIngress Snapshots { get; }
    internal DesiredLocalObjectStore? LocalObjectStore { get; }
    internal LocalObjectApplyCoordinator? LocalObjectCoordinator { get; }
    internal LocalObjectApplyRetryQueue? LocalObjectRetries { get; }
    internal LocalObjectOwnershipLedger? LocalObjectLedger { get; }

    public ReceiverActorRuntimeSnapshot State
    {
        get
        {
            // These counters can be adapter-backed. Never hold lifecycle
            // serialization while acquiring an adapter monitor: a host callback
            // may synchronously reenter runtime state or invalidation. Snapshot
            // diagnostics are non-authoritative, so an unavailable or invalid
            // count is represented conservatively as retained ownership.
            var pendingLocalObjectOwnerships = ReadPendingOwnershipCount(
                localObjectOwnershipDiagnostics is null
                    ? null
                    : () => localObjectOwnershipDiagnostics.PendingOwnershipCount);

            lock (sessionGate)
            {
                return new(
                    currentSession,
                    Snapshots.PendingCount)
                {
                    DesiredLocalObjects = LocalObjectStore?.Count ?? 0,
                    TrackedLocalWork = LocalObjectCoordinator?.TrackedCount ?? 0,
                    DelayedLocalRetries = LocalObjectRetries?.Count ?? 0,
                    OwnedLocalObjects = LocalObjectLedger?.OwnedCount ?? 0,
                    PendingCancellationCallbacks =
                        cleanupCancellation is { CompletionObserved: false } ? 1 : 0,
                    CancellationCallbackFailures = cancellationCallbackFailures,
                    PendingLocalObjectOwnerships = pendingLocalObjectOwnerships,
                };
            }
        }
    }

    private static int ReadPendingOwnershipCount(Func<int>? reader)
    {
        if (reader is null)
            return 0;

        try
        {
            var count = reader();
            return count < 0 ? 1 : count;
        }
        catch
        {
            return 1;
        }
    }

    bool IReceiverActorSessionRuntime.IsActive(ReceiverSessionKey session) =>
        Authority.IsCurrent(session);

    public bool TryActivate(ReceiverSessionKey session)
    {
        if (!session.IsValid || !IsReadyForActivation())
            return false;

        lock (sessionGate)
        {
            if (currentSession is not null)
                return currentSession == session;
        }

        // A cancelled adapter may outlive otherwise-converged cleanup while
        // its prior-session pump still owns the runtime-wide serializer. Claim
        // it first so that pump cannot create host compensation after the
        // count below has been sampled.
        if (!pumpGate.Wait(0))
            return false;

        try
        {
            lock (sessionGate)
            {
                if (currentSession is not null)
                    return currentSession == session;
            }

            // The ownership count is supplied by the local-object adapter.
            // Sample it outside the session lock so slow or reentrant host code
            // cannot retain lifecycle serialization. The pump claim makes a
            // zero sample stable against late prior-session worker ownership.
            int pendingLocalObjectOwnerships;
            try
            {
                pendingLocalObjectOwnerships =
                    localObjectOwnershipDiagnostics?.PendingOwnershipCount ?? 0;
            }
            catch
            {
                // Adapter diagnostics are part of activation authorization. If
                // their retained ownership cannot be observed, fail closed and
                // let the outer finally release the pump serializer for retry.
                return false;
            }

            bool PublishActivation()
            {
                lock (sessionGate)
                {
                    if (currentSession is not null)
                        return currentSession == session;
                    if (cleanupSession is not null ||
                        (LocalObjectLedger?.Count ?? 0) != 0 ||
                        pendingLocalObjectOwnerships != 0)
                        return false;

                    // A cleanup pass may already have cleared cleanupSession but
                    // still own its serializer until its finally block returns.
                    // Claim both gates without waiting so activation cannot publish
                    // a successor inside either final handoff window.
                    if (!cleanupGate.Wait(0))
                        return false;

                    try
                    {
                        if (!Authority.TryActivate(session))
                            return false;

                        currentSession = session;
                        // A new session starts with nothing applied and no reset pending.
                        LocalObjectDomain.Clear();
                        sessionCancellation = new CancellationTokenSource();
                        return true;
                    }
                    finally
                    {
                        cleanupGate.Release();
                    }
                }
            }

            // The product composition owns an atomic readiness fence: either
            // dependency loss wins and this publication is rejected, or this
            // publication wins and the ordered lifecycle reconciliation will
            // revoke it. Signature-neutral custom ports receive a final,
            // fail-closed resample after all potentially slow diagnostics.
            if (readinessAdmissionFence is not null)
            {
                return readinessAdmissionFence.TryExecuteIfReady(
                           PublishActivation,
                           out var activated) &&
                       activated;
            }

            return IsReadyForActivation() && PublishActivation();
        }
        finally
        {
            pumpGate.Release();
        }
    }

    private bool IsReadyForActivation()
    {
        try
        {
            return readiness.IsReady;
        }
        catch
        {
            // Readiness is authorization. An unavailable adapter must not make
            // activation throw or publish a receiver session speculatively.
            return false;
        }
    }

    /// <summary>Local-object apply domain (local-object host and location).</summary>
    internal ApplyDomainGate LocalObjectDomain { get; } = new();

    /// <summary>
    /// Releases every furniture/light receipt of the active session and then
    /// re-admits the desired furniture and lights. Used when the local-object
    /// host is lost and when the player's location changes. Actors, the
    /// connection and all desired state are untouched.
    /// </summary>
    public void ResetLocalObjects()
    {
        if (localObjectOwnershipCleanup is not null)
            LocalObjectDomain.RequestReset();
    }

    /// <summary>
    /// Releases only the receipts of one local-object kind (actors on
    /// GPose or cutscene entry and exit), then re-admits the desired local
    /// objects. Receipts of other kinds are kept, so furniture and lights are
    /// not redrawn; their re-admission finds them already applied.
    /// </summary>
    public void ResetLocalObjects(RscObjectKind kind)
    {
        if (localObjectOwnershipCleanup is not null)
            LocalObjectDomain.RequestReset(kind);
    }

    /// <summary>The furniture and lights the active session holds, for display. Empty without a session.</summary>
    public IReadOnlyList<AuthoritativeObjectState> GetDesiredLocalObjects()
    {
        ReceiverSessionKey? session;
        lock (sessionGate)
            session = currentSession;
        return session is { } active && LocalObjectStore is { } store
            ? store.GetSessionStates(active)
            : [];
    }

    private bool IsReadyForApply() => localObjectReadiness.IsDomainReady;

    /// <summary>
    /// Advances domain resets by one bounded step each: a cleanup pass while
    /// cleaning, then (once the domain is ready again) a bounded batch of
    /// re-admissions. Never runs inside a readiness or authority lock.
    /// </summary>
    private async Task AdvanceDomainResetsAsync(ReceiverSessionKey session, CancellationToken token)
    {
        if (localObjectOwnershipCleanup is not null && LocalObjectStore is not null && LocalObjectCoordinator is not null)
        {
            await AdvanceDomainResetAsync(
                LocalObjectDomain,
                localObjectReadiness,
                (epochSession, releases) => localObjectOwnershipCleanup.CleanupPassAsync(
                    epochSession, limits.MaximumCleanupEntriesPerPass, releases, token),
                () => LocalObjectStore.GetSessionStateIds(session),
                objectId => LocalObjectStore.TryGetCurrent(session, objectId, out _),
                objectId => LocalObjectCoordinator.TryAdmit(session, objectId),
                session).ConfigureAwait(false);
        }
    }

    private async Task AdvanceDomainResetAsync<TPass>(
        ApplyDomainGate domain,
        DomainGatedReadiness domainReadiness,
        Func<ReceiverSessionKey, Func<RscObjectKind, bool>?, Task<TPass>> cleanupPass,
        Func<IReadOnlyList<RscObjectId>> desiredIds,
        Func<RscObjectId, bool> isDesired,
        Func<RscObjectId, bool> admit,
        ReceiverSessionKey session)
    {
        // The epoch and the kinds it releases are read together: a reset
        // widened meanwhile has a newer epoch, so this pass cannot complete it.
        var cleaningEpoch = domain.CleaningEpoch(out var releases);
        if (cleaningEpoch >= 0)
        {
            var pass = await cleanupPass(session, releases).ConfigureAwait(false);
            var (failed, remaining) = pass switch
            {
                LocalObjectOwnershipCleanupResult local => (local.Failed, local.HasRemaining),
                _ => (1, true),
            };
            if (failed == 0 && !remaining)
                domain.TryCompleteCleaning(cleaningEpoch, desiredIds());
            return;
        }

        if (domain.Phase != ApplyDomainPhase.Readmitting || !domainReadiness.IsDomainReady)
            return;

        var batch = domain.TakeReadmissions(limits.MaximumSnapshotAdmissionsPerPump);
        var deferred = new List<RscObjectId>();
        for (var index = 0; index < batch.Length; index++)
        {
            var objectId = batch[index];
            if (!isDesired(objectId))
                continue;
            if (!admit(objectId))
            {
                // Mailbox full: keep the rest in order for the next pump.
                deferred.AddRange(batch.Skip(index));
                break;
            }
        }
        domain.ReturnReadmissions(deferred);
        domain.CompleteReadmissionIfDrained();
    }

    private bool TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (readinessAdmissionFence is not null)
            return readinessAdmissionFence.TryExecuteIfReady(operation, out result);

        if (!IsReadyForActivation())
        {
            result = default!;
            return false;
        }

        result = operation();
        return true;
    }

    public ReceiverActorSnapshotResult StageSnapshot(
        ReceiverSessionKey session,
        ObjectSnapshotMessage snapshot) =>
        Snapshots.Stage(session, snapshot);

    public ReceiverActorIngressResult AcceptCreated(
        ReceiverSessionKey session,
        ObjectCreatedMessage message) => Events.AcceptCreated(session, message);

    public ReceiverActorIngressResult AcceptUpdated(
        ReceiverSessionKey session,
        ObjectUpdatedMessage message) => Events.AcceptUpdated(session, message);

    public ReceiverActorIngressResult AcceptDeleted(
        ReceiverSessionKey session,
        ObjectDeletedMessage message) => Events.AcceptDeleted(session, message);

    public async Task<ReceiverActorPumpResult> PumpAsync(
        ReceiverSessionKey session,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!pumpGate.Wait(0))
            return EmptyPump(ReceiverActorPumpStatus.Busy);

        try
        {
            CancellationToken sessionToken;
            lock (sessionGate)
            {
                if (currentSession != session || sessionCancellation is null)
                    return EmptyPump(ReceiverActorPumpStatus.InactiveSession);

                sessionToken = sessionCancellation.Token;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionToken);
            var token = linkedCancellation.Token;
            token.ThrowIfCancellationRequested();

            ReceiverActorSnapshotResult? snapshot = null;
            if (Snapshots.HasPendingFor(session))
            {
                snapshot = Snapshots.AdmitPass(
                    session,
                    limits.MaximumSnapshotAdmissionsPerPump);
            }

            if (IsReadyForActivation())
                await AdvanceDomainResetsAsync(session, token).ConfigureAwait(false);

            if (!IsReadyForActivation() || !IsReadyForApply())
                return new(ReceiverActorPumpStatus.DependenciesUnavailable, snapshot);

            var promotedLocalRetries = localObjectWorker?.PromoteDueRetries(
                now,
                limits.MaximumRetryPromotionsPerPump) ?? 0;
            var tasks = new Task<LocalObjectApplyWorkResult>[limits.MaximumConcurrentWorkers];
            for (var index = 0; index < tasks.Length; index++)
            {
                tasks[index] = localObjectWorker is null
                    ? Task.FromResult<LocalObjectApplyWorkResult>(default)
                    : localObjectWorker.TryProcessNextAsync(now, token);
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var localResults = results
                .Where(result => result.Processed)
                .ToArray();
            return new(
                token.IsCancellationRequested
                    ? ReceiverActorPumpStatus.Cancelled
                    : ReceiverActorPumpStatus.Ran,
                snapshot)
            {
                PromotedLocalRetries = promotedLocalRetries,
                LocalWork = localResults,
            };
        }
        catch (OperationCanceledException)
        {
            return EmptyPump(ReceiverActorPumpStatus.Cancelled);
        }
        finally
        {
            pumpGate.Release();
        }
    }

    public async Task<ReceiverApplyCleanupResult> DisconnectAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancelledSession;
        lock (sessionGate)
        {
            if (currentSession != session)
                return default;

            // No retry can legitimately own this gate while the session is
            // still active. Claim it before publishing cleanupSession so a
            // direct retry cannot clear the session in the short interval
            // before cancellation ownership is attached.
            if (!cleanupGate.Wait(0))
            {
                throw new InvalidOperationException(
                    "An active receiver session unexpectedly shares its cleanup serializer.");
            }

            cancelledSession = sessionCancellation;
            currentSession = null;
            // Session cleanup releases every receipt; domain resets are moot.
            LocalObjectDomain.Clear();
            cleanupSession = session;
            sessionCancellation = null;
            cleanupCancellation = null;
        }

        // Revoke desired/apply authority before cancellation callbacks can run.
        // A callback owned by host or adapter code must never keep intake live.
        try
        {
            Authority.Invalidate(session);
            try
            {
                BeforeSessionCancellationStart?.Invoke();
            }
            finally
            {
                if (cancelledSession is not null)
                    StartSessionCancellation(session, cancelledSession);
            }

            // Once disconnect is requested, the cleanup fence remains owned
            // even if the caller has already cancelled its wait. Reconnect
            // must not overlap the old session's bounded continuation.
            return await RunCleanupPassAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                BeforeCleanupGateRelease?.Invoke();
            }
            finally
            {
                cleanupGate.Release();
            }
        }
    }

    public async Task<ReceiverApplyCleanupResult> RetryCleanupAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        lock (sessionGate)
        {
            if (cleanupSession != session)
                return default;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return AddCancellationDiagnostics(
                session,
                new(0, 0, HasRemainingOwnership: true, Cancelled: true));
        }

        if (!cleanupGate.Wait(0))
        {
            return AddCancellationDiagnostics(
                session,
                new ReceiverApplyCleanupResult(
                    0,
                    0,
                    HasRemainingOwnership: true,
                    Cancelled: false)
                {
                    Busy = true,
                });
        }

        try
        {
            lock (sessionGate)
            {
                if (cleanupSession != session)
                    return default;
            }

            return await RunCleanupPassAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                BeforeCleanupGateRelease?.Invoke();
            }
            finally
            {
                cleanupGate.Release();
            }
        }
    }

    private async Task<ReceiverApplyCleanupResult> RunCleanupPassAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken)
    {
        var deferredForCallback = await CancellationCallbacksRemainAfterWaitAsync(
            session,
            cancellationToken).ConfigureAwait(false);
        var cleanupToken = deferredForCallback
            ? new CancellationToken(canceled: true)
            : cancellationToken;
        var result = await cleanup.CleanupAsync(session, cleanupToken)
            .ConfigureAwait(false);
        if (deferredForCallback && result.Cancelled)
            result = result with { Cancelled = false };

        return CompleteCleanupPass(session, AddPumpDiagnostics(result));
    }

    private ReceiverApplyCleanupResult AddPumpDiagnostics(
        ReceiverApplyCleanupResult result)
    {
        // A non-cooperative worker may return after the bounded core/ownership
        // pass and retain compensation on its way out. Keep the exact cleanup
        // session retryable until that worker releases the shared pump slot.
        // Once the slot is stable, also require every assembly-owned adapter
        // receipt table to be empty. Ledger cleanup alone cannot authorize
        // dependency disposal when an adapter still reports residual state.
        if (!pumpGate.Wait(0))
        {
            return result with
            {
                HasRemainingOwnership = true,
                Busy = true,
            };
        }

        try
        {
            return HasResidualAdapterOwnership()
                ? result with { HasRemainingOwnership = true }
                : result;
        }
        finally
        {
            pumpGate.Release();
        }
    }

    private bool HasResidualAdapterOwnership() =>
        ReadPendingOwnershipCount(
            localObjectOwnershipDiagnostics is null
                ? null
                : () => localObjectOwnershipDiagnostics.PendingOwnershipCount) != 0;

    /// <summary>
    /// Provides the application root with a final dependency-disposal fence.
    /// This is intentionally independent of an active or retained cleanup
    /// session: a host adapter may already contain orphaned receipts before the
    /// first activation, and lifecycle shutdown has no session through which to
    /// discover them.
    /// </summary>
    internal bool HasResidualExternalOwnershipForShutdown()
    {
        // The pump is the only runtime path that can create new external
        // ownership. A busy slot is therefore retained ownership until a later
        // bounded shutdown retry can obtain and sample the stable boundary.
        if (!pumpGate.Wait(0))
            return true;

        try
        {
            return HasResidualAdapterOwnership();
        }
        finally
        {
            pumpGate.Release();
        }
    }

    private async Task<bool> CancellationCallbacksRemainAfterWaitAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken)
    {
        Task? cancellationTask;
        lock (sessionGate)
        {
            cancellationTask = cleanupSession == session &&
                cleanupCancellation is { CompletionObserved: false } state
                ? state.Task
                : null;
        }

        if (cancellationTask is null || cancellationTask.IsCompleted)
            return false;
        if (cancellationToken.IsCancellationRequested)
            return false;

        var boundedWait = Task.Delay(
            maximumCancellationCallbackWait,
            cancellationToken);
        var completed = await Task.WhenAny(cancellationTask, boundedWait)
            .ConfigureAwait(false);
        return completed != cancellationTask &&
            !cancellationToken.IsCancellationRequested;
    }

    private ReceiverApplyCleanupResult CompleteCleanupPass(
        ReceiverSessionKey session,
        ReceiverApplyCleanupResult result)
    {
        lock (sessionGate)
        {
            if (cleanupSession == session)
            {
                if (cleanupCancellation is { } cancellation)
                {
                    cancellation.OwnershipComplete =
                        !result.NeedsRetry;
                    if (cancellation.OwnershipComplete &&
                        cancellation.CompletionObserved)
                    {
                        cleanupSession = null;
                        cleanupCancellation = null;
                    }
                }
                else if (!result.NeedsRetry)
                {
                    cleanupSession = null;
                }
            }
        }

        return AddCancellationDiagnostics(session, result);
    }

    private ReceiverApplyCleanupResult AddCancellationDiagnostics(
        ReceiverSessionKey session,
        ReceiverApplyCleanupResult result)
    {
        lock (sessionGate)
        {
            var pending = cleanupSession == session &&
                cleanupCancellation is { CompletionObserved: false }
                ? 1
                : 0;
            return result with
            {
                HasRemainingOwnership = result.HasRemainingOwnership || pending != 0,
                PendingCancellationCallbacks = pending,
                CancellationCallbackFailures = cancellationCallbackFailures,
            };
        }
    }

    private void StartSessionCancellation(
        ReceiverSessionKey session,
        CancellationTokenSource cancellation)
    {
        Task task;
        try
        {
            task = cancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            task = Task.FromException(exception);
        }

        var state = new SessionCancellationState(this, session, cancellation, task);
        lock (sessionGate)
        {
            if (cleanupSession != session || cleanupCancellation is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException(
                    "Receiver session cancellation ownership changed unexpectedly.");
            }

            cleanupCancellation = state;
        }

        ObserveSessionCancellation(state);
    }

    private void ObserveSessionCancellation(SessionCancellationState state)
    {
        if (state.Task.IsCompleted)
        {
            CompleteSessionCancellation(state);
            return;
        }

        _ = state.Task.ContinueWith(
            static (_, callbackState) =>
            {
                var cancellation = (SessionCancellationState)callbackState!;
                cancellation.Owner.CompleteSessionCancellation(cancellation);
            },
            state,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteSessionCancellation(SessionCancellationState state)
    {
        _ = state.Task.Exception;
        var dispose = false;
        lock (sessionGate)
        {
            if (state.CompletionObserved)
                return;

            state.CompletionObserved = true;
            dispose = true;
            if (state.Task.IsFaulted && cancellationCallbackFailures < int.MaxValue)
                cancellationCallbackFailures++;

            if (cleanupSession == state.Session &&
                ReferenceEquals(cleanupCancellation, state) &&
                state.OwnershipComplete)
            {
                cleanupSession = null;
                cleanupCancellation = null;
            }
        }

        if (dispose)
            state.Cancellation.Dispose();
    }

    private static ReceiverActorPumpResult EmptyPump(ReceiverActorPumpStatus status) =>
        new(status, null);

    private sealed class SessionCancellationState(
        ReceiverActorRuntime owner,
        ReceiverSessionKey session,
        CancellationTokenSource cancellation,
        Task task)
    {
        public ReceiverActorRuntime Owner { get; } = owner;
        public ReceiverSessionKey Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; } = task;
        public bool CompletionObserved { get; set; }
        public bool OwnershipComplete { get; set; }
    }
}
