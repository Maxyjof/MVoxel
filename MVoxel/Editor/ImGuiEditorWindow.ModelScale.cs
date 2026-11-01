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
    private sealed record ScaleOriginalMeta(float OriginX, float OriginY, float OriginZ, int[]? Rotation = null);

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
            var meta = new ScaleOriginalMeta((float)minimum.X, (float)minimum.Y, (float)minimum.Z,
                RotationValues(sceneObject.Transform.Grid.Rotation));
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
    /// 在非 1x 尺寸上继续编辑前，把当前形态写成新的缩放原件并把缩放归 1。
    /// 这样编辑过的内容成为新基准，不会在下次缩放时被旧原件覆盖掉。
    /// </summary>
    private void BakeScaleForEditing(SceneObject sceneObject)
    {
        if (_project is null) return;
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        if (Math.Abs(data.Scale - 1F) < 0.0001F) return;
        var current = CaptureObjectVoxels(sceneObject);
        if (current.OccupiedVoxelCount == 0) return;
        if (!TryWriteScaleOriginal(sceneObject, current)) return;
        SceneComponentTypes.WriteVoxelModel(component, data with { Scale = 1F });
        var transform = sceneObject.Transform;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
        transform.ScaleZ = 1;
        sceneObject.Transform = transform;
        SetStatus($"已在 {data.Scale:0.###}x 尺寸上继续编辑，当前尺寸成为新的 1x 基准");
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

    /// <summary>本次缩放复用的 1x 基准模型所属对象，避免逐字缩放时反复读原件</summary>
    private StableId? _scaleBaseObjectId;
    /// <summary>本次缩放复用的 1x 基准模型，坐标已归一到原点</summary>
    private VoxelWorld? _scaleBaseWorld;
    /// <summary>基准模型记录时对象的朝向，重采样前先转到当前朝向</summary>
    private OrthogonalRotation _scaleBaseRotation = OrthogonalRotation.Identity;

    /// <summary>
    /// 按轴缩放模型。所有缩放都从 1x 基准模型重采样，因此反复缩放和逐字输入都不会累积精度损失
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="x">X 轴目标倍数</param>
    /// <param name="y">Y 轴目标倍数</param>
    /// <param name="z">Z 轴目标倍数</param>
    private void ApplyAxisScale(SceneObject sceneObject, int x, int y, int z)
    {
        if (x < 1 || y < 1 || z < 1) { SetStatus("缩放倍数必须为正整数"); return; }
        var transform = sceneObject.Transform;
        var oldX = Math.Max(1, transform.ScaleX); var oldY = Math.Max(1, transform.ScaleY); var oldZ = Math.Max(1, transform.ScaleZ);
        if (x == oldX && y == oldY && z == oldZ) return;
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        if (!world.TryGetBounds(out var origin, out _)) { SetStatus("模型为空，无法缩放"); return; }
        try
        {
            var source = ResolveScaleBase(sceneObject, oldX, oldY, oldZ, world, origin);
            if (source is null) return;
            var scaled = VoxelRescaler.Scale(source, x, y, z, origin, out var newOrigin,
                _scaleCancellation.Token);
            var changes = world.EnumerateOccupied().ToDictionary(cell => cell.Coordinate, _ => default(VoxelValue));
            foreach (var cell in scaled.EnumerateOccupied()) changes[cell.Coordinate] = cell.Value;
            var before = transform.Grid;
            var after = new GridTransform(newOrigin, before.Rotation);
            _voxelHistory.Execute(new VoxelStrokeCommand("缩放模型", world,
                changes.Select(cell => (cell.Key, cell.Value)), ObjectEditCallback(sceneObject.Id, forward =>
                {
                    var target = sceneObject.Transform;
                    target.SetGrid(forward ? after : before);
                    target.ScaleX = forward ? x : oldX; target.ScaleY = forward ? y : oldY; target.ScaleZ = forward ? z : oldZ;
                    UpdateTransformMarker(sceneObject);
                })));
            MarkSceneDocumentDirty(); ResetAfterVoxelTransform();
            SetStatus($"已按原模型缩放为 {x}×{y}×{z}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException or IOException)
        { SetStatus($"缩放失败：{exception.Message}"); }
    }

    /// <summary>
    /// 取得对象 1x 状态的基准模型，并按当前包围盒位置平移
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="oldX">当前 X 轴倍数</param>
    /// <param name="oldY">当前 Y 轴倍数</param>
    /// <param name="oldZ">当前 Z 轴倍数</param>
    /// <param name="world">对象当前体素</param>
    /// <param name="origin">对象当前包围盒最小角</param>
    /// <returns>可直接重采样的 1x 基准；无法建立时为空</returns>
    private VoxelWorld? ResolveScaleBase(SceneObject sceneObject, int oldX, int oldY, int oldZ,
        VoxelWorld world, VoxelCoord64 origin)
    {
        if (oldX == 1 && oldY == 1 && oldZ == 1)
        {
            //当前就在 1x：先把当前数据固化到原件，1x 上新增的方块才能进入后续缩放
            var current = world.Clone();
            TryWriteScaleOriginal(sceneObject, current);
            _scaleBaseObjectId = sceneObject.Id;
            _scaleBaseRotation = sceneObject.Transform.Grid.Rotation;
            _scaleBaseWorld = ToLocalWorld(current);
            return current;
        }

        var currentRotation = sceneObject.Transform.Grid.Rotation;
        if (_scaleBaseObjectId == sceneObject.Id && _scaleBaseWorld is { } cached)
            return TranslateWorld(OrientScaleBase(cached, _scaleBaseRotation, currentRotation), origin);

        if (TryReadScaleOriginalLocal(sceneObject, out var stored, out var storedRotation) &&
            stored.OccupiedVoxelCount > 0)
        {
            _scaleBaseObjectId = sceneObject.Id;
            _scaleBaseRotation = storedRotation;
            _scaleBaseWorld = stored;
            return TranslateWorld(OrientScaleBase(stored, storedRotation, currentRotation), origin);
        }

        //旧项目没有 1x 原件：先把当前尺寸反算回 1x 作为基准，之后的缩放都基于这份数据
        var approximate = VoxelRescaler.Scale(world, 1F / oldX, 1F / oldY, 1F / oldZ, origin,
            out _, _scaleCancellation.Token);
        if (approximate.OccupiedVoxelCount == 0)
        {
            SetStatus("重建 1x 基准模型失败，缩放已取消");
            return null;
        }

        TryWriteScaleOriginal(sceneObject, approximate);
        _scaleBaseObjectId = sceneObject.Id;
        _scaleBaseRotation = sceneObject.Transform.Grid.Rotation;
        _scaleBaseWorld = ToLocalWorld(approximate);
        return approximate;
    }

    /// <summary>
    /// 把 1x 基准从记录时的朝向转到对象当前朝向，保证先转后缩的和当前模型一致
    /// </summary>
    /// <param name="local">局部坐标基准</param>
    /// <param name="captured">记录时的朝向</param>
    /// <param name="current">对象当前朝向</param>
    /// <returns>当前朝向的局部坐标基准</returns>
    private static VoxelWorld OrientScaleBase(VoxelWorld local, OrthogonalRotation captured, OrthogonalRotation current)
    {
        if (captured.Equals(current)) return local;
        var delta = current.Compose(captured.Inverse());
        var rotated = new VoxelWorld();
        foreach (var cell in local.EnumerateOccupied()) rotated.Set(delta.Apply(cell.Coordinate), cell.Value);
        return ToLocalWorld(rotated);
    }

    /// <summary>
    /// 把旋转写成九个整数，供原件元数据持久化
    /// </summary>
    /// <param name="rotation">离散旋转</param>
    /// <returns>行优先的九个元素</returns>
    private static int[] RotationValues(OrthogonalRotation rotation) =>
    [
        rotation.M00, rotation.M01, rotation.M02, rotation.M10, rotation.M11, rotation.M12, rotation.M20, rotation.M21, rotation.M22,
    ];

    /// <summary>
    /// 从原件元数据还原旋转，缺失或非法时按单位旋转处理
    /// </summary>
    /// <param name="values">行优先的九个元素</param>
    /// <returns>离散旋转</returns>
    private static OrthogonalRotation ParseRotation(int[]? values)
    {
        if (values is not { Length: 9 }) return OrthogonalRotation.Identity;
        var rotation = new OrthogonalRotation(
            (sbyte)values[0], (sbyte)values[1], (sbyte)values[2],
            (sbyte)values[3], (sbyte)values[4], (sbyte)values[5],
            (sbyte)values[6], (sbyte)values[7], (sbyte)values[8]);
        return rotation.IsValid ? rotation : OrthogonalRotation.Identity;
    }

    /// <summary>
    /// 丢弃缓存的 1x 基准，模型数据被整体替换后调用
    /// </summary>
    /// <param name="sceneObjectId">目标对象</param>
    private void InvalidateScaleBase(StableId sceneObjectId)
    {
        if (_scaleBaseObjectId != sceneObjectId) return;
        _scaleBaseObjectId = null;
        _scaleBaseWorld = null;
        _scaleBaseRotation = OrthogonalRotation.Identity;
    }

    /// <summary>
    /// 读取原件文件的局部坐标数据，包围盒最小角归零
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="local">读取结果</param>
    /// <param name="rotation">记录时的对象朝向</param>
    /// <returns>文件存在且可读时为 true</returns>
    private bool TryReadScaleOriginalLocal(SceneObject sceneObject, out VoxelWorld local, out OrthogonalRotation rotation)
    {
        local = new VoxelWorld();
        rotation = OrthogonalRotation.Identity;
        try
        {
            var path = ScaleOriginalModelPath(sceneObject);
            if (!File.Exists(path)) return false;
            local = LargeWorldFormat.Read(path, VoxelCoord64.Zero);
            var metaPath = ScaleOriginalMetaPath(sceneObject);
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize<ScaleOriginalMeta>(File.ReadAllText(metaPath), ScaleMetaJson);
                rotation = ParseRotation(meta?.Rotation);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or JsonException)
        {
            MVoxelLog.Error($"读取缩放原件失败：{sceneObject.Name}", exception);
            SetStatus($"读取缩放原件失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把世界平移成包围盒最小角为零的局部坐标
    /// </summary>
    /// <param name="world">任意位置的世界</param>
    /// <returns>局部坐标副本</returns>
    private static VoxelWorld ToLocalWorld(VoxelWorld world)
    {
        if (!world.TryGetBounds(out var minimum, out _)) return new VoxelWorld();
        var local = new VoxelWorld();
        foreach (var cell in world.EnumerateOccupied()) local.Set(cell.Coordinate - minimum, cell.Value);
        return local;
    }

    /// <summary>
    /// 把局部坐标基准平移到对象的包围盒位置
    /// </summary>
    /// <param name="world">局部坐标世界</param>
    /// <param name="origin">目标最小角</param>
    /// <returns>平移后的世界，原点无需平移时直接复用传入世界</returns>
    private static VoxelWorld TranslateWorld(VoxelWorld world, VoxelCoord64 origin)
    {
        if (origin == VoxelCoord64.Zero) return world;
        var translated = new VoxelWorld();
        foreach (var cell in world.EnumerateOccupied()) translated.Set(cell.Coordinate + origin, cell.Value);
        return translated;
    }

    private readonly CancellationTokenSource _scaleCancellation = new();
}
