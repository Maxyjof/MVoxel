namespace MVoxel.Voxels;

/// <summary>Non-destructive axis-aligned voxel slice used by editor inspection views.</summary>
public static class VoxelWorldSlice
{
    /// <summary>
    /// Copies the selected half-space into a new world. Axis is 0=X, 1=Y, 2=Z.
    /// </summary>
    public static VoxelWorld CreateHalfSpace(VoxelWorld source, int axis, long plane, bool keepGreater)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
        var result = new VoxelWorld();
        foreach (var (coordinate, value) in source.EnumerateOccupied())
        {
            var component = axis switch { 0 => coordinate.X, 1 => coordinate.Y, _ => coordinate.Z };
            if (keepGreater ? component >= plane : component <= plane) result.Set(coordinate, value);
        }
        return result;
    }
}
