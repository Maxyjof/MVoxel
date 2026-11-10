using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 沿曲线摆放体素源：按弧长取站点，按朝向旋转体素，可选贴地
/// </summary>
/// <remarks>
/// 为避免任意角度旋转在整数格上出现空洞，这里用“目标格反查”的方式重采样：
/// 先按旋转后的包围盒遍历每个目标格，再反变换回源坐标取最近格。
/// 源的朝向约定为局部 +Z 指向切线方向、+Y 向上，与常见道具的建模习惯一致
/// </remarks>
public static class SplineScatter
{
    /// <summary>单个站点旋转后允许遍历的包围格数上限，防止异常参数把界面拖死</summary>
    private const long MaximumStationCells = 4_000_000;

    /// <summary>贴地向下探测的最大格数</summary>
    private const int MaximumGroundProbe = 256;

    /// <summary>单次生成的站点上限，避免超长曲线耗尽时间或内存</summary>
    private const int MaximumStations = 4096;

    /// <summary>
    /// 按曲线参数生成摆放后的体素，坐标是对象局部坐标
    /// </summary>
    /// <param name="data">曲线与摆放参数</param>
    /// <param name="source">体素源，缺失时返回空结果并给出原因</param>
    /// <param name="context">生成上下文，贴地时需要场景采样</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="truncated">是否因预算截断</param>
    /// <param name="message">失败或降级原因</param>
    /// <param name="stationCount">计算出的站点数量</param>
    /// <param name="placedCount">实际放下物件的站点数量</param>
    /// <returns>生成结果</returns>
    public static VoxelWorld Generate(CurveRendererComponentData data, VoxelSource? source,
        GeneratedVoxelContext? context, int maximumVoxels, out bool truncated, out string message,
        out int stationCount, out int placedCount)
    {
        truncated = false;
        message = string.Empty;
        stationCount = 0;
        placedCount = 0;
        var world = new VoxelWorld();
        if (source is null || !source.IsValid)
        {
            message = string.IsNullOrWhiteSpace(source?.Error) ? "未指定体素源" : source.Error;
            return world;
        }

        var samples = new List<CurveSample>();
        CurveSpline.BuildSamples(data, data.Resolution, samples);
        // 去掉分段接缝处重复的采样点，避免弧长累计出现零长度段
        var cleaned = new List<CurveSample>(samples.Count);
        foreach (var sample in samples)
        {
            if (cleaned.Count > 0 && Vector3.DistanceSquared(cleaned[^1].Position, sample.Position) < 0.0001F)
                continue;
            cleaned.Add(sample);
        }

        if (cleaned.Count < 2)
        {
            message = "曲线太短，至少需要两个有效采样点";
            return world;
        }

        var lengths = new float[cleaned.Count];
        for (var index = 1; index < cleaned.Count; index++)
            lengths[index] = lengths[index - 1] + Vector3.Distance(cleaned[index - 1].Position, cleaned[index].Position);
        var totalLength = lengths[^1];
        var start = Math.Clamp(data.StartOffset, 0F, totalLength);
        var end = Math.Clamp(totalLength - data.EndOffset, start, totalLength);
        if (end - start < 0.01F)
        {
            message = "起点与终点之间的曲线太短，无法摆放";
            return world;
        }

        var stations = new List<float>();
        var wrap = data.Closed && data.ClosedWrap && start == 0F && end == totalLength;
        var limitedStations = false;
        if (data.SpacingMode == ScatterSpacingMode.Count)
        {
            var count = Math.Clamp(data.ScatterCount, 1, MaximumStations);
            if (count == 1) stations.Add(wrap ? start : (start + end) * 0.5F);
            else
                for (var index = 0; index < count; index++)
                    stations.Add(start + (end - start) * index / (wrap ? count : count - 1F));
        }
        else if (wrap)
        {
            var desiredCount = Math.Max(1D, Math.Round((end - start) / MathF.Max(0.5F, data.Spacing)));
            limitedStations = desiredCount > MaximumStations;
            var count = (int)Math.Min(desiredCount, MaximumStations);
            for (var index = 0; index < count; index++)
                stations.Add(start + (end - start) * index / (float)desiredCount);
        }
        else
        {
            var step = MathF.Max(0.5F, data.Spacing);
            var desiredCount = Math.Floor(((double)end - start + 0.001D) / step) + 1D;
            limitedStations = desiredCount > MaximumStations;
            var count = (int)Math.Min(desiredCount, MaximumStations);
            for (var index = 0; index < count; index++) stations.Add(start + index * step);
        }

        if (limitedStations)
        {
            truncated = true;
            message = $"超过 {MaximumStations:N0} 个站点上限，结果已截断";
        }

        if (stations.Count == 0)
        {
            message = "没有可摆放的站点，请检查间距与起止留空";
            return world;
        }

        stationCount = stations.Count;

        // 源体素只枚举一次，所有站点共用；字典查找避免每个目标格都线性扫一遍源
        var sourceCells = new Dictionary<(int X, int Y, int Z), VoxelValue>();
        foreach (var (coordinate, value) in source.World.EnumerateOccupied())
            sourceCells[((int)coordinate.X, (int)coordinate.Y, (int)coordinate.Z)] = value;
        var sourceSize = new Vector3(source.Size.X, source.Size.Y, source.Size.Z);
        var pivot = data.Pivot == ScatterPivot.Bottom
            ? new Vector3(sourceSize.X * 0.5F, 0F, sourceSize.Z * 0.5F)
            : sourceSize * 0.5F;
        var writer = world.CreateBulkWriter();
        var cellCount = 0;
        var firstStation = Vector3.Zero;
        for (var stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            var distance = stations[stationIndex];
            SampleAt(cleaned, lengths, distance, out var position, out var forward, out var up);
            var seed = (ulong)data.ScatterSeed;
            var jitterDistance = data.JitterDistance > 0F
                ? (float)(DeterministicRandom.Mix(seed, stationIndex, 1, 21) % 2001) / 1000F - 1F
                : 0F;
            var jitterAngle = data.JitterAngle > 0F
                ? ((float)(DeterministicRandom.Mix(seed, stationIndex, 2, 22) % 2001) / 1000F - 1F) * data.JitterAngle
                : 0F;
            if (jitterDistance != 0F) position += forward * (jitterDistance * data.JitterDistance);

            var rotation = BuildRotation(data, forward, up, jitterAngle);
            var stationPosition = position;
            if (data.GroundSnap)
            {
                if (context?.Surface is null)
                {
                    message = "贴地需要场景采样，当前没有可读取的地面";
                    return new VoxelWorld();
                }

                var probe = new VoxelCoord64(
                    (long)MathF.Floor(stationPosition.X + context.ObjectOrigin.X),
                    (long)MathF.Floor(stationPosition.Y + context.ObjectOrigin.Y),
                    (long)MathF.Floor(stationPosition.Z + context.ObjectOrigin.Z));
                if (!context.Surface.TryHit(probe, new VoxelCoord64(0, -1, 0), MaximumGroundProbe, out var ground))
                    continue;
                stationPosition.Y = ground.Y + 1 - context.ObjectOrigin.Y + data.GroundOffset;
            }

            if (stationIndex == 0) firstStation = stationPosition;
            if (!TryPlaceStation(sourceCells, sourceSize, pivot, stationPosition, rotation, writer,
                    maximumVoxels, ref cellCount, out _)) continue;
            placedCount++;
            if (cellCount < maximumVoxels) continue;
            truncated = true;
            message = $"超过 {maximumVoxels:N0} 格上限，结果已截断";
            break;
        }

        if (placedCount == 0 && message.Length == 0)
            message = $"没有放下任何物件：源 {sourceCells.Count} 格，尺寸 {sourceSize.X:0}×{sourceSize.Y:0}×{sourceSize.Z:0}，" +
                      $"首站点 {firstStation.X:0.##},{firstStation.Y:0.##},{firstStation.Z:0.##}";
        return world;
    }

    /// <summary>
    /// 按弧长取插值站点：位置线性插值，朝向用线性插值后再正交化
    /// </summary>
    private static void SampleAt(IReadOnlyList<CurveSample> samples, float[] lengths, float distance,
        out Vector3 position, out Vector3 forward, out Vector3 up)
    {
        var index = 1;
        while (index < lengths.Length - 1 && lengths[index] < distance) index++;
        var previous = index - 1;
        var span = lengths[index] - lengths[previous];
        var t = span > 0.0001F ? Math.Clamp((distance - lengths[previous]) / span, 0F, 1F) : 0F;
        position = Vector3.Lerp(samples[previous].Position, samples[index].Position, t);
        forward = Vector3.Lerp(samples[previous].Forward, samples[index].Forward, t);
        up = Vector3.Lerp(samples[previous].Up, samples[index].Up, t);
        forward = forward.LengthSquared() < 0.0001F ? Vector3.UnitZ : Vector3.Normalize(forward);
        up = up.LengthSquared() < 0.0001F ? Vector3.UnitY : Vector3.Normalize(up);
    }

    /// <summary>
    /// 按对齐方式与滚转构造站点朝向
    /// </summary>
    private static Quaternion BuildRotation(CurveRendererComponentData data, Vector3 forward, Vector3 up, float jitterAngle)
    {
        var baseRotation = data.Align switch
        {
            ScatterAlign.Horizontal => FromForwardUp(new Vector3(forward.X, 0F, forward.Z), Vector3.UnitY),
            ScatterAlign.Fixed => Quaternion.Identity,
            _ => FromForwardUp(forward, up),
        };
        var roll = data.RollDegrees + jitterAngle;
        if (MathF.Abs(roll) < 0.0001F) return baseRotation;
        var axis = data.Align == ScatterAlign.Fixed ? Vector3.UnitY : Vector3.Transform(Vector3.UnitZ, baseRotation);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, roll * MathF.PI / 180F) * baseRotation);
    }

    /// <summary>
    /// 由前向与上方向构造旋转；退化时回落到单位旋转
    /// </summary>
    private static Quaternion FromForwardUp(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 0.0001F) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(up, forward);
        if (right.LengthSquared() < 0.0001F)
        {
            up = MathF.Abs(forward.Y) > 0.99F ? Vector3.UnitZ : Vector3.UnitY;
            right = Vector3.Cross(up, forward);
        }

        right = Vector3.Normalize(right);
        var correctedUp = Vector3.Cross(forward, right);
        var matrix = new Matrix4x4(
            right.X, right.Y, right.Z, 0F,
            correctedUp.X, correctedUp.Y, correctedUp.Z, 0F,
            forward.X, forward.Y, forward.Z, 0F,
            0F, 0F, 0F, 1F);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    /// <summary>
    /// 把一个源物件按站点位姿写入目标世界：遍历旋转后的包围格并反查源坐标
    /// </summary>
    /// <returns>是否写入了体素</returns>
    private static bool TryPlaceStation(Dictionary<(int X, int Y, int Z), VoxelValue> sourceCells,
        Vector3 sourceSize, Vector3 pivot, Vector3 stationPosition, Quaternion rotation, VoxelWorld.BulkWriter writer,
        int maximumVoxels, ref int cellCount, out int stationCells)
    {
        stationCells = 0;
        var inverse = Quaternion.Conjugate(rotation);
        // 用源的八个小角估算旋转后的范围，单位是相对支点的坐标
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? -pivot.X : sourceSize.X - pivot.X,
                (corner & 2) == 0 ? -pivot.Y : sourceSize.Y - pivot.Y,
                (corner & 4) == 0 ? -pivot.Z : sourceSize.Z - pivot.Z);
            var world = Vector3.Transform(local, rotation);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }

        var minX = (long)MathF.Floor(stationPosition.X + minimum.X);
        var minY = (long)MathF.Floor(stationPosition.Y + minimum.Y);
        var minZ = (long)MathF.Floor(stationPosition.Z + minimum.Z);
        var maxX = (long)MathF.Ceiling(stationPosition.X + maximum.X);
        var maxY = (long)MathF.Ceiling(stationPosition.Y + maximum.Y);
        var maxZ = (long)MathF.Ceiling(stationPosition.Z + maximum.Z);
        var volume = (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
        if (volume <= 0 || volume > MaximumStationCells) return false;

        var any = false;
        for (var z = minZ; z <= maxZ; z++)
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var centered = new Vector3(x + 0.5F, y + 0.5F, z + 0.5F) - stationPosition;
            var local = Vector3.Transform(centered, inverse) + pivot;
            var sx = (int)MathF.Floor(local.X);
            var sy = (int)MathF.Floor(local.Y);
            var sz = (int)MathF.Floor(local.Z);
            if (sx < 0 || sy < 0 || sz < 0 ||
                sx >= (int)sourceSize.X || sy >= (int)sourceSize.Y || sz >= (int)sourceSize.Z) continue;
            if (!sourceCells.TryGetValue((sx, sy, sz), out var value)) continue;
            writer.Set(new VoxelCoord64(x, y, z), value);
            any = true;
            stationCells++;
            cellCount++;
            if (cellCount >= maximumVoxels) return any;
        }

        return any || TryForwardPlacement(sourceCells, pivot, stationPosition, rotation, writer, maximumVoxels,
            ref cellCount, out stationCells);
    }

    /// <summary>
    /// 反查落空时的兜底：把源的每个体素中心正变换到世界再取整
    /// </summary>
    /// <remarks>
    /// 源在某个方向上很薄（例如 1 格厚的招牌）时，旋转后目标格反查可能全部落在源格外。
    /// 正向映射保证这类薄片也能完整放下，代价是任意角度下可能出现极少量格位错位
    /// </remarks>
    private static bool TryForwardPlacement(Dictionary<(int X, int Y, int Z), VoxelValue> sourceCells, Vector3 pivot,
        Vector3 stationPosition, Quaternion rotation, VoxelWorld.BulkWriter writer, int maximumVoxels,
        ref int cellCount, out int stationCells)
    {
        stationCells = 0;
        var any = false;
        foreach (var ((sx, sy, sz), value) in sourceCells)
        {
            var local = new Vector3(sx + 0.5F - pivot.X, sy + 0.5F - pivot.Y, sz + 0.5F - pivot.Z);
            var world = Vector3.Transform(local, rotation) + stationPosition;
            var cell = new VoxelCoord64((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y),
                (long)MathF.Floor(world.Z));
            writer.Set(cell, value);
            any = true;
            stationCells++;
            cellCount++;
            if (cellCount >= maximumVoxels) return any;
        }

        return any;
    }
}
