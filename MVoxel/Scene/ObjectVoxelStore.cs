using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 每个对象独立保存体素，合成时只在同一坐标选择优先级最高的体素
/// </summary>
internal sealed class ObjectVoxelStore
{
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

    internal bool TryGetWorld(StableId id, out VoxelWorld world) => _objects.TryGetValue(id, out world!);

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
            if (visible && component is { Enabled: true } &&
                RenderWorldComposer.LayerAllowed(cameraMask, item.LayerIndex) &&
                (!useSceneVisibility || scene.IsLayerVisible(item.LayerIndex)))
            {
                var data = SceneComponentTypes.ReadVoxelModel(component);
                var layer = scene.SortingLayerNames.FindIndex(name =>
                    string.Equals(name, data.SortingLayer, StringComparison.Ordinal));
                if (layer < 0) layer = scene.SortingLayerNames.IndexOf("默认");
                entries.Add(new ObjectVoxelRenderEntry(item.Id, Math.Max(0, layer), data.SortingOrder));
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
    internal VoxelWorld Compose(IEnumerable<ObjectVoxelRenderEntry> entries, CancellationToken cancellation = default,
        Func<StableId, VoxelValue, VoxelValue>? remap = null)
    {
        var result = new VoxelWorld();
        var writer = result.CreateBulkWriter();
        var processed = 0;
        foreach (var entry in entries.Select((value, index) => (value, index))
                     .OrderBy(item => item.value.SortingLayer)
                     .ThenBy(item => item.value.SortingOrder)
                     .ThenBy(item => item.index))
        {
            if (!_objects.TryGetValue(entry.value.ObjectId, out var world)) continue;
            foreach (var (coordinate, value) in world.EnumerateOccupied())
            {
                if ((processed++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                writer.Set(coordinate, remap is null ? value : remap(entry.value.ObjectId, value));
            }
        }
        return result;
    }
}

internal readonly record struct ObjectVoxelRenderEntry(StableId ObjectId, int SortingLayer, int SortingOrder);
