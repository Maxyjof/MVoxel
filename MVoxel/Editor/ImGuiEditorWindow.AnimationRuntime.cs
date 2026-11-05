using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Runtime.CompilerServices;
using MVoxel.Animation;
using MVoxel.Scene;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private SceneDocument? _timelinePreviewSource;
    private string _timelinePreviewSignature = string.Empty;
    private SceneDocument? _timelinePreviewScene;
    private Dictionary<StableId, VoxelWorld>? _timelinePreviewWorlds;
    private VideoExportBatch? _samplingVideoBatch;
    private string _timelinePreviewError = string.Empty;

    // 预览只在渲染调用期间替换场景引用，编辑、保存与撤销始终使用原场景。
    private bool RenderWithTimelinePreview(Func<bool> render)
    {
        //截图可临时指定时间轴与帧，优先级高于时间轴窗口里的当前值
        var timeline = _captureTimelineAsset ?? _timelineAsset;
        var frame = _captureTimelineFrame ?? _timelineFrame;
        if (_sceneDocument is null || _project is null ||
            (timeline.ActivationRanges.Count == 0 && timeline.Clips.Count == 0 && timeline.Events.Count == 0))
            return render();
        var originalScene = _sceneDocument;
        var originalWorlds = _objectVoxels.CaptureReferences();
        try
        {
            // 与编辑历史使用同一场景修订号：未编辑时不必为两个视口每帧序列化整个场景。
            var signature = new StringBuilder().Append(_historySceneRevision).Append('|').Append(JsonSerializer.Serialize(timeline)).Append('|').Append(frame);
            foreach (var (id, world) in originalWorlds.OrderBy(item => item.Key.ToString()))
                signature.Append('|').Append(id).Append(':').Append(RuntimeHelpers.GetHashCode(world)).Append(':').Append(world.ContentVersion);
            foreach (var clip in timeline.Clips)
            {
                if (string.IsNullOrWhiteSpace(clip.ClipAsset)) continue;
                var path = Path.Combine(_project.RootPath, clip.ClipAsset.Replace('/', Path.DirectorySeparatorChar));
                signature.Append('|').Append(path).Append(':').Append(File.GetLastWriteTimeUtc(path).Ticks);
            }
            var key = signature.ToString();
            if (!ReferenceEquals(originalScene, _timelinePreviewSource) || key != _timelinePreviewSignature || _timelinePreviewScene is null || _timelinePreviewWorlds is null)
            {
                var scene = SceneSerializer.LoadFromJson(SceneSerializer.ToJson(originalScene));
                _sceneDocument = scene;
                _objectVoxels.RestoreReferences(originalWorlds);
                ApplyTimelineFrame(timeline, frame);
                _timelinePreviewScene = scene;
                _timelinePreviewWorlds = _objectVoxels.CaptureReferences();
                _timelinePreviewSource = originalScene;
                _timelinePreviewSignature = key;
            }
            _sceneDocument = _timelinePreviewScene;
            _objectVoxels.RestoreReferences(_timelinePreviewWorlds);
            _timelinePreviewError = string.Empty;
            return render();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        {
            _sceneDocument = originalScene;
            _objectVoxels.RestoreReferences(originalWorlds);
            var message = $"时间轴预览失败，已显示编辑场景：{exception.Message}";
            if (_timelinePreviewError != message)
            {
                _timelinePreviewError = message;
                MVoxelLog.Error(message, exception);
                SetStatus(message);
            }
            return render();
        }
        finally
        {
            _sceneDocument = originalScene;
            _objectVoxels.RestoreReferences(originalWorlds);
        }
    }

    // 时间轴导出采样在独立场景和体素副本上进行，不能修改用户正在编辑的场景。
    private bool CaptureTimelineExportFrame(ExportJob job, string? asset)
    {
        if (_sceneDocument is null) return false;
        var batch = job.Video;
        if (batch is not null && (_project is null || !string.Equals(_project.RootPath, batch.ProjectRoot, StringComparison.OrdinalIgnoreCase))) return false;
        var timeline = batch?.Timeline ?? (asset is null ? null : LoadTimelineRuntimeAsset(asset));
        if (timeline is null) return false;
        var originalScene = _sceneDocument;
        var originalWorlds = _objectVoxels.CaptureReferences();
        var sampledScene = SceneSerializer.LoadFromJson(batch?.SceneJson ?? SceneSerializer.ToJson(originalScene));
        // 采样时仅在实际写入某个对象时复制其体素，其他对象可安全共享只读引用。
        var sampledWorlds = new Dictionary<StableId, VoxelWorld>(batch?.Worlds ?? originalWorlds);
        var previousBatch = _samplingVideoBatch;
        try
        {
            _samplingVideoBatch = batch;
            _sceneDocument = sampledScene;
            _objectVoxels.RestoreReferences(sampledWorlds);
            InvalidateComposedWorldCache();
            if (!ApplyTimelineFrame(timeline, job.TimelineFrame)) return false;
            InvalidateComposedWorldCache();
            var mask = job.CullingMask ?? uint.MaxValue;
            job.World = GetRenderWorld(mask);
            job.WeatherTime = batch is null ? job.TimelineFrame / (float)Math.Max(1, timeline.FrameRate)
                : batch.Completed / (float)batch.FrameRate;
            if (batch is not null) job.WaterTime = job.WeatherTime;
            job.SceneLights = CollectSceneLights(mask);
            if (batch?.CameraId is { } cameraId && sampledScene.Objects.FirstOrDefault(item => item.Id == cameraId) is { } cameraObject)
                job.Camera = ToSceneCamera(cameraObject);
            job.SurfaceDeposits = CollectSurfaceDeposits(mask);
            job.ReceiverLayers = NeedsReceiverLayers(job.SurfaceDeposits)
                ? _objectVoxels.CaptureReceiverLayers(sampledScene, mask, false) : null;
            job.ReceiverTable = null;
            return true;
        }
        finally
        {
            _sceneDocument = originalScene;
            _samplingVideoBatch = previousBatch;
            _objectVoxels.RestoreReferences(originalWorlds);
            InvalidateComposedWorldCache();
        }
    }

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
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 在显式导出或动画采样上下文中应用时间轴指定帧，不由普通渲染预览调用
    /// </summary>
    private bool ApplyTimelineFrame(string asset, int frame)
    {
        var timeline = LoadTimelineRuntimeAsset(asset);
        return timeline is not null && ApplyTimelineFrame(timeline, frame);
    }

    private bool ApplyTimelineFrame(TimelineAsset timeline, int frame)
    {
        if (_sceneDocument is null) return false;
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
            if (!active) continue;
            var animation = component is null ? new AnimationComponentData() : SceneComponentTypes.ReadAnimation(component);
            animation.ClipAsset = placement.ClipAsset;
            var clip = LoadAnimationClip(animation.ClipAsset);
            if (clip is null) continue;
            clip.Normalize();
            animation.Playing = true;
            animation.Loop = placement.Loop;
            animation.Speed = placement.Speed;
            var clipFrame = (frame - placement.StartFrame) * placement.Speed * clip.FrameRate / Math.Max(1F, timeline.FrameRate);
            if (placement.Speed < 0) clipFrame += clip.LengthFrames;
            animation.CurrentFrame = placement.Loop
                ? ((int)MathF.Floor(clipFrame) % Math.Max(1, clip.LengthFrames) + Math.Max(1, clip.LengthFrames)) % Math.Max(1, clip.LengthFrames)
                : Math.Clamp((int)MathF.Round(clipFrame), 0, clip.LengthFrames);
            animation.PlaybackTimeSeconds = animation.CurrentFrame / (float)Math.Max(1, clip.FrameRate);
            if (component is not null) SceneComponentTypes.WriteAnimation(component, animation);
            EvaluateAnimation(target, animation);
        }
        foreach (var marker in timeline.Events.Where(item => item.Frame <= frame).OrderBy(item => item.Frame))
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
        if (_samplingVideoBatch is { } batch)
            return batch.Clips.GetValueOrDefault(asset.Replace('\\', '/'))
                ?? throw new InvalidDataException($"导出快照缺少动画片段：{asset}");
        var path = Path.Combine(_project.RootPath, asset.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到动画片段：{asset}", path);
        try { return JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path)) ?? throw new InvalidDataException($"动画片段内容为空：{asset}"); }
        catch (JsonException exception) { throw new InvalidDataException($"动画片段格式无效：{asset}", exception); }
    }

    private void EvaluateAnimation(SceneObject root, AnimationComponentData animation)
    {
        if (!animation.Playing || _project is null || string.IsNullOrWhiteSpace(animation.ClipAsset)) return;
        var clip = LoadAnimationClip(animation.ClipAsset);
        if (clip is null || clip.Keyframes.Count == 0) return;
        clip.Normalize();
        var frame = Math.Clamp(animation.CurrentFrame, 0, clip.LengthFrames);
        foreach (var animatedObject in EnumerateAnimationObjects(root))
        {
            var voxelKey = clip.Keyframes.Where(key => key.ObjectId == animatedObject.Id.ToString() && key.ComponentType == "VoxelModel" && key.Parameter == "VoxelData" && key.Frame <= frame).OrderBy(key => key.Frame).LastOrDefault();
            if (voxelKey is not null && !string.IsNullOrWhiteSpace(voxelKey.SnapshotAsset))
                ApplyVoxelAnimationSnapshot(animatedObject, voxelKey.SnapshotAsset);
            ApplyTransformAnimation(animatedObject, clip, frame);
        }
    }

    private IEnumerable<SceneObject> EnumerateAnimationObjects(SceneObject root)
    {
        yield return root;
        foreach (var child in _sceneDocument?.Objects.Where(item => item.ParentId == root.Id) ?? [])
        foreach (var item in EnumerateAnimationObjects(child)) yield return item;
    }

    private void ApplyTransformAnimation(SceneObject target, AnimationClip clip, int frame)
    {
        var position = EvaluateVector(clip, target.Id.ToString(), "Position", frame);
        var rotation = EvaluateVector(clip, target.Id.ToString(), "Rotation", frame);
        if (target.Transform.Mode == TransformMode.Grid)
        {
            var currentGrid = target.Transform.Grid;
            var newPosition = position is null ? currentGrid.Position : new VoxelCoord64((long)MathF.Round(position.Value.X), (long)MathF.Round(position.Value.Y), (long)MathF.Round(position.Value.Z));
            var newRotation = EvaluateGridRotation(clip, target.Id.ToString(), frame) ?? currentGrid.Rotation;
            if ((newPosition != currentGrid.Position || !newRotation.Equals(currentGrid.Rotation)) && _objectVoxels.TryGetWorld(target.Id, out var source))
            {
                var deltaRotation = newRotation.Compose(currentGrid.Rotation.Inverse());
                var moved = new VoxelWorld();
                foreach (var (coordinate, value) in source.EnumerateOccupied())
                    moved.Set(newPosition + deltaRotation.Apply(coordinate - currentGrid.Position), value);
                _objectVoxels.SetWorld(target.Id, moved);
            }
            target.Transform.SetGrid(new GridTransform(newPosition, newRotation));
            return;
        }
        var current = target.Transform.Free;
        var nextPosition = position is null ? new Vector3((float)current.Position.X, (float)current.Position.Y, (float)current.Position.Z) : position.Value;
        var nextRotation = EvaluateQuaternion(clip, target.Id.ToString(), frame) ??
            (rotation is null ? current.Rotation : Quaternion.CreateFromYawPitchRoll(rotation.Value.Y, rotation.Value.X, rotation.Value.Z));
        target.Transform.SetFree(new FreeTransform(new Double3(nextPosition.X, nextPosition.Y, nextPosition.Z), nextRotation));
    }

    private static OrthogonalRotation? EvaluateGridRotation(AnimationClip clip, string objectId, int frame)
    {
        var keys = clip.Keyframes.Where(key => key.ObjectId == objectId && key.ComponentType == "Transform" && key.Parameter == "GridRotation")
            .OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0) return null;
        var values = (keys.LastOrDefault(key => key.Frame <= frame) ?? keys[0]).Values;
        if (values.Length != 9 || values.Any(value => !float.IsFinite(value) || value != MathF.Round(value) || value < -1 || value > 1)) return null;
        try
        {
            return new OrthogonalRotation((sbyte)values[0], (sbyte)values[1], (sbyte)values[2],
                (sbyte)values[3], (sbyte)values[4], (sbyte)values[5], (sbyte)values[6], (sbyte)values[7], (sbyte)values[8]);
        }
        catch (ArgumentException) { return null; }
    }

    private static Quaternion? EvaluateQuaternion(AnimationClip clip, string objectId, int frame)
    {
        var keys = clip.Keyframes.Where(key => key.ObjectId == objectId && key.ComponentType == "Transform" && key.Parameter == "Quaternion" &&
            key.Values.Length == 4 && key.Values.All(float.IsFinite)).OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0) return null;
        var previous = keys.LastOrDefault(key => key.Frame <= frame) ?? keys[0];
        var next = keys.FirstOrDefault(key => key.Frame >= frame) ?? previous;
        static Quaternion Read(float[] v) => new(v[0], v[1], v[2], v[3]);
        var a = Read(previous.Values); var b = Read(next.Values);
        if (a.LengthSquared() < 1e-8F || b.LengthSquared() < 1e-8F) return null;
        var t = previous.Frame == next.Frame || previous.RightCurve.Mode == AnimationInterpolation.Step
            ? 0F : (frame - previous.Frame) / (float)(next.Frame - previous.Frame);
        return Quaternion.Normalize(Quaternion.Slerp(Quaternion.Normalize(a), Quaternion.Normalize(b), Math.Clamp(t, 0F, 1F)));
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
        try
        {
            string json;
            if (_samplingVideoBatch is { } batch)
            {
                if (!batch.VoxelSnapshots.TryGetValue(snapshotAsset.Replace('\\', '/'), out var snapshot))
                    throw new InvalidDataException($"导出快照缺少体素动画数据：{snapshotAsset}");
                json = snapshot;
            }
            else
            {
                var path = Path.Combine(_project.RootPath, snapshotAsset.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new FileNotFoundException($"找不到体素动画数据：{snapshotAsset}", path);
                json = File.ReadAllText(path);
            }
            using var document = JsonDocument.Parse(json);
            var world = new VoxelWorld();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var coordinate = new VoxelCoord64(item.GetProperty("x").GetInt64(), item.GetProperty("y").GetInt64(), item.GetProperty("z").GetInt64());
                var color = item.GetProperty("color");
                var rgba = new Rgba32(color.GetProperty("r").GetByte(), color.GetProperty("g").GetByte(), color.GetProperty("b").GetByte(), color.GetProperty("a").GetByte());
                world.Set(coordinate, new VoxelValue(rgba, item.GetProperty("material").GetUInt16(), item.GetProperty("flags").GetByte()));
            }
            _objectVoxels.SetWorld(root.Id, world);
        }
        catch (JsonException exception) { throw new InvalidDataException($"体素动画数据格式无效：{snapshotAsset}", exception); }
    }
}
