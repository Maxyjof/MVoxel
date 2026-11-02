namespace MVoxel.Voxels;

/// <summary>
/// VOX转换损失摘要，超过材质容量时必须重新选择策略，不能确认后强行合并材质
/// </summary>
public sealed record VoxExportLossSummary(int SourceCombinations, int PaletteEntries, int MaterialCount,
    long ApproximatedVoxelCount, long FlagsNotRepresentableVoxelCount, int MissingMaterialDefinitions,
    IReadOnlyList<string> MaterialWarnings)
{
    public bool RequiresMaterialDecision => MaterialCount > 255;
    public bool HasLoss => ApproximatedVoxelCount > 0 || FlagsNotRepresentableVoxelCount > 0 ||
        MissingMaterialDefinitions > 0 || MaterialWarnings.Count > 0;
}

/// <summary>
/// 分析时捕获的独立快照，原世界后续编辑不会改变待确认内容
/// </summary>
public sealed class VoxExportPlan
{
    private readonly Action<string> _write;

    internal VoxExportPlan(VoxExportLossSummary summary, Action<string> write)
    {
        LossSummary = summary;
        _write = write;
    }

    public VoxExportLossSummary LossSummary { get; }

    /// <summary>
    /// 只在确认损失后落盘，材质种类超限时始终拒绝
    /// </summary>
    public void Write(string path, bool confirmLoss = false)
    {
        if (LossSummary.RequiresMaterialDecision)
            throw new InvalidOperationException("材质种类超过VOX的255项容量，需要进一步确认材质简化策略");
        if (LossSummary.HasLoss && !confirmLoss)
            throw new InvalidOperationException("VOX导出存在损失，请先查看LossSummary并明确确认");
        _write(path);
    }
}
