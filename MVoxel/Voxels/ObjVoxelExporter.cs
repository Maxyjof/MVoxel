using System.Globalization;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>将体素的可见外表面导出为带顶点颜色的 OBJ 网格。</summary>
public static class ObjVoxelExporter
{
    public static void Write(string path, VoxelWorld world)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false), 1024 * 1024))
            {
                writer.WriteLine("# MVoxel OBJ: vertex RGB; Y is up");
                long vertex = 1;
                foreach (var (p, value) in world.EnumerateOccupied())
                {
                    for (var face = 0; face < 6; face++)
                    {
                        var (dx, dy, dz) = Directions[face];
                        if (world.TryGet(new VoxelCoord64(p.X + dx, p.Y + dy, p.Z + dz), out _)) continue;
                        foreach (var (x, y, z) in Corners[face])
                            writer.WriteLine(FormattableString.Invariant($"v {p.X + x} {p.Y + y} {p.Z + z} {value.Color.R / 255.0:F6} {value.Color.G / 255.0:F6} {value.Color.B / 255.0:F6}"));
                        writer.WriteLine(FormattableString.Invariant($"f {vertex} {vertex + 1} {vertex + 2} {vertex + 3}"));
                        vertex += 4;
                    }
                }
            }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static readonly (int, int, int)[] Directions = [(1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1)];
    private static readonly (int, int, int)[][] Corners =
    [
        [(1,0,0),(1,1,0),(1,1,1),(1,0,1)],
        [(0,0,1),(0,1,1),(0,1,0),(0,0,0)],
        [(0,1,0),(0,1,1),(1,1,1),(1,1,0)],
        [(0,0,1),(0,0,0),(1,0,0),(1,0,1)],
        [(1,0,1),(1,1,1),(0,1,1),(0,0,1)],
        [(0,0,0),(0,1,0),(1,1,0),(1,0,0)]
    ];
}
