using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RSyncStageCraft.Core.Appearance;

// Compression and blob identity: a blob travels and is stored as a
// versioned envelope around its raw content, raw or Brotli-compressed. The
// identity is always the SHA-256 of the RAW (uncompressed) content; the
// envelope never changes bundle ids, deduplication, references or quotas.

/// <summary>How an envelope's payload encodes the raw content (format v1).</summary>
public enum AppearanceBlobCodec : byte
{
    /// <summary>The payload is the raw content.</summary>
    Raw = 0,
    /// <summary>The payload is one Brotli stream (System.IO.Compression) of the raw content.</summary>
    Brotli = 1,
}

public enum AppearanceEnvelopeStatus
{
    Valid,
    TooShort,
    BadMagic,
    UnsupportedVersion,
    UnknownCodec,
    InvalidHeader,
    SizeOutOfRange,
}

/// <summary>
/// The fixed 56-byte envelope header, format v1 (all integers little-endian):
/// <code>
///  0  4  magic "RSCB"
///  4  1  format version (1)
///  5  1  codec (0 raw, 1 Brotli)
///  6  2  reserved, must be 0
///  8  8  uncompressed (raw) size, 1..64 MiB
/// 16  8  stored payload size: raw = the raw size; Brotli = 1..raw size
/// 24 32  SHA-256 of the raw content (the blob's identity)
/// 56     payload (exactly the stored size), nothing after it
/// </code>
/// </summary>
public sealed record AppearanceBlobEnvelopeHeader(
    AppearanceBlobCodec Codec,
    long UncompressedSize,
    long StoredSize,
    ContentHash UncompressedHash)
{
    public const int Length = 56;
    public const byte FormatVersion = 1;

    /// <summary>The largest envelope (header plus the largest permitted payload).</summary>
    public const long MaximumEnvelopeBytes = Length + AppearanceLimits.MaximumBlobBytes;

    public static ReadOnlySpan<byte> Magic => "RSCB"u8;

    /// <summary>The raw blob this envelope carries: its identity and raw size.</summary>
    public AppearanceBlobRef Blob => new(UncompressedHash, UncompressedSize);

    public long EnvelopeLength => Length + StoredSize;

    public bool IsValid =>
        Codec is AppearanceBlobCodec.Raw or AppearanceBlobCodec.Brotli &&
        UncompressedHash.IsValid &&
        UncompressedSize is > 0 and <= AppearanceLimits.MaximumBlobBytes &&
        (Codec == AppearanceBlobCodec.Raw
            ? StoredSize == UncompressedSize
            : StoredSize is > 0 && StoredSize <= UncompressedSize);

    public byte[] ToBytes()
    {
        if (!IsValid)
            throw new InvalidOperationException("The envelope header is not valid.");
        var bytes = new byte[Length];
        Magic.CopyTo(bytes);
        bytes[4] = FormatVersion;
        bytes[5] = (byte)Codec;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), UncompressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), StoredSize);
        Convert.FromHexString(UncompressedHash.Value).CopyTo(bytes, 24);
        return bytes;
    }

    /// <summary>Reads and validates a header; never normalizes anything.</summary>
    public static AppearanceEnvelopeStatus TryRead(ReadOnlySpan<byte> source, out AppearanceBlobEnvelopeHeader? header)
    {
        header = null;
        if (source.Length < Length)
            return AppearanceEnvelopeStatus.TooShort;
        if (!source[..4].SequenceEqual(Magic))
            return AppearanceEnvelopeStatus.BadMagic;
        if (source[4] != FormatVersion)
            return AppearanceEnvelopeStatus.UnsupportedVersion;
        if (source[5] is not ((byte)AppearanceBlobCodec.Raw or (byte)AppearanceBlobCodec.Brotli))
            return AppearanceEnvelopeStatus.UnknownCodec;
        if (source[6] != 0 || source[7] != 0)
            return AppearanceEnvelopeStatus.InvalidHeader;
        if (!ContentHash.TryParse(Convert.ToHexString(source.Slice(24, 32)).ToLowerInvariant(), out var hash))
            return AppearanceEnvelopeStatus.InvalidHeader;
        var candidate = new AppearanceBlobEnvelopeHeader(
            (AppearanceBlobCodec)source[5],
            BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            hash);
        if (!candidate.IsValid)
            return AppearanceEnvelopeStatus.SizeOutOfRange;
        header = candidate;
        return AppearanceEnvelopeStatus.Valid;
    }

    /// <summary>Reads exactly one header from a stream (fewer bytes = TooShort).</summary>
    public static async Task<(AppearanceEnvelopeStatus Status, AppearanceBlobEnvelopeHeader? Header)> ReadAsync(
        Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = new byte[Length];
        var read = await source.ReadAtLeastAsync(bytes, Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read < Length)
            return (AppearanceEnvelopeStatus.TooShort, null);
        var status = TryRead(bytes, out var header);
        return (status, header);
    }
}

public enum AppearancePayloadStatus
{
    /// <summary>Valid so far (Append) or completely valid (Complete).</summary>
    Valid,
    /// <summary>The Brotli stream is invalid.</summary>
    Corrupt,
    /// <summary>Bytes follow the end of the Brotli stream inside the payload.</summary>
    TrailingData,
    /// <summary>The payload ended before its Brotli stream did.</summary>
    Truncated,
    /// <summary>The raw output ran past the declared raw size (e.g. a decompression bomb).</summary>
    Overrun,
    /// <summary>The raw output is shorter than the declared raw size.</summary>
    RawSizeMismatch,
    /// <summary>The raw output's SHA-256 is not the declared identity.</summary>
    HashMismatch,
}

/// <summary>
/// Streams an envelope's payload through its decoder while counting and
/// hashing the raw output, with bounded memory. Raw output never exceeds the
/// declared size by more than one decoder buffer before it is refused. An
/// optional sink receives the raw output (a client writing its cache file).
/// Brotli is decoded with System.IO.Compression's <see cref="BrotliDecoder"/>
/// (the decoder behind BrotliStream), whose span API reports exactly how much
/// input it consumed, so bytes after the end of the stream are detected.
/// </summary>
public sealed class AppearancePayloadVerifier : IDisposable
{
    private const int OutputBytes = 64 * 1024;
    private readonly AppearanceBlobEnvelopeHeader header;
    private readonly IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Action<ReadOnlySpan<byte>>? sink;
    private readonly byte[]? output;
    private BrotliDecoder decoder;
    private bool brotliDone;
    private AppearancePayloadStatus failure = AppearancePayloadStatus.Valid;
    private bool disposed;

    public AppearancePayloadVerifier(AppearanceBlobEnvelopeHeader header, Action<ReadOnlySpan<byte>>? sink = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!header.IsValid)
            throw new ArgumentException("The envelope header is not valid.", nameof(header));
        this.header = header;
        this.sink = sink;
        if (header.Codec == AppearanceBlobCodec.Brotli)
        {
            decoder = new BrotliDecoder();
            output = ArrayPool<byte>.Shared.Rent(OutputBytes);
        }
    }

    public long StoredBytes { get; private set; }
    public long RawBytes { get; private set; }

    /// <summary>Feeds the next payload bytes. Once it fails it stays failed.</summary>
    public AppearancePayloadStatus Append(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure != AppearancePayloadStatus.Valid || payload.IsEmpty)
            return failure;
        StoredBytes += payload.Length;
        if (header.Codec == AppearanceBlobCodec.Raw)
            return failure = Emit(payload);

        while (true)
        {
            if (brotliDone)
                return failure = AppearancePayloadStatus.TrailingData;
            var status = decoder.Decompress(payload, output!, out var consumed, out var written);
            payload = payload[consumed..];
            if (written > 0)
            {
                var emitted = Emit(output.AsSpan(0, written));
                if (emitted != AppearancePayloadStatus.Valid)
                    return failure = emitted;
            }
            switch (status)
            {
                case OperationStatus.Done:
                    brotliDone = true;
                    if (!payload.IsEmpty)
                        return failure = AppearancePayloadStatus.TrailingData;
                    return AppearancePayloadStatus.Valid;
                case OperationStatus.DestinationTooSmall when consumed > 0 || written > 0:
                    continue;
                case OperationStatus.NeedMoreData when payload.IsEmpty:
                    return AppearancePayloadStatus.Valid;
                case OperationStatus.NeedMoreData when consumed > 0 || written > 0:
                    continue;
                default:
                    // Invalid data, or no progress at all.
                    return failure = AppearancePayloadStatus.Corrupt;
            }
        }
    }

    /// <summary>
    /// After the whole payload: the stream ended, the raw size and SHA-256 are
    /// exactly the declared ones.
    /// </summary>
    public AppearancePayloadStatus Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure != AppearancePayloadStatus.Valid)
            return failure;
        if (header.Codec == AppearanceBlobCodec.Brotli && !brotliDone)
            return failure = AppearancePayloadStatus.Truncated;
        if (RawBytes != header.UncompressedSize)
            return failure = AppearancePayloadStatus.RawSizeMismatch;
        var digest = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        return failure = string.Equals(digest, header.UncompressedHash.Value, StringComparison.Ordinal)
            ? AppearancePayloadStatus.Valid
            : AppearancePayloadStatus.HashMismatch;
    }

    private AppearancePayloadStatus Emit(ReadOnlySpan<byte> raw)
    {
        RawBytes += raw.Length;
        if (RawBytes > header.UncompressedSize)
            return AppearancePayloadStatus.Overrun;
        sha.AppendData(raw);
        sink?.Invoke(raw);
        return AppearancePayloadStatus.Valid;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        sha.Dispose();
        if (output is not null)
        {
            decoder.Dispose();
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}

/// <summary>Streaming envelope encoding and decoding with bounded memory.</summary>
public static class AppearanceBlobEnvelope
{
    /// <summary>Provisional Brotli quality; may be tuned from benchmarks without changing the envelope.</summary>
    public const int BrotliQuality = 5;

    /// <summary>Brotli is kept only when the whole stored envelope is at least this much smaller than the raw one.</summary>
    public const int MinimumSavingPercent = 5;

    private const int BufferBytes = 81920;

    /// <summary>
    /// The client rule: Brotli is kept only when the complete stored envelope
    /// (header plus Brotli payload) is at least 5% smaller than the complete
    /// raw envelope.
    /// </summary>
    public static bool KeepsBrotli(long rawSize, long compressedSize) =>
        compressedSize > 0 && compressedSize <= rawSize &&
        (AppearanceBlobEnvelopeHeader.Length + compressedSize) * 100 <=
            (AppearanceBlobEnvelopeHeader.Length + rawSize) * (long)(100 - MinimumSavingPercent);

    /// <summary>
    /// Writes the envelope for the raw content of <paramref name="raw"/> (read
    /// from its current position to its end, which must be seekable) to
    /// <paramref name="output"/>. The raw content is compressed with
    /// <see cref="BrotliStream"/> into <paramref name="scratchPath"/> (a
    /// temporary file the caller owns and deletes); Brotli is kept only when the
    /// complete stored envelope is at least 5% smaller than the raw envelope,
    /// otherwise the raw bytes are sent.
    /// </summary>
    public static async Task<AppearanceBlobEnvelopeHeader> EncodeAsync(
        Stream raw, Stream output, string scratchPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(output);
        if (!raw.CanSeek)
            throw new ArgumentException("The raw content must be seekable (a file).", nameof(raw));
        var start = raw.Position;
        long rawSize = 0;
        ContentHash hash;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var scratch = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous))
            await using (var brotli = new BrotliStream(scratch, new BrotliCompressionOptions { Quality = BrotliQuality }, leaveOpen: false))
            {
                int read;
                while ((read = await raw.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    rawSize += read;
                    if (rawSize > AppearanceLimits.MaximumBlobBytes)
                        throw new InvalidDataException("The content is larger than the largest permitted blob.");
                    sha.AppendData(buffer, 0, read);
                    await brotli.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            if (rawSize == 0)
                throw new InvalidDataException("A blob cannot be empty.");
            ContentHash.TryParse(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), out hash);

            var compressed = new FileInfo(scratchPath).Length;
            var useBrotli = KeepsBrotli(rawSize, compressed);
            var header = useBrotli
                ? new AppearanceBlobEnvelopeHeader(AppearanceBlobCodec.Brotli, rawSize, compressed, hash)
                : new AppearanceBlobEnvelopeHeader(AppearanceBlobCodec.Raw, rawSize, rawSize, hash);
            await output.WriteAsync(header.ToBytes(), cancellationToken).ConfigureAwait(false);
            if (useBrotli)
            {
                await using var payload = new FileStream(scratchPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.Asynchronous);
                await payload.CopyToAsync(output, BufferBytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                raw.Position = start;
                long copied = 0;
                int read;
                using var again = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (copied < rawSize &&
                       (read = await raw.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferBytes, rawSize - copied)), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    again.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }
                // The second pass must read exactly what the first one hashed.
                if (copied != rawSize ||
                    !string.Equals(Convert.ToHexString(again.GetHashAndReset()).ToLowerInvariant(), hash.Value, StringComparison.Ordinal))
                    throw new InvalidDataException("The content changed while it was encoded.");
            }
            return header;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one complete envelope from <paramref name="envelope"/>, decodes it
    /// to <paramref name="rawOutput"/> and checks it is exactly the expected
    /// blob (raw size and SHA-256), the stream ends after the payload, and
    /// nothing is left over. The caller publishes the output only on Valid.
    /// </summary>
    public static async Task<(AppearanceEnvelopeStatus Envelope, AppearancePayloadStatus Payload)> DecodeAsync(
        Stream envelope, Stream rawOutput, AppearanceBlobRef expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(rawOutput);
        var (status, header) = await AppearanceBlobEnvelopeHeader.ReadAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (status != AppearanceEnvelopeStatus.Valid)
            return (status, AppearancePayloadStatus.Corrupt);
        if (header!.Blob != expected)
            return (AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.HashMismatch);
        // Raw output goes straight to the output stream: memory stays bounded
        // however far a Brotli payload expands (up to the declared size).
        using var verifier = new AppearancePayloadVerifier(header, raw => rawOutput.Write(raw));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long remaining = header.StoredSize;
            while (true)
            {
                var read = await envelope.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferBytes, remaining + 1)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (read > remaining)
                    return (AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.TrailingData);
                remaining -= read;
                var result = verifier.Append(buffer.AsSpan(0, read));
                if (result != AppearancePayloadStatus.Valid)
                    return (AppearanceEnvelopeStatus.Valid, result);
            }
            if (remaining != 0)
                return (AppearanceEnvelopeStatus.Valid, header.Codec == AppearanceBlobCodec.Brotli
                    ? AppearancePayloadStatus.Truncated
                    : AppearancePayloadStatus.RawSizeMismatch);
            return (AppearanceEnvelopeStatus.Valid, verifier.Complete());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
