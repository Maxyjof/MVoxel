using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Color = System.Drawing.Color;

namespace MVoxel.Editor;

/// <summary>
/// 场景视图里的对象框选与多选状态。单击仍然走体素拾取，拖动超过阈值才进入框选。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>多选中的对象；顺序与层级一致，最后一个是主对象</summary>
    private readonly List<StableId> _selectedSceneObjectIds = new();

    private bool _objectBoxSelecting;
    private Vector2 _objectBoxStart;

    /// <summary>多选数量大于 1 时检查器走聚合模式</summary>
    private bool HasMultipleSelection => _selectedSceneObjectIds.Count > 1;

    /// <summary>对象是否在当前选择集合里（含主对象）</summary>
    private bool IsInSceneSelection(StableId id) =>
        _selectedSceneObjectId == id || _selectedSceneObjectIds.Contains(id);

    /// <summary>对象是否为当前主对象（Unity 里的 active selection）</summary>
    private bool IsPrimarySceneSelection(StableId id) => _selectedSceneObjectId == id;

    /// <summary>
    /// 用一组对象替换当前选择，主对象取最后一个
    /// </summary>
    /// <param name="ids">目标对象集合</param>
    private void SetSceneSelection(IReadOnlyList<StableId> ids)
    {
        _selectedSceneObjectIds.Clear();
        _selectedSceneObjectIds.AddRange(ids);
        _selectedSceneObjectId = ids.Count > 0 ? ids[^1] : null;
        _revealHierarchySelection = _selectedSceneObjectId;
        if (_selectedSceneObjectIds.Count <= 1) _selectedSceneObjectIds.Clear();
        // 选择集合变了，混合值输入草稿全部失效（对应 Unity 的 SetIsDifferentCacheDirty）
        _multiMixedText.Clear();
        _multiSelectionCacheKey = string.Empty;
    }

    /// <summary>
    /// 记录单击选中：清空多选，只保留一个对象
    /// </summary>
    /// <param name="id">选中的对象</param>
    private void SetSingleSceneSelection(StableId id)
    {
        _selectedSceneObjectIds.Clear();
        _selectedSceneObjectId = id;
        _multiMixedText.Clear();
        _multiSelectionCacheKey = string.Empty;
    }

    /// <summary>
    /// 场景拖拽框选：按下后拖动超过阈值才进入框选，松开时选中投影落在框内的对象
    /// </summary>
    /// <param name="origin">视口左上角</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>本帧是否消费了这次输入</returns>
    private bool HandleObjectBoxSelection(Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_sceneDocument is null)
        {
            _objectBoxSelecting = false;
            return false;
        }

        var mouse = ImGui.GetIO().MousePos;
        if (!_objectBoxSelecting)
        {
            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return false;
            // 窗口遮挡优先：插件管理、脚本运行器等浮动窗口压在视口上时，
            // IsMouseHoveringRect 仍为真，不补窗口判定就会在点窗口时开始框选
            if (!ImGui.IsWindowHovered() || !ImGui.IsMouseHoveringRect(origin, origin + size)) return false;
            _objectBoxStart = mouse;
            _objectBoxSelecting = true;
            // 按下的这一帧仍交给体素拾取，拖过阈值后才由框选接管输入
            return false;
        }

        // 框选矩形夹在视口内：拖到别的面板也不会把框画到窗口外、把框选范围算大
        var viewMaximum = origin + size;
        var rectMinimum = Vector2.Clamp(Vector2.Min(_objectBoxStart, mouse), origin, viewMaximum);
        var rectMaximum = Vector2.Clamp(Vector2.Max(_objectBoxStart, mouse), origin, viewMaximum);
        var hasArea = rectMaximum.X - rectMinimum.X > 4F || rectMaximum.Y - rectMinimum.Y > 4F;
        if (hasArea)
        {
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(rectMinimum, rectMaximum, ToPackedColor(Color.FromArgb(48, EditorPalette.Accent)),
                2F);
            drawList.AddRect(rectMinimum, rectMaximum, ToPackedColor(EditorPalette.Accent), 2F, ImDrawFlags.None,
                1.4F);
        }

        // 释放事件可能被弹窗、工具面板或焦点切换吞掉；用 !IsMouseDown 兜底收尾，
        // 否则 _objectBoxSelecting 会卡住，之后每帧都画框且不再响应单击拾取
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return hasArea;
        _objectBoxSelecting = false;
        if (!hasArea) return false;
        var selection = new List<StableId>();
        foreach (var item in _sceneDocument.Objects)
        {
            if (!IsSceneObjectVisible(item) || !_sceneDocument.IsLayerVisible(item.LayerIndex)) continue;
            if (!TryGetObjectScreenRect(item, origin, size, camera, out var objectMinimum, out var objectMaximum))
                continue;
            var overlaps = objectMaximum.X >= rectMinimum.X && objectMinimum.X <= rectMaximum.X &&
                           objectMaximum.Y >= rectMinimum.Y && objectMinimum.Y <= rectMaximum.Y;
            if (overlaps) selection.Add(item.Id);
        }

        SetSceneSelection(selection);
        SetStatus(selection.Count > 1 ? $"已框选 {selection.Count} 个对象" :
            selection.Count == 1 ? "已选中 1 个对象" : "框选范围内没有对象");
        return true;
    }

    /// <summary>
    /// 取对象体素包围盒投影后的屏幕矩形
    /// </summary>
    private bool TryGetObjectScreenRect(SceneObject item, Vector2 origin, Vector2 size, SceneCamera camera,
        out Vector2 minimum, out Vector2 maximum)
    {
        minimum = new Vector2(float.MaxValue);
        maximum = new Vector2(float.MinValue);
        if (!_objectVoxels.TryGetWorld(item.Id, out var world) ||
            !world.TryGetBounds(out var boundsMinimum, out var boundsMaximum)) return false;
        for (var index = 0; index < 8; index++)
        {
            var corner = new Vector3(
                (index & 1) == 0 ? boundsMinimum.X : boundsMaximum.X + 1,
                (index & 2) == 0 ? boundsMinimum.Y : boundsMaximum.Y + 1,
                (index & 4) == 0 ? boundsMinimum.Z : boundsMaximum.Z + 1);
            if (!SceneProjection.Project(corner, origin, size, camera, out var screen, out _)) continue;
            minimum = Vector2.Min(minimum, screen);
            maximum = Vector2.Max(maximum, screen);
        }

        return maximum.X >= minimum.X;
    }
}
