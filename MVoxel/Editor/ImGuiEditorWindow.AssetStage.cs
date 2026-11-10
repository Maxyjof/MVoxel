using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private sealed record VoxelStage(string RelativePath, string AbsolutePath, MaterialExchangeMetadata Materials,
        byte[] OriginalHash, SceneDocument? Scene, Dictionary<StableId, VoxelWorld> Worlds,
        bool SceneDirty, bool LoadFailed, StableId? Selection, StableId? EditTarget, StableId ModelId,
        StableId? InspectorLock, StableId? Pinned, CommandHistory SceneHistory, AssetStageCamera Camera,
        Dictionary<StableId, string>? SegmentIds = null, Dictionary<string, VoxelCoord64>? OriginalSegmentOrigins = null,
        bool Saved = false);
    private const string EmbeddedMaterialPrefix = "@mvasset/";
    private VoxelStage? _voxelStage;
    private bool _voxelStageExitRequested;
    private sealed record PrefabStage(string RelativePath, string AbsolutePath, PrefabAsset Original, PrefabAsset Effective,
        string? BasePath, Dictionary<string, byte[]> BaseHashes, byte[] OriginalHash,
        SceneDocument? Scene, Dictionary<StableId, VoxelWorld> Worlds, bool SceneDirty, bool LoadFailed,
        StableId? Selection, StableId? EditTarget, StableId? InspectorLock, StableId? Pinned,
        CommandHistory SceneHistory, AssetStageCamera Camera, bool Saved = false);
    private readonly record struct AssetStageCamera(System.Numerics.Vector2 Pan, float Height, float Distance,
        float Yaw, float Pitch, float Zoom, StableId? RenderCameraId, OpenTK.Mathematics.Vector3? RenderAnchor);

    private AssetStageCamera CaptureAssetStageCamera() => new(_scenePan, _sceneFreeMoveHeight, _sceneCameraDistance,
        _sceneOrbitYaw, _sceneOrbitPitch, _sceneZoom, _renderCameraId, _renderOrbitAnchor);

    private void RestoreAssetStageCamera(AssetStageCamera camera)
    {
        _scenePan = camera.Pan; _sceneFreeMoveHeight = camera.Height; _sceneCameraDistance = camera.Distance;
        _sceneOrbitYaw = camera.Yaw; _sceneOrbitPitch = camera.Pitch; _sceneZoom = camera.Zoom;
        _renderCameraId = camera.RenderCameraId; _renderOrbitAnchor = camera.RenderAnchor;
        _sceneFocusAnimation = null; _renderFocusAnimation = null;
    }
    private PrefabStage? _prefabStage;
    private bool IsAssetStage => _voxelStage is not null || _prefabStage is not null;

    /// <summary>各资产在编辑空间里的上次取景，返回场景后再进入时恢复；仅本次会话有效</summary>
    private readonly Dictionary<string, AssetStageCamera> _assetStageCameras = new(StringComparer.OrdinalIgnoreCase);
    private bool HasSuspendedSceneChanges => _voxelStage?.SceneDirty == true || _prefabStage?.SceneDirty == true;
    private string ActiveEditingName => _voxelStage is { } voxel ? $"体素资产：{Path.GetFileNameWithoutExtension(voxel.RelativePath)}"
        : _prefabStage is { } prefab ? $"预制体：{Path.GetFileNameWithoutExtension(prefab.RelativePath)}"
        : Path.GetFileNameWithoutExtension(_activeSceneRelativePath);
    private bool SaveAssetStage() => _prefabStage is not null ? SavePrefabAssetStage() : SaveVoxelAssetStage();

    private IReadOnlyList<string> FindOpenAssetReferenceNames(string path)
    {
        var names = new List<string>();
        if (_sceneDocument is not null)
            names.AddRange(AssetReferences.FindReferencingObjects(_sceneDocument, path)
                .Select(item => IsAssetStage ? $"编辑空间：{item.Name}" : item.Name));
        var suspended = _voxelStage?.Scene ?? _prefabStage?.Scene;
        if (suspended is not null)
            names.AddRange(AssetReferences.FindReferencingObjects(suspended, path)
                .Select(item => $"原场景：{item.Name}"));
        var editingPath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
        if (editingPath is not null && string.Equals(editingPath.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            names.Add("当前正在编辑的资产");
        return names;
    }

    private int UpdateSuspendedSceneReferences(string oldPath, string newPath)
    {
        if (_voxelStage is { Scene: { } voxelScene } voxel)
        {
            var count = AssetReferences.UpdateReferences(voxelScene, oldPath, newPath);
            if (count > 0) _voxelStage = voxel with { SceneDirty = true };
            return count;
        }
        if (_prefabStage is { Scene: { } prefabScene } prefab)
        {
            var count = AssetReferences.UpdateReferences(prefabScene, oldPath, newPath);
            if (count > 0) _prefabStage = prefab with { SceneDirty = true };
            return count;
        }
        return 0;
    }

    private void MarkSuspendedSceneDirty()
    {
        if (_voxelStage is { } voxel) _voxelStage = voxel with { SceneDirty = true };
        else if (_prefabStage is { } prefab) _prefabStage = prefab with { SceneDirty = true };
        else MarkSceneDocumentDirty();
    }

    private void UpdateEditingAssetPath(string oldPath, string newPath)
    {
        if (_project is null) return;
        var absolute = Path.GetFullPath(Path.Combine(_project.AssetsPath, newPath.Replace('/', Path.DirectorySeparatorChar)));
        if (_voxelStage is { } voxel && string.Equals(voxel.RelativePath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            _voxelStage = voxel with { RelativePath = newPath, AbsolutePath = absolute };
            if (_sceneDocument is not null && voxel.SegmentIds is null &&
                string.Equals(_sceneDocument.Name, Path.GetFileNameWithoutExtension(oldPath), StringComparison.Ordinal))
            {
                var previousName = _sceneDocument.Name;
                var nextName = Path.GetFileNameWithoutExtension(newPath);
                _sceneDocument.Name = nextName;
                if (_sceneDocument.Objects.FirstOrDefault(item => item.Id == voxel.ModelId) is { } model &&
                    string.Equals(model.Name, previousName, StringComparison.Ordinal)) model.Name = nextName;
            }
            if (_voxelAssetsChangedDuringSession.Remove(voxel.AbsolutePath)) _voxelAssetsChangedDuringSession.Add(absolute);
            _assetBrowser.EditingAssetPath = newPath;
        }
        else if (_prefabStage is { } prefab && string.Equals(prefab.RelativePath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            _prefabStage = prefab with { RelativePath = newPath, AbsolutePath = absolute };
            _assetBrowser.EditingAssetPath = newPath;
        }
    }

    private static byte[] HashAssetFile(string path)
    {
        using var stream = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    private void OpenVoxelAssetStage(string relativePath)
    {
        if (_project is null) return;
        if (IsAssetStage)
        {
            // 同一资产重复打开时给出明确提示，避免被误读成“不能打开任何资产”
            var editingPath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
            SetStatus(string.Equals(editingPath, relativePath, StringComparison.OrdinalIgnoreCase)
                ? $"当前已在编辑空间打开该资产：{relativePath}"
                : "请先返回场景，再打开其他资产");
            return;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { SetStatus("体素资产路径无效"); return; }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".vox" or ".qb" or ".schem") { ImportExternalVoxelForEditing(path); return; }
        if (extension is not (".mvmodel" or ".mvworld")) { SetStatus("不支持的体素资产格式"); return; }
        BeginStageTransition();
        try
        {
            var hash = HashAssetFile(path);
            var multi = IsNativeObjectDocument(path);
            var multiDocument = multi ? LargeWorldFormat.ReadObjects(path) : null;
            var singleDocument = multi ? null : LargeWorldFormat.ReadDocument(path);
            var materials = multiDocument?.Materials ?? singleDocument!.Materials;
            if (!HashAssetFile(path).SequenceEqual(hash))
                throw new InvalidOperationException("体素资产读取期间发生变化，请重新打开");
            var scene = new SceneDocument { Name = Path.GetFileNameWithoutExtension(path) };
            var graph = new SceneGraph(scene);
            var worlds = new Dictionary<StableId, VoxelWorld>();
            Dictionary<StableId, string>? segmentIds = multi ? [] : null;
            if (multiDocument is not null)
            {
                if (multiDocument.Objects.Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).Count() != multiDocument.Objects.Count)
                    throw new InvalidDataException("对象片段标识重复");
                foreach (var segment in multiDocument.Objects)
                {
                    var item = graph.Add(segment.Name, transform: TransformComponent.FromGrid(new GridTransform(segment.Origin, OrthogonalRotation.Identity)));
                    item.Components.Add(SceneComponentTypes.CreateVoxelModel(string.Empty, segment.World.OccupiedVoxelCount, "资产编辑", item.Name));
                    var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
                    var data = SceneComponentTypes.ReadVoxelModel(component);
                    var bindings = materials.ObjectBindings.TryGetValue(segment.ObjectId, out var perObject) ? perObject : materials.Bindings;
                    SceneComponentTypes.WriteVoxelModel(component, data with { MaterialSlots = StageMaterialBindings(bindings) });
                    worlds[item.Id] = segment.World;
                    segmentIds![item.Id] = segment.ObjectId;
                }
            }
            else
            {
                var item = graph.Add(scene.Name, transform: TransformComponent.FromGrid(GridTransform.Identity));
                item.Components.Add(SceneComponentTypes.CreateVoxelModel(string.Empty, singleDocument!.World.OccupiedVoxelCount, "资产编辑", scene.Name));
                var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
                var data = SceneComponentTypes.ReadVoxelModel(component);
                SceneComponentTypes.WriteVoxelModel(component, data with { MaterialSlots = StageMaterialBindings(materials.Bindings) });
                worlds[item.Id] = singleDocument.World;
            }
            var model = scene.Objects[0];
            EndEditorHistory(force: true);
            var previous = new VoxelStage(relativePath, path, materials, hash, _sceneDocument,
                _objectVoxels.CaptureReferences(), _sceneDocumentDirty, _worldLoadFailed,
                _selectedSceneObjectId, _voxelEditTargetId, model.Id, _inspectorLockedObjectId, _pinnedVoxelModelId, _voxelHistory, CaptureAssetStageCamera(), segmentIds,
                multiDocument?.Objects.ToDictionary(segment => segment.ObjectId, segment => segment.Origin, StringComparer.Ordinal));
            _historyBefore = null;
            _historyPriming = true;
            _voxelStage = previous;
            _sceneDocument = scene;
            _objectVoxels.Clear();
            foreach (var (id, world) in worlds) _objectVoxels.SetWorld(id, world);
            _worldLoadFailed = false;
            _sceneDocumentDirty = false;
            _selectedSceneObjectId = model.Id;
            _voxelEditTargetId = model.Id;
            _pinnedVoxelModelId = null;
            _inspectorLockedObjectId = null;
            _selectedVoxel = null;
            _sceneFocusAnimation = null; _renderFocusAnimation = null;
            _renderCameraId = null; _renderOrbitAnchor = null;
            RebuildObjectVoxelWorld();
            // 同一资产再次进入时沿用上次的取景，新的资产才对准包围盒
            if (_assetStageCameras.TryGetValue(relativePath, out var savedCamera)) RestoreAssetStageCamera(savedCamera);
            else FrameCameraOnWorld();
            _assetBrowser.SelectAsset(relativePath);
            _assetBrowser.EditingAssetPath = relativePath;
            SetStatus($"正在编辑体素资产：{relativePath}");
            CommitStageTransition($"打开体素资产：{relativePath}");

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { AbortStageTransition(); SetStatus($"打开体素资产失败：{exception.Message}"); }
    }

    private void ImportExternalVoxelForEditing(string sourcePath)
    {
        if (_project is null) return;
        try
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            VoxDocument? vox = extension == ".vox" ? VoxFormat.ReadDocument(sourcePath) : null;
            var multi = vox is { Instances.Count: > 1 };
            if (vox is { Instances.Count: > 4096 })
                throw new InvalidDataException("VOX 实例数量超过单个原生多对象文档的容量，请先拆分后导入");
            var world = vox is null ? ReadVoxelAsset(sourcePath) : multi ? new VoxelWorld() : VoxFormat.Compose(vox);
            var metadata = new MaterialExchangeMetadata
            { OriginOffset = world.TryGetBounds(out var minimum, out _) ? minimum : VoxelCoord64.Zero };
            if (vox is not null)
            {
                foreach (var source in vox.Materials)
                {
                    if (source.Id is <= 0 or > ushort.MaxValue) continue;
                    var id = $"vox:{source.Id}";
                    var material = MaterialAsset.CreateDefault($"VOX 材质 {source.Id}");
                    if (source.Roughness >= 0F) material.Roughness = source.Roughness;
                    if (source.Metal >= 0F) material.Metallic = source.Metal;
                    if (source.Emissive >= 0F) material.EmissiveScale = source.Emissive;
                    if (source.Specular >= 0F) material.Specular = source.Specular;
                    if (source.RefractionIndex > 0F) material.Ior = source.RefractionIndex;
                    if (source.Transmission >= 0F) material.Opacity = 1F - Math.Clamp(source.Transmission, 0F, 1F);
                    metadata.Definitions[id] = material;
                    metadata.Bindings[(ushort)source.Id] = id;
                }
            }
            var directory = Path.GetDirectoryName(sourcePath)!;
            var fileName = UniqueAssetFileName(directory, Path.GetFileNameWithoutExtension(sourcePath), ".mvmodel");
            var target = Path.Combine(directory, fileName);
            if (multi)
            {
                var segments = vox!.Instances.Select((instance, index) => new LargeWorldFormat.NativeObjectSegment(
                    StableId.New().ToString(), string.IsNullOrWhiteSpace(instance.Name) ? $"对象 {index + 1}" : instance.Name,
                    instance.Offset, VoxFormat.ComposeInstance(vox, instance))).ToArray();
                LargeWorldFormat.WriteObjects(target, segments, metadata);
            }
            else LargeWorldFormat.Write(target, world, metadata);
            AssetMetaStore.EnsureMeta(target);
            var relative = Path.GetRelativePath(_project.AssetsPath, target).Replace('\\', '/');
            HandleAssetChanged(new ProjectAssetChange(relative, relative));
            _assetBrowser.SelectAsset(relative);
            OpenVoxelAssetStage(relative);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        { SetStatus($"导入体素资产失败：{exception.Message}"); }
    }

    private static MaterialSlotBinding[] StageMaterialBindings(IReadOnlyDictionary<ushort, string> bindings) =>
        bindings.Select(pair => new MaterialSlotBinding(pair.Key,
            EmbeddedMaterialPrefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(pair.Value)))).ToArray();

    private void OpenPrefabAssetStage(string relativePath)
    {
        if (_project is null) return;
        if (IsAssetStage)
        {
            var editingPath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
            SetStatus(string.Equals(editingPath, relativePath, StringComparison.OrdinalIgnoreCase)
                ? $"当前已在编辑空间打开该资产：{relativePath}"
                : "请先返回场景，再打开其他资产");
            return;
        }
        BeginStageTransition();
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("预制体路径无效");
            var hash = HashAssetFile(path);
            var asset = PrefabSerializer.Load(path);
            if (!HashAssetFile(path).SequenceEqual(hash))
                throw new InvalidOperationException("预制体读取期间发生变化，请重新打开");
            string? basePath = null;
            var baseHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var effective = asset;
            if (asset.BaseAssetPath.Length > 0)
            {
                basePath = ResolvePrefabPath(_project, asset.BaseAssetPath);
                var cursor = asset;
                while (cursor.BaseAssetPath.Length > 0)
                {
                    var dependencyPath = ResolvePrefabPath(_project, cursor.BaseAssetPath);
                    if (baseHashes.ContainsKey(dependencyPath) || baseHashes.Count >= 8)
                        throw new InvalidDataException("预制体基础链循环或超过深度限制");
                    var dependencyHash = HashAssetFile(dependencyPath);
                    cursor = PrefabSerializer.Load(dependencyPath);
                    if (!HashAssetFile(dependencyPath).SequenceEqual(dependencyHash))
                        throw new InvalidOperationException("基础预制体读取期间发生变化，请重新打开");
                    baseHashes.Add(dependencyPath, dependencyHash);
                }
                _prefabCache.Clear();
                if (!TryGetEffectivePrefab(asset, out effective, out _))
                    throw new InvalidOperationException("无法解析变体的基础预制体");
                if (baseHashes.Any(pair => !HashAssetFile(pair.Key).SequenceEqual(pair.Value)))
                    throw new InvalidOperationException("基础预制体读取期间发生变化，请重新打开");
            }
            var scene = new SceneDocument { Name = effective.Name, Objects = effective.Objects };
            if (scene.Objects.Count(item => item.ParentId is null) != 1 || scene.Objects.All(item => item.Id != asset.PrefabId))
                throw new InvalidDataException("预制体需要一个有效的根对象");
            var editScene = new SceneDocument
            {
                Name = scene.Name,
                Objects = scene.Objects.Select(SceneSerializer.CloneObject).ToList(),
                LayerNames = _sceneDocument?.LayerNames.ToList() ?? ["默认"],
                SortingLayerNames = _sceneDocument?.SortingLayerNames.ToList() ?? ["默认"],
            };
            var maxLayer = editScene.Objects.Count == 0 ? 0 : editScene.Objects.Max(item => item.LayerIndex);
            while (editScene.LayerNames.Count <= maxLayer && editScene.LayerNames.Count < SceneDocument.MaximumLayerCount)
                editScene.LayerNames.Add($"图层 {editScene.LayerNames.Count}");
            var keys = GetObjectModelLoadKeys(_project, editScene).ToDictionary(item => item.ObjectId, item => item.Key);
            var editWorlds = new Dictionary<StableId, VoxelWorld>();
            foreach (var item in editScene.Objects)
            {
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (component is null) continue;
                var data = SceneComponentTypes.ReadVoxelModel(component);
                editWorlds.Add(item.Id, string.IsNullOrWhiteSpace(data.Model)
                    ? EmbeddedVoxelData.HasData(component) ? EmbeddedVoxelData.Read(component) : new VoxelWorld()
                    : keys.TryGetValue(item.Id, out var key) ? ResolveAssetWorld(key, null, default)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}"));
            }
            EndEditorHistory(force: true);
            _prefabStage = new PrefabStage(relativePath, path, asset, effective, basePath, baseHashes, hash, _sceneDocument,
                _objectVoxels.CaptureReferences(), _sceneDocumentDirty, _worldLoadFailed,
                _selectedSceneObjectId, _voxelEditTargetId, _inspectorLockedObjectId, _pinnedVoxelModelId, _voxelHistory, CaptureAssetStageCamera());
            _historyBefore = null; _historyPriming = true;
            _sceneDocument = editScene;
            _objectVoxels.RestoreReferences(editWorlds);
            _worldLoadFailed = false; _sceneDocumentDirty = false;
            _selectedSceneObjectId = asset.PrefabId; _voxelEditTargetId = null; _selectedVoxel = null;
            _inspectorLockedObjectId = null; _pinnedVoxelModelId = null;
            _sceneFocusAnimation = null; _renderFocusAnimation = null;
            _renderCameraId = null; _renderOrbitAnchor = null;
            RebuildObjectVoxelWorld();
            if (_assetStageCameras.TryGetValue(relativePath, out var savedPrefabCamera)) RestoreAssetStageCamera(savedPrefabCamera);
            else FrameCameraOnWorld();
            _assetBrowser.SelectAsset(relativePath);
            _assetBrowser.EditingAssetPath = relativePath;
            CommitStageTransition("进入预制体编辑");
            SetStatus($"正在编辑预制体：{relativePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        { AbortStageTransition(); SetStatus($"打开预制体失败：{exception.Message}"); }
    }

    private bool SavePrefabAssetStage()
    {
        if (_prefabStage is not { } stage || _project is null) return false;
        if (!_sceneDocumentDirty) { SetStatus("预制体没有未保存的修改"); return true; }
        try
        {
            if (_worldLoadFailed) throw new InvalidOperationException("预制体中的体素数据未完整加载");
            if (_sceneDocument is null || _sceneDocument.Objects.Count(item => item.ParentId is null) != 1 ||
                _sceneDocument.Objects.All(item => item.Id != stage.Original.PrefabId || item.ParentId is not null))
                throw new InvalidOperationException("预制体必须保留原来的唯一根对象");
            if (!HashAssetFile(stage.AbsolutePath).SequenceEqual(stage.OriginalHash))
                throw new InvalidOperationException("资产文件已在外部修改，请先处理冲突");
            if (stage.BaseHashes.Any(pair => !HashAssetFile(pair.Key).SequenceEqual(pair.Value)))
                throw new InvalidOperationException("基础预制体链已在外部修改，请返回后重新打开");
            SaveObjectVoxelWorlds(_project);
            var snapshot = SceneSerializer.LoadFromJson(SceneSerializer.ToJson(_sceneDocument));
            // 修订基线随资产保存，不依赖可清理的 Library 缓存，其他场景以后可据此合并。
            var before = PrefabSerializer.Load(stage.AbsolutePath);
            var history = new List<PrefabRevision>(before.Revisions)
            {
                new() { Revision = before.Revision, Name = before.Name, Objects = before.Objects },
            };
            var overrides = new Dictionary<string, string>(before.OverrideKinds, StringComparer.Ordinal);
            if (stage.BasePath is not null)
            {
                var compareScene = new SceneDocument { Objects = snapshot.Objects.Select(SceneSerializer.CloneObject).ToList() };
                var root = compareScene.Objects.First(item => item.Id == before.PrefabId);
                root.Prefab = new PrefabInstanceData { SourceId = stage.Effective.PrefabId,
                    Links = stage.Effective.Objects.ToDictionary(item => item.Id.Value.ToString(), item => item.Id.Value.ToString(), StringComparer.Ordinal) };
                foreach (var change in PrefabOverrides.Compute(compareScene, root, stage.Effective))
                {
                    if (change.Kind is PrefabOverrides.Kind.Added or PrefabOverrides.Kind.Missing or PrefabOverrides.Kind.SourceDrift) continue;
                    var key = change.ObjectId.Value.ToString();
                    var kinds = overrides.TryGetValue(key, out var value)
                        ? value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
                    kinds.Add(change.Kind.ToString());
                    overrides[key] = string.Join(",", kinds.Order(StringComparer.Ordinal));
                }
            }
            var baseAsset = stage.BasePath is null ? null : PrefabSerializer.Load(stage.BasePath);
            var asset = new PrefabAsset { PrefabId = before.PrefabId, FormatVersion = before.FormatVersion,
                Revision = checked(before.Revision + 1), Revisions = history,
                BaseAssetPath = before.BaseAssetPath, BasePrefabId = baseAsset?.PrefabId ?? before.BasePrefabId,
                BaseRevision = baseAsset?.Revision ?? before.BaseRevision,
                BaseLinks = new Dictionary<string, string>(stage.Effective.BaseLinks, StringComparer.Ordinal), OverrideKinds = overrides,
                Name = snapshot.Objects.First(item => item.Id == before.PrefabId).Name, Objects = snapshot.Objects };
            PrefabSerializer.Save(stage.AbsolutePath, asset);
            _prefabCache[stage.AbsolutePath] = asset;
            _prefabStage = stage with { OriginalHash = HashAssetFile(stage.AbsolutePath), Saved = true };
            _sceneDocumentDirty = false;
            SetStatus($"已保存预制体：{stage.RelativePath}；实例同步尚未完成");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { SetStatus($"保存预制体失败：{exception.Message}"); return false; }
    }

    private void ReturnFromPrefabAssetStage()
    {
        if (_prefabStage is not { } stage) return;
        BeginStageTransition();
        _historyBefore = null; _historyPriming = true;
        _sceneDocument = stage.Scene; _objectVoxels.RestoreReferences(stage.Worlds);
        _sceneDocumentDirty = stage.SceneDirty; _worldLoadFailed = stage.LoadFailed;
        _selectedSceneObjectId = stage.Selection; _voxelEditTargetId = stage.EditTarget; _selectedVoxel = null;
        _inspectorLockedObjectId = stage.InspectorLock; _pinnedVoxelModelId = stage.Pinned;
        _prefabStage = null;
        _assetBrowser.EditingAssetPath = string.Empty;
        _assetStageCameras[stage.RelativePath] = CaptureAssetStageCamera();
        var updated = stage.Saved ? SyncPendingPrefabRevisions(stage.AbsolutePath) : 0;
        RebuildObjectVoxelWorld(); RestoreAssetStageCamera(stage.Camera);
        SetStatus(stage.Saved ? $"已返回场景，更新 {updated} 个实例；已补入可同步的新对象；受覆盖保护的删除、嵌套结构及其他场景同步尚未完成" : "已返回场景");
        CommitStageTransition("返回主场景");
    }

    private bool SaveVoxelAssetStage()
    {
        if (_voxelStage is not { } stage) return false;
        if (!_sceneDocumentDirty) { SetStatus("体素资产没有未保存的修改"); return true; }
        try
        {
            if (_sceneDocument is null) throw new InvalidOperationException("资产编辑空间不可用");
            if (!HashAssetFile(stage.AbsolutePath).SequenceEqual(stage.OriginalHash))
                throw new InvalidOperationException("资产文件已在外部修改，请先处理冲突");
            MaterialExchangeMetadata materials;
            if (stage.SegmentIds is null)
            {
                if (_sceneDocument.Objects.Count != 1 || _sceneDocument.Objects[0].Id != stage.ModelId ||
                    !_objectVoxels.TryGetWorld(stage.ModelId, out var world))
                    throw new InvalidOperationException("单模型体素数据需要保留唯一的模型对象");
                var item = _sceneDocument.Objects[0];
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel)
                    ?? throw new InvalidOperationException("体素模型组件不存在");
                ValidateVoxelSegmentForSave(item, component);
                if (item.Transform.Grid.Position != VoxelCoord64.Zero)
                    throw new InvalidOperationException("单模型体素资产不保存对象原点；请将对象位置还原为零后保存");
                if (!string.Equals(item.Name, _sceneDocument.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("单模型体素资产的对象名称由文件名决定；请还原名称或在资产面板重命名文件");
                var bindings = BuildVoxelStageMaterials(stage, component);
                materials = new MaterialExchangeMetadata
                {
                    OriginOffset = world.TryGetBounds(out var minimum, out _) ? minimum : VoxelCoord64.Zero,
                    Bindings = bindings.Bindings, ObjectBindings = bindings.ObjectBindings, Definitions = bindings.Definitions,
                };
                LargeWorldFormat.Write(stage.AbsolutePath, world, materials);
            }
            else
            {
                var segments = new List<LargeWorldFormat.NativeObjectSegment>();
                materials = new MaterialExchangeMetadata();
                foreach (var entry in stage.Materials.Definitions) materials.Definitions.Add(entry.Key, entry.Value);
                foreach (var entry in stage.Materials.Bindings) materials.Bindings.Add(entry.Key, entry.Value);
                var knownIds = stage.SegmentIds.Values.ToHashSet(StringComparer.Ordinal);
                foreach (var entry in stage.Materials.ObjectBindings.Where(entry => !knownIds.Contains(entry.Key)))
                    materials.ObjectBindings.Add(entry.Key, new Dictionary<ushort, string>(entry.Value));
                var ids = new Dictionary<StableId, string>(stage.SegmentIds);
                foreach (var item in _sceneDocument.Objects)
                {
                    var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                    if (component is null || !_objectVoxels.TryGetWorld(item.Id, out var world))
                        throw new InvalidOperationException("多对象体素文档只支持体素模型对象");
                    ValidateVoxelSegmentForSave(item, component);
                    if (!ids.TryGetValue(item.Id, out var id)) ids[item.Id] = id = item.Id.ToString();
                    var objectMaterials = BuildVoxelStageMaterials(stage, component);
                    foreach (var definition in objectMaterials.Definitions) materials.Definitions[definition.Key] = definition.Value;
                    materials.ObjectBindings[id] = new Dictionary<ushort, string>(objectMaterials.Bindings);
                    segments.Add(new LargeWorldFormat.NativeObjectSegment(id, item.Name, GetSceneModelOrigin(item), world));
                }
                if (segments.Count == 0) throw new InvalidOperationException("至少保留一个体素模型对象");
                if (_project is not null)
                {
                    var remainingIds = segments.Select(segment => segment.ObjectId).ToHashSet(StringComparer.Ordinal);
                    var removedIds = knownIds.Where(id => !remainingIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
                    if (removedIds.Count > 0)
                    {
                        var affected = FindRemovedVoxelSegmentReferences(stage, removedIds);
                        if (affected.Count > 0)
                            throw new InvalidOperationException($"场景或预制体仍引用已删除的片段（{string.Join("、", affected)}）；请先还原片段或处理这些引用");
                    }
                }
                LargeWorldFormat.WriteObjects(stage.AbsolutePath, segments, materials);
                stage = stage with { SegmentIds = ids };
            }
            _voxelStage = stage with { OriginalHash = HashAssetFile(stage.AbsolutePath), Materials = materials, Saved = true };
            _voxelAssetsChangedDuringSession.Add(stage.AbsolutePath);
            _sceneDocumentDirty = false;
            SetStatus($"已保存体素资产：{stage.RelativePath}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException)
        { SetStatus($"保存体素资产失败：{exception.Message}"); return false; }
    }

    private IReadOnlyList<string> FindRemovedVoxelSegmentReferences(VoxelStage stage, IReadOnlySet<string> removedIds)
    {
        var matches = new List<string>();
        void Find(SceneDocument scene, string source)
        {
            foreach (var item in AssetReferences.FindReferencingObjects(scene, stage.RelativePath))
            {
                if (!item.Components.Where(SceneComponentTypes.IsVoxelModel).Any(component =>
                    removedIds.Contains(SceneComponentTypes.ReadVoxelModel(component).SourceSegment))) continue;
                matches.Add($"{source}：{item.Name}");
                if (matches.Count >= 4) return;
            }
        }
        if (stage.Scene is not null) Find(stage.Scene, "原场景");
        if (_project is null || matches.Count >= 4) return matches;
        var activeScene = Path.GetFullPath(Path.Combine(_project.AssetsPath,
            _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var path in Directory.EnumerateFiles(_project.AssetsPath, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(path, activeScene, StringComparison.OrdinalIgnoreCase)) continue;
            if (extension.Equals(".mvox", StringComparison.OrdinalIgnoreCase))
                Find(SceneSerializer.Load(path), Path.GetRelativePath(_project.AssetsPath, path));
            else if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                Find(new SceneDocument { Objects = PrefabSerializer.Load(path).Objects }, Path.GetRelativePath(_project.AssetsPath, path));
            if (matches.Count >= 4) break;
        }
        return matches;
    }

    private static void ValidateVoxelSegmentForSave(SceneObject item, ComponentRecord component)
    {
        var transform = item.Transform;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        if (item.ParentId is not null || item.Prefab is not null || !item.Visible || item.Locked || item.LayerIndex != 0 ||
            item.Components.Any(value => !SceneComponentTypes.IsTransform(value) && !SceneComponentTypes.IsVoxelModel(value)) ||
            item.Components.Count(SceneComponentTypes.IsVoxelModel) != 1 || !component.Enabled ||
            transform.Mode != TransformMode.Grid || !transform.Grid.Rotation.Equals(OrthogonalRotation.Identity) ||
            transform.ScaleX != 1F || transform.ScaleY != 1F || transform.ScaleZ != 1F ||
            data.ScaleX != 1F || data.ScaleY != 1F || data.ScaleZ != 1F ||
            data.MirrorX || data.MirrorY || data.MirrorZ ||
            data.SortingLayer != "默认" || data.SortingOrder != 0)
            throw new InvalidOperationException($"“{item.Name}”含有原生对象片段无法保存的层级、变换或组件；请还原这些属性后保存");
    }

    private MaterialExchangeMetadata BuildVoxelStageMaterials(VoxelStage stage, ComponentRecord component)
    {
        var result = new MaterialExchangeMetadata();
        foreach (var entry in stage.Materials.Definitions) result.Definitions.Add(entry.Key, entry.Value);
        foreach (var entry in stage.Materials.ObjectBindings)
            result.ObjectBindings.Add(entry.Key, new Dictionary<ushort, string>(entry.Value));
        foreach (var binding in SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
        {
            if (binding.Slot is <= 0 or > ushort.MaxValue) continue;
            var path = binding.MaterialAsset;
            string key;
            if (path.StartsWith(EmbeddedMaterialPrefix, StringComparison.Ordinal))
            {
                try { key = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(path[EmbeddedMaterialPrefix.Length..])); }
                catch (FormatException exception) { throw new InvalidDataException("内嵌材质标识无效", exception); }
                if (!result.Definitions.ContainsKey(key)) throw new InvalidDataException($"内嵌材质缺失：{key}");
            }
            else
            {
                var material = FindMaterialAsset(path) ?? throw new InvalidDataException($"材质资产缺失：{path}");
                var baseKey = "project:" + path;
                key = baseKey;
                var suffix = 1;
                var serialized = System.Text.Json.JsonSerializer.Serialize(material);
                while (result.Definitions.TryGetValue(key, out var existing) &&
                       !string.Equals(System.Text.Json.JsonSerializer.Serialize(existing), serialized, StringComparison.Ordinal))
                    key = $"{baseKey}#{suffix++}";
                result.Definitions[key] = material;
            }
            result.Bindings[(ushort)binding.Slot] = key;
        }
        result.Validate();
        return result;
    }

    private void ReturnFromVoxelAssetStage(bool saveChanges)
    {
        if (_voxelStage is not { } stage || (saveChanges && _sceneDocumentDirty && !SaveVoxelAssetStage())) return;
        BeginStageTransition();
        stage = _voxelStage!;
        _historyBefore = null;
        _historyPriming = true;
        _sceneDocument = stage.Scene;
        _objectVoxels.RestoreReferences(stage.Worlds);
        _sceneDocumentDirty = stage.SceneDirty;
        _worldLoadFailed = stage.LoadFailed;
        _selectedSceneObjectId = stage.Selection;
        _voxelEditTargetId = stage.EditTarget;
        _inspectorLockedObjectId = stage.InspectorLock;
        _pinnedVoxelModelId = stage.Pinned;
        _selectedVoxel = null;
        _voxelStage = null;
        _assetBrowser.EditingAssetPath = string.Empty;
        _assetStageCameras[stage.RelativePath] = CaptureAssetStageCamera();
        RestoreAssetStageCamera(stage.Camera);
        try { if (_project is not null) RefreshReferencedVoxelAsset(_project, stage.AbsolutePath, stage.Saved ? stage.OriginalSegmentOrigins : null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            // 场景仍保留原有内存数据；未能确认共享资产状态前不能覆盖保存。
            _worldLoadFailed = true;
            SetStatus($"返回场景，但刷新引用失败，已阻止覆盖保存：{exception.Message}");
            AbortStageTransition();
            return;
        }
        RebuildObjectVoxelWorld();
        SetStatus("已返回场景");
        CommitStageTransition("返回主场景");
    }

    /// <summary>
    /// 把当前编辑的资产另存为副本并切换到副本继续编辑：未保存的修改先写回原资产，
    /// 副本与原资产同目录同扩展名，切换后保存签名与路径基线一并更新
    /// </summary>
    private void SaveAssetStageCopy()
    {
        if (_project is null) return;
        var stagePath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
        if (stagePath is null) return;
        if (_sceneDocumentDirty && !SaveAssetStage()) return;
        var result = ProjectAssetOperations.Duplicate(_project, stagePath);
        if (!result.Success) { SetStatus(result.Message); return; }
        HandleAssetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
        UpdateEditingAssetPath(stagePath, result.RelativePath);
        _assetBrowser.SelectAsset(result.RelativePath);
        _assetBrowser.EditingAssetPath = result.RelativePath;
        SetStatus($"已另存为副本并切换编辑：{result.RelativePath}");
    }

    /// <summary>
    /// 退出前先离开资产编辑空间：有未保存修改时弹出保存或放弃的选择，
    /// 没有修改时直接返回原场景，供退出、关闭项目和切换项目等入口复用
    /// </summary>
    private void RequestAssetStageExit()
    {
        if (!IsAssetStage) return;
        if (_sceneDocumentDirty)
        {
            _voxelStageExitRequested = true;
            SetStatus("资产有未保存的修改，请选择保存或放弃后返回场景");
            return;
        }

        if (_prefabStage is not null) ReturnFromPrefabAssetStage(); else ReturnFromVoxelAssetStage(false);
    }

    private void DrawVoxelAssetStageBar()
    {
        if (!IsAssetStage) return;
        var stagePath = _voxelStage?.RelativePath ?? _prefabStage!.RelativePath;
        void ReturnStage() { if (_prefabStage is not null) ReturnFromPrefabAssetStage(); else ReturnFromVoxelAssetStage(false); }
        if (ImGui.SmallButton("返回场景"))
        {
            if (_sceneDocumentDirty) _voxelStageExitRequested = true;
            else ReturnStage();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted($"{(_prefabStage is null ? "体素资产" : "预制体")}：{Path.GetFileName(stagePath)}{(_sceneDocumentDirty ? " *" : "")}");
        var stageObjects = _sceneDocument?.Objects.Count ?? 0;
        ImGui.SameLine();
        ImGui.TextDisabled($"{_objectVoxels.TotalOccupiedVoxelCount} 体素 · {stageObjects} 对象");
        ImGui.SameLine();
        ImGui.BeginDisabled(!_sceneDocumentDirty);
        if (ImGui.SmallButton("保存资产")) SaveAssetStage();
        ImGui.EndDisabled();
        if (!_sceneDocumentDirty && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("没有未保存的修改");
        ImGui.SameLine();
        if (ImGui.SmallButton("另存副本")) SaveAssetStageCopy();
        ImGui.SameLine();
        if (ImGui.SmallButton("定位")) _assetBrowser.SelectAsset(stagePath);
        if (_voxelStageExitRequested)
        {
            ImGui.OpenPopup("返回场景##VoxelStageExit");
            _voxelStageExitRequested = false;
        }
        if (!CenteredModal.Begin("返回场景##VoxelStageExit", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("资产有未保存的修改。返回场景前如何处理？");
        if (ImGui.Button("保存并返回"))
        {
            if (SaveAssetStage()) { ImGui.CloseCurrentPopup(); ReturnStage(); }
        }
        ImGui.SameLine();
        if (ImGui.Button("放弃修改并返回")) { ImGui.CloseCurrentPopup(); ReturnStage(); }
        ImGui.SameLine();
        if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}

