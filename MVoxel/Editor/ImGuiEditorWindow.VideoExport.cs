using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Project;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private sealed record VideoEncoding(VideoExportBatch Batch, CancellationTokenSource Cancellation)
    {
        /// <summary>编码收尾任务，退出时用于等待临时文件清理完成</summary>
        internal Task? Completion { get; set; }
    }
    private readonly List<VideoEncoding> _videoEncodings = [];

    /// <summary>视频导出启动结果，界面与自动化共用</summary>
    private sealed record VideoExportStart(bool Started, string OutputPath, int FrameRate, int FrameCount, string Message);

    /// <summary>一次视频导出的可查询状态，保留最近若干条供自动化读取</summary>
    private sealed class VideoExportInfo
    {
        internal required string OutputPath { get; init; }
        internal required string FramesDirectory { get; init; }
        internal required int FrameRate { get; init; }
        internal required int FrameCount { get; init; }
        internal required long StartedAt { get; init; }
        internal string? TimelinePath { get; init; }
        /// <summary>preparing / rendering / encoding / completed / failed / canceled</summary>
        internal string Phase { get; set; } = "preparing";
        internal int Completed { get; set; }
        internal string Message { get; set; } = string.Empty;
        internal long UpdatedAt { get; set; }
    }

    private const int MaximumVideoExportRecords = 20;
    private readonly List<VideoExportInfo> _videoExportInfos = [];

    private sealed class VideoExportBatch
    {
        internal required string ProjectRoot { get; init; }
        internal required string FramesDirectory { get; init; }
        internal required string OutputPath { get; init; }
        internal required TimelineAsset Timeline { get; init; }
        internal required string SceneJson { get; init; }
        internal required Dictionary<StableId, VoxelWorld> Worlds { get; init; }
        internal required Dictionary<string, AnimationClip> Clips { get; init; }
        internal required Dictionary<string, string> VoxelSnapshots { get; init; }
        internal StableId? CameraId { get; init; }
        internal required int FrameRate { get; init; }
        internal required int FrameCount { get; init; }
        internal int Completed { get; set; }
        internal ExportJob? Template { get; set; }
        /// <summary>批次中止后不再推进后续帧，也不会进入编码</summary>
        internal bool Aborted { get; set; }
        internal string AbortReason { get; set; } = string.Empty;
        /// <summary>对应的可查询状态记录，帧推进与结束时同步更新</summary>
        internal VideoExportInfo? Status { get; set; }
    }

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>把导出前的轨道完整性检查集中到一处，界面与自动化共用同一套判定</summary>
    private static List<string> CollectTimelineExportIssues(TimelineAsset timeline)
    {
        var issues = new List<string>();
        for (var index = 0; index < timeline.ActivationRanges.Count; index++)
        {
            var range = timeline.ActivationRanges[index];
            if (string.IsNullOrWhiteSpace(range.ObjectId)) issues.Add($"对象启用轨道第 {index + 1} 条尚未选择对象");
            if (range.EndFrame < range.StartFrame) issues.Add($"对象启用轨道第 {index + 1} 条结束帧早于起始帧");
        }
        for (var index = 0; index < timeline.Clips.Count; index++)
        {
            var clip = timeline.Clips[index];
            if (string.IsNullOrWhiteSpace(clip.ObjectId)) issues.Add($"动画片段轨道第 {index + 1} 条尚未选择对象");
            else if (string.IsNullOrWhiteSpace(clip.ClipAsset)) issues.Add($"动画片段轨道第 {index + 1} 条尚未选择片段");
            if (clip.EndFrame < clip.StartFrame) issues.Add($"动画片段轨道第 {index + 1} 条结束帧早于起始帧");
        }
        for (var index = 0; index < timeline.Events.Count; index++)
            if (string.IsNullOrWhiteSpace(timeline.Events[index].Action)) issues.Add($"条件事件第 {index + 1} 条尚未填写动作");
        return issues;
    }

    private VideoExportStart StartVideoExport(string outputPath, TimelineAsset sourceTimeline, int frameRate,
        string? timelinePath = null)
    {
        try
        {
            if (_project is null || _sceneDocument is null) throw new InvalidOperationException("请先打开项目和场景");
            var issues = CollectTimelineExportIssues(sourceTimeline);
            if (issues.Count > 0) throw new InvalidOperationException(issues[0]);
            var timeline = JsonSerializer.Deserialize<TimelineAsset>(JsonSerializer.Serialize(sourceTimeline))
                ?? throw new InvalidDataException("时间轴数据无效");
            timeline.Normalize();
            frameRate = Math.Clamp(frameRate, 1, 240);
            var frameCount = checked((int)Math.Ceiling(timeline.LengthFrames * (double)frameRate / timeline.FrameRate));
            var (size, scale) = ResolveExportCanvas();
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new InvalidOperationException("H.264 视频的宽和高必须为偶数，请调整导出分辨率");
            outputPath = Path.GetFullPath(outputPath);
            if (!outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outputPath += ".mp4";
            if (File.Exists(outputPath)) throw new IOException("目标视频已存在，请选择其他文件名");
            //同一个路径不能有两条待完成或正在编码的视频，否则后完成的会覆盖前者
            if (IsVideoOutputPending(outputPath)) throw new IOException("该路径已有视频任务在排队或编码，请选择其他文件名");
            var directory = Path.GetDirectoryName(outputPath) ?? throw new IOException("输出路径无效");
            var project = _project;
            var originalScene = _sceneDocument;
            var sceneJson = SceneSerializer.ToJson(originalScene);
            var sceneRevision = _historySceneRevision;
            var sourceWorlds = _objectVoxels.CaptureReferences();
            var sourceVersions = sourceWorlds.ToDictionary(item => item.Key, item => item.Value.ContentVersion);
            var cameraId = GetActiveRenderCameraObject()?.Id;
            var camera = GetRenderViewCamera();
            var mask = GetActiveRenderCullingMask();
            var cameraName = GetActiveRenderCameraObject()?.Name ?? "场景视角";
            var frameSettings = ResolveRenderSettings(GetActiveRenderCameraObject(), _projectSettings!.ExportRenderSettings);
            frameSettings.PathSamplesPerFrame = 1;
            RefreshRuntimeMaterialTable();
            var materials = MaterialAsset.PackTable(_runtimeMaterials);
            var framesDirectory = Path.Combine(directory, $".mvoxel-frames-{Guid.NewGuid():N}");
            var info = new VideoExportInfo
            {
                OutputPath = outputPath, FramesDirectory = framesDirectory, FrameRate = frameRate,
                FrameCount = frameCount, StartedAt = NowMilliseconds(), UpdatedAt = NowMilliseconds(),
                TimelinePath = string.IsNullOrWhiteSpace(timelinePath) ? null : timelinePath,
                Message = "正在准备动画资产和体素快照",
            };
            var started = _projectOperation.TryStart("准备视频导出", "正在准备动画资产和体素快照…",
                (cancellation, report) =>
                {
                    var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
                    var voxelSnapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var placement in timeline.Clips)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var clipPath = placement.ClipAsset.Replace('\\', '/');
                        if (clips.ContainsKey(clipPath)) continue;
                        var fullPath = Path.Combine(project.RootPath, clipPath.Replace('/', Path.DirectorySeparatorChar));
                        var clip = JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(fullPath))
                            ?? throw new InvalidDataException($"动画片段无效：{clipPath}");
                        clip.Normalize();
                        clips.Add(clipPath, clip);
                        foreach (var key in clip.Keyframes.Where(item => item.ComponentType == "VoxelModel" && item.Parameter == "VoxelData" && !string.IsNullOrWhiteSpace(item.SnapshotAsset)))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var snapshotPath = key.SnapshotAsset.Replace('\\', '/');
                            if (!voxelSnapshots.ContainsKey(snapshotPath))
                                voxelSnapshots.Add(snapshotPath, File.ReadAllText(Path.Combine(project.RootPath, snapshotPath.Replace('/', Path.DirectorySeparatorChar))));
                        }
                    }
                    var worlds = new Dictionary<StableId, VoxelWorld>();
                    foreach (var (id, world) in sourceWorlds)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress($"正在复制体素对象（{worlds.Count + 1}/{sourceWorlds.Count}）…",
                            .1F + .8F * worlds.Count / Math.Max(1, sourceWorlds.Count), true));
                        worlds.Add(id, world.Clone(cancellation));
                        if (world.ContentVersion != sourceVersions[id])
                            throw new InvalidOperationException("准备期间体素发生变化，请重新导出");
                    }
                    return new VideoExportBatch
                    {
                        ProjectRoot = project.RootPath, FramesDirectory = framesDirectory, OutputPath = outputPath,
                        Timeline = timeline, SceneJson = sceneJson, Worlds = worlds, Clips = clips,
                        VoxelSnapshots = voxelSnapshots, CameraId = cameraId, FrameRate = frameRate, FrameCount = frameCount,
                    };
                },
                batch =>
                {
                    var currentWorlds = _objectVoxels.CaptureReferences();
                    if (!ReferenceEquals(_project, project) || !ReferenceEquals(_sceneDocument, originalScene) ||
                        _historySceneRevision != sceneRevision || currentWorlds.Count != sourceWorlds.Count ||
                        sourceWorlds.Any(item => !currentWorlds.TryGetValue(item.Key, out var current) ||
                            !ReferenceEquals(current, item.Value) || current.ContentVersion != sourceVersions[item.Key]))
                    {
                        info.Phase = "failed";
                        info.Message = "准备期间场景发生变化，已中止视频导出";
                        info.UpdatedAt = NowMilliseconds();
                        SetStatus("准备期间场景发生变化，已中止视频导出，请重新导出");
                        return;
                    }
                    info.Phase = "rendering";
                    info.Message = "正在渲染视频帧";
                    info.UpdatedAt = NowMilliseconds();
                    var first = new ExportJob(Path.Combine(batch.FramesDirectory, "帧-000000.png"), frameSettings)
                    {
                        Camera = camera, CameraName = cameraName, CullingMask = mask, Video = batch,
                        TimelineFrame = 0, Size = size, Scale = scale, Materials = materials,
                    };
                    batch.Template = first;
                    batch.Status = info;
                    _exportQueue.Enqueue(first);
                    SetStatus($"已加入视频导出：{frameCount} 帧，{frameRate} 帧/秒");
                },
                exception =>
                {
                    info.Phase = "failed";
                    info.Message = exception.Message;
                    info.UpdatedAt = NowMilliseconds();
                    MVoxelLog.Error("准备视频导出失败", exception);
                    SetStatus($"无法开始视频导出：{exception.Message}");
                },
                () =>
                {
                    info.Phase = "canceled";
                    info.Message = "已取消准备视频导出";
                    info.UpdatedAt = NowMilliseconds();
                    SetStatus("已取消准备视频导出");
                });
            if (!started)
            {
                var message = "已有项目操作正在进行，请稍后重试";
                SetStatus(message);
                return new VideoExportStart(false, outputPath, frameRate, frameCount, message);
            }
            TrackVideoExport(info);
            SetStatus("正在后台准备视频导出…");
            return new VideoExportStart(true, outputPath, frameRate, frameCount, "已加入视频导出队列");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException or JsonException or ArgumentException)
        {
            SetStatus($"无法开始视频导出：{exception.Message}");
            return new VideoExportStart(false, outputPath, frameRate, 0, exception.Message);
        }
    }

    /// <summary>登记一条视频导出状态，超过上限时丢弃最早的记录</summary>
    private void TrackVideoExport(VideoExportInfo info)
    {
        var existing = _videoExportInfos.FindIndex(item => string.Equals(item.OutputPath, info.OutputPath, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _videoExportInfos.RemoveAt(existing);
        _videoExportInfos.Add(info);
        if (_videoExportInfos.Count <= MaximumVideoExportRecords) return;
        var oldest = _videoExportInfos.OrderBy(item => item.StartedAt).First();
        _videoExportInfos.Remove(oldest);
    }

    private VideoExportInfo? FindVideoExportInfo(string outputPath) =>
        _videoExportInfos.FirstOrDefault(item => string.Equals(item.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase));

    private static object DescribeVideoExport(VideoExportInfo info) => new
    {
        outputPath = info.OutputPath,
        phase = info.Phase,
        completedFrames = info.Completed,
        frameCount = info.FrameCount,
        frameRate = info.FrameRate,
        progress = info.FrameCount > 0 ? Math.Round(info.Completed / (double)info.FrameCount, 4) : 0D,
        message = info.Message,
        framesDirectory = info.FramesDirectory,
        timelinePath = info.TimelinePath,
        startedAt = info.StartedAt,
        updatedAt = info.UpdatedAt,
    };

    /// <summary>按输出路径查找仍在排队、渲染或编码的视频批次</summary>
    private VideoExportBatch? FindVideoBatch(string outputPath)
    {
        static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        if (_activeExport?.Video is { } active && Same(active.OutputPath, outputPath)) return active;
        foreach (var job in _exportQueue)
            if (job.Video is { } queued && Same(queued.OutputPath, outputPath)) return queued;
        foreach (var encoding in _videoEncodings)
            if (Same(encoding.Batch.OutputPath, outputPath)) return encoding.Batch;
        return null;
    }

    /// <summary>同一路径只允许一条视频任务排队或在编码，避免成片互相覆盖</summary>
    private bool IsVideoOutputPending(string outputPath) =>
        _videoEncodings.Any(item => string.Equals(item.Batch.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase)) ||
        _exportQueue.Any(job => job.Video is { } batch && string.Equals(batch.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase)) ||
        (_activeExport?.Video is { } active && string.Equals(active.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase));

    private void CompleteVideoFrame(ExportJob job, bool succeeded)
    {
        if (job.Video is not { } batch) return;
        if (batch.Aborted) return;
        if (!succeeded)
        {
            AbortVideoExport(batch, "视频帧渲染中止", failed: true);
            return;
        }
        batch.Completed++;
        if (batch.Status is { } progress) { progress.Completed = batch.Completed; progress.UpdatedAt = NowMilliseconds(); }
        if (batch.Completed < batch.FrameCount)
        {
            if (batch.Template is not { } template)
            {
                AbortVideoExport(batch, "视频导出缺少模板帧任务", failed: true);
                return;
            }
            var outputIndex = batch.Completed;
            var timelineFrame = (int)((long)outputIndex * batch.Timeline.FrameRate / batch.FrameRate);
            var next = new ExportJob(Path.Combine(batch.FramesDirectory, $"帧-{outputIndex:000000}.png"), template.Settings)
            {
                Camera = template.Camera, CameraName = template.CameraName, CullingMask = template.CullingMask,
                Video = batch, TimelineFrame = timelineFrame, Size = template.Size, Scale = template.Scale,
                Materials = template.Materials,
            };
            _exportQueue.Enqueue(next);
            SetStatus($"正在渲染视频：{batch.Completed}/{batch.FrameCount} 帧");
            return;
        }
        //编码前先核对帧数，缺帧直接中止，不产出不完整的成片
        if (!HasAllVideoFrames(batch, out var written))
        {
            AbortVideoExport(batch, $"视频帧不完整（应有 {batch.FrameCount} 帧，实际 {written} 帧），已停止编码", failed: true);
            return;
        }
        if (batch.Status is { } encodingInfo)
        {
            encodingInfo.Phase = "encoding";
            encodingInfo.Message = "正在编码 MP4";
            encodingInfo.UpdatedAt = NowMilliseconds();
        }
        SetStatus("视频帧渲染完成，正在后台编码 MP4…");
        var cancellation = new CancellationTokenSource();
        var encoding = new VideoEncoding(batch, cancellation);
        _videoEncodings.Add(encoding);
        encoding.Completion = VideoEncoder.EncodeAsync(batch.FramesDirectory, batch.OutputPath, 0, batch.FrameCount, batch.FrameRate, cancellation.Token)
            .ContinueWith(task =>
            {
                _pendingActions.Enqueue(() =>
                {
                    _videoEncodings.Remove(encoding);
                    if (batch.Status is { } info) info.UpdatedAt = NowMilliseconds();
                    if (task.IsCompletedSuccessfully)
                    {
                        if (batch.Status is { } done) { done.Phase = "completed"; done.Message = "视频已导出"; }
                        SetStatus($"视频已导出：{batch.OutputPath}");
                        TryDeleteVideoFrames(batch.FramesDirectory);
                    }
                    else if (task.IsCanceled)
                    {
                        var reason = batch.Aborted && batch.AbortReason.Length > 0 ? batch.AbortReason : "视频编码已取消";
                        if (batch.Status is { } canceled) { canceled.Phase = "canceled"; canceled.Message = reason; }
                        SetStatus($"{reason}；成片未生成，帧保留在 {batch.FramesDirectory}");
                    }
                    else
                    {
                        var error = task.Exception?.GetBaseException().Message ?? "未知错误";
                        if (batch.Status is { } failed) { failed.Phase = "failed"; failed.Message = error; }
                        MVoxelLog.Error($"视频编码失败：{error}");
                        SetStatus($"视频编码失败：{error}；帧保留在 {batch.FramesDirectory}");
                    }
                    cancellation.Dispose();
                });
            }, TaskScheduler.Default);
    }

    /// <summary>中止整个视频批次：停止后续帧、取消编码并报告帧目录</summary>
    private void AbortVideoExport(VideoExportBatch batch, string reason, bool failed)
    {
        if (batch.Aborted) return;
        batch.Aborted = true;
        batch.AbortReason = reason;
        if (batch.Status is { } info)
        {
            info.Phase = failed ? "failed" : "canceled";
            info.Message = reason;
            info.UpdatedAt = NowMilliseconds();
        }
        //已排队但未开始的同批帧任务不再渲染，避免断链后继续消耗时间
        if (_exportQueue.Count > 0)
            _exportQueue = new Queue<ExportJob>(_exportQueue.Where(item => !ReferenceEquals(item.Video, batch)));
        //正在渲染的那一帧不等它跑完，下一帧检查时会立即退出
        if (_activeExport?.Video is { } active && ReferenceEquals(active, batch)) _cancelActiveExportRequested = true;
        var cancelling = _videoEncodings.Where(item => ReferenceEquals(item.Batch, batch)).ToArray();
        foreach (var encoding in cancelling) encoding.Cancellation.Cancel();
        //编码进行中的情况由编码回调给出最终状态
        if (cancelling.Length == 0)
            SetStatus($"{reason}；视频未生成，已渲染的帧保留在 {batch.FramesDirectory}");
    }

    /// <summary>取消尚未开始的导出任务；视频帧取消等于中止整条视频</summary>
    private void CancelWaitingExportJob(ExportJob job)
    {
        if (job.Video is { } batch) AbortVideoExport(batch, "已取消排队中的视频帧", failed: false);
        else SetStatus($"已取消等待中的导出：{Path.GetFileName(job.Path)}");
    }

    /// <summary>退出前取消编码并限时等待清理，避免遗留半成品或继续占用输出文件</summary>
    private void CancelVideoEncodingsAndWait()
    {
        var encodings = _videoEncodings.ToArray();
        if (encodings.Length == 0) return;
        foreach (var encoding in encodings) encoding.Cancellation.Cancel();
        try
        {
            Task.WaitAll(encodings.Select(item => item.Completion).OfType<Task>().ToArray(), 5000);
        }
        catch (AggregateException)
        {
            //编码失败已由回调记录，退出阶段不重复抛出
        }
    }

    private static bool HasAllVideoFrames(VideoExportBatch batch, out int written)
    {
        written = Directory.Exists(batch.FramesDirectory)
            ? Directory.GetFiles(batch.FramesDirectory, "帧-*.png").Length : 0;
        return written >= batch.FrameCount;
    }

    private static void TryDeleteVideoFrames(string framesDirectory)
    {
        try
        {
            if (Directory.Exists(framesDirectory)) Directory.Delete(framesDirectory, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Error($"清理视频中间帧失败：{exception.Message}");
        }
    }
}
