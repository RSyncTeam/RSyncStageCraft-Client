using System.Net;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppearanceMemoryCollection
{
    public const string Name = "Appearance memory (runs alone)";
}

/// <summary>
/// The client encodes, uploads, downloads, decodes and verifies a
/// maximum-size blob (64 MiB raw) as a stream with bounded managed memory.
/// The server side is stubbed so the process-wide allocation counter measures
/// only the client; runs alone with the other memory tests.
/// </summary>
[Collection(AppearanceMemoryCollection.Name)]
public sealed class AppearanceClientMemoryTests : IDisposable
{
    /// <summary>Far below the 64 MiB a buffered implementation would need.</summary>
    private const long AllocationBudget = 16L * 1024 * 1024;

    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-client-memory-" + Guid.NewGuid().ToString("N"));
    private readonly string key = "rsc1." + new string('a', 32) + "." + new string('b', 64);

    public AppearanceClientMemoryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMaximumSizeBlobIsUploadedAndDownloadedWithBoundedMemory(bool compressible)
    {
        // Compressible content travels as Brotli; random content as a raw
        // 64 MiB body (the encoder's second pass, the upload body and the
        // download stream all carry the full size).
        var (bigRaw, bigEnvelope) = await WriteBlobAsync("big", AppearanceLimits.MaximumBlobBytes, compressible);
        var (smallRaw, smallEnvelope) = await WriteBlobAsync("small", 200_000, compressible);

        // Exercise every path once on a small blob outside the measurement.
        await RoundTripAsync(smallRaw, smallEnvelope);

        var (upload, download) = await RoundTripAsync(bigRaw, bigEnvelope);
        Assert.True(upload < AllocationBudget, $"{upload:N0} bytes allocated while encoding and uploading 64 MiB");
        Assert.True(download < AllocationBudget, $"{download:N0} bytes allocated while downloading and verifying 64 MiB");
    }

    /// <summary>Uploads then downloads one blob (as a one-blob bundle) and returns the bytes allocated by each.</summary>
    private async Task<(long Upload, long Download)> RoundTripAsync(string rawPath, string envelopePath)
    {
        var blob = await RefOfFileAsync(rawPath);
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId("alice"), blob, null, null, [], out var error)!;
        Assert.Null(error);
        var commits = 0;
        long uploaded = 0;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                return Interlocked.Increment(ref commits) == 1
                    ? Json(HttpStatusCode.Conflict, $$"""{"code":"missingBlobs","missing":["{{blob.Hash.Value}}"]}""")
                    : Json(HttpStatusCode.Created, "{}");
            }
            if (request.Method == HttpMethod.Put)
            {
                var sink = new CountingSink();
                await request.Content!.CopyToAsync(sink, cancellationToken);
                uploaded = sink.Length;
                return Json(HttpStatusCode.Created, "{}");
            }
            if (path.EndsWith("/manifest", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest.CanonicalBytes) };
            var content = new StreamContent(File.OpenRead(envelopePath), 81920);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        Func<ServerConnectionSettings> settings = () => new ServerConnectionSettings("wss://rsc.example.test/rsc/v1/control", key, true);

        long uploadAllocated;
        using (var owner = new AppearanceTransferClient(settings, new AppearanceBlobCache(Path.Combine(root, "owner-" + Guid.NewGuid().ToString("N"))), null, handler))
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var result = await owner.UploadBundleAsync(manifest, new Dictionary<ContentHash, string> { [blob.Hash] = rawPath });
            uploadAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.Equal(AppearanceTransferStatus.Succeeded, result.Status);
            Assert.True(uploaded > AppearanceBlobEnvelopeHeader.Length);
        }

        long downloadAllocated;
        var receiverHandler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest.CanonicalBytes) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(envelopePath), 81920) }));
        using (var receiver = new AppearanceTransferClient(settings, new AppearanceBlobCache(Path.Combine(root, "receiver-" + Guid.NewGuid().ToString("N"))), null, receiverHandler))
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var result = await receiver.DownloadBundleAsync(manifest.BundleId);
            downloadAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.Equal(AppearanceTransferStatus.Succeeded, result.Result.Status);
            Assert.Equal(blob.Size, new FileInfo(receiver.Cache.PathOf(blob.Hash)).Length);
        }
        return (uploadAllocated, downloadAllocated);
    }

    /// <summary>Compressible or random content written to a file (never held in memory), and its envelope.</summary>
    private async Task<(string Raw, string Envelope)> WriteBlobAsync(string name, long size, bool compressible)
    {
        var rawPath = Path.Combine(root, name + ".raw");
        await using (var raw = File.Create(rawPath))
        {
            var block = new byte[64 * 1024];
            for (long written = 0, i = 0; written < size; i++)
            {
                if (compressible)
                {
                    for (var j = 0; j < block.Length; j += 4)
                        BitConverter.TryWriteBytes(block.AsSpan(j), (int)(i * 7919) ^ (j / 64));
                }
                else
                {
                    System.Security.Cryptography.RandomNumberGenerator.Fill(block);
                }
                var count = (int)Math.Min(block.Length, size - written);
                await raw.WriteAsync(block.AsMemory(0, count));
                written += count;
            }
        }
        var envelopePath = Path.Combine(root, name + ".rscb");
        await using (var raw = File.OpenRead(rawPath))
        await using (var envelope = File.Create(envelopePath))
            Assert.Equal(compressible ? AppearanceBlobCodec.Brotli : AppearanceBlobCodec.Raw,
                (await AppearanceBlobEnvelope.EncodeAsync(raw, envelope, Path.Combine(root, name + ".br"))).Codec);
        return (rawPath, envelopePath);
    }

    private static async Task<AppearanceBlobRef> RefOfFileAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(file);
        Assert.True(ContentHash.TryParse(Convert.ToHexString(hash).ToLowerInvariant(), out var identity));
        return new AppearanceBlobRef(identity, file.Length);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    /// <summary>Counts what is written and keeps nothing.</summary>
    private sealed class CountingSink : Stream
    {
        private long length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => length += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            length += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            length += count;
            return Task.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
