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
        if (ImGui.Button("插入当前帧")) InsertAnimationKey(linked, animation, animation.CurrentFrame, "Transform", "Position");
        ImGui.SameLine();
        ImGui.Text("参数默认隐藏，可在节点参数列表中点击显示");
        DrawAnimationParameterList(linked, animation);
        ImGui.SeparatorText("关键帧曲线");
        var clipPath = animation.ClipAsset;
        if (_project is not null && !string.IsNullOrWhiteSpace(clipPath))
        {
            var path = Path.Combine(_project.RootPath, clipPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                try
                {
                    var animationClip = JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)) ?? new AnimationClip();
                    animationClip.Normalize();
                    var length = animationClip.LengthFrames;
                    if (ImGui.DragInt("片段长度", ref length, 1F, 1, 100000)) { animationClip.LengthFrames = length; File.WriteAllText(path, JsonSerializer.Serialize(animationClip, new JsonSerializerOptions { WriteIndented = true })); }
                    foreach (var key in animationClip.Keyframes.Where(item => item.Frame == animation.CurrentFrame).ToArray())
                    {
                        ImGui.Text($"{key.ComponentType}.{key.Parameter} · 第{key.Frame}帧");
                        var side = (int)key.TransitionSide;
                        if (ImGui.Combo("过渡方向", ref side, "左侧\\0右侧\\0左右\\0")) { key.TransitionSide = (AnimationTransitionSide)side; File.WriteAllText(path, JsonSerializer.Serialize(animationClip, new JsonSerializerOptions { WriteIndented = true })); }
                        var mode = (int)key.RightCurve.Mode;
                        if (ImGui.Combo("右侧过渡", ref mode, "阶梯\\0线性\\0平滑\\0贝塞尔\\0")) { key.RightCurve.Mode = (AnimationInterpolation)mode; File.WriteAllText(path, JsonSerializer.Serialize(animationClip, new JsonSerializerOptions { WriteIndented = true })); }
                        var leftTangent = key.LeftCurve.RightTangent;
                        if (ImGui.SliderFloat("左切线", ref leftTangent, -4F, 4F)) key.LeftCurve.RightTangent = leftTangent;
                        var rightTangent = key.RightCurve.LeftTangent;
                        if (ImGui.SliderFloat("右切线", ref rightTangent, -4F, 4F)) key.RightCurve.LeftTangent = rightTangent;
                    }
                }
                catch (JsonException) { ImGui.TextDisabled("动画片段数据无效"); }
            }
        }
        SceneComponentTypes.WriteAnimation(animationComponent, animation);
        ImGui.End();
    }

    private void DrawAnimationHierarchy(SceneObject root)
    {
        var opened = ImGui.TreeNodeEx($"节点：{root.Name}", ImGuiTreeNodeFlags.DefaultOpen);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("动画对象层级，锁定动画对象后可继续编辑子节点");
        if (!opened) return;
        foreach (var child in _sceneDocument?.Objects.Where(item => item.ParentId == root.Id) ?? [])
            DrawAnimationHierarchy(child);
        ImGui.TreePop();
    }

    private void DrawAnimationParameterList(SceneObject root, AnimationComponentData animation)
    {
        ImGui.SeparatorText("可显示参数");
        if (_project is null || string.IsNullOrWhiteSpace(animation.ClipAsset)) return;
        var path = Path.Combine(_project.RootPath, animation.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
        var clip = File.Exists(path) ? JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)) ?? new AnimationClip() : new AnimationClip();
        clip.Normalize();
        foreach (var parameter in new[] { "Transform.Position", "Transform.Rotation", "Transform.Scale", "VoxelModel.Scale", "VoxelModel.Asset" })
        {
            var visible = clip.VisibleParameters.Contains(parameter);
            if (ImGui.Checkbox(parameter, ref visible))
            {
                if (visible) clip.VisibleParameters.Add(parameter); else clip.VisibleParameters.Remove(parameter);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static System.Numerics.Vector3 QuaternionToEuler(System.Numerics.Quaternion q)
    {
        var sinPitch = 2F * (q.W * q.X + q.Y * q.Z);
        var cosPitch = 1F - 2F * (q.X * q.X + q.Y * q.Y);
        var pitch = MathF.Atan2(sinPitch, cosPitch);
        var sinYaw = Math.Clamp(2F * (q.W * q.Y - q.Z * q.X), -1F, 1F);
        var yaw = MathF.Asin(sinYaw);
        var sinRoll = 2F * (q.W * q.Z + q.X * q.Y);
        var cosRoll = 1F - 2F * (q.Y * q.Y + q.Z * q.Z);
        return new System.Numerics.Vector3(pitch, yaw, MathF.Atan2(sinRoll, cosRoll));
    }

    private void InsertAnimationKey(SceneObject target, AnimationComponentData animation, int frame, string component, string parameter)
    {
        if (_project is null) return;
        var clip = new AnimationClip { Name = string.IsNullOrWhiteSpace(animation.ClipAsset) ? "动画片段" : Path.GetFileNameWithoutExtension(animation.ClipAsset) };
        var values = parameter switch
        {
            "Position" when target.Transform.Mode == MVoxel.Core.TransformMode.Free => AnimationValueCodec.FromVector(new System.Numerics.Vector3((float)target.Transform.Free.Position.X, (float)target.Transform.Free.Position.Y, (float)target.Transform.Free.Position.Z)),
            "Rotation" when target.Transform.Mode == MVoxel.Core.TransformMode.Free => AnimationValueCodec.FromVector(QuaternionToEuler(target.Transform.Free.Rotation)),
            _ => [0F, 0F, 0F]
        };
        clip.Keyframes.Add(new AnimationKeyframe { Frame = frame, ObjectId = target.Id.ToString(), ComponentType = component, Parameter = parameter, Values = values });
        var path = Path.Combine(_project.AssetsPath, "Animations", clip.Name + ".mvanim.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));
        SceneComponentTypes.WriteAnimation(target.Components.First(SceneComponentTypes.IsAnimation), animation with { ClipAsset = "Animations/" + Path.GetFileName(path) });
        MarkSceneDocumentDirty();
    }
}
