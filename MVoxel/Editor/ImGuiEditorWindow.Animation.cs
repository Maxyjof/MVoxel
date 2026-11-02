using System.Text.Json;
using ImGuiNET;
using MVoxel.Animation;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void DrawAnimationWindow()
    {
        if (!_showAnimationWindow) return;
        var open = _showAnimationWindow;
        if (!ImGui.Begin("动画##AnimationWindow", ref open, PanelWindowFlags)) { ImGui.End(); _showAnimationWindow = open; return; }
        _showAnimationWindow = open;
        ImGui.Checkbox("锁定动画对象", ref _animationWindowLocked);
        var linked = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _animationLinkedObjectId);
        if (!_animationWindowLocked && _selectedSceneObjectId is not null) linked = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId);
        if (linked is null)
        {
            ImGui.TextDisabled("请选择带动画组件的对象");
            ImGui.End();
            return;
        }
        if (!_animationWindowLocked) _animationLinkedObjectId = linked.Id;
        ImGui.Text($"根节点：{linked.Name}");
        DrawAnimationHierarchy(linked);
        var animationComponent = linked.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
        if (animationComponent is null)
        {
            ImGui.TextDisabled("该对象没有动画组件");
            ImGui.End();
            return;
        }
        var animation = SceneComponentTypes.ReadAnimation(animationComponent);
        ImGui.SeparatorText("动画片段");
        var clip = animation.ClipAsset;
        if (ImGui.InputText("片段资产", ref clip, 512)) animation.ClipAsset = clip;
        var frame = animation.CurrentFrame;
        if (ImGui.DragInt("当前帧", ref frame, 1F, 0, 100000)) animation.CurrentFrame = frame;
        var recording = animation.Recording;
        if (ImGui.Checkbox("录制模式", ref recording)) animation.Recording = recording;
        var playing = animation.Playing;
        if (ImGui.Checkbox("播放", ref playing)) animation.Playing = playing;
        if (ImGui.Button("插入当前帧")) InsertAnimationKey(linked, animation, 0, "Transform", "Position");
        ImGui.SameLine();
        ImGui.Text("参数默认隐藏，可在节点参数列表中点击显示");
        DrawAnimationParameterList(linked, animation);
        SceneComponentTypes.WriteAnimation(animationComponent, animation);
        ImGui.End();
    }

    private void DrawAnimationHierarchy(SceneObject root)
    {
        ImGui.TreeNodeEx($"根节点：{root.Name}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Leaf);
        foreach (var child in _sceneDocument?.Objects.Where(item => item.ParentId == root.Id) ?? [])
            ImGui.TreeNodeEx($"子节点：{child.Name}", ImGuiTreeNodeFlags.Leaf);
    }

    private void DrawAnimationParameterList(SceneObject root, AnimationComponentData animation)
    {
        ImGui.SeparatorText("可显示参数");
        foreach (var parameter in new[] { "Transform.Position", "Transform.Rotation", "Transform.Scale", "VoxelModel.Scale", "VoxelModel.Asset" })
        {
            var visible = animation.ClipAsset.Contains(parameter, StringComparison.Ordinal);
            if (ImGui.Checkbox(parameter, ref visible))
                SetStatus(visible ? $"已显示参数：{parameter}" : $"已隐藏参数：{parameter}");
        }
    }

    private void InsertAnimationKey(SceneObject target, AnimationComponentData animation, int frame, string component, string parameter)
    {
        if (_project is null) return;
        var clip = new AnimationClip { Name = string.IsNullOrWhiteSpace(animation.ClipAsset) ? "动画片段" : Path.GetFileNameWithoutExtension(animation.ClipAsset) };
        clip.Keyframes.Add(new AnimationKeyframe { Frame = frame, ObjectId = target.Id.ToString(), ComponentType = component, Parameter = parameter, Values = [0F, 0F, 0F] });
        var path = Path.Combine(_project.AssetsPath, "Animations", clip.Name + ".mvanim.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));
        SceneComponentTypes.WriteAnimation(target.Components.First(SceneComponentTypes.IsAnimation), animation with { ClipAsset = "Animations/" + Path.GetFileName(path) });
        MarkSceneDocumentDirty();
    }
}
