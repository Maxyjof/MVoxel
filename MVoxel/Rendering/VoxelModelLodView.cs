using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 模型级 LOD 视图：把每个区块映射到它所属模型在本次相机下的 LOD 级。
/// 与按区块距离分档不同，同一模型的所有区块使用同一级，距离由模型包围盒到相机的距离决定；
/// 视图直接查对象级结果，不额外复制整张区块表。
/// </summary>
public sealed class VoxelModelLodView
{
    private readonly Func<ChunkCoord64, int> _levelFor;
    private readonly Func<ChunkCoord64, int, IReadOnlyList<VoxelBakedCell>?>? _bakedCells;

    /// <summary>
    /// 创建模型级 LOD 视图
    /// </summary>
    /// <param name="levelFor">区块到 LOD 级的查询，0 为完整细节</param>
    /// <param name="bakedCells">区块在指定级的预烘焙图元，没有时返回 null</param>
    /// <param name="signature">相机、模型级与预烘焙状态的组合指纹，用于跳过重复同步</param>
    public VoxelModelLodView(Func<ChunkCoord64, int> levelFor,
        Func<ChunkCoord64, int, IReadOnlyList<VoxelBakedCell>?>? bakedCells, long signature)
    {
        _levelFor = levelFor ?? throw new ArgumentNullException(nameof(levelFor));
        _bakedCells = bakedCells;
        Signature = signature;
    }

    /// <summary>
    /// 相机与 LOD 配置的组合指纹；用于工作集缓存判等
    /// </summary>
    public long Signature { get; }

    /// <summary>
    /// 取区块的 LOD 级，未登记的区块按完整细节处理
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    public int LevelFor(ChunkCoord64 coordinate) => _levelFor(coordinate);

    /// <summary>
    /// 取区块的预烘焙图元；没有预烘焙数据时返回 null，调用方回退运行时粗化
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    /// <param name="level">LOD 级</param>
    public IReadOnlyList<VoxelBakedCell>? BakedCells(ChunkCoord64 coordinate, int level) =>
        _bakedCells?.Invoke(coordinate, level);
}
