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

    public CameraPostProcessEffect Normalize()
    {
        Intensity = float.IsFinite(Intensity) ? Math.Clamp(Intensity, 0F, 4F) : 1F;
        Radius = float.IsFinite(Radius) ? Math.Clamp(Radius, 0.05F, 128F) : 1.75F;
        Samples = Math.Clamp(Samples, 1, 64);
        TemporalWeight = float.IsFinite(TemporalWeight) ? Math.Clamp(TemporalWeight, 0F, 0.99F) : 0.9F;
        return this;
    }

    public CameraPostProcessEffect Clone() => new()
    {
        Type = Type, Enabled = Enabled, Intensity = Intensity, Radius = Radius,
        Samples = Samples, TemporalWeight = TemporalWeight,
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
