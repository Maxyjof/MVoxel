using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// CPU DDA (Amanatides &amp; Woo) over the sparse world. The editor uses it for
/// mouse picking; the GPU pipeline implements the same traversal in GLSL. Keeping
/// a managed reference version means picking, tests and shaders share semantics.
/// </summary>
public static class VoxelDda
{
    /// <param name="Coordinate">Occupied cell that was hit.</param>
    /// <param name="Normal">Unit face normal of the entry face (one axis, ±1).</param>
    /// <param name="Distance">Travelled distance in ray-direction units.</param>
    /// <param name="Point">World-space hit point.</param>
    public readonly record struct Hit(VoxelCoord64 Coordinate, VoxelCoord64 Normal, float Distance, Vector3 Point);

    /// <summary>First occupied voxel along the ray, or null when nothing is hit.</summary>
    public static Hit? RayCast(VoxelWorld world, Vector3 origin, Vector3 direction, float maximumDistance = 512F)
    {
        if (Vector3.Dot(direction, direction) < 1e-12F || maximumDistance <= 0F) return null;
        var step = Vector3.Normalize(direction);

        var cell = new VoxelCoord64(
            (long)MathF.Floor(origin.X), (long)MathF.Floor(origin.Y), (long)MathF.Floor(origin.Z));
        var delta = new Vector3(
            ReciprocalDelta(step.X), ReciprocalDelta(step.Y), ReciprocalDelta(step.Z));
        var next = new Vector3(
            BoundaryDistance(origin.X, cell.X, step.X),
            BoundaryDistance(origin.Y, cell.Y, step.Y),
            BoundaryDistance(origin.Z, cell.Z, step.Z));
        var sign = (X: (int)MathF.Sign(step.X), Y: (int)MathF.Sign(step.Y), Z: (int)MathF.Sign(step.Z));

        var travelled = 0F;
        var normal = InitialNormal(step);
        // The budget guards against pathological rays crossing huge empty regions.
        var stepsLeft = (int)Math.Clamp(maximumDistance * 4F, 256F, 250_000F);
        while (stepsLeft-- > 0)
        {
            if (world.TryGet(cell, out var value) && !value.IsEmpty)
            {
                return new Hit(cell, normal, travelled, origin + step * travelled);
            }

            if (next.X <= next.Y && next.X <= next.Z)
            {
                travelled = next.X;
                next.X += delta.X;
                cell = cell with { X = cell.X + sign.X };
                normal = new VoxelCoord64(-sign.X, 0, 0);
            }
            else if (next.Y <= next.Z)
            {
                travelled = next.Y;
                next.Y += delta.Y;
                cell = cell with { Y = cell.Y + sign.Y };
                normal = new VoxelCoord64(0, -sign.Y, 0);
            }
            else
            {
                travelled = next.Z;
                next.Z += delta.Z;
                cell = cell with { Z = cell.Z + sign.Z };
                normal = new VoxelCoord64(0, 0, -sign.Z);
            }

            if (travelled > maximumDistance) return null;
        }

        return null;
    }

    /// <summary>
    /// Intersects a ray with the infinite ground plane (y = 0). Used when a pick
    /// misses all voxels so the first brush stroke can land on empty canvas.
    /// </summary>
    public static bool TryGroundHit(Vector3 origin, Vector3 direction, float maximumDistance, out Vector3 point)
    {
        point = default;
        if (Math.Abs(direction.Y) < 1e-7F) return false;
        var t = -origin.Y / direction.Y;
        if (t is <= 0F or > float.MaxValue / 2F || t > maximumDistance) return false;
        point = origin + Vector3.Normalize(direction) * t;
        return true;
    }

    private static float ReciprocalDelta(float step) =>
        Math.Abs(step) < 1e-7F ? float.PositiveInfinity : Math.Abs(1F / step);

    private static float BoundaryDistance(float axisOrigin, long cellIndex, float step)
    {
        if (Math.Abs(step) < 1e-7F) return float.PositiveInfinity;
        var boundary = step > 0 ? cellIndex + 1F : cellIndex;
        return Math.Abs((boundary - axisOrigin) / step);
    }

    /// <summary>
    /// When the ray starts inside a voxel there was no entry face; fall back to
    /// the face most directly facing the camera along the dominant axis.
    /// </summary>
    private static VoxelCoord64 InitialNormal(Vector3 step)
    {
        var (x, y, z) = (Math.Abs(step.X), Math.Abs(step.Y), Math.Abs(step.Z));
        if (x >= y && x >= z) return new VoxelCoord64(-(long)MathF.Sign(step.X), 0, 0);
        if (y >= z) return new VoxelCoord64(0, -(long)MathF.Sign(step.Y), 0);
        return new VoxelCoord64(0, 0, -(long)MathF.Sign(step.Z));
    }
}
