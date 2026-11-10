using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 瓷砖局部格与世界格之间的采样变换
/// </summary>
internal readonly struct TileSpaceTransform
{
    private readonly VoxelCoord64 _origin;
    private readonly Quaternion _rotation;
    private readonly Quaternion _inverseRotation;
    private readonly Vector3 _scale;

    private TileSpaceTransform(VoxelCoord64 origin, Quaternion rotation, Vector3 scale)
    {
        _origin = origin;
        _rotation = rotation;
        _inverseRotation = Quaternion.Conjugate(rotation);
        _scale = scale;
    }

    /// <summary>
    /// 验证变换并建立采样空间
    /// </summary>
    public static bool TryCreate(GeneratedVoxelContext context, out TileSpaceTransform transform)
    {
        transform = default;
        var rotation = context.ObjectRotation;
        var length = rotation.LengthSquared();
        var scale = context.ObjectScale;
        if (!float.IsFinite(length) || length < 0.000001F ||
            !float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z) ||
            scale.X <= 0F || scale.Y <= 0F || scale.Z <= 0F ||
            scale.X > 64F || scale.Y > 64F || scale.Z > 64F) return false;
        transform = new TileSpaceTransform(context.ObjectOrigin, Quaternion.Normalize(rotation), scale);
        return true;
    }

    /// <summary>
    /// 沿局部方向采样世界体素，返回局部命中格
    /// </summary>
    public bool TryHit(SceneSurfaceSampler surface, VoxelCoord64 start, VoxelCoord64 direction,
        int maxSteps, out VoxelCoord64 hit)
    {
        var axisScale = direction.X != 0 ? _scale.X : direction.Y != 0 ? _scale.Y : _scale.Z;
        var subdivisions = Math.Max(1, (int)MathF.Ceiling(axisScale * 2F));
        for (var step = 0; step < maxSteps; step++)
        {
            var cell = start + new VoxelCoord64(direction.X * step, direction.Y * step, direction.Z * step);
            for (var sub = 0; sub < subdivisions; sub++)
            {
                var offset = (sub + .5F) / subdivisions - .5F;
                var point = new Vector3(cell.X + .5F + direction.X * offset,
                    cell.Y + .5F + direction.Y * offset, cell.Z + .5F + direction.Z * offset);
                if (!surface.TryGet(ToWorldCell(TransformPoint(point)), out _)) continue;
                hit = cell;
                return true;
            }
        }
        hit = start;
        return false;
    }

    /// <summary>
    /// 把一个局部立方格重采样为世界格，薄格无格心命中时保留其中心
    /// </summary>
    public IEnumerable<VoxelCoord64> RasterizeCell(VoxelCoord64 cell)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var p = new Vector3(cell.X + ((corner & 1) == 0 ? 0F : 1F),
                cell.Y + ((corner & 2) == 0 ? 0F : 1F), cell.Z + ((corner & 4) == 0 ? 0F : 1F));
            var transformed = TransformPoint(p);
            minimum = Vector3.Min(minimum, transformed);
            maximum = Vector3.Max(maximum, transformed);
        }
        var minX = FloorStable(minimum.X); var maxX = FloorStable(maximum.X);
        var minY = FloorStable(minimum.Y); var maxY = FloorStable(maximum.Y);
        var minZ = FloorStable(minimum.Z); var maxZ = FloorStable(maximum.Z);
        var found = false;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var local = Vector3.Transform(new Vector3(x + .5F, y + .5F, z + .5F), _inverseRotation) / _scale;
            if (FloorStable(local.X) != cell.X || FloorStable(local.Y) != cell.Y || FloorStable(local.Z) != cell.Z)
                continue;
            found = true;
            yield return new VoxelCoord64(_origin.X + x, _origin.Y + y, _origin.Z + z);
        }
        if (!found)
            yield return ToWorldCell(TransformPoint(new Vector3(cell.X + .5F, cell.Y + .5F, cell.Z + .5F)));
    }

    private Vector3 TransformPoint(Vector3 local) => Vector3.Transform(local * _scale, _rotation);

    private VoxelCoord64 ToWorldCell(Vector3 relative) => new(
        _origin.X + FloorStable(relative.X), _origin.Y + FloorStable(relative.Y), _origin.Z + FloorStable(relative.Z));

    private static long FloorStable(float value)
    {
        var rounded = MathF.Round(value);
        return (long)MathF.Floor(MathF.Abs(value - rounded) < .00001F ? rounded : value);
    }
}
