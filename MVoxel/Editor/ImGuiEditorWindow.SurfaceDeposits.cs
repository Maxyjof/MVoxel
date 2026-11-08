using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private readonly Dictionary<(uint Mask, bool SceneVisibility, VoxelCoord64 Origin), SurfaceReceiverLayerTable> _receiverLayerTables = new();
    private SceneDocument? _receiverLayerCacheScene;
    private long _receiverLayerCacheSignature;

    private SurfaceReceiverLayerTable? ResolveReceiverLayerTable(float[] deposits, uint cameraMask,
        bool sceneVisibility, VoxelCoord64 origin)
    {
        if (_sceneDocument is null || !NeedsReceiverLayers(deposits)) return null;
        var signature = ReceiverLayerSignature();
        if (!ReferenceEquals(_receiverLayerCacheScene, _sceneDocument) || _receiverLayerCacheSignature != signature)
        {
            _receiverLayerTables.Clear();
            _receiverLayerCacheScene = _sceneDocument;
            _receiverLayerCacheSignature = signature;
        }
        var key = (cameraMask, sceneVisibility, origin);
        if (_receiverLayerTables.TryGetValue(key, out var cached)) return cached;
        var snapshot = _objectVoxels.CaptureReceiverLayers(_sceneDocument, cameraMask, sceneVisibility);
        var table = SurfaceReceiverLayerTable.Build(snapshot, origin);
        // 通常只需要场景与渲染两份；限制缓存条目数，避免频繁切换相机时无限保留大表。
        if (_receiverLayerTables.Count >= 2) _receiverLayerTables.Clear();
        _receiverLayerTables[key] = table;
        return table;
    }

    private long ReceiverLayerSignature()
    {
        unchecked
        {
            var signature = RenderStateSignature();
            if (_sceneDocument is null) return signature;
            foreach (var item in _sceneDocument.Objects)
            {
                if (!_objectVoxels.TryGetWorld(item.Id, out var world)) continue;
                signature = signature * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(world);
                signature = signature * 31 + world.ContentVersion;
            }
            return signature;
        }
    }

    private static bool NeedsReceiverLayers(float[] deposits)
    {
        for (var index = 31; index < deposits.Length; index += 32)
            if (BitConverter.SingleToUInt32Bits(deposits[index]) != uint.MaxValue) return true;
        return false;
    }

    private float[] CollectSurfaceDeposits(uint cameraMask)
    {
        if (_sceneDocument is null) return [];
        var deposits = new List<(int Order, int Sequence, SceneObject Owner, SurfaceDepositComponentData Data, bool Snow)>();
        var sequence = 0;
        foreach (var owner in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(owner) || !_sceneDocument.IsLayerVisible(owner.LayerIndex) ||
                !RenderWorldComposer.LayerAllowed(cameraMask, owner.LayerIndex)) continue;
            foreach (var component in owner.Components)
            {
                if (!component.Enabled || !SceneComponentTypes.IsSurfaceDeposit(component)) continue;
                var data = SceneComponentTypes.ReadSurfaceDeposit(component);
                if (!data.Enabled) continue;
                deposits.Add((data.SortOrder, sequence++, owner, data, SceneComponentTypes.IsSnowDecal(component)));
            }
        }
        deposits.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Sequence.CompareTo(b.Sequence));
        var packed = new List<float>(deposits.Count * 32);
        foreach (var (_, _, owner, data, snow) in deposits)
        {
            var world = SurfaceDepositWorld(owner, data);
            if (!Matrix4x4.Invert(world, out var inverse)) continue;
            packed.AddRange([
                inverse.M11, inverse.M21, inverse.M31, inverse.M41,
                inverse.M12, inverse.M22, inverse.M32, inverse.M42,
                inverse.M13, inverse.M23, inverse.M33, inverse.M43,
                data.Size.X * .5F, data.Size.Y * .5F, data.Size.Z * .5F, data.EdgeFeather,
                data.Color.X, data.Color.Y, data.Color.Z, data.Opacity,
                data.Roughness, data.RoughnessStrength, data.ColorStrength, data.DepthFeather,
                MathF.Cos(data.NormalFadeStart * MathF.PI / 180F), MathF.Cos(data.NormalFadeEnd * MathF.PI / 180F), data.Coverage, data.PatternScale,
                data.PatternSoftness, data.Seed, data.NormalStrength, BitConverter.UInt32BitsToSingle(data.ReceiverLayers),
            ]);
        }
        return [.. packed];
    }

    private static Matrix4x4 SurfaceDepositWorld(SceneObject owner, SurfaceDepositComponentData data)
    {
        var t = owner.Transform;
        Matrix4x4 rotation;
        Vector3 position;
        if (t.Mode == TransformMode.Grid)
        {
            var r = t.Grid.Rotation;
            rotation = new Matrix4x4(r.M00, r.M10, r.M20, 0F, r.M01, r.M11, r.M21, 0F,
                r.M02, r.M12, r.M22, 0F, 0F, 0F, 0F, 1F);
            position = new Vector3(t.Grid.Position.X, t.Grid.Position.Y, t.Grid.Position.Z);
        }
        else
        {
            var q = t.Free.Rotation;
            q = float.IsFinite(q.LengthSquared()) && q.LengthSquared() > 1e-8F ? Quaternion.Normalize(q) : Quaternion.Identity;
            rotation = Matrix4x4.CreateFromQuaternion(q);
            position = new Vector3((float)t.Free.Position.X, (float)t.Free.Position.Y, (float)t.Free.Position.Z);
        }
        return Matrix4x4.CreateTranslation(data.Offset) *
            Matrix4x4.CreateScale(Math.Max(1, t.ScaleX), Math.Max(1, t.ScaleY), Math.Max(1, t.ScaleZ)) *
            rotation * Matrix4x4.CreateTranslation(position);
    }

    private void DrawSurfaceDepositInspector(ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadSurfaceDeposit(component);
        var changed = ImGui.Checkbox("启用投射", ref data.Enabled);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("控制此投射组件是否生效");
        BeginInspectorField("接收图层", "仅对所选图层中可见的体素表面生效；解析地面仅在选择全部图层时接收投射。");
        var layerNames = _sceneDocument?.LayerNames ?? ["默认"];
        var layerLabel = data.ReceiverLayers == uint.MaxValue ? "全部图层" : data.ReceiverLayers == 0 ? "无图层" : "自定义图层";
        if (ImGui.BeginCombo("##DepositReceiverLayers", layerLabel))
        {
            if (ImGui.Selectable("全部图层")) { data.ReceiverLayers = uint.MaxValue; changed = true; }
            if (ImGui.Selectable("无图层")) { data.ReceiverLayers = 0; changed = true; }
            ImGui.Separator();
            for (var index = 0; index < Math.Min(layerNames.Count, 32); index++)
            {
                var bit = 1u << index;
                var included = (data.ReceiverLayers & bit) != 0;
                if (ImGui.Checkbox($"{layerNames[index]}##DepositLayer{index}", ref included))
                {
                    data.ReceiverLayers = included ? data.ReceiverLayers | bit : data.ReceiverLayers & ~bit;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        changed |= InspectorDragVector3Field("范围 X/Y/Z",
            "投射盒的局部宽度、深度和长度；Y 轴为投射深度。旋转和缩放跟随所属对象。", ref data.Size, .1F, .01F,
            100000F);
        changed |= InspectorDragVector3Field("局部偏移", "投射盒中心相对所属对象原点的偏移。", ref data.Offset, .1F,
            -100000F, 100000F);
        changed |= InspectorDragFloatField("强度", "控制投射对表面颜色、粗糙度和法线的整体影响。", ref data.Opacity, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("边缘淡化", "沿投射盒 X/Z 边缘向内淡化的比例。", ref data.EdgeFeather, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("深度淡化", "沿投射盒 Y 方向两端向内淡化的比例。", ref data.DepthFeather, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("角度开始", "表面法线与投射盒局部上方向的夹角达到此值时开始淡出。", ref data.NormalFadeStart, .5F, 0F, 180F, "%.0f°");
        changed |= InspectorDragFloatField("角度结束", "达到此夹角时不再投射；不能小于开始角度。", ref data.NormalFadeEnd, .5F, 0F, 180F, "%.0f°");
        changed |= InspectorDragFloatField("覆盖率", "随机图案覆盖投射范围的比例；0 为无覆盖，1 为全部覆盖。", ref data.Coverage, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("图案尺度", "控制局部噪声频率；数值越大斑块越细。", ref data.PatternScale, .01F, .001F, 100F);
        changed |= InspectorDragFloatField("图案柔和度", "控制随机覆盖边界的过渡宽度。", ref data.PatternSoftness, .01F, 0F, 1F);
        changed |= InspectorDragIntField("随机种子", "更改局部覆盖图案；固定种子与对象变换时分布保持不变。", ref data.Seed, 1F, 0, 65535);
        BeginInspectorField("颜色", "覆盖表面的颜色；颜色影响可单独调低，以保留底材外观。");
        changed |= ImGui.ColorEdit3("##DepositColor", ref data.Color);
        changed |= InspectorDragFloatField("颜色影响", "在原表面颜色与投射颜色之间混合的强度。", ref data.ColorStrength, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("目标粗糙度", "积水通常较低，积雪通常较高；与原材质按粗糙度影响混合。", ref data.Roughness, .01F, .01F, 1F);
        changed |= InspectorDragFloatField("粗糙度影响", "独立控制投射对表面粗糙度的影响。", ref data.RoughnessStrength, .01F, 0F, 1F);
        changed |= InspectorDragFloatField("法线扰动", "根据覆盖图案轻微扰动表面法线，不生成真实几何厚度。", ref data.NormalStrength, .01F, 0F, 1F);
        changed |= InspectorDragIntField("叠加顺序", "多个投射盒重叠时从小到大依次应用；相同值按场景对象顺序应用。", ref data.SortOrder, 1F, -10000, 10000);
        if (changed)
        {
            SceneComponentTypes.WriteSurfaceDeposit(component, data);
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            _scenePipeline?.ResetAccumulation();
        }
    }
}
