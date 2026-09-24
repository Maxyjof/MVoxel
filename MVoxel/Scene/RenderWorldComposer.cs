using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 渲染世界组成器：把合并态工作世界按相机图层遮罩与对象可见性过滤成渲染世界。
/// 主模型对象的体素是合并态中不属于任何分层对象的部分，按主对象的可见性与图层参与；
/// 每个分层对象按自身可见性与图层独立剔除
/// </summary>
public static class RenderWorldComposer
{
    /// <summary>
    /// 执行组成
    /// </summary>
    /// <param name="merged">编辑器合并态工作世界</param>
    /// <param name="primaryId">主模型对象 ID</param>
    /// <param name="objects">场景全部对象</param>
    /// <param name="ownership">分层对象的体素所有权表</param>
    /// <param name="cullingMask">相机图层遮罩，位 i 为 1 表示渲染第 i 层</param>
    /// <returns>只含允许图层且可见内容的新世界</returns>
    public static VoxelWorld Compose(VoxelWorld merged, StableId primaryId,
        IReadOnlyList<SceneObject> objects, IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> ownership,
        uint cullingMask, IReadOnlyList<string>? layerNames = null, IReadOnlyCollection<string>? hiddenLayers = null)
    {
        var result = new VoxelWorld();
        var objectsById = objects.ToDictionary(item => item.Id);
        var ownedUnion = new HashSet<VoxelCoord64>();
        foreach (var owned in ownership.Values)
        {
            ownedUnion.UnionWith(owned);
        }

        var primary = objects.FirstOrDefault(item => item.Id == primaryId);
        if (primary is null || (IsObjectVisible(primary, objectsById) && LayerAllowed(cullingMask, primary.LayerIndex) &&
                                IsLayerVisible(primary.LayerIndex, layerNames, hiddenLayers)))
        {
            foreach (var item in merged.EnumerateOccupied())
            {
                if (!ownedUnion.Contains(item.Coordinate))
                {
                    result.Set(item.Coordinate, item.Value);
                }
            }
        }

        foreach (var sceneObject in objects)
        {
            if (sceneObject.Id == primaryId || !IsObjectVisible(sceneObject, objectsById) ||
                !LayerAllowed(cullingMask, sceneObject.LayerIndex) ||
                !IsLayerVisible(sceneObject.LayerIndex, layerNames, hiddenLayers) ||
                !ownership.TryGetValue(sceneObject.Id, out var owned))
            {
                continue;
            }

            foreach (var coordinate in owned)
            {
                if (merged.TryGet(coordinate, out var value))
                {
                    result.Set(coordinate, value);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 判断遮罩是否允许某图层
    /// </summary>
    /// <param name="mask">图层遮罩</param>
    /// <param name="layerIndex">图层索引</param>
    /// <returns>是否允许渲染</returns>
    public static bool LayerAllowed(uint mask, int layerIndex) =>
        layerIndex is >= 0 and < 32 && (mask & (1u << layerIndex)) != 0;

    private static bool IsLayerVisible(int index, IReadOnlyList<string>? names, IReadOnlyCollection<string>? hidden) =>
        names is null || index < 0 || index >= names.Count || hidden is null || !hidden.Contains(names[index]);

    private static bool IsObjectVisible(SceneObject sceneObject, IReadOnlyDictionary<StableId, SceneObject> objects)
    {
        var visited = new HashSet<StableId>();
        for (SceneObject? current = sceneObject; current is not null;)
        {
            if (!current.Visible || !visited.Add(current.Id)) return false;
            current = current.ParentId is { } parent && objects.TryGetValue(parent, out var found) ? found : null;
        }
        return true;
    }
}
