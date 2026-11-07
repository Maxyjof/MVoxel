using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace MVoxel.Voxels;

/// <summary>
/// 预烘焙 LOD 的一个图元：世界坐标上的同值方块，边长由 <see cref="Extent"/> 给出
/// </summary>
public readonly record struct VoxelBakedCell(long X, long Y, long Z, VoxelValue Value, int Extent);

/// <summary>
/// 单个模型体素数据的 LOD 链。每级把 2^k 网格内的体素折叠成一个代表体素，
/// 坐标与源数据保持同一世界坐标系，渲染时按 <see cref="VoxelBakedCell.Extent"/> 展开成方块。
/// 第 0 级为完整细节，不保存；预烘焙时各级代表体素写回源文件。
/// </summary>
public sealed class ModelLodChain
{
    /// <summary>空链，只含完整细节</summary>
    public static ModelLodChain Empty { get; } = new(Array.Empty<VoxelWorld>(), 0);

    private readonly VoxelWorld[] _levels;

    private ModelLodChain(VoxelWorld[] levels, int levelCount)
    {
        _levels = levels;
        LevelCount = levelCount;
    }

    /// <summary>
    /// 链包含的级数（含第 0 级完整细节），至少为 1
    /// </summary>
    public int LevelCount { get; }

    /// <summary>
    /// 该级是否保存了预烘焙数据；第 0 级恒为 false
    /// </summary>
    /// <param name="level">LOD 级</param>
    public bool HasBakedLevel(int level) => level > 0 && level < _levels.Length && _levels[level] is not null;

    /// <summary>
    /// 取该级的代表体素世界，供写盘复用；没有预烘焙数据时返回 null
    /// </summary>
    /// <param name="level">LOD 级</param>
    public VoxelWorld? LevelWorld(int level) => level > 0 && level < _levels.Length ? _levels[level] : null;

    /// <summary>
    /// 取某个区块在指定级的预烘焙图元；没有预烘焙数据时返回 null，
    /// 调用方回退到运行时按同样规则粗化
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    /// <param name="level">LOD 级</param>
    /// <returns>世界坐标的方块图元，或 null</returns>
    public IReadOnlyList<VoxelBakedCell>? Cells(ChunkCoord64 coordinate, int level)
    {
        if (!HasBakedLevel(level)) return null;
        var world = _levels[level];
        if (!world.TryGetChunk(coordinate, out var chunk) || chunk.OccupiedCount == 0) return null;
        var blockSize = BlockSizeFor(level);
        var result = new VoxelBakedCell[chunk.OccupiedCount];
        var write = 0;
        foreach (var (index, value) in chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            result[write++] = new VoxelBakedCell(
                coordinate.X * VoxelWorld.ChunkSize + local.X,
                coordinate.Y * VoxelWorld.ChunkSize + local.Y,
                coordinate.Z * VoxelWorld.ChunkSize + local.Z,
                value, blockSize);
        }

        return result;
    }

    /// <summary>
    /// 该级的方块边长；与运行时粗化一致，最大不超过一个区块
    /// </summary>
    /// <param name="level">LOD 级</param>
    public static int BlockSizeFor(int level) => Math.Min(1 << level, VoxelWorld.ChunkSize);

    /// <summary>
    /// 由已读出的各级代表体素世界构造链；索引 0 为占位，可传 null
    /// </summary>
    /// <param name="levelsByIndex">按级存放的代表体素世界</param>
    /// <returns>预烘焙 LOD 链</returns>
    public static ModelLodChain FromLevels(IReadOnlyList<VoxelWorld?> levelsByIndex)
    {
        ArgumentNullException.ThrowIfNull(levelsByIndex);
        if (levelsByIndex.Count == 0) return Empty;
        var levels = new VoxelWorld[levelsByIndex.Count];
        for (var index = 1; index < levelsByIndex.Count; index++)
        {
            if (levelsByIndex[index] is { } world) levels[index] = world;
        }

        return new ModelLodChain(levels, levelsByIndex.Count);
    }

    /// <summary>
    /// 从源体素世界生成预烘焙 LOD 各级。规则与运行时 <c>CoarsenChunk</c> 完全一致：
    /// 区块内按 2^k 网格分桶，每个桶取稳定序首个体素作为代表，坐标落在方块最小角。
    /// </summary>
    /// <param name="source">源体素世界</param>
    /// <param name="levelCount">总级数（含第 0 级）</param>
    /// <param name="cancellation">取消令牌</param>
    /// <returns>预烘焙链；级数不足 2 时返回空链</returns>
    public static ModelLodChain Generate(VoxelWorld source, int levelCount, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        levelCount = Math.Clamp(levelCount, 1, MVoxel.Rendering.VoxelLodSettings.MaximumLevels);
        if (levelCount <= 1 || source.OccupiedVoxelCount == 0) return Empty;
        var levels = new VoxelWorld?[levelCount];
        for (var level = 1; level < levelCount; level++)
        {
            cancellation.ThrowIfCancellationRequested();
            levels[level] = GenerateLevel(source, level, cancellation);
        }

        return FromLevels(levels);
    }

    /// <summary>
    /// 把整条链编码成 Brotli 压缩的 Base64 文本，供独立模型资产的元数据内嵌保存。
    /// 布局：容器版本 u16、级数 u16、逐级（方块边长 u16、区块数 i32、
    /// 区块坐标 i64×3、载荷长度 i32、区块载荷）。
    /// </summary>
    public static string Encode(ModelLodChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)1);
            writer.Write((ushort)chain.LevelCount);
            for (var level = 1; level < chain.LevelCount; level++)
            {
                writer.Write((ushort)BlockSizeFor(level));
                var world = chain.LevelWorld(level);
                var chunks = world is null ? new List<(ChunkCoord64, SparseVoxelChunk)>() : world.SnapshotChunks();
                writer.Write(chunks.Count);
                foreach (var (coordinate, chunk) in chunks)
                {
                    writer.Write(coordinate.X);
                    writer.Write(coordinate.Y);
                    writer.Write(coordinate.Z);
                    var bytes = VoxelBlockPayload.BuildCompressed(chunk);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
            }
        }

        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            buffer.WriteTo(compressor);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// 解码 <see cref="Encode"/> 写出的文本；空或格式不符时返回 null
    /// </summary>
    public static ModelLodChain? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var compressed = Convert.FromBase64String(encoded);
            using var stream = new MemoryStream(compressed, writable: false);
            using var decoder = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new BinaryReader(decoder, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt16() != 1) return null;
            var levelCount = reader.ReadUInt16();
            if (levelCount is < 1 or > MVoxel.Rendering.VoxelLodSettings.MaximumLevels) return null;
            var levels = new List<VoxelWorld?> { null };
            for (var level = 1; level < levelCount; level++)
            {
                var blockSize = reader.ReadUInt16();
                var count = reader.ReadInt32();
                if (count < 0) return null;
                var world = new VoxelWorld();
                var writer = world.CreateBulkWriter();
                for (var index = 0; index < count; index++)
                {
                    var coordinate = new ChunkCoord64(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                    var length = reader.ReadInt32();
                    if (length < 0) return null;
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length) return null;
                    VoxelBlockPayload.Decode(bytes, coordinate, world, writer, VoxelWorld.ChunkSize);
                }

                while (levels.Count <= level) levels.Add(null);
                levels[level] = world;
            }

            return FromLevels(levels);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or IOException or OverflowException)
        {
            return null;
        }
    }

    private static VoxelWorld GenerateLevel(VoxelWorld source, int level, CancellationToken cancellation)
    {
        var blockSize = BlockSizeFor(level);
        var perChunk = source.SnapshotChunks();
        var outputs = new ConcurrentDictionary<ChunkCoord64, SparseVoxelChunk>();
        System.Threading.Tasks.Parallel.For(0, perChunk.Count, new System.Threading.Tasks.ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
        }, chunkIndex =>
        {
            var (coordinate, chunk) = perChunk[chunkIndex];
            if (chunk.OccupiedCount == 0) return;
            var target = new SparseVoxelChunk();
            var seen = new HashSet<int>();
            foreach (var (index, value) in chunk.Snapshot)
            {
                var local = SparseVoxelChunk.FromIndex(index);
                var localX = local.X / blockSize * blockSize;
                var localY = local.Y / blockSize * blockSize;
                var localZ = local.Z / blockSize * blockSize;
                var blockKey = SparseVoxelChunk.ToIndex(new LocalCoord((byte)localX, (byte)localY, (byte)localZ));
                if (!seen.Add(blockKey)) continue;
                target.SetIndex(blockKey, value);
            }

            outputs[coordinate] = target;
        });

        var result = new VoxelWorld();
        foreach (var (coordinate, chunk) in outputs) result.AdoptChunk(coordinate, chunk);
        return result;
    }
}

/// <summary>
/// 按模型世界缓存的预烘焙 LOD 链，避免每次同步重建
/// </summary>
internal static class ModelLodChainCache
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VoxelWorld, CacheEntry> Cache = new();

    private sealed record CacheEntry(int LevelCount, int ContentVersion, ModelLodChain Chain);

    internal static ModelLodChain Get(VoxelWorld world, int levelCount)
    {
        if (world is null || levelCount <= 1) return ModelLodChain.Empty;
        var existing = Cache.GetOrAdd(world, _ => new CacheEntry(0, -1, ModelLodChain.Empty));
        if (existing.LevelCount == levelCount && existing.ContentVersion == world.ContentVersion) return existing.Chain;
        var chain = ModelLodChain.Generate(world, levelCount);
        Cache.Remove(world);
        Cache.Add(world, new CacheEntry(levelCount, world.ContentVersion, chain));
        return chain;
    }
}
