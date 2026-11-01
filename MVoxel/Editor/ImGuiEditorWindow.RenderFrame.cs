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

    /// <summary>
    /// 绘制渲染窗口顶部的画面比例与分辨率选择器
    /// </summary>
    private void DrawRenderFrameSelector()
    {
        if (_projectSettings is not { } settings) return;

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
            ImGui.SetTooltip("自由比例铺满整个视图；选定比例后视图按该比例留黑边，导出也按这个比例出图。");

        ImGui.SameLine();
        ImGui.TextUnformatted("分辨率");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170F);
        var resolutionName = settings.FindResolutionPreset()?.Name ?? "跟随视图";
        if (ImGui.BeginCombo("##RenderResolutionCombo", resolutionName))
        {
            if (ImGui.Selectable("跟随视图", settings.RenderResolutionPresetId.Length == 0))
            {
                settings.RenderResolutionPresetId = string.Empty;
                ApplyRenderFrameChange();
            }

            foreach (var preset in settings.RenderResolutionPresets)
            {
                if (!ImGui.Selectable(preset.Name, preset.Id == settings.RenderResolutionPresetId)) continue;
                settings.RenderResolutionPresetId = preset.Id;
                ApplyRenderFrameChange();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("选定分辨率后，预览按该像素数渲染，导出 PNG 也用它，所见即所得。");

        ImGui.SameLine();
        if (ImGui.Button("管理…##RenderFramePresets")) _showFramePresetManager = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("增删改名、调整顺序。预设按项目保存，不影响其他项目。");

        if (_showFramePresetManager) DrawFramePresetManager(settings);
    }

    /// <summary>
    /// 预设管理弹窗：比例与分辨率两张列表，支持改名、改数值、排序与删除
    /// </summary>
    private void DrawFramePresetManager(ProjectSettingsDocument settings)
    {
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
            if (ImGui.InputText("##name", ref name, 32))
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
            if (ImGui.SmallButton("↑")) { moveFrom = index; moveDelta = -1; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == presets.Count - 1);
            if (ImGui.SmallButton("↓")) { moveFrom = index; moveDelta = 1; }
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

        if (removeIndex < 0) return;
        var removedId = presets[removeIndex].Id;
        presets.RemoveAt(removeIndex);
        if (selection == removedId) selection = string.Empty;
        ApplyRenderFrameChange();
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
