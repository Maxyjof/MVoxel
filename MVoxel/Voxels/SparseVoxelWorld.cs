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
/// 体素材质的基础色快照
/// 编辑器中的基础色必须从当前调色板选择，存储时保留 RGBA 值以保证项目可独立读取
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
/// 体素的存储值：调色板基础色快照、材质槽和标志位
/// </summary>
/// <param name="Color">RGBA 颜色</param>
/// <param name="MaterialSlot">材质槽索引，对应项目材质属性</param>
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
    private readonly Dictionary<int, VoxelValue> _voxels;

    public SparseVoxelChunk() => _voxels = new Dictionary<int, VoxelValue>();

    private SparseVoxelChunk(Dictionary<int, VoxelValue> voxels) => _voxels = voxels;

    internal SparseVoxelChunk Clone() => new(new Dictionary<int, VoxelValue>(_voxels));

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

public readonly record struct VoxelWorldSnapshot(
    int ContentVersion,
    long StructureStamp,
    IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Value)> Voxels);

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

    private readonly HashSet<ChunkCoord64> _dirtyChunks = new();

    /// <summary>
    /// 区块新增或回收时递增；内容修改不改变此值
    /// </summary>
    internal long StructureStamp { get; private set; }

    /// <summary>
    /// 是否存在等待增量同步的脏区块
    /// </summary>
    internal bool HasDirtyChunks => _dirtyChunks.Count > 0;

    /// <summary>
    /// 取出当前脏区块快照（不清空），同步成功后由调用方 MarkChunksSynced 归账；
    /// 取出之后发生的新编辑不在快照内，不会被误清
    /// </summary>
    /// <param name="chunks">脏区块坐标快照</param>
    /// <returns>存在脏区块时返回 true</returns>
    internal bool TryTakeDirty(out IReadOnlySet<ChunkCoord64> chunks)
    {
        if (_dirtyChunks.Count == 0)
        {
            chunks = EmptyChunkSet;
            return false;
        }

        chunks = new HashSet<ChunkCoord64>(_dirtyChunks);
        return true;
    }

    /// <summary>
    /// 增量同步成功后归账：把快照中的区块移出脏集
    /// </summary>
    /// <param name="chunks">此前 TryTakeDirty 返回的快照</param>
    internal void MarkChunksSynced(IReadOnlySet<ChunkCoord64> chunks)
    {
        foreach (var coordinate in chunks)
        {
            _dirtyChunks.Remove(coordinate);
        }
    }

    /// <summary>
    /// 把全部已加载区块标记为脏（增量路径的兜底入口）
    /// </summary>
    internal void MarkAllChunksDirty()
    {
        foreach (var coordinate in _chunks.Keys)
        {
            _dirtyChunks.Add(coordinate);
        }
    }

    private static readonly IReadOnlySet<ChunkCoord64> EmptyChunkSet = new HashSet<ChunkCoord64>();

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

    public VoxelWorldSnapshot CaptureSnapshot(int maxRetries = 2)
    {
        maxRetries = Math.Clamp(maxRetries, 0, 8);
        for (var attempt = 0; ; attempt++)
        {
            var contentVersion = ContentVersion;
            var structureStamp = StructureStamp;
            IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Value)> voxels;
            try
            {
                voxels = EnumerateOccupied().ToArray();
            }
            catch (InvalidOperationException) when (attempt < maxRetries)
            {
                continue;
            }
            if (contentVersion == ContentVersion && structureStamp == StructureStamp)
                return new VoxelWorldSnapshot(contentVersion, structureStamp, voxels);
            if (attempt >= maxRetries)
                throw new InvalidOperationException("体素世界在快照期间发生编辑，请稍后重试保存或导出。");
        }
    }

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
        // 逐区块先求局部包围盒再换算世界坐标：内层只剩整数比较，
        // 不再为每个体素构造 (坐标, 值) 元组并走一遍迭代器
        foreach (var (chunkCoordinate, chunk) in _chunks)
        {
            if (chunk.OccupiedCount == 0) continue;
            var localFound = false;
            int localMinX = 0, localMinY = 0, localMinZ = 0, localMaxX = 0, localMaxY = 0, localMaxZ = 0;
            foreach (var index in chunk.Snapshot.Keys)
            {
                var local = SparseVoxelChunk.FromIndex(index);
                if (!localFound)
                {
                    localMinX = localMaxX = local.X;
                    localMinY = localMaxY = local.Y;
                    localMinZ = localMaxZ = local.Z;
                    localFound = true;
                    continue;
                }

                if (local.X < localMinX) localMinX = local.X;
                else if (local.X > localMaxX) localMaxX = local.X;
                if (local.Y < localMinY) localMinY = local.Y;
                else if (local.Y > localMaxY) localMaxY = local.Y;
                if (local.Z < localMinZ) localMinZ = local.Z;
                else if (local.Z > localMaxZ) localMaxZ = local.Z;
            }

            if (!localFound) continue;
            var baseX = chunkCoordinate.X * ChunkSize;
            var baseY = chunkCoordinate.Y * ChunkSize;
            var baseZ = chunkCoordinate.Z * ChunkSize;
            var chunkMinX = baseX + localMinX;
            var chunkMinY = baseY + localMinY;
            var chunkMinZ = baseZ + localMinZ;
            var chunkMaxX = baseX + localMaxX;
            var chunkMaxY = baseY + localMaxY;
            var chunkMaxZ = baseZ + localMaxZ;
            if (!found)
            {
                (minX, minY, minZ) = (chunkMinX, chunkMinY, chunkMinZ);
                (maxX, maxY, maxZ) = (chunkMaxX, chunkMaxY, chunkMaxZ);
                found = true;
                continue;
            }

            if (chunkMinX < minX) minX = chunkMinX;
            if (chunkMinY < minY) minY = chunkMinY;
            if (chunkMinZ < minZ) minZ = chunkMinZ;
            if (chunkMaxX > maxX) maxX = chunkMaxX;
            if (chunkMaxY > maxY) maxY = chunkMaxY;
            if (chunkMaxZ > maxZ) maxZ = chunkMaxZ;
        }

        minimum = new VoxelCoord64(minX, minY, minZ);
        maximum = new VoxelCoord64(maxX, maxY, maxZ);
        return found;
    }

    /// <summary>
    /// 物化当前区块表快照，供序列化层按区块并行填充记录
    /// </summary>
    /// <returns>区块坐标与稀疏区块的列表</returns>
    internal List<(ChunkCoord64 Coordinate, SparseVoxelChunk Chunk)> SnapshotChunks()
    {
        var list = new List<(ChunkCoord64, SparseVoxelChunk)>(_chunks.Count);
        foreach (var (coordinate, chunk) in _chunks)
        {
            list.Add((coordinate, chunk));
        }

        return list;
    }

    /// <summary>
    /// 复制全部区块，副本和原世界可独立修改
    /// </summary>
    internal VoxelWorld Clone()
    {
        var copy = new VoxelWorld();
        foreach (var (coordinate, chunk) in _chunks)
            copy.AdoptChunk(coordinate, chunk.Clone());
        return copy;
    }

    /// <summary>
    /// 复制全部区块并整体平移指定偏移，副本和原世界可独立修改
    /// </summary>
    /// <remarks>
    /// 模型文件只保存局部坐标，同一份资产被多个场景对象引用时只需要解析一次，
    /// 其余对象按原点差平移复制，避免把同一份几十万体素的文件重复解码
    /// </remarks>
    /// <param name="offsetX">X 方向平移量</param>
    /// <param name="offsetY">Y 方向平移量</param>
    /// <param name="offsetZ">Z 方向平移量</param>
    /// <returns>平移后的新世界</returns>
    internal VoxelWorld CloneShifted(long offsetX, long offsetY, long offsetZ)
    {
        if (offsetX == 0 && offsetY == 0 && offsetZ == 0) return Clone();
        var copy = new VoxelWorld();
        var target = new Dictionary<ChunkCoord64, SparseVoxelChunk>(_chunks.Count);
        foreach (var (coordinate, chunk) in _chunks)
        {
            var baseX = coordinate.X * ChunkSize + offsetX;
            var baseY = coordinate.Y * ChunkSize + offsetY;
            var baseZ = coordinate.Z * ChunkSize + offsetZ;
            foreach (var (index, value) in chunk.Snapshot)
            {
                var local = SparseVoxelChunk.FromIndex(index);
                var worldX = baseX + local.X;
                var worldY = baseY + local.Y;
                var worldZ = baseZ + local.Z;
                var chunkCoordinate = new ChunkCoord64(
                    FloorDiv(worldX, ChunkSize), FloorDiv(worldY, ChunkSize), FloorDiv(worldZ, ChunkSize));
                if (!target.TryGetValue(chunkCoordinate, out var destination))
                {
                    destination = new SparseVoxelChunk();
                    target.Add(chunkCoordinate, destination);
                }

                destination.Set(new LocalCoord(
                    FloorMod(worldX, ChunkSize), FloorMod(worldY, ChunkSize), FloorMod(worldZ, ChunkSize)), value);
            }
        }

        foreach (var (coordinate, chunk) in target)
            copy.AdoptChunk(coordinate, chunk);
        return copy;
    }

    /// <summary>
    /// 收养一个外部构建好的稀疏区块，键必须尚未存在于世界中，
    /// 供导入器把并行解析的分组结果整体并入，避免逐体素全局锁合并
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    /// <param name="chunk">构建完成的稀疏区块</param>
    internal void AdoptChunk(ChunkCoord64 coordinate, SparseVoxelChunk chunk)
    {
        _chunks.Add(coordinate, chunk);
        ContentVersion += chunk.OccupiedCount;
        _dirtyChunks.Add(coordinate);
        StructureStamp++;
    }

    /// <summary>文件读取专用写入器；连续记录通常属于同一区块，避免每个体素重复查全局区块字典。</summary>
    internal BulkWriter CreateBulkWriter() => new(this);

    internal sealed class BulkWriter(VoxelWorld world)
    {
        private ChunkCoord64 _lastCoordinate;
        private SparseVoxelChunk? _lastChunk;

        internal void Set(VoxelCoord64 coordinate, VoxelValue value)
        {
            var (chunkCoordinate, localCoordinate) = Split(coordinate);
            if (_lastChunk is null || chunkCoordinate != _lastCoordinate)
            {
                if (!world._chunks.TryGetValue(chunkCoordinate, out _lastChunk))
                {
                    _lastChunk = new SparseVoxelChunk();
                    world._chunks.Add(chunkCoordinate, _lastChunk);
                    world.StructureStamp++;
                }

                _lastCoordinate = chunkCoordinate;
                world._dirtyChunks.Add(chunkCoordinate);
            }

            _lastChunk.Set(localCoordinate, value);
            world.ContentVersion++;
        }
    }

    /// <summary>
    /// 读取指定坐标的稀疏区块
    /// </summary>
    /// <param name="coordinate">区块坐标</param>
    /// <param name="chunk">命中时输出区块</param>
    /// <returns>区块存在且非空时返回 true</returns>
    internal bool TryGetChunk(ChunkCoord64 coordinate, out SparseVoxelChunk chunk)
    {
        if (_chunks.TryGetValue(coordinate, out var found) && found.OccupiedCount > 0)
        {
            chunk = found;
            return true;
        }

        chunk = null!;
        return false;
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
                _dirtyChunks.Add(chunkCoordinate);
                if (existing.IsEmpty)
                {
                    _chunks.Remove(chunkCoordinate);
                    StructureStamp++;
                }
            }

            return;
        }

        if (!_chunks.TryGetValue(chunkCoordinate, out var chunk))
        {
            chunk = new SparseVoxelChunk();
            _chunks.Add(chunkCoordinate, chunk);
            StructureStamp++;
        }

        chunk.Set(localCoordinate, value);
        ContentVersion++;
        _dirtyChunks.Add(chunkCoordinate);
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
        _dirtyChunks.Add(chunkCoordinate);
        if (chunk.IsEmpty)
        {
            _chunks.Remove(chunkCoordinate);
            StructureStamp++;
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
    /// 向下取整除法，保证负坐标正确映射到所属 Chunk
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
