using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器主窗口的预制体集成：层级右键菜单、检查器区块、资产实例化与资产缓存
/// </summary>
internal partial class ImGuiEditorWindow
{
    /// <summary>
    /// 预制体资产缓存，键为绝对路径；应用或重新保存资产时移除对应条目
    /// </summary>
    private static string ResolvePrefabPath(ProjectHandle project, string relativePath)
    {
        var relative = relativePath.Replace('\\', '/');
        if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.AssetsPath)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("预制体路径不在项目资产内");
        return path;
    }

    private readonly Dictionary<string, PrefabAsset> _prefabCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取并缓存预制体资产，同一文件在一次会话内只读一次磁盘
    /// </summary>
    private PrefabAsset LoadPrefabCached(string absolutePath)
    {
        if (_prefabCache.TryGetValue(absolutePath, out var cached))
        {
            return cached;
        }

        var asset = PrefabSerializer.Load(absolutePath);
        _prefabCache[absolutePath] = asset;
        return asset;
    }

    /// <summary>
    /// 把当前选中的场景对象创建为预制体：写资产文件并把原对象转为实例
    /// </summary>
    private void CreatePrefabFromSelection()
    {
        if (_project is null || _sceneDocument is null)
        {
            return;
        }

        var selected = _sceneDocument.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId);
        if (selected is null)
        {
            SetStatus("请先在层级中选择要创建为预制体的对象");
            return;
        }

        if (selected.Prefab is not null)
        {
            SetStatus("该对象已经是预制体实例");
            return;
        }

        try
        {
            // 场景体素保存在内存中，先把当前对象数据写入副本，再生成资产。
            var originals = PrefabSystem.CollectSubtree(_sceneDocument, selected);
            var staged = new SceneDocument { Objects = originals.Select(SceneSerializer.CloneObject).ToList() };
            foreach (var item in staged.Objects)
            {
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (component is not null && string.IsNullOrWhiteSpace(SceneComponentTypes.ReadVoxelModel(component).Model) &&
                    _objectVoxels.TryGetWorld(item.Id, out var world))
                {
                    EmbeddedVoxelData.Write(component, world, BakeLodFor(world));
                }
            }
            var stagedRoot = staged.Objects.First(item => item.Id == selected.Id);
            var asset = PrefabSystem.CreateAssetFromScene(staged, stagedRoot, selected.Name);
            var fileBase = SanitizeFileName(selected.Name);
            var relativePath = $"Assets/Prefabs/{fileBase}.prefab";
            var absolutePath = Path.Combine(_project.AssetsPath, "Prefabs", $"{fileBase}.prefab");
            var serial = 1;
            while (File.Exists(absolutePath))
            {
                relativePath = $"Assets/Prefabs/{fileBase}_{serial}.prefab";
                absolutePath = Path.Combine(_project.AssetsPath, "Prefabs", $"{fileBase}_{serial}.prefab");
                serial++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            PrefabSerializer.Save(absolutePath, asset);
            var originalsById = originals.ToDictionary(item => item.Id);
            foreach (var item in staged.Objects)
            {
                var stagedComponent = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                var originalComponent = originalsById[item.Id].Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (stagedComponent is not null && originalComponent is not null &&
                    string.IsNullOrWhiteSpace(SceneComponentTypes.ReadVoxelModel(stagedComponent).Model))
                    originalComponent.Data = stagedComponent.Data.Clone();
            }
            PrefabSystem.MakeInstanceFromScene(_sceneDocument, selected, relativePath, asset);
            MarkSceneDocumentDirty();
            SetStatus($"已创建预制体：{relativePath}，原对象已转为实例");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("创建预制体失败", exception);
            SetStatus($"创建预制体失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 实例化资产浏览器中选中的预制体文件到场景根
    /// </summary>
    private void InstantiatePrefabAsset(string relativePath)
    {
        if (_project is null || _sceneDocument is null)
        {
            SetStatus("请先打开项目再实例化预制体");
            return;
        }

        try
        {
            var absolutePath = ResolvePrefabPath(_project, relativePath);
            var asset = LoadPrefabCached(absolutePath);
            if (!TryGetEffectivePrefab(asset, out var effective, out _))
                throw new InvalidDataException("无法解析预制体的基础资产");
            var staged = new SceneDocument { Name = effective.Name };
            var instance = PrefabSystem.Instantiate(effective, relativePath, staged);
            var keys = GetObjectModelLoadKeys(_project, staged).ToDictionary(item => item.ObjectId, item => item.Key);
            var worlds = new Dictionary<StableId, VoxelWorld>();
            var chains = new Dictionary<StableId, ModelLodChain>();
            foreach (var item in staged.Objects)
            {
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (component is null) continue;
                var data = SceneComponentTypes.ReadVoxelModel(component);
                if (string.IsNullOrWhiteSpace(data.Model))
                {
                    if (EmbeddedVoxelData.HasData(component))
                    {
                        var (embedded, chain) = EmbeddedVoxelData.ReadWithLod(component);
                        worlds.Add(item.Id, embedded);
                        if (chain.LevelCount > 1) chains[item.Id] = chain;
                    }
                    else
                    {
                        worlds.Add(item.Id, new VoxelWorld());
                    }
                }
                else
                {
                    worlds.Add(item.Id, keys.TryGetValue(item.Id, out var key) ? ResolveAssetWorld(key, null, default)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}"));
                }
            }
            var combined = _objectVoxels.CaptureReferences();
            foreach (var (id, world) in worlds)
                if (!combined.TryAdd(id, world)) throw new InvalidDataException("预制体对象身份与当前场景冲突");
            var existingIds = _sceneDocument.Objects.Select(item => item.Id).ToHashSet();
            if (staged.Objects.Any(item => existingIds.Contains(item.Id)))
                throw new InvalidDataException("预制体对象身份与当前场景冲突");
            _sceneDocument.Objects.AddRange(staged.Objects);
            _objectVoxels.RestoreReferences(combined);
            foreach (var (id, chain) in chains) _objectLodChains[id] = chain;
            RebuildObjectVoxelWorld();
            _selectedSceneObjectId = instance.Id;
            _selectedVoxel = null;
            MarkSceneDocumentDirty();
            SetStatus($"已实例化预制体：{Path.GetFileName(relativePath)}（{effective.Objects.Count} 个对象）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            MVoxelLog.Error("实例化预制体失败", exception);
            SetStatus($"实例化预制体失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 解析资产的生效内容：普通资产原样返回；变体沿基础链逐层合并（防环，深度上限 8）
    /// </summary>
    private bool TryGetEffectivePrefab(PrefabAsset asset, out PrefabAsset effective, out IReadOnlyList<string> conflicts)
    {
        effective = asset;
        conflicts = Array.Empty<string>();
        if (asset.BaseAssetPath.Length == 0 || _project is null)
        {
            return true;
        }

        var chain = new List<PrefabAsset> { asset };
        var visited = new HashSet<StableId> { asset.PrefabId };
        var current = asset;
        while (current.BaseAssetPath.Length > 0)
        {
            var basePath = ResolvePrefabPath(_project, current.BaseAssetPath);
            if (!File.Exists(basePath))
            {
                return false; // 基础资产缺失，按原内容使用
            }

            PrefabAsset baseAsset;
            try
            {
                baseAsset = LoadPrefabCached(basePath);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                return false;
            }

            if (!visited.Add(baseAsset.PrefabId) || chain.Count > 8)
            {
                return false; // 基础链成环或过深
            }

            chain.Add(baseAsset);
            current = baseAsset;
        }

        var allConflicts = new List<string>();
        effective = chain[^1];
        for (var index = chain.Count - 2; index >= 0; index--)
        {
            var (merged, list, _) = PrefabSystem.ComputeEffectiveVariant(chain[index], effective);
            allConflicts.AddRange(list);
            effective = merged;
        }

        conflicts = allConflicts;
        return true;
    }

    /// <summary>
    /// 把当前选中的实例存为变体资产：记录基础链与覆盖类型，实例改指向变体
    /// </summary>
    private void CreateVariantFromSelectedInstance()
    {
        if (_project is null || _sceneDocument is null)
        {
            return;
        }

        if (!TryGetSelectedPrefabInstance(out var instanceRoot, out var baseAsset, out _))
        {
            return;
        }

        try
        {
            var variant = PrefabSystem.CreateVariantFromInstance(_sceneDocument, instanceRoot, baseAsset, instanceRoot.Name);
            var fileBase = SanitizeFileName($"{instanceRoot.Name}变体");
            var relativePath = $"Assets/Prefabs/{fileBase}.prefab";
            var absolutePath = Path.Combine(_project.AssetsPath, "Prefabs", $"{fileBase}.prefab");
            var serial = 1;
            while (File.Exists(absolutePath))
            {
                relativePath = $"Assets/Prefabs/{fileBase}_{serial}.prefab";
                absolutePath = Path.Combine(_project.AssetsPath, "Prefabs", $"{fileBase}_{serial}.prefab");
                serial++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            PrefabSerializer.Save(absolutePath, variant);
            instanceRoot.Prefab!.AssetPath = relativePath;
            instanceRoot.Prefab.SourceId = variant.PrefabId;
            _prefabCache[absolutePath] = variant;
            MarkSceneDocumentDirty();
            SetStatus($"已存为变体：{relativePath}（基础：{variant.BaseAssetPath}）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("存为变体失败", exception);
            SetStatus($"存为变体失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 给选中变体的全部实例拉取基础更新：生效内容写回变体资产并刷新实例
    /// </summary>
    private void PullBaseUpdateForSelectedVariant()
    {
        if (_project is null || _sceneDocument is null)
        {
            return;
        }

        if (!TryGetSelectedPrefabInstance(out var instanceRoot, out var asset, out var relativePath))
        {
            return;
        }

        if (asset.BaseAssetPath.Length == 0)
        {
            SetStatus("该实例来自普通预制体，没有基础可拉取");
            return;
        }

        try
        {
            var basePath = ResolvePrefabPath(_project, asset.BaseAssetPath);
            var assetPath = ResolvePrefabPath(_project, relativePath);
            var baseAsset = PrefabSerializer.Load(basePath);
            if (!TryGetEffectivePrefab(baseAsset, out var effectiveBase, out _))
                throw new InvalidDataException("无法解析基础预制体链");
            var (effective, conflicts, changed) = PrefabSystem.ComputeEffectiveVariant(asset, effectiveBase);
            if (!changed) { SetStatus("变体已使用最新基础修订"); return; }
            var history = new List<PrefabRevision>(asset.Revisions)
            { new() { Revision = asset.Revision, Name = asset.Name, Objects = asset.Objects } };
            var updated = new PrefabAsset
            {
                FormatVersion = asset.FormatVersion, PrefabId = asset.PrefabId, Name = asset.Name,
                Revision = checked(asset.Revision + 1), Revisions = history,
                BaseAssetPath = asset.BaseAssetPath, BasePrefabId = baseAsset.PrefabId, BaseRevision = baseAsset.Revision,
                BaseLinks = new Dictionary<string, string>(effective.BaseLinks, StringComparer.Ordinal),
                OverrideKinds = new Dictionary<string, string>(asset.OverrideKinds, StringComparer.Ordinal),
                Objects = effective.Objects,
            };
            PrefabSerializer.Save(assetPath, updated);
            _prefabCache[assetPath] = updated;
            var synced = SyncPendingPrefabRevisions(assetPath);
            SetStatus($"已拉取基础更新，尝试同步 {synced} 个实例" +
                (conflicts.Count == 0 ? string.Empty : $"；保留 {conflicts.Count} 处变体覆盖"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            MVoxelLog.Error("拉取基础更新失败", exception);
            SetStatus($"拉取基础更新失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 把选中的实例完全还原为资产内容，丢弃实例侧修改
    /// </summary>
    private void RevertSelectedPrefabInstance()
    {
        if (!TryGetSelectedPrefabInstance(out var instanceRoot, out var asset, out var relativePath))
        {
            return;
        }

        try
        {
            if (!TryGetEffectivePrefab(asset, out var effective, out _))
            {
                SetStatus($"预制体生效内容不可用，无法还原：{relativePath}");
                return;
            }

            var stagedRoot = SceneSerializer.CloneObject(instanceRoot);
            var staged = new SceneDocument { Objects = [stagedRoot] };
            if (!PrefabSystem.RefreshInstance(staged, stagedRoot, effective, preserveRootTransform: false))
                throw new InvalidDataException("所选对象不是预制体实例");
            var keys = GetObjectModelLoadKeys(_project!, staged).ToDictionary(item => item.ObjectId, item => item.Key);
            var replacementWorlds = new Dictionary<StableId, VoxelWorld>();
            var replacementChains = new Dictionary<StableId, ModelLodChain>();
            foreach (var item in staged.Objects)
            {
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (component is null) continue;
                var data = SceneComponentTypes.ReadVoxelModel(component);
                if (string.IsNullOrWhiteSpace(data.Model))
                {
                    if (EmbeddedVoxelData.HasData(component))
                    {
                        var (embedded, chain) = EmbeddedVoxelData.ReadWithLod(component);
                        replacementWorlds[item.Id] = embedded;
                        if (chain.LevelCount > 1) replacementChains[item.Id] = chain;
                    }
                    else
                    {
                        replacementWorlds[item.Id] = new VoxelWorld();
                    }
                }
                else
                {
                    replacementWorlds[item.Id] = keys.TryGetValue(item.Id, out var key) ? ResolveAssetWorld(key, null, default)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}");
                }
            }
            var oldIds = PrefabSystem.CollectSubtree(_sceneDocument!, instanceRoot).Select(item => item.Id).ToHashSet();
            var otherIds = _sceneDocument!.Objects.Where(item => !oldIds.Contains(item.Id)).Select(item => item.Id).ToHashSet();
            if (staged.Objects.Any(item => otherIds.Contains(item.Id))) throw new InvalidDataException("还原对象身份与场景冲突");
            var worlds = _objectVoxels.CaptureReferences();
            foreach (var id in oldIds) worlds.Remove(id);
            foreach (var (id, world) in replacementWorlds) worlds.Add(id, world);
            _sceneDocument.Objects.RemoveAll(item => oldIds.Contains(item.Id));
            _sceneDocument.Objects.AddRange(staged.Objects);
            _objectVoxels.RestoreReferences(worlds);
            foreach (var id in oldIds) _objectLodChains.Remove(id);
            foreach (var (id, chain) in replacementChains) _objectLodChains[id] = chain;
            _selectedSceneObjectId = instanceRoot.Id;
            _voxelEditTargetId = null;
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            SetStatus($"已还原实例：{relativePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetStatus($"预制体资产不可用，无法还原：{relativePath}（{exception.Message}）");
        }
    }

    /// <summary>
    /// 把选中的实例内容应用到预制体资产，并自动更新同源的其他实例
    /// </summary>
    private void ApplySelectedPrefabToAsset()
    {
        if (!TryGetSelectedPrefabInstance(out var instanceRoot, out var before, out var relativePath)) return;
        try
        {
            if (before.BaseAssetPath.Length > 0) throw new InvalidOperationException("变体请在对应的变体工具中应用");
            var subtree = PrefabSystem.CollectSubtree(_sceneDocument!, instanceRoot);
            if (subtree.Any(item => item.Id != instanceRoot.Id && item.Prefab is not null))
                throw new InvalidOperationException("包含嵌套预制体的实例请在独立编辑空间中修改");
            var links = instanceRoot.Prefab!.Links;
            if (instanceRoot.Prefab.SourceId != before.PrefabId ||
                instanceRoot.Prefab.SourceRevision != before.Revision)
                throw new InvalidOperationException("实例尚未与最新预制体同步，请先处理来源更新");
            var oldIds = before.Objects.Select(item => item.Id).ToHashSet();
            var idMap = new Dictionary<StableId, StableId>();
            foreach (var item in subtree)
            {
                var source = links.TryGetValue(item.Id.Value.ToString(), out var value) &&
                    StableId.TryParse(value, out var parsed) && oldIds.Contains(parsed) ? parsed : StableId.New();
                if (idMap.Values.Contains(source)) throw new InvalidDataException("实例的预制体链接重复");
                idMap[item.Id] = source;
            }
            if (idMap[instanceRoot.Id] != before.PrefabId) throw new InvalidDataException("预制体根链接无效");
            var copy = subtree.Select(SceneSerializer.CloneObject).ToList();
            foreach (var item in copy)
            {
                var originalId = item.Id;
                var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                if (component is not null && string.IsNullOrWhiteSpace(SceneComponentTypes.ReadVoxelModel(component).Model) &&
                    _objectVoxels.TryGetWorld(originalId, out var world))
                {
                    EmbeddedVoxelData.Write(component, world, BakeLodFor(world));
                }
                item.Id = idMap[originalId];
                item.ParentId = originalId == instanceRoot.Id ? null : item.ParentId is { } parent && idMap.TryGetValue(parent, out var mapped) ? mapped : null;
                if (originalId == instanceRoot.Id) item.Prefab = null;
            }
            var history = new List<PrefabRevision>(before.Revisions)
            { new() { Revision = before.Revision, Name = before.Name, Objects = before.Objects } };
            var asset = new PrefabAsset { PrefabId = before.PrefabId, FormatVersion = before.FormatVersion,
                Revision = checked(before.Revision + 1), Revisions = history, Name = instanceRoot.Name, Objects = copy };
            var absolutePath = ResolvePrefabPath(_project!, relativePath);
            PrefabSerializer.Save(absolutePath, asset);
            _prefabCache[absolutePath] = asset;
            links.Clear();
            foreach (var pair in idMap) links[pair.Key.Value.ToString()] = pair.Value.Value.ToString();
            instanceRoot.Prefab.SourceRevision = asset.Revision;
            MarkSceneDocumentDirty();
            var updated = SyncPendingPrefabRevisions(absolutePath);
            SetStatus($"已应用预制体：{relativePath}；尝试更新 {updated} 个其他实例");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            MVoxelLog.Error("应用预制体失败", exception);
            SetStatus($"应用预制体失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 取当前选中对象作为预制体实例根，并加载其来源资产
    /// </summary>
    private bool TryGetSelectedPrefabInstance(out SceneObject instanceRoot, out PrefabAsset asset,
        out string relativePath)
    {
        instanceRoot = null!;
        asset = null!;
        relativePath = string.Empty;
        if (_project is null || _sceneDocument is null || _selectedSceneObjectId is null)
        {
            SetStatus("请先在层级中选择对象");
            return false;
        }

        var selected = _sceneDocument.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId);
        if (selected?.Prefab is null)
        {
            SetStatus("所选对象不是预制体实例");
            return false;
        }

        relativePath = selected.Prefab.AssetPath;
        var absolutePath = ResolvePrefabPath(_project, relativePath);
        if (!File.Exists(absolutePath))
        {
            SetStatus($"预制体资产缺失，实例保持当前内容：{relativePath}");
            return false;
        }

        try
        {
            asset = LoadPrefabCached(absolutePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            SetStatus($"预制体资产已损坏，实例保持当前内容：{relativePath}");
            return false;
        }

        instanceRoot = selected;
        return true;
    }

    /// <summary>
    /// 绘制层级对象的右键菜单，按对象是否为实例提供对应操作
    /// </summary>
    private void DrawSceneObjectContextMenu(SceneObject sceneObject)
    {
        if (ImGui.MenuItem("聚焦对象")) FocusHierarchyObject(sceneObject);
        if (ImGui.MenuItem(sceneObject.Visible ? "隐藏对象" : "显示对象"))
        { sceneObject.Visible = !sceneObject.Visible; MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }
        if (ImGui.MenuItem(sceneObject.Locked ? "解锁" : "锁定")) ToggleSceneObjectLock(sceneObject);
        if (ImGui.BeginMenu("创建子对象"))
        {
            DrawHierarchyCreateMenu(sceneObject);
            ImGui.EndMenu();
        }
        ImGui.Separator();
        if (sceneObject.Prefab is null)
        {
            if (ImGui.MenuItem("创建为预制体"))
            {
                _pendingActions.Enqueue(CreatePrefabFromSelection);
            }
        }
        else
        {
            ImGui.TextDisabled($"预制体：{sceneObject.Prefab.AssetPath}");
            ImGui.Separator();
            if (ImGui.MenuItem("还原实例"))
            {
                _selectedSceneObjectId = sceneObject.Id;
                _pendingActions.Enqueue(RevertSelectedPrefabInstance);
            }

            if (ImGui.MenuItem("应用到预制体"))
            {
                _selectedSceneObjectId = sceneObject.Id;
                _pendingActions.Enqueue(ApplySelectedPrefabToAsset);
            }

            if (ImGui.MenuItem("存为变体"))
            {
                _selectedSceneObjectId = sceneObject.Id;
                _pendingActions.Enqueue(CreateVariantFromSelectedInstance);
            }
        }
    }

    /// <summary>
    /// 在检查器头部绘制预制体区块：来源路径、还原与应用按钮
    /// </summary>
    private void DrawPrefabInspectorSection(SceneObject selectedObject)
    {
        if (selectedObject.Prefab is null)
        {
            return;
        }

        ImGui.TextDisabled($"预制体实例：{selectedObject.Prefab.AssetPath}");
        if (ImGui.SmallButton("还原实例"))
        {
            RevertSelectedPrefabInstance();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("应用到预制体"))
        {
            ApplySelectedPrefabToAsset();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("存为变体"))
        {
            CreateVariantFromSelectedInstance();
            return;
        }

        // 变体：显示基础链状态与拉取更新入口
        if (TryGetSelectedPrefabInstance(out _, out var asset, out _) && asset.BaseAssetPath.Length > 0)
        {
            ImGui.TextDisabled($"变体基础：{asset.BaseAssetPath}");
            var basePath = _project is null ? null : ResolvePrefabPath(_project, asset.BaseAssetPath);
            var baseExists = basePath is not null && File.Exists(basePath);
            if (!baseExists)
            {
                ImGui.TextColored(new Vector4(1F, 0.5F, 0.3F, 1F), "基础资产缺失，变体按当前内容使用");
            }
            else if (asset.BasePrefabId != StableId.Empty && LoadPrefabCached(basePath!)
                     is { } baseAsset && (baseAsset.PrefabId != asset.BasePrefabId || baseAsset.Revision != asset.BaseRevision))
            {
                ImGui.TextColored(new Vector4(1F, 0.6F, 0.25F, 1F), "基础资产已更新，实例可拉取继承内容");
                if (ImGui.SmallButton("拉取基础更新"))
                {
                    PullBaseUpdateForSelectedVariant();
                    return;
                }
            }
        }

        // 覆盖检测：相对生效内容展示差异并支持单项还原
        if (TryGetSelectedPrefabInstance(out _, out asset, out _) &&
            TryGetEffectivePrefab(asset, out var effective, out var conflicts))
        {
            if (conflicts.Count > 0)
            {
                ImGui.TextColored(new Vector4(1F, 0.6F, 0.25F, 1F), $"基础与变体冲突 {conflicts.Count} 处（保留变体值）");
            }

            var overrides = PrefabOverrides.Compute(_sceneDocument!, selectedObject, effective);
            if (overrides.Count == 0)
            {
                ImGui.TextDisabled("与预制体资产一致，无覆盖");
            }
            else
            {
                if (overrides.Any(item => item.Kind == PrefabOverrides.Kind.SourceDrift))
                {
                    ImGui.TextColored(new Vector4(1F, 0.6F, 0.25F, 1F), "来源资产已更新，实例尚未完全同步");
                    if (ImGui.SmallButton("尝试同步##PrefabRevision"))
                    {
                        var path = ResolvePrefabPath(_project!, selectedObject.Prefab.AssetPath);
                        var updated = SyncPendingPrefabRevisions(path);
                        SetStatus($"已尝试同步 {updated} 个预制体实例；无法安全合并的内容会保留");
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("保留实例覆盖，并尝试合并可安全处理的来源更新。");
                }

                if (ImGui.CollapsingHeader($"实例覆盖（{overrides.Count}）"))
                {
                    foreach (var item in overrides)
                    {
                        ImGui.BulletText($"{item.ObjectName}：{PrefabSystem.KindLabel(item.Kind)}　{item.Detail}");
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"还原此项##{item.ObjectId.Value}"))
                        {
                            if (PrefabOverrides.RevertObject(_sceneDocument!, selectedObject, effective, item.ObjectId))
                            {
                                MarkSceneDocumentDirty();
                                SetStatus($"已还原对象：{item.ObjectName}");
                            }
                            else
                            {
                                SetStatus($"还原失败：{item.ObjectName}");
                            }
                        }
                    }
                }
            }
        }

        ImGui.Separator();
    }
}
