namespace MVoxel.Voxels;

/// <summary>
/// MagicaVoxel 风格的 256 项颜色调色板，编辑器色板与项目色板的默认来源
/// 前若干项是带渐隐 Alpha 的透明色（玻璃与幽灵色），其余为 16 个色相族、
/// 每族 15 级明度的确定性渐变；体素存储仍是完整 RGBA，调色板只是编辑便利
/// </summary>
public static class VoxelPalette
{
    /// <summary>
    /// 调色板固定条目数
    /// </summary>
    public const int EntryCount = 256;

    /// <summary>
    /// 低透明特殊色数量，索引小于该值的条目带渐隐 Alpha
    /// </summary>
    public const int TransparentCount = 8;

    /// <summary>
    /// 生成默认色板，同一实现跨平台输出完全一致（有测试锁定）
    /// </summary>
    /// <returns>256 项色表</returns>
    public static Rgba32[] Build()
    {
        var colors = new Rgba32[EntryCount];

        // 0 号是全透明"空气"，擦除类操作在色板上有一个稳定的代表色
        colors[0] = new Rgba32(0, 0, 0, 0);
        for (var i = 1; i < TransparentCount; i++)
        {
            var alpha = (byte)(i * 32);
            colors[i] = new Rgba32(180, 190, 205, alpha);
        }

        // 248 个渐变槽位：16 个色相族，明度逐级上升，
        // 饱和度按 纯色、粉彩、浓色 三格轮换，对应 MagicaVoxel 色板的阅读习惯
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

    /// <summary>
    /// HSV 转 8 位 RGB
    /// </summary>
    /// <param name="h">色相，0 到 1</param>
    /// <param name="s">饱和度，0 到 1</param>
    /// <param name="v">明度，0 到 1</param>
    /// <returns>三分量字节颜色</returns>
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
