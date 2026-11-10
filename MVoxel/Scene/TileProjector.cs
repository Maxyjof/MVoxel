using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 一次瓷砖铺设的统计，检查器与自动化命令共用
/// </summary>
/// <param name="TileCount">实际落下的瓷砖块数</param>
/// <param name="CellCount">写入的体素格数</param>
/// <param name="ColumnCount">盒体内有效墙面列数</param>
/// <param name="SkippedColumns">没有找到墙或被共面容差过滤掉的列数</param>
/// <param name="Truncated">是否因预算截断</param>
/// <param name="Message">失败或降级原因，空字符串表示正常</param>
public readonly record struct TileLayerStats(
    int TileCount, int CellCount, int ColumnCount, int SkippedColumns, bool Truncated, string Message)
{
    /// <summary>正常结果的空统计</summary>
    public static TileLayerStats Empty => new(0, 0, 0, 0, false, string.Empty);
}

/// <summary>
/// 瓷砖贴合计算：把盒体、朝向与体素源变成一组真实体素
/// </summary>
/// <remarks>
/// 算法分三步：先把盒体换算到朝向自己的 U/V/W 坐标，再沿 W 方向逐列找墙，
/// 最后按槽位把瓷砖图案贴到每列的墙面深度上。
/// 所有随机都由固定种子计算，重复生成结果逐格一致
/// </remarks>
public static class TileProjector
{
    /// <summary>单次铺设允许扫描的最大列数，避免误把盒体拖成整张地图后卡死界面</summary>
    public const long MaximumColumnCount = 2_000_000;

    /// <summary>
    /// 按组件参数生成瓷砖体素，坐标是对象局部坐标
    /// </summary>
    /// <param name="component">组件记录，用于内嵌源与指纹</param>
    /// <param name="data">瓷砖参数</param>
    /// <param name="source">已解析的体素源</param>
    /// <param name="context">生成上下文，缺少场景采样时返回空结果</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <returns>生成结果</returns>
    public static TileLayerBuildResult Build(ComponentRecord component, TileLayerComponentData data,
        VoxelSource? source, GeneratedVoxelContext? context, int maximumVoxels)
    {
        var world = new VoxelWorld();
        if (maximumVoxels <= 0)
            return new TileLayerBuildResult(world, new TileLayerStats(0, 0, 0, 0, true,
                "体素数量上限必须大于 0"));
        if (source is null || !source.IsValid)
            return new TileLayerBuildResult(world, new TileLayerStats(0, 0, 0, 0, false,
                string.IsNullOrWhiteSpace(source?.Error) ? "未指定体素源" : source.Error));
        if (context?.Surface is null)
            return new TileLayerBuildResult(world, new TileLayerStats(0, 0, 0, 0, false, "缺少场景采样，无法贴着墙生成"));
        if (source.Size.X > 8192 || source.Size.Y > 8192 || source.Size.Z > 8192)
            return new TileLayerBuildResult(world, new TileLayerStats(0, 0, 0, 0, false,
                $"体素源尺寸 {source.Size.X}×{source.Size.Y}×{source.Size.Z} 过大，请换一块更小的瓷砖"));

        var normalized = data.Normalize();
        var frame = TileFrame.Create(normalized.Facing);
        var origin = context.ObjectOrigin;
        if (!TileSpaceTransform.TryCreate(context, out var transform))
            return new TileLayerBuildResult(world, TileLayerStats.Empty with { Message = "对象旋转或缩放无效，无法生成瓷砖" });
        var boxMin = normalized.Origin;
        var boxMax = boxMin + normalized.Size - new VoxelCoord64(1, 1, 1);
        frame.ComputeRanges(boxMin, boxMax, out var uMin, out var uMax, out var vMin, out var vMax,
            out var wMin, out var wMax);
        var uCount = uMax - uMin + 1;
        var vCount = vMax - vMin + 1;
        var wCount = wMax - wMin + 1;
        if (uCount <= 0 || vCount <= 0 || wCount <= 0)
            return new TileLayerBuildResult(world, TileLayerStats.Empty with { Message = "盒体范围为空" });
        if (uCount > MaximumColumnCount || vCount > MaximumColumnCount / uCount)
            return new TileLayerBuildResult(world, TileLayerStats.Empty with
            {
                Message = $"盒体横截面积超过上限 {MaximumColumnCount:N0} 列，请缩小长宽",
            });
        var columnCount = uCount * vCount;

        // 先把每列的墙面深度算出来，后面瓷砖只查这张表，不重复扫描
        var depthCount = checked((int)columnCount);
        var depths = new long[depthCount];
        var valid = new bool[depthCount];
        var maxSteps = (int)Math.Min(wCount, normalized.MaxDepth);
        var validColumns = 0;
        var validDepthList = new List<long>(depthCount / 4 + 1);
        for (var v = 0L; v < vCount; v++)
        for (var u = 0L; u < uCount; u++)
        {
            var column = frame.ToWorld(uMin + u, vMin + v, wMax);
            if (!transform.TryHit(context.Surface, column, frame.NegativeW, maxSteps, out var hit)) continue;
            var depth = frame.DepthOf(hit);
            var index = checked((int)(u + v * uCount));
            depths[index] = depth;
            valid[index] = true;
            validColumns++;
            validDepthList.Add(depth);
        }

        var skippedColumns = depthCount - validColumns;
        if (validDepthList.Count == 0)
            return new TileLayerBuildResult(world, new TileLayerStats(0, 0, 0, depthCount, false,
                "盒体内没有找到朝向内的墙面，请检查朝向与盒体位置"));
        // 共面容差：和这面墙的中位深度差太多的列视为门窗或远处背景，不铺
        validDepthList.Sort();
        var median = validDepthList[validDepthList.Count / 2];
        if (normalized.SurfaceTolerance > 0)
        {
            var filtered = 0;
            for (var index = 0; index < depthCount; index++)
            {
                if (!valid[index] || Math.Abs(depths[index] - median) <= normalized.SurfaceTolerance) continue;
                valid[index] = false;
                filtered++;
            }

            if (filtered > 0) skippedColumns += filtered;
        }

        var offsetX = (int)Math.Clamp(normalized.SourceOffset.X, 0, Math.Max(0, source.Size.X - 1));
        var offsetY = (int)Math.Clamp(normalized.SourceOffset.Y, 0, Math.Max(0, source.Size.Y - 1));
        var offsetZ = (int)Math.Clamp(normalized.SourceOffset.Z, 0, Math.Max(0, source.Size.Z - 1));
        var usableWidth = source.Size.X - offsetX;
        var usableHeight = source.Size.Y - offsetY;
        var variantWidth = (int)(usableWidth / normalized.VariantCount);
        var variantCount = normalized.VariantCount;
        if (variantWidth <= 0)
        {
            variantWidth = (int)usableWidth;
            variantCount = 1;
        }
        else if (usableWidth % normalized.VariantCount != 0)
        {
            variantCount = 1;
            variantWidth = (int)usableWidth;
        }

        var tileU = Math.Max(1, normalized.TileWidth > 0 ? normalized.TileWidth : variantWidth);
        var tileV = Math.Max(1, normalized.TileHeight > 0 ? normalized.TileHeight : (int)usableHeight);
        var quarterTurns = normalized.Rotation != TileRotationMode.None;
        var slotSizeU = quarterTurns ? Math.Max(tileU, tileV) : tileU;
        var slotSizeV = quarterTurns ? Math.Max(tileU, tileV) : tileV;
        var stepU = slotSizeU + normalized.Gap;
        var stepV = slotSizeV + normalized.Gap;
        var centerU = (slotSizeU - tileU) / 2;
        var centerV = (slotSizeV - tileV) / 2;

        // 源体素只枚举一次，槽位里按图案索引取
        var sourceCells = new List<(int X, int Y, int Z, VoxelValue Value)>();
        foreach (var (coordinate, value) in source.World.EnumerateOccupied())
            sourceCells.Add(((int)coordinate.X, (int)coordinate.Y, (int)coordinate.Z, value));

        var writer = world.CreateBulkWriter();
        var occupied = new HashSet<VoxelCoord64>();
        var cellCount = 0;
        var tileCount = 0;
        var truncated = false;
        var seed = (ulong)normalized.Seed;
        for (var slotV0 = 0L; slotV0 + 1 <= vCount && !truncated; slotV0 += stepV)
        for (var slotU0 = 0L; slotU0 + 1 <= uCount; slotU0 += stepU)
        {
            var row = slotV0 / stepV;
            var columnIndex = slotU0 / stepU;
            var stagger = normalized.Stagger && row % 2 == 1 ? tileU / 2 : 0;
            var jitterU = normalized.JitterU > 0
                ? DeterministicRandom.Range(seed, columnIndex, row, 11, -normalized.JitterU, normalized.JitterU + 1)
                : 0;
            var jitterV = normalized.JitterV > 0
                ? DeterministicRandom.Range(seed, columnIndex, row, 12, -normalized.JitterV, normalized.JitterV + 1)
                : 0;
            var variant = DeterministicRandom.Range(seed, columnIndex, row, 13, 0, variantCount);
            var turns = normalized.Rotation switch
            {
                TileRotationMode.QuarterRandom => DeterministicRandom.Range(seed, columnIndex, row, 14, 0, 4),
                TileRotationMode.RowAlternate => (int)((columnIndex + row) & 1),
                _ => 0,
            };

            var slotU = uMin + slotU0 + stagger + jitterU;
            var slotV = vMin + slotV0 + jitterV;

            // 整块模式先定一个共同深度；逐列模式每列用自己的深度
            var blockDepth = long.MinValue;
            if (normalized.Fit != TileFitMode.PerColumn)
            {
                var has = false;
                for (var dv = 0; dv < tileV; dv++)
                for (var du = 0; du < tileU; du++)
                {
                    var u = slotU + centerU + du;
                    var v = slotV + centerV + dv;
                    if (!TryIndex(u, v, uMin, vMin, uCount, vCount, out var index) || !valid[index]) continue;
                    blockDepth = has
                        ? normalized.Fit == TileFitMode.BlockFront
                            ? Math.Max(blockDepth, depths[index])
                            : Math.Min(blockDepth, depths[index])
                        : depths[index];
                    has = true;
                }

                if (!has) continue;
            }

            var tileOutOfBounds = false;
            if (normalized.Edge == TileEdgeMode.SkipTile)
            {
                for (var dv = 0; dv < tileV && !tileOutOfBounds; dv++)
                for (var du = 0; du < tileU; du++)
                {
                    if (TryIndex(slotU + centerU + du, slotV + centerV + dv,
                            uMin, vMin, uCount, vCount, out _)) continue;
                    tileOutOfBounds = true;
                    break;
                }

                if (tileOutOfBounds) continue;
            }

            var placedSomething = false;
            var variantStart = variant * variantWidth;
            foreach (var (sx, sy, sz, value) in sourceCells)
            {
                if (sx < offsetX + variantStart || sx >= offsetX + variantStart + variantWidth) continue;
                if (sy < offsetY) continue;
                // 源沿厚度方向从源起点开始只取 tileDepth 层，薄贴片默认只取一层
                var depthIndex = sz - offsetZ;
                if (depthIndex < 0 || depthIndex >= normalized.TileDepth) continue;
                var localX = sx - offsetX - variantStart;
                var localY = sy - offsetY;
                // 旋转只作用在瓷砖自身的裁剪区域上，不跟随整个源的尺寸
                var (du, dv) = turns switch
                {
                    1 => (tileV - 1 - localY, localX),
                    2 => (tileU - 1 - localX, tileV - 1 - localY),
                    3 => (localY, tileU - 1 - localX),
                    _ => (localX, localY),
                };
                var u = slotU + centerU + du;
                var v = slotV + centerV + dv;
                // 源比槽位大时按槽位裁剪，避免写到相邻瓷砖上
                if (du < 0 || dv < 0 || du >= tileU || dv >= tileV) continue;
                if (!TryIndex(u, v, uMin, vMin, uCount, vCount, out var index)) continue;
                if (!valid[index]) continue;
                var columnDepth = normalized.Fit == TileFitMode.PerColumn ? depths[index] : blockDepth;
                var w = columnDepth + 1 + normalized.Offset + depthIndex;
                var localCell = frame.ToWorld(u, v, w);
                foreach (var transformed in transform.RasterizeCell(localCell))
                {
                    var resultCell = transformed - origin;
                    if (!occupied.Contains(resultCell) && occupied.Count >= maximumVoxels)
                    {
                        truncated = true;
                        break;
                    }
                    writer.Set(resultCell, value);
                    occupied.Add(resultCell);
                    placedSomething = true;
                }
                cellCount = occupied.Count;
                if (truncated) break;
            }

            if (placedSomething) tileCount++;
            if (truncated) break;
        }

        return new TileLayerBuildResult(world, new TileLayerStats(tileCount, cellCount, validColumns, skippedColumns,
            truncated, truncated ? $"超过 {maximumVoxels:N0} 格上限，结果已截断" : string.Empty));

        // 槽位在盒体里的列索引；超出盒体返回 false
        static bool TryIndex(long u, long v, long uMin, long vMin, long uCount, long vCount, out int index)
        {
            index = 0;
            if (u < uMin || v < vMin) return false;
            var localU = u - uMin;
            var localV = v - vMin;
            if (localU >= uCount || localV >= vCount) return false;
            index = checked((int)(localU + localV * uCount));
            return true;
        }
    }

    /// <summary>
    /// 朝向对应的 U/V/W 三轴
    /// </summary>
    /// <remarks>
    /// U/V/W 保持右手关系（U 叉乘 V 等于 W），这样瓷砖图案不会被镜像。
    /// 竖直墙的 V 固定向上，地板与天花板的 V 沿 Z，方便贴地砖
    /// </remarks>
    private readonly record struct TileFrame(VoxelCoord64 U, VoxelCoord64 V, VoxelCoord64 W)
    {
        public VoxelCoord64 NegativeW => new(-W.X, -W.Y, -W.Z);

        public static TileFrame Create(TileFacing facing) => facing switch
        {
            TileFacing.PosX => new(new VoxelCoord64(0, 0, -1), new VoxelCoord64(0, 1, 0), new VoxelCoord64(1, 0, 0)),
            TileFacing.NegX => new(new VoxelCoord64(0, 0, 1), new VoxelCoord64(0, 1, 0), new VoxelCoord64(-1, 0, 0)),
            TileFacing.PosY => new(new VoxelCoord64(1, 0, 0), new VoxelCoord64(0, 0, -1), new VoxelCoord64(0, 1, 0)),
            TileFacing.NegY => new(new VoxelCoord64(1, 0, 0), new VoxelCoord64(0, 0, 1), new VoxelCoord64(0, -1, 0)),
            TileFacing.PosZ => new(new VoxelCoord64(1, 0, 0), new VoxelCoord64(0, 1, 0), new VoxelCoord64(0, 0, 1)),
            _ => new(new VoxelCoord64(-1, 0, 0), new VoxelCoord64(0, 1, 0), new VoxelCoord64(0, 0, -1)),
        };

        /// <summary>把朝向坐标换算成世界坐标</summary>
        public VoxelCoord64 ToWorld(long u, long v, long w) => new(
            U.X * u + V.X * v + W.X * w,
            U.Y * u + V.Y * v + W.Y * w,
            U.Z * u + V.Z * v + W.Z * w);

        /// <summary>世界坐标在朝向坐标里的深度（W 分量）</summary>
        public long DepthOf(VoxelCoord64 world) => world.X * W.X + world.Y * W.Y + world.Z * W.Z;

        /// <summary>
        /// 把世界盒体的八个角换算成朝向坐标范围
        /// </summary>
        public void ComputeRanges(VoxelCoord64 minimum, VoxelCoord64 maximum,
            out long uMin, out long uMax, out long vMin, out long vMax, out long wMin, out long wMax)
        {
            uMin = long.MaxValue; uMax = long.MinValue;
            vMin = long.MaxValue; vMax = long.MinValue;
            wMin = long.MaxValue; wMax = long.MinValue;
            for (var corner = 0; corner < 8; corner++)
            {
                var x = (corner & 1) == 0 ? minimum.X : maximum.X;
                var y = (corner & 2) == 0 ? minimum.Y : maximum.Y;
                var z = (corner & 4) == 0 ? minimum.Z : maximum.Z;
                var u = x * U.X + y * U.Y + z * U.Z;
                var v = x * V.X + y * V.Y + z * V.Z;
                var w = x * W.X + y * W.Y + z * W.Z;
                uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
                wMin = Math.Min(wMin, w); wMax = Math.Max(wMax, w);
            }
        }
    }
}

/// <summary>
/// 瓷砖生成的返回：体素世界与统计
/// </summary>
/// <param name="World">局部坐标的体素世界</param>
/// <param name="Stats">铺设统计</param>
public readonly record struct TileLayerBuildResult(VoxelWorld World, TileLayerStats Stats);
