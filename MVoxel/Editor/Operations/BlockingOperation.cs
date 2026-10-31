using System.Drawing;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 用户可见的编辑器阻塞操作的状态机。工作在线程池执行，
/// 完成回调刻意由 UI 线程 Pump 消费，保证项目状态与 ImGui
/// 永远不跨线程访问
/// </summary>
internal sealed class BlockingOperationRunner : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task<OperationResult>? _task;
    private BlockingOperationSnapshot? _snapshot;
    private Action<object?>? _onSucceeded;
    private Action<Exception>? _onFailed;
    private Action? _onCancelled;
    private int _generation;

    /// <summary>
    /// 是否有一个操作正在执行
    /// </summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate) return _task is not null;
        }
    }

    /// <summary>
    /// 当前操作的只读快照，空闲时为 null
    /// </summary>
    internal BlockingOperationSnapshot? Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    /// <summary>
    /// 启动一个后台操作，已有操作运行时返回 false
    /// 不把取消令牌交给 Task.Run：调度与实际执行之间被取消也必须产生
    /// 正常结果，让 UI 能确定性地消费并清理
    /// </summary>
    /// <param name="title">弹窗标题</param>
    /// <param name="initialDetail">初始进度描述</param>
    /// <param name="work">后台工作，可上报进度并响应取消</param>
    /// <param name="succeeded">成功回调（UI 线程执行）</param>
    /// <param name="failed">失败回调（UI 线程执行）</param>
    /// <param name="cancelled">取消回调（UI 线程执行）</param>
    /// <typeparam name="T">工作结果类型</typeparam>
    /// <returns>是否成功启动</returns>
    internal bool TryStart<T>(string title, string initialDetail,
        Func<CancellationToken, Action<BlockingOperationProgress>, T> work,
        Action<T> succeeded, Action<Exception> failed, Action? cancelled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialDetail);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(succeeded);
        ArgumentNullException.ThrowIfNull(failed);

        CancellationTokenSource cancellation;
        int generation;
        lock (_gate)
        {
            if (_task is not null) return false;
            cancellation = new CancellationTokenSource();
            generation = ++_generation;
            _cancellation = cancellation;
            _snapshot = new BlockingOperationSnapshot(title, initialDetail, .04F, CanCancel: true, CancelRequested: false);
            _onSucceeded = value => succeeded((T)value!);
            _onFailed = failed;
            _onCancelled = cancelled;
            _task = Task.Run(() => Execute(work, cancellation.Token,
                progress => Report(generation, title, progress)), CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// 请求取消当前操作，仅在阶段允许取消时生效，且只生效一次
    /// </summary>
    /// <returns>是否接受了取消请求</returns>
    internal bool RequestCancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_snapshot is not { CanCancel: true } snapshot || _cancellation is null) return false;
            _snapshot = snapshot with
            {
                Detail = "正在取消本次操作…",
                CanCancel = false,
                CancelRequested = true,
            };
            cancellation = _cancellation;
        }

        cancellation.Cancel();
        return true;
    }

    /// <summary>
    /// 由 UI 线程每帧调用，任务完成时执行对应回调并清理状态
    /// </summary>
    internal void Pump()
    {
        Task<OperationResult>? task;
        CancellationTokenSource? cancellation;
        Action<object?>? succeeded;
        Action<Exception>? failed;
        Action? cancelled;
        lock (_gate)
        {
            if (_task is not { IsCompleted: true }) return;
            task = _task;
            cancellation = _cancellation;
            succeeded = _onSucceeded;
            failed = _onFailed;
            cancelled = _onCancelled;
        }

        var result = task.GetAwaiter().GetResult();
        try
        {
            // 保持快照直到 UI 完成回调结束。项目打开的最后阶段会在此应用场景、
            // 体素数据和材质；若提前清空，进度弹窗会在 UI 短暂停顿前消失。
            if (result.Cancelled)
            {
                cancelled?.Invoke();
                return;
            }
            if (result.Error is not null)
            {
                failed?.Invoke(result.Error);
                return;
            }

            succeeded?.Invoke(result.Value);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_task, task))
                {
                    _task = null;
                    _cancellation = null;
                    _snapshot = null;
                    _onSucceeded = null;
                    _onFailed = null;
                    _onCancelled = null;
                }
            }

            cancellation?.Dispose();
        }
    }

    /// <summary>
    /// 放弃当前操作的所有回调并请求取消后台工作
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ++_generation;
            cancellation = _cancellation;
            _task = null;
            _cancellation = null;
            _snapshot = null;
            _onSucceeded = null;
            _onFailed = null;
            _onCancelled = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void Report(int generation, string title, BlockingOperationProgress progress)
    {
        lock (_gate)
        {
            if (generation != _generation || _cancellation is null) return;
            var cancelRequested = _snapshot?.CancelRequested ?? false;
            _snapshot = new BlockingOperationSnapshot(
                title,
                progress.Detail,
                Math.Clamp(progress.Fraction, 0F, 1F),
                progress.CanCancel && !cancelRequested,
                CancelRequested: cancelRequested);
        }
    }

    private static OperationResult Execute<T>(Func<CancellationToken, Action<BlockingOperationProgress>, T> work,
        CancellationToken cancellation, Action<BlockingOperationProgress> report)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var value = work(cancellation, report);
            cancellation.ThrowIfCancellationRequested();
            return new OperationResult(value, null, Cancelled: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new OperationResult(null, null, Cancelled: true);
        }
        catch (Exception exception)
        {
            return new OperationResult(null, exception, Cancelled: false);
        }
    }

    private readonly record struct OperationResult(object? Value, Exception? Error, bool Cancelled);
}

/// <summary>
/// 后台工作上报的一步进度
/// </summary>
/// <param name="Detail">界面显示描述</param>
/// <param name="Fraction">完成比例 0 到 1</param>
/// <param name="CanCancel">该阶段是否还允许取消</param>
internal readonly record struct BlockingOperationProgress(string Detail, float Fraction, bool CanCancel);

/// <summary>
/// UI 线程每帧读取的操作快照
/// </summary>
/// <param name="Title">弹窗标题</param>
/// <param name="Detail">当前描述</param>
/// <param name="Fraction">完成比例</param>
/// <param name="CanCancel">此刻是否可取消</param>
/// <param name="CancelRequested">取消是否已被请求</param>
internal readonly record struct BlockingOperationSnapshot(
    string Title,
    string Detail,
    float Fraction,
    bool CanCancel,
    bool CancelRequested);

/// <summary>
/// 应用级模态弹窗的共享 ImGui 呈现：阻塞操作的进度卡与
/// 纯提示的输入拦截卡使用同一套背景与布局
/// </summary>
internal static class BlockingOperationDialog
{
    /// <summary>
    /// 绘制可取消操作的进度弹窗
    /// </summary>
    /// <param name="snapshot">操作快照</param>
    /// <param name="requestCancel">点击取消时触发的动作</param>
    internal static void Draw(BlockingOperationSnapshot snapshot, Action requestCancel)
    {
        DrawBackdrop("##MVoxelOperationBackdrop", snapshot.Title, snapshot.Detail, snapshot.Fraction, snapshot.CanCancel,
            snapshot.CancelRequested, requestCancel, isInformational: false);
    }

    /// <summary>
    /// 绘制纯提示的输入拦截弹窗（例如欢迎窗口未关闭时）
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="detail">说明</param>
    internal static void DrawInteractionBlocker(string title, string detail)
    {
        DrawBackdrop("##MVoxelWelcomeBlocker", title, detail, 0F, canCancel: false, cancelRequested: false,
            requestCancel: null, isInformational: true);
    }

    private static void DrawBackdrop(string id, string title, string detail, float progress, bool canCancel,
        bool cancelRequested, Action? requestCancel, bool isInformational)
    {
        var display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0F || display.Y <= 0F) return;

        var cardSize = new Vector2(Math.Min(468F, Math.Max(300F, display.X - 48F)), isInformational ? 142F : 204F);
        ImGui.OpenPopup(id);
        ImGui.SetNextWindowPos(display / 2F, ImGuiCond.Always, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(cardSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28F, 22F));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
        if (CenteredModal.Begin(id, flags))
        {
            var draw = ImGui.GetWindowDrawList();
            var window = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            draw.AddLine(window + new Vector2(3F, 9F), window + new Vector2(3F, windowSize.Y - 9F),
                ToPacked(EditorPalette.Accent), 3F);
            ImGui.TextColored(ToColor(EditorPalette.Text), title);
            ImGui.Dummy(new Vector2(0F, 4F));
            ImGui.TextWrapped(detail);
            if (!isInformational)
            {
                ImGui.Dummy(new Vector2(0F, 8F));
                ImGui.ProgressBar(Math.Clamp(progress, 0F, 1F), new Vector2(-1F, 18F), $"{progress * 100F:0}%");
                ImGui.Dummy(new Vector2(0F, 8F));
                if (canCancel && ImGui.Button("取消本次操作", new Vector2(126F, 32F))) requestCancel?.Invoke();
                ImGui.SameLine();
                ImGui.TextDisabled(cancelRequested ? "正在取消…" : canCancel ? "可继续等待，或取消。" : "当前步骤将安全完成。");
            }

            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
    private static uint ToPacked(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
