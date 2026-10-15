using System.Numerics;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>The matrices and screen rectangle the gizmo is drawn with.</summary>
public readonly record struct GizmoCamera(
    Matrix4x4 View,
    Matrix4x4 Projection,
    bool Orthographic,
    Vector2 ViewportPosition,
    Vector2 ViewportSize);

/// <summary>
/// Prepares the game's render-camera matrices for ImGuizmo. The near/far
/// correction follows Stagehand's overlay. Every
/// input is checked: any missing, non-finite or degenerate value means no
/// gizmo is drawn and nothing is applied.
/// </summary>
public static class GizmoCameraMath
{
    public static bool TryPrepare(
        Matrix4x4 view,
        Matrix4x4 projection,
        float nearPlane,
        float farPlane,
        bool orthographic,
        Vector2 viewportPosition,
        Vector2 viewportSize,
        out GizmoCamera camera)
    {
        camera = default;
        if (!IsFinite(view) || !IsFinite(projection) ||
            !float.IsFinite(nearPlane) || !float.IsFinite(farPlane) ||
            nearPlane <= 0 || farPlane <= nearPlane || farPlane - nearPlane < 1e-3f ||
            !float.IsFinite(viewportPosition.X) || !float.IsFinite(viewportPosition.Y) ||
            !float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y) ||
            viewportSize.X < 1 || viewportSize.Y < 1)
            return false;

        var clip = farPlane / (farPlane - nearPlane);
        projection.M43 = -(clip * nearPlane);
        projection.M33 = -((farPlane + nearPlane) / (farPlane - nearPlane));
        view.M44 = 1.0f;
        // A degenerate camera (for example all zeros) cannot place a gizmo.
        if (!IsFinite(view) || !IsFinite(projection) ||
            projection.M11 == 0 || projection.M22 == 0 || !Matrix4x4.Invert(view, out _))
            return false;

        camera = new GizmoCamera(view, projection, orthographic, viewportPosition, viewportSize);
        return true;
    }

    /// <summary>The gizmo matrix for a furniture transform (no scale).</summary>
    public static Matrix4x4 Compose(Vector3 position, Quaternion rotation) =>
        Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);

    /// <summary>Reads position and rotation back from a gizmo matrix; scale is ignored.</summary>
    public static bool TryDecompose(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        if (!IsFinite(matrix) || !Matrix4x4.Decompose(matrix, out _, out var decomposed, out var translation))
            return false;
        if (!FurnitureTransforms.IsValidPosition(translation) ||
            !FurnitureTransforms.TryNormalize(decomposed, out var normalized))
            return false;
        position = translation;
        rotation = normalized;
        return true;
    }

    /// <summary>
    /// Projects a world point to screen coordinates with a prepared camera
    /// (row vectors, as ImGuizmo uses the same matrices). Fails closed: a
    /// non-finite input, a point at or behind the camera plane, or a
    /// non-finite result gives false and nothing is drawn for it.
    /// </summary>
    public static bool TryProject(in GizmoCamera camera, Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
            return false;
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.View * camera.Projection);
        if (!float.IsFinite(clip.W) || clip.W <= 1e-4f)
            return false;
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        if (!float.IsFinite(ndc.X) || !float.IsFinite(ndc.Y))
            return false;
        screen = new Vector2(
            camera.ViewportPosition.X + (ndc.X + 1f) * 0.5f * camera.ViewportSize.X,
            camera.ViewportPosition.Y + (1f - ndc.Y) * 0.5f * camera.ViewportSize.Y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>Number of segments per range-outline circle.</summary>
    public const int OutlineSegments = 48;

    /// <summary>
    /// The light's range outline (after Izzy's overlay): three circles of the
    /// given radius around the centre, in the XZ, XY and YZ planes, as world
    /// segments. Empty for a non-finite or non-positive radius.
    /// </summary>
    public static IReadOnlyList<(Vector3 From, Vector3 To)> RangeOutline(Vector3 center, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0 ||
            !float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
            return Array.Empty<(Vector3, Vector3)>();
        var segments = new List<(Vector3, Vector3)>(OutlineSegments * 3);
        for (var plane = 0; plane < 3; plane++)
        {
            var previous = Point(plane, 0);
            for (var i = 1; i <= OutlineSegments; i++)
            {
                var next = Point(plane, i);
                segments.Add((previous, next));
                previous = next;
            }
        }
        return segments;

        Vector3 Point(int plane, int index)
        {
            var angle = MathF.Tau * index / OutlineSegments;
            var (sin, cos) = MathF.SinCos(angle);
            var offset = plane switch
            {
                0 => new Vector3(cos, 0, sin),
                1 => new Vector3(cos, sin, 0),
                _ => new Vector3(0, cos, sin),
            };
            return center + offset * radius;
        }
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
