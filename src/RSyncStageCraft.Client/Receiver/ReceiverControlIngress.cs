using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ReceiverRemoteConnection(
    long SessionId,
    long SessionGeneration,
    long ConnectionEpoch)
{
    public bool IsValid => SessionId > 0 && SessionGeneration > 0 && ConnectionEpoch > 0;
}

/// <summary>
/// Host-owned capability captured by one authenticated transport callback.
/// Wire fields cannot manufacture this identity. It retains no runtime,
/// transport, credentials, payloads, or cancellation registrations.
/// </summary>
public sealed class ReceiverControlBinding
{
    internal ReceiverControlBinding(
        ReceiverSessionKey localSession,
        ReceiverRemoteConnection remote,
        SyncConnectionLease connection)
    {
        LocalSession = localSession;
        Remote = remote;
        Connection = connection;
    }

    public ReceiverSessionKey LocalSession { get; }
    public ReceiverRemoteConnection Remote { get; }
    internal SyncConnectionLease Connection { get; }
}

public enum ReceiverControlBindStatus
{
    InvalidSession,
    Bound,
    InactiveSession,
    DependenciesUnavailable,
    AlreadyBound,
}

public readonly record struct ReceiverControlBindResult(
    ReceiverControlBindStatus Status,
    ReceiverControlBinding? Binding = null)
{
    public bool Bound => Status == ReceiverControlBindStatus.Bound;
}

public enum ReceiverControlReceiveStatus
{
    InvalidMessage,
    Accepted,
    Busy,
    InactiveBinding,
    EnvelopeMismatch,
    DependenciesUnavailable,
    WrongDirection,
    UnsupportedMessage,
    RejectedByReceiver,
    /// <summary>
    /// A correlated reply (completion or rejection). It is never desired-state
    /// intake; the authentication coordinator matches it to its request.
    /// </summary>
    Reply,
    /// <summary>A reply that matched no pending request, or that its request refused.</summary>
    UnmatchedReply,
}

public readonly record struct ReceiverControlReceiveResult(ReceiverControlReceiveStatus Status)
{
    public bool Accepted => Status == ReceiverControlReceiveStatus.Accepted;
    public ObjectControlCodecError CodecError { get; init; }
    public ReceiverActorSnapshotResult? Snapshot { get; init; }
    public ReceiverActorIngressResult? Event { get; init; }
    // The decoded frame, kept only for in-process reply correlation by the
    // authentication coordinator. Never retained beyond one receive.
    internal ObjectControlMessage? Message { get; init; }
}

/// <summary>
/// Single-connection, exact-binding single-flight control intake. The host must verify the
/// remote connection before binding and capture the returned capability in
/// that connection's receive callback; received numbers never select a session.
/// The lifecycle interface retires the binding before receiver cancellation.
/// No socket, receive queue, retry loop, or game/IPC side effect is created here.
/// </summary>
public sealed class ReceiverControlIngress : IReceiverActorSessionRuntime
{
    private readonly object bindingGate = new();
    private readonly SyncConnectionLock connection;
    private readonly ReceiverActorRuntime receiver;
    private readonly IReceiverReadinessPort readiness;
    private readonly IReceiverReadinessAdmissionFence? readinessAdmissionFence;
    private readonly ObjectControlMessageCodec codec;
    private ReceiverControlBinding? binding;
    private ReceiverControlBinding? receivingBinding;

    /// <summary>Owner display names from accepted actor messages (UI only, never authority).</summary>
    public OwnerDisplayNameDirectory OwnerNames { get; } = new();
    private SyncReceiverLifecycle? lifecycle;
    private long retiredLocalEpoch;

    internal ReceiverControlIngress(SyncConnectionLock connection, ReceiverActorRuntime receiver,
        IReceiverReadinessPort readiness,
        int maximumMessageBytes = ProtocolLimits.MaximumControlMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(readiness);
        this.connection = connection;
        this.receiver = receiver;
        this.readiness = readiness;
        readinessAdmissionFence = readiness as IReceiverReadinessAdmissionFence;
        codec = new(maximumMessageBytes);
    }

    // Deterministic seam for verifying that a receiver activation cannot be
    // mistaken for committed lifecycle authority. It is assembly-owned and
    // cannot alter the session selected by the lifecycle.
    internal Action<ReceiverSessionKey>? BeforeLifecycleActivationCommit { get; set; }

    internal void AttachLifecycle(SyncReceiverLifecycle owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (bindingGate)
        {
            if (lifecycle is not null)
                throw new InvalidOperationException("The control ingress lifecycle is already attached.");
            if (binding is not null || receivingBinding is not null)
                throw new InvalidOperationException("Lifecycle authority must be attached before control admission.");
            lifecycle = owner;
        }
    }

    public int RetainedBindingCount
    {
        get
        {
            lock (bindingGate)
                return binding is null ? 0 : 1;
        }
    }

    /// <summary>
    /// Trusted host handoff, NOT credential verification. Bind once after local
    /// activation and remote authentication. A different transport requires a
    /// full disconnect/reconnect, even for the same remote identity.
    /// </summary>
    public ReceiverControlBindResult BindVerifiedConnection(
        ReceiverSessionKey localSession, ReceiverRemoteConnection remote)
    {
        if (!localSession.IsValid || !remote.IsValid ||
            localSession.SessionId != remote.SessionId ||
            localSession.SessionGeneration != remote.SessionGeneration)
        {
            return new(ReceiverControlBindStatus.InvalidSession);
        }

        var lease = connection.CurrentLease;
        if (!lease.IsValid || lease.Epoch != localSession.ConnectionEpoch)
            return new(ReceiverControlBindStatus.InactiveSession);
        if (!HasCommittedLifecycleAuthority(localSession, lease))
            return new(ReceiverControlBindStatus.InactiveSession);
        if (!receiver.Authority.IsCurrent(localSession))
            return new(ReceiverControlBindStatus.InactiveSession);

        ReceiverControlBindResult Commit() =>
            connection.TryExecute(lease, () =>
                receiver.Authority.TryExecute(localSession, () =>
                {
                    if (!HasCommittedLifecycleAuthority(localSession, lease))
                        return new ReceiverControlBindResult(ReceiverControlBindStatus.InactiveSession);
                    lock (bindingGate)
                    {
                        if (localSession.ConnectionEpoch <= retiredLocalEpoch)
                            return new ReceiverControlBindResult(ReceiverControlBindStatus.InactiveSession);
                        if (binding is not null)
                            return new ReceiverControlBindResult(ReceiverControlBindStatus.AlreadyBound);

                        binding = new(localSession, remote, lease);
                        return new ReceiverControlBindResult(ReceiverControlBindStatus.Bound, binding);
                    }
                }, out var admitted) ? admitted : new(ReceiverControlBindStatus.InactiveSession),
                out var result) ? result : new(ReceiverControlBindStatus.InactiveSession);

        return TryExecuteIfReady(Commit, out var result)
            ? result
            : new(ReceiverControlBindStatus.DependenciesUnavailable);
    }

    /// <summary>
    /// Input memory must stay stable until return. Busy admits/retains nothing;
    /// the host must retry the same frame in order with bounded backpressure or
    /// disconnect. Accepted means desired-state admission, not rendered state.
    /// </summary>
    public ReceiverControlReceiveResult Receive(ReceiverControlBinding? source, ReadOnlyMemory<byte> utf8Json) =>
        Receive(source, utf8Json, decoded: null);

    private ReceiverControlReceiveResult Receive(
        ReceiverControlBinding? source,
        ReadOnlyMemory<byte> utf8Json,
        ObjectControlMessage? decoded)
    {
        if (source is null)
            return new(ReceiverControlReceiveStatus.InactiveBinding);
        lock (bindingGate)
        {
            if (!ReferenceEquals(binding, source))
                return new(ReceiverControlReceiveStatus.InactiveBinding);
            if (receivingBinding is not null)
                return new(ReceiverControlReceiveStatus.Busy);

            receivingBinding = source;
        }

        try
        {
            var authorized = ExecuteBound(source, () => new(ReceiverControlReceiveStatus.Accepted));
            if (!authorized.Accepted)
                return authorized;
            // Decode outside all lifecycle/authority locks. Disconnect need
            // not await parsing; the exact binding is checked again afterward.
            var message = decoded;
            if (message is null && !codec.TryDecode(utf8Json, out message, out var error))
                return new(ReceiverControlReceiveStatus.InvalidMessage) { CodecError = error };

            var admitted = ExecuteBound(
                source,
                () => new(ReceiverControlReceiveStatus.Accepted));
            if (!admitted.Accepted)
                return admitted;

            // The exact binding and connection admit this already-decoded
            // operation, but receiver work must not retain either lock across
            // fingerprinting or a session-port callback. Each destination owns
            // the apply-authority commit fence. If disconnect wins after this
            // admission, it either waits for that bounded commit or rejects it
            // as stale before ordered session cleanup can converge.
            return Dispatch(source, message!) with { Message = message };
        }
        finally
        {
            lock (bindingGate)
            {
                // Retirement may already have released this old session's
                // slot and a successor may now own it. A stale callback can
                // clear only its exact binding, never replacement admission.
                if (ReferenceEquals(receivingBinding, source))
                    receivingBinding = null;
            }
        }
    }

    private ReceiverControlReceiveResult ExecuteBound(
        ReceiverControlBinding source, Func<ReceiverControlReceiveResult> operation)
    {
        if (!connection.IsCurrent(source.Connection) ||
            !HasCommittedLifecycleAuthority(source.LocalSession, source.Connection) ||
            !receiver.Authority.IsCurrent(source.LocalSession))
        {
            return new(ReceiverControlReceiveStatus.InactiveBinding);
        }

        ReceiverControlReceiveResult Commit() =>
            connection.TryExecute(source.Connection, () =>
            {
                if (!HasCommittedLifecycleAuthority(source.LocalSession, source.Connection) ||
                    !receiver.Authority.IsCurrent(source.LocalSession))
                    return new ReceiverControlReceiveResult(ReceiverControlReceiveStatus.InactiveBinding);
                // Connection -> binding -> existing receiver ingress. Do NOT hold
                // apply authority here: snapshot admission/pumping takes its own
                // snapshot gate before authority. Each destination performs its
                // existing atomic authority check at mutation time.
                lock (bindingGate)
                {
                    if (!ReferenceEquals(binding, source))
                        return new ReceiverControlReceiveResult(ReceiverControlReceiveStatus.InactiveBinding);
                    return operation();
                }
            },
                out var result) ? result : new(ReceiverControlReceiveStatus.InactiveBinding);

        return TryExecuteIfReady(Commit, out var admitted)
            ? admitted
            : new(ReceiverControlReceiveStatus.DependenciesUnavailable);
    }

    private bool TryExecuteIfReady<T>(Func<T> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (readinessAdmissionFence is not null)
            return readinessAdmissionFence.TryExecuteIfReady(operation, out result);

        try
        {
            if (!readiness.IsReady)
            {
                result = default!;
                return false;
            }
        }
        catch
        {
            result = default!;
            return false;
        }

        result = operation();
        return true;
    }

    // Bounded assembly-owned command bookkeeping only. Never normalize a
    // collection, invoke an adapter/provider, await, or dispatch actor work here.
    // Unlike state ingress, this metadata commit needs no snapshot/actor gates.
    internal bool TryExecuteCommandMetadata<T>(ReceiverControlBinding source, Func<T> operation, out T result)
    {
        T committed = default!;
        bool Commit() => connection.TryExecute(source.Connection, () =>
        {
            if (!HasCommittedLifecycleAuthority(source.LocalSession, source.Connection)) return false;
            // Preserve existing connection -> binding -> destination authority
            // order. Exact retirement cannot pass any of these short fences.
            lock (bindingGate)
            {
                if (!ReferenceEquals(binding, source)) return false;
                return receiver.Authority.TryExecute(source.LocalSession, operation, out committed);
            }
        }, out var entered) && entered;
        var accepted = TryExecuteIfReady(Commit, out var admitted) && admitted;
        result = accepted ? committed : default!;
        return accepted;
    }

    private ReceiverControlReceiveResult Dispatch(ReceiverControlBinding source, ObjectControlMessage message)
    {
        var remote = source.Remote;
        var envelope = message.Envelope;
        if (envelope.SessionId != remote.SessionId ||
            envelope.SessionGeneration != remote.SessionGeneration ||
            envelope.ConnectionEpoch != remote.ConnectionEpoch)
        {
            return new(ReceiverControlReceiveStatus.EnvelopeMismatch);
        }

        var session = source.LocalSession;
        switch (message.Payload)
        {
            case ObjectSnapshotMessage snapshot:
                var staged = receiver.StageSnapshot(session, snapshot);
                if (staged.Accepted)
                    OwnerNames.Observe(snapshot);
                return new(Status(staged.Accepted)) { Snapshot = staged };
            case ObjectCreatedMessage created:
                var create = receiver.AcceptCreated(session, created);
                if (create.Accepted)
                    OwnerNames.Observe(created);
                return new(Status(create.Accepted)) { Event = create };
            case ObjectUpdatedMessage updated:
                var update = receiver.AcceptUpdated(session, updated);
                if (update.Accepted)
                    OwnerNames.Observe(updated);
                return new(Status(update.Accepted)) { Event = update };
            case ObjectDeletedMessage deleted:
                var delete = receiver.AcceptDeleted(session, deleted);
                return new(Status(delete.Accepted)) { Event = delete };
            case ObjectRequestRejected:
            case ObjectEventBatchCompletion:
            case SceneSaveCompletion:
            case SceneReadCompletion:
            case ObjectMutationCompletion:
            case ObjectSnapshotCompletion:
                // Correlated replies belong to the request registry, not to
                // desired-state intake. Never present them as state intake.
                return new(ReceiverControlReceiveStatus.Reply);
            default:
                return new(ReceiverControlReceiveStatus.WrongDirection);
        }
    }

    /// <summary>
    /// Stages the snapshot carried by a snapshot completion that the request
    /// registry has already matched to this binding's pending request. Uses
    /// the same exact-binding and readiness admission as ordinary intake.
    /// </summary>
    internal ReceiverControlReceiveResult StageCorrelatedSnapshot(
        ReceiverControlBinding source,
        ObjectSnapshotMessage snapshot)
    {
        var admitted = ExecuteBound(
            source,
            () => new(ReceiverControlReceiveStatus.Accepted));
        if (!admitted.Accepted)
            return admitted;
        var staged = receiver.StageSnapshot(source.LocalSession, snapshot);
        if (staged.Accepted)
            OwnerNames.Observe(snapshot);
        return new(Status(staged.Accepted)) { Snapshot = staged };
    }

    private static ReceiverControlReceiveStatus Status(bool accepted) => accepted
        ? ReceiverControlReceiveStatus.Accepted
        : ReceiverControlReceiveStatus.RejectedByReceiver;

    private bool HasCommittedLifecycleAuthority(
        ReceiverSessionKey session,
        SyncConnectionLease lease)
    {
        var owner = lifecycle;
        // Standalone component tests exercise the lower-layer binding and
        // retirement fences without a lifecycle. Production construction is
        // assembly-owned and always attaches the lifecycle before exposure.
        return owner is null || owner.HasActiveAuthority(session, lease);
    }

    bool IReceiverActorSessionRuntime.IsActive(ReceiverSessionKey session) =>
        receiver.Authority.IsCurrent(session);

    bool IReceiverActorSessionRuntime.TryActivate(ReceiverSessionKey session)
    {
        var activated = receiver.TryActivate(session);
        if (activated)
            BeforeLifecycleActivationCommit?.Invoke(session);
        return activated;
    }

    Task<ReceiverApplyCleanupResult> IReceiverActorSessionRuntime.DisconnectAsync(
        ReceiverSessionKey session, CancellationToken cancellationToken)
    {
        RetireBinding(session);
        return receiver.DisconnectAsync(session, cancellationToken);
    }

    Task<ReceiverApplyCleanupResult> IReceiverActorSessionRuntime.RetryCleanupAsync(
        ReceiverSessionKey session, CancellationToken cancellationToken)
    {
        RetireBinding(session);
        return receiver.RetryCleanupAsync(session, cancellationToken);
    }

    private void RetireBinding(ReceiverSessionKey session)
    {
        // Never acquire authority or connection while holding bindingGate.
        // The epoch watermark prevents rebind in the short interval before
        // receiver.DisconnectAsync revokes authority, including direct use of
        // the lifecycle interface. An obsolete cleanup cannot clear a successor.
        var current = receiver.Authority.IsCurrent(session);
        lock (bindingGate)
        {
            var matches = binding?.LocalSession == session;
            if (current || matches)
                retiredLocalEpoch = Math.Max(retiredLocalEpoch, session.ConnectionEpoch);
            if (matches)
            {
                var retiredBinding = binding;
                binding = null;
                if (ReferenceEquals(receivingBinding, retiredBinding))
                    receivingBinding = null;
            }
        }
    }
}
