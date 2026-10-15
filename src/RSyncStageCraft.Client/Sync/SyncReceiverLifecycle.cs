using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Sync;

public enum SyncReceiverLifecycleState
{
    Disconnected,
    Active,
    Suspended,
    Cleaning,
    CleanupRequired,
    Terminated,
}

public enum SyncReceiverConnectStatus
{
    Activated,
    AlreadyActive,
    InvalidSession,
    DependenciesUnavailable,
    ActiveSessionConflict,
    CleanupRequired,
    ConnectionInUse,
    ConnectionEpochExhausted,
    ReceiverRejected,
    CapacityExceeded,
    Cancelled,
    Terminated,
}

public readonly record struct SyncReceiverConnectResult(
    SyncReceiverConnectStatus Status,
    ReceiverSessionKey? Session)
{
    public bool Connected =>
        Status is SyncReceiverConnectStatus.Activated or
            SyncReceiverConnectStatus.AlreadyActive;
}

public enum SyncReceiverDisconnectStatus
{
    Disconnected,
    Suspended,
    InactiveSession,
    CleanupRequired,
    CapacityExceeded,
    Cancelled,
    Terminated,
}

public readonly record struct SyncReceiverDisconnectResult(
    SyncReceiverDisconnectStatus Status,
    ReceiverApplyCleanupResult ReceiverCleanup)
{
    public bool NeedsRetry => Status == SyncReceiverDisconnectStatus.CleanupRequired;
}

public readonly record struct SyncReceiverLifecycleSnapshot(
    SyncReceiverLifecycleState State,
    ReceiverSessionKey? ActiveSession,
    ReceiverSessionKey? CleanupSession,
    SyncConnectionLease Lease);

public enum SyncReceiverReadinessStatus
{
    Ready,
    Inactive,
    Suspended,
    CleanupRequired,
    CapacityExceeded,
    Cancelled,
    Terminated,
}

public readonly record struct SyncReceiverReadinessResult(
    SyncReceiverReadinessStatus Status,
    ReceiverSessionKey? Session,
    ReceiverApplyCleanupResult ReceiverCleanup);

/// <summary>
/// Owns the connection-to-receiver lifecycle boundary. All transitions are
/// serialized so a new connection epoch cannot be acquired until
/// receiver-state and plugin-owned cleanup for the prior session have
/// converged.
/// </summary>
public sealed class SyncReceiverLifecycle
{
    private readonly object admissionGate = new();
    private readonly object stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SyncConnectionLock connection;
    private readonly IReceiverActorSessionRuntime receiver;
    private readonly IReceiverReadinessPort readiness;
    private readonly int maximumPendingOperations;
    private SyncReceiverLifecycleState state;
    private ReceiverSessionKey? activeSession;
    private SyncConnectionLease activeLease;
    private ReceiverSessionKey? cleanupSession;
    private SyncReceiverLifecycleState cleanupDestination =
        SyncReceiverLifecycleState.Disconnected;
    private ReceiverSessionKey? pendingReceiverCleanupSession;
    private Task<ReceiverApplyCleanupResult>? pendingReceiverCleanupTask;
    private int pendingOperations;
    private bool shutdownRequested;
    private Task<SyncReceiverDisconnectResult>? shutdownTask;

    internal SyncReceiverLifecycle(
        SyncConnectionLock connection,
        IReceiverActorSessionRuntime receiver,
        IReceiverReadinessPort readiness,
        int maximumPendingOperations =
            ClientLimits.DefaultReceiverLifecycleOperations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(readiness);
        if (maximumPendingOperations <= 0 ||
            maximumPendingOperations > ClientLimits.MaximumReceiverLifecycleOperations)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingOperations));
        }

        this.connection = connection;
        this.receiver = receiver;
        this.readiness = readiness;
        this.maximumPendingOperations = maximumPendingOperations;
    }

    public int MaximumPendingOperations => maximumPendingOperations;

    // Assembly-owned notification fired only after this lifecycle has claimed
    // an exact active session for revocation. Callbacks run outside the state
    // and connection locks and must perform bounded synchronous admission
    // retirement only.
    internal event Action<ReceiverSessionKey>? OnSessionRevoked;

    // Deterministic seam proving retry admission is reopened before a
    // retryable terminal-shutdown result becomes observable to its waiters.
    // It is assembly-only and cannot alter production cleanup behavior.
    internal Action? BeforeRetryableShutdownResultPublication { get; set; }

    public int PendingOperationCount
    {
        get
        {
            lock (admissionGate)
                return pendingOperations;
        }
    }

    public bool ShutdownRequested
    {
        get
        {
            lock (admissionGate)
                return shutdownRequested;
        }
    }

    public SyncReceiverLifecycleSnapshot Current
    {
        get
        {
            var snapshot = ReadOwnedState();
            if (ShutdownRequested)
                return snapshot with { Lease = default };

            var hasAuthority = snapshot.State == SyncReceiverLifecycleState.Active &&
                snapshot.ActiveSession is { } session &&
                connection.IsCurrent(snapshot.Lease) &&
                receiver.IsActive(session);
            // Recheck after the receiver sample. Shutdown publication must be
            // the final authority fence even if that sample blocks or reenters.
            return hasAuthority && !ShutdownRequested
                ? snapshot
                : snapshot with { Lease = default };
        }
    }

    internal bool HasActiveAuthority(
        ReceiverSessionKey session,
        SyncConnectionLease lease)
    {
        var snapshot = Current;
        return snapshot.State == SyncReceiverLifecycleState.Active &&
            snapshot.ActiveSession == session &&
            snapshot.Lease == lease;
    }

    internal bool HasActiveAuthority(ReceiverSessionKey session)
    {
        var snapshot = Current;
        return snapshot.State == SyncReceiverLifecycleState.Active &&
            snapshot.ActiveSession == session &&
            snapshot.Lease.IsValid &&
            snapshot.Lease.Epoch == session.ConnectionEpoch;
    }

    public async Task<SyncReceiverConnectResult> ConnectAsync(
        long sessionId,
        long sessionGeneration,
        CancellationToken cancellationToken = default)
    {
        if (sessionId <= 0 || sessionGeneration <= 0)
            return new(SyncReceiverConnectStatus.InvalidSession, null);

        using var operation = TryAdmitOperation(out var terminated);
        if (operation is null)
        {
            var shutdown = Current;
            var cleanupPending = terminated &&
                shutdown.State == SyncReceiverLifecycleState.CleanupRequired;
            return new(
                terminated
                    ? cleanupPending
                        ? SyncReceiverConnectStatus.CleanupRequired
                        : SyncReceiverConnectStatus.Terminated
                    : SyncReceiverConnectStatus.CapacityExceeded,
                cleanupPending ? shutdown.CleanupSession : null);
        }

        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
            return new(SyncReceiverConnectStatus.Cancelled, null);

        try
        {
            if (ShutdownRequested)
            {
                var shutdown = Current;
                return shutdown.State == SyncReceiverLifecycleState.CleanupRequired
                    ? new(SyncReceiverConnectStatus.CleanupRequired, shutdown.CleanupSession)
                    : new(SyncReceiverConnectStatus.Terminated, null);
            }

            var snapshot = Current;
            if (snapshot.State == SyncReceiverLifecycleState.Terminated)
                return new(SyncReceiverConnectStatus.Terminated, null);

            if (snapshot.State == SyncReceiverLifecycleState.Active)
            {
                if (!snapshot.Lease.IsValid &&
                    snapshot.ActiveSession is { } orphaned)
                {
                    try
                    {
                        var retired = await DisconnectActiveUnsafeAsync(
                            orphaned,
                            SyncReceiverLifecycleState.Disconnected).ConfigureAwait(false);
                        if (retired.Status != SyncReceiverDisconnectStatus.Disconnected)
                        {
                            return new(
                                SyncReceiverConnectStatus.CleanupRequired,
                                orphaned);
                        }
                    }
                    catch
                    {
                        MarkCleanupRequired(orphaned);
                        throw;
                    }

                    snapshot = Current;
                }
                else
                {
                    if (snapshot.ActiveSession is { } current &&
                        current.SessionId == sessionId &&
                        current.SessionGeneration == sessionGeneration)
                    {
                        return new(SyncReceiverConnectStatus.AlreadyActive, current);
                    }

                    return new(
                        SyncReceiverConnectStatus.ActiveSessionConflict,
                        snapshot.ActiveSession);
                }
            }

            if (snapshot.State is SyncReceiverLifecycleState.Cleaning or
                SyncReceiverLifecycleState.CleanupRequired)
            {
                return new(SyncReceiverConnectStatus.CleanupRequired, snapshot.CleanupSession);
            }

            if (!IsReadyForAdmission())
                return new(SyncReceiverConnectStatus.DependenciesUnavailable, null);

            if (!connection.TryAcquireNew(
                    out var lease,
                    out var cancellationPending,
                    out var connectionInUse))
            {
                return new(
                    cancellationPending
                        ? SyncReceiverConnectStatus.CleanupRequired
                        : connectionInUse
                            ? SyncReceiverConnectStatus.ConnectionInUse
                            : SyncReceiverConnectStatus.ConnectionEpochExhausted,
                    null);
            }

            var session = new ReceiverSessionKey(sessionId, sessionGeneration, lease.Epoch);
            bool activated;
            try
            {
                activated = receiver.TryActivate(session);
            }
            catch
            {
                await CleanupRejectedActivationAsync(
                    lease,
                    session,
                    receiverMayOwnState: true).ConfigureAwait(false);
                throw;
            }

            if (activated)
            {
                SetState(
                    SyncReceiverLifecycleState.Active,
                    session,
                    null,
                    activeConnectionLease: lease);
                return new(SyncReceiverConnectStatus.Activated, session);
            }

            // Activation owns no usable receiver session, but the acquired
            // connection epoch must still be invalidated and scrubbed before
            // another attempt can receive a new epoch.
            await CleanupRejectedActivationAsync(
                lease,
                session,
                receiverMayOwnState: false).ConfigureAwait(false);
            return new(SyncReceiverConnectStatus.ReceiverRejected, null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SyncReceiverDisconnectResult> DisconnectAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        using var operation = TryAdmitOperation(out var terminated);
        if (operation is null)
        {
            var shutdown = Current;
            var cleanupPending = terminated &&
                shutdown.State == SyncReceiverLifecycleState.CleanupRequired;
            return new(
                terminated
                    ? cleanupPending
                        ? SyncReceiverDisconnectStatus.CleanupRequired
                        : SyncReceiverDisconnectStatus.Terminated
                    : SyncReceiverDisconnectStatus.CapacityExceeded,
                default);
        }

        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
            return new(SyncReceiverDisconnectStatus.Cancelled, default);

        try
        {
            if (ShutdownRequested)
            {
                return new(
                    Current.State == SyncReceiverLifecycleState.CleanupRequired
                        ? SyncReceiverDisconnectStatus.CleanupRequired
                        : SyncReceiverDisconnectStatus.Terminated,
                    default);
            }

            return await DisconnectActiveUnsafeAsync(
                session,
                SyncReceiverLifecycleState.Disconnected).ConfigureAwait(false);
        }
        catch
        {
            MarkCleanupRequired(session);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Reconciles live dependency health with the active connection. A loss of
    /// readiness performs the same fenced cleanup as disconnect and lands in a
    /// suspended state; recovery requires an explicit activation and new epoch.
    /// </summary>
    public Task<SyncReceiverReadinessResult> ReconcileReadinessAsync(
        CancellationToken cancellationToken = default) =>
        ReconcileReadinessCoreAsync(observedReadiness: null, cancellationToken);

    internal Task<SyncReceiverReadinessResult> ReconcileReadinessAsync(
        bool isReady,
        CancellationToken cancellationToken = default) =>
        ReconcileReadinessCoreAsync(isReady, cancellationToken);

    private async Task<SyncReceiverReadinessResult> ReconcileReadinessCoreAsync(
        bool? observedReadiness,
        CancellationToken cancellationToken)
    {
        using var operation = TryAdmitOperation(out var terminated);
        if (operation is null)
        {
            var shutdown = Current;
            var cleanupPending = terminated &&
                shutdown.State == SyncReceiverLifecycleState.CleanupRequired;
            return new(
                terminated
                    ? cleanupPending
                        ? SyncReceiverReadinessStatus.CleanupRequired
                        : SyncReceiverReadinessStatus.Terminated
                    : SyncReceiverReadinessStatus.CapacityExceeded,
                cleanupPending ? shutdown.CleanupSession : null,
                default);
        }

        // The application root passes the exact dependency observation that
        // initiated this reconciliation. Component callers are sampled before
        // entering lifecycle serialization so readiness cannot synchronously
        // reenter and wait on the operation currently evaluating it.
        var isReady = observedReadiness ?? IsReadyForAdmission();
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
            return new(SyncReceiverReadinessStatus.Cancelled, null, default);

        ReceiverSessionKey? session = null;
        try
        {
            if (ShutdownRequested)
            {
                var shutdown = Current;
                return new(
                    shutdown.State == SyncReceiverLifecycleState.CleanupRequired
                        ? SyncReceiverReadinessStatus.CleanupRequired
                        : SyncReceiverReadinessStatus.Terminated,
                    shutdown.CleanupSession,
                    default);
            }

            var snapshot = Current;
            if (snapshot.State == SyncReceiverLifecycleState.Terminated)
            {
                return new(
                    SyncReceiverReadinessStatus.Terminated,
                    null,
                    default);
            }

            if (isReady)
            {
                if (snapshot.State == SyncReceiverLifecycleState.Active &&
                    !snapshot.Lease.IsValid &&
                    snapshot.ActiveSession is { } orphaned)
                {
                    session = orphaned;
                    var retired = await DisconnectActiveUnsafeAsync(
                        orphaned,
                        SyncReceiverLifecycleState.Disconnected).ConfigureAwait(false);
                    return retired.Status == SyncReceiverDisconnectStatus.Disconnected
                        ? new(
                            SyncReceiverReadinessStatus.Inactive,
                            null,
                            retired.ReceiverCleanup)
                        : new(
                            SyncReceiverReadinessStatus.CleanupRequired,
                            orphaned,
                            retired.ReceiverCleanup);
                }

                return snapshot.State switch
                {
                    SyncReceiverLifecycleState.Active => new(
                        SyncReceiverReadinessStatus.Ready,
                        snapshot.ActiveSession,
                        default),
                    SyncReceiverLifecycleState.CleanupRequired => new(
                        SyncReceiverReadinessStatus.CleanupRequired,
                        snapshot.CleanupSession,
                        default),
                    SyncReceiverLifecycleState.Suspended => new(
                        SyncReceiverReadinessStatus.Suspended,
                        null,
                        default),
                    _ => new(SyncReceiverReadinessStatus.Inactive, null, default),
                };
            }

            if (snapshot.State == SyncReceiverLifecycleState.CleanupRequired)
            {
                return new(
                    SyncReceiverReadinessStatus.CleanupRequired,
                    snapshot.CleanupSession,
                    default);
            }

            if (snapshot.State == SyncReceiverLifecycleState.Suspended)
                return new(SyncReceiverReadinessStatus.Suspended, null, default);

            if (snapshot.State != SyncReceiverLifecycleState.Active ||
                snapshot.ActiveSession is not { } active)
            {
                return new(SyncReceiverReadinessStatus.Inactive, null, default);
            }

            session = active;
            var disconnected = await DisconnectActiveUnsafeAsync(
                active,
                SyncReceiverLifecycleState.Suspended).ConfigureAwait(false);
            return new(
                disconnected.Status == SyncReceiverDisconnectStatus.Suspended
                    ? SyncReceiverReadinessStatus.Suspended
                    : SyncReceiverReadinessStatus.CleanupRequired,
                active,
                disconnected.ReceiverCleanup);
        }
        catch
        {
            if (session is { } cleanup)
                MarkCleanupRequired(cleanup);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SyncReceiverDisconnectResult> RetryCleanupAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        using var operation = TryAdmitOperation(out var terminated);
        if (operation is null)
        {
            var shutdown = Current;
            var cleanupPending = terminated &&
                shutdown.State == SyncReceiverLifecycleState.CleanupRequired;
            if (cleanupPending && shutdown.CleanupSession == session)
            {
                // Terminal shutdown owns the lifecycle serializer after it
                // closes ordinary operation admission. Advance the retained
                // exact-session obligation through that existing single-flight
                // path instead of making the public cleanup retry a no-op.
                return await ShutdownAsync().ConfigureAwait(false);
            }

            return new(
                terminated
                    ? cleanupPending
                        ? SyncReceiverDisconnectStatus.CleanupRequired
                        : SyncReceiverDisconnectStatus.Terminated
                    : SyncReceiverDisconnectStatus.CapacityExceeded,
                default);
        }

        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
            return new(SyncReceiverDisconnectStatus.Cancelled, default);

        try
        {
            if (ShutdownRequested)
            {
                return new(
                    Current.State == SyncReceiverLifecycleState.CleanupRequired
                        ? SyncReceiverDisconnectStatus.CleanupRequired
                        : SyncReceiverDisconnectStatus.Terminated,
                    default);
            }

            return await RetryCleanupUnsafeAsync(session).ConfigureAwait(false);
        }
        catch
        {
            MarkCleanupRequired(session);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Permanently closes this lifecycle. Active or retained cleanup state is
    /// advanced through the same bounded disconnect/retry path; incomplete
    /// cleanup remains retryable, but its destination stays terminal and no
    /// later connection can be admitted.
    /// </summary>
    public Task<SyncReceiverDisconnectResult> ShutdownAsync()
    {
        TaskCompletionSource<SyncReceiverDisconnectResult> completion;
        lock (admissionGate)
        {
            shutdownRequested = true;
            if (shutdownTask is not null)
                return shutdownTask;

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownTask = completion.Task;
        }

        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(
        TaskCompletionSource<SyncReceiverDisconnectResult> completion)
    {
        try
        {
            var result = await ShutdownCoreAsync().ConfigureAwait(false);
            if (result.Status != SyncReceiverDisconnectStatus.Terminated)
            {
                ClearRetryableShutdown(completion.Task);
                BeforeRetryableShutdownResultPublication?.Invoke();
            }

            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            ClearRetryableShutdown(completion.Task);
            completion.TrySetException(exception);
        }
    }

    private async Task<SyncReceiverDisconnectResult> ShutdownCoreAsync()
    {
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        ReceiverSessionKey? cleanup = null;
        try
        {
            var snapshot = Current;
            if (snapshot.State == SyncReceiverLifecycleState.Terminated)
                return new(SyncReceiverDisconnectStatus.Terminated, default);

            if (snapshot.State == SyncReceiverLifecycleState.Active &&
                snapshot.ActiveSession is { } active)
            {
                cleanup = active;
                return await DisconnectActiveUnsafeAsync(
                    active,
                    SyncReceiverLifecycleState.Terminated).ConfigureAwait(false);
            }

            if (snapshot.State == SyncReceiverLifecycleState.CleanupRequired &&
                snapshot.CleanupSession is { } pending)
            {
                cleanup = pending;
                return await RetryCleanupUnsafeAsync(
                    pending,
                    SyncReceiverLifecycleState.Terminated).ConfigureAwait(false);
            }

            if (snapshot.State == SyncReceiverLifecycleState.Cleaning)
            {
                throw new InvalidOperationException(
                    "Serialized lifecycle cleanup was unexpectedly still active.");
            }

            if (connection.CurrentLease.IsValid ||
                connection.PendingCancellationCallbacks != 0)
            {
                throw new InvalidOperationException(
                    "An inactive lifecycle cannot terminate with a live or cancelling connection lease.");
            }

            SetState(SyncReceiverLifecycleState.Terminated, null, null);
            return new(SyncReceiverDisconnectStatus.Terminated, default);
        }
        catch
        {
            if (cleanup is { } session)
                MarkCleanupRequired(session);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void ClearRetryableShutdown(Task<SyncReceiverDisconnectResult> completed)
    {
        lock (admissionGate)
        {
            if (ReferenceEquals(shutdownTask, completed))
                shutdownTask = null;
        }
    }

    private LifecycleOperationLease? TryAdmitOperation(out bool terminated)
    {
        lock (admissionGate)
        {
            terminated = shutdownRequested;
            if (terminated || pendingOperations >= maximumPendingOperations)
                return null;

            pendingOperations++;
            return new LifecycleOperationLease(this);
        }
    }

    private void ExitOperation()
    {
        lock (admissionGate)
        {
            if (pendingOperations <= 0)
                throw new InvalidOperationException("Receiver lifecycle admission underflow.");

            pendingOperations--;
        }
    }

    private async Task<bool> TryEnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool IsReadyForAdmission()
    {
        try
        {
            return readiness.IsReady;
        }
        catch
        {
            // Readiness is authorization. If its source cannot be sampled,
            // connecting must remain closed and reconciliation must retire any
            // authority that was established by an earlier healthy sample.
            return false;
        }
    }

    private async Task CleanupRejectedActivationAsync(
        SyncConnectionLease lease,
        ReceiverSessionKey session,
        bool receiverMayOwnState)
    {
        connection.Invalidate(lease);
        SetState(SyncReceiverLifecycleState.Cleaning, null, session);
        try
        {
            Task<ReceiverApplyCleanupResult>? receiverCleanupTask = null;
            if (receiverMayOwnState)
            {
                // A throwing activation is not assumed atomic. Revoke any
                // admission or ownership it published before the failure.
                receiverCleanupTask = receiver.DisconnectAsync(
                    session,
                    CancellationToken.None);
                TrackPendingReceiverCleanup(session, receiverCleanupTask);
            }

            var receiverCleanup = receiverCleanupTask is null
                ? default
                : await ConsumePendingReceiverCleanupAsync(
                    session,
                    receiverCleanupTask).ConfigureAwait(false);
            if (connection.HasPendingCancellation(lease) ||
                receiverCleanup.NeedsRetry)
            {
                MarkCleanupRequired(session);
                return;
            }
            SetState(SyncReceiverLifecycleState.Disconnected, null, null);
        }
        catch
        {
            MarkCleanupRequired(session);
            throw;
        }
    }

    private async Task<SyncReceiverDisconnectResult> RetryCleanupUnsafeAsync(
        ReceiverSessionKey session,
        SyncReceiverLifecycleState? destinationOverride = null)
    {
        var snapshot = Current;
        if (snapshot.State != SyncReceiverLifecycleState.CleanupRequired ||
            snapshot.CleanupSession != session)
        {
            return new(SyncReceiverDisconnectStatus.InactiveSession, default);
        }

        if (connection.CurrentLease.IsValid)
            return new(SyncReceiverDisconnectStatus.CleanupRequired, default);

        var destination = destinationOverride ?? GetCleanupDestination(session);
        SetState(SyncReceiverLifecycleState.Cleaning, null, session, destination);
        if (!connection.TryGetIssuedLease(
                session.ConnectionEpoch,
                out var cleanupLease))
        {
            SetState(
                SyncReceiverLifecycleState.CleanupRequired,
                null,
                session,
                destination);
            return new(SyncReceiverDisconnectStatus.CleanupRequired, default);
        }

        ReceiverApplyCleanupResult receiverCleanup;
        if (TryGetPendingReceiverCleanup(session, out var pendingReceiverCleanup))
        {
            if (!pendingReceiverCleanup.IsCompleted)
            {
                var busy = new ReceiverApplyCleanupResult(
                    0,
                    0,
                    HasRemainingOwnership: true,
                    Cancelled: false)
                {
                    Busy = true,
                };
                SetState(
                    SyncReceiverLifecycleState.CleanupRequired,
                    null,
                    session,
                    destination);
                return new(SyncReceiverDisconnectStatus.CleanupRequired, busy);
            }

            receiverCleanup = await ConsumePendingReceiverCleanupAsync(
                session,
                pendingReceiverCleanup).ConfigureAwait(false);
            if (receiverCleanup.NeedsRetry)
            {
                receiverCleanup = await receiver.RetryCleanupAsync(
                    session,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            receiverCleanup = await receiver.RetryCleanupAsync(
                session,
                CancellationToken.None).ConfigureAwait(false);
        }

        if (connection.HasPendingCancellation(cleanupLease) ||
            receiverCleanup.NeedsRetry)
        {
            SetState(
                SyncReceiverLifecycleState.CleanupRequired,
                null,
                session,
                destination);
            return new(SyncReceiverDisconnectStatus.CleanupRequired, receiverCleanup);
        }

        SetState(destination, null, null);
        return new(CompletedStatus(destination), receiverCleanup);
    }

    private async Task<SyncReceiverDisconnectResult> DisconnectActiveUnsafeAsync(
        ReceiverSessionKey session,
        SyncReceiverLifecycleState successState)
    {
        var snapshot = ReadOwnedState();
        if (snapshot.State != SyncReceiverLifecycleState.Active ||
            snapshot.ActiveSession != session)
        {
            return new(SyncReceiverDisconnectStatus.InactiveSession, default);
        }

        var lease = snapshot.Lease;
        var currentLease = connection.CurrentLease;
        if (currentLease.IsValid && currentLease != lease)
        {
            // Never invalidate or clean a different live epoch. Revoke the
            // receiver session and retain a cleanup-required fence instead.
            SetState(
                SyncReceiverLifecycleState.Cleaning,
                null,
                session,
                successState);
            NotifySessionRevoked(session);
            var mismatchedCleanup = await receiver.DisconnectAsync(
                session,
                CancellationToken.None).ConfigureAwait(false);
            SetState(
                SyncReceiverLifecycleState.CleanupRequired,
                null,
                session,
                successState);
            return new(SyncReceiverDisconnectStatus.CleanupRequired, mismatchedCleanup);
        }

        SetState(SyncReceiverLifecycleState.Cleaning, null, session, successState);
        if (lease.IsValid)
            connection.Invalidate(lease);
        else if (!connection.TryGetIssuedLease(session.ConnectionEpoch, out lease))
        {
            SetState(
                SyncReceiverLifecycleState.CleanupRequired,
                null,
                session,
                successState);
            return new(SyncReceiverDisconnectStatus.CleanupRequired, default);
        }

        NotifySessionRevoked(session);

        // DisconnectAsync must cancel receiver admission/work before it
        // returns its task.
        var receiverCleanupTask = receiver.DisconnectAsync(
            session,
            CancellationToken.None);
        TrackPendingReceiverCleanup(session, receiverCleanupTask);
        var receiverCleanup = await ConsumePendingReceiverCleanupAsync(
            session,
            receiverCleanupTask).ConfigureAwait(false);
        var connectionOperationsRemain =
            connection.HasPendingCancellation(lease);
        if (connectionOperationsRemain ||
            receiverCleanup.NeedsRetry)
        {
            SetState(
                SyncReceiverLifecycleState.CleanupRequired,
                null,
                session,
                successState);
            return new(SyncReceiverDisconnectStatus.CleanupRequired, receiverCleanup);
        }

        SetState(successState, null, null);
        return new(CompletedStatus(successState), receiverCleanup);
    }

    private void NotifySessionRevoked(ReceiverSessionKey session)
    {
        var observers = OnSessionRevoked;
        if (observers is null)
            return;

        foreach (Action<ReceiverSessionKey> observer in observers.GetInvocationList())
        {
            try
            {
                observer(session);
            }
            catch
            {
                // Revocation has already been claimed. An assembly-owned
                // observer may not prevent receiver/connection cleanup from
                // converging; its stale completions remain fenced by leases.
            }
        }
    }

    private void TrackPendingReceiverCleanup(
        ReceiverSessionKey session,
        Task<ReceiverApplyCleanupResult> cleanupTask)
    {
        if (pendingReceiverCleanupTask is not null)
        {
            throw new InvalidOperationException(
                "A receiver cleanup task is already owned by this lifecycle.");
        }

        pendingReceiverCleanupSession = session;
        pendingReceiverCleanupTask = cleanupTask;
        _ = ObserveReceiverCleanupFaultAsync(cleanupTask);
    }

    private bool TryGetPendingReceiverCleanup(
        ReceiverSessionKey session,
        out Task<ReceiverApplyCleanupResult> cleanupTask)
    {
        if (pendingReceiverCleanupTask is { } pending)
        {
            if (pendingReceiverCleanupSession != session)
            {
                throw new InvalidOperationException(
                    "Receiver cleanup task ownership does not match the retained session.");
            }

            cleanupTask = pending;
            return true;
        }

        cleanupTask = null!;
        return false;
    }

    private async Task<ReceiverApplyCleanupResult> ConsumePendingReceiverCleanupAsync(
        ReceiverSessionKey session,
        Task<ReceiverApplyCleanupResult> cleanupTask)
    {
        try
        {
            return await cleanupTask.ConfigureAwait(false);
        }
        finally
        {
            if (pendingReceiverCleanupSession == session &&
                ReferenceEquals(pendingReceiverCleanupTask, cleanupTask))
            {
                pendingReceiverCleanupSession = null;
                pendingReceiverCleanupTask = null;
            }
        }
    }

    private static async Task ObserveReceiverCleanupFaultAsync(
        Task<ReceiverApplyCleanupResult> cleanupTask)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        catch
        {
            // The lifecycle-owned task remains faulted for the serialized
            // cleanup path to consume; this observer only prevents an
            // abandoned lifecycle from raising an unobserved-task fault.
        }
    }

    private SyncReceiverLifecycleState GetCleanupDestination(ReceiverSessionKey session)
    {
        lock (stateGate)
        {
            return cleanupSession == session
                ? cleanupDestination
                : SyncReceiverLifecycleState.Disconnected;
        }
    }

    private static SyncReceiverDisconnectStatus CompletedStatus(
        SyncReceiverLifecycleState state) =>
        state switch
        {
            SyncReceiverLifecycleState.Suspended => SyncReceiverDisconnectStatus.Suspended,
            SyncReceiverLifecycleState.Terminated => SyncReceiverDisconnectStatus.Terminated,
            _ => SyncReceiverDisconnectStatus.Disconnected,
        };

    private void MarkCleanupRequired(ReceiverSessionKey session)
    {
        lock (stateGate)
        {
            if (state == SyncReceiverLifecycleState.Cleaning && cleanupSession == session)
                state = SyncReceiverLifecycleState.CleanupRequired;
        }
    }

    private void SetState(
        SyncReceiverLifecycleState next,
        ReceiverSessionKey? active,
        ReceiverSessionKey? cleanup,
        SyncReceiverLifecycleState destination = SyncReceiverLifecycleState.Disconnected,
        SyncConnectionLease activeConnectionLease = default)
    {
        if (next == SyncReceiverLifecycleState.Active &&
            (active is null ||
             !activeConnectionLease.IsValid ||
             activeConnectionLease.Epoch != active.Value.ConnectionEpoch))
        {
            throw new InvalidOperationException(
                "An active receiver lifecycle requires its exact connection lease.");
        }

        lock (stateGate)
        {
            state = next;
            activeSession = active;
            activeLease = next == SyncReceiverLifecycleState.Active
                ? activeConnectionLease
                : default;
            cleanupSession = cleanup;
            cleanupDestination = cleanup is null
                ? SyncReceiverLifecycleState.Disconnected
                : destination;
        }
    }

    private SyncReceiverLifecycleSnapshot ReadOwnedState()
    {
        lock (stateGate)
            return new(state, activeSession, cleanupSession, activeLease);
    }

    private sealed class LifecycleOperationLease : IDisposable
    {
        private readonly SyncReceiverLifecycle owner;
        private int disposed;

        public LifecycleOperationLease(SyncReceiverLifecycle owner) =>
            this.owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.ExitOperation();
        }
    }
}
