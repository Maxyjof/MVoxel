namespace MVoxel.Voxels;

/// <summary>
/// MagicaVoxel-inspired 256-entry color palette. The classic editor keeps
/// 250 ordinary ramp colors plus a handful of special low-alpha entries; MVoxel
/// follows the same structure so the brush UI, transparent materials and the
/// renderer share one deterministic table. Voxel colors remain full RGBA32 —
/// the palette is an authoring convenience, not a storage limit.
/// </summary>
public static class VoxelPalette
{
    public const int EntryCount = 256;

    /// <summary>Indices below this entry use graduated alpha (glass/ghost tones).</summary>
    public const int TransparentCount = 8;

    public static Rgba32[] Build()
    {
        var colors = new Rgba32[EntryCount];

        // The first slot is fully transparent “air” so erase-style picks have a
        // stable representative color at index 0.
        colors[0] = new Rgba32(0, 0, 0, 0);
        for (var i = 1; i < TransparentCount; i++)
        {
            var alpha = (byte)(i * 32);
            colors[i] = new Rgba32(180, 190, 205, alpha);
        }

        // 248 ramp slots: 16 hue families. Brightness climbs steadily while the
        // saturation cycles pure → pastel → deep, mirroring the reading order
        // artists know from MagicaVoxel's palette grid.
        const int familySize = 15;
        for (var slot = TransparentCount; slot < EntryCount; slot++)
        {
            var linear = slot - TransparentCount;
            var hueIndex = linear / familySize;
            var step = linear % familySize;
            var hue = (hueIndex * 22.5F + step * 0.75F) % 360F;
            var value = 0.45F + 0.55F * (step / (float)(familySize - 1));
            var saturation = step % 3 switch
            {
                0 => 0.80F,
                1 => 0.42F,
                _ => 0.95F,
            };

            var rgb = FromHsv(hue / 360F, saturation, value);
            colors[slot] = new Rgba32(rgb.R, rgb.G, rgb.B, 255);
        }

        return colors;
    }

    private static (byte R, byte G, byte B) FromHsv(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6F);
        var f = h * 6F - i;
        var p = v * (1F - s);
        var q = v * (1F - f * s);
        var t = v * (1F - (1F - f) * s);
        var (r, g, b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return (ToByte(r), ToByte(g), ToByte(b));

        static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0F, 1F) * 255F);
    }
}
