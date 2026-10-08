using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Editor.Settings;
using MVoxel.Editor.Templates;
using MVoxel.Voxels;
using MVoxel.Scene;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using OpenTK.Mathematics;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using NumVector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

internal enum EditorTool
{
    Camera,
    Draw,
    Erase,
    Pick,
    Line,
    Box,
    Fill,
    Eyedropper,
}

/// <summary>
/// GPU 视口接线、笔刷工具、撤销入口和渲染设置面板
/// 所有操作都在编辑器线程执行，渲染管线只接收不可变快照和扁平参数块
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    // 场景与渲染窗口可以同时可见，不能共享会被另一窗口重建的输出纹理。
    private VulkanVoxelRenderPipeline? _scenePipeline;
    private bool _worldLoadFailed;
    private Rgba32? _sampledBrushColor;
    private bool _settingsToggleSun;
    private bool _settingsToggleAo;
    private bool _settingsTogglePalette;
    private bool _settingsToggleGround;
    private bool _settingsTogglePath;
    private bool _settingsTogglePathDenoise;
    private bool _settingsToggleDof;
    private bool _settingsToggleMis;
    private bool _settingsToggleEmissive;
    private bool _settingsToggleDither;
    private bool _settingsToggleHueCycle;
    private bool _settingsToggleAutoPerformance;
    private bool _brushPreviewErase;
    private VoxelCoord64? _toolDragAnchor;
    private VoxelCoord64? _toolDragCurrent;
    private StableId? _toolDragTargetId;
    private bool _toolDragErase;
    private IReadOnlyList<VoxelCoord64>? _pendingFloodCells;
    private VoxelValue _pendingFloodValue;
    private StableId? _pendingFloodTargetId;
    private bool _pendingFloodOpen;

    private void InitializeVoxelPipeline()
    {
        if (_pipelineInitializationAttempted) return;
        _pipelineInitializationAttempted = true;
        _gpuRenderingDisabled = false;
        _pipelineStatus = "显卡体素管线正在初始化";
        try
        {
            _pipeline = new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
            _pipeline.SetMaterialTable(Project.MaterialAsset.PackTable(_brushMaterials));
            _pipelineStatus = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            _pipeline = null;
            _pipelineStatus = $"显卡计算管线初始化失败，当前使用 CPU 预览：{exception.Message}";
            MVoxelLog.Error("Vulkan Compute 管线初始化失败，已切换 CPU 预览", exception);
        }

        SyncSettingsToggles();
    }

    private void SyncSettingsToggles() => SyncSettingsToggles(_renderSettings);

    private void SyncSettingsToggles(RenderSettings settings)
    {
        _settingsToggleSun = settings.SunEnabled;
        _settingsToggleAo = settings.AmbientOcclusionEnabled;
        _settingsTogglePalette = settings.PaletteShading;
        _settingsToggleGround = settings.ShowGround;
        _settingsTogglePath = settings.PathTracingEnabled;
        _settingsTogglePathDenoise = settings.PathDenoising;
        _settingsToggleDof = settings.DepthOfField;
        _settingsToggleMis = settings.DirectLightingMIS;
        _settingsToggleEmissive = settings.EmissiveVoxels;
        _settingsToggleDither = settings.Dither;
        _settingsToggleHueCycle = settings.HueCycle;
        _settingsToggleAutoPerformance = settings.AutoPerformanceScaling;
    }

    private void ShutdownVoxelPipeline()
    {
        if (_project is null) SaveEditorSettings();
        else SaveProjectSettings();

        _pipeline?.Dispose();
        _pipeline = null;
        _scenePipeline?.Dispose();
        _scenePipeline = null;
    }

    internal readonly record struct ModelLoadKey(string Path, VoxelCoord64 Origin);

    private sealed record PreparedProjectLoad(ProjectHandle Project, PreloadedModels Models);

    internal sealed record PreloadedModels(IReadOnlyDictionary<ModelLoadKey, VoxelWorld> Worlds,
        IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> LayerOwnership, int MergedLayers,
        bool MergeCompleted);

    /// <summary>在项目操作线程预读场景实际引用的模型，避免完成回调在 UI 线程逐格解码。</summary>
    internal static PreloadedModels PreloadProjectModels(ProjectHandle project,
        CancellationToken cancellation, Action<BlockingOperationProgress> report)
    {
        var scenePath = Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox");
        Scene.SceneDocument? scene = null;
        if (File.Exists(scenePath))
        {
            try { scene = Scene.SceneSerializer.Load(scenePath); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
        }

        var assetsRoot = Path.GetFullPath(project.AssetsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var keys = new List<ModelLoadKey>();
        if (scene is not null)
        {
            foreach (var sceneObject in scene.Objects)
            foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
            {
                var relativePath = SceneComponentTypes.ReadVoxelModel(component).Model;
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    !(relativePath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase) ||
                      relativePath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase))) continue;
                var path = Path.GetFullPath(Path.Combine(project.RootPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (path.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    keys.Add(new ModelLoadKey(path, GetSceneModelOrigin(sceneObject)));
            }
        }

        if (keys.Count == 0)
        {
            var worldsPath = Path.Combine(project.AssetsPath, "Worlds");
            var firstLargeWorld = scene is null || !HasModelReference(scene)
                ? Directory.Exists(worldsPath)
                    ? Directory.EnumerateFiles(worldsPath, "*.mvworld")
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    : null
                : null;
            var fallback = firstLargeWorld ?? Path.Combine(worldsPath, "主世界.mvmodel");
            if (File.Exists(fallback)) keys.Add(new ModelLoadKey(fallback, VoxelCoord64.Zero));
        }

        keys = keys.Distinct().ToList();
        var loaded = new Dictionary<ModelLoadKey, VoxelWorld>();
        var totalBytes = keys.Sum(key => new FileInfo(key.Path).Length);
        long completedBytes = 0;
        foreach (var key in keys)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = new FileInfo(key.Path).Length;
            try
            {
                loaded.Add(key, LargeWorldFormat.Read(key.Path, key.Origin, cancellation,
                    (completed, total) => report(new BlockingOperationProgress(
                        $"正在读取 {Path.GetFileName(key.Path)}（{completed:N0}/{total:N0} 体素）…",
                        (float)(.22D + .72D * (completedBytes + length * (double)completed / Math.Max(1, total)) /
                        Math.Max(1, totalBytes)), CanCancel: true))));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                // 保留原有的主世界回退与分层跳过逻辑，由 UI 应用阶段处理失败资产。
            }
            completedBytes += length;
        }

        var ownership = new Dictionary<StableId, HashSet<VoxelCoord64>>();
        var primaryPath = FindReferencedModelPath(project, scene, ".mvworld") ??
                          FindReferencedModelPath(project, scene, ".mvmodel");
        if (primaryPath is null && keys.Count > 0) primaryPath = keys[0].Path;
        var primaryObject = primaryPath is null ? null : FindReferencedModelObject(project, scene, primaryPath);
        var primaryKey = primaryPath is null ? default : new ModelLoadKey(primaryPath, GetSceneModelOrigin(primaryObject));
        if (scene is null || primaryPath is null || !loaded.TryGetValue(primaryKey, out var primaryWorld))
            return new PreloadedModels(loaded, ownership, 0, MergeCompleted: false);

        var merged = 0;
        var mergeTotal = loaded.Where(entry => !string.Equals(entry.Key.Path, primaryPath,
            StringComparison.OrdinalIgnoreCase)).Sum(entry => (long)entry.Value.OccupiedVoxelCount);
        long mergedCells = 0;
        var mergedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sceneObject in scene.Objects)
        foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
        {
            cancellation.ThrowIfCancellationRequested();
            var model = SceneComponentTypes.ReadVoxelModel(component).Model;
            if (string.IsNullOrWhiteSpace(model)) continue;
            var absolute = Path.GetFullPath(Path.Combine(project.RootPath,
                model.Replace('/', Path.DirectorySeparatorChar)));
            if (string.Equals(absolute, primaryPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(absolute) || !mergedPaths.Add(absolute)) continue;
            if (!loaded.TryGetValue(new ModelLoadKey(absolute, GetSceneModelOrigin(sceneObject)), out var layerWorld))
                continue;

            var owned = new HashSet<VoxelCoord64>();
            var copied = 0;
            foreach (var item in layerWorld.EnumerateOccupied())
            {
                primaryWorld.Set(item.Coordinate, item.Value);
                owned.Add(item.Coordinate);
                copied++;
                mergedCells++;
                if (copied % 65536 != 0) continue;
                cancellation.ThrowIfCancellationRequested();
                report(new BlockingOperationProgress($"正在合并分层对象：{sceneObject.Name}…",
                    .94F + .02F * mergedCells / Math.Max(1, mergeTotal), CanCancel: true));
            }
            ownership[sceneObject.Id] = owned;
            merged++;
            report(new BlockingOperationProgress($"已合并 {merged} 个分层对象…", .95F, CanCancel: true));
        }

        return new PreloadedModels(new Dictionary<ModelLoadKey, VoxelWorld> { [primaryKey] = primaryWorld },
            ownership, merged, MergeCompleted: true);
    }

    private void LoadWorldForProject(ProjectHandle project,
        PreloadedModels? preload = null)
    {
        _voxelHistory.Clear();
        _layerOwnership.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
        _pinnedVoxelModelId = null;
        _sceneDocument = LoadSceneDocument(project);
        _sceneDocumentDirty = false;
        _renderCameraId = null;
        _renderOrbitAnchor = null;
        _renderCameraInitialFramePending = true;
        _composedRenderWorld = null;
        _composedSignature = 0;
        _composedMask = uint.MaxValue;
        _selectedProjectPalettePath = EditorPalettes.NormalizeProjectPalettePath(_projectSettings.ActivePalettePath);
        var projectPalettePaths = EditorPalettes.EnumerateProjectPalettePaths(project);
        if (projectPalettePaths.Count > 0 &&
            !projectPalettePaths.Contains(_selectedProjectPalettePath, StringComparer.OrdinalIgnoreCase))
        {
            _selectedProjectPalettePath = projectPalettePaths[0];
            _projectSettings.ActivePalettePath = _selectedProjectPalettePath;
            SaveProjectSettings();
        }
        _palette = EditorPalettes.ResolveProjectPalette(project, _selectedProjectPalettePath);
        _sampledBrushColor = null;
        _paletteIsProjectLevel = true;
        var referencedLargeWorldPath = FindReferencedModelPath(project, _sceneDocument, ".mvworld");
        var referencedNativePath = FindReferencedModelPath(project, _sceneDocument, ".mvmodel");
        var hasModelReference = HasModelReference(_sceneDocument);
        var largeWorldPath = referencedLargeWorldPath ??
            (!hasModelReference && referencedNativePath is null && Directory.Exists(Path.Combine(project.AssetsPath, "Worlds"))
                ? Directory.EnumerateFiles(Path.Combine(project.AssetsPath, "Worlds"), "*.mvworld")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null);
        var modelPath = referencedNativePath ?? Path.Combine(project.AssetsPath, "Worlds", "主世界.mvmodel");
        var primaryObject = FindReferencedModelObject(project, _sceneDocument, modelPath);
        var primaryOrigin = GetSceneModelOrigin(primaryObject);
        try
        {
            if (largeWorldPath is not null && File.Exists(largeWorldPath))
            {
                _world = preload is not null &&
                         preload.Worlds.TryGetValue(new ModelLoadKey(largeWorldPath, primaryOrigin), out var loadedLarge)
                    ? loadedLarge : LargeWorldFormat.Read(largeWorldPath, primaryOrigin);
                SetStatus($"已载入体素世界：{Path.GetFileName(largeWorldPath)}（{_world.OccupiedVoxelCount:N0} 体素）");
            }
            else if (File.Exists(modelPath))
            {
                _world = preload is not null &&
                         preload.Worlds.TryGetValue(new ModelLoadKey(modelPath, primaryOrigin), out var loadedModel)
                    ? loadedModel : LargeWorldFormat.Read(modelPath, primaryOrigin);
                SetStatus($"已载入体素世界：{Path.GetFileName(modelPath)}（{_world.OccupiedVoxelCount} 体素）");
            }
            else if (string.Equals(_projectSettings.TemplateId, ProjectTemplateId.Empty.ToString(), StringComparison.Ordinal))
            {
                _world = new VoxelWorld();
                SetStatus("已载入空项目，可以从零开始建模");
            }
            else
            {
                _world = new VoxelWorld();
                _worldLoadFailed = true;
                SetStatus("引用的体素数据不存在，未使用示范模型替代；请检查资产路径");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            _world = new VoxelWorld();
            MVoxelLog.Error("模型读取失败，已保留场景引用", exception);
            _worldLoadFailed = true;
            SetStatus($"模型读取失败，未使用其他模型替代：{exception.Message}");
        }

        var primaryPath = largeWorldPath is not null && File.Exists(largeWorldPath) ? largeWorldPath : modelPath;
        var mergedLayers = preload?.MergeCompleted == true
            ? preload.MergedLayers
            : MergeSceneModelWorlds(project, primaryPath, preload?.Worlds);
        if (preload?.MergeCompleted == true)
            foreach (var (objectId, owned) in preload.LayerOwnership) _layerOwnership[objectId] = owned;
        if (mergedLayers > 0)
        {
            SetStatus($"{_statusText}，已合并 {mergedLayers} 个分层对象");
        }

        // 项目创建/打开完成回调不能在 UI 线程同步构建和上传 GPU 工作集，
        // 否则大模板会让进度弹窗停在 98%。场景视图不依赖该工作集，
        // 渲染视图首次绘制时会按当前图层和项目配置按需同步。
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _lastRenderGpuSubmitMs = 0;
        _renderViewNeedsInitialRender = true;
        FrameCameraOnWorld();
    }

    /// <summary>
    /// 按场景中的模型组件解析资源，避免项目同时存在多个模型时误载入其他文件。
    /// 解析范围限定在 Assets 内，场景文件中的外部路径不会被访问。
    /// </summary>
    private static string? FindReferencedModelPath(ProjectHandle project, Scene.SceneDocument? document,
        string extension)
    {
        var model = document?.Objects
            .SelectMany(item => item.Components)
            .Where(SceneComponentTypes.IsVoxelModel)
            .Select(SceneComponentTypes.ReadVoxelModel)
            .Select(data => data.Model)
            .FirstOrDefault(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(model)) return null;

        var candidate = Path.GetFullPath(Path.Combine(project.RootPath, model.Replace('/', Path.DirectorySeparatorChar)));
        var assetsRoot = Path.GetFullPath(project.AssetsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    /// <summary>
    /// 找到实际作为当前工作世界入口的模型对象。模型文件按局部包围盒保存，
    /// 读取时必须同时取得场景对象的格变换原点，否则首个分层模型会被错误放到世界原点。
    /// </summary>
    private static SceneObject? FindReferencedModelObject(ProjectHandle project, Scene.SceneDocument? document,
        string absolutePath)
    {
        if (document is null) return null;
        foreach (var sceneObject in document.Objects)
        foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
        {
            var model = SceneComponentTypes.ReadVoxelModel(component).Model;
            if (string.IsNullOrWhiteSpace(model)) continue;
            var candidate = Path.GetFullPath(Path.Combine(project.RootPath,
                model.Replace('/', Path.DirectorySeparatorChar)));
            if (string.Equals(candidate, absolutePath, StringComparison.OrdinalIgnoreCase))
                return sceneObject;
        }

        return null;
    }

    private static VoxelCoord64 GetSceneModelOrigin(SceneObject? sceneObject) =>
        sceneObject?.Transform.Mode == TransformMode.Grid
            ? sceneObject.Transform.Grid.Position
            : VoxelCoord64.Zero;

    private static bool HasModelReference(Scene.SceneDocument? document) => document?.Objects
        .SelectMany(item => item.Components)
        .Where(SceneComponentTypes.IsVoxelModel)
        .Select(SceneComponentTypes.ReadVoxelModel)
        .Any(data => !string.IsNullOrWhiteSpace(data.Model)) == true;

    /// <summary>
    /// 读取项目主场景并保留场景文档供层级和渲染窗口共同使用
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <returns>主场景文档</returns>
    private Scene.SceneDocument LoadSceneDocument(ProjectHandle project)
    {
        var scenePath = Path.Combine(project.AssetsPath,
            _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scenePath))
        {
            _sceneValidationErrors = Array.Empty<string>();
            return new Scene.SceneDocument { Name = "主场景" };
        }

        try
        {
            var scene = Scene.SceneSerializer.Load(scenePath);
            _sceneValidationErrors = Scene.SceneValidation.Validate(scene);
            foreach (var error in _sceneValidationErrors)
            {
                MVoxelLog.Warn($"主场景结构校验：{error}");
            }

            return scene;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            _sceneValidationErrors = Array.Empty<string>();
            MVoxelLog.Error($"主场景读取失败，已使用新的场景文档：{scenePath}", exception);
            return new Scene.SceneDocument { Name = "主场景" };
        }
    }

    private void FrameCameraOnWorld()
    {
        if (_world.TryGetBounds(out var min, out var max))
        {
            var limits = SceneZoomPolicy.FromBounds(
                max.X - min.X + 1D, max.Y - min.Y + 1D, max.Z - min.Z + 1D);
            var center = new Vector2((float)((min.X + max.X) / 2.0), (float)((min.Z + max.Z) / 2.0));
            var diagonal = MathF.Sqrt(
                MathF.Pow((float)(max.X - min.X + 1), 2) +
                MathF.Pow((float)(max.Y - min.Y + 1), 2) +
                MathF.Pow((float)(max.Z - min.Z + 1), 2));
            _scenePan = center;
            _sceneFreeMoveHeight = 0F;
            _sceneCameraDistance = Math.Clamp(diagonal * 1.15F, limits.MinimumDistance, limits.MaximumDistance);
            _sceneOrbitYaw = MathF.PI / 4F;
            _sceneOrbitPitch = 0.42F;
        }
        else
        {
            _scenePan = Vector2.Zero;
            _sceneFreeMoveHeight = 0F;
            var limits = SceneZoomPolicy.Default();
            _sceneCameraDistance = Math.Clamp(18F, limits.MinimumDistance, limits.MaximumDistance);
        }
    }

    private SceneZoomLimits GetSceneZoomLimits()
    {
        if (!_world.TryGetBounds(out var min, out var max))
        {
            return SceneZoomPolicy.Default();
        }

        return SceneZoomPolicy.FromBounds(
            max.X - min.X + 1D,
            max.Y - min.Y + 1D,
            max.Z - min.Z + 1D);
    }

    /// <summary>
    /// 根据当前场景距离和 LOD 配置计算场景视图的 GPU 体素预算
    /// </summary>
    /// <returns>零表示完整工作集，其余数值表示场景视图采样上限</returns>
    private int GetSceneGpuCellBudget()
    {
        // 场景视图只按显存安全上限采样，不再按相机距离做隐式降采样；
        // 距离粗化统一交给"场景窗口 LOD"（RenderSettings.SceneLod）的勾选与分档控制
        var count = _world.OccupiedVoxelCount;
        return count <= 250_000 ? 0 : 250_000;
    }

    /// <summary>
    /// 当前渲染图层遮罩对应的渲染世界：按对象可见性与图层过滤合并态工作世界，
    /// 结果按（内容版本、可见性/图层状态、遮罩）缓存，状态不变时零开销复用
    /// </summary>
    /// <param name="cullingMask">相机图层遮罩</param>
    /// <returns>可同步到 GPU 的渲染世界</returns>
    private VoxelWorld GetRenderWorld(uint cullingMask)
    {
        if (_sceneDocument is null)
        {
            return _world;
        }

        var signature = RenderStateSignature();
        if (_composedRenderWorld is { } cached && _composedSignature == signature && _composedMask == cullingMask)
        {
            return cached;
        }

        var primaryId = GetVoxelWorldObject()?.Id ?? default;
        var composed = Scene.RenderWorldComposer.Compose(_world, primaryId, _sceneDocument.Objects,
            _layerOwnership, cullingMask, _sceneDocument.LayerNames, _sceneDocument.HiddenLayerNames);
        _composedRenderWorld = composed;
        _composedSignature = signature;
        _composedMask = cullingMask;
        return composed;
    }

    /// <summary>
    /// 渲染状态指纹：工作世界版本、全部对象的可见性与图层、分层所有权规模
    /// </summary>
    private long RenderStateSignature()
    {
        unchecked
        {
            var hash = (long)_world.ContentVersion * 397;
            foreach (var sceneObject in _sceneDocument?.Objects ?? [])
            {
                hash = hash * 31 + sceneObject.Id.GetHashCode();
                hash = hash * 31 + (sceneObject.ParentId?.GetHashCode() ?? 0);
                hash = hash * 31 + (sceneObject.Visible ? 7 : 3);
                hash = hash * 31 + sceneObject.LayerIndex;
                foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
                    hash = hash * 31 + (component.Enabled ? 7 : 3);
            }

            foreach (var layer in _sceneDocument?.HiddenLayerNames ?? [])
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(layer);

            foreach (var owned in _layerOwnership.Values)
            {
                hash = hash * 31 + owned.Count;
            }

            return hash;
        }
    }

    /// <summary>
    /// 当前渲染视口选中相机的图层遮罩；未绑定相机对象时渲染全部图层
    /// </summary>
    private uint GetActiveRenderCullingMask()
    {
        var component = GetActiveRenderCameraObject()?.Components.FirstOrDefault(SceneComponentTypes.IsCamera);
        return component is null ? uint.MaxValue : SceneComponentTypes.ReadCamera(component).CullingMask;
    }

    /// <summary>
    /// 解析当前渲染配置。渲染参数只来自编辑器默认配置或当前项目配置，
    /// 相机对象不再拥有独立覆盖。
    /// </summary>
    /// <param name="cameraObject">相机对象，可为空</param>
    /// <returns>生效渲染配置</returns>
    private RenderSettings ResolveRenderSettings(SceneObject? cameraObject) => _renderSettings;

    /// <summary>
    /// 返回渲染窗口右侧面板应该编辑的项目配置。
    /// </summary>
    private RenderSettings GetEditableRenderSettings() => _renderSettings;

    /// <summary>
    /// 确认右侧面板对项目配置的修改，并重置渲染累积。
    /// </summary>
    private void SaveEditableRenderSettings(RenderSettings settings, string before)
    {
        if (string.Equals(before, settings.ToJson(), StringComparison.Ordinal))
        {
            return;
        }

        settings.ClampAll();
        _renderSettings = settings;
        SyncSettingsToggles(settings);
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
    }

    /// <summary>
    /// 分层体素的所有权表：对象 ID 到该对象拥有的体素坐标集合，
    /// 保存时据此把主世界与分层文件各自写回，互不吞并
    /// </summary>
    private readonly Dictionary<StableId, HashSet<VoxelCoord64>> _layerOwnership = new();

    /// <summary>体素编辑事务日志，随恢复区目录存放，正式保存或快照后清空</summary>
    private VoxelEditingJournal? _editingJournal;

    /// <summary>按图层遮罩组成的渲染世界缓存</summary>
    private VoxelWorld? _composedRenderWorld;

    private VoxelWorld? _axisOcclusionBoundsWorld;
    private int _axisOcclusionBoundsVersion = -1;
    private VoxelCoord64 _axisOcclusionMinimum;
    private VoxelCoord64 _axisOcclusionMaximum;

    private void UpdateAxisOcclusionBounds()
    {
        if (ReferenceEquals(_axisOcclusionBoundsWorld, _world) &&
            _axisOcclusionBoundsVersion == _world.ContentVersion) return;
        if (!_world.TryGetBounds(out _axisOcclusionMinimum, out _axisOcclusionMaximum)) return;
        _axisOcclusionBoundsWorld = _world;
        _axisOcclusionBoundsVersion = _world.ContentVersion;
    }

    private bool _sceneSliceEnabled;
    private int _sceneSliceAxis;
    private int _sceneSliceCoordinate;
    private bool _sceneSliceKeepGreater;
    private VoxelWorld? _sceneSliceWorld;
    private VoxelWorld? _sceneSliceSource;
    private int _sceneSliceSourceVersion = -1;
    private int _sceneSliceCachedAxis = -1;
    private int _sceneSliceCachedCoordinate;
    private bool _sceneSliceCachedKeepGreater;
    private VoxelWorld? _sceneSliceBoundsSource;
    private int _sceneSliceBoundsVersion = -1;
    private VoxelCoord64 _sceneSliceMinimum;
    private VoxelCoord64 _sceneSliceMaximum;

    private bool TryGetSceneSliceBounds(VoxelWorld world, out VoxelCoord64 minimum, out VoxelCoord64 maximum)
    {
        if (ReferenceEquals(_sceneSliceBoundsSource, world) && _sceneSliceBoundsVersion == world.ContentVersion)
        {
            minimum = _sceneSliceMinimum;
            maximum = _sceneSliceMaximum;
            return true;
        }

        if (!world.TryGetBounds(out minimum, out maximum)) return false;
        _sceneSliceBoundsSource = world;
        _sceneSliceBoundsVersion = world.ContentVersion;
        _sceneSliceMinimum = minimum;
        _sceneSliceMaximum = maximum;
        return true;
    }

    private VoxelWorld GetSceneInspectionWorld(VoxelWorld source)
    {
        if (!_sceneSliceEnabled) return source;
        if (_sceneSliceWorld is not null && ReferenceEquals(_sceneSliceSource, source)
            && _sceneSliceSourceVersion == source.ContentVersion && _sceneSliceCachedAxis == _sceneSliceAxis
            && _sceneSliceCachedCoordinate == _sceneSliceCoordinate && _sceneSliceCachedKeepGreater == _sceneSliceKeepGreater)
            return _sceneSliceWorld;

        _sceneSliceWorld = VoxelWorldSlice.CreateHalfSpace(source, _sceneSliceAxis, _sceneSliceCoordinate, _sceneSliceKeepGreater);
        _sceneSliceSource = source;
        _sceneSliceSourceVersion = source.ContentVersion;
        _sceneSliceCachedAxis = _sceneSliceAxis;
        _sceneSliceCachedCoordinate = _sceneSliceCoordinate;
        _sceneSliceCachedKeepGreater = _sceneSliceKeepGreater;
        return _sceneSliceWorld;
    }

    private void DrawSceneSliceControls()
    {
        if (!_sceneSliceEnabled) return;

        var world = GetRenderWorld(uint.MaxValue);
        if (!TryGetSceneSliceBounds(world, out var min, out var max))
        {
            ImGui.TextDisabled("场景中没有体素");
            return;
        }

        ImGui.SetNextItemWidth(72F);
        if (ImGui.Combo("轴##SceneSliceAxis", ref _sceneSliceAxis, "X\0Y\0Z\0", 3)) _sceneSliceWorld = null;
        ImGui.SameLine();
        var keepGreater = _sceneSliceKeepGreater;
        if (ImGui.Checkbox("保留正侧##SceneSliceSide", ref keepGreater))
        {
            _sceneSliceKeepGreater = keepGreater;
            _sceneSliceWorld = null;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("保留坐标大于或等于切面位置的一侧。");

        var minimum = _sceneSliceAxis switch { 0 => min.X, 1 => min.Y, _ => min.Z };
        var maximum = _sceneSliceAxis switch { 0 => max.X, 1 => max.Y, _ => max.Z };
        var low = (int)Math.Clamp(minimum, int.MinValue, int.MaxValue);
        var high = (int)Math.Clamp(maximum, int.MinValue, int.MaxValue);
        if (_sceneSliceCoordinate < low || _sceneSliceCoordinate > high)
            _sceneSliceCoordinate = (int)Math.Clamp(minimum + (maximum - minimum) / 2, int.MinValue, int.MaxValue);
        ImGui.SetNextItemWidth(Math.Max(120F, ImGui.GetContentRegionAvail().X - 90F));
        if (low < high && ImGui.SliderInt("切面位置##SceneSlicePosition", ref _sceneSliceCoordinate, low, high))
            _sceneSliceWorld = null;
    }

    /// <summary>缓存对应的渲染状态指纹</summary>
    private long _composedSignature;

    /// <summary>缓存对应的图层遮罩</summary>
    private uint _composedMask;

    private bool SaveWorldToProject(ProjectHandle project)
    {
        if (_worldLoadFailed)
        {
            SetStatus("体素资产未完整加载，已阻止保存覆盖；请修复资产路径或重新打开项目");
            return false;
        }
        try
        {
            if (GetVoxelWorldObject() is null && _world.OccupiedVoxelCount == 0)
            {
                Scene.SceneSerializer.Save(Path.Combine(project.AssetsPath, _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    _sceneDocument ?? new SceneDocument { Name = "主场景" });
                return true;
            }
            var modelAssetPath = ResolveCurrentWorldAssetPath(project);
            var modelPath = Path.Combine(project.RootPath, modelAssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            // 分层模式下 _world 是合并态：主世界文件只写非分层体素，
            // 各分层对象按所有权分别写回自己的文件，分层在保存后依然成立
            var primaryVoxelCount = _world.OccupiedVoxelCount;
            var primaryWorldForBounds = _world;
            if (_layerOwnership.Count == 0)
            {
                if (_world.OccupiedVoxelCount > 0)
                {
                    LargeWorldFormat.Write(modelPath, _world);
                }
                else LargeWorldFormat.WriteEmpty(modelPath);
            }
            else
            {
                var ownedUnion = new HashSet<VoxelCoord64>();
                foreach (var owned in _layerOwnership.Values)
                {
                    ownedUnion.UnionWith(owned);
                }

                var terrainOnly = new VoxelWorld();
                foreach (var item in _world.EnumerateOccupied())
                {
                    if (!ownedUnion.Contains(item.Coordinate))
                    {
                        terrainOnly.Set(item.Coordinate, item.Value);
                    }
                }

                primaryVoxelCount = terrainOnly.OccupiedVoxelCount;
                primaryWorldForBounds = terrainOnly;
                if (primaryVoxelCount > 0)
                {
                    LargeWorldFormat.Write(modelPath, terrainOnly);
                }
                else
                {
                    LargeWorldFormat.WriteEmpty(modelPath);
                }

                SaveOwnedLayers(project);
            }

            var scenePath = Path.Combine(project.AssetsPath, _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var document = _sceneDocument ?? new Scene.SceneDocument { Name = "主场景" };
            var graph = new Scene.SceneGraph(document);
            var modelObject = document.Objects.FirstOrDefault(item =>
                item.Components.Any(SceneComponentTypes.IsVoxelModel));
            modelObject ??= graph.Add("体素世界", transform: TransformComponent.FromGrid(GridTransform.Identity));
            AlignModelOriginToBounds(modelObject, primaryWorldForBounds);
            var modelComponent = modelObject.Components.FirstOrDefault(component =>
                SceneComponentTypes.IsVoxelModel(component));
            if (modelComponent is null)
            {
                modelObject.Components.Add(new Scene.ComponentRecord
                {
                    TypeId = SceneComponentTypes.VoxelModel,
                    SchemaVersion = 1,
                    Data = JsonSerializer.SerializeToElement(new
                    {
                        model = modelAssetPath,
                        voxelCount = primaryVoxelCount,
                        part = "场景模型",
                    }),
                });
            }
            else
            {
                modelComponent.Data = JsonSerializer.SerializeToElement(new
                {
                    model = modelAssetPath,
                    voxelCount = primaryVoxelCount,
                    part = "场景模型",
                });
            }
            _sceneDocument = document;
            Scene.SceneSerializer.Save(scenePath, document);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"保存失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把每个分层对象当前拥有的体素写回它引用的模型文件，并同步组件里的体素数
    /// </summary>
    private void SaveOwnedLayers(ProjectHandle project)
    {
        if (_sceneDocument is null)
        {
            return;
        }

        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!_layerOwnership.TryGetValue(sceneObject.Id, out var owned))
            {
                continue;
            }

            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null)
            {
                _layerOwnership.Remove(sceneObject.Id);
                continue;
            }

            var data = SceneComponentTypes.ReadVoxelModel(component);
            var layerPath = Path.GetFullPath(
                Path.Combine(project.RootPath, data.Model.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(layerPath)!);
            var layerWorld = new VoxelWorld();
            foreach (var coordinate in owned)
            {
                if (_world.TryGet(coordinate, out var value))
                {
                    layerWorld.Set(coordinate, value);
                }
            }

            if (layerWorld.OccupiedVoxelCount > 0)
            {
                LargeWorldFormat.Write(layerPath, layerWorld);
                AlignModelOriginToBounds(sceneObject, layerWorld);
            }
            else LargeWorldFormat.WriteEmpty(layerPath);
            component.Data = JsonSerializer.SerializeToElement(new
            {
                model = data.Model,
                voxelCount = layerWorld.OccupiedVoxelCount,
                part = string.IsNullOrWhiteSpace(data.Part) ? "分层对象" : data.Part,
            });
        }
    }

    /// <summary>格式使用包围盒最小角作为局部原点；保存后同步场景对象原点，保证重载位置不变。</summary>
    internal static void AlignModelOriginToBounds(SceneObject sceneObject, VoxelWorld world)
    {
        if (!world.TryGetBounds(out var minimum, out _)) return;
        var rotation = sceneObject.Transform.Mode == TransformMode.Grid
            ? sceneObject.Transform.Grid.Rotation : OrthogonalRotation.Identity;
        sceneObject.Transform = TransformComponent.FromGrid(new GridTransform(minimum, rotation));
    }

    /// <summary>
    /// 解析当前工作世界对应的资产相对路径，场景模型组件引用优先，缺失时回退到 Worlds/主世界.mvmodel
    /// </summary>
    private string ResolveCurrentWorldAssetPath(ProjectHandle project)
    {
        var referenced = FindReferencedModelPath(project, _sceneDocument, ".mvworld")
            ?? FindReferencedModelPath(project, _sceneDocument, ".mvmodel");
        return referenced is not null ? ToAssetRelativePath(project, referenced) : "Assets/Worlds/主世界.mvmodel";
    }

    /// <summary>
    /// 把绝对模型路径换算成 Assets 打头的资产相对路径，供场景模型组件引用
    /// </summary>
    private static string ToAssetRelativePath(ProjectHandle project, string absolutePath)
    {
        var relative = Path.GetRelativePath(project.AssetsPath, absolutePath).Replace('\\', '/');
        return "Assets/" + relative;
    }

    /// <summary>
    /// 把场景中的模型组件引用指向指定世界资产，保证保存与重新打开都作用于同一文件
    /// </summary>
    private void UpdateSceneModelReference(string assetRelativePath)
    {
        var document = _sceneDocument ??= new Scene.SceneDocument { Name = "主场景" };
        var graph = new Scene.SceneGraph(document);
        var modelObject = document.Objects.FirstOrDefault(item =>
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
        modelObject ??= graph.Add("体素世界", transform: TransformComponent.FromGrid(GridTransform.Identity));
        var modelComponent = modelObject.Components.FirstOrDefault(component =>
            SceneComponentTypes.IsVoxelModel(component));
        var data = new
        {
            model = assetRelativePath,
            voxelCount = _world.OccupiedVoxelCount,
            part = "场景模型",
        };
        if (modelComponent is null)
        {
            modelObject.Components.Add(new Scene.ComponentRecord
            {
                TypeId = SceneComponentTypes.VoxelModel,
                SchemaVersion = 1,
                Data = JsonSerializer.SerializeToElement(data),
            });
        }
        else
        {
            modelComponent.Data = JsonSerializer.SerializeToElement(data);
        }

        _sceneDocument = document;
    }

    /// <summary>
    /// 打开资产浏览器中指定的世界文件作为当前工作世界，导入与双击均走此入口
    /// </summary>
    private void OpenWorldAsset(string relativePath)
    {
        if (_project is null)
        {
            SetStatus("请先打开项目再打开体素数据");
            return;
        }
        var absolute = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolute))
        {
            SetStatus($"体素数据文件不存在：{relativePath}");
            return;
        }

        if (_sceneDocumentDirty)
        {
            SaveProject();
            if (_sceneDocumentDirty) return;
        }
        try
        {
            var workingRelativePath = relativePath.Replace('\\', '/');
            var extension = Path.GetExtension(absolute).ToLowerInvariant();
            if (extension is ".vox" or ".qb" or ".schem")
            {
                // 外部交换格式不能直接成为可编辑工作资产；双击时先导入为原生 .mvmodel。
                _world = ReadVoxelAsset(absolute);
                var importedBaseName = Path.GetFileNameWithoutExtension(absolute);
                var importedName = importedBaseName + ".mvmodel";
                var importedPath = Path.Combine(_project.AssetsPath, "Worlds", importedName);
                for (var suffix = 2; File.Exists(importedPath); suffix++)
                {
                    importedName = $"{importedBaseName}_{suffix}.mvmodel";
                    importedPath = Path.Combine(_project.AssetsPath, "Worlds", importedName);
                }
                LargeWorldFormat.Write(importedPath, _world);
                AssetMetaStore.EnsureMeta(importedPath);
                workingRelativePath = "Worlds/" + importedName;
                SetStatus($"已将外部 {extension} 导入为体素数据：{workingRelativePath}");
            }
            else
            {
                _world = LargeWorldFormat.Read(absolute);
            }

            var primaryAssetPath = "Assets/" + workingRelativePath;
            UpdateSceneModelReference(primaryAssetPath);
            // 切换世界是新的工作上下文：清空分层所有权，并移除旧世界遗留的分层引用
            _layerOwnership.Clear();
            _editingJournal = new VoxelEditingJournal(
                Path.Combine(RecoveryStore.DirectoryFor(_project.RootPath), "journal.jsonl"));
            var removedLayers = _sceneDocument is null
                ? 0
                : ConsolidateSecondaryModelObjects(_sceneDocument, primaryAssetPath);
            _sceneDocumentDirty = false;
            if (_sceneDocument is not null)
            {
                Scene.SceneSerializer.Save(
                    Path.Combine(_project.AssetsPath, _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar)), _sceneDocument);
            }

            _voxelHistory.Clear();
            _selectedVoxel = null;
            _pipeline?.SyncWorld(GetRenderWorld(uint.MaxValue), GetSceneGpuCellBudget());
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            FrameCameraOnWorld();
            SetStatus($"已打开体素数据：{Path.GetFileName(workingRelativePath)}（{_world.OccupiedVoxelCount:N0} 体素）");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"打开体素数据失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 把场景中其他模型组件引用的世界合并进当前世界，分层对象由此获得可见体素
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <param name="primaryPath">主世界文件绝对路径，与它相同的引用不再合并</param>
    /// <returns>成功合并的对象数量</returns>
    private int MergeSceneModelWorlds(ProjectHandle project, string primaryPath,
        IReadOnlyDictionary<ModelLoadKey, VoxelWorld>? preloadedModels = null)
    {
        if (_sceneDocument is null) return 0;
        var mergedCount = 0;
        var mergedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
            {
                var model = SceneComponentTypes.ReadVoxelModel(component).Model;
                if (string.IsNullOrWhiteSpace(model)) continue;
                var absolute = Path.GetFullPath(
                    Path.Combine(project.RootPath, model.Replace('/', Path.DirectorySeparatorChar)));
                if (string.Equals(absolute, primaryPath, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(absolute) || !mergedPaths.Add(absolute))
                {
                    continue;
                }

                try
                {
                    var origin = GetSceneModelOrigin(sceneObject);
                    var layerWorld = preloadedModels is not null &&
                                     preloadedModels.TryGetValue(new ModelLoadKey(absolute, origin), out var loadedLayer)
                        ? loadedLayer : LargeWorldFormat.Read(absolute, origin);
                    var owned = new HashSet<VoxelCoord64>();
                    foreach (var item in layerWorld.EnumerateOccupied())
                    {
                        _world.Set(item.Coordinate, item.Value);
                        owned.Add(item.Coordinate);
                    }

                    _layerOwnership[sceneObject.Id] = owned;
                    mergedCount++;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                {
                    MVoxelLog.Warn($"分层对象读取失败，已跳过：{sceneObject.Name}");
                }
            }
        }

        return mergedCount;
    }

    /// <summary>
    /// 移除主世界之外模型对象上的模型组件，供切换世界时清理旧上下文的分层引用
    /// </summary>
    /// <param name="document">待整理的场景文档</param>
    /// <param name="primaryAssetPath">主世界资产的相对路径</param>
    /// <returns>移除的模型组件数量</returns>
    private static int ConsolidateSecondaryModelObjects(Scene.SceneDocument document, string primaryAssetPath)
    {
        var removed = 0;
        for (var objectIndex = document.Objects.Count - 1; objectIndex >= 0; objectIndex--)
        {
            var sceneObject = document.Objects[objectIndex];
            for (var componentIndex = sceneObject.Components.Count - 1; componentIndex >= 0; componentIndex--)
            {
                var component = sceneObject.Components[componentIndex];
                if (!SceneComponentTypes.IsVoxelModel(component)) continue;
                var model = SceneComponentTypes.ReadVoxelModel(component).Model;
                if (string.Equals(model, primaryAssetPath, StringComparison.OrdinalIgnoreCase)) continue;
                sceneObject.Components.RemoveAt(componentIndex);
                removed++;
            }

            if (sceneObject.Components.Count == 0)
            {
                document.Objects.RemoveAt(objectIndex);
            }
        }

        return removed;
    }

    // 渲染视口 GPU 管线

    private VoxelRenderParameters.FrameState BuildFrameState(SceneCamera camera, Vector2 viewLogical,
        float deviceScale, int widthOverride, int heightOverride, bool pathMode, bool outlines,
        bool showGrid = false, float gridStep = 0F, bool scenePreview = false,
        VulkanVoxelRenderPipeline? sourcePipeline = null)
    {
        var framePipeline = sourcePipeline ?? _pipeline;
        NumVector3 Convert(Vector3 value) => new(value.X, value.Y, value.Z);
        var width = Math.Max(16, widthOverride);
        var height = Math.Max(16, heightOverride);
        var effectiveSettings = ResolveRenderSettings(GetActiveRenderCameraObject());
        return new VoxelRenderParameters.FrameState(
            Convert(camera.Position), Convert(camera.Forward), Convert(camera.Right), Convert(camera.Up),
            SceneProjection.FocalLength(viewLogical, camera) * deviceScale,
            width, height,
            // 实时渲染与场景预览是确定性结果，稳定帧号允许管线复用上一帧；
            // 路径追踪仍需帧号驱动样本去相关。
            FrameIndex: pathMode ? (int)(Environment.TickCount & 0x3FFFFFFF) : 0,
            framePipeline?.RegionMin ?? default,
            framePipeline?.RegionSize ?? default,
            framePipeline?.StepBudget ?? 8192,
            framePipeline?.EmissiveLightCount ?? 0,
            HuePhaseDegrees: effectiveSettings.HueCycle ? _elapsedSeconds * effectiveSettings.HueCycleSpeed % 360F : 0F,
            ShowOutlines: outlines,
            AccumulatedSamples: framePipeline?.AccumulatedSamples ?? 0,
            PathMode: pathMode,
            GridStep: gridStep,
            ShowGrid: showGrid,
            ScenePreview: scenePreview)
        {
            NearClip = camera.NearClip,
            FarClip = float.IsFinite(camera.FarClip) ? camera.FarClip : 100_000_000F,
        };
    }

    /// <summary>
    /// 只有光照、几何体和相机变化会使累积失效
    /// 后期处理变化直接重新呈现，不重启路径追踪
    /// </summary>
    private string TraceSignature(Vector2 size, float deviceScale)
    {
        var settings = ResolveRenderSettings(GetActiveRenderCameraObject());
        var buffer = new StringBuilder()
            .Append(size.X.ToString("F1")).Append('x').Append(size.Y.ToString("F1")).Append('@').Append(deviceScale.ToString("F2")).Append('|')
            .Append(_world.ContentVersion).Append('|')
            .Append(settings.SunAzimuthDegrees).Append(',').Append(settings.SunElevationDegrees).Append(',')
            .Append(settings.SunIntensity).Append(',').Append(settings.ShadowSoftness).Append(',').Append(settings.ShadowSamples)
            .Append(',').Append(settings.SunColor.X).Append(',').Append(settings.SunColor.Y).Append(',').Append(settings.SunColor.Z)
            .Append(',').Append(settings.SunEnabled).Append(',').Append(settings.ShowGround).Append(',').Append(settings.GroundFade)
            .Append(',').Append(settings.GroundColor.X).Append(',').Append(settings.GroundColor.Y).Append(',').Append(settings.GroundColor.Z)
            .Append(',').Append(settings.NearFade).Append(',').Append(settings.SkyTop.X).Append(',').Append(settings.SkyTop.Y)
            .Append(',').Append(settings.SkyTop.Z).Append(',').Append(settings.SkyHorizon.X).Append(',').Append(settings.SkyHorizon.Y)
            .Append(',').Append(settings.SkyHorizon.Z).Append(',').Append(settings.SkyAmbient).Append(',').Append(settings.AmbientTint)
            .Append(',').Append(settings.FogColor.X).Append(',').Append(settings.FogColor.Y).Append(',').Append(settings.FogColor.Z)
            .Append(',').Append(settings.FogDensity).Append(',').Append(settings.PathTracingEnabled)
            .Append(',').Append(settings.UseHardwareRayTracing)
            .Append(settings.MaxBounces).Append(',').Append(settings.PathRoughness).Append(',').Append(settings.PathMetalness)
            .Append(',').Append(settings.PathSpecularF0).Append(',').Append(settings.ResolutionScale)
            .Append(',').Append(settings.PathSamplesPerFrame).Append(',').Append(settings.PathMaxAccumulatedSamples)
            .Append(',').Append(settings.DepthOfField).Append(',').Append(settings.ApertureRadius).Append(',').Append(settings.FocusDistance)
            .Append(',').Append(settings.CameraFovDegrees).Append(',').Append(settings.EmissiveVoxels).Append(settings.EmissiveIntensity)
            .Append(',').Append(settings.DirectLightingMIS).Append(',').Append(settings.RussianRouletteThreshold)
            .Append(',').Append(settings.RadianceClamp).Append(',').Append(settings.TransmissiveAbsorption).Append(',').Append(settings.Backend)
            .Append(',').Append(settings.AmbientOcclusionEnabled).Append(',').Append(settings.AoStrength).Append(',').Append(settings.AoPower).Append(',').Append(settings.AoSamples)
            .Append(',').Append(settings.PaletteShading).Append(',').Append(settings.Saturation).Append(',').Append(settings.HueShiftDegrees)
            .Append(',').Append(settings.ColorContrast).Append(',').Append(settings.Brightness).Append(',').Append(settings.Tone)
            .Append(',').Append(settings.Exposure).Append(',').Append(settings.Gamma).Append(',').Append(settings.WhiteBalance)
            .Append(',').Append(settings.Vignette).Append(',').Append(settings.Dither).Append(',').Append(settings.PathDenoising)
            .Append(',').Append(settings.PathDenoiseStrength)
            .Append(',').Append(settings.AutoPerformanceScaling)
            .Append(',').Append(settings.BloomEnabled).Append(',').Append(settings.BloomIntensity)
            .Append(',').Append(settings.BloomThreshold).Append(',').Append(settings.BloomRadius)
            .Append(',').Append(settings.SceneShading)
            .Append(",lod:").Append(settings.SceneLod.Enabled).Append(';').Append(settings.SceneLod.LevelCount)
            .Append(':').Append(string.Join(",", settings.SceneLod.Distances))
            .Append("|rlod:").Append(settings.RenderLod.Enabled).Append(';').Append(settings.RenderLod.LevelCount)
            .Append(':').Append(string.Join(",", settings.RenderLod.Distances))
            .Append(',').Append(settings.SceneBackgroundColor.X).Append(',').Append(settings.SceneBackgroundColor.Y)
            .Append(',').Append(settings.SceneBackgroundColor.Z);
        if (GetActiveRenderCameraObject() is null)
        {
            buffer.Append("|scene:")
                .Append(_sceneOrbitYaw.ToString("F4")).Append(',')
                .Append(_sceneOrbitPitch.ToString("F4")).Append(',')
                .Append(_sceneCameraDistance.ToString("F3")).Append(',')
                .Append(_scenePan.X.ToString("F2")).Append(',').Append(_scenePan.Y.ToString("F2")).Append(',')
                .Append(_sceneFreeMoveHeight.ToString("F2")).Append(',')
                .Append(_sceneNearClip.ToString("F3")).Append(',').Append(_sceneFarClip.ToString("F1")).Append(',')
                .Append(_sceneFieldOfView.ToString("F1"));
        }
        else
        {
            buffer.Append('|').Append(RenderCameraSignature());
        }

        return buffer.ToString();
    }

    private readonly VoxelWorld _emptyScenePreviewWorld = new();

    /// <summary>
    /// 用 Vulkan Compute 绘制场景基础几何和无限网格，空场景也保留网格。
    /// </summary>
    private bool TryRenderGpuSceneView(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        bool showGeometry = true)
    {
        if (_project is null || _gpuRenderingDisabled) return false;
        try
        {
            _scenePipeline ??= new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
            var pipeline = _scenePipeline;
            var settings = CurrentSceneRenderSettings.CreateScenePreview();
            pipeline.SyncWorld(showGeometry ? GetSceneInspectionWorld(GetRenderWorld(uint.MaxValue)) : _emptyScenePreviewWorld, 0, settings.SceneLod,
                new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
                greedyMerge: false,
                includeInterior: IsCameraInsideOccupiedVoxel(camera));
            if (!pipeline.IsAvailable) return false;

            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var frame = BuildFrameState(camera, size, scale, width, height,
                pathMode: false, outlines: _showVoxelOutlines, scenePreview: true,
                showGrid: _showGrid, gridStep: 1F, sourcePipeline: pipeline);
            if (!pipeline.RenderSceneView(settings, frame, width, height)) return false;

            drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                Vector2.Zero, pipeline.DisplayUvMax);
            return true;
        }
        catch (Exception exception)
        {
            DisableGpuRendering(exception);
            return false;
        }
    }

    /// <summary>
    /// 只有相机实际进入实体体素时才恢复内部体素。
    /// 不能用世界 AABB 判断：大型地形的包围盒通常包含大量空气，
    /// 仅位于 AABB 内并不代表相机在模型内部，否则近距离观察会突然显示整块内部结构。
    /// 相机在实体外时仍使用强制内部体素剔除，渲染与导出路径始终保持剔除。
    /// </summary>
    private bool IsCameraInsideOccupiedVoxel(SceneCamera camera, VoxelWorld? world = null)
    {
        var coordinate = new VoxelCoord64(
            (long)MathF.Floor(camera.Position.X),
            (long)MathF.Floor(camera.Position.Y),
            (long)MathF.Floor(camera.Position.Z));
        return (world ?? _world).TryGet(coordinate, out var value) && !value.IsEmpty;
    }

    /// <summary>
    /// 绘制 Vulkan Compute 渲染标签页，GPU 不可用时返回 false
    /// </summary>
    private bool TryRenderGpuPathView(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var effective = ResolveRenderSettings(GetActiveRenderCameraObject());
        if (_gpuRenderingDisabled || effective.Backend == RenderBackend.Cpu) return false;
        if (_pipeline is null) InitializeVoxelPipeline();
        if (_pipeline is not { } pipeline) return false;
        try
        {
            pipeline.SyncWorld(GetRenderWorld(GetActiveRenderCullingMask()), 0, effective.RenderLod, new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z));
            // 首帧工作集是在渲染视图按需上传的，因此不能在 SyncWorld 之前用
            // IsAvailable 拦截；否则“尚未上传”会永远阻止上传，永久退回 CPU 预览。
            if (!pipeline.IsAvailable) return false;
            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var previewSettings = effective.CreateLargeScenePreview(pipeline.SyncTotalCells);
            var cameraNavigating = ImGui.IsMouseDragging(ImGuiMouseButton.Right) ||
                                   ImGui.IsMouseDragging(ImGuiMouseButton.Middle);
            var settingsInteracting = ImGui.IsAnyItemActive() && !cameraNavigating;
            // 相机交互也按完整视口像素渲染，不能通过缩小图像再放大换帧率。
            // 只有用户在渲染配置中主动调整的 ResolutionScale 可以改变内部尺寸。
            var renderScale = Math.Clamp(previewSettings.ResolutionScale, 0.25F, 1F);
            var width = Math.Max(16, (int)(size.X * scale * renderScale));
            var height = Math.Max(16, (int)(size.Y * scale * renderScale));
            var nowMs = Environment.TickCount64;
            var signature = TraceSignature(size, scale);
            if (signature != _accumulationSignature)
            {
                _accumulationSignature = signature;
                pipeline.ResetAccumulation();
            }

            // 实时帧是确定性的；参数未变时管线会直接复用上一张 GPU 图像，
            // 相机、场景或配置改变时仍会重绘。
            if (!effective.PathTracingEnabled)
            {
                // 环绕/平移相机时保持 AO、光照和后处理完整；仅编辑渲染控件时才启用轻量预览。
                var realtimeSettings = settingsInteracting ? previewSettings.CreateInteractionPreview() : previewSettings;
                var realtimeFrame = BuildFrameState(camera, size, scale, width, height,
                    pathMode: false, outlines: false);
                if (!pipeline.RenderSceneView(realtimeSettings, realtimeFrame, width, height))
                {
                    return false;
                }

                _lastRenderGpuSubmitMs = nowMs;
                _renderViewNeedsInitialRender = false;
                drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                    Vector2.Zero, pipeline.DisplayUvMax);
                return true;
            }

            // 调整渲染配置时可以短暂复用上一张纹理，避免拖动滑块同步等待完整路径追踪；
            // 相机导航绝不能走这条分支，否则新姿态刚写入就会被旧视角覆盖显示，
            // 表现为右键环绕后视角瞬间弹回。
            if (settingsInteracting && pipeline.DisplayTextureId != IntPtr.Zero &&
                nowMs - _lastRenderGpuSubmitMs < 33)
            {
                drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                    Vector2.Zero, pipeline.DisplayUvMax);
                return true;
            }
            // 相机移动同样使用完整路径追踪配置；不能因导航临时关闭照明或减少反弹。
            var renderSettings = settingsInteracting
                ? previewSettings.CreateInteractionPreview()
                : previewSettings;
            var frame = BuildFrameState(camera, size, scale, width, height, pathMode: true, outlines: false);
            // 首次进入渲染页没有可呈现的纹理，必须先提交一次，而不能直接走 Present。
            var firstSubmit = _renderViewNeedsInitialRender || _lastRenderGpuSubmitMs == 0 ||
                              pipeline.DisplayTextureId == IntPtr.Zero;
            // 是否采样只由当前项目配置决定，不能读取全局 UI 开关；
            // 首帧和路径追踪状态都必须直接驱动当前渲染提交。
            var wantTrace = (firstSubmit || effective.PathTracingEnabled) &&
                            pipeline.AccumulatedSamples < renderSettings.PathMaxAccumulatedSamples;
            if (wantTrace)
            {
                var submitted = pipeline.RenderPathView(renderSettings, frame, width, height);
                if (!submitted)
                {
                    return false;
                }
            }
            else if (!pipeline.Present(renderSettings, frame, width, height))
            {
                return false;
            }

            _lastRenderGpuSubmitMs = nowMs;
            if (wantTrace)
            {
                _renderViewNeedsInitialRender = false;
            }

            drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                Vector2.Zero, pipeline.DisplayUvMax);
            return true;
        }
        catch (Exception exception)
        {
            DisableGpuRendering(exception);
            return false;
        }
    }

    /// <summary>
    /// 记录 GPU 设备异常并安全切换到 CPU 预览
    /// </summary>
    /// <param name="exception">导致 GPU 管线不可用的异常</param>
    private void DisableGpuRendering(Exception exception)
    {
        if (_gpuRenderingDisabled) return;
        _gpuRenderingDisabled = true;
        _pipelineStatus = "显卡设备不可用，当前使用 CPU 预览";
        MVoxelLog.Error("Vulkan 体素渲染失败，已切换 CPU 预览", exception);
        try
        {
            _pipeline?.Dispose();
            _scenePipeline?.Dispose();
        }
        catch (Exception disposeException)
        {
            MVoxelLog.Error("释放故障 Vulkan 资源时发生异常", disposeException);
        }

        _pipeline = null;
        _scenePipeline = null;
    }

    // 体素笔刷工具

    private VoxelWorld? _pickWorld;
    private int _pickVersion;
    private NumVector3 _pickOrigin;
    private NumVector3 _pickDirection;
    private float _pickDistance;
    private VoxelDda.Hit? _pickResult;

    private VoxelDda.Hit? PickVoxel(Vector2 screenPixel, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var ray = SceneProjection.RayFromScreen(screenPixel, origin, size, camera);
        var origin3 = new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
        var direction3 = new NumVector3(ray.X, ray.Y, ray.Z);
        // 拾取应与场景实际显示一致：隐藏图层和切面后的体素不能抢占可见模型的命中。
        var visibleWorld = GetSceneInspectionWorld(GetRenderWorld(uint.MaxValue));
        var distance = _sceneCameraDistance * 8F + 256F;
        if (ReferenceEquals(_pickWorld, visibleWorld) && _pickVersion == visibleWorld.ContentVersion &&
            _pickOrigin == origin3 && _pickDirection == direction3 && _pickDistance == distance) return _pickResult;
        _pickWorld = visibleWorld;
        _pickVersion = visibleWorld.ContentVersion;
        _pickOrigin = origin3;
        _pickDirection = direction3;
        _pickDistance = distance;
        return _pickResult = VoxelDda.RayCast(visibleWorld, origin3, direction3, distance);
    }

    private void HandleVoxelToolInput(Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var io = ImGui.GetIO();
        if (_welcomeWindow?.IsOpen == true || _projectOperation.IsRunning || !IsFocused)
        {
            _brushPreview = null;
            return;
        }

        var mouse = io.MousePos;
        if (!ImGui.IsWindowHovered() || mouse.X < origin.X || mouse.X > origin.X + size.X ||
            mouse.Y < origin.Y || mouse.Y > origin.Y + size.Y)
        {
            _brushPreview = null;
            return;
        }

        var capsLockMode = IsCapsLockEnabled();
        var shiftPressed = IsShiftPressed();
        // 相机和选择是观察工具，绝不能因为默认左键动作是 Place 而修改体素。
        // 之前“相机/选择/擦除”按钮虽然能切换状态，却仍会落入同一套放置逻辑。
        if (_activeTool == EditorTool.Camera)
        {
            _brushPreview = null;
            return;
        }

        if (_activeTool is EditorTool.Pick or EditorTool.Eyedropper)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _selectedVoxel = PickVoxel(mouse, origin, size, camera);
                if (_selectedVoxel is { } pickedVoxel)
                {
                    _ = ResolveVoxelEditTarget(pickedVoxel.Coordinate);
                    if (_activeTool == EditorTool.Eyedropper && _world.TryGet(pickedVoxel.Coordinate, out var value))
                    {
                        _sampledBrushColor = value.Color;
                        _brushMaterialSlot = value.MaterialSlot;
                        _brushEmissive = (value.Flags & (byte)VoxelFlags.Emissive) != 0;
                        _activeTool = EditorTool.Draw;
                    }
                }
                SetStatus(_selectedVoxel is { } picked
                    ? $"已选择体素 ({picked.Coordinate.X}, {picked.Coordinate.Y}, {picked.Coordinate.Z})"
                    : "未点中体素");
            }

            return;
        }

        var leftAction = ViewportInputPolicy.Resolve(EditorMouseButton.Left, shiftPressed, capsLockMode,
            _editorSettings.McMode);
        var rightAction = ViewportInputPolicy.Resolve(EditorMouseButton.Right, shiftPressed, capsLockMode,
            _editorSettings.McMode);
        var hoverHit = PickVoxel(mouse, origin, size, camera);
        var primary = GetVoxelWorldObject()?.Id;
        var objects = _sceneDocument?.Objects;
        var selectedModelId = objects?.FirstOrDefault(item => item.Id == _selectedSceneObjectId &&
            item.Components.Any(SceneComponentTypes.IsVoxelModel))?.Id;
        var hoverTargetId = GetPinnedVoxelModelObject()?.Id ??
            (hoverHit is { } hovered && objects is not null
                ? VoxelEditOwnership.ResolveOwner(hovered.Coordinate, primary, objects, _layerOwnership)
                : selectedModelId ?? primary);
        var hoverTarget = objects?.FirstOrDefault(item => item.Id == hoverTargetId);
        var hoverErase = _activeTool == EditorTool.Erase || leftAction == ViewportMouseAction.Destroy;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{(hoverErase ? "擦除" : "绘制")}目标：{hoverTarget?.Name ?? "首次绘制自动创建模型"}{(hoverTarget?.Locked == true ? "（已锁定）" : "")}");
            if (hoverTarget is not null && (!hoverTarget.Visible ||
                !(_sceneDocument?.IsLayerVisible(hoverTarget.LayerIndex) ?? false) ||
                !IsSceneObjectAncestorsVisible(hoverTarget, objects!.ToDictionary(item => item.Id)) ||
                !hoverTarget.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) && component.Enabled)))
                ImGui.TextUnformatted("不可编辑：目标、父级、图层隐藏或模型组件禁用");
            if (hoverHit is { } location)
            {
                var coordinate = hoverErase ? location.Coordinate : location.Coordinate + location.Normal;
                ImGui.TextUnformatted($"格子：{coordinate.X}, {coordinate.Y}, {coordinate.Z}");
            }
            else ImGui.TextUnformatted(hoverErase ? "未命中可擦除方块" : "未命中模型；与网格平面相交时可起笔");
            ImGui.EndTooltip();
        }
        if (_activeTool is EditorTool.Line or EditorTool.Box &&
            HandleDragShapeToolInput(mouse, origin, size, camera, leftAction, rightAction))
        {
            return;
        }

        if (_activeTool == EditorTool.Fill &&
            HandleFillToolInput(mouse, origin, size, camera, leftAction, rightAction))
        {
            _brushPreview = null;
            return;
        }

        var handled = false;
        handled |= ApplyMouseVoxelAction(leftAction, ImGuiMouseButton.Left, mouse, origin, size, camera);
        handled |= ApplyMouseVoxelAction(rightAction, ImGuiMouseButton.Right, mouse, origin, size, camera);
        if (!handled)
        {
            _brushPreview = hoverHit;
            _brushPreviewErase = hoverErase;
        }
    }

    private bool ApplyMouseVoxelAction(ViewportMouseAction action, ImGuiMouseButton button,
        Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (action is ViewportMouseAction.None or ViewportMouseAction.Orbit ||
            !(ImGui.IsMouseClicked(button) || button == ImGuiMouseButton.Left &&
                ImGui.IsMouseDragging(button) && ImGui.GetIO().MouseDelta != Vector2.Zero))
        {
            return false;
        }

        var erase = _activeTool == EditorTool.Erase || action == ViewportMouseAction.Destroy;
        var hit = PickVoxel(mouse, origin, size, camera);
        var targetId = ResolveVoxelEditTarget(hit?.Coordinate);
        if (hit is null)
        {
            var ray = SceneProjection.RayFromScreen(mouse, origin, size, camera);
            var origin3 = new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
            var direction3 = new NumVector3(ray.X, ray.Y, ray.Z);
            if (!erase &&
                VoxelDda.TryGroundHit(origin3, direction3, _sceneCameraDistance * 8F + 256F, out var groundPoint))
            {
                var cells = VoxelBrush.CellsForGround(groundPoint, _brushShape, _brushRadius);
                CommitStroke(cells, erase: false, targetId);
            }
            else SetStatus(erase ? "未命中可擦除的体素" : "射线未命中模型或网格平面，请调整视角后绘制");

            _brushPreview = null;
            return true;
        }

        _brushPreview = hit;
        _brushPreviewErase = erase;
        ApplyStroke(hit.Value, erase, targetId);
        return true;
    }

    /// <summary>命中模型自动成为编辑目标；锁定模型时始终固定归属，不随命中切换。</summary>
    private StableId? ResolveVoxelEditTarget(VoxelCoord64? hitCoordinate)
    {
        if (GetPinnedVoxelModelObject() is { } pinned)
        {
            _selectedSceneObjectId = pinned.Id;
            return pinned.Id;
        }

        var primaryId = GetVoxelWorldObject()?.Id;
        var objects = _sceneDocument?.Objects;
        if (hitCoordinate is { } hit && objects is not null && _world.TryGet(hit, out _))
        {
            var owner = VoxelEditOwnership.ResolveOwner(hit, primaryId, objects, _layerOwnership);
            if (owner is { } ownerId && objects.Any(item => item.Id == ownerId))
            {
                _selectedSceneObjectId = ownerId;
                return ownerId;
            }
        }

        var selected = objects?.FirstOrDefault(item => item.Id == _selectedSceneObjectId &&
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
        return selected?.Id ?? primaryId;
    }

    private void ApplyStroke(VoxelDda.Hit hit, bool erase, StableId? targetId)
    {
        var radius = erase ? Math.Max(1, (_brushRadius + 1) / 2) : _brushRadius;
        var cells = erase
            ? VoxelBrush.CellsAround(hit.Coordinate, _brushShape, radius)
            : VoxelBrush.CellsForStroke(hit, erase: false, _brushShape, radius);
        CommitStroke(cells, erase, targetId);
    }

    private void CommitStroke(IReadOnlyList<VoxelCoord64> cells, bool erase, StableId? targetId)
    {
        if (cells.Count == 0) return;
        CommitCells(erase ? "擦除体素" : "绘制体素", cells, CurrentBrushValue(erase), targetId);
    }

    /// <summary>
    /// 按当前调色板与发光开关计算笔刷值
    /// </summary>
    private VoxelValue CurrentBrushValue(bool erase)
    {
        if (erase) return default;
        var paletteColor = _sampledBrushColor ?? _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
        var flags = (byte)((_brushEmissive ? VoxelFlags.Emissive : VoxelFlags.None) |
                           (paletteColor.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        return new VoxelValue(paletteColor, _brushMaterialSlot, flags);
    }

    /// <summary>
    /// 提交一组格子为一次可撤销事务。不在此处立即重建 GPU 工作集：
    /// 场景视图与渲染视图每帧各自按预算同步，同帧多笔与撤销连发自动合并，
    /// 避免拖动一次触发多次全量重建
    /// </summary>
    private void CommitCells(string name, IReadOnlyList<VoxelCoord64> cells, VoxelValue value,
        StableId? targetId = null)
    {
        if (cells.Count == 0) return;
        if (_project is null)
        {
            SetStatus("请先新建或打开项目");
            return;
        }
        if (!value.IsEmpty && GetVoxelWorldObject() is null) CreateVoxelModel();
        var primaryId = GetVoxelWorldObject()?.Id;
        var objects = _sceneDocument?.Objects ?? [];
        targetId ??= ResolveVoxelEditTarget(null);
        var target = objects.FirstOrDefault(item => item.Id == targetId);
        if (target is null || !target.Visible || !(_sceneDocument?.IsLayerVisible(target.LayerIndex) ?? false) ||
            !IsSceneObjectAncestorsVisible(target, objects.ToDictionary(item => item.Id)) ||
            !target.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) && component.Enabled))
        {
            SetStatus("编辑目标不可见或模型组件未启用；请显示目标对象及其图层后再编辑");
            return;
        }
        var editable = VoxelEditOwnership.FilterEditableCells(cells, _world, targetId, primaryId,
            objects, _layerOwnership, value.IsEmpty);
        if (editable.Count == 0)
        {
            SetStatus("当前格子属于其他体素模型，未修改；可选择该模型或解除编辑目标锁定");
            return;
        }

        VoxelEditOwnership.Change[] ownershipChanges = [];
        var command = new VoxelStrokeCommand(name, _world, editable, value,
            forward => VoxelEditOwnership.Apply(ownershipChanges, forward, primaryId, _layerOwnership));
        if (command.ChangedCellCount == 0)
        {
            SetStatus("此操作没有改变方块，撤销历史保持不变");
            return;
        }
        ownershipChanges = VoxelEditOwnership.Capture(command.Edits, targetId, primaryId, objects, _layerOwnership);
        _voxelHistory.Execute(command);
        _editingJournal?.Append(command.Name, command.Edits);
        SetStatus($"{name} · {target.Name} · {command.ChangedCellCount:N0} 格（可撤销）");

        _pipeline?.ResetAccumulation();
        MarkSceneDocumentDirty();
    }

    private void CreateVoxelModel()
    {
        if (_project is null) return;
        _sceneDocument ??= new SceneDocument { Name = "主场景" };
        var firstModel = GetVoxelWorldObject() is null;
        var name = "体素模型";
        for (var suffix = 2; _sceneDocument.Objects.Any(item => item.Name == name); suffix++)
            name = $"体素模型 {suffix}";
        var model = new SceneGraph(_sceneDocument).Add(name,
            transform: TransformComponent.FromGrid(GridTransform.Identity));
        var relative = firstModel ? "Assets/Worlds/主世界.mvmodel" : $"Assets/Worlds/模型_{model.Id}.mvmodel";
        model.Components.Add(SceneComponentTypes.CreateVoxelModel(relative, 0, "手动创建", name));
        if (!firstModel) _layerOwnership[model.Id] = new HashSet<VoxelCoord64>();
        _selectedSceneObjectId = model.Id;
        _composedRenderWorld = null;
        _activeTool = EditorTool.Draw;
        _showModelingTools = true;
        MarkSceneDocumentDirty();
        SetStatus($"已创建 {name}，可在网格上绘制；Ctrl+S 保存项目");
    }

    /// <summary>
    /// 直线与长方体工具的拖动状态机：按下取锚点，拖动更新终点预览，松开一次性提交
    /// </summary>
    private bool HandleDragShapeToolInput(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        ViewportMouseAction leftAction, ViewportMouseAction rightAction)
    {
        var place = leftAction == ViewportMouseAction.Place;
        var destroy = !place && rightAction == ViewportMouseAction.Destroy;
        if (!place && !destroy)
        {
            return false;
        }

        var button = place ? ImGuiMouseButton.Left : ImGuiMouseButton.Right;
        var ioState = ImGui.GetIO();
        if (ioState.MouseClicked[(int)button])
        {
            var hit = PickShapeSurface(mouse, origin, size, camera, destroy);
            if (hit is null)
            {
                return true;
            }

            _toolDragTargetId = ResolveVoxelEditTarget(hit.Value.Coordinate);
            var cell = destroy ? hit.Value.Coordinate : hit.Value.Coordinate + hit.Value.Normal;
            _toolDragAnchor = cell;
            _toolDragCurrent = cell;
            _toolDragErase = destroy;
            _brushPreview = null;
        }
        else if (ImGui.IsMouseDown(button) && _toolDragAnchor is not null)
        {
            var hit = PickShapeSurface(mouse, origin, size, camera, _toolDragErase);
            if (hit is not null)
            {
                _toolDragCurrent = _toolDragErase ? hit.Value.Coordinate : hit.Value.Coordinate + hit.Value.Normal;
            }
        }
        else if (ioState.MouseReleased[(int)button] && _toolDragAnchor is { } anchor)
        {
            var to = _toolDragCurrent ?? anchor;
            var cells = _activeTool == EditorTool.Line
                ? VoxelEditingTools.LineCells(anchor, to)
                : VoxelEditingTools.BoxCells(anchor, to, hollow: false);
            CommitCells(_activeTool == EditorTool.Line
                ? (_toolDragErase ? "擦除直线" : "绘制直线")
                : (_toolDragErase ? "擦除长方体" : "绘制长方体"), cells, CurrentBrushValue(_toolDragErase),
                _toolDragTargetId);
            _toolDragAnchor = null;
            _toolDragCurrent = null;
            _toolDragTargetId = null;
        }

        return true;
    }

    private VoxelDda.Hit? PickShapeSurface(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera, bool erase)
    {
        var hit = PickVoxel(mouse, origin, size, camera);
        if (hit is not null || erase) return hit;
        var ray = SceneProjection.RayFromScreen(mouse, origin, size, camera);
        if (!VoxelDda.TryGroundHit(new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
                new NumVector3(ray.X, ray.Y, ray.Z), _sceneCameraDistance * 8F + 256F, out var point)) return null;
        return new VoxelDda.Hit(new VoxelCoord64((long)MathF.Floor(point.X), -1, (long)MathF.Floor(point.Z)),
            new VoxelCoord64(0, 1, 0), 0F, point);
    }

    /// <summary>
    /// 洪水填充：点击起点格扩散同值连通域，命中预算上限时进入二次确认弹窗
    /// </summary>
    private bool HandleFillToolInput(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        ViewportMouseAction leftAction, ViewportMouseAction rightAction)
    {
        var place = leftAction == ViewportMouseAction.Place;
        var destroy = !place && rightAction == ViewportMouseAction.Destroy;
        if (!place && !destroy)
        {
            return false;
        }

        if (!ImGui.IsMouseClicked(place ? ImGuiMouseButton.Left : ImGuiMouseButton.Right))
        {
            return true;
        }

        var hit = PickVoxel(mouse, origin, size, camera);
        if (hit is null)
        {
            SetStatus("填充需要点中已有体素");
            return true;
        }

        var targetId = ResolveVoxelEditTarget(hit.Value.Coordinate);
        var cells = VoxelEditingTools.FloodFillCells(_world, hit.Value.Coordinate, MaximumFloodCells, out var truncated);
        var value = CurrentBrushValue(destroy);
        if (truncated)
        {
            _pendingFloodCells = cells;
            _pendingFloodValue = value;
            _pendingFloodTargetId = targetId;
            _pendingFloodOpen = true;
            SetStatus($"填充区域达到 {MaximumFloodCells:N0} 格上限，等待二次确认");
            return true;
        }

        CommitCells(destroy ? "擦除填充" : "洪水填充", cells, value, targetId);
        return true;
    }

    /// <summary>
    /// 单次洪水填充的最大格子数
    /// </summary>
    private const int MaximumFloodCells = 2_000_000;

    /// <summary>
    /// 重新加载项目 Materials 目录并上传材质槽表到 GPU
    /// </summary>
    private void ReloadMaterialTable()
    {
        if (_project is null) return;
        try
        {
            var materials = Project.MaterialAsset.LoadFromProject(_project);
            _brushMaterials = materials;
            if (!materials.Any(item => item.Slot == _brushMaterialSlot)) _brushMaterialSlot = 0;
            _pipeline?.SetMaterialTable(Project.MaterialAsset.PackTable(materials));
            if (materials.Count > 0)
                MVoxelLog.Info($"已加载 {materials.Count} 个材质槽定义");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("材质表加载失败", exception);
            SetStatus($"材质表加载失败：{exception.Message}");
        }
    }

    private Project.MaterialAsset? _editingMaterial;
    private ushort _brushMaterialSlot;
    private IReadOnlyList<MaterialAsset> _brushMaterials = Array.Empty<MaterialAsset>();
    private string _editingMaterialPath = string.Empty;

    /// <summary>
    /// 选中 .material 资产时在检查器绘制材质编辑面板，保存后即时重载 GPU 材质表
    /// </summary>
    private bool TryDrawSelectedMaterialInspector()
    {
        var relativePath = _assetBrowser.SelectedAssetPath;
        if (_project is null || !relativePath.EndsWith(".material", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return false;

        if (!string.Equals(_editingMaterialPath, path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _editingMaterial = Project.MaterialAsset.Load(path);
                _editingMaterialPath = path;
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                SetStatus($"材质文件读取失败：{exception.Message}");
                return true;
            }
        }

        var material = _editingMaterial!;
        ImGui.Text("材质");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        var name = material.Name;
        if (ImGui.InputText("名称", ref name, 48)) material.Name = name;
        var slot = material.Slot;
        if (ImGui.InputInt("槽位", ref slot)) material.Slot = Math.Clamp(slot, 0, Project.MaterialAsset.TableSlots - 1);

        DrawFollowableSlider("粗糙度", "粗糙度##mat", 0.01F, 1F, material.Roughness, value => material.Roughness = value);
        DrawFollowableSlider("金属度", "金属度##mat", 0F, 1F, material.Metallic, value => material.Metallic = value);
        DrawFollowableSlider("发光倍率", "发光倍率##mat", 0F, 32F, material.EmissiveScale, value => material.EmissiveScale = value);
        DrawFollowableSlider("吸收步长", "吸收步长##mat", 0F, 8F, material.AbsorptionStep, value => material.AbsorptionStep = value);

        var absorption = material.AbsorptionColor ?? new Rgba32(0, 0, 0);
        var absorptionColor = new System.Numerics.Vector3(absorption.R / 255F, absorption.G / 255F, absorption.B / 255F);
        var followAbsorption = material.AbsorptionColor is null;
        if (ImGui.Checkbox("吸收色跟随体素基础色", ref followAbsorption))
            material.AbsorptionColor = followAbsorption ? null : absorption;
        if (!followAbsorption && ImGui.ColorEdit3("吸收色", ref absorptionColor))
            material.AbsorptionColor = new Rgba32(
                (byte)Math.Round(Math.Clamp(absorptionColor.X, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(absorptionColor.Y, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(absorptionColor.Z, 0F, 1F) * 255F));

        ImGui.Spacing();
        if (ImGui.Button("保存材质", new Vector2(120F, 30F)))
        {
            try
            {
                material.Save(path);
                ReloadMaterialTable();
                SetStatus($"材质已保存并生效：{material.Name}（槽位 {material.Slot}）");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"材质保存失败：{exception.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("重新读取"))
        {
            _editingMaterial = null;
            _editingMaterialPath = string.Empty;
        }

        return true;
    }

    /// <summary>
    /// 选中 .hdr/.exr 环境资产时在检查器提供设为/清除环境贴图操作
    /// </summary>
    private bool TryDrawSelectedEnvironmentInspector()
    {
        var relativePath = _assetBrowser.SelectedAssetPath;
        if (_project is null ||
            !(relativePath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ||
              relativePath.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))) return false;
        var path = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return false;

        ImGui.Text("环境贴图");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"格式  {Path.GetExtension(path).ToUpperInvariant()[1..]}    大小  {new FileInfo(path).Length / 1024F:0.0} KB");
        if (ImGui.Button("设为环境贴图", new Vector2(150F, 30F)))
        {
            try
            {
                var pixels = Voxels.RadianceImage.Decode(path, out var envWidth, out var envHeight);
                _pipeline?.SetEnvironment(pixels, envWidth, envHeight);
                SetStatus($"环境贴图已生效：{envWidth}×{envHeight}");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                SetStatus($"环境贴图加载失败：{exception.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清除环境贴图", new Vector2(120F, 30F)))
        {
            _pipeline?.SetEnvironment([], 0, 0);
            SetStatus("已恢复程序化天空");
        }

        return true;
    }

    /// <summary>
    /// 绘制带“跟随全局”开关的浮点滑条
    /// </summary>
    private static void DrawFollowableSlider(string followLabel, string sliderLabel, float minimum, float maximum,
        float? current, Action<float> apply)
    {
        var follow = current is null;
        if (ImGui.Checkbox(followLabel + "跟随全局##follow", ref follow))
        {
            if (!follow) apply(Math.Clamp(current ?? (minimum + maximum) * 0.5F, minimum, maximum));
            else apply(float.NaN);
        }
        // apply(float.NaN) 由 Normalize 的 ClampOrFollow 转回跟随全局
        if (!follow)
        {
            var value = current ?? (minimum + maximum) * 0.5F;
            SetSettingsSliderWidth(sliderLabel);
            if (ImGui.SliderFloat(sliderLabel, ref value, minimum, maximum)) apply(value);
        }
    }

    private void HandleVoxelShortcuts()
    {
        var io = ImGui.GetIO();
        var scheme = _editorSettings.ActiveScheme();
        var undoDown = ShortcutBindingRouter.IsDown(scheme, "edit.undo");
        if (io.WantTextInput)
        {
            _undoHeldSeconds = 0F;
            _undoRepeatAccumulator = 0F;
            return;
        }
        else if (undoDown)
        {
            if (ShortcutBindingRouter.IsPressed(scheme, "edit.undo"))
            {
                ApplyUndo();
                _undoHeldSeconds = 0F;
                _undoRepeatAccumulator = 0F;
            }
            else
            {
                _undoHeldSeconds += Math.Max(0F, io.DeltaTime);
                var delay = _editorSettings.UndoRepeatDelayMs / 1000F;
                if (_undoHeldSeconds >= delay)
                {
                    _undoRepeatAccumulator += Math.Max(0F, io.DeltaTime);
                    var interval = _editorSettings.UndoRepeatIntervalMs / 1000F;
                    while (_undoRepeatAccumulator >= interval)
                    {
                        _undoRepeatAccumulator -= interval;
                        if (!ApplyUndo())
                        {
                            _undoRepeatAccumulator = 0F;
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            _undoHeldSeconds = 0F;
            _undoRepeatAccumulator = 0F;
            if (ShortcutBindingRouter.IsPressed(scheme, "edit.redo")) ApplyRedo();
        }

        if (ShortcutBindingRouter.IsPressed(scheme, "project.new")) ShowProjectPathDialog(create: true);
        if (ShortcutBindingRouter.IsPressed(scheme, "project.open")) ShowProjectPathDialog(create: false);
        if (ShortcutBindingRouter.IsPressed(scheme, "project.save")) SaveProject();
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.camera")) _activeTool = EditorTool.Camera;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.draw")) _activeTool = EditorTool.Draw;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.erase")) _activeTool = EditorTool.Erase;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.toggleGrid")) _showGrid = !_showGrid;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.togglePanels"))
        {
            var visible = _showHierarchy && _showProjectAssets && _showInspector;
            _showHierarchy = !visible;
            _showProjectAssets = !visible;
            _showInspector = !visible;
            _showModelingTools = !visible;
        }

        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.focus"))
        {
            var limits = GetSceneZoomLimits();
            _scenePan = Vector2.Zero;
            _sceneFreeMoveHeight = 0F;
            _sceneOrbitYaw = MathF.PI / 4F;
            _sceneOrbitPitch = 0.48F;
            _sceneCameraDistance = Math.Clamp(18F, limits.MinimumDistance, limits.MaximumDistance);
            _sceneZoom = 1F;
        }
    }

    /// <summary>
    /// 执行一次撤销；工作集由视图每帧按预算同步，这里只使累积失效
    /// </summary>
    private bool ApplyUndo()
    {
        if (_voxelHistory.PeekUndo() is VoxelStrokeCommand undoneStroke)
        {
            _editingJournal?.AppendInverse(undoneStroke.Name, undoneStroke.Edits);
        }

        if (!_voxelHistory.Undo()) return false;
        SetStatus($"已撤销（剩余 {_voxelHistory.UndoCount} 步）");
        _pipeline?.ResetAccumulation();
        MarkSceneDocumentDirty();
        return true;
    }

    /// <summary>
    /// 执行一次重做；工作集由视图每帧按预算同步，这里只使累积失效
    /// </summary>
    private bool ApplyRedo()
    {
        if (_voxelHistory.PeekRedo() is VoxelStrokeCommand redoneStroke)
        {
            _editingJournal?.Append(redoneStroke.Name, redoneStroke.Edits);
        }

        if (!_voxelHistory.Redo()) return false;
        SetStatus($"已重做（剩余 {_voxelHistory.RedoCount} 步）");
        _pipeline?.ResetAccumulation();
        MarkSceneDocumentDirty();
        return true;
    }

    private void DrawVoxelBrushPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_selectedVoxel is { } selected)
        {
            DrawCellWireframe(drawList, origin, size, camera, selected.Coordinate, 0.56F,
                ToPackedColor(Color.FromArgb(255, 255, 200, 60)), 2.2F);
        }

        if (_toolDragAnchor is { } anchor)
        {
            if (DrawCellWireframe(drawList, origin, size, camera, anchor, 0.52F,
                    ToPackedColor(Color.FromArgb(235, 93, 187, 255)), 1.6F))
            {
                var to = _toolDragCurrent ?? anchor;
                DrawCellWireframe(drawList, origin, size, camera, to, 0.52F,
                    ToPackedColor(Color.FromArgb(235, 93, 187, 255)), 1.6F);
            }
        }

        if (_brushPreview is not { } hit) return;
        var cell = _brushPreviewErase ? hit.Coordinate : hit.Coordinate + hit.Normal;
        DrawCellWireframe(drawList, origin, size, camera, cell, 0.52F,
            ToPackedColor(Color.FromArgb(_brushPreviewErase ? 210 : 235,
                _brushPreviewErase ? 255 : 93, _brushPreviewErase ? 112 : 187, _brushPreviewErase ? 120 : 255)), 1.6F);
    }

    /// <summary>
    /// 把一个格子画成线框立方体，全部角点都在相机后方时返回 false
    /// </summary>
    private static bool DrawCellWireframe(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        VoxelCoord64 cell, float half, uint color, float thickness)
    {
        var center = new Vector3((float)cell.X + 0.5F, (float)cell.Y + 0.5F, (float)cell.Z + 0.5F);
        var corners = new[]
        {
            center + new Vector3(-half, -half, -half), center + new Vector3(half, -half, -half),
            center + new Vector3(half, half, -half), center + new Vector3(-half, half, -half),
            center + new Vector3(-half, -half, half), center + new Vector3(half, -half, half),
            center + new Vector3(half, half, half), center + new Vector3(-half, half, half),
        };
        var points = new Vector2[8];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!SceneProjection.Project(corners[index], origin, size, camera, out points[index], out _)) return false;
        }

        foreach (var (start, end) in new[]
                 {
                     (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
                 })
        {
            drawList.AddLine(points[start], points[end], color, thickness);
        }

        return true;
    }

    // 工具栏和色板

    private void DrawBrushToolbarRow()
    {
        var previousTool = _activeTool;
        var tools = new (string Label, EditorTool Tool)[]
        {
            ("绘制", EditorTool.Draw), ("擦除", EditorTool.Erase), ("填充", EditorTool.Fill),
            ("选择", EditorTool.Pick), ("取色", EditorTool.Eyedropper), ("直线", EditorTool.Line),
            ("长方体", EditorTool.Box), ("相机", EditorTool.Camera),
        };
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var used = 0F;
        foreach (var (label, tool) in tools)
        {
            var width = Math.Max(62F, ImGui.CalcTextSize(label).X + 20F);
            if (used > 0F && used + ImGui.GetStyle().ItemSpacing.X + width <= rowWidth)
            {
                ImGui.SameLine();
                used += ImGui.GetStyle().ItemSpacing.X;
            }
            else used = 0F;
            ToolButton(label, tool);
            used += width;
        }
        DrawFloodConfirmModal();
        if (previousTool == _activeTool) return;
        SetStatus(_activeTool switch
        {
            EditorTool.Camera => "相机工具：左键不编辑 · 右键环绕 · 中键平移",
            EditorTool.Pick => "选择工具：点击体素查看坐标与颜色（可取色）",
            EditorTool.Eyedropper => "取色工具：点击体素取样颜色、材质槽与自发光标记",
            EditorTool.Draw => "绘制工具：左键放置 · Shift+左键破坏",
            EditorTool.Line => "直线工具：按下取起点，拖动到终点松开，一次可撤销",
            EditorTool.Box => "长方体工具：按下取一个角，拖到对角松开",
            EditorTool.Fill => "填充工具：点击体素替换其同值连通域，超上限需确认",
            _ => "擦除工具：左键擦除",
        });
    }

    /// <summary>
    /// 洪水填充命中预算上限后的二次确认弹窗，确认应用截断结果或放弃
    /// </summary>
    private void DrawFloodConfirmModal()
    {
        if (_pendingFloodCells is null) return;
        if (_pendingFloodOpen)
        {
            ImGui.OpenPopup("洪水填充达到上限##flood_confirm");
            _pendingFloodOpen = false;
        }

        var cellCount = _pendingFloodCells.Count;
        ImGui.SetNextWindowSize(new Vector2(420F, 0F));
        if (ImGui.BeginPopupModal("洪水填充达到上限##flood_confirm", ref _pendingFloodOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"填充区域达到 {MaximumFloodCells:N0} 格安全上限，结果已被截断。");
            ImGui.Text($"已收集 {cellCount:N0} 格，是否应用截断结果？");
            if (ImGui.Button("应用截断结果"))
            {
                CommitCells("洪水填充（截断）", _pendingFloodCells, _pendingFloodValue, _pendingFloodTargetId);
                _pendingFloodCells = null;
                _pendingFloodTargetId = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                _pendingFloodCells = null;
                _pendingFloodTargetId = null;
                SetStatus("已取消本次填充，未改动任何体素");
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void ToolButton(string label, EditorTool tool)
    {
        var active = _activeTool == tool;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.Accent));
        if (ImGui.Button(label, new Vector2(Math.Max(62F, ImGui.CalcTextSize(label).X + 20F), ImGui.GetFrameHeight())) && !active)
            _activeTool = tool;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tool switch
        {
            EditorTool.Camera => "观察：左键不修改体素，右键环绕，中键平移",
            EditorTool.Pick => "选择：点击体素查看坐标和颜色",
            EditorTool.Eyedropper => "取色：采样体素的精确颜色、材质槽和自发光标记，再返回绘制",
            EditorTool.Draw => "绘制：左键放置，Shift+左键擦除；右键环绕（Caps Lock 模式除外）",
            EditorTool.Erase => "擦除：左键擦除体素；右键仍用于环绕",
            EditorTool.Line => "直线：拖动绘制或擦除一条直线",
            EditorTool.Box => "长方体：拖动绘制或擦除实心长方体",
            EditorTool.Fill => "填充：替换同值连通区域",
            _ => label,
        });
        if (active) ImGui.PopStyleColor();
    }

    private void DrawPaletteSelector()
    {
        var available = ImGui.GetContentRegionAvail().X;
        var minimumCell = Math.Max(14F, ImGui.GetTextLineHeight());
        var columns = Math.Clamp((int)((available + 1F) / (minimumCell + 1F)), 1, 16);
        var cellSize = Math.Max(4F, Math.Min(26F, (available - (columns - 1)) / columns));
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < VoxelPalette.EntryCount; index++)
        {
            if (index % columns != 0) ImGui.SameLine(0, 1);
            ImGui.PushID(index);
            if (ImGui.InvisibleButton("##色板格", new Vector2(cellSize, cellSize)))
            {
                _paletteIndex = index;
                _sampledBrushColor = null;
                var picked = _palette[index];
                _brushEmissive = false;
                SetStatus($"画笔颜色 #{index} · RGB({picked.R},{picked.G},{picked.B}) · A{picked.A}");
            }

            ImGui.PopID();
            if (ImGui.IsItemHovered())
            {
                var swatch = _palette[index];
                ImGui.SetTooltip($"颜色 {index} · RGB({swatch.R}, {swatch.G}, {swatch.B}) · A {swatch.A}");
            }
            var position = ImGui.GetItemRectMin();
            var color = _palette[index];
            var min = position + new Vector2(1F, 1F);
            var max = position + new Vector2(cellSize - 1F, cellSize - 1F);
            if (index == _paletteIndex)
            {
                draw.AddRectFilled(position, position + new Vector2(cellSize, cellSize), ToPackedColor(EditorPalette.Accent), 3F);
            }

            if (color.A < 255)
            {
                draw.AddRectFilled(min, max, ToPackedColor(Color.FromArgb(255, 46, 52, 62)), 2F);
                draw.AddRectFilled(min, (min + max) * 0.5F, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
                draw.AddRectFilled((min + max) * 0.5F, max, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
            }
            else
            {
                draw.AddRectFilled(min, max, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
            }
        }
    }

    // 设置面板

    private void DrawRenderSettingsPanel()
    {
        var settings = GetEditableRenderSettings();
        var before = settings.ToJson();

        if (_pipeline is not { IsAvailable: true })
        {
            var message = _pipeline?.GpuWorksetUploaded == true
                ? "显卡体素工作集已上传，显卡绘制管线仍在初始化，当前使用 CPU 预览。"
                : "显卡体素工作集尚未上传，当前使用 CPU 预览。";
            ImGui.TextColored(ToColor(EditorPalette.Error), message);
            if (!string.IsNullOrEmpty(_pipelineStatus)) ImGui.TextWrapped(_pipelineStatus);
        }

        if (ImGui.Button("恢复默认参数", new Vector2(138F, 30F)))
        {
            var defaults = new RenderSettings();
            if (_project is null)
                _editorSettings.RenderViewSettings = defaults;
            else
            {
                _projectSettings.RenderSettings = defaults;
                _projectSettings.DefaultRenderPreset = "默认";
            }

            _renderSettings = defaults;
            settings = defaults;

            SyncSettingsToggles(settings);
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            SetStatus("已恢复默认渲染参数");
        }

        DrawRenderSettingsAccordion("lighting", "光照与阴影", () => DrawLightingSection(settings));
        DrawRenderSettingsAccordion("environment", "环境与地面", () => DrawEnvironmentSection(settings));
        DrawRenderSettingsAccordion("palette", "基础色与色相", () => DrawPaletteSection(settings));
        DrawRenderSettingsAccordion("color", "色彩输出", () => DrawPostSection(settings));
        DrawRenderSettingsAccordion("path", "路径追踪", () => DrawPathTraceSection(settings));
        DrawRenderSettingsAccordion("material", "全局材质", () => DrawGlobalMaterialSection(settings));
        DrawRenderSettingsAccordion("camera", "相机", () => DrawCameraSection(settings));
        DrawRenderSettingsAccordion("lod", "LOD", () => DrawLodSection(settings));
        DrawRenderSettingsAccordion("performance", "性能", () => DrawPerformanceSection(settings));
        SaveEditableRenderSettings(settings, before);
    }

    /// <summary>
    /// 渲染配置的纵向折叠分组：避免窄面板中的标签被截断，且允许用户只展开当前职责。
    /// </summary>
    private void DrawRenderSettingsAccordion(string id, string title, Action drawContent)
    {
        // v2 ID deliberately avoids restoring the old layout's open state. Every
        // render-settings group therefore starts collapsed after this UI redesign.
        ImGui.PushID($"render-accordion-v2-{id}");
        var flags = ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (string.Equals(_requestedRenderSettingsSection, id, StringComparison.Ordinal))
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        var open = ImGui.CollapsingHeader(title, flags);
        if (open)
        {
            ImGui.Indent(8F);
            drawContent();
            ImGui.Unindent(8F);
        }

        ImGui.PopID();
        if (string.Equals(_requestedRenderSettingsSection, id, StringComparison.Ordinal))
            _requestedRenderSettingsSection = null;
    }

    private static void DrawColorSlider(string label, ref NumVector3 linearColor)
    {
        var display = ToSrgbVector(linearColor);
        if (ImGui.ColorEdit3(label, ref display, ImGuiColorEditFlags.Float))
        {
            linearColor = ToLinearVector(display);
        }
    }

    private static NumVector3 ToSrgbVector(NumVector3 linear) => new(
        LinearToSrgbComponent(Math.Clamp(linear.X, 0F, 4F)),
        LinearToSrgbComponent(Math.Clamp(linear.Y, 0F, 4F)),
        LinearToSrgbComponent(Math.Clamp(linear.Z, 0F, 4F)));

    private static NumVector3 ToLinearVector(NumVector3 srgb) => new(
        SrgbToLinearComponent(Math.Clamp(srgb.X, 0F, 1F)),
        SrgbToLinearComponent(Math.Clamp(srgb.Y, 0F, 1F)),
        SrgbToLinearComponent(Math.Clamp(srgb.Z, 0F, 1F)));

    private static float LinearToSrgbComponent(float value) =>
        value <= 0.0031308F ? value * 12.92F : 1.055F * MathF.Pow(value, 1F / 2.4F) - 0.055F;

    private static float SrgbToLinearComponent(float value) =>
        value <= 0.04045F ? value / 12.92F : MathF.Pow((value + 0.055F) / 1.055F, 2.4F);

    private void DrawSceneSection(RenderSettings settings)
    {
        DrawLightingSection(settings);
        DrawEnvironmentSection(settings);
    }

    private void DrawLightingSection(RenderSettings settings)
    {
        ImGui.SeparatorText("太阳与阴影");
        Toggle("太阳平行光", ref _settingsToggleSun, value => settings.SunEnabled = value);
        SliderFloat("方位角##sun", ref settings.SunAzimuthDegrees, 0F, 360F, "%.0f°");
        SliderFloat("仰角##sun", ref settings.SunElevationDegrees, 2F, 90F, "%.0f°");
        SliderFloat("强度##sun", ref settings.SunIntensity, 0F, 6F, "%.2f");
        DrawColorSlider("颜色##sun", ref settings.SunColor);
        SliderFloat("阴影柔和度（半影锥）##sun", ref settings.ShadowSoftness, 0F, 0.3F, "%.3f");
        SliderInt("阴影采样数##sun", ref settings.ShadowSamples, 1, 8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每像素阴影射线数。较低值性能更好，较高值可降低半影噪点；默认值偏向性能。");
        if (ImGui.CollapsingHeader("接触阴影 AO"))
        {
            Toggle("角点环境光遮蔽", ref _settingsToggleAo, value => settings.AmbientOcclusionEnabled = value);
            SliderFloat("AO 强度", ref settings.AoStrength, 0F, 1F, "%.2f");
            SliderFloat("AO 对比", ref settings.AoPower, 0.3F, 3F, "%.2f");
            SliderInt("AO 射线采样数", ref settings.AoSamples, 4, 64);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("每个可见表面像素发出的 AO 射线数；更多采样更平滑但更耗时。默认 16，优先保证性能。");
        }

    }

    private void DrawEnvironmentSection(RenderSettings settings)
    {
        ImGui.SeparatorText("天空与地面");
        Toggle("显示地面平面", ref _settingsToggleGround, value => settings.ShowGround = value);
        DrawColorSlider("地面颜色", ref settings.GroundColor);
        SliderFloat("地面地平线过渡（0=纯色无限地面）", ref settings.GroundFade, 0F, 1024F, "%.0f");
        DrawColorSlider("天顶色", ref settings.SkyTop);
        DrawColorSlider("地平线色", ref settings.SkyHorizon);
        SliderFloat("天空环境光##sky", ref settings.SkyAmbient, 0F, 1.5F, "%.2f");
        SliderFloat("暖色补光##sky", ref settings.AmbientTint, 0F, 0.8F, "%.2f");
        ImGui.SeparatorText("雾与景深提示");
        DrawColorSlider("雾颜色", ref settings.FogColor);
        SliderFloat("雾浓度（0=关）", ref settings.FogDensity, 0F, 0.02F, "%.4f");
        SliderFloat("近处淡入距离（0=关）", ref settings.NearFade, 0F, 256F, "%.0f");
    }

    private void DrawPaletteSection(RenderSettings settings)
    {
        ImGui.SeparatorText("基础色与色相");
        Toggle("调色板着色", ref _settingsTogglePalette, value => settings.PaletteShading = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("体素使用纯色，基础色来自当前调色板，不使用贴图");
        SliderFloat("饱和度", ref settings.Saturation, 0F, 2.5F, "%.2f");
        SliderFloat("色相偏移", ref settings.HueShiftDegrees, 0F, 360F, "%.0f°");
        SliderFloat("对比度", ref settings.ColorContrast, 0.3F, 2.5F, "%.2f");
        SliderFloat("明度", ref settings.Brightness, 0.2F, 2F, "%.2f");
        Toggle("色相环动画", ref _settingsToggleHueCycle, value => settings.HueCycle = value);
        SliderFloat("色相环速度##cycle", ref settings.HueCycleSpeed, 0F, 90F, "%.0f°/秒");
    }

    private void DrawBrushProperties()
    {
        var selectedMaterial = _brushMaterials.FirstOrDefault(item => item.Slot == _brushMaterialSlot);
        if (ImGui.BeginCombo("笔刷材质", selectedMaterial?.Name ?? "默认材质（槽 0）"))
        {
            if (ImGui.Selectable("默认材质（槽 0）", _brushMaterialSlot == 0)) _brushMaterialSlot = 0;
            foreach (var material in _brushMaterials)
                if (ImGui.Selectable($"{material.Name}（槽 {material.Slot}）", _brushMaterialSlot == material.Slot))
                    _brushMaterialSlot = (ushort)material.Slot;
            ImGui.EndCombo();
        }
        var shape = _brushShape == VoxelBrushShape.Cube ? "立方" : "球";
        if (ImGui.BeginCombo("形状", shape))
        {
            foreach (var (label, value) in new[] { ("立方", VoxelBrushShape.Cube), ("球", VoxelBrushShape.Sphere) })
            {
                if (ImGui.Selectable(label, _brushShape == value)) _brushShape = value;
            }

            ImGui.EndCombo();
        }

        SliderInt("半径（1=单格）", ref _brushRadius, 1, 8);
        if (ImGui.Checkbox("自发光笔刷（提灯）", ref _brushEmissive))
        {
            SetStatus(_brushEmissive ? "新绘制的体素将带自发光标记" : "新绘制的体素为普通颜色");
        }

        ImGui.SeparatorText("色板");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_paletteIsProjectLevel
            ? $"项目调色板：{_selectedProjectPalettePath}" : "编辑器默认调色板");
        DrawPaletteSelector();
        if (_project is not null)
        {
            if (ImGui.Button("在项目设置中编辑调色板", new Vector2(-1F, 30F)))
                OpenSettings(1, 3);
        }
    }

    private void DrawPostSection(RenderSettings settings)
    {
        ImGui.SeparatorText("色调映射");
        var tone = (int)settings.Tone;
        if (ImGui.Combo("色调曲线", ref tone, "线性截断\0Reinhard\0ACES（Narkowicz）\0Filmic（HV）\0", 4))
        {
            settings.Tone = (ToneCurve)Math.Clamp(tone, 0, 3);
        }

        SliderFloat("曝光（EV）", ref settings.Exposure, -4F, 4F, "%.2f");
        SliderFloat("Gamma", ref settings.Gamma, 1.0F, 3.5F, "%.2f");
        SliderFloat("白平衡色温（K）", ref settings.WhiteBalance, 2000F, 12000F, "%.0f K");
        SliderFloat("暗角", ref settings.Vignette, 0F, 1F, "%.2f");
        Toggle("输出抖动（消除色带）", ref _settingsToggleDither, value => settings.Dither = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("体素颜色按 sRGB 存储，渲染计算在线性空间进行");
    }

    private void DrawPathTraceSection(RenderSettings settings)
    {
        ImGui.SeparatorText("渐进式路径追踪");
        Toggle("启用路径追踪渲染", ref _settingsTogglePath, value =>
        {
            settings.PathTracingEnabled = value;
        });
        SliderInt("每帧样本数", ref settings.PathSamplesPerFrame, 1, 8);
        SliderInt("累积样本上限", ref settings.PathMaxAccumulatedSamples, 64, 8192, "%d spp");
        SliderFloat("内部分辨率倍率", ref settings.ResolutionScale, 0.25F, 1F, "%.2f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("仅在你主动调整时改变路径追踪视口分辨率；1.00 为完整窗口分辨率。相机交互和大型场景优化不会自动降低分辨率。");
        SliderInt("最大弹射次数", ref settings.MaxBounces, 1, 12);
        SliderFloat("光线终止阈值（RR）", ref settings.RussianRouletteThreshold, 0.0005F, 0.05F, "%.4f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("路径权重低于此值（约 1/255，低于输出 LSB）时提前终止并做无偏存活判定。");
        SliderFloat("辐射度截断（消防萤火虫）", ref settings.RadianceClamp, 1F, 200F, "%.1f");
        Toggle("快速降噪（渲染视图）", ref _settingsTogglePathDenoise, value => settings.PathDenoising = value);
        SliderFloat("降噪强度", ref settings.PathDenoiseStrength, 0F, 1F, "%.2f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在呈现通道对累积结果做保边缘邻域滤波，不增加路径追踪样本，导出画面也会使用该设置。");
    }

    private void DrawGlobalMaterialSection(RenderSettings settings)
    {
        ImGui.SeparatorText("全局材质（GGX）");
        SliderFloat("粗糙度（GGX）", ref settings.PathRoughness, 0.01F, 1F, "%.3f");
        SliderFloat("金属度", ref settings.PathMetalness, 0F, 1F, "%.2f");
        SliderFloat("介电基础反射 F0", ref settings.PathSpecularF0, 0F, 0.2F, "%.3f");
        SliderFloat("半透明吸收浓度", ref settings.TransmissiveAbsorption, 0F, 6F, "%.2f");
        SliderFloat("自发光强度（灯）", ref settings.EmissiveIntensity, 0F, 40F, "%.1f");
        Toggle("自发光体素参与光照", ref _settingsToggleEmissive, value => settings.EmissiveVoxels = value);
        Toggle("直接光 MIS 采样（太阳+灯）", ref _settingsToggleMis, value => settings.DirectLightingMIS = value);
    }

    private void DrawCameraSection(RenderSettings settings)
    {
        ImGui.SeparatorText("渲染相机");
        SliderFloat("视场角 FOV##path", ref settings.CameraFovDegrees, 20F, 120F, "%.0f°");
        Toggle("景深", ref _settingsToggleDof, value => settings.DepthOfField = value);
        SliderFloat("光圈半径##dof", ref settings.ApertureRadius, 0F, 0.5F, "%.3f");
        SliderFloat("对焦距离##dof", ref settings.FocusDistance, 1F, 256F, "%.1f");
    }

    private void DrawPerformanceSection(RenderSettings settings)
    {
        ImGui.SeparatorText("大型场景视口优化");
        Toggle("自动性能优化（仅视口）", ref _settingsToggleAutoPerformance,
            value => settings.AutoPerformanceScaling = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只限制大型场景的实时 AO、阴影采样；不会降低场景窗口或渲染窗口的像素分辨率，也不会影响导出质量。");
        ImGui.TextWrapped("保持完整像素分辨率；大型工作集自动限制实时 AO/阴影采样，超过 70 万格时关闭实时 AO。相机交互不使用低分辨率放大。");
    }

    /// <summary>
    /// 绘制距离 LOD 配置区：场景窗口与渲染窗口各一份，默认关闭，
    /// 可调整级数与每级生效距离
    /// </summary>
    /// <param name="settings">目标渲染设置</param>
    private void DrawLodSection(RenderSettings settings)
    {
        ImGui.SeparatorText("距离 LOD（场景窗口与渲染窗口各一份，默认关闭）");
        DrawLodGroup("场景窗口", settings.SceneLod);
        DrawLodGroup("渲染窗口", settings.RenderLod);
    }

    /// <summary>
    /// 绘制单窗口 LOD 分组：开关、级数与逐级起始距离
    /// </summary>
    private static void DrawLodGroup(string label, VoxelLodSettings lod)
    {
        var enabled = lod.Enabled;
        if (ImGui.Checkbox($"{label}启用 LOD##lodEnabled{label}", ref enabled))
        {
            lod.Enabled = enabled;
        }

        if (!enabled)
        {
            return;
        }

        var levels = lod.LevelCount;
        if (ImGui.SliderInt($"{label}级数##lodCount{label}", ref levels, 1, VoxelLodSettings.MaximumLevels))
        {
            lod.LevelCount = levels;
            lod.ClampAll();
        }

        for (var index = 0; index < Math.Max(lod.LevelCount - 1, 0); index++)
        {
            var value = index < lod.Distances.Length ? lod.Distances[index] : 64F * (index + 1);
            var sliderLabel = $"{label}第 {index + 1} 级起始距离##lodDist{label}{index}";
            SetSettingsSliderWidth(sliderLabel);
            if (ImGui.DragFloat(sliderLabel, ref value,
                    1F, VoxelLodSettings.MinimumDistance, 100_000F, "%.0f 格"))
            {
                var distances = (float[])lod.Distances.Clone();
                Array.Resize(ref distances, Math.Max(distances.Length, Math.Max(lod.LevelCount - 1, 0)));
                distances[index] = value;
                lod.Distances = distances;
                lod.ClampAll();
            }
        }
    }

    private static void SliderFloat(string label, ref float value, float minimum, float maximum, string format)
    {
        SetSettingsSliderWidth(label);
        ImGui.SliderFloat(label, ref value, minimum, maximum, format);
    }

    private static void SliderInt(string label, ref int value, int minimum, int maximum)
    {
        SetSettingsSliderWidth(label);
        ImGui.SliderInt(label, ref value, minimum, maximum);
    }

    private static void SliderInt(string label, ref int value, int minimum, int maximum, string format)
    {
        SetSettingsSliderWidth(label);
        ImGui.SliderInt(label, ref value, minimum, maximum, format);
    }

    /// <summary>
    /// 为配置面板中的滑动条按当前可用宽度布局：先为右侧标签文本预留空间，
    /// 再把剩余空间分配给滑动条。这样长标签不会被滑动条挤出子窗口。
    /// </summary>
    private static void SetSettingsSliderWidth(string label = "")
    {
        const float maxWidth = 320F;
        const float rightMargin = 6F;
        var available = MathF.Max(1F, ImGui.GetContentRegionAvail().X);
        var visibleLabel = label;
        var labelSeparator = visibleLabel.IndexOf("##", StringComparison.Ordinal);
        if (labelSeparator >= 0) visibleLabel = visibleLabel[..labelSeparator];
        var labelWidth = visibleLabel.Length == 0
            ? 0F
            : ImGui.CalcTextSize(visibleLabel).X + ImGui.GetStyle().ItemInnerSpacing.X;
        var sliderWidth = MathF.Max(1F, available - labelWidth - rightMargin);
        ImGui.SetNextItemWidth(MathF.Min(maxWidth, sliderWidth));
    }

    private static void Toggle(string label, ref bool storage, Action<bool> apply)
    {
        if (ImGui.Checkbox(label, ref storage)) apply(storage);
    }
}
