using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenTkImage = OpenTK.Windowing.Common.Input.Image;
using OpenTkWindowIcon = OpenTK.Windowing.Common.Input.WindowIcon;

namespace MVoxel.Editor;

/// <summary>
/// 窗口图标与品牌图的加载工具。System.Drawing 在这里只是 PNG 解码器，
/// 不涉及任何 WinForms 类型
/// </summary>
internal static class AppBranding
{
    private static readonly string[] LogoCandidates =
    {
        Path.Combine(AppContext.BaseDirectory, "Editor", "Brand", "MVoxelLogo.png"),
        Path.Combine(AppContext.BaseDirectory, "Brand", "MVoxelLogo.png"),
    };

    /// <summary>
    /// 生成 GLFW 窗口图标（32 与 16 两档），logo 缺失时返回 null 让系统用默认图标
    /// </summary>
    /// <returns>多尺寸窗口图标或 null</returns>
    internal static OpenTkWindowIcon? LoadWindowIcon()
    {
        var images = new List<OpenTkImage>();
        foreach (var size in new[] { 32, 16 })
        {
            using var source = LoadLogo();
            if (source is null)
            {
                break;
            }

            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            images.Add(new OpenTkImage(size, size, ToRgba(bitmap)));
        }

        return images.Count == 0 ? null : new OpenTkWindowIcon(images.ToArray());
    }

    /// <summary>
    /// 读取品牌图并转换为 Vulkan ImGui 可上传的 RGBA8 像素
    /// </summary>
    /// <param name="pixels">连续排列的 RGBA8 像素</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <returns>是否成功读取品牌图</returns>
    internal static bool TryLoadLogoPixels(out byte[] pixels, out int width, out int height)
    {
        using var source = LoadLogo();
        if (source is null)
        {
            pixels = Array.Empty<byte>();
            width = 0;
            height = 0;
            return false;
        }

        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        pixels = ToRgba(bitmap);
        width = bitmap.Width;
        height = bitmap.Height;
        return true;
    }

    /// <summary>
    /// 从候选路径加载原始 logo 位图
    /// </summary>
    /// <returns>logo 位图或 null</returns>
    private static Bitmap? LoadLogo()
    {
        foreach (var path in LogoCandidates)
        {
            if (File.Exists(path))
            {
                return new Bitmap(path);
            }
        }

        return null;
    }

    /// <summary>
    /// 把 BGRA 位图内存转成 GLFW 需要的 RGBA 字节序
    /// </summary>
    /// <param name="bitmap">32bppArgb 位图</param>
    /// <returns>RGBA 字节数组</returns>
    private static byte[] ToRgba(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var index = 0; index < bytes.Length; index += 4)
            {
                (bytes[index], bytes[index + 2]) = (bytes[index + 2], bytes[index]);
            }

            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

}
