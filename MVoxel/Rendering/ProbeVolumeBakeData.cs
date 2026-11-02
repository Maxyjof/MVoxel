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
