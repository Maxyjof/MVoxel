using ImGuiNET;
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
        _objectLodChains.Clear();
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
                if (string.IsNullOrWhiteSpace(data.Model))
                {
                    if (EmbeddedVoxelData.HasData(component))
                    {
                        var (embedded, chain) = EmbeddedVoxelData.ReadWithLod(component);
                        _objectVoxels.SetWorld(item.Id, embedded);
                        if (chain.LevelCount > 1) _objectLodChains[item.Id] = chain;
                    }
                    else
                    {
                        _objectVoxels.SetWorld(item.Id, new VoxelWorld());
                    }
                }
                else
                {
                    var world = keys.TryGetValue(item.Id, out var key) ? ResolveAssetWorld(key, null, default)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}");
                    _objectVoxels.SetWorld(item.Id, world);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                _worldLoadFailed = true;
                MVoxelLog.Error("对象体素加载失败", exception);
            }
        }
        RebuildObjectVoxelWorld();
        LoadAssetLodChains(project, _sceneDocument, _objectLodChains);
    }

    /// <summary>
    /// 为引用独立模型资产的对象读取预烘焙 LOD 链：资产文件内部保存的数据随模型一起加载，
    /// 与场景内嵌、预制体内嵌的读取路径保持同样的对象级映射
    /// </summary>
    private static void LoadAssetLodChains(ProjectHandle project, SceneDocument scene,
        Dictionary<StableId, ModelLodChain> chains)
    {
        var materialsByPath = new Dictionary<string, MaterialExchangeMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scene.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (string.IsNullOrWhiteSpace(data.Model)) continue;
            var path = Path.GetFullPath(Path.Combine(project.RootPath,
                data.Model.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) continue;
            try
            {
                if (!materialsByPath.TryGetValue(path, out var materials))
                    materialsByPath[path] = materials = LargeWorldFormat.ReadMaterials(path);
                if (!materials.BakedLod.TryGetValue(data.SourceSegment, out var encoded)) continue;
                if (ModelLodChain.Decode(encoded) is { LevelCount: > 1 } chain) chains[item.Id] = chain;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"预烘焙 LOD 读取失败：{data.Model}：{exception.Message}");
            }
        }
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

    /// <summary>
    /// 把对象体素写回场景组件；大模型按区块增量编码，只有被编辑的区块重新压缩
    /// </summary>
    /// <returns>本次重新编码了区块的对象及其区块坐标，供自动保存记录差异日志</returns>
    private IReadOnlyDictionary<StableId, IReadOnlyList<ChunkCoord64>> SaveObjectVoxelWorlds(ProjectHandle project)
    {
        var dirty = new Dictionary<StableId, IReadOnlyList<ChunkCoord64>>();
        if (_sceneDocument is null) return dirty;
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null || !_objectVoxels.TryGetWorld(item.Id, out var world)) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (string.IsNullOrWhiteSpace(data.Model))
            {
                var baked = BakeLodFor(world);
                var changed = EmbeddedVoxelData.Write(component, world, baked);
                if (changed.Count > 0) dirty[item.Id] = changed;
                if (baked is { LevelCount: > 1 }) _objectLodChains[item.Id] = baked;
                else _objectLodChains.Remove(item.Id);
            }
            else
            {
                // 外部资产是共享来源；实例编辑转为内嵌数据，不覆盖其他实例引用的文件。
                SceneComponentTypes.WriteVoxelModel(component, data with { VoxelCount = world.OccupiedVoxelCount });
            }
        }

        return dirty;
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

    private const long SceneSizeWarningThresholdBytes = 512L * 1024 * 1024;
    private const long EstimatedBytesPerPlainCell = 280L;
    private const long EstimatedBytesPerBlockCell = 16L;
    private bool _sceneSizeWarningPending;
    private bool _sceneSizeWarningConfirmed;
    private long _sceneSizeWarningEstimatedBytes;

    /// <summary>
    /// 估算当前场景中内嵌体素写为 JSON 后的体积，用于保存前提醒。
    /// 小模型逐格 JSON 约 250–300 字节，按 280 字节估算；
    /// 大模型走压缩区块，按每格 16 字节（含压缩、Base64 与索引开销）估算。
    /// </summary>
    private long EstimateEmbeddedSceneBytes()
    {
        if (_sceneDocument is null) return 0;
        long estimated = 0;
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (!string.IsNullOrWhiteSpace(data.Model)) continue;
            if (!_objectVoxels.TryGetWorld(item.Id, out var world)) continue;
            var count = world.OccupiedVoxelCount;
            estimated += count < EmbeddedVoxelData.BlockEncodingThreshold
                ? count * EstimatedBytesPerPlainCell
                : count * EstimatedBytesPerBlockCell;
        }
        return estimated;
    }

    /// <summary>
    /// 保存前检查场景内嵌体素体积；超过阈值时只提醒，不自动剥离也不阻止保存。
    /// </summary>
    /// <returns>是否因提示而暂停本次保存</returns>
    private bool PauseForLargeSceneWarning()
    {
        if (_sceneSizeWarningConfirmed || _sceneDocument is null) return false;
        var estimated = EstimateEmbeddedSceneBytes();
        if (estimated < SceneSizeWarningThresholdBytes) return false;
        _sceneSizeWarningEstimatedBytes = estimated;
        _sceneSizeWarningPending = true;
        SetStatus("场景内嵌体素体积较大，已暂停保存；请确认提示，或先剥离部分模型组件");
        return true;
    }

    private static string FormatSceneBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024D * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024D * 1024):0.0} MB";
        return $"{bytes / 1024D:0.0} KB";
    }

    private void DrawSceneSizeWarning()
    {
        if (!_sceneSizeWarningPending) return;
        const string popupName = "场景内嵌体素体积提醒";
        if (!ImGui.IsPopupOpen(popupName)) ImGui.OpenPopup(popupName);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560F, 0F));
        if (CenteredModal.Begin(popupName, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("当前场景的内嵌体素写为 JSON 后体积较大，保存与打开会明显变慢，崩溃恢复也会占用较多磁盘。");
            ImGui.Text($"预计场景体积约 {FormatSceneBytes(_sceneSizeWarningEstimatedBytes)}（小模型逐格、大模型按压缩区块估算）");
            ImGui.TextWrapped("建议先在模型组件中点击“剥离为独立资产…”，把大模型写成外部 .mvmodel 资产后再保存。");
            ImGui.TextWrapped("本提示不会自动剥离，也不会阻止保存；确认后将继续保存当前场景。");
            ImGui.Separator();
            if (ImGui.Button("确认并继续保存"))
            {
                _sceneSizeWarningPending = false;
                _sceneSizeWarningConfirmed = true;
                ImGui.CloseCurrentPopup();
                SaveProject();
            }
            ImGui.EndPopup();
        }
    }
}
