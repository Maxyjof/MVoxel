using System.Drawing;
using System.Drawing.Imaging;
using OpenTK.Graphics.OpenGL4;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MVoxel.Editor;

/// <summary>
/// 供 ImGui 直接采样的一张 GL 纹理包装。System.Drawing 在这里只充当
/// PNG 解码器（GDI+ 图像编解码），不是 UI 框架，也不涉及任何 WinForms 类型
/// </summary>
internal sealed class ImGuiTexture : IDisposable
{
    private readonly int _handle;

    private ImGuiTexture(int handle, int width, int height)
    {
        _handle = handle;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// 纹理宽度（像素）
    /// </summary>
    internal int Width { get; }

    /// <summary>
    /// 纹理高度（像素）
    /// </summary>
    internal int Height { get; }

    /// <summary>
    /// 交给 ImGui.Image 或 AddImage 的纹理句柄
    /// </summary>
    internal IntPtr ImGuiHandle => (IntPtr)_handle;

    /// <summary>
    /// 从输出目录加载品牌 logo，找不到文件时返回 null 而不是抛异常
    /// </summary>
    /// <returns>已上传 GPU 的 logo 纹理，或 null</returns>
    internal static ImGuiTexture? LoadLogo()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Editor", "Brand", "MVoxelLogo.png"),
            Path.Combine(AppContext.BaseDirectory, "Brand", "MVoxelLogo.png"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }

        // 先绘制到 32bppArgb 中转位图，保证统一按 BGRA 上传
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bitmap.Width, bitmap.Height, 0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return new ImGuiTexture(handle, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// 删除底层 GL 纹理
    /// </summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            GL.DeleteTexture(_handle);
        }
    }
}
