using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 古迹与禁地：剑冢、锁妖塔、传送大阵与古战场，
/// 是大陆叙事里年代最久远的一层，
/// 全部构件以 y=0 地面为基座并在水平方向居中于 (0,0)
/// </summary>
internal static class XianxiaLandmarks
{
    // 剑冢崖谷与崖顶外缘半径
    private const int TombRadius = 46;
    private const int TombRimOuter = 58;

    // 锁妖塔层数、层高与塔基高程
    private const int PagodaFloors = 13;
    private const int PagodaFloorStep = 9;
    private const int PagodaBaseY = 5;
    private const int PagodaPlinthY = 4;

    // 古战场焦土坑的位置与半径
    private static readonly (int X, int Z, int Radius)[] Craters =
        [(18, -22, 9), (-28, 24, 11), (30, 26, 7), (-6, -34, 6)];

    // 剑冢
    /// <summary>剑冢：万剑插地的环形崖谷，中央巨剑与剑台镇住满谷剑灵</summary>
    public static VoxelWorld BuildSwordTomb()
    {
        var world = new VoxelWorld();
        BuildTombFloor(world);
        BuildTombRim(world);
        BuildTombStairs(world);
        BuildSwordAltar(world);
        BuildGiantSword(world);
        BuildScatteredSwords(world);
        BuildTombRelics(world);
        return world;
    }
    /// <summary>剑冢谷底：玄岩铺地，边缘泛苔并浮起薄薄一层浮尘</summary>
    private static void BuildTombFloor(VoxelWorld world)
    {
        for (var z = -TombRadius; z <= TombRadius; z++)
        for (var x = -TombRadius; x <= TombRadius; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius > TombRadius) continue;
            var dust = XianxiaNoise.Fbm(x * .9F, z * .9F, 4101, 3, .05F);
            var color = XianxiaPalette.Blend(XianxiaPalette.BasaltDeep, XianxiaPalette.StoneDark, dust);
            if (radius > 36F) color = XianxiaPalette.Blend(color, XianxiaPalette.Moss, .32F);
            Put(world, x, 0, z, XianxiaPalette.Grain(color, x, 0, z, 4103, .08F));
        }
    }
    /// <summary>剑冢环形崖谷：内侧陡壁、崖顶与外侧缓坡</summary>
    private static void BuildTombRim(VoxelWorld world)
    {
        for (var z = -TombRimOuter; z <= TombRimOuter; z++)
        for (var x = -TombRimOuter; x <= TombRimOuter; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius <= TombRadius || radius > TombRimOuter || IsTombStairLane(x, z)) continue;
            var crest = 9F + XianxiaNoise.Fbm(x * 1.5F, z * 1.5F, 4201, 3, .06F) * 5F;
            var top = radius <= 50F ? 4F + (radius - TombRadius) * (crest - 4F) / 4F
                : radius <= 54F ? crest : crest * (1F - XianxiaNoise.SmoothStep(54F, TombRimOuter, radius));
            var topY = (int)MathF.Round(top);
            for (var y = 0; y <= topY; y++)
                Put(world, x, y, z, XianxiaPalette.Grain((topY - y) switch
                {
                    0 => XianxiaPalette.Blend(XianxiaPalette.Ochre, XianxiaPalette.Moss,
                        XianxiaNoise.Hash01(x, z, 4203) > .5F ? .42F : .04F),
                    1 => XianxiaPalette.Ochre,
                    _ => y % 4 == 0 ? XianxiaPalette.BasaltDeep : XianxiaPalette.Basalt,
                }, x, y, z, 4207, .07F));
        }
    }
    /// <summary>四条登崖石阶：谷底升上崖顶，再向外降到平原，两侧带残破矮墙</summary>
    private static void BuildTombStairs(VoxelWorld world)
    {
        var body = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
        var cap = XianxiaPalette.Grain(XianxiaPalette.Stone, 0, 0, 0, 4211, .02F);
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        for (var distance = 40; distance <= 60; distance++)
        for (var offset = -4; offset <= 4; offset++)
        {
            var curb = Math.Abs(offset) == 4;
            var x = dx == 0 ? offset : dx * distance;
            var z = dz == 0 ? offset : dz * distance;
            if (curb && Chance(x, distance, z, 4213, .22F)) continue;
            var topY = TombStairTop(distance) + (curb ? 1 : 0);
            for (var y = 0; y <= topY; y++) Put(world, x, y, z, y == topY ? cap : body);
        }
    }

    private static int TombStairTop(int distance) => distance <= 52
        ? (int)MathF.Round((distance - 40) * 9F / 12F)
        : Math.Max(0, 9 - (distance - 52));

    private static bool IsTombStairLane(int x, int z) =>
        (Math.Abs(z) <= 3 && Math.Abs(x) >= 38) || (Math.Abs(x) <= 3 && Math.Abs(z) >= 38);
    /// <summary>三层八边形剑台，台面嵌煞红、灵蓝与灵青三圈阵纹</summary>
    private static void BuildSwordAltar(VoxelWorld world)
    {
        for (var y = 0; y <= 5; y++)
        {
            var radius = y < 2 ? 17 : y < 4 ? 13 : 9;
            var color = y % 2 == 0 ? XianxiaPalette.StoneDark : XianxiaPalette.Stone;
            for (var z = -radius; z <= radius; z++)
            for (var x = -radius; x <= radius; x++)
                if (x * x + z * z <= radius * radius)
                    Put(world, x, y, z, XianxiaPalette.Grain(color, x, y, z, 4301, .05F));
        }
        RuneCircle(world, 1, 15.2D, XianxiaPalette.Glow(XianxiaPalette.BloodRed));
        RuneCircle(world, 3, 11.4D, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
        RuneCircle(world, 5, 7.2D, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        for (var spoke = 0; spoke < 8; spoke++)
        {
            var angle = spoke * MathF.Tau / 8F + .2F;
            for (var radius = spoke % 2 == 0 ? 4.2F : 5.4F; radius <= 7.2F; radius += .9F)
                Put(world, Coord(angle, radius), 5, Coord(angle + MathF.PI / 2F, radius),
                    XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        }
    }
    /// <summary>中央巨剑：剑尖入石、剑身向上收分、金格赤柄与剑首灵珠</summary>
    private static void BuildGiantSword(VoxelWorld world)
    {
        const int tipY = 4;
        const int guardY = 35;
        for (var y = tipY; y < guardY; y++)
        {
            var t = (y - tipY) / (float)(guardY - tipY - 1);
            var half = 1F + t * 2.7F;
            var depth = .55F + t * 1.45F;
            for (var dz = -(int)MathF.Ceiling(depth); dz <= (int)MathF.Ceiling(depth); dz++)
            for (var dx = -(int)MathF.Ceiling(half); dx <= (int)MathF.Ceiling(half); dx++)
            {
                if (dx * dx / (half * half) + dz * dz / (depth * depth) > 1F) continue;
                var color = dx == 0 || dz == 0 ? XianxiaPalette.MoonWhite : XianxiaPalette.Silver;
                Put(world, dx, y, dz, XianxiaPalette.Metal(color));
                if (y % 9 == 0 && dx == 0 && dz == 0)
                    Put(world, dx, y, dz, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
            }
        }
        for (var y = guardY; y <= guardY + 2; y++)
        for (var dx = -7; dx <= 7; dx++)
        {
            var guard = Math.Abs(dx) == 7 ? XianxiaPalette.Copper : XianxiaPalette.Gold;
            Put(world, dx, y, 0, XianxiaPalette.Metal(guard));
            if (y == guardY) Put(world, dx, y, 1, XianxiaPalette.Metal(guard));
        }
        for (var y = guardY + 3; y <= guardY + 9; y++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
            Put(world, dx, y, dz, (y + dz) % 4 == 0
                ? XianxiaPalette.Cloth(XianxiaPalette.BloodRed)
                : XianxiaPalette.Metal(XianxiaPalette.BasaltDeep));
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
            Put(world, dx, guardY + 10, dz, XianxiaPalette.Jade(XianxiaPalette.JadeGreen));
        Put(world, 0, guardY + 11, 0, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
    }
    /// <summary>万剑插地：按疏密节奏散布大小不一的长剑</summary>
    private static void BuildScatteredSwords(VoxelWorld world)
    {
        for (var z = -44; z <= 44; z++)
        for (var x = -44; x <= 44; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius < 18F || radius > 44.5F || IsTombStairLane(x, z)) continue;
            if (!Chance(x, 0, z, 4327, SwordDensity(radius, x, z))) continue;
            var height = 4 + TemplateKit.Hash(x, 0, z, 4331) % (radius < 25F ? 14 : 11);
            PlantSword(world, x, z, height, 4343, Chance(x, 0, z, 4337, .07F));
        }
    }
    /// <summary>剑冢插剑密度：近谷心密、中段稀、外圈成簇</summary>
    private static float SwordDensity(float radius, int x, int z) =>
        (radius < 24F ? .17F : radius < 32F ? .035F : .09F)
        * (.3F + XianxiaNoise.Fbm(x * 1.6F, z * 1.6F, 4349, 3, .05F) * 1.5F);
    /// <summary>插一柄剑：剑身、剑格、剑柄、剑首分色，少数剑身发灵光</summary>
    private static void PlantSword(VoxelWorld world, int x, int z, int height, int seed, bool spirit)
    {
        var lean = TemplateKit.Hash(x, 0, z, seed) % 5 - 2;
        for (var level = 0; level < height; level++)
        {
            var offset = lean * level / Math.Max(height, 1);
            var color = level % 3 == 1 ? XianxiaPalette.MoonWhite : XianxiaPalette.Silver;
            if (level == height - 1) color = XianxiaPalette.StoneDark;
            Put(world, x + offset, level, z, XianxiaPalette.Metal(color));
            if (height >= 9 && level < height / 2)
                Put(world, x + offset, level, z + 1, XianxiaPalette.Metal(XianxiaPalette.StoneDark));
            if (spirit && level >= 2 && level % 4 == 2)
                Put(world, x + offset, level, z, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        }
        var tipX = x + lean;
        var guard = XianxiaPalette.Metal(Math.Abs(x * z) % 3 == 0 ? XianxiaPalette.Copper : XianxiaPalette.Gold);
        var alongX = (x + z) % 2 == 0;
        for (var offset = -2; offset <= 2; offset++)
        {
            if (alongX) Put(world, tipX + offset, height, z, guard);
            else Put(world, tipX, height, z + offset, guard);
        }
        if (alongX) Put(world, tipX, height, z + 1, guard);
        var grip = Math.Abs(x + 2 * z) % 3 == 0
            ? XianxiaPalette.Cloth(XianxiaPalette.BloodRed)
            : XianxiaPalette.Metal(XianxiaPalette.BasaltDeep);
        for (var level = 1; level <= 3; level++) Put(world, tipX, height + level, z, grip);
        if (height >= 7) Put(world, tipX, height + 4, z, XianxiaPalette.Metal(XianxiaPalette.Gold));
    }
    /// <summary>剑冢遗物：残碑与横卧斜插的剑鞘</summary>
    private static void BuildTombRelics(VoxelWorld world)
    {
        foreach (var (x, z, height, seed) in new[]
                 {
                     (30, -18, 8, 4401), (-27, 22, 6, 4403), (12, 34, 9, 4407),
                     (-34, -12, 7, 4409), (-16, -32, 8, 4417),
                 })
        {
            if (!IsTombStairLane(x, z)) BuildBrokenStele(world, x, z, height, seed);
        }
        for (var index = 0; index < 9; index++)
        {
            var angle = index * MathF.Tau / 9F + .6F;
            var radius = 24F + TemplateKit.Hash(2, index, 3, 4421) % 16;
            var x = Coord(angle, radius);
            var z = Coord(angle + MathF.PI / 2F, radius);
            if (!IsTombStairLane(x, z)) BuildScabbard(world, x, z, 6 + TemplateKit.Hash(3, index, 5, 4423) % 5, 4429);
        }
    }
    /// <summary>残破石碑：碑体倾斜、顶部崩缺、碑面刻煞红灵纹</summary>
    private static void BuildBrokenStele(VoxelWorld world, int x, int z, int height, int seed)
    {
        var lean = TemplateKit.Hash(x, 0, z, seed) % 3 - 1;
        var wide = 2 + TemplateKit.Hash(x, 1, z, seed) % 2;
        for (var y = 0; y < height; y++)
        {
            var offset = lean * y / Math.Max(height, 1);
            for (var side = 0; side < wide; side++)
            {
                if (y >= height - 2 && Chance(x, y, z + side, seed, .38F)) continue;
                var color = XianxiaPalette.Blend(XianxiaPalette.BasaltDeep, XianxiaPalette.StoneDark, XianxiaNoise.Hash01(x + side, z, seed));
                Put(world, x + offset + side, y, z, XianxiaPalette.Grain(color, x + side, y, z, seed + 1, .06F));
            }
        }
        if (height < 6) return;
        for (var side = 0; side <= 1; side++)
            Put(world, x + lean, height / 2, z + side, XianxiaPalette.Glow(XianxiaPalette.BloodRed));
    }
    /// <summary>剑鞘：横卧或斜插的细长匣，铜口银尾</summary>
    private static void BuildScabbard(VoxelWorld world, int x, int z, int length, int seed)
    {
        var lying = (TemplateKit.Hash(x, 2, z, seed) & 1) == 0;
        var alongX = (TemplateKit.Hash(x, 3, z, seed) & 1) == 0;
        var lean = TemplateKit.Hash(x, 4, z, seed) % 3 - 1;
        for (var step = 0; step < length; step++)
        {
            var value = step == length - 1 ? XianxiaPalette.Metal(XianxiaPalette.Silver)
                : step < 2 ? XianxiaPalette.Metal(XianxiaPalette.Copper)
                : XianxiaPalette.Cloth(XianxiaPalette.InkNight);
            if (lying) Put(world, alongX ? x + step : x, 1, alongX ? z : z + step, value);
            else
            {
                var offset = lean * step / Math.Max(length, 1);
                Put(world, x + offset, step, z, value);
                if (step < 2) Put(world, x + offset + 1, step, z, value);
            }
        }
    }

    // 锁妖塔
    /// <summary>锁妖塔：八角十三层塔身逐层收分，腰檐、塔刹、锁链与八卦塔基</summary>
    public static VoxelWorld BuildDemonPagoda()
    {
        var world = new VoxelWorld();
        BuildPagodaBase(world);
        for (var floor = 0; floor < PagodaFloors; floor++)
        {
            var baseY = PagodaBaseY + floor * PagodaFloorStep;
            BuildPagodaBody(world, baseY, PagodaRadius(floor), floor);
            BuildPagodaEave(world, baseY + 6, PagodaRadius(floor), floor);
        }
        BuildPagodaFinial(world, PagodaBaseY + PagodaFloors * PagodaFloorStep);
        BuildPagodaChains(world);
        return world;
    }
    /// <summary>塔基：双层石台、四面踏跺、八卦纹与镇妖石像</summary>
    private static void BuildPagodaBase(VoxelWorld world)
    {
        for (var y = 0; y <= 2; y++)
            Disc(world, y, 23.5D, XianxiaPalette.Grain(
                y == 2 ? XianxiaPalette.Stone : XianxiaPalette.StoneDark, 0, y, 0, 4501, .05F));
        for (var y = 3; y <= PagodaPlinthY; y++)
            Disc(world, y, 17.5D, XianxiaPalette.Grain(XianxiaPalette.Stone, 0, y, 0, 4503, .04F));
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        for (var step = 0; step < 4; step++)
        {
            var topY = PagodaPlinthY - step;
            for (var far = 23.5D + step * 1.15D; far <= 24.6D + step * 1.15D; far += .55D)
            for (var side = -2; side <= 2; side++)
            {
                var x = dx == 0 ? side : (int)Math.Round(dx * far);
                var z = dz == 0 ? side : (int)Math.Round(dz * far);
                for (var y = 0; y <= topY; y++)
                    Put(world, x, y, z, XianxiaPalette.Solid(y == topY ? XianxiaPalette.Stone : XianxiaPalette.StoneDark));
            }
        }
        for (var sector = 0; sector < 8; sector++)
        {
            var angle = sector * MathF.Tau / 8F + MathF.PI / 8F;
            BuildBaguaTrigram(world, PagodaPlinthY, angle, 15.4D, sector % 2 == 0);
            BuildDemonStatue(world, Coord(angle, 20.6D), Coord(angle + MathF.PI / 2F, 20.6D), 4507);
        }
        RuneCircle(world, PagodaPlinthY, 14.2D, XianxiaPalette.Glow(XianxiaPalette.BloodRed));
        RuneCircle(world, PagodaPlinthY, 17.2D, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
    }
    /// <summary>一组八卦爻线，broken 表示中爻断裂的阴爻</summary>
    private static void BuildBaguaTrigram(VoxelWorld world, int y, double angle, double radius, bool broken)
    {
        var cos = MathF.Cos((float)angle);
        var sin = MathF.Sin((float)angle);
        for (var bar = 0; bar < 3; bar++)
        {
            var along = radius + (bar - 1) * 1.15D;
            for (var tangent = -2; tangent <= 2; tangent++)
            {
                if (broken && bar == 1 && Math.Abs(tangent) <= 1) continue;
                Put(world, (int)Math.Round(cos * along - sin * tangent * .95D), y,
                    (int)Math.Round(sin * along + cos * tangent * .95D), XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
            }
        }
    }
    /// <summary>蹲伏的镇妖石兽，头朝塔心，双目煞红</summary>
    private static void BuildDemonStatue(VoxelWorld world, int centerX, int centerZ, int seed)
    {
        var inwardX = Math.Sign(-centerX);
        var inwardZ = Math.Sign(-centerZ);
        var stone = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
        var mossy = XianxiaPalette.Solid(XianxiaPalette.Blend(XianxiaPalette.StoneDark, XianxiaPalette.Moss, .3F));
        TemplateKit.Ellipsoid(world, centerX + .5, 5.1D, centerZ + .5, 2.2D, 1.7D, 2.4D, stone);
        TemplateKit.Ellipsoid(world, centerX + .5 + inwardX * 1.9D, 6.2D, centerZ + .5 + inwardZ * 1.9D,
            1.5D, 1.4D, 1.5D, mossy);
        var headX = centerX + inwardX * 3;
        var headZ = centerZ + inwardZ * 3;
        Put(world, headX, 7, headZ, XianxiaPalette.Glow(XianxiaPalette.BloodRed));
        Put(world, headX + inwardX, 7, headZ, XianxiaPalette.Glow(XianxiaPalette.BloodRed));
        Put(world, headX, 8, headZ, stone);
        Put(world, headX, 9, headZ, mossy);
        Put(world, centerX + 1, 3, centerZ + 1, stone);
        Put(world, centerX - 1, 3, centerZ - 1, stone);
        if (Chance(centerX, 0, centerZ, seed, .6F)) Put(world, centerX + inwardZ, 3, centerZ + inwardX, mossy);
    }
    /// <summary>塔身一层：石壁、朱柱、梁枋与窗洞，内部留空</summary>
    private static void BuildPagodaBody(VoxelWorld world, int baseY, double radius, int floor)
    {
        var limit = (int)Math.Ceiling(radius) + 1;
        for (var y = baseY; y < baseY + 6; y++)
        for (var z = -limit; z <= limit; z++)
        for (var x = -limit; x <= limit; x++)
        {
            var distance = OctagonDistance(x, z);
            if (distance > radius + .35D) continue;
            if (distance < radius - 2D)
            {
                if (y == baseY) Put(world, x, y, z, XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, y, z, 4601, .05F));
                continue;
            }
            if (floor == 0 && IsPagodaDoor(x, z, y, baseY, radius)) continue;
            VoxelValue value;
            if (y == baseY) value = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
            else if (y == baseY + 5) value = XianxiaPalette.Metal(XianxiaPalette.Gold);
            else if (Math.Abs(Math.Abs(x) - Math.Abs(z)) <= 1 && distance > radius - 2.5D)
                value = XianxiaPalette.Solid(XianxiaPalette.Cinnabar);
            else value = XianxiaPalette.Grain(XianxiaPalette.Blend(XianxiaPalette.MoonWhite, XianxiaPalette.Stone,
                .25F + XianxiaNoise.Hash01(x + floor * 13, z, 4603) * .35F), x, y, z, 4607, .035F);
            if (IsPagodaWindow(x, z, y, baseY, radius))
                value = y == baseY + 4 ? XianxiaPalette.Glow(XianxiaPalette.SpiritCyan)
                    : XianxiaPalette.Solid(XianxiaPalette.InkNight);
            Put(world, x, y, z, value);
        }
    }

    private static bool IsPagodaDoor(int x, int z, int y, int baseY, double radius) =>
        x < 0 && Math.Abs(x) >= radius - 2.2D && Math.Abs(z) <= 2 && y <= baseY + 4;

    private static bool IsPagodaWindow(int x, int z, int y, int baseY, double radius) =>
        y >= baseY + 2 && y <= baseY + 4
        && ((Math.Abs(x) <= 1 && Math.Abs(z) > radius * .55D)
            || (Math.Abs(z) <= 1 && Math.Abs(x) > radius * .55D));
    /// <summary>腰檐三层出檐，八角起翘并点缀发光雷纹</summary>
    private static void BuildPagodaEave(VoxelWorld world, int eaveY, double radius, int floor)
    {
        var limit = (int)Math.Ceiling(radius + 4.5D);
        for (var layer = 0; layer < 3; layer++)
        for (var z = -limit; z <= limit; z++)
        for (var x = -limit; x <= limit; x++)
        {
            var distance = OctagonDistance(x, z);
            if (distance > radius + 3.4D - layer * 1.05D || distance < radius - 2.2D) continue;
            Put(world, x, eaveY + layer, z, XianxiaPalette.Grain(
                layer == 2 ? XianxiaPalette.RoofTileLight : XianxiaPalette.RoofTile, x, eaveY + layer, z, 4703, .05F));
        }
        for (var corner = 0; corner < 8; corner++)
        {
            var angle = corner * MathF.Tau / 8F + MathF.PI / 8F;
            for (var step = 0; step < 3; step++)
                Put(world, Coord(angle, radius + 3.4D + step * .6D), eaveY + 1 + step,
                    Coord(angle + MathF.PI / 2F, radius + 3.4D + step * .6D),
                    XianxiaPalette.Solid(XianxiaPalette.RoofTileLight));
        }
        for (var rune = 0; rune < 4; rune++)
        {
            var angle = rune * MathF.Tau / 4F + (floor % 2 == 0 ? .2F : .55F);
            foreach (var (offset, lift) in new[] { (-.1F, 0), (-.04F, 1), (.03F, 0), (.09F, 1) })
                Put(world, Coord(angle + offset, radius + 2.1D), eaveY + 1 + lift,
                    Coord(angle + offset + MathF.PI / 2F, radius + 2.1D), XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
        }
    }
    /// <summary>塔刹：相轮、宝顶与浮空灵珠</summary>
    private static void BuildPagodaFinial(VoxelWorld world, int topY)
    {
        Disc(world, topY, 4.6D, XianxiaPalette.Solid(XianxiaPalette.RoofTileLight));
        Put(world, 0, topY + 1, 0, XianxiaPalette.Metal(XianxiaPalette.Gold));
        for (var ring = 0; ring < 9; ring++)
        {
            RuneCircle(world, topY + 2 + ring, 3.5D - ring * .25D, XianxiaPalette.Metal(XianxiaPalette.Copper));
            Put(world, 0, topY + 2 + ring, 0, XianxiaPalette.Metal(XianxiaPalette.Silver));
        }
        TemplateKit.Ellipsoid(world, .5D, topY + 12.5D, .5D, 2.3D, 2.5D, 2.3D,
            XianxiaPalette.Jade(XianxiaPalette.JadeDeep));
        TemplateKit.Ellipsoid(world, .5D, topY + 12.5D, .5D, 1.2D, 1.5D, 1.2D,
            XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        Put(world, 0, topY + 15, 0, XianxiaPalette.Metal(XianxiaPalette.Gold));
        Put(world, 0, topY + 16, 0, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
    }
    /// <summary>锁链：环塔铁箍、螺旋锁链、四道斜拉链与铁环</summary>
    private static void BuildPagodaChains(VoxelWorld world)
    {
        var link = XianxiaPalette.Metal(XianxiaPalette.Silver);
        foreach (var y in new[] { 43, 70, 97, 115 }) IronCollar(world, y, link);
        for (var sample = 0; sample < 360; sample++)
        {
            var angle = sample * 8F * MathF.PI / 360F;
            var y = 12 + sample * 90F / 360F;
            var radius = OctagonBoundaryRadius(angle, PagodaRadiusAt(y)) + 2.2D;
            Put(world, Coord(angle, radius), (int)y, Coord(angle + MathF.PI / 2F, radius), link);
            if (sample % 3 == 0)
                Put(world, Coord(angle, radius), (int)y + 1, Coord(angle + MathF.PI / 2F, radius), link);
        }
        for (var anchor = 0; anchor < 4; anchor++)
        {
            var angle = anchor * MathF.Tau / 4F + MathF.PI / 4F;
            ChainLine(world, Coord(angle, 22D), PagodaPlinthY + 1, Coord(angle + MathF.PI / 2F, 22D),
                Coord(angle, 14.1D), 47, Coord(angle + MathF.PI / 2F, 14.1D), link);
            IronRing(world, Coord(angle, 17.4D), 26, Coord(angle + MathF.PI / 2F, 17.4D), link);
        }
    }
    /// <summary>环塔铁箍：贴着八角塔身交错的链环</summary>
    private static void IronCollar(VoxelWorld world, int y, VoxelValue value)
    {
        var radius = PagodaRadiusAt(y) + .9D;
        var steps = (int)(MathF.Tau * radius * 1.8F);
        for (var step = 0; step < steps; step++)
        {
            var angle = step * MathF.Tau / steps;
            var x = Coord(angle, OctagonBoundaryRadius(angle, radius));
            var z = Coord(angle + MathF.PI / 2F, OctagonBoundaryRadius(angle, radius));
            Put(world, x, y, z, value);
            if (step % 2 == 0) Put(world, x, y + 1, z, value);
        }
    }
    /// <summary>两点之间的链，隔段抬高半格模拟链环交错</summary>
    private static void ChainLine(VoxelWorld world, int fromX, int fromY, int fromZ,
        int toX, int toY, int toZ, VoxelValue value)
    {
        var steps = Math.Max(4, Math.Max(Math.Abs(toX - fromX),
            Math.Max(Math.Abs(toY - fromY), Math.Abs(toZ - fromZ))) * 3);
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (double)steps;
            var x = (int)Math.Round(fromX + (toX - fromX) * t);
            var y = (int)Math.Round(fromY + (toY - fromY) * t);
            var z = (int)Math.Round(fromZ + (toZ - fromZ) * t);
            Put(world, x, y, z, value);
            if (step % 3 == 0) Put(world, x, y + 1, z, value);
        }
    }

    private static void IronRing(VoxelWorld world, int centerX, int centerY, int centerZ, VoxelValue value)
    {
        for (var offset = -2; offset <= 2; offset++)
        {
            Put(world, centerX + offset, centerY, centerZ - 2, value);
            Put(world, centerX + offset, centerY, centerZ + 2, value);
            Put(world, centerX - 2, centerY, centerZ + offset, value);
            Put(world, centerX + 2, centerY, centerZ + offset, value);
        }
    }

    private static double PagodaRadiusAt(double y) =>
        Math.Clamp(13.2D - (y - PagodaBaseY) / PagodaFloorStep * .62D, 5.76D, 13.2D);

    private static double PagodaRadius(int floor) => 13.2D - floor * .62D;

    // 传送大阵
    /// <summary>传送大阵：圆形石阵、双环八卦分区、发光符文与四象石柱</summary>
    public static VoxelWorld BuildTeleportArray()
    {
        var world = new VoxelWorld();
        BuildArrayPlatform(world);
        BuildArrayRunes(world);
        BuildArrayPillars(world);
        return world;
    }
    /// <summary>阵台：三层圆台、外圈石缘与四面踏跺</summary>
    private static void BuildArrayPlatform(VoxelWorld world)
    {
        for (var y = 0; y <= 2; y++)
            Disc(world, y, 24.5D - y * .65D, XianxiaPalette.Grain(
                y == 2 ? XianxiaPalette.Stone : XianxiaPalette.StoneDark, 0, y, 0, 4801, .05F));
        for (var gate = 0; gate < 4; gate++)
        {
            var angle = gate * MathF.Tau / 4F;
            Put(world, Coord(angle, 23.2D), 3, Coord(angle + MathF.PI / 2F, 23.2D),
                XianxiaPalette.Solid(XianxiaPalette.Stone));
            for (var step = 0; step < 3; step++)
            for (var side = -2; side <= 2; side++)
            {
                var sx = Coord(angle, 23.6D + step * .9D) + Coord(angle + MathF.PI / 2F, side);
                var sz = Coord(angle + MathF.PI / 2F, 23.6D + step * .9D) + Coord(angle, side);
                for (var y = 0; y <= 2 - step; y++)
                    Put(world, sx, y, sz, XianxiaPalette.Solid(
                        y == 2 - step ? XianxiaPalette.Stone : XianxiaPalette.StoneDark));
            }
        }
    }
    /// <summary>阵纹：双环、八道分区、八卦爻线、内圈符文与阵心</summary>
    private static void BuildArrayRunes(VoxelWorld world)
    {
        var outer = XianxiaPalette.Glow(XianxiaPalette.SpiritBlue);
        var inner = XianxiaPalette.Glow(XianxiaPalette.SpiritCyan);
        var seal = XianxiaPalette.Glow(XianxiaPalette.BloodRed);
        RuneCircle(world, 3, 21.2D, outer);
        RuneCircle(world, 3, 18.4D, outer);
        RuneCircle(world, 3, 12.8D, inner);
        for (var spoke = 0; spoke < 8; spoke++)
        {
            var angle = spoke * MathF.Tau / 8F;
            for (var radius = 13.4D; radius <= 21D; radius += .8D)
                Put(world, Coord(angle, radius), 3, Coord(angle + MathF.PI / 2F, radius),
                    XianxiaPalette.Solid(XianxiaPalette.StoneDark));
            BuildBaguaTrigram(world, 3, angle + MathF.PI / 8F, 16D, spoke % 2 == 0);
            BuildArrayGlyph(world, 3, angle + MathF.PI / 8F, 10.6D, spoke);
        }
        for (var ray = 0; ray < 8; ray++)
        {
            var angle = ray * MathF.Tau / 8F;
            for (var radius = 2.4D; radius <= 8.6D; radius += .8D)
                Put(world, Coord(angle, radius), 3, Coord(angle + MathF.PI / 2F, radius),
                    ray % 2 == 0 ? inner : outer);
        }
        for (var tick = 0; tick < 4; tick++)
        {
            var angle = tick * MathF.Tau / 4F + MathF.PI / 4F;
            Put(world, Coord(angle, 1.6D), 3, Coord(angle + MathF.PI / 2F, 1.6D), seal);
        }
        for (var z = -1; z <= 1; z++)
        for (var x = -1; x <= 1; x++) Put(world, x, 3, z, inner);
    }
    /// <summary>内圈的小型几何符文</summary>
    private static void BuildArrayGlyph(VoxelWorld world, int y, double angle, double radius, int sector)
    {        for (var step = 0; step < 3; step++)
        {
            var tangent = (step - 1) * .9D;
            var radial = ((sector >> step) & 1) == 0 ? .7D : -.7D;
            Put(world, Coord(angle, radius + radial) + Coord(angle + MathF.PI / 2F, tangent), y,
                Coord(angle + MathF.PI / 2F, radius + radial) + Coord(angle, tangent),
                XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        }
    }
    /// <summary>四象石柱：柱顶承露盘上悬浮空灵光，外围散列残石</summary>
    private static void BuildArrayPillars(VoxelWorld world)
    {
        for (var corner = 0; corner < 4; corner++)
        {
            var angle = corner * MathF.Tau / 4F + MathF.PI / 4F;
            var x = Coord(angle, 19.6D);
            var z = Coord(angle + MathF.PI / 2F, 19.6D);
            for (var y = 0; y <= 15; y++)
            for (var dz = -(y < 12 ? 1 : 0); dz <= (y < 12 ? 1 : 0); dz++)
            for (var dx = -(y < 12 ? 1 : 0); dx <= (y < 12 ? 1 : 0); dx++)
                Put(world, x + dx, y, z + dz, XianxiaPalette.Grain(
                    y % 5 == 0 ? XianxiaPalette.StoneDark : XianxiaPalette.Stone, x + dx, y, z + dz, 4901, .04F));
            Put(world, x, 6, z, XianxiaPalette.Metal(XianxiaPalette.Gold));
            Put(world, x, 7, z, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
            for (var dz = -2; dz <= 2; dz++)
            for (var dx = -2; dx <= 2; dx++)
            {
                if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                Put(world, x + dx, 16, z + dz, XianxiaPalette.Solid(XianxiaPalette.StoneDark));
            }
            TemplateKit.Ellipsoid(world, x + .5D, 20D, z + .5D, 1.6D, 1.6D, 1.6D,
                XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
            for (var side = 0; side < 4; side++)
            {
                var sideAngle = angle + side * MathF.PI / 2F;
                Put(world, x + Coord(sideAngle, 3), 20, z + Coord(sideAngle + MathF.PI / 2F, 3),
                    XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
            }
        }
    }

    // 古战场
    /// <summary>古战场：焦土、断矛成阵、残旗断甲与巨兽骸骨</summary>
    public static VoxelWorld BuildAncientBattlefield()
    {
        var world = new VoxelWorld();
        BuildBattleGround(world);
        BuildSpearFormation(world);
        BuildBattleBanners(world);
        BuildBeastSkeleton(world, -26, -12, 26, 1);
        BuildBeastSkeleton(world, 22, 36, 13, 2);
        BuildArmorRemains(world);
        BuildBrokenShields(world);
        BuildBattleRubble(world);
        return world;
    }
    /// <summary>焦土地表：烧灼色差、熔岩细缝与灰丘</summary>
    private static void BuildBattleGround(VoxelWorld world)
    {
        const int radius = 54;
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            var distance = MathF.Sqrt(x * x + z * z);
            if (distance > radius) continue;
            if (distance > 50F && Chance(x, 0, z, 5101, (distance - 50F) / 6F)) continue;
            var noise = XianxiaNoise.Fbm(x * .85F, z * .85F, 5103, 4, .05F);
            var charred = XianxiaNoise.Fbm(x * 1.4F, z * 1.4F, 5107, 3, .07F) > .56F;
            var color = XianxiaPalette.Blend(XianxiaPalette.Earth, XianxiaPalette.Ochre, noise);
            if (noise < .34F) color = XianxiaPalette.Blend(color, XianxiaPalette.StoneDark, .5F);
            if (charred) color = XianxiaPalette.Blend(color, XianxiaPalette.InkNight, .55F);
            foreach (var (craterX, craterZ, craterRadius) in Craters)
            {
                var craterDistance = MathF.Sqrt((x - craterX) * (x - craterX) + (z - craterZ) * (z - craterZ));
                if (craterDistance < craterRadius)
                {
                    color = XianxiaPalette.Blend(XianxiaPalette.InkNight, XianxiaPalette.BasaltDeep,
                        craterDistance / craterRadius);
                    if (craterDistance < craterRadius * .4F)
                        color = XianxiaPalette.Blend(color, XianxiaPalette.LavaOrange, .22F);
                }
                else if (craterDistance < craterRadius + 2F)
                    color = XianxiaPalette.Blend(color, XianxiaPalette.Basalt, .4F);
            }
            Put(world, x, 0, z, XianxiaPalette.Grain(color, x, 0, z, 5111, .07F));
            var vein = MathF.Abs(XianxiaNoise.Fbm(x * 1.6F, z * 1.6F, 5113, 3, .06F) - .5F);
            if (vein < .012F && distance < 46F && (charred || distance < 24F))
                Put(world, x, 0, z, XianxiaPalette.Glow(XianxiaPalette.LavaGlow));
            if (noise > .7F && !charred)
            {
                Put(world, x, 1, z, XianxiaPalette.Grain(XianxiaPalette.Ochre, x, 1, z, 5117, .06F));
                if (noise > .78F && Chance(x, 2, z, 5119, .5F))
                    Put(world, x, 2, z, XianxiaPalette.Grain(XianxiaPalette.Earth, x, 2, z, 5123, .06F));
            }
            else if (charred && noise > .5F)
                Put(world, x, 1, z, XianxiaPalette.Grain(
                    XianxiaPalette.Blend(XianxiaPalette.InkNight, XianxiaPalette.Earth, .3F), x, 1, z, 5127, .06F));
        }
    }
    /// <summary>断矛成阵：五列纵深的残阵，矛头多已折断</summary>
    private static void BuildSpearFormation(VoxelWorld world)
    {
        for (var rank = 0; rank < 5; rank++)
        for (var file = 0; file < 8; file++)
        {
            var x = -8 + file * 3 + TemplateKit.Hash(rank, 0, file, 5201) % 3 - 1;
            var z = 14 + rank * 3 + TemplateKit.Hash(rank, 1, file, 5203) % 3 - 1;
            var height = (rank * 8 + file) % 5 == 0 ? 2 : 4 + TemplateKit.Hash(rank, 2, file, 5207) % 7;
            PlantSpear(world, x, z, height, 5209);
        }
    }
    /// <summary>插一支断矛：木杆斜插，完好的带三格铁矛头</summary>
    private static void PlantSpear(VoxelWorld world, int x, int z, int height, int seed)
    {
        var lean = TemplateKit.Hash(x, 3, z, seed) % 3 - 1;
        for (var level = 0; level < height; level++)
        {
            var offset = lean * level / Math.Max(height, 1);
            Put(world, x + offset, level, z,
                XianxiaPalette.Grain(XianxiaPalette.Bark, x + offset, level, z, seed + 1, .07F));
        }
        var tipX = x + lean;
        if (height <= 3)
        {
            Put(world, tipX, height, z, XianxiaPalette.Metal(XianxiaPalette.StoneDark));
            return;
        }
        Put(world, tipX, height, z, XianxiaPalette.Metal(XianxiaPalette.Silver));
        Put(world, tipX, height + 1, z, XianxiaPalette.Metal(XianxiaPalette.Silver));
        Put(world, tipX + 1, height - 1, z, XianxiaPalette.Metal(XianxiaPalette.StoneDark));
        Put(world, tipX - 1, height - 1, z, XianxiaPalette.Metal(XianxiaPalette.StoneDark));
    }
    /// <summary>残旗：断裂倾覆的旗杆与破布</summary>
    private static void BuildBattleBanners(VoxelWorld world)
    {
        foreach (var (x, z, height, seed) in new[]
                 {
                     (-32, -4, 16, 5301), (10, -30, 12, 5303), (34, 6, 18, 5307),
                     (-4, 32, 10, 5311), (38, -32, 14, 5313),
                 })
        {
            var lean = TemplateKit.Hash(x, 0, z, seed) % 5 - 2;
            var broken = TemplateKit.Hash(x, 1, z, seed) % 3 == 0;
            var poleTop = broken ? height * 2 / 3 : height;
            for (var level = 0; level < poleTop; level++)
                Put(world, x + lean * level / Math.Max(height, 1), level, z, XianxiaPalette.Grain(
                    XianxiaPalette.Bark, x, level, z, seed + 1, .05F));
            var topX = x + lean * poleTop / Math.Max(height, 1);
            Put(world, topX, poleTop, z, XianxiaPalette.Metal(XianxiaPalette.Copper));
            if (broken) continue;
            var width = 4 + TemplateKit.Hash(x, 2, z, seed) % 3;
            var length = 5 + TemplateKit.Hash(x, 3, z, seed) % 4;
            var cloth = (TemplateKit.Hash(x, 4, z, seed) & 1) == 0
                ? XianxiaPalette.Cloth(XianxiaPalette.Cinnabar)
                : XianxiaPalette.Cloth(XianxiaPalette.InkNight);
            for (var row = 0; row < length; row++)
            for (var column = 0; column < width; column++)
            {
                if (row >= length - 2 && Chance(x + column, row, z, seed, .18F + (row - length + 2) * .22F)) continue;
                Put(world, topX + column - width + 1, poleTop - 1 - row, z, cloth);
            }
        }
    }
    /// <summary>巨兽骸骨：脊椎、肋骨、头骨、角、四肢与尾椎</summary>
    private static void BuildBeastSkeleton(VoxelWorld world, int centerX, int centerZ, int length, int seed)
    {
        var bone = XianxiaPalette.Solid(XianxiaPalette.BoneWhite);
        var aged = XianxiaPalette.Solid(XianxiaPalette.Blend(XianxiaPalette.BoneWhite, XianxiaPalette.Moss, .32F));
        const int spineY = 3;
        for (var index = 0; index <= length; index++)
        {
            var x = centerX + index;
            var y = spineY + (int)MathF.Round(MathF.Sin(index / (float)length * MathF.PI) * 1.3F);
            Put(world, x, y, centerZ, bone);
            if (index % 2 == 0) Put(world, x, y + 1, centerZ, aged);
            if (index < 3 || index > length - 3 || index % 2 != 0 || Chance(x, 0, centerZ, seed, .16F)) continue;
            for (var side = -1; side <= 1; side += 2)
            for (var step = 1; step <= 5; step++)
            {
                if (step == 3 && Chance(x, step, centerZ, seed + 1, .28F)) continue;
                Put(world, x, y - (int)MathF.Round(MathF.Pow(step, 1.15F) * .7F), centerZ + side * step,
                    step >= 4 ? aged : bone);
            }
        }
        var skullX = centerX + length + 2;
        TemplateKit.Ellipsoid(world, skullX + .5D, spineY + .5D, centerZ + .5D, 2.6D, 1.6D, 2D, bone);
        for (var side = -1; side <= 1; side += 2)
        {
            Put(world, skullX + 2, spineY + 1, centerZ + side, XianxiaPalette.Solid(XianxiaPalette.InkNight));
            for (var step = 0; step <= 4; step++)
                Put(world, skullX - step, spineY + 2 + (int)MathF.Round(step * .7F),
                    centerZ + side * (2 + step / 2), aged);
        }
        for (var side = -1; side <= 1; side++) Put(world, skullX + 2, spineY - 1, centerZ + side, bone);
        for (var leg = 0; leg < 4; leg++)
        {
            var legX = centerX + (leg < 2 ? length / 4 : length * 3 / 4) + leg % 2;
            var legZ = centerZ + (leg < 2 ? -3 : 3);
            var boneHeight = 3 + TemplateKit.Hash(legX, leg, legZ, seed) % 2;
            for (var level = 0; level < boneHeight; level++)
                Put(world, legX, 1 + level, legZ, level == boneHeight - 1 ? aged : bone);
            Put(world, legX, 1, legZ + Math.Sign(legZ), bone);
        }
        for (var step = 1; step <= 5; step++)
            Put(world, centerX - step, Math.Max(1, spineY - step / 2), centerZ, step % 2 == 0 ? aged : bone);
    }
    /// <summary>散落的兵甲：半埋胸甲、护肩与断剑</summary>
    private static void BuildArmorRemains(VoxelWorld world)
    {
        foreach (var (x, z, seed) in new[]
                 {
                     (-40, 22, 5401), (4, 4, 5403), (26, -14, 5407), (-14, 40, 5409), (44, 20, 5411),
                 })
        {
            var steel = XianxiaPalette.Metal(XianxiaPalette.Silver);
            var rust = XianxiaPalette.Metal(XianxiaPalette.Copper);
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) == 2) continue;
                Put(world, x + dx, 1, z + dz, dx == 0 ? rust : steel);
            }
            Put(world, x, 2, z, steel);
            Put(world, x + 2, 2, z - 1, rust);
            Put(world, x - 3, 1, z + 1, steel);
            Put(world, x - 3, 2, z + 1, XianxiaPalette.Solid(XianxiaPalette.Cinnabar));
            Put(world, x - 3, 3, z + 1, XianxiaPalette.Metal(XianxiaPalette.Gold));
            for (var level = 0; level < 4; level++)
                Put(world, x + 4, 1, z + level, level == 3
                    ? XianxiaPalette.Metal(XianxiaPalette.StoneDark) : XianxiaPalette.Metal(XianxiaPalette.Silver));
            if (Chance(x, 0, z, seed, .6F)) Put(world, x + 1, 1, z + 2, XianxiaPalette.Solid(XianxiaPalette.BoneWhite));
        }
    }
    /// <summary>残破盾牌：半埋的圆盾与盾心铜泡</summary>
    private static void BuildBrokenShields(VoxelWorld world)
    {
        foreach (var (x, z) in new[] { (-12, -2), (18, 12), (-38, -22), (30, -26), (-6, 44), (40, 36) })
        {
            var radius = 2 + Math.Abs(x + z) % 2;
            var start = TemplateKit.Hash(x, 0, z, 5501) % 8;
            for (var step = 0; step < 20; step++)
            {
                if ((step + start) % 9 == 0) continue;
                var angle = step * MathF.Tau / 20F;
                Put(world, Coord(angle, radius) + x, 1, Coord(angle + MathF.PI / 2F, radius) + z,
                    step % 4 == 0 ? XianxiaPalette.Solid(XianxiaPalette.Cinnabar)
                        : XianxiaPalette.Metal(XianxiaPalette.Copper));
            }
            Put(world, x, 1, z, XianxiaPalette.Metal(XianxiaPalette.Gold));
            if (x + z > 0) Put(world, x, 2, z, XianxiaPalette.Metal(XianxiaPalette.Silver));
        }
    }
    /// <summary>战场零碎：坍墙、散骨、断箭与焦木</summary>
    private static void BuildBattleRubble(VoxelWorld world)
    {
        for (var step = 0; step < 22; step++)
        {
            var x = 34 + step;
            var z = -18 + step / 2;
            var height = 1 + TemplateKit.Hash(x, 0, z, 5601) % 3;
            for (var y = 0; y < height; y++)
                Put(world, x, y, z, XianxiaPalette.Grain(
                    y == height - 1 && Chance(x, y, z, 5603, .3F)
                        ? XianxiaPalette.Moss : XianxiaPalette.StoneDark, x, y, z, 5607, .06F));
        }
        for (var index = 0; index < 90; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 17, 5611) * MathF.Tau;
            var radius = 6F + XianxiaNoise.Hash01(index, 19, 5613) * 44F;
            var x = Coord(angle, radius);
            var z = Coord(angle + MathF.PI / 2F, radius);
            if (Math.Abs(x) > 53 || Math.Abs(z) > 53) continue;
            for (var bone = 0; bone < 1 + TemplateKit.Hash(index, 21, 0, 5617) % 3; bone++)
                Put(world, x + bone, 1, z, bone == 0
                    ? XianxiaPalette.Solid(XianxiaPalette.BoneWhite)
                    : XianxiaPalette.Solid(XianxiaPalette.Blend(XianxiaPalette.BoneWhite, XianxiaPalette.Moss, .3F)));
        }
        for (var index = 0; index < 26; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 23, 5621) * MathF.Tau;
            var radius = 8F + XianxiaNoise.Hash01(index, 27, 5623) * 40F;
            var x = Coord(angle, radius);
            var z = Coord(angle + MathF.PI / 2F, radius);
            if (Math.Abs(x) > 53 || Math.Abs(z) > 53) continue;
            Put(world, x, 1, z, XianxiaPalette.Grain(XianxiaPalette.Bark, x, 1, z, 5627, .08F));
        }
    }

    // 共用工具

    private static void Put(VoxelWorld world, int x, int y, int z, VoxelValue value) =>
        world.Set(new VoxelCoord64(x, y, z), value);

    private static bool Chance(int x, int y, int z, int seed, float probability) =>
        TemplateKit.Hash(x, y, z, seed) / (float)0x7FFFFFFF < probability;
    /// <summary>取圆周上一点在某一轴向上的整数分量</summary>
    private static int Coord(double angle, double radius) =>
        (int)MathF.Round(MathF.Cos((float)angle) * (float)radius);

    private static void Disc(VoxelWorld world, int y, double radius, VoxelValue value)
    {
        var limit = (int)Math.Ceiling(radius);
        for (var z = -limit; z <= limit; z++)
        for (var x = -limit; x <= limit; x++)
            if (x * x + z * z <= radius * radius) Put(world, x, y, z, value);
    }

    private static void RuneCircle(VoxelWorld world, int y, double radius, VoxelValue value)
    {
        var steps = (int)(MathF.Tau * radius * 1.5F);
        for (var step = 0; step < steps; step++)
        {
            var angle = step * MathF.Tau / steps;
            Put(world, Coord(angle, radius), y, Coord(angle + MathF.PI / 2F, radius), value);
        }
    }

    private static double OctagonDistance(int x, int z) =>
        Math.Max(Math.Max(Math.Abs(x), Math.Abs(z)), (Math.Abs(x) + Math.Abs(z)) * .70710678D);

    private static double OctagonBoundaryRadius(double angle, double radius)
    {
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));
        return radius / Math.Max(Math.Max(Math.Max(cos, sin), (cos + sin) * .70710678D), .001D);
    }
}
