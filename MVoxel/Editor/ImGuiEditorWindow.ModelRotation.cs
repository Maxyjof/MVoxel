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
    /// 在现有旋转上叠加一次绕世界轴的旋转，按基准重新采样并写回组件参数
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="axis">世界轴：0/1/2 对应 X/Y/Z</param>
    /// <param name="radians">旋转角度，按轴正方向</param>
    private void ApplyModelRotationDelta(SceneObject sceneObject, int axis, float radians)
    {
        if (_project is null || _sceneDocument is null) return;
        if (sceneObject.Transform.Mode != TransformMode.Grid)
        {
            SetStatus("只有体素模型对象支持浮点旋转");
            return;
        }

        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null)
        {
            SetStatus("对象没有体素模型组件");
            return;
        }

        var axisVector = axis switch
        {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        var delta = Quaternion.CreateFromAxisAngle(axisVector, radians);
        var current = RotationFromDegrees(ReadModelRotation(sceneObject));
        var target = Quaternion.Normalize(delta * current);
        ApplyModelRotation(sceneObject, component, target);
    }

    /// <summary>
    /// 按目标四元数重新生成模型体素，参数写回组件
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">模型组件</param>
    /// <param name="rotation">目标旋转</param>
    private void ApplyModelRotation(SceneObject sceneObject, ComponentRecord component, Quaternion rotation,
        Vector3? degrees = null)
    {
        if (_project is null || _sceneDocument is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var world = _objectVoxels.GetOrCreate(sceneObject.Id);
        if (!world.TryGetBounds(out var origin, out _))
        {
            SetStatus("模型为空，无法旋转");
            return;
        }

        var source = ResolveScaleBase(sceneObject, world, origin);
        if (source is null) return;

        // 先按当前倍数还原到目标尺寸，再按旋转采样；两步都以基准为准
        var baseWorld = source;
        if (!ModelScaleRule.IsIdentity(data.ScaleX) || !ModelScaleRule.IsIdentity(data.ScaleY) ||
            !ModelScaleRule.IsIdentity(data.ScaleZ))
        {
            if (!ModelScaleRule.TryResample(source, data.ScaleX, data.ScaleY, data.ScaleZ, origin, out var scaled,
                    out var scaleError, _scaleCancellation.Token))
            {
                SetStatus($"旋转失败：{scaleError}");
                return;
            }

            baseWorld = scaled;
        }

        if (!ModelRotationRule.TryResample(baseWorld, rotation, origin, out var rotated, out var error))
        {
            SetStatus($"旋转失败：{error}");
            return;
        }

        if (!ReplaceObjectVoxels(sceneObject, rotated, origin, scale: (data.ScaleX, data.ScaleY, data.ScaleZ)))
        {
            SetStatus("旋转结果无法应用到对象");
            return;
        }

        // 把角度写回组件，供检查器与下次旋转继续使用
        var euler = degrees ?? ToEulerDegrees(rotation);
        SceneComponentTypes.WriteVoxelModel(component, SceneComponentTypes.ReadVoxelModel(component) with
        {
            RotationX = euler.X,
            RotationY = euler.Y,
            RotationZ = euler.Z,
        });
        MarkSceneDocumentDirty();
        ResetAfterVoxelTransform();
        SetStatus($"已按浮点角度旋转：{rotated.OccupiedVoxelCount:N0} 体素");
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
