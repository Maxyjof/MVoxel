using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Project;

/// <summary>
/// 项目内可共享的 .material 材质资产：定义路径追踪的表面属性，
/// 未填写的字段跟随全局渲染设置
/// </summary>
public sealed class MaterialAsset
{
    /// <summary>
    /// 原生体素材质槽容量上限，槽0为默认材质，运行时表按需分配
    /// </summary>
    public const int TableSlots = ushort.MaxValue + 1;

    /// <summary>
    /// 每个槽位的打包 float 数：粗糙度、金属度、发光强度倍率、吸收步长倍率、
    /// 吸收色 RGB、折射率、镜面强度，末位对齐占位
    /// </summary>
    public const int FloatsPerSlot = 16;

    /// <summary>
    /// 表示“跟随全局渲染设置”的哨兵值
    /// </summary>
    public const float FollowGlobal = -1F;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 文件格式版本
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 材质显示名
    /// </summary>
    public string Name { get; set; } = "材质";

    /// <summary>
    /// 粗糙度覆盖，null 表示跟随全局
    /// </summary>
    public float? Roughness { get; set; }

    /// <summary>
    /// 金属度覆盖，null 表示跟随全局
    /// </summary>
    public float? Metallic { get; set; }

    /// <summary>
    /// 发光强度倍率覆盖，null 表示跟随全局
    /// </summary>
    public float? EmissiveScale { get; set; }

    /// <summary>
    /// 半透明吸收步长倍率覆盖，null 表示跟随默认
    /// </summary>
    public float? AbsorptionStep { get; set; }

    /// <summary>
    /// 半透明吸收色覆盖，null 表示使用体素基础色
    /// </summary>
    public Rgba32? AbsorptionColor { get; set; }

    /// <summary>
    /// 折射率覆盖，null 表示跟随全局；1 表示不偏折，玻璃约 1.5，水约 1.33
    /// </summary>
    public float? Ior { get; set; }

    /// <summary>
    /// 镜面反射强度覆盖（菲涅尔 F0 倍率），null 表示跟随全局
    /// </summary>
    public float? Specular { get; set; }

    /// <summary>
    /// 半透明不透明度，null 使用体素颜色的 Alpha；0 清澈，1 吸收较强。
    /// </summary>
    public float? Opacity { get; set; }

    /// <summary>
    /// 介质内部散射强度，0 清澈，1 强烈雾化。
    /// </summary>
    public float? VolumeScattering { get; set; }

    /// <summary>独立的雾化密度，0 为无额外雾化，null 表示 0。</summary>
    public float? HazeDensity { get; set; }

    /// <summary>介质散射颜色，null 跟随体素颜色。</summary>
    public Rgba32? ScatteringColor { get; set; }

    /// <summary>
    /// 资产来源路径，运行期填充，不参与序列化
    /// </summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 默认材质：所有字段跟随全局渲染设置
    /// </summary>
    public static MaterialAsset CreateDefault(string name = "材质") => new() { Name = name };

    /// <summary>
    /// 从文件读取材质资产
    /// </summary>
    /// <param name="path">.material 文件路径</param>
    /// <returns>解析出的材质</returns>
    public static MaterialAsset Load(string path)
    {
        var material = Parse(File.ReadAllText(path));
        material.SourcePath = Path.GetFullPath(path);
        return material;
    }

    /// <summary>
    /// 从 JSON 文本解析材质并规范化字段
    /// </summary>
    /// <param name="json">文件文本</param>
    /// <returns>解析出的材质</returns>
    public static MaterialAsset Parse(string json)
    {
        var material = JsonSerializer.Deserialize<MaterialAsset>(json, JsonOptions)
            ?? throw new InvalidDataException("材质文件为空");
        material.Normalize();
        return material;
    }

    /// <summary>
    /// 原子保存材质资产
    /// </summary>
    /// <param name="path">.material 文件路径</param>
    public void Save(string path)
    {
        Normalize();
        AtomicFile.WriteText(path, JsonSerializer.Serialize(this, JsonOptions));
        SourcePath = Path.GetFullPath(path);
    }

    /// <summary>
    /// 把运行时槽位到材质资产的映射打包成GPU表，每槽16个float
    /// </summary>
    /// <param name="materials">
    /// 由模型组件材质槽映射生成的运行时槽位表
    /// </param>
    /// <returns>
    /// 按最高槽位分配的打包表，至少包含默认槽
    /// </returns>
    public static float[] PackTable(IReadOnlyDictionary<int, MaterialAsset> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var slotCount = 1;
        foreach (var slot in materials.Keys)
        {
            if (slot is < 0 or >= TableSlots)
                throw new ArgumentOutOfRangeException(nameof(materials), slot, "材质槽必须在0到65535之间");
            slotCount = Math.Max(slotCount, slot + 1);
        }

        var table = new float[slotCount * FloatsPerSlot];
        for (var slot = 0; slot < slotCount; slot++)
        {
            var base0 = slot * FloatsPerSlot;
            table[base0] = FollowGlobal;
            table[base0 + 1] = FollowGlobal;
            table[base0 + 2] = FollowGlobal;
            table[base0 + 3] = FollowGlobal;
            table[base0 + 8] = FollowGlobal;
            table[base0 + 9] = FollowGlobal;
            table[base0 + 10] = FollowGlobal;
            table[base0 + 11] = FollowGlobal;
            table[base0 + 12] = FollowGlobal;
            table[base0 + 15] = FollowGlobal;
        }

        foreach (var (slot, material) in materials)
        {
            var base0 = slot * FloatsPerSlot;
            table[base0] = material.Roughness ?? FollowGlobal;
            table[base0 + 1] = material.Metallic ?? FollowGlobal;
            table[base0 + 2] = material.EmissiveScale ?? FollowGlobal;
            table[base0 + 3] = material.AbsorptionStep ?? FollowGlobal;
            if (material.AbsorptionColor is { } color)
            {
                table[base0 + 4] = color.R / 255F;
                table[base0 + 5] = color.G / 255F;
                table[base0 + 6] = color.B / 255F;
                table[base0 + 7] = 1F;
            }

            table[base0 + 8] = material.Ior ?? FollowGlobal;
            table[base0 + 9] = material.Specular ?? FollowGlobal;
            table[base0 + 10] = material.Opacity ?? FollowGlobal;
            table[base0 + 11] = material.VolumeScattering ?? FollowGlobal;
            if (material.ScatteringColor is { } scatterColor)
            {
                table[base0 + 12] = scatterColor.R / 255F;
                table[base0 + 13] = scatterColor.G / 255F;
                table[base0 + 14] = scatterColor.B / 255F;
            }
            table[base0 + 15] = material.HazeDensity ?? FollowGlobal;
        }

        return table;
    }

    /// <summary>
    /// 从项目资产目录递归加载材质
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <returns>材质列表，损坏文件跳过并记日志</returns>
    public static List<MaterialAsset> LoadFromProject(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var materials = new List<MaterialAsset>();
        var directory = project.AssetsPath;
        if (!System.IO.Directory.Exists(directory)) return materials;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.material", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            try
            {
                materials.Add(Load(file));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                MVoxelLog.Warn($"材质文件损坏已跳过：{file}，{exception.Message}");
            }
        }

        return materials;
    }

    /// <summary>
    /// 规范化可空字段：非法值回退为跟随全局。
    /// </summary>
    public void Normalize()
    {
        Roughness = ClampOrFollow(Roughness, 0.01F, 1F);
        Metallic = ClampOrFollow(Metallic, 0F, 1F);
        EmissiveScale = ClampOrFollow(EmissiveScale, 0F, 32F);
        AbsorptionStep = ClampOrFollow(AbsorptionStep, 0F, 8F);
        Ior = ClampOrFollow(Ior, 1F, 2.5F);
        Specular = ClampOrFollow(Specular, 0F, 1F);
        Opacity = ClampOrFollow(Opacity, 0F, 1F);
        VolumeScattering = ClampOrFollow(VolumeScattering, 0F, 1F);
        HazeDensity = ClampOrFollow(HazeDensity, 0F, 8F);
        if (string.IsNullOrWhiteSpace(Name)) Name = "材质";
    }

    private static float? ClampOrFollow(float? value, float minimum, float maximum)
    {
        if (value is not { } number || !float.IsFinite(number)) return null;
        return Math.Clamp(number, minimum, maximum);
    }
}
