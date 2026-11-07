using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The Debug skeleton-group receiver (W1): a fixed receive order through a fake
/// host port, release in the hold's order (remove, then nothing names the
/// files, then dispose), a hold kept whenever a clean-up step fails or times
/// out, and a slot reserved while its receive is in flight. Synthetic bytes and
/// a fake port only: no game, no IPC, no actor.
/// </summary>
public sealed class SkeletonGroupReceiverTests : IDisposable
{
    private const ushort Hyur = 101;
    private const string BaseSklb = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
    private const string BasePhyb = "chara/human/c0101/skeleton/base/b0001/phy_c0101b0001.phyb";
    private const string Model = "chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl";
    private static readonly AppearanceReceiveOptions On = new(AllowSkeletonGroups: true, AllowDeformer: true);
    private static readonly TimeSpan ShortCleanup = TimeSpan.FromMilliseconds(200);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-skeleton-receiver", Guid.NewGuid().ToString("N"));
    private readonly FakeAppearanceService service = new() { AcceptSkeletonGroups = true };
    private readonly FakeAssignmentPort port = new();

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
    public async Task AReceiveAssignsThenRedrawsAndStaysHiddenUntilTheHostShowsIt()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slot = ActorSlotKey.New();

        var result = await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.Ready, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Redraw, result.Step);
        Assert.True(result.Transfer!.Succeeded);
        Assert.Equal(new[] { "assign", "redraw" }, port.Calls);
        Assert.True(port.HoldIntactAtAssign);
        Assert.Same(On, receiver.Options);
        Assert.True(receiver.CanRedraw(slot));
        Assert.Equal(1, receiver.SlotCount);
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(2, bob.Cache.Usage.InUseEntries);

        // A redraw while held needs no request.
        var requests = service.Requests.Count;
        Assert.True(receiver.CanRedraw(slot));
        Assert.Equal(requests, service.Requests.Count);

        var released = await receiver.ReleaseAsync(slot);

        Assert.Equal(SkeletonGroupReceiveStatus.Released, released.Status);
        Assert.Equal(new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        Assert.Equal(new[] { true, true }, port.HoldIntactAtCleanup);
        Assert.False(receiver.CanRedraw(slot));
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
        Assert.Equal(SkeletonGroupReceiveStatus.NotHeld, (await receiver.ReleaseAsync(slot)).Status);
    }

    [Fact]
    public async Task AFormatOneBundleIsNoGroupAndCallsNothing()
    {
        var manifest = await PublishAsync(null, (Model, RandomNumberGenerator.GetBytes(1_000)));
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slot = ActorSlotKey.New();

        var result = await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.NoGroup, result.Status);
        Assert.Empty(port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        Assert.False(receiver.CanRedraw(slot));
        AssertNothingHeld(bob);
    }

    /// <summary>The receiver's options are the hold's options: off refuses a format-2 bundle before any blob request, as today.</summary>
    [Fact]
    public async Task OptionsOffStayHiddenBeforeAnyBlobRequest()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, AppearanceReceiveOptions.Default);

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.InvalidResponse);
        Assert.Equal(0, BlobRequests());
        Assert.Empty(port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((ushort)201)]
    public async Task ARaceMismatchStaysHiddenBeforeAnyBlobRequest(ushort? puppetRace)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, puppetRace);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.SkeletonGroupRaceMismatch);
        Assert.Equal(0, BlobRequests());
        Assert.Empty(port.Calls);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task AForgedManifestStaysHiddenBeforeAnyBlobRequest()
    {
        var manifest = await PublishGroupAsync();
        var forged = AppearanceBundleManifest.TryCreate(manifest.OwnerId, manifest.Glamourer, null, null,
            manifest.Files.Where(file => file.GamePath != BasePhyb), Hyur, out var error);
        Assert.Null(error);
        service.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(forged!.CanonicalBytes) })
            : null;
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.InvalidResponse);
        Assert.Equal(0, BlobRequests());
        Assert.Empty(port.Calls);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task AMemberThatFailsItsStructureCheckStaysHidden()
    {
        var bad = Sklb();
        bad[0] ^= 0xFF;
        var manifest = await PublishAsync(Hyur, (BaseSklb, bad), (BasePhyb, Phyb()));
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.SkeletonGroupInvalid);
        Assert.Empty(port.Calls);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task ADeformerWithItsOptionOffStaysHiddenBeforeAnyBlobRequest()
    {
        var manifest = await PublishAsync(Hyur, (BaseSklb, Sklb()), (SkeletonResourcePaths.DeformerGamePath, Pbd()));
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, new AppearanceReceiveOptions(AllowSkeletonGroups: true));

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.SkeletonGroupNotAllowed);
        Assert.Equal(0, BlobRequests());
        Assert.Empty(port.Calls);
        AssertNothingHeld(bob);
    }

    /// <summary>A group that does not fit the cache stays hidden and calls nothing; the hold already made is untouched.</summary>
    [Fact]
    public async Task AGroupThatDoesNotFitTheCacheStaysHidden()
    {
        var held = await PublishAsync(Hyur, (BaseSklb, Sklb(20_000)), (BasePhyb, Phyb()));
        var other = await PublishAsync(Hyur, (BaseSklb, Sklb(20_000)), (BasePhyb, Phyb()));
        using var bob = Client("bob", AppearanceBlobCache.CreateForTests(Folder(), 40_000));
        using var first = (await bob.HoldBundleAsync(held.BundleId, Hyur, On)).Hold!;
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), other.BundleId, Hyur);

        AssertStayHiddenAtHold(result, AppearanceTransferStatus.CacheFull);
        Assert.Empty(port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        Assert.True(first.IsIntact);
        Assert.Equal(1, bob.LiveHoldCount);
    }

    [Theory]
    [InlineData(Behaviour.False)]
    [InlineData(Behaviour.Throw)]
    public async Task AFailedAssignmentIsRemovedThenTheHoldDisposed(Behaviour assign)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        port.Assign = assign;

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Assign, result.Step);
        Assert.False(result.Cancelled);
        // Nothing was redrawn: removing the assignment is enough before disposing.
        Assert.Equal(new[] { "assign", "remove" }, port.Calls);
        Assert.Equal(new[] { true }, port.HoldIntactAtCleanup);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    /// <summary>
    /// Owner contract 1: when the assignment fails and removing it then fails,
    /// throws or times out, the files may still be named, so the hold is kept,
    /// ReleaseFailed is returned, and the slot stays occupied until a later
    /// release succeeds in the full order.
    /// </summary>
    [Theory]
    [InlineData(Behaviour.False)]
    [InlineData(Behaviour.Throw)]
    [InlineData(Behaviour.WaitForToken)]
    [InlineData(Behaviour.IgnoreToken)]
    public async Task WhenRemovingAFailedAssignmentFailsTheHoldIsKept(Behaviour remove)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On, ShortCleanup);
        var slot = ActorSlotKey.New();
        port.Assign = Behaviour.False;
        port.Remove = remove;

        var result = await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.ReleaseFailed, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Remove, result.Step);
        Assert.Equal(new[] { "assign", "remove" }, port.Calls);
        // Kept, never disposed: its members stay leased.
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(2, bob.Cache.Usage.InUseEntries);
        Assert.Equal(1, receiver.SlotCount);
        Assert.Equal(1, receiver.ReleaseFailedCount);
        Assert.False(receiver.CanRedraw(slot));

        // Occupied: no second hold for the slot, not even a manifest request.
        var requests = service.RequestsOf("bob").Count;
        Assert.Equal(SkeletonGroupReceiveStatus.SlotOccupied, (await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur)).Status);
        Assert.Equal(requests, service.RequestsOf("bob").Count);
        Assert.Equal(1, bob.LiveHoldCount);

        // A later release, once the host can act, runs the full order and only then disposes.
        port.Remove = Behaviour.True;
        var released = await receiver.ReleaseAsync(slot);

        Assert.Equal(SkeletonGroupReceiveStatus.Released, released.Status);
        Assert.Equal(new[] { "assign", "remove", "remove", "nothing" }, port.Calls);
        Assert.Equal(new[] { true, true, true }, port.HoldIntactAtCleanup);
        Assert.Equal(0, receiver.ReleaseFailedCount);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData(Behaviour.False)]
    [InlineData(Behaviour.Throw)]
    public async Task AFailedRedrawUsesTheFullReleaseOrder(Behaviour redraw)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        port.Redraw = redraw;

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Redraw, result.Step);
        Assert.Equal(new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        Assert.Equal(new[] { true, true }, port.HoldIntactAtCleanup);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData("remove", SkeletonGroupReceiveStep.Remove)]
    [InlineData("nothing", SkeletonGroupReceiveStep.NothingNamesFiles)]
    public async Task WhenAReleaseStepAfterAFailedRedrawFailsTheHoldIsKept(string failing, SkeletonGroupReceiveStep step)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On, ShortCleanup);
        var slot = ActorSlotKey.New();
        port.Redraw = Behaviour.False;
        if (failing == "remove")
            port.Remove = Behaviour.IgnoreToken;
        else
            port.NothingNames = Behaviour.False;

        var result = await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.ReleaseFailed, result.Status);
        Assert.Equal(step, result.Step);
        // NothingNamesFiles is never asked before Remove has succeeded.
        Assert.Equal(failing == "remove" ? new[] { "assign", "redraw", "remove" } : new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(1, receiver.ReleaseFailedCount);

        port.Remove = Behaviour.True;
        port.NothingNames = Behaviour.True;
        Assert.Equal(SkeletonGroupReceiveStatus.Released, (await receiver.ReleaseAsync(slot)).Status);
        AssertNothingHeld(bob);
    }

    [Theory]
    [InlineData(SkeletonGroupReceiveStep.Assign)]
    [InlineData(SkeletonGroupReceiveStep.Redraw)]
    public async Task ACancelledReceiveStillCleansUpUnderTheReceiversOwnToken(SkeletonGroupReceiveStep at)
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        using var cancel = new CancellationTokenSource();
        if (at == SkeletonGroupReceiveStep.Assign)
            port.Assign = Behaviour.WaitForToken;
        else
            port.Redraw = Behaviour.WaitForToken;
        port.Entered = name =>
        {
            if (name is "assign" && at == SkeletonGroupReceiveStep.Assign || name is "redraw")
                cancel.Cancel();
        };

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur, cancel.Token);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(at, result.Step);
        Assert.True(result.Cancelled);
        Assert.Equal(at == SkeletonGroupReceiveStep.Assign ? new[] { "assign", "remove" } : new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        // The clean-up steps never saw the caller's cancelled token.
        Assert.DoesNotContain(true, port.CleanupTokensCancelledAtCall);
        Assert.NotEmpty(port.CleanupTokensCancelledAtCall);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task AReceiveCancelledBeforeTheHoldHoldsNothingAndCallsNothing()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur, cancel.Token);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Hold, result.Step);
        Assert.True(result.Cancelled);
        Assert.Empty(port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    /// <summary>
    /// Owner contract 2: the slot is reserved before the hold is requested, so a
    /// second receive for it while the first is in flight acquires no hold and
    /// makes no request; a release meanwhile is refused as busy.
    /// </summary>
    [Fact]
    public async Task ASlotIsReservedWhileItsHoldIsBeingMade()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slot = ActorSlotKey.New();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.TestHook = async at =>
        {
            if (at == "hold:after-manifest" && reached.TrySetResult())
                await resume.Task;
        };

        var first = receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var requests = service.RequestsOf("bob").Count;

        var second = await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.SlotOccupied, second.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Admission, second.Step);
        Assert.Equal(requests, service.RequestsOf("bob").Count);
        Assert.Equal(0, BlobRequests());
        Assert.Equal(0, bob.LiveHoldCount);
        Assert.Equal(SkeletonGroupReceiveStatus.SlotBusy, (await receiver.ReleaseAsync(slot)).Status);
        Assert.False(receiver.CanRedraw(slot));
        Assert.Equal(1, receiver.SlotCount);

        resume.SetResult();
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await first).Status);
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(new[] { "assign", "redraw" }, port.Calls);
        Assert.Equal(SkeletonGroupReceiveStatus.Released, (await receiver.ReleaseAsync(slot)).Status);
        AssertNothingHeld(bob);
    }

    /// <summary>Owner contract 2, later in the order: while the host assigns, the slot is still reserved.</summary>
    [Fact]
    public async Task ASlotIsReservedWhileTheHostAssigns()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slot = ActorSlotKey.New();
        port.Assign = Behaviour.Gate;

        var first = receiver.ReceiveAsync(slot, manifest.BundleId, Hyur);
        await port.GateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SkeletonGroupReceiveStatus.SlotOccupied, (await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur)).Status);
        Assert.Equal(1, bob.LiveHoldCount);
        // Another slot is not blocked by it.
        var other = ActorSlotKey.New();
        port.Assign = Behaviour.True;
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(other, manifest.BundleId, Hyur)).Status);
        Assert.Equal(2, bob.LiveHoldCount);

        port.OpenGate(true);
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await first).Status);
        Assert.Equal(2, receiver.SlotCount);
    }

    [Fact]
    public async Task AReleaseInFlightMakesASecondReleaseAndAReceiveWait()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slot = ActorSlotKey.New();
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur)).Status);
        port.Remove = Behaviour.Gate;

        var releasing = receiver.ReleaseAsync(slot);
        await port.GateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SkeletonGroupReceiveStatus.SlotBusy, (await receiver.ReleaseAsync(slot)).Status);
        Assert.Equal(SkeletonGroupReceiveStatus.SlotOccupied, (await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur)).Status);
        Assert.False(receiver.CanRedraw(slot));
        Assert.Equal(1, bob.LiveHoldCount);

        port.OpenGate(true);
        Assert.Equal(SkeletonGroupReceiveStatus.Released, (await releasing).Status);
        Assert.Equal(new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task TheReceiverHoldsAtMostTheActiveActorLimit()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        var slots = Enumerable.Range(0, ActorCapacityPolicy.ActiveMaximumActorsPerScene).Select(_ => ActorSlotKey.New()).ToArray();
        foreach (var slot in slots)
            Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(slot, manifest.BundleId, Hyur)).Status);
        Assert.Equal(ActorCapacityPolicy.ActiveMaximumActorsPerScene, receiver.SlotCount);
        var requests = service.RequestsOf("bob").Count;

        var ninth = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, ninth.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Admission, ninth.Step);
        Assert.Equal(requests, service.RequestsOf("bob").Count);
        Assert.Equal(ActorCapacityPolicy.ActiveMaximumActorsPerScene, bob.LiveHoldCount);

        Assert.Equal(SkeletonGroupReceiveStatus.Released, (await receiver.ReleaseAsync(slots[0])).Status);
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur)).Status);
        Assert.Equal(ActorCapacityPolicy.ActiveMaximumActorsPerScene, receiver.SlotCount);
    }

    /// <summary>Unload: every held slot is released in order; a hold whose release fails is kept and reported, then its client releases it.</summary>
    [Fact]
    public async Task DisposingTheReceiverReleasesEverySlotAndKeepsWhatCannotBeReleased()
    {
        var manifest = await PublishGroupAsync();
        var bob = Client("bob");
        var receiver = new SkeletonGroupReceiver(bob, port, On, ShortCleanup);
        var good = ActorSlotKey.New();
        var stuck = ActorSlotKey.New();
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(good, manifest.BundleId, Hyur)).Status);
        Assert.Equal(SkeletonGroupReceiveStatus.Ready, (await receiver.ReceiveAsync(stuck, manifest.BundleId, Hyur)).Status);
        port.FailNothingNamesFor = stuck;

        await receiver.DisposeAsync();

        Assert.Equal(1, receiver.SlotCount);
        Assert.Equal(1, receiver.ReleaseFailedCount);
        Assert.False(receiver.CanRedraw(good));
        Assert.Equal(1, bob.LiveHoldCount);
        Assert.Equal(2, port.Calls.Count(call => call == "remove"));
        Assert.Equal(2, port.Calls.Count(call => call == "nothing"));
        // Every clean-up call saw its own slot's hold still intact: disposal is last.
        Assert.Equal(new[] { true, true, true, true }, port.HoldIntactAtCleanup);
        // Disposed: a new receive is refused before any request.
        var requests = service.RequestsOf("bob").Count;
        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, (await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur)).Status);
        Assert.Equal(requests, service.RequestsOf("bob").Count);

        bob.Dispose();
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task AReceiverDisposedWhileTheHoldIsMadeDisposesItWithoutCallingTheHost()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        var receiver = new SkeletonGroupReceiver(bob, port, On);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.TestHook = async at =>
        {
            if (at == "hold:before-assemble" && reached.TrySetResult())
                await resume.Task;
        };

        var receiving = receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await receiver.DisposeAsync();
        resume.SetResult();
        var result = await receiving;

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Admission, result.Step);
        Assert.Empty(port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task AReceiverDisposedDuringTheRedrawReleasesInTheFullOrder()
    {
        var manifest = await PublishGroupAsync();
        using var bob = Client("bob");
        var receiver = new SkeletonGroupReceiver(bob, port, On);
        port.Redraw = Behaviour.Gate;

        var receiving = receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);
        await port.GateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await receiver.DisposeAsync();
        port.OpenGate(true);
        var result = await receiving;

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Redraw, result.Step);
        Assert.Equal(new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    /// <summary>A hold lost during the redraw (its client was disposed) is a failed step, released in the full order.</summary>
    [Fact]
    public async Task AHoldLostDuringTheRedrawIsAFailedStep()
    {
        var manifest = await PublishGroupAsync();
        var bob = Client("bob");
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        port.Entered = name =>
        {
            if (name == "redraw")
                bob.Dispose();
        };

        var result = await receiver.ReceiveAsync(ActorSlotKey.New(), manifest.BundleId, Hyur);

        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Redraw, result.Step);
        Assert.Equal(new[] { "assign", "redraw", "remove", "nothing" }, port.Calls);
        Assert.Equal(0, receiver.SlotCount);
        AssertNothingHeld(bob);
    }

    [Fact]
    public async Task TheConstructorRefusesABadCleanupBound()
    {
        using var bob = Client("bob");
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkeletonGroupReceiver(bob, port, On, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new SkeletonGroupReceiver(bob, null!, On));
        await using var receiver = new SkeletonGroupReceiver(bob, port, On);
        Assert.Equal(0, receiver.SlotCount);
    }

    // ---- Helpers ---------------------------------------------------------------

    public enum Behaviour
    {
        True,
        False,
        Throw,
        /// <summary>Waits until its token is cancelled.</summary>
        WaitForToken,
        /// <summary>Never finishes, whatever its token does.</summary>
        IgnoreToken,
        /// <summary>Waits for <see cref="FakeAssignmentPort.OpenGate"/>.</summary>
        Gate,
    }

    /// <summary>A host port that records each call in order and acts as told. No game, no IPC.</summary>
    private sealed class FakeAssignmentPort : ISkeletonGroupAssignmentPort
    {
        private readonly object gate = new();
        private readonly List<string> calls = [];
        private readonly List<bool> cleanupTokens = [];
        private readonly List<bool> holdIntactAtCleanup = [];
        private readonly Dictionary<ActorSlotKey, AppearanceBundleHold> assigned = [];
        private TaskCompletionSource<bool> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Behaviour Assign { get; set; }

        public Behaviour Redraw { get; set; }

        public Behaviour Remove { get; set; }

        public Behaviour NothingNames { get; set; }

        public ActorSlotKey? FailNothingNamesFor { get; set; }

        public Action<string>? Entered { get; set; }

        public bool HoldIntactAtAssign { get; private set; }

        public TaskCompletionSource GateReached { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls
        {
            get
            {
                lock (gate)
                    return [.. calls];
            }
        }

        /// <summary>Whether the slot's assigned hold was still intact at each Remove and NothingNamesFiles call: it must be, since disposal is last.</summary>
        public List<bool> HoldIntactAtCleanup
        {
            get
            {
                lock (gate)
                    return [.. holdIntactAtCleanup];
            }
        }

        public List<bool> CleanupTokensCancelledAtCall
        {
            get
            {
                lock (gate)
                    return [.. cleanupTokens];
            }
        }

        public void OpenGate(bool answer)
        {
            var current = opened;
            opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            GateReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            current.SetResult(answer);
        }

        public Task<bool> AssignAsync(ActorSlotKey slot, SkeletonResourceGroup group, AppearanceBundleHold hold, CancellationToken cancellationToken)
        {
            HoldIntactAtAssign = hold.IsIntact && ReferenceEquals(group, hold.Group);
            lock (gate)
                assigned[slot] = hold;
            return Act("assign", Assign, cancellationToken);
        }

        public Task<bool> RedrawAndConfirmAsync(ActorSlotKey slot, SkeletonResourceGroup group, CancellationToken cancellationToken) =>
            Act("redraw", Redraw, cancellationToken);

        public Task<bool> RemoveAsync(ActorSlotKey slot, CancellationToken cancellationToken) =>
            Act("remove", Remove, cancellationToken, slot);

        public Task<bool> NothingNamesFilesAsync(ActorSlotKey slot, CancellationToken cancellationToken) =>
            Act("nothing", FailNothingNamesFor == slot ? Behaviour.False : NothingNames, cancellationToken, slot);

        private Task<bool> Act(string name, Behaviour behaviour, CancellationToken cancellationToken, ActorSlotKey? cleanupOf = null)
        {
            lock (gate)
            {
                calls.Add(name);
                if (cleanupOf is { } slot)
                {
                    cleanupTokens.Add(cancellationToken.IsCancellationRequested);
                    holdIntactAtCleanup.Add(assigned.TryGetValue(slot, out var hold) && hold.IsIntact);
                }
            }
            Entered?.Invoke(name);
            switch (behaviour)
            {
                case Behaviour.True:
                    return Task.FromResult(true);
                case Behaviour.False:
                    return Task.FromResult(false);
                case Behaviour.Throw:
                    return Task.FromException<bool>(new InvalidOperationException("synthetic host failure"));
                case Behaviour.WaitForToken:
                    return WaitForTokenAsync(cancellationToken);
                case Behaviour.IgnoreToken:
                    return new TaskCompletionSource<bool>().Task;
                case Behaviour.Gate:
                    var answer = opened.Task;
                    GateReached.TrySetResult();
                    return answer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(behaviour));
            }
        }

        private static async Task<bool> WaitForTokenAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }
    }

    private static void AssertStayHiddenAtHold(SkeletonGroupReceiveResult result, AppearanceTransferStatus status)
    {
        Assert.Equal(SkeletonGroupReceiveStatus.StayHidden, result.Status);
        Assert.Equal(SkeletonGroupReceiveStep.Hold, result.Step);
        Assert.Equal(status, result.Transfer!.Status);
    }

    private Task<AppearanceBundleManifest> PublishGroupAsync() =>
        PublishAsync(Hyur, (BaseSklb, Sklb()), (BasePhyb, Phyb()));

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

    /// <summary>One deformer entry for Hyur, with a 4-byte body after the tables.</summary>
    private static byte[] Pbd()
    {
        var data = new byte[4 + 20 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Hyur);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 24);
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
}
