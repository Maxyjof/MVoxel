using System.Globalization;
using ImGuiNET;
using MVoxel.Core;
using OpenTK.Windowing.Desktop;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 提供 Vulkan 编辑器的可选界面回归诊断
/// </summary>
internal static class UiDiagnostics
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("MVOXEL_UI_CAPTURE_DIR");
    private static int _frame;
    private static int _blockingOperationRequested;
    private static int _settingsRequested;
    private static int _exportRequested;

    /// <summary>
    /// 获取回归诊断是否启用
    /// </summary>
    internal static bool Enabled => !string.IsNullOrEmpty(Output);

    /// <summary>
    /// 获取当前回归剧本阶段名称
    /// </summary>
    private static string Scenario => _frame < 30 ? "default" : _frame < 60 ? "horizon" :
        _frame < 90 ? "far" : _frame < 120 ? "panned" : _frame < 150 ? "configuration" : "render";

    /// <summary>
    /// 读取窗口所在显示器的 DPI 缩放
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <returns>限制在 1 到 4 之间的缩放系数</returns>
    internal static float Dpi(GameWindow window)
    {
        if (Enabled && _frame >= 60 && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI_AFTER"),
                CultureInfo.InvariantCulture, out var laterScale)) return laterScale;
        if (Enabled && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI"),
                CultureInfo.InvariantCulture, out var scale)) return scale;
        return window.TryGetCurrentMonitorScale(out var x, out _) ? Math.Clamp(x, 1F, 4F) : 1F;
    }

    /// <summary>
    /// 配置 ImGui 回归运行时
    /// </summary>
    internal static unsafe void Configure()
    {
        if (Enabled) ImGui.GetIO().NativePtr->IniFilename = null;
    }

    /// <summary>
    /// 获取是否应启动演示用阻塞加载流程
    /// </summary>
    internal static bool TakeBlockingOperationRequest() =>
        Enabled && string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_LOADING"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _blockingOperationRequested, 1) == 0;

    /// <summary>
    /// 获取是否应打开设置窗口
    /// </summary>
    internal static bool TakeSettingsRequest() =>
        Enabled && _frame == 45 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_SETTINGS"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _settingsRequested, 1) == 0;

    /// <summary>
    /// 获取是否应执行渲染导出验证
    /// </summary>
    internal static bool TakeExportRequest() =>
        Enabled && _frame >= 160 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_EXPORT_PNG"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _exportRequested, 1) == 0;

    /// <summary>
    /// 获取是否应关闭欢迎窗口
    /// </summary>
    internal static bool ShouldCloseWelcome =>
        Enabled && _frame == 30 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_CLOSE_WELCOME"), "1", StringComparison.Ordinal);

    /// <summary>
    /// 按回归剧本切换页面
    /// </summary>
    /// <param name="center">中央页面请求</param>
    /// <param name="inspector">检查器页面请求</param>
    internal static void PrepareTabs(ref int? center, ref int? inspector)
    {
        if (!Enabled) return;
        if (_frame == 120) inspector = 1;
        if (_frame == 150) center = 1;
    }

    /// <summary>
    /// 记录 Vulkan 帧的可验证诊断信息
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="name">输出文件名前缀</param>
    internal static unsafe void Capture(GameWindow window, string name)
    {
        if (!Enabled || _frame % 30 != 15 || (name == "welcome" && _frame > 30)) return;
        Directory.CreateDirectory(Output!);
        var io = ImGui.GetIO();
        var font = io.Fonts.Fonts.Size > 0 ? io.Fonts.Fonts[0] : default;
        var missing = font.NativePtr is null ? "字体图集未创建" : string.Concat("渲染场景检查器资产细节等级新建项目欢迎使用坐标方向球光线追踪透明背景".Distinct()
            .Where(c => font.FindGlyphNoFallback(c).NativePtr == null));
        var style = ImGui.GetStyle();
        var windowBackground = style.Colors[(int)ImGuiCol.WindowBg];
        AtomicFile.WriteText(Path.Combine(Output!, $"{name}-{Scenario}.txt"),
            $"Backend=Vulkan\nFramebuffer={window.FramebufferSize.X}x{window.FramebufferSize.Y}\n" +
            $"Display={io.DisplaySize}\nScale={io.DisplayFramebufferScale}\n" +
            $"FontLine={(font.NativePtr is null ? 0 : font.FontSize)}\nChineseAdvance={(font.NativePtr is null ? 0 : font.GetCharAdvance('中'))}\n" +
            $"Missing={missing}\nWindowBg={windowBackground.X:F3},{windowBackground.Y:F3},{windowBackground.Z:F3}\n" +
            $"Vertices={ImGui.GetDrawData().TotalVtxCount}\nVulkan=Ready\n");
        if (missing.Length != 0) throw new InvalidOperationException("界面字体回归验证失败");
    }

    /// <summary>
    /// 推进回归剧本并调整相机
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="yaw">偏航角引用</param>
    /// <param name="pitch">俯仰角引用</param>
    /// <param name="distance">观察距离引用</param>
    /// <param name="pan">平移目标引用</param>
    internal static void Advance(GameWindow window, ref float yaw, ref float pitch, ref float distance, ref Vector2 pan)
    {
        if (!Enabled) return;
        _frame++;
        if (_frame == 30) pitch = 0.035F;
        if (_frame == 60) { pitch = 0.48F; distance = 256F; }
        if (_frame == 90 && !string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_NO_PAN"), "1", StringComparison.Ordinal))
        {
            yaw = -0.85F;
            pitch = 0.7F;
            distance = 18F;
            pan = new Vector2(12000, -8400);
        }
        if (_frame == 150) { pitch = 0.42F; distance = 33F; }
        if (_frame == 180) window.Close();
    }
}
