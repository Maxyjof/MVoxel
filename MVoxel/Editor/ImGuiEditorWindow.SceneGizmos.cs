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
    private enum SceneGizmoMode { Select, Move, Rotate, Scale }

    private SceneGizmoMode _sceneGizmoMode = SceneGizmoMode.Select;
    private bool _sceneGizmoDragging;
    private int _sceneGizmoDragAxis;
    private bool _sceneGizmoDragRange;
    private bool _sceneGizmoDragScale;
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
    private float _sceneGizmoDragStartValue;
    private float _sceneGizmoDragPixelsPerUnit = 1F;
    private Vector2 _sceneGizmoDragScreenDirection;

    private static readonly Color AxisXColor = Color.FromArgb(255, 232, 84, 92);
    private static readonly Color AxisYColor = Color.FromArgb(255, 108, 214, 132);
    private static readonly Color AxisZColor = Color.FromArgb(255, 86, 156, 232);

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
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex) ||
                !sceneObject.Components.Any(component => SceneComponentTypes.IsCamera(component) && component.Enabled)) continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera, out var marker, out _) ||
                marker.X < origin.X || marker.X >= origin.X + size.X || marker.Y < origin.Y || marker.Y >= origin.Y + size.Y) continue;
            var color = ToPackedColor(selected?.Id == sceneObject.Id ? EditorPalette.Accent : EditorPalette.Icon);
            DrawSceneCameraIcon(drawList, marker, color);
            if (ImGui.IsWindowHovered() && Vector2.Distance(ImGui.GetIO().MousePos, marker) < 9F)
                ImGui.SetTooltip(sceneObject.Name);
        }
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex) ||
                sceneObject.Components.Any(component => SceneComponentTypes.IsVoxelModel(component) || SceneComponentTypes.IsCamera(component) || SceneComponentTypes.IsLight(component))) continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera, out var marker, out _)) continue;
            var color = ToPackedColor(selected?.Id == sceneObject.Id ? EditorPalette.Accent : EditorPalette.Icon);
            drawList.AddRect(marker - new Vector2(5F), marker + new Vector2(5F), color, 1F, ImDrawFlags.None, 1.3F);
            drawList.AddLine(marker - new Vector2(3F, 0F), marker + new Vector2(3F, 0F), color);
            drawList.AddLine(marker - new Vector2(0F, 3F), marker + new Vector2(0F, 3F), color);
            if (ImGui.IsWindowHovered() && Vector2.Distance(ImGui.GetIO().MousePos, marker) < 8F) ImGui.SetTooltip(sceneObject.Name);
        }
        if (selected is null) return;
        var pivot = GetObjectPivot(selected);
        if (!SceneProjection.Project(pivot, origin, size, camera, out var pivotScreen, out var depth)) return;
        var unitsPerPixel = Math.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        switch (_sceneGizmoMode)
        {
            case SceneGizmoMode.Move:
                DrawMoveGizmo(drawList, origin, size, camera, pivot, pivotScreen, unitsPerPixel);
                break;
            case SceneGizmoMode.Rotate:
                DrawRotateGizmo(drawList, origin, size, camera, pivot, unitsPerPixel);
                break;
            case SceneGizmoMode.Scale:
                DrawScaleGizmo(drawList, origin, size, camera, selected, pivot, pivotScreen);
                break;
        }
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

    private void DrawMoveGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        Vector3 pivot, Vector2 pivotScreen, float unitsPerPixel)
    {
        var length = unitsPerPixel * 82F;
        for (var plane = 0; plane < 3; plane++)
        {
            var (u, v) = AxisPlane(plane);
            if (!SceneProjection.Project(pivot + (u + v) * unitsPerPixel * 22F, origin, size, camera, out var point, out _)) continue;
            var color = ToPackedColor(Color.FromArgb(180, AxisColor(plane)));
            drawList.AddRectFilled(point - new Vector2(5F), point + new Vector2(5F), color, 1F);
        }
        for (var axisIndex = 0; axisIndex < 3; axisIndex++)
        {
            var axis = AxisVector(axisIndex);
            if (!TryProjectAxis(origin, size, camera, pivot, axis, out var end, out _)) continue;
            var color = ToPackedColor(AxisColor(axisIndex));
            drawList.AddLine(pivotScreen, end, color, 2.4F);
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
            var color = ToPackedColor(AxisColor(axisIndex));
            var (u, v) = AxisPlane(axisIndex);
            DrawWorldCircle(drawList, origin, size, camera, pivot, u, v, radius, color, 2F);
        }
    }

    private void DrawScaleGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject sceneObject, Vector3 pivot, Vector2 pivotScreen)
    {
        var handle = pivotScreen + new Vector2(0F, -72F);
        var color = ToPackedColor(EditorPalette.Accent);
        drawList.AddLine(pivotScreen, handle, color, 2F);
        drawList.AddRectFilled(handle - new Vector2(5F), handle + new Vector2(5F), color);
        drawList.AddText(handle + new Vector2(10F, -6F), color,
            $"{sceneObject.Transform.ScaleX} · {sceneObject.Transform.ScaleY} · {sceneObject.Transform.ScaleZ}");
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
        if (_sceneGizmoDragging)
        {
            UpdateSceneGizmoDrag(origin, size, camera);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndSceneGizmoDrag();
            return true;
        }

        if (!ImGui.IsMouseHoveringRect(origin, origin + size) || orientationGizmoHovered) return false;
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
            _activeTool = EditorTool.Pick;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) FocusHierarchyObject(sceneObject);
            return true;
        }

        if (selected is null || !IsSceneObjectVisible(selected) || !_sceneDocument.IsLayerVisible(selected.LayerIndex)) return false;
        var pivot = GetObjectPivot(selected);
        if (!SceneProjection.Project(pivot, origin, size, camera, out var pivotScreen, out var depth)) return false;
        var unitsPerPixel = MathF.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        switch (_sceneGizmoMode)
        {
            case SceneGizmoMode.Move:
                for (var plane = 0; plane < 3; plane++)
                {
                    var (u, v) = AxisPlane(plane);
                    if (!SceneProjection.Project(pivot + (u + v) * unitsPerPixel * 22F, origin, size, camera, out var point, out _) ||
                        Vector2.Distance(mouse, point) > 9F ||
                        !SceneProjection.Project(pivot + u, origin, size, camera, out var pu, out _) ||
                        !SceneProjection.Project(pivot + v, origin, size, camera, out var pv, out _)) continue;
                    var su = pu - pivotScreen; var sv = pv - pivotScreen;
                    if (MathF.Abs(su.X * sv.Y - su.Y * sv.X) < .0001F) continue;
                    _sceneGizmoDragPlane = plane; _sceneGizmoPlaneU = su; _sceneGizmoPlaneV = sv;
                    _sceneGizmoDragStartMouse = mouse; _sceneGizmoDragStartPosition = pivot;
                    _sceneGizmoDragging = true; _sceneGizmoDragRotation = false;
                    return true;
                }
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    if (!TryProjectAxis(origin, size, camera, pivot, AxisVector(axisIndex),
                            out var end, out var pixelsPerUnit)) continue;
                    if (DistanceToSegment(mouse, pivotScreen, end) > 9F) continue;
                    _sceneGizmoDragPlane = -1;
                    _sceneGizmoDragRotation = false;
                    _sceneGizmoDragAxis = axisIndex;
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoDragStartPosition = pivot;
                    _sceneGizmoDragStartGrid = selected.Transform.Grid.Position;
                    _sceneGizmoDragPixelsPerUnit = MathF.Max(0.001F, pixelsPerUnit);
                    _sceneGizmoDragScreenDirection = Vector2.Normalize(end - pivotScreen);
                    _sceneGizmoDragging = true;
                    _sceneGizmoDragRange = false;
                    _sceneGizmoDragScale = false;
                    return true;
                }

                return false;
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
                    _sceneGizmoDragStartValue = component is null ? 1F : SceneComponentTypes.ReadVoxelModel(component).Scale;
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
        if (_sceneGizmoDragRotation)
        {
            var angle = MathF.Atan2(mouse.Y - _sceneGizmoPivotScreen.Y, mouse.X - _sceneGizmoPivotScreen.X);
            _sceneGizmoRotationDelta = MathF.Atan2(MathF.Sin(angle - _sceneGizmoStartAngle), MathF.Cos(angle - _sceneGizmoStartAngle));
            if (selected.Transform.Mode == TransformMode.Free)
            {
                var axis = AxisVector(_sceneGizmoDragAxis);
                var delta = Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(axis.X, axis.Y, axis.Z), _sceneGizmoRotationDelta);
                selected.Transform.SetFree(new FreeTransform(selected.Transform.Free.Position, Quaternion.Normalize(delta * _sceneGizmoStartRotation)));
                UpdateTransformMarker(selected); _pipeline?.ResetAccumulation();
            }
            return;
        }
        if (_sceneGizmoDragPlane >= 0)
        {
            if (selected.Transform.Mode == TransformMode.Free)
            {
                var delta = PlaneDragDelta(mouse);
                var position = _sceneGizmoDragStartPosition + delta;
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
                MathF.Pow(2F, -(mouse.Y - _sceneGizmoDragStartMouse.Y) / 80F), 0.125F, 8F);
            SceneComponentTypes.WriteVoxelModel(component, data with { Scale = factor });
            return;
        }

        if (selected.Transform.Mode == TransformMode.Free)
        {
            var axis = AxisVector(_sceneGizmoDragAxis);
            if (!SceneProjection.Project(_sceneGizmoDragStartPosition, origin, size, camera, out var start, out _) ||
                !SceneProjection.Project(_sceneGizmoDragStartPosition + axis, origin, size, camera, out var unit, out _))
                return;
            var direction = unit - start;
            if (direction.LengthSquared() < 0.0001F) return;
            var pixelsPerUnit = direction.Length();
            direction /= pixelsPerUnit;
            var deltaPixels = Vector2.Dot(mouse - _sceneGizmoDragStartMouse, direction);
            var position = _sceneGizmoDragStartPosition + axis * (deltaPixels / pixelsPerUnit);
            var rotation = selected.Transform.Free.Rotation;
            selected.Transform.SetFree(new FreeTransform(
                new Double3(position.X, position.Y, position.Z), rotation));
            UpdateTransformMarker(selected);
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }
    }

    private void EndSceneGizmoDrag()
    {
        var selected = GetSelectedSceneObject();
        _sceneGizmoDragging = false;
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
                var steps = (int)MathF.Round(_sceneGizmoRotationDelta / (MathF.PI / 2F));
                for (var i = 0; i < ((steps % 4 + 4) % 4); i++) RotateModelObjectBy90(selected, _sceneGizmoDragAxis);
            }
            else if (MathF.Abs(_sceneGizmoRotationDelta) > .0001F) MarkSceneDocumentDirty();
            return;
        }
        if (_sceneGizmoDragPlane >= 0)
        {
            var delta = PlaneDragDelta(mouse);
            _sceneGizmoDragPlane = -1;
            if (selected.Transform.Mode == TransformMode.Grid)
                MoveModelObjectByGrid(selected, new VoxelCoord64((long)MathF.Round(delta.X), (long)MathF.Round(delta.Y), (long)MathF.Round(delta.Z)));
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
                selected.Transform.ScaleX = Math.Max(1, (int)MathF.Round(selected.Transform.ScaleX * multiplier));
                selected.Transform.ScaleY = Math.Max(1, (int)MathF.Round(selected.Transform.ScaleY * multiplier));
                selected.Transform.ScaleZ = Math.Max(1, (int)MathF.Round(selected.Transform.ScaleZ * multiplier));
                var after = SceneSerializer.CloneObject(selected);
                if (!SceneSerializer.ObjectsEqual(before, after))
                {
                    _voxelHistory.RecordExecuted(new SceneObjectStateCommand(selected, before, after,
                        () => { MarkSceneDocumentDirty(); _pipeline?.ResetAccumulation(); }));
                    MarkSceneDocumentDirty();
                }
                return;
            }
            var factor = SceneComponentTypes.ReadVoxelModel(component).Scale;
            SceneComponentTypes.WriteVoxelModel(component,
                SceneComponentTypes.ReadVoxelModel(component) with { Scale = _sceneGizmoDragStartValue });
            if (MathF.Abs(factor - _sceneGizmoDragStartValue) > 0.01F)
            {
                var multiplier = factor / Math.Max(0.125F, _sceneGizmoDragStartValue);
                ApplyAxisScale(selected, Math.Max(1, (int)MathF.Round(selected.Transform.ScaleX * multiplier)),
                    Math.Max(1, (int)MathF.Round(selected.Transform.ScaleY * multiplier)),
                    Math.Max(1, (int)MathF.Round(selected.Transform.ScaleZ * multiplier)));
            }
            return;
        }

        if (selected.Transform.Mode == TransformMode.Grid)
        {
            var steps = (int)MathF.Round(
                Vector2.Dot(mouse - _sceneGizmoDragStartMouse, _sceneGizmoDragScreenDirection) /
                _sceneGizmoDragPixelsPerUnit);
            if (steps == 0) return;
            var axis = AxisVector(_sceneGizmoDragAxis);
            MoveModelObjectByGrid(selected, new VoxelCoord64(
                (long)Math.Round(axis.X * steps), (long)Math.Round(axis.Y * steps), (long)Math.Round(axis.Z * steps)));
        }
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
        if (!TryGetObjectCellBounds(sceneObject, out var minimum, out var maximum)) return;
        var pivot = new Vector3(
            (minimum.X + maximum.X) * 0.5F, (minimum.Y + maximum.Y) * 0.5F, (minimum.Z + maximum.Z) * 0.5F);
        var rotated = new VoxelWorld();
        foreach (var cell in _objectVoxels.GetOrCreate(sceneObject.Id).EnumerateOccupied())
            rotated.Set(RotateCoordinate(cell.Coordinate, pivot, axisIndex), cell.Value);

        if (!rotated.TryGetBounds(out var newMinimum, out _)) return;
        var newRotation = sceneObject.Transform.Grid.Rotation.Compose(AxisRotation(axisIndex));
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
