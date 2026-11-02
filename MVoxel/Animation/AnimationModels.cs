using System.Numerics;
using System.Text.Json.Serialization;

namespace MVoxel.Animation;

public enum AnimationInterpolation
{
    Step,
    Linear,
    Smooth,
    Bezier
}

public enum AnimationTransitionSide
{
    Left,
    Right,
    Both
}

public sealed class AnimationCurve
{
    public AnimationInterpolation Mode { get; set; } = AnimationInterpolation.Linear;
    public float LeftTangent { get; set; } = 1F;
    public float RightTangent { get; set; } = 1F;
}

public sealed class AnimationKeyframe
{
    public int Frame { get; set; }
    public string ObjectId { get; set; } = string.Empty;
    public string ComponentType { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public float[] Values { get; set; } = [];
    public AnimationCurve LeftCurve { get; set; } = new();
    public AnimationCurve RightCurve { get; set; } = new();
    public AnimationTransitionSide TransitionSide { get; set; } = AnimationTransitionSide.Both;
}

public sealed class AnimationClip
{
    public string Name { get; set; } = "动画片段";
    public int FrameRate { get; set; } = 30;
    public int LengthFrames { get; set; } = 60;
    public List<AnimationKeyframe> Keyframes { get; set; } = [];
    public HashSet<string> VisibleParameters { get; set; } = new(StringComparer.Ordinal);

    public void Normalize()
    {
        FrameRate = Math.Clamp(FrameRate, 1, 240);
        LengthFrames = Math.Clamp(LengthFrames, 1, 1_000_000);
        Keyframes.RemoveAll(key => key is null || key.Frame < 0 || key.Frame > LengthFrames || key.Values is null);
        VisibleParameters.RemoveWhere(string.IsNullOrWhiteSpace);
    }
}

public sealed record AnimationComponentData
{
    public string ClipAsset { get; set; } = string.Empty;
    public bool Playing { get; set; }
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1F;
    public int CurrentFrame { get; set; }
    public bool Recording { get; set; }

    public AnimationComponentData Normalize()
    {
        ClipAsset = ClipAsset?.Replace('\\', '/') ?? string.Empty;
        Speed = float.IsFinite(Speed) ? Math.Clamp(Speed, -8F, 8F) : 1F;
        CurrentFrame = Math.Max(0, CurrentFrame);
        return this;
    }
}

public static class AnimationValueCodec
{
    public static float[] FromVector(Vector3 value) => [value.X, value.Y, value.Z];
    public static Vector3 ToVector(IReadOnlyList<float> values) => new(values.Count > 0 ? values[0] : 0F, values.Count > 1 ? values[1] : 0F, values.Count > 2 ? values[2] : 0F);
}
