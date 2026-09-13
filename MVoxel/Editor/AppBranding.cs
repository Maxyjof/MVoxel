using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenTkImage = OpenTK.Windowing.Common.Input.Image;
using OpenTkWindowIcon = OpenTK.Windowing.Common.Input.WindowIcon;

namespace MVoxel.Editor;

internal static class AppBranding
{
    private static readonly string[] LogoCandidates =
    {
        Path.Combine(AppContext.BaseDirectory, "Editor", "Brand", "MVoxelLogo.png"),
        Path.Combine(AppContext.BaseDirectory, "Brand", "MVoxelLogo.png"),
    };

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
