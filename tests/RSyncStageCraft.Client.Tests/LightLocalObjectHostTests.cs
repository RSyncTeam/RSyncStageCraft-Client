using System.Numerics;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Lights;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>Client-only point lights: records every native call.</summary>
internal sealed class FakeLightBackend : ILightSceneBackend
{
    private nint next = 0x9000;

    internal Dictionary<nint, LightInstance> Live { get; } = new();
    internal List<string> Events { get; } = new();
    internal int CreateCalls { get; private set; }
    internal int Created { get; private set; }
    internal int Applies { get; private set; }
    /// <summary>When set, every creation fails this way.</summary>
    internal LightCreateStatus? FailWith { get; set; }
    internal bool FaultNextApply { get; set; }

    public LightCreateResult Create()
    {
        CreateCalls++;
        if (FailWith is { } failure)
        {
            Events.Add($"create-{failure}");
            return new(failure, 0);
        }
        Created++;
        var instance = next;
        next += 0x10;
        Live[instance] = new LightInstance { Number = Created };
        Events.Add($"create:{Created}");
        return new(LightCreateStatus.Created, instance);
    }

    public void Apply(nint instance, NativeLightSettings settings)
    {
        var light = Live[instance];
        if (FaultNextApply)
        {
            FaultNextApply = false;
            Events.Add($"apply-fault:{light.Number}");
            throw new InvalidOperationException("simulated native fault");
        }
        Applies++;
        light.Settings = settings;
        Events.Add($"apply:{light.Number}");
    }

    public void Show(nint instance)
    {
        var light = Live[instance];
        Assert.NotNull(light.Settings); // shown only once fully applied
        Assert.False(light.Shown);
        light.Shown = true;
        Events.Add($"show:{light.Number}");
    }

    // The host swallows cleanup exceptions, so these record what happened in
    // Events (asserted by the tests) instead of asserting here.

    /// <summary>Light numbers whose Hide or CleanupRender throws.</summary>
    internal HashSet<int> FaultHide { get; } = new();
    internal HashSet<int> FaultCleanup { get; } = new();

    public void Hide(nint instance)
    {
        var light = Live[instance];
        if (FaultHide.Contains(light.Number))
        {
            Events.Add($"hide-fault:{light.Number}");
            throw new InvalidOperationException("simulated hide fault");
        }
        light.Shown = false;
        light.Hidden = true;
        Events.Add($"hide:{light.Number}");
    }

    public void CleanupRender(nint instance)
    {
        var light = Live[instance];
        if (FaultCleanup.Contains(light.Number))
        {
            Events.Add($"cleanup-fault:{light.Number}");
            throw new InvalidOperationException("simulated render cleanup fault");
        }
        light.CleanedUp = true;
        Events.Add(light.Hidden ? $"cleanup:{light.Number}" : $"cleanup-visible:{light.Number}");
    }

    public void Free(nint instance)
    {
        var light = Live[instance];
        Live.Remove(instance);
        Events.Add(light.CleanedUp ? $"free:{light.Number}" : $"free-uncleaned:{light.Number}");
    }

    /// <summary>The free sequences, one string per freed light.</summary>
    internal string[] Frees() => Events
        .Where(entry => entry.StartsWith("free", StringComparison.Ordinal))
        .ToArray();

    internal sealed class LightInstance
    {
        internal int Number { get; init; }
        internal NativeLightSettings? Settings { get; set; }
        internal bool Shown { get; set; }
        internal bool Hidden { get; set; }
        internal bool CleanedUp { get; set; }
    }
}

internal static class LightTestStates
{
    internal static readonly ReceiverSessionKey Session = new(1, 1, 1);

    internal static LightState Light(
        Vector3State position,
        float range = 8f,
        string identity = LightIdentity.Point) =>
        new(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"), TestLocations.SharedScene,
            identity, position, LightLimits.UnitScale) { Location = TestLocations.Default, Range = range };

    internal static LocalObjectSpawnRequest Request(AuthoritativeObjectState state, long generation = 1) => new(
        new LocalObjectApplyToken(Session, state.ObjectId, new ApplyGeneration(generation)),
        LocalObjectSpawnIdentity.FromState(state),
        1,
        state);
}

public sealed class LightLocalObjectHostTests
{
    private static LightState Light(Vector3State position, float range = 8f, string identity = LightIdentity.Point) =>
        LightTestStates.Light(position, range, identity);

    private static LocalObjectSpawnRequest Request(AuthoritativeObjectState state, long generation = 1) =>
        LightTestStates.Request(state, generation);

    [Fact]
    public void ALightIsCreatedHiddenFullyAppliedThenShown()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3)) with { Color = new LightColor(1f, 0.5f, 0f), Intensity = 2f };

        var spawned = host.SpawnOwned(Request(light));

        Assert.Equal(ApplyPortStatus.Succeeded, spawned.Status);
        Assert.Equal(RscObjectKind.Light, spawned.Value.Kind);
        Assert.Equal(light.ObjectId, spawned.Value.ObjectId);
        Assert.Equal(new[] { "create:1", "apply:1", "show:1" }, backend.Events);
        var native = Assert.Single(backend.Live).Value.Settings!.Value;
        Assert.Equal(new NativeLightSettings(new Vector3(1, 2, 3), new Vector3(6f, 1.5f, 0f), 2f, 8f), native);
        Assert.True(host.IsDrawn(light.ObjectId));
        Assert.Equal(1, host.OwnedCount);
        Assert.Empty(host.Unsupported);
    }

    [Fact]
    public void TheColourConversionIsSixTimesTheSquareAndIntensityIsAsIs()
    {
        Assert.Equal(new Vector3(6, 6, 6), LightColorConversion.ToNative(LightColor.White));
        Assert.Equal(new Vector3(6f * 0.25f, 6f * 0.0625f, 0f), LightColorConversion.ToNative(new LightColor(0.5f, 0.25f, 0f)));
        var state = Light(new(1, 2, 3)) with { Color = new LightColor(0.2f, 0.4f, 1f), Intensity = 3.5f, Range = 12f };
        var native = LightColorConversion.ToNative(state);
        Assert.Equal(new Vector3(1, 2, 3), native.Position);
        Assert.Equal(6f * 0.2f * 0.2f, native.Color.X, 5);
        Assert.Equal(6f * 0.4f * 0.4f, native.Color.Y, 5);
        Assert.Equal(6f, native.Color.Z, 5);
        Assert.Equal(3.5f, native.Intensity);
        Assert.Equal(12f, native.Range);
    }

    [Fact]
    public void NativeWritesHappenOnlyWhenSomethingChanged()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3));
        var handle = host.SpawnOwned(Request(light)).Value;
        Assert.Equal(1, backend.Applies);

        // The same state, or only a new revision: no native write.
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, light).Status);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, light with { Revision = new(2) }).Status);
        Assert.Equal(1, backend.Applies);

        // A new range: one write to the same light, never shown twice.
        Assert.Equal(ApplyPortStatus.Succeeded, host.ApplyOwned(handle, light with { Revision = new(3), Range = 20f }).Status);
        Assert.Equal(2, backend.Applies);
        Assert.Equal(20f, Assert.Single(backend.Live).Value.Settings!.Value.Range);
        Assert.Equal(1, backend.Created);
        Assert.Equal(new[] { "create:1", "apply:1", "show:1", "apply:1" }, backend.Events);
    }

    [Theory]
    [InlineData(LightCreateStatus.Failed)]
    [InlineData(LightCreateStatus.IncompleteFreed)]
    public void AFailedCreationOwnsNothingIsRetryingAndFailsAfterTheLastRetry(LightCreateStatus failure)
    {
        var backend = new FakeLightBackend { FailWith = failure };
        var host = new LightLocalObjectHost(backend);
        Assert.Equal(6, LightLocalObjectHost.DefaultCreationAttempts); // an initial attempt plus five retries
        var light = Light(new(1, 0, 1));
        var request = Request(light);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = host.SpawnOwned(request);
            Assert.Equal(ApplyPortStatus.TransientFailure, result.Status);
            Assert.False(result.Value.IsValid);
            var report = Assert.Single(host.Unsupported);
            Assert.Equal(LocalObjectReportKind.Retrying, report.Kind);
            Assert.Equal("Could not create the light (retrying).", report.Reason);
            Assert.Equal("Point light", report.Name);
        }
        Assert.Equal(ApplyPortStatus.TransientFailure, host.SpawnOwned(request).Status);
        var failed = Assert.Single(host.Unsupported);
        Assert.Equal(LocalObjectReportKind.Failed, failed.Kind);
        Assert.Equal("Could not create the light; retries exhausted.", failed.Reason);
        Assert.Equal(6, backend.CreateCalls);
        Assert.Equal(0, host.OwnedCount);
        Assert.Empty(backend.Live);
        Assert.False(host.IsDrawn(light.ObjectId));

        // A fresh apply generation (a new desired state, a reconnect, a
        // domain reset) starts a fresh budget.
        Assert.Equal(ApplyPortStatus.TransientFailure, host.SpawnOwned(Request(light, generation: 2)).Status);
        Assert.Equal(LocalObjectReportKind.Retrying, Assert.Single(host.Unsupported).Kind);

        // Drawing it clears its status.
        backend.FailWith = null;
        Assert.Equal(ApplyPortStatus.Succeeded, host.SpawnOwned(Request(light, generation: 3)).Status);
        Assert.Empty(host.Unsupported);
        Assert.True(host.IsDrawn(light.ObjectId));
    }

    [Fact]
    public void ClearingAndRetainingReportsAlsoResetTheBudget()
    {
        var backend = new FakeLightBackend { FailWith = LightCreateStatus.Failed };
        var host = new LightLocalObjectHost(backend, creationAttempts: 2);
        var light = Light(new(1, 0, 1));
        host.SpawnOwned(Request(light));
        host.SpawnOwned(Request(light));
        Assert.Equal(LocalObjectReportKind.Failed, Assert.Single(host.Unsupported).Kind);

        host.ClearUnsupported();
        Assert.Empty(host.Unsupported);
        host.SpawnOwned(Request(light));
        Assert.Equal(LocalObjectReportKind.Retrying, Assert.Single(host.Unsupported).Kind);

        // A removed object's status goes away.
        Assert.Equal(1, host.RetainUnsupported(_ => false));
        Assert.Empty(host.Unsupported);
    }

    [Theory]
    [InlineData("light:spot", 8f)]
    [InlineData("light:point", 100f)]
    public void AnUnsupportedIdentityOrOutOfBoundsStateIsNeverDrawn(string identity, float range)
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(0, 0, 0), range, identity);

        var result = host.SpawnOwned(Request(light));

        Assert.Equal(ApplyPortStatus.PermanentFailure, result.Status);
        Assert.Equal(0, backend.CreateCalls);
        var report = Assert.Single(host.Unsupported);
        Assert.Equal(LocalObjectReportKind.NotSupported, report.Kind);
        Assert.Equal(light.ObjectId, report.ObjectId);
        Assert.Equal(0, host.OwnedCount);
    }

    [Fact]
    public void AFaultWhileApplyingPropagatesAndReleaseFreesTheExactLight()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3));
        var handle = host.SpawnOwned(Request(light)).Value;

        backend.FaultNextApply = true;
        Assert.Throws<InvalidOperationException>(() => host.ApplyOwned(handle, light with { Revision = new(2), Range = 10f }));

        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
        Assert.Empty(backend.Live);
        Assert.Equal(new[] { "hide:1", "cleanup:1", "free:1" }, backend.Events.TakeLast(3));
        Assert.Equal(0, host.OwnedCount);
        // Releasing again is harmless.
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(handle).Status);
    }

    [Fact]
    public void ALightWhoseFirstApplyFaultedBeforeShowIsStillCleanedUpBeforeItIsFreed()
    {
        var backend = new FakeLightBackend { FaultNextApply = true };
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3));

        // Created (with its render part), then the first apply faults: never shown.
        var spawned = host.SpawnOwned(Request(light));
        Assert.Equal(ApplyPortStatus.TransientFailure, spawned.Status);
        Assert.True(spawned.Value.IsValid); // the receipt is returned so it can be released
        Assert.False(Assert.Single(backend.Live).Value.Shown);
        Assert.False(host.IsDrawn(light.ObjectId));

        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(spawned.Value).Status);

        // Hide, render cleanup, then free: cleanup is not skipped for a light
        // that was never shown.
        Assert.Equal(new[] { "create:1", "apply-fault:1", "hide:1", "cleanup:1", "free:1" }, backend.Events);
        Assert.Empty(backend.Live);

        // The same on a zone wipe.
        backend.FaultNextApply = true;
        var second = host.SpawnOwned(Request(Light(new(4, 5, 6))));
        Assert.Equal(ApplyPortStatus.TransientFailure, second.Status);
        Assert.Equal(1, host.DestroyAll());
        Assert.Equal(new[] { "create:2", "apply-fault:2", "hide:2", "cleanup:2", "free:2" }, backend.Events.Skip(5));
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("cleanup")]
    public void ACleanupStepFaultStillRunsTheLaterStepsAndTheOtherLights(string faulting)
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        host.SpawnOwned(Request(Light(new(1, 0, 0))));
        host.SpawnOwned(Request(Light(new(2, 0, 0))));
        host.SpawnOwned(Request(Light(new(3, 0, 0))));
        (faulting == "hide" ? backend.FaultHide : backend.FaultCleanup).Add(2);

        // A zone wipe: every light is released even though one step faults.
        Assert.Equal(3, host.DestroyAll());

        Assert.Empty(backend.Live); // nothing stranded: Free ran for all three
        Assert.Equal(0, host.OwnedCount);
        var second = backend.Events.Where(entry => entry.EndsWith(":2", StringComparison.Ordinal)).TakeLast(3).ToArray();
        Assert.Equal(faulting == "hide"
                ? new[] { "hide-fault:2", "cleanup-visible:2", "free:2" }
                : new[] { "hide:2", "cleanup-fault:2", "free-uncleaned:2" },
            second);
        foreach (var other in new[] { 1, 3 })
            Assert.Equal(new[] { $"hide:{other}", $"cleanup:{other}", $"free:{other}" },
                backend.Events.Where(entry => entry.EndsWith($":{other}", StringComparison.Ordinal)).TakeLast(3));
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("cleanup")]
    public void AReleaseWithACleanupStepFaultStillFreesTheLight(string faulting)
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var handle = host.SpawnOwned(Request(Light(new(1, 0, 0)))).Value;
        (faulting == "hide" ? backend.FaultHide : backend.FaultCleanup).Add(1);

        var released = host.ReleaseOwned(handle);

        Assert.Equal(ApplyPortStatus.Succeeded, released.Status);
        Assert.NotNull(released.Error); // reported, not hidden
        Assert.Empty(backend.Live);
        Assert.StartsWith("free", backend.Events[^1]);

        // Shutdown with a faulting step frees the others too.
        var other = new FakeLightBackend();
        var closing = new LightLocalObjectHost(other);
        closing.SpawnOwned(Request(Light(new(1, 0, 0))));
        closing.SpawnOwned(Request(Light(new(2, 0, 0))));
        (faulting == "hide" ? other.FaultHide : other.FaultCleanup).Add(1);
        Assert.Equal(2, closing.Shutdown());
        Assert.Empty(other.Live);
    }

    [Fact]
    public void AFaultDuringAPreviewRetiresTheExactLight()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3));
        var other = Light(new(5, 5, 5));
        host.SpawnOwned(Request(light));
        host.SpawnOwned(Request(other));
        var retired = 0;
        host.InstanceRetired += () => retired++;

        var begin = host.BeginPreview(light);
        Assert.True(begin.Started);
        backend.FaultNextApply = true;
        Assert.False(host.MovePreview(begin.Id, new Vector3(2, 2, 3), Quaternion.Identity));

        Assert.Equal(1, retired);
        Assert.Equal(FurniturePreviewStatus.Faulted, host.PreviewStatus(begin.Id));
        Assert.False(host.IsDrawn(light.ObjectId));
        Assert.True(host.IsDrawn(other.ObjectId));
        Assert.Equal(new[] { "free:1" }, backend.Frees());
        Assert.Equal(new[] { "hide:1", "cleanup:1", "free:1" },
            backend.Events.Where(entry => entry.EndsWith(":1", StringComparison.Ordinal)).TakeLast(3));
    }

    [Fact]
    public void APreviewMovesOnlyThePositionAndIsRestoredOnCancel()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var light = Light(new(1, 2, 3), range: 12f);
        host.SpawnOwned(Request(light));
        var live = Assert.Single(backend.Live).Value;

        var begin = host.BeginPreview(light);
        Assert.True(host.MovePreview(begin.Id, new Vector3(4, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)));
        Assert.Equal(new Vector3(4, 2, 3), live.Settings!.Value.Position);
        Assert.Equal(12f, live.Settings!.Value.Range);

        host.CancelPreview(begin.Id);
        Assert.Equal(new Vector3(1, 2, 3), live.Settings!.Value.Position);
        Assert.Equal(FurniturePreviewStatus.Cancelled, host.PreviewStatus(begin.Id));
    }

    [Fact]
    public void ReleaseZoneWipeAndShutdownFreeEveryLightInOrder()
    {
        var backend = new FakeLightBackend();
        var host = new LightLocalObjectHost(backend);
        var first = host.SpawnOwned(Request(Light(new(1, 0, 0)))).Value;
        host.SpawnOwned(Request(Light(new(2, 0, 0))));
        host.SpawnOwned(Request(Light(new(3, 0, 0))));

        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(first).Status);
        Assert.Equal(2, host.DestroyAll());
        Assert.Empty(backend.Live);
        Assert.Equal(
            new[] { "free:1", "free:2", "free:3" },
            backend.Frees().Order());

        host.SpawnOwned(Request(Light(new(4, 0, 0))));
        Assert.Equal(1, host.Shutdown());
        Assert.True(host.IsShutDown);
        Assert.Empty(backend.Live);
        Assert.Equal(ApplyPortStatus.Unavailable, host.SpawnOwned(Request(Light(new(5, 0, 0)))).Status);
        Assert.Equal(4, backend.Created);
    }
}

public sealed class LocalObjectHostTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool");

    private static (LocalObjectHost Host, FakeFurnitureBackend Furniture, FakeLightBackend Lights) Create()
    {
        var assets = new FakeFurnitureAssets();
        assets.AddSingleModel(Stool);
        var furnitureBackend = new FakeFurnitureBackend { ChecksUntilLoaded = 0 };
        var lightBackend = new FakeLightBackend();
        var host = new LocalObjectHost(
            new FurnitureLocalObjectHost(new FurnitureSupportResolver(new FurnitureCatalog([Stool]), assets), furnitureBackend),
            new LightLocalObjectHost(lightBackend));
        return (host, furnitureBackend, lightBackend);
    }

    private static FurnitureState Furniture(string identity) => new(
        new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"), TestLocations.SharedScene,
        identity, new Vector3State(1, 0, 1), new RotationState(0, 0, 0, 1)) { Location = TestLocations.Default };

    [Fact]
    public void ObjectsAreDispatchedByKindToTheirOwnHost()
    {
        var (host, furnitureBackend, lightBackend) = Create();
        var stool = Furniture(Stool.Identity);
        var light = LightTestStates.Light(new(2, 0, 2));

        var stoolHandle = host.SpawnOwned(LightTestStates.Request(stool)).Value;
        var lightHandle = host.SpawnOwned(LightTestStates.Request(light)).Value;

        Assert.Equal(RscObjectKind.Furniture, stoolHandle.Kind);
        Assert.Equal(RscObjectKind.Light, lightHandle.Kind);
        Assert.Equal(1, furnitureBackend.Created);
        Assert.Equal(1, lightBackend.Created);
        Assert.Equal(1, host.Furniture.OwnedCount);
        Assert.Equal(1, host.Lights.OwnedCount);
        Assert.Equal(2, host.OwnedCount);
        Assert.True(host.IsDrawn(stool.ObjectId));
        Assert.True(host.IsDrawn(light.ObjectId));

        // Apply and release go by the receipt's kind.
        Assert.Equal(ApplyPortStatus.Succeeded,
            host.ApplyOwned(lightHandle, light with { Revision = new(2), Range = 4f }).Status);
        Assert.Equal(4f, Assert.Single(lightBackend.Live).Value.Settings!.Value.Range);
        Assert.Equal(ApplyPortStatus.Succeeded, host.ReleaseOwned(stoolHandle).Status);
        Assert.Empty(furnitureBackend.Live);
        Assert.Single(lightBackend.Live);

        Assert.Equal(1, host.DestroyAll());
        Assert.Empty(lightBackend.Live);
    }

    [Fact]
    public void ReportsFromBothHostsAreListedAndClearedTogether()
    {
        var (host, _, lightBackend) = Create();
        lightBackend.FailWith = LightCreateStatus.Failed;
        var unknown = Furniture("furniture:outdoor:42");
        var light = LightTestStates.Light(new(2, 0, 2));

        host.SpawnOwned(LightTestStates.Request(unknown));
        host.SpawnOwned(LightTestStates.Request(light));

        Assert.Equal(new[] { unknown.ObjectId, light.ObjectId }, host.Unsupported.Select(report => report.ObjectId));
        Assert.Equal(new[] { LocalObjectReportKind.NotSupported, LocalObjectReportKind.Retrying },
            host.Unsupported.Select(report => report.Kind));
        host.ClearUnsupported();
        Assert.Empty(host.Unsupported);
    }

    [Fact]
    public void PreviewsGoToTheHostThatDrawsTheObjectAndOnlyOneRunsAtATime()
    {
        var (host, furnitureBackend, lightBackend) = Create();
        var stool = Furniture(Stool.Identity);
        var light = LightTestStates.Light(new(2, 0, 2));
        host.SpawnOwned(LightTestStates.Request(stool));
        host.SpawnOwned(LightTestStates.Request(light));

        var lightPreview = host.BeginPreview(light);
        Assert.True(lightPreview.Started);
        Assert.Equal(FurniturePreviewBeginStatus.Busy, host.BeginPreview(stool).Status);
        Assert.True(host.MovePreview(lightPreview.Id, new Vector3(3, 0, 2), Quaternion.Identity));
        Assert.Equal(new Vector3(3, 0, 2), Assert.Single(lightBackend.Live).Value.Settings!.Value.Position);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(furnitureBackend.Live).Value.Position);

        var end = host.EndPreview(lightPreview.Id);
        Assert.True(end.Changed);
        Assert.Same(light, end.Basis);
        host.CompletePreview(lightPreview.Id);
        Assert.Equal(FurniturePreviewStatus.Finished, host.PreviewStatus(lightPreview.Id));

        var stoolPreview = host.BeginPreview(stool);
        Assert.True(stoolPreview.Started);
        host.CancelPreview(stoolPreview.Id);
        Assert.Equal(FurniturePreviewStatus.Cancelled, host.PreviewStatus(stoolPreview.Id));
    }
}

public sealed class LightDraftTests
{
    private static LightState Light() =>
        LightTestStates.Light(new(1, 2, 3)) with { Color = new LightColor(1f, 0.5f, 0f), Intensity = 2f, Range = 10f };

    [Fact]
    public void AnUntouchedDraftOrValuesWithinRoundingChangeNothing()
    {
        var draft = new LightDraft();
        var light = Light();
        draft.Sync(light, locked: false);
        Assert.False(draft.IsDirty);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);

        draft.Edit(new Vector3(1.0004f, 2, 3), new LightColor(1f, 0.5004f, 0f), 2.004f, 10.004f);
        Assert.True(draft.IsDirty);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void OnlyChangedFieldsGoIntoOneAtomicPatch()
    {
        var draft = new LightDraft();
        var light = Light();
        draft.Sync(light, locked: false);

        draft.Edit(new Vector3(1, 2, 3), new LightColor(1f, 0.5f, 0f), 2f, 24f);
        var range = draft.TryApply();
        Assert.Equal(FurnitureDraftApplyStatus.Ready, range.Status);
        Assert.Same(light, range.Basis);
        Assert.Equal(new LightPointPatch(null, null, null, 24f), range.Patch);

        draft.Sync(light, locked: false);
        draft.Edit(new Vector3(4, 2, 3), new LightColor(0f, 0f, 1f), 3f, 10f);
        var combined = draft.TryApply();
        Assert.Equal(new LightPointPatch(new Vector3State(4, 2, 3), new LightColor(0f, 0f, 1f), 3f, null), combined.Patch);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 2f, 10f, "The colour must not be black")]
    [InlineData(1f, 1f, 1f, 17f, 10f, "Brightness must be between")]
    [InlineData(1f, 1f, 1f, 0.01f, 10f, "Brightness must be between")]
    [InlineData(1f, 1f, 1f, 2f, 65f, "Range must be between")]
    [InlineData(1f, 1f, 1f, 2f, 0.2f, "Range must be between")]
    [InlineData(1f, 1f, 1f, float.NaN, 10f, "Enter numbers only.")]
    public void OutOfBoundsValuesAreRefusedBeforeAnythingIsSent(float r, float g, float b, float intensity, float range, string message)
    {
        var draft = new LightDraft();
        draft.Sync(Light(), locked: false);
        draft.Edit(new Vector3(1, 2, 3), new LightColor(r, g, b), intensity, range);

        var result = draft.TryApply();

        Assert.Equal(FurnitureDraftApplyStatus.Invalid, result.Status);
        Assert.StartsWith(message, result.Message);
        Assert.Null(result.Patch);
        Assert.True(draft.IsDirty); // the typed values stay to be corrected
    }

    [Fact]
    public void AChangeFromSomeoneElseWinsOverTheDraft()
    {
        var draft = new LightDraft();
        var light = Light();
        draft.Sync(light, locked: false);
        draft.Edit(new Vector3(1, 2, 3), light.Color, light.Intensity, 30f);

        // A newer revision arrives while the draft is dirty: kept until Apply.
        var newer = light with { Revision = new(2), Range = 5f };
        draft.Sync(newer, locked: false);
        Assert.Equal(30f, draft.Range);

        var result = draft.TryApply();
        Assert.Equal(FurnitureDraftApplyStatus.Stale, result.Status);
        Assert.Null(result.Patch);
        Assert.Equal(5f, draft.Range);
        Assert.False(draft.IsDirty);

        // Not dirty: the shown values follow the shared state.
        draft.Sync(newer with { Revision = new(3), Range = 7f }, locked: false);
        Assert.Equal(7f, draft.Range);
        // Another light selected: its values load.
        var other = LightTestStates.Light(new(9, 9, 9));
        draft.Sync(other, locked: false);
        Assert.Equal(other.ObjectId, draft.ObjectId);
        Assert.Equal(new Vector3(9, 9, 9), draft.Position);
    }
}

public sealed class LightGizmoControllerTests
{
    [Fact]
    public void ApplyingTheLightFieldsSendsOneUpdateAndLocksEditingUntilTheEvent()
    {
        var fixture = new Fixture();
        var light = fixture.Spawn();
        fixture.Gizmo.Select(light.ObjectId);

        Assert.True(fixture.Gizmo.ApplyLight(light, new LightPointPatch(null, new LightColor(0f, 0f, 1f), null, 20f)));

        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(light.Revision, update.BaseRevision);
        Assert.Equal(new LightPointPatch(null, new LightColor(0f, 0f, 1f), null, 20f), update.Patch);
        Assert.False(fixture.Gizmo.View.CanEdit);
        Assert.False(fixture.Gizmo.ApplyLight(light, new LightPointPatch(null, null, null, 30f)));

        // A reply alone is not authority; the event (a newer revision) ends it.
        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Completed, Completion(light with
        {
            Revision = new(2), Color = new LightColor(0f, 0f, 1f), Range = 20f,
        })));
        fixture.Gizmo.Pump();
        Assert.False(fixture.Gizmo.View.CanEdit);
        fixture.Current = light with { Revision = new(2), Range = 20f };
        fixture.Gizmo.Pump();
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Null(fixture.Gizmo.View.Message);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public void AStaleLightChangeIsRefusedWithAMessage()
    {
        var fixture = new Fixture();
        var light = fixture.Spawn();
        fixture.Gizmo.Select(light.ObjectId);
        Assert.True(fixture.Gizmo.ApplyLight(light, new LightPointPatch(null, null, 4f, null)));

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: ProtocolRejectReason.StaleRevision));
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Equal("Someone else changed it first; the light was not changed.", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void AnEmptyOrInvalidPatchSendsNothing()
    {
        var fixture = new Fixture();
        var light = fixture.Spawn();
        fixture.Gizmo.Select(light.ObjectId);

        Assert.False(fixture.Gizmo.ApplyLight(light, new LightPointPatch(null, null, null, null)));
        Assert.False(fixture.Gizmo.ApplyLight(light, new LightPointPatch(null, null, null, 100f)));
        Assert.Empty(fixture.Sent);
        Assert.True(fixture.Gizmo.View.CanEdit);
    }

    [Fact]
    public void DraggingALightOnlyMovesItAndSendsOnePositionUpdate()
    {
        var fixture = new Fixture();
        var light = fixture.Spawn();
        fixture.Gizmo.Select(light.ObjectId);
        fixture.Gizmo.SetMode(FurnitureGizmoMode.Rotate); // a light still only moves

        fixture.Gizmo.BeginDrag(light);
        fixture.Gizmo.Drag(new Vector3(4, 0, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f));
        fixture.Gizmo.Pump();
        Assert.Equal(new Vector3(4, 0, 1), Assert.Single(fixture.Backend.Live).Value.Settings!.Value.Position);
        Assert.Empty(fixture.Sent);

        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(new LightPointPatch(new Vector3State(4, 0, 1), null, null, null), update.Patch);
    }

    private static ObjectMutationCompletion Completion(LightState updated)
    {
        var completion = new ObjectMutationCompletion(Guid.NewGuid(), ObjectMutationCompletionKind.Update, false,
            new ServerSequence(2), updated, null);
        Assert.True(completion.IsValid);
        return completion;
    }

    private sealed class Fixture
    {
        private TaskCompletionSource<ServerCommandResult> reply = new();

        internal Fixture()
        {
            Host = new LightLocalObjectHost(Backend);
            Editor = new FurnitureEditor((request, _) =>
            {
                Sent.Add(request);
                reply = new TaskCompletionSource<ServerCommandResult>();
                return reply.Task;
            });
            Gizmo = new FurnitureGizmoController(Host, Editor, () => 1,
                id => Current is { } current && current.ObjectId == id ? current : Shown.GetValueOrDefault(id));
        }

        internal FakeLightBackend Backend { get; } = new();
        internal LightLocalObjectHost Host { get; }
        internal FurnitureEditor Editor { get; }
        internal FurnitureGizmoController Gizmo { get; }
        internal List<object> Sent { get; } = new();
        internal Dictionary<RscObjectId, LightState> Shown { get; } = new();
        internal LightState Current { get; set; }

        internal LightState Spawn()
        {
            var light = LightTestStates.Light(new(1, 0, 1));
            Assert.True(Host.SpawnOwned(LightTestStates.Request(light)).Value.IsValid);
            Shown[light.ObjectId] = light;
            return light;
        }

        internal void Reply(ServerCommandResult result) => reply.SetResult(result);
    }
}

public sealed class LightRangeOutlineTests
{
    private static GizmoCamera Camera()
    {
        Assert.True(GizmoCameraMath.TryPrepare(
            Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 100f),
            0.1f, 100f, false, new Vector2(10, 20), new Vector2(100, 100), out var camera));
        return camera;
    }

    [Fact]
    public void ThePointInFrontOfTheCameraProjectsAndOneBehindItDoesNot()
    {
        var camera = Camera();
        Assert.True(GizmoCameraMath.TryProject(camera, Vector3.Zero, out var center));
        Assert.Equal(60f, center.X, 2);
        Assert.Equal(70f, center.Y, 2);
        Assert.True(GizmoCameraMath.TryProject(camera, new Vector3(0, 1, 0), out var above));
        Assert.True(above.Y < center.Y); // up is up on screen

        Assert.False(GizmoCameraMath.TryProject(camera, new Vector3(0, 0, 10), out _));
        Assert.False(GizmoCameraMath.TryProject(camera, new Vector3(float.NaN, 0, 0), out _));
    }

    [Fact]
    public void TheOutlineIsThreeCirclesOfTheRange()
    {
        var center = new Vector3(1, 2, 3);
        var segments = GizmoCameraMath.RangeOutline(center, 8f);
        Assert.Equal(3 * GizmoCameraMath.OutlineSegments, segments.Count);
        Assert.All(segments, segment =>
        {
            Assert.Equal(8f, Vector3.Distance(center, segment.From), 3);
            Assert.Equal(8f, Vector3.Distance(center, segment.To), 3);
        });
        Assert.Empty(GizmoCameraMath.RangeOutline(center, 0f));
        Assert.Empty(GizmoCameraMath.RangeOutline(center, float.NaN));
        Assert.Empty(GizmoCameraMath.RangeOutline(new Vector3(float.NaN, 0, 0), 8f));
    }

    [Fact]
    public void NoOutlineWithoutAValidCamera()
    {
        // The outline is drawn only with a camera that passed the gizmo's checks.
        Assert.False(GizmoCameraMath.TryPrepare(Matrix4x4.Identity, new Matrix4x4(), 0.1f, 100f, false,
            Vector2.Zero, new Vector2(100, 100), out _));
        Assert.False(GizmoCameraMath.TryPrepare(Matrix4x4.Identity, Matrix4x4.Identity, float.NaN, 100f, false,
            Vector2.Zero, new Vector2(100, 100), out _));
        Assert.False(GizmoCameraMath.TryPrepare(Matrix4x4.Identity, Matrix4x4.Identity, 0.1f, 100f, false,
            Vector2.Zero, Vector2.Zero, out _));
    }
}
