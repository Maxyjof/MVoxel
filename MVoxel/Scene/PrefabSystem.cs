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
                Components = [],
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

        // 实例根的链接表覆盖整棵子树，与从场景创建实例时的语义一致
        if (newRoot?.Prefab is not null)
        {
            foreach (var source in asset.Objects)
            {
                newRoot.Prefab.Links[map[source.Id].Id.Value.ToString()] = source.Id.Value.ToString();
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
    internal static void RemoveSubtree(SceneDocument document, SceneObject root, bool keepRoot)
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
    /// 把实例转换为变体资产：快照实例子树并记录基础链与逐对象覆盖类型，
    /// 返回的资产由调用方保存为新的 .prefab 文件，实例随后改指向变体
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="instanceRoot">实例根</param>
    /// <param name="baseAsset">实例当前来源资产</param>
    /// <param name="name">变体资产显示名</param>
    /// <returns>待保存的变体资产</returns>
    public static PrefabAsset CreateVariantFromInstance(SceneDocument document, SceneObject instanceRoot,
        PrefabAsset baseAsset, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(baseAsset);
        if (instanceRoot.Prefab is null)
        {
            throw new InvalidOperationException("只有预制体实例可以转换为变体");
        }

        var variant = ApplyInstanceToAsset(document, instanceRoot, name);
        variant.BaseAssetPath = instanceRoot.Prefab.AssetPath;
        variant.BasePrefabId = baseAsset.PrefabId;
        variant.BaseLinks = new Dictionary<string, string>(instanceRoot.Prefab.Links, StringComparer.Ordinal);
        foreach (var item in PrefabOverrides.Compute(document, instanceRoot, baseAsset))
        {
            if (item.Kind is PrefabOverrides.Kind.SourceDrift or PrefabOverrides.Kind.Missing)
            {
                continue;
            }

            var key = item.ObjectId.Value.ToString();
            variant.OverrideKinds[key] = variant.OverrideKinds.TryGetValue(key, out var existing)
                ? $"{existing},{item.Kind}"
                : item.Kind.ToString();
        }

        return variant;
    }

    /// <summary>
    /// 计算变体的生效内容：非覆盖类型继承基础资产最新值，覆盖类型保留变体值，
    /// 基础新增对象并入变体树，变体删除的对象保持删除
    /// </summary>
    /// <param name="variant">变体资产</param>
    /// <param name="baseAsset">基础资产当前内容</param>
    /// <returns>生效内容、冲突说明列表与基础是否有更新</returns>
    public static (PrefabAsset Effective, IReadOnlyList<string> Conflicts, bool BaseChanged)
        ComputeEffectiveVariant(PrefabAsset variant, PrefabAsset baseAsset)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(baseAsset);
        var conflicts = new List<string>();
        var baseChanged = variant.BasePrefabId != StableId.Empty && baseAsset.PrefabId != variant.BasePrefabId;
        if (variant.BaseAssetPath.Length == 0 ||
            variant.BasePrefabId == StableId.Empty ||
            !baseChanged)
        {
            return (variant, conflicts, baseChanged);
        }

        var baseById = baseAsset.Objects.ToDictionary(item => item.Id);
        var linkedBaseIds = new HashSet<StableId>(
            variant.BaseLinks.Values
                .Select(value => StableId.TryParse(value, out var id) ? id : StableId.Empty)
                .Where(id => id != StableId.Empty));
        var merged = new List<SceneObject>();
        foreach (var variantObject in variant.Objects)
        {
            SceneObject? baseObject = null;
            var hasBase = variant.BaseLinks.TryGetValue(variantObject.Id.Value.ToString(), out var baseLink) &&
                StableId.TryParse(baseLink, out var baseId) &&
                baseById.TryGetValue(baseId, out baseObject);
            if (!hasBase || baseObject is null)
            {
                merged.Add(CloneObject(variantObject)); // 基础中已删除或变体新增：保留变体内容
                continue;
            }

            var kinds = ParseKinds(variant.OverrideKinds, variantObject.Id);
            var clone = new SceneObject
            {
                Id = variantObject.Id,
                ParentId = variantObject.ParentId,
                Name = kinds.Contains(PrefabOverrides.Kind.Renamed) ? variantObject.Name : baseObject.Name,
                Visible = kinds.Contains(PrefabOverrides.Kind.Visibility) ? variantObject.Visible : baseObject.Visible,
                Locked = kinds.Contains(PrefabOverrides.Kind.Lock) ? variantObject.Locked : baseObject.Locked,
                LayerIndex = kinds.Contains(PrefabOverrides.Kind.Layer) ? variantObject.LayerIndex : baseObject.LayerIndex,
                Transform = kinds.Contains(PrefabOverrides.Kind.Transform)
                    ? CloneTransform(variantObject.Transform)
                    : CloneTransform(baseObject.Transform),
                Components = [],
            };
            clone.Components.AddRange(
                (kinds.Contains(PrefabOverrides.Kind.Component)
                    ? variantObject.Components
                    : baseObject.Components)
                .Select(item => new ComponentRecord
                {
                    Enabled = item.Enabled,
                    OwnerPluginId = item.OwnerPluginId,
                    TypeId = item.TypeId,
                    SchemaVersion = item.SchemaVersion,
                    Data = item.Data.ValueKind == JsonValueKind.Undefined ? item.Data : item.Data.Clone(),
                }));
            merged.Add(clone);
            if (kinds.Count > 0)
            {
                conflicts.Add($"{variantObject.Name}：{string.Join("、", kinds.Select(kind => KindLabel(kind)))}（保留变体值）");
            }
        }

        // 基础侧新增对象并入变体树：父级优先映射到变体对象，否则挂到变体根
        var rootId = variant.Objects[0].Id;
        var newBaseIds = baseAsset.Objects
            .Select(item => item.Id)
            .Where(id => !linkedBaseIds.Contains(id))
            .ToHashSet();
        var addedLinks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var baseObject in baseAsset.Objects)
        {
            if (!newBaseIds.Contains(baseObject.Id))
            {
                continue;
            }

            var clone = CloneObject(baseObject);
            clone.Id = StableId.New();
            clone.Prefab = null;
            clone.ParentId = baseObject.ParentId is not null &&
                baseById.TryGetValue(baseObject.ParentId.Value, out var baseParent) &&
                addedLinks.TryGetValue(baseParent.Id.Value.ToString(), out var mappedParent)
                ? StableId.Parse(mappedParent)
                : rootId;
            merged.Add(clone);
            addedLinks[clone.Id.Value.ToString()] = baseObject.Id.Value.ToString();
        }

        var effective = new PrefabAsset
        {
            Name = variant.Name,
            PrefabId = variant.PrefabId,
            BaseAssetPath = variant.BaseAssetPath,
            BasePrefabId = variant.BasePrefabId,
            BaseLinks = new Dictionary<string, string>(variant.BaseLinks, StringComparer.Ordinal),
            OverrideKinds = new Dictionary<string, string>(variant.OverrideKinds, StringComparer.Ordinal),
            Objects = merged,
        };
        foreach (var (key, value) in addedLinks)
        {
            effective.BaseLinks[key] = value;
        }

        return (effective, conflicts, baseChanged);
    }

    /// <summary>
    /// 应用变体的基础更新：把生效内容写回变体资产（含新并入对象）并原子保存
    /// </summary>
    /// <param name="variant">变体资产（就地更新 Objects 与 BaseLinks、BasePrefabId）</param>
    /// <param name="baseAsset">基础资产当前内容</param>
    /// <param name="path">变体资产文件路径</param>
    /// <returns>冲突说明列表</returns>
    public static IReadOnlyList<string> PullBaseUpdate(PrefabAsset variant, PrefabAsset baseAsset, string path)
    {
        var (effective, conflicts, baseChanged) = ComputeEffectiveVariant(variant, baseAsset);
        if (!baseChanged)
        {
            return conflicts;
        }

        variant.Objects = effective.Objects;
        variant.BaseLinks = new Dictionary<string, string>(effective.BaseLinks, StringComparer.Ordinal);
        variant.BasePrefabId = baseAsset.PrefabId;
        PrefabSerializer.Save(path, variant);
        return conflicts;
    }

    /// <summary>
    /// 深拷贝场景对象（不保留实例标记）
    /// </summary>
    private static SceneObject CloneObject(SceneObject source)
    {
        return new SceneObject
        {
            Id = source.Id,
            ParentId = source.ParentId,
            Name = source.Name,
            Visible = source.Visible,
            Locked = source.Locked,
            LayerIndex = source.LayerIndex,
            Transform = CloneTransform(source.Transform),
            Components = source.Components.Select(item => new ComponentRecord
            {
                Enabled = item.Enabled,
                OwnerPluginId = item.OwnerPluginId,
                TypeId = item.TypeId,
                SchemaVersion = item.SchemaVersion,
                Data = item.Data.ValueKind == JsonValueKind.Undefined ? item.Data : item.Data.Clone(),
            }).ToList(),
        };
    }

    /// <summary>
    /// 读取某对象的覆盖类型名单
    /// </summary>
    private static HashSet<PrefabOverrides.Kind> ParseKinds(IReadOnlyDictionary<string, string> overrideKinds,
        StableId objectId)
    {
        var result = new HashSet<PrefabOverrides.Kind>();
        if (overrideKinds.TryGetValue(objectId.Value.ToString(), out var text))
        {
            foreach (var name in text.Split(','))
            {
                if (Enum.TryParse<PrefabOverrides.Kind>(name.Trim(), out var kind))
                {
                    result.Add(kind);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 覆盖类型的中文标签（与检查器一致）
    /// </summary>
    public static string KindLabel(PrefabOverrides.Kind kind) => kind switch
    {
        PrefabOverrides.Kind.Renamed => "重命名",
        PrefabOverrides.Kind.Transform => "变换",
        PrefabOverrides.Kind.Layer => "图层",
        PrefabOverrides.Kind.Visibility => "可见性",
        PrefabOverrides.Kind.Lock => "锁定",
        PrefabOverrides.Kind.Component => "组件",
        PrefabOverrides.Kind.Added => "新增对象",
        PrefabOverrides.Kind.Missing => "缺失对象",
        PrefabOverrides.Kind.SourceDrift => "来源漂移",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 统一资产路径分隔符，保证实例与资产按同一路径匹配
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
