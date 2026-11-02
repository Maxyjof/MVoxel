using System.Text.Json.Serialization;

namespace MVoxel.Rendering;

/// <summary>
/// 相机后处理效果类型
/// </summary>
public enum CameraPostProcessEffectType
{
    AmbientOcclusion = 0,
    VoxelIndirectLight = 1,
    ProbeVolumeGi = 2,
    ToneMapping = 3,
    Dither = 4,
    Vignette = 5,
    DepthOfField = 6,
    Bloom = 7,
    ColorGrading = 8,
    FilmGrain = 9,
    Posterize = 10,
    SplitToning = 11,
}

/// <summary>
/// 单个相机后处理效果，可由相机组件覆盖项目默认值
/// </summary>
public sealed class CameraPostProcessEffect
{
    public CameraPostProcessEffectType Type;
    public bool Enabled = true;
    public float Intensity = 1F;
    public float Radius = 1.75F;
    public int Samples = 8;
    public float TemporalWeight = 0.9F;
    public ToneCurve ToneCurve = ToneCurve.AcesNarkowicz;
    public float ExposureOffset;
    public float Gamma = 2.2F;
    public float WhiteBalance = 6500F;
    public float Threshold = 1F;
    public float AoPower = 1.2F;
    public float Saturation = 1F;
    public float HueShift;
    public float Contrast = 1F;
    public float Brightness = 1F;
    public float ShadowR = 1F, ShadowG = 1F, ShadowB = 1F;
    public float HighlightR = 1F, HighlightG = 1F, HighlightB = 1F;
    public float ToningBalance = .5F;
    public bool OverrideDepthOfFieldParameters;
    public float DepthOfFieldAperture = .05F;
    public float DepthOfFieldFocusDistance = 10F;

    public CameraPostProcessEffect Normalize()
    {
        Intensity = float.IsFinite(Intensity) ? Math.Clamp(Intensity, 0F, 4F) : 1F;
        Radius = float.IsFinite(Radius) ? Math.Clamp(Radius, 0.05F, 128F) : 1.75F;
        Samples = Math.Clamp(Samples, 1, 64);
        TemporalWeight = float.IsFinite(TemporalWeight) ? Math.Clamp(TemporalWeight, 0F, 0.99F) : 0.9F;
        ToneCurve = Enum.IsDefined(ToneCurve) ? ToneCurve : ToneCurve.AcesNarkowicz;
        ExposureOffset = float.IsFinite(ExposureOffset) ? Math.Clamp(ExposureOffset, -16F, 16F) : 0F;
        Gamma = float.IsFinite(Gamma) ? Math.Clamp(Gamma, 1F, 3.5F) : 2.2F;
        WhiteBalance = float.IsFinite(WhiteBalance) ? Math.Clamp(WhiteBalance, 1500F, 12000F) : 6500F;
        Threshold = float.IsFinite(Threshold) ? Math.Clamp(Threshold, 0F, 8F) : 1F;
        AoPower = float.IsFinite(AoPower) ? Math.Clamp(AoPower, .2F, 4F) : 1.2F;
        Saturation = float.IsFinite(Saturation) ? Math.Clamp(Saturation, 0F, 2.5F) : 1F;
        HueShift = float.IsFinite(HueShift) ? Math.Clamp(HueShift, -180F, 180F) : 0F;
        Contrast = float.IsFinite(Contrast) ? Math.Clamp(Contrast, .2F, 2.5F) : 1F;
        Brightness = float.IsFinite(Brightness) ? Math.Clamp(Brightness, .1F, 3F) : 1F;
        ShadowR = SafeTint(ShadowR); ShadowG = SafeTint(ShadowG); ShadowB = SafeTint(ShadowB);
        HighlightR = SafeTint(HighlightR); HighlightG = SafeTint(HighlightG); HighlightB = SafeTint(HighlightB);
        ToningBalance = float.IsFinite(ToningBalance) ? Math.Clamp(ToningBalance, .05F, .95F) : .5F;
        DepthOfFieldAperture = float.IsFinite(DepthOfFieldAperture) ? Math.Clamp(DepthOfFieldAperture, 0F, .5F) : .05F;
        DepthOfFieldFocusDistance = float.IsFinite(DepthOfFieldFocusDistance) ? Math.Clamp(DepthOfFieldFocusDistance, .01F, 1_000_000F) : 10F;
        return this;
    }

    private static float SafeTint(float value) => float.IsFinite(value) ? Math.Clamp(value, 0F, 1F) : 1F;

    public CameraPostProcessEffect Clone() => new()
    {
        Type = Type, Enabled = Enabled, Intensity = Intensity, Radius = Radius,
        Samples = Samples, TemporalWeight = TemporalWeight, ToneCurve = ToneCurve,
        ExposureOffset = ExposureOffset, Gamma = Gamma, WhiteBalance = WhiteBalance,
        Threshold = Threshold, AoPower = AoPower, Saturation = Saturation, HueShift = HueShift,
        Contrast = Contrast, Brightness = Brightness,
        ShadowR = ShadowR, ShadowG = ShadowG, ShadowB = ShadowB,
        HighlightR = HighlightR, HighlightG = HighlightG, HighlightB = HighlightB, ToningBalance = ToningBalance,
        OverrideDepthOfFieldParameters = OverrideDepthOfFieldParameters,
        DepthOfFieldAperture = DepthOfFieldAperture, DepthOfFieldFocusDistance = DepthOfFieldFocusDistance,
    };
}

/// <summary>
/// 相机后处理栈。首期提供AO、体素间接光和探针GI，复杂实时增量更新留作后续升级
/// </summary>
public sealed class CameraPostProcessStack
{
    public bool OverrideProjectDefaults;
    public List<CameraPostProcessEffect> Effects = CreateDefaultEffects();

    public static CameraPostProcessStack Default => new();

    public static List<CameraPostProcessEffect> CreateDefaultEffects() =>
    [
        new() { Type = CameraPostProcessEffectType.AmbientOcclusion, Intensity = 0.7F, Radius = 1.75F, Samples = 8 },
        new() { Type = CameraPostProcessEffectType.VoxelIndirectLight, Intensity = 0.35F, Radius = 6F, Samples = 4 },
        new() { Type = CameraPostProcessEffectType.ProbeVolumeGi, Intensity = 0.5F, Radius = 8F, Samples = 1 },
        new() { Type = CameraPostProcessEffectType.Vignette, Enabled = true, Intensity = 0.12F, Radius = 0.75F },
        new() { Type = CameraPostProcessEffectType.DepthOfField, Enabled = false, Intensity = 0.5F, Radius = 2F },
    ];

    public void Normalize()
    {
        Effects ??= [];
        Effects.RemoveAll(effect => effect is null);
        foreach (var effect in Effects) effect.Normalize();
        Effects = Effects.GroupBy(effect => effect.Type).Select(group => group.Last()).ToList();
    }

    public CameraPostProcessStack Clone()
    {
        var clone = new CameraPostProcessStack { OverrideProjectDefaults = OverrideProjectDefaults };
        clone.Effects = Effects?.Where(effect => effect is not null).Select(effect => effect.Clone()).ToList() ?? [];
        return clone;
    }

    public CameraPostProcessEffect? Find(CameraPostProcessEffectType type) =>
        Effects?.FirstOrDefault(effect => effect.Type == type);
}
