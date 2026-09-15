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
/// 项目插件启用状态诊断结果
/// </summary>
public sealed record ProjectPluginDiagnostic(string PluginId, bool Installed, string? Version, string Status);

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
                Permissions = ["project.read", "project.write", "plugin.manage", "automation.register", "editor.command", "editor.panel", "scene.component"],
            },
        };
        if (Directory.Exists(AppPaths.PluginDirectory))
        {
            foreach (var packagePath in Directory.EnumerateFiles(AppPaths.PluginDirectory, "package.json", SearchOption.AllDirectories))
            {
                try
                {
                    var package = JsonSerializer.Deserialize<EditorPluginPackage>(File.ReadAllText(packagePath), JsonOptions);
                    if (package is not null)
                    {
                        Validate(package);
                        if (packages.ContainsKey(package.Id)) continue;
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
        var destinationFull = Path.GetFullPath(destination);
        if (string.Equals(destinationFull, source, StringComparison.OrdinalIgnoreCase))
            return CreateInstalledPackage(package, source);
        if (source.StartsWith(destinationFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destinationFull.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件安装源不能位于目标插件目录或其子目录内");

        var staging = destinationFull + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.GetFullPath(Path.Combine(staging, relative));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件文件路径超出插件目录");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }

            var stagedPackage = CreateInstalledPackage(package, staging);
            ResolveEntryPath(stagedPackage);
            if (Directory.Exists(destinationFull)) Directory.Delete(destinationFull, true);
            Directory.Move(staging, destinationFull);
            return CreateInstalledPackage(package, destinationFull);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
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
    /// 诊断项目清单中的插件安装状态
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <returns>项目插件诊断结果</returns>
    public static IReadOnlyList<ProjectPluginDiagnostic> Diagnose(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var installed = ListInstalled().ToDictionary(item => item.Id, StringComparer.Ordinal);
        var enabled = ListEnabled(project).ToHashSet(StringComparer.Ordinal);
        return enabled.Select(pluginId =>
        {
            installed.TryGetValue(pluginId, out var package);
            if (package is null)
                return new ProjectPluginDiagnostic(pluginId, false, null, "缺失插件");
            var issue = FindDependencyIssue(pluginId, installed, enabled, new HashSet<string>(StringComparer.Ordinal));
            return issue is null
                ? new ProjectPluginDiagnostic(pluginId, true, package.Version, "正常")
                : new ProjectPluginDiagnostic(pluginId, true, package.Version, issue);
        }).ToArray();
    }

    /// <summary>
    /// 递归查找插件依赖链中的第一个问题
    /// </summary>
    /// <param name="pluginId">当前插件 ID</param>
    /// <param name="installed">编辑器级已安装插件</param>
    /// <param name="enabled">项目已启用插件</param>
    /// <param name="visiting">当前递归路径</param>
    /// <returns>诊断文字，没有问题时返回 null</returns>
    private static string? FindDependencyIssue(string pluginId,
        IReadOnlyDictionary<string, EditorPluginPackage> installed,
        IReadOnlySet<string> enabled,
        ISet<string> visiting)
    {
        if (!installed.TryGetValue(pluginId, out var package))
            return $"缺失插件：{pluginId}";
        if (!visiting.Add(pluginId))
            return $"依赖存在循环：{pluginId}";
        foreach (var dependency in package.Dependencies)
        {
            if (!installed.TryGetValue(dependency.Key, out var dependencyPackage))
                return $"缺失依赖：{dependency.Key}";
            if (!enabled.Contains(dependency.Key))
                return $"依赖未启用：{dependency.Key}";
            if (!IsVersionSatisfied(dependencyPackage.Version, dependency.Value))
                return $"依赖版本不满足：{dependency.Key} {dependency.Value}";
            var issue = FindDependencyIssue(dependency.Key, installed, enabled, visiting);
            if (issue is not null)
                return issue;
        }
        visiting.Remove(pluginId);
        return null;
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
        if (!IsValidPluginId(packageInfo.Id) || !Version.TryParse(packageInfo.Version, out _) || string.IsNullOrWhiteSpace(packageInfo.Entry) ||
            packageInfo.Permissions is null || packageInfo.Dependencies is null ||
            !packageInfo.Entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(packageInfo.Entry) || packageInfo.Entry.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("插件 ID 或入口文件不符合安全要求");
        }
        foreach (var dependency in packageInfo.Dependencies)
        {
            if (!IsValidPluginId(dependency.Key) || !IsValidVersionRange(dependency.Value) ||
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
    /// 验证依赖版本范围的基础字符集
    /// </summary>
    /// <param name="versionRange">版本范围</param>
    /// <returns>是否为可解析的基础版本范围</returns>
    private static bool IsValidVersionRange(string? versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange)) return false;
        var hasVersionCharacter = false;
        foreach (var character in versionRange)
        {
            if (char.IsAsciiDigit(character)) hasVersionCharacter = true;
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '^' or '~' or '<' or '>' or '=' or '*' or '|' or ',' or ' ')
                continue;
            return false;
        }
        return hasVersionCharacter || versionRange.Trim() == "*";
    }

    /// <summary>
    /// 创建带有实际安装信息的插件清单
    /// </summary>
    /// <param name="package">原始插件清单</param>
    /// <param name="rootDirectory">插件实际目录</param>
    /// <returns>标准化后的插件清单</returns>
    private static EditorPluginPackage CreateInstalledPackage(EditorPluginPackage package, string rootDirectory) => new()
    {
        Id = package.Id,
        Name = package.Name,
        Version = package.Version,
        Entry = package.Entry,
        Source = "installed",
        RootDirectory = rootDirectory,
        Permissions = package.Permissions.ToList(),
        Dependencies = package.Dependencies.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
    };

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
        foreach (var missingId in current.Where(id => !installed.ContainsKey(id)))
            MVoxelLog.Warn($"项目插件尚未安装，保留清单但不写入锁文件：{missingId}");
        var lockFile = new PluginLockFile
        {
            Plugins = current.Where(installed.ContainsKey).Order(StringComparer.Ordinal).Select(id =>
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
        foreach (var dependency in package.Dependencies)
        {
            if (!installed.TryGetValue(dependency.Key, out var dependencyPackage))
                throw new InvalidOperationException($"插件依赖未安装：{dependency.Key}");
            if (!IsVersionSatisfied(dependencyPackage.Version, dependency.Value))
                throw new InvalidOperationException($"插件依赖版本不满足：{dependency.Key} {dependency.Value}，实际为 {dependencyPackage.Version}");
            AddDependencies(dependency.Key, installed, enabled, visiting);
        }
        visiting.Remove(pluginId);
        enabled.Add(pluginId);
    }

    /// <summary>
    /// 判断插件版本是否满足依赖范围
    /// </summary>
    /// <param name="versionText">已安装版本</param>
    /// <param name="range">依赖版本范围</param>
    /// <returns>是否满足版本范围</returns>
    private static bool IsVersionSatisfied(string versionText, string range)
    {
        if (range.Trim() == "*") return Version.TryParse(versionText, out _);
        if (!Version.TryParse(versionText, out var actual)) return false;
        foreach (var alternative in range.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = true;
            foreach (var part in alternative.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var prefix = part.Length > 0 && part[0] is '^' or '~' ? part[0] : '\0';
                var value = prefix == '\0' ? part : part[1..];
                if (value.EndsWith(".*", StringComparison.Ordinal))
                {
                    var prefixParts = value[..^2].Split('.', StringSplitOptions.RemoveEmptyEntries);
                    matches &= prefixParts.Length switch
                    {
                        1 => int.TryParse(prefixParts[0], out var major) && actual.Major == major,
                        2 => int.TryParse(prefixParts[0], out var major) && int.TryParse(prefixParts[1], out var minor) && actual.Major == major && actual.Minor == minor,
                        _ => false,
                    };
                    continue;
                }
                if (!Version.TryParse(value.TrimStart('>', '<', '='), out var expected)) { matches = false; continue; }
                matches &= prefix switch
                {
                    '^' => actual.Major == expected.Major && actual >= expected,
                    '~' => actual.Major == expected.Major && actual.Minor == expected.Minor && actual >= expected,
                    _ when part.StartsWith(">=", StringComparison.Ordinal) => actual >= expected,
                    _ when part.StartsWith(">", StringComparison.Ordinal) => actual > expected,
                    _ when part.StartsWith("<=", StringComparison.Ordinal) => actual <= expected,
                    _ when part.StartsWith("<", StringComparison.Ordinal) => actual < expected,
                    _ => actual == expected,
                };
            }
            if (matches) return true;
        }
        return false;
    }
}
