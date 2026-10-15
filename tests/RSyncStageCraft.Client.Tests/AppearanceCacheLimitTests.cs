using System.Net;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The client cache's size limit and eviction. Small limits come from the test seam; every test also checks
/// that the files on disk (entries and temporary files) never exceed the limit.
/// </summary>
public sealed class AppearanceCacheLimitTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-cache-limit", Guid.NewGuid().ToString("N"));

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
    public async Task TheLimitIsTenGiBByDefaultAndAdjustableFromTwoToSixtyFourGiB()
    {
        Assert.Equal(10 * GiB, AppearanceBlobCache.DefaultLimitBytes);
        var cache = new AppearanceBlobCache(Folder());
        Assert.Equal(10 * GiB, cache.LimitBytes);
        Assert.Equal(10 * GiB, cache.Usage.LimitBytes);

        Assert.True(AppearanceBlobCache.IsValidLimit(2 * GiB));
        Assert.True(AppearanceBlobCache.IsValidLimit(64 * GiB));
        Assert.False(AppearanceBlobCache.IsValidLimit(2 * GiB - 1));
        Assert.False(AppearanceBlobCache.IsValidLimit(64 * GiB + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppearanceBlobCache(Folder(), 2 * GiB - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppearanceBlobCache(Folder(), 64 * GiB + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetLimitAsync(64 * GiB + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetLimitAsync(1 * GiB));
        Assert.Equal(10 * GiB, cache.LimitBytes);

        await cache.SetLimitAsync(2 * GiB);
        Assert.Equal(2 * GiB, cache.LimitBytes);
        Assert.Equal(64 * GiB, new AppearanceBlobCache(Folder(), 64 * GiB).LimitBytes);
    }

    [Fact]
    public async Task StartupEvictsTheLeastRecentlyUsedEntriesAboveTheLimitByModificationTime()
    {
        var folder = Folder();
        var first = AppearanceBlobCache.CreateForTests(folder, 1_000_000);
        var blobs = Enumerable.Range(0, 4).Select(_ => RandomNumberGenerator.GetBytes(10_000)).ToArray();
        foreach (var blob in blobs)
            Assert.Equal(AppearanceCacheStoreStatus.Stored, (await first.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob))).Status);
        // Last use is the modification time: blob 0 is the oldest, blob 3 the newest.
        for (var index = 0; index < blobs.Length; index++)
            File.SetLastWriteTimeUtc(first.PathOf(ContentHash.Of(blobs[index])), new DateTime(2020, 1, 2 + index, 0, 0, 0, DateTimeKind.Utc));
        // A file that is not a blob still counts, and goes by its age too.
        var strayFolder = Path.Combine(first.BlobsDirectory, "zz");
        Directory.CreateDirectory(strayFolder);
        var stray = Path.Combine(strayFolder, "not-a-blob");
        await File.WriteAllBytesAsync(stray, new byte[1_000]);
        File.SetLastWriteTimeUtc(stray, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(41_000, DiskBytes(first));

        var reopened = AppearanceBlobCache.CreateForTests(folder, 25_000);

        Assert.Equal(3, reopened.StartupTrim.Removed);
        Assert.Equal(21_000, reopened.StartupTrim.RemovedBytes);
        Assert.False(File.Exists(stray));
        Assert.False(File.Exists(reopened.PathOf(ContentHash.Of(blobs[0]))));
        Assert.False(File.Exists(reopened.PathOf(ContentHash.Of(blobs[1]))));
        Assert.Equal(AppearanceCacheState.Present, (await reopened.TryGetAsync(Ref(blobs[2]))).State);
        Assert.Equal(AppearanceCacheState.Present, (await reopened.TryGetAsync(Ref(blobs[3]))).State);
        Assert.Equal(20_000, reopened.Usage.StoredBytes);
        Assert.Equal(2, reopened.Usage.Entries);
        Assert.True(DiskBytes(reopened) <= reopened.LimitBytes);
    }

    [Fact]
    public async Task AnAcquisitionRecordsUseInTheModificationTimeAndDecidesTheEvictionOrder()
    {
        var folder = Folder();
        var setup = AppearanceBlobCache.CreateForTests(folder, 1_000_000);
        var (a, b, c, d) = (RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000),
            RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000));
        foreach (var blob in new[] { a, b, c })
            await setup.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob));
        File.SetLastWriteTimeUtc(setup.PathOf(ContentHash.Of(a)), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(setup.PathOf(ContentHash.Of(b)), new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(setup.PathOf(ContentHash.Of(c)), new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var cache = AppearanceBlobCache.CreateForTests(folder, 30_000);
        Assert.Equal(0, cache.StartupTrim.Removed);

        // Acquiring A records its use once, in its modification time.
        var (state, lease) = await cache.AcquireAsync(Ref(a));
        Assert.Equal(AppearanceCacheState.Present, state);
        Assert.Equal(cache.PathOf(ContentHash.Of(a)), lease!.Path);
        lease.Dispose();
        Assert.True(File.GetLastWriteTimeUtc(cache.PathOf(ContentHash.Of(a))) > new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Making room for D evicts the least recently used entry: B, not A.
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(d)), Ref(d))).Status);
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(b))));
        foreach (var kept in new[] { a, c, d })
            Assert.True(File.Exists(cache.PathOf(ContentHash.Of(kept))));
        Assert.Equal(30_000, cache.Usage.StoredBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);

        // The order survives a restart (it is the modification time).
        var reopened = AppearanceBlobCache.CreateForTests(folder, 20_000);
        Assert.False(File.Exists(reopened.PathOf(ContentHash.Of(c))));
        Assert.True(File.Exists(reopened.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(reopened.PathOf(ContentHash.Of(d))));
    }

    [Fact]
    public async Task AnEntryInUseIsNeverEvictedAndANewFileIsRefusedWhenOnlyItCouldMakeRoom()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 35_000);
        var (a, b, e) = (RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000));
        foreach (var blob in new[] { a, b, e })
            await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob));
        var leaseA = (await cache.AcquireAsync(Ref(a))).Lease!;
        var leaseB = (await cache.AcquireAsync(Ref(b))).Lease!;
        Assert.Equal(20_000, cache.Usage.InUseBytes);
        Assert.Equal(2, cache.Usage.InUseEntries);

        // 20 000 in use + 20 000 new > 35 000 even with E gone: refused, and
        // nothing is evicted for a request that cannot fit.
        var large = RandomNumberGenerator.GetBytes(20_000);
        Assert.Equal(AppearanceCacheStoreStatus.Full, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(large)), Ref(large))).Status);
        Assert.Null(await cache.ReserveAsync(20_000));
        foreach (var kept in new[] { a, b, e })
            Assert.True(File.Exists(cache.PathOf(ContentHash.Of(kept))));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(large))));
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(0, cache.Usage.TemporaryBytes);

        // A smaller file fits by evicting E, the only entry not in use.
        var small = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(small)), Ref(small))).Status);
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(e))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(b))));
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);

        // Once released, entries in use can be evicted again.
        leaseB.Dispose();
        leaseB.Dispose(); // idempotent
        var another = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(another)), Ref(another))).Status);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.Equal(10_000, cache.Usage.InUseBytes);
        leaseA.Dispose();
        Assert.Equal(0, cache.Usage.InUseBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
    }

    [Fact]
    public async Task AnEntryWhoseHashLockIsHeldIsNeverEvicted()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 25_000);
        var (a, b) = (RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000));
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(b)), Ref(b));
        File.SetLastWriteTimeUtc(cache.PathOf(ContentHash.Of(a)), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var leaseB = (await cache.AcquireAsync(Ref(b))).Lease!;

        // A's lock is held (a verification paused inside it).
        var inLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.TestHook = async (point, hash) =>
        {
            if (point == "verify:in-lock" && hash == ContentHash.Of(a))
            {
                inLock.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        var verifying = Task.Run(() => cache.TryGetAsync(Ref(a)));
        await inLock.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A is the only entry not in use, but its lock is held: refused, A kept.
        var c = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Full, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(c)), Ref(c))).Status);
        Assert.Equal(0, (await cache.ClearUnusedAsync()).Removed);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));

        release.TrySetResult();
        Assert.Equal(AppearanceCacheState.Present, (await verifying).State);
        cache.TestHook = null;

        // Free again: A (not in use) makes room.
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(c)), Ref(c))).Status);
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(b))));
        leaseB.Dispose();
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
    }

    [Fact]
    public async Task ATemporaryFileBeingWrittenIsCountedAndNeverRemoved()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 25_000);
        var a = RandomNumberGenerator.GetBytes(10_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));

        // X is half written into a temporary file and held there.
        var x = RandomNumberGenerator.GetBytes(10_000);
        var halfway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storing = Task.Run(() => cache.StoreEnvelopeAsync(new StallingStream(RawEnvelope(x), 5_000, halfway, resume.Task), Ref(x)));
        await halfway.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var temporary = Assert.Single(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(10_000, cache.Usage.TemporaryBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);

        // Its full bound counts: Y (10 000) fits only by evicting A.
        var y = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(y)), Ref(y))).Status);
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
        // With Y in use, a further file does not fit: the temporary file is never evicted.
        var leaseY = (await cache.AcquireAsync(Ref(y))).Lease!;
        var z = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Full, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(z)), Ref(z))).Status);

        // "Clear unused" keeps the entry in use and the temporary file.
        Assert.Equal(0, (await cache.ClearUnusedAsync()).Removed);
        Assert.True(File.Exists(temporary));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(y))));
        leaseY.Dispose();

        // Completed, the reservation becomes the entry (the total does not grow).
        resume.TrySetResult();
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await storing).Status);
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(0, cache.Usage.TemporaryBytes);
        Assert.Equal(20_000, cache.Usage.StoredBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);

        // Now unused, both are cleared.
        var cleared = await cache.ClearUnusedAsync();
        Assert.Equal(2, cleared.Removed);
        Assert.Equal(20_000, cleared.RemovedBytes);
        Assert.Equal(0, cache.Usage.StoredBytes);
        Assert.Equal(0, DiskBytes(cache));
    }

    [Fact]
    public async Task ConcurrentStoresNeverExceedTheLimitOnDiskAndKeepTheAccountingExact()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 35_000);
        var maximum = 0L;
        cache.TestHook = (point, _) =>
        {
            // Each temporary file is complete here, before publication. The
            // accounting (entries plus reserved temporary space) is read
            // atomically; the files on disk are compared at quiet points below.
            InterlockedMax(ref maximum, cache.Usage.TotalBytes);
            return Task.CompletedTask;
        };
        var blobs = Enumerable.Range(0, 12).Select(_ => RandomNumberGenerator.GetBytes(10_000)).ToArray();

        var results = await Task.WhenAll(blobs.Select(blob =>
            Task.Run(() => cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob)))));
        cache.TestHook = null;

        // Each is stored, or refused while the others' temporary files take the room.
        Assert.All(results, result => Assert.Contains(result.Status, new[] { AppearanceCacheStoreStatus.Stored, AppearanceCacheStoreStatus.Full }));
        Assert.Contains(results, result => result.Status == AppearanceCacheStoreStatus.Stored);
        Assert.True(Interlocked.Read(ref maximum) <= cache.LimitBytes);
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(0, cache.Usage.TemporaryBytes);
        Assert.Equal(DiskBytes(cache), cache.Usage.StoredBytes);
        Assert.True(cache.Usage.StoredBytes <= cache.LimitBytes);

        // Afterwards there is room again for each refused one, one at a time.
        foreach (var blob in blobs.Where((_, index) => results[index].Status == AppearanceCacheStoreStatus.Full))
            Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob))).Status);
        Assert.Equal(DiskBytes(cache), cache.Usage.StoredBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
    }

    [Fact]
    public async Task AnEntryAlreadyPresentIsFoundEvenWhenThereIsNoRoomForATemporaryFile()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 15_000);
        var a = RandomNumberGenerator.GetBytes(10_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));
        using var lease = (await cache.AcquireAsync(Ref(a))).Lease!;

        // No room for another 10 000-byte temporary file, but A is already there.
        Assert.Equal(AppearanceCacheStoreStatus.AlreadyPresent, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a))).Status);
        var (status, second) = await cache.StoreAndAcquireEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));
        Assert.Equal(AppearanceCacheStoreStatus.AlreadyPresent, status);
        Assert.NotNull(second);
        second!.Dispose();
        var b = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Full, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(b)), Ref(b))).Status);
        Assert.Equal(10_000, cache.Usage.InUseBytes);
        Assert.Equal(0, cache.Usage.TemporaryBytes);
    }

    [Fact]
    public async Task EveryFileUnderTheCacheRootCountsAndUnknownFilesAreClearedAsUnused()
    {
        var folder = Folder();
        var first = AppearanceBlobCache.CreateForTests(folder, 1_000_000);
        var a = RandomNumberGenerator.GetBytes(4_000);
        await first.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));
        await File.WriteAllBytesAsync(Path.Combine(first.Root, "notes.txt"), new byte[500]);
        var deeper = Path.Combine(first.BlobsDirectory, "ab", "deeper");
        Directory.CreateDirectory(deeper);
        await File.WriteAllBytesAsync(Path.Combine(deeper, "file"), new byte[700]);

        var cache = AppearanceBlobCache.CreateForTests(folder, 1_000_000);
        Assert.Equal(5_200, cache.Usage.StoredBytes);
        Assert.Equal(3, cache.Usage.Entries);
        Assert.Equal(DiskBytes(cache), cache.Usage.StoredBytes);

        using (var lease = (await cache.AcquireAsync(Ref(a))).Lease!)
        {
            var cleared = await cache.ClearUnusedAsync();
            Assert.Equal(2, cleared.Removed);
            Assert.Equal(1_200, cleared.RemovedBytes);
        }
        Assert.Equal(4_000, DiskBytes(cache));
        Assert.Equal(4_000, cache.Usage.StoredBytes);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while ((current = Interlocked.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public async Task ClearUnusedRemovesOnlyEntriesThatAreNotInUseAndNotLocked()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var (a, b, c) = (RandomNumberGenerator.GetBytes(3_000), RandomNumberGenerator.GetBytes(4_000), RandomNumberGenerator.GetBytes(5_000));
        foreach (var blob in new[] { a, b, c })
            await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob));
        using var leaseA = (await cache.AcquireAsync(Ref(a))).Lease!;
        var inLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.TestHook = async (point, hash) =>
        {
            if (point == "verify:in-lock" && hash == ContentHash.Of(b))
            {
                inLock.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        var verifying = Task.Run(() => cache.TryGetAsync(Ref(b)));
        await inLock.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var cleared = await cache.ClearUnusedAsync();

        Assert.Equal(1, cleared.Removed);
        Assert.Equal(5_000, cleared.RemovedBytes);
        Assert.Equal(2, cleared.Kept);
        Assert.Equal(7_000, cleared.KeptBytes);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(b))));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(c))));
        release.TrySetResult();
        Assert.Equal(AppearanceCacheState.Present, (await verifying).State);
        cache.TestHook = null;
    }

    [Fact]
    public async Task LoweringTheLimitBelowWhatIsInUseRefusesEveryNewReservationAndTrimsAsFilesAreReleased()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 100_000);
        var (a, b, c) = (RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000));
        foreach (var blob in new[] { a, b, c })
            await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob));
        var leaseA = (await cache.AcquireAsync(Ref(a))).Lease!;
        // X is being written: a reservation of 10 000 in flight.
        var x = RandomNumberGenerator.GetBytes(10_000);
        var halfway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storing = Task.Run(() => cache.StoreEnvelopeAsync(new StallingStream(RawEnvelope(x), 5_000, halfway, resume.Task), Ref(x)));
        await halfway.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var temporary = Assert.Single(Directory.EnumerateFiles(cache.TemporaryDirectory));

        // Lowered below what is in use and reserved: unused entries go at once;
        // the entry in use and the reservation stay (nothing is cancelled).
        var trim = await cache.SetLimitForTestsAsync(15_000);
        Assert.Equal(2, trim.Removed);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(temporary));
        Assert.Equal(10_000, cache.Usage.StoredBytes);
        Assert.Equal(10_000, cache.Usage.TemporaryBytes);
        Assert.True(cache.Usage.OverLimit);

        // Over the limit: every new reservation is refused at once, however small.
        Assert.Equal(AppearanceCacheReserveStatus.OverLimit, (await cache.TryReserveAsync(1)).Status);
        var d = RandomNumberGenerator.GetBytes(1_000);
        Assert.Equal(AppearanceCacheStoreStatus.OverLimit, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(d)), Ref(d))).Status);
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(d))));
        Assert.Equal(0, (await cache.ClearUnusedAsync()).Removed);
        Assert.True(File.Exists(temporary));

        // X completes; as an unused entry over the limit it is trimmed.
        resume.TrySetResult();
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await storing).Status);
        await cache.TrimTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(x))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.False(cache.Usage.OverLimit);
        Assert.Equal(10_000, cache.Usage.TotalBytes);

        // Within the limit again: reservations are granted.
        using (var granted = await cache.ReserveAsync(5_000))
            Assert.NotNull(granted);

        // Lowered below the entry in use: it stays until released, then is trimmed.
        await cache.SetLimitForTestsAsync(5_000);
        Assert.True(cache.Usage.OverLimit);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        leaseA.Dispose();
        await cache.TrimTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.False(cache.Usage.OverLimit);
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(d)), Ref(d))).Status);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
    }

    [Fact]
    public async Task ExternallyAddedFilesAreCountedNeverTrustedAndPutTheCacheOverItsLimitWithoutDeletingActiveData()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 25_000);
        var a = RandomNumberGenerator.GetBytes(10_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(a)), Ref(a));
        var leaseA = (await cache.AcquireAsync(Ref(a))).Lease!;

        // A hash-named file another program wrote is not trusted: its full
        // verification fails and it is removed.
        var b = RandomNumberGenerator.GetBytes(20_000);
        var pathB = cache.PathOf(ContentHash.Of(b));
        Directory.CreateDirectory(Path.GetDirectoryName(pathB)!);
        await File.WriteAllBytesAsync(pathB, RandomNumberGenerator.GetBytes(20_000));
        Assert.Equal(AppearanceCacheState.Missing, (await cache.TryGetAsync(Ref(b))).State);
        Assert.False(File.Exists(pathB));

        // Its correct content is used only after full verification; in use, it
        // puts the cache over its limit: reported, new reservations refused,
        // and no active data deleted.
        await File.WriteAllBytesAsync(pathB, b);
        var leaseB = (await cache.AcquireAsync(Ref(b))).Lease!;
        await cache.TrimTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(cache.Usage.OverLimit);
        Assert.Equal(30_000, cache.Usage.StoredBytes);
        Assert.Equal(AppearanceCacheReserveStatus.OverLimit, (await cache.TryReserveAsync(1)).Status);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.True(File.Exists(pathB));

        // Released, the eligible entry is trimmed until the cache is within its limit.
        leaseB.Dispose();
        await cache.TrimTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(pathB));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.False(cache.Usage.OverLimit);

        // Any other file another program adds is counted on the next refresh.
        await File.WriteAllBytesAsync(Path.Combine(cache.Root, "foreign.dat"), new byte[3_000]);
        Assert.Equal(13_000, (await cache.RefreshAsync()).StoredBytes);
        leaseA.Dispose();
        Assert.Equal(DiskBytes(cache), cache.Usage.StoredBytes);
        Assert.True(DiskBytes(cache) <= cache.LimitBytes);
    }

    /// <remarks>
    /// Creates file and folder symbolic links. On a Windows host whose process lacks
    /// only the link privilege it is reported as skipped, never as passed.
    /// </remarks>
    [SymbolicLinkFact]
    public async Task LinksAndReparsePointsAreNeverFollowedOutsideTheCache()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var outside = Folder();
        // Fixed contents whose hashes start with different folders ("8a", "1e").
        var c = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("link test content C ", 400)));
        var e = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("linked folder content E ", 300)));
        Assert.NotEqual(ContentHash.Of(c).Value[..2], ContentHash.Of(e).Value[..2]);
        var target = Path.Combine(outside, "valid-content");
        await File.WriteAllBytesAsync(target, c);
        var outsideFolder = Path.Combine(outside, "folder");
        Directory.CreateDirectory(outsideFolder);
        var outsideFile = Path.Combine(outsideFolder, "kept");
        await File.WriteAllBytesAsync(outsideFile, new byte[5_000]);
        // The outside folder even holds E's valid content under E's name.
        await File.WriteAllBytesAsync(Path.Combine(outsideFolder, ContentHash.Of(e).Value), e);

        // A link at C's canonical path, to a file with the right content.
        var pathC = cache.PathOf(ContentHash.Of(c));
        Directory.CreateDirectory(Path.GetDirectoryName(pathC)!);
        File.CreateSymbolicLink(pathC, target);
        // E's folder under blobs/ is a link to the folder outside the cache.
        Directory.CreateSymbolicLink(Path.GetDirectoryName(cache.PathOf(ContentHash.Of(e)))!, outsideFolder);

        // Neither link's target is counted.
        Assert.Equal(0, (await cache.RefreshAsync()).StoredBytes);
        // The file link is never trusted: only the link is removed.
        Assert.Equal(AppearanceCacheState.Missing, (await cache.TryGetAsync(Ref(c))).State);
        Assert.False(File.Exists(pathC));
        Assert.Null(new FileInfo(pathC).LinkTarget);
        Assert.Equal(c, await File.ReadAllBytesAsync(target));

        // Nothing is read, published or deleted through the folder link.
        Assert.Equal(AppearanceCacheState.Unavailable, (await cache.TryGetAsync(Ref(e))).State);
        Assert.Null((await cache.AcquireAsync(Ref(e))).Lease);
        Assert.Equal(AppearanceCacheStoreStatus.Unavailable, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(e)), Ref(e))).Status);
        await cache.ClearUnusedAsync();
        Assert.Equal(new[] { "kept", ContentHash.Of(e).Value }.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(outsideFolder).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        Assert.Equal(5_000, new FileInfo(outsideFile).Length);
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(0, cache.Usage.TemporaryBytes);
    }

    [SymbolicLinkFact]
    public void ACacheWhoseOwnFoldersAreLinksIsRefused()
    {
        var folder = Folder();
        var outside = Folder();
        Directory.CreateDirectory(Path.Combine(folder, AppearanceBlobCache.FormatFolder));
        Directory.CreateSymbolicLink(Path.Combine(folder, AppearanceBlobCache.FormatFolder, AppearanceBlobCache.TemporaryFolder), outside);
        Assert.Throws<IOException>(() => AppearanceBlobCache.CreateForTests(folder, 1_000_000));
        Assert.Throws<IOException>(() => new AppearanceBlobCache(folder));
    }

    [Fact]
    public async Task ABundleDownloadHoldsItsOwnBlobsAndFailsWithCacheFullInsteadOfEvictingThem()
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, contents) = Bundle();
        using (var alice = Client(service, "alice", AppearanceBlobCache.CreateForTests(Folder(), 1_000_000)))
        {
            Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);
            Assert.True((await alice.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);
        }

        // 80 000 bytes of blobs; an unrelated, older entry makes room for them.
        var bobCache = AppearanceBlobCache.CreateForTests(Folder(), 100_000);
        var unrelated = RandomNumberGenerator.GetBytes(30_000);
        await bobCache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(unrelated)), Ref(unrelated));
        using (var bob = Client(service, "bob", bobCache))
        {
            Assert.Equal(AppearanceTransferStatus.Succeeded, (await bob.DownloadBundleAsync(manifest.BundleId)).Result.Status);
            Assert.False(File.Exists(bobCache.PathOf(ContentHash.Of(unrelated))));
            foreach (var content in contents)
                Assert.True(File.Exists(bobCache.PathOf(ContentHash.Of(content))));
            Assert.Equal(0, bobCache.Usage.InUseBytes); // released when the download ends
            Assert.True(DiskBytes(bobCache) <= bobCache.LimitBytes);
        }

        // A limit below the bundle: only its own blobs could make room, and
        // they are held for the download, so it fails instead.
        var smallCache = AppearanceBlobCache.CreateForTests(Folder(), 70_000);
        using (var again = Client(service, "bob", smallCache))
        {
            var result = (await again.DownloadBundleAsync(manifest.BundleId)).Result;
            Assert.Equal(AppearanceTransferStatus.CacheFull, result.Status);
            Assert.False(result.IsRetryable);
            Assert.Equal(2, contents.Count(content => File.Exists(smallCache.PathOf(ContentHash.Of(content)))));
            Assert.Empty(Directory.EnumerateFiles(smallCache.TemporaryDirectory));
            Assert.Equal(0, smallCache.Usage.InUseBytes);
            Assert.Equal(0, smallCache.Usage.TemporaryBytes);
            Assert.True(DiskBytes(smallCache) <= smallCache.LimitBytes);
        }
    }

    [Fact]
    public async Task AnUploadWritesItsTemporaryFilesOnlyWithinTheLimit()
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, _) = Bundle();
        Assert.True(AppearanceBlobCache.UploadTemporaryBytes(30_000) > 100_000);

        var tight = AppearanceBlobCache.CreateForTests(Folder(), 100_000);
        using (var alice = Client(service, "alice", tight))
        {
            Assert.Equal(AppearanceTransferStatus.CacheFull, (await alice.UploadBundleAsync(manifest, sources)).Status);
            Assert.Empty(Directory.EnumerateFiles(tight.TemporaryDirectory));
            Assert.Equal(0, tight.Usage.TemporaryBytes);
            Assert.Empty((await alice.ListBundlesAsync()).Bundles);
        }

        var enough = AppearanceBlobCache.CreateForTests(Folder(), AppearanceBlobCache.UploadTemporaryBytes(30_000));
        using (var alice = Client(service, "alice", enough))
        {
            Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);
            Assert.Equal(0, enough.Usage.TemporaryBytes);
            Assert.Single((await alice.ListBundlesAsync()).Bundles);
        }
    }

    [Fact]
    public async Task ACapturesWorkingFileIsReservedWithinTheLimitUntilThePlanIsDisposed()
    {
        var work = Folder();
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.SavedDesign, "design-base64-state", [], "Evening look");
        var state = Encoding.UTF8.GetByteCount(read.GlamourerState);

        // No room: refused before anything is written.
        var full = AppearanceBlobCache.CreateForTests(Folder(), state - 1);
        using (var refused = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), read, work, default,
            async (bytes, token) => await full.ReserveAsync(bytes, token)))
        {
            Assert.Equal(AppearanceCaptureRefusal.CacheFull, refused.Refusal);
            Assert.False(refused.CanUpload);
            Assert.Empty(Directory.EnumerateFiles(work));
        }

        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), read, work, default,
            async (bytes, token) => await cache.ReserveAsync(bytes, token));
        Assert.True(plan.CanUpload);
        Assert.Equal(state, cache.Usage.TemporaryBytes);
        Assert.Single(Directory.EnumerateFiles(work));
        plan.Dispose();
        Assert.Equal(0, cache.Usage.TemporaryBytes);
        Assert.Empty(Directory.EnumerateFiles(work));
    }

    /// <summary>
    /// A source that supplies its declared bytes and then one more (it grew
    /// while it was encoded): the extra byte is detected before it reaches the
    /// Brotli scratch file or the envelope, which hold at most the declared bytes.
    /// </summary>
    [Fact]
    public async Task ASourceThatSuppliesOneByteMoreThanDeclaredIsStoppedBeforeEitherTemporaryOutput()
    {
        // Larger than the encoder's read buffer, so several reads come first.
        var declared = RandomNumberGenerator.GetBytes(200_000);
        var source = new OverlongStream([.. declared, 0x5A], declared.Length);
        var bounded = new AppearanceBoundedSourceStream(source, declared.Length, null);
        var scratch = Path.Combine(Folder(), "scratch.br");
        using var envelope = new MemoryStream();

        await Assert.ThrowsAsync<AppearanceSourceException>(() => AppearanceBlobEnvelope.EncodeAsync(bounded, envelope, scratch));

        // The extra byte was supplied (and so detected) ...
        Assert.Equal(declared.Length + 1, source.Supplied);
        // ... but nothing of the envelope was written, and the scratch file
        // holds exactly the declared bytes.
        Assert.Equal(0, envelope.Length);
        using (var compressed = new MemoryStream(await File.ReadAllBytesAsync(scratch)))
        using (var decoder = new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionMode.Decompress))
        using (var decoded = new MemoryStream())
        {
            await decoder.CopyToAsync(decoded);
            Assert.Equal(declared, decoded.ToArray());
        }
        Assert.True(new FileInfo(scratch).Length + envelope.Length <= AppearanceBlobCache.UploadTemporaryBytes(declared.Length));
    }

    [Fact]
    public async Task AnUploadWhoseSourceGrowsFailsWithinItsReservationAndReleasesEverything()
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, contents) = Bundle();
        var model = contents[1];
        var reservation = AppearanceBlobCache.UploadTemporaryBytes(model.Length);
        // Exactly one upload reservation of the largest blob fits.
        var cache = AppearanceBlobCache.CreateForTests(Folder(), reservation);
        using var alice = Client(service, "alice", cache);
        long largestTemporary = 0, largestReserved = 0;
        OverlongStream? grown = null;
        alice.TestSourceOpener = path =>
        {
            if (path != sources[ContentHash.Of(model)])
                return null;
            return grown = new OverlongStream([.. model, 0x5A], model.Length, () =>
            {
                // At every read: the temporary files and the reservation.
                largestTemporary = Math.Max(largestTemporary, TemporaryBytes(cache));
                largestReserved = Math.Max(largestReserved, cache.Usage.TemporaryBytes);
            });
        };

        var result = await alice.UploadBundleAsync(manifest, sources);

        Assert.Equal(AppearanceTransferStatus.LocalContentInvalid, result.Status);
        Assert.Equal(ContentHash.Of(model), result.Blob);
        Assert.NotNull(grown);
        Assert.Equal(model.Length + 1, grown!.Supplied);
        Assert.Equal(reservation, largestReserved);
        Assert.True(largestTemporary <= reservation);
        // Nothing of it was sent or committed, and every temporary file and
        // reserved byte is released.
        Assert.False(service.Holds(ContentHash.Of(model)));
        Assert.DoesNotContain($"PUT blobs/{ContentHash.Of(model).Value}", service.RequestsOf("alice"));
        Assert.Empty((await alice.ListBundlesAsync()).Bundles);
        Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
        Assert.Equal(0, cache.Usage.TemporaryBytes);
        Assert.Equal(0, cache.Usage.TotalBytes);
    }

    // ---- Helpers -------------------------------------------------------------

    private string Folder()
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Every file under the cache root: entries and temporary files.</summary>
    private static long DiskBytes(AppearanceBlobCache cache)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(cache.Root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // Removed (evicted or published) while counting.
            }
        }
        return total;
    }

    private static long TemporaryBytes(AppearanceBlobCache cache)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(cache.TemporaryDirectory))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
            }
        }
        return total;
    }

    private static AppearanceTransferClient Client(FakeAppearanceService service, string user, AppearanceBlobCache cache) =>
        new(() => service.Settings(user), cache, null, service);

    /// <summary>A bundle of 80 000 bytes of incompressible blobs (state 20 000, two files of 30 000).</summary>
    private (AppearanceBundleManifest Manifest, Dictionary<ContentHash, string> Sources, byte[][] Contents) Bundle()
    {
        var glamourer = RandomNumberGenerator.GetBytes(20_000);
        var model = RandomNumberGenerator.GetBytes(30_000);
        var texture = RandomNumberGenerator.GetBytes(30_000);
        var folder = Folder();
        var sources = new Dictionary<ContentHash, string>();
        foreach (var (name, content) in new[] { ("glamourer", glamourer), ("model", model), ("texture", texture) })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, content);
            sources[ContentHash.Of(content)] = path;
        }
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId("alice"), Ref(glamourer), null, null,
        [
            new AppearanceFileEntry("chara/limit/body.mdl", Ref(model)),
            new AppearanceFileEntry("chara/limit/body.tex", Ref(texture)),
        ], out var error);
        Assert.Null(error);
        return (manifest!, sources, [glamourer, model, texture]);
    }

    /// <summary>
    /// A seekable source whose <see cref="Length"/> declares fewer bytes than it
    /// supplies: the declared bytes, then the extra ones (a file that grew).
    /// </summary>
    private sealed class OverlongStream(byte[] content, long declared, Action? onRead = null) : MemoryStream(content)
    {
        private long supplied;

        public override long Length => declared;

        /// <summary>Bytes handed out in total (readable after the stream is disposed).</summary>
        public long Supplied => Interlocked.Read(ref supplied);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke();
            var read = await base.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref supplied, read);
            return read;
        }
    }

    /// <summary>Serves its content, pausing once at <paramref name="stallAt"/> bytes until released.</summary>
    private sealed class StallingStream(byte[] content, int stallAt, TaskCompletionSource reached, Task resume) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= stallAt && !resume.IsCompleted)
            {
                reached.TrySetResult();
                await resume.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            var limit = Position < stallAt ? (int)Math.Min(buffer.Length, stallAt - Position) : buffer.Length;
            return await base.ReadAsync(buffer[..limit], cancellationToken);
        }
    }
}

/// <summary>
/// The link tests' fact. It probes once, at discovery, and skips the test with a
/// fixed reason only when a symbolic-link creation call itself throws exactly the
/// Windows missing-privilege IOException and the probe's cleanup then succeeds.
/// Any other probe exception, cleanup failures included, is thrown, so xunit
/// reports the test as failed instead of running or skipping it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
file sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
        : this(SymbolicLinkProbe.FileSystem, OperatingSystem.IsWindows())
    {
    }

    public SymbolicLinkFactAttribute(SymbolicLinkProbe probe, bool windows)
    {
        Skip = probe.SkipReason(windows);
    }
}

/// <summary>The probe's file-system steps; the focused tests replace them to inject failures.</summary>
internal sealed record SymbolicLinkProbe(
    Action<string> CreateFolder,
    Action<string> CreateFile,
    Action<string, string> CreateFileLink,
    Action<string, string> CreateDirectoryLink,
    Action<string> DeleteFolder)
{
    /// <summary>ERROR_PRIVILEGE_NOT_HELD (1314), as the HRESULT of the IOException .NET throws.</summary>
    public const int PrivilegeNotHeld = unchecked((int)0x80070522);

    public const string Reason = "This Windows process lacks the privilege to create symbolic links (ERROR_PRIVILEGE_NOT_HELD); run elevated or with Developer Mode to cover this test.";

    public static SymbolicLinkProbe FileSystem { get; } = new(
        folder => Directory.CreateDirectory(folder),
        file => File.WriteAllBytes(file, []),
        (link, target) => File.CreateSymbolicLink(link, target),
        (link, target) => Directory.CreateSymbolicLink(link, target),
        folder =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        });

    /// <summary>
    /// Creates a target folder and file in a fresh scratch folder, then one file link
    /// and one folder link to them, and always attempts to delete the scratch folder.
    /// Returns <see cref="Reason"/> only for the recognized privilege failure followed
    /// by successful cleanup, null when every step succeeded, and throws otherwise.
    /// </summary>
    public string? SkipReason(bool windows)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rsc-symbolic-link-probe", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "target");
        IOException? missingPrivilege = null;
        Exception? failure = null;
        try
        {
            CreateFolder(target);
            CreateFile(Path.Combine(target, "file"));
            missingPrivilege = Link(() => CreateFileLink(Path.Combine(folder, "file-link"), Path.Combine(target, "file")), windows)
                ?? Link(() => CreateDirectoryLink(Path.Combine(folder, "folder-link"), target), windows);
        }
        catch (Exception exception)
        {
            // Thrown again below, once cleanup has been attempted.
            failure = exception;
        }

        Exception? cleanup = null;
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception exception)
        {
            cleanup = exception;
        }

        // A cleanup failure is always thrown; it is reported together with whatever
        // the probe found first, the recognized privilege failure included.
        var first = failure ?? missingPrivilege;
        if (first is not null && cleanup is not null)
            throw new AggregateException("The symbolic-link probe's cleanup failed.", first, cleanup);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        if (cleanup is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cleanup);
        return missingPrivilege is null ? null : Reason;
    }

    /// <returns>The exception only when the call itself threw exactly the Windows missing-privilege IOException; otherwise null or it throws.</returns>
    private static IOException? Link(Action create, bool windows)
    {
        try
        {
            create();
            return null;
        }
        catch (IOException exception) when (windows && exception.GetType() == typeof(IOException) && exception.HResult == PrivilegeNotHeld)
        {
            return exception;
        }
    }
}

/// <summary>An IOException subtype that carries the privilege HRESULT (not the exact type .NET throws).</summary>
file sealed class DerivedPrivilegeException : DirectoryNotFoundException
{
    public DerivedPrivilegeException(int hresult)
        : base("derived") => HResult = hresult;
}

public sealed class SymbolicLinkFactTests
{
    private readonly List<string> steps = [];

    private static IOException MissingPrivilege() => new("A required privilege is not held by the client.", SymbolicLinkProbe.PrivilegeNotHeld);

    /// <summary>A probe whose steps only record their names, with the given ones replaced.</summary>
    private SymbolicLinkProbe Probe(
        Action<string>? createFolder = null,
        Action<string>? createFile = null,
        Action<string, string>? createFileLink = null,
        Action<string, string>? createDirectoryLink = null,
        Action<string>? deleteFolder = null) => new(
        path => { steps.Add("folder"); createFolder?.Invoke(path); },
        path => { steps.Add("file"); createFile?.Invoke(path); },
        (link, target) => { steps.Add("file-link"); createFileLink?.Invoke(link, target); },
        (link, target) => { steps.Add("folder-link"); createDirectoryLink?.Invoke(link, target); },
        path => { steps.Add("delete"); deleteFolder?.Invoke(path); });

    [Fact]
    public void TheLinkTestsUseOnlyTheLinkFact()
    {
        foreach (var name in new[] { nameof(AppearanceCacheLimitTests.LinksAndReparsePointsAreNeverFollowedOutsideTheCache), nameof(AppearanceCacheLimitTests.ACacheWhoseOwnFoldersAreLinksIsRefused) })
        {
            var facts = typeof(AppearanceCacheLimitTests).GetMethod(name)!.GetCustomAttributes(typeof(FactAttribute), inherit: true);
            Assert.IsType<SymbolicLinkFactAttribute>(Assert.Single(facts));
        }
    }

    [Fact]
    public void ASuccessfulProbeLeavesSkipUnsetSoTheOriginalTestRuns()
    {
        foreach (var windows in new[] { true, false })
        {
            steps.Clear();
            Assert.Null(new SymbolicLinkFactAttribute(Probe(), windows).Skip);
            Assert.Equal(["folder", "file", "file-link", "folder-link", "delete"], steps);
        }

        // The real probe on this host: off Windows it can never skip.
        var real = new SymbolicLinkFactAttribute().Skip;
        if (!OperatingSystem.IsWindows())
            Assert.Null(real);
        else
            Assert.True(real is null or SymbolicLinkProbe.Reason);
    }

    [Fact]
    public void TheRealProbeCreatesAFileLinkAndAFolderLinkToItsTargets()
    {
        var seen = new List<string>();
        var inspecting = SymbolicLinkProbe.FileSystem with
        {
            DeleteFolder = folder =>
            {
                var file = new FileInfo(Path.Combine(folder, "file-link"));
                if (file.Exists)
                {
                    Assert.Equal(Path.Combine(folder, "target", "file"), file.LinkTarget);
                    seen.Add("file-link");
                }
                var directory = new DirectoryInfo(Path.Combine(folder, "folder-link"));
                if (directory.Exists)
                {
                    Assert.Equal(Path.Combine(folder, "target"), directory.LinkTarget);
                    seen.Add("folder-link");
                }
                SymbolicLinkProbe.FileSystem.DeleteFolder(folder);
                Assert.False(Directory.Exists(folder));
            },
        };
        var skip = new SymbolicLinkFactAttribute(inspecting, OperatingSystem.IsWindows()).Skip;
        // Where links can be created (here, or any elevated host) both exist before cleanup.
        if (skip is null)
            Assert.Equal(["file-link", "folder-link"], seen);
        else
            Assert.Equal(SymbolicLinkProbe.Reason, skip);
    }

    [Fact]
    public void AFileLinkPrivilegeFailureSkipsWithTheFixedReason()
    {
        var skip = new SymbolicLinkFactAttribute(Probe(createFileLink: (_, _) => throw MissingPrivilege()), windows: true).Skip;
        Assert.Equal("This Windows process lacks the privilege to create symbolic links (ERROR_PRIVILEGE_NOT_HELD); run elevated or with Developer Mode to cover this test.", skip);
        Assert.Equal(["folder", "file", "file-link", "delete"], steps);
    }

    [Fact]
    public void AFolderLinkPrivilegeFailureSkipsWithTheFixedReason()
    {
        var skip = new SymbolicLinkFactAttribute(Probe(createDirectoryLink: (_, _) => throw MissingPrivilege()), windows: true).Skip;
        Assert.Equal(SymbolicLinkProbe.Reason, skip);
        Assert.Equal(["folder", "file", "file-link", "folder-link", "delete"], steps);
        Assert.Equal(unchecked((int)0x80070000 | 1314), SymbolicLinkProbe.PrivilegeNotHeld);
    }

    [Fact]
    public void ThePrivilegeFailureOffWindowsFails()
    {
        var privilege = MissingPrivilege();
        Assert.Same(privilege, Assert.Throws<IOException>(() => new SymbolicLinkFactAttribute(Probe(createFileLink: (_, _) => throw privilege), windows: false)));
        Assert.Same(privilege, Assert.Throws<IOException>(() => new SymbolicLinkFactAttribute(Probe(createDirectoryLink: (_, _) => throw privilege), windows: false)));
    }

    [Fact]
    public void OtherHResultsAndExceptionTypesFromEitherLinkFail()
    {
        foreach (var failure in new Exception[]
                 {
                     new IOException("Access is denied.", unchecked((int)0x80070005)),
                     new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7)),
                     new IOException("The system cannot find the path specified.", unchecked((int)0x80070003)),
                     new IOException("no HRESULT"),
                     new DerivedPrivilegeException(SymbolicLinkProbe.PrivilegeNotHeld),
                     new UnauthorizedAccessException("Access is denied."),
                     new PlatformNotSupportedException(),
                     new InvalidOperationException(),
                 })
        {
            foreach (var windows in new[] { true, false })
            {
                steps.Clear();
                Assert.Same(failure, Assert.ThrowsAny<Exception>(() => new SymbolicLinkFactAttribute(Probe(createFileLink: (_, _) => throw failure), windows)));
                Assert.Equal("delete", steps[^1]);
                steps.Clear();
                Assert.Same(failure, Assert.ThrowsAny<Exception>(() => new SymbolicLinkFactAttribute(Probe(createDirectoryLink: (_, _) => throw failure), windows)));
                Assert.Equal("delete", steps[^1]);
            }
        }
    }

    [Fact]
    public void TargetFolderAndFileSetupFailuresFailEvenWithThePrivilegeHResult()
    {
        foreach (var failure in new Exception[] { MissingPrivilege(), new IOException("Access is denied.", unchecked((int)0x80070005)), new UnauthorizedAccessException() })
        {
            steps.Clear();
            Assert.Same(failure, Assert.ThrowsAny<Exception>(() => new SymbolicLinkFactAttribute(Probe(createFolder: _ => throw failure), windows: true)));
            Assert.Equal(["folder", "delete"], steps);
            steps.Clear();
            Assert.Same(failure, Assert.ThrowsAny<Exception>(() => new SymbolicLinkFactAttribute(Probe(createFile: _ => throw failure), windows: true)));
            Assert.Equal(["folder", "file", "delete"], steps);
        }
    }

    [Fact]
    public void ACleanupFailureFailsEvenAfterTheRecognizedPrivilegeFailure()
    {
        var cleanup = new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));

        // After every step succeeded.
        Assert.Same(cleanup, Assert.Throws<IOException>(() => new SymbolicLinkFactAttribute(Probe(deleteFolder: _ => throw cleanup), windows: true)));

        // After the recognized privilege failure from either link: no skip; both are reported.
        var fromFileLink = MissingPrivilege();
        Assert.Equal([fromFileLink, cleanup], Assert.Throws<AggregateException>(() => new SymbolicLinkFactAttribute(Probe(createFileLink: (_, _) => throw fromFileLink, deleteFolder: _ => throw cleanup), windows: true)).InnerExceptions);
        var fromFolderLink = MissingPrivilege();
        Assert.Equal([fromFolderLink, cleanup], Assert.Throws<AggregateException>(() => new SymbolicLinkFactAttribute(Probe(createDirectoryLink: (_, _) => throw fromFolderLink, deleteFolder: _ => throw cleanup), windows: true)).InnerExceptions);

        // A cleanup failure that itself carries the privilege HRESULT is still a failure.
        var privilegeCleanup = MissingPrivilege();
        Assert.Same(privilegeCleanup, Assert.Throws<IOException>(() => new SymbolicLinkFactAttribute(Probe(deleteFolder: _ => throw privilegeCleanup), windows: true)));

        // After an unrelated failure: both are reported.
        var unrelated = new UnauthorizedAccessException();
        var both = Assert.Throws<AggregateException>(() => new SymbolicLinkFactAttribute(Probe(createFileLink: (_, _) => throw unrelated, deleteFolder: _ => throw cleanup), windows: true));
        Assert.Equal([unrelated, cleanup], both.InnerExceptions);
    }
}
