using System.Reflection;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Editor.Settings;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private int _historyRepeatDirection;
    private (IEditorCommand Command, bool Forward, string Warning)? _historyConfirmation;
    private bool _openHistoryConfirmation;
    /// <summary>自动化已回答，下一帧在弹窗作用域内补关弹窗</summary>
    private bool _historyConfirmationResolved;
    private bool _historyAwaitKeyRelease;
    private bool _historyPriming = true;
    private int _historyLayoutRestoreFrame = -2;
    private long _historySceneRevision;
    private bool _historyInitialized, _historyReplaying, _historyCommitting;
    private EditorHistoryState? _historyBefore;
    private readonly List<IEditorCommand> _historyCommands = [];
    private FileEditHistory? _fileEditHistory;
    private sealed record HistoryEnvironment(float[] Pixels, int Width, int Height);
    private HistoryEnvironment _historyEnvironment = new([], 0, 0);
    private FileEditHistory _historyFiles => _fileEditHistory ??= new FileEditHistory(AcceptsHistoryFile);

    private void DrawHistoryConfirmationDialog()
    {
        const string title = "确认撤销或重做##HistoryWarning";
        if (_openHistoryConfirmation)
        {
            ImGui.OpenPopup(title);
            _openHistoryConfirmation = false;
        }
        if (_historyConfirmation is not { } pending)
        {
            //自动化回答后弹窗还开着，补一帧把它关掉，避免空弹窗挡住输入
            if (_historyConfirmationResolved && CenteredModal.Begin(title, ImGuiWindowFlags.AlwaysAutoResize))
            {
                _historyConfirmationResolved = false;
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
            return;
        }
        var displayWidth = ImGui.GetIO().DisplaySize.X;
        // 提示内容通常只有一两行，窗口宽度按字号取，过宽会把画面完全挡住
        var dialogWidth = Math.Clamp(ImGui.GetFontSize() * 24F, 320F, displayWidth * 0.6F);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(dialogWidth, 0F), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(280F, 0F), ImGui.GetIO().DisplaySize * .9F);
        if (!CenteredModal.Begin(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped($"即将{(pending.Forward ? "重做" : "撤销")}：{pending.Command.Name}");
        ImGui.Separator();
        var warningHeight = Math.Min(ImGui.GetIO().DisplaySize.Y * .5F,
            ImGui.CalcTextSize(pending.Warning, false, Math.Max(1F, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize)).Y + ImGui.GetStyle().WindowPadding.Y * 2F);
        ImGui.BeginChild("##HistoryWarningDetails", new System.Numerics.Vector2(0F, warningHeight), ImGuiChildFlags.Borders);
        ImGui.TextWrapped(pending.Warning);
        ImGui.EndChild();
        ImGui.Spacing();
        ImGui.TextWrapped("请确认以上影响。取消不会修改数据或移动撤销/重做记录。");
        ImGui.Spacing();
        var confirm = ImGui.Button("确认执行");
        ImGui.SameLine();
        var cancel = ImGui.Button("取消") || ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        ImGui.SetItemDefaultFocus();
        if (confirm || cancel)
        {
            _historyConfirmation = null;
            _historyRepeatDirection = 0;
            _undoHeldSeconds = _undoRepeatAccumulator = 0F;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        if (confirm) ExecuteHistory(pending.Command, pending.Forward);
    }

    // Only user-controlled values, never timers, GPU objects, diagnostics or transient popup state.
    private static readonly FieldInfo[] HistoryUiFields = new[]
    {
        nameof(_activeTool), nameof(_voxelEditTargetId), nameof(_inspectorLockedObjectId), nameof(_selectedSceneObjectId), nameof(_pinnedVoxelModelId),
        nameof(_sceneGizmoMode), nameof(_brushShape), nameof(_brushRadius), nameof(_paletteIndex), nameof(_brushEmissive), nameof(_brushMaterialSlot),
        nameof(_renderCameraId), nameof(_showGrid), nameof(_showVoxelOutlines),
        nameof(_showHierarchy), nameof(_showInspector), nameof(_showProjectAssets), nameof(_showModelingTools), nameof(_showRenderConfig),
        nameof(_showGeneratorPanel), nameof(_showVoxelMeasurements), nameof(_showPerformanceAnalyzer), nameof(_showRenderStatistics),
        nameof(_activeLayoutFile), nameof(_activeLayoutName), nameof(_layoutPreset), nameof(_activeSceneRelativePath),
        nameof(_generatorSource), nameof(_generatorRelativePath), nameof(_generatorDirty), nameof(_paletteIsProjectLevel), nameof(_selectedProjectPalettePath),
        nameof(_sampledBrushColor), nameof(_hollowBox), nameof(_sceneSliceEnabled), nameof(_sceneSliceAxis), nameof(_sceneSliceCoordinate),
        nameof(_sceneSliceKeepGreater), nameof(_worldLoadFailed),
        nameof(_editingMaterialPath), nameof(_centerTab), nameof(_showSceneStatistics), nameof(_editPanelLayout),
        nameof(_showSettingsWindow), nameof(_settingsScope), nameof(_editorSettingsCategory), nameof(_projectSettingsCategory),
    }.Select(name => typeof(ImGuiEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic))
        .Select(field => field ?? throw new InvalidOperationException("撤销状态字段不存在")).ToArray();

    private bool AcceptsHistoryFile(string path)
    {
        if (_historyReplaying) return false;
        if (string.Equals(path, SettingsStore.EditorSettingsPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, _project?.ProjectSettingsPath, StringComparison.OrdinalIgnoreCase)) return false;
        static bool Within(string path, string root) => path.StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        return (_project is not null && Within(path, _project.AssetsPath)) || Within(path, AppPaths.ConfigDirectory);
    }

    private void BeginEditorHistory()
    {
        if (!_historyInitialized)
        {
            _historyInitialized = true;
            _voxelHistory.RecordHandler = command =>
            {
                if (_historyCommitting || _historyReplaying) return false;
                _historyCommands.Add(command);
                return true;
            };
            _voxelHistory.Cleared += ResetEditorHistory;
        }
        _historyBefore ??= CaptureEditorHistory(null);
    }

    private void ResetEditorHistory()
    {
        _historyConfirmation = null;
        _historyConfirmationResolved = false;
        _openHistoryConfirmation = false;
        foreach (var command in _historyCommands) (command as IDisposable)?.Dispose();
        _historyCommands.Clear();
        _historyFiles.Reset();
        _historyBefore = null;
        _historyPriming = true;
    }

    private EditorHistoryState CaptureEditorHistory(EditorHistoryState? previous)
    {
        _editingMaterial?.Normalize();
        var sameScene = previous is not null && ReferenceEquals(previous.Scene, _sceneDocument) &&
            previous.Revision == _historySceneRevision && _historyCommands.Count == 0;
        return new EditorHistoryState(_sceneDocument,
            sameScene ? previous!.SceneJson : _sceneDocument is null ? null : SceneSerializer.ToJson(_sceneDocument),
            _sceneDocument?.Objects.ToDictionary(item => item.Id) ?? [], _objectVoxels.CaptureReferences(),
            SettingsStore.Snapshot(_editorSettings), SettingsStore.Snapshot(_projectSettings),
            HistoryUiFields.Select(field => field.GetValue(this)).ToArray(), (Rgba32[])_palette.Clone(),
            new Dictionary<string, object?>(_generatorParameters), _historySceneRevision,
            _generatorAsset is null ? null : SettingsStore.Snapshot(_generatorAsset),
            CaptureLayout(), SettingsStore.Snapshot(_extraInspectors), _visiblePluginPanels.ToArray(),
            _editingMaterial is null ? null : SettingsStore.Snapshot(_editingMaterial),
            _assetBrowser.SelectedAssetPath, _historyEnvironment);
    }

    private void EndEditorHistory(bool force = false)
    {
        if (_historyReplaying) return;
        BeginEditorHistory();
        TrackConfigurationChanges();
        if (!force && (ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle) || _sceneGizmoDragging || _sceneMouseCaptured || _resetDockLayoutRequested ||
            _sceneFocusAnimation is not null || _renderFocusAnimation is not null)) return;
        var before = _historyBefore!;
        var after = CaptureEditorHistory(before);
        // ImGui 在后续帧把恢复的停靠布局应用到活动窗口；这不是用户的新编辑。
        if (ImGui.GetFrameCount() <= _historyLayoutRestoreFrame + 1) before = before with { Layout = after.Layout };
        var files = _historyFiles.Complete();
        var changed = before.SceneJson != after.SceneJson || before.EditorSettings != after.EditorSettings ||
            before.Generator != after.Generator || before.Layout != after.Layout || before.Inspectors != after.Inspectors ||
            before.Material != after.Material || before.AssetSelection != after.AssetSelection ||
            !ReferenceEquals(before.Environment, after.Environment) ||
            !before.PluginPanels.SequenceEqual(after.PluginPanels) ||
            before.ProjectSettings != after.ProjectSettings || !before.Ui.SequenceEqual(after.Ui) ||
            !before.Palette.SequenceEqual(after.Palette) || !before.Parameters.OrderBy(pair => pair.Key).SequenceEqual(after.Parameters.OrderBy(pair => pair.Key)) ||
            WorldReferencesChanged(before, after);
        if ((changed && !_historyPriming) || files is not null || _historyCommands.Count > 0 || before.SceneJson != after.SceneJson)
        {
            var created = after.Objects.Keys.Count(id => !before.Objects.ContainsKey(id));
            var removed = before.Objects.Keys.Count(id => !after.Objects.ContainsKey(id));
            var name = _historyCommands.FirstOrDefault()?.Name ?? (created > 0 && removed > 0 ? "导入或替换场景对象" :
                created > 0 ? $"创建对象（{created}）" : removed > 0 ? $"移除对象（{removed}）" : files is not null ? "资产与配置修改" :
                before.SceneJson != after.SceneJson ? "场景属性与层级修改" :
                before.EditorSettings != after.EditorSettings || before.ProjectSettings != after.ProjectSettings ? "配置修改" : "编辑器操作");
            _historyCommitting = true;
            try { _voxelHistory.RecordExecuted(new EditorTransaction(this, name, before, after, _historyCommands.ToArray(), files)); }
            finally { _historyCommitting = false; }
        }
        _historyCommands.Clear();
        _historyBefore = after;
        _historyPriming = false;
    }

    private sealed record EditorHistoryState(SceneDocument? Scene, string? SceneJson,
        Dictionary<StableId, SceneObject> Objects, Dictionary<StableId, VoxelWorld> Worlds,
        string EditorSettings, string ProjectSettings, object?[] Ui, Rgba32[] Palette,
        Dictionary<string, object?> Parameters, long Revision, string? Generator, string Layout,
        string Inspectors, string[] PluginPanels, string? Material, string AssetSelection, HistoryEnvironment Environment);

    private static bool WorldReferencesChanged(EditorHistoryState before, EditorHistoryState after) =>
        before.Worlds.Keys.Union(after.Worlds.Keys).Any(id =>
        {
            before.Worlds.TryGetValue(id, out var oldWorld);
            after.Worlds.TryGetValue(id, out var newWorld);
            return !ReferenceEquals(oldWorld, newWorld) &&
                ((oldWorld?.OccupiedVoxelCount ?? 0) > 0 || (newWorld?.OccupiedVoxelCount ?? 0) > 0);
        });

    private void RestoreHistoryScene(EditorHistoryState state)
    {
        _sceneDocument = state.Scene;
        if (state.Scene is not null && state.SceneJson is not null)
        {
            var source = SceneSerializer.LoadFromJson(state.SceneJson);
            state.Scene.SceneId = source.SceneId;
            state.Scene.FormatVersion = source.FormatVersion;
            state.Scene.Name = source.Name;
            state.Scene.LayerNames = source.LayerNames;
            state.Scene.HiddenLayerNames = source.HiddenLayerNames;
            state.Scene.SortingLayerNames = source.SortingLayerNames;
            state.Scene.Objects = source.Objects.Select(item =>
            {
                var target = state.Objects[item.Id];
                SceneObjectStateCommand.Copy(target, item);
                return target;
            }).ToList();
        }
        _objectVoxels.RestoreReferences(state.Worlds);
        // 曲线渲染器与树生成器的体素由组件参数生成，历史快照里的世界引用可能来自另一时刻；
        // 先摘掉这些对象的世界，让 RebuildObjectVoxelWorld 按恢复后的组件重新生成，
        // 否则会出现组件参数已撤销、画面上的体素却停在撤销前的情况
        if (_sceneDocument is not null)
        {
            foreach (var sceneObject in _sceneDocument.Objects)
            {
                if (GetGeneratedVoxelComponent(sceneObject) is not null) _objectVoxels.Remove(sceneObject.Id);
            }
        }
    }

    private void RestoreHistoryUi(EditorHistoryState state, bool sceneChanged, bool assetsChanged)
    {
        _editorSettings = SettingsStore.Restore<EditorSettingsDocument>(state.EditorSettings);
        _projectSettings = SettingsStore.Restore<ProjectSettingsDocument>(state.ProjectSettings);
        ApplyEditorSettingsToUi();
        if (_project is not null) ApplyProjectSettingsToUi();
        for (var index = 0; index < HistoryUiFields.Length; index++) HistoryUiFields[index].SetValue(this, state.Ui[index]);
        _palette = (Rgba32[])state.Palette.Clone();
        _editingMaterial = state.Material is null ? null : SettingsStore.Restore<MaterialAsset>(state.Material);
        if (_editingMaterial is not null) _editingMaterial.SourcePath = _editingMaterialPath;
        if (!ReferenceEquals(_historyEnvironment, state.Environment))
        {
            _historyEnvironment = state.Environment;
            _pipeline?.SetEnvironment(state.Environment.Pixels, state.Environment.Width, state.Environment.Height);
        }
        _generatorParameters.Clear();
        foreach (var pair in state.Parameters) _generatorParameters.Add(pair.Key, pair.Value);
        var generatorChanged = (_generatorAsset is null ? null : SettingsStore.Snapshot(_generatorAsset)) != state.Generator;
        _generatorAsset = state.Generator is null ? null : SettingsStore.Restore<GeneratorAsset>(state.Generator);
        if (generatorChanged && _generatorAsset is not null) DescribeGeneratorForUi(_generatorAsset);
        if (_generatorAsset is null) _generatorDefinition = null;
        if (CaptureLayout() != state.Layout) ImGui.LoadIniSettingsFromMemory(state.Layout);
        _historyLayoutRestoreFrame = ImGui.GetFrameCount();
        _requestedCenterTab = _centerTab;
        _extraInspectors.Clear();
        _extraInspectors.AddRange(SettingsStore.Restore<List<ExtraInspector>>(state.Inspectors));
        _visiblePluginPanels.Clear();
        foreach (var panel in state.PluginPanels) _visiblePluginPanels.Add(panel);
        _lastEditorSettingsSnapshot = state.EditorSettings;
        _lastProjectSettingsSnapshot = state.ProjectSettings;
        QueueConfigurationAutoSave();
        EditorPalette.ReloadThemes();
        EditorPalette.ApplyTheme(_editorSettings.Theme);
        ApplyImGuiStyle();
        _voxelModelReferenceCacheRoot = null;
        _assetPreviewPath = string.Empty;
        _assetBrowser.Reset();
        if (!string.IsNullOrEmpty(state.AssetSelection)) _assetBrowser.SelectAsset(state.AssetSelection);
        _lastAssetSelectionPath = state.AssetSelection;
        _prefabCache.Clear();
        if (assetsChanged) ReloadMaterialTable();
        _selectedVoxel = null;
        _sceneGizmoDragging = false;
        _toolDragAnchor = null;
        _toolDragCurrent = null;
        _toolDragTargetId = null;
        _sceneFocusAnimation = null;
        _renderFocusAnimation = null;
        if (sceneChanged) RebuildObjectVoxelWorld();
        ResetAfterVoxelTransform();
    }

    private sealed class EditorTransaction(ImGuiEditorWindow editor, string name, EditorHistoryState before,
        EditorHistoryState after, IEditorCommand[] commands, IEditorCommand? files) : IEditorCommand, IDisposable, IHistoryWarning
    {
        public string Name => name;
        public string? GetWarning(bool forward)
        {
            var warnings = new List<string>();
            if (files is IHistoryWarning warning && warning.GetWarning(forward) is { } fileWarning) warnings.Add(fileWarning);
            foreach (var child in commands.OfType<IHistoryWarning>())
                if (child.GetWarning(forward) is { } childWarning) warnings.Add(childWarning);
            var source = forward ? before : after;
            var target = forward ? after : before;
            var removed = source.Objects.Keys.Count(id => !target.Objects.ContainsKey(id));
            if (removed > 0) warnings.Add($"此操作会从场景移除 {removed} 个对象及其组件和模型数据。");
            // 体素数据的撤销重做属于日常编辑，直接用新的世界引用覆盖旧引用即可，不再弹窗确认；
            // 只有对象增删和磁盘文件冲突这类真正不可逆的后果才需要提醒。
            return warnings.Count == 0 ? null : string.Join("\n\n", warnings);
        }
        public void Execute() => Apply(true);
        public void Undo() => Apply(false);
        private void Apply(bool forward)
        {
            using var suspended = FileEditHistory.Suspend();
            editor._historyReplaying = true;
            var completed = new List<IEditorCommand>();
            var filesApplied = false;
            var sceneChanged = before.SceneJson != after.SceneJson || commands.Any(command => command is VoxelStrokeCommand or SceneObjectStateCommand) ||
                WorldReferencesChanged(before, after);
            var assetsChanged = files is not null || commands.Any(command => command is IHistoryWarning);
            try
            {
                // Check/restore disk first; conflicts cannot leave model state half-reverted.
                if (files is not null) { if (forward) files.Execute(); else files.Undo(); filesApplied = true; }
                // New/deleted objects must exist while the retained voxel callbacks run.
                if (sceneChanged) editor.RestoreHistoryScene(after);
                if (sceneChanged && editor._sceneDocument is not null)
                    foreach (var item in before.Objects.Values.Where(item => !after.Objects.ContainsKey(item.Id)))
                        editor._sceneDocument.Objects.Add(item);
                foreach (var command in forward ? commands : commands.Reverse())
                {
                    if (forward) command.Execute(); else command.Undo();
                    completed.Add(command);
                }
                if (sceneChanged) editor.RestoreHistoryScene(forward ? after : before);
                if (sceneChanged) editor.RefreshEditedVoxelAssetsAfterHistory();
                editor.RestoreHistoryUi(forward ? after : before, sceneChanged, assetsChanged);
                if (sceneChanged)
                    editor.MarkSceneDocumentDirty();

            }
            catch
            {
                foreach (var command in completed.AsEnumerable().Reverse())
                    if (forward) command.Undo(); else command.Execute();
                if (filesApplied) { if (forward) files!.Undo(); else files!.Execute(); }
                if (sceneChanged) editor.RestoreHistoryScene(forward ? before : after);
                editor.RestoreHistoryUi(forward ? before : after, sceneChanged, assetsChanged);
                throw;
            }
            finally
            {
                editor._historyReplaying = false;
                editor._historyBefore = editor.CaptureEditorHistory(null);
            }
        }
        public void Dispose()
        {
            foreach (var command in commands) (command as IDisposable)?.Dispose();
            (files as IDisposable)?.Dispose();
        }
    }
}
