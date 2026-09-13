using System.Drawing;
using System.Drawing.Imaging;
using OpenTK.Graphics.OpenGL4;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MVoxel.Editor;

internal sealed class ImGuiTexture : IDisposable
{
    private readonly int _handle;

    private ImGuiTexture(int handle, int width, int height)
    {
        _handle = handle;
        Width = width;
        Height = height;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal IntPtr ImGuiHandle => (IntPtr)_handle;

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

    public void Dispose()
    {
        if (_handle != 0)
        {
            GL.DeleteTexture(_handle);
        }
    }
}
