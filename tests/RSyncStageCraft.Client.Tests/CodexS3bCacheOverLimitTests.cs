using System.Buffers.Binary;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>Supplemental AM4-S3b coverage for the accepted CacheOverLimit case.</summary>
public sealed class CodexS3bCacheOverLimitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverLimitRefusesTheNewHoldAndPreservesTheExistingHold(bool lowerDuringPartialHold)
    {
        var root = Path.Combine(Path.GetTempPath(), "rsc-codex-s3b-overlimit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FakeAppearanceService { AcceptSkeletonGroups = true };
            using var alice = new AppearanceTransferClient(
                () => service.Settings("alice"), new AppearanceBlobCache(Path.Combine(root, "alice")), null, service);
            var cache = AppearanceBlobCache.CreateForTests(Path.Combine(root, "bob"), 1_000_000);
            using var bob = new AppearanceTransferClient(() => service.Settings("bob"), cache, null, service);
            const string basePath = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
            const string hairPath = "chara/human/c0101/skeleton/hair/h0001/skl_c0101h0001.sklb";
            var baseBytes = Skeleton(1);
            var hairBytes = Skeleton(2);
            var oldManifest = await PublishAsync(alice, root, (basePath, baseBytes));
            var nextManifest = await PublishAsync(alice, root, (basePath, baseBytes), (hairPath, hairBytes));
            var receive = new AppearanceReceiveOptions(AllowSkeletonGroups: true);
            var oldResult = await bob.HoldBundleAsync(oldManifest.BundleId, 101, receive);
            Assert.True(oldResult.Result.Succeeded);
            using var oldHold = oldResult.Hold!;
            Assert.True(oldHold.IsIntact);
            Assert.Equal(1, cache.HeldHandleCount);
            var memberVisits = 0;
            if (lowerDuringPartialHold)
            {
                bob.TestHook = async point =>
                {
                    if (point == "hold:before-member" && ++memberVisits == 2)
                    {
                        // Old hold plus the new hold's first (shared) member.
                        Assert.Equal(2, cache.HeldHandleCount);
                        await cache.SetLimitForTestsAsync(16);
                        Assert.True(cache.Usage.OverLimit);
                    }
                };
            }
            else
            {
                await cache.SetLimitForTestsAsync(16);
                Assert.True(cache.Usage.OverLimit);
            }

            var failed = await bob.HoldBundleAsync(nextManifest.BundleId, 101, receive);

            Assert.Equal(AppearanceTransferStatus.CacheOverLimit, failed.Result.Status);
            Assert.Null(failed.Hold);
            Assert.Null(failed.Group);
            Assert.True(oldHold.IsIntact);
            Assert.Equal(1, bob.LiveHoldCount);
            Assert.Equal(1, cache.HeldHandleCount);
            Assert.Equal(1, cache.Usage.InUseEntries);
            Assert.Equal(baseBytes.Length, cache.Usage.InUseBytes);
            Assert.Equal(0, cache.Usage.TemporaryBytes);
            Assert.Empty(Directory.EnumerateFiles(cache.TemporaryDirectory));
            Assert.Equal(baseBytes, await File.ReadAllBytesAsync(cache.PathOf(ContentHash.Of(baseBytes))));
            if (lowerDuringPartialHold)
                Assert.Equal(2, memberVisits);
            bob.TestHook = null;
            oldHold.Dispose();
            Assert.Equal(0, bob.LiveHoldCount);
            Assert.Equal(0, cache.HeldHandleCount);
            Assert.Equal(0, cache.Usage.InUseEntries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AppearanceBundleManifest> PublishAsync(
        AppearanceTransferClient alice, string root, params (string GamePath, byte[] Content)[] files)
    {
        var glamourer = Encoding.UTF8.GetBytes("synthetic Glamourer state");
        var sources = new Dictionary<ContentHash, string>();
        foreach (var bytes in files.Select(file => file.Content).Append(glamourer))
        {
            var hash = ContentHash.Of(bytes);
            var path = Path.Combine(root, hash.Value);
            await File.WriteAllBytesAsync(path, bytes);
            sources[hash] = path;
        }
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId("alice"), Ref(glamourer), null, null,
            files.Select(file => new AppearanceFileEntry(file.GamePath, Ref(file.Content))), 101, out var error);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.True((await alice.UploadBundleAsync(manifest, sources)).Succeeded);
        Assert.True((await alice.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);
        return manifest;
    }

    private static byte[] Skeleton(byte payload)
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x736B6C62);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x31333031);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 16);
        bytes[16] = payload;
        return bytes;
    }
}
