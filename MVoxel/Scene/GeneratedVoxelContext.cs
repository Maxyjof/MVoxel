using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 程序化组件生成时能看到的外部世界：只读的其他对象体素
/// </summary>
/// <remarks>
/// 瓷砖与沿曲线铺设需要知道场地里已有的几何，才能“贴着墙”生成。
/// 这里只保存模型组件的真实体素，不包含其他程序化组件的输出：
/// 这样组件之间的生成顺序不会互相影响，也不会出现互相重算的死循环
/// </remarks>
public sealed class SceneSurfaceSampler
{
    private readonly (StableId Id, VoxelWorld World)[] _worlds;

    /// <summary>
    /// 由若干对象的体素世界构建采样器
    /// </summary>
    /// <param name="worlds">对象标识与世界，顺序即查询优先级</param>
    public SceneSurfaceSampler(IReadOnlyList<(StableId Id, VoxelWorld World)> worlds)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        _worlds = worlds.Where(item => item.World is not null && item.World.OccupiedVoxelCount > 0).ToArray();
    }

    /// <summary>采样源数量，界面上用来判断是否真的读到了场景</summary>
    public int WorldCount => _worlds.Length;

    /// <summary>
    /// 查询某个世界坐标是否有实体，重叠时取优先级靠前的对象
    /// </summary>
    /// <param name="coordinate">世界坐标</param>
    /// <param name="value">查到的体素值</param>
    /// <returns>是否存在实体</returns>
    public bool TryGet(VoxelCoord64 coordinate, out VoxelValue value)
    {
        foreach (var (_, world) in _worlds)
        {
            if (world.TryGet(coordinate, out value)) return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 沿轴方向逐格探测第一个实体
    /// </summary>
    /// <param name="start">起点格</param>
    /// <param name="direction">轴方向，必须是六个轴向之一</param>
    /// <param name="maxSteps">最多前进的格数，包含起点</param>
    /// <param name="hit">命中的实体格</param>
    /// <returns>是否命中</returns>
    public bool TryHit(VoxelCoord64 start, VoxelCoord64 direction, int maxSteps, out VoxelCoord64 hit)
    {
        if (maxSteps <= 0)
        {
            hit = start;
            return false;
        }

        var current = start;
        for (var step = 0; step < maxSteps; step++)
        {
            if (TryGet(current, out _))
            {
                hit = current;
                return true;
            }

            current += direction;
        }

        hit = current;
        return false;
    }
}

/// <summary>
/// 程序化组件的生成上下文：对象原点、场景采样器与体素源解析入口
/// </summary>
/// <remarks>
/// 树生成器与曲线管体不需要上下文，传 null 时行为与之前完全一致。
/// 需要读取场景的组件（瓷砖、沿曲线铺设）通过它拿到只读世界与资产解析能力
/// </remarks>
public sealed class GeneratedVoxelContext
{
    /// <summary>当前对象在场景中的格原点，生成结果据此换算回局部坐标</summary>
    public VoxelCoord64 ObjectOrigin { get; init; }

    /// <summary>对象局部空间绕对象原点的旋转</summary>
    public Quaternion ObjectRotation { get; init; } = Quaternion.Identity;

    /// <summary>对象局部空间的三轴缩放</summary>
    public Vector3 ObjectScale { get; init; } = Vector3.One;

    /// <summary>其他对象真实几何的只读采样器，没有场景时为空</summary>
    public SceneSurfaceSampler? Surface { get; init; }

    /// <summary>体素源解析入口，按资产引用读取并缓存</summary>
    public Func<VoxelSourceRef, VoxelSource>? ResolveSource { get; init; }
}
