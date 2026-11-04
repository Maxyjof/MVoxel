using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Rendering;

namespace MVoxel.Scene;

public sealed class SceneEnvironmentJsonConverter : JsonConverter<SceneEnvironmentSettings>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    public override SceneEnvironmentSettings? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<SceneEnvironmentSettings>(ref reader, Options);
    public override void Write(Utf8JsonWriter writer, SceneEnvironmentSettings value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Options);
}

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
    public float FogHeight;
    public float FogHeightFalloff;
    public float FogAnisotropy = .6F;
    public float FogSunScatterStrength = 1F;
    public Vector3 FogSunScatterColor = new(1F, .9F, .75F);
    public float FogAlbedo = 1F;
    public float FogInscatterStrength = 1F;
    public float FogSkyFogStrength = .55F;
    public int FogVolumetricSteps;
    public float FogViewDistance;
    public float FogNoiseStrength;
    public float FogNoiseScale = .035F;
    public float FogNoiseSpeed = .6F;
    public bool ShowWater;
    public float WaterHeight;
    public Vector3 WaterColor = new(.08F, .32F, .42F);
    public float WaterWaveSize = .35F;
    public float WaterWaveLength = 16F;
    public float WaterRoughness = .12F;
    public bool WaterAnimationEnabled = true;
    public float WaterAnimationSpeed = .7F;
    public Vector2 WaterFlowDirection = new(1F, .35F);
    public float WaterDetailStrength = .35F;
    public float WaterSpecularStrength = .8F;
    public float WeatherWindStrength;
    public float WeatherWindDirection;
    public float WeatherRainIntensity;
    public float WeatherSnowIntensity;
    public float WeatherStormIntensity;
    public float WeatherHeatHaze;
    public float WeatherLightningFrequency;
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
        FogHeight = Clamp(FogHeight, -100000F, 100000F);
        FogHeightFalloff = Clamp(FogHeightFalloff, 0F, .2F);
        FogAnisotropy = Clamp(FogAnisotropy, -.95F, .95F);
        FogSunScatterStrength = Clamp(FogSunScatterStrength, 0F, 4F);
        FogSunScatterColor = Color(FogSunScatterColor, new(1F, .9F, .75F));
        FogAlbedo = Clamp(FogAlbedo, 0F, 1F);
        FogInscatterStrength = Clamp(FogInscatterStrength, 0F, 4F);
        FogSkyFogStrength = Clamp(FogSkyFogStrength, 0F, 1F);
        FogVolumetricSteps = Math.Clamp(FogVolumetricSteps, 0, 64);
        FogViewDistance = Clamp(FogViewDistance, 0F, 100000F);
        FogNoiseStrength = Clamp(FogNoiseStrength, 0F, 1F);
        FogNoiseScale = Clamp(FogNoiseScale, .001F, 1F);
        FogNoiseSpeed = Clamp(FogNoiseSpeed, 0F, 4F);
        WaterHeight = Clamp(WaterHeight, -100000F, 100000F);
        WaterWaveSize = Clamp(WaterWaveSize, 0F, 4F);
        WaterWaveLength = Clamp(WaterWaveLength, .1F, 1024F);
        WaterRoughness = Clamp(WaterRoughness, .01F, 1F);
        WaterAnimationSpeed = Clamp(WaterAnimationSpeed, -8F, 8F);
        WaterDetailStrength = Clamp(WaterDetailStrength, 0F, 2F);
        WaterSpecularStrength = Clamp(WaterSpecularStrength, 0F, 4F);
        WeatherWindStrength = Clamp(WeatherWindStrength, 0F, 4F);
        WeatherWindDirection = Clamp(WeatherWindDirection, 0F, 360F);
        WeatherRainIntensity = Clamp(WeatherRainIntensity, 0F, 1F);
        WeatherSnowIntensity = Clamp(WeatherSnowIntensity, 0F, 1F);
        WeatherStormIntensity = Clamp(WeatherStormIntensity, 0F, 1F);
        WeatherHeatHaze = Clamp(WeatherHeatHaze, 0F, 1F);
        WeatherLightningFrequency = Clamp(WeatherLightningFrequency, 0F, 4F);
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
        settings.FogHeight = FogHeight;
        settings.FogHeightFalloff = FogHeightFalloff;
        settings.FogAnisotropy = FogAnisotropy;
        settings.FogSunScatterStrength = FogSunScatterStrength;
        settings.FogSunScatterColor = FogSunScatterColor;
        settings.FogAlbedo = FogAlbedo;
        settings.FogInscatterStrength = FogInscatterStrength;
        settings.FogSkyFogStrength = FogSkyFogStrength;
        settings.FogVolumetricSteps = FogVolumetricSteps;
        settings.FogViewDistance = FogViewDistance;
        settings.FogNoiseStrength = FogNoiseStrength;
        settings.FogNoiseScale = FogNoiseScale;
        settings.FogNoiseSpeed = FogNoiseSpeed;
        settings.ShowWater = ShowWater;
        settings.WaterHeight = WaterHeight;
        settings.WaterColor = WaterColor;
        settings.WaterWaveSize = WaterWaveSize;
        settings.WaterWaveLength = WaterWaveLength;
        settings.WaterRoughness = WaterRoughness;
        settings.WaterAnimationEnabled = WaterAnimationEnabled;
        settings.WaterAnimationSpeed = WaterAnimationSpeed;
        settings.WaterFlowDirection = WaterFlowDirection;
        settings.WaterDetailStrength = WaterDetailStrength;
        settings.WaterSpecularStrength = WaterSpecularStrength;
        settings.WeatherWindStrength = WeatherWindStrength;
        settings.WeatherWindDirection = WeatherWindDirection;
        settings.WeatherRainIntensity = WeatherRainIntensity;
        settings.WeatherSnowIntensity = WeatherSnowIntensity;
        settings.WeatherStormIntensity = WeatherStormIntensity;
        settings.WeatherHeatHaze = WeatherHeatHaze;
        settings.WeatherLightningFrequency = WeatherLightningFrequency;
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
        FogHeight = settings.FogHeight;
        FogHeightFalloff = settings.FogHeightFalloff;
        FogAnisotropy = settings.FogAnisotropy;
        FogSunScatterStrength = settings.FogSunScatterStrength;
        FogSunScatterColor = settings.FogSunScatterColor;
        FogAlbedo = settings.FogAlbedo;
        FogInscatterStrength = settings.FogInscatterStrength;
        FogSkyFogStrength = settings.FogSkyFogStrength;
        FogVolumetricSteps = settings.FogVolumetricSteps;
        FogViewDistance = settings.FogViewDistance;
        FogNoiseStrength = settings.FogNoiseStrength;
        FogNoiseScale = settings.FogNoiseScale;
        FogNoiseSpeed = settings.FogNoiseSpeed;
        ShowWater = settings.ShowWater;
        WaterHeight = settings.WaterHeight;
        WaterColor = settings.WaterColor;
        WaterWaveSize = settings.WaterWaveSize;
        WaterWaveLength = settings.WaterWaveLength;
        WaterRoughness = settings.WaterRoughness;
        WaterAnimationEnabled = settings.WaterAnimationEnabled;
        WaterAnimationSpeed = settings.WaterAnimationSpeed;
        WaterFlowDirection = settings.WaterFlowDirection;
        WaterDetailStrength = settings.WaterDetailStrength;
        WaterSpecularStrength = settings.WaterSpecularStrength;
        WeatherWindStrength = settings.WeatherWindStrength;
        WeatherWindDirection = settings.WeatherWindDirection;
        WeatherRainIntensity = settings.WeatherRainIntensity;
        WeatherSnowIntensity = settings.WeatherSnowIntensity;
        WeatherStormIntensity = settings.WeatherStormIntensity;
        WeatherHeatHaze = settings.WeatherHeatHaze;
        WeatherLightningFrequency = settings.WeatherLightningFrequency;
        NearFade = settings.NearFade;
        Normalize();
    }

    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
    private static Vector3 Color(Vector3 value, Vector3 fallback) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, 0F, 4F) : fallback.X,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, 0F, 4F) : fallback.Y,
        float.IsFinite(value.Z) ? Math.Clamp(value.Z, 0F, 4F) : fallback.Z);
}
