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
    private bool _showModelingTools = true;
    private bool _showGettingStarted;
    private bool _resetDockLayoutRequested;
    private bool _focusModelingTools;
    private Action? _afterProjectClose;
    private bool _allowProjectReplacement;
    private StableId? _repaintModelId;
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
        if (!_showModelingTools) return;
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        SetPanelDefaults(new Vector2(0F, WorkspaceTop + 1F), new Vector2(layout.LeftWidth, 210F));
        if (ImGui.Begin("工具##建模工具箱", PanelWindowFlags)) DrawBrushToolbarRow();
        ImGui.End();
        SetPanelDefaults(new Vector2(display.X - layout.RightWidth, WorkspaceTop + 1F),
            new Vector2(layout.RightWidth, layout.MainHeight));
        if (_focusModelingTools) ImGui.SetNextWindowFocus();
        _focusModelingTools = false;
        if (ImGui.Begin("颜色与笔刷##建模工具窗口", ref _showModelingTools, PanelWindowFlags))
        {
            var target = GetPinnedVoxelModelObject() ?? _sceneDocument?.Objects.FirstOrDefault(item =>
                item.Id == _selectedSceneObjectId && item.Components.Any(SceneComponentTypes.IsVoxelModel)) ?? GetVoxelWorldObject();
            if (target is not null)
            {
                ImGui.TextUnformatted("目标");
                ImGui.SameLine();
                ImGui.TextWrapped(target.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("未锁定时，命中的模型自动成为编辑目标；锁定后只修改该模型。");
                if (ImGui.Button(target.Locked ? "解除目标锁定" : "锁定编辑目标")) ToggleSceneObjectLock(target);
            }
            else ImGui.TextUnformatted("目标：首次绘制自动创建");
            if (ImGui.Button("新建体素模型", new Vector2(-1F, ImGui.GetFrameHeight()))) CreateVoxelModel();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("创建独立模型。也可以在空项目中直接落笔，自动建立第一个模型。");
            var color = _sampledBrushColor ?? _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
            ImGui.ColorButton("当前笔刷色", new Vector4(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F), ImGuiColorEditFlags.NoTooltip,
                new Vector2(ImGui.GetFrameHeight() * 2F, ImGui.GetFrameHeight()));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"颜色 {_paletteIndex} · RGB({color.R}, {color.G}, {color.B}) · A {color.A}");
            ImGui.SameLine();
            ImGui.TextUnformatted(_sampledBrushColor.HasValue ? "取样色" : $"颜色 {_paletteIndex}");
            DrawBrushProperties();
            var material = _brushMaterials.FirstOrDefault(item => item.Slot == _brushMaterialSlot);
            if (material is not null && ImGui.Button("编辑共享材质", new Vector2(-1F, ImGui.GetFrameHeight())))
            {
                _assetBrowser.SelectAsset(Path.GetRelativePath(_project!.AssetsPath, material.SourcePath));
                _showInspector = true;
                _requestedInspectorTab = 0;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("修改此材质槽会影响所有引用它的体素；选择笔刷颜色不会重绘已有方块。");
            ImGui.BeginDisabled(target is null);
            if (ImGui.Button("重绘当前模型…", new Vector2(-1F, ImGui.GetFrameHeight())))
            {
                _repaintModelId = target?.Id;
                ImGui.OpenPopup("重绘模型确认");
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("将当前颜色、材质槽与发光标记应用到此模型已有方块，不添加新方块；整次操作可撤销。");
            ImGui.EndDisabled();
            if (ImGui.BeginPopupModal("重绘模型确认", ImGuiWindowFlags.AlwaysAutoResize))
            {
                var repaintTarget = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _repaintModelId);
                ImGui.TextWrapped($"将当前笔刷应用到“{repaintTarget?.Name}”的所有已有方块。共享材质的参数不会被修改。");
                if (ImGui.Button("应用并保留撤销"))
                {
                    if (repaintTarget is not null)
                    {
                        var primaryId = GetVoxelWorldObject()?.Id;
                        var cells = _world.EnumerateOccupied().Where(cell =>
                            VoxelEditOwnership.ResolveOwner(cell.Coordinate, primaryId, _sceneDocument!.Objects, _layerOwnership) == repaintTarget.Id)
                            .Select(cell => cell.Coordinate).ToArray();
                        CommitCells("重绘模型", cells, CurrentBrushValue(false), repaintTarget.Id);
                    }
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        ImGui.End();
    }

    private void DrawGettingStarted()
    {
        if (!_showGettingStarted) return;
        ImGui.SetNextWindowSize(new Vector2(700F, 570F), ImGuiCond.Appearing);
        if (ImGui.Begin("入门与 MagicaVoxel 迁移指南", ref _showGettingStarted))
        {
            ImGui.TextWrapped("从第一笔到导出");
            ImGui.Separator();
            ImGui.TextWrapped("1. 项目 → 新建项目，填写项目名称和保存位置。空项目可以直接从网格上绘制，也可以先新建体素模型。");
            ImGui.TextWrapped("2. 在建模工具中选绘制、颜色和笔刷。左键放置，Shift+左键擦除；右键环绕，中键平移，滚轮缩放。相机工具不会改动模型。");
            ImGui.TextWrapped("3. 命中哪个模型就编辑哪个模型。锁定编辑目标后，新方块始终归属该模型；隐藏对象、图层或禁用组件后不能编辑。");
            ImGui.TextWrapped("4. 笔刷颜色用于新绘制的方块；材质槽可以被多个模型共享。修改共享材质会影响所有引用体素。");
            ImGui.TextWrapped("5. 场景用于建模，渲染用于观察最终画面。切面查看只影响场景，不删除体素，也不改变导出内容。");
            ImGui.TextWrapped("6. 项目 → 保存项目写入正式文件。自动保存是恢复快照，不能替代正式保存。退出、关闭和切换项目会保护未保存修改。");
            ImGui.TextWrapped("7. 项目 → 导出默认使用 MVoxel 原生 .mvmodel/.mvworld，保留更高的尺寸上限和材质槽；完整场景与材质定义用另存项目。MagicaVoxel/Qubicle 仅是兼容交换入口。PNG/HDR 使用当前渲染相机。输出均在 Assets/Exports，不覆盖同名文件。");
            ImGui.SeparatorText("熟悉 MagicaVoxel 时需要注意");
            ImGui.TextWrapped("MVoxel 将项目、场景对象、体素数据、图层和材质槽分开管理；色板仍用于绘制，但场景对象不是色板项。先用“小屋与花园”熟悉对象归属，再使用大型场景模板。");
            if (ImGui.Button("显示建模工具")) { _showModelingTools = true; _focusModelingTools = true; }
            ImGui.SameLine();
            if (ImGui.Button("快捷键方案")) OpenSettings(0, 2, "editor.input.shortcuts");
            ImGui.SameLine();
            if (ImGui.Button("新建项目")) ShowProjectPathDialog(create: true);
        }
        ImGui.End();
    }

    private void ExportWorkingVoxels(bool selectedOnly, bool qubicle, bool native = false)
    {
        if (_project is null) return;
        try
        {
            var source = _world;
            var target = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId &&
                item.Components.Any(SceneComponentTypes.IsVoxelModel));
            if (selectedOnly && target is null) { SetStatus("请先选择一个体素模型"); return; }
            var name = selectedOnly ? target!.Name : _project.Manifest.Name;
            var objects = _sceneDocument?.Objects ?? [];
            var primary = GetVoxelWorldObject()?.Id;
            var directory = Path.Combine(_project.AssetsPath, "Exports");
            Directory.CreateDirectory(directory);
            var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var extension = native ? selectedOnly ? ".mvmodel" : ".mvworld" : qubicle ? ".qb" : ".vox";
            var path = Path.Combine(directory, UniqueAssetFileName(directory, safeName, extension));
            var scope = selectedOnly ? $"选中模型：{name}" : "整个场景体素（含隐藏对象）";
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
                        if (!selectedOnly || VoxelEditOwnership.ResolveOwner(cell.Coordinate, primary, objects, _layerOwnership) == target!.Id)
                            world.Set(cell.Coordinate, cell.Value);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    report(new BlockingOperationProgress($"写入 {extension} · {world.OccupiedVoxelCount:N0} 体素\n{path}", .85F, CanCancel: false));
                    if (native)
                    {
                        if (world.OccupiedVoxelCount == 0) LargeWorldFormat.WriteEmpty(path);
                        else LargeWorldFormat.Write(path, world);
                    }
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
