using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Rendering;

namespace MVoxel.Editor.Settings;

/// <summary>
/// 可重新绑定的编辑器命令。命令 ID 稳定，显示名称可以随中文界面演进。
/// </summary>
public sealed record ShortcutCommandDefinition(string Id, string DisplayName, string DefaultBinding, string Category);

/// <summary>
/// 一组用户可切换的快捷键方案。
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

/// <summary>
/// 用户目录中的编辑器设置。它只保存个人偏好，不把用户布局强塞进项目。
/// </summary>
public sealed class EditorSettingsDocument
{
    public int FormatVersion = 1;
    public string ActiveShortcutScheme = "标准";
    public List<ShortcutScheme> ShortcutSchemes = new();
    public bool McMode;
    public float UiScale = 1F;
    public string Theme = "MVoxel 深色";
    public bool ShowWelcomeOnStartup = true;
    public bool EnableGpuDiagnostics;

    public static EditorSettingsDocument CreateDefault()
    {
        var document = new EditorSettingsDocument();
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
        Theme = string.IsNullOrWhiteSpace(Theme) ? "MVoxel 深色" : Theme.Trim();
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
/// 统一写进项目设置后，新机器打开项目也能得到相同的画面。
/// </summary>
public sealed class ProjectSettingsDocument
{
    public int FormatVersion = 1;
    public int LodLevels = 4;
    public float[] LodDistances = { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F };
    public bool ShowGrid = true;
    public bool ShowVoxelOutlines = true;
    public bool ShowLighting = true;
    public string VoxelUnit = "1 格 = 1 体素单位";
    public string DefaultRenderPreset = "默认";
    public bool AutoSave = true;
    public int AutoSaveSeconds = 120;
    public RenderSettings RenderSettings = new();

    public static ProjectSettingsDocument CreateDefault() => new();

    public void Normalize()
    {
        LodDistances ??= Array.Empty<float>();
        if (LodDistances.Length != 8)
        {
            LodDistances = LodDistances
                .Concat(new[] { 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F, 8192F })
                .Take(8)
                .ToArray();
        }

        LodLevels = Math.Clamp(LodLevels, 1, LodDistances.Length);
        for (var index = 0; index < LodDistances.Length; index++)
        {
            var fallback = index == 0 ? 64F : LodDistances[index - 1] * 2F;
            LodDistances[index] = float.IsFinite(LodDistances[index])
                ? Math.Clamp(LodDistances[index], index == 0 ? 1F : LodDistances[index - 1], 16384F)
                : Math.Clamp(fallback, index == 0 ? 1F : LodDistances[index - 1], 16384F);
        }

        AutoSaveSeconds = Math.Clamp(AutoSaveSeconds, 10, 3600);
        VoxelUnit = string.IsNullOrWhiteSpace(VoxelUnit) ? "1 格 = 1 体素单位" : VoxelUnit.Trim();
        DefaultRenderPreset = string.IsNullOrWhiteSpace(DefaultRenderPreset) ? "默认" : DefaultRenderPreset.Trim();
        RenderSettings ??= new RenderSettings();
        RenderSettings.SanitizeNaN();
        RenderSettings.ClampAll();
    }
}

/// <summary>
/// 内置命令清单。快捷键方案只修改绑定，不修改这些稳定 ID。
/// </summary>
public static class ShortcutCommands
{
    public static readonly IReadOnlyList<ShortcutCommandDefinition> All = new ShortcutCommandDefinition[]
    {
        new("edit.undo", "撤销", "Ctrl+Z", "编辑"),
        new("edit.redo", "重做", "Ctrl+Y", "编辑"),
        new("project.new", "新建项目", "Ctrl+N", "项目"),
        new("project.open", "打开项目", "Ctrl+O", "项目"),
        new("viewport.camera", "相机工具", "C", "视口"),
        new("viewport.draw", "绘制工具", "B", "视口"),
        new("viewport.erase", "擦除工具", "E", "视口"),
        new("viewport.focus", "聚焦选中体素", "F", "视口"),
        new("viewport.toggleGrid", "显示/隐藏网格", "G", "视口"),
        new("viewport.togglePanels", "显示/隐藏面板", "Shift+Space", "视口"),
    };
}

/// <summary>
/// 设置文件的容错读写入口。所有写入都走原子替换，损坏文件回退默认值。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string EditorSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVoxel", "editor-settings.json");

    public static EditorSettingsDocument LoadEditor()
    {
        try
        {
            if (File.Exists(EditorSettingsPath))
            {
                var document = JsonSerializer.Deserialize<EditorSettingsDocument>(File.ReadAllText(EditorSettingsPath), JsonOptions)
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

    public static ProjectSettingsDocument LoadProject(ProjectHandle project)
    {
        try
        {
            if (File.Exists(project.ProjectSettingsPath))
            {
                var document = JsonSerializer.Deserialize<ProjectSettingsDocument>(
                                   File.ReadAllText(project.ProjectSettingsPath), JsonOptions)
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

    public static void SaveProject(ProjectHandle project, ProjectSettingsDocument document)
    {
        document.Normalize();
        Directory.CreateDirectory(project.ProjectSettingsPath[..project.ProjectSettingsPath.LastIndexOf(Path.DirectorySeparatorChar)]);
        AtomicFile.WriteText(project.ProjectSettingsPath, JsonSerializer.Serialize(document, JsonOptions));
    }
}

/// <summary>
/// 将方案中的简单文本绑定转换为 ImGui 按键判断。未知绑定会安全地返回 false，
/// 不会让文本输入框或插件快捷键误触发编辑命令。
/// </summary>
public static class ShortcutBindingRouter
{
    public static bool IsPressed(ShortcutScheme scheme, string commandId)
    {
        if (!scheme.Bindings.TryGetValue(commandId, out var binding)) return false;
        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var key = parts[^1];
        var ctrl = parts.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase));
        var shift = parts.Any(part => part.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var alt = parts.Any(part => part.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        var io = ImGui.GetIO();
        if (io.KeyCtrl != ctrl || io.KeyShift != shift || io.KeyAlt != alt) return false;
        return TryGetImGuiKey(key, out var imguiKey) && ImGui.IsKeyPressed(imguiKey, false);
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
            "F1" => ImGuiKey.F1, "F2" => ImGuiKey.F2, "F3" => ImGuiKey.F3,
            "F4" => ImGuiKey.F4, "F5" => ImGuiKey.F5, "F6" => ImGuiKey.F6,
            "F7" => ImGuiKey.F7, "F8" => ImGuiKey.F8, "F9" => ImGuiKey.F9,
            "F10" => ImGuiKey.F10, "F11" => ImGuiKey.F11, "F12" => ImGuiKey.F12,
            _ => ImGuiKey.None,
        };
        return key != ImGuiKey.None;
    }
}
