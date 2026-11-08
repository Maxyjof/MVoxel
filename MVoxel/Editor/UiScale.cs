namespace MVoxel.Editor;

/// <summary>
/// 编辑器界面的 DPI 缩放基准：所有按 96 DPI 手写的像素尺寸都从这里换算
/// 字体由编辑器字体目录按 DPI 放大，样式与自绘尺寸也必须同倍率，否则高缩放下按钮会互相溢出或裁字
/// </summary>
internal static class UiScale
{
    /// <summary>当前界面缩放系数，1 表示 100%</summary>
    internal static float Current { get; private set; } = 1F;

    /// <summary>更新缩放系数，变化时返回 true 以便调用方重建样式</summary>
    /// <param name="scale">窗口所在显示器的缩放系数</param>
    /// <returns>是否发生变化</returns>
    internal static bool Set(float scale)
    {
        var clamped = Math.Clamp(scale, 1F, 4F);
        if (Math.Abs(clamped - Current) < 0.01F) return false;
        Current = clamped;
        return true;
    }

    /// <summary>把 96 DPI 下的像素值换算到当前缩放</summary>
    /// <param name="value">设计像素值</param>
    /// <returns>缩放后的值</returns>
    internal static float Px(float value) => value * Current;

    /// <summary>按当前缩放构造尺寸，高至少为一个控件行高，避免文字被裁切</summary>
    /// <param name="width">设计宽度</param>
    /// <param name="minimumHeight">设计最小高度</param>
    /// <returns>缩放后的尺寸</returns>
    internal static System.Numerics.Vector2 Size(float width, float minimumHeight) =>
        new(Px(width), MathF.Max(Px(minimumHeight), ImGuiNET.ImGui.GetFrameHeight()));
}
