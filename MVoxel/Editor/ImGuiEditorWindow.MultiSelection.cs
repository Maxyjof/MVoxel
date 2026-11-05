using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 多选检查器：只显示所有选中对象共有的信息，混合值用横线占位，输入后统一写入全部对象
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>混合值输入框里的草稿文本</summary>
    private readonly Dictionary<string, string> _multiMixedText = new();

    /// <summary>
    /// 绘制多选检查器
    /// </summary>
    private void DrawMultiSelectionInspector()
    {
        if (_sceneDocument is null) return;
        var objects = _selectedSceneObjectIds
            .Select(id => _sceneDocument.Objects.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null).Select(item => item!).ToArray();
        if (objects.Length == 0) return;
        ImGui.TextDisabled($"已选择 {objects.Length} 个对象");
        foreach (var item in objects)
        {
            ImGui.PushID(item.Id.ToString());
            var primary = _selectedSceneObjectId == item.Id;
            if (ImGui.Selectable(item.Name, primary))
            {
                _selectedSceneObjectId = item.Id;
                _revealHierarchySelection = item.Id;
            }

            ImGui.PopID();
        }

        ImGui.Separator();
        DrawMultiTransformSection(objects);
    }

    /// <summary>
    /// 多个对象共有的变换字段：相同值显示数值，不同值显示横线，输入后写入全部对象
    /// </summary>
    /// <param name="objects">选中的对象</param>
    private void DrawMultiTransformSection(SceneObject[] objects)
    {
        if (!ImGui.CollapsingHeader("变换（多选）##MultiTransform", ImGuiTreeNodeFlags.DefaultOpen)) return;
        DrawMultiFloatRow("位置 X", objects, static item => item.Transform.Grid.Position.X,
            static (item, value) => MoveMultiObjectAxis(item, 0, value));
        DrawMultiFloatRow("位置 Y", objects, static item => item.Transform.Grid.Position.Y,
            static (item, value) => MoveMultiObjectAxis(item, 1, value));
        DrawMultiFloatRow("位置 Z", objects, static item => item.Transform.Grid.Position.Z,
            static (item, value) => MoveMultiObjectAxis(item, 2, value));
        DrawMultiFloatRow("缩放 X", objects, ReadObjectScaleX, WriteObjectScaleX);
        DrawMultiFloatRow("缩放 Y", objects, ReadObjectScaleY, WriteObjectScaleY);
        DrawMultiFloatRow("缩放 Z", objects, ReadObjectScaleZ, WriteObjectScaleZ);
    }

    /// <summary>
    /// 沿单轴移动对象，供多选位置字段使用
    /// </summary>
    private static void MoveMultiObjectAxis(SceneObject item, int axis, double value)
    {
        if (item.Transform.Mode != TransformMode.Grid) return;
        var target = (long)Math.Truncate(value);
        var current = item.Transform.Grid.Position;
        var delta = checked(target - (axis switch { 0 => current.X, 1 => current.Y, _ => current.Z }));
        if (delta == 0) return;
        item.Transform.SetGrid(new GridTransform(new VoxelCoord64(
            axis == 0 ? current.X + delta : current.X,
            axis == 1 ? current.Y + delta : current.Y,
            axis == 2 ? current.Z + delta : current.Z), item.Transform.Grid.Rotation));
    }

    private static double ReadObjectScaleX(SceneObject item) => item.Transform.ScaleX;
    private static double ReadObjectScaleY(SceneObject item) => item.Transform.ScaleY;
    private static double ReadObjectScaleZ(SceneObject item) => item.Transform.ScaleZ;
    private static void WriteObjectScaleX(SceneObject item, double value) => item.Transform.ScaleX = (int)Math.Round(value);
    private static void WriteObjectScaleY(SceneObject item, double value) => item.Transform.ScaleY = (int)Math.Round(value);
    private static void WriteObjectScaleZ(SceneObject item, double value) => item.Transform.ScaleZ = (int)Math.Round(value);

    /// <summary>
    /// 一行多选数值：全部相同显示可拖数值，不同显示横线输入框，输入后统一写入
    /// </summary>
    /// <param name="label">字段名</param>
    /// <param name="objects">目标对象</param>
    /// <param name="read">读取单个对象的数值</param>
    /// <param name="write">写入单个对象的数值</param>
    private void DrawMultiFloatRow(string label, SceneObject[] objects, Func<SceneObject, double> read,
        Action<SceneObject, double> write)
    {
        var values = objects.Select(read).ToArray();
        var first = values[0];
        var mixed = values.Skip(1).Any(value => Math.Abs(value - first) > .000001D);
        BeginInspectorField(label);
        if (mixed)
        {
            // 混合值：显示横线，点击后可以直接输入统一值
            if (!_multiMixedText.TryGetValue(label, out var text)) text = "—";
            if (ImGui.InputText("##" + label, ref text, 32))
            {
                _multiMixedText[label] = text;
                if (double.TryParse(text, out var parsed))
                {
                    foreach (var item in objects) write(item, parsed);
                    MarkSceneDocumentDirty();
                    RebuildObjectVoxelWorld();
                    _multiMixedText.Remove(label);
                }
            }
            else _multiMixedText[label] = text;
            return;
        }

        var value = (float)first;
        if (ImGui.DragFloat("##" + label, ref value, label.StartsWith("位置") ? 1F : .05F))
        {
            foreach (var item in objects) write(item, value);
            MarkSceneDocumentDirty();
            RebuildObjectVoxelWorld();
        }
    }
}
