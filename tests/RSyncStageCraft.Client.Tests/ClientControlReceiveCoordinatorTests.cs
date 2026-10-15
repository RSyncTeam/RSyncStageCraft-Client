#nullable enable

using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientControlReceiveCoordinatorTests
{
    [Fact]
    public async Task RealWebSocketHandsAuthenticationDirectlyToExactControlIntake()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        await fixture.ReadyAsync();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var clientStream = new ClientAuthenticationCoordinatorTests.DuplexStream(
            serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        using var serverStream = new ClientAuthenticationCoordinatorTests.DuplexStream(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        using var client = WebSocketControlMessageReaderTests.CreateSocket(clientStream, isServer: false);
        using var server = WebSocketControlMessageReaderTests.CreateSocket(serverStream, isServer: true);
        var authorization = new AuthorizeMessage(17, 18, 19);
        var control = Encode(new(17, 18, 19), EmptySnapshot());
        var serverExchange = SendAuthorizationAndControlAsync(server, authorization, control);
        var connection = fixture.Begin(client);
        var authenticated = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            client,
            ClientAuthenticationCoordinatorTests.Credential.AsSpan()).WaitAsync(TimeSpan.FromSeconds(5));

        var received = await fixture.Runtime.Authentication.ReceiveControlAsync(
            connection, client).WaitAsync(TimeSpan.FromSeconds(5));
        await serverExchange.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(authenticated.Activated);
        Assert.True(received.Accepted);
        Assert.Equal(WebSocketControlReadStatus.MessageReceived, received.ReadStatus);
        Assert.Equal(ReceiverControlReceiveStatus.Accepted, received.Dispatch!.Value.Status);
        Assert.True(fixture.Runtime.Receiver.Events.IsInitializedForSession(authenticated.Session!.Value));
        client.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
    }

    [Fact]
    public async Task SequentialMessagesReuseTheBindingWithoutRetainingAQueue()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var binding = authenticated.Result.Binding!;
        socket.Add(Encode(binding.Remote, EmptySnapshot()));
        socket.Add(Encode(binding.Remote,
            new ObjectDeletedMessage(new(1), new ObjectTombstone(new(Guid.NewGuid()), RscObjectKind.Furniture, new(2)))));

        var first = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        var second = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(3, socket.ReceiveCalls); // Authorization plus two control frames.
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out var retained));
        Assert.Same(binding, retained);
        Assert.Equal(new ServerSequence(1), fixture.Runtime.Receiver.Events.CurrentSequence);
    }

    [Fact]
    public async Task ConcurrentReceiveIsBusyWithoutEnteringTheProviderTwice()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var release = Signal();
        var message = Encode(authenticated.Result.Binding!.Remote, EmptySnapshot());
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            message.CopyTo(buffer);
            return new(message.Length, WebSocketMessageType.Text, true);
        });
        var first = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competing = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.Busy, competing.Status);
        Assert.False(competing.Terminal);
        Assert.Equal(2, socket.ReceiveCalls);
        release.TrySetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
    }

    [Fact]
    public async Task WrongSocketAndForeignCapabilityConsumeNoControlBytes()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        await using var foreign = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var wrong = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var foreignSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var foreignAuthenticated = await foreign.AuthenticateAsync(foreignSocket, new(3, 4, 5));

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.ReceiveControlAsync(
                authenticated.Connection, wrong)).Status);
        Assert.Equal(ClientControlReceiveStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.ReceiveControlAsync(
                foreignAuthenticated.Connection, socket)).Status);
        Assert.Equal(0, wrong.ReceiveCalls);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task PreCancelledReceiveConsumesNothingAndCanBeRetried()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        socket.Add(Encode(authenticated.Result.Binding!.Remote, EmptySnapshot()));

        var cancelled = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket, new CancellationToken(canceled: true));

        Assert.Equal(ClientControlReceiveStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.Terminal);
        Assert.Equal(1, socket.ReceiveCalls);
        Assert.True((await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket)).Accepted);
        Assert.Equal(2, socket.ReceiveCalls);
    }

    [Theory]
    [InlineData(0)] // Binary.
    [InlineData(1)] // Remote close.
    [InlineData(2)] // Invalid JSON.
    [InlineData(3)] // Wrong remote envelope.
    [InlineData(4)] // Expected provider exception.
    [InlineData(5)] // Unexpected provider exception is contained.
    [InlineData(6)] // Impossible receive count.
    public async Task InvalidReadOrDispatchIsTerminalAndRetiresExactAuthority(int mode)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var remote = authenticated.Result.Binding!.Remote;
        if (mode == 0) socket.Add([1], type: WebSocketMessageType.Binary);
        if (mode == 1) socket.Add([], type: WebSocketMessageType.Close);
        if (mode == 2) socket.Add("{}"u8.ToArray());
        if (mode == 3) socket.Add(Encode(remote with { ConnectionEpoch = remote.ConnectionEpoch + 1 }, EmptySnapshot()));
        if (mode == 4) socket.ReceiveSteps.Enqueue((_, _) => throw new IOException("private provider details"));
        if (mode == 5) socket.ReceiveSteps.Enqueue((_, _) => throw new InvalidOperationException("private details"));
        if (mode == 6) socket.ReceiveSteps.Enqueue((buffer, _) => ValueTask.FromResult(
            new ValueWebSocketReceiveResult(buffer.Length + 1, WebSocketMessageType.Text, true)));

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.True(result.Terminal);
        if (mode <= 1 || mode >= 4)
            Assert.Equal(ClientControlReceiveStatus.ReadFailure, result.Status);
        else
            Assert.Equal(ClientControlReceiveStatus.DispatchFailure, result.Status);
        if (mode == 0) Assert.Equal(WebSocketControlReadStatus.UnexpectedMessageType, result.ReadStatus);
        if (mode == 1) Assert.Equal(WebSocketControlReadStatus.RemoteClosed, result.ReadStatus);
        if (mode == 2) Assert.Equal(ReceiverControlReceiveStatus.InvalidMessage, result.Dispatch!.Value.Status);
        if (mode == 3) Assert.Equal(ReceiverControlReceiveStatus.EnvelopeMismatch, result.Dispatch!.Value.Status);
        if (mode >= 4) Assert.Equal(WebSocketControlReadStatus.TransportFailure, result.ReadStatus);
        Assert.Equal(mode != 1, socket.Aborted);
        fixture.AssertRetired(authenticated.Connection);
        Assert.False(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out _));
    }

    [Theory]
    [InlineData(0, WebSocketControlReadStatus.MessageTooLarge)]
    [InlineData(1, WebSocketControlReadStatus.SegmentLimitExceeded)]
    public async Task ComposedReaderEnforcesConfiguredByteAndSegmentBounds(
        int mode, WebSocketControlReadStatus expected)
    {
        var limits = mode == 0
            ? new ClientControlReceiveLimits(8, 4, TimeSpan.FromSeconds(5), 3)
            : new ClientControlReceiveLimits(128, 2, TimeSpan.FromSeconds(5), 3);
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlLimits: limits);
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        if (mode == 0)
            socket.Add(new byte[9]);
        else
        {
            socket.Add("a"u8.ToArray(), end: false);
            socket.Add("b"u8.ToArray(), end: false);
        }

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.ReadFailure, result.Status);
        Assert.Equal(expected, result.ReadStatus);
        Assert.True(result.Terminal);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task PartialMessageDeadlineIsTerminalButDoesNotDefineFirstByteLiveness()
    {
        var limits = new ClientControlReceiveLimits(
            128, 8, TimeSpan.FromMilliseconds(30), 3);
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlLimits: limits);
        using var partialSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var partial = await fixture.AuthenticateAsync(partialSocket);
        partialSocket.Add("partial"u8.ToArray(), end: false);
        partialSocket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });

        var timedOut = await fixture.Runtime.Authentication.ReceiveControlAsync(
            partial.Connection, partialSocket).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlReceiveStatus.ReadFailure, timedOut.Status);
        Assert.Equal(WebSocketControlReadStatus.PartialMessageTimedOut, timedOut.ReadStatus);
        Assert.True(timedOut.Terminal);
    }

    [Fact]
    public async Task FirstByteIdleRequiresExplicitHostCancellation()
    {
        var limits = new ClientControlReceiveLimits(
            128, 8, TimeSpan.FromMilliseconds(30), 3);
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlLimits: limits);
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(80);
        Assert.False(receive.IsCompleted);

        cancellation.Cancel();
        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlReceiveStatus.Cancelled, result.Status);
        Assert.True(result.Terminal);
        Assert.True(socket.Aborted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleRevocationCancelsAuthenticatedTransportWithoutSweep(
        bool readinessLoss)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
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
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection,
            socket);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (readinessLoss)
        {
            var result = await fixture.Runtime.UpdateReadinessAsync(
                new(true, true, true, false, true));
            Assert.Equal(SyncReceiverReadinessStatus.Suspended, result.Status);
        }
        else
        {
            var result = await fixture.Runtime.DisconnectAsync(
                authenticated.Result.Session!.Value);
            Assert.Equal(SyncReceiverDisconnectStatus.Disconnected, result.Status);
        }

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retired = await receive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection, retired.Status);
        Assert.True(retired.Terminal);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        socket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection)).Released);
    }

    [Fact]
    public async Task BusyDispatchRetainsOneFrameAndRetriesBeforeAnotherSocketRead()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var binding = authenticated.Result.Binding!;
        var message = Encode(binding.Remote, EmptySnapshot());
        using var paused = new PausedMemory(message);
        var external = Task.Run(() => fixture.Runtime.ControlIngress.Receive(binding, paused.Memory));
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Add(message);

        var first = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        Assert.Equal(ClientControlReceiveStatus.Backpressure, first.Status);
        Assert.Equal(ReceiverControlReceiveStatus.Busy, first.Dispatch!.Value.Status);
        var providerCalls = socket.ReceiveCalls;
        paused.Release.Set();
        Assert.True((await external.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);

        var retry = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.True(retry.Accepted);
        Assert.Equal(providerCalls, socket.ReceiveCalls);
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out _));
    }

    [Fact]
    public async Task DispatchRetryBudgetTerminatesWithoutReadingPastThePendingFrame()
    {
        var limits = new ClientControlReceiveLimits(
            ProtocolLimits.MaximumControlMessageBytes, 1024, TimeSpan.FromSeconds(30), 2);
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlLimits: limits);
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var binding = authenticated.Result.Binding!;
        var message = Encode(binding.Remote, EmptySnapshot());
        using var paused = new PausedMemory(message);
        var external = Task.Run(() => fixture.Runtime.ControlIngress.Receive(binding, paused.Memory));
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Add(message);

        Assert.True((await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket)).RetryRequired);
        var providerCalls = socket.ReceiveCalls;
        var exhausted = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.DispatchFailure, exhausted.Status);
        Assert.Equal(ReceiverControlReceiveStatus.Busy, exhausted.Dispatch!.Value.Status);
        Assert.True(exhausted.Terminal);
        Assert.Equal(providerCalls, socket.ReceiveCalls);
        Assert.True(socket.Aborted);
        fixture.AssertRetired(authenticated.Connection);
        paused.Release.Set();
        await external.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)] // Physical close.
    [InlineData(1)] // Root shutdown.
    [InlineData(2)] // Required dependency loss and sweep.
    public async Task LateProviderCompletionCannotDispatchAfterRetirement(int retirement)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var message = Encode(authenticated.Result.Binding!.Remote, EmptySnapshot());
        var entered = Signal();
        var release = Signal();
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation.
            message.CopyTo(buffer);
            return new(message.Length, WebSocketMessageType.Text, true);
        });
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (retirement == 0)
        {
            socket.Dispose();
            Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(
                authenticated.Connection)).Released);
        }
        if (retirement == 1)
            Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
                (await fixture.Runtime.ShutdownAsync()).Status);
        if (retirement == 2)
        {
            await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true));
            fixture.AssertRetired(authenticated.Connection);
        }
        Assert.False(receive.IsCompleted);

        release.TrySetResult();
        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection, result.Status);
        Assert.True(result.Terminal);
        Assert.False(fixture.Runtime.Receiver.Events.IsInitializedForSession(
            authenticated.Result.Session!.Value));
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        if (fixture.Runtime.Authentication.Count != 0)
        {
            socket.Dispose();
            Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(
                authenticated.Connection)).Released);
        }
    }

    [Fact]
    public async Task CloseClearsPendingBackpressureBeforeReplacementAdmission()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var firstSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var first = await fixture.AuthenticateAsync(firstSocket);
        var message = Encode(first.Result.Binding!.Remote, EmptySnapshot());
        using var paused = new PausedMemory(message);
        var external = Task.Run(() => fixture.Runtime.ControlIngress.Receive(first.Result.Binding, paused.Memory));
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstSocket.Add(message);
        Assert.True((await fixture.Runtime.Authentication.ReceiveControlAsync(
            first.Connection, firstSocket)).RetryRequired);
        paused.Release.Set();
        await external.WaitAsync(TimeSpan.FromSeconds(5));
        firstSocket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(first.Connection)).Released);

        using var secondSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var second = await fixture.AuthenticateAsync(secondSocket, new(1, 2, 58));
        secondSocket.Add(Encode(second.Result.Binding!.Remote, EmptySnapshot()));

        Assert.True((await fixture.Runtime.Authentication.ReceiveControlAsync(
            second.Connection, secondSocket)).Accepted);
        Assert.True(second.Result.Session!.Value.ConnectionEpoch > first.Result.Session!.Value.ConnectionEpoch);
    }

    [Fact]
    public async Task CancellationCallbackDrainRetainsTheExactConnectionAndFrameOwnership()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var callbackEntered = Signal();
        using var releaseCallback = new ManualResetEventSlim();
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
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(receive.IsCompleted);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        releaseCallback.Set();
        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientControlReceiveStatus.Cancelled, result.Status);
        Assert.False(result.CancellationCallbacksFailed);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsContainedAndReported()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            token.Register(() => throw new IOException("private callback details"));
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlReceiveStatus.Cancelled, result.Status);
        Assert.True(result.CancellationCallbacksFailed);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task DispatchAbortFailureIsOnlyABoundedDiagnostic()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket
        {
            ThrowOnAbort = true,
        };
        var authenticated = await fixture.AuthenticateAsync(socket);
        socket.Add("{}"u8.ToArray());

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.DispatchFailure, result.Status);
        Assert.True(result.AbortFailed);
        Assert.True(result.Terminal);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task DirectLifecycleDisconnectInvalidatesControlAdmissionWithoutReading()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
            (await fixture.Runtime.DisconnectAsync(authenticated.Result.Session!.Value)).Status);

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.ReceiveCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task InvalidatedActiveLeaseRejectsControlAdmissionWithoutReading()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.True(fixture.Runtime.Connection.Invalidate(
            fixture.Runtime.Connection.CurrentLease));

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.ReceiveCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task LostReceiverAuthorityRejectsControlAdmissionWithoutReading()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(
            authenticated.Result.Session!.Value));

        var result = await fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);

        Assert.Equal(ClientControlReceiveStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.ReceiveCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Theory]
    [InlineData(0, 1024, 30000, 3)]
    [InlineData(16777217, 1024, 30000, 3)]
    [InlineData(128, 0, 30000, 3)]
    [InlineData(128, 4097, 30000, 3)]
    [InlineData(128, 8, 0, 3)]
    [InlineData(128, 8, 120001, 3)]
    [InlineData(128, 8, 30000, 0)]
    [InlineData(128, 8, 30000, 17)]
    public async Task InvalidControlBoundsAreRejectedAtComposition(
        int bytes, int segments, int timeoutMilliseconds, int attempts)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClientAuthenticationCoordinator(
                fixture.Runtime.Lifecycle,
                fixture.Runtime.ControlIngress,
                fixture.Runtime.Readiness,
                ClientAuthenticationLimits.Default,
                TimeProvider.System,
                new(bytes, segments, TimeSpan.FromMilliseconds(timeoutMilliseconds), attempts)));
    }

    private static ObjectSnapshotMessage EmptySnapshot() =>
        new(new(0), Array.Empty<AuthoritativeObjectState>(), Array.Empty<ObjectTombstone>());

    private static byte[] Encode(ReceiverRemoteConnection remote, object payload)
    {
        var type = payload switch
        {
            ObjectSnapshotMessage => ObjectProtocolMessageType.ObjectSnapshot,
            ObjectDeletedMessage => ObjectProtocolMessageType.ObjectDeleted,
            _ => throw new ArgumentOutOfRangeException(nameof(payload)),
        };
        var envelope = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            type,
            Guid.NewGuid(),
            remote.SessionId,
            remote.SessionGeneration,
            remote.ConnectionEpoch);
        Assert.True(new ObjectControlMessageCodec().TryEncode(
            envelope, payload, out var bytes, out var error), error.ToString());
        return bytes;
    }

    private static async Task SendAuthorizationAndControlAsync(
        WebSocket server,
        AuthorizeMessage authorization,
        byte[] control)
    {
        var request = new byte[AuthenticationMessageCodec.MaximumRequestBytes + 1];
        var credential = new char[AccessKeyCredentialFormat.CredentialLength];
        try
        {
            var length = 0;
            while (true)
            {
                var received = await server.ReceiveAsync(
                    request.AsMemory(length), CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                length += received.Count;
                Assert.InRange(length, 1, AuthenticationMessageCodec.MaximumRequestBytes);
                if (received.EndOfMessage) break;
            }
            Assert.True(AuthenticationMessageCodec.TryReadRequest(
                request.AsSpan(0, length), credential, out _, out _));
            Assert.True(ClientAuthenticationCoordinatorTests.Credential.AsSpan().SequenceEqual(credential));
            var response = AuthenticationMessageCodec.EncodeAuthorization(authorization);
            try
            {
                await server.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
                await server.SendAsync(control.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
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

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PausedMemory(byte[] bytes) : MemoryManager<byte>
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal ManualResetEventSlim Release { get; } = new();
        public override Memory<byte> Memory => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release decoding");
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => Release.Dispose();
    }
}
