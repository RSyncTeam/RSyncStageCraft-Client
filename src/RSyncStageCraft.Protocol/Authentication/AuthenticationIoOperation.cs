namespace RSyncStageCraft.Core.Authentication;

public enum AuthenticationIoCancellation { InactiveConnection, Cancelled, TimedOut }

// One per retained handshake I/O operation. CancelAsync marks cancellation
// synchronously while dispatching callbacks asynchronously. Its first task must
// remain owned: later CancelAsync calls alone would not wait for that first drain.
public sealed class AuthenticationIoOperation
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private Task callbacks = Task.CompletedTask;
    private bool cancelling;
    private bool sealedForDrain;
    public CancellationToken Token { get; }
    public AuthenticationIoCancellation CancellationStatus { get; private set; }

    public AuthenticationIoOperation() => Token = cancellation.Token;

    public void Cancel(AuthenticationIoCancellation status)
    {
        lock (gate)
        {
            if (sealedForDrain || cancelling) return;
            cancelling = true;
            CancellationStatus = status;
            callbacks = cancellation.CancelAsync();
        }
    }

    public async Task<bool> DrainAsync()
    {
        Task pending;
        lock (gate)
        {
            sealedForDrain = true;
            pending = callbacks;
        }
        var failed = false;
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { failed = true; }
        // Do not keep a completed fault task (whose exception may contain
        // provider data) reachable through this retired operation.
        pending = Task.CompletedTask;
        lock (gate) callbacks = Task.CompletedTask;
        cancellation.Dispose();
        return failed;
    }
}
