namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 大陆级确定性噪声：分形起伏、山脊线与区域散布，
/// 全部由整数哈希驱动，同一坐标在任意次生成中结果一致
/// </summary>
internal static class XianxiaNoise
{
    /// <summary>平滑插值后的分形噪声，返回 0 到 1</summary>
    /// <param name="x">世界 X</param>
    /// <param name="z">世界 Z</param>
    /// <param name="seed">种子</param>
    /// <param name="octaves">倍频数</param>
    /// <param name="frequency">基础频率</param>
    /// <returns>噪声值</returns>
    public static float Fbm(float x, float z, int seed, int octaves = 4, float frequency = .004F)
    {
        var sum = 0F;
        var amplitude = 1F;
        var total = 0F;
        var current = frequency;
        for (var octave = 0; octave < octaves; octave++)
        {
            sum += Sample(x * current, z * current, seed + octave * 977) * amplitude;
            total += amplitude;
            amplitude *= .5F;
            current *= 2.02F;
        }

        return sum / total;
    }

    /// <summary>山脊噪声，返回 0 到 1，峰顶锐利适合表现山脉走向</summary>
    /// <param name="x">世界 X</param>
    /// <param name="z">世界 Z</param>
    /// <param name="seed">种子</param>
    /// <param name="octaves">倍频数</param>
    /// <param name="frequency">基础频率</param>
    /// <returns>山脊值</returns>
    public static float Ridged(float x, float z, int seed, int octaves = 4, float frequency = .0032F)
    {
        var sum = 0F;
        var amplitude = .55F;
        var total = 0F;
        var current = frequency;
        for (var octave = 0; octave < octaves; octave++)
        {
            var sample = Sample(x * current, z * current, seed + octave * 6151);
            var ridge = 1F - MathF.Abs(sample * 2F - 1F);
            sum += ridge * ridge * amplitude;
            total += amplitude;
            amplitude *= .48F;
            current *= 2.07F;
        }

        return sum / total;
    }

    /// <summary>两个整数坐标的稳定散布值，返回 0 到 1</summary>
    /// <param name="x">X</param>
    /// <param name="z">Z</param>
    /// <param name="seed">种子</param>
    /// <returns>散布值</returns>
    public static float Hash01(int x, int z, int seed) => TemplateKit.Hash(x, 0, z, seed) / (float)0x7FFFFFFF;

    /// <summary>平滑阶跃</summary>
    /// <param name="edge0">下边界</param>
    /// <param name="edge1">上边界</param>
    /// <param name="value">输入值</param>
    /// <returns>0 到 1 的过渡值</returns>
    public static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge0 == edge1) return value < edge0 ? 0F : 1F;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0F, 1F);
        return t * t * (3F - 2F * t);
    }

    /// <summary>按幂次收束的高斯衰减，用于孤峰与火山锥</summary>
    /// <param name="distance">到中心的距离</param>
    /// <param name="radius">影响半径</param>
    /// <param name="power">收束指数</param>
    /// <returns>0 到 1 的权重</returns>
    public static float Falloff(float distance, float radius, float power = 1.6F)
    {
        if (distance >= radius) return 0F;
        var t = 1F - distance / radius;
        return MathF.Pow(t, power);
    }

    /// <summary>把噪声值压缩到指定区间</summary>
    /// <param name="value">输入</param>
    /// <param name="minimum">下限</param>
    /// <param name="maximum">上限</param>
    /// <returns>压缩后的值</returns>
    public static float Range(float value, float minimum, float maximum) =>
        minimum + Math.Clamp(value, 0F, 1F) * (maximum - minimum);

    private static float Sample(float x, float z, int seed)
    {
        var xi = (int)MathF.Floor(x);
        var zi = (int)MathF.Floor(z);
        var xf = x - xi;
        var zf = z - zi;
        var smoothX = xf * xf * (3F - 2F * xf);
        var smoothZ = zf * zf * (3F - 2F * zf);
        var top = Value(xi, zi, seed) * (1F - smoothX) + Value(xi + 1, zi, seed) * smoothX;
        var bottom = Value(xi, zi + 1, seed) * (1F - smoothX) + Value(xi + 1, zi + 1, seed) * smoothX;
        return top * (1F - smoothZ) + bottom * smoothZ;
    }

    private static float Value(int x, int z, int seed) =>
        (TemplateKit.Hash(x, 0, z, seed) & 0x7FFFFFFF) / (float)0x7FFFFFFF;
}
