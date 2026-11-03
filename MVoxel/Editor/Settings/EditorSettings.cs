using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Editor;
using MVoxel.Project;
using MVoxel.Rendering;

namespace MVoxel.Editor.Settings;

/// <summary>
/// 可重新绑定的编辑器命令，命令 ID 稳定，显示名称支持中文界面
/// </summary>
public sealed record ShortcutCommandDefinition(string Id, string DisplayName, string DefaultBinding, string Category);

/// <summary>
/// 一组用户可切换的快捷键方案
/// </summary>
public sealed class ShortcutScheme
{
    public string Name = "标准";
    public Dictionary<string, string> Bindings = new(StringComparer.OrdinalIgnoreCase);

    public ShortcutScheme Clone(string name)
    {
        return new ShortcutScheme
        {
            Name = name,
            Bindings = new Dictionary<string, string>(Bindings, StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>自动保存写入恢复区或正式项目。</summary>
public enum AutoSaveMode
{
    RecoverySnapshot,
    Project,
}

/// <summary>
/// 用户目录中的编辑器设置，只保存个人偏好，不把用户布局写入项目
/// </summary>
public sealed class EditorSettingsDocument
{
    public const int CurrentFormatVersion = 2;

    /// <summary>
    /// 新建项目对话框默认使用的项目根目录
    /// </summary>
    public static string DefaultProjectRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
            ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MVoxel Projects");

    public int FormatVersion = CurrentFormatVersion;
    public string ProjectRootPath = DefaultProjectRootPath;
    public string ActiveShortcutScheme = "标准";
    public List<ShortcutScheme> ShortcutSchemes = new();
    public bool McMode;
    public float UiScale = 1F;
    public string Theme = EditorPalette.DefaultThemeName;
    public bool EnableGpuDiagnostics;
    public bool UseHardwareRayTracing;
    public bool ShowSceneGrid = true;
    public bool ShowVoxelOutlines = true;
    public float SceneMoveSpeed = 12F;
    public float SceneNearClip = 0.05F;
    public float SceneFarClip = 100000F;
    public float SceneFieldOfView = 50F;
    public RenderSettings SceneRenderSettings = new();
    public RenderSettings RenderViewSettings = new();
    public RenderSettings ExportRenderSettings = new();
    public int UndoRepeatDelayMs = 350;
    public int UndoRepeatIntervalMs = 70;
    public int ExportParallelism = 1;
    public bool AutoSave = true;
    public int AutoSaveSeconds = 600;
    public AutoSaveMode AutoSaveMode = AutoSaveMode.RecoverySnapshot;

    public static EditorSettingsDocument CreateDefault()
    {
        var document = new EditorSettingsDocument();
        RenderQualityPresets.Apply(document.RenderViewSettings, RenderQualityPreset.Ultra);
        document.RenderViewSettings.PathMaxAccumulatedSamples = 128;
        document.ShortcutSchemes.Add(CreateDefaultScheme());
        return document;
    }

    public ShortcutScheme ActiveScheme()
    {
        Normalize();
        return ShortcutSchemes.First(scheme => string.Equals(scheme.Name, ActiveShortcutScheme,
            StringComparison.OrdinalIgnoreCase));
    }

    public void Normalize()
    {
        if (string.Equals(Theme, "MVoxel 浅白", StringComparison.Ordinal) ||
            string.Equals(Theme, "MVoxel 深色", StringComparison.Ordinal))
        {
            Theme = EditorPalette.DefaultThemeName;
        }

        ExportParallelism = Math.Clamp(ExportParallelism, 1, 4);
        AutoSaveSeconds = Math.Clamp(AutoSaveSeconds, 10, 3600);
        if (!Enum.IsDefined(AutoSaveMode)) AutoSaveMode = AutoSaveMode.RecoverySnapshot;
        ProjectRootPath = string.IsNullOrWhiteSpace(ProjectRootPath)
            ? DefaultProjectRootPath
            : ProjectRootPath.Trim().Trim('"');
        // 迁移旧版程序目录下的默认值；不移动已有项目，也不改动自定义目录。
        if (string.Equals(ProjectRootPath, Path.Combine(AppPaths.BaseDirectory, "MVoxel_Workspace"), StringComparison.OrdinalIgnoreCase))
            ProjectRootPath = DefaultProjectRootPath;
        ShortcutSchemes ??= new List<ShortcutScheme>();
        if (ShortcutSchemes.Count == 0) ShortcutSchemes.Add(CreateDefaultScheme());

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in ShortcutSchemes)
        {
            scheme.Name = string.IsNullOrWhiteSpace(scheme.Name) ? "未命名方案" : scheme.Name.Trim();
            if (!names.Add(scheme.Name)) scheme.Name = $"{scheme.Name} {names.Count + 1}";
            scheme.Bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in ShortcutCommands.All)
            {
                if (!scheme.Bindings.ContainsKey(command.Id)) scheme.Bindings[command.Id] = command.DefaultBinding;
            }
        }

        if (!ShortcutSchemes.Any(scheme => string.Equals(scheme.Name, ActiveShortcutScheme,
                StringComparison.OrdinalIgnoreCase)))
        {
            ActiveShortcutScheme = ShortcutSchemes[0].Name;
        }

        UiScale = float.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.75F, 2F) : 1F;
        Theme = string.IsNullOrWhiteSpace(Theme) ? EditorPalette.DefaultThemeName : Theme.Trim();
        UndoRepeatDelayMs = Math.Clamp(UndoRepeatDelayMs, 100, 2000);
        UndoRepeatIntervalMs = Math.Clamp(UndoRepeatIntervalMs, 20, 500);
        SceneMoveSpeed = float.IsFinite(SceneMoveSpeed) ? Math.Clamp(SceneMoveSpeed, 0.1F, 256F) : 12F;
        SceneNearClip = float.IsFinite(SceneNearClip) ? Math.Clamp(SceneNearClip, 0.001F, 1000F) : 0.05F;
        SceneFarClip = float.IsFinite(SceneFarClip) ? Math.Clamp(SceneFarClip, SceneNearClip + 1F, 100000000F) : 100000F;
        SceneFieldOfView = float.IsFinite(SceneFieldOfView) ? Math.Clamp(SceneFieldOfView, 20F, 120F) : 50F;
        SceneRenderSettings ??= new RenderSettings();
        RenderViewSettings ??= new RenderSettings();
        ExportRenderSettings ??= new RenderSettings();
        SceneRenderSettings.SanitizeNaN();
        SceneRenderSettings.ClampAll();
        RenderViewSettings.SanitizeNaN();
        RenderViewSettings.ClampAll();
        ExportRenderSettings.SanitizeNaN();
        ExportRenderSettings.ClampAll();
    }

    private static ShortcutScheme CreateDefaultScheme()
    {
        var scheme = new ShortcutScheme();
        foreach (var command in ShortcutCommands.All) scheme.Bindings[command.Id] = command.DefaultBinding;
        return scheme;
    }
}

/// <summary>
/// 项目级配置。LOD 只影响场景编辑视图；RenderSettings 供渲染页和导出使用，
/// 统一写进项目设置后，项目可以保存一致的画面配置
/// </summary>
/// <summary>
/// 渲染画面的比例或分辨率预设。比例用份数（16:9），分辨率用像素（1920×1080）。
/// 每个项目各自保存一份，可以自由增删改排序。
/// </summary>
public sealed class RenderFramePreset
{
    /// <summary>稳定标识，当前选择按它记录，改名不影响选中状态</summary>
    public string Id = string.Empty;
    /// <summary>显示名，用户可以随意改</summary>
    public string Name = string.Empty;
    /// <summary>比例份数或像素宽</summary>
    public int Width = 16;
    /// <summary>比例份数或像素高</summary>
    public int Height = 9;

    /// <summary>读比例值：宽 ÷ 高</summary>
    public float Aspect => Height > 0 ? (float)Width / Height : 0F;
}

public sealed class ProjectSettingsDocument
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion = CurrentFormatVersion;
    public string TemplateId = "未指定";
    public bool ShowGrid = true;
    public bool ShowVoxelOutlines = true;
    public string VoxelUnit = "1 格 = 1 体素单位";
    /// <summary>
    /// 当前项目使用的调色板资产路径，相对于 Assets；Palettes 下可以存在多个调色板。
    /// </summary>
    public string ActivePalettePath = EditorPalettes.ProjectPaletteRelativePath;
    public string DefaultRenderPreset = "默认";
    public float SceneMoveSpeed = 12F;
    public float SceneNearClip = 0.05F;
    public float SceneFarClip = 100000F;
    public float SceneFieldOfView = 50F;
    public RenderSettings SceneRenderSettings = new();
    public RenderSettings RenderSettings = new();
    public RenderSettings ExportRenderSettings = new();
    /// <summary>新建相机和未覆盖相机使用的默认后处理栈</summary>
    public CameraPostProcessStack DefaultCameraPostProcess = CameraPostProcessStack.Default;

    /// <summary>
    public List<RenderFramePreset> RenderAspectPresets = CreateDefaultAspectPresets();

    /// <summary>渲染视图与导出可选的分辨率预设；空 Id 表示跟随视图尺寸</summary>
    public List<RenderFramePreset> RenderResolutionPresets = CreateDefaultResolutionPresets();

    /// <summary>当前选中的比例预设 Id，空表示自由比例</summary>
    public string RenderAspectPresetId = string.Empty;

    /// <summary>当前选中的分辨率预设 Id，空表示跟随视图；预览不使用它，仅导出对话框的默认值</summary>
    public string RenderResolutionPresetId = string.Empty;

    /// <summary>导出对话框上次使用的格式：png / hdr / all / video</summary>
    public string ExportDialogFormat = "png";

    /// <summary>视频导出使用的时间轴资产相对Assets路径</summary>
    public string ExportDialogTimelineAsset = string.Empty;

    /// <summary>视频导出帧率</summary>
    public int ExportDialogVideoFrameRate = 30;

    /// <summary>视频导出起始帧</summary>
    public int ExportDialogVideoStartFrame;

    /// <summary>视频导出结束帧</summary>
    public int ExportDialogVideoEndFrame = 300;

    /// <summary>导出对话框上次选择的分辨率预设 Id，空表示按比例从视图推算</summary>
    public string ExportDialogResolutionId = string.Empty;

    /// <summary>导出对话框上次是否使用自定义宽高</summary>
    public bool ExportDialogCustomSize;

    /// <summary>导出对话框上次填写的自定义宽高</summary>
    public int ExportDialogWidth = 1920;

    /// <summary>导出对话框上次填写的自定义高</summary>
    public int ExportDialogHeight = 1080;

    /// <summary>新建项目时的常见画面比例</summary>
    public static List<RenderFramePreset> CreateDefaultAspectPresets() =>
    [
        new() { Id = "1-1", Name = "1:1", Width = 1, Height = 1 },
        new() { Id = "4-3", Name = "4:3", Width = 4, Height = 3 },
        new() { Id = "3-2", Name = "3:2", Width = 3, Height = 2 },
        new() { Id = "16-9", Name = "16:9", Width = 16, Height = 9 },
        new() { Id = "21-9", Name = "21:9", Width = 21, Height = 9 },
    ];

    /// <summary>新建项目时的常见导出分辨率</summary>
    public static List<RenderFramePreset> CreateDefaultResolutionPresets() =>
    [
        new() { Id = "1280x720", Name = "1280 × 720", Width = 1280, Height = 720 },
        new() { Id = "1920x1080", Name = "1920 × 1080", Width = 1920, Height = 1080 },
        new() { Id = "2560x1440", Name = "2560 × 1440", Width = 2560, Height = 1440 },
        new() { Id = "3840x2160", Name = "3840 × 2160（4K）", Width = 3840, Height = 2160 },
    ];

    /// <summary>按 Id 找比例预设，找不到就是自由比例</summary>
    public RenderFramePreset? FindAspectPreset() =>
        RenderAspectPresets.FirstOrDefault(preset => preset.Id == RenderAspectPresetId);

    /// <summary>按 Id 找分辨率预设，找不到就是跟随视图</summary>
    public RenderFramePreset? FindResolutionPreset() =>
        RenderResolutionPresets.FirstOrDefault(preset => preset.Id == RenderResolutionPresetId);

    public static ProjectSettingsDocument CreateDefault()
    {
        var settings = new ProjectSettingsDocument();
        RenderQualityPresets.Apply(settings.RenderSettings, RenderQualityPreset.Ultra);
        settings.RenderSettings.UseHardwareRayTracing = false;
        // 预览默认 128 采样；导出仍使用独立的质量设置。
        settings.RenderSettings.PathMaxAccumulatedSamples = 128;
        return settings;
    }

    public void Normalize()
    {
        TemplateId = string.IsNullOrWhiteSpace(TemplateId) ? "未指定" : TemplateId.Trim();
        VoxelUnit = string.IsNullOrWhiteSpace(VoxelUnit) ? "1 格 = 1 体素单位" : VoxelUnit.Trim();
        ActivePalettePath = EditorPalettes.NormalizeProjectPalettePath(ActivePalettePath);
        DefaultRenderPreset = string.IsNullOrWhiteSpace(DefaultRenderPreset) ? "默认" : DefaultRenderPreset.Trim();
        SceneMoveSpeed = float.IsFinite(SceneMoveSpeed) ? Math.Clamp(SceneMoveSpeed, 0.1F, 256F) : 12F;
        SceneNearClip = float.IsFinite(SceneNearClip) ? Math.Clamp(SceneNearClip, 0.001F, 1000F) : 0.05F;
        SceneFarClip = float.IsFinite(SceneFarClip) ? Math.Clamp(SceneFarClip, SceneNearClip + 1F, 100000000F) : 100000F;
        SceneFieldOfView = float.IsFinite(SceneFieldOfView) ? Math.Clamp(SceneFieldOfView, 20F, 120F) : 50F;
        RenderSettings ??= new RenderSettings();
        SceneRenderSettings ??= new RenderSettings();
        ExportRenderSettings ??= new RenderSettings();
        DefaultCameraPostProcess ??= CameraPostProcessStack.Default;
        DefaultCameraPostProcess.Normalize();
        RenderAspectPresets ??= CreateDefaultAspectPresets();
        RenderResolutionPresets ??= CreateDefaultResolutionPresets();
        RenderAspectPresets.RemoveAll(preset => preset is null || preset.Width <= 0 || preset.Height <= 0);
        RenderResolutionPresets.RemoveAll(preset => preset is null || preset.Width <= 0 || preset.Height <= 0);
        foreach (var preset in RenderAspectPresets.Concat(RenderResolutionPresets))
        {
            if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Guid.NewGuid().ToString("N")[..8];
            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = $"{preset.Width}:{preset.Height}";
            preset.Name = preset.Name.Trim();
            preset.Width = Math.Clamp(preset.Width, 1, 16384);
            preset.Height = Math.Clamp(preset.Height, 1, 16384);
        }

        ExportDialogWidth = Math.Clamp(ExportDialogWidth, 16, 8192);
        ExportDialogHeight = Math.Clamp(ExportDialogHeight, 16, 8192);
        if (ExportDialogFormat is not ("png" or "hdr" or "all" or "video")) ExportDialogFormat = "png";
        ExportDialogVideoFrameRate = Math.Clamp(ExportDialogVideoFrameRate, 1, 240);
        ExportDialogVideoStartFrame = Math.Max(0, ExportDialogVideoStartFrame);
        ExportDialogVideoEndFrame = Math.Max(ExportDialogVideoStartFrame, ExportDialogVideoEndFrame);

        // 选中的预设被删掉时回落到自由比例 / 跟随视图
        if (RenderAspectPresets.All(preset => preset.Id != RenderAspectPresetId)) RenderAspectPresetId = string.Empty;
        if (RenderResolutionPresets.All(preset => preset.Id != RenderResolutionPresetId)) RenderResolutionPresetId = string.Empty;
        if (RenderResolutionPresets.All(preset => preset.Id != ExportDialogResolutionId)) ExportDialogResolutionId = string.Empty;
        SceneRenderSettings.SanitizeNaN();
        SceneRenderSettings.ClampAll();
        RenderSettings.SanitizeNaN();
        RenderSettings.ClampAll();
        ExportRenderSettings.SanitizeNaN();
        ExportRenderSettings.ClampAll();
    }
}

/// <summary>
/// 内置命令清单，快捷键方案只修改绑定，不修改稳定 ID
/// </summary>
public static class ShortcutCommands
{
    public static readonly IReadOnlyList<ShortcutCommandDefinition> All = new ShortcutCommandDefinition[]
    {
        new("edit.undo", "撤销", "Ctrl+Z", "编辑"),
        new("edit.redo", "重做", "Ctrl+Y", "编辑"),
        new("edit.redoAlternate", "重做（备用）", "Ctrl+Shift+Z", "编辑"),
        new("project.new", "新建项目", "Ctrl+N", "项目"),
        new("project.open", "打开项目", "Ctrl+O", "项目"),
        new("project.save", "保存项目", "Ctrl+S", "项目"),
        new("viewport.camera", "相机工具", "C", "视口"),
        new("viewport.draw", "绘制工具", "B", "视口"),
        new("viewport.erase", "擦除工具", "E", "视口"),
        new("viewport.focus", "聚焦选中体素", "F", "视口"),
        new("viewport.toggleGrid", "显示/隐藏网格", "G", "视口"),
        new("viewport.togglePanels", "显示/隐藏面板", "Shift+Space", "视口"),
    };
}

/// <summary>
/// 设置文件的容错读写入口，所有写入都走原子替换，损坏文件使用默认值
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string EditorSettingsPath => AppPaths.ConfigPath("editor-settings.json");

    public static EditorSettingsDocument LoadEditor()
    {
        try
        {
            if (File.Exists(EditorSettingsPath))
            {
                var json = File.ReadAllText(EditorSettingsPath);
                EnsureCurrentFormatVersion(json, "编辑器设置", EditorSettingsDocument.CurrentFormatVersion);
                var document = JsonSerializer.Deserialize<EditorSettingsDocument>(json, JsonOptions)
                               ?? EditorSettingsDocument.CreateDefault();
                document.Normalize();
                return document;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return EditorSettingsDocument.CreateDefault();
    }

    public static void SaveEditor(EditorSettingsDocument document)
    {
        document.Normalize();
        AtomicFile.WriteText(EditorSettingsPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    internal static T Restore<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;

    internal static string Snapshot<T>(T document) => JsonSerializer.Serialize(document, JsonOptions);

    public static ProjectSettingsDocument LoadProject(ProjectHandle project)
    {
        try
        {
            if (File.Exists(project.ProjectSettingsPath))
            {
                var json = File.ReadAllText(project.ProjectSettingsPath);
                EnsureCurrentFormatVersion(json, "项目设置", ProjectSettingsDocument.CurrentFormatVersion);
                var document = JsonSerializer.Deserialize<ProjectSettingsDocument>(json, JsonOptions)
                               ?? ProjectSettingsDocument.CreateDefault();
                document.Normalize();
                return document;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return ProjectSettingsDocument.CreateDefault();
    }

    private static void EnsureCurrentFormatVersion(string json, string description, int currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("formatVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) || version != currentVersion)
        {
            throw new InvalidDataException(
                $"{description}格式版本不受支持，当前只接受版本 {currentVersion}，请在当前版本重新创建");
        }
    }

    public static void SaveProject(ProjectHandle project, ProjectSettingsDocument document)
    {
        document.Normalize();
        Directory.CreateDirectory(project.ProjectSettingsPath[..project.ProjectSettingsPath.LastIndexOf(Path.DirectorySeparatorChar)]);
        AtomicFile.WriteText(project.ProjectSettingsPath, JsonSerializer.Serialize(document, JsonOptions));
    }
}

/// <summary>
/// 将方案中的简单文本绑定转换为 ImGui 按键判断。未知绑定会安全地返回 false，
/// 防止文本输入框或插件快捷键误触发编辑命令
/// </summary>
public static class ShortcutBindingRouter
{
    /// <summary>
    /// 判断快捷键是否在当前帧首次按下
    /// </summary>
    public static bool IsPressed(ShortcutScheme scheme, string commandId)
    {
        return TryResolve(scheme, commandId, out var imguiKey, out var ctrl, out var shift, out var alt) &&
               ModifiersMatch(ctrl, shift, alt) && ImGui.IsKeyPressed(imguiKey, false);
    }

    /// <summary>
    /// 判断快捷键是否仍处于按住状态
    /// </summary>
    public static bool IsDown(ShortcutScheme scheme, string commandId)
    {
        return TryResolve(scheme, commandId, out var imguiKey, out var ctrl, out var shift, out var alt) &&
               ModifiersMatch(ctrl, shift, alt) && ImGui.IsKeyDown(imguiKey);
    }

    /// <summary>
    /// 解析方案中的按键和修饰键
    /// </summary>
    private static bool TryResolve(ShortcutScheme scheme, string commandId, out ImGuiKey imguiKey,
        out bool ctrl, out bool shift, out bool alt)
    {
        imguiKey = ImGuiKey.None;
        ctrl = false;
        shift = false;
        alt = false;
        if (!scheme.Bindings.TryGetValue(commandId, out var binding)) return false;
        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !TryGetImGuiKey(parts[^1], out imguiKey)) return false;
        ctrl = parts.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase));
        shift = parts.Any(part => part.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        alt = parts.Any(part => part.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// 判断当前帧的修饰键状态是否符合绑定
    /// </summary>
    private static bool ModifiersMatch(bool ctrl, bool shift, bool alt)
    {
        var io = ImGui.GetIO();
        return io.KeyCtrl == ctrl && io.KeyShift == shift && io.KeyAlt == alt && !io.KeySuper;
    }

    public static bool TryGetImGuiKey(string text, out ImGuiKey key)
    {
        key = text.ToUpperInvariant() switch
        {
            "A" => ImGuiKey.A, "B" => ImGuiKey.B, "C" => ImGuiKey.C, "D" => ImGuiKey.D,
            "E" => ImGuiKey.E, "F" => ImGuiKey.F, "G" => ImGuiKey.G, "H" => ImGuiKey.H,
            "I" => ImGuiKey.I, "J" => ImGuiKey.J, "K" => ImGuiKey.K, "L" => ImGuiKey.L,
            "M" => ImGuiKey.M, "N" => ImGuiKey.N, "O" => ImGuiKey.O, "P" => ImGuiKey.P,
            "Q" => ImGuiKey.Q, "R" => ImGuiKey.R, "S" => ImGuiKey.S, "T" => ImGuiKey.T,
            "U" => ImGuiKey.U, "V" => ImGuiKey.V, "W" => ImGuiKey.W, "X" => ImGuiKey.X,
            "Y" => ImGuiKey.Y, "Z" => ImGuiKey.Z, "SPACE" => ImGuiKey.Space,
            "0" => ImGuiKey._0, "1" => ImGuiKey._1, "2" => ImGuiKey._2, "3" => ImGuiKey._3,
            "4" => ImGuiKey._4, "5" => ImGuiKey._5, "6" => ImGuiKey._6, "7" => ImGuiKey._7,
            "8" => ImGuiKey._8, "9" => ImGuiKey._9,
            "ENTER" or "RETURN" => ImGuiKey.Enter, "TAB" => ImGuiKey.Tab,
            "ESC" or "ESCAPE" => ImGuiKey.Escape, "BACKSPACE" => ImGuiKey.Backspace,
            "DELETE" or "DEL" => ImGuiKey.Delete, "INSERT" or "INS" => ImGuiKey.Insert,
            "HOME" => ImGuiKey.Home, "END" => ImGuiKey.End,
            "PAGEUP" or "PGUP" => ImGuiKey.PageUp, "PAGEDOWN" or "PGDN" => ImGuiKey.PageDown,
            "LEFT" => ImGuiKey.LeftArrow, "RIGHT" => ImGuiKey.RightArrow,
            "UP" => ImGuiKey.UpArrow, "DOWN" => ImGuiKey.DownArrow,
            "F1" => ImGuiKey.F1, "F2" => ImGuiKey.F2, "F3" => ImGuiKey.F3,
            "F4" => ImGuiKey.F4, "F5" => ImGuiKey.F5, "F6" => ImGuiKey.F6,
            "F7" => ImGuiKey.F7, "F8" => ImGuiKey.F8, "F9" => ImGuiKey.F9,
            "F10" => ImGuiKey.F10, "F11" => ImGuiKey.F11, "F12" => ImGuiKey.F12,
            _ => ImGuiKey.None,
        };
        return key != ImGuiKey.None;
    }
}
