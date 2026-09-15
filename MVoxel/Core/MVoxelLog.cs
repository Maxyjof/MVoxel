using System.Text;

namespace MVoxel.Core;

/// <summary>
/// MVoxel 本地文件日志
/// </summary>
public static class MVoxelLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static string _runtimeContext = "未设置运行上下文";

    /// <summary>
    /// 上次异常退出的摘要
    /// </summary>
    public static PreviousCrashNotice? PreviousCrash { get; private set; }

    /// <summary>
    /// 当前日志文件路径
    /// </summary>
    public static string? CurrentLogPath { get; private set; }

    /// <summary>
    /// 初始化日志文件与全局异常记录
    /// </summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                AppPaths.EnsureDirectories();
                PreviousCrash = LoadPreviousCrash();
                CurrentLogPath = Path.Combine(AppPaths.LogDirectory, $"MVoxel-{DateTime.Now:yyyyMMdd}.log");
                _writer = new StreamWriter(CurrentLogPath, append: true, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                WriteCore("信息", "日志系统已启动");
            }
            catch
            {
                _writer = null;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }
    }

    /// <summary>
    /// 记录普通运行信息
    /// </summary>
    /// <param name="message">日志内容</param>
    public static void Info(string message) => WriteCore("信息", message);

    /// <summary>
    /// 记录警告信息
    /// </summary>
    /// <param name="message">日志内容</param>
    public static void Warn(string message) => WriteCore("警告", message);

    /// <summary>
    /// 记录错误和异常堆栈
    /// </summary>
    /// <param name="message">错误说明</param>
    /// <param name="exception">可选异常</param>
    public static void Error(string message, Exception? exception = null)
    {
        WriteCore("错误", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    /// <summary>
    /// 更新当前运行上下文供异常报告使用
    /// </summary>
    /// <param name="context">当前操作上下文</param>
    public static void SetRuntimeContext(string context)
    {
        lock (Gate)
        {
            _runtimeContext = string.IsNullOrWhiteSpace(context) ? "未设置运行上下文" : context.Replace("\0", string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 写入崩溃报告并创建下次启动提示
    /// </summary>
    /// <param name="message">异常阶段说明</param>
    /// <param name="exception">异常对象</param>
    public static void RecordCrash(string message, Exception? exception)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            var fileName = $"MVoxel-Crash-{now:yyyyMMdd-HHmmss-fff}.log";
            var reportPath = Path.Combine(AppPaths.LogDirectory, fileName);
            var report = new StringBuilder()
                .AppendLine("MVoxel 崩溃报告")
                .AppendLine($"时间：{now:yyyy-MM-dd HH:mm:ss.fff zzz}")
                .AppendLine($"程序：{Environment.ProcessPath ?? "未知"}")
                .AppendLine($"程序目录：{AppPaths.BaseDirectory}")
                .AppendLine($"运行时：{Environment.Version}")
                .AppendLine($"系统：{Environment.OSVersion}")
                .AppendLine($"线程：{Environment.CurrentManagedThreadId}")
                .AppendLine($"上下文：{_runtimeContext}")
                .AppendLine($"阶段：{message}")
                .AppendLine("异常堆栈：")
                .AppendLine(exception?.ToString() ?? "无异常对象");

            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                AtomicFile.WriteText(reportPath, report.ToString(), backupExtension: null);
                var noticePath = Path.Combine(AppPaths.LogDirectory, "last-crash.notice");
                var notice = $"{now:O}{Environment.NewLine}{reportPath}{Environment.NewLine}{message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}";
                AtomicFile.WriteText(noticePath, notice, backupExtension: null);
                PreviousCrash = new PreviousCrashNotice(message, reportPath, now);
            }
            catch
            {
            }

            WriteCore("崩溃", $"{message}{Environment.NewLine}{exception}");
        }
    }

    /// <summary>
    /// 清除欢迎界面已经显示过的崩溃提示
    /// </summary>
    public static void AcknowledgePreviousCrash()
    {
        lock (Gate)
        {
            try
            {
                File.Delete(Path.Combine(AppPaths.LogDirectory, "last-crash.notice"));
            }
            catch
            {
            }

            PreviousCrash = null;
        }
    }

    /// <summary>
    /// 关闭日志文件并解除全局事件
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            try
            {
                WriteCore("信息", "日志系统已关闭");
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;
        }
    }

    private static void WriteCore(string level, string message)
    {
        lock (Gate)
        {
            if (_writer is null) return;
            try
            {
                var safeMessage = message.Replace("\0", string.Empty, StringComparison.Ordinal);
                _writer.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [线程 {Environment.CurrentManagedThreadId}] {safeMessage}");
            }
            catch
            {
            }
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        RecordCrash("未处理的应用异常", args.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Error("未观察到的任务异常", args.Exception);
        args.SetObserved();
    }

    /// <summary>
    /// 读取上次异常退出留下的启动提示
    /// </summary>
    /// <returns>崩溃提示或 null</returns>
    private static PreviousCrashNotice? LoadPreviousCrash()
    {
        var noticePath = Path.Combine(AppPaths.LogDirectory, "last-crash.notice");
        try
        {
            var lines = File.ReadAllLines(noticePath, Encoding.UTF8);
            if (lines.Length < 3 || !DateTimeOffset.TryParse(lines[0], out var occurredAt))
            {
                return null;
            }

            var reportPath = lines[1];
            var summary = string.Join(" ", lines.Skip(2)).Trim();
            return string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(reportPath)
                ? null
                : new PreviousCrashNotice(summary, reportPath, occurredAt);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 欢迎界面显示的上次崩溃信息
/// </summary>
public sealed record PreviousCrashNotice(string Summary, string ReportPath, DateTimeOffset OccurredAt);
