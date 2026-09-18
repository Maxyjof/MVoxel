using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 体素世界 CPU 等轴测缩略图渲染器：把世界投影为方形 RGBA 位图，
/// 供资源浏览器为体素世界资产生成预览图标
/// 纯确定性：同世界同参数输出逐字节一致，绘制顺序为 (X+Z) 升序、Y 升序的画家算法，
/// 顶面按原色、左面按 3/4、右面按 9/16 着色，背景透明
/// </summary>
public static class VoxelThumbnailRenderer
{
    /// <summary>
    /// 单次渲染允许的最大体素数，超出应由调用方改用程序化图标，避免长帧
    /// </summary>
    public const int MaximumVoxels = 400_000;

    /// <summary>
    /// 渲染等轴测缩略图
    /// </summary>
    /// <param name="world">要渲染的世界，必须非空</param>
    /// <param name="size">输出边长（像素），正方形</param>
    /// <returns>自上而下逐行排列的 RGBA8 像素，背景透明</returns>
    public static byte[] RenderRgba(VoxelWorld world, int size)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        var pixels = new byte[size * size * 4];
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            return pixels;
        }

        // 六边形半宽 a：同时容纳 X-Z 水平跨度与总高度（(X+Z) 每单位占半格、Y 每单位占一格），留 6% 边距
        var horizontalUnits = (double)((maximum.X - minimum.Z) - (minimum.X - maximum.Z) + 2);
        var verticalUnits = ((maximum.X + maximum.Z) - (minimum.X + minimum.Z) + 2) * 0.5 + (maximum.Y - minimum.Y + 1);
        var halfWidth = Math.Min(size * 0.94 / horizontalUnits, size * 0.94 / verticalUnits);
        if (halfWidth <= 0.5)
        {
            return pixels;
        }

        var originX = size / 2.0 - ((maximum.X - minimum.Z) + (minimum.X - maximum.Z)) * halfWidth / 2.0;
        var originY = size / 2.0
            - ((minimum.X + minimum.Z + maximum.X + maximum.Z) * 0.25 - (minimum.Y + maximum.Y) * 0.5) * halfWidth;
        var cells = world.EnumerateOccupied()
            .OrderBy(cell => cell.Coordinate.X + cell.Coordinate.Z)
            .ThenBy(cell => cell.Coordinate.Y)
            .ThenBy(cell => cell.Coordinate.X)
            .ToList();
        foreach (var (coordinate, value) in cells)
        {
            var centerX = originX + (coordinate.X - coordinate.Z) * halfWidth;
            var centerY = originY + (coordinate.X + coordinate.Z) * halfWidth / 2.0 - coordinate.Y * halfWidth;
            DrawHexagon(pixels, size, centerX, centerY, halfWidth, value.Color);
        }

        return pixels;
    }

    /// <summary>
    /// 把单个体素的六边形轮廓按三个可见面光栅化到位图
    /// </summary>
    private static void DrawHexagon(byte[] pixels, int size, double centerX, double centerY, double halfWidth, Rgba32 color)
    {
        var radius = (int)Math.Ceiling(halfWidth);
        var minX = Math.Max(0, (int)Math.Floor(centerX) - radius);
        var maxX = Math.Min(size - 1, (int)Math.Ceiling(centerX) + radius);
        var minY = Math.Max(0, (int)Math.Floor(centerY) - radius);
        var maxY = Math.Min(size - 1, (int)Math.Ceiling(centerY) + radius);
        for (var py = minY; py <= maxY; py++)
        {
            var dy = (py + 0.5 - centerY) / halfWidth;
            for (var px = minX; px <= maxX; px++)
            {
                var dx = (px + 0.5 - centerX) / halfWidth;
                var shade = ClassifyShade(dx, dy);
                if (shade == 0)
                {
                    continue;
                }

                var offset = (py * size + px) * 4;
                pixels[offset] = (byte)Math.Clamp(color.R * shade >> 8, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(color.G * shade >> 8, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(color.B * shade >> 8, 0, 255);
                pixels[offset + 3] = 255;
            }
        }
    }

    /// <summary>
    /// 按六边形几何判定像素属于哪个可见面，返回 8.8 定点亮度系数（0 表示轮廓外）
    /// 顶面 256、左面 192、右面 144
    /// </summary>
    private static int ClassifyShade(double dx, double dy)
    {
        if (dy <= -0.5 * Math.Abs(dx) && dy >= 0.5 * Math.Abs(dx) - 1)
        {
            return 256; // 顶面
        }

        if (dx >= 0 && dy >= -0.5 * dx && dy <= 1 - 0.5 * dx)
        {
            return 144; // 右面
        }

        if (dx <= 0 && dy >= 0.5 * dx && dy <= 1 + 0.5 * dx)
        {
            return 192; // 左面
        }

        return 0;
    }
}
