using System.Numerics;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>Transform helpers for furniture editing. Furniture has no scale.</summary>
public static class FurnitureTransforms
{
    /// <summary>Below these, a drag changed nothing (a click on the gizmo).</summary>
    public const float PositionEpsilon = 1e-4f;
    public const float AngleEpsilon = 1e-4f;

    /// <summary>Largest coordinate accepted; anything beyond is not a real placement.</summary>
    public const float MaximumCoordinate = 100_000f;

    public static bool IsValidPosition(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) &&
        MathF.Abs(position.X) <= MaximumCoordinate &&
        MathF.Abs(position.Y) <= MaximumCoordinate &&
        MathF.Abs(position.Z) <= MaximumCoordinate;

    /// <summary>A finite, non-zero rotation, normalized.</summary>
    public static bool TryNormalize(Quaternion rotation, out Quaternion normalized)
    {
        var length = rotation.Length();
        if (!float.IsFinite(length) || length < 1e-6f)
        {
            normalized = Quaternion.Identity;
            return false;
        }
        normalized = Quaternion.Normalize(rotation);
        return float.IsFinite(normalized.X) && float.IsFinite(normalized.Y) &&
            float.IsFinite(normalized.Z) && float.IsFinite(normalized.W);
    }

    /// <summary>The heading (rotation around the vertical axis) of a rotation, in radians.</summary>
    public static float Yaw(Quaternion q) =>
        MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X));

    /// <summary>
    /// Turns <paramref name="basis"/> around the world vertical axis so its
    /// heading matches <paramref name="target"/>. Only the heading changes:
    /// any tilt of the basis is kept, and a gizmo that reported pitch or roll
    /// cannot introduce them.
    /// </summary>
    public static Quaternion WithYawOf(Quaternion basis, Quaternion target)
    {
        var delta = WrapAngle(Yaw(target) - Yaw(basis));
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, delta) * basis);
    }

    public static bool SameTransform(Vector3 positionA, Quaternion rotationA, Vector3 positionB, Quaternion rotationB) =>
        Vector3.Distance(positionA, positionB) < PositionEpsilon &&
        AngleBetween(rotationA, rotationB) < AngleEpsilon;

    /// <summary>The angle of the rotation between two orientations, in radians (0 to π).</summary>
    public static float AngleBetween(Quaternion a, Quaternion b)
    {
        // In double precision: in float, angles below about 1e-3 would read as 0.
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W;
        double bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        var lengths = Math.Sqrt((ax * ax + ay * ay + az * az + aw * aw) * (bx * bx + by * by + bz * bz + bw * bw));
        if (!(lengths > 1e-12))
            return MathF.PI;
        var dot = Math.Abs(ax * bx + ay * by + az * bz + aw * bw) / lengths;
        return (float)(2 * Math.Acos(Math.Min(1.0, dot)));
    }

    public static float WrapAngle(float radians)
    {
        var wrapped = MathF.IEEERemainder(radians, 2 * MathF.PI);
        return float.IsFinite(wrapped) ? wrapped : 0f;
    }
}
