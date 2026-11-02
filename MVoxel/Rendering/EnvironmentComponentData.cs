using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>
/// 场景环境组件，集中提供背景、雾和曝光参数
/// </summary>
public sealed class EnvironmentComponentData
{
    public bool Enabled = true;
    public bool IsGlobal = true;
    public float Weight = 1F;
    public Vector3 BackgroundColor = new(.025F, .035F, .05F);
    public Vector3 FogColor = new(.12F, .16F, .24F);
    public float FogDensity;
    public float FogStart;
    public float FogSkyBlend = .55F;
    public float FogMaxOpacity = .92F;
    public float Exposure;

    public EnvironmentComponentData Normalize()
    {
        Weight = float.IsFinite(Weight) ? Math.Clamp(Weight, 0F, 1F) : 1F;
        BackgroundColor = ClampColor(BackgroundColor, new(.025F, .035F, .05F));
        FogColor = ClampColor(FogColor, new(.12F, .16F, .24F));
        FogDensity = float.IsFinite(FogDensity) ? Math.Clamp(FogDensity, 0F, .05F) : 0F;
        FogStart = float.IsFinite(FogStart) ? Math.Clamp(FogStart, 0F, 100000F) : 0F;
        FogSkyBlend = float.IsFinite(FogSkyBlend) ? Math.Clamp(FogSkyBlend, 0F, 1F) : .55F;
        FogMaxOpacity = float.IsFinite(FogMaxOpacity) ? Math.Clamp(FogMaxOpacity, 0F, 1F) : .92F;
        Exposure = float.IsFinite(Exposure) ? Math.Clamp(Exposure, -16F, 16F) : 0F;
        return this;
    }

    private static Vector3 ClampColor(Vector3 value, Vector3 fallback) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, 0F, 8F) : fallback.X,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, 0F, 8F) : fallback.Y,
        float.IsFinite(value.Z) ? Math.Clamp(value.Z, 0F, 8F) : fallback.Z);
}
