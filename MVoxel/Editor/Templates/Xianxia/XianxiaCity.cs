using MVoxel.Core;
using MVoxel.Voxels;
namespace MVoxel.Editor.Templates.Xianxia;
/// <summary>
/// 长乐坊市与人间烟火：街巷店铺、渡口栈桥、灵田梯田与望海亭，
/// 构件以脚下平台为原点，水平居中于 (0,0)
/// </summary>
internal static class XianxiaCity
{
    private const int PlanWidth = 176;
    private const int PlanDepth = 160;
    private const int PlanOffsetX = 88;
    private const int PlanOffsetZ = 80;
    private enum Face { North, South, East, West }
    private static readonly VoxelValue Wood = XianxiaPalette.Solid(XianxiaPalette.Bark);
    private static readonly VoxelValue Plank = XianxiaPalette.Solid(XianxiaPalette.Copper);
    private static readonly VoxelValue Stone = XianxiaPalette.Solid(XianxiaPalette.Stone);
    private static readonly VoxelValue StoneDark = XianxiaPalette.Solid(XianxiaPalette.StoneDark);
    private static readonly VoxelValue Tile = XianxiaPalette.Solid(XianxiaPalette.RoofTile);
    private static readonly VoxelValue TileLight = XianxiaPalette.Solid(XianxiaPalette.RoofTileLight);
    private static readonly VoxelValue Ink = XianxiaPalette.Solid(XianxiaPalette.InkNight);
    private static readonly VoxelValue Brass = XianxiaPalette.Metal(XianxiaPalette.Gold);
    private static readonly VoxelValue LanternGlow = XianxiaPalette.Glow(XianxiaPalette.Gold);
    private static readonly VoxelValue[] Cloths =
    [
        XianxiaPalette.Cloth(XianxiaPalette.Cinnabar), XianxiaPalette.Cloth(XianxiaPalette.Gold),
        XianxiaPalette.Cloth(XianxiaPalette.JadeGreen), XianxiaPalette.Cloth(XianxiaPalette.LotusPink),
        XianxiaPalette.Cloth(XianxiaPalette.SpiritBlue),
    ];
    private static void Put(VoxelWorld world, int x, int y, int z, VoxelValue value) =>
        world.Set(new VoxelCoord64(x, y, z), value);
    /// <summary>
    /// 长乐坊市：十字街巷、招幌、茶楼与灯笼
    /// </summary>
    public static VoxelWorld BuildMarketTown()
    {
        var world = new VoxelWorld();
        var plan = new bool[PlanWidth * PlanDepth];
        PaveMarket(world, plan);
        CarveCanal(world, plan);
        BuildPlaza(world, plan);
        BuildTeaHouse(world, plan);
        BuildBridge(world);
        Archway(world, plan, 0, 69, true, 4401);
        Archway(world, plan, 0, -69, true, 4402);
        Archway(world, plan, 79, 0, false, 4403);
        Archway(world, plan, -79, 0, false, 4404);
        FillShopRows(world, plan);
        FillBlocks(world, plan);
        DressMarket(world);
        return world;
    }

    /// <summary>
    /// 渡灵渡口：伸入海面的栈桥、灵舟与引航灯
    /// </summary>
    public static VoxelWorld BuildSpiritHarbor()
    {
        var world = new VoxelWorld();
        BuildQuay(world);
        BuildBasin(world);
        BuildJetty(world);
        BuildHarborBuildings(world);
        MoorBoats(world);
        BuildNavLights(world);
        DressHarbor(world);
        return world;
    }

    /// <summary>
    /// 灵田梯田：沿坡而下的九层田垄与水渠
    /// </summary>
    public static VoxelWorld BuildSpiritFields()
    {
        var world = new VoxelWorld();
        BuildTerraces(world);
        BuildFieldHut(world);
        PlantFieldTrees(world);
        return world;
    }

    /// <summary>
    /// 望海亭：临崖六角亭与石阶
    /// </summary>
    public static VoxelWorld BuildSeaPavilion()
    {
        var world = new VoxelWorld();
        BuildCliff(world);
        BuildHexPavilion(world);
        DressCliff(world);
        return world;
    }
    private static int Index(int x, int z) => (x + PlanOffsetX) * PlanDepth + z + PlanOffsetZ;
    private static bool InPlan(int x, int z) => x >= -88 && x < 88 && z >= -80 && z < 80;
    //检查一片区域是否未被占用
    private static bool Free(bool[] plan, int minX, int minZ, int maxX, int maxZ)
    {
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
            if (!InPlan(x, z) || plan[Index(x, z)]) return false;
        return true;
    }
    //把一片区域标记为已占用
    private static void Reserve(bool[] plan, int minX, int minZ, int maxX, int maxZ)
    {
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
            if (InPlan(x, z)) plan[Index(x, z)] = true;
    }
    //十字主街、支巷与街区占位
    private static void PaveMarket(VoxelWorld world, bool[] plan)
    {
        for (var x = -85; x <= 85; x++)
        for (var z = -75; z <= 75; z++)
        {
            var street = Math.Abs(x) <= 7 || Math.Abs(z) <= 6 || Math.Abs(Math.Abs(z) - 43) <= 3
                || Math.Abs(Math.Abs(x) - 43) <= 3;
            var curb = street && (Math.Abs(x) == 7 || Math.Abs(z) == 6
                || Math.Abs(Math.Abs(z) - 43) == 3 || Math.Abs(Math.Abs(x) - 43) == 3);
            Put(world, x, 0, z, curb ? StoneDark : street
                ? XianxiaPalette.Grain(XianxiaPalette.Stone, x, 0, z, 701, .06F)
                : XianxiaPalette.Grain(XianxiaPalette.Earth, x, 0, z, 702, .05F));
            if (street) plan[Index(x, z)] = true;
        }
    }
    //横贯街市的水渠、石岸与踏石
    private static void CarveCanal(VoxelWorld world, bool[] plan)
    {
        for (var x = -84; x <= 84; x++)
        {
            for (var z = 22; z <= 27; z++) Put(world, x, 0, z, XianxiaFlora.WaterBody(x, 0, z));
            for (var z = 20; z <= 29; z++) plan[Index(x, z)] = true;
            Put(world, x, 1, 21, XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 1, 21, 711, .05F));
            Put(world, x, 1, 28, XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 1, 28, 712, .05F));
            if (TemplateKit.Hash(x, 2, 1, 713) % 17 == 0) Put(world, x, 1, 24, Stone);
        }
    }
    //桥头市集广场：摊位与小树
    private static void BuildPlaza(VoxelWorld world, bool[] plan)
    {
        for (var x = -22; x <= 22; x++)
        for (var z = 7; z <= 20; z++)
        {
            plan[Index(x, z)] = true;
            if (Math.Abs(x) <= 7 || Math.Abs(z) <= 6) continue;
            Put(world, x, 0, z, XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 0, z, 721, .055F));
        }
        Stall(world, 13, 10, 731);
        Stall(world, 18, 17, 732);
        XianxiaFlora.CherryTree(world, -21, 0, 9, 733);
    }
    //布篷摊位，柜台上摆着货物
    private static void Stall(VoxelWorld world, int x, int z, int seed)
    {
        foreach (var (dx, dz) in new[] { (0, 0), (3, 0), (0, 2), (3, 2) })
        {
            Put(world, x + dx, 1, z + dz, Wood);
            Put(world, x + dx, 2, z + dz, Wood);
        }
        TemplateKit.Box(world, x, 3, z, x + 3, 3, z + 2, Cloths[seed % Cloths.Length]);
        for (var dx = 0; dx <= 3; dx++)
        {
            Put(world, x + dx, 1, z + 1, Plank);
            if (TemplateKit.Hash(x + dx, 0, z, seed) % 3 != 0) continue;
            Put(world, x + dx, 2, z + 1, XianxiaPalette.Solid(
                TemplateKit.Hash(x, dx, z, seed) % 2 == 0 ? XianxiaPalette.SandGold : XianxiaPalette.Bamboo));
        }
        Put(world, x + 1, 4, z + 1, LanternGlow);
    }
    //三层茶楼：回字挑台、茶座与招牌
    private static void BuildTeaHouse(VoxelWorld world, bool[] plan)
    {
        const int minX = 12, maxX = 32, minZ = -34, maxZ = -14, top = 12, seed = 4242;
        Reserve(plan, minX - 2, minZ - 2, maxX + 2, maxZ + 2);
        TemplateKit.Box(world, minX, 0, minZ, maxX, 0, maxZ, StoneDark);
        for (var y = 1; y <= top; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var west = x == minX;
            var south = z == maxZ;
            var onX = west || x == maxX;
            var onZ = south || z == minZ;
            if ((!onX && !onZ) || ((west || south) && y <= 3)) continue;
            var post = (onX && onZ) || (west && z % 5 == 0) || (south && x % 5 == 0)
                || y is 1 or 5 or 9 or top;
            Put(world, x, y, z, post ? Wood
                : XianxiaPalette.Grain(XianxiaPalette.MoonWhite, x, y, z, seed, .03F));
        }
        for (var z = minZ; z <= maxZ; z += 4)
        for (var y = 1; y <= 3; y++) Put(world, minX, y, z, Wood);
        for (var x = minX; x <= maxX; x += 4)
        for (var y = 1; y <= 3; y++) Put(world, x, y, maxZ, Wood);
        TemplateKit.Box(world, minX, 4, maxZ, maxX, 4, maxZ, Wood);
        TemplateKit.Box(world, minX, 4, minZ, minX, 4, maxZ, Wood);
        TemplateKit.Box(world, minX + 1, 5, minZ + 1, maxX - 1, 5, maxZ - 1, Plank);
        TemplateKit.Box(world, minX + 1, 9, minZ + 1, maxX - 1, 9, maxZ - 1, Plank);
        for (var floor = 1; floor <= 2; floor++)
        {
            var y = floor * 4 + 1;
            TemplateKit.Box(world, minX - 3, y, minZ + 2, minX - 1, y, maxZ - 2, Plank);
            TemplateKit.Box(world, minX + 2, y, maxZ + 1, maxX - 2, y, maxZ + 3, Plank);
            for (var z = minZ + 2; z <= maxZ - 2; z += 2)
            {
                Put(world, minX - 3, y + 1, z, Wood);
                Put(world, minX - 3, y + 2, z, Wood);
            }
            for (var x = minX + 2; x <= maxX - 2; x += 2)
            {
                Put(world, x, y + 1, maxZ + 3, Wood);
                Put(world, x, y + 2, maxZ + 3, Wood);
            }
            TemplateKit.Box(world, minX - 3, y + 2, minZ + 2, minX - 3, y + 2, maxZ - 2, Wood);
            TemplateKit.Box(world, minX + 2, y + 2, maxZ + 3, maxX - 2, y + 2, maxZ + 3, Wood);
            Lantern(world, minX - 3, y - 1, minZ + 4);
            Lantern(world, minX - 3, y - 1, maxZ - 4);
            Lantern(world, minX + 6, y - 1, maxZ + 3);
            Lantern(world, maxX - 6, y - 1, maxZ + 3);
        }
        Signboard(world, Face.West, minX, minZ, maxX, maxZ, 3, 10);
        Signboard(world, Face.South, minX, minZ, maxX, maxZ, 3, 8);
        Banner(world, Face.West, minX, minZ, maxX, maxZ, 3, 7, 4, Cloths[0]);
        TileRoof(world, minX, minZ, maxX, maxZ, 13, 5, true, seed);
    }
    //横跨水渠的石桥与桥头灯
    private static void BuildBridge(VoxelWorld world)
    {
        for (var z = 16; z <= 33; z++)
        for (var x = -7; x <= 7; x++)
            Put(world, x, z is 16 or 17 or 32 or 33 ? 1 : 2, z,
                XianxiaPalette.Grain(XianxiaPalette.Stone, x, 0, z, 741, .05F));
        foreach (var z in new[] { 18, 31 })
        {
            for (var x = -7; x <= 7; x++) Put(world, x, 3, z, x % 3 == 0 ? StoneDark : Wood);
            foreach (var x in new[] { -7, 7 })
            {
                Put(world, x, 3, z, Stone);
                Lantern(world, x, 5, z);
            }
        }
    }
    //四柱三间牌坊，含匾额、灯笼与瓦顶
    private static void Archway(VoxelWorld world, bool[] plan, int centerX, int centerZ,
        bool acrossX, int seed)
    {
        if (acrossX) Reserve(plan, centerX - 12, centerZ - 2, centerX + 12, centerZ + 2);
        else Reserve(plan, centerX - 2, centerZ - 12, centerX + 2, centerZ + 12);
        foreach (var offset in new[] { -10, -4, 4, 10 })
        {
            var px = acrossX ? centerX + offset : centerX;
            var pz = acrossX ? centerZ : centerZ + offset;
            for (var y = 0; y <= 8; y++) Put(world, px, y, pz, y < 2 ? StoneDark : Wood);
            Put(world, px, 2, pz, Brass);
            Lantern(world, px, 5, pz);
        }
        if (acrossX)
        {
            TemplateKit.Box(world, centerX - 11, 6, centerZ - 1, centerX + 11, 6, centerZ + 1, Wood);
            TemplateKit.Box(world, centerX - 11, 8, centerZ, centerX + 11, 8, centerZ, Wood);
            TemplateKit.Box(world, centerX - 4, 7, centerZ - 1, centerX + 4, 8, centerZ + 1, Ink);
        }
        else
        {
            TemplateKit.Box(world, centerX - 1, 6, centerZ - 11, centerX + 1, 6, centerZ + 11, Wood);
            TemplateKit.Box(world, centerX, 8, centerZ - 11, centerX, 8, centerZ + 11, Wood);
            TemplateKit.Box(world, centerX - 1, 7, centerZ - 4, centerX + 1, 8, centerZ + 4, Ink);
        }
        TileRoof(world, acrossX ? centerX - 12 : centerX - 2, acrossX ? centerZ - 2 : centerZ - 12,
            acrossX ? centerX + 12 : centerX + 2, acrossX ? centerZ + 2 : centerZ + 12,
            9, 3, !acrossX, seed);
    }
    //十二条临街店铺序列
    private static void FillShopRows(VoxelWorld world, bool[] plan)
    {
        foreach (var (alongZ, positive, front, seed) in new[]
                 {
                     (true, true, 8, 11), (true, false, -8, 12), (false, false, -7, 13),
                     (false, true, 8, 14), (false, false, -47, 15), (false, true, -39, 16),
                     (false, false, 39, 17), (false, true, 47, 18), (true, false, -47, 19),
                     (true, true, -39, 20), (true, false, 39, 21), (true, true, 47, 22),
                 })
        {
            ShopRow(world, plan, alongZ, positive, front, alongZ ? -73 : -84, alongZ ? 73 : 84, seed);
        }
    }
    //填补街区腹地的后宅，让屋顶连成一片
    private static void FillBlocks(VoxelWorld world, bool[] plan)
    {
        for (var x = -78; x <= 78; x += 14)
        for (var z = -68; z <= 68; z += 14)
        {
            if (!Free(plan, x, z, x + 10, z + 8)) continue;
            Reserve(plan, x, z, x + 10, z + 8);
            TemplateKit.Box(world, x, 0, z, x + 10, 0, z + 8, Stone);
            TemplateKit.Shell(world, x, 1, z, x + 10, 4, z + 8,
                XianxiaPalette.Solid(XianxiaPalette.SilkWhite), x + z + 1300);
            TileRoof(world, x, z, x + 10, z + 8, 5, 2, (x + z) % 2 == 0, x + z);
            Put(world, x + 5, 1, z + 8, Wood);
        }
    }

    //单侧店铺序列，宽度、进深与层数由哈希决定
    private static void ShopRow(VoxelWorld world, bool[] plan, bool alongZ, bool positive,
        int front, int from, int to, int seed)
    {
        var cursor = from;
        while (cursor <= to)
        {
            var hash = TemplateKit.Hash(cursor, front, seed, 907);
            var width = 8 + hash % 6;
            var depth = 7 + hash / 6 % 4;
            var floors = hash / 96 % 11 == 0 ? 1 : hash / 96 % 23 == 0 ? 4 : 2 + hash / 24 % 2;
            var end = cursor + width - 1;
            if (end > to) break;
            var face = alongZ ? (positive ? Face.West : Face.East) : (positive ? Face.North : Face.South);
            var minX = alongZ ? (positive ? front : front - depth + 1) : cursor;
            var maxX = alongZ ? (positive ? front + depth - 1 : front) : end;
            var minZ = alongZ ? cursor : (positive ? front : front - depth + 1);
            var maxZ = alongZ ? end : (positive ? front + depth - 1 : front);
            cursor = BuildShop(world, plan, minX, minZ, maxX, maxZ, face, seed + cursor, floors)
                ? end + 2 + TemplateKit.Hash(end, 1, seed, 911) % 3 : cursor + 3;
        }
    }
    //单间木构店铺：台基、铺面、招牌、挑台与瓦顶
    private static bool BuildShop(VoxelWorld world, bool[] plan, int minX, int minZ, int maxX, int maxZ,
        Face face, int seed, int floors)
    {
        if (minX < -84 || maxX > 84 || minZ < -74 || maxZ > 74) return false;
        if (!Free(plan, minX, minZ, maxX, maxZ)) return false;
        Reserve(plan, minX, minZ, maxX, maxZ);
        var top = floors * 4;
        var timber = TemplateKit.Hash(minX, 3, maxZ, seed) % 6 == 0;
        TemplateKit.Box(world, minX, 0, minZ, maxX, 0, maxZ,
            XianxiaPalette.Grain(XianxiaPalette.Stone, minX, 0, minZ, seed, .05F));
        for (var y = 1; y <= top; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var onX = x == minX || x == maxX;
            var onZ = z == minZ || z == maxZ;
            if ((!onX && !onZ) || (IsFront(face, minX, minZ, maxX, maxZ, x, z) && y <= 3)) continue;
            var frame = (onX && onZ) || y is 1 or 4 or 5 or 9 || y == top;
            Put(world, x, y, z, frame || timber ? Wood
                : XianxiaPalette.Grain(XianxiaPalette.MoonWhite, x, y, z, seed, .035F));
        }
        var width = face is Face.West or Face.East ? maxZ - minZ + 1 : maxX - minX + 1;
        for (var u = 1; u < width; u += 3)
        {
            var (px, pz) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
            for (var y = 1; y <= 3; y++) Put(world, px, y, pz, Wood);
        }
        for (var floor = 1; floor < floors; floor++)
        for (var u = 2; u + 2 < width; u += 4)
        {
            var (wx, wz) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
            Put(world, wx, floor * 4 + 1, wz,
                TemplateKit.Hash(wx, floor, wz, seed) % 3 != 0 ? LanternGlow : Wood);
            Put(world, wx, floor * 4 + 2, wz, Wood);
        }
        var (mx, mz) = FacadeCell(face, minX, minZ, maxX, maxZ, width / 2);
        var (counterX, counterZ) = Inward(face, mx, mz, 1);
        Put(world, counterX, 1, counterZ, Plank);
        Signboard(world, face, minX, minZ, maxX, maxZ, 3, Math.Min(width - 2, 9));
        foreach (var u in new[] { 1, width - 2 })
        {
            var (lx, lz) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
            var (ox, oz) = Outward(face, lx, lz, 1);
            Lantern(world, ox, 2, oz);
        }
        if (TemplateKit.Hash(minX, 5, maxZ, seed) % 2 == 0)
        {
            Banner(world, face, minX, minZ, maxX, maxZ, width - 3, 6, 3,
                Cloths[TemplateKit.Hash(minX, 6, maxZ, seed) % Cloths.Length]);
        }
        if (floors >= 2 && TemplateKit.Hash(minX, 7, maxZ, seed) % 3 != 0)
        {
            for (var u = 1; u + 1 < width; u++)
            {
                var (x, z) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
                var (bx, bz) = Outward(face, x, z, 1);
                var (cx, cz) = Outward(face, x, z, 2);
                Put(world, bx, 6, bz, Plank);
                Put(world, cx, 6, cz, Plank);
                if (u % 2 == 0) Put(world, cx, 7, cz, Wood);
            }
        }
        var (edgeX, edgeZ) = FacadeCell(face, minX, minZ, maxX, maxZ, width - 1);
        var (crateX, crateZ) = Outward(face, edgeX, edgeZ, 2);
        Crate(world, crateX, 0, crateZ);
        TileRoof(world, minX, minZ, maxX, maxZ, top + 1,
            3 + TemplateKit.Hash(minX, 8, maxZ, seed) % 3, face is Face.West or Face.East, seed);
        return true;
    }
    //双坡瓦顶，带出檐、翘角与脊饰
    private static void TileRoof(VoxelWorld world, int minX, int minZ, int maxX, int maxZ,
        int baseY, int rise, bool ridgeAlongZ, int seed)
    {
        var half = ((ridgeAlongZ ? maxX - minX : maxZ - minZ) + 1) / 2.0;
        for (var layer = 0; layer <= rise; layer++)
        {
            var inset = (int)Math.Round(half * layer / Math.Max(1, rise));
            var low = ridgeAlongZ ? minX - 1 + inset : minZ - 1 + inset;
            var high = ridgeAlongZ ? maxX + 1 - inset : maxZ + 1 - inset;
            var y = baseY + layer;
            if (high - low <= 1)
            {
                if (ridgeAlongZ) TemplateKit.Box(world, low, y, minZ - 1, high, y, maxZ + 1, TileLight);
                else TemplateKit.Box(world, minX - 1, y, low, maxX + 1, y, high, TileLight);
                break;
            }
            var tile = XianxiaPalette.Grain(XianxiaPalette.RoofTile, low, y, high, seed, .04F);
            if (ridgeAlongZ)
            {
                TemplateKit.Box(world, low, y, minZ - 1, low, y, maxZ + 1, tile);
                TemplateKit.Box(world, high, y, minZ - 1, high, y, maxZ + 1, tile);
            }
            else
            {
                TemplateKit.Box(world, minX - 1, y, low, maxX + 1, y, low, tile);
                TemplateKit.Box(world, minX - 1, y, high, maxX + 1, y, high, tile);
            }
        }
        foreach (var (x, z) in new[] { (minX - 1, minZ - 1), (minX - 1, maxZ + 1),
                     (maxX + 1, minZ - 1), (maxX + 1, maxZ + 1) })
            Put(world, x, baseY + 1, z, TileLight);
        var cap = (int)Math.Round(half);
        Put(world, ridgeAlongZ ? minX - 1 + cap : minX - 1, baseY + rise,
            ridgeAlongZ ? minZ - 1 : minZ - 1 + cap, Brass);
        Put(world, ridgeAlongZ ? maxX + 1 - cap : maxX + 1, baseY + rise,
            ridgeAlongZ ? maxZ + 1 : maxZ + 1 - cap, Brass);
    }
    //街市陈设：灯笼串、行道树与货箱
    private static void DressMarket(VoxelWorld world)
    {
        foreach (var z in new[] { -64, -50, -36, 36, 50, 64 }) LanternString(world, -7, z, 7, z, 9, 2);
        foreach (var x in new[] { -64, -50, 50, 64 }) LanternString(world, x, -6, x, 6, 9, 2);
        XianxiaFlora.CherryTree(world, -52, 0, 19, 7401);
        XianxiaFlora.CherryTree(world, 52, 0, 19, 7402);
        XianxiaFlora.CherryTree(world, -30, 0, 19, 7403);
        XianxiaFlora.CherryTree(world, 30, 0, 19, 7404);
        XianxiaFlora.PineTree(world, -30, 0, 74, 7405);
        foreach (var (x, z) in new[] { (6, -58), (-6, -44), (6, 44), (-6, 58), (6, 12) })
            Crate(world, x, 0, z);
    }
    //横跨街道的灯笼串，绳上每隔三步挂一盏
    private static void LanternString(VoxelWorld world, int x1, int z1, int x2, int z2, int topY, int sag)
    {
        for (var y = 1; y <= topY; y++)
        {
            Put(world, x1, y, z1, Wood);
            Put(world, x2, y, z2, Wood);
        }
        Put(world, x1, topY + 1, z1, LanternGlow);
        Put(world, x2, topY + 1, z2, LanternGlow);
        var steps = Math.Abs(x2 - x1) + Math.Abs(z2 - z1);
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (double)steps;
            var x = (int)Math.Round(x1 + (x2 - x1) * t);
            var z = (int)Math.Round(z1 + (z2 - z1) * t);
            var y = topY - (int)Math.Round(Math.Sin(t * Math.PI) * sag);
            Put(world, x, y, z, Ink);
            if (step % 3 == 0) Put(world, x, y - 1, z, LanternGlow);
        }
    }
    //门前悬挂的灯笼
    private static void Lantern(VoxelWorld world, int x, int y, int z)
    {
        Put(world, x, y, z, LanternGlow);
        Put(world, x, y + 1, z, Ink);
        if (y > 1) Put(world, x, y - 1, z, Cloths[0]);
    }
    //黑底金字的店招匾额
    private static void Signboard(VoxelWorld world, Face face, int minX, int minZ, int maxX, int maxZ,
        int y, int width)
    {
        var span = face is Face.West or Face.East ? maxZ - minZ + 1 : maxX - minX + 1;
        var start = (span - width) / 2;
        for (var u = start; u < start + width; u++)
        {
            var (x, z) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
            var (ox, oz) = Outward(face, x, z, 1);
            Put(world, ox, y, oz, Ink);
            Put(world, ox, y + 1, oz,
                u == start || u == start + width - 1 || (u - start) % 3 == 1 ? Brass : Ink);
        }
    }
    //挑出的招幌，布面下垂并带金属坠
    private static void Banner(VoxelWorld world, Face face, int minX, int minZ, int maxX, int maxZ,
        int u, int topY, int length, VoxelValue cloth)
    {
        var (x, z) = FacadeCell(face, minX, minZ, maxX, maxZ, u);
        var (ax, az) = Outward(face, x, z, 1);
        var (bx, bz) = Outward(face, x, z, 2);
        Put(world, ax, topY, az, Wood);
        Put(world, bx, topY, bz, Wood);
        for (var step = 1; step <= length; step++) Put(world, bx, topY - step, bz, cloth);
        Put(world, bx, topY - length - 1, bz, Brass);
    }
    //木箱货担
    private static void Crate(VoxelWorld world, int x, int y, int z)
    {
        for (var dx = 0; dx <= 1; dx++)
        for (var dz = 0; dz <= 1; dz++) Put(world, x + dx, y + 1, z + dz, Wood);
        Put(world, x, y + 2, z, Plank);
        Put(world, x + 1, y + 2, z + 1, StoneDark);
    }
    private static bool IsFront(Face face, int minX, int minZ, int maxX, int maxZ, int x, int z) => face switch
    {
        Face.North => z == minZ, Face.South => z == maxZ, Face.East => x == maxX, _ => x == minX,
    };
    private static (int X, int Z) FacadeCell(Face face, int minX, int minZ, int maxX, int maxZ, int u) =>
        face switch
        {
            Face.West => (minX, minZ + u), Face.East => (maxX, minZ + u),
            Face.North => (minX + u, minZ), _ => (minX + u, maxZ),
        };
    private static (int X, int Z) Outward(Face face, int x, int z, int distance) => face switch
    {
        Face.West => (x - distance, z), Face.East => (x + distance, z),
        Face.North => (x, z - distance), _ => (x, z + distance),
    };
    private static (int X, int Z) Inward(Face face, int x, int z, int distance) => Outward(face, x, z, -distance);
    //渡口石砌码头、驳岸与下水石阶
    private static void BuildQuay(VoxelWorld world)
    {
        for (var x = -34; x <= -1; x++)
        for (var z = -34; z <= 34; z++)
        {
            Put(world, x, 0, z, XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, 0, z, 1801, .06F));
            Put(world, x, 1, z, XianxiaPalette.Grain(XianxiaPalette.Stone, x, 1, z, 1802, .06F));
        }
        for (var z = -34; z <= 34; z++)
        {
            Put(world, 0, 0, z, StoneDark);
            Put(world, 0, 1, z, StoneDark);
            if (Math.Abs(z) > 4) Put(world, 0, 2, z, Stone);
        }
        for (var z = -3; z <= 3; z++)
        for (var x = 1; x <= 3; x++)
        {
            Put(world, x, 0, z, Stone);
            if (x > 1) Put(world, x, 1, z, Stone);
        }
        for (var z = -30; z <= 30; z += 10)
        {
            Put(world, -2, 2, z, Wood);
            Put(world, -2, 3, z, Brass);
        }
        Lantern(world, -4, 3, -8);
        Lantern(world, -4, 3, 8);
    }
    //港池水面与岸边乱石
    private static void BuildBasin(VoxelWorld world)
    {
        for (var x = 1; x <= 34; x++)
        for (var z = -34; z <= 34; z++) Put(world, x, 0, z, XianxiaFlora.WaterBody(x, 0, z));
        for (var z = -34; z <= 34; z++)
        for (var x = 0; x <= 2; x++)
        {
            if (TemplateKit.Hash(x, 1, z, 1811) % 7 != 0) continue;
            TemplateKit.Ellipsoid(world, x + .5, 1.2, z + .5, 1.4, 1.1, 1.4,
                XianxiaPalette.Grain(XianxiaPalette.Basalt, x, 1, z, 1812, .08F));
        }
    }
    //木栈桥、T 形桥头、栏杆与系泊桩
    private static void BuildJetty(VoxelWorld world)
    {
        for (var x = 1; x <= 27; x++)
        for (var z = -4; z <= 4; z++) Put(world, x, 1, z, (x + z) % 2 == 0 ? Plank : Wood);
        for (var x = 4; x <= 24; x += 5)
        foreach (var z in new[] { -4, 0, 4 }) Put(world, x, 0, z, Wood);
        for (var x = 2; x <= 27; x++)
        foreach (var z in new[] { -5, 5 }) Put(world, x, 3, z, Wood);
        for (var x = 2; x <= 27; x += 3)
        foreach (var z in new[] { -4, 4 })
        {
            Put(world, x, 2, z, Wood);
            Put(world, x, 3, z, Wood);
        }
        for (var x = 22; x <= 33; x++)
        for (var z = -9; z <= 9; z++) Put(world, x, 1, z, (x + z) % 2 == 0 ? Plank : Wood);
        for (var x = 23; x <= 32; x += 3)
        foreach (var z in new[] { -9, 9 })
        {
            Put(world, x, 2, z, Wood);
            Put(world, x, 3, z, Wood);
        }
        foreach (var z in new[] { -9, 9 })
        {
            for (var y = 2; y <= 6; y++) Put(world, 33, y, z, Wood);
            Put(world, 33, 7, z, LanternGlow);
        }
        Crate(world, 12, 1, 2);
        Crate(world, 12, 1, -3);
        Crate(world, 27, 1, 6);
        Lantern(world, 8, 4, 4);
        Lantern(world, 20, 4, 4);
    }
    //渡口司、两座货栈与岸边小屋
    private static void BuildHarborBuildings(VoxelWorld world)
    {
        ShoreHall(world, -30, -26, -16, -14, 2001, false);
        ShoreHall(world, -32, 6, -14, 20, 2002, true);
        ShoreHall(world, -28, 24, -12, 32, 2003, true);
        ShoreHall(world, -20, -8, -12, 2, 2004, false);
        for (var z = -18; z <= -10; z += 4) Crate(world, -8, 1, z);
    }
    //木构渡口房屋，货栈墙面更深沉
    private static void ShoreHall(VoxelWorld world, int minX, int minZ, int maxX, int maxZ,
        int seed, bool warehouse)
    {
        var top = warehouse ? 5 : 4;
        TemplateKit.Box(world, minX, 0, minZ, maxX, 1, maxZ, StoneDark);
        for (var y = 2; y <= top; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            if (x != minX && x != maxX && z != minZ && z != maxZ) continue;
            if (z == maxZ && Math.Abs(x - (minX + maxX) / 2) <= 1 && y <= 3) continue;
            var frame = (x == minX || x == maxX) && (z == minZ || z == maxZ) || y == top;
            Put(world, x, y, z, frame ? Wood
                : XianxiaPalette.Grain(warehouse ? XianxiaPalette.Bark : XianxiaPalette.MoonWhite,
                    x, y, z, seed, .05F));
        }
        for (var x = minX + 2; x <= maxX - 2; x += 4) Put(world, x, 2, maxZ, LanternGlow);
        TileRoof(world, minX, minZ, maxX, maxZ, top + 2, 3, maxX - minX >= maxZ - minZ, seed);
        Lantern(world, minX, 3, maxZ + 1);
        Lantern(world, maxX, 3, maxZ + 1);
    }
    //泊在栈桥两侧的三条灵舟
    private static void MoorBoats(VoxelWorld world)
    {
        Boat(world, 6, 9, 14, true, 2101);
        Boat(world, 10, -9, 15, true, 2102);
        Boat(world, 26, 12, 13, false, 2103);
    }
    //木身灵舟，船头点灵灯，船身嵌灵光纹
    private static void Boat(VoxelWorld world, int x, int z, int length, bool alongX, int seed)
    {
        for (var i = 0; i < length; i++)
        {
            var spread = 2 - (i < length * .72 ? 0 : (int)Math.Round((i / (length - 1.0) - .72) / .28 * 2));
            for (var w = spread; w >= -spread; w--)
            {
                var wx = alongX ? x + i : x + w;
                var wz = alongX ? z + w : z + i;
                Put(world, wx, 0, wz, XianxiaPalette.Grain(XianxiaPalette.Bark, wx, 0, wz, seed, .06F));
                Put(world, wx, 1, wz, (wx + wz) % 2 == 0 ? Plank : Wood);
                if (Math.Abs(w) == spread) Put(world, wx, 2, wz, Wood);
            }
        }
        for (var i = 1; i <= 4; i++)
        for (var w = -1; w <= 1; w++)
        {
            var wx = alongX ? x + i : x + w;
            var wz = alongX ? z + w : z + i;
            if (i is 1 or 4 && Math.Abs(w) == 1) Put(world, wx, 2, wz, Wood);
            if (i is 1 or 4 || Math.Abs(w) == 1) Put(world, wx, 3, wz, Tile);
        }
        for (var i = 3; i < length - 3; i += 3)
            Put(world, alongX ? x + i : x + 2, 2, alongX ? z + 2 : z + i,
                XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
        var bowX = alongX ? x + length - 1 : x;
        var bowZ = alongX ? z : z + length - 1;
        for (var y = 1; y <= 2; y++) Put(world, bowX, y, bowZ, Wood);
        Put(world, bowX, 3, bowZ, LanternGlow);
        var mastX = alongX ? x + length / 2 : x;
        var mastZ = alongX ? z : z + length / 2;
        for (var y = 2; y <= 6; y++) Put(world, mastX, y, mastZ, Wood);
        for (var y = 4; y <= 5; y++)
        for (var w = -1; w <= 1; w++)
            Put(world, alongX ? mastX : mastX + w, y, alongX ? mastZ + w : mastZ, Cloths[2]);
    }
    //港池两岸高耸的引航灯柱
    private static void BuildNavLights(VoxelWorld world)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var z = side * 26;
            TemplateKit.Box(world, 31, 0, z - 1, 33, 0, z + 1, StoneDark);
            for (var y = 1; y <= 9; y++) Put(world, 32, y, z, y % 3 == 0 ? Stone : Wood);
            TemplateKit.Box(world, 30, 8, z, 34, 8, z, Wood);
            Put(world, 30, 9, z, LanternGlow);
            Put(world, 34, 9, z, LanternGlow);
            Put(world, 32, 10, z, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
        }
    }
    //渡口杂项：树、货堆与旗杆
    private static void DressHarbor(VoxelWorld world)
    {
        XianxiaFlora.PineTree(world, -30, 1, -30, 2101);
        XianxiaFlora.CherryTree(world, -10, 1, -30, 2102);
        XianxiaFlora.BroadleafTree(world, -24, 1, 26, 2103);
        foreach (var (x, z) in new[] { (-8, -12), (-6, 12), (-9, 22), (-5, 28) }) Crate(world, x, 1, z);
        for (var y = 2; y <= 8; y++) Put(world, -8, y, 0, Wood);
        TemplateKit.Box(world, -7, 7, 0, -3, 8, 0, Cloths[0]);
    }
    //按弧形分层的田面高度，前低后高共九层
    private static int FieldLevel(int x, int z) =>
        Math.Clamp((int)MathF.Floor((60F - z - x * x / 240F) / 13F), 0, 8);
    //九层弧形梯田、田埂、水渠与灵稻
    private static void BuildTerraces(VoxelWorld world)
    {
        for (var x = -58; x <= 58; x++)
        for (var z = -58; z <= 58; z++)
        {
            var level = FieldLevel(x, z);
            var border = Math.Abs(x) >= 56 || Math.Abs(z) >= 56;
            var band = 60F - z - x * x / 240F - level * 13F;
            var canal = Math.Abs(x - (24 + (int)MathF.Round(6F * MathF.Sin(z * .07F)))) <= 1;
            var higher = FieldLevel(x + 1, z) > level || FieldLevel(x - 1, z) > level
                || FieldLevel(x, z + 1) > level || FieldLevel(x, z - 1) > level;
            if (border)
            {
                for (var y = 0; y <= level; y++)
                {
                    Put(world, x, y, z, y == level
                        ? XianxiaPalette.Grain(XianxiaPalette.Stone, x, y, z, 1503, .06F)
                        : XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, y, z, 1502, .06F));
                }
                continue;
            }
            Put(world, x, level, z, XianxiaPalette.Grain(XianxiaPalette.Earth, x, level, z, 1501, .07F));
            if (level > 0)
            {
                Put(world, x, level - 1, z,
                    XianxiaPalette.Grain(XianxiaPalette.Earth, x, level - 1, z, 1504, .05F));
            }
            if (canal || band < 4F)
            {
                Put(world, x, level, z, XianxiaFlora.WaterBody(x, level, z));
            }
            else if (higher)
            {
                Put(world, x, level + 1, z,
                    XianxiaPalette.Grain(XianxiaPalette.Stone, x, level + 1, z, 1505, .05F));
                if (TemplateKit.Hash(x, level, z, 1506) % 5 == 0)
                {
                    Put(world, x, level + 2, z, XianxiaPalette.Solid(XianxiaPalette.Moss));
                }
            }
            else if (band >= 5F)
            {
                Rice(world, x, level, z);
            }
        }
    }
    //单株灵稻，青绿为主间有金黄熟穗
    private static void Rice(VoxelWorld world, int x, int level, int z)
    {
        if ((x + z * 3 + level) % 4 != 0) return;
        var roll = TemplateKit.Hash(x, level, z, 1601);
        var color = roll % 5 < 2 ? XianxiaPalette.SandGold
            : roll % 2 == 0 ? XianxiaPalette.Bamboo : XianxiaPalette.GrassLight;
        Put(world, x, level + 1, z, XianxiaPalette.Solid(color));
        if (roll % 19 == 0) Put(world, x, level + 2, z, XianxiaPalette.Solid(color));
        if (roll % 401 == 0) Put(world, x, level + 2, z, XianxiaPalette.Glow(XianxiaPalette.SpiritCyan));
    }
    //田边小屋与草垛
    private static void BuildFieldHut(VoxelWorld world)
    {
        const int minX = -24, maxX = -14, minZ = 10, maxZ = 18;
        var pad = 0;
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++) pad = Math.Max(pad, FieldLevel(x, z));
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            for (var y = 0; y <= pad; y++)
            {
                Put(world, x, y, z, XianxiaPalette.Grain(XianxiaPalette.Earth, x, y, z, 1520, .05F));
            }
            Put(world, x, pad, z, XianxiaPalette.Grain(XianxiaPalette.Stone, x, pad, z, 1521, .05F));
        }
        for (var y = pad + 1; y <= pad + 4; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            if (x != minX && x != maxX && z != minZ && z != maxZ) continue;
            if (z == maxZ && Math.Abs(x - (minX + maxX) / 2) <= 1 && y <= pad + 3) continue;
            Put(world, x, y, z, y == pad + 4 || x == minX || x == maxX ? Wood
                : XianxiaPalette.Grain(XianxiaPalette.SilkWhite, x, y, z, 1522, .04F));
        }
        Put(world, (minX + maxX) / 2, pad + 3, maxZ + 1, LanternGlow);
        TileRoof(world, minX, minZ, maxX, maxZ, pad + 5, 3, true, 1523);
        Haystack(world, minX - 5, FieldLevel(minX - 5, minZ + 2) + 1, minZ + 2, 1524);
        Haystack(world, minX - 8, FieldLevel(minX - 8, maxZ) + 1, maxZ, 1525);
    }
    //田头草垛
    private static void Haystack(VoxelWorld world, int x, int groundY, int z, int seed)
    {
        TemplateKit.Taper(world, x + .5, z + .5, groundY, groundY + 2, 2.2, .6,
            XianxiaPalette.Grain(XianxiaPalette.SandGold, x, groundY, z, seed, .06F));
        Put(world, x, groundY + 3, z, Wood);
    }
    //田边松、樱与阔叶树
    private static void PlantFieldTrees(VoxelWorld world)
    {
        XianxiaFlora.PineTree(world, -48, FieldLevel(-48, -46) + 1, -46, 1531);
        XianxiaFlora.PineTree(world, 46, FieldLevel(46, -50) + 1, -50, 1532);
        XianxiaFlora.CherryTree(world, -6, FieldLevel(-6, 22) + 1, 22, 1533);
    }
    //六边形崖体，前侧凿出下行的石阶槽
    private static void BuildCliff(VoxelWorld world)
    {
        for (var x = -16; x <= 16; x++)
        for (var z = -16; z <= 18; z++)
        {
            var stair = Math.Abs(x) <= 4 && z >= 9;
            var parapet = Math.Abs(x) == 5 && z is >= 9 and <= 17;
            var inside = InHex(x, z, 15) && !(Math.Abs(x) <= 4 && z >= 9);
            if (!inside && !stair && !parapet) continue;
            var top = stair ? Math.Clamp(8 - (z - 8), 0, 7)
                : parapet ? Math.Clamp(8 - (z - 8), 0, 7) + 1 : 8;
            for (var y = 0; y <= top; y++)
            {
                Put(world, x, y, z, y switch
                {
                    <= 1 => XianxiaPalette.Grain(XianxiaPalette.BasaltDeep, x, y, z, 1701, .09F),
                    <= 3 => XianxiaPalette.Grain(XianxiaPalette.Basalt, x, y, z, 1702, .08F),
                    <= 5 => XianxiaPalette.Grain(XianxiaPalette.StoneDark, x, y, z, 1703, .07F),
                    _ => XianxiaPalette.Grain(XianxiaPalette.Stone, x, y, z, 1704, .06F),
                });
            }
            if (top == 8 && TemplateKit.Hash(x, 8, z, 1705) % 9 == 0)
                Put(world, x, 8, z, XianxiaPalette.Solid(XianxiaPalette.Moss));
        }
    }
    //六角亭：石台、六柱、栏杆、攒尖顶与石桌
    private static void BuildHexPavilion(VoxelWorld world)
    {
        for (var x = -7; x <= 7; x++)
        for (var z = -7; z <= 7; z++)
        {
            if (InHex(x, z, 7)) Put(world, x, 9, z, (Math.Abs(x) + Math.Abs(z)) % 3 == 0
                ? XianxiaPalette.Jade(XianxiaPalette.JadeGreen) : Stone);
        }
        var columns = new (int X, int Z)[6];
        for (var index = 0; index < 6; index++)
        {
            var angle = index * MathF.PI / 3F;
            var cx = (int)MathF.Round(MathF.Cos(angle) * 5.6F);
            var cz = (int)MathF.Round(MathF.Sin(angle) * 5.6F);
            columns[index] = (cx, cz);
            Put(world, cx, 9, cz, StoneDark);
            for (var y = 10; y <= 14; y++) Put(world, cx, y, cz, y % 2 == 0 ? Wood : Plank);
        }
        for (var index = 0; index < 6; index++)
        {
            var (ax, az) = columns[index];
            var (bx, bz) = columns[(index + 1) % 6];
            for (var step = 0; step <= 6; step++)
            {
                var x = (int)Math.Round(ax + (bx - ax) * step / 6.0);
                var z = (int)Math.Round(az + (bz - az) * step / 6.0);
                Put(world, x, 15, z, Wood);
                if ((az + bz) / 2.0 > 3.5 || step is 0 or 6 || step % 2 == 1) continue;
                Put(world, x, 10, z, Wood);
                Put(world, x, 11, z, Wood);
            }
        }
        for (var layer = 0; layer <= 4; layer++)
        for (var x = -9; x <= 9; x++)
        for (var z = -9; z <= 9; z++)
        {
            if (InHex(x, z, 8F - layer * 1.7F)) Put(world, x, 16 + layer, z, Tile);
        }
        for (var index = 0; index < 6; index++)
        {
            var angle = index * MathF.PI / 3F;
            Put(world, (int)MathF.Round(MathF.Cos(angle) * 8.6F), 17,
                (int)MathF.Round(MathF.Sin(angle) * 8.6F), TileLight);
        }
        for (var y = 21; y <= 23; y++) Put(world, 0, y, 0, Brass);
        Put(world, 0, 24, 0, LanternGlow);
        TemplateKit.Box(world, -1, 10, -1, 1, 10, 1, Stone);
        Put(world, 0, 11, 0, LanternGlow);
        Put(world, 3, 10, 0, Wood);
        Put(world, 0, 10, 3, Wood);
    }
    //崖顶点缀：松树、石灯与石碑
    private static void DressCliff(VoxelWorld world)
    {
        XianxiaFlora.PineTree(world, -11, 9, -8, 1751);
        XianxiaFlora.PineTree(world, 10, 9, -9, 1752);
        for (var index = -13; index <= 13; index += 4)
        {
            Put(world, index, 9, -12, StoneDark);
            Put(world, index, 10, -12, Stone);
        }
        for (var y = 9; y <= 11; y++) Put(world, 7, y, 6, StoneDark);
        Put(world, 7, 12, 6, XianxiaPalette.Glow(XianxiaPalette.SpiritBlue));
    }
    //六边形范围判定
    private static bool InHex(int x, int z, double radius) =>
        Math.Abs(x) <= radius && Math.Abs(z) <= radius * .92
        && Math.Abs(x) + Math.Abs(z) * .58 <= radius * 1.14;
}
