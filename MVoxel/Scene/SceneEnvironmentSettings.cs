using System.Numerics;
using MVoxel.Rendering;

namespace MVoxel.Scene;

/// <summary>
/// 场景唯一的环境配置，不包含相机、光照和后处理参数
/// </summary>
public sealed class SceneEnvironmentSettings
{
    public bool ShowGround = true;
    public Vector3 SceneBackgroundColor = new(.025F, .035F, .05F);
    public Vector3 GroundColor = Vector3.One;
    public float GroundFade;
    public Vector3 SkyTop = new(.08F, .18F, .35F);
    public Vector3 SkyHorizon = new(.55F, .72F, .95F);
    public float SkyAmbient = .35F;
    public float AmbientTint = .2F;
    public Vector3 FogColor = new(.12F, .16F, .24F);
    public float FogDensity;
    public float FogStart;
    public float FogSkyBlend = .55F;
    public float FogMaxOpacity = .92F;
    public bool ShowWater;
    public float WaterHeight;
    public Vector3 WaterColor = new(.08F, .32F, .42F);
    public float WaterWaveSize = .35F;
    public float WaterWaveLength = 16F;
    public float WaterRoughness = .12F;
    public float NearFade;

    public void Normalize()
    {
        GroundFade = Clamp(GroundFade, 0F, 4096F);
        SkyAmbient = Clamp(SkyAmbient, 0F, 2F);
        AmbientTint = Clamp(AmbientTint, 0F, 1F);
        FogDensity = Clamp(FogDensity, 0F, .05F);
        FogStart = Clamp(FogStart, 0F, 100000F);
        FogSkyBlend = Clamp(FogSkyBlend, 0F, 1F);
        FogMaxOpacity = Clamp(FogMaxOpacity, 0F, 1F);
        WaterHeight = Clamp(WaterHeight, -100000F, 100000F);
        WaterWaveSize = Clamp(WaterWaveSize, 0F, 4F);
        WaterWaveLength = Clamp(WaterWaveLength, .1F, 1024F);
        WaterRoughness = Clamp(WaterRoughness, .01F, 1F);
        NearFade = Clamp(NearFade, 0F, 512F);
        SceneBackgroundColor = Color(SceneBackgroundColor, new(.025F, .035F, .05F));
        GroundColor = Color(GroundColor, Vector3.One);
        SkyTop = Color(SkyTop, new(.08F, .18F, .35F));
        SkyHorizon = Color(SkyHorizon, new(.55F, .72F, .95F));
        FogColor = Color(FogColor, new(.12F, .16F, .24F));
        WaterColor = Color(WaterColor, new(.08F, .32F, .42F));
    }

    public void ApplyTo(RenderSettings settings)
    {
        Normalize();
        settings.ShowGround = ShowGround;
        settings.SceneBackgroundColor = SceneBackgroundColor;
        settings.GroundColor = GroundColor;
        settings.GroundFade = GroundFade;
        settings.SkyTop = SkyTop;
        settings.SkyHorizon = SkyHorizon;
        settings.SkyAmbient = SkyAmbient;
        settings.AmbientTint = AmbientTint;
        settings.FogColor = FogColor;
        settings.FogDensity = FogDensity;
        settings.FogStart = FogStart;
        settings.FogSkyBlend = FogSkyBlend;
        settings.FogMaxOpacity = FogMaxOpacity;
        settings.ShowWater = ShowWater;
        settings.WaterHeight = WaterHeight;
        settings.WaterColor = WaterColor;
        settings.WaterWaveSize = WaterWaveSize;
        settings.WaterWaveLength = WaterWaveLength;
        settings.WaterRoughness = WaterRoughness;
        settings.NearFade = NearFade;
    }

    public void CaptureFrom(RenderSettings settings)
    {
        settings.ClampAll();
        ShowGround = settings.ShowGround;
        SceneBackgroundColor = settings.SceneBackgroundColor;
        GroundColor = settings.GroundColor;
        GroundFade = settings.GroundFade;
        SkyTop = settings.SkyTop;
        SkyHorizon = settings.SkyHorizon;
        SkyAmbient = settings.SkyAmbient;
        AmbientTint = settings.AmbientTint;
        FogColor = settings.FogColor;
        FogDensity = settings.FogDensity;
        FogStart = settings.FogStart;
        FogSkyBlend = settings.FogSkyBlend;
        FogMaxOpacity = settings.FogMaxOpacity;
        ShowWater = settings.ShowWater;
        WaterHeight = settings.WaterHeight;
        WaterColor = settings.WaterColor;
        WaterWaveSize = settings.WaterWaveSize;
        WaterWaveLength = settings.WaterWaveLength;
        WaterRoughness = settings.WaterRoughness;
        NearFade = settings.NearFade;
        Normalize();
    }

    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
    private static Vector3 Color(Vector3 value, Vector3 fallback) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, 0F, 4F) : fallback.X,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, 0F, 4F) : fallback.Y,
        float.IsFinite(value.Z) ? Math.Clamp(value.Z, 0F, 4F) : fallback.Z);
}
