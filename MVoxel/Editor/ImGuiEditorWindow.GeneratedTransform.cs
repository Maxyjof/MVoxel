using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 生成器对象的浮点旋转与缩放。参数写在生成器组件的 JSON 里，重建体素时按参数重新采样，
/// 所以既不需要实例化体素模型，也不会在重新生成时丢失朝向与倍数
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private static readonly string[] GeneratedRotationKeys = ["rotationX", "rotationY", "rotationZ"];
    private static readonly string[] GeneratedScaleKeys = ["scaleX", "scaleY", "scaleZ"];

    /// <summary>
    /// 读取生成器组件上记录的对象级旋转与缩放
    /// </summary>
    /// <param name="component">生成器组件</param>
    /// <returns>旋转角（度）与缩放倍数</returns>
    private static (System.Numerics.Vector3 Rotation, System.Numerics.Vector3 Scale) ReadGeneratedTransform(
        ComponentRecord component)
    {
        var rotation = new System.Numerics.Vector3(
            ReadGeneratedNumber(component, "rotationX", 0F),
            ReadGeneratedNumber(component, "rotationY", 0F),
            ReadGeneratedNumber(component, "rotationZ", 0F));
        var scale = new System.Numerics.Vector3(
            ReadGeneratedNumber(component, "scaleX", 1F),
            ReadGeneratedNumber(component, "scaleY", 1F),
            ReadGeneratedNumber(component, "scaleZ", 1F));
        return (rotation, scale);
    }

    private static float ReadGeneratedNumber(ComponentRecord component, string name, float fallback)
    {
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out var value)) return fallback;
        return float.IsFinite(value) ? value : fallback;
    }

    private static void WriteGeneratedTransform(ComponentRecord component, System.Numerics.Vector3 rotation,
        System.Numerics.Vector3 scale)
    {
        var node = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject()
            : new JsonObject();
        node[GeneratedRotationKeys[0]] = JsonValue.Create(rotation.X);
        node[GeneratedRotationKeys[1]] = JsonValue.Create(rotation.Y);
        node[GeneratedRotationKeys[2]] = JsonValue.Create(rotation.Z);
        node[GeneratedScaleKeys[0]] = JsonValue.Create(MathF.Max(0F, scale.X));
        node[GeneratedScaleKeys[1]] = JsonValue.Create(MathF.Max(0F, scale.Y));
        node[GeneratedScaleKeys[2]] = JsonValue.Create(MathF.Max(0F, scale.Z));
        component.Data = JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// 把组件上记录的旋转与缩放套用到一份局部体素世界上
    /// </summary>
    /// <param name="component">生成器组件</param>
    /// <param name="local">按生成规则得到的局部体素</param>
    /// <returns>套用后的体素世界</returns>
    private static VoxelWorld ApplyGeneratedRenderTransform(ComponentRecord component, VoxelWorld local)
    {
        var (rotation, scale) = ReadGeneratedTransform(component);
        var world = local;
        if (MathF.Abs(scale.X - 1F) > .001F || MathF.Abs(scale.Y - 1F) > .001F ||
            MathF.Abs(scale.Z - 1F) > .001F)
        {
            if (scale.X <= 0F || scale.Y <= 0F || scale.Z <= 0F) return new VoxelWorld();
            var anchor = world.TryGetBounds(out var minimum, out _) ? minimum : VoxelCoord64.Zero;
            if (ModelScaleRule.TryResample(world, scale.X, scale.Y, scale.Z, anchor, out var scaled, out _))
                world = scaled;
        }

        if (MathF.Abs(rotation.X) > .001F || MathF.Abs(rotation.Y) > .001F || MathF.Abs(rotation.Z) > .001F)
        {
            var anchor = world.TryGetBounds(out var minimum, out _) ? minimum : VoxelCoord64.Zero;
            var quaternion = RotationFromDegrees(rotation);
            if (ModelRotationRule.TryResample(world, quaternion, anchor, out var rotated, out _)) world = rotated;
        }

        return world;
    }

    /// <summary>
    /// 修改生成器对象的旋转或缩放：参数写回组件后整体重建，避免叠加失真
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">生成器组件</param>
    /// <param name="rotation">新的旋转角（度），为空表示不改</param>
    /// <param name="scale">新的缩放倍数，为空表示不改</param>
    private void ApplyGeneratedObjectTransform(SceneObject sceneObject, ComponentRecord component,
        System.Numerics.Vector3? rotation = null, System.Numerics.Vector3? scale = null)
    {
        var (currentRotation, currentScale) = ReadGeneratedTransform(component);
        WriteGeneratedTransform(component, rotation ?? currentRotation, scale ?? currentScale);
        if (SceneComponentTypes.IsTileLayer(component))
        {
            RebuildGeneratedVoxels(sceneObject, component, "已更新瓷砖对象变换");
            ResetAfterVoxelTransform();
            return;
        }
        _objectVoxels.Remove(sceneObject.Id);
        EnsureGeneratedVoxelWorlds();
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
        ResetAfterVoxelTransform();
        SetStatus("已更新生成器对象的旋转与缩放");
    }

    /// <summary>
    /// 读取生成器对象当前的旋转与缩放，供检查器显示
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>角度与倍数；不是生成器对象时返回默认值</returns>
    private static (System.Numerics.Vector3 Rotation, System.Numerics.Vector3 Scale) ReadGeneratedObjectTransform(
        SceneObject sceneObject)
    {
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent);
        return component is null
            ? (System.Numerics.Vector3.Zero, new System.Numerics.Vector3(1F))
            : ReadGeneratedTransform(component);
    }
}
