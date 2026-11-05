using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private string _themeCopyName = "";
    private static readonly string[] ThemeColorLabels =
        ["窗口背景", "面板背景", "面板标题", "输入框背景", "边框", "强调色", "柔和强调色", "文字", "次要文字", "图标", "成功", "错误"];

    private void DrawThemeManager()
    {
        ImGui.TextUnformatted("主题管理");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("主题保存在本机编辑器配置；内置主题可修改或恢复，用户主题可复制和删除。");
        var name = _editorSettings.Theme;
        ImGui.SetNextItemWidth(Math.Max(120F, ImGui.GetContentRegionAvail().X * .5F));
        if (ImGui.BeginCombo("##管理主题选择", name))
        {
            foreach (var option in EditorPalette.ThemeNames)
                if (ImGui.Selectable(option, name == option))
                {
                    _editorSettings.Theme = name = option;
                    EditorPalette.ApplyTheme(option); ApplyImGuiStyle(); SaveEditorSettings();
                }
            ImGui.EndCombo();
        }
        var colors = EditorPalette.CurrentColors();
        ImGui.SetNextItemWidth(Math.Max(120F, ImGui.GetContentRegionAvail().X * .5F));
        ImGui.InputTextWithHint("##复制主题名称", "新主题名称", ref _themeCopyName, 80, ImGuiInputTextFlags.AutoSelectAll);
        ImGui.SameLine();
        if (ImGui.Button("复制主题"))
        {
            var copy = _themeCopyName.Trim();
            if (copy.Length == 0 || EditorPalette.ThemeNames.Contains(copy, StringComparer.OrdinalIgnoreCase))
                SetStatus("主题名称不能为空或重复");
            else try
            {
                EditorPalette.SaveTheme(copy, colors); _editorSettings.Theme = name = copy;
                EditorPalette.ApplyTheme(copy); ApplyImGuiStyle(); SaveEditorSettings(); _themeCopyName = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { SetStatus($"保存主题失败：{exception.Message}"); }
        }
        ImGui.SameLine();
        if (EditorPalette.IsBuiltIn(name))
        {
            if (ImGui.Button("恢复默认"))
                try { EditorPalette.RemoveTheme(name); EditorPalette.ApplyTheme(name); ApplyImGuiStyle(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { SetStatus($"恢复主题失败：{exception.Message}"); }
        }
        else if (ImGui.Button("删除主题"))
            try
            {
                EditorPalette.RemoveTheme(name); _editorSettings.Theme = name = EditorPalette.DefaultThemeName;
                EditorPalette.ApplyTheme(name); ApplyImGuiStyle(); SaveEditorSettings();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { SetStatus($"删除主题失败：{exception.Message}"); }
        ImGui.Spacing();
        for (var i = 0; i < colors.Length; i++)
        {
            var color = System.Drawing.Color.FromArgb(colors[i]);
            var value = new Vector3(color.R / 255F, color.G / 255F, color.B / 255F);
            ImGui.SetNextItemWidth(Math.Max(120F, ImGui.GetContentRegionAvail().X * .55F));
            if (!ImGui.ColorEdit3(ThemeColorLabels[i], ref value)) continue;
            colors[i] = System.Drawing.Color.FromArgb(255, (int)(value.X * 255F), (int)(value.Y * 255F), (int)(value.Z * 255F)).ToArgb();
            try { EditorPalette.SaveTheme(name, colors); EditorPalette.ApplyTheme(name); ApplyImGuiStyle(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { SetStatus($"保存主题失败：{exception.Message}"); }
        }
    }
}
