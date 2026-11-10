using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 模型浮点旋转的重新采样规则。体素永远落在整数格上，所以旋转不是把方块转成斜的，
/// 而是以“基准体素”为源、按旋转后的格心回采到整数格：结果始终是干净的体素形态，
/// 反复调整旋转都以基准为准，不会一层层糊掉。
/// </summary>
public static class ModelRotationRule
{
    /// <summary>单次旋转重采样允许扫描的最大包围盒格数，避免稀疏大模型拖拽卡死</summary>
    private const long MaximumSampleCells = 8_000_000;
    /// <summary>
    /// 按给定四元数对基准模型重新采样
    /// </summary>
    /// <param name="source">基准模型，坐标已在世界格上</param>
    /// <param name="rotation">绕模型几何中心的旋转</param>
    /// <param name="anchor">结果要对齐到的包围盒最小角</param>
    /// <param name="result">采样结果</param>
    /// <param name="error">失败原因</param>
    /// <returns>成功时为 true</returns>
    public static bool TryResample(VoxelWorld source, Quaternion rotation, VoxelCoord64 anchor, out VoxelWorld result,
        out string error) =>
        TryResample(source, null, rotation, anchor, out result, out error);

    /// <summary>
    /// 建立基准模型的坐标到体素值索引，拖动预览反复重采样时复用同一份索引
    /// </summary>
    /// <param name="source">基准模型</param>
    /// <returns>坐标索引</returns>
    public static Dictionary<(long X, long Y, long Z), VoxelValue> CreateLookup(VoxelWorld source)
    {
        var lookup = new Dictionary<(long X, long Y, long Z), VoxelValue>(source.OccupiedVoxelCount);
        foreach (var cell in source.EnumerateOccupied())
            lookup[(cell.Coordinate.X, cell.Coordinate.Y, cell.Coordinate.Z)] = cell.Value;
        return lookup;
    }

    /// <summary>
    /// 按给定四元数对基准模型重新采样，可复用预先建好的坐标索引
    /// </summary>
    /// <param name="source">基准模型，坐标已在世界格上</param>
    /// <param name="lookup">基准模型坐标索引；为空时在本次调用里建立</param>
    /// <param name="rotation">绕模型几何中心的旋转</param>
    /// <param name="anchor">结果要对齐到的包围盒最小角</param>
    /// <param name="result">采样结果</param>
    /// <param name="error">失败原因</param>
    /// <returns>成功时为 true</returns>
    public static bool TryResample(VoxelWorld source, IReadOnlyDictionary<(long X, long Y, long Z), VoxelValue>? lookup,
        Quaternion rotation, VoxelCoord64 anchor, out VoxelWorld result, out string error)
    {
        result = new VoxelWorld();
        error = string.Empty;
        if (!source.TryGetBounds(out var minimum, out var maximum))
        {
            error = "模型为空，无法旋转";
            return false;
        }

        var center = new Vector3(
            (minimum.X + maximum.X + 1) * .5F,
            (minimum.Y + maximum.Y + 1) * .5F,
            (minimum.Z + maximum.Z + 1) * .5F);
        var inverse = Quaternion.Normalize(Quaternion.Inverse(rotation));
        lookup ??= CreateLookup(source);

        // 旋转后的包围盒用 8 个角点估算，保证扫到的格子足够覆盖结果
        var minimumCorner = new Vector3(minimum.X, minimum.Y, minimum.Z);
        var maximumCorner = new Vector3(maximum.X + 1, maximum.Y + 1, maximum.Z + 1);
        var minimumRotated = new Vector3(float.MaxValue);
        var maximumRotated = new Vector3(float.MinValue);
        for (var index = 0; index < 8; index++)
        {
            var corner = new Vector3(
                (index & 1) == 0 ? minimumCorner.X : maximumCorner.X,
                (index & 2) == 0 ? minimumCorner.Y : maximumCorner.Y,
                (index & 4) == 0 ? minimumCorner.Z : maximumCorner.Z);
            var rotated = Vector3.Transform(corner - center, rotation) + center;
            minimumRotated = Vector3.Min(minimumRotated, rotated);
            maximumRotated = Vector3.Max(maximumRotated, rotated);
        }

        var startX = (long)MathF.Floor(minimumRotated.X);
        var startY = (long)MathF.Floor(minimumRotated.Y);
        var startZ = (long)MathF.Floor(minimumRotated.Z);
        var endX = (long)MathF.Ceiling(maximumRotated.X);
        var endY = (long)MathF.Ceiling(maximumRotated.Y);
        var endZ = (long)MathF.Ceiling(maximumRotated.Z);
        var sizeX = endX - startX + 1;
        var sizeY = endY - startY + 1;
        var sizeZ = endZ - startZ + 1;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0 ||
            sizeX > MaximumSampleCells || sizeY > MaximumSampleCells / sizeX ||
            sizeZ > MaximumSampleCells / (sizeX * sizeY))
        {
            error = $"旋转后的包围盒超过 {MaximumSampleCells:N0} 格采样上限；请先拆分模型或缩小模型范围";
            return false;
        }

        var sampled = new VoxelWorld();
        for (var x = startX; x <= endX; x++)
        for (var y = startY; y <= endY; y++)
        for (var z = startZ; z <= endZ; z++)
        {
            var worldCenter = new Vector3(x + .5F, y + .5F, z + .5F);
            var local = Vector3.Transform(worldCenter - center, inverse) + center;
            var sourceCell = ((long)MathF.Floor(local.X), (long)MathF.Floor(local.Y), (long)MathF.Floor(local.Z));
            if (lookup.TryGetValue(sourceCell, out var value)) sampled.Set(new VoxelCoord64(x, y, z), value);
        }

        if (sampled.OccupiedVoxelCount == 0)
        {
            error = "旋转后模型为空，请减小角度";
            return false;
        }

        result = ModelScaleRule.Align(sampled, anchor);
        return true;
    }
}
