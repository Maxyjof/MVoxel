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
    /// <returns>可直接上传到 GPU 的确定性快照</returns>
    /// <exception cref="ArgumentNullException">世界为空时抛出</exception>
    /// <exception cref="InvalidDataException">包围盒尺寸超出 GPU 局部坐标范围时抛出</exception>
    public static VoxelGpuWorkset Build(VoxelWorld world)
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
        var cells = new List<VoxelGpuCell>(world.OccupiedVoxelCount);
        var emissiveCount = 0;
        foreach (var (coordinate, value) in world.EnumerateOccupied())
        {
            var cell = new VoxelGpuCell(
                checked((int)(coordinate.X - minimum.X)),
                checked((int)(coordinate.Y - minimum.Y)),
                checked((int)(coordinate.Z - minimum.Z)),
                value);
            cells.Add(cell);
            if (VoxelFlags.Has(value.Flags, VoxelFlags.Emissive)) emissiveCount++;
        }

        cells.Sort(static (left, right) =>
        {
            var result = left.Z.CompareTo(right.Z);
            if (result != 0) return result;
            result = left.Y.CompareTo(right.Y);
            return result != 0 ? result : left.X.CompareTo(right.X);
        });
        return new VoxelGpuWorkset(world.ContentVersion, minimum, size, cells.ToArray(), emissiveCount);
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
