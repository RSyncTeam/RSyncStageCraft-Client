#nullable enable

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.IO.Pipelines;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientAuthenticationCoordinatorTests
{
    internal const string Credential =
        "rsc1.11111111111111111111111111111111." +
        "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public async Task RealClientAndServerWebSocketsCompleteTheBoundedExchange()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var clientStream = new DuplexStream(
            serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        using var serverStream = new DuplexStream(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        using var client = WebSocketControlMessageReaderTests.CreateSocket(clientStream, isServer: false);
        using var server = WebSocketControlMessageReaderTests.CreateSocket(serverStream, isServer: true);
        var authorization = new AuthorizeMessage(17, 18, 19);
        var serverExchange = ExchangeServerSideAsync(server, authorization);
        var connection = fixture.Begin(client);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, client, Credential.AsSpan()).WaitAsync(TimeSpan.FromSeconds(5));
        await serverExchange.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Activated);
        Assert.Equal(17, result.Session!.Value.SessionId);
        Assert.Equal(18, result.Session.Value.SessionGeneration);
        Assert.Equal(new ReceiverRemoteConnection(17, 18, 19), result.Binding!.Remote);
        client.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
    }

    [Theory]
    [InlineData(1, 256)]
    [InlineData(7, 64)]
    [InlineData(256, 64)]
    public async Task FragmentedAuthorizationActivatesAndBindsOnlyItsExactConnection(
        int fragmentSize, int maximumSegments)
    {
        await using var fixture = new Fixture(
            limits: new(30, TimeSpan.FromSeconds(15), maximumSegments));
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var authorization = new AuthorizeMessage(41, 7, 99);
        socket.AddFragments(AuthenticationMessageCodec.EncodeAuthorization(authorization), fragmentSize);
        socket.SendStep = (buffer, _) =>
        {
            var characters = new char[AccessKeyCredentialFormat.CredentialLength];
            try
            {
                Assert.True(AuthenticationMessageCodec.TryReadRequest(
                    buffer.Span,
                    characters,
                    out var hello,
                    out var readiness));
                Assert.True(Credential.AsSpan().SequenceEqual(characters));
                Assert.Equal(ProtocolConstants.CurrentVersion, hello.ProtocolVersion);
                Assert.Equal(new ReadinessMessage(true, true, true), readiness);
                Assert.Equal(WebSocketMessageType.Text, socket.SentMessageType);
                Assert.True(socket.SentEndOfMessage);
            }
            finally
            {
                Array.Clear(characters);
            }
            return ValueTask.CompletedTask;
        };
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.True(result.Activated);
        var session = Assert.IsType<ReceiverSessionKey>(result.Session);
        var binding = Assert.IsType<ReceiverControlBinding>(result.Binding);
        Assert.Equal(authorization.SessionId, session.SessionId);
        Assert.Equal(authorization.Generation, session.SessionGeneration);
        Assert.Equal(fixture.Runtime.Connection.CurrentLease.Epoch, session.ConnectionEpoch);
        Assert.Equal(session, binding.LocalSession);
        Assert.Equal(new ReceiverRemoteConnection(41, 7, 99), binding.Remote);
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(
            connection, out var retainedSession, out var retainedBinding));
        Assert.Equal(session, retainedSession);
        Assert.Same(binding, retainedBinding);
        Assert.Equal(1, fixture.Runtime.ControlIngress.RetainedBindingCount);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal((int)Math.Ceiling(
            AuthenticationMessageCodec.EncodeAuthorization(authorization).Length / (double)fragmentSize),
            socket.ReceiveCalls);
        AssertReleasedBuffers(socket);

        socket.Dispose();
        var closed = await fixture.Runtime.Authentication.CloseTransportAsync(connection);
        Assert.True(closed.Found);
        Assert.True(closed.Released);
        Assert.False(closed.CleanupFailed);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
    }

    [Fact]
    public async Task RepeatedTransportCloseRetriesRetainedLifecycleCleanupBeforeRelease()
    {
        var ports = new RSyncStageCraftRuntimeTests.StubPorts
        {
            ReleaseStatus = ApplyPortStatus.TransientFailure,
        };
        await using var fixture = new Fixture(ports: ports);
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var session = Assert.IsType<ReceiverSessionKey>(authenticated.Result.Session);
        RecordOwnedLocalObject(fixture, session);
        socket.Dispose();

        var first = await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection);

        Assert.True(first.Found);
        Assert.False(first.Released);
        Assert.True(first.CleanupFailed);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        Assert.Equal(1, fixture.Runtime.Receiver.LocalObjectLedger!.Count);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            fixture.Runtime.Lifecycle.Current.State);

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        var retry = await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection);

        Assert.True(retry.Found);
        Assert.True(retry.Released);
        Assert.False(retry.CleanupFailed);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectLedger!.Count);
        Assert.Equal(
            SyncReceiverLifecycleState.Disconnected,
            fixture.Runtime.Lifecycle.Current.State);
    }

    [Fact]
    public async Task PhysicalCloseAdvancesRetainedTerminalCleanupBeforeRelease()
    {
        var ports = new RSyncStageCraftRuntimeTests.StubPorts
        {
            ReleaseStatus = ApplyPortStatus.TransientFailure,
        };
        await using var fixture = new Fixture(ports: ports);
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var session = Assert.IsType<ReceiverSessionKey>(authenticated.Result.Session);
        RecordOwnedLocalObject(fixture, session);

        var shutdown = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, shutdown.Status);
        Assert.Equal(
            SyncReceiverLifecycleState.CleanupRequired,
            fixture.Runtime.Lifecycle.Current.State);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        Assert.Equal(1, fixture.Runtime.Receiver.LocalObjectLedger!.Count);

        ports.ReleaseStatus = ApplyPortStatus.Succeeded;
        socket.Dispose();
        var closed = await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection);

        Assert.True(closed.Found);
        Assert.True(closed.Released);
        Assert.False(closed.CleanupFailed);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(0, fixture.Runtime.Receiver.LocalObjectLedger!.Count);
        Assert.Equal(
            SyncReceiverLifecycleState.Terminated,
            fixture.Runtime.Lifecycle.Current.State);
        Assert.Equal(
            SyncReceiverDisconnectStatus.Terminated,
            (await fixture.Runtime.ShutdownAsync()).Status);
    }

    [Theory]
    [InlineData(AuthenticationMessageCodec.MaximumResponseBytes, ClientAuthenticationStatus.Activated)]
    [InlineData(AuthenticationMessageCodec.MaximumResponseBytes + 1, ClientAuthenticationStatus.MessageTooLarge)]
    public async Task AuthorizationHasAnExactByteLimit(
        int length, ClientAuthenticationStatus expected)
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var encoded = AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3));
        var payload = new byte[length];
        encoded.CopyTo(payload, 0);
        Array.Fill(payload, (byte)' ', encoded.Length, payload.Length - encoded.Length);
        socket.Add(payload);
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected != ClientAuthenticationStatus.Activated, socket.Aborted);
        AssertReleasedBuffers(socket);
    }

    [Theory]
    [InlineData(true, ClientAuthenticationStatus.Activated)]
    [InlineData(false, ClientAuthenticationStatus.SegmentLimitExceeded)]
    public async Task ReceiveSegmentLimitIncludesTheCompletingSegment(
        bool completesAtLimit, ClientAuthenticationStatus expected)
    {
        await using var fixture = new Fixture(
            limits: new(30, TimeSpan.FromSeconds(15), 2));
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var payload = AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3));
        socket.Add(payload[..5], end: false);
        socket.Add(payload[5..], end: completesAtLimit);
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(expected, result.Status);
        Assert.Equal(2, socket.ReceiveCalls);
        AssertReleasedBuffers(socket);
    }

    [Theory]
    [InlineData(0, ClientAuthenticationStatus.UnexpectedMessageType)]
    [InlineData(1, ClientAuthenticationStatus.RemoteClosed)]
    [InlineData(2, ClientAuthenticationStatus.InvalidAuthorization)]
    [InlineData(3, ClientAuthenticationStatus.TransportFailure)]
    public async Task InvalidOrNonTextAuthorizationCannotActivate(
        int mode, ClientAuthenticationStatus expected)
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        if (mode == 0) socket.Add("binary"u8.ToArray(), type: WebSocketMessageType.Binary);
        if (mode == 1) socket.Add([], type: WebSocketMessageType.Close);
        if (mode == 2) socket.Add("{}"u8.ToArray());
        if (mode == 3)
            socket.ReceiveSteps.Enqueue((buffer, _) => ValueTask.FromResult(
                new ValueWebSocketReceiveResult(buffer.Length + 1, WebSocketMessageType.Text, true)));
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(expected, result.Status);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        AssertReleasedBuffers(socket);
    }

    [Fact]
    public async Task LocalValidationAndReadinessRefusalsConsumeNoIoAndRemainRetryable()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        var connection = fixture.Begin(socket);

        Assert.Equal(
            ClientAuthenticationStatus.InvalidCredential,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                connection, socket, "not-a-key".AsSpan())).Status);
        Assert.Equal(
            ClientAuthenticationStatus.DependenciesUnavailable,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                connection, socket, Credential.AsSpan())).Status);
        Assert.Equal(0, socket.SendCalls);
        Assert.Equal(0, socket.ReceiveCalls);

        await fixture.ReadyAsync();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(2, 3, 4)));
        Assert.True((await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan())).Activated);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task MissingReceiptSafeHostStillAllowsSignIn()
    {
        // Split readiness: hosts gate applying, not sign-in.
        await using var fixture = new Fixture();
        await fixture.Runtime.UpdateReadinessAsync(new(
            PenumbraReady: true,
            GlamourerReady: true,
            IpcReady: true,
            ProtocolCompatible: true,
            LocalObjectHostReady: false));
        using var socket = new ScriptedSocket();
        var connection = fixture.Begin(socket);
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(2, 3, 4)));

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.Activated, result.Status);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal(1, socket.ReceiveCalls);
        Assert.True(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.False(fixture.Runtime.Readiness.LocalObjects.IsReady);
    }

    [Fact]
    public async Task ExactSocketAndOneShotOperationPreventDuplicateAdmission()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        using var wrongSocket = new ScriptedSocket();
        var entered = Signal();
        var release = Signal();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(2, 3, 4)));
        socket.SendStep = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var connection = fixture.Begin(socket);
        var authenticate = fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientAuthenticationStatus.Busy,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                connection, socket, Credential.AsSpan())).Status);
        Assert.Equal(ClientAuthenticationStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                connection, wrongSocket, Credential.AsSpan())).Status);
        Assert.Equal(ClientAuthenticationStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                new ClientAuthenticationConnection(), socket, Credential.AsSpan())).Status);
        Assert.Equal(1, socket.SendCalls);
        Assert.Equal(0, wrongSocket.SendCalls);

        release.TrySetResult();
        Assert.True((await authenticate.WaitAsync(TimeSpan.FromSeconds(5))).Activated);
        Assert.Equal(ClientAuthenticationStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.AuthenticateAsync(
                connection, socket, Credential.AsSpan())).Status);
        Assert.Equal(1, socket.SendCalls);
    }

    [Fact]
    public async Task PreCancelledOperationDoesNotEnterTheProvider()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan(), cancellation.Token);

        Assert.Equal(ClientAuthenticationStatus.Cancelled, result.Status);
        Assert.Equal(0, socket.SendCalls);
        Assert.Equal(0, socket.ReceiveCalls);
        Assert.True(socket.Aborted);
        fixture.AssertRetired(connection);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    public async Task ExpiredAndRegressingClocksRefuseBeforeProviderIo(int seconds)
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var connection = fixture.Begin(socket);
        clock.Advance(TimeSpan.FromSeconds(seconds));

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.Equal(0, socket.SendCalls);
        Assert.Equal(0, socket.ReceiveCalls);
        fixture.AssertRetired(connection);
    }

    [Fact]
    public async Task TimeoutUsesOnlyTheRemainingOriginalHandshakeBudget()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(
            limits: new(30, TimeSpan.FromMilliseconds(150), 64),
            clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket
        {
            SendStep = async (_, token) => await Task.Delay(Timeout.Infinite, token),
        };
        var connection = fixture.Begin(socket);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientAuthenticationStatus.TimedOut, result.Status);
        Assert.True(socket.Aborted);
        AssertReleasedBuffers(socket);
        fixture.AssertRetired(connection);
    }

    [Theory]
    [InlineData(0, ClientAuthenticationStatus.Cancelled)]
    [InlineData(1, ClientAuthenticationStatus.InactiveConnection)]
    [InlineData(2, ClientAuthenticationStatus.InactiveConnection)]
    [InlineData(3, ClientAuthenticationStatus.InactiveConnection)]
    [InlineData(4, ClientAuthenticationStatus.InactiveConnection)]
    public async Task LateReceiveCannotActivateAfterItsOwnerIsRetired(
        int retirement, ClientAuthenticationStatus expected)
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var response = AuthenticationMessageCodec.EncodeAuthorization(new(5, 6, 7));
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            entered.TrySetResult();
            await release.Task; // A provider may return late after ignoring cancellation.
            response.CopyTo(buffer);
            return new(response.Length, WebSocketMessageType.Text, true);
        });
        var connection = fixture.Begin(socket);
        var authentication = fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (retirement == 0) cancellation.Cancel();
        if (retirement == 1)
        {
            socket.Dispose();
            Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
        }
        if (retirement == 2)
            Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
                (await fixture.Runtime.ShutdownAsync()).Status);
        if (retirement == 3)
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            fixture.AssertRetired(connection);
        }
        if (retirement == 4)
        {
            await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true));
            fixture.AssertRetired(connection);
        }
        Assert.False(authentication.IsCompleted);

        release.TrySetResult();
        var result = await authentication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, result.Status);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        AssertReleasedBuffers(socket);
        if (fixture.Runtime.Authentication.Count != 0)
        {
            socket.Dispose();
            Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
        }
    }

    [Fact]
    public async Task ReadinessLossCancelsHandshakeWithoutSweep()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var entered = Signal();
        var cancellationObserved = Signal();
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancellationObserved.TrySetResult();
            }

            return default;
        });
        var connection = fixture.Begin(socket);
        var authentication = fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var readiness = await fixture.Runtime.UpdateReadinessAsync(
            new(true, true, true, false, true));
        Assert.Equal(SyncReceiverReadinessStatus.Inactive, readiness.Status);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retired = await authentication.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, retired.Status);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        socket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
    }

    [Fact]
    public async Task NoSessionCleanupCannotReleaseALaterPublishedActivation()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(5, 6, 7)));
        var connection = fixture.Begin(socket);
        var receiverGate = Assert.IsType<object>(typeof(ReceiverActorRuntime)
            .GetField("sessionGate", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.Runtime.Receiver));
        var gateHeld = Signal();
        using var releaseActivation = new ManualResetEventSlim();
        var gateOwner = Task.Factory.StartNew(
            () =>
            {
                lock (receiverGate)
                {
                    gateHeld.TrySetResult();
                    if (!releaseActivation.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Test did not release receiver activation.");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await gateHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cleanupEntered = Signal();
        using var releaseCleanup = new ManualResetEventSlim();
        fixture.Runtime.Authentication.BeforeCleanupCompletion = session =>
        {
            if (session is not null) return;
            cleanupEntered.TrySetResult();
            if (!releaseCleanup.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release authentication cleanup.");
        };

        var authentication = Task.Factory.StartNew(
                () => fixture.Runtime.Authentication.AuthenticateAsync(
                    connection, socket, Credential.AsSpan()),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Runtime.Connection.CurrentLease.IsValid,
            TimeSpan.FromSeconds(5)));
        socket.Dispose();
        var close = Task.Factory.StartNew(
                () => fixture.Runtime.Authentication.CloseTransportAsync(connection),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseActivation.Set();
        await gateOwner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientAuthenticationStatus.CleanupRequired,
            (await authentication.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(SyncReceiverLifecycleState.Active, fixture.Runtime.Lifecycle.Current.State);

        releaseCleanup.Set();
        var firstClose = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstClose.Found);
        Assert.False(firstClose.Released);
        Assert.False(firstClose.CleanupFailed);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);

        var retry = await fixture.Runtime.Authentication.CloseTransportAsync(connection)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(retry.Found);
        Assert.True(retry.Released);
        Assert.False(retry.CleanupFailed);
        Assert.Equal(SyncReceiverLifecycleState.Disconnected, fixture.Runtime.Lifecycle.Current.State);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
    }

    [Fact]
    public async Task ReadinessLostAtReceiveCompletionPreventsActivation()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var response = AuthenticationMessageCodec.EncodeAuthorization(new(5, 6, 7));
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true));
            response.CopyTo(buffer);
            return new(response.Length, WebSocketMessageType.Text, true);
        });
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        AssertReleasedBuffers(socket);
    }

    [Theory]
    [InlineData(0, ClientAuthenticationStatus.TransportFailure, false)]
    [InlineData(1, ClientAuthenticationStatus.TransportFailure, false)]
    [InlineData(2, ClientAuthenticationStatus.InvalidAuthorization, true)]
    [InlineData(3, ClientAuthenticationStatus.TransportFailure, false)]
    public async Task ProviderAndAbortFailuresDoNotEscapeOrRetainState(
        int mode, ClientAuthenticationStatus expected, bool abortFailed)
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket { ThrowOnAbort = mode == 2 };
        if (mode == 0) socket.SendStep = (_, _) => throw new IOException("private send details");
        if (mode == 1)
            socket.ReceiveSteps.Enqueue((_, _) => throw new IOException("private receive details"));
        if (mode == 2) socket.Add("{}"u8.ToArray());
        if (mode == 3)
            socket.ReceiveSteps.Enqueue((buffer, _) => ValueTask.FromResult(
                new ValueWebSocketReceiveResult(buffer.Length + 1, WebSocketMessageType.Text, true)));
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(expected, result.Status);
        Assert.Equal(abortFailed, result.AbortFailed);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        AssertReleasedBuffers(socket);
    }

    [Fact]
    public async Task CancellationCallbacksAreDrainedBeforeSecretBuffersAreReleased()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var callbackEntered = Signal();
        var releaseCallback = new ManualResetEventSlim();
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait(TimeSpan.FromSeconds(10));
            });
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        var connection = fixture.Begin(socket);
        var authentication = fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(authentication.IsCompleted);
        Assert.False(IsZero(socket.BorrowedSend.Span));

        releaseCallback.Set();
        var result = await authentication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientAuthenticationStatus.Cancelled, result.Status);
        Assert.False(result.CancellationCallbacksFailed);
        AssertReleasedBuffers(socket);
        releaseCallback.Dispose();
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsContainedAndReported()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            token.Register(() => throw new IOException("private callback details"));
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        var connection = fixture.Begin(socket);
        var authentication = fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await authentication.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientAuthenticationStatus.Cancelled, result.Status);
        Assert.True(result.CancellationCallbacksFailed);
        AssertReleasedBuffers(socket);
    }

    [Fact]
    public async Task ExistingLocalSessionCannotBeClaimedByAuthorization()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var authorization = new AuthorizeMessage(5, 6, 7);
        var response = AuthenticationMessageCodec.EncodeAuthorization(authorization);
        ReceiverSessionKey existing = default;
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            existing = (await fixture.Runtime.ConnectAsync(5, 6)).Session!.Value;
            response.CopyTo(buffer);
            return new(response.Length, WebSocketMessageType.Text, true);
        });
        var connection = fixture.Begin(socket);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.ActivationRejected, result.Status);
        Assert.Equal(SyncReceiverConnectStatus.AlreadyActive, result.ConnectStatus);
        Assert.Equal(existing, fixture.Runtime.Lifecycle.Current.ActiveSession);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        Assert.True(socket.Aborted);
        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
            (await fixture.Runtime.DisconnectAsync(existing)).Status);
    }

    [Fact]
    public async Task PhysicalCloseCleansTheExactSessionBeforeReplacementAdmission()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var firstSocket = new ScriptedSocket();
        firstSocket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(8, 9, 10)));
        var first = fixture.Begin(firstSocket);
        var firstResult = await fixture.Runtime.Authentication.AuthenticateAsync(
            first, firstSocket, Credential.AsSpan());
        var firstSession = firstResult.Session!.Value;
        firstSocket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(first)).Released);

        using var secondSocket = new ScriptedSocket();
        secondSocket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(8, 9, 11)));
        var second = fixture.Begin(secondSocket);
        var secondResult = await fixture.Runtime.Authentication.AuthenticateAsync(
            second, secondSocket, Credential.AsSpan());

        Assert.True(secondResult.Activated);
        Assert.True(secondResult.Session!.Value.ConnectionEpoch > firstSession.ConnectionEpoch);
        Assert.Equal(11, secondResult.Binding!.Remote.ConnectionEpoch);
        Assert.False(fixture.Runtime.Authentication.TryResolveAuthenticated(first, out _, out _));
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(second, out _, out _));
    }

    [Fact]
    public async Task BeginIsCapacityAndRateBoundedAndUsesReferenceIdentity()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(
            limits: new(1, TimeSpan.FromSeconds(15), 64),
            clock: clock);
        using var firstSocket = new ScriptedSocket();
        using var secondSocket = new ScriptedSocket();
        var first = fixture.Runtime.Authentication.BeginTrustedTransport(firstSocket);
        Assert.Equal(ClientAuthenticationBeginStatus.Begun, first.Status);
        Assert.Equal(ClientAuthenticationBeginStatus.AlreadyTracked,
            fixture.Runtime.Authentication.BeginTrustedTransport(firstSocket).Status);
        Assert.Equal(ClientAuthenticationBeginStatus.CapacityExceeded,
            fixture.Runtime.Authentication.BeginTrustedTransport(secondSocket).Status);

        firstSocket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(first.Connection)).Released);
        Assert.Equal(ClientAuthenticationBeginStatus.RateLimited,
            fixture.Runtime.Authentication.BeginTrustedTransport(secondSocket).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClientAuthenticationBeginStatus.Begun,
            fixture.Runtime.Authentication.BeginTrustedTransport(secondSocket).Status);
    }

    [Fact]
    public async Task ReentrantClockTerminationCannotPublishClientTransportAdmission()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        using var socket = new ScriptedSocket();
        clock.InvokeOnce(fixture.Runtime.Authentication.Terminate);

        var result = fixture.Runtime.Authentication.BeginTrustedTransport(socket);

        Assert.Equal(ClientAuthenticationBeginStatus.Terminated, result.Status);
        Assert.Null(result.Connection);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
    }

    [Fact]
    public async Task ReentrantClockTerminationCannotPublishClientOperationOwnership()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var connection = fixture.Begin(socket);
        clock.InvokeOnce(fixture.Runtime.Authentication.Terminate);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.Equal(0, socket.SendCalls);
        Assert.Equal(0, socket.ReceiveCalls);
        fixture.AssertRetired(connection);
    }

    [Fact]
    public async Task ReentrantClockTerminationCannotReportClientActivation()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3)));
        var connection = fixture.Begin(socket);
        clock.InvokeAfter(4, fixture.Runtime.Authentication.Terminate);

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        fixture.AssertRetired(connection);
    }

    [Fact]
    public async Task LeaseInvalidationAtFinalPublicationCannotReportClientActivation()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3)));
        var connection = fixture.Begin(socket);
        clock.InvokeAfter(4, () => fixture.Runtime.Connection.Invalidate(
            fixture.Runtime.Connection.CurrentLease));

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        fixture.AssertRetired(connection);
    }

    [Fact]
    public async Task ReceiverAuthorityLossAtFinalPublicationCannotReportClientActivation()
    {
        var clock = new Clock();
        await using var fixture = new Fixture(clock: clock);
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3)));
        var connection = fixture.Begin(socket);
        clock.InvokeAfter(4, () =>
        {
            var session = Assert.IsType<ReceiverSessionKey>(
                fixture.Runtime.Lifecycle.Current.ActiveSession);
            Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(session));
        });

        var result = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            socket,
            Credential.AsSpan());

        Assert.Equal(ClientAuthenticationStatus.InactiveConnection, result.Status);
        Assert.False(fixture.Runtime.Lifecycle.Current.Lease.IsValid);
        Assert.Equal(0, fixture.Runtime.ControlIngress.RetainedBindingCount);
        fixture.AssertRetired(connection);
    }

    [Theory]
    [InlineData(0, 15, 64)]
    [InlineData(121, 15, 64)]
    [InlineData(30, 0, 64)]
    [InlineData(30, 121, 64)]
    [InlineData(30, 15, 0)]
    [InlineData(30, 15, 1025)]
    public async Task InvalidClientBoundsAreRejectedAtComposition(
        int starts, int timeoutSeconds, int segments)
    {
        await using var fixture = new Fixture();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClientAuthenticationCoordinator(
                fixture.Runtime.Lifecycle,
                fixture.Runtime.ControlIngress,
                fixture.Runtime.Readiness,
                new(starts, TimeSpan.FromSeconds(timeoutSeconds), segments),
                TimeProvider.System));
    }

    [Fact]
    public async Task NullTransportAndTerminalCoordinatorCannotRegister()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        Assert.Equal(ClientAuthenticationBeginStatus.InvalidTransport,
            fixture.Runtime.Authentication.BeginTrustedTransport(null).Status);
        fixture.Runtime.Authentication.Terminate();
        Assert.Equal(ClientAuthenticationBeginStatus.Terminated,
            fixture.Runtime.Authentication.BeginTrustedTransport(socket).Status);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
    }

    [Fact]
    public async Task ShutdownRetiresButDoesNotReleaseAProviderThatStillOwnsBuffers()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        using var socket = new ScriptedSocket();
        var entered = Signal();
        var release = Signal();
        var response = AuthenticationMessageCodec.EncodeAuthorization(new(1, 2, 3));
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            response.CopyTo(buffer);
            return new(response.Length, WebSocketMessageType.Text, true);
        });
        var connection = fixture.Begin(socket);
        var authentication = fixture.Runtime.Authentication.AuthenticateAsync(
            connection, socket, Credential.AsSpan());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
            (await fixture.Runtime.ShutdownAsync()).Status);
        Assert.True(fixture.Runtime.Authentication.IsTerminated);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        fixture.AssertRetired(connection);
        Assert.False(authentication.IsCompleted);

        socket.Dispose();
        Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
        release.TrySetResult();
        Assert.Equal(ClientAuthenticationStatus.InactiveConnection,
            (await authentication.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        AssertReleasedBuffers(socket);
    }

    [Fact]
    public async Task ActiveLocalSessionRefusesANewTransportBeforeAdmission()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        var active = (await fixture.Runtime.ConnectAsync(1, 2)).Session!.Value;
        using var socket = new ScriptedSocket();

        Assert.Equal(ClientAuthenticationBeginStatus.LocalSessionActive,
            fixture.Runtime.Authentication.BeginTrustedTransport(socket).Status);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
            (await fixture.Runtime.DisconnectAsync(active)).Status);
    }

    [Fact]
    public async Task OrphanedActiveLeaseRequiresCleanupBeforeTransportAdmission()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        await fixture.Runtime.ConnectAsync(1, 2);
        Assert.True(fixture.Runtime.Connection.Invalidate(
            fixture.Runtime.Connection.CurrentLease));
        using var socket = new ScriptedSocket();

        var begin = fixture.Runtime.Authentication.BeginTrustedTransport(socket);

        Assert.Equal(ClientAuthenticationBeginStatus.LocalCleanupRequired, begin.Status);
        Assert.Null(begin.Connection);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(
            SyncReceiverReadinessStatus.Inactive,
            (await fixture.Runtime.Lifecycle
                .ReconcileReadinessAsync(isReady: true)).Status);
    }

    [Fact]
    public async Task LostReceiverAuthorityRequiresCleanupBeforeTransportAdmission()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        var session = (await fixture.Runtime.ConnectAsync(1, 2)).Session!.Value;
        Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(session));
        using var socket = new ScriptedSocket();

        var begin = fixture.Runtime.Authentication.BeginTrustedTransport(socket);

        Assert.Equal(ClientAuthenticationBeginStatus.LocalCleanupRequired, begin.Status);
        Assert.Null(begin.Connection);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(
            SyncReceiverReadinessStatus.Inactive,
            (await fixture.Runtime.Lifecycle
                .ReconcileReadinessAsync(isReady: true)).Status);
    }

    [Fact]
    public async Task EstablishedAuthenticationRetiresWhenActiveLeaseIsInvalidated()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.True(fixture.Runtime.Connection.Invalidate(
            fixture.Runtime.Connection.CurrentLease));

        var resolved = fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection,
            out _,
            out _);

        Assert.False(resolved);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task EstablishedAuthenticationRetiresWhenReceiverAuthorityIsLost()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var session = authenticated.Result.Session!.Value;
        Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(session));

        var resolved = fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection,
            out _,
            out _);

        Assert.False(resolved);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task EstablishedAuthenticationRetiresOnReadinessLossBeforeLifecycleReconciliation()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var session = authenticated.Result.Session!.Value;

        fixture.Runtime.Readiness.Update(new(true, true, true, false, true));
        Assert.Equal(SyncReceiverLifecycleState.Active, fixture.Runtime.Lifecycle.Current.State);
        Assert.Equal(session, fixture.Runtime.Lifecycle.Current.ActiveSession);

        var resolved = fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection,
            out _,
            out _);

        Assert.False(resolved);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task ExactReadinessLossRetirementCannotBeErasedByConcurrentRecovery()
    {
        await using var fixture = new Fixture();
        using var socket = new ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var session = authenticated.Result.Session!.Value;

        // Model recovery winning the shared readiness publication between the
        // root's exact unhealthy observation and authentication retirement.
        fixture.Runtime.Readiness.Update(new(true, true, true, false, true));
        fixture.Runtime.Readiness.Update(new(true, true, true, true, true));
        fixture.Runtime.Authentication.RetireForObservedReadinessLoss();

        Assert.True(fixture.Runtime.Readiness.IsReady);
        Assert.Equal(SyncReceiverLifecycleState.Active, fixture.Runtime.Lifecycle.Current.State);
        Assert.Equal(session, fixture.Runtime.Lifecycle.Current.ActiveSession);
        Assert.False(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection,
            out _,
            out _));
        fixture.AssertRetired(authenticated.Connection);

        socket.Dispose();
        var closed = await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection);
        Assert.True(closed.Found);
        Assert.True(closed.Released);
        Assert.False(closed.CleanupFailed);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExchangeServerSideAsync(
        WebSocket server, AuthorizeMessage authorization)
    {
        var request = new byte[AuthenticationMessageCodec.MaximumRequestBytes + 1];
        var credential = new char[AccessKeyCredentialFormat.CredentialLength];
        try
        {
            var length = 0;
            while (true)
            {
                var received = await server.ReceiveAsync(request.AsMemory(length), CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                Assert.InRange(received.Count, 0, AuthenticationMessageCodec.MaximumRequestBytes - length);
                length += received.Count;
                if (received.EndOfMessage) break;
            }
            Assert.True(AuthenticationMessageCodec.TryReadRequest(
                request.AsSpan(0, length), credential, out _, out _));
            Assert.True(Credential.AsSpan().SequenceEqual(credential));
            var response = AuthenticationMessageCodec.EncodeAuthorization(authorization);
            try
            {
                await server.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request);
            Array.Clear(credential);
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value != 0) return false;
        return true;
    }

    private static void AssertReleasedBuffers(ScriptedSocket socket)
    {
        if (!socket.BorrowedSend.IsEmpty) Assert.True(IsZero(socket.BorrowedSend.Span));
        if (!socket.BorrowedReceive.IsEmpty) Assert.True(IsZero(socket.BorrowedReceive.Span));
    }

    private static void RecordOwnedLocalObject(Fixture fixture, ReceiverSessionKey session)
    {
        var objectId = new RscObjectId(Guid.NewGuid());
        var token = new LocalObjectApplyToken(session, objectId, new ApplyGeneration(1));
        var identity = new LocalObjectSpawnIdentity(RscObjectKind.Furniture, "furniture:indoor:6000");
        var ledger = fixture.Runtime.Receiver.LocalObjectLedger!;
        Assert.True(ledger.TryReserve(token, identity).Accepted);
        Assert.True(ledger.TryRecordSpawn(
            token,
            identity,
            new LocalObjectInstanceHandle(Guid.NewGuid(), objectId, RscObjectKind.Furniture, 1)));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly List<(ClientAuthenticationConnection Connection, WebSocket Socket)> connections = [];

        internal Fixture(
            ClientAuthenticationLimits? limits = null,
            TimeProvider? clock = null,
            ClientControlReceiveLimits? controlLimits = null,
            ClientControlSendLimits? controlSendLimits = null,
            RSyncStageCraftRuntimeTests.StubPorts? ports = null)
        {
            Ports = ports ?? new RSyncStageCraftRuntimeTests.StubPorts();
            Runtime = new RSyncStageCraftRuntime(
                Ports,
                Ports,
                authenticationLimits: limits,
                authenticationTimeProvider: clock,
                controlReceiveLimits: controlLimits,
                controlSendLimits: controlSendLimits);
        }

        internal RSyncStageCraftRuntime Runtime { get; }
        internal RSyncStageCraftRuntimeTests.StubPorts Ports { get; }

        internal Task ReadyAsync() =>
            Runtime.UpdateReadinessAsync(new(true, true, true, true, true));

        internal ClientAuthenticationConnection Begin(WebSocket socket)
        {
            var result = Runtime.Authentication.BeginTrustedTransport(socket);
            Assert.Equal(ClientAuthenticationBeginStatus.Begun, result.Status);
            var connection = Assert.IsType<ClientAuthenticationConnection>(result.Connection);
            connections.Add((connection, socket));
            return connection;
        }

        internal async Task<(ClientAuthenticationConnection Connection,
            ClientAuthenticationResult Result)> AuthenticateAsync(
            ScriptedSocket socket,
            AuthorizeMessage? authorization = null)
        {
            await ReadyAsync();
            socket.Add(AuthenticationMessageCodec.EncodeAuthorization(
                authorization ?? new(1, 2, 57)));
            var connection = Begin(socket);
            var result = await Runtime.Authentication.AuthenticateAsync(
                connection, socket, Credential.AsSpan());
            Assert.True(result.Activated);
            return (connection, result);
        }

        internal void AssertRetired(ClientAuthenticationConnection connection) =>
            Assert.Contains(connection, Runtime.Authentication.GetConnectionsToClose());

        public async ValueTask DisposeAsync()
        {
            Runtime.Authentication.Terminate();
            foreach (var item in connections) item.Socket.Dispose();
            foreach (var connection in Runtime.Authentication.GetConnectionsToClose())
                await Runtime.Authentication.CloseTransportAsync(connection);
            await Runtime.ShutdownAsync();
            foreach (var connection in Runtime.Authentication.GetConnectionsToClose())
                await Runtime.Authentication.CloseTransportAsync(connection);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp = TimeSpan.TicksPerDay;
        private Action? onTimestamp;
        private int timestampCalls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (Volatile.Read(ref onTimestamp) is not null &&
                Interlocked.Decrement(ref timestampCalls) == 0)
            {
                Interlocked.Exchange(ref onTimestamp, null)?.Invoke();
            }
            return Interlocked.Read(ref timestamp);
        }
        internal void Advance(TimeSpan delta) => Interlocked.Add(ref timestamp, delta.Ticks);
        internal void InvokeOnce(Action callback) => InvokeAfter(1, callback);
        internal void InvokeAfter(int calls, Action callback)
        {
            if (calls <= 0)
                throw new ArgumentOutOfRangeException(nameof(calls));
            Volatile.Write(ref timestampCalls, calls);
            Interlocked.Exchange(ref onTimestamp, callback);
        }
    }

    internal sealed class ScriptedSocket : WebSocket
    {
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> SendStep { get; set; } =
            (_, _) => ValueTask.CompletedTask;
        internal Queue<Func<Memory<byte>, CancellationToken, ValueTask<ValueWebSocketReceiveResult>>> ReceiveSteps
        { get; } = new();
        internal ReadOnlyMemory<byte> BorrowedSend { get; private set; }
        internal Memory<byte> BorrowedReceive { get; private set; }
        internal WebSocketMessageType SentMessageType { get; private set; }
        internal bool SentEndOfMessage { get; private set; }
        internal int SendCalls { get; private set; }
        internal int ReceiveCalls { get; private set; }
        internal bool Aborted { get; private set; }
        internal int AbortCalls { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool ThrowOnAbort { get; init; }

        internal void Add(byte[] bytes, bool end = true,
            WebSocketMessageType type = WebSocketMessageType.Text) =>
            ReceiveSteps.Enqueue((buffer, _) =>
            {
                bytes.CopyTo(buffer);
                return ValueTask.FromResult(new ValueWebSocketReceiveResult(bytes.Length, type, end));
            });

        internal void AddFragments(byte[] bytes, int maximumFragmentBytes)
        {
            for (var offset = 0; offset < bytes.Length; offset += maximumFragmentBytes)
            {
                var count = Math.Min(maximumFragmentBytes, bytes.Length - offset);
                Add(bytes.AsSpan(offset, count).ToArray(), offset + count == bytes.Length);
            }
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            BorrowedSend = buffer;
            SentMessageType = messageType;
            SentEndOfMessage = endOfMessage;
            return SendStep(buffer, cancellationToken);
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReceiveCalls++;
            if (BorrowedReceive.IsEmpty) BorrowedReceive = buffer;
            return ReceiveSteps.Dequeue()(buffer, cancellationToken);
        }

        public override void Abort()
        {
            AbortCalls++;
            Aborted = true;
            if (ThrowOnAbort) throw new IOException("private abort details");
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State =>
            Disposed ? WebSocketState.Closed : Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Dispose() => Disposed = true;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer,
            WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => output.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) =>
            output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await input.DisposeAsync();
            await output.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
