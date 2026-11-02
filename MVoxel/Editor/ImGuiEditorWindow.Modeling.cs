using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Project;
using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private Dictionary<StableId, Dictionary<int, int>> _runtimeMaterialSlotsByObject = [];
    private IReadOnlyDictionary<int, MaterialAsset> _runtimeMaterials = new Dictionary<int, MaterialAsset>();
    private string _runtimeMaterialSignature = string.Empty;

    private void RefreshRuntimeMaterialTable()
    {
        var byPath = _brushMaterials.ToDictionary(item => Path.GetFullPath(item.SourcePath), StringComparer.OrdinalIgnoreCase);
        var runtimeByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var objectSlots = new Dictionary<StableId, Dictionary<int, int>>();
        var runtimeMaterials = new Dictionary<int, MaterialAsset>();
        foreach (var sceneObject in _sceneDocument?.Objects ?? [])
        {
            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var localSlots = new Dictionary<int, int>();
            foreach (var binding in data.MaterialSlots ?? [])
            {
                if (_project is null) continue;
                var assetPath = Path.GetFullPath(Path.Combine(_project.AssetsPath,
                    binding.MaterialAsset.Replace('/', Path.DirectorySeparatorChar)));
                if (!byPath.TryGetValue(assetPath, out var material)) continue;
                if (!runtimeByPath.TryGetValue(assetPath, out var runtimeSlot))
                {
                    runtimeSlot = runtimeByPath.Count + 1;
                    if (runtimeSlot >= MaterialAsset.TableSlots)
                        throw new InvalidOperationException("场景材质数量超过16位运行时容量，请减少同时使用的不同材质资产");
                    runtimeByPath.Add(assetPath, runtimeSlot);
                    runtimeMaterials[runtimeSlot] = material;
                }
                localSlots[binding.Slot] = runtimeSlot;
            }
            objectSlots[sceneObject.Id] = localSlots;
        }

        var signature = string.Join("|", runtimeMaterials.OrderBy(item => item.Key)
            .Select(item => $"{item.Key}:{item.Value.SourcePath}:{File.GetLastWriteTimeUtc(item.Value.SourcePath).Ticks}")) +
            ";" + string.Join("|", objectSlots.OrderBy(item => item.Key)
                .SelectMany(item => item.Value.OrderBy(slot => slot.Key)
                    .Select(slot => $"{item.Key}:{slot.Key}>{slot.Value}")));
        _runtimeMaterialSlotsByObject = objectSlots;
        _runtimeMaterials = runtimeMaterials;
        if (signature == _runtimeMaterialSignature) return;
        _runtimeMaterialSignature = signature;
        _composedRenderWorld = null;
        _composedSignature = 0;
        _pipeline?.SetMaterialTable(MaterialAsset.PackTable(runtimeMaterials));
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
    }

    private VoxelValue RemapRuntimeMaterial(StableId objectId, VoxelValue value)
    {
        if (value.MaterialSlot == 0) return value;
        var runtimeSlot = _runtimeMaterialSlotsByObject.TryGetValue(objectId, out var slots) &&
            slots.TryGetValue(value.MaterialSlot, out var mapped) ? mapped : (ushort)0;
        return value with { MaterialSlot = (ushort)runtimeSlot };
    }

    private bool _showModelingTools = true;
    private bool _resetDockLayoutRequested;
    private bool _focusModelingTools;
    private Action? _afterProjectClose;
    private bool _allowProjectReplacement;
    private bool _showRenderStatistics;

    private bool DeferProjectReplacement(Action continuation)
    {
        if (_allowProjectReplacement || _project is null || !_sceneDocumentDirty) return false;
        _afterProjectClose = continuation;
        _projectPathDialogMode = 0;
        _closeProjectDialogRequested = true;
        return true;
    }

    private void CompleteProjectClose()
    {
        var continuation = _afterProjectClose;
        _afterProjectClose = null;
        if (continuation is null) CloseProject();
        else
        {
            _allowProjectReplacement = true;
            try { continuation(); }
            finally { _allowProjectReplacement = false; }
        }
    }

    private void ShowSaveAsDialog()
    {
        if (_project is null) return;
        _projectPathDialogMode = 3;
        _projectPathText = Path.Combine(DefaultProjectRootPath, _project.Manifest.Name + " 副本");
        _projectPathError = string.Empty;
    }

    private void BeginSaveAsProject(string requestedPath)
    {
        if (_project is not { } source) return;
        try
        {
            var destination = Path.GetFullPath(requestedPath);
            var root = Path.TrimEndingDirectorySeparator(source.RootPath);
            if (destination.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("副本必须位于当前项目之外的独立文件夹。");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("目标文件夹已存在，请选择一个新位置，避免覆盖文件。");
            SaveProject();
            if (_sceneDocumentDirty) return;
            if (!_projectOperation.TryStart("正在另存项目", "正在复制项目资产…",
                (cancellation, report) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    report(new BlockingOperationProgress("正在复制项目资产…", .1F, CanCancel: false));
                    Directory.CreateDirectory(destination);
                    foreach (var directory in new[] { "Assets", "ProjectSettings", "Plugins" })
                        CopyProjectDirectory(Path.Combine(root, directory), Path.Combine(destination, directory));
                    var manifest = new ProjectManifest { Name = Path.GetFileName(destination), EngineVersion = source.Manifest.EngineVersion };
                    AtomicFile.WriteText(Path.Combine(destination, ProjectLayout.ProjectFileName),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    var project = ProjectLayout.Open(destination);
                    var models = PreloadProjectModels(project, CancellationToken.None, report);
                    return new PreparedProjectLoad(project, models);
                }, prepared => OpenProject(prepared.Project, prepared.Models),
                exception => ShowProjectOperationError("另存项目失败（原项目保留）", exception, false, 3, requestedPath),
                () => SetStatus("已取消另存项目"))) return;
            _projectPathDialogMode = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _projectPathError = exception.Message;
        }
    }

    private static void CopyProjectDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"另存项目不跟随目录链接：{source}");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"另存项目不跟随文件链接：{file}");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyProjectDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private void DrawModelingTools()
    {
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        if (!_showModelingTools) return;
        SetPanelDefaults(new Vector2(display.X - layout.RightWidth, WorkspaceTop + 1F),
            new Vector2(layout.RightWidth, layout.MainHeight));
        if (_focusModelingTools) ImGui.SetNextWindowFocus();
        _focusModelingTools = false;
        if (ImGui.Begin("调色盘##建模工具窗口", PanelWindowFlags))
        {
            DrawPanelWindowMenu();
            DrawPaletteContents();
        }
        ImGui.End();
    }

    private void DrawPaletteContents() => DrawPaletteContentsCore();

    private void DrawBrushMaterialSelector() => DrawBrushMaterialSelectorCore();

    private void DrawModelMaterialSlots(SceneObject modelObject) => DrawModelMaterialSlotsCore(modelObject);

    private MaterialAsset? FindCurrentBrushMaterial()
    {
        EnsureModelBrushMaterialTarget();
        var component = FindBrushModelComponent();
        if (component is null || _brushMaterialSlot == 0) return null;
        var binding = (SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
            .FirstOrDefault(item => item.Slot == _brushMaterialSlot);
        return binding is { } selected ? FindMaterialAsset(selected.MaterialAsset) : null;
    }

    private MaterialAsset? FindMaterialAsset(string? relativePath) => string.IsNullOrWhiteSpace(relativePath) || _project is null
        ? null : _brushMaterials.FirstOrDefault(item =>
            string.Equals(Path.GetRelativePath(_project.AssetsPath, item.SourcePath).Replace('\\', '/'),
                relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private void SaveModelMaterialBindings(SceneObject modelObject, ComponentRecord component,
        VoxelModelComponentData data, Dictionary<int, MaterialSlotBinding> bindings)
    {
        //只写组件映射，不改体素内容或本地编号
        SceneComponentTypes.WriteVoxelModel(component, data with { MaterialSlots = bindings.Values.OrderBy(item => item.Slot).ToArray() });
        EnsureModelBrushMaterialTarget();
        MarkSceneDocumentDirty();
        _composedRenderWorld = null;
        _composedSignature = 0;
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        SetStatus($"已更新“{modelObject.Name}”的材质槽位映射");
    }

    private void ExportWorkingVoxels(bool selectedOnly, bool qubicle, bool native = false, bool obj = false)
    {
        if (_project is null) return;
        try
        {
            var source = _world;
            var target = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId &&
                item.Components.Any(SceneComponentTypes.IsVoxelModel));
            if (selectedOnly && target is null) { SetStatus("请先选择一个体素模型"); return; }
            source = selectedOnly ? CaptureObjectVoxels(target!) : GetRenderWorld(uint.MaxValue).Clone();
            var name = selectedOnly ? target!.Name : _project.Manifest.Name;
            var objects = _sceneDocument?.Objects ?? [];
            var primary = GetVoxelWorldObject()?.Id;
            var directory = Path.Combine(_project.AssetsPath, "Exports");
            Directory.CreateDirectory(directory);
            var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var extension = obj ? ".obj" : native ? selectedOnly ? ".mvmodel" : ".mvworld" : qubicle ? ".qb" : ".vox";
            var path = Path.Combine(directory, UniqueAssetFileName(directory, safeName, extension));
            var scope = selectedOnly ? $"选中模型：{name}" : "场景可见体素（重叠按排序合成）";
            _projectOperation.TryStart($"导出 {extension} · {scope}", path,
                (cancellation, report) =>
                {
                    var world = new VoxelWorld();
                    long processed = 0;
                    foreach (var cell in source.EnumerateOccupied())
                    {
                        if (++processed % 16384 == 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            report(new BlockingOperationProgress($"收集体素 {processed:N0} / {source.OccupiedVoxelCount:N0}\n{path}",
                                .7F * processed / Math.Max(1, source.OccupiedVoxelCount), CanCancel: true));
                        }
                        world.Set(cell.Coordinate, cell.Value);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    report(new BlockingOperationProgress($"写入 {extension} · {world.OccupiedVoxelCount:N0} 体素\n{path}", .85F, CanCancel: obj));
                    if (native)
                    {
                        if (world.OccupiedVoxelCount == 0) LargeWorldFormat.WriteEmpty(path);
                        else LargeWorldFormat.Write(path, world);
                    }
                    else if (obj) ObjVoxelExporter.Write(path, world, cancellation);
                    else if (qubicle) QubicleFormat.Write(path, world);
                    else VoxFormat.Write(path, world);
                    return world.OccupiedVoxelCount;
                }, count => SetStatus($"已导出 {extension} · {scope} · {count:N0} 体素：{path}"),
                exception => { MVoxelLog.Error("导出工作模型失败", exception); SetStatus($"导出失败：{exception.Message}"); },
                () => SetStatus("已取消模型导出，未写入目标文件"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OverflowException or ArgumentException)
        {
            MVoxelLog.Error("导出工作模型失败", exception);
            SetStatus($"导出失败：{exception.Message}");
        }
    }
}
