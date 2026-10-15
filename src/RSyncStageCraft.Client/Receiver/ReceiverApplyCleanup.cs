namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ReceiverApplyCleanupResult(
    int OwnershipReleased,
    int OwnershipFailed,
    bool HasRemainingOwnership,
    bool Cancelled)
{
    public bool Busy { get; init; }
    public int PendingCancellationCallbacks { get; init; }
    public int CancellationCallbackFailures { get; init; }

    /// <summary>
    /// A failed release is itself an unresolved cleanup outcome. Consumers must
    /// require a later exact-session pass even when a faulty or racing producer
    /// omits the corresponding remaining-ownership flag.
    /// </summary>
    public bool NeedsRetry =>
        OwnershipFailed != 0 ||
        HasRemainingOwnership ||
        Cancelled ||
        Busy ||
        PendingCancellationCallbacks != 0;
}

/// <summary>
/// Composes receiver invalidation/work cancellation/state removal with
/// plugin-owned external resource release. Core session state is drained first;
/// ownership cleanup then advances in bounded, yielding passes through one fair
/// sweep. Persistent failures are attempted once per invocation and remain for
/// an externally scheduled retry without starving later releasable entries.
/// </summary>
public sealed class ReceiverApplyCleanup
{
    private readonly ReceiverSessionCleanup sessionCleanup;
    private readonly LocalObjectOwnershipCleanup? localObjectCleanup;
    private readonly int maximumOwnershipEntriesPerPass;

    public ReceiverApplyCleanup(
        ReceiverSessionCleanup sessionCleanup,
        int maximumOwnershipEntriesPerPass = 32,
        LocalObjectOwnershipCleanup? localObjectCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(sessionCleanup);
        if (maximumOwnershipEntriesPerPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumOwnershipEntriesPerPass));

        this.sessionCleanup = sessionCleanup;
        this.localObjectCleanup = localObjectCleanup;
        this.maximumOwnershipEntriesPerPass = maximumOwnershipEntriesPerPass;
    }

    public async Task<ReceiverApplyCleanupResult> CleanupAsync(
        ReceiverSessionKey session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsValid)
            return default;

        sessionCleanup.Cleanup(session);
        try
        {
            await sessionCleanup.CleanupCompletion
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(0, 0, HasRemainingOwnership: true, Cancelled: true);
        }

        var released = 0;
        var failed = 0;
        var remainingLocalSweep = localObjectCleanup?.GetSessionEntryCount(session) ?? 0;
        while (remainingLocalSweep > 0)
        {
            var pass = await localObjectCleanup!.CleanupPassAsync(
                session,
                Math.Min(maximumOwnershipEntriesPerPass, remainingLocalSweep),
                cancellationToken).ConfigureAwait(false);
            released += pass.Released;
            failed += pass.Failed;
            remainingLocalSweep -= pass.Released + pass.Failed;

            if (pass.Cancelled)
                return new(released, failed, HasRemaining(session), Cancelled: true);
            if (!pass.HasRemaining || pass.Released + pass.Failed == 0)
                break;

            await Task.Yield();
        }

        return new(
            released,
            failed,
            HasRemaining(session),
            Cancelled: false);
    }

    private bool HasRemaining(ReceiverSessionKey session) =>
        (localObjectCleanup?.GetSessionEntryCount(session) ?? 0) > 0;
}
