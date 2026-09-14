using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 最近项目列表中的一条记录
/// </summary>
public sealed class RecentProjectEntry
{
    /// <summary>
    /// 记录时的项目名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 项目文件夹绝对路径（已去除尾部分隔符）
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 最后一次打开的时间（UTC）
    /// </summary>
    public DateTimeOffset LastOpenedUtc { get; set; }
}

/// <summary>
/// 用户级"最近打开"列表的存取：显示上限固定、路径规范化去重、
/// 指向已删除项目的记录在展示前就被过滤掉
/// </summary>
public static class RecentProjects
{
    /// <summary>
    /// 列表最多显示的条目数
    /// </summary>
    public const int MaxEntries = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 默认存储位置：程序目录/MVoxelData/Config/recent-projects.json
    /// </summary>
    public static string DefaultStoragePath
    {
        get => AppPaths.ConfigPath("recent-projects.json");
    }

    /// <summary>
    /// 读取并净化最近项目列表：去重、过滤失效路径、按时间倒序截断
    /// </summary>
    /// <param name="storagePath">可选的自定义存储路径（测试用）</param>
    /// <returns>可直接展示的条目序列</returns>
    public static IReadOnlyList<RecentProjectEntry> Load(string? storagePath = null)
    {
        var path = storagePath ?? DefaultStoragePath;
        if (!File.Exists(path))
        {
            return Array.Empty<RecentProjectEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(path), JsonOptions)
                ?? new List<RecentProjectEntry>();
            return entries
                .Where(entry => entry is not null && IsExistingProject(entry))
                .GroupBy(entry => NormalizePath(entry.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(entry => entry.LastOpenedUtc).First())
                .OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (IOException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (ArgumentException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
    }

    /// <summary>
    /// 记录一次项目打开，同路径条目更新后按时间排序并原子落盘
    /// </summary>
    /// <param name="project">刚打开的项目</param>
    /// <param name="storagePath">可选的自定义存储路径（测试用）</param>
    /// <param name="openedAtUtc">可选的打开时间（测试用）</param>
    public static void Record(ProjectHandle project, string? storagePath = null, DateTimeOffset? openedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var path = storagePath ?? DefaultStoragePath;
        var normalizedProjectPath = NormalizePath(project.RootPath);
        var entries = Load(path)
            .Where(entry => !string.Equals(NormalizePath(entry.Path), normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        entries.Add(new RecentProjectEntry
        {
            Name = project.Manifest.Name,
            Path = normalizedProjectPath,
            LastOpenedUtc = openedAtUtc ?? DateTimeOffset.UtcNow,
        });

        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Recent-project storage must include a directory.", nameof(storagePath));
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(
            entries.OrderByDescending(entry => entry.LastOpenedUtc).Take(MaxEntries),
            JsonOptions);
        AtomicFile.WriteText(path, json);
    }

    /// <summary>
    /// 路径是否仍指向一个含清单的有效项目
    /// </summary>
    /// <param name="entry">待检查条目</param>
    /// <returns>项目是否仍然存在</returns>
    private static bool IsExistingProject(RecentProjectEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
        {
            return false;
        }

        try
        {
            var rootPath = NormalizePath(entry.Path);
            return Directory.Exists(rootPath)
                && File.Exists(System.IO.Path.Combine(rootPath, ProjectLayout.ProjectFileName));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 规范化路径：转绝对路径并去掉尾部分隔符（根路径除外）
    /// </summary>
    /// <param name="path">原始路径</param>
    /// <returns>规范化后的路径</returns>
    private static string NormalizePath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath);
        return root is not null && fullPath.Length > root.Length
            ? fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
