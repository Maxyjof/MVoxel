using System.Drawing;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// Thread-safe state for a user-visible editor operation.  Work happens off
/// the render thread; completion callbacks are deliberately pumped by the UI
/// thread so project state and ImGui never cross thread boundaries.
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

    internal bool IsRunning
    {
        get
        {
            lock (_gate) return _task is not null;
        }
    }

    internal BlockingOperationSnapshot? Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

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
            // Do not give Task.Run the cancellation token: a cancellation
            // between scheduling and invocation must still produce a normal
            // result that the UI can consume and clear deterministically.
            _task = Task.Run(() => Execute(work, cancellation.Token,
                progress => Report(generation, title, progress)), CancellationToken.None);
        }

        return true;
    }

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

    /// <summary>Runs callbacks for a completed task on the editor UI thread.</summary>
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
            _task = null;
            _cancellation = null;
            _snapshot = null;
            _onSucceeded = null;
            _onFailed = null;
            _onCancelled = null;
        }

        var result = task.GetAwaiter().GetResult();
        cancellation?.Dispose();
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

internal readonly record struct BlockingOperationProgress(string Detail, float Fraction, bool CanCancel);

internal readonly record struct BlockingOperationSnapshot(
    string Title,
    string Detail,
    float Fraction,
    bool CanCancel,
    bool CancelRequested);

/// <summary>Shared application-modal ImGui presentation for cancellable work.</summary>
internal static class BlockingOperationDialog
{
    internal static void Draw(BlockingOperationSnapshot snapshot, Action requestCancel)
    {
        DrawBackdrop("##MVoxelOperationBackdrop", snapshot.Title, snapshot.Detail, snapshot.Fraction, snapshot.CanCancel,
            snapshot.CancelRequested, requestCancel, isInformational: false);
    }

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
        if (ImGui.BeginPopupModal(id, flags))
        {
            var draw = ImGui.GetWindowDrawList();
            var window = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            draw.AddRectFilled(window, window + new Vector2(4F, windowSize.Y), ToPacked(EditorPalette.Accent), 7F);
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
