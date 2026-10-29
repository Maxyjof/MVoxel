using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 仙域植被库：松、竹、樱、冰晶与火山焦木，
/// 每棵树的形态由坐标哈希决定，同一位置永远长出同一棵树
/// </summary>
internal static class XianxiaFlora
{
    /// <summary>云海体素，按坐标做厚薄与明暗变化</summary>
    /// <param name="x">世界 X</param>
    /// <param name="y">世界 Y</param>
    /// <param name="z">世界 Z</param>
    /// <param name="fade">顶面淡化系数，0 到 1</param>
    /// <returns>云雾体素</returns>
    public static VoxelValue CloudBody(int x, int y, int z, float fade)
    {
        var hash = TemplateKit.Hash(x, y, z, 2203) % 19;
        var alpha = (byte)Math.Clamp((int)((96 + hash * 4) * fade), 40, 220);
        var tint = hash < 9 ? XianxiaPalette.Cloud : XianxiaPalette.MoonWhite;
        return new VoxelValue(new Rgba32(tint.R, tint.G, tint.B, alpha),
            XianxiaPalette.SlotCloud, VoxelFlags.Translucent);
    }

    /// <summary>秋色乔木：枫红或银杏金的伞形林冠</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">树根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">树根 Z</param>
    /// <param name="seed">形态种子</param>
    /// <param name="accentShift">0 为银杏金，1 为枫红</param>
    public static void AutumnTree(VoxelWorld world, int x, int y, int z, int seed, int accentShift)
    {
        var trunk = 5 + TemplateKit.Hash(x, 0, z, seed) % 3;
        TemplateKit.Taper(world, x + .5, z + .5, y, y + trunk, 1.2, .6,
            XianxiaPalette.Solid(XianxiaPalette.Bark));
        var canopy = accentShift == 0 ? XianxiaPalette.AutumnGold : XianxiaPalette.MapleRed;
        var highlight = accentShift == 0 ? XianxiaPalette.SilkWhite : XianxiaPalette.LotusPink;
        var canopyY = y + trunk + 1;
        TemplateKit.Ellipsoid(world, x + .5, canopyY, z + .5, 3.4, 2.2, 3.4,
            XianxiaPalette.Solid(canopy));
        TemplateKit.Ellipsoid(world, x + 2.1, canopyY - .8, z - .9, 2.2, 1.7, 2.2,
            XianxiaPalette.Solid(XianxiaPalette.Blend(canopy, XianxiaPalette.AutumnGold, .35F)));
        TemplateKit.Ellipsoid(world, x - 1.9, canopyY + .4, z + 1.2, 1.9, 1.5, 1.9,
            XianxiaPalette.Solid(XianxiaPalette.Blend(canopy, highlight, .25F)));
    }

    /// <summary>水面与湖水体素，按坐标做细微色阶，避免大片死板同色</summary>
    /// <param name="x">世界 X</param>
    /// <param name="y">世界 Y</param>
    /// <param name="z">世界 Z</param>
    /// <returns>水体素</returns>
    public static VoxelValue WaterBody(int x, int y, int z)
    {
        var ripple = TemplateKit.Hash(x, y, z, 2113) % 7;
        var alpha = (byte)(150 + ripple * 6);
        var tint = ripple < 3 ? XianxiaPalette.SeaShallow : XianxiaPalette.SeaDeep;
        return new VoxelValue(new Rgba32(tint.R, tint.G, tint.B, alpha),
            XianxiaPalette.SlotWater, VoxelFlags.Translucent);
    }

    /// <summary>松树：直干加三层收分针冠</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">树根 X</param>
    /// <param name="groundY">地面高</param>
    /// <param name="z">树根 Z</param>
    /// <param name="seed">形态种子</param>
    /// <param name="scale">尺度</param>
    public static void PineTree(VoxelWorld world, int x, int groundY, int z, int seed, int scale = 1)
    {
        var height = (5 + TemplateKit.Hash(x, 0, z, seed) % 4) * scale;
        TemplateKit.Taper(world, x + .5, z + .5, groundY, groundY + height,
            .75 * scale, .3 * scale, XianxiaPalette.Solid(XianxiaPalette.Bark));
        for (var layer = 0; layer < 3; layer++)
        {
            var radius = (3.4 - layer * .95) * scale;
            var baseY = groundY + 2 + layer * Math.Max(1, (int)(2.1 * scale));
            var topY = baseY + Math.Max(2, (int)(3.2 * scale)) - layer;
            TemplateKit.Taper(world, x + .5, z + .5, baseY, topY, radius, .3 * scale,
                XianxiaPalette.Solid(layer == 1 ? XianxiaPalette.PineDeep : XianxiaPalette.Bamboo));
        }

        world.Set(new VoxelCoord64(x, groundY + height + 1, z),
            XianxiaPalette.Solid(XianxiaPalette.Bamboo));
    }

    /// <summary>樱花树：短壮主干加三团粉色冠</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">树根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">树根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void CherryTree(VoxelWorld world, int x, int y, int z, int seed)
    {
        var trunk = 4 + TemplateKit.Hash(x, 0, z, seed) % 3;
        TemplateKit.Taper(world, x + .5, z + .5, y, y + trunk, 1.0, .55,
            XianxiaPalette.Solid(XianxiaPalette.Bark));
        var canopyY = y + trunk + 1;
        TemplateKit.Ellipsoid(world, x + .5, canopyY, z + .5, 3.1, 2.1, 3.1,
            XianxiaPalette.Solid(XianxiaPalette.LotusPink));
        TemplateKit.Ellipsoid(world, x + 1.9, canopyY - .6, z + .4, 2.1, 1.5, 2.1,
            XianxiaPalette.Solid(XianxiaPalette.CherryPink));
        TemplateKit.Ellipsoid(world, x - 1.7, canopyY - .3, z - 1.3, 1.9, 1.4, 1.9,
            XianxiaPalette.Solid(XianxiaPalette.CherryPink));
        world.Set(new VoxelCoord64(x, canopyY + 2, z), XianxiaPalette.Solid(XianxiaPalette.SilkWhite));
    }

    /// <summary>阔叶树：粗干加不规则冠球</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">树根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">树根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void BroadleafTree(VoxelWorld world, int x, int y, int z, int seed)
    {
        var trunk = 5 + TemplateKit.Hash(x, 0, z, seed) % 3;
        TemplateKit.Taper(world, x + .5, z + .5, y, y + trunk, 1.1, .6,
            XianxiaPalette.Solid(XianxiaPalette.Bark));
        var canopyY = y + trunk + 1;
        TemplateKit.Ellipsoid(world, x + .5, canopyY, z + .5, 3.6, 2.4, 3.6,
            XianxiaPalette.Solid(XianxiaPalette.Bamboo));
        TemplateKit.Ellipsoid(world, x + 2.2, canopyY - .7, z - 1.1, 2.2, 1.6, 2.2,
            XianxiaPalette.Solid(XianxiaPalette.GrassLight));
        TemplateKit.Ellipsoid(world, x - 2.1, canopyY - .4, z + 1.4, 2.0, 1.5, 2.0,
            XianxiaPalette.Solid(XianxiaPalette.PineDeep));
    }

    /// <summary>竹林：一簇细高竹竿与顶部竹叶</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">中心 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">中心 Z</param>
    /// <param name="seed">形态种子</param>
    public static void BambooCluster(VoxelWorld world, int x, int y, int z, int seed)
    {
        var count = 10 + TemplateKit.Hash(x, 0, z, seed) % 12;
        for (var stalk = 0; stalk < count; stalk++)
        {
            var hash = TemplateKit.Hash(x, stalk, z, seed + stalk * 37);
            var ox = hash % 9 - 4;
            var oz = hash / 9 % 9 - 4;
            var height = 7 + hash / 81 % 8;
            var baseX = x + ox;
            var baseZ = z + oz;
            for (var step = 0; step < height; step++)
            {
                var value = step % 3 == 0
                    ? XianxiaPalette.Solid(XianxiaPalette.Moss)
                    : XianxiaPalette.Solid(XianxiaPalette.Bamboo);
                world.Set(new VoxelCoord64(baseX, y + step, baseZ), value);
            }

            TemplateKit.Ellipsoid(world, baseX + .5, y + height, baseZ + .5, 1.7, .9, 1.7,
                XianxiaPalette.Solid(XianxiaPalette.GrassLight));
        }
    }

    /// <summary>冰晶：半透明的尖锥，尖端发淡蓝微光</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void IceSpire(VoxelWorld world, int x, int y, int z, int seed)
    {
        var height = 5 + TemplateKit.Hash(x, 0, z, seed) % 11;
        TemplateKit.Taper(world, x + .5, z + .5, y, y + height,
            1.5 + TemplateKit.Hash(x, 1, z, seed) % 2 * .6, .15,
            XianxiaPalette.Solid(XianxiaPalette.IceBlue));
        world.Set(new VoxelCoord64(x, y + height + 1, z),
            XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
    }

    /// <summary>枯木：歪斜的枝干，没有叶片</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void DeadTree(VoxelWorld world, int x, int y, int z, int seed)
    {
        var height = 4 + TemplateKit.Hash(x, 0, z, seed) % 5;
        var lean = TemplateKit.Hash(x, 2, z, seed) % 3 - 1;
        for (var step = 0; step < height; step++)
        {
            world.Set(new VoxelCoord64(x + step * lean / 3, y + step, z),
                XianxiaPalette.Solid(XianxiaPalette.StoneDark));
            if (step == height - 1)
            {
                world.Set(new VoxelCoord64(x + step * lean / 3 + 1, y + step, z),
                    XianxiaPalette.Solid(XianxiaPalette.StoneDark));
                world.Set(new VoxelCoord64(x + step * lean / 3, y + step, z + 1),
                    XianxiaPalette.Solid(XianxiaPalette.StoneDark));
            }
        }
    }

    /// <summary>火山焦木：炭化主干与残余余烬</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void CharredTree(VoxelWorld world, int x, int y, int z, int seed)
    {
        var height = 4 + TemplateKit.Hash(x, 0, z, seed) % 6;
        for (var step = 0; step < height; step++)
        {
            world.Set(new VoxelCoord64(x, y + step, z),
                XianxiaPalette.Solid(XianxiaPalette.InkNight));
            if (step == height - 1)
            {
                world.Set(new VoxelCoord64(x + 1, y + step, z),
                    XianxiaPalette.Glow(XianxiaPalette.LavaGlow));
                world.Set(new VoxelCoord64(x, y + step, z - 1),
                    XianxiaPalette.Solid(XianxiaPalette.BasaltDeep));
            }
        }
    }

    /// <summary>熔岩尖刺：黑色岩刺内嵌熔光</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">根 X</param>
    /// <param name="y">地面高</param>
    /// <param name="z">根 Z</param>
    /// <param name="seed">形态种子</param>
    public static void LavaSpike(VoxelWorld world, int x, int y, int z, int seed)
    {
        var height = 3 + TemplateKit.Hash(x, 0, z, seed) % 8;
        TemplateKit.Taper(world, x + .5, z + .5, y, y + height, 1.8, .2,
            XianxiaPalette.Solid(XianxiaPalette.BasaltDeep));
        world.Set(new VoxelCoord64(x, y + height, z),
            XianxiaPalette.Glow(XianxiaPalette.LavaOrange));
    }

    /// <summary>地表小花与灵草：单格装饰，少量带微光</summary>
    /// <param name="world">目标世界</param>
    /// <param name="x">X</param>
    /// <param name="y">贴地高度</param>
    /// <param name="z">Z</param>
    /// <param name="biome">地貌编号</param>
    /// <param name="seed">种子</param>
    public static void SmallFlora(VoxelWorld world, int x, int y, int z, byte biome, int seed)
    {
        var roll = TemplateKit.Hash(x, 0, z, seed) % 13;
        var color = biome switch
        {
            4 => roll < 6 ? XianxiaPalette.LavaOrange : XianxiaPalette.BasaltDeep,
            6 => XianxiaPalette.IceBlue,
            5 => XianxiaPalette.Moss,
            _ => roll switch
            {
                < 4 => XianxiaPalette.LotusPink,
                < 7 => XianxiaPalette.SilkWhite,
                < 10 => XianxiaPalette.GrassLight,
                _ => XianxiaPalette.Wisteria,
            },
        };
        if (roll == 12 && biome is not 4)
        {
            world.Set(new VoxelCoord64(x, y, z), XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
            return;
        }

        world.Set(new VoxelCoord64(x, y, z), XianxiaPalette.Solid(color));
        if (roll < 3)
        {
            world.Set(new VoxelCoord64(x, y + 1, z), XianxiaPalette.Solid(color));
        }
    }
}
