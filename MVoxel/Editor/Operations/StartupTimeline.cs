using System.Diagnostics;
using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>
/// 启动、项目准备与首帧渲染的分阶段计时账本
/// </summary>
/// <remarks>
/// 只回答三个时间点：窗口何时可响应、项目何时可操作、首张完整画面何时出现，
/// 以及这几个时间点之前每一段工作各自花了多久
/// 同名阶段可以出现多次，耗时会累加但保留首次开始时刻；后台线程上报同样安全。
/// 记录本身只是一次加锁写入，不参与项目、渲染或界面逻辑的任何判定
/// </remarks>
internal static class StartupTimeline
{
    /// <summary>窗口完成初始化并可以显示欢迎界面的里程碑</summary>
    internal const string MilestoneWindowReady = "窗口可响应";

    /// <summary>项目状态已经交给界面、进度弹窗可以消失的里程碑</summary>
    internal const string MilestoneProjectReady = "项目可操作";

    /// <summary>预览视图画出首帧的里程碑</summary>
    internal const string MilestonePreviewFrame = "预览首帧";

    /// <summary>渲染视图画出首帧的里程碑</summary>
    internal const string MilestoneRenderFrame = "渲染首帧";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly List<PhaseRecord> Records = [];
    private static readonly List<MilestoneRecord> Milestones = [];
    private static bool _reportWritten;

    /// <summary>
    /// 从编辑器启动到现在的毫秒数
    /// </summary>
    internal static double ElapsedMs
    {
        get
        {
            lock (Gate)
            {
                return Clock.Elapsed.TotalMilliseconds;
            }
        }
    }

    /// <summary>
    /// 开始一段计时，返回的作用域释放时把耗时写入账本
    /// </summary>
    /// <param name="name">阶段名称，同一名称的耗时会累加</param>
    /// <returns>用于 using 的计时作用域</returns>
    internal static Scope Begin(string name)
    {
        double startMs;
        lock (Gate)
        {
            startMs = Clock.Elapsed.TotalMilliseconds;
        }

        return new Scope(name, startMs);
    }

    /// <summary>
    /// 记录一个里程碑的绝对时刻，同名里程碑只保留第一次
    /// </summary>
    /// <param name="name">里程碑名称</param>
    internal static void Mark(string name)
    {
        var report = false;
        lock (Gate)
        {
            if (Milestones.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal))) return;
            Milestones.Add(new MilestoneRecord(name, Clock.Elapsed.TotalMilliseconds));
            // 首个画面出现时把整份账本落进日志，便于事后对比不同版本
            var hasFrame = Milestones.Any(item =>
                item.Name is MilestonePreviewFrame or MilestoneRenderFrame);
            report = hasFrame && !_reportWritten;
            if (report) _reportWritten = true;
        }

        if (report) WriteReport();
    }

    /// <summary>
    /// 供命令行与自动化读取的结构化快照
    /// </summary>
    /// <returns>里程碑与已记录阶段</returns>
    internal static object Snapshot()
    {
        lock (Gate)
        {
            return new
            {
                elapsedMs = Clock.Elapsed.TotalMilliseconds,
                milestones = Milestones
                    .OrderBy(item => item.AtMs)
                    .Select(item => new { name = item.Name, atMs = item.AtMs })
                    .ToArray(),
                phases = Records
                    .OrderBy(item => item.StartMs)
                    .Select(item => new
                    {
                        name = item.Name,
                        startMs = item.StartMs,
                        endMs = item.StartMs + item.DurationMs,
                        durationMs = item.DurationMs,
                        count = item.Count,
                        threadId = item.ThreadId,
                    })
                    .ToArray(),
            };
        }
    }

    /// <summary>
    /// 清空账本并重置计时起点，用于在同一进程内重新量一次打开项目
    /// </summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Records.Clear();
            Milestones.Clear();
            _reportWritten = false;
            Clock.Restart();
        }
    }

    private static void Add(string name, double startMs)
    {
        double endMs;
        lock (Gate)
        {
            endMs = Clock.Elapsed.TotalMilliseconds;
            var index = Records.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (index >= 0)
            {
                // 同名阶段只累加耗时，保留首次开始时刻，避免后台重复上报把时间线打乱
                var existing = Records[index];
                Records[index] = existing with
                {
                    DurationMs = existing.DurationMs + Math.Max(0, endMs - startMs),
                    Count = existing.Count + 1,
                };
            }
            else
            {
                Records.Add(new PhaseRecord(name, startMs, Math.Max(0, endMs - startMs),
                    Environment.CurrentManagedThreadId, 1));
            }
        }
    }

    /// <summary>
    /// 把账本写进运行日志，里程碑按时间排序，阶段按开始时刻排序
    /// </summary>
    private static void WriteReport()
    {
        List<MilestoneRecord> milestones;
        List<PhaseRecord> phases;
        lock (Gate)
        {
            milestones = [.. Milestones.OrderBy(item => item.AtMs)];
            phases = [.. Records.OrderBy(item => item.StartMs)];
        }

        foreach (var milestone in milestones)
        {
            MVoxelLog.Info($"[启动耗时] {milestone.Name}：{milestone.AtMs:0} ms");
        }

        foreach (var phase in phases)
        {
            var repeat = phase.Count > 1 ? $" ×{phase.Count}" : string.Empty;
            MVoxelLog.Info(
                $"[启动耗时] {phase.Name}{repeat}：{phase.StartMs:0}→{phase.StartMs + phase.DurationMs:0} ms，耗时 {phase.DurationMs:0} ms，线程 {phase.ThreadId}");
        }
    }

    /// <summary>
    /// 一段计时，释放时写入账本
    /// </summary>
    internal struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly double _startMs;
        private bool _disposed;

        internal Scope(string name, double startMs)
        {
            _name = name;
            _startMs = startMs;
            _disposed = false;
        }

        /// <summary>
        /// 结束计时并记录耗时，重复释放不会重复计入
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Add(_name, _startMs);
        }
    }

    private readonly record struct PhaseRecord(string Name, double StartMs, double DurationMs, int ThreadId,
        int Count);

    private readonly record struct MilestoneRecord(string Name, double AtMs);
}
