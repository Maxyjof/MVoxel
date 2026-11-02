namespace MVoxel.Rendering;

/// <summary>
/// 自动探针的低频烘焙结果。首期保存规则网格统计，后续可扩展球谐和分层缓存
/// </summary>
public sealed class ProbeVolumeBakeData
{
    public int Version = 1;
    public int CountX = 1;
    public int CountY = 1;
    public int CountZ = 1;
    public List<ProbeSample> Samples = [];
    public void Normalize()
    {
        CountX = Math.Clamp(CountX, 1, 128);
        CountY = Math.Clamp(CountY, 1, 128);
        CountZ = Math.Clamp(CountZ, 1, 128);
        Samples = Samples?.Where(sample => sample is not null &&
            float.IsFinite(sample.X) && float.IsFinite(sample.Y) && float.IsFinite(sample.Z) &&
            float.IsFinite(sample.Occlusion)).ToList() ?? [];
        foreach (var sample in Samples)
        {
            sample.X = Math.Clamp(sample.X, 0F, 1F);
            sample.Y = Math.Clamp(sample.Y, 0F, 1F);
            sample.Z = Math.Clamp(sample.Z, 0F, 1F);
            sample.Occlusion = Math.Clamp(sample.Occlusion, 0F, 1F);
            sample.R = float.IsFinite(sample.R) ? Math.Clamp(sample.R, 0F, 1F) : 0F;
            sample.G = float.IsFinite(sample.G) ? Math.Clamp(sample.G, 0F, 1F) : 0F;
            sample.B = float.IsFinite(sample.B) ? Math.Clamp(sample.B, 0F, 1F) : 0F;
        }
    }
}

public sealed class ProbeSample
{
    public float X;
    public float Y;
    public float Z;
    public float R;
    public float G;
    public float B;
    public float Occlusion = 1F;
}
