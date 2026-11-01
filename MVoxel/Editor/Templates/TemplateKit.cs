using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates;

/// <summary>
/// 模板通用建造工具库：确定性噪声、曲线形状、树木与地表涂装，
/// 让所有示例模板共享同一套有机造型语言，摆脱方块堆叠感
/// </summary>
internal static class TemplateKit
{
    /// <summary>玻璃材质槽</summary>
    public const ushort SlotGlass = 1;

    /// <summary>水质材质槽</summary>
    public const ushort SlotWater = 2;

    /// <summary>金属材质槽</summary>
    public const ushort SlotMetal = 3;

    /// <summary>发光材质槽</summary>
    public const ushort SlotGlow = 4;

    /// <summary>默认场景的可乐材质槽</summary>
    public const ushort SlotCola = 5;

    /// <summary>
    /// 整数哈希，用于确定性抖动与散布
    /// </summary>
    public static int Hash(int x, int y, int z)
    {
        long value = x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        value ^= value >> 13;
        value *= 1274126177L;
        value ^= value >> 16;
        return (int)(value & 0x7FFFFFFF);
    }

    /// <summary>
    /// 分形布朗噪声，四个倍频，返回约 0 到 1
    /// </summary>
    public static float Fbm(float x, float y, int seed, int octaves = 4, float frequency = 0.08F)
    {
        var sum = 0F;
        var amplitude = 1F;
        var total = 0F;
        var fx = frequency;
        for (var octave = 0; octave < octaves; octave++)
        {
            sum += SampleSmooth(x * fx, y * fx, seed + octave * 977) * amplitude;
            total += amplitude;
            amplitude *= .5F;
            fx *= 2F;
        }

        return sum / total;

        static float SampleSmooth(float x, float y, int seed)
        {
            var xi = (int)MathF.Floor(x);
            var yi = (int)MathF.Floor(y);
            var xf = x - xi;
            var yf = y - yi;
            float Value(int cx, int cy) => Hash(cx, cy, seed) / (float)0x7FFFFFFF;
            var smoothX = xf * xf * (3F - 2F * xf);
            var smoothY = yf * yf * (3F - 2F * yf);
            var top = Value(xi, yi) * (1 - smoothX) + Value(xi + 1, yi) * smoothX;
            var bottom = Value(xi, yi + 1) * (1 - smoothX) + Value(xi + 1, yi + 1) * smoothX;
            return top * (1 - smoothY) + bottom * smoothY;
        }
    }

    /// <summary>
    /// 给自然表面加确定性明度抖动。规则建筑默认不抖色，调用方必须显式传入幅度。
    /// </summary>
    public static Rgba32 Jitter(Rgba32 color, int x, int y, int z, int seed, float amount = 0F)
    {
        var factor = 1F + (Hash(x, y, z, seed) / (float)0x7FFFFFFF * 2F - 1F) * amount;
        return new Rgba32(
            (byte)Math.Clamp((int)MathF.Round(color.R * factor), 1, 255),
            (byte)Math.Clamp((int)MathF.Round(color.G * factor), 1, 255),
            (byte)Math.Clamp((int)MathF.Round(color.B * factor), 1, 255),
            color.A);
    }

    public static int Hash(int x, int y, int z, int seed)
    {
        long value = x * 73856093L ^ y * 19349663L ^ z * 83492791L ^ seed * 2654435761L;
        value ^= value >> 13;
        value *= 1274126177L;
        value ^= value >> 16;
        return (int)(value & 0x7FFFFFFF);
    }

    /// <summary>
    /// 填充包含端点的长方体
    /// </summary>
    public static void Box(VoxelWorld world, long minX, long minY, long minZ,
        long maxX, long maxY, long maxZ, VoxelValue value, int jitterSeed = 0)
    {
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var cell = value;
            if (jitterSeed != 0)
            {
                cell = new VoxelValue(Jitter(value.Color, (int)x, (int)y, (int)z, jitterSeed),
                    value.MaterialSlot, value.Flags);
            }

            world.Set(new VoxelCoord64(x, y, z), cell);
        }
    }

    /// <summary>
    /// 只填充长方体外壳，内部留空
    /// </summary>
    public static void Shell(VoxelWorld world, long minX, long minY, long minZ,
        long maxX, long maxY, long maxZ, VoxelValue value, int jitterSeed = 0)
    {
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var onShell = x == minX || x == maxX || y == minY || y == maxY || z == minZ || z == maxZ;
            if (!onShell) continue;
            var cell = value;
            if (jitterSeed != 0)
            {
                cell = new VoxelValue(Jitter(value.Color, (int)x, (int)y, (int)z, jitterSeed),
                    value.MaterialSlot, value.Flags);
            }

            world.Set(new VoxelCoord64(x, y, z), cell);
        }
    }

    /// <summary>
    /// 填充椭球（半径可为 0 表示圆盘）
    /// </summary>
    public static void Ellipsoid(VoxelWorld world, double centerX, double centerY, double centerZ,
        double radiusX, double radiusY, double radiusZ, VoxelValue value, bool hollow = false)
    {
        var minX = (long)Math.Floor(centerX - radiusX); var maxX = (long)Math.Ceiling(centerX + radiusX);
        var minY = (long)Math.Floor(centerY - radiusY); var maxY = (long)Math.Ceiling(centerY + radiusY);
        var minZ = (long)Math.Floor(centerZ - radiusZ); var maxZ = (long)Math.Ceiling(centerZ + radiusZ);
        for (var z = minZ; z <= maxZ; z++)
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var distance = Math.Pow((x + .5 - centerX) / Math.Max(radiusX, .5), 2)
                + Math.Pow((y + .5 - centerY) / Math.Max(radiusY, .5), 2)
                + Math.Pow((z + .5 - centerZ) / Math.Max(radiusZ, .5), 2);
            if (distance > 1) continue;
            if (hollow && distance < Math.Pow(1 - 2 / Math.Max(Math.Max(radiusX, radiusY), Math.Max(radiusZ, 2)), 2)) continue;
            world.Set(new VoxelCoord64(x, y, z), value);
        }
    }

    /// <summary>
    /// 圆柱或椭圆柱，radiusY 为高度方向的半径（半高）
    /// </summary>
    public static void Cylinder(VoxelWorld world, double centerX, double centerY, double centerZ,
        double radiusX, double radiusZ, double halfHeight, VoxelValue value, bool hollow = false)
    {
        for (var y = (long)Math.Floor(centerY - halfHeight); y <= Math.Ceiling(centerY + halfHeight); y++)
        for (var z = (long)Math.Floor(centerZ - radiusZ); z <= Math.Ceiling(centerZ + radiusZ); z++)
        for (var x = (long)Math.Floor(centerX - radiusX); x <= Math.Ceiling(centerX + radiusX); x++)
        {
            var distance = Math.Pow((x + .5 - centerX) / Math.Max(radiusX, .5), 2)
                + Math.Pow((z + .5 - centerZ) / Math.Max(radiusZ, .5), 2);
            if (distance > 1 || distance < 1 - .35 && hollow) continue;
            world.Set(new VoxelCoord64(x, y, z), value);
        }
    }

    /// <summary>
    /// 圆台，从底面半径到顶面半径线性收分
    /// </summary>
    public static void Taper(VoxelWorld world, double centerX, double centerZ, long bottomY, long topY,
        double bottomRadius, double topRadius, VoxelValue value, int jitterSeed = 0)
    {
        var span = Math.Max(1, topY - bottomY);
        for (var y = bottomY; y <= topY; y++)
        {
            var t = (y - bottomY) / (double)span;
            var radius = bottomRadius + (topRadius - bottomRadius) * t;
            for (var z = (long)Math.Floor(centerZ - radius); z <= Math.Ceiling(centerZ + radius); z++)
            for (var x = (long)Math.Floor(centerX - radius); x <= Math.Ceiling(centerX + radius); x++)
            {
                var distance = Math.Pow((x + .5 - centerX) / Math.Max(radius, .4), 2)
                    + Math.Pow((z + .5 - centerZ) / Math.Max(radius, .4), 2);
                if (distance > 1) continue;
                var cell = value;
                if (jitterSeed != 0)
                {
                    cell = new VoxelValue(Jitter(value.Color, (int)x, (int)y, (int)z, jitterSeed),
                        value.MaterialSlot, value.Flags);
                }

                world.Set(new VoxelCoord64(x, y, z), cell);
            }
        }
    }

    /// <summary>
    /// 双坡屋顶：从屋檐到屋脊逐层收分，overhang 控制出檐
    /// </summary>
    public static void GableRoof(VoxelWorld world, long minX, long minZ, long maxX, long maxZ,
        long baseY, long ridgeRise, VoxelValue value, VoxelValue? ridgeCap = null)
    {
        var halfDepth = (maxZ - minZ + 1) / 2.0;
        for (var layer = 0L; layer <= ridgeRise; layer++)
        {
            var t = ridgeRise == 0 ? 1 : layer / (double)ridgeRise;
            var zInset = (long)Math.Round(halfDepth * t);
            var y = baseY + layer;
            Box(world, minX - 1, y, minZ + zInset, maxX + 1, y, minZ + zInset, value);
            Box(world, minX - 1, y, maxZ - zInset, maxX + 1, y, maxZ - zInset, value);
            if (zInset * 2 >= maxZ - minZ)
            {
                Box(world, minX - 1, y, minZ + zInset, maxX + 1, y, maxZ - zInset, ridgeCap ?? value);
                break;
            }
        }
    }

    /// <summary>
    /// 圆拱：在 XZ 平面沿 Y 拉伸的半圆拱洞边界（用于拱门与柱廊顶）
    /// </summary>
    public static void Arch(VoxelWorld world, long centerX, long minZ, long maxZ, long springY,
        long radius, int thickness, VoxelValue value)
    {
        for (var z = minZ; z <= maxZ; z++)
        for (var y = springY - radius; y <= springY + radius; y++)
        {
            for (var offset = 0; offset < thickness; offset++)
            {
                var r = radius - offset;
                if (r <= 0) continue;
                var dy = y + .5 - springY;
                var dz = z + .5 - (minZ + maxZ + 1) / 2.0;
                var distance = Math.Sqrt(dy * dy + dz * dz);
                if (Math.Abs(distance - r) < .5 && dy >= -0.05)
                {
                    world.Set(new VoxelCoord64(centerX, y, z), value);
                }
            }
        }
    }

    /// <summary>
    /// 三点采样平滑曲线在 XY 平面内拉伸到 Z 深度（用于飘带与道路）
    /// </summary>
    public static void Line(VoxelWorld world, (long X, long Y, long Z) from, (long X, long Y, long Z) to,
        VoxelValue value, int thickness = 1)
    {
        var steps = Math.Max(Math.Abs(to.X - from.X), Math.Max(Math.Abs(to.Y - from.Y), Math.Abs(to.Z - from.Z)));
        for (var step = 0; step <= steps; step++)
        {
            var t = steps == 0 ? 0 : step / (double)steps;
            var x = (long)Math.Round(from.X + (to.X - from.X) * t);
            var y = (long)Math.Round(from.Y + (to.Y - from.Y) * t);
            var z = (long)Math.Round(from.Z + (to.Z - from.Z) * t);
            Box(world, x - thickness, y, z - thickness, x + thickness, y, z + thickness, value);
        }
    }

    /// <summary>
    /// 阔叶树：分叉树干 + 三个重叠冠球，返回是否成功落在地面上
    /// </summary>
    public static bool BroadleafTree(VoxelWorld world, int centerX, int groundY, int centerZ,
        int seed, VoxelValue bark, VoxelValue leaf, int scale = 1)
    {
        var trunkHeight = (4 + Hash(centerX, groundY, centerZ, seed) % 3) * scale;
        Taper(world, centerX + .5, centerZ + .5, groundY, groundY + trunkHeight,
            .9 * scale, .55 * scale, bark);
        var canopyY = groundY + trunkHeight + scale;
        Ellipsoid(world, centerX + .5, canopyY, centerZ + .5, 2.6 * scale, 1.9 * scale, 2.6 * scale, leaf);
        Ellipsoid(world, centerX + 1.6 * scale, canopyY - .4 * scale, centerZ + .3, 1.9 * scale, 1.5 * scale, 1.9 * scale, leaf);
        Ellipsoid(world, centerX - 1.4 * scale, canopyY - .3 * scale, centerZ - 1.2 * scale, 1.8 * scale, 1.4 * scale, 1.8 * scale, leaf);
        return true;
    }

    /// <summary>
    /// 针叶树：树干 + 三层收缩锥冠
    /// </summary>
    public static void ConiferTree(VoxelWorld world, int centerX, int groundY, int centerZ,
        VoxelValue bark, VoxelValue leaf, int scale = 1)
    {
        var trunkHeight = 6 * scale;
        Taper(world, centerX + .5, centerZ + .5, groundY, groundY + trunkHeight, .6 * scale, .35 * scale, bark);
        for (var layer = 0; layer < 3; layer++)
        {
            var radius = (3.2 - layer * .9) * scale;
            var baseY = groundY + 1 + layer * (int)(2.2 * scale);
            Taper(world, centerX + .5, centerZ + .5, baseY, baseY + (int)(3.4 * scale) - layer, radius, .15 * scale, leaf);
        }
    }

    /// <summary>
    /// 灌木球
    /// </summary>
    public static void Bush(VoxelWorld world, int centerX, int groundY, int centerZ, VoxelValue leaf, double radius = 1.4)
    {
        Ellipsoid(world, centerX + .5, groundY + radius * .7, centerZ + .5, radius, radius * .8, radius, leaf);
    }

    /// <summary>
    /// 草地散布小花
    /// </summary>
    public static void ScatterFlowers(VoxelWorld world, Func<int, int, bool> canPlace,
        int minX, int maxX, int minZ, int maxZ, int seed, params VoxelValue[] petals)
    {
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var roll = Hash(x, 0, z, seed) % 97;
            if (roll > 6 || !canPlace(x, z)) continue;
            var petal = petals[roll % petals.Length];
            var groundY = FindSurfaceY(world, x, z);
            if (groundY < int.MinValue) continue;
            world.Set(new VoxelCoord64(x, groundY + 1, z), petal);
            if (roll < 2)
            {
                world.Set(new VoxelCoord64(x + 1, groundY + 1, z), petals[(roll + 1) % petals.Length]);
            }
        }
    }

    /// <summary>
    /// 从高空向下找该列最上方实心体素的 y，找不到返回 int.MinValue
    /// </summary>
    public static int FindSurfaceY(VoxelWorld world, int x, int z)
    {
        for (var y = 512; y >= -64; y--)
        {
            if (world.TryGet(new VoxelCoord64(x, y, z), out _)) return y;
        }

        return int.MinValue;
    }

    /// <summary>
    /// 带高度查询的地形柱：从 y=0 填到 height，按分层材质着色并加抖动
    /// </summary>
    public static void TerrainColumn(VoxelWorld world, int x, int z, int height,
        Func<int, int, int, VoxelValue> layerSelector, int jitterSeed)
    {
        for (var y = 0; y < height; y++)
        {
            var value = layerSelector(x, y, z);
            world.Set(new VoxelCoord64(x, y, z), new VoxelValue(
                Jitter(value.Color, x, y, z, jitterSeed, .04F), value.MaterialSlot, value.Flags));
        }
    }
}
