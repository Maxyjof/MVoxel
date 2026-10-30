namespace MVoxel.Rendering;

/// <summary>
/// 用户主动应用的渲染窗口质量方案
/// </summary>
public enum RenderQualityPreset { Low, Medium, High, Ultra }

/// <summary>
/// 渲染质量方案只修改性能相关参数，保留项目的颜色和光源设置
/// </summary>
public static class RenderQualityPresets
{
    /// <summary>
    /// 根据方案涉及的参数识别当前配置，手动更改后返回空值
    /// </summary>
    /// <param name="settings">当前配置</param>
    /// <returns>匹配的方案或空值</returns>
    public static RenderQualityPreset? Identify(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var quality in Enum.GetValues<RenderQualityPreset>())
        {
            var expected = settings.CloneForExport();
            Apply(expected, quality);
            if (settings.Backend == expected.Backend && settings.UseHardwareRayTracing == expected.UseHardwareRayTracing &&
                settings.ResolutionScale == expected.ResolutionScale && settings.AutoPerformanceScaling == expected.AutoPerformanceScaling &&
                settings.PathTracingEnabled == expected.PathTracingEnabled && settings.PathSamplesPerFrame == expected.PathSamplesPerFrame &&
                settings.PathMaxAccumulatedSamples == expected.PathMaxAccumulatedSamples && settings.MaxBounces == expected.MaxBounces &&
                settings.ShadowSamples == expected.ShadowSamples && settings.AmbientOcclusionEnabled == expected.AmbientOcclusionEnabled &&
                settings.AoSamples == expected.AoSamples && settings.BloomEnabled == expected.BloomEnabled &&
                settings.PathDenoising == expected.PathDenoising && settings.DirectLightingMIS == expected.DirectLightingMIS &&
                settings.RenderLod.Enabled == expected.RenderLod.Enabled && settings.RenderLod.LevelCount == expected.RenderLod.LevelCount &&
                settings.RenderLod.Distances.SequenceEqual(expected.RenderLod.Distances)) return quality;
        }
        return null;
    }

    /// <summary>
    /// 将质量方案应用到渲染窗口配置
    /// </summary>
    /// <param name="settings">目标配置</param>
    /// <param name="quality">用户选择的质量</param>
    public static void Apply(RenderSettings settings, RenderQualityPreset quality)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        settings.Backend = RenderBackend.Gpu;
        settings.UseHardwareRayTracing = false;
        settings.ResolutionScale = 1F;
        settings.AutoPerformanceScaling = quality == RenderQualityPreset.Low;
        settings.PathTracingEnabled = quality is RenderQualityPreset.High or RenderQualityPreset.Ultra;
        settings.PathSamplesPerFrame = quality == RenderQualityPreset.Ultra ? 2 : 1;
        settings.PathMaxAccumulatedSamples = quality switch { RenderQualityPreset.High => 256, RenderQualityPreset.Ultra => 1024, _ => 64 };
        settings.MaxBounces = quality == RenderQualityPreset.Ultra ? 6 : quality == RenderQualityPreset.High ? 3 : 1;
        settings.ShadowSamples = quality switch { RenderQualityPreset.Low => 1, RenderQualityPreset.Medium => 2, RenderQualityPreset.High => 4, _ => 6 };
        settings.AmbientOcclusionEnabled = quality != RenderQualityPreset.Low;
        settings.AoSamples = quality switch { RenderQualityPreset.Low => 4, RenderQualityPreset.Medium => 4, RenderQualityPreset.High => 8, _ => 16 };
        settings.BloomEnabled = quality != RenderQualityPreset.Low;
        settings.PathDenoising = quality is RenderQualityPreset.High or RenderQualityPreset.Ultra;
        settings.DirectLightingMIS = true;
        settings.RenderLod = quality switch
        {
            RenderQualityPreset.Low => new VoxelLodSettings { Enabled = true, LevelCount = 5, Distances = [64F, 128F, 256F, 512F] },
            RenderQualityPreset.Medium => new VoxelLodSettings { Enabled = true, LevelCount = 3, Distances = [256F, 512F] },
            _ => new VoxelLodSettings { Enabled = false },
        };
        settings.ClampAll();
    }
}
