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

// Opt-in, local render regression capture. Reads this application's actual
// back buffer before SwapBuffers, never the desktop or any other application.
internal static class UiDiagnostics
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("MVOXEL_UI_CAPTURE_DIR");
    private static int _frame;
    private static int _blockingOperationRequested;
    internal static bool Enabled => !string.IsNullOrEmpty(Output);
    private static string Scenario => _frame < 30 ? "default" : _frame < 60 ? "horizon" :
        _frame < 90 ? "far" : _frame < 120 ? "panned" : _frame < 150 ? "configuration" : "render";

    internal static float Dpi(GameWindow window)
    {
        if (Enabled && _frame >= 60 && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI_AFTER"),
                CultureInfo.InvariantCulture, out var laterScale)) return laterScale;
        if (Enabled && float.TryParse(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_DPI"),
                CultureInfo.InvariantCulture, out var scale)) return scale;
        return window.TryGetCurrentMonitorScale(out var x, out _) ? Math.Clamp(x, 1F, 4F) : 1F;
    }

    internal static unsafe void Configure()
    {
        if (Enabled) ImGui.GetIO().NativePtr->IniFilename = null;
    }

    // Test-only controls used by the local screenshot harness. They are
    // inert unless capture mode is explicitly enabled, so normal editor runs
    // never create fake work or close the user's welcome window.
    internal static bool TakeBlockingOperationRequest() =>
        Enabled && string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_LOADING"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _blockingOperationRequested, 1) == 0;

    private static int _exportRequested;
    internal static bool TakeExportRequest() =>
        Enabled && _frame >= 160 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_EXPORT_PNG"), "1", StringComparison.Ordinal) &&
        Interlocked.Exchange(ref _exportRequested, 1) == 0;

    internal static bool ShouldCloseWelcome =>
        Enabled && _frame == 30 &&
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_TEST_CLOSE_WELCOME"), "1", StringComparison.Ordinal);

    internal static void PrepareTabs(ref int? center, ref int? inspector)
    {
        if (!Enabled) return;
        if (_frame == 120) inspector = 1;
        if (_frame == 150) center = 1;
    }

    internal static void ProbeGlStage(GameWindow window, string stage)
    {
        if (!Enabled) return;
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new InvalidOperationException($"GL error {stage} at frame {_frame}: {error}");
        }
    }

    internal static unsafe void Capture(GameWindow window, string name)
    {
        if (!Enabled || _frame % 30 != 15 || (name == "welcome" && _frame > 30)) return;
        Directory.CreateDirectory(Output!);
        var width = window.FramebufferSize.X;
        var height = window.FramebufferSize.Y;
        var bytes = new byte[width * height * 4];
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
        // GL errors fail verification instead of accepting a merely alive process.
        var error = GL.GetError();
        AtomicFile.WriteText(Path.Combine(Output!, $"{name}-{Scenario}.txt"),
            $"Framebuffer={width}x{height}\nDisplay={io.DisplaySize}\nScale={io.DisplayFramebufferScale}\nFontLine={font.FontSize}\nChineseAdvance={font.GetCharAdvance('中')}\nMissing={missing}\nVertices={ImGui.GetDrawData().TotalVtxCount}\nGL={error}\n");
        if (error != ErrorCode.NoError || missing.Length != 0) throw new InvalidOperationException("UI render verification failed.");
    }

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
