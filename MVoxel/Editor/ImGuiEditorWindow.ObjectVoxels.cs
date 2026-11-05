using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    // 旧场景历史快照可能持有资产编辑前的世界引用；重放历史后需重新读取这些来源。
    private readonly HashSet<string> _voxelAssetsChangedDuringSession = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshEditedVoxelAssetsAfterHistory()
    {
        if (_project is null || _sceneDocument is null || IsAssetStage || _voxelAssetsChangedDuringSession.Count == 0) return;
        foreach (var path in _voxelAssetsChangedDuringSession)
        {
            if (!File.Exists(path)) continue;
            try { RefreshReferencedVoxelAsset(_project, path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { MVoxelLog.Warn($"撤销或重做后无法刷新体素资产：{path}：{exception.Message}"); }
        }
    }

    private void LoadObjectVoxelWorlds(ProjectHandle project)
    {
        _objectVoxels.Clear();
        if (_sceneDocument is null) return;
        if (SyncReferencedSegmentOrigins(project, _sceneDocument, out var originFailed)) MarkSceneDocumentDirty();
        _worldLoadFailed |= originFailed;
        var keys = GetObjectModelLoadKeys(project, _sceneDocument).ToDictionary(item => item.ObjectId, item => item.Key);
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            try
            {
                var data = SceneComponentTypes.ReadVoxelModel(component);
                var world = string.IsNullOrWhiteSpace(data.Model)
                    ? EmbeddedVoxelData.HasData(component) ? EmbeddedVoxelData.Read(component) : new VoxelWorld()
                    : keys.TryGetValue(item.Id, out var key) ? ResolveAssetWorld(key, null, default)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}");
                _objectVoxels.SetWorld(item.Id, world);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                _worldLoadFailed = true;
                MVoxelLog.Error("对象体素加载失败", exception);
            }
        }
        RebuildObjectVoxelWorld();
    }

    /// <summary>
    /// 重新载入场景中仍引用指定体素资产的对象。读取完成后替换；
    /// 已删除的片段保留现有数据并提示，其他片段仍可更新。
    /// </summary>
    private int RefreshReferencedVoxelAsset(ProjectHandle project, string absolutePath,
        IReadOnlyDictionary<string, VoxelCoord64>? previousSegmentOrigins = null)
    {
        var target = Path.GetFullPath(absolutePath);
        var references = GetObjectModelLoadKeys(project, _sceneDocument)
            .Where(item => string.Equals(item.Key.Path, target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var loaded = new List<(StableId Id, VoxelWorld World, VoxelCoord64? NewOrigin, VoxelCoord64? SourceOrigin)>(references.Length);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new List<string>();
        var document = references.Any(item => item.Key.SourceSegment.Length != 0)
            ? LargeWorldFormat.ReadObjects(target) : null;
        if (document is not null && document.Objects.Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).Count() != document.Objects.Count)
            throw new InvalidDataException("体素资产包含重复的对象标识");
        var segments = document?.Objects.ToDictionary(item => item.ObjectId, StringComparer.Ordinal);
        var sceneObjects = _sceneDocument?.Objects.ToDictionary(item => item.Id);
        foreach (var (objectId, key) in references)
        {
            LargeWorldFormat.NativeObjectSegment? segment = null;
            if (key.SourceSegment.Length != 0 && !segments!.TryGetValue(key.SourceSegment, out segment))
            {
                missing.Add(key.SourceSegment);
                continue;
            }
            VoxelCoord64? newOrigin = null;
            VoxelCoord64? sourceOrigin = null;
            var effectiveKey = key;
            if (key.SourceSegment.Length != 0)
            {
                var item = sceneObjects![objectId];
                var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
                var data = SceneComponentTypes.ReadVoxelModel(component);
                sourceOrigin = segment!.Origin;
                var oldOrigin = data.SourceOrigin;
                if (oldOrigin is null && previousSegmentOrigins is not null &&
                    previousSegmentOrigins.TryGetValue(key.SourceSegment, out var initialOrigin)) oldOrigin = initialOrigin;
                if (oldOrigin is { } original && segment.Origin != original)
                {
                    if (item.Transform.Mode != TransformMode.Grid || !item.Transform.Grid.Rotation.Equals(OrthogonalRotation.Identity) ||
                        item.Transform.ScaleX != 1F || item.Transform.ScaleY != 1F || item.Transform.ScaleZ != 1F ||
                        data.ScaleX != 1F || data.ScaleY != 1F || data.ScaleZ != 1F || data.MirrorX)
                    {
                        unsupported.Add(item.Name);
                        continue;
                    }
                    newOrigin = new VoxelCoord64(checked(key.Origin.X + segment.Origin.X - original.X),
                        checked(key.Origin.Y + segment.Origin.Y - original.Y), checked(key.Origin.Z + segment.Origin.Z - original.Z));
                    effectiveKey = key with { Origin = newOrigin.Value };
                }
            }
            loaded.Add((objectId, key.SourceSegment.Length == 0 ? ResolveAssetWorld(effectiveKey, null, default) : ResolveAssetSegment(effectiveKey, segment!), newOrigin, sourceOrigin));
        }
        foreach (var (id, world, newOrigin, sourceOrigin) in loaded)
        {
            _objectVoxels.SetWorld(id, world);
            var item = sceneObjects?.GetValueOrDefault(id);
            if (item is not null && newOrigin is { } origin)
            {
                item.Transform.SetGrid(new GridTransform(origin, item.Transform.Grid.Rotation));
                UpdateTransformMarker(item);
                MarkSceneDocumentDirty();
            }
            var component = item?.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is not null)
            {
                var data = SceneComponentTypes.ReadVoxelModel(component);
                if (data.VoxelCount != world.OccupiedVoxelCount || data.SourceOrigin != sourceOrigin && sourceOrigin is not null)
                {
                    SceneComponentTypes.WriteVoxelModel(component, data with { VoxelCount = world.OccupiedVoxelCount, SourceOrigin = sourceOrigin ?? data.SourceOrigin });
                    if (sourceOrigin is not null && data.SourceOrigin != sourceOrigin) MarkSceneDocumentDirty();
                }
            }
        }
        // 资产编辑保存可能在同一时间戳粒度内完成；显式丢弃对应材质缓存。
        _nativeMaterialCache.Remove(target);
        _runtimeMaterialRefreshAt = 0;
        if (loaded.Count > 0) RebuildObjectVoxelWorld();
        if (missing.Count > 0 || unsupported.Count > 0)
        {
            _worldLoadFailed = true;
            var detail = missing.Count > 0 ? $"{missing.Count} 个来源片段已删除" :
                $"{unsupported.Count} 个对象的旋转、缩放或镜像导致位置未同步";
            throw new InvalidDataException($"已刷新 {loaded.Count} 个引用；{detail}，已阻止覆盖保存场景，请处理后重新打开");
        }
        return loaded.Count;
    }

    private void RebuildObjectVoxelWorld()
    {
        if (_sceneDocument is null) return;
        // 曲线对象的体素藏在组件里，场景加载、复制对象与撤销后都可能缺少世界，这里统一补齐
        EnsureGeneratedVoxelWorlds();
        _world = _objectVoxels.Compose(_sceneDocument, uint.MaxValue, false);
        _layerOwnership.Clear();
        InvalidateComposedWorldCache();
        _pickVersion++;
        _pipeline?.ResetAccumulation();
    }

    private void SaveObjectVoxelWorlds(ProjectHandle project)
    {
        if (_sceneDocument is null) return;
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null || !_objectVoxels.TryGetWorld(item.Id, out var world)) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (string.IsNullOrWhiteSpace(data.Model)) EmbeddedVoxelData.Write(component, world);
            else
            {
                // 外部资产是共享来源；实例编辑转为内嵌数据，不覆盖其他实例引用的文件。
                SceneComponentTypes.WriteVoxelModel(component, data with { VoxelCount = world.OccupiedVoxelCount });
            }
        }
    }

    private Action<bool> ObjectEditCallback(StableId id, Action<bool>? transform = null)
    {
        var component = _sceneDocument!.Objects.First(item => item.Id == id)
            .Components.First(SceneComponentTypes.IsVoxelModel);
        var before = component.Data.Clone();
        var data = SceneComponentTypes.ReadVoxelModel(component);
        return forward =>
        {
            component = _sceneDocument!.Objects.First(item => item.Id == id)
                .Components.First(SceneComponentTypes.IsVoxelModel);
            component.Data = before.Clone();
            if (forward) SceneComponentTypes.WriteVoxelModel(component, data with
            {
                Model = string.Empty, SourceSegment = string.Empty, SourceOrigin = null, VoxelCount = _objectVoxels.GetOrCreate(id).OccupiedVoxelCount,
            });
            transform?.Invoke(forward);
            RebuildObjectVoxelWorld();
        };
    }

    private string CreateObjectRecoveryJson()
    {
        var snapshot = SceneSerializer.LoadFromJson(SceneSerializer.ToJson(_sceneDocument ?? new SceneDocument()));
        foreach (var item in snapshot.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is not null && _objectVoxels.TryGetWorld(item.Id, out var world))
                EmbeddedVoxelData.Write(component, world);
        }
        return SceneSerializer.ToJson(snapshot);
    }
}
