using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>从随程序发布的 Markdown 文档显示入门帮助。</summary>
internal sealed partial class ImGuiEditorWindow
{
    private bool _showGettingStarted;
    private int _guidePage;
    private static readonly string[] GuidePageNames = ["快速入门", "视角与绘制", "对象与图层", "资产与渲染", "MagicaVoxel 迁移"];
    private static readonly string[] GuideFiles = ["快速入门.md", "视角与绘制.md", "对象与图层.md", "资产与渲染.md", "MagicaVoxel迁移.md"];

    private void OpenGuide(int page)
    {
        _guidePage = page == 2 ? 4 : 0;
        _showGettingStarted = true;
    }

    private void DrawGettingStarted()
    {
        if (!_showGettingStarted) return;
        ImGui.SetNextWindowSize(new Vector2(820F, 680F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480F, 360F), new Vector2(1800F, 1400F));
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        var open = true;
        if (ImGui.Begin("入门指南##MVoxelGuide", ref open, ImGuiWindowFlags.NoCollapse))
        {
            FloatingWindowShadow.Draw();
            var sidebarWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * .24F, ImGui.GetFontSize() * 10F, ImGui.GetFontSize() * 16F);
            ImGui.BeginChild("##指南目录", new Vector2(sidebarWidth, 0F), ImGuiChildFlags.Borders);
            ImGui.TextDisabled("入门文档");
            ImGui.Spacing();
            for (var index = 0; index < GuidePageNames.Length; index++)
            {
                if (index == 4) ImGui.Spacing();
                if (ImGui.Selectable(GuidePageNames[index], _guidePage == index,
                    ImGuiSelectableFlags.None, new Vector2(0F, ImGui.GetFrameHeight() * 1.25F)))
                    _guidePage = index;
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##Markdown帮助", Vector2.Zero, ImGuiChildFlags.None);
            var path = Path.Combine(AppContext.BaseDirectory, "Editor", "Guide", "Docs", GuideFiles[_guidePage]);
            try { DrawGuideMarkdown(File.ReadAllText(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { ImGui.TextWrapped($"无法读取帮助文档：{path}\n{exception.Message}"); }
            ImGui.EndChild();
        }
        ImGui.End();
        _showGettingStarted = open;
    }

    private static void DrawGuideMarkdown(string markdown)
    {
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { ImGui.Spacing(); continue; }
            if (line.StartsWith("# "))
            {
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.35F);
                ImGui.TextColored(ToColor(EditorPalette.Text), line[2..]);
                ImGui.SetWindowFontScale(1F);
                ImGui.Spacing();
            }
            else if (line.StartsWith("## "))
            {
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.12F);
                ImGui.TextColored(ToColor(EditorPalette.Accent), line[3..]);
                ImGui.SetWindowFontScale(1F);
                ImGui.Spacing();
            }
            else if (line.StartsWith("- "))
            { ImGui.Bullet(); ImGui.SameLine(); ImGui.TextWrapped(line[2..].Replace("`", "")); }
            else if (line.StartsWith("> "))
            { ImGui.TextColored(ToColor(EditorPalette.Accent), "提示"); ImGui.SameLine(); ImGui.TextWrapped(line[2..]); }
            else ImGui.TextWrapped(line.Replace("`", ""));
        }
    }
}
