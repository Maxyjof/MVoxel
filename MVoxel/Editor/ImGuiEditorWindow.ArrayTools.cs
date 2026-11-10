using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 对象阵列窗口与执行逻辑
/// </summary>
/// <remarks>
/// 阵列是一次性动作而不是组件：副本是真对象，要能单独选中、锁定、进预制体。
/// 参数只在应用时展开成对象，不持续驱动，所以不会覆盖用户后续的手动修改。
/// 平移副本保留原来的组件与资产引用；旋转或镜像副本会把体素烘焙成内嵌数据，
/// 否则重新加载时资产里的未旋转形状会对不上
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    private bool _showArrayWindow;
    private SceneArraySettings _arraySettings = SceneArraySettings.Default;
    private string _arrayMessage = string.Empty;

    /// <summary>一次阵列的执行结果，界面与自动化共用</summary>
    /// <param name="Copies">生成的副本数</param>
    /// <param name="Objects">新对象总数，含分组</param>
    /// <param name="Grouped">是否打了组</param>
    /// <param name="Roots">新副本的根对象标识</param>
    /// <param name="Message">结果说明</param>
    internal readonly record struct ObjectArrayResult(int Copies, int Objects, bool Grouped,
        IReadOnlyList<StableId> Roots, string Message);

    /// <summary>
    /// 阵列窗口
    /// </summary>
    private void DrawArrayWindow()
    {
        if (!_showArrayWindow) return;
        // 首次打开给一个能放下全部参数的尺寸，之后交给停靠布局记忆；
        // 再加最小尺寸约束，避免上次被拖窄后这次仍然挤成一团看不清参数
        ImGui.SetNextWindowSize(new Vector2(430F, 600F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(390F, 480F), new Vector2(float.MaxValue, float.MaxValue));
        if (!BeginToolWindow("阵列填充##ArrayWindow", PanelWindowFlags, () => _showArrayWindow = false))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("副本是独立对象；旋转与镜像会把体素烘焙成内嵌数据。");
        var roots = GetArrayRoots();
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("请先打开场景");
            ImGui.End();
            return;
        }

        if (roots.Count == 0) ImGui.TextDisabled("先在层级或场景里选中一个或多个对象");
        else
        {
            var locked = CountLockedSelection();
            ImGui.TextDisabled($"选中 {roots.Count} 个对象（不含子对象重复计数）" +
                               (locked > 0 ? $"，锁定对象 {locked} 个将被跳过" : string.Empty));
        }
        var settings = _arraySettings.Normalize();
        var bounds = TryGetArrayBounds(roots, out var minimum, out var maximum);
        var size = bounds ? maximum - minimum + new VoxelCoord64(1, 1, 1) : new VoxelCoord64(1, 1, 1);
        var autoCenter = new Vector3(
            (float)((minimum.X + maximum.X + 1) * 0.5D),
            (float)((minimum.Y + maximum.Y + 1) * 0.5D),
            (float)((minimum.Z + maximum.Z + 1) * 0.5D));
        var modeIndex = (int)settings.Mode;
        if (ImGui.Combo("方式##ArrayMode", ref modeIndex, "矩形\0环形\0镜像\0"))
            settings = settings with { Mode = (SceneArrayMode)modeIndex };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("矩形按三轴复制；环形绕轴转一圈；镜像越过中心平面翻一份");

        if (settings.Mode == SceneArrayMode.Rectangular)
        {
            var counts = settings.Counts;
            var countX = (int)counts.X;
            var countY = (int)counts.Y;
            var countZ = (int)counts.Z;
            ImGui.TextDisabled("份数");
            if (InspectorDragIntField("X 份数", "沿 X 轴复制多少份，包含原件。", ref countX, 1, 256))
                settings = settings with { Counts = settings.Counts with { X = countX } };
            if (InspectorDragIntField("Y 份数", null, ref countY, 1, 256))
                settings = settings with { Counts = settings.Counts with { Y = countY } };
            if (InspectorDragIntField("Z 份数", null, ref countZ, 1, 256))
                settings = settings with { Counts = settings.Counts with { Z = countZ } };
            var spacingIndex = (int)settings.Spacing;
                if (ImGui.Combo("间距口径##ArraySpacing", ref spacingIndex, "按格数\0按包围盒\0"))
                settings = settings with { Spacing = (SceneArraySpacing)spacingIndex };
            if (settings.Spacing == SceneArraySpacing.Cells)
            {
                var steps = settings.Steps;
                var stepX = (int)steps.X;
                var stepY = (int)steps.Y;
                var stepZ = (int)steps.Z;
                if (InspectorDragIntField("X 间距（格）", "相邻两份之间沿 X 的格数差，可为负。", ref stepX, -4096, 4096))
                    settings = settings with { Steps = settings.Steps with { X = stepX } };
                if (InspectorDragIntField("Y 间距（格）", null, ref stepY, -4096, 4096))
                    settings = settings with { Steps = settings.Steps with { Y = stepY } };
                if (InspectorDragIntField("Z 间距（格）", null, ref stepZ, -4096, 4096))
                    settings = settings with { Steps = settings.Steps with { Z = stepZ } };
            }
            else
            {
                var gap = settings.Gap;
                if (InspectorDragIntField("间隔（格）", "按选中内容的包围尺寸自动排开，这里只加额外空隙。", ref gap, 0, 256))
                    settings = settings with { Gap = gap };
            }
        }
        else if (settings.Mode == SceneArrayMode.Ring)
        {
            var axis = settings.Axis;
                if (ImGui.Combo("旋转轴##ArrayRingAxis", ref axis, "X 轴\0Y 轴\0Z 轴\0")) settings = settings with { Axis = axis };
            var ringCount = settings.RingCount;
            if (InspectorDragIntField("份数", "绕轴一圈的份数，包含原件；非 90 度倍数的份数会对体素重采样。", ref ringCount, 4, 64))
                settings = settings with { RingCount = ringCount };
            ImGui.TextDisabled($"每份旋转 {360F / Math.Max(4, ringCount):0.##}°");
            ImGui.TextDisabled("单个对象默认绕世界原点转；想绕其他点就在下面填自定义中心");
        }
        else
        {
            var axis = settings.Axis;
                if (ImGui.Combo("镜像轴##ArrayMirrorAxis", ref axis, "X 轴\0Y 轴\0Z 轴\0")) settings = settings with { Axis = axis };
            ImGui.TextDisabled("单个对象沿自身原点平面翻，多选时沿选中内容包围盒中心翻");
        }

        if (settings.Mode != SceneArrayMode.Rectangular)
        {
            var custom = settings.Center.HasValue;
            if (ImGui.Checkbox("自定义中心##ArrayCustomCenter", ref custom))
                settings = settings with { Center = custom ? autoCenter : null };
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("环形与镜像都绕这个点计算。单个对象默认绕自身中心，副本会叠回原位，所以要复制成一圈时应指向外部中心");
            if (settings.Center is { } center)
            {
                var centerX = center.X;
                var centerY = center.Y;
                var centerZ = center.Z;
                if (InspectorDragFloatField("中心 X", null, ref centerX, .5F, -4096F, 4096F, "%.1f"))
                    settings = settings with { Center = new Vector3(centerX, centerY, centerZ) };
                if (InspectorDragFloatField("中心 Y", null, ref centerY, .5F, -4096F, 4096F, "%.1f"))
                    settings = settings with { Center = new Vector3(centerX, centerY, centerZ) };
                if (InspectorDragFloatField("中心 Z", null, ref centerZ, .5F, -4096F, 4096F, "%.1f"))
                    settings = settings with { Center = new Vector3(centerX, centerY, centerZ) };
            }
        }

        var group = settings.Group;
        if (ImGui.Checkbox("打组到新建父对象##ArrayGroup", ref group)) settings = settings with { Group = group };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("把副本收进一个新建父对象，避免平铺的几十个对象撑乱层级");

        _arraySettings = settings;
        var copies = SceneObjectArrayMath.Plan(settings, size);
        ImGui.Separator();
        if (bounds)
        {
            ImGui.TextDisabled($"内容包围：{size.X} × {size.Y} × {size.Z} 格 · " +
                               $"中心 {(minimum.X + maximum.X + 1) * 0.5:0.#},{(minimum.Y + maximum.Y + 1) * 0.5:0.#},{(minimum.Z + maximum.Z + 1) * 0.5:0.#}");
        }

        ImGui.TextDisabled($"将生成 {copies.Count} 份副本");
        ImGui.BeginDisabled(roots.Count == 0 || copies.Count == 0);
        if (ImGui.Button("应用##ArrayApply", new Vector2(-1F, 0F)))
        {
            var result = ApplyObjectArray(settings, roots);
            _arrayMessage = result.Message;
        }

        ImGui.EndDisabled();
        if (_arrayMessage.Length > 0) ImGui.TextWrapped(_arrayMessage);
        ImGui.End();
    }

    /// <summary>
    /// 执行一次对象阵列，返回生成的副本信息
    /// </summary>
    /// <param name="settings">阵列参数</param>
    /// <param name="roots">参与阵列的根对象；省略时取当前选择</param>
    /// <returns>执行结果</returns>
    internal ObjectArrayResult ApplyObjectArray(SceneArraySettings settings, IReadOnlyList<SceneObject>? roots = null)
    {
        if (_sceneDocument is not { } scene) return new ObjectArrayResult(0, 0, false, [], "请先打开场景");
        roots ??= GetArrayRoots();
        if (roots.Count == 0) return new ObjectArrayResult(0, 0, false, [], "请先选中要阵列的对象");
        var data = settings.Normalize();
        if (!TryGetArrayBounds(roots, out var minimum, out var maximum))
            return new ObjectArrayResult(0, 0, false, [], "选中对象没有可用于定位的坐标或体素");
        var size = maximum - minimum + new VoxelCoord64(1, 1, 1);
        var pivot = new Vector3(
            (float)((minimum.X + maximum.X + 1) * 0.5D),
            (float)((minimum.Y + maximum.Y + 1) * 0.5D),
            (float)((minimum.Z + maximum.Z + 1) * 0.5D));
        // 镜像只选一个对象时，绕自身包围盒中心翻会把副本压回原位，等于没翻；
        // 改为绕该对象的原点平面翻，得到一份紧贴原件的镜像副本
        if (data.Mode == SceneArrayMode.Mirror && roots.Count == 1)
        {
            var origin = roots[0].Transform.VoxelOrigin;
            pivot = new Vector3(origin.X, origin.Y, origin.Z);
        }

        // 环形只选一个对象时同理：绕自身中心转仍是原地叠一堆，默认改绕世界原点，要绕别处就填自定义中心
        if (data.Mode == SceneArrayMode.Ring && roots.Count == 1) pivot = Vector3.Zero;

        // 用户显式给了中心就以它为准：环形转一圈、镜像绕外部平面都需要这个
        if (data.Center is { } customCenter) pivot = customCenter;
        var copies = SceneObjectArrayMath.Plan(data, size);
        if (copies.Count == 0) return new ObjectArrayResult(0, 0, false, [], "当前参数没有产生副本");

        SceneObject? group = null;
        if (data.Group)
        {
            group = new SceneGraph(scene).Add(NextArrayGroupName(scene), roots[0].ParentId,
                TransformComponent.FromGrid(GridTransform.Identity));
        }

        var newRoots = new List<StableId>();
        var objectCount = group is null ? 0 : 1;
        var baked = 0;
        foreach (var root in roots)
        foreach (var copy in copies)
        {
            var parentId = group?.Id ?? root.ParentId;
            var clones = CloneSubtree(root, parentId);
            foreach (var (original, clone) in clones)
                ApplyArrayCopy(original, clone, copy, pivot, data.Axis, ref baked);
            var cloneRoot = clones.FirstOrDefault(item => item.Original.Id == root.Id).Clone;
            if (cloneRoot is not null) newRoots.Add(cloneRoot.Id);
            objectCount += clones.Count;
        }

        if (newRoots.Count == 0) return new ObjectArrayResult(0, objectCount, group is not null, [],
            "没有生成任何副本");
        SetSceneSelection(newRoots);
        foreach (var root in roots) UpdateTransformMarker(root);
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        SetStatus($"阵列完成：{copies.Count} 份副本、{objectCount} 个新对象" +
                  (baked > 0 ? $"，其中 {baked} 个对象烘焙为内嵌体素" : string.Empty));
        return new ObjectArrayResult(copies.Count, objectCount, group is not null, newRoots,
            $"已生成 {copies.Count} 份副本、{objectCount} 个新对象" +
            (baked > 0 ? $"，{baked} 个旋转或镜像副本已烘焙为内嵌体素" : string.Empty));
    }

    /// <summary>
    /// 取当前选择作为阵列根：多选时去掉被其他选中对象包含的子对象
    /// </summary>
    private List<SceneObject> GetArrayRoots()
    {
        var scene = _sceneDocument;
        if (scene is null) return [];
        var ids = new List<StableId>();
        if (_selectedSceneObjectId is { } primary) ids.Add(primary);
        foreach (var id in _selectedSceneObjectIds)
            if (!ids.Contains(id)) ids.Add(id);
        var selected = scene.Objects.Where(item => ids.Contains(item.Id) && !item.Locked).ToList();
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var byId = scene.Objects.ToDictionary(item => item.Id);
        return selected.Where(item => !HasSelectedAncestor(item, selectedIds, byId)).ToList();
    }

    /// <summary>统计被锁定的选中对象数量，用于界面提示</summary>
    private int CountLockedSelection()
    {
        var scene = _sceneDocument;
        if (scene is null) return 0;
        var ids = new List<StableId>();
        if (_selectedSceneObjectId is { } primary) ids.Add(primary);
        ids.AddRange(_selectedSceneObjectIds);
        return scene.Objects.Count(item => ids.Contains(item.Id) && item.Locked);
    }

    private static bool HasSelectedAncestor(SceneObject item, HashSet<StableId> selectedIds,
        IReadOnlyDictionary<StableId, SceneObject> byId)
    {
        for (var current = item.ParentId; current is { } parentId && byId.TryGetValue(parentId, out var parent);)
        {
            if (selectedIds.Contains(parentId)) return true;
            current = parent.ParentId;
        }

        return false;
    }

    /// <summary>
    /// 选中内容的联合包围盒：包含每个根对象的完整子树；优先用体素包围，没有体素时按对象原点
    /// </summary>
    private bool TryGetArrayBounds(IReadOnlyList<SceneObject> roots, out VoxelCoord64 minimum,
        out VoxelCoord64 maximum)
    {
        minimum = new VoxelCoord64(long.MaxValue, long.MaxValue, long.MaxValue);
        maximum = new VoxelCoord64(long.MinValue, long.MinValue, long.MinValue);
        var any = false;
        var scene = _sceneDocument;
        if (scene is null) return false;
        var visited = new HashSet<StableId>();
        foreach (var root in roots)
        foreach (var item in PrefabSystem.CollectSubtree(scene, root))
        {
            if (!visited.Add(item.Id)) continue;
            if (_objectVoxels.TryGetWorld(item.Id, out var world) && world.TryGetBounds(out var itemMin, out var itemMax))
            {
                minimum = new VoxelCoord64(Math.Min(minimum.X, itemMin.X), Math.Min(minimum.Y, itemMin.Y),
                    Math.Min(minimum.Z, itemMin.Z));
                maximum = new VoxelCoord64(Math.Max(maximum.X, itemMax.X), Math.Max(maximum.Y, itemMax.Y),
                    Math.Max(maximum.Z, itemMax.Z));
            }
            else
            {
                var origin = item.Transform.VoxelOrigin;
                minimum = new VoxelCoord64(Math.Min(minimum.X, origin.X), Math.Min(minimum.Y, origin.Y),
                    Math.Min(minimum.Z, origin.Z));
                maximum = new VoxelCoord64(Math.Max(maximum.X, origin.X), Math.Max(maximum.Y, origin.Y),
                    Math.Max(maximum.Z, origin.Z));
            }
            any = true;
        }

        return any;
    }

    /// <summary>给分组起一个不与现有对象重名的名字</summary>
    private static string NextArrayGroupName(SceneDocument scene)
    {
        const string baseName = "阵列";
        var name = baseName;
        for (var suffix = 2; scene.Objects.Any(item => item.Name == name); suffix++) name = $"{baseName} {suffix}";
        return name;
    }

    /// <summary>
    /// 复制一棵子树并全部换新标识，预制体链接表按新标识重映射
    /// </summary>
    private List<(SceneObject Original, SceneObject Clone)> CloneSubtree(SceneObject root, StableId? parentId)
    {
        var scene = _sceneDocument!;
        var subtree = PrefabSystem.CollectSubtree(scene, root);
        var idMap = new Dictionary<StableId, StableId>();
        foreach (var item in subtree) idMap[item.Id] = StableId.New();
        var result = new List<(SceneObject Original, SceneObject Clone)>(subtree.Count);
        foreach (var item in subtree)
        {
            var clone = SceneSerializer.CloneObject(item);
            var originalId = item.Id;
            clone.Id = idMap[originalId];
            clone.ParentId = originalId == root.Id
                ? parentId
                : item.ParentId is { } parent && idMap.TryGetValue(parent, out var mapped) ? mapped : parentId;
            if (clone.Prefab is { } prefab && prefab.Links.Count > 0)
            {
                var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in prefab.Links)
                {
                    if (!StableId.TryParse(key, out var keyId) || !idMap.TryGetValue(keyId, out var newKey)) continue;
                    remapped[newKey.Value.ToString()] = value;
                }

                prefab.Links = remapped;
            }

            scene.Objects.Add(clone);
            result.Add((item, clone));
        }

        return result;
    }

    /// <summary>
    /// 把一个副本的体素与变换落到目标对象上
    /// </summary>
    private void ApplyArrayCopy(SceneObject original, SceneObject clone, SceneArrayCopy copy, Vector3 pivot,
        int axis, ref int baked)
    {
        var hasWorld = _objectVoxels.TryGetWorld(original.Id, out var sourceWorld) &&
                       sourceWorld.OccupiedVoxelCount > 0;
        var rotated = copy.RotationDegrees != 0F || copy.MirrorAxis >= 0;
        if (!hasWorld)
        {
            ApplyTransformOnly(original, clone, copy, pivot, axis);
            return;
        }

        var transformed = SceneObjectArrayMath.Transform(sourceWorld!, copy, pivot, axis);
        _objectVoxels.AssignWorld(clone.Id, transformed);
        if (rotated)
        {
            // 旋转与镜像无法用资产里的原始朝向还原，烘焙成内嵌体素
            BakeCloneVoxels(clone, transformed);
            baked++;
            if (transformed.TryGetBounds(out var newMinimum, out _))
            {
                if (clone.Transform.Mode == TransformMode.Grid)
                    clone.Transform.SetGrid(new GridTransform(newMinimum,
                        ComposeGridRotation(clone.Transform.Grid.Rotation, copy, axis)));
                else
                    clone.Transform.SetFree(new FreeTransform(
                        new Double3(newMinimum.X, newMinimum.Y, newMinimum.Z), clone.Transform.Free.Rotation));
            }
        }
        else
        {
            if (clone.Transform.Mode == TransformMode.Grid)
                clone.Transform.SetGrid(new GridTransform(clone.Transform.Grid.Position + copy.Translation,
                    clone.Transform.Grid.Rotation));
            else
                clone.Transform.SetFree(new FreeTransform(
                    new Double3(clone.Transform.Free.Position.X + copy.Translation.X,
                        clone.Transform.Free.Position.Y + copy.Translation.Y,
                        clone.Transform.Free.Position.Z + copy.Translation.Z), clone.Transform.Free.Rotation));
        }

        UpdateTransformMarker(clone);
    }

    /// <summary>非体素对象只变换坐标与朝向</summary>
    private static void ApplyTransformOnly(SceneObject original, SceneObject clone, SceneArrayCopy copy,
        Vector3 pivot, int axis)
    {
        if (clone.Transform.Mode == TransformMode.Grid)
        {
            var position = new Vector3(clone.Transform.Grid.Position.X, clone.Transform.Grid.Position.Y,
                clone.Transform.Grid.Position.Z);
            var moved = SceneObjectArrayMath.TransformPoint(position, copy, pivot, axis);
            clone.Transform.SetGrid(new GridTransform(
                new VoxelCoord64((long)Math.Round(moved.X), (long)Math.Round(moved.Y), (long)Math.Round(moved.Z)),
                ComposeGridRotation(clone.Transform.Grid.Rotation, copy, axis)));
            return;
        }

        var freePosition = new Vector3((float)clone.Transform.Free.Position.X,
            (float)clone.Transform.Free.Position.Y, (float)clone.Transform.Free.Position.Z);
        var movedFree = SceneObjectArrayMath.TransformPoint(freePosition, copy, pivot, axis);
        var rotation = clone.Transform.Free.Rotation;
        if (copy.MirrorAxis >= 0)
            rotation = SceneObjectArrayMath.MirrorRotation(rotation, copy.MirrorAxis);
        else if (copy.RotationDegrees != 0F)
            rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(SceneObjectArrayMath.AxisVector(axis),
                copy.RotationDegrees * MathF.PI / 180F) * rotation);
        clone.Transform.SetFree(new FreeTransform(new Double3(movedFree.X, movedFree.Y, movedFree.Z), rotation));
    }

    /// <summary>90 度倍数的环形副本同步格旋转；其他角度保持原值</summary>
    private static OrthogonalRotation ComposeGridRotation(OrthogonalRotation rotation, SceneArrayCopy copy, int axis)
    {
        if (copy.MirrorAxis >= 0 || copy.RotationDegrees == 0F) return rotation;
        var quarters = SceneObjectArrayMath.QuarterTurns(copy.RotationDegrees);
        var result = rotation;
        for (var turn = 0; turn < quarters; turn++)
            result = result.Compose(axis switch
            {
                0 => OrthogonalRotation.AroundX90,
                1 => OrthogonalRotation.AroundY90,
                _ => OrthogonalRotation.AroundZ90,
            });
        return result;
    }

    /// <summary>
    /// 把旋转或镜像副本的体素烘焙成内嵌数据：去掉生成器组件，模型组件断开资产引用
    /// </summary>
    private static void BakeCloneVoxels(SceneObject clone, VoxelWorld world)
    {
        foreach (var generated in clone.Components.Where(SceneComponentTypes.IsGeneratedVoxelComponent).ToArray())
            clone.Components.Remove(generated);
        var model = clone.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (model is null)
        {
            model = SceneComponentTypes.CreateVoxelModel(string.Empty, world.OccupiedVoxelCount, "阵列副本",
                string.IsNullOrWhiteSpace(clone.Name) ? "阵列副本" : clone.Name);
            clone.Components.Add(model);
            return;
        }

        SceneComponentTypes.WriteVoxelModel(model, SceneComponentTypes.ReadVoxelModel(model) with
        {
            Model = string.Empty,
            SourceSegment = string.Empty,
            SourceOrigin = null,
            VoxelCount = world.OccupiedVoxelCount,
            MirrorX = false,
            MirrorY = false,
            MirrorZ = false,
        });
    }
}
