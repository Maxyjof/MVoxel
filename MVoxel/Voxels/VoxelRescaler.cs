using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 体素模型的最近邻倍数重采样
/// 放大时每个源体素扩成实心块，缩小时按输出格中心抽样，颜色、材质槽和标志位原样保留
/// </summary>
public static class VoxelRescaler
{
    /// <summary>
    /// 进度回调的最小间隔，按已处理的输出格数计数
    /// </summary>
    private const long ProgressInterval = 1 << 16;

    /// <summary>
    /// 双精度包围盒允许的最大绝对值，留出取整余量避免转回 long 时溢出
    /// </summary>
    private const double MaximumBoundMagnitude = 9.2e18;

    /// <summary>
    /// 按倍数对源世界做最近邻重采样，返回新的稀疏世界
    /// </summary>
    /// <param name="source">源世界，调用后保持不变</param>
    /// <param name="factor">缩放倍数，大于 1 放大，小于 1 缩小</param>
    /// <param name="pivot">缩放中心，通常取源包围盒最小角</param>
    /// <param name="newOrigin">输出世界包围盒最小角，结果为空时等于 pivot</param>
    /// <param name="cancellation">取消标记</param>
    /// <param name="progress">进度回调，参数是已写入的输出体素数</param>
    /// <param name="maximumOutputVoxels">输出包围盒的体素体积上限，防止误填大倍数耗尽内存</param>
    /// <returns>重采样后的新世界</returns>
    /// <exception cref="ArgumentOutOfRangeException">factor 不是有限值或小于等于零</exception>
    /// <exception cref="InvalidOperationException">缩放后的包围盒越过坐标范围或体积超出预算</exception>
    public static VoxelWorld Scale(
        VoxelWorld source,
        float factor,
        VoxelCoord64 pivot,
        out VoxelCoord64 newOrigin,
        CancellationToken cancellation = default,
        Action<long>? progress = null,
        long maximumOutputVoxels = 50_000_000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(factor) || factor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "缩放倍数必须是大于零的有限数值");
        }

        cancellation.ThrowIfCancellationRequested();
        if (!source.TryGetBounds(out var sourceMinimum, out var sourceMaximum))
        {
            newOrigin = pivot;
            return new VoxelWorld();
        }

        //连续缩放范围：下界向下取整，上界按格子的外沿向上取整，保证缩小后仍完整覆盖源模型
        var lowerX = ScaleContinuous(sourceMinimum.X, pivot.X, factor);
        var lowerY = ScaleContinuous(sourceMinimum.Y, pivot.Y, factor);
        var lowerZ = ScaleContinuous(sourceMinimum.Z, pivot.Z, factor);
        var upperX = ScaleContinuous((double)sourceMaximum.X + 1.0, pivot.X, factor);
        var upperY = ScaleContinuous((double)sourceMaximum.Y + 1.0, pivot.Y, factor);
        var upperZ = ScaleContinuous((double)sourceMaximum.Z + 1.0, pivot.Z, factor);
        var outputMinimum = new VoxelCoord64(
            FloorBound(lowerX),
            FloorBound(lowerY),
            FloorBound(lowerZ));
        var outputMaximum = new VoxelCoord64(
            InclusiveUpperBound(upperX),
            InclusiveUpperBound(upperY),
            InclusiveUpperBound(upperZ));

        //极端缩小可能把某一轴压到零格，至少保留一格
        outputMaximum = new VoxelCoord64(
            Math.Max(outputMaximum.X, outputMinimum.X),
            Math.Max(outputMaximum.Y, outputMinimum.Y),
            Math.Max(outputMaximum.Z, outputMinimum.Z));

        var sizeX = AxisSize(outputMaximum.X, outputMinimum.X, 'X');
        var sizeY = AxisSize(outputMaximum.Y, outputMinimum.Y, 'Y');
        var sizeZ = AxisSize(outputMaximum.Z, outputMinimum.Z, 'Z');
        var volume = (double)sizeX * sizeY * sizeZ;
        if (volume > maximumOutputVoxels)
        {
            throw new InvalidOperationException(
                $"缩放后输出包围盒为 {sizeX}x{sizeY}x{sizeZ}，超过体素预算 {maximumOutputVoxels}，请降低缩放倍数");
        }

        var inverse = 1.0 / factor;
        var destination = new VoxelWorld();
        var writer = destination.CreateBulkWriter();

        //源区块缓存：连续输出格大多落在同一区块，避免每格重复查全局区块字典
        var cachedCoordinate = default(ChunkCoord64);
        SparseVoxelChunk? cachedChunk = null;
        var hasCache = false;

        var processed = 0L;
        var written = 0L;
        var minimumX = 0L;
        var minimumY = 0L;
        var minimumZ = 0L;

        for (var z = outputMinimum.Z; z <= outputMaximum.Z; z++)
        {
            var sourceZ = MapAxis(z, pivot.Z, inverse, sourceMinimum.Z, sourceMaximum.Z);
            for (var y = outputMinimum.Y; y <= outputMaximum.Y; y++)
            {
                var sourceY = MapAxis(y, pivot.Y, inverse, sourceMinimum.Y, sourceMaximum.Y);
                for (var x = outputMinimum.X; x <= outputMaximum.X; x++)
                {
                    processed++;
                    if (processed % ProgressInterval == 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress?.Invoke(written);
                    }

                    var sourceX = MapAxis(x, pivot.X, inverse, sourceMinimum.X, sourceMaximum.X);
                    var sourceCoordinate = new VoxelCoord64(sourceX, sourceY, sourceZ);
                    var (chunkCoordinate, localCoordinate) = VoxelWorld.Split(sourceCoordinate);
                    if (!hasCache || chunkCoordinate != cachedCoordinate)
                    {
                        cachedCoordinate = chunkCoordinate;
                        hasCache = true;
                        cachedChunk = source.TryGetChunk(chunkCoordinate, out var chunk) ? chunk : null;
                    }

                    if (cachedChunk is null || !cachedChunk.TryGet(localCoordinate, out var value))
                    {
                        continue;
                    }

                    writer.Set(new VoxelCoord64(x, y, z), value);
                    if (written == 0)
                    {
                        minimumX = x;
                        minimumY = y;
                        minimumZ = z;
                    }
                    else
                    {
                        minimumX = Math.Min(minimumX, x);
                        minimumY = Math.Min(minimumY, y);
                        minimumZ = Math.Min(minimumZ, z);
                    }

                    written++;
                }
            }
        }

        cancellation.ThrowIfCancellationRequested();
        progress?.Invoke(written);
        newOrigin = written > 0
            ? new VoxelCoord64(minimumX, minimumY, minimumZ)
            : outputMinimum;
        return destination;
    }

    /// <summary>
    /// 把整数坐标映射到以 pivot 为中心的连续缩放坐标
    /// </summary>
    /// <param name="coordinate">待缩放的整数坐标</param>
    /// <param name="pivot">缩放中心</param>
    /// <param name="factor">缩放倍数</param>
    /// <returns>连续缩放坐标</returns>
    private static double ScaleContinuous(double coordinate, long pivot, double factor) =>
        (double)pivot + (coordinate - (double)pivot) * factor;

    /// <summary>
    /// 下界取向下取整，转换前校验数值范围
    /// </summary>
    /// <param name="value">连续下界</param>
    /// <returns>输出格的最小索引</returns>
    private static long FloorBound(double value)
    {
        ValidateBound(value);
        return (long)Math.Floor(value);
    }

    /// <summary>
    /// 上界是开区间，转换为包含式最大格索引并校验数值范围
    /// </summary>
    /// <param name="exclusiveUpper">连续开区间上界</param>
    /// <returns>输出格的最大索引</returns>
    private static long InclusiveUpperBound(double exclusiveUpper)
    {
        ValidateBound(exclusiveUpper);
        return (long)Math.Ceiling(exclusiveUpper) - 1;
    }

    /// <summary>
    /// 校验连续包围盒仍在坐标可表示范围内
    /// </summary>
    /// <param name="value">待校验的连续坐标</param>
    private static void ValidateBound(double value)
    {
        if (!double.IsFinite(value) || value < -MaximumBoundMagnitude || value > MaximumBoundMagnitude)
        {
            throw new InvalidOperationException("缩放后的包围盒超出坐标表示范围，请减小缩放倍数");
        }
    }

    /// <summary>
    /// 计算单轴输出格数，溢出时转为中文业务异常
    /// </summary>
    /// <param name="maximum">包含式最大格坐标</param>
    /// <param name="minimum">包含式最小格坐标</param>
    /// <param name="axis">轴名，用于异常消息</param>
    /// <returns>该轴格数</returns>
    private static long AxisSize(long maximum, long minimum, char axis)
    {
        try
        {
            return checked(maximum - minimum + 1);
        }
        catch (OverflowException error)
        {
            throw new InvalidOperationException($"缩放后的包围盒在 {axis} 轴超出坐标表示范围", error);
        }
    }

    /// <summary>
    /// 把输出格反算成源体素坐标：先求输出格在源空间覆盖的连续区间，
    /// 与源包围盒求交后取区间中点并向下取整
    /// 区间完整落在包围盒内时中点就是格中心，缩小到一格时中点就是包围盒中心
    /// </summary>
    /// <param name="output">输出格坐标</param>
    /// <param name="pivot">缩放中心</param>
    /// <param name="inverse">缩放倍数的倒数</param>
    /// <param name="minimum">源包围盒最小坐标</param>
    /// <param name="maximum">源包围盒最大坐标</param>
    /// <returns>源体素坐标</returns>
    private static long MapAxis(long output, long pivot, double inverse, long minimum, long maximum)
    {
        //输出格对应的源连续区间，源模型放大时区间变窄，缩小时变宽
        var start = (double)pivot + ((double)output - (double)pivot) * inverse;
        var end = start + inverse;
        var low = Math.Max(start, (double)minimum);
        var high = Math.Min(end, (double)maximum + 1.0);
        if (high <= low)
        {
            return start < minimum ? minimum : maximum;
        }

        var sample = Math.Floor((low + high) * 0.5);
        if (!double.IsFinite(sample))
        {
            return minimum;
        }

        if (sample <= minimum)
        {
            return minimum;
        }

        if (sample >= maximum)
        {
            return maximum;
        }

        return (long)sample;
    }
}
