namespace MVoxel.Rendering;

/// <summary>
/// 体素距离 LOD 配置：按相机到区块的距离把体素分档粗化，远处区块以 2^k 大块近似。
/// 场景窗口与渲染窗口各持一份，默认关闭。距离阈值按层级递增，第一档内保持全细节
/// </summary>
public sealed class VoxelLodSettings
{
    /// <summary>最大 LOD 级数，第六级对应 64 格大块</summary>
    public const int MaximumLevels = 6;

    /// <summary>最小距离阈值（格）</summary>
    public const float MinimumDistance = 8F;

    /// <summary>是否启用距离 LOD</summary>
    public bool Enabled;

    /// <summary>LOD 级数（含全细节第 0 级），1 到 6</summary>
    public int LevelCount = 3;

    /// <summary>各层级切换距离（格），第 k 级在 Distances[k-1] 之外生效，必须递增</summary>
    public float[] Distances = [64F, 128F, 256F];

    /// <summary>
    /// 规范化：级数夹取、距离去重排序补齐并保证递增
    /// </summary>
    public void ClampAll()
    {
        LevelCount = Math.Clamp(LevelCount, 1, MaximumLevels);
        var source = Distances is { Length: > 0 } ? Distances : [];
        var levels = LevelCount - 1;
        var distances = new float[Math.Max(levels, 0)];
        var previous = 0F;
        for (var index = 0; index < distances.Length; index++)
        {
            var value = index < source.Length && float.IsFinite(source[index]) ? source[index] : previous + 64F;
            previous = Math.Max(value, previous + MinimumDistance);
            distances[index] = Math.Clamp(previous, MinimumDistance, 1_000_000F);
        }

        Distances = distances;
    }

    /// <summary>
    /// 求给定距离所属 LOD 级（0 为全细节）
    /// </summary>
    /// <param name="distance">相机到目标的距离（格）</param>
    /// <returns>粗化级，块尺寸为 2 的该数次方</returns>
    public int LevelFor(float distance)
    {
        if (!Enabled)
        {
            return 0;
        }

        var level = 0;
        var bound = Math.Min(Distances?.Length ?? 0, Math.Max(LevelCount - 1, 0));
        for (var index = 0; index < bound; index++)
        {
            if (distance >= Distances[index])
            {
                level = index + 1;
            }
        }

        return level;
    }

    /// <summary>
    /// 独立副本，供设置克隆与导出快照使用
    /// </summary>
    /// <returns>深拷贝后的配置</returns>
    public VoxelLodSettings Clone() => new()
    {
        Enabled = Enabled,
        LevelCount = LevelCount,
        Distances = (float[])(Distances ?? []).Clone(),
    };
}
