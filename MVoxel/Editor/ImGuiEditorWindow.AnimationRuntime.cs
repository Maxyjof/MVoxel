using System.Numerics;
using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Scene;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private TimelineAsset? LoadTimelineRuntimeAsset(string asset)
    {
        if (_project is null || string.IsNullOrWhiteSpace(asset)) return null;
        var path = Path.Combine(_project.AssetsPath, asset.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        try
        {
            var timeline = JsonSerializer.Deserialize<TimelineAsset>(File.ReadAllText(path));
            timeline?.Normalize();
            return timeline;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 在显式导出或动画采样上下文中应用时间轴指定帧，不由普通渲染预览调用
    /// </summary>
    private bool ApplyTimelineFrame(string asset, int frame)
    {
        var timeline = LoadTimelineRuntimeAsset(asset);
        if (timeline is null || _sceneDocument is null) return false;
        frame = Math.Clamp(frame, 0, timeline.LengthFrames);
        foreach (var range in timeline.ActivationRanges)
        {
            if (!Guid.TryParse(range.ObjectId, out var objectId)) continue;
            var target = _sceneDocument.Objects.FirstOrDefault(item => item.Id.Value == objectId);
            if (target is not null) target.Visible = range.Enabled && frame >= range.StartFrame && frame <= range.EndFrame;
        }
        foreach (var placement in timeline.Clips)
        {
            if (!Guid.TryParse(placement.ObjectId, out var objectId)) continue;
            var target = _sceneDocument.Objects.FirstOrDefault(item => item.Id.Value == objectId);
            if (target is null) continue;
            var active = frame >= placement.StartFrame && frame <= placement.EndFrame;
            var component = target.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
            if (component is null || !active) continue;
            var animation = SceneComponentTypes.ReadAnimation(component);
            animation.Playing = true;
            animation.Loop = placement.Loop;
            animation.Speed = placement.Speed;
            animation.CurrentFrame = Math.Max(0, (int)MathF.Round((frame - placement.StartFrame) * placement.Speed));
            animation.PlaybackTimeSeconds = animation.CurrentFrame / (float)Math.Max(1, LoadAnimationClip(animation.ClipAsset)?.FrameRate ?? 30);
            SceneComponentTypes.WriteAnimation(component, animation);
            EvaluateAnimation(target, animation);
        }
        foreach (var marker in timeline.Events.Where(item => item.Frame == frame))
        {
            var target = _sceneDocument.Objects.FirstOrDefault(item => item.Id.ToString() == marker.TargetObjectId || item.Name == marker.TargetObjectId);
            if (target is null || !TimelineConditionMatches(marker.Condition, target)) continue;
            switch (marker.Action.Trim().ToLowerInvariant())
            {
                case "visible": target.Visible = true; break;
                case "hidden": target.Visible = false; break;
                case "animation.play": SetTimelineAnimationPlayback(target, true); break;
                case "animation.stop": SetTimelineAnimationPlayback(target, false); break;
            }
        }
        return true;
    }

    private static bool TimelineConditionMatches(string condition, SceneObject target) => condition.Trim().ToLowerInvariant() switch
    {
        "" or "always" => true,
        "visible" => target.Visible,
        "hidden" => !target.Visible,
        _ => false,
    };

    private static void SetTimelineAnimationPlayback(SceneObject target, bool playing)
    {
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsAnimation);
        if (component is null) return;
        var animation = SceneComponentTypes.ReadAnimation(component);
        animation.Playing = playing;
        SceneComponentTypes.WriteAnimation(component, animation);
    }

    private AnimationClip? LoadAnimationClip(string asset)
    {
        if (_project is null || string.IsNullOrWhiteSpace(asset)) return null;
        var path = Path.Combine(_project.RootPath, asset.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

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
        foreach (var animatedObject in EnumerateAnimationObjects(root))
        {
            ApplyTransformAnimation(animatedObject, clip, frame);
            var voxelKey = clip.Keyframes.Where(key => key.ObjectId == animatedObject.Id.ToString() && key.ComponentType == "VoxelModel" && key.Parameter == "VoxelData" && key.Frame <= frame).OrderBy(key => key.Frame).LastOrDefault();
            if (voxelKey is not null && !string.IsNullOrWhiteSpace(voxelKey.SnapshotAsset))
                ApplyVoxelAnimationSnapshot(animatedObject, voxelKey.SnapshotAsset);
        }
    }

    private IEnumerable<SceneObject> EnumerateAnimationObjects(SceneObject root)
    {
        yield return root;
        foreach (var child in _sceneDocument?.Objects.Where(item => item.ParentId == root.Id) ?? [])
        foreach (var item in EnumerateAnimationObjects(child)) yield return item;
    }

    private static void ApplyTransformAnimation(SceneObject target, AnimationClip clip, int frame)
    {
        var position = EvaluateVector(clip, target.Id.ToString(), "Position", frame);
        var rotation = EvaluateVector(clip, target.Id.ToString(), "Rotation", frame);
        var scale = EvaluateVector(clip, target.Id.ToString(), "Scale", frame);
        if (target.Transform.Mode != TransformMode.Free) return;
        var current = target.Transform.Free;
        var nextPosition = position is null ? new Vector3((float)current.Position.X, (float)current.Position.Y, (float)current.Position.Z) : new Vector3(MathF.Round(position.Value.X), MathF.Round(position.Value.Y), MathF.Round(position.Value.Z));
        var nextRotation = rotation is null ? current.Rotation : Quaternion.CreateFromYawPitchRoll(rotation.Value.Y, rotation.Value.X, rotation.Value.Z);
        target.Transform.SetFree(new FreeTransform(new Double3(nextPosition.X, nextPosition.Y, nextPosition.Z), nextRotation));
    }

    private static Vector3? EvaluateVector(AnimationClip clip, string objectId, string parameter, int frame)
    {
        var keys = clip.Keyframes.Where(key => key.ObjectId == objectId && key.ComponentType == "Transform" && key.Parameter == parameter).OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0) return null;
        var previous = keys.LastOrDefault(key => key.Frame <= frame) ?? keys[0];
        var next = keys.FirstOrDefault(key => key.Frame >= frame) ?? previous;
        var t = previous.Frame == next.Frame ? 0F : (frame - previous.Frame) / (float)(next.Frame - previous.Frame);
        if (previous.RightCurve.Mode == AnimationInterpolation.Step) t = 0F;
        return Vector3.Lerp(AnimationValueCodec.ToVector(previous.Values), AnimationValueCodec.ToVector(next.Values), t);
    }


    private void ApplyVoxelAnimationSnapshot(SceneObject root, string snapshotAsset)
    {
        if (_project is null) return;
        var path = Path.Combine(_project.RootPath, snapshotAsset.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var world = _objectVoxels.GetOrCreate(root.Id);
            foreach (var item in world.EnumerateOccupied().Select(item => item.Coordinate).ToArray()) world.Set(item, default);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var coordinate = new VoxelCoord64(item.GetProperty("x").GetInt64(), item.GetProperty("y").GetInt64(), item.GetProperty("z").GetInt64());
                var color = item.GetProperty("color");
                var rgba = new Rgba32(color.GetProperty("r").GetByte(), color.GetProperty("g").GetByte(), color.GetProperty("b").GetByte(), color.GetProperty("a").GetByte());
                world.Set(coordinate, new VoxelValue(rgba, item.GetProperty("material").GetUInt16(), item.GetProperty("flags").GetByte()));
            }
            InvalidateComposedWorldCache();
            _pipeline?.ResetAccumulation();
        }
        catch (JsonException) { }
    }
}
