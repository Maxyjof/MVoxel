using ImGuiNET;
using MVoxel.Project;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

internal interface IEditorWelcomeHost
{
    event EventHandler? ProjectOpened;
    event EventHandler<ProjectOperationFailedEventArgs>? ProjectOperationFailed;
    bool HasStartupProject { get; }
    bool IsProjectOperationRunning { get; }
    BlockingOperationSnapshot? ProjectOperationSnapshot { get; }
    void StartNewProjectFromWelcome(string path);
    void OpenProjectFromWelcome(string path);
    void OpenRecentProject(string path);
    void CancelProjectOperation();
}

internal sealed class ProjectOperationFailedEventArgs : EventArgs
{
    internal ProjectOperationFailedEventArgs(string message)
    {
        Message = message;
    }

    internal string Message { get; }
}

/// <summary>
/// Separate startup window rendered entirely with ImGui. It deliberately has
/// its own OpenTK window/context so Welcome is not a Scene tab in the editor.
/// </summary>
internal sealed class ImGuiWelcomeWindow : GameWindow
{
    private readonly IEditorWelcomeHost _editor;
    private VulkanImGuiController? _controller;
    private VulkanWindowContext? _vulkan;
    private VulkanImGuiRenderer? _imguiRenderer;
    private readonly EditorFontCatalog _fonts = new();
    private IntPtr _imguiContext;
    private string _pathText = string.Empty;
    private string _errorText = string.Empty;
    private int _pathMode;
    private int _closeRequested;

    internal ImGuiWelcomeWindow(IEditorWelcomeHost editor)
        : base(GameWindowSettings.Default, CreateWindowSettings())
    {
        _editor = editor;
        _editor.ProjectOpened += OnProjectOpened;
        _editor.ProjectOperationFailed += OnProjectOperationFailed;
    }

    internal void RequestClose() => Interlocked.Exchange(ref _closeRequested, 1);

    internal void InitializeForManualLoop()
    {
        OnLoad();
    }

    /// <summary>
    /// Manually advances the secondary GLFW window. Unlike GameWindow.Run(),
    /// this owner loop must dispose a window after its close request; merely
    /// observing IsExiting leaves the native title-bar close button visible.
    /// </summary>
    internal bool PumpFrame(double deltaSeconds)
    {
        if (IsExiting || Volatile.Read(ref _closeRequested) != 0)
        {
            return false;
        }

        ProcessEvents(0D);
        if (IsExiting || Volatile.Read(ref _closeRequested) != 0)
        {
            return false;
        }

        ImGui.SetCurrentContext(_imguiContext);
        OnUpdateFrame(new FrameEventArgs(deltaSeconds));
        if (!IsExiting && Volatile.Read(ref _closeRequested) == 0)
        {
            OnRenderFrame(new FrameEventArgs(deltaSeconds));
        }

        return !IsExiting && Volatile.Read(ref _closeRequested) == 0;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
        _imguiContext = ImGui.GetCurrentContext();
        _fonts.Load(_controller, UiDiagnostics.Dpi(this));
        _imguiRenderer = new VulkanImGuiRenderer(_vulkan, _controller);
        ConfigureImGui();
        UiDiagnostics.Configure();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        if (Volatile.Read(ref _closeRequested) != 0)
        {
            Close();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs args)
    {
        // The callback is delivered during ProcessEvents(). Let the editor's
        // owner loop dispose this independent window on the same main thread.
        RequestClose();
        base.OnClosing(args);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        _fonts.Refresh(_controller!, UiDiagnostics.Dpi(this));
        _controller?.Update((float)Math.Max(args.Time, 1.0 / 120.0));
        if (_controller is not null)
        {
            DrawWelcome();
            _controller.Render();
            UiDiagnostics.Capture(this, "welcome");
        }

        _vulkan?.RenderFrame(new System.Numerics.Vector4(0.05F, 0.07F, 0.10F, 1F), commandBuffer =>
        {
            if (_imguiRenderer is not null && _controller is not null)
            {
                _imguiRenderer.Record(commandBuffer, _controller);
            }
        });
    }

    protected override void OnUnload()
    {
        ReleaseResources();
        base.OnUnload();
    }

    private void ReleaseResources()
    {
        if (_controller is null) return;
        ImGui.SetCurrentContext(_imguiContext);
        _editor.ProjectOpened -= OnProjectOpened;
        _editor.ProjectOperationFailed -= OnProjectOperationFailed;
        _controller.Dispose();
        _controller = null;
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _vulkan?.Dispose();
        _vulkan = null;
    }

    public override void Dispose()
    {
        // 这个独立窗口使用手动循环，因此必须在 GLFW 销毁前释放自己的 Vulkan 资源
        ReleaseResources();
        base.Dispose();
    }

    private static NativeWindowSettings CreateWindowSettings()
    {
        return new NativeWindowSettings
        {
            Title = "欢迎使用 MVoxel",
            ClientSize = new Vector2i(780, 620),
            MinimumClientSize = new Vector2i(680, 520),
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
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.ConfigWindowsMoveFromTitleBarOnly = true;

        var style = ImGui.GetStyle();
        style.WindowRounding = 8F;
        style.FrameRounding = 4F;
        style.WindowBorderSize = 1F;
        style.FrameBorderSize = 1F;
        style.ItemSpacing = new Vector2(10F, 8F);
        style.WindowPadding = new Vector2(34F, 28F);
        style.Colors[(int)ImGuiCol.WindowBg] = ToColor(EditorPalette.Window);
        style.Colors[(int)ImGuiCol.ChildBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.Button] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.ButtonHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.ButtonActive] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Border] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.Text] = ToColor(EditorPalette.Text);
        style.Colors[(int)ImGuiCol.TextDisabled] = ToColor(EditorPalette.MutedText);
    }

    private void DrawWelcome()
    {
        var display = ImGui.GetIO().DisplaySize;
        var operation = _editor.ProjectOperationSnapshot;
        if (operation is not null) ImGui.BeginDisabled();
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;
        ImGui.Begin("##MVoxelWelcome", flags);

        var contentWidth = Math.Min(660F, display.X - 68F);
        var contentLeft = MathF.Floor(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 13F);
        ImGui.PushFont(_fonts.Title);
        ImGui.TextColored(ToColor(EditorPalette.Text), "MVoxel");
        ImGui.PopFont();
        ImGui.TextColored(ToColor(EditorPalette.Accent), "用小方块，创造大世界");
        ImGui.TextDisabled("欢迎使用 MVoxel 体素建模软件");
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0F, 28F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        if (ImGui.Button("新建项目", new Vector2(210F, 48F))) BeginPathDialog(1);
        ImGui.SameLine();
        if (ImGui.Button("打开项目", new Vector2(210F, 48F))) BeginPathDialog(2);

        ImGui.Dummy(new Vector2(0F, 24F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.TextColored(ToColor(EditorPalette.Text), "最近打开的项目");
        ImGui.Separator();

        var recent = RecentProjects.Load();
        ImGui.SetCursorPosX(contentLeft);
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() * 2F + 16F;
        var pathHeight = _pathMode == 0 ? 0F : ImGui.GetTextLineHeightWithSpacing() * 3F + 96F;
        var recentHeight = Math.Max(60F, ImGui.GetContentRegionAvail().Y - footerHeight - pathHeight);
        ImGui.BeginChild("##RecentProjects", new Vector2(contentWidth, recentHeight), ImGuiChildFlags.Borders);
        if (recent.Count == 0)
        {
            ImGui.TextWrapped("还没有最近项目。点击上方“新建项目”，开始你的第一个作品。");
        }
        else
        {
            foreach (var entry in recent)
            {
                // ImGui Selectable only supports single-line labels; render the
                // name+time as the clickable row and the path as a caption.
                ImGui.PushID(entry.Path);
                var label = $"{entry.Name}  ·  {entry.LastOpenedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
                if (ImGui.Button(label, new Vector2(-1F, 0F)))
                {
                    _errorText = string.Empty;
                    _editor.OpenRecentProject(entry.Path);
                }

                var indent = ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X + 4F;
                ImGui.SetCursorPosX(indent);
                ImGui.TextDisabled(entry.Path);
                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        ImGui.SetCursorPosX(contentLeft);
        ImGui.PushTextWrapPos(contentLeft + contentWidth);
        if (!string.IsNullOrWhiteSpace(_errorText))
        {
            ImGui.TextColored(ToColor(EditorPalette.Error), _errorText);
        }
        else
        {
            ImGui.TextDisabled("仅显示最近打开的 5 个有效项目。");
        }
        ImGui.PopTextWrapPos();

        DrawPathDialog();
        ImGui.End();
        if (operation is not null)
        {
            ImGui.EndDisabled();
            BlockingOperationDialog.Draw(operation.Value, _editor.CancelProjectOperation);
        }
    }

    private void BeginPathDialog(int mode)
    {
        _pathMode = mode;
        _pathText = string.Empty;
        _errorText = string.Empty;
    }

    private void DrawPathDialog()
    {
        if (_pathMode == 0)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(ToColor(EditorPalette.Accent), _pathMode == 1 ? "新建 MVoxel 项目" : "打开 MVoxel 项目");
        ImGui.TextWrapped(_pathMode == 1 ? "输入项目文件夹路径，项目文件会由编辑器创建。" : "输入你之前保存项目的文件夹路径。");
        ImGui.SetNextItemWidth(-1F);
        ImGui.InputText("##WelcomeProjectPath", ref _pathText, 2048);
        if (ImGui.Button("确认", new Vector2(100F, 34F)))
        {
            SubmitPath();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(100F, 34F)))
        {
            _pathMode = 0;
            _errorText = string.Empty;
        }
    }

    private void SubmitPath()
    {
        var path = _pathText.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            _errorText = "请先输入项目文件夹路径。";
            return;
        }

        _errorText = string.Empty;
        if (_pathMode == 1)
        {
            _editor.StartNewProjectFromWelcome(path);
        }
        else
        {
            _editor.OpenProjectFromWelcome(path);
        }

        _pathMode = 0;
    }

    private void OnProjectOpened(object? sender, EventArgs args) => RequestClose();

    private void OnProjectOperationFailed(object? sender, ProjectOperationFailedEventArgs args) => _errorText = args.Message;

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
}
