using System.Numerics;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class FurnitureStainPaletteTests
{
    private static readonly FurnitureStainColor Red = new(200, 30, 30, 255);

    [Fact]
    public void OnlyNamedHousingStainsBelowTheObjectStainCountWithANativeColourAreListed()
    {
        var asked = new List<byte>();
        var palette = FurnitureStainPalette.Build(
            [
                new(0, "Default", 0, true, 0),           // row 0 is never a stain
                new(1, "Snow White", 0xE4DFD0, true, 1001),
                new(2, "Not For Housing", 0x112233, false, 1002),
                new(3, "   ", 0x112233, true, 1003),       // no name
                new(4, "No Native Colour", 0x112233, true, 1004),
                new(5, "Rose Pink", 0xE6C6B9, true, 500),
                new(200, "Last Object Stain", 0x112233, true, 1500),
                new(201, "Beyond Count", 0x112233, true, 1),
                new(250, "Far Beyond", 0x112233, true, 1),
            ],
            objectStainCount: 0xC9,
            id =>
            {
                asked.Add(id);
                return id == 4 ? null : Red;
            });

        // Ordered by the game's dye order (shade, then sub-order).
        Assert.Equal(new byte[] { 5, 1, 200 }, palette.Entries.Select(entry => entry.Id));
        // The native lookup is asked only for rows that passed every other rule.
        Assert.Equal(new byte[] { 1, 4, 5, 200 }, asked.Order());
        Assert.True(palette.TryGet(1, out var white));
        Assert.Equal("Snow White", white.Name);
        Assert.Equal(0xE4DFD0u, white.Swatch);
        Assert.Equal(Red, white.Color);
        Assert.False(palette.TryGet(0, out _));
        Assert.False(palette.TryGet(2, out _));
        Assert.False(palette.TryGet(4, out _));
        Assert.True(palette.TryGet(200, out _));  // 0xC8, the last below ObjectStainCount (0xC9)
        Assert.False(palette.TryGet(201, out _));
    }

    [Fact]
    public void ANativeLookupFaultLeavesTheStainOut()
    {
        var palette = FurnitureStainPalette.Build(
            [new(1, "Snow White", 0, true, 0), new(2, "Soot Black", 0, true, 0)],
            0xC9,
            id => id == 1 ? throw new InvalidOperationException("native") : Red);

        Assert.Equal(new byte[] { 2 }, palette.Entries.Select(entry => entry.Id));
        Assert.Empty(FurnitureStainPalette.Empty.Entries);
    }
}

public sealed class FurnitureDyeHostTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool", DyeCount: 1);
    private static readonly FurnitureCatalogRow Plain = new(FurnitureSource.Indoor, 6002, 14, "Plain Stool");
    private static readonly FurnitureStainColor Native = new(10, 20, 30, 255);
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);

    [Fact]
    public void DefaultIsTheUndyedModelWithNoStainCallEvenOnAnUndyeableItem()
    {
        var (host, backend) = Host();

        Assert.Equal(ApplyPortStatus.Succeeded, host.SpawnOwned(Request(Stool, stain: 0)).Status);
        Assert.Equal(ApplyPortStatus.Succeeded, host.SpawnOwned(Request(Plain, stain: 0)).Status);

        Assert.Equal(2, backend.Live.Count);
        Assert.Empty(backend.StainCalls);
        Assert.All(backend.Live.Values, live => Assert.Null(live.Stain));
    }

    [Fact]
    public void DefaultDrawsTheItemWithItsOwnDefaultStainLikeTheGame()
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        assets.AddSingleModel(Plain);
        // The stool's layout names a default housing stain (as most dyeable
        // furniture does); the plain stool's does not.
        assets.DefaultStains[FurnitureModelPaths.Layout(Stool.Source, Stool.ModelKey)] = 12;
        var defaultColor = new FurnitureStainColor(150, 120, 90, 255);
        var asked = new List<byte>();
        var palette = new FurnitureStainPalette(
            [new FurnitureStainEntry(5, "Rose Pink", 0xE6C6B9, Native)],
            id => { asked.Add(id); return id == 12 ? defaultColor : null; },
            objectStainCount: 0xC9);
        var backend = new FakeFurnitureBackend { ChecksUntilLoaded = 0, StainChecksUntilReady = 1 };
        var host = new FurnitureLocalObjectHost(
            new FurnitureSupportResolver(new FurnitureCatalog([Stool, Plain]), assets), backend, palette);
        var request = Request(Stool, stain: 0);

        // Not black: the item's own default colour, and hidden until it is set.
        var spawned = host.SpawnOwned(request);
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        var live = Assert.Single(backend.Live).Value;
        Assert.False(live.Shown);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(spawned.Value, request.DesiredState).Status);
        Assert.True(live.Shown);
        Assert.Equal(defaultColor, live.Stain);
        Assert.Equal(new byte[] { 12 }, asked);

        // No default stain in the layout: the plain model, no stain call.
        var calls = backend.StainCalls.Count;
        Assert.Equal(ApplyPortStatus.Succeeded, host.SpawnOwned(Request(Plain, stain: 0)).Status);
        Assert.Equal(calls, backend.StainCalls.Count);
        Assert.Equal(new byte[] { 12 }, asked);
        Assert.Empty(host.Unsupported);
    }

    [Fact]
    public void ADefaultStainTheGameCannotResolveStillDrawsThePlainModel()
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        assets.DefaultStains[FurnitureModelPaths.Layout(Stool.Source, Stool.ModelKey)] = 12;
        var palette = new FurnitureStainPalette([], _ => null, objectStainCount: 0xC9);
        var backend = new FakeFurnitureBackend();
        var host = new FurnitureLocalObjectHost(
            new FurnitureSupportResolver(new FurnitureCatalog([Stool]), assets), backend, palette);

        Assert.Equal(ApplyPortStatus.Succeeded, host.SpawnOwned(Request(Stool, stain: 0)).Status);
        Assert.Empty(backend.StainCalls);
        Assert.True(Assert.Single(backend.Live).Value.Shown);
        Assert.False(palette.TryGetDefaultColor(0, out _));
        Assert.False(palette.TryGetDefaultColor(201, out _));
    }

    [Fact]
    public void ADyeIsAppliedOnlyAfterLoadAndStainBufferAndCountsAsAppliedThen()
    {
        var (host, backend) = Host();
        backend.ChecksUntilLoaded = 1;
        backend.StainChecksUntilReady = 2;
        var request = Request(Stool, stain: 5);

        var spawned = host.SpawnOwned(request);
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        Assert.Empty(backend.StainCalls); // not loaded yet: no stain call

        // Loaded, but the stain buffer is not ready: still awaiting, and the
        // instance stays hidden, so the dyed item is never seen undyed.
        var live = Assert.Single(backend.Live).Value;
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(spawned.Value, request.DesiredState).Status);
        Assert.True(live.Loaded);
        Assert.False(live.Shown);
        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(spawned.Value, request.DesiredState).Status);
        Assert.False(live.Shown);
        Assert.Equal(0, backend.Shows);
        Assert.False(host.IsDrawn(request.Token.ObjectId));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(spawned.Value, request.DesiredState).Status);
        Assert.True(live.Shown);
        Assert.Equal(1, backend.Shows);

        // The native housing colour, unchanged; never the sheet's display colour.
        Assert.Equal(Native, live.Stain);
        Assert.All(backend.StainCalls, call => Assert.Equal(Native, call.Color));
        Assert.True(host.IsDrawn(request.Token.ObjectId));

        // A later transform update does not set the stain again.
        var calls = backend.StainCalls.Count;
        var moved = (FurnitureState)request.DesiredState with { Position = new(5, 5, 5), Revision = new(2) };
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(spawned.Value, moved).Status);
        Assert.Equal(calls, backend.StainCalls.Count);
    }

    [Fact]
    public void AStainFaultPropagatesSoTheExactReceiptIsReleased()
    {
        var (host, backend) = Host();
        backend.ChecksUntilLoaded = 1;
        var request = Request(Stool, stain: 5);
        var spawned = host.SpawnOwned(request);
        backend.ThrowOnStain = true;

        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(spawned.Value, request.DesiredState));
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(spawned.Value).Status);
        Assert.Empty(backend.Live);
    }

    [Theory]
    [InlineData(99)]   // not a housing dye this client knows
    [InlineData(250)]
    public void AnUnknownDyeIsNotDrawnAtAllAndIsReported(int stain)
    {
        var (host, backend) = Host();

        var spawned = host.SpawnOwned(Request(Stool, (byte)stain));

        Assert.Equal(ApplyPortStatus.PermanentFailure, spawned.Status);
        Assert.False(spawned.Value.IsValid);
        Assert.Equal(0, backend.Created);
        Assert.Empty(backend.StainCalls);
        var report = Assert.Single(host.Unsupported);
        Assert.Equal(Stool.Identity, report.Identity);
        Assert.Contains("not drawn", report.Reason);
    }

    [Fact]
    public void ReportsArePerObjectAndClearedWhenThatObjectDrawsOrIsRemoved()
    {
        var (host, backend) = Host();
        var first = Request(Plain, stain: 5);
        var second = Request(Plain, stain: 5);
        var third = Request(Stool, stain: 99);

        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(first).Status);
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(second).Status);
        Assert.Equal(ApplyPortStatus.PermanentFailure, host.SpawnOwned(third).Status);
        // Two copies of the same item are two reports.
        Assert.Equal(
            new[] { first.Token.ObjectId, second.Token.ObjectId, third.Token.ObjectId },
            host.Unsupported.Select(report => report.ObjectId));

        // The first is reset to Default and draws: only its report goes.
        var reset = (FurnitureState)first.DesiredState with { Revision = new(2), Stain = 0 };
        var redrawn = host.SpawnOwned(new LocalObjectSpawnRequest(
            new LocalObjectApplyToken(Session, reset.ObjectId, new ApplyGeneration(2)),
            LocalObjectSpawnIdentity.FromState(reset), 2, reset));
        Assert.Equal(ApplyPortStatus.Succeeded, redrawn.Status);
        Assert.Equal(
            new[] { second.Token.ObjectId, third.Token.ObjectId },
            host.Unsupported.Select(report => report.ObjectId));

        // The third is deleted: its report goes; the second is still wanted.
        Assert.Equal(1, host.RetainUnsupported(id => id != third.Token.ObjectId));
        Assert.Equal(second.Token.ObjectId, Assert.Single(host.Unsupported).ObjectId);
        Assert.Single(backend.Live);
    }

    [Fact]
    public async Task AnInvalidDyeResetToDefaultDrawsAndClearsItsReport()
    {
        var (host, backend) = Host();
        var fence = new Fence();
        var reconciler = new LocalObjectReconciler(fence, new LocalObjectOwnershipLedger(4), new Dispatcher(), host,
            new Ready());
        var invalid = (FurnitureState)Request(Plain, stain: 5).DesiredState;

        var refused = await Apply(fence, reconciler, invalid, 1);
        Assert.Equal(LocalObjectReconcileStatus.PermanentFailure, refused.Status);
        Assert.Empty(backend.Live);
        Assert.Equal(invalid.ObjectId, Assert.Single(host.Unsupported).ObjectId);

        var reset = await Apply(fence, reconciler, invalid with { Revision = new(2), Stain = 0 }, 2);
        Assert.Equal(LocalObjectReconcileStatus.Applied, reset.Status);
        var live = Assert.Single(backend.Live).Value;
        Assert.True(live.Shown);
        Assert.Null(live.Stain);
        Assert.Empty(host.Unsupported);
    }

    [Fact]
    public void ADyeOnAnItemThatCannotBeDyedIsNotDrawnAtAll()
    {
        var (host, backend) = Host();

        var spawned = host.SpawnOwned(Request(Plain, stain: 5));

        Assert.Equal(ApplyPortStatus.PermanentFailure, spawned.Status);
        Assert.Equal(0, backend.Created);
        Assert.Contains("cannot be dyed", Assert.Single(host.Unsupported).Reason);
    }

    [Fact]
    public void TheStainIsASeparateTypedPartOfTheSpawnIdentity()
    {
        var state = (FurnitureState)Request(Stool, stain: 0).DesiredState;
        var dyed = state with { Stain = 5, Revision = new(2) };

        var undyedIdentity = LocalObjectSpawnIdentity.FromState(state);
        var dyedIdentity = LocalObjectSpawnIdentity.FromState(dyed);

        // The wire identity is untouched; only the typed stain differs.
        Assert.Equal(Stool.Identity, dyedIdentity.Value);
        Assert.Equal((byte)5, dyedIdentity.Stain);
        Assert.Equal((byte)0, undyedIdentity.Stain);
        Assert.NotEqual(undyedIdentity, dyedIdentity);
        Assert.Equal(dyedIdentity, LocalObjectSpawnIdentity.FromState(dyed with { Position = new(9, 9, 9) }));
        Assert.True(dyedIdentity.IsValid);
        Assert.False(new LocalObjectSpawnIdentity(RscObjectKind.Light, "lamp", Stain: 1).IsValid);
    }

    [Fact]
    public async Task AStainOnlyChangeReplacesTheExactOwnedInstance()
    {
        var (host, backend) = Host();
        var fence = new Fence();
        var reconciler = new LocalObjectReconciler(fence, new LocalObjectOwnershipLedger(4), new Dispatcher(), host,
            new Ready());
        var state = (FurnitureState)Request(Stool, stain: 0).DesiredState;

        var first = await Apply(fence, reconciler, state, 1);
        Assert.Equal(LocalObjectReconcileStatus.Applied, first.Status);
        var undyed = Assert.Single(backend.Live).Key;

        var dyed = state with { Revision = new(2), Stain = 5 };
        var second = await Apply(fence, reconciler, dyed, 2);
        Assert.Equal(LocalObjectReconcileStatus.Applied, second.Status);
        Assert.Contains(undyed, backend.Destroyed);
        var redrawn = Assert.Single(backend.Live);
        Assert.NotEqual(undyed, redrawn.Key);
        Assert.Equal(Native, redrawn.Value.Stain);
        Assert.NotEqual(first.Handle!.Value.OwnershipId, second.Handle!.Value.OwnershipId);

        // Default removes the dye with a fresh undyed instance.
        var third = await Apply(fence, reconciler, dyed with { Revision = new(3), Stain = 0 }, 3);
        Assert.Equal(LocalObjectReconcileStatus.Applied, third.Status);
        Assert.Contains(redrawn.Key, backend.Destroyed);
        Assert.Null(Assert.Single(backend.Live).Value.Stain);
        Assert.Equal(3, backend.Created);
    }

    private static Task<LocalObjectReconcileResult> Apply(
        Fence fence, LocalObjectReconciler reconciler, FurnitureState state, long generation)
    {
        var token = new LocalObjectApplyToken(Session, state.ObjectId, new ApplyGeneration(generation));
        fence.Current = token;
        return reconciler.ReconcileAsync(token, state, CancellationToken.None);
    }

    private sealed class Fence : ILocalObjectDesiredFence
    {
        internal LocalObjectApplyToken Current { get; set; }

        public bool IsCurrent(LocalObjectApplyToken token) => Current == token;

        public bool TryExecuteCurrent<T>(LocalObjectApplyToken token, Func<T> operation, out T result)
        {
            if (Current != token)
            {
                result = default!;
                return false;
            }
            result = operation();
            return true;
        }
    }

    private sealed class Dispatcher : IGameThreadDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
            Task.FromResult(operation());
    }

    private sealed class Ready : IReceiverReadinessPort
    {
        public bool IsReady => true;
    }

    private static (FurnitureLocalObjectHost Host, FakeFurnitureBackend Backend) Host()
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        assets.AddSingleModel(Plain);
        var backend = new FakeFurnitureBackend();
        var palette = new FurnitureStainPalette([new FurnitureStainEntry(5, "Rose Pink", 0xE6C6B9, Native)]);
        var host = new FurnitureLocalObjectHost(
            new FurnitureSupportResolver(new FurnitureCatalog([Stool, Plain]), assets), backend, palette);
        return (host, backend);
    }

    private static LocalObjectSpawnRequest Request(FurnitureCatalogRow row, byte stain)
    {
        var state = new FurnitureState(
            new RscObjectId(Guid.NewGuid()),
            new ObjectRevision(1),
            new RscUserId("a"),
            TestLocations.SharedScene,
            row.Identity,
            new Vector3State(1, 2, 3),
            new RotationState(0, 0, 0, 1)) { Location = TestLocations.Default, Stain = stain };
        return new(
            new LocalObjectApplyToken(Session, state.ObjectId, new ApplyGeneration(1)),
            LocalObjectSpawnIdentity.FromState(state),
            1,
            state);
    }
}

public sealed class FurnitureDyeControllerTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool", DyeCount: 2);
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);

    [Fact]
    public void PickingADyeSendsOneUpdateAndLocksAllEditingUntilTheEvent()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);

        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(state.Revision, update.BaseRevision);
        Assert.Equal(new FurnitureStainPatch(5), update.Patch);
        AssertLocked(fixture, state);

        // A successful reply alone is not authority: still saving.
        fixture.Accept(state);
        for (var frame = 0; frame < 3; frame++)
            fixture.Gizmo.Pump();
        AssertLocked(fixture, state);
        Assert.Single(fixture.Sent);

        // The event arrives: the authoritative state has a newer revision.
        fixture.Current = state with { Revision = new(2), Stain = 5 };
        fixture.Gizmo.Pump();
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Null(fixture.Gizmo.View.Message);
    }

    [Fact]
    public void AnInvalidDyeCanBeResetToDefault()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn() with { Stain = 99 };
        fixture.Gizmo.Select(state.ObjectId);

        Assert.True(fixture.Gizmo.ApplyStain(state, 0));

        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(new FurnitureStainPatch(0), update.Patch);
    }

    [Fact]
    public async Task PickingTheDyeItAlreadyHasSendsNothing()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn() with { };
        fixture.Gizmo.Select(state.ObjectId);

        Assert.False(fixture.Gizmo.ApplyStain(state, 0));
        Assert.False(fixture.Gizmo.ApplyStain(state with { Stain = 5 }, 5));
        Assert.Empty(fixture.Sent);
        Assert.True(fixture.Gizmo.View.CanEdit);
        // The editor refuses it too.
        Assert.Equal(ServerCommandStatus.Invalid, (await fixture.Editor.SetStainAsync(state, 0)).Status);
        Assert.Empty(fixture.Sent);
    }

    // A refused non-move change (here a dye change) reads
    // "Not changed. {text}", where the text is the command's own player text;
    // the reply's Detail (a path and an exception message) and the enum name
    // never appear.
    private const string SentinelDetail = @"C:\Users\sentinel-user\secret.json SentinelExceptionMessage-7f3a";

    [Theory]
    [InlineData(ProtocolRejectReason.NotOwner, "Not changed. Refused: only its owner can change it.")]
    [InlineData(ProtocolRejectReason.Deleted, "Not changed. Refused: it no longer exists.")]
    [InlineData(ProtocolRejectReason.InvalidPatch, "Not changed. Refused: the change is not valid.")]
    [InlineData(ProtocolRejectReason.None, "Not changed. Refused by the server.")]
    public void ARefusedDyeChangeReadsNotChangedWithTheSafeRefusalText(ProtocolRejectReason reason, string expected)
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: reason, Detail: SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Equal(expected, fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reason.ToString(), fixture.Gizmo.View.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Sent);
        Assert.Empty(fixture.Backend.Destroyed);
    }

    [Theory]
    [InlineData(ServerCommandStatus.Busy, "Not changed. Still sending earlier changes; try again.")]
    [InlineData((ServerCommandStatus)99, "Not changed. Refused by the server.")]
    public void ARefusedDyeChangeForAnotherStatusShowsNoStatusNameOrDetail(ServerCommandStatus status, string expected)
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Reply(new ServerCommandResult(status, Detail: SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.Equal(expected, fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("99", fixture.Gizmo.View.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Busy", fixture.Gizmo.View.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidDyeChangeShowsOnlyTheEditorsOwnReason()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Nothing to change."));
        fixture.Gizmo.Pump();

        Assert.Equal("Not changed. Nothing to change.", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void AFailedDyeCommandNeverShowsTheExceptionText()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Fail(new IOException(SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Equal("The connection dropped before the server answered; showing the shared state.", fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IOException", fixture.Gizmo.View.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleDyeChangeIsRefusedWithAMessage()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: ProtocolRejectReason.StaleRevision));
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Equal("Someone else changed it first; the dye was not changed.", fixture.Gizmo.View.Message);
        Assert.Single(fixture.Sent);
        // Nothing local changed: the drawn instance is untouched.
        Assert.Single(fixture.Backend.Live);
        Assert.Empty(fixture.Backend.Destroyed);
    }

    [Fact]
    public void ADroppedConnectionEndsTheSaveWithAMessage()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));
        fixture.Serial = 0;
        fixture.Gizmo.Pump();
        AssertLocked(fixture, state); // still in flight until it is answered

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.NotConnected));
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Contains("connection dropped", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void ChangingSelectionWhileSavingKeepsEverythingLocked()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        var other = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));

        fixture.Gizmo.Select(other.ObjectId);
        fixture.Gizmo.Pump();

        var view = fixture.Gizmo.View;
        Assert.Equal(other.ObjectId, view.Selected);
        Assert.Equal(state.ObjectId, view.Busy);
        Assert.False(view.CanEdit);
        Assert.False(fixture.Gizmo.ApplyStain(other, 5));
        Assert.False(fixture.Gizmo.ApplyTransform(other, new Vector3(9, 9, 9), Quaternion.Identity));
        Assert.Single(fixture.Sent);

        fixture.Accept(state);
        fixture.Current = state with { Revision = new(2), Stain = 5 };
        fixture.Gizmo.Pump();
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Equal(other.ObjectId, fixture.Gizmo.View.Selected);
    }

    [Fact]
    public void TheItemDisappearingWhileSavingEndsTheSave()
    {
        var fixture = new Fixture();
        var state = fixture.Spawn();
        fixture.Gizmo.Select(state.ObjectId);
        Assert.True(fixture.Gizmo.ApplyStain(state, 5));
        fixture.Accept(state);
        fixture.Gone = true;

        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
    }

    private static void AssertLocked(Fixture fixture, FurnitureState state)
    {
        var view = fixture.Gizmo.View;
        Assert.Equal(FurnitureGizmoPhase.Committing, view.Phase);
        Assert.False(view.CanEdit); // dye, transform fields, move to me, delete, place
        Assert.False(view.ShowsGizmoOn(state.ObjectId));
        Assert.False(fixture.Gizmo.ApplyStain(state, 7));
        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(9, 9, 9), Quaternion.Identity));
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
    }

    private sealed class Fixture
    {
        private TaskCompletionSource<ServerCommandResult> reply = new();

        internal Fixture()
        {
            var assets = new FakeFurnitureAssets();
            assets.AddSingleModel(Stool);
            Host = new FurnitureLocalObjectHost(new FurnitureSupportResolver(new FurnitureCatalog([Stool]), assets), Backend);
            Editor = new FurnitureEditor((request, _) =>
            {
                Sent.Add(request);
                reply = new TaskCompletionSource<ServerCommandResult>();
                return reply.Task;
            });
            Gizmo = new FurnitureGizmoController(Host, Editor, () => Serial,
                id => Gone ? null : Current is { } current && current.ObjectId == id ? current : Shown.GetValueOrDefault(id));
        }

        internal FakeFurnitureBackend Backend { get; } = new();
        internal FurnitureLocalObjectHost Host { get; }
        internal FurnitureEditor Editor { get; }
        internal FurnitureGizmoController Gizmo { get; }
        internal List<object> Sent { get; } = new();
        internal Dictionary<RscObjectId, FurnitureState> Shown { get; } = new();
        internal FurnitureState Current { get; set; }
        internal bool Gone { get; set; }
        internal long Serial { get; set; } = 1;

        internal FurnitureState Spawn()
        {
            var state = new FurnitureState(
                new RscObjectId(Guid.NewGuid()),
                new ObjectRevision(1),
                new RscUserId("a"),
                TestLocations.SharedScene,
                Stool.Identity,
                new Vector3State(1, 0, 1),
                new RotationState(0, 0, 0, 1)) { Location = TestLocations.Default };
            var request = new LocalObjectSpawnRequest(
                new LocalObjectApplyToken(Session, state.ObjectId, new ApplyGeneration(1)),
                LocalObjectSpawnIdentity.FromState(state),
                1,
                state);
            Assert.True(Host.SpawnOwned(request).Value.IsValid);
            Shown[state.ObjectId] = state;
            return state;
        }

        internal void Reply(ServerCommandResult result) => reply.SetResult(result);

        /// <summary>The command task itself fails (for example the transport threw).</summary>
        internal void Fail(Exception exception) => reply.SetException(exception);

        /// <summary>The server's (valid) completion for the stain update; not authority to apply.</summary>
        internal void Accept(FurnitureState basis)
        {
            var updated = basis with { Revision = new ObjectRevision(basis.Revision.Value + 1), Stain = 5 };
            var completion = new ObjectMutationCompletion(Guid.NewGuid(), ObjectMutationCompletionKind.Update, false,
                new ServerSequence(2), updated, null);
            Assert.True(completion.IsValid);
            Reply(new ServerCommandResult(ServerCommandStatus.Completed, completion));
        }
    }
}
