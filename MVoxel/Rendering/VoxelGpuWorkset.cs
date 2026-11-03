using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// GPU 工作集中的单个图元：一个同值盒体。默认 1³ 即原始体素；
/// 贪心合并把相邻同值体素并成更大的盒，距离 LOD 用 2^k 大块近似远处区块
/// </summary>
/// <param name="X">相对于工作集原点的 X 坐标（盒体最小角）</param>
/// <param name="Y">相对于工作集原点的 Y 坐标（盒体最小角）</param>
/// <param name="Z">相对于工作集原点的 Z 坐标（盒体最小角）</param>
/// <param name="Value">体素材质与颜色</param>
/// <param name="ExtentX">盒体 X 方向格数，至少 1</param>
/// <param name="ExtentY">盒体 Y 方向格数，至少 1</param>
/// <param name="ExtentZ">盒体 Z 方向格数，至少 1</param>
public readonly record struct VoxelGpuCell(
    int X, int Y, int Z, VoxelValue Value, int ExtentX = 1, int ExtentY = 1, int ExtentZ = 1);

/// <summary>
/// 从稀疏世界生成的确定性 GPU 工作集快照
/// </summary>
/// <remarks>
/// 快照只保存实际占用的体素，坐标使用包围盒原点附近的 32 位局部坐标
/// 世界坐标仍由 Origin 保留为 64 位，因此远距离负坐标不会在同步时丢失
/// </remarks>
public sealed class VoxelGpuWorkset
{
    /// <summary>
    /// 空世界对应的空工作集
    /// </summary>
    public static VoxelGpuWorkset Empty { get; } = new(
        0,
        default,
        default,
        [],
        0);

    /// <summary>
    /// 创建工作集快照
    /// </summary>
    /// <param name="sourceVersion">源世界内容版本</param>
    /// <param name="origin">工作集原点</param>
    /// <param name="size">工作集包围盒尺寸</param>
    /// <param name="cells">按 Z、Y、X 排序的局部体素</param>
    /// <param name="emissiveCount">发光体素数量</param>
    private VoxelGpuWorkset(int sourceVersion, VoxelCoord64 origin, VoxelCoord64 size,
        VoxelGpuCell[] cells, int emissiveCount)
    {
        SourceVersion = sourceVersion;
        Origin = origin;
        Size = size;
        Cells = cells;
        EmissiveCount = emissiveCount;
    }

    /// <summary>
    /// 生成工作集的世界内容版本
    /// </summary>
    public int SourceVersion { get; }

    /// <summary>
    /// 为砖路径创建记账工作集：cells 直接持有砖池格序列（体素集合与传统路径相同，仅顺序不同），
    /// 供硬件 RT、区域参数与统计沿用现有读数
    /// </summary>
    internal static VoxelGpuWorkset CreateForBricks(int sourceVersion, VoxelCoord64 origin, VoxelCoord64 size,
        VoxelGpuCell[] brickCells, int emissiveCount)
    {
        return new VoxelGpuWorkset(sourceVersion, origin, size, brickCells, emissiveCount);
    }

    /// <summary>
    /// 局部坐标原点对应的世界坐标
    /// </summary>
    public VoxelCoord64 Origin { get; }

    /// <summary>
    /// 包围盒尺寸，三个分量均为正数时表示非空工作集
    /// </summary>
    public VoxelCoord64 Size { get; }

    /// <summary>
    /// 按 Z、Y、X 稳定排序的占用体素
    /// </summary>
    public IReadOnlyList<VoxelGpuCell> Cells { get; }

    /// <summary>
    /// 工作集中的发光体素数量
    /// </summary>
    public int EmissiveCount { get; }

    /// <summary>
    /// 是否没有占用体素
    /// </summary>
    public bool IsEmpty => Cells.Count == 0;

    /// <summary>
    /// 从稀疏世界创建 GPU 工作集
    /// </summary>
    /// <param name="world">源稀疏世界</param>
    /// <param name="maximumCells">工作集最大体素数，零表示不采样</param>
    /// <param name="lod">距离 LOD 配置，null 或关闭时全细节</param>
    /// <param name="cameraPosition">LOD 判距相机</param>
    /// <param name="greedyMerge">是否做贪心盒体合并；场景视图传 false 只显示原始体素，渲染与导出传 true</param>
    /// <returns>可直接上传到 GPU 的确定性快照</returns>
    /// <exception cref="ArgumentNullException">世界为空时抛出</exception>
    /// <exception cref="InvalidDataException">包围盒尺寸超出 GPU 局部坐标范围时抛出</exception>
    public static VoxelGpuWorkset Build(VoxelWorld world, int maximumCells = 0,
        VoxelLodSettings? lod = null, System.Numerics.Vector3? cameraPosition = null, bool greedyMerge = true,
        bool includeInterior = false)
    {
        return Build(world, maximumCells, lod, cameraPosition, greedyMerge, includeInterior, out _);
    }

    /// <summary>
    /// 从稀疏世界创建 GPU 工作集，并回传各阶段耗时供性能分析器定位热点
    /// </summary>
    /// <param name="world">源稀疏世界</param>
    /// <param name="maximumCells">工作集最大体素数，零表示不采样</param>
    /// <param name="lod">距离 LOD 配置，null 或关闭时全细节</param>
    /// <param name="cameraPosition">LOD 判距相机</param>
    /// <param name="greedyMerge">是否做贪心盒体合并；场景视图传 false 只显示原始体素，渲染与导出传 true</param>
    /// <param name="includeInterior">保留完全被包围的内部体素</param>
    /// <param name="timing">各阶段耗时</param>
    /// <returns>可直接上传到 GPU 的确定性快照</returns>
    public static VoxelGpuWorkset Build(VoxelWorld world, int maximumCells,
        VoxelLodSettings? lod, System.Numerics.Vector3? cameraPosition, bool greedyMerge,
        bool includeInterior, out VoxelWorksetBuildTiming timing)
    {
        ArgumentNullException.ThrowIfNull(world);
        timing = default;
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            return world.ContentVersion == 0
                ? Empty
                : new VoxelGpuWorkset(world.ContentVersion, default, default, [], 0);
        }

        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var size = new VoxelCoord64(
            CheckedExtent(minimum.X, maximum.X, nameof(minimum)),
            CheckedExtent(minimum.Y, maximum.Y, nameof(minimum)),
            CheckedExtent(minimum.Z, maximum.Z, nameof(minimum)));

        //按空间邻近顺序排列区块，减少大型场景BVH节点的包围盒重叠
        var chunks = world.SnapshotChunks();
        chunks.Sort((left, right) =>
        {
            var leftKey = MortonChunkKey(left.Coordinate, minimum, size);
            var rightKey = MortonChunkKey(right.Coordinate, minimum, size);
            var byKey = leftKey.CompareTo(rightKey);
            if (byKey != 0) return byKey;
            var byZ = left.Coordinate.Z.CompareTo(right.Coordinate.Z);
            if (byZ != 0) return byZ;
            var byY = left.Coordinate.Y.CompareTo(right.Coordinate.Y);
            return byY != 0 ? byY : left.Coordinate.X.CompareTo(right.Coordinate.X);
        });

        var chunkCount = chunks.Count;
        var lodActive = lod is { Enabled: true } && cameraPosition.HasValue;
        var snapshotMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();

        // 字典枚举顺序不稳定，先整体落成数组；排序与剔除分成独立并行阶段，
        // 既方便分别计时，也避免一个 lambda 里混着分配、排序和邻域查询
        var visiblePairs = new (int Key, VoxelValue Value)[chunkCount][];
        System.Threading.Tasks.Parallel.For(0, chunkCount, ChunkParallelOptions, chunkIndex =>
        {
            var chunk = chunks[chunkIndex].Chunk;
            var pairs = new (int Key, VoxelValue Value)[chunk.OccupiedCount];
            var pairIndex = 0;
            foreach (var pair in chunk.Snapshot)
            {
                pairs[pairIndex++] = (pair.Key, pair.Value);
            }

            visiblePairs[chunkIndex] = pairs;
        });
        var collectMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();

        // 局部索引天然落在 0 到 ChunkVolume 之间，用桶填充代替比较排序：
        // 结果只取决于索引本身，与字典枚举顺序无关，也不会在近乎有序的输入上退化。
        // 大场景里一个区块上万体素，比较排序在十几亿次比较上要花几百毫秒
        System.Threading.Tasks.Parallel.For(0, chunkCount, ChunkParallelOptions,
            chunkIndex => SortPairsByLocalIndex(visiblePairs[chunkIndex]));
        var sortMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();

        // 所有显示和导出路径均先经过这一层的全封闭体素剔除。源世界并不修改；
        // 这里只消除永远不能从外部射线命中的不透明内部格。随后预算采样作用于
        // 已剔除的可见工作集，而不是合并后的盒，避免把一个合并盒整块挖空。
        System.Threading.Tasks.Parallel.For(0, chunkCount, ChunkParallelOptions, chunkIndex =>
        {
            var pairs = visiblePairs[chunkIndex];
            visiblePairs[chunkIndex] = includeInterior
                ? pairs
                : CullFullyOccludedPairs(world, pairs, chunks[chunkIndex].Coordinate, greedyMerge);
        });
        var cullMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();

        var rawStarts = new long[chunkCount + 1];
        for (var index = 0; index < chunkCount; index++)
        {
            rawStarts[index + 1] = rawStarts[index] + visiblePairs[index].Length;
        }

        var rawTotal = (int)rawStarts[chunkCount];
        var sampleStride = !lodActive && maximumCells > 0 && rawTotal > maximumCells
            ? (rawTotal + maximumCells - 1) / maximumCells
            : 1;

        var perChunk = new List<VoxelGpuCell>[chunkCount];
        var chunkEmissive = new int[chunkCount];
        System.Threading.Tasks.Parallel.For(0, chunkCount, ChunkParallelOptions, chunkIndex =>
        {
            var (coordinate, _) = chunks[chunkIndex];
            var pairs = visiblePairs[chunkIndex];
            if (sampleStride != 1)
            {
                var kept = new List<(int Key, VoxelValue Value)>(pairs.Length / sampleStride + 1);
                for (var localIndex = 0; localIndex < pairs.Length; localIndex++)
                {
                    if ((rawStarts[chunkIndex] + localIndex) % sampleStride == 0)
                    {
                        kept.Add(pairs[localIndex]);
                    }
                }

                pairs = [.. kept];
            }

            var level = 0;
            if (lodActive)
            {
                level = lod!.LevelFor(ChunkDistance(cameraPosition!.Value, coordinate, minimum));
            }

            var list = new List<VoxelGpuCell>(pairs.Length);
            if (level == 0)
            {
                if (greedyMerge)
                {
                    GreedyMergeChunk(pairs, coordinate, minimum, list);
                }
                else
                {
                    // 场景视图：逐格原始体素，不做盒体合并，素描边与拾取按真实体素
                    foreach (var (key, value) in pairs)
                    {
                        var local = SparseVoxelChunk.FromIndex(key);
                        list.Add(new VoxelGpuCell(
                            (int)(coordinate.X * VoxelWorld.ChunkSize + local.X - minimum.X),
                            (int)(coordinate.Y * VoxelWorld.ChunkSize + local.Y - minimum.Y),
                            (int)(coordinate.Z * VoxelWorld.ChunkSize + local.Z - minimum.Z),
                            value));
                    }
                }
            }
            else
            {
                CoarsenChunk(pairs, coordinate, minimum, level, list);
            }

            // 发光面计数顺带在做完的列表上统计，省掉之后再全量扫一遍
            var emissive = 0;
            foreach (var cell in list)
            {
                if (VoxelFlags.Has(cell.Value.Flags, VoxelFlags.Emissive)) emissive++;
            }

            chunkEmissive[chunkIndex] = emissive;
            perChunk[chunkIndex] = list;
        });
        var mergeMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();

        var total = 0;
        var emissiveTotal = 0;
        var chunkStarts = new int[chunkCount + 1];
        for (var index = 0; index < chunkCount; index++)
        {
            chunkStarts[index] = total;
            total += perChunk[index].Count;
            emissiveTotal += chunkEmissive[index];
        }

        chunkStarts[chunkCount] = total;
        var cells = new VoxelGpuCell[total];
        System.Threading.Tasks.Parallel.For(0, chunkCount, ChunkParallelOptions, chunkIndex =>
        {
            var list = perChunk[chunkIndex];
            var write = chunkStarts[chunkIndex];
            for (var index = 0; index < list.Count; index++) cells[write + index] = list[index];
        });
        var gatherMs = phaseWatch.Elapsed.TotalMilliseconds;

        timing = new VoxelWorksetBuildTiming(snapshotMs, collectMs, sortMs, cullMs, mergeMs, gatherMs);
        return new VoxelGpuWorkset(world.ContentVersion, minimum, size, cells, emissiveTotal);
    }

    /// <summary>
    /// 工作集构建各阶段的耗时，单位毫秒
    /// </summary>
    public readonly record struct VoxelWorksetBuildTiming(
        double SnapshotMs, double CollectMs, double SortMs, double CullMs, double MergeMs, double GatherMs)
    {
        /// <summary>各阶段合计</summary>
        public double TotalMs => SnapshotMs + CollectMs + SortMs + CullMs + MergeMs + GatherMs;
    }

    /// <summary>
    /// 工作集并行阶段共享的线程预算
    /// </summary>
    private static readonly System.Threading.Tasks.ParallelOptions ChunkParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
    };

    private static uint MortonChunkKey(ChunkCoord64 coordinate, VoxelCoord64 minimum, VoxelCoord64 size)
    {
        static uint Quantize(long chunk, long min, long extent)
        {
            var position = (double)chunk * VoxelWorld.ChunkSize + VoxelWorld.ChunkSize * 0.5;
            return (uint)Math.Clamp((position - min) * 1023.0 / Math.Max(1.0, extent), 0.0, 1023.0);
        }
        static uint Spread(uint value)
        {
            value = (value | value << 16) & 0x030000FFu;
            value = (value | value << 8) & 0x0300F00Fu;
            value = (value | value << 4) & 0x030C30C3u;
            return (value | value << 2) & 0x09249249u;
        }
        return Spread(Quantize(coordinate.X, minimum.X, size.X)) |
               (Spread(Quantize(coordinate.Y, minimum.Y, size.Y)) << 1) |
               (Spread(Quantize(coordinate.Z, minimum.Z, size.Z)) << 2);
    }

    /// <summary>
    /// 从已排序的区块体素中移除完全不可见的内部不透明格，使用按线程复用的区块局部缓冲
    /// </summary>
    /// <param name="world">完整源世界</param>
    /// <param name="pairs">已按局部索引排序的区块体素</param>
    /// <param name="chunkCoordinate">区块坐标</param>
    /// <param name="preserveUniformSolids">同值实心体是否保留核心</param>
    internal static (int Key, VoxelValue Value)[] CullFullyOccludedPairs(VoxelWorld world,
        (int Key, VoxelValue Value)[] pairs, ChunkCoord64 chunkCoordinate, bool preserveUniformSolids) =>
        CullFullyOccludedPairs(world, pairs, chunkCoordinate, preserveUniformSolids, RentLocalCells());

    /// <summary>按线程复用的区块局部缓冲，进出由剔除方法负责清空</summary>
    [ThreadStatic]
    private static VoxelValue[]? _threadLocalCells;

    /// <summary>按线程复用的排序桶，记录每个局部索引当前属于第几代填充</summary>
    [ThreadStatic]
    private static VoxelValue[]? _sortBuckets;

    [ThreadStatic]
    private static int[]? _sortStamps;

    [ThreadStatic]
    private static int _sortGeneration;

    private static VoxelValue[] RentLocalCells() =>
        _threadLocalCells ??= new VoxelValue[VoxelWorld.ChunkVolume];

    /// <summary>
    /// 按局部索引升序排列区块内体素，使用有界索引的桶填充
    /// </summary>
    /// <param name="pairs">区块内的体素与局部索引</param>
    private static void SortPairsByLocalIndex((int Key, VoxelValue Value)[] pairs)
    {
        if (pairs.Length <= 1) return;
        var values = _sortBuckets ??= new VoxelValue[VoxelWorld.ChunkVolume];
        var stamps = _sortStamps ??= new int[VoxelWorld.ChunkVolume];
        // 用代次标记代替每次清空三万个槽位，连续排序时不产生多余写
        if (++_sortGeneration == int.MaxValue)
        {
            Array.Clear(stamps);
            _sortGeneration = 1;
        }

        var generation = _sortGeneration;
        for (var index = 0; index < pairs.Length; index++)
        {
            var key = pairs[index].Key;
            values[key] = pairs[index].Value;
            stamps[key] = generation;
        }

        var write = 0;
        for (var key = 0; key < VoxelWorld.ChunkVolume && write < pairs.Length; key++)
        {
            if (stamps[key] != generation) continue;
            pairs[write++] = (key, values[key]);
        }
    }

    /// <summary>
    /// 从已排序的区块体素中移除完全不可见的内部不透明格。该方法也由砖块工作集调用，
    /// 确保场景预览、实时/路径渲染和导出使用同一条可见性规则。
    /// </summary>
    /// <param name="world">完整源世界，只用于查询区块边界外一格的邻居</param>
    /// <param name="pairs">已按局部索引排序的区块体素</param>
    /// <param name="chunkCoordinate">区块坐标</param>
    /// <param name="preserveUniformSolids">同值实心体是否保留核心</param>
    /// <param name="localCells">
    /// 调用方提供的区块局部密集缓冲，长度至少为 <see cref="VoxelWorld.ChunkVolume"/>，
    /// 进出都必须是空值。区块内部的邻居直接查这张表，只有贴边的体素才回落到世界查询
    /// </param>
    internal static (int Key, VoxelValue Value)[] CullFullyOccludedPairs(VoxelWorld world,
        (int Key, VoxelValue Value)[] pairs, ChunkCoord64 chunkCoordinate, bool preserveUniformSolids,
        VoxelValue[] localCells)
    {
        if (pairs.Length == 0) return pairs;
        if (localCells.Length < VoxelWorld.ChunkVolume)
        {
            throw new ArgumentException("区块局部缓冲长度不足", nameof(localCells));
        }

        // 先把本区块写进密集数组：区块内邻居查询因此退化成一次数组下标，
        // 只有贴在区块六个面上的体素才需要回落到世界查询
        foreach (var pair in pairs)
        {
            localCells[pair.Key] = pair.Value;
        }

        Span<VoxelValue> neighbors = stackalloc VoxelValue[VoxelRenderVisibility.NeighborCount];
        var baseX = chunkCoordinate.X * VoxelWorld.ChunkSize;
        var baseY = chunkCoordinate.Y * VoxelWorld.ChunkSize;
        var baseZ = chunkCoordinate.Z * VoxelWorld.ChunkSize;
        try
        {
            List<(int Key, VoxelValue Value)>? retained = null;
            for (var index = 0; index < pairs.Length; index++)
            {
                var pair = pairs[index];
                var key = pair.Key;
                var local = SparseVoxelChunk.FromIndex(key);
                var worldX = baseX + local.X;
                var worldY = baseY + local.Y;
                var worldZ = baseZ + local.Z;
                neighbors[0] = local.X > 0 ? localCells[key - 1]
                    : NeighborAt(world, worldX - 1, worldY, worldZ);
                neighbors[1] = local.X < VoxelWorld.ChunkSize - 1 ? localCells[key + 1]
                    : NeighborAt(world, worldX + 1, worldY, worldZ);
                neighbors[2] = local.Y > 0 ? localCells[key - VoxelWorld.ChunkSize]
                    : NeighborAt(world, worldX, worldY - 1, worldZ);
                neighbors[3] = local.Y < VoxelWorld.ChunkSize - 1 ? localCells[key + VoxelWorld.ChunkSize]
                    : NeighborAt(world, worldX, worldY + 1, worldZ);
                neighbors[4] = local.Z > 0 ? localCells[key - ChunkLayer]
                    : NeighborAt(world, worldX, worldY, worldZ - 1);
                neighbors[5] = local.Z < VoxelWorld.ChunkSize - 1 ? localCells[key + ChunkLayer]
                    : NeighborAt(world, worldX, worldY, worldZ + 1);

                if (!VoxelRenderVisibility.CanCullInterior(pair.Value, neighbors, preserveUniformSolids))
                {
                    retained?.Add(pair);
                    continue;
                }

                if (retained is null)
                {
                    retained = new List<(int Key, VoxelValue Value)>(pairs.Length);
                    for (var previous = 0; previous < index; previous++) retained.Add(pairs[previous]);
                }
            }

            return retained is null ? pairs : [.. retained];
        }
        finally
        {
            // 缓冲由线程复用，离开时必须还原为空值。
            // 整块顺序清零比按占用下标逐个回写便宜一个数量级：
            // 一个区块上万个零散随机写入，折成一次连续的内存清零
            Array.Clear(localCells);
        }
    }

    /// <summary>区块一层的体素数量，用于 Z 方向邻居的索引偏移</summary>
    private const int ChunkLayer = VoxelWorld.ChunkSize * VoxelWorld.ChunkSize;

    /// <summary>读取区块外一格的邻居，缺失按空值处理</summary>
    private static VoxelValue NeighborAt(VoxelWorld world, long x, long y, long z) =>
        world.TryGet(new VoxelCoord64(x, y, z), out var value) ? value : default;

    /// <summary>
    /// 相机到区块世界 AABB 的最短距离（格）。区块覆盖世界坐标
    /// [coordinate*32, coordinate*32+31]，相机坐标已是世界系
    /// </summary>
    private static float ChunkDistance(System.Numerics.Vector3 camera, ChunkCoord64 coordinate, VoxelCoord64 minimum)
    {
        _ = minimum;
        var sum = 0F;
        var lowX = coordinate.X * VoxelWorld.ChunkSize;
        var lowY = coordinate.Y * VoxelWorld.ChunkSize;
        var lowZ = coordinate.Z * VoxelWorld.ChunkSize;
        var highX = lowX + VoxelWorld.ChunkSize - 1;
        var highY = lowY + VoxelWorld.ChunkSize - 1;
        var highZ = lowZ + VoxelWorld.ChunkSize - 1;
        var dx = camera.X < lowX ? (float)(lowX - camera.X) : camera.X > highX ? (float)(camera.X - highX) : 0F;
        var dy = camera.Y < lowY ? (float)(lowY - camera.Y) : camera.Y > highY ? (float)(camera.Y - highY) : 0F;
        var dz = camera.Z < lowZ ? (float)(lowZ - camera.Z) : camera.Z > highZ ? (float)(camera.Z - highZ) : 0F;
        sum = dx * dx + dy * dy + dz * dz;
        return MathF.Sqrt(sum);
    }

    /// <summary>
    /// 区块内贪心盒体合并：按 Z、Y、X 稳定序取种子，先沿 X 扩同值游程，
    /// 再沿 Y 扩成板、沿 Z 扩成块；合并盒严格同值且互不重叠，
    /// 最近命中结果与逐格表示逐像素一致
    /// </summary>
    internal static void GreedyMergeChunk((int Key, VoxelValue Value)[] pairs, ChunkCoord64 coordinate,
        VoxelCoord64 minimum, List<VoxelGpuCell> output)
    {
        var lookup = new Dictionary<int, VoxelValue>(pairs.Length);
        foreach (var (key, value) in pairs)
        {
            lookup[key] = value;
        }

        var visited = new bool[VoxelWorld.ChunkVolume];
        foreach (var (key, value) in pairs)
        {
            if (visited[key])
            {
                continue;
            }

            var origin = SparseVoxelChunk.FromIndex(key);
            var extentX = 1;
            while (origin.X + extentX < VoxelWorld.ChunkSize && TryUnvisited(lookup, visited,
                       SparseVoxelChunk.ToIndex(new LocalCoord(origin.X + extentX, origin.Y, origin.Z)), value))
            {
                extentX++;
            }

            var extentY = 1;
            while (origin.Y + extentY < VoxelWorld.ChunkSize)
            {
                var canExpandPlate = true;
                for (var dx = 0; dx < extentX; dx++)
                {
                    if (!TryUnvisited(lookup, visited, SparseVoxelChunk.ToIndex(
                            new LocalCoord(origin.X + dx, origin.Y + extentY, origin.Z)), value))
                    {
                        canExpandPlate = false;
                        break;
                    }
                }

                if (!canExpandPlate)
                {
                    break;
                }

                extentY++;
            }

            var extentZ = 1;
            while (origin.Z + extentZ < VoxelWorld.ChunkSize)
            {
                var canExpandBlock = true;
                for (var dy = 0; dy < extentY; dy++)
                for (var dx = 0; dx < extentX; dx++)
                {
                    if (!TryUnvisited(lookup, visited, SparseVoxelChunk.ToIndex(
                            new LocalCoord(origin.X + dx, origin.Y + dy, origin.Z + extentZ)), value))
                    {
                        canExpandBlock = false;
                        break;
                    }
                }

                if (!canExpandBlock)
                {
                    break;
                }

                extentZ++;
            }

            for (var z = 0; z < extentZ; z++)
            for (var y = 0; y < extentY; y++)
            for (var x = 0; x < extentX; x++)
            {
                visited[SparseVoxelChunk.ToIndex(new LocalCoord(origin.X + x, origin.Y + y, origin.Z + z))] = true;
            }

            output.Add(new VoxelGpuCell(
                (int)(coordinate.X * VoxelWorld.ChunkSize + origin.X - minimum.X),
                (int)(coordinate.Y * VoxelWorld.ChunkSize + origin.Y - minimum.Y),
                (int)(coordinate.Z * VoxelWorld.ChunkSize + origin.Z - minimum.Z),
                value, extentX, extentY, extentZ));
        }
    }

    /// <summary>
    /// 判断某局部格是否未访问且与种子同值
    /// </summary>
    private static bool TryUnvisited(Dictionary<int, VoxelValue> lookup, bool[] visited, int key, VoxelValue expected) =>
        !visited[key] && lookup.TryGetValue(key, out var value) && value == expected;

    /// <summary>
    /// 距离 LOD 粗化：区块按 2^level 网格分桶，每桶取稳定序首个体素的值发射一个 2^k 大块
    /// </summary>
    private static void CoarsenChunk((int Key, VoxelValue Value)[] pairs, ChunkCoord64 coordinate,
        VoxelCoord64 minimum, int level, List<VoxelGpuCell> output)
    {
        var blockSize = Math.Min(1 << level, VoxelWorld.ChunkSize);
        var seen = new HashSet<int>();
        foreach (var (key, value) in pairs)
        {
            var local = SparseVoxelChunk.FromIndex(key);
            var blockKey = (local.Z / blockSize * (VoxelWorld.ChunkSize / blockSize) + local.Y / blockSize)
                * (VoxelWorld.ChunkSize / blockSize) + local.X / blockSize;
            if (!seen.Add(blockKey))
            {
                continue;
            }

            output.Add(new VoxelGpuCell(
                (int)(coordinate.X * VoxelWorld.ChunkSize + local.X / blockSize * blockSize - minimum.X),
                (int)(coordinate.Y * VoxelWorld.ChunkSize + local.Y / blockSize * blockSize - minimum.Y),
                (int)(coordinate.Z * VoxelWorld.ChunkSize + local.Z / blockSize * blockSize - minimum.Z),
                value, blockSize, blockSize, blockSize));
        }
    }

    /// <summary>
    /// 转换为参数块使用的三维整数尺寸
    /// </summary>
    /// <returns>GPU 参数块中的区域尺寸</returns>
    public VoxelRenderParameters.Vector3I ToParameterSize() => new(
        checked((int)Size.X),
        checked((int)Size.Y),
        checked((int)Size.Z));

    /// <summary>
    /// 转换为参数块使用的三维整数原点
    /// </summary>
    /// <returns>GPU 参数块中的区域原点</returns>
    public VoxelRenderParameters.Vector3I ToParameterOrigin()
    {
        return new(
            checked((int)Origin.X),
            checked((int)Origin.Y),
            checked((int)Origin.Z));
    }

    /// <summary>
    /// 计算包含首尾坐标的包围盒尺寸并检查局部坐标范围
    /// </summary>
    /// <param name="minimum">包围盒最小坐标</param>
    /// <param name="maximum">包围盒最大坐标</param>
    /// <param name="parameterName">参数名</param>
    /// <returns>包围盒尺寸</returns>
    private static long CheckedExtent(long minimum, long maximum, string parameterName)
    {
        var extent = checked(maximum - minimum + 1L);
        if (extent <= 0 || extent > int.MaxValue)
        {
            throw new InvalidDataException($"GPU 工作集{parameterName}范围超出 32 位局部坐标限制");
        }

        return extent;
    }
}
