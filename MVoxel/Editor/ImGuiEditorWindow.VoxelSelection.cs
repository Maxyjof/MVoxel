using System.Drawing;
using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 体素框选组件：在对象自己的体素数据里框出一个长方体范围，
/// 按目标颜色挑出范围内的体素，再对这批体素做换色、删除、移动、旋转和缩放
/// </summary>
/// <remarks>
/// 选择结果只存在于当前编辑会话：组件里持久化的是范围与目标颜色，
/// 实际选中的坐标集合由“选择体素”按钮当场扫描世界得到，避免把上万个坐标写进场景文件。
/// 所有修改都走 VoxelStrokeCommand，和笔刷共用同一套撤销历史、场景标脏与动画记录
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>单次框选允许扫描的最大体积，防止误把范围拖成整张地图后卡死界面</summary>
    private const long VoxelSelectionMaxVolume = 8_000_000;

    /// <summary>场景视图里逐格高亮的上限，超过只画范围线框，避免每帧投影过多点</summary>
    private const int VoxelSelectionMaxHighlight = 4096;

    /// <summary>当前选中的体素属于哪个对象</summary>
    private StableId _voxelSelectionObjectId = StableId.Empty;

    /// <summary>当前选中的体素坐标</summary>
    private VoxelCoord64[] _voxelSelectionCells = [];

    /// <summary>建立选择时的范围与颜色指纹，用来提示“范围已改、需要重新选择”</summary>
    private string _voxelSelectionSignature = string.Empty;

    // 换色栏的运行时参数：目标色默认跟随组件的匹配色，不写回场景文件
    private Vector4 _voxelSelectionPaintColor = new(1F, 1F, 1F, 1F);
    private bool _voxelSelectionPaintInitialized;
    private bool _voxelSelectionKeepEmissive = true;
    private bool _voxelSelectionKeepMaterial = true;

    // 移动栏的位移量
    private int _voxelSelectionMoveX;
    private int _voxelSelectionMoveY;
    private int _voxelSelectionMoveZ;

    // 旋转轴与缩放倍率
    private int _voxelSelectionRotateAxis = 1;
    private float _voxelSelectionScale = 2F;

    // 场景视图里拖动框范围时的临时状态
    private bool _voxelSelectionGizmoDragging;
    private int _voxelSelectionGizmoAxis = -1;
    private int _voxelSelectionGizmoFaceSign = 1;
    private VoxelCoord64 _voxelSelectionGizmoStartMinimum;
    private VoxelCoord64 _voxelSelectionGizmoStartSize;
    private float _voxelSelectionGizmoPixelsPerUnit = 1F;

    /// <summary>
    /// 添加组件时按对象现有体素包围盒给一个贴合的初始范围；
    /// 局部原点落在包围盒最小角，起点从 0 开始，坐标保持小数值
    /// </summary>
    /// <param name="sceneObject">挂载组件的对象</param>
    /// <returns>新的框选组件记录</returns>
    private ComponentRecord CreateVoxelSelectionForObject(SceneObject sceneObject)
    {
        var data = VoxelSelectionComponentData.Default;
        if (_objectVoxels.TryGetWorld(sceneObject.Id, out var world) && world.OccupiedVoxelCount > 0 &&
            world.TryGetBounds(out var minimum, out var maximum))
        {
            var objectOrigin = GetObjectGridOrigin(sceneObject);
            data = data with
            {
                StartX = 0,
                StartY = 0,
                StartZ = 0,
                SizeX = (int)Math.Clamp(maximum.X - minimum.X + 1, 1, 4096),
                SizeY = (int)Math.Clamp(maximum.Y - minimum.Y + 1, 1, 4096),
                SizeZ = (int)Math.Clamp(maximum.Z - minimum.Z + 1, 1, 4096),
                OriginX = (int)Math.Clamp(minimum.X - objectOrigin.X, -10_000_000, 10_000_000),
                OriginY = (int)Math.Clamp(minimum.Y - objectOrigin.Y, -10_000_000, 10_000_000),
                OriginZ = (int)Math.Clamp(minimum.Z - objectOrigin.Z, -10_000_000, 10_000_000),
                Editing = true,
            };
        }

        _voxelSelectionPaintInitialized = false;
        return SceneComponentTypes.CreateVoxelSelection(data);
    }

    /// <summary>
    /// 检查器中的体素框选组件内容
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">组件记录</param>
    private void DrawVoxelSelectionComponent(SceneObject sceneObject, ComponentRecord component)
    {
        // 旧场景里范围存的是世界坐标，这里一次性换成相对对象原点的局部坐标
        MigrateVoxelSelectionToLocal(sceneObject, component);
        var data = SceneComponentTypes.ReadVoxelSelection(component);
        var hasModel = sceneObject.Components.Any(item => SceneComponentTypes.IsVoxelModel(item) && item.Enabled);
        var editing = data.Editing && _selectedSceneObjectId == sceneObject.Id;

        ImGui.BeginDisabled(!hasModel);
        if (ImGui.Button(editing ? "退出框选模式" : "进入框选模式", new Vector2(-1F, 0F)))
        {
            data = data with { Editing = !editing };
            SceneComponentTypes.WriteVoxelSelection(component, data);
            MarkSceneDocumentDirty();
            if (!editing)
            {
                // 框选与绘制是两件事：进入框选后切到选择工具，避免点框时顺手画出方块
                _selectedSceneObjectId = sceneObject.Id;
                _sceneGizmoMode = SceneGizmoMode.Select;
                _activeTool = EditorTool.Pick;
                _voxelEditTargetId = null;
                _brushPreview = null;
                MarkComponentEditingOwner(sceneObject.Id);
            }

            SetStatus(editing ? "已退出框选模式" : "已进入框选模式：拖动中心手柄平移范围，拖动六面手柄改长宽高");
        }

        ImGui.EndDisabled();
        if (!hasModel && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("需要对象上有启用的模型组件；请先添加模型组件");

        var changed = false;
        var startX = data.StartX;
        var startY = data.StartY;
        var startZ = data.StartZ;
        var startDrag = BeginInspectorField("起点（局部格）",
            "相对局部原点的格坐标，随对象一起移动；局部原点默认对齐模型包围盒最小角，所以新添加时从 0 开始。",
            draggable: true);
        changed |= DrawVoxelSelectionInt3("##VoxelSelectionStart", ref startX, ref startY, ref startZ,
            -10_000_000, 10_000_000, startDrag);
        var sizeX = data.SizeX;
        var sizeY = data.SizeY;
        var sizeZ = data.SizeZ;
        var sizeDrag = BeginInspectorField("长宽高（格）",
            "范围覆盖的格数，从起点向正方向展开；至少 1 格，单轴上限 4096 格。", draggable: true);
        changed |= DrawVoxelSelectionInt3("##VoxelSelectionSize", ref sizeX, ref sizeY, ref sizeZ, 1, 4096,
            sizeDrag);

        // 局部坐标换算到世界后才是体素数据的键，这里给一行只读坐标方便核对
        var objectOrigin = GetObjectGridOrigin(sceneObject);
        var worldMinimum = data.WorldMinimum(objectOrigin);
        ImGui.TextDisabled($"局部原点 {data.OriginX}, {data.OriginY}, {data.OriginZ} · 世界起点 " +
            $"{worldMinimum.X}, {worldMinimum.Y}, {worldMinimum.Z}");
        ImGui.SameLine();
        if (ImGui.SmallButton("对齐模型"))
        {
            if (_objectVoxels.TryGetWorld(sceneObject.Id, out var world) && world.TryGetBounds(out var boundsMin, out _))
            {
                var newOriginX = boundsMin.X - objectOrigin.X;
                var newOriginY = boundsMin.Y - objectOrigin.Y;
                var newOriginZ = boundsMin.Z - objectOrigin.Z;
                // 起点补偿原点移动，范围框在世界上保持原位
                data = data with
                {
                    OriginX = (int)Math.Clamp(newOriginX, -10_000_000, 10_000_000),
                    OriginY = (int)Math.Clamp(newOriginY, -10_000_000, 10_000_000),
                    OriginZ = (int)Math.Clamp(newOriginZ, -10_000_000, 10_000_000),
                    StartX = data.StartX + data.OriginX - (int)Math.Clamp(newOriginX, -10_000_000, 10_000_000),
                    StartY = data.StartY + data.OriginY - (int)Math.Clamp(newOriginY, -10_000_000, 10_000_000),
                    StartZ = data.StartZ + data.OriginZ - (int)Math.Clamp(newOriginZ, -10_000_000, 10_000_000),
                };
                startX = data.StartX;
                startY = data.StartY;
                startZ = data.StartZ;
                changed = true;
            }
            else
            {
                SetStatus("该对象还没有体素，无法对齐模型包围盒");
            }
        }

        BeginInspectorField("目标颜色", "要挑选的体素颜色；透明度也参与匹配。");
        var color = new Vector4(data.ColorR / 255F, data.ColorG / 255F, data.ColorB / 255F, data.ColorA / 255F);
        var pickerWidth = MathF.Max(ImGui.GetFontSize() * 4F, ImGui.GetContentRegionAvail().X -
            ImGui.CalcTextSize("取笔刷色").X - ImGui.GetStyle().FramePadding.X * 2F - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(pickerWidth);
        if (ImGui.ColorEdit4("##VoxelSelectionColor", ref color,
                ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar))
        {
            data = data with
            {
                ColorR = ToByte(color.X), ColorG = ToByte(color.Y), ColorB = ToByte(color.Z), ColorA = ToByte(color.W),
            };
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("点开色条取任意颜色");
        ImGui.SameLine();
        if (ImGui.SmallButton("取笔刷色"))
        {
            var brush = _sampledBrushColor ?? _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
            data = data with { ColorR = brush.R, ColorG = brush.G, ColorB = brush.B, ColorA = brush.A };
            changed = true;
        }

        var tolerance = data.ColorTolerance;
        if (InspectorDragFloatField("颜色容差", "逐通道允许的最大差值。0 只匹配完全相同的颜色；调大可容忍相近色，255 等于不按颜色筛选。", ref tolerance, .5F, 0F, 255F, "%.0f"))
        {
            data = data with { ColorTolerance = tolerance };
            changed = true;
        }

        var emissiveOnly = data.EmissiveOnly;
        if (ImGui.Checkbox("只选带自发光标记的体素", ref emissiveOnly))
        {
            data = data with { EmissiveOnly = emissiveOnly };
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关闭时普通颜色与自发光体素一并匹配");

        if (changed)
        {
            data = data with
            {
                StartX = startX, StartY = startY, StartZ = startZ,
                SizeX = sizeX, SizeY = sizeY, SizeZ = sizeZ,
            };
            SceneComponentTypes.WriteVoxelSelection(component, data);
            MarkSceneDocumentDirty();
        }

        ImGui.TextDisabled($"范围 {data.SizeX} × {data.SizeY} × {data.SizeZ} = {data.Volume:N0} 格" +
            (data.Volume > VoxelSelectionMaxVolume ? "（超过单次扫描上限）" : string.Empty));
        ImGui.Dummy(new Vector2(0F, UiScale.Px(2F)));
        if (ImGui.Button("选择范围内该颜色的体素", new Vector2(-1F, 0F)))
            SelectVoxelsInRange(sceneObject, data);

        var selectedCount = _voxelSelectionObjectId == sceneObject.Id ? _voxelSelectionCells.Length : 0;
        if (selectedCount > 0)
        {
            var swatchOrigin = ImGui.GetCursorScreenPos();
            var swatchSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetTextLineHeight());
            ImGui.Dummy(swatchSize);
            ImGui.GetWindowDrawList().AddRectFilled(swatchOrigin, swatchOrigin + swatchSize,
                ImGui.ColorConvertFloat4ToU32(new Vector4(data.ColorR / 255F, data.ColorG / 255F, data.ColorB / 255F, 1F)), 2F);
            ImGui.SameLine();
            ImGui.Text($"已选中 {selectedCount:N0} 格");
            if (_voxelSelectionSignature != VoxelSelectionSignature(sceneObject, data))
                ImGui.TextDisabled("范围或颜色已改变，重新点上方按钮刷新选择");
        }
        else
        {
            ImGui.TextDisabled("尚未选择体素");
        }

        DrawVoxelSelectionLockedSection("换色", selectedCount > 0, () => DrawVoxelSelectionRecolor(sceneObject));
        DrawVoxelSelectionLockedSection("删除", selectedCount > 0, () => DrawVoxelSelectionDelete(sceneObject, selectedCount));
        DrawVoxelSelectionLockedSection("移动", selectedCount > 0, () => DrawVoxelSelectionMove(sceneObject));
        DrawVoxelSelectionLockedSection("旋转", selectedCount > 0, () => DrawVoxelSelectionRotate(sceneObject));
        DrawVoxelSelectionLockedSection("缩放", selectedCount > 0, () => DrawVoxelSelectionScale(sceneObject));
    }

    /// <summary>
    /// 三轴整数拖动输入，宽度按当前可用区域等分
    /// </summary>
    /// <param name="id">控件作用域标识</param>
    /// <param name="x">X 值</param>
    /// <param name="y">Y 值</param>
    /// <param name="z">Z 值</param>
    /// <param name="minimum">最小值</param>
    /// <param name="maximum">最大值</param>
    /// <returns>三个轴是否有任一变化</returns>
    private static bool DrawVoxelSelectionInt3(string id, ref int x, ref int y, ref int z, int minimum, int maximum,
        float dragPixels = 0F)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(ImGui.GetFontSize() * 3F, (ImGui.GetContentRegionAvail().X - spacing * 2F) / 3F);
        var changed = false;
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragInt("##X", ref x, .25F, minimum, maximum);
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragInt("##Y", ref y, .25F, minimum, maximum);
        ImGui.SameLine(0F, spacing);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.DragInt("##Z", ref z, .25F, minimum, maximum);
        ImGui.PopID();
        // 标签拖动时三轴按同一格数增减，和三分量浮点字段手感一致
        if (MathF.Abs(dragPixels) > 0F)
        {
            var steps = (int)MathF.Round(dragPixels * .25F);
            if (steps != 0)
            {
                x = Math.Clamp(x + steps, minimum, maximum);
                y = Math.Clamp(y + steps, minimum, maximum);
                z = Math.Clamp(z + steps, minimum, maximum);
                changed = true;
            }
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("依次为 X、Y、Z；按住左侧标签拖动可三轴一起调整");
        return changed;
    }

    /// <summary>
    /// 未建立选择时以暗色锁定的折叠栏
    /// </summary>
    /// <param name="title">栏目标题</param>
    /// <param name="unlocked">是否已选中体素</param>
    /// <param name="drawContent">解锁并展开后绘制的内容</param>
    private static void DrawVoxelSelectionLockedSection(string title, bool unlocked, Action drawContent)
    {
        if (unlocked)
        {
            if (!ImGui.CollapsingHeader($"{title}##VoxelSelectionSection{title}")) return;
            ImGui.Indent(10F);
            drawContent();
            ImGui.Unindent(10F);
            ImGui.Dummy(new Vector2(0F, UiScale.Px(2F)));
            return;
        }

        // 锁定态：标题栏压暗并禁用交互，标题直接说明为什么点不动
        var dimText = Color.FromArgb(120, EditorPalette.MutedText.R, EditorPalette.MutedText.G, EditorPalette.MutedText.B);
        var dimHeader = Color.FromArgb(110, EditorPalette.PanelHeader.R, EditorPalette.PanelHeader.G, EditorPalette.PanelHeader.B);
        ImGui.PushStyleColor(ImGuiCol.Text, ToColor(dimText));
        ImGui.PushStyleColor(ImGuiCol.Header, ToColor(dimHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ToColor(dimHeader));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ToColor(dimHeader));
        ImGui.BeginDisabled();
        ImGui.CollapsingHeader($"{title}（需要先选择体素）##VoxelSelectionSection{title}");
        ImGui.EndDisabled();
        ImGui.PopStyleColor(4);
    }

    /// <summary>换色栏</summary>
    private void DrawVoxelSelectionRecolor(SceneObject sceneObject)
    {
        if (!_voxelSelectionPaintInitialized)
        {
            var data = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == sceneObject.Id)?.Components
                .FirstOrDefault(SceneComponentTypes.IsVoxelSelection) is { } component
                ? SceneComponentTypes.ReadVoxelSelection(component)
                : VoxelSelectionComponentData.Default;
            _voxelSelectionPaintColor = new Vector4(data.ColorR / 255F, data.ColorG / 255F, data.ColorB / 255F, data.ColorA / 255F);
            _voxelSelectionPaintInitialized = true;
        }

        BeginInspectorField("换成颜色", "选中体素统一换成这个颜色；透明度小于 255 会自动加上半透明标记。");
        ImGui.ColorEdit4("##VoxelSelectionPaintColor", ref _voxelSelectionPaintColor,
            ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar);
        ImGui.Checkbox("保留自发光标记", ref _voxelSelectionKeepEmissive);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭后换色的体素不再发光");
        ImGui.Checkbox("保留材质槽", ref _voxelSelectionKeepMaterial);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭后材质槽归零，改用全局材质");
        if (ImGui.Button("应用换色", new Vector2(-1F, 0F))) ApplyVoxelSelectionRecolor(sceneObject);
    }

    /// <summary>删除栏</summary>
    private void DrawVoxelSelectionDelete(SceneObject sceneObject, int selectedCount)
    {
        _ = sceneObject;
        ImGui.TextDisabled($"将移除 {selectedCount:N0} 格体素；撤销可以逐格还原。");
        ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.Error));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToColor(EditorPalette.Error));
        if (ImGui.Button("删除选中体素", new Vector2(-1F, 0F))) RemoveVoxelSelectionCells();
        ImGui.PopStyleColor(2);
    }

    /// <summary>移动栏</summary>
    private void DrawVoxelSelectionMove(SceneObject sceneObject)
    {
        var moveDrag = BeginInspectorField("位移（格）", "整体平移选中体素；目标位置会覆盖原有体素，原位留空。",
            draggable: true);
        DrawVoxelSelectionInt3("##VoxelSelectionMove", ref _voxelSelectionMoveX, ref _voxelSelectionMoveY,
            ref _voxelSelectionMoveZ, -4096, 4096, moveDrag);
        if (ImGui.Button("应用移动", new Vector2(-1F, 0F))) ApplyVoxelSelectionMove(sceneObject);
    }

    /// <summary>旋转栏</summary>
    private void DrawVoxelSelectionRotate(SceneObject sceneObject)
    {
        BeginInspectorField("旋转轴", "绕选中体素包围盒中心做 90° 整数旋转，格点不会错位。");
        ImGui.Combo("##VoxelSelectionRotateAxis", ref _voxelSelectionRotateAxis, "绕 X 轴\0绕 Y 轴\0绕 Z 轴\0");
        var half = MathF.Max(ImGui.GetFontSize() * 4F,
            (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * .5F);
        if (ImGui.Button("逆时针 90°", new Vector2(half, 0F))) ApplyVoxelSelectionRotate(sceneObject, quarterTurns: 3);
        ImGui.SameLine();
        if (ImGui.Button("顺时针 90°", new Vector2(half, 0F))) ApplyVoxelSelectionRotate(sceneObject, quarterTurns: 1);
        ImGui.TextDisabled("范围框不跟着旋转，需要时重新框选。");
    }

    /// <summary>缩放栏</summary>
    private void DrawVoxelSelectionScale(SceneObject sceneObject)
    {
        InspectorDragFloatField("倍率", "以选中体素的包围盒中心缩放。放大按最近邻复制方块，缩小按最近邻抽样，可能丢细节。", ref _voxelSelectionScale, .05F, .05F, 8F, "%.2f×");
        if (ImGui.Button("应用缩放", new Vector2(-1F, 0F))) ApplyVoxelSelectionScale(sceneObject);
    }

    /// <summary>
    /// 判断对象是否正处在框选调整模式
    /// </summary>
    /// <param name="sceneObject">待判断对象</param>
    /// <returns>是否处于框选模式</returns>
    private static bool IsVoxelSelectionEditing(SceneObject? sceneObject)
    {
        if (sceneObject is null) return false;
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        return component is not null && SceneComponentTypes.ReadVoxelSelection(component).Editing;
    }

    /// <summary>
    /// 扫描范围，把匹配目标颜色的体素记为当前选择
    /// </summary>
    /// <param name="sceneObject">选择所属对象</param>
    /// <param name="data">框选参数</param>
    private void SelectVoxelsInRange(SceneObject sceneObject, VoxelSelectionComponentData data)
    {
        if (!_objectVoxels.TryGetWorld(sceneObject.Id, out var world))
        {
            SetStatus("该对象没有可框选的体素数据");
            return;
        }

        if (data.Volume > VoxelSelectionMaxVolume)
        {
            SetStatus($"框选范围 {data.SizeX}×{data.SizeY}×{data.SizeZ} 过大，请缩小范围后重试");
            return;
        }

        var origin = GetVoxelSelectionOrigin(sceneObject, data);
        var cells = new List<VoxelCoord64>();
        for (var x = 0; x < data.SizeX; x++)
        for (var y = 0; y < data.SizeY; y++)
        for (var z = 0; z < data.SizeZ; z++)
        {
            var local = new VoxelCoord64(data.StartX + x, data.StartY + y, data.StartZ + z);
            if (!world.TryGet(local + origin, out var value)) continue;
            if (!MatchesVoxelSelection(value, data)) continue;
            // 存局部坐标：对象移动后范围与选择仍然对准同一批体素
            cells.Add(local);
        }

        _voxelSelectionObjectId = sceneObject.Id;
        _voxelSelectionCells = cells.ToArray();
        _voxelSelectionSignature = VoxelSelectionSignature(sceneObject, data);
        _selectedVoxel = null;
        _voxelSelectionPaintInitialized = false;
        SetStatus(cells.Count == 0
            ? "范围内没有匹配该颜色的体素；可以调大颜色容差，或检查起点与长宽高"
            : $"已选中 {cells.Count:N0} 格体素，可在组件下方换色、删除、移动、旋转或缩放");
    }

    /// <summary>
    /// 判断体素是否匹配框选条件
    /// </summary>
    /// <param name="value">体素值</param>
    /// <param name="data">框选参数</param>
    /// <returns>是否匹配</returns>
    private static bool MatchesVoxelSelection(VoxelValue value, VoxelSelectionComponentData data)
    {
        if (data.EmissiveOnly && (value.Flags & VoxelFlags.Emissive) == 0) return false;
        var tolerance = data.ColorTolerance;
        return Math.Abs(value.Color.R - data.ColorR) <= tolerance &&
               Math.Abs(value.Color.G - data.ColorG) <= tolerance &&
               Math.Abs(value.Color.B - data.ColorB) <= tolerance &&
               Math.Abs(value.Color.A - data.ColorA) <= tolerance;
    }

    /// <summary>
    /// 选择指纹：只包含对象与框选条件，体素内容变化不参与，避免换色后立刻显示“范围已改变”
    /// </summary>
    private static string VoxelSelectionSignature(SceneObject sceneObject, VoxelSelectionComponentData data) =>
        $"{sceneObject.Id}|{data.OriginX},{data.OriginY},{data.OriginZ}|" +
        $"{data.StartX},{data.StartY},{data.StartZ}|{data.SizeX},{data.SizeY},{data.SizeZ}|" +
        $"{data.ColorR},{data.ColorG},{data.ColorB},{data.ColorA}|{data.ColorTolerance:R}|{data.EmissiveOnly}";

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0F, 1F) * 255F), 0, 255);

    /// <summary>取当前选择所属对象</summary>
    private SceneObject? GetVoxelSelectionTarget() =>
        _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _voxelSelectionObjectId);

    /// <summary>
    /// 对象的格原点：格对齐对象取变换位置，自由对象取位置向下取整
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <returns>对象的格原点</returns>
    private static VoxelCoord64 GetObjectGridOrigin(SceneObject sceneObject) => sceneObject.Transform.VoxelOrigin;

    /// <summary>
    /// 取框选范围的世界原点：对象原点 + 组件局部原点
    /// </summary>
    /// <param name="sceneObject">框选所属对象</param>
    /// <param name="data">框选参数</param>
    /// <returns>局部坐标 (0,0,0) 对应的世界体素坐标</returns>
    private static VoxelCoord64 GetVoxelSelectionOrigin(SceneObject sceneObject, VoxelSelectionComponentData data) =>
        GetObjectGridOrigin(sceneObject) + data.Origin;

    /// <summary>
    /// 旧版组件把世界坐标直接写进起点，这里改成相对对象原点的局部坐标
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">框选组件记录</param>
    private static void MigrateVoxelSelectionToLocal(SceneObject sceneObject, ComponentRecord component)
    {
        if (component.SchemaVersion >= SceneComponentTypes.VoxelSelectionSchemaVersion) return;
        var data = SceneComponentTypes.ReadVoxelSelection(component);
        var objectOrigin = GetObjectGridOrigin(sceneObject);
        // 让 对象原点 + 局部原点 + 旧起点 仍然等于旧的世界起点：局部原点取对象原点的相反数
        SceneComponentTypes.WriteVoxelSelection(component, data with
        {
            OriginX = (int)Math.Clamp(-objectOrigin.X, -10_000_000, 10_000_000),
            OriginY = (int)Math.Clamp(-objectOrigin.Y, -10_000_000, 10_000_000),
            OriginZ = (int)Math.Clamp(-objectOrigin.Z, -10_000_000, 10_000_000),
        });
    }

    /// <summary>
    /// 把当前选择的局部坐标换算成体素世界坐标
    /// </summary>
    /// <param name="origin">局部原点</param>
    /// <returns>世界坐标数组</returns>
    private VoxelCoord64[] ToWorldCells(VoxelCoord64 origin)
    {
        var cells = new VoxelCoord64[_voxelSelectionCells.Length];
        for (var index = 0; index < cells.Length; index++) cells[index] = _voxelSelectionCells[index] + origin;
        return cells;
    }

    /// <summary>
    /// 把当前选择里仍然存在的体素读成“局部坐标到值”的映射
    /// </summary>
    /// <param name="world">对象的体素世界</param>
    /// <param name="origin">局部原点</param>
    private Dictionary<VoxelCoord64, VoxelValue> ReadSelectedValues(VoxelWorld world, VoxelCoord64 origin)
    {
        var values = new Dictionary<VoxelCoord64, VoxelValue>(_voxelSelectionCells.Length);
        foreach (var cell in _voxelSelectionCells)
            if (world.TryGet(cell + origin, out var value))
                values[cell] = value;
        return values;
    }

    /// <summary>
    /// 把一批体素改动提交为一次可撤销事务
    /// </summary>
    /// <param name="target">目标对象</param>
    /// <param name="name">命令显示名</param>
    /// <param name="changes">局部坐标与目标值，空值表示删除</param>
    /// <returns>是否真的改动了体素</returns>
    private bool CommitVoxelSelectionChanges(SceneObject target, string name,
        IEnumerable<(VoxelCoord64 Coordinate, VoxelValue Value)> changes)
    {
        if (_project is null)
        {
            SetStatus("请先新建或打开项目");
            return false;
        }

        if (!target.Components.Any(SceneComponentTypes.IsVoxelModel))
        {
            SetStatus("该对象没有模型组件，无法修改体素");
            return false;
        }

        // 与笔刷一致：在非基准尺寸上编辑前，先把当前形态写成新的 1x 基准
        BakeScaleForEditing(target);
        if (!_objectVoxels.TryGetWorld(target.Id, out var world))
        {
            SetStatus("找不到对象的体素数据");
            return false;
        }

        // 组件范围是局部坐标，写入体素世界前统一加上局部原点
        var selectionComponent = target.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        var origin = selectionComponent is null
            ? VoxelCoord64.Zero
            : GetVoxelSelectionOrigin(target, SceneComponentTypes.ReadVoxelSelection(selectionComponent));
        var worldChanges = changes.Select(pair => (pair.Coordinate + origin, pair.Value));
        var command = new VoxelStrokeCommand(name, world, worldChanges, ObjectEditCallback(target.Id));
        if (command.ChangedCellCount == 0)
        {
            SetStatus("选中体素没有发生变化，撤销历史保持不变");
            return false;
        }

        _voxelHistory.Execute(command);
        _pipeline?.ResetAccumulation();
        MarkSceneDocumentDirty();
        RecordVoxelAnimationFrame(target, command.ChangedCellCount);
        SetStatus($"{name} · {target.Name} · {command.ChangedCellCount:N0} 格（可撤销）");
        return true;
    }

    /// <summary>换色：按选定颜色覆写选中体素，可保留发光标记与材质槽</summary>
    private void ApplyVoxelSelectionRecolor(SceneObject sceneObject)
    {
        var target = GetVoxelSelectionTarget();
        if (target is null || target.Id != sceneObject.Id)
        {
            SetStatus("当前选择不属于此对象，请重新选择");
            return;
        }

        if (!_objectVoxels.TryGetWorld(target.Id, out var world)) return;
        var selectionComponent = target.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        var origin = selectionComponent is null
            ? VoxelCoord64.Zero
            : GetVoxelSelectionOrigin(target, SceneComponentTypes.ReadVoxelSelection(selectionComponent));
        var red = ToByte(_voxelSelectionPaintColor.X);
        var green = ToByte(_voxelSelectionPaintColor.Y);
        var blue = ToByte(_voxelSelectionPaintColor.Z);
        var alpha = ToByte(_voxelSelectionPaintColor.W);
        var changes = new List<(VoxelCoord64 Coordinate, VoxelValue Value)>(_voxelSelectionCells.Length);
        foreach (var cell in _voxelSelectionCells)
        {
            if (!world.TryGet(cell + origin, out var value)) continue;
            var flags = _voxelSelectionKeepEmissive
                ? value.Flags
                : (byte)(value.Flags & ~VoxelFlags.Emissive);
            flags = alpha < byte.MaxValue
                ? (byte)(flags | VoxelFlags.Translucent)
                : (byte)(flags & ~VoxelFlags.Translucent);
            changes.Add((cell, new VoxelValue(new Rgba32(red, green, blue, alpha),
                _voxelSelectionKeepMaterial ? value.MaterialSlot : (ushort)0, flags)));
        }

        CommitVoxelSelectionChanges(target, "框选换色", changes);
    }

    /// <summary>删除选中的体素</summary>
    private void RemoveVoxelSelectionCells()
    {
        var target = GetVoxelSelectionTarget();
        if (target is null || _voxelSelectionCells.Length == 0) return;
        if (!CommitVoxelSelectionChanges(target, "删除选中体素",
                _voxelSelectionCells.Select(cell => (cell, default(VoxelValue))))) return;
        _voxelSelectionCells = [];
        _voxelSelectionSignature = string.Empty;
    }

    /// <summary>
    /// 让组件的范围重新贴合当前选中的体素（旋转、缩放后轮廓会变）
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private void SyncVoxelSelectionRangeToCells(SceneObject sceneObject)
    {
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        if (component is null || _voxelSelectionCells.Length == 0) return;
        var (minimum, maximum) = GetVoxelSelectionBounds();
        var data = SceneComponentTypes.ReadVoxelSelection(component);
        data = data with
        {
            StartX = (int)Math.Clamp(minimum.X, -10_000_000, 10_000_000),
            StartY = (int)Math.Clamp(minimum.Y, -10_000_000, 10_000_000),
            StartZ = (int)Math.Clamp(minimum.Z, -10_000_000, 10_000_000),
            SizeX = (int)Math.Clamp(maximum.X - minimum.X + 1, 1, 4096),
            SizeY = (int)Math.Clamp(maximum.Y - minimum.Y + 1, 1, 4096),
            SizeZ = (int)Math.Clamp(maximum.Z - minimum.Z + 1, 1, 4096),
        };
        SceneComponentTypes.WriteVoxelSelection(component, data);
        // 范围是我们自己跟着选择改的，不该提示“范围已改变”
        _voxelSelectionSignature = VoxelSelectionSignature(sceneObject, data);
    }

    /// <summary>整体平移选中的体素</summary>
    private void ApplyVoxelSelectionMove(SceneObject sceneObject)
    {
        var target = GetVoxelSelectionTarget();
        if (target is null || target.Id != sceneObject.Id)
        {
            SetStatus("当前选择不属于此对象，请重新选择");
            return;
        }

        var delta = new VoxelCoord64(_voxelSelectionMoveX, _voxelSelectionMoveY, _voxelSelectionMoveZ);
        if (delta == VoxelCoord64.Zero)
        {
            SetStatus("位移为 0，没有需要移动的体素");
            return;
        }

        if (!_objectVoxels.TryGetWorld(target.Id, out var world)) return;
        var origin = GetVoxelSelectionOrigin(target, ReadVoxelSelectionData(target));
        var values = ReadSelectedValues(world, origin);
        if (values.Count == 0)
        {
            SetStatus("选中体素已不存在，请重新选择");
            return;
        }

        var changes = new Dictionary<VoxelCoord64, VoxelValue>(values.Count * 2);
        foreach (var cell in values.Keys) changes[cell] = default;
        foreach (var pair in values) changes[pair.Key + delta] = pair.Value;
        if (!CommitVoxelSelectionChanges(target, "移动选中体素",
                changes.Select(pair => (pair.Key, pair.Value)))) return;
        // 局部坐标与世界坐标的平移量相同，范围框跟着选中的体素一起走
        _voxelSelectionCells = _voxelSelectionCells.Select(cell => cell + delta).ToArray();
        ShiftVoxelSelectionRange(target, delta);
        _voxelSelectionMoveX = _voxelSelectionMoveY = _voxelSelectionMoveZ = 0;
    }

    /// <summary>
    /// 读取对象上的框选参数，没有组件时返回默认值
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    private static VoxelSelectionComponentData ReadVoxelSelectionData(SceneObject sceneObject) =>
        sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection) is { } component
            ? SceneComponentTypes.ReadVoxelSelection(component)
            : VoxelSelectionComponentData.Default;

    /// <summary>
    /// 按世界位移平移组件的局部范围，让范围框跟着移动过的体素
    /// </summary>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="delta">世界格位移</param>
    private void ShiftVoxelSelectionRange(SceneObject sceneObject, VoxelCoord64 delta)
    {
        var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        if (component is null) return;
        var data = SceneComponentTypes.ReadVoxelSelection(component);
        data = data with
        {
            StartX = data.StartX + (int)delta.X,
            StartY = data.StartY + (int)delta.Y,
            StartZ = data.StartZ + (int)delta.Z,
        };
        SceneComponentTypes.WriteVoxelSelection(component, data);
        _voxelSelectionSignature = VoxelSelectionSignature(sceneObject, data);
    }

    /// <summary>绕选中体素包围盒中心旋转 90°</summary>
    /// <param name="sceneObject">选择所属对象</param>
    /// <param name="quarterTurns">旋转几个 90°，1 表示正向一次</param>
    private void ApplyVoxelSelectionRotate(SceneObject sceneObject, int quarterTurns)
    {
        var target = GetVoxelSelectionTarget();
        if (target is null || target.Id != sceneObject.Id)
        {
            SetStatus("当前选择不属于此对象，请重新选择");
            return;
        }

        if (!_objectVoxels.TryGetWorld(target.Id, out var world)) return;
        var values = ReadSelectedValues(world, GetVoxelSelectionOrigin(target, ReadVoxelSelectionData(target)));
        if (values.Count == 0)
        {
            SetStatus("选中体素已不存在，请重新选择");
            return;
        }

        var (minimum, maximum) = GetVoxelSelectionBounds();
        // 用格边界坐标的中点做轴心：整数或半整数，旋转 90° 后仍然落在格点上
        var pivotX = (minimum.X + maximum.X + 1) * .5;
        var pivotY = (minimum.Y + maximum.Y + 1) * .5;
        var pivotZ = (minimum.Z + maximum.Z + 1) * .5;
        var axis = Math.Clamp(_voxelSelectionRotateAxis, 0, 2);
        var changes = new Dictionary<VoxelCoord64, VoxelValue>(values.Count * 2);
        foreach (var cell in values.Keys) changes[cell] = default;
        var destinations = new List<VoxelCoord64>(values.Count);
        foreach (var pair in values)
        {
            var destination = RotateVoxelCell(pair.Key, axis, quarterTurns, pivotX, pivotY, pivotZ);
            changes[destination] = pair.Value;
            destinations.Add(destination);
        }

        if (!CommitVoxelSelectionChanges(target, "旋转选中体素",
                changes.Select(pair => (pair.Key, pair.Value)))) return;
        _voxelSelectionCells = destinations.Distinct().ToArray();
        SyncVoxelSelectionRangeToCells(target);
    }

    /// <summary>
    /// 以格中心为基准把一格绕轴旋转 90°，结果仍落在整数格点上
    /// </summary>
    private static VoxelCoord64 RotateVoxelCell(VoxelCoord64 cell, int axis, int quarterTurns,
        double pivotX, double pivotY, double pivotZ)
    {
        var x = cell.X + .5;
        var y = cell.Y + .5;
        var z = cell.Z + .5;
        for (var turn = 0; turn < quarterTurns; turn++)
        {
            switch (axis)
            {
                case 0:
                {
                    var rotatedY = pivotY - (z - pivotZ);
                    var rotatedZ = pivotZ + (y - pivotY);
                    y = rotatedY;
                    z = rotatedZ;
                    break;
                }
                case 1:
                {
                    var rotatedX = pivotX + (z - pivotZ);
                    var rotatedZ = pivotZ - (x - pivotX);
                    x = rotatedX;
                    z = rotatedZ;
                    break;
                }
                default:
                {
                    var rotatedX = pivotX - (y - pivotY);
                    var rotatedY = pivotY + (x - pivotX);
                    x = rotatedX;
                    y = rotatedY;
                    break;
                }
            }
        }

        return new VoxelCoord64(
            (long)Math.Round(x - .5, MidpointRounding.AwayFromZero),
            (long)Math.Round(y - .5, MidpointRounding.AwayFromZero),
            (long)Math.Round(z - .5, MidpointRounding.AwayFromZero));
    }

    /// <summary>以选中体素包围盒中心为轴心缩放</summary>
    private void ApplyVoxelSelectionScale(SceneObject sceneObject)
    {
        var target = GetVoxelSelectionTarget();
        if (target is null || target.Id != sceneObject.Id)
        {
            SetStatus("当前选择不属于此对象，请重新选择");
            return;
        }

        var factor = Math.Clamp(_voxelSelectionScale, .05F, 8F);
        if (MathF.Abs(factor - 1F) < .001F)
        {
            SetStatus("倍率为 1，没有变化");
            return;
        }

        if (!_objectVoxels.TryGetWorld(target.Id, out var world)) return;
        var values = ReadSelectedValues(world, GetVoxelSelectionOrigin(target, ReadVoxelSelectionData(target)));
        if (values.Count == 0)
        {
            SetStatus("选中体素已不存在，请重新选择");
            return;
        }

        var (minimum, maximum) = GetVoxelSelectionBounds();
        var sizeX = (int)(maximum.X - minimum.X + 1);
        var sizeY = (int)(maximum.Y - minimum.Y + 1);
        var sizeZ = (int)(maximum.Z - minimum.Z + 1);
        var newSizeX = Math.Max(1, (int)Math.Round(sizeX * (double)factor));
        var newSizeY = Math.Max(1, (int)Math.Round(sizeY * (double)factor));
        var newSizeZ = Math.Max(1, (int)Math.Round(sizeZ * (double)factor));
        if ((long)newSizeX * newSizeY * newSizeZ > VoxelSelectionMaxVolume)
        {
            SetStatus("缩放结果超过单次处理上限，请降低倍率");
            return;
        }

        var centerX = minimum.X + sizeX * .5;
        var centerY = minimum.Y + sizeY * .5;
        var centerZ = minimum.Z + sizeZ * .5;
        var startX = (int)Math.Round(centerX - newSizeX * .5);
        var startY = (int)Math.Round(centerY - newSizeY * .5);
        var startZ = (int)Math.Round(centerZ - newSizeZ * .5);

        var changes = new List<(VoxelCoord64 Coordinate, VoxelValue Value)>(values.Count * 2);
        // 先清空原位置，再按最近邻把结果写进新位置；同一坐标后写覆盖先写
        foreach (var cell in values.Keys) changes.Add((cell, default));
        var destinations = new List<VoxelCoord64>();
        for (var x = 0; x < newSizeX; x++)
        for (var y = 0; y < newSizeY; y++)
        for (var z = 0; z < newSizeZ; z++)
        {
            var sourceX = minimum.X + Math.Clamp((int)((x + .5) / newSizeX * sizeX), 0, sizeX - 1);
            var sourceY = minimum.Y + Math.Clamp((int)((y + .5) / newSizeY * sizeY), 0, sizeY - 1);
            var sourceZ = minimum.Z + Math.Clamp((int)((z + .5) / newSizeZ * sizeZ), 0, sizeZ - 1);
            if (!values.TryGetValue(new VoxelCoord64(sourceX, sourceY, sourceZ), out var value)) continue;
            var destination = new VoxelCoord64(startX + x, startY + y, startZ + z);
            changes.Add((destination, value));
            destinations.Add(destination);
        }

        if (destinations.Count == 0)
        {
            SetStatus("缩放后没有留下体素，请换一个倍率");
            return;
        }

        if (!CommitVoxelSelectionChanges(target, "缩放选中体素", changes)) return;
        _voxelSelectionCells = destinations.ToArray();
        SyncVoxelSelectionRangeToCells(target);
    }

    /// <summary>
    /// 当前选中体素的包围盒，两个角都是包含在内的格坐标
    /// </summary>
    private (VoxelCoord64 Minimum, VoxelCoord64 Maximum) GetVoxelSelectionBounds()
    {
        if (_voxelSelectionCells.Length == 0) return (VoxelCoord64.Zero, VoxelCoord64.Zero);
        long minX = long.MaxValue, minY = long.MaxValue, minZ = long.MaxValue;
        long maxX = long.MinValue, maxY = long.MinValue, maxZ = long.MinValue;
        foreach (var cell in _voxelSelectionCells)
        {
            minX = Math.Min(minX, cell.X);
            minY = Math.Min(minY, cell.Y);
            minZ = Math.Min(minZ, cell.Z);
            maxX = Math.Max(maxX, cell.X);
            maxY = Math.Max(maxY, cell.Y);
            maxZ = Math.Max(maxZ, cell.Z);
        }

        return (new VoxelCoord64(minX, minY, minZ), new VoxelCoord64(maxX, maxY, maxZ));
    }

    /// <summary>
    /// 在场景视图里画框选范围与已选体素
    /// </summary>
    /// <param name="drawList">绘制列表</param>
    /// <param name="origin">视口原点</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">场景相机</param>
    /// <param name="selected">当前选中对象</param>
    /// <param name="data">框选参数</param>
    private void DrawVoxelSelectionGizmo(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject selected, VoxelSelectionComponentData data)
    {
        // 组件范围是局部坐标，投影前先换算到体素世界
        var objectOrigin = GetObjectGridOrigin(selected);
        var worldMinimum = data.WorldMinimum(objectOrigin);
        var localOrigin = objectOrigin + data.Origin;
        var minimum = new Vector3(worldMinimum.X, worldMinimum.Y, worldMinimum.Z);
        var maximum = minimum + new Vector3(data.SizeX, data.SizeY, data.SizeZ);
        var center = (minimum + maximum) * .5F;
        var editing = data.Editing;
        var color = ToPackedColor(editing ? EditorPalette.Accent : EditorPalette.Icon);

        Span<Vector3> corners = stackalloc Vector3[8];
        Span<Vector2> projected = stackalloc Vector2[8];
        Span<float> depths = stackalloc float[8];
        var visible = 0;
        for (var index = 0; index < 8; index++)
        {
            corners[index] = new Vector3(
                (index & 1) == 0 ? minimum.X : maximum.X,
                (index & 2) == 0 ? minimum.Y : maximum.Y,
                (index & 4) == 0 ? minimum.Z : maximum.Z);
            if (!SceneProjection.Project(corners[index], origin, size, camera, out projected[index], out depths[index]))
                continue;
            visible++;
        }

        // 八个角都能投影才画，避免相机进到盒子里时出现乱线
        if (visible == 8)
        {
            var nearDepth = depths[0];
            var farDepth = depths[0];
            for (var index = 1; index < 8; index++)
            {
                nearDepth = MathF.Min(nearDepth, depths[index]);
                farDepth = MathF.Max(farDepth, depths[index]);
            }

            var span = MathF.Max(.001F, farDepth - nearDepth);
            for (var axis = 0; axis < 3; axis++)
            {
                var step = 1 << axis;
                for (var index = 0; index < 8; index++)
                {
                    if ((index & step) != 0) continue;
                    var other = index | step;
                    var alpha = Math.Clamp(1F - ((depths[index] + depths[other]) * .5F - nearDepth) / span * .6F, .35F, 1F);
                    drawList.AddLine(projected[index], projected[other], AlphaColor(color, alpha), editing ? 1.6F : 1.1F);
                }
            }

            if (editing)
            {
                for (var axis = 0; axis < 3; axis++)
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var handle = center + (axis switch
                    {
                        0 => new Vector3((maximum.X - minimum.X) * .5F * sign, 0F, 0F),
                        1 => new Vector3(0F, (maximum.Y - minimum.Y) * .5F * sign, 0F),
                        _ => new Vector3(0F, 0F, (maximum.Z - minimum.Z) * .5F * sign),
                    });
                    if (!SceneProjection.Project(handle, origin, size, camera, out var handleScreen, out _)) continue;
                    drawList.AddRectFilled(handleScreen - new Vector2(4F), handleScreen + new Vector2(4F), color, 1F);
                    drawList.AddRect(handleScreen - new Vector2(4F), handleScreen + new Vector2(4F),
                        AlphaColor(color, .95F), 1F, ImDrawFlags.None, 1.2F);
                }

                if (SceneProjection.Project(center, origin, size, camera, out var centerScreen, out _))
                {
                    drawList.AddCircleFilled(centerScreen, 5F, color);
                    drawList.AddCircle(centerScreen, 5F, AlphaColor(color, .95F), 16, 1.4F);
                }
            }
        }

        // 已选体素逐格点出中心色块，上限之外只保留框与计数
        var selectedCount = _voxelSelectionObjectId == selected.Id ? _voxelSelectionCells.Length : 0;
        if (selectedCount > 0 && selectedCount <= VoxelSelectionMaxHighlight &&
            _objectVoxels.TryGetWorld(selected.Id, out var world))
        {
            foreach (var cell in _voxelSelectionCells)
            {
                var point = new Vector3(localOrigin.X + cell.X + .5F, localOrigin.Y + cell.Y + .5F,
                    localOrigin.Z + cell.Z + .5F);
                if (!SceneProjection.Project(point, origin, size, camera, out var screen, out _)) continue;
                var voxelColor = world.TryGet(cell + localOrigin, out var value) ? value.Color : Rgba32.OpaqueWhite;
                drawList.AddRectFilled(screen - new Vector2(1.5F), screen + new Vector2(1.5F),
                    ToPackedColor(Color.FromArgb(170, voxelColor.R, voxelColor.G, voxelColor.B)));
            }
        }

        if (SceneProjection.Project(center, origin, size, camera, out var labelScreen, out _))
        {
            var label = editing
                ? $"框选范围 {data.SizeX}×{data.SizeY}×{data.SizeZ}（调整中）"
                : $"框选范围 {data.SizeX}×{data.SizeY}×{data.SizeZ}";
            if (selectedCount > 0) label += $" · 已选 {selectedCount:N0} 格";
            drawList.AddText(labelScreen + new Vector2(10F, 14F), AlphaColor(color, .95F), label);
        }
    }

    /// <summary>
    /// 命中框选盒的中心或六面手柄时开始拖动
    /// </summary>
    /// <returns>是否开始拖动</returns>
    private bool TryBeginVoxelSelectionGizmoDrag(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        SceneObject selected)
    {
        var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        if (component is null) return false;
        MigrateVoxelSelectionToLocal(selected, component);
        var data = SceneComponentTypes.ReadVoxelSelection(component);
        if (!data.Editing) return false;
        var worldMinimum = data.WorldMinimum(GetObjectGridOrigin(selected));
        var minimum = new Vector3(worldMinimum.X, worldMinimum.Y, worldMinimum.Z);
        var maximum = minimum + new Vector3(data.SizeX, data.SizeY, data.SizeZ);
        var center = (minimum + maximum) * .5F;
        // 命中顺序：中心手柄优先，其次六个面手柄
        if (SceneProjection.Project(center, origin, size, camera, out var centerScreen, out var centerDepth) &&
            Vector2.Distance(mouse, centerScreen) < 10F)
        {
            BeginVoxelSelectionGizmoDrag(mouse, data, -1, 1, centerDepth, size, camera);
            return true;
        }

        for (var axis = 0; axis < 3; axis++)
        for (var sign = -1; sign <= 1; sign += 2)
        {
            var handle = center + (axis switch
            {
                0 => new Vector3((maximum.X - minimum.X) * .5F * sign, 0F, 0F),
                1 => new Vector3(0F, (maximum.Y - minimum.Y) * .5F * sign, 0F),
                _ => new Vector3(0F, 0F, (maximum.Z - minimum.Z) * .5F * sign),
            });
            if (!SceneProjection.Project(handle, origin, size, camera, out var handleScreen, out var handleDepth)) continue;
            if (Vector2.Distance(mouse, handleScreen) >= 9F) continue;
            BeginVoxelSelectionGizmoDrag(mouse, data, axis, sign, handleDepth, size, camera);
            return true;
        }

        return false;
    }

    /// <summary>记录拖动起点</summary>
    private void BeginVoxelSelectionGizmoDrag(Vector2 mouse, VoxelSelectionComponentData data, int axis, int faceSign,
        float depth, Vector2 size, SceneCamera camera)
    {
        _voxelSelectionGizmoDragging = true;
        _sceneGizmoDragging = true;
        _voxelSelectionGizmoAxis = axis;
        _voxelSelectionGizmoFaceSign = faceSign;
        _sceneGizmoDragStartMouse = mouse;
        _voxelSelectionGizmoStartMinimum = data.Minimum;
        _voxelSelectionGizmoStartSize = data.Size;
        _voxelSelectionGizmoPixelsPerUnit =
            MathF.Max(.001F, SceneProjection.FocalLength(size, camera) / MathF.Max(1F, depth));
    }

    /// <summary>
    /// 拖动中更新框选范围；返回 true 表示本帧已由框选拖动接管
    /// </summary>
    /// <returns>是否处理了拖动</returns>
    private bool UpdateVoxelSelectionGizmoDrag(Vector2 origin, Vector2 size, SceneCamera camera, SceneObject selected)
    {
        if (!_voxelSelectionGizmoDragging) return false;
        var component = selected.Components.FirstOrDefault(SceneComponentTypes.IsVoxelSelection);
        if (component is null)
        {
            _voxelSelectionGizmoDragging = false;
            return false;
        }

        var data = SceneComponentTypes.ReadVoxelSelection(component);
        var mouseDelta = ImGui.GetIO().MousePos - _sceneGizmoDragStartMouse;
        if (_voxelSelectionGizmoAxis < 0)
        {
            // 中心手柄：只在相机平面内平移，长宽高不变
            var shiftX = mouseDelta.X / _voxelSelectionGizmoPixelsPerUnit;
            var shiftY = -mouseDelta.Y / _voxelSelectionGizmoPixelsPerUnit;
            var shift = camera.Right * shiftX + camera.Up * shiftY;
            data = data with
            {
                StartX = (int)(_voxelSelectionGizmoStartMinimum.X + (int)MathF.Round(shift.X)),
                StartY = (int)(_voxelSelectionGizmoStartMinimum.Y + (int)MathF.Round(shift.Y)),
                StartZ = (int)(_voxelSelectionGizmoStartMinimum.Z + (int)MathF.Round(shift.Z)),
            };
        }
        else
        {
            var axis = _voxelSelectionGizmoAxis;
            var axisUnit = axis switch
            {
                0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ,
            };
            // 面手柄在体素世界里计算，再按局部原点偏移投影；位移量本身与原点无关
            var worldOrigin = GetVoxelSelectionOrigin(selected, data);
            var originOffset = new Vector3(worldOrigin.X, worldOrigin.Y, worldOrigin.Z);
            var startMinimum = _voxelSelectionGizmoStartMinimum;
            var startSize = _voxelSelectionGizmoStartSize;
            var startCenter = new Vector3(startMinimum.X + startSize.X * .5F,
                startMinimum.Y + startSize.Y * .5F, startMinimum.Z + startSize.Z * .5F) + originOffset;
            var startExtent = axis switch { 0 => startSize.X, 1 => startSize.Y, _ => startSize.Z } * .5F;
            var facePoint = startCenter + axisUnit * (startExtent * _voxelSelectionGizmoFaceSign);
            if (!SceneProjection.Project(facePoint, origin, size, camera, out var faceScreen, out _) ||
                !SceneProjection.Project(facePoint + axisUnit, origin, size, camera, out var faceNext, out _))
                return true;

            // 先求该轴一个世界单位在屏幕上的长度，再把手柄位移反投影成整格步数
            var axisScreen = faceNext - faceScreen;
            var pixelsPerUnit = axisScreen.Length();
            if (pixelsPerUnit < .001F) return true;
            var steps = (int)MathF.Round(Vector2.Dot(mouseDelta, axisScreen / pixelsPerUnit) / pixelsPerUnit);
            if (_voxelSelectionGizmoFaceSign > 0)
            {
                var newSize = Math.Max(1, AxisComponent(startSize, axis) + steps);
                data = WithSize(data, axis, newSize);
            }
            else
            {
                var startLength = AxisComponent(startSize, axis);
                var newSize = Math.Max(1, startLength - steps);
                var newStart = AxisComponent(startMinimum, axis) + (startLength - newSize);
                data = WithSize(WithStart(data, axis, newStart), axis, newSize);
            }
        }

        SceneComponentTypes.WriteVoxelSelection(component, data);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        return true;
    }

    private static int AxisComponent(VoxelCoord64 value, int axis) => axis switch
    {
        0 => (int)Math.Clamp(value.X, int.MinValue / 2, int.MaxValue / 2),
        1 => (int)Math.Clamp(value.Y, int.MinValue / 2, int.MaxValue / 2),
        _ => (int)Math.Clamp(value.Z, int.MinValue / 2, int.MaxValue / 2),
    };

    private static VoxelSelectionComponentData WithStart(VoxelSelectionComponentData data, int axis, int value) =>
        axis switch
        {
            0 => data with { StartX = value },
            1 => data with { StartY = value },
            _ => data with { StartZ = value },
        };

    private static VoxelSelectionComponentData WithSize(VoxelSelectionComponentData data, int axis, int value) =>
        axis switch
        {
            0 => data with { SizeX = value },
            1 => data with { SizeY = value },
            _ => data with { SizeZ = value },
        };
}
