using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels;

public enum VoxelBrushShape
{
    Cube,
    Sphere,
}

/// <summary>
/// Pure brush geometry: given the DDA hit (cell + entry normal) it produces the
/// voxel writes a single stroke should apply. Draw places the brush on the hit
/// face, erase removes around the hit cell itself.
/// </summary>
public static class VoxelBrush
{
    public static IReadOnlyList<VoxelCoord64> CellsForStroke(
        VoxelDda.Hit hit, bool erase, VoxelBrushShape shape, int radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        var center = erase
            ? hit.Coordinate
            : hit.Coordinate + hit.Normal;
        return CellsAround(center, shape, radius);
    }

    public static IReadOnlyList<VoxelCoord64> CellsForGround(Vector3 groundPoint, VoxelBrushShape shape, int radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        var cell = new VoxelCoord64(
            (long)MathF.Floor(groundPoint.X),
            (long)MathF.Floor(groundPoint.Y), // plane hit at y=0 lands in cell [0,1)
            (long)MathF.Floor(groundPoint.Z));
        return CellsAround(cell, shape, radius);
    }

    public static IReadOnlyList<VoxelCoord64> CellsAround(VoxelCoord64 center, VoxelBrushShape shape, int radius)
    {
        var cells = new List<VoxelCoord64>();
        var reach = radius - 1;
        for (var y = -reach; y <= reach; y++)
        for (var z = -reach; z <= reach; z++)
        for (var x = -reach; x <= reach; x++)
        {
            if (shape == VoxelBrushShape.Sphere)
            {
                var squared = x * x + y * y + z * z;
                if (squared > radius * radius - radius * 0.5F) continue;
            }

            cells.Add(center + new VoxelCoord64(x, y, z));
        }

        return cells;
    }
}

/// <summary>
/// One undoable stroke. Records previous values so Undo restores exactly, and
/// keeps the world reference for Redo re-application.
/// </summary>
public sealed class VoxelStrokeCommand : IEditorCommand
{
    private readonly VoxelWorld _world;
    private readonly (VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)[] _edits;

    public VoxelStrokeCommand(string name, VoxelWorld world, IEnumerable<VoxelCoord64> cells, VoxelValue after)
    {
        ArgumentNullException.ThrowIfNull(world);
        Name = name;
        _world = world;
        var edits = new List<(VoxelCoord64, VoxelValue, VoxelValue)>();
        foreach (var cell in cells)
        {
            var before = world.TryGet(cell, out var existing) ? existing : default;
            if (before == after) continue;
            edits.Add((cell, before, after));
        }

        _edits = edits.ToArray();
    }

    public string Name { get; }
    public int ChangedCellCount => _edits.Length;

    public void Execute()
    {
        foreach (var (coordinate, _, after) in _edits) _world.Set(coordinate, after);
    }

    public void Undo()
    {
        foreach (var (coordinate, before, _) in _edits) _world.Set(coordinate, before);
    }
}
