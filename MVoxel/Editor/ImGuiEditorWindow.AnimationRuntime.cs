using System.Numerics;
using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Scene;
using MVoxel.Core;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void EvaluateAnimation(SceneObject root, AnimationComponentData animation)
    {
        if (!animation.Playing || _project is null || string.IsNullOrWhiteSpace(animation.ClipAsset)) return;
        var path = Path.Combine(_project.RootPath, animation.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return;
        AnimationClip? clip;
        try { clip = JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)); }
        catch (JsonException) { return; }
        if (clip is null || clip.Keyframes.Count == 0) return;
        clip.Normalize();
        var frame = Math.Clamp(animation.CurrentFrame, 0, clip.LengthFrames);
        var keys = clip.Keyframes.Where(key => key.ObjectId == root.Id.ToString() && key.ComponentType == "Transform" && key.Parameter == "Position").OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0) return;
        var previous = keys.LastOrDefault(key => key.Frame <= frame);
        var next = keys.FirstOrDefault(key => key.Frame >= frame);
        if (previous is null) previous = next;
        if (next is null) next = previous;
        if (previous is null || next is null) return;
        var t = previous.Frame == next.Frame ? 0F : (frame - previous.Frame) / (float)(next.Frame - previous.Frame);
        if (previous.TransitionSide == AnimationTransitionSide.Left && frame > previous.Frame) t = 0F;
        if (previous.TransitionSide == AnimationTransitionSide.Right && frame < next.Frame) t = 0F;
        if (previous.RightCurve.Mode == AnimationInterpolation.Step) t = 0F;
        var a = AnimationValueCodec.ToVector(previous.Values);
        var b = AnimationValueCodec.ToVector(next.Values);
        var value = Vector3.Lerp(a, b, t);
        value = new Vector3(MathF.Round(value.X), MathF.Round(value.Y), MathF.Round(value.Z));
        if (root.Transform.Mode == TransformMode.Free)
            root.Transform.SetFree(new FreeTransform(new Double3(value.X, value.Y, value.Z), root.Transform.Free.Rotation));
    }
}
