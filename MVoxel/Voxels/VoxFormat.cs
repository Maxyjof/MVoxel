using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// MagicaVoxel .vox 交换模型（单 SIZE/XYZA 模型）的导入导出。
/// 项目内部资源使用 .mvmodel，不通过本类保存。
/// 外部文件一律不可信：所有计数在分配内存前先做上限校验，
/// 截断、超大或越界的文件只会得到异常，绝不会拖垮编辑器
/// 坐标映射：world.X = vox.X，world.Y = vox.Z（vox 为 Z 轴向上）
/// </summary>
public static class VoxFormat
{
    /// <summary>
    /// 文件魔数，ASCII "VOX " 的小端 32 位
    /// </summary>
    public const uint Magic = 0x0058_4F56;

    /// <summary>
    /// 单文件允许的最大体素数，先校验后分配
    /// </summary>
    public const int MaximumVoxels = 4_000_000;

    /// <summary>
    /// 单轴允许的最大格数
    /// </summary>
    public const int MaximumAxisExtent = 1024;

    /// <summary>
    /// 把体素世界导出为外部 .vox 文件，世界按包围盒重锚到原点。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">要导出的世界，必须非空</param>
    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            throw new InvalidOperationException("Cannot export an empty voxel world.");
        }

        var sizeX = (int)(maximum.X - minimum.X) + 1;
        var sizeY = (int)(maximum.Z - minimum.Z) + 1; // vox 深度对应 world Z
        var sizeZ = (int)(maximum.Y - minimum.Y) + 1; // vox 高度对应 world Y
        if (sizeX > MaximumAxisExtent || sizeY > MaximumAxisExtent || sizeZ > MaximumAxisExtent)
        {
            throw new InvalidOperationException("Voxel model exceeds the single-file .vox extent limit.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(150);
        // 磁盘上的块布局：id(4) + contentSize(4) + childrenSize(4) + 内容
        WriteAscii(writer, "MAIN");
        writer.Write(0);
        writer.Write(0);
        WriteAscii(writer, "SIZE");
        writer.Write(12);
        writer.Write(0);
        writer.Write(sizeX);
        writer.Write(sizeY);
        writer.Write(sizeZ);
        var voxels = world.EnumerateOccupied().ToArray();
        WriteAscii(writer, "XYZA");
        writer.Write(4 + voxels.Length * 7);
        writer.Write(0);
        writer.Write(voxels.Length);
        foreach (var (coordinate, value) in voxels)
        {
            writer.Write((byte)(coordinate.X - minimum.X));
            writer.Write((byte)(coordinate.Z - minimum.Z));
            writer.Write((byte)(coordinate.Y - minimum.Y));
            writer.Write(value.Color.R);
            writer.Write(value.Color.G);
            writer.Write(value.Color.B);
            writer.Write(value.Color.A);
        }

        writer.Flush();
        AtomicFile.WriteBytes(path, stream.ToArray());
    }

    /// <summary>
    /// 从磁盘读取外部 .vox 文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>包含全部体素的新世界</returns>
    public static VoxelWorld Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes, VoxelCoord64.Zero);
    }

    /// <summary>
    /// 从字节解析 .vox 文件，非法结构一律抛 InvalidDataException
    /// </summary>
    /// <param name="bytes">完整文件字节</param>
    /// <param name="origin">导入摆放的世界原点</param>
    /// <returns>包含全部体素的新世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (bytes.Length < 8 || reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a VoxEdit/MagicaVoxel .vox file.");
        }

        reader.ReadUInt32(); // 引擎版本号，任何版本都接受
        var world = new VoxelWorld();
        var size = (X: 0, Y: 0, Z: 0);
        var cursor = 8L;
        while (cursor + 8 <= bytes.Length)
        {
            stream.Position = cursor;
            var chunkId = new string(reader.ReadChars(4));
            var contentSize = reader.ReadInt32();
            var childrenSize = reader.ReadInt32();
            if (contentSize < 0 || childrenSize < 0 ||
                contentSize > bytes.Length || childrenSize > bytes.Length)
            {
                throw new InvalidDataException($"Corrupt .vox chunk size for '{chunkId}'.");
            }

            var next = cursor + 12 + contentSize + childrenSize;
            if (next > bytes.Length || next <= cursor)
            {
                throw new InvalidDataException(".vox chunk extends beyond the file.");
            }

            switch (chunkId)
            {
                case "SIZE":
                {
                    size.X = reader.ReadInt32();
                    size.Y = reader.ReadInt32();
                    size.Z = reader.ReadInt32();
                    if (size.X is < 1 or > MaximumAxisExtent ||
                        size.Y is < 1 or > MaximumAxisExtent ||
                        size.Z is < 1 or > MaximumAxisExtent)
                    {
                        throw new InvalidDataException(".vox SIZE exceeds supported bounds.");
                    }

                    break;
                }
                case "XYZI" or "XYZA":
                {
                    var count = reader.ReadInt32();
                    var bytesPerVoxel = chunkId == "XYZA" ? 7 : 4;
                    if (count is < 0 or > MaximumVoxels ||
                        (long)count * bytesPerVoxel > contentSize - 4)
                    {
                        throw new InvalidDataException(".vox voxel count is inconsistent with its chunk size.");
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var x = reader.ReadByte();
                        var y = reader.ReadByte();
                        var z = reader.ReadByte();
                        byte r, g, b, a;
                        if (chunkId == "XYZA")
                        {
                            r = reader.ReadByte();
                            g = reader.ReadByte();
                            b = reader.ReadByte();
                            a = reader.ReadByte();
                        }
                        else
                        {
                            // 老格式只有调色板索引，用近似色表展开
                            var colorIndex = reader.ReadByte();
                            (r, g, b) = LegacyColor(colorIndex);
                            a = colorIndex == 0 ? (byte)0 : (byte)255;
                        }

                        // 索引 0（或全透明）表示空气，故意不写入世界
                        if (a == 0) continue;
                        world.Set(origin + new VoxelCoord64(x, z, y),
                            new VoxelValue(new Rgba32(r, g, b, a), Flags: a < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
                    }

                    break;
                }
            }

            cursor = next;
        }

        return world;
    }

    /// <summary>
    /// 写入四字节 ASCII 块标识
    /// </summary>
    /// <param name="writer">目标写入器</param>
    /// <param name="value">恰好四个字符的标识</param>
    private static void WriteAscii(BinaryWriter writer, string value)
    {
        foreach (var character in value) writer.Write(character);
    }

    /// <summary>
    /// 将 256 色 .vox 调色板索引展开为 RGB 颜色
    /// </summary>
    /// <param name="index">调色板索引</param>
    /// <returns>近似 RGB 颜色</returns>
    private static (byte, byte, byte) LegacyColor(int index) => index switch
    {
        0 => (0, 0, 0),
        1 => (255, 255, 255),
        2 => (170, 170, 170),
        3 => (85, 85, 85),
        4 => (255, 0, 0),
        5 => (255, 170, 0),
        6 => (255, 255, 0),
        7 => (0, 255, 0),
        8 => (0, 255, 255),
        9 => (0, 0, 255),
        10 => (255, 0, 255),
        11 => (85, 85, 255),
        12 => (170, 85, 0),
        13 => (170, 127, 85),
        14 => (127, 170, 0),
        15 => (255, 127, 39),
        _ => ((byte)(index * 37 % 256), (byte)(index * 73 % 256), (byte)(index * 113 % 256)),
    };
}
