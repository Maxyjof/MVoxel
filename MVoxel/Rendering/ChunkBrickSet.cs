using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 区块级 Brick 化构建器：把一个 32³ 世界区块重排为 64 个 8³ brick 的格序列，
/// 供 GPU 增量上传按"区块对齐"局部重写，不动其他区块的缓冲区间
/// 排列规则：brick 按 (Z, Y, X) 枚举，每个 brick 内占用格按全局 (Z, Y, X) 升序排前，
/// 与全量路径的格排序口径一致，保证着色器侧求交结果确定
/// </summary>
public static class ChunkBrickSet
{
    /// <summary>
    /// brick 边长（格数）
    /// </summary>
    public const int BrickSize = 8;

    /// <summary>
    /// 每个区块的 brick 数量（4×4×4）
    /// </summary>
    public const int BricksPerChunk = 64;

    /// <summary>
    /// 单个区块的 brick 化数据
    /// </summary>
    /// <param name="Cells">全部占用格（VoxelGpuCell，坐标相对工作集锚点，_extent 恒为 1）</param>
    /// <param name="BrickStarts">64 项：每个 brick 在 Cells 中的起始下标</param>
    /// <param name="BrickCounts">64 项：每个 brick 的占用格数</param>
    public sealed record BrickChunkData(VoxelGpuCell[] Cells, int[] BrickStarts, int[] BrickCounts)
    {
        /// <summary>
        /// 全部 brick 的占用格总数（等于 Cells 长度）
        /// </summary>
        public int OccupiedCount => Cells.Length;
    }

    /// <summary>
    /// 为单个区块构建 brick 化数据；空区块返回零格结果
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    /// <param name="chunk">稀疏区块内容</param>
    /// <param name="anchor">工作集锚点（包围盒最小角的世界坐标），输出格坐标相对它</param>
    /// <returns>brick 化数据</returns>
    public static BrickChunkData Build(ChunkCoord64 coordinate, SparseVoxelChunk chunk, VoxelCoord64 anchor)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var starts = new int[BricksPerChunk];
        var counts = new int[BricksPerChunk];
        if (chunk.OccupiedCount == 0)
        {
            return new BrickChunkData([], starts, counts);
        }

        // 第一遍：按 brick 桶计数，得到每个 brick 在输出数组中的起始偏移
        var pairs = new (int Key, VoxelValue Value)[chunk.OccupiedCount];
        var pairIndex = 0;
        foreach (var pair in chunk.Snapshot)
        {
            pairs[pairIndex++] = (pair.Key, pair.Value);
        }

        var brickOfCell = new int[pairs.Length];
        for (var index = 0; index < pairs.Length; index++)
        {
            var local = SparseVoxelChunk.FromIndex(pairs[index].Key);
            brickOfCell[index] = BrickIndexOf(local);
            counts[brickOfCell[index]]++;
        }

        var cursor = 0;
        for (var brick = 0; brick < BricksPerChunk; brick++)
        {
            starts[brick] = cursor;
            cursor += counts[brick];
        }

        var writeCursors = new int[BricksPerChunk];
        Array.Copy(starts, writeCursors, BricksPerChunk);

        // 第二遍：全局 (Z, Y, X) 升序遍历，天然把每个 brick 内的格排成稳定序
        var cells = new VoxelGpuCell[pairs.Length];
        Array.Sort(pairs, static (left, right) =>
        {
            var leftLocal = SparseVoxelChunk.FromIndex(left.Key);
            var rightLocal = SparseVoxelChunk.FromIndex(right.Key);
            var byZ = leftLocal.Z.CompareTo(rightLocal.Z);
            if (byZ != 0) return byZ;
            var byY = leftLocal.Y.CompareTo(rightLocal.Y);
            if (byY != 0) return byY;
            return leftLocal.X.CompareTo(rightLocal.X);
        });
        foreach (var (key, value) in pairs)
        {
            var local = SparseVoxelChunk.FromIndex(key);
            var brick = BrickIndexOf(local);
            cells[writeCursors[brick]++] = new VoxelGpuCell(
                (int)(coordinate.X * VoxelWorld.ChunkSize + local.X - anchor.X),
                (int)(coordinate.Y * VoxelWorld.ChunkSize + local.Y - anchor.Y),
                (int)(coordinate.Z * VoxelWorld.ChunkSize + local.Z - anchor.Z),
                value);
        }

        return new BrickChunkData(cells, starts, counts);
    }

    /// <summary>
    /// 区块内局部坐标所属 brick 的枚举下标（brick 按 Z、Y、X 排列）
    /// </summary>
    /// <param name="local">区块内局部坐标</param>
    /// <returns>0 到 63</returns>
    public static int BrickIndexOf(LocalCoord local)
    {
        var bx = local.X / BrickSize;
        var by = local.Y / BrickSize;
        var bz = local.Z / BrickSize;
        return bz * 16 + by * 4 + bx;
    }

    /// <summary>
    /// brick 枚举下标对应的区块内 8³ 区域最小局部坐标
    /// </summary>
    /// <param name="brickIndex">0 到 63</param>
    /// <returns>brick 最小角局部坐标</returns>
    public static LocalCoord BrickOrigin(int brickIndex)
    {
        if ((uint)brickIndex >= BricksPerChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(brickIndex));
        }

        var bz = brickIndex / 16;
        var by = brickIndex % 16 / 4;
        var bx = brickIndex % 4;
        return new LocalCoord(bx * BrickSize, by * BrickSize, bz * BrickSize);
    }
}
