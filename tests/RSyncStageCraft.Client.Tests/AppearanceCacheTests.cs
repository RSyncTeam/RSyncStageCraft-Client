using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>The client's content-addressed appearance cache.</summary>
public sealed class AppearanceCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-cache", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheCacheIsAVersionedDisposableFolderAndStartupRemovesTemporaryFiles()
    {
        var cache = new AppearanceBlobCache(root);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "v1"), cache.Root);
        var raw = Encoding.UTF8.GetBytes("cached content");
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw))).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(cache.PathOf(ContentHash.Of(raw))));
        Assert.StartsWith(Path.Combine(cache.Root, "blobs", ContentHash.Of(raw).Value[..2]), cache.PathOf(ContentHash.Of(raw)), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));

        // Left over by an interrupted session: removed when the cache opens.
        await File.WriteAllBytesAsync(Path.Combine(cache.TemporaryDirectory, "left.raw"), [1, 2, 3]);
        var reopened = new AppearanceBlobCache(root);
        Assert.Empty(Directory.EnumerateFiles(reopened.TemporaryDirectory));
        Assert.Equal(AppearanceCacheState.Present, (await reopened.TryGetAsync(Ref(raw))).State);

        // Entirely disposable: deleting it only loses copies.
        Directory.Delete(root, recursive: true);
        var fresh = new AppearanceBlobCache(root);
        Assert.Equal(AppearanceCacheState.Missing, (await fresh.TryGetAsync(Ref(raw))).State);
    }

    [Fact]
    public async Task OnlyAVerifiedEnvelopeIsPublishedAndTemporaryFilesAreRemoved()
    {
        var cache = new AppearanceBlobCache(root);
        var raw = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("chara/x/body.mdl ", 2000)));
        var other = Encoding.ASCII.GetBytes(new string('z', raw.Length));
        var damaged = BrotliEnvelope(raw);
        damaged[^3] ^= 0x55;

        foreach (var (envelope, expected) in new (byte[], AppearanceBlobRef)[]
        {
            (RawEnvelope(other), Ref(raw)),                      // another blob
            (Envelope(RawHeader(raw), other), Ref(raw)),          // header names it, content differs
            (damaged, Ref(raw)),                                  // corrupt Brotli
            (BrotliEnvelope(raw)[..^4], Ref(raw)),                // truncated
            (RawEnvelope(raw).Append((byte)0).ToArray(), Ref(raw)), // trailing data
            (RawEnvelope(raw)[..40], Ref(raw)),                   // no complete header
        })
        {
            var (status, _, _) = await cache.StoreEnvelopeAsync(new MemoryStream(envelope), expected);
            Assert.Equal(AppearanceCacheStoreStatus.Invalid, status);
            Assert.False(File.Exists(cache.PathOf(expected.Hash)));
            Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        }

        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(BrotliEnvelope(raw)), Ref(raw))).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(cache.PathOf(ContentHash.Of(raw))));
    }

    [Fact]
    public async Task AValidExistingEntryIsNeverOverwrittenAndACorruptOneIsReplaced()
    {
        var raw = RandomNumberGenerator.GetBytes(200_000);
        var cache = new AppearanceBlobCache(root);
        var path = cache.PathOf(ContentHash.Of(raw));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A valid entry from an earlier session (not yet verified in this one).
        await File.WriteAllBytesAsync(path, raw);
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var reopened = new AppearanceBlobCache(root);
        Assert.Equal(AppearanceCacheStoreStatus.AlreadyPresent, (await reopened.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw))).Status);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.EnumerateFiles(reopened.TemporaryDirectory));

        // Corrupt on disk (same length): first use proves it and removes it ...
        var corrupt = raw.ToArray();
        corrupt[1234] ^= 0xFF;
        await File.WriteAllBytesAsync(path, corrupt);
        var third = new AppearanceBlobCache(root);
        Assert.Equal(AppearanceCacheState.Missing, (await third.TryGetAsync(Ref(raw))).State);
        Assert.False(File.Exists(path));

        // ... and a corrupt entry met by a verified download is replaced atomically.
        await File.WriteAllBytesAsync(path, corrupt);
        var fourth = new AppearanceBlobCache(root);
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await fourth.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw))).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));
        Assert.Equal(AppearanceCacheState.Present, (await fourth.TryGetAsync(Ref(raw))).State);

        // A wrong-length entry is also corrupt.
        await File.WriteAllBytesAsync(path, raw[..^1]);
        var fifth = new AppearanceBlobCache(root);
        Assert.Equal(AppearanceCacheState.Missing, (await fifth.TryGetAsync(Ref(raw))).State);
    }

    /// <summary>
    /// First-use validation and a download of the same hash, forced into both
    /// orders by the cache's test hooks: the per-hash lock serializes them, a
    /// corrupt entry is replaced once, and the result is always a valid entry.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentFirstUseValidationAndDownloadOfTheSameHashAreSerialized(bool validationFirst)
    {
        var raw = RandomNumberGenerator.GetBytes(150_000);
        var hash = ContentHash.Of(raw);
        var seed = new AppearanceBlobCache(root);
        var path = seed.PathOf(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var corrupt = raw.ToArray();
        corrupt[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, corrupt);

        var cache = new AppearanceBlobCache(root);
        var events = new List<string>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(AppearanceCacheState State, string? Path)>? validation = null;
        Task<(AppearanceCacheStoreStatus Status, AppearanceEnvelopeStatus Envelope, AppearancePayloadStatus Payload)>? download = null;
        cache.TestHook = async (point, blob) =>
        {
            if (blob != hash)
                return;
            lock (events)
                events.Add(point);
            switch (point)
            {
                case "verify:in-lock" when validationFirst:
                    download = Task.Run(() => cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw)));
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    break;
                case "publish:before-lock" when validationFirst:
                    waiting.TrySetResult();
                    break;
                case "publish:in-lock" when !validationFirst:
                    validation = Task.Run(() => cache.TryGetAsync(Ref(raw)));
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    break;
                case "verify:before-lock" when !validationFirst:
                    waiting.TrySetResult();
                    break;
            }
        };

        if (validationFirst)
        {
            var first = await cache.TryGetAsync(Ref(raw));
            Assert.NotNull(download);
            var stored = await download;
            // The validation proved the old entry corrupt and removed it; the
            // download then published its verified content.
            Assert.Equal(AppearanceCacheState.Missing, first.State);
            Assert.Equal(AppearanceCacheStoreStatus.Stored, stored.Status);
            Assert.Equal(new[] { "verify:before-lock", "verify:in-lock", "publish:before-lock", "publish:in-lock" }, events);
        }
        else
        {
            var stored = await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
            Assert.NotNull(validation);
            var later = await validation;
            // The download replaced the corrupt entry under the lock; the
            // validation waiting for it then finds the verified entry.
            Assert.Equal(AppearanceCacheStoreStatus.Stored, stored.Status);
            Assert.Equal(AppearanceCacheState.Present, later.State);
            Assert.Equal(new[] { "publish:before-lock", "publish:in-lock", "verify:before-lock", "verify:in-lock" }, events);
        }
        cache.TestHook = null;
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(AppearanceCacheState.Present, (await new AppearanceBlobCache(root).TryGetAsync(Ref(raw))).State);
    }

    [Fact]
    public async Task ConcurrentDownloadsOfTheSameHashPublishOnce()
    {
        var raw = RandomNumberGenerator.GetBytes(300_000);
        var cache = new AppearanceBlobCache(root);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw)))));
        Assert.Single(results, result => result.Status == AppearanceCacheStoreStatus.Stored);
        Assert.Equal(5, results.Count(result => result.Status == AppearanceCacheStoreStatus.AlreadyPresent));
        Assert.Equal(raw, await File.ReadAllBytesAsync(cache.PathOf(ContentHash.Of(raw))));
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
    }

    [Fact]
    public async Task ACancelledStorePublishesNothingAndRemovesItsTemporaryFile()
    {
        var raw = RandomNumberGenerator.GetBytes(400_000);
        var cache = new AppearanceBlobCache(root);
        using var cancel = new CancellationTokenSource();
        var source = new CancellingStream(RawEnvelope(raw), afterBytes: 100_000, cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.StoreEnvelopeAsync(source, Ref(raw), cancel.Token));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(raw))));
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
    }

    /// <summary>Cancels the token once this many bytes were read.</summary>
    private sealed class CancellingStream(byte[] content, int afterBytes, CancellationTokenSource cancel) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= afterBytes)
                await cancel.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
