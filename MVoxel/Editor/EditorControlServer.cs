using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>
/// 为本机 CLI 提供受控的编辑器命令通道
/// </summary>
internal sealed class EditorControlServer : IDisposable
{
    private readonly string _pipeName = $"MVoxel.Editor.{Environment.ProcessId}";
    private readonly Func<JsonElement, object?> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _worker;

    /// <summary>
    /// 创建编辑器命令服务
    /// </summary>
    /// <param name="handler">主线程安全的命令处理器</param>
    public EditorControlServer(Func<JsonElement, object?> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// 启动本机命名管道服务
    /// </summary>
    public void Start()
    {
        AppPaths.EnsureDirectories();
        AtomicFile.WriteText(AppPaths.EditorEndpointPath, _pipeName);
        _worker = Task.Run(RunAsync);
        MVoxelLog.Info($"编辑器 CLI 通道已启动：{_pipeName}");
    }

    /// <summary>
    /// 循环接收并处理本机编辑器命令
    /// </summary>
    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cancellation.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                try
                {
                    var line = await reader.ReadLineAsync(_cancellation.Token);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var result = _handler(document.RootElement);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, result }));
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    MVoxelLog.Warn($"编辑器 CLI 命令失败：{exception.Message}");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = exception.Message }));
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                MVoxelLog.Warn($"编辑器 CLI 命令处理失败：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// 停止本机命名管道服务
    /// </summary>
    public void Dispose()
    {
        _cancellation.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        _cancellation.Dispose();
        try
        {
            if (File.Exists(AppPaths.EditorEndpointPath) &&
                File.ReadAllText(AppPaths.EditorEndpointPath).Trim() == _pipeName)
                File.Delete(AppPaths.EditorEndpointPath);
        }
        catch (IOException exception)
        {
            MVoxelLog.Warn($"清理编辑器控制端点失败：{exception.Message}");
        }
    }
}
