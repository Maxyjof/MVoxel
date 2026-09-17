using System.Buffers.Binary;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// MVoxel 原生体素资源格式，使用 16 位局部坐标保存 .mvmodel/.mvworld，
/// 并完整记录颜色、材质槽和体素渲染标志。
/// </summary>
public static class LargeWorldFormat
{
    private const uint Magic = 0x3158564D;
    private const int Version = 2;
    private const int RecordSize = 13;

    /// <summary>
    /// 记录数上限：整个文件长度必须落进 int 范围
    /// </summary>
    private const int MaximumVoxelRecords = (int.MaxValue - 24) / RecordSize;

    /// <summary>
    /// 低于该记录数时串行填充，避免小文件付线程调度成本
    /// </summary>
    private const int ParallelWriteThreshold = 500_000;

    /// <summary>
    /// 超大型模板的 X 轴尺寸
    /// </summary>
    public const int DemoWidth = 1024;

    /// <summary>
    /// 超大型模板的 Z 轴尺寸
    /// </summary>
    public const int DemoDepth = 1024;

    /// <summary>
    /// 超大型建筑模板的 Y 轴尺寸
    /// </summary>
    public const int DemoHeight = 1024;

    private const int DemoTowerSize = 8;
    private const int DemoTowerInset = 160;
    private const int DemoBridgeStart = DemoTowerInset + DemoTowerSize;
    private const int DemoBridgeEnd = DemoWidth - DemoBridgeStart - 1;
    private const int DemoBridgeSpan = DemoBridgeEnd - DemoBridgeStart + 1;
    private const int DemoBridgeWidth = 8;
    private const int DemoBridgeLevels = 3;

    /// <summary>
    /// 超大型模板的实际占用体素数量
    /// </summary>
    public const int DemoVoxelCount = DemoWidth * DemoDepth +
        4 * DemoTowerSize * DemoTowerSize * (DemoHeight - 1) +
        DemoBridgeLevels * (2 * DemoBridgeSpan * DemoBridgeWidth - DemoBridgeWidth * DemoBridgeWidth);

    /// <summary>
    /// 将超大型建筑示例写入紧凑的大世界文件
    /// </summary>
    /// <param name="path">目标文件路径</param>
    public static void WriteDemo(string path)
    {
        var bytes = new byte[checked(24 + DemoVoxelCount * RecordSize)];
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        WriteHeader(writer, DemoWidth, DemoDepth, DemoHeight, DemoVoxelCount);
        WriteDemoVoxels(writer, WriteVoxel);

        AtomicFile.WriteBytes(path, bytes);
    }

    /// <summary>
    /// 将稀疏世界保存为大世界模型文件；记录按区块连续排布，
    /// 大世界的各区块并行填充到互不相交的缓冲区间，小世界串行填充
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">待保存的稀疏世界</param>
    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            throw new InvalidOperationException("不能保存空的大世界模型");
        }

        var width = checked((int)(maximum.X - minimum.X + 1));
        var height = checked((int)(maximum.Y - minimum.Y + 1));
        var depth = checked((int)(maximum.Z - minimum.Z + 1));
        ValidateDimensions(width, height, depth);
        var count = world.OccupiedVoxelCount;
        var bytes = new byte[checked(24 + count * RecordSize)];
        using (var stream = new MemoryStream(bytes, writable: true))
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            WriteHeader(writer, width, depth, height, count);
        }

        var chunks = world.SnapshotChunks();
        var offsets = new long[chunks.Count + 1];
        for (var index = 0; index < chunks.Count; index++)
        {
            offsets[index + 1] = offsets[index] + chunks[index].Chunk.OccupiedCount;
        }

        if (offsets[chunks.Count] != count)
        {
            throw new InvalidOperationException("写模型期间世界被并发修改，区块记录数不一致");
        }

        if (count < ParallelWriteThreshold)
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                FillChunkRecords(bytes, (int)(24 + offsets[index] * RecordSize), chunks[index], minimum);
            }
        }
        else
        {
            System.Threading.Tasks.Parallel.For(0, chunks.Count,
                new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
                },
                index => FillChunkRecords(bytes, (int)(24 + offsets[index] * RecordSize), chunks[index], minimum));
        }

        AtomicFile.WriteBytes(path, bytes);
    }

    /// <summary>
    /// 把一个稀疏区块的全部体素记录写进缓冲区指定区间
    /// </summary>
    /// <param name="bytes">文件字节缓冲</param>
    /// <param name="offset">本区块首记录的字节偏移</param>
    /// <param name="entry">区块坐标与稀疏区块</param>
    /// <param name="minimum">世界包围盒最小角</param>
    private static void FillChunkRecords(byte[] bytes, int offset,
        (ChunkCoord64 Coordinate, SparseVoxelChunk Chunk) entry, VoxelCoord64 minimum)
    {
        var slot = offset;
        foreach (var (index, value) in entry.Chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            var record = bytes.AsSpan(slot, RecordSize);
            BinaryPrimitives.WriteUInt16LittleEndian(record,
                checked((ushort)(entry.Coordinate.X * VoxelWorld.ChunkSize + local.X - minimum.X)));
            BinaryPrimitives.WriteUInt16LittleEndian(record[2..],
                checked((ushort)(entry.Coordinate.Y * VoxelWorld.ChunkSize + local.Y - minimum.Y)));
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..],
                checked((ushort)(entry.Coordinate.Z * VoxelWorld.ChunkSize + local.Z - minimum.Z)));
            record[6] = value.Color.R;
            record[7] = value.Color.G;
            record[8] = value.Color.B;
            record[9] = value.Color.A;
            BinaryPrimitives.WriteUInt16LittleEndian(record[10..], value.MaterialSlot);
            record[12] = value.Flags;
            slot += RecordSize;
        }
    }

    /// <summary>
    /// 读取大世界模型文件
    /// </summary>
    /// <param name="path">模型文件路径</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(string path)
    {
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>
    /// 从字节读取大世界模型文件
    /// </summary>
    /// <param name="bytes">完整文件内容</param>
    /// <param name="origin">模型在世界中的起点</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 24)
        {
            throw new InvalidDataException("大世界模型文件头不完整");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("不是受支持的 MVoxel 大世界模型文件");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException("不是受支持的 MVoxel 大世界模型文件");
        }

        var width = reader.ReadInt32();
        var depth = reader.ReadInt32();
        var height = reader.ReadInt32();
        var count = reader.ReadInt32();
        ValidateDimensions(width, height, depth);
        var expectedLength = checked(24L + count * RecordSize);
        if (count < 0 || count > MaximumVoxelRecords || expectedLength != bytes.Length)
        {
            throw new InvalidDataException("大世界模型体素记录数量或文件长度不一致");
        }

        var world = new VoxelWorld();
        for (var index = 0; index < count; index++)
        {
            var x = reader.ReadUInt16();
            var y = reader.ReadUInt16();
            var z = reader.ReadUInt16();
            var value = new VoxelValue(new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
                reader.ReadUInt16(), reader.ReadByte());
            if (x >= width || y >= height || z >= depth || value.IsEmpty)
            {
                throw new InvalidDataException("大世界模型包含越界或空体素记录");
            }

            world.Set(origin + new VoxelCoord64(x, y, z), value);
        }

        return world;
    }

    /// <summary>
    /// 写入大世界模型文件头
    /// </summary>
    /// <param name="writer">目标写入器</param>
    /// <param name="width">X 轴尺寸</param>
    /// <param name="depth">Z 轴尺寸</param>
    /// <param name="height">Y 轴尺寸</param>
    /// <param name="count">体素记录数量</param>
    private static void WriteHeader(BinaryWriter writer, int width, int depth, int height, int count, int version = Version)
    {
        writer.Write(Magic);
        writer.Write(version);
        writer.Write(width);
        writer.Write(depth);
        writer.Write(height);
        writer.Write(count);
    }

    /// <summary>
    /// 写入一个大世界体素记录
    /// </summary>
    /// <param name="writer">目标写入器</param>
    /// <param name="x">局部 X 坐标</param>
    /// <param name="y">局部 Y 坐标</param>
    /// <param name="z">局部 Z 坐标</param>
    /// <param name="value">体素值</param>
    private static void WriteVoxel(BinaryWriter writer, int x, int y, int z, VoxelValue value)
    {
        writer.Write(checked((ushort)x));
        writer.Write(checked((ushort)y));
        writer.Write(checked((ushort)z));
        writer.Write(value.Color.R);
        writer.Write(value.Color.G);
        writer.Write(value.Color.B);
        writer.Write(value.Color.A);
        writer.Write(value.MaterialSlot);
        writer.Write(value.Flags);
    }

    /// <summary>
    /// 校验大世界模型尺寸
    /// </summary>
    /// <param name="width">X 轴尺寸</param>
    /// <param name="height">Y 轴尺寸</param>
    /// <param name="depth">Z 轴尺寸</param>
    private static void ValidateDimensions(int width, int height, int depth)
    {
        if (width is < 1 or > 1024 || height is < 1 or > 1024 || depth is < 1 or > 1024)
        {
            throw new InvalidDataException("大世界模型尺寸超出 1024 格限制");
        }
    }

    /// <summary>
    /// 判断超大型建筑中的塔楼位置
    /// </summary>
    /// <param name="x">X 坐标</param>
    /// <param name="z">Z 坐标</param>
    /// <returns>是否为高柱</returns>
    private static bool IsDemoTower(int x, int z) =>
        (x is >= DemoTowerInset and < DemoTowerInset + DemoTowerSize ||
         x is >= DemoWidth - DemoTowerInset - DemoTowerSize and < DemoWidth - DemoTowerInset) &&
        (z is >= DemoTowerInset and < DemoTowerInset + DemoTowerSize ||
         z is >= DemoDepth - DemoTowerInset - DemoTowerSize and < DemoDepth - DemoTowerInset);

    /// <summary>
    /// 判断桥面层是否覆盖某个水平位置
    /// </summary>
    /// <param name="x">X 坐标</param>
    /// <param name="z">Z 坐标</param>
    /// <returns>是否属于桥面</returns>
    private static bool IsDemoBridge(int x, int z) =>
        (x is >= DemoBridgeStart and <= DemoBridgeEnd && z is >= DemoBridgeStart and < DemoBridgeStart + DemoBridgeWidth) ||
        (z is >= DemoBridgeStart and <= DemoBridgeEnd && x is >= DemoBridgeStart and < DemoBridgeStart + DemoBridgeWidth);

    /// <summary>
    /// 生成超大型建筑的确定性颜色
    /// </summary>
    /// <param name="x">X 坐标</param>
    /// <param name="y">Y 坐标</param>
    /// <param name="z">Z 坐标</param>
    /// <param name="tower">是否为塔楼</param>
    /// <returns>体素颜色</returns>
    private static Rgba32 DemoColor(int x, int y, int z, bool tower)
    {
        if (tower)
        {
            return y == DemoHeight - 1
                ? new Rgba32(232, 170, 76)
                : (y / 64) % 2 == 0 ? new Rgba32(66, 105, 156) : new Rgba32(58, 88, 132);
        }

        if (IsDemoBridge(x, z)) return new Rgba32(104, 142, 184);
        return (x / 32 + z / 32) % 2 == 0
            ? new Rgba32(66, 138, 102)
            : new Rgba32(57, 119, 91);
    }

    /// <summary>
    /// 按确定性顺序写入超大型建筑的全部稀疏体素
    /// </summary>
    /// <param name="writer">写入器</param>
    /// <param name="write">单体素写入动作</param>
    private static void WriteDemoVoxels(BinaryWriter writer, Action<BinaryWriter, int, int, int, VoxelValue> write)
    {
        for (var z = 0; z < DemoDepth; z++)
        for (var x = 0; x < DemoWidth; x++)
            write(writer, x, 0, z, new VoxelValue(DemoColor(x, 0, z, false)));

        for (var z = 0; z < DemoDepth; z++)
        for (var x = 0; x < DemoWidth; x++)
        {
            if (!IsDemoTower(x, z)) continue;
            for (var y = 1; y < DemoHeight; y++)
                write(writer, x, y, z, new VoxelValue(DemoColor(x, y, z, true)));
        }

        foreach (var y in new[] { 256, 512, 768 })
        {
            for (var z = DemoBridgeStart; z < DemoBridgeStart + DemoBridgeWidth; z++)
            for (var x = DemoBridgeStart; x <= DemoBridgeEnd; x++)
                write(writer, x, y, z, new VoxelValue(DemoColor(x, y, z, false)));
            for (var x = DemoBridgeStart; x < DemoBridgeStart + DemoBridgeWidth; x++)
            for (var z = DemoBridgeStart; z <= DemoBridgeEnd; z++)
            {
                if (z < DemoBridgeStart + DemoBridgeWidth) continue;
                write(writer, x, y, z, new VoxelValue(DemoColor(x, y, z, false)));
            }
        }
    }

    /// <summary>
    /// 构建超大型建筑模板的完整内存世界供测试使用
    /// </summary>
    /// <returns>1024×1024×1024 的稀疏建筑示例世界</returns>
    public static VoxelWorld BuildDemoWorld()
    {
        var world = new VoxelWorld();
        for (var z = 0; z < DemoDepth; z++)
        for (var x = 0; x < DemoWidth; x++)
            world.Set(new VoxelCoord64(x, 0, z), new VoxelValue(DemoColor(x, 0, z, false)));

        for (var z = 0; z < DemoDepth; z++)
        for (var x = 0; x < DemoWidth; x++)
        {
            if (!IsDemoTower(x, z)) continue;
            for (var y = 1; y < DemoHeight; y++)
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(DemoColor(x, y, z, true)));
        }

        foreach (var y in new[] { 256, 512, 768 })
        {
            for (var z = DemoBridgeStart; z < DemoBridgeStart + DemoBridgeWidth; z++)
            for (var x = DemoBridgeStart; x <= DemoBridgeEnd; x++)
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(DemoColor(x, y, z, false)));
            for (var x = DemoBridgeStart; x < DemoBridgeStart + DemoBridgeWidth; x++)
            for (var z = DemoBridgeStart; z <= DemoBridgeEnd; z++)
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(DemoColor(x, y, z, false)));
        }

        return world;
    }
}
