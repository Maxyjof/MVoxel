using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 生成器参数类型，决定编辑器参数区生成的控件与传参方式
/// </summary>
public static class GeneratorParameterType
{
    /// <summary>
    /// 整数参数
    /// </summary>
    public const string Int = "int";

    /// <summary>
    /// 浮点参数
    /// </summary>
    public const string Float = "float";

    /// <summary>
    /// 布尔开关参数
    /// </summary>
    public const string Bool = "bool";

    /// <summary>
    /// 文本参数
    /// </summary>
    public const string String = "string";

    /// <summary>
    /// RGB 颜色参数，以 0xRRGGBB 整数传入脚本
    /// </summary>
    public const string Color = "color";

    /// <summary>
    /// 全部合法参数类型
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Int, Float, Bool, String, Color };
}

/// <summary>
/// 生成器脚本声明的单个参数
/// </summary>
public sealed class GeneratorParameter
{
    /// <summary>
    /// 参数标识，脚本中以 p.id 访问，必须是合法标识符
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 参数区显示名，缺省时用 Id
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// 参数类型，取值见 <see cref="GeneratorParameterType"/>
    /// </summary>
    public string Type { get; set; } = GeneratorParameterType.Int;

    /// <summary>
    /// 默认值，类型随参数类型而定
    /// </summary>
    public object? Default { get; set; }

    /// <summary>
    /// 数值参数下限
    /// </summary>
    public double? Min { get; set; }

    /// <summary>
    /// 数值参数上限
    /// </summary>
    public double? Max { get; set; }
}

/// <summary>
/// 生成器脚本声明的元数据：名称、作用包围盒与参数表
/// </summary>
public sealed record GeneratorDefinition(
    string Name,
    VoxelCoord64 BoundsMin,
    VoxelCoord64 BoundsMax,
    IReadOnlyList<GeneratorParameter> Parameters);

/// <summary>
/// 项目内 .mvgenerator 生成器资产：单文件保存脚本语言与源码，
/// 名称、包围盒和参数由源码中的 generator.define 声明，运行时解析
/// </summary>
public sealed class GeneratorAsset
{
    /// <summary>
    /// 文件格式版本
    /// </summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>
    /// 当前文件格式版本
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// JavaScript 语言标识
    /// </summary>
    public const string JavaScriptLanguage = "javascript";

    /// <summary>
    /// Lua 语言标识
    /// </summary>
    public const string LuaLanguage = "lua";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 脚本语言，javascript 或 lua
    /// </summary>
    public string Language { get; set; } = JavaScriptLanguage;

    /// <summary>
    /// 脚本源码
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 资产来源路径，运行期填充，不参与序列化
    /// </summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 新建生成器的 JavaScript 模板源码
    /// </summary>
    public const string JavaScriptTemplate = """
generator.define({
    name: "我的生成器",
    bounds: { min: [-16, 0, -16], max: [16, 32, 16] },
    parameters: [
        { id: "size", label: "边长", type: "int", default: 6, min: 1, max: 16 },
        { id: "tint", label: "颜色", type: "color", default: 0x4aa3df }
    ],
    run: function (ctx, p) {
        ctx.fillBox(-p.size, 0, -p.size, p.size, p.size * 2, p.size, p.tint);
    }
});
""";

    /// <summary>
    /// 新建生成器的 Lua 模板源码
    /// </summary>
    public const string LuaTemplate = """
generator.define({
    name = "我的生成器",
    bounds = { min = { -16, 0, -16 }, max = { 16, 32, 16 } },
    parameters = {
        { id = "size", label = "边长", type = "int", default = 6, min = 1, max = 16 },
        { id = "tint", label = "颜色", type = "color", default = 0x4aa3df }
    },
    run = function(ctx, p)
        ctx.fillBox(-p.size, 0, -p.size, p.size, p.size * 2, p.size, p.tint)
    end
})
""";

    /// <summary>
    /// 从文件读取生成器资产
    /// </summary>
    /// <param name="path">.mvgenerator 文件路径</param>
    /// <returns>解析出的生成器</returns>
    public static GeneratorAsset Load(string path)
    {
        var asset = Parse(File.ReadAllText(path));
        asset.SourcePath = Path.GetFullPath(path);
        return asset;
    }

    /// <summary>
    /// 从 JSON 文本解析生成器资产并校验语言与版本
    /// </summary>
    /// <param name="json">文件文本</param>
    /// <returns>解析出的生成器</returns>
    public static GeneratorAsset Parse(string json)
    {
        GeneratorAsset asset;
        try
        {
            asset = JsonSerializer.Deserialize<GeneratorAsset>(json, JsonOptions)
                ?? throw new InvalidDataException("生成器文件为空");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"生成器文件不是合法 JSON：{exception.Message}");
        }

        if (asset.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"生成器文件版本 {asset.FormatVersion} 不受支持");
        }

        if (asset.Language is not (JavaScriptLanguage or LuaLanguage))
        {
            throw new InvalidDataException("生成器语言必须是 javascript 或 lua");
        }

        if (string.IsNullOrWhiteSpace(asset.Source))
        {
            throw new InvalidDataException("生成器源码为空");
        }

        return asset;
    }

    /// <summary>
    /// 原子保存生成器资产
    /// </summary>
    /// <param name="path">.mvgenerator 文件路径</param>
    public void Save(string path)
    {
        if (Language is not (JavaScriptLanguage or LuaLanguage))
        {
            throw new InvalidDataException("生成器语言必须是 javascript 或 lua");
        }

        AtomicFile.WriteText(path, JsonSerializer.Serialize(this, JsonOptions));
        SourcePath = Path.GetFullPath(path);
    }
}
