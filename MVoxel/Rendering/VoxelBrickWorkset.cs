using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 区块粒度 Brick 工作集：GPU 两级求交（粗树 → BrickTable → BrickCells）的 CPU 数据源
/// 布局契约：BrickTable 与 BrickCells 都按"区块对齐"连续存放，一个区块的内容更新
/// 只影响它自己的 64 项表项与格区间，不搬动其他区块
/// 粗树沿用 BvhNode 三 vec4 布局：叶节点 links.x = -1，links.z/w 指向该区块在
/// BrickTable 中的 64 项连续区间；内部节点 links.x/y 为子节点下标
/// </summary>
public sealed class VoxelBrickWorkset
{
    /// <summary>
    /// 空工作集
    /// </summary>
    public static VoxelBrickWorkset Empty { get; } = new([], [], [], VoxelCoord64.Zero, 0, 0);

    private VoxelBrickWorkset(VoxelGpuCell[] cells, BrickEntry[] brickTable, Node[] nodes,
        VoxelCoord64 anchor, int chunkCount, int occupiedCount)
    {
        Cells = cells;
        BrickTable = brickTable;
        Nodes = nodes;
        Anchor = anchor;
        ChunkCount = chunkCount;
        OccupiedCount = occupiedCount;
    }

    /// <summary>
    /// BrickCells 池：全部占用格，按区块 64 个 brick 连续排布
    /// </summary>
    public VoxelGpuCell[] Cells { get; }

    /// <summary>
    /// BrickTable：每区块 64 项，项 = brick 区域最小格（相对锚点）+ 占用格数
    /// </summary>
    public BrickEntry[] BrickTable { get; }

    /// <summary>
    /// 区块粒度粗树（BvhNode 布局）
    /// </summary>
    public Node[] Nodes { get; }

    /// <summary>
    /// 工作集锚点（世界包围盒最小角）
    /// </summary>
    public VoxelCoord64 Anchor { get; }

    /// <summary>
    /// 参与渲染的区块数
    /// </summary>
    public int ChunkCount { get; }

    /// <summary>
    /// 占用体素总数
    /// </summary>
    public int OccupiedCount { get; }

    /// <summary>
    /// BrickTable 项：brick 区域最小格与占用数
    /// </summary>
    public readonly record struct BrickEntry(int MinX, int MinY, int MinZ, int Count);

    /// <summary>
    /// 粗树节点（与着色器 BvhNode 的三 ivec4 布局对齐）
    /// </summary>
    public readonly record struct Node(
        int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
        int LinkX, int LinkY, int LinkZ, int LinkW);

    /// <summary>
    /// 从世界构建 Brick 工作集；空世界返回 Empty。
    /// 区块按 (Z, Y, X) 稳定序排列，全部输出逐字节确定
    /// </summary>
    /// <param name="world">源稀疏世界</param>
    /// <returns>Brick 工作集</returns>
    public static VoxelBrickWorkset Build(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            return Empty;
        }

        var chunks = world.SnapshotChunks();
        chunks.Sort(static (left, right) =>
        {
            var byZ = left.Coordinate.Z.CompareTo(right.Coordinate.Z);
            if (byZ != 0) return byZ;
            var byY = left.Coordinate.Y.CompareTo(right.Coordinate.Y);
            return byY != 0 ? byY : left.Coordinate.X.CompareTo(right.Coordinate.X);
        });

        var chunkCount = chunks.Count;
        var brickTable = new BrickEntry[chunkCount * ChunkBrickSet.BricksPerChunk];
        var chunkDatas = new ChunkBrickSet.BrickChunkData[chunkCount];
        var chunkCellStarts = new int[chunkCount];
        var chunkBoundsMin = new VoxelCoord64[chunkCount];
        var chunkBoundsMax = new VoxelCoord64[chunkCount];
        var occupied = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            var (coordinate, chunk) = chunks[index];
            var data = ChunkBrickSet.Build(coordinate, chunk, minimum);
            chunkDatas[index] = data;
            chunkCellStarts[index] = occupied;
            var brickBase = index * ChunkBrickSet.BricksPerChunk;
            var chunkBase = new VoxelCoord64(
                coordinate.X * VoxelWorld.ChunkSize,
                coordinate.Y * VoxelWorld.ChunkSize,
                coordinate.Z * VoxelWorld.ChunkSize);
            for (var brick = 0; brick < ChunkBrickSet.BricksPerChunk; brick++)
            {
                var origin = ChunkBrickSet.BrickOrigin(brick);
                brickTable[brickBase + brick] = new BrickEntry(
                    (int)(chunkBase.X + origin.X - minimum.X),
                    (int)(chunkBase.Y + origin.Y - minimum.Y),
                    (int)(chunkBase.Z + origin.Z - minimum.Z),
                    data.BrickCounts[brick]);
            }

            var occupiedMin = new VoxelCoord64(long.MaxValue, long.MaxValue, long.MaxValue);
            var occupiedMax = new VoxelCoord64(long.MinValue, long.MinValue, long.MinValue);
            foreach (var cell in data.Cells)
            {
                occupiedMin = VoxelCoord64.Zero + new VoxelCoord64(
                    Math.Min(occupiedMin.X, cell.X), Math.Min(occupiedMin.Y, cell.Y),
                    Math.Min(occupiedMin.Z, cell.Z));
                occupiedMax = VoxelCoord64.Zero + new VoxelCoord64(
                    Math.Max(occupiedMax.X, cell.X), Math.Max(occupiedMax.Y, cell.Y),
                    Math.Max(occupiedMax.Z, cell.Z));
            }

            if (data.OccupiedCount == 0)
            {
                occupiedMin = VoxelCoord64.Zero;
                occupiedMax = VoxelCoord64.Zero;
            }

            // data.Cells 已相对锚点：包围盒同样保持相对锚点
            chunkBoundsMin[index] = occupiedMin;
            chunkBoundsMax[index] = occupiedMax;
            occupied += data.OccupiedCount;
        }

        // 拼接 BrickCells：各区块区间绝对连续
        var cells = new VoxelGpuCell[occupied];
        for (var index = 0; index < chunkCount; index++)
        {
            Array.Copy(chunkDatas[index].Cells, 0, cells, chunkCellStarts[index], chunkDatas[index].Cells.Length);
        }

        var chunkBrickOffsets = new int[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            chunkBrickOffsets[index] = index * ChunkBrickSet.BricksPerChunk;
        }

        var nodes = new List<Node>(Math.Max(1, chunkCount * 2));
        BuildTree(chunkBoundsMin, chunkBoundsMax, 0, chunkCount, chunkBrickOffsets, nodes, minimum);
        return new VoxelBrickWorkset(cells, brickTable, nodes.ToArray(), minimum, chunkCount, occupied);
    }

    /// <summary>
    /// 递归构建区块粒度二叉树：按区间中位切分，叶节点 = 单区块
    /// </summary>
    private static void BuildTree(VoxelCoord64[] boundsMin, VoxelCoord64[] boundsMax,
        int start, int end, int[] chunkBrickOffsets, List<Node> nodes, VoxelCoord64 anchor)
    {
        var minX = long.MaxValue; var minY = long.MaxValue; var minZ = long.MaxValue;
        var maxX = long.MinValue; var maxY = long.MinValue; var maxZ = long.MinValue;
        for (var index = start; index < end; index++)
        {
            minX = Math.Min(minX, boundsMin[index].X);
            minY = Math.Min(minY, boundsMin[index].Y);
            minZ = Math.Min(minZ, boundsMin[index].Z);
            maxX = Math.Max(maxX, boundsMax[index].X);
            maxY = Math.Max(maxY, boundsMax[index].Y);
            maxZ = Math.Max(maxZ, boundsMax[index].Z);
        }

        var nodeIndex = nodes.Count;
        nodes.Add(default);
        if (end - start == 1)
        {
            // 区块包围盒在 Build 中已相对锚点，这里直接使用
            nodes[nodeIndex] = new Node(
                (int)minX, (int)minY, (int)minZ,
                (int)maxX, (int)maxY, (int)maxZ,
                -1, -1, chunkBrickOffsets[start], ChunkBrickSet.BricksPerChunk);
            return;
        }

        var middle = (start + end) / 2;
        BuildTree(boundsMin, boundsMax, start, middle, chunkBrickOffsets, nodes, anchor);
        // 左子树根紧随父节点；右子树根是左子树全部节点之后的下一个分配位
        var leftChild = nodeIndex + 1;
        var rightChild = nodes.Count;
        BuildTree(boundsMin, boundsMax, middle, end, chunkBrickOffsets, nodes, anchor);
        nodes[nodeIndex] = new Node(
            (int)minX, (int)minY, (int)minZ,
            (int)maxX, (int)maxY, (int)maxZ,
            leftChild, rightChild, -1, -1);
    }
}
