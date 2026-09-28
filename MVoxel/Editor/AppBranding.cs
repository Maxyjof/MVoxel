using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using OpenTkImage = OpenTK.Windowing.Common.Input.Image;
using OpenTkWindowIcon = OpenTK.Windowing.Common.Input.WindowIcon;

namespace MVoxel.Editor;

/// <summary>
/// 窗口图标与品牌图的加载工具
/// </summary>
internal static class AppBranding
{
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint WmSetIcon = 0x0080;
    private const nint IconSmall = 0;
    private const nint IconBig = 1;
    private const int ClassIcon = -14;
    private const int ClassSmallIcon = -34;
    private static nint _smallTaskbarIcon;
    private static nint _largeTaskbarIcon;

    private static readonly string[] LogoCandidates =
    {
        Path.Combine(AppContext.BaseDirectory, "Editor", "Brand", "MVoxelLogo.png"),
        Path.Combine(AppContext.BaseDirectory, "Brand", "MVoxelLogo.png"),
    };

    /// <summary>设置固定的 Windows Shell 身份，使任务栏按 MVoxel 的应用图标分组。</summary>
    internal static void ConfigureWindowsApplicationId()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetCurrentProcessExplicitAppUserModelId("MVoxel.Editor");
        }
    }

    /// <summary>把 exe 中同源 ICO 明确附加到窗口与窗口类，供任务栏和 Alt+Tab 读取。</summary>
    internal static void ApplyWindowsTaskbarIcon(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
        {
            return;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Editor", "Brand", "MVoxelLogo.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        _smallTaskbarIcon = LoadImageW(nint.Zero, iconPath, ImageIcon,
            GetSystemMetrics(49), GetSystemMetrics(50), LoadFromFile);
        _largeTaskbarIcon = LoadImageW(nint.Zero, iconPath, ImageIcon,
            GetSystemMetrics(11), GetSystemMetrics(12), LoadFromFile);
        if (_smallTaskbarIcon == 0 || _largeTaskbarIcon == 0)
        {
            return;
        }

        _ = SendMessageW(windowHandle, WmSetIcon, IconSmall, _smallTaskbarIcon);
        _ = SendMessageW(windowHandle, WmSetIcon, IconBig, _largeTaskbarIcon);
        SetWindowClassIcon(windowHandle, ClassSmallIcon, _smallTaskbarIcon);
        SetWindowClassIcon(windowHandle, ClassIcon, _largeTaskbarIcon);
    }

    private static void SetWindowClassIcon(nint windowHandle, int index, nint icon)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetClassLongPtrW(windowHandle, index, icon);
        }
        else
        {
            _ = SetClassLongW(windowHandle, index, icon.ToInt32());
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelId(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern nint SetClassLongPtrW(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern int SetClassLongW(nint windowHandle, int index, int value);

    /// <summary>
    /// 生成 GLFW 窗口图标
    /// </summary>
    /// <returns>多尺寸窗口图标或 null</returns>
    internal static OpenTkWindowIcon? LoadWindowIcon()
    {
        if (!PngDecoder.TryDecode(FindLogoPath(), out var pixels, out var width, out var height))
        {
            return null;
        }

        var images = new[] { 16, 20, 24, 32, 48, 64, 128, 256 }
            .Select(size => new OpenTkImage(size, size, ResizeRgba(pixels, width, height, size, size)))
            .ToArray();
        return new OpenTkWindowIcon(images);
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
        return PngDecoder.TryDecode(FindLogoPath(), out pixels, out width, out height);
    }

    /// <summary>读取项目中的 PNG 文件并转换为 RGBA8 像素。</summary>
    internal static bool TryLoadPngPixels(string path, out byte[] pixels, out int width, out int height)
    {
        return PngDecoder.TryDecode(path, out pixels, out width, out height);
    }

    /// <summary>
    /// 查找构建输出中的品牌图路径
    /// </summary>
    /// <returns>品牌图路径或 null</returns>
    private static string? FindLogoPath()
    {
        foreach (var path in LogoCandidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// 使用最近邻采样生成窗口图标尺寸
    /// </summary>
    /// <param name="source">源 RGBA8 像素</param>
    /// <param name="sourceWidth">源图宽度</param>
    /// <param name="sourceHeight">源图高度</param>
    /// <param name="targetWidth">目标图宽度</param>
    /// <param name="targetHeight">目标图高度</param>
    /// <returns>目标尺寸的 RGBA8 像素</returns>
    private static byte[] ResizeRgba(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var result = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                var sourceIndex = checked((sourceY * sourceWidth + sourceX) * 4);
                var targetIndex = checked((y * targetWidth + x) * 4);
                source.AsSpan(sourceIndex, 4).CopyTo(result.AsSpan(targetIndex, 4));
            }
        }

        return result;
    }

    /// <summary>
    /// 读取应用自带 PNG 并转换为 RGBA8
    /// </summary>
    private static class PngDecoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>
        /// 解码指定路径的无交错八位 PNG
        /// </summary>
        /// <param name="path">PNG 路径</param>
        /// <param name="pixels">输出 RGBA8 像素</param>
        /// <param name="width">输出宽度</param>
        /// <param name="height">输出高度</param>
        /// <returns>是否成功解码</returns>
        internal static bool TryDecode(string? path, out byte[] pixels, out int width, out int height)
        {
            pixels = Array.Empty<byte>();
            width = 0;
            height = 0;
            if (path is null)
            {
                return false;
            }

            try
            {
                var data = File.ReadAllBytes(path);
                if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                {
                    return false;
                }

                using var idat = new MemoryStream();
                byte[]? palette = null;
                byte[]? transparency = null;
                byte bitDepth = 0;
                byte colorType = 0;
                byte interlace = 0;
                var offset = Signature.Length;
                while (offset + 12 <= data.Length)
                {
                    var length = ReadUInt32(data, offset);
                    offset += 4;
                    var type = Encoding.ASCII.GetString(data, offset, 4);
                    offset += 4;
                    if (length > int.MaxValue || offset + (long)length + 4 > data.Length)
                    {
                        return false;
                    }

                    var chunkLength = (int)length;
                    var chunk = data.AsSpan(offset, chunkLength);
                    offset += chunkLength + 4;
                    switch (type)
                    {
                        case "IHDR" when chunkLength == 13:
                            width = checked((int)ReadUInt32(chunk, 0));
                            height = checked((int)ReadUInt32(chunk, 4));
                            bitDepth = chunk[8];
                            colorType = chunk[9];
                            interlace = chunk[12];
                            break;
                        case "PLTE":
                            palette = chunk.ToArray();
                            break;
                        case "tRNS":
                            transparency = chunk.ToArray();
                            break;
                        case "IDAT":
                            idat.Write(chunk);
                            break;
                        case "IEND":
                            offset = data.Length;
                            break;
                    }
                }

                if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0 || idat.Length == 0)
                {
                    return false;
                }

                var channels = ChannelsFor(colorType);
                if (channels == 0 || colorType == 3 && (palette is null || palette.Length % 3 != 0))
                {
                    return false;
                }

                var scanlineSize = checked(width * channels);
                var decompressedSize = checked((scanlineSize + 1) * height);
                var filtered = new byte[decompressedSize];
                idat.Position = 0;
                using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
                {
                    if (!ReadExactly(zlib, filtered))
                    {
                        return false;
                    }
                }

                var rows = Unfilter(filtered, width, height, channels, scanlineSize);
                pixels = ConvertToRgba(rows, width, height, colorType, palette, transparency);
                return pixels.Length != 0;
            }
            catch (Exception) when (IsDecodeFailure())
            {
                pixels = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }
        }

        /// <summary>
        /// 返回当前异常是否属于品牌图解码失败
        /// </summary>
        /// <returns>始终返回 true 以屏蔽损坏品牌图对编辑器启动的影响</returns>
        private static bool IsDecodeFailure() => true;

        /// <summary>
        /// 读取大端序无符号整数
        /// </summary>
        /// <param name="data">源数据</param>
        /// <param name="offset">读取位置</param>
        /// <returns>整数值</returns>
        private static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// 读取大端序切片整数
        /// </summary>
        /// <param name="data">源数据</param>
        /// <param name="offset">读取位置</param>
        /// <returns>整数值</returns>
        private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// 返回 PNG 颜色类型的通道数
        /// </summary>
        /// <param name="colorType">PNG 颜色类型</param>
        /// <returns>通道数</returns>
        private static int ChannelsFor(byte colorType)
        {
            return colorType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                6 => 4,
                _ => 0,
            };
        }

        /// <summary>
        /// 读取解压后的完整扫描数据
        /// </summary>
        /// <param name="stream">解压流</param>
        /// <param name="buffer">目标缓冲区</param>
        /// <returns>是否读取完整</returns>
        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            var readTotal = 0;
            while (readTotal < buffer.Length)
            {
                var read = stream.Read(buffer, readTotal, buffer.Length - readTotal);
                if (read == 0)
                {
                    return false;
                }

                readTotal += read;
            }

            return stream.ReadByte() == -1;
        }

        /// <summary>
        /// 根据 PNG 过滤类型还原扫描行
        /// </summary>
        /// <param name="filtered">带过滤类型的数据</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="bytesPerPixel">每像素字节数</param>
        /// <param name="scanlineSize">扫描行字节数</param>
        /// <returns>还原后的扫描行</returns>
        private static byte[] Unfilter(byte[] filtered, int width, int height, int bytesPerPixel, int scanlineSize)
        {
            var rows = new byte[checked(scanlineSize * height)];
            var sourceOffset = 0;
            for (var y = 0; y < height; y++)
            {
                var filter = filtered[sourceOffset++];
                var rowOffset = y * scanlineSize;
                var previousRowOffset = rowOffset - scanlineSize;
                for (var x = 0; x < scanlineSize; x++)
                {
                    var raw = filtered[sourceOffset++];
                    var left = x >= bytesPerPixel ? rows[rowOffset + x - bytesPerPixel] : (byte)0;
                    var up = y > 0 ? rows[previousRowOffset + x] : (byte)0;
                    var upperLeft = y > 0 && x >= bytesPerPixel
                        ? rows[previousRowOffset + x - bytesPerPixel]
                        : (byte)0;
                    rows[rowOffset + x] = filter switch
                    {
                        0 => raw,
                        1 => (byte)(raw + left),
                        2 => (byte)(raw + up),
                        3 => (byte)(raw + ((left + up) / 2)),
                        4 => (byte)(raw + Paeth(left, up, upperLeft)),
                        _ => throw new InvalidDataException("品牌图包含未知 PNG 过滤类型"),
                    };
                }
            }

            return rows;
        }

        /// <summary>
        /// 计算 PNG Paeth 预测值
        /// </summary>
        /// <param name="left">左侧字节</param>
        /// <param name="up">上方字节</param>
        /// <param name="upperLeft">左上字节</param>
        /// <returns>预测字节</returns>
        private static byte Paeth(byte left, byte up, byte upperLeft)
        {
            var estimate = left + up - upperLeft;
            var leftDistance = Math.Abs(estimate - left);
            var upDistance = Math.Abs(estimate - up);
            var upperLeftDistance = Math.Abs(estimate - upperLeft);
            return leftDistance <= upDistance && leftDistance <= upperLeftDistance
                ? left
                : upDistance <= upperLeftDistance ? up : upperLeft;
        }

        /// <summary>
        /// 把过滤后的扫描行转换成 RGBA8
        /// </summary>
        /// <param name="rows">还原后的扫描行</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <param name="colorType">PNG 颜色类型</param>
        /// <param name="palette">调色板</param>
        /// <param name="transparency">透明度表</param>
        /// <returns>RGBA8 像素</returns>
        private static byte[] ConvertToRgba(byte[] rows, int width, int height, byte colorType, byte[]? palette, byte[]? transparency)
        {
            var pixels = new byte[checked(width * height * 4)];
            var sourceChannels = ChannelsFor(colorType);
            for (var index = 0; index < width * height; index++)
            {
                var sourceIndex = index * sourceChannels;
                var targetIndex = index * 4;
                switch (colorType)
                {
                    case 0:
                        pixels[targetIndex] = pixels[targetIndex + 1] = pixels[targetIndex + 2] = rows[sourceIndex];
                        pixels[targetIndex + 3] = 255;
                        break;
                    case 2:
                        rows.AsSpan(sourceIndex, 3).CopyTo(pixels.AsSpan(targetIndex, 3));
                        pixels[targetIndex + 3] = 255;
                        break;
                    case 3:
                        var paletteIndex = rows[sourceIndex] * 3;
                        if (palette is null || paletteIndex + 2 >= palette.Length)
                        {
                            return Array.Empty<byte>();
                        }

                        palette.AsSpan(paletteIndex, 3).CopyTo(pixels.AsSpan(targetIndex, 3));
                        pixels[targetIndex + 3] = transparency is not null && rows[sourceIndex] < transparency.Length
                            ? transparency[rows[sourceIndex]]
                            : (byte)255;
                        break;
                    case 4:
                        pixels[targetIndex] = pixels[targetIndex + 1] = pixels[targetIndex + 2] = rows[sourceIndex];
                        pixels[targetIndex + 3] = rows[sourceIndex + 1];
                        break;
                    case 6:
                        rows.AsSpan(sourceIndex, 4).CopyTo(pixels.AsSpan(targetIndex, 4));
                        break;
                    default:
                        return Array.Empty<byte>();
                }
            }

            return pixels;
        }
    }
}
