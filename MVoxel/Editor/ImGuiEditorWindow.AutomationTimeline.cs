using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Core;
using MVoxel.Project;

namespace MVoxel.Editor;

/// <summary>
/// 时间轴与视频导出的自动化入口：让 MCP 能编排时间轴、定位预览帧并安全地导出视频，
/// 与界面走同一套字段、校验和导出队列
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>自动化读写时间轴时统一使用大小写不敏感解析，兼容 camelCase 与 PascalCase</summary>
    private static readonly JsonSerializerOptions TimelineAutomationJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static int? ReadInteger(JsonElement arguments, string name) =>
        ReadNumber(arguments, name) is { } value ? (int)Math.Round(value) : null;

    /// <summary>拒绝未登记的参数，避免拼错字段却静默生效</summary>
    private static void RequireAutomationArguments(JsonElement arguments, string subject, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须是对象");
        foreach (var property in arguments.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"不支持的{subject}参数：{property.Name}");
    }

    private static TimelineAsset CloneTimeline(TimelineAsset source)
    {
        var copy = JsonSerializer.Deserialize<TimelineAsset>(JsonSerializer.Serialize(source), TimelineAutomationJson)
            ?? new TimelineAsset();
        copy.Normalize();
        return copy;
    }

    /// <summary>把时间轴写入指定的 Assets 内文件，返回相对路径</summary>
    private string WriteTimelineAsset(TimelineAsset timeline, string fullPath)
    {
        var project = RequireProject();
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        AtomicFile.WriteText(fullPath, JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));
        AssetMetaStore.EnsureMeta(fullPath);
        return Path.GetRelativePath(project.AssetsPath, fullPath).Replace('\\', '/');
    }

    /// <summary>把时间轴应用回窗口；saved 为真时同步已保存快照</summary>
    private void ApplyTimelineToWindow(TimelineAsset timeline, string? relativePath, bool saved)
    {
        _timelineAsset = timeline;
        if (!string.IsNullOrWhiteSpace(relativePath)) _timelineAssetPath = relativePath;
        _timelineFrame = Math.Clamp(_timelineFrame, 0, Math.Max(1, timeline.LengthFrames));
        _timelineWeatherFrame = _timelineFrame;
        _showTimelineWindow = true;
        if (saved) _timelineSavedState = JsonSerializer.Serialize(timeline);
    }

    /// <summary>时间轴窗口状态与轨道的只读摘要</summary>
    private object DescribeTimelineState() => new
    {
        path = string.IsNullOrWhiteSpace(_timelineAssetPath) ? null : _timelineAssetPath,
        temporary = string.IsNullOrWhiteSpace(_timelineAssetPath),
        dirty = TimelineIsDirty,
        name = _timelineAsset.Name,
        frameRate = _timelineAsset.FrameRate,
        lengthFrames = _timelineAsset.LengthFrames,
        durationSeconds = Math.Round(_timelineAsset.LengthFrames / (double)Math.Max(1, _timelineAsset.FrameRate), 3),
        currentFrame = _timelineFrame,
        currentSeconds = Math.Round(_timelineFrame / (double)Math.Max(1, _timelineAsset.FrameRate), 3),
        playing = _timelineWeatherPlaying,
        activationCount = _timelineAsset.ActivationRanges.Count,
        clipCount = _timelineAsset.Clips.Count,
        eventCount = _timelineAsset.Events.Count,
    };

    /// <summary>时间轴是否可用作视频导出，与界面导出前的判定一致</summary>
    private static object DescribeTimelineValidation(TimelineAsset timeline)
    {
        var issues = CollectTimelineExportIssues(timeline);
        return new
        {
            valid = issues.Count == 0,
            issueCount = issues.Count,
            issues,
            frameRate = timeline.FrameRate,
            lengthFrames = timeline.LengthFrames,
        };
    }

    /// <summary>用给出的时间轴对象替换窗口内容，可另存为资产</summary>
    private object SetTimelineAsset(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "timeline", "path", "save", "overwrite");
        if (!arguments.TryGetProperty("timeline", out var node) || node.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("timeline 参数必须是时间轴对象");
        var timeline = node.Deserialize<TimelineAsset>(TimelineAutomationJson)
            ?? throw new ArgumentException("timeline 无法解析为时间轴");
        timeline.Normalize();
        var relativePath = ReadString(arguments, "path");
        var save = ReadBoolean(arguments, "save") ?? true;
        var overwrite = ReadBoolean(arguments, "overwrite") ?? false;
        var project = RequireProject();
        //给了路径就让窗口指向它；只有 save 为真才真正写盘
        string? targetFull = null;
        if (!string.IsNullOrWhiteSpace(relativePath)) targetFull = ResolveTimelinePath(relativePath);
        else if (save)
        {
            if (string.IsNullOrWhiteSpace(_timelineAssetPath))
                throw new InvalidOperationException("当前是临时时间轴，请传 path 指定保存位置");
            targetFull = ResolveTimelinePath(_timelineAssetPath);
        }
        var targetRelative = targetFull is null
            ? null : Path.GetRelativePath(project.AssetsPath, targetFull).Replace('\\', '/');
        var saved = false;
        if (save && targetFull is not null)
        {
            var current = string.IsNullOrWhiteSpace(_timelineAssetPath) ? null : ResolveTimelinePath(_timelineAssetPath);
            var sameAsCurrent = current is not null && string.Equals(current, targetFull, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(targetFull) && !sameAsCurrent && !overwrite)
                throw new InvalidOperationException("目标时间轴已存在，如需覆盖请传 overwrite=true");
            targetRelative = WriteTimelineAsset(timeline, targetFull);
            saved = true;
        }
        ApplyTimelineToWindow(timeline, targetRelative, saved);
        var state = DescribeTimelineState();
        return new
        {
            saved,
            path = targetRelative,
            state,
            timeline = _timelineAsset,
            warnings = CollectTimelineExportIssues(_timelineAsset),
        };
    });

    /// <summary>局部修改帧率、总时长或轨道，可追加与删除单条轨道</summary>
    private object UpdateTimelineAsset(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "frameRate", "lengthFrames", "durationSeconds", "name",
            "activationRanges", "clips", "events", "addActivation", "addClip", "addEvent",
            "removeActivation", "removeClip", "removeEvent", "save", "path", "overwrite");
        var timeline = CloneTimeline(_timelineAsset);
        if (ReadNumber(arguments, "frameRate") is { } frameRate) timeline.FrameRate = (int)Math.Round(frameRate);
        timeline.FrameRate = Math.Clamp(timeline.FrameRate, 1, 240);
        //改帧率后按秒数换算，保持原有时间跨度
        if (ReadNumber(arguments, "durationSeconds") is { } seconds)
            timeline.LengthFrames = (int)Math.Round(seconds * timeline.FrameRate);
        if (ReadNumber(arguments, "lengthFrames") is { } lengthFrames) timeline.LengthFrames = (int)Math.Round(lengthFrames);
        if (ReadString(arguments, "name") is { } name) timeline.Name = name;
        if (TryReadActivationArray(arguments, "activationRanges") is { } ranges) timeline.ActivationRanges = ranges;
        if (TryReadClipArray(arguments, "clips") is { } clips) timeline.Clips = clips;
        if (TryReadEventArray(arguments, "events") is { } events) timeline.Events = events;
        if (TryReadObject<TimelineActivationRange>(arguments, "addActivation") is { } addedRange) timeline.ActivationRanges.Add(addedRange);
        if (TryReadObject<TimelineClipPlacement>(arguments, "addClip") is { } addedClip) timeline.Clips.Add(addedClip);
        if (TryReadObject<TimelineEventMarker>(arguments, "addEvent") is { } addedEvent) timeline.Events.Add(addedEvent);
        RemoveTrackAt(timeline.ActivationRanges, ReadInteger(arguments, "removeActivation"), "对象启用轨道");
        RemoveTrackAt(timeline.Clips, ReadInteger(arguments, "removeClip"), "动画片段轨道");
        RemoveTrackAt(timeline.Events, ReadInteger(arguments, "removeEvent"), "条件事件");
        timeline.Normalize();
        var save = ReadBoolean(arguments, "save") ?? false;
        var relativePath = ReadString(arguments, "path");
        var project = RequireProject();
        //默认不落盘；给了 path 等同于另存
        string? targetFull = null;
        if (!string.IsNullOrWhiteSpace(relativePath)) targetFull = ResolveTimelinePath(relativePath);
        else if (save)
        {
            if (string.IsNullOrWhiteSpace(_timelineAssetPath))
                throw new InvalidOperationException("当前是临时时间轴，请传 path 指定保存位置");
            targetFull = ResolveTimelinePath(_timelineAssetPath);
        }
        var targetRelative = targetFull is null
            ? null : Path.GetRelativePath(project.AssetsPath, targetFull).Replace('\\', '/');
        var saved = false;
        if (targetFull is not null)
        {
            var current = string.IsNullOrWhiteSpace(_timelineAssetPath) ? null : ResolveTimelinePath(_timelineAssetPath);
            var sameAsCurrent = current is not null && string.Equals(current, targetFull, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(targetFull) && !sameAsCurrent && !(ReadBoolean(arguments, "overwrite") ?? false))
                throw new InvalidOperationException("目标时间轴已存在，如需覆盖请传 overwrite=true");
            targetRelative = WriteTimelineAsset(timeline, targetFull);
            saved = true;
        }
        ApplyTimelineToWindow(timeline, targetRelative, saved);
        var state = DescribeTimelineState();
        return new
        {
            saved,
            path = targetRelative,
            state,
            timeline = _timelineAsset,
            warnings = CollectTimelineExportIssues(_timelineAsset),
        };
    });

    private List<TimelineActivationRange>? TryReadActivationArray(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<List<TimelineActivationRange>>(TimelineAutomationJson) : null;

    private List<TimelineClipPlacement>? TryReadClipArray(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<List<TimelineClipPlacement>>(TimelineAutomationJson) : null;

    private List<TimelineEventMarker>? TryReadEventArray(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<List<TimelineEventMarker>>(TimelineAutomationJson) : null;

    private static T? TryReadObject<T>(JsonElement arguments, string name) where T : class =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize<T>(TimelineAutomationJson) : null;

    private static void RemoveTrackAt<T>(List<T> tracks, int? index, string label)
    {
        if (index is not { } target) return;
        if (target < 0 || target >= tracks.Count) throw new ArgumentException($"{label}索引超出范围：{target}");
        tracks.RemoveAt(target);
    }

    /// <summary>定位播放头并暂停预览；frame 与 seconds 二选一</summary>
    private object SeekTimeline(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "frame", "seconds");
        var frame = ReadInteger(arguments, "frame") ??
            (ReadNumber(arguments, "seconds") is { } seconds ? (int)Math.Round(seconds * _timelineAsset.FrameRate) : (int?)null);
        if (frame is null) throw new ArgumentException("需要 frame 或 seconds");
        _timelineWeatherPlaying = false;
        _timelineFrame = Math.Clamp(frame.Value, 0, _timelineAsset.LengthFrames);
        _timelineWeatherFrame = _timelineFrame;
        return DescribeTimelineState();
    });

    /// <summary>开始或暂停预览；播放依赖时间轴窗口推进</summary>
    private object SetTimelinePlaying(bool playing) => RunOnUi(() =>
    {
        _showTimelineWindow = true;
        if (playing && _timelineFrame >= _timelineAsset.LengthFrames) _timelineFrame = 0;
        _timelineWeatherFrame = _timelineFrame;
        _timelineWeatherPlaying = playing;
        return DescribeTimelineState();
    });

    /// <summary>按路径、给定对象或窗口内容校验时间轴</summary>
    private object ValidateTimelineAsset(JsonElement arguments) => RunOnUi(() =>
    {
        RequireTimelineArguments(arguments, "path", "timeline");
        TimelineAsset timeline;
        if (arguments.TryGetProperty("timeline", out var node) && node.ValueKind == JsonValueKind.Object)
            timeline = node.Deserialize<TimelineAsset>(TimelineAutomationJson)
                ?? throw new ArgumentException("timeline 无法解析为时间轴");
        else if (ReadString(arguments, "path") is { } relativePath)
        {
            ResolveTimelinePath(relativePath);
            timeline = LoadTimelineRuntimeAsset(relativePath)
                ?? throw new FileNotFoundException("时间轴资产无法读取", relativePath);
        }
        else timeline = CloneTimeline(_timelineAsset);
        timeline.Normalize();
        return DescribeTimelineValidation(timeline);
    });

    /// <summary>启动视频导出：省略 timelinePath 时使用窗口当前时间轴</summary>
    private object ExportVideoFromArguments(JsonElement arguments) => RunOnUi(() =>
    {
        RequireAutomationArguments(arguments, "视频导出", "path", "timelinePath", "frameRate");
        var outputPath = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("缺少输出视频路径 path");
        var timelinePath = ReadString(arguments, "timelinePath");
        TimelineAsset timeline;
        if (string.IsNullOrWhiteSpace(timelinePath)) timeline = CloneTimeline(_timelineAsset);
        else
        {
            ResolveTimelinePath(timelinePath);
            timeline = LoadTimelineRuntimeAsset(timelinePath)
                ?? throw new FileNotFoundException("时间轴资产无法读取", timelinePath);
            timelinePath = timelinePath.Replace('\\', '/');
        }
        var frameRate = ReadInteger(arguments, "frameRate") ?? _projectSettings?.ExportDialogVideoFrameRate ?? 30;
        var result = StartVideoExport(outputPath, timeline, frameRate, timelinePath);
        if (!result.Started) throw new InvalidOperationException(result.Message);
        return new
        {
            queued = true,
            outputPath = result.OutputPath,
            frameCount = result.FrameCount,
            frameRate = result.FrameRate,
            message = result.Message,
        };
    });

    /// <summary>按路径读取单个视频导出状态，或列出最近任务</summary>
    private object DescribeVideoExports(JsonElement arguments) => RunOnUi(() =>
    {
        RequireAutomationArguments(arguments, "视频导出状态", "path");
        var path = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new
            {
                count = _videoExportInfos.Count,
                exports = _videoExportInfos.OrderByDescending(item => item.StartedAt).Select(DescribeVideoExport).ToArray(),
            };
        var fullPath = NormalizeVideoPath(path);
        var info = FindVideoExportInfo(fullPath) ?? throw new InvalidOperationException("没有该视频导出的记录");
        return new { path = fullPath, export = DescribeVideoExport(info) };
    });

    /// <summary>取消指定视频导出：排队帧整批中止，编码中的任务取消进程</summary>
    private object CancelVideoExportFromArguments(JsonElement arguments) => RunOnUi(() =>
    {
        RequireAutomationArguments(arguments, "视频取消", "path");
        var path = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("缺少要取消的视频路径 path");
        var fullPath = NormalizeVideoPath(path);
        var batch = FindVideoBatch(fullPath);
        if (batch is not null) AbortVideoExport(batch, "已通过自动化取消视频导出", failed: false);
        else
        {
            var info = FindVideoExportInfo(fullPath) ?? throw new InvalidOperationException("没有该视频导出的记录");
            if (info.Phase == "preparing")
            {
                CancelProjectOperation();
                info.Phase = "canceled";
                info.Message = "已取消视频导出准备";
                info.UpdatedAt = NowMilliseconds();
            }
            else if (info.Phase is "completed" or "failed" or "canceled")
                return new { canceled = false, export = DescribeVideoExport(info) };
        }
        var updated = FindVideoExportInfo(fullPath);
        return new { canceled = true, export = updated is null ? null : DescribeVideoExport(updated) };
    });

    private static string NormalizeVideoPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? full : full + ".mp4";
    }
}
