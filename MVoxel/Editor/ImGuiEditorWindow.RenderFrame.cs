using System.Numerics;
using ImGuiNET;
using MVoxel.Editor.Settings;

namespace MVoxel.Editor;

/// <summary>
/// 渲染窗口的画面比例与输出分辨率选择。两者都按项目管理，
/// 比例决定视图里留黑边的有效矩形，分辨率决定实际渲染与导出像素数。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private bool _showFramePresetManager;
    private bool _showExportDialog;

    /// <summary>
    /// 绘制渲染窗口顶部的画面比例与分辨率选择器
    /// </summary>
    private void DrawRenderFrameSelector()
    {
        if (_projectSettings is not { } settings) return;

        // 接在渲染相机选择器后面，两者共处工具栏同一行
        ImGui.SameLine(0F, 18F);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("画面比例");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120F);
        var aspectName = settings.FindAspectPreset()?.Name ?? "自由";
        if (ImGui.BeginCombo("##RenderAspectCombo", aspectName))
        {
            if (ImGui.Selectable("自由", settings.RenderAspectPresetId.Length == 0))
            {
                settings.RenderAspectPresetId = string.Empty;
                ApplyRenderFrameChange();
            }

            foreach (var preset in settings.RenderAspectPresets)
            {
                if (!ImGui.Selectable(preset.Name, preset.Id == settings.RenderAspectPresetId)) continue;
                settings.RenderAspectPresetId = preset.Id;
                ApplyRenderFrameChange();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自由比例铺满整个视图；选定比例后视图按该比例留黑边。\n导出时的分辨率与比例在导出对话框里单独设置。");

        ImGui.SameLine();
        if (ImGui.Button("管理…##RenderFramePresets")) _showFramePresetManager = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("增删改名、调整顺序。预设按项目保存，不影响其他项目。");

        if (_showFramePresetManager) DrawFramePresetManager(settings);
    }

    /// <summary>
    /// 按导出对话框里的设置算出实际出图尺寸：自定义宽高优先，其次预设分辨率，
    /// 都没选就按当前画面比例从视图尺寸推算。
    /// </summary>
    private (Vector2 Size, float Scale) ResolveExportCanvas()
    {
        var viewport = _lastRenderViewportSize.X > 0 && _lastRenderViewportSize.Y > 0
            ? _lastRenderViewportSize
            : new Vector2(1280, 720);
        if (_projectSettings is { } settings)
        {
            if (settings.ExportDialogCustomSize)
                return (new Vector2(settings.ExportDialogWidth, settings.ExportDialogHeight), 1F);
            if (settings.RenderResolutionPresets.FirstOrDefault(preset =>
                    preset.Id == settings.ExportDialogResolutionId) is { } preset)
                return (new Vector2(preset.Width, preset.Height), 1F);
        }

        return (ResolveRenderFrameRect(viewport).Size, MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X));
    }

    /// <summary>
    /// 导出对话框：格式、比例、分辨率；确认后按这些设置入队，下次打开沿用上次的选择
    /// </summary>
    private void DrawExportDialog()
    {
        if (!_showExportDialog) return;
        if (_projectSettings is not { } settings)
        {
            _showExportDialog = false;
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(400F, 0F), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("导出", ref _showExportDialog, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("导出类型");
        if (ImGui.RadioButton("图像", settings.ExportDialogFormat != "video")) settings.ExportDialogFormat = "png";
        ImGui.SameLine();
        if (ImGui.RadioButton("视频", settings.ExportDialogFormat == "video")) settings.ExportDialogFormat = "video";
        if (settings.ExportDialogFormat == "video")
        {
            ImGui.TextDisabled("按时间轴逐帧渲染并编码 MP4。");
            var timelines = _project is null ? Array.Empty<string>() : Directory.GetFiles(_project.AssetsPath, "*.mvtimeline.json", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_project.AssetsPath, path).Replace('\\', '/')).OrderBy(path => path).ToArray();
            ImGui.SetNextItemWidth(-1F);
            var timelineLabel = string.IsNullOrWhiteSpace(settings.ExportDialogTimelineAsset) ? "当前时间轴" : settings.ExportDialogTimelineAsset;
            if (ImGui.BeginCombo("##ExportTimeline", timelineLabel))
            {
                if (ImGui.Selectable("当前时间轴", string.IsNullOrWhiteSpace(settings.ExportDialogTimelineAsset))) settings.ExportDialogTimelineAsset = string.Empty;
                foreach (var timeline in timelines)
                    if (ImGui.Selectable(timeline, timeline == settings.ExportDialogTimelineAsset)) settings.ExportDialogTimelineAsset = timeline;
                ImGui.EndCombo();
            }
            var frameRate = settings.ExportDialogVideoFrameRate;
            if (ImGui.InputInt("视频帧率", ref frameRate)) settings.ExportDialogVideoFrameRate = Math.Clamp(frameRate, 1, 240);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("视频时长由所选时间轴决定；帧率可独立设置。");
            if (string.IsNullOrWhiteSpace(settings.ExportDialogTimelineAsset))
            {
                //只有“当前时间轴”能在这里直接算出帧数，避免对话框每帧去读资产文件
                var frames = (int)Math.Ceiling(_timelineAsset.LengthFrames * (double)settings.ExportDialogVideoFrameRate /
                    Math.Max(1, _timelineAsset.FrameRate));
                ImGui.TextDisabled($"当前时间轴将渲染 {frames} 帧，约 {frames / (double)Math.Max(1, settings.ExportDialogVideoFrameRate):0.00} 秒");
            }
            if (!string.IsNullOrWhiteSpace(_timelineAssetPath) && TimelineIsDirty)
                ImGui.TextDisabled("当前时间轴有未保存修改，导出使用窗口中的最新内容");
        }
        else
        {
            ImGui.TextUnformatted("图像格式");
            foreach (var (id, label) in new[] { ("png", "当前相机 · PNG"), ("hdr", "当前相机 · HDR"), ("all", "全部相机 · PNG") })
                if (ImGui.RadioButton(label, settings.ExportDialogFormat == id)) settings.ExportDialogFormat = id;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("画面比例");
        ImGui.SetNextItemWidth(-1F);
        var aspectName = settings.FindAspectPreset()?.Name ?? "自由（跟随视图）";
        if (ImGui.BeginCombo("##ExportAspectCombo", aspectName))
        {
            if (ImGui.Selectable("自由（跟随视图）", settings.RenderAspectPresetId.Length == 0))
            {
                settings.RenderAspectPresetId = string.Empty;
                ApplyRenderFrameChange();
            }

            foreach (var preset in settings.RenderAspectPresets)
            {
                if (!ImGui.Selectable(preset.Name, preset.Id == settings.RenderAspectPresetId)) continue;
                settings.RenderAspectPresetId = preset.Id;
                ApplyRenderFrameChange();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("改这里会同时改变渲染视图的取景，方便边调边看。");

        ImGui.TextUnformatted("分辨率");
        ImGui.SetNextItemWidth(-1F);
        var selectedResolution = settings.RenderResolutionPresets
            .FirstOrDefault(preset => preset.Id == settings.ExportDialogResolutionId);
        var resolutionLabel = settings.ExportDialogCustomSize
            ? $"自定义 {settings.ExportDialogWidth} × {settings.ExportDialogHeight}"
            : selectedResolution?.Name ?? "按比例从视图推算";
        if (ImGui.BeginCombo("##ExportResolutionCombo", resolutionLabel))
        {
            if (ImGui.Selectable("按比例从视图推算", !settings.ExportDialogCustomSize && selectedResolution is null))
            {
                settings.ExportDialogCustomSize = false;
                settings.ExportDialogResolutionId = string.Empty;
            }

            foreach (var preset in settings.RenderResolutionPresets)
            {
                var selected = !settings.ExportDialogCustomSize && preset.Id == settings.ExportDialogResolutionId;
                if (!ImGui.Selectable(preset.Name, selected)) continue;
                settings.ExportDialogCustomSize = false;
                settings.ExportDialogResolutionId = preset.Id;
            }

            if (ImGui.Selectable("自定义…", settings.ExportDialogCustomSize))
            {
                settings.ExportDialogCustomSize = true;
                // 从当前推算值起步，避免一上来就是占位数字
                var (current, currentScale) = ResolveExportCanvas();
                settings.ExportDialogWidth = Math.Clamp((int)(current.X * currentScale), 16, 8192);
                settings.ExportDialogHeight = Math.Clamp((int)(current.Y * currentScale), 16, 8192);
            }

            ImGui.EndCombo();
        }

        if (settings.ExportDialogCustomSize)
        {
            ImGui.SetNextItemWidth(110F);
            var width = settings.ExportDialogWidth;
            if (ImGui.InputInt("宽", ref width)) settings.ExportDialogWidth = Math.Clamp(width, 16, 8192);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110F);
            var height = settings.ExportDialogHeight;
            if (ImGui.InputInt("高", ref height)) settings.ExportDialogHeight = Math.Clamp(height, 16, 8192);
        }

        var (canvas, canvasScale) = ResolveExportCanvas();
        ImGui.TextDisabled($"输出 {(int)(canvas.X * canvasScale)} × {(int)(canvas.Y * canvasScale)} 像素");
        if (settings.ExportDialogFormat == "video")
        {
            ImGui.TextDisabled("MP4 · H.264，宽高需为偶数。");
        }
        else if (settings.ExportDialogFormat == "all")
            ImGui.TextDisabled("每个相机各出一张，尺寸同上，构图按各自相机取景");

        ImGui.Separator();
        if (ImGui.Button("导出", new Vector2(110F, 0F)))
        {
            SaveProjectSettings();
            if (settings.ExportDialogFormat == "video")
            {
                var timeline = string.IsNullOrWhiteSpace(settings.ExportDialogTimelineAsset)
                    ? _timelineAsset : LoadTimelineRuntimeAsset(settings.ExportDialogTimelineAsset);
                if (timeline is null)
                {
                    SetStatus("时间轴资产无法读取，请重新选择");
                    ImGui.End();
                    return;
                }
                var outputPath = NativeDialogs.SaveFile("保存视频", "MP4 视频\0*.mp4\0\0", "mp4", $"视频-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
                if (string.IsNullOrWhiteSpace(outputPath)) { ImGui.End(); return; }
                var export = StartVideoExport(outputPath, timeline, settings.ExportDialogVideoFrameRate,
                    string.IsNullOrWhiteSpace(settings.ExportDialogTimelineAsset) ? null : settings.ExportDialogTimelineAsset);
                if (!export.Started) { ImGui.End(); return; }
            }
            else
            {
                switch (settings.ExportDialogFormat)
                {
                case "hdr":
                    ExportRenderHdr();
                    break;
                case "all":
                    ExportAllCamerasPng();
                    break;
                default:
                    ExportRenderPng();
                    break;
                }
            }

            _showExportDialog = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(110F, 0F))) _showExportDialog = false;

        ImGui.End();
    }

    /// <summary>
    /// 预设管理弹窗：比例与分辨率两张列表，支持改名、改数值、排序与删除
    /// </summary>
    private void DrawFramePresetManager(ProjectSettingsDocument settings)
    {
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(460F, 520F), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("画面比例与分辨率", ref _showFramePresetManager))
        {
            ImGui.End();
            return;
        }

        DrawPresetSection("比例", settings.RenderAspectPresets, isAspect: true, ref settings.RenderAspectPresetId);
        ImGui.Separator();
        DrawPresetSection("分辨率", settings.RenderResolutionPresets, isAspect: false,
            ref settings.RenderResolutionPresetId);

        ImGui.End();
    }

    /// <summary>
    /// 单张预设列表：每行改名、改数值、上移下移与删除
    /// </summary>
    private void DrawPresetSection(string title, List<RenderFramePreset> presets, bool isAspect,
        ref string selection)
    {
        ImGui.PushID(isAspect ? "AspectPresets" : "ResolutionPresets");
        ImGui.TextUnformatted($"{title}预设");
        ImGui.SameLine();
        ImGui.TextDisabled(isAspect ? "自由比例是内置项，删不掉" : "跟随视图是内置项，删不掉");

        var removeIndex = -1;
        var moveFrom = -1;
        var moveDelta = 0;
        for (var index = 0; index < presets.Count; index++)
        {
            var preset = presets[index];
            ImGui.PushID($"{title}{index}");
            ImGui.SetNextItemWidth(140F);
            var name = preset.Name;
            if (ImGui.InputText("##name", ref name, 32, ImGuiInputTextFlags.AutoSelectAll))
            {
                preset.Name = name;
                ApplyRenderFrameChange();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(70F);
            var width = preset.Width;
            if (ImGui.InputInt("##width", ref width, 0))
            {
                preset.Width = Math.Clamp(width, 1, 16384);
                ApplyRenderFrameChange();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted("×");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70F);
            var height = preset.Height;
            if (ImGui.InputInt("##height", ref height, 0))
            {
                preset.Height = Math.Clamp(height, 1, 16384);
                ApplyRenderFrameChange();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.ArrowButton("##moveUp", ImGuiDir.Up)) { moveFrom = index; moveDelta = -1; }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("上移");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == presets.Count - 1);
            if (ImGui.ArrowButton("##moveDown", ImGuiDir.Down)) { moveFrom = index; moveDelta = 1; }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("下移");
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("删除")) removeIndex = index;
            ImGui.PopID();
        }

        if (ImGui.Button($"添加{(isAspect ? "比例" : "分辨率")}"))
        {
            var preset = new RenderFramePreset
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = isAspect ? "自定义比例" : "自定义分辨率",
                Width = isAspect ? 16 : 1920,
                Height = isAspect ? 9 : 1080,
            };
            presets.Add(preset);
            ApplyRenderFrameChange();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(presets.Count == 0);
        if (ImGui.Button("全部重置"))
        {
            presets.Clear();
            presets.AddRange(isAspect
                ? ProjectSettingsDocument.CreateDefaultAspectPresets()
                : ProjectSettingsDocument.CreateDefaultResolutionPresets());
            ApplyRenderFrameChange();
        }

        ImGui.EndDisabled();

        if (moveFrom >= 0)
        {
            var target = Math.Clamp(moveFrom + moveDelta, 0, presets.Count - 1);
            if (target != moveFrom)
            {
                (presets[moveFrom], presets[target]) = (presets[target], presets[moveFrom]);
                ApplyRenderFrameChange();
            }
        }

        if (removeIndex >= 0)
        {
            var removedId = presets[removeIndex].Id;
            presets.RemoveAt(removeIndex);
            if (selection == removedId) selection = string.Empty;
            ApplyRenderFrameChange();
        }
        ImGui.PopID();
    }

    /// <summary>
    /// 画面比例或分辨率变化后重置累积并保存项目设置；旧画面不能停留在错误的裁切上
    /// </summary>
    private void ApplyRenderFrameChange()
    {
        _projectSettings?.Normalize();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _renderViewNeedsInitialRender = true;
        SaveProjectSettings();
    }
}
