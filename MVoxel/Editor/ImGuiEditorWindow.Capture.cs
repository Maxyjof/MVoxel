using System.Collections.Concurrent;
using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private readonly ConcurrentDictionary<string, string> _captureResults = new();
    private string? _capturePath;
    private int _captureFrames;
    private MVoxel.Rendering.RenderSettings? _captureRenderSettings;
    private int _captureSamples;
    private int _captureWidth, _captureHeight;
    private long _captureDeadline;
    private readonly ConcurrentDictionary<string, object> _captureMetadata = new();

    private object QueueCaptureView(JsonElement request)
    {
        var arguments = request.GetProperty("arguments");
        var path = Path.GetFullPath(arguments.GetProperty("path").GetString() ?? throw new ArgumentException("截图路径不能为空"));
        var cameraName = arguments.TryGetProperty("cameraName", out var cameraValue) ? cameraValue.GetString() : null;
        var width = arguments.TryGetProperty("width", out var widthValue) ? widthValue.GetInt32() : 0;
        var height = arguments.TryGetProperty("height", out var heightValue) ? heightValue.GetInt32() : 0;
        if ((width != 0 || height != 0) && (width is < 16 or > 8192 || height is < 16 or > 8192))
            throw new ArgumentException("截图宽高必须同时指定，且在16–8192之间");
        var samples = arguments.TryGetProperty("samples", out var sampleValue) ? sampleValue.GetInt32() : 128;
        if (samples is < 1 or > 16384) throw new ArgumentException("截图样本数必须在 1–16384 之间");
        var timeout = arguments.TryGetProperty("timeoutSeconds", out var timeoutValue) ? timeoutValue.GetInt32() : 300;
        if (timeout is < 1 or > 600) throw new ArgumentException("截图超时必须在 1–600 秒之间");
        bool? direct = arguments.TryGetProperty("directSampling", out var directValue) ? directValue.GetBoolean() : null;
        bool? tree = arguments.TryGetProperty("lightTree", out var treeValue) ? treeValue.GetBoolean() : null;
        bool? denoise = arguments.TryGetProperty("denoise", out var denoiseValue) ? denoiseValue.GetBoolean() : null;
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
            // 截图必须使用真正参与渲染的有效设置：项目配置与场景环境合并后的结果，
            // 直接用 _renderSettings 会漏掉场景环境对天空、雾、地面等字段的覆盖
            _captureRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject()).CloneForExport();
            _captureRenderSettings.PathTracingEnabled = true;
            _captureRenderSettings.PathSamplesPerFrame = 1;
            _captureRenderSettings.PathMaxAccumulatedSamples = Math.Max(16, samples);
            if (direct.HasValue) _captureRenderSettings.EmissiveDirectSampling = direct.Value;
            if (tree.HasValue) _captureRenderSettings.EmissiveLightTree = tree.Value;
            if (denoise.HasValue) _captureRenderSettings.PathDenoising = denoise.Value;
            _captureSamples = samples;
            _captureWidth = width; _captureHeight = height;
            _captureDeadline = Environment.TickCount64 + timeout * 1000L;
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            _captureFrames = 0;
            _requestedCenterTab = 1;
        });
        return new { queued = true, path };
    }

    private object CaptureStatus(JsonElement request)
    {
        var path = Path.GetFullPath(request.GetProperty("arguments").GetProperty("path").GetString() ?? "");
        return new { path, status = _captureResults.GetValueOrDefault(path, "unknown"),
            metadata = _captureMetadata.GetValueOrDefault(path) };
    }

    private void ProcessCaptureView()
    {
        if (_capturePath is not { } path) return;
        if (++_captureFrames < 2) return;
        if (Environment.TickCount64 >= _captureDeadline)
        {
            _captureResults[path] = "截图等待超时";
            FinishCapture();
            return;
        }
        if (_centerTab != 1 || _pipeline?.CanSaveDisplayPng != true)
        {
            return;
        }
        if (_pipeline.AccumulatedSamples < _captureSamples) return;
        try
        {
            if (File.Exists(path)) throw new IOException("截图文件已存在");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var saved = _pipeline.SaveDisplayPng(path);
            _captureMetadata[path] = new { samples = _pipeline.AccumulatedSamples,
                width = _pipeline.OutputWidth, height = _pipeline.OutputHeight,
                directSampling = _captureRenderSettings!.EmissiveDirectSampling,
                lightTree = _captureRenderSettings.EmissiveLightTree,
                denoise = _captureRenderSettings.PathDenoising,
                exposure = _captureRenderSettings.Exposure,
                skyAmbient = _captureRenderSettings.SkyAmbient,
                emissiveIntensity = _captureRenderSettings.EmissiveIntensity,
                sunEnabled = _captureRenderSettings.SunEnabled,
                emissiveFaces = _pipeline.EmissiveFaceCount,
                lightTreeNodes = _pipeline.EmissiveTreeNodeCount,
                indexSyncCpuMs = _pipeline.EmissiveSyncMilliseconds };
            _captureResults[path] = saved ? "completed" : "截图保存失败";
        }
        catch (Exception exception) { _captureResults[path] = exception.Message; }
        FinishCapture();
    }

    private void FinishCapture()
    {
        _capturePath = null;
        _captureRenderSettings = null;
        _accumulationSignature = string.Empty;
    }
}
