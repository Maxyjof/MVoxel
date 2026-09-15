using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using MVoxel.Editor.Templates;
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
    string DefaultProjectRootPath { get; }
    void StartNewProjectFromWelcome(string path, ProjectTemplateId templateId);
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
/// 使用 ImGui 独立绘制的启动窗口
/// Welcome 使用独立窗口，因此不会成为编辑器中的场景标签页
/// </summary>
internal sealed class ImGuiWelcomeWindow : GameWindow
{
    private const float TitleBarHeight = 34F;
    private readonly IEditorWelcomeHost _editor;
    private VulkanImGuiController? _controller;
    private VulkanWindowContext? _vulkan;
    private VulkanImGuiRenderer? _imguiRenderer;
    private readonly EditorFontCatalog _fonts = new();
    private IntPtr _imguiContext;
    private nint _logoTextureId;
    private string _pathText = string.Empty;
    private string _browserPath = string.Empty;
    private string? _browserSelectedPath;
    private readonly Stack<string> _browserBack = new();
    private string _browserError = string.Empty;
    private string _errorText = string.Empty;
    private int _pathMode;
    private ProjectTemplateId _selectedTemplate = ProjectTemplateId.Empty;
    private int _closeRequested;
    private int _closeAllowed;

    internal ImGuiWelcomeWindow(IEditorWelcomeHost editor)
        : base(GameWindowSettings.Default, CreateWindowSettings())
    {
        _editor = editor;
        _editor.ProjectOpened += OnProjectOpened;
        _editor.ProjectOperationFailed += OnProjectOperationFailed;
    }

    internal void RequestClose()
    {
        Interlocked.Exchange(ref _closeAllowed, 1);
        Interlocked.Exchange(ref _closeRequested, 1);
    }

    internal void InitializeForManualLoop()
    {
        OnLoad();
    }

    /// <summary>
    /// 手动推进独立 GLFW 窗口
    /// 所有者循环负责处理关闭请求并释放窗口资源
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
        CenterOnCurrentMonitor();
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
        _imguiContext = ImGui.GetCurrentContext();
        _fonts.Load(_controller, UiDiagnostics.Dpi(this));
        _imguiRenderer = new VulkanImGuiRenderer(_vulkan, _controller);
        if (AppBranding.TryLoadLogoPixels(out var logoPixels, out var logoWidth, out var logoHeight))
        {
            _logoTextureId = _imguiRenderer.RegisterTexture(logoPixels, logoWidth, logoHeight);
        }

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

    /// <summary>
    /// 标记独立 Welcome 窗口的 Vulkan 交换链需要按新尺寸重建
    /// </summary>
    protected override void OnResize(ResizeEventArgs args)
    {
        base.OnResize(args);
        _vulkan?.RequestResize();
    }

    /// <summary>
    /// 将欢迎窗口放到当前显示器工作区中央
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs args)
    {
        if (Volatile.Read(ref _closeAllowed) == 0)
        {
            args.Cancel = true;
            return;
        }

        // 只有项目创建或打开成功后的内部请求才允许结束 Welcome
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
        _logoTextureId = IntPtr.Zero;
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
            WindowBorder = WindowBorder.Hidden,
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
        style.WindowPadding = Vector2.Zero;
        style.ItemSpacing = new Vector2(10F, 8F);
        style.Colors[(int)ImGuiCol.WindowBg] = ToColor(EditorPalette.Window);
        style.Colors[(int)ImGuiCol.PopupBg] = ToColor(EditorPalette.Window);
        style.Colors[(int)ImGuiCol.ChildBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBg] = ToColor(EditorPalette.Input);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = ToColor(EditorPalette.PanelHeader);
        style.Colors[(int)ImGuiCol.Button] = ToColor(EditorPalette.AccentSoft);
        style.Colors[(int)ImGuiCol.ButtonHovered] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.ButtonActive] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.Border] = ToColor(EditorPalette.Border);
        style.Colors[(int)ImGuiCol.CheckMark] = ToColor(EditorPalette.Accent);
        style.Colors[(int)ImGuiCol.TextSelectedBg] = ToColor(EditorPalette.AccentSoft);
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

        var titleBarPosition = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##WelcomeTitleBar", new Vector2(display.X, TitleBarHeight));
        var titleBarMax = titleBarPosition + new Vector2(display.X, TitleBarHeight);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(titleBarPosition, titleBarMax, ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.PanelHeader)));
        drawList.AddLine(titleBarMax - new Vector2(0F, 1F), titleBarMax - new Vector2(display.X, 1F),
            ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.Border)), 1F);
        drawList.AddText(titleBarPosition + new Vector2(14F, (TitleBarHeight - ImGui.GetTextLineHeight()) * 0.5F),
            ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.Text)), "欢迎使用 MVoxel");
        if (ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta.LengthSquared() > 0F)
            {
                Location = new Vector2i(
                    Location.X + (int)MathF.Round(delta.X),
                    Location.Y + (int)MathF.Round(delta.Y));
            }
        }

        ImGui.SetCursorPos(new Vector2(0F, TitleBarHeight));

        var crashNoticeHeight = DrawCrashNotice(display);
        ImGui.SetCursorPos(new Vector2(0F, TitleBarHeight + crashNoticeHeight));

        var contentWidth = Math.Min(660F, display.X - 68F);
        var contentLeft = MathF.Floor(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 13F);
        if (_logoTextureId != IntPtr.Zero)
        {
            ImGui.Image(_logoTextureId, new Vector2(96F, 96F));
            ImGui.SameLine(0F, 18F);
        }

        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 14F);
        ImGui.PushFont(_fonts.Title);
        ImGui.TextColored(ToColor(EditorPalette.Text), "MVoxel");
        ImGui.PopFont();
        ImGui.TextColored(ToColor(EditorPalette.Accent), "用小方块，创造大世界");
        ImGui.TextDisabled("欢迎使用 MVoxel 体素建模软件");
        ImGui.EndGroup();
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
        var pathHeight = _pathMode == 0
            ? 0F
            : ImGui.GetTextLineHeightWithSpacing() * (_pathMode == 1 ? 11F : 3F) + 112F;
        var recentHeight = Math.Max(60F, ImGui.GetContentRegionAvail().Y - footerHeight - pathHeight);
        ImGui.BeginChild("##RecentProjects", new Vector2(contentWidth, recentHeight), ImGuiChildFlags.Borders);
        if (recent.Count == 0)
        {
            ImGui.Dummy(new Vector2(0F, 18F));
            ImGui.SetCursorPosX(18F);
            ImGui.TextWrapped("还没有最近项目。点击上方“新建项目”，开始你的第一个作品。");
        }
        else
        {
            foreach (var entry in recent)
            {
                ImGui.PushID(entry.Path);
                var rowWidth = ImGui.GetContentRegionAvail().X;
                const float rowHeight = 70F;
                var rowMin = ImGui.GetCursorScreenPos();
                var clicked = ImGui.InvisibleButton("##RecentProjectCard", new Vector2(rowWidth, rowHeight));
                var rowMax = rowMin + new Vector2(rowWidth, rowHeight);
                var draw = ImGui.GetWindowDrawList();
                var background = ImGui.IsItemHovered() ? EditorPalette.PanelHeader : EditorPalette.Input;
                var border = ImGui.IsItemHovered() ? EditorPalette.Accent : EditorPalette.Border;
                var textColor = ToPackedColor(EditorPalette.Text);
                var mutedColor = ToPackedColor(EditorPalette.MutedText);
                draw.AddRectFilled(rowMin, rowMax, ToPackedColor(background), 6F);
                draw.AddRect(rowMin, rowMax, ToPackedColor(border), 6F, ImDrawFlags.None, 1F);
                draw.AddRectFilled(rowMin, rowMin + new Vector2(4F, rowHeight), ToPackedColor(EditorPalette.Accent), 6F,
                    ImDrawFlags.RoundCornersLeft);
                draw.AddText(rowMin + new Vector2(16F, 10F), textColor, entry.Name);
                var date = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var dateWidth = ImGui.CalcTextSize(date).X;
                draw.AddText(rowMin + new Vector2(rowWidth - dateWidth - 16F, 10F), mutedColor, date);
                draw.AddText(rowMin + new Vector2(16F, 39F), mutedColor, entry.Path);
                if (clicked)
                {
                    _errorText = string.Empty;
                    _editor.OpenRecentProject(entry.Path);
                }
                ImGui.Dummy(new Vector2(0F, 8F));
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

    /// <summary>
    /// 在欢迎界面顶部显示上次异常退出提示
    /// </summary>
    /// <param name="display">欢迎窗口尺寸</param>
    /// <returns>提示区域占用高度</returns>
    private static float DrawCrashNotice(Vector2 display)
    {
        var crash = MVoxelLog.PreviousCrash;
        if (crash is null)
        {
            return 0F;
        }

        const float margin = 22F;
        const float height = 92F;
        ImGui.SetCursorPos(new Vector2(margin, TitleBarHeight + 10F));
        ImGui.BeginChild("##PreviousCrashNotice", new Vector2(display.X - margin * 2F, height), ImGuiChildFlags.Borders);
        ImGui.TextColored(ToColor(EditorPalette.Error), "检测到上次运行异常退出");
        ImGui.TextWrapped($"时间：{crash.OccurredAt:yyyy-MM-dd HH:mm:ss}  ·  {crash.Summary}");
        ImGui.TextDisabled($"详细日志：{crash.ReportPath}");
        if (ImGui.Button("我知道了##PreviousCrash"))
        {
            MVoxelLog.AcknowledgePreviousCrash();
        }

        ImGui.EndChild();
        return height + 20F;
    }

    private void BeginPathDialog(int mode)
    {
        _pathMode = mode;
        if (mode == 1) _selectedTemplate = ProjectTemplateId.Empty;
        _pathText = mode == 1 ? Path.Combine(_editor.DefaultProjectRootPath, "新项目") : string.Empty;
        if (mode == 2)
        {
            var start = Directory.Exists(_editor.DefaultProjectRootPath)
                ? _editor.DefaultProjectRootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _browserPath = Path.GetFullPath(start);
            _browserSelectedPath = null;
            _browserBack.Clear();
            _browserError = string.Empty;
        }
        _errorText = string.Empty;
        ImGui.OpenPopup("##WelcomeProjectPathDialog");
    }

    private void DrawPathDialog()
    {
        if (_pathMode == 0)
        {
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        var dialogWidth = Math.Min(680F, Math.Max(520F, display.X - 48F));
        var dialogHeight = _pathMode == 1 ? 438F : 610F;
        ImGui.SetNextWindowPos(display / 2F, ImGuiCond.Appearing, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(dialogWidth, dialogHeight), ImGuiCond.Appearing);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28F, 24F));
        if (ImGui.BeginPopupModal("##WelcomeProjectPathDialog", flags))
        {
            var draw = ImGui.GetWindowDrawList();
            var window = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            draw.AddRectFilled(window, window + windowSize, ToPackedColor(EditorPalette.Window), 8F);
            draw.AddRect(window, window + windowSize, ToPackedColor(EditorPalette.Border), 8F,
                ImDrawFlags.None, 1F);
            draw.AddRectFilled(window, window + new Vector2(5F, windowSize.Y),
                ToPackedColor(EditorPalette.Accent), 8F, ImDrawFlags.RoundCornersLeft);
            ImGui.TextColored(ToColor(EditorPalette.Text), _pathMode == 1 ? "新建 MVoxel 项目" : "打开 MVoxel 项目");
            ImGui.Dummy(new Vector2(0F, 6F));
            ImGui.Dummy(new Vector2(0F, 14F));
            if (_pathMode == 1)
            {
                DrawTemplatePicker();
                ImGui.Dummy(new Vector2(0F, 14F));
            }

            if (_pathMode == 1)
            {
                ImGui.TextColored(ToColor(EditorPalette.MutedText), "项目文件夹");
                ImGui.SetNextItemWidth(-1F);
                ImGui.InputText("##WelcomeProjectPath", ref _pathText, 2048);
            }
            else
            {
                DrawProjectBrowser();
            }
            if (!string.IsNullOrWhiteSpace(_errorText))
            {
                ImGui.TextColored(ToColor(EditorPalette.Error), _errorText);
            }

            ImGui.Dummy(new Vector2(0F, 14F));
            if (ImGui.Button(_pathMode == 1 ? "创建项目" : "打开项目", new Vector2(120F, 36F)))
            {
                SubmitPath();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(100F, 36F)))
            {
                _pathMode = 0;
                _errorText = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private void SubmitPath()
    {
        var path = (_pathMode == 2 ? _browserSelectedPath ?? string.Empty : _pathText).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            _errorText = _pathMode == 2 ? "请先选择一个项目文件夹。" : "请先输入项目文件夹路径。";
            return;
        }

        _errorText = string.Empty;
        if (_pathMode == 1)
        {
            _editor.StartNewProjectFromWelcome(path, _selectedTemplate);
        }
        else
        {
            if (!IsProjectFolder(path))
            {
                _errorText = "所选文件夹不是 MVoxel 项目（缺少 Project.mvoxproj）。";
                return;
            }
            _editor.OpenProjectFromWelcome(path);
        }

        _pathMode = 0;
        ImGui.CloseCurrentPopup();
    }

    private void DrawProjectBrowser()
    {
        ImGui.TextColored(ToColor(EditorPalette.MutedText), "选择 MVoxel 项目文件夹");
        ImGui.TextDisabled(_browserPath);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.PanelHeader));
        if (ImGui.Button("^##ProjectBrowserUp", new Vector2(34F, 28F))) NavigateProjectBrowserUp();
        ImGui.SameLine();
        if (ImGui.Button("<##ProjectBrowserBack", new Vector2(34F, 28F))) NavigateProjectBrowserBack();
        ImGui.PopStyleColor();

        ImGui.BeginChild("##ProjectBrowserFolders", new Vector2(0F, 350F), ImGuiChildFlags.Borders);
        try
        {
            var directories = Directory.EnumerateDirectories(_browserPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                var isProject = IsProjectFolder(directory);
                var selected = string.Equals(_browserSelectedPath, directory, StringComparison.OrdinalIgnoreCase);
                var label = $"▰  {name}{(isProject ? "    · MVoxel 项目" : string.Empty)}##{directory}";
                if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0F, 30F)))
                {
                    _browserSelectedPath = directory;
                    _errorText = string.Empty;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) EnterProjectBrowser(directory);
                }
            }
            if (directories.Length == 0) ImGui.TextDisabled("当前文件夹没有可浏览的子文件夹");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _browserError = "无法读取当前文件夹：" + exception.Message;
            ImGui.TextColored(ToColor(EditorPalette.Error), _browserError);
        }
        ImGui.EndChild();
        if (!string.IsNullOrWhiteSpace(_browserSelectedPath))
            ImGui.TextDisabled(IsProjectFolder(_browserSelectedPath) ? "已选择 MVoxel 项目" : "已选择普通文件夹");
    }

    private void EnterProjectBrowser(string path)
    {
        if (!Directory.Exists(path)) return;
        _browserBack.Push(_browserPath);
        _browserPath = Path.GetFullPath(path);
        _browserSelectedPath = null;
        _browserError = string.Empty;
    }

    private void NavigateProjectBrowserBack()
    {
        if (_browserBack.Count == 0) return;
        _browserPath = _browserBack.Pop();
        _browserSelectedPath = null;
        _browserError = string.Empty;
    }

    private void NavigateProjectBrowserUp()
    {
        var parent = Directory.GetParent(_browserPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return;
        _browserBack.Push(_browserPath);
        _browserPath = parent;
        _browserSelectedPath = null;
        _browserError = string.Empty;
    }

    private static bool IsProjectFolder(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, ProjectLayout.ProjectFileName));

    private void OnProjectOpened(object? sender, EventArgs args) => RequestClose();

    /// <summary>
    /// 绘制新项目模板选择器
    /// </summary>
    private void DrawTemplatePicker()
    {
        var selected = ProjectTemplateCatalog.Get(_selectedTemplate);
        if (ImGui.BeginCombo("项目模板", selected.Name))
        {
            foreach (var template in ProjectTemplateCatalog.All)
            {
                var isSelected = template.Id == _selectedTemplate;
                if (ImGui.Selectable(template.Name, isSelected)) _selectedTemplate = template.Id;
                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

    }

    private void OnProjectOperationFailed(object? sender, ProjectOperationFailedEventArgs args) => _errorText = args.Message;

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);

    /// <summary>
    /// 将系统颜色转换为 ImGui 绘图颜色
    /// </summary>
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
