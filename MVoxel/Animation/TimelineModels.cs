using System.Text.Json.Serialization;

namespace MVoxel.Animation;

/// <summary>时间轴轨道类型：对象状态、动画片段编排或条件事件</summary>
public enum TimelineTrackType
{
    Property,
    AnimationClip,
    Event
}

/// <summary>时间轴中的对象启用区间</summary>
public sealed class TimelineActivationRange
{
    public string ObjectId { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int EndFrame { get; set; } = 60;
    public bool Enabled { get; set; } = true;
}

/// <summary>时间轴中的动画片段编排项</summary>
public sealed class TimelineClipPlacement
{
    public string ObjectId { get; set; } = string.Empty;
    public string ClipAsset { get; set; } = string.Empty;
    public int StartFrame { get; set; }
    public int EndFrame { get; set; } = 60;
    public float Speed { get; set; } = 1F;
    public bool Loop { get; set; }
}

/// <summary>时间轴条件事件，供后续触发组件或切换对象状态</summary>
public sealed class TimelineEventMarker
{
    public int Frame { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetObjectId { get; set; } = string.Empty;
}

/// <summary>跨对象统筹动画片段、对象启用状态与条件事件的时间轴资产</summary>
public sealed class TimelineAsset
{
    public string Name { get; set; } = "时间轴";
    public int FrameRate { get; set; } = 30;
    public int LengthFrames { get; set; } = 300;
    public List<TimelineActivationRange> ActivationRanges { get; set; } = [];
    public List<TimelineClipPlacement> Clips { get; set; } = [];
    public List<TimelineEventMarker> Events { get; set; } = [];

    public void Normalize()
    {
        Name ??= "时间轴";
        ActivationRanges ??= [];
        Clips ??= [];
        Events ??= [];
        FrameRate = Math.Clamp(FrameRate, 1, 240);
        LengthFrames = Math.Clamp(LengthFrames, 1, 10_000_000);
        // 未填写完成的轨道也是用户工作，应保存在资产中，供下次继续编辑。
        ActivationRanges.RemoveAll(item => item is null);
        Clips.RemoveAll(item => item is null);
        Events.RemoveAll(item => item is null);
        foreach (var range in ActivationRanges)
        {
            range.ObjectId ??= string.Empty;
            range.StartFrame = Math.Clamp(range.StartFrame, 0, 10_000_000);
            range.EndFrame = Math.Clamp(range.EndFrame, 0, 10_000_000);
        }
        foreach (var clip in Clips)
        {
            clip.ObjectId ??= string.Empty;
            clip.ClipAsset ??= string.Empty;
            clip.ClipAsset = clip.ClipAsset.Replace('\\', '/');
            clip.StartFrame = Math.Clamp(clip.StartFrame, 0, 10_000_000);
            clip.EndFrame = Math.Clamp(clip.EndFrame, 0, 10_000_000);
            clip.Speed = float.IsFinite(clip.Speed) ? Math.Clamp(clip.Speed, -8F, 8F) : 1F;
        }
        foreach (var marker in Events)
        {
            marker.Condition ??= string.Empty;
            marker.Action ??= string.Empty;
            marker.TargetObjectId ??= string.Empty;
            marker.Frame = Math.Clamp(marker.Frame, 0, 10_000_000);
        }
    }
}
