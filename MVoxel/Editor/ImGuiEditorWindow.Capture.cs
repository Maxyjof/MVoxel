using System.Collections.Concurrent;
using System.Text.Json;
using MVoxel.Animation;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private readonly ConcurrentDictionary<string, string> _captureResults = new();
    private string? _capturePath;
    private int _captureFrames;
    private bool _captureRendered;
    private float _captureWeatherTime;
    private float? _captureRain, _captureSnow, _captureHeat, _captureStorm, _captureLightning;
    private int? _captureWeatherSeed;
    private float? _captureWeatherViewDistance;
    private float? _captureWaterFiltering;
    private bool? _captureHardwareAcceleration;
    private MVoxel.Rendering.RenderSettings? _captureRenderSettings;
    private int _captureSamples;
    private int _captureWidth, _captureHeight;
    private long _captureDeadline;
    private readonly ConcurrentDictionary<string, object> _captureMetadata = new();
    /// <summary>截图期间临时使用的时间轴与帧，不写入用户正在编辑的时间轴窗口</summary>
    private TimelineAsset? _captureTimelineAsset;
    private int? _captureTimelineFrame;
    private string? _captureTimelineLabel;

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
        var pathTracing = !arguments.TryGetProperty("pathTracing", out var pathValue) || pathValue.GetBoolean();
        bool? hardwareAcceleration = arguments.TryGetProperty("hardwareAcceleration", out var hardwareValue)
            ? hardwareValue.GetBoolean() : null;
        float? ReadWeather(string key)
        {
            if (!arguments.TryGetProperty(key, out var value)) return null;
            var result = value.GetSingle();
            if (!float.IsFinite(result) || result < 0F || result > 1F) throw new ArgumentException($"{key} 必须在 0–1 之间");
            return result;
        }
        var rain = ReadWeather("rain");
        var snow = ReadWeather("snow");
        var heat = ReadWeather("heatHaze");
        var storm = ReadWeather("storm");
        float? lightning = arguments.TryGetProperty("lightning", out var lightningValue) ? lightningValue.GetSingle() : null;
        if (lightning.HasValue && (!float.IsFinite(lightning.Value) || lightning.Value < 0F || lightning.Value > 4F))
            throw new ArgumentException("lightning 必须在 0–4 之间");
        int? weatherSeed = arguments.TryGetProperty("weatherSeed", out var seedValue) ? seedValue.GetInt32() : null;
        if (weatherSeed is < 0 or > 65535) throw new ArgumentException("weatherSeed 必须在 0–65535 之间");
        float? weatherTime = arguments.TryGetProperty("weatherTime", out var timeValue) ? timeValue.GetSingle() : null;
        if (weatherTime.HasValue && (!float.IsFinite(weatherTime.Value) || weatherTime.Value < 0F || weatherTime.Value > 1000000F))
            throw new ArgumentException("weatherTime 必须在 0–1000000 秒之间");
        float? weatherViewDistance = arguments.TryGetProperty("weatherViewDistance", out var distanceValue) ? distanceValue.GetSingle() : null;
        if (weatherViewDistance.HasValue && (!float.IsFinite(weatherViewDistance.Value) || weatherViewDistance.Value < 1F || weatherViewDistance.Value > 256F))
            throw new ArgumentException("weatherViewDistance 必须在 1–256 之间");
        float? waterFiltering = arguments.TryGetProperty("waterFiltering", out var filterValue) ? filterValue.GetSingle() : null;
        if (waterFiltering.HasValue && (!float.IsFinite(waterFiltering.Value) || waterFiltering.Value < 0F || waterFiltering.Value > 2F))
            throw new ArgumentException("waterFiltering 必须在 0–2 之间");
        MVoxel.Rendering.SunShadowMethod? sunMethod = null;
        if (arguments.TryGetProperty("sunShadowMethod", out var sunValue))
        {
            if (!Enum.TryParse<MVoxel.Rendering.SunShadowMethod>(sunValue.GetString(), true, out var parsed) ||
                !Enum.IsDefined(parsed)) throw new ArgumentException("太阳阴影方案无效");
            sunMethod = parsed;
        }
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
        //时间轴采样：给了资产就按该资产取帧，只给帧则冻结窗口里的时间轴内容
        string? timelineRelativePath = null;
        TimelineAsset? captureTimeline = null;
        if (arguments.TryGetProperty("timelinePath", out var timelineValue) && !string.IsNullOrWhiteSpace(timelineValue.GetString()))
        {
            timelineRelativePath = timelineValue.GetString()!;
            ResolveTimelinePath(timelineRelativePath);
            captureTimeline = LoadTimelineRuntimeAsset(timelineRelativePath)
                ?? throw new ArgumentException("时间轴资产无法读取或内容无效");
        }
        int? timelineFrame = arguments.TryGetProperty("timelineFrame", out var frameValue) ? frameValue.GetInt32() : null;
        if (timelineFrame is < 0 or > 10_000_000) throw new ArgumentException("timelineFrame 必须在 0–10000000 之间");
        if (captureTimeline is not null && timelineFrame is null) timelineFrame = 0;
        if (timelineFrame.HasValue)
        {
            captureTimeline ??= JsonSerializer.Deserialize<TimelineAsset>(JsonSerializer.Serialize(_timelineAsset)) ?? _timelineAsset;
            timelineFrame = Math.Clamp(timelineFrame.Value, 0, Math.Max(1, captureTimeline.LengthFrames));
        }
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
            _captureTimelineAsset = captureTimeline;
            _captureTimelineFrame = timelineFrame;
            _captureTimelineLabel = timelineRelativePath;
            //按帧截图时天气时刻跟着帧走，未指定 weatherTime 才自动推算
            _captureWeatherTime = weatherTime ?? (timelineFrame.HasValue && captureTimeline is not null
                ? timelineFrame.Value / (float)Math.Max(1, captureTimeline.FrameRate)
                : TimelineWeatherTime);
            _captureRain = rain; _captureSnow = snow; _captureHeat = heat;
            _captureStorm = storm; _captureLightning = lightning; _captureWeatherSeed = weatherSeed;
            _captureWeatherViewDistance = weatherViewDistance;
            _captureWaterFiltering = waterFiltering;
            _captureHardwareAcceleration = hardwareAcceleration;
            // 截图必须使用真正参与渲染的有效设置：项目配置与场景环境合并后的结果，
            // 直接用 _renderSettings 会漏掉场景环境对天空、雾、地面等字段的覆盖
            _captureRenderSettings = ResolveRenderSettings(GetActiveRenderCameraObject()).CloneForExport();
            _captureRenderSettings.PathTracingEnabled = pathTracing;
            if (hardwareAcceleration.HasValue)
            {
                _captureRenderSettings.HardwareRayTracingAuto = false;
                _captureRenderSettings.UseHardwareRayTracing = hardwareAcceleration.Value;
                _captureRenderSettings.ForceComputePath = false;
            }
            if (sunMethod.HasValue) _captureRenderSettings.SunShadowMethod = sunMethod.Value;
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
            _captureRendered = false;
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
        if (_centerTab != 1 || !_captureRendered || _pipeline?.CanSaveDisplayPng != true)
        {
            return;
        }
        if (_captureRenderSettings is not { } settings) return;
        if (settings.PathTracingEnabled && _pipeline.AccumulatedSamples < _captureSamples) return;
        if (!settings.PathTracingEnabled && settings.SunEnabled &&
            settings.SunShadowMethod == MVoxel.Rendering.SunShadowMethod.Progressive &&
            settings.ShadowSoftness > 0.0001F &&
            _pipeline.RealtimeConvergedFrames < Math.Max(0, settings.SunAccumulationFrames - 1)) return;
        try
        {
            if (File.Exists(path)) throw new IOException("截图文件已存在");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var saved = _pipeline.SaveDisplayPng(path);
            _captureMetadata[path] = new { samples = _pipeline.AccumulatedSamples,
                pathTracing = settings.PathTracingEnabled, realtimeFrames = settings.PathTracingEnabled ? 0 : _pipeline.RealtimeConvergedFrames + 1,
                width = _pipeline.OutputWidth, height = _pipeline.OutputHeight,
                directSampling = _captureRenderSettings!.EmissiveDirectSampling,
                lightTree = _captureRenderSettings.EmissiveLightTree,
                denoise = _captureRenderSettings.PathDenoising,
                exposure = _captureRenderSettings.Exposure,
                skyAmbient = _captureRenderSettings.SkyAmbient,
                emissiveIntensity = _captureRenderSettings.EmissiveIntensity,
                sunEnabled = _captureRenderSettings.SunEnabled,
                rain = _captureRenderSettings.WeatherRainIntensity, snow = _captureRenderSettings.WeatherSnowIntensity,
                heatHaze = _captureRenderSettings.WeatherHeatHaze, storm = _captureRenderSettings.WeatherStormIntensity,
                lightning = _captureRenderSettings.WeatherLightningFrequency,
                weatherTime = _captureWeatherTime, weatherSeed = _captureRenderSettings.WeatherSeed,
                weatherViewDistance = _captureRenderSettings.WeatherViewDistance,
                timelinePath = _captureTimelineLabel,
                timelineFrame = _captureTimelineFrame,
                waterFiltering = _captureWaterFiltering,
                sunShadowMethod = settings.SunShadowMethod.ToString(),
                sunShadowFallback = _pipeline.SunShadowFallbackReason,
                hardwareRayQueryActive = _pipeline.HardwareShadowsActive,
                hardwareAccelerationRequested = _captureHardwareAcceleration,
                hardwareRayQueryStatus = _pipeline.HardwareShadowStatus,
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
        _captureTimelineAsset = null;
        _captureTimelineFrame = null;
        _captureTimelineLabel = null;
        _captureRenderSettings = null;
        _captureRain = null; _captureSnow = null; _captureHeat = null;
        _captureStorm = null; _captureLightning = null; _captureWeatherSeed = null;
        _captureWeatherViewDistance = null;
        _captureWaterFiltering = null;
        _captureHardwareAcceleration = null;
        _accumulationSignature = string.Empty;
    }
}
