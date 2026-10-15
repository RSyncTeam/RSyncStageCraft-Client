using System.Net.WebSockets;

namespace RSyncStageCraft.Core.Protocol;

public readonly record struct WebSocketControlReadLimits(
    int MaximumMessageBytes,
    int MaximumReceiveSegments,
    TimeSpan PartialMessageTimeout)
{
    public const int MaximumReceiveSegmentsHardLimit = 4096;
    public static readonly TimeSpan MaximumPartialMessageTimeout = TimeSpan.FromMinutes(2);
    public static WebSocketControlReadLimits Default => new(
        ProtocolLimits.MaximumControlMessageBytes, MaximumReceiveSegmentsHardLimit, TimeSpan.FromSeconds(30));

    public bool IsValid =>
        MaximumMessageBytes > 0 && MaximumMessageBytes <= ProtocolLimits.MaximumControlMessageBytes &&
        MaximumReceiveSegments > 0 && MaximumReceiveSegments <= MaximumReceiveSegmentsHardLimit &&
        PartialMessageTimeout > TimeSpan.Zero && PartialMessageTimeout <= MaximumPartialMessageTimeout;
}

public enum WebSocketControlReadStatus
{
    Invalid,
    MessageReceived,
    Busy,
    Cancelled,
    PartialMessageTimedOut,
    MessageTooLarge,
    SegmentLimitExceeded,
    UnexpectedMessageType,
    RemoteClosed,
    TransportFailure,
    Terminated,
}

public readonly record struct WebSocketControlReadResult(
    WebSocketControlReadStatus Status,
    ReadOnlyMemory<byte> Payload = default)
{
    public bool Received => Status == WebSocketControlReadStatus.MessageReceived;
    public bool AbortFailed { get; init; }
}

/// <summary>
/// One text message per call from an already-established WebSocket. Exactly one
/// reader must own that socket's receive path. This class opens no endpoint and
/// does not authenticate, authorize, decode JSON, queue messages, or own session
/// cleanup. The host owns/disposes the socket and passes its exact connection
/// cancellation token. Every terminal result requires host lifecycle cleanup.
/// </summary>
public sealed class WebSocketControlMessageReader
{
    public const int ReceiveBufferBytes = 16 * 1024;
    private readonly WebSocket socket;
    private readonly WebSocketControlReadLimits limits;
    private int reading;
    private int terminated;
    private int abortFailed;

    public WebSocketControlMessageReader(WebSocket socket, WebSocketControlReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var selected = limits ?? WebSocketControlReadLimits.Default;
        if (!selected.IsValid)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.socket = socket;
        this.limits = selected;
    }

    public bool IsTerminated => Volatile.Read(ref terminated) != 0;

    /// <summary>
    /// Busy and cancellation before the first receive consume no bytes. Once a
    /// receive has started, cancellation/failure makes the reader terminal; a
    /// partial message is never mistaken for the next one. Successful payloads
    /// own independent buffers which the reader does not retain or reuse. The
    /// host must finish dispatch/retry before reading another message and bound
    /// aggregate queued bytes independently.
    /// </summary>
    public async Task<WebSocketControlReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            return new(WebSocketControlReadStatus.Busy);

        try
        {
            if (IsTerminated)
                return new(WebSocketControlReadStatus.Terminated) { AbortFailed = Volatile.Read(ref abortFailed) != 0 };
            if (cancellationToken.IsCancellationRequested)
                return new(WebSocketControlReadStatus.Cancelled);

            // Scratch is fixed-size; accumulation grows only after checking
            // the remaining message budget. No advertised wire length allocates
            // an unbounded buffer, and no final full-message copy is required.
            var scratch = new byte[Math.Min(ReceiveBufferBytes, limits.MaximumMessageBytes + 1)];
            var payload = Array.Empty<byte>();
            var length = 0;
            using var deadline = new CancellationTokenSource();
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token);
            try
            {
                for (var segment = 0; segment < limits.MaximumReceiveSegments; segment++)
                {
                    var received = await socket.ReceiveAsync(scratch.AsMemory(), receiveCancellation.Token)
                        .ConfigureAwait(false);
                    // Cancellation may race a successful receive. Never publish
                    // bytes after the caller/partial-message deadline has won.
                    receiveCancellation.Token.ThrowIfCancellationRequested();
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        Volatile.Write(ref terminated, 1);
                        // The host may finish the close handshake; no peer text
                        // or partial payload is reflected into diagnostics.
                        return new(WebSocketControlReadStatus.RemoteClosed);
                    }
                    if (received.MessageType != WebSocketMessageType.Text)
                        return Fail(WebSocketControlReadStatus.UnexpectedMessageType);
                    if (received.Count < 0 || received.Count > scratch.Length)
                        return Fail(WebSocketControlReadStatus.TransportFailure);
                    if (received.Count > limits.MaximumMessageBytes - length)
                        return Fail(WebSocketControlReadStatus.MessageTooLarge);

                    var required = length + received.Count;
                    if (required > payload.Length)
                    {
                        var capacity = Math.Min(limits.MaximumMessageBytes,
                            Math.Max(required, Math.Max(256, payload.Length * 2)));
                        Array.Resize(ref payload, capacity);
                    }
                    scratch.AsSpan(0, received.Count).CopyTo(payload.AsSpan(length));
                    length = required;
                    if (received.EndOfMessage)
                        return new(WebSocketControlReadStatus.MessageReceived, payload.AsMemory(0, length));

                    // Idle time before the first segment belongs to the host's
                    // heartbeat/connection policy. Once partial input exists,
                    // this deadline is armed once, never reset by trickling data.
                    if (segment == 0)
                        deadline.CancelAfter(limits.PartialMessageTimeout);
                }
                return Fail(WebSocketControlReadStatus.SegmentLimitExceeded);
            }
            catch (OperationCanceledException) when (receiveCancellation.IsCancellationRequested)
            {
                return Fail(cancellationToken.IsCancellationRequested
                    ? WebSocketControlReadStatus.Cancelled
                    : WebSocketControlReadStatus.PartialMessageTimedOut);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
            {
                return Fail(WebSocketControlReadStatus.TransportFailure);
            }
            catch
            {
                // Unexpected provider failures still cannot leave a partially
                // consumed stream reusable. Preserve the original exception.
                Fail(WebSocketControlReadStatus.TransportFailure);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref reading, 0);
        }
    }

    private WebSocketControlReadResult Fail(WebSocketControlReadStatus status)
    {
        Volatile.Write(ref terminated, 1);
        try
        {
            socket.Abort();
        }
        catch
        {
            // Best-effort transport abort must not replace the read failure or
            // resurrect the reader. The host still owns disposal and cleanup.
            Volatile.Write(ref abortFailed, 1);
        }
        return new(status) { AbortFailed = Volatile.Read(ref abortFailed) != 0 };
    }
}
