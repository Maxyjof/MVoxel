using System.Text.Json;
using System.Threading;
using MVoxel.Core;
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
        new("scene.selection", "场景", "读取选中", "读取当前选中对象的摘要", []),
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
        new("material.get", "材质", "读取材质", "按槽位读取材质参数",
            [Param("slot", "integer", true, "材质槽位")]),
        new("material.set", "材质", "写入材质", "按槽位新建或覆盖材质，未给出的字段保持原值",
            [Param("slot", "integer", true, "材质槽位"), Param("name", "string", false, "材质名称"),
             Param("roughness", "number", false, "粗糙度 0.01–1"), Param("metallic", "number", false, "金属度 0–1"),
             Param("emissive", "number", false, "发光强度倍率 0–32"),
             Param("opacity", "number", false, "半透明不透明度 0–1"),
             Param("volumeScattering", "number", false, "体积散射强度 0–1"),
             Param("hazeDensity", "number", false, "雾化密度 0–8"),
             Param("scatteringColor", "number[3]", false, "散射色 r/g/b，0–255"),
             Param("absorptionStep", "number", false, "半透明吸收步长倍率 0–8"),
             Param("absorptionColor", "number[3]", false, "吸收色 r/g/b，0–255"),
             Param("ior", "number", false, "折射率，1 表示不偏折，玻璃约 1.5"),
             Param("specular", "number", false, "镜面强度（菲涅尔 F0 倍率）")]),
        new("material.remove", "材质", "删除材质", "删除槽位对应的材质文件",
            [Param("slot", "integer", true, "材质槽位")]),
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
             Param("ambientOcclusion", "boolean", false, "是否启用环境光遮蔽"),
             Param("aoStrength", "number", false, "环境光遮蔽强度"),
             Param("fogDensity", "number", false, "雾浓度"),
             Param("showGround", "boolean", false, "是否显示地面"),
             Param("transmissiveAbsorption", "number", false, "半透明吸收浓度"),
             Param("denoise", "boolean", false, "是否降噪")]),
        new("render.resetAccumulation", "渲染", "重置累积", "丢弃已累积样本，重新开始采样", []),
        new("model.scale", "模型", "设置模型缩放", "按三轴倍率重采样体素模型",
            [.. TargetIdOrName, Param("scaleX", "number", true, "X 轴倍率"),
             Param("scaleY", "number", true, "Y 轴倍率"), Param("scaleZ", "number", true, "Z 轴倍率")]),
        new("ui.panels", "界面", "列出面板", "列出可开关的界面面板与当前状态", []),
        new("ui.setPanel", "界面", "开关面板", "显示或隐藏指定界面面板",
            [Param("panel", "string", true, "面板标识，见 ui.panels"), Param("visible", "boolean", true, "是否显示")]),
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
            "material.set" => SetMaterial(payload),
            "material.remove" => RemoveMaterial(payload),
            "render.settings.get" => DescribeRenderSettings(),
            "render.settings.set" => ApplyRenderSettings(payload),
            "render.resetAccumulation" => ResetRenderAccumulation(),
            "model.scale" => SetModelScale(payload),
            "ui.panels" => ListPanels(),
            "ui.setPanel" => SetPanel(payload),
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
    private object RunOnUi(Func<object?> action)
    {
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

    /// <summary>对象的统一摘要，读写命令共用</summary>
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

    private object ListMaterials()
    {
        var project = RequireProject();
        var materials = MaterialAsset.LoadFromProject(project);
        return new
        {
            count = materials.Count,
            materials = materials.OrderBy(item => item.Slot).Select(DescribeMaterial).ToArray(),
        };
    }

    private static object DescribeMaterial(MaterialAsset material) => new
    {
        slot = material.Slot,
        name = material.Name,
        path = material.SourcePath,
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

    private object GetMaterial(JsonElement arguments)
    {
        var project = RequireProject();
        var slot = (int?)ReadNumber(arguments, "slot") ?? throw new ArgumentException("缺少 slot");
        var matches = MaterialAsset.LoadFromProject(project).Where(item => item.Slot == slot).ToArray();
        // 同一槽位有多份定义时只有一份生效，这里把冲突报出来，避免改了文件画面却不变
        var material = matches.FirstOrDefault();
        return material is null
            ? new { slot, exists = false }
            : new { material = DescribeMaterial(material), conflictCount = matches.Length - 1 };
    }

    private object SetMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        var project = RequireProject();
        var slot = (int?)ReadNumber(arguments, "slot") ?? throw new ArgumentException("缺少 slot");
        slot = Math.Clamp(slot, 0, MaterialAsset.TableSlots - 1);
        var materials = MaterialAsset.LoadFromProject(project);
        var material = materials.FirstOrDefault(item => item.Slot == slot);
        var isNew = material is null;
        material ??= MaterialAsset.CreateDefault(slot);
        var name = ReadString(arguments, "name");
        if (!string.IsNullOrWhiteSpace(name)) material.Name = name;
        if (ReadNumber(arguments, "roughness") is { } roughness) material.Roughness = (float)roughness;
        if (ReadNumber(arguments, "metallic") is { } metallic) material.Metallic = (float)metallic;
        if (ReadNumber(arguments, "emissive") is { } emissive) material.EmissiveScale = (float)emissive;
        if (ReadNumber(arguments, "opacity") is { } opacity) material.Opacity = (float)opacity;
        if (ReadNumber(arguments, "volumeScattering") is { } scattering) material.VolumeScattering = (float)scattering;
        if (ReadNumber(arguments, "hazeDensity") is { } haze) material.HazeDensity = (float)haze;
        if (ReadVector(arguments, "scatteringColor") is { } scatterRgb)
            material.ScatteringColor = new Rgba32(
                (byte)Math.Clamp((int)Math.Round(scatterRgb[0]), 0, 255),
                (byte)Math.Clamp((int)Math.Round(scatterRgb[1]), 0, 255),
                (byte)Math.Clamp((int)Math.Round(scatterRgb[2]), 0, 255));
        if (ReadNumber(arguments, "absorptionStep") is { } step) material.AbsorptionStep = (float)step;
        if (ReadNumber(arguments, "ior") is { } ior) material.Ior = (float)ior;
        if (ReadNumber(arguments, "specular") is { } specular) material.Specular = (float)specular;
        if (ReadVector(arguments, "absorptionColor") is { } rgb)
            material.AbsorptionColor = new Rgba32(
                (byte)Math.Clamp((int)Math.Round(rgb[0]), 0, 255),
                (byte)Math.Clamp((int)Math.Round(rgb[1]), 0, 255),
                (byte)Math.Clamp((int)Math.Round(rgb[2]), 0, 255));
        material.Normalize();
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        // 已存在的材质必须写回原文件：改名后按新名字另存会让同一槽位出现两份定义，
        // 加载顺序决定谁生效，画面会莫名跳变
        var path = isNew || material.SourcePath.Length == 0
            ? Path.Combine(directory, $"{MaterialFileName(material.Name)}.material")
            : material.SourcePath;
        material.Save(path);
        ReloadMaterialTable();
        _pipeline?.ResetAccumulation();
        SetStatus($"已{(isNew ? "新建" : "更新")}材质：{material.Name}（槽位 {material.Slot}）");
        return DescribeMaterial(material);
    });

    private object RemoveMaterial(JsonElement arguments) => RunOnUi(() =>
    {
        var project = RequireProject();
        var slot = (int?)ReadNumber(arguments, "slot") ?? throw new ArgumentException("缺少 slot");
        var material = MaterialAsset.LoadFromProject(project).FirstOrDefault(item => item.Slot == slot);
        if (material is null) return new { slot, removed = false };
        if (material.SourcePath.Length > 0 && File.Exists(material.SourcePath)) File.Delete(material.SourcePath);
        ReloadMaterialTable();
        _pipeline?.ResetAccumulation();
        return new { slot, removed = true, name = material.Name };
    });

    /// <summary>材质文件用名称命名，非法字符交给编辑器统一的清理函数处理</summary>
    private static string MaterialFileName(string name)
    {
        var text = SanitizeFileName(name);
        return text.Length == 0 ? "材质" : text;
    }

    private object DescribeRenderSettings()
    {
        var settings = _renderSettings;
        return new
        {
            pathTracing = settings.PathTracingEnabled,
            hardwareRayTracing = settings.UseHardwareRayTracing,
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
            ambientOcclusion = settings.AmbientOcclusionEnabled,
            aoStrength = settings.AoStrength,
            fogDensity = settings.FogDensity,
            showGround = settings.ShowGround,
            transmissiveAbsorption = settings.TransmissiveAbsorption,
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
        if (ReadBoolean(arguments, "ambientOcclusion") is { } ao) settings.AmbientOcclusionEnabled = ao;
        if (ReadNumber(arguments, "aoStrength") is { } aoStrength) settings.AoStrength = (float)aoStrength;
        if (ReadNumber(arguments, "fogDensity") is { } fog) settings.FogDensity = (float)fog;
        if (ReadBoolean(arguments, "showGround") is { } ground) settings.ShowGround = ground;
        if (ReadNumber(arguments, "transmissiveAbsorption") is { } absorption)
            settings.TransmissiveAbsorption = (float)absorption;
        if (ReadBoolean(arguments, "denoise") is { } denoise) settings.PathDenoising = denoise;
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

    private object SetPanel(JsonElement arguments) => RunOnUi(() =>
    {
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
