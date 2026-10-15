using System.IO.Compression;
using RSyncStageCraft.Core.Appearance;

namespace RSyncStageCraft.Tests;

/// <summary>Builds blob envelopes (format v1) for the appearance tests.</summary>
internal static class TestEnvelopes
{
    public static AppearanceBlobRef Ref(byte[] raw) => new(ContentHash.Of(raw), raw.Length);

    public static AppearanceBlobEnvelopeHeader RawHeader(byte[] raw) =>
        new(AppearanceBlobCodec.Raw, raw.Length, raw.Length, ContentHash.Of(raw));

    public static byte[] Envelope(AppearanceBlobEnvelopeHeader header, byte[] payload) => [.. header.ToBytes(), .. payload];

    public static byte[] RawEnvelope(byte[] raw) => Envelope(RawHeader(raw), raw);

    public static byte[] Compress(byte[] raw)
    {
        var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, new BrotliCompressionOptions { Quality = AppearanceBlobEnvelope.BrotliQuality }, leaveOpen: true))
            brotli.Write(raw);
        return compressed.ToArray();
    }

    /// <summary>A Brotli envelope of compressible content (the 5% rule is the client's, not checked here).</summary>
    public static byte[] BrotliEnvelope(byte[] raw)
    {
        var payload = Compress(raw);
        return Envelope(new(AppearanceBlobCodec.Brotli, raw.Length, payload.Length, ContentHash.Of(raw)), payload);
    }
}
