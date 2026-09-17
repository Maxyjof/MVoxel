using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// GPU 工作集中的单个体素
/// </summary>
/// <param name="X">相对于工作集原点的 X 坐标</param>
/// <param name="Y">相对于工作集原点的 Y 坐标</param>
/// <param name="Z">相对于工作集原点的 Z 坐标</param>
/// <param name="Value">体素材质与颜色</param>
public readonly record struct VoxelGpuCell(int X, int Y, int Z, VoxelValue Value);

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
    /// <returns>可直接上传到 GPU 的确定性快照</returns>
    /// <exception cref="ArgumentNullException">世界为空时抛出</exception>
    /// <exception cref="InvalidDataException">包围盒尺寸超出 GPU 局部坐标范围时抛出</exception>
    public static VoxelGpuWorkset Build(VoxelWorld world, int maximumCells = 0)
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
        // 因此只需区块内的小排序与前缀和定位，不再对全体体素做全局排序
        var chunks = world.SnapshotChunks();
        chunks.Sort(static (left, right) =>
        {
            var byZ = left.Coordinate.Z.CompareTo(right.Coordinate.Z);
            if (byZ != 0) return byZ;
            var byY = left.Coordinate.Y.CompareTo(right.Coordinate.Y);
            return byY != 0 ? byY : left.Coordinate.X.CompareTo(right.Coordinate.X);
        });

        var chunkCount = chunks.Count;
        var starts = new long[chunkCount + 1];
        for (var index = 0; index < chunkCount; index++)
        {
            starts[index + 1] = starts[index] + chunks[index].Chunk.OccupiedCount;
        }

        var occupiedCount = checked((int)starts[chunkCount]);
        var sampleStride = maximumCells > 0 && occupiedCount > maximumCells
            ? (occupiedCount + maximumCells - 1) / maximumCells
            : 1;
        var cellCount = sampleStride == 1
            ? occupiedCount
            : CountSampled(starts, chunkCount, sampleStride);
        var cells = new VoxelGpuCell[cellCount];
        var writeStarts = new int[chunkCount + 1];
        if (sampleStride == 1)
        {
            for (var index = 0; index <= chunkCount; index++)
            {
                writeStarts[index] = (int)starts[index];
            }
        }
        else
        {
            for (var index = 0; index < chunkCount; index++)
            {
                writeStarts[index + 1] = writeStarts[index] +
                    CountSampledInChunk(starts[index], starts[index + 1], sampleStride);
            }
        }

        var emissiveTotals = new int[chunkCount];
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
                var emissive = 0;
                var write = writeStarts[chunkIndex];
                for (var localIndex = 0; localIndex < pairs.Length; localIndex++)
                {
                    if (sampleStride != 1)
                    {
                        var global = starts[chunkIndex] + localIndex;
                        if (global % sampleStride != 0)
                        {
                            continue;
                        }
                    }

                    var (key, value) = pairs[localIndex];
                    var local = SparseVoxelChunk.FromIndex(key);
                    var cell = new VoxelGpuCell(
                        (int)(coordinate.X * VoxelWorld.ChunkSize + local.X - minimum.X),
                        (int)(coordinate.Y * VoxelWorld.ChunkSize + local.Y - minimum.Y),
                        (int)(coordinate.Z * VoxelWorld.ChunkSize + local.Z - minimum.Z),
                        value);
                    cells[write++] = cell;
                    if (VoxelFlags.Has(value.Flags, VoxelFlags.Emissive)) emissive++;
                }

                emissiveTotals[chunkIndex] = emissive;
            });

        var emissiveCount = 0;
        foreach (var emissive in emissiveTotals) emissiveCount += emissive;
        return new VoxelGpuWorkset(world.ContentVersion, minimum, size, cells, emissiveCount);
    }

    /// <summary>
    /// 统计全局编号落在采样点上的体素总数
    /// </summary>
    private static int CountSampled(long[] starts, int chunkCount, int sampleStride)
    {
        var total = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            total += CountSampledInChunk(starts[index], starts[index + 1], sampleStride);
        }

        return total;
    }

    /// <summary>
    /// 统计全局编号区间 [from, to) 内命中采样步数的条数
    /// </summary>
    private static int CountSampledInChunk(long from, long to, int sampleStride)
    {
        var firstHit = from + ((sampleStride - from % sampleStride) % sampleStride);
        return firstHit >= to ? 0 : (int)((to - 1 - firstHit) / sampleStride) + 1;
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
