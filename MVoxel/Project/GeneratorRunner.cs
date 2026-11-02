using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Jint;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Project;

/// <summary>
/// 在受限解释器中运行 .mvgenerator 生成器脚本，
/// 白名单 mvox 批量 API 全部由 C# 原生实现，脚本只能产出暂存体素差量
/// </summary>
public static class GeneratorRunner
{
    /// <summary>
    /// 包围盒单轴最大跨度
    /// </summary>
    public const int MaxBoundsSpan = 512;

    /// <summary>
    /// 包围盒坐标分量的最大绝对值
    /// </summary>
    public const long MaxCoordinateMagnitude = 1_000_000;

    /// <summary>
    /// 单次运行允许暂存的最大格子数，与洪水填充预算同级
    /// </summary>
    public const int DefaultMaxStagedCells = 2_000_000;

    /// <summary>
    /// 参数表上限
    /// </summary>
    public const int MaxParameters = 32;

    /// <summary>
    /// 模型本地材质槽上限，槽0为默认材质
    /// </summary>
    public const int MaxMaterialSlot = ushort.MaxValue;

    /// <summary>
    /// 只执行 generator.define 读取名称、包围盒与参数表，不执行 run
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="timeout">解析脚本的时间上限</param>
    /// <returns>生成器声明的元数据</returns>
    public static GeneratorDefinition Describe(GeneratorAsset asset, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var raw = asset.Language == GeneratorAsset.LuaLanguage
            ? CaptureLuaDefinition(asset, timeout)
            : CaptureJavaScriptDefinition(asset, timeout);
        return BuildDefinition(raw);
    }

    /// <summary>
    /// 运行生成器脚本，返回按坐标去重的暂存体素差量，不直接写入世界
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="readOnlyWorld">只读目标世界，replaceColor 据此匹配现有体素，可为空</param>
    /// <param name="parameters">参数标识到取值的映射，缺省项用脚本声明的默认值</param>
    /// <param name="timeout">脚本执行时间上限</param>
    /// <param name="maxCells">暂存格子数预算</param>
    /// <returns>坐标与目标体素值的列表，空值表示擦除</returns>
    public static IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Value)> Run(
        GeneratorAsset asset,
        VoxelWorld? readOnlyWorld,
        IDictionary<string, object?>? parameters,
        TimeSpan timeout,
        int maxCells = DefaultMaxStagedCells)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxCells <= 0) throw new ArgumentOutOfRangeException(nameof(maxCells));
        var definition = Describe(asset, timeout);
        var resolved = ResolveParameters(definition, parameters);
        var sink = new GeneratorSink(definition.BoundsMin, definition.BoundsMax, readOnlyWorld, maxCells, timeout);
        if (asset.Language == GeneratorAsset.LuaLanguage)
        {
            RunLua(asset, sink, resolved, timeout);
        }
        else
        {
            RunJavaScript(asset, sink, resolved, timeout);
        }

        sink.CheckDeadline();
        return sink.StagedCells();
    }

    /// <summary>
    /// 把脚本声明的原始字典校验并转换为生成器元数据
    /// </summary>
    /// <param name="raw">脚本捕获的 generator.define 入参</param>
    /// <returns>规范化的生成器定义</returns>
    private static GeneratorDefinition BuildDefinition(object? raw)
    {
        if (raw is not IDictionary<string, object?> definition)
        {
            throw new InvalidDataException("generator.define 必须接收一个声明对象");
        }

        var name = ReadString(definition, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("生成器必须声明 name");
        }

        name = name.Trim();
        if (name.Length > 64)
        {
            throw new InvalidDataException("生成器名称不能超过 64 个字符");
        }

        if (definition.TryGetValue("bounds", out var boundsValue) &&
            boundsValue is IDictionary<string, object?> bounds)
        {
            var minimum = ReadVector(bounds, "min");
            var maximum = ReadVector(bounds, "max");
            for (var axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(minimum[axis]) > MaxCoordinateMagnitude ||
                    Math.Abs(maximum[axis]) > MaxCoordinateMagnitude)
                {
                    throw new InvalidDataException("生成器包围盒坐标超出允许范围");
                }

                if (minimum[axis] > maximum[axis])
                {
                    throw new InvalidDataException("生成器包围盒 min 不能大于 max");
                }

                if (maximum[axis] - minimum[axis] > MaxBoundsSpan)
                {
                    throw new InvalidDataException($"生成器包围盒单轴跨度不能超过 {MaxBoundsSpan}");
                }
            }

            var parameters = ReadParameters(definition);
            return new GeneratorDefinition(
                name,
                new VoxelCoord64(minimum[0], minimum[1], minimum[2]),
                new VoxelCoord64(maximum[0], maximum[1], maximum[2]),
                parameters);
        }

        throw new InvalidDataException("生成器必须声明 bounds 包围盒");
    }

    /// <summary>
    /// 校验参数表并规范化默认值
    /// </summary>
    /// <param name="definition">声明对象字典</param>
    /// <returns>参数列表</returns>
    private static IReadOnlyList<GeneratorParameter> ReadParameters(IDictionary<string, object?> definition)
    {
        if (!definition.TryGetValue("parameters", out var rawParameters) || rawParameters is null) return [];
        // Lua 的空表既可表示数组也可表示对象，转换后为空字典。
        if (rawParameters is IDictionary<string, object?> { Count: 0 }) return [];
        if (rawParameters is not IList list)
            throw new InvalidDataException("生成器 parameters 必须是参数数组，Lua 请使用连续下标的表");

        if (list.Count > MaxParameters)
        {
            throw new InvalidDataException($"生成器参数数量不能超过 {MaxParameters}");
        }

        var parameters = new List<GeneratorParameter>(list.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            if (item is not IDictionary<string, object?> parameter)
            {
                throw new InvalidDataException("参数声明必须是对象");
            }

            var id = ReadString(parameter, "id");
            if (string.IsNullOrWhiteSpace(id) || !IsValidIdentifier(id))
            {
                throw new InvalidDataException($"参数 id 非法：{id}");
            }

            if (!ids.Add(id))
            {
                throw new InvalidDataException($"参数 id 重复：{id}");
            }

            var type = ReadString(parameter, "type") ?? GeneratorParameterType.Int;
            if (!GeneratorParameterType.All.Contains(type))
            {
                throw new InvalidDataException($"参数 {id} 的类型不受支持：{type}");
            }

            double? minimum = ReadOptionalNumber(parameter, "min");
            double? maximum = ReadOptionalNumber(parameter, "max");
            if (minimum is { } minValue && maximum is { } maxValue && minValue > maxValue)
            {
                throw new InvalidDataException($"参数 {id} 的 min 不能大于 max");
            }
            if (type == GeneratorParameterType.Int &&
                (minimum is < int.MinValue or > int.MaxValue || maximum is < int.MinValue or > int.MaxValue ||
                 (minimum is { } intMin && maximum is { } intMax && Math.Ceiling(intMin) > Math.Floor(intMax))))
            {
                throw new InvalidDataException($"整数参数 {id} 的范围必须包含可编辑的 32 位整数");
            }

            var label = parameter.TryGetValue("label", out var labelValue) ? labelValue?.ToString() : null;
            var defaultValue = parameter.TryGetValue("default", out var declaredDefault)
                ? declaredDefault : DefaultFor(type);
            var normalizedDefault = NormalizeParameterValue(id, type, defaultValue, minimum, maximum);
            parameters.Add(new GeneratorParameter
            {
                Id = id,
                Label = string.IsNullOrWhiteSpace(label) ? null : label,
                Type = type,
                Default = normalizedDefault,
                Min = minimum,
                Max = maximum,
            });
        }

        return parameters;
    }

    /// <summary>
    /// 按参数类型把外部取值与默认值规范化为脚本可消费的简单值
    /// </summary>
    /// <param name="id">参数标识</param>
    /// <param name="type">参数类型</param>
    /// <param name="value">原始取值</param>
    /// <param name="minimum">可选下限</param>
    /// <param name="maximum">可选上限</param>
    /// <returns>规范化后的值</returns>
    private static object? NormalizeParameterValue(string id, string type, object? value, double? minimum, double? maximum)
    {
        switch (type)
        {
            case GeneratorParameterType.Int:
            {
                var number = ToFiniteDouble(id, value);
                number = Math.Round(number, MidpointRounding.AwayFromZero);
                number = Math.Clamp(number, int.MinValue, int.MaxValue);
                if (minimum is { } minValue) number = Math.Max(number, Math.Ceiling(minValue));
                if (maximum is { } maxValue) number = Math.Min(number, Math.Floor(maxValue));
                return (long)number;
            }

            case GeneratorParameterType.Float:
            {
                var number = ToFiniteDouble(id, value);
                if (minimum is { } minValue) number = Math.Max(number, minValue);
                if (maximum is { } maxValue) number = Math.Min(number, maxValue);
                return number;
            }

            case GeneratorParameterType.Bool:
                return value as bool? ?? false;
            case GeneratorParameterType.String:
                return value?.ToString() ?? string.Empty;
            case GeneratorParameterType.Color:
            {
                var packed = ParseColor(value);
                return packed < 0 ? 0xFFFFFFL : packed & 0xFFFFFF;
            }
            default:
                throw new InvalidDataException($"参数 {id} 的类型不受支持：{type}");
        }
    }

    internal static object? NormalizeParameterValue(GeneratorParameter parameter, object? value) =>
        NormalizeParameterValue(parameter.Id, parameter.Type, value, parameter.Min, parameter.Max);

    /// <summary>
    /// 未声明默认值时按类型给空默认值
    /// </summary>
    /// <param name="type">参数类型</param>
    /// <returns>默认值</returns>
    private static object? DefaultFor(string type) => type switch
    {
        GeneratorParameterType.Bool => false,
        GeneratorParameterType.String => string.Empty,
        GeneratorParameterType.Color => 0xFFFFFFL,
        GeneratorParameterType.Float => 0D,
        _ => 0L,
    };

    /// <summary>
    /// 合并外部传入参数与脚本默认值，并做类型与范围规范化
    /// </summary>
    /// <param name="definition">生成器定义</param>
    /// <param name="supplied">外部传入的参数值</param>
    /// <returns>可直接序列化进脚本的参数表</returns>
    private static Dictionary<string, object?> ResolveParameters(
        GeneratorDefinition definition, IDictionary<string, object?>? supplied)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            var value = supplied is not null && supplied.TryGetValue(parameter.Id, out var suppliedValue)
                ? suppliedValue
                : parameter.Default;
            result[parameter.Id] =
                NormalizeParameterValue(parameter.Id, parameter.Type, value, parameter.Min, parameter.Max);
        }

        return result;
    }

    /// <summary>
    /// 解析颜色输入为 0xAARRGGBB 长整数，空输入返回 -1 表示擦除
    /// </summary>
    /// <param name="value">数字、十六进制字符串或三到四通道数组</param>
    /// <returns>打包颜色，-1 表示擦除</returns>
    internal static long ParseColor(object? value)
    {
        switch (value)
        {
            case null:
                return -1;
            case bool:
                throw new InvalidDataException("颜色不能是布尔值");
            case long packedLong:
                return packedLong & 0xFFFFFFFF;
            case int packedInt:
                return packedInt & 0xFFFFFFFF;
            case double packedDouble:
                if (!double.IsFinite(packedDouble)) throw new InvalidDataException("颜色数值非法");
                return (long)Math.Truncate(packedDouble) & 0xFFFFFFFF;
            case string text:
                return ParseColorText(text);
            case JsonElement element:
                return ParseJsonColor(element);
            case IList list:
            {
                if (list.Count is not (3 or 4))
                {
                    throw new InvalidDataException("颜色数组必须是 [r,g,b] 或 [r,g,b,a]");
                }

                var r = ToColorChannel(list[0]);
                var g = ToColorChannel(list[1]);
                var b = ToColorChannel(list[2]);
                var a = list.Count == 4 ? ToColorChannel(list[3]) : 255L;
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            default:
                throw new InvalidDataException($"无法识别的颜色形式：{value.GetType().Name}");
        }
    }

    /// <summary>
    /// 解析十六进制颜色文本，支持 #RGB、#RRGGBB、#AARRGGBB 与不带井号形式
    /// </summary>
    /// <param name="text">颜色文本</param>
    /// <returns>打包颜色</returns>
    private static long ParseColorText(string text)
    {
        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 3)
        {
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }

        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8 || !hex.All(IsHexDigit))
        {
            throw new InvalidDataException($"颜色字符串非法：{text}");
        }

        return Convert.ToInt64(hex, 16);
    }

    /// <summary>
    /// 解析 JSON 形态的颜色值
    /// </summary>
    /// <param name="element">JSON 元素</param>
    /// <returns>打包颜色</returns>
    private static long ParseJsonColor(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var packed):
                return packed & 0xFFFFFFFF;
            case JsonValueKind.String:
                return ParseColorText(element.GetString() ?? string.Empty);
            case JsonValueKind.Array:
            {
                var values = element.EnumerateArray().Select(item => item.GetDouble()).ToArray();
                return ParseColor(values);
            }
            default:
                throw new InvalidDataException("颜色参数必须是数字、字符串或数组");
        }
    }

    /// <summary>
    /// 把任意数值颜色通道夹到 0 到 255 的整数
    /// </summary>
    /// <param name="value">通道原始值</param>
    /// <returns>通道字节</returns>
    private static long ToColorChannel(object? value)
    {
        var number = ToFiniteDouble("颜色通道", value);
        return Math.Clamp((long)Math.Round(number, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>
    /// 判断字符串是否是合法参数标识符
    /// </summary>
    /// <param name="id">待判断文本</param>
    /// <returns>合法返回 true</returns>
    private static bool IsValidIdentifier(string id) =>
        id.Length <= 32 &&
        (char.IsLetter(id[0]) || id[0] == '_') &&
        id.All(character => char.IsLetterOrDigit(character) || character == '_');

    /// <summary>
    /// 判断字符是否为十六进制数字
    /// </summary>
    /// <param name="character">待判断字符</param>
    /// <returns>是十六进制数字返回 true</returns>
    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// 从声明字典读取字符串字段
    /// </summary>
    /// <param name="dictionary">声明字典</param>
    /// <param name="key">字段名</param>
    /// <returns>字符串值，缺失返回 null</returns>
    private static string? ReadString(IDictionary<string, object?> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// 从声明字典读取可选数值字段
    /// </summary>
    /// <param name="dictionary">声明字典</param>
    /// <param name="key">字段名</param>
    /// <returns>数值，缺失或非法返回 null</returns>
    private static double? ReadOptionalNumber(IDictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value) || value is null) return null;
        var number = ToFiniteDouble(key, value);
        return number;
    }

    /// <summary>
    /// 读取三轴向量，必须恰好包含三个有限数字
    /// </summary>
    /// <param name="dictionary">声明字典</param>
    /// <param name="key">字段名</param>
    /// <returns>三个 long 分量</returns>
    private static long[] ReadVector(IDictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value) || value is not IList list || list.Count != 3)
        {
            throw new InvalidDataException($"bounds.{key} 必须是三个数字");
        }

        var result = new long[3];
        for (var i = 0; i < 3; i++)
        {
            var number = ToFiniteDouble($"{key}[{i}]", list[i]);
            if (Math.Abs(number) > MaxCoordinateMagnitude)
                throw new InvalidDataException($"bounds.{key}[{i}] 坐标超出允许范围");
            result[i] = (long)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        return result;
    }

    /// <summary>
    /// 把脚本回传的数值安全转换为有限 double
    /// </summary>
    /// <param name="field">字段名，用于错误提示</param>
    /// <param name="value">原始值</param>
    /// <returns>有限 double</returns>
    private static double ToFiniteDouble(string field, object? value)
    {
        double number;
        switch (value)
        {
            case null:
                throw new InvalidDataException($"字段 {field} 缺失数值");
            case double doubleValue:
                number = doubleValue;
                break;
            case float floatValue:
                number = floatValue;
                break;
            case long longValue:
                number = longValue;
                break;
            case int intValue:
                number = intValue;
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                number = element.GetDouble();
                break;
            default:
                throw new InvalidDataException($"字段 {field} 必须是数字，实际为 {value.GetType().Name}");
        }

        if (!double.IsFinite(number))
        {
            throw new InvalidDataException($"字段 {field} 不是有限数字");
        }

        return number;
    }

    /// <summary>
    /// 去掉与插件运行时一致的 export 前缀，生成器源码按普通脚本执行
    /// </summary>
    /// <param name="source">原始源码</param>
    /// <returns>可直接执行的源码</returns>
    private static string ToJavaScriptSource(string source) =>
        source.Replace("export async function ", "async function ", StringComparison.Ordinal)
              .Replace("export function ", "function ", StringComparison.Ordinal);

    /// <summary>
    /// 只执行 JavaScript 源码并捕获 define 声明，不构造 ctx、不执行 run
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="timeout">执行时间上限</param>
    /// <returns>define 声明的原始对象</returns>
    private static object? CaptureJavaScriptDefinition(GeneratorAsset asset, TimeSpan timeout)
    {
        var engine = new Engine(options => options.TimeoutInterval(timeout).LimitRecursion(128));
        engine.Execute(JsDefinePreamble);
        engine.Execute(ToJavaScriptSource(asset.Source));
        var definition = engine.GetValue("__mvoxDefinition");
        if (definition.IsUndefined() || definition.IsNull())
        {
            throw new InvalidDataException("JavaScript 脚本没有调用 generator.define");
        }

        return definition.ToObject();
    }

    /// <summary>
    /// 运行 JavaScript 生成器
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="sink">暂存接收器</param>
    /// <param name="parameters">已规范化的参数表</param>
    /// <param name="timeout">执行时间上限</param>
    private static void RunJavaScript(GeneratorAsset asset, GeneratorSink sink,
        Dictionary<string, object?> parameters, TimeSpan timeout)
    {
        var engine = new Engine(options => options.TimeoutInterval(timeout).LimitRecursion(128));
        engine.SetValue("__mvoxParamsJson", JsonSerializer.Serialize(parameters));
        engine.Execute(JsDefinePreamble);
        engine.SetValue("__mvoxSink", sink);
        engine.Execute(JsContextPreamble);
        engine.Execute(ToJavaScriptSource(asset.Source));
        var definition = engine.GetValue("__mvoxDefinition");
        if (definition.IsUndefined() || definition.IsNull())
        {
            throw new InvalidDataException("JavaScript 脚本没有调用 generator.define");
        }

        engine.Execute("""
if (typeof __mvoxDefinition.run !== "function") {
    throw new Error("生成器没有声明 run 函数");
}
__mvoxDefinition.run(ctx, __mvoxParams);
""");
    }

    private const string JsDefinePreamble = """
var __mvoxDefinition = null;
var generator = {
    define: function (d) {
        if (__mvoxDefinition !== null) throw new Error("generator.define 只能调用一次");
        __mvoxDefinition = d;
    }
};
""";

    private const string JsContextPreamble = """
var __mvoxParams = JSON.parse(__mvoxParamsJson);
function __mvoxNumbers(values) {
    for (var i = 0; i < values.length; i++)
        if (typeof values[i] !== "number" || !Number.isFinite(values[i]))
            throw new Error("坐标和体素数据必须是有限数字");
}
function __mvoxOptional(value) {
    if (value === undefined || value === null) return 0;
    __mvoxNumbers([value]);
    return value;
}
function __mvoxColor(c) {
    if (c === null || c === undefined) return -1;
    if (typeof c === "number") { __mvoxNumbers([c]); return Math.trunc(c); }
    if (typeof c === "string") return __mvoxSink.ParseColor(c);
    if (Array.isArray(c)) {
        if (c.length !== 3 && c.length !== 4) throw new Error("颜色数组必须包含 3 或 4 个通道");
        __mvoxNumbers(c);
        var r = Math.trunc(c[0]) & 255;
        var g = Math.trunc(c[1]) & 255;
        var b = Math.trunc(c[2]) & 255;
        var a = c.length > 3 ? Math.trunc(c[3]) & 255 : 255;
        return a * 16777216 + r * 65536 + g * 256 + b;
    }
    throw new Error("颜色必须是数字、数组或十六进制字符串");
}
var ctx = {
    getVoxel: function (x, y, z) {
        __mvoxNumbers([x, y, z]);
        return JSON.parse(__mvoxSink.GetVoxelJson(x, y, z));
    },
    setVoxelData: function (x, y, z, voxel) {
        __mvoxNumbers([x, y, z]);
        if (voxel === null || voxel === undefined) { __mvoxSink.EraseVoxel(x, y, z); return; }
        __mvoxNumbers([voxel.color, voxel.slot, voxel.flags]);
        __mvoxSink.SetVoxelData(x, y, z, voxel.color, voxel.slot, voxel.flags);
    },
    setVoxel: function (x, y, z, c, slot, flags) {
        __mvoxNumbers([x, y, z]);
        __mvoxSink.SetVoxel(x, y, z, __mvoxColor(c), __mvoxOptional(slot), __mvoxOptional(flags));
    },
    fillBox: function (x0, y0, z0, x1, y1, z1, c, slot, flags) {
        __mvoxNumbers([x0, y0, z0, x1, y1, z1]);
        __mvoxSink.FillBox(x0, y0, z0, x1, y1, z1, __mvoxColor(c), __mvoxOptional(slot), __mvoxOptional(flags));
    },
    fillSphere: function (cx, cy, cz, radius, c, slot, flags) {
        __mvoxNumbers([cx, cy, cz, radius]);
        __mvoxSink.FillSphere(cx, cy, cz, radius, __mvoxColor(c), __mvoxOptional(slot), __mvoxOptional(flags));
    },
    eraseVoxel: function (x, y, z) {
        __mvoxNumbers([x, y, z]);
        __mvoxSink.EraseVoxel(x, y, z);
    },
    clearBox: function (x0, y0, z0, x1, y1, z1) {
        __mvoxNumbers([x0, y0, z0, x1, y1, z1]);
        __mvoxSink.ClearBox(x0, y0, z0, x1, y1, z1);
    },
    replaceColor: function (x0, y0, z0, x1, y1, z1, oldColor, newColor, slot, flags) {
        __mvoxNumbers([x0, y0, z0, x1, y1, z1]);
        __mvoxSink.ReplaceColor(x0, y0, z0, x1, y1, z1, __mvoxColor(oldColor), __mvoxColor(newColor), __mvoxOptional(slot), __mvoxOptional(flags));
    }
};
""";

    /// <summary>
    /// 只执行 Lua 源码并捕获 define 声明，不构造 ctx、不执行 run
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="timeout">执行时间上限</param>
    /// <returns>define 声明的原始表</returns>
    private static object? CaptureLuaDefinition(GeneratorAsset asset, TimeSpan timeout)
    {
        var script = CreateLuaScript(timeout);
        script.DoString(LuaDefinePreamble, null, "generator-preamble");
        script.DoString(asset.Source, null, asset.SourcePath);
        var definition = script.Globals.Get("__mvoxDefinition");
        if (definition.Type != DataType.Table)
        {
            throw new InvalidDataException("Lua 脚本没有调用 generator.define");
        }

        return LuaTableToObject(definition.Table);
    }

    /// <summary>
    /// 运行 Lua 生成器
    /// </summary>
    /// <param name="asset">生成器资产</param>
    /// <param name="sink">暂存接收器</param>
    /// <param name="parameters">已规范化的参数表</param>
    /// <param name="timeout">执行时间上限</param>
    private static void RunLua(GeneratorAsset asset, GeneratorSink sink,
        Dictionary<string, object?> parameters, TimeSpan timeout)
    {
        var script = CreateLuaScript(timeout);
        script.DoString(LuaDefinePreamble, null, "generator-preamble");
        script.Globals.Set("__mvoxSink", DynValue.NewTable(CreateLuaSinkTable(script, sink)));
        script.DoString(LuaContextPreamble, null, "generator-ctx");
        script.Globals.Set("__mvoxParams", ObjectToLua(script, parameters));
        script.DoString(asset.Source, null, asset.SourcePath);
        var definition = script.Globals.Get("__mvoxDefinition");
        if (definition.Type != DataType.Table)
        {
            throw new InvalidDataException("Lua 脚本没有调用 generator.define");
        }

        var run = definition.Table.Get("run");
        if (run.Type != DataType.Function)
        {
            throw new InvalidDataException("生成器没有声明 run 函数");
        }

        script.Call(run, script.Globals.Get("ctx"), script.Globals.Get("__mvoxParams"));
    }

    /// <summary>
    /// 创建带执行超时的受限 Lua 脚本环境
    /// </summary>
    /// <param name="timeout">单条语句执行上限</param>
    /// <returns>Lua 脚本环境</returns>
    private static Script CreateLuaScript(TimeSpan timeout)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.AttachDebugger(new LuaTimeoutDebugger(timeout));
        return script;
    }

    /// <summary>
    /// 把暂存接收器暴露为 Lua 回调表
    /// </summary>
    /// <param name="script">Lua 脚本环境</param>
    /// <param name="sink">暂存接收器</param>
    /// <returns>回调表</returns>
    private static Table CreateLuaSinkTable(Script script, GeneratorSink sink)
    {
        var table = new Table(script);
        table.Set("GetVoxel", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 3);
            var value = sink.GetVoxel(args[0].Number, args[1].Number, args[2].Number);
            if (value.IsEmpty) return DynValue.Nil;
            var result = new Table(script);
            result.Set("color", DynValue.NewNumber(PackVoxelColor(value)));
            result.Set("slot", DynValue.NewNumber(value.MaterialSlot));
            result.Set("flags", DynValue.NewNumber(value.Flags));
            return DynValue.NewTable(result);
        }));
        table.Set("SetVoxelData", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 6);
            sink.SetVoxelData(args[0].Number, args[1].Number, args[2].Number,
                args[3].Number, args[4].Number, args[5].Number);
            return DynValue.Nil;
        }));
        table.Set("SetVoxel", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 6);
            sink.SetVoxel(args[0].Number, args[1].Number, args[2].Number,
                args[3].Number, args[4].Number, args[5].Number);
            return DynValue.Nil;
        }));
        table.Set("FillBox", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 9);
            sink.FillBox(args[0].Number, args[1].Number, args[2].Number,
                args[3].Number, args[4].Number, args[5].Number,
                args[6].Number, args[7].Number, args[8].Number);
            return DynValue.Nil;
        }));
        table.Set("FillSphere", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 7);
            sink.FillSphere(args[0].Number, args[1].Number, args[2].Number, args[3].Number,
                args[4].Number, args[5].Number, args[6].Number);
            return DynValue.Nil;
        }));
        table.Set("EraseVoxel", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 3);
            sink.EraseVoxel(args[0].Number, args[1].Number, args[2].Number);
            return DynValue.Nil;
        }));
        table.Set("ClearBox", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 6);
            sink.ClearBox(args[0].Number, args[1].Number, args[2].Number,
                args[3].Number, args[4].Number, args[5].Number);
            return DynValue.Nil;
        }));
        table.Set("ReplaceColor", DynValue.NewCallback((_, args) =>
        {
            RequireLuaNumbers(args, 10);
            sink.ReplaceColor(args[0].Number, args[1].Number, args[2].Number,
                args[3].Number, args[4].Number, args[5].Number,
                args[6].Number, args[7].Number, args[8].Number, args[9].Number);
            return DynValue.Nil;
        }));
        table.Set("ParseColor", DynValue.NewCallback((_, args) =>
            DynValue.NewNumber(sink.ParseColor(args[0].String ?? string.Empty))));
        return table;
    }

    private static void RequireLuaNumbers(CallbackArguments args, int count)
    {
        for (var i = 0; i < count; i++)
            if (i >= args.Count || args[i].Type != DataType.Number)
                throw new InvalidDataException($"生成器参数 {i + 1} 必须是数字");
    }

    private const string LuaDefinePreamble = """
__mvoxDefinition = nil
generator = {
    define = function(d)
        if __mvoxDefinition ~= nil then error("generator.define 只能调用一次") end
        __mvoxDefinition = d
    end
}
""";

    private const string LuaContextPreamble = """
local function __mvoxnumber(value)
    if type(value) ~= "number" or value ~= value or value == math.huge or value == -math.huge then
        error("颜色通道必须是有限数字")
    end
    return value < 0 and math.ceil(value) or math.floor(value)
end
local function __mvoxoptional(value)
    if value == nil then return 0 end
    return value
end
local function __mvoxcolor(c)
    if c == nil then return -1 end
    local t = type(c)
    if t == "number" then return __mvoxnumber(c) end
    if t == "string" then return __mvoxSink.ParseColor(c) end
    if t == "table" then
        for key in pairs(c) do
            if type(key) ~= "number" or key ~= math.floor(key) or key < 1 or key > 4 then
                error("颜色数组必须包含 3 或 4 个通道")
            end
        end
        local r = __mvoxnumber(c[1]) % 256
        local g = __mvoxnumber(c[2]) % 256
        local b = __mvoxnumber(c[3]) % 256
        local a = c[4] ~= nil and __mvoxnumber(c[4]) % 256 or 255
        return a * 16777216 + r * 65536 + g * 256 + b
    end
    error("颜色必须是数字、数组或十六进制字符串")
end
ctx = {
    getVoxel = function(x, y, z)
        return __mvoxSink.GetVoxel(x, y, z)
    end,
    setVoxelData = function(x, y, z, voxel)
        if voxel == nil then __mvoxSink.EraseVoxel(x, y, z); return end
        __mvoxSink.SetVoxelData(x, y, z, voxel.color, voxel.slot, voxel.flags)
    end,
    setVoxel = function(x, y, z, c, slot, flags)
        __mvoxSink.SetVoxel(x, y, z, __mvoxcolor(c), __mvoxoptional(slot), __mvoxoptional(flags))
    end,
    fillBox = function(x0, y0, z0, x1, y1, z1, c, slot, flags)
        __mvoxSink.FillBox(x0, y0, z0, x1, y1, z1, __mvoxcolor(c), __mvoxoptional(slot), __mvoxoptional(flags))
    end,
    fillSphere = function(cx, cy, cz, radius, c, slot, flags)
        __mvoxSink.FillSphere(cx, cy, cz, radius, __mvoxcolor(c), __mvoxoptional(slot), __mvoxoptional(flags))
    end,
    eraseVoxel = function(x, y, z)
        __mvoxSink.EraseVoxel(x, y, z)
    end,
    clearBox = function(x0, y0, z0, x1, y1, z1)
        __mvoxSink.ClearBox(x0, y0, z0, x1, y1, z1)
    end,
    replaceColor = function(x0, y0, z0, x1, y1, z1, oldColor, newColor, slot, flags)
        __mvoxSink.ReplaceColor(x0, y0, z0, x1, y1, z1, __mvoxcolor(oldColor), __mvoxcolor(newColor), __mvoxoptional(slot), __mvoxoptional(flags))
    end
}
""";

    /// <summary>
    /// 把 C# 参数表转换为 Lua 表
    /// </summary>
    /// <param name="script">Lua 脚本环境</param>
    /// <param name="value">原始值</param>
    /// <returns>Lua 值</returns>
    private static DynValue ObjectToLua(Script script, object? value)
    {
        switch (value)
        {
            case null:
                return DynValue.Nil;
            case bool boolean:
                return DynValue.NewBoolean(boolean);
            case long or int or double or float:
                return DynValue.NewNumber(Convert.ToDouble(value));
            case string text:
                return DynValue.NewString(text);
            case IDictionary<string, object?> dictionary:
            {
                var table = new Table(script);
                foreach (var (key, item) in dictionary) table.Set(key, ObjectToLua(script, item));
                return DynValue.NewTable(table);
            }
            case IList list:
            {
                var table = new Table(script);
                for (var i = 0; i < list.Count; i++) table.Set(i + 1, ObjectToLua(script, list[i]));
                return DynValue.NewTable(table);
            }
            default:
                return DynValue.FromObject(script, value);
        }
    }

    /// <summary>
    /// 把 Lua 表转换为 C# 字典或列表，嵌套表按 1 起始连续数字键识别为列表
    /// </summary>
    /// <param name="table">Lua 表</param>
    /// <returns>字典、列表或简单值</returns>
    private static object? LuaTableToObject(Table table, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("Lua 生成器声明嵌套过深或包含循环引用");
        var pairs = table.Pairs.ToList();
        var isArray = pairs.Count > 0 && pairs.All(pair =>
            pair.Key.Type == DataType.Number &&
            pair.Key.Number >= 1 &&
            pair.Key.Number == Math.Truncate(pair.Key.Number));
        if (isArray)
        {
            var ordered = pairs.OrderBy(pair => pair.Key.Number).ToList();
            if (ordered.Select((pair, index) => (long)pair.Key.Number == index + 1).All(item => item))
            {
                return ordered.Select(pair => LuaValueToObject(pair.Value, depth + 1)).ToList();
            }
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var key = pair.Key.Type == DataType.String
                ? pair.Key.String
                : pair.Key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result[key!] = LuaValueToObject(pair.Value, depth + 1);
        }

        return result;
    }

    /// <summary>
    /// 把单个 Lua 值转换为 C# 简单值
    /// </summary>
    /// <param name="value">Lua 值</param>
    /// <returns>简单值</returns>
    private static object? LuaValueToObject(DynValue value, int depth) => value.Type switch
    {
        DataType.Nil or DataType.Void => null,
        DataType.Boolean => value.Boolean,
        DataType.Number => value.Number,
        DataType.String => value.String,
        DataType.Table => LuaTableToObject(value.Table, depth),
        _ => null,
    };

    /// <summary>
    /// Lua 执行超时调试器，超时在语句回调中抛异常终止脚本
    /// </summary>
    private sealed class LuaTimeoutDebugger : IDebugger
    {
        private readonly long _deadline;

        /// <summary>
        /// 按超时时限构造调试器
        /// </summary>
        /// <param name="timeout">执行时间上限</param>
        public LuaTimeoutDebugger(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            _deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        }

        /// <summary>
        /// 声明支持源码调试
        /// </summary>
        /// <returns>调试能力标志</returns>
        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;

        /// <summary>
        /// 不需要调试服务
        /// </summary>
        /// <param name="debugService">调试服务</param>
        public void SetDebugService(DebugService debugService) { }

        /// <summary>
        /// 不需要设置源码
        /// </summary>
        /// <param name="sourceCode">源码</param>
        public void SetSourceCode(SourceCode sourceCode) { }

        /// <summary>
        /// 不需要字节码
        /// </summary>
        /// <param name="byteCode">字节码</param>
        public void SetByteCode(string[] byteCode) { }

        /// <summary>
        /// 到达时限时请求暂停并抛超时异常
        /// </summary>
        /// <returns>是否应暂停</returns>
        public bool IsPauseRequested() => Stopwatch.GetTimestamp() >= _deadline;

        /// <summary>
        /// 运行时异常不拦截
        /// </summary>
        /// <param name="ex">脚本异常</param>
        /// <returns>始终 false</returns>
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;

        /// <summary>
        /// 每条语句检查时限
        /// </summary>
        /// <param name="ip">指令位置</param>
        /// <param name="sourceRef">源码位置</param>
        /// <returns>调试动作</returns>
        public DebuggerAction GetAction(int ip, SourceRef sourceRef)
        {
            if (IsPauseRequested()) throw new TimeoutException("Lua 生成器执行超时");
            return new DebuggerAction();
        }

        /// <summary>
        /// 执行结束无需处理
        /// </summary>
        public void SignalExecutionEnded() { }

        /// <summary>
        /// 不监视变量
        /// </summary>
        /// <param name="watchType">监视类型</param>
        /// <param name="items">监视项</param>
        public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }

        /// <summary>
        /// 没有监视项
        /// </summary>
        /// <returns>空监视列表</returns>
        public List<DynamicExpression> GetWatchItems() => [];

        /// <summary>
        /// 不处理断点
        /// </summary>
        /// <param name="refs">断点位置</param>
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }

    /// <summary>
    /// 脚本批量 API 的暂存接收器：校验坐标与预算，按坐标去重收集目标体素值
    /// </summary>
    private static long PackVoxelColor(VoxelValue value) =>
        ((long)value.Color.A << 24) | ((long)value.Color.R << 16) | ((long)value.Color.G << 8) | value.Color.B;

    internal sealed class GeneratorSink
    {
        private readonly VoxelCoord64 _minimum;
        private readonly VoxelCoord64 _maximum;
        private readonly VoxelWorld? _world;
        private readonly int _budget;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TimeSpan _timeout;
        private int _workCounter;
        private readonly Dictionary<VoxelCoord64, VoxelValue> _staged = new();

        /// <summary>
        /// 按包围盒、只读世界与预算构造接收器
        /// </summary>
        /// <param name="minimum">包围盒最小角</param>
        /// <param name="maximum">包围盒最大角</param>
        /// <param name="world">只读目标世界，replaceColor 使用</param>
        /// <param name="budget">暂存格子数上限</param>
        public GeneratorSink(VoxelCoord64 minimum, VoxelCoord64 maximum, VoxelWorld? world, int budget, TimeSpan timeout)
        {
            _minimum = minimum;
            _maximum = maximum;
            _world = world;
            _budget = budget;
            _timeout = timeout;
        }

        public void CheckDeadline()
        {
            if (Stopwatch.GetElapsedTime(_started) >= _timeout)
                throw new TimeoutException("生成器执行超时，已取消本次结果");
        }

        private void CheckWorkDeadline()
        {
            if ((++_workCounter & 4095) == 0) CheckDeadline();
        }

        /// <summary>
        /// 导出按坐标排序的暂存差量，保证结果确定性
        /// </summary>
        /// <returns>坐标与值的列表</returns>
        public IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Value)> StagedCells() =>
            _staged.OrderBy(item => item.Key.Z).ThenBy(item => item.Key.Y).ThenBy(item => item.Key.X)
                  .Select(item => (item.Key, item.Value))
                  .ToArray();

        public VoxelValue GetVoxel(double x, double y, double z)
        {
            CheckWorkDeadline();
            var coordinate = ToCoordinate(x, y, z);
            if (_staged.TryGetValue(coordinate, out var staged)) return staged;
            return _world is not null && _world.TryGet(coordinate, out var existing) ? existing : default;
        }

        public string GetVoxelJson(double x, double y, double z)
        {
            var value = GetVoxel(x, y, z);
            return value.IsEmpty ? "null" : JsonSerializer.Serialize(new
            {
                color = PackVoxelColor(value), slot = value.MaterialSlot, flags = value.Flags,
            });
        }

        /// <summary>按读取接口的完整 RGBA、材质槽和标志写入体素。</summary>
        public void SetVoxelData(double x, double y, double z, double packedColor, double materialSlot, double flags)
        {
            var packed = ToInteger(packedColor, "体素颜色");
            if (packed < 0 || packed > uint.MaxValue)
                throw new InvalidOperationException("体素颜色必须是 0 到 0xFFFFFFFF 的整数");
            var value = new VoxelValue(new Rgba32((byte)(packed >> 16), (byte)(packed >> 8),
                (byte)packed, (byte)(packed >> 24)), ToSlot(materialSlot), ToFlags(flags));
            Stage(ToCoordinate(x, y, z), value);
        }

        /// <summary>
        /// 暂存单格写入
        /// </summary>
        /// <param name="x">世界 X</param>
        /// <param name="y">世界 Y</param>
        /// <param name="z">世界 Z</param>
        /// <param name="packedColor">0xAARRGGBB 颜色，-1 或 alpha 为 0 表示擦除</param>
        /// <param name="materialSlot">材质槽</param>
        /// <param name="flags">体素标志位</param>
        public void SetVoxel(double x, double y, double z, double packedColor, double materialSlot, double flags)
        {
            Stage(ToCoordinate(x, y, z), ToValue(packedColor, materialSlot, flags));
        }

        /// <summary>
        /// 暂存轴对齐长方体填充
        /// </summary>
        public void FillBox(double x0, double y0, double z0, double x1, double y1, double z1,
            double packedColor, double materialSlot, double flags)
        {
            var value = ToValue(packedColor, materialSlot, flags);
            EnumerateBox(x0, y0, z0, x1, y1, z1, coordinate => Stage(coordinate, value));
        }

        /// <summary>
        /// 暂存球体填充，半径判定与编辑器球形笔刷一致
        /// </summary>
        public void FillSphere(double centerX, double centerY, double centerZ, double radius,
            double packedColor, double materialSlot, double flags)
        {
            var center = ToCoordinate(centerX, centerY, centerZ);
            var r = ToInteger(radius, "球体半径");
            if (r < 0) throw new InvalidOperationException("球体半径不能为负");
            if (r > MaxBoundsSpan || (2 * r + 1) * (2 * r + 1) * (2 * r + 1) > (long)_budget * 8)
                throw new InvalidOperationException("球体遍历范围过大，请缩小半径");
            var value = ToValue(packedColor, materialSlot, flags);
            var reach = r;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                CheckWorkDeadline();
                if (dx * dx + dy * dy + dz * dz > r * r - r * 0.5D) continue;
                Stage(new VoxelCoord64(center.X + dx, center.Y + dy, center.Z + dz), value);
            }
        }

        /// <summary>
        /// 暂存单格擦除
        /// </summary>
        public void EraseVoxel(double x, double y, double z) => Stage(ToCoordinate(x, y, z), default);

        /// <summary>
        /// 暂存长方体区域擦除
        /// </summary>
        public void ClearBox(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            EnumerateBox(x0, y0, z0, x1, y1, z1, coordinate => Stage(coordinate, default));
        }

        /// <summary>
        /// 在长方体内把匹配颜色的已占用体素替换为新颜色，省略材质槽与标志时保留原值
        /// </summary>
        public void ReplaceColor(double x0, double y0, double z0, double x1, double y1, double z1,
            double oldPackedColor, double newPackedColor, double materialSlot, double flags)
        {
            if (_world is null)
            {
                throw new InvalidOperationException("replaceColor 需要目标世界，但本次运行没有提供");
            }

            var oldColor = ToInteger(oldPackedColor, "待替换颜色");
            if (oldColor < 0 || oldColor > uint.MaxValue)
            {
                throw new InvalidOperationException("replaceColor 的待替换颜色必须在 0 到 0xFFFFFFFF 之间");
            }

            var oldRgb = (int)(oldColor & 0xFFFFFF);
            EnumerateBox(x0, y0, z0, x1, y1, z1, coordinate =>
            {
                var hasValue = _staged.TryGetValue(coordinate, out var existing) || _world.TryGet(coordinate, out existing);
                if (!hasValue || existing.IsEmpty) return;
                var existingRgb = (existing.Color.R << 16) | (existing.Color.G << 8) | existing.Color.B;
                if (existingRgb != oldRgb) return;
                var slot = materialSlot > 0 ? ToSlot(materialSlot) : existing.MaterialSlot;
                var voxelFlags = flags > 0 ? ToFlags(flags) : existing.Flags;
                Stage(coordinate, ToValue(newPackedColor, slot, voxelFlags));
            });
        }

        /// <summary>
        /// 供脚本预编译解析十六进制颜色字符串
        /// </summary>
        /// <param name="text">颜色文本</param>
        /// <returns>打包颜色</returns>
        public double ParseColor(string text) => ParseColorText(text);

        /// <summary>
        /// 暂存一格写入并执行预算检查，同一坐标以最后一次写入为准
        /// </summary>
        /// <param name="coordinate">世界坐标</param>
        /// <param name="value">目标值</param>
        private void Stage(VoxelCoord64 coordinate, VoxelValue value)
        {
            CheckWorkDeadline();
            EnsureInside(coordinate.X, coordinate.Y, coordinate.Z);
            _staged[coordinate] = value;
            if (_staged.Count > _budget)
            {
                throw new InvalidOperationException(
                    $"生成结果超过 {_budget} 格预算，请缩小包围范围或拆分生成步骤");
            }
        }

        /// <summary>
        /// 规范化长方体两角并逐格执行回调，遍历体积超出预算时提前拒绝
        /// </summary>
        private void EnumerateBox(double x0, double y0, double z0, double x1, double y1, double z1,
            Action<VoxelCoord64> action)
        {
            var minX = ToInteger(Math.Min(x0, x1), "长方体坐标");
            var minY = ToInteger(Math.Min(y0, y1), "长方体坐标");
            var minZ = ToInteger(Math.Min(z0, z1), "长方体坐标");
            var maxX = ToInteger(Math.Max(x0, x1), "长方体坐标");
            var maxY = ToInteger(Math.Max(y0, y1), "长方体坐标");
            var maxZ = ToInteger(Math.Max(z0, z1), "长方体坐标");
            EnsureInside(minX, minY, minZ);
            EnsureInside(maxX, maxY, maxZ);
            var width = maxX - minX + 1;
            var height = maxY - minY + 1;
            var depth = maxZ - minZ + 1;
            var volume = width * height * depth;
            if (volume > _budget)
            {
                throw new InvalidOperationException(
                    $"填充范围约 {volume} 格，超过 {_budget} 格预算，请缩小包围范围");
            }

            for (var z = minZ; z <= maxZ; z++)
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                CheckWorkDeadline();
                action(new VoxelCoord64(x, y, z));
            }
        }

        /// <summary>
        /// 把三个数字坐标转换为包围盒内的整数世界坐标
        /// </summary>
        private VoxelCoord64 ToCoordinate(double x, double y, double z)
        {
            var coordinate = new VoxelCoord64(
                ToInteger(x, "x 坐标"), ToInteger(y, "y 坐标"), ToInteger(z, "z 坐标"));
            EnsureInside(coordinate.X, coordinate.Y, coordinate.Z);
            return coordinate;
        }

        /// <summary>
        /// 断言坐标在声明包围盒内
        /// </summary>
        private void EnsureInside(long x, long y, long z)
        {
            if (x < _minimum.X || x > _maximum.X ||
                y < _minimum.Y || y > _maximum.Y ||
                z < _minimum.Z || z > _maximum.Z)
            {
                throw new InvalidOperationException(
                    $"坐标 ({x}, {y}, {z}) 超出声明的生成包围盒 " +
                    $"({_minimum.X}, {_minimum.Y}, {_minimum.Z}) 到 ({_maximum.X}, {_maximum.Y}, {_maximum.Z})");
            }
        }

        /// <summary>
        /// 把打包颜色与材质信息转换为体素值，-1 表示空气；
        /// 数字颜色按文档约定为 0xRRGGBB 不透明白，alpha 为 0 时补满不透明
        /// </summary>
        private static VoxelValue ToValue(double packedColor, double materialSlot, double flags)
        {
            var slot = ToSlot(materialSlot);
            var voxelFlags = ToFlags(flags);
            var packed = ToInteger(packedColor, "体素颜色");
            if (packed < 0) return default;
            if (packed > uint.MaxValue)
                throw new InvalidOperationException("体素颜色不能超过 0xFFFFFFFF");
            var alpha = (byte)((packed >> 24) & 255);
            if (alpha == 0) alpha = 255;
            var color = new Rgba32(
                (byte)((packed >> 16) & 255),
                (byte)((packed >> 8) & 255),
                (byte)(packed & 255),
                alpha);
            return new VoxelValue(color, slot, voxelFlags);
        }

        /// <summary>
        /// 校验模型本地材质槽为0到65535的整数
        /// </summary>
        private static ushort ToSlot(double materialSlot)
        {
            var slot = ToInteger(materialSlot, "材质槽");
            if (slot < 0 || slot > MaxMaterialSlot)
            {
                throw new InvalidOperationException($"材质槽必须在 0 到 {MaxMaterialSlot} 之间");
            }

            return (ushort)slot;
        }

        /// <summary>
        /// 校验并保留原生格式中的完整标志字节
        /// </summary>
        private static byte ToFlags(double flags)
        {
            var value = ToInteger(flags, "体素标志");
            if (value < 0 || value > byte.MaxValue)
            {
                throw new InvalidOperationException("体素标志必须在 0 到 255 之间");
            }

            return (byte)value;
        }

        /// <summary>
        /// 把脚本数字转换为整数，拒绝非整数与非有限值
        /// </summary>
        private static long ToInteger(double value, string field)
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidOperationException($"{field}必须是有限数字");
            }

            if (value < long.MinValue || value >= 9223372036854775808D)
                throw new InvalidOperationException($"{field}超出整数范围");

            var integer = (long)Math.Truncate(value);
            if (integer != value)
            {
                throw new InvalidOperationException($"{field}必须是整数，实际为 {value}");
            }

            return integer;
        }
    }
}
