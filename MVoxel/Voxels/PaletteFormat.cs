namespace MVoxel.Voxels;

/// <summary>
/// 调色板文件的读写，扩展名 .palette。磁盘布局与 MagicaVoxel 的 .pal 完全兼容：
/// 固定 256 项、每项 4 字节 BGRA、无文件头，可直接导入外部编辑器的色板
/// </summary>
public static class PaletteFormat
{
    /// <summary>
    /// 单个调色板文件的固定字节数，256 项乘以每项 4 字节
    /// </summary>
    public const int FileSize = VoxelPalette.EntryCount * 4;

    /// <summary>
    /// 把调色板原子写入 .palette 文件，写入失败或回读不一致时抛出 IOException
    /// </summary>
    /// <param name="path">目标文件路径，所在目录会自动创建</param>
    /// <param name="colors">必须恰好包含 256 项的色表</param>
    public static void Save(string path, Rgba32[] colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Length != VoxelPalette.EntryCount)
        {
            throw new ArgumentException($"调色板必须恰好 {VoxelPalette.EntryCount} 项", nameof(colors));
        }

        var bytes = new byte[FileSize];
        for (var index = 0; index < colors.Length; index++)
        {
            var color = colors[index];
            // MagicaVoxel 的 .pal 按 BGRA 存放
            bytes[index * 4] = color.B;
            bytes[index * 4 + 1] = color.G;
            bytes[index * 4 + 2] = color.R;
            bytes[index * 4 + 3] = color.A;
        }

        MVoxel.Core.AtomicFile.WriteBytes(path, bytes);
    }

    /// <summary>
    /// 读取 .palette 文件。文件长度不符合 1024 字节的固定布局时抛出
    /// InvalidDataException，绝不按文件内容动态放大分配
    /// </summary>
    /// <param name="path">调色板文件路径</param>
    /// <returns>256 项色表</returns>
    public static Rgba32[] Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    /// <summary>
    /// 解析调色板字节，供文件读取和测试共用
    /// </summary>
    /// <param name="bytes">必须恰好为 <see cref="FileSize"/> 字节的 BGRA 数据</param>
    /// <returns>256 项色表</returns>
    public static Rgba32[] Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != FileSize)
        {
            throw new InvalidDataException($"调色板文件应为 {FileSize} 字节，实际 {bytes.Length} 字节");
        }

        var colors = new Rgba32[VoxelPalette.EntryCount];
        for (var index = 0; index < colors.Length; index++)
        {
            colors[index] = new Rgba32(
                bytes[index * 4 + 2],
                bytes[index * 4 + 1],
                bytes[index * 4],
                bytes[index * 4 + 3]);
        }

        return colors;
    }
}
