using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Lights;
using RSyncStageCraft.Core.Protocol;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace RSyncStageCraft.Ui;

/// <summary>
/// Draws the move/rotate gizmo on the selected furniture, or the move gizmo
/// and range outline on the selected light, over the game view. Overlay layout and camera math follow Stagehand, the defensive
/// camera checks follow Izzy's Furniture; only
/// Dalamud's shipped ImGuizmo binding is used. This class never touches a
/// game object: it reports drag input to the gizmo controller, and the
/// furniture host applies it on the game thread.
/// </summary>
internal sealed unsafe class FurnitureGizmoOverlay
{
    private readonly PluginConnection connection;
    private readonly Func<bool> windowOpen;
    private bool dragging;

    internal FurnitureGizmoOverlay(PluginConnection connection, Func<bool> windowOpen)
    {
        this.connection = connection;
        this.windowOpen = windowOpen;
    }

    internal void Draw()
    {
        var gizmo = connection.Gizmo;
        var view = gizmo.View;
        if (!windowOpen() || view.Selected is not { } selected)
        {
            StopDragging();
            return;
        }

        // Drawn only on the selected item while idle, or on the dragged item
        // while dragging; hidden while an update is saving. The preview
        // transform belongs to the busy item and is never used for another.
        if (!view.ShowsGizmoOn(selected))
        {
            StopDragging();
            return;
        }
        var state = connection.Runtime.DesiredLocalObjects()
            .FirstOrDefault(candidate => candidate.ObjectId == selected &&
                candidate is FurnitureState or LightState or ActorState);
        if (state is null || (view.Phase == FurnitureGizmoPhase.Idle && !connection.Objects.IsDrawn(selected)))
        {
            StopDragging();
            return;
        }

        // No camera, no gizmo: nothing is drawn or applied.
        if (!TryReadCamera(out var camera))
        {
            if (dragging)
            {
                dragging = false;
                gizmo.Cancel("The camera is unavailable; the move was discarded.");
            }
            return;
        }

        var ownPreview = view.Busy == selected;
        var light = state as LightState;
        var position = (ownPreview ? view.PreviewPosition : null) ?? state switch
        {
            LightState lit => LightLocalObjectHost.PositionOf(lit),
            ActorState actor => FurnitureLocalObjectHost.ToVector(actor.Position),
            _ => FurnitureLocalObjectHost.ToVector(((FurnitureState)state).Position),
        };
        // A point light has no rotation: its gizmo is always Move, axis-aligned.
        // An actor turns around the vertical axis only (yaw).
        var rotation = light is not null
            ? Quaternion.Identity
            : (ownPreview ? view.PreviewRotation : null) ?? state switch
            {
                ActorState actor => ActorLocalObjectHost.RotationOf(actor),
                _ => FurnitureLocalObjectHost.ToQuaternion(((FurnitureState)state).Rotation),
            };
        var matrix = GizmoCameraMath.Compose(position, rotation);
        var cameraView = camera.View;
        var cameraProjection = camera.Projection;

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        {
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.SetNextWindowPos(camera.ViewportPosition);
            ImGui.SetNextWindowSize(camera.ViewportSize);
            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus;
            if (ImGui.Begin("RSyncStageCraft furniture gizmo###RSyncStageCraftGizmo", flags))
            {
                // A stable ID from the full object ID, within this window's ID scope.
                var gizmoId = (int)ImGui.GetID($"rsc-furniture-{selected.Value:N}");
                ImGuizmo.SetID(gizmoId);
                ImGuizmo.SetDrawlist(ImGui.GetBackgroundDrawList());
                ImGuizmo.SetOrthographic(camera.Orthographic);
                ImGuizmo.SetRect(camera.ViewportPosition.X, camera.ViewportPosition.Y,
                    camera.ViewportSize.X, camera.ViewportSize.Y);
                ImGuizmo.BeginFrame();
                ImGuizmo.Enable(true);

                if (light is not null)
                    DrawRangeOutline(camera, position, light.Range);

                var operation = view.Mode == FurnitureGizmoMode.Rotate && light is null
                    ? ImGuizmoOperation.RotateY
                    : ImGuizmoOperation.Translate;
                var changed = ImGuizmo.Manipulate(
                    ref cameraView.M11, ref cameraProjection.M11, operation, ImGuizmoMode.World, ref matrix.M11);
                var inUse = ImGuizmo.IsUsing();

                // Keep the drag from also turning the game camera.
                if (inUse || ImGuizmo.IsOver())
                    ImGui.SetNextFrameWantCaptureMouse(true);

                if ((changed || inUse) && !dragging && view.Phase == FurnitureGizmoPhase.Idle)
                {
                    dragging = true;
                    gizmo.BeginDrag(state);
                }
                if (changed && dragging && GizmoCameraMath.TryDecompose(matrix, out var newPosition, out var newRotation))
                    gizmo.Drag(newPosition, newRotation);
                if (dragging && !inUse)
                    StopDragging();

                ImGuizmo.SetID(-1);
            }
            ImGui.End();
        }
    }

    /// <summary>
    /// The selected light's range (three circles, after Izzy's overlay).
    /// ImGui only; nothing native is written. Uses the camera that already
    /// passed the gizmo's fail-closed checks; a segment whose ends cannot be
    /// projected (behind the camera, non-finite) is skipped.
    /// </summary>
    private static void DrawRangeOutline(in GizmoCamera camera, Vector3 center, float range)
    {
        var draw = ImGui.GetBackgroundDrawList();
        var color = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.4f, 0.55f));
        foreach (var (from, to) in GizmoCameraMath.RangeOutline(center, range))
        {
            if (GizmoCameraMath.TryProject(camera, from, out var a) &&
                GizmoCameraMath.TryProject(camera, to, out var b))
                draw.AddLine(a, b, color, 1.5f);
        }
    }

    private void StopDragging()
    {
        if (!dragging)
            return;
        dragging = false;
        connection.Gizmo.EndDrag();
    }

    /// <summary>Izzy-style null checks on every pointer; Stagehand's math on the values.</summary>
    private static bool TryReadCamera(out GizmoCamera camera)
    {
        camera = default;
        try
        {
            var manager = SceneCameraManager.Instance();
            if (manager == null)
                return false;
            var current = manager->CurrentCamera;
            if (current == null)
                return false;
            var render = current->RenderCamera;
            if (render == null)
                return false;
            var viewport = ImGui.GetMainViewport();
            return GizmoCameraMath.TryPrepare(
                render->ViewMatrix,
                render->ProjectionMatrix,
                render->NearPlane,
                render->FarPlane,
                render->IsOrtho,
                viewport.Pos,
                viewport.Size,
                out camera);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
