using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 瓷砖铺设组件的自动化命令：创建、读写、生成、实例化与抓取体素源
/// </summary>
/// <remarks>
/// 与界面共用同一套参数解析、生成与重建流程，脚本与 MCP 不需要模拟鼠标。
/// 生成依赖场景里其他模型对象的真实几何，接口会返回墙面列数与跳过列数，便于外部判断贴合是否成立
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 新建带瓷砖组件的对象并生成一次
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>对象与组件摘要</returns>
    private object CreateTileObject(JsonElement arguments) => RunOnUi(() =>
    {
        var scene = RequireScene();
        var item = CreateGeneratedObject(scene, arguments, "瓷砖铺设");
        var data = ApplyTileFields(TileLayerComponentData.Default, arguments);
        var component = SceneComponentTypes.CreateTileLayer(data);
        item.Components.Add(component);
        if (ReadString(arguments, "sourceObjectId") is { Length: > 0 } sourceText)
            CaptureTileSourceInto(item, component, sourceText);
        if (ReadString(arguments, "fitObjectId") is { Length: > 0 } wallText)
            FitTileBoxToObject(item, component, wallText);
        if (ReadBoolean(arguments, "generate") ?? true)
            RebuildGeneratedVoxels(item, component, string.Empty);
        MarkSceneDocumentDirty();
        return BuildTileResult(item, component);
    });

    /// <summary>
    /// 读取瓷砖组件参数与生成状态
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>组件摘要</returns>
    private object DescribeTileLayer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer)
                        ?? throw new InvalidOperationException("对象上没有瓷砖铺设组件");
        return BuildTileResult(target, component);
    });

    /// <summary>
    /// 写入瓷砖参数，默认立即重新生成
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>写入后的组件摘要</returns>
    private object SetTileLayer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer)
                        ?? throw new InvalidOperationException("对象上没有瓷砖铺设组件");
        var data = ApplyTileFields(SceneComponentTypes.ReadTileLayer(component), arguments);
        SceneComponentTypes.WriteTileLayer(component, data);
        if (ReadString(arguments, "sourceObjectId") is { Length: > 0 } sourceText)
            CaptureTileSourceInto(target, component, sourceText);
        if (ReadString(arguments, "fitObjectId") is { Length: > 0 } wallText)
            FitTileBoxToObject(target, component, wallText);
        if (ReadBoolean(arguments, "generate") ?? true)
            RebuildGeneratedVoxels(target, component, string.Empty);
        else
        {
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
        }

        return BuildTileResult(target, component);
    });

    /// <summary>
    /// 按当前参数重新生成瓷砖
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>生成后的组件摘要</returns>
    private object GenerateTileLayer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer)
                        ?? throw new InvalidOperationException("对象上没有瓷砖铺设组件");
        RebuildGeneratedVoxels(target, component, string.Empty);
        return BuildTileResult(target, component);
    });

    /// <summary>
    /// 把瓷砖组件替换成可绘制的体素模型
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>实例化后的对象状态</returns>
    private object InstantiateTileLayer(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer)
                        ?? throw new InvalidOperationException("对象上没有瓷砖铺设组件");
        InstantiateGeneratedVoxels(target, component, "瓷砖模型");
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            components = target.Components.Select(item => item.TypeId).ToArray(),
            voxelCount = _objectVoxels.TryGetWorld(target.Id, out var world) ? world.OccupiedVoxelCount : 0,
        };
    });

    /// <summary>
    /// 把场景中另一个模型对象的体素抓成内嵌瓷砖源
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>抓取结果与新的组件摘要</returns>
    private object CaptureTileSource(JsonElement arguments) => RunOnUi(() =>
    {
        var target = RequireObject(arguments);
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer)
                        ?? throw new InvalidOperationException("对象上没有瓷砖铺设组件");
        var sourceText = ReadString(arguments, "sourceId")
                         ?? throw new ArgumentException("缺少 sourceId：提供体素的模型对象标识或名称");
        var captured = CaptureTileSourceInto(target, component, sourceText);
        RebuildGeneratedVoxels(target, component, string.Empty);
        return new
        {
            sourceObjectId = captured.Id,
            sourceObjectName = captured.Name,
            capturedCells = captured.Cells,
            tile = BuildTileResult(target, component),
        };
    });

    /// <summary>
    /// 把指定对象的体素写成组件的内嵌瓷砖源并把来源切换过去
    /// </summary>
    /// <param name="target">瓷砖对象</param>
    /// <param name="component">瓷砖组件</param>
    /// <param name="sourceText">提供体素的对象标识或名称</param>
    /// <returns>抓取来源的对象信息与格数</returns>
    private (string Id, string Name, int Cells) CaptureTileSourceInto(SceneObject target, ComponentRecord component,
        string sourceText)
    {
        var scene = RequireScene();
        var sourceObject = FindSceneObject(scene, sourceText);
        if (!_objectVoxels.TryGetWorld(sourceObject.Id, out var world) || world.OccupiedVoxelCount == 0)
            throw new InvalidOperationException($"对象“{sourceObject.Name}”没有体素可抓取");
        TileSourcePayload.Write(component, world);
        var data = SceneComponentTypes.ReadTileLayer(component) with
        {
            Source = new VoxelSourceRef(VoxelSourceKind.Embedded, string.Empty),
        };
        SceneComponentTypes.WriteTileLayer(component, data);
        SetStatus($"已抓取瓷砖源：{sourceObject.Name} · {world.OccupiedVoxelCount:N0} 格");
        return (sourceObject.Id.ToString(), sourceObject.Name, world.OccupiedVoxelCount);
    }

    /// <summary>
    /// 把另一个模型对象的包围盒四周外扩一格后写进瓷砖盒体
    /// </summary>
    /// <param name="tileObject">瓷砖对象</param>
    /// <param name="component">瓷砖组件</param>
    /// <param name="wallText">提供墙面的对象标识或名称</param>
    private void FitTileBoxToObject(SceneObject tileObject, ComponentRecord component, string wallText)
    {
        var scene = RequireScene();
        var wall = FindSceneObject(scene, wallText);
        if (!_objectVoxels.TryGetWorld(wall.Id, out var world) || !world.TryGetBounds(out var minimum, out var maximum))
            throw new InvalidOperationException($"对象“{wall.Name}”没有体素包围盒");
        var (_, scale) = ReadGeneratedTransform(component);
        var orientation = GetTileObjectOrientation(tileObject, ReadGeneratedTransform(component).Rotation);
        var inverse = System.Numerics.Quaternion.Conjugate(orientation);
        var objectOrigin = GetObjectGridOrigin(tileObject);
        var localMinimum = new System.Numerics.Vector3(float.MaxValue);
        var localMaximum = new System.Numerics.Vector3(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var worldCorner = new System.Numerics.Vector3(
                (float)((corner & 1) == 0 ? minimum.X : maximum.X + 1),
                (float)((corner & 2) == 0 ? minimum.Y : maximum.Y + 1),
                (float)((corner & 4) == 0 ? minimum.Z : maximum.Z + 1));
            var relative = worldCorner - new System.Numerics.Vector3((float)objectOrigin.X,
                (float)objectOrigin.Y, (float)objectOrigin.Z);
            var local = System.Numerics.Vector3.Transform(relative, inverse) / scale;
            localMinimum = System.Numerics.Vector3.Min(localMinimum, local);
            localMaximum = System.Numerics.Vector3.Max(localMaximum, local);
        }
        var localMin = new VoxelCoord64((long)MathF.Floor(localMinimum.X) - 1,
            (long)MathF.Floor(localMinimum.Y) - 1, (long)MathF.Floor(localMinimum.Z) - 1);
        var localMax = new VoxelCoord64((long)MathF.Ceiling(localMaximum.X),
            (long)MathF.Ceiling(localMaximum.Y), (long)MathF.Ceiling(localMaximum.Z));
        var data = SceneComponentTypes.ReadTileLayer(component) with
        {
            Origin = localMin,
            Size = localMax - localMin + new VoxelCoord64(1, 1, 1),
        };
        SceneComponentTypes.WriteTileLayer(component, data);
    }

    /// <summary>
    /// 按标识或名称找场景对象
    /// </summary>
    private static SceneObject FindSceneObject(SceneDocument scene, string text) =>
        scene.Objects.FirstOrDefault(item =>
            string.Equals(item.Id.ToString(), text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, text, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"找不到对象：{text}");

    /// <summary>
    /// 把命令参数合并进瓷砖数据，缺省字段保持不变
    /// </summary>
    /// <param name="data">原数据</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>合并后的数据</returns>
    private static TileLayerComponentData ApplyTileFields(TileLayerComponentData data, JsonElement arguments)
    {
        if (ReadVector(arguments, "origin") is { Length: 3 } origin)
            data = data with
            {
                Origin = new VoxelCoord64((long)Math.Round(origin[0]), (long)Math.Round(origin[1]),
                    (long)Math.Round(origin[2])),
            };
        if (ReadVector(arguments, "size") is { Length: 3 } size)
            data = data with
            {
                Size = new VoxelCoord64((long)Math.Round(size[0]), (long)Math.Round(size[1]),
                    (long)Math.Round(size[2])),
            };
        if (ReadEnum(arguments, "facing", data.Facing, out TileFacing facing)) data = data with { Facing = facing };
        if (ReadVector(arguments, "sourceOffset") is { Length: 3 } sourceOffset)
            data = data with
            {
                SourceOffset = new VoxelCoord64((long)Math.Round(sourceOffset[0]),
                    (long)Math.Round(sourceOffset[1]), (long)Math.Round(sourceOffset[2])),
            };
        var kind = ReadString(arguments, "sourceKind");
        var path = ReadString(arguments, "sourcePath");
        if (kind is { Length: > 0 })
        {
            if (!Enum.TryParse<VoxelSourceKind>(kind, ignoreCase: true, out var parsedKind))
                throw new ArgumentException($"不支持的体素源种类：{kind}");
            data = data with { Source = new VoxelSourceRef(parsedKind, path ?? data.Source.AssetPath) };
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            data = data with { Source = new VoxelSourceRef(VoxelSourceKind.ModelAsset, path) };
        }

        if (ReadNumber(arguments, "tileWidth") is { } tileWidth) data = data with { TileWidth = (int)Math.Round(tileWidth) };
        if (ReadNumber(arguments, "tileHeight") is { } tileHeight) data = data with { TileHeight = (int)Math.Round(tileHeight) };
        if (ReadNumber(arguments, "tileDepth") is { } tileDepth) data = data with { TileDepth = (int)Math.Round(tileDepth) };
        if (ReadNumber(arguments, "gap") is { } gap) data = data with { Gap = (int)Math.Round(gap) };
        if (ReadBoolean(arguments, "stagger") is { } stagger) data = data with { Stagger = stagger };
        if (ReadEnum(arguments, "rotation", data.Rotation, out TileRotationMode rotation))
            data = data with { Rotation = rotation };
        if (ReadNumber(arguments, "variantCount") is { } variantCount)
            data = data with { VariantCount = (int)Math.Round(variantCount) };
        if (ReadNumber(arguments, "seed") is { } seed) data = data with { Seed = (int)Math.Round(seed) };
        if (ReadNumber(arguments, "jitterU") is { } jitterU) data = data with { JitterU = (int)Math.Round(jitterU) };
        if (ReadNumber(arguments, "jitterV") is { } jitterV) data = data with { JitterV = (int)Math.Round(jitterV) };
        if (ReadEnum(arguments, "fit", data.Fit, out TileFitMode fit)) data = data with { Fit = fit };
        if (ReadNumber(arguments, "surfaceTolerance") is { } tolerance)
            data = data with { SurfaceTolerance = (int)Math.Round(tolerance) };
        if (ReadNumber(arguments, "maxDepth") is { } maxDepth) data = data with { MaxDepth = (int)Math.Round(maxDepth) };
        if (ReadNumber(arguments, "offset") is { } offset) data = data with { Offset = (int)Math.Round(offset) };
        if (ReadEnum(arguments, "edge", data.Edge, out TileEdgeMode edge)) data = data with { Edge = edge };
        if (ReadBoolean(arguments, "autoRebuild") is { } autoRebuild) data = data with { AutoRebuild = autoRebuild };
        return data.Normalize();
    }

    /// <summary>
    /// 拼出瓷砖命令的返回结构
    /// </summary>
    /// <param name="target">场景对象</param>
    /// <param name="component">瓷砖组件</param>
    /// <returns>参数、体素统计与最近一次铺设结果</returns>
    private object BuildTileResult(SceneObject target, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadTileLayer(component);
        var stats = SceneComponentTypes.LastTileStats;
        var hasSignature = SceneComponentTypes.TryReadGeneratedSignature(component, out var stored);
        return new
        {
            objectId = target.Id.ToString(),
            name = target.Name,
            typeId = SceneComponentTypes.TileLayer,
            origin = new[] { data.Origin.X, data.Origin.Y, data.Origin.Z },
            size = new[] { data.Size.X, data.Size.Y, data.Size.Z },
            facing = data.Facing.ToString(),
            sourceKind = data.Source.Kind.ToString(),
            sourcePath = data.Source.AssetPath,
            sourceOffset = new[] { data.SourceOffset.X, data.SourceOffset.Y, data.SourceOffset.Z },
            tileWidth = data.TileWidth,
            tileHeight = data.TileHeight,
            tileDepth = data.TileDepth,
            gap = data.Gap,
            stagger = data.Stagger,
            rotation = data.Rotation.ToString(),
            variantCount = data.VariantCount,
            seed = data.Seed,
            jitterU = data.JitterU,
            jitterV = data.JitterV,
            fit = data.Fit.ToString(),
            surfaceTolerance = data.SurfaceTolerance,
            maxDepth = data.MaxDepth,
            offset = data.Offset,
            edge = data.Edge.ToString(),
            autoRebuild = data.AutoRebuild,
            generation = new
            {
                voxelCount = _objectVoxels.TryGetWorld(target.Id, out var world) ? world.OccupiedVoxelCount : 0,
                payloadBytes = SceneComponentTypes.GetGeneratedVoxelPayloadLength(component) * 3 / 4,
                truncated = _generatedVoxelTruncated.TryGetValue(target.Id, out var truncated) && truncated,
                pending = !hasSignature || stored != SceneComponentTypes.ComputeGeneratedSignature(component),
                tiles = stats.TileCount,
                cells = stats.CellCount,
                columns = stats.ColumnCount,
                skippedColumns = stats.SkippedColumns,
                message = stats.Message,
            },
        };
    }
}
