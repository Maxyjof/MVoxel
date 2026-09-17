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
        var totalChunks = widthX * widthZ;
        var processed = 0;
        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                processed++;
                if (processed % 16 == 1)
                {
                    progress?.Invoke($"已解析 {processed}/{totalChunks} 区块");
                }

                var root = regions.ReadChunk(chunkX, chunkZ);
                if (root is null)
                {
                    continue;
                }

                CollectChunkBlocks(AnvilChunk.FromNbt(root),
                    originX + (chunkX << 4), originZ + (chunkZ << 4), yBase, full, vegetation, logs);
            }
        }

        return new McImportResult(full, SplitTrees(full, vegetation, logs));
    }

    /// <summary>
    /// 把一个区块内全部非空气方块写入世界，并登记植被坐标
    /// </summary>
    private static void CollectChunkBlocks(AnvilChunk chunk, long blockOriginX, long blockOriginZ, int yBase,
        VoxelWorld world, HashSet<VoxelCoord64> vegetation, HashSet<VoxelCoord64> logs)
    {
        foreach (var section in chunk.Sections)
        {
            if (IsAirOnlySection(section))
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

                        var worldY = section.BaseY + y - yBase;
                        if (worldY < 0)
                        {
                            continue;
                        }

                        var coordinate = new VoxelCoord64(blockOriginX + x, worldY, blockOriginZ + z);
                        var flags = VoxelFlags.None;
                        if (MinecraftBlockColors.IsTranslucent(name)) flags |= VoxelFlags.Translucent;
                        if (MinecraftBlockColors.IsEmissive(name)) flags |= VoxelFlags.Emissive;
                        world.Set(coordinate, new VoxelValue(MinecraftBlockColors.ColorOf(name), 0, flags));
                        if (IsVegetationName(name))
                        {
                            vegetation.Add(coordinate);
                            if (name.Contains("_log", StringComparison.Ordinal))
                            {
                                logs.Add(coordinate);
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
    /// region 文件缓存：按需打开所属 r.X.Z.mca，供区块读取复用
    /// </summary>
    private sealed class RegionCache : IDisposable
    {
        private readonly string _regionDirectory;
        private readonly Dictionary<(int X, int Z), AnvilRegionFile> _regions = new();

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
                var path = Path.Combine(_regionDirectory, $"r.{regionX}.{regionZ}.mca");
                if (!File.Exists(path))
                {
                    return null;
                }

                region = AnvilRegionFile.Open(path);
                _regions.Add((regionX, regionZ), region);
            }

            return region.ReadChunk((int)(chunkX - regionX * 32), (int)(chunkZ - regionZ * 32));
        }

        /// <summary>
        /// 释放全部已打开的 region 文件
        /// </summary>
        public void Dispose()
        {
            foreach (var region in _regions.Values)
            {
                region.Dispose();
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
