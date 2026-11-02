using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void RecordVoxelAnimationFrame(SceneObject target, int changedCellCount)
    {
        if (_project is null || changedCellCount <= 0) return;
        var animationComponent = target.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
        if (animationComponent is null) return;
        var animation = SceneComponentTypes.ReadAnimation(animationComponent);
        if (!animation.Recording || string.IsNullOrWhiteSpace(animation.ClipAsset)) return;
        var path = Path.Combine(_project.RootPath, animation.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
        var clip = File.Exists(path) ? JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)) ?? new AnimationClip() : new AnimationClip();
        clip.Normalize();
        clip.Keyframes.RemoveAll(key => key.Frame == animation.CurrentFrame && key.ObjectId == target.Id.ToString() && key.ComponentType == "VoxelModel");
        clip.Keyframes.Add(new AnimationKeyframe
        {
            Frame = animation.CurrentFrame,
            ObjectId = target.Id.ToString(),
            ComponentType = "VoxelModel",
            Parameter = "VoxelData",
            Values = [changedCellCount],
            LeftCurve = new AnimationCurve(),
            RightCurve = new AnimationCurve()
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus($"录制体素动画第{animation.CurrentFrame}帧：{changedCellCount:N0}格");
    }
}
