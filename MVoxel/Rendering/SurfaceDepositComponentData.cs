using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>
/// 积水与积雪投射体共用的数据。投射方向由所属对象的世界变换决定；
/// 范围、偏移和投射深度均以对象局部坐标表达。
/// 此类型先定义持久化契约，接入场景组件和渲染前不暴露为可用控件。
/// </summary>
public sealed class SurfaceDepositComponentData
{
    public bool Enabled = true;
    public Vector3 Size = new(16F, 4F, 16F);
    public Vector3 Offset;
    public float Opacity = 1F;
    public float EdgeFeather = .1F;
    public float DepthFeather = .1F;
    public float NormalFadeStart = 30F;
    public float NormalFadeEnd = 75F;
    public uint ReceiverLayers = uint.MaxValue;
    public int SortOrder;
    public int Seed;
    public float Coverage = .8F;
    public float PatternScale = .2F;
    public float PatternSoftness = .15F;
    public Vector3 Color = Vector3.One;
    public float ColorStrength = .8F;
    public float Roughness = .15F;
    public float RoughnessStrength = 1F;
    public float NormalStrength;

    public static SurfaceDepositComponentData CreatePuddle() => new()
    {
        Color = new Vector3(.28F, .34F, .38F),
        ColorStrength = .25F,
        Roughness = .06F,
        Coverage = .65F,
        NormalStrength = .2F,
    };

    public static SurfaceDepositComponentData CreateSnow() => new()
    {
        Color = new Vector3(.94F, .97F, 1F),
        Roughness = .8F,
        NormalFadeStart = 15F,
        NormalFadeEnd = 60F,
    };

    public SurfaceDepositComponentData Normalize()
    {
        Size = new Vector3(Clamp(Size.X, .01F, 100000F, 16F),
            Clamp(Size.Y, .01F, 100000F, 4F), Clamp(Size.Z, .01F, 100000F, 16F));
        Offset = new Vector3(Clamp(Offset.X, -100000F, 100000F, 0F),
            Clamp(Offset.Y, -100000F, 100000F, 0F), Clamp(Offset.Z, -100000F, 100000F, 0F));
        Opacity = Clamp(Opacity, 0F, 1F, 1F);
        EdgeFeather = Clamp(EdgeFeather, 0F, 1F, .1F);
        DepthFeather = Clamp(DepthFeather, 0F, 1F, .1F);
        NormalFadeStart = Clamp(NormalFadeStart, 0F, 180F, 30F);
        NormalFadeEnd = Clamp(NormalFadeEnd, NormalFadeStart, 180F, 75F);
        SortOrder = Math.Clamp(SortOrder, -10000, 10000);
        Seed = Math.Clamp(Seed, 0, 65535);
        Coverage = Clamp(Coverage, 0F, 1F, .8F);
        PatternScale = Clamp(PatternScale, .001F, 100F, .2F);
        PatternSoftness = Clamp(PatternSoftness, 0F, 1F, .15F);
        Color = new Vector3(Clamp(Color.X, 0F, 1F, 1F), Clamp(Color.Y, 0F, 1F, 1F), Clamp(Color.Z, 0F, 1F, 1F));
        ColorStrength = Clamp(ColorStrength, 0F, 1F, .8F);
        Roughness = Clamp(Roughness, .01F, 1F, .15F);
        RoughnessStrength = Clamp(RoughnessStrength, 0F, 1F, 1F);
        NormalStrength = Clamp(NormalStrength, 0F, 1F, 0F);
        return this;
    }

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);
}
