using System.Diagnostics;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 插件向编辑器注册的自动化入口
/// </summary>
public sealed record PluginAutomation(string Id, string Name, string PluginId);

/// <summary>
/// 插件向编辑器注册的命令
/// </summary>
public sealed record PluginCommand(string Id, string Name, string PluginId);

/// <summary>
/// 插件向编辑器注册的面板
/// </summary>
public sealed record PluginPanel(string Id, string Name, string PluginId);

/// <summary>
/// 通过受限 Node.js 子进程执行 JavaScript 插件入口
/// </summary>
public sealed class JavaScriptPluginRuntime : IDisposable
{
    private readonly List<EditorPluginPackage> _active = new();
    private readonly List<PluginAutomation> _automations = new();
    private readonly Dictionary<string, EditorPluginPackage> _automationOwners = new(StringComparer.Ordinal);
    private readonly List<PluginCommand> _commands = new();
    private readonly List<PluginPanel> _panels = new();
    private readonly Dictionary<string, EditorPluginPackage> _commandOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前已注册的自动化入口
    /// </summary>
    public IReadOnlyList<PluginAutomation> Automations => _automations;

    /// <summary>
    /// 当前已注册的编辑器命令
    /// </summary>
    public IReadOnlyList<PluginCommand> Commands => _commands;

    /// <summary>
    /// 当前已注册的编辑器面板
    /// </summary>
    public IReadOnlyList<PluginPanel> Panels => _panels;

    /// <summary>
    /// 激活项目中已启用的 JavaScript 插件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="timeout">单个插件启动时间上限</param>
    /// <returns>成功激活的插件</returns>
    public IReadOnlyList<EditorPluginPackage> Activate(ProjectHandle project, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var pluginId in PluginManager.ListEnabled(project))
        {
            var package = PluginManager.ListInstalled().FirstOrDefault(item => item.Id == pluginId);
            if (package is null) continue;
            if (!package.Permissions.Contains("automation.register", StringComparer.Ordinal))
            {
                MVoxelLog.Warn($"插件未声明 automation.register 权限，跳过激活：{package.Id}");
                continue;
            }
            try
            {
                var extensions = ExecuteActivate(package, timeout);
                foreach (var item in extensions.Automations)
                {
                    var automation = new PluginAutomation(item.Id, item.Name, package.Id);
                    _automations.Add(automation);
                    _automationOwners[item.Id] = package;
                }
                foreach (var item in extensions.Commands)
                {
                    if (!package.Permissions.Contains("editor.command", StringComparer.Ordinal)) continue;
                    _commands.Add(new PluginCommand(item.Id, item.Name, package.Id));
                    _commandOwners[item.Id] = package;
                }
                if (package.Permissions.Contains("editor.panel", StringComparer.Ordinal))
                    _panels.AddRange(extensions.Panels.Select(item => new PluginPanel(item.Id, item.Name, package.Id)));
                _active.Add(package);
                MVoxelLog.Info($"JavaScript 插件已激活：{package.Id}");
            }
            catch (Exception exception)
            {
                MVoxelLog.Warn($"JavaScript 插件激活失败：{package.Id}，原因：{exception.Message}");
            }
        }
        return _active;
    }

    /// <summary>
    /// 执行当前项目已注册的自动化入口
    /// </summary>
    /// <param name="automationId">自动化入口 ID</param>
    /// <param name="arguments">自动化参数</param>
    /// <param name="timeout">单次执行时间上限</param>
    /// <returns>自动化返回值</returns>
    public JsonElement Invoke(string automationId, JsonElement arguments, TimeSpan timeout)
    {
        if (!_automationOwners.TryGetValue(automationId, out var package))
            throw new InvalidOperationException($"未找到已启用的自动化入口：{automationId}");
        var entry = PluginManager.ResolveEntryPath(package);
        var script = "const m=await import('node:url').then(x=>x.pathToFileURL(process.argv[1]).href);" +
            "const p=await import(m);const id=process.argv[2];const a=JSON.parse(process.argv[3]);" +
            "const h={registerAutomation:()=>{}};let r=null;if(typeof p.runAutomation==='function')r=await p.runAutomation(id,a,h);" +
            "process.stdout.write(JSON.stringify(r??null));";
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--permission");
        startInfo.ArgumentList.Add($"--allow-fs-read={package.RootDirectory}");
        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(entry);
        startInfo.ArgumentList.Add(automationId);
        startInfo.ArgumentList.Add(arguments.GetRawText());
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 JavaScript 运行时");
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(true);
            throw new TimeoutException("自动化执行超时");
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "自动化执行失败" : error.Trim());
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 执行当前项目已注册的编辑器命令
    /// </summary>
    /// <param name="commandId">命令 ID</param>
    /// <param name="arguments">命令参数</param>
    /// <param name="timeout">单次执行时间上限</param>
    /// <returns>命令返回值</returns>
    public JsonElement InvokeCommand(string commandId, JsonElement arguments, TimeSpan timeout)
    {
        if (!_commandOwners.TryGetValue(commandId, out var package))
            throw new InvalidOperationException($"未找到已启用的编辑器命令：{commandId}");
        var entry = PluginManager.ResolveEntryPath(package);
        var script = "const m=await import('node:url').then(x=>x.pathToFileURL(process.argv[1]).href);" +
            "const p=await import(m);const id=process.argv[2];const a=JSON.parse(process.argv[3]);" +
            "let r=null;if(typeof p.runCommand==='function')r=await p.runCommand(id,a);process.stdout.write(JSON.stringify(r??null));";
        var startInfo = new ProcessStartInfo { FileName = "node", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        startInfo.ArgumentList.Add("--permission");
        startInfo.ArgumentList.Add($"--allow-fs-read={package.RootDirectory}");
        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(entry);
        startInfo.ArgumentList.Add(commandId);
        startInfo.ArgumentList.Add(arguments.GetRawText());
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 JavaScript 运行时");
        if (!process.WaitForExit((int)timeout.TotalMilliseconds)) { process.Kill(true); throw new TimeoutException("编辑器命令执行超时"); }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "编辑器命令执行失败" : error.Trim());
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static PluginExtensionData ExecuteActivate(EditorPluginPackage package, TimeSpan timeout)
    {
        var entry = PluginManager.ResolveEntryPath(package);
        var script = "const m=await import('node:url').then(x=>x.pathToFileURL(process.argv[1]).href);" +
            "const p=await import(m);const a=[],c=[],u=[];const h={registerAutomation:(id,name)=>a.push({id,name}),registerCommand:(id,name)=>c.push({id,name}),registerPanel:(id,name)=>u.push({id,name})};" +
            "if(typeof p.activate==='function')await p.activate(h);process.stdout.write(JSON.stringify({automations:a,commands:c,panels:u}));";
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--permission");
        startInfo.ArgumentList.Add($"--allow-fs-read={package.RootDirectory}");
        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(entry);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 JavaScript 运行时");
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(true);
            throw new TimeoutException("插件启动超时");
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "JavaScript 插件返回失败" : error.Trim());
        using var document = JsonDocument.Parse(output);
        return new PluginExtensionData(
            ReadExtensionItems(document.RootElement, "automations"),
            ReadExtensionItems(document.RootElement, "commands"),
            ReadExtensionItems(document.RootElement, "panels"));
    }

    private static IReadOnlyList<PluginExtensionItem> ReadExtensionItems(JsonElement root, string property)
    {
        return root.GetProperty(property).EnumerateArray()
            .Select(item => new PluginExtensionItem(item.GetProperty("id").GetString() ?? string.Empty, item.GetProperty("name").GetString() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)).ToList();
    }

    /// <summary>
    /// 停用当前运行时中的插件
    /// </summary>
    public void Dispose()
    {
        foreach (var package in _active) MVoxelLog.Info($"JavaScript 插件已停用：{package.Id}");
        _active.Clear();
        _automations.Clear();
        _automationOwners.Clear();
        _commands.Clear();
        _panels.Clear();
        _commandOwners.Clear();
    }

    private sealed record PluginExtensionItem(string Id, string Name);
    private sealed record PluginExtensionData(IReadOnlyList<PluginExtensionItem> Automations, IReadOnlyList<PluginExtensionItem> Commands, IReadOnlyList<PluginExtensionItem> Panels);
}
