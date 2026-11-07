using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    /// <summary>截图前等待显卡画面恢复的最长时间</summary>
    private const long WindowCaptureGpuWaitMs = 8000;

    private object CaptureEditorWindow(JsonElement request)
    {
        var args = request.GetProperty("arguments");
        var path = Path.GetFullPath(args.GetProperty("path").GetString() ?? throw new ArgumentException("截图路径不能为空"));
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
            throw new IOException("截图路径必须是不存在的 PNG 文件");
        _captureResults[path] = "queued";
        var requestedAtMs = Environment.TickCount64;

        // 显卡失败后的 CPU 回退预览没有深度缓冲，斜坡面片按画家算法排序，
        // 画面看起来像半透明房子。自动化截图必须等到显卡画面重新可用，
        // 宁可不产出截图，也不要把回退画面写进 PNG。
        void TryCapture()
        {
            if (_vulkan is null)
            {
                _captureResults[path] = "Vulkan 窗口不可用";
                return;
            }

            var now = Environment.TickCount64;
            var gpuFrameAtMs = _centerTab == 1 ? _lastRenderGpuSubmitMs : _sceneGpuRenderedAtMs;
            var gpuHealthy = gpuFrameAtMs > 0 && now - gpuFrameAtMs <= 500;
            if (!gpuHealthy)
            {
                if (now - requestedAtMs >= WindowCaptureGpuWaitMs)
                {
                    _captureResults[path] = "显卡预览不可用，已跳过截图（避免截到 CPU 回退的半透明画面）";
                    return;
                }

                _pendingActions.Enqueue(TryCapture);
                return;
            }

            _vulkan.QueueWindowCapture(path, result => _captureResults[path] = result);
        }

        _pendingActions.Enqueue(TryCapture);
        return new { queued = true, path, source = "Vulkan 编辑器画面，不包含系统标题栏" };
    }
}
