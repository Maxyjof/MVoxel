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
            var bytes = new byte[checked(24 + count * RecordSize)];
            using (var stream = new MemoryStream(bytes, writable: true))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                WriteHeader(writer, width, depth, height, count);
            }

            for (var index = 0; index < chunks.Count; index++)
            {
                SerializeChunkRecords(bytes.AsSpan((int)(24 + offsets[index] * RecordSize)), chunks[index], minimum);
            }

            AtomicFile.WriteBytes(path, bytes);
            return;
        }

        WriteStreaming(path, width, depth, height, count, chunks, offsets, minimum);
    }

    /// <summary>
    /// 大世界流式写入：预分配定长临时文件，各区块用池化缓冲序列化后并行定点写入自己的区间，
    /// 记录 FNV-1a 区哈希；强制刷盘后整文件回读重算哈希，逐区一致才原子替换目标，
    /// 峰值内存只有一个区块的缓冲规模，不再要求整份文件驻留内存
    /// </summary>
    private static void WriteStreaming(string path, int width, int depth, int height, int count,
        List<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)> chunks, long[] offsets, VoxelCoord64 minimum)
    {
        var temporaryPath = AtomicFile.BeginTemporary(path);
        try
        {
            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
            };
            var writeGate = new object();
            var writtenHashes = new long[chunks.Count];
            var header = new byte[24];
            using (var headerStream = new MemoryStream(header, writable: true))
            using (var headerWriter = new BinaryWriter(headerStream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                WriteHeader(headerWriter, width, depth, height, count);
            }

            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.Read, bufferSize: 1 << 20))
            {
                stream.SetLength(24L + count * RecordSize);
                stream.Write(header, 0, header.Length);
                System.Threading.Tasks.Parallel.For(0, chunks.Count, options, index =>
                {
                    var byteLength = (int)((offsets[index + 1] - offsets[index]) * RecordSize);
                    if (byteLength == 0)
                    {
                        return;
                    }

                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(byteLength);
                    try
                    {
                        var span = buffer.AsSpan(0, byteLength);
                        SerializeChunkRecords(span, chunks[index], minimum);
                        writtenHashes[index] = HashRegion(span);
                        lock (writeGate)
                        {
                            stream.Seek(24 + offsets[index] * RecordSize, SeekOrigin.Begin);
                            stream.Write(buffer, 0, byteLength);
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                });
                stream.Flush(flushToDisk: true);
            }

            // 回读按文件顺序单流进行，区块区间天然连续，读完一区立刻比对哈希，尽早失败
            var maximumRegion = 0;
            for (var index = 0; index < chunks.Count; index++)
            {
                maximumRegion = Math.Max(maximumRegion, (int)((offsets[index + 1] - offsets[index]) * RecordSize));
            }

            var verifyBuffer = new byte[maximumRegion];
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, bufferSize: 1 << 20))
            {
                var headerCheck = new byte[24];
                ReadFully(stream, headerCheck, headerCheck.Length);
                if (!headerCheck.AsSpan().SequenceEqual(header))
                {
                    throw new IOException("大世界模型文件头回读校验失败");
                }

                for (var index = 0; index < chunks.Count; index++)
                {
                    var byteLength = (int)((offsets[index + 1] - offsets[index]) * RecordSize);
                    if (byteLength == 0)
                    {
                        continue;
                    }

                    ReadFully(stream, verifyBuffer, byteLength);
                    if (HashRegion(verifyBuffer.AsSpan(0, byteLength)) != writtenHashes[index])
                    {
                        throw new IOException($"大世界模型区块 {index} 回读校验失败");
                    }
                }
            }

            AtomicFile.CommitTemporary(temporaryPath, path);
        }
        catch
        {
            AtomicFile.DiscardTemporary(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// 从流读满指定长度，短读循环补齐，提前遇到文件末尾视为损坏
    /// </summary>
    private static void ReadFully(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var got = stream.Read(buffer, read, count - read);
            if (got <= 0)
            {
                throw new InvalidDataException("大世界模型文件在记录结束前截断");
            }

            read += got;
        }
    }

    /// <summary>
    /// 对一个区块的连续记录区间求 FNV-1a 哈希，用于写回一致性校验
    /// </summary>
    private static long HashRegion(ReadOnlySpan<byte> data)
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        var hash = Offset;
        foreach (var byteValue in data)
        {
            hash ^= byteValue;
            hash *= Prime;
        }

        return (long)hash;
    }

    /// <summary>
    /// 把一个稀疏区块的全部体素记录序列化进目标区间，返回写入字节数
    /// </summary>
    /// <param name="target">本区块记录的目标区间</param>
    /// <param name="entry">区块坐标与稀疏区块</param>
    /// <param name="minimum">世界包围盒最小角</param>
    private static int SerializeChunkRecords(Span<byte> target,
        (ChunkCoord64 Coordinate, SparseVoxelChunk Chunk) entry, VoxelCoord64 minimum)
    {
        var slot = 0;
        foreach (var (index, value) in entry.Chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            var record = target.Slice(slot, RecordSize);
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

        return slot;
    }

    /// <summary>
    /// 读取大世界模型文件：校验文件头与长度后按固定窗口流式解析记录，
    /// 不把整份文件载入内存
    /// </summary>
    /// <param name="path">模型文件路径</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan);
        var header = new byte[24];
        ReadFully(stream, header, header.Length);
        var (width, depth, height, count) = ValidateHeader(header, stream.Length);
        var world = new VoxelWorld();
        var buffer = new byte[RecordSize * 65536];
        var remaining = count;
        while (remaining > 0)
        {
            var records = (int)Math.Min(remaining, buffer.Length / RecordSize);
            var bytes = records * RecordSize;
            ReadFully(stream, buffer, bytes);
            for (var record = 0; record < records; record++)
            {
                ParseRecord(buffer.AsSpan(record * RecordSize, RecordSize), width, height, depth, default, world);
            }

            remaining -= records;
        }

        return world;
    }

    /// <summary>
    /// 校验大世界模型文件头：签名、版本、尺寸与记录数和文件长度的一致性
    /// </summary>
    private static (int Width, int Depth, int Height, int Count) ValidateHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version)
        {
            throw new InvalidDataException("不是受支持的 MVoxel 大世界模型文件");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var depth = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        ValidateDimensions(width, height, depth);
        if (count < 0 || count > MaximumVoxelRecords || 24L + count * RecordSize != fileLength)
        {
            throw new InvalidDataException("大世界模型体素记录数量或文件长度不一致");
        }

        return (width, depth, height, count);
    }

    /// <summary>
    /// 解析一条 13 字节体素记录并写入世界，越界或空体素视为损坏
    /// </summary>
    private static void ParseRecord(ReadOnlySpan<byte> record, int width, int height, int depth,
        VoxelCoord64 origin, VoxelWorld world)
    {
        var x = BinaryPrimitives.ReadUInt16LittleEndian(record);
        var y = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        var z = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var value = new VoxelValue(
            new Rgba32(record[6], record[7], record[8], record[9]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[10..]),
            record[12]);
        if (x >= width || y >= height || z >= depth || value.IsEmpty)
        {
            throw new InvalidDataException("大世界模型包含越界或空体素记录");
        }

        world.Set(origin + new VoxelCoord64(x, y, z), value);
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

        var (width, depth, height, count) = ValidateHeader(bytes, bytes.Length);
        var world = new VoxelWorld();
        for (var index = 0; index < count; index++)
        {
            ParseRecord(bytes.AsSpan(24 + index * RecordSize, RecordSize), width, height, depth, origin, world);
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
