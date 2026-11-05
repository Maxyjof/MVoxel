using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Editor.Assets;
using MVoxel.Project;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Animation;
using MVoxel.Voxels;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using MVoxel.Editor.Settings;
using MVoxel.Editor.Templates;
using MVoxel.Scene;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Color = System.Drawing.Color;
using OpenTkCursorState = OpenTK.Windowing.Common.CursorState;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using Vector3 = OpenTK.Mathematics.Vector3;
using NumVector3 = System.Numerics.Vector3;
using NumQuaternion = System.Numerics.Quaternion;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow : GameWindow, IEditorWelcomeHost
{
    private const int CapsLockVirtualKey = 0x14;
    private const uint MainDockspaceId = 0x6A3C3001;
    private const float HierarchyRowIconSize = 18F;
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly string? _startupProjectPath;
    private string? _imguiLayoutPath;
    private VulkanImGuiController? _controller;
    private VulkanWindowContext? _vulkan;
    private VulkanImGuiRenderer? _imguiRenderer;
    private readonly EditorFontCatalog _fonts = new();
    private readonly ProjectAssetBrowser _assetBrowser = new();

    /// <summary>体素世界缩略图缓存；随 ImGui 渲染器创建与销毁</summary>
    private ThumbnailCache? _thumbnailCache;
    private nint _assetPreviewTextureId;
    private string _assetPreviewPath = string.Empty;
    private string _lastAssetSelectionPath = string.Empty;
    private int _assetPreviewWidth;
    private int _assetPreviewHeight;
    private readonly BlockingOperationRunner _projectOperation = new();
    private ImGuiWelcomeContent? _welcomeWindow;
    private bool _showWelcomeRequested;
    private IntPtr _imguiContext;
    private ProjectHandle? _project;
    private bool _showHierarchy = true;
    private string _hierarchySearch = string.Empty;

    /// <summary>层级面板的图层过滤，-1 表示全部图层</summary>
    private string _newSceneLayerName = string.Empty;
    private int _renamingSceneLayer = -1;
    private string _sceneLayerRenameBuffer = string.Empty;
    private readonly HashSet<string> _expandedInspectorComponents = new(StringComparer.Ordinal);
    private StableId? _inspectorExpansionObjectId;
    private IReadOnlyList<string> _voxelModelReferenceCache = Array.Empty<string>();
    private string? _voxelModelReferenceCacheRoot;
    private bool _showProjectAssets = true;
    private bool _showInspector = true;
    private volatile bool _openWindowMenuRequested;
    private volatile bool _openInspectorPanelMenuRequested;
    private bool _showRenderConfig;
    private bool _showEnvironmentWindow;
    private bool _focusEnvironmentWindow;
    private bool _showProbeVolumeWindow;
    private bool _showAnimationWindow;
    private bool _showTimelineWindow;
    private bool _animationWindowLocked;
    private StableId? _animationLinkedObjectId;
    private int _probeVolumeSelection;
    private int _centerTab;
    private int? _requestedCenterTab;
    private int? _requestedInspectorTab;
    private string? _requestedRenderSettingsSection;
    private SceneDocument? _sceneDocument;
    private string _activeSceneRelativePath = "Scenes/主场景.mvox";
    private StableId? _renderCameraId;
    // 渲染窗口相机的临时导航锚点，不代表聚焦对象；中键平移时与相机同步移动。
    private Vector3? _renderOrbitAnchor;
    private bool _applyPreset;
    private bool _editPanelLayout;
    private LayoutPreset _layoutPreset = LayoutPreset.Default;
    private string _statusText = "就绪";
    private IReadOnlyList<string> _sceneValidationErrors = Array.Empty<string>();
    private float _sceneZoom = 1F;
    private float _sceneOrbitYaw = MathF.PI / 4F;
    private float _sceneOrbitPitch = 0.48F;
    private float _sceneCameraDistance = 18F;
    private Vector2 _scenePan;
    private float _sceneFreeMoveHeight;
    private float _sceneMoveSpeed = 12F;
    private float _sceneNearClip = 0.05F;
    private float _sceneFarClip = 100000F;
    private float _sceneFieldOfView = 50F;
    private bool _showGrid = true;
    private bool _showVoxelOutlines = true;
    private bool _showSceneStatistics;
    private bool _sceneMouseCaptured;
    private ViewportCaptureTarget _capturedViewport;
    private bool _sceneDocumentDirty;
    private bool _exitConfirmationRequested;
    private bool _allowWindowClose;
    private float _autoSaveCountdown = float.PositiveInfinity;
    private bool _autoSaveQueued;

    /// <summary>
    /// 记录当前捕获鼠标的视口
    /// </summary>
    private enum ViewportCaptureTarget
    {
        None,
        Scene,
        Render,
    }

    // 体素编辑和 GPU 渲染
    private VoxelWorld _world = new();
    private Rgba32[] _palette = VoxelPalette.Build();
    private bool _paletteIsProjectLevel;
    private CommandHistory _voxelHistory = new();
    private VulkanVoxelRenderPipeline? _pipeline;
    private RenderSettings _renderSettings = new();
    private long _renderSettingsInteractionAt;
    private EditorTool _activeTool = EditorTool.Pick;
    private StableId? _voxelEditTargetId;
    private StableId? _inspectorLockedObjectId;
    private sealed class ExtraInspector { public int Id; public string Kind = "检查器"; public StableId? Locked; public bool Open = true; public uint DockId; }
    private readonly List<ExtraInspector> _extraInspectors = new();
    private int _nextInspectorId;
    private VoxelBrushShape _brushShape = VoxelBrushShape.Sphere;
    private int _brushRadius = 1;
    private int _paletteIndex = 40;
    private bool _brushEmissive;
    private string? _componentWarning;
    private string _componentSearch = string.Empty;
    private VoxelDda.Hit? _selectedVoxel;
    private StableId? _selectedSceneObjectId;
    private StableId? _revealHierarchySelection;
    private StableId? _pinnedVoxelModelId;
    private VoxelDda.Hit? _brushPreview;
    private float _elapsedSeconds;
    private float _undoHeldSeconds;
    private float _undoRepeatAccumulator;
    private string _accumulationSignature = string.Empty;
    // 只影响后处理的显示签名：变化时重跑收尾通道即可，不丢弃已完成的路径样本
    private string _accumulationDisplaySignature = string.Empty;
    private string _pipelineStatus = string.Empty;
    private bool _gpuRenderingDisabled;
    private bool _pipelineInitializationAttempted;
    private long _lastRenderGpuSubmitMs;
    private bool _renderViewNeedsInitialRender = true;
    private bool _renderCameraInitialFramePending = true;
    private bool _renderViewWasFocused;
    private Vector2 _lastSceneViewportSize;
    private Vector2 _lastRenderViewportSize;
    private Queue<ExportJob> _exportQueue = new();
    private ExportJob? _activeExport;
    private bool _exportQueueExpanded;
    private bool _cancelActiveExportRequested;
    private int _exportSequence;
    private readonly List<CpuPreviewSurfaceFace> _cpuPreviewSurfaceFaces = [];
    private VoxelWorld? _cpuPreviewWorld;
    private int _cpuPreviewWorldVersion = -1;
    private bool _cpuPreviewIncludesInterior;
    private EditorControlServer? _controlServer;
    private PluginScriptRuntime? _pluginRuntime;
    private readonly Dictionary<string, string> _pluginCommandResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pluginCommandInputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiblePluginPanels = new(StringComparer.Ordinal);

    public ImGuiEditorWindow(string[] args)
        : base(GameWindowSettings.Default, CreateWindowSettings(args))
    {
        // 显式在原生窗口创建后再应用图标，确保标题栏和任务栏使用品牌图，
        // 避免部分 Windows/GLFW 初始化顺序下保留默认应用图标。
        Icon = AppBranding.LoadWindowIcon();
        ApplyWindowsTaskbarBranding();
        IsVisible = true;
        _startupProjectPath = ResolveStartupProjectPath(args);
    }

    private unsafe void ApplyWindowsTaskbarBranding() =>
        AppBranding.ApplyWindowsTaskbarIcon(GLFW.GetWin32Window(WindowPtr));

    internal static ImGuiEditorWindow Create(string[] args) => new(args);

    public event EventHandler? ProjectOpened;

    public event EventHandler<ProjectOperationFailedEventArgs>? ProjectOperationFailed;

    public bool HasStartupProject => !string.IsNullOrWhiteSpace(_startupProjectPath);

    public bool HasProject => _project is not null;

    public string DefaultProjectRootPath => string.IsNullOrWhiteSpace(_editorSettings.ProjectRootPath)
        ? EditorSettingsDocument.DefaultProjectRootPath
        : _editorSettings.ProjectRootPath.Trim().Trim('"');

    public void StartNewProjectFromWelcome(string path, ProjectTemplateId templateId) =>
        _pendingActions.Enqueue(() => BeginCreateProject(path, templateId, fromWelcome: true));

    public void StartVoxProjectFromWelcome(string voxFilePath) =>
        _pendingActions.Enqueue(() => BeginCreateVoxProject(voxFilePath, fromWelcome: true));

    public void OpenProjectFromWelcome(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public void OpenRecentProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public bool IsProjectOperationRunning => _projectOperation.IsRunning;

    public BlockingOperationSnapshot? ProjectOperationSnapshot => _projectOperation.Snapshot;

    public void CancelProjectOperation() => _projectOperation.RequestCancel();

    public void CloseEditor() => RequestExit();

    private void RequestExit()
    {
        if (IsAssetStage) { SetStatus("请先从资产编辑返回场景"); return; }
        if (_projectOperation.IsRunning)
        {
            SetStatus("请等待当前操作完成，或先在进度窗口取消操作，再退出");
            return;
        }
        if (_project is not null && TimelineIsDirty)
        {
            ConfirmTimelineChangesBefore(RequestExit);
            return;
        }
        if (_project is not null && (_sceneDocumentDirty || _generatorDirty))
        {
            RequestExitDialog();
            return;
        }

        _allowWindowClose = true;
        Close();
    }

    /// <summary>请求显示退出确认框，供界面与自动化共用</summary>
    private void RequestExitDialog()
    {
        _exitConfirmationRequested = true;
        _exitDialogOpen = true;
        _exitDialogResolved = false;
    }

    /// <summary>自动化已直接决定关闭，收起仍开着的确认框</summary>
    private void DismissExitDialog()
    {
        _exitConfirmationRequested = false;
        _exitDialogOpen = false;
        _exitDialogResolved = true;
    }

    /// <summary>自动化已直接决定关闭项目，收起仍开着的确认框</summary>
    private void DismissProjectCloseDialog()
    {
        _closeProjectDialogRequested = false;
        _projectCloseDialogOpen = false;
        _projectCloseDialogResolved = true;
        _afterProjectClose = null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (IsAssetStage) { e.Cancel = true; SetStatus("请先从资产编辑返回场景"); return; }
        if (_projectOperation.IsRunning)
        {
            e.Cancel = true;
            SetStatus("请等待当前操作完成，或先取消操作，再退出");
            return;
        }
        if (e.Cancel || _allowWindowClose || _project is null) return;
        if (TimelineIsDirty)
        {
            e.Cancel = true;
            ConfirmTimelineChangesBefore(RequestExit);
            return;
        }
        if (!_sceneDocumentDirty && !_generatorDirty) return;

        e.Cancel = true;
        RequestExitDialog();
    }

    internal void RequestOpenProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: false));

    protected override void OnLoad()
    {
        base.OnLoad();
        using var startupPhase = StartupTimeline.Begin("编辑器启动");
        CenterOnCurrentMonitor();
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
        //让 ImGui 决定窗口光标形状：可拖动字段显示左右调整光标，输入框显示文本光标
        _controller.ManageCursor = true;
        _imguiContext = ImGui.GetCurrentContext();
        _fonts.Load(_controller, UiDiagnostics.Dpi(this));
        _imguiRenderer = new VulkanImGuiRenderer(_vulkan, _controller);
        _thumbnailCache = new ThumbnailCache(_imguiRenderer);
        _assetBrowser.Thumbnails = _thumbnailCache;
        LoadEditorSettings();
        EditorPalette.ApplyTheme(_editorSettings.Theme);
        ConfigureImGui();
        UiDiagnostics.Configure();
        _controlServer = new EditorControlServer(HandleControlCommand);
        _controlServer.Start();
        _palette = EditorPalettes.ResolveEditorPalette();
        var startupProjectPath = _startupProjectPath;
        if (!string.IsNullOrWhiteSpace(startupProjectPath))
        {
            RequestWelcomeWindow();
            EnsureWelcomeWindow();
            _pendingActions.Enqueue(() => BeginOpenProject(startupProjectPath, fromWelcome: true));
        }
        else
        {
            RequestWelcomeWindow();
            EnsureWelcomeWindow();
        }

        StartupTimeline.Mark(StartupTimeline.MilestoneWindowReady);
    }

    protected override void OnResize(ResizeEventArgs args)
    {
        base.OnResize(args);
        _vulkan?.RequestResize();
    }

    /// <summary>
    /// 将主窗口放到当前显示器工作区中央
    /// </summary>
    private void CenterOnCurrentMonitor()
    {
        var workArea = Monitors.GetMonitorFromWindow(this).WorkArea;
        var windowSize = Bounds.Size;
        if (windowSize.X <= 0 || windowSize.Y <= 0) windowSize = ClientSize;
        Location = workArea.Min + new Vector2i(
            Math.Max(0, (workArea.Size.X - windowSize.X) / 2),
            Math.Max(0, (workArea.Size.Y - windowSize.Y) / 2));
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        var desiredTitle = _project is null ? "MVoxel 体素建模" :
            $"MVoxel 体素建模 - {(_sceneDocumentDirty || _generatorDirty || HasSuspendedSceneChanges ? "*" : "")}{_project.Manifest.Name} - {ActiveEditingName}";
        if (!string.Equals(Title, desiredTitle, StringComparison.Ordinal)) Title = desiredTitle;
        if (!IsFocused || !IsCapsLockEnabled())
        {
            EndSceneMouseCapture();
        }

        _controlServer?.PumpOnMainThread();
        AdvanceTimelineWeather(args.Time);

        if (_project is not null && _sceneDocument is not null)
        {
            // 编辑器预览保持静止；动画只在显式时间轴或视频导出上下文中推进
        }

        while (_historyConfirmation is null && _pendingActions.TryDequeue(out var action))
        {
            BeginEditorHistory();
            using (_historyFiles.Activate()) action();
            EndEditorHistory(force: true);
        }

        if (UiDiagnostics.TakeBlockingOperationRequest()) BeginDiagnosticLoad();
        if (UiDiagnostics.TakeSettingsRequest()) OpenSettings(0, 2, "editor.input.shortcuts");
        if (UiDiagnostics.TakeExportRequest() && _centerTab == 1) ExportRenderPng();
        if (_historyConfirmation is null) _projectOperation.Pump();
        if (_historyConfirmation is null) PumpProbeBake();
        if (_project is null && _welcomeWindow?.IsOpen != true) RequestWelcomeWindow();
        EnsureWelcomeWindow();
        if (_project is not null && _editorSettings.AutoSave && _sceneDocumentDirty)
        {
            _autoSaveCountdown -= (float)Math.Max(args.Time, 0.01F);
            if (_autoSaveCountdown <= 0F && !_autoSaveQueued)
            {
                _autoSaveQueued = true;
                _autoSaveCountdown = _editorSettings.AutoSaveSeconds;
                _pendingActions.Enqueue(TryAutoSaveProject);
            }
        }
        ProcessConfigurationAutoSave((float)Math.Max(args.Time, 0.01F));
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        var analysisStart = PerformanceProbeActive ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        BeginPerformanceFrame();
        try
        {
            base.OnRenderFrame(args);
            //设备丢失后先重建 Vulkan 设备，否则整个窗口会因为无法呈现而冻结
            RecoverVulkanDeviceIfNeeded();
            ImGui.SetCurrentContext(_imguiContext);
            var updateMark = PerformanceMark();
            _fonts.Refresh(_controller!, UiDiagnostics.Dpi(this));
            _elapsedSeconds += (float)Math.Max(args.Time, 0.0);
            _controller?.Update((float)Math.Max(args.Time, 1.0 / 120.0));
            MeasureSince(updateMark, ref _analysisLive.UpdateMs);
            if (_controller is not null)
            {
                if (UiDiagnostics.ShouldCloseWelcome) _welcomeWindow?.RequestClose();
                var uiStart = PerformanceMark();
                DrawEditorFrame();
                MeasureSince(uiStart, ref _analysisLive.UiMs);
                ProcessCaptureView();
                if (_activeExport is not null || _exportQueue.Count > 0 || _exportSnapshot is not null)
                    ProcessExportQueue(GetRenderViewCamera(), _lastRenderViewportSize);
                var imguiStart = PerformanceMark();
                _controller.Render();
                MeasureSince(imguiStart, ref _analysisLive.ImGuiRenderMs);
                UiDiagnostics.Capture(this, "editor");
            }

            PollPerformanceGpuTimings();
            var submitStart = PerformanceMark();
            _vulkan?.RenderFrame(new System.Numerics.Vector4(0.05F, 0.07F, 0.10F, 1F), commandBuffer =>
            {
                if (_imguiRenderer is not null && _controller is not null)
                {
                    _imguiRenderer.Record(commandBuffer, _controller);
                }
            });
            MeasureSince(submitStart, ref _analysisLive.PresentMs);

            ImGui.SetCurrentContext(_imguiContext);
            UiDiagnostics.Advance(this, ref _sceneOrbitYaw, ref _sceneOrbitPitch, ref _sceneCameraDistance, ref _scenePan);
            UpdatePerformanceMemory(args.Time);
            if (analysisStart != 0L) RecordPerformanceFrame(args.Time, analysisStart);
        }
        catch (Exception exception)
        {
            MVoxelLog.SetRuntimeContext($"编辑器帧渲染，项目={_project?.Manifest.Name ?? "未打开项目"}，体素={_world.OccupiedVoxelCount}");
            MVoxelLog.RecordCrash("编辑器帧渲染异常", exception);
            _gpuRenderingDisabled = true;
            _allowWindowClose = true;
            Close();
        }
    }

    protected override void OnUnload()
    {
        CancelVideoEncodingsAndWait();
        CloseMcImport();
        _voxelHistory.Clear();
        EndSceneMouseCapture();
        if (!UiDiagnostics.Enabled && _imguiLayoutPath is not null)
        {
            ImGui.SaveIniSettingsToDisk(_imguiLayoutPath);
        }

        SaveEditorSettings();
        SaveProjectSettings();
        _welcomeWindow?.Dispose();
        _welcomeWindow = null;
        _projectOperation.Dispose();
        _controlServer?.Dispose();
        _controlServer = null;
        _pluginRuntime?.Dispose();
        _pluginRuntime = null;
        ImGui.SetCurrentContext(_imguiContext);
        ShutdownVoxelPipeline();
        _exportPipeline?.Dispose();
        _exportPipeline = null;
        if (_assetPreviewTextureId != 0) _imguiRenderer?.UnregisterTexture(_assetPreviewTextureId);
        _assetPreviewTextureId = 0;
        _assetBrowser.Thumbnails = null;
        _thumbnailCache?.Dispose();
        _thumbnailCache = null;
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _controller?.Dispose();
        _vulkan?.Dispose();
        _vulkan = null;
        base.OnUnload();
    }

    /// <summary>
    /// 把工作集构建的分阶段耗时整理成可序列化结构，供 CLI 与自动化读取
    /// </summary>
    /// <param name="pipeline">目标管线，可为空</param>
    /// <returns>各阶段毫秒数与合计</returns>
    private static object DescribeWorksetBuild(VulkanVoxelRenderPipeline? pipeline)
    {
        var timing = pipeline?.WorksetBuildTiming ?? default;
        return new
        {
            snapshotMs = timing.SnapshotMs,
            collectMs = timing.CollectMs,
            sortMs = timing.SortMs,
            cullMs = timing.CullMs,
            mergeMs = timing.MergeMs,
            gatherMs = timing.GatherMs,
            totalMs = timing.TotalMs,
        };
    }

    /// <summary>
    /// 处理 CLI 发来的只读状态和项目打开命令
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>命令结果</returns>
    private object HandleControlCommand(JsonElement request)
    {
        var method = request.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;
        return method switch
        {
            "editor.status" => new
            {
                processId = Environment.ProcessId,
                hasProject = _project is not null,
                projectName = _project?.Manifest.Name,
                sceneName = _sceneDocument?.Name,
                sceneObjectCount = _sceneDocument?.Objects.Count ?? 0,
                sceneComponentCount = _sceneDocument?.Objects.Sum(item => item.Components.Count) ?? 0,
                sceneValid = _sceneDocument is not null && _sceneValidationErrors.Count == 0,
                sceneValidationErrorCount = _sceneValidationErrors.Count,
                sceneDirty = _sceneDocumentDirty,
                autoSaveEnabled = _project is not null && _editorSettings.AutoSave,
                autoSaveSeconds = _project is null ? 0 : _editorSettings.AutoSaveSeconds,
                renderingDegraded = _gpuRenderingDisabled,
                renderingStatus = _pipelineStatus,
                renderSamples = _pipeline?.AccumulatedSamples ?? 0,
                renderPathTracing = _renderSettings.PathTracingEnabled,
                renderWorksetCells = _pipeline?.SyncTotalCells ?? 0,
                realtimeHistoryActive = _pipeline?.RealtimeHistoryActive ?? false,
                realtimeConvergedFrames = _pipeline?.RealtimeConvergedFrames ?? 0,
                usingHardwareRayTracing = _pipeline?.UsingHardwareRayTracing ?? false,
                hardwareRayQuerySupported = _pipeline?.SupportsHardwareRayQuery ?? false,
                hardwareShadowsActive = _pipeline?.HardwareShadowsActive ?? false,
                hardwareShadowStatus = _pipeline?.HardwareShadowStatus ?? "渲染管线未初始化",
                forceComputePath = _renderSettings.ForceComputePath,
                renderWorksetBuildMs = _pipeline?.SyncWorksetBuildMs ?? 0,
                renderWorksetSyncCount = _pipeline?.SyncCompletedCount ?? 0,
                renderWorksetBuild = DescribeWorksetBuild(_pipeline),
                performanceAnalyzerVisible = IsPerformanceAnalyzerVisible,
                centerTab = _centerTab,
                timeline = DescribeTimelineState(),
                pendingDialog = DescribePendingDialog(),
                videoExports = _videoExportInfos.OrderByDescending(item => item.StartedAt).Take(5).Select(DescribeVideoExport).ToArray(),
                exportQueue = new
                {
                    queued = _exportQueue.Count,
                    hasActive = _activeExport is not null,
                    activePath = _activeExport?.Path,
                    activeKind = _activeExport?.Video is null ? "image" : "video",
                    encoding = _videoEncodings.Count,
                },
                scenePipelineAvailable = _scenePipeline?.IsAvailable ?? false,
                renderPipelineAvailable = _pipeline?.IsAvailable ?? false,
                sceneWorksetCells = _scenePipeline?.SyncTotalCells ?? 0,
                sceneWorksetBuildMs = _scenePipeline?.SyncWorksetBuildMs ?? 0,
                sceneWorksetSyncCount = _scenePipeline?.SyncCompletedCount ?? 0,
                sceneWorksetBuild = DescribeWorksetBuild(_scenePipeline),
                sceneBvhBuildMs = _scenePipeline?.SyncBvhBuildMs ?? 0,
                scenePackMs = _scenePipeline?.SyncPackMs ?? 0,
                sceneUploadMs = _scenePipeline?.SyncUploadMs ?? 0,
                sceneEmissiveSyncCpuMs = _scenePipeline?.EmissiveSyncMilliseconds ?? 0,
                sceneDispatchCount = _scenePipeline?.LastDispatchCount ?? 0,
                performanceAnalyzerRecording = PerformanceProbeActive,
                performanceAnalyzerFrozen = _analysisPaused,
                performanceAnalyzerPaused = _analysisPaused,
                performanceFrameCount = _analysisFrameCount,
                performanceFrameCapacity = AnalysisFrameCapacity,
                frameIntervalMs = LatestAnalysisFrame.IntervalMs,
                renderCallbackWallMs = LatestAnalysisFrame.CallbackMs,
                frameCallbackMs = LatestAnalysisFrame.CallbackMs,
                frameUiMs = LatestAnalysisFrame.UiMs,
                frameViewportMs = LatestAnalysisFrame.ViewportCpuMs,
                framePresentMs = LatestAnalysisFrame.PresentMs,
                gpuFrameMs = LatestAnalysisFrame.GpuValid ? LatestAnalysisFrame.GpuMs : 0,
                gpuSamplingMs = LatestAnalysisFrame.GpuValid ? LatestAnalysisFrame.GpuSamplingMs : 0,
                gpuFinishMs = LatestAnalysisFrame.GpuValid ? LatestAnalysisFrame.GpuFinishMs : 0,
                lastResolvedGpuFrameMs = _lastResolvedGpuFrameMs,
                lastResolvedGpuSamplingMs = _lastResolvedGpuSamplingMs,
                lastResolvedGpuFinishMs = _lastResolvedGpuFinishMs,
                lastResolvedGpuToken = _lastResolvedGpuToken,
                gpuTimingSupported = (_pipeline ?? _scenePipeline)?.GpuTimingSupported ?? false,
                managedAllocationBytesPerSecond = _analysisAllocatedPerSecond,
                renderBvhBuildMs = _pipeline?.SyncBvhBuildMs ?? 0,
                renderPackMs = _pipeline?.SyncPackMs ?? 0,
                renderUploadMs = _pipeline?.SyncUploadMs ?? 0,
                emissiveFaces = _pipeline?.EmissiveFaceCount ?? 0,
                lightTreeNodes = _pipeline?.EmissiveTreeNodeCount ?? 0,
                emissiveSyncCpuMs = _pipeline?.EmissiveSyncMilliseconds ?? 0,
                automations = _pluginRuntime?.Automations ?? [],
                commands = _pluginRuntime?.Commands ?? [],
                panels = _pluginRuntime?.Panels ?? [],
                pluginDiagnostics = _project is null ? Array.Empty<ProjectPluginDiagnostic>() : PluginManager.Diagnose(_project),
                pluginActivationStatus = _pluginRuntime?.ActivationStatus,
                probeBakeActive = _probeBakeJob is not null,
                probeBakeProgress = _probeBakeJob?.Progress ?? 1F,
                probeBakeProbes = _probeBakeJob?.ProbeCount ?? 0,
                liveProbeSamples = _liveProbeBake?.Samples.Count ?? 0,
                probeBakeCompletedCount = _probeBakeCompletedCount,
                startup = StartupTimeline.Snapshot(),
            },
            "scene.validate" => ValidateCurrentSceneCommand(),
            "editor.saveProject" => QueueSaveProject(),
            "editor.closeProject" => QueueCloseProject(request),
            "editor.openProject" => QueueOpenProject(request),
            "editor.captureView" => QueueCaptureView(request),
            "editor.captureWindow" => CaptureEditorWindow(request),
            "editor.showWindowMenu" => RequestWindowMenu(),
            "editor.showPanelMenu" => RequestInspectorPanelMenu(),
            "editor.captureStatus" => CaptureStatus(request),
            "editor.exportVideo" => ExportVideoFromArguments(Unwrap(request)),
            "editor.exportStatus" => DescribeVideoExports(Unwrap(request)),
            "editor.cancelExport" => CancelVideoExportFromArguments(Unwrap(request)),
            "editor.pendingDialog" => DescribePendingDialog(),
            "editor.answerDialog" => AnswerDialog(Unwrap(request)),
            "editor.exitEditor" => QueueExitEditor(Unwrap(request)),
            "editor.selectObject" => QueueSelectObject(request),
            "editor.selection" => DescribeSelection(),
            "editor.commands" => ListEditorCommands(request),
            "editor.exec" => ExecuteEditorCommand(request),
            "editor.invokeAutomation" => InvokeAutomation(request),
            "editor.invokeCommand" => InvokeCommand(request),
            "editor.reloadPlugins" => QueueReloadPlugins(),
            "editor.installPlugin" => InstallPlugin(request),
            "editor.setPluginEnabled" => SetPluginEnabled(request),
            _ => throw new InvalidOperationException($"不支持的编辑器命令：{method}"),
        };
    }

    /// <summary>
    /// 按 id 或名称选中场景对象：供自动化验证检查器与画布状态，不依赖鼠标操作
    /// </summary>
    /// <param name="request">命令 JSON，可带 id 或 name</param>
    /// <returns>投递结果</returns>
    private object QueueSelectObject(JsonElement request)
    {
        var arguments = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("arguments", out var value)
            ? value : request;
        var id = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("id", out var idValue)
            ? idValue.GetString() : null;
        var name = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("name", out var nameValue)
            ? nameValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("选中对象需要提供 id 或 name");

        _pendingActions.Enqueue(() =>
        {
            var target = FindSceneObject(id, name);
            if (target is null)
            {
                SetStatus($"找不到对象：{name ?? id}");
                return;
            }

            UnlockSceneSelectionForHierarchy(target.Id);
            _selectedSceneObjectId = target.Id;
            _selectedVoxel = null;
            _showInspector = true;
            _requestedInspectorTab = 0;
            FocusHierarchyObject(target);
            SetStatus($"已选中对象：{target.Name}");
        });
        return new { queued = true, id, name };
    }

    /// <summary>
    /// 读取当前选中对象的摘要，用于确认选中状态与缩放倍数
    /// </summary>
    /// <returns>选中信息，未选中时 selected 为 false</returns>
    private object DescribeSelection()
    {
        var selected = GetSelectedSceneObject();
        if (selected is null) return new { selected = false, inspectorVisible = _showInspector };

        var model = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        var data = model is null ? VoxelModelComponentData.Default : SceneComponentTypes.ReadVoxelModel(model);
        var transform = selected.Transform;
        return new
        {
            selected = true,
            inspectorVisible = _showInspector,
            id = selected.Id.ToString(),
            name = selected.Name,
            visible = selected.Visible,
            layerIndex = selected.LayerIndex,
            transformMode = transform.Mode.ToString(),
            position = transform.Mode == TransformMode.Grid
                ? new[] { transform.Grid.Position.X, transform.Grid.Position.Y, transform.Grid.Position.Z }
                : new[] { (long)Math.Round(transform.Free.Position.X), (long)Math.Round(transform.Free.Position.Y), (long)Math.Round(transform.Free.Position.Z) },
            hasModel = model is not null,
            modelAsset = data.Model,
            voxelCount = data.VoxelCount,
            scaleX = data.ScaleX,
            scaleY = data.ScaleY,
            scaleZ = data.ScaleZ,
        };
    }

    /// <summary>
    /// 按 id 或名称查找场景对象
    /// </summary>
    /// <param name="id">对象标识文本，可为空</param>
    /// <param name="name">对象名称，可为空</param>
    /// <returns>找到的对象，找不到时为空</returns>
    private SceneObject? FindSceneObject(string? id, string? name)
    {
        if (_sceneDocument is null) return null;
        if (!string.IsNullOrWhiteSpace(id))
            return StableId.TryParse(id, out var parsed)
                ? _sceneDocument.Objects.FirstOrDefault(item => item.Id == parsed)
                : _sceneDocument.Objects.FirstOrDefault(item => string.Equals(item.Id.ToString(), id, StringComparison.OrdinalIgnoreCase));
        return _sceneDocument.Objects.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 将打开项目操作安全地投递到窗口线程
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>投递结果</returns>
    private object RequestInspectorPanelMenu()
    {
        _openInspectorPanelMenuRequested = true;
        _pendingActions.Enqueue(() => { _showInspector = true; _requestedInspectorTab = 0; });
        return new { queued = true };
    }

    private object RequestWindowMenu()
    {
        _openWindowMenuRequested = true;
        return new { queued = true };
    }

    private object QueueOpenProject(JsonElement request)
    {
        var arguments = request.TryGetProperty("arguments", out var value) ? value : request;
        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目路径不能为空");
        var saveBeforeSwitch = arguments.TryGetProperty("saveBeforeSwitch", out var saveValue) &&
            saveValue.ValueKind == JsonValueKind.True;
        var discard = ReadBoolean(arguments, "discard") ?? false;
        _pendingActions.Enqueue(() =>
        {
            if (saveBeforeSwitch && _project is not null)
            {
                if (_sceneDocumentDirty) SaveProject();
                if (_generatorDirty) SaveGeneratorSource();
                if (_sceneDocumentDirty || _generatorDirty)
                {
                    SetStatus("保存未完成，已取消项目切换");
                    return;
                }
            }
            //discard 表示明确丢弃未保存修改，跳过确认框
            var previous = _allowProjectReplacement;
            _allowProjectReplacement = discard;
            try { BeginOpenProject(path, fromWelcome: false); }
            finally { _allowProjectReplacement = previous; }
        });
        return new { queued = true, path, discard };
    }

    /// <summary>
    /// 执行当前项目插件注册的自动化入口
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>自动化结果</returns>
    private object InvokeAutomation(JsonElement request)
    {
        if (_pluginRuntime is null) throw new InvalidOperationException("当前没有激活的插件运行时");
        var arguments = request.TryGetProperty("arguments", out var value) ? value : default;
        var id = arguments.GetProperty("automationId").GetString();
        using var emptyPayload = JsonDocument.Parse("{}");
        var payload = arguments.TryGetProperty("payload", out var item) ? item : emptyPayload.RootElement;
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("自动化入口 ID 不能为空");
        return _pluginRuntime.Invoke(id, payload, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 执行当前项目插件注册的编辑器命令
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>命令结果</returns>
    private object InvokeCommand(JsonElement request)
    {
        if (_pluginRuntime is null) throw new InvalidOperationException("当前没有激活的插件运行时");
        var arguments = request.TryGetProperty("arguments", out var value) ? value : default;
        var id = arguments.GetProperty("commandId").GetString();
        using var emptyPayload = JsonDocument.Parse("{}");
        var payload = arguments.TryGetProperty("payload", out var item) ? item : emptyPayload.RootElement;
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("命令 ID 不能为空");
        return _pluginRuntime.InvokeCommand(id, payload, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 将插件重载操作投递到窗口线程
    /// </summary>
    /// <returns>投递结果</returns>
    private object QueueReloadPlugins()
    {
        _pendingActions.Enqueue(ReloadPlugins);
        return new { queued = true };
    }

    /// <summary>
    /// 将项目保存操作投递到编辑器窗口线程
    /// </summary>
    private object QueueSaveProject()
    {
        if (_project is null) throw new InvalidOperationException("当前没有打开的项目");
        _pendingActions.Enqueue(() => SaveProject());
        return new { queued = true };
    }

    /// <summary>
    /// 关闭当前项目：未保存且未明确处理时弹出界面确认框，等待 editor.answerDialog 回答
    /// </summary>
    private object QueueCloseProject(JsonElement request)
    {
        if (_project is null) return new { queued = false, alreadyClosed = true, sceneDirty = false };
        var arguments = Unwrap(request);
        var discard = ReadBoolean(arguments, "discard") ?? false;
        var save = ReadBoolean(arguments, "save") ?? false;
        if (save && discard) throw new ArgumentException("save 与 discard 不能同时为真");
        var timelinePath = ReadString(arguments, "timelinePath");
        var sceneDirty = _sceneDocumentDirty;
        var generatorDirty = _generatorDirty;
        var timelineDirty = TimelineIsDirty;
        if (!sceneDirty && !generatorDirty && !timelineDirty)
        {
            DismissProjectCloseDialog();
            _pendingActions.Enqueue(CloseProject);
            return new { queued = true, alreadyClosed = false, sceneDirty, requiresSave = false };
        }
        if (discard)
        {
            if (timelineDirty) ResetTimelineWeather();
            DismissProjectCloseDialog();
            _pendingActions.Enqueue(CloseProject);
            return new { queued = true, alreadyClosed = false, sceneDirty, requiresSave = false, discarded = true };
        }
        if (save)
        {
            if (timelineDirty)
            {
                if (string.IsNullOrWhiteSpace(timelinePath) && string.IsNullOrWhiteSpace(_timelineAssetPath))
                    return new
                    {
                        queued = false, alreadyClosed = false, sceneDirty, requiresSave = true,
                        message = "当前是临时时间轴，请传 timelinePath 指定保存位置",
                        pendingDialog = DescribePendingDialog(),
                    };
                if (!TryConfirmTimelineSave(path: timelinePath, overwrite: ReadBoolean(arguments, "overwrite") ?? false))
                    return new { queued = false, alreadyClosed = false, sceneDirty, requiresSave = true, message = "时间轴保存未完成" };
            }
            SaveProject();
            if (_generatorDirty) SaveGeneratorSource();
            if (_sceneDocumentDirty || _generatorDirty)
                return new { queued = false, alreadyClosed = false, sceneDirty, requiresSave = true, message = "项目保存未完成" };
            DismissProjectCloseDialog();
            _pendingActions.Enqueue(CloseProject);
            return new { queued = true, alreadyClosed = false, sceneDirty, requiresSave = false, saved = true };
        }
        //未指定处理方式：按界面流程弹出确认框，供 MCP 读取后回答
        RequestCloseProject();
        return new { queued = false, alreadyClosed = false, sceneDirty, requiresSave = true, pendingDialog = DescribePendingDialog() };
    }

    /// <summary>
    /// 由编辑器进程安装插件并请求重载
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>安装结果</returns>
    private object InstallPlugin(JsonElement request)
    {
        var arguments = request.TryGetProperty("arguments", out var value) ? value : request;
        var source = arguments.GetProperty("sourceDirectory").GetString();
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("插件目录不能为空");
        var package = PluginManager.InstallFromDirectory(source);
        _pendingActions.Enqueue(ReloadPlugins);
        return new { package.Id, package.Name, package.Version, queued = true };
    }

    /// <summary>
    /// 由编辑器进程修改当前项目插件启停状态并请求重载
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>启停结果</returns>
    private object SetPluginEnabled(JsonElement request)
    {
        if (_project is null) throw new InvalidOperationException("当前没有打开项目");
        var arguments = request.TryGetProperty("arguments", out var value) ? value : request;
        var pluginId = arguments.GetProperty("pluginId").GetString();
        var enabled = arguments.GetProperty("enabled").GetBoolean();
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("插件 ID 不能为空");
        if (enabled) PluginManager.Enable(_project, pluginId);
        else PluginManager.Disable(_project, pluginId);
        _pendingActions.Enqueue(ReloadPlugins);
        return new { pluginId, enabled, queued = true };
    }

    /// <summary>
    /// 根据当前项目清单重建插件运行时
    /// </summary>
    private void ReloadPlugins()
    {
        if (_project is null) return;
        _pluginCommandResults.Clear();
        _pluginCommandInputs.Clear();
        _pluginRuntime?.Dispose();
        _pluginRuntime = new PluginScriptRuntime();
        _pluginRuntime.Activate(_project, TimeSpan.FromSeconds(2));
        _statusText = "插件已重新加载";
    }

    private void InstallPluginPackageFromMenu()
    {
        var path = NativeDialogs.PickFile("选择插件分发包", "MVoxel 插件包\0*.mvplugin\0全部文件\0*.*\0");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var package = PluginManager.InstallFromPackage(path);
            if (_project is not null) ReloadPlugins();
            SetStatus($"已安装插件：{package.Name}（{package.Version}）");
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("安装插件包失败", exception);
            SetStatus($"安装插件包失败：{exception.Message}");
        }
    }

    private void PackPluginFromMenu()
    {
        var source = NativeDialogs.PickFolder("选择包含 package.json 的插件目录");
        if (string.IsNullOrWhiteSpace(source)) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var target = NativeDialogs.SaveFile("保存插件分发包", "MVoxel 插件包\0*.mvplugin\0", "mvplugin", name + ".mvplugin");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            PluginManager.PackPlugin(source, target);
            SetStatus($"已创建插件包：{target}");
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("打包插件失败", exception);
            SetStatus($"打包插件失败：{exception.Message}");
        }
    }

    private void SetProjectPluginFromMenu(ProjectHandle project, string pluginId, bool enabled)
    {
        if (!ReferenceEquals(_project, project)) return;
        try
        {
            if (enabled) PluginManager.Enable(project, pluginId);
            else PluginManager.Disable(project, pluginId);
            ReloadPlugins();
            SetStatus($"插件已{(enabled ? "启用" : "禁用")}：{pluginId}");
        }
        catch (Exception exception)
        {
            MVoxelLog.Warn($"修改插件状态失败：{pluginId}，原因：{exception.Message}");
            SetStatus($"修改插件状态失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 在窗口线程执行插件命令
    /// </summary>
    /// <param name="commandId">命令 ID</param>
    private void ExecutePluginCommand(string commandId, string argumentsJson = "{}")
    {
        if (_pluginRuntime is null) return;
        var owner = _pluginRuntime.Commands.FirstOrDefault(command => command.Id == commandId)?.PluginId;
        try
        {
            using var payload = JsonDocument.Parse(argumentsJson);
            var result = _pluginRuntime.InvokeCommand(commandId, payload.RootElement, TimeSpan.FromSeconds(10)).GetRawText();
            if (owner is not null)
                _pluginCommandResults[owner] = $"{commandId}\n{(result.Length > 4096 ? result[..4096] + "…" : result)}";
            _statusText = $"已执行插件命令：{commandId}";
        }
        catch (Exception exception)
        {
            MVoxelLog.Error($"插件命令执行失败：{commandId}", exception);
            if (owner is not null) _pluginCommandResults[owner] = $"{commandId}\n执行失败：{exception.Message}";
            _statusText = $"插件命令执行失败：{commandId}";
        }
    }

    private void RequestWelcomeWindow() => _showWelcomeRequested = true;

    private void EnsureWelcomeWindow()
    {
        if (!_showWelcomeRequested) return;
        if (_welcomeWindow?.IsOpen == true)
        {
            _showWelcomeRequested = false;
            return;
        }
        _welcomeWindow?.Dispose();
        _showWelcomeRequested = false;
        _welcomeWindow = new ImGuiWelcomeContent(this);
        _welcomeWindow.Initialize(_imguiRenderer!, _fonts);
    }

    private static NativeWindowSettings CreateWindowSettings(string[] args)
    {
        AppBranding.ConfigureWindowsApplicationId();
        return new NativeWindowSettings
        {
            Title = "MVoxel 体素建模",
            ClientSize = new Vector2i(1440, 900),
            MinimumClientSize = new Vector2i(1000, 680),
            StartVisible = false,
            StartFocused = true,
            API = ContextAPI.NoAPI,
            Vsync = VSyncMode.On,
            Icon = AppBranding.LoadWindowIcon(),
        };
    }

    private void ConfigureImGui()
    {
        ImGui.StyleColorsDark();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigDockingWithShift = false;
        io.ConfigViewportsNoTaskBarIcon = false;
        AppPaths.EnsureDirectories();
        var layoutPath = AppPaths.ConfigPath("imgui-layout.ini");
        var layoutVersionPath = AppPaths.ConfigPath("imgui-layout.version");
        const string layoutVersion = "9";
        LoadLayoutSelection();
        _imguiLayoutPath = layoutPath;
        var savedLayout = File.Exists(layoutPath) ? File.ReadAllText(layoutPath) : string.Empty;
        var hasCurrentLayoutVersion = File.Exists(layoutVersionPath) &&
                                      string.Equals(File.ReadAllText(layoutVersionPath).Trim(), layoutVersion,
                                          StringComparison.Ordinal);
        var hasCurrentPanelLayout = (hasCurrentLayoutVersion || _activeLayoutFile is not null) &&
                                     savedLayout.Contains("DockSpace ID=0x6A3C3001", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][场景##场景窗口]", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][渲染##渲染窗口]", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][检查器##检查器窗口]", StringComparison.Ordinal) &&
                                     !savedLayout.Contains("[Window][场景与渲染##中央面板]", StringComparison.Ordinal);
        if (hasCurrentPanelLayout)
        {
            ImGui.LoadIniSettingsFromMemory(MigrateLayoutWindowNames(savedLayout));
            if (!hasCurrentLayoutVersion) AtomicFile.WriteText(layoutVersionPath, layoutVersion);
        }
        else
        {
            ImGui.LoadIniSettingsFromMemory(GetPresetDockLayout(_layoutPreset));
            _requestedInspectorTab = 0;
            AtomicFile.WriteText(layoutVersionPath, layoutVersion);
        }

        ApplyImGuiStyle();
    }

    /// <summary>
    /// 应用当前主题的 ImGui 间距、圆角、边框和控件颜色
    /// </summary>
    private void ApplyImGuiStyle()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 4F;
        style.ChildRounding = 3F;
        style.FrameRounding = 3F;
        style.PopupRounding = 8F;
        style.ScrollbarRounding = 2F;
        style.GrabRounding = 2F;
        style.WindowBorderSize = 1F;
        style.ChildBorderSize = 1F;
        style.PopupBorderSize = 1F;
        style.FrameBorderSize = 1F;
        style.DockingSeparatorSize = 2F;
        style.ItemSpacing = new Vector2(7F, 5F);
        style.WindowPadding = new Vector2(7F, 7F);
        style.Colors[(int)ImGuiCol.WindowBg] = ToColor(EditorPalette.Window);
        style.Colors[(int)ImGuiCol.ChildBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.PopupBg] = ToColor(EditorPalette.Panel);
        style.Colors[(int)ImGuiCol.FrameBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = ToColor(EditorPalette.Hover);
        style.Colors[(int)ImGuiCol.FrameBgActive] = ToColor(EditorPalette.Pressed);
        style.Colors[(int)ImGuiCol.TitleBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TitleBgActive] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.MenuBarBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.Header] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.HeaderHovered] = ToColor(EditorPalette.SelectedHover);
        style.Colors[(int)ImGuiCol.Button] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.ButtonHovered] = ToColor(EditorPalette.Hover);
        style.Colors[(int)ImGuiCol.ButtonActive] = ToColor(EditorPalette.Pressed);
        style.Colors[(int)ImGuiCol.Border] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.Separator] = ToColor(EditorPalette.Border);
        // 表格头和隔行底色跟随当前主题，不能沿用 ImGui 默认的深色表头，
        // 否则浅色主题下会出现黑底黑字
        style.Colors[(int)ImGuiCol.TableHeaderBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TableRowBg] = new Vector4(0F, 0F, 0F, 0F);
        var tableRowAlt = ToColor(EditorPalette.Panel);
        tableRowAlt.W = 0.45F;
        style.Colors[(int)ImGuiCol.TableRowBgAlt] = tableRowAlt;
        style.Colors[(int)ImGuiCol.TableBorderStrong] = ToColor(EditorPalette.Border);
        var tableBorderLight = ToColor(EditorPalette.Border);
        tableBorderLight.W = 0.6F;
        style.Colors[(int)ImGuiCol.TableBorderLight] = tableBorderLight;
        style.Colors[(int)ImGuiCol.SeparatorHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Tab] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TabHovered] = ToColor(EditorPalette.SelectedHover);
        style.Colors[(int)ImGuiCol.TabSelected] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.TabDimmed] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TabDimmedSelected] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.TabDimmedSelectedOverline] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.TabSelectedOverline] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.HeaderActive] = ToColor(EditorPalette.Pressed);
        style.Colors[(int)ImGuiCol.CheckMark] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.SliderGrab] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.SliderGrabActive] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.TextSelectedBg] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.ScrollbarBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.ScrollbarGrab] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.ScrollbarGrabActive] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Text] = ToColor(EditorPalette.Text);
        style.Colors[(int)ImGuiCol.TextDisabled] = ToColor(EditorPalette.MutedText);
    }

    private void DrawEditorFrame()
    {
        if (_resetDockLayoutRequested)
        {
            ImGui.LoadIniSettingsFromMemory(GetPresetDockLayout(_layoutPreset));
            _resetDockLayoutRequested = false;
            _applyPreset = true;
            _focusModelingTools = _layoutPreset == LayoutPreset.Modeling;
            _requestedCenterTab = _layoutPreset == LayoutPreset.Rendering ? 1 : 0;
            _requestedInspectorTab = _layoutPreset == LayoutPreset.Modeling ? null : _layoutPreset == LayoutPreset.Rendering ? 1 : 0;
        }
        UiDiagnostics.PrepareTabs(ref _requestedCenterTab, ref _requestedInspectorTab);
        var welcomeIsOpen = _welcomeWindow?.IsOpen == true;
        var operation = _projectOperation.Snapshot;
        var inputBlocked = welcomeIsOpen || operation is not null || _historyConfirmation is not null;
        if (!inputBlocked && !_screenColorSampling) HandleVoxelShortcuts();
        inputBlocked |= _historyConfirmation is not null;
        BeginEditorHistory();
        using var fileHistoryScope = _historyFiles.Activate();
        if (inputBlocked) ImGui.BeginDisabled();
        var mark = PerformanceMark();
        DrawMenuBar();
        DrawDockspace();
        MeasureSince(mark, ref _analysisLive.MenuDockMs);

        mark = PerformanceMark();
        DrawHierarchy();
        MeasureSince(mark, ref _analysisLive.HierarchyMs);

        mark = PerformanceMark();
        DrawCenter();
        MeasureSince(mark, ref _analysisLive.CenterMs);

        mark = PerformanceMark();
        DrawInspector();
        DrawExtraInspectors();
        DrawRenderConfig();
        MeasureSince(mark, ref _analysisLive.InspectorMs);

        mark = PerformanceMark();
        DrawModelingTools();
        DrawFloodConfirmModal();
        DrawVoxExportConfirmation();
        DrawProjectAssets();
        DrawPluginPanels();
        DrawGeneratorWindow();
        DrawAnalysisWindows();
        DrawEnvironmentWindow();
        DrawProbeVolumeWindow();
        DrawAnimationWindow();
        DrawTimelineWindow();
        DrawStatusBar();
        DrawMinecraftImportWindow();
        DrawMeshImportWindow();
        MeasureSince(mark, ref _analysisLive.OtherPanelsMs);

        mark = PerformanceMark();
        if (inputBlocked) ImGui.EndDisabled();
        DrawProjectPathDialog();
        DrawLayoutNameDialog();
        DrawCloseProjectDialog();
        DrawHistoryConfirmationDialog();
        DrawExitConfirmationDialog();
        if (!inputBlocked) DrawSettingsWindow();
        if (!inputBlocked) DrawGettingStarted();
        if (!inputBlocked) DrawScreenColorSampler();
        if (welcomeIsOpen) _welcomeWindow!.Draw();
        else if (operation is { } snapshot)
        {
            BlockingOperationDialog.Draw(snapshot, CancelProjectOperation);
        }
        MeasureSince(mark, ref _analysisLive.DialogsMs);
        _applyPreset = false;
        EndEditorHistory();
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        if (ImGui.BeginMenu("项目"))
        {
            if (ImGui.MenuItem("新建项目", "Ctrl+N")) ShowProjectPathDialog(create: true);
            if (ImGui.MenuItem("打开项目", "Ctrl+O")) ShowProjectPathDialog(create: false);
            if (ImGui.MenuItem("保存项目", "Ctrl+S", false, _project is not null)) SaveProject();
            if (ImGui.MenuItem("另存项目为…", null, false, _project is not null)) ShowSaveAsDialog();
            if (ImGui.MenuItem("新建体素模型", null, false, _project is not null)) CreateVoxelModel();
            if (ImGui.BeginMenu("导出", _project is not null))
            {
                if (ImGui.MenuItem("选中模型 → MVoxel 原生 (.mvmodel)")) ExportWorkingVoxels(true, false, native: true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("保留原生颜色、材质槽与标记，不使用 MagicaVoxel 的尺寸或色板上限。材质定义保存在项目内。");
                if (ImGui.MenuItem("整个场景体素 → MVoxel 原生 (.mvworld)")) ExportWorkingVoxels(false, false, native: true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出合并体素数据，包括隐藏模型；保留层级、相机、光照与材质定义请使用另存项目。");
                if (ImGui.BeginMenu("兼容导出"))
                {
                    if (ImGui.MenuItem("选中模型 → MagicaVoxel (.vox)")) ExportWorkingVoxels(true, false);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("仅交换几何和颜色：单模型每轴最多 256 格、255 个非空颜色。不导出 MVoxel 材质参数与层级；超限明确拒绝，不裁剪、不量化。");
                    if (ImGui.MenuItem("整个场景体素 → MagicaVoxel (.vox)")) ExportWorkingVoxels(false, false);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("含隐藏模型的合并几何与颜色；仅用于满足 VOX 单模型限制的小场景。大场景优先原生格式。");
                    if (ImGui.MenuItem("选中模型 → Blender 网格 (.obj)")) ExportWorkingVoxels(true, false, obj: true);
                    if (ImGui.MenuItem("整个场景体素 → Blender 网格 (.obj)")) ExportWorkingVoxels(false, false, obj: true);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出外表面与顶点颜色；不包含材质参数、相机和层级。大型场景可能产生很大的文件。");
                    if (ImGui.MenuItem("选中模型 → Qubicle (.qb)")) ExportWorkingVoxels(true, true);
                    if (ImGui.MenuItem("整个场景体素 → Qubicle (.qb)")) ExportWorkingVoxels(false, true);
                    ImGui.EndMenu();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("当前渲染相机 → PNG")) { _requestedCenterTab = 1; ExportRenderPng(); }
                if (ImGui.MenuItem("当前渲染相机 → HDR")) { _requestedCenterTab = 1; ExportRenderHdr(); }
                if (ImGui.MenuItem("全部相机 → PNG")) { _requestedCenterTab = 1; ExportAllCamerasPng(); }
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("关闭项目", enabled: _project is not null)) RequestCloseProject();
            if (ImGui.MenuItem("导入 MVoxel 原生体素数据…", enabled: _project is not null)) ImportVoxAsWorld(native: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("导入 .mvmodel/.mvworld，保留原生颜色、材质槽与标记；若来自其他项目，材质定义需一同放入 Materials。");
            if (ImGui.MenuItem("兼容导入（.vox / .qb / .schem）…", enabled: _project is not null)) ImportVoxAsWorld();
            if (ImGui.MenuItem("导入网格模型（obj/stl/glb/gltf）…", enabled: _project is not null)) ShowMeshImportDialog();
            if (ImGui.MenuItem("导入 Minecraft 存档…", enabled: _project is not null)) ShowMinecraftImportDialog();
            if (ImGui.MenuItem("退出", "Alt+F4")) RequestExit();
            ImGui.EndMenu();
        }

        if (_openWindowMenuRequested) { ImGui.OpenPopup("窗口"); _openWindowMenuRequested = false; }
        if (ImGui.BeginMenu("编辑"))
        {
            var undo = _voxelHistory.PeekUndo();
            var redo = _voxelHistory.PeekRedo();
            var bindings = _editorSettings.ActiveScheme().Bindings;
            if (ImGui.MenuItem("撤销", bindings.GetValueOrDefault("edit.undo"), false, undo is not null || _historyCommands.Count > 0)) ApplyUndo();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(undo is null ? "撤销上一步；按住快捷键可连续撤销。" : $"撤销：{undo.Name}；按住快捷键可连续撤销。");
            if (ImGui.MenuItem("重做", bindings.GetValueOrDefault("edit.redo"), false, redo is not null)) ApplyRedo();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{(redo is null ? "重做上一步" : $"重做：{redo.Name}")}；备用快捷键 {bindings.GetValueOrDefault("edit.redoAlternate")}，支持按住连续重做。");
            ImGui.Separator();
            var selected = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId);
            if (ImGui.MenuItem("聚焦选中对象", enabled: selected is not null) && selected is not null) FocusHierarchyObject(selected);
            if (ImGui.MenuItem(selected?.Visible == false ? "显示选中对象" : "隐藏选中对象", enabled: selected is not null) && selected is not null)
            { selected.Visible = !selected.Visible; MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }
            if (ImGui.MenuItem(selected?.Locked == true ? "解锁选中对象" : "锁定选中对象", enabled: selected is not null) && selected is not null) ToggleSceneObjectLock(selected);
            if (ImGui.BeginMenu("创建子对象", selected is not null))
            {
                DrawHierarchyCreateMenu(selected!);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("取消选择", enabled: selected is not null)) _selectedSceneObjectId = null;
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("窗口"))
        {
            if (ImGui.BeginMenu("常规"))
            {
                if (ImGui.MenuItem("场景", "F1")) _requestedCenterTab = 0;
                if (ImGui.MenuItem("层级")) { _showHierarchy = true; ImGui.SetWindowFocus("层级##层级面板"); }
                if (ImGui.MenuItem("检查器")) { _showInspector = true; _requestedInspectorTab = 0; }
                if (ImGui.MenuItem("调色盘")) { _showModelingTools = true; _focusModelingTools = true; }
                if (ImGui.MenuItem("项目资产")) { _showProjectAssets = true; ImGui.SetWindowFocus("项目资产##资源浏览器"); }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("渲染"))
            {
                if (ImGui.MenuItem("渲染", "F2")) _requestedCenterTab = 1;
                if (ImGui.MenuItem("渲染配置")) { _showRenderConfig = true; _requestedInspectorTab = 1; }
                if (ImGui.MenuItem("环境配置")) OpenEnvironmentWindow();
                if (ImGui.MenuItem("探针体积")) _showProbeVolumeWindow = true;
                if (ImGui.MenuItem("动画")) _showAnimationWindow = true;
                if (ImGui.MenuItem("时间轴")) _showTimelineWindow = true;
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("工具"))
            {
                if (ImGui.MenuItem("生成器")) { _showGeneratorPanel = true; ImGui.SetWindowFocus("生成器##生成器面板"); }
                if (ImGui.MenuItem("体素度量")) { _showVoxelMeasurements = true; ImGui.SetWindowFocus("体素度量"); }
                if (ImGui.MenuItem("性能分析器")) { _showPerformanceAnalyzer = true; ImGui.SetWindowFocus("性能分析器"); }
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("插件"))
        {
            if (ImGui.MenuItem("安装插件包…")) _pendingActions.Enqueue(InstallPluginPackageFromMenu);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("选择 .mvplugin 文件安装到编辑器；项目启用状态保持不变。");
            if (ImGui.MenuItem("打包插件目录…")) _pendingActions.Enqueue(PackPluginFromMenu);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("将包含 package.json 的目录打包为 .mvplugin 文件。");
            var diagnostics = _project is null ? Array.Empty<ProjectPluginDiagnostic>() : PluginManager.Diagnose(_project);
            if (_project is { } pluginProject && ImGui.BeginMenu("管理项目插件"))
            {
                var enabled = PluginManager.ListEnabled(pluginProject).ToHashSet(StringComparer.Ordinal);
                var installed = PluginManager.ListInstalled();
                foreach (var package in installed)
                {
                    var isEnabled = enabled.Contains(package.Id);
                    var displayName = string.IsNullOrWhiteSpace(package.Name) ? package.Id : package.Name;
                    if (ImGui.MenuItem($"{displayName}##plugin:{package.Id}", null, isEnabled))
                        _pendingActions.Enqueue(() => SetProjectPluginFromMenu(pluginProject, package.Id, !isEnabled));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted($"{package.Id} · {package.Version}");
                        ImGui.EndTooltip();
                    }
                }
                foreach (var missing in diagnostics.Where(item => !item.Installed))
                {
                    if (ImGui.MenuItem($"禁用缺失插件：{missing.PluginId}"))
                        _pendingActions.Enqueue(() => SetProjectPluginFromMenu(pluginProject, missing.PluginId, false));
                }
                ImGui.EndMenu();
            }
            if (_pluginRuntime is not null && ImGui.BeginMenu("启动状态"))
            {
                if (_pluginRuntime.ActivationStatus.Count == 0) ImGui.TextDisabled("没有已启用的插件");
                foreach (var (id, status) in _pluginRuntime.ActivationStatus.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    ImGui.TextUnformatted(id);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(status);
                        ImGui.EndTooltip();
                    }
                }
                ImGui.EndMenu();
            }
            var missingCount = diagnostics.Count(item => !item.Installed);
            if (missingCount > 0)
            {
                ImGui.TextColored(new Vector4(1F, .65F, .25F, 1F), $"缺失插件：{missingCount}");
                ImGui.Separator();
            }
            var panels = _pluginRuntime?.Panels ?? [];
            if (panels.Count == 0) ImGui.MenuItem("暂无已启用插件面板", enabled: false);
            foreach (var panel in panels)
            {
                var visible = _visiblePluginPanels.Contains(panel.Id);
                if (ImGui.MenuItem(panel.Name, null, ref visible))
                {
                    if (visible) _visiblePluginPanels.Add(panel.Id);
                    else _visiblePluginPanels.Remove(panel.Id);
                }
            }
            var commands = _pluginRuntime?.Commands ?? [];
            if (commands.Count > 0) ImGui.Separator();
            foreach (var command in commands)
            {
                if (ImGui.MenuItem(command.Name)) _pendingActions.Enqueue(() => ExecutePluginCommand(command.Id));
            }
            ImGui.EndMenu();
        }

        DrawLayoutMenu();

        if (ImGui.BeginMenu("工具"))
        {
            ImGui.TextDisabled("视口工具");
            if (ImGui.MenuItem("相机", null, _activeTool == EditorTool.Camera)) _activeTool = EditorTool.Camera;
            if (ImGui.MenuItem("选择", null, _activeTool == EditorTool.Pick)) _activeTool = EditorTool.Pick;
            ImGui.Separator();
            ImGui.TextDisabled("体素编辑工具");
            if (ImGui.MenuItem("绘制", null, _activeTool == EditorTool.Draw, _voxelEditTargetId is not null)) _activeTool = EditorTool.Draw;
            if (ImGui.MenuItem("擦除", null, _activeTool == EditorTool.Erase, _voxelEditTargetId is not null)) _activeTool = EditorTool.Erase;
            ImGui.Separator();
            ImGui.TextDisabled("场景对象");
            if (ImGui.MenuItem("添加点光源", enabled: _project is not null)) CreateLightObject(LightShape.Point);
            if (ImGui.MenuItem("添加聚光灯", enabled: _project is not null)) CreateLightObject(LightShape.Spot);
            if (ImGui.MenuItem("添加方向光", enabled: _project is not null)) CreateLightObject(LightShape.Directional);
            if (ImGui.MenuItem("添加面积灯", enabled: _project is not null)) CreateLightObject(LightShape.Area);
            ImGui.Separator();
            ImGui.TextDisabled("度量与诊断");
            if (ImGui.MenuItem("体素度量", null, ref _showVoxelMeasurements)) _measurementRefreshSeconds = 1F;
            if (ImGui.MenuItem("性能分析器", null, ref _showPerformanceAnalyzer))
            {
                _analysisAllocatedBytes = 0;
                _analysisRefreshSeconds = 0;
            }
            if (ImGui.MenuItem("验证当前场景", enabled: _project is not null)) ValidateCurrentScene();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("主题"))
        {
            foreach (var themeName in EditorPalette.ThemeNames)
            {
                if (!ImGui.MenuItem(themeName, null, _editorSettings.Theme == themeName)) continue;
                _editorSettings.Theme = themeName;
                EditorPalette.ApplyTheme(themeName);
                ApplyImGuiStyle();
                SaveEditorSettings();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("主题管理")) OpenSettings(_project is null ? 0 : 1, _project is null ? 4 : 4, "editor.appearance.theme");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("设置"))
        {
            if (ImGui.MenuItem("编辑器设置")) OpenSettings(0);
            if (ImGui.MenuItem("快捷键方案")) OpenSettings(0, 2, "editor.input.shortcuts");
            if (ImGui.MenuItem("项目设置", enabled: _project is not null)) OpenSettings(1);
            ImGui.Separator();
            if (ImGui.MenuItem("快速画面设置"))
            {
                _showRenderConfig = true;
                _requestedInspectorTab = 1;
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("帮助"))
        {
            if (ImGui.MenuItem("教学文档")) OpenGuide(0);
            ImGui.Separator();
            if (ImGui.MenuItem("快捷键方案")) OpenSettings(0, 2, "editor.input.shortcuts");
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    private void DrawDockspace()
    {
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(0F, WorkspaceTop), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(display.X, Math.Max(1F, display.Y - WorkspaceTop - StatusHeight)), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus |
                    ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        ImGui.Begin("##MVoxelDockspaceHost", flags);
        ImGui.DockSpace(MainDockspaceId, Vector2.Zero,
            ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }

    private const string DefaultDockLayout = """
[Docking][Data]
DockSpace ID=0x6A3C3001 Pos=0,24 Size=1440,840 Split=Y Selected=0x6A3C3002
  DockNode ID=0x6A3C3002 Parent=0x6A3C3001 SizeRef=1440,615 Split=X Selected=0x6A3C3003
    DockNode ID=0x6A3C3003 Parent=0x6A3C3002 SizeRef=300,615
    DockNode ID=0x6A3C3004 Parent=0x6A3C3002 SizeRef=1140,615 Split=X Selected=0x6A3C3005
      DockNode ID=0x6A3C3005 Parent=0x6A3C3004 SizeRef=780,615 Selected=0xD7AC1002
      DockNode ID=0x6A3C3006 Parent=0x6A3C3004 SizeRef=360,615
  DockNode ID=0x6A3C3007 Parent=0x6A3C3001 SizeRef=1440,225 Selected=0xD7AC1004
[Window][层级##层级面板]
DockId=0x6A3C3003,0
[Window][场景##场景窗口]
DockId=0x6A3C3005,0
[Window][渲染##渲染窗口]
DockId=0x6A3C3005,1
[Window][检查器##检查器窗口]
DockId=0x6A3C3006,0
[Window][调色盘##建模工具窗口]
DockId=0x6A3C3006,1
[Window][渲染配置##渲染配置窗口]
DockId=0x6A3C3006,2
[Window][项目资产##资源浏览器]
DockId=0x6A3C3007,0
""";

    private void DrawHierarchy()
    {
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(0F, WorkspaceTop + 1F), new Vector2(layout.LeftWidth, layout.MainHeight));
        if (!_showHierarchy)
        {
            return;
        }

        if (!ImGui.Begin("层级##层级面板", PanelWindowFlags))
        {
            ImGui.End();
            return;
        }

        DrawPanelWindowMenu();
        DrawHierarchyContent();
        ImGui.End();
    }

    private void DrawHierarchyContent()
    {
        DrawVoxelAssetStageBar();
        ImGui.TextColored(ToColor(EditorPalette.Text), "层级");
        ImGui.SameLine();
        ImGui.TextDisabled(_project is null ? "未打开项目" : ActiveEditingName);
        ImGui.SetNextItemWidth(-1F);
        ImGui.InputTextWithHint("##层级搜索", "搜索对象", ref _hierarchySearch, 128);
        ImGui.Separator();
        if (_project is null)
        {
            ImGui.TextWrapped("打开或创建项目后，场景层级会显示在这里。");
        }
        else
        {
            var objects = _sceneDocument?.Objects ?? new List<SceneObject>();
            if (_revealHierarchySelection is not null) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (ImGui.TreeNodeEx($"{(_sceneDocumentDirty ? "*" : "")}{ActiveEditingName}###ActiveScene", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (objects.Count == 0)
                {
                    if (ImGui.Button("创建第一个体素模型", new Vector2(-1F, ImGui.GetFrameHeight()))) CreateVoxelModel();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("也可以选择绘制工具，直接在网格上落笔，自动创建模型。");
                }
                else
                {
                    var roots = objects.Where(item => item.ParentId is null && MatchesHierarchySearch(item, objects)).ToArray();
                    foreach (var sceneObject in roots)
                    {
                        DrawSceneObjectNode(sceneObject, objects);
                    }
                }

                ImGui.TreePop();
            }
        }

        if (_project is not null && ImGui.IsWindowHovered() &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGui.IsAnyItemHovered())
            ImGui.OpenPopup("##HierarchyBlankMenu");
        if (ImGui.BeginPopup("##HierarchyBlankMenu"))
        {
            DrawHierarchyCreateMenu(null);
            ImGui.EndPopup();
        }
    }

    private bool MatchesHierarchySearch(SceneObject sceneObject, IReadOnlyList<SceneObject> objects)
    {
        var searchMatched = string.IsNullOrWhiteSpace(_hierarchySearch) ||
            sceneObject.Name.Contains(_hierarchySearch.Trim(), StringComparison.CurrentCultureIgnoreCase);
        if (searchMatched)
            return true;
        return objects.Where(item => item.ParentId == sceneObject.Id)
            .Any(child => MatchesHierarchySearch(child, objects));
    }

    /// <summary>
    /// 绘制层级中的对象节点及其子对象
    /// </summary>
    /// <param name="sceneObject">当前对象</param>
    /// <param name="objects">场景对象列表</param>
    private void DrawSceneObjectNode(SceneObject sceneObject, IReadOnlyList<SceneObject> objects)
    {
        var children = objects.Where(item => item.ParentId == sceneObject.Id).ToArray();
        var nodeFlags = children.Length == 0
            ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen
            : ImGuiTreeNodeFlags.None;
        if (_selectedSceneObjectId == sceneObject.Id)
        {
            nodeFlags |= ImGuiTreeNodeFlags.Selected;
        }

        if (_revealHierarchySelection is { } revealId && sceneObject.Id != revealId)
        {
            var current = objects.FirstOrDefault(item => item.Id == revealId);
            var visited = new HashSet<StableId>();
            while (current?.ParentId is { } parentId && visited.Add(parentId))
            {
                if (parentId == sceneObject.Id) { ImGui.SetNextItemOpen(true, ImGuiCond.Always); break; }
                current = objects.FirstOrDefault(item => item.Id == parentId);
            }
        }
        var nodeId = $"{sceneObject.Name}##SceneObject_{sceneObject.Id}";
        var transparentHeader = Color.FromArgb(0, EditorPalette.PanelHeader);
        ImGui.PushStyleColor(ImGuiCol.Header, ToColor(transparentHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ToColor(transparentHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ToColor(transparentHeader));
        var hidden = !IsSceneObjectVisible(sceneObject) || !_sceneDocument!.IsLayerVisible(sceneObject.LayerIndex);
        if (hidden) ImGui.PushStyleColor(ImGuiCol.Text, ToColor(EditorPalette.MutedText));
        var expanded = ImGui.TreeNodeEx(nodeId, nodeFlags);
        if (hidden) ImGui.PopStyleColor();
        ImGui.PopStyleColor(3);
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        if (_revealHierarchySelection == sceneObject.Id)
        {
            ImGui.SetScrollHereY(0.5F);
            _revealHierarchySelection = null;
        }
        var rowClicked = ImGui.IsItemClicked();
        var treeCursor = ImGui.GetCursorScreenPos();
        const float iconWidth = HierarchyRowIconSize;
        var iconGap = 2F;
        var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X -
                        (ImGui.GetScrollMaxY() > 0F ? ImGui.GetStyle().ScrollbarSize : 0F) - ImGui.GetScrollX();
        var minimumIconStart = rowMin.X + ImGui.GetTreeNodeToLabelSpacing() + ImGui.CalcTextSize(sceneObject.Name).X + ImGui.GetStyle().ItemSpacing.X;
        var iconsStart = Math.Max(minimumIconStart, rightEdge - iconWidth * 2F - iconGap);
        var rowWidth = Math.Max(1F, iconsStart + iconWidth * 2F + iconGap - rowMin.X);
        ImGui.SetCursorScreenPos(rowMin);
        var rowSelectableClicked = ImGui.InvisibleButton($"##SceneObjectRow_{sceneObject.Id}",
            new Vector2(Math.Max(1F, rowWidth - 48F), rowMax.Y - rowMin.Y));
        var rowSelectableHovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(treeCursor);

        var fullRowMax = rowMin + new Vector2(rowWidth, rowMax.Y - rowMin.Y);
        var rowHovered = rowSelectableHovered || ImGui.IsMouseHoveringRect(rowMin, fullRowMax);
        if (rowHovered || _selectedSceneObjectId == sceneObject.Id)
        {
            var drawList = ImGui.GetWindowDrawList();
            var rowColor = _selectedSceneObjectId == sceneObject.Id
                ? Color.FromArgb(150, EditorPalette.Accent)
                : Color.FromArgb(42, EditorPalette.AccentSoft);
            drawList.AddRectFilled(rowMin, fullRowMax,
                ToPackedColor(rowColor), 2F);
        }

        var rowDoubleClicked = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && rowHovered &&
                               ImGui.GetIO().MousePos.X < iconsStart;
        ImGui.SetCursorScreenPos(new Vector2(iconsStart, rowMin.Y + 1F));
        var visibilityClicked = DrawHierarchyIconButton($"##Visibility_{sceneObject.Id}", "显示对象", "隐藏对象",
            sceneObject.Visible, DrawVisibilityIcon, iconWidth);
        if (visibilityClicked)
        {
            sceneObject.Visible = !sceneObject.Visible;
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }

        ImGui.SameLine(0F, iconGap);
        var modelObject = sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel);
        var selectableClicked = DrawHierarchyIconButton($"##Selectable_{sceneObject.Id}",
            "锁定场景选择", "解锁场景选择",
            !sceneObject.Locked, DrawLockIcon, iconWidth);
        if (selectableClicked)
        {
            ToggleSceneObjectLock(sceneObject);
        }

        if ((rowClicked || rowSelectableClicked) && !visibilityClicked && !selectableClicked &&
            true)
        {
            UnlockSceneSelectionForHierarchy(sceneObject.Id);
            _selectedSceneObjectId = sceneObject.Id;
            _selectedVoxel = null;
            if (_voxelEditTargetId != sceneObject.Id) { _activeTool = EditorTool.Pick; _voxelEditTargetId = null; }
        }

        // 不依赖 TreeNodeEx/InvisibleButton 哪一个拿到本次点击：双击判定只看
        // 当前行的实际悬停区域，避免树节点展开状态或 ImGui last-item 状态吞掉聚焦。
        if (rowDoubleClicked && !visibilityClicked && !selectableClicked &&
            true)
        {
            UnlockSceneSelectionForHierarchy(sceneObject.Id);
            _selectedSceneObjectId = sceneObject.Id;
            _selectedVoxel = null;
            if (_voxelEditTargetId != sceneObject.Id) { _activeTool = EditorTool.Pick; _voxelEditTargetId = null; }
            FocusHierarchyObject(sceneObject);
        }

        if (rowHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            UnlockSceneSelectionForHierarchy(sceneObject.Id);
            _selectedSceneObjectId = sceneObject.Id;
            ImGui.OpenPopup($"##SceneObjectRowMenu_{sceneObject.Id}");
        }
        if (ImGui.BeginPopup($"##SceneObjectRowMenu_{sceneObject.Id}"))
        {
            _selectedSceneObjectId = sceneObject.Id;
            if (_voxelEditTargetId != sceneObject.Id) { _activeTool = EditorTool.Pick; _voxelEditTargetId = null; }
            DrawSceneObjectContextMenu(sceneObject);
            ImGui.EndPopup();
        }

        if (children.Length > 0 && expanded)
        {
            foreach (var child in children)
            {
                if (MatchesHierarchySearch(child, objects)) DrawSceneObjectNode(child, objects);
            }

            ImGui.TreePop();
        }
    }

    /// <summary>
    /// 绘制层级对象的状态图标按钮
    /// </summary>
    /// <param name="id">按钮 ID</param>
    /// <param name="enabledTip">启用状态提示</param>
    /// <param name="disabledTip">禁用状态提示</param>
    /// <param name="active">当前是否启用</param>
    /// <param name="drawIcon">图标绘制回调</param>
    /// <returns>按钮是否被点击</returns>
    private static bool DrawHierarchyIconButton(string id, string enabledTip, string disabledTip, bool active,
        Action<ImDrawListPtr, Vector2, Vector2, uint, bool> drawIcon, float size = 20F)
    {
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(min, max, ToPackedColor(EditorPalette.PanelHeader), 3F);
            ImGui.SetTooltip(active ? enabledTip : disabledTip);
        }

        drawIcon(drawList, min, max, ToPackedColor(active ? EditorPalette.Text : EditorPalette.MutedText), active);
        return clicked;
    }

    /// <summary>
    /// 绘制显示状态眼睛图标
    /// </summary>
    /// <param name="drawList">目标绘制列表</param>
    /// <param name="min">图标左上角</param>
    /// <param name="max">图标右下角</param>
    /// <param name="color">图标颜色</param>
    /// <param name="active">当前是否显示</param>
    private static void DrawVisibilityIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint color, bool active)
    {
        var center = (min + max) * 0.5F;
        var halfWidth = Math.Max(4F, (max.X - min.X) * 0.32F);
        var halfHeight = Math.Max(3F, (max.Y - min.Y) * 0.22F);
        drawList.AddBezierCubic(center + new Vector2(-halfWidth, 0F),
            center + new Vector2(-halfWidth * 0.35F, -halfHeight),
            center + new Vector2(halfWidth * 0.35F, -halfHeight),
            center + new Vector2(halfWidth, 0F), color, 1.4F);
        drawList.AddBezierCubic(center + new Vector2(halfWidth, 0F),
            center + new Vector2(halfWidth * 0.35F, halfHeight),
            center + new Vector2(-halfWidth * 0.35F, halfHeight),
            center + new Vector2(-halfWidth, 0F), color, 1.4F);
        drawList.AddCircleFilled(center, 2.2F, color, 12);
        if (!active)
        {
            drawList.AddLine(min + new Vector2(3F, 3F), max - new Vector2(3F, 3F), color, 1.8F);
        }
    }

    /// <summary>
    /// 绘制对象锁定状态图标。active 表示对象当前允许选中，因此绘制开锁；
    /// inactive 表示对象已锁定，因此绘制闭锁。图标只表达锁定语义，不再使用准星。
    /// </summary>
    /// <param name="drawList">目标绘制列表</param>
    /// <param name="min">图标左上角</param>
    /// <param name="max">图标右下角</param>
    /// <param name="color">图标颜色</param>
    /// <param name="active">当前是否允许选中</param>
    private static void DrawLockIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint color, bool active)
    {
        var center = (min + max) * 0.5F - new Vector2(0F, 2F);
        var size = Math.Min(max.X - min.X, max.Y - min.Y);
        var bodyWidth = Math.Max(8F, size * .56F);
        var bodyHeight = Math.Max(6F, size * .38F);
        var bodyMin = new Vector2(center.X - bodyWidth * .5F, center.Y + size * .08F);
        var bodyMax = new Vector2(bodyMin.X + bodyWidth, bodyMin.Y + bodyHeight);
        drawList.AddRect(bodyMin, bodyMax, color, 2F, ImDrawFlags.None, 1.6F);

        var shackleLeft = bodyMin.X + bodyWidth * .2F;
        var shackleRight = bodyMin.X + bodyWidth * .8F;
        var shackleTop = bodyMin.Y - size * .32F;
        var shackleBottom = bodyMin.Y + 1F;
        if (active)
        {
            // 开锁状态：右侧锁环打开并向右偏移，仍保持“锁”的形状。
            var openRight = shackleRight + size * .18F;
            drawList.AddBezierCubic(new Vector2(shackleLeft, shackleBottom),
                new Vector2(shackleLeft, shackleTop), new Vector2(shackleRight, shackleTop),
                new Vector2(openRight, shackleTop + size * .12F), color, 1.6F);
            drawList.AddLine(new Vector2(openRight, shackleTop + size * .12F),
                new Vector2(openRight, bodyMin.Y - size * .02F), color, 1.6F);
        }
        else
        {
            drawList.AddBezierCubic(new Vector2(shackleLeft, shackleBottom),
                new Vector2(shackleLeft, shackleTop), new Vector2(shackleRight, shackleTop),
                new Vector2(shackleRight, shackleBottom), color, 1.6F);
        }

        var keyhole = new Vector2(center.X, bodyMin.Y + bodyHeight * .43F);
        drawList.AddCircleFilled(keyhole, Math.Max(1.2F, size * .07F), color, 8);
        drawList.AddLine(keyhole + new Vector2(0F, size * .06F),
            keyhole + new Vector2(0F, size * .18F), color, 1.4F);
    }

    private void DrawCenter()
    {
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(layout.LeftWidth, WorkspaceTop + 1F), new Vector2(layout.CenterWidth, layout.MainHeight));
        if (_requestedCenterTab == 0) ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("场景##场景窗口", PanelWindowFlags))
        {
        }
        else
        {
            DrawPanelWindowMenu();
            _centerTab = 0;
            DrawSceneView();
        }
        ImGui.End();

        SetPanelDefaults(new Vector2(layout.LeftWidth, WorkspaceTop + 1F), new Vector2(layout.CenterWidth, layout.MainHeight));
        if (_requestedCenterTab == 1) ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("渲染##渲染窗口", PanelWindowFlags))
        {
            _requestedCenterTab = null;
        }
        else
        {
            DrawPanelWindowMenu();
            _centerTab = 1;
            var renderViewFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (renderViewFocused && !_renderViewWasFocused)
            {
                _renderViewNeedsInitialRender = true;
            }
            _renderViewWasFocused = renderViewFocused;
            DrawRenderView();
        }
        ImGui.End();

        _requestedCenterTab = null;
    }

    private void DrawSceneView()
    {
        if (!ImGui.BeginChild("##SceneViewport", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        if (!_showHierarchy) DrawVoxelAssetStageBar();
        DrawSceneToolbar();
        DrawSceneSliceControls();

        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        _lastSceneViewportSize = size;
        var drawList = ImGui.GetWindowDrawList();
        var sceneSettings = CurrentSceneRenderSettings;
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(SceneBackgroundDisplayColor(_editorSettings.SceneRenderSettings)));

        var gizmoCenter = origin + new Vector2(size.X - 58F, 76F);
        var gizmoRadius = Math.Clamp(Math.Min(size.X, size.Y) * 0.07F, 28F, 36F);
        CancelFocusOnManualNavigation(origin, size, renderViewport: false);
        AdvanceSceneCameraFocus(ImGui.GetIO().DeltaTime);
        var gizmoHovered = HandleSceneNavigation(origin, size, gizmoCenter, gizmoRadius,
            allowMouseCapture: true, ViewportCaptureTarget.Scene);
        var camera = GetSceneCamera();
        var floatingToolSize = Math.Max(28F, ImGui.GetFrameHeight() * 1.25F);
        var floatingToolsHovered = ImGui.IsMouseHoveringRect(origin + new Vector2(8F, 10F),
            origin + new Vector2(8F + floatingToolSize, 10F + 4F * (floatingToolSize + 3F)));
        _colorSampleSceneOrigin = origin;
        _colorSampleSceneSize = size;
        var gizmoInput = !_screenColorSampling && HandleSceneGizmoInput(origin, size, camera, gizmoHovered || floatingToolsHovered);
        MVoxelLog.SetRuntimeContext(
            $"场景视图，视口={size.X:0}x{size.Y:0}，相机位置=({camera.Position.X:0.000},{camera.Position.Y:0.000},{camera.Position.Z:0.000})，" +
            $"距离={_sceneCameraDistance:0.000}，体素={_world.OccupiedVoxelCount}");

        // 辅助图层与模型内容是两个独立系统。空场景仍必须显示网格，
        // 否则用户无法判断原点、比例和编辑位置。

        // 场景窗口使用 GPU 绘制基础体素几何和无限解析网格，不启用路径追踪、Bloom、AO。
        // GPU 不可用时保留 CPU 兼容预览；坐标轴和编辑反馈仍由编辑器绘制层负责。
        var showVoxelWorld = IsVoxelWorldVisible();
        var gpuRendered = TryRenderGpuSceneView(drawList, origin, size, camera,
            showGeometry: showVoxelWorld && _project is not null);

        if (!gpuRendered)
        {
            if (_showGrid) SceneGrid.Draw(drawList, origin, size, camera, _sceneCameraDistance);
            if (showVoxelWorld && _project is not null)
                DrawVoxelWorldPreview(drawList, origin, size, camera, _showVoxelOutlines,
                    GetSceneInspectionWorld(GetRenderWorld(uint.MaxValue, sceneVisibility: true)));
        }

        drawList.PushClipRect(origin, origin + size, true);
        DrawSceneFloatingTools(drawList, origin, size);
        if (!_screenColorSampling && _project is not null && !gizmoHovered && !floatingToolsHovered && !gizmoInput && _sceneGizmoMode == SceneGizmoMode.Select &&
            (showVoxelWorld || GetVoxelWorldObject() is null))
        {
            HandleVoxelToolInput(origin, size, camera);
            DrawVoxelBrushPreview(drawList, origin, size, camera);
        }
        else if (_project is null && !gizmoHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                 ImGui.IsMouseHoveringRect(origin, origin + size))
        {
            RequestWelcomeWindow();
        }

        // ImGui 绘制列表没有场景深度，轴线在体素图像之后提交并按体素射线裁剪遮挡段
        DrawSceneWorldAxes(drawList, origin, size, camera);
        DrawSceneGizmos(drawList, origin, size, camera);

        DrawOrientationGizmo(drawList, gizmoCenter, gizmoRadius, camera, gizmoHovered);
        if (_showSceneStatistics) DrawSceneStatistics(drawList, origin);
        if (_sceneMouseCaptured && _capturedViewport == ViewportCaptureTarget.Scene)
        {
            DrawSceneCrosshair(drawList, origin + size * 0.5F);
        }

        drawList.PopClipRect();
        ImGui.EndChild();
    }

    /// <summary>
    /// 将场景配置中的线性背景色转换为 ImGui 使用的显示色
    /// </summary>
    /// <param name="settings">场景画面配置</param>
    /// <returns>显示色</returns>
    private static Color SceneBackgroundDisplayColor(RenderSettings settings)
    {
        var color = ToSrgbVector(settings.SceneBackgroundColor);
        return Color.FromArgb(
            255,
            Math.Clamp((int)MathF.Round(color.X * 255F), 0, 255),
            Math.Clamp((int)MathF.Round(color.Y * 255F), 0, 255),
            Math.Clamp((int)MathF.Round(color.Z * 255F), 0, 255));
    }

    private SceneCamera GetSceneCamera()
    {
        var target = new Vector3(_scenePan.X, _sceneFreeMoveHeight, _scenePan.Y);
        var direction = new Vector3(
            MathF.Cos(_sceneOrbitPitch) * MathF.Sin(_sceneOrbitYaw),
            MathF.Sin(_sceneOrbitPitch),
            MathF.Cos(_sceneOrbitPitch) * MathF.Cos(_sceneOrbitYaw));
        var position = target + direction * _sceneCameraDistance;
        var forward = Vector3.Normalize(target - position);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.UnitX;
        }

        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return new SceneCamera(position, target, forward, right, up)
        {
            NearClip = _sceneNearClip,
            FarClip = _sceneFarClip,
            FieldOfViewDegrees = _sceneFieldOfView,
        };
    }

    /// <summary>场景窗口顶部工具栏：切面、相机、信息依次靠右，统一按钮尺寸与状态色。</summary>
    private void DrawSceneToolbar()
    {
        const float buttonWidth = 82F;
        const float buttonHeight = 24F;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var left = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(left + Math.Max(0F, available - buttonWidth * 4F - spacing * 3F));

        if (SceneToolbarButton("图层##SceneLayers", ImGui.IsPopupOpen("##SceneLayersPopup"),
                buttonWidth, buttonHeight, "管理图层并设置场景窗口可见性，不影响相机的图层遮罩。"))
            ImGui.OpenPopup("##SceneLayersPopup");
        if (ImGui.BeginPopup("##SceneLayersPopup"))
        {
            DrawSceneLayers();
            ImGui.EndPopup();
        }
        ImGui.SameLine(0F, spacing);

        if (SceneToolbarButton("切面查看##SceneSlice", _sceneSliceEnabled, buttonWidth, buttonHeight,
                "非破坏性切面，仅影响场景窗口；启用后可选择轴和位置。"))
        {
            _sceneSliceEnabled = !_sceneSliceEnabled;
            _sceneSliceWorld = null;
        }

        ImGui.SameLine(0F, spacing);
        var cameraTip = IsCapsLockEnabled()
            ? _editorSettings.McMode
                ? "相机设置。Caps Lock：WASD 移动、Q/E 升降、鼠标转向；左键破坏、右键放置、Shift 加速、中键平移。"
                : "相机设置。Caps Lock：WASD 移动、Q/E 升降、鼠标转向；左键放置、右键破坏、Shift 加速、中键平移。"
            : "相机设置。左键放置、右键环绕、中键平移、Shift+左键破坏、滚轮缩放；点击 XYZ 控件定向。";
        if (SceneToolbarButton("相机##SceneCameraButton", ImGui.IsPopupOpen("##SceneCameraPopup"),
                buttonWidth, buttonHeight, cameraTip))
            ImGui.OpenPopup("##SceneCameraPopup");

        ImGui.SameLine(0F, spacing);
        if (SceneToolbarButton("信息##SceneInformation", _showSceneStatistics, buttonWidth, buttonHeight,
                "显示或隐藏帧率、帧耗时、体素数量和当前视角。"))
            _showSceneStatistics = !_showSceneStatistics;

        DrawSceneCameraPopup();
    }

    private bool DrawSceneFloatingTools(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        var saved = ImGui.GetCursorScreenPos();
        var buttonSize = Math.Max(28F, ImGui.GetFrameHeight() * 1.25F);
        var top = origin + new Vector2(8F, 10F);
        var hovered = false;
        for (var i = 0; i < 4; i++)
        {
            var position = top + new Vector2(0F, i * (buttonSize + 3F));
            ImGui.SetCursorScreenPos(position);
            ImGui.PushID(i);
            if (ImGui.InvisibleButton("##场景工具", new Vector2(buttonSize))) _sceneGizmoMode = (SceneGizmoMode)i;
            var over = ImGui.IsItemHovered(); hovered |= over;
            var color = ToPackedColor(i == (int)_sceneGizmoMode ? Color.FromArgb(230, EditorPalette.AccentSoft) :
                over ? Color.FromArgb(220, EditorPalette.PanelHeader) : Color.FromArgb(190, EditorPalette.Window));
            draw.AddRectFilled(position, position + new Vector2(buttonSize), color, 5F);
            draw.AddRect(position, position + new Vector2(buttonSize), ToPackedColor(EditorPalette.Border), 5F);
            var c = position + new Vector2(buttonSize * .5F);
            var ink = ToPackedColor(EditorPalette.Text);
            DrawSceneToolIcon(draw, i, c, buttonSize, ink);
            if (over) ImGui.SetTooltip(i switch { 0 => "选择", 1 => "移动", 2 => "旋转", _ => "缩放" });
            ImGui.PopID();
        }
        ImGui.SetCursorScreenPos(saved);
        return hovered;
    }

    /// <summary>使用方向箭头、旋转箭头和缩放手柄区分变换工具。</summary>
    private static void DrawSceneToolIcon(ImDrawListPtr draw, int tool, Vector2 center, float buttonSize, uint color)
    {
        var unit = buttonSize / 28F;
        Vector2 P(float x, float y) => center + new Vector2(x * unit, y * unit);
        var line = Math.Max(1.4F, 1.6F * unit);
        if (tool == 0)
        {
            draw.AddTriangle(P(-5F, -8F), P(-5F, 6F), P(5F, 2F), color, line);
            return;
        }
        if (tool == 1)
        {
            draw.AddLine(P(-7F, 0F), P(7F, 0F), color, line);
            draw.AddLine(P(0F, -7F), P(0F, 7F), color, line);
            draw.AddTriangleFilled(P(0F, -10F), P(-3F, -6F), P(3F, -6F), color);
            draw.AddTriangleFilled(P(0F, 10F), P(-3F, 6F), P(3F, 6F), color);
            draw.AddTriangleFilled(P(-10F, 0F), P(-6F, -3F), P(-6F, 3F), color);
            draw.AddTriangleFilled(P(10F, 0F), P(6F, -3F), P(6F, 3F), color);
            return;
        }
        if (tool == 2)
        {
            var previous = P(0F, -8F);
            for (var segment = 1; segment <= 20; segment++)
            {
                var angle = -MathF.PI / 2F + segment * (MathF.PI * 1.65F / 20F);
                var next = P(MathF.Cos(angle) * 8F, MathF.Sin(angle) * 8F);
                draw.AddLine(previous, next, color, line);
                previous = next;
            }
            draw.AddTriangleFilled(P(-8.5F, -7.5F), P(-3.5F, -8F), P(-6.5F, -3.5F), color);
            return;
        }
        draw.AddRect(P(-7F, -7F), P(3F, 3F), color, 0F, ImDrawFlags.None, line);
        draw.AddLine(P(1F, 1F), P(8F, 8F), color, line);
        draw.AddRectFilled(P(5F, 5F), P(10F, 10F), color);
        draw.AddRectFilled(P(-9F, -9F), P(-5F, -5F), color);
    }

    private static bool SceneToolbarButton(string label, bool active, float width, float height, string tooltip)
    {
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.Accent));
        var clicked = ImGui.Button(label, new Vector2(width, height));
        if (active) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void DrawSceneStatistics(ImDrawListPtr drawList, Vector2 origin, bool renderView = false)
    {
        var fps = MathF.Max(0F, ImGui.GetIO().Framerate);
        var milliseconds = fps > 0F ? 1000F / fps : 0F;
        var pipeline = renderView ? _pipeline : _scenePipeline;
        var gpuCells = _project is null ? 0 : pipeline?.SyncTotalCells ?? 0;
        var lines = new[]
        {
            $"帧率  {fps:0.0} FPS  ·  {milliseconds:0.0} ms",
            $"体素  {_world.OccupiedVoxelCount:N0}  ·  GPU 工作集  {gpuCells:N0}",
            $"工作集构建  {pipeline?.SyncWorksetBuildMs ?? 0:0.00} ms  ·  BVH  {pipeline?.SyncBvhBuildMs ?? 0:0.00} ms",
            $"打包  {pipeline?.SyncPackMs ?? 0:0.00} ms  ·  上传  {pipeline?.SyncUploadMs ?? 0:0.00} ms（CPU 最近更新）",
            $"计算派发  {pipeline?.LastDispatchCount ?? 0:N0}  ·  工作组  {pipeline?.LastWorkgroupCount ?? 0:N0}（最近提交）",
            renderView ? $"渲染采样  {pipeline?.AccumulatedSamples ?? 0:N0}" : $"视角  {_sceneOrbitYaw * 180F / MathF.PI:0}° / {_sceneOrbitPitch * 180F / MathF.PI:0}°  ·  距离  {_sceneCameraDistance:0.0}",
        };
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var width = lines.Max(line => ImGui.CalcTextSize(line).X) + 16F;
        var panelMin = origin + new Vector2(8F, 8F);
        drawList.AddRectFilled(panelMin, panelMin + new Vector2(width, lineHeight * lines.Length + 12F),
            ToPackedColor(Color.FromArgb(205, 24, 29, 36)), 4F);
        for (var index = 0; index < lines.Length; index++)
            drawList.AddText(panelMin + new Vector2(8F, 6F + lineHeight * index),
                ToPackedColor(Color.FromArgb(255, 230, 235, 241)), lines[index]);
    }

    /// <summary>
    /// 绘制场景相机的即时设置面板
    /// </summary>
    private void DrawSceneCameraPopup()
    {
        if (!ImGui.BeginPopup("##SceneCameraPopup")) return;

        ImGui.TextUnformatted("场景相机");
        ImGui.Separator();
        ImGui.SetNextItemWidth(220F);
        ImGui.SliderFloat("移动速度##SceneCamera", ref _sceneMoveSpeed, 0.1F, 500F, "%.1f 格/秒");
        ImGui.SetNextItemWidth(220F);
        ImGui.SliderFloat("近处裁剪##SceneCamera", ref _sceneNearClip, 0.001F, 100F, "%.3f");
        ImGui.SetNextItemWidth(220F);
        ImGui.SliderFloat("远处裁剪##SceneCamera", ref _sceneFarClip, 100F, 10_000_000F, "%.0f");
        ImGui.SetNextItemWidth(220F);
        ImGui.SliderFloat("视场角 FOV##SceneCamera", ref _sceneFieldOfView, 20F, 120F, "%.0f°");

        _sceneMoveSpeed = Math.Clamp(_sceneMoveSpeed, 0.1F, 500F);
        _sceneNearClip = Math.Clamp(_sceneNearClip, 0.001F, 100F);
        _sceneFarClip = Math.Clamp(_sceneFarClip, Math.Max(100F, _sceneNearClip + 0.001F), 10_000_000F);
        _sceneFieldOfView = Math.Clamp(_sceneFieldOfView, 20F, 120F);

        if (ImGui.Button("恢复默认##SceneCamera"))
        {
            _sceneMoveSpeed = 12F;
            _sceneNearClip = 0.05F;
            _sceneFarClip = 100000F;
            _sceneFieldOfView = 50F;
        }

        ImGui.EndPopup();
    }

    private bool HandleSceneNavigation(Vector2 origin, Vector2 size, Vector2 gizmoCenter, float gizmoRadius,
        bool allowMouseCapture, ViewportCaptureTarget captureTarget = ViewportCaptureTarget.Scene)
    {
        if (_welcomeWindow?.IsOpen == true || _projectOperation.IsRunning || _screenColorSampling || _historyConfirmation is not null)
        {
            EndSceneMouseCapture(captureTarget);
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        if (inViewport)
        {
            MVoxelLog.SetRuntimeContext(
                $"{captureTarget} 导航输入，鼠标=({mouse.X:0.0},{mouse.Y:0.0})，增量=({io.MouseDelta.X:0.0},{io.MouseDelta.Y:0.0})，" +
                $"左键={ImGui.IsMouseDown(ImGuiMouseButton.Left)}，中键={ImGui.IsMouseDown(ImGuiMouseButton.Middle)}，右键={ImGui.IsMouseDown(ImGuiMouseButton.Right)}，" +
                $"Shift={IsShiftPressed()}，CapsLock={IsCapsLockEnabled()}，距离={_sceneCameraDistance:0.000}，视口={size.X:0}x{size.Y:0}");
        }
        var gizmoHovered = inViewport && Vector2.Distance(mouse, gizmoCenter) <= gizmoRadius + 8F;
        if (!inViewport || !IsFocused || !ImGui.IsWindowHovered())
        {
            EndSceneMouseCapture(captureTarget);
            return false;
        }

        var capsLockMode = IsCapsLockEnabled();
        if (allowMouseCapture && capsLockMode)
        {
            BeginSceneMouseCapture(captureTarget);
            gizmoHovered = false;
        }
        else if (_capturedViewport == captureTarget)
        {
            EndSceneMouseCapture(captureTarget);
        }

        if (_sceneMouseCaptured && _capturedViewport == captureTarget)
        {
            var camera = GetSceneCamera();
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            {
                var panScale = _sceneCameraDistance / Math.Max(160F, Math.Min(size.X, size.Y));
                var pan = ViewportNavigationMath.Pan(camera, io.MouseDelta, panScale);
                _scenePan += new Vector2(pan.X, pan.Z);
                _sceneFreeMoveHeight += pan.Y;
            }
            else
            {
                _sceneOrbitYaw += ViewportInputPolicy.OrbitYawDelta(io.MouseDelta.X);
                _sceneOrbitPitch = Math.Clamp(_sceneOrbitPitch + ViewportInputPolicy.OrbitPitchDelta(io.MouseDelta.Y),
                    -1.35F, 1.35F);
            }

            UpdateFreeMove(camera, io.DeltaTime);
            return false;
        }

        if (ViewportInputPolicy.ShouldApplyWheelZoom(
                io.MouseWheel, ImGui.IsMouseDown(ImGuiMouseButton.Right)) && !gizmoHovered)
        {
            var limits = GetSceneZoomLimits();
            var continuousFactor = MathF.Exp(-io.MouseWheel * 0.2F);
            _sceneCameraDistance = Math.Clamp(_sceneCameraDistance * continuousFactor,
                limits.MinimumDistance, limits.MaximumDistance);
            _sceneZoom = Math.Clamp(18F / _sceneCameraDistance, 0.07F, 4F);
        }

        var orbitRight = !gizmoHovered && !capsLockMode && ImGui.IsMouseDragging(ImGuiMouseButton.Right);
        if (orbitRight)
        {
            _sceneOrbitYaw += ViewportInputPolicy.OrbitYawDelta(io.MouseDelta.X);
            _sceneOrbitPitch = Math.Clamp(_sceneOrbitPitch + ViewportInputPolicy.OrbitPitchDelta(io.MouseDelta.Y),
                -1.35F, 1.35F);
        }

        if (!gizmoHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var camera = GetSceneCamera();
            var panScale = _sceneCameraDistance / Math.Max(160F, Math.Min(size.X, size.Y));
            var pan = ViewportNavigationMath.Pan(camera, io.MouseDelta, panScale);
            _scenePan += new Vector2(pan.X, pan.Z);
            _sceneFreeMoveHeight += pan.Y;
        }

        if (gizmoHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var camera = GetSceneCamera();
            var axes = new[]
            {
                (Axis: Vector3.UnitX, Yaw: MathF.PI / 2F, Pitch: 0F),
                (Axis: Vector3.UnitY, Yaw: 0F, Pitch: 1.25F),
                (Axis: Vector3.UnitZ, Yaw: 0F, Pitch: 0F),
            };
            var closest = axes
                .Select(axis => (axis, point: gizmoCenter + ProjectDirection(axis.Axis, camera) * gizmoRadius * 0.76F))
                .OrderBy(item => Vector2.DistanceSquared(mouse, item.point))
                .First();
            if (Vector2.Distance(mouse, closest.point) <= 18F)
            {
                _sceneOrbitYaw = closest.axis.Yaw;
                _sceneOrbitPitch = closest.axis.Pitch;
                SetStatus($"已切换到 {AxisName(closest.axis.Axis)} 视图");
            }
        }

        return gizmoHovered;
    }

    private void BeginSceneMouseCapture(ViewportCaptureTarget target)
    {
        if (_sceneMouseCaptured && _capturedViewport == target)
        {
            return;
        }

        if (_sceneMouseCaptured)
        {
            EndSceneMouseCapture();
        }

        CursorState = OpenTkCursorState.Grabbed;
        _sceneMouseCaptured = true;
        _capturedViewport = target;
    }

    private void EndSceneMouseCapture(ViewportCaptureTarget? target = null)
    {
        if (!_sceneMouseCaptured || target is not null && _capturedViewport != target)
        {
            return;
        }

        CursorState = OpenTkCursorState.Normal;
        _sceneMouseCaptured = false;
        _capturedViewport = ViewportCaptureTarget.None;
    }

    private void UpdateFreeMove(SceneCamera camera, float deltaTime)
    {
        var movement = GetFreeMoveDelta(camera, deltaTime);
        _scenePan += new Vector2(movement.X, movement.Z);
        _sceneFreeMoveHeight += movement.Y;
    }

    /// <summary>
    /// 根据当前相机和按键状态计算自由视角位移
    /// </summary>
    /// <param name="camera">当前相机</param>
    /// <param name="deltaTime">帧间隔秒数</param>
    /// <returns>世界空间位移</returns>
    private Vector3 GetFreeMoveDelta(SceneCamera camera, float deltaTime)
    {
        var forward = new Vector3(camera.Forward.X, 0F, camera.Forward.Z);
        if (forward.LengthSquared > 0.0001F)
        {
            forward = Vector3.Normalize(forward);
        }

        var right = new Vector3(camera.Right.X, 0F, camera.Right.Z);
        if (right.LengthSquared > 0.0001F)
        {
            right = Vector3.Normalize(right);
        }

        var movement = Vector3.Zero;
        if (IsVirtualKeyDown(0x57, Keys.W)) movement += forward;
        if (IsVirtualKeyDown(0x53, Keys.S)) movement -= forward;
        if (IsVirtualKeyDown(0x44, Keys.D)) movement += right;
        if (IsVirtualKeyDown(0x41, Keys.A)) movement -= right;
        if (movement.LengthSquared > 0.0001F)
        {
            movement = Vector3.Normalize(movement);
            var speed = MathF.Max(0.1F, _sceneMoveSpeed) * (IsShiftPressed() ? 4F : 1F);
            movement *= speed * MathF.Max(deltaTime, 0.001F);
        }
        else
        {
            movement = Vector3.Zero;
        }

        var verticalSpeed = MathF.Max(0.1F, _sceneMoveSpeed) * (IsShiftPressed() ? 4F : 1F);
        if (IsVirtualKeyDown(0x45, Keys.E))
        {
            movement.Y += verticalSpeed * MathF.Max(deltaTime, 0.001F);
        }

        if (IsVirtualKeyDown(0x51, Keys.Q))
        {
            movement.Y -= verticalSpeed * MathF.Max(deltaTime, 0.001F);
        }

        return movement;
    }

    /// <summary>
    /// 读取 Caps Lock 的切换状态
    /// </summary>
    /// <returns>Caps Lock 是否已开启</returns>
    private static bool IsCapsLockEnabled() => (GetKeyState(CapsLockVirtualKey) & 1) != 0;

    /// <summary>
    /// 判断 Shift 是否正在按下
    /// </summary>
    /// <returns>Shift 是否正在按下</returns>
    private bool IsShiftPressed() =>
        KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift) ||
        IsAsyncKeyDown(0x10);

    /// <summary>
    /// 判断指定按键是否正在按下
    /// </summary>
    /// <param name="virtualKey">Windows 虚拟键码</param>
    /// <param name="fallbackKey">OpenTK 按键值</param>
    /// <returns>按键是否正在按下</returns>
    private bool IsVirtualKeyDown(int virtualKey, Keys fallbackKey) =>
        KeyboardState.IsKeyDown(fallbackKey) || IsAsyncKeyDown(virtualKey);

    /// <summary>
    /// 读取 Windows 当前按键状态
    /// </summary>
    /// <param name="virtualKey">Windows 虚拟键码</param>
    /// <returns>按键是否正在按下</returns>
    private static bool IsAsyncKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// 读取 Windows 键盘切换键状态
    /// </summary>
    /// <param name="virtualKey">Windows 虚拟键码</param>
    /// <returns>键的当前状态</returns>
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    /// <summary>
    /// 读取 Windows 当前异步按键状态
    /// </summary>
    /// <param name="virtualKey">Windows 虚拟键码</param>
    /// <returns>按键状态位</returns>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static void DrawSceneCrosshair(ImDrawListPtr drawList, Vector2 center)
    {
        var dark = ToPackedColor(Color.FromArgb(235, 10, 12, 16));
        var light = ToPackedColor(Color.FromArgb(255, 242, 246, 250));
        drawList.AddLine(center - new Vector2(10F, 0F), center + new Vector2(10F, 0F), dark, 3F);
        drawList.AddLine(center - new Vector2(0F, 10F), center + new Vector2(0F, 10F), dark, 3F);
        drawList.AddLine(center - new Vector2(8F, 0F), center + new Vector2(8F, 0F), light, 1F);
        drawList.AddLine(center - new Vector2(0F, 8F), center + new Vector2(0F, 8F), light, 1F);
    }

    private void DrawSceneWorldAxes(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_world.OccupiedVoxelCount > 0 && IsVoxelWorldVisible())
            UpdateAxisOcclusionBounds();
        var axisLength = GetInfiniteAxisLength(camera);
        DrawBidirectionalAxis(drawList, Vector3.UnitX, axisLength, origin, size, camera,
            Color.FromArgb(235, 230, 78, 84));
        DrawBidirectionalAxis(drawList, Vector3.UnitY, axisLength, origin, size, camera,
            Color.FromArgb(235, 88, 210, 132));
        DrawBidirectionalAxis(drawList, Vector3.UnitZ, axisLength, origin, size, camera,
            Color.FromArgb(235, 82, 154, 238));
    }

    /// <summary>
    /// 根据相机到世界原点的距离计算足够覆盖当前视锥的坐标轴长度
    /// </summary>
    private static float GetInfiniteAxisLength(SceneCamera camera)
    {
        var cameraDistance = MathF.Max(camera.Position.Length, camera.Target.Length);
        if (!float.IsFinite(cameraDistance)) return 1_000_000F;
        return Math.Clamp(cameraDistance * 4F + 1_024F, 1_024F, 100_000_000F);
    }

    /// <summary>
    /// 绘制穿过原点并向正负方向延伸的双向坐标轴
    /// </summary>
    private void DrawBidirectionalAxis(ImDrawListPtr drawList, Vector3 axis, float length,
        Vector2 origin, Vector2 size, SceneCamera camera, Color color)
    {
        var positive = ToPackedColor(color);
        var negative = ToPackedColor(Color.FromArgb(color.A / 2, color.R, color.G, color.B));
        DrawOcclusionAwareAxis(drawList, Vector3.Zero, axis * length, origin, size, camera, positive, 2F);
        DrawOcclusionAwareAxis(drawList, Vector3.Zero, -axis * length, origin, size, camera, negative, 2F);
    }

    private void DrawOcclusionAwareAxis(ImDrawListPtr drawList, Vector3 start, Vector3 end, Vector2 origin, Vector2 size,
        SceneCamera camera, uint color, float thickness)
    {
        if (!SceneProjection.ClipLineWithWorld(start, end, origin, size, camera,
                out var clippedStart, out var clippedEnd, out var startPoint, out var endPoint))
        {
            return;
        }

        if (_world.OccupiedVoxelCount == 0 || !IsVoxelWorldVisible())
        {
            drawList.AddLine(startPoint, endPoint, color, thickness);
            return;
        }

        var screenLength = Vector2.Distance(startPoint, endPoint);
        // 遮挡采样按屏幕长度预算，避免相机拉远后按巨大世界长度重复执行体素 DDA
        var sampleCount = Math.Clamp((int)MathF.Ceiling(screenLength / 3F), 64, 512);
        var previousWorld = clippedStart;
        var previousScreen = startPoint;
        var previousVisible = IsAxisPointVisible(clippedStart, origin, size, camera);
        for (var index = 1; index <= sampleCount; index++)
        {
            var t = index / (float)sampleCount;
            var worldPoint = Vector3.Lerp(clippedStart, clippedEnd, t);
            if (!SceneProjection.Project(worldPoint, origin, size, camera, out var screenPoint, out _))
            {
                previousVisible = false;
                continue;
            }

            var visible = IsAxisPointVisible(worldPoint, origin, size, camera);
            if (previousVisible && visible)
            {
                drawList.AddLine(previousScreen, screenPoint, color, thickness);
            }
            else if (previousVisible != visible)
            {
                var boundary = RefineAxisVisibilityTransition(previousWorld, worldPoint, previousVisible,
                    origin, size, camera);
                if (SceneProjection.Project(boundary, origin, size, camera, out var boundaryScreen, out _))
                {
                    if (previousVisible)
                    {
                        drawList.AddLine(previousScreen, boundaryScreen, color, thickness);
                    }
                    else if (visible)
                    {
                        drawList.AddLine(boundaryScreen, screenPoint, color, thickness);
                    }
                }
            }

            previousWorld = worldPoint;
            previousScreen = screenPoint;
            previousVisible = visible;
        }
    }

    /// <summary>
    /// 二分查找轴线与模型遮挡状态的切换位置
    /// </summary>
    /// <param name="from">当前采样世界点</param>
    /// <param name="to">下一个采样世界点</param>
    /// <param name="fromVisible">当前采样点是否可见</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>遮挡状态切换附近的世界点</returns>
    private Vector3 RefineAxisVisibilityTransition(Vector3 from, Vector3 to, bool fromVisible,
        Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var lower = from;
        var upper = to;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var middle = (lower + upper) / 2F;
            var middleVisible = IsAxisPointVisible(middle, origin, size, camera);
            if (middleVisible == fromVisible)
            {
                lower = middle;
            }
            else
            {
                upper = middle;
            }
        }

        return (lower + upper) / 2F;
    }

    /// <summary>
    /// 判断坐标轴采样点是否位于模型表面之前
    /// </summary>
    private bool IsAxisPointVisible(Vector3 worldPoint, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (!SceneProjection.Project(worldPoint, origin, size, camera, out var screenPoint, out _)) return false;
        var ray = SceneProjection.RayFromScreen(screenPoint, origin, size, camera);
        var cameraPosition = new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
        var direction = new NumVector3(ray.X, ray.Y, ray.Z);
        var pointDistance = Vector3.Distance(camera.Position, worldPoint);
        var near = float.IsFinite(camera.NearClip) ? Math.Max(0.001F, camera.NearClip) : SceneProjection.Near;
        if (pointDistance <= near)
        {
            return true;
        }

        // 只查询目标点之前的射线段，命中模型内部或模型后的部分不会被误判为可见
        return !VoxelDda.HasHitInBounds(_world, cameraPosition, direction,
            MathF.Max(pointDistance - 0.01F, near), _axisOcclusionMinimum, _axisOcclusionMaximum,
            ignoreOriginCell: true);
    }

    /// <summary>
    /// 获取当前场景中的体素模型对象
    /// </summary>
    /// <returns>体素模型对象或空值</returns>
    private SceneObject? GetVoxelWorldObject()
    {
        return _sceneDocument?.Objects.FirstOrDefault(item =>
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
    }

    /// <summary>
    /// 判断对象是否贡献可渲染体素：模型组件、曲线渲染器或树生成器
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>存在启用的体素来源时为真</returns>
    private static bool HasVoxelSource(SceneObject sceneObject) => sceneObject.Components.Any(component =>
        component.Enabled &&
        (SceneComponentTypes.IsVoxelModel(component) || SceneComponentTypes.IsGeneratedVoxelComponent(component)));

    /// <summary>
    /// 判断体素模型对象是否允许显示
    /// </summary>
    /// <returns>允许显示时返回 true</returns>
    private bool IsVoxelWorldVisible()
    {
        var voxelObjects = _sceneDocument?.Objects.Where(HasVoxelSource).ToArray();
        var sceneObjectsById = _sceneDocument?.Objects.ToDictionary(item => item.Id);
        return voxelObjects is { Length: > 0 } && voxelObjects.Any(voxelObject =>
            voxelObject.Visible && IsSceneObjectAncestorsVisible(voxelObject, sceneObjectsById) &&
            (_sceneDocument?.IsLayerVisible(voxelObject.LayerIndex) ?? true) && HasVoxelSource(voxelObject));
    }

    private static bool IsSceneObjectAncestorsVisible(SceneObject sceneObject,
        IReadOnlyDictionary<StableId, SceneObject>? byId)
    {
        if (byId is null) return sceneObject.Visible;
        var visited = new HashSet<StableId>();
        for (SceneObject? current = sceneObject; current is not null;)
        {
            if (!current.Visible || !visited.Add(current.Id)) return false;
            current = current.ParentId is { } parent && byId.TryGetValue(parent, out var found) ? found : null;
        }
        return true;
    }

    /// <summary>
    /// 当前固定的模型编辑目标；模型的锁图标固定归属，不再禁止体素笔刷。
    /// </summary>
    private SceneObject? GetPinnedVoxelModelObject()
    {
        var objects = _sceneDocument?.Objects;
        if (objects is null) return null;
        var pinned = objects.FirstOrDefault(item => item.Id == _pinnedVoxelModelId && item.Locked &&
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
        pinned ??= objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId && item.Locked &&
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
        return pinned ?? objects.FirstOrDefault(item => item.Locked &&
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
    }

    private void UnlockSceneSelectionForHierarchy(StableId selectedId)
    {
        foreach (var item in _sceneDocument?.Objects ?? [])
            if (item.Id != selectedId && item.Locked) { item.Locked = false; MarkSceneDocumentDirty(); }
    }

    private bool IsSceneSelectionLockedAgainst(StableId id) =>
        (_sceneDocument?.Objects ?? []).Any(item => item.Locked && item.Id != id);

    private void ToggleSceneObjectLock(SceneObject sceneObject)
    {
        var locked = !sceneObject.Locked;
        if (locked)
        {
            UnlockSceneSelectionForHierarchy(sceneObject.Id);
            _selectedSceneObjectId = sceneObject.Id;
        }
        sceneObject.Locked = locked;
        MarkSceneDocumentDirty();
    }

    private static void DrawVoxelPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        bool showOutlines)
    {
        var samples = new[]
        {
            new PreviewVoxel(new Vector3(-1F, 0.5F, 0F), Color.FromArgb(235, 71, 163, 224)),
            new PreviewVoxel(new Vector3(0F, 0.5F, 0F), Color.FromArgb(235, 82, 194, 144)),
            new PreviewVoxel(new Vector3(1F, 0.5F, 0F), Color.FromArgb(235, 232, 104, 112)),
            new PreviewVoxel(new Vector3(0F, 1.5F, 0F), Color.FromArgb(235, 238, 181, 86)),
            new PreviewVoxel(new Vector3(0F, 0.5F, -1F), Color.FromArgb(235, 151, 115, 222)),
        };

        foreach (var voxel in samples.OrderByDescending(item => Vector3.Dot(item.Center - camera.Position, camera.Forward)))
        {
            DrawVoxelCube(drawList, voxel.Center, 1F, voxel.Color, origin, size, camera, showOutlines);
        }
    }

    /// <summary>
    /// 按深度顺序绘制体素世界的轻量 CPU 预览
    /// </summary>
    /// <param name="drawList">场景绘制列表</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="showOutlines">是否绘制体素黑色描边</param>
    private void DrawVoxelWorldPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        bool showOutlines, VoxelWorld? previewWorld = null)
    {
        previewWorld ??= _world;
        EnsureCpuPreviewSurface(IsCameraInsideOccupiedVoxel(camera, previewWorld), previewWorld);
        var faces = new List<CpuPreviewFace>(_cpuPreviewSurfaceFaces.Count);
        for (var surfaceIndex = 0; surfaceIndex < _cpuPreviewSurfaceFaces.Count; surfaceIndex++)
        {
            var surface = _cpuPreviewSurfaceFaces[surfaceIndex];
            var center = (surface.Corners[0] + surface.Corners[1] + surface.Corners[2] + surface.Corners[3]) / 4F;
            // CPU 回退没有硬件深度缓冲；先剔除背向面，避免模型背面的面片在
            // 斜视角被错误排到前景。法线来自体素外露面的固定朝向，和顶点绕序无关。
            if (Vector3.Dot(surface.Normal, camera.Position - center) <= 0.0001F) continue;

            var points = new Vector2[4];
            var nearestDepth = float.PositiveInfinity;
            var farthestDepth = float.NegativeInfinity;
            var totalDepth = 0F;
            for (var index = 0; index < surface.Corners.Length; index++)
            {
                if (!TryProject(surface.Corners[index], origin, size, camera, out points[index], out var depth))
                    goto nextFace;
                nearestDepth = Math.Min(nearestDepth, depth);
                farthestDepth = Math.Max(farthestDepth, depth);
                totalDepth += depth;
            }
            faces.Add(new CpuPreviewFace(points, surface.Color, nearestDepth, totalDepth / 4F,
                farthestDepth, surfaceIndex));
            nextFace:;
        }

        // 平均深度在“地面横面”和“建筑竖面”相交的投影中会发生次序反转；
        // 先以最远顶点深度排序，能让跨越更大深度范围的远面优先落笔，
        // 再用平均/最近深度和稳定序号打破平局，避免微小转角时闪烁或穿模。
        faces.Sort(static (left, right) =>
        {
            var comparison = right.FarthestDepth.CompareTo(left.FarthestDepth);
            if (comparison != 0) return comparison;
            comparison = right.MeanDepth.CompareTo(left.MeanDepth);
            if (comparison != 0) return comparison;
            comparison = right.NearestDepth.CompareTo(left.NearestDepth);
            return comparison != 0 ? comparison : left.StableOrder.CompareTo(right.StableOrder);
        });
        foreach (var face in faces)
        {
            var polygon = face.Points;
            drawList.AddConvexPolyFilled(ref polygon[0], polygon.Length, ToPackedColor(face.Color));
            if (showOutlines)
            {
                var shortestEdge = float.PositiveInfinity;
                for (var index = 0; index < polygon.Length; index++)
                    shortestEdge = MathF.Min(shortestEdge,
                        Vector2.Distance(polygon[index], polygon[(index + 1) % polygon.Length]));
                var fade = Math.Clamp((shortestEdge - 2F) / 7F, 0F, 1F);
                fade = fade * fade * (3F - 2F * fade);
                var alpha = (int)(245F * fade);
                if (alpha > 0)
                {
                    var outline = ToPackedColor(Color.FromArgb(alpha, 4, 7, 11));
                    for (var index = 0; index < polygon.Length; index++)
                        drawList.AddLine(polygon[index], polygon[(index + 1) % polygon.Length], outline, 1.2F);
                }
            }
        }
    }

    private void EnsureCpuPreviewSurface(bool includeInterior, VoxelWorld previewWorld)
    {
        if (ReferenceEquals(_cpuPreviewWorld, previewWorld) && _cpuPreviewWorldVersion == previewWorld.ContentVersion &&
            _cpuPreviewIncludesInterior == includeInterior)
            return;

        const int maximumPreviewFaces = 250_000;
        _cpuPreviewSurfaceFaces.Clear();
        _cpuPreviewWorld = previewWorld;
        // 场景窗口不使用渲染窗口的光照或路径追踪，且在关闭场景 LOD 时必须保持
        // 一体素一格的几何语义。这里不能使用渲染/导出路径的贪心盒合并，否则相邻
        // 体素会变成长方体，靠近模型时尤其明显。仅绘制外露面，仍能控制 ImGui 面片量。
        var workset = VoxelGpuWorkset.Build(previewWorld, greedyMerge: false, includeInterior: includeInterior);
        foreach (var cell in workset.Cells)
        {
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Left, maximumPreviewFaces);
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Right, maximumPreviewFaces);
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Bottom, maximumPreviewFaces);
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Top, maximumPreviewFaces);
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Back, maximumPreviewFaces);
            AddCpuPreviewBoxSurfaceFace(workset.Origin, cell, FaceDirection.Front, maximumPreviewFaces);
        }

        if (_cpuPreviewSurfaceFaces.Count >= maximumPreviewFaces)
        {
            MVoxelLog.Info($"场景窗口 CPU 预览已限额：原始 {previewWorld.OccupiedVoxelCount:N0} 体素，合并盒 {workset.Cells.Count:N0}，表面 {_cpuPreviewSurfaceFaces.Count:N0} 面");
        }

        _cpuPreviewWorld = previewWorld;
        _cpuPreviewWorldVersion = previewWorld.ContentVersion;
        _cpuPreviewIncludesInterior = includeInterior;
    }

    private void AddCpuPreviewBoxSurfaceFace(VoxelCoord64 worksetOrigin, VoxelGpuCell cell,
        FaceDirection direction, int maximumFaces)
    {
        if (_cpuPreviewSurfaceFaces.Count >= maximumFaces) return;

        var coordinate = new VoxelCoord64(
            worksetOrigin.X + cell.X,
            worksetOrigin.Y + cell.Y,
            worksetOrigin.Z + cell.Z);
        var extentX = Math.Max(1, cell.ExtentX);
        var extentY = Math.Max(1, cell.ExtentY);
        var extentZ = Math.Max(1, cell.ExtentZ);
        if (!IsCpuPreviewBoxFaceExposed(coordinate, extentX, extentY, extentZ, direction))
            return;

        var x = (float)coordinate.X;
        var y = (float)coordinate.Y;
        var z = (float)coordinate.Z;
        var maxX = x + extentX;
        var maxY = y + extentY;
        var maxZ = z + extentZ;
        var corners = direction switch
        {
            FaceDirection.Left => new[] { new Vector3(x, y, z), new Vector3(x, maxY, z), new Vector3(x, maxY, maxZ), new Vector3(x, y, maxZ) },
            FaceDirection.Right => new[] { new Vector3(maxX, y, maxZ), new Vector3(maxX, maxY, maxZ), new Vector3(maxX, maxY, z), new Vector3(maxX, y, z) },
            FaceDirection.Bottom => new[] { new Vector3(x, y, maxZ), new Vector3(maxX, y, maxZ), new Vector3(maxX, y, z), new Vector3(x, y, z) },
            FaceDirection.Top => new[] { new Vector3(x, maxY, z), new Vector3(maxX, maxY, z), new Vector3(maxX, maxY, maxZ), new Vector3(x, maxY, maxZ) },
            FaceDirection.Back => new[] { new Vector3(maxX, y, z), new Vector3(maxX, maxY, z), new Vector3(x, maxY, z), new Vector3(x, y, z) },
            _ => new[] { new Vector3(x, y, maxZ), new Vector3(x, maxY, maxZ), new Vector3(maxX, maxY, maxZ), new Vector3(maxX, y, maxZ) },
        };
        var shade = direction switch
        {
            FaceDirection.Top => 1.12F,
            FaceDirection.Bottom => 0.62F,
            FaceDirection.Left => 0.72F,
            FaceDirection.Right => 0.94F,
            FaceDirection.Back => 0.86F,
            _ => 0.98F,
        };
        var baseColor = Color.FromArgb(cell.Value.Color.A, cell.Value.Color.R, cell.Value.Color.G, cell.Value.Color.B);
        var normal = direction switch
        {
            FaceDirection.Left => -Vector3.UnitX,
            FaceDirection.Right => Vector3.UnitX,
            FaceDirection.Bottom => -Vector3.UnitY,
            FaceDirection.Top => Vector3.UnitY,
            FaceDirection.Back => -Vector3.UnitZ,
            _ => Vector3.UnitZ,
        };
        _cpuPreviewSurfaceFaces.Add(new CpuPreviewSurfaceFace(corners, normal, Shade(baseColor, shade)));
    }

    private bool IsCpuPreviewBoxFaceExposed(VoxelCoord64 coordinate, int extentX, int extentY, int extentZ,
        FaceDirection direction)
    {
        var width = direction is FaceDirection.Left or FaceDirection.Right ? extentZ : extentX;
        var height = direction is FaceDirection.Top or FaceDirection.Bottom ? extentZ : extentY;
        for (var u = 0; u < width; u++)
        for (var v = 0; v < height; v++)
        {
            var neighbor = direction switch
            {
                FaceDirection.Left => new VoxelCoord64(coordinate.X - 1, coordinate.Y + v, coordinate.Z + u),
                FaceDirection.Right => new VoxelCoord64(coordinate.X + extentX, coordinate.Y + v, coordinate.Z + u),
                FaceDirection.Bottom => new VoxelCoord64(coordinate.X + u, coordinate.Y - 1, coordinate.Z + v),
                FaceDirection.Top => new VoxelCoord64(coordinate.X + u, coordinate.Y + extentY, coordinate.Z + v),
                FaceDirection.Back => new VoxelCoord64(coordinate.X + u, coordinate.Y + v, coordinate.Z - 1),
                _ => new VoxelCoord64(coordinate.X + u, coordinate.Y + v, coordinate.Z + extentZ),
            };
            var neighborWorld = _cpuPreviewWorld ?? _world;
            if (!neighborWorld.TryGet(neighbor, out var value) || value.IsEmpty) return true;
        }

        return false;
    }

    private enum FaceDirection
    {
        Left, Right, Bottom, Top, Back, Front,
    }

    private readonly record struct CpuPreviewFace(Vector2[] Points, Color Color, float NearestDepth,
        float MeanDepth, float FarthestDepth, int StableOrder);

    private readonly record struct CpuPreviewSurfaceFace(Vector3[] Corners, Vector3 Normal, Color Color);

    private static void DrawVoxelCube(ImDrawListPtr drawList, Vector3 center, float side, Color baseColor,
        Vector2 origin, Vector2 size, SceneCamera camera, bool showOutline)
    {
        var half = side / 2F;
        var corners = new[]
        {
            center + new Vector3(-half, -half, -half),
            center + new Vector3(half, -half, -half),
            center + new Vector3(half, half, -half),
            center + new Vector3(-half, half, -half),
            center + new Vector3(-half, -half, half),
            center + new Vector3(half, -half, half),
            center + new Vector3(half, half, half),
            center + new Vector3(-half, half, half),
        };
        var points = new Vector2[8];
        var depths = new float[8];
        var near = float.IsFinite(camera.NearClip) ? Math.Max(0.001F, camera.NearClip) : SceneProjection.Near;
        var far = float.IsFinite(camera.FarClip) ? Math.Max(near + 0.001F, camera.FarClip) : 100_000_000F;
        for (var index = 0; index < corners.Length; index++)
        {
            var corner = corners[index];
            var depth = Vector3.Dot(corner - camera.Position, camera.Forward);
            if (depth < near)
            {
                corner += camera.Forward * (near + 0.001F - depth);
            }
            else if (depth > far)
            {
                corner -= camera.Forward * (depth - far + 0.001F);
            }

            if (!TryProject(corner, origin, size, camera, out points[index], out depths[index]))
            {
                return;
            }
        }

        var faces = new[]
        {
            new CubeFace(new[] { 0, 1, 5, 4 }, Shade(baseColor, 0.72F), (depths[0] + depths[1] + depths[5] + depths[4]) / 4F),
            new CubeFace(new[] { 3, 7, 6, 2 }, Shade(baseColor, 1.12F), (depths[3] + depths[7] + depths[6] + depths[2]) / 4F),
            new CubeFace(new[] { 0, 4, 7, 3 }, Shade(baseColor, 0.86F), (depths[0] + depths[4] + depths[7] + depths[3]) / 4F),
            new CubeFace(new[] { 1, 2, 6, 5 }, Shade(baseColor, 0.94F), (depths[1] + depths[2] + depths[6] + depths[5]) / 4F),
            new CubeFace(new[] { 0, 3, 2, 1 }, Shade(baseColor, 0.62F), (depths[0] + depths[3] + depths[2] + depths[1]) / 4F),
            new CubeFace(new[] { 4, 5, 6, 7 }, Shade(baseColor, 0.98F), (depths[4] + depths[5] + depths[6] + depths[7]) / 4F),
        };
        foreach (var face in faces.OrderByDescending(face => face.Depth))
        {
            var polygon = face.Indices.Select(index => points[index]).ToArray();
            drawList.AddConvexPolyFilled(ref polygon[0], polygon.Length, ToPackedColor(face.Color));
        }

        if (!showOutline)
        {
            return;
        }

        var outline = ToPackedColor(Color.FromArgb(245, 4, 7, 11));
        var edges = new[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (start, end) in edges)
        {
            drawList.AddLine(points[start], points[end], outline, 1.35F);
        }
    }

    private static Color Shade(Color color, float factor)
    {
        return Color.FromArgb(color.A,
            Math.Clamp((int)(color.R * factor), 0, 255),
            Math.Clamp((int)(color.G * factor), 0, 255),
            Math.Clamp((int)(color.B * factor), 0, 255));
    }

    private static bool TryProject(Vector3 world, Vector2 origin, Vector2 size, SceneCamera camera,
        out Vector2 screen, out float depth)
    {
        return SceneProjection.Project(world, origin, size, camera, out screen, out depth);
    }

    private static Vector2 ProjectDirection(Vector3 axis, SceneCamera camera)
    {
        var direction = new Vector2(Vector3.Dot(axis, camera.Right), -Vector3.Dot(axis, camera.Up));
        return direction;
    }

    private bool DrawOrientationGizmo(ImDrawListPtr drawList, Vector2 center, float radius, SceneCamera camera, bool hovered)
    {
        var io = ImGui.GetIO();
        var panelRadius = radius + 8F;
        drawList.AddCircleFilled(center, panelRadius, ToPackedColor(Color.FromArgb(215, 28, 36, 48)), 32);
        drawList.AddCircle(center, panelRadius, ToPackedColor(Color.FromArgb(210, 155, 173, 194)), 32, 1.2F);

        var axes = new[]
        {
            (Axis: Vector3.UnitX, Label: "X", Color: Color.FromArgb(255, 238, 92, 98)),
            (Axis: Vector3.UnitY, Label: "Y", Color: Color.FromArgb(255, 96, 220, 136)),
            (Axis: Vector3.UnitZ, Label: "Z", Color: FromArgb(255, 92, 158, 248)),
        };
        var points = axes
            .Select(axis => (axis, point: center + ProjectDirection(axis.Axis, camera) * radius * 0.76F))
            .OrderBy(item => Vector3.Dot(item.axis.Axis, camera.Forward))
            .ToArray();
        foreach (var item in points)
        {
            var color = ToPackedColor(item.axis.Color);
            drawList.AddLine(center, item.point, color, 3F);
            drawList.AddCircleFilled(item.point, 5F, color, 12);
            drawList.AddCircle(item.point, 7F, ToPackedColor(Color.FromArgb(180, 8, 12, 18)), 12, 1.2F);
            drawList.AddText(item.point + new Vector2(7F, -7F), color, item.axis.Label);
        }
        drawList.AddCircleFilled(center, 4F, ToPackedColor(Color.FromArgb(235, 220, 228, 238)), 16);

        var mouse = io.MousePos;
        if (!hovered)
        {
            return Vector2.Distance(mouse, center) <= radius + 8F;
        }

        drawList.AddCircle(center, panelRadius + 2F, ToPackedColor(EditorPalette.Accent), 32, 1.2F);
        return true;
    }

    private static string AxisName(Vector3 axis)
    {
        return axis == Vector3.UnitX ? "X" : axis == Vector3.UnitY ? "Y" : "Z";
    }

    private readonly record struct PreviewVoxel(Vector3 Center, Color Color);

    private readonly record struct CubeFace(int[] Indices, Color Color, float Depth);

    private static Color FromArgb(int alpha, int red, int green, int blue) => Color.FromArgb(alpha, red, green, blue);

    private void DrawRenderView()
    {
        DrawRenderCameraSelector();
        DrawRenderFrameSelector();
        ImGui.Separator();
        if (!ImGui.BeginChild("##RenderViewport", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        var io = ImGui.GetIO();
        _lastRenderViewportSize = size;
        CancelFocusOnManualNavigation(origin, size, renderViewport: true);
        AdvanceRenderCameraFocus(io.DeltaTime);
        if (GetActiveRenderCameraObject() is null)
        {
            AdvanceSceneCameraFocus(io.DeltaTime);
        }
        MVoxelLog.SetRuntimeContext(
            $"RenderView 导航输入，鼠标=({io.MousePos.X:0.0},{io.MousePos.Y:0.0})，增量=({io.MouseDelta.X:0.0},{io.MouseDelta.Y:0.0})，" +
            $"左键={ImGui.IsMouseDown(ImGuiMouseButton.Left)}，中键={ImGui.IsMouseDown(ImGuiMouseButton.Middle)}，右键={ImGui.IsMouseDown(ImGuiMouseButton.Right)}，" +
            $"Shift={IsShiftPressed()}，CapsLock={IsCapsLockEnabled()}，视口={size.X:0}x{size.Y:0}");
        var camera = HandleRenderCameraNavigation(origin, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(Color.FromArgb(255, 14, 18, 24)));
        var showVoxelWorld = IsVoxelWorldVisible();
        var gpuRendered = false;
        if (!gpuRendered)
            gpuRendered = showVoxelWorld && _project is not null && TryRenderGpuPathView(drawList, origin, size, camera);
        if (!gpuRendered)
        {
            if (showVoxelWorld)
            {
                DrawVoxelWorldPreview(drawList, origin, size, camera, showOutlines: false);
            }
            var navigating = ImGui.IsMouseDragging(ImGuiMouseButton.Right) ||
                             ImGui.IsMouseDragging(ImGuiMouseButton.Middle);
            if (!navigating)
            {
                var activeRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject());
                ImGui.SetCursorScreenPos(origin + new Vector2(
                    Math.Max(0F, (size.X - ImGui.CalcTextSize("渲染管线不可用").X) / 2F), size.Y / 2F));
                ImGui.TextDisabled(showVoxelWorld
                    ? activeRenderSettings.PathTracingEnabled
                        ? _pipelineStatus.Length > 0 ? "显卡路径追踪不可用 · 当前显示 CPU 预览" : "显卡路径追踪初始化中"
                        : _pipelineStatus.Length > 0 ? "实时渲染管线不可用 · 当前显示 CPU 预览" : "实时渲染管线初始化中"
                    : "体素世界已隐藏");
                if (_pipelineStatus.Length > 0)
                {
                    // 失败原因直接摆在视口里：降级本身用户能看见，但“为什么降级”以前只在日志里
                    var reason = _pipelineStatus.Length > 180 ? string.Concat(_pipelineStatus.AsSpan(0, 180), "…") : _pipelineStatus;
                    ImGui.SetCursorScreenPos(origin + new Vector2(12F, size.Y * .5F + 24F));
                    ImGui.PushTextWrapPos(origin.X + size.X - 12F);
                    ImGui.TextColored(new System.Numerics.Vector4(1F, .66F, .3F, 1F), reason);
                    ImGui.PopTextWrapPos();
                }
            }
        }
        else
        {
            DrawRenderViewOverlay(origin, size, drawList);
        }

        if (_sceneMouseCaptured && _capturedViewport == ViewportCaptureTarget.Render)
        {
            DrawSceneCrosshair(drawList, origin + size * 0.5F);
        }

        ImGui.EndChild();
    }

    private void ProcessExportQueue(SceneCamera camera, Vector2 size)
    {
        if (_activeExport is null && _exportQueue.Count == 0)
        {
            _exportSnapshot = null;
            _exportSnapshotSource = null;
            _exportSnapshotVersion = -1;
            return;
        }
        if (_vulkan is null) return;
        _exportPipeline ??= new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
        var pipeline = _exportPipeline;
        if (_cancelActiveExportRequested)
        {
            _cancelActiveExportRequested = false;
            if (_activeExport is not null)
            {
                SetStatus($"已取消导出：{Path.GetFileName(_activeExport.Path)}");
                //视频按整批取消，避免只停一帧后整条帧链断在半路
                if (_activeExport.Video is { } videoBatch) AbortVideoExport(videoBatch, "已取消视频导出", failed: false);
                else CompleteVideoFrame(_activeExport, false);
                _activeExport = null;
            }
        }
        if (_activeExport is null && _exportQueue.Count > 0)
        {
            _activeExport = _exportQueue.Dequeue();
            pipeline.ResetAccumulation();
            if (_activeExport.Materials is { } materials) pipeline.SetMaterialTable(materials);
        }
        if (_activeExport is null) return;
        try
        {
            var settings = _activeExport.Settings;
            if (pipeline.IsInitializing) return;
            if (!_activeExport.TimelineApplied && (_activeExport.Video is not null || _activeExport.TimelineAsset is not null))
            {
                if (!CaptureTimelineExportFrame(_activeExport, _activeExport.TimelineAsset))
                {
                    SetStatus($"时间轴帧采样失败：{Path.GetFileName(_activeExport.Path)}");
                    CompleteVideoFrame(_activeExport, false);
                    _activeExport = null;
                    return;
                }
                _activeExport.TimelineApplied = true;
            }
            var viewCamera = _activeExport.Camera ?? camera;
            viewCamera = viewCamera with { FieldOfViewDegrees = settings.CameraFovDegrees };
            var mask = _activeExport.CullingMask ?? uint.MaxValue;
            pipeline.SetSceneLights(_activeExport.SceneLights ?? CollectSceneLights(mask));
            pipeline.SyncWorld(_activeExport.World ?? GetRenderWorld(mask), 0, settings.RenderLod,
                new System.Numerics.Vector3(viewCamera.Position.X, viewCamera.Position.Y, viewCamera.Position.Z));
            if (!pipeline.IsAvailable)
            {
                if (pipeline.InitializationError is { } error) throw new InvalidOperationException(error);
                return;
            }
            if (_activeExport.ReceiverLayers is { } receiverLayers &&
                (_activeExport.ReceiverTable is null || _activeExport.ReceiverTable.Origin != pipeline.Workset.Origin))
                _activeExport.ReceiverTable = SurfaceReceiverLayerTable.Build(receiverLayers, pipeline.Workset.Origin);
            pipeline.SetSurfaceDeposits(_activeExport.SurfaceDeposits, _activeExport.ReceiverTable);

            size = _activeExport.Size;
            var scale = _activeExport.Scale;
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var frame = BuildFrameState(viewCamera, size, scale, width, height,
                pathMode: settings.PathTracingEnabled, outlines: false, sourcePipeline: pipeline, frameSettings: settings);
            frame = frame with { WeatherTime = _activeExport.WeatherTime, WaterTime = _activeExport.WaterTime };
            var submitted = settings.PathTracingEnabled
                ? pipeline.RenderPathView(settings, frame, width, height)
                : pipeline.RenderSceneView(settings, frame, width, height);
            if (!submitted)
            {
                SetStatus($"导出失败：{Path.GetFileName(_activeExport.Path)}");
                CompleteVideoFrame(_activeExport, false);
                _activeExport = null;
                return;
            }

            // 渐进太阳阴影在实时模式下也需要多帧，不能导出首帧的随机估计。
            var progressiveSunExport = !settings.PathTracingEnabled && settings.SunEnabled &&
                settings.SunShadowMethod == SunShadowMethod.Progressive && settings.ShadowSoftness > 0.0001F;
            var finished = settings.PathTracingEnabled
                ? pipeline.AccumulatedSamples >= settings.PathMaxAccumulatedSamples
                : !progressiveSunExport || pipeline.RealtimeConvergedFrames >= Math.Max(0, settings.SunAccumulationFrames - 1);
            if (!finished) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_activeExport.Path)!);
            var saved = _activeExport.Format == ExportImageFormat.Hdr
                ? pipeline.SaveDisplayHdr(_activeExport.Path)
                : pipeline.SaveDisplayPng(_activeExport.Path);
            if (saved)
                SetStatus($"图片已导出：{Path.GetFileName(_activeExport.Path)}（剩余队列 {_exportQueue.Count}）");
            else
                SetStatus($"导出失败：{Path.GetFileName(_activeExport.Path)}");
            CompleteVideoFrame(_activeExport, saved);
            _activeExport = null;
            if (_activeExport is null && _exportQueue.Count == 0)
            {
                // 批量任务全部结束后让渲染视图按自身相机重新收敛
                _accumulationSignature = string.Empty;
                _renderViewNeedsInitialRender = true;
            }
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("导出渲染任务失败", exception);
            SetStatus($"导出失败：{exception.Message}");
            if (_activeExport is not null) CompleteVideoFrame(_activeExport, false);
            _activeExport = null;
        }
    }

    private void DrawRenderViewOverlay(Vector2 origin, Vector2 size, ImDrawListPtr drawList)
    {
        var activeRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject());
        var buttonWidth = Math.Max(80F, Math.Max(ImGui.CalcTextSize("导出…").X, ImGui.CalcTextSize("信息").X) + 20F);
        var buttonHeight = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        DrawRenderSampleProgress(origin, activeRenderSettings);

        // 导出与信息按钮都贴右上角排列，左上角留给采样进度
        ImGui.SetCursorScreenPos(origin + new Vector2(size.X - buttonWidth * 2F - spacing - 12F, 10F));
        var canExport = _pipeline?.IsAvailable == true;
        ImGui.BeginDisabled(!canExport);
        if (SceneToolbarButton("导出…##RenderExport", false, buttonWidth, buttonHeight,
                "打开导出对话框：选择格式、比例与分辨率后再导出")) _showExportDialog = true;
        ImGui.EndDisabled();
        ImGui.SetCursorScreenPos(origin + new Vector2(Math.Max(12F + buttonWidth + spacing, size.X - buttonWidth - 12F), 10F));
        if (SceneToolbarButton("信息##RenderInformation", _showRenderStatistics, buttonWidth, buttonHeight,
                $"帧率、体素工作集、派发量和 CPU 更新耗时\n视场角 {activeRenderSettings.CameraFovDegrees:0}° · 视口 {size.X:0}×{size.Y:0}\n统计来自当前视图，不运行性能台架"))
            _showRenderStatistics = !_showRenderStatistics;
        if (_showRenderStatistics) DrawSceneStatistics(drawList, origin + new Vector2(0F, buttonHeight + 14F), renderView: true);
        DrawExportDialog();
    }

    /// <summary>
    /// 左上角显示路径追踪的采样进度，像 Blender 的 Sample 计数一样从 0 逐步累加到目标值
    /// </summary>
    private void DrawRenderSampleProgress(Vector2 origin, RenderSettings settings)
    {
        if (!settings.PathTracingEnabled)
        {
            DrawProgressText(origin, "实时渲染");
            return;
        }

        var target = Math.Max(1, settings.PathMaxAccumulatedSamples);
        var samples = Math.Clamp(_pipeline?.AccumulatedSamples ?? 0, 0, target);
        DrawProgressText(origin, samples >= target
            ? $"采样完成 {target} / {target}"
            : $"渲染中 {samples} / {target}");
    }

    private static void DrawProgressText(Vector2 origin, string text)
    {
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F + ImGui.GetStyle().FramePadding.Y));
        ImGui.TextDisabled(text);
    }

    private void ExportRenderPng()
    {
        if (_gpuRenderingDisabled || _vulkan is null || _project is null)
        {
            SetStatus("显卡渲染不可用，无法加入导出队列");
            return;
        }

        var projectPath = _project?.RootPath
            ?? AppPaths.DataDirectory;
        var fileName = $"渲染-{DateTime.Now:yyyyMMdd-HHmmss}-{++_exportSequence:000}.png";
        var path = Path.Combine(projectPath, "Assets", "Exports", fileName);
        var exportSettings = ResolveRenderSettings(GetActiveRenderCameraObject(),
            _project is null ? _editorSettings.ExportRenderSettings : _projectSettings.ExportRenderSettings);
        EnqueueExport(new ExportJob(path, exportSettings) { Camera = GetRenderViewCamera(), CullingMask = GetActiveRenderCullingMask() });
        SetStatus($"已加入导出队列：{fileName}");
    }

    /// <summary>
    /// 把当前渲染视图的 HDR 累积结果导出为 Radiance .hdr 文件加入导出队列
    /// </summary>
    private void ExportRenderHdr()
    {
        if (_gpuRenderingDisabled || _vulkan is null || _project is null)
        {
            SetStatus("显卡渲染不可用，无法加入导出队列");
            return;
        }

        var projectPath = _project?.RootPath ?? AppPaths.DataDirectory;
        var fileName = $"渲染-{DateTime.Now:yyyyMMdd-HHmmss}-{++_exportSequence:000}.hdr";
        var path = Path.Combine(projectPath, "Assets", "Exports", fileName);
        var exportSettings = ResolveRenderSettings(GetActiveRenderCameraObject(),
            _project is null ? _editorSettings.ExportRenderSettings : _projectSettings.ExportRenderSettings);
        exportSettings.PathTracingEnabled = true; // HDR 必须有线性累积缓冲。
        EnqueueExport(new ExportJob(path, exportSettings, ExportImageFormat.Hdr) { Camera = GetRenderViewCamera(), CullingMask = GetActiveRenderCullingMask() });
        SetStatus($"已加入导出队列：{fileName}");
    }

    /// <summary>
    /// 批量出图：为场景中每个相机对象各排一个导出任务，
    /// 所有相机统一使用项目导出配置，并按各自图层遮罩重组渲染世界
    /// </summary>
    private void ExportAllCamerasPng()
    {
        if (_gpuRenderingDisabled || _vulkan is null || _project is null)
        {
            SetStatus("显卡渲染不可用，无法批量出图");
            return;
        }

        if (_project is not { } project)
        {
            return;
        }

        var cameras = GetRenderCameraObjects();
        if (cameras.Count == 0)
        {
            ExportRenderPng();
            return;
        }

        var baseSettings = _projectSettings.ExportRenderSettings;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var cameraObject in cameras)
        {
            var component = cameraObject.Components.First(SceneComponentTypes.IsCamera);
            var cameraData = SceneComponentTypes.ReadCamera(component);
            var settings = ResolveRenderSettings(cameraObject, baseSettings);
            var fileName = $"渲染-{SanitizeFileName(cameraObject.Name)}-{stamp}-{++_exportSequence:000}.png";
            EnqueueExport(new ExportJob(Path.Combine(project.AssetsPath, "Exports", fileName), settings)
            {
                Camera = ToSceneCamera(cameraObject),
                CameraName = cameraObject.Name,
                CullingMask = cameraData.CullingMask,
            });
        }

        SetStatus($"已批量入队 {cameras.Count} 个相机的出图任务");
    }

    internal enum ExportImageFormat { Png, Hdr }

    private VulkanVoxelRenderPipeline? _exportPipeline;
    private VoxelWorld? _exportSnapshotSource;
    private VoxelWorld? _exportSnapshot;
    private int _exportSnapshotVersion = -1;

    private void EnqueueExport(ExportJob job)
    {
        //导出任务必须持有独立快照，避免后续编辑改变排队中的渲染结果
        job.World = GetRenderWorld(job.CullingMask ?? uint.MaxValue);
        if (ReferenceEquals(job.World, _world))
        {
            if (!ReferenceEquals(_exportSnapshotSource, _world) || _exportSnapshotVersion != _world.ContentVersion)
            {
                var snapshot = new VoxelWorld();
                foreach (var item in _world.EnumerateOccupied()) snapshot.Set(item.Coordinate, item.Value);
                _exportSnapshot = snapshot;
                _exportSnapshotSource = _world;
                _exportSnapshotVersion = _world.ContentVersion;
            }
            job.World = _exportSnapshot;
        }
        var (exportSize, exportScale) = ResolveExportCanvas();
        job.Size = exportSize;
        job.Scale = exportScale;
        job.Materials = MaterialAsset.PackTable(_runtimeMaterials);
        job.SurfaceDeposits = CollectSurfaceDeposits(job.CullingMask ?? uint.MaxValue);
        job.ReceiverLayers = NeedsReceiverLayers(job.SurfaceDeposits) && _sceneDocument is not null
            ? _objectVoxels.CaptureReceiverLayers(_sceneDocument, job.CullingMask ?? uint.MaxValue, false) : null;
        job.WeatherTime = TimelineWeatherTime;
        job.WaterTime = _waterAnimationTime;
        job.CameraName ??= GetActiveRenderCameraObject()?.Name ?? "场景视角";
        _exportQueue.Enqueue(job);
    }

    private sealed class ExportJob
    {
        internal ExportJob(string path, RenderSettings settings, ExportImageFormat format = ExportImageFormat.Png)
        { Path = path; Settings = settings; Format = format; }
        internal string Path { get; }
        internal RenderSettings Settings { get; }
        internal ExportImageFormat Format { get; }
        internal VoxelWorld? World { get; set; }
        internal Vector2 Size { get; set; }
        internal float Scale { get; set; }
        internal float[]? Materials { get; set; }
        internal string? CameraName { get; set; }
        internal string? TimelineAsset { get; init; }
        internal VideoExportBatch? Video { get; init; }
        internal int TimelineFrame { get; init; }
        internal bool TimelineApplied { get; set; }
        internal float WeatherTime { get; set; }
        internal float WaterTime { get; set; }
        internal IReadOnlyList<SceneLight>? SceneLights { get; set; }
        internal float[] SurfaceDeposits { get; set; } = [];
        internal Dictionary<VoxelCoord64, byte>? ReceiverLayers { get; set; }
        internal SurfaceReceiverLayerTable? ReceiverTable { get; set; }

        /// <summary>任务专属相机；为空表示沿用渲染视图当前相机</summary>
        internal SceneCamera? Camera { get; set; }

        /// <summary>任务专属图层遮罩；有值时按该遮罩重组渲染世界</summary>
        internal uint? CullingMask { get; init; }
    }

    private void DrawInspector()
    {
        if (!_showInspector)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(display.X - layout.RightWidth, WorkspaceTop + 1F), new Vector2(layout.RightWidth, layout.MainHeight));
        if (_requestedInspectorTab == 0) ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("检查器##检查器窗口", PanelWindowFlags))
        {
        }
        else
        {
            DrawPanelWindowMenu(true);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8F, 8F));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(6F, MathF.Max(5F, ImGui.GetFontSize() * .2F)));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1F);
            ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.PanelHeader));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToColor(EditorPalette.AccentSoft));
            DrawInspectorProperties();
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(3);
        }
        ImGui.End();
    }

    private void DrawExtraInspectors()
    {
        foreach (var inspector in _extraInspectors.ToArray())
        {
            ImGui.SetNextWindowSize(new Vector2(420F, 600F), ImGuiCond.FirstUseEver);
            if (inspector.DockId != 0) ImGui.SetNextWindowDockID(inspector.DockId, ImGuiCond.FirstUseEver);
            var mainLock = _inspectorLockedObjectId;
            _inspectorLockedObjectId = inspector.Locked;
            if (ImGui.Begin($"{inspector.Kind} {inspector.Id}##ExtraInspector{inspector.Id}", ref inspector.Open, ImGuiWindowFlags.NoCollapse))
            {
                DrawPanelWindowMenu();
                DrawExtraWindowContents(inspector.Kind);
            }
            ImGui.End();
            inspector.Locked = _inspectorLockedObjectId;
            _inspectorLockedObjectId = mainLock;
        }
        _extraInspectors.RemoveAll(item => !item.Open);
    }

    private void DrawExtraWindowContents(string kind)
    {
        switch (kind)
        {
            case "检查器": DrawInspectorProperties(); break;
            case "层级": DrawHierarchyContent(); break;
            case "场景": _centerTab = 0; DrawSceneView(); break;
            case "渲染": _centerTab = 1; DrawRenderView(); break;
            case "调色盘": DrawPaletteContents(); break;
            case "项目资产":
                _assetBrowser.Draw(_project, SetStatus, HandleAssetChanged, OpenVoxelAssetStage, InstantiatePrefabAsset,
                    ExportVoxAsset, path => OpenGeneratorAsset(path), ExportQubicleAsset,
                    path => FindOpenAssetReferenceNames(path).Count,
                    FindOpenAssetReferenceNames, OpenPrefabAssetStage, AddVoxelAssetReferenceToScene);
                break;
            case "渲染配置": DrawConfigurationPanel(); break;
            case "生成器": DrawGeneratorToolbar(); ImGui.Separator(); DrawGeneratorEditor(); ImGui.Separator(); DrawGeneratorParameters(); DrawGeneratorMessage(); break;
            case "体素度量": DrawExtraMeasurements(); break;
            case "性能分析器": DrawExtraPerformance(); break;
        }
    }

    private void DrawPanelWindowMenu(bool inspectorPanel = false)
    {
        var dockId = ImGui.GetWindowDockID();
        if (dockId == 0) return;
        var cursor = ImGui.GetCursorScreenPos();
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var height = ImGui.GetFrameHeight();
        var buttonMin = new Vector2(position.X + size.X - height - ImGui.GetStyle().WindowPadding.X - 2F, position.Y);
        ImGui.PushClipRect(position, position + size, false);
        ImGui.SetCursorScreenPos(buttonMin);
        ImGui.PushID($"PanelMenu{dockId}");
        if (inspectorPanel && _openInspectorPanelMenuRequested)
        {
            ImGui.OpenPopup("##添加窗口");
            _openInspectorPanelMenuRequested = false;
        }
        if (ImGui.InvisibleButton("##窗口菜单", new Vector2(height))) ImGui.OpenPopup("##添加窗口");
        var draw = ImGui.GetWindowDrawList();
        var color = ToPackedColor(EditorPalette.Text);
        var center = buttonMin + new Vector2(height * .5F);
        for (var i = -1; i <= 1; i++) draw.AddCircleFilled(center + new Vector2(0F, i * 4F), 1.3F, color);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加窗口标签");
        ImGui.SetNextWindowPos(buttonMin + new Vector2(height, height), ImGuiCond.Appearing, new Vector2(1F, 0F));
        if (ImGui.BeginPopup("##添加窗口"))
        {
            if (ImGui.BeginMenu("常规"))
            {
                foreach (var name in new[] { "场景", "层级", "检查器", "调色盘", "项目资产" })
                    if (ImGui.MenuItem(name)) _extraInspectors.Add(new ExtraInspector { Id = ++_nextInspectorId, DockId = dockId, Kind = name });
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("渲染"))
            {
                foreach (var name in new[] { "渲染", "渲染配置" })
                    if (ImGui.MenuItem(name)) _extraInspectors.Add(new ExtraInspector { Id = ++_nextInspectorId, DockId = dockId, Kind = name });
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("工具"))
            {
                foreach (var name in new[] { "生成器", "体素度量", "性能分析器" })
                    if (ImGui.MenuItem(name)) _extraInspectors.Add(new ExtraInspector { Id = ++_nextInspectorId, DockId = dockId, Kind = name });
                ImGui.EndMenu();
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(cursor);
    }

    private void DrawExportQueueWindow(Vector2 display)
    {
        var open = true;
        ImGui.SetNextWindowPos(new Vector2(Math.Max(8F, display.X - 440F),
            Math.Max(8F, display.Y - StatusHeight - 290F)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(420F, 270F), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("导出队列##ExportQueueWindow", ref open, flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled($"共 {_exportQueue.Count + (_activeExport is null ? 0 : 1) + _videoEncodings.Count} 个任务");
        ImGui.Separator();
        foreach (var encoding in _videoEncodings)
        {
            ImGui.PushID(encoding.Batch.OutputPath);
            ImGui.TextWrapped($"正在编码：{encoding.Batch.OutputPath}");
            ImGui.SameLine();
            ImGui.BeginDisabled(encoding.Cancellation.IsCancellationRequested);
            if (ImGui.SmallButton("取消编码")) encoding.Cancellation.Cancel();
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        if (_activeExport is { } active)
        {
            ImGui.TextUnformatted("正在导出");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消##取消当前导出")) _cancelActiveExportRequested = true;
            ImGui.TextWrapped($"相机：{active.CameraName} · {active.Format}\n{active.Path}");
            ImGui.ProgressBar(ExportProgress(active), new Vector2(-1F, 0F),
                $"{ExportProgress(active) * 100F:0}%");
        }
        else
        {
            ImGui.TextDisabled("当前没有正在执行的任务");
        }

        ImGui.Separator();
        ImGui.TextDisabled($"等待中的任务（{_exportQueue.Count}）");
        foreach (var job in _exportQueue.ToArray())
        {
            ImGui.PushID(job.Path);
            ImGui.TextWrapped($"相机：{job.CameraName} · {job.Format}\n{job.Path}");
            ImGui.SameLine();
            ImGui.ProgressBar(0F, new Vector2(90F, 0F), "等待");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消"))
            {
                CancelWaitingExportJob(job);
                //取消视频帧会中止整批，队列已重建，这里只再确保当前项被移除
                _exportQueue = new Queue<ExportJob>(_exportQueue.Where(item => !ReferenceEquals(item, job)));
            }
            ImGui.PopID();
        }
        ImGui.End();
        if (!open) _exportQueueExpanded = false;
    }

    private float ExportProgress(ExportJob job)
    {
        if (_exportPipeline is null) return 0F;
        if (!job.Settings.PathTracingEnabled)
        {
            var settings = job.Settings;
            if (!settings.SunEnabled || settings.SunShadowMethod != SunShadowMethod.Progressive ||
                settings.ShadowSoftness <= 0.0001F) return 1F;
            return Math.Clamp((float)(_exportPipeline.RealtimeConvergedFrames + 1) /
                Math.Max(1, settings.SunAccumulationFrames), 0F, 1F);
        }
        if (job.Settings.PathMaxAccumulatedSamples <= 0) return 0F;
        return Math.Clamp((float)_exportPipeline.AccumulatedSamples / job.Settings.PathMaxAccumulatedSamples, 0F, 1F);
    }

    /// <summary>
    /// 绘制检查器的对象属性内容
    /// </summary>
    private void DrawInspectorProperties()
    {
        if (_inspectorLockedObjectId is null && _selectedSceneObjectId is null &&
            (TryDrawSelectedPngInspector() || TryDrawSelectedMaterialInspector() || TryDrawSelectedEnvironmentInspector() ||
             TryDrawSelectedAssetInspector()))
        {
            return;
        }

        var inspectedObjectId = _inspectorLockedObjectId ?? _selectedSceneObjectId;
        var selectedObject = inspectedObjectId is { } selectedObjectId
            ? _sceneDocument?.Objects.FirstOrDefault(item => item.Id == selectedObjectId)
            : null;
        if (selectedObject is null)
        {
            ImGui.TextDisabled("未选择对象");
            ImGui.TextDisabled("在层级或场景中选择一个对象");
            return;
        }

        // 检查器内容必须由外层统一滚动。组件自身不能用固定高度裁剪，
        // 否则相机的图层等字段会在展开状态下直接消失。
        // 只用纵向滚动：检查器是纵向表单，横向滚动条会因为个别控件差一两像素就常驻，
        // 并反过来把文本换行宽度撑大，造成右侧内容被裁掉
        if (!ImGui.BeginChild("##InspectorScroll", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }




        var hasModelCameraConflict = selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel) &&
                                     selectedObject.Components.Any(SceneComponentTypes.IsCamera);
        var hasGeneratedModelConflict = selectedObject.Components.Any(SceneComponentTypes.IsGeneratedVoxelComponent) &&
                                        selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel);
        if (hasModelCameraConflict)
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), "警告：模型组件与相机组件不能同时挂载");
        else if (hasGeneratedModelConflict)
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), "警告：模型组件与程序化生成组件不能同时挂载");
        else if (!string.IsNullOrWhiteSpace(_componentWarning))
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), $"警告：{_componentWarning}");
        var objectRowHeight = ImGui.GetFrameHeight();
        var objectRowGap = ImGui.GetStyle().ItemSpacing.Y;
        const float objectPadding = 7F;
        var modelObject = selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel);
        var objectHeaderHeight = objectRowHeight + objectPadding * 2F;
        var objectIconSize = objectRowHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ToColor(EditorPalette.PanelHeader));
        if (ImGui.BeginChild("##InspectorObjectHeader", new Vector2(-1F, objectHeaderHeight),
                ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorPos(new Vector2(8F, objectPadding));
            var visible = selectedObject.Visible;
            var visibilityClicked = DrawHierarchyIconButton("##InspectorObjectVisibility", "对象可见", "对象隐藏",
                visible, DrawVisibilityIcon, objectIconSize);
            if (visibilityClicked)
            {
                selectedObject.Visible = !visible;
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
                _accumulationSignature = string.Empty;
            }
            ImGui.SameLine();
            var objectName = selectedObject.Name;
            var headerAvailable = ImGui.GetContentRegionAvail().X;
            var layerWidth = Math.Max(ImGui.GetFontSize() * 5F, headerAvailable * 0.34F);
            var layerLabelWidth = ImGui.CalcTextSize("图层").X;
            ImGui.SetNextItemWidth(Math.Max(ImGui.GetFontSize() * 3F, headerAvailable - layerWidth - layerLabelWidth - (objectIconSize + 30F)));
            if (ImGui.InputText("##InspectorObjectName", ref objectName, 128) &&
                !string.IsNullOrWhiteSpace(objectName) && objectName != selectedObject.Name)
            {
                selectedObject.Name = objectName.Trim();
                MarkSceneDocumentDirty();
            }
            ImGui.SameLine(0F, 4F);
            var inspectorLocked = _inspectorLockedObjectId is not null;
            if (DrawHierarchyIconButton("##InspectorObjectSelectable", "锁定检查器", "解锁检查器",
                    !inspectorLocked, DrawLockIcon, objectIconSize))
                _inspectorLockedObjectId = inspectorLocked ? null : selectedObject.Id;
            ImGui.SameLine(0F, 6F);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("图层");
            ImGui.SameLine();
            var layerNames = _sceneDocument?.LayerNames ?? ["默认"];
            var currentLayer = Math.Clamp(selectedObject.LayerIndex, 0, layerNames.Count - 1);
            ImGui.SetNextItemWidth(Math.Max(ImGui.GetFontSize() * 4F, ImGui.GetContentRegionAvail().X - 8F));
            if (ImGui.BeginCombo("##InspectorObjectLayer", layerNames[currentLayer]))
            {
                for (var layerIndex = 0; layerIndex < layerNames.Count; layerIndex++)
                {
                    if (ImGui.Selectable($"{layerIndex + 1}. {layerNames[layerIndex]}", layerIndex == currentLayer))
                    {
                        selectedObject.LayerIndex = layerIndex;
                        MarkSceneDocumentDirty();
                        _pipeline?.ResetAccumulation();
                        _accumulationSignature = string.Empty;
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("对象所属图层；渲染窗口中相机的图层遮罩决定本对象是否被该相机渲染");

        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0F, 3F));
        DrawPrefabInspectorSection(selectedObject);

        var removeComponentIndex = -1;
        if (_inspectorExpansionObjectId != selectedObject.Id)
        {
            _inspectorExpansionObjectId = selectedObject.Id;
            _expandedInspectorComponents.Clear();
            for (var componentIndex = 0; componentIndex < selectedObject.Components.Count; componentIndex++)
            {
                _expandedInspectorComponents.Add(GetInspectorComponentExpansionKey(
                    selectedObject.Components[componentIndex], componentIndex));
            }
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6F, 3F));
        for (var componentIndex = 0; componentIndex < selectedObject.Components.Count; componentIndex++)
        {
            var component = selectedObject.Components[componentIndex];
            var definition = SceneComponentTypes.BuiltInDefinitions.FirstOrDefault(item => item.TypeId == component.TypeId);
            var pluginDefinition = _pluginRuntime?.Components.FirstOrDefault(item => item.Id == component.TypeId);
            var label = definition is null
                ? pluginDefinition is null
                    ? $"{SceneComponentTypes.GetDisplayName(component)} · 缺失插件"
                    : pluginDefinition.Name
                : definition.DisplayName;
            ImGui.PushID($"InspectorComponent{componentIndex}");
            var headerMin = ImGui.GetCursorScreenPos();
            var totalHeaderWidth = ImGui.GetContentRegionAvail().X;
            var isTransformComponent = SceneComponentTypes.IsTransform(component);
            var controlHeight = ImGui.GetFrameHeight();
            var menuWidth = Math.Max(28F, ImGui.GetFontSize() * 1.1F);
            var actionWidth = menuWidth + (isTransformComponent ? 8F : controlHeight + 12F);
            var headerWidth = Math.Max(40F, totalHeaderWidth - actionWidth);
            var headerHeight = controlHeight + 8F;
            var headerClicked = ImGui.InvisibleButton("##InspectorComponentHeader", new Vector2(headerWidth, headerHeight));
            var headerMax = headerMin + new Vector2(totalHeaderWidth, headerHeight);
            var expansionKey = GetInspectorComponentExpansionKey(component, componentIndex);
            var expanded = _expandedInspectorComponents.Contains(expansionKey);
            if (headerClicked)
            {
                if (expanded) _expandedInspectorComponents.Remove(expansionKey);
                else _expandedInspectorComponents.Add(expansionKey);
                expanded = !expanded;
            }
            var draw = ImGui.GetWindowDrawList();
            var headerColor = ImGui.IsItemHovered() ? EditorPalette.AccentSoft : EditorPalette.PanelHeader;
            draw.AddRectFilled(headerMin, headerMax, ToPackedColor(headerColor));
            draw.AddLine(new Vector2(headerMin.X, headerMax.Y), headerMax,
                ToPackedColor(EditorPalette.Border));
            var triangleCenter = new Vector2(headerMin.X + 12F, headerMin.Y + headerHeight / 2F);
            if (expanded)
                draw.AddTriangleFilled(triangleCenter + new Vector2(-4F, -2F), triangleCenter + new Vector2(4F, -2F),
                    triangleCenter + new Vector2(0F, 4F), ToPackedColor(EditorPalette.Text));
            else
                draw.AddTriangleFilled(triangleCenter + new Vector2(-2F, -4F), triangleCenter + new Vector2(-2F, 4F),
                    triangleCenter + new Vector2(4F, 0F), ToPackedColor(EditorPalette.Text));
            draw.AddText(headerMin + new Vector2(25F,
                (headerHeight - ImGui.GetTextLineHeight()) * .5F), ToPackedColor(EditorPalette.Text), label);

            var resetComponent = false;
            ImGui.SetCursorScreenPos(new Vector2(headerMax.X - actionWidth,
                headerMin.Y + (headerHeight - controlHeight) * .5F));
            var enabledClicked = false;
            if (!isTransformComponent)
            {
                var enabled = component.Enabled;
                enabledClicked = ImGui.Checkbox("##InspectorComponentEnabled", ref enabled);
                if (enabledClicked && component.Enabled != enabled)
                {
                    component.Enabled = enabled;
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                    SetStatus($"{selectedObject.Name} 的{SceneComponentTypes.GetDisplayName(component)}组件已{(enabled ? "启用" : "停用")}");
                }
            }
            if (!isTransformComponent) ImGui.SameLine(0F, 4F);
            var menuMin = ImGui.GetCursorScreenPos();
            var menuClicked = ImGui.InvisibleButton("##InspectorComponentMenu", new Vector2(menuWidth, controlHeight));
            var menuMax = ImGui.GetItemRectMax();
            var menuDraw = ImGui.GetWindowDrawList();
            if (ImGui.IsItemHovered())
                menuDraw.AddRectFilled(menuMin, menuMax, ToPackedColor(EditorPalette.AccentSoft), 3F);
            var menuCenter = (menuMin + menuMax) * 0.5F;
            var menuDotColor = ToPackedColor(EditorPalette.Text);
            menuDraw.AddCircleFilled(menuCenter + new Vector2(0F, -5F), 1.6F, menuDotColor, 8);
            menuDraw.AddCircleFilled(menuCenter, 1.6F, menuDotColor, 8);
            menuDraw.AddCircleFilled(menuCenter + new Vector2(0F, 5F), 1.6F, menuDotColor, 8);
            if (menuClicked) ImGui.OpenPopup("##InspectorComponentMenuPopup");
            if (ImGui.BeginPopup("##InspectorComponentMenuPopup"))
            {
                var canResetComponent = definition is not null || pluginDefinition is not null;
                if (ImGui.MenuItem("恢复默认", null, false, canResetComponent)) resetComponent = true;
                if (!canResetComponent)
                    ImGui.TextDisabled("插件未启用时无法恢复默认");
                ImGui.Separator();
                var canMoveUp = !isTransformComponent && componentIndex > 1;
                var canMoveDown = !isTransformComponent && componentIndex < selectedObject.Components.Count - 1;
                if (ImGui.MenuItem("上移组件", null, false, canMoveUp))
                {
                    (selectedObject.Components[componentIndex - 1], selectedObject.Components[componentIndex]) =
                        (selectedObject.Components[componentIndex], selectedObject.Components[componentIndex - 1]);
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                    SetStatus($"已上移{SceneComponentTypes.GetDisplayName(component)}组件");
                }
                if (ImGui.MenuItem("下移组件", null, false, canMoveDown))
                {
                    (selectedObject.Components[componentIndex + 1], selectedObject.Components[componentIndex]) =
                        (selectedObject.Components[componentIndex], selectedObject.Components[componentIndex + 1]);
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                    SetStatus($"已下移{SceneComponentTypes.GetDisplayName(component)}组件");
                }
                ImGui.Separator();
                ImGui.BeginDisabled(isTransformComponent);
                if (ImGui.MenuItem("移除组件")) removeComponentIndex = componentIndex;
                ImGui.EndDisabled();
                if (isTransformComponent) ImGui.TextDisabled("变换组件不能移除");
                ImGui.EndPopup();
            }
            ImGui.SetCursorScreenPos(new Vector2(headerMin.X, headerMax.Y));
            if (resetComponent) ResetInspectorComponent(selectedObject, component);
            if (expanded)
            {
                ImGui.Dummy(new Vector2(0F, 4F));
                ImGui.Indent(12F);
                DrawComponentSummary(selectedObject, component);
                ImGui.Unindent(12F);
                ImGui.Dummy(new Vector2(0F, 4F));
            }
            var cardBottom = ImGui.GetCursorScreenPos().Y;
            draw.AddRect(headerMin, new Vector2(headerMax.X, cardBottom), ToPackedColor(EditorPalette.Border), 2F);
            ImGui.Dummy(new Vector2(0F, Math.Max(4F, ImGui.GetStyle().ItemSpacing.Y)));
            ImGui.PopID();
        }
        ImGui.PopStyleVar();

        if (removeComponentIndex >= 0)
        {
            var removed = selectedObject.Components[removeComponentIndex];
            selectedObject.Components.RemoveAt(removeComponentIndex);
            _componentWarning = null;
            MarkSceneDocumentDirty();
            // 程序化组件删除后生成体素没有归属，一并清掉，避免留下看不见的独立世界
            if (SceneComponentTypes.IsGeneratedVoxelComponent(removed)) RemoveGeneratedVoxelWorld(selectedObject);
            SetStatus($"已从“{selectedObject.Name}”移除{SceneComponentTypes.GetDisplayName(removed)}组件");
        }

        // 实例化会替换组件列表，放到组件循环结束后执行，避免遍历中改动集合
        if (_pendingTreeInstantiate is { } instantiateTarget)
        {
            _pendingTreeInstantiate = null;
            if (selectedObject.Components.Contains(instantiateTarget))
                InstantiateGeneratedVoxels(selectedObject, instantiateTarget, "体素模型");
        }

        ImGui.Dummy(new Vector2(0F, 6F));
        var addWidth = Math.Min(220F, ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0F, (ImGui.GetContentRegionAvail().X - addWidth) / 2F));
        ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.PanelHeader));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToColor(EditorPalette.AccentSoft));
        var addHeight = Math.Max(ImGui.GetFrameHeight() + 8F, ImGui.GetTextLineHeight() + 12F);
        if (ImGui.Button("添加组件", new Vector2(addWidth, addHeight)))
        {
            _componentSearch = string.Empty;
            ImGui.OpenPopup("##InspectorAddComponentPopup");
        }
        ImGui.PopStyleColor(2);
        if (ImGui.BeginPopup("##InspectorAddComponentPopup"))
            {
                ImGui.SetNextItemWidth(Math.Max(ImGui.GetFontSize() * 12F, ImGui.GetContentRegionAvail().X));
                ImGui.InputTextWithHint("##ComponentSearch", "搜索组件或分类", ref _componentSearch, 128);
                ImGui.Separator();
                var query = _componentSearch.Trim();
                var resultCount = 0;
                string? previousCategory = null;
                foreach (var definition in SceneComponentTypes.BuiltInDefinitions)
                {
                    if (definition.TypeId == SceneComponentTypes.Transform) continue;
                    if (query.Length > 0 && !definition.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !definition.Category.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !definition.TypeId.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    resultCount++;
                    if (definition.Category != previousCategory)
                    {
                        if (previousCategory is not null) ImGui.Separator();
                        ImGui.TextDisabled(definition.Category);
                        previousCategory = definition.Category;
                    }

                    var alreadyPresent = selectedObject.Components.Any(item => item.TypeId == definition.TypeId);
                    var hasModel = selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel);
                    var hasCurveRenderer = selectedObject.Components.Any(SceneComponentTypes.IsCurveRenderer);
                    var hasTreeGenerator = selectedObject.Components.Any(SceneComponentTypes.IsTreeGenerator);
                    var reason = alreadyPresent ? "同一对象不能重复添加此组件" :
                        definition.TypeId == SceneComponentTypes.VoxelModel && selectedObject.Components.Any(SceneComponentTypes.IsCamera)
                            ? "模型组件与相机组件不能同时挂载" :
                        definition.TypeId == SceneComponentTypes.VoxelModel && (hasCurveRenderer || hasTreeGenerator)
                            ? "程序化生成组件已经负责产生体素，不能再挂模型组件" :
                        definition.TypeId == SceneComponentTypes.Camera && hasModel
                            ? "相机组件与模型组件不能同时挂载" :
                        definition.TypeId == SceneComponentTypes.CurveRenderer && (hasModel || hasTreeGenerator)
                            ? "曲线渲染器会自己生成体素，不能和模型或树生成器同时挂载" :
                        definition.TypeId == SceneComponentTypes.TreeGenerator && (hasModel || hasCurveRenderer)
                            ? "树生成器会自己生成体素，不能和模型或曲线渲染器同时挂载" : null;
                    ImGui.BeginDisabled(reason is not null);
                    var addSelected = ImGui.Selectable(definition.DisplayName);
                    ImGui.EndDisabled();
                    if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(reason);
                    if (!addSelected) continue;
                    _componentWarning = null;
                    if (definition.TypeId is SceneComponentTypes.VoxelModel or SceneComponentTypes.CurveRenderer
                        or SceneComponentTypes.TreeGenerator)
                    {
                        selectedObject.Transform.SetGrid(selectedObject.Transform.ToGrid());
                        var transform = selectedObject.Components.FirstOrDefault(SceneComponentTypes.IsTransform);
                        if (transform is not null) transform.Data = JsonSerializer.SerializeToElement(new { kind = "Grid" });
                    }

                    var addedComponent = definition.TypeId switch
                    {
                        SceneComponentTypes.Camera => SceneComponentTypes.CreateCamera(),
                        SceneComponentTypes.Light => SceneComponentTypes.CreateLight(LightComponentData.Default),
                        SceneComponentTypes.ProbeVolume => SceneComponentTypes.CreateProbeVolume(ProbeVolumeComponentData.Default),
                        SceneComponentTypes.PostProcessOverride => SceneComponentTypes.CreatePostProcessOverride(new PostProcessOverrideComponentData()),
                        SceneComponentTypes.WindField => SceneComponentTypes.CreateWindField(new WindFieldComponentData()),
                        SceneComponentTypes.WindReceiver => SceneComponentTypes.CreateWindReceiver(new WindReceiverComponentData()),
                        SceneComponentTypes.PuddleDecal => SceneComponentTypes.CreatePuddleDecal(),
                        SceneComponentTypes.SnowDecal => SceneComponentTypes.CreateSnowDecal(),
                        SceneComponentTypes.Animation => SceneComponentTypes.CreateAnimation(new AnimationComponentData()),
                        SceneComponentTypes.VoxelModel => SceneComponentTypes.CreateVoxelModel(string.Empty, 0, "手动添加", "模型"),
                        SceneComponentTypes.VoxelSelection => CreateVoxelSelectionForObject(selectedObject),
                        SceneComponentTypes.CurveRenderer => SceneComponentTypes.CreateCurveRenderer(
                            CurveRendererComponentData.CreateDefault()),
                        SceneComponentTypes.TreeGenerator => SceneComponentTypes.CreateTreeGenerator(
                            TreeGeneratorComponentData.Default),
                        _ => SceneComponentTypes.CreateTransform(selectedObject.Transform),
                    };
                    selectedObject.Components.Add(addedComponent);
                    if (SceneComponentTypes.IsCurveRenderer(addedComponent))
                        InitializeCurveRendererObject(selectedObject, addedComponent);
                    else if (SceneComponentTypes.IsTreeGenerator(addedComponent))
                        InitializeTreeGeneratorObject(selectedObject, addedComponent);
                    MarkSceneDocumentDirty();
                }

                var pluginHeader = false;
                foreach (var definition in _pluginRuntime?.Components ?? [])
                {
                    if (query.Length > 0 && !definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !definition.Category.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !definition.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    resultCount++;
                    if (!pluginHeader)
                    {
                        if (previousCategory is not null) ImGui.Separator();
                        ImGui.TextDisabled("插件组件");
                        pluginHeader = true;
                    }
                    var alreadyPresent = selectedObject.Components.Any(item => item.TypeId == definition.Id);
                    ImGui.BeginDisabled(alreadyPresent);
                    var addSelected = ImGui.Selectable($"{definition.Name} · {definition.Category}##{definition.Id}");
                    ImGui.EndDisabled();
                    if (alreadyPresent && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("同一对象不能重复添加此组件");
                    if (!addSelected) continue;
                    var defaultData = definition.DefaultData.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? JsonSerializer.SerializeToElement(new { })
                        : definition.DefaultData.Clone();
                    selectedObject.Components.Add(new ComponentRecord
                    {
                        TypeId = definition.Id,
                        SchemaVersion = 1,
                        OwnerPluginId = definition.PluginId,
                        Data = defaultData,
                    });
                    MarkSceneDocumentDirty();
                }
                if (resultCount == 0) ImGui.TextDisabled("没有匹配的组件");
                ImGui.EndPopup();
            }

        ImGui.EndChild();
    }

    private static string GetInspectorComponentExpansionKey(ComponentRecord component, int componentIndex) =>
        $"{componentIndex}:{component.TypeId}";

    private bool TryDrawSelectedPngInspector()
    {
        try
        {
            var relativePath = _assetBrowser.SelectedAssetPath;
            if (_project is null || !relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
            var path = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) return false;

            if (!string.Equals(_assetPreviewPath, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_assetPreviewTextureId != 0) _imguiRenderer?.UnregisterTexture(_assetPreviewTextureId);
                _assetPreviewTextureId = 0;
                _assetPreviewPath = path;
                if (AppBranding.TryLoadPngPixels(path, out var pixels, out _assetPreviewWidth, out _assetPreviewHeight) && _imguiRenderer is not null)
                {
                    _assetPreviewTextureId = _imguiRenderer.RegisterTexture(pixels, _assetPreviewWidth, _assetPreviewHeight);
                }
            }

            ImGui.Text("PNG 图像");
            ImGui.Separator();
            ImGui.TextWrapped(Path.GetFileName(path));
            var info = new FileInfo(path);
            ImGui.TextDisabled($"尺寸  {_assetPreviewWidth} × {_assetPreviewHeight}");
            ImGui.TextDisabled($"大小  {info.Length / 1024F:0.0} KB");
            ImGui.TextDisabled("格式  PNG");
            ImGui.Spacing();
            if (_assetPreviewTextureId != 0)
            {
                var available = ImGui.GetContentRegionAvail().X;
                var width = Math.Min(available, Math.Max(1F, _assetPreviewWidth));
                var height = width * _assetPreviewHeight / Math.Max(1F, _assetPreviewWidth);
                ImGui.Image(_assetPreviewTextureId, new Vector2(width, height));
            }
            else
            {
                ImGui.TextDisabled("无法读取此 PNG 图像");
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _assetPreviewPath = string.Empty;
            _assetPreviewTextureId = 0;
            SetStatus($"PNG 预览失败：{exception.Message}");
            ImGui.TextDisabled("无法读取此 PNG 图像");
            return true;
        }
    }

    private void ResetInspectorComponent(SceneObject sceneObject, ComponentRecord component)
    {
        if (SceneComponentTypes.IsTransform(component))
        {
            if (sceneObject.Transform.Mode == TransformMode.Grid)
                sceneObject.Transform.SetGrid(GridTransform.Identity);
            else
                sceneObject.Transform.SetFree(FreeTransform.Identity);
            UpdateTransformMarker(sceneObject);
        }
        else if (SceneComponentTypes.IsCamera(component))
        {
            SceneComponentTypes.WriteCamera(component, CameraComponentData.Default);
        }
        else if (SceneComponentTypes.IsLight(component))
        {
            SceneComponentTypes.WriteLight(component, LightComponentData.Default);
        }
        else if (SceneComponentTypes.IsPostProcessOverride(component))
        {
            SceneComponentTypes.WritePostProcessOverride(component, new PostProcessOverrideComponentData());
        }
        else if (SceneComponentTypes.IsWindField(component))
        {
            SceneComponentTypes.WriteWindField(component, new WindFieldComponentData());
        }
        else if (SceneComponentTypes.IsWindReceiver(component))
        {
            SceneComponentTypes.WriteWindReceiver(component, new WindReceiverComponentData());
        }
        else if (SceneComponentTypes.IsSurfaceDeposit(component))
        {
            SceneComponentTypes.WriteSurfaceDeposit(component, SceneComponentTypes.IsSnowDecal(component)
                ? SurfaceDepositComponentData.CreateSnow() : SurfaceDepositComponentData.CreatePuddle());
        }
        else if (SceneComponentTypes.IsProbeVolume(component))
        {
            SceneComponentTypes.WriteProbeVolume(component, ProbeVolumeComponentData.Default);
        }
        else if (SceneComponentTypes.IsAnimation(component))
        {
            SceneComponentTypes.WriteAnimation(component, new AnimationComponentData());
        }
        else if (SceneComponentTypes.IsVoxelModel(component))
        {
            SceneComponentTypes.WriteVoxelModel(component, VoxelModelComponentData.Default);
        }
        else if (SceneComponentTypes.IsVoxelSelection(component))
        {
            SceneComponentTypes.WriteVoxelSelection(component, VoxelSelectionComponentData.Default);
            _voxelSelectionCells = [];
            _voxelSelectionObjectId = StableId.Empty;
            _voxelSelectionSignature = string.Empty;
        }
        else if (SceneComponentTypes.IsCurveRenderer(component))
        {
            SceneComponentTypes.WriteCurveRenderer(component,
                CurveRendererComponentData.CreateDefault() with { Editing = false });
            RebuildGeneratedVoxels(sceneObject, component, string.Empty);
        }
        else if (SceneComponentTypes.IsTreeGenerator(component))
        {
            SceneComponentTypes.WriteTreeGenerator(component, TreeGeneratorComponentData.Default);
            RebuildGeneratedVoxels(sceneObject, component, string.Empty);
        }
        else
        {
            var definition = _pluginRuntime?.Components.FirstOrDefault(item => item.Id == component.TypeId);
            component.Data = definition is null || definition.DefaultData.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.SerializeToElement(new { })
                : definition.DefaultData.Clone();
        }

        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        SetStatus($"已恢复“{sceneObject.Name}”的{SceneComponentTypes.GetDisplayName(component)}组件默认值");
    }

    private static void BeginInspectorField(string label, string? tooltip = null)
    {
        var start = ImGui.GetCursorPosX();
        var labelWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * .31F, 94F, 158F);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        if (tooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(start + labelWidth, ImGui.GetCursorPosX()));
        ImGui.SetNextItemWidth(-1F);
    }

    /// <summary>
    /// 绘制 Unity 风格的体素资产引用字段。字段本身不是文本框，资产路径只能通过项目中的可用资产选择。
    /// </summary>
    private unsafe bool DrawVoxelModelReference(string current, bool hasEmbeddedData, out string selected, out bool extractRequested)
    {
        selected = current;
        extractRequested = false;
        var references = GetVoxelModelReferences();
        var known = references.Any(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase));
        var display = string.IsNullOrWhiteSpace(current)
            ? hasEmbeddedData ? "内嵌数据" : "无引用（选择体素资产）"
            : known
                ? Path.GetFileNameWithoutExtension(current)
                : $"丢失引用 · {Path.GetFileName(current)}";
        var availableTextWidth = Math.Max(28F, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() * 1.7F);
        if (ImGui.CalcTextSize(display).X > availableTextWidth)
        {
            while (display.Length > 1 && ImGui.CalcTextSize(display + "…").X > availableTextWidth)
                display = display[..^1];
            display += "…";
        }
        var changed = false;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.BeginCombo("##InspectorVoxelModelReference", display))
        {
            if (!string.IsNullOrWhiteSpace(current) && !known)
            {
                ImGui.BeginDisabled();
                ImGui.Selectable($"丢失引用 · {current}", true);
                ImGui.EndDisabled();
                ImGui.Separator();
            }

            if (ImGui.Selectable("无引用", string.IsNullOrWhiteSpace(current)))
            {
                selected = string.Empty;
                changed = true;
            }

            foreach (var reference in references)
            {
                ImGui.PushID(reference);
                var isSelected = string.Equals(reference, current, StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileNameWithoutExtension(reference);
                if (ImGui.Selectable(name, isSelected))
                {
                    selected = reference;
                    changed = true;
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip(reference);
                ImGui.PopID();
            }

            ImGui.EndCombo();
        }

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("MVoxelVoxelAsset");
            if (payload.NativePtr != null && !string.IsNullOrEmpty(_assetBrowser.DraggingVoxelAssetPath))
            {
                selected = "Assets/" + _assetBrowser.DraggingVoxelAssetPath;
                changed = true;
            }
            ImGui.EndDragDropTarget();
        }
        if (hasEmbeddedData && string.IsNullOrWhiteSpace(current) &&
            ImGui.BeginPopupContextItem("##EmbeddedVoxelMenu"))
        {
            extractRequested = ImGui.MenuItem("提取体素数据…");
            ImGui.EndPopup();
        }
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(current))
            ImGui.SetTooltip(known ? current : "引用的体素资产不存在，请重新选择。");
        return changed;
    }

    private IReadOnlyList<string> GetVoxelModelReferences()
    {
        if (_project is null) return Array.Empty<string>();
        var root = Path.GetFullPath(_project.AssetsPath);
        if (string.Equals(_voxelModelReferenceCacheRoot, root, StringComparison.OrdinalIgnoreCase))
            return _voxelModelReferenceCache;

        _voxelModelReferenceCacheRoot = root;
        try
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mvmodel", ".mvworld" };
            _voxelModelReferenceCache = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Select(path => "Assets/" + Path.GetRelativePath(root, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            _voxelModelReferenceCache = Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            _voxelModelReferenceCache = Array.Empty<string>();
        }

        return _voxelModelReferenceCache;
    }

    private void DrawComponentSummary(SceneObject selectedObject, ComponentRecord component)
    {
        if (SceneComponentTypes.IsTransform(component))
        {
            DrawTransformInputs(selectedObject);
            return;
        }

        if (SceneComponentTypes.IsVoxelModel(component))
        {
            var editing = _voxelEditTargetId == selectedObject.Id && _activeTool == EditorTool.Draw;
            if (ImGui.Button(editing ? "退出绘制模式" : "绘制模式", new Vector2(-1F, 0F)))
            {
                _voxelEditTargetId = editing ? null : selectedObject.Id;
                _activeTool = editing ? EditorTool.Pick : EditorTool.Draw;
                _sceneGizmoMode = SceneGizmoMode.Select;
                if (!editing) _selectedSceneObjectId = selectedObject.Id;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("仅编辑此模型的体素；再次点击退出绘制模式。");
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var mirrored = data.MirrorX;
            BeginInspectorField("镜像 X", "绕模型局部 X 轴的包围盒中心翻转体素；操作可撤销，编辑共享资产时保留原始资产。请先将模型缩放恢复为 1。");
            if (ImGui.Checkbox("##VoxelMirrorX", ref mirrored))
                MirrorModelLocalX(selectedObject, component, mirrored);
            data = SceneComponentTypes.ReadVoxelModel(component);
            BeginInspectorField("体素数据", "一个组件对应一份体素数据。可从资产窗口拖入模型，或选择资产；编辑引用实例会转为内嵌数据，保留共享源文件。右键内嵌数据可提取。");
            var hasEmbeddedData = string.IsNullOrWhiteSpace(data.Model) &&
                (EmbeddedVoxelData.HasData(component) || _objectVoxels.GetOrCreate(selectedObject.Id).OccupiedVoxelCount > 0);
            var changed = DrawVoxelModelReference(data.Model, hasEmbeddedData, out var model, out var extractRequested);
            if (hasEmbeddedData && string.IsNullOrWhiteSpace(data.Model) && ImGui.BeginPopupContextItem("##VoxelAnimationKeyMenu"))
            {
                if (ImGui.MenuItem("插入动画关键帧"))
                {
                    var animationComponent = selectedObject.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
                    if (animationComponent is not null)
                        InsertAnimationKey(selectedObject, SceneComponentTypes.ReadAnimation(animationComponent), 0, "VoxelModel", "VoxelData");
                }
                ImGui.EndPopup();
            }

            BeginInspectorField("排序图层", "同一坐标按排序图层、排序顺序和层级顺序决定显示优先级。");
            var sortingLayer = data.SortingLayer;
            if (_sceneDocument is { } sortingDocument && ImGui.BeginCombo("##VoxelSortingLayer", sortingLayer))
            {
                foreach (var layer in sortingDocument.SortingLayerNames)
                {
                    if (ImGui.Selectable(layer, layer == sortingLayer))
                    {
                        data = data with { SortingLayer = layer };
                        SceneComponentTypes.WriteVoxelModel(component, data);
                        MarkSceneDocumentDirty();
                    }
                }
                ImGui.EndCombo();
            }
            BeginInspectorField("排序顺序", "同一坐标按排序图层、排序顺序和层级顺序决定显示优先级。");
            var sortingOrder = data.SortingOrder;
            if (ExpressionInput.Int("##VoxelSortingOrder", ref sortingOrder))
            {
                data = data with { SortingOrder = sortingOrder };
                SceneComponentTypes.WriteVoxelModel(component, data);
                MarkSceneDocumentDirty();
            }
            BeginInspectorField("体素数量", "当前模型包含的体素数量。");
            var voxelCount = _objectVoxels.TryGetWorld(selectedObject.Id, out var objectWorld)
                ? objectWorld.OccupiedVoxelCount : data.VoxelCount;
            ImGui.TextDisabled($"{voxelCount:N0}");
            DrawModelMaterialSlots(selectedObject);
            if (changed)
            {
                ApplyVoxelModelReference(selectedObject, component, model);
            }
            return;
        }

        if (SceneComponentTypes.IsVoxelSelection(component))
        {
            DrawVoxelSelectionComponent(selectedObject, component);
            return;
        }

        if (SceneComponentTypes.IsCurveRenderer(component))
        {
            DrawCurveRendererComponent(selectedObject, component);
            return;
        }

        if (SceneComponentTypes.IsTreeGenerator(component))
        {
            DrawTreeGeneratorComponent(selectedObject, component);
            return;
        }

        if (SceneComponentTypes.IsProbeVolume(component))
        {
            var data = SceneComponentTypes.ReadProbeVolume(component);
            var editing = _selectedSceneObjectId == selectedObject.Id && data.Editing;
            if (ImGui.Button(editing ? "退出探针编辑" : "编辑探针", new Vector2(-1F, 0F)))
            {
                data = data with { Editing = !editing };
                SceneComponentTypes.WriteProbeVolume(component, data);
                MarkSceneDocumentDirty();
                SetStatus(editing ? "已退出探针编辑" : "已进入探针编辑，可在场景视图调整体积");
            }
            BeginInspectorField("自动烘焙", "开启后，场景体素内容稳定约半秒会自动重烘一次探针；自动结果只在本会话生效，需要写进项目请到探针体积窗口手动生成数据。");
            var autoBake = data.AutoBake;
            ImGui.BeginDisabled();
            ImGui.Checkbox("##ProbeAutoBake", ref autoBake);
            ImGui.EndDisabled();
            BeginInspectorField("偏移 X", "探针体积相对对象原点的偏移。视图Gizmo与此值同步。");
            var offsetX = data.OffsetX; if (ImGui.DragFloat("##ProbeOffsetX", ref offsetX, 0.05F)) data = data with { OffsetX = offsetX };
            BeginInspectorField("偏移 Y", null);
            var offsetY = data.OffsetY; if (ImGui.DragFloat("##ProbeOffsetY", ref offsetY, 0.05F)) data = data with { OffsetY = offsetY };
            BeginInspectorField("偏移 Z", null);
            var offsetZ = data.OffsetZ; if (ImGui.DragFloat("##ProbeOffsetZ", ref offsetZ, 0.05F)) data = data with { OffsetZ = offsetZ };
            BeginInspectorField("尺寸 X/Y/Z", "探针体积范围和自动采样网格尺寸。");
            var probeSize = new System.Numerics.Vector3(data.SizeX, data.SizeY, data.SizeZ);
            var sizeChanged = ImGui.DragFloat3("##ProbeSize", ref probeSize, 0.1F);
            if (sizeChanged) data = data with { SizeX = probeSize.X, SizeY = probeSize.Y, SizeZ = probeSize.Z };
            BeginInspectorField("探针密度", "每格体积内的探针密度，首期烘焙使用规则三维网格。");
            var density = data.Density;
            if (ImGui.DragFloat("##ProbeDensity", ref density, 0.05F, 0.05F, 32F)) data = data with { Density = density };
            if (!data.Equals(SceneComponentTypes.ReadProbeVolume(component)))
            {
                SceneComponentTypes.WriteProbeVolume(component, data);
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
            }
            return;
        }

        if (SceneComponentTypes.IsAnimation(component))
        {
            var animation = SceneComponentTypes.ReadAnimation(component);
            var clip = animation.ClipAsset;
            BeginInspectorField("动画片段", "动画片段资产路径；可在动画窗口中创建和编辑。");
            var changed = ImGui.InputText("##AnimationClip", ref clip, 512);
            var playing = animation.Playing;
            changed |= ImGui.Checkbox("播放", ref playing);
            var loop = animation.Loop;
            changed |= ImGui.Checkbox("循环", ref loop);
            var speed = animation.Speed;
            changed |= ImGui.DragFloat("速度", ref speed, .05F, -8F, 8F);
            if (changed)
            {
                SceneComponentTypes.WriteAnimation(component, animation with { ClipAsset = clip, Playing = playing, Loop = loop, Speed = speed });
                MarkSceneDocumentDirty();
            }
            if (ImGui.Button("打开动画窗口"))
            {
                _showAnimationWindow = true;
                _animationLinkedObjectId = GetSelectedSceneObject()?.Id;
            }
            return;
        }

        if (SceneComponentTypes.IsSurfaceDeposit(component))
        {
            DrawSurfaceDepositInspector(component);
            return;
        }

        if (SceneComponentTypes.IsPostProcessOverride(component))
        {
            var data = SceneComponentTypes.ReadPostProcessOverride(component);
            var changed = ImGui.Checkbox("启用局部后处理", ref data.Enabled);
            BeginInspectorField("覆盖权重", "按局部范围影响后处理参数的强度。");
            changed |= ImGui.DragFloat("##PostProcessWeight", ref data.Weight, .01F, 0F, 1F);
            BeginInspectorField("曝光偏移", "局部曝光覆盖，环境雾和天气不在此组件中配置。");
            changed |= ImGui.DragFloat("##PostProcessExposure", ref data.Exposure, .01F, -16F, 16F);
            BeginInspectorField("Bloom强度", "局部辉光强度覆盖。");
            changed |= ImGui.DragFloat("##PostProcessBloom", ref data.BloomIntensity, .01F, 0F, 8F);
            BeginInspectorField("Bloom阈值", "局部辉光亮度阈值。");
            changed |= ImGui.DragFloat("##PostProcessBloomThreshold", ref data.BloomThreshold, .01F, 0F, 8F);
            BeginInspectorField("Bloom半径", "局部辉光扩散半径。");
            changed |= ImGui.DragFloat("##PostProcessBloomRadius", ref data.BloomRadius, .05F, .1F, 64F);
            BeginInspectorField("暗角", "局部暗角强度。");
            changed |= ImGui.DragFloat("##PostProcessVignette", ref data.Vignette, .01F, 0F, 1F);
            if (changed)
            {
                SceneComponentTypes.WritePostProcessOverride(component, data);
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
            }
            return;
        }

        if (SceneComponentTypes.IsWindField(component))
        {
            var data = SceneComponentTypes.ReadWindField(component);
            var changed = ImGui.Checkbox("启用局部风场", ref data.Enabled);
            BeginInspectorField("风向 X/Y/Z", "局部风力方向，会自动归一化。");
            changed |= ImGui.DragFloat3("##WindDirection", ref data.Direction, .01F);
            BeginInspectorField("风力强度", "局部风场基础强度。");
            changed |= ImGui.DragFloat("##WindStrength", ref data.Strength, .05F, 0F, 1000F);
            BeginInspectorField("影响范围", "从风场对象原点开始的球形影响半径。");
            changed |= ImGui.DragFloat("##WindRange", ref data.Range, .1F, .01F, 100000F);
            BeginInspectorField("衰减", "距离越远风力按此指数衰减。");
            changed |= ImGui.DragFloat("##WindFalloff", ref data.Falloff, .05F, 0F, 8F);
            changed |= ImGui.Checkbox("启用湍流", ref data.Turbulence);
            if (data.Turbulence)
            {
                BeginInspectorField("湍流强度", "局部风场的动态扰动幅度。");
                changed |= ImGui.DragFloat("##WindTurbulence", ref data.TurbulenceStrength, .05F, 0F, 1000F);
            }
            if (changed)
            {
                SceneComponentTypes.WriteWindField(component, data);
                MarkSceneDocumentDirty();
            }
            return;
        }

        if (SceneComponentTypes.IsWindReceiver(component))
        {
            var data = SceneComponentTypes.ReadWindReceiver(component);
            var changed = ImGui.Checkbox("启用风力接收", ref data.Enabled);
            changed |= ImGui.Checkbox("接收全局风", ref data.ReceiveGlobalWind);
            changed |= ImGui.Checkbox("接收局部风场", ref data.ReceiveLocalWind);
            changed |= ImGui.Checkbox("子对象一并接收", ref data.IncludeChildren);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("启用后会将风力接收规则传播到当前对象层级下的子对象。");
            BeginInspectorField("受力倍率", "体素模型对风场响应的强度。");
            changed |= ImGui.DragFloat("##WindForceScale", ref data.ForceScale, .05F, 0F, 1000F);
            BeginInspectorField("阻尼", "风力响应的速度阻尼。");
            changed |= ImGui.DragFloat("##WindDrag", ref data.Drag, .01F, 0F, 1F);
            BeginInspectorField("柔韧度", "体素模型受风形变的响应程度。");
            changed |= ImGui.DragFloat("##WindFlexibility", ref data.Flexibility, .01F, 0F, 1F);
            if (changed)
            {
                SceneComponentTypes.WriteWindReceiver(component, data);
                MarkSceneDocumentDirty();
            }
            return;
        }


        if (SceneComponentTypes.IsCamera(component))
        {
            var data = SceneComponentTypes.ReadCamera(component);
            var fieldOfView = data.FieldOfViewDegrees;
            var nearClip = data.NearClip;
            var farClip = data.FarClip;
            var changed = false;
            var physicalCamera = data.PhysicalCamera;
            if (!physicalCamera)
            {
                BeginInspectorField("视场角", "非物理相机使用的垂直视野角度。");
                var changedFov = ImGui.DragFloat("##InspectorCameraFov", ref fieldOfView, 0.1F, 20F, 120F, "%.1f°");
                changed = changedFov;
            }
            else
            {
                changed = false;
            }
            if (ImGui.Checkbox("物理相机", ref physicalCamera))
            {
                data = data with { PhysicalCamera = physicalCamera };
                changed = true;
            }
            BeginInspectorField("近裁剪", "相机近平面距离；过近可能导致几何体被裁掉。");
            changed |= ImGui.DragFloat("##InspectorCameraNear", ref nearClip, 0.01F, 0.001F, 1_000_000F, "%.3f");
            BeginInspectorField("远裁剪", "相机远平面距离；超过此距离的内容不会显示。");
            changed |= ImGui.DragFloat("##InspectorCameraFar", ref farClip, 1F, 0.002F, 100_000_000F, "%.1f");
            var aaMode = data.RealtimeAntiAliasingPattern == 2 ? 3 : data.RealtimeAntiAliasingSamples == 1 ? 0 : data.RealtimeAntiAliasingPattern + 1;
            BeginInspectorField("实时抗锯齿", "FXAA 在显示图上平滑边缘；超采样追踪多条完整光照射线。路径追踪使用独立累积采样。");
            if (ImGui.Combo("##CameraAAMode", ref aaMode, "关闭\0规则网格超采样\0旋转网格超采样\0FXAA\0", 4))
            {
                data = data with { RealtimeAntiAliasingSamples = aaMode is 0 or 3 ? 1 : data.RealtimeAntiAliasingSamples == 1 ? 4 : data.RealtimeAntiAliasingSamples, RealtimeAntiAliasingPattern = aaMode == 3 ? 2 : Math.Max(0, aaMode - 1) };
                changed = true;
            }
            if (aaMode is 1 or 2)
            {
                var aaSamples = data.RealtimeAntiAliasingSamples switch { 2 => 0, 8 => 2, 16 => 3, _ => 1 };
                BeginInspectorField("采样数", "每像素追踪的射线数，采样数越高开销越大。");
                if (ImGui.Combo("##CameraAASamples", ref aaSamples, "2×\04×\08×\016×\0", 4))
                {
                    data = data with { RealtimeAntiAliasingSamples = aaSamples switch { 0 => 2, 1 => 4, 2 => 8, _ => 16 } };
                    changed = true;
                }
                var aaSpread = data.RealtimeAntiAliasingSpread;
                BeginInspectorField("采样范围", "控制样本在像素内的分布范围；较小范围保留更多锐度，平滑效果减弱。");
                if (ImGui.DragFloat("##CameraAASpread", ref aaSpread, .01F, .25F, 1F, "%.2f"))
                {
                    data = data with { RealtimeAntiAliasingSpread = aaSpread };
                    changed = true;
                }
            }
            if (aaMode == 3)
            {
                var threshold = data.FxaaThreshold;
                BeginInspectorField("边缘阈值", "较低的阈值会平滑更多低对比度边缘。");
                if (ImGui.DragFloat("##FxaaThreshold", ref threshold, .002F, .0312F, .333F, "%.3f")) { data = data with { FxaaThreshold = threshold }; changed = true; }
                var strength = data.FxaaStrength;
                BeginInspectorField("平滑强度", "控制边缘平滑的混合比例。");
                if (ImGui.DragFloat("##FxaaStrength", ref strength, .01F, 0F, 1F)) { data = data with { FxaaStrength = strength }; changed = true; }
            }
            var waterFiltering = data.WaterFootprintFiltering;
            BeginInspectorField("水面斜视过滤", "按相机像素在水面上的方向和大小过滤高频波纹，减轻远处闪烁和条纹；适用于程序化水面，不是纹理采样器的各向异性倍数。");
            if (ImGui.DragFloat("##WaterFootprintFiltering", ref waterFiltering, .02F, 0F, 2F, "%.2f"))
            { data = data with { WaterFootprintFiltering = waterFiltering }; changed = true; }
            var postProcess = data.PostProcess?.Clone() ?? CameraPostProcessStack.Default;
            if (ImGui.CollapsingHeader("相机后处理", ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= ImGui.Checkbox("覆盖项目默认后处理", ref postProcess.OverrideProjectDefaults);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("开启后应用相机效果；新添加的色调映射、辉光、色彩调整和输出抖动未配置时继承项目设置。");
                foreach (var effect in postProcess.Effects.ToArray())
                {
                    ImGui.PushID(effect.Type.ToString());
                    var enabled = effect.Enabled;
                    if (ImGui.Checkbox("启用", ref enabled)) { effect.Enabled = enabled; changed = true; }
                    ImGui.SameLine(); ImGui.Text(effect.Type switch
                    {
                        CameraPostProcessEffectType.AmbientOcclusion => "环境遮蔽",
                        CameraPostProcessEffectType.VoxelIndirectLight => "体素间接光",
                        CameraPostProcessEffectType.ProbeVolumeGi => "探针 GI",
                        CameraPostProcessEffectType.Vignette => "暗角",
                        CameraPostProcessEffectType.DepthOfField => "景深（路径追踪）",
                        CameraPostProcessEffectType.ToneMapping => "色调映射",
                        CameraPostProcessEffectType.Dither => "输出抖动",
                        CameraPostProcessEffectType.Bloom => "辉光",
                        CameraPostProcessEffectType.ColorGrading => "色彩调整",
                        CameraPostProcessEffectType.FilmGrain => "胶片颗粒",
                        CameraPostProcessEffectType.Posterize => "色阶分离",
                        CameraPostProcessEffectType.SplitToning => "分离色调",
                        _ => effect.Type.ToString(),
                    });
                    if (effect.Type == CameraPostProcessEffectType.DepthOfField && ImGui.IsItemHovered())
                        ImGui.SetTooltip("仅路径追踪生效；物理相机使用镜头参数，普通相机可继承项目参数或单独设置。启用覆盖后使用此开关。");
                    if (effect.Type == CameraPostProcessEffectType.DepthOfField && !physicalCamera)
                    {
                        BeginInspectorField("独立景深参数", "关闭时继承项目的光圈半径和对焦距离。");
                        changed |= ImGui.Checkbox("##CameraDofOverride", ref effect.OverrideDepthOfFieldParameters);
                        if (effect.OverrideDepthOfFieldParameters)
                        {
                            BeginInspectorField("光圈半径", "镜头采样半径；零表示没有景深模糊。");
                            changed |= ImGui.DragFloat("##CameraDofAperture", ref effect.DepthOfFieldAperture, .005F, 0F, .5F, "%.3f");
                            BeginInspectorField("对焦距离", "沿相机前向到焦平面的距离。");
                            changed |= ImGui.DragFloat("##CameraDofFocus", ref effect.DepthOfFieldFocusDistance, .1F, .01F, 1_000_000F);
                        }
                    }
                    if (effect.Type is CameraPostProcessEffectType.AmbientOcclusion or CameraPostProcessEffectType.VoxelIndirectLight or CameraPostProcessEffectType.ProbeVolumeGi or CameraPostProcessEffectType.Vignette)
                    {
                        var intensity = effect.Intensity;
                        if (ImGui.DragFloat("强度", ref intensity, .01F, 0F, effect.Type is CameraPostProcessEffectType.AmbientOcclusion or CameraPostProcessEffectType.Vignette ? 1F : 4F)) { effect.Intensity = intensity; changed = true; }
                    }
                    else if (!Enum.IsDefined(effect.Type))
                        ImGui.TextDisabled("该效果尚未接入渲染");
                    if (effect.Type == CameraPostProcessEffectType.ToneMapping)
                    {
                        var curve = (int)effect.ToneCurve;
                        BeginInspectorField("色调曲线", "线性截断、Reinhard、ACES 或 Filmic。");
                        if (ImGui.Combo("##ToneCurve", ref curve, "线性\0Reinhard\0ACES\0Filmic\0", 4)) { effect.ToneCurve = (ToneCurve)curve; changed = true; }
                        BeginInspectorField("曝光偏移（EV）", "叠加到物理相机或项目曝光；Volume 曝光随后叠加。");
                        changed |= ImGui.DragFloat("##ToneExposure", ref effect.ExposureOffset, .02F, -16F, 16F);
                        BeginInspectorField("Gamma", "输出显示 Gamma。");
                        changed |= ImGui.DragFloat("##ToneGamma", ref effect.Gamma, .01F, 1F, 3.5F);
                        BeginInspectorField("白平衡（K）", "6500 K 为中性。");
                        changed |= ImGui.DragFloat("##ToneWhiteBalance", ref effect.WhiteBalance, 10F, 1500F, 12000F, "%.0f");
                    }
                    if (effect.Type == CameraPostProcessEffectType.Bloom)
                    {
                        BeginInspectorField("强度", "高光辉光强度。");
                        changed |= ImGui.DragFloat("##BloomIntensity", ref effect.Intensity, .01F, 0F, 2F);
                        BeginInspectorField("亮度阈值", "高于阈值的亮部产生辉光；全局 Volume 可以叠加阈值偏移。");
                        changed |= ImGui.DragFloat("##BloomThreshold", ref effect.Threshold, .01F, 0F, 8F);
                        BeginInspectorField("半径（像素）", "辉光扩散范围。");
                        changed |= ImGui.DragFloat("##BloomRadius", ref effect.Radius, .1F, 1F, 64F);
                    }
                    if (effect.Type == CameraPostProcessEffectType.ColorGrading)
                    {
                        BeginInspectorField("饱和度", "1 为原始饱和度。");
                        changed |= ImGui.DragFloat("##GradeSaturation", ref effect.Saturation, .01F, 0F, 2.5F);
                        BeginInspectorField("色相偏移", "色相旋转角度；相机覆盖启用时不使用项目色相循环。");
                        changed |= ImGui.DragFloat("##GradeHue", ref effect.HueShift, .5F, -180F, 180F, "%.1f°");
                        BeginInspectorField("对比度", "1 为原始对比度。");
                        changed |= ImGui.DragFloat("##GradeContrast", ref effect.Contrast, .01F, .2F, 2.5F);
                        BeginInspectorField("亮度", "1 为原始亮度。");
                        changed |= ImGui.DragFloat("##GradeBrightness", ref effect.Brightness, .01F, .1F, 3F);
                    }
                    if (effect.Type == CameraPostProcessEffectType.SplitToning)
                    {
                        var shadows = new System.Numerics.Vector3(effect.ShadowR, effect.ShadowG, effect.ShadowB);
                        BeginInspectorField("暗部颜色", "暗部的颜色乘数；白色不改变颜色。");
                        if (ImGui.ColorEdit3("##SplitShadows", ref shadows)) { effect.ShadowR = shadows.X; effect.ShadowG = shadows.Y; effect.ShadowB = shadows.Z; changed = true; }
                        var highlights = new System.Numerics.Vector3(effect.HighlightR, effect.HighlightG, effect.HighlightB);
                        BeginInspectorField("亮部颜色", "亮部的颜色乘数；白色不改变颜色。");
                        if (ImGui.ColorEdit3("##SplitHighlights", ref highlights)) { effect.HighlightR = highlights.X; effect.HighlightG = highlights.Y; effect.HighlightB = highlights.Z; changed = true; }
                        BeginInspectorField("明暗分界", "分界越低，越多中间调使用亮部颜色。");
                        changed |= ImGui.DragFloat("##SplitBalance", ref effect.ToningBalance, .01F, .05F, .95F);
                        BeginInspectorField("混合强度", "0 为原图，1 为完整染色。");
                        changed |= ImGui.DragFloat("##SplitMix", ref effect.Intensity, .01F, 0F, 1F);
                    }
                    if (effect.Type == CameraPostProcessEffectType.FilmGrain)
                    {
                        BeginInspectorField("强度", "固定屏幕空间颗粒的强度；不同帧保持稳定。");
                        changed |= ImGui.DragFloat("##GrainIntensity", ref effect.Intensity, .01F, 0F, 1F);
                        BeginInspectorField("颗粒尺寸（像素）", "较大数值形成更粗的颗粒。");
                        changed |= ImGui.DragFloat("##GrainSize", ref effect.Radius, .1F, 1F, 8F);
                    }
                    if (effect.Type == CameraPostProcessEffectType.Posterize)
                    {
                        BeginInspectorField("色阶数", "每个颜色通道保留的离散亮度级数。");
                        changed |= ImGui.DragInt("##PosterizeLevels", ref effect.Samples, 1F, 2, 64);
                        BeginInspectorField("混合强度", "0 为原图，1 为完全色阶分离。");
                        changed |= ImGui.DragFloat("##PosterizeMix", ref effect.Intensity, .01F, 0F, 1F);
                    }
                    if (effect.Type == CameraPostProcessEffectType.AmbientOcclusion)
                    {
                        var samples = effect.Samples;
                        BeginInspectorField("采样数", "环境遮蔽采样数；更高的值增加渲染开销。");
                        if (ImGui.DragInt("##CameraAoSamples", ref samples, 1F, 4, 64)) { effect.Samples = samples; changed = true; }
                        BeginInspectorField("遮蔽曲线", "控制环境遮蔽的明暗分布。");
                        changed |= ImGui.DragFloat("##CameraAoPower", ref effect.AoPower, .02F, .2F, 4F);
                    }
                    if (ImGui.SmallButton("删除")) { postProcess.Effects.Remove(effect); changed = true; }
                    ImGui.PopID();
                }
                if (ImGui.BeginMenu("添加效果"))
                {
                    foreach (var (type, label) in new[]
                    {
                        (CameraPostProcessEffectType.AmbientOcclusion, "环境遮蔽"),
                        (CameraPostProcessEffectType.VoxelIndirectLight, "体素间接光"),
                        (CameraPostProcessEffectType.ProbeVolumeGi, "探针 GI"),
                        (CameraPostProcessEffectType.Vignette, "暗角"),
                        (CameraPostProcessEffectType.DepthOfField, "景深"),
                        (CameraPostProcessEffectType.ToneMapping, "色调映射"),
                        (CameraPostProcessEffectType.Dither, "输出抖动"),
                        (CameraPostProcessEffectType.Bloom, "辉光"),
                        (CameraPostProcessEffectType.ColorGrading, "色彩调整"),
                        (CameraPostProcessEffectType.FilmGrain, "胶片颗粒"),
                        (CameraPostProcessEffectType.Posterize, "色阶分离"),
                        (CameraPostProcessEffectType.SplitToning, "分离色调"),
                    })
                    {
                        if (postProcess.Find(type) is null && ImGui.MenuItem(label))
                        {
                            postProcess.Effects.Add(type switch
                            {
                                CameraPostProcessEffectType.Bloom => new CameraPostProcessEffect { Type = type, Intensity = .35F, Radius = 12F },
                                CameraPostProcessEffectType.FilmGrain => new CameraPostProcessEffect { Type = type, Intensity = .15F, Radius = 1F },
                                _ => new CameraPostProcessEffect { Type = type },
                            });
                            changed = true;
                        }
                    }
                    ImGui.EndMenu();
                }
            }
            postProcess.Normalize();
            data = data with { FieldOfViewDegrees = fieldOfView, NearClip = nearClip, FarClip = farClip, PostProcess = postProcess };
            if (physicalCamera)
            {
                BeginInspectorField("焦距（mm）", "物理镜头焦距，影响视场角。");
                var focal = data.FocalLengthMillimeters; changed |= ImGui.DragFloat("##CameraFocal", ref focal, .1F, 1F, 500F); data = data with { FocalLengthMillimeters = focal };
                BeginInspectorField("传感器宽度（mm）", null);
                var sensor = data.SensorWidthMillimeters; changed |= ImGui.DragFloat("##CameraSensor", ref sensor, .1F, 1F, 100F); data = data with { SensorWidthMillimeters = sensor };
                BeginInspectorField("光圈（f）", null);
                var aperture = data.ApertureFStop; changed |= ImGui.DragFloat("##CameraAperture", ref aperture, .05F, .7F, 64F); data = data with { ApertureFStop = aperture };
                BeginInspectorField("快门（秒）", null);
                var shutter = data.ShutterSpeed; changed |= ImGui.DragFloat("##CameraShutter", ref shutter, .001F, 1F / 8000F, 60F); data = data with { ShutterSpeed = shutter };
                BeginInspectorField("ISO", null);
                var iso = data.Iso; changed |= ImGui.DragFloat("##CameraIso", ref iso, 1F, 25F, 51200F); data = data with { Iso = iso };
                BeginInspectorField("对焦距离", null);
                var focus = data.FocusDistance; changed |= ImGui.DragFloat("##CameraFocus", ref focus, .1F, .01F, 1000000F); data = data with { FocusDistance = focus };
            }
            var mask = data.CullingMask;
            var layerNames = _sceneDocument?.LayerNames ?? ["默认"];
            var visibleCount = 0;
            for (var layerIndex = 0; layerIndex < layerNames.Count; layerIndex++)
            {
                if ((mask & (1u << layerIndex)) != 0)
                {
                    visibleCount++;
                }
            }

            BeginInspectorField("渲染图层", "相机只渲染勾选图层里的对象，其余图层对本相机不可见。");
            if (ImGui.BeginCombo($"##InspectorCameraLayers", $"{visibleCount}/{layerNames.Count} 可见"))
            {
                if (ImGui.Selectable("全选"))
                {
                    var all = 0u;
                    for (var layerIndex = 0; layerIndex < Math.Min(layerNames.Count, SceneDocument.MaximumLayerCount); layerIndex++)
                    {
                        all |= 1u << layerIndex;
                    }

                    changed |= all != mask;
                    mask = all;
                }

                if (ImGui.Selectable("全不选"))
                {
                    changed |= mask != 0u;
                    mask = 0u;
                }

                ImGui.Separator();
                for (var layerIndex = 0; layerIndex < layerNames.Count; layerIndex++)
                {
                    var bit = 1u << layerIndex;
                    var on = (mask & bit) != 0;
                    if (ImGui.Checkbox($"{layerNames[layerIndex]}##InspectorCameraLayer{layerIndex}", ref on))
                    {
                        mask = on ? mask | bit : mask & ~bit;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            if (changed)
            {
                SceneComponentTypes.WriteCamera(component, data with { CullingMask = mask, PhysicalCamera = physicalCamera });
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
            }
            return;
        }

        if (SceneComponentTypes.IsLight(component))
        {
            DrawLightComponentInspector(component);
            return;
        }

        var pluginDefinition = _pluginRuntime?.Components.FirstOrDefault(item => item.Id == component.TypeId);
        if (component.OwnerPluginId is { Length: > 0 } ownerPlugin && pluginDefinition is null)
            ImGui.TextColored(new Vector4(1F, .65F, .25F, 1F), $"插件未启用或未安装：{ownerPlugin}");
        ImGui.TextDisabled($"类型 ID  {component.TypeId}");
        ImGui.TextDisabled($"来源插件  {component.OwnerPluginId ?? "未知插件"}");
        var pluginData = component.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : component.Data.GetRawText();
        if (ImGui.InputTextMultiline("数据##InspectorPluginComponentData", ref pluginData,
                16_384, new Vector2(-1F, 108F), ImGuiInputTextFlags.AllowTabInput))
        {
            try
            {
                using var document = JsonDocument.Parse(pluginData);
                component.Data = document.RootElement.Clone();
                _componentWarning = null;
                MarkSceneDocumentDirty();
            }
            catch (JsonException)
            {
                _componentWarning = "插件组件数据必须是合法 JSON";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件可以通过自己的面板解释这些数据；没有专用面板时可在此编辑 JSON。保存前会校验格式。");
    }

    private void RotateModel(SceneObject sceneObject, OrthogonalRotation rotation)
    {
        if (sceneObject.Transform.Mode != TransformMode.Grid) return;
        RotateModelObjectBy90(sceneObject, rotation.Equals(OrthogonalRotation.AroundX90) ? 0 :
            rotation.Equals(OrthogonalRotation.AroundY90) ? 1 : 2);
    }

    private static void UpdateTransformMarker(SceneObject sceneObject)
    {
        var transform = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsTransform);
        if (transform is not null)
            transform.Data = JsonSerializer.SerializeToElement(new { kind = sceneObject.Transform.Mode.ToString() });
    }

    private void MarkSceneDocumentDirty()
    {
        _sceneDocumentDirty = true;
        _historySceneRevision++;
        // 探针自动烘焙只看这里打出的脏标记，没有编辑就不会去算内容指纹
        _probeSceneSignatureDirty = true;
    }

    /// <summary>
    /// 绘制与检查器并列停靠的渲染配置窗口
    /// </summary>
    private void DrawRenderConfig()
    {
        if (!_showRenderConfig)
        {
            _requestedInspectorTab = null;
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(display.X - layout.RightWidth, WorkspaceTop + 1F), new Vector2(layout.RightWidth, layout.MainHeight));
        if (_requestedInspectorTab == 1) ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("渲染配置##渲染配置窗口", PanelWindowFlags))
        {
            _requestedInspectorTab = null;
        }
        else
        {
            DrawPanelWindowMenu();
            ImGui.TextColored(ToColor(EditorPalette.Text), "渲染配置");
            DrawConfigurationPanel();
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsAnyItemActive())
                _renderSettingsInteractionAt = Environment.TickCount64;
        }
        ImGui.End();
        _requestedInspectorTab = null;
    }

    private void DrawConfigurationPanel()
    {
        DrawSettingsPageHeader("视口渲染", "当前项目值 · 实时生效");
        // 环境是独立窗口，渲染配置只放一个入口按钮，不重复它的参数
        var halfButton = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5F;
        if (ImGui.Button("打开完整设置", new Vector2(halfButton, 32F)))
            OpenSettings(1, 1, "project.visual.render");
        ImGui.SameLine();
        if (ImGui.Button("环境配置…", new Vector2(halfButton, 32F)))
            OpenEnvironmentWindow();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开独立的环境配置窗口：天空与地面、水面、天气、雾气（含高度雾、噪声与散射）");

        DrawSettingsSectionHeader("场景视图辅助显示");
        ImGui.Checkbox("显示网格##scene", ref _showGrid);
        ImGui.Checkbox("体素棱线描边##scene", ref _showVoxelOutlines);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("近处显示体素棱线，远处随屏幕上的体素尺寸逐渐淡出");
        DrawSettingsSectionHeader("渲染参数");
        DrawRenderSettingsPanel();
    }

    private int _projectPathDialogMode;
    private string _projectPathText = string.Empty;
    private string _projectPathError = string.Empty;
    private readonly ProjectLocationForm _locationForm = new();
    private string _projectBrowserPath = string.Empty;
    private string? _projectBrowserSelectedPath;
    private readonly Stack<string> _projectBrowserBack = new();
    private ProjectTemplateId _selectedProjectTemplate = ProjectTemplateId.DefaultRoom;
    private string _projectTemplateAutoName = string.Empty;

    private void DrawProjectAssets()
    {
        if (!_showProjectAssets)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(layout.LeftWidth, display.Y - layout.AssetsHeight - StatusHeight), new Vector2(display.X - layout.LeftWidth, layout.AssetsHeight));
        if (!ImGui.Begin("项目资产##资源浏览器", PanelWindowFlags & ~ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.End();
            return;
        }

        DrawPanelWindowMenu();
        _assetBrowser.Draw(_project, SetStatus, HandleAssetChanged, OpenVoxelAssetStage, InstantiatePrefabAsset, ExportVoxAsset, OpenGeneratorAsset,
            ExportQubicleAsset,
            path => FindOpenAssetReferenceNames(path).Count,
            FindOpenAssetReferenceNames, OpenPrefabAssetStage, AddVoxelAssetReferenceToScene);
        var selectedAssetPath = _assetBrowser.SelectedAssetPath;
        if (!string.Equals(selectedAssetPath, _lastAssetSelectionPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastAssetSelectionPath = selectedAssetPath;
            if (selectedAssetPath.Length > 0 && !IsAssetStage)
            {
                _selectedSceneObjectId = null;
                _selectedVoxel = null;
            }
        }

        ImGui.End();
    }

    /// <summary>
    /// 资产文件变更后的统一刷新入口。
    /// </summary>
    private void HandleAssetChanged(ProjectAssetChange change)
    {
        _assetPreviewPath = string.Empty;
        _voxelModelReferenceCacheRoot = null;
        _voxelModelReferenceCache = Array.Empty<string>();
        if (_assetPreviewTextureId != 0)
        {
            _imguiRenderer?.UnregisterTexture(_assetPreviewTextureId);
            _assetPreviewTextureId = 0;
        }

        if (_project is null) return;
        var oldPath = change.OldRelativePath.Replace('\\', '/');
        var newPath = change.NewRelativePath?.Replace('\\', '/');
        // 引用自动跟随：移动或重命名资产时，场景对象与预制体资产内的引用一并改写
        var updatedReferences = 0;
        if (newPath is not null && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            UpdateEditingAssetPath(oldPath, newPath);
            if (_sceneDocument is not null)
            {
                var activeReferences = AssetReferences.UpdateReferences(_sceneDocument, oldPath, newPath);
                updatedReferences += activeReferences;
                if (activeReferences > 0) MarkSceneDocumentDirty();
            }
            updatedReferences += UpdateSuspendedSceneReferences(oldPath, newPath);

            updatedReferences += AssetReferences.UpdateProjectReferences(
                _project.AssetsPath, oldPath, newPath);
            SetStatus(updatedReferences > 0
                ? $"已跟随更新 {updatedReferences} 处对 {oldPath} 的引用"
                : $"移动完成；未发现对 {oldPath} 的引用，请检查外部引用是否断链");
        }
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal) &&
            string.Equals(oldPath, _activeSceneRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            if (newPath?.EndsWith(".mvox", StringComparison.OrdinalIgnoreCase) == true)
                _activeSceneRelativePath = newPath;
            MarkSuspendedSceneDirty();
        }
        // 已载入对象是独立编辑实例，资产重命名/删除不能重新载入并丢掉未保存体素和历史。
        _pipeline?.ResetAccumulation();

        if (oldPath.EndsWith(".material", StringComparison.OrdinalIgnoreCase) ||
            newPath?.EndsWith(".material", StringComparison.OrdinalIgnoreCase) == true)
        {
            ReloadMaterialTable();
        }
    }

    /// <summary>
    /// 绘制已启用插件注册的面板
    /// </summary>
    private void DrawPluginPanels()
    {
        var panels = _pluginRuntime?.Panels ?? [];
        foreach (var panel in panels)
        {
            if (!_visiblePluginPanels.Contains(panel.Id)) continue;
            var open = true;
            if (!ImGui.Begin($"{panel.Name}##PluginPanel:{panel.Id}", ref open, PanelWindowFlags))
            {
                ImGui.End();
                continue;
            }
            ImGui.TextUnformatted($"插件：{panel.PluginId}");
            ImGui.Separator();
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(panel.Content) ? "插件未提供面板说明。" : panel.Content);
            ImGui.PopTextWrapPos();
            var commands = _pluginRuntime?.Commands.Where(command => command.PluginId == panel.PluginId).ToArray() ?? [];
            if (commands.Length > 0)
            {
                ImGui.Separator();
                var input = _pluginCommandInputs.GetValueOrDefault(panel.PluginId, "{}");
                ImGui.SetNextItemWidth(-1F);
                if (ImGui.InputTextMultiline($"##PluginArguments:{panel.Id}", ref input, 65536,
                    new Vector2(-1F, ImGui.GetTextLineHeightWithSpacing() * 3F)))
                    _pluginCommandInputs[panel.PluginId] = input;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("命令参数，使用 JSON 格式；默认为空对象。");
                foreach (var command in commands)
                {
                    ImGui.PushID(command.Id);
                    if (ImGui.Button(command.Name))
                    {
                        var arguments = input;
                        _pendingActions.Enqueue(() => ExecutePluginCommand(command.Id, arguments));
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(command.Id);
                        ImGui.EndTooltip();
                    }
                    ImGui.PopID();
                }
            }
            if (_pluginCommandResults.TryGetValue(panel.PluginId, out var result))
            {
                ImGui.Separator();
                ImGui.TextUnformatted("最近一次命令结果");
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(result);
                ImGui.PopTextWrapPos();
            }
            ImGui.End();
            if (!open) _visiblePluginPanels.Remove(panel.Id);
        }
    }

    private void DrawStatusBar()
    {
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(0F, display.Y - StatusHeight), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(display.X, StatusHeight), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        ImGui.Begin("##MVoxelStatusBar", flags);
        var exportCount = _exportQueue.Count + (_activeExport is null ? 0 : 1) + _videoEncodings.Count;
        var saveState = _project is null ? "未打开项目" : IsAssetStage
            ? $"资产{(_sceneDocumentDirty ? "未保存" : "已保存")}{(HasSuspendedSceneChanges ? " · 场景未保存" : "")}"
            : _sceneDocumentDirty ? "未保存" : "已保存";
        var status = string.IsNullOrWhiteSpace(_statusText) ? saveState : $"{saveState} · {_statusText}";
        var statusWidth = Math.Max(1F, ImGui.GetContentRegionAvail().X - (exportCount > 0 ? 360F : 0F));
        var statusPosition = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().PushClipRect(statusPosition, statusPosition + new Vector2(statusWidth, ImGui.GetTextLineHeight()), true);
        ImGui.TextUnformatted(status);
        ImGui.GetWindowDrawList().PopClipRect();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(status);
        if (exportCount > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(8F, display.X - 360F));
            ImGui.TextDisabled($"导出队列 {exportCount}");
            ImGui.SameLine();
            var progress = _activeExport is null ? 0F : ExportProgress(_activeExport);
            ImGui.ProgressBar(progress, new Vector2(120F, 0F));
            ImGui.SameLine();
            if (ImGui.SmallButton(_exportQueueExpanded ? "收起" : "展开")) _exportQueueExpanded = !_exportQueueExpanded;
        }
        ImGui.End();
        if (_exportQueueExpanded && exportCount > 0)
            DrawExportQueueWindow(display);
    }

    private static float MenuHeight => ImGui.GetFrameHeight();
    private static float WorkspaceTop => MenuHeight;
    private static float StatusHeight => ImGui.GetTextLineHeight() + 16F;

    private ImGuiWindowFlags PanelWindowFlags => ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.HorizontalScrollbar;

    private LayoutMetrics GetLayoutMetrics(Vector2 display)
    {
        var metrics = _layoutPreset switch
        {
            LayoutPreset.Modeling => new LayoutMetrics(310F, 360F, 250F),
            LayoutPreset.Rendering => new LayoutMetrics(250F, 400F, 300F),
            LayoutPreset.Assets => new LayoutMetrics(270F, 320F, 380F),
            LayoutPreset.Generator => new LayoutMetrics(250F, 440F, 260F),
            LayoutPreset.Analysis => new LayoutMetrics(260F, 400F, 310F),
            _ => new LayoutMetrics(300F, 360F, 280F),
        };
        var availableWidth = Math.Max(1F, display.X - metrics.LeftWidth - metrics.RightWidth);
        var availableHeight = Math.Max(1F, display.Y - WorkspaceTop - 1F - StatusHeight - metrics.AssetsHeight);
        return metrics with { CenterWidth = availableWidth, MainHeight = availableHeight };
    }

    private void SetPanelDefaults(Vector2 position, Vector2 size)
    {
        var condition = _applyPreset ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(position, condition);
        ImGui.SetNextWindowSize(size, condition);
    }

    private void SelectLayout(LayoutPreset preset)
    {
        _layoutPreset = preset;
        _activeLayoutFile = null;
        _activeLayoutName = string.Empty;
        SaveLayoutSelection($"@builtin:{preset}");
        _applyPreset = true;
        _resetDockLayoutRequested = true;
        ApplyPresetPanels(preset);
        _requestedCenterTab = preset == LayoutPreset.Rendering ? 1 : 0;
        _focusModelingTools = preset == LayoutPreset.Modeling;
        _requestedInspectorTab = preset == LayoutPreset.Modeling ? null : preset == LayoutPreset.Rendering ? 1 : 0;
    }

    private void ApplyPresetPanels(LayoutPreset preset, bool openDiagnostics = true)
    {
        _showModelingTools = true;
        _showInspector = true;
        _showRenderConfig = preset == LayoutPreset.Rendering;
        _showHierarchy = true;
        _showProjectAssets = true;
        _showGeneratorPanel = preset == LayoutPreset.Generator;
        if (!openDiagnostics) return;
        _showVoxelMeasurements = preset == LayoutPreset.Analysis;
        _showPerformanceAnalyzer = preset == LayoutPreset.Analysis;
    }

    private void ShowProjectPathDialog(bool create)
    {
        _projectPathDialogMode = create ? 1 : 2;
        if (create) _selectedProjectTemplate = ProjectTemplateId.DefaultRoom;
        _projectPathText = create
            ? Path.Combine(DefaultProjectRootPath, "新项目")
            : string.Empty;
        _projectTemplateAutoName = string.Empty;
        if (create)
        {
            _projectPathText = ImGuiWelcomeContent.ApplyTemplateProjectName(_projectPathText,
                _projectTemplateAutoName, ProjectTemplateCatalog.Get(_selectedProjectTemplate).Name, out var autoName);
            _projectTemplateAutoName = autoName ?? string.Empty;
        }
        if (!create)
        {
            var start = Directory.Exists(DefaultProjectRootPath)
                ? DefaultProjectRootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _projectBrowserPath = Path.GetFullPath(start);
            _projectBrowserSelectedPath = null;
            _projectBrowserBack.Clear();
        }
        _projectPathError = string.Empty;
    }

    private void DrawProjectPathDialog()
    {
        if (_projectPathDialogMode == 0)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var open = true;
        ImGui.SetNextWindowPos(display / 2F, ImGuiCond.Appearing, new Vector2(0.5F, 0.5F));
        ImGui.SetNextWindowSize(new Vector2(580F, _projectPathDialogMode == 2 ? 610F : 0F), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(Math.Min(580F, display.X - 48F), 0F),
            new Vector2(Math.Max(200F, display.X - 48F), Math.Max(200F, display.Y - 48F)));
        if (ImGui.Begin(_projectPathDialogMode == 1 ? "新建 MVoxel 项目" : _projectPathDialogMode == 3 ? "另存 MVoxel 项目" : "打开 MVoxel 项目", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            if (_projectPathDialogMode == 1)
            {
                DrawProjectTemplatePicker();
            }

            if (_projectPathDialogMode is 1 or 3)
            {
                _locationForm.Draw(ref _projectPathText);
            }
            else
            {
                DrawEditorProjectBrowser();
            }
            if (!string.IsNullOrWhiteSpace(_projectPathError))
            {
                ImGui.TextColored(ToColor(EditorPalette.Error), _projectPathError);
            }

            if (ImGui.Button("确认", new Vector2(100F, 34F)))
            {
                SubmitProjectPath();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(100F, 34F)))
            {
                _projectPathDialogMode = 0;
            }
        }

        ImGui.End();
        if (!open)
        {
            _projectPathDialogMode = 0;
        }
    }

    private void SubmitProjectPath()
    {
        var path = (_projectPathDialogMode == 2 ? _projectBrowserSelectedPath ?? string.Empty : _projectPathText)
            .Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            _projectPathError = _projectPathDialogMode == 2 ? "请先选择一个项目文件夹。" : "请先输入项目文件夹路径。";
            return;
        }

        if (_projectPathDialogMode is 1 or 3 && !_locationForm.TryValidate(path, out _projectPathError)) return;
        if (_projectPathDialogMode == 3) BeginSaveAsProject(path);
        else if (_projectPathDialogMode == 1) BeginCreateProject(path, _selectedProjectTemplate, fromWelcome: false);
        else if (!IsProjectFolder(path)) _projectPathError = "所选文件夹不是 MVoxel 项目（缺少 Project.mvoxproj）。";
        else BeginOpenProject(path, fromWelcome: false);
    }

    private void DrawEditorProjectBrowser()
    {
        ImGui.TextDisabled(_projectBrowserPath);
        ImGui.SameLine();
        if (ImGui.Button("^##EditorBrowserUp", new Vector2(34F, 28F))) NavigateEditorProjectUp();
        ImGui.SameLine();
        if (ImGui.Button("<##EditorBrowserBack", new Vector2(34F, 28F))) NavigateEditorProjectBack();
        ImGui.BeginChild("##EditorProjectBrowserFolders", new Vector2(0F, 390F), ImGuiChildFlags.Borders);
        try
        {
            var directories = Directory.EnumerateDirectories(_projectBrowserPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase).ToArray();
            foreach (var directory in directories)
            {
                var isProject = IsProjectFolder(directory);
                var selected = string.Equals(_projectBrowserSelectedPath, directory, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"目录  {Path.GetFileName(directory)}{(isProject ? "    · MVoxel 项目" : string.Empty)}##{directory}",
                        selected, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0F, 30F)))
                {
                    _projectBrowserSelectedPath = directory;
                    _projectPathError = string.Empty;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) EnterEditorProjectDirectory(directory);
                }
            }
            if (directories.Length == 0) ImGui.TextDisabled("当前文件夹没有可浏览的子文件夹");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ImGui.TextColored(ToColor(EditorPalette.Error), "无法读取当前文件夹：" + exception.Message);
        }
        ImGui.EndChild();
        if (_projectBrowserSelectedPath is not null)
            ImGui.TextDisabled(IsProjectFolder(_projectBrowserSelectedPath) ? "已选择 MVoxel 项目" : "已选择普通文件夹");
    }

    private void EnterEditorProjectDirectory(string path)
    {
        _projectBrowserBack.Push(_projectBrowserPath);
        _projectBrowserPath = Path.GetFullPath(path);
        _projectBrowserSelectedPath = null;
    }

    private void NavigateEditorProjectBack()
    {
        if (_projectBrowserBack.Count == 0) return;
        _projectBrowserPath = _projectBrowserBack.Pop();
        _projectBrowserSelectedPath = null;
    }

    private void NavigateEditorProjectUp()
    {
        var parent = Directory.GetParent(_projectBrowserPath)?.FullName;
        if (parent is null) return;
        _projectBrowserBack.Push(_projectBrowserPath);
        _projectBrowserPath = parent;
        _projectBrowserSelectedPath = null;
    }

    private static bool IsProjectFolder(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, ProjectLayout.ProjectFileName));

    /// <summary>
    /// 绘制编辑器内新建项目的模板选择器
    /// </summary>
    private void DrawProjectTemplatePicker()
    {
        var selected = ProjectTemplateCatalog.Get(_selectedProjectTemplate);
        if (ImGui.BeginCombo("项目模板", selected.Name))
        {
            string? category = null;
            foreach (var template in ProjectTemplateCatalog.All)
            {
                var nextCategory = ProjectTemplateCatalog.CategoryFor(template.Id);
                if (category != nextCategory)
                {
                    category = nextCategory;
                    ImGui.SeparatorText(category);
                }
                var isSelected = template.Id == _selectedProjectTemplate;
                if (ImGui.Selectable(template.Name, isSelected))
                {
                    _selectedProjectTemplate = template.Id;
                    _projectPathText = ImGuiWelcomeContent.ApplyTemplateProjectName(_projectPathText,
                        _projectTemplateAutoName, template.Name, out var autoName);
                    if (autoName is not null) _projectTemplateAutoName = autoName;
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{selected.Description}\n规模：{selected.Scale}\n重点：{selected.Focus}\n学习目标：{selected.LearningGoal}");
        TemplateLearningPreview.Draw(_selectedProjectTemplate);
    }

    private static string GetNewProjectPath(string requestedPath)
    {
        if (CanCreateProjectAt(requestedPath)) return requestedPath;

        var parent = Directory.GetParent(requestedPath)?.FullName
                     ?? throw new IOException("无法为新项目确定父目录");
        var name = new DirectoryInfo(requestedPath).Name;
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(parent, $"{name} {index}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }

        throw new IOException("无法为新项目找到可用的项目文件夹");
    }

    private static bool CanCreateProjectAt(string path)
    {
        if (File.Exists(path)) return false;
        return !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
    }

    private void BeginCreateProject(string path, ProjectTemplateId templateId, bool fromWelcome)
    {
        if (DeferProjectReplacement(() => BeginCreateProject(path, templateId, fromWelcome))) return;
        try
        {
            var absolutePath = Path.GetFullPath(path.Trim().Trim('"'));
            absolutePath = GetNewProjectPath(absolutePath);

            var name = new DirectoryInfo(absolutePath).Name;
            if (!_projectOperation.TryStart(
                    "正在创建 MVoxel 项目",
                    "正在检查项目位置…",
                    (cancellation, report) =>
                    {
                        using var createPhase = StartupTimeline.Begin("新建·后台准备");
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在建立标准资产目录…", .34F, CanCancel: true));
                        cancellation.ThrowIfCancellationRequested();
                        // 从这里开始由 ProjectLayout 使用原子文件写入
                        // 完成小规模提交比留下半成品项目更安全
                        report(new BlockingOperationProgress("正在写入项目文件…", .68F, CanCancel: false));
                        ProjectHandle project;
                        using (StartupTimeline.Begin("新建·写入项目骨架"))
                        {
                            project = ProjectLayout.Create(absolutePath, name);
                        }

                        Dictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff = null;                        using (StartupTimeline.Begin("新建·生成模板内容"))
                        {
                            handoff = new Dictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>();
                            ProjectTemplateCatalog.Populate(project, templateId,
                                (detail, fraction, canCancel) => report(new BlockingOperationProgress(detail, fraction, canCancel)),
                                handoff);
                        }

                        using (StartupTimeline.Begin("新建·写入编辑器设置"))
                        {
                            ApplyNewProjectEditorSettings(project, templateId == ProjectTemplateId.Empty);
                        }

                        // 模板刚写出的内容就在磁盘上，接着在这里读回来：
                        // 交付给界面的已经是解好码、合好世界的完整结果，
                        // 完成回调里不再同步读模型，进度条消失之后也不再卡一下。
                        // 传入手表让预读直接接管模板刚生成的体素，省掉写盘再解析的往返
                        var prepared = PrepareFreshProject(project, cancellation, report, handoff);
                        report(new BlockingOperationProgress("正在完成项目初始化…", .98F, CanCancel: false));
                        return prepared;
                    },
                    prepared => OpenProject(prepared.Project, prepared.Models),
                    exception => ShowProjectOperationError("创建项目失败", exception, fromWelcome, 1, path),
                    () => SetStatus("已取消创建项目")))
            {
                SetStatus("已有项目操作正在进行");
                return;
            }

            _projectPathDialogMode = 0;
            _projectPathError = string.Empty;
            SetStatus("正在创建项目…");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowProjectOperationError("创建项目失败", exception, fromWelcome, editorDialogMode: 1, path);
        }
    }

    /// <summary>
    /// 在后台把刚创建好的项目读一遍，交出可直接进编辑器的完整结果
    /// </summary>
    /// <remarks>
    /// 新建后如果不在这里读，界面回调里就要同步读一遍刚写出的模型并合成世界，
    /// 大模板会在进度条消失之后再卡两秒；打开已有项目本来就走这条后台准备流程
    /// </remarks>
    /// <param name="project">刚创建的项目</param>
    /// <param name="cancellation">取消令牌</param>
    /// <param name="report">进度回调</param>
    /// <returns>项目句柄与预读结果</returns>
    private static PreparedProjectLoad PrepareFreshProject(ProjectHandle project,
        CancellationToken cancellation, Action<BlockingOperationProgress> report,
        IReadOnlyDictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff = null)
    {
        try
        {
            return ReadFreshProject(project, cancellation, report, handoff);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            // 预读失败不该挡住新建：退回应用阶段按需读取，行为与改造前一致
            MVoxelLog.Error("新建项目的体素预读失败，将在应用时重新读取", exception);
            return new PreparedProjectLoad(project, null);
        }
    }

    /// <summary>
    /// 新建项目的后台预读主体
    /// </summary>
    /// <param name="project">刚创建的项目</param>
    /// <param name="cancellation">取消令牌</param>
    /// <param name="report">进度回调</param>
    /// <returns>项目句柄与预读结果</returns>
    private static PreparedProjectLoad ReadFreshProject(ProjectHandle project,
        CancellationToken cancellation, Action<BlockingOperationProgress> report,
        IReadOnlyDictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff)
    {
        report(new BlockingOperationProgress("正在读取材质表…", .90F, true));
        IReadOnlyList<MaterialAsset>? materials = null;
        using (StartupTimeline.Begin("新建·读取材质"))
        {
            try
            {
                materials = MaterialAsset.LoadFromProject(project);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MVoxelLog.Error("新建项目的材质表预读失败，将在应用时重试", exception);
            }
        }

        report(new BlockingOperationProgress("正在读取体素资产…", .92F, true));
        PreloadedModels models;
        using (StartupTimeline.Begin("新建·预读场景与模型"))
        {
            models = PreloadProjectModels(project, cancellation,
                progress => report(new BlockingOperationProgress(progress.Detail,
                    .92F + progress.Fraction * .06F, progress.CanCancel)),
                materials: materials, handoff: handoff);
        }

        return new PreparedProjectLoad(project, models);
    }

    /// <summary>
    /// 把编辑器默认设置写入新项目。空项目模板还要套用更暗的渲染默认值，
    /// 避免刚创建时相机和光照设置与示例场景不匹配
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="useEmptyTemplateRenderDefaults">是否套用空项目模板的渲染默认值</param>
    private void ApplyNewProjectEditorSettings(ProjectHandle project, bool useEmptyTemplateRenderDefaults)
    {
        var projectSettings = SettingsStore.LoadProject(project);
        projectSettings.ShowGrid = _editorSettings.ShowSceneGrid;
        projectSettings.ShowVoxelOutlines = _editorSettings.ShowVoxelOutlines;
        projectSettings.SceneMoveSpeed = _editorSettings.SceneMoveSpeed;
        projectSettings.SceneNearClip = _editorSettings.SceneNearClip;
        projectSettings.SceneFarClip = _editorSettings.SceneFarClip;
        projectSettings.SceneFieldOfView = _editorSettings.SceneFieldOfView;
        if (useEmptyTemplateRenderDefaults)
        {
            projectSettings.SceneRenderSettings = _editorSettings.SceneRenderSettings.CloneForExport();
            projectSettings.RenderSettings = _editorSettings.RenderViewSettings.CloneForExport();
            projectSettings.RenderSettings.ShowGround = false;
            projectSettings.RenderSettings.SunEnabled = false;
            projectSettings.RenderSettings.SkyAmbient = .16F;
            projectSettings.RenderSettings.PathTracingEnabled = false;
        }
        projectSettings.ExportRenderSettings = _editorSettings.ExportRenderSettings;
        // 硬件加速是项目级设置：新项目默认自动跟随设备能力，用户之后关掉会记在本项目
        projectSettings.RenderSettings.HardwareRayTracingAuto = true;
        projectSettings.ExportRenderSettings.HardwareRayTracingAuto = true;
        SettingsStore.SaveProject(project, projectSettings);
    }

    /// <summary>
    /// 以 MagicaVoxel .vox 文件为内容创建项目：项目名取文件名，位置取默认项目根目录
    /// </summary>
    /// <param name="voxFilePath">MagicaVoxel 文件路径</param>
    /// <param name="fromWelcome">是否由欢迎界面发起，用于失败后回到对应入口</param>
    private void BeginCreateVoxProject(string voxFilePath, bool fromWelcome)
    {
        if (DeferProjectReplacement(() => BeginCreateVoxProject(voxFilePath, fromWelcome))) return;
        try
        {
            var absoluteVoxPath = Path.GetFullPath(voxFilePath.Trim().Trim('"'));
            if (!File.Exists(absoluteVoxPath))
            {
                ShowProjectOperationError("新建项目失败",
                    new FileNotFoundException($"MagicaVoxel 文件不存在：{absoluteVoxPath}", absoluteVoxPath),
                    fromWelcome, editorDialogMode: 1, absoluteVoxPath);
                return;
            }

            if (!string.Equals(Path.GetExtension(absoluteVoxPath), ".vox", StringComparison.OrdinalIgnoreCase))
            {
                ShowProjectOperationError("新建项目失败",
                    new InvalidDataException($"只支持 MagicaVoxel .vox 文件：{Path.GetFileName(absoluteVoxPath)}"),
                    fromWelcome, editorDialogMode: 1, absoluteVoxPath);
                return;
            }

            var projectName = Path.GetFileNameWithoutExtension(absoluteVoxPath).Trim();
            if (projectName.Length == 0) projectName = "导入模型";
            var absolutePath = GetNewProjectPath(Path.Combine(DefaultProjectRootPath, projectName));
            //路径是本次新建的空文件夹时，导入失败要把它删掉，避免留下半个项目占用名字
            var projectFolderExisted = Directory.Exists(absolutePath);
            if (!_projectOperation.TryStart(
                    "正在从 MagicaVoxel 文件创建项目",
                    "正在检查 MagicaVoxel 文件…",
                    (cancellation, report) =>
                    {
                        using var createPhase = StartupTimeline.Begin("新建·后台准备");
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在建立标准资产目录…", .34F, CanCancel: true));
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在写入项目文件…", .62F, CanCancel: false));
                        ProjectHandle project;
                        using (StartupTimeline.Begin("新建·写入项目骨架"))
                        {
                            project = ProjectLayout.Create(absolutePath, new DirectoryInfo(absolutePath).Name);
                        }

                        Dictionary<string, ProjectTemplateCatalog.TemplateWorldHandoff>? handoff = null;
                        try
                        {
                            using (StartupTimeline.Begin("新建·生成模板内容"))
                            {
                                handoff = [];
                                ProjectTemplateCatalog.PopulateFromVox(project, absoluteVoxPath,
                                    (detail, fraction, canCancel) => report(new BlockingOperationProgress(detail, fraction, canCancel)),
                                    handoff);
                            }

                            using (StartupTimeline.Begin("新建·写入编辑器设置"))
                            {
                                ApplyNewProjectEditorSettings(project, useEmptyTemplateRenderDefaults: false);
                            }
                        }
                        catch
                        {
                            if (!projectFolderExisted) TryDeleteDirectory(absolutePath);
                            throw;
                        }

                        var prepared = PrepareFreshProject(project, cancellation, report, handoff);
                        report(new BlockingOperationProgress("正在完成项目初始化…", .98F, CanCancel: false));
                        return prepared;
                    },
                    prepared => OpenProject(prepared.Project, prepared.Models),
                    exception => ShowProjectOperationError("创建项目失败", exception, fromWelcome, 1, absolutePath),
                    () => SetStatus("已取消创建项目")))
            {
                SetStatus("已有项目操作正在进行");
                return;
            }

            _projectPathDialogMode = 0;
            _projectPathError = string.Empty;
            SetStatus($"正在用 {Path.GetFileName(absoluteVoxPath)} 创建项目…");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            ShowProjectOperationError("创建项目失败", exception, fromWelcome, editorDialogMode: 1, voxFilePath);
        }
    }

    /// <summary>
    /// 尽力删除本次新建的项目文件夹，失败时保留现场供用户手动处理
    /// </summary>
    /// <param name="path">项目根目录</param>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Warn($"清理未完成的项目文件夹失败：{path}，{exception.Message}");
        }
    }

    private void BeginOpenProject(string path, bool fromWelcome)
    {
        if (DeferProjectReplacement(() => BeginOpenProject(path, fromWelcome))) return;
        try
        {
            var absolutePath = Path.GetFullPath(path.Trim().Trim('"'));
            if (!_projectOperation.TryStart(
                    "正在加载 MVoxel 项目",
                    "正在检查项目文件…",
                    (cancellation, report) =>
                    {
                        using var openPhase = StartupTimeline.Begin("打开·后台准备");
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在读取项目清单…", .18F, CanCancel: true));
                        ProjectHandle project;
                        using (StartupTimeline.Begin("打开·读取清单"))
                        {
                            project = ProjectLayout.Open(absolutePath);
                        }

                        cancellation.ThrowIfCancellationRequested();
                        // 材质表先读：模型预读要用它构建运行时槽位映射，
                        // 才能在后台把场景视图世界也一并合成好
                        report(new BlockingOperationProgress("正在读取材质表…", .20F, true));
                        IReadOnlyList<MaterialAsset>? materials = null;
                        using (StartupTimeline.Begin("打开·读取材质"))
                        {
                            try
                            {
                                materials = MaterialAsset.LoadFromProject(project);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                            {
                                MVoxelLog.Error("材质表预读失败，将在应用时重试", exception);
                            }
                        }

                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在读取体素资产…", .22F, CanCancel: true));
                        void ReportModels(BlockingOperationProgress progress) => report(progress with
                        {
                            Fraction = .18F + (progress.Fraction - .18F) * .65F,
                        });
                        // 恢复快照优先：有可用的恢复数据时只准备恢复场景。
                        // 先完整准备正式场景再准备恢复场景，会白算一套最终不进画面的世界
                        report(new BlockingOperationProgress("正在检查恢复数据…", .63F, true));
                        var preload = PreloadForOpen(project, cancellation, ReportModels, materials, out var snapshot);

                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在启动项目插件…", .88F, true));
                        var runtime = new PluginScriptRuntime();
                        try
                        {
                            using (StartupTimeline.Begin("打开·启动插件"))
                            {
                                runtime.Activate(project, TimeSpan.FromSeconds(2), cancellation);
                            }

                            cancellation.ThrowIfCancellationRequested();
                            report(new BlockingOperationProgress("正在应用编辑器工作区…", .96F, false));
                            return new PreparedOpenProject(project, preload, snapshot, materials, runtime);
                        }
                        catch { runtime.Dispose(); throw; }
                    },
                    prepared => OpenProject(prepared.Project, prepared.Models, prepared),
                    exception => ShowProjectOperationError("打开项目失败", exception, fromWelcome, 2, path),
                    () => SetStatus("已取消加载项目")))
            {
                SetStatus("已有项目操作正在进行");
                return;
            }

            _projectPathDialogMode = 0;
            _projectPathError = string.Empty;
            SetStatus("正在加载项目…");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowProjectOperationError("打开项目失败", exception, fromWelcome, editorDialogMode: 2, path);
        }
    }

    private void BeginDiagnosticLoad()
    {
        _projectOperation.TryStart(
            "正在加载项目资产",
            "正在准备加载诊断…",
            (cancellation, report) =>
            {
                for (var step = 1; step <= 8; step++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Thread.Sleep(38);
                    report(new BlockingOperationProgress($"正在加载资产索引（{step}/8）…", .10F + step * .10F, CanCancel: true));
                }

                return true;
            },
            _ => SetStatus("加载诊断已完成"),
            exception => SetStatus($"加载诊断失败：{exception.Message}"),
            () => SetStatus("加载诊断已取消"));
    }

    private static string FindStartupSceneRelativePath(ProjectHandle project)
    {
        const string main = "Scenes/主场景.mvox";
        var example = "Scenes/" + ExampleSceneTemplate.FileName;
        if (File.Exists(Path.Combine(project.AssetsPath, main.Replace('/', Path.DirectorySeparatorChar)))) return main;
        if (File.Exists(Path.Combine(project.AssetsPath, example.Replace('/', Path.DirectorySeparatorChar)))) return example;
        if (!Directory.Exists(project.AssetsPath)) return main;
        var found = Directory.EnumerateFiles(project.AssetsPath, "*.mvox", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
        }).Min(StringComparer.OrdinalIgnoreCase);
        return found is null ? main : Path.GetRelativePath(project.AssetsPath, found).Replace('\\', '/');
    }

    /// <summary>
    /// 打开项目时的体素预读：优先用恢复区快照，读不出来再退回正式场景
    /// </summary>
    /// <remarks>
    /// 恢复快照存在时最终画面用的是恢复场景，先准备正式场景等于白算一整套世界。
    /// 恢复数据损坏或引用的资产已经不在了，才退回正式场景，回退语义保持不变
    /// </remarks>
    /// <param name="project">目标项目</param>
    /// <param name="cancellation">取消令牌</param>
    /// <param name="report">进度回调</param>
    /// <param name="materials">项目材质资产，可为空</param>
    /// <param name="snapshot">实际采用的恢复快照，没有恢复数据时为空</param>
    /// <returns>要显示的那个场景的预读结果</returns>
    private static PreloadedModels PreloadForOpen(ProjectHandle project, CancellationToken cancellation,
        Action<BlockingOperationProgress> report, IReadOnlyList<MaterialAsset>? materials,
        out RecoverySnapshot? snapshot)
    {
        snapshot = null;
        if (RecoveryStore.TryLoad(project.RootPath, out var loaded) && loaded is not null)
        {
            try
            {
                using (StartupTimeline.Begin("打开·预读恢复数据"))
                {
                    var scene = Scene.SceneSerializer.LoadFromJson(loaded.SceneJson);
                    var recovered = PreloadProjectModels(project, cancellation, report, scene, materials: materials);
                    snapshot = loaded;
                    return recovered;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or InvalidDataException or JsonException)
            {
                snapshot = null;
                MVoxelLog.Error("恢复区快照预读失败，改用正式场景", exception);
            }
        }

        using (StartupTimeline.Begin("打开·预读场景与模型"))
        {
            return PreloadProjectModels(project, cancellation, report, materials: materials);
        }
    }

    private void OpenProject(ProjectHandle project, PreloadedModels? preloadedModels = null, PreparedOpenProject? prepared = null)
    {
        using var applyPhase = StartupTimeline.Begin("打开·应用项目状态");
        _worldLoadFailed = false;
        ResetGeneratorPanel();
        _pluginRuntime?.Dispose();
        _pluginCommandResults.Clear();
        _pluginCommandInputs.Clear();
        _pluginRuntime = prepared?.Runtime ?? new PluginScriptRuntime();
        ResetTimelineWeather();
        _project = project;
        _activeSceneRelativePath = preloadedModels?.ScenePath ?? FindStartupSceneRelativePath(project);
        _assetBrowser.Reset();
        _assetBrowser.EditingAssetPath = string.Empty;
        _voxelModelReferenceCacheRoot = null;
        _voxelModelReferenceCache = Array.Empty<string>();
        _lastAssetSelectionPath = string.Empty;
        RecentProjects.Record(project);
        try
        {
            LoadProjectSettings(project);
        }
        catch (InvalidDataException exception)
        {
            _pluginRuntime.Dispose();
            _pluginRuntime = null;
            _project = null;
            _statusText = $"项目设置格式不受支持：{exception.Message}";
            SetStatus(_statusText);
            RequestWelcomeWindow();
            return;
        }
        _autoSaveCountdown = _editorSettings.AutoSaveSeconds;
        _autoSaveQueued = false;
        LoadWorldForProject(project, preloadedModels);
        if (prepared?.Snapshot is { } snapshot && preloadedModels?.Scene is not null)
        {
            // 恢复场景已经是预读时选好的那一份，这里只补上“未保存工作”的语义
            _voxelHistory.Clear();
            MarkSceneDocumentDirty();
            FrameCameraOnWorld();
            SetStatus($"已从恢复区还原未保存工作（{snapshot.SavedAt.ToLocalTime():MM-dd HH:mm}），确认无误后请正式保存");
            MVoxelLog.Info($"已套用恢复区快照：{project.Manifest.Name}，保存于 {snapshot.SavedAt:O}");
        }
        else if (prepared is null) TryRestoreRecovery(project);
        var synchronizedPrefabs = SyncPendingPrefabRevisions();
        if (synchronizedPrefabs > 0) MVoxelLog.Info($"已合并 {synchronizedPrefabs} 个场景预制体实例的资产更新");
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        _inspectorLockedObjectId = null;
        if (prepared?.Materials is { } openMaterials)
        {
            ApplyPreloadedMaterials(openMaterials, prepared.Models.MaterialMap);
        }
        else if (preloadedModels?.Materials is { } freshMaterials)
        {
            ApplyPreloadedMaterials(freshMaterials, preloadedModels.MaterialMap);
        }
        else ReloadMaterialTable();
        if (prepared is null) _pluginRuntime.Activate(project, TimeSpan.FromSeconds(2));
        _statusText = _sceneValidationErrors.Count == 0
            ? $"已打开项目：{project.Manifest.Name}"
            : $"已打开项目：{project.Manifest.Name}（主场景存在 {_sceneValidationErrors.Count} 个结构问题：{_sceneValidationErrors[0]}）";
        ProjectOpened?.Invoke(this, EventArgs.Empty);
        StartupTimeline.Mark(StartupTimeline.MilestoneProjectReady);
    }

    private bool TryOpenProject(string path, bool showError)
    {
        try
        {
            OpenProject(ProjectLayout.Open(path));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            _statusText = "项目不可用";
            if (showError)
            {
                ShowProjectError("打开项目失败", exception);
            }

            return false;
        }
    }

    private void CloseProject()
    {
        CloseMcImport();
        _pickWorld = null;
        _worldLoadFailed = false;
        _pluginRuntime?.Dispose();
        _pluginRuntime = null;
        SaveProjectSettings();
        _project = null;
        _lastProjectSettingsSnapshot = string.Empty;
        _sceneDocument = null;
        _activeSceneRelativePath = "Scenes/主场景.mvox";
        _sceneDocumentDirty = false;
        _renderCameraId = null;
        _renderOrbitAnchor = null;
        _renderCameraInitialFramePending = true;
        _assetBrowser.Reset();
        _assetBrowser.EditingAssetPath = string.Empty;
        _lastAssetSelectionPath = string.Empty;
        _pluginCommandResults.Clear();
        _pluginCommandInputs.Clear();
        ResetGeneratorPanel();
        _world = new VoxelWorld();
        _layerOwnership.Clear();
        _objectVoxels.Clear();
        _voxelHistory.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
        _pinnedVoxelModelId = null;
        _palette = EditorPalettes.ResolveEditorPalette();
        _paletteIsProjectLevel = false;
        _selectedProjectPalettePath = EditorPalettes.ProjectPaletteRelativePath;
        _autoSaveCountdown = float.PositiveInfinity;
        _autoSaveQueued = false;
        ApplyEditorSettingsToUi();
        SyncSettingsToggles();
        _statusText = "已关闭项目";
        RequestWelcomeWindow();
    }

    private bool _closeProjectDialogRequested;
    /// <summary>退出确认框是否处于待处理状态，供自动化读取与回答</summary>
    private bool _exitDialogOpen;
    private bool _exitDialogResolved;
    /// <summary>关闭或切换项目的确认框是否处于待处理状态</summary>
    private bool _projectCloseDialogOpen;
    private bool _projectCloseDialogResolved;

    private void DrawExitConfirmationDialog()
    {
        if (_exitConfirmationRequested)
        {
            ImGui.OpenPopup("退出编辑器##UnsavedScene");
            _exitConfirmationRequested = false;
        }

        if (CenteredModal.Begin("退出编辑器##UnsavedScene", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("当前项目有未保存的场景或生成器源码。退出前要保存吗？");
            ImGui.Spacing();
            if (ImGui.Button("保存并退出", new Vector2(120F, 30F))) ConfirmExitSave();
            ImGui.SameLine();
            if (ImGui.Button("不保存退出", new Vector2(120F, 30F))) ConfirmExitDiscard();
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90F, 30F))) CancelExitConfirmation();
            if (_exitDialogResolved)
            {
                _exitDialogResolved = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        //按 Esc 或点外部关闭同样视为取消，避免确认状态一直挂着
        if (_exitDialogOpen && !ImGui.IsPopupOpen("退出编辑器##UnsavedScene")) _exitDialogOpen = false;
    }

    /// <summary>保存场景与生成器源码后退出，仍有未保存内容时保持弹窗</summary>
    private void ConfirmExitSave()
    {
        SaveProject();
        if (_generatorDirty) SaveGeneratorSource();
        if (_sceneDocumentDirty || _generatorDirty) return;
        _exitDialogResolved = true;
        _exitDialogOpen = false;
        _allowWindowClose = true;
        _pendingActions.Enqueue(Close);
    }

    /// <summary>放弃未保存内容直接退出</summary>
    private void ConfirmExitDiscard()
    {
        _exitDialogResolved = true;
        _exitDialogOpen = false;
        _allowWindowClose = true;
        _pendingActions.Enqueue(Close);
    }

    /// <summary>取消退出，保留当前项目</summary>
    private void CancelExitConfirmation()
    {
        _exitDialogResolved = true;
        _exitDialogOpen = false;
        _exitConfirmationRequested = false;
    }

    private void RequestCloseProject()
    {
        if (IsAssetStage) { SetStatus("请先从资产编辑返回场景"); return; }
        if (_project is not null && TimelineIsDirty)
        {
            ConfirmTimelineChangesBefore(RequestCloseProject);
            return;
        }
        _afterProjectClose = null;
        if (_project is null) return;
        if (_sceneDocumentDirty || _generatorDirty)
        {
            RequestCloseProjectDialog();
            return;
        }

        CloseProject();
    }

    private void RequestCloseProjectDialog()
    {
        _closeProjectDialogRequested = true;
        _projectCloseDialogOpen = true;
        _projectCloseDialogResolved = false;
    }

    private void DrawCloseProjectDialog()
    {
        if (_closeProjectDialogRequested)
        {
            ImGui.OpenPopup("关闭项目##UnsavedScene");
            _closeProjectDialogRequested = false;
        }

        if (CenteredModal.Begin("关闭项目##UnsavedScene", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("当前项目有未保存的场景或生成器源码。请选择如何处理这些修改。");
            ImGui.Spacing();
            if (ImGui.Button(_afterProjectClose is null ? "保存并关闭" : "保存并继续", new Vector2(120F, 30F)))
                ConfirmCloseProjectSave();
            ImGui.SameLine();
            if (ImGui.Button(_afterProjectClose is null ? "不保存关闭" : "不保存继续", new Vector2(120F, 30F)))
                ConfirmCloseProjectDiscard();
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90F, 30F))) CancelCloseProjectConfirmation();
            if (_projectCloseDialogResolved)
            {
                _projectCloseDialogResolved = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (_projectCloseDialogOpen && !ImGui.IsPopupOpen("关闭项目##UnsavedScene"))
        {
            _projectCloseDialogOpen = false;
            _afterProjectClose = null;
        }
    }

    /// <summary>保存后关闭项目；未保存干净时保持弹窗</summary>
    private void ConfirmCloseProjectSave()
    {
        SaveProject();
        if (_generatorDirty) SaveGeneratorSource();
        if (_sceneDocumentDirty || _generatorDirty) return;
        _projectCloseDialogResolved = true;
        _projectCloseDialogOpen = false;
        CompleteProjectClose();
    }

    /// <summary>放弃修改后关闭项目或继续切换</summary>
    private void ConfirmCloseProjectDiscard()
    {
        _projectCloseDialogResolved = true;
        _projectCloseDialogOpen = false;
        CompleteProjectClose();
    }

    /// <summary>取消关闭或切换，保留当前项目</summary>
    private void CancelCloseProjectConfirmation()
    {
        _projectCloseDialogResolved = true;
        _projectCloseDialogOpen = false;
        _afterProjectClose = null;
    }

    private void SaveProject(bool automatic = false)
    {
        if (IsAssetStage) { SaveAssetStage(); return; }
        using var historySuspended = FileEditHistory.Suspend();
        if (_project is null)
        {
            if (!automatic) SetStatus("没有可保存的项目");
            return;
        }

        try
        {
            if (_worldLoadFailed)
            {
                _sceneDocumentDirty = true;
                SetStatus("模型加载失败，已阻止覆盖保存；请修复资产引用后重新打开项目");
                return;
            }
            ProjectLayout.EnsureDirectories(_project.RootPath);
            SaveProjectSettings();
            if (!SaveWorldToProject(_project))
            {
                _sceneDocumentDirty = true;
                return;
            }
            RecoveryStore.Clear(_project.RootPath);
            _sceneDocumentDirty = false;
            _autoSaveCountdown = _editorSettings.AutoSaveSeconds;
            SetStatus(automatic
                ? $"项目已自动保存：{_project.Manifest.Name}"
                : $"项目已保存：{_project.Manifest.Name}（场景与体素世界，{_world.OccupiedVoxelCount} 体素）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _sceneDocumentDirty = true;
            MVoxelLog.Error("保存项目失败", exception);
            SetStatus($"保存未完成，已保留未保存状态：{exception.Message}");
        }
    }

    private void TryAutoSaveProject()
    {
        using var historySuspended = FileEditHistory.Suspend();
        _autoSaveQueued = false;
        if (_project is null || _worldLoadFailed || !_editorSettings.AutoSave || !_sceneDocumentDirty) return;
        if (IsAssetStage)
        {
            // 恢复区属于原场景，不能用资产编辑空间覆盖；仅项目自动保存模式写回资产。
            if (_editorSettings.AutoSaveMode == AutoSaveMode.Project) SaveAssetStage();
            _autoSaveCountdown = _editorSettings.AutoSaveSeconds;
            return;
        }
        if (_editorSettings.AutoSaveMode == AutoSaveMode.Project)
        {
            SaveProject(automatic: true);
            return;
        }

        try
        {
            // 恢复快照包含各对象的独立体素，不再生成冗余的合成世界副本。
            RecoveryStore.Write(_project.RootPath, CreateObjectRecoveryJson());
            _autoSaveCountdown = _editorSettings.AutoSaveSeconds;
            SetStatus("已保存快照到恢复区（正式保存后自动清理）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("自动保存恢复区快照失败", exception);
            SetStatus($"自动保存失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 打开项目后尝试套用恢复区快照，存在即视为比正式保存更新的工作，载入并标记为未保存
    /// </summary>
    private void TryRestoreRecovery(ProjectHandle project)
    {
        if (!RecoveryStore.TryLoad(project.RootPath, out var snapshot) || snapshot is null) return;
        try
        {
            var restored = Scene.SceneSerializer.LoadFromJson(snapshot.SceneJson);
            _sceneDocument = restored;
            LoadObjectVoxelWorlds(project);
            _voxelHistory.Clear();
            MarkSceneDocumentDirty();
            SetStatus($"已从恢复区还原未保存工作（{snapshot.SavedAt.ToLocalTime():MM-dd HH:mm}），确认无误后请正式保存");
            MVoxelLog.Info($"已套用恢复区快照：{project.Manifest.Name}，保存于 {snapshot.SavedAt:O}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            MVoxelLog.Error("恢复区快照套用失败，已按正式保存内容打开", exception);
            SetStatus($"恢复区快照不可用，已打开正式保存内容：{exception.Message}");
        }
    }

    private void RefreshProjectAssets()
    {
        _assetBrowser.Reset();
        _lastAssetSelectionPath = string.Empty;
        SetStatus(_project is null ? "资源视图已刷新：未打开项目" : "资产视图已刷新");
    }

    private void ValidateCurrentScene()
    {
        if (_project is null || _sceneDocument is null)
        {
            SetStatus("没有可验证的场景");
            return;
        }

        _sceneValidationErrors = SceneValidation.Validate(_sceneDocument);
        foreach (var error in _sceneValidationErrors)
        {
            MVoxelLog.Warn($"主场景结构校验：{error}");
        }

        SetStatus(_sceneValidationErrors.Count == 0
            ? "当前主场景校验通过"
            : $"当前主场景存在 {_sceneValidationErrors.Count} 个结构问题：{_sceneValidationErrors[0]}");
    }

    private object ValidateCurrentSceneCommand()
    {
        if (_project is null || _sceneDocument is null)
            throw new InvalidOperationException("当前没有打开的项目场景");

        _sceneValidationErrors = SceneValidation.Validate(_sceneDocument);
        foreach (var error in _sceneValidationErrors)
        {
            MVoxelLog.Warn($"主场景结构校验：{error}");
        }

        return new
        {
            projectName = _project.Manifest.Name,
            sceneName = _sceneDocument.Name,
            objectCount = _sceneDocument.Objects.Count,
            componentCount = _sceneDocument.Objects.Sum(item => item.Components.Count),
            valid = _sceneValidationErrors.Count == 0,
            errors = _sceneValidationErrors,
        };
    }

    private void SetStatus(string text) => _statusText = text;

    private void ShowProjectOperationError(string title, Exception exception, bool fromWelcome,
        int editorDialogMode, string path)
    {
        _statusText = "项目不可用";
        _projectPathText = path;
        if (!fromWelcome)
        {
            _projectPathDialogMode = editorDialogMode;
        }

        ShowProjectError(title, exception);
    }

    private void ShowProjectError(string title, Exception exception)
    {
        _projectPathError = $"{title}：{exception.Message}";
        ProjectOperationFailed?.Invoke(this, new ProjectOperationFailedEventArgs(_projectPathError));
    }

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);

    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));

    private static string? ResolveStartupProjectPath(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return args.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal));
    }

    private sealed class SceneObjectStateCommand : IEditorCommand
    {
        private readonly SceneObject _target;
        private readonly SceneObject _before;
        private readonly SceneObject _after;
        private readonly Action _afterApply;

        public SceneObjectStateCommand(SceneObject target, SceneObject before, SceneObject after, Action afterApply)
        {
            _target = target;
            _before = before;
            _after = after;
            _afterApply = afterApply;
        }

        public string Name => "检查器属性修改";

        public void Execute()
        {
            Copy(_target, _after);
            _afterApply();
        }

        public void Undo()
        {
            Copy(_target, _before);
            _afterApply();
        }

        internal static void Copy(SceneObject target, SceneObject source)
        {
            target.Name = source.Name;
            target.ParentId = source.ParentId;
            target.Visible = source.Visible;
            target.Locked = source.Locked;
            target.LayerIndex = source.LayerIndex;
            target.Prefab = source.Prefab is null ? null : new PrefabInstanceData
            {
                AssetPath = source.Prefab.AssetPath,
                SourceId = source.Prefab.SourceId,
                SourceRevision = source.Prefab.SourceRevision,
                Links = new Dictionary<string, string>(source.Prefab.Links, StringComparer.Ordinal),
            };
            target.Transform = new TransformComponent(source.Transform.Mode, source.Transform.Grid, source.Transform.Free)
            {
                ScaleX = source.Transform.ScaleX,
                ScaleY = source.Transform.ScaleY,
                ScaleZ = source.Transform.ScaleZ,
            };
            target.Components = source.Components.Select(component => new ComponentRecord
            {
                Enabled = component.Enabled,
                OwnerPluginId = component.OwnerPluginId,
                TypeId = component.TypeId,
                SchemaVersion = component.SchemaVersion,
                Data = component.Data.Clone(),
            }).ToList();
        }
    }

    private readonly record struct LayoutMetrics(float LeftWidth, float RightWidth, float AssetsHeight)
    {
        public float CenterWidth { get; init; }
        public float MainHeight { get; init; }
    }

    private enum LayoutPreset
    {
        Default,
        Modeling,
        Rendering,
        Assets,
        Generator,
        Analysis,
        Custom,
    }
}
