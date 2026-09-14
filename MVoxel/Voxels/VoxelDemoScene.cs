using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 新项目还没有保存过模型时生成的确定性示范场景，借用 MagicaVoxel 经典演示的语汇：
/// 带屋顶和玻璃窗的小屋、两棵树、一盏自发光提灯和石板草地，
/// 无需任何用户输入就能同时检验调色着色、角点 AO、半透明、自发光和阴影
/// 所有物体贴地摆放（首层格子 y=0，地面平面在 y=0）
/// </summary>
public static class VoxelDemoScene
{
    /// <summary>
    /// 构建示范场景
    /// </summary>
    /// <param name="palette">当前生效的调色板，保留参数以便未来让场景跟随项目色板</param>
    /// <returns>包含全部物体的新世界</returns>
    public static VoxelWorld Build(Rgba32[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var world = new VoxelWorld();

        // 手挑的 MagicaVoxel 风格起步配色
        var wall = new Rgba32(226, 214, 190);
        var wallDark = new Rgba32(186, 170, 146);
        var roof = new Rgba32(196, 82, 62);
        var wood = new Rgba32(128, 90, 56);
        var leaf = new Rgba32(96, 168, 92);
        var leafLight = new Rgba32(128, 198, 112);
        var stone = new Rgba32(150, 152, 160);
        var grass = new Rgba32(128, 178, 116);
        var glass = new Rgba32(150, 205, 240, 120);
        var lamp = new Rgba32(255, 214, 120);

        BuildHouse(world, wall, wallDark, roof, wood, glass, lamp);
        BuildTree(world, -6, 0, 5, wood, leaf, leafLight);
        BuildTree(world, 8, 0, -5, wood, leaf, leafLight);
        BuildPath(world, stone, grass);
        BuildLantern(world, wood, lamp);
        return world;
    }

    /// <summary>
    /// 建造小屋：9×7 占地，墙体 y=0 到 5，门口在 +Z 面，屋顶为四层棱台
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="wall">主墙面颜色</param>
    /// <param name="wallDark">墙基颜色</param>
    /// <param name="roof">屋顶颜色</param>
    /// <param name="wood">地板与木门颜色</param>
    /// <param name="glass">半透明玻璃颜色</param>
    /// <param name="lamp">门口自发光灯颜色</param>
    private static void BuildHouse(VoxelWorld world, Rgba32 wall, Rgba32 wallDark, Rgba32 roof,
        Rgba32 wood, Rgba32 glass, Rgba32 lamp)
    {
        for (var y = 0; y <= 5; y++)
        for (var z = 0; z <= 6; z++)
        for (var x = 0; x <= 8; x++)
        {
            var shell = x is 0 or 8 || z is 0 or 6;
            if (!shell) continue;
            var doorway = z == 6 && x is 3 or 4 && y <= 3;
            if (doorway) continue;
            var window = (z is 0 or 6 && (x is 1 or 7) && y is 2 or 3) ||
                         (x is 0 or 8 && z is 2 or 4 && y is 2 or 3);
            var value = window
                ? new VoxelValue(glass, Flags: VoxelFlags.Translucent)
                : new VoxelValue(y == 0 ? wallDark : wall);
            world.Set(new VoxelCoord64(x, y, z), value);
        }

        // 地板与四层屋顶棱台
        for (var z = 0; z <= 6; z++)
        for (var x = 0; x <= 8; x++)
        {
            world.Set(new VoxelCoord64(x, 0, z), new VoxelValue(wood));
        }

        for (var layer = 0; layer < 4; layer++)
        {
            var y = (long)6 + layer;
            for (var z = layer; z <= 6 - layer; z++)
            for (var x = layer; x <= 8 - layer; x++)
            {
                var edge = x == layer || x == 8 - layer || z == layer || z == 6 - layer;
                var cap = layer == 3;
                if (!edge && !cap) continue;
                world.Set(new VoxelCoord64(x, y, z), new VoxelValue(roof));
            }
        }

        // 门口上方一块自发光灯
        world.Set(new VoxelCoord64(4, 4, 6), new VoxelValue(lamp, Flags: VoxelFlags.Emissive));
    }

    /// <summary>
    /// 建树：五格树干加扁球形树冠，树冠每隔三格混入亮色叶
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="baseX">树根 X</param>
    /// <param name="baseY">树根 Y（地面为 0）</param>
    /// <param name="baseZ">树根 Z</param>
    /// <param name="wood">树干颜色</param>
    /// <param name="leaf">主叶色</param>
    /// <param name="leafLight">亮叶色</param>
    private static void BuildTree(VoxelWorld world, long baseX, long baseY, long baseZ,
        Rgba32 wood, Rgba32 leaf, Rgba32 leafLight)
    {
        for (var y = 0; y <= 4; y++)
        {
            world.Set(new VoxelCoord64(baseX, baseY + y, baseZ), new VoxelValue(wood));
        }

        for (var dy = 0; dy < 5; dy++)
        for (var dz = -2; dz <= 2; dz++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var blob = dx * dx + dy * dy * 2 + dz * dz <= 6;
            if (!blob) continue;
            var color = (dx + dz + dy) % 3 == 0 ? leafLight : leaf;
            world.Set(new VoxelCoord64(baseX + dx, baseY + 4 + dy, baseZ + dz), new VoxelValue(color));
        }
    }

    /// <summary>
    /// 门前石板与草地拼花，贴着地面第一层
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="stone">石板颜色</param>
    /// <param name="grassy">草地颜色</param>
    private static void BuildPath(VoxelWorld world, Rgba32 stone, Rgba32 grassy)
    {
        for (var z = 7; z <= 11; z++)
        for (var x = 1; x <= 7; x++)
        {
            var edge = z == 7 || x is 1 or 7;
            world.Set(new VoxelCoord64(x, 0, z), new VoxelValue(edge ? stone : grassy));
        }
    }

    /// <summary>
    /// 提灯：木杆、自发光灯头与顶盖
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="wood">木杆颜色</param>
    /// <param name="lamp">灯头颜色</param>
    private static void BuildLantern(VoxelWorld world, Rgba32 wood, Rgba32 lamp)
    {
        for (var y = 0; y <= 6; y++)
        {
            world.Set(new VoxelCoord64(10, y, 9), new VoxelValue(wood));
        }

        world.Set(new VoxelCoord64(10, 7, 9), new VoxelValue(lamp, Flags: VoxelFlags.Emissive));
        world.Set(new VoxelCoord64(10, 8, 9), new VoxelValue(wood));
    }
}
