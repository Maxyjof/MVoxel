using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private enum HistoryStageKind { MainScene, VoxelAsset, Prefab }

    private sealed record VoxelStageHistoryData(
        string RelativePath, string AbsolutePath, MaterialExchangeMetadata Materials,
        byte[] OriginalHash, SceneDocument? SuspendedScene, Dictionary<StableId, VoxelWorld> SuspendedWorlds,
        bool SceneDirty, bool LoadFailed, StableId? Selection, StableId? EditTarget, StableId ModelId,
        StableId? InspectorLock, StableId? Pinned, Dictionary<StableId, string>? SegmentIds,
        Dictionary<string, VoxelCoord64>? OriginalSegmentOrigins, bool Saved);

    private sealed record PrefabStageHistoryData(
        string RelativePath, string AbsolutePath, PrefabAsset Original, PrefabAsset Effective,
        string? BasePath, Dictionary<string, byte[]> BaseHashes, byte[] OriginalHash,
        SceneDocument? SuspendedScene, Dictionary<StableId, VoxelWorld> SuspendedWorlds,
        bool SceneDirty, bool LoadFailed, StableId? Selection, StableId? EditTarget,
        StableId? InspectorLock, StableId? Pinned, bool Saved);

    private sealed record StageSnapshot(
        HistoryStageKind Kind, EditorHistoryState Editor,
        string ActiveSceneRelativePath, string AssetSelection, string EditingAssetPath,
        bool SceneDirty, bool LoadFailed,
        VoxelStageHistoryData? VoxelStage, PrefabStageHistoryData? PrefabStage);

    private static SceneDocument? CloneScene(SceneDocument? scene) => scene is null
        ? null : SceneSerializer.LoadFromJson(SceneSerializer.ToJson(scene));

    private static Dictionary<StableId, VoxelWorld> CloneWorlds(IReadOnlyDictionary<StableId, VoxelWorld> worlds) =>
        worlds.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());

    private static EditorHistoryState CloneEditorHistoryState(EditorHistoryState source)
    {
        var scene = CloneScene(source.Scene);
        var objects = scene?.Objects.ToDictionary(item => item.Id) ?? [];
        var environment = new HistoryEnvironment((float[])source.Environment.Pixels.Clone(), source.Environment.Width, source.Environment.Height);
        return new EditorHistoryState(scene, source.SceneJson, objects, CloneWorlds(source.Worlds),
            source.EditorSettings, source.ProjectSettings, (object?[])source.Ui.Clone(), (Rgba32[])source.Palette.Clone(),
            new Dictionary<string, object?>(source.Parameters), source.Revision, source.Generator,
            (StableId[])source.SelectedSceneObjectIds.Clone(), source.Inspectors, (string[])source.PluginPanels.Clone(),
            source.Material, source.AssetSelection, source.EditingAssetPath, environment);
    }

    private static VoxelStageHistoryData CloneVoxelStage(VoxelStageHistoryData source) =>
        new(source.RelativePath, source.AbsolutePath, source.Materials, (byte[])source.OriginalHash.Clone(),
            CloneScene(source.SuspendedScene), CloneWorlds(source.SuspendedWorlds), source.SceneDirty, source.LoadFailed,
            source.Selection, source.EditTarget, source.ModelId, source.InspectorLock, source.Pinned,
            source.SegmentIds is null ? null : new Dictionary<StableId, string>(source.SegmentIds),
            source.OriginalSegmentOrigins is null ? null : new Dictionary<string, VoxelCoord64>(source.OriginalSegmentOrigins), source.Saved);

    private static PrefabStageHistoryData ClonePrefabStage(PrefabStageHistoryData source) =>
        new(source.RelativePath, source.AbsolutePath, PrefabSerializer.Clone(source.Original), PrefabSerializer.Clone(source.Effective),
            source.BasePath, source.BaseHashes.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal),
            (byte[])source.OriginalHash.Clone(), CloneScene(source.SuspendedScene), CloneWorlds(source.SuspendedWorlds), source.SceneDirty,
            source.LoadFailed, source.Selection, source.EditTarget, source.InspectorLock, source.Pinned, source.Saved);

    private StageSnapshot CaptureStageSnapshot()
    {
        var voxel = _voxelStage is { } currentVoxel
            ? CloneVoxelStage(new VoxelStageHistoryData(currentVoxel.RelativePath, currentVoxel.AbsolutePath, currentVoxel.Materials,
                currentVoxel.OriginalHash, currentVoxel.Scene, currentVoxel.Worlds, currentVoxel.SceneDirty, currentVoxel.LoadFailed,
                currentVoxel.Selection, currentVoxel.EditTarget, currentVoxel.ModelId, currentVoxel.InspectorLock, currentVoxel.Pinned,
                currentVoxel.SegmentIds, currentVoxel.OriginalSegmentOrigins, currentVoxel.Saved)) : null;
        var prefab = _prefabStage is { } currentPrefab
            ? ClonePrefabStage(new PrefabStageHistoryData(currentPrefab.RelativePath, currentPrefab.AbsolutePath, currentPrefab.Original,
                currentPrefab.Effective, currentPrefab.BasePath, currentPrefab.BaseHashes, currentPrefab.OriginalHash, currentPrefab.Scene,
                currentPrefab.Worlds, currentPrefab.SceneDirty, currentPrefab.LoadFailed, currentPrefab.Selection, currentPrefab.EditTarget,
                currentPrefab.InspectorLock, currentPrefab.Pinned, currentPrefab.Saved)) : null;
        var kind = voxel is not null ? HistoryStageKind.VoxelAsset : prefab is not null ? HistoryStageKind.Prefab : HistoryStageKind.MainScene;
        return new StageSnapshot(kind, CloneEditorHistoryState(CaptureEditorHistory(null)), _activeSceneRelativePath,
            _assetBrowser.SelectedAssetPath, _assetBrowser.EditingAssetPath, _sceneDocumentDirty, _worldLoadFailed, voxel, prefab);
    }

    private StageSnapshot? _stageTransitionBefore;
    private void BeginStageTransition() => _stageTransitionBefore = CaptureStageSnapshot();

    private void AbortStageTransition() => _stageTransitionBefore = null;

    private void CommitStageTransition(string name)
    {
        if (_stageTransitionBefore is not { } before) return;
        _stageTransitionBefore = null;
        var after = CaptureStageSnapshot();
        _historyCommitting = true;
        try { _voxelHistory.RecordExecuted(new StageTransitionCommand(this, name, before, after)); }
        finally { _historyCommitting = false; }
        _historyCommands.Clear();
        _historyBefore = CaptureEditorHistory(null);
        _historyPriming = false;
    }

    private void RestoreStageSnapshot(StageSnapshot snapshot)
    {
        var editor = CloneEditorHistoryState(snapshot.Editor);
        _activeSceneRelativePath = snapshot.ActiveSceneRelativePath;
        _sceneDocumentDirty = snapshot.SceneDirty;
        _worldLoadFailed = snapshot.LoadFailed;
        _assetBrowser.SelectAsset(snapshot.AssetSelection);
        _assetBrowser.EditingAssetPath = snapshot.EditingAssetPath;
        _voxelStage = null; _prefabStage = null;
        RestoreHistoryScene(editor);
        _objectVoxels.RestoreReferences(editor.Worlds);
        if (snapshot.Kind == HistoryStageKind.VoxelAsset && snapshot.VoxelStage is { } voxel)
        {
            _voxelStage = new VoxelStage(voxel.RelativePath, voxel.AbsolutePath, voxel.Materials, (byte[])voxel.OriginalHash.Clone(),
                CloneScene(voxel.SuspendedScene), CloneWorlds(voxel.SuspendedWorlds), voxel.SceneDirty, voxel.LoadFailed,
                voxel.Selection, voxel.EditTarget, voxel.ModelId, voxel.InspectorLock, voxel.Pinned, _voxelHistory,
                CaptureAssetStageCamera(), voxel.SegmentIds is null ? null : new Dictionary<StableId, string>(voxel.SegmentIds),
                voxel.OriginalSegmentOrigins is null ? null : new Dictionary<string, VoxelCoord64>(voxel.OriginalSegmentOrigins), voxel.Saved);
        }
        else if (snapshot.Kind == HistoryStageKind.Prefab && snapshot.PrefabStage is { } prefab)
        {
            _prefabStage = new PrefabStage(prefab.RelativePath, prefab.AbsolutePath, PrefabSerializer.Clone(prefab.Original),
                PrefabSerializer.Clone(prefab.Effective), prefab.BasePath,
                prefab.BaseHashes.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal),
                (byte[])prefab.OriginalHash.Clone(), CloneScene(prefab.SuspendedScene), CloneWorlds(prefab.SuspendedWorlds),
                prefab.SceneDirty, prefab.LoadFailed, prefab.Selection, prefab.EditTarget, prefab.InspectorLock, prefab.Pinned,
                _voxelHistory, CaptureAssetStageCamera(), prefab.Saved);
        }
        RestoreHistoryUi(editor, true, false);
        _historyBefore = CaptureEditorHistory(null); _historyPriming = false;
    }

    private sealed class StageTransitionCommand(ImGuiEditorWindow editor, string name, StageSnapshot before, StageSnapshot after) : IEditorCommand
    {
        public string Name => name;
        public void Execute() => Apply(after);
        public void Undo() => Apply(before);
        private void Apply(StageSnapshot target)
        {
            var rollback = editor.CaptureStageSnapshot();
            using var suspended = FileEditHistory.Suspend();
            editor._historyReplaying = true;
            try { editor.RestoreStageSnapshot(target); }
            catch
            {
                try { editor.RestoreStageSnapshot(rollback); }
                catch (Exception rollbackException) { MVoxelLog.Error("阶段撤回回滚失败", rollbackException); }
                throw;
            }
            finally { editor._historyReplaying = false; }
        }
    }
}
