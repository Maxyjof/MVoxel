using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private readonly Dictionary<string, string> _transformInputBuffers = new();
    private string? _axisDragId;
    private double _axisDragStart;
    private float _axisDragMouseX;
    private double _axisDragPreview;

    private bool TransformAxisInput(string key, string axis, double value, float width, out double result)
    {
        var id = $"{_selectedSceneObjectId}:{key}:{axis}";
        if (!_transformInputBuffers.TryGetValue(id, out var text)) text = value.ToString("0.###", CultureInfo.InvariantCulture);
        ImGui.PushID(id);
        var axisTextSize = ImGui.CalcTextSize(axis);
        var axisMin = ImGui.GetCursorScreenPos();
        var axisHeight = ImGui.GetFrameHeight();
        ImGui.InvisibleButton("##拖拽轴标签", new Vector2(axisTextSize.X, axisHeight));
        ImGui.GetWindowDrawList().AddText(axisMin + new Vector2(0F, (axisHeight - axisTextSize.Y) * .5F),
            ImGui.GetColorU32(ImGuiCol.Text), axis);
        if (ImGui.IsItemHovered()) { ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW); ImGui.SetTooltip("左右拖动调整数值"); }
        if (ImGui.IsItemActivated()) { _axisDragId = id; _axisDragStart = value; _axisDragMouseX = ImGui.GetIO().MousePos.X; _axisDragPreview = value; }
        var dragCommitted = false;
        if (_axisDragId == id)
        {
            var step = key == "旋转" ? 90D : 1D;
            _axisDragPreview = _axisDragStart + Math.Round((ImGui.GetIO().MousePos.X - _axisDragMouseX) / 12D) * step;
            if (ImGui.IsItemDeactivated()) { dragCommitted = _axisDragPreview != _axisDragStart; _axisDragId = null; }
            value = _axisDragPreview;
            if (dragCommitted || ImGui.IsItemActive()) text = value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        ImGui.PopID();
        ImGui.SameLine(0, 3);
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.InputText("##" + id, ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        var active = ImGui.IsItemActive();
        _transformInputBuffers[id] = active ? text : value.ToString("0.###", CultureInfo.InvariantCulture);
        result = value;
        if (dragCommitted) return true;
        if (!changed) return false;
        try
        {
            if (text.Length == 0 || text.Any(c => !char.IsDigit(c) && !".+-*/%() ".Contains(c))) throw new FormatException();
            using var table = new System.Data.DataTable { Locale = CultureInfo.InvariantCulture };
            result = Convert.ToDouble(table.Compute(text, ""), CultureInfo.InvariantCulture);
            if (!double.IsFinite(result)) throw new FormatException();
            _transformInputBuffers[id] = result.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or System.Data.EvaluateException or System.Data.SyntaxErrorException or DivideByZeroException)
        { SetStatus("表达式无效，请检查数字和运算符"); return false; }
    }

    private void DrawTransformInputs(SceneObject item)
    {
        var transform = item.Transform;
        var free = transform.Mode == TransformMode.Free;
        var position = free ? transform.Free.Position : new Double3(transform.Grid.Position.X, transform.Grid.Position.Y, transform.Grid.Position.Z);
        var rotation = free ? transform.GetEulerDegrees() : GridEuler(transform.Grid.Rotation);
        var positionValues = new[] { position.X, position.Y, position.Z };
        var rotationValues = new[] { rotation.X, rotation.Y, rotation.Z };
        var scaleValues = new[] { (double)transform.ScaleX, transform.ScaleY, transform.ScaleZ };
        DrawTransformRow("位置", positionValues, (axis, value) =>
        {
            if (free)
            {
                positionValues[axis] = value;
                transform.SetFree(new FreeTransform(new Double3(positionValues[0], positionValues[1], positionValues[2]), transform.Free.Rotation));
                UpdateTransformMarker(item); MarkSceneDocumentDirty();
            }
            else
            {
                if (value < long.MinValue || value >= (double)long.MaxValue) { SetStatus("位置超出整数范围"); return; }
                var target = (long)Math.Truncate(value);
                var current = transform.Grid.Position;
                try
                {
                    var delta = checked(target - (axis == 0 ? current.X : axis == 1 ? current.Y : current.Z));
                    MoveModelObjectByGrid(item, new VoxelCoord64(axis == 0 ? delta : 0, axis == 1 ? delta : 0, axis == 2 ? delta : 0));
                }
                catch (OverflowException) { SetStatus("位置变化超出整数范围"); }
            }
        });
        DrawTransformRow("旋转", rotationValues, (axis, value) =>
        {
            if (free)
            {
                rotationValues[axis] = value;
                transform.SetFreeEuler(transform.Free.Position, new Double3(rotationValues[0], rotationValues[1], rotationValues[2]));
                UpdateTransformMarker(item); MarkSceneDocumentDirty();
            }
            else
            {
                if (Math.Abs(value) > 360000 || Math.Abs(value / 90 - Math.Round(value / 90)) > 0.00001)
                { SetStatus("体素旋转角度必须为 90 度的整数倍"); return; }
                var steps = ((int)Math.Round((value - rotationValues[axis]) / 90) % 4 + 4) % 4;
                for (var step = 0; step < steps; step++) RotateModelObjectBy90(item, axis);
            }
        });
        if (item.Components.Any(SceneComponentTypes.IsVoxelModel))
            DrawTransformRow("缩放", scaleValues, (axis, value) =>
            {
                if (value < 1 || value > int.MaxValue) { SetStatus("缩放必须为正整数"); return; }
                scaleValues[axis] = Math.Truncate(value);
                ApplyAxisScale(item, (int)scaleValues[0], (int)scaleValues[1], (int)scaleValues[2]);
            });
    }

    private void DrawTransformRow(string label, double[] values, Action<int, double> apply)
    {
        BeginInspectorField(label, "拖动 X、Y、Z 标签调整；输入支持表达式，按 Enter 或失焦应用。体素旋转为 90 度整数倍。");
        var width = Math.Max(35F, (ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 3 - 32F) / 3F);
        string[] axes = ["X", "Y", "Z"];
        for (var i = 0; i < 3; i++)
        {
            if (i != 0) ImGui.SameLine(0, 5);
            if (TransformAxisInput(label, axes[i], values[i], width, out var result)) apply(i, result);
        }
    }

    private static Double3 GridEuler(OrthogonalRotation r)
    {
        var matrix = new Matrix4x4(r.M00, r.M01, r.M02, 0, r.M10, r.M11, r.M12, 0, r.M20, r.M21, r.M22, 0, 0, 0, 0, 1);
        return TransformComponent.FromFree(new FreeTransform(Double3.Zero, Quaternion.CreateFromRotationMatrix(matrix))).GetEulerDegrees();
    }
}
