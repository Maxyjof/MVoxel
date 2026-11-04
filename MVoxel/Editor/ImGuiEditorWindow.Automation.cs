using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MVoxel.Core;
using MVoxel.Animation;
using MVoxel.Editor.Assets;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Scene;
using MVoxel.Voxels;
using OpenTK.Mathematics;
using NumQuaternion = System.Numerics.Quaternion;
using NumVector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器命令总线：把界面里能做的主要操作登记成可枚举、可调用的命令，
/// MCP 不需要模拟鼠标键盘也能完成同样的编辑，并且能自行发现全部可用命令
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>命令参数描述，供 MCP 自行拼参数</summary>
    private sealed record CommandParameter(string Name, string Type, bool Required, string Description);

    /// <summary>命令登记项：编号、分类、标题、说明与参数表</summary>
    private sealed record EditorCommandEntry(string Id, string Category, string Title, string Description,
        CommandParameter[] Parameters);

    private static CommandParameter Param(string name, string type, bool required, string description) =>
        new(name, type, required, description);

    private static readonly CommandParameter[] TargetIdOrName =
    [
        Param("id", "string", false, "对象标识，优先使用"),
        Param("name", "string", false, "对象名称，id 缺省时按名称精确匹配"),
    ];

    /// <summary>全部可调用命令的登记表，editor.commands 原样返回</summary>
    private static readonly EditorCommandEntry[] CommandTable =
    [
        new("scene.list", "场景", "列出场景对象", "按文档顺序列出全部对象的父子关系、可见性、图层与模型摘要", []),
        new("scene.create", "场景", "新建对象", "在场景根或指定父级下新建对象",
            [Param("name", "string", true, "对象名称"), Param("parentId", "string", false, "父对象标识，缺省挂到场景根"),
             Param("visible", "boolean", false, "是否可见，默认 true")]),
        new("scene.rename", "场景", "重命名对象", "按 id 或名称修改对象显示名",
            [.. TargetIdOrName, Param("newName", "string", true, "新名称")]),
        new("scene.delete", "场景", "删除对象", "删除对象及其全部子对象",
            [.. TargetIdOrName]),
        new("scene.setParent", "场景", "改变父级", "把对象挂到新父级或移回场景根",
            [.. TargetIdOrName, Param("parentId", "string", false, "新父对象标识，null 或省略表示场景根")]),
        new("scene.setVisible", "场景", "设置可见性", "隐藏或显示对象",
            [.. TargetIdOrName, Param("visible", "boolean", true, "是否可见")]),
        new("scene.setLocked", "场景", "设置锁定", "锁定后对象不可在界面中选中编辑",
            [.. TargetIdOrName, Param("locked", "boolean", true, "是否锁定")]),
        new("scene.setLayer", "场景", "设置图层", "把对象移到指定图层",
            [.. TargetIdOrName, Param("layer", "integer", true, "图层索引，从 0 开始")]),
        new("scene.setTransform", "场景", "设置变换", "设置对象的自由变换位置与欧拉旋转（度）",
            [.. TargetIdOrName, Param("position", "number[3]", false, "世界位置 x/y/z"),
             Param("rotation", "number[3]", false, "欧拉角 x/y/z，单位为度")]),
        new("scene.select", "场景", "选中对象", "选中对象并打开检查器，等同在层级里点选",
            TargetIdOrName),
        new("scene.components.get", "环境交互", "读取环境交互组件", "读取对象上的局部后处理覆盖、局部风场和风力接收器参数", [.. TargetIdOrName]),
        new("scene.components.set", "环境交互", "写入环境交互组件", "创建或更新对象上的局部后处理覆盖、局部风场和风力接收器参数", [.. TargetIdOrName,
             Param("postProcessOverride", "object", false, "局部后处理覆盖字段"), Param("windField", "object", false, "局部风场字段"), Param("windReceiver", "object", false, "风力接收器字段")]),
        new("layer.list", "图层", "列出图层", "列出全部图层名称与显隐状态", []),
        new("layer.create", "图层", "新建图层", "追加一个新图层并返回索引",
            [Param("name", "string", true, "图层名称")]),
        new("layer.setVisible", "图层", "设置图层显隐", "整体隐藏或显示某个图层",
            [Param("layer", "integer", true, "图层索引"), Param("visible", "boolean", true, "是否可见")]),
        new("camera.list", "相机", "列出相机", "列出场景中挂载相机组件的对象", []),
        new("camera.create", "相机", "新建相机", "新建带相机组件的对象",
            [Param("name", "string", true, "相机对象名称"), Param("position", "number[3]", false, "相机位置 x/y/z"),
             Param("target", "number[3]", false, "注视点 x/y/z，缺省时沿 -Z 方向")]),
        new("camera.setTransform", "相机", "设置相机位姿", "写入相机对象的位置与注视点",
            [.. TargetIdOrName, Param("position", "number[3]", true, "相机位置 x/y/z"),
             Param("target", "number[3]", true, "注视点 x/y/z")]),
        new("camera.setParams", "相机", "设置相机参数", "写入视场角与裁剪面",
            [.. TargetIdOrName, Param("fieldOfView", "number", false, "视场角（度）"),
             Param("nearClip", "number", false, "近裁剪面"), Param("farClip", "number", false, "远裁剪面")]),
        new("camera.setActive", "相机", "设为渲染相机", "让渲染窗口使用指定相机",
            [.. TargetIdOrName]),
        new("material.list", "材质", "列出材质", "列出项目内全部 .material 资产", []),
        new("material.get", "材质", "读取材质", "按资产路径读取材质参数，路径相对项目Assets目录",
            [Param("path", "string", true, "材质资产路径，以.material结尾")]),
        new("material.create", "材质", "新建材质", "创建默认材质资产，不覆盖已有文件，材质参数只能在材质检查器中编辑",
            [Param("path", "string", true, "相对Assets目录的.material路径"), Param("name", "string", false, "材质名称")]),
        new("material.set", "材质", "材质参数编辑说明", "此接口不修改参数，始终提示到材质检查器编辑；不再支持全局slot", []),
        new("material.remove", "材质", "删除材质", "按路径删除材质资产，保留组件中的失效引用，不改体素编号",
            [Param("path", "string", true, "相对Assets目录的.material路径")]),
        new("material.references", "材质", "查询材质引用", "查询当前场景引用此路径的模型组件，资产删除后仍可查询失效引用",
            [Param("path", "string", true, "相对Assets目录的.material路径")]),
        new("model.materials.get", "材质", "读取组件材质槽", "读取指定模型组件的本地槽位引用；槽0始终为默认材质",
            [Param("objectId", "string", true, "场景对象标识")]),
        new("model.materials.bind", "材质", "绑定组件材质", "绑定或替换本地槽位的资产引用，影响该组件使用此槽的全部体素，不修改体素编号",
            [Param("objectId", "string", true, "场景对象标识"), Param("localSlot", "integer", true, "本地槽位1至65535，槽0不可绑定"),
             Param("path", "string", true, "相对Assets目录的.material路径")]),
        new("render.settings.get", "渲染", "读取渲染设置", "读取当前场景的渲染设置", []),
        new("render.settings.set", "渲染", "写入渲染设置", "逐字段修改渲染设置，未给出的字段保持原值",
            [Param("pathTracing", "boolean", false, "是否开启路径追踪"),
             Param("maxSamples", "integer", false, "最大累积样本数"), Param("maxBounces", "integer", false, "最大反弹次数"),
             Param("exposure", "number", false, "曝光"), Param("gamma", "number", false, "伽马"),
             Param("saturation", "number", false, "饱和度"), Param("brightness", "number", false, "亮度"),
             Param("contrast", "number", false, "对比度"),
             Param("sunEnabled", "boolean", false, "是否启用太阳光"),
             Param("sunAzimuth", "number", false, "太阳方位角（度）"),
             Param("sunElevation", "number", false, "太阳高度角（度）"),
             Param("sunIntensity", "number", false, "太阳强度"),
             Param("skyAmbient", "number", false, "天空环境光强度"),
             Param("skyTop", "color", false, "天空顶部颜色，[r,g,b] 或 \"r,g,b\"，取值 0 到 1"),
             Param("skyHorizon", "color", false, "天空地平线颜色，[r,g,b] 或 \"r,g,b\"，取值 0 到 1"),
             Param("sceneBackgroundColor", "color", false, "渲染背景色，[r,g,b] 或 \"r,g,b\"，路径追踪按 MagicaVoxel 的 Lb 语义画在曝光之后"),
             Param("ambientOcclusion", "boolean", false, "是否启用环境光遮蔽"),
             Param("aoStrength", "number", false, "环境光遮蔽强度"),
             Param("aoSamples", "integer", false, "AO 解析邻域精度档位，4 到 64，不发射射线"),
             Param("voxelIndirectLightSamples", "integer", false, "体素间接光邻域精度档位，1 到 32，不发射射线"),
             Param("shadowSamples", "integer", false, "阴影半影质量档位，1 到 8；实时通道按档位×2 条射线采样"),
             Param("realtimeAntiAliasingSamples", "integer", false, "实时通道抗锯齿采样数，1、2、4、8、16"),
             Param("realtimeAntiAliasingPattern", "integer", false, "抗锯齿模式，0 多采样，1 旋转网格，2 FXAA"),
             Param("probeBakeRays", "integer", false, "每个探针的烘焙方向数，8 到 256"),
             Param("probeBakeMaxProbes", "integer", false, "单个体积的探针数量上限，1024 到 262144"),
             Param("probeBakeSmoothing", "integer", false, "烘焙结果平滑次数，0 到 4"),
             Param("probeVolumeGiStrength", "number", false, "探针 GI 补光强度"),
             Param("realtimeTemporalWeight", "number", false, "非光追实时历史平滑权重，0 到 0.98；确定性光照下默认 0"),
             Param("fogDensity", "number", false, "雾浓度"),
             Param("showGround", "boolean", false, "是否显示地面"),
              Param("fogStart", "number", false, "雾起始距离"),
              Param("fogSkyBlend", "number", false, "雾颜色与天空颜色混合权重"),
              Param("fogMaxOpacity", "number", false, "雾最大不透明度"),
              Param("showWater", "boolean", false, "是否显示水面"),
              Param("waterHeight", "number", false, "水面高度"),
              Param("waterColor", "string", false, "水面颜色，格式为R,G,B"),
              Param("waterWaveSize", "number", false, "水面波浪大小"),
              Param("waterWaveLength", "number", false, "水面波长"),
              Param("waterRoughness", "number", false, "水面粗糙度"),
              Param("waterAnimationEnabled", "boolean", false, "是否启用实时水面动画"),
              Param("waterAnimationSpeed", "number", false, "水面动画速度"),
              Param("waterFlowX", "number", false, "水面流向X"),
              Param("waterFlowZ", "number", false, "水面流向Z"),
              Param("waterDetailStrength", "number", false, "水面细节扰动"),
              Param("waterSpecularStrength", "number", false, "水面高光强度"),
              Param("weatherWindStrength", "number", false, "风强度"), Param("weatherWindDirection", "number", false, "风向"),
              Param("weatherRain", "number", false, "降雨强度"), Param("weatherSnow", "number", false, "降雪强度"),
              Param("weatherStorm", "number", false, "雷暴强度"), Param("weatherHeatHaze", "number", false, "热浪折射散射"),
              Param("weatherLightning", "number", false, "雷电频率"),
             Param("transmissiveAbsorption", "number", false, "半透明吸收浓度"),
             Param("emissiveIntensity", "number", false, "发光体素辐射强度，0 表示关闭发光照明"),
             Param("emissiveVoxels", "boolean", false, "是否把发光体素作为光源"),
             Param("softFillEnabled", "boolean", false, "是否启用环境柔光（对应 MagicaVoxel 的 Area 灯）"),
             Param("softFillIntensity", "number", false, "环境柔光强度"),
             Param("softFillColor", "color", false, "环境柔光颜色，[r,g,b] 或 \"r,g,b\"，取值 0 到 1"),
             Param("softFillSize", "number", false, "柔光板半边长相对场景包围盒的倍率"),
             Param("softFillDistance", "number", false, "柔光板到相机的距离相对包围盒的倍率"),
             Param("denoise", "boolean", false, "是否降噪")]),
        new("render.resetAccumulation", "渲染", "重置累积", "丢弃已累积样本，重新开始采样", []),
        new("model.scale", "模型", "设置模型缩放", "按三轴倍率重采样体素模型",
            [.. TargetIdOrName, Param("scaleX", "number", true, "X 轴倍率"),
             Param("scaleY", "number", true, "Y 轴倍率"), Param("scaleZ", "number", true, "Z 轴倍率")]),
        new("timeline.list", "时间轴", "列出时间轴", "列出Assets目录下的时间轴资产", []),
        new("timeline.get", "时间轴", "读取时间轴", "读取一个时间轴资产", [Param("path", "string", true, "相对Assets目录的.mvtimeline.json路径")]),
        new("timeline.create", "时间轴", "新建时间轴", "创建默认时间轴资产，不覆盖已有文件", [Param("path", "string", true, "相对Assets目录的.mvtimeline.json路径"), Param("name", "string", false, "时间轴名称")]),

        new("ui.setPanel", "界面", "开关面板", "显示或隐藏指定界面面板",
            [Param("panel", "string", true, "面板标识，见 ui.panels"), Param("visible", "boolean", true, "是否显示")]),
        new("perf.profile", "性能", "强制性能采样", "不打开分析器窗口也开启 GPU 时间戳采样，供命令行取证",
            [Param("enabled", "boolean", true, "是否开启")]),
        new("project.importVox", "项目", "导入体素模型", "按路径导入 .vox/.qb/.schem/.mvmodel 到项目并打开，返回资产路径与体素统计",
            [Param("path", "string", true, "体素模型文件路径")]),
        new("perf.stats", "性能", "读取性能汇总", "返回最近若干帧的 GPU 采样与视口耗时的均值、P95 与峰值", []),
        new("camera.setPostProcess", "相机", "设置相机后处理效果", "开关相机后处理栈里的效果并写入强度与采样数，用于非光追画质标定",
            [Param("name", "string", false, "相机对象名"), Param("effect", "string", true, "效果类型，如 AmbientOcclusion 或 VoxelIndirectLight"), Param("enabled", "boolean", false, "是否启用"), Param("intensity", "number", false, "强度"), Param("samples", "integer", false, "采样数")]),
        new("render.captureDisplay", "渲染", "保存当前显示结果", "把当前最终显示图直接存成 PNG，不做重新渲染，用于核对非光追通道的画面",
            [Param("path", "string", true, "输出 PNG 路径")]),
        new("ui.setCenterTab", "界面", "切换中央页面", "把中央区域切到场景或渲染页面，等同点击页签",
            [Param("tab", "string", true, "scene 或 render")]),
        new("ui.status", "界面", "读取状态栏", "读取编辑器状态栏文本", []),
        new("project.info", "项目", "读取项目信息", "读取当前项目的路径、场景与统计", []),
    ];

    /// <summary>
    /// 列出全部可调用命令，供 MCP 自行发现能力
    /// </summary>
    /// <param name="request">命令 JSON</param>
    /// <returns>命令登记表</returns>
    private object ListEditorCommands(JsonElement request)
    {
        var category = ReadString(Unwrap(request), "category");
        var entries = string.IsNullOrWhiteSpace(category)
            ? CommandTable
            : CommandTable.Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new
        {
            count = entries.Length,
            commands = entries.Select(item => new
            {
                id = item.Id,
                category = item.Category,
                title = item.Title,
                description = item.Description,
                parameters = item.Parameters.Select(parameter => new
                {
                    name = parameter.Name,
                    type = parameter.Type,
                    required = parameter.Required,
                    description = parameter.Description,
                }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>
    /// 按编号执行一条登记命令
    /// </summary>
    /// <param name="request">命令 JSON，带 commandId 与 arguments</param>
    /// <returns>命令结果</returns>
    private object ExecuteEditorCommand(JsonElement request)
    {
        var arguments = Unwrap(request);
        var commandId = ReadString(arguments, "commandId") ?? ReadString(request, "commandId");
        if (string.IsNullOrWhiteSpace(commandId)) throw new ArgumentException("缺少 commandId");
        var payload = arguments.TryGetProperty("arguments", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested : arguments;
        if (commandId.StartsWith("material.", StringComparison.Ordinal) || commandId.StartsWith("model.materials.", StringComparison.Ordinal))
            RejectLegacyMaterialSlot(payload);
        return commandId switch
        {
            "scene.list" => ListSceneObjects(),
            "scene.create" => CreateSceneObject(payload),
            "scene.rename" => RenameSceneObject(payload),
            "scene.delete" => DeleteSceneObject(payload),
            "scene.setParent" => SetSceneObjectParent(payload),
            "scene.setVisible" => SetSceneObjectFlag(payload, visible: true),
            "scene.setLocked" => SetSceneObjectFlag(payload, visible: false),
            "scene.setLayer" => SetSceneObjectLayer(payload),
            "scene.setTransform" => SetSceneObjectTransform(payload),
            "scene.select" => QueueSelectObject(WrapArguments(payload)),
            "scene.selection" => DescribeSelection(),
            "scene.components.get" => GetEnvironmentComponents(payload),
            "scene.components.set" => SetEnvironmentComponents(payload),
            "layer.list" => ListLayers(),
            "layer.create" => CreateLayer(payload),
            "layer.setVisible" => SetLayerVisible(payload),
            "camera.list" => ListCameras(),
            "camera.create" => CreateCamera(payload),
            "camera.setTransform" => SetCameraTransform(payload),
            "camera.setParams" => SetCameraParameters(payload),
            "camera.setActive" => SetActiveCamera(payload),
            "material.list" => ListMaterials(),
            "material.get" => GetMaterial(payload),
            "material.create" => CreateMaterial(payload),
            "material.set" => throw new InvalidOperationException("材质参数只能在材质检查器中编辑。创建资产请用material.create(path,name)，查询请用material.get(path)，组件绑定请用model.materials.bind(objectId,localSlot,path)；不支持全局slot"),
            "material.remove" => RemoveMaterial(payload),
            "material.references" => GetMaterialReferences(payload),
            "model.materials.get" => GetModelMaterials(payload),
            "model.materials.bind" => BindModelMaterial(payload),
            "render.settings.get" => DescribeRenderSettings(),
            "render.effectiveSettings" => DescribeEffectiveRenderSettings(),
            "render.settings.set" => ApplyRenderSettings(payload),
            "render.resetAccumulation" => ResetRenderAccumulation(),
            "model.scale" => SetModelScale(payload),
            "timeline.list" => ListTimelineAssets(),
            "timeline.get" => GetTimelineAsset(payload),
            "timeline.create" => CreateTimelineAsset(payload),

            "perf.profile" => SetForcedProfiling(payload),
            "camera.setPostProcess" => SetCameraPostProcess(payload),
            "render.captureDisplay" => RunOnUi(() =>
            {
                var path = Path.GetFullPath(ReadString(payload, "path") ?? throw new ArgumentException("missing path"));
                var saved = _pipeline?.SaveDisplayPng(path) == true;
                return new { path, saved };
            }),
            "project.importVox" => RunOnUi(() =>
                ImportVoxFromPath(ReadString(payload, "path") ?? throw new ArgumentException("缺少 path"))),
            "perf.stats" => DescribePerformanceStats(),
            "ui.setPanel" => SetPanel(payload),            "ui.setCenterTab" => SetCenterTab(payload),
            "ui.status" => new { status = _statusText },
            "project.info" => DescribeProject(),
            _ => throw new InvalidOperationException($"不支持的编辑器命令：{commandId}"),
        };
    }

    /// <summary>
    /// 把会改动界面的操作投递到窗口线程并等待结果，保证与界面操作走同一条代码路径
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <returns>操作返回值，没有返回值时返回 ok</returns>
    private readonly int _automationUiThreadId = Environment.CurrentManagedThreadId;

    private object RunOnUi(Func<object?> action)
    {
        // 控制服务器已在窗口线程 Pump；再次排队并阻塞会让窗口等待自己。
        if (Environment.CurrentManagedThreadId == _automationUiThreadId)
            return action() ?? new { ok = true };
        using var completed = new ManualResetEventSlim(false);
        object? result = null;
        Exception? failure = null;
        _pendingActions.Enqueue(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = exception; }
            finally { completed.Set(); }
        });
        if (!completed.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("编辑器界面线程 30 秒内没有执行该操作");
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
        return result ?? new { ok = true };
    }

    /// <summary>
    /// 把参数重新包成 { arguments: ... }，复用只认 arguments 的旧命令入口
    /// </summary>
    private static JsonElement WrapArguments(JsonElement arguments)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { arguments }));
        return document.RootElement.Clone();
    }

    /// <summary>取出命令里的参数节点，兼容直接把参数写在请求根节点上的写法</summary>
    private static JsonElement Unwrap(JsonElement request) =>
        request.ValueKind == JsonValueKind.Object && request.TryGetProperty("arguments", out var value)
            ? value : request;

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadBoolean(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;

    private static double? ReadNumber(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static double[]? ReadVector(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            return null;
        return [value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble()];
    }

    /// <summary>
    /// 读取颜色参数，同时接受 "r,g,b" 文本与 [r,g,b] 数组两种写法
    /// </summary>
    private static System.Numerics.Vector3? ReadColor(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3)
            return new System.Numerics.Vector3((float)value[0].GetDouble(),
                (float)value[1].GetDouble(), (float)value[2].GetDouble());
        if (value.ValueKind != JsonValueKind.String) return null;
        var parts = (value.GetString() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var red) ||
            !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var green) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var blue))
            return null;
        return new System.Numerics.Vector3(red, green, blue);
    }

    private SceneDocument RequireScene() =>
        _sceneDocument ?? throw new InvalidOperationException("当前没有打开的场景");

    private ProjectHandle RequireProject() =>
        _project ?? throw new InvalidOperationException("当前没有打开的项目");

    /// <summary>按 id 或名称解析目标对象，找不到时抛错</summary>
    private SceneObject RequireObject(JsonElement arguments)
    {
        var target = FindSceneObject(ReadString(arguments, "id"), ReadString(arguments, "name"));
        return target ?? throw new InvalidOperationException(
            $"找不到对象：{ReadString(arguments, "name") ?? ReadString(arguments, "id")}");
    }

    private static readonly JsonSerializerOptions ComponentFieldJsonOptions = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    private object GetEnvironmentComponents(JsonElement arguments) => RunOnUi(() => DescribeEnvironmentComponents(RequireObject(arguments)));

    private object DescribeEnvironmentComponents(SceneObject target) => new
    {
        objectId = target.Id.ToString(),
        name = target.Name,
        postProcessOverride = target.Components.FirstOrDefault(SceneComponentTypes.IsPostProcessOverride) is { } postProcess
            ? SceneComponentTypes.ReadPostProcessOverride(postProcess) : null,
        windField = target.Components.FirstOrDefault(SceneComponentTypes.IsWindField) is { } windField
            ? SceneComponentTypes.ReadWindField(windField) : null,
        windReceiver = target.Components.FirstOrDefault(SceneComponentTypes.IsWindReceiver) is { } windReceiver
            ? SceneComponentTypes.ReadWindReceiver(windReceiver) : null,
    };


    private object SetEnvironmentComponents(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var changed = false;
        if (arguments.TryGetProperty("postProcessOverride", out var postProcess) && postProcess.ValueKind == JsonValueKind.Object)
        {
            var component = target.Components.FirstOrDefault(SceneComponentTypes.IsPostProcessOverride);
            if (component is null)
            {
                component = SceneComponentTypes.CreatePostProcessOverride(new PostProcessOverrideComponentData());
                target.Components.Add(component);
            }
            SceneComponentTypes.WritePostProcessOverride(component, JsonSerializer.Deserialize<PostProcessOverrideComponentData>(MergeComponentFields(component.Data, postProcess).GetRawText(), ComponentFieldJsonOptions) ?? new PostProcessOverrideComponentData());
            changed = true;
        }
        if (arguments.TryGetProperty("windField", out var windField) && windField.ValueKind == JsonValueKind.Object)
        {
            var component = target.Components.FirstOrDefault(SceneComponentTypes.IsWindField);
            if (component is null)
            {
                component = SceneComponentTypes.CreateWindField(new WindFieldComponentData());
                target.Components.Add(component);
            }
            SceneComponentTypes.WriteWindField(component, JsonSerializer.Deserialize<WindFieldComponentData>(MergeComponentFields(component.Data, windField).GetRawText(), ComponentFieldJsonOptions) ?? new WindFieldComponentData());
            changed = true;
        }
        if (arguments.TryGetProperty("windReceiver", out var windReceiver) && windReceiver.ValueKind == JsonValueKind.Object)
        {
            var component = target.Components.FirstOrDefault(SceneComponentTypes.IsWindReceiver);
            if (component is null)
            {
                component = SceneComponentTypes.CreateWindReceiver(new WindReceiverComponentData());
                target.Components.Add(component);
            }
            SceneComponentTypes.WriteWindReceiver(component, JsonSerializer.Deserialize<WindReceiverComponentData>(MergeComponentFields(component.Data, windReceiver).GetRawText(), ComponentFieldJsonOptions) ?? new WindReceiverComponentData());
            changed = true;
        }
        if (!changed) throw new ArgumentException("至少提供postProcessOverride、windField或windReceiver中的一个对象字段");
        MarkSceneDocumentDirty();
        return DescribeEnvironmentComponents(target);
    });

    private static JsonElement MergeComponentFields(JsonElement current, JsonElement patch)
    {
        var currentNode = current.ValueKind == JsonValueKind.Object ? JsonNode.Parse(current.GetRawText())!.AsObject() : new JsonObject();
        foreach (var property in patch.EnumerateObject()) currentNode[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(currentNode, ComponentFieldJsonOptions);
    }

    private object DescribeSceneObject(SceneObject item)
    {
        var model = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        var modelData = model is null ? VoxelModelComponentData.Default : SceneComponentTypes.ReadVoxelModel(model);
        var camera = item.Components.FirstOrDefault(SceneComponentTypes.IsCamera);
        var layerName = _sceneDocument is not null && item.LayerIndex >= 0 && item.LayerIndex < _sceneDocument.LayerNames.Count
            ? _sceneDocument.LayerNames[item.LayerIndex] : null;
        var free = item.Transform.Free;
        return new
        {
            id = item.Id.ToString(),
            name = item.Name,
            parentId = item.ParentId?.ToString(),
            visible = item.Visible,
            locked = item.Locked,
            layerIndex = item.LayerIndex,
            layerName,
            isCamera = camera is not null,
            hasModel = model is not null,
            modelAsset = modelData.Model,
            voxelCount = modelData.VoxelCount,
            scaleX = modelData.ScaleX,
            scaleY = modelData.ScaleY,
            scaleZ = modelData.ScaleZ,
            position = new[] { free.Position.X, free.Position.Y, free.Position.Z },
        };
    }

    private object ListSceneObjects()
    {
        var scene = RequireScene();
        return new
        {
            sceneName = scene.Name,
            count = scene.Objects.Count,
            objects = scene.Objects.Select(DescribeSceneObject).ToArray(),
        };
    }

    private object CreateSceneObject(JsonElement arguments)
    {
        var name = ReadString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("对象名称不能为空");
        var parentId = ReadString(arguments, "parentId");
        var visible = ReadBoolean(arguments, "visible") ?? true;
        return RunOnUi(() =>
        {
            var scene = RequireScene();
            StableId? parent = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                parent = StableId.TryParse(parentId, out var parsed)
                    ? parsed
                    : scene.Objects.FirstOrDefault(item => string.Equals(item.Name, parentId, StringComparison.Ordinal))?.Id;
                if (parent is null) throw new InvalidOperationException($"找不到父对象：{parentId}");
            }

            var created = new SceneGraph(scene).Add(name, parent);
            created.Visible = visible;
            MarkSceneDocumentDirty();
            SetStatus($"已新建对象：{created.Name}");
            return new { created = DescribeSceneObject(created) };
        });
    }

    private object RenameSceneObject(JsonElement arguments)
    {
        var newName = ReadString(arguments, "newName");
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("新名称不能为空");
        return RunOnUi(() =>
        {
            var target = RequireObject(arguments);
            var before = SceneSerializer.CloneObject(target);
            target.Name = newName;
            var after = SceneSerializer.CloneObject(target);
            _voxelHistory.RecordExecuted(new SceneObjectStateCommand(target, before, after,
                () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
            MarkSceneDocumentDirty();
            SetStatus($"已重命名为：{newName}");
            return new { id = target.Id.ToString(), name = target.Name };
        });
    }

    private object DeleteSceneObject(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var target = RequireObject(arguments);
        var removed = new List<StableId> { target.Id };
        // 子对象随父对象一起删除，避免留下孤儿层级
        for (var index = 0; index < removed.Count; index++)
            foreach (var item in scene.Objects.Where(item => item.ParentId == removed[index]))
                removed.Add(item.Id);
        scene.Objects.RemoveAll(item => removed.Contains(item.Id));
        if (_selectedSceneObjectId is { } selected && removed.Contains(selected)) _selectedSceneObjectId = null;
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        SetStatus($"已删除 {removed.Count} 个对象：{target.Name}");
        return new { removed = removed.Select(item => item.ToString()).ToArray() };
    });

    private object SetSceneObjectParent(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var target = RequireObject(arguments);
        var parentId = ReadString(arguments, "parentId");
        StableId? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId) &&
            !string.Equals(parentId, "null", StringComparison.OrdinalIgnoreCase))
        {
            parent = StableId.TryParse(parentId, out var parsed)
                ? parsed
                : scene.Objects.FirstOrDefault(item => string.Equals(item.Name, parentId, StringComparison.Ordinal))?.Id;
            if (parent is null) throw new InvalidOperationException($"找不到父对象：{parentId}");
        }

        new SceneGraph(scene).SetParent(target.Id, parent);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return new { id = target.Id.ToString(), parentId = target.ParentId?.ToString() };
    });

    /// <summary>设置可见性或锁定标记，两者共用同一套撤销记录</summary>
    private object SetSceneObjectFlag(JsonElement arguments, bool visible) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var enabled = (visible ? ReadBoolean(arguments, "visible") : ReadBoolean(arguments, "locked"))
                      ?? throw new ArgumentException(visible ? "缺少 visible" : "缺少 locked");
        var before = SceneSerializer.CloneObject(target);
        if (visible) target.Visible = enabled; else target.Locked = enabled;
        var after = SceneSerializer.CloneObject(target);
        _voxelHistory.RecordExecuted(new SceneObjectStateCommand(target, before, after,
            () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return DescribeSceneObject(target);
    });

    private object SetSceneObjectLayer(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var layer = (int?)ReadNumber(arguments, "layer") ?? throw new ArgumentException("缺少 layer");
        if (layer < 0 || layer >= scene.LayerNames.Count)
            throw new ArgumentException($"图层索引超出范围：{layer}，当前共 {scene.LayerNames.Count} 个图层");
        var target = RequireObject(arguments);
        var before = SceneSerializer.CloneObject(target);
        target.LayerIndex = layer;
        var after = SceneSerializer.CloneObject(target);
        _voxelHistory.RecordExecuted(new SceneObjectStateCommand(target, before, after,
            () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return DescribeSceneObject(target);
    });

    private object SetSceneObjectTransform(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var position = ReadVector(arguments, "position");
        var rotation = ReadVector(arguments, "rotation");
        if (position is null && rotation is null) throw new ArgumentException("position 与 rotation 至少给一个");
        var before = SceneSerializer.CloneObject(target);
        var free = target.Transform.Free;
        var nextPosition = position is null
            ? free.Position
            : new Double3(position[0], position[1], position[2]);
        var nextRotation = rotation is null
            ? free.Rotation
            : NumQuaternion.CreateFromYawPitchRoll(
                (float)(rotation[1] * Math.PI / 180D),
                (float)(rotation[0] * Math.PI / 180D),
                (float)(rotation[2] * Math.PI / 180D));
        target.Transform.SetFree(new FreeTransform(nextPosition, nextRotation));
        var after = SceneSerializer.CloneObject(target);
        _voxelHistory.RecordExecuted(new SceneObjectStateCommand(target, before, after,
            () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
        UpdateTransformMarker(target);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return DescribeSceneObject(target);
    });

    private object ListLayers()
    {
        var scene = RequireScene();
        return new
        {
            count = scene.LayerNames.Count,
            layers = scene.LayerNames.Select((name, index) => new
            {
                index,
                name,
                visible = scene.IsLayerVisible(index),
                objectCount = scene.Objects.Count(item => item.LayerIndex == index),
            }).ToArray(),
        };
    }

    private object CreateLayer(JsonElement arguments)
    {
        var name = ReadString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("图层名称不能为空");
        return RunOnUi(() =>
        {
            var scene = RequireScene();
            var index = scene.AddLayer(name);
            MarkSceneDocumentDirty();
            return new { index, name };
        });
    }

    private object SetLayerVisible(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var layer = (int?)ReadNumber(arguments, "layer") ?? throw new ArgumentException("缺少 layer");
        var visible = ReadBoolean(arguments, "visible") ?? throw new ArgumentException("缺少 visible");
        if (layer < 0 || layer >= scene.LayerNames.Count)
            throw new ArgumentException($"图层索引超出范围：{layer}");
        scene.SetLayerVisible(layer, visible);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return new { index = layer, name = scene.LayerNames[layer], visible };
    });

    private object ListCameras()
    {
        var cameras = GetRenderCameraObjects();
        // 没有显式指定渲染相机时，渲染窗口实际用的是场景里的第一台相机
        var active = cameras.FirstOrDefault(item => item.Id == _renderCameraId) ?? cameras.FirstOrDefault();
        return new
        {
            count = cameras.Count,
            activeId = active?.Id.ToString(),
            cameras = cameras.Select(item =>
            {
                var data = SceneComponentTypes.ReadCamera(item.Components.First(SceneComponentTypes.IsCamera));
                var free = item.Transform.Free;
                var forward = NumVector3.Transform(NumVector3.UnitZ, free.Rotation);
                return new
                {
                    id = item.Id.ToString(),
                    name = item.Name,
                    position = new[] { free.Position.X, free.Position.Y, free.Position.Z },
                    target = new[]
                    {
                        free.Position.X + forward.X,
                        free.Position.Y + forward.Y,
                        free.Position.Z + forward.Z,
                    },
                    fieldOfView = data.FieldOfViewDegrees,
                    nearClip = data.NearClip,
                    farClip = data.FarClip,
                };
            }).ToArray(),
        };
    }

    private object CreateCamera(JsonElement arguments)
    {
        var name = ReadString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("相机名称不能为空");
        var position = ReadVector(arguments, "position") ?? [0D, 8D, 24D];
        var target = ReadVector(arguments, "target");
        return RunOnUi(() =>
        {
            var scene = RequireScene();
            var created = new SceneGraph(scene).Add(name);
            created.Components.Add(SceneComponentTypes.CreateCamera());
            WriteCameraTransform(created,
                new Vector3((float)position[0], (float)position[1], (float)position[2]),
                target is null
                    ? -Vector3.UnitZ
                    : new Vector3((float)(target[0] - position[0]), (float)(target[1] - position[1]), (float)(target[2] - position[2])),
                Vector3.UnitY);
            MarkSceneDocumentDirty();
            SetStatus($"已新建相机：{created.Name}");
            return new { id = created.Id.ToString(), name = created.Name };
        });
    }

    private object SetCameraTransform(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var camera = target.Components.FirstOrDefault(SceneComponentTypes.IsCamera)
                     ?? throw new InvalidOperationException($"对象不是相机：{target.Name}");
        _ = camera;
        var position = ReadVector(arguments, "position") ?? throw new ArgumentException("缺少 position");
        var lookAt = ReadVector(arguments, "target") ?? throw new ArgumentException("缺少 target");
        WriteCameraTransform(target,
            new Vector3((float)position[0], (float)position[1], (float)position[2]),
            new Vector3((float)(lookAt[0] - position[0]), (float)(lookAt[1] - position[1]), (float)(lookAt[2] - position[2])),
            Vector3.UnitY);
        MarkSceneDocumentDirty();
        return new { id = target.Id.ToString(), name = target.Name };
    });

    private object SetCameraParameters(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var camera = target.Components.FirstOrDefault(SceneComponentTypes.IsCamera)
                     ?? throw new InvalidOperationException($"对象不是相机：{target.Name}");
        var data = SceneComponentTypes.ReadCamera(camera);
        var fieldOfView = ReadNumber(arguments, "fieldOfView");
        var nearClip = ReadNumber(arguments, "nearClip");
        var farClip = ReadNumber(arguments, "farClip");
        if (fieldOfView is null && nearClip is null && farClip is null)
            throw new ArgumentException("fieldOfView、nearClip、farClip 至少给一个");
        SceneComponentTypes.WriteCamera(camera, new CameraComponentData(
            (float)(fieldOfView ?? data.FieldOfViewDegrees),
            (float)(nearClip ?? data.NearClip),
            (float)(farClip ?? data.FarClip), data.CullingMask));
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return new
        {
            id = target.Id.ToString(),
            fieldOfView = (float)(fieldOfView ?? data.FieldOfViewDegrees),
            nearClip = (float)(nearClip ?? data.NearClip),
            farClip = (float)(farClip ?? data.FarClip),
        };
    });

    private object SetActiveCamera(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        if (target.Components.FirstOrDefault(SceneComponentTypes.IsCamera) is null)
            throw new InvalidOperationException($"对象不是相机：{target.Name}");
        _renderCameraId = target.Id;
        _renderOrbitAnchor = null;
        _renderCameraInitialFramePending = true;
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        return new { activeId = target.Id.ToString(), name = target.Name };
    });

    private object ListTimelineAssets() => RunOnUi(() =>
    {
        var project = RequireProject();
        var assets = Directory.Exists(project.AssetsPath)
            ? Directory.GetFiles(project.AssetsPath, "*.mvtimeline.json", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(project.AssetsPath, path).Replace('\\', '/')).OrderBy(path => path).ToArray()
            : Array.Empty<string>();
        return new { count = assets.Length, assets };
    });

    private object GetTimelineAsset(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "path");
        var path = ResolveTimelinePath(ReadString(arguments, "path")!);
        if (!File.Exists(path)) throw new FileNotFoundException("未找到时间轴资产", path);
        var timeline = JsonSerializer.Deserialize<TimelineAsset>(File.ReadAllText(path)) ?? new TimelineAsset();
        timeline.Normalize();
        return new { path = Path.GetRelativePath(RequireProject().AssetsPath, path).Replace('\\', '/'), timeline };
    });

    private object CreateTimelineAsset(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "path", "name");
        var path = ResolveTimelinePath(ReadString(arguments, "path")!);
        if (File.Exists(path)) throw new InvalidOperationException("时间轴资产已存在，不允许覆盖");
        var timeline = new TimelineAsset { Name = ReadString(arguments, "name") ?? Path.GetFileNameWithoutExtension(path) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));
        AssetMetaStore.EnsureMeta(path);
        SetStatus($"已新建时间轴：{timeline.Name}");
        return new { path = Path.GetRelativePath(RequireProject().AssetsPath, path).Replace('\\', '/'), timeline };
    });

    private static void RequireTimelineArguments(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须是对象");
        foreach (var property in arguments.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException($"不支持的时间轴参数：{property.Name}");
    }

    private string ResolveTimelinePath(string relativePath)
    {
        var project = RequireProject();
        if (!relativePath.EndsWith(".mvtimeline.json", StringComparison.OrdinalIgnoreCase)) relativePath += ".mvtimeline.json";
        var fullPath = Path.GetFullPath(Path.Combine(project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(project.AssetsPath) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("时间轴路径必须位于Assets目录内");
        return fullPath;
    }

    private object ListMaterials()
    {
        var project = RequireProject();
        var materials = MaterialAsset.LoadFromProject(project);
        return new
        {
            count = materials.Count,
            materials = materials.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).Select(DescribeMaterial).ToArray(),
        };
    }

    private object DescribeMaterial(MaterialAsset material) => new
    {
        name = material.Name,
        path = Path.GetRelativePath(RequireProject().AssetsPath, material.SourcePath).Replace('\\', '/'),
        roughness = material.Roughness,
        metallic = material.Metallic,
        emissive = material.EmissiveScale,
        opacity = material.Opacity,
        volumeScattering = material.VolumeScattering,
        hazeDensity = material.HazeDensity,
        scatteringColor = material.ScatteringColor is { } scatteringColor ? new[] { scatteringColor.R, scatteringColor.G, scatteringColor.B } : null,
        absorptionStep = material.AbsorptionStep,
        absorptionColor = material.AbsorptionColor is { } color ? new[] { color.R, color.G, color.B } : null,
        ior = material.Ior,
        specular = material.Specular,
    };

    private object GetMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        var path = RequireMaterialPath(arguments);
        return File.Exists(path)
            ? new { material = DescribeMaterial(MaterialAsset.Load(path)), exists = true }
            : (object)new { path = MaterialRelativePath(path), exists = false };
    });

    private object CreateMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        //仅允许资产标识和名称，不能通过创建接口注入材质参数
        RequireMaterialArguments(arguments, "path", "name");
        var path = RequireMaterialPath(arguments);
        if (File.Exists(path)) throw new InvalidOperationException("材质资产已存在，不允许覆盖；材质参数请在材质检查器中编辑");
        var name = ReadString(arguments, "name");
        var material = MaterialAsset.CreateDefault(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        material.Save(path);
        ReloadMaterialTable();
        _pipeline?.ResetAccumulation();
        SetStatus($"已新建材质：{material.Name}，参数请在材质检查器中编辑");
        return DescribeMaterial(material);
    });

    private object RemoveMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        var path = RequireMaterialPath(arguments);
        var exists = File.Exists(path);
        if (exists) File.Delete(path);
        //只删除资产文件，组件引用保留以便查询和重新绑定
        ReloadMaterialTable();
        _pipeline?.ResetAccumulation();
        return new { path = MaterialRelativePath(path), removed = exists, referencesPreserved = true };
    });

    private object GetMaterialReferences(JsonElement arguments) => RunOnUi(() =>
    {
        var path = RequireMaterialPath(arguments);
        var relative = MaterialRelativePath(path);
        var references = RequireScene().Objects.SelectMany(item => item.Components
            .Where(SceneComponentTypes.IsVoxelModel)
            .SelectMany(component => SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
            .Where(binding => string.Equals(binding.MaterialAsset.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase))
            .Select(binding => new { objectId = item.Id.ToString(), name = item.Name, localSlot = binding.Slot })).ToArray();
        return new { path = relative, exists = File.Exists(path), scope = "currentScene", count = references.Length, references };
    });

    private object GetModelMaterials(JsonElement arguments) => RunOnUi(() =>
    {
        RejectLegacyMaterialSlot(arguments);
        var (target, component) = RequireMaterialModel(arguments);
        var bindings = SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [];
        var materials = MaterialAsset.LoadFromProject(RequireProject())
            .Select(material => MaterialRelativePath(material.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new
        {
            objectId = target.Id.ToString(), defaultLocalSlot = 0,
            bindings = bindings.Select(binding => new
            {
                localSlot = binding.Slot, path = binding.MaterialAsset,
                exists = materials.Contains(binding.MaterialAsset.Replace('\\', '/')),
            }).ToArray(),
        };
    });

    private object BindModelMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        RequireMaterialArguments(arguments, "objectId", "localSlot", "path");
        var (target, component) = RequireMaterialModel(arguments);
        if (!arguments.TryGetProperty("localSlot", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var slot) || slot <= 0 || slot >= MaterialAsset.TableSlots)
            throw new ArgumentException("localSlot必须为1至65535的整数；槽0始终使用默认材质，不能绑定资产");
        var path = RequireMaterialPath(arguments);
        if (!File.Exists(path)) throw new FileNotFoundException("材质资产不存在，请先用material.create创建", path);
        _ = MaterialAsset.Load(path);
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var bindings = (data.MaterialSlots ?? []).ToDictionary(item => item.Slot);
        var previousPath = bindings.TryGetValue(slot, out var previous) ? previous.MaterialAsset : null;
        var relative = MaterialRelativePath(path);
        bindings[slot] = new MaterialSlotBinding(slot, relative);
        //只更新组件引用，体素存储及其本地槽位编号保持不变
        SaveModelMaterialBindings(target, component, data, bindings);
        return new { objectId = target.Id.ToString(), localSlot = slot, path = relative, previousPath };
    });

    private (SceneObject Target, ComponentRecord Component) RequireMaterialModel(JsonElement arguments)
    {
        var objectId = ReadString(arguments, "objectId");
        if (!StableId.TryParse(objectId ?? string.Empty, out var id))
            throw new ArgumentException("需要有效的objectId；请先用scene.list查询对象标识，不能仅提供slot");
        var target = RequireScene().Objects.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException($"找不到对象：{objectId}");
        var components = target.Components.Where(SceneComponentTypes.IsVoxelModel).ToArray();
        if (components.Length != 1) throw new InvalidOperationException("对象必须恰好包含一个体素模型组件，否则objectId无法唯一确定绑定目标");
        return (target, components[0]);
    }

    private string RequireMaterialPath(JsonElement arguments)
    {
        RejectLegacyMaterialSlot(arguments);
        var path = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("需要相对项目Assets目录的材质path，不支持全局slot或绝对路径");
        var root = Path.GetFullPath(RequireProject().AssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".material", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path必须指向项目Assets目录内的.material资产");
        return fullPath;
    }

    private string MaterialRelativePath(string path) =>
        Path.GetRelativePath(RequireProject().AssetsPath, path).Replace('\\', '/');

    private static void RejectLegacyMaterialSlot(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("材质命令参数必须是对象");
        if (arguments.TryGetProperty("slot", out _))
            throw new ArgumentException("不再支持全局slot；资产请用material.get/create/remove(path)，组件绑定请用model.materials.bind(objectId,localSlot,path)");
    }

    private static void RequireMaterialArguments(JsonElement arguments, params string[] allowed)
    {
        RejectLegacyMaterialSlot(arguments);
        foreach (var property in arguments.EnumerateObject())
            if (property.Name != "commandId" && !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"不支持参数：{property.Name}；材质参数只能在材质检查器中编辑");
    }

    private object DescribeRenderSettings() => DescribeRenderSettings(_renderSettings);

    /// <summary>
    /// 输出实际参与渲染的设置：项目配置与场景环境合并后的结果
    /// </summary>
    private object DescribeEffectiveRenderSettings() => RunOnUi(() =>
        DescribeRenderSettings(ResolveRenderSettings(GetActiveRenderCameraObject())));

    private object DescribeRenderSettings(RenderSettings settings)
    {
        return new
        {
            pathTracing = settings.PathTracingEnabled,
            hardwareRayTracing = settings.HardwareRayTracingAuto
                ? (_vulkan?.SupportsHardwareRayQuery == true)
                : settings.UseHardwareRayTracing,
            hardwareRayTracingAuto = settings.HardwareRayTracingAuto,
            hardwareRayTracingAvailable = _vulkan?.SupportsHardwareRayQuery == true,
            hardwareRayTracingActive = _pipeline?.HardwareShadowsActive ?? false,
            sunShadowMethod = settings.SunShadowMethod.ToString(),
            backend = settings.Backend.ToString(),
            maxSamples = settings.PathMaxAccumulatedSamples,
            maxBounces = settings.MaxBounces,
            resolutionScale = settings.ResolutionScale,
            exposure = settings.Exposure,
            gamma = settings.Gamma,
            saturation = settings.Saturation,
            brightness = settings.Brightness,
            contrast = settings.ColorContrast,
            tone = settings.Tone.ToString(),
            sunEnabled = settings.SunEnabled,
            sunAzimuth = settings.SunAzimuthDegrees,
            sunElevation = settings.SunElevationDegrees,
            sunIntensity = settings.SunIntensity,
            skyAmbient = settings.SkyAmbient,
            skyTop = settings.SkyTop,
            skyHorizon = settings.SkyHorizon,
            sceneBackgroundColor = settings.SceneBackgroundColor,
            ambientOcclusion = settings.AmbientOcclusionEnabled,
            aoStrength = settings.AoStrength,
            fogDensity = settings.FogDensity,
             fogStart = settings.FogStart,
             fogSkyBlend = settings.FogSkyBlend,
             fogMaxOpacity = settings.FogMaxOpacity,
             showWater = settings.ShowWater,
             waterHeight = settings.WaterHeight,
             waterColor = settings.WaterColor,
             waterWaveSize = settings.WaterWaveSize,
             waterWaveLength = settings.WaterWaveLength,
             waterRoughness = settings.WaterRoughness,
             waterAnimationEnabled = settings.WaterAnimationEnabled,
             waterAnimationSpeed = settings.WaterAnimationSpeed,
             waterFlowX = settings.WaterFlowDirection.X,
             waterFlowZ = settings.WaterFlowDirection.Y,
             waterDetailStrength = settings.WaterDetailStrength,
             waterSpecularStrength = settings.WaterSpecularStrength,
             weatherWindStrength = settings.WeatherWindStrength,
             weatherWindDirection = settings.WeatherWindDirection,
             weatherRain = settings.WeatherRainIntensity,
             weatherSnow = settings.WeatherSnowIntensity,
             weatherStorm = settings.WeatherStormIntensity,
             weatherHeatHaze = settings.WeatherHeatHaze,
             weatherLightning = settings.WeatherLightningFrequency,
            showGround = settings.ShowGround,
            transmissiveAbsorption = settings.TransmissiveAbsorption,
            emissiveIntensity = settings.EmissiveIntensity,
            emissiveVoxels = settings.EmissiveVoxels,
            softFillEnabled = settings.SoftFillEnabled,
            softFillIntensity = settings.SoftFillIntensity,
            softFillSize = settings.SoftFillSize,
            softFillDistance = settings.SoftFillDistance,
            denoise = settings.PathDenoising,
        };
    }

    private object ApplyRenderSettings(JsonElement arguments) => RunOnUi(() =>
    {
        var settings = _renderSettings;
        if (ReadBoolean(arguments, "pathTracing") is { } pathTracing) settings.PathTracingEnabled = pathTracing;
        if (ReadNumber(arguments, "maxSamples") is { } samples)
            settings.PathMaxAccumulatedSamples = (int)Math.Clamp((int)samples, 1, 16384);
        if (ReadNumber(arguments, "maxBounces") is { } bounces)
            settings.MaxBounces = (int)Math.Clamp((int)bounces, 1, 64);
        if (ReadNumber(arguments, "exposure") is { } exposure) settings.Exposure = (float)exposure;
        if (ReadNumber(arguments, "gamma") is { } gamma) settings.Gamma = (float)gamma;
        if (ReadNumber(arguments, "saturation") is { } saturation) settings.Saturation = (float)saturation;
        if (ReadNumber(arguments, "brightness") is { } brightness) settings.Brightness = (float)brightness;
        if (ReadNumber(arguments, "contrast") is { } contrast) settings.ColorContrast = (float)contrast;
        if (ReadBoolean(arguments, "sunEnabled") is { } sunEnabled) settings.SunEnabled = sunEnabled;
        if (ReadNumber(arguments, "sunAzimuth") is { } azimuth) settings.SunAzimuthDegrees = (float)azimuth;
        if (ReadNumber(arguments, "sunElevation") is { } elevation) settings.SunElevationDegrees = (float)elevation;
        if (ReadNumber(arguments, "sunIntensity") is { } intensity) settings.SunIntensity = (float)intensity;
        if (ReadNumber(arguments, "skyAmbient") is { } ambient) settings.SkyAmbient = (float)ambient;
        if (ReadColor(arguments, "skyTop") is { } skyTop) settings.SkyTop = skyTop;
        if (ReadColor(arguments, "skyHorizon") is { } skyHorizon) settings.SkyHorizon = skyHorizon;
        if (ReadColor(arguments, "sceneBackgroundColor") is { } backgroundColor)
            settings.SceneBackgroundColor = backgroundColor;
        if (ReadBoolean(arguments, "ambientOcclusion") is { } ao) settings.AmbientOcclusionEnabled = ao;
        if (ReadNumber(arguments, "aoStrength") is { } aoStrength) settings.AoStrength = (float)aoStrength;
        if (ReadNumber(arguments, "aoSamples") is { } aoSamples) settings.AoSamples = (int)aoSamples;
        if (ReadNumber(arguments, "voxelIndirectLightSamples") is { } indirectSamples)
            settings.VoxelIndirectLightSamples = (int)indirectSamples;
        if (ReadNumber(arguments, "shadowSamples") is { } shadowSamples) settings.ShadowSamples = (int)shadowSamples;
        if (ReadNumber(arguments, "realtimeAntiAliasingSamples") is { } aaSamples) settings.RealtimeAntiAliasingSamples = (int)aaSamples;
        if (ReadNumber(arguments, "realtimeAntiAliasingPattern") is { } aaPattern) settings.RealtimeAntiAliasingPattern = (int)aaPattern;
        if (ReadNumber(arguments, "probeBakeRays") is { } probeRays) settings.ProbeBakeRays = (int)probeRays;
        if (ReadNumber(arguments, "probeBakeMaxProbes") is { } probeMaxProbes)
            settings.ProbeBakeMaxProbes = (int)probeMaxProbes;
        if (ReadNumber(arguments, "probeBakeSmoothing") is { } probeSmoothing)
            settings.ProbeBakeSmoothing = (int)probeSmoothing;
        if (ReadNumber(arguments, "probeVolumeGiStrength") is { } probeGi)
            settings.ProbeVolumeGiStrength = (float)probeGi;
        if (ReadNumber(arguments, "realtimeTemporalWeight") is { } temporal)
            settings.RealtimeTemporalWeight = (float)temporal;
         if (ReadNumber(arguments, "fogDensity") is { } fog) settings.FogDensity = (float)fog;
         if (ReadNumber(arguments, "fogStart") is { } fogStart) settings.FogStart = (float)fogStart;
         if (ReadNumber(arguments, "fogSkyBlend") is { } fogSkyBlend) settings.FogSkyBlend = (float)fogSkyBlend;
         if (ReadNumber(arguments, "fogMaxOpacity") is { } fogMaxOpacity) settings.FogMaxOpacity = (float)fogMaxOpacity;
         if (ReadBoolean(arguments, "showWater") is { } showWater) settings.ShowWater = showWater;
         if (ReadNumber(arguments, "waterHeight") is { } waterHeight) settings.WaterHeight = (float)waterHeight;
         if (ReadNumber(arguments, "waterWaveSize") is { } waterWaveSize) settings.WaterWaveSize = (float)waterWaveSize;
         if (ReadNumber(arguments, "waterWaveLength") is { } waterWaveLength) settings.WaterWaveLength = (float)waterWaveLength;
         if (ReadNumber(arguments, "waterRoughness") is { } waterRoughness) settings.WaterRoughness = (float)waterRoughness;
         if (ReadBoolean(arguments, "waterAnimationEnabled") is { } animationEnabled) settings.WaterAnimationEnabled = animationEnabled;
         if (ReadNumber(arguments, "waterAnimationSpeed") is { } animationSpeed) settings.WaterAnimationSpeed = (float)animationSpeed;
         if (ReadNumber(arguments, "waterFlowX") is { } flowX) settings.WaterFlowDirection.X = (float)flowX;
         if (ReadNumber(arguments, "waterFlowZ") is { } flowZ) settings.WaterFlowDirection.Y = (float)flowZ;
         if (ReadNumber(arguments, "waterDetailStrength") is { } detailStrength) settings.WaterDetailStrength = (float)detailStrength;
         if (ReadNumber(arguments, "waterSpecularStrength") is { } specularStrength) settings.WaterSpecularStrength = (float)specularStrength;
         if (ReadNumber(arguments, "weatherWindStrength") is { } windStrength) settings.WeatherWindStrength = (float)windStrength;
         if (ReadNumber(arguments, "weatherWindDirection") is { } windDirection) settings.WeatherWindDirection = (float)windDirection;
         if (ReadNumber(arguments, "weatherRain") is { } rainIntensity) settings.WeatherRainIntensity = (float)rainIntensity;
         if (ReadNumber(arguments, "weatherSnow") is { } snowIntensity) settings.WeatherSnowIntensity = (float)snowIntensity;
         if (ReadNumber(arguments, "weatherStorm") is { } stormIntensity) settings.WeatherStormIntensity = (float)stormIntensity;
         if (ReadNumber(arguments, "weatherHeatHaze") is { } heatHaze) settings.WeatherHeatHaze = (float)heatHaze;
         if (ReadNumber(arguments, "weatherLightning") is { } lightningFrequency) settings.WeatherLightningFrequency = (float)lightningFrequency;
         if (ReadBoolean(arguments, "showGround") is { } ground) settings.ShowGround = ground;
        if (ReadNumber(arguments, "transmissiveAbsorption") is { } absorption)
            settings.TransmissiveAbsorption = (float)absorption;
        if (ReadNumber(arguments, "emissiveIntensity") is { } emissive) settings.EmissiveIntensity = (float)emissive;
        if (ReadBoolean(arguments, "emissiveVoxels") is { } emissiveVoxels) settings.EmissiveVoxels = emissiveVoxels;
        if (ReadBoolean(arguments, "softFillEnabled") is { } softFill) settings.SoftFillEnabled = softFill;
        if (ReadNumber(arguments, "softFillIntensity") is { } areaIntensity)
            settings.SoftFillIntensity = (float)areaIntensity;
        if (ReadColor(arguments, "softFillColor") is { } areaColor) settings.SoftFillColor = areaColor;
        if (ReadNumber(arguments, "softFillSize") is { } areaSize) settings.SoftFillSize = (float)areaSize;
        if (ReadNumber(arguments, "softFillDistance") is { } areaDistance)
            settings.SoftFillDistance = (float)areaDistance;
        if (ReadBoolean(arguments, "denoise") is { } denoise) settings.PathDenoising = denoise;
        // 天空、雾、水、天气、地面属于场景环境资产：渲染前会用场景环境覆盖这些字段，
        // 只改 _renderSettings 会在下一帧被冲掉，因此同时回写到场景文档
        if (_sceneDocument is not null)
        {
            _sceneDocument.Environment.CaptureFrom(settings);
            MarkSceneDocumentDirty();
        }
        ApplyRenderPresetSettings(settings, null);
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        SetStatus("已更新渲染设置");
        return DescribeRenderSettings();
    });

    private object ResetRenderAccumulation() => RunOnUi(() =>
    {
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        return new { reset = true };
    });

    private object SetModelScale(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var scaleX = ReadNumber(arguments, "scaleX") ?? throw new ArgumentException("缺少 scaleX");
        var scaleY = ReadNumber(arguments, "scaleY") ?? throw new ArgumentException("缺少 scaleY");
        var scaleZ = ReadNumber(arguments, "scaleZ") ?? throw new ArgumentException("缺少 scaleZ");
        ApplyModelScale(target, (float)scaleX, (float)scaleY, (float)scaleZ);
        MarkSceneDocumentDirty();
        return DescribeSceneObject(target);
    });

    private static readonly (string Id, string Title)[] PanelIds =
    [
        ("hierarchy", "层级"), ("assets", "项目资产"), ("inspector", "检查器"),
        ("renderConfig", "渲染配置"), ("settings", "设置"), ("modelingTools", "建模工具"),
        ("grid", "网格"), ("outlines", "体素描边"), ("statistics", "场景统计"),
        ("renderStatistics", "渲染统计"), ("generator", "生成器"), ("framePreset", "画面预设"),
        ("exportDialog", "导出对话框"), ("performance", "性能分析"), ("guide", "入门指引"),
    ];

    private bool GetPanelVisible(string id) => id switch
    {
        "hierarchy" => _showHierarchy,
        "assets" => _showProjectAssets,
        "inspector" => _showInspector,
        "renderConfig" => _showRenderConfig,
        "settings" => _showSettingsWindow,
        "modelingTools" => _showModelingTools,
        "grid" => _showGrid,
        "outlines" => _showVoxelOutlines,
        "statistics" => _showSceneStatistics,
        "renderStatistics" => _showRenderStatistics,
        "generator" => _showGeneratorPanel,
        "framePreset" => _showFramePresetManager,
        "exportDialog" => _showExportDialog,
        "performance" => _showPerformanceAnalyzer,
        "guide" => _showGettingStarted,
        _ => throw new ArgumentException($"未知面板：{id}"),
    };

    private void SetPanelVisible(string id, bool visible)
    {
        switch (id)
        {
            case "hierarchy": _showHierarchy = visible; break;
            case "assets": _showProjectAssets = visible; break;
            case "inspector": _showInspector = visible; break;
            case "renderConfig": _showRenderConfig = visible; break;
            case "settings": _showSettingsWindow = visible; break;
            case "modelingTools": _showModelingTools = visible; break;
            case "grid": _showGrid = visible; break;
            case "outlines": _showVoxelOutlines = visible; break;
            case "statistics": _showSceneStatistics = visible; break;
            case "renderStatistics": _showRenderStatistics = visible; break;
            case "generator": _showGeneratorPanel = visible; break;
            case "framePreset": _showFramePresetManager = visible; break;
            case "exportDialog": _showExportDialog = visible; break;
            case "performance": _showPerformanceAnalyzer = visible; break;
            case "guide": _showGettingStarted = visible; break;
            default: throw new ArgumentException($"未知面板：{id}");
        }
    }

    private object ListPanels() => new
    {
        count = PanelIds.Length,
        panels = PanelIds.Select(item => new { id = item.Id, title = item.Title, visible = GetPanelVisible(item.Id) }).ToArray(),
    };

    /// <summary>
    /// 切换中央页面：与菜单里的“场景/渲染”走同一个请求字段，下一帧聚焦对应窗口
    /// </summary>
    /// <param name="arguments">命令参数，tab 取 scene 或 render</param>
    /// <returns>目标页面编号</returns>
    private object SetCenterTab(JsonElement arguments) => RunOnUi(() =>
    {
        var tab = ReadString(arguments, "tab") ?? throw new ArgumentException("缺少 tab");
        var target = tab.Equals("render", StringComparison.OrdinalIgnoreCase) ? 1
            : tab.Equals("scene", StringComparison.OrdinalIgnoreCase) ? 0
            : throw new ArgumentException("tab 只能是 scene 或 render");
        _requestedCenterTab = target;
        return new { centerTab = target };
    });

    /// <summary>
    /// 开关强制性能采样：不打开分析器窗口也能取 GPU 时间戳
    /// </summary>
    /// <param name="arguments">参数，enabled</param>
    /// <returns>当前强制采样状态</returns>
    private object SetForcedProfiling(JsonElement arguments) => RunOnUi(() =>
    {
        _forcedProfiling = ReadBoolean(arguments, "enabled") ?? throw new ArgumentException("缺少 enabled");
        _analysisPaused = false;
        return new { forcedProfiling = _forcedProfiling, active = PerformanceProbeActive };
    });

    /// <summary>
    /// 设置相机后处理栈里某个效果
    /// </summary>
    /// <remarks>
    /// 模板相机开着覆盖项目默认后处理，画质标定必须改相机栈上的字段才生效
    /// </remarks>
    /// <param name="arguments">参数，name、effect、enabled、intensity、samples</param>
    /// <returns>写入后的效果状态</returns>
    private object SetCameraPostProcess(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsCamera)
            ?? throw new InvalidOperationException("对象没有相机组件");
        var data = SceneComponentTypes.ReadCamera(component);
        var stack = data.PostProcess ?? throw new InvalidOperationException("相机没有后处理栈");
        var effectName = ReadString(arguments, "effect") ?? throw new ArgumentException("缺少 effect");
        if (!Enum.TryParse<CameraPostProcessEffectType>(effectName, true, out var type))
            throw new ArgumentException($"未知后处理效果：{effectName}");
        var effect = stack.Find(type) ?? throw new InvalidOperationException($"后处理栈里没有 {type}");
        if (ReadBoolean(arguments, "enabled") is { } enabled) effect.Enabled = enabled;
        if (ReadNumber(arguments, "intensity") is { } intensity) effect.Intensity = (float)intensity;
        if (ReadNumber(arguments, "samples") is { } samples) effect.Samples = (int)samples;
        SceneComponentTypes.WriteCamera(component, data);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return new { effect = type.ToString(), enabled = effect.Enabled, intensity = effect.Intensity, samples = effect.Samples };
    });
    private object SetPanel(JsonElement arguments) => RunOnUi(() =>    {
        var id = ReadString(arguments, "panel") ?? throw new ArgumentException("缺少 panel");
        var visible = ReadBoolean(arguments, "visible") ?? throw new ArgumentException("缺少 visible");
        SetPanelVisible(id, visible);
        return new { panel = id, visible };
    });

    private object DescribeProject()
    {
        var project = RequireProject();
        return new
        {
            name = project.Manifest.Name,
            rootPath = project.RootPath,
            assetsPath = project.AssetsPath,
            sceneName = _sceneDocument?.Name,
            scenePath = _activeSceneRelativePath,
            objectCount = _sceneDocument?.Objects.Count ?? 0,
            layerCount = _sceneDocument?.LayerNames.Count ?? 0,
            cameraCount = GetRenderCameraObjects().Count,
            materialCount = MaterialAsset.LoadFromProject(project).Count,
            dirty = _sceneDocumentDirty,
            renderSamples = _pipeline?.AccumulatedSamples ?? 0,
        };
    }
}
