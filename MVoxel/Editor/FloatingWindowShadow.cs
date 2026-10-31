using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>为未停靠窗口在窗口外侧绘制柔和阴影。</summary>
internal static class FloatingWindowShadow
{
    internal static void Draw()
    {
        if (ImGui.IsWindowDocked()) return;
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (size.X <= 0 || size.Y <= 0) return;
        var draw = ImGui.GetWindowDrawList();
        var scale = Math.Max(1F, ImGui.GetIO().FontGlobalScale);
        draw.PushClipRectFullScreen();
        for (var index = 10; index >= 1; index--)
        {
            var spread = index * scale;
            var alpha = (byte)Math.Clamp((11 - index) * 2, 0, 24);
            var color = ImGui.ColorConvertFloat4ToU32(new Vector4(0F, 0F, 0F, alpha / 255F));
            draw.AddRect(position - new Vector2(spread, spread * .55F),
                position + size + new Vector2(spread, spread * 1.25F), color,
                ImGui.GetStyle().WindowRounding + spread, ImDrawFlags.RoundCornersAll, scale);
        }
        draw.PopClipRect();
    }
}
