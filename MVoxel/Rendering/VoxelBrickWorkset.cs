using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 区块对齐两级 BVH 工作集：GPU 增量上传与两级求交的 CPU 数据源
/// 结构为"外层区块树 + 每区块内层格树"：外层叶节点指向所属区块的内层子树根，
/// 内层叶节点指向格池中的小区间（默认 8 格），彻底取代早期"砖内线性扫描"在密集场景的退化
/// 布局契约：格池与内层节点都按区块连续分片存放，编辑单个区块只需重建该区块的格片、
/// 内层子树与外层叶包围盒，不触碰其他区块，为增量上传提供 O(改动) 的写入边界
/// 节点沿用着色器 BvhNode 三 ivec4 布局：links.x &lt; 0 为格叶（links.z/w = 格起点/数量），
/// 否则 links.x/y 为子节点下标（外层叶的 links.y = -1，遍历时被守卫跳过）
/// </summary>
public sealed class VoxelBrickWorkset
{
    /// <summary>
    /// 内层格树叶子容量
    /// </summary>
    private const int InnerLeafSize = 8;

    /// <summary>
    /// 空工作集
    /// </summary>
    public static VoxelBrickWorkset Empty { get; } = new(
        [], [], [], VoxelCoord64.Zero, 0, 0, 0, default);

    private VoxelBrickWorkset(VoxelGpuCell[] cells, Node[] nodes, ChunkSlab[] slabs,
        VoxelCoord64 anchor, int chunkCount, int occupiedCount, int emissiveCount, VoxelCoord64 size)
    {
        Cells = cells;
        Nodes = nodes;
        Slabs = slabs;
        Anchor = anchor;
        ChunkCount = chunkCount;
        OccupiedCount = occupiedCount;
        EmissiveCount = emissiveCount;
        Size = size;
    }

    /// <summary>
    /// 格池：全部合并盒图元，按区块连续分片，坐标相对锚点
    /// </summary>
    public VoxelGpuCell[] Cells { get; }

    /// <summary>
    /// 两级节点数组（外层树在前，随后是各区块连续内层子树）
    /// </summary>
    public Node[] Nodes { get; }

    /// <summary>
    /// 每区块的格片与内层子树边界，供增量上传定位写入区间
    /// </summary>
    public ChunkSlab[] Slabs { get; }

    /// <summary>
    /// 工作集锚点（世界包围盒最小角）
    /// </summary>
    public VoxelCoord64 Anchor { get; }

    /// <summary>
    /// 工作集包围盒尺寸
    /// </summary>
    public VoxelCoord64 Size { get; }

    /// <summary>
    /// 参与渲染的区块数
    /// </summary>
    public int ChunkCount { get; }

    /// <summary>
    /// 合并盒图元总数（等于格池长度）
    /// </summary>
    public int OccupiedCount { get; }

    /// <summary>
    /// 发光图元数量
    /// </summary>
    public int EmissiveCount { get; }

    /// <summary>
    /// 单个区块的对齐分片边界
    /// </summary>
    /// <param name="Coordinate">区块坐标</param>
    /// <param name="CellBase">格池起始下标</param>
    /// <param name="CellCount">格池长度</param>
    /// <param name="NodeBase">内层子树在节点数组中的起始下标</param>
    /// <param name="NodeCount">内层子树节点数</param>
    /// <param name="OuterLeafIndex">外层树中指向该区块内层根的叶节点下标</param>
    public readonly record struct ChunkSlab(
        ChunkCoord64 Coordinate, int CellBase, int CellCount, int NodeBase, int NodeCount, int OuterLeafIndex);

    /// <summary>
    /// 节点（与着色器 BvhNode 三 ivec4 布局对齐）
    /// </summary>
    public readonly record struct Node(
        int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
        int LinkX, int LinkY, int LinkZ, int LinkW);

    /// <summary>
    /// 从世界构建两级 BVH 工作集；空世界返回 Empty。
    /// 区块按 (Z, Y, X) 稳定序排列，格图元经贪心合并，全部输出逐字节确定
    /// </summary>
    /// <param name="world">源稀疏世界</param>
    /// <returns>两级 BVH 工作集</returns>
    public static VoxelBrickWorkset Build(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out _))
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
        var perChunkCells = new VoxelGpuCell[chunkCount][];
        System.Threading.Tasks.Parallel.For(0, chunkCount,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
            },
            index =>
            {
                var (coordinate, chunk) = chunks[index];
                perChunkCells[index] = BuildChunkCells(world, chunk, coordinate, minimum);
            });

        // 拼接格池并记录每区块格片边界
        var totalCells = 0;
        foreach (var cells in perChunkCells) totalCells += cells.Length;
        var cells_ = new VoxelGpuCell[totalCells];
        var cellBase = new int[chunkCount];
        var emissive = 0;
        var cursor = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            cellBase[index] = cursor;
            cursor += perChunkCells[index].Length;
            foreach (var cell in perChunkCells[index])
            {
                if (VoxelFlags.Has(cell.Value.Flags, VoxelFlags.Emissive)) emissive++;
            }
        }

        // 每区块内层 BVH（局部索引，叶 start/count 相对区块格片），记录子树规模与包围盒。
        // 内层叶按 order 指向重排后的格片，因此格池写入采用叶序而非原始合并序
        var innerSlabs = new List<InnerSlab>(chunkCount);
        var chunkBoxes = new Box[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var chunkCells = perChunkCells[index];
            var items = new Box[chunkCells.Length];
            for (var cell = 0; cell < chunkCells.Length; cell++)
            {
                var value = chunkCells[cell];
                items[cell] = new Box(value.X, value.Y, value.Z,
                    value.X + value.ExtentX, value.Y + value.ExtentY, value.Z + value.ExtentZ, cell);
            }

            var (nodes, order) = BuildBvh(items, InnerLeafSize);
            innerSlabs.Add(new InnerSlab(nodes, order));
            for (var position = 0; position < order.Length; position++)
            {
                cells_[cellBase[index] + position] = chunkCells[order[position]];
            }

            chunkBoxes[index] = nodes.Count > 0
                ? new Box(nodes[0].MinX, nodes[0].MinY, nodes[0].MinZ, nodes[0].MaxX, nodes[0].MaxY, nodes[0].MaxZ, index)
                : new Box(0, 0, 0, 0, 0, 0, index);
        }

        // 外层区块树（叶容量 1，每叶对应一个区块），按区块索引对半切分保证拓扑与包围盒无关，
        // 增量更新时外层拓扑稳定（只改包围盒不改结构）
        var (outerNodes, outerOrder) = BuildBvh(chunkBoxes, 1, spatial: false);
        var outerCount = outerNodes.Count;

        // 组装全局节点数组：外层在前，随后各区块内层子树连续排布
        var globalNodes = new Node[outerCount + innerSlabs.Sum(slab => slab.Nodes.Count)];
        var nodeBase = new int[chunkCount];
        var innerBase = outerCount;
        for (var index = 0; index < chunkCount; index++)
        {
            nodeBase[index] = innerBase;
            innerBase += innerSlabs[index].Nodes.Count;
        }

        var outerLeafToChunk = new int[outerCount];
        for (var node = 0; node < outerCount; node++)
        {
            var source = outerNodes[node];
            outerLeafToChunk[node] = -1;
            if (source.Left < 0)
            {
                // 外层叶：links.x 指向该区块内层根，links.y = -1 由遍历守卫跳过
                var chunkIndex = outerOrder[source.Start];
                outerLeafToChunk[node] = chunkIndex;
                globalNodes[node] = new Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, nodeBase[chunkIndex], -1, 0, 0);
            }
            else
            {
                globalNodes[node] = new Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, source.Left, source.Right, 0, 0);
            }
        }

        for (var index = 0; index < chunkCount; index++)
        {
            var slab = innerSlabs[index];
            var base_ = nodeBase[index];
            var cellOffset = cellBase[index];
            for (var node = 0; node < slab.Nodes.Count; node++)
            {
                var source = slab.Nodes[node];
                if (source.Left < 0)
                {
                    globalNodes[base_ + node] = new Node(source.MinX, source.MinY, source.MinZ,
                        source.MaxX, source.MaxY, source.MaxZ, -1, -1, cellOffset + source.Start, source.Count);
                }
                else
                {
                    globalNodes[base_ + node] = new Node(source.MinX, source.MinY, source.MinZ,
                        source.MaxX, source.MaxY, source.MaxZ, base_ + source.Left, base_ + source.Right, 0, 0);
                }
            }
        }

        var slabs = new ChunkSlab[chunkCount];
        var outerLeafByChunk = new int[chunkCount];
        for (var node = 0; node < outerCount; node++)
        {
            if (outerLeafToChunk[node] >= 0)
            {
                outerLeafByChunk[outerLeafToChunk[node]] = node;
            }
        }

        for (var index = 0; index < chunkCount; index++)
        {
            slabs[index] = new ChunkSlab(chunks[index].Coordinate, cellBase[index], perChunkCells[index].Length,
                nodeBase[index], innerSlabs[index].Nodes.Count, outerLeafByChunk[index]);
        }

        world.TryGetBounds(out _, out var maximumBound);
        var size = new VoxelCoord64(
            maximumBound.X - minimum.X + 1, maximumBound.Y - minimum.Y + 1, maximumBound.Z - minimum.Z + 1);
        return new VoxelBrickWorkset(cells_, globalNodes, slabs, minimum, chunkCount, totalCells, emissive, size);
    }

    /// <summary>
    /// 构建单个区块格片：渲染路径可贪心合并，场景视口可保留逐格图元
    /// </summary>
    internal static VoxelGpuCell[] BuildChunkCells(VoxelWorld world, SparseVoxelChunk chunk, ChunkCoord64 coordinate,
        VoxelCoord64 anchor, bool greedyMerge = true, bool includeInterior = false)
    {
        var pairs = new (int Key, VoxelValue Value)[chunk.OccupiedCount];
        var pairIndex = 0;
        foreach (var pair in chunk.Snapshot)
        {
            pairs[pairIndex++] = (pair.Key, pair.Value);
        }

        Array.Sort(pairs, static (a, b) => a.Key.CompareTo(b.Key));
        if (!includeInterior)
            pairs = VoxelGpuWorkset.CullFullyOccludedPairs(world, pairs, coordinate, preserveUniformSolids: greedyMerge);
        if (!greedyMerge)
        {
            var raw = new VoxelGpuCell[pairs.Length];
            var baseX = checked(coordinate.X * VoxelWorld.ChunkSize - anchor.X);
            var baseY = checked(coordinate.Y * VoxelWorld.ChunkSize - anchor.Y);
            var baseZ = checked(coordinate.Z * VoxelWorld.ChunkSize - anchor.Z);
            for (var index = 0; index < pairs.Length; index++)
            {
                var local = SparseVoxelChunk.FromIndex(pairs[index].Key);
                raw[index] = new VoxelGpuCell(checked((int)(baseX + local.X)),
                    checked((int)(baseY + local.Y)), checked((int)(baseZ + local.Z)), pairs[index].Value);
            }
            return raw;
        }
        var merged = new List<VoxelGpuCell>(pairs.Length);
        VoxelGpuWorkset.GreedyMergeChunk(pairs, coordinate, anchor, merged);
        return merged.ToArray();
    }

    /// <summary>
    /// 为一个区块的合并格片构建内层 BVH，返回格池写入序（order 指向入参下标）与局部节点
    /// </summary>
    internal static (List<BvhNode> Nodes, int[] Order) BuildInnerBvh(VoxelGpuCell[] chunkCells)
    {
        var items = new Box[chunkCells.Length];
        for (var cell = 0; cell < chunkCells.Length; cell++)
        {
            var value = chunkCells[cell];
            items[cell] = new Box(value.X, value.Y, value.Z,
                value.X + value.ExtentX, value.Y + value.ExtentY, value.Z + value.ExtentZ, cell);
        }

        return BuildBvh(items, InnerLeafSize);
    }

    /// <summary>
    /// 把内层局部节点写入全局节点数组指定基址：格叶 links.z 叠加 cellBase，内部链接叠加 nodeBase
    /// </summary>
    internal static void WriteInnerNodes(List<BvhNode> localNodes, int nodeBase, int cellBase, Node[] globalNodes)
    {
        for (var node = 0; node < localNodes.Count; node++)
        {
            var source = localNodes[node];
            if (source.Left < 0)
            {
                globalNodes[nodeBase + node] = new Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, -1, -1, cellBase + source.Start, source.Count);
            }
            else
            {
                globalNodes[nodeBase + node] = new Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, nodeBase + source.Left, nodeBase + source.Right, 0, 0);
            }
        }
    }

    /// <summary>
    /// 内层子树节点数（供容量预算）
    /// </summary>
    internal static int InnerNodeCount(VoxelGpuCell[] chunkCells) =>
        chunkCells.Length == 0 ? 0 : BuildInnerBvh(chunkCells).Nodes.Count;

    /// <summary>
    /// 轴对齐盒（构建期中间表示，坐标相对锚点，max 为开区间上界）
    /// </summary>
    internal readonly record struct Box(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, int Payload);

    /// <summary>
    /// 单区块内层子树的中间结果
    /// </summary>
    private sealed record InnerSlab(List<BvhNode> Nodes, int[] Order);

    internal readonly record struct BvhNode(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
        int Left, int Right, int Start, int Count);

    /// <summary>
    /// 通用空间 BVH：spatial 为真按最长轴中心中位数切分，为假按当前下标对半切分（拓扑与包围盒无关，供外层稳定复用）。
    /// 叶容量 leafSize，前序分配节点编号，返回节点列表（局部索引）与叶内负载按遍历顺序的重排数组
    /// </summary>
    internal static (List<BvhNode> Nodes, int[] Order) BuildBvh(Box[] items, int leafSize, bool spatial = true)
    {
        var nodes = new List<BvhNode>();
        var order = new int[items.Length];
        var indices = new int[items.Length];
        for (var index = 0; index < indices.Length; index++) indices[index] = index;
        var orderCursor = 0;
        BuildNode(items, indices, 0, indices.Length, leafSize, spatial, nodes, order, ref orderCursor);
        return (nodes, order);
    }

    private static void BuildNode(Box[] items, int[] indices, int start, int count, int leafSize, bool spatial,
        List<BvhNode> nodes, int[] order, ref int orderCursor)
    {
        var minX = int.MaxValue; var minY = int.MaxValue; var minZ = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue; var maxZ = int.MinValue;
        for (var index = start; index < start + count; index++)
        {
            var box = items[indices[index]];
            minX = Math.Min(minX, box.MinX); minY = Math.Min(minY, box.MinY); minZ = Math.Min(minZ, box.MinZ);
            maxX = Math.Max(maxX, box.MaxX); maxY = Math.Max(maxY, box.MaxY); maxZ = Math.Max(maxZ, box.MaxZ);
        }

        var nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= leafSize)
        {
            var leafStart = orderCursor;
            for (var index = 0; index < count; index++)
            {
                order[orderCursor++] = items[indices[start + index]].Payload;
            }

            nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX, maxY, maxZ, -1, -1, leafStart, count);
            return;
        }

        if (spatial)
        {
            var extentX = maxX - minX;
            var extentY = maxY - minY;
            var extentZ = maxZ - minZ;
            var axis = extentX >= extentY && extentX >= extentZ ? 0 : extentY >= extentZ ? 1 : 2;
            Array.Sort(indices, start, count, Comparer<int>.Create((a, b) =>
            {
                var centerA = AxisCenter(items[a], axis);
                var centerB = AxisCenter(items[b], axis);
                return centerA != centerB ? centerA.CompareTo(centerB) : a.CompareTo(b);
            }));
        }

        var middle = count / 2;
        var left = nodeIndex + 1;
        BuildNode(items, indices, start, middle, leafSize, spatial, nodes, order, ref orderCursor);
        var right = nodes.Count;
        BuildNode(items, indices, start + middle, count - middle, leafSize, spatial, nodes, order, ref orderCursor);
        nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX, maxY, maxZ, left, right, 0, 0);
    }

    private static int AxisCenter(Box box, int axis) => axis switch
    {
        0 => box.MinX + box.MaxX,
        1 => box.MinY + box.MaxY,
        _ => box.MinZ + box.MaxZ,
    };
}
