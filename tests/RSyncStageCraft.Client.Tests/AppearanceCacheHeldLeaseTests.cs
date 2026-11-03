using System.Security.Cryptography;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The cache's held, write-locked leases (<see cref="AppearanceBlobCache.AcquireHeldAsync"/>)
/// and the withdrawal of session trust after a mismatch. Every test checks that nothing stays
/// pinned and no handle stays open once a lease is gone or an acquisition failed.
/// </summary>
public sealed class AppearanceCacheHeldLeaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-cache-held", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lease left open by a failed assertion must not hide that failure.
        }
    }

    /// <summary>
    /// An entry verified in this session, then rewritten at the same length with its
    /// modification time put back. A plain acquisition still trusts it (the remainder
    /// of #42, out of scope here); a held lease re-hashes it, removes it and withdraws
    /// the trust, so a plain acquisition no longer finds it either.
    /// </summary>
    [Fact]
    public async Task ATimestampRestoredCorruptionIsCaughtByAHeldLease()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(120_000);
        var path = cache.PathOf(ContentHash.Of(raw));
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw))).Status);

        var stamp = File.GetLastWriteTimeUtc(path);
        var corrupt = raw.ToArray();
        corrupt[60_000] ^= 0x5A;
        await File.WriteAllBytesAsync(path, corrupt);
        File.SetLastWriteTimeUtc(path, stamp);

        // #42 remains open for ordinary acquisitions: the session trust is used.
        var (plainState, plainLease) = await cache.AcquireAsync(Ref(raw));
        Assert.Equal(AppearanceCacheState.Present, plainState);
        plainLease!.Dispose();

        // The held lease never relies on that trust.
        var (state, lease) = await cache.AcquireHeldAsync(Ref(raw), raw.Length);
        Assert.Equal(AppearanceCacheState.Missing, state);
        Assert.Null(lease);
        Assert.False(File.Exists(path));
        AssertNothingHeld(cache);
        Assert.Equal(0, cache.Usage.Entries);
        Assert.Equal(AppearanceCacheState.Missing, (await cache.AcquireAsync(Ref(raw))).State);

        // A verified copy can take its place and be held.
        Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw))).Status);
        var (again, held) = await cache.AcquireHeldAsync(Ref(raw), raw.Length);
        Assert.Equal(AppearanceCacheState.Present, again);
        Assert.Equal(raw, held!.Bytes!.Value.ToArray());
        held.Dispose();
        AssertNothingHeld(cache);
    }

    [Fact]
    public async Task AnEntryFromAnEarlierSessionIsRehashedAndHeldWithItsBytesWithinTheBound()
    {
        var folder = Folder();
        var raw = RandomNumberGenerator.GetBytes(50_000);
        var setup = AppearanceBlobCache.CreateForTests(folder, 1_000_000);
        await setup.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
        var path = setup.PathOf(ContentHash.Of(raw));
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var cache = AppearanceBlobCache.CreateForTests(folder, 1_000_000);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.AcquireHeldAsync(Ref(raw), -1));
        Assert.Equal((AppearanceCacheState.Missing, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(new AppearanceBlobRef(ContentHash.Of(raw), 0), 0));

        var (state, lease) = await cache.AcquireHeldAsync(Ref(raw), raw.Length);
        Assert.Equal(AppearanceCacheState.Present, state);
        Assert.True(lease!.IsHeld);
        Assert.Equal(path, lease.Path);
        Assert.Equal(raw, lease.Bytes!.Value.ToArray());
        // The use is recorded, as for any acquisition.
        Assert.True(File.GetLastWriteTimeUtc(path) > new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, cache.HeldHandleCount);
        Assert.Equal(1, cache.Usage.InUseEntries);

        // Bytes are kept only when the bound admits them.
        var (_, unkept) = await cache.AcquireHeldAsync(Ref(raw), 0);
        var (_, tooSmall) = await cache.AcquireHeldAsync(Ref(raw), raw.Length - 1);
        Assert.Null(unkept!.Bytes);
        Assert.Null(tooSmall!.Bytes);
        Assert.Equal(3, cache.HeldHandleCount);
        unkept.Dispose();
        tooSmall.Dispose();

        lease.Dispose();
        Assert.False(lease.IsHeld);
        Assert.Null(lease.Bytes);
        AssertNothingHeld(cache);
    }

    [Fact]
    public async Task AHeldEntryIsNeverClearedOrEvictedAndDisposingTwiceReleasesItOnce()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 35_000);
        var (a, b, c) = (RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000), RandomNumberGenerator.GetBytes(10_000));
        foreach (var blob in new[] { a, b, c })
            Assert.Equal(AppearanceCacheStoreStatus.Stored, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(blob)), Ref(blob))).Status);
        // A is the least recently used entry.
        File.SetLastWriteTimeUtc(cache.PathOf(ContentHash.Of(a)), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var held = (await cache.AcquireHeldAsync(Ref(a), 0)).Lease!;
        var plain = (await cache.AcquireAsync(Ref(a))).Lease!;
        Assert.Equal(1, cache.Usage.InUseEntries);

        await cache.ClearUnusedAsync();
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(b))));
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(c))));

        // Disposing the held lease twice releases its own pin once: the plain lease still holds A.
        held.Dispose();
        held.Dispose();
        Assert.Equal(0, cache.HeldHandleCount);
        Assert.Equal(1, cache.Usage.InUseEntries);
        await cache.ClearUnusedAsync();
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));

        plain.Dispose();
        AssertNothingHeld(cache);

        // Held again, A survives eviction for a new entry that cannot fit beside it.
        var again = (await cache.AcquireHeldAsync(Ref(a), 0)).Lease!;
        await cache.SetLimitForTestsAsync(15_000);
        var d = RandomNumberGenerator.GetBytes(10_000);
        Assert.Equal(AppearanceCacheStoreStatus.Full, (await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(d)), Ref(d))).Status);
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(a))));
        again.Dispose();
        AssertNothingHeld(cache);
        await cache.ClearUnusedAsync();
        Assert.False(File.Exists(cache.PathOf(ContentHash.Of(a))));
    }

    [Theory]
    [InlineData("held:before-lock")]
    [InlineData("held:in-lock")]
    [InlineData("held:opened")]
    public async Task ACancelledAcquisitionClosesItsHandleAndPinsNothing(string point)
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(30_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
        using var cancel = new CancellationTokenSource();
        var reached = 0;
        cache.TestHook = async (at, _) =>
        {
            if (at == point)
            {
                reached++;
                await cancel.CancelAsync();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireHeldAsync(Ref(raw), raw.Length, cancel.Token));
        Assert.Equal(1, reached);
        cache.TestHook = null;
        AssertNothingHeld(cache);
        // Cancellation proves nothing about the entry: it is kept and can be held.
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(raw))));
        using var lease = (await cache.AcquireHeldAsync(Ref(raw), raw.Length)).Lease!;
        Assert.Equal(raw, lease.Bytes!.Value.ToArray());
    }

    [Fact]
    public async Task AReadFailureAfterOpeningIsUnavailableAndClosesTheHandle()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(30_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
        var opened = -1;
        cache.TestHook = (at, _) =>
        {
            if (at == "held:opened")
            {
                opened = cache.HeldHandleCount;
                throw new IOException("injected read failure");
            }
            return Task.CompletedTask;
        };

        Assert.Equal((AppearanceCacheState.Unavailable, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(Ref(raw), raw.Length));
        Assert.Equal(1, opened);
        cache.TestHook = null;
        AssertNothingHeld(cache);
        // Unavailable is not proof of corruption: the entry is kept.
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(raw))));
    }

    [Fact]
    public async Task AMissingWrongLengthOrMismatchedEntryIsMissingAndLeavesNothingHeld()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(40_000);
        var path = cache.PathOf(ContentHash.Of(raw));

        // Missing.
        Assert.Equal((AppearanceCacheState.Missing, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(Ref(raw), raw.Length));
        AssertNothingHeld(cache);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var different = raw.ToArray();
        different[^1] ^= 0xFF;
        foreach (var content in new[] { raw[..^1], raw.Append((byte)0).ToArray(), different })
        {
            await File.WriteAllBytesAsync(path, content);
            Assert.Equal((AppearanceCacheState.Missing, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(Ref(raw), raw.Length));
            Assert.False(File.Exists(path));
            AssertNothingHeld(cache);
            Assert.Equal(0, cache.Usage.Entries);
        }
    }

    /// <remarks>
    /// Creates a file symbolic link. On a Windows host whose process lacks only the
    /// link privilege it is reported as skipped, never as passed.
    /// </remarks>
    [HeldLinkFact]
    public async Task ALinkIsRemovedNeverFollowedAndNothingIsHeld()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(20_000);
        var target = Path.Combine(Folder(), "valid-content");
        await File.WriteAllBytesAsync(target, raw);
        var path = cache.PathOf(ContentHash.Of(raw));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);

        Assert.Equal((AppearanceCacheState.Missing, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(Ref(raw), raw.Length));
        Assert.False(File.Exists(path));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal(raw, await File.ReadAllBytesAsync(target));
        AssertNothingHeld(cache);
    }

    /// <remarks>Windows sharing modes; reported as skipped elsewhere.</remarks>
    [WindowsFact]
    public async Task WhileHeldNoOtherHandleCanWriteReplaceOrDeleteTheEntry()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(25_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
        var path = cache.PathOf(ContentHash.Of(raw));
        var replacement = Path.Combine(Folder(), "replacement");
        await File.WriteAllBytesAsync(replacement, new byte[raw.Length]);

        var lease = (await cache.AcquireHeldAsync(Ref(raw), 0)).Lease!;
        Refused(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
        Refused(() => File.WriteAllBytes(path, new byte[raw.Length]));
        Refused(() => File.Delete(path));
        Refused(() => File.Move(replacement, path, overwrite: true));
        // Reading and recording use are still possible.
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));

        lease.Dispose();
        AssertNothingHeld(cache);
        await File.WriteAllBytesAsync(path, raw);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    /// <remarks>Windows sharing modes; reported as skipped elsewhere.</remarks>
    [WindowsFact]
    public async Task AnEntryAnotherProgramHasOpenForWritingIsUnavailable()
    {
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        var raw = RandomNumberGenerator.GetBytes(25_000);
        await cache.StoreEnvelopeAsync(new MemoryStream(RawEnvelope(raw)), Ref(raw));
        var path = cache.PathOf(ContentHash.Of(raw));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal((AppearanceCacheState.Unavailable, (AppearanceCacheHeldLease?)null), await cache.AcquireHeldAsync(Ref(raw), raw.Length));
            AssertNothingHeld(cache);
            Assert.True(File.Exists(path));
        }

        using var lease = (await cache.AcquireHeldAsync(Ref(raw), raw.Length)).Lease!;
        Assert.Equal(raw, lease.Bytes!.Value.ToArray());
    }

    /// <summary>
    /// The transfer client's Glamourer read re-reads the leased entry. When that read
    /// does not match (a timestamp-restored corruption the session trusted), the trust
    /// is withdrawn: the next read re-hashes, removes the corrupt entry and downloads
    /// the blob again.
    /// </summary>
    [Fact]
    public async Task AGlamourerReadMismatchWithdrawsTheTrustSoTheNextReadDownloadsAgain()
    {
        var service = new FakeAppearanceService();
        using var alice = new AppearanceTransferClient(() => service.Settings("alice"), new AppearanceBlobCache(Folder()), null, service);
        using var bob = new AppearanceTransferClient(() => service.Settings("bob"), new AppearanceBlobCache(Folder()), null, service);
        var glamourer = RandomNumberGenerator.GetBytes(6_000);
        var source = Path.Combine(Folder(), "glamourer");
        await File.WriteAllBytesAsync(source, glamourer);
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId("alice"), Ref(glamourer), null, null, [], out var error);
        Assert.Null(error);
        Assert.Equal(AppearanceTransferStatus.Succeeded,
            (await alice.UploadBundleAsync(manifest!, new Dictionary<ContentHash, string> { [ContentHash.Of(glamourer)] = source })).Status);
        Assert.True((await alice.UpdateBundleAsync(manifest!.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);
        int Downloads() => service.RequestsOf("bob").Count(request => request.StartsWith("GET ", StringComparison.Ordinal) && request.Contains("/blobs/", StringComparison.Ordinal));

        var first = await bob.ReadGlamourerStateAsync(manifest.BundleId, 1_000_000);
        Assert.Equal(AppearanceTransferStatus.Succeeded, first.Result.Status);
        Assert.Equal(glamourer, first.Bytes);
        Assert.Equal(1, Downloads());

        var path = bob.Cache.PathOf(ContentHash.Of(glamourer));
        var stamp = File.GetLastWriteTimeUtc(path);
        var corrupt = glamourer.ToArray();
        corrupt[3_000] ^= 0x01;
        await File.WriteAllBytesAsync(path, corrupt);
        File.SetLastWriteTimeUtc(path, stamp);

        var mismatched = await bob.ReadGlamourerStateAsync(manifest.BundleId, 1_000_000);
        Assert.Equal(AppearanceTransferStatus.CacheUnavailable, mismatched.Result.Status);
        Assert.Null(mismatched.Bytes);
        Assert.Equal(1, Downloads());

        var recovered = await bob.ReadGlamourerStateAsync(manifest.BundleId, 1_000_000);
        Assert.Equal(AppearanceTransferStatus.Succeeded, recovered.Result.Status);
        Assert.Equal(glamourer, recovered.Bytes);
        Assert.Equal(2, Downloads());
        Assert.Equal(glamourer, await File.ReadAllBytesAsync(path));
        AssertNothingHeld(bob.Cache);
    }

    private static void AssertNothingHeld(AppearanceBlobCache cache)
    {
        Assert.Equal(0, cache.HeldHandleCount);
        Assert.Equal(0, cache.Usage.InUseEntries);
        Assert.Equal(0, cache.Usage.InUseBytes);
    }

    /// <summary>The sharing mode refuses the operation (a sharing violation or access denied).</summary>
    private static void Refused(Action operation)
    {
        var exception = Record.Exception(operation);
        Assert.True(exception is IOException or UnauthorizedAccessException, $"Expected a refusal, got {exception?.GetType().Name ?? "success"}.");
    }

    private string Folder()
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Runs only on Windows, where file sharing modes are enforced; reported as skipped elsewhere.</summary>
file sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows file sharing modes only; not run on this operating system.";
    }
}

/// <summary>Like the cache limit tests' link fact: skipped, never passed, when Windows lacks only the link privilege.</summary>
file sealed class HeldLinkFactAttribute : FactAttribute
{
    public HeldLinkFactAttribute()
    {
        Skip = SymbolicLinkProbe.FileSystem.SkipReason(OperatingSystem.IsWindows());
    }
}
