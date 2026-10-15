#nullable enable

using System.Net.WebSockets;
using System.Text;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

public sealed class WebSocketControlMessageReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task RealWebSocketFragmentsReassembleIntoTheExistingCodec(int fragmentBytes)
    {
        var codec = new ObjectControlMessageCodec();
        foreach (var sample in ObjectControlMessageCodecTests.Messages())
        {
            var envelope = new SessionMessageEnvelope(ProtocolConstants.CurrentVersion, (ObjectProtocolMessageType)sample[0], ObjectControlMessageCodecTests.MessageIdFor(sample[1]), 1, 2, 57);
            Assert.True(codec.TryEncode(envelope, sample[1], out var expected, out _));
            using var input = new MemoryStream(await FrameAsync(expected, fragmentBytes));
            using var socket = CreateSocket(input, isServer: false);
            var reader = new WebSocketControlMessageReader(socket);
            var result = await reader.ReadAsync();
            Assert.True(result.Received);
            Assert.Equal(expected, result.Payload.ToArray());
            Assert.True(codec.TryDecode(result.Payload, out var message, out _));
            Assert.Equal(envelope, message!.Envelope);
            Assert.Equal(sample[1].GetType(), message.Payload.GetType());
            Assert.False(reader.IsTerminated);
        }
    }

    [Fact]
    public async Task RealLargeFrameUsesBoundedReceiveChunksAndDoesNotMergeMessages()
    {
        var first = Encoding.UTF8.GetBytes(new string('x', WebSocketControlMessageReader.ReceiveBufferBytes * 3 + 7));
        var second = Encoding.UTF8.GetBytes("second € message");
        using var output = new MemoryStream();
        using (var sender = CreateSocket(output, isServer: true))
        {
            await sender.SendAsync(first.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            await sender.SendAsync(second.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            var wire = output.ToArray();
            using var input = new MemoryStream(wire);
            using var socket = CreateSocket(input, isServer: false);
            var reader = new WebSocketControlMessageReader(socket);
            var one = await reader.ReadAsync();
            var two = await reader.ReadAsync();
            Assert.Equal(first, one.Payload.ToArray());
            Assert.Equal(second, two.Payload.ToArray());
            Assert.Equal(first, one.Payload.ToArray()); // The next read did not reuse its storage.
        }
    }

    [Theory]
    [InlineData(3, WebSocketControlReadStatus.MessageReceived)]
    [InlineData(2, WebSocketControlReadStatus.MessageTooLarge)]
    public async Task ExactByteLimitIsAcceptedAndOverflowIsTerminal(int limit, WebSocketControlReadStatus expected)
    {
        using var socket = new ScriptedSocket();
        socket.Add("ab"u8.ToArray(), end: false);
        socket.Add("c"u8.ToArray());
        var reader = new WebSocketControlMessageReader(socket, Limits(bytes: limit));
        var result = await reader.ReadAsync();
        Assert.Equal(expected, result.Status);
        if (result.Received)
            Assert.Equal("abc", Encoding.UTF8.GetString(result.Payload.Span));
        else
            await AssertTerminal(reader, socket, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SegmentsIncludingEmptyOnesHaveAnExactLimit(bool lastEndsMessage)
    {
        using var socket = new ScriptedSocket();
        socket.Add(Array.Empty<byte>(), end: false);
        socket.Add(Array.Empty<byte>(), end: false);
        socket.Add(Array.Empty<byte>(), end: lastEndsMessage);
        socket.Add("must-not-read"u8.ToArray());
        var reader = new WebSocketControlMessageReader(socket, Limits(segments: 3));
        var result = await reader.ReadAsync();
        Assert.Equal(3, socket.ReceiveCalls);
        if (lastEndsMessage)
        {
            Assert.True(result.Received);
            Assert.True(result.Payload.IsEmpty);
            Assert.False(reader.IsTerminated);
        }
        else
        {
            Assert.Equal(WebSocketControlReadStatus.SegmentLimitExceeded, result.Status);
            await AssertTerminal(reader, socket, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryInputNeverPublishesPartialText(bool afterPartial)
    {
        using var socket = new ScriptedSocket();
        if (afterPartial)
            socket.Add("partial"u8.ToArray(), end: false);
        socket.Add(new byte[] { 1, 2 }, type: WebSocketMessageType.Binary);
        var reader = new WebSocketControlMessageReader(socket);
        var result = await reader.ReadAsync();
        Assert.Equal(WebSocketControlReadStatus.UnexpectedMessageType, result.Status);
        await AssertTerminal(reader, socket, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCloseDiscardsPartialInputAndLeavesHandshakeToHost(bool afterPartial)
    {
        using var socket = new ScriptedSocket();
        if (afterPartial)
            socket.Add("partial"u8.ToArray(), end: false);
        socket.Add(Array.Empty<byte>(), type: WebSocketMessageType.Close);
        var reader = new WebSocketControlMessageReader(socket);
        var result = await reader.ReadAsync();
        Assert.Equal(WebSocketControlReadStatus.RemoteClosed, result.Status);
        Assert.True(result.Payload.IsEmpty);
        Assert.True(reader.IsTerminated);
        Assert.False(socket.Aborted);
        Assert.Equal(WebSocketControlReadStatus.Terminated, (await reader.ReadAsync()).Status);
    }

    [Fact]
    public async Task ConcurrentReadReturnsBusyWithoutIssuingAnotherSocketReceive()
    {
        using var socket = new ScriptedSocket();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Steps.Enqueue(async (buffer, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            buffer.Span[0] = (byte)'x';
            return new(1, WebSocketMessageType.Text, true);
        });
        var reader = new WebSocketControlMessageReader(socket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = reader.ReadAsync(timeout.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketControlReadStatus.Busy, (await reader.ReadAsync()).Status);
            Assert.Equal(1, socket.ReceiveCalls);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True((await first).Received);
    }

    [Fact]
    public async Task PreCancelledReadConsumesNothingAndCanBeRetried()
    {
        using var socket = new ScriptedSocket();
        socket.Add("ok"u8.ToArray());
        var reader = new WebSocketControlMessageReader(socket);
        Assert.Equal(WebSocketControlReadStatus.Cancelled,
            (await reader.ReadAsync(new CancellationToken(canceled: true))).Status);
        Assert.Equal(0, socket.ReceiveCalls);
        Assert.False(reader.IsTerminated);
        Assert.True((await reader.ReadAsync()).Received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterReceiveStartsIsTerminalEvenIfSocketReturnsSuccess(bool returnsSuccess)
    {
        using var socket = new ScriptedSocket();
        using var cancel = new CancellationTokenSource();
        socket.Steps.Enqueue((buffer, token) =>
        {
            cancel.Cancel();
            if (!returnsSuccess)
                token.ThrowIfCancellationRequested();
            buffer.Span[0] = (byte)'x';
            return ValueTask.FromResult(new ValueWebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        });
        var reader = new WebSocketControlMessageReader(socket);
        var result = await reader.ReadAsync(cancel.Token);
        Assert.Equal(WebSocketControlReadStatus.Cancelled, result.Status);
        await AssertTerminal(reader, socket, result);
    }

    [Fact]
    public async Task PartialMessageDeadlineAppliesAfterSeveralSegments()
    {
        using var socket = new ScriptedSocket();
        socket.Add("a"u8.ToArray(), end: false);
        socket.Add("b"u8.ToArray(), end: false);
        socket.Steps.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        var reader = new WebSocketControlMessageReader(socket, Limits(timeout: TimeSpan.FromMilliseconds(30)));
        var result = await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketControlReadStatus.PartialMessageTimedOut, result.Status);
        // A heavily delayed test thread may observe expiry on the second
        // receive rather than entering the third; both discard partial input.
        Assert.InRange(socket.ReceiveCalls, 2, 3);
        await AssertTerminal(reader, socket, result);
    }

    [Theory]
    [InlineData("socket")]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("unexpected")]
    public async Task ReadFailuresCannotLeaveThePartialStreamReusable(string failure)
    {
        using var socket = new ScriptedSocket();
        socket.Add("partial"u8.ToArray(), end: false);
        socket.Steps.Enqueue((_, _) => throw failure switch
        {
            "socket" => new WebSocketException(),
            "io" => new IOException(),
            "disposed" => new ObjectDisposedException("test socket"),
            _ => new InvalidOperationException("test provider failure"),
        });
        var reader = new WebSocketControlMessageReader(socket);
        if (failure == "unexpected")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync());
            Assert.True(reader.IsTerminated);
            Assert.True(socket.Aborted);
        }
        else
        {
            var result = await reader.ReadAsync();
            Assert.Equal(WebSocketControlReadStatus.TransportFailure, result.Status);
            await AssertTerminal(reader, socket, result);
        }
    }

    [Fact]
    public async Task AbortFailureIsBoundedDiagnosticAndDoesNotReplaceTheReadFailure()
    {
        using var socket = new ScriptedSocket { ThrowOnAbort = true };
        socket.Add(new byte[] { 1 }, type: WebSocketMessageType.Binary);
        var reader = new WebSocketControlMessageReader(socket);
        var result = await reader.ReadAsync();
        Assert.Equal(WebSocketControlReadStatus.UnexpectedMessageType, result.Status);
        Assert.True(result.AbortFailed);
        Assert.True(result.Payload.IsEmpty);
        Assert.True(reader.IsTerminated);
        Assert.True((await reader.ReadAsync()).AbortFailed);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task InvalidReceiveCountCannotAccessBeyondScratchBuffer()
    {
        using var socket = new ScriptedSocket();
        socket.Steps.Enqueue((_, _) => ValueTask.FromResult(
            new ValueWebSocketReceiveResult(100, WebSocketMessageType.Text, true)));
        var reader = new WebSocketControlMessageReader(socket, Limits(bytes: 3));
        var result = await reader.ReadAsync();
        Assert.Equal(WebSocketControlReadStatus.TransportFailure, result.Status);
        await AssertTerminal(reader, socket, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWebSocketHandlesBothServerAndClientFraming(bool serverReads)
    {
        using var output = new MemoryStream();
        using var sender = CreateSocket(output, isServer: !serverReads);
        byte[] expected = Encoding.UTF8.GetBytes("test € payload");
        await sender.SendAsync(expected.AsMemory(0, 6), WebSocketMessageType.Text, false, CancellationToken.None);
        await sender.SendAsync(expected.AsMemory(6), WebSocketMessageType.Text, true, CancellationToken.None);
        using var input = new MemoryStream(output.ToArray());
        using var socket = CreateSocket(input, isServer: serverReads);
        var result = await new WebSocketControlMessageReader(socket).ReadAsync();
        Assert.True(result.Received);
        Assert.Equal(expected, result.Payload.ToArray());
    }

    [Fact]
    public async Task RealWebSocketRejectsInvalidUtf8WithoutExposingAnyPartialPayload()
    {
        var wire = await FrameAsync(new byte[] { 0xC3, 0x28 }, 1);
        using var input = new MemoryStream();
        input.Write(wire);
        input.Position = 0;
        using var socket = CreateSocket(input, isServer: false);
        var reader = new WebSocketControlMessageReader(socket);
        var result = await reader.ReadAsync();
        Assert.Equal(WebSocketControlReadStatus.TransportFailure, result.Status);
        Assert.True(result.Payload.IsEmpty);
        Assert.True(reader.IsTerminated);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(16777217, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 4097, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 120001)]
    public void InvalidLimitsAreRejectedBeforeAnyRead(int bytes, int segments, int timeoutMilliseconds)
    {
        using var socket = new ScriptedSocket();
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketControlMessageReader(socket,
            new(bytes, segments, TimeSpan.FromMilliseconds(timeoutMilliseconds))));
        Assert.Equal(0, socket.ReceiveCalls);
        Assert.Throws<ArgumentNullException>(() => new WebSocketControlMessageReader(null!));
    }

    internal static async Task<byte[]> FrameAsync(byte[] payload, int fragmentBytes)
    {
        using var stream = new MemoryStream();
        using var socket = CreateSocket(stream, isServer: true);
        for (var offset = 0; offset < payload.Length; offset += fragmentBytes)
        {
            var count = Math.Min(fragmentBytes, payload.Length - offset);
            await socket.SendAsync(payload.AsMemory(offset, count), WebSocketMessageType.Text,
                offset + count == payload.Length, CancellationToken.None);
        }
        return stream.ToArray();
    }

    internal static WebSocket CreateSocket(Stream stream, bool isServer) => WebSocket.CreateFromStream(stream,
        new WebSocketCreationOptions { IsServer = isServer, KeepAliveInterval = Timeout.InfiniteTimeSpan });

    private static WebSocketControlReadLimits Limits(int bytes = 128, int segments = 8, TimeSpan? timeout = null) =>
        new(bytes, segments, timeout ?? TimeSpan.FromSeconds(5));

    private static async Task AssertTerminal(WebSocketControlMessageReader reader, ScriptedSocket socket,
        WebSocketControlReadResult result)
    {
        Assert.True(result.Payload.IsEmpty);
        Assert.True(reader.IsTerminated);
        Assert.True(socket.Aborted);
        var calls = socket.ReceiveCalls;
        Assert.Equal(WebSocketControlReadStatus.Terminated, (await reader.ReadAsync()).Status);
        Assert.Equal(calls, socket.ReceiveCalls);
    }

    private sealed class ScriptedSocket : WebSocket
    {
        public Queue<Func<Memory<byte>, CancellationToken, ValueTask<ValueWebSocketReceiveResult>>> Steps { get; } = new();
        public int ReceiveCalls { get; private set; }
        public bool Aborted { get; private set; }
        public bool ThrowOnAbort { get; init; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public void Add(byte[] data, bool end = true, WebSocketMessageType type = WebSocketMessageType.Text) =>
            Steps.Enqueue((buffer, _) =>
            {
                Assert.True(data.Length <= buffer.Length);
                data.CopyTo(buffer);
                return ValueTask.FromResult(new ValueWebSocketReceiveResult(data.Length, type, end));
            });
        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            ReceiveCalls++;
            return Steps.Dequeue()(buffer, token);
        }
        public override void Abort()
        {
            Aborted = true;
            if (ThrowOnAbort)
                throw new IOException("Test abort failure");
        }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => throw new NotSupportedException();
    }
}
