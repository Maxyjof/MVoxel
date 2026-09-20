using System.Text.Json;
using ImGuiNET;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Scene;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 项目资产的 Unity 式检视器：资产浏览器选中任何受支持文件后，
/// 右侧显示可验证的格式、内容摘要和可执行操作，而不是回到“未选择对象”。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private string _assetInspectorWorldPath = string.Empty;
    private DateTime _assetInspectorWorldStamp;
    private VoxelWorld? _assetInspectorWorld;

    private bool TryDrawSelectedAssetInspector()
    {
        if (_project is null || string.IsNullOrWhiteSpace(_assetBrowser.SelectedAssetPath)) return false;
        var relativePath = _assetBrowser.SelectedAssetPath.Replace('\\', '/');
        var absolutePath = Path.GetFullPath(Path.Combine(_project.AssetsPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolutePath)) return false;

        var kind = ProjectAssetCatalog.Classify(absolutePath, false);
        return kind switch
        {
            ProjectAssetKind.VoxelModel => DrawVoxelAssetInspector(relativePath, absolutePath),
            ProjectAssetKind.Scene => DrawSceneAssetInspector(relativePath, absolutePath),
            ProjectAssetKind.Prefab => DrawPrefabAssetInspector(relativePath, absolutePath),
            ProjectAssetKind.Palette => DrawPaletteAssetInspector(relativePath, absolutePath),
            ProjectAssetKind.RenderPreset => DrawRenderPresetInspector(relativePath, absolutePath),
            ProjectAssetKind.Generator => DrawGeneratorAssetInspector(relativePath, absolutePath),
            ProjectAssetKind.Script or ProjectAssetKind.Document or ProjectAssetKind.Unknown =>
                DrawTextAssetInspector(relativePath, absolutePath, kind),
            _ => false,
        };
    }

    private bool DrawVoxelAssetInspector(string relativePath, string path)
    {
        ImGui.Text("体素数据");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        ImGui.TextDisabled($"格式  {Path.GetExtension(path).ToUpperInvariant()[1..]}");
        ImGui.TextDisabled($"大小  {new FileInfo(path).Length / 1024F:0.0} KB");
        try
        {
            var world = LoadAssetWorld(path);
            ImGui.Text($"体素数量  {world.OccupiedVoxelCount:N0}");
            if (world.TryGetBounds(out var minimum, out var maximum))
            {
                ImGui.TextDisabled($"包围盒  ({minimum.X},{minimum.Y},{minimum.Z}) → ({maximum.X},{maximum.Y},{maximum.Z})");
                ImGui.TextDisabled($"尺寸  {maximum.X - minimum.X + 1} × {maximum.Y - minimum.Y + 1} × {maximum.Z - minimum.Z + 1}");
            }

            if (ImGui.Button("设为当前体素数据", new Vector2(150F, 30F))) OpenWorldAsset(relativePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "文件损坏或格式不受支持");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private VoxelWorld LoadAssetWorld(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_assetInspectorWorld is not null &&
            string.Equals(_assetInspectorWorldPath, path, StringComparison.OrdinalIgnoreCase) &&
            _assetInspectorWorldStamp == stamp)
        {
            return _assetInspectorWorld;
        }

        var world = Path.GetExtension(path).Equals(".vox", StringComparison.OrdinalIgnoreCase)
            ? VoxFormat.Read(path)
            : LargeWorldFormat.Read(path);
        _assetInspectorWorldPath = path;
        _assetInspectorWorldStamp = stamp;
        _assetInspectorWorld = world;
        return world;
    }

    private static bool DrawSceneAssetInspector(string relativePath, string path)
    {
        ImGui.Text("场景");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        try
        {
            var scene = SceneSerializer.Load(path);
            var models = scene.Objects.Count(item => item.Components.Any(SceneComponentTypes.IsVoxelModel));
            var cameras = scene.Objects.Count(item => item.Components.Any(SceneComponentTypes.IsCamera));
            var lights = scene.Objects.Count(item => item.Components.Any(SceneComponentTypes.IsLight));
            ImGui.Text($"对象  {scene.Objects.Count}");
            ImGui.TextDisabled($"模型  {models}    相机  {cameras}    光照  {lights}");
            ImGui.TextDisabled($"图层  {scene.LayerNames.Count}");
            ImGui.TextWrapped("场景保存对象层级、变换、可见性、相机和灯光；实际体素内容由模型组件引用的体素数据提供。");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "场景文件损坏或版本不受支持");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private static bool DrawPrefabAssetInspector(string relativePath, string path)
    {
        ImGui.Text("预制体");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        try
        {
            var prefab = PrefabSerializer.Load(path);
            ImGui.Text($"对象  {prefab.Objects.Count}");
            ImGui.TextDisabled($"名称  {prefab.Name}");
            ImGui.TextDisabled(prefab.BaseAssetPath.Length == 0
                ? "类型  普通预制体"
                : $"类型  预制体变体 · 基础：{prefab.BaseAssetPath}");
            ImGui.TextWrapped("预制体保存一棵可复用的对象子树，实例化后可以在场景中独立覆盖。");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "预制体文件损坏或为空");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private bool DrawPaletteAssetInspector(string relativePath, string path)
    {
        ImGui.Text("调色板");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        try
        {
            var colors = PaletteFormat.Load(path);
            ImGui.TextDisabled($"256 色 · {new FileInfo(path).Length:N0} 字节 · BGRA");
            if (ImGui.Button("在项目设置中编辑", new Vector2(-1F, 30F)))
            {
                OpenSettings(1, 3);
                SetStatus("已打开项目设置的“调色板”，可编辑当前调色板颜色");
            }
            var draw = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            const float cell = 15F;
            for (var index = 0; index < colors.Length; index++)
            {
                var x = index % 16;
                var y = index / 16;
                var min = start + new Vector2(x * cell, y * cell);
                var max = min + new Vector2(cell - 1F, cell - 1F);
                var color = colors[index];
                draw.AddRectFilled(min, max, ToPackedColor(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)));
            }

            ImGui.Dummy(new Vector2(16F * cell, 16F * cell));
            ImGui.TextWrapped("该调色板是项目内的独立资产；项目可以同时保存多个调色板，并在项目设置中指定当前使用项。");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "调色板文件损坏或长度不正确");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private bool DrawRenderPresetInspector(string relativePath, string path)
    {
        ImGui.Text("渲染预设");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        try
        {
            var settings = RenderSettings.FromJson(File.ReadAllText(path));
            ImGui.TextDisabled($"地面  {(settings.ShowGround ? "开启" : "关闭")}    太阳  {(settings.SunEnabled ? "开启" : "关闭")}");
            ImGui.TextDisabled($"路径追踪  {(settings.PathTracingEnabled ? "开启" : "关闭")}    调色着色  {(settings.PaletteShading ? "开启" : "关闭")}");
            ImGui.TextDisabled($"曝光  {settings.Exposure:0.00}    视场角  {settings.CameraFovDegrees:0.0}°");
            if (ImGui.Button("应用到当前项目", new Vector2(145F, 30F)))
            {
                _projectSettings.RenderSettings = settings;
                _renderSettings = settings;
                SyncSettingsToggles();
                SaveProjectSettings();
                SetStatus($"已应用渲染预设：{Path.GetFileNameWithoutExtension(path)}");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "渲染预设损坏或格式不受支持");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private bool DrawGeneratorAssetInspector(string relativePath, string path)
    {
        ImGui.Text("生成器");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"路径  {relativePath}");
        try
        {
            var asset = GeneratorAsset.Load(path);
            ImGui.TextDisabled($"语言  {asset.Language}    源码  {asset.Source.Length:N0} 字符");
            ImGui.TextWrapped("生成器可以声明参数并将结果作为可撤销操作写入当前体素数据。");
            if (ImGui.Button("在生成器面板打开", new Vector2(150F, 30F))) OpenGeneratorAsset(relativePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1F, .55F, .35F, 1F), "生成器文件损坏或格式不受支持");
            ImGui.TextWrapped(exception.Message);
        }

        return true;
    }

    private static bool DrawTextAssetInspector(string relativePath, string path, ProjectAssetKind kind)
    {
        ImGui.Text(ProjectAssetCatalog.KindLabel(kind));
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        var info = new FileInfo(path);
        ImGui.TextDisabled($"路径  {relativePath}");
        ImGui.TextDisabled($"大小  {info.Length:N0} 字节    修改时间  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
        if (kind is ProjectAssetKind.Script or ProjectAssetKind.Document)
        {
            try
            {
                ImGui.TextDisabled("内容预览");
                var preview = File.ReadLines(path).Take(8).ToArray();
                ImGui.BeginChild("##AssetTextPreview", new Vector2(0F, Math.Min(150F, 24F + preview.Length * 18F)), ImGuiChildFlags.Borders);
                foreach (var line in preview) ImGui.TextUnformatted(line);
                ImGui.EndChild();
            }
            catch (IOException)
            {
                ImGui.TextDisabled("无法读取文本内容");
            }
        }

        return true;
    }
}
