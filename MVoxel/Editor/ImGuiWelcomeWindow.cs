using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using MVoxel.Editor.Templates;
using OpenTK.Mathematics;
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
    void CloseEditor();
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
/// 欢迎界面的内容绘制层，由独立窗口宿主调用。
/// </summary>
internal sealed class ImGuiWelcomeContent
{
    private const float TitleBarHeight = 34F;
    private readonly IEditorWelcomeHost _editor;
    private VulkanImGuiRenderer? _imguiRenderer;
    private EditorFontCatalog? _fonts;
    private nint _logoTextureId;
    private string _pathText = string.Empty;
    private string _browserPath = string.Empty;
    private string? _browserSelectedPath;
    private readonly Stack<string> _browserBack = new();
    private string _browserError = string.Empty;
    private string _errorText = string.Empty;
    private int _pathMode;
    private ProjectTemplateId _selectedTemplate = ProjectTemplateId.Empty;
    private string _templateAutoName = string.Empty;
    private int _closeRequested;
    private int _closeAllowed;

    internal ImGuiWelcomeContent(IEditorWelcomeHost editor)
    {
        _editor = editor;
        _editor.ProjectOpened += OnProjectOpened;
        _editor.ProjectOperationFailed += OnProjectOperationFailed;
    }

    internal bool IsOpen => Volatile.Read(ref _closeRequested) == 0;

    internal void Initialize(VulkanImGuiRenderer renderer, EditorFontCatalog fonts)
    {
        _imguiRenderer = renderer;
        _fonts = fonts;
        if (AppBranding.TryLoadLogoPixels(out var logoPixels, out var logoWidth, out var logoHeight))
            _logoTextureId = renderer.RegisterTexture(logoPixels, logoWidth, logoHeight);
    }

    internal void ConfigureForHost(VulkanImGuiController controller, float dpi)
    {
        _fonts?.Load(controller, dpi);
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 8F;
        style.PopupRounding = 8F;
        style.PopupBorderSize = 1F;
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
        style.Colors[(int)ImGuiCol.Text] = ToColor(EditorPalette.Text);
        style.Colors[(int)ImGuiCol.TextDisabled] = ToColor(EditorPalette.MutedText);
    }

    internal void RequestClose()
    {
        Interlocked.Exchange(ref _closeAllowed, 1);
        Interlocked.Exchange(ref _closeRequested, 1);
    }

    internal void Dispose()
    {
        _editor.ProjectOpened -= OnProjectOpened;
        _editor.ProjectOperationFailed -= OnProjectOperationFailed;
        if (_logoTextureId != 0) _imguiRenderer?.UnregisterTexture(_logoTextureId);
        _logoTextureId = 0;
    }

    internal void Draw()
    {
        var screen = ImGui.GetIO().DisplaySize;
        var panelWidth = Math.Min(780F, Math.Max(520F, screen.X - 32F));
        var recent = RecentProjects.Load();
        var recentLineHeight = ImGui.GetTextLineHeight();
        var recentRowHeight = Math.Max(62F, recentLineHeight * 2F + 24F);
        const float recentRowGap = 4F;
        var recentContentHeight = recent.Count == 0
            ? 60F
            : recent.Count * (recentRowHeight + recentRowGap + ImGui.GetStyle().ItemSpacing.Y * 2F) + 8F +
              ImGui.GetStyle().WindowPadding.Y * 2F + 4F;
        var crashNoticeHeight = MeasureCrashNoticeHeight(new Vector2(panelWidth, screen.Y));
        var contentHeight = ImGui.GetStyle().WindowPadding.Y * 2F + TitleBarHeight + crashNoticeHeight +
                            8F + 80F + 16F + 40F + 12F + ImGui.GetTextLineHeightWithSpacing() +
                            5F + recentContentHeight + ImGui.GetTextLineHeightWithSpacing() * 2F + 16F +
                            ImGui.GetStyle().ItemSpacing.Y * 6F;
        var panelSize = new Vector2(panelWidth, Math.Min(contentHeight, Math.Max(320F, screen.Y - 48F)));
        var operation = _editor.ProjectOperationSnapshot;
        if (operation is not null) ImGui.BeginDisabled();
        ImGui.SetNextWindowPos(screen / 2F, ImGuiCond.Always, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(panelSize, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.Begin("##MVoxelWelcome", flags);
        var display = ImGui.GetWindowSize();

        var titleBarPosition = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##WelcomeTitleBar", new Vector2(display.X, TitleBarHeight));
        var titleBarMax = titleBarPosition + new Vector2(display.X, TitleBarHeight);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(titleBarPosition, titleBarMax, ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.PanelHeader)));
        drawList.AddLine(titleBarMax - new Vector2(0F, 1F), titleBarMax - new Vector2(display.X, 1F),
            ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.Border)), 1F);
        drawList.AddText(titleBarPosition + new Vector2(14F, (TitleBarHeight - ImGui.GetTextLineHeight()) * 0.5F),
            ImGui.ColorConvertFloat4ToU32(ToColor(EditorPalette.Text)), "欢迎使用 MVoxel");
        ImGui.SetCursorPos(new Vector2(0F, TitleBarHeight));

        DrawCrashNotice(display);
        ImGui.SetCursorPos(new Vector2(0F, TitleBarHeight + crashNoticeHeight));

        var contentWidth = Math.Min(660F, display.X - 68F);
        var contentLeft = MathF.Floor(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8F);
        if (_logoTextureId != IntPtr.Zero)
        {
            ImGui.Image(_logoTextureId, new Vector2(80F, 80F));
            ImGui.SameLine(0F, 14F);
        }

        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10F);
        if (_fonts is not null) ImGui.PushFont(_fonts.Title);
        ImGui.TextColored(ToColor(EditorPalette.Text), "MVoxel");
        if (_fonts is not null) ImGui.PopFont();
        ImGui.TextColored(ToColor(EditorPalette.Accent), "用小方块，创造大世界");
        ImGui.TextDisabled("欢迎使用 MVoxel 体素建模软件");
        ImGui.EndGroup();
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0F, 16F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        if (ImGui.Button("新建项目", new Vector2(210F, 40F))) BeginPathDialog(1);
        ImGui.SameLine();
        if (ImGui.Button("打开项目", new Vector2(210F, 40F))) BeginPathDialog(2);

        ImGui.Dummy(new Vector2(0F, 12F));
        ImGui.SetCursorPosX(Math.Max(34F, (display.X - contentWidth) / 2F));
        ImGui.TextColored(ToColor(EditorPalette.Text), "最近打开的项目");
        ImGui.Separator();

        ImGui.SetCursorPosX(contentLeft);
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() * 2F + 16F;
        var desiredRecentHeight = recentContentHeight;
        var recentHeight = Math.Min(desiredRecentHeight,
            Math.Max(0F, ImGui.GetContentRegionAvail().Y - footerHeight));
        ImGui.BeginChild("##RecentProjects", new Vector2(contentWidth, recentHeight), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
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
                var rowHeight = recentRowHeight;
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
                var date = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var dateWidth = ImGui.CalcTextSize(date).X;
                var titleMaxWidth = Math.Max(1F, rowWidth - dateWidth - 36F);
                var title = FitText(entry.Name, titleMaxWidth);
                var path = FitText(entry.Path, Math.Max(1F, rowWidth - 24F));
                var firstLineY = MathF.Max(6F, (recentRowHeight - recentLineHeight * 2F - 4F) * 0.5F);
                var secondLineY = firstLineY + recentLineHeight + 4F;
                draw.PushClipRect(rowMin, rowMax, true);
                draw.AddText(rowMin + new Vector2(12F, firstLineY), textColor, title);
                draw.AddText(rowMin + new Vector2(rowWidth - dateWidth - 12F, firstLineY), mutedColor, date);
                draw.AddText(rowMin + new Vector2(12F, secondLineY), mutedColor, path);
                draw.PopClipRect();
                if (clicked)
                {
                    _errorText = string.Empty;
                    _editor.OpenRecentProject(entry.Path);
                }
                ImGui.Dummy(new Vector2(0F, recentRowGap));
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
    private static float MeasureCrashNoticeHeight(Vector2 display)
    {
        var crash = MVoxelLog.PreviousCrash;
        if (crash is null)
        {
            return 0F;
        }

        const float margin = 22F;
        var width = display.X - margin * 2F;
        var textWidth = Math.Max(1F, width - ImGui.GetStyle().WindowPadding.X * 2F - 2F);
        var summary = $"时间：{crash.OccurredAt:yyyy-MM-dd HH:mm:ss}  ·  {crash.Summary}";
        var logPath = $"详细日志：{crash.ReportPath}";
        var lineSpacing = ImGui.GetTextLineHeightWithSpacing();
        var contentHeight = ImGui.GetStyle().WindowPadding.Y * 2F + lineSpacing +
                            ImGui.CalcTextSize(summary, false, textWidth).Y +
                            ImGui.CalcTextSize(logPath, false, textWidth).Y +
                            ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 3F + 2F;
        return Math.Max(92F, contentHeight) + 20F;
    }

    private static string FitText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string ellipsis = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + ellipsis).X <= maxWidth) low = middle;
            else high = middle - 1;
        }

        return low == 0 ? ellipsis : text[..low] + ellipsis;
    }

    private static void DrawCrashNotice(Vector2 display)
    {
        var crash = MVoxelLog.PreviousCrash;
        if (crash is null) return;

        const float margin = 22F;
        var width = display.X - margin * 2F;
        var height = MeasureCrashNoticeHeight(display) - 20F;
        var textWidth = Math.Max(1F, width - ImGui.GetStyle().WindowPadding.X * 2F - 2F);
        var summary = $"时间：{crash.OccurredAt:yyyy-MM-dd HH:mm:ss}  ·  {crash.Summary}";
        var logPath = $"详细日志：{crash.ReportPath}";
        var childFlags = height <= display.Y - TitleBarHeight - 20F
            ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            : ImGuiWindowFlags.None;
        ImGui.SetCursorPos(new Vector2(margin, TitleBarHeight + 10F));
        ImGui.BeginChild("##PreviousCrashNotice", new Vector2(width, height), ImGuiChildFlags.Borders, childFlags);
        ImGui.TextColored(ToColor(EditorPalette.Error), "检测到上次运行异常退出");
        ImGui.TextWrapped(summary);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
        ImGui.TextDisabled(logPath);
        ImGui.PopTextWrapPos();
        if (ImGui.Button("我知道了##PreviousCrash"))
        {
            MVoxelLog.AcknowledgePreviousCrash();
        }

        ImGui.EndChild();
    }

    private void BeginPathDialog(int mode)
    {
        _pathMode = mode;
        if (mode == 1) _selectedTemplate = ProjectTemplateId.Empty;
        _templateAutoName = string.Empty;
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
        var dialogHeight = _pathMode == 1 ? 0F : 610F;
        ImGui.SetNextWindowPos(display / 2F, ImGuiCond.Appearing, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(dialogWidth, dialogHeight), ImGuiCond.Appearing);
        if (_pathMode == 1) ImGui.SetNextWindowSizeConstraints(new Vector2(dialogWidth, 0F),
            new Vector2(dialogWidth, Math.Max(200F, display.Y - 48F)));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        if (_pathMode == 1) flags |= ImGuiWindowFlags.AlwaysAutoResize;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28F, 24F));
        if (ImGui.BeginPopupModal("##WelcomeProjectPathDialog", flags))
        {
            var draw = ImGui.GetWindowDrawList();
            var window = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            draw.AddLine(window + new Vector2(3F, 9F), window + new Vector2(3F, windowSize.Y - 9F),
                ToPackedColor(EditorPalette.Accent), 3F);
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
                _locationForm.Draw(ref _pathText);
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
            if (!_locationForm.TryValidate(path, out _errorText)) return;
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
                var label = $"目录  {name}{(isProject ? "    · MVoxel 项目" : string.Empty)}##{directory}";
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
                if (ImGui.Selectable($"{template.Name}  ·  {template.Scale}", isSelected))
                {
                    _selectedTemplate = template.Id;
                    _pathText = ApplyTemplateProjectName(_pathText, _templateAutoName, template.Name, out var autoName);
                    if (autoName is not null) _templateAutoName = autoName;
                }

                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{selected.Description}\n规模：{selected.Scale}\n重点：{selected.Focus}\n学习目标：{selected.LearningGoal}");
        TemplateLearningPreview.Draw(_selectedTemplate);
    }

    /// <summary>
    /// 选择模板后同步项目名：路径末段仍是默认名“新项目”或上一次模板自动名时，
    /// 替换为所选模板名；用户手动改过的名字保持不变，空模板不参与自动命名
    /// </summary>
    /// <param name="pathText">当前项目路径文本</param>
    /// <param name="templateAutoName">上一次由模板自动填入的名字</param>
    /// <param name="templateName">所选模板显示名</param>
    /// <param name="newAutoName">新的自动名，未发生替换时为 null</param>
    /// <returns>替换后的路径文本</returns>
    internal static string ApplyTemplateProjectName(string pathText, string templateAutoName,
        string templateName, out string? newAutoName)
    {
        newAutoName = null;
        if (string.IsNullOrWhiteSpace(pathText)) return pathText;
        var separatorIndex = pathText.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        var folder = separatorIndex < 0 ? pathText : pathText[(separatorIndex + 1)..];
        if (folder != "新项目" && folder != templateAutoName) return pathText;
        if (templateName == "无 · 空项目") return pathText;
        var safeName = new string(templateName.Where(character =>
            !Path.GetInvalidFileNameChars().Contains(character)).ToArray()).Trim();
        if (safeName.Length == 0) return pathText;
        newAutoName = safeName;
        return separatorIndex < 0 ? safeName : pathText[..(separatorIndex + 1)] + safeName;
    }

    private readonly ProjectLocationForm _locationForm = new();

    private void OnProjectOperationFailed(object? sender, ProjectOperationFailedEventArgs args) => _errorText = args.Message;

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);

    /// <summary>
    /// 将系统颜色转换为 ImGui 绘图颜色
    /// </summary>
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
