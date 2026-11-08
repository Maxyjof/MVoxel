using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates;

/// <summary>
/// 翻新后的模板场景库：统一调色与有机造型，
/// 每个场景围绕可辨识的主体轮廓组织细节
/// </summary>
internal static class TemplateScenes
{
    // 统一调色板：低饱和自然色 + 少量高饱和点缀
    internal static readonly Rgba32 Limestone = new(201, 191, 166);
    internal static readonly Rgba32 Plaster = new(232, 224, 206);
    internal static readonly Rgba32 Slate = new(74, 85, 104);
    internal static readonly Rgba32 RoofRed = new(142, 74, 62);
    internal static readonly Rgba32 Timber = new(107, 74, 47);
    internal static readonly Rgba32 Bark = new(84, 62, 44);
    internal static readonly Rgba32 LeafGreen = new(88, 152, 84);
    internal static readonly Rgba32 LeafDeep = new(58, 108, 66);
    internal static readonly Rgba32 GrassGreen = new(110, 158, 92);
    internal static readonly Rgba32 Soil = new(122, 92, 64);
    internal static readonly Rgba32 Rock = new(122, 124, 132);
    internal static readonly Rgba32 RockDark = new(88, 92, 102);
    internal static readonly Rgba32 Sand = new(216, 196, 154);
    internal static readonly Rgba32 PathGray = new(148, 142, 130);
    internal static readonly Rgba32 Copper = new(176, 106, 60);
    internal static readonly Rgba32 Fabric = new(122, 78, 99);
    internal static readonly Rgba32 AccentRed = new(196, 72, 66);
    internal static readonly Rgba32 AccentGold = new(232, 178, 82);
    internal static readonly Rgba32 Snow = new(236, 240, 246);

    /// <summary>玻璃体素：半透明浅蓝，槽 1</summary>
    internal static VoxelValue Glass(byte alpha = 120) =>
        new(new Rgba32(168, 216, 232, alpha), TemplateKit.SlotGlass, VoxelFlags.Translucent);

    /// <summary>水体素：半透明深蓝，槽 2</summary>
    internal static VoxelValue Water(byte alpha = 170) =>
        new(new Rgba32(62, 125, 181, alpha), TemplateKit.SlotWater, VoxelFlags.Translucent);

    /// <summary>金属体素，槽 3</summary>
    internal static VoxelValue Metal(Rgba32 color) => new(color, TemplateKit.SlotMetal);

    /// <summary>发光体素，槽 4</summary>
    internal static VoxelValue Glow(Rgba32 color) => new(color, TemplateKit.SlotGlow, VoxelFlags.Emissive);

    private static VoxelValue Paint(Rgba32 color, int jitterSeed = 0) => jitterSeed == 0
        ? new VoxelValue(color)
        : new VoxelValue(color);

    /// <summary>世界环境展示：小岛、灯塔和远处航标。</summary>
    public static VoxelWorld BuildEnvironmentIsland(int variant)
    {
        var w = new VoxelWorld();
        var sand = Paint(Sand);
        var grass = Paint(GrassGreen);
        var rock = Paint(RockDark);
        TemplateKit.Ellipsoid(w, 0, 1, 0, 22, 4, 17, rock);
        TemplateKit.Ellipsoid(w, 0, 3, 0, 20, 2, 15, sand);
        TemplateKit.Ellipsoid(w, -2, 4, -1, 15, 1.5, 11, grass);
        TemplateKit.Cylinder(w, -3, 5, -2, 5, 5, 1, Paint(Slate));
        TemplateKit.Cylinder(w, -3, 14, -2, 4, 4, 8, Paint(Plaster));
        TemplateKit.Cylinder(w, -3, 23, -2, 5, 5, 1, Paint(RoofRed));
        TemplateKit.Cylinder(w, -3, 25, -2, 3, 3, 1, Glow(AccentGold));
        TemplateKit.Cylinder(w, -3, 28, -2, 5, 5, 1, Paint(Slate));
        TemplateKit.Box(w, -4, 7, 1, -2, 11, 2, Paint(Timber));
        foreach (var (x, z) in new[] { (9, -3), (10, 6), (-13, 5) })
        {
            TemplateKit.Box(w, x, 5, z, x + 1, 11, z + 1, Paint(Bark));
            TemplateKit.Ellipsoid(w, x, 13, z, 4, 3, 4, Paint(variant == 2 ? new Rgba32(180, 130, 72) : LeafGreen));
        }
        TemplateKit.Ellipsoid(w, 29, 1, -20, 8, 3, 6, rock);
        TemplateKit.Box(w, 28, 3, -21, 30, 10, -19, Paint(Plaster));
        TemplateKit.Box(w, 28, 11, -21, 30, 12, -19, Glow(AccentGold));
        if (variant == 1)
            TemplateKit.Ellipsoid(w, -28, 1, -24, 9, 4, 7, rock);
        return w;
    }

    /// <summary>开放的室内餐桌场景，保留前方视线和物件细节。</summary>
    public static VoxelWorld BuildDefaultRoom()
    {
        var w = new VoxelWorld();
        var floor = Paint(new Rgba32(174, 135, 96));
        var wall = Paint(new Rgba32(229, 218, 194));
        var wood = Paint(new Rgba32(119, 72, 39));
        var lightWood = Paint(new Rgba32(177, 119, 68));
        var plate = Paint(new Rgba32(237, 232, 217));
        var cookie = Paint(new Rgba32(192, 137, 71));
        var chip = Paint(new Rgba32(83, 48, 30));
        var cola = new VoxelValue(new Rgba32(110, 52, 24, 200), TemplateKit.SlotCola, VoxelFlags.Translucent);
        TemplateKit.Box(w, -36, 0, -30, 36, 0, 30, floor);
        TemplateKit.Box(w, -36, 1, -30, 36, 36, -30, wall);
        TemplateKit.Box(w, -36, 1, -29, -36, 36, 30, wall);
        TemplateKit.Box(w, -12, 10, -5, 12, 11, 9, lightWood);
        foreach (var x in new[] { -10, 9 }) foreach (var z in new[] { -3, 7 })
            TemplateKit.Box(w, x, 1, z, x + 1, 9, z + 1, wood);
        // 椅子朝向桌子。
        TemplateKit.Box(w, -7, 6, 13, 3, 7, 19, lightWood);
        foreach (var x in new[] { -7, 2 }) foreach (var z in new[] { 13, 18 })
            TemplateKit.Box(w, x, 1, z, x + 1, 5, z + 1, wood);
        TemplateKit.Box(w, -7, 8, 18, -6, 15, 19, wood);
        TemplateKit.Box(w, 2, 8, 18, 3, 15, 19, wood);
        TemplateKit.Box(w, -7, 14, 18, 3, 15, 19, lightWood);
        // 碟子与三块带巧克力碎的饼干。
        TemplateKit.Ellipsoid(w, -4, 12.2, 1, 5, 0.7, 4, plate);
        foreach (var (x, z) in new[] { (-6, 0), (-3, 1), (-5, 3) })
        {
            TemplateKit.Ellipsoid(w, x, 13.1, z, 1.6, .6, 1.6, cookie);
            TemplateKit.Box(w, x, 13, z, x, 13, z, chip);
        }
        // 纸杯使用不透明纸色体素，杯口内保留可单独编辑的可乐。
        for (var y = 12; y <= 16; y++)
        for (var z = -1; z <= 3; z++)
        for (var x = 4; x <= 8; x++)
        {
            var d = (x - 6) * (x - 6) + (z - 1) * (z - 1);
            if (d > 8) continue;
            if (y == 12 || d >= 4) w.Set(new VoxelCoord64(x, y, z), Paint(new Rgba32(235, 225, 202)));
            else if (y <= 15) w.Set(new VoxelCoord64(x, y, z), cola);
        }
        // 后墙壁画。
        TemplateKit.Box(w, -18, 19, -29, -4, 29, -29, wood);
        TemplateKit.Box(w, -17, 20, -28, -5, 28, -28, Paint(new Rgba32(112, 173, 194)));
        TemplateKit.Box(w, -17, 20, -27, -5, 22, -27, Paint(new Rgba32(82, 142, 91)));
        TemplateKit.Ellipsoid(w, -8, 26, -27, 1.5, 1.5, .5, Paint(new Rgba32(244, 199, 99)));
        // 落地风扇：底座、立杆、圆形护网、扇叶和中心电机。
        var fanDark = Metal(new Rgba32(72, 83, 90));
        var fanLight = Paint(new Rgba32(208, 218, 215));
        var blade = Paint(new Rgba32(103, 155, 169));
        TemplateKit.Ellipsoid(w, 20.5, 1.4, -9.5, 4, .8, 3, fanDark);
        TemplateKit.Box(w, 20, 2, -10, 20, 12, -10, fanLight);
        for (var y = 10; y <= 22; y++)
        for (var x = 14; x <= 26; x++)
        {
            var dx = x - 20; var dy = y - 16; var radius = dx * dx + dy * dy;
            if (radius >= 25 && radius <= 37)
                w.Set(new VoxelCoord64(x, y, -10), fanDark);
            else if (radius <= 20 && (Math.Abs(dx) <= 1 || Math.Abs(dy) <= 1))
                w.Set(new VoxelCoord64(x, y, -10), blade);
        }
        TemplateKit.Box(w, 19, 15, -9, 21, 17, -9, fanLight);
        // 电线从风扇底座连接地面插排，插排再连接后墙插座。
        var cable = Paint(new Rgba32(45, 49, 53));
        var socket = Paint(new Rgba32(236, 235, 225));
        TemplateKit.Box(w, 21, 1, -10, 24, 1, -10, cable);
        TemplateKit.Box(w, 24, 1, -21, 24, 1, -10, cable);
        TemplateKit.Box(w, 23, 1, -24, 27, 1, -22, socket);
        TemplateKit.Box(w, 24, 2, -23, 24, 2, -23, cable);
        TemplateKit.Box(w, 26, 2, -23, 26, 2, -23, cable);
        TemplateKit.Box(w, 27, 1, -26, 27, 1, -24, cable);
        TemplateKit.Box(w, 27, 1, -27, 29, 1, -26, cable);
        TemplateKit.Box(w, 29, 1, -29, 29, 3, -27, cable);
        TemplateKit.Box(w, 28, 3, -29, 30, 5, -29, socket);
        TemplateKit.Box(w, 28, 4, -28, 28, 4, -28, cable);
        TemplateKit.Box(w, 30, 4, -28, 30, 4, -28, cable);
        return w;
    }

    private static VoxelValue Optical(byte r, byte g, byte b, byte alpha, ushort slot) =>
        new(new Rgba32(r, g, b, alpha), slot, VoxelFlags.Translucent);

    /// <summary>开放正面的体素康奈尔盒，以顶灯、彩墙和不同材质观察间接光。</summary>
    public static VoxelWorld BuildCornellBox()
    {
        var w = new VoxelWorld();
        var white = Paint(new Rgba32(210, 208, 195));
        var red = Paint(new Rgba32(180, 57, 48));
        var green = Paint(new Rgba32(58, 145, 76));
        TemplateKit.Box(w, -20, 0, -20, 20, 0, 20, white);
        TemplateKit.Box(w, -20, 1, -20, 20, 40, -20, white);
        TemplateKit.Box(w, -20, 1, -19, -20, 40, 20, red);
        TemplateKit.Box(w, 20, 1, -19, 20, 40, 20, green);
        TemplateKit.Box(w, -20, 41, -20, 20, 41, 20, white);
        TemplateKit.Box(w, -6, 40, -6, 6, 40, 6, Glow(new Rgba32(255, 245, 223)));
        TemplateKit.Box(w, -13, 1, -8, -3, 12, 3, white);
        TemplateKit.Ellipsoid(w, 9, 7, 2, 6, 6, 6, Metal(new Rgba32(220, 222, 225)));
        return w;
    }

    /// <summary>棋盘与玻璃、金属球的体素折射和反射实验。</summary>
    public static VoxelWorld BuildSphereOptics()
    {
        var w = new VoxelWorld();
        var light = Paint(new Rgba32(231, 230, 219));
        var dark = Paint(new Rgba32(72, 83, 99));
        for (var x = -24; x <= 24; x++) for (var z = -15; z <= 20; z++)
            w.Set(new VoxelCoord64(x, 0, z), ((x + 24) / 4 + (z + 15) / 4) % 2 == 0 ? light : dark);
        for (var x = -24; x <= 24; x++) for (var y = 1; y <= 20; y++)
            w.Set(new VoxelCoord64(x, y, -15), ((x + 24) / 4 + (y - 1) / 4) % 2 == 0 ? light : dark);
        TemplateKit.Ellipsoid(w, -9, 7, 1, 6, 6, 6, Optical(230, 244, 245, 90, 10));
        TemplateKit.Ellipsoid(w, 9, 7, 1, 6, 6, 6, Metal(new Rgba32(220, 223, 225)));
        return w;
    }

    /// <summary>同一棋盘背景前的三种玻璃，便于直接比较透射。</summary>
    public static VoxelWorld BuildGlassStudy()
    {
        var w = new VoxelWorld();
        var light = Paint(new Rgba32(233, 230, 221));
        var dark = Paint(new Rgba32(67, 83, 105));
        TemplateKit.Box(w, -25, 0, -12, 25, 0, 16, Paint(new Rgba32(160, 151, 137)));
        for (var x = -24; x <= 24; x++) for (var y = 1; y <= 20; y++)
            w.Set(new VoxelCoord64(x, y, -11), ((x + 24) / 3 + (y - 1) / 3) % 2 == 0 ? light : dark);
        var panes = new[] { (X: -17, Color: Optical(240, 248, 250, 80, 10)),
            (X: 0, Color: Optical(234, 240, 242, 140, 11)),
            (X: 17, Color: Optical(231, 152, 67, 170, 12)) };
        foreach (var pane in panes)
        {
            TemplateKit.Box(w, pane.X - 5, 1, -2, pane.X + 5, 1, 1, Paint(Slate));
            TemplateKit.Box(w, pane.X - 4, 2, -1, pane.X + 4, 15, -1, pane.Color);
            TemplateKit.Box(w, pane.X - 5, 2, -1, pane.X - 5, 15, -1, Paint(Timber));
            TemplateKit.Box(w, pane.X + 5, 2, -1, pane.X + 5, 15, -1, Paint(Timber));
        }
        return w;
    }

    /// <summary>同一背景下的薄雾与云朵，保持完整体积供透射计算。</summary>
    /// <summary>
    /// 玻璃与镜面综合展台：同一展厅里并排展示薄板、毛玻璃、厚玻璃砖、透镜球、
    /// 玻璃柱、镜面、拉丝金属、半透明、发光玻璃、双层玻璃、棱镜与多色吸收玻璃等 15 种光学案例
    /// </summary>
    public static VoxelWorld BuildGlassShowcase()
    {
        var w = new VoxelWorld();
        var floor = Paint(new Rgba32(168, 162, 150));
        var wallLight = Paint(new Rgba32(232, 228, 219));
        var wallDark = Paint(new Rgba32(58, 70, 88));
        var stone = Paint(new Rgba32(122, 124, 129));
        var timber = Paint(new Rgba32(126, 86, 54));
        TemplateKit.Box(w, -46, 0, -34, 46, 0, 30, floor);
        for (var x = -45; x <= 45; x++)
        for (var y = 1; y <= 26; y++)
            w.Set(new VoxelCoord64(x, y, -33), ((x + 45) / 3 + (y - 1) / 3) % 2 == 0 ? wallLight : wallDark);
        // 展厅只保留背景墙，左右不封墙，渲染相机可以从正面一次看完所有展品

        static VoxelValue Solid(Rgba32 color, ushort slot) => new(color, slot);
        var clear = new Rgba32(236, 244, 246);
        var frosted = new Rgba32(226, 232, 234);
        var deepGlass = new Rgba32(208, 232, 236);
        var red = new Rgba32(238, 96, 88);
        var green = new Rgba32(96, 202, 138);
        var amber = new Rgba32(240, 170, 70);
        var blue = new Rgba32(96, 148, 232);

        var positions = new (int X, int Z)[]
        {
            (-34, -22), (-17, -22), (0, -22), (17, -22), (34, -22),
            (-34, -2), (-17, -2), (0, -2), (17, -2), (34, -2),
            (-34, 18), (-17, 18), (0, 18), (17, 18), (34, 18),
        };
        for (var index = 0; index < positions.Length; index++)
        {
            var (x, z) = positions[index];
            var slot = (ushort)(20 + index);
            TemplateKit.Box(w, x - 4, 1, z - 4, x + 4, 3, z + 4, stone);
            switch (index)
            {
                case 0: // 透明平板玻璃
                case 1: // 毛玻璃
                case 7: // 半透明玻璃
                    TemplateKit.Box(w, x - 4, 4, z, x + 4, 16, z,
                        Optical(clear.R, clear.G, clear.B, 90, slot));
                    TemplateKit.Box(w, x - 5, 4, z, x - 5, 16, z, timber);
                    TemplateKit.Box(w, x + 5, 4, z, x + 5, 16, z, timber);
                    break;
                case 2: // 厚玻璃砖
                    TemplateKit.Box(w, x - 3, 4, z - 3, x + 3, 12, z + 3,
                        Optical(deepGlass.R, deepGlass.G, deepGlass.B, 200, slot));
                    break;
                case 3: // 玻璃球（透镜）
                    TemplateKit.Ellipsoid(w, x, 10, z, 4, 4, 4, Optical(clear.R, clear.G, clear.B, 70, slot));
                    break;
                case 4: // 玻璃圆柱（棱镜条纹）
                    TemplateKit.Cylinder(w, x, 10, z, 3, 3, 6, Optical(clear.R, clear.G, clear.B, 110, slot));
                    break;
                case 5: // 完美镜面
                case 6: // 拉丝金属
                    TemplateKit.Box(w, x - 4, 4, z, x + 4, 15, z,
                        Solid(new Rgba32(228, 230, 234), slot));
                    break;
                case 8: // 发光玻璃砖
                    TemplateKit.Box(w, x - 3, 4, z - 3, x + 3, 11, z + 3,
                        Solid(new Rgba32(255, 214, 150), slot));
                    break;
                case 9: // 双层玻璃（多次界面）
                    TemplateKit.Box(w, x - 4, 4, z - 2, x + 4, 15, z - 2,
                        Optical(clear.R, clear.G, clear.B, 80, slot));
                    TemplateKit.Box(w, x - 4, 4, z + 2, x + 4, 15, z + 2,
                        Optical(clear.R, clear.G, clear.B, 80, slot));
                    break;
                case 10: // 棱镜阶梯
                    for (var step = 0; step < 6; step++)
                        TemplateKit.Box(w, x - 4 + step, 4, z, x + 4 - step, 5 + step * 2, z,
                            Optical(clear.R, clear.G, clear.B, 120, slot));
                    break;
                case 11: // 红色吸收玻璃
                case 12: // 绿色吸收玻璃
                case 13: // 琥珀吸收玻璃
                case 14: // 蓝色吸收玻璃
                    var color = index switch
                    {
                        11 => red,
                        12 => green,
                        13 => amber,
                        _ => blue,
                    };
                    TemplateKit.Box(w, x - 4, 4, z, x + 4, 16, z,
                        Optical(color.R, color.G, color.B, 110, slot));
                    break;
            }
        }

        return w;
    }

    public static VoxelWorld BuildCloudAndFog()
    {
        var w = new VoxelWorld();
        TemplateKit.Box(w, -24, 0, -18, 24, 0, 18, Paint(new Rgba32(108, 145, 102)));
        for (var x = -23; x <= 23; x++)
        {
            var height = 4 + (int)(TemplateKit.Fbm(x, 2, 47, 3, .07F) * 13);
            TemplateKit.Box(w, x, 1, -17, x, height, -15, Paint(new Rgba32(94, 123, 137)));
        }
        TemplateKit.Box(w, -20, 1, -5, -9, 7, 5, Optical(222, 233, 238, 90, 13));
        TemplateKit.Box(w, -5, 1, -5, 5, 7, 5, Optical(222, 233, 238, 190, 16));
        var cloud = Optical(245, 247, 250, 170, 14);
        TemplateKit.Ellipsoid(w, 10, 17, 0, 9, 4, 5, cloud);
        TemplateKit.Ellipsoid(w, 5, 19, 0, 5, 4, 4, cloud);
        TemplateKit.Ellipsoid(w, 15, 19, 0, 5, 4, 4, cloud);
        return w;
    }

    /// <summary>棋盘池底与不同水深构成的水面展示场景。</summary>
    public static VoxelWorld BuildWaterStudy()
    {
        var w = new VoxelWorld();
        var rim = Paint(new Rgba32(180, 182, 176));
        TemplateKit.Box(w, -23, 0, -17, 23, 0, 17, rim);
        for (var x = -20; x <= 20; x++) for (var z = -14; z <= 14; z++)
        {
            var color = ((x + 20) / 4 + (z + 14) / 4) % 2 == 0
                ? new Rgba32(228, 229, 220) : new Rgba32(67, 111, 142);
            w.Set(new VoxelCoord64(x, 1, z), Paint(color));
        }
        TemplateKit.Box(w, -22, 1, -16, -21, 5, 16, rim);
        TemplateKit.Box(w, 21, 1, -16, 22, 5, 16, rim);
        TemplateKit.Box(w, -20, 1, -16, 20, 5, -15, rim);
        TemplateKit.Box(w, -20, 1, 15, 20, 5, 16, rim);
        var water = Optical(180, 224, 232, 100, 15);
        TemplateKit.Box(w, -20, 2, -14, 0, 3, 14, water);
        TemplateKit.Box(w, 1, 2, -14, 20, 5, 14, water);
        TemplateKit.Box(w, -13, 2, -5, -11, 5, -3, Paint(new Rgba32(216, 139, 78)));
        TemplateKit.Box(w, 11, 2, -5, 13, 7, -3, Paint(new Rgba32(216, 139, 78)));
        return w;
    }

    private static IReadOnlyList<(string Name, string Part, VoxelWorld World)> BuildOpticalParts(
        ProjectTemplateId id, VoxelWorld source)
    {
        var names = id switch
        {
            ProjectTemplateId.CornellBox => new[] { "盒体与漫反射方块", "顶灯", "金属球" },
            ProjectTemplateId.SphereOptics => new[] { "棋盘背景", "玻璃球", "金属球" },
            ProjectTemplateId.GlassStudy => new[] { "环境与棋盘", "透明玻璃", "毛玻璃", "有色玻璃" },
            ProjectTemplateId.GlassShowcase => new[]
            {
                "展厅与棋盘",
                "01 透明薄板玻璃", "02 毛玻璃", "03 厚玻璃砖", "04 玻璃透镜球", "05 玻璃圆柱",
                "06 完美镜面", "07 拉丝金属", "08 半透明玻璃", "09 发光玻璃", "10 双层玻璃",
                "11 棱镜阶梯", "12 红色吸收玻璃", "13 绿色吸收玻璃", "14 琥珀吸收玻璃", "15 蓝色吸收玻璃",
            },
            ProjectTemplateId.CloudAndFog => new[] { "环境与山景", "薄雾", "云朵", "浓雾" },
            _ => new[] { "池底与池边", "水体" },
        };
        var worlds = names.Select(_ => new VoxelWorld()).ToArray();
        var writers = worlds.Select(world => world.CreateBulkWriter()).ToArray();
        foreach (var (coordinate, value) in source.EnumerateOccupied())
        {
            var index = id switch
            {
                ProjectTemplateId.CornellBox => value.MaterialSlot switch { 4 => 1, 3 => 2, _ => 0 },
                ProjectTemplateId.SphereOptics => value.MaterialSlot switch { 10 => 1, 3 => 2, _ => 0 },
                ProjectTemplateId.GlassStudy => value.MaterialSlot switch { 10 => 1, 11 => 2, 12 => 3, _ => 0 },
                ProjectTemplateId.GlassShowcase => value.MaterialSlot is >= 20 and <= 34
                    ? value.MaterialSlot - 19 : 0,
                ProjectTemplateId.CloudAndFog => value.MaterialSlot switch { 13 => 1, 14 => 2, 16 => 3, _ => 0 },
                _ => value.MaterialSlot == 15 ? 1 : 0,
            };
            writers[index].Set(coordinate, value);
        }
        return names.Select((name, index) => (Name: name, Part: name, World: worlds[index]))
            .Where(item => item.World.OccupiedVoxelCount > 0).ToArray();
    }

    /// <summary>
    /// 小屋与花园：石基木墙、双坡红瓦、玻璃窗、门口提灯、
    /// 名木两棵、花圃、矮栅与井台
    /// </summary>
    public static VoxelWorld BuildCottageGarden()
    {
        var world = new VoxelWorld();
        const int seed = 7;
        var grass = Paint(GrassGreen);
        // 起伏草地
        for (var z = -26; z <= 30; z++)
        for (var x = -26; x <= 26; x++)
        {
            var height = 1 + (int)MathF.Round(TemplateKit.Fbm(x, z, seed, 3, .045F) * 2.2F);
            TemplateKit.TerrainColumn(world, x, z, height, (_, _, _) => grass, seed);
        }

        var groundY = 3;
        // 石基地圈梁 + 木墙 + 出檐双坡顶
        TemplateKit.Box(world, -8, groundY, -6, 7, groundY + 1, 5, Paint(Rock), seed);
        for (var y = groundY + 2; y <= groundY + 8; y++)
        {
            TemplateKit.Box(world, -7, y, -5, 6, y, 4, Paint(Plaster), seed);
        }

        // 木立柱骨架
        foreach (var (cx, cz) in new[] { (-7, -5), (6, -5), (-7, 4), (6, 4), (-1, -5), (-1, 4) })
        {
            TemplateKit.Box(world, cx, groundY + 2, cz, cx, groundY + 8, cz, Paint(Timber));
        }

        TemplateKit.GableRoof(world, -8, -6, 7, 5, groundY + 9, 4, Paint(RoofRed), Paint(Slate));
        // 烟囱
        TemplateKit.Box(world, 4, groundY + 10, -3, 5, groundY + 15, -2, Paint(RockDark), seed);
        TemplateKit.Box(world, 4, groundY + 15, -3, 5, groundY + 15, -2, Glow(new Rgba32(255, 158, 74)));

        // 玻璃窗（东墙与南墙）与门
        TemplateKit.Box(world, 7, groundY + 4, -2, 7, groundY + 6, 0, Glass());
        TemplateKit.Box(world, 7, groundY + 3, -2, 7, groundY + 3, 0, Paint(Timber));
        TemplateKit.Box(world, -4, groundY + 4, 5, -2, groundY + 6, 5, Glass());
        TemplateKit.Box(world, 2, groundY + 2, 5, 4, groundY + 6, 5, Paint(Timber));
        world.Set(new VoxelCoord64(3, groundY + 4, 5), Glow(new Rgba32(255, 214, 130)));

        // 门口提灯柱
        TemplateKit.Box(world, 6, groundY + 2, 8, 6, groundY + 5, 8, Paint(Timber));
        TemplateKit.Box(world, 5, groundY + 5, 8, 6, groundY + 6, 8, Glow(new Rgba32(255, 206, 110)));

        // 石板小路：从门口蜿蜒向南
        var pathX = 3;
        for (var z = 6; z <= 28; z++)
        {
            pathX += TemplateKit.Hash(0, 0, z, seed) % 5 == 0 ? (z % 4 == 0 ? 1 : -1) : 0;
            TemplateKit.Box(world, pathX - 1, groundY, z, pathX + 1, groundY, z, Paint(PathGray), seed);
        }

        // 井台
        TemplateKit.Cylinder(world, -14.5, groundY + 1.5, 12.5, 2.2, 2.2, 1.5, Paint(Rock));
        TemplateKit.Cylinder(world, -14.5, groundY + 1.5, 12.5, 1.5, 1.5, 1.2, Water());
        TemplateKit.Box(world, -16, groundY + 3, 12, -16, groundY + 6, 12, Paint(Timber));
        TemplateKit.Box(world, -13, groundY + 3, 12, -13, groundY + 6, 12, Paint(Timber));
        TemplateKit.GableRoof(world, -17, 10, -12, 14, groundY + 7, 2, Paint(Slate));

        // 矮栅沿花园东侧
        for (var z = -20; z <= 24; z += 2)
        {
            TemplateKit.Box(world, 20, groundY, z, 20, groundY + 2, z, Paint(Timber));
            if (z % 4 == 0) TemplateKit.Box(world, 20, groundY + 1, z + 1, 20, groundY + 1, z + 1, Paint(Timber));
        }

        // 名木两棵：西侧阔叶、南侧针叶
        TemplateKit.BroadleafTree(world, -15, groundY, -2, seed + 11, Paint(Bark), Paint(LeafGreen), 2);
        TemplateKit.ConiferTree(world, 12, groundY, 16, Paint(Bark), Paint(LeafDeep), 2);
        TemplateKit.Bush(world, -6, groundY, 12, Paint(LeafGreen), 1.6);
        TemplateKit.Bush(world, 14, groundY, 2, Paint(LeafDeep), 1.2);
        TemplateKit.ScatterFlowers(world, (x, z) =>
        {
            world.TryGet(new VoxelCoord64(x, groundY, z), out _);
            return true;
        }, -18, 18, -18, 22, seed,
            new VoxelValue(AccentRed), new VoxelValue(AccentGold), new VoxelValue(new Rgba32(214, 128, 168)));
        return world;
    }

    /// <summary>
    /// 将小屋花园拆成真正可编辑的模型层。
    /// 坐标保持原世界坐标，因此拆分后重新组合不会产生位移或缩放误差。
    /// </summary>
    public static IReadOnlyList<(string Name, string Part, VoxelWorld World)> BuildCottageParts(VoxelWorld source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parts = new (string Name, string Part, VoxelWorld World)[]
        {
            ("小屋", "房屋", new VoxelWorld()),
            ("左侧树木", "左侧树木", new VoxelWorld()),
            ("右侧树木", "右侧树木", new VoxelWorld()),
            ("石板小路", "小路", new VoxelWorld()),
            ("门口提灯", "提灯", new VoxelWorld()),
            ("花园环境", "花园环境", new VoxelWorld()),
        };

        foreach (var (coordinate, value) in source.EnumerateOccupied())
        {
            var index = coordinate switch
            {
                // 提灯柱与灯笼
                { X: >= 5 and <= 6, Z: 8, Y: >= 5 } => 4,
                // 只把石板本身归入小路，路下的草地留给环境层
                { Y: 3, Z: >= 6, X: >= 0 and <= 8 } when value.Color == PathGray => 3,
                // 树冠和树干按空间区域分层；房屋边界优先于环境
                { X: <= -9, Z: >= -9 and <= 7, Y: >= 3 } => 1,
                { X: >= 9, Z: >= 8 and <= 25, Y: >= 3 } => 2,
                { X: >= -9 and <= 8, Z: >= -7 and <= 6, Y: >= 3 } => 0,
                _ => 5,
            };
            parts[index].World.Set(coordinate, value);
        }

        return parts;
    }

    /// <summary>
    /// 将每个示例模板按可编辑职责拆成独立模型资源。这里保留原世界坐标，
    /// 由场景对象的格变换负责把局部文件重新组装回原位置；因此隐藏、移动或替换
    /// 某个对象不会再影响同一份“完整模型”的其他对象。
    /// </summary>
    public static IReadOnlyList<(string Name, string Part, VoxelWorld World)> BuildParts(
        ProjectTemplateId id, VoxelWorld source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return id switch
        {
            ProjectTemplateId.CornellBox or ProjectTemplateId.SphereOptics or ProjectTemplateId.GlassStudy or
                ProjectTemplateId.GlassShowcase or ProjectTemplateId.CloudAndFog or ProjectTemplateId.WaterStudy =>
                BuildOpticalParts(id, source),
            ProjectTemplateId.WorldOcean or ProjectTemplateId.WorldMist or ProjectTemplateId.WorldSunset or
                ProjectTemplateId.WeatherRain or ProjectTemplateId.WeatherSnow or ProjectTemplateId.WeatherStorm or
                ProjectTemplateId.WeatherHeatHaze => SplitByRegions(source,
                ("岛屿", "岛屿", c => c.Y <= 4),
                ("远方航标", "航标", c => c.X >= 22),
                ("灯塔与树木", "岛上景物", _ => true)),
            ProjectTemplateId.DefaultRoom => SplitByRegions(source,
                ("地板与墙壁", "房间", c => c.Y <= 0 || c.X <= -36 || c.Z <= -30),
                ("壁画", "壁画", c => c.X is >= -18 and <= -4 && c.Y is >= 19 and <= 29 && c.Z is >= -29 and <= -27),
                ("墙壁插座", "插座", c => c.X is >= 28 and <= 30 && c.Y is >= 3 and <= 5 && c.Z is >= -29 and <= -28),
                ("插排与电线", "电源", c => c.X is >= 23 and <= 29 && c.Y <= 3 && c.Z is >= -29 and <= -10),
                ("落地电风扇", "电风扇", c => c.X is >= 14 and <= 26 && c.Y is >= 1 and <= 22 && c.Z is >= -13 and <= -7),
                ("可乐", "可乐", c => c.X is >= 5 and <= 7 && c.Y is >= 13 and <= 15 && c.Z is >= 0 and <= 2),
                ("纸杯", "纸杯", c => c.X is >= 4 and <= 8 && c.Y is >= 12 and <= 16 && c.Z is >= -1 and <= 3),
                ("饼干", "饼干", c => c.X is >= -10 and <= 1 && c.Y is >= 13 and <= 14 && c.Z is >= -4 and <= 6),
                ("碟子", "碟子", c => c.X is >= -10 and <= 1 && c.Y == 12 && c.Z is >= -4 and <= 6),
                ("椅子", "椅子", c => c.Z >= 13),
                ("桌子", "桌子", _ => true)),
            ProjectTemplateId.Showcase => BuildCottageParts(source),
            ProjectTemplateId.MaterialLab => SplitByRegions(source,
                ("环境与地面", "环境", c => c.Y <= 1),
                ("粗糙度阶梯", "粗糙度", c => c.Z is >= -8 and <= -4),
                ("材质样品", "样品", c => c.X is >= -14 and <= 12 && c.Z is >= -3 and <= 10),
                ("水池与灯环", "水池与灯环", _ => true)),
            ProjectTemplateId.PaletteWorkshop => SplitByRegions(source,
                ("展台底座", "底座", c => c.Y <= 1),
                ("色卡看台", "色卡", c => c.Y <= 5),
                ("中心样品", "中心样品", _ => true)),
            ProjectTemplateId.SparseWorld => SplitByRegions(source,
                ("信号塔", "信号塔", c => c.X < -200),
                ("桥梁", "桥梁", c => c.X is >= 200 and <= 500),
                ("灯塔岛", "灯塔岛", _ => true)),
            ProjectTemplateId.LodPlayground => SplitByRegions(source,
                ("完整细节", "LOD 0", c => c.Z > -3),
                ("简化屋顶", "LOD 1", c => c.Z is <= -3 and > -33),
                ("简化体块", "LOD 2", c => c.Z is <= -33 and > -63),
                ("低模轮廓", "LOD 3", c => c.Z is <= -63 and > -93),
                ("最低细节", "LOD 4", _ => true)),
            ProjectTemplateId.ProceduralTerrain => SplitByRegions(source,
                ("西北地形", "西北地形", c => c.X < 0 && c.Z < 0),
                ("东北地形", "东北地形", c => c.X >= 0 && c.Z < 0),
                ("西南地形", "西南地形", c => c.X < 0 && c.Z >= 0),
                ("东南地形与林带", "东南地形与林带", _ => true)),
            ProjectTemplateId.Architecture => SplitByRegions(source,
                ("基座与台阶", "基座", c => c.Y <= 3),
                ("柱廊", "柱廊", c => c.Y is >= 4 and <= 14 && Math.Abs(c.X) >= 9),
                ("后殿与穹顶", "后殿与穹顶", c => c.Z <= -5),
                ("拱门与水池", "拱门与水池", _ => true)),
            ProjectTemplateId.CameraGallery => SplitByRegions(source,
                ("摄影棚地面", "地面", c => c.Y <= 0),
                ("螺旋雕塑", "主体雕塑", c => Math.Abs(c.X) <= 8 && Math.Abs(c.Z) <= 8),
                ("景深参照物", "景深参照物", _ => true)),
            ProjectTemplateId.LargeWorld => SplitByRegions(source,
                ("中央阶梯圣坛", "中央圣坛", c => c.X is >= 400 and <= 624 && c.Z is >= 400 and <= 624),
                ("四角高塔", "四角高塔", c => (Math.Abs(c.X - 160) <= 32 && Math.Abs(c.Z - 160) <= 32) ||
                                                   (Math.Abs(c.X - 832) <= 32 && Math.Abs(c.Z - 160) <= 32) ||
                                                   (Math.Abs(c.X - 160) <= 32 && Math.Abs(c.Z - 832) <= 32) ||
                                                   (Math.Abs(c.X - 832) <= 32 && Math.Abs(c.Z - 832) <= 32)),
                ("高空拱桥", "拱桥", c => c.Y >= 800),
                ("环带街区与地基", "街区与地基", _ => true)),
            ProjectTemplateId.RenderingGallery => SplitByRegions(source,
                ("灯塔", "灯塔", c => c.X <= -35 && c.Z <= -35 && c.Y >= 4),
                ("远景塔楼", "远景塔楼", c => c.X >= 55 && c.Z <= -50 && c.Y >= 3),
                ("码头与船", "码头与船", c => c.X >= 38 && c.Z >= -10 && c.Y >= 5),
                ("玻璃凉亭", "玻璃凉亭", c => Math.Abs(c.X) <= 10 && c.Z is >= 1 and <= 17 && c.Y >= 7),
                ("小镇建筑", "小镇建筑", c => c.X is >= -36 and <= 36 && c.Z is >= -30 and <= -8 && c.Y >= 5),
                ("林间小径与树木", "林间小径与树木", c => c.Z >= 0 && c.X is >= -50 and <= 32 && c.Y >= 5),
                ("海湾地形", "海湾地形", _ => true)),
            ProjectTemplateId.MinecraftValley => SplitByRegions(source,
                ("地形", "湖盆与瀑布", c => c.X is >= -20 and <= 8 && c.Z is >= -30 and <= 15),
                ("山谷西区", "山谷西区", c => c.X < 0),
                ("山谷东区", "山谷东区", _ => true)),
            ProjectTemplateId.MoonbaseOutpost => SplitByRegions(source,
                ("环形坑与着陆坪", "月面地形", c => c.Y <= 2),
                ("气密穹舱", "主基地", c => c.X is >= -12 and <= 12 && c.Z is >= -10 and <= 10),
                ("太阳能阵列", "能源阵列", c => c.X <= -22 || c.X >= 22),
                ("月面车与信标", "探索设备", _ => true)),
            ProjectTemplateId.ClockworkConservatory => SplitByRegions(source,
                ("石质基座与水渠", "基座", c => c.Y <= 2),
                ("玻璃穹顶与机械肋骨", "穹顶", c => c.Y >= 4 && (Math.Abs(c.X) >= 18 || Math.Abs(c.Z) >= 18)),
                ("中央古树", "植物", c => Math.Abs(c.X) <= 9 && Math.Abs(c.Z) <= 9),
                ("花床与齿轮展台", "花园", _ => true)),
            ProjectTemplateId.DesertCaravanserai => SplitByRegions(source,
                ("沙丘与商路", "沙漠地形", c => c.Y <= 5),
                ("驿站围墙与角楼", "主体建筑", c => Math.Abs(c.X) >= 16 || Math.Abs(c.Z) >= 16),
                ("织棚与驼队", "市集", c => c.Z <= -24),
                ("庭院绿洲", "庭院", _ => true)),
            ProjectTemplateId.ProbeGiAtrium => SplitByRegions(source,
                ("地面与花园", "地面", c => c.Y <= 1),
                ("二层回廊与楼梯", "回廊", c => c.Y is >= 16 and <= 19),
                ("天窗与屋顶", "屋顶", c => c.Y >= 34),
                ("外墙与柱列", "墙体", _ => true)),
            ProjectTemplateId.ProbeGiCave => SplitByRegions(source,
                ("洞口岩壁", "洞口", c => c.Z >= 64),
                ("主洞室与石柱", "主洞室", c => c.Z is >= 12 and <= 51),
                ("内室与连接隧道", "内室", c => c.Z is >= 52 and <= 63),
                ("山体岩层", "岩体", _ => true)),
            _ => [("模型", "模型", source)],
        };
    }

    private static IReadOnlyList<(string Name, string Part, VoxelWorld World)> SplitByRegions(
        VoxelWorld source,
        params (string Name, string Part, Func<VoxelCoord64, bool> Matches)[] regions)
    {
        if (regions.Length == 0) return [("模型", "模型", source)];
        var parts = regions.Select(region => (region.Name, region.Part, World: new VoxelWorld())).ToArray();
        var writers = parts.Select(part => part.World.CreateBulkWriter()).ToArray();
        source.TryGetBounds(out var minimum, out var maximum);
        foreach (var (coordinate, value) in source.EnumerateOccupied())
        {
            // 首个对象承担模型局部原点的边界体素，确保场景对象位置仍能准确恢复
            // 整个模板的包围盒原点；这些边界体素本来就属于环境/基座职责。
            var index = coordinate.X == minimum.X || coordinate.Y == minimum.Y || coordinate.Z == minimum.Z ||
                        coordinate.X == maximum.X || coordinate.Y == maximum.Y || coordinate.Z == maximum.Z
                ? 0
                : Array.FindIndex(regions, region => region.Matches(coordinate));
            writers[index < 0 ? regions.Length - 1 : index].Set(coordinate, value);
        }

        return parts.Where(part => part.World.OccupiedVoxelCount > 0)
            .Select(part => (part.Name, part.Part, part.World))
            .ToArray();
    }

    /// <summary>
    /// 材质实验室：粗糙度阶梯、金属/玻璃/发光/水样品台与弧形背景屏
    /// </summary>
    public static VoxelWorld BuildMaterialLab()
    {
        var world = new VoxelWorld();
        const int seed = 21;
        TemplateKit.Box(world, -22, 0, -14, 22, 0, 14, Paint(RockDark), seed);
        // 棋盘地面
        for (var z = -13; z <= 13; z++)
        for (var x = -21; x <= 21; x++)
        {
            if (((x + z) % 4 + 4) % 4 < 2) continue;
            TemplateKit.Box(world, x, 1, z, x, 1, z, Paint(new Rgba32(64, 70, 84)));
        }

        // 粗糙度阶梯：同一红色从镜面到全糙，槽 5..9 由模板材质定义
        var roughnessColors = new[] { new Rgba32(214, 88, 78), new Rgba32(208, 96, 84), new Rgba32(200, 102, 90), new Rgba32(192, 108, 96), new Rgba32(186, 112, 102) };
        for (var step = 0; step < 5; step++)
        {
            var x = -16 + step * 8;
            TemplateKit.Box(world, x - 2, 2, -8, x + 2, 4, -4, new VoxelValue(roughnessColors[step], (ushort)(5 + step)));
            TemplateKit.Box(world, x - 2, 5, -8, x + 2, 5, -4, Paint(Slate));
        }

        // 中央样品柱：金属球、玻璃立方、发光环、水池
        TemplateKit.Ellipsoid(world, -10.5, 8, 5.5, 2.6, 2.6, 2.6, Metal(new Rgba32(208, 196, 158)));
        TemplateKit.Box(world, -1, 6, 3, 4, 9, 6, Glass(150));
        TemplateKit.Box(world, -1, 6, 3, 4, 6, 6, Paint(Slate));
        for (var angle = 0; angle < 24; angle++)
        {
            var theta = angle / 24.0 * Math.PI * 2;
            var lx = (long)Math.Round(10 + Math.Cos(theta) * 3);
            var lz = (long)Math.Round(5 + Math.Sin(theta) * 3);
            TemplateKit.Box(world, lx, 8, lz, lx, 9, lz, Glow(new Rgba32(255, 198, 96)));
        }

        TemplateKit.Cylinder(world, 17.5, 2.5, 5.5, 3.4, 3.4, .4, Paint(Rock));
        TemplateKit.Cylinder(world, 17.5, 2.9, 5.5, 2.9, 2.9, .3, Water());
        // 弧形背景屏
        for (var angle = -60; angle <= 60; angle++)
        {
            var theta = angle / 180.0 * Math.PI;
            var x = (long)Math.Round(Math.Sin(theta) * 19);
            var z = (long)Math.Round(-Math.Cos(theta) * 19);
            TemplateKit.Box(world, x, 2, z, x, 13, z, Paint(new Rgba32(58, 64, 78)), seed);
        }

        return world;
    }

    /// <summary>
    /// 调色板工坊：色卡沿螺旋看台排布，中央为当前选中色的高台
    /// </summary>
    public static VoxelWorld BuildPaletteWorkshop(Rgba32[] palette)
    {
        var world = new VoxelWorld();
        TemplateKit.Cylinder(world, 0, 0, 0, 34, 34, .5, Paint(RockDark), hollow: false);
        var total = Math.Min(VoxelPalette.EntryCount, 128) - 1;
        for (var index = 1; index <= total; index++)
        {
            var theta = (index - 1) / (double)total * Math.PI * 4.6;
            var radius = 7 + (index - 1) / (double)total * 20;
            var x = (int)Math.Round(Math.Cos(theta) * radius);
            var z = (int)Math.Round(Math.Sin(theta) * radius);
            var height = 1 + index % 5;
            var color = palette[index];
            var value = new VoxelValue(color,
                (ushort)(index % 8),
                color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None);
            TemplateKit.Box(world, x - 1, 1, z - 1, x + 1, height, z + 1, value);
        }

        TemplateKit.Cylinder(world, 0, 2, 0, 3.2, 3.2, 1.2, Glow(palette[1]));
        TemplateKit.Ellipsoid(world, .5, 5, .5, 2.2, 2.2, 2.2, Glass());
        return world;
    }

    /// <summary>
    /// 稀疏世界：灯塔岛、桥塔与信号塔三组远距离地标
    /// </summary>
    public static VoxelWorld BuildSparseWorld()
    {
        var world = new VoxelWorld();
        const int seed = 41;
        // 中央灯塔岛
        TemplateKit.Ellipsoid(world, .5, -1, .5, 14, 6, 14, Paint(Rock), hollow: false);
        TemplateKit.Taper(world, .5, .5, 4, 44, 5.5, 2.6, Paint(Plaster), seed);
        for (var ring = 0; ring < 3; ring++)
        {
            TemplateKit.Cylinder(world, .5, 12 + ring * 11, .5, 4.6 - ring, 4.6 - ring, .4, Paint(AccentRed));
        }

        TemplateKit.Cylinder(world, .5, 46, .5, 3.4, 3.4, 1.2, Paint(Slate));
        TemplateKit.Cylinder(world, .5, 46.5, .5, 2.4, 2.4, .9, Glow(new Rgba32(255, 236, 170)));
        // 桥塔（+384）
        foreach (var offset in new[] { -24L, 24L })
        {
            TemplateKit.Box(world, 384 + offset - 3, 0, -8, 384 + offset + 3, 60, 8, Paint(Rock), seed);
            TemplateKit.Box(world, 384 + offset - 1, 60, -3, 384 + offset + 1, 74, 3, Paint(Rock));
        }

        TemplateKit.Box(world, 384 - 30, 46, -2, 384 + 30, 48, 2, Paint(SteelGray()));
        // 信号塔（-384）：三支柱与顶部环形天线
        foreach (var angle in new[] { 0, 120, 240 })
        {
            var theta = angle / 180.0 * Math.PI;
            var px = (long)(-384 + Math.Cos(theta) * 7);
            var pz = (long)(Math.Sin(theta) * 7);
            TemplateKit.Line(world, (px, 0, pz), ((long)Math.Round(-384 + Math.Cos(theta) * 2.5), 52, (long)Math.Round(Math.Sin(theta) * 2.5)), Paint(Slate));
        }

        for (var angle = 0; angle < 16; angle++)
        {
            var theta = angle / 16.0 * Math.PI * 2;
            TemplateKit.Box(world, (long)(-384 + Math.Cos(theta) * 5), 54, (long)(Math.Sin(theta) * 5),
                (long)(-384 + Math.Cos(theta) * 5), 55, (long)(Math.Sin(theta) * 5), Glow(new Rgba32(255, 120, 110)));
        }

        return world;
    }

    private static Rgba32 SteelGray() => new(150, 156, 166);

    /// <summary>
    /// LOD 练习场：同一栋房屋从全细节到方块体逐级简化，沿视线排开
    /// </summary>
    public static VoxelWorld BuildLodPlayground()
    {
        var world = new VoxelWorld();
        const int seed = 55;
        TemplateKit.Box(world, -16, 0, -120, 16, 0, 24, Paint(GrassGreen), seed);
        for (var level = 0; level <= 4; level++)
        {
            var z = 12 - level * 30;
            var y = 1;
            switch (level)
            {
                case 0:
                {
                    TemplateKit.Box(world, -6, y, z - 5, 6, y + 5, z + 5, Paint(Plaster), seed);
                    TemplateKit.GableRoof(world, -7, z - 6, 7, z + 6, y + 6, 3, Paint(RoofRed));
                    TemplateKit.Box(world, -2, y + 1, z + 5, 2, y + 3, z + 5, Glass());
                    TemplateKit.Box(world, 6, y + 1, z - 1, 6, y + 3, z + 1, Glass());
                    TemplateKit.BroadleafTree(world, -10, y, z, seed + level, Paint(Bark), Paint(LeafGreen));
                    break;
                }

                case 1:
                {
                    TemplateKit.Box(world, -6, y, z - 5, 6, y + 5, z + 5, Paint(Plaster), seed);
                    TemplateKit.GableRoof(world, -6, z - 5, 6, z + 5, y + 6, 2, Paint(RoofRed));
                    break;
                }

                case 2:
                {
                    TemplateKit.Box(world, -6, y, z - 5, 6, y + 7, z + 5, Paint(Plaster), seed);
                    break;
                }

                case 3:
                {
                    TemplateKit.Box(world, -5, y, z - 4, 5, y + 6, z + 4, Paint(Limestone), seed);
                    break;
                }

                default:
                {
                    TemplateKit.Box(world, -4, y, z - 3, 4, y + 5, z + 3, Paint(PathGray));
                    break;
                }
            }
        }

        return world;
    }

    /// <summary>
    /// 程序化地形：fbm 山脊 + 冰斗湖 + 沙滩 + 针叶林带
    /// </summary>
    public static VoxelWorld BuildProceduralTerrain()
    {
        var world = new VoxelWorld();
        const int seed = 77;
        const int size = 56;
        const int waterLevel = 7;
        for (var x = -size; x <= size; x++)
        for (var z = -size; z <= size; z++)
        {
            var ridge = 1F - MathF.Abs(TemplateKit.Fbm(x, z, seed + 5, 2, .02F) * 2F - 1F);
            var height = 2 + (int)MathF.Round(
                TemplateKit.Fbm(x, z, seed, 4, .035F) * 14F + ridge * ridge * 12F);
            height = Math.Clamp(height, 1, 30);
            TemplateKit.TerrainColumn(world, x, z, height, (_, y, _) =>
            {
                if (y == height - 1) return height > 24 ? Paint(Snow) : height > waterLevel + 1 ? Paint(GrassGreen) : Paint(Sand);
                if (y >= height - 4) return height > 24 ? Paint(Rock) : height > waterLevel + 1 ? Paint(Soil) : Paint(Sand);
                return Paint(Rock);
            }, seed);
            if (height <= waterLevel)
            {
                for (var y = height; y <= waterLevel; y++)
                {
                    world.Set(new VoxelCoord64(x, y, z), Water());
                }
            }
        }

        // 针叶林带：海拔 9 到 18 的草地随机散布
        for (var z = -size + 4; z <= size - 4; z += 3)
        for (var x = -size + 4; x <= size - 4; x += 3)
        {
            var roll = TemplateKit.Hash(x, 0, z, seed + 9) % 100;
            if (roll > 34) continue;
            var groundY = TemplateKit.FindSurfaceY(world, x, z);
            if (groundY is < 9 or > 18) continue;
            if (!world.TryGet(new VoxelCoord64(x, groundY, z), out var surface) || surface.Color != GrassGreen) continue;
            TemplateKit.ConiferTree(world, x, groundY + 1, z, Paint(Bark), Paint(LeafDeep), roll % 3 == 0 ? 2 : 1);
        }

        return world;
    }

    /// <summary>
    /// 模块化建筑：柱廊庭院、圆拱门、台阶基座、穹顶与镜水池
    /// </summary>
    public static VoxelWorld BuildColonnade()
    {
        var world = new VoxelWorld();
        TemplateKit.Box(world, -22, 0, -18, 22, 0, 18, Paint(GrassGreen));
        // 两级基座
        TemplateKit.Box(world, -20, 1, -16, 20, 2, 16, Paint(Limestone));
        TemplateKit.Box(world, -18, 3, -14, 18, 3, 14, Paint(Limestone));
        // 柱廊：柱基 + 凹槽柱身 + 柱头
        foreach (var x in new[] { -15L, -9L, -3L, 3L, 9L, 15L })
        {
            foreach (var z in new[] { -11L, 11L })
            {
                Column(world, x, z, 4, 12, Paint(Plaster));
            }
        }

        foreach (var z in new[] { -7L, -1L, 5L })
        {
            foreach (var x in new[] { -15L, 15L })
            {
                Column(world, x, z, 4, 12, Paint(Plaster));
            }
        }

        // 檐带
        TemplateKit.Box(world, -17, 13, -13, 17, 14, 13, Paint(Limestone));
        // 后殿与穹顶
        TemplateKit.Box(world, -8, 4, -13, 8, 12, -6, Paint(Plaster));
        TemplateKit.Shell(world, -8, 4, -13, 8, 12, -6, Paint(Plaster));
        TemplateKit.Ellipsoid(world, .5, 14, -9.5, 6.5, 4.6, 4.6, Paint(Copper), hollow: true);
        TemplateKit.Box(world, 0, 18, -10, 0, 19, -10, Glow(new Rgba32(255, 224, 150)));
        // 正面圆拱门
        TemplateKit.Arch(world, 0, -4, 4, 6, 4, 1, Paint(Limestone));
        // 镜水池
        TemplateKit.Box(world, -6, 4, 2, 6, 4, 10, Paint(Slate));
        TemplateKit.Box(world, -5, 5, 3, 5, 5, 9, Water());
        // 台阶
        for (var step = 0; step < 3; step++)
        {
            TemplateKit.Box(world, -5, 1 + step, 14 - step, 5, 1 + step, 15 - step, Paint(Limestone));
        }

        return world;
    }

    private static void Column(VoxelWorld world, long x, long z, long baseY, long topY, VoxelValue value)
    {
        TemplateKit.Box(world, x - 1, baseY, z - 1, x + 1, baseY, z + 1, Paint(Rock));
        for (var y = baseY + 1; y <= topY - 1; y++)
        {
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) == 2) continue;
                world.Set(new VoxelCoord64(x + dx, y, z + dz),
                    value);
            }
        }

        TemplateKit.Box(world, x - 1, topY, z - 1, x + 1, topY, z + 1, Paint(Limestone));
    }

    /// <summary>
    /// 多相机构图室：螺旋雕塑 + 前景/中景/远景参照物
    /// </summary>
    public static VoxelWorld BuildSculptureHall()
    {
        var world = new VoxelWorld();
        const int seed = 99;
        TemplateKit.Cylinder(world, .5, 0, .5, 26, 26, .5, Paint(RockDark), hollow: false);
        // 螺旋上升雕塑
        for (var step = 0; step < 90; step++)
        {
            var theta = step / 90.0 * Math.PI * 5;
            var radius = 3 + step * .07;
            var x = (long)Math.Round(Math.Cos(theta) * radius);
            var z = (long)Math.Round(Math.Sin(theta) * radius);
            var y = 1 + step / 6;
            var color = step % 3 == 0 ? Metal(new Rgba32(216, 186, 130)) : step % 3 == 1 ? Paint(Slate) : Glass(170);
            TemplateKit.Box(world, x - 1, y, z - 1, x + 1, y + 1, z + 1, color);
        }

        TemplateKit.Ellipsoid(world, .5, 17, .5, 2.4, 2.4, 2.4, Glow(new Rgba32(255, 220, 160)));
        // 前景/中景/远景参照柱列
        foreach (var (x, z, height, color) in new (long, long, long, Rgba32)[]
                 {
                     (-16, 14, 5, AccentRed), (16, 14, 5, AccentGold),
                     (-20, -6, 9, Fabric), (20, -6, 9, LeafDeep),
                     (-10, -20, 13, Copper), (10, -20, 13, SteelGray()),
                 })
        {
            TemplateKit.Box(world, x - 1, 1, z - 1, x + 1, height, z + 1, Paint(color), seed);
        }

        return world;
    }

    /// <summary>
    /// 超大型压力场景：1024³ 内的阶梯圣坛核心、四塔、拱桥与城市网格街区，
    /// 全部空心壳控制体量
    /// </summary>
    public static VoxelWorld BuildMegastructure()
    {
        var world = new VoxelWorld();
        const int seed = 123;
        var baseBlue = new Rgba32(66, 105, 156);
        var baseCyan = new Rgba32(104, 142, 184);
        var gold = new Rgba32(232, 170, 76);
        // 地基棋盘
        for (var z = 0; z < 1024; z += 32)
        for (var x = 0; x < 1024; x += 32)
        {
            if ((x / 32 + z / 32) % 2 == 0) continue;
            TemplateKit.Box(world, x, 0, z, x + 31, 0, z + 31, new VoxelValue(baseBlue));
        }

        // 中央阶梯圣坛：八层外圈环带
        for (var layer = 0; layer < 8; layer++)
        {
            var inset = 64 + layer * 26;
            var y = (long)layer * 40;
            var color = layer % 2 == 0 ? new VoxelValue(baseBlue) : new VoxelValue(baseCyan);
            TemplateKit.Box(world, inset, y, inset, 1023 - inset, y, inset + 3, color);
            TemplateKit.Box(world, inset, y, 1020 - inset, 1023 - inset, y, 1023 - inset, color);
            TemplateKit.Box(world, inset, y, inset, inset + 3, y, 1023 - inset, color);
            TemplateKit.Box(world, 1020 - inset, y, inset, 1023 - inset, y, 1023 - inset, color);
        }

        TemplateKit.Shell(world, 448, 320, 448, 575, 640, 575, new VoxelValue(baseBlue), seed);
        TemplateKit.Ellipsoid(world, 512, 700, 512, 96, 96, 96, new VoxelValue(gold), hollow: true);
        // 四角空心塔与金色收尖
        foreach (var (tx, tz) in new[] { (160, 160), (832, 160), (160, 832), (832, 832) })
        {
            TemplateKit.Shell(world, tx - 16, 0, tz - 16, tx + 16, 800, tz + 16, new VoxelValue(baseBlue), seed);
            TemplateKit.Taper(world, tx + .5, tz + .5, 800, 960, 26, 4, new VoxelValue(gold), seed);
            TemplateKit.Box(world, tx - 6, 960, tz - 6, tx + 6, 1023, tz + 6, Glow(new Rgba32(255, 230, 160)));
        }

        // 高空拱桥（四向）
        foreach (var (from, to) in new[]
                 {
                     ((160, 860, 512), (448, 860, 512)),
                     ((575, 860, 512), (832, 860, 512)),
                     ((512, 860, 160), (512, 860, 448)),
                     ((512, 860, 575), (512, 860, 832)),
                 })
        {
            var steps = Math.Abs(to.Item1 - from.Item1) + Math.Abs(to.Item3 - from.Item3);
            for (var step = 0; step <= steps; step += 8)
            {
                var t = step / (double)steps;
                var x = (long)Math.Round(from.Item1 + (to.Item1 - from.Item1) * t);
                var z = (long)Math.Round(from.Item3 + (to.Item3 - from.Item3) * t);
                var sag = (long)(Math.Sin(t * Math.PI) * 40);
                TemplateKit.Box(world, x - 10, 860 - sag, z - 10, x + 10, 860 - sag, z + 10, new VoxelValue(baseCyan), seed);
            }
        }

        // 城市网格街区：环带上的随机高度街区
        for (var block = 0; block < 40; block++)
        {
            var angle = block / 40.0 * Math.PI * 2;
            var radius = 300 + TemplateKit.Hash(block, 0, 0, seed) % 140;
            var cx = (long)(512 + Math.Cos(angle) * radius);
            var cz = (long)(512 + Math.Sin(angle) * radius);
            var height = 40 + TemplateKit.Hash(block, 1, 0, seed) % 180;
            var width = 18 + TemplateKit.Hash(block, 2, 0, seed) % 22;
            TemplateKit.Shell(world, cx - width, 1, cz - width, cx + width, height, cz + width,
                block % 3 == 0 ? new VoxelValue(new Rgba32(232, 170, 76)) : new VoxelValue(baseBlue), seed);
        }

        return world;
    }

    /// <summary>
    /// Minecraft 山谷地形：山脊、湖盆与瀑布、沙岸、花丛与灯笼小径
    /// </summary>
    public static VoxelWorld BuildValleyTerrain()
    {
        var world = new VoxelWorld();
        const int seed = 2024;
        const int size = 52;
        const int waterLevel = 5;
        for (var x = -size; x <= size; x++)
        for (var z = -size; z <= size; z++)
        {
            var baseHeight = 7F + TemplateKit.Fbm(x, z, seed, 4, .045F) * 9F;
            var ridge = 1F - MathF.Abs(TemplateKit.Fbm(x, z, seed + 31, 2, .02F) * 2F - 1F);
            baseHeight += ridge * ridge * 8F;
            var basinX = x + 16;
            var basinZ = z - 12;
            // 湖盆用锥形开挖保证一定见水：高斯下凹会被山脊项淹没，此前种子下湖底全是旱地
            var basinFalloff = MathF.Max(0F, 1F - MathF.Sqrt(basinX * basinX + basinZ * basinZ) / 26F);
            basinFalloff *= basinFalloff;
            if (basinFalloff > 0F)
                baseHeight = MathF.Min(baseHeight - 9F * basinFalloff, waterLevel + 1.4F - 6F * basinFalloff);
            var height = Math.Clamp((int)MathF.Round(baseHeight), 1, 22);
            var underWater = height <= waterLevel;
            for (var y = 0; y < height; y++)
            {
                VoxelValue value;
                if (underWater) value = y >= height - 1 ? Paint(Sand) : y >= height - 3 ? Paint(new Rgba32(150, 138, 116)) : Paint(Rock);
                else if (height <= waterLevel + 2) value = y == height - 1 ? Paint(Sand) : y >= height - 3 ? Paint(Soil) : Paint(Rock);
                else value = y == height - 1 ? height > 17 ? Paint(Rock) : Paint(GrassGreen) : y >= height - 3 ? Paint(Soil) : Paint(Rock);
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(
                    TemplateKit.Jitter(value.Color, x, y, z, seed, .04F), value.MaterialSlot, value.Flags));
            }

            if (underWater)
            {
                for (var y = height; y <= waterLevel; y++)
                {
                    world.Set(new VoxelCoord64(x, y, z), Water());
                }
            }
        }

        // 瀑布：湖盆东岸岩壁，水帘自崖顶高出水面处直落入湖
        for (var y = 1; y <= 8; y++)
        for (var z = 8; z <= 11; z++)
        {
            world.Set(new VoxelCoord64(-3, y, z), Paint(TemplateKit.Jitter(Rock, -3, y, z, seed, .04F)));
            world.Set(new VoxelCoord64(-4, y, z), Water());
        }

        // 花丛与灯笼小径
        TemplateKit.ScatterFlowers(world, (x, z) =>
        {
            var groundY = TemplateKit.FindSurfaceY(world, x, z);
            return groundY > 5 && world.TryGet(new VoxelCoord64(x, groundY, z), out var top) && top.Color == GrassGreen;
        }, -46, 46, -46, 46, seed,
            new VoxelValue(new Rgba32(214, 72, 66)), new VoxelValue(new Rgba32(240, 210, 92)), new VoxelValue(new Rgba32(206, 128, 178)));
        foreach (var (postX, postZ) in new[] { (10, 26), (22, 22), (22, 4), (18, -12), (2, -26) })
        {
            var groundY = TemplateKit.FindSurfaceY(world, postX, postZ);
            if (groundY < 5) continue;
            TemplateKit.Box(world, postX, groundY + 1, postZ, postX, groundY + 4, postZ, Paint(new Rgba32(96, 76, 52)));
            TemplateKit.Box(world, postX, groundY + 5, postZ, postX, groundY + 6, postZ, Glow(new Rgba32(255, 206, 110)));
        }

        return world;
    }

    /// <summary>
    /// 山谷树：阔叶橡树与桦树混合，供分层导出
    /// </summary>
    public static List<VoxelWorld> BuildValleyTrees(VoxelWorld terrain)
    {
        var chosen = new List<(int X, int Z, bool Birch)>();
        for (var z = -46; z <= 46 && chosen.Count < 9; z++)
        for (var x = -46; x <= 46 && chosen.Count < 9; x++)
        {
            var groundY = TemplateKit.FindSurfaceY(terrain, x, z);
            if (groundY is < 8 or > 16) continue;
            if (!terrain.TryGet(new VoxelCoord64(x, groundY, z), out var top) || top.Color != GrassGreen) continue;
            if (chosen.Any(item => Math.Abs(item.X - x) + Math.Abs(item.Z - z) < 13)) continue;
            chosen.Add((x, z, TemplateKit.Hash(x, z, 0, 5) % 4 == 0));
        }

        var trees = new List<VoxelWorld>();
        foreach (var (x, z, birch) in chosen)
        {
            var groundY = TemplateKit.FindSurfaceY(terrain, x, z);
            var tree = new VoxelWorld();
            if (birch)
            {
                TemplateKit.Taper(tree, x + .5, z + .5, groundY + 1, groundY + 7, .7, .5,
                    new VoxelValue(new Rgba32(226, 222, 210)));
                TemplateKit.BroadleafTree(tree, x, groundY + 6, z, 17,
                    new VoxelValue(new Rgba32(226, 222, 210)), new VoxelValue(new Rgba32(146, 186, 96)), 1);
            }
            else
            {
                TemplateKit.BroadleafTree(tree, x, groundY + 1, z, 11, Paint(Bark), Paint(LeafGreen), 2);
            }

            trees.Add(tree);
        }

        return trees;
    }

    /// <summary>月面前哨：陨坑台地、气密舱、穹顶、太阳能阵列、月面车与信标。</summary>
    public static VoxelWorld BuildMoonbaseOutpost()
    {
        var world = new VoxelWorld();
        const int seed = 421;
        var regolith = new Rgba32(142, 145, 151);
        var regolithLight = new Rgba32(184, 184, 178);
        var basalt = new Rgba32(70, 78, 94);
        var hull = new Rgba32(208, 216, 218);
        var orange = new Rgba32(224, 116, 58);
        var cyan = new Rgba32(72, 211, 226);

        // Circular crater shelf with a shallow bowl and deterministic mineral flecks.
        for (var z = -38; z <= 38; z++)
        for (var x = -38; x <= 38; x++)
        {
            var radius = MathF.Sqrt(x * x + z * z);
            if (radius > 38) continue;
            var crater = MathF.Max(0F, 1F - MathF.Abs(radius - 25F) / 13F) * 5F;
            var height = -3 + (int)MathF.Round(crater + TemplateKit.Fbm(x, z, seed, 3, .09F) * 2F);
            for (var y = -5; y <= height; y++)
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(y == height
                    // 月面地形只保留轻微自然色差；较大的逐体素明度抖动会在近景渲染中
                    // 变成明显的棋盘色块，并被强 AO/低角度阳光进一步放大。
                    ? TemplateKit.Jitter(regolithLight, x, y, z, seed, .018F)
                    : TemplateKit.Jitter(basalt, x, y, z, seed, .012F)));
        }

        // Landing apron and a concentric warning ring.
        TemplateKit.Box(world, -19, 1, -18, 19, 1, 18, new VoxelValue(new Rgba32(87, 100, 116)));
        for (var x = -18; x <= 18; x += 2)
        {
            TemplateKit.Box(world, x, 2, -17, x, 2, -17, new VoxelValue(orange));
            TemplateKit.Box(world, x, 2, 17, x, 2, 17, new VoxelValue(orange));
        }
        for (var z = -15; z <= 15; z += 2)
        {
            TemplateKit.Box(world, -18, 2, z, -18, 2, z, new VoxelValue(orange));
            TemplateKit.Box(world, 18, 2, z, 18, 2, z, new VoxelValue(orange));
        }

        // Main pressure module with a visible glazed observation bay.
        TemplateKit.Box(world, -10, 2, -8, 10, 3, 8, Metal(new Rgba32(120, 137, 151)));
        TemplateKit.Shell(world, -9, 4, -7, 9, 11, 7, new VoxelValue(hull), seed);
        TemplateKit.Box(world, -8, 5, 7, 8, 9, 7, Glass(148));
        for (var x = -8; x <= 8; x += 4)
            TemplateKit.Box(world, x, 4, 7, x, 10, 7, Metal(new Rgba32(94, 117, 133)));
        TemplateKit.Box(world, -11, 11, -9, 11, 11, 9, Metal(new Rgba32(91, 112, 128)));
        TemplateKit.Ellipsoid(world, 0, 11, 0, 8, 5, 8, Glass(104), hollow: true);
        for (var y = 12; y <= 15; y += 2)
            TemplateKit.Cylinder(world, .5, y, .5, 7 - (y - 12) * 1.5, 7 - (y - 12) * 1.5,
                .5, Metal(new Rgba32(194, 148, 87)), hollow: true);

        // Twin solar farms, with supports and bright collector-cell bands.
        foreach (var side in new[] { -1, 1 })
        {
            var x0 = side < 0 ? -34 : 23;
            TemplateKit.Box(world, x0, 3, -10, x0 + 11, 3, 10, Metal(new Rgba32(44, 73, 117)));
            for (var x = x0; x <= x0 + 11; x += 3)
                TemplateKit.Box(world, x, 4, -10, x, 4, 10, new VoxelValue(new Rgba32(72, 142, 194)));
            foreach (var z in new[] { -8, 8 })
                TemplateKit.Box(world, x0 + 5, 0, z, x0 + 5, 2, z, Metal(hull));
            TemplateKit.Box(world, x0 + 4, 0, 0, x0 + 6, 8, 0, Metal(new Rgba32(138, 148, 151)));
            TemplateKit.Box(world, x0 + 5, 8, -2, x0 + 5, 9, 2, Glow(cyan));
        }

        // Compact rover: six wheels, orange pressure cabin, head lamps and instrument mast.
        TemplateKit.Box(world, -5, 2, 20, 6, 4, 27, Metal(new Rgba32(157, 165, 164)));
        TemplateKit.Box(world, -3, 5, 21, 4, 8, 26, new VoxelValue(orange));
        TemplateKit.Box(world, -2, 6, 26, 3, 7, 26, Glass(180));
        foreach (var x in new[] { -4, 0, 5 })
        foreach (var z in new[] { 20, 27 })
            TemplateKit.Cylinder(world, x + .5, 3.5, z + .5, 1.4, .7, 1.5,
                Metal(new Rgba32(47, 54, 65)), hollow: true);
        TemplateKit.Box(world, 1, 9, 23, 1, 13, 23, Metal(new Rgba32(153, 158, 160)));
        TemplateKit.Box(world, 0, 13, 22, 2, 14, 24, Glow(new Rgba32(255, 216, 130)));

        // Direction beacons and a sparse scatter of impact stones make the scale legible.
        foreach (var (x, z) in new[] { (-25, -22), (25, -22), (-25, 22), (25, 22) })
        {
            var y = TemplateKit.FindSurfaceY(world, x, z);
            if (y == int.MinValue) continue;
            TemplateKit.Box(world, x, y + 1, z, x, y + 6, z, Metal(hull));
            TemplateKit.Box(world, x - 1, y + 7, z - 1, x + 1, y + 8, z + 1, Glow(cyan));
        }
        foreach (var (x, z, r) in new[] { (-23, 9, 3.2), (26, 13, 4.1), (-31, -5, 2.5), (29, -7, 2.8) })
            TemplateKit.Ellipsoid(world, x, TemplateKit.FindSurfaceY(world, x, z) + 1, z, r, r * .55, r * .8,
                new VoxelValue(TemplateKit.Jitter(new Rgba32(102, 107, 117), x, 0, z, seed, .035F)));
        return world;
    }

    /// <summary>齿轮温室：玻璃穹顶、黄铜环肋、中央古树、花床和环形水渠。</summary>
    public static VoxelWorld BuildClockworkConservatory()
    {
        var world = new VoxelWorld();
        const int seed = 438;
        var brass = new Rgba32(190, 137, 63);
        var glass = Glass(86);
        var darkGreen = new Rgba32(52, 98, 67);
        var water = Water(138);

        TemplateKit.Cylinder(world, .5, 0, .5, 34, 34, .5, Metal(new Rgba32(65, 77, 78)));
        TemplateKit.Cylinder(world, .5, 1, .5, 31, 31, .5, new VoxelValue(new Rgba32(194, 184, 151)));
        TemplateKit.Cylinder(world, .5, 2, .5, 28, 28, .5, new VoxelValue(new Rgba32(221, 211, 180)));
        // Transparent elliptical dome shell with layered brass rings and meridian ribs.
        TemplateKit.Ellipsoid(world, .5, 13, .5, 24, 12, 24, glass, hollow: true);
        for (var y = 4; y <= 22; y += 3)
        {
            var normalized = (y - 13) / 12.0;
            var radius = Math.Sqrt(Math.Max(0, 1 - normalized * normalized)) * 24;
            if (radius > 1) TemplateKit.Cylinder(world, .5, y, .5, radius, radius, .45,
                Metal(brass), hollow: true);
        }
        for (var angle = 0; angle < 360; angle += 30)
        {
            var radians = angle * Math.PI / 180;
            var x = (long)Math.Round(Math.Cos(radians) * 23);
            var z = (long)Math.Round(Math.Sin(radians) * 23);
            TemplateKit.Line(world, (0, 2, 0), (x, 13, z), Metal(new Rgba32(163, 119, 61)), thickness: 1);
        }
        TemplateKit.Cylinder(world, .5, 2, .5, 29, 29, .6, Metal(brass), hollow: true);

        // Raised planting beds arranged as a four-petal garden around the water ring.
        foreach (var (x, z) in new[] { (-13, -13), (13, -13), (-13, 13), (13, 13) })
        {
            TemplateKit.Box(world, x - 6, 3, z - 6, x + 6, 3, z + 6, Metal(new Rgba32(100, 69, 48)));
            TemplateKit.Box(world, x - 5, 4, z - 5, x + 5, 4, z + 5, new VoxelValue(new Rgba32(75, 91, 59)));
            TemplateKit.Bush(world, x, 4, z, new VoxelValue(darkGreen), 4.2);
            foreach (var (dx, dz) in new[] { (-3, -2), (3, 2), (-2, 3), (2, -3) })
                TemplateKit.Box(world, x + dx, 5, z + dz, x + dx, 6, z + dz,
                    new VoxelValue((dx + dz) % 2 == 0 ? AccentGold : new Rgba32(211, 103, 142)));
        }

        // Central specimen tree and a still-water reflecting channel.
        TemplateKit.Cylinder(world, .5, 4, .5, 7, 7, .7, water, hollow: true);
        TemplateKit.Taper(world, .5, .5, 4, 18, 2.2, .8, Paint(Bark), seed);
        TemplateKit.Ellipsoid(world, .5, 20, .5, 8, 6, 8, new VoxelValue(new Rgba32(66, 132, 82)));
        TemplateKit.Ellipsoid(world, -4, 19, 2, 4.5, 4, 5, new VoxelValue(new Rgba32(82, 151, 88)));
        TemplateKit.Ellipsoid(world, 4, 18, -3, 4.5, 3.8, 4.5, new VoxelValue(new Rgba32(56, 117, 78)));
        TemplateKit.Ellipsoid(world, .5, 24, .5, 1.2, 1.2, 1.2, Glow(new Rgba32(255, 225, 151)));

        // Brass clockwork at the four cardinal points and warm floor lamps.
        foreach (var (x, z) in new[] { (0, -25), (25, 0), (0, 25), (-25, 0) })
        {
            TemplateKit.Cylinder(world, x, 5, z, 2.8, 2.8, 1.2, Metal(brass), hollow: true);
            TemplateKit.Cylinder(world, x, 6, z, 1.2, 1.2, 1.4, Metal(new Rgba32(80, 88, 78)));
            TemplateKit.Box(world, x - 1, 8, z - 1, x + 1, 10, z + 1, Glow(new Rgba32(255, 196, 104)));
        }
        foreach (var (x, z) in new[] { (-22, -4), (-4, -22), (22, 4), (4, 22), (-22, 5), (22, -5) })
        {
            TemplateKit.Box(world, x, 3, z, x, 6, z, Metal(new Rgba32(112, 80, 48)));
            TemplateKit.Box(world, x - 1, 7, z - 1, x + 1, 8, z + 1, Glow(new Rgba32(255, 215, 137)));
        }
        return world;
    }

    /// <summary>沙漠绿洲驿站：风蚀沙丘、围合客院、圆拱门、池水、棕榈和织棚。</summary>
    public static VoxelWorld BuildDesertCaravanserai()
    {
        var world = new VoxelWorld();
        const int seed = 457;
        var sandstone = new Rgba32(202, 151, 99);
        var sunlit = new Rgba32(232, 190, 128);
        var shadow = new Rgba32(119, 82, 63);
        var teal = new Rgba32(49, 143, 151);

        // Wind-shaped dune shelf; soil strata remain visible along the cut edges.
        for (var z = -44; z <= 44; z++)
        for (var x = -44; x <= 44; x++)
        {
            var dune = TemplateKit.Fbm(x + 130, z - 70, seed, 4, .045F) * 5F;
            var ridge = MathF.Sin((x + z * .55F) * .09F) * 1.4F;
            var height = 1 + (int)MathF.Round(dune + ridge);
            for (var y = -2; y <= height; y++)
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(y == height
                    ? TemplateKit.Jitter(new Rgba32(220, 185, 128), x, y, z, seed, .06F)
                    : y >= height - 2 ? sandstone : new Rgba32(139, 92, 63)));
        }

        // Courtyard paving, pool, and deep-set colonnade walls.
        TemplateKit.Box(world, -20, 5, -20, 20, 5, 20, new VoxelValue(new Rgba32(219, 190, 146)));
        TemplateKit.Box(world, -8, 6, -7, 8, 6, 8, Metal(new Rgba32(120, 101, 81)));
        TemplateKit.Box(world, -7, 7, -6, 7, 7, 7, Water(160));
        TemplateKit.Box(world, -20, 6, -20, 20, 11, -18, new VoxelValue(sandstone));
        TemplateKit.Box(world, -20, 6, 18, 20, 11, 20, new VoxelValue(sandstone));
        TemplateKit.Box(world, -20, 6, -17, -18, 11, 17, new VoxelValue(sandstone));
        TemplateKit.Box(world, 18, 6, -17, 20, 11, 17, new VoxelValue(sandstone));

        // Repeated arcades, corner watch towers, and striped canopy produce a readable skyline.
        foreach (var x in new[] { -16, -8, 0, 8, 16 })
        {
            TemplateKit.Box(world, x, 6, -17, x + 1, 13, -17, new VoxelValue(sunlit));
            TemplateKit.Box(world, x, 6, 17, x + 1, 13, 17, new VoxelValue(sunlit));
            TemplateKit.Box(world, x, 13, -19, x + 1, 13, -16, Metal(new Rgba32(118, 79, 59)));
            TemplateKit.Box(world, x, 13, 16, x + 1, 13, 19, Metal(new Rgba32(118, 79, 59)));
        }
        foreach (var (x, z) in new[] { (-18, -18), (18, -18), (-18, 18), (18, 18) })
        {
            TemplateKit.Box(world, x - 2, 6, z - 2, x + 2, 18, z + 2, new VoxelValue(sunlit));
            TemplateKit.Box(world, x - 3, 18, z - 3, x + 3, 19, z + 3, Metal(new Rgba32(145, 91, 65)));
            TemplateKit.Box(world, x - 1, 20, z - 1, x + 1, 23, z + 1, Glow(new Rgba32(255, 179, 92)));
        }

        // Four textile shades above the guest stalls, held by slender timber poles.
        foreach (var (x, z, color) in new[]
                 { (-10, -9, new Rgba32(172, 74, 59)), (10, -9, new Rgba32(52, 132, 142)),
                   (-10, 10, new Rgba32(177, 128, 70)), (10, 10, new Rgba32(128, 75, 105)) })
        {
            foreach (var (dx, dz) in new[] { (-4, -3), (4, -3), (-4, 3), (4, 3) })
                TemplateKit.Box(world, x + dx, 6, z + dz, x + dx, 12, z + dz, Paint(Timber));
            for (var dx = -4; dx <= 4; dx++)
                TemplateKit.Box(world, x + dx, 12, z - 3, x + dx, 12, z + 3,
                    new VoxelValue(dx % 2 == 0 ? color : sunlit));
            TemplateKit.Box(world, x - 3, 7, z - 2, x + 3, 9, z + 2, new VoxelValue(shadow));
            TemplateKit.Box(world, x - 2, 10, z - 1, x + 2, 11, z + 1, new VoxelValue(color));
        }

        // Palm-lined approach, date palms, and a low caravan of pack animals.
        for (var z = -38; z <= -23; z += 7)
        foreach (var x in new[] { -27, 27 })
        {
            TemplateKit.Taper(world, x, z, 2, 11, 1.6, .8, Paint(new Rgba32(118, 82, 50)), seed);
            for (var angle = 0; angle < 360; angle += 60)
            {
                var radians = angle * Math.PI / 180;
                var endX = x + (long)Math.Round(Math.Cos(radians) * 7);
                var endZ = z + (long)Math.Round(Math.Sin(radians) * 7);
                TemplateKit.Line(world, (x, 10, z), (endX, 13, endZ),
                    new VoxelValue(new Rgba32(73, 119, 70)), thickness: 2);
            }
            TemplateKit.Ellipsoid(world, x, 13, z, 2.5, 1.8, 2.5, new VoxelValue(new Rgba32(93, 137, 74)));
        }
        foreach (var (x, z) in new[] { (-5, -31), (1, -33), (7, -30) })
        {
            TemplateKit.Ellipsoid(world, x, 6, z, 3.4, 2.4, 2.2, new VoxelValue(new Rgba32(166, 118, 75)));
            TemplateKit.Box(world, x + 2, 7, z, x + 3, 10, z, new VoxelValue(new Rgba32(166, 118, 75)));
            TemplateKit.Box(world, x - 2, 3, z - 1, x - 2, 6, z - 1, Metal(new Rgba32(62, 54, 49)));
            TemplateKit.Box(world, x + 1, 3, z + 1, x + 1, 6, z + 1, Metal(new Rgba32(62, 54, 49)));
            TemplateKit.Box(world, x, 8, z - 2, x, 9, z - 2, Glow(teal));
        }
        // A stepped road and turquoise lanterns guide the eye from foreground to courtyard.
        TemplateKit.Box(world, -3, 5, -42, 3, 5, -21, new VoxelValue(new Rgba32(177, 139, 96)));
        foreach (var z in new[] { -37, -30, -23 })
        foreach (var x in new[] { -5, 5 })
        {
            TemplateKit.Box(world, x, 5, z, x, 9, z, Metal(new Rgba32(93, 67, 50)));
            TemplateKit.Box(world, x - 1, 10, z - 1, x + 1, 12, z + 1, Glow(teal));
        }
        return world;
    }

    /// <summary>
    /// 渲染体验馆：海湾小镇——码头与船、玻璃凉亭、灯塔、林间小径与远景塔楼
    /// </summary>
    public static VoxelWorld BuildHarborTown()
    {
        var world = new VoxelWorld();
        const int seed = 311;
        const int size = 72;
        const int waterLevel = 4;
        // 海湾地形：西岸城镇台地，东南入水
        for (var x = -size; x <= size; x++)
        for (var z = -size; z <= size; z++)
        {
            var slope = (x + size) * .06F;
            var height = 6 + (int)MathF.Round(TemplateKit.Fbm(x, z, seed, 3, .03F) * 4F + slope * .6F);
            var inBay = x - z > 30 || x > 40;
            if (inBay) height = Math.Min(height, 2);
            height = Math.Clamp(height, 1, 12);
            for (var y = 0; y < height; y++)
            {
                VoxelValue value;
                if (height <= waterLevel + 1) value = y == height - 1 ? Paint(Sand) : Paint(new Rgba32(150, 138, 116));
                else value = y == height - 1 ? Paint(GrassGreen) : y >= height - 3 ? Paint(Soil) : Paint(Rock);
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(
                    TemplateKit.Jitter(value.Color, x, y, z, seed, .04F), value.MaterialSlot, value.Flags));
            }

            if (inBay)
            {
                for (var y = height; y <= waterLevel; y++)
                {
                    world.Set(new VoxelCoord64(x, y, z), Water());
                }
            }
        }

        // 码头与船
        var dockY = 5;
        for (var z = -6; z <= 18; z += 8)
        {
            TemplateKit.Box(world, 44, dockY - 3, z, 44, dockY, z, Paint(Timber), seed);
        }

        TemplateKit.Box(world, 40, dockY + 1, -8, 56, dockY + 1, 20, Paint(Timber), seed);
        TemplateKit.Shell(world, 46, dockY + 2, 22, 54, dockY + 6, 32, Paint(new Rgba32(132, 84, 52)), seed);
        TemplateKit.Box(world, 46, dockY + 2, 24, 54, dockY + 3, 30, Paint(new Rgba32(164, 116, 70)));
        TemplateKit.Box(world, 49, dockY + 7, 26, 50, dockY + 16, 27, Paint(Timber));
        TemplateKit.Box(world, 44, dockY + 12, 20, 56, dockY + 12, 34, new VoxelValue(new Rgba32(0, 0, 0, 0)));
        TemplateKit.Box(world, 50, dockY + 8, 26, 50, dockY + 15, 32, Paint(Plaster));

        // 城镇台地：六栋小屋沿路排布
        var houseX = -30;
        foreach (var roofColor in new[] { RoofRed, Slate, Copper, RoofRed, Slate, Fabric })
        {
            House(world, houseX, -20, roofColor, seed);
            houseX += 14;
        }

        TemplateKit.Box(world, -34, 6, -8, 12, 6, -6, Paint(PathGray), seed);

        // 玻璃凉亭（镇中心广场）
        TemplateKit.Box(world, -8, 7, 2, 8, 7, 16, Paint(Limestone), seed);
        foreach (var (px, pz) in new[] { (-7, 3), (7, 3), (-7, 15), (7, 15), (0, 3), (0, 15), (-7, 9), (7, 9) })
        {
            TemplateKit.Box(world, px, 8, pz, px, 13, pz, Metal(new Rgba32(196, 188, 168)));
        }

        TemplateKit.Box(world, -8, 14, 2, 8, 14, 16, Glass(120));
        TemplateKit.Box(world, -8, 15, 2, 8, 15, 16, Paint(Slate));
        TemplateKit.Cylinder(world, .5, 16.5, 9.5, 2.2, 2.2, .5, Glow(new Rgba32(255, 226, 170)));

        // 灯塔（北岬角）
        TemplateKit.Ellipsoid(world, -52.5, 4, -52.5, 16, 8, 16, Paint(Rock), hollow: false);
        TemplateKit.Taper(world, -52.5, -52.5, 10, 40, 4.4, 2.6, Paint(Plaster), seed);
        TemplateKit.Box(world, -56, 34, -56, -49, 35, -49, Paint(AccentRed));
        TemplateKit.Cylinder(world, -52.5, 41, -52.5, 3.2, 3.2, 1.6, Paint(Slate));
        TemplateKit.Cylinder(world, -52.5, 41.5, -52.5, 2.2, 2.2, 1.2, Glow(new Rgba32(255, 240, 190)));

        // 远景塔楼（东地平线）
        TemplateKit.Taper(world, 68.5, -60.5, 3, 34, 7, 3.4, Paint(Limestone), seed);
        TemplateKit.GableRoof(world, 62, -66, 75, -55, 35, 6, Paint(RoofRed));
        TemplateKit.Box(world, 67, 41, -62, 70, 44, -59, Glass(140));

        // 林间小径与树
        for (var step = 0; step <= 40; step++)
        {
            var px = -36 + step;
            var pz = (int)(-2 + MathF.Sin(step * .22F) * 8F);
            var groundY = TemplateKit.FindSurfaceY(world, px, pz);
            if (groundY < waterLevel) continue;
            TemplateKit.Box(world, px - 1, groundY + 1, pz - 1, px + 1, groundY + 1, pz + 1, Paint(PathGray), seed);
        }

        foreach (var (tx, tz, conifer) in new[] { (-44, 14, false), (-18, 26, true), (6, 34, false), (26, 30, true), (-40, 34, true), (14, 44, false), (-8, 46, true) })
        {
            var groundY = TemplateKit.FindSurfaceY(world, tx, tz);
            if (groundY < waterLevel + 1) continue;
            if (conifer) TemplateKit.ConiferTree(world, tx, groundY + 1, tz, Paint(Bark), Paint(LeafDeep), 2);
            else TemplateKit.BroadleafTree(world, tx, groundY + 1, tz, seed + tx, Paint(Bark), Paint(LeafGreen), 2);
        }

        TemplateKit.ScatterFlowers(world, (x, z) =>
        {
            var groundY = TemplateKit.FindSurfaceY(world, x, z);
            return groundY > waterLevel && world.TryGet(new VoxelCoord64(x, groundY, z), out var top) && top.Color == GrassGreen;
        }, -40, 20, 4, 40, seed,
            new VoxelValue(new Rgba32(214, 72, 66)), new VoxelValue(new Rgba32(240, 210, 92)));
        return world;
    }

    private static void House(VoxelWorld world, int x, int z, Rgba32 roofColor, int seed)
    {
        var groundY = TemplateKit.FindSurfaceY(world, x + 3, z + 3);
        if (groundY < 4) groundY = 6;
        TemplateKit.Box(world, x, groundY + 1, z, x + 7, groundY + 2, z + 6, Paint(Rock), seed);
        TemplateKit.Box(world, x + 1, groundY + 3, z + 1, x + 6, groundY + 7, z + 5, Paint(Plaster), seed);
        TemplateKit.GableRoof(world, x, z, x + 7, z + 6, groundY + 8, 3, Paint(roofColor), Paint(Slate));
        TemplateKit.Box(world, x + 3, groundY + 3, z + 5, x + 4, groundY + 5, z + 6, Paint(Timber));
        TemplateKit.Box(world, x + 1, groundY + 4, z + 3, x + 2, groundY + 6, z + 3, Glass());
        TemplateKit.Box(world, x + 6, groundY + 4, z + 2, x + 6, groundY + 6, z + 4, Glass());
    }

    /// <summary>
    /// 探针 GI 中庭：两层回廊围出天井，顶层只留一个方形天窗，
    /// 室内不放任何发光体素，间接光只能靠探针体积与体素间接光补上
    /// </summary>
    public static VoxelWorld BuildProbeAtrium()
    {
        var w = new VoxelWorld();
        var wall = Paint(Plaster);
        var trim = Paint(Limestone);
        var floor = Paint(Slate);
        var rail = Paint(Copper);
        var bench = Paint(Timber);
        // 地面：石板为主，四边走一圈浅色收边
        TemplateKit.Box(w, -30, 0, -30, 30, 0, 30, floor, 17);
        TemplateKit.Box(w, -30, 0, -30, 30, 0, -28, trim);
        TemplateKit.Box(w, -30, 0, 28, 30, 0, 30, trim);
        TemplateKit.Box(w, -30, 0, -30, -28, 0, 30, trim);
        TemplateKit.Box(w, 28, 0, -30, 30, 0, 30, trim);
        // 外墙：厚两格，四面高度到 34；窗洞随后统一挖出
        TemplateKit.Box(w, -30, 1, -30, 30, 34, -29, wall, 23);
        TemplateKit.Box(w, -30, 1, 29, 30, 34, 30, wall, 23);
        TemplateKit.Box(w, -30, 1, -28, -29, 34, 28, wall, 23);
        TemplateKit.Box(w, 29, 1, -28, 30, 34, 28, wall, 23);
        // 顶层：中间留 18×18 天窗，其余封顶
        TemplateKit.Box(w, -30, 35, -30, 30, 35, -9, trim);
        TemplateKit.Box(w, -30, 35, 9, 30, 35, 30, trim);
        TemplateKit.Box(w, -30, 35, -8, -9, 35, 8, trim);
        TemplateKit.Box(w, 9, 35, -8, 30, 35, 8, trim);
        // 二层回廊：中间留 26×26 的井口，靠墙一圈承台
        TemplateKit.Box(w, -28, 16, -28, 28, 17, -13, trim, 11);
        TemplateKit.Box(w, -28, 16, 13, 28, 17, 28, trim, 11);
        TemplateKit.Box(w, -28, 16, -12, -13, 17, 12, trim, 11);
        TemplateKit.Box(w, 13, 16, -12, 28, 17, 12, trim, 11);
        // 回廊栏杆：沿井口一圈一格高一格栏杆，中间隔出通行口
        foreach (var coordinate in new[]
                 {
                     (-13, 17), (13, 17), (-13, -17), (13, -17),
                 })
        {
            TemplateKit.Box(w, coordinate.Item1, 18, coordinate.Item2, coordinate.Item1, 19, coordinate.Item2, rail);
        }
        for (var t = -12; t <= 12; t++)
        {
            if (Math.Abs(t) <= 3) continue;
            TemplateKit.Box(w, t, 18, -13, t, 19, -13, rail);
            TemplateKit.Box(w, t, 18, 13, t, 19, 13, rail);
            TemplateKit.Box(w, -13, 18, t, -13, 19, t, rail);
            TemplateKit.Box(w, 13, 18, t, 13, 19, t, rail);
        }
        // 四角柱与中柱：从地面直通二层承台
        foreach (var (x, z) in new[] { (-26, -26), (26, -26), (-26, 26), (26, 26), (-26, 0), (26, 0), (0, -26), (0, 26) })
        {
            TemplateKit.Box(w, x - 1, 1, z - 1, x + 1, 15, z + 1, trim, 31);
        }
        // 折返楼梯：二层西侧上到回廊
        for (var step = 0; step < 8; step++)
        {
            var y = 1 + step * 2;
            TemplateKit.Box(w, 18, y, -4, 22, y, 4, trim);
            TemplateKit.Box(w, 18, y, -4, 18, y + 1, 4, wall);
        }
        // 底层与二层共用高窗：三面墙各开四个，天光从侧面进到回廊底部
        foreach (var (centerX, centerZ) in new[]
                 {
                     (-18, -29), (-6, -29), (6, -29), (18, -29),
                     (-18, 29), (-6, 29), (6, 29), (18, 29),
                 })
        {
            for (var depth = 0; depth <= 1; depth++)
            for (var y = 3; y <= 26; y++)
            for (var side = -3; side <= 3; side++)
            {
                var offset = centerZ < 0 ? -30 + depth : 30 - depth;
                w.Remove(new VoxelCoord64(centerX + side, y, offset));
            }
        }
        foreach (var centerZ in new[] { -12, 0, 12 })
        {
            for (var depth = 0; depth <= 1; depth++)
            for (var y = 3; y <= 26; y++)
            for (var side = -3; side <= 3; side++)
            {
                w.Remove(new VoxelCoord64(-30 + depth, y, centerZ + side));
                w.Remove(new VoxelCoord64(30 - depth, y, centerZ + side));
            }
        }
        // 天井下挂四组浅色吊板，让落下的天光在回廊上分层
        foreach (var (x, z) in new[] { (-14, -14), (14, -14), (-14, 14), (14, 14) })
        {
            TemplateKit.Box(w, x, 26, z, x + 1, 26, z + 1, trim);
            TemplateKit.Box(w, x, 27, z, x + 1, 34, z + 1, wall);
        }
        // 底层长凳与花池，作为间接光的深浅参照
        TemplateKit.Box(w, -6, 1, -6, 6, 1, 6, bench, 13);
        TemplateKit.Box(w, -5, 2, -5, 5, 2, 5, Paint(Soil));
        TemplateKit.Ellipsoid(w, 0, 4, 0, 4, 3, 4, Paint(LeafGreen));
        return w;
    }

    /// <summary>
    /// 间接光洞穴：只有洞口进光，主洞室、连接隧道与内室层层加深，
    /// 全程不点灯，靠实时体素间接光显示明暗衰减
    /// </summary>
    public static VoxelWorld BuildProbeCave()
    {
        var w = new VoxelWorld();
        var rock = Paint(Rock);
        var rockDark = Paint(RockDark);
        var sand = Paint(Sand);
        // 先堆出整块山体
        TemplateKit.Box(w, -20, 0, 0, 20, 24, 72, rock, 41);
        TemplateKit.Box(w, -20, 24, 0, 20, 26, 20, rockDark, 43);
        // 主洞室：大椭球掏空
        CarveEllipsoid(w, 0, 11, 26, 15, 8, 18);
        // 内室：更小的椭球
        CarveEllipsoid(w, 0, 9, 56, 9, 7, 9);
        // 连接主洞室与内室的隧道
        for (var z = 30; z <= 50; z++)
        for (var y = 3; y <= 17; y++)
        for (var x = -6; x <= 6; x++)
        {
            if (x * x + (y - 9) * (y - 9) <= 30) w.Remove(new VoxelCoord64(x, y, z));
        }
        // 洞口：向外扩成喇叭口
        CarveEllipsoid(w, 0, 9, 72, 8, 7, 6);
        CarveEllipsoid(w, 0, 10, 76, 10, 8, 5);
        // 洞底铺沙，与岩石形成明暗参照
        for (var z = 12; z <= 68; z++)
        for (var x = -14; x <= 14; x++)
        {
            if (w.TryGet(new VoxelCoord64(x, 3, z), out _) || !w.TryGet(new VoxelCoord64(x, 2, z), out _)) continue;
            w.Set(new VoxelCoord64(x, 2, z), sand);
        }
        // 石笋与钟乳：高度随机，制造竖直方向的遮蔽层次
        foreach (var (x, z, height, up) in new[]
                 {
                     (-9, 20, 6, true), (7, 24, 8, false), (-4, 33, 5, true),
                     (10, 40, 7, true), (-11, 45, 6, false), (3, 51, 4, true),
                 })
        {
            var baseY = up ? 3 : 18;
            for (var step = 0; step <= height; step++)
            {
                var y = up ? baseY + step : baseY - step;
                TemplateKit.Box(w, x, y, z, x + 1, y, z + 1, up ? rock : rockDark);
            }
        }
        // 洞顶开一个通到山脊的竖井，主洞室深处因此有一束天光，
        // 周围的间接光衰减才有参照
        for (var y = 6; y <= 30; y++)
        for (var z = 52; z <= 58; z++)
        for (var x = 2; x <= 8; x++)
        {
            if ((x - 5) * (x - 5) + (z - 55) * (z - 55) > 9) continue;
            w.Remove(new VoxelCoord64(x, y, z));
        }
        // 洞顶垂下两根石柱，让主洞室的光线被切出明显的明暗边界
        TemplateKit.Box(w, -2, 12, 22, 0, 19, 22, rockDark, 47);
        TemplateKit.Box(w, -2, 8, 46, 0, 16, 46, rockDark, 53);
        return w;
    }

    /// <summary>
    /// 掏空一个椭球区域，用于把实心山体挖成洞穴
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="centerX">中心 X</param>
    /// <param name="centerY">中心 Y</param>
    /// <param name="centerZ">中心 Z</param>
    /// <param name="radiusX">X 半径</param>
    /// <param name="radiusY">Y 半径</param>
    /// <param name="radiusZ">Z 半径</param>
    private static void CarveEllipsoid(VoxelWorld world, double centerX, double centerY, double centerZ,
        double radiusX, double radiusY, double radiusZ)
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
            world.Remove(new VoxelCoord64(x, y, z));
        }
    }
}
