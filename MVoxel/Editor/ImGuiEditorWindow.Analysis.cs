using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MVoxel.Editor.Rendering.Vulkan;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private const int AnalysisFrameCapacity = 240;

    /// <summary>
    /// 单帧性能样本：记录界面各阶段、视口提交与 GPU 时间戳
    /// </summary>
    private struct AnalysisFrameSample
    {
        public long Serial;
        public float IntervalMs;
        public float CallbackMs;
        public float UpdateMs;
        public float UiMs;
        public float MenuDockMs;
        public float HierarchyMs;
        public float CenterMs;
        public float ViewportCpuMs;
        public float InspectorMs;
        public float OtherPanelsMs;
        public float DialogsMs;
        public float ImGuiRenderMs;
        public float PresentMs;
        public float PresentAcquireMs;
        public float PresentFenceWaitMs;
        public float PresentRecordMs;
        public float PresentSubmitMs;
        public float PresentQueueMs;
        public float ViewportFenceWaitMs;
        public float GpuMs;
        public float GpuSamplingMs;
        public float GpuFinishMs;
        public bool GpuValid;
        public bool ViewportRendered;
        public bool PathTracing;
        public int ViewportSubmits;
        public int ViewportDispatches;
        public int ViewportWidth;
        public int ViewportHeight;
    }

    private bool _showVoxelMeasurements;
    private bool _showPerformanceAnalyzer;
    private readonly AnalysisFrameSample[] _analysisFrames = new AnalysisFrameSample[AnalysisFrameCapacity];
    private readonly float[] _analysisStatBuffer = new float[AnalysisFrameCapacity];
    private AnalysisFrameSample _analysisLive;
    private long _analysisSerial;
    private bool _analysisPaused, _analysisFollowLatest = true;
    private long _analysisSelectedSerial;
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

    /// <summary>
    /// 性能分析器当前是否可见，独立窗口或额外检查器中的面板都算
    /// </summary>
    private bool IsPerformanceAnalyzerVisible
    {
        get
        {
            if (_showPerformanceAnalyzer) return true;
            for (var index = 0; index < _extraInspectors.Count; index++)
            {
                var item = _extraInspectors[index];
                if (item.Open && item.Kind == "性能分析器") return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 是否正在采样：窗口可见且未暂停，关闭窗口后不再产生新样本
    /// </summary>
    private bool PerformanceProbeActive => IsPerformanceAnalyzerVisible && !_analysisPaused;

    /// <summary>
    /// 开始一帧采样，样本序号用于把异步回读的 GPU 结果写回对应帧
    /// </summary>
    private void BeginPerformanceFrame()
    {
        if (!PerformanceProbeActive) return;
        _analysisLive = default;
        _analysisLive.Serial = ++_analysisSerial;
    }

    private long PerformanceMark() => PerformanceProbeActive ? Stopwatch.GetTimestamp() : 0L;

    private static void MeasureSince(long mark, ref float target)
    {
        if (mark == 0L) return;
        target = (float)Stopwatch.GetElapsedTime(mark).TotalMilliseconds;
    }

    /// <summary>
    /// 记录一帧完整样本，暂停或窗口关闭时不再写入
    /// </summary>
    private void RecordPerformanceFrame(double intervalSeconds, long start)
    {
        if (!PerformanceProbeActive) return;
        var sample = _analysisLive;
        sample.IntervalMs = (float)(Math.Max(intervalSeconds, 0D) * 1000D);
        sample.CallbackMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (_vulkan is { } window)
        {
            sample.PresentAcquireMs = (float)window.LastAcquireMs;
            sample.PresentFenceWaitMs = (float)window.LastFrameFenceWaitMs;
            sample.PresentRecordMs = (float)window.LastRecordMs;
            sample.PresentSubmitMs = (float)window.LastSubmitMs;
            sample.PresentQueueMs = (float)window.LastPresentMs;
        }

        _analysisFrames[_analysisFrameIndex] = sample;
        _analysisFrameIndex = (_analysisFrameIndex + 1) % AnalysisFrameCapacity;
        _analysisFrameCount = Math.Min(_analysisFrameCount + 1, AnalysisFrameCapacity);
    }

    /// <summary>
    /// 回读两个管线已经完成的 GPU 时间戳，并把结果写回发起采样的那一帧
    /// </summary>
    private void PollPerformanceGpuTimings()
    {
        StoreGpuTiming(_scenePipeline);
        StoreGpuTiming(_pipeline);
    }

    private void StoreGpuTiming(VulkanVoxelRenderPipeline? pipeline)
    {
        if (pipeline is null ||
            !pipeline.TryTakeGpuTiming(out var token, out var milliseconds,
                out var samplingMilliseconds, out var finishMilliseconds)) return;
        if (token <= 0) return;
        var slot = (int)((token - 1) % AnalysisFrameCapacity);
        if (_analysisFrames[slot].Serial != token) return;
        _analysisFrames[slot].GpuMs = (float)milliseconds;
        _analysisFrames[slot].GpuSamplingMs = (float)samplingMilliseconds;
        _analysisFrames[slot].GpuFinishMs = (float)finishMilliseconds;
        _analysisFrames[slot].GpuValid = true;
    }

    /// <summary>
    /// 记录视口一帧的 CPU 提交与 GPU 队列信息，供分析器把中央视口拆开看
    /// </summary>
    private void RecordPerformanceViewport(VulkanVoxelRenderPipeline pipeline, double cpuMs,
        long submitsBefore, double fenceWaitBefore, int width, int height, bool pathTracing)
    {
        if (!PerformanceProbeActive) return;
        _analysisLive.ViewportRendered = true;
        _analysisLive.ViewportCpuMs = (float)cpuMs;
        _analysisLive.ViewportSubmits = (int)Math.Max(0L, pipeline.SubmitCount - submitsBefore);
        _analysisLive.ViewportFenceWaitMs = (float)Math.Max(0D, pipeline.FenceWaitTotalMs - fenceWaitBefore);
        _analysisLive.ViewportDispatches = pipeline.LastDispatchCount;
        _analysisLive.ViewportWidth = width;
        _analysisLive.ViewportHeight = height;
        _analysisLive.PathTracing = pathTracing;
    }

    /// <summary>
    /// 每秒刷新一次进程内存与 GC 计数，暂停帧历史时也保持实时
    /// </summary>
    private void UpdatePerformanceMemory(double deltaSeconds)
    {
        if (!IsPerformanceAnalyzerVisible) return;
        _analysisRefreshSeconds += Math.Max(deltaSeconds, 0D);
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

    private AnalysisFrameSample LatestAnalysisFrame => _analysisFrameCount == 0
        ? default
        : _analysisFrames[(_analysisFrameIndex - 1 + AnalysisFrameCapacity) % AnalysisFrameCapacity];

    private void ResetAnalysisFrames()
    {
        _analysisFrameCount = 0;
        _analysisFrameIndex = 0;
        // 跨过一整轮容量且保持 serial % 容量 == 0，下一次采样会从槽位 0 开始，
        // 迟到回读的旧 GPU 结果也不会误写进新记录
        _analysisSerial = (_analysisSerial / AnalysisFrameCapacity + 1) * AnalysisFrameCapacity;
        _analysisSelectedSerial = 0;
        _analysisLive = default;
        Array.Clear(_analysisFrames);
    }

    /// <summary>
    /// 暂停或继续录制，暂停时保留当前帧和已有历史，不清空
    /// </summary>
    private void SetAnalysisPaused(bool paused)
    {
        _analysisPaused = paused;
        if (paused)
        {
            _analysisFollowLatest = false;
            if (_analysisFrameCount > 0) _analysisSelectedSerial = LatestAnalysisFrame.Serial;
        }
        else
        {
            _analysisFollowLatest = true;
            _analysisSelectedSerial = 0;
        }
    }

    /// <summary>
    /// 选中并保留某一帧，与 Unity 点击时间轴后停在所选帧的行为一致
    /// </summary>
    private void SelectAnalysisFrame(long serial)
    {
        _analysisSelectedSerial = serial;
        _analysisFollowLatest = false;
        _analysisPaused = true;
    }

    private void SelectLatestAnalysisFrame()
    {
        _analysisFollowLatest = true;
        _analysisSelectedSerial = 0;
    }

    /// <summary>
    /// 取得当前要展示的帧：跟随最新或按选中序号查找，序号已被覆盖时退回最新
    /// </summary>
    private bool TryGetAnalysisFrame(out AnalysisFrameSample sample)
    {
        sample = default;
        if (_analysisFrameCount == 0) return false;
        if (_analysisFollowLatest || _analysisSelectedSerial <= 0)
        {
            sample = LatestAnalysisFrame;
            return true;
        }

        for (var offset = 0; offset < _analysisFrameCount; offset++)
        {
            var index = (_analysisFrameIndex - _analysisFrameCount + offset + AnalysisFrameCapacity * 2) % AnalysisFrameCapacity;
            if (_analysisFrames[index].Serial != _analysisSelectedSerial) continue;
            sample = _analysisFrames[index];
            return true;
        }

        sample = LatestAnalysisFrame;
        return true;
    }

    private void DrawAnalysisWindows()
    {
        if (_showVoxelMeasurements) DrawVoxelMeasurements();
        if (_showPerformanceAnalyzer) DrawPerformanceAnalyzer();
    }

    private void DrawVoxelMeasurements()
    {
        var unit = ImGui.GetFontSize();
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
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
        if (_applyPreset && _layoutPreset == LayoutPreset.Analysis) ImGui.SetNextWindowFocus();
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(unit * 50F, unit * 40F), ImGuiCond.FirstUseEver);
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

        var recording = !_analysisPaused;
        if (recording) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72F, 0.18F, 0.18F, 1F));
        if (ImGui.Button(recording ? "录制中" : "已暂停")) SetAnalysisPaused(recording);
        if (recording) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开分析器即开始记录；点击暂停会保留已有帧和当前所选帧，再次点击继续记录，不会清空历史。" + Environment.NewLine +
                "关闭分析器窗口也会停止采样。");

        ImGui.SameLine();
        if (ImGui.Button("清空")) ResetAnalysisFrames();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("清空所有已记录帧，不改变当前的录制或暂停状态");

        ImGui.SameLine();
        if (ImGui.Button("最新帧")) SelectLatestAnalysisFrame();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("选中最新一帧并恢复跟随；对应 Unity 性能分析器的 Current 按钮");

        ImGui.SameLine();
        ImGui.TextDisabled(recording
            ? $"录制中 {_analysisFrameCount}/{AnalysisFrameCapacity}"
            : $"已暂停 {_analysisFrameCount}/{AnalysisFrameCapacity}");
        if (!_analysisFollowLatest && _analysisFrameCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· 已选第 {_analysisSelectedSerial} 帧");
        }

        if (_analysisFrameCount > 0)
        {
            DrawAnalysisFrameChart(unit);
            DrawAnalysisSelectedFrame(unit);
            DrawAnalysisStatistics();
        }
        else
        {
            ImGui.SeparatorText("帧记录");
            ImGui.TextDisabled("打开项目并保持本窗口可见即开始采样；暂停只停止记录，不会清空已有帧。");
        }

        ImGui.SeparatorText("进程内存与垃圾回收");
        ImGui.Text($"工作集  {ToMiB(_analysisWorkingSet):0.0} MiB   私有内存  {ToMiB(_analysisPrivateBytes):0.0} MiB");
        ImGui.Text($"托管堆  {ToMiB(_analysisManagedBytes):0.0} MiB   托管分配  {ToMiB(_analysisAllocatedPerSecond):0.00} MiB/s");
        ImGui.Text($"GC 次数  0代 {_analysisGen0:N0}  ·  1代 {_analysisGen1:N0}  ·  2代 {_analysisGen2:N0}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("进程启动以来的累计回收次数；内存与分配速度约每秒刷新");

        ImGui.SeparatorText("视口最近一次资源更新（CPU）");
        DrawAnalysisPipeline("场景", _scenePipeline);
        DrawAnalysisPipeline("渲染", _pipeline);
        ImGui.TextDisabled("显存数字为体素和 BVH 缓冲容量，不代表总显存；时间戳只覆盖 Compute 提交，不包含呈现等待。");
    }

    /// <summary>
    /// 绘制最近 240 帧的堆叠柱状图，点击任意柱选择该帧
    /// </summary>
    private void DrawAnalysisFrameChart(float unit)
    {
        var width = Math.Max(1F, ImGui.GetContentRegionAvail().X);
        var height = unit * 6F;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var offset = _analysisFrameCount == AnalysisFrameCapacity ? _analysisFrameIndex : 0;
        var maxMs = 1F;
        for (var index = 0; index < _analysisFrameCount; index++)
        {
            var sample = _analysisFrames[(offset + index) % AnalysisFrameCapacity];
            maxMs = Math.Max(maxMs, sample.CallbackMs);
        }

        var scaleMs = Math.Max(1F, MathF.Ceiling(maxMs / 5F) * 5F);
        var barWidth = width / _analysisFrameCount;
        var selectedIndex = -1;
        for (var index = 0; index < _analysisFrameCount; index++)
        {
            var sample = _analysisFrames[(offset + index) % AnalysisFrameCapacity];
            if (!_analysisFollowLatest && sample.Serial == _analysisSelectedSerial) selectedIndex = index;
            var x0 = origin.X + index * barWidth;
            var x1 = origin.X + (index + 1) * barWidth;
            var y = origin.Y + height;
            var update = Math.Max(0F, sample.UpdateMs);
            var uiRest = Math.Max(0F, sample.CenterMs > 0F ? sample.UiMs - sample.CenterMs : sample.UiMs);
            var centerRest = Math.Max(0F, sample.CenterMs - sample.ViewportCpuMs);
            var imGui = Math.Max(0F, sample.ImGuiRenderMs);
            var present = Math.Max(0F, sample.PresentMs);
            var other = Math.Max(0F, sample.CallbackMs - update - sample.UiMs - imGui - present);

            DrawAnalysisSegment(draw, x0, x1, ref y, other, scaleMs, height, 0xff8a8a8a);
            DrawAnalysisSegment(draw, x0, x1, ref y, present, scaleMs, height, 0xff6586dd);
            DrawAnalysisSegment(draw, x0, x1, ref y, imGui, scaleMs, height, 0xff8e7cc3);
            DrawAnalysisSegment(draw, x0, x1, ref y, centerRest, scaleMs, height, 0xffd08c33);
            DrawAnalysisSegment(draw, x0, x1, ref y, sample.ViewportCpuMs, scaleMs, height, 0xff35c8b0);
            DrawAnalysisSegment(draw, x0, x1, ref y, uiRest, scaleMs, height, 0xff55ad70);
            DrawAnalysisSegment(draw, x0, x1, ref y, update, scaleMs, height, 0xff7f93a8);

            if (sample.GpuValid)
            {
                // GPU 时间戳与 CPU 回调不是同一条时间线，只画一条顶部标记避免看起来像堆叠
                var gpuY = origin.Y + height - Math.Min(1F, sample.GpuMs / scaleMs) * height;
                draw.AddLine(new Vector2(x0, gpuY), new Vector2(Math.Max(x0 + 1F, x1 - 0.35F), gpuY), 0xffe0d060, 1F);
            }
        }

        if (selectedIndex >= 0)
        {
            var x0 = origin.X + selectedIndex * barWidth;
            var x1 = origin.X + (selectedIndex + 1) * barWidth;
            draw.AddRect(new Vector2(x0, origin.Y), new Vector2(x1, origin.Y + height), 0xffffffff, 0F, ImDrawFlags.None, 1F);
        }

        draw.AddRect(new Vector2(origin.X, origin.Y), new Vector2(origin.X + width, origin.Y + height), 0xff3a4250);
        var scaleText = $"{scaleMs:0} ms";
        draw.AddText(new Vector2(origin.X + width - ImGui.CalcTextSize(scaleText).X - 4F, origin.Y + 2F), 0xffc8c8c8, scaleText);
        ImGui.InvisibleButton("##性能帧柱状图", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            var index = Math.Clamp((int)((ImGui.GetIO().MousePos.X - origin.X) / barWidth), 0, _analysisFrameCount - 1);
            var sample = _analysisFrames[(offset + index) % AnalysisFrameCapacity];
            ImGui.SetTooltip($"第 {sample.Serial} 帧" + Environment.NewLine +
                $"帧间隔 {sample.IntervalMs:0.00} ms" + Environment.NewLine +
                $"界面回调 {sample.CallbackMs:0.00} ms" + Environment.NewLine +
                (sample.GpuValid
                    ? $"GPU 计算 {sample.GpuMs:0.00} ms（采样 {sample.GpuSamplingMs:0.00} + 收尾 {sample.GpuFinishMs:0.00}，异步回读）"
                    : "GPU 计算 等待回读或不可用") + Environment.NewLine +
                $"中央视口 {sample.ViewportCpuMs:0.00} ms · 提交 {sample.ViewportSubmits} 次" + Environment.NewLine +
                "点击会暂停录制并查看该帧的逐层耗时" + Environment.NewLine + Environment.NewLine +
                "颜色依次为更新、其他界面、中央视口其余部分、视口 GPU 提交、ImGui 合成、Vulkan 呈现、未归类。" + Environment.NewLine +
                "黄色细线是 GPU Compute 耗时，不参与堆叠；帧间隔包含垂直同步等待。");
        }

        if (ImGui.IsItemClicked())
        {
            var index = Math.Clamp((int)((ImGui.GetIO().MousePos.X - origin.X) / barWidth), 0, _analysisFrameCount - 1);
            SelectAnalysisFrame(_analysisFrames[(offset + index) % AnalysisFrameCapacity].Serial);
        }
    }

    /// <summary>
    /// 从底部向上堆叠一段耗时柱，避免在每帧循环里创建闭包
    /// </summary>
    private static void DrawAnalysisSegment(ImDrawListPtr draw, float x0, float x1, ref float y,
        float milliseconds, float scaleMs, float height, uint color)
    {
        var segmentHeight = Math.Max(0F, milliseconds) / scaleMs * height;
        if (segmentHeight <= 0F) return;
        draw.AddRectFilled(new Vector2(x0, y - segmentHeight),
            new Vector2(Math.Max(x0 + 1F, x1 - 0.35F), y), color);
        y -= segmentHeight;
    }

    /// <summary>
    /// 显示选中帧的逐层耗时表
    /// </summary>
    private void DrawAnalysisSelectedFrame(float unit)
    {
        if (!TryGetAnalysisFrame(out var sample)) return;
        var total = Math.Max(sample.CallbackMs, 0.0001F);
        var recentGpu = _pipeline?.LastGpuFrameMs ?? 0D;
        ImGui.SeparatorText($"第 {sample.Serial} 帧");
        ImGui.TextDisabled($"帧间隔 {sample.IntervalMs:0.00} ms  ·  " +
            $"界面回调 {sample.CallbackMs:0.00} ms  ·  " +
            (sample.GpuValid
                ? $"GPU 计算 {sample.GpuMs:0.00} ms"
                : recentGpu > 0D
                    ? $"GPU 最近完成 {recentGpu:0.00} ms（本帧等待回读）"
                    : "GPU 计算 等待回读或不可用"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("帧间隔是两次编辑器帧开始之间的墙钟，包含垂直同步与调度等待。" +
                Environment.NewLine + "GPU 计算来自 Vulkan 时间戳，异步回读，可能晚一到三帧。" +
                Environment.NewLine + "这些数字用于定位热点，不是完整调用栈。");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                      ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##分析阶段", 4, flags)) return;
        ImGui.TableSetupColumn("阶段", ImGuiTableColumnFlags.WidthStretch, 2.2F);
        ImGui.TableSetupColumn("毫秒", ImGuiTableColumnFlags.WidthStretch, 0.7F);
        ImGui.TableSetupColumn("占回调", ImGuiTableColumnFlags.WidthStretch, 0.7F);
        ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch, 2.2F);
        ImGui.TableHeadersRow();

        void Row(string name, float milliseconds, string note, bool showPercent = true)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(milliseconds >= 0F ? $"{milliseconds:0.00}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(showPercent && milliseconds >= 0F ? $"{milliseconds / total * 100F:0.0}%" : "-");
            ImGui.TableNextColumn();
            if (note.Length > 0) ImGui.TextDisabled(note);
        }

        Row("帧间隔", sample.IntervalMs, "墙钟，含垂直同步与调度等待", showPercent: false);
        Row("编辑器帧回调", sample.CallbackMs, "本次采样覆盖的完整编辑器帧");
        Row("  更新与输入", sample.UpdateMs, "控制器更新");
        Row("  界面绘制", sample.UiMs, "所有 ImGui 窗口与视口提交");
        Row("    菜单与停靠", sample.MenuDockMs, string.Empty);
        Row("    层级", sample.HierarchyMs, string.Empty);
        Row("    中央视口", sample.CenterMs, sample.ViewportRendered
            ? $"{sample.ViewportWidth}×{sample.ViewportHeight}，{(sample.PathTracing ? "路径追踪" : "实时")}"
            : "本帧未提交 GPU 视口");
        Row("      GPU 队列提交", sample.ViewportRendered ? sample.ViewportCpuMs : -1F,
            sample.ViewportRendered
                ? $"{sample.ViewportSubmits} 次提交，{sample.ViewportDispatches} 次派发，栅栏等待 {sample.ViewportFenceWaitMs:0.00} ms"
                : "CPU 录制与提交，不包含 GPU 执行");
        Row("      其余视口界面", sample.ViewportRendered ? Math.Max(0F, sample.CenterMs - sample.ViewportCpuMs) : sample.CenterMs,
            "相机输入、叠加层与图像绘制");
        Row("    检查器与渲染配置", sample.InspectorMs, string.Empty);
        Row("    其他面板", sample.OtherPanelsMs, "资产、生成器、插件、环境、动画等");
        Row("    弹窗与对话框", sample.DialogsMs, "设置、指南、欢迎页与确认框");
        Row("  ImGui 合成", sample.ImGuiRenderMs, "顶点缓冲上传与绘制命令录制");
        Row("  Vulkan 呈现", sample.PresentMs, "交换链图像获取、录制、提交与呈现");
        Row("    获取交换链图像", sample.PresentAcquireMs, "可能等待垂直同步");
        Row("    帧栅栏等待", sample.PresentFenceWaitMs, "等待上一帧命令完成");
        Row("    命令录制", sample.PresentRecordMs, "含 ImGui 绘制列表");
        Row("    队列提交", sample.PresentSubmitMs, string.Empty);
        Row("    呈现", sample.PresentQueueMs, "不等待下一帧垂直同步");
        Row("  未归类", Math.Max(0F, sample.CallbackMs - sample.UpdateMs - sample.UiMs -
            sample.ImGuiRenderMs - sample.PresentMs), "回调内的其他固定开销");
        Row("GPU 场景计算", sample.GpuValid ? sample.GpuMs : -1F,
            sample.GpuValid
                ? "Vulkan 时间戳，异步回读"
                : recentGpu > 0D
                    ? $"本帧尚未回读，最近完成 {recentGpu:0.00} ms"
                    : "不可用或尚未回读");
        Row("  路径采样", sample.GpuValid ? sample.GpuSamplingMs : -1F,
            sample.GpuValid ? "求交、弹射与直接光，不含收尾" : "不可用或尚未回读");
        Row("  收尾通道", sample.GpuValid ? sample.GpuFinishMs : -1F,
            sample.GpuValid ? "降噪、Bloom、描边与 FXAA" : "不可用或尚未回读");

        ImGui.EndTable();

        var scene = _scenePipeline;
        var render = _pipeline;
        if (scene is not null || render is not null)
        {
            ImGui.TextDisabled("最近一次工作集同步（不一定是本帧）：");
            if (scene is not null)
                ImGui.Text($"场景  构建 {scene.SyncWorksetBuildMs:0.00}  BVH {scene.SyncBvhBuildMs:0.00}  " +
                    $"打包 {scene.SyncPackMs:0.00}  上传 {scene.SyncUploadMs:0.00} ms");
            if (render is not null)
                ImGui.Text($"渲染  构建 {render.SyncWorksetBuildMs:0.00}  BVH {render.SyncBvhBuildMs:0.00}  " +
                    $"打包 {render.SyncPackMs:0.00}  上传 {render.SyncUploadMs:0.00} ms");
        }
    }

    /// <summary>
    /// 统计当前帧窗口的均值、峰值与 P95，帮助判断偶发卡顿
    /// </summary>
    private void DrawAnalysisStatistics()
    {
        var count = _analysisFrameCount;
        if (count == 0) return;
        var intervalSum = 0F;
        var callbackSum = 0F;
        var callbackMax = 0F;
        var gpuSum = 0D;
        var gpuMax = 0F;
        var gpuSamplingSum = 0D;
        var gpuFinishSum = 0D;
        var gpuCount = 0;
        var slowFrames = 0;
        var buffer = _analysisStatBuffer;
        var offset = count == AnalysisFrameCapacity ? _analysisFrameIndex : 0;
        for (var index = 0; index < count; index++)
        {
            var sample = _analysisFrames[(offset + index) % AnalysisFrameCapacity];
            intervalSum += Math.Max(0F, sample.IntervalMs);
            callbackSum += Math.Max(0F, sample.CallbackMs);
            callbackMax = Math.Max(callbackMax, sample.CallbackMs);
            if (sample.IntervalMs > 33.34F) slowFrames++;
            if (sample.GpuValid)
            {
                gpuSum += sample.GpuMs;
                gpuMax = Math.Max(gpuMax, sample.GpuMs);
                gpuSamplingSum += sample.GpuSamplingMs;
                gpuFinishSum += sample.GpuFinishMs;
                gpuCount++;
            }

            buffer[index] = sample.CallbackMs;
        }

        Array.Sort(buffer, 0, count);
        var p95Index = Math.Clamp((int)MathF.Ceiling(count * 0.95F) - 1, 0, count - 1);
        var averageInterval = intervalSum / count;
        var latest = LatestAnalysisFrame;
        ImGui.SeparatorText("统计");
        ImGui.Text($"样本 {count} 帧  ·  覆盖 {intervalSum / 1000F:0.0} s  ·  平均 {(averageInterval > 0F ? 1000F / averageInterval : 0F):0.0} FPS");
        ImGui.Text($"界面回调  平均 {callbackSum / count:0.00} ms  ·  P95 {buffer[p95Index]:0.00} ms  ·  峰值 {callbackMax:0.00} ms");
        ImGui.Text(gpuCount > 0
            ? $"GPU 计算  平均 {gpuSum / gpuCount:0.00} ms  ·  峰值 {gpuMax:0.00} ms  ·  {gpuCount}/{count} 帧有效"
            : "GPU 计算  暂无有效时间戳");
        if (gpuCount > 0)
        {
            ImGui.Text($"  ├ 采样  平均 {gpuSamplingSum / gpuCount:0.00} ms  ·  收尾  平均 {gpuFinishSum / gpuCount:0.00} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("采样段是路径求交、弹射与直接光，收尾段是降噪、Bloom、描边与 FXAA。" +
                    Environment.NewLine + "两者之间的间隔是队列提交与调度开销，合计不等于简单相加。");
        }
        ImGui.TextDisabled($"超过 33.3 ms 的帧 {slowFrames} 帧  ·  最近一帧 {latest.CallbackMs:0.00} ms");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("P95 表示 95% 的采样帧不超过该值，比平均值更容易暴露偶发卡顿。" +
                Environment.NewLine + "GPU 统计只包含成功回读时间戳的帧，异步回读期间会显示较少样本。");
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
        ImGui.Text($"{name}：工作集 {pipeline.SyncTotalCells:N0}  派发 {pipeline.LastDispatchCount:N0}  " +
            $"提交 {pipeline.SubmitCount:N0}  栅栏等待 {pipeline.FenceWaitTotalMs:0.00} ms");
        ImGui.Text($"构建 {pipeline.SyncWorksetBuildMs:0.00}  BVH {pipeline.SyncBvhBuildMs:0.00}  " +
            $"打包 {pipeline.SyncPackMs:0.00}  上传 {pipeline.SyncUploadMs:0.00} ms");
        ImGui.Text($"发光面 {pipeline.EmissiveFaceCount:N0}  光源树节点 {pipeline.EmissiveTreeNodeCount:N0}  " +
            $"索引同步 {pipeline.EmissiveSyncMilliseconds:0.00} ms");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("最近一次 CPU 发光面提取、排序、光源树构建或更新、打包与上传耗时，不是 GPU 采样耗时。反向 PDF 使用二分面查询和相同的光源树分支概率。");
        ImGui.Text($"GPU 时间戳  {(pipeline.GpuTimingSupported ? "可用" : "不可用")}  " +
            $"最近完成 {pipeline.LastGpuFrameMs:0.00} ms（采样 {pipeline.LastGpuSamplingMs:0.00} + 收尾 {pipeline.LastGpuFinishMs:0.00}）");
        ImGui.Text($"体素/BVH 缓冲容量 {ToMiB(capacity.VoxelBytes + (double)capacity.BvhBytes):0.0} MiB");
    }
}
