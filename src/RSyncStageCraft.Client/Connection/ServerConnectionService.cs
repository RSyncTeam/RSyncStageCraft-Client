using System.Net.WebSockets;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Core.Connection;

public enum ServerConnectionState
{
    /// <summary>The user has not asked to connect.</summary>
    Disabled,
    /// <summary>Address or access key missing or malformed.</summary>
    NotConfigured,
    /// <summary>Penumbra or Glamourer is not ready yet.</summary>
    WaitingForDependencies,
    Connecting,
    /// <summary>Signed in; loading the scene from the server.</summary>
    Syncing,
    /// <summary>Signed in and caught up with the server.</summary>
    SyncReady,
    /// <summary>The server refused the sign-in. Not retried until settings change or Reconnect.</summary>
    SignInRefused,
    /// <summary>The server could not be reached or the connection dropped; retrying.</summary>
    Unreachable,
    Stopped,
}

/// <summary>Point-in-time status for the UI. Contains no secret.</summary>
public sealed record ServerConnectionStatus(
    ServerConnectionState State,
    string Detail,
    int HeldObjects = 0,
    long? Watermark = null,
    DateTimeOffset? RetryAt = null)
{
    public bool IsConnected => State is ServerConnectionState.Syncing or ServerConnectionState.SyncReady;
}

public sealed record ServerConnectionOptions
{
    public static ServerConnectionOptions Default { get; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>How often an up-to-date client asks for new events.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DependencyRecheckInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinimumBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public int MaximumEventsPerRequest { get; init; } = 64;
    /// <summary>Upper bound on receiver pumps spent admitting one snapshot.</summary>
    public int MaximumSnapshotPumps { get; init; } = 400;
    /// <summary>How often the receiver is pumped (applies server state to the game) while connected.</summary>
    public TimeSpan PumpInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Creates, updates and deletes that may wait to be sent at once.</summary>
    public int MaximumPendingCommands { get; init; } = 16;
}

/// <summary>
/// Keeps the plugin signed in to the configured RSC server: waits for the
/// required dependencies, signs in over wss://, loads the scene snapshot and
/// then pulls event batches after its cursor. The only network peer is the
/// configured endpoint. Server state is held in the receiver; whether it is
/// applied to the game is decided by apply readiness, not here.
/// </summary>
public sealed class ServerConnectionService : IAsyncDisposable
{
    private readonly RSyncStageCraftRuntime runtime;
    private readonly Func<ServerConnectionSettings> settings;
    private readonly Func<CancellationToken, Task<ReceiverDependencySnapshot>> dependencies;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> connect;
    private readonly TimeProvider time;
    private readonly ServerConnectionOptions options;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private TaskCompletionSource wake = NewWake();
    private ServerConnectionStatus status = new(ServerConnectionState.Disabled, "Not started.");
    private Task? loop;
    private bool reconnectRequested;
    private ClientAuthenticationConnection? unreleased;
    private readonly Queue<PendingServerCommand> commands = new();
    // Commands are accepted only while a session is synced.
    private bool acceptingCommands;
    private long sessionSerial;
    private RscUserId? selfUserId;
    private string? selfDisplayName;
    // The connection attempt in flight (connect, sign-in, snapshot or
    // polling). Disconnect, Reconnect and changed settings cancel it at once.
    private CancellationTokenSource? attempt;
    private string? attemptIdentity;

    public ServerConnectionService(
        RSyncStageCraftRuntime runtime,
        Func<ServerConnectionSettings> settings,
        Func<CancellationToken, Task<ReceiverDependencySnapshot>> dependencies,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect = null,
        TimeProvider? time = null,
        ServerConnectionOptions? options = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dependencies);
        this.runtime = runtime;
        this.settings = settings;
        this.dependencies = dependencies;
        this.connect = connect ?? ConnectClientWebSocketAsync;
        this.time = time ?? TimeProvider.System;
        this.options = options ?? ServerConnectionOptions.Default;
        this.log = log;
    }

    /// <summary>
    /// A number that identifies the current synced connection, or 0 while
    /// not synced. It changes on every new sign-in, so work tied to one
    /// connection can tell that the connection dropped or was replaced.
    /// </summary>
    public long SessionSerial
    {
        get
        {
            lock (gate)
                return acceptingCommands ? sessionSerial : 0;
        }
    }

    public ServerConnectionStatus Status
    {
        get
        {
            lock (gate)
                return status;
        }
    }

    /// <summary>
    /// The signed-in user's own stable RSC identity (protocol 6), from the last
    /// sign-in; null before one. The UI offers editing only for actors whose
    /// stable owner id equals it (<see cref="ActorOwnership"/>), never by
    /// display name; the server enforces owner-only editing regardless.
    /// </summary>
    public RscUserId? SelfUserId
    {
        get
        {
            lock (gate)
                return selfUserId;
        }
    }

    /// <summary>The signed-in account's optional display name (presentation only).</summary>
    public string? SelfDisplayName
    {
        get
        {
            lock (gate)
                return selfDisplayName;
        }
    }

    /// <summary>An actor owner's display name as the server last reported it, or null (presentation only).</summary>
    public string? OwnerDisplayName(RscUserId owner) => runtime.ControlIngress.OwnerNames.NameOf(owner);

    public void Start()
    {
        lock (gate)
        {
            if (loop is not null || stopping.IsCancellationRequested)
                return;
            loop = Task.Run(() => RunAsync(stopping.Token));
        }
    }

    /// <summary>
    /// Settings or dependencies may have changed: re-evaluate now. If the
    /// user disconnected or changed the address or key, the attempt in
    /// flight is cancelled immediately, wherever it is waiting.
    /// </summary>
    public void Wake()
    {
        CancelAttemptIfStale();
        TaskCompletionSource current;
        lock (gate)
        {
            current = wake;
            wake = NewWake();
        }
        current.TrySetResult();
    }

    /// <summary>
    /// A game-side host's readiness changed (for example the player arrived
    /// at a place, so furniture can be drawn again): check dependencies on
    /// the next poll instead of waiting for <see cref="ServerConnectionOptions.DependencyRecheckInterval"/>.
    /// </summary>
    public void RecheckDependencies()
    {
        Interlocked.Exchange(ref dependencyCheckRequested, 1);
        Wake();
    }

    private int dependencyCheckRequested;

    /// <summary>
    /// Sends one create, update or delete to the server on the current
    /// connection and returns the server's answer. The change becomes visible
    /// through the normal event stream like any other player's change.
    /// </summary>
    public Task<ServerCommandResult> SubmitAsync(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = ServerCommandRequests.RequestIdOf(request);
        if (requestId == Guid.Empty)
            return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Invalid));
        var pending = new PendingServerCommand(request, requestId);
        lock (gate)
        {
            if (!acceptingCommands)
                return Task.FromResult(new ServerCommandResult(ServerCommandStatus.NotConnected));
            if (commands.Count >= options.MaximumPendingCommands)
                return Task.FromResult(new ServerCommandResult(ServerCommandStatus.Busy));
            commands.Enqueue(pending);
        }
        Wake();
        return cancellationToken.CanBeCanceled
            ? pending.Completion.Task.WaitAsync(cancellationToken)
            : pending.Completion.Task;
    }

    /// <summary>Drop the current connection (if any) and connect again, also after a refusal.</summary>
    public void Reconnect()
    {
        CancellationTokenSource? inFlight;
        lock (gate)
        {
            reconnectRequested = true;
            inFlight = attempt;
        }
        Cancel(inFlight);
        Wake();
    }

    private void CancelAttemptIfStale()
    {
        CancellationTokenSource? inFlight;
        string? inFlightIdentity;
        lock (gate)
        {
            inFlight = attempt;
            inFlightIdentity = attemptIdentity;
        }
        if (inFlight is null)
            return;
        ServerConnectionSettings current;
        try
        {
            current = settings();
        }
        catch (Exception)
        {
            return;
        }
        if (!current.Enabled || Identity(current) != inFlightIdentity)
            Cancel(inFlight);
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task StopAsync()
    {
        Task? running;
        lock (gate)
        {
            running = loop;
            if (!stopping.IsCancellationRequested)
                stopping.Cancel();
        }
        Wake();
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Publish(new(ServerConnectionState.Stopped, "Stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    private static TaskCompletionSource NewWake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;
        var dropsInARow = 0;
        string? refusedFor = null;
        string? droppedFor = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var wakeTask = WakeTask();
                var current = settings();
                var forced = TakeReconnectRequest();
                if (forced)
                {
                    // An explicit Reconnect is a fresh start, including the
                    // one-retry allowance for a dropped sign-in.
                    refusedFor = null;
                    droppedFor = null;
                    dropsInARow = 0;
                }

                if (!current.Enabled)
                {
                    Publish(new(ServerConnectionState.Disabled, "Not connected. Press Connect to sign in."));
                    failures = 0;
                    await WaitAsync(Timeout.InfiniteTimeSpan, wakeTask, token).ConfigureAwait(false);
                    continue;
                }
                if (!ServerConnectionSettings.TryParseEndpoint(current.Endpoint, out var endpoint, out var problem))
                {
                    Publish(new(ServerConnectionState.NotConfigured, problem));
                    await WaitAsync(Timeout.InfiniteTimeSpan, wakeTask, token).ConfigureAwait(false);
                    continue;
                }
                if (!current.HasWellFormedAccessKey)
                {
                    Publish(new(ServerConnectionState.NotConfigured,
                        current.HasAccessKey
                            ? ConnectionText.KeyNotValid
                            : "Enter your access key."));
                    await WaitAsync(Timeout.InfiniteTimeSpan, wakeTask, token).ConfigureAwait(false);
                    continue;
                }
                var identity = Identity(current);
                if (refusedFor == identity)
                {
                    await WaitAsync(Timeout.InfiniteTimeSpan, wakeTask, token).ConfigureAwait(false);
                    continue;
                }

                var deps = await dependencies(token).ConfigureAwait(false);
                await runtime.UpdateReadinessAsync(deps, token).ConfigureAwait(false);
                if (!deps.CanConnect)
                {
                    Publish(new(ServerConnectionState.WaitingForDependencies, DescribeMissing(deps)));
                    await WaitAsync(options.DependencyRecheckInterval, wakeTask, token).ConfigureAwait(false);
                    continue;
                }

                Publish(new(ServerConnectionState.Connecting, $"Connecting to {endpoint!.Host}..."));
                var outcome = await RunAttemptAsync(
                        endpoint, current, identity, () => failures = dropsInARow = 0, token)
                    .ConfigureAwait(false);
                if (outcome.Kind == OutcomeKind.Dropped)
                {
                    dropsInARow = droppedFor == identity ? dropsInARow + 1 : 1;
                    droppedFor = identity;
                    outcome = dropsInARow >= 2
                        ? Outcome.Refused(outcome.Detail)
                        : Outcome.Lost("The connection closed during sign-in; trying once more.");
                }
                switch (outcome.Kind)
                {
                    case OutcomeKind.Refused:
                        refusedFor = identity;
                        Publish(new(ServerConnectionState.SignInRefused, outcome.Detail));
                        break;
                    case OutcomeKind.Restart:
                        // Settings changed, dependencies changed, or a fresh
                        // snapshot is needed: go round again almost at once.
                        await WaitAsync(TimeSpan.FromMilliseconds(250), WakeTask(), token).ConfigureAwait(false);
                        break;
                    case OutcomeKind.Lost:
                        failures++;
                        var delay = Backoff(failures);
                        Publish(new(ServerConnectionState.Unreachable, outcome.Detail,
                            RetryAt: time.GetUtcNow() + delay));
                        await WaitAsync(delay, WakeTask(), token).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never let the loop die; never echo provider details that
                // could include addresses beyond the configured host.
                failures++;
                var delay = Backoff(failures);
                log?.Invoke($"RSC connection loop error: {exception.GetType().Name}");
                Publish(new(ServerConnectionState.Unreachable,
                    "Unexpected error; retrying.", RetryAt: time.GetUtcNow() + delay));
                try { await WaitAsync(delay, WakeTask(), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        await ReleaseUnreleasedAsync().ConfigureAwait(false);
    }

    private async Task<Outcome> RunAttemptAsync(
        Uri endpoint,
        ServerConnectionSettings current,
        string identity,
        Action signedIn,
        CancellationToken token)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (gate)
        {
            attempt = source;
            attemptIdentity = identity;
        }
        try
        {
            // Settings may have changed between reading them and publishing
            // this attempt; a Wake in that window found nothing to cancel.
            CancelAttemptIfStale();
            return await RunConnectionAsync(endpoint, current, identity, signedIn, source.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            source.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return Outcome.Restart();
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(attempt, source))
                {
                    attempt = null;
                    attemptIdentity = null;
                }
            }
        }
    }

    private async Task<Outcome> RunConnectionAsync(
        Uri endpoint,
        ServerConnectionSettings current,
        string identity,
        Action signedIn,
        CancellationToken token)
    {
        if (!await ReleaseUnreleasedAsync().ConfigureAwait(false))
            return Outcome.Lost(ConnectionText.FinishingPrevious);

        WebSocket socket;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(options.ConnectTimeout);
            socket = await connect(endpoint, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Outcome.Lost(ConnectionText.CouldNotReach(endpoint.Host));
        }

        using (socket)
        {
            var authentication = runtime.Authentication;
            var begun = authentication.BeginTrustedTransport(socket);
            if (begun.Connection is not { } connection)
            {
                Abort(socket);
                log?.Invoke($"RSC local session not ready ({begun.Status}).");
                return Outcome.Lost(ConnectionText.SignInUnfinished);
            }

            try
            {
                var signIn = await authentication.AuthenticateAsync(
                        connection, socket, current.AccessKey.AsSpan(), token)
                    .ConfigureAwait(false);
                if (!signIn.Activated || signIn.Session is not { } session)
                {
                    token.ThrowIfCancellationRequested();
                    return SignInFailed(signIn.Status, socket);
                }

                signedIn();
                lock (gate)
                {
                    selfUserId = signIn.UserId is { } user && new RscUserId(user).IsValid ? new RscUserId(user) : null;
                    selfDisplayName = selfUserId is not null && RscDisplayName.IsValid(signIn.DisplayName) ? signIn.DisplayName : null;
                }
                log?.Invoke($"RSC signed in to {endpoint.Host}.");
                Publish(new(ServerConnectionState.Syncing, "Signed in. Loading the scene..."));
                var active = new Connected(connection, socket, session, identity, endpoint.Host);
                return await SyncAsync(active, token).ConfigureAwait(false);
            }
            finally
            {
                await CloseAsync(connection, socket).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Shown when the server drops the sign-in without a reason. The
    /// server never says why, so the text names every cause: a reconnect
    /// while the previous connection is still closing, a wrong or revoked
    /// key, a plugin/server version mismatch, or a full two-player group.
    /// </summary>
    internal const string SignInRefusedText =
        "The server refused the sign-in. If you just reconnected, your previous connection may still be closing: " +
        "wait a moment, then press Reconnect. Otherwise your access key may be wrong or revoked, the plugin and " +
        "server versions may differ (update the plugin), or the server already has its two players. Ask the host.";

    private Outcome SignInFailed(ClientAuthenticationStatus status, WebSocket socket)
    {
        switch (status)
        {
            case ClientAuthenticationStatus.InvalidCredential:
                return Outcome.Refused(ConnectionText.KeyNotValid);
            case ClientAuthenticationStatus.DependenciesUnavailable:
                return Outcome.Restart();
            case ClientAuthenticationStatus.RemoteClosed or ClientAuthenticationStatus.InvalidAuthorization or
                ClientAuthenticationStatus.TransportFailure:
                // The server drops a refused sign-in without giving a reason
                // (no oracle for key guessing). A single drop may be the
                // network, so the caller retries once before calling it refused.
                return Outcome.Dropped(SignInRefusedText);
            default:
                log?.Invoke(
                    $"RSC sign-in did not complete ({status}{(socket.CloseStatus is { } close ? $", closed {close}" : string.Empty)}).");
                return Outcome.Lost(ConnectionText.SignInUnfinished);
        }
    }

    private async Task<Outcome> SyncAsync(Connected connected, CancellationToken token)
    {
        var loaded = await LoadSnapshotAsync(connected, token).ConfigureAwait(false);
        if (loaded.Outcome is { } failed)
            return failed;

        var cursor = loaded.Watermark;
        var nextDependencyCheck = time.GetUtcNow() + options.DependencyRecheckInterval;
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumping = PumpLoopAsync(connected.Session, pumpStop.Token);
        lock (gate)
        {
            acceptingCommands = true;
            sessionSerial++;
        }
        try
        {
            return await PollAsync(connected, cursor, nextDependencyCheck, token).ConfigureAwait(false);
        }
        finally
        {
            FailPendingCommands();
            pumpStop.Cancel();
            try
            {
                await pumping.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<Outcome> PollAsync(
        Connected connected,
        ServerSequence cursor,
        DateTimeOffset nextDependencyCheck,
        CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (CheckSettings(connected) is { } changed)
                return changed;
            if (await SendPendingCommandsAsync(connected, token).ConfigureAwait(false) is { } commandLost)
                return commandLost;
            if (Interlocked.Exchange(ref dependencyCheckRequested, 0) == 1 ||
                time.GetUtcNow() >= nextDependencyCheck)
            {
                var deps = await dependencies(token).ConfigureAwait(false);
                await runtime.UpdateReadinessAsync(deps, token).ConfigureAwait(false);
                if (!deps.CanConnect)
                    return Outcome.Restart();
                nextDependencyCheck = time.GetUtcNow() + options.DependencyRecheckInterval;
            }

            var request = new ObjectEventBatchRequest(Guid.NewGuid(), cursor, options.MaximumEventsPerRequest);
            var reply = await RequestAsync(connected, request, request.RequestId, token).ConfigureAwait(false);
            if (reply.Outcome is { } lost)
                return lost;

            switch (reply.Result)
            {
                case ObjectEventBatchCompletion batch:
                    switch (batch.Status)
                    {
                        case ObjectEventBatchCompletionStatus.Advanced:
                            cursor = batch.DeliveredThrough;
                            PublishSync(connected, cursor, ready: !batch.HasMore);
                            if (batch.HasMore)
                                continue;
                            break;
                        case ObjectEventBatchCompletionStatus.UpToDate:
                            PublishSync(connected, cursor, ready: true);
                            break;
                        default:
                            // The server can no longer continue from this
                            // cursor. A session admits one snapshot, so start
                            // a fresh session for a fresh snapshot.
                            log?.Invoke($"RSC resync needed ({batch.Status}).");
                            return Outcome.Restart();
                    }
                    break;
                case ObjectRequestRejected rejected:
                    log?.Invoke($"RSC event request refused ({rejected.Reason}).");
                    return Outcome.Lost(ConnectionText.LostConnection);
                default:
                    log?.Invoke("RSC unexpected reply to the event request.");
                    return Outcome.Lost(ConnectionText.LostConnection);
            }

            await WaitAsync(options.PollInterval, WakeTask(), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pumps the receiver for the whole connection, so server state is
    /// applied to the game as soon as its apply domain is ready. It runs
    /// beside the poll loop because one pump may wait for a model to load,
    /// which must not hold up event polling or sending changes.
    /// </summary>
    private async Task PumpLoopAsync(ReceiverSessionKey session, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pumped = await runtime.PumpReceiverAsync(session, time.GetUtcNow(), token)
                    .ConfigureAwait(false);
                if (pumped.Status is ReceiverActorPumpStatus.InactiveSession)
                    return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log?.Invoke($"RSC apply pump error: {exception.GetType().Name}");
            }
            try
            {
                await Task.Delay(options.PumpInterval, time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<Outcome?> SendPendingCommandsAsync(Connected connected, CancellationToken token)
    {
        while (true)
        {
            PendingServerCommand? next;
            lock (gate)
            {
                if (!commands.TryDequeue(out next))
                    break;
            }
            if (next.Completion.Task.IsCompleted)
                continue;
            var reply = await RequestAsync(connected, next.Request, next.RequestId, token).ConfigureAwait(false);
            if (reply.Outcome is { } lost)
            {
                next.Completion.TrySetResult(new(ServerCommandStatus.NotConnected, Detail: lost.Detail));
                return lost;
            }
            next.Completion.TrySetResult(reply.Result switch
            {
                ObjectMutationCompletion completion => new(ServerCommandStatus.Completed, completion),
                ObjectRequestRejected rejected => new(ServerCommandStatus.Rejected, RejectReason: rejected.Reason),
                _ => new(ServerCommandStatus.Rejected, Detail: "Unexpected reply."),
            });
        }
        // The poll that follows pulls the resulting events right away.
        return null;
    }

    private void FailPendingCommands()
    {
        PendingServerCommand[] pending;
        lock (gate)
        {
            acceptingCommands = false;
            pending = commands.ToArray();
            commands.Clear();
        }
        foreach (var command in pending)
            command.Completion.TrySetResult(new(ServerCommandStatus.NotConnected));
    }

    private async Task<(ServerSequence Watermark, Outcome? Outcome)> LoadSnapshotAsync(
        Connected connected,
        CancellationToken token)
    {
        var request = new ObjectSnapshotRequest(Guid.NewGuid());
        var reply = await RequestAsync(connected, request, request.RequestId, token).ConfigureAwait(false);
        if (reply.Outcome is { } failed)
            return (default, failed);
        ServerSequence watermark;
        switch (reply.Result)
        {
            case ObjectSnapshotCompletion completion:
                watermark = completion.Snapshot.Watermark;
                break;
            case ObjectRequestRejected rejected:
                log?.Invoke($"RSC scene request refused ({rejected.Reason}).");
                return (default, Outcome.Lost(ConnectionText.SceneNotLoaded));
            default:
                log?.Invoke("RSC unexpected reply to the scene request.");
                return (default, Outcome.Lost(ConnectionText.SceneNotLoaded));
        }

        // The snapshot is staged; admit it into the receiver. Admission is
        // state intake only; nothing is applied without apply readiness.
        for (var pump = 0; pump < options.MaximumSnapshotPumps; pump++)
        {
            if (runtime.ReceiverState.PendingSnapshotAdmissions == 0 &&
                runtime.Receiver.Events.IsInitializedForSession(connected.Session))
            {
                PublishSync(connected, watermark, ready: false);
                return (watermark, null);
            }
            var pumped = await runtime.PumpReceiverAsync(connected.Session, time.GetUtcNow(), token)
                .ConfigureAwait(false);
            if (pumped.Status is ReceiverActorPumpStatus.InactiveSession)
                return (default, Outcome.Lost(ConnectionText.LostConnection));
            if (pumped.Snapshot is { Accepted: false } refused &&
                refused.Status != ReceiverActorSnapshotStatus.AdmissionBackpressure)
            {
                log?.Invoke($"RSC scene could not be loaded ({refused.Status}).");
                return (default, Outcome.Lost(ConnectionText.SceneNotLoaded));
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), time, token).ConfigureAwait(false);
        }
        log?.Invoke("RSC scene admission did not finish within the pump limit.");
        return (default, Outcome.Lost(ConnectionText.SceneNotLoaded));
    }

    private async Task<(object? Result, Outcome? Outcome)> RequestAsync(
        Connected connected,
        object request,
        Guid requestId,
        CancellationToken token)
    {
        var authentication = runtime.Authentication;
        var sent = await authentication.SendTrackedControlAsync(
                connected.Connection, connected.Socket, request, token)
            .ConfigureAwait(false);
        if (!sent.Sent)
        {
            token.ThrowIfCancellationRequested();
            // Request IDs are single-use per connection and the per-connection
            // budget is finite by design: start a fresh connection when spent.
            if (sent.RequestRegistration == ClientControlRequestRegistrationStatus.RequestBudgetExhausted)
                return (null, Outcome.Restart());
            log?.Invoke($"RSC lost the connection while sending ({sent.Status}).");
            return (null, Outcome.Lost(ConnectionText.LostConnection));
        }

        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        replyTimeout.CancelAfter(options.ReplyTimeout);
        while (true)
        {
            var received = await authentication.ReceiveControlAsync(
                    connected.Connection, connected.Socket, replyTimeout.Token)
                .ConfigureAwait(false);
            if (received.RetryRequired)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), time, token).ConfigureAwait(false);
                continue;
            }
            if (!received.Accepted)
            {
                token.ThrowIfCancellationRequested();
                log?.Invoke(received.Status == ClientControlReceiveStatus.Cancelled
                    ? "RSC: the server did not answer in time."
                    : received.ReadStatus is WebSocketControlReadStatus.RemoteClosed
                        ? "RSC: the server closed the connection."
                        : $"RSC lost the connection ({received.Status}).");
                return (null, Outcome.Lost(ConnectionText.LostConnection));
            }
            if (received.Response is { Completed: true } completed &&
                completed.Receipt?.RequestId == requestId)
                return (completed.CommandResult, null);
        }
    }

    private Outcome? CheckSettings(Connected connected)
    {
        if (TakeReconnectRequest())
            return Outcome.Restart();
        var current = settings();
        return !current.Enabled || Identity(current) != connected.Identity
            ? Outcome.Restart()
            : null;
    }

    private void PublishSync(Connected connected, ServerSequence cursor, bool ready)
    {
        var state = runtime.ReceiverState;
        var held = state.DesiredLocalObjects;
        Publish(new(
            ready ? ServerConnectionState.SyncReady : ServerConnectionState.Syncing,
            ready ? $"Connected to {connected.Host}. Up to date." : $"Connected to {connected.Host}. Catching up...",
            held,
            cursor.Value));
    }

    private async Task CloseAsync(ClientAuthenticationConnection connection, WebSocket socket)
    {
        // Close the protocol owner first (it ends the session and its
        // cleanup), then the socket. A close that cannot finish now is
        // retried before the next connection attempt.
        try
        {
            var closed = await runtime.Authentication.CloseTransportAsync(connection).ConfigureAwait(false);
            if (closed.Found && !closed.Released)
            {
                lock (gate)
                    unreleased = connection;
            }
        }
        catch (Exception)
        {
            lock (gate)
                unreleased = connection;
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                Abort(socket);
            }
        }
        else
        {
            Abort(socket);
        }
    }

    private async Task<bool> ReleaseUnreleasedAsync()
    {
        ClientAuthenticationConnection? pending;
        lock (gate)
            pending = unreleased;
        if (pending is null)
            return true;
        try
        {
            var closed = await runtime.Authentication.CloseTransportAsync(pending).ConfigureAwait(false);
            if (!closed.Found || closed.Released)
            {
                lock (gate)
                {
                    if (ReferenceEquals(unreleased, pending))
                        unreleased = null;
                }
                return true;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    private static void Abort(WebSocket socket)
    {
        try { socket.Abort(); }
        catch (Exception) { }
    }

    private bool TakeReconnectRequest()
    {
        lock (gate)
        {
            var requested = reconnectRequested;
            reconnectRequested = false;
            return requested;
        }
    }

    private Task WakeTask()
    {
        lock (gate)
            return wake.Task;
    }

    private async Task WaitAsync(TimeSpan delay, Task wakeTask, CancellationToken token)
    {
        if (delay == Timeout.InfiniteTimeSpan)
        {
            await wakeTask.WaitAsync(token).ConfigureAwait(false);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sleep = Task.Delay(delay, time, linked.Token);
        await Task.WhenAny(sleep, wakeTask).ConfigureAwait(false);
        linked.Cancel();
        token.ThrowIfCancellationRequested();
    }

    private TimeSpan Backoff(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 10);
        var delay = TimeSpan.FromTicks(options.MinimumBackoff.Ticks << exponent);
        return delay > options.MaximumBackoff ? options.MaximumBackoff : delay;
    }

    private void Publish(ServerConnectionStatus next)
    {
        ServerConnectionStatus previous;
        lock (gate)
        {
            previous = status;
            status = next;
        }
        if (previous.State != next.State)
            log?.Invoke($"RSC connection: {next.State}. {next.Detail}");
    }

    // Changes to the address or key restart the connection. The key itself is
    // reduced to a hash so it is not kept in another string field.
    private static string Identity(ServerConnectionSettings current) =>
        current.Endpoint + "|" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(current.AccessKey)));

    // Only protocol compatibility gates connecting;
    // Penumbra and Glamourer are not required.
    private static string DescribeMissing(ReceiverDependencySnapshot deps) =>
        deps.ProtocolCompatible
            ? "Waiting for required plugins."
            : "Waiting for a compatible protocol.";

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri endpoint, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed record Connected(
        ClientAuthenticationConnection Connection,
        WebSocket Socket,
        ReceiverSessionKey Session,
        string Identity,
        string Host);

    private enum OutcomeKind { Lost, Dropped, Refused, Restart }

    private sealed record Outcome(OutcomeKind Kind, string Detail)
    {
        internal static Outcome Lost(string detail) => new(OutcomeKind.Lost, detail);
        internal static Outcome Refused(string detail) => new(OutcomeKind.Refused, detail);
        internal static Outcome Dropped(string detail) => new(OutcomeKind.Dropped, detail);
        internal static Outcome Restart() => new(OutcomeKind.Restart, string.Empty);
    }
}

/// <summary>
/// The connection texts a player sees.
/// Each says what happened and, where the player can act, one action. Raw
/// statuses, reasons and close codes go to the log instead.
/// </summary>
internal static class ConnectionText
{
    internal const string SignInUnfinished =
        "Sign-in did not finish. Press Reconnect; if it keeps happening, ask the host to check the server.";

    internal const string LostConnection = "Lost the connection to the server; reconnecting automatically.";

    internal const string SceneNotLoaded =
        "The scene could not be loaded. Press Reconnect; if it keeps happening, ask the host.";

    internal const string KeyNotValid =
        "This access key is not valid. Paste the key you were given in Settings, then press Save.";

    internal const string FinishingPrevious = "Finishing the previous connection; reconnecting in a moment.";

    /// <summary>Shown when the key could not be stored securely (Settings → Server → Save).</summary>
    internal const string KeyNotStoredSecurely =
        "Saved, but the key could not be stored securely; it is kept until the game closes. " +
        "You will need to paste it again after restarting the game.";

    internal static string CouldNotReach(string host) =>
        $"Could not reach {host}. Check the server address in Settings and your internet connection.";
}
