using System.Text.Json;
using ImGuiNET;
using MVoxel.Animation;
using MVoxel.Scene;
using MVoxel.Core;
using MVoxel.Project;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static readonly JsonSerializerOptions AnimationJsonOptions = new() { WriteIndented = true };

    private void SaveAnimationAsset(string path, AnimationClip clip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(clip, AnimationJsonOptions));
    }

    private static string AnimationClipFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".mvanim.json", StringComparison.OrdinalIgnoreCase))
            return name[..^".mvanim.json".Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string EnsureAnimationClipExtension(string path) =>
        path.EndsWith(".mvanim.json", StringComparison.OrdinalIgnoreCase) ? path : path + ".mvanim.json";

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
        ImGui.Text("动画组件引用：");
        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(animation.ClipAsset) ? "未保存动画片段" : animation.ClipAsset);
        if (ImGui.Button("另存为动画片段…")) SaveAnimationClipAs(linked, animation);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("将当前动画片段保存为项目Assets/Animations中的文件，并让动画组件引用它");
        var frame = animation.CurrentFrame;
        if (ImGui.DragInt("当前帧", ref frame, 1F, 0, 100000)) animation.CurrentFrame = frame;
        var recording = animation.Recording;
        if (ImGui.Checkbox("录制模式", ref recording)) animation.Recording = recording;
        var playing = animation.Playing;
        if (ImGui.Checkbox("播放", ref playing)) animation.Playing = playing;
        var loop = animation.Loop;
        ImGui.SameLine();
        if (ImGui.Checkbox("循环", ref loop)) animation.Loop = loop;
        var speed = animation.Speed;
        if (ImGui.DragFloat("播放速度", ref speed, 0.05F, -8F, 8F, "%.2fx")) animation.Speed = Math.Clamp(speed, -8F, 8F);
        if (ImGui.Button("插入位置关键帧")) InsertAnimationKey(linked, animation, animation.CurrentFrame, "Transform", "Position");
        ImGui.SameLine();
        if (ImGui.Button("插入旋转关键帧")) InsertAnimationKey(linked, animation, animation.CurrentFrame, "Transform",
            linked.Transform.Mode == TransformMode.Grid ? "GridRotation" : "Quaternion");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("格点模型记录 90 度整数旋转，在关键帧之间保持上一个朝向。");
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
                    if (ImGui.DragInt("片段长度", ref length, 1F, 1, 100000)) { animationClip.LengthFrames = length; SaveAnimationAsset(path, animationClip); }
                    foreach (var key in animationClip.Keyframes.Where(item => item.Frame == animation.CurrentFrame).ToArray())
                    {
                        ImGui.Text($"{key.ComponentType}.{key.Parameter} · 第{key.Frame}帧");
                        var side = (int)key.TransitionSide;
                        if (ImGui.Combo("过渡方向", ref side, "左侧\\0右侧\\0左右\\0")) { key.TransitionSide = (AnimationTransitionSide)side; SaveAnimationAsset(path, animationClip); }
                        var mode = (int)key.RightCurve.Mode;
                        if (ImGui.Combo("右侧过渡", ref mode, "阶梯\\0线性\\0平滑\\0贝塞尔\\0")) { key.RightCurve.Mode = (AnimationInterpolation)mode; SaveAnimationAsset(path, animationClip); }
                        var leftTangent = key.LeftCurve.RightTangent;
                        if (ImGui.SliderFloat("左切线", ref leftTangent, -4F, 4F)) { key.LeftCurve.RightTangent = leftTangent; SaveAnimationAsset(path, animationClip); }
                        var rightTangent = key.RightCurve.LeftTangent;
                        if (ImGui.SliderFloat("右切线", ref rightTangent, -4F, 4F)) { key.RightCurve.LeftTangent = rightTangent; SaveAnimationAsset(path, animationClip); }
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
                SaveAnimationAsset(path, clip);
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

    private void SaveAnimationClipAs(SceneObject target, AnimationComponentData animation)
    {
        if (_project is null) return;
        var sourcePath = string.IsNullOrWhiteSpace(animation.ClipAsset)
            ? null
            : Path.Combine(_project.RootPath, animation.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
        var clip = sourcePath is not null && File.Exists(sourcePath)
            ? JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(sourcePath)) ?? new AnimationClip()
            : new AnimationClip { Name = target.Name + "动画" };
        var selected = NativeDialogs.SaveFile("保存动画片段", "动画片段\0*.mvanim.json\0全部文件\0*.*\0\0", ".mvanim.json", clip.Name);
        if (string.IsNullOrWhiteSpace(selected)) return;
        var animationsDirectory = Path.Combine(_project.AssetsPath, "Animations");
        var selectedPath = Path.GetFullPath(selected);
        var fullPath = EnsureAnimationClipExtension(selectedPath);
        if (!string.Equals(selectedPath, fullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            SetStatus("目标动画片段已存在，请使用完整文件名重新选择并确认覆盖");
            return;
        }
        var root = Path.GetFullPath(animationsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("动画片段必须保存到项目Assets/Animations目录");
            return;
        }
        clip.Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fullPath));
        SaveAnimationAsset(fullPath, clip);
        var relative = Path.GetRelativePath(_project.RootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
        animation.ClipAsset = relative;
        if (component is not null) SceneComponentTypes.WriteAnimation(component, animation);
        MarkSceneDocumentDirty();
        SetStatus($"已保存动画片段：{relative}");
    }

    private void InsertAnimationKey(SceneObject target, AnimationComponentData animation, int frame, string component, string parameter)
    {
        if (_project is null) return;
        var existingPath = string.IsNullOrWhiteSpace(animation.ClipAsset)
            ? null : Path.Combine(_project.RootPath, animation.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
        AnimationClip clip;
        try
        {
            clip = existingPath is not null && File.Exists(existingPath)
                ? JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(existingPath)) ?? new AnimationClip()
                : new AnimationClip { Name = target.Name + "动画" };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"读取动画片段失败，未写入关键帧：{exception.Message}");
            return;
        }
        var values = parameter switch
        {
            "Position" when target.Transform.Mode == MVoxel.Core.TransformMode.Free => AnimationValueCodec.FromVector(new System.Numerics.Vector3((float)target.Transform.Free.Position.X, (float)target.Transform.Free.Position.Y, (float)target.Transform.Free.Position.Z)),
            "Position" when target.Transform.Mode == MVoxel.Core.TransformMode.Grid => AnimationValueCodec.FromVector(new System.Numerics.Vector3(target.Transform.Grid.Position.X, target.Transform.Grid.Position.Y, target.Transform.Grid.Position.Z)),
            "Rotation" when target.Transform.Mode == MVoxel.Core.TransformMode.Free => AnimationValueCodec.FromVector(QuaternionToEuler(target.Transform.Free.Rotation)),
            "Quaternion" when target.Transform.Mode == TransformMode.Free =>
            [target.Transform.Free.Rotation.X, target.Transform.Free.Rotation.Y, target.Transform.Free.Rotation.Z, target.Transform.Free.Rotation.W],
            "GridRotation" when target.Transform.Mode == TransformMode.Grid =>
            [target.Transform.Grid.Rotation.M00, target.Transform.Grid.Rotation.M01, target.Transform.Grid.Rotation.M02,
             target.Transform.Grid.Rotation.M10, target.Transform.Grid.Rotation.M11, target.Transform.Grid.Rotation.M12,
             target.Transform.Grid.Rotation.M20, target.Transform.Grid.Rotation.M21, target.Transform.Grid.Rotation.M22],
            _ => [0F, 0F, 0F]
        };
        clip.Keyframes.RemoveAll(key => key.Frame == frame && key.ObjectId == target.Id.ToString() && key.ComponentType == component && key.Parameter == parameter);
        var keyframe = new AnimationKeyframe { Frame = frame, ObjectId = target.Id.ToString(), ComponentType = component, Parameter = parameter, Values = values };
        if (parameter == "GridRotation") keyframe.RightCurve.Mode = AnimationInterpolation.Step;
        clip.Keyframes.Add(keyframe);
        clip.LengthFrames = Math.Max(clip.LengthFrames, frame);
        var path = existingPath;
        if (path is null)
        {
            var directory = Path.Combine(_project.AssetsPath, "Animations");
            var baseName = string.Join("_", clip.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "动画片段";
            path = Path.Combine(directory, baseName + ".mvanim.json");
            for (var index = 2; File.Exists(path); index++) path = Path.Combine(directory, $"{baseName} {index}.mvanim.json");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SaveAnimationAsset(path, clip);
        AssetMetaStore.EnsureMeta(path);
        var relative = Path.GetRelativePath(_project.RootPath, path).Replace('\\', '/');
        animation.ClipAsset = relative;
        SceneComponentTypes.WriteAnimation(target.Components.First(SceneComponentTypes.IsAnimation), animation);
        MarkSceneDocumentDirty();
    }
}
