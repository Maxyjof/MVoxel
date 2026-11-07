using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 模型组件内嵌体素的读写，坐标与体素属性均完整保存。
/// 小模型逐格写成可读的 cells；大模型按 32³ 区块写成 Brotli 压缩载荷，
/// 场景保存时载荷会提升到场景级 blockStore 按内容哈希去重，
/// 读取时再展开回各组件，保证场景、预制体与撤销历史始终使用同一套内存形态。
/// </summary>
internal static class EmbeddedVoxelData
{
    internal const string PropertyName = "embeddedVoxels";

    /// <summary>
    /// 当前内嵌数据格式版本；小对象写可读 cells，大对象写压缩 blocks
    /// </summary>
    private const int FormatVersion = 2;

    /// <summary>
    /// 区块边长，必须与运行时 VoxelWorld.ChunkSize 一致
    /// </summary>
    private const int BlockSize = 32;

    /// <summary>
    /// 单块载荷格式版本
    /// </summary>
    private const ushort BlockPayloadVersion = 1;

    /// <summary>
    /// 低于该体素数时继续使用可读 cells，超过后改用压缩区块
    /// </summary>
    internal const int BlockEncodingThreshold = 4096;

    private const string EncodingCells = "cells";
    private const string EncodingBlocks = "blocks";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly record struct Cell(long X, long Y, long Z, byte R, byte G, byte B, byte A, ushort MaterialSlot, byte Flags);

    private sealed record BlockRef(long X, long Y, long Z, string Hash);

    private sealed record Payload(
        int Version,
        string? Encoding,
        int? BlockSize,
        Cell[]? Cells,
        Dictionary<string, string>? BlockStore,
        BlockRef[]? Blocks,
        int? Scale,
        Payload[]? Lod);

    internal static bool HasData(ComponentRecord component) =>
        component.Data.ValueKind == JsonValueKind.Object &&
        component.Data.TryGetProperty(PropertyName, out var data) && data.ValueKind == JsonValueKind.Object;

    internal static VoxelWorld Read(ComponentRecord component) => ReadWithLod(component).World;

    /// <summary>
    /// 读取内嵌体素及其预烘焙 LOD 链；没有预烘焙数据的组件返回空链
    /// </summary>
    /// <param name="component">模型组件</param>
    /// <returns>体素世界与预烘焙链</returns>
    internal static (VoxelWorld World, ModelLodChain Lod) ReadWithLod(ComponentRecord component)
    {
        if (!SceneComponentTypes.IsVoxelModel(component) || !HasData(component))
            throw new InvalidDataException("模型组件不包含内嵌体素数据");
        var data = component.Data.GetProperty(PropertyName);
        var payload = data.Deserialize<Payload>(Options) ?? throw new InvalidDataException("内嵌体素数据为空");
        if (payload.Version != FormatVersion)
            throw new InvalidDataException("内嵌体素数据版本不受支持，请在当前版本重新保存场景");
        var world = ReadPayloadBody(payload);
        return (world, ReadLod(payload.Lod));
    }

    private static VoxelWorld ReadPayloadBody(Payload payload)
    {
        if (payload.Encoding is EncodingBlocks)
            return ReadBlocks(payload);
        if (payload.Encoding is EncodingCells)
            return ReadCells(payload);
        throw new InvalidDataException($"不支持的内嵌体素编码：{payload.Encoding}");
    }

    /// <summary>
    /// 由载荷里的逐级代表体素重建预烘焙链；级号由数组顺序决定
    /// </summary>
    private static ModelLodChain ReadLod(Payload[]? payloads)
    {
        if (payloads is null || payloads.Length == 0) return ModelLodChain.Empty;
        var levels = new List<VoxelWorld?> { null };
        foreach (var payload in payloads)
        {
            if (payload.Version != FormatVersion)
                throw new InvalidDataException("预烘焙 LOD 数据版本不受支持，请在当前版本重新保存场景");
            var level = payload.Scale is > 1
                ? Math.Max(1, (int)MathF.Round(MathF.Log2(payload.Scale.Value)))
                : payload.Scale ?? 0;
            while (levels.Count <= level) levels.Add(null);
            levels[level] = ReadPayloadBody(payload);
        }

        return ModelLodChain.FromLevels(levels);
    }

    private static VoxelWorld ReadCells(Payload payload)
    {
        if (payload.Cells is null) throw new InvalidDataException("内嵌体素数据缺少 cells");
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var cell in payload.Cells)
        {
            var coordinate = new VoxelCoord64(cell.X, cell.Y, cell.Z);
            var value = new VoxelValue(new Rgba32(cell.R, cell.G, cell.B, cell.A), cell.MaterialSlot, cell.Flags);
            if (value.IsEmpty || world.TryGet(coordinate, out _))
                throw new InvalidDataException("内嵌体素包含空数据或重复坐标");
            writer.Set(coordinate, value);
        }
        return world;
    }

    private static VoxelWorld ReadBlocks(Payload payload)
    {
        if (payload.BlockSize != BlockSize)
            throw new InvalidDataException($"内嵌体素区块边长不受支持：{payload.BlockSize}");
        if (payload.BlockStore is null || payload.BlockStore.Count == 0)
            throw new InvalidDataException("内嵌体素缺少区块载荷");
        if (payload.Blocks is null) throw new InvalidDataException("内嵌体素缺少区块索引");
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var block in payload.Blocks)
        {
            if (!payload.BlockStore.TryGetValue(block.Hash, out var encoded))
                throw new InvalidDataException($"内嵌体素区块载荷不存在：{block.Hash}");
            var bytes = Convert.FromBase64String(encoded);
            VoxelBlockPayload.Decode(bytes, new ChunkCoord64(block.X, block.Y, block.Z), world, writer, BlockSize);
        }
        return world;
    }

    internal static void Remove(ComponentRecord component)
    {
        if (!HasData(component)) return;
        var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        json.Remove(PropertyName);
        component.Data = JsonSerializer.SerializeToElement(json, Options);
    }

    /// <summary>
    /// 把体素世界写回模型组件；大模型按区块增量编码，只重新压缩修订号变化的区块，
    /// 其余区块直接复用上次写入的压缩载荷。
    /// </summary>
    /// <param name="component">模型组件</param>
    /// <param name="world">对象体素世界</param>
    /// <param name="baked">预烘焙 LOD 链；null 表示不写入且清除已有的预烘焙数据</param>
    /// <returns>本次重新编码的区块坐标，供自动保存记录差异日志</returns>
    internal static IReadOnlyList<ChunkCoord64> Write(ComponentRecord component, VoxelWorld world,
        ModelLodChain? baked = null)
    {
        if (!SceneComponentTypes.IsVoxelModel(component))
            throw new ArgumentException("组件不是体素模型", nameof(component));
        ArgumentNullException.ThrowIfNull(world);
        var count = world.OccupiedVoxelCount;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        SceneComponentTypes.WriteVoxelModel(component, data with { Model = string.Empty, VoxelCount = count });
        if (count < BlockEncodingThreshold)
        {
            // 逐格编码没有区块概念，切换后不再保留区块载荷缓存
            WriteCaches.Remove(world);
            var cellsPayload = WriteCells(world) with { Lod = WriteLodPayloads(baked) };
            var cellsJson = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
            cellsJson[PropertyName] = JsonSerializer.SerializeToNode(cellsPayload, Options);
            component.Data = JsonSerializer.SerializeToElement(cellsJson, Options);
            return Array.Empty<ChunkCoord64>();
        }

        var cache = WriteCaches.GetOrCreateValue(world);
        var dirty = new List<ChunkCoord64>();
        var result = WriteBlocksIncremental(world, cache, dirty);
        var existing = TryReadPayload(component);
        var wantedScales = DesiredLodScales(baked);
        // 内容未变时复用已有预烘焙载荷，只有级数或方块边长变化才重新压缩
        var lod = LodMatches(existing?.Lod, wantedScales) ? existing?.Lod : WriteLodPayloads(baked);
        // 没有任何区块变化且预烘焙设置一致时组件数据保持原样，避免每次自动保存都重写整段 JSON
        if (result is null)
        {
            if (existing is null || LodMatches(existing.Lod, wantedScales)) return dirty;
            var unchangedJson = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
            unchangedJson[PropertyName] = JsonSerializer.SerializeToNode(existing with { Lod = lod }, Options);
            component.Data = JsonSerializer.SerializeToElement(unchangedJson, Options);
            return dirty;
        }

        var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        json[PropertyName] = JsonSerializer.SerializeToNode(result.Payload with { Lod = lod }, Options);
        component.Data = JsonSerializer.SerializeToElement(json, Options);
        // 数据成功写入组件后才提交缓存，避免组件与缓存指向不同版本
        cache.Blocks = true;
        cache.OccupiedCount = count;
        cache.Refs.Clear();
        foreach (var entry in result.Entries) cache.Refs[entry.Coordinate] = (entry.Revision, entry.Hash);
        cache.Store = result.Store;
        return dirty;
    }

    /// <summary>
    /// 读取组件当前的内嵌载荷，用于判断预烘焙数据是否需要重写
    /// </summary>
    private static Payload? TryReadPayload(ComponentRecord component)
    {
        if (!HasData(component)) return null;
        try
        {
            return component.Data.GetProperty(PropertyName).Deserialize<Payload>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 期望写入的各级方块边长；没有预烘焙链时为空
    /// </summary>
    private static int[] DesiredLodScales(ModelLodChain? baked)
    {
        if (baked is null) return [];
        var scales = new List<int>();
        for (var level = 1; level < baked.LevelCount; level++)
            if (baked.HasBakedLevel(level)) scales.Add(ModelLodChain.BlockSizeFor(level));
        return scales.ToArray();
    }

    private static bool LodMatches(Payload[]? existing, int[] wanted)
    {
        if (wanted.Length == 0) return existing is null || existing.Length == 0;
        if (existing is null || existing.Length != wanted.Length) return false;
        for (var index = 0; index < wanted.Length; index++)
            if (existing[index].Scale != wanted[index]) return false;
        return true;
    }

    /// <summary>
    /// 把预烘焙各级代表体素编码成载荷数组；小级写可读 cells，大级写压缩区块
    /// </summary>
    private static Payload[]? WriteLodPayloads(ModelLodChain? baked)
    {
        if (baked is null) return null;
        var payloads = new List<Payload>();
        for (var level = 1; level < baked.LevelCount; level++)
        {
            if (baked.LevelWorld(level) is not { } world || world.OccupiedVoxelCount == 0) continue;
            var payload = world.OccupiedVoxelCount < BlockEncodingThreshold
                ? WriteCells(world)
                : WriteBlocks(world);
            payloads.Add(payload with { Scale = ModelLodChain.BlockSizeFor(level) });
        }

        return payloads.Count == 0 ? null : payloads.ToArray();
    }

    /// <summary>单对象内嵌编码的增量状态</summary>
    private sealed class WriteCache
    {
        public bool Blocks;
        public long OccupiedCount;
        public readonly Dictionary<ChunkCoord64, (long Revision, string Hash)> Refs = new();
        public Dictionary<string, string> Store = new(StringComparer.Ordinal);
    }

    private sealed record BlocksResult(
        Payload Payload,
        List<(ChunkCoord64 Coordinate, long Revision, string Hash)> Entries,
        Dictionary<string, string> Store);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VoxelWorld, WriteCache>
        WriteCaches = new();

    /// <summary>
    /// 增量生成区块载荷：修订号未变的区块复用上次的压缩载荷，
    /// 只有变化区块重新生成载荷、重新压缩并计入脏区块列表。
    /// 全部区块都未变化且体素数一致时返回 null，调用方跳过组件数据重写。
    /// </summary>
    private static BlocksResult? WriteBlocksIncremental(VoxelWorld world, WriteCache cache,
        List<ChunkCoord64> dirty)
    {
        var store = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<(ChunkCoord64 Coordinate, long Revision, string Hash)>();
        var refs = new List<BlockRef>();
        var reencoded = false;
        foreach (var (coordinate, chunk) in world.SnapshotChunks())
        {
            string? hash = null;
            if (cache.Blocks && cache.Refs.TryGetValue(coordinate, out var previous) &&
                previous.Revision == chunk.Revision && cache.Store.TryGetValue(previous.Hash, out var encoded))
            {
                hash = previous.Hash;
                store.TryAdd(hash, encoded);
            }

            if (hash is null)
            {
                var payload = BuildBlockPayload(chunk);
                hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
                if (!store.ContainsKey(hash))
                {
                    using var output = new MemoryStream();
                    using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                        compressor.Write(payload, 0, payload.Length);
                    store[hash] = Convert.ToBase64String(output.ToArray());
                }

                dirty.Add(coordinate);
                reencoded = true;
            }

            entries.Add((coordinate, chunk.Revision, hash));
            refs.Add(new BlockRef(coordinate.X, coordinate.Y, coordinate.Z, hash));
        }

        refs.Sort(static (left, right) =>
        {
            var result = left.X.CompareTo(right.X);
            if (result != 0) return result;
            result = left.Y.CompareTo(right.Y);
            return result != 0 ? result : left.Z.CompareTo(right.Z);
        });
        if (!reencoded && cache.Blocks && cache.OccupiedCount == world.OccupiedVoxelCount &&
            cache.Refs.Count == entries.Count)
        {
            return null;
        }

        return new BlocksResult(new Payload(FormatVersion, EncodingBlocks, BlockSize, null, store,
            refs.ToArray(), null, null), entries, store);
    }

    private static Payload WriteCells(VoxelWorld world)
    {
        var cells = world.EnumerateOccupied().Select(item => new Cell(
            item.Coordinate.X, item.Coordinate.Y, item.Coordinate.Z,
            item.Value.Color.R, item.Value.Color.G, item.Value.Color.B, item.Value.Color.A,
            item.Value.MaterialSlot, item.Value.Flags)).ToArray();
        return new Payload(FormatVersion, EncodingCells, null, cells, null, null, null, null);
    }

    private static Payload WriteBlocks(VoxelWorld world)
    {
        var store = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = new List<BlockRef>();
        foreach (var (coordinate, chunk) in world.SnapshotChunks())
        {
            var payload = BuildBlockPayload(chunk);
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (!store.ContainsKey(hash))
            {
                using var output = new MemoryStream();
                using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    compressor.Write(payload, 0, payload.Length);
                store[hash] = Convert.ToBase64String(output.ToArray());
            }
            refs.Add(new BlockRef(coordinate.X, coordinate.Y, coordinate.Z, hash));
        }
        refs.Sort(static (left, right) =>
        {
            var result = left.X.CompareTo(right.X);
            if (result != 0) return result;
            result = left.Y.CompareTo(right.Y);
            return result != 0 ? result : left.Z.CompareTo(right.Z);
        });
        return new Payload(FormatVersion, EncodingBlocks, BlockSize, null, store, refs.ToArray(), null, null);
    }

    /// <summary>
    /// 生成确定性的区块载荷：调色板按体素值排序，体素按区块内线性索引排序，
    /// 相同内容必然得到相同字节，从而支持内容哈希去重。
    /// 布局：版本 u16、调色板数 u16、调色板（RGBA 各 1 字节 + 材质槽 u16 + 标志 1 字节）、
    /// 体素数 u16、逐体素（局部 X/Y/Z 各 u16 + 调色板索引 u16）。
    /// </summary>
    private static byte[] BuildBlockPayload(SparseVoxelChunk chunk) => VoxelBlockPayload.BuildRaw(chunk);

    /// <summary>
    /// 保存前把各组件自带的区块载荷提升到场景级 blockStore，跨对象按哈希共享；
    /// 返回原始组件数据，写入结束后由调用方恢复，内存形态保持不变。
    /// </summary>
    internal static List<(ComponentRecord Component, JsonElement Data)> HoistBlocks(SceneDocument scene)
    {
        var originals = new List<(ComponentRecord, JsonElement)>();
        // 上一次保存若中途失败，保留的载荷不应混入本次结果
        scene.EmbeddedBlockStore.Clear();
        foreach (var component in scene.Objects.SelectMany(item => item.Components))
        {
            if (!HasData(component)) continue;
            var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
            if (json[PropertyName] is not JsonObject embedded) continue;
            var changed = HoistPayloadStore(embedded, scene);
            // 预烘焙各级同样按内容哈希提升到场景级存储，跨对象与完整细节共享相同区块
            if (embedded["lod"] is JsonArray lod)
            {
                foreach (var node in lod)
                    if (node is JsonObject level) changed |= HoistPayloadStore(level, scene);
            }

            if (!changed) continue;
            originals.Add((component, component.Data));
            component.Data = JsonSerializer.SerializeToElement(json, Options);
        }
        return originals;
    }

    private static bool HoistPayloadStore(JsonObject payload, SceneDocument scene)
    {
        if (payload["blockStore"] is not JsonObject store || store.Count == 0) return false;
        foreach (var pair in store.ToArray())
        {
            if (pair.Value is null) continue;
            scene.EmbeddedBlockStore.TryAdd(pair.Key, pair.Value.GetValue<string>());
        }
        payload.Remove("blockStore");
        return true;
    }

    /// <summary>
    /// 恢复保存前被提升的组件数据
    /// </summary>
    internal static void RestoreBlocks(IReadOnlyList<(ComponentRecord Component, JsonElement Data)> originals)
    {
        foreach (var (component, data) in originals) component.Data = data;
    }

    /// <summary>
    /// 读取场景后把场景级 blockStore 展开回每个组件的区块载荷，
    /// 之后所有读取路径都可只依赖组件自身数据
    /// </summary>
    internal static void ExpandBlocks(SceneDocument scene)
    {
        var store = scene.EmbeddedBlockStore;
        if (store.Count == 0) return;
        foreach (var component in scene.Objects.SelectMany(item => item.Components))
        {
            if (!HasData(component)) continue;
            var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
            if (json[PropertyName] is not JsonObject embedded) continue;
            var changed = ExpandPayloadStore(embedded, store);
            if (embedded["lod"] is JsonArray lod)
            {
                foreach (var node in lod)
                    if (node is JsonObject level) changed |= ExpandPayloadStore(level, store);
            }

            if (!changed) continue;
            component.Data = JsonSerializer.SerializeToElement(json, Options);
        }
        store.Clear();
    }

    private static bool ExpandPayloadStore(JsonObject payload, IReadOnlyDictionary<string, string> store)
    {
        if (payload["blocks"] is not JsonArray blocks) return false;
        var local = new JsonObject();
        foreach (var node in blocks)
        {
            var hash = node?["hash"]?.GetValue<string>();
            if (string.IsNullOrEmpty(hash)) continue;
            if (!store.TryGetValue(hash, out var encoded))
                throw new InvalidDataException($"场景缺少内嵌体素区块载荷：{hash}");
            local[hash] = encoded;
        }
        payload["blockStore"] = local;
        return true;
    }
}
