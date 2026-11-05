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
    /// <summary>拖动累积的像素位移：右与上为正，左与下为负</summary>
    private float _axisDragPixels;
    /// <summary>环绕到另一侧后需要补回的位移，避免下一帧把跳变当成拖动</summary>
    private float _axisDragWrap;
    private Vector2 _axisDragWrapSize;
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
            //任意方向都能拖：右/上增大，左/下减小；到编辑器边缘时环绕到另一侧继续拖
            var io = ImGui.GetIO();
            var delta = io.MouseDelta.X - io.MouseDelta.Y + _axisDragWrap;
            _axisDragWrap = 0F;
            _axisDragPixels += delta;
            var viewport = ImGui.GetMainViewport();
            var minimum = viewport.Pos + new Vector2(1F, 1F);
            var maximum = viewport.Pos + viewport.Size - new Vector2(2F, 2F);
            _axisDragWrapSize = maximum - minimum;
            if (io.MousePos.X <= minimum.X)
            {
                io.MousePos = new Vector2(maximum.X, io.MousePos.Y);
                _axisDragWrap = _axisDragWrapSize.X;
            }
            else if (io.MousePos.X >= maximum.X)
            {
                io.MousePos = new Vector2(minimum.X, io.MousePos.Y);
                _axisDragWrap = -_axisDragWrapSize.X;
            }
            else if (io.MousePos.Y <= minimum.Y)
            {
                io.MousePos = new Vector2(io.MousePos.X, maximum.Y);
                _axisDragWrap = -_axisDragWrapSize.Y;
            }
            else if (io.MousePos.Y >= maximum.Y)
            {
                io.MousePos = new Vector2(io.MousePos.X, minimum.Y);
                _axisDragWrap = _axisDragWrapSize.Y;
            }

            //位移换算成离散步进，松手前就写回，并在本帧让输入框直接显示预览值
            var steps = Math.Round(_axisDragPixels / pixelsPerStep);
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
        ImGui.InputText("##" + id, ref text, 128, ImGuiInputTextFlags.AutoSelectAll);
        var inputActive = ImGui.IsItemActive();
        var inputFinished = ImGui.IsItemDeactivatedAfterEdit();

        // 打字只在回车或失焦时提交：逐字写回会让缩放反复重采样大模型，
        // 也会让 "1" → "12" 这种中间态先应用到对象上。拖动轴标签仍然实时。
        if (!dragging && inputFinished)
        {
            if (TryEvaluateTransformValue(text, out var typed))
            {
                if (!SameTransformValue(typed, value))
                {
                    result = typed;
                    changed = true;
                }
            }
            else
            {
                SetStatus("表达式无效，请检查数字和运算符");
            }
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
            _axisDragPixels = 0F;
            _axisDragWrap = 0F;
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
        if (!free)
        {
            // 有体素模型组件的对象用浮点旋转；没有模型组件（例如还没实例化的生成器）仍用 90 度离散旋转
            var rotationComponent = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (rotationComponent is not null)
            {
                var modelData = SceneComponentTypes.ReadVoxelModel(rotationComponent);
                var modelRotation = new[] { (double)modelData.RotationX, modelData.RotationY, modelData.RotationZ };
                DrawTransformRow("旋转", modelRotation, 5D, 12D, (axis, value) =>
                {
                    modelRotation[axis] = value;
                    _pendingModelRotation = modelRotation;
                    _pendingModelRotationObjectId = item.Id;
                }, "浮点旋转：按角度对基准体素重新采集，结果是干净的整数体素而不是斜格子。\n拖动只预览，松开后整份重新生成；反复调整都以最近一次编辑的基准为准。");
                CommitPendingModelRotation(item, rotationComponent);
            }
            else
            {
                DrawTransformRow("旋转", rotationValues, 90D, 24D, (axis, value) =>
                {
                    if (Math.Abs(value) > 360000 || Math.Abs(value / 90 - Math.Round(value / 90)) > 0.00001)
                    {
                        SetStatus("体素旋转角度必须为 90 度的整数倍");
                        return;
                    }

                    var steps = ((int)Math.Round((value - rotationValues[axis]) / 90) % 4 + 4) % 4;
                    for (var step = 0; step < steps; step++) RotateModelObjectBy90(item, axis);
                });
            }
        }
        else
        {
            DrawTransformRow("旋转", rotationValues, 5D, 12D, (axis, value) =>
            {
                rotationValues[axis] = value;
                transform.SetFreeEuler(transform.Free.Position, new Double3(rotationValues[0], rotationValues[1], rotationValues[2]));
                UpdateTransformMarker(item); MarkSceneDocumentDirty();
            });
        }

        if (item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel) is { } modelComponent)
            DrawModelScaleRow(item, modelComponent);
        else if (!free) DrawTransformScaleRow(item);
    }

    /// <summary>缩放行的联动锁定状态，和 Unity 的链式按钮一致</summary>
    private bool _transformScaleLocked;

    /// <summary>体素模型倍数缩放的联动锁定状态</summary>
    private bool _modelScaleLocked;

    /// <summary>
    /// 带联动锁定的三轴行：锁定后改任意一轴，其余两轴同步为同一个值
    /// </summary>
    private void DrawTransformRowLocked(string label, double[] values, double dragStep, double pixelsPerStep,
        ref bool locked, Action<int, double> apply)
    {
        BeginInspectorField(label);
        DrawChainToggle(ref locked);
        ImGui.SameLine(0F, 4F);
        string[] axes = ["X", "Y", "Z"];
        var labelWidth = 0F;
        foreach (var axis in axes)
            labelWidth += Math.Max(ImGui.CalcTextSize(axis).X + 6F, ImGui.GetFrameHeight() * .8F);
        var width = Math.Max(35F, (ImGui.GetContentRegionAvail().X - labelWidth - 32F) / 3F);
        for (var i = 0; i < 3; i++)
        {
            if (i != 0) ImGui.SameLine(0, 5);
            if (TransformAxisInput(label, axes[i], values[i], width, dragStep, pixelsPerStep, out var result))
            {
                if (locked)
                {
                    for (var axis = 0; axis < 3; axis++) apply(axis, result);
                }
                else apply(i, result);
            }
        }
    }

    /// <summary>
    /// 绘制链式锁定按钮，锁定后高亮
    /// </summary>
    /// <param name="locked">锁定状态</param>
    private static void DrawChainToggle(ref bool locked)
    {
        var size = ImGui.GetFrameHeight();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID("##TransformLock");
        if (ImGui.InvisibleButton("##TransformLockButton", new Vector2(size, size))) locked = !locked;
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered || locked)
            draw.AddRectFilled(origin, origin + new Vector2(size, size),
                ToPackedColor(locked ? EditorPalette.AccentSoft : EditorPalette.PanelHeader),
                ImGui.GetStyle().FrameRounding);
        var color = ToPackedColor(locked ? EditorPalette.Accent : EditorPalette.MutedText);
        var center = origin + new Vector2(size * .5F, size * .5F);
        draw.AddCircle(center + new Vector2(-3.4F, 0F), 3.2F, color, 10, 1.4F);
        draw.AddCircle(center + new Vector2(3.4F, 0F), 3.2F, color, 10, 1.4F);
        if (hovered) ImGui.SetTooltip(locked ? "已锁定：改一个轴会同步其余两轴" : "点击锁定：改一个轴会同步其余两轴");
        ImGui.PopID();
    }

    /// <summary>
    /// 没有体素模型组件的网格对象用整数缩放，改的是对象变换的缩放分量
    /// </summary>
    /// <param name="item">选中的对象</param>
    private void DrawTransformScaleRow(SceneObject item)
    {
        var transform = item.Transform;
        var values = new[] { (double)transform.ScaleX, transform.ScaleY, transform.ScaleZ };
        DrawTransformRowLocked("缩放", values, .05D, 12D, ref _transformScaleLocked, (axis, value) =>
        {
            var clamped = MathF.Max(0F, (float)value);
            values[axis] = clamped;
            ApplyObjectScale(item, (float)values[0], (float)values[1], (float)values[2]);
        });
    }

    /// <summary>
    /// 模型倍数缩放：三轴各自填写，放大只接受整数倍，缩小接受小数倍，
    /// 基准是最近一次编辑后的模型
    /// </summary>
    /// <param name="item">选中的模型对象</param>
    /// <param name="component">模型组件</param>
    private void DrawModelScaleRow(SceneObject item, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var scaleValues = new[] { (double)data.ScaleX, data.ScaleY, data.ScaleZ };
        DrawTransformRowLocked("缩放", scaleValues, 0.1D, 12D, ref _modelScaleLocked, (axis, value) =>
        {
            scaleValues[axis] = value;
            ApplyModelScale(item, (float)scaleValues[0], (float)scaleValues[1], (float)scaleValues[2]);
        });
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
        // 旧场景可能留下非法矩阵，显示时按单位旋转处理，避免得到 NaN 欧拉角
        if (!r.IsValid) r = OrthogonalRotation.Identity;
        var matrix = new Matrix4x4(r.M00, r.M01, r.M02, 0, r.M10, r.M11, r.M12, 0, r.M20, r.M21, r.M22, 0, 0, 0, 0, 1);
        return TransformComponent.FromFree(new FreeTransform(Double3.Zero, Quaternion.CreateFromRotationMatrix(matrix))).GetEulerDegrees();
    }
}
