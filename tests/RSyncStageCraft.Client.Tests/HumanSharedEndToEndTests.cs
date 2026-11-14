#if RSC_DEVELOPMENT
using RSyncStageCraft.Core.Actors.Humans;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// HV-6, the client half of the synthetic sharing run, minus the game: two
/// synthetic clients (Player A and Player B), each with its own appearance
/// cache and its own receiver intake, against one in-memory appearance
/// service. A shares with the plugin's own publisher; the server's broadcast
/// (create, move, delete, exactly as the server half
/// HumanSharingEndToEndTests receives it) reaches both intakes; both
/// download and prepare the same appearance with the cache-backed
/// preparation; the move keeps the bundle; the removal lets both release
/// every hold; A unshares only once the reference is gone. Then the
/// cache-backed preparation's refusals, each of which holds nothing.
/// </summary>
public sealed class HumanSharedEndToEndTests : IDisposable
{
    private static readonly RscUserId PlayerA = new("player-a");
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "rsc-hv6-e2e", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(path);
        folders.Add(Path.GetDirectoryName(path)!);
        return path;
    }

    private sealed record Client(string Account, AppearanceBlobCache Cache, AppearanceTransferClient Transfer, RoutingFixture Intake) : IDisposable
    {
        public void Dispose() => Transfer.Dispose();
    }

    private Client NewClient(FakeAppearanceService service, string account)
    {
        var cache = new AppearanceBlobCache(Folder(account + "-cache"));
        return new(account, cache, new AppearanceTransferClient(() => service.Settings(account), cache, null, service), new RoutingFixture());
    }

    private static async Task<HumanCatalogueSpawnReady> ReceiveAndPrepareAsync(Client client, HumanActorState human, HumanCatalogueSpawnRead current)
    {
        Assert.True(ContentHash.TryParse(human.AppearanceBundleId, out var id));
        var download = await client.Transfer.DownloadBundleAsync(id, CancellationToken.None, AppearanceManifestReadOptions.AllowHumanPackages);
        var manifest = download.Manifest;
        Assert.True(manifest is not null, download.Result.Status.ToString());
        Assert.Equal(human.OwnerId, manifest!.OwnerId);
        var (ready, refusal) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, client.Cache, current, CancellationToken.None);
        Assert.True(ready is not null, refusal);
        return ready!;
    }

    private static void Broadcast(object message, params Client[] clients)
    {
        foreach (var client in clients)
        {
            var result = message switch
            {
                ObjectCreatedMessage created => client.Intake.Events.AcceptCreated(client.Intake.Session, created),
                ObjectUpdatedMessage updated => client.Intake.Events.AcceptUpdated(client.Intake.Session, updated),
                ObjectDeletedMessage deleted => client.Intake.Events.AcceptDeleted(client.Intake.Session, deleted),
                _ => throw new ArgumentOutOfRangeException(nameof(message)),
            };
            Assert.True(result.Accepted, $"{client.Account}: {result.Status}");
        }
    }

    [Fact]
    public async Task ASharedHumanIsPreparedIdenticallyByBothClientsMovedRemovedReleasedAndUnshared()
    {
        var fixture = HumanCatalogueFixture.Create(swap: false);
        folders.Add(fixture.Folder);
        var store = new HumanCatalogueStore(fixture.Root);
        var entry = store.FindEntry(HumanCatalogueFixture.Label, out var unknown) ?? throw new InvalidOperationException(unknown);
        var service = new FakeAppearanceService { AcceptHumanPackages = true };
        using var a = NewClient(service, PlayerA.Value);
        using var b = NewClient(service, "player-b");
        foreach (var client in new[] { a, b })
            Assert.True(client.Intake.Events.InitializeFromSnapshotWatermark(client.Intake.Session, new ServerSequence(0)));

        // Share (A): the package is built from A's entry and published; B can read it.
        var package = HumanSharedPackageBuilder.Build(entry, PlayerA, withProfile: false, HumanCatalogueFixture.Versions, out var refusal);
        Assert.True(package is not null, refusal);
        var (shared, line) = await HumanSharedPublisher.ShareAsync(package!, store, a.Cache, a.Transfer, CancellationToken.None);
        Assert.True(shared, line);
        Assert.Contains("published to the group", line, StringComparison.Ordinal);
        Assert.Equal(0, a.Cache.Usage.InUseEntries);
        Assert.True(!Directory.Exists(a.Cache.TemporaryDirectory) || !Directory.EnumerateFiles(a.Cache.TemporaryDirectory).Any(),
            "a temporary member file was left behind");
        var id = package!.Manifest.BundleId;

        // Place (A), as the server broadcasts it to both players.
        var scene = new RscSceneId(Guid.NewGuid());
        var placed = new HumanActorState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), PlayerA, scene, id.Value, new Vector3State(1, 2, 3), 0.5f);
        service.SetReferenced(id, held: true);
        Broadcast(new ObjectCreatedMessage(new ServerSequence(1), placed), a, b);

        // Inspect on both clients: the same human, the same prepared appearance as A's own entry.
        var current = fixture.Current();
        var ready = new List<HumanCatalogueSpawnReady>();
        foreach (var client in new[] { a, b })
            ready.Add(await ReceiveAndPrepareAsync(client, Assert.Single(client.Intake.Humans.Read().Humans), current));
        foreach (var prepared in ready)
        {
            Assert.Equal(HumanCatalogueEntry.SharedFormat, prepared.Entry.Format);
            Assert.Equal((entry.Race, entry.Tribe, entry.Sex, entry.BodyType), (prepared.Entry.Race, prepared.Entry.Tribe, prepared.Entry.Sex, prepared.Entry.BodyType));
            Assert.Equal(entry.GlamourerState, prepared.Entry.GlamourerState);
            Assert.Equal(entry.Manipulations, prepared.Entry.Manipulations);
            Assert.Equal(entry.Paths.Select(path => (path.GamePath, path.Blob)).Order(), prepared.Entry.Paths.Select(path => (path.GamePath, path.Blob)).Order());
            Assert.Equal(HumanCatalogueSpawnPreparation.PhaseOneMembers(entry).Order(), prepared.PhaseOneRequired.Order());
            Assert.Equal(entry.Skeleton?.Deformer ?? false, prepared.Deformer);
            Assert.All(prepared.Holds, hold => Assert.True(File.Exists(hold.FilePath)));
        }
        Assert.Equal(ready[0].Holds.Select(hold => hold.Hash).Order(), ready[1].Holds.Select(hold => hold.Hash).Order());
        Assert.Equal(ready[0].Package.Paths.Keys.Order(StringComparer.OrdinalIgnoreCase), ready[1].Package.Paths.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(ready[0].Holds.Count, a.Cache.Usage.InUseEntries);
        Assert.Equal(ready[1].Holds.Count, b.Cache.Usage.InUseEntries);

        // Unshare is refused while the human is placed.
        var (unshared, refusedLine) = await HumanSharedPublisher.UnshareAsync(id, a.Transfer, CancellationToken.None);
        Assert.False(unshared);
        Assert.Contains("remove-shared first", refusedLine, StringComparison.Ordinal);

        // Move (A): both follow the new revision; the bundle is unchanged, so nothing is prepared again.
        var moved = placed with { Revision = new ObjectRevision(2), Position = new Vector3State(4, 5, 6), Yaw = 1f };
        Broadcast(new ObjectUpdatedMessage(new ServerSequence(2), moved), a, b);
        foreach (var client in new[] { a, b })
        {
            var held = Assert.Single(client.Intake.Humans.Read().Humans);
            Assert.Equal(moved, held);
            Assert.Equal(id.Value, held.AppearanceBundleId);
        }

        // Remove (A): both desired sets empty; both release every hold.
        Broadcast(new ObjectDeletedMessage(new ServerSequence(3), new ObjectTombstone(placed.ObjectId, RscObjectKind.Human, new ObjectRevision(3))), a, b);
        service.SetReferenced(id, held: false);
        foreach (var client in new[] { a, b })
            Assert.Empty(client.Intake.Humans.Read().Humans);
        foreach (var prepared in ready)
            prepared.Abandon();
        Assert.Equal(0, a.Cache.Usage.InUseEntries);
        Assert.Equal(0, b.Cache.Usage.InUseEntries);

        // Unshare (A): unpublished and deleted; B can no longer download it.
        var (done, doneLine) = await HumanSharedPublisher.UnshareAsync(id, a.Transfer, CancellationToken.None);
        Assert.True(done, doneLine);
        Assert.Null((await b.Transfer.DownloadBundleAsync(id, CancellationToken.None, AppearanceManifestReadOptions.AllowHumanPackages)).Manifest);

        // Disconnect: the session ends, and nothing is desired any more.
        foreach (var client in new[] { a, b })
        {
            Assert.True(client.Intake.Humans.ClearSession(client.Intake.Session));
            Assert.Null(client.Intake.Humans.Read().Session);
        }
    }

    [Fact]
    public async Task ThePreparationsRefusalsHoldNothing()
    {
        var fixture = HumanCatalogueFixture.Create(swap: false);
        folders.Add(fixture.Folder);
        var store = new HumanCatalogueStore(fixture.Root);
        var entry = store.FindEntry(HumanCatalogueFixture.Label, out var unknown) ?? throw new InvalidOperationException(unknown);
        var service = new FakeAppearanceService { AcceptHumanPackages = true };
        using var a = NewClient(service, PlayerA.Value);
        using var b = NewClient(service, "player-b");
        var package = HumanSharedPackageBuilder.Build(entry, PlayerA, false, HumanCatalogueFixture.Versions, out _)!;
        Assert.True((await HumanSharedPublisher.ShareAsync(package, store, a.Cache, a.Transfer, CancellationToken.None)).Shared);
        var manifest = package.Manifest;

        // Nothing downloaded yet: every blob is missing from B's cache.
        var (none, missing) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, b.Cache, fixture.Current(), CancellationToken.None);
        Assert.Null(none);
        Assert.Contains("missing from", missing, StringComparison.Ordinal);
        Assert.Equal(0, b.Cache.Usage.InUseEntries);

        Assert.NotNull((await b.Transfer.DownloadBundleAsync(manifest.BundleId, CancellationToken.None, AppearanceManifestReadOptions.AllowHumanPackages)).Manifest);

        // Another recorded version on the receiver: refused (D7), nothing held.
        var otherVersions = new HumanCatalogueSpawnRead(fixture.RecordedVersions with { Penumbra = "0.0.0.1" }, fixture.Mods, fixture.Resources);
        var (stale, version) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, b.Cache, otherVersions, CancellationToken.None);
        Assert.Null(stale);
        Assert.NotNull(version);
        Assert.Equal(0, b.Cache.Usage.InUseEntries);

        // A receiver whose deformer differs: refused (SD-5), nothing held.
        var otherDeformer = fixture.Current(resources => resources
            .Select(resource => resource.GamePaths.Contains(FakeHumanActorPenumbra.Deformer)
                ? new AppearanceResourceEntry(fixture.WriteMod("Skeletons/other.pbd", HumanCatalogueFixture.Pbd([101, 201])), resource.GamePaths)
                : resource)
            .ToArray());
        var (mismatch, deformer) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, b.Cache, otherDeformer, CancellationToken.None);
        Assert.Null(mismatch);
        Assert.NotNull(deformer);
        Assert.Equal(0, b.Cache.Usage.InUseEntries);

        // Not a human package: refused before anything is held.
        var plain = AppearanceBundleManifest.TryCreate(PlayerA, manifest.Glamourer, null, null, [], out var plainError);
        Assert.True(plain is not null, plainError);
        Assert.Equal(1, plain!.FormatVersion);
        Assert.Null((await HumanSharedSpawnPreparation.PrepareAsync(plain, b.Cache, fixture.Current(), CancellationToken.None)).Ready);
        Assert.Equal(0, b.Cache.Usage.InUseEntries);

        // The same receiver with matching versions and deformer prepares it; abandoning releases every hold.
        var (ready, refusal) = await HumanSharedSpawnPreparation.PrepareAsync(manifest, b.Cache, fixture.Current(), CancellationToken.None);
        Assert.True(ready is not null, refusal);
        Assert.NotEqual(0, b.Cache.Usage.InUseEntries);
        ready!.Abandon();
        Assert.Equal(0, b.Cache.Usage.InUseEntries);
    }
}
#endif
