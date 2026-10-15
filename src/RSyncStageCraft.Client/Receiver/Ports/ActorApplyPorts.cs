using System.Collections.ObjectModel;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver.Ports;

public enum ApplyPortStatus
{
    Succeeded,
    Unavailable,
    AwaitingPayload,
    TransientFailure,
    PermanentFailure,
}

public readonly record struct ApplyPortResult(
    ApplyPortStatus Status,
    string? Error = null)
{
    public bool Succeeded => Status == ApplyPortStatus.Succeeded;
}

public readonly record struct ApplyPortResult<T>(
    ApplyPortStatus Status,
    T? Value = default,
    string? Error = null)
{
    public bool Succeeded => Status == ApplyPortStatus.Succeeded;
}

public interface IGameThreadDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}

public interface IReceiverReadinessPort
{
    bool IsReady { get; }
}

/// <summary>
/// Assembly-owned admission fence used by the shared readiness authority to
/// make short receiver/transfer state-admission transactions indivisible from
/// a readiness transition. External readiness ports remain signature-neutral
/// and need not implement this capability. External I/O or adapter callbacks
/// must never execute inside the transaction.
/// </summary>
internal interface IReceiverReadinessAdmissionFence
{
    bool TryExecuteIfReady<T>(Func<T> operation, out T result);
}
