using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
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
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly string? _startupProjectPath;
    private string? _imguiLayoutPath;
    private VulkanImGuiController? _controller;
    private VulkanWindowContext? _vulkan;
    private VulkanImGuiRenderer? _imguiRenderer;
    private readonly EditorFontCatalog _fonts = new();
    private readonly ProjectAssetBrowser _assetBrowser = new();
    private readonly BlockingOperationRunner _projectOperation = new();
    private ImGuiWelcomeWindow? _welcomeWindow;
    private bool _showWelcomeRequested;
    private IntPtr _imguiContext;
    private ProjectHandle? _project;
    private bool _showHierarchy = true;
    private bool _showProjectAssets = true;
    private bool _showInspector = true;
    private int _centerTab;
    private int? _requestedCenterTab;
    private int? _requestedInspectorTab;
    private SceneDocument? _sceneDocument;
    private StableId? _renderCameraId;
    private bool _applyPreset;
    private bool _editPanelLayout;
    private LayoutPreset _layoutPreset = LayoutPreset.Default;
    private string _statusText = "就绪";
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
    private int _editorLodLevels = 4;
    private int _projectLodLevels = 4;
    private readonly float[] _editorLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private readonly float[] _projectLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private bool _showGrid = true;
    private bool _showVoxelOutlines = true;
    private bool _sceneMouseCaptured;
    private ViewportCaptureTarget _capturedViewport;

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

            if (_welcomeWindow is not null && !_welcomeWindow.PumpFrame(Math.Max(args.Time, 1.0 / 120.0)))
            {
                _welcomeWindow.Dispose();
                _welcomeWindow = null;
            }
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
                automations = _pluginRuntime?.Automations ?? [],
                commands = _pluginRuntime?.Commands ?? [],
                panels = _pluginRuntime?.Panels ?? [],
                pluginDiagnostics = _project is null ? Array.Empty<ProjectPluginDiagnostic>() : PluginManager.Diagnose(_project),
            },
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
        _welcomeWindow = new ImGuiWelcomeWindow(this);
        _welcomeWindow.InitializeForManualLoop();
        // 独立欢迎窗口使用独立 Vulkan Surface
        // 继续当前帧前恢复编辑器 ImGui 上下文
        ImGui.SetCurrentContext(_imguiContext);
            // 把独立欢迎窗口置于被阻塞主窗口上层
            // 显式转移焦点，保证欢迎窗口接收用户输入
        _welcomeWindow.Focus();
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
        _imguiLayoutPath = layoutPath;
        var savedLayout = File.Exists(layoutPath) ? File.ReadAllText(layoutPath) : string.Empty;
        var hasCurrentPanelLayout = savedLayout.Contains("DockSpace ID=0x6A3C3001", StringComparison.Ordinal) &&
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
        var welcomeIsOpen = _welcomeWindow is not null;
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
        DrawStatusBar();
        DrawProjectPathDialog();
        if (inputBlocked) ImGui.EndDisabled();
        if (!inputBlocked) DrawSettingsWindow();
        if (welcomeIsOpen)
        {
            BlockingOperationDialog.DrawInteractionBlocker("欢迎窗口已打开", "请先在“欢迎使用 MVoxel”窗口中创建或打开项目。");
        }
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
            if (ImGui.MenuItem("关闭项目", enabled: _project is not null)) CloseProject();
            if (ImGui.MenuItem("退出", "Alt+F4")) Close();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("视图"))
        {
            if (ImGui.MenuItem("场景窗口", "F1")) _requestedCenterTab = 0;
            if (ImGui.MenuItem("渲染窗口", "F2")) _requestedCenterTab = 1;
            ImGui.Separator();
            if (ImGui.MenuItem("层级", null, ref _showHierarchy)) { }
            if (ImGui.MenuItem("项目资产", null, ref _showProjectAssets)) { }
            if (ImGui.MenuItem("检查器", null, ref _showInspector)) { }
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
            if (ImGui.MenuItem("相机", null, _activeTool == EditorTool.Camera)) _activeTool = EditorTool.Camera;
            if (ImGui.MenuItem("选择", null, _activeTool == EditorTool.Pick)) _activeTool = EditorTool.Pick;
            if (ImGui.MenuItem("绘制", null, _activeTool == EditorTool.Draw)) _activeTool = EditorTool.Draw;
            if (ImGui.MenuItem("擦除", null, _activeTool == EditorTool.Erase)) _activeTool = EditorTool.Erase;
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
            if (ImGui.MenuItem("快速画面设置")) _requestedInspectorTab = 1;
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("帮助"))
        {
            ImGui.MenuItem("MVoxel 体素建模软件");
            ImGui.MenuItem("在场景中旋转、平移和缩放，观察你的作品");
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
      DockNode ID=0x6A3C3005 Parent=0x6A3C3004 SizeRef=880,615 Selected=0xD7AC1002
      DockNode ID=0x6A3C3006 Parent=0x6A3C3004 SizeRef=300,615 Selected=0xD7AC1003
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
                    var roots = objects.Where(item => item.ParentId is null).ToArray();
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
        var expanded = ImGui.TreeNodeEx(nodeId, nodeFlags);
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowClicked = ImGui.IsItemClicked();

        var iconWidth = 22F;
        var iconGap = 2F;
        var iconsStart = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - iconWidth * 2F - iconGap - 8F;
        ImGui.SetCursorScreenPos(new Vector2(iconsStart, rowMin.Y + (rowMax.Y - rowMin.Y - 20F) * 0.5F));
        var visibilityClicked = DrawHierarchyIconButton($"##Visibility_{sceneObject.Id}", "显示对象", "隐藏对象",
            sceneObject.Visible, DrawVisibilityIcon);
        if (visibilityClicked)
        {
            sceneObject.Visible = !sceneObject.Visible;
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }

        ImGui.SameLine(0F, iconGap);
        var selectableClicked = DrawHierarchyIconButton($"##Selectable_{sceneObject.Id}", "允许选中对象", "禁止选中对象",
            !sceneObject.Locked, DrawSelectableIcon);
        if (selectableClicked)
        {
            sceneObject.Locked = !sceneObject.Locked;
            if (sceneObject.Locked && _selectedSceneObjectId == sceneObject.Id)
            {
                _selectedSceneObjectId = null;
            }
        }

        if (rowClicked && !visibilityClicked && !selectableClicked && !sceneObject.Locked)
        {
            _selectedSceneObjectId = sceneObject.Id;
            _selectedVoxel = null;
        }

        if (children.Length > 0 && expanded)
        {
            foreach (var child in children)
            {
                DrawSceneObjectNode(child, objects);
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
        Action<ImDrawListPtr, Vector2, Vector2, uint, bool> drawIcon)
    {
        var clicked = ImGui.InvisibleButton(id, new Vector2(20F, 20F));
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
    /// 绘制对象可选中状态图标
    /// </summary>
    /// <param name="drawList">目标绘制列表</param>
    /// <param name="min">图标左上角</param>
    /// <param name="max">图标右下角</param>
    /// <param name="color">图标颜色</param>
    /// <param name="active">当前是否允许选中</param>
    private static void DrawSelectableIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint color, bool active)
    {
        var center = (min + max) * 0.5F;
        drawList.AddCircle(center, 5.3F, color, 16, 1.3F);
        drawList.AddLine(center - new Vector2(8F, 0F), center + new Vector2(8F, 0F), color, 1.2F);
        drawList.AddLine(center - new Vector2(0F, 8F), center + new Vector2(0F, 8F), color, 1.2F);
        if (!active)
        {
            drawList.AddLine(min + new Vector2(3F, 3F), max - new Vector2(3F, 3F), color, 1.8F);
        }
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

        // 场景视图始终使用同一条纯色 GPU 绘制路径，交互时也不切换成另一套画面
        var showVoxelWorld = IsVoxelWorldVisible();
        var gpuRendered = showVoxelWorld && _project is not null &&
                          TryRenderGpuSceneView(drawList, origin, size, camera);
        if (showVoxelWorld && _project is not null && !gpuRendered)
        {
            // GPU 不可用时仍保持网格在体素之前绘制，避免网格盖住模型
            if (_showGrid)
            {
                SceneGrid.Draw(drawList, origin, size, camera, _sceneCameraDistance);
            }

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
        if (showVoxelWorld)
        {
            DrawSceneWorldAxes(drawList, origin, size, camera);
        }

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
    /// 绘制场景窗口右上角的相机设置按钮
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
        if (_welcomeWindow is not null || _projectOperation.IsRunning)
        {
            EndSceneMouseCapture(captureTarget);
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
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
    private bool IsVoxelWorldVisible() => GetVoxelWorldObject()?.Visible ?? true;

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
        const int maximumPreviewVoxels = 4096;
        var preview = _world.EnumerateOccupied()
            .Take(maximumPreviewVoxels)
            .Select(item => new PreviewVoxel(
                new Vector3((float)item.Coordinate.X + 0.5F, (float)item.Coordinate.Y + 0.5F,
                    (float)item.Coordinate.Z + 0.5F),
                Color.FromArgb(item.Value.Color.A, item.Value.Color.R, item.Value.Color.G, item.Value.Color.B)))
            .OrderByDescending(item => Vector3.Dot(item.Center - camera.Position, camera.Forward));

        foreach (var voxel in preview)
        {
            DrawVoxelCube(drawList, voxel.Center, 1F, voxel.Color, origin, size, camera, showOutlines);
        }
    }

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
        var panelMin = center - new Vector2(radius + 10F);
        var panelMax = center + new Vector2(radius + 10F);
        drawList.AddRectFilled(panelMin, panelMax, ToPackedColor(Color.FromArgb(205, 28, 36, 48)), 5F);
        drawList.AddRect(panelMin, panelMax, ToPackedColor(Color.FromArgb(210, 155, 173, 194)), 5F, 0, 1.2F);

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
            drawList.AddText(item.point + new Vector2(7F, -7F), color, item.axis.Label);
        }

        var mouse = io.MousePos;
        if (!hovered)
        {
            return Vector2.Distance(mouse, center) <= radius + 8F;
        }

        drawList.AddRect(panelMin - new Vector2(2F), panelMax + new Vector2(2F),
            ToPackedColor(EditorPalette.Accent), 6F, 0, 1.2F);
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
        HandleRenderCameraNavigation(origin, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(Color.FromArgb(255, 14, 18, 24)));
        var camera = GetRenderViewCamera();
        var showVoxelWorld = IsVoxelWorldVisible();
        var gpuRendered = showVoxelWorld && _project is not null && TryRenderGpuPathView(drawList, origin, size, camera);
        if (!gpuRendered)
        {
            if (showVoxelWorld)
            {
                DrawVoxelPreview(drawList, origin, size, camera, showOutlines: false);
            }
            ImGui.SetCursorScreenPos(origin + new Vector2(
                Math.Max(0F, (size.X - ImGui.CalcTextSize("GPU 路径追踪不可用").X) / 2F), size.Y / 2F));
            ImGui.TextDisabled(showVoxelWorld
                ? _pipelineStatus.Length > 0 ? "GPU 路径追踪不可用 · 当前显示 CPU 预览" : "GPU 路径追踪初始化中"
                : "体素世界已隐藏");
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

    private void DrawRenderViewOverlay(Vector2 origin, Vector2 size, ImDrawListPtr drawList)
    {
        _ = drawList;
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F));
        var pipeline = _pipeline;
        var backendName = _renderSettings.Backend == RenderBackend.Cpu || _gpuRenderingDisabled
            ? "CPU 渲染预览"
            : pipeline?.UsingHardwareRayTracing == true
                ? "Vulkan 硬件 RT"
                : "Vulkan Compute 路径追踪";
        ImGui.TextColored(ToColor(EditorPalette.Accent), backendName);
        if (pipeline?.HardwareRayTracingError is { Length: > 0 } hardwareError && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(hardwareError);
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F + ImGui.GetTextLineHeightWithSpacing()));
        if (ImGui.SmallButton("重新计算"))
        {
            _pipeline?.ResetAccumulation();
        }

        ImGui.SameLine();
        var canExport = _pipeline?.CanSaveDisplayPng == true;
        ImGui.BeginDisabled(!canExport);
        if (ImGui.SmallButton("导出 PNG"))
        {
            ExportRenderPng();
        }
        ImGui.EndDisabled();
        if (!canExport && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("当前 Vulkan 显示结果尚未准备好");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{(int)_renderSettings.CameraFovDegrees}° · 尺寸 {(int)size.X}×{(int)size.Y}");
    }

    private void ExportRenderPng()
    {
        if (_pipeline is not { IsAvailable: true } pipeline)
        {
            SetStatus("GPU 渲染不可用，无法导出");
            return;
        }

        var projectPath = _project?.RootPath
            ?? AppPaths.DataDirectory;
        var fileName = $"渲染-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var path = Path.Combine(projectPath, "Assets", "Exports", fileName);
        if (pipeline.SaveDisplayPng(path))
        {
            SetStatus($"图片已导出：{path}");
        }
        else
        {
            SetStatus("导出失败：帧缓冲不可读");
        }
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
            ImGui.TextColored(ToColor(EditorPalette.Text), "检查器");
            DrawInspectorProperties();
        }
        ImGui.End();
    }

    /// <summary>
    /// 绘制检查器的对象属性内容
    /// </summary>
    private void DrawInspectorProperties()
    {
        ImGui.Text("项目");
        ImGui.SameLine(90F);
        ImGui.TextColored(ToColor(EditorPalette.Text), _project?.Manifest.Name ?? "未打开项目");
        ImGui.Text("路径");
        ImGui.SameLine(90F);
        ImGui.TextWrapped(_project?.RootPath ?? "欢迎使用 MVoxel");
        ImGui.SeparatorText("选择");
        var selectedObject = _selectedSceneObjectId is { } selectedObjectId
            ? _sceneDocument?.Objects.FirstOrDefault(item => item.Id == selectedObjectId)
            : null;
        if (selectedObject is not null)
        {
            if (!string.IsNullOrWhiteSpace(_componentWarning))
                ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), $"警告：{_componentWarning}");
            ImGui.Text($"对象  {selectedObject.Name}");
            ImGui.Text($"组件  {selectedObject.Components.Count}");
            var visible = selectedObject.Visible;
            if (ImGui.Checkbox("可见##InspectorObjectVisibility", ref visible))
            {
                selectedObject.Visible = visible;
                _pipeline?.ResetAccumulation();
                _accumulationSignature = string.Empty;
            }

            var locked = selectedObject.Locked;
            if (ImGui.Checkbox("禁止选中##InspectorObjectLock", ref locked))
            {
                selectedObject.Locked = locked;
            if (locked)
            {
                _selectedSceneObjectId = null;
            }

            ImGui.SeparatorText("组件");
            for (var componentIndex = 0; componentIndex < selectedObject.Components.Count; componentIndex++)
            {
                var component = selectedObject.Components[componentIndex];
                var definition = SceneComponentTypes.BuiltInDefinitions.FirstOrDefault(item => item.TypeId == component.TypeId);
                var label = definition is null
                    ? $"{SceneComponentTypes.GetDisplayName(component)} · 插件"
                    : $"{definition.DisplayName} · {definition.Category}";
                if (!ImGui.TreeNodeEx($"{label}##InspectorComponent{componentIndex}", ImGuiTreeNodeFlags.SpanAvailWidth))
                    continue;
                DrawComponentSummary(selectedObject, component);
                ImGui.TreePop();
            }

            if (ImGui.BeginCombo("添加组件##InspectorAddComponent", "选择组件"))
            {
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
                }

                foreach (var definition in _pluginRuntime?.Components ?? [])
                {
                    if (selectedObject.Components.Any(item => item.TypeId == definition.Id)) continue;
                    if (!ImGui.Selectable($"{definition.Name} · {definition.Category}")) continue;
                    selectedObject.Components.Add(new ComponentRecord
                    {
                        TypeId = definition.Id,
                        SchemaVersion = 1,
                        Data = JsonSerializer.SerializeToElement(new { }),
                    });
                }
                ImGui.EndCombo();
            }
        }
        }

        if (_selectedVoxel is { } selected)
        {
            var voxel = _world.TryGet(selected.Coordinate, out var existing) ? existing : default;
            ImGui.Text($"坐标  {selected.Coordinate.X}, {selected.Coordinate.Y}, {selected.Coordinate.Z}");
            ImGui.Text($"颜色  #{voxel.Color.R}, {voxel.Color.G}, {voxel.Color.B} · A{voxel.Color.A}");
            var flags = voxel.Flags;
            ImGui.TextDisabled($"材质标志  {(VoxelFlags.Has(flags, VoxelFlags.Emissive) ? "自发光 " : string.Empty)}" +
                               $"{(VoxelFlags.Has(flags, VoxelFlags.Translucent) ? "半透明" : string.Empty)}" +
                               (VoxelFlags.Has(flags, VoxelFlags.Emissive | VoxelFlags.Translucent) ? string.Empty : "不透明"));
            if (ImGui.Button("取为画笔颜色"))
            {
                var nearest = 0;
                var best = int.MaxValue;
                for (var index = 0; index < _palette.Length; index++)
                {
                    var candidate = _palette[index];
                    var distance = Math.Abs(candidate.R - voxel.Color.R) +
                                   Math.Abs(candidate.G - voxel.Color.G) +
                                   Math.Abs(candidate.B - voxel.Color.B) +
                                   Math.Abs(candidate.A - voxel.Color.A) / 2;
                    if (distance >= best) continue;
                    best = distance;
                    nearest = index;
                }
                _paletteIndex = nearest;
                SetStatus($"画笔颜色已取自选中的体素（色板 #{nearest}）");
            }
        }
        else if (selectedObject is null)
        {
            ImGui.TextDisabled("未选择对象 · 使用“选择”工具点击体素");
        }

        ImGui.SeparatorText("体素世界");
        ImGui.Text($"占用体素  {_world.OccupiedVoxelCount:N0}");
        ImGui.Text($"驻留区块  {_world.LoadedChunkCount:N0} × 32³");
        ImGui.Text($"撤销历史  {_voxelHistory.UndoCount} 步");
        ImGui.TextDisabled("坐标  0, 0, 0");
        ImGui.SeparatorText("材质");
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
                if (ImGui.InputFloat3("位置##TransformPosition", ref positionValue))
                {
                    selectedObject.Transform.SetFree(new FreeTransform(
                        new Double3(positionValue.X, positionValue.Y, positionValue.Z), rotation));
                    UpdateTransformMarker(selectedObject);
                }
                var euler = selectedObject.Transform.GetEulerDegrees();
                var eulerValue = new NumVector3((float)euler.X, (float)euler.Y, (float)euler.Z);
                if (ImGui.InputFloat3("旋转 XYZ（度）##TransformRotation", ref eulerValue))
                {
                    selectedObject.Transform.SetFreeEuler(
                        position,
                        new Double3(eulerValue.X, eulerValue.Y, eulerValue.Z));
                    UpdateTransformMarker(selectedObject);
                }
            }
            else
            {
                var grid = selectedObject.Transform.Grid;
                ImGui.Text($"整数位置  {grid.Position.X}, {grid.Position.Y}, {grid.Position.Z}");
                ImGui.TextDisabled("模型旋转单位  90°");
                if (ImGui.Button("绕 X 轴 90°##TransformRotateX"))
                    RotateModel(selectedObject, OrthogonalRotation.AroundX90);
                ImGui.SameLine();
                if (ImGui.Button("绕 Y 轴 90°##TransformRotateY"))
                    RotateModel(selectedObject, OrthogonalRotation.AroundY90);
                ImGui.SameLine();
                if (ImGui.Button("绕 Z 轴 90°##TransformRotateZ"))
                    RotateModel(selectedObject, OrthogonalRotation.AroundZ90);
            }
            return;
        }

        if (SceneComponentTypes.IsVoxelModel(component))
        {
            ImGui.Text("模型");
            ImGui.SameLine(72F);
            ImGui.Text(component.Data.TryGetProperty("model", out var model) ? model.GetString() ?? "未指定" : "未指定");
            ImGui.Text("部件");
            ImGui.SameLine(72F);
            ImGui.Text(component.Data.TryGetProperty("part", out var part) ? part.GetString() ?? "模型" : "模型");
            return;
        }

        if (SceneComponentTypes.IsCamera(component))
        {
            var data = SceneComponentTypes.ReadCamera(component);
            ImGui.Text($"视场角  {data.FieldOfViewDegrees:0.##}°");
            ImGui.Text($"近裁剪  {data.NearClip:0.###}");
            ImGui.Text($"远裁剪  {data.FarClip:0.###}");
            return;
        }

        if (SceneComponentTypes.IsLight(component))
        {
            var data = SceneComponentTypes.ReadLight(component);
            ImGui.Text($"强度  {data.Intensity:0.##}");
            ImGui.Text($"颜色  {data.ColorR:0.##}, {data.ColorG:0.##}, {data.ColorB:0.##}");
            ImGui.Text($"阴影  {(data.CastShadows ? "开启" : "关闭")}");
            return;
        }

        ImGui.TextDisabled($"类型 ID  {component.TypeId}");
        ImGui.TextDisabled("由插件提供组件数据和编辑命令");
    }

    private void RotateModel(SceneObject sceneObject, OrthogonalRotation rotation)
    {
        if (sceneObject.Transform.Mode != TransformMode.Grid) return;
        var current = sceneObject.Transform.Grid;
        sceneObject.Transform.SetGrid(new GridTransform(current.Position, current.Rotation.Compose(rotation)));
        UpdateTransformMarker(sceneObject);
        _componentWarning = null;
        _pipeline?.ResetAccumulation();
    }

    private static void UpdateTransformMarker(SceneObject sceneObject)
    {
        var transform = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsTransform);
        if (transform is not null)
            transform.Data = JsonSerializer.SerializeToElement(new { kind = sceneObject.Transform.Mode.ToString() });
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
        ImGui.TextColored(ToColor(EditorPalette.Accent), "LOD 与画面设置");
        if (ImGui.Button("打开完整设置")) OpenSettings(_project is null ? 0 : 1, _project is null ? 0 : 1);
        if (ImGui.CollapsingHeader("LOD 等级"))
        {
            ImGui.PushItemWidth(Math.Max(64F, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("第 8 级距离").X - 16F));
            ImGui.SliderInt("等级数量##editor", ref _editorLodLevels, 1, _editorLodDistances.Length);
            for (var index = 0; index < _editorLodLevels; index++)
            {
                ImGui.SliderFloat($"第 {index + 1} 级距离##editor", ref _editorLodDistances[index], 1F, 16384F, "%.0f");
            }

            ImGui.SliderInt("等级数量##project", ref _projectLodLevels, 1, _projectLodDistances.Length);
            for (var index = 0; index < _projectLodLevels; index++)
            {
                ImGui.SliderFloat($"第 {index + 1} 级距离##project", ref _projectLodDistances[index], 1F, 16384F, "%.0f");
            }

            ImGui.PopItemWidth();
        }

        ImGui.SeparatorText("场景视图辅助显示");
        ImGui.Checkbox("显示网格##scene", ref _showGrid);
        ImGui.Checkbox("体素棱线描边##scene", ref _showVoxelOutlines);
        ImGui.SeparatorText("渲染设置（GPU 计算 · 实时生效）");
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

        _assetBrowser.Draw(_project, SetStatus);

        ImGui.End();
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
        ImGui.End();
    }

    private static float MenuHeight => ImGui.GetFrameHeight();
    private static float WorkspaceTop => MenuHeight;
    private static float StatusHeight => ImGui.GetTextLineHeight() + 16F;

    private ImGuiWindowFlags PanelWindowFlags => ImGuiWindowFlags.NoCollapse;

    private LayoutMetrics GetLayoutMetrics(Vector2 display)
    {
        var metrics = _layoutPreset switch
        {
            LayoutPreset.Modeling => new LayoutMetrics(280F, 300F, 250F),
            LayoutPreset.Rendering => new LayoutMetrics(210F, 380F, 300F),
            _ => new LayoutMetrics(245F, 320F, 280F),
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
        ImGui.SetNextWindowSize(new Vector2(580F, 0F), ImGuiCond.Appearing);
        if (ImGui.Begin(_projectPathDialogMode == 1 ? "新建 MVoxel 项目" : "打开 MVoxel 项目", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            if (_projectPathDialogMode == 1)
            {
                DrawProjectTemplatePicker();
            }

            ImGui.SetNextItemWidth(540F);
            ImGui.InputText("项目路径", ref _projectPathText, 2048);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_projectPathDialogMode == 1
                ? "输入新项目文件夹，模板资源将由编辑器生成"
                : "输入已有 MVoxel 项目文件夹");
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
        var path = _projectPathText.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            _projectPathError = "请先输入项目文件夹路径。";
            return;
        }

        if (_projectPathDialogMode == 1) BeginCreateProject(path, _selectedProjectTemplate, fromWelcome: false);
        else BeginOpenProject(path, fromWelcome: false);
    }

    /// <summary>
    /// 绘制编辑器内新建项目的模板选择器
    /// </summary>
    private void DrawProjectTemplatePicker()
    {
        var selected = ProjectTemplateCatalog.Get(_selectedProjectTemplate);
        if (ImGui.BeginCombo("项目模板", selected.Name))
        {
            foreach (var template in ProjectTemplateCatalog.All)
            {
                var isSelected = template.Id == _selectedProjectTemplate;
                if (ImGui.Selectable(template.Name, isSelected)) _selectedProjectTemplate = template.Id;
                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{selected.Focus}\n{selected.Description}");
    }

    private void BeginCreateProject(string path, ProjectTemplateId templateId, bool fromWelcome)
    {
        try
        {
            var absolutePath = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(Path.Combine(absolutePath, ProjectLayout.ProjectFileName)))
            {
                ShowProjectOperationError("无法新建项目",
                    new InvalidOperationException("所选文件夹已经是 MVoxel 项目，请使用“打开项目”。"),
                    fromWelcome, editorDialogMode: 1, path);
                return;
            }

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
                        projectSettings.LodLevels = _editorSettings.SceneLodLevels;
                        projectSettings.LodDistances = _editorSettings.SceneLodDistances.ToArray();
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
                    OpenProject,
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
                        report(new BlockingOperationProgress("正在扫描资源目录…", .76F, CanCancel: true));
                        foreach (var directory in ProjectLayout.AssetDirectories)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            _ = Directory.Exists(Path.Combine(project.AssetsPath, directory));
                        }

                        report(new BlockingOperationProgress("正在准备编辑器工作区…", .96F, CanCancel: true));
                        return project;
                    },
                    OpenProject,
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
            "正在加载项目资源",
            "正在准备加载诊断…",
            (cancellation, report) =>
            {
                for (var step = 1; step <= 8; step++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Thread.Sleep(38);
                    report(new BlockingOperationProgress($"正在加载资源索引（{step}/8）…", .10F + step * .10F, CanCancel: true));
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
        _assetBrowser.Reset();
        RecentProjects.Record(project);
        LoadProjectSettings(project);
        LoadWorldForProject(project);
        _pluginRuntime.Activate(project, TimeSpan.FromSeconds(2));
        _statusText = $"已打开项目：{project.Manifest.Name}";
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
        _renderCameraId = null;
        _assetBrowser.Reset();
        _world = new VoxelWorld();
        _voxelHistory.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
        _palette = EditorPalettes.ResolveEditorPalette();
        _paletteIsProjectLevel = false;
        ApplyEditorSettingsToUi();
        SyncSettingsToggles();
        _statusText = "已关闭项目";
        RequestWelcomeWindow();
    }

    private void SaveProject()
    {
        if (_project is null)
        {
            SetStatus("没有可保存的项目");
            return;
        }

        ProjectLayout.EnsureDirectories(_project.RootPath);
        SaveProjectSettings();
        if (SaveWorldToProject(_project))
        {
            SetStatus($"项目已保存：{_project.Manifest.Name}（{_world.OccupiedVoxelCount} 体素 → Assets/VoxelModels/主场景.vox）");
        }
    }

    private void RefreshProjectAssets()
    {
        _assetBrowser.Reset();
        SetStatus(_project is null ? "资源视图已刷新：未打开项目" : "资源视图已刷新");
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
