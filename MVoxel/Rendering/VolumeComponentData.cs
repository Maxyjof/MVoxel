using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>场景全局Volume组件，集中提供环境、雾和后处理覆盖参数</summary>
public sealed class VolumeComponentData
{
    public bool IsGlobal = true;
    public bool Enabled = true;
    public float Weight = 1F;
    public Vector3 EnvironmentColor = new(.12F, .16F, .24F);
    public float FogDensity;
    public float FogHeight = 16F;
    public float FogAnisotropy;
    public float BloomIntensity;
    public float BloomThreshold = 1F;
    public float Exposure;

    public VolumeComponentData Normalize()
    {
        Weight = float.IsFinite(Weight) ? Math.Clamp(Weight, 0F, 1F) : 1F;
        EnvironmentColor = new Vector3(
            float.IsFinite(EnvironmentColor.X) ? Math.Clamp(EnvironmentColor.X, 0F, 8F) : .12F,
            float.IsFinite(EnvironmentColor.Y) ? Math.Clamp(EnvironmentColor.Y, 0F, 8F) : .16F,
            float.IsFinite(EnvironmentColor.Z) ? Math.Clamp(EnvironmentColor.Z, 0F, 8F) : .24F);
        FogDensity = float.IsFinite(FogDensity) ? Math.Clamp(FogDensity, 0F, 1F) : 0F;
        FogHeight = float.IsFinite(FogHeight) ? Math.Clamp(FogHeight, .01F, 100000F) : 16F;
        FogAnisotropy = float.IsFinite(FogAnisotropy) ? Math.Clamp(FogAnisotropy, -0.9F, .9F) : 0F;
        BloomIntensity = float.IsFinite(BloomIntensity) ? Math.Clamp(BloomIntensity, 0F, 8F) : 0F;
        BloomThreshold = float.IsFinite(BloomThreshold) ? Math.Clamp(BloomThreshold, 0F, 64F) : 1F;
        Exposure = float.IsFinite(Exposure) ? Math.Clamp(Exposure, -16F, 16F) : 0F;
        return this;
    }
}
