using MVoxel.Project;

namespace MVoxel.Editor;

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
    /// <summary>预制体 .prefab</summary>
    Prefab,
    /// <summary>材质 .material</summary>
    Material,
    /// <summary>调色盘 .palette</summary>
    Palette,
    /// <summary>环境</summary>
    Environment,
    /// <summary>渲染预设</summary>
    RenderPreset,
    /// <summary>时间轴 .mvtimeline.json</summary>
    Timeline,
    /// <summary>动画片段 .mvanim.json</summary>
    AnimationClip,
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
/// 资产文件发生变化时通知编辑器刷新关联状态。
/// </summary>
internal sealed record ProjectAssetChange(string OldRelativePath, string? NewRelativePath);

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
        ProjectAssetKind.Timeline,
        ProjectAssetKind.AnimationClip,
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
        if (string.IsNullOrWhiteSpace(relativePath)) return "资产";
        return "资产 / " + string.Join(" / ", relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
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
        ProjectAssetKind.Prefab => "预制体",
        ProjectAssetKind.Material => "材质",
        ProjectAssetKind.Palette => "调色盘",
        ProjectAssetKind.Environment => "环境",
        ProjectAssetKind.RenderPreset => "渲染预设",
        ProjectAssetKind.Timeline => "时间轴",
        ProjectAssetKind.AnimationClip => "动画片段",
        ProjectAssetKind.Generator => "生成器",
        ProjectAssetKind.Script => "脚本",
        ProjectAssetKind.Document => "文档",
        ProjectAssetKind.Image => "PNG 图像",
        _ => "其他文件",
    };

    /// <summary>
    /// 空分类仍保留为稳定的项目结构，但在界面中说明它的真实用途，
    /// 避免用户把标准目录误认为未完成的占位功能。
    /// </summary>
    internal static string FolderDescription(string relativePath) => relativePath.Trim('/').ToLowerInvariant() switch
    {
        "scenes" => "保存 .mvox 场景：对象层级、变换、相机、灯光与模型引用。",
        "prefabs" => "保存 .prefab 预制体：可复用的对象子树和变体。通过层级右键创建。",
        "worlds" => "保存 .mvmodel/.mvworld 体素数据；外部 .vox 文件也会在这里显示。",
        "materials" => "保存 .material 材质槽覆盖，供渲染管线加载。",
        "palettes" => "保存多个 .palette/.pal 调色盘；当前使用的调色盘在项目设置中指定。",
        "environments" => "放置可被检视器加载的 .hdr/.exr 环境贴图。",
        "renderpresets" => "保存 .mvrenderpreset/.mvrender 渲染参数，可在检视器中应用。",
        "generators" => "保存 .mvgenerator 生成器脚本，可在生成器面板运行。",
        "exports" => "渲染图像和外部导出结果目录，不作为场景输入资产。",
        _ => string.Empty,
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
        if (path.EndsWith(".mvtimeline.json", StringComparison.OrdinalIgnoreCase)) return ProjectAssetKind.Timeline;
        if (path.EndsWith(".mvanim.json", StringComparison.OrdinalIgnoreCase)) return ProjectAssetKind.AnimationClip;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mvtimeline.json" => ProjectAssetKind.Timeline,
            ".mvox" => ProjectAssetKind.Scene,
            ".mvmodel" or ".mvworld" or ".vox" or ".qb" or ".schem" => ProjectAssetKind.VoxelModel,
            ".prefab" => ProjectAssetKind.Prefab,
            ".material" => ProjectAssetKind.Material,
            ".palette" or ".pal" => ProjectAssetKind.Palette,
            // 环境资产使用实际可解码的 Radiance HDR / OpenEXR 文件；
            // 不再把不存在解析器的自定义 .mvenvironment 假装成可用格式。
            ".hdr" or ".exr" => ProjectAssetKind.Environment,
            ".mvrenderpreset" or ".mvrender" => ProjectAssetKind.RenderPreset,
            ".mvgenerator" => ProjectAssetKind.Generator,
            ".cs" or ".lua" => ProjectAssetKind.Script,
            ".txt" or ".md" or ".json" => ProjectAssetKind.Document,
            ".png" => ProjectAssetKind.Image,
            _ => ProjectAssetKind.Unknown,
        };
    }

    private static string DisplayAssetFileName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var suffix in new[] { ".mvtimeline.json", ".mvanim.json" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static ProjectAssetEntry? ToEntry(ProjectHandle project, string path)
    {
        try
        {
            var isFolder = Directory.Exists(path);
            return new ProjectAssetEntry(
                ToRelative(project, path),
                isFolder ? DisplayFolderName(Path.GetFileName(path)) : DisplayAssetFileName(path),
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
        "Prefabs" => "预制体",
        "Worlds" => "体素数据",
        "Materials" => "材质",
        "Palettes" => "调色盘",
        "Environments" => "环境",
        "RenderPresets" => "渲染预设",
        "Generators" => "生成器",
        "Exports" => "导出",
        _ => name,
    };
}
