using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// The game-thread dispatcher, plus an unload mode. Dalamud unloads the
/// plugin on the framework thread and that thread then waits for the
/// receiver's shutdown, so work queued for it could not run until the wait
/// times out. On unload, after the furniture host has freed everything and
/// closed (it then touches no game object), the remaining cleanup runs inline.
/// </summary>
internal sealed class UnloadAwareDispatcher(IGameThreadDispatcher inner) : IGameThreadDispatcher
{
    private volatile bool unloading;

    internal bool IsUnloading => unloading;

    /// <summary>Only after every game-touching host has shut down.</summary>
    internal void RunInline() => unloading = true;

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!unloading)
            return inner.InvokeAsync(operation, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);
        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }
}
