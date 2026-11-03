using ImGuiNET;
using MVoxel.Core;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 布局预设与自定义布局管理：保存当前停靠布局、覆盖、删除、导入导出布局文件。
/// 布局文件使用 ImGui 的 ini 格式，保存在编辑器配置目录的 Layouts 下。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private string LayoutDirectory => Path.Combine(AppPaths.ConfigDirectory, "Layouts");
    private string LayoutSelectionPath => Path.Combine(LayoutDirectory, "current.txt");

    private string? _activeLayoutFile;
    private string _activeLayoutName = string.Empty;
    private bool _layoutNameDialogOpen;
    private string _layoutNameInput = string.Empty;

    private string LayoutFileName(string name) =>
        Path.Combine(LayoutDirectory, SanitizeLayoutName(name) + ".layout.ini");

    private static string SanitizeLayoutName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Trim());
        foreach (var character in invalid) builder.Replace(character, '_');
        return builder.Length == 0 ? "布局" : builder.ToString();
    }

    /// <summary>从配置目录读取上次选中的自定义布局名；布局本身由自动保存的 ini 恢复</summary>
    private void LoadLayoutSelection()
    {
        try
        {
            if (!File.Exists(LayoutSelectionPath)) return;
            var name = File.ReadAllText(LayoutSelectionPath).Trim();
            if (name.Length == 0) return;
            if (name.StartsWith("@builtin:", StringComparison.Ordinal))
            {
                if (Enum.TryParse<LayoutPreset>(name[9..], out var preset) && preset != LayoutPreset.Custom)
                {
                    _layoutPreset = preset;
                    // 启动恢复布局只恢复停靠结构，诊断窗口仍需由用户主动打开
                    ApplyPresetPanels(preset, openDiagnostics: false);
                }
                return;
            }
            var path = LayoutFileName(name);
            if (!File.Exists(path)) return;
            _activeLayoutName = name;
            _activeLayoutFile = path;
            _layoutPreset = LayoutPreset.Custom;
            RestoreLayoutPanels(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveLayoutSelection(string? name)
    {
        try
        {
            Directory.CreateDirectory(LayoutDirectory);
            AtomicFile.WriteText(LayoutSelectionPath, name ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"布局选择无法保存：{exception.Message}");
        }
    }

    private IReadOnlyList<(string Name, string Path)> GetCustomLayouts()
    {
        try
        {
            if (!Directory.Exists(LayoutDirectory)) return Array.Empty<(string, string)>();
            return Directory.EnumerateFiles(LayoutDirectory, "*.layout.ini")
                .Select(path => (Path: path, Name: Path.GetFileName(path)[..^".layout.ini".Length]))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => (item.Name, item.Path))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static string GetPresetDockLayout(LayoutPreset preset)
    {
        var (left, right, bottom) = preset switch
        {
            LayoutPreset.Modeling => (320, 360, 250),
            LayoutPreset.Rendering => (270, 400, 300),
            LayoutPreset.Assets => (270, 320, 380),
            LayoutPreset.Generator => (250, 440, 260),
            LayoutPreset.Analysis => (260, 400, 310),
            _ => (300, 360, 225),
        };
        var layout = DefaultDockLayout
            .Replace("SizeRef=300,615", $"SizeRef={left},615")
            .Replace("SizeRef=1140,615 Split=X", $"SizeRef={1440 - left},615 Split=X")
            .Replace("SizeRef=780,615 Selected", $"SizeRef={1440 - left - right},615 Selected")
            .Replace("SizeRef=360,615", $"SizeRef={right},615")
            .Replace("SizeRef=1440,225", $"SizeRef=1440,{bottom}");
        if (preset == LayoutPreset.Generator)
            layout += "\n[Window][生成器##生成器面板]\nDockId=0x6A3C3006,3\n";
        if (preset == LayoutPreset.Analysis)
            layout += "\n[Window][体素度量]\nDockId=0x6A3C3007,1\n[Window][性能分析器]\nDockId=0x6A3C3006,3\n";
        return layout;
    }

    private void DrawLayoutMenu()
    {
        if (!ImGui.BeginMenu("布局")) return;
        if (ImGui.MenuItem("默认布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Default))
            SelectLayout(LayoutPreset.Default);
        if (ImGui.MenuItem("建模布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Modeling))
            SelectLayout(LayoutPreset.Modeling);
        if (ImGui.MenuItem("渲染布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Rendering))
            SelectLayout(LayoutPreset.Rendering);
        if (ImGui.MenuItem("资产布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Assets))
            SelectLayout(LayoutPreset.Assets);
        if (ImGui.MenuItem("生成器布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Generator))
            SelectLayout(LayoutPreset.Generator);
        if (ImGui.MenuItem("分析布局", null, _activeLayoutFile is null && _layoutPreset == LayoutPreset.Analysis))
            SelectLayout(LayoutPreset.Analysis);

        var layouts = GetCustomLayouts();
        if (layouts.Count > 0)
        {
            ImGui.Separator();
            foreach (var (name, path) in layouts)
            {
                if (ImGui.MenuItem(name, null, string.Equals(_activeLayoutFile, path, StringComparison.OrdinalIgnoreCase)))
                    ApplyCustomLayout(path, name);
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem("保存当前布局为…"))
        {
            _layoutNameInput = $"自定义布局 {layouts.Count + 1}";
            _layoutNameDialogOpen = true;
        }

        if (!string.IsNullOrEmpty(_activeLayoutFile))
        {
            if (ImGui.MenuItem("覆盖当前布局")) SaveCurrentLayoutTo(_activeLayoutFile);
            if (ImGui.MenuItem("删除当前布局"))
            {
                DeleteActiveLayout();
                ImGui.EndMenu();
                return;
            }
        }

        if (ImGui.MenuItem("导入布局…")) ImportLayout();
        if (ImGui.MenuItem("导出当前布局…")) ExportLayout();
        if (ImGui.MenuItem("重置当前布局")) ResetCurrentLayout();
        ImGui.Separator();
        if (ImGui.MenuItem(_editPanelLayout ? "完成面板布局编辑" : "编辑面板布局"))
        {
            _editPanelLayout = !_editPanelLayout;
            SetStatus(_editPanelLayout ? "面板布局编辑已开启：可拖拽标题栏进行停靠" : "面板布局编辑已完成");
        }

        ImGui.EndMenu();
    }

    private void DrawLayoutNameDialog()
    {
        if (!_layoutNameDialogOpen) return;
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(display / 2F, ImGuiCond.Appearing, new Vector2(0.5F, 0.5F));
        ImGui.SetNextWindowSize(new Vector2(380F, 0F), ImGuiCond.Appearing);
        var open = true;
        if (ImGui.Begin("保存布局", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("把当前窗口停靠、位置和尺寸保存为自定义布局。");
            ImGui.SetNextItemWidth(-1F);
            ImGui.InputTextWithHint("##布局名称", "布局名称", ref _layoutNameInput, 64);
            if (ImGui.Button("保存", new Vector2(120F, 0F)))
            {
                var name = SanitizeLayoutName(_layoutNameInput);
                var path = LayoutFileName(name);
                if (File.Exists(path))
                {
                    SetStatus($"布局“{name}”已存在，请换一个名称或使用覆盖");
                }
                else
                {
                    SaveCurrentLayoutTo(path, name);
                    _layoutNameDialogOpen = false;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(100F, 0F))) _layoutNameDialogOpen = false;
        }

        ImGui.End();
        if (!open) _layoutNameDialogOpen = false;
    }

    private void SaveCurrentLayoutTo(string path, string? name = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path, CaptureLayout());
            _activeLayoutFile = path;
            _activeLayoutName = name ?? Path.GetFileName(path)[..^".layout.ini".Length];
            _layoutPreset = LayoutPreset.Custom;
            _applyPreset = false;
            SaveLayoutSelection(_activeLayoutName);
            SetStatus($"布局已保存：{_activeLayoutName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"布局保存失败：{exception.Message}");
        }
    }

    private void ApplyCustomLayout(string path, string name)
    {
        try
        {
            var contents = File.ReadAllText(path);
            ValidateLayout(contents);
            ImGui.LoadIniSettingsFromMemory(MigrateLayoutWindowNames(contents));
            _layoutPreset = LayoutPreset.Custom;
            _applyPreset = false;
            _resetDockLayoutRequested = false;
            RestoreLayoutPanels(contents);
            _activeLayoutFile = path;
            _activeLayoutName = name;
            SaveLayoutSelection(name);
            SetStatus($"已应用布局：{name}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus($"布局读取失败：{exception.Message}");
        }
    }

    private static string MigrateLayoutWindowNames(string contents)
    {
        // 历史布局可能把单标签栏保存为隐藏；只恢复标签栏，不重置用户的停靠位置。
        contents = System.Text.RegularExpressions.Regex.Replace(contents,
            @"(?m)^([ \t]*DockNode\b[^\r\n]*?)[ \t]+HiddenTabBar=1(?=[ \t\r\n]|$)", "$1");
        contents = contents.Replace("[Window][颜色与笔刷##建模工具窗口]", "[Window][调色盘##建模工具窗口]", StringComparison.Ordinal);
        const string toolHeader = "[Window][工具##建模工具箱]";
        var sections = System.Text.RegularExpressions.Regex.Matches(contents,
            @"(?ms)^\[Window\]\[(?<name>[^\r\n]*)\]\r?\n.*?(?=^\[|\z)");
        var toolbar = sections.Cast<System.Text.RegularExpressions.Match>()
            .FirstOrDefault(section => section.Value.StartsWith(toolHeader, StringComparison.Ordinal));
        var toolbarOwnsNode = toolbar is not null && toolbar.Value.Contains("DockId=0x6A3C3008", StringComparison.OrdinalIgnoreCase) &&
            sections.Cast<System.Text.RegularExpressions.Match>().Count(section =>
                section.Value.Contains("DockId=0x6A3C3008", StringComparison.OrdinalIgnoreCase)) == 1;
        if (toolbar is not null) contents = contents.Remove(toolbar.Index, toolbar.Length);
        if (!toolbarOwnsNode) return contents;

        var parent = @"(?m)^(?<node>\s*DockNode ID=0x6A3C3003 Parent=0x6A3C3002 SizeRef=\d+,\d+) Split=Y\s*$";
        var children = @"(?m)^\s*DockNode ID=0x6A3C300[89] Parent=0x6A3C3003[^\r\n]*\r?\n";
        if (!System.Text.RegularExpressions.Regex.IsMatch(contents, parent) ||
            System.Text.RegularExpressions.Regex.Matches(contents, children).Count != 2) return contents;
        contents = System.Text.RegularExpressions.Regex.Replace(contents, parent, "${node}");
        contents = System.Text.RegularExpressions.Regex.Replace(contents, children, "");
        return contents.Replace("DockId=0x6A3C3009,", "DockId=0x6A3C3003,", StringComparison.OrdinalIgnoreCase);
    }

    private string CaptureLayout()
    {
        var panels = string.Concat(_showHierarchy ? "1" : "0", _showModelingTools ? "1" : "0",
            _showInspector ? "1" : "0", _showProjectAssets ? "1" : "0", _showGeneratorPanel ? "1" : "0",
            _showRenderConfig ? "1" : "0");
        return $";MVoxelPanels={panels}\n" + ImGui.SaveIniSettingsToMemory();
    }

    private void RestoreLayoutPanels(string contents)
    {
        const string marker = ";MVoxelPanels=";
        var line = contents.Split('\n').FirstOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));
        if (line is null) return;
        var panels = line[marker.Length..].Trim();
        if (panels.Length is not (5 or 6) || panels.Any(value => value is not ('0' or '1'))) return;
        _showHierarchy = panels[0] == '1';
        _showModelingTools = panels[1] == '1';
        _showInspector = panels[2] == '1';
        _showProjectAssets = panels[3] == '1';
        _showGeneratorPanel = panels[4] == '1';
        _showRenderConfig = panels.Length == 6 && panels[5] == '1';
    }

    private static void ValidateLayout(string contents)
    {
        if (contents.Length > 4_000_000 || !contents.Contains("[Docking][Data]", StringComparison.Ordinal) ||
            !contents.Contains("DockSpace ID=0x6A3C3001", StringComparison.Ordinal))
            throw new InvalidDataException("布局文件无效或不属于 MVoxel");
    }

    private void DeleteActiveLayout()
    {
        if (string.IsNullOrEmpty(_activeLayoutFile)) return;
        try
        {
            var name = _activeLayoutName;
            MVoxel.Core.FileEditHistory.Delete(_activeLayoutFile);
            _activeLayoutFile = null;
            _activeLayoutName = string.Empty;
            SaveLayoutSelection(null);
            SelectLayout(LayoutPreset.Default);
            SetStatus($"已删除布局：{name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"布局删除失败：{exception.Message}");
        }
    }

    private void ImportLayout()
    {
        var source = NativeDialogs.PickFile("导入 MVoxel 布局", "布局文件\0*.ini;*.layout.ini\0全部文件\0*.*\0");
        if (string.IsNullOrWhiteSpace(source)) return;
        try
        {
            Directory.CreateDirectory(LayoutDirectory);
            var baseName = SanitizeLayoutName(Path.GetFileNameWithoutExtension(source));
            if (baseName.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".layout".Length];
            var name = baseName;
            var path = LayoutFileName(name);
            for (var suffix = 2; File.Exists(path); suffix++)
            {
                name = $"{baseName} {suffix}";
                path = LayoutFileName(name);
            }

            var contents = File.ReadAllText(source);
            ValidateLayout(contents);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) writer.Write(contents);
            ApplyCustomLayout(path, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"布局导入失败：{exception.Message}");
        }
    }

    private void ExportLayout()
    {
        var suggested = string.IsNullOrWhiteSpace(_activeLayoutName) ? "MVoxel 布局" : _activeLayoutName;
        var target = NativeDialogs.SaveFile("导出 MVoxel 布局", "布局文件\0*.ini\0全部文件\0*.*\0",
            "ini", suggested + ".ini");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            AtomicFile.WriteText(target, CaptureLayout());
            SetStatus($"布局已导出：{target}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"布局导出失败：{exception.Message}");
        }
    }

    private void ResetCurrentLayout()
    {
        if (!string.IsNullOrEmpty(_activeLayoutFile))
            ApplyCustomLayout(_activeLayoutFile, _activeLayoutName);
        else
            SelectLayout(_layoutPreset);
    }
}
