using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
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
            var asset = PrefabSystem.CreateAssetFromScene(_sceneDocument, selected, selected.Name);
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

        var absolutePath = Path.GetFullPath(
            Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            var asset = LoadPrefabCached(absolutePath);
            var instance = PrefabSystem.Instantiate(asset, relativePath, _sceneDocument);
            _selectedSceneObjectId = instance.Id;
            _selectedVoxel = null;
            MarkSceneDocumentDirty();
            SetStatus($"已实例化预制体：{Path.GetFileName(relativePath)}（{asset.Objects.Count} 个对象）");
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or IOException)
        {
            MVoxelLog.Error("实例化预制体失败", exception);
            SetStatus($"实例化预制体失败：{exception.Message}");
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
            if (!PrefabSystem.RefreshInstance(_sceneDocument!, instanceRoot, asset, preserveRootTransform: false))
            {
                SetStatus("所选对象不是预制体实例");
                return;
            }

            MarkSceneDocumentDirty();
            SetStatus($"已还原实例：{relativePath}");
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or IOException)
        {
            SetStatus($"预制体资产不可用，无法还原：{relativePath}");
        }
    }

    /// <summary>
    /// 把选中的实例内容应用到预制体资产，并自动更新同源的其他实例
    /// </summary>
    private void ApplySelectedPrefabToAsset()
    {
        if (!TryGetSelectedPrefabInstance(out var instanceRoot, out _, out var relativePath))
        {
            return;
        }

        try
        {
            var asset = PrefabSystem.ApplyInstanceToAsset(_sceneDocument!, instanceRoot, instanceRoot.Name);
            var absolutePath = Path.GetFullPath(
                Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            PrefabSerializer.Save(absolutePath, asset);
            _prefabCache[absolutePath] = asset;

            var updated = 0;
            foreach (var sibling in PrefabSystem.CollectInstanceRoots(_sceneDocument!, relativePath))
            {
                if (sibling.Id == instanceRoot.Id)
                {
                    continue;
                }

                if (PrefabSystem.RefreshInstance(_sceneDocument!, sibling, asset, preserveRootTransform: true))
                {
                    updated++;
                }
            }

            MarkSceneDocumentDirty();
            SetStatus(updated > 0
                ? $"已应用预制体并更新 {updated} 个同源实例：{relativePath}"
                : $"已应用预制体：{relativePath}");
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or IOException
                                              or UnauthorizedAccessException)
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
        var absolutePath = Path.GetFullPath(
            Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
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

        // 覆盖检测：相对来源资产展示差异并支持单项还原
        if (TryGetSelectedPrefabInstance(out _, out var asset, out _))
        {
            var overrides = PrefabOverrides.Compute(_sceneDocument!, selectedObject, asset);
            if (overrides.Count == 0)
            {
                ImGui.TextDisabled("与预制体资产一致，无覆盖");
            }
            else
            {
                if (overrides.Any(item => item.Kind == PrefabOverrides.Kind.SourceDrift))
                {
                    ImGui.TextColored(new Vector4(1F, 0.6F, 0.25F, 1F), "来源资产已更新，实例尚未同步");
                }

                if (ImGui.CollapsingHeader($"实例覆盖（{overrides.Count}）"))
                {
                    foreach (var item in overrides)
                    {
                        ImGui.BulletText($"{item.ObjectName}：{LabelOf(item.Kind)}　{item.Detail}");
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"还原此项##{item.ObjectId.Value}"))
                        {
                            if (PrefabOverrides.RevertObject(_sceneDocument!, selectedObject, asset, item.ObjectId))
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

    /// <summary>
    /// 覆盖类型的中文标签
    /// </summary>
    private static string LabelOf(PrefabOverrides.Kind kind) => kind switch
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
}
