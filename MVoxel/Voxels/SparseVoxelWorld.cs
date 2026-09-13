using MVoxel.Core;

namespace MVoxel.Voxels;

public readonly record struct ChunkCoord64(long X, long Y, long Z);

public readonly struct LocalCoord : IEquatable<LocalCoord>
{
    public LocalCoord(int x, int y, int z)
    {
        if ((uint)x >= VoxelWorld.ChunkSize || (uint)y >= VoxelWorld.ChunkSize || (uint)z >= VoxelWorld.ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Local coordinates must be inside the chunk.");
        }

        X = (byte)x;
        Y = (byte)y;
        Z = (byte)z;
    }

    public byte X { get; }
    public byte Y { get; }
    public byte Z { get; }

    public bool Equals(LocalCoord other) => X == other.X && Y == other.Y && Z == other.Z;
    public static bool operator ==(LocalCoord left, LocalCoord right) => left.Equals(right);
    public static bool operator !=(LocalCoord left, LocalCoord right) => !left.Equals(right);
    public override bool Equals(object? obj) => obj is LocalCoord other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>Full RGB/RGBA color; it is not restricted to a 256-color palette.</summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba32 TransparentBlack => new(0, 0, 0, 0);
    public static Rgba32 OpaqueWhite => new(255, 255, 255, 255);
}

public readonly record struct VoxelValue(Rgba32 Color, ushort MaterialSlot = 0, byte Flags = 0)
{
    public bool IsEmpty => Color.A == 0 && MaterialSlot == 0 && Flags == 0;
}

/// <summary>
/// Editable sparse chunk. Empty cells are absent from the dictionary, so the
/// world has no fixed maximum canvas and pays only for occupied voxels.
/// </summary>
public sealed class SparseVoxelChunk
{
    private readonly Dictionary<int, VoxelValue> _voxels = new();

    public int OccupiedCount => _voxels.Count;
    public bool IsEmpty => _voxels.Count == 0;
    public IReadOnlyDictionary<int, VoxelValue> Snapshot => _voxels;

    public bool TryGet(LocalCoord coordinate, out VoxelValue value) =>
        _voxels.TryGetValue(ToIndex(coordinate), out value);

    public void Set(LocalCoord coordinate, VoxelValue value)
    {
        if (value.IsEmpty)
        {
            _voxels.Remove(ToIndex(coordinate));
            return;
        }

        _voxels[ToIndex(coordinate)] = value;
    }

    public bool Remove(LocalCoord coordinate) => _voxels.Remove(ToIndex(coordinate));

    public static int ToIndex(LocalCoord coordinate) =>
        coordinate.X + VoxelWorld.ChunkSize * (coordinate.Y + VoxelWorld.ChunkSize * coordinate.Z);

    public static LocalCoord FromIndex(int index)
    {
        if ((uint)index >= VoxelWorld.ChunkVolume)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var x = index % VoxelWorld.ChunkSize;
        var y = index / VoxelWorld.ChunkSize % VoxelWorld.ChunkSize;
        var z = index / (VoxelWorld.ChunkSize * VoxelWorld.ChunkSize);
        return new LocalCoord(x, y, z);
    }
}

/// <summary>
/// Logically unbounded sparse world using mathematically correct floor
/// division for negative coordinates. The storage policy can later be backed
/// by streaming/compressed handles without changing this API.
/// </summary>
public sealed class VoxelWorld
{
    public const int ChunkSize = 32;
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    private readonly Dictionary<ChunkCoord64, SparseVoxelChunk> _chunks = new();

    public int LoadedChunkCount => _chunks.Count;
    public IEnumerable<ChunkCoord64> LoadedChunks => _chunks.Keys;

    public bool TryGet(VoxelCoord64 coordinate, out VoxelValue value)
    {
        var (chunkCoordinate, localCoordinate) = Split(coordinate);
        if (_chunks.TryGetValue(chunkCoordinate, out var chunk)
            && chunk.TryGet(localCoordinate, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public void Set(VoxelCoord64 coordinate, VoxelValue value)
    {
        var (chunkCoordinate, localCoordinate) = Split(coordinate);
        if (value.IsEmpty)
        {
            if (_chunks.TryGetValue(chunkCoordinate, out var existing))
            {
                existing.Remove(localCoordinate);
                if (existing.IsEmpty)
                {
                    _chunks.Remove(chunkCoordinate);
                }
            }

            return;
        }

        if (!_chunks.TryGetValue(chunkCoordinate, out var chunk))
        {
            chunk = new SparseVoxelChunk();
            _chunks.Add(chunkCoordinate, chunk);
        }

        chunk.Set(localCoordinate, value);
    }

    public bool Remove(VoxelCoord64 coordinate)
    {
        var (chunkCoordinate, localCoordinate) = Split(coordinate);
        if (!_chunks.TryGetValue(chunkCoordinate, out var chunk) || !chunk.Remove(localCoordinate))
        {
            return false;
        }

        if (chunk.IsEmpty)
        {
            _chunks.Remove(chunkCoordinate);
        }

        return true;
    }

    public static (ChunkCoord64 Chunk, LocalCoord Local) Split(VoxelCoord64 coordinate)
    {
        var chunkX = FloorDiv(coordinate.X, ChunkSize);
        var chunkY = FloorDiv(coordinate.Y, ChunkSize);
        var chunkZ = FloorDiv(coordinate.Z, ChunkSize);
        return (
            new ChunkCoord64(chunkX, chunkY, chunkZ),
            new LocalCoord(
                FloorMod(coordinate.X, ChunkSize),
                FloorMod(coordinate.Y, ChunkSize),
                FloorMod(coordinate.Z, ChunkSize)));
    }

    public static long FloorDiv(long value, int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor));
        }

        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    public static int FloorMod(long value, int divisor)
    {
        var remainder = value % divisor;
        return (int)(remainder < 0 ? remainder + divisor : remainder);
    }
}
