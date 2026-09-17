using System.Collections.Concurrent;
using MVoxel.Core;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// 导入结果中的一个分层世界，例如一棵独立的树
/// </summary>
/// <param name="Name">分层显示名，如“树 1”</param>
/// <param name="World">该分层独占的体素世界，坐标与地形世界同一空间</param>
public sealed record McImportLayer(string Name, VoxelWorld World);

/// <summary>
/// 导入流程单阶段耗时记录，毫秒计，供性能基准对比
/// </summary>
/// <param name="Name">阶段名，如“区块解析与合并”</param>
/// <param name="Milliseconds">该阶段耗时毫秒数</param>
public sealed record McPhaseTiming(string Name, double Milliseconds);

/// <summary>
/// 存档区域转换结果：地形世界与拆分出的分层列表
/// </summary>
/// <param name="Terrain">去除分层后的地形世界</param>
/// <param name="Layers">从地形中拆出的分层，当前为检测到的树</param>
/// <param name="Timings">导入各阶段的耗时记录</param>
public sealed record McImportResult(VoxelWorld Terrain, IReadOnlyList<McImportLayer> Layers,
    IReadOnlyList<McPhaseTiming> Timings);

/// <summary>
/// 把 Minecraft 存档中一个区块范围转换为 MVoxel 体素世界，
/// 并把植被连通域拆分为独立分层，供场景层级展示
/// </summary>
public static class McWorldImporter
{
    /// <summary>
    /// 单次导入允许的最大区块数
    /// </summary>
    public const int MaximumSelectionChunks = 4096;

    /// <summary>
    /// 植被簇被识别为树的最小体素数
    /// </summary>
    public const int MinimumTreeVoxels = 24;

    /// <summary>
    /// 单次导入最多拆出的树数量
    /// </summary>
    public const int MaximumTrees = 48;

    /// <summary>
    /// 把选中区块范围内的方块转换为体素世界
    /// </summary>
    /// <param name="info">存档检查结果，提供存档路径与 Y 基准</param>
    /// <param name="minChunkX">选中区块 X 最小值</param>
    /// <param name="minChunkZ">选中区块 Z 最小值</param>
    /// <param name="maxChunkX">选中区块 X 最大值</param>
    /// <param name="maxChunkZ">选中区块 Z 最大值</param>
    /// <param name="progress">可选进度回调，文本形如“已解析 37/256 区块”</param>
    /// <returns>地形与分层的转换结果</returns>
    public static McImportResult ConvertRange(MinecraftSaveInfo info,
        long minChunkX, long minChunkZ, long maxChunkX, long maxChunkZ,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        (minChunkX, maxChunkX) = (Math.Min(minChunkX, maxChunkX), Math.Max(minChunkX, maxChunkX));
        (minChunkZ, maxChunkZ) = (Math.Min(minChunkZ, maxChunkZ), Math.Max(minChunkZ, maxChunkZ));
        var widthX = maxChunkX - minChunkX + 1;
        var widthZ = maxChunkZ - minChunkZ + 1;
        if (widthX * widthZ > MaximumSelectionChunks)
        {
            throw new InvalidDataException($"选中区块数 {widthX * widthZ} 超出单次导入上限 {MaximumSelectionChunks}");
        }

        var originX = minChunkX << 4;
        var originZ = minChunkZ << 4;
        var yBase = info.MinBlockY;
        var full = new VoxelWorld();
        using var regions = new RegionCache(info.RootPath, info.Dimension.RegionDirectory);
        var chunkCoords = new List<(long X, long Z)>();
        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
        {
            chunkCoords.Add((chunkX, chunkZ));
        }

        // 选中区块按世界区块列做 2×2 分组：一组产出的体素在世界区块键上完全落在本组列内，
        // 组与组互不相交，因此解析与稀疏区块构建全程无锁，只在整块收养时短暂持有世界锁
        var timings = new List<McPhaseTiming>(2);
        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var (groups, groupByKey) = BuildChunkGroups(chunkCoords);
        var groupResults = new GroupResult[groups.Count];
        var mergeGate = new object();
        var processed = 0;
        Parallel.For(0, groups.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16) },
            groupIndex =>
            {
                var result = new GroupResult();
                foreach (var (chunkX, chunkZ) in groups[groupIndex].Chunks)
                {
                    var root = regions.ReadChunk(chunkX, chunkZ);
                    if (root is not null)
                    {
                        CollectChunkIntoGroup(result, AnvilChunk.FromNbt(root),
                            originX + (chunkX << 4), originZ + (chunkZ << 4), yBase);
                    }

                    var done = Interlocked.Increment(ref processed);
                    if (done % 64 == 1)
                    {
                        progress?.Invoke($"已解析 {done}/{chunkCoords.Count} 区块");
                    }
                }

                groupResults[groupIndex] = result;
                lock (mergeGate)
                {
                    foreach (var (coordinate, chunk) in result.Chunks)
                    {
                        full.AdoptChunk(coordinate, chunk);
                    }
                }
            });
        timings.Add(new McPhaseTiming("区块解析与合并", phaseWatch.Elapsed.TotalMilliseconds));

        phaseWatch.Restart();
        var layers = SplitTrees(full, groupResults, groupByKey);
        timings.Add(new McPhaseTiming("树分层拆分", phaseWatch.Elapsed.TotalMilliseconds));
        progress?.Invoke($"已拆出 {layers.Count} 个树分层");
        return new McImportResult(full, layers, timings);
    }

    /// <summary>
    /// 一个世界区块列分组的解析任务
    /// </summary>
    /// <param name="Column">世界区块列 X 键</param>
    /// <param name="Row">世界区块列 Z 键</param>
    /// <param name="Chunks">组内待解析的区块坐标</param>
    private sealed record ChunkGroup(long Column, long Row, List<(long X, long Z)> Chunks);

    /// <summary>
    /// 把选中区块按世界区块列归组：区块 X 与 Z 各向下取整除二相同的区块落入同一组，
    /// 组的体素在世界区块键上不会与任何其他组相交；同时给出列键到组下标的查找表
    /// </summary>
    private static (List<ChunkGroup> Groups, Dictionary<(long, long), int> GroupByKey) BuildChunkGroups(
        List<(long X, long Z)> chunkCoords)
    {
        var byKey = new Dictionary<(long, long), ChunkGroup>();
        var indexByKey = new Dictionary<(long, long), int>();
        var groups = new List<ChunkGroup>();
        foreach (var (x, z) in chunkCoords)
        {
            var key = (x >> 1, z >> 1);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = new ChunkGroup(key.Item1, key.Item2, new List<(long, long)>(4));
                byKey[key] = group;
                indexByKey[key] = groups.Count;
                groups.Add(group);
            }

            group.Chunks.Add((x, z));
        }

        return (groups, indexByKey);
    }

    /// <summary>
    /// 单个分组的本地解析产物：构建好的稀疏区块、植被与树干坐标集合，
    /// 以及并行连通域拆分阶段回填的本地分量与跨界边
    /// </summary>
    private sealed class GroupResult
    {
        /// <summary>
        /// 本组独占的世界区块键到稀疏区块的映射
        /// </summary>
        public readonly Dictionary<ChunkCoord64, SparseVoxelChunk> Chunks = new();

        /// <summary>
        /// 本组的植被体素坐标集合
        /// </summary>
        public readonly HashSet<VoxelCoord64> Vegetation = new();

        /// <summary>
        /// 本组的树干体素坐标集合
        /// </summary>
        public readonly HashSet<VoxelCoord64> Logs = new();

        /// <summary>
        /// 本组 BFS 产出的本地连通分量
        /// </summary>
        public readonly List<LocalComponent> Components = new();

        /// <summary>
        /// 本组通向其他分组的六邻接跨界边，端点都是本组已标记的植被坐标
        /// </summary>
        public readonly List<(VoxelCoord64 From, VoxelCoord64 To)> BoundaryEdges = new();
    }

    /// <summary>
    /// 一个分组内的植被局部连通分量，等待并查集跨组合并
    /// </summary>
    private sealed class LocalComponent
    {
        /// <summary>
        /// 全局分量编号
        /// </summary>
        public int Id;

        /// <summary>
        /// 分量包含的植被体素数
        /// </summary>
        public int Size;

        /// <summary>
        /// 分量内是否存在树干体素
        /// </summary>
        public bool HasLog;

        /// <summary>
        /// 分量内最小坐标，用于同尺寸分层的确定性排序
        /// </summary>
        public VoxelCoord64 Min;
    }

    /// <summary>
    /// 一个方块的映射结果缓存：颜色值与植被判定，按方块名复用
    /// </summary>
    private readonly record struct BlockMapping(VoxelValue Value, bool Vegetation, bool Log);

    /// <summary>
    /// 解析一个区块内全部非空气方块并直接写入分组的稀疏区块；
    /// 世界区块键与局部索引用移位与掩码推导，负坐标按向下取整语义成立；
    /// 方块名到颜色与特性的映射按区块缓存，同一方块名只查一次色表
    /// </summary>
    private static void CollectChunkIntoGroup(GroupResult target, AnvilChunk chunk,
        long blockOriginX, long blockOriginZ, int yBase)
    {
        var mappings = new Dictionary<string, BlockMapping>(StringComparer.Ordinal);
        foreach (var section in chunk.Sections)
        {
            if (section.Y < -16 || section.Y > 1023 || IsAirOnlySection(section))
            {
                continue;
            }

            for (var y = 0; y < 16; y++)
            {
                var blockY = section.BaseY + y - yBase;
                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var name = section.BlockAt(x, y, z);
                        if (IsAirName(name))
                        {
                            continue;
                        }

                        if (!mappings.TryGetValue(name, out var mapping))
                        {
                            var flags = VoxelFlags.None;
                            if (MinecraftBlockColors.IsTranslucent(name)) flags |= VoxelFlags.Translucent;
                            if (MinecraftBlockColors.IsEmissive(name)) flags |= VoxelFlags.Emissive;
                            var isVegetation = IsVegetationName(name);
                            mapping = new BlockMapping(
                                new VoxelValue(MinecraftBlockColors.ColorOf(name), 0, flags),
                                isVegetation,
                                isVegetation && name.Contains("_log", StringComparison.Ordinal));
                            mappings[name] = mapping;
                        }

                        var blockX = blockOriginX + x;
                        var blockZ = blockOriginZ + z;
                        var chunkCoordinate = new ChunkCoord64(blockX >> 5, blockY >> 5, blockZ >> 5);
                        if (!target.Chunks.TryGetValue(chunkCoordinate, out var sparseChunk))
                        {
                            sparseChunk = new SparseVoxelChunk();
                            target.Chunks[chunkCoordinate] = sparseChunk;
                        }

                        sparseChunk.Set(new LocalCoord((int)(blockX & 31), (int)(blockY & 31), (int)(blockZ & 31)),
                            mapping.Value);
                        if (mapping.Vegetation)
                        {
                            var voxelCoordinate = new VoxelCoord64(blockX, blockY, blockZ);
                            target.Vegetation.Add(voxelCoordinate);
                            if (mapping.Log)
                            {
                                target.Logs.Add(voxelCoordinate);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 判断 section 的 palette 是否只有空气，用于整段跳过
    /// </summary>
    private static bool IsAirOnlySection(AnvilSection section)
    {
        return section.Palette.Count == 0 ||
               (section.Palette.Count == 1 && IsAirName(section.Palette[0]));
    }

    /// <summary>
    /// 判断方块名称是否属于空气，空气体素不进入世界
    /// </summary>
    private static bool IsAirName(string name)
    {
        return name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";
    }

    /// <summary>
    /// 判断方块名称是否属于植被，植被是树分层的判定原料
    /// </summary>
    private static bool IsVegetationName(string name)
    {
        return name.Contains("_log", StringComparison.Ordinal) ||
               name.EndsWith("_leaves", StringComparison.Ordinal) ||
               name.EndsWith("_stem", StringComparison.Ordinal) ||
               name is "minecraft:azalea" or "minecraft:flowering_azalea" or "minecraft:mangrove_propagule";
    }

    /// <summary>
    /// 用六邻接连通域并行拆分树分层：各组先在本地植被集合上做 BFS 得到局部分量与跨界边，
    /// 跨界判定由邻居坐标直接推导所属列组并查其本地植被集合，不需要全局植被表；
    /// 并查集跨组合并后成树条件与串行版一致，簇足够大且含树干才移出地形成为独立分层，
    /// 命名按尺寸降序与最小坐标升序确定
    /// </summary>
    private static List<McImportLayer> SplitTrees(VoxelWorld full, GroupResult[] groups,
        Dictionary<(long Column, long Row), int> groupByKey)
    {
        var labels = new ConcurrentDictionary<VoxelCoord64, int>();
        var componentCounter = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16) };
        Parallel.For(0, groups.Length, options, groupIndex =>
        {
            var group = groups[groupIndex];
            if (group.Vegetation.Count == 0)
            {
                return;
            }

            var pending = new HashSet<VoxelCoord64>(group.Vegetation);
            var queue = new Queue<VoxelCoord64>();
            while (pending.Count > 0)
            {
                VoxelCoord64 seed = default;
                foreach (var item in pending)
                {
                    seed = item;
                    break;
                }

                var component = new LocalComponent { Id = Interlocked.Increment(ref componentCounter) };
                queue.Enqueue(seed);
                pending.Remove(seed);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (component.Size == 0 || CompareCoordinate(current, component.Min) < 0)
                    {
                        component.Min = current;
                    }

                    component.Size++;
                    component.HasLog |= group.Logs.Contains(current);
                    labels[current] = component.Id;
                    TryExpand(current, 1, 0, 0, groupIndex, group, groups, groupByKey, pending, queue);
                    TryExpand(current, -1, 0, 0, groupIndex, group, groups, groupByKey, pending, queue);
                    TryExpand(current, 0, 1, 0, groupIndex, group, groups, groupByKey, pending, queue);
                    TryExpand(current, 0, -1, 0, groupIndex, group, groups, groupByKey, pending, queue);
                    TryExpand(current, 0, 0, 1, groupIndex, group, groups, groupByKey, pending, queue);
                    TryExpand(current, 0, 0, -1, groupIndex, group, groups, groupByKey, pending, queue);
                }

                group.Components.Add(component);
            }
        });

        // 并查集按跨界边合并局部分量，得到与串行 BFS 相同的全局连通簇
        var parent = new int[componentCounter + 1];
        for (var id = 0; id < parent.Length; id++)
        {
            parent[id] = id;
        }

        foreach (var group in groups)
        {
            foreach (var (from, to) in group.BoundaryEdges)
            {
                Union(parent, labels[from], labels[to]);
            }
        }

        var clusters = new Dictionary<int, LocalComponent>();
        foreach (var group in groups)
        {
            foreach (var component in group.Components)
            {
                var root = Find(parent, component.Id);
                if (!clusters.TryGetValue(root, out var merged))
                {
                    merged = new LocalComponent { Id = root, Min = component.Min };
                    clusters[root] = merged;
                }

                merged.Size += component.Size;
                merged.HasLog |= component.HasLog;
                if (CompareCoordinate(component.Min, merged.Min) < 0)
                {
                    merged.Min = component.Min;
                }
            }
        }

        var selected = clusters.Values
            .Where(cluster => cluster.Size >= MinimumTreeVoxels && cluster.HasLog)
            .OrderByDescending(cluster => cluster.Size)
            .ThenBy(cluster => cluster.Min, Comparer<VoxelCoord64>.Create(CompareCoordinate))
            .Take(MaximumTrees)
            .ToList();
        if (selected.Count == 0)
        {
            return [];
        }

        var clusterIndexByRoot = new int[parent.Length];
        Array.Fill(clusterIndexByRoot, -1);
        for (var index = 0; index < selected.Count; index++)
        {
            clusterIndexByRoot[selected[index].Id] = index;
        }

        // 先把并查集根拍平成只读表，后续并行收集只查表，不再触发路径压缩写入
        var rootOf = new int[parent.Length];
        for (var id = 0; id < rootOf.Length; id++)
        {
            rootOf[id] = Find(parent, id);
        }

        // 每个植被体素按分量所属簇归位，组间并行收集，串行汇总
        var clusterVoxels = new List<List<VoxelCoord64>>(selected.Count);
        for (var index = 0; index < selected.Count; index++)
        {
            clusterVoxels.Add([]);
        }

        var perGroup = new List<VoxelCoord64>?[groups.Length][];
        Parallel.For(0, groups.Length, options, groupIndex =>
        {
            var local = new List<VoxelCoord64>?[selected.Count];
            foreach (var voxel in groups[groupIndex].Vegetation)
            {
                var clusterIndex = clusterIndexByRoot[rootOf[labels[voxel]]];
                if (clusterIndex < 0)
                {
                    continue;
                }

                local[clusterIndex] ??= [];
                local[clusterIndex]!.Add(voxel);
            }

            perGroup[groupIndex] = local;
        });

        for (var groupIndex = 0; groupIndex < perGroup.Length; groupIndex++)
        {
            var local = perGroup[groupIndex];
            if (local is null)
            {
                continue;
            }

            for (var index = 0; index < local.Length; index++)
            {
                if (local[index] is { } list)
                {
                    clusterVoxels[index].AddRange(list);
                }
            }
        }

        var result = new List<McImportLayer>();
        foreach (var voxels in clusterVoxels)
        {
            var layer = new VoxelWorld();
            foreach (var coordinate in voxels)
            {
                if (full.TryGet(coordinate, out var value))
                {
                    layer.Set(coordinate, value);
                    full.Remove(coordinate);
                }
            }

            result.Add(new McImportLayer($"树 {result.Count + 1}", layer));
        }

        return result;
    }

    /// <summary>
    /// BFS 单方向扩展：邻点若在本组待访问集中则入队；若属于本组植被说明已在本分量内，不产边；
    /// 否则由坐标推导其所在列组，命中该组植被时记录为跨界边
    /// </summary>
    private static void TryExpand(VoxelCoord64 source, long dx, long dy, long dz, int groupIndex,
        GroupResult group, GroupResult[] groups, Dictionary<(long Column, long Row), int> groupByKey,
        HashSet<VoxelCoord64> pending, Queue<VoxelCoord64> queue)
    {
        var next = new VoxelCoord64(source.X + dx, source.Y + dy, source.Z + dz);
        if (pending.Remove(next))
        {
            queue.Enqueue(next);
            return;
        }

        if (group.Vegetation.Contains(next))
        {
            return;
        }

        if (groupByKey.TryGetValue((next.X >> 5, next.Z >> 5), out var otherIndex) &&
            groups[otherIndex].Vegetation.Contains(next))
        {
            group.BoundaryEdges.Add((source, next));
        }
    }

    /// <summary>
    /// 并查集查找，带路径压缩
    /// </summary>
    private static int Find(int[] parent, int value)
    {
        var root = value;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[value] != root)
        {
            (value, parent[value]) = (parent[value], root);
        }

        return root;
    }

    /// <summary>
    /// 并查集合并两棵树的根，右侧根指向左侧根
    /// </summary>
    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }

    /// <summary>
    /// 按 X、Y、Z 字典序比较两个体素坐标，用于同尺寸分层的确定性命名
    /// </summary>
    private static int CompareCoordinate(VoxelCoord64 left, VoxelCoord64 right)
    {
        var byX = left.X.CompareTo(right.X);
        if (byX != 0)
        {
            return byX;
        }

        var byY = left.Y.CompareTo(right.Y);
        return byY != 0 ? byY : left.Z.CompareTo(right.Z);
    }

    /// <summary>
    /// region 文件缓存：按需打开所属 r.X.Z.mca，供区块读取复用，可被多线程并发访问
    /// </summary>
    private sealed class RegionCache : IDisposable
    {
        private readonly string _regionDirectory;
        private readonly ConcurrentDictionary<(int X, int Z), AnvilRegionFile?> _regions = new();
        private readonly object _openGate = new();

        public RegionCache(string saveRootPath, string regionDirectory)
        {
            _regionDirectory = Path.Combine(saveRootPath, regionDirectory.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// 读取世界区块坐标处的区块 NBT，未生成时返回 null
        /// </summary>
        public NbtCompound? ReadChunk(long chunkX, long chunkZ)
        {
            var regionX = FloorDiv(chunkX, 32);
            var regionZ = FloorDiv(chunkZ, 32);
            if (!_regions.TryGetValue((regionX, regionZ), out var region))
            {
                lock (_openGate)
                {
                    if (!_regions.TryGetValue((regionX, regionZ), out region))
                    {
                        var path = Path.Combine(_regionDirectory, $"r.{regionX}.{regionZ}.mca");
                        region = File.Exists(path) ? AnvilRegionFile.Open(path) : null;
                        _regions[(regionX, regionZ)] = region;
                    }
                }
            }

            return region?.ReadChunk((int)(chunkX - regionX * 32), (int)(chunkZ - regionZ * 32));
        }

        /// <summary>
        /// 释放全部已打开的 region 文件
        /// </summary>
        public void Dispose()
        {
            foreach (var region in _regions.Values)
            {
                region?.Dispose();
            }

            _regions.Clear();
        }
    }

    /// <summary>
    /// 向下取整除法，负区块坐标也落在正确的 region
    /// </summary>
    private static int FloorDiv(long value, int divisor)
    {
        return (int)Math.Floor((double)value / divisor);
    }
}
