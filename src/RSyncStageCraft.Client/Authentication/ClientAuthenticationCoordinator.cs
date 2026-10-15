using System.Net.WebSockets;
using System.Security.Cryptography;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Core.Authentication;

public readonly record struct ClientAuthenticationLimits(
    int MaximumStartsPerMinute, TimeSpan HandshakeTimeout, int MaximumReceiveSegments)
{
    public const int MaximumReceiveSegmentsHardLimit = 1024;
    public static ClientAuthenticationLimits Default => new(30, TimeSpan.FromSeconds(15), 64);
    public bool IsValid => MaximumStartsPerMinute > 0 && MaximumStartsPerMinute <= 120 &&
        HandshakeTimeout > TimeSpan.Zero && HandshakeTimeout <= TimeSpan.FromMinutes(2) &&
        MaximumReceiveSegments > 0 && MaximumReceiveSegments <= MaximumReceiveSegmentsHardLimit;
}

/// <summary>Reference-only local capability; never a wire token or TLS proof.</summary>
public sealed class ClientAuthenticationConnection { internal ClientAuthenticationConnection() { } }

public enum ClientAuthenticationBeginStatus
{
    InvalidTransport, Begun, AlreadyTracked, CapacityExceeded, RateLimited,
    LocalSessionActive, LocalCleanupRequired, Terminated,
}

public readonly record struct ClientAuthenticationBeginResult(
    ClientAuthenticationBeginStatus Status, ClientAuthenticationConnection? Connection = null);

public enum ClientAuthenticationStatus
{
    InactiveConnection, Activated, Busy, InvalidCredential, DependenciesUnavailable,
    Cancelled, TimedOut, MessageTooLarge, SegmentLimitExceeded,
    UnexpectedMessageType, RemoteClosed, InvalidAuthorization, TransportFailure,
    ActivationRejected, BindingRejected, CleanupRequired,
}

public readonly record struct ClientAuthenticationResult(
    ClientAuthenticationStatus Status,
    ReceiverSessionKey? Session = null,
    ReceiverControlBinding? Binding = null)
{
    public bool Activated => Status == ClientAuthenticationStatus.Activated;

    /// <summary>Protocol 6: the signed-in user's own RSC identity, as the server reported it.</summary>
    public string? UserId { get; init; }
    /// <summary>Protocol 6: that account's optional display name (presentation only).</summary>
    public string? DisplayName { get; init; }
    public bool AbortFailed { get; init; }
    public bool CancellationCallbacksFailed { get; init; }
    public SyncReceiverConnectStatus? ConnectStatus { get; init; }
    public ReceiverControlBindStatus? BindStatus { get; init; }
}

public readonly record struct ClientAuthenticationCloseResult(bool Found, bool Released, bool CleanupFailed);

public readonly record struct ClientControlReceiveLimits(
    int MaximumMessageBytes,
    int MaximumReceiveSegments,
    TimeSpan PartialMessageTimeout,
    int MaximumDispatchAttempts)
{
    public const int MaximumDispatchAttemptsHardLimit = 16;
    public static ClientControlReceiveLimits Default => new(
        ProtocolLimits.MaximumControlMessageBytes,
        1024,
        TimeSpan.FromSeconds(30),
        3);
    internal WebSocketControlReadLimits ReaderLimits => new(
        MaximumMessageBytes, MaximumReceiveSegments, PartialMessageTimeout);
    public bool IsValid => ReaderLimits.IsValid &&
        MaximumDispatchAttempts > 0 &&
        MaximumDispatchAttempts <= MaximumDispatchAttemptsHardLimit;
}

public enum ClientControlReceiveStatus
{
    InactiveConnection,
    Accepted,
    Busy,
    Backpressure,
    Cancelled,
    ReadFailure,
    DispatchFailure,
}

public readonly record struct ClientControlReceiveResult(
    ClientControlReceiveStatus Status,
    WebSocketControlReadStatus? ReadStatus = null,
    ReceiverControlReceiveResult? Dispatch = null)
{
    public bool Accepted => Status == ClientControlReceiveStatus.Accepted;
    public bool RetryRequired => Status == ClientControlReceiveStatus.Backpressure;
    public bool Terminal { get; init; }
    public bool AbortFailed { get; init; }
    public bool CancellationCallbacksFailed { get; init; }
    /// <summary>
    /// Set when the frame was a reply (or a batch event) matched to a pending
    /// tracked request: EventObserved for a batch event, Completed with the
    /// command result for the final reply.
    /// </summary>
    public ClientControlResponseResult? Response { get; init; }
}

public readonly record struct ClientControlSendLimits(int MaximumMessageBytes)
{
    public ClientControlRequestLimits Requests { get; init; } = ClientControlRequestLimits.Default;
    public static ClientControlSendLimits Default => new(ProtocolLimits.MaximumControlMessageBytes);
    public bool IsValid => MaximumMessageBytes > 0 &&
        MaximumMessageBytes <= ProtocolLimits.MaximumControlMessageBytes && Requests.IsValid;
}

public enum ClientControlSendStatus
{
    InactiveConnection,
    Sent,
    Busy,
    InvalidMessage,
    Cancelled,
    TransportFailure,
    RequestRefused,
}

public readonly record struct ClientControlSendResult(
    ClientControlSendStatus Status,
    ObjectControlCodecError? CodecError = null)
{
    public bool Sent => Status == ClientControlSendStatus.Sent;
    public bool Terminal { get; init; }
    public bool AbortFailed { get; init; }
    public bool CancellationCallbacksFailed { get; init; }
    public ClientControlRequestRegistrationStatus? RequestRegistration { get; init; }
    public ClientControlRequestReceipt? RequestReceipt { get; init; }
}

/// <summary>
/// Client-side one-transport authentication owner. The host validates TLS and
/// endpoint policy before registration and exclusively owns the supplied socket.
/// This opens no endpoint, stores no credential, and infers no external IPC API.
/// </summary>
public sealed class ClientAuthenticationCoordinator
{
    private readonly object gate = new();
    private readonly SyncReceiverLifecycle lifecycle;
    private readonly ReceiverControlIngress control;
    private readonly ReceiverReadinessCoordinator readiness;
    private readonly ClientAuthenticationLimits limits;
    private readonly ClientControlReceiveLimits controlLimits;
    private readonly ObjectControlMessageCodec controlSendCodec;
    private readonly ClientControlRequestLimits requestLimits;
    private readonly TimeProvider time;
    private Entry? entry;
    private long windowStart;
    private int windowStarts;
    private bool terminated;

    // Deterministic concurrency seam for exact cleanup-ownership tests. The
    // callback is never part of the host contract and cannot alter state.
    internal Action<ReceiverSessionKey?>? BeforeCleanupCompletion { get; set; }

    internal ClientAuthenticationCoordinator(SyncReceiverLifecycle lifecycle,
        ReceiverControlIngress control, ReceiverReadinessCoordinator readiness,
        ClientAuthenticationLimits limits, TimeProvider time,
        ClientControlReceiveLimits? controlReceiveLimits = null,
        ClientControlSendLimits? controlSendLimits = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(time);
        if (!limits.IsValid) throw new ArgumentOutOfRangeException(nameof(limits));
        var selectedControlLimits = controlReceiveLimits ?? ClientControlReceiveLimits.Default;
        if (!selectedControlLimits.IsValid)
            throw new ArgumentOutOfRangeException(nameof(controlReceiveLimits));
        var selectedControlSendLimits = controlSendLimits ?? ClientControlSendLimits.Default;
        if (!selectedControlSendLimits.IsValid)
            throw new ArgumentOutOfRangeException(nameof(controlSendLimits));
        this.lifecycle = lifecycle;
        this.control = control;
        this.readiness = readiness;
        this.limits = limits;
        controlLimits = selectedControlLimits;
        controlSendCodec = new(selectedControlSendLimits.MaximumMessageBytes);
        requestLimits = selectedControlSendLimits.Requests;
        this.time = time;
        windowStart = time.GetTimestamp();
    }

    public int Count { get { lock (gate) return entry is null ? 0 : 1; } }
    public bool IsTerminated { get { lock (gate) return terminated; } }

    public ClientAuthenticationBeginResult BeginTrustedTransport(object? transportIdentity)
    {
        if (transportIdentity is null)
            return new(ClientAuthenticationBeginStatus.InvalidTransport);

        long observedWindowStart;
        lock (gate)
        {
            if (terminated) return new(ClientAuthenticationBeginStatus.Terminated);
            if (entry is not null)
                return new(ReferenceEquals(entry.Transport, transportIdentity)
                    ? ClientAuthenticationBeginStatus.AlreadyTracked
                    : ClientAuthenticationBeginStatus.CapacityExceeded);
            var local = lifecycle.Current;
            if (local.State == SyncReceiverLifecycleState.Active)
                return new(local.Lease.IsValid
                    ? ClientAuthenticationBeginStatus.LocalSessionActive
                    : ClientAuthenticationBeginStatus.LocalCleanupRequired);
            if (local.State is SyncReceiverLifecycleState.Cleaning or SyncReceiverLifecycleState.CleanupRequired)
                return new(ClientAuthenticationBeginStatus.LocalCleanupRequired);
            if (local.State == SyncReceiverLifecycleState.Terminated) return new(ClientAuthenticationBeginStatus.Terminated);
            observedWindowStart = windowStart;
        }

        // TimeProvider is a virtual host boundary. Sample it without holding the
        // lifecycle monitor, then repeat every publication predicate below.
        var now = time.GetTimestamp();
        var elapsed = time.GetElapsedTime(observedWindowStart, now);
        lock (gate)
        {
            if (terminated) return new(ClientAuthenticationBeginStatus.Terminated);
            if (entry is not null)
                return new(ReferenceEquals(entry.Transport, transportIdentity)
                    ? ClientAuthenticationBeginStatus.AlreadyTracked
                    : ClientAuthenticationBeginStatus.CapacityExceeded);
            var local = lifecycle.Current;
            if (local.State == SyncReceiverLifecycleState.Active)
                return new(local.Lease.IsValid
                    ? ClientAuthenticationBeginStatus.LocalSessionActive
                    : ClientAuthenticationBeginStatus.LocalCleanupRequired);
            if (local.State is SyncReceiverLifecycleState.Cleaning or SyncReceiverLifecycleState.CleanupRequired)
                return new(ClientAuthenticationBeginStatus.LocalCleanupRequired);
            if (local.State == SyncReceiverLifecycleState.Terminated) return new(ClientAuthenticationBeginStatus.Terminated);
            if (windowStart != observedWindowStart)
                return new(ClientAuthenticationBeginStatus.RateLimited);
            if (elapsed < TimeSpan.Zero) return new(ClientAuthenticationBeginStatus.RateLimited);
            if (elapsed >= TimeSpan.FromMinutes(1))
            {
                windowStart = now;
                windowStarts = 0;
            }
            if (windowStarts >= limits.MaximumStartsPerMinute) return new(ClientAuthenticationBeginStatus.RateLimited);
            windowStarts++;
            var connection = new ClientAuthenticationConnection();
            entry = new(connection, transportIdentity, now);
            return new(ClientAuthenticationBeginStatus.Begun, connection);
        }
    }

    /// <summary>
    /// Copies the caller's canonical key into one owned request before returning
    /// from this non-async wrapper. The caller still owns its original secret.
    /// </summary>
    public Task<ClientAuthenticationResult> AuthenticateAsync(
        ClientAuthenticationConnection? source, WebSocket socket,
        ReadOnlySpan<char> credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!AuthenticationMessageCodec.IsCanonicalCredential(credential))
            return Task.FromResult(new ClientAuthenticationResult(ClientAuthenticationStatus.InvalidCredential));
        var ready = readiness.Current;
        if (!ready.CanConnect)
            return Task.FromResult(new ClientAuthenticationResult(ClientAuthenticationStatus.DependenciesUnavailable));
        if (!TryBegin(source, socket, out var selected, out var operation, out var remaining, out var failure))
            return Task.FromResult(new ClientAuthenticationResult(failure));
        byte[] request;
        try
        {
            request = AuthenticationMessageCodec.EncodeRequest(credential,
                new(ProtocolConstants.CurrentVersion),
                new(ready.PenumbraReady, ready.GlamourerReady, ready.IpcReady));
        }
        catch
        {
            Retire(selected!, operation!);
            End(selected!, operation!, false, null, null);
            throw;
        }
        return AuthenticateOwnedAsync(selected!, operation!, socket, request, remaining, cancellationToken);
    }

    public bool TryResolveAuthenticated(ClientAuthenticationConnection? source,
        out ReceiverSessionKey session, out ReceiverControlBinding? binding)
    {
        lock (gate)
        {
            session = default;
            binding = null;
            if (source is null || entry is null || !ReferenceEquals(entry.Connection, source) ||
                !entry.Established || entry.Retired || terminated || entry.Session is not { } active ||
                entry.Binding is not { } retained)
                return false;
            var current = lifecycle.Current;
            if (!readiness.IsReady || !HasActiveAuthority(current, active))
            {
                RetireUnsafe(entry);
                return false;
            }
            session = active;
            binding = retained;
            return true;
        }
    }

    /// <summary>
    /// Reads or retries exactly one bounded server-to-client control message.
    /// The caller must not use another receiver on this socket. Backpressure
    /// retains one frame and retries it before any later socket read.
    /// </summary>
    public Task<ClientControlReceiveResult> ReceiveControlAsync(
        ClientAuthenticationConnection? source,
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ClientControlReceiveResult(ClientControlReceiveStatus.Cancelled));
        if (!TryBeginControlReceive(
            source, socket, out var selected, out var operation,
            out var binding, out var pending, out var attempts, out var failure))
            return Task.FromResult(new ClientControlReceiveResult(failure));
        return ReceiveControlOwnedAsync(
            selected!, operation!, binding!, socket, pending, attempts, cancellationToken);
    }

    /// <summary>
    /// Encodes and sends exactly one client-to-server control message through
    /// the authenticated socket. Session envelope authority is taken only from
    /// the retained binding; callers cannot supply wire session coordinates.
    /// </summary>
    public Task<ClientControlSendResult> SendControlAsync(
        ClientAuthenticationConnection? source,
        WebSocket socket,
        object? payload,
        CancellationToken cancellationToken = default) =>
        SendControlCoreAsync(source, socket, payload, cancellationToken, trackRequest: false);

    /// <summary>
    /// Registers bounded correlation metadata before entering the socket send.
    /// IDs cannot be reused within this connection, even after abandonment.
    /// One connection uses either tracked or legacy requests; acknowledgements
    /// remain untracked. Mixing request modes is refused before socket entry.
    /// Command-response dispatch awaits a coordinated protocol revision; the
    /// returned receipt is preparation for that owner, not proof of acceptance.
    /// </summary>
    public Task<ClientControlSendResult> SendTrackedControlAsync(
        ClientAuthenticationConnection? source,
        WebSocket socket,
        object? payload,
        CancellationToken cancellationToken = default) =>
        SendControlCoreAsync(source, socket, payload, cancellationToken, trackRequest: true);

    private Task<ClientControlSendResult> SendControlCoreAsync(
        ClientAuthenticationConnection? source, WebSocket socket, object? payload,
        CancellationToken cancellationToken, bool trackRequest)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ClientControlSendResult(ClientControlSendStatus.Cancelled));
        if (!TryDescribeOutboundControl(payload, out var messageType, out var messageId))
        {
            return Task.FromResult(new ClientControlSendResult(
                ClientControlSendStatus.InvalidMessage,
                ObjectControlCodecError.InvalidPayload));
        }
        if (!TryBeginControlSend(
            source, socket, out var selected, out var operation, out var remote, out var failure))
            return Task.FromResult(new ClientControlSendResult(failure));
        return SendControlOwnedAsync(
            selected!, operation!, socket, remote, messageType, messageId, payload!, cancellationToken,
            trackRequest);
    }

    public ClientControlRequestSnapshot GetControlRequestState(ClientAuthenticationConnection? source)
    {
        lock (gate)
            return source is not null && entry is not null && ReferenceEquals(entry.Connection, source)
                ? entry.Requests?.Snapshot ?? new(0, 0, true) : new(0, 0, true);
    }

    /// <summary>Local cancellation frees pending capacity; it never makes the ID reusable.</summary>
    public bool AbandonControlRequest(ClientAuthenticationConnection? source,
        ClientControlRequestReceipt? receipt)
    {
        lock (gate)
            return receipt is not null && TryResolveAuthenticated(source, out _, out _) &&
                entry!.Requests?.Abandon(receipt) == true;
    }

    // Exact local lookup for send ownership and isolated contract tests. A
    // matching GUID alone is never command-response validation.
    internal bool TryFindControlRequest(ClientAuthenticationConnection? source,
        ReceiverControlBinding binding, Guid requestId, out ClientControlRequestReceipt? receipt)
    {
        lock (gate)
        {
            receipt = null;
            return TryResolveAuthenticated(source, out _, out var current) &&
                ReferenceEquals(current, binding) && entry!.Requests is { } requests &&
                requests.TryFind(binding, requestId, out receipt);
        }
    }

    internal bool CompleteControlRequest(ClientAuthenticationConnection? source,
        ReceiverControlBinding binding, ClientControlRequestReceipt receipt)
    {
        // Trusted local bookkeeping only, not wire dispatch. Candidate replies
        // use ReceiveCandidateControlResponse to verify family and batch proof.
        lock (gate)
            return TryResolveAuthenticated(source, out _, out var current) &&
                ReferenceEquals(current, binding) && entry!.Requests?.Complete(binding, receipt) == true;
    }

    // Isolated candidate contract intake, not the version-1 receive path. It
    // never invokes the receiver/apply pipeline or retains command documents.
    internal ClientControlResponseResult ReceiveCandidateControlResponse(ClientAuthenticationConnection? source,
        ReceiverControlBinding binding, ClientControlRequestReceipt receipt, ObjectControlMessage? message)
    {
        ClientControlRequestRegistry requests;
        lock (gate)
        {
            if (!TryResolveAuthenticated(source, out _, out var current) || !ReferenceEquals(current, binding) ||
                entry!.Requests is not { } owned || !owned.Owns(binding, receipt))
                return new(ClientControlResponseStatus.InactiveConnection);
            requests = owned;
        }
        // Collections supplied by callers may reenter retirement while being
        // normalized. Revalidate live authority before the metadata commit.
        var prepared = ClientControlResponseFrame.TryPrepare(message, out var frame);
        lock (gate)
        {
            if (!TryResolveAuthenticated(source, out _, out var current) || !ReferenceEquals(current, binding) ||
                !ReferenceEquals(entry!.Requests, requests))
                return new(ClientControlResponseStatus.InactiveConnection);
            if (control.TryExecuteCommandMetadata(binding,
                () => prepared ? requests.ReceivePrepared(binding, receipt, frame!)
                    : requests.RefuseInvalidCandidate(binding, receipt, message?.Envelope), out var result))
                return result;
            RetireUnsafe(entry!);
            return new(ClientControlResponseStatus.InactiveConnection);
        }
    }

    public IReadOnlyList<ClientAuthenticationConnection> GetConnectionsToClose()
    {
        Entry observed;
        bool established;
        lock (gate)
        {
            if (entry is null) return [];
            observed = entry;
            established = entry.Established;
        }

        var elapsed = established ? TimeSpan.Zero : SampleElapsed(observed.StartedAt);
        lock (gate)
        {
            if (!ReferenceEquals(entry, observed))
                return entry?.Retired == true ? [entry.Connection] : [];
            var current = lifecycle.Current;
            if (terminated || (!observed.Established && IsExpired(elapsed)) ||
                !readiness.IsReady || (observed.Established &&
                    (observed.Session is not { } session || !HasActiveAuthority(current, session))))
                RetireUnsafe(observed);
            return observed.Retired ? [observed.Connection] : [];
        }
    }

    public async Task<ClientAuthenticationCloseResult> CloseTransportAsync(ClientAuthenticationConnection? source)
    {
        Entry selected;
        lock (gate)
        {
            if (source is null || entry is null || !ReferenceEquals(entry.Connection, source)) return default;
            selected = entry;
            RetireUnsafe(selected);
            selected.TransportClosed = true;
        }
        var cleanup = await CleanupAsync(selected).ConfigureAwait(false);
        lock (gate)
        {
            // Cleanup may have released the entry while completing the exact
            // close obligation. Report that completed release to this owner.
            var released = !ReferenceEquals(entry, selected) || TryReleaseUnsafe(selected);
            return new(true, released, cleanup == CleanupStatus.Failed);
        }
    }

    internal void Terminate()
    {
        lock (gate)
        {
            terminated = true;
            if (entry is not null) RetireUnsafe(entry);
        }
    }

    internal void RetireForObservedReadinessLoss()
    {
        lock (gate)
        {
            // The caller supplies an exact unhealthy observation. Do not
            // resample shared readiness here: a concurrently published
            // recovery must not erase the loss event before the old transport
            // is retired and ordinary lifecycle reconciliation catches up.
            if (entry is not null)
                RetireUnsafe(entry);
        }
    }

    internal void RetireSession(ReceiverSessionKey session)
    {
        lock (gate)
        {
            if (entry?.Session == session)
                RetireUnsafe(entry);
        }
    }

    private bool TryBegin(ClientAuthenticationConnection? source, object transport,
        out Entry? selected, out AuthenticationIoOperation? operation, out TimeSpan remaining,
        out ClientAuthenticationStatus failure)
    {
        selected = null;
        operation = null;
        remaining = default;
        failure = ClientAuthenticationStatus.InactiveConnection;
        Entry observed;
        lock (gate)
        {
            if (source is null || entry is null || !ReferenceEquals(entry.Connection, source) ||
                !ReferenceEquals(entry.Transport, transport))
                return false;
            observed = entry;
        }

        var elapsed = SampleElapsed(observed.StartedAt);
        lock (gate)
        {
            if (!ReferenceEquals(entry, observed) ||
                !ReferenceEquals(observed.Connection, source) ||
                !ReferenceEquals(observed.Transport, transport) ||
                terminated || observed.Retired || observed.Established)
            {
                return false;
            }
            if (observed.Operation is not null)
            {
                failure = ClientAuthenticationStatus.Busy;
                return false;
            }
            if (observed.Started) return false;
            if (!readiness.IsReady)
            {
                failure = ClientAuthenticationStatus.DependenciesUnavailable;
                return false;
            }
            remaining = limits.HandshakeTimeout - elapsed;
            if (remaining <= TimeSpan.Zero || remaining > limits.HandshakeTimeout)
            {
                RetireUnsafe(observed);
                return false;
            }
            selected = observed;
            operation = observed.Operation = new();
            observed.Started = true;
            return true;
        }
    }

    private bool TryBeginControlReceive(
        ClientAuthenticationConnection? source,
        object transport,
        out Entry? selected,
        out AuthenticationIoOperation? operation,
        out ReceiverControlBinding? binding,
        out ReadOnlyMemory<byte>? pending,
        out int attempts,
        out ClientControlReceiveStatus failure)
    {
        lock (gate)
        {
            selected = null;
            operation = null;
            binding = null;
            pending = null;
            attempts = 0;
            failure = ClientControlReceiveStatus.InactiveConnection;
            if (source is null || entry is null || !ReferenceEquals(entry.Connection, source) ||
                !ReferenceEquals(entry.Transport, transport) || terminated || entry.Retired ||
                !entry.Established || entry.Session is not { } session || entry.Binding is not { } retained)
                return false;
            if (entry.ControlOperation is not null)
            {
                failure = ClientControlReceiveStatus.Busy;
                return false;
            }
            var current = lifecycle.Current;
            if (!readiness.IsReady || !HasActiveAuthority(current, session))
            {
                RetireUnsafe(entry);
                return false;
            }
            selected = entry;
            operation = entry.ControlOperation = new();
            binding = retained;
            pending = entry.PendingControlPayload;
            attempts = entry.PendingControlAttempts;
            return true;
        }
    }

    private bool TryBeginControlSend(
        ClientAuthenticationConnection? source,
        object transport,
        out Entry? selected,
        out AuthenticationIoOperation? operation,
        out ReceiverRemoteConnection remote,
        out ClientControlSendStatus failure)
    {
        lock (gate)
        {
            selected = null;
            operation = null;
            remote = default;
            failure = ClientControlSendStatus.InactiveConnection;
            if (source is null || entry is null || !ReferenceEquals(entry.Connection, source) ||
                !ReferenceEquals(entry.Transport, transport) || terminated || entry.Retired ||
                !entry.Established || entry.Session is not { } session || entry.Binding is not { } binding)
                return false;
            if (entry.ControlSendOperation is not null)
            {
                failure = ClientControlSendStatus.Busy;
                return false;
            }
            var current = lifecycle.Current;
            if (!readiness.IsReady || !HasActiveAuthority(current, session))
            {
                RetireUnsafe(entry);
                return false;
            }
            selected = entry;
            operation = entry.ControlSendOperation = new();
            remote = binding.Remote;
            return true;
        }
    }

    private async Task<ClientControlSendResult> SendControlOwnedAsync(
        Entry selected,
        AuthenticationIoOperation operation,
        WebSocket socket,
        ReceiverRemoteConnection remote,
        ObjectProtocolMessageType messageType,
        Guid messageId,
        object payload,
        CancellationToken cancellationToken,
        bool trackRequest)
    {
        CancellationTokenRegistration registration = default;
        var result = new ClientControlSendResult(ClientControlSendStatus.TransportFailure)
        {
            Terminal = true,
        };
        var encoded = Array.Empty<byte>();
        var keepConnection = false;
        var abortNeeded = false;
        var abortFailed = false;
        var callbackFailed = false;
        ClientControlRequestRegistration? request = null;
        try
        {
            registration = cancellationToken.UnsafeRegister(static state =>
                ((AuthenticationIoOperation)state!).Cancel(AuthenticationIoCancellation.Cancelled), operation);
            operation.Token.ThrowIfCancellationRequested();
            var envelope = new SessionMessageEnvelope(
                ProtocolConstants.CurrentVersion,
                messageType,
                messageId,
                remote.SessionId,
                remote.SessionGeneration,
                remote.ConnectionEpoch);
            if (!controlSendCodec.TryEncode(envelope, payload, out encoded, out var codecError))
            {
                keepConnection = true;
                result = new(ClientControlSendStatus.InvalidMessage, codecError);
            }
            else
            {
                request = PrepareControlRequest(selected, operation, remote,
                    messageType, messageId, payload, trackRequest);
                if (request is { } registrationResult &&
                    registrationResult.Status != ClientControlRequestRegistrationStatus.Registered)
                {
                    keepConnection = true;
                    result = new(ClientControlSendStatus.RequestRefused);
                    throw new ControlSendFailureException();
                }
                if (cancellationToken.IsCancellationRequested)
                    operation.Cancel(AuthenticationIoCancellation.Cancelled);
                operation.Token.ThrowIfCancellationRequested();
                if (!IsControlSendCurrent(selected, operation, remote))
                {
                    result = new(ClientControlSendStatus.InactiveConnection) { Terminal = true };
                    abortNeeded = true;
                    throw new ControlSendFailureException();
                }

                await socket.SendAsync(
                    encoded.AsMemory(), WebSocketMessageType.Text, true, operation.Token).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    operation.Cancel(AuthenticationIoCancellation.Cancelled);
                operation.Token.ThrowIfCancellationRequested();
                if (!IsControlSendCurrent(selected, operation, remote))
                {
                    result = new(ClientControlSendStatus.InactiveConnection) { Terminal = true };
                    abortNeeded = true;
                }
                else
                {
                    keepConnection = true;
                    result = new(ClientControlSendStatus.Sent);
                }
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            result = new(operation.CancellationStatus == AuthenticationIoCancellation.Cancelled
                ? ClientControlSendStatus.Cancelled
                : ClientControlSendStatus.InactiveConnection)
            {
                Terminal = true,
            };
            abortNeeded = true;
        }
        catch (ControlSendFailureException)
        {
            // Result and abort ownership were selected at the exact failure site.
        }
        catch (Exception)
        {
            // Provider details never cross this composed transport boundary.
            result = new(ClientControlSendStatus.TransportFailure) { Terminal = true };
            abortNeeded = true;
        }
        finally
        {
            if (result.Terminal)
                RetireControlSend(selected, operation);
            if (abortNeeded)
            {
                try { socket.Abort(); }
                catch (Exception) { abortFailed = true; }
            }
            await registration.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                operation.Cancel(AuthenticationIoCancellation.Cancelled);
            callbackFailed = await operation.DrainAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(encoded);
            var completed = CompleteControlSend(selected, operation, keepConnection);
            if (keepConnection && !completed)
                result = new(ClientControlSendStatus.InactiveConnection) { Terminal = true };
        }
        return result with
        {
            AbortFailed = abortFailed,
            CancellationCallbacksFailed = callbackFailed,
            RequestRegistration = request?.Status,
            RequestReceipt = request?.Receipt,
        };
    }

    private ClientControlRequestRegistration? PrepareControlRequest(Entry selected,
        AuthenticationIoOperation operation, ReceiverRemoteConnection remote,
        ObjectProtocolMessageType messageType, Guid requestId, object payload, bool trackRequest)
    {
        lock (gate)
        {
            if (!IsControlSendCurrent(selected, operation, remote) || selected.Requests is null)
                return new(ClientControlRequestRegistrationStatus.InactiveConnection);
            var kind = messageType switch
            {
                ObjectProtocolMessageType.CreateObject => ClientControlRequestKind.CreateObject,
                ObjectProtocolMessageType.UpdateObject => ClientControlRequestKind.UpdateObject,
                ObjectProtocolMessageType.DeleteObject => ClientControlRequestKind.DeleteObject,
                ObjectProtocolMessageType.RequestObjectSnapshot => ClientControlRequestKind.Snapshot,
                ObjectProtocolMessageType.RequestObjectEvents => ClientControlRequestKind.Events,
                ObjectProtocolMessageType.SaveScene => ClientControlRequestKind.SaveScene,
                ObjectProtocolMessageType.ReadScene => ClientControlRequestKind.ReadScene,
                _ => (ClientControlRequestKind?)null,
            };
            if (kind is null)
                return trackRequest ? new(ClientControlRequestRegistrationStatus.InvalidRequest) : null;
            // Legacy requests also carry a wire ID. Mixing modes could allow a
            // delayed legacy reply to complete a new tracked request with the
            // same ID, or bypass the tracked duplicate fence.
            if (selected.TrackedRequests is { } chosen && chosen != trackRequest)
                return new(ClientControlRequestRegistrationStatus.RequestModeConflict);
            selected.TrackedRequests = trackRequest;
            if (!trackRequest) return null;
            var scene = payload switch
            {
                SaveSceneRequest save => (RscSceneId?)save.SceneId,
                ReadSceneRequest read => read.SceneId,
                _ => null,
            };
            ClientObjectEventRequestMetadata? events = payload is ObjectEventBatchRequest batch
                ? new(batch.After, batch.MaximumEvents) : null;
            return selected.Requests.Register(requestId, kind.Value, scene, events,
                ClientObjectMutationRequestMetadata.FromRequest(payload));
        }
    }

    private bool IsControlSendCurrent(
        Entry selected,
        AuthenticationIoOperation operation,
        ReceiverRemoteConnection remote)
    {
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) ||
                !ReferenceEquals(selected.ControlSendOperation, operation) ||
                selected.Binding?.Remote != remote || terminated || selected.Retired ||
                !selected.Established || operation.Token.IsCancellationRequested ||
                !readiness.IsReady || selected.Session is not { } session)
                return false;
            var current = lifecycle.Current;
            return HasActiveAuthority(current, session);
        }
    }

    private bool CompleteControlSend(
        Entry selected,
        AuthenticationIoOperation operation,
        bool keepConnection)
    {
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) ||
                !ReferenceEquals(selected.ControlSendOperation, operation))
                return false;
            var current = lifecycle.Current;
            var accepted = keepConnection && !terminated && !selected.Retired &&
                !operation.Token.IsCancellationRequested && readiness.IsReady &&
                selected.Established && selected.Session is { } session &&
                HasActiveAuthority(current, session);
            if (!accepted)
                RetireUnsafe(selected);
            selected.ControlSendOperation = null;
            TryReleaseUnsafe(selected);
            return accepted;
        }
    }

    private void RetireControlSend(Entry selected, AuthenticationIoOperation operation)
    {
        lock (gate)
        {
            if (ReferenceEquals(entry, selected) &&
                ReferenceEquals(selected.ControlSendOperation, operation))
                RetireUnsafe(selected);
        }
    }

    private static bool HasActiveAuthority(
        SyncReceiverLifecycleSnapshot current,
        ReceiverSessionKey session) =>
        current.State == SyncReceiverLifecycleState.Active &&
        current.ActiveSession == session &&
        current.Lease.IsValid &&
        current.Lease.Epoch == session.ConnectionEpoch;

    private static bool TryDescribeOutboundControl(
        object? payload,
        out ObjectProtocolMessageType messageType,
        out Guid messageId)
    {
        messageType = default;
        messageId = default;
        switch (payload)
        {
            case CreateObjectRequest request:
                messageType = ObjectProtocolMessageType.CreateObject;
                messageId = request.ClientRequestId;
                break;
            case UpdateObjectRequest request:
                messageType = ObjectProtocolMessageType.UpdateObject;
                messageId = request.RequestId;
                break;
            case DeleteObjectRequest request:
                messageType = ObjectProtocolMessageType.DeleteObject;
                messageId = request.RequestId;
                break;
            case ObjectStreamAcknowledgement:
                messageType = ObjectProtocolMessageType.AcknowledgeObjects;
                messageId = Guid.NewGuid();
                break;
            case ObjectSnapshotRequest request:
                messageType = ObjectProtocolMessageType.RequestObjectSnapshot;
                messageId = request.RequestId;
                break;
            case ObjectEventBatchRequest request:
                messageType = ObjectProtocolMessageType.RequestObjectEvents;
                messageId = request.RequestId;
                break;
            case SaveSceneRequest request:
                messageType = ObjectProtocolMessageType.SaveScene;
                messageId = request.RequestId;
                break;
            case ReadSceneRequest request:
                messageType = ObjectProtocolMessageType.ReadScene;
                messageId = request.RequestId;
                break;
            default:
                return false;
        }
        // Preserve a payload-validation diagnostic for empty request IDs rather
        // than manufacturing an invalid envelope before the codec sees it.
        if (messageId == Guid.Empty)
            messageId = Guid.NewGuid();
        return true;
    }

    private async Task<ClientControlReceiveResult> ReceiveControlOwnedAsync(
        Entry selected,
        AuthenticationIoOperation operation,
        ReceiverControlBinding binding,
        WebSocket socket,
        ReadOnlyMemory<byte>? pending,
        int attempts,
        CancellationToken cancellationToken)
    {
        CancellationTokenRegistration registration = default;
        var result = new ClientControlReceiveResult(ClientControlReceiveStatus.ReadFailure)
        {
            Terminal = true,
        };
        var payload = pending;
        var keepConnection = false;
        var abortNeeded = false;
        var abortFailed = false;
        var callbackFailed = false;
        try
        {
            registration = cancellationToken.UnsafeRegister(static state =>
                ((AuthenticationIoOperation)state!).Cancel(AuthenticationIoCancellation.Cancelled), operation);
            operation.Token.ThrowIfCancellationRequested();
            if (payload is null)
            {
                var reader = new WebSocketControlMessageReader(socket, controlLimits.ReaderLimits);
                WebSocketControlReadResult read;
                try
                {
                    read = await reader.ReadAsync(operation.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The lower reader deliberately preserves unexpected local
                    // provider exceptions. This composed boundary exposes none.
                    result = new(ClientControlReceiveStatus.ReadFailure,
                        WebSocketControlReadStatus.TransportFailure)
                    {
                        Terminal = true,
                    };
                    throw new ControlReceiveFailureException();
                }
                if (!read.Received)
                {
                    var status = read.Status == WebSocketControlReadStatus.Cancelled
                        ? operation.CancellationStatus == AuthenticationIoCancellation.Cancelled
                            ? ClientControlReceiveStatus.Cancelled
                            : ClientControlReceiveStatus.InactiveConnection
                        : ClientControlReceiveStatus.ReadFailure;
                    result = new(status, read.Status)
                    {
                        Terminal = true,
                        AbortFailed = read.AbortFailed,
                    };
                    abortNeeded = read.Status != WebSocketControlReadStatus.RemoteClosed && !reader.IsTerminated;
                    throw new ControlReceiveFailureException();
                }
                payload = read.Payload;
            }

            if (cancellationToken.IsCancellationRequested)
                operation.Cancel(AuthenticationIoCancellation.Cancelled);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsControlCurrent(selected, operation, binding))
            {
                result = new(ClientControlReceiveStatus.InactiveConnection) { Terminal = true };
                abortNeeded = true;
                throw new ControlReceiveFailureException();
            }

            var dispatched = control.Receive(binding, payload.Value);
            var response = RouteCorrelated(selected.Connection, binding, ref dispatched);
            if (dispatched.Accepted)
            {
                keepConnection = true;
                result = new(ClientControlReceiveStatus.Accepted,
                    WebSocketControlReadStatus.MessageReceived, dispatched)
                {
                    Response = response,
                };
            }
            else if (dispatched.Status is ReceiverControlReceiveStatus.Busy or
                ReceiverControlReceiveStatus.RejectedByReceiver &&
                attempts + 1 < controlLimits.MaximumDispatchAttempts)
            {
                keepConnection = true;
                attempts++;
                result = new(ClientControlReceiveStatus.Backpressure,
                    WebSocketControlReadStatus.MessageReceived, dispatched);
            }
            else
            {
                result = new(ClientControlReceiveStatus.DispatchFailure,
                    WebSocketControlReadStatus.MessageReceived, dispatched)
                {
                    Terminal = true,
                };
                abortNeeded = true;
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            result = new(operation.CancellationStatus == AuthenticationIoCancellation.Cancelled
                ? ClientControlReceiveStatus.Cancelled
                : ClientControlReceiveStatus.InactiveConnection)
            {
                Terminal = true,
            };
            abortNeeded = true;
        }
        catch (ControlReceiveFailureException)
        {
            // Result and abort ownership were selected at the exact failure site.
        }
        finally
        {
            if (result.Terminal)
                RetireControl(selected, operation);
            if (abortNeeded)
            {
                try { socket.Abort(); }
                catch (Exception) { abortFailed = true; }
            }
            await registration.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                operation.Cancel(AuthenticationIoCancellation.Cancelled);
            callbackFailed = await operation.DrainAsync().ConfigureAwait(false);
            var completed = CompleteControlReceive(
                selected,
                operation,
                keepConnection,
                result.RetryRequired ? payload : null,
                result.RetryRequired ? attempts : 0);
            if (keepConnection && !completed)
                result = new(ClientControlReceiveStatus.InactiveConnection) { Terminal = true };
        }
        return result with
        {
            AbortFailed = result.AbortFailed || abortFailed,
            CancellationCallbacksFailed = callbackFailed,
        };
    }

    /// <summary>
    /// Matches a received reply, or an event frame of a requested batch, to
    /// its pending tracked request. A snapshot completion is staged into the
    /// receiver before the request completes, so a busy receiver retries the
    /// same frame without consuming the request. Anything that cannot be
    /// matched is refused, which ends the connection.
    /// </summary>
    private ClientControlResponseResult? RouteCorrelated(
        ClientAuthenticationConnection source,
        ReceiverControlBinding binding,
        ref ReceiverControlReceiveResult dispatched)
    {
        if (dispatched.Message is not { } message)
            return null;
        var requestId = message.Envelope.MessageId;
        if (dispatched.Status == ReceiverControlReceiveStatus.Reply)
        {
            if (!TryFindControlRequest(source, binding, requestId, out var receipt) || receipt is null)
            {
                dispatched = new(ReceiverControlReceiveStatus.UnmatchedReply);
                return null;
            }
            if (message.Payload is ObjectSnapshotCompletion snapshot &&
                receipt.Kind == ClientControlRequestKind.Snapshot)
            {
                var staged = control.StageCorrelatedSnapshot(binding, snapshot.Snapshot);
                if (!staged.Accepted)
                {
                    dispatched = staged;
                    return null;
                }
                dispatched = dispatched with { Snapshot = staged.Snapshot };
            }
            var completed = ReceiveCandidateControlResponse(source, binding, receipt, message);
            dispatched = completed.Completed
                ? dispatched with { Status = ReceiverControlReceiveStatus.Accepted }
                : new(ReceiverControlReceiveStatus.UnmatchedReply);
            return completed;
        }

        // Event frames of a requested batch carry the request ID; the registry
        // checks their count and order so the completion marker can be verified.
        if (dispatched.Accepted && dispatched.Event is not null &&
            TryFindControlRequest(source, binding, requestId, out var batch) && batch is not null)
        {
            var observed = ReceiveCandidateControlResponse(source, binding, batch, message);
            if (observed.Status != ClientControlResponseStatus.EventObserved)
                dispatched = new(ReceiverControlReceiveStatus.UnmatchedReply);
            return observed;
        }
        return null;
    }

    private bool IsControlCurrent(
        Entry selected,
        AuthenticationIoOperation operation,
        ReceiverControlBinding binding)
    {
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) ||
                !ReferenceEquals(selected.ControlOperation, operation) ||
                !ReferenceEquals(selected.Binding, binding) ||
                terminated || selected.Retired || !selected.Established ||
                operation.Token.IsCancellationRequested || !readiness.IsReady ||
                selected.Session is not { } session)
                return false;
            var current = lifecycle.Current;
            return HasActiveAuthority(current, session);
        }
    }

    private bool CompleteControlReceive(
        Entry selected,
        AuthenticationIoOperation operation,
        bool keepConnection,
        ReadOnlyMemory<byte>? pending,
        int attempts)
    {
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) ||
                !ReferenceEquals(selected.ControlOperation, operation))
                return false;
            var current = lifecycle.Current;
            var accepted = keepConnection && !terminated && !selected.Retired &&
                !operation.Token.IsCancellationRequested && readiness.IsReady &&
                selected.Established && selected.Session is { } session &&
                HasActiveAuthority(current, session);
            if (accepted)
            {
                selected.PendingControlPayload = pending;
                selected.PendingControlAttempts = attempts;
            }
            else
            {
                selected.PendingControlPayload = null;
                selected.PendingControlAttempts = 0;
                RetireUnsafe(selected);
            }
            selected.ControlOperation = null;
            TryReleaseUnsafe(selected);
            return accepted;
        }
    }

    private void RetireControl(Entry selected, AuthenticationIoOperation operation)
    {
        lock (gate)
        {
            if (ReferenceEquals(entry, selected) &&
                ReferenceEquals(selected.ControlOperation, operation))
                RetireUnsafe(selected);
        }
    }

    private async Task<ClientAuthenticationResult> AuthenticateOwnedAsync(Entry selected,
        AuthenticationIoOperation operation, WebSocket socket, byte[] request,
        TimeSpan remaining, CancellationToken cancellationToken)
    {
        var response = new byte[AuthenticationMessageCodec.MaximumResponseBytes + 1];
        Timer? timer = null;
        CancellationTokenRegistration registration = default;
        var result = new ClientAuthenticationResult(ClientAuthenticationStatus.TransportFailure);
        var abortFailed = false;
        var callbackFailed = false;
        var readyToEstablish = false;
        var accepted = false;
        ReceiverSessionKey? activated = null;
        ReceiverControlBinding? binding = null;
        try
        {
            registration = cancellationToken.UnsafeRegister(static state =>
                ((AuthenticationIoOperation)state!).Cancel(AuthenticationIoCancellation.Cancelled), operation);
            timer = new Timer(static state =>
                ((AuthenticationIoOperation)state!).Cancel(AuthenticationIoCancellation.TimedOut),
                operation, remaining, Timeout.InfiniteTimeSpan);
            operation.Token.ThrowIfCancellationRequested();
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, operation.Token).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) operation.Cancel(AuthenticationIoCancellation.Cancelled);
            operation.Token.ThrowIfCancellationRequested();

            var length = 0;
            result = new(ClientAuthenticationStatus.SegmentLimitExceeded);
            for (var segment = 0; segment < limits.MaximumReceiveSegments; segment++)
            {
                var received = await socket.ReceiveAsync(response.AsMemory(length), operation.Token).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) operation.Cancel(AuthenticationIoCancellation.Cancelled);
                operation.Token.ThrowIfCancellationRequested();
                if (received.Count < 0 || received.Count > response.Length - length)
                {
                    result = new(ClientAuthenticationStatus.TransportFailure);
                    break;
                }
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    result = new(received.MessageType == WebSocketMessageType.Close
                        ? ClientAuthenticationStatus.RemoteClosed : ClientAuthenticationStatus.UnexpectedMessageType);
                    break;
                }
                if (received.Count > AuthenticationMessageCodec.MaximumResponseBytes - length)
                {
                    result = new(ClientAuthenticationStatus.MessageTooLarge);
                    break;
                }
                length += received.Count;
                if (!received.EndOfMessage) continue;
                if (!AuthenticationMessageCodec.TryReadAuthorization(response.AsSpan(0, length), out var authorization))
                {
                    result = new(ClientAuthenticationStatus.InvalidAuthorization);
                    break;
                }
                if (!IsCurrent(selected, operation))
                {
                    result = new(ClientAuthenticationStatus.InactiveConnection);
                    break;
                }
                var connected = await lifecycle.ConnectAsync(
                    authorization.SessionId, authorization.Generation, operation.Token).ConfigureAwait(false);
                if (connected.Status != SyncReceiverConnectStatus.Activated || connected.Session is not { } local)
                {
                    result = new(ClientAuthenticationStatus.ActivationRejected) { ConnectStatus = connected.Status };
                    break;
                }
                activated = local;
                StoreActivated(selected, operation, local);
                if (!IsCurrent(selected, operation))
                {
                    result = new(ClientAuthenticationStatus.InactiveConnection);
                    break;
                }
                var bound = control.BindVerifiedConnection(local,
                    new(authorization.SessionId, authorization.Generation, authorization.ConnectionEpoch));
                if (!bound.Bound)
                {
                    result = new(ClientAuthenticationStatus.BindingRejected) { BindStatus = bound.Status };
                    break;
                }
                binding = bound.Binding;
                readyToEstablish = true;
                result = new(ClientAuthenticationStatus.Activated, local, binding) { UserId = authorization.UserId, DisplayName = authorization.DisplayName };
                break;
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            result = new(operation.CancellationStatus switch
            {
                AuthenticationIoCancellation.Cancelled => ClientAuthenticationStatus.Cancelled,
                AuthenticationIoCancellation.TimedOut => ClientAuthenticationStatus.TimedOut,
                _ => ClientAuthenticationStatus.InactiveConnection,
            });
        }
        catch (Exception)
        {
            result = new(ClientAuthenticationStatus.TransportFailure);
        }
        finally
        {
            if (!readyToEstablish)
            {
                Retire(selected, operation);
                try { socket.Abort(); }
                catch (Exception) { abortFailed = true; }
            }
            if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
            await registration.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) operation.Cancel(AuthenticationIoCancellation.Cancelled);
            callbackFailed = await operation.DrainAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(request);
            CryptographicOperations.ZeroMemory(response);
            accepted = End(selected, operation,
                readyToEstablish && !cancellationToken.IsCancellationRequested, activated, binding);
        }
        if (!accepted && activated is not null)
        {
            var cleanup = await CleanupAsync(selected).ConfigureAwait(false);
            if (cleanup != CleanupStatus.Succeeded)
                result = new(ClientAuthenticationStatus.CleanupRequired);
        }
        if (result.Status == ClientAuthenticationStatus.Activated && !accepted)
            result = new(ClientAuthenticationStatus.InactiveConnection);
        return result with { AbortFailed = abortFailed, CancellationCallbacksFailed = callbackFailed };
    }

    private bool IsCurrent(Entry selected, AuthenticationIoOperation operation)
    {
        var elapsed = SampleElapsed(selected.StartedAt);
        lock (gate)
        {
            return ReferenceEquals(entry, selected) && ReferenceEquals(selected.Operation, operation) &&
                !terminated && !selected.Retired && !operation.Token.IsCancellationRequested &&
                !IsExpired(elapsed) && readiness.IsReady;
        }
    }

    private void StoreActivated(Entry selected, AuthenticationIoOperation operation, ReceiverSessionKey session)
    {
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) || !ReferenceEquals(selected.Operation, operation)) return;
            selected.Session = session;
            // A close that observed no session cannot be the cleanup proof for
            // this late activation. Reopen that exact obligation.
            selected.Cleaned = false;
        }
    }

    private bool End(Entry selected, AuthenticationIoOperation operation, bool success,
        ReceiverSessionKey? session, ReceiverControlBinding? binding)
    {
        var elapsed = SampleElapsed(selected.StartedAt);
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected) || !ReferenceEquals(selected.Operation, operation)) return false;
            var current = lifecycle.Current;
            var accepted = success && session is { } active && binding is not null &&
                !terminated && !selected.Retired && !operation.Token.IsCancellationRequested &&
                !IsExpired(elapsed) && readiness.IsReady && selected.Session == session &&
                HasActiveAuthority(current, active);
            if (accepted)
            {
                selected.Binding = binding;
                selected.Requests = new(binding!, requestLimits);
                selected.Established = true;
            }
            else RetireUnsafe(selected);
            selected.Operation = null;
            TryReleaseUnsafe(selected);
            return accepted;
        }
    }

    private void Retire(Entry selected, AuthenticationIoOperation operation)
    {
        lock (gate)
        {
            if (ReferenceEquals(entry, selected) && ReferenceEquals(selected.Operation, operation)) RetireUnsafe(selected);
        }
    }

    private async Task<CleanupStatus> CleanupAsync(Entry selected)
    {
        ReceiverSessionKey? session;
        lock (gate)
        {
            if (!ReferenceEquals(entry, selected)) return CleanupStatus.Succeeded;
            if (selected.Cleaning) return CleanupStatus.Pending;
            if (selected.Cleaned) return CleanupStatus.Succeeded;
            selected.Cleaning = true;
            session = selected.Session;
        }
        var cleaned = session is null;
        try
        {
            if (session is { } active)
            {
                var result = await DisconnectOrRetryCleanupAsync(active).ConfigureAwait(false);
                cleaned = result.Status is SyncReceiverDisconnectStatus.Disconnected or
                    SyncReceiverDisconnectStatus.InactiveSession or SyncReceiverDisconnectStatus.Terminated;
            }
        }
        catch (Exception) { }
        try { BeforeCleanupCompletion?.Invoke(session); }
        catch (Exception) { }
        lock (gate)
        {
            selected.Cleaning = false;
            // StoreActivated can publish the session after a concurrent close
            // sampled null. That publication reopens the exact cleanup
            // obligation. Completion for the older snapshot must not prove the
            // newer session clean or release its sole retained owner.
            if (selected.Session != session)
            {
                selected.Cleaned = false;
                TryReleaseUnsafe(selected);
                return CleanupStatus.Pending;
            }
            selected.Cleaned = cleaned;
            TryReleaseUnsafe(selected);
            return cleaned ? CleanupStatus.Succeeded : CleanupStatus.Failed;
        }
    }

    private async Task<SyncReceiverDisconnectResult> DisconnectOrRetryCleanupAsync(
        ReceiverSessionKey session)
    {
        var snapshot = lifecycle.Current;
        var result = snapshot.State == SyncReceiverLifecycleState.CleanupRequired &&
            snapshot.CleanupSession == session
                ? await lifecycle.RetryCleanupAsync(session, CancellationToken.None)
                    .ConfigureAwait(false)
                : await lifecycle.DisconnectAsync(session, CancellationToken.None)
                    .ConfigureAwait(false);

        if (result.Status != SyncReceiverDisconnectStatus.InactiveSession)
            return result;

        // Cleanup may have been owned by another serialized lifecycle caller
        // when the close began. If it converged to a retained exact-session
        // obligation before ordinary disconnect entered, advance that retry
        // instead of treating InactiveSession as proof that cleanup finished.
        snapshot = lifecycle.Current;
        return snapshot.State == SyncReceiverLifecycleState.CleanupRequired &&
            snapshot.CleanupSession == session
                ? await lifecycle.RetryCleanupAsync(session, CancellationToken.None)
                    .ConfigureAwait(false)
                : result;
    }

    private TimeSpan SampleElapsed(long startedAt) =>
        time.GetElapsedTime(startedAt, time.GetTimestamp());

    private bool IsExpired(TimeSpan elapsed) =>
        elapsed < TimeSpan.Zero || elapsed >= limits.HandshakeTimeout;

    private static void RetireUnsafe(Entry selected)
    {
        selected.Retired = true;
        selected.Requests?.Retire();
        selected.Operation?.Cancel(AuthenticationIoCancellation.InactiveConnection);
        selected.ControlOperation?.Cancel(AuthenticationIoCancellation.InactiveConnection);
        selected.ControlSendOperation?.Cancel(AuthenticationIoCancellation.InactiveConnection);
        selected.PendingControlPayload = null;
        selected.PendingControlAttempts = 0;
    }

    private bool TryReleaseUnsafe(Entry selected)
    {
        if (!ReferenceEquals(entry, selected) || !selected.TransportClosed || selected.Operation is not null ||
            selected.ControlOperation is not null ||
            selected.ControlSendOperation is not null ||
            selected.Cleaning || !selected.Cleaned) return false;
        entry = null;
        return true;
    }

    private sealed class Entry(ClientAuthenticationConnection connection, object transport, long startedAt)
    {
        internal ClientAuthenticationConnection Connection { get; } = connection;
        internal object Transport { get; } = transport;
        internal long StartedAt { get; } = startedAt;
        internal AuthenticationIoOperation? Operation { get; set; }
        internal AuthenticationIoOperation? ControlOperation { get; set; }
        internal AuthenticationIoOperation? ControlSendOperation { get; set; }
        internal bool Started { get; set; }
        internal bool Established { get; set; }
        internal bool Retired { get; set; }
        internal bool TransportClosed { get; set; }
        internal bool Cleaning { get; set; }
        internal bool Cleaned { get; set; }
        internal ReceiverSessionKey? Session { get; set; }
        internal ReceiverControlBinding? Binding { get; set; }
        internal ClientControlRequestRegistry? Requests { get; set; }
        internal bool? TrackedRequests { get; set; }
        internal ReadOnlyMemory<byte>? PendingControlPayload { get; set; }
        internal int PendingControlAttempts { get; set; }
    }

    private enum CleanupStatus { Succeeded, Pending, Failed }
    private sealed class ControlReceiveFailureException : Exception { }
    private sealed class ControlSendFailureException : Exception { }
}
