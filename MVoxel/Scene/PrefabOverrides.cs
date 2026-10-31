using System.Numerics;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 预制体实例覆盖检测与单项还原：实例场景数据仍是完整拷贝，
/// 覆盖在这里相对来源资产按链接表逐对象计算得出，
/// 用于检查器展示差异、单项还原与同步状态判断，不改变场景存储格式
/// </summary>
public static class PrefabOverrides
{
    /// <summary>
    /// 覆盖类型
    /// </summary>
    public enum Kind
    {
        /// <summary>对象显示名被修改</summary>
        Renamed,

        /// <summary>变换被修改</summary>
        Transform,

        /// <summary>图层归属被修改</summary>
        Layer,

        /// <summary>可见性被修改</summary>
        Visibility,

        /// <summary>锁定状态被修改</summary>
        Lock,

        /// <summary>组件被增删或改写</summary>
        Component,

        /// <summary>实例侧新增的对象（资产中无来源）</summary>
        Added,

        /// <summary>资产中存在但实例侧缺失的对象</summary>
        Missing,

        /// <summary>来源资产已被重新应用，实例尚未同步（整树级状态）</summary>
        SourceDrift,
    }

    /// <summary>
    /// 单条覆盖记录
    /// </summary>
    /// <param name="ObjectId">实例侧对象 ID</param>
    /// <param name="ObjectName">实例侧对象显示名</param>
    /// <param name="Kind">覆盖类型</param>
    /// <param name="Detail">人类可读的差异说明</param>
    public sealed record Override(StableId ObjectId, string ObjectName, Kind Kind, string Detail);

    /// <summary>
    /// 相对来源资产计算实例子树的全部覆盖；子树外的普通对象不参与
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="instanceRoot">实例根</param>
    /// <param name="asset">来源预制体资产</param>
    /// <returns>按对象分组的覆盖列表（根在前），无覆盖时为空</returns>
    public static IReadOnlyList<Override> Compute(SceneDocument document, SceneObject instanceRoot, PrefabAsset asset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(asset);
        var result = new List<Override>();
        var prefab = instanceRoot.Prefab;
        if (prefab is null)
        {
            return result;
        }

        if (prefab.SourceId != asset.PrefabId)
        {
            result.Add(new Override(instanceRoot.Id, instanceRoot.Name, Kind.SourceDrift,
                "来源资产已被重新应用或外部修改，实例尚未同步"));
        }

        var subtree = PrefabSystem.CollectSubtree(document, instanceRoot);
        var matchedSourceIds = new HashSet<StableId>();
        foreach (var instanceObject in subtree)
        {
            SceneObject? source = null;
            if (prefab.Links.TryGetValue(instanceObject.Id.Value.ToString(), out var linkValue) &&
                StableId.TryParse(linkValue, out var sourceId))
            {
                source = FindSource(asset, sourceId);
            }

            if (source is null)
            {
                result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Added,
                    instanceObject == instanceRoot ? "实例根缺少链接" : "实例侧新增的对象"));
                continue;
            }

            matchedSourceIds.Add(source.Id);
            CompareObject(instanceObject, source, result);
        }

        foreach (var source in asset.Objects)
        {
            if (!matchedSourceIds.Contains(source.Id))
            {
                result.Add(new Override(source.Id, source.Name, Kind.Missing,
                    "资产中存在但实例侧已被删除的对象"));
            }
        }

        return result;
    }

    /// <summary>
    /// 把单个实例对象还原为来源资产内容：恢复名称、变换、图层、可见性、锁定与全部组件，
    /// 保留对象 ID、父子关系与子树；无链接（新增对象）时移除其整个子树
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="instanceRoot">所属实例根</param>
    /// <param name="asset">来源预制体资产</param>
    /// <param name="objectId">要还原的实例侧对象 ID</param>
    /// <returns>是否完成还原</returns>
    public static bool RevertObject(SceneDocument document, SceneObject instanceRoot, PrefabAsset asset,
        StableId objectId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(asset);
        var prefab = instanceRoot.Prefab;
        if (prefab is null)
        {
            return false;
        }

        var instanceObject = document.Objects.FirstOrDefault(item => item.Id == objectId);
        if (instanceObject is null)
        {
            return false;
        }

        if (!prefab.Links.TryGetValue(objectId.Value.ToString(), out var linkValue) ||
            !StableId.TryParse(linkValue, out var sourceId) ||
            FindSource(asset, sourceId) is not { } source)
        {
            // 无来源的新增对象：整体移除子树
            if (instanceObject.Id == instanceRoot.Id)
            {
                return false; // 实例根不允许通过单项还原移除
            }

            PrefabSystem.RemoveSubtree(document, instanceObject, keepRoot: false);
            return true;
        }

        instanceObject.Name = source.Name;
        instanceObject.Transform = new TransformComponent(source.Transform.Mode, source.Transform.Grid,
            source.Transform.Free) { ScaleX = source.Transform.ScaleX, ScaleY = source.Transform.ScaleY, ScaleZ = source.Transform.ScaleZ };
        instanceObject.LayerIndex = source.LayerIndex;
        instanceObject.Visible = source.Visible;
        instanceObject.Locked = source.Locked;
        instanceObject.Components = source.Components
            .Select(item => new ComponentRecord
            {
                Enabled = item.Enabled,
                OwnerPluginId = item.OwnerPluginId,
                TypeId = item.TypeId,
                SchemaVersion = item.SchemaVersion,
                Data = item.Data.Clone(),
            })
            .ToList();
        return true;
    }

    /// <summary>
    /// 比较单个实例对象与其来源，把差异加入覆盖列表
    /// </summary>
    private static void CompareObject(SceneObject instanceObject, SceneObject source, List<Override> result)
    {
        if (!string.Equals(instanceObject.Name, source.Name, StringComparison.Ordinal))
        {
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Renamed,
                $"{source.Name} → {instanceObject.Name}"));
        }

        if (!TransformEquals(instanceObject.Transform, source.Transform))
        {
            var target = instanceObject.Transform.Mode == TransformMode.Grid
                ? instanceObject.Transform.Grid.Position.ToString()
                : instanceObject.Transform.Free.Position.ToString();
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Transform,
                $"当前位置 {target}"));
        }

        if (instanceObject.LayerIndex != source.LayerIndex)
        {
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Layer,
                $"图层 {source.LayerIndex} → {instanceObject.LayerIndex}"));
        }

        if (instanceObject.Visible != source.Visible)
        {
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Visibility,
                source.Visible ? "已隐藏" : "已设为可见"));
        }

        if (instanceObject.Locked != source.Locked)
        {
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Lock,
                source.Locked ? "已解锁" : "已锁定"));
        }

        if (!ComponentsEquals(instanceObject.Components, source.Components))
        {
            var changed = instanceObject.Components
                .Where(item => FindComponent(source.Components, item.TypeId) is not { } other ||
                    !JsonElement.DeepEquals(item.Data, other.Data) || item.Enabled != other.Enabled)
                .Select(item => item.TypeId)
                .Concat(source.Components
                    .Where(item => FindComponent(instanceObject.Components, item.TypeId) is null)
                    .Select(item => item.TypeId))
                .Distinct()
                .Take(3);
            result.Add(new Override(instanceObject.Id, instanceObject.Name, Kind.Component,
                $"组件差异：{string.Join("、", changed)}"));
        }
    }

    /// <summary>
    /// 按类型 ID 查找组件
    /// </summary>
    private static ComponentRecord? FindComponent(List<ComponentRecord> components, string typeId) =>
        components.FirstOrDefault(item => item.TypeId == typeId);

    /// <summary>
    /// 比较两套变换：格变换要求完全一致，自由变换用浮点容差
    /// </summary>
    private static bool TransformEquals(TransformComponent left, TransformComponent right)
    {
        if (left.ScaleX != right.ScaleX || left.ScaleY != right.ScaleY || left.ScaleZ != right.ScaleZ) return false;
        if (left.Mode != right.Mode)
        {
            return false;
        }

        if (left.Grid != right.Grid)
        {
            return false;
        }

        const float epsilon = 1e-5F;
        var positionDelta = new Vector3(
            (float)(left.Free.Position.X - right.Free.Position.X),
            (float)(left.Free.Position.Y - right.Free.Position.Y),
            (float)(left.Free.Position.Z - right.Free.Position.Z));
        return positionDelta.LengthSquared() <= epsilon * epsilon &&
            Quaternion.Dot(left.Free.Rotation, right.Free.Rotation) >= 1F - epsilon;
    }

    /// <summary>
    /// 比较组件集合：类型集合与各组件数据逐项一致
    /// </summary>
    private static bool ComponentsEquals(List<ComponentRecord> left, List<ComponentRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var item in left)
        {
            var other = right.FirstOrDefault(candidate => candidate.TypeId == item.TypeId);
            if (other is null || !item.Enabled.Equals(other.Enabled) || item.SchemaVersion != other.SchemaVersion ||
                !JsonElement.DeepEquals(item.Data, other.Data))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 在资产对象表中查找来源对象
    /// </summary>
    private static SceneObject? FindSource(PrefabAsset asset, StableId id) =>
        asset.Objects.FirstOrDefault(item => item.Id == id);
}
