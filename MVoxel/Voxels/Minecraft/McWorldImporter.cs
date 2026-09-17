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
/// 存档区域转换结果：地形世界与拆分出的分层列表
/// </summary>
/// <param name="Terrain">去除分层后的地形世界</param>
/// <param name="Layers">从地形中拆出的分层，当前为检测到的树</param>
public sealed record McImportResult(VoxelWorld Terrain, IReadOnlyList<McImportLayer> Layers);

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
        var vegetation = new HashSet<VoxelCoord64>();
        var logs = new HashSet<VoxelCoord64>();
        using var regions = new RegionCache(info.RootPath, info.Dimension.RegionDirectory);
        var chunkCoords = new List<(long X, long Z)>();
        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
        {
            chunkCoords.Add((chunkX, chunkZ));
        }

        // 区块解析在多核上并行：本地收集后逐区块合并，内存只保留单个区块的缓冲，
        // 合并对世界加锁串行执行，吞吐瓶颈在解析端
        var mergeGate = new object();
        var processed = 0;
        Parallel.ForEach(chunkCoords,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16) },
            chunkCoord =>
            {
                var root = regions.ReadChunk(chunkCoord.X, chunkCoord.Z);
                if (root is not null)
                {
                    var chunkBlocks = CollectChunkBlocks(AnvilChunk.FromNbt(root),
                        originX + (chunkCoord.X << 4), originZ + (chunkCoord.Z << 4), yBase);
                    lock (mergeGate)
                    {
                        foreach (var (coordinate, value) in chunkBlocks.Blocks)
                        {
                            full.Set(coordinate, value);
                        }

                        vegetation.UnionWith(chunkBlocks.Vegetation);
                        logs.UnionWith(chunkBlocks.Logs);
                    }
                }

                var done = Interlocked.Increment(ref processed);
                if (done % 64 == 1)
                {
                    progress?.Invoke($"已解析 {done}/{chunkCoords.Count} 区块");
                }
            });

        return new McImportResult(full, SplitTrees(full, vegetation, logs));
    }

    /// <summary>
    /// 单个区块的本地解析结果，等待合并进世界
    /// </summary>
    private sealed record ChunkBlocks(
        List<(VoxelCoord64 Coordinate, VoxelValue Value)> Blocks,
        HashSet<VoxelCoord64> Vegetation,
        HashSet<VoxelCoord64> Logs);

    /// <summary>
    /// 一个方块的映射结果缓存：颜色值与植被判定，按方块名复用
    /// </summary>
    private readonly record struct BlockMapping(VoxelValue Value, bool Vegetation, bool Log);

    /// <summary>
    /// 解析一个区块内全部非空气方块，在本地收集后由调用方合并；
    /// 方块名到颜色与特性的映射按区块缓存，同一方块名只查一次色表
    /// </summary>
    private static ChunkBlocks CollectChunkBlocks(AnvilChunk chunk, long blockOriginX, long blockOriginZ, int yBase)
    {
        var blocks = new List<(VoxelCoord64, VoxelValue)>();
        var vegetation = new HashSet<VoxelCoord64>();
        var logs = new HashSet<VoxelCoord64>();
        var mappings = new Dictionary<string, BlockMapping>(StringComparer.Ordinal);
        foreach (var section in chunk.Sections)
        {
            if (section.Y < -16 || section.Y > 1023 || IsAirOnlySection(section))
            {
                continue;
            }

            for (var y = 0; y < 16; y++)
            {
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

                        var coordinate = new VoxelCoord64(
                            blockOriginX + x, section.BaseY + y - yBase, blockOriginZ + z);
                        blocks.Add((coordinate, mapping.Value));
                        if (mapping.Vegetation)
                        {
                            vegetation.Add(coordinate);
                            if (mapping.Log)
                            {
                                logs.Add(coordinate);
                            }
                        }
                    }
                }
            }
        }

        return new ChunkBlocks(blocks, vegetation, logs);
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
    /// 用六邻接连通域把植被拆成树：足够大的簇从地形中移出并成为独立分层
    /// </summary>
    private static List<McImportLayer> SplitTrees(VoxelWorld full,
        HashSet<VoxelCoord64> vegetation, HashSet<VoxelCoord64> logs)
    {
        var clusters = new List<List<VoxelCoord64>>();
        var pending = new HashSet<VoxelCoord64>(vegetation);
        while (pending.Count > 0)
        {
            VoxelCoord64 seed = default;
            foreach (var item in pending)
            {
                seed = item;
                break;
            }

            var cluster = new List<VoxelCoord64>();
            var queue = new Queue<VoxelCoord64>();
            queue.Enqueue(seed);
            pending.Remove(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cluster.Add(current);
                EnqueueNeighbor(current, 1, 0, 0, pending, queue);
                EnqueueNeighbor(current, -1, 0, 0, pending, queue);
                EnqueueNeighbor(current, 0, 1, 0, pending, queue);
                EnqueueNeighbor(current, 0, -1, 0, pending, queue);
                EnqueueNeighbor(current, 0, 0, 1, pending, queue);
                EnqueueNeighbor(current, 0, 0, -1, pending, queue);
            }

            if (cluster.Count >= MinimumTreeVoxels && cluster.Any(logs.Contains))
            {
                clusters.Add(cluster);
            }
        }

        var result = new List<McImportLayer>();
        foreach (var cluster in clusters.OrderByDescending(item => item.Count).Take(MaximumTrees))
        {
            var layer = new VoxelWorld();
            foreach (var coordinate in cluster)
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
    /// 把六邻接方向上的待访问植被坐标加入搜索队列
    /// </summary>
    private static void EnqueueNeighbor(VoxelCoord64 source, long dx, long dy, long dz,
        HashSet<VoxelCoord64> pending, Queue<VoxelCoord64> queue)
    {
        var next = new VoxelCoord64(source.X + dx, source.Y + dy, source.Z + dz);
        if (pending.Remove(next))
        {
            queue.Enqueue(next);
        }
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
