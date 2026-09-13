using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Project;
using MVoxel.Editor.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVoxel.Editor;

internal sealed class ImGuiEditorWindow : GameWindow, IEditorWelcomeHost
{
    private readonly ConcurrentQueue<Action> _pendingActions = new();
    private readonly string? _startupProjectPath;
    private ImGuiController? _controller;
    private ImGuiTexture? _logoTexture;
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
    private int _inspectorTab;
    private int? _requestedCenterTab;
    private int? _requestedInspectorTab;
    private bool _applyPreset;
    private LayoutPreset _layoutPreset = LayoutPreset.Default;
    private string _statusText = "就绪";
    private float _sceneZoom = 1F;
    private float _sceneOrbitYaw = MathF.PI / 4F;
    private float _sceneOrbitPitch = 0.48F;
    private float _sceneCameraDistance = 18F;
    private Vector2 _scenePan;
    private int _editorLodLevels = 4;
    private int _projectLodLevels = 4;
    private readonly float[] _editorLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private readonly float[] _projectLodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    private int _renderBackend = 1;
    private bool _rayTracing;
    private bool _showGrid = true;
    private bool _showVoxelOutlines = true;
    private bool _showLighting = true;

    public ImGuiEditorWindow(string[] args)
        : base(GameWindowSettings.Default, CreateWindowSettings(args))
    {
        _startupProjectPath = ResolveStartupProjectPath(args);
    }

    public event EventHandler? ProjectOpened;

    public event EventHandler<ProjectOperationFailedEventArgs>? ProjectOperationFailed;

    public bool HasStartupProject => !string.IsNullOrWhiteSpace(_startupProjectPath);

    public bool HasProject => _project is not null;

    public void StartNewProjectFromWelcome(string path) => _pendingActions.Enqueue(() => BeginCreateProject(path, fromWelcome: true));

    public void OpenProjectFromWelcome(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public void OpenRecentProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: true));

    public bool IsProjectOperationRunning => _projectOperation.IsRunning;

    public BlockingOperationSnapshot? ProjectOperationSnapshot => _projectOperation.Snapshot;

    public void CancelProjectOperation() => _projectOperation.RequestCancel();

    internal void RequestOpenProject(string path) => _pendingActions.Enqueue(() => BeginOpenProject(path, fromWelcome: false));

    protected override void OnLoad()
    {
        base.OnLoad();
        MakeCurrent();
        GL.ClearColor(EditorPalette.Window);
        _controller = new ImGuiController(this);
        _imguiContext = ImGui.GetCurrentContext();
        _fonts.Load(_controller, UiDiagnostics.Dpi(this));
        ConfigureImGui();
        UiDiagnostics.Configure();
        _logoTexture = ImGuiTexture.LoadLogo();
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
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        while (_pendingActions.TryDequeue(out var action))
        {
            action();
        }

        if (UiDiagnostics.TakeBlockingOperationRequest()) BeginDiagnosticLoad();
        _projectOperation.Pump();
        EnsureWelcomeWindow();
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        MakeCurrent();
        ImGui.SetCurrentContext(_imguiContext);
        _fonts.Refresh(_controller!, UiDiagnostics.Dpi(this));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _controller?.Update((float)Math.Max(args.Time, 1.0 / 120.0));
        if (_controller is not null)
        {
            if (UiDiagnostics.ShouldCloseWelcome) _welcomeWindow?.Close();
            DrawEditorFrame();
            _controller.Render();
            UiDiagnostics.Capture(this, "editor");
        }

        SwapBuffers();
        if (_welcomeWindow is not null && !_welcomeWindow.PumpFrame(Math.Max(args.Time, 1.0 / 120.0)))
        {
            _welcomeWindow.Dispose();
            _welcomeWindow = null;
        }
        MakeCurrent();
        ImGui.SetCurrentContext(_imguiContext);
        UiDiagnostics.Advance(this, ref _sceneOrbitYaw, ref _sceneOrbitPitch, ref _sceneCameraDistance, ref _scenePan);
    }

    protected override void OnUnload()
    {
        _welcomeWindow?.Dispose();
        _welcomeWindow = null;
        _projectOperation.Dispose();
        MakeCurrent();
        ImGui.SetCurrentContext(_imguiContext);
        _logoTexture?.Dispose();
        _controller?.Dispose();
        base.OnUnload();
    }

    private void RequestWelcomeWindow() => _showWelcomeRequested = true;

    private void EnsureWelcomeWindow()
    {
        if (!_showWelcomeRequested || _welcomeWindow is not null) return;
        _showWelcomeRequested = false;
        _welcomeWindow = new ImGuiWelcomeWindow(this);
        _welcomeWindow.InitializeForManualLoop();
        // Initialising the secondary OpenGL context changes the current
        // context. Restore the editor context before this frame continues.
        MakeCurrent();
        ImGui.SetCurrentContext(_imguiContext);
        // Make the separate welcome window visible above its now-blocked
        // owner. Without this explicit focus transfer, Windows can leave the
        // editor's modal dimmer in front and make its buttons look missing.
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
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            API = ContextAPI.OpenGL,
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
        if (File.Exists(layoutPath))
        {
            ImGui.LoadIniSettingsFromDisk(layoutPath);
        }

        var style = ImGui.GetStyle();
        style.WindowRounding = 4F;
        style.ChildRounding = 3F;
        style.FrameRounding = 3F;
        style.PopupRounding = 4F;
        style.ScrollbarRounding = 3F;
        style.GrabRounding = 3F;
        style.WindowBorderSize = 1F;
        style.FrameBorderSize = 1F;
        style.ItemSpacing = new Vector2(8F, 6F);
        style.WindowPadding = new Vector2(8F, 8F);
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
        style.Colors[(int)ImGuiCol.Text] = ToColor(EditorPalette.Text);
        style.Colors[(int)ImGuiCol.TextDisabled] = ToColor(EditorPalette.MutedText);
    }

    private void DrawEditorFrame()
    {
        UiDiagnostics.PrepareTabs(ref _requestedCenterTab, ref _requestedInspectorTab);
        var welcomeIsOpen = _welcomeWindow is not null;
        var operation = _projectOperation.Snapshot;
        var inputBlocked = welcomeIsOpen || operation is not null;
        if (inputBlocked) ImGui.BeginDisabled();
        DrawMenuBar();
        DrawToolbar();
        DrawDockspace();
        DrawHierarchy();
        DrawCenter();
        DrawInspector();
        DrawProjectAssets();
        DrawStatusBar();
        DrawProjectPathDialog();
        if (inputBlocked) ImGui.EndDisabled();
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
            if (ImGui.MenuItem("场景", "F1")) _requestedCenterTab = 0;
            if (ImGui.MenuItem("渲染", "F2")) _requestedCenterTab = 1;
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
            if (ImGui.MenuItem("重置当前布局")) SelectLayout(_layoutPreset);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("设置"))
        {
            if (ImGui.MenuItem("LOD 与画面设置")) _requestedInspectorTab = 1;
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

    private void DrawToolbar()
    {
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(0F, MenuHeight), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(display.X, ToolbarHeight), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("##MVoxelToolbar", flags))
        {
            ImGui.End();
            return;
        }

        if (_logoTexture is not null)
        {
            ImGui.Image(_logoTexture.ImGuiHandle, new Vector2(26F, 26F));
            ImGui.SameLine();
        }

        ImGui.TextColored(ToColor(EditorPalette.Text), "M V O X E L");
        ImGui.SameLine();
        ImGui.TextDisabled(_centerTab == 0 ? "场景" : "渲染");
        ImGui.SameLine();
        if (ImGui.Button("新建")) ShowProjectPathDialog(create: true);
        ImGui.SameLine();
        if (ImGui.Button("打开")) ShowProjectPathDialog(create: false);
        ImGui.SameLine();
        if (ImGui.Button("保存")) SaveProject();
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if (ImGui.Button("选择")) SetStatus("选择工具已激活");
        ImGui.SameLine();
        if (ImGui.Button("绘制")) SetStatus("绘制工具已激活");
        ImGui.SameLine();
        if (ImGui.Button("擦除")) SetStatus("擦除工具已激活");
        ImGui.SameLine();
        if (ImGui.Button("场景")) _requestedCenterTab = 0;
        ImGui.SameLine();
        if (ImGui.Button("渲染")) _requestedCenterTab = 1;
        ImGui.SameLine();
        if (ImGui.Button("灯光预览")) _showLighting = !_showLighting;
        ImGui.SameLine();
        if (ImGui.Button("渲染设置")) _requestedInspectorTab = 1;
        ImGui.SameLine();
        if (ImGui.Button("刷新资产")) RefreshProjectAssets();
        ImGui.End();
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
        ImGui.DockSpace(ImGui.GetID("MVoxelMainDockSpace"), Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }

    private void DrawHierarchy()
    {
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(0F, WorkspaceTop + 1F), new Vector2(layout.LeftWidth, layout.MainHeight));
        if (!_showHierarchy || !ImGui.Begin("层级", ref _showHierarchy,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            if (_showHierarchy) ImGui.End();
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
        if (!ImGui.Begin("场景与渲染", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##CenterTabs"))
        {
            if (BeginPanelTab("场景", _requestedCenterTab == 0))
            {
                _centerTab = 0;
                DrawSceneView();
                ImGui.EndTabItem();
            }

            if (BeginPanelTab("渲染", _requestedCenterTab == 1))
            {
                _centerTab = 1;
                DrawRenderView();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
            _requestedCenterTab = null;
        }

        ImGui.End();
    }

    private static unsafe bool BeginPanelTab(string label, bool requested)
    {
        // Null p_open keeps these primary tabs non-closable while allowing
        // toolbar/menu requests to select the actual ImGui tab.
        var length = System.Text.Encoding.UTF8.GetByteCount(label);
        Span<byte> utf8 = stackalloc byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(label, utf8);
        utf8[length] = 0;
        fixed (byte* name = utf8)
            return ImGuiNative.igBeginTabItem(name, null,
                requested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
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
        var gizmoHovered = HandleSceneNavigation(origin, size, gizmoCenter, gizmoRadius);
        var camera = GetSceneCamera();

        if (_showGrid)
        {
            SceneGrid.Draw(drawList, origin, size, camera, _sceneCameraDistance);
        }

        if (_project is not null && _showVoxelOutlines)
        {
            DrawVoxelPreview(drawList, origin, size, camera);
        }

        DrawSceneWorldAxes(drawList, origin, size, camera);
        DrawOrientationGizmo(drawList, gizmoCenter, gizmoRadius, camera, gizmoHovered);

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
        ImGui.TextDisabled("左键拖拽旋转  ·  中键平移  ·  滚轮缩放  ·  点击右上角 XYZ 定向");
        ImGui.EndChild();
    }

    private SceneCamera GetSceneCamera()
    {
        var target = new Vector3(_scenePan.X, 0F, _scenePan.Y);
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

    private bool HandleSceneNavigation(Vector2 origin, Vector2 size, Vector2 gizmoCenter, float gizmoRadius)
    {
        if (_welcomeWindow is not null || _projectOperation.IsRunning)
        {
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        var gizmoHovered = inViewport && Vector2.Distance(mouse, gizmoCenter) <= gizmoRadius + 8F;
        if (!inViewport || !IsFocused || !ImGui.IsWindowHovered())
        {
            return false;
        }

        if (Math.Abs(io.MouseWheel) > float.Epsilon && !gizmoHovered)
        {
            _sceneCameraDistance = Math.Clamp(_sceneCameraDistance * MathF.Pow(0.82F, io.MouseWheel), 4F, 256F);
            _sceneZoom = Math.Clamp(18F / _sceneCameraDistance, 0.07F, 4F);
        }

        if (!gizmoHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            _sceneOrbitYaw += io.MouseDelta.X * 0.008F;
            _sceneOrbitPitch = Math.Clamp(_sceneOrbitPitch - io.MouseDelta.Y * 0.008F, -1.35F, 1.35F);
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

    private static void DrawSceneWorldAxes(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var axisLength = 6F;
        DrawProjectedLine(drawList, Vector3.Zero, Vector3.UnitX * axisLength, origin, size, camera, ToPackedColor(Color.FromArgb(235, 230, 78, 84)), 2F);
        DrawProjectedLine(drawList, Vector3.Zero, Vector3.UnitY * axisLength, origin, size, camera, ToPackedColor(Color.FromArgb(235, 88, 210, 132)), 2F);
        DrawProjectedLine(drawList, Vector3.Zero, Vector3.UnitZ * axisLength, origin, size, camera, ToPackedColor(Color.FromArgb(235, 82, 154, 238)), 2F);
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

        // Keep the orientation globe intentionally spare: one latitude and
        // one longitude ring.  Earlier versions approximated these with many
        // independent lines, which looked like a distracting burst at small
        // sizes.  Four Bezier halves give continuous rings without visual
        // clutter or a seam through the centre.
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
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ToPackedColor(Color.FromArgb(255, 14, 18, 24)));
        drawList.AddCircleFilled(origin + size / 2F, Math.Min(size.X, size.Y) * 0.18F, ToPackedColor(Color.FromArgb(40, 104, 187, 255)));
        ImGui.SetCursorScreenPos(origin + new Vector2(
            Math.Max(0F, (size.X - ImGui.CalcTextSize("渲染预览").X) / 2F),
            Math.Max(0F, size.Y / 2F - ImGui.GetTextLineHeightWithSpacing())));
        ImGui.TextColored(ToColor(EditorPalette.Text), "渲染预览");
        ImGui.SetCursorScreenPos(origin + new Vector2(
            Math.Max(0F, (size.X - ImGui.CalcTextSize("渲染预览正在开发中").X) / 2F), size.Y / 2F));
        ImGui.TextDisabled("渲染预览正在开发中");
        ImGui.EndChild();
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
        if (!ImGui.Begin("检查器", ref _showInspector,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            if (_showInspector) ImGui.End();
            return;
        }

        ImGui.TextColored(ToColor(EditorPalette.Text), "检查器");
        ImGui.SameLine();
        ImGui.TextDisabled(_inspectorTab == 0 ? "对象属性" : "画面设置");
        if (ImGui.BeginTabBar("##InspectorTabs"))
        {
            if (BeginPanelTab("属性", _requestedInspectorTab == 0))
            {
                _inspectorTab = 0;
                ImGui.Text("项目");
                ImGui.SameLine(90F);
                ImGui.TextColored(ToColor(EditorPalette.Text), _project?.Manifest.Name ?? "未打开项目");
                ImGui.Text("路径");
                ImGui.SameLine(90F);
                ImGui.TextWrapped(_project?.RootPath ?? "欢迎使用 MVoxel");
                ImGui.Text("选择");
                ImGui.SameLine(90F);
                ImGui.TextDisabled("未选择对象");
                ImGui.Text("坐标");
                ImGui.SameLine(90F);
                ImGui.Text("0, 0, 0");
                ImGui.Text("尺寸");
                ImGui.SameLine(90F);
                ImGui.TextWrapped("按作品范围扩展");
                ImGui.SeparatorText("材质");
                ImGui.TextDisabled("颜色、透明度和表面效果");
                ImGui.EndTabItem();
            }

            if (BeginPanelTab("画面设置", _requestedInspectorTab == 1))
            {
                _inspectorTab = 1;
                DrawConfigurationPanel();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
            _requestedInspectorTab = null;
        }

        ImGui.End();
    }

    private void DrawConfigurationPanel()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "LOD 与画面设置");
        ImGui.TextWrapped("LOD（细节等级）控制远处物体的显示精度，让场景操作更流畅。渲染和导出始终保留完整细节。");
        ImGui.SeparatorText("新项目的默认 LOD 设置");
        ImGui.PushItemWidth(Math.Max(64F, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("第 8 级距离").X - 16F));
        ImGui.SliderInt("等级数量##editor", ref _editorLodLevels, 1, _editorLodDistances.Length);
        for (var index = 0; index < _editorLodLevels; index++)
        {
            ImGui.SliderFloat($"第 {index + 1} 级距离##editor", ref _editorLodDistances[index], 1F, 16384F, "%.0f");
        }

        ImGui.SeparatorText("当前项目的 LOD 设置");
        ImGui.SliderInt("等级数量##project", ref _projectLodLevels, 1, _projectLodDistances.Length);
        for (var index = 0; index < _projectLodLevels; index++)
        {
            ImGui.SliderFloat($"第 {index + 1} 级距离##project", ref _projectLodDistances[index], 1F, 16384F, "%.0f");
        }

        ImGui.SeparatorText("场景画面配置");
        ImGui.Checkbox("显示网格##scene", ref _showGrid);
        ImGui.Checkbox("显示体素外轮廓##scene", ref _showVoxelOutlines);
        ImGui.Checkbox("环境光预览##scene", ref _showLighting);

        ImGui.SeparatorText("渲染画面配置");
        ImGui.Text("计算设备");
        ImGui.SetNextItemWidth(-1F);
        ImGui.Combo("##renderDevice", ref _renderBackend, new[] { "CPU（处理器）", "GPU（显卡，默认）" }, 2);
        ImGui.Checkbox("开启光线追踪##render", ref _rayTracing);
        ImGui.SliderInt("采样次数##render", ref _renderSamples, 1, 1024);
        ImGui.SliderFloat("曝光##render", ref _renderExposure, -4F, 4F, "%.2f");

        ImGui.SeparatorText("渲染导出画面配置");
        ImGui.Text("计算设备");
        ImGui.SetNextItemWidth(-1F);
        ImGui.Combo("##exportDevice", ref _exportBackend, new[] { "CPU（处理器）", "GPU（显卡，默认）" }, 2);
        ImGui.Checkbox("开启光线追踪##export", ref _exportRayTracing);
        ImGui.SliderInt("采样次数##export", ref _exportSamples, 1, 4096);
        ImGui.Checkbox("透明背景##export", ref _exportAlpha);
        ImGui.PopItemWidth();
    }

    private int _renderSamples = 64;
    private int _exportSamples = 128;
    private int _exportBackend = 1;
    private float _renderExposure;
    private bool _exportRayTracing;
    private bool _exportAlpha = true;
    private int _projectPathDialogMode;
    private string _projectPathText = string.Empty;
    private string _projectPathError = string.Empty;

    private void DrawProjectAssets()
    {
        if (!_showProjectAssets)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(layout.LeftWidth, display.Y - layout.AssetsHeight - StatusHeight), new Vector2(display.X - layout.LeftWidth, layout.AssetsHeight));
        if (!ImGui.Begin("项目资产##资源浏览器", ref _showProjectAssets,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            if (_showProjectAssets) ImGui.End();
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
        ImGui.Text("MVoxel");
        ImGui.SameLine(display.X / 2F - 40F);
        ImGui.TextColored(ToColor(EditorPalette.MutedText), _statusText);
        ImGui.SameLine(display.X - ImGui.CalcTextSize("体素建模 · 开发预览版").X - 12F);
        ImGui.TextDisabled("体素建模 · 开发预览版");
        ImGui.End();
    }

    private static float MenuHeight => ImGui.GetFrameHeight();
    private static float ToolbarHeight => Math.Max(44F, ImGui.GetFrameHeight() + 16F);
    private static float WorkspaceTop => MenuHeight + ToolbarHeight;
    private static float StatusHeight => ImGui.GetTextLineHeight() + 16F;

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
        _statusText = $"已切换布局：{preset switch { LayoutPreset.Modeling => "建模", LayoutPreset.Rendering => "渲染", _ => "默认" }}";
    }

    private void ShowProjectPathDialog(bool create)
    {
        _projectPathDialogMode = create ? 1 : 2;
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

        if (_projectPathDialogMode == 1) BeginCreateProject(path, fromWelcome: false);
        else BeginOpenProject(path, fromWelcome: false);
    }

    private void BeginCreateProject(string path, bool fromWelcome)
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
                        // From this point ProjectLayout uses atomic file writes.
                        // It is safer to finish the tiny commit than to leave a
                        // half-created project because a user clicked cancel.
                        report(new BlockingOperationProgress("正在写入项目文件…", .68F, CanCancel: false));
                        var project = ProjectLayout.Create(absolutePath, name);
                        report(new BlockingOperationProgress("正在完成项目初始化…", .96F, CanCancel: false));
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
        _project = null;
        _assetBrowser.Reset();
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
        SetStatus($"项目已保存：{_project.Manifest.Name}");
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
