using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Editor.Settings;
using MVoxel.Editor.Templates;
using MVoxel.Editor.Assets;
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
    // 设备丢失后的重建次数上限，连续失败时保持 CPU 预览不再反复申请设备
    private const int MaxVulkanRecoveryAttempts = 2;
    private int _vulkanRecoveryAttempts;
    private bool _worldLoadFailed;
    private readonly ObjectVoxelStore _objectVoxels = new();
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
    private bool _hollowBox;
    private VoxelCoord64? _previewShapeAnchor;
    private VoxelCoord64? _previewShapeEnd;
    private EditorTool _previewShapeTool;
    private bool _previewShapeHollow;
    private IReadOnlyList<VoxelCoord64> _previewShapeCells = [];
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
        using var initPhase = StartupTimeline.Begin("首帧·创建渲染管线");
        _gpuRenderingDisabled = false;
        _pipelineStatus = "显卡体素管线正在初始化";
        try
        {
            RefreshRuntimeMaterialTable();
            _pipeline = new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
            _pipeline.SetMaterialTable(Project.MaterialAsset.PackTable(_runtimeMaterials));
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
        _settingsToggleMis = settings.SunDirectLighting;
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

    /// <summary>
    /// 设备丢失后重建窗口 Vulkan 上下文与 ImGui 渲染后端
    /// </summary>
    /// <remarks>
    /// 设备丢失时交换链已经无法再呈现，只切换 CPU 预览会让整个编辑器窗口停止刷新
    /// 因此这里直接重建 Vulkan 设备、ImGui 后端和缩略图缓存，让视图继续可用
    /// 旧设备上的原生对象不再逐个销毁，避免在故障设备上再次触发驱动异常
    /// </remarks>
    /// <returns>完成重建返回 true</returns>
    private bool RecoverVulkanDeviceIfNeeded()
    {
        if (_vulkan is null || !_vulkan.IsDeviceLost)
        {
            _vulkanRecoveryAttempts = 0;
            return false;
        }

        // 连续失败时不再尝试，避免在不可用的驱动状态里反复申请设备
        if (_vulkanRecoveryAttempts >= MaxVulkanRecoveryAttempts)
        {
            return false;
        }

        _vulkanRecoveryAttempts++;
        MVoxelLog.Warn($"检测到 Vulkan 设备丢失，重建渲染设备（第 {_vulkanRecoveryAttempts} 次）");
        try
        {
            _pipeline = null;
            _scenePipeline = null;
            _exportPipeline = null;
            _imguiRenderer = null;
            _thumbnailCache = null;
            _assetPreviewTextureId = 0;
            _pipelineInitializationAttempted = false;
            var lostContext = _vulkan;
            _vulkan = null;
            lostContext.Abandon();

            var context = new VulkanWindowContext(this);
            _vulkan = context;
            _imguiRenderer = new VulkanImGuiRenderer(context, _controller!);
            _thumbnailCache = new ThumbnailCache(_imguiRenderer);
            _assetBrowser.Thumbnails = _thumbnailCache;
            _gpuRenderingDisabled = false;
            _pipelineStatus = string.Empty;
            _accumulationSignature = string.Empty;
            _renderViewNeedsInitialRender = true;
            _lastRenderGpuSubmitMs = 0;
            MVoxelLog.Info("Vulkan 渲染设备已重建，视图继续使用显卡渲染");
            return true;
        }
        catch (Exception exception)
        {
            _gpuRenderingDisabled = true;
            _pipelineStatus = "显卡设备不可用，当前使用 CPU 预览";
            MVoxelLog.Error("重建 Vulkan 渲染设备失败", exception);
            return false;
        }
    }

    internal readonly record struct ModelLoadKey(string Path, VoxelCoord64 Origin);

    private sealed record PreparedProjectLoad(ProjectHandle Project, PreloadedModels? Models);

    private sealed record PreparedOpenProject(ProjectHandle Project, PreloadedModels Models,
        RecoverySnapshot? Snapshot, IReadOnlyList<MaterialAsset>? Materials,
        PluginScriptRuntime Runtime) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }

    internal sealed record PreloadedModels(string ScenePath, Scene.SceneDocument? Scene,
        Dictionary<StableId, VoxelWorld> ObjectWorlds, VoxelWorld? Composed, bool LoadFailed,
        bool HasBounds, VoxelCoord64 Minimum, VoxelCoord64 Maximum,
        RuntimeMaterialMap? MaterialMap = null, VoxelWorld? ViewComposed = null,
        IReadOnlyList<MaterialAsset>? Materials = null);

    /// <summary>
    /// 一个场景对象对模型资产的读取请求
    /// </summary>
    private readonly record struct ModelRequest(StableId ObjectId, string Name, ModelLoadKey Key);

    /// <summary>
    /// 按资产路径去重后读取对象体素
    /// </summary>
    /// <remarks>
    /// 模型文件只保存局部坐标，同一份资产常被多个场景对象引用（街区、屋顶、树木）；
    /// 原实现按“路径加原点”做缓存键，导致同一个文件被重复解码几十次。
    /// 这里改为每个资产解析一次，其余对象按原点差平移复制，并按资产并行
    /// </remarks>
    /// <param name="requests">对象的读取请求</param>
    /// <param name="worlds">输出的对象标识到体素世界的映射</param>
    /// <param name="cache">跨场景复用的模型缓存，键为路径与原点的组合</param>
    /// <param name="cancellation">取消令牌</param>
    /// <param name="report">进度回调</param>
    /// <param name="markFailed">出现单个对象失败时上报</param>
    /// <param name="handoff">模板写盘时登记的内存世界，命中时不必读盘</param>
    private static void LoadObjectWorlds(List<ModelRequest> requests,
        Dictionary<StableId, VoxelWorld> worlds, Dictionary<ModelLoadKey, VoxelWorld> cache,
        CancellationToken cancellation, Action<BlockingOperationProgress> report, Action markFailed,
        IReadOnlyDictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff = null)
    {
        if (requests.Count == 0) return;
        var materialized = new VoxelWorld?[requests.Count];
        // 精确命中缓存（路径与原点都一致）不付解析成本，留到后面的串行复制
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < requests.Count; index++)
        {
            var key = requests[index].Key;
            if (cache.ContainsKey(key)) continue;
            if (!groups.TryGetValue(key.Path, out var group)) groups.Add(key.Path, group = []);
            group.Add(index);
        }

        var totalGroups = Math.Max(1, groups.Count);
        var completedGroups = 0;
        var progressGate = new object();
        try
        {
            Parallel.ForEach(groups, new ParallelOptions
            {
                CancellationToken = cancellation,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 2, 2, 8),
            }, entry =>
            {
                var group = entry.Value;
                try
                {
                    // 每个资产只解析一次，同组其余对象在同一线程内位移复制，
                    // 组与组之间没有共享可变状态，只有进度计数需要加锁
                    var first = requests[group[0]];
                    var canonical = ResolveAssetWorld(first.Key, handoff, cancellation);
                    materialized[group[0]] = canonical;
                    for (var position = 1; position < group.Count; position++)
                    {
                        var request = requests[group[position]];
                        materialized[group[position]] = canonical.CloneShifted(
                            request.Key.Origin.X - first.Key.Origin.X,
                            request.Key.Origin.Y - first.Key.Origin.Y,
                            request.Key.Origin.Z - first.Key.Origin.Z);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                {
                    markFailed();
                    MVoxelLog.Error($"对象体素加载失败：{Path.GetFileName(entry.Key)}", exception);
                }

                int done;
                lock (progressGate)
                {
                    done = ++completedGroups;
                }

                report(new BlockingOperationProgress(
                    $"正在读取对象体素：{Path.GetFileNameWithoutExtension(entry.Key)}（{done}/{totalGroups}）…",
                    .22F + .60F * done / totalGroups, true));
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
            throw new OperationCanceledException(exception.InnerException.Message, exception.InnerException, cancellation);
        }

        //把解析结果落到对象标识上，并回填缓存供后续场景复用
        for (var index = 0; index < requests.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = requests[index];
            var world = materialized[index];
            if (world is null)
            {
                try
                {
                    if (!cache.TryGetValue(request.Key, out var source))
                        source = ResolveAssetWorld(request.Key, handoff, cancellation);
                    world = source.Clone();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                {
                    markFailed();
                    MVoxelLog.Error($"对象体素加载失败：{request.Name}", exception);
                    continue;
                }
            }

            worlds[request.ObjectId] = world;
            cache[request.Key] = world;
        }
    }

    /// <summary>
    /// 取一份资产的世界副本：优先用模板写盘时留下的内存副本，没有再读盘
    /// </summary>
    /// <remarks>
    /// 大世界格式写盘时把坐标平移到包围盒最小角，读盘再按对象原点平移回来。
    /// 内存副本本身还是写盘前的原始坐标，因此同样按“对象原点减最小角”平移就能对上，
    /// 未登记的资产自动回落到读盘，行为不变
    /// </remarks>
    /// <param name="key">资产路径与对象原点</param>
    /// <param name="handoff">内存世界登记表</param>
    /// <param name="cancellation">取消令牌</param>
    /// <returns>按对象原点就位的世界副本</returns>
    private static VoxelWorld ResolveAssetWorld(ModelLoadKey key,
        IReadOnlyDictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff,
        CancellationToken cancellation)
    {
        if (handoff is not null && handoff.TryGetValue(key.Path, out var entry))
        {
            return entry.World.CloneShifted(
                key.Origin.X - entry.Minimum.X,
                key.Origin.Y - entry.Minimum.Y,
                key.Origin.Z - entry.Minimum.Z);
        }

        return LargeWorldFormat.Read(key.Path, key.Origin, cancellation);
    }

    internal static PreloadedModels PreloadProjectModels(ProjectHandle project,
        CancellationToken cancellation, Action<BlockingOperationProgress> report, Scene.SceneDocument? overrideScene = null,
        Dictionary<ModelLoadKey, VoxelWorld>? modelCache = null, IReadOnlyList<MaterialAsset>? materials = null,
        IReadOnlyDictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff = null)
    {
        var relative = FindStartupSceneRelativePath(project);
        var path = Path.Combine(project.AssetsPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Scene.SceneDocument? scene = overrideScene;
        if (scene is null && File.Exists(path))
        {
            using var scenePhase = StartupTimeline.Begin("预读·读取场景文档");
            try { scene = Scene.SceneSerializer.Load(path); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
        }
        var worlds = new Dictionary<StableId, VoxelWorld>();
        var cache = modelCache ?? new Dictionary<ModelLoadKey, VoxelWorld>();
        var failed = false;
        if (scene is not null)
        {
            using var decodePhase = StartupTimeline.Begin("预读·解码体素模型");
            var keys = GetObjectModelLoadKeys(project, scene).ToDictionary(item => item.ObjectId, item => item.Key);
            var objects = scene.Objects.Where(item => item.Components.Any(SceneComponentTypes.IsVoxelModel)).ToList();
            var requests = new List<ModelRequest>(objects.Count);
            for (var i = 0; i < objects.Count; i++)
            {
                var item = objects[i];
                var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
                try
                {
                    var data = SceneComponentTypes.ReadVoxelModel(component);
                    if (string.IsNullOrWhiteSpace(data.Model))
                    {
                        // 内置体素对象不读文件，直接取组件里的数据
                        worlds[item.Id] = EmbeddedVoxelData.HasData(component)
                            ? EmbeddedVoxelData.Read(component)
                            : new VoxelWorld();
                        continue;
                    }

                    if (!keys.TryGetValue(item.Id, out var key))
                        throw new InvalidDataException($"模型资产不存在：{data.Model}");
                    requests.Add(new ModelRequest(item.Id, item.Name, key));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                {
                    failed = true;
                    MVoxelLog.Error("对象体素加载失败", exception);
                }
            }

            LoadObjectWorlds(requests, worlds, cache, cancellation, report, () => failed = true, handoff);
        }
        cancellation.ThrowIfCancellationRequested();
        report(new BlockingOperationProgress("正在合成可见对象…", .84F, true));
        var store = new ObjectVoxelStore();
        store.RestoreReferences(worlds);
        VoxelWorld? composed = null;
        RuntimeMaterialMap? materialMap = null;
        VoxelWorld? viewComposed = null;
        using (StartupTimeline.Begin("预读·合成世界"))
        {
            // 编辑用世界与场景视图世界都要，两者互不依赖：前者含图层隐藏的对象供编辑，
            // 后者按场景视图的最终语义（全遮罩、编辑可见性、运行时材质重映射）合成。
            // 预读阶段一次并行做完，界面线程就不必在首帧再合一遍整份世界
            if (scene is not null && materials is not null && overrideScene is null)
            {
                try
                {
                    materialMap = BuildRuntimeMaterialMap(scene, materials, project.AssetsPath);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    materialMap = null;
                    MVoxelLog.Error("运行时材质映射构建失败，场景视图将在首帧按需合成", exception);
                }
            }

            var map = materialMap;
            if (scene is not null)
            {
                composed = store.Compose(scene, uint.MaxValue, false, cancellation);
                if (map is not null)
                {
                    using var viewPhase = StartupTimeline.Begin("预读·合成视图世界");
                    viewComposed = store.Compose(scene, uint.MaxValue, true, cancellation,
                        (objectId, value) => RemapRuntimeMaterial(map.SlotsByObject, objectId, value));
                }
            }
        }

        report(new BlockingOperationProgress("正在计算场景边界…", .88F, true));
        var minimum = default(VoxelCoord64);
        var maximum = default(VoxelCoord64);
        bool hasBounds;
        using (StartupTimeline.Begin("预读·计算场景边界"))
        {
            hasBounds = composed is not null && composed.TryGetBounds(out minimum, out maximum);
        }
        cancellation.ThrowIfCancellationRequested();
        return new PreloadedModels(relative, scene, worlds, composed, failed, hasBounds, minimum, maximum,
            materialMap, viewComposed, materials);
    }

    /// <summary>
    /// 接管后台预读已经读好的材质表与运行时材质映射
    /// </summary>
    /// <remarks>
    /// 预读阶段用的是同一套纯函数，指纹相同；这里再清空指纹会顺带作废
    /// 刚接管的组合世界缓存，首帧又要白合一遍整份世界
    /// </remarks>
    /// <param name="materials">项目材质资产</param>
    /// <param name="map">预读阶段构建的运行时材质映射，可为空</param>
    private void ApplyPreloadedMaterials(IReadOnlyList<MaterialAsset> materials, RuntimeMaterialMap? map)
    {
        _brushMaterials = materials;
        _brushMaterialSlot = 0;
        if (map is not null)
        {
            ApplyRuntimeMaterialMap(map);
        }
        else
        {
            _runtimeMaterialSignature = string.Empty;
            RefreshRuntimeMaterialTable();
        }

        if (materials.Count > 0) MVoxelLog.Info($"已加载 {materials.Count} 个共享材质资产");
    }

    private void ApplyPreloadedBounds(PreloadedModels preload)    {
        _axisOcclusionBoundsWorld = preload.Composed;
        _axisOcclusionBoundsVersion = preload.Composed!.ContentVersion;
        _axisOcclusionHasBounds = preload.HasBounds;
        _axisOcclusionMinimum = preload.Minimum;
        _axisOcclusionMaximum = preload.Maximum;
    }

    private void LoadWorldForProject(ProjectHandle project,
        PreloadedModels? preload = null)
    {
        using var loadPhase = StartupTimeline.Begin("打开·装载工作世界");
        _voxelHistory.Clear();
        _layerOwnership.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
        _pinnedVoxelModelId = null;
        _sceneDocument = preload?.Scene ?? LoadSceneDocument(project);
        if (!_sceneDocument.HasSerializedEnvironment)
        {
            // 旧版打开场景时可能已将默认环境写回渲染配置；导出配置仍保留模板环境。
            // 仅在当前环境呈默认状态且导出配置明确包含雾或水时恢复，避免覆盖已有场景环境。
            var legacyEnvironment = _renderSettings.FogDensity == 0F && !_renderSettings.ShowWater && _renderSettings.ShowGround &&
                                    (_projectSettings.ExportRenderSettings.FogDensity > 0F || _projectSettings.ExportRenderSettings.ShowWater)
                ? _projectSettings.ExportRenderSettings : _renderSettings;
            _sceneDocument.Environment.CaptureFrom(legacyEnvironment);
        }
        _sceneDocument.Environment.ApplyTo(_renderSettings);
        if (preload?.Scene is not null)
        {
            _sceneValidationErrors = Scene.SceneValidation.Validate(_sceneDocument);
            foreach (var error in _sceneValidationErrors) MVoxelLog.Warn($"主场景结构校验：{error}");
        }
        _sceneDocumentDirty = false;
        _renderCameraId = null;
        _renderOrbitAnchor = null;
        _renderCameraInitialFramePending = true;
        InvalidateComposedWorldCache();
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
        _worldLoadFailed = false;
        if (preload?.Scene is not null && preload.Composed is not null)
        {
            _objectVoxels.RestoreReferences(preload.ObjectWorlds);
            _world = preload.Composed;
            ApplyPreloadedBounds(preload);
            _worldLoadFailed = preload.LoadFailed;
            _pickVersion++;
            // 预读已经按场景视图的最终语义合成过一份世界，这里连同运行时材质映射一起接管，
            // 避免界面线程在第一帧重新合成整份世界再触发一次工作集重建
            if (preload.MaterialMap is { } materialMap) ApplyRuntimeMaterialMap(materialMap);
            if (preload.ViewComposed is { } viewComposed)
            {
                _composedWorlds.Insert(0,
                    new ComposedWorldEntry(RenderStateSignature() ^ long.MinValue, uint.MaxValue, viewComposed));
            }
        }
        else LoadObjectVoxelWorlds(project);
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        _sceneGizmoMode = SceneGizmoMode.Select;

        // 项目创建/打开完成回调不能在 UI 线程同步构建和上传 GPU 工作集，
        // 否则大模板会让进度弹窗停在 98%。场景视图不依赖该工作集，
        // 渲染视图首次绘制时会按当前图层和项目配置按需同步。
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _lastRenderGpuSubmitMs = 0;
        _renderViewNeedsInitialRender = true;
        FrameCameraOnWorld();
    }

    private static string? FindPrimaryModelPath(ProjectHandle project, SceneDocument? document)
    {
        var first = document?.Objects.FirstOrDefault(item => item.Components.Any(SceneComponentTypes.IsVoxelModel));
        if (first is null) return null;
        return GetObjectModelLoadKeys(project, document).FirstOrDefault(item => item.ObjectId == first.Id).Key.Path;
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
        if (TryGetCachedWorldBounds(out var min, out var max))
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
        if (!TryGetCachedWorldBounds(out var min, out var max))
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
    /// <summary>渲染窗口实时模式的 AO 射线预算</summary>
    private const int RealtimeAoRayBudget = 8;

    /// <summary>渲染窗口实时模式的体素间接光方向预算</summary>
    private const int RealtimeIndirectRayBudget = 4;

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
    private VoxelWorld GetRenderWorld(uint cullingMask, bool sceneVisibility = false)
    {
        RefreshRuntimeMaterialTable();
        if (_sceneDocument is null)
        {
            return _world;
        }

        var signature = RenderStateSignature() ^ (sceneVisibility ? long.MinValue : 0);
        // 场景视图与渲染视图各用一份组合世界：单条缓存会让每次切换视图都重新合成整份世界，
        // 紧接着触发工作集重建，切换直接卡一秒以上。命中条目提到最近使用位置
        for (var index = 0; index < _composedWorlds.Count; index++)
        {
            var entry = _composedWorlds[index];
            if (entry.Mask != cullingMask || entry.Signature != signature) continue;
            if (index > 0)
            {
                _composedWorlds.RemoveAt(index);
                _composedWorlds.Insert(0, entry);
            }

            return entry.World;
        }

        VoxelWorld composed;
        using (StartupTimeline.Begin("组合世界"))
        {
            composed = _objectVoxels.Compose(_sceneDocument, cullingMask, sceneVisibility,
                remap: RemapRuntimeMaterial);
        }
        if (_composedWorlds.Count >= ComposedWorldCacheCapacity)
        {
            _composedWorlds.RemoveAt(_composedWorlds.Count - 1);
        }

        _composedWorlds.Insert(0, new ComposedWorldEntry(signature, cullingMask, composed));
        return composed;
    }

    /// <summary>
    /// 清空组合世界缓存：对象可见性、图层、材质等存储状态变化后必须重新合成
    /// </summary>
    private void InvalidateComposedWorldCache()
    {
        _composedWorlds.Clear();
    }

    /// <summary>
    /// 渲染状态指纹：工作世界版本、全部对象的可见性与图层、分层所有权规模
    /// </summary>
    private long RenderStateSignature()
    {
        unchecked
        {
            var hash = (long)_world.ContentVersion * 397;
            foreach (var layer in _sceneDocument?.SortingLayerNames ?? []) hash = hash * 31 + StringComparer.Ordinal.GetHashCode(layer);
            foreach (var sceneObject in _sceneDocument?.Objects ?? [])
            {
                hash = hash * 31 + sceneObject.Id.GetHashCode();
                hash = hash * 31 + (sceneObject.ParentId?.GetHashCode() ?? 0);
                hash = hash * 31 + (sceneObject.Visible ? 7 : 3);
                hash = hash * 31 + sceneObject.LayerIndex;
                foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
                {
                    var model = SceneComponentTypes.ReadVoxelModel(component);
                    hash = hash * 31 + (component.Enabled ? 7 : 3);
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(model.SortingLayer);
                    hash = hash * 31 + model.SortingOrder;
                    foreach (var slot in model.MaterialSlots ?? [])
                    {
                        hash = hash * 31 + slot.Slot;
                        hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(slot.MaterialAsset);
                    }
                }
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
    /// 在独立快照上合并项目配置、相机参数和场景覆盖。
    /// </summary>
    /// <param name="cameraObject">相机对象，可为空</param>
    /// <returns>生效渲染配置</returns>
    private RenderSettings ResolveRenderSettings(SceneObject? cameraObject, RenderSettings? baseSettings = null)
    {
        var settings = (baseSettings ?? _captureRenderSettings ?? _renderSettings).CloneForExport();
        _sceneDocument?.Environment.ApplyTo(settings);
        var camera = cameraObject?.Components.FirstOrDefault(item => item.Enabled && SceneComponentTypes.IsCamera(item));
        if (camera is not null)
        {
        var data = SceneComponentTypes.ReadCamera(camera);
        settings.RealtimeAntiAliasingSamples = data.RealtimeAntiAliasingSamples;
        settings.RealtimeAntiAliasingPattern = data.RealtimeAntiAliasingPattern;
        settings.RealtimeAntiAliasingSpread = data.RealtimeAntiAliasingSpread;
        settings.FxaaThreshold = data.FxaaThreshold;
        settings.FxaaStrength = data.FxaaStrength;
        settings.CameraFovDegrees = data.PhysicalCamera
            ? 2F * MathF.Atan(data.SensorWidthMillimeters / (2F * data.FocalLengthMillimeters)) * (180F / MathF.PI)
            : data.FieldOfViewDegrees;
        settings.ApertureRadius = data.PhysicalCamera ? Math.Clamp(1F / data.ApertureFStop, 0F, 1F) : settings.ApertureRadius;
        settings.FocusDistance = data.PhysicalCamera ? data.FocusDistance : settings.FocusDistance;
        settings.Exposure = data.PhysicalCamera ? MathF.Log2(Math.Max(data.Iso / 100F, .01F) * Math.Max(data.ShutterSpeed * 60F, .01F) / Math.Max(data.ApertureFStop, .7F)) : settings.Exposure;
        if (data.PostProcess is { OverrideProjectDefaults: true } stack)
        {
            var dof = stack.Find(CameraPostProcessEffectType.DepthOfField);
            settings.DepthOfField = dof?.Enabled == true;
            if (!data.PhysicalCamera && dof is { Enabled: true, OverrideDepthOfFieldParameters: true })
            {
                settings.ApertureRadius = dof.DepthOfFieldAperture;
                settings.FocusDistance = dof.DepthOfFieldFocusDistance;
            }
            var ao = stack.Find(CameraPostProcessEffectType.AmbientOcclusion);
            settings.AmbientOcclusionEnabled = ao?.Enabled == true;
            if (ao is not null)
            {
                settings.AoStrength = Math.Clamp(ao.Intensity, 0F, 1F);
                settings.AoSamples = Math.Clamp(ao.Samples, 4, 64);
                settings.AoPower = ao.AoPower;
            }
            var indirect = stack.Find(CameraPostProcessEffectType.VoxelIndirectLight);
            settings.VoxelIndirectLightStrength = indirect?.Enabled == true ? indirect.Intensity : 0F;
            // 采样数也由相机栈决定，这样渲染窗口的实时模式可以单独降采样而不影响场景窗口
            if (indirect is not null) settings.VoxelIndirectLightSamples = Math.Clamp(indirect.Samples, 1, 32);

        // 渲染窗口的实时（非光追）预算：这一档直接决定帧时间，
        // 场景窗口走另一条预览设置，不受这里影响
        if (!settings.PathTracingEnabled)
        {
            settings.AoSamples = Math.Min(settings.AoSamples, RealtimeAoRayBudget);
            settings.VoxelIndirectLightSamples = Math.Min(settings.VoxelIndirectLightSamples, RealtimeIndirectRayBudget);
        }
            var probeGi = stack.Find(CameraPostProcessEffectType.ProbeVolumeGi);
            settings.ProbeVolumeGiStrength = probeGi?.Enabled == true ? probeGi.Intensity : 0F;
            var tone = stack.Find(CameraPostProcessEffectType.ToneMapping);
            if (tone is not null)
            {
                settings.Tone = tone.Enabled ? tone.ToneCurve : ToneCurve.Linear;
                if (tone.Enabled)
                {
                    settings.Exposure += tone.ExposureOffset;
                    settings.Gamma = tone.Gamma;
                    settings.WhiteBalance = tone.WhiteBalance;
                }
            }
            var dither = stack.Find(CameraPostProcessEffectType.Dither);
            if (dither is not null) settings.Dither = dither.Enabled;
            var bloom = stack.Find(CameraPostProcessEffectType.Bloom);
            if (bloom is not null)
            {
                settings.BloomEnabled = bloom.Enabled;
                settings.BloomIntensity = Math.Clamp(bloom.Intensity, 0F, 2F);
                settings.BloomThreshold = bloom.Threshold;
                settings.BloomRadius = Math.Clamp(bloom.Radius, 1F, 64F);
            }
            var grading = stack.Find(CameraPostProcessEffectType.ColorGrading);
            if (grading is not null)
            {
                settings.PaletteShading = grading.Enabled;
                if (grading.Enabled)
                {
                    settings.Saturation = grading.Saturation;
                    settings.HueShiftDegrees = grading.HueShift;
                    settings.ColorContrast = grading.Contrast;
                    settings.Brightness = grading.Brightness;
                    settings.HueCycle = false;
                }
            }
            var grain = stack.Find(CameraPostProcessEffectType.FilmGrain);
            if (grain is not null)
            {
                settings.FilmGrainIntensity = grain.Enabled ? Math.Clamp(grain.Intensity, 0F, 1F) : 0F;
                settings.FilmGrainSize = Math.Clamp(grain.Radius, 1F, 8F);
            }
            var posterize = stack.Find(CameraPostProcessEffectType.Posterize);
            if (posterize is not null)
            {
                settings.PosterizeMix = posterize.Enabled ? Math.Clamp(posterize.Intensity, 0F, 1F) : 0F;
                settings.PosterizeLevels = Math.Clamp(posterize.Samples, 2, 64);
            }
            var splitTone = stack.Find(CameraPostProcessEffectType.SplitToning);
            if (splitTone is not null)
            {
                settings.SplitToneMix = splitTone.Enabled ? Math.Clamp(splitTone.Intensity, 0F, 1F) : 0F;
                settings.SplitToneShadows = new System.Numerics.Vector3(splitTone.ShadowR, splitTone.ShadowG, splitTone.ShadowB);
                settings.SplitToneHighlights = new System.Numerics.Vector3(splitTone.HighlightR, splitTone.HighlightG, splitTone.HighlightB);
                settings.SplitToneBalance = splitTone.ToningBalance;
            }
            var vignette = stack.Find(CameraPostProcessEffectType.Vignette);
            settings.Vignette = vignette?.Enabled == true ? Math.Clamp(vignette.Intensity, 0F, 1F) : 0F;
        }
        }
        var probe = _sceneDocument?.Objects.Where(IsSceneObjectVisible).SelectMany(item => item.Components)
            .FirstOrDefault(item => item.Enabled && SceneComponentTypes.IsProbeVolume(item));
        if (probe is not null && cameraObject is not null)
        {
            var probeData = SceneComponentTypes.ReadProbeVolume(probe);
            var probeObject = _sceneDocument?.Objects.FirstOrDefault(item => item.Components.Contains(probe));
            // 本会话自动烘焙过的数据优先，没烘过才回落到资产文件
            ProbeVolumeBakeData? bake = null;
            if (probeObject is not null && _liveProbeBakeObjectId == probeObject.Id)
            {
                bake = _liveProbeBake;
            }
            else if (!string.IsNullOrWhiteSpace(probeData.BakedAsset) && _project is not null)
            {
                var probePath = Path.Combine(_project.RootPath, probeData.BakedAsset.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(probePath))
                {
                    try
                    {
                        bake = JsonSerializer.Deserialize<ProbeVolumeBakeData>(File.ReadAllText(probePath), new JsonSerializerOptions { IncludeFields = true });
                        bake?.Normalize();
                    }
                    catch (JsonException) { }
                }
            }

            if (bake is { Samples.Count: > 0 })
            {
                {
                    var cameraPosition = cameraObject.Transform.Mode == TransformMode.Free
                        ? cameraObject.Transform.Free.Position
                        : new Double3(cameraObject.Transform.Grid.Position.X, cameraObject.Transform.Grid.Position.Y, cameraObject.Transform.Grid.Position.Z);
                    var probePosition = probeObject is null ? Vector3.Zero : GetObjectWorldPosition(probeObject);
                    var normalized = new Vector3(
                        Math.Clamp((float)(cameraPosition.X - probePosition.X - probeData.OffsetX) / Math.Max(probeData.SizeX, .001F) + .5F, 0F, 1F),
                        Math.Clamp((float)(cameraPosition.Y - probePosition.Y - probeData.OffsetY) / Math.Max(probeData.SizeY, .001F) + .5F, 0F, 1F),
                        Math.Clamp((float)(cameraPosition.Z - probePosition.Z - probeData.OffsetZ) / Math.Max(probeData.SizeZ, .001F) + .5F, 0F, 1F));
                    var weightedOcclusion = 0F;
                    var weightedRadiance = 0F;
                    var totalWeight = 0F;
                    foreach (var sample in bake.Samples.OrderBy(sample => (sample.X - normalized.X) * (sample.X - normalized.X) + (sample.Y - normalized.Y) * (sample.Y - normalized.Y) + (sample.Z - normalized.Z) * (sample.Z - normalized.Z)).Take(8))
                    {
                        var distanceSquared = MathF.Pow(sample.X - normalized.X, 2F) + MathF.Pow(sample.Y - normalized.Y, 2F) + MathF.Pow(sample.Z - normalized.Z, 2F);
                        var weight = 1F / MathF.Max(distanceSquared, 0.0001F);
                        weightedOcclusion += Math.Clamp(sample.Occlusion, 0F, 1F) * weight;
                        var radiance = Math.Clamp(sample.R, 0F, 1F) * 0.2126F + Math.Clamp(sample.G, 0F, 1F) * 0.7152F + Math.Clamp(sample.B, 0F, 1F) * 0.0722F;
                        weightedRadiance += radiance * weight;
                        totalWeight += weight;
                    }
                    var occlusion = totalWeight > 0F ? weightedOcclusion / totalWeight : 1F;
                    settings.SkyAmbient = Math.Clamp(settings.SkyAmbient +
                        (totalWeight > 0F ? weightedRadiance / totalWeight : 0F) * (0.35F + occlusion * 0.65F) * settings.ProbeVolumeGiStrength * .35F, 0F, 2F);
                }
            }
        }
        return settings;
    }

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

    /// <summary>
    /// 组合世界缓存容量：场景视图与渲染视图各用一份变体，另留一格给不同相机图层遮罩
    /// </summary>
    private const int ComposedWorldCacheCapacity = 3;

    /// <summary>
    /// 组合世界缓存条目：同一份对象体素按不同可见性语义与图层遮罩合成出不同世界
    /// </summary>
    private readonly record struct ComposedWorldEntry(long Signature, uint Mask, VoxelWorld World);

    /// <summary>按图层遮罩组成的渲染世界缓存，最近使用的排在最前面</summary>
    private readonly List<ComposedWorldEntry> _composedWorlds = new(ComposedWorldCacheCapacity);

    private VoxelWorld? _axisOcclusionBoundsWorld;
    private int _axisOcclusionBoundsVersion = -1;
    private VoxelCoord64 _axisOcclusionMinimum;
    private VoxelCoord64 _axisOcclusionMaximum;
    private bool _axisOcclusionHasBounds;

    private void UpdateAxisOcclusionBounds()
    {
        if (ReferenceEquals(_axisOcclusionBoundsWorld, _world) &&
            _axisOcclusionBoundsVersion == _world.ContentVersion) return;
        _axisOcclusionHasBounds = _world.TryGetBounds(out _axisOcclusionMinimum, out _axisOcclusionMaximum);
        _axisOcclusionBoundsWorld = _world;
        _axisOcclusionBoundsVersion = _world.ContentVersion;
    }

    private bool TryGetCachedWorldBounds(out VoxelCoord64 minimum, out VoxelCoord64 maximum)
    {
        UpdateAxisOcclusionBounds();
        minimum = _axisOcclusionMinimum;
        maximum = _axisOcclusionMaximum;
        return _axisOcclusionHasBounds;
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

        var world = GetRenderWorld(uint.MaxValue, sceneVisibility: true);
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

    private bool SaveWorldToProject(ProjectHandle project)
    {

        if (_worldLoadFailed)
        {
            SetStatus("体素资产未完整加载，已阻止保存覆盖；请修复资产路径或重新打开项目");
            return false;
        }
        try
        {
            _sceneDocument ??= new SceneDocument { Name = "主场景" };
            _sceneDocument.Environment.Normalize();
            if (GetVoxelWorldObject() is null && _world.OccupiedVoxelCount == 0)
            {
                Scene.SceneSerializer.Save(Path.Combine(project.AssetsPath, _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    _sceneDocument);
                return true;
            }
            SaveObjectVoxelWorlds(project);
            var scenePath = Path.Combine(project.AssetsPath, _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
            SceneSerializer.Save(scenePath, _sceneDocument ?? new SceneDocument { Name = "主场景" });
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
        var referenced = FindPrimaryModelPath(project, _sceneDocument);
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
            var existing = SceneComponentTypes.ReadVoxelModel(modelComponent);
            SceneComponentTypes.WriteVoxelModel(modelComponent, existing with
            {
                Model = assetRelativePath,
                VoxelCount = _world.OccupiedVoxelCount,
            });
        }

        _sceneDocument = document;
    }

    private static bool IsNativeObjectDocument(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        return stream.Length >= 8 && reader.ReadUInt32() == 0x3158564D && reader.ReadInt32() == 4;
    }

    private void ImportNativeObjectDocument(string path)
    {
        if (_project is null) return;
        var document = LargeWorldFormat.ReadObjects(path);
        var materialById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in document.Materials.Definitions)
        {
            var dir = Path.Combine(_project.AssetsPath, "Materials");
            Directory.CreateDirectory(dir);
            var file = UniqueAssetFileName(dir, definition.Value.Name, ".material");
            var absolute = Path.Combine(dir, file);
            definition.Value.Save(absolute);
            AssetMetaStore.EnsureMeta(absolute);
            materialById[definition.Key] = "Materials/" + file;
        }
        var graph = new Scene.SceneGraph(_sceneDocument ??= new Scene.SceneDocument { Name = "主场景" });
        foreach (var segment in document.Objects)
        {
            var objectId = StableId.TryParse(segment.ObjectId, out var parsed) ? parsed : StableId.New();
            var sceneObject = _sceneDocument.Objects.FirstOrDefault(item => item.Id == objectId) ?? graph.Add(segment.Name, transform: TransformComponent.FromGrid(new GridTransform(segment.Origin, OrthogonalRotation.Identity)));
            sceneObject.Id = objectId;
            sceneObject.Name = segment.Name;
            var relative = $"Assets/Worlds/{UniqueAssetFileName(Path.Combine(_project.AssetsPath, "Worlds"), segment.Name, ".mvmodel")}";
            var worldPath = Path.Combine(_project.AssetsPath, relative["Assets/".Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
            LargeWorldFormat.Write(worldPath, segment.World);
            var bindings = document.Materials.ObjectBindings.TryGetValue(segment.ObjectId, out var map) ? map : document.Materials.Bindings;
            var slots = bindings.Where(item => materialById.ContainsKey(item.Value)).Select(item => new MaterialSlotBinding(item.Key, materialById[item.Value])).ToArray();
            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null)
            {
                component = SceneComponentTypes.CreateVoxelModel(relative, segment.World.OccupiedVoxelCount, segment.Name, segment.Name);
                sceneObject.Components.Add(component);
            }
            SceneComponentTypes.WriteVoxelModel(component, SceneComponentTypes.ReadVoxelModel(component) with { Model = relative, VoxelCount = segment.World.OccupiedVoxelCount, MaterialSlots = slots });
        }
        MarkSceneDocumentDirty();
        LoadObjectVoxelWorlds(_project);
        SetStatus($"已导入原生v4对象片段：{document.Objects.Count}个对象");
    }

    private Dictionary<int, MaterialSlotBinding> ImportNativeMaterials(ProjectHandle project, MaterialExchangeMetadata metadata)
    {
        var result = new Dictionary<int, MaterialSlotBinding>();
        var bindings = metadata.Bindings.Count > 0
            ? metadata.Bindings
            : metadata.ObjectBindings.Values.SelectMany(item => item).GroupBy(item => item.Key)
                .ToDictionary(group => group.Key, group => group.First().Value);
        if (bindings.Count == 0) return result;
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        foreach (var binding in bindings.OrderBy(item => item.Key))
        {
            if (!metadata.Definitions.TryGetValue(binding.Value, out var source)) continue;
            var baseName = string.IsNullOrWhiteSpace(source.Name) ? $"导入材质{binding.Key}" : source.Name;
            var fileName = UniqueAssetFileName(directory, baseName, ".material");
            var absolute = Path.Combine(directory, fileName);
            var material = MaterialAsset.Parse(JsonSerializer.Serialize(source));
            material.Name = source.Name;
            material.Save(absolute);
            AssetMetaStore.EnsureMeta(absolute);
            result[binding.Key] = new MaterialSlotBinding(binding.Key, "Materials/" + fileName);
        }
        return result;
    }

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

        try
        {
            var workingRelativePath = relativePath.Replace('\\', '/');
            var extension = Path.GetExtension(absolute).ToLowerInvariant();
            if (extension == ".mvworld" && IsNativeObjectDocument(absolute))
            {
                ImportNativeObjectDocument(absolute);
                return;
            }
            MaterialExchangeMetadata? importedMaterials = null;
            if (extension is ".mvmodel" or ".mvworld")
            {
                var document = ReadNativeVoxelDocument(absolute);
                _world = document.World;
                importedMaterials = document.Materials;
            }
            else if (extension is ".vox" or ".qb" or ".schem")
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

            if (importedMaterials is not null)
            {
                var bindings = ImportNativeMaterials(_project, importedMaterials);
                var model = _sceneDocument?.Objects.FirstOrDefault(item => item.Components.Any(SceneComponentTypes.IsVoxelModel));
                if (model is not null)
                {
                    var component = model.Components.First(SceneComponentTypes.IsVoxelModel);
                    var data = SceneComponentTypes.ReadVoxelModel(component);
                    SceneComponentTypes.WriteVoxelModel(component, data with { MaterialSlots = bindings.Values.ToArray() });
                }
            }

            var primaryAssetPath = "Assets/" + workingRelativePath;
            UpdateSceneModelReference(primaryAssetPath);
            // 切换世界是新的工作上下文：清空分层所有权，并移除旧世界遗留的分层引用
            _layerOwnership.Clear();
            var removedLayers = _sceneDocument is null
                ? 0
                : ConsolidateSecondaryModelObjects(_sceneDocument, primaryAssetPath);
            LoadObjectVoxelWorlds(_project);
            MarkSceneDocumentDirty();
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

    private float _waterAnimationTime;
    private VoxelRenderParameters.FrameState BuildFrameState(SceneCamera camera, Vector2 viewLogical,
        float deviceScale, int widthOverride, int heightOverride, bool pathMode, bool outlines,
        bool showGrid = false, float gridStep = 0F, bool scenePreview = false,
        VulkanVoxelRenderPipeline? sourcePipeline = null, RenderSettings? frameSettings = null)
    {
        var framePipeline = sourcePipeline ?? _pipeline;
        NumVector3 Convert(Vector3 value) => new(value.X, value.Y, value.Z);
        var width = Math.Max(16, widthOverride);
        var height = Math.Max(16, heightOverride);
        var effectiveSettings = frameSettings ?? ResolveRenderSettings(GetActiveRenderCameraObject());
        return new VoxelRenderParameters.FrameState(
            Convert(camera.Position), Convert(camera.Forward), Convert(camera.Right), Convert(camera.Up),
            SceneProjection.FocalLength(viewLogical, camera) * deviceScale,
            width, height,
            // 实时渲染与场景预览是确定性结果，稳定帧号允许管线复用上一帧；
            // 路径追踪仍需帧号驱动样本去相关。
            FrameIndex: pathMode ? framePipeline?.AccumulatedSamples ?? 0 : 0,
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
            WaterTime = _waterAnimationTime,
            NearClip = camera.NearClip,
            FarClip = float.IsFinite(camera.FarClip) ? camera.FarClip : 100_000_000F,
        };
    }

    /// <summary>
    /// 采样签名：相机、几何体、光照、材质和采样设置变化才会让已完成的路径样本失效
    /// 每帧样本数与累积样本上限不参与签名，只影响推进速度，不会改变已经收敛的结果
    /// </summary>
    private string PathSamplingSignature(Vector2 size, float deviceScale)
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
            .Append(',').Append(settings.FogDensity).Append(',').Append(settings.FogStart).Append(',').Append(settings.FogSkyBlend).Append(',').Append(settings.FogMaxOpacity).Append(',').Append(settings.ShowWater).Append(',').Append(settings.WaterHeight).Append(',').Append(settings.WaterColor).Append(',').Append(settings.WaterWaveSize).Append(',').Append(settings.WaterWaveLength).Append(',').Append(settings.WaterRoughness).Append(',').Append(settings.WaterAnimationEnabled).Append(',').Append(settings.WaterAnimationSpeed).Append(',').Append(settings.WaterFlowDirection).Append(',').Append(settings.WaterDetailStrength).Append(',').Append(settings.WaterSpecularStrength).Append(',').Append(settings.PathTracingEnabled)
            .Append(',').Append(settings.UseHardwareRayTracing)
            .Append(settings.MaxBounces).Append(',').Append(settings.PathRoughness).Append(',').Append(settings.PathMetalness)
            .Append(',').Append(settings.PathSpecularF0).Append(',').Append(settings.ResolutionScale)
            .Append(',').Append(settings.DepthOfField).Append(',').Append(settings.ApertureRadius).Append(',').Append(settings.FocusDistance)
            .Append(',').Append(settings.CameraFovDegrees).Append(',').Append(settings.EmissiveVoxels).Append(settings.EmissiveIntensity)
            .Append(',').Append(settings.SunDirectLighting).Append(',').Append(settings.EmissiveDirectSampling)
            .Append(',').Append(settings.EmissiveLightTree).Append(',').Append(settings.RussianRouletteThreshold)
            .Append(',').Append(settings.RadianceClamp).Append(',').Append(settings.TransmissiveAbsorption).Append(',').Append(settings.Backend)
            .Append(',').Append(settings.PaletteShading).Append(',').Append(settings.SceneShading)
            .Append(',').Append(settings.AutoPerformanceScaling)
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

    /// <summary>
    /// 显示签名：只有后处理与显示参数参与，变化时直接重跑收尾通道，不重新采样
    /// </summary>
    private string PathDisplaySignature()
    {
        var settings = ResolveRenderSettings(GetActiveRenderCameraObject());
        return new StringBuilder()
            .Append(settings.Saturation).Append(',').Append(settings.HueShiftDegrees).Append(',')
            .Append(settings.HueCycle).Append(',').Append(settings.HueCycleSpeed).Append(',')
            .Append(settings.ColorContrast).Append(',').Append(settings.Brightness).Append(',').Append(settings.Tone)
            .Append(',').Append(settings.Exposure).Append(',').Append(settings.Gamma).Append(',').Append(settings.WhiteBalance)
            .Append(',').Append(settings.Vignette).Append(',').Append(settings.Dither)
            .Append(',').Append(settings.FilmGrainIntensity).Append(',').Append(settings.FilmGrainSize)
            .Append(',').Append(settings.PosterizeLevels).Append(',').Append(settings.PosterizeMix)
            .Append(',').Append(settings.SplitToneShadows).Append(',').Append(settings.SplitToneHighlights)
            .Append(',').Append(settings.SplitToneBalance).Append(',').Append(settings.SplitToneMix)
            .Append(',').Append(settings.PathDenoising).Append(',').Append(settings.PathDenoiseStrength)
            .Append(',').Append(settings.BloomEnabled).Append(',').Append(settings.BloomIntensity)
            .Append(',').Append(settings.BloomThreshold).Append(',').Append(settings.BloomRadius)
            .Append(',').Append(settings.WeatherWindStrength).Append(',').Append(settings.WeatherWindDirection)
            .Append(',').Append(settings.WeatherRainIntensity).Append(',').Append(settings.WeatherSnowIntensity)
            .Append(',').Append(settings.WeatherStormIntensity).Append(',').Append(settings.WeatherHeatHaze)
            .Append(',').Append(settings.WeatherLightningFrequency)
            .Append(',').Append(settings.AmbientOcclusionEnabled).Append(',').Append(settings.AoStrength)
            .Append(',').Append(settings.AoPower).Append(',').Append(settings.AoSamples)
            .Append(',').Append(settings.VoxelIndirectLightStrength).Append(',').Append(settings.ProbeVolumeGiStrength)
            .Append(',').Append(settings.RealtimeAntiAliasingSamples).Append(',').Append(settings.RealtimeAntiAliasingPattern)
            .Append(',').Append(settings.RealtimeAntiAliasingSpread).Append(',').Append(settings.FxaaThreshold)
            .Append(',').Append(settings.FxaaStrength)
            .ToString();
    }

    private readonly VoxelWorld _emptyScenePreviewWorld = new();

    /// <summary>
    /// 按项目选定的画面比例算出渲染与显示的有效矩形：比例之外留黑边，自由比例时铺满整个视图。
    /// </summary>
    private (Vector2 Offset, Vector2 Size) ResolveRenderFrameRect(Vector2 size)
    {
        var aspect = _projectSettings?.FindAspectPreset()?.Aspect ?? 0F;
        if (aspect <= 0F || size.X <= 0F || size.Y <= 0F) return (Vector2.Zero, size);
        if (size.X / size.Y > aspect)
        {
            var width = size.Y * aspect;
            return (new Vector2((size.X - width) * 0.5F, 0F), new Vector2(width, size.Y));
        }

        var height = size.X / aspect;
        return (new Vector2(0F, (size.Y - height) * 0.5F), new Vector2(size.X, height));
    }

    /// <summary>
    /// 按输出图像的真实宽高比绘制渲染纹理。
    /// 视图尺寸与渲染尺寸不一致时（截图指定分辨率、复用上一帧纹理）等比居中，
    /// 不把任意比例的图像拉伸铺满视图，否则画面会横向变形。
    /// </summary>
    private static void DrawPipelineImage(ImDrawListPtr drawList, VulkanVoxelRenderPipeline pipeline,
        Vector2 origin, Vector2 size)
    {
        var uvMax = pipeline.DisplayUvMax;
        var textureWidth = pipeline.OutputWidth * uvMax.X;
        var textureHeight = pipeline.OutputHeight * uvMax.Y;
        if (textureWidth <= 0F || textureHeight <= 0F || size.X <= 0F || size.Y <= 0F) return;
        var textureAspect = textureWidth / textureHeight;
        var viewAspect = size.X / size.Y;
        // 常规帧只差像素取整的千分之一以内，直接铺满；比例明显不同才等比留边
        var target = MathF.Abs(textureAspect / viewAspect - 1F) > 0.01F
            ? textureAspect > viewAspect
                ? new Vector2(size.X, size.X / textureAspect)
                : new Vector2(size.Y * textureAspect, size.Y)
            : size;
        var position = origin + (size - target) * 0.5F;
        drawList.AddImage(pipeline.DisplayTextureId, position, position + target, Vector2.Zero, uvMax);
    }

    /// <summary>
    /// 用 Vulkan Compute 绘制场景基础几何和无限网格，空场景也保留网格。
    /// </summary>
    private bool TryRenderGpuSceneView(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        bool showGeometry = true)
    {
        if (_project is null || _gpuRenderingDisabled) return false;
        try
        {
            if (_scenePipeline is null)
            {
                using (StartupTimeline.Begin("首帧·创建预览管线"))
                {
                    _scenePipeline = new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
                }
            }

            var pipeline = _scenePipeline;
            var settings = CurrentSceneRenderSettings.CreateScenePreview();
            pipeline.SyncWorld(showGeometry ? GetSceneInspectionWorld(GetRenderWorld(uint.MaxValue, sceneVisibility: true)) : _emptyScenePreviewWorld, 0, settings.SceneLod,
                new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
                greedyMerge: false,
                includeInterior: IsCameraInsideOccupiedVoxel(camera));
            if (!pipeline.IsAvailable) return false;
            pipeline.SetSceneSelection(showGeometry ? GetSelectedSubtreeWorld() : null);

            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var frame = BuildFrameState(camera, size, scale, width, height,
                pathMode: false, outlines: _showVoxelOutlines, scenePreview: true,
                showGrid: _showGrid, gridStep: 1F, sourcePipeline: pipeline);
            var submitsBefore = pipeline.SubmitCount;
            var fenceBefore = pipeline.FenceWaitTotalMs;
            pipeline.GpuProfilingEnabled = PerformanceProbeActive;
            // 设备创建前生效，运行中切换需要重建渲染设备
            VulkanWindowContext.ForceComputePath = _renderSettings.ForceComputePath;
            pipeline.GpuTimingToken = _analysisLive.Serial;
            var performanceMark = PerformanceMark();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var renderWatch = System.Diagnostics.Stopwatch.StartNew();
            if (!pipeline.RenderSceneView(settings, frame, width, height)) return false;
            renderWatch.Stop();
            StartupTimeline.Mark(StartupTimeline.MilestonePreviewFrame);
            RecordViewportFrame(_sceneViewportPerformance, pipeline, renderWatch.Elapsed.TotalMilliseconds,
                submitsBefore, fenceBefore, allocatedBefore, width, height);
            RecordPerformanceViewport(pipeline, renderWatch.Elapsed.TotalMilliseconds, submitsBefore,
                fenceBefore, width, height, pathTracing: false);
            MeasureSince(performanceMark, ref _analysisLive.ViewportCpuMs);

            DrawPipelineImage(drawList, pipeline, origin, size);
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
            pipeline.SetSceneLights(CollectSceneLights(GetActiveRenderCullingMask()));
            pipeline.SyncWorld(GetRenderWorld(GetActiveRenderCullingMask()), 0, effective.RenderLod, new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z));
            // 首帧工作集是在渲染视图按需上传的，因此不能在 SyncWorld 之前用
            // IsAvailable 拦截；否则“尚未上传”会永远阻止上传，永久退回 CPU 预览。
            if (!pipeline.IsAvailable) return false;
            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var previewSettings = effective.CreateLargeScenePreview(pipeline.SyncTotalCells);
            // 交互渲染每帧只追加一个路径样本：累加样本上限和最终画质不变，
            // 但单帧 GPU 时间减半，转视角和进度刷新不再被整帧路径追踪拖住
            previewSettings.PathSamplesPerFrame = 1;
            var cameraNavigating = ImGui.IsMouseDragging(ImGuiMouseButton.Right) ||
                                   ImGui.IsMouseDragging(ImGuiMouseButton.Middle);
            var settingsInteracting = Environment.TickCount64 - _renderSettingsInteractionAt < 150 && !cameraNavigating;
            // 相机交互也按完整视口像素渲染，不能通过缩小图像再放大换帧率。
            // 只有用户在渲染配置中主动调整的 ResolutionScale 可以改变内部尺寸。
            var renderScale = Math.Clamp(previewSettings.ResolutionScale, 0.25F, 1F);
            // 项目里选定的画面比例决定渲染与显示的有效矩形，比例之外留黑边
            var frame = ResolveRenderFrameRect(size);
            var frameOrigin = origin + frame.Offset;
            var frameSize = frame.Size;
            var width = Math.Max(16, (int)(frameSize.X * scale * renderScale));
            var height = Math.Max(16, (int)(frameSize.Y * scale * renderScale));
            if (_capturePath is not null && _captureWidth > 0)
            {
                width = _captureWidth;
                height = _captureHeight;
            }
            var nowMs = Environment.TickCount64;
            bool animatedEnvironment = (effective.ShowWater && effective.WaterAnimationEnabled && effective.WaterAnimationSpeed != 0F) ||
                effective.WeatherRainIntensity > 0F || effective.WeatherSnowIntensity > 0F ||
                effective.WeatherStormIntensity > 0F || effective.WeatherHeatHaze > 0F;
            if (_capturePath is not null && animatedEnvironment)
            {
                // 只有显式导出任务才推进环境时间；普通渲染窗口固定在当前时刻，保证采样稳定。
                var nextWaterTime = effective.PathTracingEnabled ? MathF.Floor(_elapsedSeconds * 2F) * .5F : _elapsedSeconds;
                if (effective.PathTracingEnabled && nextWaterTime != _waterAnimationTime) pipeline.ResetAccumulation();
                _waterAnimationTime = nextWaterTime;
            }
            // 采样签名只用相机、几何、光照与采样参数；后处理参数进入显示签名，
            // 变化时只重跑收尾通道，避免调曝光等纯显示参数时丢掉已完成的样本
            var samplingSignature = PathSamplingSignature(frameSize, scale) + $"|{width}x{height}";
            var displaySignature = PathDisplaySignature();
            if (samplingSignature != _accumulationSignature)
            {
                _accumulationSignature = samplingSignature;
                _accumulationDisplaySignature = displaySignature;
                pipeline.ResetAccumulation();
            }
            else if (displaySignature != _accumulationDisplaySignature)
            {
                _accumulationDisplaySignature = displaySignature;
                // 硬件光追保存的是单帧成品，没有可重新后处理的累积，只能重画
                if (pipeline.UsingHardwareRayTracing) pipeline.ResetAccumulation();
                else pipeline.RequestDisplayRefresh();
            }

            // 实时帧是确定性的；参数未变时管线会直接复用上一张 GPU 图像，
            // 相机、场景或配置改变时仍会重绘。
            if (!effective.PathTracingEnabled)
            {
                // 环绕/平移相机时保持 AO、光照和后处理完整；仅编辑渲染控件时才启用轻量预览。
                var realtimeSettings = settingsInteracting ? previewSettings.CreateInteractionPreview() : previewSettings;
                var realtimeFrame = BuildFrameState(camera, frameSize, scale, width, height,
                    pathMode: false, outlines: false);
                var submitsBefore = pipeline.SubmitCount;
                var fenceBefore = pipeline.FenceWaitTotalMs;
                pipeline.GpuProfilingEnabled = PerformanceProbeActive;
            // 设备创建前生效，运行中切换需要重建渲染设备
            VulkanWindowContext.ForceComputePath = _renderSettings.ForceComputePath;
                pipeline.GpuTimingToken = _analysisLive.Serial;
                var performanceMark = PerformanceMark();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var renderWatch = System.Diagnostics.Stopwatch.StartNew();
                if (!pipeline.RenderSceneView(realtimeSettings, realtimeFrame, width, height))
                {
                    return false;
                }

                renderWatch.Stop();
                RecordViewportFrame(_renderViewportPerformance, pipeline, renderWatch.Elapsed.TotalMilliseconds,
                    submitsBefore, fenceBefore, allocatedBefore, width, height);
                RecordPerformanceViewport(pipeline, renderWatch.Elapsed.TotalMilliseconds, submitsBefore,
                    fenceBefore, width, height, pathTracing: false);
                MeasureSince(performanceMark, ref _analysisLive.ViewportCpuMs);

                _lastRenderGpuSubmitMs = nowMs;
                _renderViewNeedsInitialRender = false;
                StartupTimeline.Mark(StartupTimeline.MilestoneRenderFrame);
                DrawPipelineImage(drawList, pipeline, frameOrigin, frameSize);
                return true;
            }

            //窗口失焦时保留已完成的画面，重新聚焦或MCP截图时继续累积
            if (!IsFocused && _capturePath is null && !_renderViewNeedsInitialRender &&
                pipeline.DisplayTextureId != IntPtr.Zero)
            {
                DrawPipelineImage(drawList, pipeline, frameOrigin, frameSize);
                return true;
            }

            // 调整渲染配置时可以短暂复用上一张纹理，避免拖动滑块同步等待完整路径追踪；
            // 相机导航绝不能走这条分支，否则新姿态刚写入就会被旧视角覆盖显示，
            // 表现为右键环绕后视角瞬间弹回。
            if (settingsInteracting && pipeline.DisplayTextureId != IntPtr.Zero &&
                nowMs - _lastRenderGpuSubmitMs < 33)
            {
                DrawPipelineImage(drawList, pipeline, frameOrigin, frameSize);
                return true;
            }
            // 相机移动同样使用完整路径追踪配置；不能因导航临时关闭照明或减少反弹。
            var renderSettings = settingsInteracting
                ? previewSettings.CreateInteractionPreview()
                : previewSettings;
            var state = BuildFrameState(camera, frameSize, scale, width, height, pathMode: true, outlines: false);
            // 样本上限变化不丢弃已经完成的样本：提高上限继续采样，降低上限只收敛计数
            pipeline.LimitAccumulatedSamples(renderSettings.PathMaxAccumulatedSamples);
            // 首次进入渲染页没有可呈现的纹理，必须先提交一次，而不能直接走 Present。
            var firstSubmit = _renderViewNeedsInitialRender || _lastRenderGpuSubmitMs == 0 ||
                              pipeline.DisplayTextureId == IntPtr.Zero;
            // 是否采样只由当前项目配置决定，不能读取全局 UI 开关；
            // 首帧和路径追踪状态都必须直接驱动当前渲染提交。
            var wantTrace = (firstSubmit || effective.PathTracingEnabled) &&
                            pipeline.AccumulatedSamples < renderSettings.PathMaxAccumulatedSamples;
            if (wantTrace)
            {
                pipeline.GpuProfilingEnabled = PerformanceProbeActive;
            // 设备创建前生效，运行中切换需要重建渲染设备
            VulkanWindowContext.ForceComputePath = _renderSettings.ForceComputePath;
                pipeline.GpuTimingToken = _analysisLive.Serial;
                var submitsBefore = pipeline.SubmitCount;
                var fenceBefore = pipeline.FenceWaitTotalMs;
                var performanceMark = PerformanceMark();
                var renderWatch = System.Diagnostics.Stopwatch.StartNew();
                var submitted = pipeline.RenderPathView(renderSettings, state, width, height);
                renderWatch.Stop();
                if (!submitted)
                {
                    return false;
                }

                RecordPerformanceViewport(pipeline, renderWatch.Elapsed.TotalMilliseconds, submitsBefore,
                    fenceBefore, width, height, pathTracing: true);
                MeasureSince(performanceMark, ref _analysisLive.ViewportCpuMs);
            }
            else if (!pipeline.Present(renderSettings, state, width, height))
            {
                return false;
            }

            _lastRenderGpuSubmitMs = nowMs;
            if (wantTrace)
            {
                _renderViewNeedsInitialRender = false;
            }

            StartupTimeline.Mark(StartupTimeline.MilestoneRenderFrame);
            DrawPipelineImage(drawList, pipeline, frameOrigin, frameSize);
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
        var visibleWorld = GetSceneInspectionWorld(GetRenderWorld(uint.MaxValue, sceneVisibility: true));
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
        if (_historyConfirmation is not null) return;
        var io = ImGui.GetIO();
        if (_welcomeWindow?.IsOpen == true || _projectOperation.IsRunning || !IsFocused)
        {
            _brushPreview = null;
            return;
        }

        var mouse = io.MousePos;
        if (_toolDragAnchor is null && (!ImGui.IsWindowHovered() || mouse.X < origin.X || mouse.X > origin.X + size.X ||
            mouse.Y < origin.Y || mouse.Y > origin.Y + size.Y))
        {
            _brushPreview = null;
            return;
        }

        if (_activeTool == EditorTool.Pick && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var hit = PickVoxel(mouse, origin, size, camera);
            if (hit is { } picked && _sceneDocument is not null &&
                _objectVoxels.TryGetVisibleVoxel(_sceneDocument, uint.MaxValue, true, picked.Coordinate, out var owner, out _))
            {
                var target = _sceneDocument.Objects.FirstOrDefault(item => item.Id == owner);
                if (target is not null && !IsSceneSelectionLockedAgainst(target.Id))
                {
                    _toolDragAnchor = null; _toolDragCurrent = null; _toolDragTargetId = null;
                    _revealHierarchySelection = target.Id;
                    _hierarchySearch = string.Empty;
                    FocusHierarchyObject(target);
                    return;
                }
            }
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
            _brushPreview = null;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _selectedVoxel = PickVoxel(mouse, origin, size, camera);
                if (_selectedVoxel is { } pickedVoxel)
                {
                    _ = ResolveVoxelEditTarget(pickedVoxel.Coordinate);
                    if (_activeTool == EditorTool.Eyedropper && GetRenderWorld(uint.MaxValue, true).TryGet(pickedVoxel.Coordinate, out var value))
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
        var hoverErase = _activeTool == EditorTool.Erase || leftAction == ViewportMouseAction.Destroy;
        _brushPreview = hoverHit ?? (!hoverErase ? PickShapeSurface(mouse, origin, size, camera, erase: false) : null);
        _brushPreviewErase = hoverErase;
        if (_activeTool is EditorTool.Draw or EditorTool.Erase or EditorTool.Line or EditorTool.Box &&
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

        _ = ApplyMouseVoxelAction(leftAction, ImGuiMouseButton.Left, mouse, origin, size, camera);
        _ = ApplyMouseVoxelAction(rightAction, ImGuiMouseButton.Right, mouse, origin, size, camera);
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
        if (_activeTool == EditorTool.Draw || _activeTool == EditorTool.Erase)
            return _voxelEditTargetId;
        if (hitCoordinate is { } hit && _sceneDocument is not null &&
            _objectVoxels.TryGetVisibleVoxel(_sceneDocument, uint.MaxValue, true, hit, out var owner, out _))
        {
            if (IsSceneSelectionLockedAgainst(owner)) return null;
            _selectedSceneObjectId = owner;
            _revealHierarchySelection = owner;
            _hierarchySearch = string.Empty;
            return owner;
        }
        return null;
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
    /// 按当前调色盘与发光开关计算笔刷值
    /// </summary>
    private VoxelValue CurrentBrushValue(bool erase)
    {
        if (erase) return default;
        EnsureModelBrushMaterialTarget();
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
        if (_voxelEditTargetId is null) { SetStatus("请先在模型组件中进入绘制模式"); return; }
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
        // 缩放状态下继续雕刻时，先把当前尺寸定为新的 1x 基准，
        // 否则下次改缩放会从旧基准重采样，把这次新增的方块丢掉
        BakeScaleForEditing(target);
        var command = new VoxelStrokeCommand(name, _objectVoxels.GetOrCreate(target.Id), cells, value,
            ObjectEditCallback(target.Id));
        if (command.ChangedCellCount == 0)
        {
            SetStatus("此操作没有改变方块，撤销历史保持不变");
            return;
        }
        _voxelHistory.Execute(command);
        SetStatus($"{name} · {target.Name} · {command.ChangedCellCount:N0} 格（可撤销）");

        _pipeline?.ResetAccumulation();
        MarkSceneDocumentDirty();
        RecordVoxelAnimationFrame(target, command.ChangedCellCount);
    }

    private void CreateVoxelModel()
    {
        if (_project is null) return;
        _sceneDocument ??= new SceneDocument { Name = "主场景" };
        var sceneDocument = _sceneDocument;
        var firstModel = GetVoxelWorldObject() is null;
        var name = "体素模型";
        for (var suffix = 2; sceneDocument.Objects.Any(item => item.Name == name); suffix++)
            name = $"体素模型 {suffix}";
        var model = new SceneGraph(sceneDocument).Add(name,
            transform: TransformComponent.FromGrid(GridTransform.Identity));
        var relative = firstModel ? "Assets/Worlds/主世界.mvmodel" : $"Assets/Worlds/模型_{model.Id}.mvmodel";
        model.Components.Add(SceneComponentTypes.CreateVoxelModel(string.Empty, 0, "手动创建", name));
        _objectVoxels.GetOrCreate(model.Id);
        if (!firstModel) _layerOwnership[model.Id] = new HashSet<VoxelCoord64>();
        _selectedSceneObjectId = model.Id;
        InvalidateComposedWorldCache();
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
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
        if (_toolDragAnchor is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _toolDragAnchor = null;
            _toolDragCurrent = null;
            _toolDragTargetId = null;
            _brushPreview = null;
            SetStatus("已取消绘制");
            return true;
        }
        var destroy = _activeTool == EditorTool.Erase || leftAction == ViewportMouseAction.Destroy;
        var place = !destroy && leftAction == ViewportMouseAction.Place;
        if (!place && !destroy)
        {
            return false;
        }

        var button = ImGuiMouseButton.Left;
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
            var releaseHit = PickShapeSurface(mouse, origin, size, camera, _toolDragErase);
            var to = releaseHit is { } finalHit
                ? (_toolDragErase ? finalHit.Coordinate : finalHit.Coordinate + finalHit.Normal)
                : _toolDragCurrent ?? anchor;
            var cells = _activeTool == EditorTool.Line
                ? VoxelEditingTools.LineCells(anchor, to)
                : VoxelEditingTools.BoxCells(anchor, to, hollow: _activeTool == EditorTool.Box && _hollowBox);
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
        var above = camera.Position.Y >= 0F;
        return new VoxelDda.Hit(new VoxelCoord64((long)MathF.Floor(point.X), above ? -1 : 0, (long)MathF.Floor(point.Z)),
            new VoxelCoord64(0, above ? 1 : -1, 0), 0F, point);
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
            _runtimeMaterialSignature = string.Empty;
            RefreshRuntimeMaterialTable();
            if (materials.Count > 0)
                MVoxelLog.Info($"已加载 {materials.Count} 个共享材质资产");
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
        ImGui.PushID(path);
        ImGui.Text("材质");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        var name = material.Name;
        if (ImGui.InputText("名称", ref name, 48)) material.Name = name;

        DrawFollowableSlider("粗糙度", "粗糙度##mat", 0.01F, 1F, material.Roughness, value => material.Roughness = value);
        DrawFollowableSlider("金属度", "金属度##mat", 0F, 1F, material.Metallic, value => material.Metallic = value);
        DrawFollowableSlider("发光倍率", "发光倍率##mat", 0F, 32F, material.EmissiveScale, value => material.EmissiveScale = value);
        var useVoxelOpacity = material.Opacity is null;
        if (ImGui.Checkbox("不透明度跟随体素 Alpha", ref useVoxelOpacity))
            material.Opacity = useVoxelOpacity ? null : 0.25F;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("仅作用于半透明体素。跟随时使用体素颜色的 Alpha；0 更清澈，1 吸收更强。需要保存材质后生效。");
        if (!useVoxelOpacity)
        {
            var opacity = material.Opacity ?? 0.25F;
            SetSettingsSliderWidth("不透明度##mat");
            if (ImGui.SliderFloat("不透明度##mat", ref opacity, 0F, 1F, "%.2f")) material.Opacity = opacity;
        }
        DrawFollowableSlider("体积散射", "体积散射##mat", 0F, 1F, material.VolumeScattering, value => material.VolumeScattering = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("控制介质内部的雾状亮度。清澈玻璃通常接近 0；更大的值适合浑浊液体。默认 0.15。");
        DrawFollowableSlider("雾化密度", "雾化密度##mat", 0F, 8F, material.HazeDensity, value => material.HazeDensity = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("与颜色吸收独立的额外消光：0 无额外雾化，数值越大越浓；受不透明度和介质厚度影响。默认 0。");
        var scatterBase = material.ScatteringColor ?? new Rgba32(255, 255, 255);
        var scatterColor = new System.Numerics.Vector3(scatterBase.R / 255F, scatterBase.G / 255F, scatterBase.B / 255F);
        var followScatter = material.ScatteringColor is null;
        if (ImGui.Checkbox("散射色跟随体素基础色", ref followScatter))
            material.ScatteringColor = followScatter ? null : scatterBase;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("设置雾体受光后的颜色；需增加体积散射和雾化密度才能看出明显效果。");
        if (!followScatter && ImGui.ColorEdit3("散射色", ref scatterColor))
            material.ScatteringColor = new Rgba32(
                (byte)Math.Round(Math.Clamp(scatterColor.X, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(scatterColor.Y, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(scatterColor.Z, 0F, 1F) * 255F));
        DrawFollowableSlider("吸收步长", "吸收步长##mat", 0F, 8F, material.AbsorptionStep, value => material.AbsorptionStep = value);
        DrawFollowableSlider("折射率", "折射率##mat", 1F, 2.5F, material.Ior, value => material.Ior = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1 表示不偏折；水约 1.33，玻璃约 1.5，钻石约 2.4。\n未设置时不偏折。");
        DrawFollowableSlider("镜面强度", "镜面强度##mat", 0F, 1F, material.Specular, value => material.Specular = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("菲涅尔 F0 倍率。0 完全无高光（哑光），1 为标准值，金属反射色不受影响。");

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

        ImGui.TextDisabled("当前场景引用");
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("未打开场景");
        }
        else
        {
            var relativeMaterialPath = Path.GetRelativePath(_project!.AssetsPath, path).Replace('\\', '/');
            var references = AssetReferences.FindReferencingObjects(_sceneDocument, relativeMaterialPath);
            if (references.Count == 0) ImGui.TextDisabled("当前场景没有引用");
            foreach (var reference in references)
            {
                ImGui.PushID(reference.Id.ToString());
                if (ImGui.SmallButton(reference.Name))
                {
                    _selectedSceneObjectId = reference.Id;
                    _voxelEditTargetId = reference.Id;
                    FocusHierarchyObject(reference);
                }
                ImGui.SameLine();
                ImGui.TextDisabled("模型组件");
                ImGui.PopID();
            }
        }
        ImGui.Spacing();
        if (ImGui.Button("保存材质", new Vector2(120F, 30F)))
        {
            try
            {
                material.Save(path);
                ReloadMaterialTable();
                SetStatus($"材质已保存并生效：{material.Name}");
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

        ImGui.PopID();
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
                _historyEnvironment = new HistoryEnvironment(pixels, envWidth, envHeight);
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
            _historyEnvironment = new HistoryEnvironment([], 0, 0);
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
        if (io.WantTextInput || !IsFocused || _historyConfirmation is not null)
        {
            _historyRepeatDirection = 0;
            _undoHeldSeconds = _undoRepeatAccumulator = 0F;
            return;
        }
        var undoDown = ShortcutBindingRouter.IsDown(scheme, "edit.undo");
        var redoDown = ShortcutBindingRouter.IsDown(scheme, "edit.redo") ||
            ShortcutBindingRouter.IsDown(scheme, "edit.redoAlternate");
        if (_historyAwaitKeyRelease)
        {
            if (!undoDown && !redoDown) _historyAwaitKeyRelease = false;
            return;
        }
        var direction = undoDown ? -1 : redoDown ? 1 : 0;
        var pressed = direction < 0 ? ShortcutBindingRouter.IsPressed(scheme, "edit.undo") :
            ShortcutBindingRouter.IsPressed(scheme, "edit.redo") || ShortcutBindingRouter.IsPressed(scheme, "edit.redoAlternate");
        if (direction == 0 || direction != _historyRepeatDirection)
            _undoHeldSeconds = _undoRepeatAccumulator = 0F;
        if (direction != 0)
        {
            if (pressed) { if (direction < 0) ApplyUndo(); else ApplyRedo(); }
            else
            {
                var previous = _undoHeldSeconds;
                _undoHeldSeconds += Math.Max(0F, io.DeltaTime);
                var delay = _editorSettings.UndoRepeatDelayMs / 1000F;
                _undoRepeatAccumulator += Math.Max(0F, _undoHeldSeconds - Math.Max(previous, delay));
                var interval = _editorSettings.UndoRepeatIntervalMs / 1000F;
                for (var count = 0; count < 8 && _undoRepeatAccumulator >= interval; count++)
                {
                    _undoRepeatAccumulator -= interval;
                    if (!(direction < 0 ? ApplyUndo() : ApplyRedo())) { _undoRepeatAccumulator = 0F; break; }
                }
            }
        }
        _historyRepeatDirection = direction;

        if (ShortcutBindingRouter.IsPressed(scheme, "project.new")) ShowProjectPathDialog(create: true);
        if (ShortcutBindingRouter.IsPressed(scheme, "project.open")) ShowProjectPathDialog(create: false);
        if (ShortcutBindingRouter.IsPressed(scheme, "project.save")) SaveProject();
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.camera")) _activeTool = EditorTool.Camera;
        if (_voxelEditTargetId is not null && ShortcutBindingRouter.IsPressed(scheme, "viewport.draw")) _activeTool = EditorTool.Draw;
        if (_voxelEditTargetId is not null && ShortcutBindingRouter.IsPressed(scheme, "viewport.erase")) _activeTool = EditorTool.Erase;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.toggleGrid")) _showGrid = !_showGrid;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.togglePanels"))
        {
            var visible = _showHierarchy && _showProjectAssets && _showInspector;
            _showHierarchy = !visible;
            _showProjectAssets = !visible;
            _showInspector = !visible;
            _showModelingTools = !visible;
            if (visible) _showRenderConfig = false;
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
    private bool ApplyUndo() => ApplyHistory(false);
    private bool ApplyRedo() => ApplyHistory(true);

    private bool ApplyHistory(bool forward)
    {
        if (_historyReplaying || _historyConfirmation is not null || _projectOperation.Snapshot is not null || _screenColorSampling) return false;
        if (_sceneGizmoDragging) EndSceneGizmoDrag();
        _sceneFocusAnimation = null;
        _renderFocusAnimation = null;
        EndSceneMouseCapture();
        EndEditorHistory(force: true);
        var command = forward ? _voxelHistory.PeekRedo() : _voxelHistory.PeekUndo();
        if (command is null) { SetStatus(forward ? "没有可重做的操作" : "没有可撤销的操作"); return false; }
        if (command is IHistoryWarning warning && warning.GetWarning(forward) is { } message)
        {
            _historyConfirmation = (command, forward, message);
            _openHistoryConfirmation = true;
            _historyAwaitKeyRelease = true;
            _historyRepeatDirection = 0;
            _undoHeldSeconds = _undoRepeatAccumulator = 0F;
            return false;
        }
        return ExecuteHistory(command, forward);
    }

    private bool ExecuteHistory(IEditorCommand command, bool forward)
    {
        if (!ReferenceEquals(command, forward ? _voxelHistory.PeekRedo() : _voxelHistory.PeekUndo()))
        { SetStatus("历史已变化，请重新选择操作"); return false; }
        try
        {
            if (!(forward ? _voxelHistory.Redo() : _voxelHistory.Undo())) return false;
            SetStatus($"已{(forward ? "重做" : "撤销")}：{command.Name}（剩余 {(forward ? _voxelHistory.RedoCount : _voxelHistory.UndoCount)} 步）");
            return true;
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("撤销/重做失败，历史记录已保留", exception);
            SetStatus($"操作未完成，历史记录已保留：{exception.Message}");
            return false;
        }
    }

    private void DrawVoxelBrushPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_toolDragAnchor is { } anchor)
        {
            var end = _toolDragCurrent ?? anchor;
            if (_previewShapeAnchor != anchor || _previewShapeEnd != end ||
                _previewShapeTool != _activeTool || _previewShapeHollow != _hollowBox)
            {
                _previewShapeAnchor = anchor;
                _previewShapeEnd = end;
                _previewShapeTool = _activeTool;
                _previewShapeHollow = _hollowBox;
                _previewShapeCells = _activeTool == EditorTool.Line
                    ? VoxelEditingTools.LineCells(anchor, end)
                    : VoxelEditingTools.BoxCells(anchor, end, _activeTool == EditorTool.Box && _hollowBox);
            }
            var color = ToPackedColor(_toolDragErase
                ? Color.FromArgb(210, 255, 112, 120) : Color.FromArgb(235, 93, 187, 255));
            foreach (var cell in _previewShapeCells)
                DrawCellWireframe(drawList, origin, size, camera, cell, 0.5F, color, 1.25F);
            return;
        }

        _previewShapeAnchor = null;
        _previewShapeCells = [];
        if (_brushPreview is not { } hit) return;
        var previewCell = _brushPreviewErase ? hit.Coordinate : hit.Coordinate + hit.Normal;
        DrawCellWireframe(drawList, origin, size, camera, previewCell, 0.52F,
            ToPackedColor(Color.FromArgb(_brushPreviewErase ? 210 : 235,
                _brushPreviewErase ? 255 : 93, _brushPreviewErase ? 112 : 187, _brushPreviewErase ? 120 : 255)), 1.6F);
    }

    private VoxelWorld? _selectedSubtreeWorld;
    private int _selectedSubtreeSignature;

    /// <summary>准备选中对象及可见子对象，变化时才重新合并。</summary>
    private VoxelWorld? GetSelectedSubtreeWorld()
    {
        if (_sceneDocument is null || _selectedSceneObjectId is not { } selectedId ||
            GetSelectedSceneObject() is not { } selected || !IsSceneObjectVisible(selected) ||
            !_sceneDocument.IsLayerVisible(selected.LayerIndex)) return null;
        var ids = new HashSet<StableId> { selectedId };
        var children = _sceneDocument.Objects.Where(item => item.ParentId is not null).ToLookup(item => item.ParentId!.Value);
        var queue = new Queue<StableId>();
        queue.Enqueue(selectedId);
        while (queue.TryDequeue(out var parent))
            foreach (var child in children[parent])
                if (ids.Add(child.Id)) queue.Enqueue(child.Id);
        var hash = new HashCode();
        hash.Add(selectedId);
        hash.Add(_sceneSliceEnabled);
        if (_sceneSliceEnabled)
        {
            hash.Add(_sceneSliceAxis);
            hash.Add(_sceneSliceCoordinate);
            hash.Add(_sceneSliceKeepGreater);
        }
        foreach (var item in _sceneDocument.Objects)
        {
            if (!ids.Contains(item.Id)) continue;
            hash.Add(item.Id);
            var visible = IsSceneObjectVisible(item) && _sceneDocument.IsLayerVisible(item.LayerIndex) &&
                item.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) && component.Enabled);
            hash.Add(visible);
            if (visible && _objectVoxels.TryGetWorld(item.Id, out var objectWorld))
            {
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(objectWorld));
                hash.Add(objectWorld.ContentVersion);
            }
        }
        var signature = hash.ToHashCode();
        if (_selectedSubtreeWorld is null || _selectedSubtreeSignature != signature)
        {
            var combined = new VoxelWorld();
            var writer = combined.CreateBulkWriter();
            foreach (var item in _sceneDocument.Objects)
            {
                if (!ids.Contains(item.Id) || !IsSceneObjectVisible(item) ||
                    !_sceneDocument.IsLayerVisible(item.LayerIndex) ||
                    !item.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) && component.Enabled) ||
                    !_objectVoxels.TryGetWorld(item.Id, out var part)) continue;
                foreach (var (coordinate, value) in part.EnumerateOccupied()) writer.Set(coordinate, value);
            }
            _selectedSubtreeWorld = _sceneSliceEnabled
                ? VoxelWorldSlice.CreateHalfSpace(combined, _sceneSliceAxis, _sceneSliceCoordinate, _sceneSliceKeepGreater)
                : combined;
            _selectedSubtreeSignature = signature;
        }
        return _selectedSubtreeWorld;
    }

    /// <summary>
    /// 把一个格子画成线框立方体，全部角点都在相机后方时返回 false
    /// </summary>
    private static bool DrawCellWireframe(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        VoxelCoord64 cell, float half, uint color, float thickness)
    {
        var center = new Vector3((float)cell.X + 0.5F, (float)cell.Y + 0.5F, (float)cell.Z + 0.5F);
        Span<Vector3> corners = stackalloc Vector3[]
        {
            center + new Vector3(-half, -half, -half), center + new Vector3(half, -half, -half),
            center + new Vector3(half, half, -half), center + new Vector3(-half, half, -half),
            center + new Vector3(-half, -half, half), center + new Vector3(half, -half, half),
            center + new Vector3(half, half, half), center + new Vector3(-half, half, half),
        };
        Span<Vector2> points = stackalloc Vector2[8];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!SceneProjection.Project(corners[index], origin, size, camera, out points[index], out _)) return false;
        }

        ReadOnlySpan<(int Start, int End)> edges = stackalloc (int, int)[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (start, end) in edges)
        {
            drawList.AddLine(points[start], points[end], color, thickness);
        }

        return true;
    }

    // 工具栏和色板

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
        if (CenteredModal.Begin("洪水填充达到上限##flood_confirm", ref _pendingFloodOpen,
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
        DrawRenderSettingsAccordion("environment", "世界环境", () => DrawEnvironmentSection(settings));
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
        if (ImGui.Checkbox("太阳平行光", ref settings.SunEnabled) && ReferenceEquals(settings, _renderSettings))
            _settingsToggleSun = settings.SunEnabled;
        ImGui.Checkbox("强制使用 Compute 路径", ref settings.ForceComputePath);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("忽略显卡的硬件光追能力，强制走 Compute 计算路径。RDNA2 等支持 ray query 但实际更慢的显卡可据此对比；切换后需重建渲染设备或重启编辑器生效。");
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
            if (ImGui.Checkbox("角点环境光遮蔽", ref settings.AmbientOcclusionEnabled) && ReferenceEquals(settings, _renderSettings))
                _settingsToggleAo = settings.AmbientOcclusionEnabled;
            SliderFloat("AO 强度", ref settings.AoStrength, 0F, 1F, "%.2f");
            SliderFloat("AO 对比", ref settings.AoPower, 0.3F, 3F, "%.2f");
            SliderInt("AO 射线采样数", ref settings.AoSamples, 4, 64);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("每个可见表面像素发出的 AO 射线数；更多采样更平滑但更耗时。默认 16，优先保证性能。");
            SliderFloat("体素间接光强度", ref settings.VoxelIndirectLightStrength, 0F, 4F, "%.2f");
            SliderInt("体素间接光采样数", ref settings.VoxelIndirectLightSamples, 1, 32);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("每个像素的间接光方向数。实时通道没有跨帧累积，这一项直接决定间接光噪声：4 个方向明显发毛，16 个方向基本可用，代价是求交次数同比上升。");
            SliderFloat("探针体积GI强度", ref settings.ProbeVolumeGiStrength, 0F, 4F, "%.2f");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("探针 GI 对环境的补光权重，依赖探针体积窗口里烘焙出的数据。");
            SliderFloat("实时时间累积", ref settings.RealtimeTemporalWeight, 0F, 0.98F, "%.2f");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("非光追通道的时间累积权重。0 表示不累积，每帧独立采样、画面一直发毛；0.85 左右静止几帧后噪声基本收敛。相机或设置变化时历史自动作废。");
        }

    }

    private void DrawEnvironmentSection(RenderSettings settings)
    {
        var environmentBefore = new SceneEnvironmentSettings();
        environmentBefore.CaptureFrom(settings);
        var previousEnvironment = System.Text.Json.JsonSerializer.Serialize(environmentBefore, EnvironmentJsonOptions);
        ImGui.SeparatorText("天空与地面");
        if (ImGui.Checkbox("显示地面平面", ref settings.ShowGround) && ReferenceEquals(settings, _renderSettings))
            _settingsToggleGround = settings.ShowGround;
        DrawColorSlider("地面颜色", ref settings.GroundColor);
        SliderFloat("地面地平线过渡（0=纯色无限地面）", ref settings.GroundFade, 0F, 1024F, "%.0f");
        DrawColorSlider("天顶色", ref settings.SkyTop);
        DrawColorSlider("地平线色", ref settings.SkyHorizon);
        SliderFloat("天空环境光##sky", ref settings.SkyAmbient, 0F, 1.5F, "%.2f");
        SliderFloat("暖色补光##sky", ref settings.AmbientTint, 0F, 0.8F, "%.2f");
        ImGui.SeparatorText("水面");
        if (ImGui.Checkbox("显示水面", ref settings.ShowWater) && settings.ShowWater)
            settings.WaterAnimationEnabled = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("无限水平水面；波浪改变表面法线，不改变轮廓或产生透明折射。");
        SliderFloat("水面高度", ref settings.WaterHeight, -1024F, 1024F, "%.1f 格");
        DrawColorSlider("水面颜色", ref settings.WaterColor);
        SliderFloat("波浪大小", ref settings.WaterWaveSize, 0F, 4F, "%.2f");
        SliderFloat("波长", ref settings.WaterWaveLength, 0.1F, 256F, "%.1f 格");
                 SliderFloat("水面粗糙度", ref settings.WaterRoughness, 0.01F, 1F, "%.2f");
         ImGui.SeparatorText("水面动画与Shader");
         ImGui.Checkbox("启用实时水面动画", ref settings.WaterAnimationEnabled);
         if (ImGui.IsItemHovered()) ImGui.SetTooltip("驱动流动波纹；路径追踪动态预览会重新累积，截图时固定水面时刻。");
         SliderFloat("动画速度", ref settings.WaterAnimationSpeed, -4F, 4F, "%.2f");
         SliderFloat("流向X", ref settings.WaterFlowDirection.X, -1F, 1F, "%.2f");
         SliderFloat("流向Z", ref settings.WaterFlowDirection.Y, -1F, 1F, "%.2f");
         SliderFloat("细节扰动", ref settings.WaterDetailStrength, 0F, 2F, "%.2f");
         SliderFloat("高光强度", ref settings.WaterSpecularStrength, 0F, 4F, "%.2f");
         ImGui.SeparatorText("天气与大气");
         SliderFloat("风强度", ref settings.WeatherWindStrength, 0F, 4F, "%.2f");
         SliderFloat("风向", ref settings.WeatherWindDirection, 0F, 360F, "%.0f°");
         SliderFloat("降雨强度", ref settings.WeatherRainIntensity, 0F, 1F, "%.2f");
         SliderFloat("降雪强度", ref settings.WeatherSnowIntensity, 0F, 1F, "%.2f");
         SliderFloat("雷暴强度", ref settings.WeatherStormIntensity, 0F, 1F, "%.2f");
         SliderFloat("热浪折射散射", ref settings.WeatherHeatHaze, 0F, 1F, "%.2f");
         SliderFloat("雷电频率", ref settings.WeatherLightningFrequency, 0F, 4F, "%.2f");
        ImGui.SeparatorText("雾气");
        DrawColorSlider("雾颜色", ref settings.FogColor);
        SliderFloat("雾浓度（0=关）", ref settings.FogDensity, 0F, 0.02F, "%.4f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("距离雾按相机到表面的距离混合雾色，作用于实时渲染和路径追踪。");
        SliderFloat("雾起始距离", ref settings.FogStart, 0F, 1024F, "%.0f 格");
        SliderFloat("雾颜色权重", ref settings.FogSkyBlend, 0F, 1F, "%.2f");
        SliderFloat("雾最大不透明度", ref settings.FogMaxOpacity, 0F, 1F, "%.2f");
        SliderFloat("近处淡入距离（0=关）", ref settings.NearFade, 0F, 256F, "%.0f");
        if (ReferenceEquals(settings, _renderSettings) && _sceneDocument is not null)
        {
            var environmentAfter = new SceneEnvironmentSettings();
            environmentAfter.CaptureFrom(settings);
            if (previousEnvironment != System.Text.Json.JsonSerializer.Serialize(environmentAfter, EnvironmentJsonOptions))
            {
                _sceneDocument.Environment = environmentAfter;
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
            }
        }
    }

    private void DrawPaletteSection(RenderSettings settings)
    {
        ImGui.SeparatorText("基础色与色相");
        Toggle("调色盘着色", ref _settingsTogglePalette, value => settings.PaletteShading = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("体素使用纯色，基础色来自当前调色盘，不使用贴图");
        SliderFloat("饱和度", ref settings.Saturation, 0F, 2.5F, "%.2f");
        SliderFloat("色相偏移", ref settings.HueShiftDegrees, 0F, 360F, "%.0f°");
        SliderFloat("对比度", ref settings.ColorContrast, 0.3F, 2.5F, "%.2f");
        SliderFloat("明度", ref settings.Brightness, 0.2F, 2F, "%.2f");
        Toggle("色相环动画", ref _settingsToggleHueCycle, value => settings.HueCycle = value);
        SliderFloat("色相环速度##cycle", ref settings.HueCycleSpeed, 0F, 90F, "%.0f°/秒");
    }

    private void DrawBrushProperties()
    {
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
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"当前笔刷包围尺寸：{_brushRadius * 2 - 1} × {_brushRadius * 2 - 1} × {_brushRadius * 2 - 1} 格；轮廓只标出中心格。球形笔刷位于此范围内。");
        if (_activeTool == EditorTool.Box)
        {
            ImGui.Checkbox("空心长方体", ref _hollowBox);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("只绘制边界，适合建造外壳；厚度为一格。两端落在同一平面时结果仍是平面。");
        }
        if (ImGui.Checkbox("自发光笔刷（提灯）", ref _brushEmissive))
        {
            SetStatus(_brushEmissive ? "新绘制的体素将带自发光标记" : "新绘制的体素为普通颜色");
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
        ImGui.Checkbox("发光面直接采样", ref settings.EmissiveDirectSampling);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("主动采样暴露发光面并与反弹命中做 MIS；关闭可比较采样收敛。仅用于路径追踪，不改变实时模式。");
        ImGui.Checkbox("光源树重要性采样", ref settings.EmissiveLightTree);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("超过 32 个发光面时按光源功率与接收点距离采样；关闭可与均匀采样比较。少量发光面仍使用均匀采样。");
        Toggle("太阳直接光采样", ref _settingsToggleMis, value => settings.SunDirectLighting = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("控制路径追踪中对太阳的直接采样；本地光源和自发光体素不受此开关控制。");
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
