using System.Diagnostics;
using System.Text.Json;
using MVoxel.Core;
using Jint;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

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

public sealed record PluginComponent(string Id, string Name, string Category, string PluginId);

/// <summary>
/// 通过随程序发布的受限 JavaScript 或 Lua 解释器执行插件入口
/// </summary>
public sealed class PluginScriptRuntime : IDisposable
{
    private readonly List<EditorPluginPackage> _active = new();
    private readonly List<PluginAutomation> _automations = new();
    private readonly Dictionary<string, EditorPluginPackage> _automationOwners = new(StringComparer.Ordinal);
    private readonly List<PluginCommand> _commands = new();
    private readonly List<PluginPanel> _panels = new();
    private readonly List<PluginComponent> _components = new();
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
    public IReadOnlyList<PluginComponent> Components => _components;

    /// <summary>
    /// 激活项目中已启用的 JavaScript 或 Lua 插件
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="timeout">单个插件启动时间上限</param>
    /// <returns>成功激活的插件</returns>
    public IReadOnlyList<EditorPluginPackage> Activate(ProjectHandle project, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var pluginId in PluginManager.ListEnabled(project))
        {
            if (_active.Any(item => item.Id == pluginId)) continue;
            var package = PluginManager.ListInstalled().FirstOrDefault(item => item.Id == pluginId);
            if (package is null)
            {
                MVoxelLog.Warn($"项目插件尚未安装，跳过激活：{pluginId}");
                continue;
            }
            if (!HasRegistrationPermission(package))
            {
                MVoxelLog.Warn($"插件没有声明任何扩展注册权限，跳过激活：{package.Id}");
                continue;
            }
            try
            {
                var extensions = ExecuteActivate(package, timeout);
                foreach (var item in extensions.Automations)
                {
                    if (!package.Permissions.Contains("automation.register", StringComparer.Ordinal)) continue;
                    if (_automationOwners.ContainsKey(item.Id))
                    {
                        MVoxelLog.Warn($"插件自动化 ID 冲突，跳过重复注册：{item.Id}");
                        continue;
                    }
                    var automation = new PluginAutomation(item.Id, item.Name, package.Id);
                    _automations.Add(automation);
                    _automationOwners[item.Id] = package;
                }
                foreach (var item in extensions.Commands)
                {
                    if (!package.Permissions.Contains("editor.command", StringComparer.Ordinal)) continue;
                    if (_commandOwners.ContainsKey(item.Id))
                    {
                        MVoxelLog.Warn($"插件命令 ID 冲突，跳过重复注册：{item.Id}");
                        continue;
                    }
                    _commands.Add(new PluginCommand(item.Id, item.Name, package.Id));
                    _commandOwners[item.Id] = package;
                }
                if (package.Permissions.Contains("editor.panel", StringComparer.Ordinal))
                {
                    foreach (var item in extensions.Panels)
                    {
                        if (_panels.Any(panel => panel.Id == item.Id))
                        {
                            MVoxelLog.Warn($"插件面板 ID 冲突，跳过重复注册：{item.Id}");
                            continue;
                        }
                        _panels.Add(new PluginPanel(item.Id, item.Name, package.Id));
                    }
                }
                if (package.Permissions.Contains("scene.component", StringComparer.Ordinal))
                {
                    foreach (var item in extensions.Components)
                    {
                        if (!IsValidComponentId(item.Id))
                        {
                            MVoxelLog.Warn($"插件组件 ID 无效，跳过注册：{item.Id}");
                            continue;
                        }
                        if (_components.Any(component => component.Id == item.Id)) continue;
                        _components.Add(new PluginComponent(item.Id, item.Name, item.Category, package.Id));
                    }
                }
                _active.Add(package);
                MVoxelLog.Info($"{GetLanguageName(package)} 插件已激活：{package.Id}");
            }
            catch (Exception exception)
            {
                MVoxelLog.Warn($"{GetLanguageName(package)} 插件激活失败：{package.Id}，原因：{exception.Message}");
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
        if (package.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            return InvokeLua(package, "runAutomation", automationId, arguments, timeout);
        return InvokeJavaScript(package, "runAutomation", automationId, arguments, timeout);
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
        if (package.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            return InvokeLua(package, "runCommand", commandId, arguments, timeout);
        return InvokeJavaScript(package, "runCommand", commandId, arguments, timeout);
    }

    /// <summary>
    /// 在随程序发布的受限解释器中执行插件激活入口并收集扩展注册
    /// </summary>
    /// <param name="package">插件包</param>
    /// <param name="timeout">执行时间上限</param>
    /// <returns>插件注册的扩展项</returns>
    private static PluginExtensionData ExecuteActivate(EditorPluginPackage package, TimeSpan timeout)
    {
        if (package.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            return ExecuteLuaActivate(package, timeout);
        var entry = PluginManager.ResolveEntryPath(package);
        var automations = new List<PluginExtensionItem>();
        var commands = new List<PluginExtensionItem>();
        var panels = new List<PluginExtensionItem>();
        var components = new List<PluginExtensionComponent>();
        var engine = CreateJavaScriptEngine(timeout);
        engine.SetValue("host", new ScriptHost(automations, commands, panels, components));
        engine.Execute(ToJavaScriptSource(File.ReadAllText(entry)));
        var activate = engine.GetValue("activate");
        if (!activate.IsUndefined() && !activate.IsNull()) engine.Invoke("activate", engine.GetValue("host"));
        return new PluginExtensionData(automations, commands, panels, components);
    }

    private static JsonElement InvokeJavaScript(EditorPluginPackage package, string functionName,
        string id, JsonElement arguments, TimeSpan timeout)
    {
        var entry = PluginManager.ResolveEntryPath(package);
        var engine = CreateJavaScriptEngine(timeout);
        engine.SetValue("__mvoxelArguments", arguments.GetRawText());
        engine.Execute(ToJavaScriptSource(File.ReadAllText(entry)));
        var function = engine.GetValue(functionName);
        if (function.IsUndefined() || function.IsNull())
            throw new InvalidOperationException($"JavaScript 插件没有导出 {functionName} 函数");
        engine.Execute("const __mvoxelPayload = JSON.parse(__mvoxelArguments);");
        var result = engine.Invoke(functionName, id, engine.GetValue("__mvoxelPayload"));
        var value = result.IsNull() || result.IsUndefined() ? null : result.ToObject();
        return JsonSerializer.SerializeToElement(value);
    }

    private static Engine CreateJavaScriptEngine(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return new Engine(options => options.TimeoutInterval(timeout).LimitRecursion(128));
    }

    private static string ToJavaScriptSource(string source) =>
        source.Replace("export async function ", "async function ", StringComparison.Ordinal)
             .Replace("export function ", "function ", StringComparison.Ordinal);

    private static string GetLanguageName(EditorPluginPackage package) =>
        package.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? "Lua" : "JavaScript";

    private sealed class ScriptHost
    {
        private readonly List<PluginExtensionItem> _automations;
        private readonly List<PluginExtensionItem> _commands;
        private readonly List<PluginExtensionItem> _panels;
        private readonly List<PluginExtensionComponent> _components;

        public ScriptHost(List<PluginExtensionItem> automations, List<PluginExtensionItem> commands,
            List<PluginExtensionItem> panels, List<PluginExtensionComponent> components)
        {
            _automations = automations;
            _commands = commands;
            _panels = panels;
            _components = components;
        }

        public void RegisterAutomation(string id, string name) => _automations.Add(new(id, name));
        public void RegisterCommand(string id, string name) => _commands.Add(new(id, name));
        public void RegisterPanel(string id, string name) => _panels.Add(new(id, name));
        public void RegisterComponent(string id, string name, string category = "插件") => _components.Add(new(id, name, category));
    }

    private static PluginExtensionData ExecuteLuaActivate(EditorPluginPackage package, TimeSpan timeout)
    {
        var entry = PluginManager.ResolveEntryPath(package);
        var script = new Script(CoreModules.Preset_SoftSandbox);
        var automations = new List<PluginExtensionItem>();
        var commands = new List<PluginExtensionItem>();
        var panels = new List<PluginExtensionItem>();
        var components = new List<PluginExtensionComponent>();
        var host = CreateLuaHost(script, automations, commands, panels, components);
        script.AttachDebugger(new LuaTimeoutDebugger(timeout));
        script.DoString(File.ReadAllText(entry), null, entry);
        var activate = script.Globals.Get("activate");
        if (activate.Type == DataType.Function) script.Call(activate, host);
        return new PluginExtensionData(automations, commands, panels, components);
    }

    private static JsonElement InvokeLua(EditorPluginPackage package, string functionName,
        string id, JsonElement arguments, TimeSpan timeout)
    {
        var entry = PluginManager.ResolveEntryPath(package);
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.AttachDebugger(new LuaTimeoutDebugger(timeout));
        script.DoString(File.ReadAllText(entry), null, entry);
        var function = script.Globals.Get(functionName);
        if (function.Type != DataType.Function)
            throw new InvalidOperationException($"Lua 插件没有导出 {functionName} 函数");
        var result = script.Call(function, DynValue.NewString(id), JsonToLua(script, arguments));
        return JsonSerializer.SerializeToElement(LuaToObject(result));
    }

    private static DynValue JsonToLua(Script script, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => DynValue.NewTable(CreateLuaTable(script, value.EnumerateObject().Select(item => (item.Name, item.Value)))),
            JsonValueKind.Array => DynValue.NewTable(CreateLuaArray(script, value)),
            JsonValueKind.String => DynValue.NewString(value.GetString() ?? string.Empty),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? DynValue.NewNumber(integer) : DynValue.NewNumber(value.GetDouble()),
            JsonValueKind.True => DynValue.NewBoolean(true),
            JsonValueKind.False => DynValue.NewBoolean(false),
            _ => DynValue.Nil,
        };
    }

    private static Table CreateLuaTable(Script script, IEnumerable<(string Key, JsonElement Value)> values)
    {
        var table = new Table(script);
        foreach (var (key, value) in values) table.Set(key, JsonToLua(script, value));
        return table;
    }

    private static Table CreateLuaArray(Script script, JsonElement value)
    {
        var table = new Table(script);
        var index = 1;
        foreach (var item in value.EnumerateArray()) table.Set(index++, JsonToLua(script, item));
        return table;
    }

    private static object? LuaToObject(DynValue value)
    {
        return value.Type switch
        {
            DataType.Nil or DataType.Void => null,
            DataType.Boolean => value.Boolean,
            DataType.Number => value.Number,
            DataType.String => value.String,
            DataType.Table => LuaTableToObject(value.Table),
            _ => value.ToObject(),
        };
    }

    private static object LuaTableToObject(Table table)
    {
        var pairs = table.Pairs.ToList();
        var isArray = pairs.Count > 0 && pairs.All(pair =>
            pair.Key.Type == DataType.Number && pair.Key.Number >= 1 &&
            pair.Key.Number == Math.Truncate(pair.Key.Number));
        if (isArray)
        {
            var ordered = pairs.OrderBy(pair => pair.Key.Number).ToList();
            if (ordered.Select((pair, index) => (long)pair.Key.Number == index + 1).All(item => item))
                return ordered.Select(pair => LuaToObject(pair.Value)).ToList();
        }
        return pairs.ToDictionary(
            pair => pair.Key.String ?? pair.Key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pair => LuaToObject(pair.Value), StringComparer.Ordinal);
    }

    private static Table CreateLuaHost(Script script, List<PluginExtensionItem> automations,
        List<PluginExtensionItem> commands, List<PluginExtensionItem> panels,
        List<PluginExtensionComponent> components)
    {
        var host = new Table(script);
        host.Set("registerAutomation", DynValue.NewCallback((_, args) =>
        {
            if (args.Count >= 2) automations.Add(new PluginExtensionItem(args[0].String ?? string.Empty, args[1].String ?? string.Empty));
            return DynValue.Nil;
        }));
        host.Set("registerCommand", DynValue.NewCallback((_, args) =>
        {
            if (args.Count >= 2) commands.Add(new PluginExtensionItem(args[0].String ?? string.Empty, args[1].String ?? string.Empty));
            return DynValue.Nil;
        }));
        host.Set("registerPanel", DynValue.NewCallback((_, args) =>
        {
            if (args.Count >= 2) panels.Add(new PluginExtensionItem(args[0].String ?? string.Empty, args[1].String ?? string.Empty));
            return DynValue.Nil;
        }));
        host.Set("registerComponent", DynValue.NewCallback((_, args) =>
        {
            if (args.Count >= 2)
                components.Add(new PluginExtensionComponent(args[0].String ?? string.Empty,
                    args[1].String ?? string.Empty, args.Count >= 3 ? args[2].String ?? "插件" : "插件"));
            return DynValue.Nil;
        }));
        return host;
    }

    private sealed class LuaTimeoutDebugger : IDebugger
    {
        private readonly long _deadline;

        public LuaTimeoutDebugger(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            _deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        }

        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;
        public void SetDebugService(DebugService debugService) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SetByteCode(string[] byteCode) { }
        public bool IsPauseRequested() => Stopwatch.GetTimestamp() >= _deadline;
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
        public DebuggerAction GetAction(int ip, SourceRef sourceRef)
        {
            if (IsPauseRequested()) throw new TimeoutException("Lua 脚本执行超时");
            return new DebuggerAction();
        }
        public void SignalExecutionEnded() { }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }
        public List<DynamicExpression> GetWatchItems() => [];
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }

    /// <summary>
    /// 从 JavaScript 返回值中读取有效扩展项
    /// </summary>
    /// <param name="root">插件返回的 JSON 根节点</param>
    /// <param name="property">扩展数组属性名</param>
    /// <returns>过滤后的扩展项</returns>
    private static IReadOnlyList<PluginExtensionItem> ReadExtensionItems(JsonElement root, string property)
    {
        return root.GetProperty(property).EnumerateArray()
            .Select(item => new PluginExtensionItem(item.GetProperty("id").GetString() ?? string.Empty, item.GetProperty("name").GetString() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)).ToList();
    }

    private static IReadOnlyList<PluginExtensionComponent> ReadComponentItems(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var property)) return [];
        return property.EnumerateArray()
            .Select(item => new PluginExtensionComponent(
                item.GetProperty("id").GetString() ?? string.Empty,
                item.GetProperty("name").GetString() ?? string.Empty,
                item.TryGetProperty("category", out var category) ? category.GetString() ?? "插件" : "插件"))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)).ToList();
    }

    private static bool IsValidComponentId(string id) =>
        id.Length >= 3 && id.Contains('.', StringComparison.Ordinal) &&
        id.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    internal static bool HasRegistrationPermission(EditorPluginPackage package) =>
        package.Permissions.Any(permission => permission is "automation.register" or "editor.command" or "editor.panel" or "scene.component");

    /// <summary>
    /// 停用当前运行时中的插件
    /// </summary>
    public void Dispose()
    {
        foreach (var package in _active) MVoxelLog.Info($"{GetLanguageName(package)} 插件已停用：{package.Id}");
        _active.Clear();
        _automations.Clear();
        _automationOwners.Clear();
        _commands.Clear();
        _panels.Clear();
        _components.Clear();
        _commandOwners.Clear();
    }

    /// <summary>
    /// 插件注册的单个扩展项
    /// </summary>
    private sealed record PluginExtensionItem(string Id, string Name);
    private sealed record PluginExtensionComponent(string Id, string Name, string Category);

    /// <summary>
    /// 插件激活阶段返回的扩展集合
    /// </summary>
    private sealed record PluginExtensionData(IReadOnlyList<PluginExtensionItem> Automations, IReadOnlyList<PluginExtensionItem> Commands, IReadOnlyList<PluginExtensionItem> Panels, IReadOnlyList<PluginExtensionComponent> Components);
}
