using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 编辑器级插件包的清单
/// </summary>
public sealed class EditorPluginPackage
{
    /// <summary>
    /// 插件稳定 ID
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 插件显示名称
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 插件版本
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// JavaScript 入口文件名
    /// </summary>
    [JsonPropertyName("main")]
    public string Entry { get; init; } = "index.js";

    /// <summary>
    /// 插件来源
    /// </summary>
    public string Source { get; init; } = "installed";

    /// <summary>
    /// 插件申请的宿主权限
    /// </summary>
    public List<string> Permissions { get; init; } = new();

    /// <summary>
    /// 插件声明的直接依赖及版本范围
    /// </summary>
    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 插件实际目录
    /// </summary>
    [JsonIgnore]
    public string RootDirectory { get; init; } = string.Empty;
}

/// <summary>
/// 管理编辑器级插件和项目级启停状态
/// </summary>
public static class PluginManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 内置 MVoxel MCP 插件稳定 ID
    /// </summary>
    public const string MvoxMcpPluginId = "com.mvoxel.mcp";

    /// <summary>
    /// 获取程序随附的内置插件
    /// </summary>
    /// <returns>内置插件清单</returns>
    public static IReadOnlyList<EditorPluginPackage> ListInstalled()
    {
        var packages = new Dictionary<string, EditorPluginPackage>(StringComparer.Ordinal)
        {
            [MvoxMcpPluginId] = new EditorPluginPackage
            {
                Id = MvoxMcpPluginId,
                Name = "MVoxel MCP",
                Version = "1.0.0",
                Entry = "index.js",
                Source = "builtin",
                RootDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins", MvoxMcpPluginId),
                Permissions = ["project.read", "project.write", "plugin.manage", "automation.register", "editor.command", "editor.panel"],
            },
        };
        if (Directory.Exists(AppPaths.PluginDirectory))
        {
            foreach (var packagePath in Directory.EnumerateFiles(AppPaths.PluginDirectory, "package.json", SearchOption.AllDirectories))
            {
                try
                {
                    var package = JsonSerializer.Deserialize<EditorPluginPackage>(File.ReadAllText(packagePath), JsonOptions);
                    if (package is not null && !packages.ContainsKey(package.Id))
                    {
                        Validate(package);
                        packages.Add(package.Id, new EditorPluginPackage
                        {
                            Id = package.Id,
                            Name = package.Name,
                            Version = package.Version,
                            Entry = package.Entry,
                            Source = "installed",
                            RootDirectory = Path.GetDirectoryName(packagePath)!,
                            Permissions = package.Permissions.ToList(),
                            Dependencies = package.Dependencies.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                        });
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    MVoxelLog.Warn($"跳过无效插件清单：{packagePath}，原因：{exception.Message}");
                }
            }
        }
        return packages.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 获取插件 JavaScript 入口的绝对路径
    /// </summary>
    /// <param name="packageInfo">插件清单</param>
    /// <returns>入口文件绝对路径</returns>
    public static string ResolveEntryPath(EditorPluginPackage packageInfo)
    {
        Validate(packageInfo);
        var root = Path.GetFullPath(packageInfo.RootDirectory);
        var entry = Path.GetFullPath(Path.Combine(root, packageInfo.Entry));
        if (!entry.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            throw new FileNotFoundException("插件 JavaScript 入口不存在", entry);
        return entry;
    }

    /// <summary>
    /// 将 JavaScript 插件目录安装到编辑器级插件目录
    /// </summary>
    /// <param name="sourceDirectory">包含 package.json 的插件目录</param>
    /// <returns>安装后的插件清单</returns>
    public static EditorPluginPackage InstallFromDirectory(string sourceDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var packagePath = Path.Combine(source, "package.json");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("插件目录缺少 package.json", packagePath);
        var package = JsonSerializer.Deserialize<EditorPluginPackage>(File.ReadAllText(packagePath), JsonOptions)
            ?? throw new InvalidDataException("插件清单为空");
        Validate(package);
        var destination = Path.Combine(AppPaths.PluginDirectory, package.Id);
        if (string.Equals(Path.GetFullPath(destination), source, StringComparison.OrdinalIgnoreCase)) return package;
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件文件路径超出插件目录");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        return new EditorPluginPackage
        {
            Id = package.Id,
            Name = package.Name,
            Version = package.Version,
            Entry = package.Entry,
            Source = "installed",
            RootDirectory = destination,
            Permissions = package.Permissions.ToList(),
            Dependencies = package.Dependencies.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// 读取项目已启用插件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <returns>已启用插件 ID</returns>
    public static IReadOnlyList<string> ListEnabled(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = Path.Combine(project.PluginsPath, "manifest.json");
        if (!File.Exists(path)) return [];
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions);
            return manifest?.Enabled?
                .Where(item => IsValidPluginId(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
        }
        catch (JsonException exception)
        {
            MVoxelLog.Warn($"插件启停清单损坏，按空集合恢复：{path}，原因：{exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// 启用项目插件并重建锁文件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="pluginId">插件 ID</param>
    public static void Enable(ProjectHandle project, string pluginId)
    {
        Update(project, pluginId, enabled: true);
    }

    /// <summary>
    /// 禁用项目插件并重建锁文件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="pluginId">插件 ID</param>
    public static void Disable(ProjectHandle project, string pluginId)
    {
        Update(project, pluginId, enabled: false);
    }

    /// <summary>
    /// 验证插件 JavaScript 入口名称
    /// </summary>
    /// <param name="packageInfo">插件清单</param>
    public static void Validate(EditorPluginPackage packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        if (!IsValidPluginId(packageInfo.Id) || string.IsNullOrWhiteSpace(packageInfo.Entry) ||
            !packageInfo.Entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(packageInfo.Entry) || packageInfo.Entry.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("插件 ID 或入口文件不符合安全要求");
        }
        foreach (var dependency in packageInfo.Dependencies)
        {
            if (!IsValidPluginId(dependency.Key) || string.IsNullOrWhiteSpace(dependency.Value) ||
                string.Equals(dependency.Key, packageInfo.Id, StringComparison.Ordinal))
                throw new InvalidDataException("插件依赖声明不符合安全要求");
        }
    }

    /// <summary>
    /// 验证插件 ID 是否可以安全作为目录名和清单键
    /// </summary>
    /// <param name="pluginId">插件 ID</param>
    /// <returns>是否为安全的插件 ID</returns>
    private static bool IsValidPluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 128 ||
            pluginId[0] == '.' || pluginId[^1] == '.' || pluginId[0] == '-' || pluginId[^1] == '-')
            return false;
        foreach (var character in pluginId)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 写入项目插件启停清单并同步锁文件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="pluginId">插件 ID</param>
    /// <param name="enabled">是否启用</param>
    private static void Update(ProjectHandle project, string pluginId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("插件 ID 不能为空", nameof(pluginId));
        var installed = ListInstalled().ToDictionary(item => item.Id, StringComparer.Ordinal);
        var packageInfo = installed.GetValueOrDefault(pluginId)
            ?? throw new InvalidOperationException($"未找到编辑器级插件：{pluginId}");
        Validate(packageInfo);

        var current = ListEnabled(project).ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            AddDependencies(pluginId, installed, current, new HashSet<string>(StringComparer.Ordinal));
        }
        else
        {
            var dependent = installed.Values.FirstOrDefault(item =>
                current.Contains(item.Id) && item.Dependencies.ContainsKey(pluginId));
            if (dependent is not null)
                throw new InvalidOperationException($"插件仍被已启用插件依赖：{dependent.Id}");
            current.Remove(pluginId);
        }
        var manifest = new PluginManifest { Enabled = current.Order(StringComparer.Ordinal).ToList() };
        AtomicFile.WriteText(Path.Combine(project.PluginsPath, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        var lockFile = new PluginLockFile
        {
            Plugins = current.Order(StringComparer.Ordinal).Select(id =>
            {
                var item = installed[id];
                return new LockedPlugin
                {
                    Id = item.Id,
                    Version = item.Version,
                    Source = item.Source,
                    Dependencies = item.Dependencies.ToDictionary(dependency => dependency.Key, dependency => dependency.Value, StringComparer.Ordinal),
                };
            }).ToList(),
        };
        AtomicFile.WriteText(Path.Combine(project.PluginsPath, "plugins-lock.json"),
            JsonSerializer.Serialize(lockFile, JsonOptions));
    }

    /// <summary>
    /// 递归解析插件依赖并加入项目启用集合
    /// </summary>
    /// <param name="pluginId">当前插件 ID</param>
    /// <param name="installed">编辑器级已安装插件</param>
    /// <param name="enabled">项目启用集合</param>
    /// <param name="visiting">当前递归路径</param>
    private static void AddDependencies(string pluginId,
        IReadOnlyDictionary<string, EditorPluginPackage> installed,
        ISet<string> enabled,
        ISet<string> visiting)
    {
        if (!installed.TryGetValue(pluginId, out var package))
            throw new InvalidOperationException($"插件依赖未安装：{pluginId}");
        if (!visiting.Add(pluginId))
            throw new InvalidOperationException($"插件依赖存在循环：{pluginId}");
        foreach (var dependencyId in package.Dependencies.Keys)
            AddDependencies(dependencyId, installed, enabled, visiting);
        visiting.Remove(pluginId);
        enabled.Add(pluginId);
    }
}
