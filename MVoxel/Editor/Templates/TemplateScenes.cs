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
    /// 光追工作室：暗室、灯槽、玻璃砖墙、材质球与检字格地面
    /// </summary>
    public static VoxelWorld BuildRayTracingStudio()
    {
        var world = new VoxelWorld();
        const int seed = 63;
        var wall = Paint(new Rgba32(40, 44, 56));
        TemplateKit.Box(world, -26, 0, -20, 26, 0, 20, wall, seed);
        TemplateKit.Shell(world, -26, 1, -20, 26, 17, 20, wall, seed);
        // 挖空内部
        TemplateKit.Box(world, -25, 1, -19, 25, 16, 19, new VoxelValue(new Rgba32(0, 0, 0, 0)));
        TemplateKit.Shell(world, -26, 1, -20, 26, 1, 20, wall, seed);
        // 顶部灯槽
        TemplateKit.Box(world, -20, 16, -3, 20, 16, 3, Glow(new Rgba32(255, 226, 168)));
        // 侧窗光带
        TemplateKit.Box(world, -26, 6, -8, -26, 12, 8, Glow(new Rgba32(198, 224, 255)));
        // 玻璃砖墙
        for (var column = 0; column < 4; column++)
        {
            TemplateKit.Box(world, 6 + column * 3, 1, -14, 8 + column * 3, 10, -12, Glass(140));
        }

        // 材质球三件套
        TemplateKit.Ellipsoid(world, -14.5, 4, 6, 3.2, 3.2, 3.2, Metal(new Rgba32(214, 178, 120)));
        TemplateKit.Ellipsoid(world, -6.5, 4, -4, 3.0, 3.0, 3.0, Glass(200));
        TemplateKit.Ellipsoid(world, 2.5, 3.5, 8, 2.8, 2.8, 2.8, Paint(new Rgba32(196, 88, 76), seed));
        // 棋盘地面
        for (var z = -18; z <= 18; z++)
        for (var x = -24; x <= 24; x++)
        {
            if (((x + z) % 6 + 6) % 6 is < 3 and not 0) continue;
            TemplateKit.Box(world, x, 1, z, x, 1, z, Paint(new Rgba32(58, 62, 74)), seed);
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
        const int seed = 88;
        TemplateKit.Box(world, -22, 0, -18, 22, 0, 18, Paint(GrassGreen), seed);
        // 两级基座
        TemplateKit.Box(world, -20, 1, -16, 20, 2, 16, Paint(Limestone), seed);
        TemplateKit.Box(world, -18, 3, -14, 18, 3, 14, Paint(Limestone), seed);
        // 柱廊：柱基 + 凹槽柱身 + 柱头
        foreach (var x in new[] { -15L, -9L, -3L, 3L, 9L, 15L })
        {
            foreach (var z in new[] { -11L, 11L })
            {
                Column(world, x, z, 4, 12, Paint(Plaster), seed);
            }
        }

        foreach (var z in new[] { -7L, -1L, 5L })
        {
            foreach (var x in new[] { -15L, 15L })
            {
                Column(world, x, z, 4, 12, Paint(Plaster), seed);
            }
        }

        // 檐带
        TemplateKit.Box(world, -17, 13, -13, 17, 14, 13, Paint(Limestone), seed);
        // 后殿与穹顶
        TemplateKit.Box(world, -8, 4, -13, 8, 12, -6, Paint(Plaster), seed);
        TemplateKit.Shell(world, -8, 4, -13, 8, 12, -6, Paint(Plaster), seed);
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

    private static void Column(VoxelWorld world, long x, long z, long baseY, long topY, VoxelValue value, int seed)
    {
        TemplateKit.Box(world, x - 1, baseY, z - 1, x + 1, baseY, z + 1, Paint(Rock));
        for (var y = baseY + 1; y <= topY - 1; y++)
        {
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) == 2) continue;
                world.Set(new VoxelCoord64(x + dx, y, z + dz),
                    new VoxelValue(TemplateKit.Jitter(value.Color, (int)(x + dx), (int)y, (int)(z + dz), seed),
                        value.MaterialSlot, value.Flags));
            }
        }

        TemplateKit.Box(world, x - 1, topY, z - 1, x + 1, topY, z + 1, Paint(Limestone), seed);
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
                    TemplateKit.Jitter(value.Color, x, y, z, seed), value.MaterialSlot, value.Flags));
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
            world.Set(new VoxelCoord64(-3, y, z), Paint(TemplateKit.Jitter(Rock, -3, y, z, seed)));
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
                    TemplateKit.Jitter(value.Color, x, y, z, seed), value.MaterialSlot, value.Flags));
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
}
