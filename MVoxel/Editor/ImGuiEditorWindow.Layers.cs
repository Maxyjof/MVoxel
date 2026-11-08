using ImGuiNET;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void DrawSceneLayers()
    {
        if (_sceneDocument is { } layerDocument)
        {
            ImGui.TextUnformatted("场景窗口可见图层");
            ImGui.SetNextItemWidth(Math.Max(80F, ImGui.GetContentRegionAvail().X - 66F));
            ImGui.InputTextWithHint("##新建场景图层", "新图层名称", ref _newSceneLayerName, 64, ImGuiInputTextFlags.AutoSelectAll);
            ImGui.SameLine();
            if (ImGui.SmallButton("添加##新场景图层"))
            {
                var added = layerDocument.AddLayer(_newSceneLayerName);
                if (added >= 0)
                {
                    _newSceneLayerName = string.Empty;
                    MarkSceneDocumentDirty();
                    SetStatus($"已添加图层：{layerDocument.LayerNames[added]}");
                }
                else SetStatus("图层名称为空、重复或已达到 32 层上限");
            }

            var layerUsageCounts = layerDocument.LayerUsageCounts();
            for (var i = 0; i < layerDocument.LayerNames.Count; i++)
            {
                ImGui.PushID($"LayerRow{i}");
                var visible = layerDocument.IsLayerVisible(i);
                if (ImGui.Checkbox("##图层显隐", ref visible))
                {
                    layerDocument.SetLayerVisible(i, visible);
                    MarkSceneDocumentDirty();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{layerDocument.LayerNames[i]} ({layerUsageCounts[i]})");
                ImGui.SameLine();
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - 42F));
                var removeLayer = false;
                if (ImGui.SmallButton("...##图层操作")) ImGui.OpenPopup("##图层操作菜单");
                if (ImGui.BeginPopup("##图层操作菜单"))
                {
                    if (ImGui.MenuItem("重命名"))
                    {
                        _renamingSceneLayer = i;
                        _sceneLayerRenameBuffer = layerDocument.LayerNames[i];
                    }
                    if (i > 0)
                    {
                        if (ImGui.MenuItem("上移", null, false, i > 1) && layerDocument.MoveLayer(i, -1)) MarkSceneDocumentDirty();
                        if (ImGui.MenuItem("下移", null, false, i < layerDocument.LayerNames.Count - 1) && layerDocument.MoveLayer(i, 1)) MarkSceneDocumentDirty();
                        ImGui.Separator();
                        removeLayer = ImGui.MenuItem("删除图层");
                    }
                    ImGui.EndPopup();
                }

                if (_renamingSceneLayer == i)
                {
                    ImGui.SetNextItemWidth(Math.Max(90F, ImGui.GetContentRegionAvail().X - 95F));
                    ImGui.InputText("##图层新名称", ref _sceneLayerRenameBuffer, 64, ImGuiInputTextFlags.AutoSelectAll);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("确定##图层改名"))
                    {
                        if (layerDocument.RenameLayer(i, _sceneLayerRenameBuffer)) MarkSceneDocumentDirty();
                        else SetStatus("图层名称不能为空或与其他图层重复");
                        _renamingSceneLayer = -1;
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("取消##图层改名")) _renamingSceneLayer = -1;
                }
                if (removeLayer && layerDocument.RemoveLayer(i))
                {
                    MarkSceneDocumentDirty();
                }
                ImGui.PopID();
            }
            ImGui.Separator();
            if (ImGui.TreeNode("排序图层"))
            {
                ImGui.InputTextWithHint("##新增排序图层", "排序图层名称", ref _newSortingLayerName, 128, ImGuiInputTextFlags.AutoSelectAll);
                if (ImGui.Button("添加排序图层"))
                {
                    if (layerDocument.AddSortingLayer(_newSortingLayerName))
                    { _newSortingLayerName = ""; MarkSceneDocumentDirty(); }
                    else SetStatus("排序图层名称不能为空或重复");
                }
                for (var index = 0; index < layerDocument.SortingLayerNames.Count; index++)
                {
                    ImGui.PushID(index + 1000);
                    var name = layerDocument.SortingLayerNames[index];
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
                    if (ImGui.InputText("##排序名称", ref name, 128, ImGuiInputTextFlags.AutoSelectAll))
                    {
                        if (layerDocument.RenameSortingLayer(index, name)) MarkSceneDocumentDirty();
                        else SetStatus("默认排序图层不能改名，其他名称不能为空或重复");
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("上移") && layerDocument.MoveSortingLayer(index, -1)) MarkSceneDocumentDirty();
                    ImGui.SameLine();
                    if (ImGui.SmallButton("下移") && layerDocument.MoveSortingLayer(index, 1)) MarkSceneDocumentDirty();
                    ImGui.SameLine();
                    if (ImGui.SmallButton("删除") && layerDocument.RemoveSortingLayer(index)) MarkSceneDocumentDirty();
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }

        }

    }
}
