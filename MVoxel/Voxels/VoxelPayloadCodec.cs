using System.IO.Compression;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 体素负载的压缩编解码：把坐标与可选体素值写成 gzip 压缩的 Base64
/// </summary>
/// <remarks>
/// 程序化组件的隐藏体素与瓷砖源快照共用同一份格式。坐标按字典序排列后写成相邻差值，
/// 逐格值可选用，几十万格通常只有几百 KB。格式只读写当前版本，不做向后兼容
/// </remarks>
internal static class VoxelPayloadCodec
{
    /// <summary>单份负载允许的格数上限，防止损坏文件把内存撑爆</summary>
    internal const int MaximumCellCount = 16_000_000;

    /// <summary>负载格式版本</summary>
    internal const int FormatVersion = 2;

    /// <summary>负载标志位：坐标之后是否逐格保存体素值</summary>
    private const byte ValueFlags = 1;

    private static readonly byte[] Magic = [(byte)'M', (byte)'V', (byte)'G', (byte)'V'];

    /// <summary>
    /// 把已排序的坐标与值编码成压缩 Base64
    /// </summary>
    /// <param name="cells">坐标与值，必须已按 X、Y、Z 升序排列</param>
    /// <param name="includeValues">是否逐格写入颜色、材质与标记</param>
    /// <returns>Base64 文本</returns>
    internal static string Encode(IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Value)> cells,
        bool includeValues)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(Magic);
            writer.Write((byte)FormatVersion);
            writer.Write(includeValues ? ValueFlags : (byte)0);
            writer.Write(cells.Count);
            long x = 0;
            long y = 0;
            long z = 0;
            foreach (var (coordinate, value) in cells)
            {
                WriteSigned(writer, coordinate.X - x);
                WriteSigned(writer, coordinate.Y - y);
                WriteSigned(writer, coordinate.Z - z);
                x = coordinate.X;
                y = coordinate.Y;
                z = coordinate.Z;
                if (!includeValues) continue;
                writer.Write(value.Color.R);
                writer.Write(value.Color.G);
                writer.Write(value.Color.B);
                writer.Write(value.Color.A);
                writer.Write(value.MaterialSlot);
                writer.Write(value.Flags);
            }
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// 解码压缩 Base64；格式不符、版本不符或数据损坏时返回 false
    /// </summary>
    /// <param name="base64">负载文本</param>
    /// <param name="fallbackValue">负载不含逐格值时使用的体素值</param>
    /// <param name="world">解出的体素世界</param>
    /// <param name="hasValues">负载是否带逐格值</param>
    /// <returns>是否解码成功</returns>
    internal static bool TryDecode(string base64, VoxelValue fallbackValue, out VoxelWorld world,
        out bool hasValues)
    {
        world = new VoxelWorld();
        hasValues = false;
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            var compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            Span<byte> header = stackalloc byte[6];
            if (reader.Read(header) != header.Length) return false;
            for (var index = 0; index < Magic.Length; index++)
                if (header[index] != Magic[index]) return false;
            if (header[4] != FormatVersion) return false;
            hasValues = (header[5] & ValueFlags) != 0;
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumCellCount) return false;
            var writer = world.CreateBulkWriter();
            long x = 0;
            long y = 0;
            long z = 0;
            for (var index = 0; index < count; index++)
            {
                x = checked(x + ReadSigned(reader));
                y = checked(y + ReadSigned(reader));
                z = checked(z + ReadSigned(reader));
                var value = fallbackValue;
                if (hasValues)
                    value = new VoxelValue(
                        new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
                        reader.ReadUInt16(), reader.ReadByte());
                writer.Set(new VoxelCoord64(x, y, z), value);
            }

            return true;
        }
        catch (Exception exception) when (exception is FormatException or IOException or InvalidDataException or
            OverflowException or EndOfStreamException or ArgumentException)
        {
            world = new VoxelWorld();
            hasValues = false;
            return false;
        }
    }

    /// <summary>
    /// 计算负载文本的稳定摘要，用于参数指纹；不用字符串自带的哈希，它每个进程都不同
    /// </summary>
    /// <param name="text">负载文本</param>
    /// <returns>64 位摘要，空文本返回 0</returns>
    internal static ulong StableHash(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0UL;
        var hash = 14695981039346656037UL;
        foreach (var character in text)
        {
            unchecked
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }

    private static void WriteSigned(BinaryWriter writer, long value)
    {
        var zigzag = (ulong)((value << 1) ^ (value >> 63));
        while (zigzag >= 0x80)
        {
            writer.Write((byte)(zigzag | 0x80));
            zigzag >>= 7;
        }

        writer.Write((byte)zigzag);
    }

    private static long ReadSigned(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (shift > 63) throw new InvalidDataException("生成体素坐标差值过长");
            var current = reader.ReadByte();
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) break;
            shift += 7;
        }

        return (long)(result >> 1) ^ -(long)(result & 1);
    }
}
