using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 区块的 64 位整数坐标，负方向同样有效
/// </summary>
/// <param name="X">区块 X 坐标</param>
/// <param name="Y">区块 Y 坐标</param>
/// <param name="Z">区块 Z 坐标</param>
public readonly record struct ChunkCoord64(long X, long Y, long Z);

/// <summary>
/// 区块内部的局部坐标，三个分量都被约束在 0 到 ChunkSize 减一之间
/// </summary>
public readonly struct LocalCoord : IEquatable<LocalCoord>
{
    /// <summary>
    /// 构造局部坐标，越界立即抛异常
    /// </summary>
    /// <param name="x">局部 X，必须小于区块边长</param>
    /// <param name="y">局部 Y，必须小于区块边长</param>
    /// <param name="z">局部 Z，必须小于区块边长</param>
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

    /// <summary>
    /// 局部 X 分量
    /// </summary>
    public byte X { get; }

    /// <summary>
    /// 局部 Y 分量
    /// </summary>
    public byte Y { get; }

    /// <summary>
    /// 局部 Z 分量
    /// </summary>
    public byte Z { get; }

    /// <summary>
    /// 按三个分量比较
    /// </summary>
    /// <param name="other">另一个局部坐标</param>
    /// <returns>是否完全相同</returns>
    public bool Equals(LocalCoord other) => X == other.X && Y == other.Y && Z == other.Z;

    /// <summary>
    /// 相等运算符
    /// </summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>是否相等</returns>
    public static bool operator ==(LocalCoord left, LocalCoord right) => left.Equals(right);

    /// <summary>
    /// 不等运算符
    /// </summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>是否不等</returns>
    public static bool operator !=(LocalCoord left, LocalCoord right) => !left.Equals(right);

    /// <summary>
    /// 与任意对象的相等判断
    /// </summary>
    /// <param name="obj">待比较对象</param>
    /// <returns>是否为相同的局部坐标</returns>
    public override bool Equals(object? obj) => obj is LocalCoord other && Equals(other);

    /// <summary>
    /// 组合三个分量的哈希码
    /// </summary>
    /// <returns>稳定哈希值</returns>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>
    /// 输出可读坐标文本
    /// </summary>
    /// <returns>形如 (x, y, z) 的文本</returns>
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// 完整 RGB/RGBA 颜色，不受 256 色调色板限制
/// 调色板只是编辑便利，存储层永远是逐体素的 32 位颜色
/// </summary>
/// <param name="R">红通道</param>
/// <param name="G">绿通道</param>
/// <param name="B">蓝通道</param>
/// <param name="A">Alpha 通道，0 表示空气</param>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>
    /// 全透明黑，即"空气"
    /// </summary>
    public static Rgba32 TransparentBlack => new(0, 0, 0, 0);

    /// <summary>
    /// 不透明白
    /// </summary>
    public static Rgba32 OpaqueWhite => new(255, 255, 255, 255);
}

/// <summary>
/// 体素的存储值：颜色加材质槽加标志位
/// </summary>
/// <param name="Color">RGBA 颜色</param>
/// <param name="MaterialSlot">材质槽索引，供未来 .material 资产引用</param>
/// <param name="Flags">材质标志，见 <see cref="VoxelFlags"/></param>
public readonly record struct VoxelValue(Rgba32 Color, ushort MaterialSlot = 0, byte Flags = 0)
{
    /// <summary>
    /// 是否为可丢弃的空体素（透明且无材质信息）
    /// </summary>
    public bool IsEmpty => Color.A == 0 && MaterialSlot == 0 && Flags == 0;
}

/// <summary>
/// 逐体素材质标志位，渲染器据此启用半透明吸收或自发光采样
/// </summary>
public static class VoxelFlags
{
    /// <summary>
    /// 无附加标志的不透明体素
    /// </summary>
    public const byte None = 0;

    /// <summary>
    /// 半透明（玻璃类），路径追踪按电介质折射并做 Beer 吸收
    /// </summary>
    public const byte Translucent = 1;

    /// <summary>
    /// 自发光（灯类），参与路径追踪的直接光采样
    /// </summary>
    public const byte Emissive = 2;

    /// <summary>
    /// 判断标志集合是否包含某个位
    /// </summary>
    /// <param name="flags">体素标志</param>
    /// <param name="flag">待检测位</param>
    /// <returns>是否置位</returns>
    public static bool Has(byte flags, byte flag) => (flags & flag) != 0;
}

/// <summary>
/// 可编辑的稀疏区块，空格子不占字典条目，因此世界没有画布上限，
/// 只为实际占用的体素付出内存
/// </summary>
public sealed class SparseVoxelChunk
{
    private readonly Dictionary<int, VoxelValue> _voxels = new();

    /// <summary>
    /// 本区块已占用的体素数
    /// </summary>
    public int OccupiedCount => _voxels.Count;

    /// <summary>
    /// 区块是否为空，空区块会被世界立即回收
    /// </summary>
    public bool IsEmpty => _voxels.Count == 0;

    /// <summary>
    /// 只读的占用快照，供序列化与工作集上传
    /// </summary>
    public IReadOnlyDictionary<int, VoxelValue> Snapshot => _voxels;

    /// <summary>
    /// 读取局部坐标处的体素
    /// </summary>
    /// <param name="coordinate">区块内局部坐标</param>
    /// <param name="value">命中时输出体素值</param>
    /// <returns>是否存在</returns>
    public bool TryGet(LocalCoord coordinate, out VoxelValue value) =>
        _voxels.TryGetValue(ToIndex(coordinate), out value);

    /// <summary>
    /// 写入局部坐标处的体素，空值等价于删除
    /// </summary>
    /// <param name="coordinate">区块内局部坐标</param>
    /// <param name="value">新的体素值</param>
    public void Set(LocalCoord coordinate, VoxelValue value)
    {
        if (value.IsEmpty)
        {
            _voxels.Remove(ToIndex(coordinate));
            return;
        }

        _voxels[ToIndex(coordinate)] = value;
    }

    /// <summary>
    /// 删除局部坐标处的体素
    /// </summary>
    /// <param name="coordinate">区块内局部坐标</param>
    /// <returns>确实删除了内容时返回 true</returns>
    public bool Remove(LocalCoord coordinate) => _voxels.Remove(ToIndex(coordinate));

    /// <summary>
    /// 局部坐标压缩为单一字典索引（x 最快，z 最慢）
    /// </summary>
    /// <param name="coordinate">区块内局部坐标</param>
    /// <returns>线性索引</returns>
    public static int ToIndex(LocalCoord coordinate) =>
        coordinate.X + VoxelWorld.ChunkSize * (coordinate.Y + VoxelWorld.ChunkSize * coordinate.Z);

    /// <summary>
    /// 线性索引还原为局部坐标，是 ToIndex 的精确逆运算
    /// </summary>
    /// <param name="index">线性索引</param>
    /// <returns>局部坐标</returns>
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
/// 逻辑上无限延伸的稀疏体素世界，负坐标使用数学正确的向下取整除法
/// 存储策略未来可以换成流送或压缩句柄而不改变这套对外 API
/// </summary>
public sealed class VoxelWorld
{
    /// <summary>
    /// 区块边长（格数），当前固定 32
    /// </summary>
    public const int ChunkSize = 32;

    /// <summary>
    /// 单区块体素容量
    /// </summary>
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    private readonly Dictionary<ChunkCoord64, SparseVoxelChunk> _chunks = new();

    /// <summary>
    /// 已加载（非空）区块数量
    /// </summary>
    public int LoadedChunkCount => _chunks.Count;

    /// <summary>
    /// 已加载区块的坐标集合
    /// </summary>
    public IEnumerable<ChunkCoord64> LoadedChunks => _chunks.Keys;

    /// <summary>
    /// 每次成功写入自增，渲染器据此判断 GPU 工作集是否需要重传
    /// </summary>
    public int ContentVersion { get; private set; }

    /// <summary>
    /// 全世界占用体素总数，遍历所有区块，仅适合低频调用
    /// </summary>
    public int OccupiedVoxelCount
    {
        get
        {
            var total = 0;
            foreach (var chunk in _chunks.Values) total += chunk.OccupiedCount;
            return total;
        }
    }

    /// <summary>
    /// 遍历全部占用体素，输出世界坐标与值
    /// </summary>
    /// <returns>坐标与值的惰性序列</returns>
    public IEnumerable<(VoxelCoord64 Coordinate, VoxelValue Value)> EnumerateOccupied()
    {
        foreach (var (chunkCoordinate, chunk) in _chunks)
        foreach (var (index, value) in chunk.Snapshot)
        {
            var local = SparseVoxelChunk.FromIndex(index);
            yield return (
                new VoxelCoord64(
                    chunkCoordinate.X * ChunkSize + local.X,
                    chunkCoordinate.Y * ChunkSize + local.Y,
                    chunkCoordinate.Z * ChunkSize + local.Z),
                value);
        }
    }

    /// <summary>
    /// 计算占用体素的轴对齐包围盒
    /// </summary>
    /// <param name="minimum">包围盒最小角</param>
    /// <param name="maximum">包围盒最大角</param>
    /// <returns>世界为空时返回 false</returns>
    public bool TryGetBounds(out VoxelCoord64 minimum, out VoxelCoord64 maximum)
    {
        minimum = default;
        maximum = default;
        var found = false;
        long minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        foreach (var (coordinate, _) in EnumerateOccupied())
        {
            if (!found)
            {
                (minX, minY, minZ) = (coordinate.X, coordinate.Y, coordinate.Z);
                (maxX, maxY, maxZ) = (minX, minY, minZ);
                found = true;
                continue;
            }

            minX = Math.Min(minX, coordinate.X);
            minY = Math.Min(minY, coordinate.Y);
            minZ = Math.Min(minZ, coordinate.Z);
            maxX = Math.Max(maxX, coordinate.X);
            maxY = Math.Max(maxY, coordinate.Y);
            maxZ = Math.Max(maxZ, coordinate.Z);
        }

        minimum = new VoxelCoord64(minX, minY, minZ);
        maximum = new VoxelCoord64(maxX, maxY, maxZ);
        return found;
    }

    /// <summary>
    /// 读取世界坐标处的体素
    /// </summary>
    /// <param name="coordinate">世界整数坐标</param>
    /// <param name="value">命中时输出体素值</param>
    /// <returns>是否存在</returns>
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

    /// <summary>
    /// 写入世界坐标处的体素，空值等价删除，区块清空后立即回收
    /// </summary>
    /// <param name="coordinate">世界整数坐标</param>
    /// <param name="value">新的体素值</param>
    public void Set(VoxelCoord64 coordinate, VoxelValue value)
    {
        var (chunkCoordinate, localCoordinate) = Split(coordinate);
        if (value.IsEmpty)
        {
            if (_chunks.TryGetValue(chunkCoordinate, out var existing))
            {
                existing.Remove(localCoordinate);
                ContentVersion++;
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
        ContentVersion++;
    }

    /// <summary>
    /// 删除世界坐标处的体素
    /// </summary>
    /// <param name="coordinate">世界整数坐标</param>
    /// <returns>确实删除了内容时返回 true</returns>
    public bool Remove(VoxelCoord64 coordinate)
    {
        var (chunkCoordinate, localCoordinate) = Split(coordinate);
        if (!_chunks.TryGetValue(chunkCoordinate, out var chunk) || !chunk.Remove(localCoordinate))
        {
            return false;
        }

        ContentVersion++;
        if (chunk.IsEmpty)
        {
            _chunks.Remove(chunkCoordinate);
        }

        return true;
    }

    /// <summary>
    /// 把世界坐标拆分为区块坐标加局部坐标，负方向按向下取整处理
    /// </summary>
    /// <param name="coordinate">世界整数坐标</param>
    /// <returns>区块与局部坐标对</returns>
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

    /// <summary>
    /// 向下取整除法：-1 除以 32 得 -1 而不是向零截断的 0
    /// </summary>
    /// <param name="value">被除数</param>
    /// <param name="divisor">正除数</param>
    /// <returns>向下取整的商</returns>
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

    /// <summary>
    /// 与 FloorDiv 配套的向下取整取模，结果恒为非负
    /// </summary>
    /// <param name="value">被取模数</param>
    /// <param name="divisor">正除数</param>
    /// <returns>0 到 divisor 减一之间的余数</returns>
    public static int FloorMod(long value, int divisor)
    {
        var remainder = value % divisor;
        return (int)(remainder < 0 ? remainder + divisor : remainder);
    }
}
