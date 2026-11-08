using System.IO.Compression;
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
    /// JavaScript 或 Lua 入口文件名
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
    /// 内置 Lua 双端协议示例插件稳定 ID
    /// </summary>
    public const string LuaSamplePluginId = "com.mvoxel.lua-sample";

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
            [LuaSamplePluginId] = new EditorPluginPackage
            {
                Id = LuaSamplePluginId,
                Name = "Lua 双端协议示例",
                Version = "1.0.0",
                Entry = "index.lua",
                Source = "builtin",
                RootDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins", LuaSamplePluginId),
                Permissions = ["automation.register", "editor.command", "editor.panel", "scene.component"],
            },
        };
        if (Directory.Exists(AppPaths.PluginDirectory))
        {
            foreach (var packagePath in EnumerateInstalledManifests(AppPaths.PluginDirectory))
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
                catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    MVoxelLog.Warn($"跳过无效插件清单：{packagePath}，原因：{exception.Message}");
                }
            }
        }
        return packages.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> EnumerateInstalledManifests(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"无法读取插件目录：{directory}，原因：{exception.Message}");
                continue;
            }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    MVoxelLog.Warn($"无法读取插件路径：{entry}，原因：{exception.Message}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (directory == root && IsInstallWorkDirectory(name)) continue;
                    pending.Push(entry);
                }
                else if (string.Equals(Path.GetFileName(entry), "package.json", StringComparison.OrdinalIgnoreCase))
                    yield return entry;
            }
        }
    }

    private static bool IsInstallWorkDirectory(string name)
    {
        foreach (var marker in new[] { ".staging-", ".backup-" })
        {
            var index = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (index > 0 && Guid.TryParseExact(name[(index + marker.Length)..], "N", out _)) return true;
        }
        return false;
    }

    /// <summary>
    /// 获取插件脚本入口的绝对路径
    /// </summary>
    /// <param name="packageInfo">插件清单</param>
    /// <returns>入口文件绝对路径</returns>
    public static string ResolveEntryPath(EditorPluginPackage packageInfo)
    {
        Validate(packageInfo);
        var root = Path.GetFullPath(packageInfo.RootDirectory);
        var entry = Path.GetFullPath(Path.Combine(root, packageInfo.Entry));
        if (!entry.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            throw new FileNotFoundException("插件脚本入口不存在", entry);
        return entry;
    }

    /// <summary>
    /// 将脚本插件目录安装到编辑器级插件目录
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
        if (package.Id is MvoxMcpPluginId or LuaSamplePluginId)
            throw new InvalidDataException($"插件 ID 属于程序内置插件，不能安装同名插件：{package.Id}");
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
            foreach (var file in EnumeratePluginFiles(source))
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
            var backup = destinationFull + ".backup-" + Guid.NewGuid().ToString("N");
            var hadPrevious = Directory.Exists(destinationFull);
            if (hadPrevious) Directory.Move(destinationFull, backup);
            try
            {
                Directory.Move(staging, destinationFull);
            }
            catch (Exception installException)
            {
                if (hadPrevious)
                {
                    try
                    {
                        if (Directory.Exists(destinationFull))
                            throw new IOException("目标目录已存在，无法自动恢复旧版本");
                        Directory.Move(backup, destinationFull);
                    }
                    catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                    {
                        var message = $"插件更新失败且旧版本未能自动恢复；备份保留在：{backup}。恢复原因：{restoreException.Message}";
                        MVoxelLog.Warn(message);
                        throw new IOException(message, new AggregateException(installException, restoreException));
                    }
                }
                throw;
            }
            if (hadPrevious)
            {
                try { Directory.Delete(backup, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    MVoxelLog.Warn($"插件已更新，但旧版本备份未能删除：{backup}，原因：{exception.Message}");
                }
            }
            return CreateInstalledPackage(package, destinationFull);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"插件安装暂存目录未能清理：{staging}，原因：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 分发包内的逐文件校验清单名
    /// </summary>
    public const string ChecksumsFileName = "checksums.json";
    private const int MaximumPackageEntries = 10000;
    private const long MaximumPackageBytes = 1L << 30;

    /// <summary>
    /// 把插件目录打包为 .mvplugin 分发包：zip 内含全部插件文件与逐文件 sha256 清单
    /// </summary>
    /// <param name="sourceDirectory">包含 package.json 的插件目录</param>
    /// <param name="outputFile">输出的 .mvplugin 文件路径</param>
    /// <returns>输出文件路径</returns>
    public static string PackPlugin(string sourceDirectory, string outputFile)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var packagePath = Path.Combine(source, "package.json");
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("插件目录缺少 package.json", source);
        var output = Path.GetFullPath(outputFile);
        if (output.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件分发包不能输出到插件源目录内");
        var files = EnumeratePluginFiles(source).ToArray();
        if (files.Length + 1 > MaximumPackageEntries)
            throw new InvalidDataException($"插件文件数量不能超过 {MaximumPackageEntries - 1}");
        long sourceBytes = 0;
        foreach (var file in files)
        {
            if (string.Equals(Path.GetRelativePath(source, file), ChecksumsFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var size = new FileInfo(file).Length;
            if (size > MaximumPackageBytes - sourceBytes)
                throw new InvalidDataException("插件文件总大小超过 1 GiB");
            sourceBytes += size;
        }
        var package = JsonSerializer.Deserialize<EditorPluginPackage>(File.ReadAllText(packagePath), JsonOptions)
            ?? throw new InvalidDataException("插件清单为空");
        Validate(package);
        ResolveEntryPath(CreateInstalledPackage(package, source));
        var directory = Path.GetDirectoryName(output)
            ?? throw new ArgumentException("输出路径必须包含目录", nameof(outputFile));
        Directory.CreateDirectory(directory);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        var checksums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(temporary, System.IO.Compression.ZipArchiveMode.Create))
            {
                long writtenBytes = 0;
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (string.Equals(relative, ChecksumsFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = archive.CreateEntry(relative);
                    using var input = File.OpenRead(file);
                    using var outputStream = entry.Open();
                    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (read > MaximumPackageBytes - writtenBytes)
                            throw new InvalidDataException("插件文件总大小超过 1 GiB");
                        writtenBytes += read;
                        hash.AppendData(buffer, 0, read);
                        outputStream.Write(buffer, 0, read);
                    }
                    checksums[relative] = Convert.ToHexString(hash.GetHashAndReset());
                }
                var checksumJson = JsonSerializer.Serialize(checksums, JsonOptions);
                if (System.Text.Encoding.UTF8.GetByteCount(checksumJson) + 3L > MaximumPackageBytes - writtenBytes)
                    throw new InvalidDataException("插件文件及校验清单总大小超过 1 GiB");
                var manifest = archive.CreateEntry(ChecksumsFileName);
                using var stream = manifest.Open();
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write(checksumJson);
            }
            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return output;
    }

    private static IEnumerable<string> EnumeratePluginFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"插件目录不能包含链接：{directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"插件目录不能包含链接：{entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    /// <summary>
    /// 安装 .mvplugin 分发包：先解包并逐文件校验 sha256，任何不一致都拒绝安装，
    /// 校验通过后复用目录安装流程
    /// </summary>
    /// <param name="packageFile">.mvplugin 文件路径</param>
    /// <returns>安装后的插件清单</returns>
    public static EditorPluginPackage InstallFromPackage(string packageFile)
    {
        var full = Path.GetFullPath(packageFile);
        if (!File.Exists(full)) throw new FileNotFoundException("插件分发包不存在", full);
        var staging = Path.Combine(Path.GetTempPath(), "MVoxel", "plugin-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(full))
            {
                if (archive.Entries.Count > MaximumPackageEntries)
                    throw new InvalidDataException($"插件包条目数量不能超过 {MaximumPackageEntries}");
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Length > MaximumPackageBytes - total)
                        throw new InvalidDataException("插件包解压大小超过 1 GiB");
                    total += entry.Length;
                }
                Directory.CreateDirectory(staging);
                long extracted = 0;
                var buffer = new byte[81920];
                foreach (var entry in archive.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':'))
                        throw new InvalidDataException($"插件包路径无效：{entry.FullName}");
                    var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"插件包路径超出目录：{entry.FullName}");
                    if (relative.EndsWith('/'))
                    {
                        if (entry.Length != 0) throw new InvalidDataException("插件包目录条目包含数据");
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var outputStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    long entryBytes = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (read > MaximumPackageBytes - extracted || read > entry.Length - entryBytes)
                            throw new InvalidDataException("插件包实际解压大小超过限制或与声明不符");
                        extracted += read;
                        entryBytes += read;
                        outputStream.Write(buffer, 0, read);
                    }
                    if (entryBytes != entry.Length) throw new InvalidDataException("插件包条目大小与声明不符");
                }
            }
            var checksumsPath = Path.Combine(staging, ChecksumsFileName);
            if (!File.Exists(checksumsPath)) throw new InvalidDataException("插件分发包缺少校验清单");
            if (new FileInfo(checksumsPath).Length > 8 * 1024 * 1024)
                throw new InvalidDataException("插件校验清单超过 8 MiB");
            var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(checksumsPath), JsonOptions)
                ?? throw new InvalidDataException("插件校验清单为空");
            var actualFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(staging, file).Replace('\\', '/'))
                .Where(relative => !string.Equals(relative, ChecksumsFileName, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var listedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, expected) in checksums)
            {
                if (relative.Contains('\\') || !listedFiles.Add(relative) || !actualFiles.Contains(relative))
                    throw new InvalidDataException($"插件校验清单包含无效或重复路径：{relative}");
                var file = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件分发包包含越界路径");
                if (!File.Exists(file) || !string.Equals(ComputeSha256(file), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"插件文件校验失败：{relative}");
            }
            if (!actualFiles.SetEquals(listedFiles))
                throw new InvalidDataException("插件分发包包含未列入校验清单的文件");

            return InstallFromDirectory(staging);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"插件包解压暂存目录未能清理：{staging}，原因：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 计算文件的 sha256 十六进制串
    /// </summary>
    private static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// 读取项目已启用插件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <returns>已启用插件 ID</returns>
    public static IReadOnlyList<string> ListEnabled(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try { return ReadEnabled(project, strict: false); }
        catch (InvalidDataException exception)
        {
            MVoxelLog.Warn($"插件启停清单不可用，跳过插件启动并保留原文件：{exception.Message}");
            return [];
        }
    }

    private static IReadOnlyList<string> ReadEnabled(ProjectHandle project, bool strict)
    {
        var path = Path.Combine(project.PluginsPath, "manifest.json");
        if (!File.Exists(path)) return [];
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions);
            if (manifest is null || manifest.Enabled is null || manifest.FormatVersion != 1)
                throw new InvalidDataException($"插件启停清单为空、字段无效或版本不受支持：{path}");
            if (strict && manifest.Enabled.Any(item => !IsValidPluginId(item)))
                throw new InvalidDataException($"插件启停清单包含无效 ID，未修改原文件：{path}");
            return manifest.Enabled
                .Where(item => IsValidPluginId(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"插件启停清单格式无效，保留原文件：{path}，原因：{exception.Message}", exception);
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
        return Diagnose(installed, enabled);
    }

    internal static IReadOnlyList<ProjectPluginDiagnostic> Diagnose(
        IReadOnlyDictionary<string, EditorPluginPackage> installed, IReadOnlySet<string> enabled)
    {
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
    /// 删除用户安装的插件目录并把它从当前项目清单移除。内置插件、越界目录、
    /// 与 ID 不一致的目录一律拒绝；安装时留下的暂存/备份目录一并清掉。
    /// </summary>
    /// <param name="pluginId">插件 ID</param>
    /// <param name="project">当前项目，可为 null</param>
    /// <returns>被删除的插件目录绝对路径</returns>
    public static string Uninstall(string pluginId, ProjectHandle? project = null)
    {
        if (!IsValidPluginId(pluginId)) throw new InvalidDataException("插件 ID 无效");
        if (pluginId is MvoxMcpPluginId or LuaSamplePluginId)
            throw new InvalidOperationException("内置插件不能删除，只能在项目里停用");
        var package = ListInstalled().FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("找不到要删除的插件", pluginId);
        if (!string.Equals(package.Source, "installed", StringComparison.Ordinal))
            throw new InvalidOperationException("内置插件不能删除");
        var root = Path.GetFullPath(AppPaths.PluginDirectory);
        var directory = Path.GetFullPath(package.RootDirectory);
        if (!string.Equals(directory, Path.Combine(root, pluginId), StringComparison.OrdinalIgnoreCase) ||
            !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("插件目录不在编辑器插件目录内或与 ID 不一致，拒绝删除");

        if (project is not null)
        {
            var enabled = ReadEnabled(project, strict: false)
                .Where(id => !string.Equals(id, pluginId, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            WritePluginState(project, enabled, ListInstalled().ToDictionary(item => item.Id, StringComparer.Ordinal));
        }

        Directory.Delete(directory, recursive: true);
        foreach (var leftover in Directory.EnumerateDirectories(root, pluginId + ".*")
                     .Where(path => IsInstallWorkDirectory(Path.GetFileName(path))))
        {
            try { Directory.Delete(leftover, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"插件残留目录清理失败：{leftover}，原因：{exception.Message}");
            }
        }

        MVoxelLog.Info($"已删除插件：{pluginId}（{directory}）");
        return directory;
    }

    /// <summary>
    /// 验证插件脚本入口名称
    /// </summary>
    /// <param name="packageInfo">插件清单</param>
    public static void Validate(EditorPluginPackage packageInfo)
    {
        ArgumentNullException.ThrowIfNull(packageInfo);
        if (!IsValidPluginId(packageInfo.Id) || !Version.TryParse(packageInfo.Version, out _) || string.IsNullOrWhiteSpace(packageInfo.Entry) ||
            packageInfo.Permissions is null || packageInfo.Dependencies is null ||
            !(packageInfo.Entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
              packageInfo.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) ||
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
    /// 验证依赖版本范围是否可由版本匹配器解析
    /// </summary>
    /// <param name="versionRange">版本范围</param>
    /// <returns>是否为可解析的基础版本范围</returns>
    private static bool IsValidVersionRange(string? versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange)) return false;
        foreach (var alternative in versionRange.Replace("||", "|", StringComparison.Ordinal).Split('|'))
        {
            var parts = alternative.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            foreach (var part in parts)
            {
                if (part == "*") continue;
                if (part.EndsWith(".*", StringComparison.Ordinal))
                {
                    var components = part[..^2].Split('.');
                    if (components.Length is < 1 or > 2 || components.Any(item =>
                        item.Length == 0 || !item.All(char.IsAsciiDigit) || !int.TryParse(item, out _))) return false;
                    continue;
                }
                var prefixLength = part.StartsWith(">=", StringComparison.Ordinal) || part.StartsWith("<=", StringComparison.Ordinal)
                    ? 2 : part[0] is '^' or '~' or '>' or '<' or '=' ? 1 : 0;
                if (!Version.TryParse(part[prefixLength..], out _)) return false;
            }
        }
        return true;
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
        if (!IsValidPluginId(pluginId)) throw new ArgumentException("插件 ID 不合法", nameof(pluginId));
        var installed = ListInstalled().ToDictionary(item => item.Id, StringComparer.Ordinal);

        var current = ReadEnabled(project, strict: true).ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            var packageInfo = installed.GetValueOrDefault(pluginId)
                ?? throw new InvalidOperationException($"未找到编辑器级插件：{pluginId}");
            Validate(packageInfo);
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
        WritePluginState(project, current, installed);
    }

    /// <summary>
    /// 写入项目插件清单与锁文件。卸载插件时用它强制移除记录，绕过依赖检查。
    /// </summary>
    private static void WritePluginState(ProjectHandle project, ISet<string> current,
        IReadOnlyDictionary<string, EditorPluginPackage> installed)
    {
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
    internal static bool IsVersionSatisfied(string versionText, string range)
    {
        if (!IsValidVersionRange(range)) return false;
        if (!Version.TryParse(versionText, out var parsedActual)) return false;
        var actual = NormalizeVersion(parsedActual);
        foreach (var alternative in range.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = true;
            foreach (var part in alternative.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part == "*") continue;
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
                if (!Version.TryParse(value.TrimStart('>', '<', '='), out var parsedExpected)) { matches = false; continue; }
                var expected = NormalizeVersion(parsedExpected);
                matches &= prefix switch
                {
                    '^' => actual >= expected && actual.Major == expected.Major &&
                           (expected.Major > 0 || actual.Minor == expected.Minor) &&
                           (expected.Major > 0 || expected.Minor > 0 || parsedExpected.Build < 0 || actual.Build == expected.Build),
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

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
