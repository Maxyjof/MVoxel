using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>阻塞弹窗始终相对于编辑器主视口居中。</summary>
internal static class CenteredModal
{
    private static void Center()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5F, ImGuiCond.Always, new Vector2(0.5F, 0.5F));
    }

    internal static bool Begin(string name, ImGuiWindowFlags flags)
    {
        Center();
        var visible = ImGui.BeginPopupModal(name, flags | ImGuiWindowFlags.NoMove);
        if (visible) FloatingWindowShadow.Draw();
        return visible;
    }

    internal static bool Begin(string name, ref bool open, ImGuiWindowFlags flags)
    {
        Center();
        var visible = ImGui.BeginPopupModal(name, ref open, flags | ImGuiWindowFlags.NoMove);
        if (visible) FloatingWindowShadow.Draw();
        return visible;
    }
}
