using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Reconciles one exact local-object apply generation. The desired fence is
/// acquired inside the game-thread callback, so an older callback cannot
/// mutate, replace, or remove a newer generation. Replacement is ordered as
/// release old receipt, checkpoint release, then spawn the next generation.
/// Each external side effect is followed by a current-generation check before
/// another side effect or applied marker can be published; exact release and
/// newly-created ownership checkpointing still complete if the call reenters
/// desired-state admission and supersedes its own generation.
/// </summary>
public sealed class LocalObjectReconciler
{
    private readonly ILocalObjectDesiredFence desiredFence;
    private readonly LocalObjectOwnershipLedger ledger;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly ILocalObjectRuntimePort runtime;
    private readonly IReceiverReadinessPort readiness;
    private readonly ILocalObjectVisibility? visibility;
    private readonly TimeSpan loadTimeout;

    public static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LoadPollInterval = TimeSpan.FromMilliseconds(50);

    // Internal marker: the instance is owned but still loading. Never
    // returned from ReconcileAsync; it is resolved by the bounded load wait.
    private const string LoadingMarker = "Local object is loading.";

    internal LocalObjectReconciler(
        ILocalObjectDesiredFence desiredFence,
        LocalObjectOwnershipLedger ledger,
        IGameThreadDispatcher dispatcher,
        ILocalObjectRuntimePort runtime,
        IReceiverReadinessPort readiness,
        ILocalObjectVisibility? visibility = null,
        TimeSpan? loadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(desiredFence);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(readiness);

        this.desiredFence = desiredFence;
        this.ledger = ledger;
        this.dispatcher = dispatcher;
        this.runtime = runtime;
        this.readiness = readiness;
        this.visibility = visibility;
        this.loadTimeout = loadTimeout ?? DefaultLoadTimeout;
        if (this.loadTimeout <= TimeSpan.Zero || this.loadTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(loadTimeout));
    }

    public async Task<LocalObjectReconcileResult> ReconcileAsync(
        LocalObjectApplyToken token,
        AuthoritativeObjectState desiredState,
        CancellationToken cancellationToken = default)
    {
        if (!token.IsValid ||
            desiredState is null ||
            !desiredState.IsValid ||
            desiredState.ObjectId != token.ObjectId)
        {
            return new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: "Local desired state is invalid or does not match its token.");
        }

        var identity = LocalObjectSpawnIdentity.FromState(desiredState);
        if (!identity.IsValid)
        {
            return new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: "Local spawn identity is invalid.");
        }

        if (cancellationToken.IsCancellationRequested)
            return new(LocalObjectReconcileStatus.Cancelled);
        if (!readiness.IsReady)
            return DependenciesUnavailable();
        if (!desiredFence.IsCurrent(token))
            return new(LocalObjectReconcileStatus.Stale);
        // Not here (another zone, house or ward): desired, but no instance.
        // A location change resets the whole domain, so nothing owned can be
        // left behind for an object that stops being visible.
        if (!IsVisibleHere(desiredState))
            return new(LocalObjectReconcileStatus.Applied);

        var reservationIsCurrent = desiredFence.TryExecuteCurrent(
            token,
            () => ledger.TryReserve(token, identity),
            out var reservation);
        if (!reservationIsCurrent)
            return new(LocalObjectReconcileStatus.Stale);
        if (!reservation.Accepted)
            return FromReservation(reservation.Status);

        try
        {
            var reconciled = await dispatcher.InvokeAsync(
                () =>
                {
                    if (!readiness.IsReady)
                        return DependenciesUnavailable();

                    var executed = desiredFence.TryExecuteCurrent(
                        token,
                        () => ReconcileCurrent(token, identity, desiredState),
                        out var result);
                    return executed && desiredFence.IsCurrent(token)
                        ? result
                        : new LocalObjectReconcileResult(
                            LocalObjectReconcileStatus.Stale,
                            result.Handle);
                },
                cancellationToken).ConfigureAwait(false);
            return IsLoading(reconciled) && reconciled.Handle is { } loading
                ? await AwaitLoadAsync(token, desiredState, loading, cancellationToken).ConfigureAwait(false)
                : reconciled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(LocalObjectReconcileStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return desiredFence.IsCurrent(token)
                ? new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: ReceiverFailureDiagnostic.From(ex))
                : new(LocalObjectReconcileStatus.Stale);
        }
    }

    private LocalObjectReconcileResult ReconcileCurrent(
        LocalObjectApplyToken token,
        LocalObjectSpawnIdentity identity,
        AuthoritativeObjectState desiredState)
    {
        if (!ledger.TryGet(token, out var reservation) ||
            reservation.DesiredIdentity != identity)
        {
            return new(LocalObjectReconcileStatus.Stale);
        }

        if (reservation.Compensation is { })
        {
            var release = ReleaseCompensation(token, reservation);
            if (!release.Succeeded)
                return release;
            if (!desiredFence.IsCurrent(token))
                return new(LocalObjectReconcileStatus.Stale);
            if (!readiness.IsReady)
                return DependenciesUnavailable();

            if (!ledger.TryGet(token, out reservation) ||
                reservation.DesiredIdentity != identity)
            {
                return new(LocalObjectReconcileStatus.Stale);
            }
        }

        if (ledger.IsApplied(token) && reservation.Handle is { } alreadyApplied)
            return new(LocalObjectReconcileStatus.Applied, alreadyApplied);

        if (reservation.Action == LocalObjectReconcileAction.Replace)
        {
            var release = ReleaseForReplacement(token, reservation);
            if (!release.Succeeded)
                return release;
            if (!desiredFence.IsCurrent(token))
                return new(LocalObjectReconcileStatus.Stale);
            if (!readiness.IsReady)
                return DependenciesUnavailable();

            if (!ledger.TryGet(token, out reservation) ||
                reservation.DesiredIdentity != identity)
            {
                return new(LocalObjectReconcileStatus.Stale);
            }
        }

        if (reservation.Action == LocalObjectReconcileAction.Update)
        {
            ApplyPortResult applied;
            try
            {
                applied = runtime.ApplyOwned(reservation.Handle!.Value, desiredState);
            }
            catch (Exception exception)
            {
                // The instance is unusable; release this exact receipt so a
                // later retry respawns instead of leaving it owned.
                return ReleaseAfterApplyFault(token, reservation, exception);
            }
            if (applied.Status == ApplyPortStatus.AwaitingPayload)
                return Loading(reservation.Handle.Value);
            if (!applied.Succeeded)
                return FromPort(applied.Status, applied.Error);
            if (!desiredFence.IsCurrent(token))
                return new(LocalObjectReconcileStatus.Stale);
            if (!readiness.IsReady)
                return DependenciesUnavailable();

            return ledger.TryMarkApplied(token, reservation.Handle.Value)
                ? new(LocalObjectReconcileStatus.Applied, reservation.Handle)
                : new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Applied local object could not be checkpointed.");
        }

        if (!IsVisibleHere(desiredState))
        {
            // The location changed while this was queued: spawn nothing.
            ledger.TryRemoveEmpty(token);
            return new(LocalObjectReconcileStatus.Applied);
        }

        if (reservation.SpawnGenerationExhausted)
        {
            return new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: "Local spawn generation is exhausted.");
        }

        var request = new LocalObjectSpawnRequest(
            token,
            identity,
            reservation.NextSpawnGeneration,
            desiredState);
        if (!request.IsValid)
        {
            return new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: "Local spawn request is invalid.");
        }

        if (!ledger.TryBeginSpawnMutation(
                token,
                identity,
                reservation.NextSpawnGeneration,
                out reservation))
        {
            return new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object spawn ownership is being cleaned up or changed.");
        }

        try
        {
            var spawned = runtime.SpawnOwned(request);
            var handle = spawned.Value;
            if (spawned.Status == ApplyPortStatus.AwaitingPayload &&
                handle.IsValid &&
                handle.ObjectId == token.ObjectId &&
                handle.Kind == identity.Kind &&
                handle.SpawnGeneration == reservation.NextSpawnGeneration)
            {
                // Created and owned, but its model is still loading. Record
                // the exact receipt first so every exit path can release it.
                if (!ledger.TryRecordSpawnMutation(token, identity, handle))
                {
                    if (!ledger.TryRecordCompensationMutation(token, handle))
                    {
                        return new(
                            LocalObjectReconcileStatus.PermanentFailure,
                            Error: "Loading local object ownership could not be retained safely.");
                    }
                    var compensated = ReleaseCompensation(token, handle, mutationClaimed: true);
                    return compensated.Succeeded
                        ? new(
                            LocalObjectReconcileStatus.TransientFailure,
                            Error: "Loading local object could not be checkpointed and was compensated.")
                        : compensated;
                }
                if (!desiredFence.IsCurrent(token))
                    return new(LocalObjectReconcileStatus.Stale, Handle: handle);
                if (!readiness.IsReady)
                    return DependenciesUnavailable(handle);
                return Loading(handle);
            }

            if (!spawned.Succeeded)
            {
                if (!handle.IsValid)
                    return FromPort(spawned.Status, spawned.Error);

                if (!ledger.TryRecordCompensationMutation(token, handle))
                {
                    return new(
                        LocalObjectReconcileStatus.PermanentFailure,
                        Error: "Failed local object ownership could not be retained safely.");
                }

                var failedReceiptRelease = ReleaseCompensation(
                    token,
                    handle,
                    mutationClaimed: true);
                return failedReceiptRelease.Succeeded
                    ? FromPort(spawned.Status, spawned.Error)
                    : failedReceiptRelease;
            }

            if (!handle.IsValid ||
                handle.ObjectId != token.ObjectId ||
                handle.Kind != identity.Kind ||
                handle.SpawnGeneration != reservation.NextSpawnGeneration)
            {
                if (!handle.IsValid)
                {
                    return new(
                        LocalObjectReconcileStatus.PermanentFailure,
                        Error: "Runtime returned an invalid local object receipt.");
                }

                if (!ledger.TryRecordCompensationMutation(token, handle))
                {
                    return new(
                        LocalObjectReconcileStatus.PermanentFailure,
                        Error: "Mismatched local object ownership could not be retained safely.");
                }

                var compensation = ReleaseCompensation(
                    token,
                    handle,
                    mutationClaimed: true);
                return compensation.Succeeded
                    ? new(
                        LocalObjectReconcileStatus.PermanentFailure,
                        Error: "Runtime returned a mismatched local object receipt and it was compensated.")
                    : compensation;
            }

            if (ledger.TryRecordSpawnMutation(token, identity, handle))
            {
                if (!desiredFence.IsCurrent(token))
                {
                    return new(
                        LocalObjectReconcileStatus.Stale,
                        Handle: handle);
                }
                if (!readiness.IsReady)
                {
                    return DependenciesUnavailable(handle);
                }

                ledger.TryEndMutation(token);
                return ledger.TryMarkApplied(token, handle)
                    ? new(LocalObjectReconcileStatus.Applied, handle)
                    : new(
                        LocalObjectReconcileStatus.TransientFailure,
                        Handle: handle,
                        Error: "Spawned local object apply could not be checkpointed.");
            }

            if (!ledger.TryRecordCompensationMutation(token, handle))
            {
                return new(
                    LocalObjectReconcileStatus.PermanentFailure,
                    Error: "Uncheckpointed local object ownership could not be retained safely.");
            }

            var released = ReleaseCompensation(
                token,
                handle,
                mutationClaimed: true);
            return released.Succeeded
                ? new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Spawned local object could not be checkpointed and was compensated.")
                : released;
        }
        finally
        {
            ledger.TryEndMutation(token);
        }
    }

    private bool IsVisibleHere(AuthoritativeObjectState desiredState)
    {
        if (visibility is null)
            return true;
        try
        {
            return visibility.IsVisibleHere(desiredState);
        }
        catch
        {
            return false;
        }
    }

    private static LocalObjectReconcileResult Loading(LocalObjectInstanceHandle handle) =>
        new(LocalObjectReconcileStatus.TransientFailure, handle, LoadingMarker);

    private static bool IsLoading(LocalObjectReconcileResult result) =>
        result.Status == LocalObjectReconcileStatus.TransientFailure &&
        ReferenceEquals(result.Error, LoadingMarker);

    /// <summary>
    /// Waits, within <see cref="loadTimeout"/>, for an owned instance to
    /// finish loading. Each poll runs on the game thread inside the exact
    /// desired-generation fence and only for the exact recorded receipt.
    /// Success marks it applied. A failed load or the time running out
    /// releases that exact receipt; a newer generation, lost readiness or
    /// cancellation leaves it owned for the newer work or session cleanup.
    /// </summary>
    private async Task<LocalObjectReconcileResult> AwaitLoadAsync(
        LocalObjectApplyToken token,
        AuthoritativeObjectState desiredState,
        LocalObjectInstanceHandle loading,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + loadTimeout;
        try
        {
            while (true)
            {
                await Task.Delay(LoadPollInterval, cancellationToken).ConfigureAwait(false);
                var timedOut = DateTime.UtcNow >= deadline;
                var polled = await dispatcher.InvokeAsync(
                    () =>
                    {
                        if (!readiness.IsReady)
                            return DependenciesUnavailable(loading);
                        var executed = desiredFence.TryExecuteCurrent(
                            token,
                            () => PollLoadCurrent(token, desiredState, loading, timedOut),
                            out var result);
                        return executed && desiredFence.IsCurrent(token)
                            ? result
                            : new LocalObjectReconcileResult(LocalObjectReconcileStatus.Stale, loading);
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!IsLoading(polled))
                    return polled;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(LocalObjectReconcileStatus.Cancelled, loading);
        }
    }

    private LocalObjectReconcileResult PollLoadCurrent(
        LocalObjectApplyToken token,
        AuthoritativeObjectState desiredState,
        LocalObjectInstanceHandle loading,
        bool timedOut)
    {
        if (!ledger.TryGet(token, out var reservation) || reservation.Handle != loading)
            return new(LocalObjectReconcileStatus.Stale);
        if (ledger.IsApplied(token))
            return new(LocalObjectReconcileStatus.Applied, loading);

        ApplyPortResult applied;
        try
        {
            applied = runtime.ApplyOwned(loading, desiredState);
        }
        catch (Exception exception)
        {
            return ReleaseAfterApplyFault(token, reservation, exception);
        }
        if (applied.Succeeded)
        {
            if (!desiredFence.IsCurrent(token))
                return new(LocalObjectReconcileStatus.Stale, loading);
            return ledger.TryMarkApplied(token, loading)
                ? new(LocalObjectReconcileStatus.Applied, loading)
                : new(
                    LocalObjectReconcileStatus.TransientFailure,
                    loading,
                    "Loaded local object could not be checkpointed.");
        }
        if (applied.Status == ApplyPortStatus.AwaitingPayload && !timedOut)
            return Loading(loading);

        // The load failed or took too long: release this exact instance so a
        // later retry starts from a clean spawn.
        var released = ReleaseForReplacement(token, reservation);
        if (!released.Succeeded)
            return released;
        return new(
            LocalObjectReconcileStatus.TransientFailure,
            Error: timedOut
                ? "Local object did not finish loading in time and was released."
                : applied.Error ?? "Local object failed to load and was released.");
    }

    /// <summary>
    /// The adapter threw while applying to an owned instance. Its state is
    /// unknown, so release that exact receipt now rather than leaving it
    /// owned until disconnect while retries run out.
    /// </summary>
    private LocalObjectReconcileResult ReleaseAfterApplyFault(
        LocalObjectApplyToken token,
        LocalObjectReservation reservation,
        Exception exception)
    {
        // A newer generation inherits the receipt; only the current one
        // decides that the instance is unusable.
        if (!desiredFence.IsCurrent(token))
            return new(LocalObjectReconcileStatus.Stale, reservation.Handle);
        LocalObjectReconcileResult released;
        try
        {
            released = ReleaseForReplacement(token, reservation);
        }
        catch (Exception)
        {
            return new(
                LocalObjectReconcileStatus.TransientFailure,
                reservation.Handle,
                "Local object apply and release both failed.");
        }
        if (!released.Succeeded)
            return released;
        return new(
            LocalObjectReconcileStatus.TransientFailure,
            Error: $"Local object apply failed ({exception.GetType().Name}) and was released.");
    }

    private LocalObjectReconcileResult ReleaseCompensation(
        LocalObjectApplyToken token,
        LocalObjectReservation reservation) =>
        reservation.Compensation is { } expected
            ? ReleaseCompensation(token, expected)
            : new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object compensation receipt is unavailable.");

    private LocalObjectReconcileResult ReleaseCompensation(
        LocalObjectApplyToken token,
        LocalObjectInstanceHandle expected,
        bool mutationClaimed = false)
    {
        var releaseStarted = mutationClaimed
            ? ledger.TryTransitionMutationToCompensationRelease(token, out var claimed)
            : ledger.TryBeginCompensationRelease(token, expected, out claimed);
        if (!releaseStarted)
        {
            return new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object compensation release could not be claimed.");
        }

        try
        {
            if (claimed != expected)
            {
                return new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Local object compensation receipt changed before release.");
            }

            var released = runtime.ReleaseOwned(claimed);
            if (!released.Succeeded)
                return FromPort(released.Status, released.Error);

            if (!ledger.TryCompleteCompensationRelease(token, claimed))
            {
                return new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Released local object compensation could not be checkpointed.");
            }

            return new(LocalObjectReconcileStatus.Applied);
        }
        finally
        {
            ledger.TryEndRelease(token);
        }
    }

    private LocalObjectReconcileResult ReleaseForReplacement(
        LocalObjectApplyToken token,
        LocalObjectReservation reservation)
    {
        if (reservation.Handle is not { } expected ||
            !ledger.TryBeginRelease(token, expected, out var claimed))
        {
            return new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object replacement release could not be claimed.");
        }

        try
        {
            if (claimed != expected)
            {
                return new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Local object replacement receipt changed before release.");
            }

            var released = runtime.ReleaseOwned(claimed);
            if (!released.Succeeded)
                return FromPort(released.Status, released.Error);

            if (!ledger.TryCompleteRelease(token, claimed))
            {
                return new(
                    LocalObjectReconcileStatus.TransientFailure,
                    Error: "Released local object could not be checkpointed.");
            }

            return new(LocalObjectReconcileStatus.Applied);
        }
        finally
        {
            ledger.TryEndRelease(token);
        }
    }

    private static LocalObjectReconcileResult FromReservation(
        LocalObjectReservationStatus status) => status switch
        {
            LocalObjectReservationStatus.CapacityExceeded => new(
                LocalObjectReconcileStatus.CapacityExceeded,
                Error: "Local object ownership capacity is unavailable."),
            LocalObjectReservationStatus.StaleGeneration or
            LocalObjectReservationStatus.SessionConflict => new(
                LocalObjectReconcileStatus.Stale),
            LocalObjectReservationStatus.ReleaseInProgress => new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object release is already in progress."),
            LocalObjectReservationStatus.MutationInProgress => new(
                LocalObjectReconcileStatus.TransientFailure,
                Error: "Local object spawn mutation is already in progress."),
            _ => new(
                LocalObjectReconcileStatus.PermanentFailure,
                Error: $"Local object reservation was rejected: {status}."),
        };

    private static LocalObjectReconcileResult FromPort(
        ApplyPortStatus status,
        string? error) => new(MapPort(status), Error: error);

    private static LocalObjectReconcileResult DependenciesUnavailable(
        LocalObjectInstanceHandle? handle = null) => new(
        LocalObjectReconcileStatus.Unavailable,
        handle,
        "Receiver dependencies are unavailable.");

    private static LocalObjectReconcileStatus MapPort(ApplyPortStatus status) => status switch
    {
        ApplyPortStatus.Unavailable => LocalObjectReconcileStatus.Unavailable,
        ApplyPortStatus.TransientFailure => LocalObjectReconcileStatus.TransientFailure,
        ApplyPortStatus.PermanentFailure => LocalObjectReconcileStatus.PermanentFailure,
        _ => LocalObjectReconcileStatus.PermanentFailure,
    };
}
