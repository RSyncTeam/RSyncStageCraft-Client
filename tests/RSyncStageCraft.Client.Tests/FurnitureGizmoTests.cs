using System.Numerics;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

public sealed class FurnitureTransformsTests
{
    [Fact]
    public void YawOnlyRotationKeepsAnyTiltAndIgnoresGizmoPitchAndRoll()
    {
        var tilted = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var gizmo = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0.7f, -0.4f);

        var result = FurnitureTransforms.WithYawOf(tilted, gizmo);

        Assert.Equal(FurnitureTransforms.Yaw(gizmo), FurnitureTransforms.Yaw(result), 3);
        // Only a turn around the vertical axis was applied to the original.
        var turn = result * Quaternion.Inverse(tilted);
        var axis = Vector3.Normalize(new Vector3(turn.X, turn.Y, turn.Z));
        Assert.Equal(1, MathF.Abs(axis.Y), 3);
    }

    [Fact]
    public void InvalidTransformsAreRefused()
    {
        Assert.False(FurnitureTransforms.IsValidPosition(new Vector3(float.NaN, 0, 0)));
        Assert.False(FurnitureTransforms.IsValidPosition(new Vector3(0, float.PositiveInfinity, 0)));
        Assert.False(FurnitureTransforms.IsValidPosition(new Vector3(0, 0, 1e9f)));
        Assert.True(FurnitureTransforms.IsValidPosition(new Vector3(1, 2, 3)));
        Assert.False(FurnitureTransforms.TryNormalize(new Quaternion(0, 0, 0, 0), out _));
        Assert.False(FurnitureTransforms.TryNormalize(new Quaternion(float.NaN, 0, 0, 1), out _));
        Assert.True(FurnitureTransforms.TryNormalize(new Quaternion(0, 2, 0, 0), out var normalized));
        Assert.Equal(1, normalized.Length(), 4);
    }

    [Fact]
    public void TinyDifferencesAreTheSameTransform()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1);
        Assert.True(FurnitureTransforms.SameTransform(Vector3.One, rotation, Vector3.One + new Vector3(1e-6f), rotation));
        Assert.False(FurnitureTransforms.SameTransform(Vector3.One, rotation, Vector3.One + new Vector3(0.01f, 0, 0), rotation));
        Assert.False(FurnitureTransforms.SameTransform(Vector3.One, rotation, Vector3.One,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.01f)));
    }
}

public sealed class GizmoCameraMathTests
{
    private static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(new Vector3(0, 2, 5), Vector3.Zero, Vector3.UnitY);
    private static readonly Matrix4x4 Projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 16f / 9, 0.1f, 1000);

    [Fact]
    public void ValidCameraIsPreparedWithTheNearFarCorrection()
    {
        Assert.True(GizmoCameraMath.TryPrepare(View, Projection, 0.1f, 1000, false,
            new Vector2(0, 0), new Vector2(1920, 1080), out var camera));

        var clip = 1000f / (1000f - 0.1f);
        Assert.Equal(-(clip * 0.1f), camera.Projection.M43, 5);
        Assert.Equal(-((1000f + 0.1f) / (1000f - 0.1f)), camera.Projection.M33, 5);
        Assert.Equal(1, camera.View.M44);
        Assert.Equal(new Vector2(1920, 1080), camera.ViewportSize);
        Assert.False(camera.Orthographic);
    }

    [Theory]
    [InlineData(0f, 1000f, 1920f, 1080f)]
    [InlineData(0.1f, 0.1f, 1920f, 1080f)]
    [InlineData(10f, 1f, 1920f, 1080f)]
    [InlineData(float.NaN, 1000f, 1920f, 1080f)]
    [InlineData(0.1f, float.PositiveInfinity, 1920f, 1080f)]
    [InlineData(0.1f, 1000f, 0f, 1080f)]
    [InlineData(0.1f, 1000f, 1920f, float.NaN)]
    public void MissingOrInvalidCameraStateDrawsNothing(float near, float far, float width, float height)
    {
        Assert.False(GizmoCameraMath.TryPrepare(View, Projection, near, far, false,
            Vector2.Zero, new Vector2(width, height), out _));
    }

    [Fact]
    public void NonFiniteOrEmptyMatricesDrawNothing()
    {
        var broken = View;
        broken.M22 = float.NaN;
        Assert.False(GizmoCameraMath.TryPrepare(broken, Projection, 0.1f, 1000, false, Vector2.Zero, new Vector2(10, 10), out _));
        Assert.False(GizmoCameraMath.TryPrepare(default, default, 0.1f, 1000, false, Vector2.Zero, new Vector2(10, 10), out _));
    }

    [Fact]
    public void GizmoMatricesRoundTripWithoutScale()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f);
        var matrix = GizmoCameraMath.Compose(new Vector3(1, 2, 3), rotation) * Matrix4x4.CreateScale(1);

        Assert.True(GizmoCameraMath.TryDecompose(matrix, out var position, out var decoded));
        Assert.Equal(new Vector3(1, 2, 3), position);
        Assert.True(FurnitureTransforms.AngleBetween(rotation, decoded) < 1e-3f);

        var bad = matrix;
        bad.M41 = float.NaN;
        Assert.False(GizmoCameraMath.TryDecompose(bad, out _, out _));
    }
}

public sealed class FurnitureGizmoTests
{
    private static readonly FurnitureCatalogRow Stool = new(FurnitureSource.Indoor, 6000, 12, "Wooden Stool");
    private static readonly ReceiverSessionKey Session = new(1, 1, 1);

    [Fact]
    public void DragMovesTheExistingInstanceAndSendsExactlyOneUpdateOnRelease()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var instance = Assert.Single(fixture.Backend.Live).Value;

        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.BeginDrag(state);
        for (var step = 1; step <= 5; step++)
        {
            fixture.Gizmo.Drag(new Vector3(1 + step, 0, 1), Quaternion.Identity);
            fixture.Gizmo.Pump();
            Assert.Equal(new Vector3(1 + step, 0, 1), instance.Position);
        }

        // Zero updates during the drag; the same instance moved.
        Assert.Empty(fixture.Sent);
        Assert.Equal(1, fixture.Backend.Created);
        Assert.Empty(fixture.Backend.Destroyed);
        Assert.Equal(FurnitureGizmoPhase.Dragging, fixture.Gizmo.View.Phase);

        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(state.ObjectId, update.ObjectId);
        Assert.Equal(state.Revision, update.BaseRevision);
        var patch = Assert.IsType<FurnitureTransformPatch>(update.Patch);
        Assert.Equal(new Vector3State(6, 0, 1), patch.Position);
        Assert.Null(patch.Rotation);

        fixture.Accept(state, new Vector3State(6, 0, 1));
        fixture.Gizmo.Pump();
        // A reply is not authority: still saving until the event is applied.
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        fixture.ApplyAuthoritative(state, new Vector3State(6, 0, 1));
        fixture.Gizmo.Pump();
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Null(fixture.Gizmo.View.Message);
        Assert.Single(fixture.Sent);
        Assert.Equal(new Vector3(6, 0, 1), instance.Position);
    }

    [Fact]
    public void ASuccessfulReplyAloneNeverEndsThePreview()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var instance = Assert.Single(fixture.Backend.Live).Value;
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));

        fixture.Accept(state, new Vector3State(4, 0, 1));
        for (var frame = 0; frame < 5; frame++)
            fixture.Gizmo.Pump();

        // The desired store still has the old revision: keep showing the
        // preview, and allow no second drag from the stale revision.
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(4, 0, 1), instance.Position);
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();
        Assert.Single(fixture.Sent);

        fixture.ApplyAuthoritative(state, new Vector3State(4, 0, 1));
        fixture.Gizmo.Pump();
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(4, 0, 1), instance.Position);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("mode")]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    [InlineData("reconnect")]
    [InlineData("location")]
    [InlineData("host-loss")]
    [InlineData("unload")]
    public void InterruptionsAfterReleaseDoNotCallTheUpdateBack(string interruption)
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        var other = fixture.Spawn(new Vector3State(5, 0, 5)).State;
        var instance = fixture.Backend.Live.Values.First(live => live.Position == new Vector3(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(3, 0, 1));
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        var released = interruption is "location" or "host-loss" or "unload";

        switch (interruption)
        {
            case "selection": fixture.Gizmo.Select(other.ObjectId); break;
            case "mode": fixture.Gizmo.SetMode(FurnitureGizmoMode.Rotate); break;
            case "close": fixture.Gizmo.Select(null); break;
            case "cancel": fixture.Gizmo.Cancel("window closed"); break;
            case "disconnect": fixture.Serial = 0; break;
            case "reconnect": fixture.Serial = 2; break;
            case "location": fixture.Gizmo.Cancel("moved"); fixture.Host.DestroyAll(); break;
            case "host-loss": fixture.Host.ReleaseOwned(handle); break;
            case "unload": fixture.Gizmo.Cancel(); fixture.Host.Shutdown(); break;
        }
        var writes = fixture.Backend.TransformWrites;
        fixture.Gizmo.Pump();

        // Still waiting for the outcome; nothing restored or reported discarded.
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        Assert.Equal(writes, fixture.Backend.TransformWrites);
        Assert.Null(fixture.Gizmo.View.Message);
        if (!released)
            Assert.Equal(new Vector3(3, 0, 1), instance.Position);

        fixture.Accept(state, new Vector3State(3, 0, 1));
        fixture.Gizmo.Pump();
        if (!released)
        {
            Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
            fixture.ApplyAuthoritative(state, new Vector3State(3, 0, 1));
            fixture.Gizmo.Pump();
            Assert.Equal(new Vector3(3, 0, 1), instance.Position);
        }
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Null(fixture.Gizmo.View.Message);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public void SelectingAnotherItemWhileSavingKeepsTheFirstLockedAndItsPreviewItsOwn()
    {
        var fixture = new Fixture();
        var (a, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var (b, _) = fixture.Spawn(new Vector3State(5, 0, 5));
        var instanceA = fixture.Backend.Live.Values.Single(live => live.Position == new Vector3(1, 0, 1));
        var instanceB = fixture.Backend.Live.Values.Single(live => live.Position == new Vector3(5, 0, 5));
        fixture.Gizmo.Select(a.ObjectId);
        fixture.Drag(a, new Vector3(3, 0, 1));

        fixture.Gizmo.Select(b.ObjectId);
        fixture.Gizmo.Pump();
        var view = fixture.Gizmo.View;

        // B is selected, but the pending update and its preview belong to A.
        Assert.Equal(b.ObjectId, view.Selected);
        Assert.Equal(a.ObjectId, view.Busy);
        Assert.Equal(FurnitureGizmoPhase.Committing, view.Phase);
        Assert.False(view.CanEdit);
        Assert.False(view.ShowsGizmoOn(b.ObjectId));
        Assert.False(view.ShowsGizmoOn(a.ObjectId));

        // No drag of B can start, and B never receives A's preview transform.
        fixture.Gizmo.BeginDrag(b);
        fixture.Gizmo.Drag(new Vector3(9, 0, 9), Quaternion.Identity);
        fixture.Gizmo.EndDrag();
        for (var frame = 0; frame < 3; frame++)
            fixture.Gizmo.Pump();
        Assert.Equal(new Vector3(5, 0, 5), instanceB.Position);
        Assert.Equal(new Vector3(3, 0, 1), instanceA.Position);
        Assert.Single(fixture.Sent);

        // Once A's update is authoritative, editing is open again.
        fixture.Accept(a, new Vector3State(3, 0, 1));
        fixture.ApplyAuthoritative(a, new Vector3State(3, 0, 1));
        fixture.Gizmo.Pump();
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.True(fixture.Gizmo.View.ShowsGizmoOn(b.ObjectId));
        Assert.Null(fixture.Gizmo.View.Busy);
    }

    [Fact]
    public void WhileDraggingOnlyTheDraggedItemShowsTheGizmo()
    {
        var fixture = new Fixture();
        var (a, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var (b, _) = fixture.Spawn(new Vector3State(5, 0, 5));
        fixture.Gizmo.Select(a.ObjectId);
        fixture.Gizmo.BeginDrag(a);
        fixture.Gizmo.Pump();

        Assert.True(fixture.Gizmo.View.ShowsGizmoOn(a.ObjectId));
        Assert.False(fixture.Gizmo.View.ShowsGizmoOn(b.ObjectId));
        Assert.False(fixture.Gizmo.View.CanEdit);
    }

    [Fact]
    public void AnUndeliveredUpdateRestoresTheSharedState()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var instance = Assert.Single(fixture.Backend.Live).Value;
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(3, 0, 1));
        fixture.Serial = 0;

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.NotConnected));
        fixture.Gizmo.Pump();

        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(1, 0, 1), instance.Position);
        Assert.Contains("connection dropped", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void ANativeFaultWhileDraggingRetiresTheExactInstanceWithoutAnotherWrite()
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        var retired = 0;
        fixture.Host.InstanceRetired += () => retired++;
        var faulted = Assert.Single(fixture.Backend.Live).Key;
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Pump();
        fixture.Backend.ThrowOnTransform = true;

        fixture.Gizmo.Drag(new Vector3(2, 0, 1), Quaternion.Identity);
        fixture.Gizmo.Pump();
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();
        fixture.Gizmo.Pump();

        // Fenced and freed once; no restore write was attempted afterwards.
        Assert.Equal(1, fixture.Backend.TransformFaults);
        Assert.Equal(new[] { faulted }, fixture.Backend.Destroyed);
        Assert.Equal(0, fixture.Host.OwnedCount);
        Assert.Equal(1, retired);
        Assert.Empty(fixture.Sent);
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Contains("redrawn", fixture.Gizmo.View.Message);
        // The receiver's receipt releases as already gone, so a fresh instance can spawn.
        Assert.True(fixture.Host.ReleaseOwned(handle).Succeeded);
        fixture.Backend.ThrowOnTransform = false;
        Assert.Equal(ApplyPortStatus.Succeeded, fixture.Host.SpawnOwned(fixture.Request(state, generation: 2)).Status);
        Assert.Equal(2, fixture.Backend.Created);
    }

    [Fact]
    public void ANativeFaultWhileRestoringRetiresTheExactInstance()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var retired = 0;
        fixture.Host.InstanceRetired += () => retired++;
        var id = fixture.Host.BeginPreview(state).Id;
        Assert.True(fixture.Host.MovePreview(id, new Vector3(2, 0, 1), Quaternion.Identity));
        fixture.Backend.ThrowOnTransform = true;

        fixture.Host.CancelPreview(id);
        fixture.Host.CancelPreview(id);

        Assert.Equal(1, fixture.Backend.TransformFaults);
        Assert.Single(fixture.Backend.Destroyed);
        Assert.Equal(0, fixture.Host.OwnedCount);
        Assert.Equal(FurniturePreviewStatus.Faulted, fixture.Host.PreviewStatus(id));
        Assert.Equal(1, retired);
    }

    [Fact]
    public void ANativeFaultWhileRestoringAfterARejectionRetiresTheInstance()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var retired = 0;
        fixture.Host.InstanceRetired += () => retired++;
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(3, 0, 1));
        fixture.Backend.ThrowOnTransform = true;

        fixture.Reject(ProtocolRejectReason.StaleRevision);
        fixture.Gizmo.Pump();
        fixture.Gizmo.Pump();

        Assert.Equal(1, fixture.Backend.TransformFaults);
        Assert.Single(fixture.Backend.Destroyed);
        Assert.Equal(1, retired);
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        // Not "restored": the instance was destroyed and is being redrawn.
        Assert.Contains("redrawn", fixture.Gizmo.View.Message);
        Assert.DoesNotContain("restored", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void ANoOpDragSendsNothingAndRestoresTheShownState()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);

        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Drag(new Vector3(1, 0, 1.00001f), Quaternion.Identity);
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
    }

    [Fact]
    public void RotateModeTurnsOnlyAroundTheVerticalAxisAndKeepsThePosition()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.SetMode(FurnitureGizmoMode.Rotate);

        fixture.Gizmo.BeginDrag(state);
        // A gizmo matrix that also moved and tilted: only the heading is used.
        fixture.Gizmo.Drag(new Vector3(9, 9, 9), Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0.5f, 0.2f));
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        var patch = Assert.IsType<FurnitureTransformPatch>(Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent)).Patch);
        Assert.Null(patch.Position);
        Assert.Equal(90, FurnitureEditor.YawDegrees(patch.Rotation!.Value), 2);
        var turned = FurnitureLocalObjectHost.ToQuaternion(patch.Rotation.Value);
        Assert.True(FurnitureTransforms.AngleBetween(turned, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)) < 1e-3f);
    }

    // A refused move reads "Not moved. {text}", where the
    // text is the command's own player text. The reply's Detail (here a path
    // and an exception message) and the enum name never appear.
    private const string SentinelDetail = @"C:\Users\sentinel-user\secret.json SentinelExceptionMessage-7f3a";

    [Theory]
    [InlineData(ProtocolRejectReason.NotOwner, "Not moved. Refused: only its owner can change it.")]
    [InlineData(ProtocolRejectReason.LocationMismatch, "Not moved. Refused: this scene belongs to another place.")]
    [InlineData(ProtocolRejectReason.UnknownObject, "Not moved. Refused: it no longer exists.")]
    [InlineData(ProtocolRejectReason.SyncNotReady, "Not moved. Refused: the scene is still loading. Try again in a moment.")]
    [InlineData(ProtocolRejectReason.None, "Not moved. Refused by the server.")]
    public void ARefusedMoveReadsNotMovedWithTheSafeRefusalText(ProtocolRejectReason reason, string expected)
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));
        Assert.Single(fixture.Sent);

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: reason, Detail: SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Equal(expected, fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reason.ToString(), fixture.Gizmo.View.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServerCommandStatus.Busy, "Not moved. Still sending earlier changes; try again.")]
    [InlineData((ServerCommandStatus)99, "Not moved. Refused by the server.")]
    public void ARefusedMoveForAnotherStatusShowsNoStatusNameOrDetail(ServerCommandStatus status, string expected)
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));

        fixture.Reply(new ServerCommandResult(status, Detail: SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Equal(expected, fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("99", fixture.Gizmo.View.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Busy", fixture.Gizmo.View.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidMoveShowsOnlyTheEditorsOwnReason()
    {
        // The one arm that shows a Detail: the editor's fixed, safe reasons.
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));

        fixture.Reply(new ServerCommandResult(ServerCommandStatus.Invalid, Detail: "Invalid transform."));
        fixture.Gizmo.Pump();

        Assert.Equal("Not moved. Invalid transform.", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void AFailedMoveCommandNeverShowsTheExceptionText()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));

        fixture.Fail(new IOException(SentinelDetail));
        fixture.Gizmo.Pump();

        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Equal("The connection dropped before the server answered; showing the shared state.", fixture.Gizmo.View.Message);
        Assert.DoesNotContain("sentinel", fixture.Gizmo.View.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IOException", fixture.Gizmo.View.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictRestoresTheLatestServerStateWithAMessage()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Drag(state, new Vector3(4, 0, 1));
        Assert.Single(fixture.Sent);

        fixture.Reject(ProtocolRejectReason.StaleRevision);
        fixture.Gizmo.Pump();

        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        Assert.Contains("restored", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void ARemoteUpdateDuringADragCancelsItAndTheServerStateWins()
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Drag(new Vector3(3, 0, 1), Quaternion.Identity);
        fixture.Gizmo.Pump();

        // Someone else moved it: the receiver applies the new authoritative state.
        var remote = state with { Revision = new ObjectRevision(2), Position = new Vector3State(7, 0, 7) };
        Assert.True(fixture.Host.ApplyOwned(handle, remote).Succeeded);
        fixture.Gizmo.Drag(new Vector3(4, 0, 1), Quaternion.Identity);
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.Equal(new Vector3(7, 0, 7), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Contains("Someone else", fixture.Gizmo.View.Message);
        Assert.Equal(FurniturePreviewBeginStatus.Stale, fixture.Host.BeginPreview(state).Status);
    }

    [Fact]
    public void ARemoteDeletionCancelsWithoutTouchingAnything()
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Drag(new Vector3(3, 0, 1), Quaternion.Identity);
        fixture.Gizmo.Pump();

        Assert.True(fixture.Host.ReleaseOwned(handle).Succeeded);
        var writes = fixture.Backend.TransformWrites;
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Backend.Live);
        Assert.Equal(writes, fixture.Backend.TransformWrites);
        Assert.Contains("no longer here", fixture.Gizmo.View.Message);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("disconnect")]
    [InlineData("reconnect")]
    [InlineData("host-loss")]
    [InlineData("selection")]
    [InlineData("mode")]
    [InlineData("close")]
    [InlineData("unload")]
    public void InterruptionsLeaveNoPreviewAuthorityBehind(string interruption)
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        var other = fixture.Spawn(new Vector3State(5, 0, 5)).State;
        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Drag(new Vector3(3, 0, 1), Quaternion.Identity);
        fixture.Gizmo.Pump();

        switch (interruption)
        {
            case "location":
                // As the plugin does on a scene change.
                fixture.Gizmo.Cancel("moved");
                fixture.Host.DestroyAll();
                break;
            case "disconnect":
                fixture.Serial = 0;
                break;
            case "reconnect":
                fixture.Serial = 2;
                break;
            case "host-loss":
                // The receiver resets the local-object domain: the receipt is released.
                fixture.Host.ReleaseOwned(handle);
                break;
            case "selection":
                fixture.Gizmo.Select(other.ObjectId);
                break;
            case "mode":
                fixture.Gizmo.SetMode(FurnitureGizmoMode.Rotate);
                break;
            case "close":
                fixture.Gizmo.Select(null);
                break;
            case "unload":
                fixture.Gizmo.Cancel();
                fixture.Host.Shutdown();
                break;
        }
        fixture.Gizmo.Pump();
        fixture.Gizmo.EndDrag();
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.Equal(FurnitureGizmoPhase.Idle, fixture.Gizmo.View.Phase);
        // A surviving instance shows the authoritative state again.
        foreach (var live in fixture.Backend.Live.Values.Where(live => live.ModelPath.Length > 0))
            Assert.Contains(live.Position, new[] { new Vector3(1, 0, 1), new Vector3(5, 0, 5) });
        // No preview is left: further drag input does nothing native.
        var writes = fixture.Backend.TransformWrites;
        fixture.Gizmo.Drag(new Vector3(9, 0, 9), Quaternion.Identity);
        fixture.Gizmo.Pump();
        Assert.Equal(writes, fixture.Backend.TransformWrites);
    }

    [Fact]
    public void ACancelNeverTouchesAReplacementWithTheSameObjectId()
    {
        var fixture = new Fixture();
        var (state, handle) = fixture.Spawn(new Vector3State(1, 0, 1));
        var id = fixture.Host.BeginPreview(state).Id;
        Assert.True(fixture.Host.MovePreview(id, new Vector3(2, 0, 1), Quaternion.Identity));

        // The receipt is released and the object respawned with a newer generation.
        Assert.True(fixture.Host.ReleaseOwned(handle).Succeeded);
        var replacement = fixture.Host.SpawnOwned(fixture.Request(state, generation: 2)).Value;
        var newInstance = Assert.Single(fixture.Backend.Live).Value;
        var writes = fixture.Backend.TransformWrites;

        fixture.Host.CancelPreview(id);
        Assert.False(fixture.Host.MovePreview(id, new Vector3(3, 0, 1), Quaternion.Identity));

        Assert.Equal(writes, fixture.Backend.TransformWrites);
        Assert.Equal(new Vector3(1, 0, 1), newInstance.Position);
        Assert.NotEqual(handle, replacement);
        Assert.Equal(FurniturePreviewStatus.Released, fixture.Host.PreviewStatus(id));
    }

    [Fact]
    public void InvalidDragValuesNeverReachTheNativeObject()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var id = fixture.Host.BeginPreview(state).Id;
        var writes = fixture.Backend.TransformWrites;

        Assert.False(fixture.Host.MovePreview(id, new Vector3(float.NaN, 0, 0), Quaternion.Identity));
        Assert.False(fixture.Host.MovePreview(id, new Vector3(float.PositiveInfinity, 0, 0), Quaternion.Identity));
        Assert.False(fixture.Host.MovePreview(id, Vector3.Zero, new Quaternion(0, 0, 0, 0)));
        Assert.False(fixture.Host.MovePreview(id, Vector3.Zero, new Quaternion(float.NaN, 0, 0, 1)));
        Assert.Equal(writes, fixture.Backend.TransformWrites);

        fixture.Gizmo.Select(state.ObjectId);
        fixture.Gizmo.Drag(new Vector3(float.NaN, 0, 0), Quaternion.Identity);
        fixture.Gizmo.Pump();
        Assert.Equal(writes, fixture.Backend.TransformWrites);
    }

    [Fact]
    public void OnlyTheExactShownStateOfADrawnObjectCanBePreviewed()
    {
        var fixture = new Fixture { ChecksUntilLoaded = 2 };
        var (loading, _) = fixture.Spawn(new Vector3State(1, 0, 1));

        // Created but still loading: not drawn, so no gizmo.
        Assert.False(fixture.Host.IsDrawn(loading.ObjectId));
        Assert.Equal(FurniturePreviewBeginStatus.NotDrawn, fixture.Host.BeginPreview(loading).Status);

        var drawn = new Fixture();
        var (state, _) = drawn.Spawn(new Vector3State(1, 0, 1));
        Assert.True(drawn.Host.IsDrawn(state.ObjectId));
        Assert.Equal(FurniturePreviewBeginStatus.Stale,
            drawn.Host.BeginPreview(state with { Revision = new ObjectRevision(5) }).Status);
        var first = drawn.Host.BeginPreview(state);
        Assert.True(first.Started);
        Assert.Equal(FurniturePreviewBeginStatus.Busy, drawn.Host.BeginPreview(state).Status);
    }

    [Fact]
    public void MoveToMeStillWorksBesideTheGizmo()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);

        _ = fixture.Editor.NudgeAsync(state, new Vector3(0.5f, 0, 0), 0);

        var patch = Assert.IsType<FurnitureTransformPatch>(Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent)).Patch);
        Assert.Equal(new Vector3State(1.5f, 0, 1), patch.Position);
    }

    [Fact]
    public void TypedTransformSendsOneAtomicUpdateAndWaitsForTheEvent()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        var instance = Assert.Single(fixture.Backend.Live).Value;
        fixture.Gizmo.Select(state.ObjectId);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        Assert.True(fixture.Gizmo.ApplyTransform(state, new Vector3(2, 3, 4), turned));
        // Queued: nothing else may start before the game thread takes it.
        Assert.False(fixture.Gizmo.View.CanEdit);
        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(9, 9, 9), turned));
        Assert.Empty(fixture.Sent);
        fixture.Gizmo.Pump();

        // One update, position and facing together, from the shown revision.
        var update = Assert.IsType<UpdateObjectRequest>(Assert.Single(fixture.Sent));
        Assert.Equal(state.Revision, update.BaseRevision);
        var patch = Assert.IsType<FurnitureTransformPatch>(update.Patch);
        Assert.Equal(new Vector3State(2, 3, 4), patch.Position);
        Assert.Equal(90, FurnitureEditor.YawDegrees(patch.Rotation!.Value), 2);
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        Assert.False(fixture.Gizmo.View.CanEdit);
        Assert.Equal(new Vector3(2, 3, 4), instance.Position);

        fixture.Accept(state, new Vector3State(2, 3, 4));
        fixture.Gizmo.Pump();
        Assert.Equal(FurnitureGizmoPhase.Committing, fixture.Gizmo.View.Phase);
        fixture.ApplyAuthoritative(state, new Vector3State(2, 3, 4));
        fixture.Gizmo.Pump();
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Null(fixture.Gizmo.View.Message);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public void TypedTransformConflictRestoresTheServerStateWithAMessage()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);

        Assert.True(fixture.Gizmo.ApplyTransform(state, new Vector3(5, 0, 1), Quaternion.Identity));
        fixture.Gizmo.Pump();
        fixture.Reject(ProtocolRejectReason.StaleRevision);
        fixture.Gizmo.Pump();

        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.True(fixture.Gizmo.View.CanEdit);
        Assert.Contains("restored", fixture.Gizmo.View.Message);
    }

    [Fact]
    public void TypedTransformFromAnOutdatedRevisionSendsNothing()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);
        var outdated = state with { Position = new Vector3State(9, 9, 9) };

        Assert.True(fixture.Gizmo.ApplyTransform(outdated, new Vector3(5, 0, 1), Quaternion.Identity));
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.Equal(new Vector3(1, 0, 1), Assert.Single(fixture.Backend.Live).Value.Position);
        Assert.Equal("It changed meanwhile; not applied.", fixture.Gizmo.View.Message);
        Assert.True(fixture.Gizmo.View.CanEdit);
    }

    [Fact]
    public void TypedTransformIsRefusedWhileDraggingUnselectedOrInvalid()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));

        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(2, 0, 1), Quaternion.Identity));
        fixture.Gizmo.Select(state.ObjectId);
        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(float.NaN, 0, 1), Quaternion.Identity));
        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(2, 0, 1), new Quaternion(0, 0, 0, 0)));

        fixture.Gizmo.BeginDrag(state);
        fixture.Gizmo.Pump();
        Assert.Equal(FurnitureGizmoPhase.Dragging, fixture.Gizmo.View.Phase);
        Assert.False(fixture.Gizmo.ApplyTransform(state, new Vector3(2, 0, 1), Quaternion.Identity));
        fixture.Gizmo.Pump();
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public void ChangingSelectionDropsAQueuedTypedTransform()
    {
        var fixture = new Fixture();
        var (state, _) = fixture.Spawn(new Vector3State(1, 0, 1));
        fixture.Gizmo.Select(state.ObjectId);

        Assert.True(fixture.Gizmo.ApplyTransform(state, new Vector3(2, 0, 1), Quaternion.Identity));
        fixture.Gizmo.Select(null);
        fixture.Gizmo.Pump();

        Assert.Empty(fixture.Sent);
        Assert.True(fixture.Gizmo.View.CanEdit);
    }

    private sealed class Fixture
    {
        private TaskCompletionSource<ServerCommandResult> reply;

        internal Fixture()
        {
            var assets = new FakeFurnitureAssets();
            assets.AddSingleModel(Stool);
            Backend = new FakeFurnitureBackend();
            Host = new FurnitureLocalObjectHost(
                new FurnitureSupportResolver(new FurnitureCatalog([Stool]), assets), Backend);
            Editor = new FurnitureEditor((request, _) =>
            {
                Sent.Add(request);
                reply = new TaskCompletionSource<ServerCommandResult>();
                return reply.Task;
            });
            Gizmo = new FurnitureGizmoController(Host, Editor, () => Serial);
        }

        internal int ChecksUntilLoaded
        {
            set => Backend.ChecksUntilLoaded = value;
        }

        internal FakeFurnitureBackend Backend { get; }
        internal FurnitureLocalObjectHost Host { get; }
        internal FurnitureEditor Editor { get; }
        internal FurnitureGizmoController Gizmo { get; }
        internal List<object> Sent { get; } = new();
        internal Dictionary<RscObjectId, LocalObjectInstanceHandle> Handles { get; } = new();
        internal long Serial { get; set; } = 1;

        internal (FurnitureState State, LocalObjectInstanceHandle Handle) Spawn(Vector3State position)
        {
            var state = new FurnitureState(
                new RscObjectId(Guid.NewGuid()),
                new ObjectRevision(1),
                new RscUserId("a"),
                TestLocations.SharedScene,
                Stool.Identity,
                position,
                new RotationState(0, 0, 0, 1)) { Location = TestLocations.Default };
            var spawned = Host.SpawnOwned(Request(state, generation: 1));
            Assert.True(spawned.Value.IsValid);
            Handles[state.ObjectId] = spawned.Value;
            return (state, spawned.Value);
        }

        internal LocalObjectSpawnRequest Request(FurnitureState state, long generation) => new(
            new LocalObjectApplyToken(Session, state.ObjectId, new ApplyGeneration(generation)),
            LocalObjectSpawnIdentity.FromState(state),
            generation,
            state);

        internal void Drag(FurnitureState state, Vector3 to)
        {
            Gizmo.BeginDrag(state);
            Gizmo.Drag(to, Quaternion.Identity);
            Gizmo.EndDrag();
            Gizmo.Pump();
        }

        /// <summary>The server's (valid) completion for the update; not authority to apply.</summary>
        internal void Accept(FurnitureState basis, Vector3State position)
        {
            var updated = basis with { Revision = new ObjectRevision(basis.Revision.Value + 1), Position = position };
            var completion = new ObjectMutationCompletion(Guid.NewGuid(), ObjectMutationCompletionKind.Update, false,
                new ServerSequence(2), updated, null);
            Assert.True(completion.IsValid);
            Reply(new ServerCommandResult(ServerCommandStatus.Completed, completion));
        }

        /// <summary>The receiver applies the authoritative event for the object.</summary>
        internal void ApplyAuthoritative(FurnitureState basis, Vector3State position)
        {
            var updated = basis with { Revision = new ObjectRevision(basis.Revision.Value + 1), Position = position };
            var handle = Handles[basis.ObjectId];
            Assert.True(Host.ApplyOwned(handle, updated).Succeeded);
        }

        internal void Reply(ServerCommandResult result) => reply.SetResult(result);

        /// <summary>The command task itself fails (for example the transport threw).</summary>
        internal void Fail(Exception exception) => reply.SetException(exception);

        internal void Reject(ProtocolRejectReason reason) =>
            Reply(new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: reason));
    }
}

public sealed class FurnitureTransformDraftTests
{
    private static readonly RscObjectId Chair = new(Guid.NewGuid());
    private static readonly RscObjectId Table = new(Guid.NewGuid());

    private static FurnitureState State(RscObjectId id, long revision, Vector3State position, float facingDegrees = 30) =>
        new(id, new ObjectRevision(revision), new RscUserId("a"), TestLocations.SharedScene,
            "furniture:indoor:6000", position, FurnitureEditor.Yaw(facingDegrees * MathF.PI / 180))
        { Location = TestLocations.Default };

    [Fact]
    public void AnUntouchedDraftFollowsTheServerAndApplyingItSendsNothing()
    {
        var draft = new FurnitureTransformDraft();
        var shown = State(Chair, 1, new Vector3State(1.23456f, 2, 3));
        draft.Sync(shown, locked: false);

        Assert.Equal(1.23456f, draft.X);
        Assert.Equal(30, draft.Facing, 2);
        Assert.False(draft.IsDirty);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);

        // Values that only differ by the shown rounding are unchanged too.
        draft.Edit(1.235f, 2, 3, 30.00f);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);
        Assert.False(draft.IsDirty);

        // A newer server state replaces an untouched draft.
        var moved = State(Chair, 2, new Vector3State(7, 2, 3));
        draft.Sync(moved, locked: false);
        Assert.Equal(7, draft.X);
    }

    [Fact]
    public void ApplyBuildsOneTransformFromTheShownRevision()
    {
        var draft = new FurnitureTransformDraft();
        var shown = State(Chair, 4, new Vector3State(1.23456f, 2.5f, 3));
        draft.Sync(shown, locked: false);

        draft.Edit(10, 2.5f, 3, 120);
        Assert.True(draft.IsDirty);
        var result = draft.TryApply();

        Assert.Equal(FurnitureDraftApplyStatus.Ready, result.Status);
        Assert.Same(shown, result.Basis);
        // Untouched fields keep their exact server values.
        Assert.Equal(new Vector3(10, 2.5f, 3), result.Position);
        Assert.Equal(120, FurnitureTransforms.Yaw(result.Rotation) * 180 / MathF.PI, 2);
        Assert.False(draft.IsDirty);

        // Position only: the rotation is exactly the server's.
        var other = new FurnitureTransformDraft();
        other.Sync(shown, locked: false);
        other.Edit(1.23456f, 9, 3, other.Facing);
        var positionOnly = other.TryApply();
        Assert.Equal(new Vector3(1.23456f, 9, 3), positionOnly.Position);
        Assert.True(FurnitureTransforms.AngleBetween(FurnitureLocalObjectHost.ToQuaternion(shown.Rotation), positionOnly.Rotation) < 1e-5f);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, 0f, float.NaN)]
    [InlineData(0f, 0f, 0f, float.PositiveInfinity)]
    [InlineData(200000f, 0f, 0f, 0f)]
    [InlineData(0f, -100001f, 0f, 0f)]
    public void InvalidValuesAreRefusedBeforeSending(float x, float y, float z, float facing)
    {
        var draft = new FurnitureTransformDraft();
        draft.Sync(State(Chair, 1, new Vector3State(1, 2, 3)), locked: false);
        draft.Edit(x, y, z, facing);

        var result = draft.TryApply();

        Assert.Equal(FurnitureDraftApplyStatus.Invalid, result.Status);
        Assert.Null(result.Basis);
        Assert.NotNull(result.Message);
        // The typed values stay visible to be corrected.
        Assert.True(draft.IsDirty);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-90f, 270f)]
    [InlineData(360f, 0f)]
    [InlineData(720.004f, 0f)]
    [InlineData(359.99f, 359.99f)]
    [InlineData(359.996f, 0f)]
    [InlineData(-0.001f, 0f)]
    [InlineData(45.678f, 45.68f)]
    public void FacingIsNormalizedToZeroTo360(float input, float expected)
    {
        Assert.Equal(expected, FurnitureTransformDraft.NormalizeDegrees(input), 3);
    }

    [Fact]
    public void FacingFromTheServerIsShownNormalized()
    {
        var draft = new FurnitureTransformDraft();
        draft.Sync(State(Chair, 1, new Vector3State(0, 0, 0), facingDegrees: -90), locked: false);
        Assert.Equal(270, draft.Facing, 2);
        draft.Edit(0, 0, 0, -45);
        Assert.Equal(315, draft.Facing, 2);
    }

    [Fact]
    public void AnEditedDraftFromAnOutdatedRevisionIsRefusedAndShowsTheServerState()
    {
        var draft = new FurnitureTransformDraft();
        draft.Sync(State(Chair, 1, new Vector3State(1, 2, 3)), locked: false);
        draft.Edit(5, 2, 3, draft.Facing);

        // Someone else moved it: the edits stay until Apply.
        var newer = State(Chair, 2, new Vector3State(8, 8, 8));
        draft.Sync(newer, locked: false);
        Assert.Equal(5, draft.X);

        var result = draft.TryApply();

        Assert.Equal(FurnitureDraftApplyStatus.Stale, result.Status);
        Assert.Null(result.Basis);
        Assert.Contains("changed", result.Message);
        Assert.Equal(8, draft.X);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void ResetRestoresTheServerValuesAndSelectionLoadsTheNewItem()
    {
        var draft = new FurnitureTransformDraft();
        draft.Sync(State(Chair, 1, new Vector3State(1, 2, 3)), locked: false);
        draft.Edit(5, 6, 7, 90);
        draft.Reset();
        Assert.Equal(1, draft.X);
        Assert.Equal(30, draft.Facing, 2);
        Assert.False(draft.IsDirty);

        draft.Edit(5, 6, 7, 90);
        draft.Sync(State(Table, 1, new Vector3State(4, 4, 4)), locked: false);
        Assert.Equal(Table, draft.ObjectId);
        Assert.Equal(4, draft.X);
        Assert.False(draft.IsDirty);

        draft.Sync(null, locked: false);
        Assert.Null(draft.ObjectId);
        Assert.Equal(FurnitureDraftApplyStatus.NoItem, draft.TryApply().Status);
    }

    [Fact]
    public void WhileSavingTheAppliedValuesStayShownThenTheServerStateReturns()
    {
        var draft = new FurnitureTransformDraft();
        var shown = State(Chair, 1, new Vector3State(1, 2, 3));
        draft.Sync(shown, locked: false);
        draft.Edit(5, 2, 3, draft.Facing);
        Assert.Equal(FurnitureDraftApplyStatus.Ready, draft.TryApply().Status);

        // Saving: the controls are locked and keep the applied values.
        draft.Sync(shown, locked: true);
        Assert.Equal(5, draft.X);
        // Saved or refused: the authoritative state is shown again.
        draft.Sync(shown, locked: false);
        Assert.Equal(1, draft.X);
    }
}

public sealed class FurnitureScrubTests
{
    [Fact]
    public void StepsAreFineByDefaultWithFinerAndCoarserModifiers()
    {
        Assert.Equal(0.01f, FurnitureScrub.Step(FurnitureScrub.PositionStep, fine: false, coarse: false), 6);
        Assert.Equal(0.001f, FurnitureScrub.Step(FurnitureScrub.PositionStep, fine: true, coarse: false), 6);
        Assert.Equal(0.1f, FurnitureScrub.Step(FurnitureScrub.PositionStep, fine: false, coarse: true), 6);
        Assert.Equal(0.001f, FurnitureScrub.Step(FurnitureScrub.PositionStep, fine: true, coarse: true), 6);
        Assert.Equal(1f, FurnitureScrub.Step(FurnitureScrub.FacingStep, false, false));
    }

    [Fact]
    public void WheelStepsTheValueAndIgnoresNonFiniteInput()
    {
        Assert.Equal(1.02f, FurnitureScrub.Wheel(1f, 2, 0.01f), 5);
        Assert.Equal(0.99f, FurnitureScrub.Wheel(1f, -1, 0.01f), 5);
        Assert.Equal(1f, FurnitureScrub.Wheel(1f, 0, 0.01f));
        Assert.Equal(1f, FurnitureScrub.Wheel(1f, float.NaN, 0.01f));
        Assert.Equal(1f, FurnitureScrub.Wheel(1f, 1, float.PositiveInfinity));
        Assert.Equal(float.MaxValue, FurnitureScrub.Wheel(float.MaxValue, 1, float.MaxValue));
        Assert.True(float.IsNaN(FurnitureScrub.Wheel(float.NaN, 1, 0.01f)));
    }

    [Fact]
    public void DraggingUsesTheModifiersLikeTheWheel()
    {
        // 10 pixels sideways: 0.1 yalms, 0.01 with Alt (finer), 1 with Shift (coarser).
        Assert.Equal(1.1f, FurnitureScrub.Drag(1f, 10, FurnitureScrub.Step(FurnitureScrub.PositionStep, false, false)), 5);
        Assert.Equal(1.01f, FurnitureScrub.Drag(1f, 10, FurnitureScrub.Step(FurnitureScrub.PositionStep, true, false)), 5);
        Assert.Equal(2f, FurnitureScrub.Drag(1f, 10, FurnitureScrub.Step(FurnitureScrub.PositionStep, false, true)), 5);
        Assert.Equal(0.9f, FurnitureScrub.Drag(1f, -10, FurnitureScrub.PositionStep), 5);
        // Facing: 0.5 degrees per pixel.
        Assert.Equal(15f, FurnitureScrub.Drag(10f, 10, FurnitureScrub.FacingDragStep), 5);
        Assert.Equal(1f, FurnitureScrub.Drag(1f, 0, 0.01f));
        Assert.Equal(1f, FurnitureScrub.Drag(1f, float.NaN, 0.01f));
        Assert.Equal(1f, FurnitureScrub.Drag(1f, 1, float.NegativeInfinity));
    }

    [Fact]
    public void ChevronAndWheelTurnsWrapThroughTheDraft()
    {
        var draft = new FurnitureTransformDraft();
        var state = new FurnitureState(new RscObjectId(Guid.NewGuid()), new ObjectRevision(1), new RscUserId("a"),
            TestLocations.SharedScene, "furniture:indoor:6000", new Vector3State(0, 0, 0), FurnitureEditor.Yaw(0))
        { Location = TestLocations.Default };
        draft.Sync(state, locked: false);

        // One chevron left from 0 shows 359; scrolling back past 360 shows 0.
        draft.Edit(draft.X, draft.Y, draft.Z, draft.Facing - FurnitureScrub.FacingStep);
        Assert.Equal(359, draft.Facing, 2);
        draft.Edit(draft.X, draft.Y, draft.Z, FurnitureScrub.Wheel(draft.Facing, 1, FurnitureScrub.FacingStep));
        Assert.Equal(0, draft.Facing, 2);
        Assert.Equal(FurnitureDraftApplyStatus.NothingChanged, draft.TryApply().Status);
    }
}
