using System.Diagnostics;
using System.Text.Json;
using Jint;
using MoonSharp.Interpreter;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>单文件脚本的一行输出；Kind 为 log / warn / error / result</summary>
public sealed record OneShotScriptLine(string Kind, string Text);

/// <summary>单文件脚本的一次执行结果</summary>
public sealed record OneShotScriptResult(
    bool Success,
    IReadOnlyList<OneShotScriptLine> Lines,
    string? ReturnValue,
    string? Error,
    string? Status,
    double ElapsedMilliseconds,
    string FilePath,
    string Language);

/// <summary>单文件脚本执行请求：只携带主线程准备好的只读快照，便于后台线程安全执行</summary>
public sealed record OneShotScriptRequest(
    string FilePath,
    string Language,
    string Source,
    TimeSpan Timeout,
    string ProjectName,
    string ProjectPath,
    int SceneObjectCount,
    long VoxelCount,
    int SelectedObjectCount,
    int EnabledPluginCount);

/// <summary>
/// 一次性单文件脚本执行器。每次执行都新建受限解释器，不注册扩展、不常驻、
/// 不保留跨次状态，也不允许文件 / 网络 / 进程 / 反射；只提供日志输出与只读编辑器快照。
/// </summary>
public static class OneShotScriptRunner
{
    /// <summary>脚本源码长度上限</summary>
    public const int MaxSourceLength = 512_000;

    /// <summary>输出行数上限</summary>
    public const int MaxOutputLines = 500;

    private const int MaxLineLength = 2048;

    /// <summary>执行一份 JavaScript 或 Lua 源码</summary>
    /// <param name="request">执行请求</param>
    /// <returns>执行结果与全部输出</returns>
    public static OneShotScriptResult Execute(OneShotScriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = new List<OneShotScriptLine>();
        var watch = Stopwatch.StartNew();
        string? status = null;

        void AddLine(string kind, string? text)
        {
            if (lines.Count >= MaxOutputLines) return;
            var value = text ?? string.Empty;
            if (value.Length > MaxLineLength) value = value[..MaxLineLength] + "…";
            lines.Add(new OneShotScriptLine(kind, value));
        }

        try
        {
            if (request.Source.Length > MaxSourceLength)
                throw new InvalidDataException($"脚本超过 {MaxSourceLength / 1024} KiB 上限");
            if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(60))
                throw new ArgumentOutOfRangeException(nameof(request), "超时时间必须在 0 到 60 秒之间");

            var language = string.Equals(request.Language, "lua", StringComparison.OrdinalIgnoreCase)
                ? "Lua" : "JavaScript";
            string? returnValue;
            if (language == "Lua")
            {
                returnValue = RunLua(request, AddLine, text => status = text);
            }
            else
            {
                returnValue = RunJavaScript(request, AddLine, text => status = text);
            }

            watch.Stop();
            if (returnValue is not null) AddLine("result", "返回值：" + returnValue);
            MVoxelLog.Info($"一次性脚本执行完成：{request.FilePath}，{watch.Elapsed.TotalMilliseconds:0.0}ms");
            return new OneShotScriptResult(true, lines, returnValue, null, status,
                watch.Elapsed.TotalMilliseconds, request.FilePath, language);
        }
        catch (Exception exception)
        {
            watch.Stop();
            var message = exception.Message;
            AddLine("error", message);
            MVoxelLog.Warn($"一次性脚本执行失败：{request.FilePath}，原因：{message}");
            return new OneShotScriptResult(false, lines, null, message, status,
                watch.Elapsed.TotalMilliseconds, request.FilePath, request.Language);
        }
    }

    private static string RunJavaScript(OneShotScriptRequest request, Action<string, string?> addLine,
        Action<string> setStatus)
    {
        var engine = PluginScriptRuntime.CreateJavaScriptEngine(request.Timeout);
        engine.SetValue("__mvoxelLog", new Action<string>(text => addLine("log", text)));
        engine.SetValue("__mvoxelWarn", new Action<string>(text => addLine("warn", text)));
        engine.SetValue("__mvoxelError", new Action<string>(text => addLine("error", text)));
        engine.SetValue("__mvoxelStatus", new Action<string>(setStatus));
        engine.SetValue("__mvoxelContext", BuildContextJson(request));
        engine.Execute("""
function __mvoxelFormat(value) {
    if (typeof value === 'string') return value;
    try { return JSON.stringify(value); } catch (error) { return String(value); }
}
function __mvoxelJoin(args) { return Array.prototype.slice.call(args).map(__mvoxelFormat).join(' '); }
var console = {
    log: function() { __mvoxelLog(__mvoxelJoin(arguments)); },
    warn: function() { __mvoxelWarn(__mvoxelJoin(arguments)); },
    error: function() { __mvoxelError(__mvoxelJoin(arguments)); }
};
var mvoxel = JSON.parse(__mvoxelContext);
mvoxel.log = function(text) { __mvoxelLog(String(text)); };
mvoxel.status = function(text) { __mvoxelStatus(String(text)); };
""");
        var result = engine.Evaluate(PluginScriptRuntime.ToJavaScriptSource(request.Source));
        return result.IsNull() || result.IsUndefined() ? string.Empty : result.ToString();
    }

    private static string RunLua(OneShotScriptRequest request, Action<string, string?> addLine,
        Action<string> setStatus)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.AttachDebugger(new PluginScriptRuntime.LuaTimeoutDebugger(request.Timeout));
        var context = new Table(script);
        context["projectName"] = request.ProjectName;
        context["projectPath"] = request.ProjectPath;
        context["sceneObjectCount"] = request.SceneObjectCount;
        context["voxelCount"] = (double)request.VoxelCount;
        context["selectedObjectCount"] = request.SelectedObjectCount;
        context["enabledPluginCount"] = request.EnabledPluginCount;
        context["status"] = DynValue.NewCallback((_, args) =>
        {
            setStatus(args.Count > 0 ? args[0].ToPrintString() : string.Empty);
            return DynValue.Nil;
        });
        context["log"] = DynValue.NewCallback((_, args) =>
        {
            addLine("log", FormatLuaArguments(args));
            return DynValue.Nil;
        });
        script.Globals["mvoxel"] = context;
        script.Globals["print"] = DynValue.NewCallback((_, args) =>
        {
            addLine("log", FormatLuaArguments(args));
            return DynValue.Nil;
        });
        var result = script.DoString(request.Source, null, request.FilePath);
        // 没有 return 的脚本返回 Void，不要把 "void" 当成返回值显示给用户
        return result.Type == DataType.Void || result.IsNilOrNan() ? string.Empty : result.ToPrintString();
    }

    private static string FormatLuaArguments(CallbackArguments args)
    {
        var parts = new List<string>();
        for (var index = 0; index < args.Count; index++) parts.Add(args[index].ToPrintString());
        return string.Join("\t", parts);
    }

    private static string BuildContextJson(OneShotScriptRequest request) => JsonSerializer.Serialize(new
    {
        projectName = request.ProjectName,
        projectPath = request.ProjectPath,
        sceneObjectCount = request.SceneObjectCount,
        voxelCount = request.VoxelCount,
        selectedObjectCount = request.SelectedObjectCount,
        enabledPluginCount = request.EnabledPluginCount,
    });
}
