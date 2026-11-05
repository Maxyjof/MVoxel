using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private object? _materialBrushScene;
    private StableId? _materialBrushTarget;
    private StableId? _materialDeleteTarget;
    private int _materialDeleteSlot;

    //画笔槽只属于当前模型，切换模型或场景后重新选择
    private void EnsureModelBrushMaterialTarget()
    {
        var target = _voxelEditTargetId ?? _selectedSceneObjectId;
        if (!ReferenceEquals(_materialBrushScene, _sceneDocument) || _materialBrushTarget != target)
        {
            _materialBrushScene = _sceneDocument;
            _materialBrushTarget = target;
            _brushMaterialSlot = 0;
        }
        if (_brushMaterialSlot == 0) return;
        var component = FindBrushModelComponent();
        var binding = component is null ? default : (SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
            .FirstOrDefault(item => item.Slot == _brushMaterialSlot);
        if (FindMaterialAsset(binding.MaterialAsset) is null) _brushMaterialSlot = 0;
    }

    private void SetModelBrushMaterialSlot(ushort slot)
    {
        EnsureModelBrushMaterialTarget();
        _brushMaterialSlot = slot;
        EnsureModelBrushMaterialTarget();
    }

    private ComponentRecord? FindBrushModelComponent()
    {
        var target = _voxelEditTargetId ?? _selectedSceneObjectId;
        return target is { } id ? _sceneDocument?.Objects.FirstOrDefault(item => item.Id == id)?.Components
            .FirstOrDefault(SceneComponentTypes.IsVoxelModel) : null;
    }

    private void DrawPaletteContentsCore()
    {
        EnsureModelBrushMaterialTarget();
        ImGui.SeparatorText("画笔颜色");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("选择新方块的基础颜色，不修改已有方块或材质资产参数。");
        var color = _sampledBrushColor ?? _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
        ImGui.ColorButton("当前画笔颜色", new Vector4(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F),
            ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight() * 2F, ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"新方块基础色：RGB({color.R}, {color.G}, {color.B})，Alpha {color.A}。透明效果由材质控制。");
        var label = _sampledBrushColor.HasValue ? "取样色" : $"色板 {_paletteIndex + 1}";
        ContinueMaterialControl(ImGui.CalcTextSize(label).X);
        ImGui.TextUnformatted(label);
        var sampling = _screenColorSampling;
        var sampleLabel = sampling ? "取消取色" : "取色";
        ContinueMaterialControl(MaterialButtonWidth(sampleLabel));
        if (ImGui.Button(sampleLabel))
        {
            _screenColorSampling = !sampling;
            _screenColorSamplingArmed = false;
            SetStatus(sampling ? "已取消取色" : "点击场景体素取原色，其他位置取显示颜色；Esc取消");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("场景体素取原色、材质和发光标记；其他位置只取显示颜色。材质槽只能在所属模型使用，按Esc取消。");
        DrawPaletteSelector();
        if (_project is not null)
        {
            if (ImGui.SmallButton("编辑项目色板")) OpenSettings(1, 3);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("编辑基础色板，不修改材质资产参数。");
        }
        ImGui.SeparatorText("画笔材质");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只对新方块生效。局部改材质只能擦除后重绘；材质参数在材质检查器编辑，独立效果请手动复制资产后绑定。");
        DrawBrushMaterialSelector();
        DrawBrushProperties();
    }

    private void DrawBrushMaterialSelectorCore()
    {
        EnsureModelBrushMaterialTarget();
        var component = FindBrushModelComponent();
        if (component is null)
        {
            ImGui.BeginDisabled();
            ImGui.Button("先选中模型组件", new Vector2(-1F, 0F));
            ImGui.EndDisabled();
            return;
        }
        var selected = FindCurrentBrushMaterial();
        var display = _brushMaterialSlot == 0 ? "槽0 · 默认材质" : $"槽{_brushMaterialSlot} · {selected?.Name}";
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##绘制材质", display))
        {
            if (ImGui.Selectable("槽0 · 默认材质", _brushMaterialSlot == 0)) SetModelBrushMaterialSlot(0);
            foreach (var binding in (SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? []).OrderBy(item => item.Slot))
            {
                var asset = FindMaterialAsset(binding.MaterialAsset);
                ImGui.PushID(binding.Slot);
                ImGui.BeginDisabled(asset is null);
                if (ImGui.Selectable($"槽{binding.Slot} · {asset?.Name ?? "资产缺失"}", _brushMaterialSlot == binding.Slot))
                    SetModelBrushMaterialSlot((ushort)binding.Slot);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(binding.MaterialAsset);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("本模型的本地槽位，仅用于新方块；局部改材质请擦除后重绘。切换模型后画笔恢复槽0。");
        selected = FindCurrentBrushMaterial();
        if (selected is not null && !IsEmbeddedStageMaterial(selected) && ImGui.Button("打开材质检查器")) OpenMaterialInspector(selected);
    }

    private void DrawModelMaterialSlotsCore(SceneObject modelObject)
    {
        if (!ImGui.CollapsingHeader("材质槽位", ImGuiTreeNodeFlags.SpanAvailWidth)) return;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("槽0固定默认材质，槽1至65535映射共享资产。替换引用影响本组件使用该编号的全部方块，不改体素编号。独立效果请手动复制资产后绑定。");
        var component = modelObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        var bindings = (data.MaterialSlots ?? []).ToDictionary(item => item.Slot);
        var usage = _objectVoxels.TryGetWorld(modelObject.Id, out var world)
            ? world.EnumerateOccupied().GroupBy(item => (int)item.Value.MaterialSlot).ToDictionary(group => group.Key, group => group.Count())
            : new Dictionary<int, int>();
        ImGui.PushID($"model-materials-{modelObject.Id}");
        ImGui.TextUnformatted("槽0 · 默认材质");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("固定默认槽，不能删除或绑定资产。");
        var defaultCount = $"{usage.GetValueOrDefault(0):N0}个方块";
        ContinueMaterialControl(ImGui.CalcTextSize(defaultCount).X);
        ImGui.TextDisabled(defaultCount);
        var requestDelete = false;
        var slots = usage.Keys.Concat(bindings.Keys).Where(slot => slot is > 0 and <= ushort.MaxValue).Distinct().Order().ToArray();
        foreach (var slot in slots)
        {
            var bound = bindings.TryGetValue(slot, out var binding);
            var material = FindMaterialAsset(binding.MaterialAsset);
            var count = usage.GetValueOrDefault(slot);
            ImGui.PushID(slot);
            ImGui.Separator();
            ImGui.TextUnformatted($"槽{slot}");
            var countLabel = $"{count:N0}个方块";
            ContinueMaterialControl(ImGui.CalcTextSize(countLabel).X);
            ImGui.TextDisabled(countLabel);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            var slotLabel = material is null && bound ? "资产缺失 · 诊断外观" : material?.Name ?? (bound ? "资产缺失" : "未绑定");
            if (ImGui.BeginCombo("##slot-asset", slotLabel))
            {
                DrawMaterialAssetChoices(candidate =>
                {
                    bindings[slot] = new MaterialSlotBinding(slot, RelativeMaterialAssetPath(candidate));
                    SaveModelMaterialBindings(modelObject, component, data, bindings);
                }, binding.MaterialAsset);
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(bound ? $"{binding.MaterialAsset}\n替换共享资产引用，不修改体素编号。" : "体素保留此编号，但组件没有绑定。可在此重新绑定，新增槽位不会复用该编号。");
            material = bindings.TryGetValue(slot, out var current) ? FindMaterialAsset(current.MaterialAsset) : null;
            if (material is not null)
            {
                if (!IsEmbeddedStageMaterial(material) && ImGui.SmallButton("打开材质检查器")) OpenMaterialInspector(material);
                ContinueMaterialControl(MaterialButtonWidth("删除槽位"));
            }
            ImGui.BeginDisabled(!bindings.ContainsKey(slot));
            if (ImGui.SmallButton("删除槽位"))
            {
                if (count == 0)
                {
                    bindings.Remove(slot);
                    SaveModelMaterialBindings(modelObject, component, data, bindings);
                }
                else
                {
                    _materialDeleteTarget = modelObject.Id;
                    _materialDeleteSlot = slot;
                    requestDelete = true;
                }
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(bound ? "只删除组件引用，体素编号保留并显示为未绑定。" : "已无组件绑定，编号仍被体素占用。局部修改请擦除后重绘。");
            ImGui.PopID();
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##add-material-slot", "添加材质槽位…"))
        {
            DrawMaterialAssetChoices(candidate =>
            {
                var slot = Enumerable.Range(1, ushort.MaxValue).FirstOrDefault(value => !bindings.ContainsKey(value) && !usage.ContainsKey(value));
                if (slot == 0) { SetStatus("此模型没有未绑定且未被体素占用的可用槽位"); return; }
                bindings[slot] = new MaterialSlotBinding(slot, RelativeMaterialAssetPath(candidate));
                SaveModelMaterialBindings(modelObject, component, data, bindings);
            }, null);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("选择现有资产添加槽位，不复用仍被体素占用的编号；新材质请先在资产面板创建。");
        if (requestDelete) ImGui.OpenPopup("删除材质槽位");
        DrawDeleteMaterialSlotConfirmation(modelObject, component, usage);
        ImGui.PopID();
    }

    private void DrawDeleteMaterialSlotConfirmation(SceneObject modelObject, ComponentRecord component, Dictionary<int, int> usage)
    {
        if (!CenteredModal.Begin("删除材质槽位", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (_materialDeleteTarget != modelObject.Id)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetFontSize() * 26F);
        ImGui.TextWrapped($"槽{_materialDeleteSlot}被{usage.GetValueOrDefault(_materialDeleteSlot):N0}个方块使用。继续删除只移除组件绑定，保留体素原编号，并在列表显示为未绑定。");
        ImGui.PopTextWrapPos();
        if (ImGui.Button("删除并保留编号"))
        {
            //确认时读取当前组件，避免覆盖同帧的其他槽位操作
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var bindings = (data.MaterialSlots ?? []).ToDictionary(item => item.Slot);
            if (bindings.Remove(_materialDeleteSlot)) SaveModelMaterialBindings(modelObject, component, data, bindings);
            _materialDeleteTarget = null;
            ImGui.CloseCurrentPopup();
        }
        ContinueMaterialControl(MaterialButtonWidth("取消"));
        if (ImGui.Button("取消"))
        {
            _materialDeleteTarget = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawMaterialAssetChoices(Action<MaterialAsset> choose, string? selectedPath)
    {
        if (_project is null || !_brushMaterials.Any())
        {
            ImGui.TextDisabled("请先在资产面板创建材质资产");
            return;
        }
        foreach (var asset in _brushMaterials)
        {
            var relative = RelativeMaterialAssetPath(asset);
            if (ImGui.Selectable($"{asset.Name}##{relative}", string.Equals(relative, selectedPath, StringComparison.OrdinalIgnoreCase))) choose(asset);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(relative);
        }
    }

    private string RelativeMaterialAssetPath(MaterialAsset material) =>
        Path.GetRelativePath(_project!.AssetsPath, material.SourcePath).Replace('\\', '/');

    private bool IsEmbeddedStageMaterial(MaterialAsset material) =>
        _voxelStage is not null && _voxelStage.Materials.Definitions.Values.Any(value => ReferenceEquals(value, material));

    private void OpenMaterialInspector(MaterialAsset material)
    {
        if (_project is null) return;
        _assetBrowser.SelectAsset(RelativeMaterialAssetPath(material));
        _showInspector = true;
        _requestedInspectorTab = 0;
    }

    private static float MaterialButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2F;

    //按实际字体和剩余空间换行，避免固定尺寸挤占面板
    private static void ContinueMaterialControl(float width)
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }
}
