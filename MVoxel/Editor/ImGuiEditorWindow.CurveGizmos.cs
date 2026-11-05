using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using OpenTK.Mathematics;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using NumVector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 曲线渲染器在场景视图里的可视化与手柄操作
/// </summary>
/// <remarks>
/// 曲线路径始终可见，编辑模式下额外显示控制点、前后切线与朝向手柄。
/// 拖动统一按相机平面换算世界位移，和控制点所在的格原点无关，
/// 手柄位置与世界点的对应关系与绘制时完全一致，不会出现点哪偏哪
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    private enum CurveGizmoDragKind
    {
        None,
        Point,
        TangentIn,
        TangentOut,
        Rotation,
    }

    private CurveGizmoDragKind _curveGizmoDragKind;
    private bool _curveGizmoDragging;
    private bool _curveGizmoDragChanged;
    private int _curveGizmoDragIndex = -1;
    private Vector2 _curveGizmoDragStartMouse;
    private float _curveGizmoPixelsPerUnit = 1F;
    private CurvePointData _curveGizmoDragStartPoint;
    private Vector3 _curveGizmoDragStartWorld;
    private Vector3 _curveGizmoDragStartHandleWorld;
    private readonly List<CurveSample> _curveSampleBuffer = new(512);

    /// <summary>切线入手柄颜色</summary>
    private static readonly Color CurveTangentInColor = Color.FromArgb(255, 236, 146, 82);
    /// <summary>切线出手柄颜色</summary>
    private static readonly Color CurveTangentOutColor = Color.FromArgb(255, 88, 196, 236);
    /// <summary>朝向手柄颜色</summary>
    private static readonly Color CurveRotationColor = Color.FromArgb(255, 146, 214, 118);

    /// <summary>
    /// 绘制全部曲线路径，选中对象额外绘制编辑手柄
    /// </summary>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="selected">当前选中对象</param>
    private void DrawCurveRendererOverlays(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject? selected)
    {
        if (_sceneDocument is null) return;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (selected?.Id == sceneObject.Id || !IsSceneObjectVisible(sceneObject) ||
                !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex)) continue;
            var component = GetCurveRendererComponent(sceneObject);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadCurveRenderer(component);
            if (data.Points.Length == 0) continue;
            DrawCurvePath(drawList, origin, size, camera, sceneObject, data,
                AlphaColor(ToPackedColor(EditorPalette.Icon), .5F), 1.2F);
        }

        if (selected is null || !IsSceneObjectVisible(selected) ||
            !_sceneDocument.IsLayerVisible(selected.LayerIndex)) return;
        var selectedComponent = GetCurveRendererComponent(selected);
        if (selectedComponent is null) return;
        DrawCurveEditor(drawList, origin, size, camera, selected,
            SceneComponentTypes.ReadCurveRenderer(selectedComponent));
    }

    /// <summary>
    /// 画一条曲线的折线路径
    /// </summary>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="owner">曲线所属对象</param>
    /// <param name="data">曲线数据</param>
    /// <param name="color">线条颜色</param>
    /// <param name="width">线宽</param>
    private void DrawCurvePath(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject owner, CurveRendererComponentData data, uint color, float width)
    {
        CurveSpline.BuildSamples(data, Math.Clamp(data.Resolution, 8, 32), _curveSampleBuffer);
        for (var index = 0; index + 1 < _curveSampleBuffer.Count; index++)
            DrawWorldLine(drawList, origin, size, camera,
                CurveLocalToWorld(owner, _curveSampleBuffer[index].Position),
                CurveLocalToWorld(owner, _curveSampleBuffer[index + 1].Position), color, width);
    }

    /// <summary>
    /// 画选中曲线的控制点、切线手柄与朝向手柄
    /// </summary>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="owner">曲线所属对象</param>
    /// <param name="data">曲线数据</param>
    private void DrawCurveEditor(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject owner, CurveRendererComponentData data)
    {
        var editing = data.Editing && _selectedSceneObjectId == owner.Id;
        var accent = ToPackedColor(EditorPalette.Accent);
        var points = data.Points;
        if (points.Length > 1)
        {
            // 控制多边形只在编辑时画，避免成品视图出现多余的辅助线
            var segments = data.Closed ? points.Length : points.Length - 1;
            var polygonColor = AlphaColor(ToPackedColor(EditorPalette.MutedText), editing ? .55F : .22F);
            for (var index = 0; index < segments; index++)
                DrawWorldLine(drawList, origin, size, camera,
                    CurveLocalToWorld(owner, points[index].Position),
                    CurveLocalToWorld(owner, points[(index + 1) % points.Length].Position), polygonColor, 1F);
        }

        DrawCurvePath(drawList, origin, size, camera, owner, data, accent, editing ? 2.4F : 1.8F);
        for (var index = 0; index < points.Length; index++)
        {
            if (!SceneProjection.Project(CurveLocalToWorld(owner, points[index].Position), origin, size, camera,
                    out var screen, out _)) continue;
            var selectedPoint = editing && index == _curvePointIndex;
            var radius = selectedPoint ? 6F : 4.2F;
            drawList.AddCircleFilled(screen, radius, selectedPoint ? accent : AlphaColor(accent, .85F), 16);
            drawList.AddCircle(screen, radius + 1F, AlphaColor(ToPackedColor(EditorPalette.Text), .8F), 16, 1.2F);
            if (editing && points.Length <= 48)
                drawList.AddText(screen + new Vector2(radius + 3F, -ImGui.GetTextLineHeight() * .7F),
                    AlphaColor(ToPackedColor(EditorPalette.Text), .85F), index.ToString());
        }

        if (!editing || _curvePointIndex < 0 || _curvePointIndex >= points.Length) return;
        var point = points[_curvePointIndex];
        var pointWorld = CurveLocalToWorld(owner, point.Position);
        if (!SceneProjection.Project(pointWorld, origin, size, camera, out _, out var depth)) return;
        var unitsPerPixel = MathF.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        CurveSpline.BasisAt(data, _curvePointIndex, out var up, out _);
        var handleLength = unitsPerPixel * 56F;
        var upHandle = pointWorld + ToWorld(up) * handleLength;
        if (SceneProjection.Project(upHandle, origin, size, camera, out var upScreen, out _))
        {
            var color = ToPackedColor(CurveRotationColor);
            DrawWorldLine(drawList, origin, size, camera, pointWorld, upHandle, color, 1.6F);
            drawList.AddCircleFilled(upScreen, 4.5F, color, 12);
            drawList.AddText(upScreen + new Vector2(6F, -4F), color, "朝向");
        }

        DrawCurveTangentHandle(drawList, origin, size, camera, pointWorld, point.TangentIn,
            ToPackedColor(CurveTangentInColor), "入");
        DrawCurveTangentHandle(drawList, origin, size, camera, pointWorld, point.TangentOut,
            ToPackedColor(CurveTangentOutColor), "出");
    }

    /// <summary>
    /// 画一个切线手柄：从控制点到手柄的连线加方块
    /// </summary>
    private void DrawCurveTangentHandle(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 pointWorld, NumVector3 tangent, uint color, string label)
    {
        if (tangent.LengthSquared() < 0.000001F) return;
        var handle = pointWorld + ToWorld(tangent);
        if (!SceneProjection.Project(handle, origin, size, camera, out var screen, out _)) return;
        DrawWorldLine(drawList, origin, size, camera, pointWorld, handle, AlphaColor(color, .85F), 1.4F);
        drawList.AddRectFilled(screen - new Vector2(3.5F), screen + new Vector2(3.5F), color, 1F);
        drawList.AddText(screen + new Vector2(6F, -4F), color, label);
    }

    /// <summary>
    /// 处理曲线手柄的按下、插入与删除
    /// </summary>
    /// <param name="mouse">鼠标位置</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <returns>是否已处理本次输入</returns>
    private bool HandleCurveGizmoInput(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_project is null || _sceneDocument is null) return false;
        if (!ImGui.IsMouseHoveringRect(origin, origin + size)) return false;
        var selected = GetSelectedSceneObject();
        var component = selected is null ? null : GetCurveRendererComponent(selected);
        var data = component is null ? default : SceneComponentTypes.ReadCurveRenderer(component);
        var editing = selected is not null && component is not null && data.Editing &&
                      _selectedSceneObjectId == selected.Id && IsSceneObjectVisible(selected) &&
                      _sceneDocument.IsLayerVisible(selected.LayerIndex);
        if (!editing)
        {
            // 非编辑状态：点击曲线附近也能选中对象，避免只能点对象原点
            if (_activeTool is not (EditorTool.Pick or EditorTool.Camera)) return false;
            return TrySelectCurveByClick(mouse, origin, size, camera, isDoubleClick: false);
        }

        var wantTextInput = ImGui.GetIO().WantTextInput;
        if (!wantTextInput && _curvePointIndex >= 0 && _curvePointIndex < data.Points.Length &&
            ImGui.IsKeyPressed(ImGuiKey.Delete))
        {
            DeleteCurvePointAt(selected!, component!, _curvePointIndex);
            return true;
        }

        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return false;
        if (TryBeginCurveGizmoDrag(mouse, origin, size, camera, selected!, data)) return true;

        // 双击曲线插入控制点，位置取屏幕最近点对应的曲线上一点
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
            TryPickCurveLocation(selected!, data, mouse, origin, size, camera, 10F, out var afterIndex, out var local))
        {
            InsertCurvePointOnCurve(selected!, component!, afterIndex, local);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 曲线编辑模式下响应删除键，删除当前选中的控制点
    /// </summary>
    /// <returns>是否消费了删除键</returns>
    private bool HandleCurveDeleteKey()
    {
        if (ImGui.GetIO().WantTextInput || !ImGui.IsKeyPressed(ImGuiKey.Delete)) return false;
        var selected = GetSelectedSceneObject();
        if (selected is null || !IsCurveEditing(selected)) return false;
        var component = GetCurveRendererComponent(selected);
        if (component is null) return false;
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (_curvePointIndex < 0 || _curvePointIndex >= data.Points.Length) return false;
        DeleteCurvePointAt(selected, component, _curvePointIndex);
        return true;
    }

    /// <summary>
    /// 非编辑模式下按曲线路径选中对象
    /// </summary>
    /// <param name="mouse">鼠标位置</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="isDoubleClick">是否双击，双击时顺带聚焦对象</param>
    /// <returns>是否选中了某个曲线对象</returns>
    private bool TrySelectCurveByClick(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        bool isDoubleClick)
    {
        if (_sceneDocument is null) return false;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex) ||
                IsSceneSelectionLockedAgainst(sceneObject.Id)) continue;
            var component = GetCurveRendererComponent(sceneObject);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadCurveRenderer(component);
            if (data.Points.Length == 0) continue;
            if (!TryPickCurveLocation(sceneObject, data, mouse, origin, size, camera, 6F, out _, out _)) continue;
            _selectedSceneObjectId = sceneObject.Id;
            _revealHierarchySelection = sceneObject.Id;
            _hierarchySearch = string.Empty;
            _activeTool = EditorTool.Pick;
            if (isDoubleClick) FocusHierarchyObject(sceneObject);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 求鼠标在屏幕空间距离最近的曲线位置，返回插入位置与局部坐标
    /// </summary>
    /// <param name="owner">曲线所属对象</param>
    /// <param name="data">曲线数据</param>
    /// <param name="mouse">鼠标位置</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="maximumDistance">允许的最大屏幕距离</param>
    /// <param name="afterPointIndex">插入位置的前一个控制点下标</param>
    /// <param name="localPosition">命中处的局部坐标</param>
    /// <returns>是否命中曲线</returns>
    private bool TryPickCurveLocation(SceneObject owner, CurveRendererComponentData data, Vector2 mouse,
        Vector2 origin, Vector2 size, SceneCamera camera, float maximumDistance, out int afterPointIndex,
        out NumVector3 localPosition)
    {
        afterPointIndex = -1;
        localPosition = NumVector3.Zero;
        if (data.Points.Length == 0) return false;
        const int perSegment = 12;
        CurveSpline.BuildSamples(data, perSegment, _curveSampleBuffer);
        if (_curveSampleBuffer.Count < 2) return false;
        var bestDistance = float.MaxValue;
        var bestT = 0F;
        var bestSegment = -1;
        for (var index = 0; index + 1 < _curveSampleBuffer.Count; index++)
        {
            if (!SceneProjection.Project(CurveLocalToWorld(owner, _curveSampleBuffer[index].Position), origin, size,
                    camera, out var a, out _) ||
                !SceneProjection.Project(CurveLocalToWorld(owner, _curveSampleBuffer[index + 1].Position), origin, size,
                    camera, out var b, out _)) continue;
            var ab = b - a;
            var lengthSquared = ab.LengthSquared();
            var t = lengthSquared < 0.0001F ? 0F : Math.Clamp(Vector2.Dot(mouse - a, ab) / lengthSquared, 0F, 1F);
            var distance = Vector2.Distance(mouse, a + ab * t);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestT = t;
            bestSegment = index;
        }

        if (bestSegment < 0 || bestDistance > maximumDistance) return false;
        afterPointIndex = Math.Clamp(bestSegment / perSegment, 0, data.Points.Length - 1);
        localPosition = NumVector3.Lerp(_curveSampleBuffer[bestSegment].Position,
            _curveSampleBuffer[bestSegment + 1].Position, bestT);
        return true;
    }

    /// <summary>
    /// 命中手柄时开始拖动
    /// </summary>
    private bool TryBeginCurveGizmoDrag(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject owner, CurveRendererComponentData data)
    {
        var points = data.Points;
        var index = Math.Clamp(_curvePointIndex, 0, points.Length - 1);
        var point = points[index];
        var pointWorld = CurveLocalToWorld(owner, point.Position);
        if (!SceneProjection.Project(pointWorld, origin, size, camera, out _, out var depth)) return false;
        var unitsPerPixel = MathF.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));

        CurveSpline.BasisAt(data, index, out var up, out _);
        var upHandle = pointWorld + ToWorld(up) * unitsPerPixel * 56F;
        if (SceneProjection.Project(upHandle, origin, size, camera, out var upScreen, out _) &&
            Vector2.Distance(mouse, upScreen) <= 9F)
        {
            BeginCurveDrag(CurveGizmoDragKind.Rotation, index, point, pointWorld, upHandle, unitsPerPixel, mouse);
            return true;
        }

        if (TryCurveHandleHit(pointWorld, point.TangentOut, mouse, origin, size, camera, out var tangentOutScreen))
        {
            BeginCurveDrag(CurveGizmoDragKind.TangentOut, index, point, pointWorld, tangentOutScreen,
                unitsPerPixel, mouse);
            return true;
        }

        if (TryCurveHandleHit(pointWorld, point.TangentIn, mouse, origin, size, camera, out var tangentInScreen))
        {
            BeginCurveDrag(CurveGizmoDragKind.TangentIn, index, point, pointWorld, tangentInScreen, unitsPerPixel,
                mouse);
            return true;
        }

        for (var candidate = 0; candidate < points.Length; candidate++)
        {
            if (!SceneProjection.Project(CurveLocalToWorld(owner, points[candidate].Position), origin, size, camera,
                    out var screen, out var candidateDepth)) continue;
            if (Vector2.Distance(mouse, screen) > 8F) continue;
            var candidateUnits = MathF.Max(0.0001F,
                candidateDepth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
            _curvePointIndex = candidate;
            BeginCurveDrag(CurveGizmoDragKind.Point, candidate, points[candidate],
                CurveLocalToWorld(owner, points[candidate].Position), Vector3.Zero, candidateUnits, mouse);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断切线手柄是否被命中，成功时回传手柄的世界位置
    /// </summary>
    private bool TryCurveHandleHit(Vector3 pointWorld, NumVector3 tangent, Vector2 mouse, Vector2 origin,
        Vector2 size, SceneCamera camera, out Vector3 handleWorld)
    {
        handleWorld = pointWorld + ToWorld(tangent);
        if (tangent.LengthSquared() < 0.000001F) return false;
        return SceneProjection.Project(handleWorld, origin, size, camera, out var screen, out _) &&
               Vector2.Distance(mouse, screen) <= 8F;
    }

    private void BeginCurveDrag(CurveGizmoDragKind kind, int index, CurvePointData point, Vector3 pointWorld,
        Vector3 handleWorld, float unitsPerPixel, Vector2 mouse)
    {
        _curveGizmoDragKind = kind;
        _curveGizmoDragging = true;
        _curveGizmoDragChanged = false;
        _sceneGizmoDragging = true;
        _curveGizmoDragIndex = index;
        _curveGizmoDragStartPoint = point;
        _curveGizmoDragStartWorld = pointWorld;
        _curveGizmoDragStartHandleWorld = handleWorld;
        _curveGizmoPixelsPerUnit = MathF.Max(0.0001F, unitsPerPixel);
        _curveGizmoDragStartMouse = mouse;
        _sceneGizmoDragStartMouse = mouse;
    }

    /// <summary>
    /// 拖动过程中的实时重建
    /// </summary>
    private void UpdateCurveGizmoDrag(Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var selected = GetSelectedSceneObject();
        if (selected is null) return;
        var component = GetCurveRendererComponent(selected);
        if (component is null) return;
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (_curveGizmoDragIndex < 0 || _curveGizmoDragIndex >= data.Points.Length) return;
        var mouse = ImGui.GetIO().MousePos;
        var deltaPixels = mouse - _curveGizmoDragStartMouse;
        // 按下但没移动时不重建体素，避免单击也触发一次全量生成
        if (deltaPixels.LengthSquared() < 0.04F) return;
        var delta = camera.Right * (deltaPixels.X * _curveGizmoPixelsPerUnit) +
                    camera.Up * (-deltaPixels.Y * _curveGizmoPixelsPerUnit);
        var points = data.Points.ToList();
        var point = points[_curveGizmoDragIndex];
        switch (_curveGizmoDragKind)
        {
            case CurveGizmoDragKind.Point:
                point = _curveGizmoDragStartPoint with
                {
                    Position = _curveGizmoDragStartPoint.Position + ToNum(delta),
                };
                points[_curveGizmoDragIndex] = point;
                CurveSpline.RecomputeAutoTangents(points, data.Closed);
                break;
            case CurveGizmoDragKind.TangentIn:
                point = point with
                {
                    TangentIn = _curveGizmoDragStartPoint.TangentIn + ToNum(delta),
                    TangentMode = CurveTangentMode.Free,
                };
                if (_curveGizmoDragStartPoint.TangentMode is CurveTangentMode.Aligned or CurveTangentMode.Mirrored)
                    point = CurveSpline.AlignTangent(point, editedOut: false,
                        forceSameLength: _curveGizmoDragStartPoint.TangentMode == CurveTangentMode.Mirrored);
                points[_curveGizmoDragIndex] = point;
                break;
            case CurveGizmoDragKind.TangentOut:
                point = point with
                {
                    TangentOut = _curveGizmoDragStartPoint.TangentOut + ToNum(delta),
                    TangentMode = CurveTangentMode.Free,
                };
                if (_curveGizmoDragStartPoint.TangentMode is CurveTangentMode.Aligned or CurveTangentMode.Mirrored)
                    point = CurveSpline.AlignTangent(point, editedOut: true,
                        forceSameLength: _curveGizmoDragStartPoint.TangentMode == CurveTangentMode.Mirrored);
                points[_curveGizmoDragIndex] = point;
                break;
            case CurveGizmoDragKind.Rotation:
            {
                var target = _curveGizmoDragStartHandleWorld + delta;
                var direction = target - _curveGizmoDragStartWorld;
                if (direction.LengthSquared < 0.000001F) return;
                var forward = CurveSpline.ForwardAt(data, _curveGizmoDragIndex);
                points[_curveGizmoDragIndex] = point with
                {
                    Rotation = CurveRotation.FromBasis(forward, ToNum(direction)),
                };
                break;
            }
        }

        SceneComponentTypes.WriteCurveRenderer(component, data with { Points = points.ToArray() });
        RebuildGeneratedVoxels(selected, component, string.Empty);
        _curveGizmoDragChanged = true;
    }

    /// <summary>
    /// 结束曲线手柄拖动
    /// </summary>
    private void EndCurveGizmoDrag()
    {
        var changed = _curveGizmoDragChanged;
        _curveGizmoDragging = false;
        _sceneGizmoDragging = false;
        _curveGizmoDragChanged = false;
        _curveGizmoDragKind = CurveGizmoDragKind.None;
        _curveGizmoDragIndex = -1;
        if (!changed) return;
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        SetStatus("曲线已更新，体素已同步重建");
    }

    /// <summary>
    /// 删除指定控制点并重建体素，供场景里的删除键使用
    /// </summary>
    /// <param name="sceneObject">曲线对象</param>
    /// <param name="component">曲线组件</param>
    /// <param name="index">控制点下标</param>
    private void DeleteCurvePointAt(SceneObject sceneObject, ComponentRecord component, int index)
    {
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (index < 0 || index >= data.Points.Length) return;
        var points = data.Points.ToList();
        points.RemoveAt(index);
        SceneComponentTypes.WriteCurveRenderer(component, data with { Points = points.ToArray() });
        _curvePointIndex = points.Count == 0 ? -1 : Math.Clamp(index - 1, 0, points.Count - 1);
        RebuildGeneratedVoxels(sceneObject, component, points.Count == 0 ? "已删除最后一个控制点" : "已删除控制点");
    }

    /// <summary>
    /// 在曲线上插入控制点并保持曲线走向
    /// </summary>
    /// <param name="sceneObject">曲线对象</param>
    /// <param name="component">曲线组件</param>
    /// <param name="afterIndex">插入位置的前一个控制点下标</param>
    /// <param name="localPosition">新控制点的局部坐标</param>
    private void InsertCurvePointOnCurve(SceneObject sceneObject, ComponentRecord component, int afterIndex,
        NumVector3 localPosition)
    {
        var data = SceneComponentTypes.ReadCurveRenderer(component);
        if (data.Points.Length == 0) return;
        var points = data.Points.ToList();
        afterIndex = Math.Clamp(afterIndex, 0, points.Count - 1);
        points.Insert(afterIndex + 1, CurvePointData.At(localPosition) with { Rotation = points[afterIndex].Rotation });
        CurveSpline.RecomputeAutoTangents(points, data.Closed);
        SceneComponentTypes.WriteCurveRenderer(component, data with { Points = points.ToArray() });
        _curvePointIndex = afterIndex + 1;
        RebuildGeneratedVoxels(sceneObject, component, "已在曲线上插入控制点");
    }

    /// <summary>
    /// 曲线局部坐标换算到世界坐标
    /// </summary>
    private static Vector3 CurveLocalToWorld(SceneObject owner, NumVector3 local)
    {
        var origin = GetObjectGridOrigin(owner);
        return new Vector3((float)origin.X + local.X, (float)origin.Y + local.Y, (float)origin.Z + local.Z);
    }

    private static Vector3 ToWorld(NumVector3 value) => new(value.X, value.Y, value.Z);

    private static NumVector3 ToNum(Vector3 value) => new(value.X, value.Y, value.Z);
}
