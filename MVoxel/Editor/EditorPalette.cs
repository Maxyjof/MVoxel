using System.Drawing;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器界面的统一配色表，全部为 sRGB 字节色
/// 交给 ImGui 前统一转成 0 到 1 浮点
/// </summary>
internal static class EditorPalette
{
    /// <summary>
    /// 默认主题名称
    /// </summary>
    internal const string DefaultThemeName = "MVoxel 暗色";

    /// <summary>
    /// 可用主题名称
    /// </summary>
    internal static readonly string[] BuiltInThemeNames =
    {
        DefaultThemeName,
        "柔和浅色",
        "石墨灰",
        "高对比度",
        "海蓝清新",
    };

    private static Dictionary<string, int[]>? _customThemes;
    private static string ThemeFile => MVoxel.Core.AppPaths.ConfigPath("themes.json");
    internal static string[] ThemeNames => BuiltInThemeNames.Concat(LoadThemes().Keys
        .Where(name => !BuiltInThemeNames.Contains(name, StringComparer.OrdinalIgnoreCase))) .ToArray();
    internal static bool IsBuiltIn(string name) => BuiltInThemeNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, int[]> LoadThemes()
    {
        if (_customThemes is not null) return _customThemes;
        try
        {
            _customThemes = File.Exists(ThemeFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(ThemeFile)) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _customThemes = new(); }
        return _customThemes;
    }
    internal static void ReloadThemes() => _customThemes = null;
    internal static int[] CurrentColors() => [Window.ToArgb(), Panel.ToArgb(), PanelHeader.ToArgb(), Input.ToArgb(),
        Border.ToArgb(), Accent.ToArgb(), AccentSoft.ToArgb(), Text.ToArgb(), MutedText.ToArgb(), Icon.ToArgb(), Success.ToArgb(), Error.ToArgb()];
    internal static void SaveTheme(string name, int[] colors)
    {
        if (string.IsNullOrWhiteSpace(name) || colors.Length != 12) throw new ArgumentException("主题名称或颜色无效");
        LoadThemes()[name.Trim()] = (int[])colors.Clone();
        SaveThemeFile();
    }
    internal static void RemoveTheme(string name)
    {
        LoadThemes().Remove(name);
        SaveThemeFile();
    }
    private static void SaveThemeFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
        MVoxel.Core.AtomicFile.WriteText(ThemeFile, System.Text.Json.JsonSerializer.Serialize(LoadThemes(),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 窗口底色
    /// </summary>
    internal static Color Window { get; private set; }

    /// <summary>
    /// 面板底色
    /// </summary>
    internal static Color Panel { get; private set; }

    /// <summary>
    /// 面板标题与分组条底色
    /// </summary>
    internal static Color PanelHeader { get; private set; }

    /// <summary>
    /// 输入控件底色
    /// </summary>
    internal static Color Input { get; private set; }

    /// <summary>
    /// 描边色
    /// </summary>
    internal static Color Border { get; private set; }

    /// <summary>
    /// 主强调色
    /// </summary>
    internal static Color Accent { get; private set; }

    /// <summary>
    /// 柔和强调色
    /// </summary>
    internal static Color AccentSoft { get; private set; }

    /// <summary>
    /// 正文色
    /// </summary>
    internal static Color Text { get; private set; }

    /// <summary>
    /// 次要文字色
    /// </summary>
    internal static Color MutedText { get; private set; }

    /// <summary>
    /// 图标色
    /// </summary>
    internal static Color Icon { get; private set; }

    /// <summary>
    /// 成功状态色
    /// </summary>
    internal static Color Success { get; private set; }

    /// <summary>
    /// 错误状态色
    /// </summary>
    internal static Color Error { get; private set; }

    static EditorPalette()
    {
        ApplyTheme(DefaultThemeName);
    }

    /// <summary>
    /// 应用指定的编辑器颜色主题
    /// </summary>
    /// <param name="themeName">主题名称</param>
    internal static void ApplyTheme(string? themeName)
    {
        var theme = themeName?.Trim();
        (Window, Panel, PanelHeader, Input, Border, Accent, AccentSoft, Text, MutedText, Icon, Success, Error) =
            theme switch
            {
                "柔和浅色" => (
                    Color.FromArgb(255, 232, 237, 243), Color.FromArgb(255, 247, 249, 251),
                    Color.FromArgb(255, 204, 214, 225), Color.FromArgb(255, 239, 243, 247),
                    Color.FromArgb(255, 128, 145, 163), Color.FromArgb(255, 43, 103, 155),
                    Color.FromArgb(255, 198, 218, 236), Color.FromArgb(255, 25, 37, 51),
                    Color.FromArgb(255, 83, 98, 115), Color.FromArgb(255, 52, 83, 112),
                    Color.FromArgb(255, 37, 139, 94), Color.FromArgb(255, 194, 64, 76)),
                "石墨灰" => (
                    Color.FromArgb(255, 39, 42, 47), Color.FromArgb(255, 52, 56, 62),
                    Color.FromArgb(255, 66, 71, 78), Color.FromArgb(255, 31, 34, 39),
                    Color.FromArgb(255, 128, 137, 149), Color.FromArgb(255, 103, 175, 226),
                    Color.FromArgb(255, 48, 82, 110), Color.FromArgb(255, 246, 246, 244),
                    Color.FromArgb(255, 196, 198, 201), Color.FromArgb(255, 157, 200, 232),
                    Color.FromArgb(255, 112, 214, 157), Color.FromArgb(255, 255, 122, 122)),
                "高对比度" => (
                    Color.Black, Color.FromArgb(255, 18, 18, 18), Color.FromArgb(255, 40, 40, 40),
                    Color.Black, Color.White, Color.FromArgb(255, 255, 224, 0),
                    Color.FromArgb(255, 120, 80, 0), Color.White, Color.FromArgb(255, 235, 235, 235),
                    Color.FromArgb(255, 255, 224, 0), Color.FromArgb(255, 80, 255, 130),
                    Color.FromArgb(255, 255, 90, 90)),
                "海蓝清新" => (
                    Color.FromArgb(255, 235, 247, 250), Color.White, Color.FromArgb(255, 211, 235, 241),
                    Color.FromArgb(255, 247, 252, 253), Color.FromArgb(255, 115, 168, 180),
                    Color.FromArgb(255, 28, 133, 164), Color.FromArgb(255, 185, 226, 235),
                    Color.FromArgb(255, 24, 52, 62), Color.FromArgb(255, 75, 112, 123),
                    Color.FromArgb(255, 27, 117, 143), Color.FromArgb(255, 25, 150, 105),
                    Color.FromArgb(255, 198, 65, 72)),
                _ => (
                    Color.FromArgb(255, 16, 21, 28), Color.FromArgb(255, 27, 35, 45),
                    Color.FromArgb(255, 38, 49, 63), Color.FromArgb(255, 18, 26, 34),
                    Color.FromArgb(255, 98, 119, 142), Color.FromArgb(255, 76, 176, 238),
                    Color.FromArgb(255, 33, 82, 116), Color.FromArgb(255, 235, 240, 246),
                    Color.FromArgb(255, 155, 170, 188), Color.FromArgb(255, 184, 202, 220),
                    Color.FromArgb(255, 104, 214, 147), Color.FromArgb(255, 255, 102, 112)),
            };
        if (theme is not null && LoadThemes().TryGetValue(theme, out var colors) && colors is { Length: 12 })
            (Window, Panel, PanelHeader, Input, Border, Accent, AccentSoft, Text, MutedText, Icon, Success, Error) =
                (Color.FromArgb(colors[0]), Color.FromArgb(colors[1]), Color.FromArgb(colors[2]), Color.FromArgb(colors[3]),
                 Color.FromArgb(colors[4]), Color.FromArgb(colors[5]), Color.FromArgb(colors[6]), Color.FromArgb(colors[7]),
                 Color.FromArgb(colors[8]), Color.FromArgb(colors[9]), Color.FromArgb(colors[10]), Color.FromArgb(colors[11]));
    }
}
