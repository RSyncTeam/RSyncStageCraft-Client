using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// Bounded, coalescing mailbox for furniture/light reconciliation. One object
/// identity occupies at most one queued or active slot; a newer generation is
/// retained as pending work while the older attempt completes.
/// </summary>
public sealed class LocalObjectApplyCoordinator
{
    private readonly record struct WorkKey(
        ReceiverSessionKey Session,
        RscObjectId ObjectId);

    private sealed class ActiveAttempt
    {
        public LocalObjectApplyToken Token { get; set; }
        public LocalObjectApplyToken? Pending { get; set; }
    }

    private readonly object gate = new();
    private readonly DesiredLocalObjectStore desiredStore;
    private readonly int maximumTrackedObjects;
    private readonly LinkedList<WorkKey> queue = new();
    private readonly Dictionary<WorkKey, LinkedListNode<WorkKey>> queued = new();
    private readonly Dictionary<WorkKey, ActiveAttempt> active = new();
    private readonly Dictionary<ReceiverSessionKey, HashSet<WorkKey>> sessionWork = new();

    internal Action? BeforeStaleReservationRemoval { get; set; }

    public LocalObjectApplyCoordinator(
        DesiredLocalObjectStore desiredStore,
        int maximumTrackedObjects)
    {
        ArgumentNullException.ThrowIfNull(desiredStore);
        if (maximumTrackedObjects <= 0 ||
            maximumTrackedObjects > ProtocolLimits.MaximumObjectsPerSnapshot)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedObjects));
        }

        this.desiredStore = desiredStore;
        this.maximumTrackedObjects = maximumTrackedObjects;
    }

    public int TrackedCount
    {
        get
        {
            lock (gate)
                return queued.Count + active.Count;
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (gate)
                return queued.Count;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (gate)
                return active.Count;
        }
    }

    public bool TryAdmit(ReceiverSessionKey session, RscObjectId objectId)
    {
        if (!TryGetCurrentWork(session, objectId, out var work))
            return false;

        var key = new WorkKey(session, objectId);
        var executed = desiredStore.TryExecuteCurrent(
            work.Token,
            () => TryAdmitUnsafe(key, work.Token),
            out var admitted);
        return executed && admitted;
    }

    public bool TryBeginNext(out LocalObjectApplyWorkItem workItem)
    {
        var remainingCandidates = QueuedCount;
        while (remainingCandidates-- > 0 && TryReserveNext(out var key))
        {
            if (!TryGetCurrentWork(key.Session, key.ObjectId, out var current))
            {
                BeforeStaleReservationRemoval?.Invoke();
                RemoveStaleActive(key);
                continue;
            }

            lock (gate)
            {
                if (!active.TryGetValue(key, out var attempt))
                    continue;

                attempt.Token = current.Token;
                if (attempt.Pending == current.Token)
                    attempt.Pending = null;
                workItem = current;
                return true;
            }
        }

        workItem = default;
        return false;
    }

    public bool Complete(LocalObjectApplyToken token, bool retryCurrent = false)
    {
        if (!token.IsValid)
            return false;

        var key = new WorkKey(token.Session, token.ObjectId);
        lock (gate)
        {
            if (!active.TryGetValue(key, out var attempt) || attempt.Token != token)
                return false;

            active.Remove(key);
            if (retryCurrent || attempt.Pending is not null)
            {
                var node = queue.AddLast(key);
                queued.Add(key, node);
            }
            else
            {
                RemoveSessionWorkUnsafe(key);
            }
        }

        return true;
    }

    public int CancelSession(ReceiverSessionKey session, int maximumEntries)
    {
        if (!session.IsValid)
            return 0;
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        lock (gate)
        {
            if (!sessionWork.TryGetValue(session, out var workKeys))
                return 0;

            var selected = workKeys.Take(maximumEntries).ToArray();
            foreach (var key in selected)
            {
                if (queued.Remove(key, out var node))
                    queue.Remove(node);

                active.Remove(key);
                workKeys.Remove(key);
            }

            if (workKeys.Count == 0)
                sessionWork.Remove(session);

            return selected.Length;
        }
    }

    public bool HasSessionWork(ReceiverSessionKey session)
    {
        lock (gate)
            return sessionWork.TryGetValue(session, out var workKeys) && workKeys.Count > 0;
    }

    private bool TryAdmitUnsafe(WorkKey key, LocalObjectApplyToken token)
    {
        lock (gate)
        {
            if (queued.ContainsKey(key))
                return true;

            if (active.TryGetValue(key, out var attempt))
            {
                if (!attempt.Token.IsValid || attempt.Token != token)
                    attempt.Pending = token;

                return true;
            }

            if (queued.Count + active.Count >= maximumTrackedObjects)
                return false;

            var node = queue.AddLast(key);
            queued.Add(key, node);
            AddSessionWorkUnsafe(key);
            return true;
        }
    }

    private bool TryReserveNext(out WorkKey key)
    {
        lock (gate)
        {
            if (queue.First is null)
            {
                key = default;
                return false;
            }

            var node = queue.First;
            key = node.Value;
            queue.RemoveFirst();
            queued.Remove(key);
            active.Add(key, new ActiveAttempt());
            return true;
        }
    }

    private void RemoveStaleActive(WorkKey key)
    {
        lock (gate)
        {
            if (!active.Remove(key, out var attempt))
                return;

            if (attempt.Pending is not null)
            {
                var node = queue.AddLast(key);
                queued.Add(key, node);
            }
            else
            {
                RemoveSessionWorkUnsafe(key);
            }
        }
    }

    private bool TryGetCurrentWork(
        ReceiverSessionKey session,
        RscObjectId objectId,
        out LocalObjectApplyWorkItem work)
    {
        if (desiredStore.TryGetCurrent(session, objectId, out var desired))
        {
            work = new LocalObjectApplyWorkItem(desired);
            return true;
        }

        if (desiredStore.TryGetRemoval(session, objectId, out var removal) &&
            !removal.Completed)
        {
            work = new LocalObjectApplyWorkItem(removal);
            return true;
        }

        work = default;
        return false;
    }

    private void AddSessionWorkUnsafe(WorkKey key)
    {
        if (!sessionWork.TryGetValue(key.Session, out var workKeys))
        {
            workKeys = new HashSet<WorkKey>();
            sessionWork.Add(key.Session, workKeys);
        }

        workKeys.Add(key);
    }

    private void RemoveSessionWorkUnsafe(WorkKey key)
    {
        if (!sessionWork.TryGetValue(key.Session, out var workKeys))
            return;

        workKeys.Remove(key);
        if (workKeys.Count == 0)
            sessionWork.Remove(key.Session);
    }
}
