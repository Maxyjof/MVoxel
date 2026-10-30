namespace MVoxel.Editor.Guide;

/// <summary>指南页面上可以触发的编辑器动作</summary>
internal enum GuideActionId
{
    NewProject,
    OpenProject,
    SaveProject,
    ModelingLayout,
    ShowModelingTools,
    SceneTab,
    RenderTab,
    AssetBrowser,
    ProjectSettings,
    PaletteSettings,
    EditorSettings,
    ShortcutSettings,
    GeneratorPanel,
    RenderConfig,
    ImportCompatibility,
}

/// <summary>指南里附带的一个跳转按钮</summary>
/// <param name="Label">按钮文字</param>
/// <param name="Id">要触发的编辑器动作</param>
internal sealed record GuideAction(string Label, GuideActionId Id);

/// <summary>快速入门的一节，内容按阅读顺序组织</summary>
internal sealed record GuideLesson(
    string Title,
    string Summary,
    string[] Steps,
    string? Note,
    GuideAction[] Actions);

/// <summary>完整教程的一章</summary>
internal sealed record GuideChapter(
    string Title,
    string Summary,
    string[] Steps,
    string[] Points,
    string[] Issues,
    GuideAction[] Actions);

/// <summary>MagicaVoxel 与 MVoxel 的概念对应</summary>
internal sealed record GuideConcept(string MagicaVoxel, string MVoxel, string Note);

/// <summary>MagicaVoxel 与 MVoxel 的操作对应</summary>
internal sealed record GuideOperation(string Task, string MVoxel, string Note);

/// <summary>指南的文本检索，供完整教程筛选章节</summary>
internal static class GuideSearch
{
    internal static bool Matches(GuideChapter chapter, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (chapter.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            chapter.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return chapter.Steps.Any(line => line.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               chapter.Points.Any(line => line.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               chapter.Issues.Any(line => line.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
