using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Editor;

/// <summary>
/// 组件的编辑状态跟随对象焦点：任何组件（曲线渲染器、框选、探针体积、体素绘制）
/// 只在所属对象处于选中且由用户主动进入时才保持编辑态，切走或载入场景都会自动退出
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>由用户主动进入编辑状态的对象；文件里残留的编辑标记会被这里判为无效</summary>
    private StableId? _componentEditingOwnerId;

    /// <summary>用户最近一次主动选择的变换工具，选中对象时自动切回来</summary>
    private SceneGizmoMode _lastTransformMode = SceneGizmoMode.Move;

    /// <summary>上一帧看到的选中对象，用于判断“刚选中”与“刚取消”</summary>
    private StableId? _toolSelectionTracked;

    /// <summary>
    /// 曲线编辑中选中切线或朝向手柄时缩放没有意义，缩放工具应锁定
    /// </summary>
    private bool IsCurveScaleToolLocked()
    {
        var selected = GetSelectedSceneObject();
        if (selected is null || !IsCurveEditing(selected)) return false;
        return _curveActiveHandle != CurveHandleKind.Point;
    }

    /// <summary>
    /// 按 Unity 的规则让工具跟随选择：刚选中对象时切到上次的变换工具，
    /// 取消选中时回到选择工具
    /// </summary>
    private void SyncToolModeWithSelection()
    {
        // 层级或快捷键改了主选中对象时，多选列表跟着失效，避免检查器停在多选模式
        if (_selectedSceneObjectIds.Count > 1 &&
            (_selectedSceneObjectId is not { } primary || !_selectedSceneObjectIds.Contains(primary)))
            _selectedSceneObjectIds.Clear();
        var selected = _selectedSceneObjectId;
        if (selected == _toolSelectionTracked) return;
        var hadSelection = _toolSelectionTracked is not null;
        _toolSelectionTracked = selected;
        if (selected is null)
        {
            _sceneGizmoMode = SceneGizmoMode.Select;
            return;
        }

        if (!hadSelection) _sceneGizmoMode = _lastTransformMode;
    }

    private bool _componentEditingScanned;
    private StableId? _componentEditingScanSelection;
    private StableId? _componentEditingScanOwner;

    /// <summary>
    /// 打开场景后清掉文件里记录的组件编辑状态，避免带着“正在编辑”进入新会话
    /// </summary>
    private void ClearPersistedComponentEditingStates()
    {
        if (_sceneDocument is null) return;
        foreach (var sceneObject in _sceneDocument.Objects)
        foreach (var component in sceneObject.Components)
        {
            if (SceneComponentTypes.IsCurveRenderer(component))
            {
                var data = SceneComponentTypes.ReadCurveRenderer(component);
                if (data.Editing) SceneComponentTypes.WriteCurveRenderer(component, data with { Editing = false });
            }
            else if (SceneComponentTypes.IsVoxelSelection(component))
            {
                var data = SceneComponentTypes.ReadVoxelSelection(component);
                if (data.Editing) SceneComponentTypes.WriteVoxelSelection(component, data with { Editing = false });
            }
            else if (SceneComponentTypes.IsProbeVolume(component))
            {
                var data = SceneComponentTypes.ReadProbeVolume(component);
                if (data.Editing) SceneComponentTypes.WriteProbeVolume(component, data with { Editing = false });
            }
        }

        _componentEditingOwnerId = null;
        _componentEditingScanned = false;
        _componentEditingScanSelection = null;
        _componentEditingScanOwner = null;
    }

    /// <summary>记录用户主动进入编辑状态的对象</summary>
    /// <param name="id">对象标识</param>
    private void MarkComponentEditingOwner(StableId id) => _componentEditingOwnerId = id;

    /// <summary>
    /// 每帧校正组件编辑状态：焦点不在该对象、或编辑标记来自场景文件时一律退出
    /// </summary>
    private void SyncComponentEditingState()
    {
        if (_sceneDocument is null) return;
        var selected = _selectedSceneObjectId;
        // 只在选择或编辑归属变化时扫描一次，避免每帧反序列化全部组件数据
        if (_componentEditingScanned && _componentEditingScanSelection == selected &&
            _componentEditingScanOwner == _componentEditingOwnerId) return;
        _componentEditingScanned = true;
        _componentEditingScanSelection = selected;
        _componentEditingScanOwner = _componentEditingOwnerId;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            var focused = selected is { } current && sceneObject.Id == current &&
                          _componentEditingOwnerId == sceneObject.Id;
            if (focused) continue;
            foreach (var component in sceneObject.Components)
            {
                if (SceneComponentTypes.IsCurveRenderer(component))
                {
                    var data = SceneComponentTypes.ReadCurveRenderer(component);
                    if (data.Editing) SceneComponentTypes.WriteCurveRenderer(component, data with { Editing = false });
                }
                else if (SceneComponentTypes.IsVoxelSelection(component))
                {
                    var data = SceneComponentTypes.ReadVoxelSelection(component);
                    if (data.Editing) SceneComponentTypes.WriteVoxelSelection(component, data with { Editing = false });
                }
                else if (SceneComponentTypes.IsProbeVolume(component))
                {
                    var data = SceneComponentTypes.ReadProbeVolume(component);
                    if (data.Editing) SceneComponentTypes.WriteProbeVolume(component, data with { Editing = false });
                }
            }
        }

        // 绘制模式也绑定在对象上，焦点切走就回到选择工具
        if (_voxelEditTargetId is { } target && selected != target)
        {
            _voxelEditTargetId = null;
            if (_activeTool == EditorTool.Draw) _activeTool = EditorTool.Pick;
        }
    }
}
