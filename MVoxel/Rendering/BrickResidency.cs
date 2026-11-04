using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 砖驻留：GPU 两级 BVH 的可变常驻体，把笔刷同步从 O(世界) 降到 O(改动)
/// 每区块的格片与内层子树按预留容量固定基址存放，编辑单个区块只需在容量内原地重建该片，
/// 并沿外层树父链合并包围盒；容量不足、区块增删（结构变化）时回退一次全量重建
/// 池布局与 VoxelBrickWorkset 一致（外层树在前、内层子树连续），着色器遍历语义完全相同
/// </summary>
public sealed class BrickResidency
{
    private VoxelGpuCell[] _cells = [];
    private VoxelBrickWorkset.Node[] _nodes = [];
    private readonly Dictionary<ChunkCoord64, int> _slotByCoord = new();
    private ChunkSlot[] _slots = [];
    private int[] _outerParent = [];
    private int _emissive;

    private sealed class ChunkSlot
    {
        internal ChunkCoord64 Coordinate;
        internal int CellBase;
        internal int CellCapacity;
        internal int CellCount;
        internal int NodeBase;
        internal int NodeCapacity;
        internal int NodeCount;
        internal int OuterLeaf;
        internal int Emissive;
    }

    /// <summary>
    /// 上传计划：全量或按脏区间增量
    /// </summary>
    public sealed class UploadPlan
    {
        public bool FullRebuild;
        public IReadOnlyList<(int Start, int Count)> CellRanges = Array.Empty<(int, int)>();
        public IReadOnlyList<(int Start, int Count)> NodeRanges = Array.Empty<(int, int)>();
    }

    private BrickResidency()
    {
    }

    /// <summary>
    /// 锚点（世界包围盒最小角），全量重建时更新
    /// </summary>
    public VoxelCoord64 Anchor { get; private set; }

    /// <summary>
    /// 包围盒尺寸
    /// </summary>
    public VoxelCoord64 Size { get; private set; }

    /// <summary>
    /// 格池（含预留容量间隙，叶区间外的格不会被着色器引用）
    /// </summary>
    public VoxelGpuCell[] Cells => _cells;

    /// <summary>
    /// 节点池（外层 [0,OuterCount) 在前，随后各区块内层子树）
    /// </summary>
    public VoxelBrickWorkset.Node[] Nodes => _nodes;

    /// <summary>
    /// 外层树节点数
    /// </summary>
    public int OuterCount { get; private set; }

    /// <summary>
    /// 区块数
    /// </summary>
    public int ChunkCount => _slots.Length;

    /// <summary>
    /// 合并格图元实际总数（不含预留间隙）
    /// </summary>
    public int OccupiedCount { get; private set; }

    /// <summary>
    /// 为硬件求交生成保留池索引的快照；空槽必须清零，因为增量更新后它可能仍保存旧体素。
    /// </summary>
    internal VoxelGpuCell[] CreateHardwareCellSnapshot()
    {
        var snapshot = new VoxelGpuCell[_cells.Length];
        foreach (var slot in _slots)
            Array.Copy(_cells, slot.CellBase, snapshot, slot.CellBase, slot.CellCount);
        return snapshot;
    }

    /// <summary>
    /// 发光图元数
    /// </summary>
    public int EmissiveCount => _emissive;

    /// <summary>
    /// 构建时记录的结构戳与内容版本，用于判断能否增量
    /// </summary>
    public long BuiltStructureStamp { get; private set; }

    /// <summary>
    /// 是否为空
    /// </summary>
    public bool IsEmpty => _slots.Length == 0;

    /// <summary>
    /// 最近一次 TryUpdate 拒绝增量的原因（诊断用，成功时为空）
    /// </summary>
    public string LastRejectReason { get; private set; } = string.Empty;

    /// <summary>
    /// 全量重建驻留
    /// </summary>
    public static BrickResidency BuildFull(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var residency = new BrickResidency();
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            residency._cells = [];
            residency._nodes = [];
            residency.BuiltStructureStamp = world.StructureStamp;
            return residency;
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
        var perChunk = new ChunkData[chunkCount];
        System.Threading.Tasks.Parallel.For(0, chunkCount,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
            },
            index =>
            {
                var (coordinate, chunk) = chunks[index];
                var cells = VoxelBrickWorkset.BuildChunkCells(world, chunk, coordinate, minimum);
                var inner = VoxelBrickWorkset.BuildInnerBvh(cells);
                perChunk[index] = new ChunkData(coordinate, cells, inner.Nodes, inner.Order);
            });

        var outerCount = chunkCount == 0 ? 0 : 2 * chunkCount - 1;
        var cellPool = 0;
        var nodePool = outerCount;
        var slots = new ChunkSlot[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var slot = new ChunkSlot
            {
                Coordinate = perChunk[index].Coordinate,
                CellCount = perChunk[index].Cells.Length,
                NodeCount = perChunk[index].InnerNodes.Count,
                CellCapacity = CapacityFor(perChunk[index].Cells.Length),
                NodeCapacity = CapacityFor(perChunk[index].InnerNodes.Count),
            };
            slot.CellBase = cellPool;
            cellPool += slot.CellCapacity;
            slot.NodeBase = nodePool;
            nodePool += slot.NodeCapacity;
            slots[index] = slot;
            residency._slotByCoord[slot.Coordinate] = index;
        }

        residency._cells = new VoxelGpuCell[cellPool];
        residency._nodes = new VoxelBrickWorkset.Node[nodePool];
        residency._slots = slots;
        residency.OuterCount = outerCount;

        // 内层子树与格片写入
        var emissive = 0;
        var chunkBoxes = new VoxelBrickWorkset.Box[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var data = perChunk[index];
            var slot = slots[index];
            for (var position = 0; position < data.Order.Length; position++)
            {
                residency._cells[slot.CellBase + position] = data.Cells[data.Order[position]];
            }

            VoxelBrickWorkset.WriteInnerNodes(data.InnerNodes, slot.NodeBase, slot.CellBase, residency._nodes);
            var root = data.InnerNodes[0];
            slot.Emissive = CountEmissive(data.Cells);
            emissive += slot.Emissive;
            chunkBoxes[index] = new VoxelBrickWorkset.Box(root.MinX, root.MinY, root.MinZ,
                root.MaxX, root.MaxY, root.MaxZ, index);
        }

        residency._emissive = emissive;
        residency.OccupiedCount = slots.Sum(slot => slot.CellCount);

        // 外层树（索引对半，拓扑稳定）
        var (outerNodes, outerOrder) = VoxelBrickWorkset.BuildBvh(chunkBoxes, 1, spatial: false);
        var outerParent = new int[outerCount];
        Array.Fill(outerParent, -1);
        for (var node = 0; node < outerCount; node++)
        {
            var source = outerNodes[node];
            if (source.Left < 0)
            {
                var chunkIndex = outerOrder[source.Start];
                slots[chunkIndex].OuterLeaf = node;
                residency._nodes[node] = new VoxelBrickWorkset.Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, slots[chunkIndex].NodeBase, -1, 0, 0);
            }
            else
            {
                residency._nodes[node] = new VoxelBrickWorkset.Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, source.Left, source.Right, 0, 0);
                outerParent[source.Left] = node;
                outerParent[source.Right] = node;
            }
        }

        residency._outerParent = outerParent;
        residency.Anchor = minimum;
        residency.Size = new VoxelCoord64(maximum.X - minimum.X + 1, maximum.Y - minimum.Y + 1, maximum.Z - minimum.Z + 1);
        residency.BuiltStructureStamp = world.StructureStamp;
        return residency;
    }

    /// <summary>
    /// 尝试按脏区块增量更新；结构变化、脏区块未知或容量溢出时返回 false，调用方回退全量。
    /// 可见性会查询六个相邻体素，所以还必须同步重建相邻区块的边界格片。
    /// </summary>
    public bool TryUpdate(VoxelWorld world, IReadOnlySet<ChunkCoord64> dirty, out UploadPlan plan)
    {
        plan = new UploadPlan();
        LastRejectReason = string.Empty;
        if (world.StructureStamp != BuiltStructureStamp)
        {
            LastRejectReason = $"structure {world.StructureStamp}!={BuiltStructureStamp}";
            return false;
        }

        if (dirty.Count == 0)
        {
            LastRejectReason = "no dirty";
            return false;
        }

        foreach (var coordinate in dirty)
        {
            if (!_slotByCoord.ContainsKey(coordinate))
            {
                LastRejectReason = $"chunk {coordinate} not in slots";
                return false;
            }
        }

        // 内部体素剔除依赖六邻域。编辑恰好落在区块边界时，邻区的一个体素也可能
        // 从“完全遮挡”变成“可见”（或反过来），因此不能只更新原始脏区块。
        var affected = new HashSet<ChunkCoord64>(dirty);
        foreach (var coordinate in dirty)
        {
            foreach (var (x, y, z) in NeighborChunkOffsets)
            {
                var neighbor = new ChunkCoord64(coordinate.X + x, coordinate.Y + y, coordinate.Z + z);
                if (_slotByCoord.ContainsKey(neighbor)) affected.Add(neighbor);
            }
        }

        var cellRanges = new List<(int, int)>();
        var nodeRanges = new List<(int, int)> { (0, OuterCount) };
        foreach (var coordinate in affected.OrderBy(static item => item.Z)
                     .ThenBy(static item => item.Y).ThenBy(static item => item.X))
        {
            var slotIndex = _slotByCoord[coordinate];

            if (!world.TryGetChunk(coordinate, out var chunk))
            {
                LastRejectReason = $"chunk {coordinate} missing in world";
                return false;
            }

            var slot = _slots[slotIndex];
            var cells = VoxelBrickWorkset.BuildChunkCells(world, chunk, coordinate, Anchor);
            var inner = VoxelBrickWorkset.BuildInnerBvh(cells);
            if (cells.Length > slot.CellCapacity || inner.Nodes.Count > slot.NodeCapacity)
            {
                LastRejectReason = $"chunk {coordinate} overflow cells {cells.Length}/{slot.CellCapacity} nodes {inner.Nodes.Count}/{slot.NodeCapacity}";
                return false; // 容量不足，交回全量重建重排
            }

            var newEmissive = CountEmissive(cells);
            for (var position = 0; position < inner.Order.Length; position++)
            {
                _cells[slot.CellBase + position] = cells[inner.Order[position]];
            }

            VoxelBrickWorkset.WriteInnerNodes(inner.Nodes, slot.NodeBase, slot.CellBase, _nodes);
            OccupiedCount += cells.Length - slot.CellCount;
            _emissive += newEmissive - slot.Emissive;
            cellRanges.Add((slot.CellBase, cells.Length));
            nodeRanges.Add((slot.NodeBase, inner.Nodes.Count));
            slot.CellCount = cells.Length;
            slot.NodeCount = inner.Nodes.Count;
            slot.Emissive = newEmissive;

            RefreshOuterBounds(slot);
        }

        plan.CellRanges = cellRanges;
        plan.NodeRanges = nodeRanges;
        return true;
    }

    /// <summary>
    /// 更新某区块外层叶包围盒并沿父链向上合并
    /// </summary>
    private void RefreshOuterBounds(ChunkSlot slot)
    {
        var leaf = slot.OuterLeaf;
        var root = _nodes[slot.NodeBase];
        var current = leaf;
        var minX = root.MinX; var minY = root.MinY; var minZ = root.MinZ;
        var maxX = root.MaxX; var maxY = root.MaxY; var maxZ = root.MaxZ;
        var node = _nodes[current];
        _nodes[current] = new VoxelBrickWorkset.Node(minX, minY, minZ, maxX, maxY, maxZ,
            node.LinkX, node.LinkY, node.LinkZ, node.LinkW);
        current = _outerParent[current];
        while (current >= 0)
        {
            var parent = _nodes[current];
            var left = _nodes[parent.LinkX];
            var unionMinX = left.MinX; var unionMinY = left.MinY; var unionMinZ = left.MinZ;
            var unionMaxX = left.MaxX; var unionMaxY = left.MaxY; var unionMaxZ = left.MaxZ;
            if (parent.LinkY >= 0)
            {
                var right = _nodes[parent.LinkY];
                unionMinX = Math.Min(unionMinX, right.MinX); unionMinY = Math.Min(unionMinY, right.MinY);
                unionMinZ = Math.Min(unionMinZ, right.MinZ);
                unionMaxX = Math.Max(unionMaxX, right.MaxX); unionMaxY = Math.Max(unionMaxY, right.MaxY);
                unionMaxZ = Math.Max(unionMaxZ, right.MaxZ);
            }

            if (parent.MinX == unionMinX && parent.MinY == unionMinY && parent.MinZ == unionMinZ &&
                parent.MaxX == unionMaxX && parent.MaxY == unionMaxY && parent.MaxZ == unionMaxZ)
            {
                break; // 包围盒未变，祖先也不会变
            }

            _nodes[current] = new VoxelBrickWorkset.Node(unionMinX, unionMinY, unionMinZ,
                unionMaxX, unionMaxY, unionMaxZ, parent.LinkX, parent.LinkY, parent.LinkZ, parent.LinkW);
            current = _outerParent[current];
        }
    }

    private static int CapacityFor(int count) => count + Math.Max(64, count / 2);

    private static readonly (long X, long Y, long Z)[] NeighborChunkOffsets =
    [
        (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1),
    ];

    private static int CountEmissive(VoxelGpuCell[] cells)
    {
        var count = 0;
        foreach (var cell in cells)
        {
            if (VoxelFlags.Has(cell.Value.Flags, VoxelFlags.Emissive)) count++;
        }

        return count;
    }

    private readonly record struct ChunkData(
        ChunkCoord64 Coordinate, VoxelGpuCell[] Cells,
        List<VoxelBrickWorkset.BvhNode> InnerNodes, int[] Order);
}
