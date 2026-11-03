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
    internal void Record(double frameMs, double cpuMs, long submits, double fenceWaitMs,
        int syncCount, double syncMs, int dispatches, int width, int height, int cells,
        double gpuMs, double gpuSamplingMs, double gpuFinishMs, int accumulatedSamples,
        double allocatedKb)
    {
        var now = Environment.TickCount64;
        if (_windowStartMs == 0)
        {
            _windowStartMs = now;
        }

        // 同步耗时只在真正发生重建的那一帧才算进本帧开销，避免把历史值当成常态
        var frameSyncMs = syncCount != _lastSyncCount ? syncMs : 0;
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

        // 只有真正提交过 GPU 工作的帧才可能是视口渲染卡顿，
        // 项目加载等阻塞操作产生的长帧不归到视口头上
        var didRenderWork = submits > 0 || cpuMs > 0.5 || fenceWaitMs > 0.5;
        if (didRenderWork && frameMs >= SlowFrameMs && now - _slowReportMs >= SlowReportIntervalMs)
        {
            _slowReportMs = now;
            MVoxelLog.Warn($"视口掉帧｜{_viewport}：本帧 {frameMs:0.0}ms，" +
                $"CPU 提交 {cpuMs:0.0}ms，栅栏等待 {Math.Max(0, fenceWaitMs):0.0}ms，" +
                $"队列提交 {submits} 次，工作集同步 {frameSyncMs:0.0}ms，" +
                $"派发 {dispatches} 次，分配 {allocatedKb:0}KB，输出 {width}x{height}，体素 {cells:N0}");
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
            $"分配 {_allocSum / frames:0}KB/帧，输出 {_lastWidth}x{_lastHeight}，体素 {_lastCells:N0}");
        Reset(now);
    }

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
    }
}
