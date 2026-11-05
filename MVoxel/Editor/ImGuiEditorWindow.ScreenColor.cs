using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private bool _screenColorSampling;
    private bool _screenColorSamplingArmed;
    private Vector2 _colorSampleSceneOrigin;
    private Vector2 _colorSampleSceneSize;

    private void DrawScreenColorSampler()
    {
        if (!_screenColorSampling) return;
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##屏幕取色拦截", flags))
        {
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.InvisibleButton("##取色范围", display);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _screenColorSamplingArmed = true;
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _screenColorSampling = false;
                SetStatus("已取消取色");
            }
            else if (_screenColorSamplingArmed && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (TrySampleSceneVoxel(ImGui.GetIO().MousePos))
                {
                    _activeTool = EditorTool.Draw;
                    _screenColorSampling = false;
                }
                else if (EditorScreenColorSampler.TrySample(out var color))
                {
                    _sampledBrushColor = color;
                    _activeTool = EditorTool.Draw;
                    _screenColorSampling = false;
                    SetStatus($"已取显示颜色：RGB({color.R}, {color.G}, {color.B})");
                }
                else SetStatus("无法读取此处颜色，请在编辑器窗口内重试或按 Esc 取消");
            }
        }
        ImGui.End();
    }

    private bool TrySampleSceneVoxel(Vector2 mouse)
    {
        var origin = _colorSampleSceneOrigin;
        var size = _colorSampleSceneSize;
        if (_centerTab != 0 || _project is null || size.X <= 0F || size.Y <= 0F ||
            mouse.X < origin.X || mouse.Y < origin.Y || mouse.X >= origin.X + size.X || mouse.Y >= origin.Y + size.Y)
            return false;
        var gizmoCenter = origin + new Vector2(size.X - 58F, 76F);
        var gizmoRadius = Math.Clamp(Math.Min(size.X, size.Y) * .07F, 28F, 36F);
        if (Vector2.Distance(mouse, gizmoCenter) <= gizmoRadius + 8F) return false;
        var hit = PickVoxel(mouse, origin, size, GetSceneCamera());
        if (hit is not { } voxel || !GetRenderWorld(uint.MaxValue, true).TryGet(voxel.Coordinate, out var value) ||
            _sceneDocument is null || !_objectVoxels.TryGetVisibleVoxel(_sceneDocument, uint.MaxValue, true,
                voxel.Coordinate, out var owner, out _)) return false;
        _sampledBrushColor = value.Color;
        _selectedSceneObjectId = owner;
        // 曲线渲染器生成的体素只用于取样颜色，不会成为绘制目标
        _voxelEditTargetId = IsVoxelEditableObject(owner) ? owner : null;
        EnsureModelBrushMaterialTarget();
        SetModelBrushMaterialSlot(value.MaterialSlot);
        _brushEmissive = (value.Flags & (byte)MVoxel.Voxels.VoxelFlags.Emissive) != 0;
        SetStatus($"已取体素原色：RGB({value.Color.R}, {value.Color.G}, {value.Color.B})，并取样材质与发光标记");
        return true;
    }
}

