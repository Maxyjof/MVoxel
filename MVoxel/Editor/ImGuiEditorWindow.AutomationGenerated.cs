using System.Numerics;
using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 程序化体素组件的 MCP 命令：曲线渲染器与树生成器的读写、生成与实例化
/// </summary>
/// <remarks>
/// 这些命令通过 editor.commands 发现、editor.exec 调用，
/// 与界面操作走同一套数据与重建流程，脚本不需要模拟鼠标
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 读取曲线渲染器参数、控制点与生成状态
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>曲线数据与体素统计</returns>
    private object DescribeCurveRenderer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsCurveRenderer)
                        ?? throw new InvalidOperationException("对象上没有曲线渲染器组件");
        return BuildCurveResult(target, component);
    });

    /// <summary>
    /// 写入曲线参数或整体替换控制点，默认立即重建体素
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>写入后的曲线数据与体素统计</returns>
    private object SetCurveRenderer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsCurveRenderer)
                        ?? throw new InvalidOperationException("对象上没有曲线渲染器组件");
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (ReadEnum(arguments, "interpolation", data.Interpolation, out CurveInterpolation interpolation))
            data = data with { Interpolation = interpolation };
        if (ReadEnum(arguments, "shape", data.Shape, out CurveBrushShape shape))
            data = data with { Shape = shape };
        if (ReadBoolean(arguments, "closed") is { } closed) data = data with { Closed = closed };
        if (ReadEnum(arguments, "output", data.Output, out CurveOutputMode output)) data = data with { Output = output };
        if (ReadString(arguments, "sourceKind") is { Length: > 0 } sourceKindText)
        {
            if (!Enum.TryParse<VoxelSourceKind>(sourceKindText, ignoreCase: true, out var sourceKind))
                throw new ArgumentException($"不支持的体素源种类：{sourceKindText}");
            data = data with
            {
                ScatterSource = new VoxelSourceRef(sourceKind,
                    ReadString(arguments, "sourcePath") ?? data.ScatterSource.AssetPath),
            };
        }
        else if (ReadString(arguments, "sourcePath") is { Length: > 0 } sourcePath)
        {
            data = data with { ScatterSource = new VoxelSourceRef(VoxelSourceKind.ModelAsset, sourcePath) };
        }

        if (ReadEnum(arguments, "spacingMode", data.SpacingMode, out ScatterSpacingMode spacingMode))
            data = data with { SpacingMode = spacingMode };
        if (ReadEnum(arguments, "scatterAlign", data.Align, out ScatterAlign scatterAlign))
            data = data with { Align = scatterAlign };
        if (ReadEnum(arguments, "scatterPivot", data.Pivot, out ScatterPivot scatterPivot))
            data = data with { Pivot = scatterPivot };
        if (ReadNumber(arguments, "spacing") is { } spacing) data = data with { Spacing = (float)spacing };
        if (ReadNumber(arguments, "scatterCount") is { } scatterCount) data = data with { ScatterCount = (int)scatterCount };
        if (ReadNumber(arguments, "roll") is { } roll) data = data with { RollDegrees = (float)roll };
        if (ReadNumber(arguments, "jitterDistance") is { } jitterDistance) data = data with { JitterDistance = (float)jitterDistance };
        if (ReadNumber(arguments, "jitterAngle") is { } jitterAngle) data = data with { JitterAngle = (float)jitterAngle };
        if (ReadNumber(arguments, "startOffset") is { } startOffset) data = data with { StartOffset = (float)startOffset };
        if (ReadNumber(arguments, "endOffset") is { } endOffset) data = data with { EndOffset = (float)endOffset };
        if (ReadNumber(arguments, "groundOffset") is { } groundOffset) data = data with { GroundOffset = (int)groundOffset };
        if (ReadNumber(arguments, "scatterSeed") is { } scatterSeed) data = data with { ScatterSeed = (int)scatterSeed };
        if (ReadBoolean(arguments, "groundSnap") is { } groundSnap) data = data with { GroundSnap = groundSnap };
        if (ReadBoolean(arguments, "closedWrap") is { } closedWrap) data = data with { ClosedWrap = closedWrap };
        if (ReadBoolean(arguments, "emissive") is { } emissive) data = data with { Emissive = emissive };
        if (ReadBoolean(arguments, "editing") is { } editing) data = data with { Editing = editing };
        if (ReadNumber(arguments, "resolution") is { } resolution)
            data = data with { Resolution = (int)Math.Round(resolution) };
        if (ReadNumber(arguments, "step") is { } step) data = data with { Step = (float)step };
        if (ReadNumber(arguments, "radius") is { } radius) data = data with { Radius = (float)radius };
        if (ReadNumber(arguments, "materialSlot") is { } slot) data = data with { MaterialSlot = (ushort)Math.Clamp((int)slot, 0, ushort.MaxValue) };
        if (ReadNumber(arguments, "sortingOrder") is { } sortingOrder) data = data with { SortingOrder = (int)sortingOrder };
        if (ReadString(arguments, "sortingLayer") is { Length: > 0 } sortingLayer) data = data with { SortingLayer = sortingLayer };
        if (ReadColor(arguments, "color") is { } color)
            data = data with
            {
                ColorR = ToByte(color.X), ColorG = ToByte(color.Y), ColorB = ToByte(color.Z),
                ColorA = ReadNumber(arguments, "alpha") is { } alpha ? ToByte((float)alpha) : data.ColorA,
            };
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            data = data with { Points = ReadCurvePoints(points) };
        SceneComponentTypes.WriteCurveRenderer(component, data);
        var generate = ReadBoolean(arguments, "generate") ?? true;
        if (generate) RebuildGeneratedVoxels(target, component, string.Empty);
        else
        {
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
        }

        return BuildCurveResult(target, component);
    });

    /// <summary>
    /// 按当前曲线参数重新生成体素
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>生成后的体素统计</returns>
    private object GenerateCurveRenderer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsCurveRenderer)
                        ?? throw new InvalidOperationException("对象上没有曲线渲染器组件");
        RebuildGeneratedVoxels(target, component, string.Empty);
        return BuildCurveResult(target, component);
    });

    /// <summary>
    /// 读取树生成器参数与生成状态
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>树参数与体素统计</returns>
    private object DescribeTreeGenerator(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTreeGenerator)
                        ?? throw new InvalidOperationException("对象上没有树生成器组件");
        return BuildTreeResult(target, component);
    });

    /// <summary>
    /// 写入树参数，默认立即生成新树
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>写回后的树参数与体素统计</returns>
    private object SetTreeGenerator(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTreeGenerator)
                        ?? throw new InvalidOperationException("对象上没有树生成器组件");
        var data = ApplyTreeFields(SceneComponentTypes.ReadTreeGenerator(component), arguments);
        SceneComponentTypes.WriteTreeGenerator(component, data);
        if (ReadBoolean(arguments, "generate") ?? true) RebuildGeneratedVoxels(target, component, string.Empty);
        else
        {
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
        }

        return BuildTreeResult(target, component);
    });

    /// <summary>
    /// 按当前树参数重新生成体素
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>生成后的体素统计</returns>
    private object GenerateTreeGenerator(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTreeGenerator)
                        ?? throw new InvalidOperationException("对象上没有树生成器组件");
        RebuildGeneratedVoxels(target, component, string.Empty);
        return BuildTreeResult(target, component);
    });

    /// <summary>
    /// 把树生成器实例化为体素模型，之后可以进入绘制模式
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>模型组件摘要；撤销可恢复生成器</returns>
    private object InstantiateTreeGenerator(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTreeGenerator)
                        ?? throw new InvalidOperationException("对象上没有树生成器组件");
        if (target.Components.Any(SceneComponentTypes.IsVoxelModel))
            throw new InvalidOperationException("对象已经有模型组件，无法再实例化");
        InstantiateGeneratedVoxels(target, component, "体素模型");
        var model = target.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel)
                    ?? throw new InvalidOperationException("实例化失败：没有生成模型组件");
        var data = SceneComponentTypes.ReadVoxelModel(model);
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            typeId = SceneComponentTypes.VoxelModel,
            voxelCount = _objectVoxels.TryGetWorld(target.Id, out var world) ? world.OccupiedVoxelCount : 0,
            sortingLayer = data.SortingLayer,
            sortingOrder = data.SortingOrder,
            status = _statusText,
            canUndo = true,
        };
    });

    /// <summary>
    /// 新建一个带曲线渲染器的对象并生成体素
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>新对象与曲线摘要</returns>
    private object CreateCurveObject(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var item = CreateGeneratedObject(scene, arguments, "曲线渲染器");
        var component = SceneComponentTypes.CreateCurveRenderer(CurveRendererComponentData.CreateDefault());
        item.Components.Add(component);
        RebuildGeneratedVoxels(item, component, string.Empty);
        MarkSceneDocumentDirty();
        return BuildCurveResult(item, component);
    });

    /// <summary>
    /// 新建一个带树生成器的对象并生成一棵树
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>新对象与树参数摘要</returns>
    private object CreateTreeObject(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var item = CreateGeneratedObject(scene, arguments, "树生成器");
        var data = ApplyTreeFields(TreeGeneratorComponentData.Default, arguments);
        var component = SceneComponentTypes.CreateTreeGenerator(data);
        item.Components.Add(component);
        RebuildGeneratedVoxels(item, component, string.Empty);
        MarkSceneDocumentDirty();
        return BuildTreeResult(item, component);
    });

    /// <summary>
    /// 列出对象上的全部组件与体素来源摘要
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>组件清单</returns>
    private object DescribeSceneComponents(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            components = target.Components.Select(component => new
            {
                typeId = component.TypeId,
                enabled = component.Enabled,
                schemaVersion = component.SchemaVersion,
                voxelCount = SceneComponentTypes.IsGeneratedVoxelComponent(component) ||
                             SceneComponentTypes.IsVoxelModel(component)
                    ? _objectVoxels.TryGetWorld(target.Id, out var world) ? world.OccupiedVoxelCount : 0
                    : 0,
                pendingGeneration = SceneComponentTypes.IsGeneratedVoxelComponent(component) &&
                                    SceneComponentTypes.TryReadGeneratedSignature(component, out var signature) &&
                                    signature != SceneComponentTypes.ComputeGeneratedSignature(component),
                editableVoxels = SceneComponentTypes.IsVoxelModel(component) && component.Enabled,
            }).ToArray(),
            voxelCount = _objectVoxels.TryGetWorld(target.Id, out var objectWorld) ? objectWorld.OccupiedVoxelCount : 0,
        };
    });

    /// <summary>
    /// 新建对象：格变换、可选父级与位置，名字自动去重
    /// </summary>
    /// <param name="scene">场景文档</param>
    /// <param name="arguments">命令参数</param>
    /// <param name="defaultName">默认名称</param>
    /// <returns>新对象</returns>
    private SceneObject CreateGeneratedObject(SceneDocument scene, JsonElement arguments, string defaultName)
    {
        var baseName = ReadString(arguments, "name") ?? defaultName;
        var name = baseName;
        for (var suffix = 2; scene.Objects.Any(item => item.Name == name); suffix++) name = $"{baseName} {suffix}";
        StableId? parentId = null;
        if (ReadString(arguments, "parentId") is { Length: > 0 } parentText)
        {
            var parent = scene.Objects.FirstOrDefault(item => string.Equals(item.Id.ToString(), parentText,
                StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"父对象不存在：{parentText}");
            parentId = parent.Id;
        }

        var position = ReadVector(arguments, "position");
        var grid = position is { Length: 3 }
            ? new GridTransform(new VoxelCoord64((long)Math.Round(position[0]), (long)Math.Round(position[1]),
                (long)Math.Round(position[2])), OrthogonalRotation.Identity)
            : GridTransform.Identity;
        var item = new SceneGraph(scene).Add(name, parentId, TransformComponent.FromGrid(grid));
        _selectedSceneObjectId = item.Id;
        _revealHierarchySelection = item.Id;
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        return item;
    }

    /// <summary>
    /// 拼出曲线命令的返回结构
    /// </summary>
    /// <param name="target">场景对象</param>
    /// <param name="component">曲线组件</param>
    /// <returns>曲线参数、控制点与体素统计</returns>
    private object BuildCurveResult(SceneObject target, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            typeId = SceneComponentTypes.CurveRenderer,
            points = data.Points.Select(point => new
            {
                position = new[] { point.Position.X, point.Position.Y, point.Position.Z },
                tangentIn = new[] { point.TangentIn.X, point.TangentIn.Y, point.TangentIn.Z },
                tangentOut = new[] { point.TangentOut.X, point.TangentOut.Y, point.TangentOut.Z },
                rotation = new[] { point.Rotation.X, point.Rotation.Y, point.Rotation.Z, point.Rotation.W },
                tangentMode = point.TangentMode.ToString(),
                width = point.Width,
            }).ToArray(),
            closed = data.Closed,
            interpolation = data.Interpolation.ToString(),
            resolution = data.Resolution,
            step = data.Step,
            shape = data.Shape.ToString(),
            radius = data.Radius,
            color = new[] { (int)data.ColorR, data.ColorG, data.ColorB, data.ColorA },
            emissive = data.Emissive,
            materialSlot = data.MaterialSlot,
            sortingLayer = data.SortingLayer,
            sortingOrder = data.SortingOrder,
            editing = data.Editing,
            output = data.Output.ToString(),
            scatterSourceKind = data.ScatterSource.Kind.ToString(),
            scatterSourcePath = data.ScatterSource.AssetPath,
            spacingMode = data.SpacingMode.ToString(),
            spacing = data.Spacing,
            scatterCount = data.ScatterCount,
            scatterAlign = data.Align.ToString(),
            scatterPivot = data.Pivot.ToString(),
            roll = data.RollDegrees,
            jitterDistance = data.JitterDistance,
            jitterAngle = data.JitterAngle,
            startOffset = data.StartOffset,
            endOffset = data.EndOffset,
            groundSnap = data.GroundSnap,
            groundOffset = data.GroundOffset,
            scatterSeed = data.ScatterSeed,
            closedWrap = data.ClosedWrap,
            scatterMessage = SceneComponentTypes.LastScatterMessage,
            scatterStations = SceneComponentTypes.LastScatterStations,
            scatterPlaced = SceneComponentTypes.LastScatterPlaced,
            generation = DescribeGenerationState(target, component),
        };
    }

    /// <summary>
    /// 拼出树命令的返回结构
    /// </summary>
    /// <param name="target">场景对象</param>
    /// <param name="component">树组件</param>
    /// <returns>树参数与体素统计</returns>
    private object BuildTreeResult(SceneObject target, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadTreeGenerator(component);
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            typeId = SceneComponentTypes.TreeGenerator,
            style = data.Style.ToString(),
            seed = data.Seed,
            trunkHeight = data.TrunkHeight,
            trunkRadius = data.TrunkRadius,
            trunkLean = data.TrunkLean,
            trunkBend = data.TrunkBend,
            trunkTaper = data.TrunkTaper,
            rootFlare = data.RootFlare,
            branchCount = data.BranchCount,
            branchLength = data.BranchLength,
            branchThickness = data.BranchThickness,
            branchAngle = data.BranchAngle,
            branchDroop = data.BranchDroop,
            branchDepth = data.BranchDepth,
            crownStart = data.CrownStart,
            crownRadius = data.CrownRadius,
            crownHeight = data.CrownHeight,
            crownShape = data.CrownShape.ToString(),
            leafDensity = data.LeafDensity,
            leafSize = data.LeafSize,
            barkColor = new[] { (int)data.BarkR, data.BarkG, data.BarkB },
            leafColor = new[] { (int)data.LeafR, data.LeafG, data.LeafB },
            leaf2Color = new[] { (int)data.Leaf2R, data.Leaf2G, data.Leaf2B },
            colorVariation = data.ColorVariation,
            emissiveLeaves = data.EmissiveLeaves,
            materialSlot = data.MaterialSlot,
            sortingLayer = data.SortingLayer,
            sortingOrder = data.SortingOrder,
            generation = DescribeGenerationState(target, component),
        };
    }

    /// <summary>
    /// 生成状态：体素数量、负载大小、是否截断与参数是否已生效
    /// </summary>
    /// <param name="target">场景对象</param>
    /// <param name="component">程序化组件</param>
    /// <returns>生成状态摘要</returns>
    private object DescribeGenerationState(SceneObject target, ComponentRecord component)
    {
        var voxelCount = _objectVoxels.TryGetWorld(target.Id, out var world) ? world.OccupiedVoxelCount : 0;
        var hasSignature = SceneComponentTypes.TryReadGeneratedSignature(component, out var stored);
        return new
        {
            voxelCount,
            payloadBytes = SceneComponentTypes.GetGeneratedVoxelPayloadLength(component) * 3 / 4,
            truncated = _generatedVoxelTruncated.TryGetValue(target.Id, out var truncated) && truncated,
            pending = !hasSignature || stored != SceneComponentTypes.ComputeGeneratedSignature(component),
        };
    }

    /// <summary>
    /// 解析曲线控制点数组
    /// </summary>
    /// <param name="points">JSON 数组</param>
    /// <returns>控制点集合</returns>
    private static CurvePointData[] ReadCurvePoints(JsonElement points)
    {
        var result = new List<CurvePointData>(points.GetArrayLength());
        foreach (var item in points.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var position = ReadVector(item, "position") ?? new[] { 0D, 0D, 0D };
            var tangentIn = ReadVector(item, "tangentIn") ?? ReadVector(item, "in") ?? new[] { 0D, 0D, 0D };
            var tangentOut = ReadVector(item, "tangentOut") ?? ReadVector(item, "out") ?? new[] { 0D, 0D, 0D };
            var rotation = ReadVector4(item, "rotation");
            if (rotation is null && ReadVector(item, "euler") is { } euler)
            {
                var quaternion = CurveRotation.FromEulerDegrees(new Vector3((float)euler[0], (float)euler[1],
                    (float)euler[2]));
                rotation = new[] { (double)quaternion.X, quaternion.Y, quaternion.Z, quaternion.W };
            }

            var mode = CurveTangentMode.Auto;
            if (ReadEnum(item, "tangentMode", mode, out CurveTangentMode parsedMode)) mode = parsedMode;
            else if (ReadEnum(item, "mode", mode, out var shortMode)) mode = shortMode;
            var width = ReadNumber(item, "width") is { } value ? (float)value : 1F;
            result.Add(new CurvePointData(
                new Vector3((float)position[0], (float)position[1], (float)position[2]),
                new Vector3((float)tangentIn[0], (float)tangentIn[1], (float)tangentIn[2]),
                new Vector3((float)tangentOut[0], (float)tangentOut[1], (float)tangentOut[2]),
                rotation is { Length: 4 }
                    ? Quaternion.Normalize(new Quaternion((float)rotation[0], (float)rotation[1],
                        (float)rotation[2], (float)rotation[3]))
                    : Quaternion.Identity,
                mode, width));
        }

        return result.ToArray();
    }

    /// <summary>
    /// 读取四分量向量数组
    /// </summary>
    private static double[]? ReadVector4(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) return null;
        return [value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble(), value[3].GetDouble()];
    }

    /// <summary>
    /// 读取枚举参数，同时接受名称与整数
    /// </summary>
    private static bool ReadEnum<TEnum>(JsonElement arguments, string name, TEnum fallback, out TEnum value)
        where TEnum : struct, Enum
    {
        value = fallback;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.String &&
            Enum.TryParse(property.GetString(), ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) &&
            Enum.IsDefined((TEnum)Enum.ToObject(typeof(TEnum), number)))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), number);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把命令参数合并到树参数上，未给出的字段保持原值
    /// </summary>
    /// <param name="data">原参数</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>合并后的参数</returns>
    private static TreeGeneratorComponentData ApplyTreeFields(TreeGeneratorComponentData data,
        JsonElement arguments)
    {
        // 风格放在最前面：切换风格会套用推荐参数，后面的显式字段优先
        if (ReadEnum(arguments, "style", data.Style, out TreeStyle style)) data = data.WithStyleDefaults(style);
        if (ReadNumber(arguments, "seed") is { } seed) data = data with { Seed = (int)Math.Round(seed) };
        if (ReadNumber(arguments, "trunkHeight") is { } trunkHeight) data = data with { TrunkHeight = (int)Math.Round(trunkHeight) };
        if (ReadNumber(arguments, "trunkRadius") is { } trunkRadius) data = data with { TrunkRadius = (float)trunkRadius };
        if (ReadNumber(arguments, "trunkLean") is { } trunkLean) data = data with { TrunkLean = (float)trunkLean };
        if (ReadNumber(arguments, "trunkBend") is { } trunkBend) data = data with { TrunkBend = (float)trunkBend };
        if (ReadNumber(arguments, "trunkTaper") is { } trunkTaper) data = data with { TrunkTaper = (float)trunkTaper };
        if (ReadNumber(arguments, "rootFlare") is { } rootFlare) data = data with { RootFlare = (float)rootFlare };
        if (ReadNumber(arguments, "branchCount") is { } branchCount) data = data with { BranchCount = (int)Math.Round(branchCount) };
        if (ReadNumber(arguments, "branchLength") is { } branchLength) data = data with { BranchLength = (float)branchLength };
        if (ReadNumber(arguments, "branchThickness") is { } branchThickness) data = data with { BranchThickness = (float)branchThickness };
        if (ReadNumber(arguments, "branchAngle") is { } branchAngle) data = data with { BranchAngle = (float)branchAngle };
        if (ReadNumber(arguments, "branchDroop") is { } branchDroop) data = data with { BranchDroop = (float)branchDroop };
        if (ReadNumber(arguments, "branchDepth") is { } branchDepth) data = data with { BranchDepth = (int)Math.Round(branchDepth) };
        if (ReadNumber(arguments, "crownStart") is { } crownStart) data = data with { CrownStart = (float)crownStart };
        if (ReadNumber(arguments, "crownRadius") is { } crownRadius) data = data with { CrownRadius = (float)crownRadius };
        if (ReadNumber(arguments, "crownHeight") is { } crownHeight) data = data with { CrownHeight = (float)crownHeight };
        if (ReadEnum(arguments, "crownShape", data.CrownShape, out TreeCrownShape crownShape)) data = data with { CrownShape = crownShape };
        if (ReadNumber(arguments, "leafDensity") is { } leafDensity) data = data with { LeafDensity = (float)leafDensity };
        if (ReadNumber(arguments, "leafSize") is { } leafSize) data = data with { LeafSize = (float)leafSize };
        if (ReadColor(arguments, "barkColor") is { } bark)
            data = data with { BarkR = ToByte(bark.X), BarkG = ToByte(bark.Y), BarkB = ToByte(bark.Z) };
        if (ReadColor(arguments, "leafColor") is { } leaf)
            data = data with { LeafR = ToByte(leaf.X), LeafG = ToByte(leaf.Y), LeafB = ToByte(leaf.Z) };
        if (ReadColor(arguments, "leaf2Color") is { } leaf2)
            data = data with { Leaf2R = ToByte(leaf2.X), Leaf2G = ToByte(leaf2.Y), Leaf2B = ToByte(leaf2.Z) };
        if (ReadNumber(arguments, "colorVariation") is { } variation) data = data with { ColorVariation = (float)variation };
        if (ReadBoolean(arguments, "emissiveLeaves") is { } emissiveLeaves) data = data with { EmissiveLeaves = emissiveLeaves };
        if (ReadNumber(arguments, "materialSlot") is { } materialSlot)
            data = data with { MaterialSlot = (ushort)Math.Clamp((int)materialSlot, 0, ushort.MaxValue) };
        if (ReadString(arguments, "sortingLayer") is { Length: > 0 } sortingLayer) data = data with { SortingLayer = sortingLayer };
        if (ReadNumber(arguments, "sortingOrder") is { } sortingOrder) data = data with { SortingOrder = (int)sortingOrder };
        return data;
    }
}
