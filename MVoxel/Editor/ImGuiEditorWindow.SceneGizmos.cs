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
        if (selected is not null && !_sceneDocument.IsLayerVisible(selected.LayerIndex)) selected = null;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            var lightComponent = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsLight);
            if (lightComponent is null || !IsSceneObjectVisible(sceneObject) ||
                !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex)) continue;
            var selectedLight = selected?.Id == sceneObject.Id;
            if (!selectedLight)
            {
                // 未选中的灯光只画一个小标记，避免大范围圆环铺满视口
                if (SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera,
                        out var marker, out _))
                {
                    var data = SceneComponentTypes.ReadLight(lightComponent);
                    drawList.AddCircleFilled(marker, 3F, ToPackedColor(Color.FromArgb(170,
                        Math.Clamp((int)(data.ColorR * 255F), 0, 255),
                        Math.Clamp((int)(data.ColorG * 255F), 0, 255),
                        Math.Clamp((int)(data.ColorB * 255F), 0, 255))));
                }
                continue;
            }

            DrawLightGizmo(drawList, origin, size, camera, sceneObject, lightComponent, selected: true);
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
        drawList.AddCircleFilled(centerScreen, selected ? 4F : 3F, packed);
        if (!selected) return;

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
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var handle = pivotScreen + new Vector2(0F, -72F);
        var color = ToPackedColor(EditorPalette.Accent);
        drawList.AddLine(pivotScreen, handle, color, 2F);
        drawList.AddRectFilled(handle - new Vector2(5F), handle + new Vector2(5F), color);
        drawList.AddText(handle + new Vector2(10F, -6F), color, $"{data.Scale:0.##}x");
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
            if (!IsSceneObjectVisible(sceneObject) || !_sceneDocument.IsLayerVisible(sceneObject.LayerIndex) ||
                !sceneObject.Components.Any(SceneComponentTypes.IsLight)) continue;
            if (!SceneProjection.Project(GetObjectWorldPosition(sceneObject), origin, size, camera,
                    out var screen, out _) || Vector2.Distance(mouse, screen) > 12F) continue;
            _selectedSceneObjectId = sceneObject.Id;
            return true;
        }

        if (selected is null || !_sceneDocument.IsLayerVisible(selected.LayerIndex)) return false;
        var pivot = GetObjectPivot(selected);
        if (!SceneProjection.Project(pivot, origin, size, camera, out var pivotScreen, out var depth)) return false;
        var unitsPerPixel = MathF.Max(0.0001F, depth / MathF.Max(1F, SceneProjection.FocalLength(size, camera)));
        switch (_sceneGizmoMode)
        {
            case SceneGizmoMode.Move:
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    if (!TryProjectAxis(origin, size, camera, pivot, AxisVector(axisIndex),
                            out var end, out var pixelsPerUnit)) continue;
                    if (DistanceToSegment(mouse, pivotScreen, end) > 9F) continue;
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
                    RotateSceneObject(selected, axisIndex);
                    return true;
                }

                return false;
            case SceneGizmoMode.Scale:
                if (selected.Components.Any(SceneComponentTypes.IsVoxelModel) &&
                    Vector2.Distance(mouse, pivotScreen + new Vector2(0F, -72F)) <= 12F)
                {
                    var component = selected.Components.First(SceneComponentTypes.IsVoxelModel);
                    _sceneGizmoDragStartMouse = mouse;
                    _sceneGizmoDragStartValue = SceneComponentTypes.ReadVoxelModel(component).Scale;
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
            if (component is null) return;
            var factor = SceneComponentTypes.ReadVoxelModel(component).Scale;
            SceneComponentTypes.WriteVoxelModel(component,
                SceneComponentTypes.ReadVoxelModel(component) with { Scale = _sceneGizmoDragStartValue });
            if (MathF.Abs(factor - _sceneGizmoDragStartValue) > 0.01F) ApplyModelScale(selected, factor);
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
