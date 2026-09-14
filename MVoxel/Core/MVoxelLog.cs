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
        Error("未处理的应用异常", args.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Error("未观察到的任务异常", args.Exception);
        args.SetObserved();
    }
}
