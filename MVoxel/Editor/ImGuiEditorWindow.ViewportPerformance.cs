using ImGuiNET;
using MVoxel.Editor.Rendering.Vulkan;

namespace MVoxel.Editor;

/// <summary>
/// 场景窗口与渲染窗口的帧开销采样，日志用于在用户机器上定位视口卡顿来源
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private readonly ViewportPerformanceLog _sceneViewportPerformance = new("场景窗口");
    private readonly ViewportPerformanceLog _renderViewportPerformance = new("渲染窗口");

    /// <summary>
    /// 记录一次 GPU 视口帧的提交、等待与同步开销
    /// </summary>
    /// <param name="log">对应视口的采样器</param>
    /// <param name="pipeline">当前视口管线</param>
    /// <param name="cpuMs">本帧提交与录制占用的 CPU 毫秒</param>
    /// <param name="submitsBefore">本帧开始前的提交累计次数</param>
    /// <param name="fenceBefore">本帧开始前的栅栏等待累计毫秒</param>
    /// <param name="allocatedBytesBefore">本帧开始前的线程分配字节数</param>
    /// <param name="width">输出宽度</param>
    /// <param name="height">输出高度</param>
    private void RecordViewportFrame(ViewportPerformanceLog log, VulkanVoxelRenderPipeline pipeline,
        double cpuMs, long submitsBefore, double fenceBefore, long allocatedBytesBefore, int width, int height)
    {
        var allocatedKb = (GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore) / 1024.0;
        var gpuProfiling = pipeline.GpuProfilingEnabled;
        // 归因来自管线最近一次提交：等待已提交计算完成、硬件阴影加速结构重建，
        // 再补上交换链的采集与呈现等待，便于判断卡在 CPU、GPU 还是呈现
        var attribution = new ViewportPerformanceLog.FrameAttribution(
            pipeline.LastComputeIdleWaitMs, pipeline.LastShadowAccelerationBuildMs,
            pipeline.HardwareShadowsActive, pipeline.SoftwareBvhVariantName,
            _vulkan?.LastPresentMs ?? 0, _vulkan?.LastAcquireMs ?? 0,
            pipeline.DeviceName);
        log.Record(ImGui.GetIO().DeltaTime * 1000.0, cpuMs, pipeline.SubmitCount - submitsBefore,
            pipeline.FenceWaitTotalMs - fenceBefore, pipeline.SyncCompletedCount, pipeline.SyncTotalMs,
            pipeline.LastDispatchCount, width, height, pipeline.SyncTotalCells,
            gpuProfiling ? pipeline.LastGpuFrameMs : 0,
            gpuProfiling ? pipeline.LastGpuSamplingMs : 0,
            gpuProfiling ? pipeline.LastGpuFinishMs : 0,
            pipeline.AccumulatedSamples,
            allocatedKb, attribution);
    }
}
