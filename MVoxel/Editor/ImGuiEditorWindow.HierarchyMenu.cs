using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void DrawHierarchyCreateMenu(SceneObject? parent)
    {
        if (_sceneDocument is null) return;
        if (ImGui.MenuItem("创建空对象")) CreateHierarchyObject("空对象", parent, false);
        if (ImGui.MenuItem("创建体素模型")) CreateHierarchyObject("体素模型", parent, true);
        if (ImGui.MenuItem("创建相机"))
        {
            var item = CreateHierarchyObject("相机", parent, false);
            item.Components.Add(SceneComponentTypes.CreateCamera());
        }
        if (ImGui.BeginMenu("光源"))
        {
            foreach (var (name, shape) in new[] { ("点光源", LightShape.Point), ("聚光灯", LightShape.Spot), ("方向光", LightShape.Directional), ("面积灯", LightShape.Area) })
                if (ImGui.MenuItem(name))
                {
                    var item = CreateHierarchyObject(name, parent, false);
                    item.Components.Add(SceneComponentTypes.CreateLight(LightComponentData.Default with { Shape = shape }));
                }
            ImGui.EndMenu();
        }
    }

    private SceneObject CreateHierarchyObject(string name, SceneObject? parent, bool model)
    {
        var baseName = name;
        for (var suffix = 2; _sceneDocument!.Objects.Any(item => item.Name == name); suffix++) name = $"{baseName} {suffix}";
        var item = new SceneGraph(_sceneDocument).Add(name, parent?.Id,
            model ? TransformComponent.FromGrid(GridTransform.Identity) : TransformComponent.FromFree(FreeTransform.Identity));
        if (model)
        {
            item.Components.Add(SceneComponentTypes.CreateVoxelModel(string.Empty, 0, "手动创建", name));
            _objectVoxels.GetOrCreate(item.Id);
        }
        _selectedSceneObjectId = item.Id;
        MarkSceneDocumentDirty();
        return item;
    }
}
