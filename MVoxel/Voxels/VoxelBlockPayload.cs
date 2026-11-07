using System.IO.Compression;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 32³ 区块载荷的通用编解码：调色板 + 局部坐标 + 调色板索引，外层 Brotli 压缩。
/// 场景内嵌体素与预烘焙 LOD 共用同一格式，保证同样的体素写出同样的字节。
/// </summary>
internal static class VoxelBlockPayload
{
    internal const ushort Version = 1;

    /// <summary>
    /// 编码一个区块的未压缩载荷；体素值按颜色、材质、标志排序后建调色板，索引按坐标顺序写出
    /// </summary>
    internal static byte[] BuildRaw(SparseVoxelChunk chunk)
    {
        var values = chunk.Snapshot.Values.Distinct().ToList();
        values.Sort(static (left, right) =>
        {
            var result = left.Color.R.CompareTo(right.Color.R);
            if (result != 0) return result;
            result = left.Color.G.CompareTo(right.Color.G);
            if (result != 0) return result;
            result = left.Color.B.CompareTo(right.Color.B);
            if (result != 0) return result;
            result = left.Color.A.CompareTo(right.Color.A);
            if (result != 0) return result;
            result = left.MaterialSlot.CompareTo(right.MaterialSlot);
            return result != 0 ? result : left.Flags.CompareTo(right.Flags);
        });
        var paletteIndex = new Dictionary<VoxelValue, ushort>(values.Count);
        for (var index = 0; index < values.Count; index++) paletteIndex[values[index]] = (ushort)index;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write((ushort)values.Count);
            foreach (var value in values)
            {
                writer.Write(value.Color.R);
                writer.Write(value.Color.G);
                writer.Write(value.Color.B);
                writer.Write(value.Color.A);
                writer.Write(value.MaterialSlot);
                writer.Write(value.Flags);
            }
            writer.Write((ushort)chunk.OccupiedCount);
            foreach (var index in chunk.Snapshot.Keys.Order())
            {
                var local = SparseVoxelChunk.FromIndex(index);
                writer.Write((ushort)local.X);
                writer.Write((ushort)local.Y);
                writer.Write((ushort)local.Z);
                writer.Write(paletteIndex[chunk.Snapshot[index]]);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 编码并 Brotli 压缩一个区块，供没有场景级去重存储的独立资产使用
    /// </summary>
    internal static byte[] BuildCompressed(SparseVoxelChunk chunk)
    {
        var raw = BuildRaw(chunk);
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    /// <summary>
    /// 解码一个区块并写入目标世界；坐标按区块原点加局部坐标计算
    /// </summary>
    internal static void Decode(byte[] compressed, ChunkCoord64 coordinate, VoxelWorld world,
        VoxelWorld.BulkWriter writer, int blockSize)
    {
        using var stream = new MemoryStream(compressed, writable: false);
        using var decoder = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new BinaryReader(decoder, Encoding.ASCII, leaveOpen: false);
        if (reader.ReadUInt16() != Version)
            throw new InvalidDataException("体素区块载荷版本不受支持");
        var paletteCount = reader.ReadUInt16();
        var palette = new VoxelValue[paletteCount];
        for (var index = 0; index < paletteCount; index++)
        {
            var color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            palette[index] = new VoxelValue(color, reader.ReadUInt16(), reader.ReadByte());
        }

        var count = reader.ReadUInt16();
        for (var index = 0; index < count; index++)
        {
            var localX = reader.ReadUInt16();
            var localY = reader.ReadUInt16();
            var localZ = reader.ReadUInt16();
            var paletteIndex = reader.ReadUInt16();
            if (localX >= blockSize || localY >= blockSize || localZ >= blockSize || paletteIndex >= paletteCount)
                throw new InvalidDataException("体素区块载荷包含越界坐标或无效调色板索引");
            var value = palette[paletteIndex];
            if (value.IsEmpty) throw new InvalidDataException("体素区块载荷包含空体素");
            var target = new VoxelCoord64(
                checked(coordinate.X * blockSize + localX),
                checked(coordinate.Y * blockSize + localY),
                checked(coordinate.Z * blockSize + localZ));
            if (world.TryGet(target, out _))
                throw new InvalidDataException("体素区块载荷包含重复坐标");
            writer.Set(target, value);
        }
    }
}
