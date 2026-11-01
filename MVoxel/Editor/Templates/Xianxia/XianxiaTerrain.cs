using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 山体生成结果：六块分区体素世界与逐列山体高度，
/// 地标装配只从这里读取高程，不重复推算地形
/// </summary>
/// <param name="Worlds">按分区索引的体素世界</param>
/// <param name="Heights">逐列山体高度，云海列为 0</param>
internal readonly record struct XianxiaTerrainResult(
    IReadOnlyDictionary<XianxiaRegion, VoxelWorld> Worlds, int[] Heights);

/// <summary>
/// 仙峰山体：云海之上的孤峰、放射状山脊与崖壁、宗门平台、悬空瀑布与植被，
/// 全部由确定性噪声驱动，同一版本永远得到同一座山
/// </summary>
internal static class XianxiaTerrain
{
    private const int Size = XianxiaGeography.Size;
    private const int CloudLevel = XianxiaGeography.CloudLevel;

    /// <summary>山体</summary>
    private const byte BiomeMountain = 0;

    /// <summary>草坡与林地</summary>
    private const byte BiomeGrass = 1;

    /// <summary>崖壁裸岩</summary>
    private const byte BiomeRock = 2;

    /// <summary>台地铺装边缘</summary>
    private const byte BiomeTerrace = 3;

    private static readonly int[] Heights = new int[Size * Size];
    private static readonly byte[] Biomes = new byte[Size * Size];
    private static readonly float[] Slopes = new float[Size * Size];
    private static readonly uint[] SurfaceColors = new uint[Size * Size];
    private static readonly bool[] MountainColumns = new bool[Size * Size];

    /// <summary>
    /// 生成整座仙峰与环绕云海
    /// </summary>
    /// <returns>分区体素世界与逐列高度</returns>
    public static XianxiaTerrainResult Build()
    {
        BuildHeightField();
        ResolvePlateauLevels();
        ApplyFlatten();
        ComputeSlopes();
        PaintSurface();

        var worlds = new Dictionary<XianxiaRegion, VoxelWorld>();
        foreach (var region in Enum.GetValues<XianxiaRegion>())
        {
            worlds[region] = new VoxelWorld();
        }

        FillTerrain(worlds);
        CarveWaterfalls(worlds);
        AddMistBands(worlds);
        ScatterVegetation(worlds);
        return new XianxiaTerrainResult(worlds, Heights);
    }

    /// <summary>读取生成时的逐列山体高度</summary>
    /// <param name="x">世界 X</param>
    /// <param name="z">世界 Z</param>
    /// <returns>山体高度，云海列返回 0</returns>
    public static int HeightAt(int x, int z)
    {
        if (x < 0 || z < 0 || x >= Size || z >= Size) return 0;
        return Heights[z * Size + x];
    }

    private static void BuildHeightField()
    {
        Parallel.For(0, Size, z =>
        {
            for (var x = 0; x < Size; x++) Heights[z * Size + x] = ComputeHeight(x, z);
        });
    }

    private static int ComputeHeight(int x, int z)
    {
        var dx = x - XianxiaGeography.Center;
        var dz = z - XianxiaGeography.Center;
        var radius = MathF.Sqrt(dx * dx + dz * dz) / (XianxiaGeography.Size * .5F);
        var angle = MathF.Atan2(dz, dx);

        // 山脚轮廓：低频噪声让岩体边缘长出崖嘴与凹谷
        var edge = XianxiaGeography.MassifRadius
                   + (XianxiaNoise.Fbm(x, z, 101, 4, .0021F) - .5F) * .1F
                   + (XianxiaNoise.Fbm(x, z, 103, 2, .011F) - .5F) * .018F;
        if (radius >= edge) return 0;

        var t = 1F - radius / edge;
        var profile = MathF.Pow(t, 1.18F)
                      + .28F * MathF.Pow(1F - MathF.Min(1F, t * 3.4F), 1.5F);
        // 放射状沟壑：让山体出现一条条棱脊与冲沟，而不是光滑圆锥
        var rib = XianxiaNoise.Ridged(x, z, 409, 5, .0082F);
        var ribFine = XianxiaNoise.Ridged(x, z, 421, 3, .026F);
        var gully = .58F + .56F * rib + .16F * ribFine;
        // 山腰的横向缓坡带，供平台、林带与云带落位
        var swell = (XianxiaNoise.Fbm(x, z, 331, 3, .0043F) - .5F) * 26F * t;
        // 三处次峰，打破单峰的呆板轮廓
        var subPeak = 46F * XianxiaNoise.Falloff(Distance(x, z, 430, 428), 132F, 1.5F)
                      + 36F * XianxiaNoise.Falloff(Distance(x, z, 604, 566), 116F, 1.5F)
                      + 30F * XianxiaNoise.Falloff(Distance(x, z, 470, 610), 96F, 1.5F);

        var height = CloudLevel + 560F * profile * gully + swell + subPeak * profile;
        var rounded = (int)MathF.Round(height);
        return rounded <= CloudLevel + 2 ? 0 : rounded;
    }

    /// <summary>
    /// 解析每处地标的平台高程：取基址圈内原始山体的平均高度，
    /// 让平台与周围山势自然衔接，而不是悬在半空或陷成坑
    /// </summary>
    private static void ResolvePlateauLevels()
    {
        var levels = new int[XianxiaGeography.Sites.Length];
        for (var index = 0; index < levels.Length; index++)
        {
            var site = XianxiaGeography.Sites[index];
            var radius = (int)(site.Radius * .9F);
            long total = 0;
            var count = 0;
            for (var z = site.Z - radius; z <= site.Z + radius; z += 2)
            for (var x = site.X - radius; x <= site.X + radius; x += 2)
            {
                if (x < 0 || z < 0 || x >= Size || z >= Size) continue;
                var sample = Heights[z * Size + x];
                if (sample == 0) continue;
                total += sample;
                count++;
            }

            levels[index] = count == 0 ? site.Height : (int)(total / count);
        }

        XianxiaGeography.SetPlateauLevels(levels);
    }

    private static void ApplyFlatten()
    {
        var levels = new int[XianxiaGeography.Sites.Length];
        for (var index = 0; index < levels.Length; index++)
        {
            levels[index] = XianxiaGeography.SiteLevel(XianxiaGeography.Sites[index].Name);
        }

        Parallel.For(0, Size, z =>
        {
            for (var x = 0; x < Size; x++)
            {
                var index = z * Size + x;
                var original = Heights[index];
                var flattened = XianxiaGeography.Flatten(x, z, original == 0 ? CloudLevel : original, levels);
                Heights[index] = original == 0 && flattened <= CloudLevel + 3
                    ? 0 : Math.Max(flattened, CloudLevel + 3);
            }
        });
    }

    private static float Distance(int x, int z, int cx, int cz)
    {
        var dx = x - cx;
        var dz = z - cz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static void ComputeSlopes()
    {
        Parallel.For(0, Size, z =>
        {
            var row = z * Size;
            for (var x = 0; x < Size; x++)
            {
                var index = row + x;
                if (Heights[index] == 0)
                {
                    Slopes[index] = 0F;
                    continue;
                }

                var left = x > 0 && Heights[index - 1] > 0 ? Heights[index - 1] : Heights[index];
                var right = x < Size - 1 && Heights[index + 1] > 0 ? Heights[index + 1] : Heights[index];
                var back = z > 0 && Heights[index - Size] > 0 ? Heights[index - Size] : Heights[index];
                var front = z < Size - 1 && Heights[index + Size] > 0 ? Heights[index + Size] : Heights[index];
                var gradientX = (right - left) * .5F;
                var gradientZ = (front - back) * .5F;
                Slopes[index] = MathF.Sqrt(gradientX * gradientX + gradientZ * gradientZ);
            }
        });
    }

    private static void PaintSurface()
    {
        Parallel.For(0, Size, z =>
        {
            for (var x = 0; x < Size; x++)
            {
                var index = z * Size + x;
                var height = Heights[index];
                if (height == 0)
                {
                    MountainColumns[index] = false;
                    continue;
                }

                MountainColumns[index] = true;
                var slope = Slopes[index];
                var slopeNoise = (XianxiaNoise.Fbm(x, z, 1607, 2, .03F) - .5F) * .9F;
                var outcrop = XianxiaNoise.Fbm(x, z, 1619, 2, .012F);
                var rocky = slope + slopeNoise > 1.15F || outcrop > .84F;
                Biomes[index] = rocky ? BiomeRock : BiomeGrass;
                SurfaceColors[index] = PackSurfaceColor(x, z, height, slope, rocky);
            }
        });
    }

    private static uint PackSurfaceColor(int x, int z, int height, float slope, bool rocky)
    {
        var patch = XianxiaNoise.Fbm(x, z, 907, 3, .011F);
        var accent = XianxiaNoise.Fbm(x, z, 911, 2, .05F);
        Rgba32 color;

        if (rocky)
        {
            color = XianxiaPalette.Blend(XianxiaPalette.Stone, XianxiaPalette.Ochre,
                XianxiaNoise.SmoothStep(.35F, .75F, patch));
            if (accent < .32F) color = XianxiaPalette.StoneDark;
            if (accent > .78F) color = XianxiaPalette.Basalt;
            // 崖壁青苔：靠近草坡的岩面挂一层薄苔
            if (slope < 2.4F && accent > .62F) color = XianxiaPalette.Blend(color, XianxiaPalette.Moss, .35F);
        }
        else
        {
            color = XianxiaPalette.Blend(XianxiaPalette.GrassLight, XianxiaPalette.Moss,
                XianxiaNoise.SmoothStep(.3F, .7F, patch));
            if (accent > .8F) color = XianxiaPalette.Bamboo;
            if (accent < .12F) color = XianxiaPalette.Earth;
            // 半山秋色林斑：橙红与金黄点缀，制造参考图那种季节层次
            var autumn = XianxiaNoise.Fbm(x, z, 1613, 2, .0065F);
            if (autumn > .62F && height is > 200 and < 430)
            {
                color = XianxiaPalette.Blend(color,
                    accent > .5F ? XianxiaPalette.AutumnGold : XianxiaPalette.MapleRed,
                    XianxiaNoise.SmoothStep(.62F, .78F, autumn) * .75F);
            }
        }

        // 高处的岩石与草坡统一偏冷，云雾里的山体更通透
        if (height > 420) color = XianxiaPalette.Blend(color, XianxiaPalette.MoonWhite, .12F);
        return (uint)(color.R << 24 | color.G << 16 | color.B << 8 | color.A);
    }

    private static void FillTerrain(Dictionary<XianxiaRegion, VoxelWorld> worlds)
    {
        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var index = z * Size + x;
                var height = Heights[index];
                if (!MountainColumns[index])
                {
                    FillCloudColumn(worlds[XianxiaRegion.CloudSea], x, z);
                    continue;
                }

                var region = XianxiaGeography.Classify(x, z, height);
                var world = worlds[region];
                var packed = SurfaceColors[index];
                var surface = new VoxelValue(new Rgba32((byte)(packed >> 24), (byte)(packed >> 16),
                    (byte)(packed >> 8), (byte)packed));

                // 地表两层加崖壁裙边：陡坡侧面自动补齐，平缓处只留薄壳
                // 裙边必须一直补到相邻列的地表，不能截断成固定格数：
                // 棱脊与平台落差超过截断值时，侧面会留下一条能看穿山体内部的缝，
                // 从山外能直接看到内壁和背后的分件
                var lowest = height - 1;
                if (x > 0 && MountainColumns[index - 1]) lowest = Math.Min(lowest, Heights[index - 1] - 1);
                if (x < Size - 1 && MountainColumns[index + 1]) lowest = Math.Min(lowest, Heights[index + 1] - 1);
                if (z > 0 && MountainColumns[index - Size]) lowest = Math.Min(lowest, Heights[index - Size] - 1);
                if (z < Size - 1 && MountainColumns[index + Size]) lowest = Math.Min(lowest, Heights[index + Size] - 1);

                world.Set(new VoxelCoord64(x, height, z), surface);
                for (var y = lowest; y < height; y++)
                {
                    // 草坡的侧面沿用草色，斜坡才不会露出一圈岩色条纹
                    if (height - y <= 2 && Biomes[index] == BiomeGrass)
                    {
                        world.Set(new VoxelCoord64(x, y, z), XianxiaPalette.Grain(
                            new Rgba32((byte)(packed >> 24), (byte)(packed >> 16),
                                (byte)(packed >> 8), (byte)packed), x, y, z, 813, .02F));
                        continue;
                    }

                    world.Set(new VoxelCoord64(x, y, z), StrataValue(x, y, z, height, Biomes[index]));
                }
            }
        }
    }

    private static void FillCloudColumn(VoxelWorld world, int x, int z)
    {
        // 云海以单层云面为主，局部隆起成团块；不挖空洞，避免看到世界外部
        var density = XianxiaNoise.Fbm(x, z, 1711, 4, .0042F);
        var billow = XianxiaNoise.Fbm(x, z, 1717, 3, .013F);
        var top = CloudLevel + 2 + (int)MathF.Round(density * 6F + billow * 7F);
        var bottom = top - 3 - (int)(billow * 14F) - (int)(density * 8F);
        for (var y = bottom; y <= top; y++)
        {
            // 底部逐渐变淡，云海往下堆成一片而不是悬空的薄板
            var fade = y >= top - 1 ? .58F : y <= bottom + 2 ? .5F : .9F;
            world.Set(new VoxelCoord64(x, y, z), XianxiaFlora.CloudBody(x, y, z, fade));
        }
    }

    private static VoxelValue StrataValue(int x, int y, int z, int height, byte biome)
    {
        var depth = height - y;
        var hash = XianxiaNoise.Hash01(x * 31 + y, z * 17 - y, 651);
        var band = TemplateKit.Hash(x, y / 4, z, 907) % 13;

        if (depth <= 2 && biome == BiomeGrass)
        {
            return XianxiaPalette.Grain(XianxiaPalette.Earth, x, y, z, 811, .02F);
        }

        // 崖壁灵脉：低频高光提示地脉走向，顺带在山体内部埋一些发光的洞窟
        if (depth > 6 && hash < .0045F) return XianxiaPalette.Glow(XianxiaPalette.SpiritCyan);
        if (depth > 12 && hash > .9992F) return XianxiaPalette.Glow(XianxiaPalette.LavaGlow);

        // 岩色用连续三维噪声混合，斜面上不会出现规则条纹
        var warm = XianxiaNoise.Fbm(x * .05F + y * .022F, z * .05F - y * .03F, 907, 3, 1F);
        var cool = XianxiaNoise.Fbm(x * .12F - y * .04F, z * .12F + y * .02F, 911, 2, 1F);
        var rock = XianxiaPalette.Blend(XianxiaPalette.Basalt, XianxiaPalette.Stone,
            XianxiaNoise.SmoothStep(.32F, .68F, warm));
        rock = XianxiaPalette.Blend(rock, XianxiaPalette.Ochre,
            XianxiaNoise.SmoothStep(.5F, .78F, cool) * .55F);
        if (cool < .28F) rock = XianxiaPalette.Blend(rock, XianxiaPalette.StoneDark, .35F);
        if (warm > .72F) rock = XianxiaPalette.Blend(rock, XianxiaPalette.Moss, .45F);
        return XianxiaPalette.Grain(rock, x, y, z, 877, .014F);
    }

    private static void CarveWaterfalls(Dictionary<XianxiaRegion, VoxelWorld> worlds)
    {
        var water = worlds[XianxiaRegion.CloudSea];
        foreach (var fall in XianxiaGeography.Waterfalls)
        {
            var steps = fall.Drop;
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (float)steps;
                var y = fall.TopY - step;
                if (y <= CloudLevel - 6) break;
                var x = (int)MathF.Round(fall.X + fall.DriftX * t * t);
                var z = (int)MathF.Round(fall.Z + fall.DriftZ * t * t);
                var width = 4.5F - t * 2.2F;
                var limit = (int)MathF.Ceiling(width);
                for (var ox = -limit; ox <= limit; ox++)
                for (var oz = -limit; oz <= limit; oz++)
                {
                    if (ox * ox + oz * oz > width * width) continue;
                    var wobble = TemplateKit.Hash(x + ox, y, z + oz, 2011) % 11 == 0 ? 1 : 0;
                    water.Set(new VoxelCoord64(x + ox, y + wobble, z + oz),
                        XianxiaFlora.WaterBody(x + ox, y, z + oz));
                }
            }

            // 落水处炸开的云雾与水沫
            var bottomY = Math.Max(CloudLevel - 4, fall.TopY - fall.Drop);
            var bottomX = (int)MathF.Round(fall.X + fall.DriftX);
            var bottomZ = (int)MathF.Round(fall.Z + fall.DriftZ);
            TemplateKit.Ellipsoid(water, bottomX, bottomY + 3, bottomZ, 11, 5, 11,
                XianxiaPalette.CloudMist(120), hollow: false);
        }
    }

    private static void AddMistBands(Dictionary<XianxiaRegion, VoxelWorld> worlds)
    {
        // 山腰云带：绕山一条条半透明雾带，拉开前后层次
        var clouds = worlds[XianxiaRegion.CloudSea];
        for (var z = 0; z < Size; z += 2)
        for (var x = 0; x < Size; x += 2)
        {
            var index = z * Size + x;
            if (!MountainColumns[index]) continue;
            var height = Heights[index];
            if (height < 200) continue;
            var band = XianxiaNoise.Fbm(x, z, 1901, 2, .0045F);
            if (band < .74F) continue;
            var bandY = 210 + (int)(XianxiaNoise.Fbm(x, z, 1907, 2, .0016F) * 150F);
            if (Math.Abs(height - bandY) > 3) continue;
            var radius = 2 + (int)(XianxiaNoise.Fbm(x, z, 1913, 2, .02F) * 3F);
            for (var ox = -radius; ox <= radius; ox++)
            for (var oz = -radius; oz <= radius; oz++)
            {
                if (ox * ox + oz * oz > radius * radius) continue;
                clouds.Set(new VoxelCoord64(x + ox, bandY, z + oz), XianxiaPalette.CloudMist(96));
            }
        }
    }

    private static void ScatterVegetation(Dictionary<XianxiaRegion, VoxelWorld> worlds)
    {
        for (var z = 3; z < Size - 3; z++)
        {
            for (var x = 3; x < Size - 3; x++)
            {
                var index = z * Size + x;
                if (!MountainColumns[index]) continue;
                if (InsideSite(x, z)) continue;
                var height = Heights[index];
                var slope = Slopes[index];
                if (slope > 1.45F) continue;

                var grove = XianxiaNoise.Fbm(x, z, 1601, 3, .0075F);
                var autumn = XianxiaNoise.Fbm(x, z, 1613, 2, .0065F);
                var density = (int)(64F - grove * 40F);
                if (density < 6) density = 6;
                if (TemplateKit.Hash(x, 0, z, 1307) % density != 0) continue;

                var roll = XianxiaNoise.Hash01(x, z, 1311);
                var region = XianxiaGeography.Classify(x, z, height);
                var world = worlds[region];
                if (height > 430)
                {
                    if (roll < .5F) XianxiaFlora.PineTree(world, x, height, z, 1423, 2);
                    else XianxiaFlora.CherryTree(world, x, height, z, 1433);
                    continue;
                }

                if (autumn > .6F)
                {
                    if (roll < .55F) XianxiaFlora.AutumnTree(world, x, height, z, 1621,
                        accentShift: autumn > .72F ? 1 : 0);
                    else XianxiaFlora.PineTree(world, x, height, z, 1423, 2);
                    continue;
                }

                if (roll < .3F) XianxiaFlora.BambooCluster(world, x, height, z, 1409);
                else if (roll < .7F) XianxiaFlora.PineTree(world, x, height, z, 1423, roll < .45F ? 2 : 1);
                else XianxiaFlora.BroadleafTree(world, x, height, z, 1451);
            }
        }

        ScatterGroundDetails(worlds);
    }

    private static void ScatterGroundDetails(Dictionary<XianxiaRegion, VoxelWorld> worlds)
    {
        for (var z = 1; z < Size - 1; z++)
        {
            for (var x = 1; x < Size - 1; x++)
            {
                var index = z * Size + x;
                if (!MountainColumns[index]) continue;
                if (Slopes[index] > 1.4F) continue;
                var height = Heights[index];
                if (TemplateKit.Hash(x, 0, z, 1701) % 149 > 2) continue;
                var world = worlds[XianxiaGeography.Classify(x, z, height)];
                XianxiaFlora.SmallFlora(world, x, height + 1, z, Biomes[index] == BiomeRock ? (byte)5 : (byte)2, 1707);
            }
        }
    }

    private static bool InsideSite(int x, int z)
    {
        foreach (var site in XianxiaGeography.Sites)
        {
            var dx = x - site.X;
            var dz = z - site.Z;
            if (dx * dx + dz * dz <= site.Radius * site.Radius) return true;
        }

        return false;
    }
}
