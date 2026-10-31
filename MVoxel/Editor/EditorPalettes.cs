using MVoxel.Project;
using MVoxel.Voxels;
using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>
/// 调色板管理：编辑器级调色板只负责无项目时的默认值，项目级调色板是
/// Assets/Palettes 下可同时存在的独立资产集合。项目配置只记录当前使用的资产路径。
/// </summary>
public static class EditorPalettes
{
    /// <summary>
    /// 新项目默认创建的调色板文件名；它不是项目内唯一调色板。
    /// </summary>
    public const string ProjectPaletteName = "主调色板.palette";

    /// <summary>
    /// 项目设置保存的默认调色板相对路径（相对于 Assets）。
    /// </summary>
    public const string ProjectPaletteRelativePath = "Palettes/" + ProjectPaletteName;

    /// <summary>
    /// 编辑器级调色板路径，位于程序目录的 MVoxelData/Config 下
    /// </summary>
    public static string EditorPalettePath => AppPaths.ConfigPath("editor.palette");

    /// <summary>
    /// 解析当前生效的编辑器级调色板：文件缺失或损坏时落回内置默认色板并顺手修复磁盘文件
    /// </summary>
    /// <returns>256 项色表</returns>
    public static Rgba32[] ResolveEditorPalette()
    {
        try
        {
            if (File.Exists(EditorPalettePath))
            {
                return PaletteFormat.Load(EditorPalettePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 损坏的编辑器色板不阻塞启动，用默认值重建
        }

        var fallback = VoxelPalette.Build();
        TrySave(EditorPalettePath, fallback);
        return fallback;
    }

    /// <summary>
    /// 解析项目级调色板路径
    /// </summary>
    /// <param name="project">已打开的项目</param>
    /// <returns>Assets/Palettes/主调色板.palette 的绝对路径</returns>
    public static string ProjectPalettePath(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ProjectPaletteFilePath(project, ProjectPaletteRelativePath);
    }

    /// <summary>
    /// 项目调色板目录。
    /// </summary>
    public static string ProjectPalettesDirectory(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(project.AssetsPath, "Palettes");
    }

    /// <summary>
    /// 列出项目资产目录内的全部调色板。
    /// </summary>
    public static IReadOnlyList<string> EnumerateProjectPalettePaths(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var directory = project.AssetsPath;
        if (!Directory.Exists(directory)) return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(directory, "*.*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                .Where(path => IsPaletteExtension(Path.GetExtension(path)))
                .Select(path => Path.GetRelativePath(project.AssetsPath, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 规范化项目设置中的调色板路径，仅接受 Assets 下的 .palette/.pal。
    /// </summary>
    public static string NormalizeProjectPalettePath(string? relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0) return ProjectPaletteRelativePath;
        if (Path.IsPathRooted(normalized) ||
            normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Contains("/..", StringComparison.Ordinal) ||
            normalized.Contains("./", StringComparison.Ordinal) ||
            !IsPaletteExtension(Path.GetExtension(normalized)))
            return ProjectPaletteRelativePath;
        return normalized;
    }

    /// <summary>
    /// 把项目调色板相对路径解析为 Assets 内的文件路径，并拒绝目录穿越。
    /// </summary>
    public static string ProjectPaletteFilePath(ProjectHandle project, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var normalized = NormalizeProjectPalettePath(relativePath);
        var assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.AssetsPath));
        var path = Path.GetFullPath(Path.Combine(assetsRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("调色板路径必须位于 Assets 下", nameof(relativePath));
        return path;
    }

    /// <summary>
    /// 打开项目时解析指定的调色板资产。指定文件不可用时按项目资产顺序回退，
    /// 最后才用编辑器级默认值创建默认项目调色板。
    /// </summary>
    /// <param name="project">正在打开的项目</param>
    /// <returns>项目生效的 256 项色表</returns>
    public static Rgba32[] ResolveProjectPalette(ProjectHandle project, string? relativePath = null)
    {
        var requested = NormalizeProjectPalettePath(relativePath);
        var candidates = new[] { requested }
            .Concat(EnumerateProjectPalettePaths(project))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var path = ProjectPaletteFilePath(project, candidate);
            try
            {
                if (File.Exists(path)) return PaletteFormat.Load(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // 单个损坏资产不阻塞项目打开，继续寻找其他项目调色板。
            }
        }

        var seed = ResolveEditorPalette();
        TrySave(ProjectPalettePath(project), seed);
        return seed;
    }

    /// <summary>
    /// 把当前色板写回项目级调色板
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="colors">要保存的色表</param>
    /// <returns>写入成功返回 true，IO 失败返回 false</returns>
    public static bool SaveProjectPalette(ProjectHandle project, Rgba32[] colors) =>
        SaveProjectPalette(project, ProjectPaletteRelativePath, colors);

    /// <summary>
    /// 把指定项目调色板资产写回磁盘。
    /// </summary>
    public static bool SaveProjectPalette(ProjectHandle project, string relativePath, Rgba32[] colors) =>
        TrySave(ProjectPaletteFilePath(project, relativePath), colors);

    /// <summary>
    /// 把当前色板写回编辑器级调色板，作为以后新项目的默认
    /// </summary>
    /// <param name="colors">要保存的色表</param>
    /// <returns>写入成功返回 true，IO 失败返回 false</returns>
    public static bool SaveEditorPalette(Rgba32[] colors) => TrySave(EditorPalettePath, colors);

    private static bool IsPaletteExtension(string? extension) =>
        string.Equals(extension, ".palette", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".pal", StringComparison.OrdinalIgnoreCase);

    private static bool TrySave(string path, Rgba32[] colors)
    {
        try
        {
            PaletteFormat.Save(path, colors);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
