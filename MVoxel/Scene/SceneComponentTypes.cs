using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Animation;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// MVoxel 内置场景组件的类型标识与数据读写入口
/// </summary>
public static class SceneComponentTypes
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions FieldJsonOptions = new(JsonSerializerDefaults.Web) { IncludeFields = true };

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
    /// <summary>局部后处理覆盖组件的稳定类型标识</summary>
    public const string PostProcessOverride = "mvoxel.postprocessoverride";
    /// <summary>局部风场组件的稳定类型标识</summary>
    public const string WindField = "mvoxel.windfield";
    /// <summary>风力接收器组件的稳定类型标识</summary>
    public const string WindReceiver = "mvoxel.windreceiver";
    /// <summary>体素框选组件的稳定类型标识</summary>
    public const string VoxelSelection = "mvoxel.voxelselection";

    /// <summary>动画组件的稳定类型标识</summary>
    public const string Animation = "mvoxel.animation";

    public static readonly IReadOnlyList<SceneComponentDefinition> BuiltInDefinitions =
    [
        new(Transform, "变换", "基础", true),
        new(VoxelModel, "模型", "体素", true),
        new(VoxelSelection, "体素框选", "体素", true),
        new(Camera, "相机", "视图", true),
        new(Light, "光照", "渲染", true),
        new(ProbeVolume, "自动探针", "渲染", true),
        new(PostProcessOverride, "局部后处理覆盖", "环境交互", true),
        new(WindField, "局部风场", "环境交互", true),
        new(WindReceiver, "风力接收器", "环境交互", true),
        new(Animation, "动画", "动画", true),
    ];

    public static bool IsAnimation(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, Animation, StringComparison.Ordinal);
    public static ComponentRecord CreateAnimation(AnimationComponentData data) => new() { TypeId = Animation, SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions) };
    public static AnimationComponentData ReadAnimation(ComponentRecord component) => IsAnimation(component) && component.Data.ValueKind == JsonValueKind.Object ? component.Data.Deserialize<AnimationComponentData>()?.Normalize() ?? new AnimationComponentData() : new AnimationComponentData();
    public static void WriteAnimation(ComponentRecord component, AnimationComponentData data)
    {
        if (!IsAnimation(component)) throw new ArgumentException("组件记录不是动画组件", nameof(component));
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions));
    }

    public static bool IsTransform(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, Transform, StringComparison.Ordinal);
    public static bool IsVoxelModel(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, VoxelModel, StringComparison.Ordinal);
    /// <summary>
    /// 判断组件记录是否为体素框选组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为体素框选组件</returns>
    public static bool IsVoxelSelection(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, VoxelSelection, StringComparison.Ordinal);
    public static bool IsLight(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, Light, StringComparison.Ordinal);
    public static bool IsProbeVolume(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, ProbeVolume, StringComparison.Ordinal);
    public static bool IsPostProcessOverride(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, PostProcessOverride, StringComparison.Ordinal);

    public static ComponentRecord CreatePostProcessOverride(PostProcessOverrideComponentData data) => new()
    {
        TypeId = PostProcessOverride, SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions),
    };

    public static PostProcessOverrideComponentData ReadPostProcessOverride(ComponentRecord component) =>
        IsPostProcessOverride(component) && component.Data.ValueKind == JsonValueKind.Object
            ? component.Data.Deserialize<PostProcessOverrideComponentData>(FieldJsonOptions)?.Normalize() ?? new PostProcessOverrideComponentData()
            : new PostProcessOverrideComponentData();

    public static void WritePostProcessOverride(ComponentRecord component, PostProcessOverrideComponentData data)
    {
        if (!IsPostProcessOverride(component)) throw new ArgumentException("组件记录不是局部后处理覆盖组件", nameof(component));
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions));
    }

    public static bool IsWindField(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, WindField, StringComparison.Ordinal);
    public static bool IsWindReceiver(ComponentRecord? component) => component is not null && string.Equals(component.TypeId, WindReceiver, StringComparison.Ordinal);

    public static ComponentRecord CreateWindField(WindFieldComponentData data) => new()
    {
        TypeId = WindField, SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions),
    };

    public static WindFieldComponentData ReadWindField(ComponentRecord component) =>
        IsWindField(component) && component.Data.ValueKind == JsonValueKind.Object
            ? component.Data.Deserialize<WindFieldComponentData>(FieldJsonOptions)?.Normalize() ?? new WindFieldComponentData()
            : new WindFieldComponentData();

    public static void WriteWindField(ComponentRecord component, WindFieldComponentData data)
    {
        if (!IsWindField(component)) throw new ArgumentException("组件记录不是局部风场组件", nameof(component));
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions));
    }

    public static ComponentRecord CreateWindReceiver(WindReceiverComponentData data) => new()
    {
        TypeId = WindReceiver, SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions),
    };

    public static WindReceiverComponentData ReadWindReceiver(ComponentRecord component) =>
        IsWindReceiver(component) && component.Data.ValueKind == JsonValueKind.Object
            ? component.Data.Deserialize<WindReceiverComponentData>(FieldJsonOptions)?.Normalize() ?? new WindReceiverComponentData()
            : new WindReceiverComponentData();

    public static void WriteWindReceiver(ComponentRecord component, WindReceiverComponentData data)
    {
        if (!IsWindReceiver(component)) throw new ArgumentException("组件记录不是风力接收器组件", nameof(component));
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions));
    }

    /// <summary>体素框选组件当前数据版本：2 起范围用对象局部坐标</summary>
    public const int VoxelSelectionSchemaVersion = 2;

    public static ComponentRecord CreateVoxelSelection(VoxelSelectionComponentData data) => new()
    {
        TypeId = VoxelSelection, SchemaVersion = VoxelSelectionSchemaVersion,
        Data = JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions),
    };

    /// <summary>
    /// 读取体素框选组件：范围用相对局部原点的整格坐标，颜色用 0 到 255 的原始体素色
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>经过范围约束的框选参数</returns>
    public static VoxelSelectionComponentData ReadVoxelSelection(ComponentRecord component)
    {
        if (!IsVoxelSelection(component) || component.Data.ValueKind != JsonValueKind.Object)
            return VoxelSelectionComponentData.Default;
        var data = component.Data;
        return new VoxelSelectionComponentData(
            ReadInt(data, "startX", 0), ReadInt(data, "startY", 0), ReadInt(data, "startZ", 0),
            ReadInt(data, "sizeX", 16), ReadInt(data, "sizeY", 16), ReadInt(data, "sizeZ", 16),
            ReadInt(data, "colorR", 255), ReadInt(data, "colorG", 255), ReadInt(data, "colorB", 255),
            ReadInt(data, "colorA", 255), ReadFloat(data, "colorTolerance", 0F),
            ReadBool(data, "emissiveOnly", false), ReadBool(data, "editing", false),
            ReadInt(data, "originX", 0), ReadInt(data, "originY", 0), ReadInt(data, "originZ", 0)).Normalize();
    }

    public static void WriteVoxelSelection(ComponentRecord component, VoxelSelectionComponentData data)
    {
        if (!IsVoxelSelection(component)) throw new ArgumentException("组件记录不是体素框选组件", nameof(component));
        component.SchemaVersion = VoxelSelectionSchemaVersion;
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), FieldJsonOptions));
    }

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
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions));
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
            ReadMaterialSlots(data),
            data.TryGetProperty("mirrorX", out var mirrorX) && mirrorX.ValueKind == JsonValueKind.True);
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
            profile,
            ReadFloat(data, "areaWidth", 32F),
            ReadFloat(data, "areaHeight", 32F)).Normalize();
    }

    /// <summary>
    /// 写回光照组件数据
    /// </summary>
    public static void WriteLight(ComponentRecord component, LightComponentData data)
    {
        if (!IsLight(component)) throw new ArgumentException("组件记录不是光照组件", nameof(component));
        component.Data = MergeComponentData(component.Data, JsonSerializer.SerializeToElement(data.Normalize(), JsonOptions));
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
            physicalCamera = normalized.PhysicalCamera,
            focalLengthMillimeters = normalized.FocalLengthMillimeters,
            sensorWidthMillimeters = normalized.SensorWidthMillimeters,
            apertureFStop = normalized.ApertureFStop,
            shutterSpeed = normalized.ShutterSpeed,
            iso = normalized.Iso,
            focusDistance = normalized.FocusDistance,
            postProcess = normalized.PostProcess,
            realtimeAntiAliasingSamples = normalized.RealtimeAntiAliasingSamples,
            realtimeAntiAliasingPattern = normalized.RealtimeAntiAliasingPattern,
            realtimeAntiAliasingSpread = normalized.RealtimeAntiAliasingSpread,
            fxaaThreshold = normalized.FxaaThreshold,
            fxaaStrength = normalized.FxaaStrength,
            waterFootprintFiltering = normalized.WaterFootprintFiltering,
        }, FieldJsonOptions);
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
                ? postProcess.Deserialize<CameraPostProcessStack>(FieldJsonOptions)
                : null,
            data.TryGetProperty("physicalCamera", out var physical) && physical.ValueKind == JsonValueKind.True,
            ReadFloat(data, "focalLengthMillimeters", 50F), ReadFloat(data, "sensorWidthMillimeters", 36F),
            ReadFloat(data, "apertureFStop", 5.6F), ReadFloat(data, "shutterSpeed", 1F / 60F),
            ReadFloat(data, "iso", 100F), ReadFloat(data, "focusDistance", 10F),
            (int)ReadFloat(data, "realtimeAntiAliasingSamples", 1F),
            ReadInt(data, "realtimeAntiAliasingPattern", 0),
            ReadFloat(data, "realtimeAntiAliasingSpread", 1F),
            ReadFloat(data, "fxaaThreshold", .125F), ReadFloat(data, "fxaaStrength", .75F),
            ReadFloat(data, "waterFootprintFiltering", 1F)).Normalize();
    }

    /// <summary>
    /// 创建一个带默认摄影参数的相机组件记录
    /// </summary>
    /// <returns>相机组件记录</returns>
    public static ComponentRecord CreateCamera() => new()
    {
        TypeId = Camera,
        SchemaVersion = 1,
        Data = SerializeCameraData(CameraComponentData.Default),
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

        component.Data = MergeComponentData(component.Data, SerializeCameraData(data), mergeCameraEffects: true);
    }

    // 更新已知字段时保留未来版本写入的对象字段。数组以新值为准，
    // 只有相机效果按类型保留每个仍存在的效果中的未知字段。
    private static JsonElement MergeComponentData(JsonElement existing, JsonElement updated, bool mergeCameraEffects = false)
    {
        var original = existing.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(existing.GetRawText()) as JsonObject : null;
        var changes = JsonNode.Parse(updated.GetRawText())!.AsObject();
        var merged = MergeObjects(original ?? new JsonObject(), changes, mergeCameraEffects, false);
        return JsonSerializer.SerializeToElement(merged, FieldJsonOptions);
    }

    private static JsonObject MergeObjects(JsonObject original, JsonObject changes, bool camera, bool postProcess)
    {
        foreach (var property in changes)
        {
            if (property.Value is JsonObject child && original[property.Key] is JsonObject oldChild)
            {
                original[property.Key] = MergeObjects((JsonObject)oldChild.DeepClone(), child,
                    camera, camera && property.Key == "postProcess");
            }
            else if (postProcess && property.Key == "effects" && property.Value is JsonArray effects && original[property.Key] is JsonArray oldEffects)
            {
                var mergedEffects = new JsonArray();
                foreach (var effect in effects)
                {
                    if (effect is JsonObject newEffect && newEffect["type"] is JsonValue type && type.TryGetValue<int>(out var typeId))
                    {
                        var oldEffect = oldEffects.OfType<JsonObject>().LastOrDefault(item =>
                            item["type"] is JsonValue oldType && oldType.TryGetValue<int>(out var oldId) && oldId == typeId);
                        mergedEffects.Add(oldEffect is null ? newEffect.DeepClone() : MergeObjects((JsonObject)oldEffect.DeepClone(), newEffect, false, false));
                    }
                    else mergedEffects.Add(effect?.DeepClone());
                }
                original[property.Key] = mergedEffects;
            }
            else original[property.Key] = property.Value?.DeepClone();
        }
        return original;
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
    MaterialSlotBinding[]? MaterialSlots = null, bool MirrorX = false)
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
    /// <summary>面积灯：按对象朝向展开的矩形发光面，尺寸用宽高两个世界单位值</summary>
    Area,
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
    LightProfile Profile = LightProfile.Default,
    float AreaWidth = 32F,
    float AreaHeight = 32F)
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
            Enum.IsDefined(Profile) ? Profile : LightProfile.Default,
            Math.Clamp(float.IsFinite(AreaWidth) ? AreaWidth : 32F, 0.1F, 4096F),
            Math.Clamp(float.IsFinite(AreaHeight) ? AreaHeight : 32F, 0.1F, 4096F));
    }
}

/// <summary>
/// 体素框选组件参数：对象局部坐标里的长方体范围、目标颜色与匹配容差
/// </summary>
/// <remarks>
/// 范围用整格坐标而不是浮点包围盒：选中的格子直接对应体素世界里的键，
/// 做换色、删除、移动、旋转、缩放时不需要再做坐标取整，撤销记录也能逐格精确还原。
/// 坐标是局部的：原点由组件自己的 Origin 加对象原点组成，对象移动时范围框跟着走，
/// 场景文件里也不会出现整张地图那样的巨大坐标
/// </remarks>
/// <param name="StartX">相对局部原点的最小角 X</param>
/// <param name="StartY">相对局部原点的最小角 Y</param>
/// <param name="StartZ">相对局部原点的最小角 Z</param>
/// <param name="SizeX">X 方向格数</param>
/// <param name="SizeY">Y 方向格数</param>
/// <param name="SizeZ">Z 方向格数</param>
/// <param name="ColorR">目标颜色红通道</param>
/// <param name="ColorG">目标颜色绿通道</param>
/// <param name="ColorB">目标颜色蓝通道</param>
/// <param name="ColorA">目标颜色透明度</param>
/// <param name="ColorTolerance">逐通道允许的最大差值，0 表示完全相同</param>
/// <param name="EmissiveOnly">是否只匹配带自发光标记的体素</param>
/// <param name="Editing">是否处于框选调整模式</param>
/// <param name="OriginX">局部原点相对对象原点的 X 偏移，添加组件时取模型包围盒最小角</param>
/// <param name="OriginY">局部原点相对对象原点的 Y 偏移</param>
/// <param name="OriginZ">局部原点相对对象原点的 Z 偏移</param>
public readonly record struct VoxelSelectionComponentData(
    int StartX, int StartY, int StartZ,
    int SizeX, int SizeY, int SizeZ,
    int ColorR, int ColorG, int ColorB, int ColorA,
    float ColorTolerance = 0F, bool EmissiveOnly = false, bool Editing = false,
    int OriginX = 0, int OriginY = 0, int OriginZ = 0)
{
    /// <summary>默认框选参数：16 格立方、纯白、精确匹配、原点对齐对象</summary>
    public static VoxelSelectionComponentData Default => new(0, 0, 0, 16, 16, 16, 255, 255, 255, 255);

    /// <summary>相对局部原点的最小角</summary>
    [JsonIgnore]
    public VoxelCoord64 Minimum => new(StartX, StartY, StartZ);

    /// <summary>局部原点相对对象原点的偏移</summary>
    [JsonIgnore]
    public VoxelCoord64 Origin => new(OriginX, OriginY, OriginZ);

    /// <summary>
    /// 把局部范围换算成体素世界坐标：对象原点 + 局部原点 + 起点
    /// </summary>
    /// <param name="objectOrigin">对象自身的格原点</param>
    /// <returns>范围最小角的世界体素坐标</returns>
    public VoxelCoord64 WorldMinimum(VoxelCoord64 objectOrigin) =>
        new(objectOrigin.X + OriginX + StartX, objectOrigin.Y + OriginY + StartY, objectOrigin.Z + OriginZ + StartZ);

    /// <summary>范围格数</summary>
    [JsonIgnore]
    public VoxelCoord64 Size => new(SizeX, SizeY, SizeZ);

    /// <summary>范围体积（格数），用 64 位避免大范围溢出</summary>
    [JsonIgnore]
    public long Volume => (long)SizeX * SizeY * SizeZ;

    /// <summary>目标颜色对应的体素值，材质槽与标志由操作方决定</summary>
    [JsonIgnore]
    public VoxelValue TargetColor => new(new Rgba32((byte)ColorR, (byte)ColorG, (byte)ColorB, (byte)ColorA));

    /// <summary>
    /// 把范围、颜色和容差约束到编辑器支持区间
    /// </summary>
    /// <returns>规范化后的框选参数</returns>
    public VoxelSelectionComponentData Normalize() => new(
        ClampCoordinate(StartX), ClampCoordinate(StartY), ClampCoordinate(StartZ),
        ClampSize(SizeX), ClampSize(SizeY), ClampSize(SizeZ),
        Math.Clamp(ColorR, 0, 255), Math.Clamp(ColorG, 0, 255),
        Math.Clamp(ColorB, 0, 255), Math.Clamp(ColorA, 0, 255),
        float.IsFinite(ColorTolerance) ? Math.Clamp(ColorTolerance, 0F, 255F) : 0F,
        EmissiveOnly, Editing,
        ClampCoordinate(OriginX), ClampCoordinate(OriginY), ClampCoordinate(OriginZ));

    private static int ClampCoordinate(int value) => Math.Clamp(value, -10_000_000, 10_000_000);

    private static int ClampSize(int value) => Math.Clamp(value, 1, 4096);
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
    CameraPostProcessStack? PostProcess = null,
    bool PhysicalCamera = false,
    float FocalLengthMillimeters = 50F,
    float SensorWidthMillimeters = 36F,
    float ApertureFStop = 5.6F,
    float ShutterSpeed = 1F / 60F,
    float Iso = 100F,
    float FocusDistance = 10F,
    int RealtimeAntiAliasingSamples = 1,
    int RealtimeAntiAliasingPattern = 0,
    float RealtimeAntiAliasingSpread = 1F,
    float FxaaThreshold = .125F,
    float FxaaStrength = .75F,
    float WaterFootprintFiltering = 1F)
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
        var focal = Math.Clamp(float.IsFinite(FocalLengthMillimeters) ? FocalLengthMillimeters : 50F, 1F, 500F);
        var sensor = Math.Clamp(float.IsFinite(SensorWidthMillimeters) ? SensorWidthMillimeters : 36F, 1F, 100F);
        var aperture = Math.Clamp(float.IsFinite(ApertureFStop) ? ApertureFStop : 5.6F, 0.7F, 64F);
        var shutter = Math.Clamp(float.IsFinite(ShutterSpeed) ? ShutterSpeed : 1F / 60F, 1F / 8000F, 60F);
        var iso = Math.Clamp(float.IsFinite(Iso) ? Iso : 100F, 25F, 51200F);
        var focus = Math.Clamp(float.IsFinite(FocusDistance) ? FocusDistance : 10F, 0.01F, 1_000_000F);
        return new CameraComponentData(fov, near, far, CullingMask, normalizedPostProcess,
            PhysicalCamera, focal, sensor, aperture, shutter, iso, focus,
            RealtimeAntiAliasingSamples switch { 2 => 2, 4 => 4, 8 => 8, 16 => 16, _ => 1 },
            Math.Clamp(RealtimeAntiAliasingPattern, 0, 2),
            float.IsFinite(RealtimeAntiAliasingSpread) ? Math.Clamp(RealtimeAntiAliasingSpread, .25F, 1F) : 1F,
            float.IsFinite(FxaaThreshold) ? Math.Clamp(FxaaThreshold, .0312F, .333F) : .125F,
            float.IsFinite(FxaaStrength) ? Math.Clamp(FxaaStrength, 0F, 1F) : .75F,
            float.IsFinite(WaterFootprintFiltering) ? Math.Clamp(WaterFootprintFiltering, 0F, 2F) : 1F);
    }
}
