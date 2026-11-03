using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void LoadObjectVoxelWorlds(ProjectHandle project)
    {
        _objectVoxels.Clear();
        if (_sceneDocument is null) return;
        var keys = GetObjectModelLoadKeys(project, _sceneDocument).ToDictionary(item => item.ObjectId, item => item.Key);
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            try
            {
                var data = SceneComponentTypes.ReadVoxelModel(component);
                var world = string.IsNullOrWhiteSpace(data.Model)
                    ? EmbeddedVoxelData.HasData(component) ? EmbeddedVoxelData.Read(component) : new VoxelWorld()
                    : keys.TryGetValue(item.Id, out var key) ? LargeWorldFormat.Read(key.Path, key.Origin)
                        : throw new InvalidDataException($"模型资产不存在：{data.Model}");
                _objectVoxels.SetWorld(item.Id, world);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                _worldLoadFailed = true;
                MVoxelLog.Error("对象体素加载失败", exception);
            }
        }
        RebuildObjectVoxelWorld();
    }

    private void RebuildObjectVoxelWorld()
    {
        if (_sceneDocument is null) return;
        _world = _objectVoxels.Compose(_sceneDocument, uint.MaxValue, false);
        _layerOwnership.Clear();
        InvalidateComposedWorldCache();
        _pickVersion++;
        _pipeline?.ResetAccumulation();
    }

    private void SaveObjectVoxelWorlds(ProjectHandle project)
    {
        if (_sceneDocument is null) return;
        foreach (var item in _sceneDocument.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null || !_objectVoxels.TryGetWorld(item.Id, out var world)) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (string.IsNullOrWhiteSpace(data.Model)) EmbeddedVoxelData.Write(component, world);
            else
            {
                // 外部资产是共享来源；实例编辑转为内嵌数据，不覆盖其他实例引用的文件。
                SceneComponentTypes.WriteVoxelModel(component, data with { VoxelCount = world.OccupiedVoxelCount });
            }
        }
    }

    private Action<bool> ObjectEditCallback(StableId id, Action<bool>? transform = null)
    {
        var component = _sceneDocument!.Objects.First(item => item.Id == id)
            .Components.First(SceneComponentTypes.IsVoxelModel);
        var before = component.Data.Clone();
        var data = SceneComponentTypes.ReadVoxelModel(component);
        return forward =>
        {
            component = _sceneDocument!.Objects.First(item => item.Id == id)
                .Components.First(SceneComponentTypes.IsVoxelModel);
            component.Data = before.Clone();
            if (forward) SceneComponentTypes.WriteVoxelModel(component, data with
            {
                Model = string.Empty, VoxelCount = _objectVoxels.GetOrCreate(id).OccupiedVoxelCount,
            });
            transform?.Invoke(forward);
            RebuildObjectVoxelWorld();
        };
    }

    private string CreateObjectRecoveryJson()
    {
        var snapshot = SceneSerializer.LoadFromJson(SceneSerializer.ToJson(_sceneDocument ?? new SceneDocument()));
        foreach (var item in snapshot.Objects)
        {
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is not null && _objectVoxels.TryGetWorld(item.Id, out var world))
                EmbeddedVoxelData.Write(component, world);
        }
        return SceneSerializer.ToJson(snapshot);
    }
}
