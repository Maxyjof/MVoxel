using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// 已打开的 Anvil 区块文件，从文件名解析区域坐标并缓存全部字节
/// </summary>
public sealed class AnvilRegionFile : IDisposable
{
    private byte[] _data;
    private bool _disposed;

    private AnvilRegionFile(string path, int regionX, int regionZ, byte[] data)
    {
        Path = path;
        RegionX = regionX;
        RegionZ = regionZ;
        _data = data;
    }

    /// <summary>
    /// 区块文件路径
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 区域 X 坐标，来自文件名 r.X.Z
    /// </summary>
    public int RegionX { get; }

    /// <summary>
    /// 区域 Z 坐标，来自文件名 r.X.Z
    /// </summary>
    public int RegionZ { get; }

    /// <summary>
    /// 从文件路径打开并读取整个区域文件
    /// </summary>
    /// <param name="path">r.X.Z.mca 文件路径</param>
    /// <returns>已打开的区域文件</returns>
    public static AnvilRegionFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var fileName = System.IO.Path.GetFileName(path);
        var parts = fileName.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], "r", StringComparison.Ordinal)
            || !string.Equals(parts[3], "mca", StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var regionX)
            || !int.TryParse(parts[2], out var regionZ))
        {
            throw new ArgumentException($"区域文件名不符合 r.X.Z.mca 规范：{fileName}", nameof(path));
        }

        var data = File.ReadAllBytes(path);
        if (data.Length < 8192)
        {
            throw new InvalidDataException($"区域文件过小，缺少扇区表：{fileName}");
        }

        return new AnvilRegionFile(path, regionX, regionZ, data);
    }

    /// <summary>
    /// 判断指定局部坐标的区块是否已生成
    /// </summary>
    /// <param name="localX">局部 X（0..31）</param>
    /// <param name="localZ">局部 Z（0..31）</param>
    /// <returns>扇区偏移非零时为真</returns>
    public bool ChunkExists(int localX, int localZ)
    {
        var (offset, _) = ReadSectorHeader(localX, localZ);
        return offset != 0;
    }

    /// <summary>
    /// 读取指定局部坐标区块的 NBT 根 compound，未生成时返回 null
    /// </summary>
    /// <param name="localX">局部 X（0..31）</param>
    /// <param name="localZ">局部 Z（0..31）</param>
    /// <returns>区块 NBT 根，或 null</returns>
    public NbtCompound? ReadChunk(int localX, int localZ)
    {
        var (offset, _) = ReadSectorHeader(localX, localZ);
        if (offset == 0)
        {
            return null;
        }

        var chunkStart = (long)offset * 4096;
        if (chunkStart + 5 > _data.Length)
        {
            throw new InvalidDataException("区块扇区起始位置越界");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan((int)chunkStart, 4));
        if (length < 1)
        {
            throw new InvalidDataException("区块数据长度非法");
        }

        var compressionType = _data[(int)chunkStart + 4];
        var dataStart = chunkStart + 5;
        if (dataStart + (length - 1) > _data.Length)
        {
            throw new InvalidDataException("区块压缩数据越界");
        }

        var compressed = _data.AsSpan((int)dataStart, length - 1).ToArray();
        var nbtBytes = DecompressChunk(compressed, compressionType);
        return NbtReader.Read(nbtBytes);
    }

    /// <summary>
    /// 释放缓存的区块文件字节
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _data = Array.Empty<byte>();
    }

    private (int Offset, int Count) ReadSectorHeader(int localX, int localZ)
    {
        if ((uint)localX >= 32 || (uint)localZ >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(localX), "局部坐标必须在 0..31 之间");
        }

        var index = (localX & 31) + (localZ & 31) * 32;
        var entry = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(index * 4, 4));
        var offset = (int)(entry >> 8);
        var count = (int)(entry & 0xFF);
        return (offset, count);
    }

    private static byte[] DecompressChunk(byte[] compressed, byte compressionType)
    {
        if (compressed.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (compressionType == 3)
        {
            return compressed;
        }

        Stream decompressor = compressionType switch
        {
            1 => new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress),
            2 => new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress),
            _ => throw new InvalidDataException($"不支持的区块压缩类型 {compressionType}"),
        };

        using (decompressor)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            const long max = 64 * 1024 * 1024;
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > max)
                {
                    throw new InvalidDataException("解压后的区块数据超出上限");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
    }
}

/// <summary>
/// 已解析的区块，包含世界坐标与全部 section
/// </summary>
public sealed class AnvilChunk
{
    internal AnvilChunk(int chunkX, int chunkZ, IReadOnlyList<AnvilSection> sections)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Sections = sections;
    }

    /// <summary>
    /// 世界区块 X 坐标
    /// </summary>
    public int ChunkX { get; }

    /// <summary>
    /// 世界区块 Z 坐标
    /// </summary>
    public int ChunkZ { get; }

    /// <summary>
    /// 区块内全部 section 的只读列表
    /// </summary>
    public IReadOnlyList<AnvilSection> Sections { get; }

    /// <summary>
    /// 从区块 NBT 根 compound 解析出区块对象
    /// </summary>
    /// <param name="root">区块 NBT 根</param>
    /// <returns>已解析区块</returns>
    public static AnvilChunk FromNbt(NbtCompound root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var chunkX = root.GetInt("xPos");
        var chunkZ = root.GetInt("zPos");
        var sections = new List<AnvilSection>();
        if (root.TryGetList("sections", out var list))
        {
            foreach (var item in list.Items)
            {
                if (item is NbtCompound sectionCompound)
                {
                    sections.Add(AnvilSection.FromNbt(sectionCompound));
                }
            }
        }

        return new AnvilChunk(chunkX, chunkZ, sections);
    }
}

/// <summary>
/// 区块内的一个 16x16x16 section，延迟解包方块位数据
/// </summary>
public sealed class AnvilSection
{
    private readonly IReadOnlyList<string> _palette;
    private readonly long[]? _data;
    private readonly int _bitsPerEntry;
    private readonly int _entriesPerLong;
    private string[]? _unpacked;

    /// <summary>
    /// 由 section 索引、palette 与位数据构造 section
    /// </summary>
    /// <param name="y">section 索引，可能为负</param>
    /// <param name="palette">palette 方块名称列表</param>
    /// <param name="data">位打包的 palette 索引，palette 只有一项时为 null</param>
    public AnvilSection(int y, IReadOnlyList<string> palette, long[]? data)
    {
        Y = y;
        _palette = palette;
        _data = data;
        if (_palette.Count <= 1)
        {
            _bitsPerEntry = 0;
            _entriesPerLong = 0;
        }
        else
        {
            _bitsPerEntry = Math.Max(4, CeilLog2(_palette.Count));
            _entriesPerLong = 64 / _bitsPerEntry;
        }
    }

    /// <summary>
    /// section 索引，可能为负
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// 该 section 底面在世界中的 Y 坐标
    /// </summary>
    public int BaseY => Y * 16;

    /// <summary>
    /// palette 中的方块名称列表
    /// </summary>
    public IReadOnlyList<string> Palette => _palette;

    /// <summary>
    /// 读取 section 内局部坐标处的方块名称
    /// </summary>
    /// <param name="x">局部 X（0..15）</param>
    /// <param name="y">局部 Y（0..15）</param>
    /// <param name="z">局部 Z（0..15）</param>
    /// <returns>方块名称，如 minecraft:stone</returns>
    public string BlockAt(int x, int y, int z)
    {
        if ((uint)x >= 16 || (uint)y >= 16 || (uint)z >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "section 局部坐标必须在 0..15 之间");
        }

        EnsureUnpacked();
        var index = (y << 8) | (z << 4) | x;
        return _unpacked![index];
    }

    private void EnsureUnpacked()
    {
        if (_unpacked is not null)
        {
            return;
        }

        if (_palette.Count <= 1 || _data is null)
        {
            var single = _palette.Count == 0 ? "minecraft:air" : _palette[0];
            _unpacked = Enumerable.Repeat(single, 4096).ToArray();
            return;
        }

        var blocks = new string[4096];
        var mask = (1L << _bitsPerEntry) - 1;
        for (var index = 0; index < 4096; index++)
        {
            var slot = index / _entriesPerLong;
            var shift = (index % _entriesPerLong) * _bitsPerEntry;
            var paletteIndex = (int)((_data[slot] >> shift) & mask);
            if ((uint)paletteIndex >= _palette.Count)
            {
                throw new InvalidDataException("方块索引超出 palette 范围");
            }

            blocks[index] = _palette[paletteIndex];
        }

        _unpacked = blocks;
    }

    internal static AnvilSection FromNbt(NbtCompound section)
    {
        var y = section.TryGetByte("Y", out var yByte) ? (sbyte)yByte : section.GetInt("Y");
        IReadOnlyList<string> palette;
        long[]? data = null;
        if (section.TryGetCompound("block_states", out var blockStates))
        {
            if (blockStates.TryGetList("palette", out var paletteList))
            {
                var names = new List<string>(paletteList.Count);
                foreach (var entry in paletteList.Items)
                {
                    if (entry is NbtCompound entryCompound && entryCompound.TryGetString("Name", out var name))
                    {
                        names.Add(name);
                    }
                    else
                    {
                        names.Add("minecraft:air");
                    }
                }

                palette = names;
            }
            else
            {
                palette = Array.Empty<string>();
            }

            if (blockStates.TryGetLongArray("data", out var longArray))
            {
                data = longArray;
            }
        }
        else
        {
            palette = Array.Empty<string>();
        }

        return new AnvilSection(y, palette, data);
    }

    private static int CeilLog2(int value)
    {
        if (value <= 1)
        {
            return 0;
        }

        var bits = 0;
        var remaining = value - 1;
        while (remaining > 0)
        {
            remaining >>= 1;
            bits++;
        }

        return bits;
    }
}
