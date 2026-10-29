using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 浮空仙岛与天阶：玉京、扶摇、听雨三岛，世界树，以及从山麓盘旋到云端的登天石阶
/// </summary>
internal static class XianxiaSky
{
    private const int JadeRadius = 90, JadeDepth = 52;
    private const int FuYaoRadius = 60, FuYaoDepth = 40;
    private const int TingYuRadius = 45, TingYuDepth = 32;
    private const int TreeHeight = 112;
    //高频材质常量，避免逐格展开
    private static readonly VoxelValue JadeBar = XianxiaPalette.Jade(XianxiaPalette.JadeGreen);
    private static readonly VoxelValue White = XianxiaPalette.Solid(XianxiaPalette.SilkWhite);
    private static readonly VoxelValue Rock = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
    private static readonly VoxelValue Tile = XianxiaPalette.Solid(XianxiaPalette.RoofTile);
    private static readonly VoxelValue TileCap = XianxiaPalette.Solid(XianxiaPalette.RoofTileLight);
    private static readonly VoxelValue Gold = XianxiaPalette.Metal(XianxiaPalette.Gold);
    private static readonly VoxelValue Cinnabar = XianxiaPalette.Solid(XianxiaPalette.Cinnabar);

    //写一格体素的短入口
    private static void P(VoxelWorld w, long x, long y, long z, VoxelValue value) => w.Set(new VoxelCoord64(x, y, z), value);
    /// <summary>玉京浮岛：主岛、宫阙、飞瀑与环岛松</summary>
    public static VoxelWorld BuildJadeCapital()
    {
        var world = new VoxelWorld();
        BuildIslandBody(world, JadeRadius, JadeDepth, 3);
        BuildJadePalace(world);
        BuildJadeWaterfall(world);
        BuildJadeGardens(world);
        BuildUnderside(world, JadeRadius, JadeDepth, 3, 104);
        BuildCloudSea(world, JadeRadius + 24, JadeRadius + 110, -74, 7);
        BuildReefs(world, 122, 172, -68, 17);
        return world;
    }
    /// <summary>扶摇浮岛：中型岛与悬空楼台</summary>
    public static VoxelWorld BuildFuYaoIsle()
    {
        var world = new VoxelWorld();
        BuildIslandBody(world, FuYaoRadius, FuYaoDepth, 41);
        BuildFuYaoStructures(world);
        BuildUnderside(world, FuYaoRadius, FuYaoDepth, 43, 52);
        BuildCloudSea(world, FuYaoRadius + 16, FuYaoRadius + 82, -58, 47);
        return world;
    }
    /// <summary>听雨浮岛：小型岛与瀑布云</summary>
    public static VoxelWorld BuildTingYuIsle()
    {
        var world = new VoxelWorld();
        BuildIslandBody(world, TingYuRadius, TingYuDepth, 59);
        BuildTingYuStructures(world);
        BuildUnderside(world, TingYuRadius, TingYuDepth, 61, 30);
        BuildCloudSea(world, TingYuRadius + 12, TingYuRadius + 66, -48, 73);
        return world;
    }
    /// <summary>世界树：撑起整片林冠的巨木，树冠带灵光</summary>
    public static VoxelWorld BuildWorldTree()
    {
        var world = new VoxelWorld();
        const int seed = 67;
        BuildTreeGround(world, seed);
        BuildTreeTrunk(world, seed);
        BuildTreeRoots(world, seed);
        BuildTreeBranches(world, seed);
        BuildCanopyLayer(world, 94, 36, 10, 3, seed, 4);
        BuildCanopyLayer(world, 117, 51, 12, 3, seed + 1, 22);
        BuildCanopyLayer(world, 140, 65, 14, 3, seed + 2, 62);
        BuildCanopyDecor(world, seed);
        return world;
    }
    /// <summary>
    /// 登天石阶：在世界坐标中从山麓盘旋到玉京岛底，沿途立灯柱与云桥
    /// </summary>
    /// <param name="target">世界坐标目标世界</param>
    /// <param name="fromX">起点 X</param>
    /// <param name="fromY">起点 Y</param>
    /// <param name="fromZ">起点 Z</param>
    /// <param name="toX">终点 X</param>
    /// <param name="toY">终点 Y</param>
    /// <param name="toZ">终点 Z</param>
    public static void BuildHeavenStairs(VoxelWorld target, int fromX, int fromY, int fromZ, int toX, int toY, int toZ)
    {
        var rise = toY - fromY;
        if (rise < 6)
        {
            TemplateKit.Line(target, (fromX, fromY, fromZ), (toX, toY, toZ), White, 2);
            return;
        }
        var startDx = fromX - toX; var startDz = fromZ - toZ;
        var startRadius = MathF.Sqrt(startDx * startDx + startDz * startDz);
        var startAngle = MathF.Atan2(startDz, startDx);
        const float turn = 1.34F;
        const int seed = 211;
        //极坐标盘旋：半径随高度线性收束到落点，角度转过约七十七度
        (float X, float Z) Point(float s)
        {
            var angle = startAngle + turn * s;
            var radius = startRadius * (1F - s) + MathF.Sin(s * MathF.PI * 3F) * 5F * MathF.Sin(s * MathF.PI);
            return (toX + MathF.Cos(angle) * radius, toZ + MathF.Sin(angle) * radius);
        }

        for (var step = 0; step <= rise; step++)
        {
            var s = step / (float)rise; var y = fromY + step;
            var here = Point(s); var ahead = Point(MathF.Min(1F, s + 1F / rise));
            var dirX = ahead.X - here.X; var dirZ = ahead.Z - here.Z;
            var length = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
            if (length < 1E-4F) { dirX = 1F; dirZ = 0F; } else { dirX /= length; dirZ /= length; }
            //末段过渡为云桥，石阶收窄并浮起云雾
            var bridge = s > .86F;
            var halfWidth = bridge ? 3.2F : 4.5F + TemplateKit.Hash(step / 6, 0, 0, seed) % 5;
            PlaceStep(target, here.X, y, here.Z, dirX, dirZ, halfWidth, bridge ? 5F : 2.6F, bridge);
            if (bridge)
            {
                if (step % 3 == 0)
                {
                    CloudPuff(target, here.X - dirZ * (halfWidth + 2F), y + 1F, here.Z + dirX * (halfWidth + 2F), 2.7, 1.2, 2.7, seed + step);
                    CloudPuff(target, here.X + dirZ * (halfWidth + 2F), y + 1F, here.Z - dirX * (halfWidth + 2F), 2.7, 1.2, 2.7, seed + step + 1);
                }
                if (step % 9 == 0) HangingLantern(target, (int)MathF.Round(here.X), y + 3, (int)MathF.Round(here.Z));
                continue;
            }
            //两侧栏杆与挑出的灯柱
            var railX = -dirZ; var railZ = dirX; var offset = halfWidth + .9F;
            for (var side = -1; side <= 1; side += 2)
            {
                var rx = (long)MathF.Round(here.X + railX * offset * side);
                var rz = (long)MathF.Round(here.Z + railZ * offset * side);
                P(target, rx, y + 1, rz, White);
                if (step % 5 != 0) continue;
                P(target, rx, y + 2, rz, White);
                P(target, rx, y + 3, rz, JadeBar);
            }
            if (step % 12 == 6)
            {
                var lx = (int)MathF.Round(here.X + railX * (offset + .8F));
                var lz = (int)MathF.Round(here.Z + railZ * (offset + .8F));
                for (var h = 1; h <= 3; h++) P(target, lx, y + h, lz, Rock);
                P(target, lx, y + 4, lz, XianxiaPalette.Glow(XianxiaPalette.Gold));
                P(target, lx, y + 5, lz, Tile);
            }
            //每四十层设一处歇脚平台
            if (step % 40 == 0 && step > 0 && step < rise - 6)
            {
                BuildStairPlatform(target, (int)MathF.Round(here.X), y, (int)MathF.Round(here.Z));
            }
        }
        BuildStairLanding(target, toX, toY, toZ, seed);
    }

    //岛屿岩体
    /// <summary>倒锥形体岛：顶面草坡与分层岩壳，内部留空以控制体素</summary>
    private static void BuildIslandBody(VoxelWorld world, int radius, int depth, int seed)
    {
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            var edge = EdgeRadius(x, z, radius, seed);
            if (x * x + z * z > edge * edge) continue;
            var floorY = -(int)DropDepth(x, z, edge, depth, seed);
            var roll = TemplateKit.Hash(x, 0, z, seed) % 17;
            var grass = roll < 5 ? XianxiaPalette.Moss : roll < 8 ? XianxiaPalette.Bamboo : XianxiaPalette.GrassLight;
            P(world, x, 0, z, XianxiaPalette.Grain(grass, x, 0, z, seed + 5, .07F));
            P(world, x, -1, z, XianxiaPalette.Grain(XianxiaPalette.Earth, x, -1, z, seed, .09F));
            P(world, x, -2, z, XianxiaPalette.Grain(XianxiaPalette.Moss, x, -2, z, seed + 3, .11F));
            for (var y = floorY; y <= floorY + 2; y++) P(world, x, y, z, RockValue(x, y, z, floorY, seed));
            if (!IsRim(x, z, radius, seed)) continue;
            //外缘补一圈侧壁，避免从侧面看穿内部空腔
            for (var y = floorY + 3; y <= -3; y++) P(world, x, y, z, RockValue(x, y, z, floorY, seed));
        }
    }
    private static float EdgeRadius(int x, int z, int radius, int seed) =>
        radius * (.9F + .1F * XianxiaNoise.Fbm(x, z, seed, 3, .028F))
        + (TemplateKit.Hash(x, 11, z, seed) % 9 - 4) * .55F;
    private static float DropDepth(int x, int z, float edge, int depth, int seed)
    {
        var t = Math.Clamp(MathF.Sqrt(x * x + z * z) / MathF.Max(edge, 1F), 0F, 1F);
        var drop = 4F + (depth - 4F) * MathF.Pow(1F - t, 1.32F);
        return MathF.Max(4F, drop * (.84F + .32F * XianxiaNoise.Fbm(x, z, seed + 47, 3, .045F)));
    }
    private static bool IsRim(int x, int z, int radius, int seed)
    {
        for (var offset = 0; offset < 4; offset++)
        {
            var nx = x + (offset == 0 ? 1 : offset == 1 ? -1 : 0);
            var nz = z + (offset == 2 ? 1 : offset == 3 ? -1 : 0);
            var edge = EdgeRadius(nx, nz, radius, seed);
            if (nx * nx + nz * nz > edge * edge) return true;
        }
        return false;
    }
    private static VoxelValue RockValue(int x, int y, int z, int floorY, int seed)
    {
        var band = (y - floorY + TemplateKit.Hash(x, 0, z, seed) % 3) % 6;
        var color = band switch
        {
            0 => XianxiaPalette.BasaltDeep,
            1 => XianxiaPalette.Basalt,
            2 => XianxiaPalette.StoneDark,
            3 => XianxiaPalette.Ochre,
            4 => XianxiaPalette.StoneDark,
            _ => XianxiaPalette.Earth,
        };
        return XianxiaPalette.Grain(color, x, y, z, seed + 13, .08F);
    }

    //玉京宫阙
    /// <summary>玉京宫阙群：正殿重檐、东西配殿、白石栏杆与玉阶</summary>
    private static void BuildJadePalace(VoxelWorld world)
    {
        BuildHall(world, 0, -14, 26, 17, 3, 12, 8, true, 7);
        BuildHall(world, -52, 14, 12, 8, 1, 7, 4, false, 19);
        BuildHall(world, 52, 14, 12, 8, 1, 7, 4, false, 23);
        StairFlight(world, 0, 14, 7, 3, 0, 9);
        //庭院铺装与御道
        FillGrain(world, -34, 8, 34, 46, 0, XianxiaPalette.Stone, 11);
        FillGrain(world, -44, -2, 44, 7, 0, XianxiaPalette.SilkWhite, 12);
        FillGrain(world, -5, 47, 5, 72, 0, XianxiaPalette.Stone, 39);
        //三面栏杆，南面留出御道口
        Balustrade(world, -34, 8, -34, 46, 0, 31);
        Balustrade(world, 34, 8, 34, 46, 0, 33);
        Balustrade(world, -34, 46, -8, 46, 0, 35);
        Balustrade(world, 8, 46, 34, 46, 0, 37);
    }
    /// <summary>单体宫室：台基、朱墙玉柱、重檐屋面与宝顶</summary>
    private static void BuildHall(VoxelWorld w, int cx, int cz, int halfX, int halfZ,
        int platformTop, int wallHeight, int roofRise, bool doubleEave, int seed)
    {
        for (var y = 1; y <= platformTop; y++)
            TemplateKit.Box(w, cx - halfX - 4, y, cz - halfZ - 4, cx + halfX + 4, y, cz + halfZ + 4,
                XianxiaPalette.Solid(y == platformTop ? XianxiaPalette.SilkWhite : XianxiaPalette.StoneDark));
        Ring(w, cx - halfX - 4, cz - halfZ - 4, cx + halfX + 4, cz + halfZ + 4, platformTop, JadeBar);
        var floorY = platformTop + 1; var topY = floorY + wallHeight;
        var minX = cx - halfX; var maxX = cx + halfX; var minZ = cz - halfZ; var maxZ = cz + halfZ;
        for (var x = minX; x <= maxX; x++)
        for (var y = floorY; y < topY; y++)
        {
            var door = Math.Abs(x - cx) <= 4 && y < floorY + 7;
            var window = !door && y > floorY + 3 && y < topY - 1 && (x - minX) % 6 is 1 or 2;
            P(w, x, y, minZ, y < floorY + 2 ? Cinnabar : White);
            if (!door) P(w, x, y, maxZ, window ? XianxiaPalette.Glass() : y < floorY + 2 ? Cinnabar : White);
        }
        for (var z = minZ + 1; z < maxZ; z++)
        for (var y = floorY; y < topY; y++)
        {
            P(w, minX, y, z, y < floorY + 2 ? Cinnabar : White);
            P(w, maxX, y, z, y < floorY + 2 ? Cinnabar : White);
        }
        //檐下玉柱与门额金钉
        for (var x = minX; x <= maxX; x += 9)
        for (var y = floorY; y < topY; y++) { P(w, x, y, minZ, JadeBar); P(w, x, y, maxZ, JadeBar); }
        for (var z = minZ; z <= maxZ; z += 9)
        for (var y = floorY; y < topY; y++) { P(w, minX, y, z, JadeBar); P(w, maxX, y, z, JadeBar); }
        FillGrain(w, cx - 5, maxZ, cx + 5, maxZ, floorY + 7, XianxiaPalette.Cinnabar, seed);
        FillGrain(w, cx - 5, maxZ, cx + 5, maxZ, floorY + 8, XianxiaPalette.Gold, seed);
        P(w, cx - 3, floorY + 3, maxZ, Gold);
        P(w, cx + 3, floorY + 3, maxZ, Gold);
        //檐口、屋面与飞檐
        TemplateKit.GableRoof(w, minX - 3, minZ - 3, maxX + 3, maxZ + 3, topY, roofRise, Tile, TileCap);
        for (var x = minX - 4; x <= maxX + 4; x++) { P(w, x, topY, minZ - 3, Gold); P(w, x, topY, maxZ + 3, Gold); }
        for (var corner = 0; corner < 4; corner++)
        {
            var fx = corner < 2 ? minX - 4 : maxX + 4; var fz = corner % 2 == 0 ? minZ - 3 : maxZ + 3;
            P(w, fx, topY + 1, fz, TileCap);
            P(w, fx, topY + 2, fz, Gold);
        }
        var ridgeY = topY + roofRise;
        for (var x = minX - 1; x <= maxX + 1; x += 3) P(w, x, ridgeY + 1, cz, Gold);
        if (!doubleEave) return;
        //重檐上层：收窄的屋身与第二重屋面
        var upperMinX = cx - halfX + 8; var upperMaxX = cx + halfX - 8;
        var upperMinZ = cz - halfZ + 6; var upperMaxZ = cz + halfZ - 6;
        var upperFloor = topY + 2; var upperTop = upperFloor + 6;
        for (var x = upperMinX; x <= upperMaxX; x++)
        for (var z = upperMinZ; z <= upperMaxZ; z++)
        {
            if (x != upperMinX && x != upperMaxX && z != upperMinZ && z != upperMaxZ) continue;
            for (var y = upperFloor; y < upperTop; y++)
            {
                var window = y > upperFloor + 1 && y < upperTop - 1 && (x - upperMinX) % 4 < 2;
                P(w, x, y, z, window ? XianxiaPalette.Glass() : White);
            }
        }
        TemplateKit.GableRoof(w, upperMinX - 2, upperMinZ - 2, upperMaxX + 2, upperMaxZ + 2,
            upperTop, roofRise - 2, Tile, TileCap);
        var upperRidge = upperTop + roofRise - 2;
        for (var x = upperMinX; x <= upperMaxX; x += 2) P(w, x, upperRidge + 1, cz, Gold);
        //宝顶
        TemplateKit.Taper(w, cx + .5, cz + .5, upperRidge + 1, upperRidge + 4, 1.6, .3, Gold);
        TemplateKit.Ellipsoid(w, cx + .5, upperRidge + 5, cz + .5, 1.6, 1.3, 1.6,
            XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
    }
    /// <summary>白石栏杆：望柱与扶手，沿线逐格铺开</summary>
    private static void Balustrade(VoxelWorld w, int x0, int z0, int x1, int z1, int y, int seed)
    {
        var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        for (var step = 0; step <= steps; step++)
        {
            var t = steps == 0 ? 0F : step / (float)steps;
            var x = (int)MathF.Round(x0 + (x1 - x0) * t); var z = (int)MathF.Round(z0 + (z1 - z0) * t);
            P(w, x, y, z, XianxiaPalette.Solid(XianxiaPalette.MoonWhite));
            P(w, x, y + 1, z, White);
            if (step % 3 != 0) continue;
            P(w, x, y + 2, z, White);
            P(w, x, y + 3, z, XianxiaPalette.Jade(
                TemplateKit.Hash(x, y, z, seed) % 4 == 0 ? XianxiaPalette.JadeDeep : XianxiaPalette.JadeGreen));
        }
    }
    private static void StairFlight(VoxelWorld w, int cx, int width, int zStart, int topY, int bottomY, int seed)
    {
        for (var level = 0; level < topY - bottomY; level++)
        {
            var y = topY - level; var z = zStart + level * 2;
            for (var zz = z; zz < z + 2; zz++)
                FillGrain(w, cx - width / 2, zz, cx + width / 2, zz, y, XianxiaPalette.SilkWhite, seed + level);
            Balustrade(w, cx - width / 2 - 1, z, cx - width / 2 - 1, z, y + 1, seed);
            Balustrade(w, cx + width / 2 + 1, z, cx + width / 2 + 1, z, y + 1, seed);
        }
    }
    /// <summary>玉京飞瀑：崖边泉池、引水渠与落入云海的水体和水雾</summary>
    private static void BuildJadeWaterfall(VoxelWorld w)
    {
        const int poolX = 46; const int poolZ = 30;
        for (var z = poolZ - 10; z <= poolZ + 10; z++)
        for (var x = poolX - 10; x <= poolX + 10; x++)
        {
            var dx = x - poolX; var dz = z - poolZ;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > 10F) continue;
            if (distance > 8.2F)
            {
                P(w, x, 0, z, XianxiaPalette.Solid(XianxiaPalette.Stone));
                P(w, x, 1, z, XianxiaPalette.Solid(XianxiaPalette.Moss));
                continue;
            }
            P(w, x, 0, z, XianxiaFlora.WaterBody(x, 0, z));
            P(w, x, -1, z, XianxiaFlora.WaterBody(x, -1, z));
        }
        //引水渠沿径向流出岛缘
        for (var step = 0; step <= 34; step++)
        {
            var x = poolX + (int)MathF.Round(step * .84F); var z = poolZ + (int)MathF.Round(step * .54F);
            for (var side = -2; side <= 2; side++)
            {
                var cx = x - (int)MathF.Round(side * .54F); var cz = z + (int)MathF.Round(side * .84F);
                P(w, cx, 0, cz, XianxiaFlora.WaterBody(cx, 0, cz));
                P(w, cx, -1, cz, XianxiaFlora.WaterBody(cx, -1, cz));
            }
            if (step % 4 != 0) continue;
            for (var side = -1; side <= 1; side += 2)
            {
                var bx = x - (int)MathF.Round(side * 1.62F); var bz = z + (int)MathF.Round(side * 2.52F);
                P(w, bx, 0, bz, Rock);
                P(w, bx, 1, bz, XianxiaPalette.Solid(XianxiaPalette.Moss));
            }
        }
        BuildFall(w, 74, 48, 0, -66, 4.2F, 29);
    }
    /// <summary>飞瀑主体：水体柱随下落散开，夹带水雾与落点云团</summary>
    private static void BuildFall(VoxelWorld w, int centerX, int centerZ, int topY, int bottomY, float radius, int seed)
    {
        for (var y = topY; y >= bottomY; y--)
        {
            var t = (topY - y) / (float)Math.Max(1, topY - bottomY);
            var spread = radius * (1F + t * 1.5F); var reach = (int)MathF.Ceiling(spread);
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (dx * dx + dz * dz > spread * spread) continue;
                var x = centerX + dx; var z = centerZ + dz;
                P(w, x, y, z, XianxiaFlora.WaterBody(x, y, z));
            }
            if (y % 9 == 0) CloudPuff(w, centerX + .5, y, centerZ + .5, spread + 3.5F, 2.4, spread + 3.5F, seed + y);
        }
        CloudPuff(w, centerX + .5, bottomY + 1, centerZ + .5, radius * 4.4F, 3.4, radius * 4.4F, seed + 91);
        CloudPuff(w, centerX + .5, bottomY + 5, centerZ + .5, radius * 3.0F, 2.6, radius * 3.0F, seed + 92);
    }
    /// <summary>玉京园圃：环岛古松、庭院樱树与竹丛</summary>
    private static void BuildJadeGardens(VoxelWorld w)
    {
        (int X, int Z, int Scale)[] pines =
        [
            (-72, -26, 2), (-80, -4, 1), (-76, 24, 2), (-66, 44, 1), (-58, -54, 2), (-36, -64, 1),
            (-12, -66, 2), (16, -64, 1), (40, -56, 2), (58, -34, 1), (74, -6, 2), (78, 20, 1),
            (-2, 68, 1), (-24, 64, 2), (30, 72, 1), (52, 66, 1),
        ];
        for (var index = 0; index < pines.Length; index++)
        {
            var (x, z, scale) = pines[index];
            if (x * x + z * z > 84 * 84) continue;
            XianxiaFlora.PineTree(w, x, 0, z, 401 + index, scale);
        }
        XianxiaFlora.CherryTree(w, -40, 0, 34, 5);
        XianxiaFlora.CherryTree(w, 40, 0, 34, 9);
        XianxiaFlora.CherryTree(w, -42, 0, -34, 13);
        XianxiaFlora.CherryTree(w, 40, 0, -38, 17);
        XianxiaFlora.BambooCluster(w, -64, 0, -12, 21);
        XianxiaFlora.BambooCluster(w, 24, 0, -50, 25);
    }

    //扶摇悬空楼台
    /// <summary>扶摇岛：错落楼阁、悬挑木台、竹桥、风铃与旗幡</summary>
    private static void BuildFuYaoStructures(VoxelWorld w)
    {
        BuildHall(w, -12, -8, 10, 8, 2, 9, 5, true, 83);
        BuildHall(w, 8, 30, 5, 4, 1, 5, 3, false, 87);
        //悬挑木台：立柱扎进岩体，台上设栏杆与茶案
        const int deckY = 2;
        FillGrain(w, 26, -4, 50, 8, deckY, XianxiaPalette.Bark, 89);
        for (var z = -4; z <= 8; z += 4)
        for (var y = -6; y < deckY; y++) { P(w, 33, y, z, XianxiaPalette.Solid(XianxiaPalette.Bark)); P(w, 44, y, z, XianxiaPalette.Solid(XianxiaPalette.Bark)); }
        Balustrade(w, 26, -4, 50, -4, deckY, 91);
        Balustrade(w, 50, -4, 50, 8, deckY, 93);
        Balustrade(w, 26, 8, 50, 8, deckY, 95);
        FillGrain(w, 36, 1, 40, 3, deckY + 1, XianxiaPalette.Stone, 97);
        BuildWindChime(w, 49, deckY - 1, 5, 9, 3);
        BuildFlag(w, -32, 0, 26, 15, 5);
        BuildFlag(w, 26, 0, -30, 18, 9);
        BuildFlag(w, 6, 0, 44, 13, 13);
        BuildMiniRock(w, -76, 28, 0, 10, 16, 101);
        BuildBambooBridge(w, -46, 1, 10, -74, 1, 26);
        XianxiaFlora.PineTree(w, -77, 0, 27, 105, 1);
        XianxiaFlora.BambooCluster(w, 20, 0, 26, 107);
        XianxiaFlora.BambooCluster(w, -30, 0, -24, 109);
        XianxiaFlora.PineTree(w, 44, 0, 24, 111, 2);
        XianxiaFlora.PineTree(w, -38, 0, 40, 113, 1);
        XianxiaFlora.PineTree(w, 8, 0, -46, 115, 2);
    }
    /// <summary>竹桥：微拱桥面与两侧竹栏</summary>
    private static void BuildBambooBridge(VoxelWorld w, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        var dx = x1 - x0; var dz = z1 - z0;
        var length = MathF.Max(1F, MathF.Sqrt(dx * dx + dz * dz));
        var dirX = dx / length; var dirZ = dz / length;
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (float)steps;
            var px = x0 + dx * t; var pz = z0 + dz * t;
            var y = (int)MathF.Round(y0 + (y1 - y0) * t - MathF.Sin(t * MathF.PI) * 3F);
            for (var side = -2; side <= 2; side++)
            {
                var wx = (int)MathF.Round(px - dirZ * side); var wz = (int)MathF.Round(pz + dirX * side);
                P(w, wx, y, wz, XianxiaPalette.Solid(XianxiaPalette.Bamboo));
                if (Math.Abs(side) < 2 || step % 4 != 0) continue;
                P(w, wx, y + 1, wz, XianxiaPalette.Solid(XianxiaPalette.Bamboo));
                P(w, wx, y + 2, wz, XianxiaPalette.Solid(XianxiaPalette.Bark));
            }
            if (step % 4 == 0)
                P(w, (int)MathF.Round(px), y - 1, (int)MathF.Round(pz), XianxiaPalette.Solid(XianxiaPalette.Bamboo));
        }
    }
    private static void BuildMiniRock(VoxelWorld w, int cx, int cz, int topY, int radius, int depth, int seed)
    {
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            var edge = EdgeRadius(x + cx, z + cz, radius, seed);
            if (x * x + z * z > edge * edge) continue;
            var floorY = topY - (int)DropDepth(x + cx, z + cz, edge, depth, seed);
            P(w, cx + x, topY, cz + z, XianxiaPalette.Grain(XianxiaPalette.GrassLight, cx + x, topY, cz + z, seed, .07F));
            P(w, cx + x, topY - 1, cz + z, XianxiaPalette.Grain(XianxiaPalette.Earth, cx + x, topY - 1, cz + z, seed, .09F));
            for (var y = floorY; y <= floorY + 2 && y < topY - 1; y++)
                P(w, cx + x, y, cz + z, RockValue(cx + x, y, cz + z, floorY, seed));
        }
    }
    private static void BuildFlag(VoxelWorld w, int x, int groundY, int z, int height, int seed)
    {
        for (var step = 0; step < height; step++) P(w, x, groundY + step, z, XianxiaPalette.Solid(XianxiaPalette.Bark));
        P(w, x, groundY + height, z, Gold);
        for (var step = 0; step < 9; step++)
        {
            var wave = (int)MathF.Round(MathF.Sin(step * .85F + seed) * 1.3F);
            for (var row = 0; row < 5; row++)
            {
                if ((step + row) % 5 == 0) continue;
                P(w, x + 1 + step, groundY + height - 1 - row + wave, z, XianxiaPalette.Cloth(
                    row == 4 ? XianxiaPalette.Gold : step % 3 == 0 ? XianxiaPalette.BloodRed : XianxiaPalette.Cinnabar));
            }
        }
    }
    private static void BuildWindChime(VoxelWorld w, int x, int y, int z, int length, int seed)
    {
        for (var step = 0; step < length; step++)
        {
            P(w, x + (int)MathF.Round(MathF.Sin(step * .85F + seed) * .9F), y - step, z,
                XianxiaPalette.Metal(XianxiaPalette.Silver));
        }
        P(w, x, y - length, z, XianxiaPalette.Metal(XianxiaPalette.Copper));
        P(w, x, y - length - 1, z, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
    }

    //听雨小岛
    /// <summary>听雨岛：小亭、崖边瀑水与几株松</summary>
    private static void BuildTingYuStructures(VoxelWorld w)
    {
        BuildOctagonPavilion(w, 0, 0, 1, 8);
        FillGrain(w, -2, 10, 2, 16, 0, XianxiaPalette.Stone, 125);
        for (var z = 14; z <= 27; z++)
        for (var x = -7; x <= 7; x++)
        {
            var dz = z - 20;
            if (x * x + dz * dz > 49) continue;
            P(w, x, 0, z, XianxiaFlora.WaterBody(x, 0, z));
            P(w, x, -1, z, XianxiaFlora.WaterBody(x, -1, z));
        }
        //池水向岛缘引出一条细流，再接飞瀑
        for (var step = 0; step <= 16; step++)
        {
            var x = 4 + step; var z = 24 + step;
            for (var side = -1; side <= 1; side++)
                P(w, x - side, 0, z + side, XianxiaFlora.WaterBody(x - side, 0, z + side));
            P(w, x + 2, 0, z - 1, Rock);
            P(w, x - 1, 0, z + 2, Rock);
        }
        BuildFall(w, 24, 36, 0, -54, 3.0F, 127);
        XianxiaFlora.PineTree(w, -28, 0, 8, 129, 2);
        XianxiaFlora.PineTree(w, 26, 0, -18, 131, 2);
        XianxiaFlora.PineTree(w, -14, 0, -30, 133, 1);
        XianxiaFlora.PineTree(w, 30, 0, 12, 135, 1);
        BuildFlag(w, -20, 0, 26, 10, 137);
        BuildWindChime(w, 6, 12, 4, 6, 139);
        TemplateKit.Taper(w, -34.5, 20.5, 0, 3, 2.2, .4, XianxiaPalette.Solid(XianxiaPalette.Basalt));
        TemplateKit.Taper(w, 34.5, -26.5, 0, 2, 1.8, .4, XianxiaPalette.Solid(XianxiaPalette.Basalt));
    }
    /// <summary>八角攒尖亭：石台、八柱与圆形攒尖顶</summary>
    private static void BuildOctagonPavilion(VoxelWorld w, int cx, int cz, int baseY, int radius)
    {
        for (var z = -radius - 3; z <= radius + 3; z++)
        for (var x = -radius - 3; x <= radius + 3; x++)
        {
            if (MathF.Abs(x) + MathF.Abs(z) > (radius + 3) * 1.35F) continue;
            P(w, cx + x, baseY, cz + z, XianxiaPalette.Solid(XianxiaPalette.Stone));
            P(w, cx + x, baseY - 1, cz + z, Rock);
        }
        for (var index = 0; index < 8; index++)
        {
            var angle = index / 8F * MathF.Tau + MathF.PI / 8F;
            var px = cx + (int)MathF.Round(MathF.Cos(angle) * (radius - 1)); var pz = cz + (int)MathF.Round(MathF.Sin(angle) * (radius - 1));
            for (var h = 1; h <= 7; h++) P(w, px, baseY + h, pz, JadeBar);
            P(w, px, baseY + 8, pz, Gold);
            //檐下挂落与灯笼
            var ox = cx + (int)MathF.Round(MathF.Cos(angle) * (radius + .4F));
            var oz = cz + (int)MathF.Round(MathF.Sin(angle) * (radius + .4F));
            P(w, ox, baseY + 8, oz, Tile);
            P(w, ox, baseY + 7, oz, XianxiaPalette.Glow(XianxiaPalette.Gold));
        }
        //攒尖顶逐层收分
        for (var layer = 0; layer <= radius + 2; layer++)
        {
            var r = radius + 2 - layer * 1.05F; var y = baseY + 9 + layer;
            if (r < .6F)
            {
                P(w, cx, y, cz, Gold);
                P(w, cx, y + 1, cz, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
                break;
            }
            var reach = (int)MathF.Ceiling(r);
            for (var z = -reach; z <= reach; z++)
            for (var x = -reach; x <= reach; x++)
            {
                if (x * x + z * z <= r * r) P(w, cx + x, y, cz + z, Tile);
            }
        }
        //亭中石案
        FillGrain(w, cx - 2, cz - 2, cx + 2, cz + 2, baseY + 1, XianxiaPalette.Stone, 121);
    }

    //世界树
    /// <summary>树根地面：草丘、灵草与散落的根须</summary>
    private static void BuildTreeGround(VoxelWorld w, int seed)
    {
        for (var z = -34; z <= 34; z++)
        for (var x = -34; x <= 34; x++)
        {
            if (MathF.Sqrt(x * x + z * z) > 30F + 4F * XianxiaNoise.Fbm(x, z, seed, 3, .06F)) continue;
            P(w, x, 0, z, XianxiaPalette.Grain(XianxiaPalette.GrassLight, x, 0, z, seed, .08F));
            P(w, x, -1, z, XianxiaPalette.Grain(XianxiaPalette.Earth, x, -1, z, seed + 1, .08F));
        }
        for (var index = 0; index < 70; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 3, seed) * MathF.Tau; var distance = 6F + XianxiaNoise.Hash01(index, 4, seed) * 26F;
            XianxiaFlora.SmallFlora(w, (int)MathF.Round(MathF.Cos(angle) * distance), 1,
                (int)MathF.Round(MathF.Sin(angle) * distance), 2, 140 + index);
        }
    }

    //主干轴线随高度扭曲
    private static (float X, float Z) TrunkCenter(int y) => (
        MathF.Sin(y * .031F) * 4F + MathF.Cos(y * .017F) * 2F,
        MathF.Cos(y * .027F) * 4F + MathF.Sin(y * .023F) * 2F);

    //主干预估半径，根部明显外扩
    private static float TrunkRadius(int y) =>
        13.5F - 6F * (y / (float)TreeHeight) + (y < 12 ? 4.5F * (1F - y / 12F) : 0F) + MathF.Sin(y * .18F) * .7F;
    /// <summary>巨木主干：空心厚壁，表面带纹理与苔痕</summary>
    private static void BuildTreeTrunk(VoxelWorld w, int seed)
    {
        for (var y = 0; y <= TreeHeight; y++)
        {
            var radius = TrunkRadius(y);
            var (centerX, centerZ) = TrunkCenter(y);
            var inner = radius - 3.2F;
            var reach = (int)MathF.Ceiling(radius) + 1;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance > radius || (y > 4 && y < TreeHeight - 2 && distance < inner)) continue;
                var texture = MathF.Sin(MathF.Atan2(dz, dx) * 5F + y * .32F)
                              + XianxiaNoise.Fbm(y * .3F, MathF.Atan2(dz, dx), seed, 2, .12F);
                var color = texture > .72F ? XianxiaPalette.Moss
                    : texture < .18F ? XianxiaPalette.Shade(XianxiaPalette.Bark, .72F) : XianxiaPalette.Bark;
                var x = (int)MathF.Round(centerX + dx); var z = (int)MathF.Round(centerZ + dz);
                P(w, x, y, z, XianxiaPalette.Grain(color, x, y, z, seed + 5, .07F));
            }
        }
    }
    /// <summary>板根：从主干底部放射出去的高大根墙</summary>
    private static void BuildTreeRoots(VoxelWorld w, int seed)
    {
        for (var root = 0; root < 11; root++)
        {
            var angle = root / 11F * MathF.Tau + .17F;
            var length = 22 + TemplateKit.Hash(root, 1, 0, seed) % 12;
            var directionX = MathF.Cos(angle); var directionZ = MathF.Sin(angle);
            for (var step = 0; step < length; step++)
            {
                var t = step / (float)length;
                var height = 1 + (int)((1F - t) * 9F); var width = 1 + (int)((1F - t) * 3.6F);
                var centerX = directionX * (12F + step); var centerZ = directionZ * (12F + step);
                for (var side = -width; side <= width; side++)
                {
                    var x = (int)MathF.Round(centerX - directionZ * side);
                    var z = (int)MathF.Round(centerZ + directionX * side);
                    for (var y = 0; y <= height; y++)
                    {
                        P(w, x, y, z, XianxiaPalette.Grain(
                            y == height ? XianxiaPalette.Moss : XianxiaPalette.Bark, x, y, z, seed, .07F));
                    }
                }
            }
        }
    }
    /// <summary>主枝：从主干斜向挑出，托住三层树冠</summary>
    private static void BuildTreeBranches(VoxelWorld w, int seed)
    {
        for (var branch = 0; branch < 7; branch++)
        {
            var angle = branch / 7F * MathF.Tau + .4F;
            var baseY = 60 + branch * 7 + TemplateKit.Hash(branch, 2, 0, seed) % 6;
            var reach = 26F + TemplateKit.Hash(branch, 3, 0, seed) % 16;
            var rise = 14F + TemplateKit.Hash(branch, 4, 0, seed) % 10;
            var (startX, startZ) = TrunkCenter(baseY);
            var to = (MathF.Cos(angle) * reach, baseY + rise, MathF.Sin(angle) * reach);
            BranchTube(w, (startX, baseY, startZ),
                (MathF.Cos(angle) * reach * .45F, baseY + rise * .8F, MathF.Sin(angle) * reach * .45F),
                to, 4.4F, seed + branch);
            BranchTube(w, to, (to.Item1 * 1.14F, to.Item2 + 8F, to.Item3 * 1.14F),
                (to.Item1 * 1.32F, to.Item2 + 12F, to.Item3 * 1.32F), 2.2F, seed + 40 + branch);
        }
    }
    /// <summary>二次贝塞尔管：沿曲线逐段填球，模拟自然弯曲的枝干</summary>
    private static void BranchTube(VoxelWorld w, (float X, float Y, float Z) from,
        (float X, float Y, float Z) control, (float X, float Y, float Z) to, float startRadius, int seed)
    {
        var steps = (int)((MathF.Abs(to.X - from.X) + MathF.Abs(to.Y - from.Y) + MathF.Abs(to.Z - from.Z)) / 1.6F) + 6;
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (float)steps;
            var inv = 1F - t;
            var x = inv * inv * from.X + 2F * inv * t * control.X + t * t * to.X;
            var y = inv * inv * from.Y + 2F * inv * t * control.Y + t * t * to.Y;
            var z = inv * inv * from.Z + 2F * inv * t * control.Z + t * t * to.Z;
            var radius = startRadius * (1F - t) + .6F;
            var reach = (int)MathF.Ceiling(radius);
            for (var dy = -reach; dy <= reach; dy++)
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (dx * dx + dy * dy + dz * dz > radius * radius) continue;
                var wx = (int)MathF.Round(x) + dx; var wy = (int)MathF.Round(y) + dy; var wz = (int)MathF.Round(z) + dz;
                P(w, wx, wy, wz, XianxiaPalette.Grain(XianxiaPalette.Bark, wx, wy, wz, seed, .07F));
            }
        }
    }
    /// <summary>伞状树冠：中心高、边缘低，逐层过渡到发光叶</summary>
    private static void BuildCanopyLayer(VoxelWorld w, int topY, int radius, int depth,
        int thickness, int seed, int glowPercent)
    {
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            var distance = MathF.Sqrt(x * x + z * z);
            var limit = radius * (.82F + .18F * XianxiaNoise.Fbm(x, z, seed, 3, .05F));
            if (distance > limit) continue;
            var surface = topY - (int)(depth * MathF.Pow(distance / limit, 1.55F))
                          + TemplateKit.Hash(x, 0, z, seed) % 3 - 1;
            for (var y = surface - thickness; y <= surface; y++)
            {
                var roll = TemplateKit.Hash(x, y, z, seed + 9) % 100;
                if (roll < glowPercent)
                {
                    P(w, x, y, z, XianxiaPalette.Glow(roll % 3 == 0 ? XianxiaPalette.SpiritBlue : XianxiaPalette.SpiritCyan));
                    continue;
                }
                var leaf = (TemplateKit.Hash(x, y, z, seed + 11) % 5) switch
                {
                    0 => XianxiaPalette.GrassLight,
                    1 => XianxiaPalette.Moss,
                    2 => XianxiaPalette.PineDeep,
                    _ => XianxiaPalette.Bamboo,
                };
                P(w, x, y, z, XianxiaPalette.Grain(leaf, x, y, z, seed, .08F));
            }
        }
    }
    /// <summary>树冠垂藤与灵光果实</summary>
    private static void BuildCanopyDecor(VoxelWorld w, int seed)
    {
        for (var index = 0; index < 60; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 5, seed) * MathF.Tau;
            var distance = 12F + XianxiaNoise.Hash01(index, 6, seed) * 50F;
            var x = (int)MathF.Round(MathF.Cos(angle) * distance); var z = (int)MathF.Round(MathF.Sin(angle) * distance);
            var top = 88 + TemplateKit.Hash(index, 7, 0, seed) % 18;
            var length = 4 + TemplateKit.Hash(index, 8, 0, seed) % 12;
            for (var step = 0; step < length; step++)
                P(w, x + (int)MathF.Round(MathF.Sin(step * .6F + index) * 1.2F), top - step, z,
                    XianxiaPalette.Solid(XianxiaPalette.Moss));
            if (index % 4 != 0) continue;
            P(w, x + 1, top - length, z, XianxiaPalette.Glow(
                index % 8 == 0 ? XianxiaPalette.Gold : XianxiaPalette.SpiritCyan));
            P(w, x + 1, top - length + 1, z, XianxiaPalette.Solid(XianxiaPalette.Bark));
        }
    }

    //天阶构件
    private static void PlaceStep(VoxelWorld w, float cx, int y, float cz, float dirX, float dirZ,
        float halfWidth, float halfDepth, bool bridge)
    {
        var reach = (int)MathF.Ceiling(halfWidth + halfDepth) + 2;
        var centerX = (long)MathF.Round(cx); var centerZ = (long)MathF.Round(cz);
        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            var along = dx * dirX + dz * dirZ; var across = -dx * dirZ + dz * dirX;
            if (MathF.Abs(along) > halfDepth || MathF.Abs(across) > halfWidth) continue;
            var x = centerX + dx; var z = centerZ + dz;
            P(w, x, y, z, MathF.Abs(across) > halfWidth - .9F ? JadeBar : White);
            if (bridge) P(w, x, y - 1, z, XianxiaPalette.CloudMist(150));
        }
    }
    private static void BuildStairPlatform(VoxelWorld w, int cx, int y, int cz)
    {
        for (var dz = -8; dz <= 8; dz++)
        for (var dx = -8; dx <= 8; dx++)
        {
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > 8.5F) continue;
            P(w, cx + dx, y, cz + dz, distance > 7.3F ? JadeBar : White);
            P(w, cx + dx, y - 1, cz + dz, Rock);
        }
        for (var corner = 0; corner < 4; corner++)
        {
            var lx = cx + (corner < 2 ? -6 : 6); var lz = cz + (corner % 2 == 0 ? -6 : 6);
            for (var h = 1; h <= 3; h++) P(w, lx, y + h, lz, Rock);
            P(w, lx, y + 4, lz, XianxiaPalette.Glow(XianxiaPalette.Gold));
        }
    }
    /// <summary>天阶落点：临空洞府石台、天门双柱与承托岩柱</summary>
    private static void BuildStairLanding(VoxelWorld w, int x, int y, int z, int seed)
    {
        for (var dz = -12; dz <= 12; dz++)
        for (var dx = -12; dx <= 12; dx++)
        {
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > 12F) continue;
            for (var h = 0; h <= 2; h++) P(w, x + dx, y - h, z + dz, h == 0 ? White : Rock);
            if (distance < 10.5F) continue;
            P(w, x + dx, y + 1, z + dz, JadeBar);
            if ((dx + dz) % 3 == 0) P(w, x + dx, y + 2, z + dz, White);
        }
        //承托岩柱与天门双柱
        TemplateKit.Taper(w, x + .5, z + .5, y - 44, y - 3, 2.2F, 8.5F,
            XianxiaPalette.Solid(XianxiaPalette.Basalt));
        for (var side = -1; side <= 1; side += 2)
        {
            for (var h = 1; h <= 9; h++) P(w, x + side * 7, y + h, z, White);
            P(w, x + side * 7, y + 10, z, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        }
        for (var dx = -7; dx <= 7; dx++)
        {
            P(w, x + dx, y + 8, z, JadeBar);
            if (dx % 3 == 0) P(w, x + dx, y + 9, z, Gold);
        }
        CloudPuff(w, x + .5, y + 2, z + .5, 15, 3.2, 15, seed + 3);
    }

    //通用构件
    private static void FillGrain(VoxelWorld w, int x0, int z0, int x1, int z1, int y, Rgba32 color, int seed)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (z0 > z1) (z0, z1) = (z1, z0);
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++) P(w, x, y, z, XianxiaPalette.Grain(color, x, y, z, seed, .07F));
    }

    //矩形边框线
    private static void Ring(VoxelWorld w, int minX, int minZ, int maxX, int maxZ, int y, VoxelValue value)
    {
        for (var x = minX; x <= maxX; x++) { P(w, x, y, minZ, value); P(w, x, y, maxZ, value); }
        for (var z = minZ; z <= maxZ; z++) { P(w, minX, y, z, value); P(w, maxX, y, z, value); }
    }
    private static void CloudPuff(VoxelWorld w, double cx, double cy, double cz,
        double radiusX, double radiusY, double radiusZ, int seed)
    {
        for (var z = (int)(cz - radiusZ); z <= cz + radiusZ; z++)
        for (var y = (int)(cy - radiusY); y <= cy + radiusY; y++)
        for (var x = (int)(cx - radiusX); x <= cx + radiusX; x++)
        {
            var distance = Math.Pow((x + .5 - cx) / Math.Max(radiusX, .5), 2)
                + Math.Pow((y + .5 - cy) / Math.Max(radiusY, .5), 2)
                + Math.Pow((z + .5 - cz) / Math.Max(radiusZ, .5), 2);
            if (distance > 1 || TemplateKit.Hash(x, y, z, seed) % 5 == 0) continue;
            P(w, x, y, z, XianxiaPalette.CloudMist((byte)(96 + TemplateKit.Hash(x, y, z, seed + 7) % 62)));
        }
    }
    private static void BuildCloudSea(VoxelWorld w, int inner, int outer, int baseY, int seed)
    {
        for (var z = -outer; z <= outer; z++)
        for (var x = -outer; x <= outer; x++)
        {
            var distance = MathF.Sqrt(x * x + z * z);
            if (distance < inner || distance > outer) continue;
            var density = XianxiaNoise.Fbm(x, z, seed, 3, .034F);
            if (density < .5F) continue;
            var y = baseY + (int)(XianxiaNoise.Fbm(x, z, seed + 7, 2, .02F) * 7F) - 3;
            var alpha = (byte)(100 + TemplateKit.Hash(x, y, z, seed) % 56);
            P(w, x, y, z, XianxiaPalette.CloudMist(alpha));
            if (density > .63F) P(w, x, y - 1, z, XianxiaPalette.CloudMist((byte)(alpha - 22)));
        }
    }
    private static void BuildReefs(VoxelWorld w, int inner, int outer, int baseY, int seed)
    {
        for (var index = 0; index < 10; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 12, seed) * MathF.Tau;
            var distance = inner + XianxiaNoise.Hash01(index, 13, seed) * (outer - inner);
            var cx = (int)(MathF.Cos(angle) * distance); var cz = (int)(MathF.Sin(angle) * distance);
            var size = 4F + TemplateKit.Hash(index, 14, 0, seed) % 7;
            var top = baseY + 6 + TemplateKit.Hash(index, 15, 0, seed) % 16;
            TemplateKit.Taper(w, cx + .5, cz + .5, baseY - 4, top, size, size * .35F,
                XianxiaPalette.Solid(XianxiaPalette.Basalt), seed + index);
            if (index % 3 == 0) XianxiaFlora.PineTree(w, cx, top + 1, cz, seed + 50 + index, 1);
            else P(w, cx, top + 1, cz, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
        }
    }
    private static void BuildUnderside(VoxelWorld w, int radius, int depth, int seed, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var angle = XianxiaNoise.Hash01(index, 16, seed) * MathF.Tau;
            var distance = radius * (.42F + XianxiaNoise.Hash01(index, 17, seed) * .46F);
            var x = (int)(MathF.Cos(angle) * distance); var z = (int)(MathF.Sin(angle) * distance);
            var edge = EdgeRadius(x, z, radius, seed);
            if (distance > edge - 2F) continue;
            var floorY = -(int)DropDepth(x, z, edge, depth, seed);
            var length = 6 + TemplateKit.Hash(index, 18, 0, seed) % 26;
            if (index % 3 == 0)
            {
                var drift = TemplateKit.Hash(index, 19, 0, seed) % 5 - 2;
                for (var step = 0; step < length; step++)
                {
                    P(w, x + step * drift / 9 + (int)MathF.Round(MathF.Sin(step * .7F + index) * 1.4F),
                        floorY - 1 - step, z, step % 5 == 0
                            ? XianxiaPalette.Solid(XianxiaPalette.Moss)
                            : XianxiaPalette.Solid(XianxiaPalette.Bamboo));
                }
            }
            else
            {
                TemplateKit.Taper(w, x + .5, z + .5, floorY - length, floorY - 1, .5F, 2.6F,
                    XianxiaPalette.Solid(XianxiaPalette.Basalt));
                if (index % 5 == 0) P(w, x, floorY - length, z, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
            }
        }
    }

    //云桥灯：金顶小灯与灵光芯
    private static void HangingLantern(VoxelWorld w, int x, int y, int z)
    {
        P(w, x, y, z, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
        P(w, x, y + 1, z, Gold);
        P(w, x, y + 2, z, XianxiaPalette.CloudMist(150));
    }
}
