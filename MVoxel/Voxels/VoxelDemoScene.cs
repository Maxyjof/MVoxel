using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// Builds the deterministic starter scene shown when a fresh project has no
/// saved model yet. The layout borrows the classic MagicaVoxel demo vocabulary:
/// a small house with roof and windows, a tree, a lantern post with an
/// emissive lamp and a stone path — enough variety to exercise palette
/// shading, AO, translucency, emission and shadows without any user input.
/// </summary>
public static class VoxelDemoScene
{
    public static VoxelWorld Build(Rgba32[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var world = new VoxelWorld();

        // Hand-picked MagicaVoxel-ish starter palette for the demo build.
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

    private static void BuildHouse(VoxelWorld world, Rgba32 wall, Rgba32 wallDark, Rgba32 roof,
        Rgba32 wood, Rgba32 glass, Rgba32 lamp)
    {
        // 9×7 footprint, walls from y=0 to y=5, doorway on +Z side. The ground
        // plane sits at y=0, so the first occupied cell layer is y=0.
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

        // Floor + roof pyramid.
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

        // A lamp block glows in the doorway.
        world.Set(new VoxelCoord64(4, 4, 6), new VoxelValue(lamp, Flags: VoxelFlags.Emissive));
    }

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

    private static void BuildPath(VoxelWorld world, Rgba32 stone, Rgba32 grassy)
    {
        // A short stone apron in front of the door (house sits at z 0..6, door at z=6).
        for (var z = 7; z <= 11; z++)
        for (var x = 1; x <= 7; x++)
        {
            var edge = z == 7 || x is 1 or 7;
            world.Set(new VoxelCoord64(x, 0, z), new VoxelValue(edge ? stone : grassy));
        }
    }

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
