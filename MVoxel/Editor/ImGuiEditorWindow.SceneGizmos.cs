using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using OpenTK.Mathematics;
using Color = System.Drawing.Color;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 场景视口里的对象操作：移动/旋转/缩放 Gizmo、光照形状与范围手柄。
/// 光照对象不需要体素模型，范围手柄与检查器参数等价。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private enum SceneGizmoMode { Select, Move, Rotate, Scale, All }

    /// <summary>
    /// 切换左侧变换工具。与鼠标点按钮不同，快捷键切换保留当前选中对象，
    /// 否则按 Q 会先把选中清掉，后续的移动/旋转就无从谈起
    /// </summary>
    /// <param name="mode">目标工具</param>
    private void ApplySceneGizmoMode(SceneGizmoMode mode)
    {
        _sceneGizmoMode = mode;
        if (mode != SceneGizmoMode.Select) _lastTransformMode = mode;
    }

    private SceneGizmoMode _sceneGizmoMode = SceneGizmoMode.Select;
    private bool _sceneGizmoDragging;
    private int _sceneGizmoDragAxis;
    private bool _sceneGizmoDragRange;
    private bool _sceneGizmoDragScale;
    private bool _sceneGizmoDragProbe;
    private System.Numerics.Vector3 _sceneGizmoProbeStartSize;
    private System.Numerics.Vector3 _sceneGizmoProbeStartOffset;
    /// <summary>正在拖拽的探针面：0 到 2 为轴，符号表示拖的是哪一侧；-1 表示整体平移</summary>
    private int _sceneGizmoProbeAxis = -1;
    private int _sceneGizmoProbeFaceSign = 1;
    private bool _sceneGizmoDragRotation;
    private int _sceneGizmoDragPlane = -1;
    private Quaternion _sceneGizmoStartRotation;
    private float _sceneGizmoStartAngle;
    private float _sceneGizmoRotationDelta;
    private Vector2 _sceneGizmoPivotScreen;
    private Vector2 _sceneGizmoPlaneU;
    private Vector2 _sceneGizmoPlaneV;
    private Vector2 _sceneGizmoDragStartMouse;
    private Vector3 _sceneGizmoDragStartPosition;
    private VoxelCoord64 _sceneGizmoDragStartGrid;
    /// <summary>本次轴拖动使用的世界轴方向，局部空间下与对象轴向一致</summary>
    private Vector3 _sceneGizmoDragAxisWorld;
    /// <summary>拖动开始时对象自身的位置，和手柄支点可能不同（中心枢轴）</summary>
    private Vector3 _sceneGizmoDragStartObjectPosition;

    /// <summary>当前鼠标悬停的坐标轴 / 平面 / 旋转圆环 / 缩放手柄，-1 表示没有</summary>
    private int _gizmoHoverAxis = -1;
    private int _gizmoHoverPlane = -1;
    private int _gizmoHoverRotateAxis = -1;
    private bool _gizmoHoverScale;

    /// <summary>网格对象拖动预览：起始世界副本、起始格变换与累计格位移</summary>
    private VoxelWorld? _sceneGizmoGridDragStartWorld;
    private GridTransform _sceneGizmoGridDragStartTransform;
    private VoxelCoord64 _sceneGizmoGridDragCells;
    // 拖动中的对象：合成渲染世界时按 _sceneGizmoGridDragCells 给它叠加位移
    private StableId? _sceneGizmoGridDragObjectId;
    // 拖动实例渲染：静态世界排除被拖对象，对象单独建 BLAS，拖动只改实例平移
    private VoxelWorld? _dragInstanceStaticWorld;
    private VoxelWorld? _dragInstanceObjectWorld;
    private StableId _dragInstanceObjectId = StableId.Empty;
    private float _sceneGizmoDragStartValue;
    private float _sceneGizmoDragPixelsPerUnit = 1F;
    private Vector2 _sceneGizmoDragScreenDirection;

    private static readonly Color AxisXColor = Color.FromArgb(255, 232, 84, 92);
    private static readonly Color AxisYColor = Color.FromArgb(255, 108, 214, 132);
    private static readonly Color AxisZColor = Color.FromArgb(255, 86, 156, 232);

    /// <summary>场景对象在视口里显示的图标类型，每个对象只显示一个</summary>
    private enum SceneObjectIconKind
    {
        /// <summary>模型对象直接显示体素，不再画图标</summary>
        None,
        Light,
        Camera,
        ProbeVolume,
        CurveRenderer,
        TreeGenerator,
        Generic,
    }

    /// <summary>
    /// 按组件优先级决定对象图标：模型最高（直接显示体素），其次光照、相机、
    /// 自动探针、曲线渲染器与树生成器，最后才是通用对象
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>该对象应当显示的图标类型</returns>
    private static SceneObjectIconKind ResolveSceneObjectIcon(SceneObject sceneObject)
    {
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) && component.Enabled))
            return SceneObjectIconKind.None;
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsLight(component) && component.Enabled))
            return SceneObjectIconKind.Light;
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsCamera(component) && component.Enabled))
            return SceneObjectIconKind.Camera;
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsProbeVolume(component) && component.Enabled))
            return SceneObjectIconKind.ProbeVolume;
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsCurveRenderer(component) && component.Enabled))
            return SceneObjectIconKind.CurveRenderer;
        if (sceneObject.Components.Any(component => SceneComponentTypes.IsTreeGenerator(component) && component.Enabled))
            return SceneObjectIconKind.TreeGenerator;
        return SceneObjectIconKind.Generic;
    }

    private SceneObject? GetSelectedSceneObject() =>
        _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId);

    /// <summary>
    /// 绘制场景 Gizmo。光照形状对所有可见光照对象显示，选中对象额外显示移动/旋转/缩放手柄。
    /// </summary>
    private void DrawSceneGizmos(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_project is null || _sceneDocument is null) return;
        var selected = GetSelectedSceneObject();
        if (selected is not null && (!IsSceneObjectVisible(selected) || !_sceneDocument.IsLayerVisible(selected.LayerIndex))) selected = null;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            var lightComponent = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsLight);
            if (lightComponent is null || !lightComponent.Enabled || !IsSceneObjectVisible(sceneObject) ||
                !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex)) continue;
            var selectedLight = selected?.Id == sceneObject.Id;
            if (!selectedLight)
            {
                // 图标按组件优先级只画一个：模型最高（直接显示体素），其次光照、相机与其他组件
                if (ResolveSceneObjectIcon(sceneObject) != SceneObjectIconKind.Light) continue;
                // 与渲染光源和拾取共用对象位置；外圈标明这是光源，而非体素。
                if (SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera, out var marker, out _) &&
                    marker.X >= origin.X && marker.X < origin.X + size.X && marker.Y >= origin.Y && marker.Y < origin.Y + size.Y)
                {
                    var light = SceneComponentTypes.ReadLight(lightComponent);
                    var color = ToPackedColor(Color.FromArgb(220, Math.Clamp((int)(light.ColorR * 255F), 0, 255),
                        Math.Clamp((int)(light.ColorG * 255F), 0, 255), Math.Clamp((int)(light.ColorB * 255F), 0, 255)));
                    DrawSceneLightIcon(drawList, marker, color);
                    if (ImGui.IsWindowHovered() && Vector2.Distance(ImGui.GetIO().MousePos, marker) < 8F)
                        ImGui.SetTooltip(sceneObject.Name);
                }
                continue;
            }

            DrawLightGizmo(drawList, origin, size, camera, sceneObject, lightComponent, selected: true);
        }

        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex)) continue;
            if (ResolveSceneObjectIcon(sceneObject) != SceneObjectIconKind.Camera) continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera, out var marker, out _) ||
                marker.X < origin.X || marker.X >= origin.X + size.X || marker.Y < origin.Y || marker.Y >= origin.Y + size.Y) continue;
            var color = ToPackedColor(selected?.Id == sceneObject.Id ? EditorPalette.Accent : EditorPalette.Icon);
            DrawSceneCameraIcon(drawList, marker, color);
            if (ImGui.IsWindowHovered() && Vector2.Distance(ImGui.GetIO().MousePos, marker) < 9F)
                ImGui.SetTooltip(sceneObject.Name);
        }
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex)) continue;
            var iconKind = ResolveSceneObjectIcon(sceneObject);
            if (iconKind is SceneObjectIconKind.None or SceneObjectIconKind.Light or SceneObjectIconKind.Camera)
                continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera, out var marker, out _) ||
                marker.X < origin.X || marker.X >= origin.X + size.X || marker.Y < origin.Y || marker.Y >= origin.Y + size.Y) continue;
            var color = ToPackedColor(selected?.Id == sceneObject.Id ? EditorPalette.Accent : EditorPalette.Icon);
            switch (iconKind)
            {
                case SceneObjectIconKind.ProbeVolume:
                    DrawSceneProbeIcon(drawList, marker, color);
                    break;
                case SceneObjectIconKind.CurveRenderer:
                    DrawSceneCurveIcon(drawList, marker, color);
                    break;
                case SceneObjectIconKind.TreeGenerator:
                    DrawSceneTreeIcon(drawList, marker, color);
                    break;
                default:
                    DrawSceneGenericIcon(drawList, marker, color);
                    break;
            }

            if (ImGui.IsWindowHovered() && Vector2.Distance(ImGui.GetIO().MousePos, marker) < 10F)
                ImGui.SetTooltip(sceneObject.Name);
        }
        DrawCurveRendererOverlays(drawList, origin, size, camera, selected);
        if (selected is null) return;
        foreach (var component in selected.Components.Where(item => item.Enabled && SceneComponentTypes.IsSurfaceDeposit(item)))
            DrawSurfaceDepositBox(drawList, origin, size, camera, selected, component);
        var pivot = GetGizmoPivot(selected);
        if (!SceneProjection.Project(pivot, origin, size, camera, out var pivotScreen, out var depth)) return;
        var unitsPerPixel = Math.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        var probeComponent = selected.Components.FirstOrDefault(SceneComponentTypes.IsProbeVolume);
        if (probeComponent is not null)
        {
            var probe = SceneComponentTypes.ReadProbeVolume(probeComponent);
            DrawProbeVolumeBox(drawList, origin, size, camera, pivot, probe, probe.Editing);
        }

        var selectionComponent = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        if (selectionComponent is not null)
        {
            // 旧场景的世界坐标范围在这里也先迁移成局部坐标，避免检查器没打开时框画偏
            MigrateVoxelSelectionToLocal(selected, selectionComponent);
            DrawVoxelSelectionGizmo(drawList, origin, size, camera, selected,
                SceneComponentTypes.ReadVoxelSelection(selectionComponent));
        }

        // 曲线编辑期间不叠对象变换手柄，避免和曲线控制点的点击区域互相抢输入
        if (IsCurveEditing(selected)) return;
        UpdateGizmoHover(pivot, pivotScreen, unitsPerPixel, origin, size, camera);
        switch (_sceneGizmoMode)
        {
            case SceneGizmoMode.Move:
                DrawMoveGizmo(drawList, origin, size, camera, pivot, pivotScreen, unitsPerPixel, selected);
                break;
            case SceneGizmoMode.Rotate:
                DrawRotateGizmo(drawList, origin, size, camera, pivot, unitsPerPixel);
                break;
            case SceneGizmoMode.Scale:
                DrawScaleGizmo(drawList, origin, size, camera, selected, pivot, pivotScreen);
                break;
            case SceneGizmoMode.All:
                DrawMoveGizmo(drawList, origin, size, camera, pivot, pivotScreen, unitsPerPixel, selected);
                DrawRotateGizmo(drawList, origin, size, camera, pivot, unitsPerPixel);
                DrawScaleGizmo(drawList, origin, size, camera, selected, pivot, pivotScreen);
                break;
        }
    }

    /// <summary>
    /// 计算鼠标当前悬停的手柄，供绘制时高亮；拖动中由拖动状态决定高亮
    /// </summary>
    private void UpdateGizmoHover(Vector3 pivot, Vector2 pivotScreen, float unitsPerPixel, Vector2 origin,
        Vector2 size, SceneCamera camera)
    {
        _gizmoHoverAxis = -1;
        _gizmoHoverPlane = -1;
        _gizmoHoverRotateAxis = -1;
        _gizmoHoverScale = false;
        if (_sceneGizmoDragging) return;
        var mouse = ImGui.GetIO().MousePos;
        if (!ImGui.IsMouseHoveringRect(origin, origin + size)) return;
        var bestPlaneDistance = float.MaxValue;
        for (var plane = 0; plane < 3; plane++)
        {
            var (u, v) = AxisPlane(plane);
            if (!SceneProjection.Project(pivot + (u + v) * unitsPerPixel * 22F, origin, size, camera,
                    out var point, out _)) continue;
            var distance = Vector2.Distance(mouse, point);
            if (distance > 6.5F * 1.3F * 1.2F * 1.5F || distance >= bestPlaneDistance) continue;
            bestPlaneDistance = distance;
            _gizmoHoverPlane = plane;
        }

        for (var axisIndex = 0; axisIndex < 3; axisIndex++)
        {
            if (!TryProjectAxis(origin, size, camera, pivot, AxisVector(axisIndex), out var end, out _)) continue;
            if (DistanceToSegment(mouse, pivotScreen, end) <= 9F) _gizmoHoverAxis = axisIndex;
            var (u, v) = AxisPlane(axisIndex);
            var radius = unitsPerPixel * 58F;
            var best = float.MaxValue;
            for (var step = 0; step < 32; step++)
            {
                var angle = MathF.Tau * step / 32F;
                var world = pivot + u * (MathF.Cos(angle) * radius) + v * (MathF.Sin(angle) * radius);
                if (!SceneProjection.Project(world, origin, size, camera, out var screen, out _)) continue;
                best = MathF.Min(best, Vector2.Distance(mouse, screen));
            }

            if (best <= 9F) _gizmoHoverRotateAxis = axisIndex;
        }

        if (Vector2.Distance(mouse, pivotScreen + new Vector2(0F, -72F)) <= 12F) _gizmoHoverScale = true;
    }

    /// <summary>某条轴是否处于悬停或拖动状态</summary>
    private bool IsAxisHighlighted(int axisIndex) =>
        _gizmoHoverAxis == axisIndex ||
        (_sceneGizmoDragging && _sceneGizmoDragPlane < 0 && !_sceneGizmoDragRotation && !_sceneGizmoDragScale &&
         _sceneGizmoDragAxis == axisIndex);

    private bool IsPlaneHighlighted(int planeIndex) =>
        _gizmoHoverPlane == planeIndex || (_sceneGizmoDragging && _sceneGizmoDragPlane == planeIndex);

    private bool IsRotateHighlighted(int axisIndex) =>
        _gizmoHoverRotateAxis == axisIndex || (_sceneGizmoDragging && _sceneGizmoDragRotation &&
                                              _sceneGizmoDragAxis == axisIndex);

    private bool IsScaleHighlighted() =>
        _gizmoHoverScale || (_sceneGizmoDragging && _sceneGizmoDragScale);

    private void DrawSurfaceDepositBox(ImDrawListPtr drawList, Vector2 origin, Vector2 size,
        SceneCamera camera, SceneObject owner, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadSurfaceDeposit(component);
        if (!data.Enabled) return;
        var matrix = SurfaceDepositWorld(owner, data);
        Span<Vector3> points = stackalloc Vector3[8];
        for (var index = 0; index < points.Length; index++)
        {
            var local = new System.Numerics.Vector3(
                ((index & 1) == 0 ? -.5F : .5F) * data.Size.X,
                ((index & 2) == 0 ? -.5F : .5F) * data.Size.Y,
                ((index & 4) == 0 ? -.5F : .5F) * data.Size.Z);
            var world = System.Numerics.Vector3.Transform(local, matrix);
            points[index] = new Vector3(world.X, world.Y, world.Z);
        }
        var color = ToPackedColor(SceneComponentTypes.IsSnowDecal(component)
            ? Color.FromArgb(220, 190, 225, 255) : Color.FromArgb(220, 90, 185, 245));
        for (var index = 0; index < points.Length; index++)
            for (var axis = 0; axis < 3; axis++)
                if ((index & (1 << axis)) == 0)
                    DrawWorldLine(drawList, origin, size, camera, points[index], points[index | (1 << axis)], color, 1.5F);
    }

    private static void DrawSceneLightIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var bulb = center + new Vector2(0F, -1F);
        draw.AddCircle(bulb, 4F, color, 16, 1.4F);
        draw.AddLine(center + new Vector2(-2F, 4F), center + new Vector2(2F, 4F), color, 1.5F);
        draw.AddLine(center + new Vector2(-1.5F, 6F), center + new Vector2(1.5F, 6F), color, 1.4F);
        foreach (var direction in new[] { new Vector2(0F, -1F), new Vector2(-1F, 0F), new Vector2(1F, 0F),
                     new Vector2(-.7F, -.7F), new Vector2(.7F, -.7F) })
            draw.AddLine(bulb + direction * 6F, bulb + direction * 8F, color, 1.2F);
    }

    private static void DrawSceneCameraIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        draw.AddRect(center + new Vector2(-7F, -4F), center + new Vector2(4F, 5F), color, 1F, ImDrawFlags.None, 1.4F);
        draw.AddCircle(center + new Vector2(-1F, .5F), 2.5F, color, 12, 1.2F);
        draw.AddTriangle(center + new Vector2(4F, -2F), center + new Vector2(8F, -4F),
            center + new Vector2(8F, 4F), color, 1.4F);
    }

    /// <summary>
    /// 自动探针图标：一个探针体积的轮廓加中心点
    /// </summary>
    private static void DrawSceneProbeIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var corner = new Vector2(6F, 5.5F);
        draw.AddRect(center - corner, center + corner, color, 1F, ImDrawFlags.None, 1.2F);
        draw.AddLine(center + new Vector2(-6F, 0F), center + new Vector2(6F, 0F), color, 1F);
        draw.AddCircleFilled(center, 1.7F, color, 8);
    }

    /// <summary>
    /// 曲线渲染器图标：一段贝塞尔曲线加两个端点
    /// </summary>
    private static void DrawSceneCurveIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var start = center + new Vector2(-8F, 5F);
        var control = center + new Vector2(0F, -11F);
        var end = center + new Vector2(8F, 5F);
        draw.AddBezierQuadratic(start, control, end, color, 1.4F, 16);
        draw.AddCircleFilled(start, 1.8F, color, 8);
        draw.AddCircleFilled(end, 1.8F, color, 8);
    }

    /// <summary>
    /// 树生成器图标：树干、分叉与树冠
    /// </summary>
    private static void DrawSceneTreeIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        draw.AddLine(center + new Vector2(0F, 7F), center + new Vector2(0F, 0F), color, 1.6F);
        draw.AddLine(center + new Vector2(-3F, 2.5F), center + new Vector2(0F, -1F), color, 1.2F);
        draw.AddLine(center + new Vector2(3F, 2.5F), center + new Vector2(0F, -1F), color, 1.2F);
        draw.AddCircleFilled(center + new Vector2(0F, -4.5F), 4.6F, color, 14);
    }

    /// <summary>
    /// 通用对象图标：方框加十字
    /// </summary>
    private static void DrawSceneGenericIcon(ImDrawListPtr draw, Vector2 center, uint color)
    {
        draw.AddRect(center - new Vector2(5F), center + new Vector2(5F), color, 1F, ImDrawFlags.None, 1.3F);
        draw.AddLine(center - new Vector2(3F, 0F), center + new Vector2(3F, 0F), color);
        draw.AddLine(center - new Vector2(0F, 3F), center + new Vector2(0F, 3F), color);
    }

    /// <summary>Gizmo 使用的支点：按设置取对象原点或选中包围盒中心</summary>
    private Vector3 GetGizmoPivot(SceneObject sceneObject)
    {
        if (!_editorSettings.GizmoPivotCenter) return GetObjectPivot(sceneObject);
        if (!_objectVoxels.TryGetWorld(sceneObject.Id, out var world) ||
            !world.TryGetBounds(out var minimum, out var maximum))
            return GetObjectPivot(sceneObject);
        return new Vector3(
            (float)((minimum.X + maximum.X + 1) * 0.5),
            (float)((minimum.Y + maximum.Y + 1) * 0.5),
            (float)((minimum.Z + maximum.Z + 1) * 0.5));
    }

    private Vector3 GetObjectPivot(SceneObject sceneObject)
    {
        if (sceneObject.Transform.Mode == TransformMode.Free)
            return new Vector3(
                (float)sceneObject.Transform.Free.Position.X,
                (float)sceneObject.Transform.Free.Position.Y,
                (float)sceneObject.Transform.Free.Position.Z);
        var position = sceneObject.Transform.Grid.Position;
        return new Vector3(position.X + 0.5F, position.Y + 0.5F, position.Z + 0.5F);
    }

    private void DrawLightGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject sceneObject, ComponentRecord component, bool selected)
    {
        var data = SceneComponentTypes.ReadLight(component);
        var position = GetObjectWorldPosition(sceneObject);
        var color = selected
            ? Color.FromArgb(255, 255, 214, 120)
            : Color.FromArgb(150,
                Math.Clamp((int)(data.ColorR * 255F), 0, 255),
                Math.Clamp((int)(data.ColorG * 255F), 0, 255),
                Math.Clamp((int)(data.ColorB * 255F), 0, 255));
        var packed = ToPackedColor(color);
        var width = selected ? 2.2F : 1.2F;
        var range = Math.Max(0.5F, data.Range);

        if (data.Shape == LightShape.Point)
        {
            DrawWorldCircle(drawList, origin, size, camera, position, Vector3.UnitX, Vector3.UnitY, range, packed, width);
            DrawWorldCircle(drawList, origin, size, camera, position, Vector3.UnitX, Vector3.UnitZ, range, packed, width);
            DrawWorldCircle(drawList, origin, size, camera, position, Vector3.UnitY, Vector3.UnitZ, range, packed, width);
        }
        else if (data.Shape == LightShape.Spot)
        {
            var forward = GetObjectWorldForward(sceneObject);
            var right = Vector3.Cross(forward, Vector3.UnitY);
            if (right.LengthSquared < 0.0001F) right = Vector3.UnitX;
            right = Vector3.Normalize(right);
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var outerRadius = MathF.Tan(MathHelper.DegreesToRadians(data.SpotAngleDegrees) * 0.5F) * range;
            foreach (var (dx, dy) in new[] { (1F, 0F), (-1F, 0F), (0F, 1F), (0F, -1F) })
            {
                var end = position + forward * range + (right * dx + up * dy) * outerRadius;
                DrawWorldLine(drawList, origin, size, camera, position, end, packed, width);
            }

            var center = position + forward * range;
            DrawWorldCircle(drawList, origin, size, camera, center, right, up, outerRadius, packed, width);
        }
        else if (data.Shape == LightShape.Area)
        {
            // 面积灯画成矩形线框，朝向与渲染端一致：右轴由朝向叉乘世界向上得到
            var forward = GetObjectWorldForward(sceneObject);
            var right = Vector3.Cross(forward, Vector3.UnitY);
            if (right.LengthSquared < 0.0001F) right = Vector3.UnitX;
            right = Vector3.Normalize(right);
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var halfWidth = MathF.Max(0.05F, data.AreaWidth * 0.5F);
            var halfHeight = MathF.Max(0.05F, data.AreaHeight * 0.5F);
            var corner00 = position - right * halfWidth - up * halfHeight;
            var corner10 = position + right * halfWidth - up * halfHeight;
            var corner11 = position + right * halfWidth + up * halfHeight;
            var corner01 = position - right * halfWidth + up * halfHeight;
            DrawWorldLine(drawList, origin, size, camera, corner00, corner10, packed, width);
            DrawWorldLine(drawList, origin, size, camera, corner10, corner11, packed, width);
            DrawWorldLine(drawList, origin, size, camera, corner11, corner01, packed, width);
            DrawWorldLine(drawList, origin, size, camera, corner01, corner00, packed, width);
            // 发光方向：从灯面中心沿传播方向画一小段，方便判断正反面
            DrawWorldLine(drawList, origin, size, camera, position, position + forward * 6F, packed, width);
        }
        else
        {
            var forward = GetObjectWorldForward(sceneObject);
            var end = position + forward * 8F;
            DrawWorldLine(drawList, origin, size, camera, position, end, packed, width);
            var perpendicular = Vector3.Cross(forward, Vector3.UnitY);
            if (perpendicular.LengthSquared < 0.0001F) perpendicular = Vector3.UnitX;
            perpendicular = Vector3.Normalize(perpendicular);
            DrawWorldLine(drawList, origin, size, camera, end, end - forward * 1.2F + perpendicular * .7F, packed, width);
            DrawWorldLine(drawList, origin, size, camera, end, end - forward * 1.2F - perpendicular * .7F, packed, width);
        }

        if (!SceneProjection.Project(position, origin, size, camera, out var centerScreen, out _)) return;
        DrawSceneLightIcon(drawList, centerScreen, packed);
        if (!selected || data.Shape == LightShape.Directional) return;

        // 范围手柄：沿屏幕右方向的世界向量放置，拖动即可修改 Range
        var handle = position + camera.Right * range;
        if (SceneProjection.Project(handle, origin, size, camera, out var handleScreen, out _))
        {
            drawList.AddLine(centerScreen, handleScreen, packed, 1.2F);
            drawList.AddRectFilled(handleScreen - new Vector2(4F), handleScreen + new Vector2(4F), packed);
        }
    }

    /// <summary>
    /// 画探针体积的三维线框盒，编辑状态下额外画六面与中心手柄
    /// </summary>
    /// <remarks>
    /// 偏移与尺寸都以世界轴为准，和渲染端按世界坐标取样保持一致；
    /// 十二根棱按离相机远近分级透明度，避免正方体看起来像平面
    /// </remarks>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="pivot">探针对象的世界参考点</param>
    /// <param name="probe">探针组件参数</param>
    /// <param name="editing">是否处于探针编辑状态</param>
    private void DrawProbeVolumeBox(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 pivot, ProbeVolumeComponentData probe, bool editing)
    {
        var center = pivot + new Vector3(probe.OffsetX, probe.OffsetY, probe.OffsetZ);
        var half = new Vector3(
            MathF.Max(.05F, probe.SizeX) * .5F, MathF.Max(.05F, probe.SizeY) * .5F, MathF.Max(.05F, probe.SizeZ) * .5F);
        Span<Vector3> corners = stackalloc Vector3[8];
        Span<Vector2> projected = stackalloc Vector2[8];
        Span<float> depths = stackalloc float[8];
        var visible = 0;
        for (var index = 0; index < 8; index++)
        {
            var sx = (index & 1) == 0 ? -1F : 1F;
            var sy = (index & 2) == 0 ? -1F : 1F;
            var sz = (index & 4) == 0 ? -1F : 1F;
            corners[index] = center + new Vector3(half.X * sx, half.Y * sy, half.Z * sz);
            if (!SceneProjection.Project(corners[index], origin, size, camera, out projected[index], out depths[index]))
                continue;
            visible++;
        }

        // 八个角全都投影成功才画，避免相机贴到盒子里时出现乱线
        if (visible == 8)
        {
            var color = ToPackedColor(editing ? EditorPalette.Accent : EditorPalette.Icon);
            var nearDepth = 0F;
            var farDepth = 0F;
            for (var index = 0; index < 8; index++)
            {
                if (index == 0 || depths[index] < nearDepth) nearDepth = depths[index];
                if (index == 0 || depths[index] > farDepth) farDepth = depths[index];
            }

            var span = MathF.Max(.001F, farDepth - nearDepth);
            // 十二条棱：X 方向 4 条，Y 方向 4 条，Z 方向 4 条
            for (var axis = 0; axis < 3; axis++)
            {
                var step = 1 << axis;
                for (var index = 0; index < 8; index++)
                {
                    if ((index & step) != 0) continue;
                    var other = index | step;
                    var alphaNear = Math.Clamp(1F - ((depths[index] + depths[other]) * .5F - nearDepth) / span * .6F, .35F, 1F);
                    var edgeColor = AlphaColor(color, alphaNear);
                    drawList.AddLine(projected[index], projected[other], edgeColor, editing ? 1.6F : 1.2F);
                }
            }

            if (editing)
            {
                // 六面手柄：画在该面中心，拖它就是对这一侧缩放
                for (var axis = 0; axis < 3; axis++)
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var handle = center;
                    var offset = axis switch
                    {
                        0 => new Vector3(half.X * sign, 0F, 0F),
                        1 => new Vector3(0F, half.Y * sign, 0F),
                        _ => new Vector3(0F, 0F, half.Z * sign),
                    };
                    handle += offset;
                    if (!SceneProjection.Project(handle, origin, size, camera, out var handleScreen, out _)) continue;
                    drawList.AddRectFilled(handleScreen - new Vector2(4F), handleScreen + new Vector2(4F), color, 1F);
                    drawList.AddRect(handleScreen - new Vector2(4F), handleScreen + new Vector2(4F),
                        AlphaColor(color, .95F), 1F, ImDrawFlags.None, 1.2F);
                }

                // 中心手柄：只平移偏移，不改尺寸
                if (SceneProjection.Project(center, origin, size, camera, out var centerScreen, out _))
                {
                    drawList.AddCircleFilled(centerScreen, 5F, color);
                    drawList.AddCircle(centerScreen, 5F, AlphaColor(color, .95F), 16, 1.4F);
                }
            }

            if (SceneProjection.Project(center, origin, size, camera, out var labelScreen, out _))
                drawList.AddText(labelScreen + new Vector2(8F, -18F), AlphaColor(color, .9F),
                    editing ? "探针体积（编辑中）" : "探针体积");
        }
    }

    private static uint AlphaColor(uint packed, float alpha)
    {
        var value = (packed & 0x00FFFFFFU) | ((uint)Math.Clamp(alpha * 255F, 0F, 255F) << 24);
        return value;
    }

    /// <summary>
    /// 开始拖拽探针体积手柄
    /// </summary>
    /// <param name="mouse">按下时的鼠标位置</param>
    /// <param name="probe">探针参数</param>
    /// <param name="axis">轴向，0 到 2 表示拖面；-1 表示整体平移</param>
    /// <param name="faceSign">拖的是轴的哪一侧</param>
    /// <param name="depth">命中点的相机深度</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    private void BeginProbeGizmoDrag(Vector2 mouse, ProbeVolumeComponentData probe, int axis, int faceSign,
        float depth, Vector2 size, SceneCamera camera)
    {
        _sceneGizmoDragProbe = true;
        _sceneGizmoDragging = true;
        _sceneGizmoProbeAxis = axis;
        _sceneGizmoProbeFaceSign = faceSign;
        _sceneGizmoDragStartMouse = mouse;
        _sceneGizmoProbeStartSize = new System.Numerics.Vector3(probe.SizeX, probe.SizeY, probe.SizeZ);
        _sceneGizmoProbeStartOffset = new System.Numerics.Vector3(probe.OffsetX, probe.OffsetY, probe.OffsetZ);
        _sceneGizmoDragPixelsPerUnit =
            MathF.Max(.001F, SceneProjection.FocalLength(size, camera) / MathF.Max(1F, depth));
        _sceneGizmoPivotScreen = mouse;
    }

    private void DrawMoveGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 pivot, Vector2 pivotScreen, float unitsPerPixel, SceneObject? axisOwner = null)
    {
        var length = unitsPerPixel * 82F;
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> screen = stackalloc Vector2[4];
        for (var plane = 0; plane < 3; plane++)
        {
            // 三个面按各自所在的世界平面绘制，随相机朝向变化，而不是永远正对屏幕
            var (u, v) = AxisPlane(plane);
            var center = pivot + (u + v) * unitsPerPixel * 22F;
            var half = unitsPerPixel * 6.5F * 1.3F * 1.2F;
            corners[0] = center - u * half - v * half;
            corners[1] = center + u * half - v * half;
            corners[2] = center + u * half + v * half;
            corners[3] = center - u * half + v * half;
            var visible = true;
            for (var index = 0; index < 4; index++)
                if (!SceneProjection.Project(corners[index], origin, size, camera, out screen[index], out _))
                {
                    visible = false;
                    break;
                }

            if (!visible) continue;
            var highlighted = IsPlaneHighlighted(plane);
            var color = ToPackedColor(highlighted
                ? Color.FromArgb(235, EditorPalette.Accent)
                : Color.FromArgb(190, AxisColor(plane)));
            drawList.AddQuadFilled(screen[0], screen[1], screen[2], screen[3], color);
            drawList.AddQuad(screen[0], screen[1], screen[2], screen[3],
                ToPackedColor(highlighted ? EditorPalette.Text : EditorPalette.Border), highlighted ? 1.8F : 1F);
        }
        for (var axisIndex = 0; axisIndex < 3; axisIndex++)
        {
            var axis = axisOwner is null ? AxisVector(axisIndex) : GizmoAxisFor(axisOwner, axisIndex);
            if (!TryProjectAxis(origin, size, camera, pivot, axis, out var end, out _)) continue;
            var highlighted = axisOwner is null ? false : IsAxisHighlighted(axisIndex);
            var color = ToPackedColor(highlighted
                ? Color.FromArgb(255, EditorPalette.Accent)
                : AxisColor(axisIndex));
            drawList.AddLine(pivotScreen, end, color, highlighted ? 3.6F : 2.4F);
            var direction = Vector2.Normalize(end - pivotScreen);
            var normal = new Vector2(-direction.Y, direction.X);
            var tip = end;
            drawList.AddTriangleFilled(tip,
                tip - direction * 12F + normal * 5F,
                tip - direction * 12F - normal * 5F, color);
            drawList.AddText(end + normal * 8F, color,
                axisIndex switch { 0 => "X", 1 => "Y", _ => "Z" });
        }
    }

    private void DrawRotateGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 pivot, float unitsPerPixel)
    {
        var radius = unitsPerPixel * 58F;
        for (var axisIndex = 0; axisIndex < 3; axisIndex++)
        {
            var highlighted = IsRotateHighlighted(axisIndex);
            var color = ToPackedColor(highlighted
                ? Color.FromArgb(255, EditorPalette.Accent)
                : AxisColor(axisIndex));
            var (u, v) = AxisPlane(axisIndex);
            DrawWorldCircle(drawList, origin, size, camera, pivot, u, v, radius, color, highlighted ? 3.4F : 2F);
        }
    }

    private void DrawScaleGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject sceneObject, Vector3 pivot, Vector2 pivotScreen)
    {
        var handle = pivotScreen + new Vector2(0F, -72F);
        var highlighted = IsScaleHighlighted();
        var color = ToPackedColor(highlighted ? Color.FromArgb(255, EditorPalette.Text) : EditorPalette.Accent);
        drawList.AddLine(pivotScreen, handle, color, highlighted ? 3F : 2F);
        drawList.AddRectFilled(handle - new Vector2(highlighted ? 7F : 5F), handle + new Vector2(highlighted ? 7F : 5F), color);
        var data = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel) is { } model
            ? SceneComponentTypes.ReadVoxelModel(model)
            : default;
        var label = sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel)
            ? $"{data.ScaleX:0.##} · {data.ScaleY:0.##} · {data.ScaleZ:0.##}"
            : $"{sceneObject.Transform.ScaleX} · {sceneObject.Transform.ScaleY} · {sceneObject.Transform.ScaleZ}";
        drawList.AddText(handle + new Vector2(10F, -6F), color, label);
    }

    private static Vector3 AxisVector(int axisIndex) =>
        axisIndex switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    private static (Vector3 U, Vector3 V) AxisPlane(int axisIndex) =>
        axisIndex switch
        {
            0 => (Vector3.UnitY, Vector3.UnitZ),
            1 => (Vector3.UnitZ, Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };

    /// <summary>吸附步长：等比联动时三轴共用 X 的步长</summary>
    private float GizmoSnapStep(int axisIndex)
    {
        var value = axisIndex switch
        {
            0 => _editorSettings.MoveSnapX,
            1 => _editorSettings.MoveSnapLinked ? _editorSettings.MoveSnapX : _editorSettings.MoveSnapY,
            _ => _editorSettings.MoveSnapLinked ? _editorSettings.MoveSnapX : _editorSettings.MoveSnapZ,
        };
        return float.IsFinite(value) && value > 0.0001F ? value : 1F;
    }

    private static float SnapComponent(float value, float step) => MathF.Round(value / step) * step;

    /// <summary>旋转吸附步长：联动时三轴共用 X</summary>
    private float RotationSnapStep(int axisIndex)
    {
        var value = axisIndex switch
        {
            0 => _editorSettings.RotationSnapX,
            1 => _editorSettings.RotationSnapLinked ? _editorSettings.RotationSnapX : _editorSettings.RotationSnapY,
            _ => _editorSettings.RotationSnapLinked ? _editorSettings.RotationSnapX : _editorSettings.RotationSnapZ,
        };
        return float.IsFinite(value) && value > 0.001F ? value : 15F;
    }

    /// <summary>缩放吸附步长：联动时三轴共用 X</summary>
    private float ScaleSnapStep(int axisIndex)
    {
        var value = axisIndex switch
        {
            0 => _editorSettings.ScaleSnapX,
            1 => _editorSettings.ScaleSnapLinked ? _editorSettings.ScaleSnapX : _editorSettings.ScaleSnapY,
            _ => _editorSettings.ScaleSnapLinked ? _editorSettings.ScaleSnapX : _editorSettings.ScaleSnapZ,
        };
        return float.IsFinite(value) && value > 0.001F ? value : 0.1F;
    }

    /// <summary>把位移的三个分量分别吸附到步长</summary>
    private Vector3 SnapGizmoDelta(Vector3 delta) => new(
        SnapComponent(delta.X, GizmoSnapStep(0)),
        SnapComponent(delta.Y, GizmoSnapStep(1)),
        SnapComponent(delta.Z, GizmoSnapStep(2)));

    /// <summary>当前 Gizmo 空间下的轴方向：局部空间按对象旋转换算</summary>
    private Vector3 GizmoAxisFor(SceneObject sceneObject, int axisIndex)
    {
        var axis = AxisVector(axisIndex);
        if (!_editorSettings.GizmoLocalSpace) return axis;
        System.Numerics.Matrix4x4 matrix;
        if (sceneObject.Transform.Mode == TransformMode.Free)
        {
            matrix = System.Numerics.Matrix4x4.CreateFromQuaternion(sceneObject.Transform.Free.Rotation);
        }
        else
        {
            var rotation = sceneObject.Transform.Grid.Rotation;
            matrix = new System.Numerics.Matrix4x4(
                rotation.M00, rotation.M01, rotation.M02, 0F,
                rotation.M10, rotation.M11, rotation.M12, 0F,
                rotation.M20, rotation.M21, rotation.M22, 0F,
                0F, 0F, 0F, 1F);
        }

        var transformed = System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(axis.X, axis.Y, axis.Z), matrix);
        return new Vector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static Color AxisColor(int axisIndex) =>
        axisIndex switch { 0 => AxisXColor, 1 => AxisYColor, _ => AxisZColor };

    private bool TryProjectAxis(Vector2 origin, Vector2 size, SceneCamera camera, Vector3 pivot, Vector3 axis,
        out Vector2 end, out float pixelsPerUnit)
    {
        end = default;
        pixelsPerUnit = 0F;
        if (!SceneProjection.Project(pivot, origin, size, camera, out var start, out _)) return false;
        if (!SceneProjection.Project(pivot + axis, origin, size, camera, out var unitEnd, out _)) return false;
        pixelsPerUnit = Vector2.Distance(start, unitEnd);
        if (pixelsPerUnit < 0.5F) return false;
        end = start + Vector2.Normalize(unitEnd - start) * 82F;
        return true;
    }

    private void DrawWorldLine(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 start, Vector3 end, uint color, float width)
    {
        if (SceneProjection.ClipLine(start, end, origin, size, camera, out var a, out var b))
            drawList.AddLine(a, b, color, width);
    }

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 center, Vector3 u, Vector3 v, float radius, uint color, float width)
    {
        const int segments = 32;
        for (var index = 0; index < segments; index++)
        {
            var angle0 = MathF.Tau * index / segments;
            var angle1 = MathF.Tau * (index + 1) / segments;
            var a = center + u * (MathF.Cos(angle0) * radius) + v * (MathF.Sin(angle0) * radius);
            var b = center + u * (MathF.Cos(angle1) * radius) + v * (MathF.Sin(angle1) * radius);
            DrawWorldLine(drawList, origin, size, camera, a, b, color, width);
        }
    }

    /// <summary>
    /// 处理场景 Gizmo 与光照对象的点击。返回 true 时调用方不再处理体素绘制。
    /// </summary>
    private bool HandleSceneGizmoInput(Vector2 origin, Vector2 size, SceneCamera camera, bool orientationGizmoHovered)
    {
        if (_historyConfirmation is not null) return false;
        if (_project is null || _sceneDocument is null) return false;
        var mouse = ImGui.GetIO().MousePos;
        if (_curveGizmoDragging)
        {
            UpdateCurveGizmoDrag(origin, size, camera);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndCurveGizmoDrag();
            return true;
        }

        if (_sceneGizmoDragging)
        {
            UpdateSceneGizmoDrag(origin, size, camera);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndSceneGizmoDrag();
            return true;
        }

        if (!ImGui.IsMouseHoveringRect(origin, origin + size) || orientationGizmoHovered) return false;
        if (HandleCurveDeleteKey()) return true;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return false;

        var selected = GetSelectedSceneObject();
        // 光照范围手柄和灯体优先命中，确保任何 Gizmo 模式下都能选中灯光
        if (selected is not null && TryGetSelectedLightRangeHandle(origin, size, camera, selected, out var handleScreen) &&
            Vector2.Distance(mouse, handleScreen) <= 10F)
        {
            var light = selected.Components.FirstOrDefault(SceneComponentTypes.IsLight);
            if (light is not null)
            {
                var data = SceneComponentTypes.ReadLight(light);
                _sceneGizmoDragStartMouse = mouse;
                _sceneGizmoDragStartValue = data.Range;
                _sceneGizmoDragRange = true;
                _sceneGizmoDragging = true;
                SceneProjection.Project(GetObjectWorldPosition(selected), origin, size, camera, out _, out var rangeDepth);
                _sceneGizmoDragPixelsPerUnit = MathF.Max(0.001F,
                    SceneProjection.FocalLength(size, camera) / MathF.Max(1F, rangeDepth));
                return true;
            }
        }

        if (HandleCurveGizmoInput(mouse, origin, size, camera)) return true;

        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (_voxelEditTargetId is not null && _activeTool == EditorTool.Draw) break;
            if (IsSceneSelectionLockedAgainst(sceneObject.Id) || !IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex) ||
                sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel)) continue;
            var markers = sceneObject.Components.Where(component => component.Enabled &&
                (SceneComponentTypes.IsLight(component) || SceneComponentTypes.IsCamera(component))).ToArray();
            if (markers.Length == 0 && sceneObject.Components.Any(component => SceneComponentTypes.IsLight(component) || SceneComponentTypes.IsCamera(component))) continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera,
                    out var screen, out _) || Vector2.Distance(mouse, screen) > 12F) continue;
            _selectedSceneObjectId = sceneObject.Id;
            _revealHierarchySelection = sceneObject.Id;
            _hierarchySearch = string.Empty;
            _activeTool = EditorTool.Pick;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) FocusHierarchyObject(sceneObject);
            return true;
        }

        if (selected is null || !IsSceneObjectVisible(selected) || !_sceneDocument.IsLayerVisible(selected.LayerIndex)) return false;
        var probeComponent = selected.Components.FirstOrDefault(SceneComponentTypes.IsProbeVolume);
        if (probeComponent is not null && SceneComponentTypes.ReadProbeVolume(probeComponent).Editing)
        {
            var probe = SceneComponentTypes.ReadProbeVolume(probeComponent);
            var probePivot = GetObjectPivot(selected);
            var probeCenter = probePivot + new Vector3(probe.OffsetX, probe.OffsetY, probe.OffsetZ);
            var half = new Vector3(probe.SizeX * .5F, probe.SizeY * .5F, probe.SizeZ * .5F);
            // 命中顺序：中心手柄优先，其次六个面手柄
            if (SceneProjection.Project(probeCenter, origin, size, camera, out var centerScreen, out var centerDepth) &&
                Vector2.Distance(mouse, centerScreen) < 10F)
            {
                BeginProbeGizmoDrag(mouse, probe, -1, 1, centerDepth, size, camera);
                return true;
            }

            for (var axis = 0; axis < 3; axis++)
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var handle = probeCenter + (axis switch
                {
                    0 => new Vector3(half.X * sign, 0F, 0F),
                    1 => new Vector3(0F, half.Y * sign, 0F),
                    _ => new Vector3(0F, 0F, half.Z * sign),
                });
                if (!SceneProjection.Project(handle, origin, size, camera, out var faceHandleScreen, out var handleDepth)) continue;
                if (Vector2.Distance(mouse, faceHandleScreen) >= 9F) continue;
                BeginProbeGizmoDrag(mouse, probe, axis, sign, handleDepth, size, camera);
                return true;
            }
        }
        if (TryBeginVoxelSelectionGizmoDrag(mouse, origin, size, camera, selected)) return true;
        // 曲线编辑时手柄属于当前控制点，对象级手柄既不画也不该抢输入
        if (IsCurveEditing(selected)) return false;
        var pivot = GetGizmoPivot(selected);
        if (!SceneProjection.Project(pivot, origin, size, camera, out var pivotScreen, out var depth)) return false;
        var unitsPerPixel = MathF.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        if (_sceneGizmoMode == SceneGizmoMode.All)
        {
            foreach (var mode in new[] { SceneGizmoMode.Move, SceneGizmoMode.Rotate, SceneGizmoMode.Scale })
            {
                if (TryBeginTransformGizmoDrag(mode, mouse, origin, size, camera, selected, pivot, pivotScreen,
                        unitsPerPixel)) return true;
            }

            return false;
        }

        return TryBeginTransformGizmoDrag(_sceneGizmoMode, mouse, origin, size, camera, selected, pivot,
            pivotScreen, unitsPerPixel);
    }

    /// <summary>
    /// 按指定变换模式尝试开始对象手柄拖动；“全部”工具依次尝试移动、旋转、缩放
    /// </summary>
    private bool TryBeginTransformGizmoDrag(SceneGizmoMode mode, Vector2 mouse, Vector2 origin, Vector2 size,
        SceneCamera camera, SceneObject selected, Vector3 pivot, Vector2 pivotScreen, float unitsPerPixel)
    {
        switch (mode)
        {
            case SceneGizmoMode.Move:
            {
                // 与高亮使用同一套判定：取距离最近的平面，避免“看到蓝色按下去却拖到绿色”
                var bestPlane = -1;
                var bestPlaneDistance = float.MaxValue;
                var bestU = Vector2.Zero;
                var bestV = Vector2.Zero;
                for (var plane = 0; plane < 3; plane++)
                {
                    var (u, v) = AxisPlane(plane);
                    if (!SceneProjection.Project(pivot + (u + v) * unitsPerPixel * 22F, origin, size, camera,
                            out var point, out _))
                        continue;
                    var distance = Vector2.Distance(mouse, point);
                    if (distance > 6.5F * 1.3F * 1.2F * 1.7F || distance >= bestPlaneDistance) continue;
                    if (!SceneProjection.Project(pivot + u, origin, size, camera, out var pu, out _) ||
                        !SceneProjection.Project(pivot + v, origin, size, camera, out var pv, out _)) continue;
                    var su = pu - pivotScreen;
                    var sv = pv - pivotScreen;
                    if (MathF.Abs(su.X * sv.Y - su.Y * sv.X) < .0001F) continue;
                    bestPlane = plane;
                    bestPlaneDistance = distance;
                    bestU = su;
                    bestV = sv;
                }

                if (bestPlane >= 0)
                {
                    _sceneGizmoDragPlane = bestPlane;
                    _sceneGizmoPlaneU = bestU;
                    _sceneGizmoPlaneV = bestV;
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoDragStartPosition = pivot;
                    _sceneGizmoDragStartObjectPosition = GetObjectPivot(selected);
                    BeginGridDragPreview(selected);
                    _sceneGizmoDragging = true;
                    _sceneGizmoDragRotation = false;
                    return true;
                }
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    var gizmoAxis = GizmoAxisFor(selected, axisIndex);
                    if (!TryProjectAxis(origin, size, camera, pivot, gizmoAxis,
                            out var end, out var pixelsPerUnit)) continue;
                    if (DistanceToSegment(mouse, pivotScreen, end) > 9F) continue;
                    _sceneGizmoDragPlane = -1;
                    _sceneGizmoDragRotation = false;
                    _sceneGizmoDragAxis = axisIndex;
                    _sceneGizmoDragAxisWorld = gizmoAxis;
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoDragStartPosition = pivot;
                    _sceneGizmoDragStartObjectPosition = GetObjectPivot(selected);
                    _sceneGizmoDragStartGrid = selected.Transform.Grid.Position;
                    BeginGridDragPreview(selected);
                    _sceneGizmoDragPixelsPerUnit = MathF.Max(0.001F, pixelsPerUnit);
                    _sceneGizmoDragScreenDirection = Vector2.Normalize(end - pivotScreen);
                    _sceneGizmoDragging = true;
                    _sceneGizmoDragRange = false;
                    _sceneGizmoDragScale = false;
                    return true;
                }

                return false;
            }
            case SceneGizmoMode.Rotate:
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    var (u, v) = AxisPlane(axisIndex);
                    var radius = unitsPerPixel * 58F;
                    var bestDistance = float.MaxValue;
                    for (var index = 0; index < 32; index++)
                    {
                        var angle = MathF.Tau * index / 32F;
                        var point = pivot + u * (MathF.Cos(angle) * radius) + v * (MathF.Sin(angle) * radius);
                        if (!SceneProjection.Project(point, origin, size, camera, out var screen, out _)) continue;
                        bestDistance = MathF.Min(bestDistance, Vector2.Distance(mouse, screen));
                    }

                    if (bestDistance > 10F) continue;
                    _sceneGizmoDragAxis = axisIndex;
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoPivotScreen = pivotScreen;
                    _sceneGizmoDragStartPosition = pivot;
                    _sceneGizmoDragStartObjectPosition = GetObjectPivot(selected);
                    _sceneGizmoStartAngle = MathF.Atan2(mouse.Y - pivotScreen.Y, mouse.X - pivotScreen.X);
                    _sceneGizmoStartRotation = selected.Transform.Free.Rotation;
                    _sceneGizmoRotationDelta = 0F;
                    _sceneGizmoDragRotation = true;
                    _sceneGizmoDragging = true;
                    return true;
                }

                return false;
            case SceneGizmoMode.Scale:
                if (Vector2.Distance(mouse, pivotScreen + new Vector2(0F, -72F)) <= 12F)
                {
                    var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoDragStartValue = component is null ? 1F : SceneComponentTypes.ReadVoxelModel(component).ScaleX;
                    _sceneGizmoDragScale = true;
                    _sceneGizmoDragRange = false;
                    _sceneGizmoDragging = true;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private void UpdateSceneGizmoDrag(Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var selected = GetSelectedSceneObject();
        if (selected is null) return;
        var mouse = ImGui.GetIO().MousePos;
        if (UpdateVoxelSelectionGizmoDrag(origin, size, camera, selected)) return;
        if (_sceneGizmoDragProbe)
        {
            var probeComponent = selected.Components.FirstOrDefault(SceneComponentTypes.IsProbeVolume);
            if (probeComponent is null) return;
            var probe = SceneComponentTypes.ReadProbeVolume(probeComponent);
            var mouseDelta = mouse - _sceneGizmoDragStartMouse;
            if (_sceneGizmoProbeAxis < 0)
            {
                // 中心手柄：只在相机平面内平移，不改尺寸
                var shiftX = mouseDelta.X / _sceneGizmoDragPixelsPerUnit;
                var shiftY = -mouseDelta.Y / _sceneGizmoDragPixelsPerUnit;
                var offset = new Vector3(_sceneGizmoProbeStartOffset.X, _sceneGizmoProbeStartOffset.Y, _sceneGizmoProbeStartOffset.Z) + camera.Right * shiftX + camera.Up * shiftY;
                SceneComponentTypes.WriteProbeVolume(probeComponent, probe with
                {
                    OffsetX = offset.X,
                    OffsetY = offset.Y,
                    OffsetZ = offset.Z,
                });
            }
            else
            {
                var axis = _sceneGizmoProbeAxis;
                var axisUnit = new Vector3(
                    axis == 0 ? 1F : 0F, axis == 1 ? 1F : 0F, axis == 2 ? 1F : 0F);
                var pivot = GetObjectPivot(selected);
                var startCenter = pivot + new Vector3(_sceneGizmoProbeStartOffset.X, _sceneGizmoProbeStartOffset.Y, _sceneGizmoProbeStartOffset.Z);
                var startSize = axis switch
                {
                    0 => _sceneGizmoProbeStartSize.X,
                    1 => _sceneGizmoProbeStartSize.Y,
                    _ => _sceneGizmoProbeStartSize.Z,
                };
                var facePoint = startCenter + axisUnit * (startSize * .5F * _sceneGizmoProbeFaceSign);
                if (!SceneProjection.Project(facePoint, origin, size, camera, out var faceScreen, out _) ||
                    !SceneProjection.Project(facePoint + axisUnit, origin, size, camera, out var faceNext, out _))
                {
                    return;
                }

                // 把世界单位换算成屏幕上沿该轴的像素长度，再把手柄位移投影回世界单位
                var axisScreen = faceNext - faceScreen;
                var pixelsPerUnit = axisScreen.Length();
                if (pixelsPerUnit < .001F) return;
                var worldDelta = Vector2.Dot(mouseDelta, axisScreen / pixelsPerUnit) / pixelsPerUnit;
                var newSize = MathF.Max(.1F, startSize + worldDelta * _sceneGizmoProbeFaceSign);
                var newOffset = _sceneGizmoProbeStartOffset;
                if (axis == 0) newOffset.X += worldDelta * .5F;
                else if (axis == 1) newOffset.Y += worldDelta * .5F;
                else newOffset.Z += worldDelta * .5F;
                SceneComponentTypes.WriteProbeVolume(probeComponent, probe with
                {
                    OffsetX = newOffset.X,
                    OffsetY = newOffset.Y,
                    OffsetZ = newOffset.Z,
                    SizeX = axis == 0 ? newSize : probe.SizeX,
                    SizeY = axis == 1 ? newSize : probe.SizeY,
                    SizeZ = axis == 2 ? newSize : probe.SizeZ,
                });
            }

            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            return;
        }
        if (_sceneGizmoDragRotation)
        {
            var angle = MathF.Atan2(mouse.Y - _sceneGizmoPivotScreen.Y, mouse.X - _sceneGizmoPivotScreen.X);
            _sceneGizmoRotationDelta = MathF.Atan2(MathF.Sin(angle - _sceneGizmoStartAngle), MathF.Cos(angle - _sceneGizmoStartAngle));
            if (_editorSettings.RotationSnapEnabled)
            {
                var step = RotationSnapStep(_sceneGizmoDragAxis) * (MathF.PI / 180F);
                _sceneGizmoRotationDelta = MathF.Round(_sceneGizmoRotationDelta / step) * step;
            }
            if (selected.Transform.Mode == TransformMode.Free)
            {
                var axis = AxisVector(_sceneGizmoDragAxis);
                var delta = Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(axis.X, axis.Y, axis.Z), _sceneGizmoRotationDelta);
                var rotation = Quaternion.Normalize(delta * _sceneGizmoStartRotation);
                if (_editorSettings.GizmoPivotCenter)
                {
                    // 中心枢轴旋转时让包围盒中心保持不动，等价于绕中心转
                    var pivot = _sceneGizmoDragStartPosition;
                    var offset = new System.Numerics.Vector3(
                        pivot.X - _sceneGizmoDragStartObjectPosition.X,
                        pivot.Y - _sceneGizmoDragStartObjectPosition.Y,
                        pivot.Z - _sceneGizmoDragStartObjectPosition.Z);
                    var rotated = System.Numerics.Vector3.Transform(offset, delta);
                    var position = pivot - new Vector3(rotated.X, rotated.Y, rotated.Z);
                    selected.Transform.SetFree(new FreeTransform(
                        new Double3(position.X, position.Y, position.Z), rotation));
                }
                else
                {
                    selected.Transform.SetFree(new FreeTransform(selected.Transform.Free.Position, rotation));
                }
                UpdateTransformMarker(selected); _pipeline?.ResetAccumulation();
            }
            return;
        }
        if (selected.Transform.Mode == TransformMode.Grid && _sceneGizmoGridDragStartWorld is not null)
        {
            // 网格对象拖动时实时预览：直接把对象体素副本按整格位移摆到位，松手时才记一次撤销
            UpdateGridDragPreview(selected, mouse, origin, size, camera);
            return;
        }
        if (_sceneGizmoDragPlane >= 0)
        {
            if (selected.Transform.Mode == TransformMode.Free)
            {
                var delta = SnapGizmoDelta(PlaneDragDelta(mouse));
                var position = _sceneGizmoDragStartObjectPosition + delta;
                selected.Transform.SetFree(new FreeTransform(new Double3(position.X, position.Y, position.Z), selected.Transform.Free.Rotation));
                UpdateTransformMarker(selected); _pipeline?.ResetAccumulation();
            }
            return;
        }
        if (_sceneGizmoDragRange)
        {
            var light = selected.Components.FirstOrDefault(SceneComponentTypes.IsLight);
            if (light is null) return;
            var data = SceneComponentTypes.ReadLight(light);
            var range = Math.Clamp(
                _sceneGizmoDragStartValue + (mouse.X - _sceneGizmoDragStartMouse.X) / _sceneGizmoDragPixelsPerUnit,
                0.01F, 100000F);
            SceneComponentTypes.WriteLight(light, data with { Range = range });
            _pipeline?.ResetAccumulation();
            return;
        }

        if (_sceneGizmoDragScale)
        {
            var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) return;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var factor = Math.Clamp(_sceneGizmoDragStartValue *
                MathF.Pow(2F, -(mouse.Y - _sceneGizmoDragStartMouse.Y) / 80F),
                ModelScaleRule.Minimum, ModelScaleRule.Maximum);
            // 预览值按正式规则吸附：放大只能取整数倍，缩小按 0.05 一档
            factor = factor > 1F ? MathF.Round(factor) : MathF.Round(factor * 20F) / 20F;
            if (_editorSettings.ScaleSnapEnabled)
            {
                var step = ScaleSnapStep(0);
                factor = MathF.Max(step, MathF.Round(factor / step) * step);
            }
            var ratio = factor / MathF.Max(ModelScaleRule.Minimum, _sceneGizmoDragStartValue);
            SceneComponentTypes.WriteVoxelModel(component, data with
            {
                ScaleX = data.ScaleX * ratio,
                ScaleY = data.ScaleY * ratio,
                ScaleZ = data.ScaleZ * ratio,
            });
            return;
        }

        if (selected.Transform.Mode == TransformMode.Free)
        {
            var axis = _sceneGizmoDragAxisWorld.LengthSquared > 0.0001F
                ? _sceneGizmoDragAxisWorld : AxisVector(_sceneGizmoDragAxis);
            if (!SceneProjection.Project(_sceneGizmoDragStartPosition, origin, size, camera, out var start, out _) ||
                !SceneProjection.Project(_sceneGizmoDragStartPosition + axis, origin, size, camera, out var unit, out _))
                return;
            var direction = unit - start;
            if (direction.LengthSquared() < 0.0001F) return;
            var pixelsPerUnit = direction.Length();
            direction /= pixelsPerUnit;
            var deltaPixels = Vector2.Dot(mouse - _sceneGizmoDragStartMouse, direction);
            var step = GizmoSnapStep(_sceneGizmoDragAxis);
            var units = SnapComponent(deltaPixels / pixelsPerUnit, step);
            var position = _sceneGizmoDragStartObjectPosition + axis * units;
            var rotation = selected.Transform.Free.Rotation;
            selected.Transform.SetFree(new FreeTransform(
                new Double3(position.X, position.Y, position.Z), rotation));
            UpdateTransformMarker(selected);
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }
    }

    /// <summary>
    /// 开始网格拖动预览：记住起始体素世界与格变换
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private void BeginGridDragPreview(SceneObject sceneObject)
    {
        _sceneGizmoGridDragCells = VoxelCoord64.Zero;
        _sceneGizmoGridDragStartTransform = sceneObject.Transform.Grid;
        _sceneGizmoGridDragStartWorld = sceneObject.Transform.Mode == TransformMode.Grid
            ? _objectVoxels.GetOrCreate(sceneObject.Id).Clone()
            : null;
        _sceneGizmoGridDragObjectId = sceneObject.Transform.Mode == TransformMode.Grid ? sceneObject.Id : null;
        PrepareGridDragInstanceWorlds(sceneObject);
    }

    /// <summary>
    /// 准备拖动实例的静态世界与对象世界：静态世界排除被拖对象，拖动时只更新实例平移，
    /// 不再每步重新合成整份世界
    /// </summary>
    private void PrepareGridDragInstanceWorlds(SceneObject sceneObject)
    {
        _dragInstanceObjectId = StableId.Empty;
        _dragInstanceStaticWorld = null;
        _dragInstanceObjectWorld = null;
        if (_sceneDocument is null || _scenePipeline?.SupportsDragPreviewInstance != true) return;
        if (!_objectVoxels.TryGetWorld(sceneObject.Id, out var source)) return;
        // 对象放不进加速结构时退回整份世界合成，避免拖动预览反而让对象消失
        if (!_scenePipeline.CanUseDragPreview(source.OccupiedVoxelCount)) return;
        RefreshRuntimeMaterialTable();
        // 对象世界保留世界坐标并套用与场景工作集相同的运行时材质重映射，
        // 这样硬件实例命中的颜色和材质槽与主场景一致
        var objectWorld = new VoxelWorld();
        var writer = objectWorld.CreateBulkWriter();
        foreach (var (coordinate, value) in source.EnumerateOccupied())
            writer.Set(coordinate, RemapRuntimeMaterial(sceneObject.Id, value));
        _dragInstanceObjectWorld = objectWorld;
        _dragInstanceStaticWorld = _objectVoxels.Compose(_sceneDocument, uint.MaxValue, true,
            remap: RemapRuntimeMaterial, excludeObjectId: sceneObject.Id);
        _dragInstanceObjectId = sceneObject.Id;
    }

    private void UpdateGridDragPreview(SceneObject sceneObject, Vector2 mouse, Vector2 origin, Vector2 size,
        SceneCamera camera)
    {
        if (_sceneGizmoGridDragStartWorld is null) return;
        var cells = ComputeGridDragCells(mouse, origin, size, camera);
        if (cells == _sceneGizmoGridDragCells) return;
        _sceneGizmoGridDragCells = cells;
        // 拖动中不复制体素世界：合成渲染世界时按当前平移量给目标对象叠加位移，
        // 每步只付一次合成开销，松手时再真正写入体素并记录一步撤销
        sceneObject.Transform.SetGrid(new GridTransform(
            _sceneGizmoGridDragStartTransform.Position + cells, _sceneGizmoGridDragStartTransform.Rotation));
        UpdateTransformMarker(sceneObject);
        MarkSceneDocumentDirty();
        InvalidateComposedWorldCache();
        _pickVersion++;
        _pipeline?.ResetAccumulation();
        SetStatus($"移动中：{cells.X}, {cells.Y}, {cells.Z}");
    }

    private VoxelCoord64 ComputeGridDragCells(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_sceneGizmoDragPlane >= 0)
        {
            // 平面拖动：把屏幕位移换成世界位移，再按格截断
            var delta = PlaneDragDelta(mouse);
            // 平面拖动拿到的已经是世界位移，按格截断（不做四舍五入）
            return new VoxelCoord64(
                (long)(SnapComponent(delta.X, GizmoSnapStep(0))),
                (long)(SnapComponent(delta.Y, GizmoSnapStep(1))),
                (long)(SnapComponent(delta.Z, GizmoSnapStep(2))));
        }

        var axis = _sceneGizmoDragAxisWorld.LengthSquared > 0.0001F
            ? _sceneGizmoDragAxisWorld
            : AxisVector(_sceneGizmoDragAxis);
        if (!SceneProjection.Project(_sceneGizmoDragStartPosition, origin, size, camera, out var start, out _) ||
            !SceneProjection.Project(_sceneGizmoDragStartPosition + axis, origin, size, camera, out var unit, out _))
            return _sceneGizmoGridDragCells;
        var direction = unit - start;
        if (direction.LengthSquared() < 0.0001F) return _sceneGizmoGridDragCells;
        var pixelsPerUnit = direction.Length();
        var units = Vector2.Dot(mouse - _sceneGizmoDragStartMouse, direction / pixelsPerUnit) / pixelsPerUnit;
        var snapped = SnapComponent(units, GizmoSnapStep(_sceneGizmoDragAxis));
        return new VoxelCoord64(
            (long)(axis.X * snapped), (long)(axis.Y * snapped), (long)(axis.Z * snapped));
    }

    /// <summary>
    /// 结束网格拖动：恢复起始状态后走一次正式移动，撤销记录只有一步
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private void FinishGridDragPreview(SceneObject sceneObject)
    {
        var cells = _sceneGizmoGridDragCells;
        var startWorld = _sceneGizmoGridDragStartWorld;
        var startTransform = _sceneGizmoGridDragStartTransform;
        _sceneGizmoGridDragStartWorld = null;
        _sceneGizmoGridDragCells = VoxelCoord64.Zero;
        // 先结束合成位移与拖动实例，之后的渲染回到真实体素位置
        _sceneGizmoGridDragObjectId = null;
        _dragInstanceStaticWorld = null;
        _dragInstanceObjectWorld = null;
        _dragInstanceObjectId = StableId.Empty;
        if (startWorld is null) return;
        if (cells == VoxelCoord64.Zero)
        {
            // 没有移动：只把预览用的变换还原，体素从未改动
            sceneObject.Transform.SetGrid(startTransform);
            UpdateTransformMarker(sceneObject);
            InvalidateComposedWorldCache();
            _pickVersion++;
            return;
        }

        sceneObject.Transform.SetGrid(startTransform);
        UpdateTransformMarker(sceneObject);
        MoveModelObjectByGrid(sceneObject, cells);
    }

    private void EndSceneGizmoDrag()
    {
        var selected = GetSelectedSceneObject();
        _sceneGizmoDragging = false;
        _voxelSelectionGizmoDragging = false;
        if (selected is null)
        {
            _sceneGizmoDragRange = false;
            _sceneGizmoDragScale = false;
            return;
        }

        var mouse = ImGui.GetIO().MousePos;
        if (_sceneGizmoDragRotation)
        {
            _sceneGizmoDragRotation = false;
            if (selected.Transform.Mode == TransformMode.Grid)
            {
                // 体素网格上的旋转是重新采样生成的：按拖动角度对基准重采样，不再只取 90 度整数倍
                var radians = _sceneGizmoRotationDelta;
                if (_editorSettings.RotationSnapEnabled)
                {
                    var step = RotationSnapStep(_sceneGizmoDragAxis) * (MathF.PI / 180F);
                    radians = MathF.Round(radians / step) * step;
                }

                if (MathF.Abs(radians) > .0001F) ApplyModelRotationDelta(selected, _sceneGizmoDragAxis, radians);
            }
            else if (MathF.Abs(_sceneGizmoRotationDelta) > .0001F) MarkSceneDocumentDirty();
            return;
        }
        if (_sceneGizmoDragPlane >= 0)
        {
            var delta = PlaneDragDelta(mouse);
            _sceneGizmoDragPlane = -1;
            if (selected.Transform.Mode == TransformMode.Grid) FinishGridDragPreview(selected);
            else MarkSceneDocumentDirty();
            return;
        }
        if (_sceneGizmoDragRange)
        {
            _sceneGizmoDragRange = false;
            MarkSceneDocumentDirty();
            SetStatus($"光照范围已调整为 {SceneComponentTypes.ReadLight(
                selected.Components.First(SceneComponentTypes.IsLight)).Range:0.##}");
            return;
        }

        if (_sceneGizmoDragScale)
        {
            _sceneGizmoDragScale = false;
            var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null)
            {
                var multiplier = MathF.Pow(2F, -(mouse.Y - _sceneGizmoDragStartMouse.Y) / 80F);
                var before = SceneSerializer.CloneObject(selected);
                var snapScale = _editorSettings.ScaleSnapEnabled;
                var scaleX = MathF.Max(0F, selected.Transform.ScaleX * multiplier);
                var scaleY = MathF.Max(0F, selected.Transform.ScaleY * multiplier);
                var scaleZ = MathF.Max(0F, selected.Transform.ScaleZ * multiplier);
                if (snapScale)
                {
                    var stepX = ScaleSnapStep(0);
                    var stepY = ScaleSnapStep(1);
                    var stepZ = ScaleSnapStep(2);
                    scaleX = MathF.Round(scaleX / stepX) * stepX;
                    scaleY = MathF.Round(scaleY / stepY) * stepY;
                    scaleZ = MathF.Round(scaleZ / stepZ) * stepZ;
                }

                selected.Transform.ScaleX = scaleX;
                selected.Transform.ScaleY = scaleY;
                selected.Transform.ScaleZ = scaleZ;
                var after = SceneSerializer.CloneObject(selected);
                if (!SceneSerializer.ObjectsEqual(before, after))
                {
                    _voxelHistory.RecordExecuted(new SceneObjectStateCommand(selected, before, after,
                        () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
                    MarkSceneDocumentDirty();
                }
                return;
            }
            var start = MathF.Max(ModelScaleRule.Minimum, _sceneGizmoDragStartValue);
            // 拖动期间写进组件的是预览值，这里先还原，再按缩放规则正式应用一次
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var previewRatio = data.ScaleX / MathF.Max(ModelScaleRule.Minimum, start);
            var restored = data with
            {
                ScaleX = data.ScaleX / previewRatio,
                ScaleY = data.ScaleY / previewRatio,
                ScaleZ = data.ScaleZ / previewRatio,
            };
            SceneComponentTypes.WriteVoxelModel(component, restored);
            var target = start * MathF.Pow(2F, -(mouse.Y - _sceneGizmoDragStartMouse.Y) / 80F);
            if (MathF.Abs(target - start) <= 0.01F) return;
            var ratio = target / start;
            ApplyModelScale(selected, restored.ScaleX * ratio, restored.ScaleY * ratio, restored.ScaleZ * ratio);
            return;
        }

        if (selected.Transform.Mode == TransformMode.Grid) FinishGridDragPreview(selected);
        else
        {
            MarkSceneDocumentDirty();
        }
    }

    private Vector3 PlaneDragDelta(Vector2 mouse)
    {
        var d = mouse - _sceneGizmoDragStartMouse;
        var u = _sceneGizmoPlaneU; var v = _sceneGizmoPlaneV;
        var determinant = u.X * v.Y - u.Y * v.X;
        if (MathF.Abs(determinant) < .0001F) return Vector3.Zero;
        var (axisU, axisV) = AxisPlane(_sceneGizmoDragPlane);
        return axisU * ((d.X * v.Y - d.Y * v.X) / determinant) + axisV * ((u.X * d.Y - u.Y * d.X) / determinant);
    }

    /// <summary>绕局部 X 轴对应的世界轴镜像对象体素，保留材质并支持撤销。</summary>
    private void MirrorModelLocalX(SceneObject sceneObject, ComponentRecord component, bool mirrored)
    {
        if (_worldLoadFailed)
        {
            SetStatus("场景体素尚未安全加载，不能镜像模型");
            return;
        }
        if (sceneObject.Transform.Mode != TransformMode.Grid ||
            !TryGetObjectCellBounds(sceneObject, out var minimum, out var maximum))
        {
            SetStatus("当前模型没有可镜像的体素");
            return;
        }
        if (!IsAtBaseScale(sceneObject))
        {
            SetStatus("请先将模型缩放恢复为 1，再进行镜像");
            return;
        }
        var rotation = sceneObject.Transform.Grid.Rotation;
        var axis = rotation[0, 0] != 0 ? 0 : rotation[1, 0] != 0 ? 1 : 2;
        var command = _objectVoxels.CreateTransform(sceneObject.Id, "镜像模型", coordinate => axis switch
        {
            0 => new VoxelCoord64(minimum.X + maximum.X - coordinate.X, coordinate.Y, coordinate.Z),
            1 => new VoxelCoord64(coordinate.X, minimum.Y + maximum.Y - coordinate.Y, coordinate.Z),
            _ => new VoxelCoord64(coordinate.X, coordinate.Y, minimum.Z + maximum.Z - coordinate.Z),
        }, ObjectEditCallback(sceneObject.Id, forward =>
            SceneComponentTypes.WriteVoxelModel(component, SceneComponentTypes.ReadVoxelModel(component) with
            { MirrorX = forward ? mirrored : !mirrored })));
        _voxelHistory.Execute(command);
        InvalidateScaleBase(sceneObject.Id);
        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus(mirrored ? "已镜像模型" : "已取消模型镜像");
    }

    /// <summary>平移格变换模型的所有体素，并同步对象原点</summary>
    private void MoveModelObjectByGrid(SceneObject sceneObject, VoxelCoord64 delta)
    {
        if (delta == VoxelCoord64.Zero) return;
        var before = sceneObject.Transform.Grid;
        var after = new GridTransform(before.Position + delta, before.Rotation);
        var command = _objectVoxels.CreateTransform(sceneObject.Id, "移动模型", coordinate => coordinate + delta,
            forward =>
            {
                sceneObject.Transform.SetGrid(forward ? after : before);
                UpdateTransformMarker(sceneObject);
                RebuildObjectVoxelWorld();
            });
        _voxelHistory.Execute(command);
        UpdateTransformMarker(sceneObject);
        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus($"已移动模型：{delta.X}, {delta.Y}, {delta.Z}");
    }

    /// <summary>按世界轴把模型绕自身包围盒中心旋转 90 度</summary>
    private void RotateModelObjectBy90(SceneObject sceneObject, int axisIndex)
    {
        // 曲线对象的体素由曲线推算，直接转体素会在下次重建时被覆盖，改为旋转曲线点后重建
        if (sceneObject.Components.Any(SceneComponentTypes.IsCurveRenderer) &&
            !sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel))
        {
            RotateCurveObjectBy90(sceneObject, axisIndex);
            return;
        }

        if (!TryGetObjectCellBounds(sceneObject, out var minimum, out var maximum)) return;
        var pivot = new Vector3(
            (minimum.X + maximum.X) * 0.5F, (minimum.Y + maximum.Y) * 0.5F, (minimum.Z + maximum.Z) * 0.5F);
        var rotated = new VoxelWorld();
        foreach (var cell in _objectVoxels.GetOrCreate(sceneObject.Id).EnumerateOccupied())
            rotated.Set(RotateCoordinate(cell.Coordinate, pivot, axisIndex), cell.Value);

        if (!rotated.TryGetBounds(out var newMinimum, out _)) return;
        var newRotation = sceneObject.Transform.Grid.Rotation.Compose(AxisRotation(axisIndex));
        if (sceneObject.Components.All(component => !SceneComponentTypes.IsVoxelModel(component)))
        {
            // 生成器对象没有模型组件：只替换体素世界与格变换，并把这次变化记成一步撤销
            var beforeGrid = sceneObject.Transform.Grid;
            var afterGrid = new GridTransform(newMinimum, newRotation);
            var generatedWorld = _objectVoxels.GetOrCreate(sceneObject.Id);
            var changes = generatedWorld.EnumerateOccupied()
                .ToDictionary(cell => cell.Coordinate, _ => default(VoxelValue));
            foreach (var cell in rotated.EnumerateOccupied()) changes[cell.Coordinate] = cell.Value;
            _voxelHistory.Execute(new VoxelStrokeCommand("旋转生成体素", generatedWorld,
                changes.Select(cell => (cell.Key, cell.Value)), forward =>
                {
                    sceneObject.Transform.SetGrid(forward ? afterGrid : beforeGrid);
                    UpdateTransformMarker(sceneObject);
                    RebuildObjectVoxelWorld();
                }));
            MarkSceneDocumentDirty();
            RebuildObjectVoxelWorld();
            SetStatus("已旋转生成体素 90 度（参数不变，重新生成会恢复原朝向）");
            return;
        }

        if (!ReplaceObjectVoxels(sceneObject, rotated, newMinimum, newRotation))
            return;
        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus("已旋转模型 90 度");
    }

    private void RotateSceneObject(SceneObject sceneObject, int axisIndex)
    {
        if (sceneObject.Transform.Mode == TransformMode.Free)
        {
            var axis = AxisVector(axisIndex);
            var delta = Quaternion.CreateFromAxisAngle(
                new System.Numerics.Vector3(axis.X, axis.Y, axis.Z), MathHelper.DegreesToRadians(15F));
            var rotation = Quaternion.Normalize(delta * sceneObject.Transform.Free.Rotation);
            sceneObject.Transform.SetFree(new FreeTransform(sceneObject.Transform.Free.Position, rotation));
            UpdateTransformMarker(sceneObject);
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            return;
        }

        RotateModelObjectBy90(sceneObject, axisIndex);
    }

    private static OrthogonalRotation AxisRotation(int axisIndex) =>
        axisIndex switch
        {
            0 => OrthogonalRotation.AroundX90,
            1 => OrthogonalRotation.AroundY90,
            _ => OrthogonalRotation.AroundZ90,
        };

    private static VoxelCoord64 RotateCoordinate(VoxelCoord64 coordinate, Vector3 pivot, int axisIndex)
    {
        var x = coordinate.X - pivot.X;
        var y = coordinate.Y - pivot.Y;
        var z = coordinate.Z - pivot.Z;
        var (rx, ry, rz) = axisIndex switch
        {
            0 => (x, -z, y),
            1 => (z, y, -x),
            _ => (-y, x, z),
        };
        return new VoxelCoord64(
            (long)MathF.Round(pivot.X + rx),
            (long)MathF.Round(pivot.Y + ry),
            (long)MathF.Round(pivot.Z + rz));
    }

    private bool TryGetObjectCellBounds(SceneObject sceneObject, out VoxelCoord64 minimum, out VoxelCoord64 maximum)
    {
        return _objectVoxels.GetOrCreate(sceneObject.Id).TryGetBounds(out minimum, out maximum);
    }
    private bool TryGetSelectedLightRangeHandle(Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject sceneObject, out Vector2 screen)
    {
        screen = default;
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsLight);
        if (component is null) return false;
        var data = SceneComponentTypes.ReadLight(component);
        if (data.Shape == LightShape.Directional) return false;
        var handle = GetObjectWorldPosition(sceneObject) + camera.Right * Math.Max(0.5F, data.Range);
        return SceneProjection.Project(handle, origin, size, camera, out screen, out _);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 0.0001F) return Vector2.Distance(point, a);
        var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0F, 1F);
        return Vector2.Distance(point, a + ab * t);
    }
}
