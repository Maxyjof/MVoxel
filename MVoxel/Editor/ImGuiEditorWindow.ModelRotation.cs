using ImGuiNET;
using System.Numerics;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 体素模型的浮点旋转。和倍数缩放同源：基准是最近一次编辑后的模型，
/// 旋转只改参数并重新采样基准，反复调整不会累积失真；进入绘制模式时当前形态才成为新基准。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 读取模型组件上的浮点旋转角（度）
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>三轴角度，按 X/Y/Z 顺序</returns>
    private static Vector3 ReadModelRotation(SceneObject sceneObject)
    {
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return Vector3.Zero;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        return new Vector3(data.RotationX, data.RotationY, data.RotationZ);
    }

    /// <summary>
    /// 把三轴角度合成四元数
    /// </summary>
    /// <param name="degrees">欧拉角，单位度</param>
    /// <returns>四元数</returns>
    private static Quaternion RotationFromDegrees(Vector3 degrees) =>
        Quaternion.CreateFromYawPitchRoll(degrees.Y * MathF.PI / 180F, degrees.X * MathF.PI / 180F,
            degrees.Z * MathF.PI / 180F);

    /// <summary>
    /// 只在指定轴上叠加角度，其他两轴保持起始值
    /// </summary>
    /// <param name="startDegrees">起始三轴角度</param>
    /// <param name="axis">叠加轴</param>
    /// <param name="radians">叠加角度</param>
    /// <returns>新的三轴角度</returns>
    private static Vector3 AddAxisRotation(Vector3 startDegrees, int axis, float radians)
    {
        const float toDegrees = 180F / MathF.PI;
        var degrees = startDegrees;
        switch (axis)
        {
            case 0:
                degrees.X = NormalizeDegrees(degrees.X + radians * toDegrees);
                break;
            case 1:
                degrees.Y = NormalizeDegrees(degrees.Y + radians * toDegrees);
                break;
            default:
                degrees.Z = NormalizeDegrees(degrees.Z + radians * toDegrees);
                break;
        }

        return degrees;
    }

    /// <summary>把角度折回 -180 到 180，避免连续拖动后数值无限增大</summary>
    /// <param name="degrees">原始角度</param>
    /// <returns>折回后的角度</returns>
    private static float NormalizeDegrees(float degrees)
    {
        var wrapped = (degrees + 180F) % 360F;
        if (wrapped < 0F) wrapped += 360F;
        return wrapped - 180F;
    }

    /// <summary>
    /// 按目标四元数重新生成模型体素，参数写回组件
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">模型组件</param>
    /// <param name="rotation">目标旋转</param>
    /// <param name="degrees">写回组件的三轴角度；为空时由四元数换算</param>
    /// <param name="pivot">绕世界枢轴旋转；为空时绕模型自身包围盒中心</param>
    private void ApplyModelRotation(SceneObject sceneObject, ComponentRecord component, Quaternion rotation,
        Vector3? degrees = null, Vector3? pivot = null)
    {
        if (_project is null || _sceneDocument is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        if (!world.TryGetBounds(out var origin, out _))
        {
            SetStatus("模型为空，无法旋转");
            return;
        }

        var baseWorld = PrepareRotationSource(sceneObject, component, origin, world);
        if (baseWorld is null) return;

        // 绕枢轴旋转时，先把结果从“绕模型中心”平移到“绕枢轴”，
        // 并按旋转后包围盒的最小角重新锚定，保证拖动圆环的支点在旋转前后保持不动
        var anchor = pivot is { } pivotWorld
            ? ResolveRotationAnchor(baseWorld, origin, rotation, pivotWorld)
            : origin;

        if (!ModelRotationRule.TryResample(baseWorld, rotation, anchor, out var rotated, out var error))
        {
            SetStatus($"旋转失败：{error}");
            return;
        }

        // 旧场景的全零离散旋转顺手修正为单位旋转，避免继续沿用非法矩阵
        var gridRotation = sceneObject.Transform.Grid.Rotation;
        if (!gridRotation.IsValid) gridRotation = OrthogonalRotation.Identity;
        var euler = degrees ?? ToEulerDegrees(rotation);
        // 旋转只改变体素形状，对象位置保持不动：格变换位置是支点与后续操作的锚点，
        // 跟着旋转后的包围盒最小角走会让坐标在每次旋转后漂移
        var position = sceneObject.Transform.Grid.Position;
        var modelAfter = data with
        {
            Model = string.Empty,
            SourceSegment = string.Empty,
            SourceOrigin = null,
            VoxelCount = rotated.OccupiedVoxelCount,
            RotationX = euler.X,
            RotationY = euler.Y,
            RotationZ = euler.Z,
        };
        if (!ReplaceObjectVoxels(sceneObject, rotated, position, gridRotation,
                scale: (data.ScaleX, data.ScaleY, data.ScaleZ), modelAfter: modelAfter))
        {
            SetStatus("旋转结果无法应用到对象");
            return;
        }

        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus($"已按浮点角度旋转：{rotated.OccupiedVoxelCount:N0} 体素");
    }

    /// <summary>
    /// 取得重采样用的旋转基准：先按当前倍数还原尺寸，再对齐到对象当前朝向
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">模型组件</param>
    /// <param name="origin">结果要对齐到的原点</param>
    /// <param name="current">对象当前体素</param>
    /// <returns>可直接重采样的基准；无法建立时为空</returns>
    private VoxelWorld? PrepareRotationSource(SceneObject sceneObject, ComponentRecord component,
        VoxelCoord64 origin, VoxelWorld current)
    {
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var source = ResolveScaleBase(sceneObject, current, origin);
        if (source is null) return null;
        if (ModelScaleRule.IsIdentity(data.ScaleX) && ModelScaleRule.IsIdentity(data.ScaleY) &&
            ModelScaleRule.IsIdentity(data.ScaleZ))
            return source;
        if (!ModelScaleRule.TryResample(source, data.ScaleX, data.ScaleY, data.ScaleZ, origin, out var scaled,
                out var scaleError, _scaleCancellation.Token))
        {
            SetStatus($"旋转失败：{scaleError}");
            return null;
        }

        return scaled;
    }

    /// <summary>
    /// 计算绕枢轴旋转后包围盒应放置的整数锚点；预览与提交共用同一算法
    /// </summary>
    /// <param name="baseWorld">未旋转的基准模型</param>
    /// <param name="origin">基准模型当前包围盒最小角</param>
    /// <param name="rotation">旋转四元数</param>
    /// <param name="pivot">枢轴世界坐标</param>
    /// <returns>结果的对齐锚点</returns>
    private static VoxelCoord64 ResolveRotationAnchor(VoxelWorld baseWorld, VoxelCoord64 origin,
        Quaternion rotation, Vector3 pivot)
    {
        if (!baseWorld.TryGetBounds(out var minimum, out var maximum)) return origin;
        var center = new Vector3(
            (minimum.X + maximum.X + 1) * .5F, (minimum.Y + maximum.Y + 1) * .5F,
            (minimum.Z + maximum.Z + 1) * .5F);
        var offset = pivot - center;
        var rotatedOffset = Vector3.Transform(offset, rotation);
        var shift = offset - rotatedOffset;
        // 与 ModelRotationRule 相同：用八个角点估算旋转后的包围盒
        var minimumCorner = new Vector3(minimum.X, minimum.Y, minimum.Z);
        var maximumCorner = new Vector3(maximum.X + 1, maximum.Y + 1, maximum.Z + 1);
        var rotatedMinimum = new Vector3(float.MaxValue);
        for (var index = 0; index < 8; index++)
        {
            var corner = new Vector3(
                (index & 1) == 0 ? minimumCorner.X : maximumCorner.X,
                (index & 2) == 0 ? minimumCorner.Y : maximumCorner.Y,
                (index & 4) == 0 ? minimumCorner.Z : maximumCorner.Z);
            var rotatedCorner = Vector3.Transform(corner - center, rotation) + center;
            rotatedMinimum = Vector3.Min(rotatedMinimum, rotatedCorner);
        }

        return new VoxelCoord64(
            (long)MathF.Round(rotatedMinimum.X + shift.X),
            (long)MathF.Round(rotatedMinimum.Y + shift.Y),
            (long)MathF.Round(rotatedMinimum.Z + shift.Z));
    }

    /// <summary>
    /// Gizmo 旋转拖动的预览状态：基准模型、起始/上一次重采样的角度与枢轴
    /// </summary>
    private VoxelWorld? _gizmoRotationSource;
    private IReadOnlyDictionary<(long X, long Y, long Z), VoxelValue>? _gizmoRotationLookup;
    private VoxelCoord64 _gizmoRotationOrigin;
    private Vector3 _gizmoRotationStartDegrees;
    private Vector3 _gizmoRotationPreviewQuantizedDegrees;
    private Vector3 _gizmoRotationPreviewPivot;
    private long _gizmoRotationPreviewAtMs;
    private bool _gizmoRotationPreviewActive;
    // 预览把角度量化到固定档位后才重采样体素，档内剩余角度交给拖动实例的旋转变换补足。
    // 这样体素形态只在跨档时更新一次，画面运动仍然逐帧连续，不会每度都重建 BLAS
    private const float RotationPreviewAngleStep = 3F;
    // 两次重采样之间的最小间隔，避免一次鼠标事件里反复重采样
    private const long RotationPreviewResampleIntervalMs = 16;
    private bool _gizmoRotationPreviewNeedsMaterialRemap;
    // 拖动实例当前叠加的残差旋转与枢轴，场景视口按它更新实例变换
    private Quaternion _sceneGizmoGridDragRotation = Quaternion.Identity;
    private Vector3 _sceneGizmoGridDragPivot;

    /// <summary>
    /// 旋转拖动开始：抓取重采样基准，拖动期间按角度重采样预览，松手后才写入对象
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>能否预览</returns>
    private bool BeginModelRotationPreview(SceneObject sceneObject)
    {
        _gizmoRotationPreviewActive = false;
        _gizmoRotationSource = null;
        if (_project is null || _sceneDocument is null) return false;
        if (sceneObject.Transform.Mode != TransformMode.Grid) return false;
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return false;
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        if (!world.TryGetBounds(out var origin, out _)) return false;
        var source = PrepareRotationSource(sceneObject, component, origin, world);
        if (source is null) return false;
        _gizmoRotationSource = source;
        // 拖动期间基准模型不变，坐标索引只建一次，预览每跨一档只做回采样
        _gizmoRotationLookup = ModelRotationRule.CreateLookup(source);
        _gizmoRotationOrigin = origin;
        _gizmoRotationStartDegrees = ReadModelRotation(sceneObject);
        _gizmoRotationPreviewQuantizedDegrees = _gizmoRotationStartDegrees;
        _gizmoRotationPreviewPivot = new Vector3(
            _sceneGizmoDragStartPosition.X, _sceneGizmoDragStartPosition.Y, _sceneGizmoDragStartPosition.Z);
        _gizmoRotationPreviewAtMs = 0;
        _sceneGizmoGridDragRotation = Quaternion.Identity;
        _sceneGizmoGridDragPivot = _gizmoRotationPreviewPivot;
        // 运行时材质重映射的结果在一次拖动里不会变，开始处判断一次即可，
        // 避免每步预览都遍历整份体素做比较
        _gizmoRotationPreviewNeedsMaterialRemap = false;
        foreach (var (_, value) in source.EnumerateOccupied())
        {
            if (!RemapRuntimeMaterial(sceneObject.Id, value).Equals(value))
            {
                _gizmoRotationPreviewNeedsMaterialRemap = true;
                break;
            }
        }

        _gizmoRotationPreviewActive = true;
        return true;
    }

    /// <summary>
    /// 拖动中按角度更新旋转预览：跨角度档时才重采样体素，档内残差交给实例旋转补足
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="axis">拖动轴</param>
    /// <param name="radians">本次累计角度</param>
    private void UpdateModelRotationPreview(SceneObject sceneObject, int axis, float radians)
    {
        if (!_gizmoRotationPreviewActive || _gizmoRotationSource is null) return;
        var degrees = AddAxisRotation(_gizmoRotationStartDegrees, axis, radians);
        // 没有可用的拖动实例时只刷新状态栏，等松手后按最终角度提交
        if (_dragInstanceStaticWorld is null || _dragInstanceObjectId != sceneObject.Id)
        {
            SetStatus($"旋转中：X {degrees.X:0.#}°  Y {degrees.Y:0.#}°  Z {degrees.Z:0.#}°");
            return;
        }

        var now = Environment.TickCount64;
        // 开启吸附时按吸附步长重采样，限定 1 度就 1 度一档、限定 15 度就 15 度一档；
        // 关闭吸附才用预览档位，用残差让画面连续
        var step = _editorSettings.RotationSnapEnabled
            ? MathF.Max(0.1F, RotationSnapStep(axis))
            : RotationPreviewAngleStep;
        var quantized = QuantizeRotationDegrees(degrees, axis, step);
        var quantizedChanged = MathF.Abs(GetAxisDegrees(quantized, axis) -
            GetAxisDegrees(_gizmoRotationPreviewQuantizedDegrees, axis)) >= step * 0.5F;
        var resample = _dragInstanceObjectWorld is null ||
            quantizedChanged && now - _gizmoRotationPreviewAtMs >= RotationPreviewResampleIntervalMs;
        if (resample)
        {
            var rotation = RotationFromDegrees(quantized);
            var anchor = ResolveRotationAnchor(_gizmoRotationSource, _gizmoRotationOrigin, rotation,
                _gizmoRotationPreviewPivot);
            if (!ModelRotationRule.TryResample(_gizmoRotationSource, _gizmoRotationLookup, rotation, anchor,
                    out var rotated, out _)) return;
            _gizmoRotationPreviewQuantizedDegrees = quantized;
            _gizmoRotationPreviewAtMs = now;
            _dragInstanceObjectWorld = _gizmoRotationPreviewNeedsMaterialRemap
                ? RemapRotationPreviewMaterials(sceneObject, rotated)
                : rotated;
            _sceneGizmoGridDragObjectId = sceneObject.Id;
            _sceneGizmoGridDragCells = VoxelCoord64.Zero;
            quantizedChanged = true;
        }

        // 档内剩余角度绕同一个枢轴叠加到实例上，体素形态不重建但运动连续。
        // 残差用完整旋转乘以量化旋转的逆，换轴时也保持在世界空间正确叠加
        var fullRotation = RotationFromDegrees(degrees);
        var quantizedRotation = RotationFromDegrees(_gizmoRotationPreviewQuantizedDegrees);
        var residual = Quaternion.Normalize(fullRotation * Quaternion.Inverse(quantizedRotation));
        var residualChanged = 1F - MathF.Abs(Quaternion.Dot(residual, _sceneGizmoGridDragRotation)) > 1e-7F;
        if (residualChanged)
        {
            _sceneGizmoGridDragRotation = residual;
            _sceneGizmoGridDragPivot = _gizmoRotationPreviewPivot;
        }

        if (quantizedChanged || residualChanged) _pipeline?.ResetAccumulation();
        SetStatus($"旋转中：X {degrees.X:0.#}°  Y {degrees.Y:0.#}°  Z {degrees.Z:0.#}°");
    }

    /// <summary>
    /// 读取三轴角度里指定轴的分量
    /// </summary>
    /// <param name="degrees">三轴角度</param>
    /// <param name="axis">轴序号</param>
    /// <returns>该轴角度</returns>
    private static float GetAxisDegrees(Vector3 degrees, int axis) => axis switch
    {
        0 => degrees.X,
        1 => degrees.Y,
        _ => degrees.Z,
    };

    /// <summary>
    /// 把目标角度量化到指定步长，其他两轴保持不变
    /// </summary>
    /// <param name="degrees">目标三轴角度</param>
    /// <param name="axis">拖动轴</param>
    /// <param name="step">量化步长，单位为度</param>
    /// <returns>量化后的三轴角度</returns>
    private static Vector3 QuantizeRotationDegrees(Vector3 degrees, int axis, float step)
    {
        switch (axis)
        {
            case 0:
                degrees.X = MathF.Round(degrees.X / step) * step;
                break;
            case 1:
                degrees.Y = MathF.Round(degrees.Y / step) * step;
                break;
            default:
                degrees.Z = MathF.Round(degrees.Z / step) * step;
                break;
        }

        return degrees;
    }

    /// <summary>预览世界按运行时材质表重映射，仅在确有变化时复制一份</summary>
    private VoxelWorld RemapRotationPreviewMaterials(SceneObject sceneObject, VoxelWorld world)
    {
        foreach (var (_, value) in world.EnumerateOccupied())
            if (!RemapRuntimeMaterial(sceneObject.Id, value).Equals(value))
                return world.Remap(value => RemapRuntimeMaterial(sceneObject.Id, value));
        return world;
    }

    /// <summary>
    /// 旋转拖动结束：按最后预览角度正式重采样并记录一步撤销，随后切回正式显示
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private void FinishModelRotationPreview(SceneObject sceneObject)
    {
        _sceneGizmoGridDragActive = false;
        _sceneGizmoGridDragObjectId = null;
        var active = _gizmoRotationPreviewActive;
        _gizmoRotationPreviewActive = false;
        _gizmoRotationSource = null;
        _gizmoRotationLookup = null;
        // 以松手时的实际拖动角度提交，避免最后一小段被预览节流吞掉
        var degrees = AddAxisRotation(_gizmoRotationStartDegrees, _sceneGizmoDragAxis, _sceneGizmoRotationDelta);
        if (!active || Vector3.Distance(degrees, _gizmoRotationStartDegrees) < 0.001F)
        {
            // 没有实际旋转：预览从未改动对象体素，清掉拖动实例即可
            _dragInstanceStaticWorld = null;
            _dragInstanceObjectWorld = null;
            _dragInstanceObjectId = StableId.Empty;
            _sceneGizmoGridDragCells = VoxelCoord64.Zero;
            InvalidateComposedWorldCache();
            _pickVersion++;
            _pipeline?.ResetAccumulation();
            return;
        }

        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return;
        ApplyModelRotation(sceneObject, component, RotationFromDegrees(degrees), degrees,
            _gizmoRotationPreviewPivot);
        // 最后角度的拖动实例先留着显示，后台换好正式工作集再切过去
        StartSceneSettle();
    }

    /// <summary>检查器里正在预览的浮点旋转（度）与所属对象</summary>
    private double[]? _pendingModelRotation;
    private StableId? _pendingModelRotationObjectId;

    /// <summary>
    /// 把检查器里待提交的浮点旋转写进模型；拖动期间只预览，松手或输入结束后才重新采样
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">模型组件</param>
    /// <returns>是否提交过</returns>
    private bool CommitPendingModelRotation(SceneObject sceneObject, ComponentRecord? component)
    {
        if (_pendingModelRotation is not { Length: 3 } pending || _pendingModelRotationObjectId != sceneObject.Id)
            return false;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsAnyItemActive()) return false;
        _pendingModelRotation = null;
        _pendingModelRotationObjectId = null;
        var degrees = new Vector3((float)pending[0], (float)pending[1], (float)pending[2]);
        if (component is not null)
        {
            ApplyModelRotation(sceneObject, component, RotationFromDegrees(degrees), degrees);
            return true;
        }

        // 没有体素模型组件的生成器对象：旋转参数写到生成器组件上并整体重建
        var generated = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent);
        if (generated is null) return false;
        ApplyGeneratedObjectTransform(sceneObject, generated, rotation: degrees);
        return true;
    }

    /// <summary>
    /// 四元数转欧拉角（度），按 YXZ 顺序与旋转构造保持一致
    /// </summary>
    /// <param name="rotation">四元数</param>
    /// <returns>三轴角度</returns>
    private static Vector3 ToEulerDegrees(Quaternion rotation)
    {
        var matrix = System.Numerics.Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        var forward = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));
        var pitch = MathF.Asin(Math.Clamp(-forward.Y, -1F, 1F));
        var yaw = MathF.Atan2(forward.X, forward.Z);
        var up = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
        var roll = MathF.Asin(Math.Clamp(up.Y, -1F, 1F));
        const float toDegrees = 180F / MathF.PI;
        return new Vector3(pitch * toDegrees, yaw * toDegrees, roll * toDegrees);
    }
}
