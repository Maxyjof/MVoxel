using System.IO.Compression;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// Minecraft 结构 .schem（Structure Block NBT，字符串方块 ID）导入。
/// 与整世界导入互补：.schem 是单个可分享结构蓝图，palette 用 "minecraft:xxx" 全名，
/// 直接映射到项目调色板着色
/// 外部文件一律不可信：尺寸与 block_data 先校验上限再分配，损坏或越界一律抛 InvalidDataException
/// 坐标映射：结构为 Y 轴向上，与项目世界同向，x/y/z 直接对应并加导入原点
/// </summary>
public static class SchematicFormat
{
    /// <summary>
    /// 单轴允许的最大格数
    /// </summary>
    public const int MaximumAxisExtent = 512;

    /// <summary>
    /// 单结构允许的最大方块数，先校验后分配
    /// </summary>
    public const int MaximumBlocks = 4_000_000;

    /// <summary>
    /// 从磁盘读取 .schem（自动识别 gzip 与裸 NBT）
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>包含全部非空气方块的新世界</returns>
    public static VoxelWorld Read(string path) => Read(File.ReadAllBytes(path), VoxelCoord64.Zero);

    /// <summary>
    /// 从字节解析 .schem
    /// </summary>
    /// <param name="fileBytes">完整文件字节（gzip 或裸 NBT）</param>
    /// <param name="origin">导入摆放的世界原点</param>
    /// <returns>包含全部非空气方块的新世界</returns>
    public static VoxelWorld Read(byte[] fileBytes, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        if (fileBytes.Length < 2)
        {
            throw new InvalidDataException("Not a Minecraft .schem file.");
        }

        var nbt = Decompress(fileBytes);
        var root = new NbtDecoder(nbt).ReadRoot();

        if (!root.TryGetList("size", out var sizeList) || sizeList.Items.Count != 3)
        {
            throw new InvalidDataException(".schem is missing a 3-component size list.");
        }

        var sizeX = ShortOf(sizeList.Items[0]);
        var sizeY = ShortOf(sizeList.Items[1]);
        var sizeZ = ShortOf(sizeList.Items[2]);
        if (sizeX is <= 0 or > MaximumAxisExtent || sizeY is <= 0 or > MaximumAxisExtent ||
            sizeZ is <= 0 or > MaximumAxisExtent ||
            (long)sizeX * sizeY * sizeZ > MaximumBlocks)
        {
            throw new InvalidDataException(".schem size exceeds supported bounds.");
        }

        if (!root.TryGetList("palette", out var paletteList) || paletteList.Items.Count == 0)
        {
            throw new InvalidDataException(".schem is missing a palette.");
        }

        var palette = new string[paletteList.Items.Count];
        for (var index = 0; index < palette.Length; index++)
        {
            if (paletteList.Items[index] is not NbtCompound entry)
            {
                throw new InvalidDataException(".schem palette entry is not a compound.");
            }

            palette[index] = entry.TryGetString("Name", out var name) ? name : "minecraft:air";
        }

        if (!root.TryGetByteArray("block_data", out var blockData))
        {
            throw new InvalidDataException(".schem is missing block_data.");
        }

        var total = (long)sizeX * sizeY * sizeZ;
        var world = new VoxelWorld();
        var cursor = 0;
        for (var position = 0L; position < total && cursor < blockData.Length; position++)
        {
            var paletteIndex = ReadVarInt(blockData, ref cursor);
            if (paletteIndex < 0 || paletteIndex >= palette.Length)
            {
                throw new InvalidDataException(".schem block_data references an out-of-range palette entry.");
            }

            var blockName = palette[paletteIndex];
            if (IsAir(blockName))
            {
                continue;
            }

            // block_data 顺序为 x 最快、随后 z、最后 y（Minecraft 结构容器索引）
            var x = (int)(position % sizeX);
            var plane = position / sizeX;
            var z = (int)(plane % sizeZ);
            var y = plane / sizeZ;
            var flags = (byte)0;
            if (MinecraftBlockColors.IsTranslucent(blockName)) flags |= VoxelFlags.Translucent;
            if (MinecraftBlockColors.IsEmissive(blockName)) flags |= VoxelFlags.Emissive;
            world.Set(origin + new VoxelCoord64(x, y, z),
                new VoxelValue(MinecraftBlockColors.ColorOf(blockName), 0, flags));
        }

        return world;
    }

    /// <summary>
    /// gzip 魔数识别后解压，否则按裸 NBT 返回
    /// </summary>
    private static byte[] Decompress(byte[] fileBytes)
    {
        if (!(fileBytes.Length > 2 && fileBytes[0] == 0x1F && fileBytes[1] == 0x8B))
        {
            return fileBytes;
        }

        using var compressed = new MemoryStream(fileBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// 读取无符号 LEB128 varint（palette 索引编码）
    /// </summary>
    private static int ReadVarInt(byte[] data, ref int cursor)
    {
        var value = 0;
        var shift = 0;
        while (true)
        {
            if (cursor >= data.Length)
            {
                throw new InvalidDataException(".schem block_data varint is truncated.");
            }

            var current = data[cursor++];
            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 28)
            {
                throw new InvalidDataException(".schem block_data varint is too long.");
            }
        }
    }

    private static int ShortOf(NbtNode node) => node switch
    {
        NbtShort shortNode => shortNode.Value,
        NbtByte byteNode => byteNode.Value,
        NbtInt intNode => intNode.Value,
        _ => throw new InvalidDataException(".schem size component is not a short."),
    };

    private static bool IsAir(string blockName) =>
        blockName.EndsWith(":air", StringComparison.Ordinal) ||
        blockName is "minecraft:cave_air" or "minecraft:void_air" or "minecraft:structure_void";
}
