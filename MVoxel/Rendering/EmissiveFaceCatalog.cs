using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 从完整世界提取可供直接光采样的单位面。此索引是运行时派生数据，
/// 与渲染几何的合并及 GPU 池布局无关；暂不包含半透明发光体。
/// </summary>
internal sealed class EmissiveFaceCatalog
{
    internal readonly record struct Face(VoxelCoord64 Voxel, byte Direction, VoxelValue Value);

    private static readonly (int X, int Y, int Z)[] Directions =
    [
        (-1, 0, 0), (1, 0, 0), (0, -1, 0),
        (0, 1, 0), (0, 0, -1), (0, 0, 1)
    ];

    private readonly Dictionary<ChunkCoord64, Face[]> _chunks = new();

    internal int FaceCount { get; private set; }

    /// <summary>按区块和体素坐标稳定枚举，不依赖字典枚举顺序。</summary>
    internal IEnumerable<Face> Faces => _chunks
        .OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X)
        .SelectMany(pair => pair.Value);

    internal void Rebuild(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _chunks.Clear();
        FaceCount = 0;
        foreach (var coordinate in world.LoadedChunks) RebuildChunk(world, coordinate);
    }

    /// <summary>重建脏区块及其六邻区：边界编辑会改变邻区发光面的暴露状态。</summary>
    internal void Update(VoxelWorld world, IEnumerable<ChunkCoord64> dirty)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(dirty);
        var affected = new HashSet<ChunkCoord64>();
        foreach (var coordinate in dirty)
        {
            affected.Add(coordinate);
            foreach (var (x, y, z) in Directions)
                affected.Add(new ChunkCoord64(coordinate.X + x, coordinate.Y + y, coordinate.Z + z));
        }
        foreach (var coordinate in affected) RebuildChunk(world, coordinate);
    }

    private void RebuildChunk(VoxelWorld world, ChunkCoord64 coordinate)
    {
        if (_chunks.Remove(coordinate, out var previous)) FaceCount -= previous.Length;
        if (!world.TryGetChunk(coordinate, out var chunk)) return;
        // 直接遍历区块字典：唯一的消费方 UploadEmissiveFaces 会按砖块与坐标完整排序，
        // 区块内顺序不影响结果，因此不必为每个区块再做一次 LINQ 排序
        List<Face>? faces = null;
        foreach (var (index, value) in chunk.Snapshot)
        {
            if (!VoxelFlags.Has(value.Flags, VoxelFlags.Emissive) ||
                VoxelFlags.Has(value.Flags, VoxelFlags.Translucent) || value.Color.A != byte.MaxValue)
                continue;
            var local = SparseVoxelChunk.FromIndex(index);
            var voxel = new VoxelCoord64(coordinate.X * VoxelWorld.ChunkSize + local.X,
                coordinate.Y * VoxelWorld.ChunkSize + local.Y, coordinate.Z * VoxelWorld.ChunkSize + local.Z);
            for (byte direction = 0; direction < Directions.Length; direction++)
            {
                var (x, y, z) = Directions[direction];
                var neighbor = new VoxelCoord64(voxel.X + x, voxel.Y + y, voxel.Z + z);
                // 首版仅采样邻格完全为空的面；其余发光仍由原有命中路径处理。
                if (!world.TryGet(neighbor, out var adjacent) || adjacent.IsEmpty)
                    (faces ??= new List<Face>()).Add(new Face(voxel, direction, value));
            }
        }

        if (faces is null) return;
        var result = faces.ToArray();
        _chunks.Add(coordinate, result);
        FaceCount = checked(FaceCount + result.Length);
    }
}
