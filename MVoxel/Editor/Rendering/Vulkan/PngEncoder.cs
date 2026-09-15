using System.Buffers.Binary;
using System.IO.Compression;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// 将 Vulkan 回读的 RGBA8 像素编码为无贴图依赖的 PNG 文件
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// 编码连续存储的 RGBA8 像素
    /// </summary>
    /// <param name="rgba">按行连续存储的 RGBA8 数据</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <returns>完整 PNG 文件字节</returns>
    internal static byte[] EncodeRgba8(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA8 像素尺寸与数据长度不一致", nameof(rgba));
        }

        var scanlineSize = checked((width * 4 + 1) * height);
        byte[] scanlines = new byte[scanlineSize];
        for (var row = 0; row < height; row++)
        {
            var targetOffset = row * (width * 4 + 1);
            scanlines[targetOffset] = 0;
            rgba.Slice(row * width * 4, width * 4).CopyTo(scanlines.AsSpan(targetOffset + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        using var output = new MemoryStream(checked(Signature.Length + (int)compressed.Length + 64));
        output.Write(Signature);
        WriteChunk(output, "IHDR", CreateHeader(width, height));
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>
    /// 创建 RGBA8 PNG 的 IHDR 数据
    /// </summary>
    private static byte[] CreateHeader(int width, int height)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    /// <summary>
    /// 写入一个带 CRC 校验的 PNG 数据块
    /// </summary>
    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>
    /// 计算 PNG 数据块使用的 CRC32
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in first)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (var value in second)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    /// <summary>
    /// 更新单个字节的 CRC32 状态
    /// </summary>
    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        return crc;
    }
}
