using System.Globalization;
using System.Text;

namespace MVoxel.Voxels;

/// <summary>
/// Radiance RGBE（.hdr）环境图的解码与降采样工具，
/// 输出线性 RGB float 经纬图，供 GPU 环境缓冲上传
/// </summary>
public static class RadianceImage
{
    /// <summary>
    /// 环境贴图降采样后的最大宽度
    /// </summary>
    public const int MaxWidth = 256;

    /// <summary>
    /// 解码 .hdr 文件为线性 RGB 经纬图
    /// </summary>
    /// <param name="path">.hdr 文件路径</param>
    /// <param name="width">输出宽度</param>
    /// <param name="height">输出高度</param>
    /// <returns>每像素 3 个 float 的线性 RGB 数据</returns>
    public static float[] Decode(string path, out int width, out int height)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeBytes(bytes, out width, out height);
    }

    /// <summary>
    /// 解码 RGBE 字节流，超过最大宽度时按面积平均降采样
    /// </summary>
    /// <param name="bytes">完整文件字节</param>
    /// <param name="width">输出宽度</param>
    /// <param name="height">输出高度</param>
    /// <returns>每像素 3 个 float 的线性 RGB 数据</returns>
    public static float[] DecodeBytes(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        var (sourceWidth, sourceHeight, pixels) = ReadRadiance(bytes);
        if (sourceWidth <= MaxWidth)
        {
            width = sourceWidth;
            height = sourceHeight;
            return pixels;
        }

        width = MaxWidth;
        height = Math.Max(1, sourceHeight * MaxWidth / sourceWidth);
        var output = new float[width * height * 3];
        var xRatio = (double)sourceWidth / width;
        var yRatio = (double)sourceHeight / height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX0 = Math.Min(sourceWidth - 1, (int)(x * xRatio));
            var sourceX1 = Math.Min(sourceWidth, sourceX0 + (int)Math.Ceiling(xRatio));
            var sourceY0 = Math.Min(sourceHeight - 1, (int)(y * yRatio));
            var sourceY1 = Math.Min(sourceHeight, sourceY0 + (int)Math.Ceiling(yRatio));
            double r = 0, g = 0, b = 0;
            var count = 0;
            for (var sy = sourceY0; sy < sourceY1; sy++)
            for (var sx = sourceX0; sx < sourceX1; sx++)
            {
                var sourceIndex = (sy * sourceWidth + sx) * 3;
                r += pixels[sourceIndex];
                g += pixels[sourceIndex + 1];
                b += pixels[sourceIndex + 2];
                count++;
            }

            var outputIndex = (y * width + x) * 3;
            output[outputIndex] = (float)(r / count);
            output[outputIndex + 1] = (float)(g / count);
            output[outputIndex + 2] = (float)(b / count);
        }

        return output;
    }

    /// <summary>
    /// 把线性 RGB 编码为 RGBE 字节（4 字节像素），供测试与工具生成 .hdr
    /// </summary>
    /// <param name="linear">线性 RGB 数据</param>
    /// <returns>RGBE 字节，与输入像素数对应</returns>
    public static byte[] EncodeRgbe(ReadOnlySpan<float> linear)
    {
        var output = new byte[linear.Length / 3 * 4];
        for (var pixel = 0; pixel < linear.Length / 3; pixel++)
        {
            var r = linear[pixel * 3];
            var g = linear[pixel * 3 + 1];
            var b = linear[pixel * 3 + 2];
            var v = Math.Max(Math.Max(r, g), b);
            if (v <= 1e-32f)
            {
                continue;
            }

            var exponent = (int)Math.Ceiling(Math.Log2(v));
            var mantissa = Math.Pow(2, exponent - 8);
            var offset = pixel * 4;
            output[offset] = (byte)Math.Round(Math.Clamp(r / mantissa, 0, 255));
            output[offset + 1] = (byte)Math.Round(Math.Clamp(g / mantissa, 0, 255));
            output[offset + 2] = (byte)Math.Round(Math.Clamp(b / mantissa, 0, 255));
            output[offset + 3] = (byte)(exponent + 128);
        }

        return output;
    }

    /// <summary>
    /// 组装合法的 Radiance 文件：头部与 32 位 RLE 扫描线（每通道按不超过 128 的字面段编码）
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgbe">RGBE 像素字节</param>
    /// <returns>完整 .hdr 文件字节</returns>
    public static byte[] ComposeRadiance(int width, int height, byte[] rgbe)
    {
        var builder = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n");
        builder.Write(header);
        var useRle = width >= 8 && width <= 0x7FFF;
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width * 4;
            if (useRle)
            {
                builder.WriteByte(2);
                builder.WriteByte(2);
                builder.WriteByte((byte)(width >> 8));
                builder.WriteByte((byte)(width & 0xFF));
                for (var channel = 0; channel < 4; channel++)
                {
                    var remaining = width;
                    var column = channel;
                    while (remaining > 0)
                    {
                        var take = Math.Min(128, remaining);
                        builder.WriteByte((byte)take);
                        for (var step = 0; step < take; step++)
                        {
                            builder.WriteByte(rgbe[rowStart + (column + step * 4)]);
                        }

                        remaining -= take;
                    }
                }
            }
            else
            {
                builder.Write(rgbe, rowStart, width * 4);
            }
        }

        return builder.ToArray();
    }

    private static (int Width, int Height, float[] Pixels) ReadRadiance(ReadOnlySpan<byte> bytes)
    {
        var position = 0;
        var headerEnded = false;
        while (!headerEnded)
        {
            var lineEnd = bytes[(position..)].IndexOf((byte)'\n');
            if (lineEnd < 0) throw new InvalidDataException("Radiance 文件头不完整");
            var line = Encoding.ASCII.GetString(bytes.Slice(position, lineEnd).ToArray()).TrimEnd('\r');
            position += lineEnd + 1;
            headerEnded = line.Length == 0;
        }

        var resolutionEnd = bytes[(position..)].IndexOf((byte)'\n');
        if (resolutionEnd < 0) throw new InvalidDataException("Radiance 分辨率行缺失");
        var resolutionLine = Encoding.ASCII.GetString(bytes.Slice(position, resolutionEnd).ToArray())
            .TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        position += resolutionEnd + 1;
        if (resolutionLine.Length < 4 ||
            (!resolutionLine[0].StartsWith("-Y", StringComparison.Ordinal) &&
             !resolutionLine[0].StartsWith("+Y", StringComparison.Ordinal)) ||
            !int.TryParse(resolutionLine[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(resolutionLine[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
        {
            throw new InvalidDataException("Radiance 分辨率行无法解析");
        }

        if (width <= 0 || height <= 0 || width > 16384 || height > 8192)
        {
            throw new InvalidDataException("Radiance 分辨率缺失或超出支持范围");
        }

        var pixels = new float[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 4];
            if (position + 4 <= bytes.Length && bytes[position] == 2 && bytes[position + 1] == 2 &&
                (bytes[position + 2] << 8 | bytes[position + 3]) == width)
            {
                position += 4;
                ReadRleRow(bytes, ref position, width, row);
            }
            else
            {
                if (position + width * 4 > bytes.Length)
                {
                    throw new InvalidDataException("Radiance 平坦行数据越界");
                }

                bytes.Slice(position, width * 4).CopyTo(row);
                position += width * 4;
            }

            for (var x = 0; x < width; x++)
            {
                StorePixel(pixels, y * width + x, row[(x * 4)..]);
            }
        }

        return (width, height, pixels);
    }

    private static void ReadRleRow(ReadOnlySpan<byte> bytes, ref int position, int width, byte[] row)
    {
        if (position + width * 4 > bytes.Length)
        {
            throw new InvalidDataException("Radiance RLE 行数据越界");
        }

        for (var channel = 0; channel < 4; channel++)
        {
            var column = channel;
            var index = 0;
            while (index < width)
            {
                var run = bytes[position++];
                if (run > 128)
                {
                    var count = run - 128;
                    var value = bytes[position++];
                    for (var step = 0; step < count; step++)
                    {
                        row[(index + step) * 4 + column] = value;
                    }

                    index += count;
                }
                else
                {
                    for (var step = 0; step < run; step++)
                    {
                        row[(index + step) * 4 + column] = bytes[position++];
                    }

                    index += run;
                }
            }
        }
    }

    private static void StorePixel(float[] pixels, int index, ReadOnlySpan<byte> rgbe)
    {
        var target = index * 3;
        var exponent = rgbe[3];
        if (exponent == 0)
        {
            return;
        }

        var scale = MathF.Pow(2.0F, exponent - 136.0F);
        pixels[target] = rgbe[0] * scale;
        pixels[target + 1] = rgbe[1] * scale;
        pixels[target + 2] = rgbe[2] * scale;
    }
}
