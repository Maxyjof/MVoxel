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
        VoxelLodSettings? lod = null, System.Numerics.Vector3? cameraPosition = null, bool greedyMerge = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            return world.ContentVersion == 0
                ? Empty
                : new VoxelGpuWorkset(world.ContentVersion, default, default, [], 0);
        }

        var size = new VoxelCoord64(
            CheckedExtent(minimum.X, maximum.X, nameof(minimum)),
            CheckedExtent(minimum.Y, maximum.Y, nameof(minimum)),
            CheckedExtent(minimum.Z, maximum.Z, nameof(minimum)));

        // 区块按 (Z, Y, X) 的列顺序排好后，每个区块内部按键升序遍历就是全局 Z、Y、X 稳定序，
        // 因此只需区块内的小排序，不再对全体体素做全局排序
        var chunks = world.SnapshotChunks();
        chunks.Sort(static (left, right) =>
        {
            var byZ = left.Coordinate.Z.CompareTo(right.Coordinate.Z);
            if (byZ != 0) return byZ;
            var byY = left.Coordinate.Y.CompareTo(right.Coordinate.Y);
            return byY != 0 ? byY : left.Coordinate.X.CompareTo(right.Coordinate.X);
        });

        var chunkCount = chunks.Count;
        var lodActive = lod is { Enabled: true } && cameraPosition.HasValue;

        // 预算采样必须作用于原始体素而不是合并后的盒：丢一个采样点只是散洞，
        // 丢一个合并盒则是整块大洞。先按原始全局序号采样、再对采样子集合并，
        // 保持与合并前完全一致的稀疏语义；LOD 激活时由其自身控制规模，预算让位
        var rawStarts = new long[chunkCount + 1];
        for (var index = 0; index < chunkCount; index++)
        {
            rawStarts[index + 1] = rawStarts[index] + chunks[index].Chunk.OccupiedCount;
        }

        var rawTotal = (int)rawStarts[chunkCount];
        var sampleStride = !lodActive && maximumCells > 0 && rawTotal > maximumCells
            ? (rawTotal + maximumCells - 1) / maximumCells
            : 1;

        var perChunk = new List<VoxelGpuCell>[chunkCount];
        System.Threading.Tasks.Parallel.For(0, chunkCount,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
            },
            chunkIndex =>
            {
                var (coordinate, chunk) = chunks[chunkIndex];
                var pairs = new (int Key, VoxelValue Value)[chunk.OccupiedCount];
                var pairIndex = 0;
                foreach (var pair in chunk.Snapshot)
                {
                    pairs[pairIndex++] = (pair.Key, pair.Value);
                }

                Array.Sort(pairs, static (a, b) => a.Key.CompareTo(b.Key));
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

                perChunk[chunkIndex] = list;
            });

        var total = 0;
        var emissiveCount = 0;
        foreach (var list in perChunk)
        {
            total += list.Count;
            foreach (var cell in list)
            {
                if (VoxelFlags.Has(cell.Value.Flags, VoxelFlags.Emissive)) emissiveCount++;
            }
        }

        var cells = new VoxelGpuCell[total];
        var write = 0;
        foreach (var list in perChunk)
        {
            foreach (var cell in list) cells[write++] = cell;
        }

        return new VoxelGpuWorkset(world.ContentVersion, minimum, size, cells, emissiveCount);
    }

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
