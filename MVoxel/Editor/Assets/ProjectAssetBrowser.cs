using System.Drawing;
using System.Diagnostics;
using ImGuiNET;
using MVoxel.Project;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 刻意保持小巧的数据驱动两栏资源浏览器，对齐 Unity Project 窗口的有用部分：
/// 左侧目录树、右侧按类型呈现的资产卡片，配全局搜索与类型筛选
/// 目录遍历拒绝符号链接与 Assets 根之外的路径
/// </summary>
internal sealed class ProjectAssetBrowser
{
    private string _selectedFolder = string.Empty;
    private string _selectedAsset = string.Empty;
    private string _searchText = string.Empty;
    private ProjectAssetKind? _filter;
    private bool _searchAll = true;
    private float _treeWidth = 216F;
    private float _tileSize = 76F;

    internal string SelectedAssetPath => _selectedAsset;

    /// <summary>
    /// 清空选择状态（切换项目或手动刷新时调用）
    /// </summary>
    internal void Reset()
    {
        _selectedFolder = string.Empty;
        _selectedAsset = string.Empty;
    }

    /// <summary>
    /// 绘制整个浏览器。project 为 null 时展示新项目的标准目录结构
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="reportStatus">向状态栏汇报的回调</param>
    internal void Draw(ProjectHandle? project, Action<string> reportStatus)
    {
        if (!ProjectAssetCatalog.FolderExists(project, _selectedFolder))
        {
            Reset();
        }

        DrawToolbar(project);
        ImGui.Separator();

        var bodyHeight = Math.Max(64F, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 8F);
        ImGui.BeginChild("##资源目录树", new Vector2(_treeWidth, bodyHeight), ImGuiChildFlags.Borders);
        DrawFolderTree(project);
        ImGui.EndChild();

        ImGui.SameLine(0F, 0F);
        ImGui.InvisibleButton("##资源面板分隔条", new Vector2(5F, bodyHeight));
        if (ImGui.IsItemActive())
        {
            _treeWidth = Math.Clamp(_treeWidth + ImGui.GetIO().MouseDelta.X, 150F, 360F);
        }
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        ImGui.SameLine(0F, 0F);
        ImGui.BeginChild("##资源平铺内容", new Vector2(0F, bodyHeight), ImGuiChildFlags.Borders);
        DrawGrid(project, reportStatus);
        ImGui.EndChild();

        ImGui.Separator();
        DrawFooter(project);
    }

    private void DrawToolbar(ProjectHandle? project)
    {
        ImGui.TextColored(ToColor(EditorPalette.Text), BuildBreadcrumb(project));

        const float filterWidth = 104F;
        const float scopeWidth = 52F;
        const float searchWidth = 176F;
        var left = ImGui.GetCursorPosX();
        var right = left + ImGui.GetContentRegionAvail().X;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(left + 8F, right - filterWidth - scopeWidth - searchWidth - 12F));
        if (ImGui.Button(_searchAll ? "全局" : "当前", new Vector2(scopeWidth, 0F)))
        {
            _searchAll = !_searchAll;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(filterWidth);
        if (ImGui.BeginCombo("##资源类型筛选", FilterLabel(_filter)))
        {
            if (ImGui.Selectable("全部类型", _filter is null)) _filter = null;
            foreach (var kind in ProjectAssetCatalog.FilterKinds)
            {
                if (ImGui.Selectable(ProjectAssetCatalog.KindLabel(kind), _filter == kind)) _filter = kind;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##资源搜索", "搜索资源…", ref _searchText, 256);
    }

    private void DrawFolderTree(ProjectHandle? project)
    {
        ImGui.TextDisabled(project is null ? "新项目资源目录" : "资源");
        ImGui.Separator();
        DrawFolderNode(project, string.Empty, "资源", 0);
        if (project is null)
        {
            ImGui.Dummy(new Vector2(0F, 6F));
            ImGui.TextDisabled("打开项目后显示实际文件");
        }
    }

    private void DrawFolderNode(ProjectHandle? project, string relativePath, string displayName, int depth)
    {
        var children = ProjectAssetCatalog.Folders(project, relativePath);
        var isRoot = relativePath.Length == 0;
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (isRoot || depth < 2) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (_selectedFolder == relativePath) flags |= ImGuiTreeNodeFlags.Selected;
        if (children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        var opened = ImGui.TreeNodeEx($"{displayName}##资源树-{relativePath}", flags);
        if (ImGui.IsItemClicked())
        {
            _selectedFolder = relativePath;
            _selectedAsset = string.Empty;
        }

        if (children.Count == 0 || !opened) return;
        foreach (var child in children)
        {
            DrawFolderNode(project, child.RelativePath, child.DisplayName, depth + 1);
        }

        ImGui.TreePop();
    }

    private void DrawGrid(ProjectHandle? project, Action<string> reportStatus)
    {
        var entries = CollectEntries(project);
        if (entries.Count == 0)
        {
            ImGui.Dummy(new Vector2(0F, 14F));
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(_searchText)
                ? "此文件夹暂时没有资源。"
                : "没有找到符合条件的资源。");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        var tileWidth = Math.Max(80F, _tileSize + 28F);
        var tileHeight = _tileSize + 46F;
        var columns = Math.Max(1, (int)((available.X + 6F) / (tileWidth + 6F)));
        var draw = ImGui.GetWindowDrawList();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (index % columns != 0) ImGui.SameLine(0F, 6F);
            ImGui.PushID(entry.RelativePath);
            var selected = _selectedAsset == entry.RelativePath;
            if (ImGui.Selectable("##资源平铺项", selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(tileWidth, tileHeight)))
            {
                _selectedAsset = entry.RelativePath;
                reportStatus($"已选择{ProjectAssetCatalog.KindLabel(entry.Kind)}：{entry.DisplayName}");
            }

            if (entry.IsFolder && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _selectedFolder = entry.RelativePath;
                _selectedAsset = string.Empty;
                reportStatus($"已打开文件夹：{entry.DisplayName}");
            }

            if (ImGui.BeginPopupContextItem("##资源右键菜单"))
            {
                _selectedAsset = entry.RelativePath;
                if (ImGui.MenuItem("打开"))
                {
                    if (entry.IsFolder)
                    {
                        _selectedFolder = entry.RelativePath;
                        _selectedAsset = string.Empty;
                    }
                    else
                    {
                        OpenWithShell(project, entry.RelativePath, reportStatus);
                    }
                }
                if (ImGui.MenuItem("在资源管理器中打开"))
                    RevealInExplorer(project, entry.RelativePath, reportStatus);
                if (ImGui.MenuItem("复制相对路径"))
                {
                    ImGui.SetClipboardText(entry.RelativePath);
                    reportStatus($"已复制相对路径：{entry.RelativePath}");
                }
                if (ImGui.MenuItem("复制绝对路径"))
                {
                    var absolutePath = ResolveAssetPath(project, entry.RelativePath);
                    if (absolutePath is not null)
                    {
                        ImGui.SetClipboardText(absolutePath);
                        reportStatus($"已复制绝对路径：{absolutePath}");
                    }
                }
                ImGui.EndPopup();
            }

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();
            if (selected || hovered)
            {
                var background = selected ? EditorPalette.AccentSoft : EditorPalette.PanelHeader;
                draw.AddRectFilled(min, max, ToPackedColor(background), 4F);
                if (selected) draw.AddRect(min, max, ToPackedColor(EditorPalette.Accent), 4F, ImDrawFlags.None, 1F);
            }

            var iconSize = Math.Min(_tileSize, tileWidth - 24F);
            var iconMin = new Vector2(min.X + (tileWidth - iconSize) / 2F, min.Y + 7F);
            AssetIconPainter.Draw(draw, entry.Kind, iconMin, iconSize, selected);
            DrawCenteredText(draw, entry.DisplayName, min.X, tileWidth, iconMin.Y + iconSize + 4F, EditorPalette.Text);
            DrawCenteredText(draw, ProjectAssetCatalog.KindLabel(entry.Kind), min.X, tileWidth,
                iconMin.Y + iconSize + ImGui.GetTextLineHeight() + 5F, EditorPalette.MutedText);
            ImGui.PopID();
        }
    }

    private List<ProjectAssetEntry> CollectEntries(ProjectHandle? project)
    {
        var source = string.IsNullOrWhiteSpace(_searchText)
            ? ProjectAssetCatalog.Children(project, _selectedFolder)
            : ProjectAssetCatalog.Search(project, _searchText, _searchAll ? null : _selectedFolder);
        return source
            .Where(entry => _filter is null || entry.Kind == _filter)
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void DrawFooter(ProjectHandle? project)
    {
        var description = project is null
            ? "未打开项目：显示新项目的标准资源目录"
            : _selectedAsset.Length == 0
                ? $"当前文件夹：{ProjectAssetCatalog.DisplayPath(_selectedFolder)}"
                : $"已选择：{ProjectAssetCatalog.DisplayPath(_selectedAsset)}";
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.TextDisabled(description);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - 176F));
        ImGui.TextDisabled("图标大小");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110F);
        ImGui.SliderFloat("##资源图标大小", ref _tileSize, 48F, 112F, "%.0f");
    }

    private string BuildBreadcrumb(ProjectHandle? project)
    {
        var root = project is null ? "资源（未打开项目）" : "资源";
        var path = ProjectAssetCatalog.DisplayPath(_selectedFolder);
        return path == "资源" ? root : $"{root}  /  {path["资源".Length..].TrimStart(' ', '/', '／')}";
    }

    private static string FilterLabel(ProjectAssetKind? kind) => kind is null ? "筛选：全部" : ProjectAssetCatalog.KindLabel(kind.Value);

    private static void OpenWithShell(ProjectHandle? project, string relativePath, Action<string> reportStatus)
    {
        var path = ResolveAssetPath(project, relativePath);
        if (path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { reportStatus($"打开资源失败：{exception.Message}"); }
    }

    private static void RevealInExplorer(ProjectHandle? project, string relativePath, Action<string> reportStatus)
    {
        var path = ResolveAssetPath(project, relativePath);
        if (path is null) return;
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception) { reportStatus($"打开资源管理器失败：{exception.Message}"); }
    }

    private static string? ResolveAssetPath(ProjectHandle? project, string relativePath)
    {
        if (project is null || Path.IsPathRooted(relativePath)) return null;
        var root = Path.GetFullPath(project.AssetsPath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path : null;
    }

    private static void DrawCenteredText(ImDrawListPtr draw, string text, float x, float width, float y, Color color)
    {
        var truncated = Truncate(text, width - 8F);
        var textSize = ImGui.CalcTextSize(truncated);
        draw.AddText(new Vector2(x + Math.Max(4F, (width - textSize.X) / 2F), y), ToPackedColor(color), truncated);
    }

    private static string Truncate(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        const string suffix = "…";
        var length = value.Length;
        while (length > 1 && ImGui.CalcTextSize(value[..length] + suffix).X > width) length--;
        return value[..length] + suffix;
    }

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}

/// <summary>
/// 资产类型，决定图标、筛选与中文标签
/// </summary>
internal enum ProjectAssetKind
{
    /// <summary>文件夹</summary>
    Folder,
    /// <summary>场景 .mvox</summary>
    Scene,
    /// <summary>项目内部体素数据</summary>
    VoxelModel,
    /// <summary>预制件 .prefab</summary>
    Prefab,
    /// <summary>材质 .material</summary>
    Material,
    /// <summary>调色板 .palette</summary>
    Palette,
    /// <summary>环境</summary>
    Environment,
    /// <summary>渲染预设</summary>
    RenderPreset,
    /// <summary>生成脚本</summary>
    Generator,
    /// <summary>代码脚本</summary>
    Script,
    /// <summary>文档</summary>
    Document,
    /// <summary>PNG 图片</summary>
    Image,
    /// <summary>其他</summary>
    Unknown,
}

/// <summary>
/// 浏览器中的一个资产条目
/// </summary>
/// <param name="RelativePath">相对 Assets 的路径（正斜杠）</param>
/// <param name="DisplayName">去扩展名的显示名</param>
/// <param name="Kind">资产类型</param>
/// <param name="IsFolder">是否为目录</param>
internal sealed record ProjectAssetEntry(string RelativePath, string DisplayName, ProjectAssetKind Kind, bool IsFolder);

/// <summary>
/// 资产目录扫描的唯一入口：所有枚举、搜索与分类都经过这里，
/// 越界路径与重解析点（符号链接）在结果集筛选阶段排除
/// </summary>
internal static class ProjectAssetCatalog
{
    /// <summary>
    /// 筛选下拉框列出的全部类型
    /// </summary>
    internal static readonly ProjectAssetKind[] FilterKinds =
    {
        ProjectAssetKind.Folder,
        ProjectAssetKind.Scene,
        ProjectAssetKind.VoxelModel,
        ProjectAssetKind.Prefab,
        ProjectAssetKind.Material,
        ProjectAssetKind.Palette,
        ProjectAssetKind.Environment,
        ProjectAssetKind.RenderPreset,
        ProjectAssetKind.Generator,
        ProjectAssetKind.Script,
        ProjectAssetKind.Document,
        ProjectAssetKind.Image,
        ProjectAssetKind.Unknown,
    };

    /// <summary>
    /// 目录是否仍然存在（未打开项目时按固定结构判断）
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>是否存在</returns>
    internal static bool FolderExists(ProjectHandle? project, string relativePath)
    {
        if (project is null) return relativePath.Length == 0 || ProjectLayout.AssetDirectories.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
        var path = ResolveFolder(project, relativePath);
        return path is not null && Directory.Exists(path);
    }

    /// <summary>
    /// 列出子目录
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>子目录条目序列</returns>
    internal static IReadOnlyList<ProjectAssetEntry> Folders(ProjectHandle? project, string relativePath)
    {
        if (project is null)
        {
            return relativePath.Length == 0
                ? ProjectLayout.AssetDirectories.Select(directory => Folder(directory, directory)).ToArray()
                : Array.Empty<ProjectAssetEntry>();
        }

        var folder = ResolveFolder(project, relativePath);
        if (folder is null || !Directory.Exists(folder)) return Array.Empty<ProjectAssetEntry>();
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(path => !IsReparsePoint(path))
                .Select(path => Folder(ToRelative(project, path), Path.GetFileName(path)))
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (IOException) { return Array.Empty<ProjectAssetEntry>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<ProjectAssetEntry>(); }
    }

    /// <summary>
    /// 列出某目录的直接子项
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>子项条目序列</returns>
    internal static IReadOnlyList<ProjectAssetEntry> Children(ProjectHandle? project, string relativePath)
    {
        if (project is null) return Folders(null, relativePath);
        var folder = ResolveFolder(project, relativePath);
        if (folder is null || !Directory.Exists(folder)) return Array.Empty<ProjectAssetEntry>();
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(path => !IsReparsePoint(path))
                .Select(path => ToEntry(project, path))
                .Where(entry => entry is not null)
                .Cast<ProjectAssetEntry>()
                .ToArray();
        }
        catch (IOException) { return Array.Empty<ProjectAssetEntry>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<ProjectAssetEntry>(); }
    }

    /// <summary>
    /// 按名称搜索资产，scopeFolder 为 null 时递归整个 Assets
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="query">搜索词</param>
    /// <param name="scopeFolder">限定范围的目录相对路径，null 为全局</param>
    /// <returns>匹配的条目序列</returns>
    internal static IReadOnlyList<ProjectAssetEntry> Search(ProjectHandle? project, string query, string? scopeFolder)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0) return Children(project, scopeFolder ?? string.Empty);
        if (project is null)
        {
            return Folders(null, string.Empty)
                .Where(entry => entry.DisplayName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        }

        var folder = ResolveFolder(project, scopeFolder ?? string.Empty);
        if (folder is null || !Directory.Exists(folder)) return Array.Empty<ProjectAssetEntry>();
        try
        {
            return Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Where(path => !IsReparsePoint(path))
                .Select(path => ToEntry(project, path))
                .Where(entry => entry is not null && entry.DisplayName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase))
                .Cast<ProjectAssetEntry>()
                .ToArray();
        }
        catch (IOException) { return Array.Empty<ProjectAssetEntry>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<ProjectAssetEntry>(); }
    }

    /// <summary>
    /// 把相对路径转成中文面包屑显示文本
    /// </summary>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>显示文本</returns>
    internal static string DisplayPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "资源";
        return "资源 / " + string.Join(" / ", relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(DisplayFolderName));
    }

    /// <summary>
    /// 资产类型的中文标签
    /// </summary>
    /// <param name="kind">类型</param>
    /// <returns>标签文本</returns>
    internal static string KindLabel(ProjectAssetKind kind) => kind switch
    {
        ProjectAssetKind.Folder => "文件夹",
        ProjectAssetKind.Scene => "场景",
        ProjectAssetKind.VoxelModel => "体素数据",
        ProjectAssetKind.Prefab => "预制件",
        ProjectAssetKind.Material => "材质",
        ProjectAssetKind.Palette => "调色板",
        ProjectAssetKind.Environment => "环境",
        ProjectAssetKind.RenderPreset => "渲染预设",
        ProjectAssetKind.Generator => "生成器",
        ProjectAssetKind.Script => "脚本",
        ProjectAssetKind.Document => "文档",
        ProjectAssetKind.Image => "PNG 图像",
        _ => "其他文件",
    };

    /// <summary>
    /// 按扩展名分类资产，扩展名规范见 Docs/技术文档/11-编码规范.md
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="isFolder">是否为目录</param>
    /// <returns>资产类型</returns>
    internal static ProjectAssetKind Classify(string path, bool isFolder)
    {
        if (isFolder) return ProjectAssetKind.Folder;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mvox" => ProjectAssetKind.Scene,
            ".mvmodel" or ".vox" => ProjectAssetKind.VoxelModel,
            ".prefab" => ProjectAssetKind.Prefab,
            ".material" => ProjectAssetKind.Material,
            ".palette" or ".pal" => ProjectAssetKind.Palette,
            ".mvenvironment" or ".hdr" or ".exr" => ProjectAssetKind.Environment,
            ".mvrenderpreset" or ".mvrender" => ProjectAssetKind.RenderPreset,
            ".mvgenerator" => ProjectAssetKind.Generator,
            ".cs" or ".lua" => ProjectAssetKind.Script,
            ".txt" or ".md" or ".json" => ProjectAssetKind.Document,
            ".png" => ProjectAssetKind.Image,
            _ => ProjectAssetKind.Unknown,
        };
    }

    private static ProjectAssetEntry? ToEntry(ProjectHandle project, string path)
    {
        try
        {
            var isFolder = Directory.Exists(path);
            return new ProjectAssetEntry(
                ToRelative(project, path),
                isFolder ? DisplayFolderName(Path.GetFileName(path)) : Path.GetFileNameWithoutExtension(path),
                Classify(path, isFolder),
                isFolder);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static ProjectAssetEntry Folder(string relativePath, string name) =>
        new(relativePath.Replace('\\', '/'), DisplayFolderName(name), ProjectAssetKind.Folder, true);

    private static string? ResolveFolder(ProjectHandle project, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.AssetsPath));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static string ToRelative(ProjectHandle project, string path) =>
        Path.GetRelativePath(project.AssetsPath, path).Replace('\\', '/');

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string DisplayFolderName(string name) => name switch
    {
        "Scenes" => "场景",
        "Prefabs" => "预制件",
        "VoxelModels" => "内部数据",
        "Materials" => "材质",
        "Palettes" => "调色板",
        "Environments" => "环境",
        "RenderPresets" => "渲染预设",
        "Generators" => "生成器",
        _ => name,
    };
}

/// <summary>
/// 用 ImGui 绘制列表按类型程序化绘制资产图标，不引入任何图标字体或位图资产
/// </summary>
internal static class AssetIconPainter
{
    /// <summary>
    /// 在指定矩形内绘制某个类型的资产图标
    /// </summary>
    /// <param name="draw">目标绘制列表</param>
    /// <param name="kind">资产类型</param>
    /// <param name="min">图标左上角</param>
    /// <param name="size">图标边长</param>
    /// <param name="selected">是否处于选中态</param>
    internal static void Draw(ImDrawListPtr draw, ProjectAssetKind kind, Vector2 min, float size, bool selected)
    {
        var main = IconColor(kind, selected);
        var shade = Color.FromArgb(main.A, Math.Max(0, main.R - 42), Math.Max(0, main.G - 42), Math.Max(0, main.B - 42));
        var light = Color.FromArgb(main.A, Math.Min(255, main.R + 36), Math.Min(255, main.G + 36), Math.Min(255, main.B + 36));
        var outline = Color.FromArgb(220, 12, 18, 25);
        Func<Vector2, Vector2> p = value => min + new Vector2(size * value.X, size * value.Y);

        switch (kind)
        {
            case ProjectAssetKind.Folder:
                draw.AddRectFilled(p(new Vector2(.08F, .27F)), p(new Vector2(.57F, .47F)), ToPackedColor(light), 3F);
                draw.AddRectFilled(p(new Vector2(.06F, .38F)), p(new Vector2(.94F, .88F)), ToPackedColor(main), 4F);
                draw.AddRect(p(new Vector2(.06F, .38F)), p(new Vector2(.94F, .88F)), ToPackedColor(outline), 4F, ImDrawFlags.None, 1F);
                break;
            case ProjectAssetKind.VoxelModel:
            case ProjectAssetKind.Prefab:
                DrawCube(draw, p, main, shade, light, outline, kind == ProjectAssetKind.Prefab);
                break;
            case ProjectAssetKind.Scene:
                draw.AddRectFilled(p(new Vector2(.1F, .12F)), p(new Vector2(.9F, .86F)), ToPackedColor(shade), 3F);
                draw.AddRect(p(new Vector2(.1F, .12F)), p(new Vector2(.9F, .86F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
                draw.AddLine(p(new Vector2(.18F, .72F)), p(new Vector2(.82F, .72F)), ToPackedColor(light), 2F);
                draw.AddLine(p(new Vector2(.34F, .72F)), p(new Vector2(.62F, .30F)), ToPackedColor(main), 2F);
                draw.AddLine(p(new Vector2(.62F, .30F)), p(new Vector2(.80F, .72F)), ToPackedColor(main), 2F);
                break;
            case ProjectAssetKind.Material:
            case ProjectAssetKind.Palette:
                draw.AddCircleFilled(p(new Vector2(.5F, .5F)), size * .34F, ToPackedColor(main), 20);
                draw.AddCircle(p(new Vector2(.5F, .5F)), size * .34F, ToPackedColor(outline), 20, 1F);
                draw.AddCircleFilled(p(new Vector2(.40F, .4F)), size * .09F, ToPackedColor(light), 12);
                if (kind == ProjectAssetKind.Palette)
                {
                    draw.AddCircleFilled(p(new Vector2(.62F, .43F)), size * .08F, ToPackedColor(Color.FromArgb(255, 238, 146, 92)), 12);
                    draw.AddCircleFilled(p(new Vector2(.56F, .62F)), size * .08F, ToPackedColor(Color.FromArgb(255, 110, 205, 151)), 12);
                }
                break;
            case ProjectAssetKind.Environment:
                draw.AddCircleFilled(p(new Vector2(.5F, .42F)), size * .23F, ToPackedColor(main), 16);
                draw.AddCircleFilled(p(new Vector2(.37F, .58F)), size * .18F, ToPackedColor(light), 16);
                draw.AddCircleFilled(p(new Vector2(.60F, .59F)), size * .20F, ToPackedColor(light), 16);
                draw.AddLine(p(new Vector2(.18F, .75F)), p(new Vector2(.82F, .75F)), ToPackedColor(outline), 1.3F);
                break;
            case ProjectAssetKind.RenderPreset:
                draw.AddRectFilled(p(new Vector2(.14F, .16F)), p(new Vector2(.86F, .84F)), ToPackedColor(shade), 3F);
                draw.AddRect(p(new Vector2(.14F, .16F)), p(new Vector2(.86F, .84F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
                for (var row = 0; row < 3; row++)
                {
                    var y = .32F + row * .18F;
                    draw.AddLine(p(new Vector2(.28F, y)), p(new Vector2(.72F, y)), ToPackedColor(light), 2F);
                    draw.AddCircleFilled(p(new Vector2(.42F + row * .11F, y)), size * .055F, ToPackedColor(main), 10);
                }
                break;
            case ProjectAssetKind.Generator:
            case ProjectAssetKind.Script:
                DrawDocument(draw, p, main, shade, outline, kind == ProjectAssetKind.Generator);
                break;
            case ProjectAssetKind.Image:
                DrawDocument(draw, p, Color.FromArgb(255, 220, 74, 74), shade, outline, false);
                draw.AddText(p(new Vector2(.29F, .43F)), ToPackedColor(Color.White), "PNG");
                break;
            default:
                DrawDocument(draw, p, main, shade, outline, false);
                break;
        }
    }

    private static void DrawCube(ImDrawListPtr draw, Func<Vector2, Vector2> p, Color main, Color shade, Color light, Color outline, bool prefab)
    {
        var top = new[] { p(new Vector2(.5F, .1F)), p(new Vector2(.86F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.14F, .3F)) };
        draw.AddConvexPolyFilled(ref top[0], top.Length, ToPackedColor(light));
        draw.AddQuadFilled(p(new Vector2(.14F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.5F, .88F)), p(new Vector2(.14F, .66F)), ToPackedColor(shade));
        draw.AddQuadFilled(p(new Vector2(.5F, .5F)), p(new Vector2(.86F, .3F)), p(new Vector2(.86F, .66F)), p(new Vector2(.5F, .88F)), ToPackedColor(main));
        draw.AddQuad(p(new Vector2(.14F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.86F, .3F)), p(new Vector2(.5F, .1F)), ToPackedColor(outline), 1F);
        draw.AddLine(p(new Vector2(.5F, .5F)), p(new Vector2(.5F, .88F)), ToPackedColor(outline), 1F);
        if (prefab) draw.AddCircleFilled(p(new Vector2(.78F, .78F)), 4F, ToPackedColor(Color.FromArgb(255, 101, 190, 255)), 10);
    }

    private static void DrawDocument(ImDrawListPtr draw, Func<Vector2, Vector2> p, Color main, Color shade, Color outline, bool generated)
    {
        draw.AddRectFilled(p(new Vector2(.2F, .1F)), p(new Vector2(.8F, .9F)), ToPackedColor(main), 3F);
        draw.AddTriangleFilled(p(new Vector2(.62F, .1F)), p(new Vector2(.8F, .1F)), p(new Vector2(.8F, .28F)), ToPackedColor(shade));
        draw.AddRect(p(new Vector2(.2F, .1F)), p(new Vector2(.8F, .9F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
        for (var row = 0; row < 3; row++)
        {
            var y = .40F + row * .14F;
            draw.AddLine(p(new Vector2(.32F, y)), p(new Vector2(.67F, y)), ToPackedColor(shade), 1.6F);
        }
        if (generated) draw.AddCircleFilled(p(new Vector2(.7F, .75F)), 4F, ToPackedColor(Color.FromArgb(255, 100, 218, 151)), 10);
    }

    private static Color IconColor(ProjectAssetKind kind, bool selected) => kind switch
    {
        ProjectAssetKind.Folder => Color.FromArgb(255, 91, 166, 224),
        ProjectAssetKind.Scene => Color.FromArgb(255, 94, 190, 174),
        ProjectAssetKind.VoxelModel => Color.FromArgb(255, 115, 163, 245),
        ProjectAssetKind.Prefab => Color.FromArgb(255, 132, 143, 246),
        ProjectAssetKind.Material => Color.FromArgb(255, 224, 158, 92),
        ProjectAssetKind.Palette => Color.FromArgb(255, 206, 122, 216),
        ProjectAssetKind.Environment => Color.FromArgb(255, 105, 180, 231),
        ProjectAssetKind.RenderPreset => Color.FromArgb(255, 223, 192, 103),
        ProjectAssetKind.Generator => Color.FromArgb(255, 99, 215, 145),
        ProjectAssetKind.Script => Color.FromArgb(255, 132, 196, 246),
        ProjectAssetKind.Document => Color.FromArgb(255, 170, 183, 201),
        ProjectAssetKind.Image => Color.FromArgb(255, 224, 86, 86),
        _ => selected ? EditorPalette.Icon : Color.FromArgb(255, 143, 157, 177),
    };

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
