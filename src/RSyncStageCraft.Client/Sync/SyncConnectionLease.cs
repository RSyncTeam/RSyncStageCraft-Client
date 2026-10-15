using System.Runtime.CompilerServices;

namespace RSyncStageCraft.Core.Sync;

/// <summary>
/// Identifies one connection epoch. Publicly constructed values remain useful
/// as signature-neutral storage metadata, but only the issuing
/// <see cref="SyncConnectionLock"/> can create a lease that authorizes its
/// connection-scoped operations.
/// </summary>
public readonly struct SyncConnectionLease : IEquatable<SyncConnectionLease>
{
    private readonly object? issuer;

    public SyncConnectionLease(long epoch)
    {
        Epoch = epoch;
        issuer = null;
    }

    internal SyncConnectionLease(long epoch, object issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        Epoch = epoch;
        this.issuer = issuer;
    }

    public long Epoch { get; }

    public bool IsValid => Epoch > 0;

    public void Deconstruct(out long epoch) => epoch = Epoch;

    internal bool IsIssuedBy(object candidate) =>
        ReferenceEquals(issuer, candidate);

    public bool Equals(SyncConnectionLease other) =>
        Epoch == other.Epoch && ReferenceEquals(issuer, other.issuer);

    public override bool Equals(object? obj) =>
        obj is SyncConnectionLease other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            Epoch,
            issuer is null ? 0 : RuntimeHelpers.GetHashCode(issuer));

    public static bool operator ==(
        SyncConnectionLease left,
        SyncConnectionLease right) => left.Equals(right);

    public static bool operator !=(
        SyncConnectionLease left,
        SyncConnectionLease right) => !left.Equals(right);
}

public sealed class SyncConnectionLock
{
    private readonly object gate = new();
    private readonly object leaseIssuer = new();
    private long epoch;
    private bool connected;
    private bool hasIssuedLease;
    private CancellationTokenSource? connectionCancellation;
    private ConnectionCancellationState? retiredCancellation;
    private int cancellationCallbackFailures;

    public SyncConnectionLock()
    {
    }

    internal SyncConnectionLock(long initialEpoch)
    {
        if (initialEpoch < 0)
            throw new ArgumentOutOfRangeException(nameof(initialEpoch));

        epoch = initialEpoch;
    }

    public SyncConnectionLease CurrentLease
    {
        get
        {
            lock (gate)
                return connected ? IssueLeaseUnsafe() : default;
        }
    }

    public SyncConnectionLease Acquire()
    {
        if (!TryAcquire(out var lease, out var cancellationPending))
        {
            throw new InvalidOperationException(cancellationPending
                ? "Prior connection cancellation callbacks are still draining."
                : "Connection epoch space is exhausted.");
        }

        return lease;
    }

    /// <summary>
    /// Reports the bounded number of retired connection epochs whose
    /// cancellation callbacks have not finished. Only one epoch can be
    /// retiring because acquisition remains fenced until its drain completes.
    /// </summary>
    public int PendingCancellationCallbacks
    {
        get
        {
            lock (gate)
                return retiredCancellation is null ? 0 : 1;
        }
    }

    public int CancellationCallbackFailures
    {
        get
        {
            lock (gate)
                return cancellationCallbackFailures;
        }
    }

    public bool TryAcquire(out SyncConnectionLease lease)
        => TryAcquire(out lease, out _);

    public bool TryAcquire(
        out SyncConnectionLease lease,
        out bool cancellationPending)
    {
        lock (gate)
            return TryAcquireUnsafe(
                allowExisting: true,
                out lease,
                out cancellationPending,
                out _);
    }

    // Session owners that must create, rather than borrow, an epoch use this
    // boundary. A live lease held by another owner is a conflict, not an
    // idempotent acquisition result.
    internal bool TryAcquireNew(
        out SyncConnectionLease lease,
        out bool cancellationPending,
        out bool connectionInUse)
    {
        lock (gate)
            return TryAcquireUnsafe(
                allowExisting: false,
                out lease,
                out cancellationPending,
                out connectionInUse);
    }

    private bool TryAcquireUnsafe(
        bool allowExisting,
        out SyncConnectionLease lease,
        out bool cancellationPending,
        out bool connectionInUse)
    {
        cancellationPending = false;
        connectionInUse = false;
        if (connected)
        {
            lease = allowExisting ? IssueLeaseUnsafe() : default;
            connectionInUse = !allowExisting;
            return allowExisting;
        }

        if (retiredCancellation is not null)
        {
            cancellationPending = true;
            lease = default;
            return false;
        }

        if (epoch == long.MaxValue)
        {
            lease = default;
            return false;
        }

        epoch++;
        connectionCancellation = new CancellationTokenSource();
        hasIssuedLease = true;
        connected = true;
        lease = IssueLeaseUnsafe();
        return true;
    }

    public void Invalidate()
    {
        ConnectionCancellationState? cancellation;
        lock (gate)
        {
            if (!connected)
                return;

            cancellation = RetireCurrentUnsafe();
        }

        StartConnectionCancellation(cancellation);
    }

    public bool Invalidate(SyncConnectionLease lease)
    {
        ConnectionCancellationState? cancellation;
        lock (gate)
        {
            if (!IsCurrentUnsafe(lease))
                return false;

            cancellation = RetireCurrentUnsafe();
        }

        StartConnectionCancellation(cancellation);
        return true;
    }

    public bool HasPendingCancellation(SyncConnectionLease lease)
    {
        lock (gate)
        {
            return lease.IsValid &&
                retiredCancellation?.Lease == lease;
        }
    }

    public bool TryGetCancellationToken(
        SyncConnectionLease lease,
        out CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!IsCurrentUnsafe(lease) || connectionCancellation is null)
            {
                cancellationToken = default;
                return false;
            }

            cancellationToken = connectionCancellation.Token;
            return true;
        }
    }

    public bool IsCurrent(SyncConnectionLease lease)
    {
        lock (gate)
            return IsCurrentUnsafe(lease);
    }

    /// <summary>
    /// Recovers only this lock's latest issued lease for ordered cleanup after
    /// its public current-lease view has been revoked. A numeric epoch alone is
    /// never promoted into connection authority.
    /// </summary>
    internal bool TryGetIssuedLease(
        long expectedEpoch,
        out SyncConnectionLease lease)
    {
        lock (gate)
        {
            if (connected ||
                !hasIssuedLease ||
                expectedEpoch <= 0 ||
                expectedEpoch != epoch)
            {
                lease = default;
                return false;
            }

            lease = IssueLeaseUnsafe();
            return true;
        }
    }

    // Exact mutation callbacks are assembly-owned. Exposing this as a public
    // extension point would let arbitrary host code hold connection authority
    // and delay invalidation for an unbounded duration.
    internal bool TryExecute<T>(SyncConnectionLease lease, Func<T> operation, out T result)
    {
        lock (gate)
        {
            if (!IsCurrentUnsafe(lease))
            {
                result = default!;
                return false;
            }

            result = operation();
            return true;
        }
    }

    // Only bounded, synchronous internal state cleanup belongs here. In
    // particular, dependency callbacks and asynchronous drain waits must stay
    // outside this boundary, just as they do for connection invalidation.
    internal bool TryExecuteInvalidated(SyncConnectionLease lease, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            if (connected ||
                !lease.IsValid ||
                lease.Epoch != epoch ||
                !lease.IsIssuedBy(leaseIssuer))
            {
                return false;
            }

            operation();
            return true;
        }
    }

    private bool IsCurrentUnsafe(SyncConnectionLease lease)
        => connected &&
            lease.IsValid &&
            lease.Epoch == epoch &&
            lease.IsIssuedBy(leaseIssuer);

    private SyncConnectionLease IssueLeaseUnsafe() =>
        new(epoch, leaseIssuer);

    private ConnectionCancellationState? RetireCurrentUnsafe()
    {
        connected = false;
        var cancellation = connectionCancellation;
        connectionCancellation = null;
        if (cancellation is null)
            return null;

        if (retiredCancellation is not null)
        {
            cancellation.Dispose();
            throw new InvalidOperationException(
                "A prior connection cancellation is still draining.");
        }

        var state = new ConnectionCancellationState(
            this,
            IssueLeaseUnsafe(),
            cancellation);
        retiredCancellation = state;
        return state;
    }

    private void StartConnectionCancellation(ConnectionCancellationState? state)
    {
        if (state is null)
            return;

        Task task;
        try
        {
            task = state.Cancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            task = Task.FromException(exception);
        }

        state.Task = task;
        if (task.IsCompleted)
        {
            CompleteConnectionCancellation(state);
            return;
        }

        _ = task.ContinueWith(
            static (_, callbackState) =>
            {
                var cancellation = (ConnectionCancellationState)callbackState!;
                cancellation.Owner.CompleteConnectionCancellation(cancellation);
            },
            state,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteConnectionCancellation(ConnectionCancellationState state)
    {
        _ = state.Task?.Exception;
        lock (gate)
        {
            if (state.CompletionObserved)
                return;

            state.CompletionObserved = true;
            // CancelAsync has completed, so no callback can still be using
            // this exact source. Dispose before publishing drain completion.
            state.Cancellation.Dispose();
            if (state.Task is { IsFaulted: true } &&
                cancellationCallbackFailures < int.MaxValue)
            {
                cancellationCallbackFailures++;
            }

            if (ReferenceEquals(retiredCancellation, state))
                retiredCancellation = null;
        }
    }

    private sealed class ConnectionCancellationState(
        SyncConnectionLock owner,
        SyncConnectionLease lease,
        CancellationTokenSource cancellation)
    {
        public SyncConnectionLock Owner { get; } = owner;
        public SyncConnectionLease Lease { get; } = lease;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
        public bool CompletionObserved { get; set; }
    }
}
