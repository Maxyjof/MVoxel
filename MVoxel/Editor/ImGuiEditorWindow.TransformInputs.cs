using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    /// <summary>坐标字段的文本缓冲，键为选中对象、字段名与轴的组合</summary>
    private readonly Dictionary<string, string> _transformInputBuffers = new();
    /// <summary>正在拖拽调值的轴标签</summary>
    private string? _axisDragId;
    /// <summary>拖拽开始时该轴的数值</summary>
    private double _axisDragStart;
    /// <summary>拖拽开始时的鼠标横坐标</summary>
    private float _axisDragMouseX;
    /// <summary>拖拽过程中已经写回的预览值</summary>
    private double _axisDragPreview;

    /// <summary>
    /// 绘制单轴数值输入框：输入即时写回，轴标签可按住左右拖动调值
    /// </summary>
    /// <param name="key">字段名，决定提示文案</param>
    /// <param name="axis">轴标签</param>
    /// <param name="value">当前值</param>
    /// <param name="width">输入框宽度</param>
    /// <param name="dragStep">拖动一格对应的数值变化</param>
    /// <param name="pixelsPerStep">拖动多少像素算一格</param>
    /// <param name="result">本帧要写回的新值</param>
    /// <returns>本帧数值是否发生变化</returns>
    private bool TransformAxisInput(string key, string axis, double value, float width,
        double dragStep, double pixelsPerStep, out double result)
    {
        var id = $"{_selectedSceneObjectId}:{key}:{axis}";
        if (!_transformInputBuffers.TryGetValue(id, out var text)) text = FormatTransformValue(value);
        //松开鼠标或窗口失去焦点后立即结束拖拽，避免残留拖拽状态
        if (_axisDragId is not null && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) _axisDragId = null;
        var dragging = DrawTransformAxisLabel(key, axis, value, dragStep, pixelsPerStep);
        result = value;
        var changed = false;

        if (dragging)
        {
            //横向位移换算成离散步进，松手前就写回，并在本帧让输入框直接显示预览值
            var steps = Math.Round((ImGui.GetIO().MousePos.X - _axisDragMouseX) / pixelsPerStep);
            var preview = _axisDragStart + steps * dragStep;
            if (!SameTransformValue(preview, _axisDragPreview))
            {
                _axisDragPreview = preview;
                result = preview;
                changed = true;
            }

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _axisDragId = null;
            text = FormatTransformValue(preview);
        }

        ImGui.SameLine(0, 3);
        ImGui.SetNextItemWidth(width);
        var inputChanged = ImGui.InputText("##" + id, ref text, 128);
        var inputActive = ImGui.IsItemActive();
        var inputFinished = ImGui.IsItemDeactivatedAfterEdit();

        //拖拽期间输入框只显示预览值，不参与文本解析
        if (!dragging && inputChanged)
        {
            //边输入边写回；中间态（如 "-" 或 "3+"）解析失败时保持原值，不让对象乱跳
            if (TryEvaluateTransformValue(text, out var typed) && !SameTransformValue(typed, value))
            {
                result = typed;
                changed = true;
            }
        }
        else if (!dragging && inputFinished && !TryEvaluateTransformValue(text, out _))
        {
            SetStatus("表达式无效，请检查数字和运算符");
        }

        //编辑中保留用户原文，其余情况统一按最终值格式化，避免显示值与实际值不一致
        if (inputActive) _transformInputBuffers[id] = text;
        else if (changed) _transformInputBuffers[id] = FormatTransformValue(result);
        else _transformInputBuffers[id] = FormatTransformValue(value);
        return changed;
    }

    /// <summary>
    /// 绘制可左右拖动调值的轴标签，并提供缩放光标与悬停高亮提示
    /// </summary>
    /// <param name="key">字段名，决定提示文案</param>
    /// <param name="axis">轴标签</param>
    /// <param name="value">拖拽起点数值</param>
    /// <param name="dragStep">拖动一格对应的数值变化</param>
    /// <param name="pixelsPerStep">拖动多少像素算一格</param>
    /// <returns>该轴是否正在拖动</returns>
    private bool DrawTransformAxisLabel(string key, string axis, double value, double dragStep, double pixelsPerStep)
    {
        var id = $"{_selectedSceneObjectId}:{key}:{axis}";
        ImGui.PushID(id);
        var textSize = ImGui.CalcTextSize(axis);
        var height = ImGui.GetFrameHeight();
        var labelWidth = Math.Max(textSize.X + 6F, height * .8F);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##拖拽轴标签", new Vector2(labelWidth, height));
        var hovered = ImGui.IsItemHovered();
        var dragging = _axisDragId == id;
        if (ImGui.IsItemActivated())
        {
            _axisDragId = id;
            _axisDragStart = value;
            _axisDragMouseX = ImGui.GetIO().MousePos.X;
            _axisDragPreview = value;
            dragging = true;
        }

        var drawList = ImGui.GetWindowDrawList();
        var highlighted = hovered || dragging;
        if (highlighted)
        {
            drawList.AddRectFilled(origin, origin + new Vector2(labelWidth, height),
                ToPackedColor(dragging ? EditorPalette.Accent : EditorPalette.AccentSoft), ImGui.GetStyle().FrameRounding);
        }

        drawList.AddText(origin + new Vector2((labelWidth - textSize.X) * .5F, (height - textSize.Y) * .5F),
            ToPackedColor(highlighted ? EditorPalette.Text : EditorPalette.MutedText), axis);
        if (highlighted) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        if (hovered)
        {
            ImGui.SetTooltip(key == "旋转"
                ? $"按住左右拖动可直接调整角度，每 {pixelsPerStep:0} 像素 {dragStep:0} 度；输入后立即生效"
                : $"按住左右拖动可直接调整数值，每 {pixelsPerStep:0} 像素 {dragStep:0.###}；输入后立即生效");
        }

        ImGui.PopID();
        return dragging;
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
        DrawTransformRow("位置", positionValues, 1D, 12D, (axis, value) =>
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
        DrawTransformRow("旋转", rotationValues, free ? 5D : 90D, free ? 12D : 24D, (axis, value) =>
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
            DrawTransformRow("缩放", scaleValues, 1D, 12D, (axis, value) =>
            {
                if (value < 1 || value > int.MaxValue) { SetStatus("缩放必须为正整数"); return; }
                scaleValues[axis] = Math.Truncate(value);
                ApplyAxisScale(item, (int)scaleValues[0], (int)scaleValues[1], (int)scaleValues[2]);
            }, "缩放始终以原模型（1x）为基准重采样，逐字输入和拖动都会即时生效，反复缩放不会累积精度损失。在非 1x 尺寸上绘制的方块只存在于当前尺寸，再次缩放会按原模型重算替换。");
    }

    /// <summary>
    /// 绘制一行三轴数值输入
    /// </summary>
    /// <param name="label">字段名</param>
    /// <param name="values">三个轴的当前值</param>
    /// <param name="dragStep">拖动一格对应的数值变化</param>
    /// <param name="pixelsPerStep">拖动多少像素算一格</param>
    /// <param name="apply">写回单个轴的回调</param>
    /// <param name="tooltip">字段的悬停说明，省略时使用通用说明</param>
    private void DrawTransformRow(string label, double[] values, double dragStep, double pixelsPerStep,
        Action<int, double> apply, string? tooltip = null)
    {
        BeginInspectorField(label, tooltip ?? "输入数字或表达式时数值立即应用到对象，输入中的不完整内容会等解析成功再生效；按住 X、Y、Z 标签左右拖动也能实时调值，指针会变成左右调整光标。体素旋转为 90 度整数倍。");
        string[] axes = ["X", "Y", "Z"];
        var labelWidth = 0F;
        foreach (var axis in axes)
            labelWidth += Math.Max(ImGui.CalcTextSize(axis).X + 6F, ImGui.GetFrameHeight() * .8F);
        var width = Math.Max(35F, (ImGui.GetContentRegionAvail().X - labelWidth - 32F) / 3F);
        for (var i = 0; i < 3; i++)
        {
            if (i != 0) ImGui.SameLine(0, 5);
            if (TransformAxisInput(label, axes[i], values[i], width, dragStep, pixelsPerStep, out var result)) apply(i, result);
        }
    }

    private static string FormatTransformValue(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool SameTransformValue(double left, double right) => Math.Abs(left - right) <= .000001D;

    /// <summary>
    /// 解析数字或算术表达式，失败时不改变当前值
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <param name="value">解析结果</param>
    /// <returns>是否解析成功</returns>
    private static bool TryEvaluateTransformValue(string text, out double value)
    {
        value = 0D;
        if (text.Length == 0 || text.Any(c => !char.IsDigit(c) && !".+-*/%() ".Contains(c))) return false;
        try
        {
            using var table = new System.Data.DataTable { Locale = CultureInfo.InvariantCulture };
            value = Convert.ToDouble(table.Compute(text, ""), CultureInfo.InvariantCulture);
            return double.IsFinite(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or System.Data.EvaluateException or System.Data.SyntaxErrorException or DivideByZeroException)
        {
            return false;
        }
    }

    private static Double3 GridEuler(OrthogonalRotation r)
    {
        var matrix = new Matrix4x4(r.M00, r.M01, r.M02, 0, r.M10, r.M11, r.M12, 0, r.M20, r.M21, r.M22, 0, 0, 0, 0, 1);
        return TransformComponent.FromFree(new FreeTransform(Double3.Zero, Quaternion.CreateFromRotationMatrix(matrix))).GetEulerDegrees();
    }
}
