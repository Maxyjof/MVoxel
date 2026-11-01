using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 模型倍数缩放的规则与几何对齐。放大只接受整数倍：体素是整格复制，不存在半个体素；
/// 缩小接受小数倍，结果从模型的最小角开始成组，始终落在原始体素格上。
/// </summary>
public static class ModelScaleRule
{
    /// <summary>允许的最小倍数</summary>
    public const float Minimum = 0.125F;

    /// <summary>允许的最大倍数</summary>
    public const float Maximum = 8F;

    /// <summary>判定倍数等于 1x 的容差</summary>
    public const float Tolerance = 0.001F;

    /// <summary>单次缩放的输出体素预算，防止误填大倍数耗尽内存</summary>
    private const long OutputBudget = 50_000_000;

    /// <summary>三轴倍数是否一致，决定状态栏显示 2x 还是 2×1×3</summary>
    /// <param name="x">X 轴倍数</param>
    /// <param name="y">Y 轴倍数</param>
    /// <param name="z">Z 轴倍数</param>
    /// <returns>三轴在容差内相等时为 true</returns>
    public static bool IsUniform(float x, float y, float z) =>
        MathF.Abs(x - y) <= Tolerance && MathF.Abs(y - z) <= Tolerance;

    /// <summary>倍数是否就是基准尺寸</summary>
    /// <param name="factor">缩放倍数</param>
    /// <returns>落在 1x 容差内时为 true</returns>
    public static bool IsIdentity(float factor) => MathF.Abs(factor - 1F) <= Tolerance;

    /// <summary>
    /// 校验并规范化目标倍数。放大按最近的整数倍处理，缩小保留三位小数。
    /// </summary>
    /// <param name="requested">用户填写的倍数</param>
    /// <param name="factor">实际使用的倍数</param>
    /// <param name="note">倍数被调整或不可用时的说明，正常时为空</param>
    /// <returns>倍数可用时返回 true</returns>
    public static bool TryNormalize(float requested, out float factor, out string note)
    {
        factor = requested;
        note = string.Empty;
        if (!float.IsFinite(requested) || requested <= 0F)
        {
            note = "缩放倍数必须大于零";
            return false;
        }

        if (requested < Minimum)
        {
            note = $"最小倍数是 {Minimum:0.###}x";
            return false;
        }

        if (requested > 1F)
        {
            var integer = MathF.Round(requested);
            if (integer > Maximum)
            {
                note = $"最大倍数是 {Maximum:0.#}x";
                return false;
            }

            if (MathF.Abs(requested - integer) > Tolerance) note = $"放大只支持整数倍，已按 {integer:0}x 处理";
            factor = integer;
            return true;
        }

        factor = MathF.Round(requested, 3);
        return true;
    }

    /// <summary>
    /// 按目标倍数从基准模型重采样。<paramref name="source"/> 应当已经摆到对象当前位置，
    /// 结果的包围盒最小角会被对齐回同一个角：空层不会让模型整体漂移，
    /// 反复缩放也不会累积半格偏移。
    /// </summary>
    /// <param name="source">基准模型，最小角与 <paramref name="anchor"/> 重合</param>
    /// <param name="factorX">X 轴目标倍数</param>
    /// <param name="factorY">Y 轴目标倍数</param>
    /// <param name="factorZ">Z 轴目标倍数</param>
    /// <param name="anchor">缩放锚点，取对象当前包围盒最小角</param>
    /// <param name="result">重采样结果，已对齐到锚点</param>
    /// <param name="error">失败原因</param>
    /// <param name="cancellation">取消标记</param>
    /// <returns>成功时为 true</returns>
    public static bool TryResample(VoxelWorld source, float factorX, float factorY, float factorZ,
        VoxelCoord64 anchor, out VoxelWorld result, out string error, CancellationToken cancellation = default)
    {
        result = new VoxelWorld();
        error = string.Empty;
        try
        {
            var scaled = VoxelRescaler.Scale(Align(source, anchor), factorX, factorY, factorZ, anchor,
                out _, cancellation, null, OutputBudget);
            if (scaled.OccupiedVoxelCount == 0)
            {
                error = "缩放后模型为空，请调整倍数";
                return false;
            }

            result = Align(scaled, anchor);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 把世界整体平移到指定最小角。缩放前后的锚点是同一个整数格，
    /// 因此结果始终对齐体素网格。
    /// </summary>
    /// <param name="world">待对齐的世界</param>
    /// <param name="origin">目标最小角</param>
    /// <returns>已对齐的世界，位置本来就正确时直接返回入参</returns>
    public static VoxelWorld Align(VoxelWorld world, VoxelCoord64 origin) =>
        world.TryGetBounds(out var minimum, out _) && minimum != origin
            ? Translate(world, origin - minimum)
            : world;

    /// <summary>按整数偏移平移世界</summary>
    /// <param name="world">源世界</param>
    /// <param name="offset">偏移量</param>
    /// <returns>平移后的新世界</returns>
    public static VoxelWorld Translate(VoxelWorld world, VoxelCoord64 offset)
    {
        if (offset == VoxelCoord64.Zero) return world;
        var translated = new VoxelWorld();
        foreach (var cell in world.EnumerateOccupied()) translated.Set(cell.Coordinate + offset, cell.Value);
        return translated;
    }

    /// <summary>把世界转成包围盒最小角为零的局部坐标</summary>
    /// <param name="world">任意位置的世界</param>
    /// <returns>局部坐标副本</returns>
    public static VoxelWorld ToLocal(VoxelWorld world) =>
        world.TryGetBounds(out var minimum, out _) ? Translate(world, VoxelCoord64.Zero - minimum) : new VoxelWorld();
}
