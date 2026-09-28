using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>合并态体素世界中的模型归属。主模型不入所有权表，分层模型按对象 ID 记录。</summary>
internal static class VoxelEditOwnership
{
    internal readonly record struct Change(VoxelCoord64 Coordinate, StableId? Before, StableId? After);

    internal static StableId? ResolveOwner(VoxelCoord64 coordinate, StableId? primaryId,
        IReadOnlyList<SceneObject> objects, IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> ownership)
    {
        // 分层按场景顺序叠加，重叠格以后写入的对象为当前可见所有者。
        for (var index = objects.Count - 1; index >= 0; index--)
        {
            var sceneObject = objects[index];
            if (ownership.TryGetValue(sceneObject.Id, out var owned) && owned.Contains(coordinate))
                return sceneObject.Id;
        }

        return primaryId;
    }

    internal static List<VoxelCoord64> FilterEditableCells(IReadOnlyList<VoxelCoord64> cells,
        VoxelWorld world, StableId? targetId, StableId? primaryId, IReadOnlyList<SceneObject> objects,
        IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> ownership, bool erase)
    {
        var result = new List<VoxelCoord64>(cells.Count);
        foreach (var coordinate in cells)
        {
            var occupied = world.TryGet(coordinate, out _);
            if (!occupied)
            {
                if (!erase) result.Add(coordinate);
                continue;
            }

            // 无论是擦除还是重绘，都不能越过模型边界改动别人的方块。
            var owner = ResolveOwner(coordinate, primaryId, objects, ownership);
            if (owner == targetId) result.Add(coordinate);
        }

        return result;
    }

    internal static Change[] Capture(IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)> edits,
        StableId? targetId, StableId? primaryId, IReadOnlyList<SceneObject> objects,
        IReadOnlyDictionary<StableId, HashSet<VoxelCoord64>> ownership)
    {
        var changes = new List<Change>();
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var before = edit.Before.IsEmpty ? null : ResolveOwner(edit.Coordinate, primaryId, objects, ownership);
            var after = edit.After.IsEmpty ? null : targetId;
            if (before != after) changes.Add(new Change(edit.Coordinate, before, after));
        }

        return changes.ToArray();
    }

    internal static void Apply(IReadOnlyList<Change> changes, bool forward, StableId? primaryId,
        IDictionary<StableId, HashSet<VoxelCoord64>> ownership)
    {
        foreach (var change in changes)
        {
            foreach (var owned in ownership.Values) owned.Remove(change.Coordinate);
            var owner = forward ? change.After : change.Before;
            if (owner is not { } id || id == primaryId) continue;
            if (!ownership.TryGetValue(id, out var target))
            {
                target = new HashSet<VoxelCoord64>();
                ownership[id] = target;
            }
            target.Add(change.Coordinate);
        }
    }
}
