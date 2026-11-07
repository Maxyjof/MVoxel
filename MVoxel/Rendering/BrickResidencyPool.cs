using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// GPU 可见区块常驻池：只驻留相机可见（含保守贡献范围）的区块，
/// 近处区块优先驻留，超出格预算时优先淘汰最远、最久未可见的区块；
/// 每次同步最多新增固定数量的区块与合并格，把大场景的加载摊到多帧完成。
/// 布局沿用 <see cref="VoxelBrickWorkset"/> 的两级 BVH 契约：
/// 节点池前段固定预留给外层区块树，内层子树从空闲区间按需分配，
/// 保留区块的格片与内层子树基址保持不变，可见性变化只上传新增、重建的区间。
/// </summary>
public sealed class BrickResidencyPool
{
    /// <summary>
    /// 单次同步最多新增的区块数：控制每帧的建树与上传预算
    /// </summary>
    private const int AdmissionChunkBudget = 32;

    /// <summary>
    /// 单次同步最多新增的合并格数
    /// </summary>
    private const int AdmissionCellBudget = 262_144;

    /// <summary>
    /// 驻留区块数上限，决定外层区块树预留的节点区间
    /// </summary>
    private const int MaxResidentChunks = 16_384;

    private readonly Dictionary<ChunkCoord64, Resident> _residentByChunk = new();
    private readonly List<Resident> _residents = new();
    private readonly HashSet<ChunkCoord64> _knownEmpty = new();
    // 格池与内层节点池的空闲区间，按起始下标升序保存并在释放时合并相邻区间
    private readonly List<(int Start, int Length)> _cellFree = new();
    private readonly List<(int Start, int Length)> _nodeFree = new();

    private VoxelGpuCell[] _cells = [];
    private VoxelBrickWorkset.Node[] _nodes = [];
    private int _cellHigh;
    private int _nodeHigh;
    private int _innerBase;
    private int _occupied;
    private int _emissive;
    private bool _grown;
    private bool _outerDirty = true;
    private bool _forceFullUpload = true;
    private long _touch;
    private VoxelWorld? _world;

    private sealed class Resident
    {
        internal ChunkCoord64 Coordinate;
        internal int CellStart;
        internal int CellCount;
        internal int NodeStart;
        internal int NodeCount;
        internal int Emissive;
        internal float Priority;
        internal long LastVisible;
        internal bool Released;
    }

    /// <summary>
    /// 一次同步的 GPU 上传计划：整份重传，或只传发生变化的格片与节点区间
    /// </summary>
    public sealed class WindowUploadPlan
    {
        /// <summary>
        /// 池容量变化时整份重传（GPU 缓冲需要重新分配）
        /// </summary>
        public bool FullRebuild;

        /// <summary>
        /// 本轮是否有任何几何变化
        /// </summary>
        public bool Changed;

        /// <summary>
        /// 需要上传的格池区间
        /// </summary>
        public readonly List<(int Start, int Count)> CellRanges = new();

        /// <summary>
        /// 需要上传的节点池区间
        /// </summary>
        public readonly List<(int Start, int Count)> NodeRanges = new();
    }

    private BrickResidencyPool()
    {
    }

    /// <summary>
    /// 锚点（世界包围盒最小角）
    /// </summary>
    public VoxelCoord64 Anchor { get; private set; }

    /// <summary>
    /// 世界包围盒尺寸；保持完整世界范围，让区域参数与阴影范围可保守复用
    /// </summary>
    public VoxelCoord64 Size { get; private set; }

    /// <summary>
    /// 格池（可能含已释放的间隙，间隙内的旧数据不会被节点引用）
    /// </summary>
    public VoxelGpuCell[] Cells => _cells;

    /// <summary>
    /// 节点池：外层区块树在前，内层子树随后
    /// </summary>
    public VoxelBrickWorkset.Node[] Nodes => _nodes;

    /// <summary>
    /// 当前外层区块树节点数
    /// </summary>
    public int OuterCount { get; private set; }

    /// <summary>
    /// 当前驻留区块数
    /// </summary>
    public int ChunkCount => _residentByChunk.Count;

    /// <summary>
    /// 驻留的合并格总数（不含间隙）
    /// </summary>
    public int OccupiedCount => _occupied;

    /// <summary>
    /// 驻留的发光格数
    /// </summary>
    public int EmissiveCount => _emissive;

    /// <summary>
    /// 是否没有驻留区块
    /// </summary>
    public bool IsEmpty => _residentByChunk.Count == 0;

    /// <summary>
    /// 是否还有候选区块受每帧加载预算限制尚未驻留
    /// </summary>
    public bool AwaitingAdmission { get; private set; }

    /// <summary>
    /// 驻留格池的格数上限
    /// </summary>
    public int CellBudget { get; private set; } = 1;

    /// <summary>
    /// 创建空池并记录世界包围盒
    /// </summary>
    /// <param name="world">体素世界</param>
    /// <param name="cellBudget">格池格数上限</param>
    /// <returns>可用于流送同步的常驻池</returns>
    public static BrickResidencyPool Create(VoxelWorld world, int cellBudget)
    {
        ArgumentNullException.ThrowIfNull(world);
        var pool = new BrickResidencyPool
        {
            CellBudget = Math.Max(1, cellBudget),
        };
        pool.Reset(world);
        return pool;
    }

    /// <summary>
    /// 在世界包围盒变化后重置池内容，保留数组容量
    /// </summary>
    private void Reset(VoxelWorld world)
    {
        _world = world;
        _residentByChunk.Clear();
        _residents.Clear();
        _knownEmpty.Clear();
        _cellFree.Clear();
        _nodeFree.Clear();
        _cellHigh = 0;
        _nodeHigh = 0;
        _occupied = 0;
        _emissive = 0;
        _grown = false;
        _outerDirty = true;
        _forceFullUpload = true;
        // 外层区块树固定预留完整节点区间，内层子树基址因此不随驻留数量变化
        _innerBase = MaxResidentChunks * 2 - 1;
        if (_nodes.Length < _innerBase)
        {
            _nodes = new VoxelBrickWorkset.Node[_innerBase + 1024];
        }

        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            Anchor = default;
            Size = default;
            return;
        }

        Anchor = minimum;
        Size = new VoxelCoord64(maximum.X - minimum.X + 1, maximum.Y - minimum.Y + 1, maximum.Z - minimum.Z + 1);
    }

    /// <summary>
    /// 按可见性更新驻留：释放不可见区块、重建脏区块、按距离优先补入新可见区块。
    /// </summary>
    /// <param name="world">体素世界</param>
    /// <param name="visible">区块可见性谓词（含阴影/反射的保守贡献范围）</param>
    /// <param name="camera">判距相机位置；为空时按坐标稳定序驻留</param>
    /// <returns>本轮上传计划</returns>
    public WindowUploadPlan Update(VoxelWorld world, Func<ChunkCoord64, bool> visible, Vector3? camera)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(visible);
        _world = world;
        _grown = false;
        _touch++;
        AwaitingAdmission = false;
        var plan = new WindowUploadPlan();
        var bounds = world.TryGetBounds(out var minimum, out var maximum);
        var size = bounds
            ? new VoxelCoord64(maximum.X - minimum.X + 1, maximum.Y - minimum.Y + 1, maximum.Z - minimum.Z + 1)
            : default;
        // 锚点变化会让全部局部坐标失效，只能整池重建；包围盒仅向外扩展时只更新范围
        if (!bounds || minimum != Anchor)
        {
            Reset(world);
        }
        else
        {
            Size = size;
        }

        var dirtyPresent = world.TryTakeDirty(out var dirty);
        if (dirtyPresent)
        {
            foreach (var coordinate in dirty)
            {
                _knownEmpty.Remove(coordinate);
            }
        }

        var snapshot = _residents.ToArray();
        foreach (var resident in snapshot)
        {
            if (!visible(resident.Coordinate))
            {
                Release(resident);
                continue;
            }

            resident.LastVisible = _touch;
            resident.Priority = PriorityOf(resident.Coordinate, camera);
            if (dirtyPresent && dirty!.Contains(resident.Coordinate))
            {
                if (!world.TryGetChunk(resident.Coordinate, out var chunk))
                {
                    _knownEmpty.Add(resident.Coordinate);
                    Release(resident);
                    continue;
                }

                RebuildResident(resident, chunk, plan);
            }
        }

        // 候选按优先级（近处优先）排序；相机缺失时按坐标稳定序
        var candidates = new List<(ChunkCoord64 Coordinate, float Priority)>();
        foreach (var coordinate in world.LoadedChunks)
        {
            if (_residentByChunk.ContainsKey(coordinate) || _knownEmpty.Contains(coordinate)) continue;
            if (!visible(coordinate)) continue;
            candidates.Add((coordinate, PriorityOf(coordinate, camera)));
        }

        candidates.Sort(static (left, right) =>
        {
            var byPriority = right.Priority.CompareTo(left.Priority);
            if (byPriority != 0) return byPriority;
            var byZ = left.Coordinate.Z.CompareTo(right.Coordinate.Z);
            if (byZ != 0) return byZ;
            var byY = left.Coordinate.Y.CompareTo(right.Coordinate.Y);
            return byY != 0 ? byY : left.Coordinate.X.CompareTo(right.Coordinate.X);
        });

        var admittedChunks = 0;
        var admittedCells = 0;
        var deferred = false;
        foreach (var (coordinate, priority) in candidates)
        {
            if (admittedChunks >= AdmissionChunkBudget || admittedCells >= AdmissionCellBudget)
            {
                deferred = true;
                break;
            }

            if (_residentByChunk.Count >= MaxResidentChunks) break;
            if (!world.TryGetChunk(coordinate, out var chunk))
            {
                _knownEmpty.Add(coordinate);
                continue;
            }

            var cells = VoxelBrickWorkset.BuildChunkCells(world, chunk, coordinate, Anchor);
            if (cells.Length == 0)
            {
                _knownEmpty.Add(coordinate);
                continue;
            }

            // 预算不足时先按优先级淘汰更远的驻留区块；更近的驻留区块不会被换掉
            if (_occupied + cells.Length > CellBudget && !EvictFor(cells.Length, priority)) continue;
            var inner = VoxelBrickWorkset.BuildInnerBvh(cells);
            AddResident(coordinate, cells, inner.Nodes, inner.Order, priority, plan);
            admittedChunks++;
            admittedCells += cells.Length;
        }

        AwaitingAdmission = deferred;
        _residents.RemoveAll(static resident => resident.Released);
        if (_outerDirty) RebuildOuterTree(plan);

        if (dirtyPresent) world.MarkChunksSynced(dirty!);
        plan.Changed = _grown || plan.CellRanges.Count > 0 || plan.NodeRanges.Count > 0;
        plan.FullRebuild = _forceFullUpload || _grown || plan.FullRebuild;
        _forceFullUpload = false;
        if (!plan.FullRebuild && !plan.Changed) return plan;
        // 释放不可见的驻留区块后外层树已重建，节点区间必须重传
        return plan;
    }

    /// <summary>
    /// 为硬件求交生成保留池索引的快照；间隙必须清零，避免索引错位后读到旧体素
    /// </summary>
    internal VoxelGpuCell[] CreateHardwareCellSnapshot()
    {
        var snapshot = new VoxelGpuCell[_cells.Length];
        foreach (var resident in _residents)
        {
            Array.Copy(_cells, resident.CellStart, snapshot, resident.CellStart, resident.CellCount);
        }

        return snapshot;
    }

    /// <summary>
    /// 前方补位：新建驻留区块
    /// </summary>
    private void AddResident(ChunkCoord64 coordinate, VoxelGpuCell[] cells, List<VoxelBrickWorkset.BvhNode> inner,
        int[] order, float priority, WindowUploadPlan plan)
    {
        var cellStart = AllocateCellRange(cells.Length);
        var nodeStart = _innerBase + AllocateInnerRange(inner.Count);
        for (var position = 0; position < order.Length; position++)
        {
            _cells[cellStart + position] = cells[order[position]];
        }

        VoxelBrickWorkset.WriteInnerNodes(inner, nodeStart, cellStart, _nodes);
        var resident = new Resident
        {
            Coordinate = coordinate,
            CellStart = cellStart,
            CellCount = cells.Length,
            NodeStart = nodeStart,
            NodeCount = inner.Count,
            Emissive = CountEmissive(cells),
            Priority = priority,
            LastVisible = _touch,
        };
        _residents.Add(resident);
        _residentByChunk[coordinate] = resident;
        _occupied += cells.Length;
        _emissive += resident.Emissive;
        _outerDirty = true;
        plan.CellRanges.Add((cellStart, cells.Length));
        plan.NodeRanges.Add((nodeStart, inner.Count));
    }

    /// <summary>
    /// 重建脏驻留区块；容量内原地重写，超出则重新分配区间
    /// </summary>
    private void RebuildResident(Resident resident, SparseVoxelChunk chunk, WindowUploadPlan plan)
    {
        var cells = VoxelBrickWorkset.BuildChunkCells(_world!, chunk, resident.Coordinate, Anchor);
        if (cells.Length == 0)
        {
            _knownEmpty.Add(resident.Coordinate);
            Release(resident);
            return;
        }

        var inner = VoxelBrickWorkset.BuildInnerBvh(cells);
        if (cells.Length > resident.CellCount)
        {
            FreeCellRange(resident.CellStart, resident.CellCount);
            resident.CellStart = AllocateCellRange(cells.Length);
        }

        if (inner.Nodes.Count > resident.NodeCount)
        {
            FreeInnerRange(resident.NodeStart - _innerBase, resident.NodeCount);
            resident.NodeStart = _innerBase + AllocateInnerRange(inner.Nodes.Count);
        }

        for (var position = 0; position < inner.Order.Length; position++)
        {
            _cells[resident.CellStart + position] = cells[inner.Order[position]];
        }

        VoxelBrickWorkset.WriteInnerNodes(inner.Nodes, resident.NodeStart, resident.CellStart, _nodes);
        _occupied += cells.Length - resident.CellCount;
        var newEmissive = CountEmissive(cells);
        _emissive += newEmissive - resident.Emissive;
        resident.Emissive = newEmissive;
        resident.CellCount = cells.Length;
        resident.NodeCount = inner.Nodes.Count;
        _outerDirty = true;
        plan.CellRanges.Add((resident.CellStart, cells.Length));
        plan.NodeRanges.Add((resident.NodeStart, inner.Nodes.Count));
    }

    /// <summary>
    /// 按格预算淘汰比候选更远的驻留区块，腾出空间
    /// </summary>
    private bool EvictFor(int needed, float priority)
    {
        while (_occupied + needed > CellBudget)
        {
            Resident? victim = null;
            foreach (var resident in _residents)
            {
                if (resident.Released) continue;
                if (victim is null || resident.Priority < victim.Priority ||
                    (resident.Priority == victim.Priority && resident.LastVisible < victim.LastVisible))
                {
                    victim = resident;
                }
            }

            // 剩余驻留区块都比候选更近，不值得为新候选淘汰
            if (victim is null || victim.Priority >= priority) return false;
            Release(victim);
        }

        return true;
    }

    /// <summary>
    /// 释放驻留区块的池区间（数组元素延迟到本轮结束统一移除）
    /// </summary>
    private void Release(Resident resident)
    {
        if (resident.Released) return;
        resident.Released = true;
        FreeCellRange(resident.CellStart, resident.CellCount);
        FreeInnerRange(resident.NodeStart - _innerBase, resident.NodeCount);
        _residentByChunk.Remove(resident.Coordinate);
        _occupied -= resident.CellCount;
        _emissive -= resident.Emissive;
        _outerDirty = true;
    }

    /// <summary>
    /// 重建外层区块树，记录需要上传的节点区间
    /// </summary>
    private void RebuildOuterTree(WindowUploadPlan plan)
    {
        _outerDirty = false;
        var count = _residents.Count;
        if (count == 0)
        {
            OuterCount = 0;
            return;
        }

        var boxes = new VoxelBrickWorkset.Box[count];
        for (var index = 0; index < count; index++)
        {
            var resident = _residents[index];
            var root = _nodes[resident.NodeStart];
            boxes[index] = new VoxelBrickWorkset.Box(root.MinX, root.MinY, root.MinZ, root.MaxX, root.MaxY,
                root.MaxZ, index);
        }

        var (outerNodes, outerOrder) = VoxelBrickWorkset.BuildBvh(boxes, 1, spatial: false);
        OuterCount = outerNodes.Count;
        for (var node = 0; node < OuterCount; node++)
        {
            var source = outerNodes[node];
            if (source.Left < 0)
            {
                var resident = _residents[outerOrder[source.Start]];
                _nodes[node] = new VoxelBrickWorkset.Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, resident.NodeStart, -1, 0, 0);
            }
            else
            {
                _nodes[node] = new VoxelBrickWorkset.Node(source.MinX, source.MinY, source.MinZ,
                    source.MaxX, source.MaxY, source.MaxZ, source.Left, source.Right, 0, 0);
            }
        }

        plan.NodeRanges.Add((0, OuterCount));
    }

    /// <summary>
    /// 候选优先级：相机距离越近越高；无相机时全部相等（按坐标稳定序）
    /// </summary>
    private float PriorityOf(ChunkCoord64 coordinate, Vector3? camera)
    {
        if (camera is not { } position) return 0F;
        var center = new Vector3(
            (float)((double)coordinate.X * VoxelWorld.ChunkSize + VoxelWorld.ChunkSize * 0.5),
            (float)((double)coordinate.Y * VoxelWorld.ChunkSize + VoxelWorld.ChunkSize * 0.5),
            (float)((double)coordinate.Z * VoxelWorld.ChunkSize + VoxelWorld.ChunkSize * 0.5));
        var delta = center - position;
        return -delta.LengthSquared();
    }

    private int AllocateCellRange(int count)
    {
        var bestIndex = -1;
        var bestLength = int.MaxValue;
        for (var index = 0; index < _cellFree.Count; index++)
        {
            var length = _cellFree[index].Length;
            if (length >= count && length < bestLength)
            {
                bestIndex = index;
                bestLength = length;
            }
        }

        if (bestIndex >= 0)
        {
            var range = _cellFree[bestIndex];
            if (range.Length == count)
            {
                _cellFree.RemoveAt(bestIndex);
            }
            else
            {
                _cellFree[bestIndex] = (range.Start + count, range.Length - count);
            }

            return range.Start;
        }

        if (_cellHigh + count > _cells.Length) GrowCells(_cellHigh + count);
        var start = _cellHigh;
        _cellHigh += count;
        return start;
    }

    private void FreeCellRange(int start, int count)
    {
        if (count <= 0) return;
        InsertFreeRange(_cellFree, start, count);
    }

    private int AllocateInnerRange(int count)
    {
        if (count <= 0) return 0;
        var bestIndex = -1;
        var bestLength = int.MaxValue;
        for (var index = 0; index < _nodeFree.Count; index++)
        {
            var length = _nodeFree[index].Length;
            if (length >= count && length < bestLength)
            {
                bestIndex = index;
                bestLength = length;
            }
        }

        if (bestIndex >= 0)
        {
            var range = _nodeFree[bestIndex];
            if (range.Length == count)
            {
                _nodeFree.RemoveAt(bestIndex);
            }
            else
            {
                _nodeFree[bestIndex] = (range.Start + count, range.Length - count);
            }

            return range.Start;
        }

        if (_nodeHigh + count > _nodes.Length - _innerBase) GrowNodes(_nodeHigh + count);
        var start = _nodeHigh;
        _nodeHigh += count;
        return start;
    }

    private void FreeInnerRange(int start, int count)
    {
        if (count <= 0) return;
        InsertFreeRange(_nodeFree, start, count);
    }

    private void GrowCells(int required)
    {
        var capacity = Math.Max(required, Math.Max(1024, _cells.Length + _cells.Length / 2));
        capacity = Math.Min(capacity, Math.Max(required, CellBudget));
        if (capacity < required) capacity = required;
        Array.Resize(ref _cells, capacity);
        _grown = true;
    }

    private void GrowNodes(int required)
    {
        var innerCapacity = Math.Max(required, Math.Max(1024, (_nodes.Length - _innerBase) * 3 / 2));
        var capacity = _innerBase + innerCapacity;
        Array.Resize(ref _nodes, capacity);
        _grown = true;
    }

    /// <summary>
    /// 插入空闲区间并与相邻区间合并
    /// </summary>
    private static void InsertFreeRange(List<(int Start, int Length)> ranges, int start, int length)
    {
        var index = 0;
        while (index < ranges.Count && ranges[index].Start < start) index++;
        ranges.Insert(index, (start, length));
        // 先向后合并，再向前合并
        while (index + 1 < ranges.Count &&
               ranges[index].Start + ranges[index].Length >= ranges[index + 1].Start)
        {
            var next = ranges[index + 1];
            var mergedStart = ranges[index].Start;
            var mergedEnd = Math.Max(ranges[index].Start + ranges[index].Length, next.Start + next.Length);
            ranges[index] = (mergedStart, mergedEnd - mergedStart);
            ranges.RemoveAt(index + 1);
        }

        if (index > 0 &&
            ranges[index - 1].Start + ranges[index - 1].Length >= ranges[index].Start)
        {
            var current = ranges[index];
            var mergedStart = ranges[index - 1].Start;
            var mergedEnd = Math.Max(ranges[index - 1].Start + ranges[index - 1].Length, current.Start + current.Length);
            ranges[index - 1] = (mergedStart, mergedEnd - mergedStart);
            ranges.RemoveAt(index);
        }
    }

    private static int CountEmissive(VoxelGpuCell[] cells)
    {
        var count = 0;
        foreach (var cell in cells)
        {
            if (VoxelFlags.Has(cell.Value.Flags, VoxelFlags.Emissive)) count++;
        }

        return count;
    }
}
