using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 渲染工作集共用的体素可见性规则。
/// 源世界始终完整保留；这里只决定哪些体素需要上传为可渲染图元。
/// </summary>
internal static class VoxelRenderVisibility
{
    /// <summary>
    /// 判断一个体素是否可以从渲染工作集中安全移除。
    /// 仅移除被六个不透明邻居完全包围、且自身不发光的体素：外部相机、阴影和反射射线
    /// 都不可能首次命中它。半透明和发光体素永远保留，避免改变光学与光源语义。
    /// </summary>
    /// <param name="world">完整源世界</param>
    /// <param name="coordinate">待判断体素的世界坐标</param>
    /// <param name="value">待判断体素值</param>
    /// <param name="preserveUniformSolid">同值实体会由贪心盒体压缩时为 true，保留其核心以形成最少盒体</param>
    /// <returns>可从渲染工作集中移除时返回 true</returns>
    internal static bool CanCullInterior(VoxelWorld world, VoxelCoord64 coordinate, VoxelValue value,
        bool preserveUniformSolid)
    {
        if (!IsOpaqueOccluder(value) || VoxelFlags.Has(value.Flags, VoxelFlags.Emissive)) return false;

        var allNeighborsMatch = true;
        foreach (var offset in NeighborOffsets)
        {
            var neighborCoordinate = new VoxelCoord64(
                coordinate.X + offset.X, coordinate.Y + offset.Y, coordinate.Z + offset.Z);
            if (!world.TryGet(neighborCoordinate, out var neighbor) || !IsOpaqueOccluder(neighbor)) return false;
            allNeighborsMatch &= neighbor == value;
        }

        // 对场景逐格预览，任何全封闭不透明体素都没有可见面；对渲染/导出贪心盒路径，
        // 纯同值实体保留核心能够折成更少的实心盒，已经等价消除了内部面。
        return !preserveUniformSolid || !allNeighborsMatch;
    }

    private static bool IsOpaqueOccluder(VoxelValue value) =>
        !value.IsEmpty && value.Color.A == byte.MaxValue && !VoxelFlags.Has(value.Flags, VoxelFlags.Translucent);

    private static readonly (long X, long Y, long Z)[] NeighborOffsets =
    [
        (-1, 0, 0), (1, 0, 0),
        (0, -1, 0), (0, 1, 0),
        (0, 0, -1), (0, 0, 1),
    ];
}
