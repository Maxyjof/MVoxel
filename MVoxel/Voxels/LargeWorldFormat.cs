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
    private const int ObjectFormatVersion = 4;
    private const int MaximumObjectSegments = 4096;
    private const int MaximumObjectNameBytes = 1 * 1024 * 1024;
    private const int MaximumObjectIdBytes = 256;

    /// <summary>原生v4对象片段，体素坐标相对于片段原点保存</summary>
    public sealed record NativeObjectSegment(string ObjectId, string Name, VoxelCoord64 Origin, VoxelWorld World);

    /// <summary>原生v4对象片段文档，导入方可据此逐对象恢复场景对象和局部材质绑定</summary>
    public sealed record NativeObjectDocument(IReadOnlyList<NativeObjectSegment> Objects,
        MaterialExchangeMetadata Materials);

    /// <summary>
    /// 将稀疏世界保存为大世界模型文件；记录按区块连续排布，
    /// 大世界的各区块并行填充到互不相交的缓冲区间，小世界串行填充
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">待保存的稀疏世界</param>
    public static void Write(string path, VoxelWorld world) => Write(path, world, null);

    public static void Write(string path, VoxelWorld world, MaterialExchangeMetadata? materials)
    {
        var metadata = materials is null ? null : MaterialExchangeCodec.Encode(materials);
        ArgumentNullException.ThrowIfNull(world);
        var startContentVersion = world.ContentVersion;
        var startStructureStamp = world.StructureStamp;
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            WriteEmpty(path, materials);
            return;
        }

        var width = checked((int)(maximum.X - minimum.X + 1));
        var height = checked((int)(maximum.Y - minimum.Y + 1));
        var depth = checked((int)(maximum.Z - minimum.Z + 1));
        ValidateDimensions(width, height, depth);
        var count = world.OccupiedVoxelCount;
        var chunks = world.SnapshotChunks();
        if (startContentVersion != world.ContentVersion || startStructureStamp != world.StructureStamp)
            throw new InvalidOperationException("写模型期间世界被编辑，请稍后重试保存");
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
            var bytes = new byte[checked(24 + count * RecordSize + (metadata is null ? 0 : 4 + metadata.Length))];
            using (var stream = new MemoryStream(bytes, writable: true))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                WriteHeader(writer, width, depth, height, count, metadata is null ? Version : 3);
            }

            for (var index = 0; index < chunks.Count; index++)
            {
                SerializeChunkRecords(bytes.AsSpan((int)(24 + offsets[index] * RecordSize)), chunks[index], minimum);
            }

            if (metadata is not null)
            {
                var tail = 24 + count * RecordSize;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(tail), metadata.Length);
                metadata.CopyTo(bytes, tail + 4);
            }
            if (startContentVersion != world.ContentVersion || startStructureStamp != world.StructureStamp)
                throw new InvalidOperationException("写模型期间世界被编辑，已取消写入");
            AtomicFile.WriteBytes(path, bytes);
            return;
        }

        WriteStreaming(path, width, depth, height, count, chunks, offsets, minimum, metadata);
    }

    /// <summary>写入v4对象片段文件；对象体素坐标保存在各自World中，原点单独保存</summary>
    public static void WriteObjects(string path, IReadOnlyList<NativeObjectSegment> objects,
        MaterialExchangeMetadata? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateObjectSegments(objects);
        var versions = objects.Select(segment => segment.World.ContentVersion).ToArray();
        var metadata = materials is null ? Array.Empty<byte>() : MaterialExchangeCodec.Encode(materials);
        var temporaryPath = AtomicFile.BeginTemporary(path);
        try
        {
            var writtenHash = WriteObjectDocument(temporaryPath, objects, metadata);
            VerifyTemporary(temporaryPath, writtenHash);
            for (var index = 0; index < objects.Count; index++)
            {
                if (objects[index].World.ContentVersion != versions[index])
                    throw new InvalidOperationException("写入对象片段期间体素发生变化，请重新保存");
            }
            AtomicFile.CommitTemporary(temporaryPath, path);
        }
        finally
        {
            AtomicFile.DiscardTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// 校验片段具备写出后可被自身读回的条件：写出侧与读取侧的标识、名称、
    /// 体素数量限制保持一致，避免产出读取阶段才报错的资产
    /// </summary>
    private static void ValidateObjectSegments(IReadOnlyList<NativeObjectSegment> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0 || objects.Count > MaximumObjectSegments)
            throw new ArgumentException("对象片段数量无效", nameof(objects));
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in objects)
        {
            if (segment is null || segment.World is null)
                throw new ArgumentException("对象片段缺少体素世界", nameof(objects));
            if (string.IsNullOrWhiteSpace(segment.ObjectId))
                throw new ArgumentException("对象片段缺少标识", nameof(objects));
            if (System.Text.Encoding.UTF8.GetByteCount(segment.ObjectId) > MaximumObjectIdBytes)
                throw new ArgumentException("对象标识过长", nameof(objects));
            if (!objectIds.Add(segment.ObjectId))
                throw new ArgumentException($"对象片段标识重复：{segment.ObjectId}", nameof(objects));
            if (System.Text.Encoding.UTF8.GetByteCount(segment.Name ?? string.Empty) > MaximumObjectNameBytes)
                throw new ArgumentException("对象名称过长", nameof(objects));
            if (segment.World.OccupiedVoxelCount > MaximumVoxelRecords)
                throw new ArgumentException("对象片段体素数量超出格式上限", nameof(objects));
        }
    }

    /// <summary>把对象片段写入临时文件，返回已落盘内容的SHA256哈希</summary>
    private static byte[] WriteObjectDocument(string temporaryPath, IReadOnlyList<NativeObjectSegment> objects,
        byte[] metadata)
    {
        using var hash = System.Security.Cryptography.SHA256.Create();
        byte[] writtenHash;
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        using (var hashed = new System.Security.Cryptography.CryptoStream(stream, hash,
                   System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        using (var writer = new BinaryWriter(hashed, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(ObjectFormatVersion);
            writer.Write(objects.Count);
            foreach (var segment in objects)
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(segment.Name ?? string.Empty);
                writer.Write(segment.ObjectId);
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(segment.Origin.X);
                writer.Write(segment.Origin.Y);
                writer.Write(segment.Origin.Z);
                var version = segment.World.ContentVersion;
                var count = segment.World.OccupiedVoxelCount;
                writer.Write(count);
                var written = 0;
                foreach (var (coordinate, value) in segment.World.EnumerateOccupied())
                {
                    writer.Write(coordinate.X);
                    writer.Write(coordinate.Y);
                    writer.Write(coordinate.Z);
                    writer.Write(value.Color.R);
                    writer.Write(value.Color.G);
                    writer.Write(value.Color.B);
                    writer.Write(value.Color.A);
                    writer.Write(value.MaterialSlot);
                    writer.Write(value.Flags);
                    written++;
                }
                if (written != count || version != segment.World.ContentVersion)
                    throw new InvalidOperationException("写入对象片段期间体素发生变化，请重新保存");
            }
            writer.Write(metadata.Length);
            writer.Write(metadata);
            writer.Flush();
            hashed.FlushFinalBlock();
            writtenHash = hash.Hash!;
            stream.Flush(flushToDisk: true);
        }
        return writtenHash;
    }

    /// <summary>回读临时文件重算哈希，与写入结果不一致说明磁盘内容不可信</summary>
    private static void VerifyTemporary(string temporaryPath, byte[] expectedHash)
    {
        using var verify = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        if (!System.Security.Cryptography.SHA256.HashData(verify).SequenceEqual(expectedHash))
            throw new IOException("对象片段写入校验失败，原资产未替换");
    }

    /// <summary>判断文件是否为多对象原生文档。</summary>
    public static bool IsObjectDocument(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        return stream.Length >= 8 && reader.ReadUInt32() == Magic && reader.ReadInt32() == ObjectFormatVersion;
    }

    /// <summary>合成多对象体素以供预览；重叠位置由后面的对象覆盖。</summary>
    public static VoxelWorld ReadObjectPreview(string path)
    {
        var document = ReadObjects(path);
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var segment in document.Objects)
            foreach (var (coordinate, value) in segment.World.EnumerateOccupied()) writer.Set(coordinate, value);
        return world;
    }

    /// <summary>读取v4对象片段文件；v2/v3请继续使用ReadDocument</summary>
    public static NativeObjectDocument ReadObjects(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return ReadObjects(stream);
    }

    /// <summary>从字节读取v4对象片段文件</summary>
    public static NativeObjectDocument ReadObjects(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadObjects(stream);
    }

    private static NativeObjectDocument ReadObjects(Stream stream)
    {
        if (stream.Length < 12)
            throw new InvalidDataException("对象片段文件头不完整");
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != ObjectFormatVersion)
            throw new InvalidDataException("不是MVoxel v4对象片段文件");
        var count = reader.ReadInt32();
        if (count < 1 || count > MaximumObjectSegments) throw new InvalidDataException("对象片段数量无效");
        var segments = new List<NativeObjectSegment>(count);
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var idLength = ReadIdentifierLength(reader);
            if (idLength < 1 || idLength > MaximumObjectIdBytes || idLength > stream.Length - stream.Position)
                throw new InvalidDataException("对象标识无效或过长");
            var id = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(idLength));
            if (string.IsNullOrWhiteSpace(id) || !objectIds.Add(id))
                throw new InvalidDataException("对象标识为空或重复");
            var nameLength = reader.ReadInt32();
            if (nameLength < 0 || nameLength > MaximumObjectNameBytes || nameLength > stream.Length - stream.Position)
                throw new InvalidDataException("对象名称长度无效");
            var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            var origin = new VoxelCoord64(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
            var voxelCount = reader.ReadInt32();
            if (voxelCount < 0 || voxelCount > MaximumVoxelRecords || (long)voxelCount * 31L > stream.Length - stream.Position)
                throw new InvalidDataException("对象体素数量或文件长度无效");
            var world = new VoxelWorld();
            for (var voxel = 0; voxel < voxelCount; voxel++)
            {
                var coordinate = new VoxelCoord64(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                var value = new VoxelValue(new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()), reader.ReadUInt16(), reader.ReadByte());
                if (value.IsEmpty) throw new InvalidDataException("v4对象包含空体素");
                world.Set(coordinate, value);
            }
            if (world.OccupiedVoxelCount != voxelCount)
                throw new InvalidDataException("对象片段包含重复体素坐标");
            segments.Add(new NativeObjectSegment(id, name, origin, world));
        }
        var metadataLength = reader.ReadInt32();
        if (metadataLength == 0 && stream.Position == stream.Length)
            return new NativeObjectDocument(segments, new MaterialExchangeMetadata());
        if (metadataLength < 0 || metadataLength > MaterialExchangeCodec.MaximumBytes || stream.Length - stream.Position != metadataLength)
            throw new InvalidDataException("v4材质元数据长度无效");
        return new NativeObjectDocument(segments, MaterialExchangeCodec.Decode(reader.ReadBytes(metadataLength)));
    }

    /// <summary>读取变长整数标识长度，畸形编码统一视为文件损坏</summary>
    private static int ReadIdentifierLength(BinaryReader reader)
    {
        try { return reader.Read7BitEncodedInt(); }
        catch (FormatException exception) { throw new InvalidDataException("对象标识编码损坏", exception); }
    }


    public static void WriteEmpty(string path, MaterialExchangeMetadata? materials = null)
    {
        var metadata = materials is null ? null : MaterialExchangeCodec.Encode(materials);
        var bytes = new byte[24 + (metadata is null ? 0 : 4 + metadata.Length)];
        using (var stream = new MemoryStream(bytes, writable: true))
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            WriteHeader(writer, 1, 1, 1, 0, metadata is null ? Version : 3);
        if (metadata is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), metadata.Length);
            metadata.CopyTo(bytes, 28);
        }
        AtomicFile.WriteBytes(path, bytes);
    }

    /// <summary>
    /// 大世界流式写入：预分配定长临时文件，各区块用池化缓冲序列化后并行定点写入自己的区间，
    /// 记录 FNV-1a 区哈希；强制刷盘后整文件回读重算哈希，逐区一致才原子替换目标，
    /// 峰值内存只有一个区块的缓冲规模，不再要求整份文件驻留内存
    /// </summary>
    private static void WriteStreaming(string path, int width, int depth, int height, int count,
        List<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)> chunks, long[] offsets, VoxelCoord64 minimum, byte[]? metadata)
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
                WriteHeader(headerWriter, width, depth, height, count, metadata is null ? Version : 3);
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
                if (metadata is not null)
                {
                    stream.Position = 24L + count * RecordSize;
                    using var tailWriter = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                    tailWriter.Write(metadata.Length);
                    tailWriter.Write(metadata);
                }
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
    /// <param name="origin">模型实例在场景中的世界原点</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(string path, VoxelCoord64 origin = default,
        CancellationToken cancellationToken = default, Action<int, int>? reportProgress = null)
        => ReadDocument(path, origin, cancellationToken, reportProgress).World;

    public static NativeVoxelDocument ReadDocument(string path, VoxelCoord64 origin = default,
        CancellationToken cancellationToken = default, Action<int, int>? reportProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan);
        var header = new byte[24];
        ReadFully(stream, header, header.Length);
        var (width, depth, height, count) = ValidateHeader(header, stream.Length);
        var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        stream.Position = 24L + (long)count * RecordSize;
        var materials = ReadMetadata(stream, version);
        stream.Position = 24;
        var voxelOrigin = origin + materials.OriginOffset;
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        var buffer = new byte[RecordSize * 65536];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = (int)Math.Min(remaining, buffer.Length / RecordSize);
            var bytes = records * RecordSize;
            ReadFully(stream, buffer, bytes);
            for (var record = 0; record < records; record++)
            {
                ParseRecord(buffer.AsSpan(record * RecordSize, RecordSize), width, height, depth, voxelOrigin, writer);
            }

            remaining -= records;
            reportProgress?.Invoke(count - remaining, count);
        }

        return new NativeVoxelDocument(world, materials);
    }

    /// <summary>
    /// 校验大世界模型文件头：签名、版本、尺寸与记录数和文件长度的一致性
    /// </summary>
    private static (int Width, int Depth, int Height, int Count) ValidateHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]) is not (2 or 3))
        {
            throw new InvalidDataException("不是受支持的 MVoxel 大世界模型文件");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var depth = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        ValidateDimensions(width, height, depth);
        var recordEnd = 24L + (long)count * RecordSize;
        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (count < 0 || count > MaximumVoxelRecords ||
            (version == 2 ? recordEnd != fileLength : fileLength < recordEnd + 4 ||
                fileLength > recordEnd + 4 + MaterialExchangeCodec.MaximumBytes))
        {
            throw new InvalidDataException("大世界模型体素记录数量或文件长度不一致");
        }

        return (width, depth, height, count);
    }

    /// <summary>
    /// 解析一条 13 字节体素记录并写入世界，越界或空体素视为损坏
    /// </summary>
    private static void ParseRecord(ReadOnlySpan<byte> record, int width, int height, int depth,
        VoxelCoord64 origin, VoxelWorld.BulkWriter writer)
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

        writer.Set(origin + new VoxelCoord64(x, y, z), value);
    }

    /// <summary>
    /// 从字节读取大世界模型文件
    /// </summary>
    /// <param name="bytes">完整文件内容</param>
    /// <param name="origin">模型在世界中的起点</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default) => ReadDocument(bytes, origin).World;

    public static NativeVoxelDocument ReadDocument(byte[] bytes, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 24)
        {
            throw new InvalidDataException("大世界模型文件头不完整");
        }

        var (width, depth, height, count) = ValidateHeader(bytes, bytes.Length);
        using var stream = new MemoryStream(bytes, writable: false);
        stream.Position = 24L + (long)count * RecordSize;
        var materials = ReadMetadata(stream, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        var voxelOrigin = origin + materials.OriginOffset;
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        for (var index = 0; index < count; index++)
        {
            ParseRecord(bytes.AsSpan(24 + index * RecordSize, RecordSize), width, height, depth, voxelOrigin, writer);
        }
        return new NativeVoxelDocument(world, materials);
    }

    /// <summary>仅读取原生模型的材质元数据，不解码体素记录。</summary>
    public static MaterialExchangeMetadata ReadMaterials(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var header = new byte[24];
        ReadFully(stream, header, header.Length);
        var (_, _, _, count) = ValidateHeader(header, stream.Length);
        stream.Position = 24L + (long)count * RecordSize;
        return ReadMetadata(stream, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)));
    }

    private static MaterialExchangeMetadata ReadMetadata(Stream stream, int version)
    {
        if (version == 2) return new MaterialExchangeMetadata();
        var lengthBytes = new byte[4];
        ReadFully(stream, lengthBytes, 4);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length < 1 || length > MaterialExchangeCodec.MaximumBytes || stream.Length - stream.Position != length)
            throw new InvalidDataException("材质元数据长度无效");
        var bytes = new byte[length];
        ReadFully(stream, bytes, length);
        return MaterialExchangeCodec.Decode(bytes);
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

}
