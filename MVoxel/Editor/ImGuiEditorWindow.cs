using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Editor.Assets;
using MVoxel.Project;
using MVoxel.Core;
using MVoxel.Rendering;
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
    private int _hierarchyLayerFilter = -1;
    private readonly HashSet<string> _expandedInspectorComponents = new(StringComparer.Ordinal);
    private StableId? _inspectorExpansionObjectId;
    private bool _showProjectAssets = true;
    private bool _showInspector = true;
    private int _centerTab;
    private int? _requestedCenterTab;
    private int? _requestedInspectorTab;
    private string? _requestedRenderSettingsSection;
    private SceneDocument? _sceneDocument;
    private string _activeSceneRelativePath = "Scenes/主场景.mvox";
    private StableId? _renderCameraId;
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
    private bool _sceneMouseCaptured;
    private ViewportCaptureTarget _capturedViewport;
    private bool _sceneDocumentDirty;
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
    private readonly CommandHistory _voxelHistory = new();
    private VulkanVoxelRenderPipeline? _pipeline;
    private RenderSettings _renderSettings = new();
    private EditorTool _activeTool = EditorTool.Camera;
    private VoxelBrushShape _brushShape = VoxelBrushShape.Sphere;
    private int _brushRadius = 1;
    private int _paletteIndex = 40;
    private bool _brushEmissive;
    private string? _componentWarning;
    private VoxelDda.Hit? _selectedVoxel;
    private StableId? _selectedSceneObjectId;
    private VoxelDda.Hit? _brushPreview;
    private float _elapsedSeconds;
    private float _undoHeldSeconds;
    private float _undoRepeatAccumulator;
    private string _accumulationSignature = string.Empty;
    private string _pipelineStatus = string.Empty;
    private bool _gpuRenderingDisabled;
    private long _lastRenderGpuSubmitMs;
    private bool _renderViewNeedsInitialRender = true;
    private bool _renderCameraInitialFramePending = true;
    private bool _renderViewWasFocused;
    private Vector2 _lastRenderViewportSize;
    private Queue<ExportJob> _exportQueue = new();
    private ExportJob? _activeExport;
    private bool _exportQueueExpanded;
    private bool _cancelActiveExportRequested;
    private int _exportSequence;
    private readonly List<CpuPreviewSurfaceFace> _cpuPreviewSurfaceFaces = [];
    private VoxelWorld? _cpuPreviewWorld;
    private int _cpuPreviewWorldVersion = -1;
    private EditorControlServer? _controlServer;
    private PluginScriptRuntime? _pluginRuntime;
    private readonly HashSet<string> _visiblePluginPanels = new(StringComparer.Ordinal);

    public ImGuiEditorWindow(string[] args)
        : base(GameWindowSettings.Default, CreateWindowSettings(args))
    {
        _startupProjectPath = ResolveStartupProjectPath(args);
    }

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

    public void OpenProjectFromWelcome(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public void OpenRecentProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public bool IsProjectOperationRunning => _projectOperation.IsRunning;

    public BlockingOperationSnapshot? ProjectOperationSnapshot => _projectOperation.Snapshot;

    public void CancelProjectOperation() => _projectOperation.RequestCancel();

    public void CloseEditor() => Close();

    internal void RequestOpenProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: false));

    protected override void OnLoad()
    {
        base.OnLoad();
        CenterOnCurrentMonitor();
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
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
        InitializeVoxelPipeline();
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            TryOpenProject(_startupProjectPath, true);
        }
        else
        {
            RequestWelcomeWindow();
            EnsureWelcomeWindow();
        }
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
        if (!IsFocused || !IsCapsLockEnabled())
        {
            EndSceneMouseCapture();
        }

        while (_pendingActions.TryDequeue(out var action))
        {
            action();
        }

        if (UiDiagnostics.TakeBlockingOperationRequest()) BeginDiagnosticLoad();
        if (UiDiagnostics.TakeSettingsRequest()) OpenSettings(0, 2);
        if (UiDiagnostics.TakeExportRequest() && _centerTab == 1) ExportRenderPng();
        _projectOperation.Pump();
        EnsureWelcomeWindow();
        if (_project is not null && _projectSettings.AutoSave && _sceneDocumentDirty)
        {
            _autoSaveCountdown -= (float)Math.Max(args.Time, 0.01F);
            if (_autoSaveCountdown <= 0F && !_autoSaveQueued)
            {
                _autoSaveQueued = true;
                _autoSaveCountdown = _projectSettings.AutoSaveSeconds;
                _pendingActions.Enqueue(TryAutoSaveProject);
            }
        }
        if (_renderSettingsDirty && (_renderSettingsSaveCountdown -= (float)Math.Max(args.Time, 0.01F)) <= 0F)
        {
            _renderSettingsDirty = false;
            try
            {
                if (_project is null) SaveEditorSettings();
                else SaveProjectSettings();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"渲染配置自动保存失败：{exception.Message}");
            }
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        try
        {
            base.OnRenderFrame(args);
            ImGui.SetCurrentContext(_imguiContext);
            _fonts.Refresh(_controller!, UiDiagnostics.Dpi(this));
            _elapsedSeconds += (float)Math.Max(args.Time, 0.0);
            _controller?.Update((float)Math.Max(args.Time, 1.0 / 120.0));
            if (_controller is not null)
            {
                if (UiDiagnostics.ShouldCloseWelcome) _welcomeWindow?.RequestClose();
                DrawEditorFrame();
                _controller.Render();
                UiDiagnostics.Capture(this, "editor");
            }

            _vulkan?.RenderFrame(new System.Numerics.Vector4(0.05F, 0.07F, 0.10F, 1F), commandBuffer =>
            {
                if (_imguiRenderer is not null && _controller is not null)
                {
                    _imguiRenderer.Record(commandBuffer, _controller);
                }
            });

            ImGui.SetCurrentContext(_imguiContext);
            UiDiagnostics.Advance(this, ref _sceneOrbitYaw, ref _sceneOrbitPitch, ref _sceneCameraDistance, ref _scenePan);
        }
        catch (Exception exception)
        {
            MVoxelLog.SetRuntimeContext($"编辑器帧渲染，项目={_project?.Manifest.Name ?? "未打开项目"}，体素={_world.OccupiedVoxelCount}");
            MVoxelLog.RecordCrash("编辑器帧渲染异常", exception);
            _gpuRenderingDisabled = true;
            Close();
        }
    }

    protected override void OnUnload()
    {
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
                autoSaveEnabled = _project is not null && _projectSettings.AutoSave,
                autoSaveSeconds = _project is null ? 0 : _projectSettings.AutoSaveSeconds,
                renderingDegraded = _gpuRenderingDisabled,
                renderingStatus = _pipelineStatus,
                automations = _pluginRuntime?.Automations ?? [],
                commands = _pluginRuntime?.Commands ?? [],
                panels = _pluginRuntime?.Panels ?? [],
                pluginDiagnostics = _project is null ? Array.Empty<ProjectPluginDiagnostic>() : PluginManager.Diagnose(_project),
            },
            "scene.validate" => ValidateCurrentSceneCommand(),
            "editor.saveProject" => QueueSaveProject(),
            "editor.closeProject" => QueueCloseProject(request),
            "editor.openProject" => QueueOpenProject(request),
            "editor.invokeAutomation" => InvokeAutomation(request),
            "editor.invokeCommand" => InvokeCommand(request),
            "editor.reloadPlugins" => QueueReloadPlugins(),
            "editor.installPlugin" => InstallPlugin(request),
            "editor.setPluginEnabled" => SetPluginEnabled(request),
            _ => throw new InvalidOperationException($"不支持的编辑器命令：{method}"),
        };
    }

    /// <summary>
    /// 将打开项目操作安全地投递到窗口线程
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>投递结果</returns>
    private object QueueOpenProject(JsonElement request)
    {
        var arguments = request.TryGetProperty("arguments", out var value) ? value : request;
        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目路径不能为空");
        RequestOpenProject(path);
        return new { queued = true, path };
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
    /// 将关闭项目操作投递到编辑器窗口线程，不替调用方保存场景。
    /// </summary>
    private object QueueCloseProject(JsonElement request)
    {
        if (_project is null) return new { queued = false, alreadyClosed = true, sceneDirty = false };
        var sceneDirty = _sceneDocumentDirty;
        var arguments = request.TryGetProperty("arguments", out var value) ? value : request;
        var discard = arguments.ValueKind == JsonValueKind.Object &&
                      arguments.TryGetProperty("discard", out var discardValue) && discardValue.GetBoolean();
        if (sceneDirty && !discard)
            return new { queued = false, alreadyClosed = false, sceneDirty = true, requiresSave = true };
        _pendingActions.Enqueue(CloseProject);
        return new { queued = true, alreadyClosed = false, sceneDirty, requiresSave = false };
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
        _pluginRuntime?.Dispose();
        _pluginRuntime = new PluginScriptRuntime();
        _pluginRuntime.Activate(_project, TimeSpan.FromSeconds(2));
        _statusText = "插件已重新加载";
    }

    /// <summary>
    /// 在窗口线程执行插件命令
    /// </summary>
    /// <param name="commandId">命令 ID</param>
    private void ExecutePluginCommand(string commandId)
    {
        if (_pluginRuntime is null) return;
        try
        {
            using var payload = JsonDocument.Parse("{}");
            _pluginRuntime.InvokeCommand(commandId, payload.RootElement, TimeSpan.FromSeconds(10));
            _statusText = $"已执行插件命令：{commandId}";
        }
        catch (Exception exception)
        {
            MVoxelLog.Error($"插件命令执行失败：{commandId}", exception);
            _statusText = $"插件命令执行失败：{commandId}";
        }
    }

    private void RequestWelcomeWindow() => _showWelcomeRequested = true;

    private void EnsureWelcomeWindow()
    {
        if (!_showWelcomeRequested || _welcomeWindow is not null) return;
        _showWelcomeRequested = false;
        _welcomeWindow = new ImGuiWelcomeContent(this);
        _welcomeWindow.Initialize(_imguiRenderer!, _fonts);
    }

    private static NativeWindowSettings CreateWindowSettings(string[] args)
    {
        return new NativeWindowSettings
        {
            Title = "MVoxel 体素建模",
            ClientSize = new Vector2i(1440, 900),
            MinimumClientSize = new Vector2i(1000, 680),
            StartVisible = true,
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
        const string layoutVersion = "5";
        _imguiLayoutPath = layoutPath;
        var savedLayout = File.Exists(layoutPath) ? File.ReadAllText(layoutPath) : string.Empty;
        var hasCurrentLayoutVersion = File.Exists(layoutVersionPath) &&
                                      string.Equals(File.ReadAllText(layoutVersionPath).Trim(), layoutVersion,
                                          StringComparison.Ordinal);
        var hasCurrentPanelLayout = hasCurrentLayoutVersion &&
                                     savedLayout.Contains("DockSpace ID=0x6A3C3001", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][场景##场景窗口]", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][渲染##渲染窗口]", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][检查器##检查器窗口]", StringComparison.Ordinal) &&
                                     savedLayout.Contains("[Window][渲染配置##渲染配置窗口]", StringComparison.Ordinal) &&
                                     !savedLayout.Contains("[Window][场景与渲染##中央面板]", StringComparison.Ordinal);
        if (hasCurrentPanelLayout)
        {
            ImGui.LoadIniSettingsFromDisk(layoutPath);
        }
        else
        {
            ImGui.LoadIniSettingsFromMemory(DefaultDockLayout);
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
        style.WindowRounding = 2F;
        style.ChildRounding = 2F;
        style.FrameRounding = 2F;
        style.PopupRounding = 2F;
        style.ScrollbarRounding = 2F;
        style.GrabRounding = 2F;
        style.WindowBorderSize = 1F;
        style.FrameBorderSize = 1F;
        style.ItemSpacing = new Vector2(7F, 5F);
        style.WindowPadding = new Vector2(7F, 7F);
        style.Colors[(int)ImGuiCol.WindowBg] = ToColor(EditorPalette.Window);
        style.Colors[(int)ImGuiCol.ChildBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.PopupBg] = ToColor(EditorPalette.Panel);
        style.Colors[(int)ImGuiCol.FrameBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.FrameBgActive] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.TitleBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TitleBgActive] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.MenuBarBg] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.Header] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.HeaderHovered] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.Button] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.ButtonHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.ButtonActive] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Border] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.Separator] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.SeparatorHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Tab] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.TabHovered] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.TabSelected] = ToColor(EditorPalette.AccentSoft);
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
        UiDiagnostics.PrepareTabs(ref _requestedCenterTab, ref _requestedInspectorTab);
        var welcomeIsOpen = _welcomeWindow?.IsOpen == true;
        var operation = _projectOperation.Snapshot;
        var inputBlocked = welcomeIsOpen || operation is not null;
        if (!inputBlocked) HandleVoxelShortcuts();
        if (inputBlocked) ImGui.BeginDisabled();
        DrawMenuBar();
        DrawDockspace();
        DrawHierarchy();
        DrawCenter();
        DrawInspector();
        DrawRenderConfig();
        DrawProjectAssets();
        DrawPluginPanels();
        DrawGeneratorWindow();
        DrawStatusBar();
        DrawProjectPathDialog();
        DrawCloseProjectDialog();
        DrawMinecraftImportWindow();
        DrawMeshImportWindow();
        if (inputBlocked) ImGui.EndDisabled();
        if (!inputBlocked) DrawSettingsWindow();
        if (welcomeIsOpen) _welcomeWindow!.Draw();
        else if (operation is { } snapshot)
        {
            BlockingOperationDialog.Draw(snapshot, CancelProjectOperation);
        }
        _applyPreset = false;
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
            ImGui.Separator();
            if (ImGui.MenuItem("关闭项目", enabled: _project is not null)) RequestCloseProject();
            if (ImGui.MenuItem("导入体素数据（.vox / .qb / .schem）…", enabled: _project is not null)) ImportVoxAsWorld();
            if (ImGui.MenuItem("导入网格模型（obj/stl/glb/gltf）…", enabled: _project is not null)) ShowMeshImportDialog();
            if (ImGui.MenuItem("导入 Minecraft 存档…", enabled: _project is not null)) ShowMinecraftImportDialog();
            if (ImGui.MenuItem("退出", "Alt+F4")) Close();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("视图"))
        {
            if (ImGui.MenuItem("场景", "F1")) _requestedCenterTab = 0;
            if (ImGui.MenuItem("渲染", "F2")) _requestedCenterTab = 1;
            ImGui.Separator();
            if (ImGui.MenuItem("层级", null, ref _showHierarchy)) { }
            if (ImGui.MenuItem("项目资产", null, ref _showProjectAssets)) { }
            if (ImGui.MenuItem("检查器", null, ref _showInspector)) { }
            if (ImGui.MenuItem("生成器", null, ref _showGeneratorPanel)) { }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("插件"))
        {
            var diagnostics = _project is null ? Array.Empty<ProjectPluginDiagnostic>() : PluginManager.Diagnose(_project);
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

        if (ImGui.BeginMenu("布局"))
        {
            if (ImGui.MenuItem("默认布局", null, _layoutPreset == LayoutPreset.Default)) SelectLayout(LayoutPreset.Default);
            if (ImGui.MenuItem("建模布局", null, _layoutPreset == LayoutPreset.Modeling)) SelectLayout(LayoutPreset.Modeling);
            if (ImGui.MenuItem("渲染布局", null, _layoutPreset == LayoutPreset.Rendering)) SelectLayout(LayoutPreset.Rendering);
            ImGui.Separator();
            if (ImGui.MenuItem(_editPanelLayout ? "完成面板布局编辑" : "编辑面板布局"))
            {
                _editPanelLayout = !_editPanelLayout;
                SetStatus(_editPanelLayout ? "面板布局编辑已开启：可拖拽标题栏进行停靠" : "面板布局编辑已完成");
            }
            if (ImGui.MenuItem("重置当前布局")) SelectLayout(_layoutPreset);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("工具"))
        {
            ImGui.TextDisabled("视口工具");
            if (ImGui.MenuItem("相机", null, _activeTool == EditorTool.Camera)) _activeTool = EditorTool.Camera;
            if (ImGui.MenuItem("选择", null, _activeTool == EditorTool.Pick)) _activeTool = EditorTool.Pick;
            ImGui.Separator();
            ImGui.TextDisabled("体素编辑工具");
            if (ImGui.MenuItem("绘制", null, _activeTool == EditorTool.Draw)) _activeTool = EditorTool.Draw;
            if (ImGui.MenuItem("擦除", null, _activeTool == EditorTool.Erase)) _activeTool = EditorTool.Erase;
            if (ImGui.MenuItem("直线", null, _activeTool == EditorTool.Line)) _activeTool = EditorTool.Line;
            if (ImGui.MenuItem("长方体", null, _activeTool == EditorTool.Box)) _activeTool = EditorTool.Box;
            if (ImGui.MenuItem("填充", null, _activeTool == EditorTool.Fill)) _activeTool = EditorTool.Fill;
            ImGui.Separator();
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

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("设置"))
        {
            if (ImGui.MenuItem("编辑器设置")) OpenSettings(0);
            if (ImGui.MenuItem("快捷键方案")) OpenSettings(0, 2);
            if (ImGui.MenuItem("项目设置", enabled: _project is not null)) OpenSettings(1);
            ImGui.Separator();
            if (ImGui.MenuItem("快速画面设置"))
            {
                _showInspector = true;
                _requestedInspectorTab = 1;
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("帮助"))
        {
            ImGui.MenuItem("MVoxel 体素建模软件", enabled: false);
            ImGui.MenuItem("在场景中旋转、平移和缩放，观察你的作品", enabled: false);
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
            ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.AutoHideTabBar);
        ImGui.End();
    }

    private const string DefaultDockLayout = """
[Docking][Data]
DockSpace ID=0x6A3C3001 Pos=0,24 Size=1440,840 Split=Y Selected=0x6A3C3002
  DockNode ID=0x6A3C3002 Parent=0x6A3C3001 SizeRef=1440,615 Split=X Selected=0x6A3C3003
    DockNode ID=0x6A3C3003 Parent=0x6A3C3002 SizeRef=260,615 Selected=0xD7AC1001
    DockNode ID=0x6A3C3004 Parent=0x6A3C3002 SizeRef=1180,615 Split=X Selected=0x6A3C3005
      DockNode ID=0x6A3C3005 Parent=0x6A3C3004 SizeRef=820,615 Selected=0xD7AC1002
      DockNode ID=0x6A3C3006 Parent=0x6A3C3004 SizeRef=360,615 Selected=0xD7AC1003
  DockNode ID=0x6A3C3007 Parent=0x6A3C3001 SizeRef=1440,225 Selected=0xD7AC1004
[Window][层级##层级面板]
DockId=0x6A3C3003,0
[Window][场景##场景窗口]
DockId=0x6A3C3005,0
[Window][渲染##渲染窗口]
DockId=0x6A3C3005,1
[Window][检查器##检查器窗口]
DockId=0x6A3C3006,0
[Window][渲染配置##渲染配置窗口]
DockId=0x6A3C3006,1
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

        ImGui.TextColored(ToColor(EditorPalette.Text), "层级");
        ImGui.SameLine();
        ImGui.TextDisabled(_project?.Manifest.Name ?? "未打开项目");
        ImGui.SetNextItemWidth(Math.Max(100F, ImGui.GetContentRegionAvail().X - 4F));
        ImGui.InputTextWithHint("##层级搜索", "搜索对象", ref _hierarchySearch, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(96F);
        var filterLayers = _sceneDocument?.LayerNames ?? ["默认"];
        if (_hierarchyLayerFilter >= filterLayers.Count)
        {
            _hierarchyLayerFilter = -1;
        }

        if (ImGui.BeginCombo("##层级图层过滤", _hierarchyLayerFilter < 0 ? "全部图层" : filterLayers[_hierarchyLayerFilter]))
        {
            if (ImGui.Selectable("全部图层", _hierarchyLayerFilter < 0))
            {
                _hierarchyLayerFilter = -1;
            }

            for (var layerIndex = 0; layerIndex < filterLayers.Count; layerIndex++)
            {
                if (ImGui.Selectable($"{layerIndex + 1}. {filterLayers[layerIndex]}", _hierarchyLayerFilter == layerIndex))
                {
                    _hierarchyLayerFilter = layerIndex;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Separator();
        if (_project is null)
        {
            ImGui.TextWrapped("打开或创建项目后，场景层级会显示在这里。");
        }
        else
        {
            var objects = _sceneDocument?.Objects ?? new List<SceneObject>();
            if (ImGui.TreeNodeEx(_project.Manifest.Name, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (objects.Count == 0)
                {
                    ImGui.TextDisabled("场景中还没有对象");
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

        ImGui.End();
    }

    private bool MatchesHierarchySearch(SceneObject sceneObject, IReadOnlyList<SceneObject> objects)
    {
        var searchMatched = string.IsNullOrWhiteSpace(_hierarchySearch) ||
            sceneObject.Name.Contains(_hierarchySearch.Trim(), StringComparison.CurrentCultureIgnoreCase);
        var layerMatched = _hierarchyLayerFilter < 0 || sceneObject.LayerIndex == _hierarchyLayerFilter;
        if (searchMatched && layerMatched)
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

        var nodeId = $"{sceneObject.Name}##SceneObject_{sceneObject.Id}";
        var transparentHeader = Color.FromArgb(0, EditorPalette.PanelHeader);
        ImGui.PushStyleColor(ImGuiCol.Header, ToColor(transparentHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ToColor(transparentHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ToColor(transparentHeader));
        var expanded = ImGui.TreeNodeEx(nodeId, nodeFlags);
        ImGui.PopStyleColor(3);
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowClicked = ImGui.IsItemClicked();
        var treeCursor = ImGui.GetCursorScreenPos();
        var rowWidth = Math.Max(1F, ImGui.GetContentRegionAvail().X);
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

        const float iconWidth = HierarchyRowIconSize;
        var iconGap = 2F;
        var iconsStart = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - iconWidth * 2F - iconGap - 8F;
        ImGui.SetCursorScreenPos(new Vector2(iconsStart, rowMin.Y + 1F));
        var visibilityClicked = DrawHierarchyIconButton($"##Visibility_{sceneObject.Id}", "显示对象", "隐藏对象",
            sceneObject.Visible, DrawVisibilityIcon, iconWidth);
        if (visibilityClicked)
        {
            sceneObject.Visible = !sceneObject.Visible;
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }

        ImGui.SameLine(0F, iconGap);
        var selectableClicked = DrawHierarchyIconButton($"##Selectable_{sceneObject.Id}", "允许选中对象", "禁止选中对象",
            !sceneObject.Locked, DrawLockIcon, iconWidth);
        if (selectableClicked)
        {
            sceneObject.Locked = !sceneObject.Locked;
            if (sceneObject.Locked && _selectedSceneObjectId == sceneObject.Id)
            {
                _selectedSceneObjectId = null;
            }
        }

        if ((rowClicked || rowSelectableClicked) && !visibilityClicked && !selectableClicked && !sceneObject.Locked)
        {
            _selectedSceneObjectId = sceneObject.Id;
            _selectedVoxel = null;
        }

        if (ImGui.BeginPopupContextItem($"##SceneObjectRowMenu_{sceneObject.Id}"))
        {
            _selectedSceneObjectId = sceneObject.Id;
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

        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        _lastRenderViewportSize = size;
        var drawList = ImGui.GetWindowDrawList();
        var sceneSettings = CurrentSceneRenderSettings;
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(SceneBackgroundDisplayColor(sceneSettings)));

        var gizmoCenter = origin + new Vector2(size.X - 58F, 76F);
        var gizmoRadius = Math.Clamp(Math.Min(size.X, size.Y) * 0.07F, 28F, 36F);
        var gizmoHovered = HandleSceneNavigation(origin, size, gizmoCenter, gizmoRadius,
            allowMouseCapture: true, ViewportCaptureTarget.Scene);
        var camera = GetSceneCamera();
        MVoxelLog.SetRuntimeContext(
            $"场景视图，视口={size.X:0}x{size.Y:0}，相机位置=({camera.Position.X:0.000},{camera.Position.Y:0.000},{camera.Position.Z:0.000})，" +
            $"距离={_sceneCameraDistance:0.000}，体素={_world.OccupiedVoxelCount}");

        // 辅助图层与模型内容是两个独立系统。空场景仍必须显示网格，
        // 否则用户无法判断原点、比例和编辑位置。
        if (_showGrid)
        {
            SceneGrid.Draw(drawList, origin, size, camera, _sceneCameraDistance);
        }

        // 场景窗口是编辑器预览，不参与渲染窗口的实时光照或路径追踪。
        // 这样网格、坐标轴和体素编辑反馈不会被渲染管线的累积结果污染。
        var showVoxelWorld = IsVoxelWorldVisible();
        if (showVoxelWorld && _project is not null)
        {
            DrawVoxelWorldPreview(drawList, origin, size, camera, _showVoxelOutlines);
        }

        if (_project is not null && showVoxelWorld)
        {
            HandleVoxelToolInput(origin, size, camera);
            DrawVoxelBrushPreview(drawList, origin, size, camera);
        }
        else if (!gizmoHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            RequestWelcomeWindow();
        }

        // ImGui 绘制列表没有场景深度，轴线在体素图像之后提交并按体素射线裁剪遮挡段
        DrawSceneWorldAxes(drawList, origin, size, camera);

        DrawOrientationGizmo(drawList, gizmoCenter, gizmoRadius, camera, gizmoHovered);
        if (_sceneMouseCaptured && _capturedViewport == ViewportCaptureTarget.Scene)
        {
            DrawSceneCrosshair(drawList, origin + size * 0.5F);
        }

        var hintLineHeight = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPos(new Vector2(12F, Math.Max(12F, size.Y - hintLineHeight * 2F - 8F)));
        ImGui.TextDisabled($"视角 {_sceneOrbitYaw * 180F / MathF.PI:0}° / {_sceneOrbitPitch * 180F / MathF.PI:0}°  ·  观察距离 {_sceneCameraDistance:0.0}");
        ImGui.SetCursorPos(new Vector2(12F, Math.Max(12F, size.Y - hintLineHeight - 8F)));
        ImGui.TextDisabled(IsCapsLockEnabled()
            ? _editorSettings.McMode
                ? "Caps Lock：WASD 移动 · Q/E 升降 · 鼠标转向 · 左键破坏 · 右键放置 · Shift 加速 · 中键平移"
                : "Caps Lock：WASD 移动 · Q/E 升降 · 鼠标转向 · 左键放置 · 右键破坏 · Shift 加速 · 中键平移"
            : "左键放置 · 右键环绕 · 中键平移 · Shift+左键破坏 · 滚轮缩放 · 点击右上角 XYZ 定向");
        DrawSceneCameraButton(origin, size);
        DrawSceneCameraPopup();
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

    /// <summary>
    /// 绘制场景右上角的相机设置按钮
    /// </summary>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    private static void DrawSceneCameraButton(Vector2 origin, Vector2 size)
    {
        ImGui.SetCursorScreenPos(origin + new Vector2(Math.Max(8F, size.X - 112F), 8F));
        if (ImGui.Button("相机##SceneCameraButton", new Vector2(96F, 24F)))
        {
            ImGui.OpenPopup("##SceneCameraPopup");
        }
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
        if (_welcomeWindow?.IsOpen == true || _projectOperation.IsRunning)
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

        if (Math.Abs(io.MouseWheel) > float.Epsilon && !gizmoHovered)
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
        var hit = VoxelDda.RayCast(_world, cameraPosition, direction,
            MathF.Max(pointDistance - 0.01F, near), ignoreOriginCell: true);
        return hit is null;
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
    /// 判断体素模型对象是否允许显示
    /// </summary>
    /// <returns>允许显示时返回 true</returns>
    private bool IsVoxelWorldVisible()
    {
        var modelObjects = _sceneDocument?.Objects
            .Where(item => item.Components.Any(SceneComponentTypes.IsVoxelModel))
            .ToArray();
        return modelObjects is { Length: > 0 } && modelObjects.Any(modelObject =>
            modelObject.Visible && modelObject.Components.Any(component =>
                SceneComponentTypes.IsVoxelModel(component) && component.Enabled));
    }

    /// <summary>
    /// 判断体素模型对象是否允许选中和编辑
    /// </summary>
    /// <returns>锁定时返回 true</returns>
    private bool IsVoxelWorldLocked() => GetVoxelWorldObject()?.Locked ?? false;

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
        bool showOutlines)
    {
        EnsureCpuPreviewSurface();
        var faces = new List<CpuPreviewFace>(_cpuPreviewSurfaceFaces.Count);
        foreach (var surface in _cpuPreviewSurfaceFaces)
        {
            var points = new Vector2[4];
            var depths = new float[4];
            for (var index = 0; index < surface.Corners.Length; index++)
            {
                if (!TryProject(surface.Corners[index], origin, size, camera, out points[index], out depths[index]))
                    goto nextFace;
            }
            faces.Add(new CpuPreviewFace(points, surface.Color, depths.Average()));
            nextFace:;
        }

        foreach (var face in faces.OrderByDescending(face => face.Depth))
        {
            var polygon = face.Points;
            drawList.AddConvexPolyFilled(ref polygon[0], polygon.Length, ToPackedColor(face.Color));
            if (showOutlines)
            {
                for (var index = 0; index < polygon.Length; index++)
                    drawList.AddLine(polygon[index], polygon[(index + 1) % polygon.Length],
                        ToPackedColor(Color.FromArgb(245, 4, 7, 11)), 1.2F);
            }
        }
    }

    private void EnsureCpuPreviewSurface()
    {
        if (ReferenceEquals(_cpuPreviewWorld, _world) && _cpuPreviewWorldVersion == _world.ContentVersion)
            return;

        const int maximumPreviewFaces = 80_000;
        _cpuPreviewSurfaceFaces.Clear();
        // 场景窗口不使用渲染窗口的光照或路径追踪，但几何必须来自同一份完整世界数据。
        // 通过与 GPU 工作集一致的贪心盒合并减少 ImGui 面片数量，避免抽样造成“散点化”结构。
        var workset = VoxelGpuWorkset.Build(_world, greedyMerge: true);
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
            MVoxelLog.Info($"场景窗口 CPU 预览已限额：原始 {_world.OccupiedVoxelCount:N0} 体素，合并盒 {workset.Cells.Count:N0}，表面 {_cpuPreviewSurfaceFaces.Count:N0} 面");
        }

        _cpuPreviewWorld = _world;
        _cpuPreviewWorldVersion = _world.ContentVersion;
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
        _cpuPreviewSurfaceFaces.Add(new CpuPreviewSurfaceFace(corners, Shade(baseColor, shade)));
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
            if (!_world.TryGet(neighbor, out var value) || value.IsEmpty) return true;
        }

        return false;
    }

    private enum FaceDirection
    {
        Left, Right, Bottom, Top, Back, Front,
    }

    private readonly record struct CpuPreviewFace(Vector2[] Points, Color Color, float Depth);

    private readonly record struct CpuPreviewSurfaceFace(Vector3[] Corners, Color Color);

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
        ImGui.Separator();
        if (!ImGui.BeginChild("##RenderViewport", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        var io = ImGui.GetIO();
        MVoxelLog.SetRuntimeContext(
            $"RenderView 导航输入，鼠标=({io.MousePos.X:0.0},{io.MousePos.Y:0.0})，增量=({io.MouseDelta.X:0.0},{io.MouseDelta.Y:0.0})，" +
            $"左键={ImGui.IsMouseDown(ImGuiMouseButton.Left)}，中键={ImGui.IsMouseDown(ImGuiMouseButton.Middle)}，右键={ImGui.IsMouseDown(ImGuiMouseButton.Right)}，" +
            $"Shift={IsShiftPressed()}，CapsLock={IsCapsLockEnabled()}，视口={size.X:0}x{size.Y:0}");
        var camera = HandleRenderCameraNavigation(origin, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(Color.FromArgb(255, 14, 18, 24)));
        var showVoxelWorld = IsVoxelWorldVisible();
        var exporting = _activeExport is not null || _exportQueue.Count > 0;
        var gpuRendered = false;
        if (exporting && showVoxelWorld && _project is not null)
        {
            ProcessExportQueue(camera, size);
            if (_pipeline?.DisplayTextureId is { } exportTexture && exportTexture != IntPtr.Zero)
            {
                drawList.AddImage(exportTexture, origin, origin + size, Vector2.Zero, _pipeline.DisplayUvMax);
                gpuRendered = true;
            }
        }
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
        if (_pipeline is not { IsAvailable: true } pipeline) return;
        if (_cancelActiveExportRequested)
        {
            _cancelActiveExportRequested = false;
            if (_activeExport is not null)
            {
                SetStatus($"已取消导出：{Path.GetFileName(_activeExport.Path)}");
                _activeExport = null;
            }
        }
        if (_activeExport is null && _exportQueue.Count > 0)
        {
            _activeExport = _exportQueue.Dequeue();
            pipeline.ResetAccumulation();
        }
        if (_activeExport is null) return;
        try
        {
            var settings = _activeExport.Settings;
            var viewCamera = _activeExport.Camera ?? camera;
            viewCamera = viewCamera with { FieldOfViewDegrees = settings.CameraFovDegrees };
            if (_activeExport.CullingMask is { } mask)
            {
                _pipeline!.SyncWorld(GetRenderWorld(mask), 0, settings.RenderLod,
                    new System.Numerics.Vector3(viewCamera.Position.X, viewCamera.Position.Y, viewCamera.Position.Z));
            }

            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var frame = BuildFrameState(viewCamera, size, scale, width, height, pathMode: settings.PathTracingEnabled, outlines: false);
            var submitted = settings.PathTracingEnabled
                ? pipeline.RenderPathView(settings, frame, width, height)
                : pipeline.RenderSceneView(settings, frame, width, height);
            if (!submitted)
            {
                SetStatus($"导出失败：{Path.GetFileName(_activeExport.Path)}");
                _activeExport = null;
                return;
            }

            var finished = !settings.PathTracingEnabled || pipeline.AccumulatedSamples >= settings.PathMaxAccumulatedSamples;
            if (!finished) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_activeExport.Path)!);
            var saved = _activeExport.Format == ExportImageFormat.Hdr
                ? pipeline.SaveDisplayHdr(_activeExport.Path)
                : pipeline.SaveDisplayPng(_activeExport.Path);
            if (saved)
                SetStatus($"图片已导出：{Path.GetFileName(_activeExport.Path)}（剩余队列 {_exportQueue.Count}）");
            else
                SetStatus($"导出失败：{Path.GetFileName(_activeExport.Path)}");
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
            _activeExport = null;
        }
    }

    private void DrawRenderViewOverlay(Vector2 origin, Vector2 size, ImDrawListPtr drawList)
    {
        _ = drawList;
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F));
        var pipeline = _pipeline;
        var activeRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject());
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F));
        if (ImGui.SmallButton("重新计算"))
        {
            _pipeline?.ResetAccumulation();
        }

        ImGui.SameLine();
        var canExport = _pipeline?.IsAvailable == true;
        ImGui.BeginDisabled(!canExport);
        if (ImGui.SmallButton("导出 PNG"))
        {
            ExportRenderPng();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("导出 HDR"))
        {
            ExportRenderHdr();
        }
        if (canExport && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把 HDR 累积缓冲导出为 Radiance .hdr 文件，保留完整动态范围供后期处理");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("批量出图（全部相机）"))
        {
            ExportAllCamerasPng();
        }
        ImGui.EndDisabled();
        if (canExport && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("为每个相机对象各出一张图：统一使用项目渲染配置，并按各相机的视角、视场角和图层遮罩分别重组渲染世界");
        }
        if (!canExport && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("当前显卡显示结果尚未准备好");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{(int)activeRenderSettings.CameraFovDegrees}° · 尺寸 {(int)size.X}×{(int)size.Y}");
    }

    private void ExportRenderPng()
    {
        if (_pipeline is not { IsAvailable: true })
        {
            SetStatus("显卡渲染不可用，无法加入导出队列");
            return;
        }

        var projectPath = _project?.RootPath
            ?? AppPaths.DataDirectory;
        var fileName = $"渲染-{DateTime.Now:yyyyMMdd-HHmmss}-{++_exportSequence:000}.png";
        var path = Path.Combine(projectPath, "Assets", "Exports", fileName);
        var exportSettings = (_project is null ? _editorSettings.ExportRenderSettings : _projectSettings.ExportRenderSettings)
            .CloneForExport();
        _exportQueue.Enqueue(new ExportJob(path, exportSettings));
        SetStatus($"已加入导出队列：{fileName}");
    }

    /// <summary>
    /// 把当前渲染视图的 HDR 累积结果导出为 Radiance .hdr 文件加入导出队列
    /// </summary>
    private void ExportRenderHdr()
    {
        if (_pipeline is not { IsAvailable: true })
        {
            SetStatus("显卡渲染不可用，无法加入导出队列");
            return;
        }

        var projectPath = _project?.RootPath ?? AppPaths.DataDirectory;
        var fileName = $"渲染-{DateTime.Now:yyyyMMdd-HHmmss}-{++_exportSequence:000}.hdr";
        var path = Path.Combine(projectPath, "Assets", "Exports", fileName);
        var exportSettings = (_project is null ? _editorSettings.ExportRenderSettings : _projectSettings.ExportRenderSettings)
            .CloneForExport();
        _exportQueue.Enqueue(new ExportJob(path, exportSettings, ExportImageFormat.Hdr));
        SetStatus($"已加入导出队列：{fileName}");
    }

    /// <summary>
    /// 批量出图：为场景中每个相机对象各排一个导出任务，
    /// 所有相机统一使用项目导出配置，并按各自图层遮罩重组渲染世界
    /// </summary>
    private void ExportAllCamerasPng()
    {
        if (_pipeline is not { IsAvailable: true })
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
            var settings = baseSettings.CloneForExport();
            settings.CameraFovDegrees = cameraData.FieldOfViewDegrees;
            var fileName = $"渲染-{SanitizeFileName(cameraObject.Name)}-{stamp}-{++_exportSequence:000}.png";
            _exportQueue.Enqueue(new ExportJob(Path.Combine(project.AssetsPath, "Exports", fileName), settings)
            {
                Camera = ToSceneCamera(cameraObject),
                CullingMask = cameraData.CullingMask,
            });
        }

        SetStatus($"已批量入队 {cameras.Count} 个相机的出图任务");
    }

    internal enum ExportImageFormat { Png, Hdr }

    private sealed class ExportJob
    {
        internal ExportJob(string path, RenderSettings settings, ExportImageFormat format = ExportImageFormat.Png)
        { Path = path; Settings = settings; Format = format; }
        internal string Path { get; }
        internal RenderSettings Settings { get; }
        internal ExportImageFormat Format { get; }

        /// <summary>任务专属相机；为空表示沿用渲染视图当前相机</summary>
        internal SceneCamera? Camera { get; init; }

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
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8F, 8F));
            DrawInspectorProperties();
            ImGui.PopStyleVar();
        }
        ImGui.End();
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

        ImGui.TextDisabled($"共 {_exportQueue.Count + (_activeExport is null ? 0 : 1)} 个任务");
        ImGui.Separator();
        if (_activeExport is { } active)
        {
            ImGui.TextUnformatted("正在导出");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消##取消当前导出")) _cancelActiveExportRequested = true;
            ImGui.TextWrapped(Path.GetFileName(active.Path));
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
            ImGui.TextWrapped(Path.GetFileName(job.Path));
            ImGui.SameLine();
            ImGui.ProgressBar(0F, new Vector2(90F, 0F), "等待");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消"))
                _exportQueue = new Queue<ExportJob>(_exportQueue.Where(item => item != job));
            ImGui.PopID();
        }
        ImGui.End();
        if (!open) _exportQueueExpanded = false;
    }

    private float ExportProgress(ExportJob job)
    {
        if (_pipeline is null) return 0F;
        if (!job.Settings.PathTracingEnabled) return 1F;
        if (job.Settings.PathMaxAccumulatedSamples <= 0) return 0F;
        return Math.Clamp((float)_pipeline.AccumulatedSamples / job.Settings.PathMaxAccumulatedSamples, 0F, 1F);
    }

    /// <summary>
    /// 绘制检查器的对象属性内容
    /// </summary>
    private void DrawInspectorProperties()
    {
        if (_selectedSceneObjectId is null &&
            (TryDrawSelectedPngInspector() || TryDrawSelectedMaterialInspector() || TryDrawSelectedEnvironmentInspector() ||
             TryDrawSelectedAssetInspector()))
        {
            return;
        }

        var selectedObject = _selectedSceneObjectId is { } selectedObjectId
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
        if (!ImGui.BeginChild("##InspectorScroll", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        var inspectorBefore = SceneSerializer.CloneObject(selectedObject);

        var hasModelCameraConflict = selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel) &&
                                     selectedObject.Components.Any(SceneComponentTypes.IsCamera);
        if (hasModelCameraConflict)
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), "警告：模型组件与相机组件不能同时挂载");
        else if (!string.IsNullOrWhiteSpace(_componentWarning))
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), $"警告：{_componentWarning}");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ToColor(EditorPalette.Panel));
        ImGui.PushStyleColor(ImGuiCol.Border, ToColor(EditorPalette.Border));
        if (ImGui.BeginChild("##InspectorObjectHeader", new Vector2(-1F, 70F), ImGuiChildFlags.Borders))
        {
            var visible = selectedObject.Visible;
            var visibilityClicked = DrawHierarchyIconButton("##InspectorObjectVisibility", "对象可见", "对象隐藏",
                visible, DrawVisibilityIcon);
            if (visibilityClicked)
            {
                selectedObject.Visible = !visible;
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
                _accumulationSignature = string.Empty;
            }
            ImGui.SameLine();
            var objectName = selectedObject.Name;
            ImGui.SetNextItemWidth(Math.Max(96F, ImGui.GetContentRegionAvail().X - 30F));
            if (ImGui.InputText("##InspectorObjectName", ref objectName, 128) &&
                !string.IsNullOrWhiteSpace(objectName) && objectName != selectedObject.Name)
            {
                selectedObject.Name = objectName.Trim();
                MarkSceneDocumentDirty();
            }
            ImGui.SameLine(0F, 4F);
            var selectableClicked = DrawHierarchyIconButton("##InspectorObjectSelectable", "允许选中对象", "禁止选中对象",
                !selectedObject.Locked, DrawLockIcon);
            if (selectableClicked)
            {
                selectedObject.Locked = !selectedObject.Locked;
                MarkSceneDocumentDirty();
                if (selectedObject.Locked) _selectedSceneObjectId = null;
            }
            if (_sceneDocumentDirty && ImGui.IsItemHovered()) ImGui.SetTooltip("当前场景有未保存修改");
            ImGui.SameLine();
            var layerNames = _sceneDocument?.LayerNames ?? ["默认"];
            var currentLayer = Math.Clamp(selectedObject.LayerIndex, 0, layerNames.Count - 1);
            ImGui.SetNextItemWidth(Math.Min(220F, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("##InspectorObjectLayer", $"图层：{layerNames[currentLayer]}"))
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
        ImGui.PopStyleColor(2);
        ImGui.Dummy(new Vector2(0F, 6F));
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
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6F, 5F));
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
            var headerWidth = Math.Max(120F, totalHeaderWidth - 64F);
            var headerHeight = ImGui.GetFrameHeight() + 6F;
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
            draw.AddRectFilled(headerMin, headerMax, ToPackedColor(headerColor), 3F);
            draw.AddRect(headerMin, headerMax, ToPackedColor(EditorPalette.Border), 3F, ImDrawFlags.None, 1F);
            var triangleCenter = new Vector2(headerMin.X + 12F, headerMin.Y + headerHeight / 2F);
            if (expanded)
                draw.AddTriangleFilled(triangleCenter + new Vector2(-4F, -2F), triangleCenter + new Vector2(4F, -2F),
                    triangleCenter + new Vector2(0F, 4F), ToPackedColor(EditorPalette.Text));
            else
                draw.AddTriangleFilled(triangleCenter + new Vector2(-2F, -4F), triangleCenter + new Vector2(-2F, 4F),
                    triangleCenter + new Vector2(4F, 0F), ToPackedColor(EditorPalette.Text));
            draw.AddText(headerMin + new Vector2(25F, 5F), ToPackedColor(EditorPalette.Text), label);

            var resetComponent = false;
            ImGui.SetCursorScreenPos(new Vector2(headerMax.X - 58F, headerMin.Y + 3F));
            var isTransformComponent = SceneComponentTypes.IsTransform(component);
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
            if (ImGui.SmallButton("...##InspectorComponentMenu")) ImGui.OpenPopup("##InspectorComponentMenuPopup");
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
                if (isTransformComponent) ImGui.TextDisabled("坐标组件不能移除");
                ImGui.EndPopup();
            }
            ImGui.SetCursorScreenPos(new Vector2(headerMin.X, headerMax.Y));
            if (resetComponent) ResetInspectorComponent(selectedObject, component);
            if (expanded)
            {
                var bodyHeight = GetInspectorComponentBodyHeight(selectedObject, component);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4F);
                ImGui.PushStyleColor(ImGuiCol.ChildBg, ToColor(EditorPalette.Panel));
                if (ImGui.BeginChild("##InspectorComponentBody", new Vector2(-1F, bodyHeight),
                        ImGuiChildFlags.Borders))
                {
                    ImGui.Indent(8F);
                    DrawComponentSummary(selectedObject, component);
                    ImGui.Unindent(8F);
                }
                ImGui.EndChild();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();
            }
            ImGui.Dummy(new Vector2(0F, 2F));
            ImGui.PopID();
        }
        ImGui.PopStyleVar();

        if (removeComponentIndex >= 0)
        {
            var removed = selectedObject.Components[removeComponentIndex];
            selectedObject.Components.RemoveAt(removeComponentIndex);
            _componentWarning = null;
            MarkSceneDocumentDirty();
            SetStatus($"已从“{selectedObject.Name}”移除{SceneComponentTypes.GetDisplayName(removed)}组件");
        }

        if (ImGui.Button("添加组件", new Vector2(-1F, 30F))) ImGui.OpenPopup("##InspectorAddComponentPopup");
        if (ImGui.BeginPopup("##InspectorAddComponentPopup"))
            {
                ImGui.TextDisabled("选择要添加的组件");
                ImGui.Separator();
                foreach (var definition in SceneComponentTypes.BuiltInDefinitions)
                {
                    if (selectedObject.Components.Any(item => item.TypeId == definition.TypeId)) continue;
                    if (!ImGui.Selectable($"{definition.DisplayName} · {definition.Category}")) continue;
                    if (definition.TypeId == SceneComponentTypes.VoxelModel &&
                        selectedObject.Components.Any(SceneComponentTypes.IsCamera))
                    {
                        _componentWarning = "模型组件与相机组件不能同时挂载";
                        continue;
                    }
                    if (definition.TypeId == SceneComponentTypes.Camera &&
                        selectedObject.Components.Any(SceneComponentTypes.IsVoxelModel))
                    {
                        _componentWarning = "相机组件与模型组件不能同时挂载";
                        continue;
                    }
                    _componentWarning = null;
                    if (definition.TypeId == SceneComponentTypes.VoxelModel)
                    {
                        selectedObject.Transform.SetGrid(selectedObject.Transform.ToGrid());
                        var transform = selectedObject.Components.FirstOrDefault(SceneComponentTypes.IsTransform);
                        if (transform is not null) transform.Data = JsonSerializer.SerializeToElement(new { kind = "Grid" });
                    }
                    selectedObject.Components.Add(definition.TypeId switch
                    {
                        SceneComponentTypes.Camera => SceneComponentTypes.CreateCamera(),
                        SceneComponentTypes.Light => SceneComponentTypes.CreateLight(LightComponentData.Default),
                        SceneComponentTypes.VoxelModel => SceneComponentTypes.CreateVoxelModel(string.Empty, 0, "手动添加", "模型"),
                        _ => SceneComponentTypes.CreateTransform(selectedObject.Transform),
                    });
                    MarkSceneDocumentDirty();
                }

                foreach (var definition in _pluginRuntime?.Components ?? [])
                {
                    if (selectedObject.Components.Any(item => item.TypeId == definition.Id)) continue;
                    if (!ImGui.Selectable($"{definition.Name} · {definition.Category}")) continue;
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
                ImGui.EndPopup();
            }

        var inspectorAfter = SceneSerializer.CloneObject(selectedObject);
        if (!SceneSerializer.ObjectsEqual(inspectorBefore, inspectorAfter))
        {
            _voxelHistory.RecordExecuted(new SceneObjectStateCommand(
                selectedObject,
                inspectorBefore,
                inspectorAfter,
                () =>
                {
                    _sceneDocumentDirty = true;
                    _pipeline?.ResetAccumulation();
                }));
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
        else if (SceneComponentTypes.IsVoxelModel(component))
        {
            SceneComponentTypes.WriteVoxelModel(component, VoxelModelComponentData.Default);
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
        ImGui.TextDisabled(label);
        if (tooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(start + 82F, ImGui.GetCursorPosX()));
        ImGui.SetNextItemWidth(-1F);
    }

    private static float GetInspectorComponentBodyHeight(SceneObject sceneObject, ComponentRecord component)
    {
        if (SceneComponentTypes.IsTransform(component))
            return sceneObject.Transform.Mode == TransformMode.Free ? 92F : 76F;
        if (SceneComponentTypes.IsVoxelModel(component)) return 150F;
        // 相机组件包含三个裁剪/视场字段、图层下拉框、独立配置开关和刷新按钮；
        // 预留完整高度，超出检查器窗口时由外层 InspectorScroll 统一滚动。
        if (SceneComponentTypes.IsCamera(component)) return 190F;
        if (SceneComponentTypes.IsLight(component)) return 124F;
        // 插件组件包含状态、类型、来源和 JSON 编辑区，固定留出完整显示空间；组件内部不滚动。
        return 190F;
    }

    private static void DrawInspectorIcon(bool rotation, string tooltip)
    {
        ImGui.Dummy(new Vector2(18F, 18F));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) * 0.5F;
        var draw = ImGui.GetWindowDrawList();
        var color = ToPackedColor(EditorPalette.Text);
        if (rotation)
        {
            draw.AddCircle(center, 6F, color, 16, 1.4F);
            draw.AddTriangleFilled(center + new Vector2(4F, -6F), center + new Vector2(8F, -5F),
                center + new Vector2(6F, -1F), color);
        }
        else
        {
            draw.AddLine(center + new Vector2(-7F, 0F), center + new Vector2(7F, 0F), color, 1.4F);
            draw.AddLine(center + new Vector2(0F, -7F), center + new Vector2(0F, 7F), color, 1.4F);
            draw.AddCircleFilled(center, 2F, color, 8);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private void DrawComponentSummary(SceneObject selectedObject, ComponentRecord component)
    {
        if (SceneComponentTypes.IsTransform(component))
        {
            if (selectedObject.Transform.Mode == TransformMode.Free)
            {
                var position = selectedObject.Transform.Free.Position;
                var rotation = selectedObject.Transform.Free.Rotation;
                var positionValue = new NumVector3((float)position.X, (float)position.Y, (float)position.Z);
                DrawInspectorIcon(false, "对象在场景中的位置。");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1F);
                if (ImGui.InputFloat3("##TransformPosition", ref positionValue))
                {
                    selectedObject.Transform.SetFree(new FreeTransform(
                        new Double3(positionValue.X, positionValue.Y, positionValue.Z), rotation));
                    UpdateTransformMarker(selectedObject);
                    MarkSceneDocumentDirty();
                }
                ImGui.NewLine();
                var euler = selectedObject.Transform.GetEulerDegrees();
                var eulerValue = new NumVector3((float)euler.X, (float)euler.Y, (float)euler.Z);
                DrawInspectorIcon(true, "对象的 XYZ 旋转角度。");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1F);
                if (ImGui.InputFloat3("##TransformRotation", ref eulerValue))
                {
                    selectedObject.Transform.SetFreeEuler(
                        position,
                        new Double3(eulerValue.X, eulerValue.Y, eulerValue.Z));
                    UpdateTransformMarker(selectedObject);
                    MarkSceneDocumentDirty();
                }
            }
            else
            {
                var grid = selectedObject.Transform.Grid;
                DrawInspectorIcon(false, "体素对象的整数网格坐标。");
                ImGui.SameLine();
                ImGui.Text($"{grid.Position.X}, {grid.Position.Y}, {grid.Position.Z}");
                ImGui.NewLine();
                DrawInspectorIcon(true, "模型按 90 度为单位旋转。");
                ImGui.SameLine();
                var thirdWidth = Math.Max(46F, (ImGui.GetContentRegionAvail().X - 12F) / 3F);
                if (ImGui.Button("X +90°##TransformRotateX", new Vector2(thirdWidth, 0F)))
                    RotateModel(selectedObject, OrthogonalRotation.AroundX90);
                ImGui.SameLine(0F, 6F);
                if (ImGui.Button("Y +90°##TransformRotateY", new Vector2(thirdWidth, 0F)))
                    RotateModel(selectedObject, OrthogonalRotation.AroundY90);
                ImGui.SameLine(0F, 6F);
                if (ImGui.Button("Z +90°##TransformRotateZ", new Vector2(thirdWidth, 0F)))
                    RotateModel(selectedObject, OrthogonalRotation.AroundZ90);
            }
            return;
        }

        if (SceneComponentTypes.IsVoxelModel(component))
        {
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var model = data.Model;
            var part = data.Part;
            BeginInspectorField("模型", "对象引用的体素世界文件路径。");
            var changed = ImGui.InputText("##InspectorVoxelModel", ref model, 260);
            BeginInspectorField("部件", "模型中的部件名称；留空表示使用默认部件。");
            changed |= ImGui.InputText("##InspectorVoxelPart", ref part, 128);
            BeginInspectorField("体素数量", "当前模型包含的体素数量。");
            ImGui.TextDisabled($"{data.VoxelCount:N0}");
            BeginInspectorField("模板", "生成当前模型的示例模板名称。");
            ImGui.TextDisabled(data.Template switch { "" => "未指定", _ => data.Template });
            if (changed)
            {
                SceneComponentTypes.WriteVoxelModel(component,
                    data with { Model = model, Part = part });
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
            BeginInspectorField("视场角", "相机的视野角度。");
            var changed = ImGui.DragFloat("##InspectorCameraFov", ref fieldOfView, 0.1F, 20F, 120F, "%.1f°");
            BeginInspectorField("近裁剪", "相机近平面距离；过近可能导致几何体被裁掉。");
            changed |= ImGui.DragFloat("##InspectorCameraNear", ref nearClip, 0.01F, 0.001F, 1_000_000F, "%.3f");
            BeginInspectorField("远裁剪", "相机远平面距离；超过此距离的内容不会显示。");
            changed |= ImGui.DragFloat("##InspectorCameraFar", ref farClip, 1F, 0.002F, 100_000_000F, "%.1f");
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
                SceneComponentTypes.WriteCamera(component, new CameraComponentData(fieldOfView, nearClip, farClip, mask));
                MarkSceneDocumentDirty();
            }
            return;
        }

        if (SceneComponentTypes.IsLight(component))
        {
            var data = SceneComponentTypes.ReadLight(component);
            var intensity = data.Intensity;
            var color = new NumVector3(data.ColorR, data.ColorG, data.ColorB);
            var castShadows = data.CastShadows;
            BeginInspectorField("强度", "光照强度。");
            var changed = ImGui.DragFloat("##InspectorLightIntensity", ref intensity, 0.05F, 0F, 1000F, "%.2f");
            BeginInspectorField("颜色", "光照颜色。");
            changed |= ImGui.ColorEdit3("##InspectorLightColor", ref color, ImGuiColorEditFlags.Float);
            BeginInspectorField("阴影", "是否让该光源投射阴影。");
            changed |= ImGui.Checkbox("投射阴影##InspectorLightShadows", ref castShadows);
            if (changed)
            {
                SceneComponentTypes.WriteLight(component, new LightComponentData(
                    intensity, color.X, color.Y, color.Z, castShadows));
                MarkSceneDocumentDirty();
                _pipeline?.ResetAccumulation();
            }
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
        var current = sceneObject.Transform.Grid;
        sceneObject.Transform.SetGrid(new GridTransform(current.Position, current.Rotation.Compose(rotation)));
        UpdateTransformMarker(sceneObject);
        MarkSceneDocumentDirty();
        _componentWarning = null;
        _pipeline?.ResetAccumulation();
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
    }

    /// <summary>
    /// 绘制与检查器并列停靠的渲染配置窗口
    /// </summary>
    private void DrawRenderConfig()
    {
        if (!_showInspector)
        {
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
            ImGui.TextColored(ToColor(EditorPalette.Text), "渲染配置");
            DrawConfigurationPanel();
        }
        ImGui.End();
        _requestedInspectorTab = null;
    }

    private void DrawConfigurationPanel()
    {
        DrawSettingsPageHeader("视口渲染", _project is null
            ? "编辑器默认值 · 实时生效"
            : "当前项目值 · 实时生效");
        var activeRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject());
        ImGui.TextDisabled($"{(activeRenderSettings.Backend == RenderBackend.Gpu ? "显卡计算" : "CPU 计算")}  ·  " +
                           $"路径追踪 {(activeRenderSettings.PathTracingEnabled ? "开启" : "关闭")}  ·  " +
                           $"网格 {(_showGrid ? "显示" : "隐藏")}  ·  " +
                           $"体素轮廓 {(_showVoxelOutlines ? "显示" : "隐藏")}");
        if (ImGui.Button("打开完整设置", new Vector2(-1F, 32F)))
            OpenSettings(_project is null ? 0 : 1, _project is null ? 0 : 1);
        DrawLodSection(activeRenderSettings);

        DrawSettingsSectionHeader("场景视图辅助显示", "只影响视口辅助元素，不改变导出画面");
        ImGui.Checkbox("显示网格##scene", ref _showGrid);
        ImGui.Checkbox("体素棱线描边##scene", ref _showVoxelOutlines);
        DrawSettingsSectionHeader("渲染参数", "实时预览与导出配置的快捷入口");
        var before = _renderSettings.ToJson();
        DrawRenderSettingsPanel();
        if (_renderSettings.ToJson() != before)
        {
            _renderSettings.ClampAll();
            _renderSettingsDirty = true;
            _renderSettingsSaveCountdown = 2F;
        }
    }

    private bool _renderSettingsDirty;
    private float _renderSettingsSaveCountdown;
    private int _projectPathDialogMode;
    private string _projectPathText = string.Empty;
    private string _projectPathError = string.Empty;
    private string _projectBrowserPath = string.Empty;
    private string? _projectBrowserSelectedPath;
    private readonly Stack<string> _projectBrowserBack = new();
    private ProjectTemplateId _selectedProjectTemplate = ProjectTemplateId.Empty;

    private void DrawProjectAssets()
    {
        if (!_showProjectAssets)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(layout.LeftWidth, display.Y - layout.AssetsHeight - StatusHeight), new Vector2(display.X - layout.LeftWidth, layout.AssetsHeight));
        if (!ImGui.Begin("项目资产##资源浏览器", PanelWindowFlags))
        {
            ImGui.End();
            return;
        }

        _assetBrowser.Draw(_project, SetStatus, HandleAssetChanged, OpenWorldAsset, InstantiatePrefabAsset, ExportVoxAsset, OpenGeneratorAsset,
            ExportQubicleAsset,
            path => _sceneDocument is null ? 0 : AssetReferences.FindReferencingObjects(_sceneDocument, path).Count);
        var selectedAssetPath = _assetBrowser.SelectedAssetPath;
        if (!string.Equals(selectedAssetPath, _lastAssetSelectionPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastAssetSelectionPath = selectedAssetPath;
            if (selectedAssetPath.Length > 0)
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
        if (newPath is not null)
        {
            if (_sceneDocument is not null)
            {
                updatedReferences += AssetReferences.UpdateReferences(_sceneDocument, oldPath, newPath);
                if (updatedReferences > 0 && _activeSceneRelativePath is { } scenePath)
                {
                    try
                    {
                        SceneSerializer.Save(Path.Combine(_project.AssetsPath, scenePath.Replace('/', Path.DirectorySeparatorChar)), _sceneDocument);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        SetStatus($"引用更新已生效但场景保存失败：{exception.Message}");
                    }
                }
            }

            updatedReferences += AssetReferences.UpdatePrefabReferences(
                Path.Combine(_project.AssetsPath, "Prefabs"), oldPath, newPath);
            SetStatus(updatedReferences > 0
                ? $"已跟随更新 {updatedReferences} 处对 {oldPath} 的引用"
                : $"移动完成；未发现对 {oldPath} 的引用，请检查外部引用是否断链");
        }
        var activeScenePath = _activeSceneRelativePath;
        var touchesScene = string.Equals(oldPath, activeScenePath, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(newPath, activeScenePath, StringComparison.OrdinalIgnoreCase);
        var touchesModel = oldPath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) ||
                           oldPath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase) ||
                           newPath?.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) == true ||
                           newPath?.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase) == true;
        if (touchesScene)
        {
            if (string.Equals(oldPath, activeScenePath, StringComparison.OrdinalIgnoreCase) &&
                newPath?.EndsWith(".mvox", StringComparison.OrdinalIgnoreCase) == true)
            {
                _activeSceneRelativePath = newPath;
            }
            _sceneDocument = LoadSceneDocument(_project);
            _sceneDocumentDirty = false;
            _selectedSceneObjectId = null;
            _renderCameraId = null;
            _renderCameraInitialFramePending = true;
        }
        if (touchesScene || touchesModel)
        {
            LoadWorldForProject(_project);
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }

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
            ImGui.TextWrapped("这是由 JavaScript 插件注册的编辑器面板");
            ImGui.TextWrapped("面板内容只能通过宿主白名单接口访问项目数据和编辑器命令");
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
        var exportCount = _exportQueue.Count + (_activeExport is null ? 0 : 1);
        if (exportCount > 0)
        {
            ImGui.SetCursorPosX(Math.Max(8F, display.X - 360F));
            ImGui.TextDisabled($"导出队列 {exportCount}");
            ImGui.SameLine();
            var progress = _activeExport is null || _pipeline is null || _activeExport.Settings.PathMaxAccumulatedSamples <= 0
                ? 0F
                : Math.Clamp((float)_pipeline.AccumulatedSamples / _activeExport.Settings.PathMaxAccumulatedSamples, 0F, 1F);
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

    private ImGuiWindowFlags PanelWindowFlags => ImGuiWindowFlags.NoCollapse;

    private LayoutMetrics GetLayoutMetrics(Vector2 display)
    {
        var metrics = _layoutPreset switch
        {
            LayoutPreset.Modeling => new LayoutMetrics(270F, 360F, 250F),
            LayoutPreset.Rendering => new LayoutMetrics(210F, 400F, 300F),
            _ => new LayoutMetrics(245F, 360F, 280F),
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
        _applyPreset = true;
    }

    private void ShowProjectPathDialog(bool create)
    {
        _projectPathDialogMode = create ? 1 : 2;
        if (create) _selectedProjectTemplate = ProjectTemplateId.Empty;
        _projectPathText = create
            ? Path.Combine(DefaultProjectRootPath, "新项目")
            : string.Empty;
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
        if (ImGui.Begin(_projectPathDialogMode == 1 ? "新建 MVoxel 项目" : "打开 MVoxel 项目", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            if (_projectPathDialogMode == 1)
            {
                DrawProjectTemplatePicker();
            }

            if (_projectPathDialogMode == 1)
            {
                ImGui.SetNextItemWidth(540F);
                ImGui.InputText("项目路径", ref _projectPathText, 2048);
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

        if (_projectPathDialogMode == 1) BeginCreateProject(path, _selectedProjectTemplate, fromWelcome: false);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        ImGui.TextDisabled("模板不是装饰场景：每个模板都对应一条明确的编辑或渲染练习路径。");
        if (ImGui.BeginCombo("项目模板", selected.Name))
        {
            foreach (var template in ProjectTemplateCatalog.All)
            {
                var isSelected = template.Id == _selectedProjectTemplate;
                if (ImGui.Selectable($"{template.Name}  ·  {template.Scale}", isSelected)) _selectedProjectTemplate = template.Id;
                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled($"规模：{selected.Scale}    重点：{selected.Focus}");
        ImGui.TextWrapped(selected.Description);
        ImGui.TextColored(ToColor(EditorPalette.Accent), $"学习目标：{selected.LearningGoal}");
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
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在建立标准资产目录…", .34F, CanCancel: true));
                        cancellation.ThrowIfCancellationRequested();
                        // 从这里开始由 ProjectLayout 使用原子文件写入
                        // 完成小规模提交比留下半成品项目更安全
                        report(new BlockingOperationProgress("正在写入项目文件…", .68F, CanCancel: false));
                        var project = ProjectLayout.Create(absolutePath, name);
                        ProjectTemplateCatalog.Populate(project, templateId,
                            (detail, fraction, canCancel) => report(new BlockingOperationProgress(detail, fraction, canCancel)));
                        var projectSettings = SettingsStore.LoadProject(project);
                        projectSettings.ShowGrid = _editorSettings.ShowSceneGrid;
                        projectSettings.ShowVoxelOutlines = _editorSettings.ShowVoxelOutlines;
                        projectSettings.SceneMoveSpeed = _editorSettings.SceneMoveSpeed;
                        projectSettings.SceneNearClip = _editorSettings.SceneNearClip;
                        projectSettings.SceneFarClip = _editorSettings.SceneFarClip;
                        projectSettings.SceneFieldOfView = _editorSettings.SceneFieldOfView;
                        projectSettings.SceneRenderSettings = _editorSettings.SceneRenderSettings;
                        projectSettings.RenderSettings = _editorSettings.RenderViewSettings;
                        projectSettings.ExportRenderSettings = _editorSettings.ExportRenderSettings;
                        projectSettings.RenderSettings.UseHardwareRayTracing = _editorSettings.UseHardwareRayTracing;
                        projectSettings.ExportRenderSettings.UseHardwareRayTracing = _editorSettings.UseHardwareRayTracing;
                        SettingsStore.SaveProject(project, projectSettings);
                        report(new BlockingOperationProgress("正在完成项目初始化…", .98F, CanCancel: false));
                        return project;
                    },
                    project => _pendingActions.Enqueue(() => OpenProject(project)),
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

    private void BeginOpenProject(string path, bool fromWelcome)
    {
        try
        {
            var absolutePath = Path.GetFullPath(path.Trim().Trim('"'));
            if (!_projectOperation.TryStart(
                    "正在加载 MVoxel 项目",
                    "正在检查项目文件…",
                    (cancellation, report) =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在读取项目清单…", .18F, CanCancel: true));
                        var project = ProjectLayout.Open(absolutePath);
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress("正在扫描资产目录…", .76F, CanCancel: true));
                        foreach (var directory in ProjectLayout.AssetDirectories)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            _ = Directory.Exists(Path.Combine(project.AssetsPath, directory));
                        }

                        report(new BlockingOperationProgress("正在准备编辑器工作区…", .96F, CanCancel: true));
                        return project;
                    },
                    project => _pendingActions.Enqueue(() => OpenProject(project)),
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

    private void OpenProject(ProjectHandle project)
    {
        _pluginRuntime?.Dispose();
        _pluginRuntime = new PluginScriptRuntime();
        _project = project;
        _activeSceneRelativePath = "Scenes/主场景.mvox";
        _assetBrowser.Reset();
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
        _autoSaveCountdown = _projectSettings.AutoSaveSeconds;
        _autoSaveQueued = false;
        LoadWorldForProject(project);
        TryRestoreRecovery(project);
        ReloadMaterialTable();
        _pluginRuntime.Activate(project, TimeSpan.FromSeconds(2));
        _statusText = _sceneValidationErrors.Count == 0
            ? $"已打开项目：{project.Manifest.Name}"
            : $"已打开项目：{project.Manifest.Name}（主场景存在 {_sceneValidationErrors.Count} 个结构问题：{_sceneValidationErrors[0]}）";
        ProjectOpened?.Invoke(this, EventArgs.Empty);
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
        _pluginRuntime?.Dispose();
        _pluginRuntime = null;
        SaveProjectSettings();
        _project = null;
        _sceneDocument = null;
        _activeSceneRelativePath = "Scenes/主场景.mvox";
        _sceneDocumentDirty = false;
        _renderCameraId = null;
        _renderCameraInitialFramePending = true;
        _assetBrowser.Reset();
        _lastAssetSelectionPath = string.Empty;
        ResetGeneratorPanel();
        _world = new VoxelWorld();
        _layerOwnership.Clear();
        _editingJournal = null;
        _voxelHistory.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
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

    private void RequestCloseProject()
    {
        if (_project is null) return;
        if (_sceneDocumentDirty)
        {
            _closeProjectDialogRequested = true;
            return;
        }

        CloseProject();
    }

    private void DrawCloseProjectDialog()
    {
        if (_closeProjectDialogRequested)
        {
            ImGui.OpenPopup("关闭项目##UnsavedScene");
            _closeProjectDialogRequested = false;
        }

        if (!ImGui.BeginPopupModal("关闭项目##UnsavedScene", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("当前场景有未保存修改。请选择如何处理这些修改。");
        ImGui.Spacing();
        if (ImGui.Button("保存并关闭", new Vector2(120F, 30F)))
        {
            SaveProject();
            if (!_sceneDocumentDirty)
            {
                ImGui.CloseCurrentPopup();
                CloseProject();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("不保存关闭", new Vector2(120F, 30F)))
        {
            ImGui.CloseCurrentPopup();
            CloseProject();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90F, 30F))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void SaveProject(bool automatic = false)
    {
        if (_project is null)
        {
            if (!automatic) SetStatus("没有可保存的项目");
            return;
        }

        ProjectLayout.EnsureDirectories(_project.RootPath);
        SaveProjectSettings();
        if (SaveWorldToProject(_project))
        {
            _sceneDocumentDirty = false;
            _autoSaveCountdown = _projectSettings.AutoSaveSeconds;
            RecoveryStore.Clear(_project.RootPath);
            SetStatus(automatic
                ? $"项目已自动保存：{_project.Manifest.Name}"
                : $"项目已保存：{_project.Manifest.Name}（场景与体素世界，{_world.OccupiedVoxelCount} 体素）");
        }
    }

    private void TryAutoSaveProject()
    {
        _autoSaveQueued = false;
        if (_project is null || !_projectSettings.AutoSave || !_sceneDocumentDirty) return;

        try
        {
            // 自动保存只写恢复区快照，不触碰用户的正式保存文件
            if (_world.OccupiedVoxelCount > 0)
            {
                Voxels.LargeWorldFormat.Write(RecoveryStore.WorldFilePath(_project.RootPath), _world);
            }

            RecoveryStore.Write(_project.RootPath,
                Scene.SceneSerializer.ToJson(_sceneDocument ?? new Scene.SceneDocument { Name = "主场景" }),
                worldModelSource: null);
            _editingJournal?.Clear();
            _autoSaveCountdown = _projectSettings.AutoSaveSeconds;
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
            if (File.Exists(snapshot.WorldModelPath))
            {
                _world = Voxels.LargeWorldFormat.Read(snapshot.WorldModelPath);
                // 快照之后的事务重放：日志逐格还原未落盘的编辑，还原后清空日志
                var journalPath = Path.Combine(RecoveryStore.DirectoryFor(project.RootPath), "journal.jsonl");
                var replayed = VoxelEditingJournal.Replay(_world, VoxelEditingJournal.TryRead(journalPath));
                VoxelEditingJournal.Clear(journalPath);
                if (replayed > 0)
                {
                    MVoxelLog.Info($"事务重放完成：{replayed} 格，{project.Manifest.Name}");
                }

                // 恢复区载入也延后到渲染视图按需同步，避免打开项目回调阻塞 UI。
                _composedRenderWorld = null;
                _composedSignature = 0;
                _composedMask = uint.MaxValue;
                _renderViewNeedsInitialRender = true;
                _pipeline?.ResetAccumulation();
                FrameCameraOnWorld();
            }

            _sceneDocument = restored;
            _voxelHistory.Clear();
            MarkSceneDocumentDirty();
            SetStatus($"已从恢复区还原未保存工作（{snapshot.SavedAt.ToLocalTime():MM-dd HH:mm}），确认无误后请正式保存");
            MVoxelLog.Info($"已套用恢复区快照：{project.Manifest.Name}，保存于 {snapshot.SavedAt:O}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            MVoxelLog.Error("恢复区快照套用失败，已按正式保存内容打开", exception);
            SetStatus($"恢复区快照不可用，已打开正式保存内容：{exception.Message}");
            RecoveryStore.Clear(project.RootPath);
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

        private static void Copy(SceneObject target, SceneObject source)
        {
            target.Name = source.Name;
            target.ParentId = source.ParentId;
            target.Visible = source.Visible;
            target.Locked = source.Locked;
            target.Transform = source.Transform;
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
    }
}
