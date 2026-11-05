using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 树生成器组件：参数面板、生成体素与实例化为模型
/// </summary>
/// <remarks>
/// 参数只改组件数据，不自动重建；用户点“生成体素”才会按当前参数换一棵树。
/// 生成结果保存在组件内部，检查器看不到、也进不了绘制模式。
/// 底部的实例化按钮把组件替换成模型组件，之后可以正常绘制，但只能用撤销变回生成器
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>等待在组件循环结束后执行的实例化目标，避免遍历组件时改动列表</summary>
    private ComponentRecord? _pendingTreeInstantiate;

    /// <summary>
    /// 添加组件或创建对象时初始化树生成器：选中并立即生成第一棵树
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">新建的树组件</param>
    private void InitializeTreeGeneratorObject(SceneObject sceneObject, ComponentRecord component)
    {
        if (sceneObject.Transform.Mode == TransformMode.Free)
        {
            sceneObject.Transform.SetGrid(sceneObject.Transform.ToGrid());
            UpdateTransformMarker(sceneObject);
        }

        _selectedSceneObjectId = sceneObject.Id;
        _activeTool = EditorTool.Pick;
        _voxelEditTargetId = null;
        _sceneGizmoMode = SceneGizmoMode.Select;
        RebuildGeneratedVoxels(sceneObject, component, "已生成树体素");
        SetStatus("已添加树生成器：调整参数后点“生成体素”换一棵树，满意后可以实例化为体素模型");
    }

    /// <summary>
    /// 检查器中的树生成器内容
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    private void DrawTreeGeneratorComponent(SceneObject sceneObject, ComponentRecord component)
    {
        var data = SceneComponentTypes.ReadTreeGenerator(component);
        var updated = data;
        var sortingChanged = false;

        DrawInspectorHint("参数只改生成规则，点“生成体素”才会替换当前的树；生成的体素保存在场景文件中，不进入绘制模式。");

        DrawTreeStyleSection(ref updated);
        if (ImGui.CollapsingHeader("树干与枝条##TreeTrunkSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(8F);
            DrawTreeTrunkSection(ref updated);
            DrawTreeBranchSection(ref updated);
            ImGui.Unindent(8F);
            ImGui.Dummy(new Vector2(0F, 2F));
        }

        if (ImGui.CollapsingHeader("树冠与树叶##TreeCrownSection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(8F);
            DrawTreeCrownSection(ref updated);
            ImGui.Unindent(8F);
            ImGui.Dummy(new Vector2(0F, 2F));
        }

        if (ImGui.CollapsingHeader("颜色与排序##TreeColorSection"))
        {
            ImGui.Indent(8F);
            DrawTreeColorSection(ref updated, ref sortingChanged);
            ImGui.Unindent(8F);
            ImGui.Dummy(new Vector2(0F, 2F));
        }

        // 参数改动只写数据，等用户点生成；排序变化只影响合成顺序，不需要重建体素
        if (updated != data)
        {
            SceneComponentTypes.WriteTreeGenerator(component, updated);
            MarkSceneDocumentDirty();
            if (sortingChanged)
            {
                RebuildObjectVoxelWorld();
                _pipeline?.ResetAccumulation();
            }
        }

        DrawTreeGenerateSection(sceneObject, component, updated);
        DrawTreeInstantiateSection(sceneObject, component);
    }

    /// <summary>
    /// 风格与种子
    /// </summary>
    /// <param name="data">当前参数</param>
    private static void DrawTreeStyleSection(ref TreeGeneratorComponentData data)
    {
        BeginInspectorField("风格", "切换风格会套用该风格的推荐参数，之后仍可逐项微调。");
        var style = (int)data.Style;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##TreeStyle", ref style, "阔叶树\0针叶树\0灌木\0棕榈\0垂柳\0樱花树\0枯树\0"))
            data = data.WithStyleDefaults((TreeStyle)style);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("阔叶树：直干加椭球树冠\n针叶树：层叠锥形\n灌木：低矮丛生\n棕榈：弯干加放射叶片\n垂柳：树冠垂条\n樱花树：矮阔叶粉冠\n枯树：只留枝干");
        BeginInspectorField("种子", "同一组参数与种子永远生成同一棵树；换种子会改变枝条与树冠的随机分布。");
        var seed = data.Seed;
        var seedWidth = MathF.Max(ImGui.GetFontSize() * 4F,
            ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("随机种子").X -
            ImGui.GetStyle().FramePadding.X * 2F - ImGui.GetStyle().ItemSpacing.X - 2F);
        ImGui.SetNextItemWidth(seedWidth);
        if (ImGui.DragInt("##TreeSeed", ref seed, .5F, -1_000_000, 1_000_000)) data = data with { Seed = seed };
        ImGui.SameLine();
        if (ImGui.SmallButton("随机种子")) data = data with { Seed = Random.Shared.Next(-1_000_000, 1_000_001) };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只改种子，点“生成体素”后生效");
    }

    /// <summary>
    /// 树干参数
    /// </summary>
    /// <param name="data">当前参数</param>
    private static void DrawTreeTrunkSection(ref TreeGeneratorComponentData data)
    {
        ImGui.TextDisabled("树干");
        var height = data.TrunkHeight;
        if (InspectorDragIntField("高度", "树干高度（格）；树冠从“树冠起始”比例处向上展开。", ref height, .5F, 2,
                TreeGeneratorComponentData.MaximumTrunkHeight))
            data = data with { TrunkHeight = height };
        DrawTreeFloatField("半径", "##TreeRadius", data.TrunkRadius, .05F, 0.4F, 12F, "树干底部半径（格）", out var radius);
        if (radius != data.TrunkRadius) data = data with { TrunkRadius = radius };
        DrawTreeFloatField("倾斜", "##TreeLean", data.TrunkLean, .5F, 0F, 70F, "整棵树的倾斜角度（度）", out var lean);
        if (lean != data.TrunkLean) data = data with { TrunkLean = lean };
        DrawTreeFloatField("弯曲", "##TreeBend", data.TrunkBend, .01F, 0F, 1F, "树干的 S 形弯曲强度", out var bend);
        if (bend != data.TrunkBend) data = data with { TrunkBend = bend };
        DrawTreeFloatField("收缩", "##TreeTaper", data.TrunkTaper, .01F, 0F, 0.95F, "树干从下到上的变细比例", out var taper);
        if (taper != data.TrunkTaper) data = data with { TrunkTaper = taper };
        DrawTreeFloatField("树根", "##TreeRootFlare", data.RootFlare, .01F, 0F, 1F, "树根外扩强度", out var flare);
        if (flare != data.RootFlare) data = data with { RootFlare = flare };
    }

    /// <summary>
    /// 枝条参数
    /// </summary>
    /// <param name="data">当前参数</param>
    private static void DrawTreeBranchSection(ref TreeGeneratorComponentData data)
    {
        ImGui.TextDisabled("枝条");
        var count = data.BranchCount;
        if (InspectorDragIntField("主枝数量", "从树干上长出的主枝数量，0 表示没有枝条。", ref count, .2F, 0, 24))
            data = data with { BranchCount = count };
        DrawTreeFloatField("枝条长度", "##TreeBranchLength", data.BranchLength, .1F, 0.5F, 64F, "主枝长度（格）", out var length);
        if (length != data.BranchLength) data = data with { BranchLength = length };
        DrawTreeFloatField("枝条粗细", "##TreeBranchThickness", data.BranchThickness, .05F, 0.3F, 8F, "主枝粗细（格）", out var thickness);
        if (thickness != data.BranchThickness) data = data with { BranchThickness = thickness };
        DrawTreeFloatField("上扬角", "##TreeBranchAngle", data.BranchAngle, .5F, -20F, 89F, "枝条上扬角度（度）", out var angle);
        if (angle != data.BranchAngle) data = data with { BranchAngle = angle };
        DrawTreeFloatField("下垂", "##TreeDroop", data.BranchDroop, .01F, -1F, 1F, "枝条下垂程度，负值上扬", out var droop);
        if (droop != data.BranchDroop) data = data with { BranchDroop = droop };
        BeginInspectorField("分叉层数", "枝条再分叉的层数，层数越多越繁茂，体素也越多。");
        var depth = data.BranchDepth;
        if (ImGui.SliderInt("##TreeBranchDepth", ref depth, 0, 3)) data = data with { BranchDepth = depth };
    }

    /// <summary>
    /// 树冠与树叶参数
    /// </summary>
    /// <param name="data">当前参数</param>
    private static void DrawTreeCrownSection(ref TreeGeneratorComponentData data)
    {
        BeginInspectorField("树冠形态", "球体与椭球适合阔叶树，圆锥适合针叶树，层叠与伞形各有轮廓特点。");
        var shape = (int)data.CrownShape;
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.Combo("##TreeCrownShape", ref shape, "球形\0椭球\0圆锥\0伞形\0层叠\0无树冠\0"))
            data = data with { CrownShape = (TreeCrownShape)shape };
        DrawTreeFloatField("树冠起始", "##TreeCrownStart", data.CrownStart, .01F, 0.05F, 1F, "树冠底部占树干高度的比例", out var start);
        if (start != data.CrownStart) data = data with { CrownStart = start };
        DrawTreeFloatField("树冠半径", "##TreeCrownRadius", data.CrownRadius, .1F, 1F, 64F, "树冠水平半径（格）", out var crownRadius);
        if (crownRadius != data.CrownRadius) data = data with { CrownRadius = crownRadius };
        DrawTreeFloatField("树冠高度", "##TreeCrownHeight", data.CrownHeight, .1F, 1F, 64F, "树冠纵向高度（格）", out var crownHeight);
        if (crownHeight != data.CrownHeight) data = data with { CrownHeight = crownHeight };
        DrawTreeFloatField("叶片密度", "##TreeLeafDensity", data.LeafDensity, .01F, 0.05F, 1F, "树叶填充比例，越小越透光", out var density);
        if (density != data.LeafDensity) data = data with { LeafDensity = density };
        DrawTreeFloatField("叶团大小", "##TreeLeafSize", data.LeafSize, .05F, 0.5F, 8F, "枝头叶团的半径（格）", out var leafSize);
        if (leafSize != data.LeafSize) data = data with { LeafSize = leafSize };
    }

    /// <summary>
    /// 颜色、发光与排序
    /// </summary>
    /// <param name="data">当前参数</param>
    /// <param name="sortingChanged">排序图层或顺序是否变化</param>
    private void DrawTreeColorSection(ref TreeGeneratorComponentData data, ref bool sortingChanged)
    {
        BeginInspectorField("树皮颜色", "树干与枝条的颜色。");
        var bark = new Vector3(data.BarkR / 255F, data.BarkG / 255F, data.BarkB / 255F);
        if (ImGui.ColorEdit3("##TreeBarkColor", ref bark, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoAlpha))
            data = data with { BarkR = ToByte(bark.X), BarkG = ToByte(bark.Y), BarkB = ToByte(bark.Z) };
        BeginInspectorField("树叶主色", "树冠与枝头叶团的主色。");
        var leaf = new Vector3(data.LeafR / 255F, data.LeafG / 255F, data.LeafB / 255F);
        if (ImGui.ColorEdit3("##TreeLeafColor", ref leaf, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoAlpha))
            data = data with { LeafR = ToByte(leaf.X), LeafG = ToByte(leaf.Y), LeafB = ToByte(leaf.Z) };
        BeginInspectorField("树叶辅色", "按坐标随机混入的第二种叶色，用来打散纯色。");
        var leaf2 = new Vector3(data.Leaf2R / 255F, data.Leaf2G / 255F, data.Leaf2B / 255F);
        if (ImGui.ColorEdit3("##TreeLeaf2Color", ref leaf2, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoAlpha))
            data = data with { Leaf2R = ToByte(leaf2.X), Leaf2G = ToByte(leaf2.Y), Leaf2B = ToByte(leaf2.Z) };
        DrawTreeFloatField("辅色比例", "##TreeColorVariation", data.ColorVariation, .01F, 0F, 1F, "辅色混入比例", out var variation);
        if (variation != data.ColorVariation) data = data with { ColorVariation = variation };
        var emissive = data.EmissiveLeaves;
        if (ImGui.Checkbox("树叶自发光", ref emissive)) data = data with { EmissiveLeaves = emissive };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("给树叶加自发光标记，适合灵树、夜樱这类题材。");
        var materialSlot = (int)data.MaterialSlot;
        if (InspectorDragIntField("材质槽", "生成体素使用的本地材质槽；0 表示使用全局材质。", ref materialSlot, .2F, 0,
                ushort.MaxValue))
            data = data with { MaterialSlot = (ushort)Math.Clamp(materialSlot, 0, ushort.MaxValue) };
        BeginInspectorField("排序图层", "同一坐标按排序图层、排序顺序和层级顺序决定显示优先级。");
        var sortingLayer = data.SortingLayer;
        if (_sceneDocument is { } document && ImGui.BeginCombo("##TreeSortingLayer", sortingLayer))
        {
            foreach (var layer in document.SortingLayerNames)
            {
                if (ImGui.Selectable(layer, layer == sortingLayer))
                {
                    data = data with { SortingLayer = layer };
                    sortingChanged = true;
                }
            }

            ImGui.EndCombo();
        }

        BeginInspectorField("排序顺序", "同一排序图层内，数值大的覆盖数值小的体素。");
        var sortingOrder = data.SortingOrder;
        if (ExpressionInput.Int("##TreeSortingOrder", ref sortingOrder))
        {
            data = data with { SortingOrder = sortingOrder };
            sortingChanged = true;
        }
    }

    /// <summary>
    /// 生成按钮与统计信息
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    /// <param name="data">当前参数</param>
    private void DrawTreeGenerateSection(SceneObject sceneObject, ComponentRecord component,
        TreeGeneratorComponentData data)
    {
        ImGui.Separator();
        var voxelCount = _objectVoxels.TryGetWorld(sceneObject.Id, out var world) ? world.OccupiedVoxelCount : 0;
        ImGui.TextDisabled($"体素 {voxelCount:N0} · 负载 {SceneComponentTypes.GetGeneratedVoxelPayloadLength(component) / 1024F:0.0} KB");
        if (SceneComponentTypes.TryReadGeneratedSignature(component, out var storedSignature))
        {
            if (storedSignature != data.GenerationSignature())
                ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F), "参数已修改，点“生成体素”后生效");
        }
        else
        {
            ImGui.TextDisabled("还没有生成过体素");
        }

        if (_generatedVoxelTruncated.TryGetValue(sceneObject.Id, out var truncated) && truncated)
            ImGui.TextColored(new Vector4(1F, .65F, .2F, 1F),
                $"已超过 {ProceduralVoxelBudget:N0} 格上限，超出的部分被截断");
        if (ImGui.Button("生成体素", new Vector2(-1F, 0F)))
            RebuildGeneratedVoxels(sceneObject, component, "已生成树体素");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按当前参数重新生成一棵树，替换组件里已有的体素数据；参数调整不会自动生效。");
    }

    /// <summary>
    /// 实例化按钮：把生成结果固定成体素模型
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    private void DrawTreeInstantiateSection(SceneObject sceneObject, ComponentRecord component)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.PanelHeader));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToColor(EditorPalette.AccentSoft));
        if (ImGui.Button("实例化为体素模型", new Vector2(-1F, 0F)))
            _pendingTreeInstantiate = component;
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把当前生成的体素固定成模型组件：之后可以进入绘制模式，但不能再改回树生成器；" +
                             "如果只是想换个样子，请先调参数再点“生成体素”。");
        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(EditorPalette.MutedText));
        DrawInspectorHint("实例化会移除树生成器组件，体素转存到模型组件里。想反悔只能用撤销（Ctrl+Z）。");
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// 带标签的浮点拖动字段
    /// </summary>
    private static void DrawTreeFloatField(string label, string id, float value, float speed, float minimum,
        float maximum, string tooltip, out float result)
    {
        // 标签可拖动改值：和检查器其他数值字段一致
        result = value;
        InspectorDragFloatField(label, tooltip, ref result, speed, minimum, maximum);
    }
}
