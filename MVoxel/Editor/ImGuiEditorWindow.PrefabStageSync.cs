using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 预制体资产编辑空间：把保存后的修订安全合并回当前场景与其他实例，
/// 覆盖保护、嵌套实例和各处失败回滚都在这里处理
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>按旧资产识别实例覆盖，仅更新未覆盖的对象属性，保留实例对象 ID。</summary>
    private int SyncPrefabInstanceProperties(SceneDocument scene, string relativePath, PrefabAsset previous, PrefabAsset current,
        out HashSet<StableId> settledVoxels, out HashSet<StableId> settledTransforms, StableId? onlyRoot = null)
    {
        settledVoxels = [];
        settledTransforms = [];
        var newSources = current.Objects.ToDictionary(item => item.Id);
        var oldSources = previous.Objects.ToDictionary(item => item.Id);
        var updated = 0;
        var roots = onlyRoot is { } rootId
            ? scene.Objects.Where(item => item.Id == rootId && item.Prefab is not null).ToArray()
            : PrefabSystem.CollectInstanceRoots(scene, relativePath);
        foreach (var root in roots)
        {
            var objectsById = scene.Objects.ToDictionary(value => value.Id);
            var beforeSubtree = SceneSerializer.ToJson(new SceneDocument { Objects = PrefabSystem.CollectSubtree(scene, root).ToList() });
            var overrides = PrefabOverrides.Compute(scene, root, previous)
                .GroupBy(item => item.ObjectId).ToDictionary(group => group.Key, group => group.Select(item => item.Kind).ToHashSet());
            foreach (var item in PrefabSystem.CollectSubtree(scene, root))
            {
                if (IsInsideNestedPrefab(objectsById, root, item)) continue;
                if (root.Prefab is null || !root.Prefab.Links.TryGetValue(item.Id.Value.ToString(), out var sourceText) ||
                    !StableId.TryParse(sourceText, out var sourceId) || !oldSources.ContainsKey(sourceId) ||
                    !newSources.TryGetValue(sourceId, out var source)) continue;
                overrides.TryGetValue(item.Id, out var kinds);
                bool Has(PrefabOverrides.Kind kind) => kinds?.Contains(kind) == true;
                // 根对象的位置属于实例放置，但名称仍可继承来源；实例显式改名时保留覆盖。
                if (!Has(PrefabOverrides.Kind.Renamed)) item.Name = source.Name;
                if (item.Id != root.Id && !Has(PrefabOverrides.Kind.Transform) &&
                    !item.Components.Any(SceneComponentTypes.IsVoxelModel))
                    item.Transform = new TransformComponent(source.Transform.Mode, source.Transform.Grid, source.Transform.Free)
                    { ScaleX = source.Transform.ScaleX, ScaleY = source.Transform.ScaleY, ScaleZ = source.Transform.ScaleZ };
                if (!Has(PrefabOverrides.Kind.Layer)) item.LayerIndex = source.LayerIndex;
                if (!Has(PrefabOverrides.Kind.Visibility)) item.Visible = source.Visible;
                if (!Has(PrefabOverrides.Kind.Lock)) item.Locked = source.Locked;
                if (Has(PrefabOverrides.Kind.Component))
                {
                    // 组件已有明确覆盖，保留实例内容后可把该来源修订视为已处理。
                    settledVoxels.Add(sourceId);
                }
                else
                {
                    // 场景中的体素世界可能包含尚未序列化的编辑；只替换非体素组件。
                    var voxelComponents = item.Components.Where(SceneComponentTypes.IsVoxelModel).ToArray();
                    if (TryUpdatePrefabVoxelComponent(item, oldSources[sourceId], source, out var replacement))
                    {
                        voxelComponents = replacement;
                        settledVoxels.Add(sourceId);
                    }
                    item.Components = source.Components.Where(component => !SceneComponentTypes.IsVoxelModel(component))
                        .Select(component => new ComponentRecord
                        {
                            TypeId = component.TypeId, SchemaVersion = component.SchemaVersion,
                            Enabled = component.Enabled, OwnerPluginId = component.OwnerPluginId,
                            Data = component.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined ? component.Data : component.Data.Clone(),
                        }).Concat(voxelComponents).ToList();
                }
                // 体素坐标存放在世界中。无实例变换覆盖时，可以安全合并纯格平移；
                // 引用模型需随原点平移，内嵌模型的新快照本身已包含平移后的坐标。
                var beforeTransform = oldSources[sourceId].Transform;
                var afterTransform = source.Transform;
                var sourceModel = source.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (item.Id != root.Id && sourceModel is not null && settledVoxels.Contains(sourceId) &&
                    !Has(PrefabOverrides.Kind.Transform) && !Has(PrefabOverrides.Kind.Component) && beforeTransform.Mode == TransformMode.Grid &&
                    afterTransform.Mode == TransformMode.Grid && item.Transform.Mode == TransformMode.Grid &&
                    beforeTransform.Grid.Rotation.Equals(afterTransform.Grid.Rotation) &&
                    beforeTransform.ScaleX == afterTransform.ScaleX && beforeTransform.ScaleY == afterTransform.ScaleY &&
                    beforeTransform.ScaleZ == afterTransform.ScaleZ)
                {
                    var oldPosition = beforeTransform.Grid.Position;
                    var newPosition = afterTransform.Grid.Position;
                    if (oldPosition != newPosition)
                    {
                        var modelData = SceneComponentTypes.ReadVoxelModel(sourceModel);
                        if (!string.IsNullOrWhiteSpace(modelData.Model) && _objectVoxels.TryGetWorld(item.Id, out var world))
                            _objectVoxels.SetWorld(item.Id, world.CloneShifted(checked(newPosition.X - oldPosition.X),
                                checked(newPosition.Y - oldPosition.Y), checked(newPosition.Z - oldPosition.Z)));
                        item.Transform.SetGrid(afterTransform.Grid);
                        UpdateTransformMarker(item);
                    }
                    settledTransforms.Add(sourceId);
                }
            }
            RemoveDeletedPrefabLeaves(scene, root, previous, current, overrides, objectsById);
            AddNewPrefabObjects(scene, root, previous, current, objectsById);
            var afterSubtree = SceneSerializer.ToJson(new SceneDocument { Objects = PrefabSystem.CollectSubtree(scene, root).ToList() });
            if (!string.Equals(beforeSubtree, afterSubtree, StringComparison.Ordinal)) MarkSceneDocumentDirty();
            updated++;
        }
        return updated;
    }

    /// <summary>打开场景时依据保存在资产中的旧版，合并此前未应用的预制体更新。</summary>
    private int SyncPendingPrefabRevisions(string? onlyAbsolutePath = null)
    {
        if (_project is null || _sceneDocument is null || _worldLoadFailed) return 0;
        var roots = _sceneDocument.Objects.Where(item => item.Prefab is not null).ToArray();
        var assets = new Dictionary<string, PrefabAsset>(StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        foreach (var root in roots)
        {
            var link = root.Prefab!;
            Dictionary<StableId, SceneObject>? snapshotBefore = null;
            Dictionary<StableId, SceneObject>? objectsBefore = null;
            List<SceneObject>? objectOrderBefore = null;
            Dictionary<StableId, VoxelWorld>? worldsBefore = null;
            var dirtyBefore = _sceneDocumentDirty;
            var revisionBefore = _historySceneRevision;
            var selectionBefore = _selectedSceneObjectId;
            var editTargetBefore = _voxelEditTargetId;
            try
            {
                var relative = link.AssetPath.Replace('\\', '/');
                if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
                var assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath)) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(Path.Combine(assetsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
                    (onlyAbsolutePath is not null && !string.Equals(path, onlyAbsolutePath, StringComparison.OrdinalIgnoreCase))) continue;
                if (!assets.TryGetValue(path, out var asset))
                {
                    asset = PrefabSerializer.Load(path);
                    assets[path] = asset;
                    _prefabCache[path] = asset;
                }
                if (asset.PrefabId != link.SourceId || asset.Revision <= link.SourceRevision) continue;
                var baseline = asset.Revisions.FirstOrDefault(item => item.Revision == link.SourceRevision);
                if (baseline is null) continue;
                var previous = new PrefabAsset { PrefabId = asset.PrefabId, Name = baseline.Name, Objects = baseline.Objects, Revision = baseline.Revision };
                var subtreeBefore = PrefabSystem.CollectSubtree(_sceneDocument, root).ToList();
                snapshotBefore = subtreeBefore.ToDictionary(item => item.Id, SceneSerializer.CloneObject);
                objectsBefore = subtreeBefore.ToDictionary(item => item.Id);
                objectOrderBefore = _sceneDocument.Objects.ToList();
                worldsBefore = _objectVoxels.CaptureReferences();
                // 外层跳过嵌套子树；嵌套实例按自己的来源和修订独立处理。
                if (SyncPrefabInstanceProperties(_sceneDocument, link.AssetPath, previous, asset, out var settledVoxels, out var settledTransforms, root.Id) > 0)
                {
                    // 只有已完整覆盖的情况才能推进基线；其余留待之后重试。
                    if (CanMergePrefabRevision(previous, asset, _sceneDocument, root, settledVoxels, settledTransforms))
                    {
                        link.SourceRevision = asset.Revision;
                        MarkSceneDocumentDirty();
                    }
                    applied++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OverflowException or System.Text.Json.JsonException)
            {
                if (snapshotBefore is not null && objectsBefore is not null && objectOrderBefore is not null && worldsBefore is not null)
                {
                    foreach (var item in snapshotBefore.Values)
                    {
                        var target = objectsBefore[item.Id];
                        SceneObjectStateCommand.Copy(target, item);
                    }
                    _sceneDocument.Objects = objectOrderBefore;
                    _objectVoxels.RestoreReferences(worldsBefore);
                    _sceneDocumentDirty = dirtyBefore;
                    _historySceneRevision = revisionBefore;
                    _selectedSceneObjectId = selectionBefore;
                    _voxelEditTargetId = editTargetBefore;
                    RebuildObjectVoxelWorld();
                }
                MVoxelLog.Warn($"预制体更新未合并，已恢复实例：{link.AssetPath}：{exception.Message}");
            }
        }
        if (applied > 0) RebuildObjectVoxelWorld();
        return applied;
    }

    private static bool CanMergePrefabRevision(PrefabAsset previous, PrefabAsset current, SceneDocument scene, SceneObject root,
        IReadOnlySet<StableId> settledVoxels, IReadOnlySet<StableId> settledTransforms)
    {
        if (root.Prefab is null) return false;
        var old = previous.Objects.ToDictionary(item => item.Id);
        var currentById = current.Objects.ToDictionary(item => item.Id);
        var currentIds = current.Objects.Select(item => item.Id).ToHashSet();
        var live = PrefabSystem.CollectSubtree(scene, root).Select(item => item.Id).ToHashSet();
        var linked = root.Prefab.Links.Where(pair => StableId.TryParse(pair.Key, out var id) && live.Contains(id))
            .Select(pair => StableId.TryParse(pair.Value, out var id) ? id : StableId.Empty).ToHashSet();
        // 已从实例安全移除的来源对象可以推进基线；仍保留的覆盖不得丢失旧基线。
        if (old.Keys.Any(id => !currentIds.Contains(id) && linked.Contains(id))) return false;
        foreach (var item in current.Objects)
        {
            if (!old.TryGetValue(item.Id, out var before))
            {
                if (item.Prefab is not null || !linked.Contains(item.Id)) return false;
                continue;
            }
            // 原实例已删除的来源对象属于删除覆盖；不应为它应用新体素或父级变更。
            if (!linked.Contains(item.Id)) continue;
            if (before.ParentId != item.ParentId) return false;
            // 外层同步不会改写嵌套实例；只有嵌套子树在两版中完全相同时，
            // 才允许外层推进修订，避免无变化的嵌套实例造成永久待同步。
            if (IsNestedSource(old, before, previous.PrefabId) || IsNestedSource(currentById, item, current.PrefabId))
            {
                if (System.Text.Json.JsonSerializer.Serialize(before) != System.Text.Json.JsonSerializer.Serialize(item)) return false;
                continue;
            }
            var oldVoxel = before.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            var newVoxel = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (oldVoxel is not null && newVoxel is not null &&
                System.Text.Json.JsonSerializer.Serialize(before.Transform) != System.Text.Json.JsonSerializer.Serialize(item.Transform) &&
                !settledTransforms.Contains(item.Id)) return false;
            var changedVoxel = (oldVoxel is null) != (newVoxel is null) ||
                (oldVoxel is not null && newVoxel is not null &&
                 (!System.Text.Json.JsonElement.DeepEquals(oldVoxel.Data, newVoxel.Data) || oldVoxel.Enabled != newVoxel.Enabled));
            if (changedVoxel && linked.Contains(item.Id) && !settledVoxels.Contains(item.Id)) return false;
        }
        return true;
    }

    private static bool IsNestedSource(IReadOnlyDictionary<StableId, SceneObject> objects, SceneObject item, StableId rootId)
    {
        var visited = new HashSet<StableId>();
        while (item.Id != rootId && visited.Add(item.Id))
        {
            if (item.Prefab is not null) return true;
            if (item.ParentId is not { } parent || !objects.TryGetValue(parent, out item!)) break;
        }
        return false;
    }

    private static bool IsInsideNestedPrefab(IReadOnlyDictionary<StableId, SceneObject> byId, SceneObject root, SceneObject item)
    {
        var current = item;
        var visited = new HashSet<StableId>();
        while (current.Id != root.Id && visited.Add(current.Id))
        {
            if (current.Prefab is not null) return true;
            if (current.ParentId is not { } parent || !byId.TryGetValue(parent, out current!)) break;
        }
        return false;
    }

    /// <summary>仅删除无覆盖、无子对象及无组件引用的实例叶节点。</summary>
    private void RemoveDeletedPrefabLeaves(SceneDocument scene, SceneObject root, PrefabAsset previous, PrefabAsset current,
        Dictionary<StableId, HashSet<PrefabOverrides.Kind>> overrides, Dictionary<StableId, SceneObject> objectsById)
    {
        if (root.Prefab is null) return;
        var oldSources = previous.Objects.ToDictionary(item => item.Id);
        var remainingSources = current.Objects.Select(item => item.Id).ToHashSet();
        var pending = PrefabSystem.CollectSubtree(scene, root).Where(item => item.Id != root.Id &&
            root.Prefab.Links.TryGetValue(item.Id.Value.ToString(), out var text) &&
            StableId.TryParse(text, out var id) && oldSources.ContainsKey(id) && !remainingSources.Contains(id)).ToList();
        var progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            foreach (var item in pending.ToArray())
            {
                if (IsInsideNestedPrefab(objectsById, root, item) || scene.Objects.Any(other => other.ParentId == item.Id) ||
                    overrides.TryGetValue(item.Id, out var kinds) && kinds.Count > 0) continue;
                var idText = item.Id.Value.ToString();
                if (scene.Objects.Where(other => other.Id != item.Id).SelectMany(other => other.Components)
                    .Any(component => component.Data.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                        component.Data.GetRawText().Contains(idText, StringComparison.OrdinalIgnoreCase))) continue;
                if (!StableId.TryParse(root.Prefab.Links[idText], out var sourceId)) continue;
                var oldModel = oldSources[sourceId].Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                try
                {
                    if (oldModel is not null)
                    {
                        var baseline = ReadPrefabVoxelComponent(oldModel, item);
                        if (!_objectVoxels.TryGetWorld(item.Id, out var actual) || actual.OccupiedVoxelCount != baseline.OccupiedVoxelCount ||
                            baseline.EnumerateOccupied().Any(cell => !actual.TryGet(cell.Coordinate, out var value) || value != cell.Value)) continue;
                    }
                    else if (_objectVoxels.TryGetWorld(item.Id, out var actual) && actual.OccupiedVoxelCount != 0) continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                { throw new InvalidOperationException($"无法检查预制体已删除对象“{item.Name}”", exception); }
                scene.Objects.Remove(item); objectsById.Remove(item.Id); _objectVoxels.Remove(item.Id); root.Prefab.Links.Remove(idText);
                if (_selectedSceneObjectId == item.Id) _selectedSceneObjectId = root.Id;
                if (_voxelEditTargetId == item.Id) _voxelEditTargetId = null;
                pending.Remove(item); progress = true;
            }
        }
    }

    /// <summary>只补入资产新增的普通对象；实例侧删除、嵌套预制体和缺失父级保持原状。</summary>
    private void AddNewPrefabObjects(SceneDocument scene, SceneObject root, PrefabAsset previous, PrefabAsset current,
        Dictionary<StableId, SceneObject> objectsById)
    {
        if (root.Prefab is null) return;
        var oldIds = previous.Objects.Select(item => item.Id).ToHashSet();
        var sourceToInstance = new Dictionary<StableId, StableId>();
        var subtreeIds = PrefabSystem.CollectSubtree(scene, root).Select(item => item.Id).ToHashSet();
        foreach (var (instanceText, sourceText) in root.Prefab.Links)
            if (StableId.TryParse(instanceText, out var instanceId) && subtreeIds.Contains(instanceId) &&
                StableId.TryParse(sourceText, out var sourceId)) sourceToInstance[sourceId] = instanceId;
        var pending = current.Objects.Where(item => !oldIds.Contains(item.Id) && !sourceToInstance.ContainsKey(item.Id)).ToList();
        var madeProgress = true;
        while (madeProgress && pending.Count > 0)
        {
            madeProgress = false;
            foreach (var source in pending.ToArray())
            {
                if (source.Prefab is not null || source.ParentId is not { } parent ||
                    !sourceToInstance.TryGetValue(parent, out var parentId)) continue;
                objectsById.TryGetValue(parentId, out var parentObject);
                if (parentObject is null || IsInsideNestedPrefab(objectsById, root, parentObject)) continue;
                var clone = new SceneObject
                {
                    Id = StableId.New(), ParentId = parentId, Name = source.Name,
                    Visible = source.Visible, Locked = source.Locked, LayerIndex = source.LayerIndex,
                    Transform = new TransformComponent(source.Transform.Mode, source.Transform.Grid, source.Transform.Free)
                    { ScaleX = source.Transform.ScaleX, ScaleY = source.Transform.ScaleY, ScaleZ = source.Transform.ScaleZ },
                    Components = source.Components.Select(component => new ComponentRecord
                    {
                        TypeId = component.TypeId, SchemaVersion = component.SchemaVersion, Enabled = component.Enabled,
                        OwnerPluginId = component.OwnerPluginId,
                        Data = component.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined ? component.Data : component.Data.Clone(),
                    }).ToList(),
                };
                var model = clone.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                VoxelWorld? world = null;
                try { if (model is not null) world = ReadPrefabVoxelComponent(model, clone); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                { throw new InvalidOperationException($"无法加载预制体新增对象“{source.Name}”", exception); }
                scene.Objects.Add(clone);
                objectsById[clone.Id] = clone;
                if (world is not null) _objectVoxels.SetWorld(clone.Id, world);
                root.Prefab.Links[clone.Id.Value.ToString()] = source.Id.Value.ToString();
                sourceToInstance[source.Id] = clone.Id;
                pending.Remove(source);
                madeProgress = true;
            }
        }
    }

    private bool TryUpdatePrefabVoxelComponent(SceneObject instance, SceneObject oldSource, SceneObject newSource,
        out ComponentRecord[] replacement)
    {
        replacement = instance.Components.Where(SceneComponentTypes.IsVoxelModel).ToArray();
        var oldComponent = oldSource.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        var newComponent = newSource.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (oldComponent is null && newComponent is null) return false;
        try
        {
            if (oldComponent is not null)
            {
                var oldWorld = ReadPrefabVoxelComponent(oldComponent, instance);
                if (!_objectVoxels.TryGetWorld(instance.Id, out var actual) ||
                    actual.OccupiedVoxelCount != oldWorld.OccupiedVoxelCount ||
                    oldWorld.EnumerateOccupied().Any(cell => !actual.TryGet(cell.Coordinate, out var value) || value != cell.Value))
                    return false;
            }
            else if (_objectVoxels.TryGetWorld(instance.Id, out var existing) && existing.OccupiedVoxelCount != 0)
                return false;
            if (newComponent is null)
            {
                _objectVoxels.Remove(instance.Id);
                replacement = [];
            }
            else
            {
                var world = ReadPrefabVoxelComponent(newComponent, instance);
                _objectVoxels.SetWorld(instance.Id, world);
                replacement = [new ComponentRecord { TypeId = newComponent.TypeId, SchemaVersion = newComponent.SchemaVersion,
                    Enabled = newComponent.Enabled, OwnerPluginId = newComponent.OwnerPluginId,
                    Data = newComponent.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined ? newComponent.Data : newComponent.Data.Clone() }];
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法同步预制体实例“{instance.Name}”的体素", exception);
        }
    }

    private VoxelWorld ReadPrefabVoxelComponent(ComponentRecord component, SceneObject instance)
    {
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var model = data.Model;
        if (string.IsNullOrWhiteSpace(model))
            return EmbeddedVoxelData.HasData(component) ? EmbeddedVoxelData.Read(component) : new VoxelWorld();
        if (_project is null) throw new InvalidOperationException("项目未打开");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_project.RootPath, model.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型路径不在项目资产内");
        return ResolveAssetWorld(new ModelLoadKey(path, GetSceneModelOrigin(instance), data.SourceSegment), null, default);
    }
}
