using System.Collections.Concurrent;
using System.Numerics;
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

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow : GameWindow, IEditorWelcomeHost
{
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
    private int _editorLodLevels = 4;
    private int _projectLodLevels = 4;
    private readonly float[] _editorLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private readonly float[] _projectLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private bool _showGrid = true;
    private bool _showVoxelOutlines = true;
    private bool _showLighting = true;
    private bool _sceneMouseCaptured;

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
    private VoxelDda.Hit? _selectedVoxel;
    private VoxelDda.Hit? _brushPreview;
    private VoxelCoord64? _lastPaintedCell;
    private float _elapsedSeconds;
    private string _accumulationSignature = string.Empty;
    private string _pipelineStatus = string.Empty;

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
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
        _imguiContext = ImGui.GetCurrentContext();
        _fonts.Load(_controller, UiDiagnostics.Dpi(this));
        _imguiRenderer = new VulkanImGuiRenderer(_vulkan, _controller);
        LoadEditorSettings();
        EditorPalette.ApplyTheme(_editorSettings.Theme);
        ConfigureImGui();
        UiDiagnostics.Configure();
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

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        if (!IsFocused || !KeyboardState.IsKeyDown(Keys.CapsLock))
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
                _renderSettings.SaveTo(RenderSettingsPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"渲染配置自动保存失败：{exception.Message}");
            }
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        ImGui.SetCurrentContext(_imguiContext);
        _fonts.Refresh(_controller!, UiDiagnostics.Dpi(this));
        _elapsedSeconds += (float)Math.Max(args.Time, 0.0);
        _controller?.Update((float)Math.Max(args.Time, 1.0 / 120.0));
        if (_controller is not null)
        {
            if (UiDiagnostics.ShouldCloseWelcome) _welcomeWindow?.Close();
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
        ImGui.SetCurrentContext(_imguiContext);
        ShutdownVoxelPipeline();
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _controller?.Dispose();
        _vulkan?.Dispose();
        _vulkan = null;
        base.OnUnload();
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
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVoxel");
        Directory.CreateDirectory(configDirectory);
        var layoutPath = Path.Combine(configDirectory, "imgui-layout.ini");
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
        DrawStatusBar();
        DrawProjectPathDialog();
        if (inputBlocked) ImGui.EndDisabled();
        if (!inputBlocked) DrawSettingsWindow();
        if (welcomeIsOpen)
        {
            BlockingOperationDialog.DrawInteractionBlocker("欢迎窗口已打开", "请先在“欢迎使用 MVoxel”窗口中创建、打开项目或关闭欢迎窗口。");
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
            if (ImGui.TreeNodeEx(_project.Manifest.Name, ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.TreeNodeEx("主场景", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                if (ImGui.TreeNodeEx("体素世界", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.TreeNodeEx("体素区块", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                    ImGui.TreeNodeEx("材质", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                    ImGui.TreePop();
                }

                if (ImGui.TreeNodeEx("环境", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.TreeNodeEx("柔和环境光", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                    ImGui.TreeNodeEx("太阳光", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                    ImGui.TreePop();
                }

                ImGui.TreePop();
            }
        }

        ImGui.End();
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
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(EditorPalette.Window));

        var gizmoCenter = origin + new Vector2(size.X - 78F, 78F);
        var gizmoRadius = Math.Clamp(Math.Min(size.X, size.Y) * 0.11F, 42F, 58F);
        var gizmoHovered = HandleSceneNavigation(origin, size, gizmoCenter, gizmoRadius, allowMouseCapture: true);
        var camera = GetSceneCamera();

        var gpuRendered = _project is not null && TryRenderGpuSceneView(drawList, origin, size, camera);
        if (_project is not null && !gpuRendered && _showVoxelOutlines)
        {
            // Vulkan 设备不可用时保留 CPU 预览
            DrawVoxelPreview(drawList, origin, size, camera);
        }

        if (_showGrid && !gpuRendered)
        {
            // CPU 网格，GPU 通道自行绘制带深度关系的网格
            SceneGrid.Draw(drawList, origin, size, camera, _sceneCameraDistance);
        }

        if (_project is not null)
        {
            HandleVoxelToolInput(origin, size, camera);
            DrawVoxelBrushPreview(drawList, origin, size, camera);
        }

        DrawSceneWorldAxes(drawList, origin, size, camera);
        DrawOrientationGizmo(drawList, gizmoCenter, gizmoRadius, camera, gizmoHovered);
        if (_sceneMouseCaptured)
        {
            DrawSceneCrosshair(drawList, origin + size * 0.5F);
        }

        ImGui.SetCursorPos(new Vector2(14F, 12F));
        if (_project is null)
        {
            ImGui.TextColored(ToColor(EditorPalette.Text), "三维场景");
            ImGui.TextDisabled("创建或打开项目后，体素模型会显示在这里");
        }
        else
        {
            ImGui.TextColored(ToColor(EditorPalette.Text), $"{_project.Manifest.Name}  ·  三维场景");
            ImGui.TextDisabled("在此观察和编辑体素模型");
        }

        var hintLineHeight = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPos(new Vector2(12F, Math.Max(12F, size.Y - hintLineHeight * 2F - 8F)));
        ImGui.TextDisabled($"视角 {_sceneOrbitYaw * 180F / MathF.PI:0}° / {_sceneOrbitPitch * 180F / MathF.PI:0}°  ·  观察距离 {_sceneCameraDistance:0.0}");
        ImGui.SetCursorPos(new Vector2(12F, Math.Max(12F, size.Y - hintLineHeight - 8F)));
        ImGui.TextDisabled(KeyboardState.IsKeyDown(Keys.CapsLock)
            ? "Caps Lock：WASD 移动 · Q/E 升降 · 鼠标转向 · 左键放置 · 右键破坏"
            : "左键放置 · 右键破坏 · 中键平移 · Shift+左键环绕 · 滚轮缩放 · 点击右上角 XYZ 定向");
        ImGui.EndChild();
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
        return new SceneCamera(position, target, forward, right, up);
    }

    private bool HandleSceneNavigation(Vector2 origin, Vector2 size, Vector2 gizmoCenter, float gizmoRadius,
        bool allowMouseCapture)
    {
        if (_welcomeWindow is not null || _projectOperation.IsRunning)
        {
            EndSceneMouseCapture();
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        var gizmoHovered = inViewport && Vector2.Distance(mouse, gizmoCenter) <= gizmoRadius + 8F;
        if (!inViewport || !IsFocused || !ImGui.IsWindowHovered())
        {
            EndSceneMouseCapture();
            return false;
        }

        var capsLockMode = KeyboardState.IsKeyDown(Keys.CapsLock);
        if (allowMouseCapture && capsLockMode)
        {
            BeginSceneMouseCapture();
            gizmoHovered = false;
        }
        else
        {
            EndSceneMouseCapture();
        }

        if (_sceneMouseCaptured)
        {
            _sceneOrbitYaw += ViewportInputPolicy.OrbitYawDelta(io.MouseDelta.X);
            _sceneOrbitPitch = Math.Clamp(_sceneOrbitPitch - io.MouseDelta.Y * 0.008F, -1.35F, 1.35F);
            UpdateFreeMove(GetSceneCamera(), io.DeltaTime);
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

        var orbitLeft = !gizmoHovered && io.KeyShift && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        if (orbitLeft)
        {
            _sceneOrbitYaw += ViewportInputPolicy.OrbitYawDelta(io.MouseDelta.X);
            _sceneOrbitPitch = Math.Clamp(_sceneOrbitPitch + ViewportInputPolicy.OrbitPitchDelta(io.MouseDelta.Y),
                -1.35F, 1.35F);
        }

        if (!gizmoHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var camera = GetSceneCamera();
            var panScale = _sceneCameraDistance / Math.Max(160F, Math.Min(size.X, size.Y));
            var pan = camera.Right * (-io.MouseDelta.X * panScale) + Vector3.UnitZ * (io.MouseDelta.Y * panScale);
            _scenePan += new Vector2(pan.X, pan.Z);
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

    private void BeginSceneMouseCapture()
    {
        if (_sceneMouseCaptured)
        {
            return;
        }

        CursorState = OpenTkCursorState.Grabbed;
        _sceneMouseCaptured = true;
    }

    private void EndSceneMouseCapture()
    {
        if (!_sceneMouseCaptured)
        {
            return;
        }

        CursorState = OpenTkCursorState.Normal;
        _sceneMouseCaptured = false;
    }

    private void UpdateFreeMove(SceneCamera camera, float deltaTime)
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
        if (KeyboardState.IsKeyDown(Keys.W)) movement += forward;
        if (KeyboardState.IsKeyDown(Keys.S)) movement -= forward;
        if (KeyboardState.IsKeyDown(Keys.D)) movement += right;
        if (KeyboardState.IsKeyDown(Keys.A)) movement -= right;
        if (movement.LengthSquared > 0.0001F)
        {
            movement = Vector3.Normalize(movement);
            var speed = MathF.Max(4F, _sceneCameraDistance * 0.75F);
            _scenePan += new Vector2(movement.X, movement.Z) * speed * MathF.Max(deltaTime, 0.001F);
        }

        if (KeyboardState.IsKeyDown(Keys.E))
        {
            _sceneFreeMoveHeight += MathF.Max(4F, _sceneCameraDistance * 0.75F) * MathF.Max(deltaTime, 0.001F);
        }

        if (KeyboardState.IsKeyDown(Keys.Q))
        {
            _sceneFreeMoveHeight -= MathF.Max(4F, _sceneCameraDistance * 0.75F) * MathF.Max(deltaTime, 0.001F);
        }
    }

    private static void DrawSceneCrosshair(ImDrawListPtr drawList, Vector2 center)
    {
        var dark = ToPackedColor(Color.FromArgb(235, 10, 12, 16));
        var light = ToPackedColor(Color.FromArgb(255, 242, 246, 250));
        drawList.AddLine(center - new Vector2(10F, 0F), center + new Vector2(10F, 0F), dark, 3F);
        drawList.AddLine(center - new Vector2(0F, 10F), center + new Vector2(0F, 10F), dark, 3F);
        drawList.AddLine(center - new Vector2(8F, 0F), center + new Vector2(8F, 0F), light, 1F);
        drawList.AddLine(center - new Vector2(0F, 8F), center + new Vector2(0F, 8F), light, 1F);
    }

    private static void DrawSceneWorldAxes(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
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
    private static void DrawBidirectionalAxis(ImDrawListPtr drawList, Vector3 axis, float length,
        Vector2 origin, Vector2 size, SceneCamera camera, Color color)
    {
        var positive = ToPackedColor(color);
        var negative = ToPackedColor(Color.FromArgb(color.A / 2, color.R, color.G, color.B));
        DrawProjectedLine(drawList, Vector3.Zero, axis * length, origin, size, camera, positive, 2F);
        DrawProjectedLine(drawList, Vector3.Zero, -axis * length, origin, size, camera, negative, 2F);
    }

    private static void DrawProjectedLine(ImDrawListPtr drawList, Vector3 start, Vector3 end, Vector2 origin, Vector2 size,
        SceneCamera camera, uint color, float thickness)
    {
        if (SceneProjection.ClipLine(start, end, origin, size, camera, out var startPoint, out var endPoint))
        {
            drawList.AddLine(startPoint, endPoint, color, thickness);
        }
    }

    private static void DrawVoxelPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
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
            DrawVoxelCube(drawList, voxel.Center, 1F, voxel.Color, origin, size, camera);
        }
    }

    private static void DrawVoxelCube(ImDrawListPtr drawList, Vector3 center, float side, Color baseColor,
        Vector2 origin, Vector2 size, SceneCamera camera)
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
        for (var index = 0; index < corners.Length; index++)
        {
            if (!TryProject(corners[index], origin, size, camera, out points[index], out depths[index]))
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
        var outline = ToPackedColor(Color.FromArgb(220, 155, 173, 194));
        var fill = ToPackedColor(Color.FromArgb(205, 28, 36, 48));
        drawList.AddCircleFilled(center, radius, fill, 32);
        drawList.AddCircle(center, radius, outline, 32, 1.4F);

        // 方向球只保留一条纬线和一条经线
        // 使用四段贝塞尔曲线绘制连续环线，避免小尺寸下出现线束噪声
        var ring = ToPackedColor(Color.FromArgb(116, 122, 142, 162));
        DrawGizmoEllipse(drawList, center, radius * .84F, radius * .23F, ring);
        DrawGizmoEllipse(drawList, center, radius * .23F, radius * .84F, ring);

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
            drawList.AddCircleFilled(item.point, 7F, color, 16);
            drawList.AddText(item.point + new Vector2(9F, -8F), color, item.axis.Label);
        }

        var mouse = io.MousePos;
        if (!hovered)
        {
            return Vector2.Distance(mouse, center) <= radius + 8F;
        }

        drawList.AddCircle(center, radius + 3F, ToPackedColor(EditorPalette.Accent), 32, 1.2F);
        return true;
    }

    private static void DrawGizmoEllipse(ImDrawListPtr drawList, Vector2 center, float horizontalRadius,
        float verticalRadius, uint color)
    {
        const float control = .55228475F;
        var left = center - new Vector2(horizontalRadius, 0F);
        var right = center + new Vector2(horizontalRadius, 0F);
        drawList.AddBezierCubic(
            left,
            left - new Vector2(0F, verticalRadius * control),
            right - new Vector2(0F, verticalRadius * control),
            right,
            color,
            1F,
            12);
        drawList.AddBezierCubic(
            right,
            right + new Vector2(0F, verticalRadius * control),
            left + new Vector2(0F, verticalRadius * control),
            left,
            color,
            1F,
            12);
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
        if (!ImGui.BeginChild("##RenderViewport", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        HandleSceneNavigation(origin, size, new Vector2(-100000F, -100000F), 0F, allowMouseCapture: false);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(Color.FromArgb(255, 14, 18, 24)));
        var camera = GetSceneCamera();
        var gpuRendered = _project is not null && TryRenderGpuPathView(drawList, origin, size, camera);
        if (!gpuRendered)
        {
            DrawVoxelPreview(drawList, origin, size, camera);
            ImGui.SetCursorScreenPos(origin + new Vector2(
                Math.Max(0F, (size.X - ImGui.CalcTextSize("GPU 路径追踪不可用").X) / 2F), size.Y / 2F));
            ImGui.TextDisabled(_pipelineStatus.Length > 0 ? "GPU 路径追踪不可用 · 当前显示 CPU 预览" : "GPU 路径追踪初始化中");
        }
        else
        {
            DrawRenderViewOverlay(origin, size, drawList);
        }

        ImGui.EndChild();
    }

    private void DrawRenderViewOverlay(Vector2 origin, Vector2 size, ImDrawListPtr drawList)
    {
        _ = drawList;
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F));
        ImGui.TextColored(ToColor(EditorPalette.Accent), "Vulkan Compute 直接预览");
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F + ImGui.GetTextLineHeightWithSpacing()));
        if (ImGui.SmallButton("重新计算"))
        {
            _pipeline?.ResetAccumulation();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.SmallButton("导出 PNG（开发中）");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"{(int)_renderSettings.CameraFovDegrees}° · 尺寸 {(int)size.X}×{(int)size.Y}");
        ImGui.SetCursorScreenPos(origin + new Vector2(12F, 10F + ImGui.GetTextLineHeightWithSpacing() * 2F));
        ImGui.TextDisabled("渲染页不显示网格与辅助元素；路径追踪和导出功能正在接入");
    }

    private void ExportRenderPng()
    {
        if (_pipeline is not { IsAvailable: true } pipeline)
        {
            SetStatus("GPU 渲染不可用，无法导出");
            return;
        }

        var projectPath = _project?.RootPath
            ?? Path.Combine(Path.GetTempPath(), "MVoxel");
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
            ImGui.TextDisabled("对象属性");
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
        else
        {
            ImGui.TextDisabled("未选择对象 · 使用“选择”工具点击体素");
        }

        ImGui.SeparatorText("体素世界");
        ImGui.Text($"占用体素  {_world.OccupiedVoxelCount:N0}");
        ImGui.Text($"驻留区块  {_world.LoadedChunkCount:N0} × 32³");
        ImGui.Text($"撤销历史  {_voxelHistory.UndoCount} 步");
        ImGui.TextDisabled("坐标  0, 0, 0");
        ImGui.SeparatorText("材质");
        ImGui.TextDisabled("颜色、透明度和表面效果");
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
            ImGui.TextDisabled("LOD 与画面设置");
            DrawConfigurationPanel();
        }
        ImGui.End();
        _requestedInspectorTab = null;
    }

    private void DrawConfigurationPanel()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "LOD 与画面设置");
        ImGui.TextWrapped("LOD（细节等级）控制远处物体的显示精度，让场景操作更流畅。渲染和导出始终保留完整细节。");
        if (ImGui.Button("打开完整设置")) OpenSettings(_project is null ? 0 : 1, _project is null ? 0 : 1);
        ImGui.SameLine();
        ImGui.TextDisabled("编辑器设置 / 项目设置 / 快捷键方案");
        if (ImGui.CollapsingHeader("LOD 等级（Phase 5 预留，当前版本未参与剔除）"))
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
        ImGui.Checkbox("光照预览（关闭仅看底色）##scene", ref _showLighting);
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
        _projectPathText = string.Empty;
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
            ImGui.TextWrapped(_projectPathDialogMode == 1
                ? "输入项目文件夹路径，编辑器会为你创建项目和资产文件夹。"
                : "输入你之前保存项目的文件夹路径。");
            if (_projectPathDialogMode == 1)
            {
                DrawProjectTemplatePicker();
            }

            ImGui.SetNextItemWidth(540F);
            ImGui.InputText("项目路径", ref _projectPathText, 2048);
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

        ImGui.TextDisabled(selected.Focus);
        ImGui.TextWrapped(selected.Description);
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
        _project = project;
        _assetBrowser.Reset();
        RecentProjects.Record(project);
        LoadProjectSettings(project);
        LoadWorldForProject(project);
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
        SaveProjectSettings();
        _project = null;
        _assetBrowser.Reset();
        _world = new VoxelWorld();
        _voxelHistory.Clear();
        _selectedVoxel = null;
        _palette = EditorPalettes.ResolveEditorPalette();
        _paletteIsProjectLevel = false;
        _renderSettings = RenderSettings.LoadFrom(RenderSettingsPath);
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
