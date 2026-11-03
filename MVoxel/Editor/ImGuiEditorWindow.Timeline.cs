using System.Text.Json;
using ImGuiNET;
using MVoxel.Animation;
using MVoxel.Project;
using MVoxel.Editor.Assets;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private string _timelineAssetPath = string.Empty;
    private TimelineAsset _timelineAsset = new();
    private int _timelineFrame;

    private void DrawTimelineWindow()
    {
        if (!_showTimelineWindow) return;
        var open = _showTimelineWindow;
        if (!ImGui.Begin("时间轴##TimelineWindow", ref open, PanelWindowFlags))
        {
            ImGui.End();
            _showTimelineWindow = open;
            return;
        }
        _showTimelineWindow = open;
        if (_project is null)
        {
            ImGui.TextDisabled("请先打开项目");
            ImGui.End();
            return;
        }

        var paths = Directory.Exists(_project.AssetsPath)
            ? Directory.GetFiles(_project.AssetsPath, "*.mvtimeline.json", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_project.AssetsPath, path).Replace('\\', '/')).OrderBy(path => path).ToArray()
            : Array.Empty<string>();
        ImGui.SetNextItemWidth(-1F);
        var label = string.IsNullOrWhiteSpace(_timelineAssetPath) ? "选择时间轴资产" : _timelineAssetPath;
        if (ImGui.BeginCombo("##TimelineAsset", label))
        {
            foreach (var path in paths)
            {
                if (!ImGui.Selectable(path, path == _timelineAssetPath)) continue;
                LoadTimelineAsset(path);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("刷新"))
        {
            if (!string.IsNullOrWhiteSpace(_timelineAssetPath)) LoadTimelineAsset(_timelineAssetPath);
        }
        if (string.IsNullOrWhiteSpace(_timelineAssetPath))
        {
            ImGui.TextDisabled("请在Assets文件夹右键新建时间轴资产");
            ImGui.End();
            return;
        }

        var frameRate = _timelineAsset.FrameRate;
        if (ImGui.DragInt("帧率", ref frameRate, 1F, 1, 240)) _timelineAsset.FrameRate = frameRate;
        var length = _timelineAsset.LengthFrames;
        if (ImGui.DragInt("总帧数", ref length, 1F, 1, 10_000_000))
        {
            _timelineAsset.LengthFrames = length;
            _timelineFrame = Math.Min(_timelineFrame, length);
        }
        ImGui.SliderInt("播放头", ref _timelineFrame, 0, Math.Max(1, _timelineAsset.LengthFrames));
        ImGui.SameLine();
        ImGui.TextDisabled($"{_timelineFrame / (float)Math.Max(1, _timelineAsset.FrameRate):0.00}s");
        if (ImGui.Button("保存时间轴")) SaveTimelineAsset();
        ImGui.SameLine();
        if (ImGui.Button("重载")) LoadTimelineAsset(_timelineAssetPath);

        ImGui.SeparatorText("对象启用轨道");
        foreach (var range in _timelineAsset.ActivationRanges.ToArray())
        {
            ImGui.PushID(range.ObjectId + range.StartFrame);
            ImGui.TextUnformatted(range.ObjectId);
            ImGui.SameLine();
            var start = range.StartFrame;
            if (ImGui.DragInt("起始", ref start, 1F, 0, _timelineAsset.LengthFrames)) range.StartFrame = start;
            ImGui.SameLine();
            var end = range.EndFrame;
            if (ImGui.DragInt("结束", ref end, 1F, 0, _timelineAsset.LengthFrames)) range.EndFrame = end;
            ImGui.SameLine();
            var enabled = range.Enabled;
            if (ImGui.Checkbox("启用", ref enabled)) range.Enabled = enabled;
            ImGui.PopID();
        }
        if (ImGui.Button("添加对象启用轨道")) _timelineAsset.ActivationRanges.Add(new TimelineActivationRange());

        ImGui.SeparatorText("动画片段轨道");
        foreach (var clip in _timelineAsset.Clips.ToArray())
        {
            ImGui.PushID(clip.ObjectId + clip.ClipAsset + clip.StartFrame);
            ImGui.TextWrapped($"{clip.ObjectId} · {clip.ClipAsset}");
            var clipStart = clip.StartFrame;
            if (ImGui.DragInt("起始帧", ref clipStart, 1F, 0, _timelineAsset.LengthFrames)) clip.StartFrame = clipStart;
            ImGui.SameLine();
            var clipEnd = clip.EndFrame;
            if (ImGui.DragInt("结束帧", ref clipEnd, 1F, 0, _timelineAsset.LengthFrames)) clip.EndFrame = clipEnd;
            var clipSpeed = clip.Speed;
            if (ImGui.DragFloat("速度", ref clipSpeed, .05F, -8F, 8F, "%.2fx")) clip.Speed = clipSpeed;
            var loop = clip.Loop;
            if (ImGui.Checkbox("循环", ref loop)) clip.Loop = loop;
            ImGui.PopID();
        }
        if (ImGui.Button("添加动画片段轨道")) _timelineAsset.Clips.Add(new TimelineClipPlacement());

        ImGui.SeparatorText("条件事件轨道");
        foreach (var marker in _timelineAsset.Events.ToArray())
        {
            ImGui.PushID(marker.Frame + marker.Action);
            var markerFrame = marker.Frame;
            if (ImGui.DragInt("事件帧", ref markerFrame, 1F, 0, _timelineAsset.LengthFrames)) marker.Frame = markerFrame;
            var condition = marker.Condition;
            if (ImGui.InputText("条件", ref condition, 256)) marker.Condition = condition;
            var action = marker.Action;
            if (ImGui.InputText("动作", ref action, 256)) marker.Action = action;
            var targetObject = marker.TargetObjectId;
            if (ImGui.InputText("目标对象", ref targetObject, 128)) marker.TargetObjectId = targetObject;
            ImGui.PopID();
        }
        if (ImGui.Button("添加条件事件")) _timelineAsset.Events.Add(new TimelineEventMarker { Frame = _timelineFrame });
        ImGui.End();
    }

    private void LoadTimelineAsset(string relativePath)
    {
        if (_project is null) return;
        var fullPath = Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            _timelineAsset = File.Exists(fullPath)
                ? JsonSerializer.Deserialize<TimelineAsset>(File.ReadAllText(fullPath)) ?? new TimelineAsset()
                : new TimelineAsset();
            _timelineAsset.Normalize();
            _timelineAssetPath = relativePath;
            _timelineFrame = Math.Clamp(_timelineFrame, 0, _timelineAsset.LengthFrames);
        }
        catch (JsonException) { SetStatus("时间轴资产数据无效"); }
    }

    private void SaveTimelineAsset()
    {
        if (_project is null || string.IsNullOrWhiteSpace(_timelineAssetPath)) return;
        _timelineAsset.Normalize();
        var fullPath = Path.Combine(_project.AssetsPath, _timelineAssetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(_timelineAsset, new JsonSerializerOptions { WriteIndented = true }));
        AssetMetaStore.EnsureMeta(fullPath);
        SetStatus($"已保存时间轴：{_timelineAssetPath}");
    }
}
