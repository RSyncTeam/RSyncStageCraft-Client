using System.Numerics;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class FurnitureLocalObjectHostTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool");
    private static readonly FurnitureCatalogRow Lamp = new(FurnitureSource.Indoor, 6001, 13, "Lamp");
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);

    [Fact]
    public void CreatedIsNotAppliedUntilTheModelHasLoaded()
    {
        var (host, backend) = Host(checksUntilLoaded: 2);
        var request = Request("furniture:indoor:6000", new(1, 2, 3));

        var spawned = host.SpawnOwned(request);

        // Created and owned, but not success: the model is still loading.
        Assert.Equal(ApplyPortStatus.AwaitingPayload, spawned.Status);
        var handle = spawned.Value;
        Assert.True(handle.IsValid);
        Assert.Equal(request.Token.ObjectId, handle.ObjectId);
        Assert.Equal(RscObjectKind.Furniture, handle.Kind);
        Assert.Equal(request.SpawnGeneration, handle.SpawnGeneration);
        Assert.Equal(1, host.OwnedCount);
        var instance = Assert.Single(backend.Live).Value;
        Assert.Equal(FurnitureModelPaths.MainModel(FurnitureSource.Indoor, 12), instance.ModelPath);
        Assert.False(instance.Shown);

        Assert.Equal(ApplyPortStatus.AwaitingPayload, host.ApplyOwned(handle, request.DesiredState).Status);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, request.DesiredState).Status);
        Assert.True(instance.Shown);
        Assert.Equal(new Vector3(1, 2, 3), instance.Position);
    }

    [Fact]
    public void LoadedModelAppliesAtOnceAndUpdatesMoveTheExactInstance()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);
        var request = Request("furniture:indoor:6000", new(1, 2, 3));
        var spawned = host.SpawnOwned(request);
        Assert.Equal(ApplyPortStatus.Succeeded, spawned.Status);

        var moved = (FurnitureState)request.DesiredState with
        {
            Position = new(4, 5, 6),
            Rotation = FurnitureEditor.Yaw(MathF.PI / 2),
            Revision = new ObjectRevision(2),
        };
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(spawned.Value, moved).Status);

        var instance = Assert.Single(backend.Live).Value;
        Assert.Equal(new Vector3(4, 5, 6), instance.Position);
        Assert.Equal(90, FurnitureEditor.YawDegrees(moved.Rotation), 3);
        Assert.Equal(1, backend.Created);
    }

    [Fact]
    public void UnsupportedItemsAreReportedAndNeverCreated()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);

        var lampRequest = Request("furniture:indoor:6001", new(0, 0, 0));
        var lamp = host.SpawnOwned(lampRequest);
        var unknown = host.SpawnOwned(Request("furniture:outdoor:42", new(0, 0, 0)));
        // A retry for the same object counts on its one report.
        var again = host.SpawnOwned(lampRequest);

        Assert.Equal(ApplyPortStatus.PermanentFailure, lamp.Status);
        Assert.False(lamp.Value.IsValid);
        Assert.Contains("Multipart", lamp.Error);
        Assert.Equal(ApplyPortStatus.PermanentFailure, unknown.Status);
        Assert.Equal(ApplyPortStatus.PermanentFailure, again.Status);
        Assert.Equal(0, backend.Created);
        Assert.Equal(0, host.OwnedCount);

        Assert.Collection(
            host.Unsupported,
            report =>
            {
                Assert.Equal("furniture:outdoor:42", report.Identity);
                Assert.Equal("Unknown item on this client", report.Reason);
            },
            report =>
            {
                Assert.Equal("furniture:indoor:6001", report.Identity);
                Assert.Equal(lampRequest.Token.ObjectId, report.ObjectId);
                Assert.Equal("Lamp", report.Name);
                Assert.Equal(2, report.Count);
            });
        host.ClearUnsupported();
        Assert.Empty(host.Unsupported);
    }

    [Fact]
    public void LightsAreNotDrawnBeforeM4()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);
        var objectId = new RscObjectId(Guid.NewGuid());
        var light = new LightState(objectId, new ObjectRevision(1), new RscUserId("a"), TestLocations.SharedScene,
            "light:point", new(0, 0, 0), new(1, 1, 1)) { Location = TestLocations.Default };
        var request = new LocalObjectSpawnRequest(
            new LocalObjectApplyToken(Session, objectId, new ApplyGeneration(1)),
            LocalObjectSpawnIdentity.FromState(light),
            1,
            light);

        var result = host.SpawnOwned(request);

        Assert.Equal(ApplyPortStatus.PermanentFailure, result.Status);
        Assert.Equal(0, backend.Created);
        Assert.Contains("Lights are not drawn", Assert.Single(host.Unsupported).Reason);
    }

    [Fact]
    public void FailedCreateOwnsNothing()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);
        backend.FailCreate = true;

        var result = host.SpawnOwned(Request("furniture:indoor:6000", new(0, 0, 0)));

        Assert.Equal(ApplyPortStatus.TransientFailure, result.Status);
        Assert.False(result.Value.IsValid);
        Assert.Equal(0, host.OwnedCount);
    }

    [Fact]
    public void ReleaseFreesOnlyTheExactReceiptAndIsIdempotent()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);
        var first = host.SpawnOwned(Request("furniture:indoor:6000", new(0, 0, 0))).Value;
        var second = host.SpawnOwned(Request("furniture:indoor:6000", new(1, 0, 0))).Value;

        // A receipt with the right ownership ID but another generation is not ours to free.
        Assert.True(host.ReleaseOwned(first with { SpawnGeneration = 99 }).Succeeded);
        Assert.Equal(2, host.OwnedCount);

        Assert.True(host.ReleaseOwned(first).Succeeded);
        Assert.True(host.ReleaseOwned(first).Succeeded);
        Assert.Equal(1, host.OwnedCount);
        Assert.Single(backend.Destroyed);
        Assert.Single(backend.Live);
        Assert.Equal(1, ((ILocalObjectRuntimeOwnershipDiagnostics)host).PendingOwnershipCount);
        Assert.True(host.ReleaseOwned(second).Succeeded);
        Assert.Empty(backend.Live);
    }

    [Fact]
    public void LocationChangeWipeLeavesReceiptsHarmless()
    {
        var (host, backend) = Host(checksUntilLoaded: 5);
        var request = Request("furniture:indoor:6000", new(0, 0, 0));
        var handle = host.SpawnOwned(request).Value;

        Assert.Equal(1, host.DestroyAll());

        Assert.Empty(backend.Live);
        // A load poll of a wiped instance fails, so the reconciler releases it.
        Assert.Equal(ApplyPortStatus.TransientFailure, host.ApplyOwned(handle, request.DesiredState).Status);
        Assert.True(host.ReleaseOwned(handle).Succeeded);
        Assert.Single(backend.Destroyed);
    }

    [Fact]
    public void ShutdownFreesEverythingAndRefusesLateSpawns()
    {
        var (host, backend) = Host(checksUntilLoaded: 0);
        host.SpawnOwned(Request("furniture:indoor:6000", new(0, 0, 0)));

        Assert.Equal(1, host.Shutdown());
        var late = host.SpawnOwned(Request("furniture:indoor:6000", new(0, 0, 0)));

        Assert.Equal(ApplyPortStatus.Unavailable, late.Status);
        Assert.Empty(backend.Live);
        Assert.Equal(1, backend.Created);
    }

    [Fact]
    public void BackendFaultWhileApplyingReachesTheReconciler()
    {
        var (host, backend) = Host(checksUntilLoaded: 3);
        var request = Request("furniture:indoor:6000", new(0, 0, 0));
        var handle = host.SpawnOwned(request).Value;
        backend.ThrowOnCheck = true;

        // Not converted into an ordinary failure: the reconciler must see the
        // fault so it releases this exact, now uncertain, instance.
        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle, request.DesiredState));
        Assert.Equal(1, host.OwnedCount);
        Assert.True(host.ReleaseOwned(handle).Succeeded);
        Assert.Empty(backend.Live);
    }

    [Fact]
    public void MismatchedDesiredStateIsRefused()
    {
        var (host, _) = Host(checksUntilLoaded: 0);
        var handle = host.SpawnOwned(Request("furniture:indoor:6000", new(0, 0, 0))).Value;
        var other = Request("furniture:indoor:6000", new(0, 0, 0)).DesiredState;

        Assert.Equal(ApplyPortStatus.PermanentFailure, host.ApplyOwned(handle, other).Status);
    }

    private static (FurnitureLocalObjectHost Host, FakeFurnitureBackend Backend) Host(int checksUntilLoaded)
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        // The lamp has a light besides its model.
        var lampModel = FurnitureModelPaths.MainModel(Lamp.Source, Lamp.ModelKey);
        assets.Files.Add(lampModel);
        assets.Layouts[FurnitureModelPaths.Layout(Lamp.Source, Lamp.ModelKey)] =
            [FakeFurnitureAssets.Model(lampModel), FakeFurnitureAssets.Other()];
        var backend = new FakeFurnitureBackend { ChecksUntilLoaded = checksUntilLoaded };
        var resolver = new FurnitureSupportResolver(new FurnitureCatalog([Stool, Lamp]), assets);
        return (new FurnitureLocalObjectHost(resolver, backend), backend);
    }

    private static LocalObjectSpawnRequest Request(string identity, Vector3State position)
    {
        var objectId = new RscObjectId(Guid.NewGuid());
        var state = new FurnitureState(
            objectId,
            new ObjectRevision(1),
            new RscUserId("a"),
            TestLocations.SharedScene,
            identity,
            position,
            new RotationState(0, 0, 0, 1)) { Location = TestLocations.Default };
        return new LocalObjectSpawnRequest(
            new LocalObjectApplyToken(Session, objectId, new ApplyGeneration(1)),
            LocalObjectSpawnIdentity.FromState(state),
            1,
            state);
    }
}
