using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MVoxel.Editor.Rendering.Vulkan;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private bool _showVoxelMeasurements;
    private bool _showPerformanceAnalyzer;
    private readonly float[] _frameIntervals = new float[240];
    private readonly float[] _renderCpuTimes = new float[240];
    private int _analysisFrameIndex;
    private int _analysisFrameCount;
    private double _analysisRefreshSeconds;
    private long _analysisWorkingSet;
    private long _analysisPrivateBytes;
    private long _analysisManagedBytes;
    private long _analysisAllocatedBytes;
    private double _analysisAllocatedPerSecond;
    private int _analysisGen0;
    private int _analysisGen1;
    private int _analysisGen2;
    private double _measurementRefreshSeconds;
    private int _measuredVoxels;
    private int _measuredChunks;
    private object? _measuredWorld;

    private void RecordPerformanceFrame(double intervalSeconds, long start)
    {
        _frameIntervals[_analysisFrameIndex] = (float)(Math.Max(intervalSeconds, 0) * 1000D);
        _renderCpuTimes[_analysisFrameIndex] = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _analysisFrameIndex = (_analysisFrameIndex + 1) % _frameIntervals.Length;
        _analysisFrameCount = Math.Min(_analysisFrameCount + 1, _frameIntervals.Length);
        _analysisRefreshSeconds += Math.Max(intervalSeconds, 0);
        if (_analysisRefreshSeconds < 1D) return;

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        _analysisWorkingSet = process.WorkingSet64;
        _analysisPrivateBytes = process.PrivateMemorySize64;
        _analysisManagedBytes = GC.GetTotalMemory(false);
        var allocated = GC.GetTotalAllocatedBytes(false);
        _analysisAllocatedPerSecond = _analysisAllocatedBytes == 0 ? 0 :
            (allocated - _analysisAllocatedBytes) / _analysisRefreshSeconds;
        _analysisAllocatedBytes = allocated;
        _analysisGen0 = GC.CollectionCount(0);
        _analysisGen1 = GC.CollectionCount(1);
        _analysisGen2 = GC.CollectionCount(2);
        _analysisRefreshSeconds = 0;
    }

    private void DrawAnalysisWindows()
    {
        if (_showVoxelMeasurements) DrawVoxelMeasurements();
        if (_showPerformanceAnalyzer) DrawPerformanceAnalyzer();
    }

    private void DrawVoxelMeasurements()
    {
        var unit = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new Vector2(unit * 35F, unit * 18F), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("体素度量", ref _showVoxelMeasurements))
        {
            DrawExtraMeasurements();
        }
        ImGui.End();
    }

    private void DrawPerformanceAnalyzer()
    {
        var unit = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new Vector2(unit * 42F, unit * 36F), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("性能分析器", ref _showPerformanceAnalyzer))
        {
            DrawExtraPerformance();
        }
        ImGui.End();
    }

    private void DrawExtraMeasurements()
    {

            if (_project is null) ImGui.TextDisabled("打开项目后显示当前工作世界的统计");
            else
            {
                _measurementRefreshSeconds += ImGui.GetIO().DeltaTime;
                if (!ReferenceEquals(_measuredWorld, _world) || _measurementRefreshSeconds >= 1F)
                {
                    _measuredWorld = _world;
                    _measuredVoxels = _world.OccupiedVoxelCount;
                    _measuredChunks = _world.LoadedChunkCount;
                    _measurementRefreshSeconds = 0;
                }
                ImGui.SeparatorText("当前加载的工作世界");
                ImGui.Text($"占用体素  {_measuredVoxels:N0}");
                ImGui.Text($"非空区块  {_measuredChunks:N0}");
                ImGui.Text($"场景视口工作集  {_scenePipeline?.SyncTotalCells ?? 0:N0}");
                ImGui.Text($"渲染视口工作集  {_pipeline?.SyncTotalCells ?? 0:N0}");
                ImGui.TextWrapped("体素数统计当前加载并合并的工作世界，包含隐藏内容；重叠位置按一个格子计算。它不是项目目录下所有资产的体素总和。工作集是渲染数据，可能经过合并或过滤，不能等同于体素数。");
            }

    }

    private void DrawExtraPerformance()
    {
        var unit = ImGui.GetFontSize();

            if (ImGui.Button("清空帧记录"))
            {
                _analysisFrameCount = 0;
                _analysisFrameIndex = 0;
                Array.Clear(_frameIntervals);
                Array.Clear(_renderCpuTimes);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("只清除分析器的帧历史，不改变项目或系统计数");
            ImGui.SameLine();
            ImGui.TextDisabled($"最近 {_analysisFrameCount} 帧");
            if (_analysisFrameCount > 0)
            {
                var last = (_analysisFrameIndex + _frameIntervals.Length - 1) % _frameIntervals.Length;
                var offset = _analysisFrameCount == _frameIntervals.Length ? _analysisFrameIndex : 0;
                ImGui.SeparatorText("帧时间");
                ImGui.Text($"帧间隔  {_frameIntervals[last]:0.00} ms   渲染回调墙钟  {_renderCpuTimes[last]:0.00} ms");
                ImGui.PlotLines("##帧间隔", ref _frameIntervals[0], _analysisFrameCount, offset, "帧间隔 ms", 0F, float.MaxValue, new Vector2(-1F, unit * 4F));
                ImGui.PlotLines("##渲染回调", ref _renderCpuTimes[0], _analysisFrameCount, offset, "渲染回调 ms", 0F, float.MaxValue, new Vector2(-1F, unit * 4F));
                ImGui.TextWrapped("帧间隔包含等待和调度；渲染回调墙钟包含提交与可能的等待，并非 GPU 执行时间。采样仅在分析器打开时进行。");
            }
            ImGui.SeparatorText("进程内存与垃圾回收");
            ImGui.Text($"工作集  {ToMiB(_analysisWorkingSet):0.0} MiB   私有内存  {ToMiB(_analysisPrivateBytes):0.0} MiB");
            ImGui.Text($"托管堆  {ToMiB(_analysisManagedBytes):0.0} MiB   托管分配  {ToMiB(_analysisAllocatedPerSecond):0.00} MiB/s");
            ImGui.Text($"GC 次数  0代 {_analysisGen0:N0}  ·  1代 {_analysisGen1:N0}  ·  2代 {_analysisGen2:N0}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("进程启动以来的累计回收次数；内存与分配速度约每秒刷新");
            ImGui.SeparatorText("视口最近一次资源更新（CPU）");
            DrawAnalysisPipeline("场景", _scenePipeline);
            DrawAnalysisPipeline("渲染", _pipeline);
            ImGui.TextDisabled("显存数字为体素和 BVH 缓冲容量，不代表总显存；GPU 耗时尚未采集");

    }

    private static double ToMiB(double bytes) => bytes / (1024D * 1024D);

    private static void DrawAnalysisPipeline(string name, VulkanVoxelRenderPipeline? pipeline)
    {
        if (pipeline is null)
        {
            ImGui.TextDisabled($"{name}：尚无管线数据");
            return;
        }
        var capacity = pipeline.GpuBufferCapacity;
        ImGui.Text($"{name}：工作集 {pipeline.SyncTotalCells:N0}  派发 {pipeline.LastDispatchCount:N0}");
        ImGui.Text($"构建 {pipeline.SyncWorksetBuildMs:0.00}  BVH {pipeline.SyncBvhBuildMs:0.00}  打包 {pipeline.SyncPackMs:0.00}  上传 {pipeline.SyncUploadMs:0.00} ms");
        ImGui.Text($"发光面 {pipeline.EmissiveFaceCount:N0}  索引同步 {pipeline.EmissiveSyncMilliseconds:0.00} ms");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("最近一次 CPU 发光面提取、排序、打包与上传的耗时，不是 GPU 采样耗时。当前采用均匀面采样，反向 PDF 使用二分查询。");
        ImGui.Text($"体素/BVH 缓冲容量 {ToMiB(capacity.VoxelBytes + (double)capacity.BvhBytes):0.0} MiB");
    }
}
