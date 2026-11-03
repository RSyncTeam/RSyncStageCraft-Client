using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Receiving a skeleton group (<see cref="AppearanceTransferClient.HoldBundleAsync"/>):
/// default off, cheap refusals before any blob request, only members held through
/// S3a's write-locked re-hashed leases, all-or-nothing assembly, and release on
/// failure, cancellation, disposal and client disposal. Synthetic bytes only.
/// </summary>
public sealed class AppearanceBundleHoldTests : IDisposable
{
    private const ushort Hyur = 101;
    private const string BaseSklb = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    private const string BasePhyb = "chara/human/c0101/skeleton/base/b0001/phy_c0101b0001.phyb";
    private const string HairSklb = "chara/human/c0101/skeleton/hair/h0001/skl_c0101h0001.sklb";
    private const string Model = "chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl";
    private static readonly AppearanceReceiveOptions On = new(AllowSkeletonGroups: true, AllowDeformer: true);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-hold", Guid.NewGuid().ToString("N"));
    private readonly FakeAppearanceService service = new() { AcceptSkeletonGroups = true };

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A hold left open by a failed assertion must not hide that failure.
        }
    }

    [Fact]
    public async Task OnlyTheMembersAreHeldAndDisposingReleasesThemOnce()
    {
        var sklb = Sklb();
        var model = RandomNumberGenerator.GetBytes(3_000);
        var manifest = await PublishAsync(Hyur,
            (BaseSklb, sklb), (BasePhyb, Phyb()), (SkeletonResourcePaths.DeformerGamePath, Pbd()), (Model, model));
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.Succeeded, result.Result.Status);
        var hold = result.Hold!;
        Assert.Same(hold.Group, result.Group);
        Assert.Equal(manifest.BundleId, result.Manifest!.BundleId);
        Assert.Equal(Hyur, hold.Group.RaceCode);
        Assert.Equal(new[] { BasePhyb, BaseSklb, SkeletonResourcePaths.DeformerGamePath }.Order(StringComparer.Ordinal),
            hold.Group.Members.Select(member => member.Path.GamePath));
        Assert.Equal(manifest.SkeletonGroup!.Members.Select(member => member.Content), hold.Group.Members.Select(member => member.Content));
        Assert.True(hold.IsIntact);
        Assert.Equal(3, hold.LeaseCount);
        // Only the three members stay in use; the Glamourer blob and the model were released.
        Assert.Equal(3, bob.Cache.HeldHandleCount);
        Assert.Equal(3, bob.Cache.Usage.InUseEntries);
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(model, await File.ReadAllBytesAsync(bob.Cache.PathOf(ContentHash.Of(model))));
        Assert.Equal(sklb, await File.ReadAllBytesAsync(bob.Cache.PathOf(ContentHash.Of(sklb))));

        // A redraw checks the hold: no request, no new lease.
        var requests = service.Requests.Count;
        Assert.True(hold.IsIntact);
        Assert.Equal(requests, service.Requests.Count);
        Assert.Equal(3, bob.Cache.Usage.InUseEntries);

        hold.Dispose();
        Assert.False(hold.IsIntact);
        Assert.Equal(0, hold.LeaseCount);
        AssertNothingHeld(bob);
        hold.Dispose();
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task OptionsOffRefuseAFormatTwoBundleBeforeAnyBlobRequestAsToday()
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        using var bob = Client("bob");

        foreach (var options in new[] { AppearanceReceiveOptions.Default, new AppearanceReceiveOptions(AllowDeformer: true) })
        {
            var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, options);
            Assert.Equal(AppearanceTransferStatus.InvalidResponse, result.Result.Status);
            Assert.Null(result.Hold);
            Assert.Null(result.Manifest);
        }
        // The existing download is unchanged: format 1 only.
        Assert.Equal(AppearanceTransferStatus.InvalidResponse, (await bob.DownloadBundleAsync(manifest.BundleId)).Result.Status);
        Assert.Equal(0, BlobRequests());
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task ADeformerIsRefusedWithoutItsOptionBeforeAnyBlobRequest()
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (SkeletonResourcePaths.DeformerGamePath, Pbd()));
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, new AppearanceReceiveOptions(AllowSkeletonGroups: true));

        Assert.Equal(AppearanceTransferStatus.SkeletonGroupNotAllowed, result.Result.Status);
        Assert.Equal(SkeletonResourcePaths.DeformerGamePath, result.GamePath);
        Assert.Null(result.Hold);
        Assert.Equal(0, BlobRequests());
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((ushort)1801)]
    [InlineData((ushort)201)]
    public async Task ARaceMismatchIsRefusedBeforeAnyBlobRequest(ushort? puppetRace)
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, puppetRace, On);

        Assert.Equal(AppearanceTransferStatus.SkeletonGroupRaceMismatch, result.Result.Status);
        Assert.Null(result.Hold);
        Assert.Equal(0, BlobRequests());
        AssertNothingHeld(bob);
    }

    /// <summary>The hash matches but the bytes fail the role's checks (a server that skipped them): the whole group is refused.</summary>
    [Fact]
    public async Task AMemberThatFailsItsStructureCheckRefusesTheWholeGroup()
    {
        var bad = Sklb();
        bad[0] ^= 0xFF;
        var manifest = await PublishAsync(Hyur, (BaseSklb, bad), (BasePhyb, Phyb()));
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.SkeletonGroupInvalid, result.Result.Status);
        Assert.Equal(SkeletonGroupRefusal.Structure, result.Refusal);
        Assert.Equal(BaseSklb, result.GamePath);
        Assert.Equal(SkeletonStructureProblem.BadMagic, result.Problem);
        Assert.Null(result.Hold);
        Assert.Null(result.Group);
        AssertNothingHeld(bob);
    }

    /// <summary>
    /// The receiver cannot judge completeness itself (corrigendum B1): a manifest
    /// other than the one the bundle id names, such as one with a member added or
    /// removed, is refused as an invalid response before any blob request.
    /// </summary>
    [Fact]
    public async Task AForgedManifestIsRefusedBeforeAnyBlobRequest()
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        var forged = AppearanceBundleManifest.TryCreate(manifest.OwnerId, manifest.Glamourer, null, null,
            manifest.Files.Where(file => file.GamePath != BasePhyb), Hyur, out var error);
        Assert.Null(error);
        service.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(forged!.CanonicalBytes) })
            : null;
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.InvalidResponse, result.Result.Status);
        Assert.Equal(0, BlobRequests());
        AssertNothingHeld(bob);
    }

    /// <summary>A member changed in the cache since an earlier hold (same length, time restored) is found, replaced and validated.</summary>
    [Fact]
    public async Task AMemberChangedInTheCacheIsDownloadedAgainAndValidated()
    {
        var sklb = Sklb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, sklb), (BasePhyb, Phyb()));
        using var bob = Client("bob");
        (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!.Dispose();
        Assert.Equal(3, BlobRequests());

        var path = bob.Cache.PathOf(ContentHash.Of(sklb));
        var stamp = File.GetLastWriteTimeUtc(path);
        var changed = sklb.ToArray();
        changed[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, changed);
        File.SetLastWriteTimeUtc(path, stamp);

        using var hold = (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!;
        Assert.True(hold.IsIntact);
        Assert.Equal(4, BlobRequests());
        Assert.Equal(sklb, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ACorruptDownloadHoldsNothing()
    {
        var sklb = Sklb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, sklb), (BasePhyb, Phyb()));
        service.Tamper = request => request.RequestUri!.AbsolutePath.EndsWith(ContentHash.Of(sklb).Value, StringComparison.Ordinal);
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.InvalidResponse, result.Result.Status);
        Assert.Null(result.Hold);
        AssertNothingHeld(bob);
        Assert.False(File.Exists(bob.Cache.PathOf(ContentHash.Of(sklb))));
    }

    [Fact]
    public async Task AMemberTheServerDoesNotHaveHoldsNothing()
    {
        var phyb = Phyb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, phyb));
        service.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith(ContentHash.Of(phyb).Value, StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"code\":\"notFound\"}", Encoding.UTF8, "application/json"),
            })
            : null;
        using var bob = Client("bob");

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.NotFound, result.Result.Status);
        Assert.Equal(ContentHash.Of(phyb), result.Result.Blob);
        Assert.Null(result.Hold);
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData("before-call")]
    [InlineData("hold:after-manifest")]
    [InlineData("hold:before-member")]
    [InlineData("hold:after-download")]
    [InlineData("hold:before-assemble")]
    [InlineData("download")] // the first download: the Glamourer blob, not a member
    [InlineData("cache:held:opened")]
    public async Task ACancelledHoldLeavesNothingHeld(string point)
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        using var bob = Client("bob");
        using var cancel = new CancellationTokenSource();
        var reached = 0;
        if (point == "before-call")
        {
            reached++;
            await cancel.CancelAsync();
        }
        else if (point.StartsWith("cache:", StringComparison.Ordinal))
        {
            bob.Cache.TestHook = async (at, _) =>
            {
                if ("cache:" + at == point && reached++ == 0)
                    await cancel.CancelAsync();
            };
        }
        else
        {
            bob.TestHook = async at =>
            {
                if (at == point && reached++ == 0)
                    await cancel.CancelAsync();
            };
        }

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On, cancel.Token);

        Assert.True(reached > 0);
        Assert.Equal(AppearanceTransferStatus.Cancelled, result.Result.Status);
        Assert.Null(result.Hold);
        bob.TestHook = null;
        bob.Cache.TestHook = null;
        AssertNothingHeld(bob);
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.TemporaryDirectory));
        // Nothing was left behind that stops a later hold.
        using var hold = (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!;
        Assert.True(hold.IsIntact);
    }

    /// <summary>
    /// Make before break: while an old and a new hold both exist, both count
    /// against the limit; a new hold that does not fit fails whole and leaves the
    /// old one intact; once the old one is released the new one fits.
    /// </summary>
    [Fact]
    public async Task AReplacementThatDoesNotFitFailsWholeAndKeepsTheOldHold()
    {
        var oldSklb = Sklb(20_000);
        var newSklb = Sklb(20_000);
        var old = await PublishAsync(Hyur, (BaseSklb, oldSklb), (BasePhyb, Phyb()));
        var replacement = await PublishAsync(Hyur, (BaseSklb, newSklb), (BasePhyb, Phyb()));
        // Room for one group and a Glamourer blob, not for two groups.
        using var bob = Client("bob", AppearanceBlobCache.CreateForTests(Folder(), 40_000));

        var first = (await bob.HoldBundleAsync(old.BundleId, Hyur, On)).Hold!;
        var failed = await bob.HoldBundleAsync(replacement.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.CacheFull, failed.Result.Status);
        Assert.Null(failed.Hold);
        Assert.True(first.IsIntact);
        Assert.Equal(2, bob.Cache.HeldHandleCount);
        Assert.Equal(2, bob.Cache.Usage.InUseEntries);
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(oldSklb, await File.ReadAllBytesAsync(bob.Cache.PathOf(ContentHash.Of(oldSklb))));
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.TemporaryDirectory));

        // Released first (an explicit choice), the replacement fits.
        first.Dispose();
        using var second = (await bob.HoldBundleAsync(replacement.BundleId, Hyur, On)).Hold!;
        Assert.True(second.IsIntact);
        Assert.True(bob.Cache.Usage.TotalBytes <= bob.Cache.LimitBytes);
    }

    [Fact]
    public async Task WhileAnOldAndANewHoldBothExistBothCount()
    {
        var oldSklb = Sklb(5_000);
        var newSklb = Sklb(5_000);
        var old = await PublishAsync(Hyur, (BaseSklb, oldSklb), (BasePhyb, Phyb()));
        var replacement = await PublishAsync(Hyur, (BaseSklb, newSklb), (BasePhyb, Phyb()));
        using var bob = Client("bob");

        var first = (await bob.HoldBundleAsync(old.BundleId, Hyur, On)).Hold!;
        var second = (await bob.HoldBundleAsync(replacement.BundleId, Hyur, On)).Hold!;
        // The physics module is the same blob in both: one entry, held twice.
        Assert.Equal(3, bob.Cache.Usage.InUseEntries);
        Assert.Equal(4, bob.Cache.HeldHandleCount);
        Assert.Equal(2, bob.LiveHoldCount);

        first.Dispose();
        Assert.Equal(2, bob.Cache.Usage.InUseEntries);
        Assert.Equal(2, bob.Cache.HeldHandleCount);
        Assert.True(second.IsIntact);
        second.Dispose();
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task OneBlobServingTwoMembersAndAModelIsHeldOnceWithoutAShortLease()
    {
        var sklb = Sklb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, sklb), (HairSklb, sklb), (Model, sklb));
        using var bob = Client("bob");

        using var hold = (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!;

        Assert.Equal(2, hold.Group.Members.Count);
        Assert.Equal(1, hold.LeaseCount);
        Assert.Equal(1, bob.Cache.HeldHandleCount);
        Assert.Equal(1, bob.Cache.Usage.InUseEntries);
        // The Glamourer blob and the shared blob, each downloaded once.
        Assert.Equal(2, BlobRequests());
    }

    [Fact]
    public async Task DisposingTheClientReleasesEveryHoldItStillHas()
    {
        var a = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        var b = await PublishAsync(Hyur, (BaseSklb, Sklb()));
        var cache = new AppearanceBlobCache(Folder());
        var bob = Client("bob", cache);
        var first = (await bob.HoldBundleAsync(a.BundleId, Hyur, On)).Hold!;
        var second = (await bob.HoldBundleAsync(b.BundleId, Hyur, On)).Hold!;
        Assert.Equal(2, bob.LiveHoldCount);
        Assert.True(cache.HeldHandleCount > 0);

        bob.Dispose();

        Assert.False(first.IsIntact);
        Assert.False(second.IsIntact);
        Assert.Equal(0, bob.LiveHoldCount);
        Assert.Equal(0, cache.HeldHandleCount);
        Assert.Equal(0, cache.Usage.InUseEntries);
        // Disposing a hold again after its client is a no-op.
        first.Dispose();
        bob.Dispose();
    }

    [Fact]
    public async Task AClientDisposedWhileAHoldIsBeingMadeKeepsNothing()
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));
        var cache = new AppearanceBlobCache(Folder());
        var bob = Client("bob", cache);
        bob.TestHook = at =>
        {
            if (at == "hold:before-assemble")
                bob.Dispose();
            return Task.CompletedTask;
        };

        var result = await bob.HoldBundleAsync(manifest.BundleId, Hyur, On);

        Assert.Equal(AppearanceTransferStatus.Cancelled, result.Result.Status);
        Assert.Null(result.Hold);
        Assert.Equal(0, bob.LiveHoldCount);
        Assert.Equal(0, cache.HeldHandleCount);
        Assert.Equal(0, cache.Usage.InUseEntries);
    }

    [Fact]
    public async Task ClearingUnusedEntriesAndLoweringTheLimitKeepHeldMembers()
    {
        var sklb = Sklb();
        var phyb = Phyb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, sklb), (BasePhyb, phyb));
        var cache = AppearanceBlobCache.CreateForTests(Folder(), 1_000_000);
        using var bob = Client("bob", cache);
        using var hold = (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!;

        await cache.ClearUnusedAsync();
        await cache.SetLimitForTestsAsync(16);

        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(sklb))));
        Assert.True(File.Exists(cache.PathOf(ContentHash.Of(phyb))));
        Assert.False(File.Exists(cache.PathOf(manifest.Glamourer.Hash)));
        Assert.True(hold.IsIntact);
    }

    /// <summary>A format-1 bundle gets no hold and makes exactly the requests a download makes, with either options.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFormatOneBundleMakesTheDownloadsRequestsAndHoldsNothing(bool on)
    {
        var model = RandomNumberGenerator.GetBytes(4_000);
        var texture = RandomNumberGenerator.GetBytes(5_000);
        var manifest = await PublishAsync(null, (Model, model), ("chara/human/c0101/obj/body/b0001/texture/c0101b0001_top_n.tex", texture));
        using var downloader = Client("bob");
        using var holder = Client("bob");

        service.ClearRequests();
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await downloader.DownloadBundleAsync(manifest.BundleId)).Result.Status);
        var downloadRequests = service.RequestsOf("bob");
        service.ClearRequests();
        var result = await holder.HoldBundleAsync(manifest.BundleId, Hyur, on ? On : AppearanceReceiveOptions.Default);

        Assert.Equal(AppearanceTransferStatus.Succeeded, result.Result.Status);
        Assert.Null(result.Hold);
        Assert.Null(result.Group);
        Assert.Equal(manifest.BundleId, result.Manifest!.BundleId);
        Assert.Equal(downloadRequests, service.RequestsOf("bob"));
        Assert.Equal(model, await File.ReadAllBytesAsync(holder.Cache.PathOf(ContentHash.Of(model))));
        AssertNothingHeld(holder);
    }

    /// <remarks>Windows sharing modes; reported as skipped elsewhere.</remarks>
    [WindowsFact]
    public async Task WhileHeldNoOtherProgramCanChangeAMember()
    {
        var sklb = Sklb();
        var manifest = await PublishAsync(Hyur, (BaseSklb, sklb), (BasePhyb, Phyb()));
        using var bob = Client("bob");
        using var hold = (await bob.HoldBundleAsync(manifest.BundleId, Hyur, On)).Hold!;
        var path = bob.Cache.PathOf(ContentHash.Of(sklb));

        var exception = Record.Exception(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.True(exception is IOException or UnauthorizedAccessException, $"Expected a refusal, got {exception?.GetType().Name ?? "success"}.");
        Assert.Equal(sklb, await File.ReadAllBytesAsync(path));

        hold.Dispose();
        await File.WriteAllBytesAsync(path, sklb);
    }

    // ---- Helpers ---------------------------------------------------------------

    /// <summary>Uploads and publishes alice's bundle (format 2 when <paramref name="targetRace"/> is set).</summary>
    private async Task<AppearanceBundleManifest> PublishAsync(ushort? targetRace, params (string GamePath, byte[] Content)[] files)
    {
        var glamourer = RandomNumberGenerator.GetBytes(2_000);
        var folder = Folder();
        var sources = new Dictionary<ContentHash, string>();
        foreach (var content in files.Select(file => file.Content).Append(glamourer))
        {
            var path = Path.Combine(folder, ContentHash.Of(content).Value);
            await File.WriteAllBytesAsync(path, content);
            sources[ContentHash.Of(content)] = path;
        }
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId("alice"), Ref(glamourer), null, null,
            files.Select(file => new AppearanceFileEntry(file.GamePath, Ref(file.Content))), targetRace, out var error);
        Assert.Null(error);
        using var alice = Client("alice");
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest!, sources)).Status);
        Assert.True((await alice.UpdateBundleAsync(manifest!.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);
        service.ClearRequests();
        return manifest;
    }

    private AppearanceTransferClient Client(string user, AppearanceBlobCache? cache = null) =>
        new(() => service.Settings(user), cache ?? new AppearanceBlobCache(Folder()), null, service);

    private int BlobRequests() =>
        service.RequestsOf("bob").Count(request => request.StartsWith("GET ", StringComparison.Ordinal) && request.Contains("/blobs/", StringComparison.Ordinal));

    private static void AssertNothingHeld(AppearanceTransferClient client)
    {
        Assert.Equal(0, client.LiveHoldCount);
        Assert.Equal(0, client.Cache.HeldHandleCount);
        Assert.Equal(0, client.Cache.Usage.InUseEntries);
    }

    private string Folder()
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A synthetic .sklb (version 1301) with a random payload, so every call is a distinct blob.</summary>
    private static byte[] Sklb(int payload = 32)
    {
        var data = new byte[16 + payload];
        RandomNumberGenerator.Fill(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x736B6C62);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes("1301")));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        return data;
    }

    private static byte[] Phyb()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 24);
        return data;
    }

    /// <summary>One deformer entry for Hyur, with a 4-byte body after the tables.</summary>
    private static byte[] Pbd()
    {
        var data = new byte[4 + 20 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Hyur);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 24);
        return data;
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
