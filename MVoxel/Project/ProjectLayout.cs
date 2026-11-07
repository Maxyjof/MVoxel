using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 项目清单（Project.mvoxproj 的内存形态）
/// </summary>
public sealed class ProjectManifest
{
    /// <summary>
    /// 清单格式版本
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 项目稳定身份，移动文件夹也不改变
    /// </summary>
    public StableId ProjectId { get; set; } = StableId.New();

    /// <summary>
    /// 项目显示名
    /// </summary>
    public string Name { get; set; } = "MVoxel Project";

    /// <summary>
    /// 写入清单的引擎版本，用于识别项目结构
    /// </summary>
    public string EngineVersion { get; set; } = "0.1.1";
}

/// <summary>
/// 项目插件启停清单（Plugins/manifest.json）
/// </summary>
public sealed class PluginManifest
{
    /// <summary>
    /// 清单格式版本
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 已启用的插件 ID 列表
    /// </summary>
    public List<string> Enabled { get; set; } = new();
}

/// <summary>
/// 插件依赖解析结果（Plugins/plugins-lock.json）
/// </summary>
public sealed class PluginLockFile
{
    /// <summary>
    /// 锁文件格式版本
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 解析出的插件集合
    /// </summary>
    public List<LockedPlugin> Plugins { get; set; } = new();
}

/// <summary>
/// 单个被锁定的插件
/// </summary>
public sealed class LockedPlugin
{
    /// <summary>
    /// 插件 ID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 解析到的版本号
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 来源标记，内置插件为 embedded
    /// </summary>
    public string Source { get; set; } = "embedded";

    /// <summary>
    /// 安装时解析到的直接依赖及版本范围
    /// </summary>
    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 已打开项目的句柄：根目录、清单与固定子目录路径
/// </summary>
public sealed class ProjectHandle
{
    internal ProjectHandle(string rootPath, ProjectManifest manifest)
    {
        RootPath = rootPath;
        Manifest = manifest;
    }

    /// <summary>
    /// 项目文件夹绝对路径
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// 项目清单
    /// </summary>
    public ProjectManifest Manifest { get; }

    /// <summary>
    /// 资产根目录（Assets）
    /// </summary>
    public string AssetsPath => Path.Combine(RootPath, "Assets");

    /// <summary>
    /// 插件目录（Plugins）
    /// </summary>
    public string PluginsPath => Path.Combine(RootPath, "Plugins");

    /// <summary>
    /// 项目级设置目录中的统一配置文件
    /// </summary>
    public string ProjectSettingsPath => Path.Combine(RootPath, "ProjectSettings", "settings.json");
}

/// <summary>
/// 项目文件夹结构的唯一权威：一个项目就是一个文件夹，
/// 资产分类目录仅在创建新项目时提供默认值
/// </summary>
public static class ProjectLayout
{
    /// <summary>
    /// 项目清单文件名
    /// </summary>
    public const string ProjectFileName = "Project.mvoxproj";

    /// <summary>
    /// 新项目默认提供的资产目录，用户可自行组织其他文件夹
    /// </summary>
    public static readonly string[] AssetDirectories = ["Scenes", "Materials", "Palettes"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 创建新项目：目录结构、清单与插件文件全部原子写出
    /// </summary>
    /// <param name="rootPath">项目文件夹路径</param>
    /// <param name="name">项目名，空白时取文件夹名</param>
    /// <returns>新项目句柄</returns>
    public static ProjectHandle Create(string rootPath, string? name = null)
    {
        var absolutePath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(absolutePath);
        var manifest = new ProjectManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(absolutePath).Name : name,
        };
        EnsureDirectories(absolutePath);
        foreach (var directory in AssetDirectories)
            Directory.CreateDirectory(Path.Combine(absolutePath, "Assets", directory));
        WriteJson(Path.Combine(absolutePath, ProjectFileName), manifest);
        WriteJson(Path.Combine(absolutePath, "Plugins", "manifest.json"), new PluginManifest());
        WriteJson(Path.Combine(absolutePath, "Plugins", "plugins-lock.json"), new PluginLockFile());
        return new ProjectHandle(absolutePath, manifest);
    }

    /// <summary>
    /// 打开已有项目；只确保项目基础目录存在
    /// </summary>
    /// <param name="rootPath">项目文件夹路径</param>
    /// <returns>项目句柄</returns>
    public static ProjectHandle Open(string rootPath, bool ensureDirectories = true)
    {
        var absolutePath = Path.GetFullPath(rootPath);
        var manifestPath = Path.Combine(absolutePath, ProjectFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Not an MVoxel project: {manifestPath}", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException($"Invalid project manifest: {manifestPath}");
        if (ensureDirectories) EnsureDirectories(absolutePath);
        return new ProjectHandle(absolutePath, manifest);
    }

    /// <summary>
    /// 幂等确保项目基础目录存在
    /// </summary>
    /// <param name="rootPath">项目根目录</param>
    public static void EnsureDirectories(string rootPath)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(rootPath, "Plugins"));
        Directory.CreateDirectory(Path.Combine(rootPath, "ProjectSettings"));
    }

    private static void WriteJson<T>(string path, T value)
    {
        AtomicFile.WriteText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
