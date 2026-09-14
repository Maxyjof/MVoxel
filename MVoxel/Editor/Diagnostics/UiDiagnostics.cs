using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using ImGuiNET;
using MVoxel.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 可选开启的本地渲染回归捕获工具，只读取本应用自己的后台缓冲
/// （绝不截屏桌面或其他程序）。设置环境变量 MVOXEL_UI_CAPTURE_DIR 后启用，
/// 按帧剧本自动切换相机与页签并保存 PNG 与诊断文本，GL 错误或缺字直接抛异常，
/// 让验证以失败告终而不是接受一个"只是还活着"的进程
/// </summary>
internal static class UiDiagnostics
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("MVOXEL_UI_CAPTURE_DIR");
    private static int _frame;
    private static int _blockingOperationRequested;

    /// <summary>
    /// 捕获模式是否启用
    /// </summary>
    internal static bool Enabled => !string.IsNullOrEmpty(Output);

    /// <summary>
    /// 当前帧所处验证剧本阶段名，用于输出文件命名
    /// </summary>
    private static string Scenario => _frame < 30 ? "default" : _frame < 60 ? "horizon" :
        _frame < 90 ? "far" : _frame < 120 ? "panned" : _frame < 150 ? "configuration" : "render";

    /// <summary>
    /// 读取窗口所在显示器的 DPI 缩放，捕获模式下可被环境变量强制覆盖以测试
    /// 运行中 DPI 变化
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <returns>1 到 4 之间的缩放系数</returns>
    internal static float Dpi(GameWindow window)
    {
        if (Enabled && _frame >= 60 && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI_AFTER"),
                CultureInfo.InvariantCulture, out var laterScale)) return laterScale;
        if (Enabled && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI"),
                CultureInfo.InvariantCulture, out var scale)) return scale;
        return window.TryGetCurrentMonitorScale(out var x, out _) ? Math.Clamp(x, 1F, 4F) : 1F;
    }

    /// <summary>
    /// 捕获模式下禁用 ImGui ini 持久化，保证每次运行布局一致可复现
    /// </summary>
    internal static unsafe void Configure()
    {
        if (Enabled) ImGui.GetIO().NativePtr->IniFilename = null;
    }

    // 以下测试钩子仅在捕获模式生效，普通运行绝不会伪造任务或关闭用户的欢迎窗口

    /// <summary>
    /// 是否应触发一次演示用的阻塞加载流程（环境变量驱动，只触发一次）
    /// </summary>
    /// <returns>本次是否触发</returns>
    internal static bool TakeBlockingOperationRequest() =>
        Enabled && string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_LOADING"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _blockingOperationRequested, 1) == 0;

    private static int _exportRequested;

    /// <summary>
    /// 是否应触发一次渲染 PNG 导出验证（累积若干帧后只触发一次）
    /// </summary>
    /// <returns>本次是否触发</returns>
    internal static bool TakeExportRequest() =>
        Enabled && _frame >= 160 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_EXPORT_PNG"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _exportRequested, 1) == 0;

    /// <summary>
    /// 是否应在当前帧关闭欢迎窗口以验证解除阻塞
    /// </summary>
    internal static bool ShouldCloseWelcome =>
        Enabled && _frame == 30 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_CLOSE_WELCOME"), "1", StringComparison.Ordinal);

    /// <summary>
    /// 按剧本切换页签：先打开画面设置面板，再切到渲染页
    /// </summary>
    /// <param name="center">中央页签请求</param>
    /// <param name="inspector">检查器页签请求</param>
    internal static void PrepareTabs(ref int? center, ref int? inspector)
    {
        if (!Enabled) return;
        if (_frame == 120) inspector = 1;
        if (_frame == 150) center = 1;
    }

    /// <summary>
    /// 诊断模式下把 GL 错误归因到具体渲染阶段并立即抛出
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="stage">阶段名</param>
    internal static void ProbeGlStage(GameWindow window, string stage)
    {
        if (!Enabled) return;
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new InvalidOperationException($"GL error {stage} at frame {_frame}: {error}");
        }
    }

    /// <summary>
    /// 在约定帧读取后台缓冲保存 PNG，并写一份诊断文本
    /// （帧缓冲尺寸、DPI、字体行高、缺字探测、顶点数与 GL 错误码），
    /// 任何缺字或 GL 错误都直接判定验证失败
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="name">输出文件名前缀（editor 或 welcome）</param>
    internal static unsafe void Capture(GameWindow window, string name)
    {
        if (!Enabled || _frame % 30 != 15 || (name == "welcome" && _frame > 30)) return;
        Directory.CreateDirectory(Output!);
        var width = window.FramebufferSize.X;
        var height = window.FramebufferSize.Y;
        var bytes = new byte[width * height * 4];
        // 计算与图像写入可能残留绑定状态，读回前复位到默认读帧缓冲
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, bytes);
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(bytes, (height - 1 - row) * width * 4, data.Scan0 + row * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(Path.Combine(Output!, $"{name}-{Scenario}.png"), ImageFormat.Png);
        var io = ImGui.GetIO();
        var font = io.Fonts.Fonts[0];
        var missing = string.Concat("渲染场景检查器资产细节等级新建项目欢迎使用坐标方向球光线追踪透明背景".Distinct()
            .Where(c => font.FindGlyphNoFallback(c).NativePtr == null));
        var error = GL.GetError();
        AtomicFile.WriteText(Path.Combine(Output!, $"{name}-{Scenario}.txt"),
            $"Framebuffer={width}x{height}\nDisplay={io.DisplaySize}\nScale={io.DisplayFramebufferScale}\nFontLine={font.FontSize}\nChineseAdvance={font.GetCharAdvance('中')}\nMissing={missing}\nVertices={ImGui.GetDrawData().TotalVtxCount}\nGL={error}\n");
        if (error != ErrorCode.NoError || missing.Length != 0) throw new InvalidOperationException("UI render verification failed.");
    }

    /// <summary>
    /// 推进帧剧本：俯冲地平线、拉远、超大平移（可用 NO_PAN 跳过）、
    /// 渲染页重新取景，180 帧自动关闭窗体
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="yaw">轨道偏航角引用</param>
    /// <param name="pitch">轨道俯仰角引用</param>
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
            yaw = -0.85F; pitch = 0.7F; distance = 18F; pan = new Vector2(12000, -8400);
        }
        if (_frame == 150) { pitch = 0.42F; distance = 33F; }
        if (_frame == 180) window.Close();
    }
}
