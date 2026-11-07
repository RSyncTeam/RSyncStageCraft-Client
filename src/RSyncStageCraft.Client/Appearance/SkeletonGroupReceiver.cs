using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// An opaque key for one actor slot, issued by the actor host for an actor it
/// has admitted. The receiver never sees an object index, name, address or
/// local path; it only passes the key back to the host's port.
/// </summary>
public readonly record struct ActorSlotKey(Guid Value)
{
    public static ActorSlotKey New() => new(Guid.NewGuid());

    public override string ToString() => $"slot {Value:N}";
}

/// <summary>
/// What only the actor host can do for a received skeleton group. Every call
/// is for one slot; the host re-proves ownership of that slot's object before
/// any side effect, and answers false (never throws for an expected failure)
/// when it cannot act. Nothing in the plugin implements this yet: receiving a
/// skeleton group is not wired to any actor.
/// </summary>
public interface ISkeletonGroupAssignmentPort
{
    /// <summary>Assigns the group's members (the held cache files) to the slot's own, still hidden, object.</summary>
    Task<bool> AssignAsync(ActorSlotKey slot, SkeletonResourceGroup group, AppearanceBundleHold hold, CancellationToken cancellationToken);

    /// <summary>Redraws the slot's hidden object and confirms every member resolved for it.</summary>
    Task<bool> RedrawAndConfirmAsync(ActorSlotKey slot, SkeletonResourceGroup group, CancellationToken cancellationToken);

    /// <summary>Removes what <see cref="AssignAsync"/> made: the assignment, the temporary mod and its collection.</summary>
    Task<bool> RemoveAsync(ActorSlotKey slot, CancellationToken cancellationToken);

    /// <summary>
    /// True only once nothing can still name the slot's member files: its object
    /// was deleted, or redrawn without the redirects and that was confirmed.
    /// </summary>
    Task<bool> NothingNamesFilesAsync(ActorSlotKey slot, CancellationToken cancellationToken);
}

/// <summary>What <see cref="SkeletonGroupReceiver"/> reports.</summary>
public enum SkeletonGroupReceiveStatus
{
    /// <summary>The skeleton step is confirmed. The actor is still hidden; the host continues its own order before showing it.</summary>
    Ready,

    /// <summary>A format-1 bundle: there is no skeleton group. Nothing is held and the host was not called. Not a failure.</summary>
    NoGroup,

    /// <summary>The group was refused or a step failed: the actor stays hidden. Nothing is held for the slot.</summary>
    StayHidden,

    /// <summary>The slot already has a receive in flight, a hold, or a hold kept after a failed release. Nothing was requested.</summary>
    SlotOccupied,

    /// <summary>A receive or release for the slot is in flight. Nothing was done.</summary>
    SlotBusy,

    /// <summary>The slot has nothing to release.</summary>
    NotHeld,

    /// <summary>The slot's hold was released after nothing named its files any more.</summary>
    Released,

    /// <summary>
    /// A clean-up step failed or timed out, so the files may still be named:
    /// the hold is kept (never disposed here) and the slot stays occupied
    /// until a later release succeeds.
    /// </summary>
    ReleaseFailed,
}

/// <summary>Which step of a receive or release decided its result.</summary>
public enum SkeletonGroupReceiveStep
{
    None,
    Admission,
    Hold,
    Assign,
    Redraw,
    Remove,
    NothingNamesFiles,
}

/// <summary>
/// A receive or release result. <see cref="Transfer"/> is set when the hold
/// decided it. Only typed values: no local path, object name or index.
/// </summary>
public sealed record SkeletonGroupReceiveResult(
    SkeletonGroupReceiveStatus Status,
    SkeletonGroupReceiveStep Step = SkeletonGroupReceiveStep.None,
    AppearanceTransferResult? Transfer = null,
    bool Cancelled = false);

/// <summary>
/// Turns one received bundle into one held, validated skeleton group for one
/// actor slot, in a fixed, fail-closed order, and releases it in the order the
/// hold's contract requires (Debug receiver wiring, step W1).
/// <list type="number">
/// <item><b>Reserve.</b> The slot is reserved before anything is requested, so a
/// second receive for it cannot acquire another hold while the first is in
/// flight (<see cref="SkeletonGroupReceiveStatus.SlotOccupied"/>).</item>
/// <item><b>Hold</b> through <see cref="AppearanceTransferClient.HoldBundleAsync"/>
/// with this receiver's options, which it never changes. A format-1 bundle is
/// <see cref="SkeletonGroupReceiveStatus.NoGroup"/>; any refusal is
/// <see cref="SkeletonGroupReceiveStatus.StayHidden"/>.</item>
/// <item><b>Assign, then redraw and confirm,</b> through the host's port. Only
/// then is the hold recorded for the slot and
/// <see cref="SkeletonGroupReceiveStatus.Ready"/> returned; the actor is still
/// hidden.</item>
/// </list>
/// <para><b>Release</b> is <see cref="ISkeletonGroupAssignmentPort.RemoveAsync"/>,
/// then <see cref="ISkeletonGroupAssignmentPort.NothingNamesFilesAsync"/>, and
/// only when both succeed is the hold disposed: last, after nothing names its
/// files. A failed assignment releases with <c>RemoveAsync</c> alone (nothing
/// was redrawn); a failed or cancelled redraw uses the full order. When a
/// clean-up step fails or times out the hold is kept and
/// <see cref="SkeletonGroupReceiveStatus.ReleaseFailed"/> returned: files that
/// may still be named are never released here.</para>
/// <para><b>Clean-up</b> runs under this receiver's own bounded token, never the
/// caller's, so a cancelled receive still cleans up.</para>
/// <para><b>Not here:</b> replacement (a second appearance for an occupied slot is
/// refused), showing the actor, choosing the race (the host passes the
/// puppet's own race code), and the actor limit, which the host enforces. The
/// receiver keeps only a defensive bound at
/// <see cref="ActorCapacityPolicy.ActiveMaximumActorsPerScene"/> slots.</para>
/// </summary>
public sealed class SkeletonGroupReceiver : IAsyncDisposable
{
    /// <summary>The default bound on each clean-up step.</summary>
    public static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(30);

    private readonly AppearanceTransferClient client;
    private readonly ISkeletonGroupAssignmentPort port;
    private readonly AppearanceReceiveOptions options;
    private readonly TimeSpan cleanupTimeout;
    private readonly object gate = new();
    private readonly Dictionary<ActorSlotKey, Slot> slots = [];
    private bool disposed;

    public SkeletonGroupReceiver(
        AppearanceTransferClient client,
        ISkeletonGroupAssignmentPort port,
        AppearanceReceiveOptions options,
        TimeSpan? cleanupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(options);
        var timeout = cleanupTimeout ?? DefaultCleanupTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        this.client = client;
        this.port = port;
        this.options = options;
        this.cleanupTimeout = timeout;
    }

    /// <summary>The options this receiver holds with; set by its creator and never changed.</summary>
    public AppearanceReceiveOptions Options => options;

    /// <summary>Slots in any state: reserved, held, releasing, or kept after a failed release.</summary>
    public int SlotCount
    {
        get
        {
            lock (gate)
                return slots.Count;
        }
    }

    /// <summary>Slots whose hold was kept because a clean-up step failed (residue to report).</summary>
    public int ReleaseFailedCount
    {
        get
        {
            lock (gate)
                return slots.Values.Count(slot => slot.State == SlotState.ReleaseFailed);
        }
    }

    /// <summary>True only while the slot's hold is recorded and intact; a redraw then needs no request and no new lease.</summary>
    public bool CanRedraw(ActorSlotKey slot)
    {
        lock (gate)
            return slots.TryGetValue(slot, out var entry) && entry.State == SlotState.Held && entry.Hold is { IsIntact: true };
    }

    /// <summary>
    /// Receives <paramref name="bundleId"/> for <paramref name="slot"/>, whose
    /// object is hidden and has <paramref name="puppetRaceCode"/> as its own
    /// race code (null when it could not be read: refused before any blob request).
    /// </summary>
    public async Task<SkeletonGroupReceiveResult> ReceiveAsync(
        ActorSlotKey slot, ContentHash bundleId, ushort? puppetRaceCode, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (disposed)
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Admission);
            if (slots.ContainsKey(slot))
                return new(SkeletonGroupReceiveStatus.SlotOccupied, SkeletonGroupReceiveStep.Admission);
            if (slots.Count >= ActorCapacityPolicy.ActiveMaximumActorsPerScene)
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Admission);
            slots[slot] = new Slot(SlotState.Reserved, null);
        }

        var recorded = false;
        try
        {
            AppearanceBundleHoldResult held;
            try
            {
                held = await client.HoldBundleAsync(bundleId, puppetRaceCode, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Hold,
                    new AppearanceTransferResult(AppearanceTransferStatus.Cancelled), Cancelled: true);
            }
            if (!held.Result.Succeeded)
            {
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Hold, held.Result,
                    Cancelled: held.Result.Status == AppearanceTransferStatus.Cancelled);
            }
            if (held.Hold is not { } hold)
                return new(SkeletonGroupReceiveStatus.NoGroup, SkeletonGroupReceiveStep.Hold, held.Result);

            // Disposed while the hold was requested: nothing was assigned yet, so
            // the hold can be disposed at once.
            bool closing;
            lock (gate)
                closing = disposed;
            if (closing)
            {
                hold.Dispose();
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Admission);
            }

            // From here the hold exists: every exit either records it for the slot,
            // disposes it after a successful clean-up, or keeps it (ReleaseFailed).
            var (assigned, assignCancelled) = await StepAsync(
                token => port.AssignAsync(slot, hold.Group, hold, token), cancellationToken).ConfigureAwait(false);
            if (!assigned)
            {
                // Nothing was redrawn, so removing the assignment is enough. If that
                // fails or times out, the files may still be named: keep the hold.
                if (!await CleanupAsync(token => port.RemoveAsync(slot, token)).ConfigureAwait(false))
                {
                    recorded = Keep(slot, hold);
                    return new(SkeletonGroupReceiveStatus.ReleaseFailed, SkeletonGroupReceiveStep.Remove, Cancelled: assignCancelled);
                }
                hold.Dispose();
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Assign, Cancelled: assignCancelled);
            }

            var (confirmed, redrawCancelled) = await StepAsync(
                token => port.RedrawAndConfirmAsync(slot, hold.Group, token), cancellationToken).ConfigureAwait(false);
            // The hold can be lost meanwhile only if its client was disposed: a failed
            // step. The check and the recording share one lock, so a receiver being
            // disposed either sees the held slot or this receive sees it disposed.
            bool stop;
            lock (gate)
            {
                stop = !confirmed || !hold.IsIntact || disposed;
                if (!stop)
                {
                    slots[slot] = new Slot(SlotState.Held, hold);
                    recorded = true;
                }
            }
            if (stop)
            {
                // The redraw may have loaded the files: the full release order.
                var failed = await ReleaseOrderAsync(slot).ConfigureAwait(false);
                if (failed is { } step)
                {
                    recorded = Keep(slot, hold);
                    return new(SkeletonGroupReceiveStatus.ReleaseFailed, step, Cancelled: redrawCancelled);
                }
                hold.Dispose();
                return new(SkeletonGroupReceiveStatus.StayHidden, SkeletonGroupReceiveStep.Redraw, Cancelled: redrawCancelled);
            }

            return new(SkeletonGroupReceiveStatus.Ready, SkeletonGroupReceiveStep.Redraw, held.Result);
        }
        finally
        {
            if (!recorded)
            {
                lock (gate)
                    slots.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Releases the slot's hold in the contract's order: remove, then wait until
    /// nothing names the files, then dispose. A slot kept after a failed release
    /// is tried again in the same order.
    /// </summary>
    public async Task<SkeletonGroupReceiveResult> ReleaseAsync(ActorSlotKey slot)
    {
        AppearanceBundleHold hold;
        lock (gate)
        {
            if (!slots.TryGetValue(slot, out var entry))
                return new(SkeletonGroupReceiveStatus.NotHeld);
            if (entry.State is SlotState.Reserved or SlotState.Releasing || entry.Hold is null)
                return new(SkeletonGroupReceiveStatus.SlotBusy);
            hold = entry.Hold;
            slots[slot] = new Slot(SlotState.Releasing, hold);
        }

        var failed = await ReleaseOrderAsync(slot).ConfigureAwait(false);
        if (failed is { } step)
        {
            Keep(slot, hold);
            return new(SkeletonGroupReceiveStatus.ReleaseFailed, step);
        }
        hold.Dispose();
        lock (gate)
            slots.Remove(slot);
        return new(SkeletonGroupReceiveStatus.Released);
    }

    /// <summary>
    /// Releases every held slot in the release order and refuses new receives. A
    /// hold whose release fails is kept, not disposed here (its client disposes
    /// it when the client itself is disposed): that is residue, which
    /// <see cref="ReleaseFailedCount"/> reports. A receive still in flight finishes
    /// on its own and then releases instead of recording its hold; this method
    /// does not wait for it. The host must therefore await every receive and
    /// release it started before it disposes the transfer client, whose
    /// disposal releases any hold still open.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ActorSlotKey[] held;
        lock (gate)
        {
            disposed = true;
            held = slots.Where(entry => entry.Value.State is SlotState.Held or SlotState.ReleaseFailed)
                .Select(entry => entry.Key)
                .ToArray();
        }
        foreach (var slot in held)
            await ReleaseAsync(slot).ConfigureAwait(false);
    }

    /// <summary>Runs <c>RemoveAsync</c>, then <c>NothingNamesFilesAsync</c>; returns the step that failed, or null.</summary>
    private async Task<SkeletonGroupReceiveStep?> ReleaseOrderAsync(ActorSlotKey slot)
    {
        if (!await CleanupAsync(token => port.RemoveAsync(slot, token)).ConfigureAwait(false))
            return SkeletonGroupReceiveStep.Remove;
        if (!await CleanupAsync(token => port.NothingNamesFilesAsync(slot, token)).ConfigureAwait(false))
            return SkeletonGroupReceiveStep.NothingNamesFiles;
        return null;
    }

    private bool Keep(ActorSlotKey slot, AppearanceBundleHold hold)
    {
        lock (gate)
            slots[slot] = new Slot(SlotState.ReleaseFailed, hold);
        return true;
    }

    /// <summary>
    /// One forward step under the caller's token. Returns (succeeded, cancelled);
    /// an exception or a cancellation is a failed step, never an escape that
    /// would skip the clean-up.
    /// </summary>
    private static async Task<(bool Succeeded, bool Cancelled)> StepAsync(
        Func<CancellationToken, Task<bool>> step, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return (false, true);
        try
        {
            var succeeded = await step(cancellationToken).ConfigureAwait(false);
            return (succeeded && !cancellationToken.IsCancellationRequested, cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (false, cancellationToken.IsCancellationRequested);
        }
    }

    /// <summary>
    /// One clean-up step under this receiver's own bounded token, never the
    /// caller's. A false answer, an exception, a cancellation or a timeout is a
    /// failure; a step that ignores its token still cannot hold the release
    /// past the bound.
    /// </summary>
    private async Task<bool> CleanupAsync(Func<CancellationToken, Task<bool>> step)
    {
        using var timeout = new CancellationTokenSource(cleanupTimeout);
        Task<bool>? run = null;
        try
        {
            run = step(timeout.Token);
            return await run.WaitAsync(cleanupTimeout).ConfigureAwait(false) && !timeout.IsCancellationRequested;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Observe a late fault of a step that outlived its bound.
            _ = run?.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return false;
        }
    }

    private enum SlotState
    {
        Reserved,
        Held,
        Releasing,
        ReleaseFailed,
    }

    private sealed record Slot(SlotState State, AppearanceBundleHold? Hold);
}
