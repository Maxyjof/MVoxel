using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 青云剑宗建筑群：正殿、配殿、回廊、钟鼓楼、山门、藏经阁与演武场
/// 所有构件都以脚下平台为原点、水平居中于(0,0)
/// </summary>
internal static class XianxiaSect
{
    private static readonly VoxelValue Red = XianxiaPalette.Solid(XianxiaPalette.Cinnabar);
    private static readonly VoxelValue Tiles = XianxiaPalette.Solid(XianxiaPalette.RoofTile);
    private static readonly VoxelValue RidgeTiles = XianxiaPalette.Solid(XianxiaPalette.RoofTileLight);
    private static readonly VoxelValue Gold = XianxiaPalette.Metal(XianxiaPalette.Gold);
    private static readonly VoxelValue Bronze = XianxiaPalette.Metal(XianxiaPalette.Copper);
    private static readonly VoxelValue Silver = XianxiaPalette.Metal(XianxiaPalette.Silver);
    private static readonly VoxelValue Jade = XianxiaPalette.Jade(XianxiaPalette.JadeGreen);
    private static readonly VoxelValue JadeDeep = XianxiaPalette.Jade(XianxiaPalette.JadeDeep);
    private static readonly VoxelValue White = XianxiaPalette.Solid(XianxiaPalette.SilkWhite);
    private static readonly VoxelValue Moon = XianxiaPalette.Solid(XianxiaPalette.MoonWhite);
    private static readonly VoxelValue Stone = XianxiaPalette.Solid(XianxiaPalette.Stone);
    private static readonly VoxelValue StoneDark = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
    private static readonly VoxelValue Bark = XianxiaPalette.Solid(XianxiaPalette.Bark);
    private static readonly VoxelValue Cloth = XianxiaPalette.Cloth(XianxiaPalette.SilkWhite);
    private static readonly VoxelValue ClothRed = XianxiaPalette.Cloth(XianxiaPalette.Cinnabar);
    private static readonly VoxelValue Lamp = XianxiaPalette.Glow(
        XianxiaPalette.Blend(XianxiaPalette.Gold, XianxiaPalette.LavaGlow, .55F));
    private static readonly VoxelValue Rune = XianxiaPalette.Glow(XianxiaPalette.SpiritCyan);
    private static readonly Rgba32 PaveLight = XianxiaPalette.Blend(XianxiaPalette.Stone, XianxiaPalette.SilkWhite, .18F);
    private static readonly Rgba32 PaveWarm = XianxiaPalette.Blend(XianxiaPalette.Stone, XianxiaPalette.SandGold, .2F);

    /// <summary>
    /// 主殿群落：广场铺装、三层台基上的重檐正殿、东西配殿、回廊与钟鼓楼
    /// </summary>
    /// <returns>宗门主建筑群</returns>
    public static VoxelWorld BuildSectComplex()
    {
        var world = new VoxelWorld();
        PaveCourtyard(world);
        SwordMedallion(world, 28);
        BuildMainHall(world);
        BuildSideHall(world, -1);
        BuildSideHall(world, 1);
        BuildCorridor(world, -1);
        BuildCorridor(world, 1);
        BuildTower(world, -1, false);
        BuildTower(world, 1, true);
        BuildLanternRows(world);
        BuildBanners(world);
        LanternString(world, -34, 34, 14, 54);
        LanternString(world, -34, 34, 14, 24);
        Censer(world, 0, 50);
        StoneLion(world, -17, 0, -10);
        StoneLion(world, 17, 0, -10);
        PlantSectTrees(world);
        return world;
    }

    /// <summary>
    /// 山门坊：三开间牌坊、明间高起的匾额楼、镇山石狮与门前御路
    /// </summary>
    /// <returns>山门</returns>
    public static VoxelWorld BuildGateArch()
    {
        var world = new VoxelWorld();
        //门前石板御路
        for (var z = -14; z <= 40; z++)
        for (var x = -20; x <= 20; x++)
        {
            var edge = Math.Abs(x) == 20 || z == -14 || z == 40;
            var seam = z % 5 == 0;
            VoxelValue value;
            if (edge || seam) value = StoneDark;
            else if (Math.Abs(x) <= 3) value = XianxiaPalette.Grain(PaveLight, x, 0, z, 733, .02F);
            else value = XianxiaPalette.Grain(PaveWarm, x, 0, z, 735, .025F);
            Set(world, x, 0, z, value);
        }
        //台基与前后踏跺
        Pad(world, -26, -10, 26, 10, 2, 4701, JadeDeep);
        Stairs(world, -9, 9, 20, 10, 0, 2, 4703);
        Stairs(world, -9, 9, -20, -10, 0, 2, 4703);
        //四柱三间，柱脚加抱鼓石
        int[] pillars = [-22, -8, 7, 21];
        foreach (var x in pillars)
        {
            Pillar(world, x, -1, 2, 17);
            foreach (var sz in new[] { 2, -4 }) TemplateKit.Box(world, x - 2, 2, sz, x + 3, 3, sz + 1, StoneDark);
        }
        //额枋、斗拱与次间低檐
        TemplateKit.Box(world, -23, 17, -2, 23, 18, 1, Red);
        BracketBand(world, -24, -3, 24, 2, 18);
        Roof(world, -25, -7, -9, 7, 19, 7, 5, 6, false);
        Roof(world, 9, -7, 25, 7, 19, 7, 5, 6, false);
        //明间匾额楼与主檐
        TemplateKit.Box(world, -8, 19, -1, 8, 27, 0, Red);
        TemplateKit.Box(world, -8, 27, -1, 8, 27, 0, Gold);
        Plaque(world, 0, 21, 1, 8, 5);
        Roof(world, -13, -8, 13, 8, 28, 8, 10, 7, true);
        foreach (var x in new[] { -10, 10 }) HangingLantern(world, x, 2, 17);
        foreach (var x in new[] { -28, 28 }) Banner(world, x, 8, 20);
        foreach (var x in new[] { -14, 14 }) StoneLion(world, x, 0, 14);
        return world;
    }

    /// <summary>
    /// 藏经阁：三层台基上的五层楼阁塔，逐层收分、四面踏跺、绕塔松樱
    /// </summary>
    /// <returns>藏经阁</returns>
    public static VoxelWorld BuildScripturePavilion()
    {
        var world = new VoxelWorld();
        //塔院圆形铺地
        for (var z = -36; z <= 36; z++)
        for (var x = -36; x <= 36; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius > 36F) continue;
            var ring = radius > 33F;
            var seam = (x + z) % 7 == 0 || (x - z) % 7 == 0;
            VoxelValue value;
            if (ring) value = StoneDark;
            else if (seam) value = XianxiaPalette.Grain(PaveWarm, x, 0, z, 4811, .02F);
            else value = XianxiaPalette.Grain(PaveLight, x, 0, z, 4813, .022F);
            Set(world, x, 0, z, value);
        }
        //三层台基与四面踏跺
        Pad(world, -24, -24, 24, 24, 3, 4801, JadeDeep);
        Pad(world, -21, -21, 21, 21, 6, 4803, Gold);
        Stairs(world, -6, 6, 30, 18, 0, 6, 4805);
        Stairs(world, -6, 6, -30, -18, 0, 6, 4805);
        StairsX(world, -6, 6, 30, 18, 0, 6, 4805);
        StairsX(world, -6, 6, -30, -18, 0, 6, 4805);
        //五层塔身与五重檐
        (int Half, int From, int To)[] storeys = [(15, 6, 16), (12, 21, 27), (10, 32, 37), (8, 42, 46), (6, 51, 55)];
        for (var index = 0; index < storeys.Length; index++)
        {
            var (half, from, to) = storeys[index];
            Storey(world, half, from, to, index == 0);
            BracketBand(world, -half - 1, -half - 1, half + 1, half + 1, to);
            var last = index == storeys.Length - 1;
            var topHalf = last ? 1 : storeys[index + 1].Half + 1;
            var eave = half + 4;
            Roof(world, -eave, -eave, eave, eave, to + 1, last ? 5 : 3, eave - topHalf, eave - topHalf, last);
            HangingLantern(world, -half + 2, half + 3, to);
            HangingLantern(world, half - 2, half + 3, to);
        }
        Plaque(world, 0, 9, 16, 7, 5);
        foreach (var (sx, sz) in new[] { (-22, -22), (22, -22), (-22, 22), (22, 22) }) StoneLantern(world, sx, sz, 3);
        XianxiaFlora.PineTree(world, -25, 0, 25, 71, 2);
        XianxiaFlora.PineTree(world, 26, 0, 24, 73, 2);
        XianxiaFlora.CherryTree(world, 26, 0, -24, 77);
        XianxiaFlora.CherryTree(world, -26, 0, -25, 79);
        return world;
    }

    /// <summary>
    /// 演武场：圆形剑坪、三级圆台、剑桩阵、兵器架与北侧观礼台
    /// </summary>
    /// <returns>演武场</returns>
    public static VoxelWorld BuildSwordArena()
    {
        var world = new VoxelWorld();
        //圆形剑坪：放射金线、双环与八条石缝
        for (var z = -46; z <= 46; z++)
        for (var x = -46; x <= 46; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius > 46F) continue;
            var angle = MathF.Atan2(z, x);
            var spoke = radius > 16F && radius < 44F
                && MathF.Abs(MathF.IEEERemainder(angle, MathF.PI / 4F)) < .03F;
            var ring = radius is > 44.4F and < 45.6F or > 31.5F and < 32.5F or > 21.5F and < 22.5F;
            var seam = x % 6 == 0 || z % 6 == 0;
            VoxelValue value;
            if (radius > 45F) value = StoneDark;
            else if (ring) value = Jade;
            else if (spoke) value = Gold;
            else if (seam) value = StoneDark;
            else value = XianxiaPalette.Grain(PaveWarm, x, 0, z, 751, .03F);
            Set(world, x, 0, z, value);
        }
        //三级圆台
        for (var z = -18; z <= 18; z++)
        for (var x = -18; x <= 18; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius > 17F) continue;
            var top = radius > 14F ? 1 : radius > 11F ? 2 : 4;
            for (var y = 0; y <= top; y++)
            {
                var value = y == top
                    ? radius <= 11F ? XianxiaPalette.Grain(PaveLight, x, y, z, 753, .02F) : Stone
                    : StoneDark;
                Set(world, x, y, z, value);
            }
        }
        //台面剑纹：剑锋朝北侧观礼台
        for (var dz = -10; dz <= 4; dz++)
        for (var dx = -4; dx <= 4; dx++)
        {
            VoxelValue? value = null;
            if (dz <= -3 && Math.Abs(dx) <= (dz <= -9 ? 0 : 1)) value = dz == -10 || dx == 0 ? Gold : Silver;
            else if (dz is -2 or -1 && Math.Abs(dx) <= 4) value = Gold;
            else if (dz is >= 0 and <= 3 && Math.Abs(dx) <= 1) value = dz % 2 == 0 ? Gold : ClothRed;
            else if (dz == 4 && Math.Abs(dx) <= 2) value = Jade;
            if (value is { } sample) Set(world, dx, 4, dz, sample);
        }
        //十六根剑桩，避开北侧观礼台
        for (var index = 0; index < 16; index++)
        {
            var angle = index * MathF.PI / 8F;
            var radius = index % 2 == 0 ? 27F : 36F;
            var x = (int)MathF.Round(MathF.Cos(angle) * radius);
            var z = (int)MathF.Round(MathF.Sin(angle) * radius);
            if (z < -20 && Math.Abs(x) < 34) continue;
            Stake(world, x, z);
        }
        //北侧三级观礼台与观礼棚
        Pad(world, -32, -48, 32, -26, 3, 4701, JadeDeep);
        Pad(world, -28, -44, 28, -28, 6, 4703, Gold);
        Pad(world, -24, -40, 24, -30, 8, 4705, JadeDeep);
        Stairs(world, -9, 9, -16, -30, 0, 8, 4707);
        Railing(world, -23, -39, 23, -31, 8, 9);
        foreach (var x in new[] { -18, -6, 6, 18 })
        foreach (var z in new[] { -38, -33 })
            Pillar(world, x, z, 8, 18);
        BracketBand(world, -20, -40, 20, -30, 18);
        Roof(world, -26, -46, 26, -24, 19, 9, 10, 10, true);
        Banner(world, -34, -20, 20);
        Banner(world, 34, -20, 20);
        foreach (var (rx, rz) in new[] { (-40, 0), (40, 0), (0, 40) }) WeaponRack(world, rx, rz);
        foreach (var (sx, sz) in new[] { (-13, -13), (13, -13), (-13, 13), (13, 13) }) StoneLantern(world, sx, sz);
        XianxiaFlora.PineTree(world, -52, 0, 26, 91, 2);
        XianxiaFlora.PineTree(world, 52, 0, 26, 93, 2);
        XianxiaFlora.PineTree(world, 0, 0, -56, 95, 2);
        return world;
    }

    /// <summary>
    /// 正殿：三层须弥座、七间殿身、重檐与斗拱，前设御路踏跺与汉白玉栏杆
    /// </summary>
    private static void BuildMainHall(VoxelWorld world)
    {
        Pad(world, -46, -88, 46, -24, 3, 4101, JadeDeep);
        Pad(world, -42, -84, 42, -28, 6, 4103, Gold);
        Pad(world, -38, -80, 38, -32, 10, 4105, JadeDeep);
        Railing(world, -37, -79, 37, -33, 10, 11);
        Stairs(world, -11, 11, -14, -32, 0, 10, 7);
        //下层殿身：两格厚月白墙与青石墙裙
        Layer(world, -30, -73, 30, -39, 10, Stone, 4111);
        TemplateKit.Box(world, -28, 10, -41, 28, 26, -40, Moon);
        TemplateKit.Box(world, -28, 10, -72, 28, 26, -71, Moon);
        TemplateKit.Box(world, -28, 10, -72, -27, 26, -41, Moon);
        TemplateKit.Box(world, 27, 10, -72, 28, 26, -41, Moon);
        TemplateKit.Box(world, -29, 10, -42, 29, 12, -39, StoneDark);
        TemplateKit.Box(world, -29, 10, -73, 29, 12, -70, StoneDark);
        TemplateKit.Box(world, -29, 10, -73, -26, 12, -39, StoneDark);
        TemplateKit.Box(world, 26, 10, -73, 29, 12, -39, StoneDark);
        //前檐格扇门与槛窗
        for (var x = -28; x <= 28; x++)
        for (var y = 13; y <= 25; y++)
        {
            VoxelValue value;
            if (Math.Abs(x) <= 3)
            {
                value = Cloth;
                if (x is -3 or 3) value = Red;
                if (x % 3 == 0 && y % 4 == 1) value = Gold;
                if (x == 0) value = Red;
            }
            else if (y >= 17 && y <= 22 && Math.Abs(x) is > 5 and < 26)
            {
                value = x % 4 == 0 || y % 3 == 0 ? Gold : XianxiaPalette.Glass();
            }
            else
            {
                value = Moon;
                if (y is 13 or 25) value = Gold;
                if (y is >= 14 and <= 16 && Math.Abs(x) % 4 == 0) value = Jade;
            }
            Set(world, x, y, -40, value);
        }
        //山墙槛窗
        for (var z = -66; z <= -46; z++)
        for (var y = 17; y <= 22; y++)
        {
            if (z is not (>= -64 and <= -60) and not (>= -52 and <= -48)) continue;
            Set(world, -28, y, z, XianxiaPalette.Glass());
            Set(world, 28, y, z, XianxiaPalette.Glass());
        }
        //檐柱与山墙倚柱
        int[] frontPillars = [-27, -21, -13, -5, 4, 12, 20, 26];
        foreach (var x in frontPillars)
        {
            Pillar(world, x, -40, 10, 26);
            Pillar(world, x, -73, 10, 26);
        }
        foreach (var z in new[] { -64, -56, -48 })
        {
            Pillar(world, -29, z, 10, 26);
            Pillar(world, 28, z, 10, 26);
        }
        //匾额、斗拱与下檐
        Plaque(world, 0, 22, -39, 8, 5);
        BracketBand(world, -29, -73, 29, -39, 26);
        Roof(world, -35, -80, 35, -32, 27, 6, 15, 16, false);
        HangingLantern(world, -12, -33, 26);
        HangingLantern(world, 12, -33, 26);
        //上层殿身与重檐
        TemplateKit.Box(world, -20, 32, -49, 20, 40, -48, Moon);
        TemplateKit.Box(world, -20, 32, -64, 20, 40, -63, Moon);
        TemplateKit.Box(world, -20, 32, -64, -19, 40, -48, Moon);
        TemplateKit.Box(world, 19, 32, -64, 20, 40, -48, Moon);
        for (var x = -14; x <= 14; x++)
        for (var y = 35; y <= 39; y++)
        {
            if (Math.Abs(x) <= 8 || x % 4 == 0) continue;
            Set(world, x, y, -48, XianxiaPalette.Glass());
            Set(world, x, y, -64, XianxiaPalette.Glass());
        }
        foreach (var (px, pz) in new[] { (-21, -50), (20, -50), (-21, -66), (20, -66) }) Pillar(world, px, pz, 32, 40);
        Plaque(world, 0, 34, -47, 8, 5);
        BracketBand(world, -21, -65, 21, -47, 40);
        Roof(world, -27, -71, 27, -41, 41, 15, 15, 15, true);
    }

    /// <summary>
    /// 东西配殿：两级台基、面向广场的门廊与单檐歇山，檐角挂灯
    /// </summary>
    private static void BuildSideHall(VoxelWorld world, int side)
    {
        var x0 = side > 0 ? 48 : -68;
        var x1 = x0 + 20;
        const int z0 = -18;
        const int z1 = 12;
        Pad(world, x0 - 4, z0 - 6, x1 + 4, z1 + 6, 2, 4201, JadeDeep);
        Pad(world, x0 - 2, z0 - 4, x1 + 2, z1 + 4, 4, 4203, Gold);
        StairsX(world, z0 + 6, z1 - 6, side > 0 ? 34 : -34, side > 0 ? 48 : -48, 0, 4, 4205);
        //四面墙体与面向广场的门窗
        TemplateKit.Box(world, x0, 4, z0, x1, 16, z0 + 1, Moon);
        TemplateKit.Box(world, x0, 4, z1 - 1, x1, 16, z1, Moon);
        TemplateKit.Box(world, x0, 4, z0, x0 + 1, 16, z1, Moon);
        TemplateKit.Box(world, x1 - 1, 4, z0, x1, 16, z1, Moon);
        var facade = side > 0 ? x0 : x1;
        for (var z = z0 + 1; z < z1; z++)
        for (var y = 5; y <= 15; y++)
        {
            VoxelValue value;
            if (Math.Abs(z + 3) <= 2)
            {
                value = Cloth;
                if (z is -6 or -1) value = Red;
                if (y % 3 == 0 && z % 2 == 0) value = Gold;
            }
            else if (y >= 9 && y <= 14) value = z % 3 == 0 ? Gold : XianxiaPalette.Glass();
            else value = y == 15 ? Gold : Moon;
            Set(world, facade, y, z, value);
        }
        //檐柱、斗拱与屋顶
        var pillarX = side > 0 ? x0 - 1 : x1;
        foreach (var z in new[] { -17, -9, 2, 10 })
            Pillar(world, pillarX, z, 4, 16);
        BracketBand(world, x0 - 1, z0 - 1, x1 + 1, z1 + 1, 17);
        Roof(world, x0 - 6, z0 - 6, x1 + 6, z1 + 6, 18, 14, 15, 8, true);
        HangingLantern(world, side > 0 ? 44 : -44, -10, 17);
        HangingLantern(world, side > 0 ? 44 : -44, 4, 17);
    }

    /// <summary>
    /// 回廊：两列朱柱、连续额枋与低位廊檐，内侧挂灯
    /// </summary>
    private static void BuildCorridor(VoxelWorld world, int side)
    {
        var cx = side > 0 ? 80 : -80;
        var x0 = cx - 6;
        var x1 = cx + 6;
        TemplateKit.Box(world, x0, 0, -34, x1, 1, 42, StoneDark);
        Layer(world, x0 + 1, -33, x1 - 1, 41, 2, Stone, 4301);
        for (var z = -32; z <= 40; z += 8)
        {
            Pillar(world, cx - 4, z, 2, 10);
            Pillar(world, cx + 3, z, 2, 10);
            TemplateKit.Box(world, cx - 4, 10, z, cx + 4, 10, z, Red);
        }
        TemplateKit.Box(world, cx - 4, 10, -34, cx - 3, 10, 42, Red);
        TemplateKit.Box(world, cx + 3, 10, -34, cx + 4, 10, 42, Red);
        BracketBand(world, cx - 5, -34, cx + 5, 42, 11);
        Roof(world, cx - 8, -37, cx + 8, 45, 12, 5, 7, 3, false);
        for (var z = -28; z <= 36; z += 12)
            HangingLantern(world, cx - side * 5, z, 11);
    }

    /// <summary>
    /// 钟鼓楼：两层方台、四角亭身、攒尖顶，东楼悬铜钟、西楼置朱鼓
    /// </summary>
    private static void BuildTower(VoxelWorld world, int side, bool bell)
    {
        var cx = side > 0 ? 74 : -74;
        const int cz = 62;
        Pad(world, cx - 12, cz - 12, cx + 12, cz + 12, 3, 4401, JadeDeep);
        Pad(world, cx - 10, cz - 10, cx + 10, cz + 10, 6, 4403, Gold);
        Stairs(world, cx - 5, cx + 5, 76, 66, 0, 6, 4405);
        Railing(world, cx - 9, cz - 9, cx + 9, cz + 9, 6, 5);
        for (var dx = -1; dx <= 1; dx += 2)
        for (var dz = -1; dz <= 1; dz += 2)
            Pillar(world, cx + dx * 7 + (dx < 0 ? -1 : 0), cz + dz * 7 + (dz < 0 ? -1 : 0), 6, 22);
        BracketBand(world, cx - 9, cz - 9, cx + 9, cz + 9, 22);
        Roof(world, cx - 14, cz - 14, cx + 14, cz + 14, 24, 9, 10, 10, true);
        foreach (var (dx, dz) in new[] { (-11, -11), (11, -11), (-11, 11), (11, 11) })
            HangingLantern(world, cx + dx, cz + dz, 23);
        if (bell)
        {
            TemplateKit.Box(world, cx - 8, 20, cz - 1, cx + 8, 20, cz, Red);
            TemplateKit.Taper(world, cx + .5, cz + .5, 12, 19, 3.6, 1.4, Bronze);
            TemplateKit.Taper(world, cx + .5, cz + .5, 12, 12, 4.2, 4.2, Gold);
            Set(world, cx, 11, cz, Gold);
            return;
        }
        TemplateKit.Box(world, cx - 6, 20, cz - 1, cx + 6, 20, cz, Red);
        TemplateKit.Ellipsoid(world, cx + .5, 16, cz + .5, 4.6, 3.4, 3.4, ClothRed);
        TemplateKit.Ellipsoid(world, cx - 2.4, 16, cz + .5, .7, 3.5, 3.5, Gold);
        TemplateKit.Ellipsoid(world, cx + 3.4, 16, cz + .5, .7, 3.5, 3.5, Gold);
        Set(world, cx - 3, 19, cz, Bronze);
        Set(world, cx + 3, 19, cz, Bronze);
    }
    /// <summary>
    /// 广场铺装：大块方砖、暗色砖缝与四周石缘
    /// </summary>
    private static void PaveCourtyard(VoxelWorld world)
    {
        for (var z = -26; z <= 76; z++)
        for (var x = -92; x <= 92; x++)
        {
            var border = x is < -87 or > 87 || z is < -21 or > 71;
            var seam = x % 6 == 0 || z % 6 == 0;
            var tone = (x / 6 + z / 6) % 2 == 0;
            VoxelValue value;
            if (border) value = XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 0, z, 701, .03F);
            else if (seam) value = XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 0, z, 702, .03F);
            else value = XianxiaPalette.Grain(tone ? PaveLight : PaveWarm, x, 0, z, 703, .025F);
            Set(world, x, 0, z, value);
        }
    }
    /// <summary>
    /// 广场中心的剑纹圆盘：玉环、八道金辐与一柄剑锋朝殿的巨剑
    /// </summary>
    private static void SwordMedallion(VoxelWorld world, int centerZ)
    {
        for (var z = centerZ - 17; z <= centerZ + 17; z++)
        for (var x = -17; x <= 17; x++)
        {
            var dz = z - centerZ;
            var radius = MathF.Sqrt(x * x + dz * dz);
            if (radius > 17F) continue;
            var angle = MathF.Atan2(dz, x);
            var spoke = radius > 15.2F && radius < 16.4F
                && MathF.Abs(MathF.IEEERemainder(angle, MathF.PI / 4F)) < .055F;
            VoxelValue value;
            if (spoke) value = Gold;
            else if (radius > 16.2F) value = StoneDark;
            else if (radius > 15.2F) value = Rune;
            else if (radius > 13.6F) value = Jade;
            else if (radius > 12.6F) value = StoneDark;
            else value = XianxiaPalette.Grain(PaveLight, x, 0, z, 721, .02F);
            Set(world, x, 0, z, value);
        }
        for (var dz = -14; dz <= 5; dz++)
        for (var dx = -5; dx <= 5; dx++)
        {
            VoxelValue? value = null;
            if (dz <= -6 && Math.Abs(dx) <= (dz < -11 ? 0 : 1)) value = dz == -14 ? Gold : dx == 0 ? Gold : Silver;
            else if (dz is -5 or -4 && Math.Abs(dx) <= 4) value = Gold;
            else if (dz is >= -3 and <= 3 && Math.Abs(dx) <= 1) value = dz % 2 == 0 ? Gold : ClothRed;
            else if (dz is 4 or 5 && Math.Abs(dx) <= 2) value = Jade;
            if (value is { } sample) Set(world, dx, 0, centerZ + dz, sample);
        }
    }
    /// <summary>
    /// 宗门植栽：殿后古松、院角樱树与竹丛
    /// </summary>
    private static void PlantSectTrees(VoxelWorld world)
    {
        XianxiaFlora.PineTree(world, -60, 0, -78, 211, 2);
        XianxiaFlora.PineTree(world, 62, 0, -80, 223, 2);
        XianxiaFlora.PineTree(world, -50, 0, 68, 227, 1);
        XianxiaFlora.PineTree(world, 50, 0, 68, 229, 1);
        XianxiaFlora.CherryTree(world, -42, 0, 30, 233);
        XianxiaFlora.CherryTree(world, 42, 0, 30, 239);
        XianxiaFlora.BambooCluster(world, -76, 0, -84, 241);
        XianxiaFlora.BambooCluster(world, 78, 0, -86, 251);
    }
    /// <summary>
    /// 广场灯列：铜杆挑灯，两列对称排布
    /// </summary>
    private static void BuildLanternRows(VoxelWorld world)
    {
        for (var z = -8; z <= 56; z += 8)
        {
            LampPost(world, -20, z);
            LampPost(world, 20, z);
        }
    }
    /// <summary>
    /// 广场幡旗：三对朱幡，与灯笼串一同收束中轴
    /// </summary>
    private static void BuildBanners(VoxelWorld world)
    {
        foreach (var z in new[] { -6, 24, 54 })
        {
            Banner(world, -34, z, 18);
            Banner(world, 34, z, 18);
        }
    }

    // ---------- 通用构件 ----------
    /// <summary>
    /// 台基：顶面满铺，四壁成圈，内部留空，束腰环一圈玉带
    /// </summary>
    private static void Pad(VoxelWorld world, int x0, int z0, int x1, int z1, int topY, int seed, VoxelValue trim)
    {
        Layer(world, x0, z0, x1, z1, topY, Stone, seed);
        for (var y = 0; y < topY; y++)
            Ring(world, x0, z0, x1, z1, y, y == topY - 1 ? trim : StoneDark);
    }
    private static void Layer(VoxelWorld world, int x0, int z0, int x1, int z1, int y, VoxelValue value, int seed = 0) =>
        TemplateKit.Box(world, x0, y, z0, x1, y, z1, value, seed);
    private static void Ring(VoxelWorld world, int x0, int z0, int x1, int z1, int y, VoxelValue value)
    {
        for (var x = x0; x <= x1; x++)
        {
            Set(world, x, y, z0, value);
            Set(world, x, y, z1, value);
        }
        for (var z = z0 + 1; z < z1; z++)
        {
            Set(world, x0, y, z, value);
            Set(world, x1, y, z, value);
        }
    }
    /// <summary>
    /// 汉白玉栏杆：望柱每四格一根高出栏板，正面中段留踏跺口
    /// </summary>
    private static void Railing(VoxelWorld world, int x0, int z0, int x1, int z1, int y, int gap)
    {
        for (var x = x0; x <= x1; x++)
        {
            var open = Math.Abs(x - (x0 + x1) / 2) <= gap;
            var post = (x - x0) % 4 == 0 || x == x1;
            Rail(world, x, y, z0, post);
            if (!open) Rail(world, x, y, z1, post);
        }
        for (var z = z0 + 1; z < z1; z++)
        {
            var post = (z - z0) % 4 == 0;
            Rail(world, x0, y, z, post);
            Rail(world, x1, y, z, post);
        }
        static void Rail(VoxelWorld world, int x, int y, int z, bool post)
        {
            if (!post) { Set(world, x, y + 1, z, White); return; }
            Set(world, x, y + 1, z, Jade);
            Set(world, x, y + 2, z, Jade);
        }
    }
    /// <summary>
    /// 斗拱色带：檐下一圈金铜相间的托块，外挑一格遮蔽梁头
    /// </summary>
    private static void BracketBand(VoxelWorld world, int x0, int z0, int x1, int z1, int y)
    {
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            if (x != x0 && x != x1 && z != z0 && z != z1) continue;
            Set(world, x, y, z, (x + z) % 4 == 0 ? Gold : Bronze);
        }
    }
    /// <summary>
    /// 凹曲屋面：檐口平缓、近脊陡峭，逐层收分并压出瓦垄，
    /// 檐板满铺形成底面，四角生出上翘飞檐，脊端立鸱吻，脊心置宝顶
    /// </summary>
    private static void Roof(VoxelWorld world, int x0, int z0, int x1, int z1, int baseY, int rise,
        int insetX, int insetZ, bool finial)
    {
        var alongX = x1 - x0 >= z1 - z0;
        for (var layer = 0; layer <= rise; layer++)
        {
            var t = rise == 0 ? 1F : layer / (float)rise;
            var curve = MathF.Pow(t, .68F);
            var rx0 = (int)MathF.Round(x0 + insetX * curve);
            var rx1 = (int)MathF.Round(x1 - insetX * curve);
            var rz0 = (int)MathF.Round(z0 + insetZ * curve);
            var rz1 = (int)MathF.Round(z1 - insetZ * curve);
            if (rx0 > rx1 || rz0 > rz1) break;
            var y = baseY + layer;
            for (var x = rx0; x <= rx1; x++)
            for (var z = rz0; z <= rz1; z++)
            {
                var value = layer == rise
                    ? RidgeTiles
                    : layer == 0 && (x == rx0 || x == rx1 || z == rz0 || z == rz1)
                        ? RidgeTiles
                        : TileAt(x, z, alongX);
                Set(world, x, y, z, value);
            }
        }
        EaveHorn(world, x0, z0, -1, -1, baseY);
        EaveHorn(world, x0, z1, -1, 1, baseY);
        EaveHorn(world, x1, z0, 1, -1, baseY);
        EaveHorn(world, x1, z1, 1, 1, baseY);
        //正脊、鸱吻与宝顶
        var tx0 = x0 + insetX;
        var tx1 = x1 - insetX;
        var tz0 = z0 + insetZ;
        var tz1 = z1 - insetZ;
        var ridgeY = baseY + rise + 1;
        if (Math.Min(tx1 - tx0, tz1 - tz0) <= 3)
        {
            for (var x = tx0; x <= tx1; x++)
            for (var z = tz0; z <= tz1; z++) Set(world, x, ridgeY, z, RidgeTiles);
            var horizontal = tx1 - tx0 >= tz1 - tz0;
            var cx = (tx0 + tx1) / 2;
            var cz = (tz0 + tz1) / 2;
            var edgeAX = horizontal ? tx0 : cx;
            var edgeAZ = horizontal ? cz : tz0;
            var edgeBX = horizontal ? tx1 : cx;
            var edgeBZ = horizontal ? cz : tz1;
            Set(world, edgeAX, ridgeY + 2, edgeAZ, Gold);
            Set(world, edgeBX, ridgeY + 2, edgeBZ, Gold);
        }
        if (!finial) return;
        var finialX = (tx0 + tx1) / 2;
        var finialZ = (tz0 + tz1) / 2;
        TemplateKit.Taper(world, finialX + .5, finialZ + .5, ridgeY + 1, ridgeY + 4, 2.8, .2, Gold);
        Set(world, finialX, ridgeY + 5, finialZ, Jade);
    }
    private static VoxelValue TileAt(int x, int z, bool alongX) =>
        (alongX ? x : z) % 3 == 0 ? RidgeTiles : Tiles;
    /// <summary>
    /// 飞檐翘角：从檐角沿对角线外挑并逐格外翘，末端点金
    /// </summary>
    private static void EaveHorn(VoxelWorld world, int cornerX, int cornerZ, int dx, int dz, int eaveY)
    {
        for (var step = 1; step <= 4; step++)
        {
            var x = cornerX + dx * step;
            var z = cornerZ + dz * step;
            var y = eaveY + step;
            Set(world, x, y, z, RidgeTiles);
            if (step > 1) { Set(world, x - dx, y, z, RidgeTiles); Set(world, x, y, z - dz, RidgeTiles); }
            if (step == 4) Set(world, x, y + 1, z, Gold);
        }
    }
    /// <summary>
    /// 檐柱：石础、朱柱身与金箍柱头，柱径两格
    /// </summary>
    private static void Pillar(VoxelWorld world, int x, int z, int baseY, int topY)
    {
        TemplateKit.Box(world, x - 1, baseY, z - 1, x + 2, baseY + 1, z + 2, StoneDark);
        TemplateKit.Box(world, x, baseY + 2, z, x + 1, topY, z + 1, Red);
        TemplateKit.Box(world, x - 1, topY, z - 1, x + 2, topY + 1, z + 2, Gold);
    }
    /// <summary>
    /// 匾额：金框墨底，面上用金块拼出三组抽象字纹
    /// </summary>
    private static void Plaque(VoxelWorld world, int centerX, int minY, int z, int halfWidth, int height)
    {
        for (var x = centerX - halfWidth; x <= centerX + halfWidth; x++)
        for (var y = minY; y < minY + height; y++)
        {
            var border = x == centerX - halfWidth || x == centerX + halfWidth || y == minY || y == minY + height - 1;
            Set(world, x, y, z, border ? Gold : XianxiaPalette.Solid(XianxiaPalette.InkNight));
        }
        int[] glyphs = [0b101101101, 0b110011001, 0b101010111];
        for (var index = 0; index < glyphs.Length; index++)
        for (var bit = 0; bit < 9; bit++)
        {
            if ((glyphs[index] & (1 << bit)) == 0) continue;
            Set(world, centerX - 5 + index * 5 + bit % 3, minY + 1 + bit / 3, z, Gold);
        }
    }
    /// <summary>
    /// 踏跺：沿 Z 轴分级抬升的石阶，两侧加垂带石
    /// </summary>
    private static void Stairs(VoxelWorld world, int x0, int x1, int zFront, int zBack, int yBottom, int yTop, int seed)
    {
        var steps = Math.Max(1, yTop - yBottom);
        for (var step = 0; step < steps; step++)
        {
            var near = zFront + (zBack - zFront) * step / steps;
            var far = zFront + (zBack - zFront) * (step + 1) / steps;
            TemplateKit.Box(world, x0, yBottom, Math.Min(near, far), x1, yBottom + step + 1,
                Math.Max(near, far), Stone, seed);
        }
        for (var index = 0; index < 2; index++)
        {
            var side = index == 0 ? x0 - 1 : x1 + 1;
            for (var z = Math.Min(zFront, zBack); z <= Math.Max(zFront, zBack); z++)
            {
                var t = Math.Abs(z - zFront) / (float)Math.Max(1, Math.Abs(zBack - zFront));
                var y = yBottom + 1 + (int)MathF.Round(t * steps);
                Set(world, side, y, z, White);
                Set(world, side, y - 1, z, StoneDark);
            }
        }
    }
    private static void StairsX(VoxelWorld world, int z0, int z1, int xFront, int xBack, int yBottom, int yTop, int seed)
    {
        var steps = Math.Max(1, yTop - yBottom);
        for (var step = 0; step < steps; step++)
        {
            var near = xFront + (xBack - xFront) * step / steps;
            var far = xFront + (xBack - xFront) * (step + 1) / steps;
            TemplateKit.Box(world, Math.Min(near, far), yBottom, z0, Math.Max(near, far),
                yBottom + step + 1, z1, Stone, seed);
        }
        for (var index = 0; index < 2; index++)
        {
            var side = index == 0 ? z0 - 1 : z1 + 1;
            for (var x = Math.Min(xFront, xBack); x <= Math.Max(xFront, xBack); x++)
            {
                var t = Math.Abs(x - xFront) / (float)Math.Max(1, Math.Abs(xBack - xFront));
                var y = yBottom + 1 + (int)MathF.Round(t * steps);
                Set(world, x, y, side, White);
                Set(world, x, y - 1, side, StoneDark);
            }
        }
    }
    private static void HangingLantern(VoxelWorld world, int x, int z, int topY)
    {
        Set(world, x, topY, z, Bronze);
        TemplateKit.Box(world, x - 1, topY - 3, z - 1, x + 1, topY - 1, z + 1, Lamp);
        TemplateKit.Box(world, x - 1, topY - 4, z - 1, x + 1, topY - 4, z + 1, Gold);
        for (var dx = -1; dx <= 1; dx += 2)
        for (var dz = -1; dz <= 1; dz += 2)
            Set(world, x + dx, topY - 2, z + dz, Bronze);
    }
    private static void LampPost(VoxelWorld world, int x, int z)
    {
        TemplateKit.Box(world, x - 1, 0, z - 1, x + 1, 1, z + 1, StoneDark);
        TemplateKit.Box(world, x, 2, z, x, 5, z, Bronze);
        TemplateKit.Box(world, x - 1, 5, z - 1, x + 1, 5, z + 1, Gold);
        TemplateKit.Box(world, x - 1, 6, z - 1, x + 1, 8, z + 1, Lamp);
        for (var dx = -1; dx <= 1; dx += 2)
        for (var dz = -1; dz <= 1; dz += 2)
            TemplateKit.Box(world, x + dx, 6, z + dz, x + dx, 8, z + dz, Gold);
        TemplateKit.Box(world, x - 1, 9, z - 1, x + 1, 9, z + 1, Gold);
        Set(world, x, 10, z, Bronze);
    }
    private static void LanternString(VoxelWorld world, int x0, int x1, int y, int z)
    {
        var span = Math.Max(1, x1 - x0);
        for (var x = x0; x <= x1; x++)
        {
            var sag = (int)MathF.Round(2.2F * MathF.Sin((x - x0) / (float)span * MathF.PI));
            var line = y - sag;
            Set(world, x, line, z, Bronze);
            if ((x - x0) % 8 != 4 || x <= x0 + 2 || x >= x1 - 2) continue;
            Set(world, x, line - 1, z, Bronze);
            TemplateKit.Box(world, x - 1, line - 4, z - 1, x + 1, line - 2, z + 1, Lamp);
            TemplateKit.Box(world, x - 1, line - 5, z - 1, x + 1, line - 5, z + 1, Gold);
        }
    }
    private static void Banner(VoxelWorld world, int x, int z, int height)
    {
        var direction = x >= 0 ? 1 : -1;
        TemplateKit.Box(world, x - 1, 0, z - 1, x + 1, 2, z + 1, StoneDark);
        TemplateKit.Box(world, x, 3, z, x, height, z, Bronze);
        Set(world, x, height + 1, z, Gold);
        var top = height - 1;
        for (var y = top; y >= top - 9; y--)
        for (var step = 1; step <= 5; step++)
        {
            var value = y == top || y == top - 9 ? Gold : ClothRed;
            if (step == 5 && y % 3 == 0) value = Gold;
            Set(world, x + direction * step, y, z, value);
        }
        for (var step = 1; step <= 3; step++)
        for (var y = top - 10; y >= top - 12; y--)
            Set(world, x + direction * step * 2, y, z, ClothRed);
    }
    private static void Censer(VoxelWorld world, int x, int z)
    {
        TemplateKit.Box(world, x - 4, 0, z - 4, x + 4, 1, z + 4, StoneDark);
        TemplateKit.Box(world, x - 3, 2, z - 3, x + 3, 2, z + 3, Stone);
        for (var leg = 0; leg < 3; leg++)
        {
            var angle = MathF.PI / 2F + leg * MathF.Tau / 3F;
            var legX = x + (int)MathF.Round(MathF.Cos(angle) * 2.2F);
            var legZ = z + (int)MathF.Round(MathF.Sin(angle) * 2.2F);
            TemplateKit.Box(world, legX, 3, legZ, legX, 4, legZ, Bronze);
        }
        TemplateKit.Taper(world, x + .5, z + .5, 4, 9, 2.6, 3.4, Bronze);
        TemplateKit.Taper(world, x + .5, z + .5, 10, 10, 3.8, 3.8, Gold);
        TemplateKit.Box(world, x - 5, 8, z, x - 4, 9, z, Bronze);
        TemplateKit.Box(world, x + 4, 8, z, x + 5, 9, z, Bronze);
        Set(world, x, 11, z, Lamp);
        Set(world, x, 12, z, Rune);
    }
    private static void StoneLion(VoxelWorld world, int x, int y, int z)
    {
        TemplateKit.Box(world, x - 3, y, z - 3, x + 3, y + 1, z + 4, StoneDark);
        TemplateKit.Box(world, x - 2, y + 2, z - 2, x + 2, y + 2, z + 3, Stone);
        TemplateKit.Ellipsoid(world, x + .5, y + 5, z + .5, 2.4, 2.2, 2.6, Stone);
        TemplateKit.Ellipsoid(world, x + .5, y + 7.6, z + 2, 2, 1.8, 1.8, Stone);
        TemplateKit.Ellipsoid(world, x + .5, y + 7.4, z + .4, 2.3, 1.6, 2, StoneDark);
        Set(world, x - 1, y + 7, z + 4, Gold);
        Set(world, x + 2, y + 7, z + 4, Gold);
        for (var leg = -1; leg <= 1; leg += 2)
            TemplateKit.Box(world, x + leg * 2 - 1, y + 2, z + 2, x + leg * 2, y + 4, z + 3, StoneDark);
        Set(world, x, y + 9, z + 1, Gold);
    }
    private static void StoneLantern(VoxelWorld world, int x, int z, int y = 0)
    {
        TemplateKit.Box(world, x - 1, y, z - 1, x + 1, y + 1, z + 1, StoneDark);
        TemplateKit.Box(world, x, y + 2, z, x, y + 3, z, Stone);
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            Set(world, x + dx, y + 4, z + dz, StoneDark);
            Set(world, x + dx, y + 5, z + dz, StoneDark);
        }
        Set(world, x, y + 4, z, Lamp);
        Set(world, x, y + 5, z, Lamp);
        TemplateKit.Box(world, x - 2, y + 6, z - 2, x + 2, y + 6, z + 2, StoneDark);
        Set(world, x, y + 7, z, Jade);
    }
    private static void Stake(VoxelWorld world, int x, int z)
    {
        var height = 3 + TemplateKit.Hash(x, 0, z, 4601) % 3;
        TemplateKit.Box(world, x, 0, z, x, height, z, Bark);
        Set(world, x, height + 1, z, Silver);
        TemplateKit.Box(world, x - 1, height - 1, z, x + 1, height - 1, z, ClothRed);
        TemplateKit.Box(world, x, height - 2, z - 1, x, height - 2, z + 1, Bark);
    }
    private static void WeaponRack(VoxelWorld world, int x, int z)
    {
        for (var side = -1; side <= 1; side += 2)
        {
            TemplateKit.Box(world, x + side * 4, 0, z, x + side * 4, 6, z, Bark);
            TemplateKit.Box(world, x + side * 4, 6, z - 1, x + side * 4, 6, z + 1, Bark);
        }
        TemplateKit.Box(world, x - 4, 6, z, x + 4, 6, z, Bark);
        TemplateKit.Box(world, x - 4, 3, z, x + 4, 3, z, Bark);
        for (var index = -3; index <= 3; index += 2)
        {
            TemplateKit.Box(world, x + index, 1, z + 1, x + index, 4, z + 1, Silver);
            Set(world, x + index, 5, z + 1, Gold);
        }
    }
    /// <summary>
    /// 楼阁塔单层：四面月白墙、四角朱柱、金棂槛窗，正面开门
    /// </summary>
    private static void Storey(VoxelWorld world, int half, int y0, int y1, bool door)
    {
        TemplateKit.Box(world, -half, y0, half - 1, half, y1, half, Moon);
        TemplateKit.Box(world, -half, y0, -half, half, y1, -half + 1, Moon);
        TemplateKit.Box(world, -half, y0, -half, -half + 1, y1, half, Moon);
        TemplateKit.Box(world, half - 1, y0, -half, half, y1, half, Moon);
        for (var sx = -1; sx <= 1; sx += 2)
        for (var sz = -1; sz <= 1; sz += 2)
        {
            var cornerX = sx > 0 ? half - 1 : -half;
            var cornerZ = sz > 0 ? half - 1 : -half;
            TemplateKit.Box(world, cornerX, y0, cornerZ, cornerX + 1, y1, cornerZ + 1, Red);
        }
        for (var side = 0; side < 4; side++)
        {
            var sign = side % 2 == 0 ? 1 : -1;
            var horizontal = side < 2;
            for (var offset = -half + 3; offset <= half - 3; offset++)
            for (var y = y0 + 3; y <= y1 - 2; y++)
            {
                var (x, z) = horizontal ? (offset, sign * half) : (sign * half, offset);
                Set(world, x, y, z, offset % 3 == 0 ? Gold : XianxiaPalette.Glass());
            }
            for (var offset = -half; offset <= half; offset++)
            {
                var (x, z) = horizontal ? (offset, sign * half) : (sign * half, offset);
                Set(world, x, y0, z, StoneDark);
                Set(world, x, y1, z, Gold);
            }
        }
        if (!door) return;
        for (var offset = -3; offset <= 3; offset++)
        for (var y = y0 + 1; y <= y1 - 2; y++)
        {
            var value = offset is -3 or 3 ? Red : y % 3 == 0 ? Gold : Cloth;
            Set(world, offset, y, half, value);
            Set(world, offset, y, -half, value);
        }
    }
    private static void Set(VoxelWorld world, int x, int y, int z, VoxelValue value) =>
        world.Set(new VoxelCoord64(x, y, z), value);
}
