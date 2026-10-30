using System.Drawing;
using System.Numerics;
using ImGuiNET;
using MVoxel.Editor.Guide;
using MVoxel.Editor.Settings;

namespace MVoxel.Editor;

/// <summary>
/// 应用内入门指南的界面部分，内容数据位于 Guide 目录，页面划分为快速入门、完整教程、迁移指南和快捷键
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private bool _showGettingStarted;
    private int _guidePage;
    private int _guideChapterIndex;
    private string _guideSearch = string.Empty;

    private static readonly string[] GuidePageNames = ["快速入门", "完整教程", "迁移指南", "快捷键"];

    /// <summary>
    /// 打开指南并定位到指定页面，0 为快速入门，1 为完整教程，2 为迁移指南，3 为快捷键
    /// </summary>
    private void OpenGuide(int page)
    {
        _guidePage = Math.Clamp(page, 0, GuidePageNames.Length - 1);
        _showGettingStarted = true;
    }

    private void DrawGettingStarted()
    {
        if (!_showGettingStarted) return;
        ImGui.SetNextWindowSize(new Vector2(1040F, 780F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(780F, 560F), new Vector2(1800F, 1400F));
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * 0.5F, ImGuiCond.FirstUseEver, new Vector2(0.5F, 0.5F));
        var open = true;
        if (ImGui.Begin("入门指南##MVoxelGuide", ref open, ImGuiWindowFlags.NoCollapse))
        {
            DrawGuideNav();
            switch (_guidePage)
            {
                case 0: DrawGuideQuickStart(); break;
                case 1: DrawGuideTutorial(); break;
                case 2: DrawGuideMigration(); break;
                default: DrawGuideShortcuts(); break;
            }
        }

        ImGui.End();
        _showGettingStarted = open;
    }

    private void DrawGuideNav()
    {
        for (var index = 0; index < GuidePageNames.Length; index++)
        {
            if (index > 0) ImGui.SameLine();
            var active = _guidePage == index;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.Accent));
            var label = GuidePageNames[index];
            if (ImGui.Button(label, new Vector2(Math.Max(96F, ImGui.CalcTextSize(label).X + 28F),
                    ImGui.GetFrameHeight() + 4F)))
                _guidePage = index;
            if (active) ImGui.PopStyleColor();
        }

        ImGui.Separator();
    }

    private void DrawGuideQuickStart()
    {
        ImGui.BeginChild("##快速入门内容", new Vector2(0F, 0F), ImGuiChildFlags.None);
        DrawGuideMuted(GuideQuickStart.Summary);
        ImGui.Spacing();

        foreach (var lesson in GuideQuickStart.Lessons)
        {
            DrawGuideLesson(lesson);
            ImGui.Spacing();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(EditorPalette.Accent));
        ImGui.TextWrapped(GuideQuickStart.Closing);
        ImGui.PopStyleColor();
        ImGui.EndChild();
    }

    private void DrawGuideLesson(GuideLesson lesson)
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), lesson.Title);
        DrawGuideMuted(lesson.Summary);
        for (var step = 0; step < lesson.Steps.Length; step++)
            ImGui.TextWrapped($"{step + 1}. {lesson.Steps[step]}");
        if (!string.IsNullOrEmpty(lesson.Note)) DrawGuideNote(lesson.Note);
        DrawGuideActions(lesson.Actions);
        ImGui.Separator();
    }

    private void DrawGuideTutorial()
    {
        ImGui.BeginChild("##教程章节导航", new Vector2(232F, 0F), ImGuiChildFlags.Borders);
        ImGui.SetNextItemWidth(-1F);
        ImGui.InputTextWithHint("##教程搜索", "搜索章节内容", ref _guideSearch, 128);
        var chapters = GuideTutorial.Chapters;
        var matched = Enumerable.Range(0, chapters.Length)
            .Where(index => GuideSearch.Matches(chapters[index], _guideSearch)).ToArray();
        ImGui.TextDisabled($"{matched.Length} / {chapters.Length} 章");
        ImGui.Separator();
        if (matched.Length == 0) ImGui.TextWrapped("没有匹配的章节，换个关键词试试。");
        else
        {
            if (!matched.Contains(_guideChapterIndex)) _guideChapterIndex = matched[0];
            foreach (var index in matched)
            {
                if (ImGui.Selectable($"{index + 1:00}  {chapters[index].Title}", index == _guideChapterIndex,
                        ImGuiSelectableFlags.None, new Vector2(0F, 30F)))
                    _guideChapterIndex = index;
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##教程章节内容", new Vector2(0F, 0F), ImGuiChildFlags.None);
        if (matched.Length == 0) ImGui.TextWrapped("换成“绘制”“图层”“导出”之类的关键词再搜。");
        else DrawGuideChapter(_guideChapterIndex);
        ImGui.EndChild();
    }

    private void DrawGuideChapter(int index)
    {
        var chapters = GuideTutorial.Chapters;
        var chapter = chapters[index];
        ImGui.TextColored(ToColor(EditorPalette.Accent), $"第 {index + 1} 课 · {chapter.Title}");
        DrawGuideMuted(chapter.Summary);

        ImGui.SeparatorText("怎么做");
        for (var step = 0; step < chapter.Steps.Length; step++) ImGui.TextWrapped($"{step + 1}. {chapter.Steps[step]}");

        ImGui.SeparatorText("说明");
        foreach (var point in chapter.Points) DrawGuideBullet(point, EditorPalette.Text);

        ImGui.SeparatorText("常见问题");
        foreach (var issue in chapter.Issues) DrawGuideBullet(issue, EditorPalette.Text);

        DrawGuideActions(chapter.Actions);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.BeginDisabled(index <= 0);
        if (ImGui.Button("上一课")) _guideChapterIndex = index - 1;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(index >= chapters.Length - 1);
        if (ImGui.Button("下一课")) _guideChapterIndex = index + 1;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"{index + 1} / {chapters.Length}");
    }

    private void DrawGuideMigration()
    {
        ImGui.BeginChild("##迁移指南内容", new Vector2(0F, 0F), ImGuiChildFlags.None);
        foreach (var line in GuideMigration.Intro) ImGui.TextWrapped(line);
        ImGui.SeparatorText("概念对照");
        DrawGuideConceptTable();
        ImGui.SeparatorText("操作对照");
        DrawGuideOperationTable();
        ImGui.SeparatorText("迁入六步");
        for (var step = 0; step < GuideMigration.Steps.Length; step++)
            ImGui.TextWrapped($"{step + 1}. {GuideMigration.Steps[step]}");
        ImGui.SeparatorText("差异与限制");
        foreach (var limit in GuideMigration.Limits) DrawGuideBullet(limit, EditorPalette.Text);
        ImGui.SeparatorText("常见问题");
        foreach (var issue in GuideMigration.Issues) DrawGuideBullet(issue, EditorPalette.Text);
        DrawGuideActions(new[]
        {
            new GuideAction("新建 MVoxel 项目…", GuideActionId.NewProject),
            new GuideAction("兼容导入 .vox…", GuideActionId.ImportCompatibility),
        });
        ImGui.EndChild();
    }

    private static void DrawGuideConceptTable()
    {
        if (!ImGui.BeginTable("##概念对照表", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("MagicaVoxel", ImGuiTableColumnFlags.WidthStretch, 1.1F);
        ImGui.TableSetupColumn("MVoxel", ImGuiTableColumnFlags.WidthStretch, 1.6F);
        ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch, 1.6F);
        ImGui.TableHeadersRow();
        foreach (var concept in GuideMigration.Concepts)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(concept.MagicaVoxel);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(concept.MVoxel);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(concept.Note);
        }

        ImGui.EndTable();
    }

    private static void DrawGuideOperationTable()
    {
        if (!ImGui.BeginTable("##操作对照表", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("想做什么", ImGuiTableColumnFlags.WidthStretch, 1F);
        ImGui.TableSetupColumn("MVoxel 做法", ImGuiTableColumnFlags.WidthStretch, 1.4F);
        ImGui.TableSetupColumn("迁移提示", ImGuiTableColumnFlags.WidthStretch, 2F);
        ImGui.TableHeadersRow();
        foreach (var operation in GuideMigration.Operations)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(operation.Task);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(operation.MVoxel);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(operation.Note);
        }

        ImGui.EndTable();
    }

    private void DrawGuideShortcuts()
    {
        ImGui.BeginChild("##快捷键内容", new Vector2(0F, 0F), ImGuiChildFlags.None);
        var scheme = _editorSettings.ActiveScheme();
        ImGui.TextColored(ToColor(EditorPalette.Accent), $"当前方案 · {scheme.Name}");
        ImGui.SameLine();
        if (ImGui.Button("打开快捷键方案")) RunGuideAction(GuideActionId.ShortcutSettings);
        DrawGuideMuted("快捷键由命令绑定驱动，可以在设置里改键或新建方案；文本输入框获得焦点时不会触发建模快捷键。");

        ImGui.SeparatorText("鼠标与视图");
        DrawGuideMouseTable();

        ImGui.SeparatorText("命令绑定");
        if (ImGui.BeginTable("##快捷键命令表", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("命令", ImGuiTableColumnFlags.WidthStretch, 1.6F);
            ImGui.TableSetupColumn("分类", ImGuiTableColumnFlags.WidthStretch, 1F);
            ImGui.TableSetupColumn("当前绑定", ImGuiTableColumnFlags.WidthStretch, 1.4F);
            ImGui.TableHeadersRow();
            foreach (var command in ShortcutCommands.All)
            {
                scheme.Bindings.TryGetValue(command.Id, out var binding);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextWrapped(command.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(command.Category);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(binding) ? command.DefaultBinding : binding);
            }

            ImGui.EndTable();
        }

        ImGui.SeparatorText("面板与编辑目标");
        foreach (var line in new[]
                 {
                     "在层级或场景中选中体素模型会自动切换到绘制工具",
                     "模型行右侧的锁固定编辑目标，锁定后新方块只归属该模型",
                     "隐藏对象、隐藏图层或禁用模型组件后都不能编辑",
                     "切到相机工具后左键只观察，不会改动模型",
                 })
            DrawGuideBullet(line, EditorPalette.Text);
        ImGui.EndChild();
    }

    private static void DrawGuideMouseTable()
    {
        if (!ImGui.BeginTable("##鼠标操作表", 2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthStretch, 1.2F);
        ImGui.TableSetupColumn("效果", ImGuiTableColumnFlags.WidthStretch, 2F);
        ImGui.TableHeadersRow();
        foreach (var (action, effect) in new[]
                 {
                     ("右键拖动", "环绕观察，任何工具下都不会误改模型"),
                     ("中键拖动", "平移视角"),
                     ("滚轮", "缩放，靠近或拉远观察模型"),
                     ("左键（绘制工具）", "在命中表面放置方块"),
                     ("Shift+左键", "擦除方块"),
                     ("大写锁定", "进入第一人称，WASD 移动、Q/E 升降、Shift 加速"),
                     ("F1 / F2", "切换场景页与渲染页"),
                     ("F", "聚焦选中的体素模型"),
                     ("G", "显示或隐藏场景网格"),
                     ("Shift+Space", "显示或隐藏全部面板"),
                 })
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(action);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(effect);
        }

        ImGui.EndTable();
    }

    private void DrawGuideActions(IReadOnlyList<GuideAction> actions)
    {
        if (actions.Count == 0) return;
        ImGui.Spacing();
        var available = ImGui.GetContentRegionAvail().X;
        var used = 0F;
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var width = Math.Max(112F, ImGui.CalcTextSize(action.Label).X + 26F);
            if (used > 0F && used + ImGui.GetStyle().ItemSpacing.X + width <= available)
            {
                ImGui.SameLine();
                used += ImGui.GetStyle().ItemSpacing.X;
            }
            else used = 0F;

            ImGui.BeginDisabled(!IsGuideActionAvailable(action.Id));
            if (ImGui.Button(action.Label, new Vector2(width, ImGui.GetFrameHeight())))
                RunGuideAction(action.Id);
            ImGui.EndDisabled();
            used += width;
        }
    }

    private bool IsGuideActionAvailable(GuideActionId id) => id switch
    {
        GuideActionId.NewProject or GuideActionId.OpenProject or GuideActionId.EditorSettings or
            GuideActionId.ShortcutSettings or GuideActionId.ModelingLayout or GuideActionId.ShowModelingTools => true,
        _ => _project is not null,
    };

    private void RunGuideAction(GuideActionId id)
    {
        switch (id)
        {
            case GuideActionId.NewProject:
                ShowProjectPathDialog(create: true);
                break;
            case GuideActionId.OpenProject:
                ShowProjectPathDialog(create: false);
                break;
            case GuideActionId.SaveProject:
                if (_project is not null) SaveProject();
                break;
            case GuideActionId.ModelingLayout:
                SelectLayout(LayoutPreset.Modeling);
                break;
            case GuideActionId.ShowModelingTools:
                _showModelingTools = true;
                _focusModelingTools = true;
                break;
            case GuideActionId.SceneTab:
                _requestedCenterTab = 0;
                break;
            case GuideActionId.RenderTab:
                _requestedCenterTab = 1;
                break;
            case GuideActionId.AssetBrowser:
                _showProjectAssets = true;
                break;
            case GuideActionId.ProjectSettings:
                OpenSettings(1);
                break;
            case GuideActionId.PaletteSettings:
                OpenSettings(1, 3);
                break;
            case GuideActionId.EditorSettings:
                OpenSettings(0);
                break;
            case GuideActionId.ShortcutSettings:
                OpenSettings(0, 2, "editor.input.shortcuts");
                break;
            case GuideActionId.GeneratorPanel:
                _showGeneratorPanel = true;
                break;
            case GuideActionId.RenderConfig:
                _showInspector = true;
                _requestedInspectorTab = 1;
                break;
            case GuideActionId.ImportCompatibility:
                if (_project is not null) ImportVoxAsWorld();
                break;
        }
    }

    private static void DrawGuideMuted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(EditorPalette.MutedText));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawGuideNote(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(EditorPalette.MutedText));
        ImGui.TextWrapped("提示：" + text);
        ImGui.PopStyleColor();
    }

    private static void DrawGuideBullet(string text, Color color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(color));
        ImGui.TextWrapped("· " + text);
        ImGui.PopStyleColor();
    }
}
