#nullable enable

using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Sync;

namespace RSyncStageCraft.Tests;

public sealed class ClientControlSendCoordinatorTests
{
    public static IEnumerable<object[]> OutboundMessages()
    {
        foreach (var sample in ObjectControlMessageCodecTests.Messages())
        {
            var type = (ObjectProtocolMessageType)sample[0];
            if (type is ObjectProtocolMessageType.CreateObject or
                ObjectProtocolMessageType.UpdateObject or
                ObjectProtocolMessageType.DeleteObject or
                ObjectProtocolMessageType.AcknowledgeObjects or
                ObjectProtocolMessageType.RequestObjectSnapshot or
                ObjectProtocolMessageType.RequestObjectEvents or
                ObjectProtocolMessageType.SaveScene or
                ObjectProtocolMessageType.ReadScene)
            {
                yield return sample;
            }
        }
    }

    [Fact]
    public async Task RealWebSocketHandsAuthenticationDirectlyToExactControlSend()
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
        var serverExchange = AuthorizeAndReadControlAsync(server, authorization);
        var connection = fixture.Begin(client);
        var authenticated = await fixture.Runtime.Authentication.AuthenticateAsync(
            connection,
            client,
            ClientAuthenticationCoordinatorTests.Credential.AsSpan()).WaitAsync(TimeSpan.FromSeconds(5));
        var request = new ObjectSnapshotRequest(Guid.NewGuid());

        var sent = await fixture.Runtime.Authentication.SendControlAsync(
            connection, client, request).WaitAsync(TimeSpan.FromSeconds(5));
        var received = await serverExchange.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(authenticated.Activated);
        Assert.True(sent.Sent);
        Assert.Equal(request, Assert.IsType<ObjectSnapshotRequest>(received.Payload));
        Assert.Equal(new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.RequestObjectSnapshot,
            request.RequestId,
            authorization.SessionId,
            authorization.Generation,
            authorization.ConnectionEpoch), received.Envelope);
        client.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(connection)).Released);
    }

    [Theory]
    [MemberData(nameof(OutboundMessages))]
    public async Task EveryDefinedClientDirectionUsesTheRetainedRemoteEnvelope(
        ObjectProtocolMessageType expectedType, object payload)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket, new(31, 32, 33));
        ObjectControlMessage? captured = null;
        socket.SendStep = (buffer, _) =>
        {
            Assert.True(new ObjectControlMessageCodec().TryDecode(
                buffer, out captured, out var error), error.ToString());
            return ValueTask.CompletedTask;
        };

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, payload);

        Assert.True(result.Sent);
        Assert.NotNull(captured);
        Assert.Equal(expectedType, captured.Envelope.MessageType);
        Assert.Equal(ProtocolConstants.CurrentVersion, captured.Envelope.ProtocolVersion);
        Assert.Equal(31, captured.Envelope.SessionId);
        Assert.Equal(32, captured.Envelope.SessionGeneration);
        Assert.Equal(33, captured.Envelope.ConnectionEpoch);
        Assert.Equal(payload.GetType(), captured.Payload.GetType());
        var expectedId = RequestId(payload);
        if (expectedId is { } requestId)
            Assert.Equal(requestId, captured.Envelope.MessageId);
        else
            Assert.NotEqual(Guid.Empty, captured.Envelope.MessageId);
        Assert.Equal(WebSocketMessageType.Text, socket.SentMessageType);
        Assert.True(socket.SentEndOfMessage);
        Assert.Equal(2, socket.SendCalls); // Credential plus one control frame.
        Assert.True(IsZero(socket.BorrowedSend.Span));
    }

    [Fact]
    public async Task ConcurrentSendIsBusyWithoutEnteringTheProviderTwice()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var release = Signal();
        socket.SendStep = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competing = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.Busy, competing.Status);
        Assert.False(competing.Terminal);
        Assert.Equal(2, socket.SendCalls);
        release.TrySetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Sent);
    }

    [Fact]
    public async Task OneSendAndOneReceiveMayOwnTheDuplexSocketConcurrently()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var receiveEntered = Signal();
        var releaseReceive = Signal();
        var snapshot = EncodeSnapshot(authenticated.Result.Binding!.Remote);
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            receiveEntered.TrySetResult();
            await releaseReceive.Task;
            snapshot.CopyTo(buffer);
            return new(snapshot.Length, WebSocketMessageType.Text, true);
        });
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        await receiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sent = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sent.Sent);
        Assert.False(receive.IsCompleted);
        releaseReceive.TrySetResult();
        Assert.True((await receive.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
    }

    [Fact]
    public async Task WrongSocketAndForeignCapabilitySendNothing()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        await using var foreign = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var wrong = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var foreignSocket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var foreignAuthenticated = await foreign.AuthenticateAsync(foreignSocket, new(3, 4, 5));

        Assert.Equal(ClientControlSendStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.SendControlAsync(
                authenticated.Connection, wrong, Request())).Status);
        Assert.Equal(ClientControlSendStatus.InactiveConnection,
            (await fixture.Runtime.Authentication.SendControlAsync(
                foreignAuthenticated.Connection, socket, Request())).Status);
        Assert.Equal(0, wrong.SendCalls);
        Assert.Equal(1, socket.SendCalls);
    }

    [Fact]
    public async Task PreCancelledSendConsumesNothingAndCanBeRetried()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var request = Request();

        var cancelled = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, request, new CancellationToken(canceled: true));

        Assert.Equal(ClientControlSendStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.Terminal);
        Assert.Equal(1, socket.SendCalls);
        Assert.True((await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, request)).Sent);
        Assert.Equal(2, socket.SendCalls);
    }

    [Fact]
    public async Task WrongDirectionAndInvalidPayloadAreNonTerminalAndSendNothing()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        object?[] invalid =
        [
            null,
            new ObjectSnapshotMessage(new(0), [], []),
            new ObjectSnapshotRequest(Guid.Empty),
        ];

        foreach (var payload in invalid)
        {
            var rejected = await fixture.Runtime.Authentication.SendControlAsync(
                authenticated.Connection, socket, payload);
            Assert.Equal(ClientControlSendStatus.InvalidMessage, rejected.Status);
            Assert.Equal(ObjectControlCodecError.InvalidPayload, rejected.CodecError);
            Assert.False(rejected.Terminal);
        }

        Assert.Equal(1, socket.SendCalls);
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out _));
        Assert.True((await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request())).Sent);
    }

    [Fact]
    public async Task ConfiguredByteLimitRejectsBeforeProviderWithoutRetiringConnection()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture(
            controlSendLimits: new(1));
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.InvalidMessage, result.Status);
        Assert.Equal(ObjectControlCodecError.MessageTooLarge, result.CodecError);
        Assert.False(result.Terminal);
        Assert.Equal(1, socket.SendCalls);
        Assert.True(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureIsContainedTerminalAndRequiresClose(bool expectedException)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        socket.SendStep = (_, _) => expectedException
            ? throw new IOException("private provider details")
            : throw new InvalidOperationException("private provider details");

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.TransportFailure, result.Status);
        Assert.True(result.Terminal);
        Assert.True(socket.Aborted);
        fixture.AssertRetired(authenticated.Connection);
        Assert.False(fixture.Runtime.Authentication.TryResolveAuthenticated(
            authenticated.Connection, out _, out _));
    }

    [Fact]
    public async Task CallerCancellationDuringSendIsTerminalBecauseFramingMayBePartial()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        socket.SendStep = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlSendStatus.Cancelled, result.Status);
        Assert.True(result.Terminal);
        Assert.True(socket.Aborted);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleRevocationCancelsAuthenticatedSendWithoutSweep(
        bool readinessLoss)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var cancellationObserved = Signal();
        socket.SendStep = async (_, token) =>
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
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection,
            socket,
            Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (readinessLoss)
        {
            var result = await fixture.Runtime.UpdateReadinessAsync(
                new(true, true, true, false, true));
            Assert.Equal(SyncReceiverReadinessStatus.Suspended, result.Status);
        }
        else
        {
            var cleanupEntered = Signal();
            using var releaseCleanup = new ManualResetEventSlim();
            fixture.Runtime.Receiver.BeforeSessionCancellationStart = () =>
            {
                cleanupEntered.TrySetResult();
                if (!releaseCleanup.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test did not release receiver cleanup.");
            };
            var disconnect = Task.Run(() => fixture.Runtime.DisconnectAsync(
                authenticated.Result.Session!.Value));
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Transport retirement is attached to exact lifecycle
                // revocation, not delayed until receiver cleanup returns.
                await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(disconnect.IsCompleted);
            }
            finally
            {
                releaseCleanup.Set();
            }

            var result = await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SyncReceiverDisconnectStatus.Disconnected, result.Status);
        }

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retired = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlSendStatus.InactiveConnection, retired.Status);
        Assert.True(retired.Terminal);
        Assert.True(socket.Aborted);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        socket.Dispose();
        Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection)).Released);
    }

    [Theory]
    [InlineData(0)] // Physical close.
    [InlineData(1)] // Root shutdown.
    [InlineData(2)] // Required dependency loss and sweep.
    public async Task LateProviderCompletionCannotReportSentAfterRetirement(int retirement)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var release = Signal();
        socket.SendStep = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation.
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (retirement == 0)
        {
            socket.Dispose();
            Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(
                authenticated.Connection)).Released);
        }
        if (retirement == 1)
        {
            Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
                (await fixture.Runtime.ShutdownAsync()).Status);
        }
        if (retirement == 2)
        {
            await fixture.Runtime.UpdateReadinessAsync(new(true, true, true, false, true));
            fixture.AssertRetired(authenticated.Connection);
        }
        Assert.False(send.IsCompleted);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);

        release.TrySetResult();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlSendStatus.InactiveConnection, result.Status);
        Assert.True(result.Terminal);
        Assert.False(fixture.Runtime.Connection.CurrentLease.IsValid);
        if (fixture.Runtime.Authentication.Count != 0)
        {
            socket.Dispose();
            Assert.True((await fixture.Runtime.Authentication.CloseTransportAsync(
                authenticated.Connection)).Released);
        }
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
    }

    [Fact]
    public async Task CloseDoesNotReleaseMemoryWhileNonCooperativeSendOwnsIt()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var release = Signal();
        ReadOnlyMemory<byte> borrowed = default;
        socket.SendStep = async (buffer, _) =>
        {
            borrowed = buffer;
            entered.TrySetResult();
            await release.Task;
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        socket.Dispose();
        Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection)).Released);
        Assert.False(IsZero(borrowed.Span));
        release.TrySetResult();

        Assert.Equal(ClientControlSendStatus.InactiveConnection,
            (await send.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.True(IsZero(borrowed.Span));
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
    }

    [Fact]
    public async Task CloseHandoffRetainsConnectionUntilConcurrentSendAndReceiveBothExit()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var receiveEntered = Signal();
        var sendEntered = Signal();
        var releaseReceive = Signal();
        var releaseSend = Signal();
        var snapshot = EncodeSnapshot(authenticated.Result.Binding!.Remote);
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            receiveEntered.TrySetResult();
            await releaseReceive.Task;
            snapshot.CopyTo(buffer);
            return new(snapshot.Length, WebSocketMessageType.Text, true);
        });
        socket.SendStep = async (_, _) =>
        {
            sendEntered.TrySetResult();
            await releaseSend.Task;
        };
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());
        await Task.WhenAll(receiveEntered.Task, sendEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));

        socket.Dispose();
        Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection)).Released);
        releaseSend.TrySetResult();
        Assert.Equal(ClientControlSendStatus.InactiveConnection,
            (await send.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);

        releaseReceive.TrySetResult();
        Assert.Equal(ClientControlReceiveStatus.InactiveConnection,
            (await receive.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
    }

    [Fact]
    public async Task RootShutdownRetainsConnectionUntilConcurrentSendAndReceiveBothExit()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var receiveEntered = Signal();
        var sendEntered = Signal();
        var releaseReceive = Signal();
        var releaseSend = Signal();
        var snapshot = EncodeSnapshot(authenticated.Result.Binding!.Remote);
        socket.ReceiveSteps.Enqueue(async (buffer, _) =>
        {
            receiveEntered.TrySetResult();
            await releaseReceive.Task; // Deliberately ignore terminal cancellation.
            snapshot.CopyTo(buffer);
            return new(snapshot.Length, WebSocketMessageType.Text, true);
        });
        socket.SendStep = async (_, _) =>
        {
            sendEntered.TrySetResult();
            await releaseSend.Task; // Deliberately ignore terminal cancellation.
        };
        var receive = fixture.Runtime.Authentication.ReceiveControlAsync(
            authenticated.Connection, socket);
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());
        await Task.WhenAll(receiveEntered.Task, sendEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = await fixture.Runtime.ShutdownAsync();

        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired, shutdown.Status);
        Assert.False(send.IsCompleted);
        Assert.False(receive.IsCompleted);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);

        socket.Dispose();
        Assert.False((await fixture.Runtime.Authentication.CloseTransportAsync(
            authenticated.Connection)).Released);
        releaseSend.TrySetResult();
        Assert.Equal(ClientControlSendStatus.InactiveConnection,
            (await send.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        Assert.Equal(SyncReceiverDisconnectStatus.CleanupRequired,
            (await fixture.Runtime.ShutdownAsync()).Status);

        releaseReceive.TrySetResult();
        Assert.Equal(ClientControlReceiveStatus.InactiveConnection,
            (await receive.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, fixture.Runtime.Authentication.Count);
        Assert.Equal(SyncReceiverDisconnectStatus.Terminated,
            (await fixture.Runtime.ShutdownAsync()).Status);
    }

    [Fact]
    public async Task CancellationCallbackDrainRetainsTheExactSendOwnership()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        using var cancellation = new CancellationTokenSource();
        var authenticated = await fixture.AuthenticateAsync(socket);
        var entered = Signal();
        var callbackEntered = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        socket.SendStep = async (_, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait(TimeSpan.FromSeconds(10));
            });
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(send.IsCompleted);
        Assert.Equal(1, fixture.Runtime.Authentication.Count);
        releaseCallback.Set();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientControlSendStatus.Cancelled, result.Status);
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
        socket.SendStep = async (_, token) =>
        {
            token.Register(() => throw new IOException("private callback details"));
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var send = fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientControlSendStatus.Cancelled, result.Status);
        Assert.True(result.CancellationCallbacksFailed);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task AbortFailureIsOnlyABoundedDiagnostic()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket
        {
            ThrowOnAbort = true,
        };
        var authenticated = await fixture.AuthenticateAsync(socket);
        socket.SendStep = (_, _) => throw new IOException("private provider details");

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.TransportFailure, result.Status);
        Assert.True(result.AbortFailed);
        Assert.True(result.Terminal);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task DirectLifecycleDisconnectInvalidatesSendAdmissionWithoutProviderIo()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.Equal(SyncReceiverDisconnectStatus.Disconnected,
            (await fixture.Runtime.DisconnectAsync(authenticated.Result.Session!.Value)).Status);

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.SendCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task InvalidatedActiveLeaseRejectsSendWithoutProviderIo()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.True(fixture.Runtime.Connection.Invalidate(
            fixture.Runtime.Connection.CurrentLease));

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.SendCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Fact]
    public async Task LostReceiverAuthorityRejectsSendWithoutProviderIo()
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();
        using var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        var authenticated = await fixture.AuthenticateAsync(socket);
        Assert.True(fixture.Runtime.Receiver.Authority.Invalidate(
            authenticated.Result.Session!.Value));

        var result = await fixture.Runtime.Authentication.SendControlAsync(
            authenticated.Connection, socket, Request());

        Assert.Equal(ClientControlSendStatus.InactiveConnection, result.Status);
        Assert.Equal(1, socket.SendCalls);
        fixture.AssertRetired(authenticated.Connection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16777217)]
    public async Task InvalidSendBoundsAreRejectedAtComposition(int bytes)
    {
        await using var fixture = new ClientAuthenticationCoordinatorTests.Fixture();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClientAuthenticationCoordinator(
                fixture.Runtime.Lifecycle,
                fixture.Runtime.ControlIngress,
                fixture.Runtime.Readiness,
                ClientAuthenticationLimits.Default,
                TimeProvider.System,
                ClientControlReceiveLimits.Default,
                new(bytes)));
    }

    private static ObjectSnapshotRequest Request() => new(Guid.NewGuid());

    private static Guid? RequestId(object payload) => payload switch
    {
        CreateObjectRequest request => request.ClientRequestId,
        UpdateObjectRequest request => request.RequestId,
        DeleteObjectRequest request => request.RequestId,
        ObjectSnapshotRequest request => request.RequestId,
        ObjectEventBatchRequest request => request.RequestId,
        SaveSceneRequest request => request.RequestId,
        ReadSceneRequest request => request.RequestId,
        ObjectStreamAcknowledgement => null,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };

    private static byte[] EncodeSnapshot(RSyncStageCraft.Core.Receiver.ReceiverRemoteConnection remote)
    {
        var envelope = new SessionMessageEnvelope(
            ProtocolConstants.CurrentVersion,
            ObjectProtocolMessageType.ObjectSnapshot,
            Guid.NewGuid(),
            remote.SessionId,
            remote.SessionGeneration,
            remote.ConnectionEpoch);
        Assert.True(new ObjectControlMessageCodec().TryEncode(
            envelope,
            new ObjectSnapshotMessage(new(0), [], []),
            out var bytes,
            out var error), error.ToString());
        return bytes;
    }

    private static async Task<ObjectControlMessage> AuthorizeAndReadControlAsync(
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
                await server.SendAsync(
                    response.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response);
            }
            var read = await new WebSocketControlMessageReader(server).ReadAsync();
            Assert.True(read.Received);
            Assert.True(new ObjectControlMessageCodec().TryDecode(
                read.Payload, out var decoded, out var error), error.ToString());
            return Assert.IsType<ObjectControlMessage>(decoded);
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

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
