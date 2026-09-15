using System.Diagnostics;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 通过受限 Node.js 子进程执行 JavaScript 插件入口
/// </summary>
public sealed class JavaScriptPluginRuntime : IDisposable
{
    private readonly List<EditorPluginPackage> _active = new();

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
            try
            {
                ExecuteActivate(package, timeout);
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

    private static void ExecuteActivate(EditorPluginPackage package, TimeSpan timeout)
    {
        var entry = PluginManager.ResolveEntryPath(package);
        var script = "const m=await import('node:url').then(x=>x.pathToFileURL(process.argv[1]).href);" +
            "const p=await import(m);const a=[];const h={registerAutomation:(id,name)=>a.push({id,name})};" +
            "if(typeof p.activate==='function')await p.activate(h);process.stdout.write(JSON.stringify(a));";
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
        JsonDocument.Parse(output);
    }

    /// <summary>
    /// 停用当前运行时中的插件
    /// </summary>
    public void Dispose()
    {
        foreach (var package in _active) MVoxelLog.Info($"JavaScript 插件已停用：{package.Id}");
        _active.Clear();
    }
}
