using MVoxel.Core;

namespace MVoxel.Rendering;

/// <summary>
/// 将接收层快照转换成可上传的开放寻址表。每槽四个 uint：相对工作集原点的
/// x/y/z 位模式及图层加一；第四项为零表示空槽。此表不改变体素或材质数据。
/// </summary>
internal sealed class SurfaceReceiverLayerTable
{
    internal const int WordsPerSlot = 4;
    internal uint[] Words { get; }
    internal int SlotCount => Words.Length / WordsPerSlot;
    internal VoxelCoord64 Origin { get; }

    private SurfaceReceiverLayerTable(VoxelCoord64 origin, uint[] words)
    {
        Origin = origin;
        Words = words;
    }

    internal static SurfaceReceiverLayerTable Build(IReadOnlyDictionary<VoxelCoord64, byte> layers,
        VoxelCoord64 origin, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var slots = 1;
        var required = Math.Max(1L, (long)layers.Count * 2L);
        while (slots < required)
        {
            if (slots > Array.MaxLength / (WordsPerSlot * 2))
                throw new InvalidOperationException("表面接收图层表超出可分配容量");
            slots *= 2;
        }
        var words = new uint[checked(slots * WordsPerSlot)];
        var count = 0;
        foreach (var (coordinate, layer) in layers)
        {
            if ((count++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (layer >= 32) throw new ArgumentOutOfRangeException(nameof(layers), "接收图层必须在 0–31 之间");
            var x = checked((int)(coordinate.X - origin.X));
            var y = checked((int)(coordinate.Y - origin.Y));
            var z = checked((int)(coordinate.Z - origin.Z));
            var index = (int)(Hash(x, y, z) & (uint)(slots - 1));
            while (words[index * WordsPerSlot + 3] != 0)
                index = (index + 1) & (slots - 1);
            var offset = index * WordsPerSlot;
            words[offset] = unchecked((uint)x);
            words[offset + 1] = unchecked((uint)y);
            words[offset + 2] = unchecked((uint)z);
            words[offset + 3] = (uint)layer + 1;
        }
        return new SurfaceReceiverLayerTable(origin, words);
    }

    // GPU 查找必须使用同一组无符号运算和常量。
    private static uint Hash(int x, int y, int z)
    {
        unchecked
        {
            var value = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            return value ^ (value >> 16);
        }
    }
}
