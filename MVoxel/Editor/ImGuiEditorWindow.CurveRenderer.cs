using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 曲线渲染器组件：检查器参数、控制点列表、体素重建与对象联动
/// </summary>
/// <remarks>
/// 组件里能看见的只有曲线本身，生成的体素以隐藏负载写进同一份组件数据，
/// 既不进入绘制模式，也不会在检查器里出现可视件。所有几何修改都会重建体素，
/// 重建只替换本对象的世界，再走统一的合成流程，与其他对象的体素互不影响
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>检查器当前展开的控制点所属对象</summary>
    private StableId _curvePointObjectId = StableId.Empty;

    /// <summary>检查器与场景手柄共用的当前控制点下标，-1 表示未选中</summary>
    private int _curvePointIndex = -1;

    /// <summary>检查器编辑时收集的改动，提交前不写回组件</summary>
    private sealed class CurveEditState
    {
        public CurveRendererComponentData Data;
        public List<CurvePointData> Points = [];
        public bool GeometryChanged;
        public bool SortingChanged;
    }

    /// <summary>
    /// 取对象上启用的曲线渲染器组件
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>组件记录，没有时返回 null</returns>
    private static ComponentRecord? GetCurveRendererComponent(SceneObject? sceneObject) =>
        sceneObject?.Components.FirstOrDefault(item => SceneComponentTypes.IsCurveRenderer(item) && item.Enabled);

    /// <summary>
    /// 判断对象是否正在曲线编辑模式
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>是否处于编辑模式</returns>
    private bool IsCurveEditing(SceneObject? sceneObject)
    {
        if (sceneObject is null || _selectedSceneObjectId != sceneObject.Id) return false;
        var component = GetCurveRendererComponent(sceneObject);
        return component is not null && SceneComponentTypes.ReadCurveRenderer(component).Editing;
    }

    /// <summary>
    /// 添加组件时初始化曲线对象：切到格变换、选中对象并立即生成体素
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">新建的曲线组件</param>
    private void InitializeCurveRendererObject(SceneObject sceneObject, ComponentRecord component)
    {
        if (sceneObject.Transform.Mode == TransformMode.Free)
        {
            sceneObject.Transform.SetGrid(sceneObject.Transform.ToGrid());
            UpdateTransformMarker(sceneObject);
        }

        _selectedSceneObjectId = sceneObject.Id;
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        _sceneGizmoMode = SceneGizmoMode.Select;
        _curvePointObjectId = sceneObject.Id;
        _curvePointIndex = 0;
        MarkComponentEditingOwner(sceneObject.Id);
        RebuildGeneratedVoxels(sceneObject, component, "新建曲线");
        SetStatus("已添加曲线渲染器：拖动控制点、切线手柄，或直接修改检查器参数来调整曲线");
    }

    /// <summary>
    /// 检查器中的曲线渲染器内容
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    private void DrawCurveRendererComponent(SceneObject sceneObject, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (_curvePointObjectId != sceneObject.Id)
        {
            _curvePointObjectId = sceneObject.Id;
            _curvePointIndex = data.Points.Length > 0 ? 0 : -1;
        }

        _curvePointIndex = data.Points.Length == 0 ? -1 : Math.Clamp(_curvePointIndex, 0, data.Points.Length - 1);
        var state = new CurveEditState { Data = data, Points = data.Points.ToList() };
        DrawCurveEditingToggle(sceneObject, component, state);
        DrawCurveShapeSection(state);
        DrawCurvePointSection(state);
        DrawCurveGenerationSection(sceneObject, component, state);
        CommitCurveEditor(sceneObject, component, state);
    }

    /// <summary>
    /// 进入或退出曲线编辑模式
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    /// <param name="state">编辑缓冲</param>
    private void DrawCurveEditingToggle(SceneObject sceneObject, ComponentRecord component, CurveEditState state)
    {
        var editing = state.Data.Editing && _selectedSceneObjectId == sceneObject.Id;
        if (ImGui.Button(editing ? "退出曲线编辑" : "进入曲线编辑", new Vector2(-1F, 0F)))
        {
            state.Data = state.Data with { Editing = !editing };
            SceneComponentTypes.WriteCurveRenderer(component, state.Data);
            MarkSceneDocumentDirty();
            if (!editing)
            {
                _selectedSceneObjectId = sceneObject.Id;
                _activeTool = EditorTool.Pick;
                _voxelEditTargetId = null;
                _sceneGizmoMode = SceneGizmoMode.Select;
                _curvePointObjectId = sceneObject.Id;
                MarkComponentEditingOwner(sceneObject.Id);
                if (_curvePointIndex < 0 && state.Points.Count > 0) _curvePointIndex = 0;
            }

            SetStatus(editing ? "已退出曲线编辑" : "已进入曲线编辑：拖动控制点改位置，拖动前后切线改角度，拖动朝向手柄改截面旋转");
        }

        // 说明文字按可见宽度分行，避免把检查器撑出横向滚动条
        DrawInspectorHint("生成的体素保存在场景文件中，不进入绘制模式，只能通过编辑曲线间接修改。");
    }

    /// <summary>
    /// 曲线整体的闭合、插值与切线工具
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    private void DrawCurveShapeSection(CurveEditState state)
    {
        BeginInspectorField("闭合曲线", "首尾相连形成环形曲线，闭合后所有控制点都有前后两段相邻曲线。");
        var closed = state.Data.Closed;
        if (ImGui.Checkbox("##CurveClosed", ref closed))
        {
            state.Data = state.Data with { Closed = closed };
            CurveSpline.RecomputeAutoTangents(state.Points, closed);
            state.GeometryChanged = true;
        }

        BeginInspectorField("插值方式", "贝塞尔用每点的前后切线控制弯曲程度；直线只连接控制点，切线不参与生成。");
        var interpolation = (int)state.Data.Interpolation;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##CurveInterpolation", ref interpolation, "贝塞尔曲线\0直线\0"))
        {
            state.Data = state.Data with { Interpolation = (CurveInterpolation)interpolation };
            state.GeometryChanged = true;
        }

        var halfWidth = MathF.Max(ImGui.GetFontSize() * 4F,
            MathF.Floor((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * .5F) - 1F);
        // 直线插值不参与切线计算，切线工具与切线参数都没有意义，直接不显示
        var linear = state.Data.Interpolation == CurveInterpolation.Linear;
        if (!linear)
        {
            if (ImGui.Button("自动平滑切线", new Vector2(halfWidth, 0F)))
            {
                CurveSpline.RecomputeAutoTangents(state.Points, state.Data.Closed);
                state.GeometryChanged = true;
                SetStatus("已按相邻控制点重算自动切线，手动调整过的点不会被覆盖");
            }

            ImGui.SameLine();
        }

        if (ImGui.Button("反转方向", new Vector2(linear ? -1F : halfWidth, 0F)))
        {
            state.Points.Reverse();
            for (var index = 0; index < state.Points.Count; index++)
                state.Points[index] = state.Points[index] with
                {
                    TangentIn = state.Points[index].TangentOut,
                    TangentOut = state.Points[index].TangentIn,
                };
            state.GeometryChanged = true;
            SetStatus("已反转曲线方向");
        }
    }

    /// <summary>
    /// 控制点列表与选中点的详细参数
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    private void DrawCurvePointSection(CurveEditState state)
    {
        ImGui.Separator();
        ImGui.TextDisabled($"控制点  {state.Points.Count}");
        var requestedDelete = -1;
        var requestedInsertAfter = int.MinValue;
        for (var index = 0; index < state.Points.Count; index++)
        {
            ImGui.PushID($"CurvePointRow{index}");
            var point = state.Points[index];
            var selectedPoint = index == _curvePointIndex;
            var label = $"点 {index}    ({point.Position.X:0.##}, {point.Position.Y:0.##}, {point.Position.Z:0.##})";
            if (ImGui.Selectable(label, selectedPoint))
            {
                _curvePointIndex = index;
                _curveActiveHandle = CurveHandleKind.Point;
            }

            if (ImGui.BeginPopupContextItem("##CurvePointContext"))
            {
                if (ImGui.MenuItem("在前插入点")) requestedInsertAfter = index - 1;
                if (ImGui.MenuItem("在后插入点")) requestedInsertAfter = index;
                if (ImGui.MenuItem("删除此点")) requestedDelete = index;
                ImGui.EndPopup();
            }

            if (selectedPoint) DrawCurvePointDetails(state, index);
            ImGui.PopID();
        }

        ImGui.Dummy(new Vector2(0F, UiScale.Px(2F)));
        var third = MathF.Max(ImGui.GetFontSize() * 2.5F,
            MathF.Floor((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2F) / 3F) - 1F);
        if (ImGui.Button("添加点", new Vector2(third, 0F)))
        {
            var position = state.Points.Count == 0
                ? new Vector3(0F, 2F, 0F)
                : state.Points[^1].Position + AppendDirection(state.Points);
            state.Points.Add(CurvePointData.At(position));
            CurveSpline.RecomputeAutoTangents(state.Points, state.Data.Closed);
            _curvePointIndex = state.Points.Count - 1;
            state.GeometryChanged = true;
            SetStatus("已在曲线末尾添加控制点");
        }

        ImGui.SameLine();
        if (ImGui.Button("在选中点后插入", new Vector2(third, 0F)) && _curvePointIndex >= 0 &&
            _curvePointIndex < state.Points.Count)
            requestedInsertAfter = _curvePointIndex;
        ImGui.SameLine();
        ImGui.BeginDisabled(_curvePointIndex < 0 || _curvePointIndex >= state.Points.Count);
        if (ImGui.Button("删除选中点", new Vector2(third, 0F))) requestedDelete = _curvePointIndex;
        ImGui.EndDisabled();
        if (requestedDelete >= 0) DeleteCurvePoint(state, requestedDelete);
        else if (requestedInsertAfter != int.MinValue) InsertCurvePoint(state, requestedInsertAfter);
        if (state.Points.Count == 0) ImGui.TextDisabled("曲线还没有控制点，点“添加点”开始绘制");
    }

    /// <summary>
    /// 选中点的位置、旋转、切线、宽度与增删操作
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    /// <param name="index">控制点下标</param>
    private void DrawCurvePointDetails(CurveEditState state, int index)
    {
        ImGui.Indent(10F);
        var point = state.Points[index];
        var position = point.Position;
        BeginInspectorField("位置", "相对对象原点的体素坐标，和对象的格变换一起决定曲线在世界里的位置。");
        if (DrawCurveFloat3("##CurvePointPosition", ref position, .1F, -1_000_000F, 1_000_000F))
        {
            state.Points[index] = point with { Position = position };
            CurveSpline.RecomputeAutoTangents(state.Points, state.Data.Closed);
            point = state.Points[index];
            state.GeometryChanged = true;
        }

        // 三个欧拉角里只有绕切线的滚转真正改变截面形状，其余分量会被正交化吃掉，
        // 所以这里只暴露一个“截面旋转”，避免调了没反应
        var current = state.Data with { Points = state.Points.ToArray() };
        var roll = CurveSpline.RollDegrees(current, index);
        if (InspectorDragFloatField("截面旋转", ref roll, .5F, -180F, 180F, "%.1f°",
                "截面绕曲线走向的滚转角，直接决定方块管与十字管的朝向；拖动场景里的旋转手柄改的是同一个角度。"))
        {
            state.Points[index] = point with { Rotation = CurveSpline.WithRoll(current, index, roll) };
            point = state.Points[index];
            state.GeometryChanged = true;
        }

        // 直线插值只用控制点位置与朝向，切线与联动方式都不参与生成
        var linear = state.Data.Interpolation == CurveInterpolation.Linear;
        if (!linear)
        {
            BeginInspectorField("切线联动", "自动按相邻点推算；对齐保持前后反向；镜像额外保持等长；自由则两侧互不影响。");
            var mode = (int)point.TangentMode;
            if (ImGui.Combo("##CurvePointTangentMode", ref mode, "自动\0对齐\0镜像\0自由\0"))
            {
                point = point with { TangentMode = (CurveTangentMode)mode };
                if (point.TangentMode == CurveTangentMode.Auto)
                    CurveSpline.RecomputeAutoTangents(state.Points, state.Data.Closed);
                state.Points[index] = point;
                state.GeometryChanged = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("拖动切线手柄时，自动模式会切换成自由模式");
            var linkingMode = point.TangentMode;
            var curve = state.Data with { Points = state.Points.ToArray() };
            CurveSpline.BasisAt(curve, index, out var up, out _);
            var nextDirection = ChordDirection(state.Points, index, true, state.Data.Closed);
            var previousDirection = ChordDirection(state.Points, index, false, state.Data.Closed);
            var width = point.Width;
            if (InspectorDragFloatField("宽度倍率", "截面按这个倍率缩放，用来做曲线的粗细渐变。", ref width, .01F,
                    .05F, 8F, "%.2f×"))
            {
                state.Points[index] = point with { Width = width };
                point = state.Points[index];
                state.GeometryChanged = true;
            }

            var outLength = 0F;
            var outYaw = 0F;
            var outPitch = 0F;
            CurveSpline.DecomposeTangent(point.TangentOut, nextDirection, up, out outLength, out outYaw, out outPitch);
            BeginInspectorField("出切线（长/水平/俯仰）", "朝向下一点的贝塞尔手柄，水平角与俯仰角以控制点走向为基准；拖动数值时自动切换到自由切线。");
            if (DrawCurveTangentAngles("##CurveTangentOut", ref outLength, ref outYaw, ref outPitch))
            {
                var tangent = CurveSpline.ComposeTangent(outLength, outYaw, outPitch, nextDirection, up);
                point = point with { TangentOut = tangent, TangentMode = CurveTangentMode.Free };
                if (linkingMode is CurveTangentMode.Aligned or CurveTangentMode.Mirrored)
                    point = CurveSpline.AlignTangent(point, editedOut: true,
                        forceSameLength: linkingMode == CurveTangentMode.Mirrored);
                state.Points[index] = point;
                point = state.Points[index];
                state.GeometryChanged = true;
            }

            var inLength = 0F;
            var inYaw = 0F;
            var inPitch = 0F;
            CurveSpline.DecomposeTangent(point.TangentIn, previousDirection, up, out inLength, out inYaw, out inPitch);
            BeginInspectorField("入切线（长/水平/俯仰）", "朝向上一点的贝塞尔手柄；长度决定进入该点时的弯曲程度。");
            if (DrawCurveTangentAngles("##CurveTangentIn", ref inLength, ref inYaw, ref inPitch))
            {
                var tangent = CurveSpline.ComposeTangent(inLength, inYaw, inPitch, previousDirection, up);
                point = point with { TangentIn = tangent, TangentMode = CurveTangentMode.Free };
                if (linkingMode is CurveTangentMode.Aligned or CurveTangentMode.Mirrored)
                    point = CurveSpline.AlignTangent(point, editedOut: false,
                        forceSameLength: linkingMode == CurveTangentMode.Mirrored);
                state.Points[index] = point;
                state.GeometryChanged = true;
            }
        }

        if (ImGui.SmallButton("复制此点到末尾"))
        {
            state.Points.Add(point);
            _curvePointIndex = state.Points.Count - 1;
            state.GeometryChanged = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("删除此点"))
        {
            DeleteCurvePoint(state, index);
        }

        ImGui.Unindent(10F);
        ImGui.Dummy(new Vector2(0F, UiScale.Px(2F)));
    }

    /// <summary>
    /// 生成参数与统计信息
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    /// <param name="state">编辑缓冲</param>
    private void DrawCurveGenerationSection(SceneObject sceneObject, ComponentRecord component, CurveEditState state)
    {
        ImGui.Separator();
        ImGui.TextDisabled("生成体素");
        BeginInspectorField("输出方式", "管体按截面扫掠成连续形状；沿曲线摆放会把体素源等距放在曲线上，适合路灯、围栏与阵列。");
        var output = (int)state.Data.Output;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##CurveOutputMode", ref output, "管体\0沿曲线摆放\0"))
        {
            state.Data = state.Data with { Output = (CurveOutputMode)output };
            state.GeometryChanged = true;
        }

        if (state.Data.Output == CurveOutputMode.Tube)
        {
            BeginInspectorField("截面形状", "方块管与圆管沿曲线铺开，珠链按采样间距排成球珠，十字管适合栏杆和窗棂。");
            var shape = (int)state.Data.Shape;
            ImGui.SetNextItemWidth(-1F);
            if (ImGui.Combo("##CurveBrushShape", ref shape, "方块管\0圆管\0珠链\0十字管\0"))
            {
                state.Data = state.Data with { Shape = (CurveBrushShape)shape };
                state.GeometryChanged = true;
            }

            var radius = state.Data.Radius;
            if (InspectorDragFloatField("半径（格）", ref radius, .05F, .1F, 64F, "%.2f",
                    "截面半径，乘以每个控制点的宽度倍率得到实际粗细。"))
            {
                state.Data = state.Data with { Radius = radius };
                state.GeometryChanged = true;
            }

            // 只有珠链按间距排布球珠；方块管、圆管与十字管沿连线扫掠，间距不再影响结果
            if (state.Data.Shape == CurveBrushShape.Beads)
            {
                var step = state.Data.Step;
                if (InspectorDragFloatField("采样间距（格）", ref step, .02F, .1F, 8F, "%.2f",
                        "珠链沿曲线排布球珠的间距；调大可以做出断续的点线效果。"))
                {
                    state.Data = state.Data with { Step = step };
                    state.GeometryChanged = true;
                }
            }

            // 直线插值直接连接控制点，细分只影响贝塞尔的平滑程度
            if (state.Data.Interpolation != CurveInterpolation.Linear)
            {
                var resolution = state.Data.Resolution;
                if (InspectorSliderIntField("曲线细分", "每段曲线的折线采样数；转弯越急越需要更高的细分。", ref resolution,
                        CurveRendererComponentData.MinimumResolution, CurveRendererComponentData.MaximumResolution))
                {
                    state.Data = state.Data with { Resolution = resolution };
                    state.GeometryChanged = true;
                }
            }

            BeginInspectorField("体素颜色", "生成体素统一使用的颜色；透明度小于 255 会自动加半透明标记。");
            var color = new Vector4(state.Data.ColorR / 255F, state.Data.ColorG / 255F,
                state.Data.ColorB / 255F, state.Data.ColorA / 255F);
            var pickerWidth = MathF.Max(ImGui.GetFontSize() * 4F, ImGui.GetContentRegionAvail().X -
                ImGui.CalcTextSize("取笔刷色").X - ImGui.GetStyle().FramePadding.X * 2F -
                ImGui.GetStyle().ItemSpacing.X);
            ImGui.SetNextItemWidth(pickerWidth);
            if (ImGui.ColorEdit4("##CurveColor", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar))
            {
                state.Data = state.Data with
                {
                    ColorR = ToByte(color.X), ColorG = ToByte(color.Y), ColorB = ToByte(color.Z), ColorA = ToByte(color.W),
                };
                state.GeometryChanged = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("取笔刷色"))
            {
                var brush = _sampledBrushColor ?? _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
                state.Data = state.Data with { ColorR = brush.R, ColorG = brush.G, ColorB = brush.B, ColorA = brush.A };
                state.GeometryChanged = true;
            }

            var emissive = state.Data.Emissive;
            if (ImGui.Checkbox("自发光", ref emissive))
            {
                state.Data = state.Data with { Emissive = emissive };
                state.GeometryChanged = true;
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("给生成体素加自发光标记");
            var materialSlot = (int)state.Data.MaterialSlot;
            if (InspectorDragIntField("材质槽", "生成体素使用的本地材质槽；0 表示使用全局材质。", ref materialSlot, .2F, 0,
                    ushort.MaxValue))
            {
                state.Data = state.Data with { MaterialSlot = (ushort)Math.Clamp(materialSlot, 0, ushort.MaxValue) };
                state.GeometryChanged = true;
            }
        }
        else
        {
            DrawCurveScatterSection(state);
        }

        BeginInspectorField("排序图层", "同一坐标按排序图层、排序顺序和层级顺序决定显示优先级。");
        var sortingLayer = state.Data.SortingLayer;
        if (_sceneDocument is { } sortingDocument && ImGui.BeginCombo("##CurveSortingLayer", sortingLayer))
        {
            foreach (var layer in sortingDocument.SortingLayerNames)
            {
                if (ImGui.Selectable(layer, layer == sortingLayer))
                {
                    state.Data = state.Data with { SortingLayer = layer };
                    state.SortingChanged = true;
                }
            }

            ImGui.EndCombo();
        }

        BeginInspectorField("排序顺序", "同一排序图层内，数值大的覆盖数值小的体素。");
        var sortingOrder = state.Data.SortingOrder;
        if (ExpressionInput.Int("##CurveSortingOrder", ref sortingOrder))
        {
            state.Data = state.Data with { SortingOrder = sortingOrder };
            state.SortingChanged = true;
        }

        var world = _objectVoxels.TryGetWorld(sceneObject.Id, out var objectWorld)
            ? objectWorld.OccupiedVoxelCount : 0;
        BeginInspectorField("体素数量", "当前曲线生成的体素数量；只读，体素内容不能直接编辑。");
        ImGui.TextDisabled($"{world:N0}");
        BeginInspectorField("数据体积", "场景文件里隐藏负载的大小，坐标差值压缩后通常只有体素数量的几分之一。");
        var payloadLength = SceneComponentTypes.GetGeneratedVoxelPayloadLength(component);
        ImGui.TextDisabled($"{payloadLength / 1024F:0.0} KB");
        if (world == 0) ImGui.TextDisabled("没有生成体素：检查控制点数量、截面半径或体素源与间距");
        else if (_generatedVoxelTruncated.TryGetValue(sceneObject.Id, out var truncated) && truncated)
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F),
                "生成结果已截断，请检查站点数量和体素数量上限");
        if (state.Data.Output == CurveOutputMode.Scatter)
        {
            var stations = SceneComponentTypes.LastScatterStations;
            var placed = SceneComponentTypes.LastScatterPlaced;
            if (stations > 0) ImGui.TextDisabled($"站点 {stations:N0} · 已放置 {placed:N0}");
            var scatterMessage = SceneComponentTypes.LastScatterMessage;
            if (!string.IsNullOrWhiteSpace(scatterMessage))
                ImGui.TextColored(new Vector4(1F, .55F, .45F, 1F), scatterMessage);
        }
        var half = MathF.Max(ImGui.GetFontSize() * 4F,
            MathF.Floor((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * .5F) - 1F);
        if (ImGui.Button("重新生成体素", new Vector2(half, 0F)))
        {
            RebuildGeneratedVoxels(sceneObject, component, "重新生成曲线体素");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("清空生成的体素"))
        {
            var current = SceneComponentTypes.ReadCurveRenderer(component);
            // 写入空负载并保留当前指纹：重新打开场景也是空的，改曲线参数后才重新生成
            SceneComponentTypes.WriteGeneratedVoxels(component, new VoxelWorld(), current.GenerationSignature());
            _objectVoxels.AssignWorld(sceneObject.Id, new VoxelWorld());
            _generatedVoxelTruncated.Remove(sceneObject.Id);
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            SetStatus("已清空曲线生成的体素；曲线本身保留，修改参数或点“重新生成体素”即可恢复");
        }
    }

    /// <summary>
    /// 沿曲线摆放的参数区：源、间距、朝向、抖动与贴地
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    private void DrawCurveScatterSection(CurveEditState state)
    {
        var source = state.Data.ScatterSource;
        var sourceLabel = source.Kind switch
        {
            VoxelSourceKind.ModelAsset => $"模型资产 · {source.DisplayName}",
            VoxelSourceKind.PrefabAsset => $"预制体 · {source.DisplayName}",
            _ => "未指定",
        };
        BeginInspectorField("体素源", "沿曲线摆放的物件来源：项目内的模型资产或预制体，不需要先拆成场景对象。");
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.BeginCombo("##CurveScatterSource", sourceLabel))
        {
            foreach (var relativePath in GetTileSourceAssets())
            {
                var kind = relativePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    ? VoxelSourceKind.PrefabAsset : VoxelSourceKind.ModelAsset;
                if (!ImGui.Selectable(relativePath, source.Kind == kind &&
                                                  string.Equals(source.AssetPath, relativePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                state.Data = state.Data with { ScatterSource = new VoxelSourceRef(kind, relativePath) };
                state.GeometryChanged = true;
            }

            ImGui.EndCombo();
        }

        BeginInspectorField("间距方式", "按固定弧长间距排布，或在起止范围内均分指定数量。");
        var spacingMode = (int)state.Data.SpacingMode;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##ScatterSpacingMode", ref spacingMode, "按间距\0按数量\0"))
        {
            state.Data = state.Data with { SpacingMode = (ScatterSpacingMode)spacingMode };
            state.GeometryChanged = true;
        }

        if (state.Data.SpacingMode == ScatterSpacingMode.Distance)
        {
            var spacing = state.Data.Spacing;
            if (InspectorDragFloatField("间距（格）", "相邻两个物件沿曲线弧长的距离。", ref spacing, .05F, .5F, 256F, "%.2f"))
            {
                state.Data = state.Data with { Spacing = spacing };
                state.GeometryChanged = true;
            }
        }
        else
        {
            var count = state.Data.ScatterCount;
            if (InspectorDragIntField("数量", "在起止范围内均分摆放的物件数量。", ref count, 1, 4096))
            {
                state.Data = state.Data with { ScatterCount = count };
                state.GeometryChanged = true;
            }
        }

        BeginInspectorField("对齐方式", "跟随切线会连同俯仰一起旋转；只绕竖轴转适合路灯与围栏；固定则保持源朝向。");
        var align = (int)state.Data.Align;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##ScatterAlign", ref align, "跟随切线\0只绕竖轴\0固定朝向\0"))
        {
            state.Data = state.Data with { Align = (ScatterAlign)align };
            state.GeometryChanged = true;
        }

        BeginInspectorField("支点", "中心让物件中心落在曲线上；底部让物件站在曲线上，配合贴地更自然。");
        var pivot = (int)state.Data.Pivot;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##ScatterPivot", ref pivot, "包围盒中心\0底部中心\0"))
        {
            state.Data = state.Data with { Pivot = (ScatterPivot)pivot };
            state.GeometryChanged = true;
        }

        var roll = state.Data.RollDegrees;
        if (InspectorDragFloatField("滚转角", "绕朝向轴旋转的角度，用来对齐朝向细节。", ref roll, .5F, -180F, 180F, "%.1f°"))
        {
            state.Data = state.Data with { RollDegrees = roll };
            state.GeometryChanged = true;
        }

        var startOffset = state.Data.StartOffset;
        if (InspectorDragFloatField("起点留空", "曲线起点跳过的一段弧长，避免物件贴墙或卡在地面边缘。", ref startOffset, .1F,
                0F, 1000F, "%.2f"))
        {
            state.Data = state.Data with { StartOffset = startOffset };
            state.GeometryChanged = true;
        }

        var endOffset = state.Data.EndOffset;
        if (InspectorDragFloatField("终点留空", null, ref endOffset, .1F, 0F, 1000F, "%.2f"))
        {
            state.Data = state.Data with { EndOffset = endOffset };
            state.GeometryChanged = true;
        }

        var jitterDistance = state.Data.JitterDistance;
        if (InspectorDragFloatField("间距抖动", "每个物件沿切线随机浮动的最长距离，固定种子保证可复现。", ref jitterDistance,
                .05F, 0F, 64F, "%.2f"))
        {
            state.Data = state.Data with { JitterDistance = jitterDistance };
            state.GeometryChanged = true;
        }

        var jitterAngle = state.Data.JitterAngle;
        if (InspectorDragFloatField("角度抖动", "每个物件在朝向上随机偏转的最大角度。", ref jitterAngle, .5F, 0F, 180F, "%.1f°"))
        {
            state.Data = state.Data with { JitterAngle = jitterAngle };
            state.GeometryChanged = true;
        }

        var seed = state.Data.ScatterSeed;
        if (InspectorDragIntField("随机种子", "固定后抖动结果每次生成完全一致。", ref seed, 0, 65535))
        {
            state.Data = state.Data with { ScatterSeed = seed };
            state.GeometryChanged = true;
        }

        var groundSnap = state.Data.GroundSnap;
        if (ImGui.Checkbox("贴地", ref groundSnap))
        {
            state.Data = state.Data with { GroundSnap = groundSnap };
            state.GeometryChanged = true;
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("沿竖直方向向下探测场景体素，把物件放到地面上");
        if (state.Data.GroundSnap)
        {
            var groundOffset = state.Data.GroundOffset;
            if (InspectorDragIntField("贴地偏移", "贴地后整体上下偏移的格数。", ref groundOffset, -64, 64))
            {
                state.Data = state.Data with { GroundOffset = groundOffset };
                state.GeometryChanged = true;
            }
        }

        BeginInspectorField("闭合曲线首尾相连", "闭合且起终点留空均为零时沿整圈均分，避免首尾重复；设置留空时按开放区间摆放。");
        var closedWrap = state.Data.ClosedWrap;
        if (ImGui.Checkbox("##ScatterClosedWrap", ref closedWrap))
        {
            state.Data = state.Data with { ClosedWrap = closedWrap };
            state.GeometryChanged = true;
        }

        // 细分决定沿曲线的采样密度，摆放与管体共用
        if (state.Data.Interpolation != CurveInterpolation.Linear)
        {
            var resolution = state.Data.Resolution;
            if (InspectorSliderIntField("曲线细分", "每段曲线的折线采样数；转弯越急越需要更高的细分。", ref resolution,
                    CurveRendererComponentData.MinimumResolution, CurveRendererComponentData.MaximumResolution))
            {
                state.Data = state.Data with { Resolution = resolution };
                state.GeometryChanged = true;
            }
        }
    }

    /// <summary>
    /// 提交检查器里的曲线改动
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    /// <param name="state">编辑缓冲</param>
    private void CommitCurveEditor(SceneObject sceneObject, ComponentRecord component, CurveEditState state)
    {
        if (!state.GeometryChanged && !state.SortingChanged) return;
        state.Data = state.Data with { Points = state.Points.ToArray() };
        SceneComponentTypes.WriteCurveRenderer(component, state.Data);
        if (state.GeometryChanged)
        {
            RebuildGeneratedVoxels(sceneObject, component, string.Empty);
        }
        else
        {
            // 排序只影响合成次序，不需要重算体素
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
        }
    }

    /// <summary>
    /// 删除控制点，同步更新检查器选中项
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    /// <param name="index">要删除的下标</param>
    private void DeleteCurvePoint(CurveEditState state, int index)
    {
        if (index < 0 || index >= state.Points.Count) return;
        state.Points.RemoveAt(index);
        if (state.Points.Count == 0)
        {
            _curvePointIndex = -1;
        }
        else
        {
            _curvePointIndex = Math.Clamp(index - 1, 0, state.Points.Count - 1);
        }

        state.GeometryChanged = true;
        SetStatus(state.Points.Count == 0 ? "已删除最后一个控制点，曲线不再生成体素" : "已删除控制点");
    }

    /// <summary>
    /// 在指定控制点之后插入新点，位置取前后两点连线中点
    /// </summary>
    /// <param name="state">编辑缓冲</param>
    /// <param name="afterIndex">插入位置的前一个点下标</param>
    private void InsertCurvePoint(CurveEditState state, int afterIndex)
    {
        if (state.Points.Count == 0) return;
        afterIndex = Math.Clamp(afterIndex, -1, state.Points.Count - 1);
        var before = state.Points[Math.Max(0, afterIndex)];
        var next = state.Points[Math.Min(state.Points.Count - 1, afterIndex + 1)];
        var position = afterIndex < 0
            ? state.Points.Count >= 2
                ? before.Position - (state.Points[1].Position - before.Position) * .5F
                : before.Position - new Vector3(4F, 0F, 0F)
            : afterIndex >= state.Points.Count - 1
                ? next.Position + AppendDirection(state.Points)
                : (before.Position + next.Position) * .5F;
        state.Points.Insert(afterIndex + 1,
            CurvePointData.At(position) with { Rotation = before.Rotation });
        CurveSpline.RecomputeAutoTangents(state.Points, state.Data.Closed);
        _curvePointIndex = afterIndex + 1;
        state.GeometryChanged = true;
        SetStatus("已插入控制点");
    }

    /// <summary>
    /// 曲线末尾的延伸方向，用于新点位置的默认值
    /// </summary>
    /// <param name="points">控制点</param>
    /// <returns>长度为 4 格的延伸向量</returns>
    private static Vector3 AppendDirection(List<CurvePointData> points)
    {
        if (points.Count == 0) return new Vector3(4F, 0F, 0F);
        if (points.Count == 1) return new Vector3(4F, 0F, 0F);
        var direction = points[^1].Position - points[^2].Position;
        return direction.LengthSquared() < 0.000001F ? new Vector3(4F, 0F, 0F) : Vector3.Normalize(direction) * 4F;
    }

    /// <summary>
    /// 取控制点在前方或后方的参考弦方向
    /// </summary>
    /// <param name="points">控制点</param>
    /// <param name="index">当前点</param>
    /// <param name="forward">true 取朝向下一点，false 取朝向上一点</param>
    /// <param name="closed">是否闭合曲线</param>
    /// <returns>弦方向，退化时回落到对象前方</returns>
    private static Vector3 ChordDirection(List<CurvePointData> points, int index, bool forward, bool closed)
    {
        if (points.Count < 2) return new Vector3(0F, 0F, 1F);
        var count = points.Count;
        index = Math.Clamp(index, 0, count - 1);
        var hasNeighbor = forward ? closed || index + 1 < count : closed || index > 0;
        if (!hasNeighbor)
        {
            // 端点没有对应方向的邻点，用反方向弦的相反数保持一致
            var fallbackIndex = forward ? index - 1 : index + 1;
            fallbackIndex = Math.Clamp(fallbackIndex, 0, count - 1);
            var fallback = points[index].Position - points[fallbackIndex].Position;
            return fallback.LengthSquared() < 0.000001F ? new Vector3(0F, 0F, 1F) : Vector3.Normalize(fallback);
        }

        var neighbor = forward ? points[(index + 1) % count] : points[(index - 1 + count) % count];
        var direction = neighbor.Position - points[index].Position;
        return direction.LengthSquared() < 0.000001F ? new Vector3(0F, 0F, 1F) : Vector3.Normalize(direction);
    }

    /// <summary>
    /// 三轴浮点拖动控件，宽度按可用区域等分
    /// </summary>
    /// <param name="id">控件作用域</param>
    /// <param name="value">三轴数值</param>
    /// <param name="speed">拖动速度</param>
    /// <param name="minimum">最小值</param>
    /// <param name="maximum">最大值</param>
    /// <returns>数值是否变化</returns>
    private static bool DrawCurveFloat3(string id, ref Vector3 value, float speed, float minimum, float maximum)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        // 末尾留 1 像素，避免取整后刚好越过内容宽度撑出横向滚动条
        var width = MathF.Max(ImGui.GetFontSize() * 2.5F,
            MathF.Floor((ImGui.GetContentRegionAvail().X - spacing * 2F) / 3F) - 1F);
        var changed = false;
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##X", ref value.X, speed, minimum, maximum);
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##Y", ref value.Y, speed, minimum, maximum);
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##Z", ref value.Z, speed, minimum, maximum);
        ImGui.PopID();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("依次为 X、Y、Z");
        return changed;
    }

    /// <summary>
    /// 切线长度、水平角与俯仰角三个拖动控件
    /// </summary>
    /// <param name="id">控件作用域</param>
    /// <param name="length">长度</param>
    /// <param name="yaw">水平角（度）</param>
    /// <param name="pitch">俯仰角（度）</param>
    /// <returns>是否有变化</returns>
    private static bool DrawCurveTangentAngles(string id, ref float length, ref float yaw, ref float pitch)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(ImGui.GetFontSize() * 2.5F,
            MathF.Floor((ImGui.GetContentRegionAvail().X - spacing * 2F) / 3F) - 1F);
        var changed = false;
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##Length", ref length, .05F, 0F, 1000F, "%.2f");
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##Yaw", ref yaw, .5F, -360F, 360F, "%.0f");
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragFloat("##Pitch", ref pitch, .5F, -89F, 89F, "%.0f");
        ImGui.PopID();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("依次为长度、水平角（度）、俯仰角（度）");
        return changed;
    }

    /// <summary>
    /// 按世界轴把曲线对象整体旋转 90 度，曲线点与朝向一起旋转后重建体素
    /// </summary>
    /// <param name="sceneObject">曲线对象</param>
    /// <param name="axisIndex">旋转轴，0 到 2</param>
    private void RotateCurveObjectBy90(SceneObject sceneObject, int axisIndex)
    {
        var component = GetCurveRendererComponent(sceneObject);
        if (component is null || sceneObject.Transform.Mode != TransformMode.Grid) return;
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (data.Points.Length == 0) return;
        if (!_objectVoxels.TryGetWorld(sceneObject.Id, out var world) || !world.TryGetBounds(out var minimum, out var maximum))
            return;
        var pivot = new Vector3((minimum.X + maximum.X + 1) * .5F, (minimum.Y + maximum.Y + 1) * .5F,
            (minimum.Z + maximum.Z + 1) * .5F);
        var rotation = AxisRotationQuaternion(axisIndex);
        var origin = GetObjectGridOrigin(sceneObject);
        var points = data.Points.Select(point => point with
        {
            Position = RotateAroundPlane(point.Position + new Vector3(origin.X, origin.Y, origin.Z), pivot, axisIndex) -
                       new Vector3(origin.X, origin.Y, origin.Z),
            TangentIn = RotateVector(point.TangentIn, axisIndex),
            TangentOut = RotateVector(point.TangentOut, axisIndex),
            Rotation = Quaternion.Normalize(rotation * point.Rotation),
        }).ToArray();
        var updated = data with { Points = points };
        SceneComponentTypes.WriteCurveRenderer(component, updated);
        var newRotation = sceneObject.Transform.Grid.Rotation.Compose(AxisRotation(axisIndex));
        var beforeTransform = sceneObject.Transform.Grid;
        sceneObject.Transform.SetGrid(new GridTransform(beforeTransform.Position, newRotation));
        UpdateTransformMarker(sceneObject);
        RebuildGeneratedVoxels(sceneObject, component, "旋转曲线对象");
        SetStatus("已旋转曲线对象 90 度，曲线点与切线随之旋转");
    }

    /// <summary>绕世界轴旋转 90 度使用的四元数</summary>
    private static Quaternion AxisRotationQuaternion(int axisIndex) =>
        Quaternion.CreateFromAxisAngle(axisIndex switch
        {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            _ => Vector3.UnitZ,
        }, MathF.PI * .5F);

    /// <summary>按世界轴把点绕格中心旋转 90 度</summary>
    private static Vector3 RotateAroundPlane(Vector3 position, Vector3 pivot, int axisIndex)
    {
        var x = position.X - pivot.X;
        var y = position.Y - pivot.Y;
        var z = position.Z - pivot.Z;
        var (rx, ry, rz) = axisIndex switch
        {
            0 => (x, -z, y),
            1 => (z, y, -x),
            _ => (-y, x, z),
        };
        return new Vector3(pivot.X + rx, pivot.Y + ry, pivot.Z + rz);
    }

    /// <summary>按世界轴旋转切线向量</summary>
    private static Vector3 RotateVector(Vector3 value, int axisIndex) => axisIndex switch
    {
        0 => new Vector3(value.X, -value.Z, value.Y),
        1 => new Vector3(value.Z, value.Y, -value.X),
        _ => new Vector3(-value.Y, value.X, value.Z),
    };
}
