namespace RSyncStageCraft.Core.Diagnostics;

internal readonly record struct RuntimeObservation(
    Guid RunId,
    long Sequence,
    int ManagedThreadId,
    int ExternalCallDepth,
    bool Reentrant,
    string Phase,
    string? Detail);

/// <summary>
/// Bounded, process-local evidence for the development bootstrap. It records
/// lifecycle and read-only provider probes, but never stores provider payloads.
/// </summary>
internal sealed class RuntimeObservationTrace
{
    internal const int MaximumEntries = 128;
    private const int MaximumPhaseLength = 64;
    private const int MaximumDetailLength = 256;

    private readonly object gate = new();
    private readonly Queue<RuntimeObservation> entries = new(MaximumEntries);
    private readonly AsyncLocal<int> externalCallDepth = new();
    private readonly Action<RuntimeObservation>? publish;
    private long nextSequence;

    internal RuntimeObservationTrace(Action<RuntimeObservation>? publish = null)
    {
        this.publish = publish;
        RunId = Guid.NewGuid();
    }

    internal Guid RunId { get; }

    internal RuntimeObservation Record(string phase, string? detail = null) =>
        RecordAtDepth(phase, detail, externalCallDepth.Value, callback: false);

    internal RuntimeObservation RecordCallback(string phase, string? detail = null)
    {
        var depth = externalCallDepth.Value;
        return RecordAtDepth(phase, detail, depth, callback: true);
    }

    internal IDisposable BeginExternalCall(string phase, string? detail = null)
    {
        var depth = checked(externalCallDepth.Value + 1);
        externalCallDepth.Value = depth;
        RecordAtDepth($"{phase}.begin", detail, depth, callback: false);
        return new ExternalCallScope(this, phase, detail);
    }

    internal IReadOnlyList<RuntimeObservation> Snapshot()
    {
        lock (gate)
            return entries.ToArray();
    }

    private RuntimeObservation RecordAtDepth(
        string phase,
        string? detail,
        int depth,
        bool callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var observation = new RuntimeObservation(
            RunId,
            Interlocked.Increment(ref nextSequence),
            Environment.CurrentManagedThreadId,
            depth,
            depth > 1 || callback && depth > 0,
            Bound(phase, MaximumPhaseLength)!,
            Bound(detail, MaximumDetailLength));

        lock (gate)
        {
            if (entries.Count == MaximumEntries)
                entries.Dequeue();
            entries.Enqueue(observation);
        }

        try
        {
            publish?.Invoke(observation);
        }
        catch
        {
            // Diagnostic publication is never lifecycle authority.
        }

        return observation;
    }

    private void CompleteExternalCall(string phase, string? detail)
    {
        var depth = externalCallDepth.Value;
        try
        {
            RecordAtDepth($"{phase}.end", detail, depth, callback: false);
        }
        finally
        {
            externalCallDepth.Value = Math.Max(0, depth - 1);
        }
    }

    private static string? Bound(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength];

    private sealed class ExternalCallScope(
        RuntimeObservationTrace owner,
        string phase,
        string? detail) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            owner.CompleteExternalCall(phase, detail);
        }
    }
}
