using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>MagicaVoxel 单模型 SIZE/XYZI/RGBA 交换；内部项目使用原生世界格式。</summary>
public static class VoxFormat
{
    public const uint Magic = 0x0058_4F56;
    public const int MaximumVoxels = 4_000_000;
    public const int MaximumAxisExtent = 256;
    private const int MaximumFileBytes = 128 * 1024 * 1024;

    /// <summary>按占用包围盒重锚导出。超过单模型限制时拒绝，不裁剪或量化。</summary>
    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
            throw new InvalidOperationException("空模型不能导出 VOX，请先绘制方块。");
        var sx = checked(maximum.X - minimum.X + 1);
        var sy = checked(maximum.Z - minimum.Z + 1);
        var sz = checked(maximum.Y - minimum.Y + 1);
        if (sx > MaximumAxisExtent || sy > MaximumAxisExtent || sz > MaximumAxisExtent)
            throw new InvalidOperationException("VOX 单模型每轴最多 256 格；请导出较小的独立模型，或使用 QB/原生项目保留大场景。");
        if (world.OccupiedVoxelCount > MaximumVoxels)
            throw new InvalidOperationException("VOX 体素数量超过导出上限。");
        var voxels = world.EnumerateOccupied().ToArray();
        var palette = new Dictionary<Rgba32, byte>();
        foreach (var cell in voxels)
        {
            if (palette.ContainsKey(cell.Value.Color)) continue;
            if (palette.Count >= 255)
                throw new InvalidOperationException("标准 VOX 单模型最多 255 个非空颜色；为避免颜色失真，请减少颜色或改用 QB。");
            palette[cell.Value.Color] = (byte)(palette.Count + 1);
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(Magic); writer.Write(150);
        Header(writer, "MAIN", 0, checked(24 + 16 + voxels.Length * 4 + 1036));
        Header(writer, "SIZE", 12);
        writer.Write((int)sx); writer.Write((int)sy); writer.Write((int)sz);
        Header(writer, "XYZI", 4 + voxels.Length * 4);
        writer.Write(voxels.Length);
        foreach (var cell in voxels)
        {
            writer.Write((byte)(cell.Coordinate.X - minimum.X));
            writer.Write((byte)(cell.Coordinate.Z - minimum.Z));
            writer.Write((byte)(cell.Coordinate.Y - minimum.Y));
            writer.Write(palette[cell.Value.Color]);
        }
        Header(writer, "RGBA", 1024);
        foreach (var color in palette.Keys)
        {
            writer.Write(color.R); writer.Write(color.G); writer.Write(color.B); writer.Write(color.A);
        }
        for (var index = palette.Count; index < 256; index++) writer.Write(0U);
        writer.Flush();
        AtomicFile.WriteBytes(path, stream.ToArray());
    }

    public static VoxelWorld Read(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("VOX 文件超过读取上限。");
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>读取单模型与完整色板；多模型/动画拒绝，避免错误重叠合并。兼读旧 XYZA 交换数据。</summary>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 20 || bytes.Length > MaximumFileBytes) throw new InvalidDataException("VOX 文件长度无效。");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("不是 VOX 文件。");
        reader.ReadUInt32();
        var palette = DefaultPalette();
        var cells = new List<(VoxelCoord64 Coordinate, byte Index, Rgba32? Color)>();
        var size = (X: 0, Y: 0, Z: 0);
        var modelCount = 0;
        void Chunks(long start, long end, int depth)
        {
            if (depth > 32) throw new InvalidDataException("VOX 块嵌套过深。");
            var cursor = start;
            while (cursor < end)
            {
                if (end - cursor < 12) throw new InvalidDataException("VOX 块头被截断。");
                stream.Position = cursor;
                var id = new string(reader.ReadChars(4));
                var contentSize = reader.ReadInt32(); var childrenSize = reader.ReadInt32();
                var contentEnd = cursor + 12L + contentSize;
                var next = contentEnd + childrenSize;
                if (contentSize < 0 || childrenSize < 0 || next > end)
                    throw new InvalidDataException("VOX 块越过父块边界。");
                switch (id)
                {
                    case "PACK":
                        if (contentSize != 4 || reader.ReadInt32() != 1)
                            throw new InvalidDataException("当前仅支持单模型 VOX；请在 MagicaVoxel 中将模型分别导出。");
                        break;
                    case "SIZE":
                        if (contentSize != 12) throw new InvalidDataException("VOX SIZE 长度无效。");
                        size = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                        if (size.X is < 1 or > MaximumAxisExtent || size.Y is < 1 or > MaximumAxisExtent || size.Z is < 1 or > MaximumAxisExtent)
                            throw new InvalidDataException("VOX 单模型尺寸必须在 1–256 格之间。");
                        break;
                    case "XYZI" or "XYZA":
                        if (++modelCount > 1) throw new InvalidDataException("当前仅支持单模型 VOX，不会将多个模型错误重叠导入。");
                        if (contentSize < 4 || size.X == 0) throw new InvalidDataException("VOX 缺少有效 SIZE。");
                        var count = reader.ReadInt32();
                        var stride = id == "XYZA" ? 7 : 4;
                        if (count is < 0 or > MaximumVoxels || count * (long)stride != contentSize - 4L)
                            throw new InvalidDataException("VOX 体素数量与块长度不符。");
                        cells.Capacity = count;
                        for (var index = 0; index < count; index++)
                        {
                            var x = reader.ReadByte(); var y = reader.ReadByte(); var z = reader.ReadByte();
                            if (x >= size.X || y >= size.Y || z >= size.Z) throw new InvalidDataException("VOX 坐标超出 SIZE。");
                            var coordinate = origin + new VoxelCoord64(x, z, y);
                            if (id == "XYZA") cells.Add((coordinate, 0, new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte())));
                            else cells.Add((coordinate, reader.ReadByte(), null));
                        }
                        break;
                    case "RGBA":
                        if (contentSize != 1024) throw new InvalidDataException("VOX 色板长度无效。");
                        for (var index = 1; index <= 256; index++)
                        {
                            var color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            if (index < 256) palette[index] = color;
                        }
                        break;
                }
                if (childrenSize > 0) Chunks(contentEnd, next, depth + 1);
                cursor = next;
            }
        }
        Chunks(8, bytes.Length, 0);
        if (modelCount == 0) throw new InvalidDataException("VOX 文件没有模型数据。");
        var world = new VoxelWorld();
        foreach (var cell in cells)
        {
            var color = cell.Color ?? palette[cell.Index];
            if (color.A == 0) continue;
            world.Set(cell.Coordinate, new VoxelValue(color, Flags: color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        }
        return world;
    }

    private static void Header(BinaryWriter writer, string id, int content, int children = 0)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(id));
        writer.Write(content); writer.Write(children);
    }

    // 官方默认色板：6³ 色立方（不含黑色）加红/绿/蓝/灰各十级。
    // https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox.txt
    private static Rgba32[] DefaultPalette()
    {
        var palette = new Rgba32[256];
        var index = 1;
        for (var r = 255; r >= 0; r -= 51)
        for (var g = 255; g >= 0; g -= 51)
        for (var b = 255; b >= 0; b -= 51)
            if (r != 0 || g != 0 || b != 0) palette[index++] = new Rgba32((byte)r, (byte)g, (byte)b, 255);
        byte[] shades = [238, 221, 187, 170, 136, 119, 85, 68, 34, 17];
        for (var channel = 0; channel < 4; channel++)
        foreach (var value in shades)
            palette[index++] = new Rgba32(channel is 0 or 3 ? value : (byte)0,
                channel is 1 or 3 ? value : (byte)0, channel is 2 or 3 ? value : (byte)0, 255);
        return palette;
    }
}
