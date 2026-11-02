using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static readonly JsonSerializerOptions EnvironmentJsonOptions = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    private void DrawEnvironmentWindow()
    {
        if (!_showEnvironmentWindow) return;
        var open = _showEnvironmentWindow;
        ImGui.SetNextWindowSize(new Vector2(520F, 560F), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("环境##EnvironmentWindow", ref open, PanelWindowFlags))
        {
            ImGui.TextDisabled("当前场景唯一环境配置");
            ImGui.Separator();
            if (_sceneDocument is null)
            {
                ImGui.TextDisabled("当前没有打开的场景");
            }
            else
            {
                var before = JsonSerializer.Serialize(_sceneDocument.Environment, EnvironmentJsonOptions);
                var renderSettings = _renderSettings.CloneForExport();
                _sceneDocument.Environment.ApplyTo(renderSettings);
                DrawEnvironmentSection(renderSettings);
                _sceneDocument.Environment.CaptureFrom(renderSettings);
                _sceneDocument.Environment.ApplyTo(_renderSettings);
                var after = JsonSerializer.Serialize(_sceneDocument.Environment, EnvironmentJsonOptions);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                }
            }
        }
        ImGui.End();
        _showEnvironmentWindow = open;
    }
}
