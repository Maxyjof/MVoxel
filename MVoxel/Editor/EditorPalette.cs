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
    internal const string DefaultThemeName = "柔和浅色";

    /// <summary>
    /// 可用主题名称
    /// </summary>
    internal static readonly string[] BuiltInThemeNames =
    {
        DefaultThemeName,
        "暗色",
        "石墨灰",
        "高对比度",
        "海蓝清新",
    };

    private static Dictionary<string, int[]>? _customThemes;
    // 状态底色保持正文对比度，强调色只用于细线、图标和焦点。
    internal static Color Hover => Blend(Panel, AccentSoft, .65F);
    internal static Color Pressed => Blend(AccentSoft, Accent, .22F);
    internal static Color SelectedHover => Blend(AccentSoft, Accent, .12F);
    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(255,
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
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
        var builtIn = IsBuiltIn(theme ?? string.Empty) ? theme : DefaultThemeName;
        (Window, Panel, PanelHeader, Input, Border, Accent, AccentSoft, Text, MutedText, Icon, Success, Error) =
            builtIn switch
            {
                "柔和浅色" => (
                    Color.FromArgb(unchecked((int)0xFFE9EDF2)), Color.FromArgb(unchecked((int)0xFFF7F9FC)), Color.FromArgb(unchecked((int)0xFFDCE3EC)), Color.FromArgb(unchecked((int)0xFFFFFFFF)), Color.FromArgb(unchecked((int)0xFFA5B2C3)), Color.FromArgb(unchecked((int)0xFF245EB8)), Color.FromArgb(unchecked((int)0xFFD4E3FA)), Color.FromArgb(unchecked((int)0xFF202C3C)), Color.FromArgb(unchecked((int)0xFF596A80)), Color.FromArgb(unchecked((int)0xFF426389)), Color.FromArgb(unchecked((int)0xFF23804F)), Color.FromArgb(unchecked((int)0xFFB83B4A))),
                "暗色" => (
                    Color.FromArgb(unchecked((int)0xFF181D26)), Color.FromArgb(unchecked((int)0xFF222A36)), Color.FromArgb(unchecked((int)0xFF2C3746)), Color.FromArgb(unchecked((int)0xFF141B25)), Color.FromArgb(unchecked((int)0xFF516179)), Color.FromArgb(unchecked((int)0xFF7CAFFF)), Color.FromArgb(unchecked((int)0xFF30496B)), Color.FromArgb(unchecked((int)0xFFE8EEF7)), Color.FromArgb(unchecked((int)0xFFA9B8CD)), Color.FromArgb(unchecked((int)0xFFA6C5EF)), Color.FromArgb(unchecked((int)0xFF75D6A5)), Color.FromArgb(unchecked((int)0xFFFF939C))),
                "石墨灰" => (
                    Color.FromArgb(unchecked((int)0xFF202122)), Color.FromArgb(unchecked((int)0xFF2B2D30)), Color.FromArgb(unchecked((int)0xFF373A3E)), Color.FromArgb(unchecked((int)0xFF191B1E)), Color.FromArgb(unchecked((int)0xFF62666F)), Color.FromArgb(unchecked((int)0xFFBAA7F5)), Color.FromArgb(unchecked((int)0xFF49405E)), Color.FromArgb(unchecked((int)0xFFF0EFF4)), Color.FromArgb(unchecked((int)0xFFB7B4C0)), Color.FromArgb(unchecked((int)0xFFC6BEDA)), Color.FromArgb(unchecked((int)0xFF8AD2AF)), Color.FromArgb(unchecked((int)0xFFF599A5))),
                "高对比度" => (
                    Color.FromArgb(unchecked((int)0xFF080B10)), Color.FromArgb(unchecked((int)0xFF10151C)), Color.FromArgb(unchecked((int)0xFF202B39)), Color.FromArgb(unchecked((int)0xFF000000)), Color.FromArgb(unchecked((int)0xFFBAC9DA)), Color.FromArgb(unchecked((int)0xFFFFD65A)), Color.FromArgb(unchecked((int)0xFF3D3540)), Color.FromArgb(unchecked((int)0xFFFFFFFF)), Color.FromArgb(unchecked((int)0xFFD3DDE9)), Color.FromArgb(unchecked((int)0xFFFFE39A)), Color.FromArgb(unchecked((int)0xFF89F5B4)), Color.FromArgb(unchecked((int)0xFFFFABB3))),
                "海蓝清新" => (
                    Color.FromArgb(unchecked((int)0xFFE5F0F2)), Color.FromArgb(unchecked((int)0xFFF6FBFC)), Color.FromArgb(unchecked((int)0xFFD2E5E9)), Color.FromArgb(unchecked((int)0xFFFFFFFF)), Color.FromArgb(unchecked((int)0xFF8FAFB7)), Color.FromArgb(unchecked((int)0xFF066B85)), Color.FromArgb(unchecked((int)0xFFC5E6EC)), Color.FromArgb(unchecked((int)0xFF17373F)), Color.FromArgb(unchecked((int)0xFF4D6C75)), Color.FromArgb(unchecked((int)0xFF286C7D)), Color.FromArgb(unchecked((int)0xFF187E60)), Color.FromArgb(unchecked((int)0xFFB34251))),
                _ => throw new ArgumentException("未知内置主题"),
            };
        if (theme is not null && LoadThemes().TryGetValue(theme, out var colors) && colors is { Length: 12 })
            (Window, Panel, PanelHeader, Input, Border, Accent, AccentSoft, Text, MutedText, Icon, Success, Error) =
                (Color.FromArgb(colors[0]), Color.FromArgb(colors[1]), Color.FromArgb(colors[2]), Color.FromArgb(colors[3]),
                 Color.FromArgb(colors[4]), Color.FromArgb(colors[5]), Color.FromArgb(colors[6]), Color.FromArgb(colors[7]),
                 Color.FromArgb(colors[8]), Color.FromArgb(colors[9]), Color.FromArgb(colors[10]), Color.FromArgb(colors[11]));
    }
}
