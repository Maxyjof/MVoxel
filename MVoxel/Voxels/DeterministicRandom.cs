namespace MVoxel.Voxels;

/// <summary>
/// 确定性随机：同样的输入在任何进程、任何机器上都得到同一个结果
/// </summary>
/// <remarks>
/// 不能使用 System.Random 或 System.HashCode：前者依赖调用顺序，后者每个进程的种子不同，
/// 都会让“同一参数重开项目重铺结果一致”失效。这里用 splitmix64 混合，
/// 输入是种子、槽位行列与用途，输出与遍历顺序无关
/// </remarks>
public static class DeterministicRandom
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// 把种子与若干索引混合成一个 64 位结果
    /// </summary>
    /// <param name="seed">用户种子</param>
    /// <param name="a">第一维索引，通常是列</param>
    /// <param name="b">第二维索引，通常是行</param>
    /// <param name="salt">用途标记，区分变体、旋转、抖动</param>
    /// <returns>混合结果</returns>
    public static ulong Mix(ulong seed, long a, long b, uint salt = 0)
    {
        unchecked
        {
            var state = seed + GoldenGamma;
            state = Scramble(state ^ ((ulong)a * 0xD1B54A32D192ED03UL));
            state = Scramble(state + ((ulong)b * 0x94D049BB133111EBUL));
            state = Scramble(state + (ulong)salt * 0xBF58476D1CE4E5B9UL);
            return state;
        }
    }

    /// <summary>
    /// 取区间内的整数，上界不含
    /// </summary>
    /// <param name="seed">用户种子</param>
    /// <param name="a">第一维索引</param>
    /// <param name="b">第二维索引</param>
    /// <param name="salt">用途标记</param>
    /// <param name="minimum">下界，含</param>
    /// <param name="maximumExclusive">上界，不含</param>
    /// <returns>区间内的整数</returns>
    public static int Range(ulong seed, long a, long b, uint salt, int minimum, int maximumExclusive)
    {
        if (maximumExclusive <= minimum) return minimum;
        var span = (ulong)(maximumExclusive - minimum);
        return minimum + (int)(Mix(seed, a, b, salt) % span);
    }

    /// <summary>
    /// splitmix64 的固定混合步骤
    /// </summary>
    private static ulong Scramble(ulong value)
    {
        unchecked
        {
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
