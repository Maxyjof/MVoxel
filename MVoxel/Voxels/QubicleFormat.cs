using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// Qubicle .qb 交换模型（多矩阵、RGBA/BGRA、原始与 RLE 体素数据）的导入导出。
/// 项目内部资源使用 .mvmodel，不通过本类保存。
/// 外部文件一律不可信：矩阵尺寸与体素总数在分配内存前先做上限校验，
/// 截断、超大或越界的文件只会得到异常，绝不会拖垮编辑器
/// 坐标映射：Qubicle 为 Y 轴向上，与项目世界同向，world 轴一一对应（含矩阵偏移）
/// </summary>
public static class QubicleFormat
{
    /// <summary>
    /// 单文件允许的最大体素总数，先校验后分配
    /// </summary>
    public const int MaximumVoxels = 4_000_000;

    /// <summary>
    /// 单个矩阵单轴允许的最大格数
    /// </summary>
    public const int MaximumAxisExtent = 1024;

    /// <summary>
    /// 写入文件使用的格式版本号（1.1）
    /// </summary>
    private const uint Version = 257;

    /// <summary>
    /// RLE 数据里的游程标记：其后两个 u32 为重复次数与颜色
    /// </summary>
    private const uint RunCode = 2;

    /// <summary>
    /// RLE 数据里的下一片层标记
    /// </summary>
    private const uint NextSliceCode = 6;

    /// <summary>
    /// 把体素世界导出为外部 .qb 文件，世界按包围盒重锚到原点。
    /// 输出单矩阵、RGBA 通道序、左手法线、未压缩、二进制可见掩码
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">要导出的世界，必须非空</param>
    /// <param name="matrixName">导出矩阵名，默认 Voxel</param>
    public static void Write(string path, VoxelWorld world, string matrixName = "Voxel")
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            throw new InvalidOperationException("Cannot export an empty voxel world.");
        }

        var sizeX = (int)(maximum.X - minimum.X) + 1;
        var sizeY = (int)(maximum.Y - minimum.Y) + 1;
        var sizeZ = (int)(maximum.Z - minimum.Z) + 1;
        if (sizeX > MaximumAxisExtent || sizeY > MaximumAxisExtent || sizeZ > MaximumAxisExtent)
        {
            throw new InvalidOperationException("Voxel model exceeds the single-file .qb extent limit.");
        }

        var name = System.Text.Encoding.UTF8.GetBytes(matrixName);
        if (name.Length == 0 || name.Length > byte.MaxValue)
        {
            throw new InvalidOperationException("Matrix name must be 1 to 255 bytes.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Version);
        writer.Write(0u); // colorFormat：RGBA
        writer.Write(0u); // zAxisOrientation：左手
        writer.Write(0u); // compressed：原始数据
        writer.Write(0u); // visibilityMaskEncoded：体素级可见掩码
        writer.Write(1u); // 矩阵数量
        writer.Write((byte)name.Length);
        writer.Write(name);
        writer.Write(sizeX);
        writer.Write(sizeY);
        writer.Write(sizeZ);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        // 磁盘顺序为 Z、Y、X（X 最内层），可见字节非零表示实体，置满 255 表示六面可见
        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    if (world.TryGet(minimum + new VoxelCoord64(x, y, z), out var value))
                    {
                        writer.Write(value.Color.R);
                        writer.Write(value.Color.G);
                        writer.Write(value.Color.B);
                        writer.Write((byte)255);
                    }
                    else
                    {
                        writer.Write(0u);
                    }
                }
            }
        }

        writer.Flush();
        AtomicFile.WriteBytes(path, stream.ToArray());
    }

    /// <summary>
    /// 从磁盘读取外部 .qb 文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>包含全部矩阵体素的新世界</returns>
    public static VoxelWorld Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes, VoxelCoord64.Zero);
    }

    /// <summary>
    /// 从字节解析 .qb 文件，非法结构一律抛 InvalidDataException
    /// </summary>
    /// <param name="bytes">完整文件字节</param>
    /// <param name="origin">导入摆放的世界原点</param>
    /// <returns>包含全部矩阵体素的新世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (bytes.Length < 24)
        {
            throw new InvalidDataException("Not a Qubicle .qb file.");
        }

        reader.ReadUInt32(); // 版本号，任何版本都接受
        var colorFormat = reader.ReadUInt32(); // 0=RGBA，1=BGRA
        reader.ReadUInt32(); // Z 轴朝向，不影响体素存储
        var compressed = reader.ReadUInt32() != 0;
        reader.ReadUInt32(); // 可见掩码编码方式，非零可见性字节均视为实体
        var matrixCount = reader.ReadUInt32();
        if (colorFormat > 1 || matrixCount > 4096)
        {
            throw new InvalidDataException("Corrupt .qb header.");
        }

        // 矩阵位置可以为负，先把整体平移量算出来再写入世界
        var matrices = new List<(VoxelCoord64 Offset, VoxelCoord64 Size, long Start)>(checked((int)matrixCount));
        var world = new VoxelWorld();
        for (var matrixIndex = 0u; matrixIndex < matrixCount; matrixIndex++)
        {
            var nameLength = reader.ReadByte();
            if (nameLength > (ulong)(bytes.Length - stream.Position))
            {
                throw new InvalidDataException(".qb matrix name extends beyond the file.");
            }

            stream.Position += nameLength;
            var sizeX = reader.ReadUInt32();
            var sizeY = reader.ReadUInt32();
            var sizeZ = reader.ReadUInt32();
            if (sizeX is 0 or > MaximumAxisExtent ||
                sizeY is 0 or > MaximumAxisExtent ||
                sizeZ is 0 or > MaximumAxisExtent ||
                sizeX * sizeY * sizeZ > (ulong)MaximumVoxels)
            {
                throw new InvalidDataException(".qb matrix size exceeds supported bounds.");
            }

            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var offsetZ = reader.ReadInt32();
            var start = stream.Position;
            var dataSize = compressed ? -1L : (long)(sizeX * sizeY * sizeZ) * 4;
            if (!compressed && start + dataSize > bytes.Length)
            {
                throw new InvalidDataException(".qb matrix voxel data extends beyond the file.");
            }

            matrices.Add((new VoxelCoord64(offsetX, offsetY, offsetZ),
                new VoxelCoord64(sizeX, sizeY, sizeZ), start));
        }

        VoxelCoord64 minimum = default, maximum = default;
        if (matrices.Count > 0)
        {
            minimum = maximum = matrices[0].Offset;
            foreach (var (offset, size, _) in matrices)
            {
                minimum = new VoxelCoord64(
                    Math.Min(minimum.X, offset.X), Math.Min(minimum.Y, offset.Y), Math.Min(minimum.Z, offset.Z));
                maximum = new VoxelCoord64(
                    Math.Max(maximum.X, offset.X + size.X),
                    Math.Max(maximum.Y, offset.Y + size.Y),
                    Math.Max(maximum.Z, offset.Z + size.Z));
            }
        }

        var shift = VoxelCoord64.Zero - minimum;

        foreach (var (offset, size, start) in matrices)
        {
            stream.Position = start;
            if (compressed)
            {
                ReadCompressedMatrix(reader, bytes, size, colorFormat, origin + shift + offset, world);
            }
            else
            {
                ReadRawMatrix(reader, size, colorFormat, origin + shift + offset, world);
            }
        }

        return world;
    }

    /// <summary>
    /// 读取一个矩阵的原始体素数据，逐格写入世界
    /// </summary>
    private static void ReadRawMatrix(BinaryReader reader, VoxelCoord64 size, uint colorFormat,
        VoxelCoord64 origin, VoxelWorld world)
    {
        for (var z = 0L; z < size.Z; z++)
        {
            for (var y = 0L; y < size.Y; y++)
            {
                for (var x = 0L; x < size.X; x++)
                {
                    WriteVoxel(reader, colorFormat, origin + new VoxelCoord64(x, y, z), world);
                }
            }
        }
    }

    /// <summary>
    /// 读取一个矩阵的 RLE 体素数据：按 Z 片层解码，
    /// 码 2 为游程（重复次数 + 颜色），码 6 表示片层结束，
    /// 其余值直接作为单格颜色，片层数据不足时剩余格保持为空
    /// </summary>
    private static void ReadCompressedMatrix(BinaryReader reader, byte[] bytes, VoxelCoord64 size, uint colorFormat,
        VoxelCoord64 origin, VoxelWorld world)
    {
        var sliceLength = size.X * size.Y;
        for (var z = 0L; z < size.Z; z++)
        {
            var filled = 0L;
            while (true)
            {
                if (reader.BaseStream.Position + 4 > bytes.Length)
                {
                    throw new InvalidDataException(".qb RLE data extends beyond the file.");
                }

                var code = reader.ReadUInt32();
                if (code == NextSliceCode)
                {
                    break;
                }

                var remaining = sliceLength - filled;
                if (code == RunCode)
                {
                    if (reader.BaseStream.Position + 8 > bytes.Length)
                    {
                        throw new InvalidDataException(".qb RLE run extends beyond the file.");
                    }

                    var run = reader.ReadUInt32();
                    var color = reader.ReadUInt32();
                    if (run > (ulong)remaining)
                    {
                        throw new InvalidDataException(".qb RLE run overruns its slice.");
                    }

                    for (var index = 0L; index < (long)run; index++)
                    {
                        WriteVoxel(color, colorFormat, origin + SliceCoord(filled, size, z), world);
                        filled++;
                    }
                }
                else
                {
                    if (remaining <= 0)
                    {
                        throw new InvalidDataException(".qb RLE slice holds more than its cells.");
                    }

                    WriteVoxel(code, colorFormat, origin + SliceCoord(filled, size, z), world);
                    filled++;
                }
            }
        }
    }

    /// <summary>
    /// 把片层内序号换算为片层内的 X、Y 坐标（X 最内层），Z 由片层循环提供
    /// </summary>
    private static VoxelCoord64 SliceCoord(long index, VoxelCoord64 size, long z)
    {
        var x = index % size.X;
        var y = index / size.X;
        return new VoxelCoord64(x, y, z);
    }

    /// <summary>
    /// 从读取器取四字节并按通道序解码，可见性字节非零才写入世界
    /// </summary>
    private static void WriteVoxel(BinaryReader reader, uint colorFormat, VoxelCoord64 coordinate, VoxelWorld world)
    {
        WriteVoxel(reader.ReadUInt32(), colorFormat, coordinate, world);
    }

    /// <summary>
    /// 把存储的 u32 颜色按通道序解码，可见性字节非零才写入世界
    /// </summary>
    private static void WriteVoxel(uint packed, uint colorFormat, VoxelCoord64 coordinate, VoxelWorld world)
    {
        var bytes = BitConverter.GetBytes(packed);
        var visibility = bytes[3];
        if (visibility == 0)
        {
            return;
        }

        var (r, g, b) = colorFormat == 1 ? (bytes[2], bytes[1], bytes[0]) : (bytes[0], bytes[1], bytes[2]);
        world.Set(coordinate, new VoxelValue(new Rgba32(r, g, b, 255)));
    }
}
