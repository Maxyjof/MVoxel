using System.Diagnostics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 瓷砖铺设组件的检查器与视口盒体绘制
/// </summary>
/// <remarks>
/// 组件只保存参数与生成结果，不能进入绘制模式；要编辑体素必须先点“实例化”。
/// 参数改动后按固定延迟自动重算，避免拖动数值时每帧重建整面墙
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>参数改动后等待重算的毫秒数</summary>
    private const double TileRebuildDelayMs = 150.0;

    /// <summary>等待自动重算的对象与时间戳</summary>
    private readonly Dictionary<StableId, long> _tileRebuildPending = new();

    /// <summary>瓷砖源候选资产缓存，按项目路径与刷新标记失效</summary>
    private List<string> _tileSourceAssetCache = [];
    private string _tileSourceAssetCacheKey = string.Empty;

    /// <summary>
    /// 新建瓷砖组件时把盒体放到对象原点附近，方便马上调参数
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">新建的组件</param>
    private void InitializeTileLayerObject(SceneObject sceneObject, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadTileLayer(component);
        SceneComponentTypes.WriteTileLayer(component, data with { Origin = VoxelCoord64.Zero });
        _selectedSceneObjectId = sceneObject.Id;
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        SetStatus("已添加瓷砖组件，请选择体素源并设置盒体范围");
    }

    /// <summary>
    /// 瓷砖组件检查器
    /// </summary>
    /// <param name="selectedObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    private void DrawTileLayerComponent(SceneObject selectedObject, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadTileLayer(component);
        var original = data;
        var changed = false;

        ImGui.SeparatorText("体素源");
        DrawTileSourceRow(selectedObject, component, ref data, ref changed);
        if (ImGui.Button("从场景对象抓取…", new Vector2(-1F, 0F)))
            ImGui.OpenPopup("##TileCaptureSource");
        DrawTileCapturePopup(selectedObject, component, ref changed);
        var sourceOffsetX = (int)data.SourceOffset.X;
        var sourceOffsetY = (int)data.SourceOffset.Y;
        var sourceOffsetZ = (int)data.SourceOffset.Z;
        if (InspectorDragIntField("源起点 U", "从源的哪一格开始取瓷砖；用于在整块模型里挑一段图案。", ref sourceOffsetX, 0, 4095))
            data = data with { SourceOffset = data.SourceOffset with { X = sourceOffsetX } };
        if (InspectorDragIntField("源起点 V", null, ref sourceOffsetY, 0, 4095))
            data = data with { SourceOffset = data.SourceOffset with { Y = sourceOffsetY } };
        if (InspectorDragIntField("源起点 W", "沿厚度方向的起点，一般保持 0。", ref sourceOffsetZ, 0, 4095))
            data = data with { SourceOffset = data.SourceOffset with { Z = sourceOffsetZ } };

        ImGui.SeparatorText("盒体");
        if (ImGui.Button("适配墙对象…", new Vector2(-1F, 0F)))
            ImGui.OpenPopup("##TileFitWall");
        DrawTileFitWallPopup(selectedObject, ref data, ref changed);
        var originX = (int)data.Origin.X;
        var originY = (int)data.Origin.Y;
        var originZ = (int)data.Origin.Z;
        if (InspectorDragIntField("起点 X", "盒体最小角的 X 格坐标，相对对象原点。", ref originX, 1F, -100000, 100000))
            data = data with { Origin = data.Origin with { X = originX } };
        if (InspectorDragIntField("起点 Y", null, ref originY, 1F, -100000, 100000))
            data = data with { Origin = data.Origin with { Y = originY } };
        if (InspectorDragIntField("起点 Z", null, ref originZ, 1F, -100000, 100000))
            data = data with { Origin = data.Origin with { Z = originZ } };
        var sizeX = (int)data.Size.X;
        var sizeY = (int)data.Size.Y;
        var sizeZ = (int)data.Size.Z;
        if (InspectorDragIntField("尺寸 X", "盒体沿对象局部X轴的格数", ref sizeX, 1F, 1, 4096))
            data = data with { Size = data.Size with { X = sizeX } };
        if (InspectorDragIntField("尺寸 Y", "盒体沿对象局部Y轴的格数", ref sizeY, 1F, 1, 4096))
            data = data with { Size = data.Size with { Y = sizeY } };
        if (InspectorDragIntField("尺寸 Z", "盒体沿对象局部Z轴的格数", ref sizeZ, 1F, 1, 4096))
            data = data with { Size = data.Size with { Z = sizeZ } };

        ImGui.SeparatorText("朝向与贴合");
        var facingOptions = new[]
        {
            TileFacing.NegZ, TileFacing.PosZ, TileFacing.PosX,
            TileFacing.NegX, TileFacing.PosY, TileFacing.NegY,
        };
        var facingIndex = Math.Max(0, Array.IndexOf(facingOptions, data.Facing));
        if (ImGui.Combo("朝向", ref facingIndex, "朝北（-Z）\0朝南（+Z）\0朝东（+X）\0朝西（-X）\0朝上（+Y）\0朝下（-Y）\0"))
            data = data with { Facing = facingOptions[facingIndex] };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("瓷砖正面朝这个方向；组件从这一侧往里找墙，门窗列自动跳过");
        var fitIndex = (int)data.Fit;
        if (ImGui.Combo("贴合方式", ref fitIndex, "逐列贴合\0整块最前\0整块最后\0"))
            data = data with { Fit = (TileFitMode)fitIndex };
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("逐列贴合随凹凸起伏；整块贴平让瓷砖保持平整，凹处悬空或凸处嵌入");
        var tolerance = data.SurfaceTolerance;
        if (InspectorDragIntField("共面容差", "与这面墙的中位深度差超过该值的列视为门窗或背景，不铺。", ref tolerance, 0, 64))
            data = data with { SurfaceTolerance = tolerance };
        var maxDepth = data.MaxDepth;
        if (InspectorDragIntField("最大探测深度", "沿朝向往里最多找几格；超过仍没碰到墙就跳过这一列。", ref maxDepth, 1, 4096))
            data = data with { MaxDepth = maxDepth };
        var offset = data.Offset;
        if (InspectorDragIntField("外凸偏移", "整体沿朝向偏移的格数，负数为嵌进墙里。", ref offset, -16, 16))
            data = data with { Offset = offset };

        ImGui.SeparatorText("排布");
        var tileWidth = data.TileWidth;
        if (InspectorDragIntField("瓷砖宽（U）", "0 表示跟随体素源宽度；取值超出源范围时按源尺寸裁剪。", ref tileWidth, 0, 256))
            data = data with { TileWidth = tileWidth };
        var tileHeight = data.TileHeight;
        if (InspectorDragIntField("瓷砖高（V）", "0 表示跟随体素源高度。", ref tileHeight, 0, 256))
            data = data with { TileHeight = tileHeight };
        var tileDepth = data.TileDepth;
        if (InspectorDragIntField("瓷砖厚（W）", "沿朝向取源的前几层，默认 1 表示薄贴片；调大后瓷砖会更凸出。", ref tileDepth, 1, 256))
            data = data with { TileDepth = tileDepth };
        var gap = data.Gap;
        if (InspectorDragIntField("缝宽", "两块瓷砖之间留空的格数，0 表示无缝。", ref gap, 0, 32))
            data = data with { Gap = gap };
        var stagger = data.Stagger;
        if (ImGui.Checkbox("错缝（隔行错半块）", ref stagger)) data = data with { Stagger = stagger };
        var rotationIndex = (int)data.Rotation;
        if (ImGui.Combo("旋转", ref rotationIndex, "统一\0四向随机\0行列交替\0"))
            data = data with { Rotation = (TileRotationMode)rotationIndex };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("允许旋转时槽位按正方形排布，非正方形瓷砖会被居中放置");
        var variantCount = data.VariantCount;
        if (InspectorDragIntField("变体数", "把源沿 U 等分成几块当作不同瓷砖，随机挑选；必须整除。", ref variantCount, 1, 8))
            data = data with { VariantCount = variantCount };
        var seed = data.Seed;
        if (InspectorDragIntField("随机种子", "固定后重复生成的旋转与抖动完全一致。", ref seed, 0, 65535))
            data = data with { Seed = seed };
        var jitterU = data.JitterU;
        if (InspectorDragIntField("横向抖动", "每块瓷砖在 U 方向随机偏移的上限格数。", ref jitterU, 0, 8))
            data = data with { JitterU = jitterU };
        var jitterV = data.JitterV;
        if (InspectorDragIntField("纵向抖动", null, ref jitterV, 0, 8))
            data = data with { JitterV = jitterV };
        var edgeIndex = (int)data.Edge;
        if (ImGui.Combo("边界", ref edgeIndex, "裁剪\0跳过整块\0"))
            data = data with { Edge = (TileEdgeMode)edgeIndex };

        ImGui.SeparatorText("排序与生成");
        SceneComponentTypes.ReadTileSorting(component, out var sortingLayer, out var sortingOrder);
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.InputText("##tileSortingLayer", ref sortingLayer, 64)) changed = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("同一坐标按排序图层、排序顺序与层级顺序决定显示优先级");
        var order = sortingOrder;
        if (InspectorDragIntField("排序顺序", null, ref order, -100000, 100000))
        {
            sortingOrder = order;
            changed = true;
        }

        var autoRebuild = data.AutoRebuild;
        if (ImGui.Checkbox("参数改动后自动重算", ref autoRebuild)) data = data with { AutoRebuild = autoRebuild };
        var half = MathF.Max(80F, ImGui.GetContentRegionAvail().X * 0.5F - 4F);
        if (ImGui.Button("重新生成", new Vector2(half, 0F))) RebuildTileLayer(selectedObject, component, "已重新生成瓷砖");
        ImGui.SameLine();
        if (ImGui.Button("清空", new Vector2(-1F, 0F)))
        {
            SceneComponentTypes.RemoveGeneratedVoxels(component);
            _objectVoxels.Remove(selectedObject.Id);
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            SetStatus("已清空瓷砖生成结果，参数保留");
        }

        if (ImGui.Button("实例化", new Vector2(-1F, 0F)))
            InstantiateGeneratedVoxels(selectedObject, component, "瓷砖模型");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把当前生成结果变成可绘制的体素模型；之后不能再改参数，按 Ctrl+Z 可退回组件形态");

        var stats = SceneComponentTypes.LastTileStats;
        if (stats.CellCount > 0 || stats.Message.Length > 0)
        {
            ImGui.Separator();
            if (stats.CellCount > 0)
                ImGui.TextDisabled($"{stats.TileCount:N0} 块 · {stats.CellCount:N0} 格 · 墙面 {stats.ColumnCount:N0} 列 · 跳过 {stats.SkippedColumns:N0} 列");
            if (stats.Truncated) ImGui.TextColored(new Vector4(1F, .7F, .3F, 1F), "结果已按预算截断");
            if (stats.Message.Length > 0) ImGui.TextColored(new Vector4(1F, .55F, .45F, 1F), stats.Message);
        }

        changed |= data != original;
        if (changed)
        {
            SceneComponentTypes.WriteTileLayer(component, data);
            SceneComponentTypes.WriteTileSorting(component, sortingLayer, sortingOrder);
            MarkSceneDocumentDirty();
            if (data.AutoRebuild) _tileRebuildPending[selectedObject.Id] = Stopwatch.GetTimestamp();
            else SetStatus("参数已改，点“重新生成”才会生效");
        }

        PumpTileLayerRebuild(selectedObject, component);
    }

    /// <summary>
    /// 延迟自动重算：拖动数值时等手停下来再算，避免每帧重建整面墙
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">瓷砖组件</param>
    private void PumpTileLayerRebuild(SceneObject sceneObject, ComponentRecord component)
    {
        if (!_tileRebuildPending.TryGetValue(sceneObject.Id, out var startedAt)) return;
        if (ImGui.IsAnyItemActive()) return;
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (elapsed < TileRebuildDelayMs) return;
        _tileRebuildPending.Remove(sceneObject.Id);
        if (_sceneDocument?.Objects.Any(item => item.Id == sceneObject.Id) != true) return;
        if (!sceneObject.Components.Contains(component)) return;
        RebuildTileLayer(sceneObject, component, "已按新参数重新生成瓷砖");
    }

    /// <summary>
    /// 重建瓷砖并报告铺设统计
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">瓷砖组件</param>
    /// <param name="action">状态栏动作名</param>
    private void RebuildTileLayer(SceneObject sceneObject, ComponentRecord component, string action)
    {
        RebuildGeneratedVoxels(sceneObject, component, action);
        var stats = SceneComponentTypes.LastTileStats;
        if (stats.Message.Length > 0)
            SetStatus($"{action} · {stats.Message}");
        else if (stats.CellCount > 0)
            SetStatus($"{action} · {stats.TileCount:N0} 块 · {stats.CellCount:N0} 格" +
                      (stats.SkippedColumns > 0 ? $" · 跳过 {stats.SkippedColumns:N0} 列" : string.Empty) +
                      (stats.Truncated ? "（已截断）" : string.Empty));
    }

    /// <summary>
    /// 体素源选择行：种类、资产下拉与内嵌快照说明
    /// </summary>
    private void DrawTileSourceRow(SceneObject selectedObject, ComponentRecord component,
        ref TileLayerComponentData data, ref bool changed)
    {
        var label = data.Source.Kind switch
        {
            VoxelSourceKind.Embedded => "内嵌快照",
            VoxelSourceKind.ModelAsset => $"模型资产 · {data.Source.DisplayName}",
            VoxelSourceKind.PrefabAsset => $"预制体 · {data.Source.DisplayName}",
            _ => "未指定",
        };
        ImGui.SetNextItemWidth(-1F);
        if (!ImGui.BeginCombo("##TileSource", label)) return;
        if (ImGui.Selectable("内嵌快照", data.Source.Kind == VoxelSourceKind.Embedded))
            SetTileSourceKind(ref data, VoxelSourceKind.Embedded, string.Empty, ref changed);
        var assets = GetTileSourceAssets();
        if (assets.Count > 0) ImGui.Separator();
        foreach (var relativePath in assets)
        {
            var kind = relativePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? VoxelSourceKind.PrefabAsset : VoxelSourceKind.ModelAsset;
            if (!ImGui.Selectable(relativePath, data.Source.Kind == kind &&
                                              string.Equals(data.Source.AssetPath, relativePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            SetTileSourceKind(ref data, kind, relativePath, ref changed);
        }

        ImGui.EndCombo();
    }

    private static void SetTileSourceKind(ref TileLayerComponentData data, VoxelSourceKind kind, string path,
        ref bool changed)
    {
        data = data with { Source = new VoxelSourceRef(kind, path) };
        changed = true;
    }

    /// <summary>
    /// 列出项目里可当瓷砖源的模型与预制体资产，按 Assets 目录缓存
    /// </summary>
    private List<string> GetTileSourceAssets()
    {
        if (_project is null) return [];
        var key = _project.AssetsPath;
        if (_tileSourceAssetCacheKey == key) return _tileSourceAssetCache;
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(key, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".mvmodel", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add("Assets/" + Path.GetRelativePath(key, file).Replace('\\', '/'));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 资产目录正被外部写入时保持上一次列表，不影响界面
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        _tileSourceAssetCache = result;
        _tileSourceAssetCacheKey = key;
        return result;
    }

    /// <summary>
    /// 从场景里选一个模型对象抓取瓷砖源，内嵌进组件
    /// </summary>
    private void DrawTileCapturePopup(SceneObject selectedObject, ComponentRecord component, ref bool changed)
    {
        if (!ImGui.BeginPopup("##TileCaptureSource")) return;
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("请先打开项目");
            ImGui.EndPopup();
            return;
        }

        var candidates = _sceneDocument.Objects
            .Where(item => item.Id != selectedObject.Id &&
                           item.Components.Any(componentItem =>
                               componentItem.Enabled && SceneComponentTypes.IsVoxelModel(componentItem)))
            .ToArray();
        if (candidates.Length == 0) ImGui.TextDisabled("场景里没有可抓取的模型对象");
        foreach (var candidate in candidates)
        {
            if (!ImGui.Selectable(candidate.Name)) continue;
            if (!_objectVoxels.TryGetWorld(candidate.Id, out var world) || world.OccupiedVoxelCount == 0)
            {
                SetStatus("该对象没有体素可抓取");
                break;
            }

            TileSourcePayload.Write(component, world);
            var data = SceneComponentTypes.ReadTileLayer(component) with
            {
                Source = new VoxelSourceRef(VoxelSourceKind.Embedded, string.Empty),
            };
            SceneComponentTypes.WriteTileLayer(component, data);
            changed = true;
            _tileRebuildPending[selectedObject.Id] = Stopwatch.GetTimestamp();
            SetStatus($"已抓取瓷砖源：{candidate.Name} · {world.OccupiedVoxelCount:N0} 格");
            break;
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// 把另一个模型对象的包围盒填进盒体，省去手填坐标
    /// </summary>
    private void DrawTileFitWallPopup(SceneObject selectedObject, ref TileLayerComponentData data, ref bool changed)
    {
        if (!ImGui.BeginPopup("##TileFitWall")) return;
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("请先打开项目");
            ImGui.EndPopup();
            return;
        }

        var candidates = _sceneDocument.Objects
            .Where(item => item.Id != selectedObject.Id &&
                           item.Components.Any(component => component.Enabled && SceneComponentTypes.IsVoxelModel(component)))
            .ToArray();
        if (candidates.Length == 0) ImGui.TextDisabled("场景里没有模型对象");
        foreach (var candidate in candidates)
        {
            if (!ImGui.Selectable(candidate.Name)) continue;
            if (!_objectVoxels.TryGetWorld(candidate.Id, out var world) ||
                !world.TryGetBounds(out var minimum, out var maximum))
            {
                SetStatus("该对象没有体素包围盒");
                break;
            }

            var tileComponent = selectedObject.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer);
            var (rotation, scale) = tileComponent is null
                ? (System.Numerics.Vector3.Zero, System.Numerics.Vector3.One)
                : ReadGeneratedTransform(tileComponent);
            var orientation = GetTileObjectOrientation(selectedObject, rotation);
            if (scale.X <= 0F || scale.Y <= 0F || scale.Z <= 0F)
            {
                SetStatus("瓷砖对象缩放必须大于零，无法适配墙体");
                break;
            }
            var inverse = System.Numerics.Quaternion.Conjugate(orientation);
            var objectOrigin = GetObjectGridOrigin(selectedObject);
            var localMinimum = new System.Numerics.Vector3(float.MaxValue);
            var localMaximum = new System.Numerics.Vector3(float.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var worldCorner = new Vector3(
                    (float)((corner & 1) == 0 ? minimum.X : maximum.X + 1),
                    (float)((corner & 2) == 0 ? minimum.Y : maximum.Y + 1),
                    (float)((corner & 4) == 0 ? minimum.Z : maximum.Z + 1));
                var relative = new System.Numerics.Vector3(
                    worldCorner.X - (float)objectOrigin.X, worldCorner.Y - (float)objectOrigin.Y,
                    worldCorner.Z - (float)objectOrigin.Z);
                var local = System.Numerics.Vector3.Transform(relative, inverse) / scale;
                localMinimum = System.Numerics.Vector3.Min(localMinimum, local);
                localMaximum = System.Numerics.Vector3.Max(localMaximum, local);
            }
            var localMin = new VoxelCoord64((long)MathF.Floor(localMinimum.X) - 1,
                (long)MathF.Floor(localMinimum.Y) - 1, (long)MathF.Floor(localMinimum.Z) - 1);
            var localMax = new VoxelCoord64((long)MathF.Ceiling(localMaximum.X),
                (long)MathF.Ceiling(localMaximum.Y), (long)MathF.Ceiling(localMaximum.Z));
            data = data with { Origin = localMin, Size = localMax - localMin + new VoxelCoord64(1, 1, 1) };
            changed = true;
            SetStatus($"盒体已适配 {candidate.Name} 的包围盒");
            break;
        }

        ImGui.EndPopup();
    }

    private static System.Numerics.Quaternion GetTileObjectOrientation(SceneObject sceneObject,
        System.Numerics.Vector3 componentRotation)
    {
        var grid = sceneObject.Transform.Mode == TransformMode.Grid
            ? sceneObject.Transform.Grid.Rotation : OrthogonalRotation.Identity;
        var matrix = new System.Numerics.Matrix4x4(
            grid.M00, grid.M01, grid.M02, 0F, grid.M10, grid.M11, grid.M12, 0F,
            grid.M20, grid.M21, grid.M22, 0F, 0F, 0F, 0F, 1F);
        return System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.CreateFromRotationMatrix(matrix) * RotationFromDegrees(componentRotation));
    }

    /// <summary>
    /// 在场景视口里画出当前选中对象的瓷砖盒体与朝向箭头
    /// </summary>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口左上角</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    private void DrawTileLayerGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_sceneDocument is null || GetSelectedSceneObject() is not { } selected) return;
        var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsTileLayer);
        if (component is null) return;
        var data = SceneComponentTypes.ReadTileLayer(component);
        var baseOrigin = GetObjectGridOrigin(selected);
        var (rotation, scale) = ReadGeneratedTransform(component);
        var orientation = GetTileObjectOrientation(selected, rotation);
        Vector3 TransformLocal(System.Numerics.Vector3 local)
        {
            var rotated = System.Numerics.Vector3.Transform(local * scale, orientation);
            return new Vector3((float)baseOrigin.X + rotated.X, (float)baseOrigin.Y + rotated.Y,
                (float)baseOrigin.Z + rotated.Z);
        }
        Span<Vector2> boxPoints = stackalloc Vector2[8];
        for (var corner = 0; corner < 8; corner++)
        {
            var local = new System.Numerics.Vector3(
                data.Origin.X + ((corner & 1) == 0 ? 0 : data.Size.X),
                data.Origin.Y + ((corner & 2) == 0 ? 0 : data.Size.Y),
                data.Origin.Z + ((corner & 4) == 0 ? 0 : data.Size.Z));
            if (!SceneProjection.Project(TransformLocal(local), origin, size, camera, out boxPoints[corner], out _)) return;
        }
        var color = ToPackedColor(Color.FromArgb(210, 90, 200, 255));
        ReadOnlySpan<(int Start, int End)> boxEdges = stackalloc (int, int)[]
        {
            (0, 1), (1, 3), (3, 2), (2, 0), (4, 5), (5, 7), (7, 6), (6, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (startIndex, endIndex) in boxEdges)
            drawList.AddLine(boxPoints[startIndex], boxPoints[endIndex], color, 1.4F);
        var localCenter = new System.Numerics.Vector3(
            data.Origin.X + data.Size.X * .5F, data.Origin.Y + data.Size.Y * .5F,
            data.Origin.Z + data.Size.Z * .5F);
        var center = TransformLocal(localCenter);
        var direction = data.Facing switch
        {
            TileFacing.PosX => new Vector3(1F, 0F, 0F),
            TileFacing.NegX => new Vector3(-1F, 0F, 0F),
            TileFacing.PosY => new Vector3(0F, 1F, 0F),
            TileFacing.NegY => new Vector3(0F, -1F, 0F),
            TileFacing.PosZ => new Vector3(0F, 0F, 1F),
            _ => new Vector3(0F, 0F, -1F),
        };
        var normal = System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(direction.X, direction.Y, direction.Z), orientation);
        var worldDirection = new Vector3(normal.X, normal.Y, normal.Z);
        var tip = center + worldDirection * MathF.Max(4F, (float)Math.Max(data.Size.X, data.Size.Y) * 0.5F);
        if (!SceneProjection.Project(center, origin, size, camera, out var start, out _) ||
            !SceneProjection.Project(tip, origin, size, camera, out var end, out _)) return;
        var arrowColor = ToPackedColor(Color.FromArgb(235, 255, 210, 90));
        drawList.AddLine(start, end, arrowColor, 2.2F);
        drawList.AddCircleFilled(end, 4F, arrowColor);
    }

    /// <summary>
    /// 画一个世界坐标包围盒的十二条边
    /// </summary>
    private static void DrawWorldBox(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        VoxelCoord64 minimum, VoxelCoord64 maximum, uint color, float thickness)
    {
        Span<Vector2> points = stackalloc Vector2[8];
        for (var corner = 0; corner < 8; corner++)
        {
            var world = new Vector3(
                (corner & 1) == 0 ? minimum.X : maximum.X + 1,
                (corner & 2) == 0 ? minimum.Y : maximum.Y + 1,
                (corner & 4) == 0 ? minimum.Z : maximum.Z + 1);
            if (!SceneProjection.Project(world, origin, size, camera, out var screen, out _)) return;
            points[corner] = screen;
        }

        ReadOnlySpan<(int Start, int End)> edges = stackalloc (int, int)[]
        {
            (0, 1), (1, 3), (3, 2), (2, 0),
            (4, 5), (5, 7), (7, 6), (6, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var (start, end) in edges) drawList.AddLine(points[start], points[end], color, thickness);
    }
}
