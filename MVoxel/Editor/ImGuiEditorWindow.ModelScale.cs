using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 体素模型对象的倍数缩放。缩放基准是“最近一次编辑后的模型”：
/// 只改倍数、不再雕刻时基准保持不变，所以在 1x / 2x / 0.5x 之间来回切换都不会累积精度损失；
/// 在非 1x 尺寸上继续雕刻时，当前形态会升级为新的基准，倍数回到 1x。
/// 放大只接受整数倍，缩小接受小数倍，结果按体素格对齐。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private sealed record ScaleBaseMeta(int[]? Rotation = null);

    private static readonly JsonSerializerOptions ScaleMetaJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>基准快照目录；沿用历史目录名，老项目里的快照继续可用</summary>
    private string ScaleBaseDirectory => Path.Combine(_project!.RootPath, "ProjectSettings", "ModelOriginals");

    private string ScaleBaseModelPath(SceneObject sceneObject) =>
        Path.Combine(ScaleBaseDirectory, sceneObject.Id.ToString() + ".mvmodel");

    private string ScaleBaseMetaPath(SceneObject sceneObject) =>
        Path.Combine(ScaleBaseDirectory, sceneObject.Id.ToString() + ".json");

    /// <summary>对象是否处在基准尺寸（三轴都是 1）</summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>三轴都等于 1 时为 true</returns>
    private static bool IsAtBaseScale(SceneObject sceneObject)
    {
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return true;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        return ModelScaleRule.IsIdentity(data.ScaleX) && ModelScaleRule.IsIdentity(data.ScaleY) &&
               ModelScaleRule.IsIdentity(data.ScaleZ);
    }

    /// <summary>
    /// 读取基准快照：内容按包围盒最小角为零保存，另存缩放时的对象朝向
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="local">局部坐标基准</param>
    /// <param name="rotation">记录时的朝向</param>
    /// <returns>快照存在且可读时为 true</returns>
    private bool TryReadScaleBaseLocal(SceneObject sceneObject, out VoxelWorld local, out OrthogonalRotation rotation)
    {
        local = new VoxelWorld();
        rotation = OrthogonalRotation.Identity;
        try
        {
            var path = ScaleBaseModelPath(sceneObject);
            if (!File.Exists(path)) return false;
            local = LargeWorldFormat.Read(path, VoxelCoord64.Zero);
            var metaPath = ScaleBaseMetaPath(sceneObject);
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize<ScaleBaseMeta>(File.ReadAllText(metaPath), ScaleMetaJson);
                rotation = ParseRotation(meta?.Rotation);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or JsonException)
        {
            MVoxelLog.Error($"读取缩放基准失败：{sceneObject.Name}", exception);
            SetStatus($"读取缩放基准失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把模型保存为缩放基准。基准只记录编辑结果，不参与渲染，也不改动资产文件
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="snapshot">当前模型</param>
    /// <returns>写入成功时为 true</returns>
    private bool TryWriteScaleBase(SceneObject sceneObject, VoxelWorld snapshot)
    {
        try
        {
            if (!snapshot.TryGetBounds(out _, out _))
            {
                SetStatus("模型为空，无法建立缩放基准");
                return false;
            }

            Directory.CreateDirectory(ScaleBaseDirectory);
            LargeWorldFormat.Write(ScaleBaseModelPath(sceneObject), ModelScaleRule.ToLocal(snapshot));
            var meta = new ScaleBaseMeta(RotationValues(sceneObject.Transform.Grid.Rotation));
            AtomicFile.WriteText(ScaleBaseMetaPath(sceneObject), JsonSerializer.Serialize(meta, ScaleMetaJson));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            MVoxelLog.Error($"写入缩放基准失败：{sceneObject.Name}", exception);
            SetStatus($"写入缩放基准失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 收集对象当前在世界中的体素；主模型对象取不属于任何分层对象的体素
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>当前模型的副本</returns>
    private VoxelWorld CaptureObjectVoxels(SceneObject sceneObject) =>
        _objectVoxels.GetOrCreate(sceneObject.Id).Clone();

    /// <summary>
    /// 应用模型缩放。基准永远是最近一次编辑后的模型，只改倍数不会掉精度；
    /// 每个轴放大只接受整数倍，缩小接受小数倍，结果对齐回对象原来的最小角。
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="scaleX">X 轴目标倍数</param>
    /// <param name="scaleY">Y 轴目标倍数</param>
    /// <param name="scaleZ">Z 轴目标倍数</param>
    private void ApplyModelScale(SceneObject sceneObject, float scaleX, float scaleY, float scaleZ)
    {
        if (_project is null || _sceneDocument is null) return;
        if (sceneObject.Transform.Mode != TransformMode.Grid)
        {
            SetStatus("只有体素模型对象支持倍数缩放");
            return;
        }

        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null)
        {
            SetStatus("对象没有体素模型组件");
            return;
        }

        var notes = new List<string>();
        if (!TryNormalizeAxis(scaleX, out var factorX, notes, "X") ||
            !TryNormalizeAxis(scaleY, out var factorY, notes, "Y") ||
            !TryNormalizeAxis(scaleZ, out var factorZ, notes, "Z"))
        {
            SetStatus(notes.Count > 0 ? notes[0] : "缩放倍数无效");
            return;
        }

        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        if (!world.TryGetBounds(out var origin, out _))
        {
            SetStatus("模型为空，无法缩放");
            return;
        }

        if (ModelScaleRule.IsIdentity(factorX) && ModelScaleRule.IsIdentity(factorY) && ModelScaleRule.IsIdentity(factorZ))
        {
            RestoreModelScale(sceneObject, component);
            return;
        }

        var source = ResolveScaleBase(sceneObject, world, origin);
        if (source is null) return;

        if (!ModelScaleRule.TryResample(source, factorX, factorY, factorZ, origin, out var scaled, out var error,
                _scaleCancellation.Token))
        {
            SetStatus($"缩放失败：{error}");
            return;
        }

        // 已有浮点旋转时先缩放再按同一角度旋转，保证先转后缩与先缩后转得到同一形态
        var modelRotation = ReadModelRotation(sceneObject);
        if (MathF.Abs(modelRotation.X) > .001F || MathF.Abs(modelRotation.Y) > .001F ||
            MathF.Abs(modelRotation.Z) > .001F)
        {
            if (!ModelRotationRule.TryResample(scaled, RotationFromDegrees(modelRotation), origin, out var oriented,
                    out var rotateError))
            {
                SetStatus($"缩放失败：{rotateError}");
                return;
            }

            scaled = oriented;
        }

        if (!ReplaceObjectVoxels(sceneObject, scaled, origin,
                scale: (factorX, factorY, factorZ)))
        {
            SetStatus("缩放结果无法应用到对象");
            return;
        }

        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        var factorText = ModelScaleRule.IsUniform(factorX, factorY, factorZ)
            ? $"{factorX:0.###}x"
            : $"{factorX:0.###}×{factorY:0.###}×{factorZ:0.###}";
        var note = notes.Count > 0 ? string.Join("；", notes) + "，" : string.Empty;
        SetStatus($"{note}已缩放为 {factorText}：{scaled.OccupiedVoxelCount:N0} 体素");
    }

    /// <summary>
    /// 规范化单轴倍数：放大取整数倍，缩小保留小数；失败时把原因写进说明列表
    /// </summary>
    /// <param name="requested">用户填写的倍数</param>
    /// <param name="factor">实际使用的倍数</param>
    /// <param name="notes">倍数被调整或不可用的说明</param>
    /// <param name="axis">轴名，用于说明文案</param>
    /// <returns>该轴倍数可用时为 true</returns>
    private static bool TryNormalizeAxis(float requested, out float factor, List<string> notes, string axis)
    {
        if (ModelScaleRule.TryNormalize(requested, out factor, out var note))
        {
            if (note.Length > 0) notes.Add($"{axis} 轴{note}");
            return true;
        }

        notes.Add($"{axis} 轴：{note}");
        factor = 1F;
        return false;
    }

    /// <summary>
    /// 恢复基准尺寸：把最近一次编辑后的模型原样放回对象当前位置，不做任何重采样
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">模型组件</param>
    /// <param name="note">附带说明，可为空</param>
    private void RestoreModelScale(SceneObject sceneObject, ComponentRecord component)
    {
        if (!TryReadScaleBaseLocal(sceneObject, out var local, out var recordedRotation))
        {
            // 还没缩放过的模型本来就在基准尺寸，不需要提示
            if (IsAtBaseScale(sceneObject)) return;
            SetStatus("找不到缩放基准，无法恢复到 1x");
            return;
        }

        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        var origin = world.TryGetBounds(out var minimum, out _) ? minimum : VoxelCoord64.Zero;
        var restored = ModelScaleRule.Align(
            OrientScaleBase(local, recordedRotation, sceneObject.Transform.Grid.Rotation), origin);
        if (!ReplaceObjectVoxels(sceneObject, restored, origin, scale: (1F, 1F, 1F)))
        {
            SetStatus("恢复结果无法应用到对象");
            return;
        }

        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus($"已恢复基准尺寸：{restored.OccupiedVoxelCount:N0} 体素");
    }

    /// <summary>
    /// 在非基准尺寸上继续编辑前，把当前形态写成新的基准并把倍数归 1，
    /// 这样新画的方块会成为后续缩放的参考，不会被旧基准覆盖掉
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private void BakeScaleForEditing(SceneObject sceneObject)
    {
        if (_project is null) return;
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        if (IsAtBaseScale(sceneObject)) return;
        var current = CaptureObjectVoxels(sceneObject);
        if (current.OccupiedVoxelCount == 0) return;
        if (!TryWriteScaleBase(sceneObject, current)) return;
        SceneComponentTypes.WriteVoxelModel(component, data with
        {
            ScaleX = 1F, ScaleY = 1F, ScaleZ = 1F,
            RotationX = 0F, RotationY = 0F, RotationZ = 0F,
        });
        _scaleBaseObjectId = sceneObject.Id;
        _scaleBaseRotation = sceneObject.Transform.Grid.Rotation;
        _scaleBaseWorld = ModelScaleRule.ToLocal(current);
        var size = ModelScaleRule.IsUniform(data.ScaleX, data.ScaleY, data.ScaleZ)
            ? $"{data.ScaleX:0.###}x"
            : $"{data.ScaleX:0.###}×{data.ScaleY:0.###}×{data.ScaleZ:0.###}";
        SetStatus($"已在 {size} 尺寸上继续编辑，当前形态成为新的基准");
    }

    /// <summary>
    /// 取得缩放基准：对象在基准尺寸时基准就是它自己（最近一次编辑的结果），
    /// 非基准尺寸时用缓存或磁盘快照；都没有时把当前尺寸反算回 1x 补一份
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="current">对象当前体素</param>
    /// <param name="origin">对象当前包围盒最小角</param>
    /// <returns>摆在当前位置、可直接重采样的基准；无法建立时为空</returns>
    private VoxelWorld? ResolveScaleBase(SceneObject sceneObject, VoxelWorld current, VoxelCoord64 origin)
    {
        var rotation = sceneObject.Transform.Grid.Rotation;
        var modelRotation = ReadModelRotation(sceneObject);
        var hasModelRotation = MathF.Abs(modelRotation.X) > .001F || MathF.Abs(modelRotation.Y) > .001F ||
                               MathF.Abs(modelRotation.Z) > .001F;
        if (IsAtBaseScale(sceneObject) && !hasModelRotation)
        {
            // 基准尺寸且没有浮点旋转时，模型数据就是基准；已有旋转或缓存时不能覆盖，
            // 否则当前这份“已经旋转过的体素”会被当成基准，导致反复旋转层层糊掉
            if (_scaleBaseObjectId == sceneObject.Id && _scaleBaseWorld is { } cachedBase)
                return ModelScaleRule.Align(OrientScaleBase(cachedBase, _scaleBaseRotation, rotation), origin);
            if (!TryReadScaleBaseLocal(sceneObject, out var storedBase, out var storedBaseRotation) ||
                storedBase.OccupiedVoxelCount == 0)
            {
                if (!TryWriteScaleBase(sceneObject, current)) return null;
                storedBase = current;
                storedBaseRotation = rotation;
            }

            _scaleBaseObjectId = sceneObject.Id;
            _scaleBaseRotation = storedBaseRotation;
            _scaleBaseWorld = ModelScaleRule.ToLocal(storedBase);
            return ModelScaleRule.Align(OrientScaleBase(storedBase, storedBaseRotation, rotation), origin);
        }

        if (_scaleBaseObjectId == sceneObject.Id && _scaleBaseWorld is { } cached)
            return ModelScaleRule.Align(OrientScaleBase(cached, _scaleBaseRotation, rotation), origin);

        if (TryReadScaleBaseLocal(sceneObject, out var stored, out var storedRotation) && stored.OccupiedVoxelCount > 0)
        {
            _scaleBaseObjectId = sceneObject.Id;
            _scaleBaseRotation = storedRotation;
            _scaleBaseWorld = stored;
            return ModelScaleRule.Align(OrientScaleBase(stored, storedRotation, rotation), origin);
        }

        // 老项目没有基准快照：把当前尺寸反算回 1x 作为基准，之后的缩放都基于它
        var currentScale = SceneComponentTypes.ReadVoxelModel(
            sceneObject.Components.First(SceneComponentTypes.IsVoxelModel));
        var approximate = VoxelRescaler.Scale(current,
            1F / MathF.Max(ModelScaleRule.Minimum, currentScale.ScaleX),
            1F / MathF.Max(ModelScaleRule.Minimum, currentScale.ScaleY),
            1F / MathF.Max(ModelScaleRule.Minimum, currentScale.ScaleZ),
            origin, out _, _scaleCancellation.Token);
        if (approximate.OccupiedVoxelCount == 0)
        {
            SetStatus("无法还原缩放基准，缩放已取消");
            return null;
        }

        if (!TryWriteScaleBase(sceneObject, approximate)) return null;
        _scaleBaseObjectId = sceneObject.Id;
        _scaleBaseRotation = rotation;
        _scaleBaseWorld = ModelScaleRule.ToLocal(approximate);
        return approximate;
    }

    /// <summary>
    /// 把基准从记录时的朝向转到对象当前朝向，保证先旋转再缩放的结果和当前模型一致
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
        return ModelScaleRule.ToLocal(rotated);
    }

    /// <summary>
    /// 用缩放结果替换对象当前体素；主模型对象保留无归属体素语义，分层对象更新所有权集合
    /// </summary>
    private bool ReplaceObjectVoxels(SceneObject sceneObject, VoxelWorld replacement, VoxelCoord64 origin,
        OrthogonalRotation? newRotation = null, (float X, float Y, float Z)? scale = null)
    {
        if (_sceneDocument is null) return false;
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        var before = sceneObject.Transform.Grid;
        var after = new GridTransform(origin, newRotation ?? before.Rotation);
        var component = sceneObject.Components.First(SceneComponentTypes.IsVoxelModel);
        var previousScale = SceneComponentTypes.ReadVoxelModel(component);
        var changes = world.EnumerateOccupied().ToDictionary(cell => cell.Coordinate, _ => default(VoxelValue));
        foreach (var cell in replacement.EnumerateOccupied()) changes[cell.Coordinate] = cell.Value;
        _voxelHistory.Execute(new VoxelStrokeCommand("缩放模型", world,
            changes.Select(cell => (cell.Key, cell.Value)), ObjectEditCallback(sceneObject.Id, forward =>
            {
                sceneObject.Transform.SetGrid(forward ? after : before);
                UpdateTransformMarker(sceneObject);
                if (scale is { } factor)
                    SceneComponentTypes.WriteVoxelModel(component, SceneComponentTypes.ReadVoxelModel(component) with
                    {
                        ScaleX = forward ? factor.X : previousScale.ScaleX,
                        ScaleY = forward ? factor.Y : previousScale.ScaleY,
                        ScaleZ = forward ? factor.Z : previousScale.ScaleZ,
                    });
            })));
        return true;
    }

    private void ResetAfterVoxelTransform()
    {
        _selectedVoxel = null;
        InvalidateComposedWorldCache();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _renderViewNeedsInitialRender = true;
        _pickVersion++;
    }

    /// <summary>本次缩放复用的基准模型所属对象，避免反复读快照文件</summary>
    private StableId? _scaleBaseObjectId;
    /// <summary>本次缩放复用的基准模型，坐标已归一到原点</summary>
    private VoxelWorld? _scaleBaseWorld;
    /// <summary>基准模型记录时对象的朝向，重采样前先转到当前朝向</summary>
    private OrthogonalRotation _scaleBaseRotation = OrthogonalRotation.Identity;

    /// <summary>
    /// 把旋转写成九个整数，供基准元数据持久化
    /// </summary>
    /// <param name="rotation">离散旋转</param>
    /// <returns>行优先的九个元素</returns>
    private static int[] RotationValues(OrthogonalRotation rotation) =>
    [
        rotation.M00, rotation.M01, rotation.M02, rotation.M10, rotation.M11, rotation.M12, rotation.M20, rotation.M21, rotation.M22,
    ];

    /// <summary>
    /// 从基准元数据还原旋转，缺失或非法时按单位旋转处理
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
    /// 丢弃缓存的基准，模型数据被整体替换后调用
    /// </summary>
    /// <param name="sceneObjectId">目标对象</param>
    private void InvalidateScaleBase(StableId sceneObjectId)
    {
        if (_scaleBaseObjectId != sceneObjectId) return;
        _scaleBaseObjectId = null;
        _scaleBaseWorld = null;
        _scaleBaseRotation = OrthogonalRotation.Identity;
    }

    private readonly CancellationTokenSource _scaleCancellation = new();
}
