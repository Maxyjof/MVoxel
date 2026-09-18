using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels.Mesh;

/// <summary>
/// 三角网格体素化器：网格按包围盒等比缩放贴合目标体素网格。
/// 表面用主轴投影栅格化（每格 2×2 次采样，保证斜面容差）；
/// 实体模式再按列做扫描线奇偶填充：列内收集所有三角形与列中心轴的交点，
/// 排序后成对填充，天然处理凹形与多层结构。列间并行、结果确定性合并
/// </summary>
internal static class MeshVoxelizer
{
    /// <summary>单轴最大分辨率与总体素上限，防止超大网格拖垮编辑器</summary>
    internal const int MaximumResolution = 256;

    internal const long MaximumVoxels = 8_000_000;

    /// <summary>
    /// 执行体素化
    /// </summary>
    /// <param name="mesh">已通过校验的三角网格</param>
    /// <param name="value">写入的体素值（颜色、材质槽与标志）</param>
    /// <param name="resolution">最长轴对齐的目标体素数，2 到 256</param>
    /// <param name="fillInterior">true 为实体填充，false 仅表面壳</param>
    /// <returns>新体素世界，模型占据从原点起的紧凑包围盒</returns>
    internal static VoxelWorld Voxelize(MeshData mesh, VoxelValue value, int resolution, bool fillInterior)
    {
        var target = Math.Clamp(resolution, 2, MaximumResolution);
        var min = mesh.BoundsMin;
        var max = mesh.BoundsMax;
        var extent = max - min;
        var maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        var voxelSize = maxExtent / target;
        var sizeX = Math.Max(1, (int)Math.Ceiling(extent.X / voxelSize));
        var sizeY = Math.Max(1, (int)Math.Ceiling(extent.Y / voxelSize));
        var sizeZ = Math.Max(1, (int)Math.Ceiling(extent.Z / voxelSize));

        var occupied = new HashSet<long>();
        BuildSurface(mesh, min, voxelSize, sizeX, sizeY, sizeZ, occupied);
        if (fillInterior)
        {
            BuildSolid(mesh, min, voxelSize, sizeX, sizeY, sizeZ, occupied);
        }

        if (occupied.Count > MaximumVoxels)
            throw new InvalidOperationException(
                $"体素化结果 {occupied.Count:N0} 个体素超过上限 {MaximumVoxels:N0}，请降低分辨率");

        var world = new VoxelWorld();
        foreach (var packed in occupied)
        {
            var coordinate = Unpack(packed);
            world.Set(coordinate, value);
        }

        return world;
    }

    private static void BuildSurface(MeshData mesh, Vector3 min, float voxelSize,
        int sizeX, int sizeY, int sizeZ, HashSet<long> occupied)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        Parallel.For(0, mesh.TriangleCount, facet =>
        {
            var local = new HashSet<long>();
            var v0 = Vertex(positions, indices[facet * 3], min, voxelSize);
            var v1 = Vertex(positions, indices[facet * 3 + 1], min, voxelSize);
            var v2 = Vertex(positions, indices[facet * 3 + 2], min, voxelSize);
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            var abs = new Vector3(MathF.Abs(normal.X), MathF.Abs(normal.Y), MathF.Abs(normal.Z));
            // 主轴投影：法线分量最大的轴为丢弃轴，保证投影面积最大、采样最稳
            int drop;
            int u;
            int v;
            if (abs.X >= abs.Y && abs.X >= abs.Z) (drop, u, v) = (0, 1, 2);
            else if (abs.Y >= abs.Z) (drop, u, v) = (1, 0, 2);
            else (drop, u, v) = (2, 0, 1);
            var limitU = AxisLimit((sizeX, sizeY, sizeZ), u) - 1;
            var limitV = AxisLimit((sizeX, sizeY, sizeZ), v) - 1;
            var minU = Math.Clamp((int)MathF.Floor(Math.Min(Component(v0, u), Math.Min(Component(v1, u), Component(v2, u)))), 0, limitU);
            var maxU = Math.Clamp((int)MathF.Ceiling(Math.Max(Component(v0, u), Math.Max(Component(v1, u), Component(v2, u)))), 0, limitU);
            var minV = Math.Clamp((int)MathF.Floor(Math.Min(Component(v0, v), Math.Min(Component(v1, v), Component(v2, v)))), 0, limitV);
            var maxV = Math.Clamp((int)MathF.Ceiling(Math.Max(Component(v0, v), Math.Max(Component(v1, v), Component(v2, v)))), 0, limitV);

            for (var i = minU; i <= maxU; i++)
            for (var j = minV; j <= maxV; j++)
            {
                // 每格 2×2 次上采样：斜三角形不会在格中心漏采样，壳连续
                for (var sub = 0; sub < 4; sub++)
                {
                    var sampleU = i + (sub & 1) * 0.5F + 0.25F;
                    var sampleV = j + (sub >> 1) * 0.5F + 0.25F;
                    if (!Barycentric(v0, v1, v2, u, v, sampleU, sampleV, out var weight))
                        continue;
                    var point = v0 * weight.X + v1 * weight.Y + v2 * weight.Z;
                    var cellI = Math.Clamp((int)MathF.Floor(point.X), 0, sizeX - 1);
                    var cellJ = Math.Clamp((int)MathF.Floor(point.Y), 0, sizeY - 1);
                    var cellK = Math.Clamp((int)MathF.Floor(point.Z), 0, sizeZ - 1);
                    local.Add(Pack(cellI, cellJ, cellK));
                }
            }

            lock (occupied)
            {
                occupied.UnionWith(local);
            }
        });
    }

    private static void BuildSolid(MeshData mesh, Vector3 min, float voxelSize,
        int sizeX, int sizeY, int sizeZ, HashSet<long> occupied)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        // 按 xy 投影包围盒把三角形装进列桶，扫描线只测本桶三角形
        var buckets = new List<int>[sizeX * sizeY];
        for (var facet = 0; facet < mesh.TriangleCount; facet++)
        {
            var v0 = Vertex(positions, indices[facet * 3], min, voxelSize);
            var v1 = Vertex(positions, indices[facet * 3 + 1], min, voxelSize);
            var v2 = Vertex(positions, indices[facet * 3 + 2], min, voxelSize);
            var minX = Math.Max(0, (int)MathF.Floor(Math.Min(v0.X, Math.Min(v1.X, v2.X))));
            var maxX = Math.Min(sizeX - 1, (int)MathF.Ceiling(Math.Max(v0.X, Math.Max(v1.X, v2.X))));
            var minY = Math.Max(0, (int)MathF.Floor(Math.Min(v0.Y, Math.Min(v1.Y, v2.Y))));
            var maxY = Math.Min(sizeY - 1, (int)MathF.Ceiling(Math.Max(v0.Y, Math.Max(v1.Y, v2.Y))));
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
            {
                ref var bucket = ref buckets[x + y * sizeX];
                bucket ??= new List<int>();
                bucket.Add(facet);
            }
        }

        Parallel.For(0, sizeY, y =>
        {
            var local = new HashSet<long>();
            var crossings = new List<float>(16);
            for (var x = 0; x < sizeX; x++)
            {
                var bucket = buckets[x + y * sizeX];
                if (bucket is null or { Count: 0 })
                    continue;
                crossings.Clear();
                // 抖动 0.1% 体素的确定性偏移：列中心恰踩三角形共享边或双面重合面时
                // 交点重数退化，奇偶配对会错位漏填整段；抖动保证同一列永远同一结果
                var sampleX = x + 0.5F + 0.0013F * MathF.Sin(x * 12.9898F + 0.123F);
                var sampleY = y + 0.5F + 0.0017F * MathF.Sin(y * 78.233F + 0.456F);
                foreach (var facet in bucket)
                {
                    var v0 = Vertex(positions, indices[facet * 3], min, voxelSize);
                    var v1 = Vertex(positions, indices[facet * 3 + 1], min, voxelSize);
                    var v2 = Vertex(positions, indices[facet * 3 + 2], min, voxelSize);
                    if (Barycentric(v0, v1, v2, 0, 1, sampleX, sampleY, out var weight))
                        crossings.Add(v0.Z * weight.X + v1.Z * weight.Y + v2.Z * weight.Z);
                }

                if (crossings.Count < 2)
                    continue;
                crossings.Sort();
                // 奇偶配对填充：相邻交点之间为实体内部
                for (var pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    var zStart = Math.Max(0, (int)MathF.Floor(crossings[pair]));
                    var zEnd = Math.Min(sizeZ - 1, (int)MathF.Ceiling(crossings[pair + 1]));
                    for (var z = zStart; z <= zEnd; z++)
                        local.Add(Pack(x, y, z));
                }
            }

            lock (occupied)
            {
                occupied.UnionWith(local);
            }
        });
    }

    /// <summary>
    /// 世界顶点换算到体素格坐标系（以格为单位的浮点坐标）
    /// </summary>
    private static Vector3 Vertex(float[] positions, int index, Vector3 min, float voxelSize) =>
        new((positions[index * 3] - min.X) / voxelSize,
            (positions[index * 3 + 1] - min.Y) / voxelSize,
            (positions[index * 3 + 2] - min.Z) / voxelSize);

    private static float Component(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private static int AxisLimit((int X, int Y, int Z) limits, int axis) => axis switch
    {
        0 => limits.X,
        1 => limits.Y,
        _ => limits.Z,
    };

    /// <summary>
    /// 投影平面内的同侧法重心测试，命中时输出三顶点权重
    /// </summary>
    private static bool Barycentric(Vector3 v0, Vector3 v1, Vector3 v2, int u, int v,
        float sampleU, float sampleV, out Vector3 weight)
    {
        weight = default;
        float Edge(float ax, float ay, float bx, float by, float px, float py) =>
            (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        var e0 = Edge(Component(v0, u), Component(v0, v), Component(v1, u), Component(v1, v), sampleU, sampleV);
        var e1 = Edge(Component(v1, u), Component(v1, v), Component(v2, u), Component(v2, v), sampleU, sampleV);
        var e2 = Edge(Component(v2, u), Component(v2, v), Component(v0, u), Component(v0, v), sampleU, sampleV);
        var positive = e0 >= 0 && e1 >= 0 && e2 >= 0;
        var negative = e0 <= 0 && e1 <= 0 && e2 <= 0;
        if (!positive && !negative)
            return false;
        if (negative)
        {
            e0 = -e0;
            e1 = -e1;
            e2 = -e2;
        }

        var total = e0 + e1 + e2;
        if (total < 1e-9F)
            return false;
        // e1 是 P 与边 v1v2 构成的面积，对应 v0 的重心权重；e2 对 v1，e0 对 v2
        weight = new Vector3(e1 / total, e2 / total, e0 / total);
        return true;
    }

    private static long Pack(int x, int y, int z) =>
        ((long)z << 42) | ((long)y << 21) | (uint)x;

    private static VoxelCoord64 Unpack(long packed) =>
        new((int)(packed & 0x1FFFFF), (int)((packed >> 21) & 0x1FFFFF), (int)(packed >> 42));
}
