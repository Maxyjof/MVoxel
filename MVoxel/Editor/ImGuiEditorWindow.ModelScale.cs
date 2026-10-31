using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 体素模型对象的倍数缩放：原始模型单独备份，缩放始终以原始数据为准重采样，
/// 缩小后体素数量减少；重置缩放会恢复备份内容。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private sealed record ScaleOriginalMeta(float OriginX, float OriginY, float OriginZ);

    private static readonly JsonSerializerOptions ScaleMetaJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>项目内保存缩放原件的目录，不放在 Assets 下，避免出现在资产浏览器</summary>
    private string ScaleOriginalDirectory => Path.Combine(_project!.RootPath, "ProjectSettings", "ModelOriginals");

    private string ScaleOriginalModelPath(SceneObject sceneObject) =>
        Path.Combine(ScaleOriginalDirectory, sceneObject.Id.ToString() + ".mvmodel");

    private string ScaleOriginalMetaPath(SceneObject sceneObject) =>
        Path.Combine(ScaleOriginalDirectory, sceneObject.Id.ToString() + ".json");

    /// <summary>判断对象是否已有可用于重置和再次缩放的原始备份</summary>
    private bool HasScaleOriginal(SceneObject sceneObject) =>
        File.Exists(ScaleOriginalModelPath(sceneObject)) && File.Exists(ScaleOriginalMetaPath(sceneObject));

    /// <summary>
    /// 读取对象的原始备份；备份以包围盒最小角为原点保存，读回时按记录的原点还原世界坐标
    /// </summary>
    private bool TryReadScaleOriginal(SceneObject sceneObject, out VoxelWorld world, out VoxelCoord64 origin)
    {
        world = new VoxelWorld();
        origin = VoxelCoord64.Zero;
        try
        {
            if (!HasScaleOriginal(sceneObject)) return false;
            var meta = JsonSerializer.Deserialize<ScaleOriginalMeta>(
                File.ReadAllText(ScaleOriginalMetaPath(sceneObject)), ScaleMetaJson);
            if (meta is null) return false;
            origin = new VoxelCoord64(
                (long)Math.Round(meta.OriginX), (long)Math.Round(meta.OriginY), (long)Math.Round(meta.OriginZ));
            world = LargeWorldFormat.Read(ScaleOriginalModelPath(sceneObject), origin);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            MVoxelLog.Error($"读取缩放原件失败：{sceneObject.Name}", exception);
            SetStatus($"读取缩放原件失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把对象当前的体素保存为缩放原件；原件使用局部坐标保存，并记录包围盒最小角
    /// </summary>
    private bool TryWriteScaleOriginal(SceneObject sceneObject, VoxelWorld snapshot)
    {
        try
        {
            if (!snapshot.TryGetBounds(out var minimum, out _))
            {
                SetStatus("模型为空，无法建立缩放原始备份");
                return false;
            }

            Directory.CreateDirectory(ScaleOriginalDirectory);
            var local = new VoxelWorld();
            foreach (var cell in snapshot.EnumerateOccupied())
                local.Set(cell.Coordinate - minimum, cell.Value);
            LargeWorldFormat.Write(ScaleOriginalModelPath(sceneObject), local);
            var meta = new ScaleOriginalMeta((float)minimum.X, (float)minimum.Y, (float)minimum.Z);
            AtomicFile.WriteText(ScaleOriginalMetaPath(sceneObject), JsonSerializer.Serialize(meta, ScaleMetaJson));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            MVoxelLog.Error($"写入缩放原件失败：{sceneObject.Name}", exception);
            SetStatus($"写入缩放原件失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 收集对象当前在世界中的体素；主模型对象取不属于任何分层对象的体素
    /// </summary>
    private VoxelWorld CaptureObjectVoxels(SceneObject sceneObject)
    {
        return _objectVoxels.GetOrCreate(sceneObject.Id).Clone();
    }

    private HashSet<VoxelCoord64> CollectOwnedUnion()
    {
        var owned = new HashSet<VoxelCoord64>();
        foreach (var cells in _layerOwnership.Values) owned.UnionWith(cells);
        return owned;
    }

    private bool IsPrimaryModelObject(SceneObject sceneObject) =>
        GetVoxelWorldObject()?.Id == sceneObject.Id;

    /// <summary>
    /// 应用模型缩放。缩放始终从原始备份重采样，因此反复缩放不会累积精度损失。
    /// </summary>
    private void ApplyModelScale(SceneObject sceneObject, float factor)
    {
        if (_project is null || _sceneDocument is null) return;
        if (sceneObject.Transform.Mode != TransformMode.Grid)
        {
            SetStatus("只有体素模型对象支持倍数缩放");
            return;
        }

        var modelComponent = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (modelComponent is null)
        {
            SetStatus("对象没有体素模型组件");
            return;
        }

        factor = Math.Clamp(factor, 0.125F, 8F);
        if (Math.Abs(factor - 1F) < 0.0001F)
        {
            ResetModelScale(sceneObject, modelComponent);
            return;
        }

        VoxelWorld source;
        VoxelCoord64 sourceOrigin;
        if (HasScaleOriginal(sceneObject))
        {
            if (!TryReadScaleOriginal(sceneObject, out source, out sourceOrigin)) return;
        }
        else
        {
            source = CaptureObjectVoxels(sceneObject);
            if (source.OccupiedVoxelCount == 0)
            {
                SetStatus("模型为空，无法缩放");
                return;
            }

            source.TryGetBounds(out sourceOrigin, out _);
            if (!TryWriteScaleOriginal(sceneObject, source)) return;
        }

        try
        {
            var scaled = VoxelRescaler.Scale(source, factor, sourceOrigin, out var newOrigin,
                _scaleCancellation.Token, null, 50_000_000);
            if (!ReplaceObjectVoxels(sceneObject, scaled, newOrigin, scale: factor))
            {
                SetStatus("缩放结果与其他对象重叠，未应用");
                return;
            }

            var data = SceneComponentTypes.ReadVoxelModel(modelComponent);
            SceneComponentTypes.WriteVoxelModel(modelComponent,
                data with { VoxelCount = scaled.OccupiedVoxelCount, Scale = factor });
            MarkSceneDocumentDirty();
            ResetAfterVoxelTransform();
            SetStatus($"已缩放模型为 {factor:0.###}x：{scaled.OccupiedVoxelCount:N0} 体素");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            MVoxelLog.Error($"缩放模型失败：{sceneObject.Name}", exception);
            SetStatus($"缩放失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 恢复模型原始尺寸；需要原件的备份仍存在
    /// </summary>
    private void ResetModelScale(SceneObject sceneObject, ComponentRecord modelComponent)
    {
        if (!TryReadScaleOriginal(sceneObject, out var original, out var origin))
        {
            SetStatus("找不到该模型的原始备份，无法恢复原始尺寸");
            return;
        }

        if (!ReplaceObjectVoxels(sceneObject, original, origin, scale: 1F))
        {
            SetStatus("恢复结果与其他对象重叠，未应用");
            return;
        }

        var data = SceneComponentTypes.ReadVoxelModel(modelComponent);
        SceneComponentTypes.WriteVoxelModel(modelComponent, data with { VoxelCount = original.OccupiedVoxelCount, Scale = 1F });
        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus($"已恢复原始模型：{original.OccupiedVoxelCount:N0} 体素");
    }

    /// <summary>
    /// 用缩放结果替换对象当前体素；主模型对象保留无归属体素语义，分层对象更新所有权集合
    /// </summary>
    private bool ReplaceObjectVoxels(SceneObject sceneObject, VoxelWorld replacement, VoxelCoord64 origin,
        OrthogonalRotation? newRotation = null, float? scale = null)
    {
        if (_sceneDocument is null) return false;
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        var before = sceneObject.Transform.Grid;
        var after = new GridTransform(origin, newRotation ?? before.Rotation);
        var changes = world.EnumerateOccupied().ToDictionary(cell => cell.Coordinate, _ => default(VoxelValue));
        foreach (var cell in replacement.EnumerateOccupied()) changes[cell.Coordinate] = cell.Value;
        _voxelHistory.Execute(new VoxelStrokeCommand("变换模型", world,
            changes.Select(cell => (cell.Key, cell.Value)), ObjectEditCallback(sceneObject.Id, forward =>
            {
                sceneObject.Transform.SetGrid(forward ? after : before);
                UpdateTransformMarker(sceneObject);
                if (forward && scale is { } factor)
                {
                    var component = sceneObject.Components.First(SceneComponentTypes.IsVoxelModel);
                    SceneComponentTypes.WriteVoxelModel(component,
                        SceneComponentTypes.ReadVoxelModel(component) with { Scale = factor });
                }
            })));
        return true;
    }

    private void ResetAfterVoxelTransform()
    {
        _selectedVoxel = null;
        _composedRenderWorld = null;
        _composedSignature = 0;
        _composedMask = uint.MaxValue;
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _renderViewNeedsInitialRender = true;
        _pickVersion++;
    }

    private readonly CancellationTokenSource _scaleCancellation = new();
}
