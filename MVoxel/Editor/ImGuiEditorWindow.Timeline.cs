using System.Text.Json;
using ImGuiNET;
using MVoxel.Animation;
using MVoxel.Project;
using MVoxel.Editor.Assets;
using MVoxel.Core;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private string _timelineAssetPath = string.Empty;
    private TimelineAsset _timelineAsset = new();
    private int _timelineFrame;
    private bool _timelineWeatherPlaying;
    private double _timelineWeatherFrame;
    private string _timelineSavedState = JsonSerializer.Serialize(new TimelineAsset());
    private bool _timelineCloseConfirmation;
    /// <summary>确认框已得到结论，等待在弹窗作用域内关闭</summary>
    private bool _timelineConfirmationResolved;
    /// <summary>取消时保留时间轴窗口，不执行后续动作</summary>
    private bool _timelineConfirmationKeepWindow;
    private Action? _afterTimelineConfirmation;
    private bool TimelineIsDirty => JsonSerializer.Serialize(_timelineAsset) != _timelineSavedState;

    private float TimelineWeatherTime => _timelineFrame / (float)Math.Max(1, _timelineAsset.FrameRate);

    private void ResetTimelineWeather()
    {
        _timelineAssetPath = string.Empty;
        _timelineAsset = new TimelineAsset();
        _timelineFrame = 0;
        _timelineWeatherFrame = 0;
        _timelineWeatherPlaying = false;
        _timelineSavedState = JsonSerializer.Serialize(_timelineAsset);
        _timelineCloseConfirmation = false;
        _afterTimelineConfirmation = null;
    }

    private void ConfirmTimelineChangesBefore(Action continuation)
    {
        _afterTimelineConfirmation = continuation;
        _timelineWeatherPlaying = false;
        _showTimelineWindow = true;
        _timelineCloseConfirmation = true;
        //新的确认从干净状态开始
        _timelineConfirmationResolved = false;
        _timelineConfirmationKeepWindow = false;
    }

    private void FinishTimelineConfirmation()
    {
        var continuation = _afterTimelineConfirmation;
        _afterTimelineConfirmation = null;
        _showTimelineWindow = false;
        if (continuation is not null) _pendingActions.Enqueue(continuation);
    }

    /// <summary>保存时间轴后继续；path 为明确目标，saveAs 为让用户选择位置</summary>
    private bool TryConfirmTimelineSave(bool saveAs = false, string? path = null, bool overwrite = false)
    {
        var saved = path is null ? SaveTimelineAsset(saveAs) : SaveTimelineAsset(saveAs: true, explicitPath: path, allowOverwrite: overwrite);
        if (!saved) return false;
        //只有确实存在待处理确认时才登记结论，避免污染后续弹窗
        if (_afterTimelineConfirmation is not null || _timelineCloseConfirmation)
        {
            _timelineConfirmationResolved = true;
            _timelineConfirmationKeepWindow = false;
        }
        return true;
    }

    /// <summary>放弃时间轴修改并继续原操作</summary>
    private void ConfirmTimelineDiscard()
    {
        var continuation = _afterTimelineConfirmation;
        ResetTimelineWeather();
        _afterTimelineConfirmation = continuation;
        _timelineConfirmationResolved = true;
        _timelineConfirmationKeepWindow = false;
    }

    /// <summary>取消本次操作，保留时间轴窗口与修改</summary>
    private void CancelTimelineConfirmation()
    {
        _afterTimelineConfirmation = null;
        _timelineConfirmationResolved = true;
        _timelineConfirmationKeepWindow = true;
    }

    private void RequestTimelineSwitch(string relativePath, bool forceReload = false)
    {
        if (!forceReload && string.Equals(relativePath, _timelineAssetPath, StringComparison.OrdinalIgnoreCase)) return;
        void Switch()
        {
            _showTimelineWindow = true;
            if (string.IsNullOrWhiteSpace(relativePath)) ResetTimelineWeather();
            else LoadTimelineAsset(relativePath);
        }
        if (TimelineIsDirty) ConfirmTimelineChangesBefore(Switch);
        else Switch();
    }

    private void AdvanceTimelineWeather(double elapsed)
    {
        if (!_showTimelineWindow) _timelineWeatherPlaying = false;
        if (!_timelineWeatherPlaying || _project is null) return;
        _timelineWeatherFrame += Math.Max(0, elapsed) * Math.Max(1, _timelineAsset.FrameRate);
        if (_timelineWeatherFrame >= _timelineAsset.LengthFrames)
        {
            _timelineWeatherFrame = _timelineAsset.LengthFrames;
            _timelineWeatherPlaying = false;
        }
        _timelineFrame = (int)_timelineWeatherFrame;
    }

    /// <summary>关闭时间轴：有未保存修改时先弹确认，避免直接丢掉改动</summary>
    private void CloseTimelineWindow()
    {
        if (TimelineIsDirty)
        {
            _timelineCloseConfirmation = true;
            _showTimelineWindow = true;
            return;
        }

        _timelineWeatherPlaying = false;
        _showTimelineWindow = false;
    }

    private void DrawTimelineWindow()
    {
        if (!_showTimelineWindow) return;
        if (_timelineCloseConfirmation)
        {
            //确认框在时间轴窗口内，关窗或退出时把它显式抬到前面，避免提示被其他面板挡住
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
        }
        if (!BeginToolWindow("时间轴##TimelineWindow", PanelWindowFlags, CloseTimelineWindow))
        {
            ImGui.End();
            return;
        }
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
        var label = string.IsNullOrWhiteSpace(_timelineAssetPath) ? "临时时间轴" : _timelineAssetPath;
        if (TimelineIsDirty) label += " *";
        if (ImGui.BeginCombo("##TimelineAsset", label))
        {
            if (ImGui.Selectable("临时时间轴", string.IsNullOrWhiteSpace(_timelineAssetPath))) RequestTimelineSwitch(string.Empty);
            foreach (var path in paths)
            {
                if (!ImGui.Selectable(path, path == _timelineAssetPath)) continue;
                RequestTimelineSwitch(path);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("刷新"))
        {
            if (!string.IsNullOrWhiteSpace(_timelineAssetPath)) RequestTimelineSwitch(_timelineAssetPath, forceReload: true);
        }
        var frameRate = _timelineAsset.FrameRate;
        if (ImGui.DragInt("帧率", ref frameRate, 1F, 1, 240))
        {
            frameRate = Math.Clamp(frameRate, 1, 240);
            var seconds = _timelineAsset.LengthFrames / (double)Math.Max(1, _timelineAsset.FrameRate);
            var currentSeconds = _timelineFrame / (double)Math.Max(1, _timelineAsset.FrameRate);
            _timelineAsset.FrameRate = frameRate;
            _timelineAsset.LengthFrames = (int)Math.Clamp(Math.Round(seconds * frameRate), 1, 10_000_000);
            _timelineFrame = (int)Math.Clamp(Math.Round(currentSeconds * frameRate), 0, _timelineAsset.LengthFrames);
            _timelineWeatherFrame = _timelineFrame;
        }
        var duration = _timelineAsset.LengthFrames / (float)Math.Max(1, _timelineAsset.FrameRate);
        if (ImGui.DragFloat("总时长（秒）", ref duration, .1F, 1F / _timelineAsset.FrameRate, 10_000_000F / _timelineAsset.FrameRate, "%.2f"))
        {
            _timelineAsset.LengthFrames = (int)Math.Clamp(Math.Round((double)duration * _timelineAsset.FrameRate), 1, 10_000_000);
            _timelineFrame = Math.Min(_timelineFrame, _timelineAsset.LengthFrames);
            _timelineWeatherFrame = _timelineFrame;
        }
        var length = _timelineAsset.LengthFrames;
        if (ImGui.DragInt("总帧数", ref length, 1F, 1, 10_000_000))
        {
            _timelineAsset.LengthFrames = length;
            _timelineFrame = Math.Min(_timelineFrame, length);
            _timelineWeatherFrame = _timelineFrame;
        }
        if (ImGui.Button(_timelineWeatherPlaying ? "暂停" : "播放"))
        {
            if (!_timelineWeatherPlaying && _timelineFrame >= _timelineAsset.LengthFrames) _timelineFrame = 0;
            _timelineWeatherFrame = _timelineFrame;
            _timelineWeatherPlaying = !_timelineWeatherPlaying;
        }
        if (ImGui.SliderInt("播放头", ref _timelineFrame, 0, Math.Max(1, _timelineAsset.LengthFrames)))
            _timelineWeatherFrame = _timelineFrame;
        ImGui.SameLine();
        ImGui.TextDisabled($"{_timelineFrame / (float)Math.Max(1, _timelineAsset.FrameRate):0.00}s");
        if (ImGui.Button("保存时间轴")) SaveTimelineAsset();
        ImGui.SameLine();
        if (ImGui.Button("另存为…")) SaveTimelineAsset(saveAs: true);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_timelineAssetPath));
        if (ImGui.Button("重载")) RequestTimelineSwitch(_timelineAssetPath, forceReload: true);
        ImGui.EndDisabled();

        ImGui.SeparatorText("对象启用轨道");
        foreach (var (range, index) in _timelineAsset.ActivationRanges.Select((value, index) => (value, index)).ToArray())
        {
            ImGui.PushID(index);
            var objectId = range.ObjectId;
            DrawTimelineObjectSelector(ref objectId);
            range.ObjectId = objectId;
            ImGui.SameLine();
            if (ImGui.SmallButton("删除##activation")) { _timelineAsset.ActivationRanges.Remove(range); ImGui.PopID(); continue; }
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
        foreach (var (clip, index) in _timelineAsset.Clips.Select((value, index) => (value, index)).ToArray())
        {
            ImGui.PushID(index);
            var objectId = clip.ObjectId;
            DrawTimelineObjectSelector(ref objectId);
            clip.ObjectId = objectId;
            ImGui.SameLine();
            if (ImGui.SmallButton("删除##clip")) { _timelineAsset.Clips.Remove(clip); ImGui.PopID(); continue; }
            var clipLabel = string.IsNullOrWhiteSpace(clip.ClipAsset) ? "选择动画片段" : clip.ClipAsset;
            if (ImGui.BeginCombo("片段", clipLabel))
            {
                foreach (var path in Directory.EnumerateFiles(_project.AssetsPath, "*.mvanim.json", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_project.RootPath, path).Replace('\\', '/');
                    if (ImGui.Selectable(relative, relative == clip.ClipAsset)) clip.ClipAsset = relative;
                }
                ImGui.EndCombo();
            }
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
        foreach (var (marker, index) in _timelineAsset.Events.Select((value, index) => (value, index)).ToArray())
        {
            ImGui.PushID(index);
            if (ImGui.SmallButton("删除##event")) { _timelineAsset.Events.Remove(marker); ImGui.PopID(); continue; }
            var markerFrame = marker.Frame;
            if (ImGui.DragInt("事件帧", ref markerFrame, 1F, 0, _timelineAsset.LengthFrames)) marker.Frame = markerFrame;
            var condition = marker.Condition;
            if (ImGui.InputText("条件", ref condition, 256, ImGuiInputTextFlags.AutoSelectAll)) marker.Condition = condition;
            var action = marker.Action;
            if (ImGui.InputText("动作", ref action, 256, ImGuiInputTextFlags.AutoSelectAll)) marker.Action = action;
            var targetObject = marker.TargetObjectId;
            if (ImGui.InputText("目标对象", ref targetObject, 128, ImGuiInputTextFlags.AutoSelectAll)) marker.TargetObjectId = targetObject;
            ImGui.PopID();
        }
        if (ImGui.Button("添加条件事件")) _timelineAsset.Events.Add(new TimelineEventMarker { Frame = _timelineFrame });
        if (_timelineCloseConfirmation)
        {
            ImGui.OpenPopup("保存时间轴修改？##TimelineClose");
            _timelineCloseConfirmation = false;
        }
        if (CenteredModal.Begin("保存时间轴修改？##TimelineClose", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("时间轴有未保存的修改，继续前是否保存？");
            if (ImGui.Button("保存")) TryConfirmTimelineSave();
            ImGui.SameLine();
            if (ImGui.Button("另存为…")) TryConfirmTimelineSave(saveAs: true);
            ImGui.SameLine();
            if (ImGui.Button("不保存")) ConfirmTimelineDiscard();
            ImGui.SameLine();
            if (ImGui.Button("取消")) CancelTimelineConfirmation();
            //按钮只登记结果，弹窗在本帧作用域内统一关闭并执行后续动作
            if (_timelineConfirmationResolved)
            {
                _timelineConfirmationResolved = false;
                ImGui.CloseCurrentPopup();
                if (_timelineConfirmationKeepWindow) _timelineConfirmationKeepWindow = false;
                else FinishTimelineConfirmation();
            }
            ImGui.EndPopup();
        }
        //Esc 或点外部关闭弹窗按取消处理，避免确认状态一直挂着
        if (_afterTimelineConfirmation is not null && !_timelineCloseConfirmation && !_timelineConfirmationResolved &&
            !ImGui.IsPopupOpen("保存时间轴修改？##TimelineClose"))
            _afterTimelineConfirmation = null;
        ImGui.End();
    }

    private void DrawTimelineObjectSelector(ref string objectId)
    {
        var selectedId = objectId;
        var selected = _sceneDocument?.Objects.FirstOrDefault(item => item.Id.ToString() == selectedId);
        if (!ImGui.BeginCombo("对象", selected?.Name ?? "选择对象")) return;
        if (_sceneDocument is not null)
            foreach (var item in _sceneDocument.Objects)
                if (ImGui.Selectable($"{item.Name}##{item.Id}", item.Id.ToString() == objectId)) objectId = item.Id.ToString();
        ImGui.EndCombo();
    }

    private void LoadTimelineAsset(string relativePath)
    {
        if (_project is null) return;
        var fullPath = Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var loaded = JsonSerializer.Deserialize<TimelineAsset>(File.ReadAllText(fullPath))
                ?? throw new InvalidDataException("时间轴内容为空");
            loaded.Normalize();
            _timelineAsset = loaded;
            _timelineAssetPath = relativePath;
            _timelineFrame = Math.Clamp(_timelineFrame, 0, _timelineAsset.LengthFrames);
            _timelineWeatherFrame = _timelineFrame;
            _timelineWeatherPlaying = false;
            _timelineSavedState = JsonSerializer.Serialize(_timelineAsset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"加载时间轴失败：{exception.Message}");
        }
    }

    private bool SaveTimelineAsset(bool saveAs = false, string? explicitPath = null, bool allowOverwrite = false)
    {
        if (_project is null) return false;
        try
        {
            string fullPath;
            //临时时间轴或显式另存时先确定位置，保存后后续保存沿用新路径
            var newAsset = saveAs || string.IsNullOrWhiteSpace(_timelineAssetPath) || !string.IsNullOrWhiteSpace(explicitPath);
            if (newAsset)
            {
                if (!string.IsNullOrWhiteSpace(explicitPath))
                {
                    //自动化给出明确目标，存在冲突时要求显式覆盖
                    fullPath = Path.GetFullPath(explicitPath);
                    if (!fullPath.EndsWith(".mvtimeline.json", StringComparison.OrdinalIgnoreCase)) fullPath += ".mvtimeline.json";
                    var currentFullPath = string.IsNullOrWhiteSpace(_timelineAssetPath)
                        ? null
                        : Path.GetFullPath(Path.Combine(_project.AssetsPath, _timelineAssetPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(fullPath) && !allowOverwrite &&
                        !string.Equals(fullPath, currentFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus("目标时间轴已存在，如需覆盖请传 overwrite=true");
                        return false;
                    }
                }
                else
                {
                    var suggestedName = string.IsNullOrWhiteSpace(_timelineAssetPath)
                        ? "时间轴.mvtimeline.json" : Path.GetFileName(_timelineAssetPath);
                    var selected = NativeDialogs.SaveFile("保存时间轴资产", "时间轴资产\0*.mvtimeline.json\0全部文件\0*.*\0\0", "",
                        suggestedName);
                    if (string.IsNullOrWhiteSpace(selected)) return false;
                    fullPath = Path.GetFullPath(selected);
                    if (!fullPath.EndsWith(".mvtimeline.json", StringComparison.OrdinalIgnoreCase))
                    {
                        fullPath += ".mvtimeline.json";
                        if (File.Exists(fullPath))
                        {
                            SetStatus("目标时间轴已存在，请使用完整文件名重新选择并确认覆盖");
                            return false;
                        }
                    }
                }
                var root = Path.GetFullPath(_project.AssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("时间轴资产必须保存在当前项目的 Assets 文件夹内");
                    return false;
                }
            }
            else fullPath = Path.Combine(_project.AssetsPath, _timelineAssetPath.Replace('/', Path.DirectorySeparatorChar));
            var savedTimeline = JsonSerializer.Deserialize<TimelineAsset>(JsonSerializer.Serialize(_timelineAsset))
                ?? throw new InvalidDataException("无法复制时间轴");
            savedTimeline.Normalize();
            //另存为新资产时用文件名作为时间轴名称，避免出现一堆同名“时间轴”
            if (newAsset)
            {
                var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fullPath));
                if (!string.IsNullOrWhiteSpace(name)) savedTimeline.Name = name;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            AtomicFile.WriteText(fullPath, JsonSerializer.Serialize(savedTimeline, new JsonSerializerOptions { WriteIndented = true }));
            AssetMetaStore.EnsureMeta(fullPath);
            _timelineAsset = savedTimeline;
            _timelineAssetPath = Path.GetRelativePath(_project.AssetsPath, fullPath).Replace('\\', '/');
            _timelineSavedState = JsonSerializer.Serialize(_timelineAsset);
            SetStatus($"已保存时间轴：{_timelineAssetPath}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            SetStatus($"保存时间轴失败：{exception.Message}");
            return false;
        }
    }
}
