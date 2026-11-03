using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>对象在环境风场中的最终受力采样</summary>
public readonly record struct WindForceSample(Vector3 Force, float Magnitude, bool HasInfluence)
{
    public static WindForceSample None => new(Vector3.Zero, 0F, false);
}

/// <summary>
/// 环境风场求解器，负责空间衰减和接收器参数融合，不直接修改体素数据
/// </summary>
public static class WindInteractionSolver
{
    /// <summary>按风场对象位置和接收对象位置求解风力</summary>
    public static WindForceSample Evaluate(
        WindFieldComponentData field, Vector3 fieldPosition,
        WindReceiverComponentData receiver, Vector3 receiverPosition,
        float timeSeconds = 0F)
    {
        field.Normalize();
        receiver.Normalize();
        if (!field.Enabled || !receiver.Enabled || !receiver.ReceiveLocalWind) return WindForceSample.None;

        var offset = receiverPosition - fieldPosition;
        var distance = offset.Length();
        if (!float.IsFinite(distance) || distance > field.Range) return WindForceSample.None;

        var normalizedDistance = field.Range <= .0001F ? 1F : distance / field.Range;
        var attenuation = MathF.Pow(Math.Clamp(1F - normalizedDistance, 0F, 1F), field.Falloff);
        var turbulence = field.Turbulence
            ? 1F + MathF.Sin(timeSeconds * 2.1F + receiverPosition.X * .17F + receiverPosition.Z * .11F) *
              Math.Clamp(field.TurbulenceStrength, 0F, 1F) * .25F
            : 1F;
        var magnitude = Math.Max(0F, field.Strength) * attenuation * turbulence * receiver.ForceScale;
        var force = field.Direction * magnitude;
        return new WindForceSample(force, force.Length(), magnitude > .0001F);
    }
}
