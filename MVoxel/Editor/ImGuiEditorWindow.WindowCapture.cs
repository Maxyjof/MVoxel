using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private object CaptureEditorWindow(JsonElement request)
    {
        var args = request.GetProperty("arguments");
        var path = Path.GetFullPath(args.GetProperty("path").GetString() ?? throw new ArgumentException("截图路径不能为空"));
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
            throw new IOException("截图路径必须是不存在的 PNG 文件");
        _captureResults[path] = "queued";
        _pendingActions.Enqueue(() =>
        {
            if (_vulkan is null) { _captureResults[path] = "Vulkan 窗口不可用"; return; }
            _vulkan.QueueWindowCapture(path, result => _captureResults[path] = result);
        });
        return new { queued = true, path, source = "Vulkan 编辑器画面，不包含系统标题栏" };
    }
}
