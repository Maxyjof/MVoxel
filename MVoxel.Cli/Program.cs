using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using MVoxel.Core;
using MVoxel.Project;

namespace MVoxel.Cli;

/// <summary>
/// MVoxel 命令行和 MCP stdio 服务入口
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 执行命令行入口
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <returns>进程退出码</returns>
    private static int Main(string[] args)
    {
        if (args.Any(item => item.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            RunMcp();
            return 0;
        }

        Console.Error.WriteLine("MVoxel CLI：使用 --mcp 启动 MCP stdio 服务");
        Console.Error.WriteLine("支持工具：project.create、project.info、project.listPlugins、project.enablePlugin、project.disablePlugin、plugin.install、editor.status、editor.openProject");
        return 0;
    }

    /// <summary>
    /// 按 MCP stdio 传输处理逐行 JSON-RPC 消息
    /// </summary>
    private static void RunMcp()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var methodElement)) continue;
                var method = methodElement.GetString() ?? string.Empty;
                if (method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
                JsonElement? id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;
                var result = Handle(method, root.TryGetProperty("params", out var parameters) ? parameters : default);
                WriteResponse(id, result, error: null);
            }
            catch (Exception exception)
            {
                WriteResponse(default, null, new { code = -32603, message = exception.Message });
            }
        }
    }

    /// <summary>
    /// 处理 MCP 初始化、工具列表和工具调用
    /// </summary>
    private static object Handle(string method, JsonElement parameters)
    {
        return method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "mvoxel-cli", version = "0.1.0" },
            },
            "ping" => new { },
            "tools/list" => new { tools = ToolDefinitions },
            "tools/call" => CallTool(parameters),
            _ => throw new InvalidOperationException($"不支持的 MCP 方法：{method}"),
        };
    }

    /// <summary>
    /// 调用一个项目和插件工具
    /// </summary>
    private static object CallTool(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString() ?? string.Empty;
        var arguments = parameters.TryGetProperty("arguments", out var value) ? value : default;
        object result = name switch
        {
            "project.create" => CreateProject(arguments),
            "project.info" => ProjectInfo(arguments),
            "project.listPlugins" => ListPlugins(arguments),
            "project.enablePlugin" => SetPlugin(arguments, true),
            "project.disablePlugin" => SetPlugin(arguments, false),
            "plugin.install" => InstallPlugin(arguments),
            "editor.status" => CallEditor("editor.status", arguments),
            "editor.openProject" => CallEditor("editor.openProject", arguments),
            _ => throw new InvalidOperationException($"未找到 MCP 工具：{name}"),
        };
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }, structuredContent = result };
    }

    /// <summary>
    /// 创建项目
    /// </summary>
    private static object CreateProject(JsonElement arguments) =>
        CreateProjectCore(arguments.GetProperty("path").GetString()!, arguments.TryGetProperty("name", out var name) ? name.GetString() : null);

    /// <summary>
    /// 创建项目并返回摘要
    /// </summary>
    private static object CreateProjectCore(string path, string? name)
    {
        var project = ProjectLayout.Create(path, name);
        return new { project.RootPath, project.Manifest.Name };
    }

    /// <summary>
    /// 读取项目摘要
    /// </summary>
    private static object ProjectInfo(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        return new { project.RootPath, project.Manifest.Name, project.Manifest.EngineVersion };
    }

    /// <summary>
    /// 读取编辑器级插件和项目启用状态
    /// </summary>
    private static object ListPlugins(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        return new { installed = PluginManager.ListInstalled(), enabled = PluginManager.ListEnabled(project) };
    }

    /// <summary>
    /// 修改项目插件启用状态
    /// </summary>
    private static object SetPlugin(JsonElement arguments, bool enabled)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        var pluginId = arguments.GetProperty("pluginId").GetString()!;
        if (enabled) PluginManager.Enable(project, pluginId);
        else PluginManager.Disable(project, pluginId);
        return new { project.RootPath, pluginId, enabled };
    }

    /// <summary>
    /// 安装一个编辑器级 JavaScript 插件目录
    /// </summary>
    private static object InstallPlugin(JsonElement arguments)
    {
        var package = PluginManager.InstallFromDirectory(arguments.GetProperty("sourceDirectory").GetString()!);
        return new { package.Id, package.Name, package.Version, package.Entry };
    }

    /// <summary>
    /// 通过程序目录下的端点文件调用运行中的编辑器
    /// </summary>
    /// <param name="method">编辑器命令名</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>编辑器返回结果</returns>
    private static object CallEditor(string method, JsonElement arguments)
    {
        if (!File.Exists(AppPaths.EditorEndpointPath)) throw new InvalidOperationException("没有发现运行中的 MVoxel 编辑器");
        var pipeName = File.ReadAllText(AppPaths.EditorEndpointPath).Trim();
        if (string.IsNullOrWhiteSpace(pipeName)) throw new InvalidDataException("编辑器控制端点为空");
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var request = new Dictionary<string, object?> { ["method"] = method, ["arguments"] = arguments };
        writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        var response = reader.ReadLine() ?? throw new IOException("编辑器控制端点没有返回结果");
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("编辑器控制命令失败");
        return document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : new { };
    }

    /// <summary>
    /// 写出单行 JSON-RPC 响应
    /// </summary>
    private static void WriteResponse(JsonElement? id, object? result, object? error)
    {
        object response = error is null
            ? new { jsonrpc = "2.0", id, result }
            : new { jsonrpc = "2.0", id, error };
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        Console.Out.Flush();
    }

    private static readonly object[] ToolDefinitions =
    [
        new { name = "project.create", description = "创建 MVoxel 项目", inputSchema = new { type = "object", properties = new { path = new { type = "string" }, name = new { type = "string" } }, required = new[] { "path" } } },
        new { name = "project.info", description = "读取项目摘要", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.listPlugins", description = "读取编辑器级插件和项目启用状态", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.enablePlugin", description = "在项目中启用编辑器级插件", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" }, pluginId = new { type = "string" } }, required = new[] { "projectPath", "pluginId" } } },
        new { name = "project.disablePlugin", description = "在项目中禁用编辑器级插件", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" }, pluginId = new { type = "string" } }, required = new[] { "projectPath", "pluginId" } } },
        new { name = "plugin.install", description = "安装编辑器级 JavaScript 插件", inputSchema = new { type = "object", properties = new { sourceDirectory = new { type = "string" } }, required = new[] { "sourceDirectory" } } },
        new { name = "editor.status", description = "读取运行中编辑器状态和插件自动化入口", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.openProject", description = "请求运行中的编辑器打开项目", inputSchema = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } },
    ];
}
