using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>
/// 视口帧开销采样：汇总提交次数、栅栏等待、工作集同步与 CPU 记录耗时，
/// 按固定间隔写一条日志，并在单帧明显超时时立即告警
/// </summary>
/// <remarks>
/// 只做统计和记录，不参与渲染决策，也不改变任何画质
/// 定期日志用于看清常态开销，超时告警用于抓住偶发卡顿的那一帧
/// </remarks>
internal sealed class ViewportPerformanceLog
{
    /// <summary>
    /// 本帧卡顿归因：区分等待已提交计算完成、硬件阴影加速结构重建与窗口呈现，
    /// 用于在没有 GPU 时间戳时也能判断卡在哪一步
    /// </summary>
    internal readonly record struct FrameAttribution(
        double ComputeIdleWaitMs,
        double AccelerationBuildMs,
        bool HardwareShadows,
        string SoftwareVariant,
        double WindowPresentMs,
        double WindowAcquireMs,
        double WindowRecordMs,
        double WindowSubmitMs,
        double UiDrawMs,
        double ImGuiDrawMs,
        string Device,
        double WorksetBuildMs,
        double BvhBuildMs,
        double PackMs,
        double UploadMs,
        bool EditRegion);

    /// <summary>常态统计的汇总周期</summary>
    private const double ReportIntervalMs = 5000;

    /// <summary>单帧超过该耗时视为明显卡顿</summary>
    private const double SlowFrameMs = 250;

    /// <summary>卡顿告警的最小间隔，避免连续卡顿刷屏</summary>
    private const double SlowReportIntervalMs = 2000;

    private readonly string _viewport;
    private long _windowStartMs;
    private long _slowReportMs;
    private int _frames;
    private double _frameMsSum;
    private double _frameMsMax;
    private double _submitSum;
    private double _fenceWaitSum;
    private double _cpuSum;
    private double _syncMax;
    private int _lastWidth;
    private int _lastHeight;
    private int _lastCells;
    private long _submitMax;
    private int _dispatchMax;
    private int _lastSyncCount;
    private double _gpuSum;
    private double _gpuMax;
    private int _gpuSamples;
    private double _gpuSamplingSum;
    private double _gpuFinishSum;
    private double _allocSum;
    private double _idleWaitSum;
    private double _accelBuildMax;
    private double _presentMax;
    private double _presentSum;
    private double _acquireSum;
    private double _recordSum;
    private double _cmdSubmitSum;
    private double _uiSum;
    private double _imguiSum;
    private int _hardwareShadowFrames;
    private int _softwareShadowFrames;
    private string _device = "未知设备";
    // 工作集重建的分段峰值：拖动绘制时每次提交都会重建工作集，
    // 需要看清构建、BVH、打包与上传各占多少
    private double _worksetBuildMax;
    private double _bvhBuildMax;
    private double _packMax;
    private double _uploadMax;
    private int _editRegionFrames;
    // 路径追踪样本吞吐：记录窗口内的起点与终点样本数，
    // 计数器回退（重置、换相机）时把起点挪到新值，避免算出负数
    private int _samplesFirst;
    private int _samplesLast;
    private bool _samplesTracked;

    /// <summary>
    /// 创建单个视口的采样器
    /// </summary>
    /// <param name="viewport">视口名称，用于区分场景窗口与渲染窗口</param>
    internal ViewportPerformanceLog(string viewport) => _viewport = viewport;

    /// <summary>
    /// 记录一个已完成的视口帧
    /// </summary>
    /// <param name="frameMs">调用方测得的帧间隔毫秒</param>
    /// <param name="cpuMs">本帧 GPU 提交与录制占用的 CPU 毫秒</param>
    /// <param name="submits">本帧队列提交次数</param>
    /// <param name="fenceWaitMs">本帧等待栅栏的毫秒</param>
    /// <param name="syncCount">工作集累计同步次数，用于判断本帧是否真的重建过工作集</param>
    /// <param name="syncMs">最近一次工作集同步耗时毫秒</param>
    /// <param name="dispatches">本帧计算派发次数</param>
    /// <param name="width">输出宽度</param>
    /// <param name="height">输出高度</param>
    /// <param name="cells">工作集体素数</param>
    /// <param name="gpuMs">最近一次回读的 GPU 计算总耗时，未采样时为 0</param>
    /// <param name="gpuSamplingMs">最近一次回读的采样段耗时</param>
    /// <param name="gpuFinishMs">最近一次回读的收尾段耗时</param>
    /// <param name="accumulatedSamples">当前累积样本数，用于统计样本吞吐</param>
    /// <param name="allocatedKb">本帧线程分配字节数换算的 KB</param>
    /// <param name="attribution">本帧卡顿归因；未提交计算时传入的数值不会计入统计</param>
    internal void Record(double frameMs, double cpuMs, long submits, double fenceWaitMs,
        int syncCount, double syncMs, int dispatches, int width, int height, int cells,
        double gpuMs, double gpuSamplingMs, double gpuFinishMs, int accumulatedSamples,
        double allocatedKb, FrameAttribution attribution)
    {
        var now = Environment.TickCount64;
        if (_windowStartMs == 0)
        {
            _windowStartMs = now;
        }

        // 同步耗时只在真正发生重建的那一帧才算进本帧开销，避免把历史值当成常态
        var frameSyncMs = syncCount != _lastSyncCount ? syncMs : 0;
        if (syncCount != _lastSyncCount)
        {
            _worksetBuildMax = Math.Max(_worksetBuildMax, attribution.WorksetBuildMs);
            _bvhBuildMax = Math.Max(_bvhBuildMax, attribution.BvhBuildMs);
            _packMax = Math.Max(_packMax, attribution.PackMs);
            _uploadMax = Math.Max(_uploadMax, attribution.UploadMs);
        }
        if (attribution.EditRegion) _editRegionFrames++;
        _lastSyncCount = syncCount;
        _frames++;
        _frameMsSum += frameMs;
        _frameMsMax = Math.Max(_frameMsMax, frameMs);
        _submitSum += submits;
        _submitMax = Math.Max(_submitMax, submits);
        _dispatchMax = Math.Max(_dispatchMax, dispatches);
        _fenceWaitSum += Math.Max(0, fenceWaitMs);
        _cpuSum += cpuMs;
        _syncMax = Math.Max(_syncMax, frameSyncMs);
        _gpuSum += Math.Max(0, gpuMs);
        _gpuSamples += gpuMs > 0 ? 1 : 0;
        _gpuMax = Math.Max(_gpuMax, gpuMs);
        _gpuSamplingSum += Math.Max(0, gpuSamplingMs);
        _gpuFinishSum += Math.Max(0, gpuFinishMs);
        if (!_samplesTracked || accumulatedSamples < _samplesLast)
        {
            _samplesFirst = accumulatedSamples;
            _samplesTracked = true;
        }

        _samplesLast = accumulatedSamples;
        _allocSum += Math.Max(0, allocatedKb);
        _lastWidth = width;
        _lastHeight = height;
        _lastCells = cells;
        if (attribution.Device.Length > 0) _device = attribution.Device;
        // 归因数值是“最近一次提交”的静态结果，只有本帧真的派发过计算才计入，
        // 否则复用画面的空闲帧会把历史峰值反复累加
        if (dispatches > 0)
        {
            _idleWaitSum += Math.Max(0, attribution.ComputeIdleWaitMs);
            _accelBuildMax = Math.Max(_accelBuildMax, attribution.AccelerationBuildMs);
            if (attribution.HardwareShadows)
            {
                _hardwareShadowFrames++;
            }
            else
            {
                _softwareShadowFrames++;
            }
        }

        _presentSum += Math.Max(0, attribution.WindowPresentMs);
        _presentMax = Math.Max(_presentMax, attribution.WindowPresentMs);
        // 交换链采集 / 命令录制 / 队列提交 / UI 与 ImGui 绘制都按每帧累加，
        // 用来把帧间隔里除去 GPU 的部分逐项拆开
        _acquireSum += Math.Max(0, attribution.WindowAcquireMs);
        _recordSum += Math.Max(0, attribution.WindowRecordMs);
        _cmdSubmitSum += Math.Max(0, attribution.WindowSubmitMs);
        _uiSum += Math.Max(0, attribution.UiDrawMs);
        _imguiSum += Math.Max(0, attribution.ImGuiDrawMs);

        // 只有真正提交过 GPU 工作的帧才可能是视口渲染卡顿，
        // 项目加载等阻塞操作产生的长帧不归到视口头上
        var didRenderWork = submits > 0 || cpuMs > 0.5 || fenceWaitMs > 0.5;
        if (didRenderWork && frameMs >= SlowFrameMs && now - _slowReportMs >= SlowReportIntervalMs)
        {
            _slowReportMs = now;
            MVoxelLog.Warn($"视口掉帧｜{_viewport}：本帧 {frameMs:0.0}ms，" +
                $"CPU 提交 {cpuMs:0.0}ms，栅栏等待 {Math.Max(0, fenceWaitMs):0.0}ms，" +
                $"队列提交 {submits} 次，工作集同步 {frameSyncMs:0.0}ms，" +
                $"派发 {dispatches} 次，分配 {allocatedKb:0}KB，输出 {width}x{height}，体素 {cells:N0}；" +
                $"归因 等待计算空闲 {attribution.ComputeIdleWaitMs:0.0}ms，" +
                $"加速结构重建 {attribution.AccelerationBuildMs:0.0}ms，" +
                $"求交 {ShadowBackend(attribution.HardwareShadows, attribution.SoftwareVariant)}，" +
                $"呈现等待 {attribution.WindowPresentMs:0.0}ms，" +
                $"采集等待 {attribution.WindowAcquireMs:0.0}ms，设备 {_device}");
        }

        if (now - _windowStartMs < ReportIntervalMs)
        {
            return;
        }

        var frames = Math.Max(1, _frames);
        var coverageSeconds = Math.Max(0.001, _frameMsSum / 1000.0);
        var samplesPerSecond = Math.Max(0, _samplesLast - _samplesFirst) / coverageSeconds;
        MVoxelLog.Info($"视口性能｜{_viewport}：帧均值 {_frameMsSum / frames:0.0}ms，峰值 {_frameMsMax:0.0}ms，" +
            $"队列提交 均值 {_submitSum / frames:0.00} 次/帧 峰值 {_submitMax} 次，" +
            $"栅栏等待 {_fenceWaitSum / frames:0.0}ms/帧，CPU 提交 {_cpuSum / frames:0.0}ms/帧，" +
            $"派发峰值 {_dispatchMax} 次，同步峰值 {_syncMax:0.0}ms，" +
            $"GPU {(_gpuSamples > 0 ? $"{_gpuSum / _gpuSamples:0.0}ms 均值（采样 {_gpuSamplingSum / _gpuSamples:0.0} + 收尾 {_gpuFinishSum / _gpuSamples:0.0}） 峰值 {_gpuMax:0.0}ms" : "未采样")}，" +
            $"样本吞吐 {samplesPerSecond:0.00} spp/s，" +
            $"分配 {_allocSum / frames:0}KB/帧，" +
            $"求交后端 硬件 {_hardwareShadowFrames} 帧/软件 {_softwareShadowFrames} 帧，" +
            $"等待计算空闲 {_idleWaitSum / frames:0.0}ms/帧，加速重建峰值 {_accelBuildMax:0.0}ms，" +
            $"呈现等待 {_presentSum / frames:0.0}ms/帧 峰值 {_presentMax:0.0}ms，" +
            $"采集 {_acquireSum / frames:0.0}ms 录制 {_recordSum / frames:0.0}ms " +
            $"提交 {_cmdSubmitSum / frames:0.0}ms UI {_uiSum / frames:0.0}ms ImGui {_imguiSum / frames:0.0}ms，" +
            $"工作集峰值 构建 {_worksetBuildMax:0.0}ms BVH {_bvhBuildMax:0.0}ms " +
            $"打包 {_packMax:0.0}ms 上传 {_uploadMax:0.0}ms，编辑区域 {_editRegionFrames} 帧，" +
            $"输出 {_lastWidth}x{_lastHeight}，体素 {_lastCells:N0}，设备 {_device}");
        Reset(now);
    }

    /// <summary>
    /// 阴影求交后端名称
    /// </summary>
    private static string ShadowBackend(bool hardware, string softwareVariant) =>
        hardware ? "硬件 Ray Query"
        : softwareVariant.Length > 0 ? $"软件 BVH（{softwareVariant}）" : "软件 BVH";

    /// <summary>
    /// 清空统计，进入下一个汇总周期
    /// </summary>
    private void Reset(long now)
    {
        _windowStartMs = now;
        _frames = 0;
        _frameMsSum = 0;
        _frameMsMax = 0;
        _submitSum = 0;
        _submitMax = 0;
        _dispatchMax = 0;
        _fenceWaitSum = 0;
        _cpuSum = 0;
        _syncMax = 0;
        _gpuSum = 0;
        _gpuMax = 0;
        _gpuSamples = 0;
        _gpuSamplingSum = 0;
        _gpuFinishSum = 0;
        _samplesTracked = false;
        _samplesFirst = 0;
        _samplesLast = 0;
        _allocSum = 0;
        _idleWaitSum = 0;
        _accelBuildMax = 0;
        _presentMax = 0;
        _presentSum = 0;
        _acquireSum = 0;
        _recordSum = 0;
        _cmdSubmitSum = 0;
        _uiSum = 0;
        _imguiSum = 0;
        _hardwareShadowFrames = 0;
        _softwareShadowFrames = 0;
        _worksetBuildMax = 0;
        _bvhBuildMax = 0;
        _packMax = 0;
        _uploadMax = 0;
        _editRegionFrames = 0;
    }
}
