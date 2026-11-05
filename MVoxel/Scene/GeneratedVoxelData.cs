using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 程序化组件生成体素的隐藏负载读写，曲线渲染器与树生成器共用
/// </summary>
/// <remarks>
/// 坐标按字典序排列后写成相邻差值，再用 gzip 压缩加 Base64 存进组件 JSON，
/// 几十万格通常只有几百 KB。负载有两种形态：只存坐标时颜色来自组件参数，
/// 适合曲线渲染器（改颜色必然改指纹并重算）；需要保留每格颜色时额外写入体素值，
/// 适合树生成器（允许参数改了但还没点生成，旧树要原样留着）。
/// 只有对应的组件自己会读写这份数据，检查器和绘制工具都不会暴露它
/// </remarks>
internal static class GeneratedVoxelData
{
    /// <summary>隐藏负载在组件 JSON 里的字段名</summary>
    internal const string DataPropertyName = "generatedVoxels";

    /// <summary>生成参数指纹在组件 JSON 里的字段名</summary>
    internal const string SignaturePropertyName = "generatedSignature";

    /// <summary>负载格式版本</summary>
    private const int FormatVersion = 2;

    /// <summary>负载标志位：坐标之后是否逐格保存体素值</summary>
    private const byte ValueFlags = 1;

    private const int MaximumCellCount = 16_000_000;
    private static readonly byte[] Magic = [(byte)'M', (byte)'V', (byte)'G', (byte)'V'];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 判断组件里是否存有生成体素
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>是否存在负载对象</returns>
    internal static bool HasData(ComponentRecord component) =>
        component.Data.ValueKind == JsonValueKind.Object &&
        component.Data.TryGetProperty(DataPropertyName, out var data) && data.ValueKind == JsonValueKind.Object;

    /// <summary>
    /// 读取生成体素与指纹，数据缺失、损坏或版本不符时返回 false
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="fallbackValue">负载只存坐标时使用的体素值</param>
    /// <param name="world">读出的局部体素世界</param>
    /// <param name="signature">负载写入时的参数指纹</param>
    /// <returns>是否读取成功</returns>
    internal static bool TryRead(ComponentRecord component, VoxelValue fallbackValue, out VoxelWorld world,
        out ulong signature)
    {
        world = new VoxelWorld();
        signature = 0UL;
        if (!HasData(component)) return false;
        if (component.Data.TryGetProperty(SignaturePropertyName, out var signatureElement) &&
            signatureElement.ValueKind == JsonValueKind.String)
            _ = ulong.TryParse(signatureElement.GetString(), System.Globalization.NumberStyles.HexNumber, null,
                out signature);
        var payload = component.Data.GetProperty(DataPropertyName);
        if (!payload.TryGetProperty("cells", out var cellsElement) || cellsElement.ValueKind != JsonValueKind.String)
            return false;
        try
        {
            var compressed = Convert.FromBase64String(cellsElement.GetString() ?? string.Empty);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            Span<byte> header = stackalloc byte[6];
            if (reader.Read(header) != header.Length) return false;
            for (var index = 0; index < Magic.Length; index++)
                if (header[index] != Magic[index]) return false;
            if (header[4] != FormatVersion) return false;
            var flags = header[5];
            var hasValues = (flags & ValueFlags) != 0;
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
            return false;
        }
    }

    /// <summary>
    /// 写出生成体素与指纹，保留组件里的其他字段
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="world">局部体素世界</param>
    /// <param name="signature">生成参数指纹</param>
    /// <param name="includeValues">是否逐格保存颜色、材质与标记</param>
    internal static void Write(ComponentRecord component, VoxelWorld world, ulong signature, bool includeValues)
    {
        ArgumentNullException.ThrowIfNull(world);
        var cells = world.EnumerateOccupied()
            .OrderBy(item => item.Coordinate.X).ThenBy(item => item.Coordinate.Y).ThenBy(item => item.Coordinate.Z)
            .ToArray();
        var encoded = Encode(cells, includeValues);
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged[DataPropertyName] = new JsonObject
        {
            ["version"] = FormatVersion,
            ["count"] = cells.Length,
            ["values"] = includeValues,
            ["cells"] = encoded,
        };
        merged[SignaturePropertyName] = signature.ToString("x16");
        component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>
    /// 删除生成体素与指纹
    /// </summary>
    /// <param name="component">组件记录</param>
    internal static void Remove(ComponentRecord component)
    {
        if (component.Data.ValueKind != JsonValueKind.Object) return;
        var merged = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        if (merged.Remove(DataPropertyName) | merged.Remove(SignaturePropertyName))
            component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>
    /// 只读取负载里记录的参数指纹，用于检查器判断参数是否已经生效
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="signature">参数指纹</param>
    /// <returns>是否存在指纹</returns>
    internal static bool TryReadSignature(ComponentRecord component, out ulong signature)
    {
        signature = 0UL;
        if (!HasData(component)) return false;
        if (!component.Data.TryGetProperty(SignaturePropertyName, out var value) ||
            value.ValueKind != JsonValueKind.String) return false;
        return ulong.TryParse(value.GetString(), System.Globalization.NumberStyles.HexNumber, null, out signature);
    }

    /// <summary>
    /// 估算负载的文本长度，用于检查器显示数据体积
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>Base64 字符数，没有负载时返回 0</returns>
    internal static int GetEncodedLength(ComponentRecord component)
    {
        if (!HasData(component)) return 0;
        var payload = component.Data.GetProperty(DataPropertyName);
        return payload.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.String
            ? (cells.GetString()?.Length ?? 0) : 0;
    }

    /// <summary>
    /// 坐标与可选体素值编码成压缩 Base64
    /// </summary>
    /// <param name="cells">已排序的坐标与值</param>
    /// <param name="includeValues">是否写入体素值</param>
    /// <returns>Base64 文本</returns>
    private static string Encode((VoxelCoord64 Coordinate, VoxelValue Value)[] cells, bool includeValues)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(Magic);
            writer.Write((byte)FormatVersion);
            writer.Write(includeValues ? ValueFlags : (byte)0);
            writer.Write(cells.Length);
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
