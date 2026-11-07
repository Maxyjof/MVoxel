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
    /// 对单个对象生成可撤销的整份平移：直接复制并位移区块，
    /// 不再构造逐格差异表（29 万格时会额外付出上百毫秒）
    /// </summary>
    /// <param name="id">对象标识</param>
    /// <param name="name">命令名</param>
    /// <param name="delta">整格平移量</param>
    /// <param name="onApplied">应用与撤回后的回调</param>
    /// <returns>可撤销命令</returns>
    internal IEditorCommand CreateTranslation(StableId id, string name, VoxelCoord64 delta,
        Action<bool>? onApplied = null)
    {
        if (delta == VoxelCoord64.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "平移量不能为零");
        return new VoxelTranslationCommand(this, id, name, GetOrCreate(id), delta, onApplied);
    }

    /// <summary>
    /// 整份平移命令：以拖动前的世界为基准，执行与撤回都各自生成一份平移副本
    /// </summary>
    private sealed class VoxelTranslationCommand(ObjectVoxelStore store, StableId id, string name,
        VoxelWorld original, VoxelCoord64 delta, Action<bool>? onApplied) : IEditorCommand
    {
        public string Name => name;

        public void Execute()
        {
            store.AssignWorld(id, original.CloneShifted(delta.X, delta.Y, delta.Z));
            onApplied?.Invoke(true);
        }

        public void Undo()
        {
            store.AssignWorld(id, original.CloneShifted(-delta.X, -delta.Y, -delta.Z));
            onApplied?.Invoke(false);
        }
    }

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
    /// 按对象可见性和排序配置合成世界，并可选地记录每个区块最终由哪个对象写入。
    /// 归属按合成次序取最后一个写入者，与合成时的覆盖优先级一致，供模型级 LOD 分档使用。
    /// </summary>
    internal VoxelWorld Compose(SceneDocument scene, uint cameraMask, bool useSceneVisibility,
        CancellationToken cancellation = default, Func<StableId, VoxelValue, VoxelValue>? remap = null,
        IDictionary<ChunkCoord64, StableId>? chunkOwners = null,
        StableId shiftObjectId = default, VoxelCoord64 shift = default, StableId excludeObjectId = default) =>
        Compose(GetVisibleEntries(scene, cameraMask, useSceneVisibility), cancellation, remap, chunkOwners,
            shiftObjectId, shift, excludeObjectId);

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

    internal Dictionary<VoxelCoord64, (StableId Owner, VoxelValue Value)> ResolveVisibleVoxels(
        SceneDocument scene, uint cameraMask, bool useSceneVisibility, IReadOnlyCollection<VoxelCoord64> coordinates)
    {
        var entries = GetVisibleEntries(scene, cameraMask, useSceneVisibility)
            .Select((value, index) => (value, index))
            .OrderByDescending(item => item.value.SortingLayer)
            .ThenByDescending(item => item.value.SortingOrder)
            .ThenByDescending(item => item.index)
            .ToArray();
        var result = new Dictionary<VoxelCoord64, (StableId, VoxelValue)>(coordinates.Count);
        foreach (var coordinate in coordinates)
        foreach (var entry in entries)
        {
            if (!_objects.TryGetValue(entry.value.ObjectId, out var world) ||
                !world.TryGet(coordinate, out var value)) continue;
            result[coordinate] = (entry.value.ObjectId, value);
            break;
        }
        return result;
    }

    internal bool TryGetVisibleChunkOwner(SceneDocument scene, uint cameraMask, bool useSceneVisibility,
        ChunkCoord64 coordinate, out StableId owner)
    {
        owner = StableId.Empty;
        foreach (var entry in GetVisibleEntries(scene, cameraMask, useSceneVisibility)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.SortingLayer)
                     .ThenBy(item => item.value.SortingOrder)
                     .ThenBy(item => item.index))
        {
            if (!_objects.TryGetValue(entry.value.ObjectId, out var world) ||
                !world.TryGetChunk(coordinate, out _)) continue;
            owner = entry.value.ObjectId;
        }
        return owner != StableId.Empty;
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
        Func<StableId, VoxelValue, VoxelValue>? remap = null,
        IDictionary<ChunkCoord64, StableId>? chunkOwners = null,
        StableId shiftObjectId = default, VoxelCoord64 shift = default, StableId excludeObjectId = default)
    {
        var chunks = new System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, SparseVoxelChunk>();
        var owners = chunkOwners is null ? null : new System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, StableId>();
        foreach (var entry in entries.Select((value, index) => (value, index))
                     .OrderBy(item => item.value.SortingLayer)
                     .ThenBy(item => item.value.SortingOrder)
                     .ThenBy(item => item.index))
        {
            cancellation.ThrowIfCancellationRequested();
            // 拖动实例预览把目标对象单独交给加速结构实例，静态世界必须排除它，
            // 否则旧位置的几何会和实例位置的新几何同时出现
            if (excludeObjectId != StableId.Empty && entry.value.ObjectId == excludeObjectId) continue;
            if (!_objects.TryGetValue(entry.value.ObjectId, out var world)) continue;
            var objectId = entry.value.ObjectId;
            var sourceChunks = world.SnapshotChunks();
            if (sourceChunks.Count == 0) continue;
            // 拖动预览只给目标对象叠加平移，其余对象照旧
            var objectShift = objectId == shiftObjectId ? shift : VoxelCoord64.Zero;
            if (objectShift != VoxelCoord64.Zero)
            {
                // 平移后体素会跨区块边界，必须按世界坐标重新分桶；写进合成结果后
                // 区块归属按目标区块记，供图层与编辑归属查询使用
                foreach (var source in sourceChunks)
                    ComposeShiftedChunk(chunks, source, objectId, objectShift, remap, owners, cancellation);
                continue;
            }
            // 合成次序决定覆盖优先级，归属记录跟随同一次序：最后一个写该区块的对象生效
            if (owners is not null)
            {
                for (var index = 0; index < sourceChunks.Count; index++)
                    owners[sourceChunks[index].Coordinate] = objectId;
            }

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
        if (chunkOwners is not null && owners is not null)
        {
            foreach (var (coordinate, owner) in owners) chunkOwners[coordinate] = owner;
        }

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

    /// <summary>
    /// 把一个源区块按平移量重新分桶写入合成结果。拖动预览用它代替“克隆整份世界再平移”，
    /// 省掉一次完整的世界复制与再合成，拖动时每步只付一次合成开销
    /// </summary>
    private static void ComposeShiftedChunk(
        System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, SparseVoxelChunk> chunks,
        (ChunkCoord64 Coordinate, SparseVoxelChunk Chunk) source, StableId objectId, VoxelCoord64 shift,
        Func<StableId, VoxelValue, VoxelValue>? remap,
        System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord64, StableId>? owners,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source.Chunk.OccupiedCount == 0) return;
        var baseX = checked(source.Coordinate.X * VoxelWorld.ChunkSize + shift.X);
        var baseY = checked(source.Coordinate.Y * VoxelWorld.ChunkSize + shift.Y);
        var baseZ = checked(source.Coordinate.Z * VoxelWorld.ChunkSize + shift.Z);
        foreach (var (index, value) in source.Chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            var worldX = baseX + local.X;
            var worldY = baseY + local.Y;
            var worldZ = baseZ + local.Z;
            var coordinate = new ChunkCoord64(FloorDiv(worldX, VoxelWorld.ChunkSize),
                FloorDiv(worldY, VoxelWorld.ChunkSize), FloorDiv(worldZ, VoxelWorld.ChunkSize));
            chunks.GetOrAdd(coordinate, _ => new SparseVoxelChunk()).Set(
                new LocalCoord((int)FloorMod(worldX, VoxelWorld.ChunkSize),
                    (int)FloorMod(worldY, VoxelWorld.ChunkSize),
                    (int)FloorMod(worldZ, VoxelWorld.ChunkSize)),
                remap is null ? value : remap(objectId, value));
            owners?[coordinate] = objectId;
        }
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        if ((value % divisor != 0) && ((value < 0) != (divisor < 0))) quotient--;
        return quotient;
    }

    private static long FloorMod(long value, long divisor) => value - FloorDiv(value, divisor) * divisor;
}

internal readonly record struct ObjectVoxelRenderEntry(StableId ObjectId, int SortingLayer, int SortingOrder);
