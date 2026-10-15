using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Receiver;

internal enum ApplyDomainPhase
{
    /// <summary>Normal operation.</summary>
    Open,
    /// <summary>Releasing every receipt of the domain; nothing new is applied.</summary>
    Cleaning,
    /// <summary>Receipts released; desired objects are re-admitted when the domain is ready.</summary>
    Readmitting,
}

/// <summary>
/// One apply domain (actors, or furniture and lights). A reset releases every
/// receipt the domain holds for the session while the connection, the desired
/// state and the other domain stay untouched, then re-admits the desired
/// objects so they are applied again once the domain is ready. A reset is
/// requested when the domain's host is lost, and for local objects when the
/// player's location changes (a zone change wipes local objects).
/// </summary>
internal sealed class ApplyDomainGate
{
    private readonly object gate = new();
    private ApplyDomainPhase phase = ApplyDomainPhase.Open;
    private long epoch;
    private Queue<RscObjectId>? readmissions;
    // The kinds the current reset releases; null releases every kind.
    private HashSet<RscObjectKind>? scope;

    internal ApplyDomainPhase Phase
    {
        get
        {
            lock (gate)
                return phase;
        }
    }

    /// <summary>Applying is allowed unless the domain is being cleaned.</summary>
    internal bool IsOpen
    {
        get
        {
            lock (gate)
                return phase != ApplyDomainPhase.Cleaning;
        }
    }

    internal int PendingReadmissions
    {
        get
        {
            lock (gate)
                return readmissions?.Count ?? 0;
        }
    }

    /// <summary>
    /// Starts (or restarts) a reset. Any cleanup pass already running is
    /// superseded: its completion cannot start re-admission for an older
    /// request.
    /// </summary>
    internal void RequestReset() => RequestReset(null);

    /// <summary>
    /// Starts (or restarts) a reset that releases only the receipts of
    /// <paramref name="kind"/> (all kinds when null). A reset requested while
    /// another is still cleaning widens that one: nothing it was to release is
    /// skipped. Re-admission always covers every desired object of the
    /// domain; an object whose receipt was kept is already applied.
    /// </summary>
    internal void RequestReset(RscObjectKind? kind)
    {
        lock (gate)
        {
            if (phase == ApplyDomainPhase.Cleaning)
            {
                if (kind is null)
                    scope = null;
                else
                    scope?.Add(kind.Value);
            }
            else
            {
                scope = kind is null ? null : [kind.Value];
            }
            phase = ApplyDomainPhase.Cleaning;
            readmissions = null;
            epoch++;
        }
    }

    internal long CleaningEpoch() => CleaningEpoch(out _);

    /// <summary>The current cleaning epoch and the kinds it releases (null: all).</summary>
    internal long CleaningEpoch(out Func<RscObjectKind, bool>? releases)
    {
        lock (gate)
        {
            if (phase != ApplyDomainPhase.Cleaning)
            {
                releases = null;
                return -1;
            }
            if (scope is null)
            {
                releases = null;
            }
            else
            {
                var kinds = scope.ToArray();
                releases = kind => Array.IndexOf(kinds, kind) >= 0;
            }
            return epoch;
        }
    }

    /// <summary>Moves from cleaning to re-admission only for the exact reset that was cleaned.</summary>
    internal bool TryCompleteCleaning(long cleanedEpoch, IReadOnlyList<RscObjectId> desired)
    {
        lock (gate)
        {
            if (phase != ApplyDomainPhase.Cleaning || epoch != cleanedEpoch)
                return false;
            readmissions = new Queue<RscObjectId>(desired);
            phase = readmissions.Count == 0 ? ApplyDomainPhase.Open : ApplyDomainPhase.Readmitting;
            return true;
        }
    }

    /// <summary>Takes up to <paramref name="maximum"/> objects to re-admit.</summary>
    internal RscObjectId[] TakeReadmissions(int maximum)
    {
        lock (gate)
        {
            if (phase != ApplyDomainPhase.Readmitting || readmissions is null)
                return [];
            var taken = new List<RscObjectId>(Math.Min(maximum, readmissions.Count));
            while (taken.Count < maximum && readmissions.TryDequeue(out var objectId))
                taken.Add(objectId);
            return taken.ToArray();
        }
    }

    /// <summary>Puts back objects that could not be admitted yet (mailbox full).</summary>
    internal void ReturnReadmissions(IEnumerable<RscObjectId> objectIds)
    {
        lock (gate)
        {
            if (phase != ApplyDomainPhase.Readmitting || readmissions is null)
                return;
            var remaining = new Queue<RscObjectId>(objectIds);
            foreach (var queued in readmissions)
                remaining.Enqueue(queued);
            readmissions = remaining;
        }
    }

    internal void CompleteReadmissionIfDrained()
    {
        lock (gate)
        {
            if (phase == ApplyDomainPhase.Readmitting && (readmissions is null || readmissions.Count == 0))
            {
                phase = ApplyDomainPhase.Open;
                readmissions = null;
            }
        }
    }

    /// <summary>Runs <paramref name="operation"/> only while applying is allowed, atomically with the phase.</summary>
    internal bool TryRunIfOpen<T>(Func<T> operation, out T result)
    {
        lock (gate)
        {
            if (phase == ApplyDomainPhase.Cleaning)
            {
                result = default!;
                return false;
            }
            result = operation();
            return true;
        }
    }

    /// <summary>A session ended: nothing is left to clean or re-admit.</summary>
    internal void Clear()
    {
        lock (gate)
        {
            phase = ApplyDomainPhase.Open;
            readmissions = null;
            scope = null;
            epoch++;
        }
    }
}

/// <summary>
/// Apply readiness for one domain: the domain's own readiness, closed while
/// the domain is being reset. Lock order is readiness, then domain gate, then
/// the guarded operation's own locks.
/// </summary>
internal sealed class DomainGatedReadiness :
    IReceiverReadinessPort,
    IReceiverReadinessAdmissionFence
{
    private readonly IReceiverReadinessPort inner;
    private readonly IReceiverReadinessAdmissionFence? innerFence;
    private readonly ApplyDomainGate domain;

    internal DomainGatedReadiness(IReceiverReadinessPort inner, ApplyDomainGate domain)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(domain);
        this.inner = inner;
        innerFence = inner as IReceiverReadinessAdmissionFence;
        this.domain = domain;
    }

    /// <summary>The domain's own readiness, ignoring an in-progress reset.</summary>
    internal bool IsDomainReady
    {
        get
        {
            try
            {
                return inner.IsReady;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsReady => domain.IsOpen && IsDomainReady;

    bool IReceiverReadinessAdmissionFence.TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (innerFence is not null)
        {
            var entered = innerFence.TryExecuteIfReady(
                () => domain.TryRunIfOpen(operation, out var value)
                    ? (Open: true, Value: value)
                    : (Open: false, Value: default(T)!),
                out var guarded);
            result = guarded.Value;
            return entered && guarded.Open;
        }

        if (!IsDomainReady)
        {
            result = default!;
            return false;
        }
        return domain.TryRunIfOpen(operation, out result);
    }
}
