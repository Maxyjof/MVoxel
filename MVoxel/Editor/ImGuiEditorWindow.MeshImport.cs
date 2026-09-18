using System.Numerics;
using ImGuiNET;
using MVoxel.Project;
using MVoxel.Voxels;
using MVoxel.Voxels.Mesh;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器主窗口的网格模型体素化导入面板：
/// 选择 obj/stl/glb/gltf 文件后设置分辨率、实体填充与颜色，写入 Worlds 并作为当前工作世界打开
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private string? _meshImportPath;
    private int _meshImportResolution = 64;
    private bool _meshImportFill = true;
    private Vector3 _meshImportColor = new(0.78F, 0.78F, 0.78F);
    private string _meshImportSummary = "";
    private bool _meshImportOpen;

    /// <summary>
    /// 选择网格文件并打开导入面板，文件非法时在状态栏说明原因
    /// </summary>
    private void ShowMeshImportDialog()
    {
        var path = NativeDialogs.PickFile("选择网格模型",
            "网格模型\0*.obj;*.stl;*.glb;*.gltf\0全部文件\0*.*\0\0");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var mesh = MeshImport.ReadMesh(path);
            _meshImportPath = path;
            _meshImportSummary = $"{Path.GetFileName(path)}：顶点 {mesh.VertexCount:N0}，三角形 {mesh.TriangleCount:N0}";
            if (mesh.SuggestedColor is { } suggested)
            {
                _meshImportColor = new Vector3(suggested.R / 255F, suggested.G / 255F, suggested.B / 255F);
            }

            _meshImportOpen = true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"读取网格失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 每帧绘制网格导入面板
    /// </summary>
    private void DrawMeshImportWindow()
    {
        if (!_meshImportOpen || _meshImportPath is null)
        {
            return;
        }

        if (!ImGui.Begin("导入网格模型", ref _meshImportOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            _meshImportOpen = false;
            return;
        }

        ImGui.TextDisabled(_meshImportSummary);
        ImGui.SliderInt("分辨率（最长轴体素数）", ref _meshImportResolution, 4, 256, "%d", ImGuiSliderFlags.Logarithmic);
        ImGui.Checkbox("实体填充", ref _meshImportFill);
        ImGui.ColorEdit3("体素颜色", ref _meshImportColor);
        ImGui.Separator();
        var import = ImGui.Button("导入") && _project is not null;
        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            _meshImportOpen = false;
        }

        ImGui.End();
        if (import)
        {
            ImportMeshAsWorld(_meshImportPath);
        }
    }

    /// <summary>
    /// 体素化网格并写入项目 Worlds 目录，随后作为当前工作世界打开
    /// </summary>
    /// <param name="path">网格文件路径</param>
    private void ImportMeshAsWorld(string path)
    {
        if (_project is not { } project)
        {
            return;
        }

        try
        {
            var color = new Rgba32(
                (byte)Math.Clamp((int)MathF.Round(_meshImportColor.X * 255F), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(_meshImportColor.Y * 255F), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(_meshImportColor.Z * 255F), 0, 255));
            SetStatus("正在体素化网格…");
            var world = MeshImport.Import(path, color, _meshImportResolution, _meshImportFill);
            var worldsDirectory = Path.Combine(project.AssetsPath, "Worlds");
            Directory.CreateDirectory(worldsDirectory);
            var fileName = SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + ".mvmodel";
            var worldPath = Path.Combine(worldsDirectory, fileName);
            LargeWorldFormat.Write(worldPath, world);
            AssetMetaStore.EnsureMeta(worldPath);
            OpenWorldAsset("Worlds/" + fileName);
            SetStatus($"已导入网格：{world.OccupiedVoxelCount:N0} 体素");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"导入网格失败：{exception.Message}");
        }
    }
}
