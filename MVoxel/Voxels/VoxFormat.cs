using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// Reads and writes the MagicaVoxel “.vox” binary model (single SIZE/XYZRGBA
/// model). The format is untrusted input: every count is bounded before any
/// allocation happens, so a corrupt file cannot make the editor explode.
///
/// MagicaVoxel is Z-up; MVoxel is Y-up. The mapping is
/// world.X = vox.X, world.Y = vox.Z (up), world.Z = vox.Y (depth).
/// </summary>
public static class VoxFormat
{
    public const uint Magic = 0x0058_4F56; // "VOX "
    public const int MaximumVoxels = 4_000_000;
    public const int MaximumAxisExtent = 1024;

    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
        {
            throw new InvalidOperationException("Cannot export an empty voxel world.");
        }

        var sizeX = (int)(maximum.X - minimum.X) + 1;
        var sizeY = (int)(maximum.Z - minimum.Z) + 1; // vox depth = world Z
        var sizeZ = (int)(maximum.Y - minimum.Y) + 1; // vox up     = world Y
        if (sizeX > MaximumAxisExtent || sizeY > MaximumAxisExtent || sizeZ > MaximumAxisExtent)
        {
            throw new InvalidOperationException("Voxel model exceeds the single-file .vox extent limit.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(150);
        // Chunk layout on disk: id(4) + contentSize(4) + childrenSize(4) + content.
        WriteAscii(writer, "MAIN");
        writer.Write(0);
        writer.Write(0);
        WriteAscii(writer, "SIZE");
        writer.Write(12);
        writer.Write(0);
        writer.Write(sizeX);
        writer.Write(sizeY);
        writer.Write(sizeZ);
        var voxels = world.EnumerateOccupied().ToArray();
        WriteAscii(writer, "XYZA");
        writer.Write(4 + voxels.Length * 7);
        writer.Write(0);
        writer.Write(voxels.Length);
        foreach (var (coordinate, value) in voxels)
        {
            writer.Write((byte)(coordinate.X - minimum.X));
            writer.Write((byte)(coordinate.Z - minimum.Z));
            writer.Write((byte)(coordinate.Y - minimum.Y));
            writer.Write(value.Color.R);
            writer.Write(value.Color.G);
            writer.Write(value.Color.B);
            writer.Write(value.Color.A);
        }

        writer.Flush();
        AtomicFile.WriteBytes(path, stream.ToArray());
    }

    public static VoxelWorld Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes, VoxelCoord64.Zero);
    }

    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (bytes.Length < 8 || reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a VoxEdit/MagicaVoxel .vox file.");
        }

        reader.ReadUInt32(); // engine version — accept any
        var world = new VoxelWorld();
        var size = (X: 0, Y: 0, Z: 0);
        var cursor = 8L;
        while (cursor + 8 <= bytes.Length)
        {
            stream.Position = cursor;
            var chunkId = new string(reader.ReadChars(4));
            var contentSize = reader.ReadInt32();
            var childrenSize = reader.ReadInt32();
            if (contentSize < 0 || childrenSize < 0 ||
                contentSize > bytes.Length || childrenSize > bytes.Length)
            {
                throw new InvalidDataException($"Corrupt .vox chunk size for '{chunkId}'.");
            }

            var next = cursor + 12 + contentSize + childrenSize;
            if (next > bytes.Length || next <= cursor)
            {
                throw new InvalidDataException(".vox chunk extends beyond the file.");
            }

            switch (chunkId)
            {
                case "SIZE":
                {
                    size.X = reader.ReadInt32();
                    size.Y = reader.ReadInt32();
                    size.Z = reader.ReadInt32();
                    if (size.X is < 1 or > MaximumAxisExtent ||
                        size.Y is < 1 or > MaximumAxisExtent ||
                        size.Z is < 1 or > MaximumAxisExtent)
                    {
                        throw new InvalidDataException(".vox SIZE exceeds supported bounds.");
                    }

                    break;
                }
                case "XYZI" or "XYZA":
                {
                    var count = reader.ReadInt32();
                    var bytesPerVoxel = chunkId == "XYZA" ? 7 : 4;
                    if (count is < 0 or > MaximumVoxels ||
                        (long)count * bytesPerVoxel > contentSize - 4)
                    {
                        throw new InvalidDataException(".vox voxel count is inconsistent with its chunk size.");
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var x = reader.ReadByte();
                        var y = reader.ReadByte();
                        var z = reader.ReadByte();
                        byte r, g, b, a;
                        if (chunkId == "XYZA")
                        {
                            r = reader.ReadByte();
                            g = reader.ReadByte();
                            b = reader.ReadByte();
                            a = reader.ReadByte();
                        }
                        else
                        {
                            var colorIndex = reader.ReadByte();
                            (r, g, b) = LegacyColor(colorIndex);
                            a = colorIndex == 0 ? (byte)0 : (byte)255;
                        }

                        // The palette index 0 (or a fully transparent entry)
                        // means “air” and is intentionally not stored.
                        if (a == 0) continue;
                        world.Set(origin + new VoxelCoord64(x, z, y),
                            new VoxelValue(new Rgba32(r, g, b, a), Flags: a < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
                    }

                    break;
                }
            }

            cursor = next;
        }

        return world;
    }

    private static void WriteAscii(BinaryWriter writer, string value)
    {
        foreach (var character in value) writer.Write(character);
    }

    /// <summary>
    /// The historical 256-color .vox palette index table. Only used when
    /// importing very old files; MVoxel itself always writes XYZRGBA.
    /// </summary>
    private static (byte, byte, byte) LegacyColor(int index) => index switch
    {
        0 => (0, 0, 0),
        1 => (255, 255, 255),
        2 => (170, 170, 170),
        3 => (85, 85, 85),
        4 => (255, 0, 0),
        5 => (255, 170, 0),
        6 => (255, 255, 0),
        7 => (0, 255, 0),
        8 => (0, 255, 255),
        9 => (0, 0, 255),
        10 => (255, 0, 255),
        11 => (85, 85, 255),
        12 => (170, 85, 0),
        13 => (170, 127, 85),
        14 => (127, 170, 0),
        15 => (255, 127, 39),
        _ => ((byte)(index * 37 % 256), (byte)(index * 73 % 256), (byte)(index * 113 % 256)),
    };
}
