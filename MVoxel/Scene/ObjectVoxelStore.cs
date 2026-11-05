using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 每个对象独立保存体素，合成时只在同一坐标选择优先级最高的体素
/// </summary>
internal sealed class ObjectVoxelStore
{
    /// <summary>
    /// 低于该体素数的对象直接串行合成，避免为小对象付并行调度成本
    /// </summary>
    private const int ParallelComposeThreshold = 64 * 1024;

    private readonly Dictionary<StableId, VoxelWorld> _objects = new();

    /// <summary>
    /// 将非重叠合并世界按对象归属拆分为独立世界
    /// </summary>
    internal static ObjectVoxelStore FromMerged(VoxelWorld merged, StableId primaryId,
        IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> ownership,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(ownership);
        if (primaryId == StableId.Empty) throw new ArgumentException("主模型标识不能为空", nameof(primaryId));
        var version = merged.ContentVersion;
        var result = new ObjectVoxelStore();
        var assigned = new HashSet<VoxelCoord64>();
        foreach (var (objectId, coordinates) in ownership)
        {
            cancellation.ThrowIfCancellationRequested();
            if (objectId == primaryId) throw new InvalidDataException("主模型不能出现在分层归属表中");
            var writer = result.GetOrCreate(objectId).CreateBulkWriter();
            var processed = 0;
            foreach (var coordinate in coordinates)
            {
                if ((processed++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (!assigned.Add(coordinate)) throw new InvalidDataException("合并世界存在重复的体素归属");
                if (!merged.TryGet(coordinate, out var value)) throw new InvalidDataException("体素归属指向空坐标");
                writer.Set(coordinate, value);
            }
        }
        var primaryWriter = result.GetOrCreate(primaryId).CreateBulkWriter();
        var count = 0;
        foreach (var (coordinate, value) in merged.EnumerateOccupied())
        {
            if ((count++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (!assigned.Contains(coordinate)) primaryWriter.Set(coordinate, value);
        }
        if (merged.ContentVersion != version)
            throw new InvalidOperationException("拆分期间体素世界发生变化，请重新尝试");
        return result;
    }

    internal VoxelWorld GetOrCreate(StableId id)
    {
        if (!_objects.TryGetValue(id, out var world))
            _objects.Add(id, world = new VoxelWorld());
        return world;
    }

    /// <summary>
    /// 复制源体素到对象，确保对象之间及源世界之间相互独立
    /// </summary>
    internal void SetWorld(StableId id, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _objects[id] = world.Clone();
    }

    /// <summary>
    /// 直接接管一份刚刚构建好的体素世界，不做复制
    /// </summary>
    /// <remarks>
    /// 只用于调用方已经拥有独享所有权的新世界，例如曲线渲染器每次重新生成的体素
    /// </remarks>
    /// <param name="id">对象标识</param>
    /// <param name="world">新的体素世界</param>
    internal void AssignWorld(StableId id, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _objects[id] = world;
    }

    internal bool TryGetWorld(StableId id, out VoxelWorld world) => _objects.TryGetValue(id, out world!);

    /// <summary>全部对象体素总数，供界面统计显示，避免每帧复制引用表</summary>
    internal int TotalOccupiedVoxelCount
    {
        get
        {
            var total = 0;
            foreach (var world in _objects.Values) total += world.OccupiedVoxelCount;
            return total;
        }
    }

    internal bool Remove(StableId id) => _objects.Remove(id);

    internal void Clear() => _objects.Clear();

    internal Dictionary<StableId, VoxelWorld> CaptureReferences() => new(_objects);

    internal void RestoreReferences(Dictionary<StableId, VoxelWorld> worlds)
    {
        _objects.Clear();
        foreach (var (id, world) in worlds) _objects.Add(id, world);
    }

    /// <summary>
    /// 对单个对象创建可撤销的体素编辑命令
    /// </summary>
    internal VoxelStrokeCommand CreateStroke(StableId id, string name,
        IEnumerable<VoxelCoord64> cells, VoxelValue value, Action<bool>? onApplied = null) =>
        new(name, GetOrCreate(id), cells, value, onApplied);

    /// <summary>
    /// 对单个对象生成可撤销的体素坐标变换，其他对象的数据不会被修改
    /// </summary>
    internal VoxelStrokeCommand CreateTransform(StableId id, string name,
        Func<VoxelCoord64, VoxelCoord64> transform, Action<bool>? onApplied = null)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var world = GetOrCreate(id);
        var original = world.EnumerateOccupied().ToArray();
        var changes = new Dictionary<VoxelCoord64, VoxelValue>();
        foreach (var (coordinate, _) in original) changes[coordinate] = default;
        var destinations = new HashSet<VoxelCoord64>();
        foreach (var (coordinate, value) in original)
        {
            var destination = transform(coordinate);
            if (!destinations.Add(destination))
                throw new InvalidOperationException("对象变换产生重复体素坐标");
            changes[destination] = value;
        }
        return new VoxelStrokeCommand(name, world,
            changes.Select(item => (item.Key, item.Value)), onApplied);
    }

    /// <summary>
    /// 根据层级树与对象组件生成可见模型的合成次序
    /// </summary>
    private static List<ObjectVoxelRenderEntry> GetVisibleEntries(SceneDocument scene, uint cameraMask, bool useSceneVisibility)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var byParent = scene.Objects.Where(item => item.ParentId is not null)
            .ToLookup(item => item.ParentId!.Value);
        var visited = new HashSet<StableId>();
        var entries = new List<ObjectVoxelRenderEntry>();
        void Visit(SceneObject item, bool ancestorsVisible)
        {
            if (!visited.Add(item.Id)) return;
            var visible = ancestorsVisible && item.Visible;
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            var generated = component is null
                ? item.Components.FirstOrDefault(item => item.Enabled && SceneComponentTypes.IsGeneratedVoxelComponent(item))
                : null;
            if (visible && (component is { Enabled: true } || generated is not null) &&
                RenderWorldComposer.LayerAllowed(cameraMask, item.LayerIndex) &&
                (!useSceneVisibility || scene.IsLayerVisible(item.LayerIndex)))
            {
                string sortingLayerName;
                int sortingOrder;
                if (component is not null)
                {
                    var data = SceneComponentTypes.ReadVoxelModel(component);
                    sortingLayerName = data.SortingLayer;
                    sortingOrder = data.SortingOrder;
                }
                else if (SceneComponentTypes.IsTreeGenerator(generated))
                {
                    // 树生成器的体素由组件隐藏生成，排序参数与模型组件同名
                    SceneComponentTypes.ReadTreeSorting(generated!, out sortingLayerName, out sortingOrder);
                }
                else
                {
                    SceneComponentTypes.ReadCurveSorting(generated!, out sortingLayerName, out sortingOrder);
                }

                var layer = scene.SortingLayerNames.FindIndex(name =>
                    string.Equals(name, sortingLayerName, StringComparison.Ordinal));
                if (layer < 0) layer = scene.SortingLayerNames.IndexOf("默认");
                entries.Add(new ObjectVoxelRenderEntry(item.Id, Math.Max(0, layer), sortingOrder));
            }
            foreach (var child in byParent[item.Id]) Visit(child, visible);
        }
        foreach (var root in scene.Objects.Where(item => item.ParentId is null)) Visit(root, true);
        return entries;
    }

    /// <summary>
    /// 按对象可见性和排序配置合成世界
    /// </summary>
    internal VoxelWorld Compose(SceneDocument scene, uint cameraMask, bool useSceneVisibility,
        CancellationToken cancellation = default, Func<StableId, VoxelValue, VoxelValue>? remap = null) =>
        Compose(GetVisibleEntries(scene, cameraMask, useSceneVisibility), cancellation, remap);

    /// <summary>
    /// 构建渲染表面接收图层快照。排序与 Compose 一致，重叠位置以后绘制对象为准；
    /// 图层只存在于此临时映射中，不写入体素值或原生资产。
    /// </summary>
    internal Dictionary<VoxelCoord64, byte> CaptureReceiverLayers(SceneDocument scene, uint cameraMask,
        bool useSceneVisibility, CancellationToken cancellation = default)
    {
        var layers = scene.Objects.ToDictionary(item => item.Id, item => item.LayerIndex);
        var result = new Dictionary<VoxelCoord64, byte>();
        var entries = GetVisibleEntries(scene, cameraMask, useSceneVisibility);
        var sources = new Dictionary<StableId, (VoxelWorld World, int Version)>();
        foreach (var entry in entries)
            if (_objects.TryGetValue(entry.ObjectId, out var source))
                sources.TryAdd(entry.ObjectId, (source, source.ContentVersion));
        foreach (var entry in entries.Select((value, index) => (value, index))
                     .OrderBy(item => item.value.SortingLayer)
                     .ThenBy(item => item.value.SortingOrder)
                     .ThenBy(item => item.index))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!layers.TryGetValue(entry.value.ObjectId, out var layer) || layer is < 0 or >= 32 ||
                !sources.TryGetValue(entry.value.ObjectId, out var source)) continue;
            var processed = 0;
            foreach (var (coordinate, _) in source.World.EnumerateOccupied())
            {
                if ((processed++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                result[coordinate] = (byte)layer;
            }
        }
        foreach (var (id, source) in sources)
            if (!_objects.TryGetValue(id, out var current) || !ReferenceEquals(current, source.World) ||
                current.ContentVersion != source.Version)
                throw new InvalidOperationException("接收图层快照构建期间体素数据发生变化，请重新构建");
        return result;
    }

    /// <summary>
    /// 查询指定位置最终可见的对象和体素，与合成顺序保持一致
    /// </summary>
    internal bool TryGetVisibleVoxel(SceneDocument scene, uint cameraMask, bool useSceneVisibility,
        VoxelCoord64 coordinate, out StableId owner, out VoxelValue value)
    {
        var entries = GetVisibleEntries(scene, cameraMask, useSceneVisibility);
        foreach (var entry in entries.Select((item, index) => (item, index))
                     .OrderByDescending(item => item.item.SortingLayer)
                     .ThenByDescending(item => item.item.SortingOrder)
                     .ThenByDescending(item => item.index))
        {
            if (!_objects.TryGetValue(entry.item.ObjectId, out var world) ||
                !world.TryGet(coordinate, out value)) continue;
            owner = entry.item.ObjectId;
            return true;
        }
        owner = StableId.Empty;
        value = default;
        return false;
    }

    /// <summary>
    /// 按排序图层、排序顺序和调用方提供的层级顺序覆盖重叠坐标
    /// </summary>
    /// <remarks>
    /// 对象之间必须严格按优先级顺序写入，否则覆盖结果会变；但同一个对象内部的写入互不冲突，
    /// 而对象的世界本来就是按区块组织的，因此拆成“对象串行、区块并行”。
    /// 大型场景里对象常带几十万体素而重叠严重，这条并行把一个对象的全部区块同时铺开，
    /// 结果与逐体素串行写入逐格相同
    /// </remarks>
    internal VoxelWorld Compose(IEnumerable<ObjectVoxelRenderEntry> entries, CancellationToken cancellation = default,
        Func<StableId, VoxelValue, VoxelValue>? remap = null)
    {
        var chunks = new System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, SparseVoxelChunk>();
        foreach (var entry in entries.Select((value, index) => (value, index))
                     .OrderBy(item => item.value.SortingLayer)
                     .ThenBy(item => item.value.SortingOrder)
                     .ThenBy(item => item.index))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_objects.TryGetValue(entry.value.ObjectId, out var world)) continue;
            var objectId = entry.value.ObjectId;
            var sourceChunks = world.SnapshotChunks();
            if (world.OccupiedVoxelCount < ParallelComposeThreshold)
            {
                foreach (var source in sourceChunks) ComposeChunk(chunks, source, objectId, remap, cancellation);
                continue;
            }

            Parallel.ForEach(sourceChunks, new ParallelOptions { CancellationToken = cancellation },
                source => ComposeChunk(chunks, source, objectId, remap, cancellation));
        }

        cancellation.ThrowIfCancellationRequested();
        var result = new VoxelWorld();
        foreach (var (coordinate, chunk) in chunks) result.AdoptChunk(coordinate, chunk);
        return result;
    }

    /// <summary>
    /// 把一个源区块的全部体素写进合成结果的同一区块，源区块与目标区块坐标相同
    /// </summary>
    /// <param name="chunks">合成结果的分区块缓冲</param>
    /// <param name="source">源区块坐标与数据</param>
    /// <param name="objectId">体素所属对象</param>
    /// <param name="remap">材质槽改写，可为空</param>
    /// <param name="cancellation">取消令牌</param>
    private static void ComposeChunk(
        System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, SparseVoxelChunk> chunks,
        (ChunkCoord64 Coordinate, SparseVoxelChunk Chunk) source, StableId objectId,
        Func<StableId, VoxelValue, VoxelValue>? remap, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source.Chunk.OccupiedCount == 0) return;
        var target = chunks.GetOrAdd(source.Coordinate, _ => new SparseVoxelChunk());
        foreach (var (index, value) in source.Chunk.Snapshot)
            target.SetIndex(index, remap is null ? value : remap(objectId, value));
    }
}

internal readonly record struct ObjectVoxelRenderEntry(StableId ObjectId, int SortingLayer, int SortingOrder);
