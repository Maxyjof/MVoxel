using System.Collections.Concurrent;
using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private readonly ConcurrentDictionary<string, string> _captureResults = new();
    private string? _capturePath;
    private int _captureFrames;

    private object QueueCaptureView(JsonElement request)
    {
        var arguments = request.GetProperty("arguments");
        var path = Path.GetFullPath(arguments.GetProperty("path").GetString() ?? throw new ArgumentException("截图路径不能为空"));
        var cameraName = arguments.TryGetProperty("cameraName", out var cameraValue) ? cameraValue.GetString() : null;
        OpenTK.Mathematics.Vector3? position = null, target = null;
        if (arguments.TryGetProperty("position", out var positionValue) && arguments.TryGetProperty("target", out var targetValue))
        {
            static OpenTK.Mathematics.Vector3 ReadVector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
            position = ReadVector(positionValue);
            target = ReadVector(targetValue);
            if (!float.IsFinite(position.Value.X) || !float.IsFinite(position.Value.Y) || !float.IsFinite(position.Value.Z) ||
                !float.IsFinite(target.Value.X) || !float.IsFinite(target.Value.Y) || !float.IsFinite(target.Value.Z) ||
                (target.Value - position.Value).LengthSquared < .0001F) throw new ArgumentException("相机位置或目标无效");
            if (string.IsNullOrWhiteSpace(cameraName)) throw new ArgumentException("调整相机需要指定名称");
        }
        else if (arguments.TryGetProperty("position", out _) || arguments.TryGetProperty("target", out _))
            throw new ArgumentException("相机位置和目标必须同时提供");
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("截图路径必须以.png结尾");
        if (File.Exists(path)) throw new IOException("截图文件已存在");
        _captureResults[path] = "queued";
        _pendingActions.Enqueue(() =>
        {
            if (_capturePath is not null || _project is null)
            {
                _captureResults[path] = _project is null ? "没有打开项目" : "已有截图任务";
                return;
            }
            if (!string.IsNullOrWhiteSpace(cameraName))
            {
                var cameras = GetRenderCameraObjects().Where(camera => camera.Name == cameraName).ToArray();
                if (cameras.Length != 1)
                {
                    _captureResults[path] = cameras.Length == 0 ? "未找到指定相机" : "相机名称不唯一";
                    return;
                }
                _renderCameraId = cameras[0].Id;
                if (position.HasValue && target.HasValue)
                    WriteCameraTransform(cameras[0], position.Value, target.Value - position.Value, OpenTK.Mathematics.Vector3.UnitY);
                _renderOrbitAnchor = null;
                _renderCameraInitialFramePending = true;
                _pipeline?.ResetAccumulation();
                _accumulationSignature = string.Empty;
            }
            _capturePath = path;
            _captureFrames = 0;
            _requestedCenterTab = 1;
        });
        return new { queued = true, path };
    }

    private object CaptureStatus(JsonElement request)
    {
        var path = Path.GetFullPath(request.GetProperty("arguments").GetProperty("path").GetString() ?? "");
        return new { path, status = _captureResults.GetValueOrDefault(path, "unknown") };
    }

    private void ProcessCaptureView()
    {
        if (_capturePath is not { } path) return;
        if (++_captureFrames < 60) return;
        if (_centerTab != 1 || _pipeline?.CanSaveDisplayPng != true)
        {
            if (_captureFrames < 600) return;
            _captureResults[path] = "渲染视图不可用";
            _capturePath = null;
            return;
        }
        try
        {
            if (File.Exists(path)) throw new IOException("截图文件已存在");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _captureResults[path] = _pipeline.SaveDisplayPng(path) ? "completed" : "截图保存失败";
        }
        catch (Exception exception) { _captureResults[path] = exception.Message; }
        _capturePath = null;
    }
}
