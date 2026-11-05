using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>欢迎页与项目菜单共用的项目名称、保存位置和目录选择器。</summary>
internal sealed class ProjectLocationForm
{
    private string _lastPath = string.Empty;
    private string _name = "新项目";
    private string _location = string.Empty;
    private string _browsePath = string.Empty;
    private string _error = string.Empty;
    private string[] _directories = [];

    internal void Draw(ref string path)
    {
        if (path != _lastPath)
        {
            _name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _location = Path.GetDirectoryName(path) ?? string.Empty;
        }
        ImGui.TextUnformatted("项目名称");
        ImGui.SetNextItemWidth(-1F);
        ImGui.InputText("##ProjectName", ref _name, 160, ImGuiInputTextFlags.AutoSelectAll);
        ImGui.TextUnformatted("保存位置");
        var buttonWidth = ImGui.CalcTextSize("浏览…").X + ImGui.GetStyle().FramePadding.X * 2F;
        ImGui.SetNextItemWidth(Math.Max(80F, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##ProjectLocation", ref _location, 2048, ImGuiInputTextFlags.AutoSelectAll);
        ImGui.SameLine();
        if (ImGui.Button("浏览…"))
        {
            Navigate(Directory.Exists(_location) ? _location : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            ImGui.OpenPopup("选择项目保存位置");
        }
        ImGui.SetNextWindowSize(new Vector2(620F, 450F), ImGuiCond.Appearing);
        if (CenteredModal.Begin("选择项目保存位置", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_browsePath);
            if (ImGui.Button("上一级")) Navigate(Path.GetDirectoryName(_browsePath) ?? _browsePath);
            ImGui.SameLine();
            if (ImGui.Button("文档")) Navigate(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            ImGui.SameLine();
            if (ImGui.BeginCombo("磁盘", Path.GetPathRoot(_browsePath)))
            {
                foreach (var drive in Environment.GetLogicalDrives())
                    if (ImGui.Selectable(drive)) Navigate(drive);
                ImGui.EndCombo();
            }
            if (_error.Length > 0) ImGui.TextWrapped(_error);
            if (ImGui.BeginChild("目录", new Vector2(-1F, 280F), ImGuiChildFlags.Borders))
            {
                foreach (var directory in _directories)
                    if (ImGui.Selectable(Path.GetFileName(directory))) { Navigate(directory); break; }
            }
            ImGui.EndChild();
            if (ImGui.Button("使用此位置")) { _location = _browsePath; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        // 拼接留到提交时验证；非法字符不能导致绘制循环抛异常。
        path = _location.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + _name.Trim();
        _lastPath = path;
    }

    internal bool TryValidate(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var name = _name.Trim();
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Path.IsPathFullyQualified(path) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            {
                error = "请输入有效项目名称和绝对保存位置。";
                return false;
            }
            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = exception.Message;
            return false;
        }
    }

    private void Navigate(string path)
    {
        _error = string.Empty;
        try
        {
            _browsePath = Path.GetFullPath(path);
            _directories = Directory.EnumerateDirectories(_browsePath)
                .Where(directory => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _directories = [];
            _error = exception.Message;
        }
    }
}
