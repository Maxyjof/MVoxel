using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 预制体系统的纯数据操作：创建资产、实例化、还原、应用与实例收集。
/// 实例根携带链接表覆盖整棵子树，嵌套实例在克隆时重映射链接键
/// </summary>
public static class PrefabSystem
{
    /// <summary>
    /// 收集对象及其全部后代，父对象在前，可直接作为预制体资产快照
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="root">子树根</param>
    /// <returns>父先子后的对象列表</returns>
    public static IReadOnlyList<SceneObject> CollectSubtree(SceneDocument document, SceneObject root)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        var result = new List<SceneObject>();
        var queue = new Queue<SceneObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);
            foreach (var child in document.Objects)
            {
                if (child.ParentId == current.Id)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 把场景对象转为预制体实例：要求传入的资产已包含该子树快照
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="root">要转换的子树根</param>
    /// <param name="assetPath">资产相对路径，形如 Assets/Prefabs/名称.prefab</param>
    /// <param name="asset">包含该子树快照的资产</param>
    /// <returns>标记后的实例根</returns>
    public static SceneObject MakeInstanceFromScene(SceneDocument document, SceneObject root,
        string assetPath, PrefabAsset asset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(asset);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in CollectSubtree(document, root))
        {
            links[item.Id.Value.ToString()] = item.Id.Value.ToString();
        }

        root.Prefab = new PrefabInstanceData
        {
            AssetPath = NormalizePath(assetPath),
            SourceId = asset.PrefabId,
            Links = links,
        };
        return root;
    }

    /// <summary>
    /// 把场景子树快照成新的预制体资产：根解除父级、清除根上的实例标记，嵌套实例标记保留
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="root">子树根</param>
    /// <param name="name">资产显示名</param>
    /// <returns>待保存的资产</returns>
    public static PrefabAsset CreateAssetFromScene(SceneDocument document, SceneObject root, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        return new PrefabAsset
        {
            Name = name,
            PrefabId = root.Id,
            Objects = SnapshotSubtree(document, root),
        };
    }

    /// <summary>
    /// 把预制体资产克隆进场景，生成全新 ID 的实例
    /// </summary>
    /// <param name="asset">预制体资产</param>
    /// <param name="assetPath">资产相对路径，写入实例标记</param>
    /// <param name="document">目标场景文档</param>
    /// <param name="parentId">实例根的父对象，null 表示场景根</param>
    /// <returns>新实例的根对象</returns>
    public static SceneObject Instantiate(PrefabAsset asset, string assetPath, SceneDocument document,
        StableId? parentId = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(document);
        return CloneInto(asset, assetPath, document, parentId);
    }

    /// <summary>
    /// 用资产内容替换实例子树，实例根 ID 与父级保持不变
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="instanceRoot">实例根</param>
    /// <param name="asset">来源资产</param>
    /// <param name="preserveRootTransform">true 时保留实例根当前的变换与名称，用于更新；false 表示完全还原</param>
    /// <returns>是否成功刷新</returns>
    public static bool RefreshInstance(SceneDocument document, SceneObject instanceRoot,
        PrefabAsset asset, bool preserveRootTransform)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(asset);
        if (instanceRoot.Prefab is null)
        {
            return false;
        }

        var keepTransform = preserveRootTransform ? instanceRoot.Transform : null;
        var keepName = preserveRootTransform ? instanceRoot.Name : null;
        // 整树移除后凭 keepRootId 重建根，避免同 ID 对象重复出现在文档里
        RemoveSubtree(document, instanceRoot, keepRoot: false);
        CloneInto(asset, instanceRoot.Prefab.AssetPath, document, instanceRoot.ParentId,
            keepRootId: instanceRoot.Id, overrideRootTransform: keepTransform, overrideRootName: keepName);
        return true;
    }

    /// <summary>
    /// 把实例子树快照成新的资产内容，来源 ID 就是实例当前的对象 ID
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="instanceRoot">实例根</param>
    /// <param name="name">资产显示名</param>
    /// <returns>待保存的资产</returns>
    public static PrefabAsset ApplyInstanceToAsset(SceneDocument document, SceneObject instanceRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        var snapshot = SnapshotSubtree(document, instanceRoot);
        return new PrefabAsset
        {
            Name = name,
            PrefabId = instanceRoot.Id,
            Objects = snapshot,
        };
    }

    /// <summary>
    /// 收集场景中引用指定资产的实例根，只返回最顶层实例，嵌套实例随外层更新
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="assetPath">资产相对路径</param>
    /// <returns>实例根列表</returns>
    public static IReadOnlyList<SceneObject> CollectInstanceRoots(SceneDocument document, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = NormalizePath(assetPath);
        var byId = document.Objects.ToDictionary(item => item.Id);
        var result = new List<SceneObject>();
        foreach (var item in document.Objects)
        {
            if (item.Prefab is null || !string.Equals(item.Prefab.AssetPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isNested = false;
            var parent = item.ParentId is not null && byId.TryGetValue(item.ParentId.Value, out var found)
                ? found
                : null;
            while (parent is not null)
            {
                if (parent.Prefab is not null)
                {
                    isNested = true;
                    break;
                }

                parent = parent.ParentId is not null && byId.TryGetValue(parent.ParentId.Value, out var upper)
                    ? upper
                    : null;
            }

            if (!isNested)
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// 从任意子树对象向上找到所属的实例根
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="item">子树内任意对象</param>
    /// <returns>实例根；普通对象返回 null</returns>
    public static SceneObject? FindInstanceRoot(SceneDocument document, SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = item;
        while (current is not null)
        {
            if (current.Prefab is not null)
            {
                return current;
            }

            current = current.ParentId is not null
                ? document.Objects.FirstOrDefault(candidate => candidate.Id == current.ParentId)
                : null;
        }

        return null;
    }

    /// <summary>
    /// 把资产克隆进场景文档：全新 ID、重挂父级、根挂实例标记并重映射嵌套实例的链接键
    /// </summary>
    private static SceneObject CloneInto(PrefabAsset asset, string assetPath, SceneDocument document,
        StableId? parentId, StableId? keepRootId = null,
        TransformComponent? overrideRootTransform = null, string? overrideRootName = null)
    {
        var map = new Dictionary<StableId, SceneObject>();
        SceneObject? newRoot = null;
        foreach (var source in asset.Objects)
        {
            var isRoot = source.Id == asset.PrefabId;
            var clone = new SceneObject
            {
                Id = isRoot && keepRootId is not null ? keepRootId.Value : StableId.New(),
                Name = source.Name,
                Visible = source.Visible,
                Locked = source.Locked,
                Transform = CloneTransform(source.Transform),
            };
            foreach (var component in source.Components)
            {
                clone.Components.Add(new ComponentRecord
                {
                    TypeId = component.TypeId,
                    SchemaVersion = component.SchemaVersion,
                    Data = component.Data.ValueKind == JsonValueKind.Undefined ? component.Data : component.Data.Clone(),
                    OwnerPluginId = component.OwnerPluginId,
                });
            }

            if (isRoot)
            {
                if (overrideRootTransform is not null) clone.Transform = overrideRootTransform;
                if (overrideRootName is not null) clone.Name = overrideRootName;
                clone.Prefab = new PrefabInstanceData
                {
                    AssetPath = NormalizePath(assetPath),
                    SourceId = source.Id,
                    Links = new Dictionary<string, string> { [clone.Id.Value.ToString()] = source.Id.Value.ToString() },
                };
                newRoot = clone;
            }
            else if (source.Prefab is not null)
            {
                clone.Prefab = new PrefabInstanceData
                {
                    AssetPath = NormalizePath(source.Prefab.AssetPath),
                    SourceId = source.Prefab.SourceId,
                    Links = new Dictionary<string, string>(source.Prefab.Links, StringComparer.Ordinal),
                };
            }

            map[source.Id] = clone;
            document.Objects.Add(clone);
        }

        foreach (var source in asset.Objects)
        {
            var clone = map[source.Id];
            clone.ParentId = source.Id == asset.PrefabId
                ? parentId
                : source.ParentId is not null && map.TryGetValue(source.ParentId.Value, out var parentClone)
                    ? parentClone.Id
                    : parentId;
            if (clone.Prefab is not null)
            {
                var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (instanceKey, sourceKey) in clone.Prefab.Links)
                {
                    var newKey = Guid.TryParse(instanceKey, out var guid) &&
                                 map.TryGetValue(new StableId(guid), out var mapped)
                        ? mapped.Id.Value.ToString()
                        : instanceKey;
                    remapped[newKey] = sourceKey;
                }

                clone.Prefab.Links = remapped;
            }
        }

        return newRoot ?? throw new InvalidDataException("预制体资产缺少根对象");
    }

    /// <summary>
    /// 复制对象子树为资产快照：资产根解除父级并清除根上的实例标记，嵌套实例标记保留
    /// </summary>
    private static List<SceneObject> SnapshotSubtree(SceneDocument document, SceneObject root)
    {
        var snapshot = new List<SceneObject>();
        foreach (var source in CollectSubtree(document, root))
        {
            var copy = new SceneObject
            {
                Id = source.Id,
                Name = source.Name,
                Visible = source.Visible,
                Locked = source.Locked,
                Transform = CloneTransform(source.Transform),
                ParentId = source.Id == root.Id ? null : source.ParentId,
                Prefab = source.Id == root.Id ? null : source.Prefab,
            };
            foreach (var component in source.Components)
            {
                copy.Components.Add(new ComponentRecord
                {
                    TypeId = component.TypeId,
                    SchemaVersion = component.SchemaVersion,
                    Data = component.Data.ValueKind == JsonValueKind.Undefined ? component.Data : component.Data.Clone(),
                    OwnerPluginId = component.OwnerPluginId,
                });
            }

            snapshot.Add(copy);
        }

        return snapshot;
    }

    /// <summary>
    /// 从场景文档移除子树对象，可选择保留根
    /// </summary>
    private static void RemoveSubtree(SceneDocument document, SceneObject root, bool keepRoot)
    {
        var doomed = CollectSubtree(document, root).Select(item => item.Id).ToHashSet();
        if (keepRoot)
        {
            doomed.Remove(root.Id);
        }

        document.Objects.RemoveAll(item => doomed.Contains(item.Id));
    }

    /// <summary>
    /// 复制变换，避免实例与资产共享可变结构
    /// </summary>
    private static TransformComponent CloneTransform(TransformComponent transform)
    {
        return transform.Mode == TransformMode.Grid
            ? TransformComponent.FromGrid(transform.Grid)
            : TransformComponent.FromFree(transform.Free);
    }

    /// <summary>
    /// 统一资产路径分隔符，保证实例与资产按同一路径匹配
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
