using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>
/// 局部后处理覆盖组件参数，不承担全局环境、雾或天气职责
/// </summary>
public sealed class PostProcessOverrideComponentData
{
    public bool Enabled = true;
    public float Weight = 1F;
    public float Exposure;
    public float BloomIntensity;
    public float BloomThreshold = 1F;
    public float BloomRadius = 4F;
    public float Vignette;

    public PostProcessOverrideComponentData Normalize()
    {
        Weight = Clamp(Weight, 0F, 1F, 1F);
        Exposure = Clamp(Exposure, -16F, 16F, 0F);
        BloomIntensity = Clamp(BloomIntensity, 0F, 8F, 0F);
        BloomThreshold = Clamp(BloomThreshold, 0F, 8F, 1F);
        BloomRadius = Clamp(BloomRadius, 0.1F, 64F, 4F);
        Vignette = Clamp(Vignette, 0F, 1F, 0F);
        return this;
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
