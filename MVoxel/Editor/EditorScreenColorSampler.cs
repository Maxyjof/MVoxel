using System.Runtime.InteropServices;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 读取编辑器可见屏幕像素
/// </summary>
internal static class EditorScreenColorSampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);

    internal static bool TrySample(out Rgba32 color)
    {
        color = default;
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var point)) return false;
        var window = WindowFromPoint(point);
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0 || processId != (uint)Environment.ProcessId)
            return false;
        var dc = GetDC(0);
        if (dc == 0) return false;
        try
        {
            var value = GetPixel(dc, point.X, point.Y);
            if (value == uint.MaxValue) return false;
            color = new Rgba32((byte)value, (byte)(value >> 8), (byte)(value >> 16), 255);
            return true;
        }
        finally { ReleaseDC(0, dc); }
    }
}

