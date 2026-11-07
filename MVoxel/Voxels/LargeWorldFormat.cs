using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// MVoxel 原生体素资源格式。当前默认写出 v5：文件头与区块索引使用 Int64 计数和偏移，
/// 体素按 32³ 区块组织、块内使用调色板索引压缩颜色并保留材质槽与渲染标志；
/// 只读写当前 v5，不为旧版本文件保留兼容或迁移路径。
/// </summary>
public static class LargeWorldFormat
{
    private const uint Magic = 0x3158564D;
    private const int Version = 5;
    private const int MaximumObjectSegments = 4096;
    private const int MaximumObjectNameBytes = 1 * 1024 * 1024;
    private const int MaximumObjectIdBytes = 256;

    /// <summary>v5 文件头长度</summary>
    private const int V5HeaderSize = 120;
    /// <summary>v5 区块索引条目长度</summary>
    private const int V5ChunkIndexEntrySize = 48;
    /// <summary>v5 区块载荷开头的调色板条目数长度</summary>
    private const int V5ChunkBlobHeaderSize = 4;
    /// <summary>v5 调色板条目：RGBA + 材质槽 + 渲染标志</summary>
    private const int V5PaletteEntrySize = 7;
    /// <summary>v5 体素记录：局部 XYZ + 调色板索引</summary>
    private const int V5RecordSize = 5;
    private const uint V5FlagMetadata = 1;
    private const uint V5FlagMultiObject = 2;
    private const uint V5KnownFlags = V5FlagMetadata | V5FlagMultiObject;
    private const int V5MaximumPaletteEntries = VoxelWorld.ChunkVolume;

    /// <summary>v5 对象表固定条目长度：原点三轴、记录数、标识与名称长度</summary>
    private const long V5ObjectEntrySize = 40;

    /// <summary>原生v4对象片段，体素坐标相对于片段原点保存</summary>
    public sealed record NativeObjectSegment(string ObjectId, string Name, VoxelCoord64 Origin, VoxelWorld World);

    /// <summary>原生对象片段文档，导入方可据此逐对象恢复场景对象和局部材质绑定</summary>
    public sealed record NativeObjectDocument(IReadOnlyList<NativeObjectSegment> Objects,
        MaterialExchangeMetadata Materials);

    /// <summary>派生区块索引条目：区块在 v5 文件中的位置、长度与内容哈希，可随时重建</summary>
    public sealed record NativeChunkIndexEntry(int SegmentIndex, ChunkCoord64 Coordinate, long Offset,
        long Length, int PaletteCount, int RecordCount, string ContentHash);

    /// <summary>只读派生区块索引，不写入场景旁挂权威文件</summary>
    public sealed record NativeChunkIndex(long RecordCount, IReadOnlyList<NativeChunkIndexEntry> Chunks);

    /// <summary>v5 对象表条目，解析后用于构建对象世界或建立区块索引</summary>
    private sealed record V5ObjectHeaderEntry(string ObjectId, string Name, VoxelCoord64 Origin,
        long RecordCount);

    /// <summary>v5 区块写出计划，记录数与调色板数在写出前即可确定载荷长度</summary>
    private sealed record V5ChunkPlan(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk, int SegmentIndex,
        int RecordCount, int PaletteCount, long BlobOffset, long SourceOffset = -1, long SourceLength = -1)
    {
        internal long BlobLength => V5ChunkBlobHeaderSize
            + (long)PaletteCount * V5PaletteEntrySize
            + (long)RecordCount * V5RecordSize;
    }

    /// <summary>上次保存时单个区块的修订号与文件内位置，用于跳过未改区块的重编码</summary>
    private sealed class ChunkSnapshot
    {
        internal long Revision;
        internal long Offset;
        internal long Length;
        internal int RecordCount;
        internal int PaletteCount;
    }

    /// <summary>
    /// 上次成功保存留下的区块快照：文件长度与写入时间用于确认磁盘文件未被外部替换，
    /// 只有全部校验通过才复用旧载荷，否则退回整份重编码
    /// </summary>
    private sealed class SaveSnapshot
    {
        internal long FileLength;
        internal DateTime WriteTimeUtc;
        internal readonly Dictionary<(int SegmentIndex, ChunkCoord64 Coordinate), ChunkSnapshot> Chunks = new();
    }

    /// <summary>按世界实例保存的写入快照，世界销毁后自动回收</summary>
    private static readonly ConditionalWeakTable<VoxelWorld, Dictionary<(string Path, int SegmentIndex), SaveSnapshot>>
        SaveSnapshots = new();

    /// <summary>v5 区块索引条目</summary>
    private sealed record V5ChunkEntry(int SegmentIndex, ChunkCoord64 Coordinate, long BlobOffset, int RecordCount);

    /// <summary>已校验的 v5 文件头</summary>
    private sealed record V5Header(uint Flags, long RecordCount, long ChunkCount, long MetadataOffset,
        long MetadataLength, VoxelCoord64 Minimum, long Width, long Depth, long Height, long HeaderSize)
    {
        internal bool HasMetadata => (Flags & V5FlagMetadata) != 0;
        internal bool IsMultiObject => (Flags & V5FlagMultiObject) != 0;
        internal long MaximumX => Minimum.X + Width - 1;
        internal long MaximumY => Minimum.Y + Height - 1;
        internal long MaximumZ => Minimum.Z + Depth - 1;
    }

    /// <summary>
    /// 将稀疏世界保存为 v5 原生模型文件：按区块建立调色板索引载荷，
    /// 所有偏移使用 Int64，单模型不再受 2GB、记录数或每轴 1024 限制
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">待保存的稀疏世界</param>
    public static void Write(string path, VoxelWorld world) => Write(path, world, null);

    /// <summary>
    /// 保存独立模型时自动烘焙的 LOD 级数（含第 0 级）；小于 2 表示不写入。
    /// 编辑器按当前渲染配置设置该值，所有模型资产写入路径因此无需各自传参。
    /// </summary>
    public static int AutoBakeLodLevels { get; set; }

    public static void Write(string path, VoxelWorld world, MaterialExchangeMetadata? materials)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // 原地更新会在原文件上改写索引与文件头，保存期间需要与读取互斥
        WithPathWriteLock(path, () => WriteCore(path, world, materials));
    }

    /// <summary>
    /// 按当前 <see cref="AutoBakeLodLevels"/> 为一个世界生成预烘焙链；不需要时返回 null
    /// </summary>
    private static ModelLodChain? BakeLod(VoxelWorld world)
    {
        var levels = AutoBakeLodLevels;
        if (levels <= 1 || world.OccupiedVoxelCount == 0) return null;
        return ModelLodChainCache.Get(world, levels);
    }

    /// <summary>
    /// 把预烘焙链合并进文档元数据；空表表示清除上一次写入的烘焙数据
    /// </summary>
    private static MaterialExchangeMetadata? AttachBakedLod(MaterialExchangeMetadata? materials,
        IReadOnlyDictionary<string, ModelLodChain> baked)
    {
        var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (objectId, chain) in baked)
            if (chain.LevelCount > 1) encoded[objectId] = ModelLodChain.Encode(chain);
        if (encoded.Count == 0 && (materials is null || materials.BakedLod.Count == 0)) return materials;
        var source = materials ?? new MaterialExchangeMetadata();
        return new MaterialExchangeMetadata
        {
            OriginOffset = source.OriginOffset,
            Bindings = new Dictionary<ushort, string>(source.Bindings),
            ObjectBindings = source.ObjectBindings.ToDictionary(pair => pair.Key,
                pair => new Dictionary<ushort, string>(pair.Value), StringComparer.Ordinal),
            Definitions = new Dictionary<string, Project.MaterialAsset>(source.Definitions, StringComparer.Ordinal),
            BakedLod = encoded,
        };
    }

    private static void WriteCore(string path, VoxelWorld world, MaterialExchangeMetadata? materials)
    {
        RecoverInPlaceJournal(path);
        var effectiveMaterials = AttachBakedLod(materials, new Dictionary<string, ModelLodChain>
        {
            [string.Empty] = BakeLod(world) ?? ModelLodChain.Empty,
        });
        var metadata = effectiveMaterials is null ? Array.Empty<byte>() : MaterialExchangeCodec.Encode(effectiveMaterials);
        var startContentVersion = world.ContentVersion;
        var startStructureStamp = world.StructureStamp;
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            WriteEmptyCore(path, effectiveMaterials);
            return;
        }

        var width = checked(maximum.X - minimum.X + 1);
        var depth = checked(maximum.Z - minimum.Z + 1);
        var height = checked(maximum.Y - minimum.Y + 1);
        var chunks = SnapshotNonEmptyChunks(world);
        var snapshot = FindSaveSnapshot(world, path, 0);
        FileStream? reuseStream = OpenReuseStream(path, snapshot is null ? [] : [snapshot]);
        try
        {
            var plans = BuildChunkPlans(chunks, 0, reuseStream is null ? null : snapshot);
            if (startContentVersion != world.ContentVersion || startStructureStamp != world.StructureStamp)
                throw new InvalidOperationException("写模型期间世界被编辑，请稍后重试保存");

            bool Consistent() => world.ContentVersion == startContentVersion &&
                                 world.StructureStamp == startStructureStamp;
            // 区块数量与旧文件一致时直接把脏区块追加到旧文件并修补索引与头，只写脏块；
            // 追加会使碎片增长，超过阈值或结构变化时改走整份重写压缩
            if (TryWriteInPlace(path, plans, metadata, minimum, width, depth, height, Consistent,
                    ref reuseStream))
            {
                RecordSaveSnapshot([(world, 0)], path, plans);
                return;
            }

            // 原地更新失败后旧文件读取源可能已关闭，重新打开再复用未改区块载荷
            reuseStream ??= OpenReuseStream(path, snapshot is null ? [] : [snapshot]);
            WriteV5Document(path, null, plans, Array.Empty<long>(), minimum, width, depth, height, metadata,
                Consistent, reuseStream);
            RecordSaveSnapshot([(world, 0)], path, plans);
        }
        finally
        {
            reuseStream?.Dispose();
        }
    }

    /// <summary>写入多对象片段文件；每个片段保留自己的标识、名称和原点</summary>
    public static void WriteObjects(string path, IReadOnlyList<NativeObjectSegment> objects,
        MaterialExchangeMetadata? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WithPathWriteLock(path, () => WriteObjectsCore(path, objects, materials));
    }

    private static void WriteObjectsCore(string path, IReadOnlyList<NativeObjectSegment> objects,
        MaterialExchangeMetadata? materials)
    {
        RecoverInPlaceJournal(path);
        ValidateObjectSegments(objects);
        var startVersions = objects.Select(segment => segment.World.ContentVersion).ToArray();
        var startStamps = objects.Select(segment => segment.World.StructureStamp).ToArray();
        var baked = new Dictionary<string, ModelLodChain>(StringComparer.Ordinal);
        foreach (var segment in objects)
        {
            var chain = BakeLod(segment.World);
            if (chain is not null) baked[segment.ObjectId] = chain;
        }

        var effectiveMaterials = AttachBakedLod(materials, baked);
        var metadata = effectiveMaterials is null ? Array.Empty<byte>() : MaterialExchangeCodec.Encode(effectiveMaterials);
        var plans = new List<V5ChunkPlan>();
        var segmentRecordCounts = new long[objects.Count];
        var minimum = new VoxelCoord64(long.MaxValue, long.MaxValue, long.MaxValue);
        var maximum = new VoxelCoord64(long.MinValue, long.MinValue, long.MinValue);
        var found = false;
        // 多对象文档共用一个旧文件读取源，各片段用自己的世界快照判断复用
        var snapshots = new SaveSnapshot?[objects.Count];
        for (var segmentIndex = 0; segmentIndex < objects.Count; segmentIndex++)
        {
            snapshots[segmentIndex] = FindSaveSnapshot(objects[segmentIndex].World, path, segmentIndex);
        }

        using var reuseStream = OpenReuseStream(path, snapshots);
        for (var segmentIndex = 0; segmentIndex < objects.Count; segmentIndex++)
        {
            var world = objects[segmentIndex].World;
            if (!world.TryGetBounds(out var segmentMinimum, out var segmentMaximum)) continue;
            if (!found)
            {
                minimum = segmentMinimum;
                maximum = segmentMaximum;
                found = true;
            }
            else
            {
                minimum = new VoxelCoord64(Math.Min(minimum.X, segmentMinimum.X),
                    Math.Min(minimum.Y, segmentMinimum.Y), Math.Min(minimum.Z, segmentMinimum.Z));
                maximum = new VoxelCoord64(Math.Max(maximum.X, segmentMaximum.X),
                    Math.Max(maximum.Y, segmentMaximum.Y), Math.Max(maximum.Z, segmentMaximum.Z));
            }

            var chunks = SnapshotNonEmptyChunks(world);
            var start = plans.Count;
            plans.AddRange(BuildChunkPlans(chunks, segmentIndex, reuseStream is null ? null : snapshots[segmentIndex]));
            foreach (var plan in plans.GetRange(start, plans.Count - start))
            {
                segmentRecordCounts[segmentIndex] = checked(segmentRecordCounts[segmentIndex] + plan.RecordCount);
            }
        }

        if (!found)
        {
            minimum = VoxelCoord64.Zero;
            maximum = VoxelCoord64.Zero;
        }

        var width = found ? checked(maximum.X - minimum.X + 1) : 1;
        var depth = found ? checked(maximum.Z - minimum.Z + 1) : 1;
        var height = found ? checked(maximum.Y - minimum.Y + 1) : 1;
        for (var segmentIndex = 0; segmentIndex < objects.Count; segmentIndex++)
        {
            if (objects[segmentIndex].World.ContentVersion != startVersions[segmentIndex] ||
                objects[segmentIndex].World.StructureStamp != startStamps[segmentIndex])
            {
                throw new InvalidOperationException("写入对象片段期间体素发生变化，请重新保存");
            }
        }

        WriteV5Document(path, objects, plans, segmentRecordCounts, minimum, width, depth, height, metadata,
            () =>
            {
                for (var index = 0; index < objects.Count; index++)
                {
                    if (objects[index].World.ContentVersion != startVersions[index] ||
                        objects[index].World.StructureStamp != startStamps[index]) return false;
                }

                return true;
            }, reuseStream);
        RecordSaveSnapshot(objects.Select((segment, index) => (segment.World, index)), path, plans);
    }

    /// <summary>
    /// 校验片段具备写出后可被自身读回的条件：写出侧与读取侧的标识、名称、
    /// 对象数量限制保持一致，避免产出读取阶段才报错的资产
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
            if (Encoding.UTF8.GetByteCount(segment.ObjectId) > MaximumObjectIdBytes)
                throw new ArgumentException("对象标识过长", nameof(objects));
            if (!objectIds.Add(segment.ObjectId))
                throw new ArgumentException($"对象片段标识重复：{segment.ObjectId}", nameof(objects));
            if (Encoding.UTF8.GetByteCount(segment.Name ?? string.Empty) > MaximumObjectNameBytes)
                throw new ArgumentException("对象名称过长", nameof(objects));
        }
    }

    /// <summary>判断文件是否为 v5 多对象原生文档</summary>
    public static bool IsObjectDocument(string path)
    {
        RecoverInPlaceJournalLocked(path);
        return WithPathReadLock(path, () => IsObjectDocumentCore(path));
    }

    private static bool IsObjectDocumentCore(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 12) return false;
        var magicVersion = new byte[8];
        stream.ReadExactly(magicVersion);
        if (BinaryPrimitives.ReadUInt32LittleEndian(magicVersion) != Magic) return false;
        var version = BinaryPrimitives.ReadInt32LittleEndian(magicVersion.AsSpan(4));
        if (version != Version) return false;
        var flagBytes = new byte[4];
        stream.ReadExactly(flagBytes);
        return (BinaryPrimitives.ReadUInt32LittleEndian(flagBytes) & V5FlagMultiObject) != 0;
    }

    /// <summary>合成多对象体素以供预览；重叠位置由后面的对象覆盖。</summary>
    public static VoxelWorld ReadObjectPreview(string path)
    {
        var document = ReadObjects(path);
        var world = new VoxelWorld();
        foreach (var segment in document.Objects)
            foreach (var (coordinate, value) in segment.World.EnumerateOccupied()) world.Set(coordinate, value);
        return world;
    }

    /// <summary>读取 v5 多对象文件</summary>
    public static NativeObjectDocument ReadObjects(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RecoverInPlaceJournalLocked(path);
        return WithPathReadLock(path, () => ReadObjectsCore(path));
    }

    private static NativeObjectDocument ReadObjectsCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        return ReadObjects(stream);
    }

    /// <summary>从字节读取 v5 多对象文件</summary>
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
        var magicVersion = new byte[8];
        ReadFully(stream, magicVersion, magicVersion.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(magicVersion) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(magicVersion.AsSpan(4)) != Version)
        {
            throw new InvalidDataException("不是受支持的 MVoxel v5 多对象文件");
        }

        stream.Position = 0;
        return ReadObjectsV5(stream);
    }

    /// <summary>写入空模型；使用 v5 头，读取端只有一条格式路径</summary>
    public static void WriteEmpty(string path, MaterialExchangeMetadata? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WithPathWriteLock(path, () => WriteEmptyCore(path, materials));
    }

    private static void WriteEmptyCore(string path, MaterialExchangeMetadata? materials)
    {
        RecoverInPlaceJournal(path);
        var metadata = materials is null ? Array.Empty<byte>() : MaterialExchangeCodec.Encode(materials);
        var bytes = new byte[V5HeaderSize + metadata.Length];
        var flags = metadata.Length == 0 ? 0u : V5FlagMetadata;
        WriteV5Header(bytes, flags, 0, 0, metadata.Length == 0 ? 0 : V5HeaderSize, metadata.Length,
            VoxelCoord64.Zero, 1, 1, 1);
        metadata.CopyTo(bytes, V5HeaderSize);
        AtomicFile.WriteBytes(path, bytes);
    }

    /// <summary>
    /// 按文件路径的读写闸门：整份重写依靠原子替换对读者无感，
    /// 原地增量保存则直接改写原文件，必须让同路径的读取与保存互斥。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        ReaderWriterLockSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);

    private static T WithPathReadLock<T>(string path, Func<T> action)
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new ReaderWriterLockSlim());
        gate.EnterReadLock();
        try
        {
            return action();
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    private static void WithPathWriteLock(string path, Action action)
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new ReaderWriterLockSlim());
        gate.EnterWriteLock();
        try
        {
            action();
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    /// <summary>没有残留回滚日志时走无锁快检；有日志时在写锁内恢复，避免与并发的读取或保存交错</summary>
    private static void RecoverInPlaceJournalLocked(string path)
    {
        if (!File.Exists(path + InPlaceJournalSuffix)) return;
        var gate = PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new ReaderWriterLockSlim());
        gate.EnterWriteLock();
        try
        {
            RecoverInPlaceJournal(path);
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    /// <summary>原地增量保存的回滚日志后缀：与模型同目录，属于临时事务文件</summary>
    internal const string InPlaceJournalSuffix = ".mvjournal";
    private const uint InPlaceJournalMagic = 0x4E4A564D;
    private const int InPlaceJournalHeaderSize = 48;
    private const int InPlaceJournalVersion = 1;

    /// <summary>
    /// 原地增量保存：区块数量与旧文件一致时，未编辑区块的载荷留在原位置，
    /// 只把脏区块追加到文件末尾，再重写区块索引与文件头。
    /// 追加会使旧载荷成为碎片，最终长度超过有效数据两倍时放弃并改走整份重写压缩。
    /// 写盘前先落一份回滚日志（原文件长度、原文件头、原区块索引），
    /// 中途崩溃或写入失败时按日志恢复旧状态，避免留下读不回来的半成品。
    /// </summary>
    /// <returns>写入成功返回 true；旧文件结构不适用或已回滚返回 false</returns>
    private static bool TryWriteInPlace(string path, List<V5ChunkPlan> plans, byte[] metadata,
        VoxelCoord64 minimum, long width, long depth, long height, Func<bool> consistencyCheck,
        ref FileStream? reuseStream)
    {
        if (reuseStream is null || plans.Count == 0) return false;
        V5Header oldHeader;
        try
        {
            oldHeader = ReadV5Header(reuseStream, reuseStream.Length);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (oldHeader.IsMultiObject || oldHeader.ChunkCount != plans.Count) return false;
        foreach (var plan in plans)
        {
            if (plan.SegmentIndex != 0) return false;
        }

        var indexStart = oldHeader.HeaderSize;
        var oldLength = reuseStream.Length;
        var originalHeader = new byte[V5HeaderSize];
        var originalIndex = new byte[checked(plans.Count * V5ChunkIndexEntrySize)];
        try
        {
            reuseStream.Position = 0;
            ReadFully(reuseStream, originalHeader, originalHeader.Length);
            reuseStream.Position = indexStart;
            ReadFully(reuseStream, originalIndex, originalIndex.Length);
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
        {
            return false;
        }

        // 脏区块追加到旧文件末尾，未编辑区块继续引用原偏移
        var appendStart = oldLength;
        long recordCount = 0;
        long liveLength = indexStart + (long)plans.Count * V5ChunkIndexEntrySize + metadata.Length;
        var appended = new List<V5ChunkPlan>();
        foreach (var plan in plans)
        {
            liveLength = checked(liveLength + plan.BlobLength);
            recordCount = checked(recordCount + plan.RecordCount);
        }

        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            if (plan.SourceOffset >= 0 && plan.SourceLength == plan.BlobLength)
            {
                plans[index] = plan with { BlobOffset = plan.SourceOffset };
                continue;
            }

            plans[index] = plan with { BlobOffset = appendStart };
            appended.Add(plans[index]);
            appendStart = checked(appendStart + plan.BlobLength);
        }

        var metadataOffset = metadata.Length == 0 ? 0L : appendStart;
        var fileLength = checked(appendStart + metadata.Length);
        // 旧载荷与旧元数据留在文件中成为碎片；碎片过多时改走整份重写回收空间
        if (fileLength > liveLength + liveLength / 2) return false;

        var indexBytes = new byte[checked(plans.Count * V5ChunkIndexEntrySize)];
        var entry = new byte[V5ChunkIndexEntrySize];
        for (var index = 0; index < plans.Count; index++)
        {
            WriteChunkIndexEntry(entry, plans[index]);
            Buffer.BlockCopy(entry, 0, indexBytes, index * V5ChunkIndexEntrySize, V5ChunkIndexEntrySize);
        }

        var headerBytes = new byte[V5HeaderSize];
        WriteV5Header(headerBytes, metadata.Length == 0 ? 0u : V5FlagMetadata, recordCount, plans.Count,
            metadataOffset, metadata.Length, minimum, width, depth, height);
        if (oldHeader.HeaderSize != V5HeaderSize)
            BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(12), (int)oldHeader.HeaderSize);

        var journalPath = path + InPlaceJournalSuffix;
        var journalLength = InPlaceJournalHeaderSize + V5HeaderSize * 2 + originalIndex.Length;
        var journal = new byte[journalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(journal, InPlaceJournalMagic);
        BinaryPrimitives.WriteInt32LittleEndian(journal.AsSpan(4), InPlaceJournalVersion);
        BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(8), oldLength);
        BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(16), indexStart);
        BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(24), plans.Count);
        BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(32), fileLength);
        originalHeader.CopyTo(journal, InPlaceJournalHeaderSize);
        headerBytes.CopyTo(journal, InPlaceJournalHeaderSize + V5HeaderSize);
        originalIndex.CopyTo(journal, InPlaceJournalHeaderSize + V5HeaderSize * 2);
        try
        {
            using var journalStream = new FileStream(journalPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, FileOptions.WriteThrough);
            journalStream.Write(journal, 0, journal.Length);
            journalStream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteJournal(journalPath);
            return false;
        }

        // 回滚日志已落盘，可以放开旧文件读取源并独占写入
        reuseStream.Dispose();
        reuseStream = null;
        var maximumBlobLength = 0;
        foreach (var plan in plans)
        {
            maximumBlobLength = Math.Max(maximumBlobLength, checked((int)plan.BlobLength));
        }

        FileStream? writer = null;
        try
        {
            FileEditHistory.BeforeWrite(path);
            writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

            // 1. 追加脏区块载荷与新的材质元数据
            var buffer = new byte[maximumBlobLength];
            using var appendedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            writer.Position = oldLength;
            foreach (var plan in appended)
            {
                SerializeChunkBlob(buffer, plan);
                var length = checked((int)plan.BlobLength);
                writer.Write(buffer, 0, length);
                appendedHash.AppendData(buffer, 0, length);
            }

            if (metadata.Length > 0)
            {
                writer.Write(metadata, 0, metadata.Length);
                appendedHash.AppendData(metadata, 0, metadata.Length);
            }

            // 2. 重写区块索引
            writer.Position = indexStart;
            writer.Write(indexBytes, 0, indexBytes.Length);
            writer.Flush(flushToDisk: true);

            // 3. 回读追加区与索引，确认磁盘内容可信；按固定窗口流式校验，避免整段追加区驻留内存
            var expectedAppendHash = appendedHash.GetHashAndReset();
            var appendLength = appendStart - oldLength + metadata.Length;
            var verifyBuffer = new byte[Math.Min(Math.Max(appendLength, 1), 1 << 20)];
            using (var verifyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                writer.Position = oldLength;
                var remaining = appendLength;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, verifyBuffer.Length);
                    ReadFully(writer, verifyBuffer, count);
                    verifyHash.AppendData(verifyBuffer, 0, count);
                    remaining -= count;
                }

                if (!verifyHash.GetHashAndReset().AsSpan().SequenceEqual(expectedAppendHash))
                    throw new IOException("原地保存追加区校验失败");
            }

            writer.Position = indexStart;
            var verifyIndex = new byte[indexBytes.Length];
            ReadFully(writer, verifyIndex, verifyIndex.Length);
            if (!verifyIndex.AsSpan().SequenceEqual(indexBytes))
                throw new IOException("原地保存区块索引校验失败");

            if (writer.Length != fileLength)
                throw new IOException("原地保存文件长度与索引不一致");

            // 4. 世界在写入期间被编辑时回滚，不提交新文件头
            if (!consistencyCheck())
                throw new InvalidOperationException("写入模型期间体素发生变化，请重新保存");

            // 5. 最后写入文件头作为提交点
            writer.Position = 0;
            writer.Write(headerBytes, 0, headerBytes.Length);
            writer.Flush(flushToDisk: true);
            writer.Position = 0;
            var verifyHeader = new byte[V5HeaderSize];
            ReadFully(writer, verifyHeader, verifyHeader.Length);
            if (!verifyHeader.AsSpan().SequenceEqual(headerBytes))
                throw new IOException("原地保存文件头校验失败");
        }
        catch (Exception exception)
        {
            writer?.Dispose();
            writer = null;
            if (TryRestoreInPlace(path, oldLength, originalHeader, indexStart, originalIndex))
                TryDeleteJournal(journalPath);
            // 世界在写入期间被编辑不是保存环境问题，回滚后仍要上报给调用方
            if (exception is InvalidOperationException) throw;
            if (exception is not (IOException or UnauthorizedAccessException)) throw;
            return false;
        }
        finally
        {
            writer?.Dispose();
        }

        TryDeleteJournal(journalPath);
        return true;
    }

    /// <summary>按回滚日志把目标文件恢复到保存前的长度、文件头与区块索引</summary>
    private static bool TryRestoreInPlace(string path, long originalLength, byte[] originalHeader,
        long indexStart, byte[] originalIndex)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
            // 先截掉追加区，再补回旧头与旧索引
            if (stream.Length > originalLength) stream.SetLength(originalLength);
            stream.Position = 0;
            stream.Write(originalHeader, 0, originalHeader.Length);
            if (originalIndex.Length > 0 && stream.Length >= indexStart + originalIndex.Length)
            {
                stream.Position = indexStart;
                stream.Write(originalIndex, 0, originalIndex.Length);
            }

            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 检查并处理残留的原地保存回滚日志：目标已是提交后的完整状态则删除日志，
    /// 否则按日志恢复旧文件头与索引并截掉未提交的追加区
    /// </summary>
    private static void RecoverInPlaceJournal(string path)
    {
        var journalPath = path + InPlaceJournalSuffix;
        if (!File.Exists(journalPath)) return;
        byte[] journal;
        try
        {
            journal = File.ReadAllBytes(journalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (journal.Length < InPlaceJournalHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(journal) != InPlaceJournalMagic ||
            BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(4)) != InPlaceJournalVersion)
        {
            // 日志尚未写全，说明目标文件还没被修改
            TryDeleteJournal(journalPath);
            return;
        }

        var originalLength = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(8));
        var indexStart = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(16));
        var chunkCount = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(24));
        var newLength = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(32));
        var expected = InPlaceJournalHeaderSize + V5HeaderSize * 2 + chunkCount * V5ChunkIndexEntrySize;
        if (chunkCount < 0 || chunkCount > int.MaxValue / V5ChunkIndexEntrySize ||
            journal.Length != expected || indexStart < 0 || originalLength < 0)
        {
            TryDeleteJournal(journalPath);
            return;
        }

        var originalHeader = new byte[V5HeaderSize];
        Array.Copy(journal, InPlaceJournalHeaderSize, originalHeader, 0, originalHeader.Length);
        var newHeader = new byte[V5HeaderSize];
        Array.Copy(journal, InPlaceJournalHeaderSize + V5HeaderSize, newHeader, 0, newHeader.Length);
        var originalIndex = new byte[chunkCount * V5ChunkIndexEntrySize];
        Array.Copy(journal, InPlaceJournalHeaderSize + V5HeaderSize * 2, originalIndex, 0,
            originalIndex.Length);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
            if (stream.Length == newLength)
            {
                var header = new byte[V5HeaderSize];
                stream.Position = 0;
                ReadFully(stream, header, header.Length);
                if (header.AsSpan().SequenceEqual(newHeader))
                {
                    // 提交已完成，只是没来得及删除日志
                    TryDeleteJournal(journalPath);
                    return;
                }
            }

            if (stream.Length < originalLength)
            {
                throw new IOException($"模型文件存在未完成的原地保存，长度不足以回滚：{path}");
            }

            stream.SetLength(originalLength);
            stream.Position = 0;
            stream.Write(originalHeader, 0, originalHeader.Length);
            if (originalIndex.Length > 0)
            {
                stream.Position = indexStart;
                stream.Write(originalIndex, 0, originalIndex.Length);
            }

            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Error($"恢复原地保存回滚日志失败：{path}", exception);
            throw new IOException($"模型文件存在未完成的原地保存且无法回滚：{path}", exception);
        }

        TryDeleteJournal(journalPath);
        MVoxelLog.Warn($"已按回滚日志恢复未完成的原地保存：{path}");
    }

    private static void TryDeleteJournal(string journalPath)
    {
        try
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Warn($"无法删除原地保存回滚日志：{journalPath}（{exception.Message}）");
        }
    }

    /// <summary>
    /// 把 v5 文档顺序写入临时文件：头、对象表、区块索引、区块载荷、材质元数据，
    /// 写入过程同时计算整文件 SHA-256，回读比对通过且世界未被编辑才原子替换目标
    /// </summary>
    private static void WriteV5Document(string path, IReadOnlyList<NativeObjectSegment>? objects,
        List<V5ChunkPlan> plans, long[] segmentRecordCounts, VoxelCoord64 minimum,
        long width, long depth, long height, byte[] metadata, Func<bool> consistencyCheck,
        Stream? reuseStream = null)
    {
        var flags = (metadata.Length == 0 ? 0u : V5FlagMetadata) |
                    (objects is null ? 0u : V5FlagMultiObject);
        var objectTableLength = ComputeObjectTableLength(objects);
        var headerAndTable = checked(V5HeaderSize + objectTableLength);
        var indexLength = checked((long)plans.Count * V5ChunkIndexEntrySize);
        var dataStart = checked(headerAndTable + indexLength);
        var recordCount = 0L;
        var offset = dataStart;
        for (var index = 0; index < plans.Count; index++)
        {
            plans[index] = plans[index] with { BlobOffset = offset };
            recordCount = checked(recordCount + plans[index].RecordCount);
            offset = checked(offset + plans[index].BlobLength);
        }

        var metadataOffset = metadata.Length == 0 ? 0L : offset;
        var fileLength = checked(offset + metadata.Length);
        var temporaryPath = AtomicFile.BeginTemporary(path);
        try
        {
            byte[] writtenHash;
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1 << 20))
            {
                writtenHash = WriteV5Stream(stream, flags, objects, plans, segmentRecordCounts, recordCount,
                    metadataOffset, metadata, minimum, width, depth, height, reuseStream);
                stream.Flush(flushToDisk: true);
                if (stream.Length != fileLength)
                    throw new IOException("大世界模型写出长度与索引不一致");
            }

            VerifyTemporary(temporaryPath, writtenHash);
            if (!consistencyCheck())
                throw new InvalidOperationException("写入模型期间体素发生变化，请重新保存");
            AtomicFile.CommitTemporary(temporaryPath, path);
        }
        finally
        {
            AtomicFile.DiscardTemporary(temporaryPath);
        }
    }

    private static byte[] WriteV5Stream(Stream stream, uint flags, IReadOnlyList<NativeObjectSegment>? objects,
        List<V5ChunkPlan> plans, long[] segmentRecordCounts, long recordCount, long metadataOffset,
        byte[] metadata, VoxelCoord64 minimum, long width, long depth, long height, Stream? reuseStream = null)
    {
        using var hash = SHA256.Create();
        using (var hashed = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            var header = new byte[V5HeaderSize];
            WriteV5Header(header, flags, recordCount, (long)plans.Count, metadataOffset, metadata.Length,
                minimum, width, depth, height);
            hashed.Write(header, 0, header.Length);

            if (objects is not null) WriteObjectTable(hashed, objects, segmentRecordCounts);

            var entry = new byte[V5ChunkIndexEntrySize];
            foreach (var plan in plans)
            {
                WriteChunkIndexEntry(entry, plan);
                hashed.Write(entry, 0, entry.Length);
            }

            // 未编辑区块直接复用上次写入的载荷字节，只对脏区块重新编码；
            // 载荷缓冲按最大区块长度复用，避免逐区块分配
            var maximumBlobLength = 0;
            foreach (var plan in plans)
            {
                maximumBlobLength = Math.Max(maximumBlobLength, checked((int)plan.BlobLength));
            }

            var buffer = new byte[maximumBlobLength];
            foreach (var plan in plans)
            {
                var length = checked((int)plan.BlobLength);
                if (reuseStream is not null && plan.SourceOffset >= 0 &&
                    TryCopyReusedBlob(reuseStream, buffer, plan))
                {
                    // 已从旧文件读回等长的原载荷
                }
                else
                {
                    SerializeChunkBlob(buffer, plan);
                }

                hashed.Write(buffer, 0, length);
            }

            if (metadata.Length > 0) hashed.Write(metadata, 0, metadata.Length);
            hashed.FlushFinalBlock();
        }

        return hash.Hash ?? throw new IOException("模型写入哈希计算失败");
    }

    /// <summary>回读临时文件重算哈希，与写入结果不一致说明磁盘内容不可信</summary>
    private static void VerifyTemporary(string temporaryPath, byte[] expectedHash)
    {
        using var verify = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        if (!SHA256.HashData(verify).AsSpan().SequenceEqual(expectedHash))
            throw new IOException("大世界模型写入校验失败，原资产未替换");
    }

    /// <summary>v5 文件头：签名、版本、标志、Int64 计数/偏移、包围盒与尺寸</summary>
    private static void WriteV5Header(byte[] header, uint flags, long recordCount, long chunkCount,
        long metadataOffset, long metadataLength, VoxelCoord64 minimum, long width, long depth, long height)
    {
        var span = header.AsSpan(0, V5HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], V5HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], recordCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], chunkCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], metadataOffset);
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], metadataLength);
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], minimum.X);
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], minimum.Y);
        BinaryPrimitives.WriteInt64LittleEndian(span[64..], minimum.Z);
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], width);
        BinaryPrimitives.WriteInt64LittleEndian(span[80..], depth);
        BinaryPrimitives.WriteInt64LittleEndian(span[88..], height);
        BinaryPrimitives.WriteInt64LittleEndian(span[96..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(span[104..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(span[112..], 0);
    }

    private static long ComputeObjectTableLength(IReadOnlyList<NativeObjectSegment>? objects)
    {
        if (objects is null) return 0;
        var total = 8L;
        foreach (var segment in objects)
        {
            total = checked(total + V5ObjectEntrySize
                + Encoding.UTF8.GetByteCount(segment.ObjectId)
                + Encoding.UTF8.GetByteCount(segment.Name ?? string.Empty));
        }

        return total;
    }

    private static void WriteObjectTable(Stream stream, IReadOnlyList<NativeObjectSegment> objects,
        long[] segmentRecordCounts)
    {
        Span<byte> countBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(countBytes, objects.Count);
        stream.Write(countBytes);
        var entry = new byte[V5ObjectEntrySize];
        for (var index = 0; index < objects.Count; index++)
        {
            var segment = objects[index];
            BinaryPrimitives.WriteInt64LittleEndian(entry, segment.Origin.X);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), segment.Origin.Y);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), segment.Origin.Z);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(24), segmentRecordCounts[index]);
            var idBytes = Encoding.UTF8.GetBytes(segment.ObjectId);
            var nameBytes = Encoding.UTF8.GetBytes(segment.Name ?? string.Empty);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(32), idBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(36), nameBytes.Length);
            stream.Write(entry);
            stream.Write(idBytes);
            stream.Write(nameBytes);
        }
    }

    private static void WriteChunkIndexEntry(byte[] entry, V5ChunkPlan plan)
    {
        BinaryPrimitives.WriteInt32LittleEndian(entry, plan.SegmentIndex);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), 0);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), plan.Coordinate.X);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), plan.Coordinate.Y);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(24), plan.Coordinate.Z);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(32), plan.BlobOffset);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(40), plan.RecordCount);
    }

    /// <summary>收集非空区块；空区块不写入文件</summary>
    private static List<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)> SnapshotNonEmptyChunks(
        VoxelWorld world)
    {
        var chunks = world.SnapshotChunks();
        var result = new List<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)>(chunks.Count);
        foreach (var (coordinate, chunk) in chunks)
        {
            if (chunk.OccupiedCount > 0) result.Add((coordinate, chunk));
        }

        return result;
    }

    private static List<V5ChunkPlan> BuildChunkPlans(
        IReadOnlyList<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)> chunks, int segmentIndex,
        SaveSnapshot? reuse = null)
    {
        var plans = new List<V5ChunkPlan>(chunks.Count);
        var distinct = new HashSet<VoxelValue>();
        foreach (var (coordinate, chunk) in chunks)
        {
            var reused = reuse is null ? null : TryReuseChunk(reuse, segmentIndex, coordinate, chunk);
            if (reused is not null)
            {
                plans.Add(reused);
                continue;
            }

            distinct.Clear();
            foreach (var value in chunk.Snapshot.Values) distinct.Add(value);
            plans.Add(new V5ChunkPlan(coordinate, chunk, segmentIndex, chunk.OccupiedCount, distinct.Count, -1));
        }

        return plans;
    }

    /// <summary>
    /// 判断区块自上次保存后是否未被编辑；只有修订号与记录数都与快照一致才复用旧载荷
    /// </summary>
    private static V5ChunkPlan? TryReuseChunk(SaveSnapshot reuse, int segmentIndex,
        ChunkCoord64 coordinate, SparseVoxelChunk chunk)
    {
        if (!reuse.Chunks.TryGetValue((segmentIndex, coordinate), out var saved)) return null;
        if (saved.Revision != chunk.Revision || saved.RecordCount != chunk.OccupiedCount) return null;
        var length = V5ChunkBlobHeaderSize
            + (long)saved.PaletteCount * V5PaletteEntrySize
            + (long)saved.RecordCount * V5RecordSize;
        if (length != saved.Length) return null;
        return new V5ChunkPlan(coordinate, chunk, segmentIndex, saved.RecordCount, saved.PaletteCount, -1,
            saved.Offset, saved.Length);
    }

    /// <summary>
    /// 从旧文件按偏移读回区块载荷；长度、调色板数与计划不一致时返回 false，交由重新编码
    /// </summary>
    private static bool TryCopyReusedBlob(Stream source, byte[] buffer, V5ChunkPlan plan)
    {
        var length = checked((int)plan.BlobLength);
        if (plan.SourceLength != length) return false;
        try
        {
            source.Position = plan.SourceOffset;
            var read = 0;
            while (read < length)
            {
                var count = source.Read(buffer, read, length - read);
                if (count <= 0) return false;
                read += count;
            }
        }
        catch (IOException)
        {
            return false;
        }

        return BinaryPrimitives.ReadInt32LittleEndian(buffer) == plan.PaletteCount;
    }

    /// <summary>
    /// 查找该世界上次写到此路径的快照
    /// </summary>
    private static SaveSnapshot? FindSaveSnapshot(VoxelWorld world, string path, int segmentIndex)
    {
        if (!SaveSnapshots.TryGetValue(world, out var map)) return null;
        return map.TryGetValue((path, segmentIndex), out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// 仅当快照对应的磁盘文件仍保持写入时的长度与时间戳时才打开旧文件读取源，
    /// 防止外部替换文件后按过期偏移复用错误载荷
    /// </summary>
    private static FileStream? OpenReuseStream(string path, IReadOnlyList<SaveSnapshot?> snapshots)
    {
        if (snapshots.Count == 0) return null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null) return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!info.Exists) return null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot!.FileLength != info.Length || snapshot.WriteTimeUtc != info.LastWriteTimeUtc)
                return null;
        }

        try
        {
            // 允许删除共享：提交阶段要替换目标文件，读取源不能独占目标
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                1 << 16, FileOptions.RandomAccess);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 保存成功后记录各区块的修订号与文件内位置，供下次保存复用未改区块的载荷
    /// </summary>
    private static void RecordSaveSnapshot(IEnumerable<(VoxelWorld World, int SegmentIndex)> worlds,
        string path, IReadOnlyList<V5ChunkPlan> plans)
    {
        long fileLength;
        DateTime writeTimeUtc;
        try
        {
            var info = new FileInfo(path);
            fileLength = info.Length;
            writeTimeUtc = info.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var (world, segmentIndex) in worlds)
        {
            var snapshot = new SaveSnapshot
            {
                FileLength = fileLength,
                WriteTimeUtc = writeTimeUtc,
            };
            foreach (var plan in plans)
            {
                if (plan.SegmentIndex != segmentIndex) continue;
                snapshot.Chunks[(segmentIndex, plan.Coordinate)] = new ChunkSnapshot
                {
                    Revision = plan.Chunk.Revision,
                    Offset = plan.BlobOffset,
                    Length = plan.BlobLength,
                    RecordCount = plan.RecordCount,
                    PaletteCount = plan.PaletteCount,
                };
            }

            var map = SaveSnapshots.GetValue(world, static _ => new Dictionary<(string, int), SaveSnapshot>());
            map[(path, segmentIndex)] = snapshot;
        }
    }

    /// <summary>
    /// 序列化区块载荷：调色板按颜色和材质字段排序以保证同内容输出稳定，
    /// 记录只保存块内局部坐标与调色板索引
    /// </summary>
    private static void SerializeChunkBlob(byte[] target, V5ChunkPlan plan)
    {
        if (plan.Chunk.OccupiedCount != plan.RecordCount)
            throw new InvalidOperationException("写模型期间区块体素数量发生变化，请重新保存");
        var palette = BuildPalette(plan.Chunk, plan.PaletteCount);
        if (palette.Count != plan.PaletteCount)
            throw new InvalidOperationException("写模型期间区块调色板发生变化，请重新保存");
        BinaryPrimitives.WriteInt32LittleEndian(target, palette.Count);
        var position = V5ChunkBlobHeaderSize;
        var indices = new Dictionary<VoxelValue, int>(palette.Count);
        for (var index = 0; index < palette.Count; index++)
        {
            var value = palette[index];
            indices[value] = index;
            target[position] = value.Color.R;
            target[position + 1] = value.Color.G;
            target[position + 2] = value.Color.B;
            target[position + 3] = value.Color.A;
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(position + 4), value.MaterialSlot);
            target[position + 6] = value.Flags;
            position += V5PaletteEntrySize;
        }

        foreach (var (index, value) in plan.Chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            target[position] = (byte)local.X;
            target[position + 1] = (byte)local.Y;
            target[position + 2] = (byte)local.Z;
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(position + 3), (ushort)indices[value]);
            position += V5RecordSize;
        }

        // 调用方按最大区块长度复用同一缓冲区，校验目标是本区块自身长度而不是整个缓冲区
        if (position != plan.BlobLength)
            throw new InvalidOperationException("区块载荷长度与索引不一致");
    }

    private static List<VoxelValue> BuildPalette(SparseVoxelChunk chunk, int expectedCount)
    {
        var distinct = new HashSet<VoxelValue>(Math.Max(1, expectedCount));
        foreach (var value in chunk.Snapshot.Values) distinct.Add(value);
        var palette = new List<VoxelValue>(distinct.Count);
        palette.AddRange(distinct);
        palette.Sort(static (left, right) => CompareVoxelValues(left, right));
        return palette;
    }

    private static int CompareVoxelValues(VoxelValue left, VoxelValue right)
    {
        var result = left.Color.R.CompareTo(right.Color.R);
        if (result != 0) return result;
        result = left.Color.G.CompareTo(right.Color.G);
        if (result != 0) return result;
        result = left.Color.B.CompareTo(right.Color.B);
        if (result != 0) return result;
        result = left.Color.A.CompareTo(right.Color.A);
        if (result != 0) return result;
        result = left.MaterialSlot.CompareTo(right.MaterialSlot);
        return result != 0 ? result : left.Flags.CompareTo(right.Flags);
    }

    /// <summary>
    /// 读取大世界模型文件：校验文件头与区块索引后逐区块流式解析，
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
        // 原地保存会在原文件上改写索引与文件头，读取期间必须与保存互斥
        RecoverInPlaceJournalLocked(path);
        return WithPathReadLock(path, () => ReadDocumentCore(path, origin, cancellationToken, reportProgress));
    }

    private static NativeVoxelDocument ReadDocumentCore(string path, VoxelCoord64 origin,
        CancellationToken cancellationToken, Action<int, int>? reportProgress)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        if (stream.Length < 24)
            throw new InvalidDataException("大世界模型文件头不完整");
        var peek = new byte[8];
        ReadFully(stream, peek, peek.Length);
        var version = BinaryPrimitives.ReadInt32LittleEndian(peek.AsSpan(4));
        stream.Position = 0;
        if (version != Version)
            throw new InvalidDataException("不是受支持的 MVoxel v5 原生模型文件，请在当前版本重新导出");
        return ReadV5Single(stream, origin, cancellationToken, reportProgress);
    }

    /// <summary>从字节读取 v5 原生模型文件</summary>
    /// <param name="bytes">完整文件内容</param>
    /// <param name="origin">模型在世界中的起点</param>
    /// <returns>读取出的稀疏世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default) => ReadDocument(bytes, origin).World;

    public static NativeVoxelDocument ReadDocument(byte[] bytes, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 24)
            throw new InvalidDataException("大世界模型文件头不完整");
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (version != Version)
            throw new InvalidDataException("不是受支持的 MVoxel v5 原生模型文件，请在当前版本重新导出");
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadV5Single(stream, origin, CancellationToken.None, null);
    }

    /// <summary>仅读取 v5 原生模型的材质元数据，不解码体素记录。</summary>
    public static MaterialExchangeMetadata ReadMaterials(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RecoverInPlaceJournalLocked(path);
        return WithPathReadLock(path, () => ReadMaterialsCore(path));
    }

    /// <summary>
    /// 读取独立模型资产中某个片段的预烘焙 LOD 链；单模型文件传空片段标识。
    /// 只解析元数据段，不解码体素记录。
    /// </summary>
    /// <param name="path">模型资产路径</param>
    /// <param name="objectSegment">片段标识，单模型文件为空字符串</param>
    /// <returns>预烘焙链，没有时返回 null</returns>
    public static ModelLodChain? ReadBakedLod(string path, string objectSegment)
    {
        var materials = ReadMaterials(path);
        return materials.BakedLod.TryGetValue(objectSegment ?? string.Empty, out var encoded)
            ? ModelLodChain.Decode(encoded)
            : null;
    }

    private static MaterialExchangeMetadata ReadMaterialsCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.SequentialScan);
        if (stream.Length < 24) throw new InvalidDataException("大世界模型文件头不完整");
        var peek = new byte[8];
        ReadFully(stream, peek, peek.Length);
        var version = BinaryPrimitives.ReadInt32LittleEndian(peek.AsSpan(4));
        if (version != Version)
            throw new InvalidDataException("不是受支持的 MVoxel v5 原生模型文件，请在当前版本重新导出");
        stream.Position = 0;
        var header = ReadV5Header(stream, stream.Length);
        if (!header.HasMetadata) return new MaterialExchangeMetadata();
        return ReadRawMetadata(stream, header.MetadataOffset, header.MetadataLength);
    }

    /// <summary>
    /// 读取 v5 单模型文档：解析头、区块索引与材质元数据后逐区块解码
    /// </summary>
    private static NativeVoxelDocument ReadV5Single(Stream stream, VoxelCoord64 origin,
        CancellationToken cancellationToken, Action<int, int>? reportProgress)
    {
        var header = ReadV5Header(stream, stream.Length);
        if (header.IsMultiObject)
            throw new InvalidDataException("对象片段文档请使用 ReadObjects 读取");
        var entries = ReadChunkIndex(stream, header, 1);
        var materials = ReadV5Metadata(stream, header);
        var voxelOrigin = origin + materials.OriginOffset;
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        var processed = 0L;
        var dataEnd = header.HasMetadata ? header.MetadataOffset : stream.Length;
        System.Threading.Tasks.Task<ChunkPayload[]>? pending = null;
        try
        {
            for (var start = 0; start < entries.Count; start += DecodeBatchChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(DecodeBatchChunks, entries.Count - start);
                // 预读与解码交错：下一批的异步读提前发出，当前批在本线程解码
                var payloads = pending is null
                    ? ReadPayloadBatchAsync(stream, entries, start, count, dataEnd, cancellationToken)
                        .GetAwaiter().GetResult()
                    : pending.GetAwaiter().GetResult();
                var nextStart = start + DecodeBatchChunks;
                pending = nextStart < entries.Count
                    ? ReadPayloadBatchAsync(stream, entries, nextStart,
                        Math.Min(DecodeBatchChunks, entries.Count - nextStart), dataEnd, cancellationToken)
                    : null;
                var decoded = DecodePayloadBatch(payloads, header, voxelOrigin, rebase: true, cancellationToken);
                for (var index = 0; index < decoded.Length; index++)
                {
                    var entry = entries[start + index];
                    if (entry.SegmentIndex != 0)
                        throw new InvalidDataException("单模型文档包含非零片段索引");
                    ApplyDecodedChunk(writer, decoded[index]);
                    processed += entry.RecordCount;
                }

                ReportProgress(reportProgress, processed, header.RecordCount);
            }
        }
        finally
        {
            AwaitPendingRead(pending);
        }

        if (processed != header.RecordCount)
            throw new InvalidDataException("大世界模型记录数与区块索引不一致");
        return new NativeVoxelDocument(world, materials);
    }

    /// <summary>读取 v5 多对象文档：每个片段各自解码为一个稀疏世界</summary>
    private static NativeObjectDocument ReadObjectsV5(Stream stream)
    {
        var header = ReadV5Header(stream, stream.Length);
        if (!header.IsMultiObject)
            throw new InvalidDataException("对象片段文档缺少对象表");
        var table = ReadV5ObjectTable(stream);
        var segmentCount = table.Count;
        var objects = new List<NativeObjectSegment>(segmentCount);
        var worlds = new List<VoxelWorld>(segmentCount);
        var writers = new List<VoxelWorld.BulkWriter>(segmentCount);
        var expectedCounts = new long[segmentCount];
        for (var index = 0; index < segmentCount; index++)
        {
            expectedCounts[index] = table[index].RecordCount;
            var world = new VoxelWorld();
            worlds.Add(world);
            writers.Add(world.CreateBulkWriter());
            objects.Add(new NativeObjectSegment(table[index].ObjectId, table[index].Name,
                table[index].Origin, world));
        }

        var entries = ReadChunkIndex(stream, header, segmentCount);
        var materials = ReadV5Metadata(stream, header);
        var decodedCounts = new long[segmentCount];
        var dataEnd = header.HasMetadata ? header.MetadataOffset : stream.Length;
        System.Threading.Tasks.Task<ChunkPayload[]>? pending = null;
        try
        {
            for (var start = 0; start < entries.Count; start += DecodeBatchChunks)
            {
                var count = Math.Min(DecodeBatchChunks, entries.Count - start);
                var payloads = pending is null
                    ? ReadPayloadBatchAsync(stream, entries, start, count, dataEnd, CancellationToken.None)
                        .GetAwaiter().GetResult()
                    : pending.GetAwaiter().GetResult();
                var nextStart = start + DecodeBatchChunks;
                pending = nextStart < entries.Count
                    ? ReadPayloadBatchAsync(stream, entries, nextStart,
                        Math.Min(DecodeBatchChunks, entries.Count - nextStart), dataEnd, CancellationToken.None)
                    : null;
                var decoded = DecodePayloadBatch(payloads, header, VoxelCoord64.Zero, rebase: false,
                    CancellationToken.None);
                for (var index = 0; index < decoded.Length; index++)
                {
                    var entry = entries[start + index];
                    if (entry.SegmentIndex < 0 || entry.SegmentIndex >= segmentCount)
                        throw new InvalidDataException("区块索引引用了不存在的对象片段");
                    ApplyDecodedChunk(writers[entry.SegmentIndex], decoded[index]);
                    decodedCounts[entry.SegmentIndex] += entry.RecordCount;
                }
            }
        }
        finally
        {
            AwaitPendingRead(pending);
        }

        for (var index = 0; index < segmentCount; index++)
        {
            if (decodedCounts[index] != expectedCounts[index])
                throw new InvalidDataException("对象片段体素数量与区块索引不一致");
        }

        return new NativeObjectDocument(objects, materials);
    }

    /// <summary>解析 v5 对象表：标识、名称、原点与记录数；读取后流位置移到区块索引</summary>
    private static List<V5ObjectHeaderEntry> ReadV5ObjectTable(Stream stream)
    {
        var countBytes = new byte[8];
        ReadFully(stream, countBytes, countBytes.Length);
        var count64 = BinaryPrimitives.ReadInt64LittleEndian(countBytes);
        if (count64 < 1 || count64 > MaximumObjectSegments)
            throw new InvalidDataException("v5 对象片段数量无效");
        var count = (int)count64;
        var entries = new List<V5ObjectHeaderEntry>(count);
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[V5ObjectEntrySize];
        for (var index = 0; index < count; index++)
        {
            ReadFully(stream, buffer, buffer.Length);
            var origin = new VoxelCoord64(
                BinaryPrimitives.ReadInt64LittleEndian(buffer),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)));
            var recordCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
            var idLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(32));
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(36));
            if (recordCount < 0 || idLength < 1 || idLength > MaximumObjectIdBytes ||
                nameLength < 0 || nameLength > MaximumObjectNameBytes ||
                idLength + nameLength > stream.Length - stream.Position)
            {
                throw new InvalidDataException("对象标识、名称或记录数无效");
            }

            var idBytes = new byte[idLength];
            var nameBytes = new byte[nameLength];
            ReadFully(stream, idBytes, idBytes.Length);
            ReadFully(stream, nameBytes, nameBytes.Length);
            var id = Encoding.UTF8.GetString(idBytes);
            var name = Encoding.UTF8.GetString(nameBytes);
            if (string.IsNullOrWhiteSpace(id) || !objectIds.Add(id))
                throw new InvalidDataException("对象标识为空或重复");
            entries.Add(new V5ObjectHeaderEntry(id, name, origin, recordCount));
        }

        return entries;
    }

    /// <summary>
    /// 由 v5 文件的区块索引派生只读区块索引：补充载荷长度、调色板数量与 SHA-256，
    /// 结果仅驻留内存，随时可从文件重建，不作为权威数据写入场景旁挂文件
    /// </summary>
    /// <param name="path">v5 模型文件路径</param>
    /// <param name="includeHashes">是否计算每个区块载荷的内容哈希</param>
    public static NativeChunkIndex ReadChunkIndex(string path, bool includeHashes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RecoverInPlaceJournalLocked(path);
        return WithPathReadLock(path, () => ReadChunkIndexCore(path, includeHashes));
    }

    private static NativeChunkIndex ReadChunkIndexCore(string path, bool includeHashes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.RandomAccess);
        if (stream.Length < V5HeaderSize)
            throw new InvalidDataException("大世界模型文件头不完整");
        var header = ReadV5Header(stream, stream.Length);
        var segmentCount = 1;
        if (header.IsMultiObject) segmentCount = ReadV5ObjectTable(stream).Count;
        var entries = ReadChunkIndex(stream, header, segmentCount);
        var dataEnd = header.HasMetadata ? header.MetadataOffset : stream.Length;
        var chunks = new List<NativeChunkIndexEntry>(entries.Count);
        var paletteHeader = new byte[V5ChunkBlobHeaderSize];
        foreach (var entry in entries)
        {
            stream.Position = entry.BlobOffset;
            ReadFully(stream, paletteHeader, paletteHeader.Length);
            var paletteCount = BinaryPrimitives.ReadInt32LittleEndian(paletteHeader);
            if (paletteCount < 1 || paletteCount > V5MaximumPaletteEntries ||
                paletteCount > entry.RecordCount)
            {
                throw new InvalidDataException("v5 模型区块调色板数量无效");
            }

            var length = checked(V5ChunkBlobHeaderSize + (long)paletteCount * V5PaletteEntrySize
                + (long)entry.RecordCount * V5RecordSize);
            if (entry.BlobOffset + length > dataEnd)
                throw new InvalidDataException("v5 模型区块载荷超出文件数据区");
            var contentHash = includeHashes ? HashBlob(stream, entry.BlobOffset, length) : string.Empty;
            chunks.Add(new NativeChunkIndexEntry(entry.SegmentIndex, entry.Coordinate, entry.BlobOffset,
                length, paletteCount, entry.RecordCount, contentHash));
        }

        return new NativeChunkIndex(header.RecordCount, chunks);
    }

    /// <summary>按字节偏移顺序读取区块载荷并计算 SHA-256，避免把大区块整块复制后重复读取</summary>
    private static string HashBlob(Stream stream, long offset, long length)
    {
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            ReadFully(stream, buffer, count);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>读取并校验 v5 文件头；文件长度用于拒绝越界索引</summary>
    private static V5Header ReadV5Header(Stream stream, long fileLength)
    {
        var header = new byte[V5HeaderSize];
        ReadFully(stream, header, header.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != Version)
        {
            throw new InvalidDataException("不是 MVoxel v5 大世界模型文件");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if ((flags & ~V5KnownFlags) != 0)
            throw new InvalidDataException("v5 模型包含未知标志");
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (headerSize < V5HeaderSize || headerSize > fileLength)
            throw new InvalidDataException("v5 模型文件头长度无效");
        var recordCount = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        var chunkCount = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
        var metadataOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
        var metadataLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
        var minimum = new VoxelCoord64(
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(64)));
        var width = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        var depth = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(80));
        var height = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(88));
        if (recordCount < 0 || chunkCount < 0 || chunkCount > (fileLength - headerSize) / V5ChunkIndexEntrySize ||
            width < 0 || depth < 0 || height < 0 ||
            (width > 0 && (depth <= 0 || height <= 0)) ||
            (depth > 0 && (width <= 0 || height <= 0)) ||
            (height > 0 && (width <= 0 || depth <= 0)))
        {
            throw new InvalidDataException("v5 模型计数或尺寸无效");
        }

        if (metadataLength < 0 || metadataLength > MaterialExchangeCodec.MaximumBytes ||
            (metadataLength > 0
                ? metadataOffset < headerSize || metadataOffset + metadataLength != fileLength
                : metadataOffset != 0))
        {
            throw new InvalidDataException("v5 模型材质元数据位置无效");
        }

        stream.Position = headerSize;
        return new V5Header(flags, recordCount, chunkCount, metadataOffset, metadataLength,
            minimum, width, depth, height, headerSize);
    }

    /// <summary>读取 v5 区块索引并校验条目范围；调用时流位置应在索引起点</summary>
    private static List<V5ChunkEntry> ReadChunkIndex(Stream stream, V5Header header, int segmentCount)
    {
        var entries = new List<V5ChunkEntry>(checked((int)Math.Min(header.ChunkCount, int.MaxValue)));
        var buffer = new byte[V5ChunkIndexEntrySize];
        var indexStart = stream.Position;
        var dataStart = checked(indexStart + header.ChunkCount * V5ChunkIndexEntrySize);
        var dataEnd = header.HasMetadata ? header.MetadataOffset : stream.Length;
        if (dataStart > dataEnd)
            throw new InvalidDataException("v5 模型区块索引超出文件范围");
        long totalRecords = 0;
        for (var index = 0; index < header.ChunkCount; index++)
        {
            ReadFully(stream, buffer, buffer.Length);
            var segmentIndex = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            var coordinate = new ChunkCoord64(
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
            var blobOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32));
            var recordCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(40));
            if (segmentIndex < 0 || segmentIndex >= segmentCount ||
                recordCount < 1 || recordCount > V5MaximumPaletteEntries ||
                blobOffset < dataStart || blobOffset > dataEnd - V5ChunkBlobHeaderSize)
            {
                throw new InvalidDataException("v5 模型区块索引条目无效");
            }

            entries.Add(new V5ChunkEntry(segmentIndex, coordinate, blobOffset, (int)recordCount));
            totalRecords = checked(totalRecords + recordCount);
        }

        if (totalRecords != header.RecordCount)
            throw new InvalidDataException("v5 模型记录数与区块索引不一致");
        stream.Position = dataStart;
        return entries;
    }

    /// <summary>读取 v5 材质元数据；不存在时返回空元数据</summary>
    private static MaterialExchangeMetadata ReadV5Metadata(Stream stream, V5Header header)
    {
        if (!header.HasMetadata) return new MaterialExchangeMetadata();
        return ReadRawMetadata(stream, header.MetadataOffset, header.MetadataLength);
    }

    /// <summary>
    /// 读取 v5 元数据段：写入端在 metadataOffset 处直接写编码字节，长度记录在文件头，
    /// 这里按头里的长度精确读取，不能再按旧格式去读前置长度字段
    /// </summary>
    /// <param name="stream">文件流</param>
    /// <param name="offset">元数据偏移</param>
    /// <param name="length">元数据字节数</param>
    /// <returns>解析后的材质元数据</returns>
    private static MaterialExchangeMetadata ReadRawMetadata(Stream stream, long offset, long length)
    {
        if (length < 1 || length > MaterialExchangeCodec.MaximumBytes ||
            offset < 0 || offset + length > stream.Length)
            throw new InvalidDataException("材质元数据长度无效");
        stream.Position = offset;
        var bytes = new byte[length];
        ReadFully(stream, bytes, bytes.Length);
        return MaterialExchangeCodec.Decode(bytes);
    }

    /// <summary>
    /// 单批解码的区块数量；区块载荷最大约 400KB，16 个区块的上限把
    /// 批量解码的临时内存控制在 8MB 以内，同时给线程池足够任务
    /// </summary>
    private const int DecodeBatchChunks = 16;

    /// <summary>顺序读入的区块载荷，保留原始字节供并行解码</summary>
    private sealed class ChunkPayload
    {
        internal required V5ChunkEntry Entry { get; init; }
        internal required byte[] Bytes { get; init; }
    }

    /// <summary>一个区块解码后的世界坐标与体素值，写入世界时保持索引顺序</summary>
    private readonly record struct DecodedChunk(VoxelCoord64[] Targets, VoxelValue[] Values);

    /// <summary>
    /// 异步读入一批区块载荷：调用方在解码当前批时预读下一批，
    /// 磁盘 IO 与解码在时间上重叠，IO 本身仍按区块顺序进行
    /// </summary>
    private static async System.Threading.Tasks.Task<ChunkPayload[]> ReadPayloadBatchAsync(Stream stream,
        IReadOnlyList<V5ChunkEntry> entries, int start, int count, long dataEnd,
        CancellationToken cancellationToken)
    {
        var payloads = new ChunkPayload[count];
        for (var offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[start + offset];
            stream.Position = entry.BlobOffset;
            var paletteCountBytes = new byte[V5ChunkBlobHeaderSize];
            await ReadFullyAsync(stream, paletteCountBytes, paletteCountBytes.Length, 0, cancellationToken)
                .ConfigureAwait(false);
            var paletteCount = BinaryPrimitives.ReadInt32LittleEndian(paletteCountBytes);
            if (paletteCount < 1 || paletteCount > V5MaximumPaletteEntries ||
                paletteCount > entry.RecordCount)
            {
                throw new InvalidDataException("v5 模型区块调色板数量无效");
            }

            var blobBytes = checked(V5ChunkBlobHeaderSize + (long)paletteCount * V5PaletteEntrySize
                + (long)entry.RecordCount * V5RecordSize);
            if (entry.BlobOffset + blobBytes > dataEnd || blobBytes > int.MaxValue)
                throw new InvalidDataException("v5 模型区块载荷超出文件数据区");
            var bytes = new byte[checked((int)blobBytes)];
            paletteCountBytes.CopyTo(bytes, 0);
            // count 是本次要读入的字节数，不是写入终点：载荷缓冲已含 4 字节调色板数
            await ReadFullyAsync(stream, bytes, (int)blobBytes - V5ChunkBlobHeaderSize,
                V5ChunkBlobHeaderSize, cancellationToken).ConfigureAwait(false);
            payloads[offset] = new ChunkPayload { Entry = entry, Bytes = bytes };
        }

        return payloads;
    }

    /// <summary>等待预读任务结束；正常流程中结果已被取走，异常路径只需释放读取</summary>
    private static void AwaitPendingRead(System.Threading.Tasks.Task<ChunkPayload[]>? pending)
    {
        if (pending is null) return;
        try
        {
            pending.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 预读失败不会影响已经返回的错误原因，这里只保证任务被观察并结束
        }
    }

    /// <summary>异步读满指定区间，用于把磁盘读取交给文件流的异步 IO</summary>
    private static async System.Threading.Tasks.Task ReadFullyAsync(Stream stream, byte[] buffer, int count,
        int offset, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), cancellationToken)
                .ConfigureAwait(false);
            if (got <= 0)
            {
                throw new InvalidDataException("大世界模型文件在记录结束前截断");
            }

            read += got;
        }
    }

    /// <summary>
    /// 并行解码一批区块：调色板解析、局部坐标与包围盒校验互不共享状态；
    /// 结果保持批次内顺序，由调用方在单线程写入世界
    /// </summary>
    private static DecodedChunk[] DecodePayloadBatch(ChunkPayload[] payloads, V5Header header,
        VoxelCoord64 voxelOrigin, bool rebase, CancellationToken cancellationToken)
    {
        var decoded = new DecodedChunk[payloads.Length];
        try
        {
            System.Threading.Tasks.Parallel.For(0, payloads.Length,
                new System.Threading.Tasks.ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
                },
                index => decoded[index] = DecodeChunkPayload(payloads[index], header, voxelOrigin, rebase));
        }
        catch (AggregateException exception)
        {
            var first = exception.Flatten().InnerExceptions[0];
            if (first is OperationCanceledException)
                throw new OperationCanceledException(cancellationToken);
            throw first is InvalidDataException
                ? new InvalidDataException(first.Message, first)
                : new InvalidOperationException(first.Message, first);
        }

        return decoded;
    }

    /// <summary>
    /// 解码单个区块载荷到独立数组；单模型把坐标重锚到包围盒最小角，
    /// 多对象文档保留各片段自身的绝对坐标
    /// </summary>
    private static DecodedChunk DecodeChunkPayload(ChunkPayload payload, V5Header header,
        VoxelCoord64 voxelOrigin, bool rebase)
    {
        var entry = payload.Entry;
        var bytes = payload.Bytes;
        var paletteCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (paletteCount < 1 || paletteCount > V5MaximumPaletteEntries ||
            paletteCount > entry.RecordCount)
        {
            throw new InvalidDataException("v5 模型区块调色板数量无效");
        }

        var palette = new VoxelValue[paletteCount];
        var paletteStart = V5ChunkBlobHeaderSize;
        for (var index = 0; index < paletteCount; index++)
        {
            var offset = paletteStart + index * V5PaletteEntrySize;
            var value = new VoxelValue(
                new Rgba32(bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)),
                bytes[offset + 6]);
            if (value.IsEmpty) throw new InvalidDataException("v5 模型区块包含空体素");
            palette[index] = value;
        }

        var recordStart = paletteStart + checked(paletteCount * V5PaletteEntrySize);
        var targets = new VoxelCoord64[entry.RecordCount];
        var values = new VoxelValue[entry.RecordCount];
        var baseX = checked(entry.Coordinate.X * VoxelWorld.ChunkSize);
        var baseY = checked(entry.Coordinate.Y * VoxelWorld.ChunkSize);
        var baseZ = checked(entry.Coordinate.Z * VoxelWorld.ChunkSize);
        for (var index = 0; index < entry.RecordCount; index++)
        {
            var offset = recordStart + index * V5RecordSize;
            var localX = bytes[offset];
            var localY = bytes[offset + 1];
            var localZ = bytes[offset + 2];
            if (localX >= VoxelWorld.ChunkSize || localY >= VoxelWorld.ChunkSize ||
                localZ >= VoxelWorld.ChunkSize)
            {
                throw new InvalidDataException("v5 模型区块局部坐标越界");
            }

            var paletteIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 3));
            if (paletteIndex >= paletteCount)
                throw new InvalidDataException("v5 模型区块调色板索引越界");
            var absolute = new VoxelCoord64(checked(baseX + localX), checked(baseY + localY),
                checked(baseZ + localZ));
            if (rebase)
            {
                if (absolute.X < header.Minimum.X || absolute.X > header.MaximumX ||
                    absolute.Y < header.Minimum.Y || absolute.Y > header.MaximumY ||
                    absolute.Z < header.Minimum.Z || absolute.Z > header.MaximumZ)
                {
                    throw new InvalidDataException("v5 模型区块坐标超出包围盒");
                }

                targets[index] = new VoxelCoord64(
                    checked(voxelOrigin.X + absolute.X - header.Minimum.X),
                    checked(voxelOrigin.Y + absolute.Y - header.Minimum.Y),
                    checked(voxelOrigin.Z + absolute.Z - header.Minimum.Z));
            }
            else
            {
                targets[index] = absolute;
            }

            values[index] = palette[paletteIndex];
        }

        return new DecodedChunk(targets, values);
    }

    /// <summary>把一批已解码区块顺序写入稀疏世界，保持 BulkWriter 的单线程约定</summary>
    private static void ApplyDecodedChunk(VoxelWorld.BulkWriter writer, DecodedChunk chunk)
    {
        for (var index = 0; index < chunk.Targets.Length; index++)
            writer.Set(chunk.Targets[index], chunk.Values[index]);
    }

    private static void ReportProgress(Action<int, int>? reportProgress, long processed, long total)
    {
        if (reportProgress is null) return;
        var current = (int)Math.Min(processed, int.MaxValue);
        var maximum = (int)Math.Min(total, int.MaxValue);
        reportProgress(current, maximum);
    }

    private static MaterialExchangeMetadata ReadMetadata(Stream stream)
    {
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
    /// 从流读满指定长度，短读循环补齐，提前遇到文件末尾视为损坏
    /// </summary>
    private static void ReadFully(Stream stream, byte[] buffer, int count)
        => ReadFully(stream, buffer, 0, count);

    /// <summary>从流读满指定区间，允许把连续载荷写入已有缓冲的偏移处</summary>
    private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var got = stream.Read(buffer, offset + read, count - read);
            if (got <= 0)
            {
                throw new InvalidDataException("大世界模型文件在记录结束前截断");
            }

            read += got;
        }
    }
}
