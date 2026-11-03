using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>
/// 局部风场组件参数，描述对象周围的空间风力区域
/// </summary>
public sealed class WindFieldComponentData
{
    public bool Enabled = true;
    public Vector3 Direction = new(1F, 0F, 0F);
    public float Strength = 1F;
    public float Range = 16F;
    public float Falloff = 1F;
    public bool Turbulence;
    public float TurbulenceStrength;

    public WindFieldComponentData Normalize()
    {
        Direction = NormalizeDirection(Direction);
        Strength = FiniteClamp(Strength, 0F, 1000F, 1F);
        Range = FiniteClamp(Range, .01F, 1_000_000F, 16F);
        Falloff = FiniteClamp(Falloff, 0F, 8F, 1F);
        TurbulenceStrength = FiniteClamp(TurbulenceStrength, 0F, 1000F, 0F);
        return this;
    }

    private static Vector3 NormalizeDirection(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || value.LengthSquared() < .000001F)
            return Vector3.UnitX;
        return Vector3.Normalize(value);
    }

    private static float FiniteClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 风力接收器组件参数，声明体素模型是否接受局部与全局风场
/// </summary>
public sealed class WindReceiverComponentData
{
    public bool Enabled = true;
    public bool ReceiveGlobalWind = true;
    public bool ReceiveLocalWind = true;
    public bool IncludeChildren;
    public float ForceScale = 1F;
    public float Drag = .2F;
    public float Flexibility = .5F;

    public WindReceiverComponentData Normalize()
    {
        ForceScale = FiniteClamp(ForceScale, 0F, 1000F, 1F);
        Drag = FiniteClamp(Drag, 0F, 1F, .2F);
        Flexibility = FiniteClamp(Flexibility, 0F, 1F, .5F);
        return this;
    }

    private static float FiniteClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
