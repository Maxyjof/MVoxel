using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Rendering;

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
    /// <summary>体积光照探针组件的稳定类型标识</summary>
    public const string ProbeVolume = "mvoxel.probevolume";

    public static readonly IReadOnlyList<SceneComponentDefinition> BuiltInDefinitions =
    [
        new(Transform, "变换", "基础", true),
        new(VoxelModel, "模型", "体素", true),
        new(Camera, "相机", "视图", true),
        new(Light, "光照", "渲染", true),
        new(ProbeVolume, "自动探针", "渲染", true),
    ];

    public static bool IsTransform(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, Transform, StringComparison.Ordinal);
    public static bool IsVoxelModel(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, VoxelModel, StringComparison.Ordinal);
    public static bool IsLight(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, Light, StringComparison.Ordinal);
    public static bool IsProbeVolume(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, ProbeVolume, StringComparison.Ordinal);

    public static ComponentRecord CreateProbeVolume(ProbeVolumeComponentData data) => new()
    {
        TypeId = ProbeVolume, SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions),
    };

    public static ProbeVolumeComponentData ReadProbeVolume(ComponentRecord component)
    {
        if (!IsProbeVolume(component) || component.Data.ValueKind != JsonValueKind.Object) return ProbeVolumeComponentData.Default;
        var data = component.Data;
        return new ProbeVolumeComponentData(
            ReadFloat(data, "offsetX", 0F), ReadFloat(data, "offsetY", 0F), ReadFloat(data, "offsetZ", 0F),
            ReadFloat(data, "sizeX", 16F), ReadFloat(data, "sizeY", 16F), ReadFloat(data, "sizeZ", 16F),
            ReadFloat(data, "density", 1F), ReadBool(data, "autoBake", true), ReadBool(data, "editing", false),
            ReadString(data, "bakedAsset")).Normalize();
    }

    public static void WriteProbeVolume(ComponentRecord component, ProbeVolumeComponentData data)
    {
        if (!IsProbeVolume(component)) throw new ArgumentException("组件记录不是探针组件", nameof(component));
        component.Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions);
    }

    /// <summary>
    /// 判断类型 ID 是否属于宿主内置组件，供插件注册校验和检查器分类共用。
    /// </summary>
    public static bool IsBuiltInTypeId(string? typeId) =>
        !string.IsNullOrWhiteSpace(typeId) && BuiltInDefinitions.Any(item => item.TypeId == typeId);

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

    /// <summary>
    /// 读取体素模型组件的资源引用信息
    /// </summary>
    public static VoxelModelComponentData ReadVoxelModel(ComponentRecord component)
    {
        if (!IsVoxelModel(component) || component.Data.ValueKind != JsonValueKind.Object)
            return VoxelModelComponentData.Default;
        var data = component.Data;
        return new VoxelModelComponentData(
            ReadString(data, "model"),
            ReadString(data, "part", "模型"),
            data.TryGetProperty("voxelCount", out var count) && count.TryGetInt32(out var voxelCount)
                ? Math.Max(0, voxelCount)
                : 0,
            ReadString(data, "template"),
            ReadModelScale(data, "scaleX"),
            ReadModelScale(data, "scaleY"),
            ReadModelScale(data, "scaleZ"),
            ReadString(data, "sortingLayer", "默认"),
            data.TryGetProperty("sortingOrder", out var order) && order.TryGetInt32(out var orderValue) ? orderValue : 0,
            ReadMaterialSlots(data));
    }

    /// <summary>
    /// 写回体素模型组件的资源引用信息
    /// </summary>
    public static void WriteVoxelModel(ComponentRecord component, VoxelModelComponentData data)
    {
        if (!IsVoxelModel(component)) throw new ArgumentException("组件记录不是体素模型组件", nameof(component));
        var updated = JsonSerializer.SerializeToNode(data with
        {
            Model = data.Model.Trim(),
            Part = string.IsNullOrWhiteSpace(data.Part) ? "模型" : data.Part.Trim(),
            VoxelCount = Math.Max(0, data.VoxelCount),
            ScaleX = ClampModelScale(data.ScaleX),
            ScaleY = ClampModelScale(data.ScaleY),
            ScaleZ = ClampModelScale(data.ScaleZ),
            SortingLayer = string.IsNullOrWhiteSpace(data.SortingLayer) ? "默认" : data.SortingLayer.Trim(),
            MaterialSlots = NormalizeMaterialSlots(data.MaterialSlots),
        }, JsonOptions)!.AsObject();
        // 旧版本只认单个 scale，这里补一份，用旧版打开时至少能看到大致倍数
        updated["scale"] = JsonValue.Create(ClampModelScale(data.ScaleX));
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        foreach (var property in updated)
            merged[property.Key] = property.Value?.DeepClone();
        if (!string.IsNullOrWhiteSpace(data.Model)) merged.Remove(EmbeddedVoxelData.PropertyName);
        component.Data = JsonSerializer.SerializeToElement(merged, JsonOptions);
    }

    /// <summary>
    /// 读取单轴缩放倍数；没有分轴字段的旧文件回退到单个 scale
    /// </summary>
    private static float ReadModelScale(JsonElement data, string key)
    {
        if (data.TryGetProperty(key, out var value) && value.TryGetSingle(out var scale)) return ClampModelScale(scale);
        if (data.TryGetProperty("scale", out var legacy) && legacy.TryGetSingle(out var legacyScale)) return ClampModelScale(legacyScale);
        return 1F;
    }

    /// <summary>倍数统一夹在缩放下限与上限之间</summary>
    private static float ClampModelScale(float scale) =>
        float.IsFinite(scale) ? Math.Clamp(scale, 0.125F, 8F) : 1F;

    private static MaterialSlotBinding[] ReadMaterialSlots(JsonElement data)
    {
        if (!data.TryGetProperty("materialSlots", out var property) || property.ValueKind != JsonValueKind.Array)
            return [];
        try { return NormalizeMaterialSlots(property.Deserialize<MaterialSlotBinding[]>(JsonOptions) ?? []); }
        catch (JsonException) { return []; }
    }

    //仅规范化槽号和路径文本，缺失资产的引用保留以便重新绑定或恢复
    private static MaterialSlotBinding[] NormalizeMaterialSlots(IEnumerable<MaterialSlotBinding>? slots) =>
        (slots ?? []).Where(item => item.Slot is > 0 and <= ushort.MaxValue &&
                                     !string.IsNullOrWhiteSpace(item.MaterialAsset))
            .GroupBy(item => item.Slot).Select(group => group.Last())
            .Select(item => new MaterialSlotBinding(item.Slot, item.MaterialAsset.Replace('\\', '/').Trim()))
            .OrderBy(item => item.Slot).ToArray();

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
        var shape = ReadInt(data, "shape", 0) is var shapeIndex && Enum.IsDefined((LightShape)shapeIndex)
            ? (LightShape)shapeIndex : LightShape.Point;
        var profileIndex = ReadInt(data, "profile", 0);
        var profile = Enum.IsDefined((LightProfile)profileIndex) ? (LightProfile)profileIndex : LightProfile.Default;
        return new LightComponentData(
            shape,
            ReadFloat(data, "intensity", 1F),
            ReadFloat(data, "colorR", 1F),
            ReadFloat(data, "colorG", 1F),
            ReadFloat(data, "colorB", 1F),
            ReadFloat(data, "range", 32F),
            ReadFloat(data, "spotAngleDegrees", 45F),
            ReadFloat(data, "innerSpotAngleDegrees", 25F),
            data.TryGetProperty("castShadows", out var shadows) &&
            shadows.ValueKind == JsonValueKind.True,
            profile).Normalize();
    }

    /// <summary>
    /// 写回光照组件数据
    /// </summary>
    public static void WriteLight(ComponentRecord component, LightComponentData data)
    {
        if (!IsLight(component)) throw new ArgumentException("组件记录不是光照组件", nameof(component));
        component.Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions);
    }

    /// <summary>
    /// 序列化相机组件数据。渲染参数统一由项目配置提供，相机只保存摄影参数与图层遮罩。
    /// </summary>
    private static JsonElement SerializeCameraData(CameraComponentData data)
    {
        var normalized = data.Normalize();
        return JsonSerializer.SerializeToElement(new
        {
            fieldOfViewDegrees = normalized.FieldOfViewDegrees,
            nearClip = normalized.NearClip,
            farClip = normalized.FarClip,
            cullingMask = normalized.CullingMask,
            postProcess = normalized.PostProcess,
        }, JsonOptions);
    }

    public static string GetDisplayName(ComponentRecord component) =>
        BuiltInDefinitions.FirstOrDefault(item => item.TypeId == component.TypeId)?.DisplayName ?? component.TypeId;

    /// <summary>
    /// 判断组件记录是否为内置相机组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为相机组件</returns>
    public static bool IsCamera(ComponentRecord? component) =>
        component is not null && string.Equals(component.TypeId, Camera, StringComparison.Ordinal);

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
        var mask = uint.MaxValue;
        if (data.TryGetProperty("cullingMask", out var maskElement) && maskElement.ValueKind == JsonValueKind.Number)
        {
            var raw = maskElement.GetInt64();
            if (raw is >= 0 and <= uint.MaxValue)
            {
                mask = (uint)raw;
            }
        }

        return new CameraComponentData(
            ReadFloat(data, "fieldOfViewDegrees", CameraComponentData.Default.FieldOfViewDegrees),
            ReadFloat(data, "nearClip", CameraComponentData.Default.NearClip),
            ReadFloat(data, "farClip", CameraComponentData.Default.FarClip), mask,
            data.TryGetProperty("postProcess", out var postProcess)
                ? postProcess.Deserialize<CameraPostProcessStack>()
                : null).Normalize();
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
        Data = SerializeCameraData(data),
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

        component.Data = SerializeCameraData(data);
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

    private static string ReadString(JsonElement data, string name, string fallback = "") =>
        data.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement data, string name, bool fallback) =>
        data.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : fallback;
    private static int ReadInt(JsonElement data, string name, int fallback) =>
        data.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
}

/// <summary>
/// 体素模型组件保存的资源引用信息
/// </summary>
public readonly record struct VoxelModelComponentData(
    string Model, string Part, int VoxelCount, string Template,
    float ScaleX = 1F, float ScaleY = 1F, float ScaleZ = 1F,
    string SortingLayer = "默认", int SortingOrder = 0,
    MaterialSlotBinding[]? MaterialSlots = null)
{
    public static VoxelModelComponentData Default => new(string.Empty, "模型", 0, string.Empty);
}

/// <summary>模型组件的本地体素材质槽到共享材质资产的映射。</summary>
public readonly record struct MaterialSlotBinding(int Slot, string MaterialAsset);

/// <summary>
/// 场景光照组件的类型；方向光不需要位置范围，由对象朝向决定照射方向
/// </summary>
public enum LightShape
{
    Point,
    Spot,
    Directional,
}

public sealed record SceneComponentDefinition(string TypeId, string DisplayName, string Category, bool BuiltIn);

/// <summary>
/// 本地光源的配光曲线预设，用于模拟 IES 中常见的窄束、宽束和蝴蝶结分布
/// </summary>
public enum LightProfile
{
    Default,
    Narrow,
    Wide,
    Batwing,
}

/// <summary>
/// 光照组件参数：类型、强度、颜色、范围、聚光内外角、阴影开关和配光曲线预设
/// </summary>
public readonly record struct LightComponentData(
    LightShape Shape,
    float Intensity,
    float ColorR,
    float ColorG,
    float ColorB,
    float Range,
    float SpotAngleDegrees,
    float InnerSpotAngleDegrees,
    bool CastShadows,
    LightProfile Profile = LightProfile.Default)
{
    public static LightComponentData Default => new(LightShape.Point, 1F, 1F, 1F, 1F, 32F, 45F, 25F, false);

    /// <summary>旧版只含强度、颜色与阴影的构造入口，按点光和默认配光处理</summary>
    public LightComponentData(float intensity, float colorR, float colorG, float colorB, bool castShadows)
        : this(LightShape.Point, intensity, colorR, colorG, colorB, 32F, 45F, 25F, castShadows)
    {
    }

    public LightComponentData Normalize()
    {
        var range = Math.Clamp(float.IsFinite(Range) ? Range : 32F, 0.01F, 100_000F);
        var outer = Math.Clamp(float.IsFinite(SpotAngleDegrees) ? SpotAngleDegrees : 45F, 0.1F, 179F);
        var inner = Math.Clamp(float.IsFinite(InnerSpotAngleDegrees) ? InnerSpotAngleDegrees : 25F, 0F, outer);
        return new LightComponentData(
            Enum.IsDefined(Shape) ? Shape : LightShape.Point,
            Math.Clamp(float.IsFinite(Intensity) ? Intensity : 1F, 0F, 1000F),
            Math.Clamp(float.IsFinite(ColorR) ? ColorR : 1F, 0F, 1F),
            Math.Clamp(float.IsFinite(ColorG) ? ColorG : 1F, 0F, 1F),
            Math.Clamp(float.IsFinite(ColorB) ? ColorB : 1F, 0F, 1F),
            range, outer, inner, CastShadows,
            Enum.IsDefined(Profile) ? Profile : LightProfile.Default);
    }
}

/// <summary>自动探针体积组件数据</summary>
public readonly record struct ProbeVolumeComponentData(
    float OffsetX, float OffsetY, float OffsetZ, float SizeX, float SizeY, float SizeZ,
    float Density, bool AutoBake, bool Editing, string BakedAsset)
{
    public static ProbeVolumeComponentData Default => new(0F, 0F, 0F, 16F, 16F, 16F, 1F, true, false, "");
    public ProbeVolumeComponentData Normalize() => new(
        float.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -1_000_000F, 1_000_000F) : 0F,
        float.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -1_000_000F, 1_000_000F) : 0F,
        float.IsFinite(OffsetZ) ? Math.Clamp(OffsetZ, -1_000_000F, 1_000_000F) : 0F,
        float.IsFinite(SizeX) ? Math.Clamp(SizeX, 0.25F, 100000F) : 16F,
        float.IsFinite(SizeY) ? Math.Clamp(SizeY, 0.25F, 100000F) : 16F,
        float.IsFinite(SizeZ) ? Math.Clamp(SizeZ, 0.25F, 100000F) : 16F,
        float.IsFinite(Density) ? Math.Clamp(Density, 0.05F, 32F) : 1F, AutoBake, Editing,
        BakedAsset?.Replace('\\', '/') ?? "");
}


/// <param name="FieldOfViewDegrees">垂直视场角</param>
/// <param name="NearClip">近平面距离</param>
/// <param name="FarClip">远平面距离</param>
public readonly record struct CameraComponentData(
    float FieldOfViewDegrees,
    float NearClip,
    float FarClip,
    uint CullingMask,
    CameraPostProcessStack? PostProcess = null)
{
    /// <summary>
    /// 默认相机摄影参数：全图层遮罩
    /// </summary>
    public static CameraComponentData Default => new(50F, 0.05F, 100000F, uint.MaxValue);

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
        var normalizedPostProcess = (PostProcess ?? CameraPostProcessStack.Default).Clone();
        normalizedPostProcess.Normalize();
        return new CameraComponentData(fov, near, far, CullingMask, normalizedPostProcess);
    }
}
