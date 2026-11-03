using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Scene;
using NumVector3 = System.Numerics.Vector3;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 光照组件的检查器编辑、对象创建与场景灯光收集；光照对象不需要体素模型
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 光照组件检查器：类型、颜色、强度、范围、聚光角与阴影开关
    /// </summary>
    private void DrawLightComponentInspector(ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadLight(component);
        var changed = false;
        var shape = (int)data.Shape;
        BeginInspectorField("类型", "点光向四周发光，聚光按角度范围照射，方向光按对象朝向平行照射。");
        if (ImGui.Combo("##LightShape", ref shape, "点光\0聚光\0方向光\0", 3))
        {
            data = data with { Shape = (LightShape)Math.Clamp(shape, 0, 2) };
            changed = true;
        }

        BeginInspectorField("颜色", "灯光颜色。");
        var color = new NumVector3(data.ColorR, data.ColorG, data.ColorB);
        if (ImGui.ColorEdit3("##LightColor", ref color, ImGuiColorEditFlags.Float))
        {
            data = data with { ColorR = color.X, ColorG = color.Y, ColorB = color.Z };
            changed = true;
        }

        BeginInspectorField("强度", "灯光强度，点光与聚光按距离衰减。");
        var intensity = data.Intensity;
        if (ImGui.DragFloat("##LightIntensity", ref intensity, 0.05F, 0F, 1000F, "%.2f"))
        {
            data = data with { Intensity = intensity };
            changed = true;
        }

        if (data.Shape is LightShape.Point or LightShape.Spot)
        {
            BeginInspectorField("范围", "光照影响的最大距离，超出范围后衰减到零。");
            var range = data.Range;
            if (ImGui.DragFloat("##LightRange", ref range, 0.25F, 0.01F, 100000F, "%.2f"))
            {
                data = data with { Range = range };
                changed = true;
            }
        }

        if (data.Shape == LightShape.Spot)
        {
            BeginInspectorField("外角", "聚光锥的外角，边缘衰减到零。");
            var outer = data.SpotAngleDegrees;
            if (ImGui.SliderFloat("##LightSpotOuter", ref outer, 0.1F, 179F, "%.1f°"))
            {
                data = data with { SpotAngleDegrees = outer };
                changed = true;
            }

            BeginInspectorField("内角", "聚光锥的完整亮度范围，从内角到外角逐渐衰减。");
            var inner = data.InnerSpotAngleDegrees;
            if (ImGui.SliderFloat("##LightSpotInner", ref inner, 0F, data.SpotAngleDegrees, "%.1f°"))
            {
                data = data with { InnerSpotAngleDegrees = inner };
                changed = true;
            }
        }

        BeginInspectorField("阴影", "让点光、聚光或方向光在体素上投射硬阴影；阴影采样会增加渲染开销。");
        var shadows = data.CastShadows;
        if (ImGui.Checkbox("投射阴影##LightShadows", ref shadows))
        {
            data = data with { CastShadows = shadows };
            changed = true;
        }

        if (data.Shape != LightShape.Directional)
        {
            BeginInspectorField("配光曲线", "围绕灯光朝向的亮度分布：窄束、宽束和蝴蝶结用于模拟常见 IES 轮廓。");
            var profile = (int)data.Profile;
            if (ImGui.Combo("##LightProfile", ref profile, "默认\0窄束\0宽束\0蝴蝶结\0", 4))
            {
                data = data with { Profile = (LightProfile)Math.Clamp(profile, 0, 3) };
                changed = true;
            }
        }

        if (!changed) return;
        SceneComponentTypes.WriteLight(component, data);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
    }

    /// <summary>
    /// 在场景中心附近创建一个只带光照组件的对象，用它照明不需要体素模型
    /// </summary>
    private void CreateLightObject(LightShape shape)
    {
        if (_project is null) return;
        _sceneDocument ??= new SceneDocument { Name = "主场景" };
        var name = shape switch
        {
            LightShape.Spot => "聚光灯",
            LightShape.Directional => "方向光",
            _ => "点光源",
        };
        var baseName = name;
        for (var suffix = 2; _sceneDocument.Objects.Any(item => item.Name == name); suffix++)
            name = $"{baseName} {suffix}";
        var position = new Double3(_scenePan.X, _sceneFreeMoveHeight + 8D, _scenePan.Y);
        var rotation = shape == LightShape.Directional
            ? Quaternion.CreateFromAxisAngle(NumVector3.UnitX, -0.9F)
            : Quaternion.Identity;
        var sceneObject = new SceneGraph(_sceneDocument).Add(name,
            transform: TransformComponent.FromFree(new FreeTransform(position, rotation)));
        sceneObject.Components.Add(SceneComponentTypes.CreateLight(
            LightComponentData.Default with { Shape = shape }));
        _selectedSceneObjectId = sceneObject.Id;
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        SetStatus($"已创建{name}：选中后可在检查器或场景中调整范围和角度");
    }

    /// <summary>光照对象的世界位置；格变换对象取整数格原点</summary>
    private static Vector3 GetObjectWorldPosition(SceneObject sceneObject) =>
        sceneObject.Transform.Mode == TransformMode.Free
            ? new Vector3(
                (float)sceneObject.Transform.Free.Position.X,
                (float)sceneObject.Transform.Free.Position.Y,
                (float)sceneObject.Transform.Free.Position.Z)
            : new Vector3(
                sceneObject.Transform.Grid.Position.X,
                sceneObject.Transform.Grid.Position.Y,
                sceneObject.Transform.Grid.Position.Z);

    /// <summary>方向光等自由对象的照射方向</summary>
    private static Vector3 GetObjectWorldForward(SceneObject sceneObject)
    {
        var rotation = sceneObject.Transform.Mode == TransformMode.Free
            ? sceneObject.Transform.Free.Rotation
            : Quaternion.Identity;
        if (rotation.LengthSquared() < 0.000001F || !float.IsFinite(rotation.LengthSquared()))
            rotation = Quaternion.Identity;
        else rotation = Quaternion.Normalize(rotation);
        var forward = NumVector3.Transform(NumVector3.UnitZ, rotation);
        return forward.LengthSquared() > 0.000001F
            ? new Vector3(forward.X, forward.Y, forward.Z)
            : new Vector3(0F, 0F, 1F);
    }

    /// <summary>
    /// 判断对象及其父级是否可见
    /// </summary>
    /// <remarks>
    /// 这个判断在每帧里会被调用成百上千次（逐个对象、逐条绘制分支）。
    /// 之前每次调用都重建全场景字典和防环集合，对象越多分配越接近平方级，
    /// 实测能顶到每秒近千兆字节的托管分配并触发频繁 GC。
    /// 现在字典按场景结构指纹缓存，防环改用固定栈缓冲，单次调用不再分配
    /// </remarks>
    private bool IsSceneObjectVisible(SceneObject sceneObject)
    {
        if (_sceneDocument is null) return sceneObject.Visible;
        var byId = VisibilityIndex();
        if (byId is null) return sceneObject.Visible;
        Span<StableId> visited = stackalloc StableId[MaxVisibilityChainDepth];
        var depth = 0;
        for (var current = sceneObject; current is not null;)
        {
            if (!current.Visible) return false;
            // 父级链深度超过上限视为层级损坏，按不可见处理，避免无限循环
            if (depth == visited.Length) return false;
            for (var index = 0; index < depth; index++)
            {
                if (visited[index] == current.Id) return false;
            }

            visited[depth++] = current.Id;
            current = current.ParentId is { } parent && byId.TryGetValue(parent, out var found) ? found : null;
        }

        return true;
    }

    /// <summary>父级链允许的最大深度，超过即视为层级损坏</summary>
    private const int MaxVisibilityChainDepth = 64;

    private Dictionary<StableId, SceneObject>? _visibilityIndexCache;
    private long _visibilityIndexSignature = long.MinValue;
    private int _visibilityIndexFrame = -1;

    /// <summary>
    /// 取场景对象索引，按帧与结构指纹缓存，供可见性判断复用
    /// </summary>
    /// <returns>对象标识到对象的索引，没有场景时为 null</returns>
    private Dictionary<StableId, SceneObject>? VisibilityIndex()
    {
        var objects = _sceneDocument?.Objects;
        if (objects is null) return null;
        var frame = ImGui.GetFrameCount();
        if (_visibilityIndexCache is not null && _visibilityIndexFrame == frame)
        {
            return _visibilityIndexCache;
        }

        _visibilityIndexFrame = frame;
        var signature = SceneStructureSignature(objects);
        if (_visibilityIndexCache is not null && signature == _visibilityIndexSignature)
        {
            return _visibilityIndexCache;
        }

        _visibilityIndexSignature = signature;
        var index = new Dictionary<StableId, SceneObject>(objects.Count);
        foreach (var item in objects)
        {
            index[item.Id] = item;
        }

        _visibilityIndexCache = index;
        return index;
    }

    /// <summary>
    /// 计算场景结构指纹：标识、父级、可见性与相机启用状态，零分配
    /// </summary>
    /// <param name="objects">场景对象集合</param>
    /// <returns>结构指纹</returns>
    private static long SceneStructureSignature(List<SceneObject> objects)
    {
        unchecked
        {
            var hash = (long)objects.Count * 397;
            foreach (var item in objects)
            {
                hash = hash * 31 + item.Id.GetHashCode();
                hash = hash * 31 + (item.ParentId?.GetHashCode() ?? 0);
                hash = hash * 31 + (item.Visible ? 7 : 3);
                var components = item.Components;
                for (var index = 0; index < components.Count; index++)
                {
                    var component = components[index];
                    if (!SceneComponentTypes.IsCamera(component)) continue;
                    hash = hash * 31 + (component.Enabled ? 11 : 5);
                }
            }

            return hash;
        }
    }

    /// <summary>
    /// 收集当前可见图层、可见对象上的光照组件，转换为渲染管线使用的本地光源数据
    /// </summary>
    private IReadOnlyList<SceneLight> CollectSceneLights(uint cullingMask)
    {
        var result = new List<SceneLight>();
        if (_sceneDocument is null) return result;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsLight);
            if (component is null || !component.Enabled || !IsSceneObjectVisible(sceneObject)) continue;
            if (!RenderWorldComposer.LayerAllowed(cullingMask, sceneObject.LayerIndex)) continue;
            var data = SceneComponentTypes.ReadLight(component);
            var position = GetObjectWorldPosition(sceneObject);
            var direction = GetObjectWorldForward(sceneObject);
            var color = SrgbToLinear(new Vector3(data.ColorR, data.ColorG, data.ColorB));
            var kind = data.Shape switch
            {
                LightShape.Spot => SceneLightKind.Spot,
                LightShape.Directional => SceneLightKind.Directional,
                _ => SceneLightKind.Point,
            };
            result.Add(new SceneLight(
                new System.Numerics.Vector3(position.X, position.Y, position.Z),
                new System.Numerics.Vector3(direction.X, direction.Y, direction.Z),
                new System.Numerics.Vector3(color.X, color.Y, color.Z),
                data.Intensity, data.Range, data.SpotAngleDegrees, data.InnerSpotAngleDegrees,
                kind, Enabled: true, CastShadows: data.CastShadows, Profile: (int)data.Profile));
            if (result.Count >= SceneLight.Capacity) break;
        }

        return result;
    }

    private static Vector3 SrgbToLinear(Vector3 color) => new(
        SrgbChannelToLinear(color.X), SrgbChannelToLinear(color.Y), SrgbChannelToLinear(color.Z));

    private static float SrgbChannelToLinear(float value)
    {
        value = Math.Clamp(value, 0F, 1F);
        return value <= 0.04045F ? value / 12.92F : MathF.Pow((value + 0.055F) / 1.055F, 2.4F);
    }

    /// <summary>把当前场景光源同步给渲染管线，供实时渲染与路径追踪共同使用</summary>
    private void PushSceneLights(uint cullingMask) => _pipeline?.SetSceneLights(CollectSceneLights(cullingMask));
}
