using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MVoxel.Rendering;

public enum ToneCurve
{
    Linear = 0,
    Reinhard = 1,
    AcesNarkowicz = 2,
    Filmic = 3,
}

/// <summary>
/// Every tunable of both viewports in one persisted structure. The GPU
/// pipeline uploads it as a flat float block, the ImGui panel binds to the
/// public fields directly (ref SliderFloat), and defaults stay MagicaVoxel-ish
/// while the path tracer exposes the physical controls. ClampAll() runs on
/// load and on every parameter upload, so a corrupt settings file can never
/// push the shaders out of range.
/// </summary>
public sealed class RenderSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------- Scene view (MagicaVoxel-style ray cast) ----------
    public bool ShowGround = true;
    public Vector3 GroundColor = new(0.42F, 0.46F, 0.52F);
    public float GroundFade = 90F;          // distance where ground melts into sky

    public bool SunEnabled = true;
    public float SunAzimuthDegrees = 135F;  // MagicaVoxel “light angle”
    public float SunElevationDegrees = 55F;
    public float SunIntensity = 1.05F;
    public Vector3 SunColor = new(1.00F, 0.98F, 0.92F);
    public float ShadowSoftness = 0.06F;    // sun cone angle, 0 = hard shadow
    public int ShadowSamples = 3;

    public bool AmbientOcclusionEnabled = true;
    public float AoStrength = 0.85F;        // MV corner-AO amount
    public float AoPower = 1.2F;            // contrast of the AO curve

    public Vector3 SkyTop = new(0.24F, 0.45F, 0.78F);
    public Vector3 SkyHorizon = new(0.62F, 0.72F, 0.85F);
    public float SkyAmbient = 0.42F;        // hemisphere fill strength
    public float AmbientTint = 0.15F;       // flat warm fill like MV “环境光”

    // MagicaVoxel palette shading family.
    public bool PaletteShading = true;
    public float Saturation = 1.0F;         // MV “color saturation”
    public float HueShiftDegrees;
    public float ColorContrast = 1.0F;
    public float Brightness = 1.0F;
    public bool HueCycle;                   // MV hue-ring animation
    public float HueCycleSpeed = 20F;       // degrees per second

    public Vector3 FogColor = new(0.62F, 0.72F, 0.85F);
    public float FogDensity;                // exponential distance fog
    public float NearFade;                  // soft fade-in distance (0 = off)

    // ---------- Shared post ----------
    public ToneCurve Tone = ToneCurve.AcesNarkowicz;
    public float Exposure;                  // EV offset
    public float Gamma = 2.2F;
    public float WhiteBalance = 6500F;      // kelvin
    public float Vignette;
    public bool Dither = true;

    // ---------- Render view (progressive path tracer) ----------
    public bool PathTracingEnabled = true;
    public int PathSamplesPerFrame = 1;
    public int PathMaxAccumulatedSamples = 1024;
    public float ResolutionScale = 1.0F;    // internal resolution multiplier
    public int MaxBounces = 6;
    public float RussianRouletteThreshold = 0.004F; // ≈ 1/255, below LSB
    public float RadianceClamp = 40F;       // fireflies guard
    public float PathRoughness = 0.22F;     // GGX roughness for plastics/metals
    public float PathMetalness;             // 0 dielectric, 1 metal
    public float PathSpecularF0 = 0.04F;    // Schlick base reflectance
    public bool DirectLightingMIS = true;   // sun/sky/emissive next-event sampling
    public bool EmissiveVoxels = true;
    public float EmissiveIntensity = 12F;
    public float TransmissiveAbsorption = 1.0F; // Beer-Lambert density for alpha<1 voxels
    public bool DepthOfField;
    public float ApertureRadius = 0.08F;
    public float FocusDistance = 18F;
    public float CameraFovDegrees = 50F;    // render view only; scene view focal matches the grid

    public Vector3 SunDirection
    {
        get
        {
            var azimuth = MathF.PI * Math.Clamp(SunAzimuthDegrees, 0F, 360F) / 180F;
            var elevation = MathF.PI * Math.Clamp(SunElevationDegrees, -5F, 90F) / 180F;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(elevation) * MathF.Sin(azimuth),
                MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Cos(azimuth)));
        }
    }

    public void ClampAll()
    {
        GroundColor = ClampChannel(GroundColor);
        SunColor = ClampChannel(SunColor);
        SkyTop = ClampChannel(SkyTop);
        SkyHorizon = ClampChannel(SkyHorizon);
        FogColor = ClampChannel(FogColor);
        SunAzimuthDegrees = Wrap(SunAzimuthDegrees, 360F);
        SunElevationDegrees = Math.Clamp(SunElevationDegrees, 2F, 90F);
        SunIntensity = Math.Clamp(SunIntensity, 0F, 8F);
        ShadowSoftness = Math.Clamp(ShadowSoftness, 0F, 0.5F);
        ShadowSamples = Math.Clamp(ShadowSamples, 1, 8);
        AoStrength = Math.Clamp(AoStrength, 0F, 1F);
        AoPower = Math.Clamp(AoPower, 0.2F, 4F);
        SkyAmbient = Math.Clamp(SkyAmbient, 0F, 2F);
        AmbientTint = Math.Clamp(AmbientTint, 0F, 1F);
        Saturation = Math.Clamp(Saturation, 0F, 2.5F);
        HueShiftDegrees = Wrap(HueShiftDegrees, 360F);
        ColorContrast = Math.Clamp(ColorContrast, 0.2F, 2.5F);
        Brightness = Math.Clamp(Brightness, 0.1F, 3F);
        HueCycleSpeed = Math.Clamp(HueCycleSpeed, 0F, 180F);
        FogDensity = Math.Clamp(FogDensity, 0F, 0.05F);
        NearFade = Math.Clamp(NearFade, 0F, 512F);
        GroundFade = Math.Clamp(GroundFade, 4F, 4096F);
        Exposure = Math.Clamp(Exposure, -6F, 6F);
        Gamma = Math.Clamp(Gamma, 1F, 3.5F);
        WhiteBalance = Math.Clamp(WhiteBalance, 1500F, 12000F);
        Vignette = Math.Clamp(Vignette, 0F, 1F);
        PathSamplesPerFrame = Math.Clamp(PathSamplesPerFrame, 1, 8);
        PathMaxAccumulatedSamples = Math.Clamp(PathMaxAccumulatedSamples, 16, 16384);
        ResolutionScale = Math.Clamp(ResolutionScale, 0.25F, 1F);
        MaxBounces = Math.Clamp(MaxBounces, 1, 12);
        RussianRouletteThreshold = Math.Clamp(RussianRouletteThreshold, 0.0005F, 0.1F);
        RadianceClamp = Math.Clamp(RadianceClamp, 1F, 1000F);
        PathRoughness = Math.Clamp(PathRoughness, 0.01F, 1F);
        PathMetalness = Math.Clamp(PathMetalness, 0F, 1F);
        PathSpecularF0 = Math.Clamp(PathSpecularF0, 0F, 0.6F);
        EmissiveIntensity = Math.Clamp(EmissiveIntensity, 0F, 64F);
        TransmissiveAbsorption = Math.Clamp(TransmissiveAbsorption, 0F, 8F);
        ApertureRadius = Math.Clamp(ApertureRadius, 0F, 1F);
        FocusDistance = Math.Clamp(FocusDistance, 1F, 512F);
        CameraFovDegrees = Math.Clamp(CameraFovDegrees, 20F, 120F);
    }

    private static Vector3 ClampChannel(Vector3 value) => new(
        Math.Clamp(value.X, 0F, 4F), Math.Clamp(value.Y, 0F, 4F), Math.Clamp(value.Z, 0F, 4F));

    private static float Wrap(float value, float period)
    {
        if (!float.IsFinite(value)) return 0F;
        value %= period;
        return value < 0 ? value + period : value;
    }

    public void SaveTo(string path) => MVoxel.Core.AtomicFile.WriteText(path, ToJson());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RenderSettings LoadFrom(string path)
    {
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file must never block the editor; fall back to defaults.
            return new RenderSettings();
        }
    }

    public static RenderSettings FromJson(string json)
    {
        var loaded = JsonSerializer.Deserialize<RenderSettings>(json, JsonOptions) ?? new RenderSettings();
        loaded.SanitizeNaN();
        loaded.ClampAll();
        return loaded;
    }

    private void SanitizeNaN()
    {
        SunAzimuthDegrees = Safe(SunAzimuthDegrees, 135F);
        SunElevationDegrees = Safe(SunElevationDegrees, 55F);
        SunIntensity = Safe(SunIntensity, 1.35F);
        ShadowSoftness = Safe(ShadowSoftness, 0.06F);
        AoStrength = Safe(AoStrength, 0.85F);
        AoPower = Safe(AoPower, 1.2F);
        SkyAmbient = Safe(SkyAmbient, 0.52F);
        AmbientTint = Safe(AmbientTint, 0.15F);
        Saturation = Safe(Saturation, 1F);
        HueShiftDegrees = Safe(HueShiftDegrees, 0F);
        ColorContrast = Safe(ColorContrast, 1F);
        Brightness = Safe(Brightness, 1F);
        HueCycleSpeed = Safe(HueCycleSpeed, 20F);
        FogDensity = Safe(FogDensity, 0F);
        NearFade = Safe(NearFade, 0F);
        GroundFade = Safe(GroundFade, 90F);
        Exposure = Safe(Exposure, 0F);
        Gamma = Safe(Gamma, 2.2F);
        WhiteBalance = Safe(WhiteBalance, 6500F);
        Vignette = Safe(Vignette, 0F);
        ResolutionScale = Safe(ResolutionScale, 1F);
        RussianRouletteThreshold = Safe(RussianRouletteThreshold, 0.004F);
        RadianceClamp = Safe(RadianceClamp, 40F);
        PathRoughness = Safe(PathRoughness, 0.22F);
        PathMetalness = Safe(PathMetalness, 0F);
        PathSpecularF0 = Safe(PathSpecularF0, 0.04F);
        EmissiveIntensity = Safe(EmissiveIntensity, 12F);
        TransmissiveAbsorption = Safe(TransmissiveAbsorption, 1F);
        ApertureRadius = Safe(ApertureRadius, 0.08F);
        FocusDistance = Safe(FocusDistance, 18F);
        CameraFovDegrees = Safe(CameraFovDegrees, 50F);
        GroundColor = SafeChannel(GroundColor, new Vector3(0.42F, 0.46F, 0.52F));
        SunColor = SafeChannel(SunColor, Vector3.One);
        SkyTop = SafeChannel(SkyTop, new Vector3(0.24F, 0.45F, 0.78F));
        SkyHorizon = SafeChannel(SkyHorizon, new Vector3(0.62F, 0.72F, 0.85F));
        FogColor = SafeChannel(FogColor, new Vector3(0.62F, 0.72F, 0.85F));

        static float Safe(float value, float fallback) => float.IsFinite(value) ? value : fallback;

        static Vector3 SafeChannel(Vector3 value, Vector3 fallback) => new(
            Safe(value.X, fallback.X), Safe(value.Y, fallback.Y), Safe(value.Z, fallback.Z));
    }
}
