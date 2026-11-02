using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Scene;
using MVoxel.Rendering;
using MVoxel.Core;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void DrawProbeVolumeWindow()
    {
        if (!_showProbeVolumeWindow) return;
        var open = _showProbeVolumeWindow;
        if (!ImGui.Begin("探针体积##ProbeVolumeWindow", ref open, PanelWindowFlags))
        {
            ImGui.End();
            _showProbeVolumeWindow = open;
            return;
        }
        _showProbeVolumeWindow = open;
        ImGui.TextWrapped("探针体积：手动生成规则网格及高度渐变环境数据。当前不采样场景光照或遮挡，自动烘焙尚未实现。");
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("请先打开场景");
            ImGui.End();
            return;
        }
        var probes = _sceneDocument.Objects
            .SelectMany(item => item.Components.Where(SceneComponentTypes.IsProbeVolume).Select(component => (item, component)))
            .ToArray();
        if (probes.Length == 0)
        {
            ImGui.TextDisabled("场景中没有自动探针组件，请在对象检查器中添加");
            ImGui.End();
            return;
        }
        var selectedIndex = Math.Clamp(_probeVolumeSelection, 0, probes.Length - 1);
        if (ImGui.BeginCombo("探针对象##ProbeObject", probes[selectedIndex].item.Name))
        {
            for (var index = 0; index < probes.Length; index++)
            {
                if (ImGui.Selectable(probes[index].item.Name, index == selectedIndex))
                {
                    selectedIndex = index;
                    _probeVolumeSelection = index;
                }
            }
            ImGui.EndCombo();
        }
        var selectedProbe = probes[selectedIndex];
        var data = SceneComponentTypes.ReadProbeVolume(selectedProbe.component);
        ImGui.SeparatorText(selectedProbe.item.Name);
        ImGui.Text($"尺寸 {data.SizeX:0.##} × {data.SizeY:0.##} × {data.SizeZ:0.##} · 密度 {data.Density:0.##}");
        if (ImGui.Button("生成探针数据##ProbeBake", new Vector2(-1F, 0F)))
            BakeProbeVolume(selectedProbe.item, selectedProbe.component, data);
        ImGui.End();
    }

    private void BakeProbeVolume(SceneObject sceneObject, ComponentRecord component, ProbeVolumeComponentData data)
    {
        if (_project is null) return;
        var countX = Math.Clamp((int)MathF.Ceiling(data.SizeX * data.Density) + 1, 1, 128);
        var countY = Math.Clamp((int)MathF.Ceiling(data.SizeY * data.Density) + 1, 1, 128);
        var countZ = Math.Clamp((int)MathF.Ceiling(data.SizeZ * data.Density) + 1, 1, 128);
        var result = new ProbeVolumeBakeData { CountX = countX, CountY = countY, CountZ = countZ };
        for (var z = 0; z < countZ; z++)
        for (var y = 0; y < countY; y++)
        for (var x = 0; x < countX; x++)
        {
            var fx = countX == 1 ? .5F : (float)x / (countX - 1);
            var fy = countY == 1 ? .5F : (float)y / (countY - 1);
            var fz = countZ == 1 ? .5F : (float)z / (countZ - 1);
            var sky = Vector3.Lerp(new Vector3(.12F, .15F, .2F), Vector3.One, fy);
            result.Samples.Add(new ProbeSample { X = fx, Y = fy, Z = fz, R = sky.X, G = sky.Y, B = sky.Z, Occlusion = 1F });
        }
        var directory = Path.Combine(_project.AssetsPath, "Lighting");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{sceneObject.Id}.probe.json");
        AtomicFile.WriteText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        SceneComponentTypes.WriteProbeVolume(component, data with { BakedAsset = "Lighting/" + Path.GetFileName(path) });
        MarkSceneDocumentDirty();
        SetStatus($"已生成探针数据：{sceneObject.Name} · {result.Samples.Count:N0}个探针");
    }
}
