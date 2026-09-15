using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// MVoxel 内置场景组件的类型标识与数据读写入口
/// </summary>
public static class SceneComponentTypes
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 坐标组件的稳定类型标识
    /// </summary>
    public const string Transform = "mvoxel.transform";
    /// <summary>
    /// 体素模型组件的稳定类型标识
    /// </summary>
    public const string VoxelModel = "mvoxel.voxelmodel";
    /// <summary>
    /// 相机组件的稳定类型标识
    /// </summary>
    public const string Camera = "mvoxel.camera";
    /// <summary>
    /// 光照组件的稳定类型标识
    /// </summary>
    public const string Light = "mvoxel.light";

    public static readonly IReadOnlyList<SceneComponentDefinition> BuiltInDefinitions =
    [
        new(Transform, "坐标", "基础", true),
        new(VoxelModel, "模型", "体素", true),
        new(Camera, "相机", "视图", true),
        new(Light, "光照", "渲染", true),
    ];

    public static bool IsTransform(ComponentRecord component) => string.Equals(component.TypeId, Transform, StringComparison.Ordinal);
    public static bool IsVoxelModel(ComponentRecord component) => string.Equals(component.TypeId, VoxelModel, StringComparison.Ordinal);
    public static bool IsLight(ComponentRecord component) => string.Equals(component.TypeId, Light, StringComparison.Ordinal);

    public static ComponentRecord CreateTransform(TransformComponent transform) => new()
    {
        TypeId = Transform,
        SchemaVersion = 1,
        Data = JsonSerializer.SerializeToElement(new { kind = transform.Mode.ToString() }, JsonOptions),
    };

    public static ComponentRecord CreateVoxelModel(string model, int voxelCount, string template, string part) => new()
    {
        TypeId = VoxelModel,
        SchemaVersion = 1,
        Data = JsonSerializer.SerializeToElement(new { model, voxelCount, template, part }, JsonOptions),
    };

    public static ComponentRecord CreateLight(LightComponentData data) => new()
    {
        TypeId = Light,
        SchemaVersion = 1,
        Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions),
    };

    /// <summary>
    /// 读取光照组件并规范化数据
    /// </summary>
    public static LightComponentData ReadLight(ComponentRecord component)
    {
        if (!IsLight(component) || component.Data.ValueKind != JsonValueKind.Object)
            return LightComponentData.Default;
        var data = component.Data;
        return new LightComponentData(
            ReadFloat(data, "intensity", 1F),
            ReadFloat(data, "colorR", 1F),
            ReadFloat(data, "colorG", 1F),
            ReadFloat(data, "colorB", 1F),
            !data.TryGetProperty("castShadows", out var shadows) ||
            shadows.ValueKind != JsonValueKind.False);
    }

    /// <summary>
    /// 写回光照组件数据
    /// </summary>
    public static void WriteLight(ComponentRecord component, LightComponentData data)
    {
        if (!IsLight(component)) throw new ArgumentException("组件记录不是光照组件", nameof(component));
        component.Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions);
    }

    public static string GetDisplayName(ComponentRecord component) =>
        BuiltInDefinitions.FirstOrDefault(item => item.TypeId == component.TypeId)?.DisplayName ?? component.TypeId;

    /// <summary>
    /// 判断组件记录是否为内置相机组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为相机组件</returns>
    public static bool IsCamera(ComponentRecord component) =>
        string.Equals(component.TypeId, Camera, StringComparison.Ordinal);

    /// <summary>
    /// 读取相机组件的摄影参数
    /// </summary>
    /// <param name="component">相机组件记录</param>
    /// <returns>经过范围约束的相机参数</returns>
    public static CameraComponentData ReadCamera(ComponentRecord component)
    {
        if (!IsCamera(component) || component.Data.ValueKind != JsonValueKind.Object)
        {
            return CameraComponentData.Default;
        }

        var data = component.Data;
        return new CameraComponentData(
            ReadFloat(data, "fieldOfViewDegrees", CameraComponentData.Default.FieldOfViewDegrees),
            ReadFloat(data, "nearClip", CameraComponentData.Default.NearClip),
            ReadFloat(data, "farClip", CameraComponentData.Default.FarClip)).Normalize();
    }

    /// <summary>
    /// 创建一个带默认摄影参数的相机组件记录
    /// </summary>
    /// <returns>相机组件记录</returns>
    public static ComponentRecord CreateCamera() => new()
    {
        TypeId = Camera,
        SchemaVersion = 1,
        Data = JsonSerializer.SerializeToElement(CameraComponentData.Default, JsonOptions),
    };

    /// <summary>
    /// 创建带指定摄影参数的相机组件记录
    /// </summary>
    /// <param name="data">摄影参数</param>
    /// <returns>相机组件记录</returns>
    public static ComponentRecord CreateCamera(CameraComponentData data) => new()
    {
        TypeId = Camera,
        SchemaVersion = 1,
        Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions),
    };

    /// <summary>
    /// 写回已有相机组件的摄影参数并保留未知字段
    /// </summary>
    /// <param name="component">相机组件记录</param>
    /// <param name="data">新的摄影参数</param>
    public static void WriteCamera(ComponentRecord component, CameraComponentData data)
    {
        if (!IsCamera(component))
        {
            throw new ArgumentException("组件记录不是相机组件", nameof(component));
        }

        component.Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions);
    }

    /// <summary>
    /// 从组件数据中读取有限单精度字段
    /// </summary>
    /// <param name="data">组件 JSON 数据</param>
    /// <param name="name">字段名称</param>
    /// <param name="fallback">字段缺失时的默认值</param>
    /// <returns>读取到的有限数值</returns>
    private static float ReadFloat(JsonElement data, string name, float fallback)
    {
        return data.TryGetProperty(name, out var property) && property.TryGetSingle(out var value) &&
               float.IsFinite(value)
            ? value
            : fallback;
    }
}

public sealed record SceneComponentDefinition(string TypeId, string DisplayName, string Category, bool BuiltIn);

public readonly record struct LightComponentData(
    float Intensity,
    float ColorR,
    float ColorG,
    float ColorB,
    bool CastShadows)
{
    public static LightComponentData Default => new(1F, 1F, 1F, 1F, true);

    public LightComponentData Normalize() => new(
        Math.Clamp(float.IsFinite(Intensity) ? Intensity : 1F, 0F, 1000F),
        Math.Clamp(float.IsFinite(ColorR) ? ColorR : 1F, 0F, 1F),
        Math.Clamp(float.IsFinite(ColorG) ? ColorG : 1F, 0F, 1F),
        Math.Clamp(float.IsFinite(ColorB) ? ColorB : 1F, 0F, 1F),
        CastShadows);
}

/// <summary>
/// 相机组件保存的连续摄影参数
/// </summary>
/// <param name="FieldOfViewDegrees">垂直视场角</param>
/// <param name="NearClip">近平面距离</param>
/// <param name="FarClip">远平面距离</param>
public readonly record struct CameraComponentData(
    float FieldOfViewDegrees,
    float NearClip,
    float FarClip)
{
    /// <summary>
    /// 默认相机摄影参数
    /// </summary>
    public static CameraComponentData Default => new(50F, 0.05F, 100000F);

    /// <summary>
    /// 将摄影参数限制到渲染管线支持的范围
    /// </summary>
    /// <returns>规范化后的摄影参数</returns>
    public CameraComponentData Normalize()
    {
        var near = Math.Clamp(float.IsFinite(NearClip) ? NearClip : Default.NearClip, 0.001F, 1_000_000F);
        var far = Math.Clamp(float.IsFinite(FarClip) ? FarClip : Default.FarClip, near + 0.001F, 100_000_000F);
        var fov = Math.Clamp(float.IsFinite(FieldOfViewDegrees) ? FieldOfViewDegrees : Default.FieldOfViewDegrees,
            20F, 120F);
        return new CameraComponentData(fov, near, far);
    }
}
