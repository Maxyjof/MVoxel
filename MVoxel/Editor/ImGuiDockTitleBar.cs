using System.Runtime.InteropServices;

namespace MVoxel.Editor;

/// <summary>
/// 停靠标签栏的拖动扩展。ImGui 原生只支持把单个标签页拖出来重组，停靠着的窗口无法靠拖动标题栏整组移动；
/// 定制版 ImGui（Native/ImGuiDocking）因此加了补丁：拖动标题栏空白处会把整个停靠节点（连同该节点所有标签页）
/// 拖出来成为浮动窗口。这里负责把「标题栏右侧自绘按钮区」的宽度告诉原生层，点三点/叉号时不会误拖窗口。
/// </summary>
internal static class ImGuiDockTitleBar
{
    private const string Library = "cimgui";

    [DllImport(Library, EntryPoint = "igMvoxelSetTitleBarDragExcludeRight")]
    private static extern void NativeSetTitleBarDragExcludeRight(float width);

    private static bool _unavailable;

    /// <summary>每帧同步右侧按钮区宽度：该区间内的按下不会被当成拖动标题栏。</summary>
    /// <param name="frameHeight">标题栏按钮边长（与 DrawPanelWindowMenu 一致）</param>
    /// <param name="windowPaddingX">窗口内边距，叉号贴右时用的是它的一半</param>
    internal static void SyncButtonArea(float frameHeight, float windowPaddingX)
    {
        if (_unavailable) return;
        try
        {
            // 叉号贴最右（右内边距一半），三点在叉号左侧，两者等宽、间距 2px（外加 4px 容差）
            NativeSetTitleBarDragExcludeRight(windowPaddingX * .5F + frameHeight * 2F + 6F);
        }
        catch (DllNotFoundException)
        {
            NativeSetTitleBarDragExcludeRightFallback();
        }
        catch (EntryPointNotFoundException)
        {
            NativeSetTitleBarDragExcludeRightFallback();
        }
    }

    /// <summary>旧版原生库没有该导出时静默降级：拖动仍然可用，只是按钮区也能拖窗口。</summary>
    private static void NativeSetTitleBarDragExcludeRightFallback() => _unavailable = true;
}
