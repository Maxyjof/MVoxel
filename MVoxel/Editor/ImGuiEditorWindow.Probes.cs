using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Scene;
using MVoxel.Rendering;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    /// <summary>自动烘焙每帧的时间预算，单位毫秒</summary>
    private const double ProbeBakeFrameBudgetMs = 5;

    /// <summary>自动烘焙的检查间隔，单位毫秒</summary>
    private const long ProbeAutoBakeCheckIntervalMs = 250;

    /// <summary>世界内容稳定多久后才自动烘焙，避免连续编辑时反复重烘</summary>
    private const long ProbeAutoBakeSettleMs = 600;

    private ProbeBakeJob? _probeBakeJob;
    private StableId _probeBakeObjectId;
    private ComponentRecord? _probeBakeComponent;
    private ProbeVolumeComponentData _probeBakeData;
    private ProbeBakeLighting _probeBakeLighting;
    private ProbeBakeQuality _probeBakeQuality;
    private bool _probeBakePersist;
    private ProbeVolumeBakeData? _liveProbeBake;
    private StableId _liveProbeBakeObjectId;
    private int _probeBakedVersion = -1;
    private long _probeBakedParameters = long.MinValue;
    private long _probeBakedSceneSignature = long.MinValue;
    private int _probeBakeCompletedCount;
    /// <summary>场景内容自从上次烘焙后是否发生过改动，未改动时自动烘焙完全不做检查</summary>
    private bool _probeSceneSignatureDirty = true;
    private long _probeAutoBakeCheckAt;
    private long _probeAutoBakeSeenAt;
    private long _probeAutoBakeSeenSignature = long.MinValue;

    // GPU 探针体积的上传缓存：烘焙结果引用、体积变换与管线实例都没变时不重新打包
    private string _probeAssetCachePath = string.Empty;
    private DateTime _probeAssetCacheWrite;
    private ProbeVolumeBakeData? _probeAssetCache;
    private long _gpuProbeSignature = long.MinValue;
    private object? _gpuProbePipeline;

    private void DrawProbeVolumeWindow()
    {
        if (!_showProbeVolumeWindow) return;
        if (!BeginToolWindow("探针体积##ProbeVolumeWindow", PanelWindowFlags, () => _showProbeVolumeWindow = false))
        {
            ImGui.End();
            return;
        }
        ImGui.TextWrapped("探针体积：沿固定方向集对体素世界采集可见天空与一次反弹，网格参数与可见度一起写入烘焙数据。勾选“自动烘焙”的对象会在场景内容稳定后自动重烘；也可以在这里手动重烘。");
        if (_sceneDocument is null)
        {
            ImGui.TextDisabled("请先打开场景");
            ImGui.End();
            return;
        }
        var probes = _sceneDocument.Objects
            .SelectMany(item => item.Components.Where(SceneComponentTypes.IsProbeVolume).Select(component => (item, component)))
            .ToArray();
        if (probes.Length == 0)
        {
            ImGui.TextDisabled("场景中没有自动探针组件，请在对象检查器中添加");
            ImGui.End();
            return;
        }
        var selectedIndex = Math.Clamp(_probeVolumeSelection, 0, probes.Length - 1);
        if (ImGui.BeginCombo("探针对象##ProbeObject", probes[selectedIndex].item.Name))
        {
            for (var index = 0; index < probes.Length; index++)
            {
                if (ImGui.Selectable(probes[index].item.Name, index == selectedIndex))
                {
                    selectedIndex = index;
                    _probeVolumeSelection = index;
                }
            }
            ImGui.EndCombo();
        }
        var selectedProbe = probes[selectedIndex];
        var data = SceneComponentTypes.ReadProbeVolume(selectedProbe.component);
        ImGui.SeparatorText(selectedProbe.item.Name);
        ImGui.Text($"尺寸 {data.SizeX:0.##} × {data.SizeY:0.##} × {data.SizeZ:0.##} · 密度 {data.Density:0.##}");
        var (gridX, gridY, gridZ) = ProbeBakeJob.ResolveGrid(data, _renderSettings.ProbeBakeMaxProbes);
        ImGui.TextDisabled($"网格 {gridX} × {gridY} × {gridZ} = {(long)gridX * gridY * gridZ:N0} 个探针 · " +
            $"每探针 {Math.Clamp(_renderSettings.ProbeBakeRays, 8, 256)} 个方向 · 平滑 {_renderSettings.ProbeBakeSmoothing} 次");
        var baking = _probeBakeJob is not null && _probeBakeObjectId == selectedProbe.item.Id;
        if (baking)
        {
            ImGui.ProgressBar(_probeBakeJob!.Progress, new Vector2(-1F, 16F),
                _probeBakePersist ? "手动烘焙中…" : "自动烘焙中…");
        }
        else if (ImGui.Button("生成探针数据##ProbeBake", new Vector2(-1F, 0F)))
        {
            StartProbeBake(selectedProbe.item, selectedProbe.component, data, persist: true);
        }

        var autoBake = data.AutoBake;
        if (ImGui.Checkbox("自动烘焙##ProbeAutoBakeInline", ref autoBake))
        {
            SceneComponentTypes.WriteProbeVolume(selectedProbe.component, data with { AutoBake = autoBake });
            MarkSceneDocumentDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只在实际内容变化时自动重烘，调参不触发，改完点生成探针数据");

        ImGui.SeparatorText("烘焙质量");
        DrawProbeBakeQualitySettings();

        var (currentRays, currentMaxProbes, currentSmoothing, currentDistance) =
            (_renderSettings.ProbeBakeRays, _renderSettings.ProbeBakeMaxProbes,
                _renderSettings.ProbeBakeSmoothing, _renderSettings.ProbeBakeDistance);
        var currentQuality = new ProbeBakeQuality(currentRays, currentMaxProbes, currentSmoothing, currentDistance);
        if (ProbeBakeParameters(data, BuildProbeLighting(), currentQuality) != _probeBakedParameters &&
            _liveProbeBakeObjectId == selectedProbe.item.Id)
            ImGui.TextDisabled("参数已修改，点“生成探针数据”应用");

        if (_liveProbeBakeObjectId == selectedProbe.item.Id && _liveProbeBake is { Samples.Count: > 0 } live)
            ImGui.Text($"当前生效的探针数据：{live.Samples.Count:N0} 个样本（{live.CountX} × {live.CountY} × {live.CountZ}）");
        else if (!string.IsNullOrWhiteSpace(data.BakedAsset))
            ImGui.TextDisabled($"已保存到 {data.BakedAsset}，但本次会话尚未重新烘焙");
        else
            ImGui.TextDisabled("尚未生成探针数据");
        ImGui.End();
    }

    /// <summary>
    /// 探针烘焙质量设置，直接写入项目渲染配置
    /// </summary>
    private void DrawProbeBakeQualitySettings()
    {
        SliderInt("每探针方向数##ProbeRays", ref _renderSettings.ProbeBakeRays, 8, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每个探针的采样方向数，越多越稳定，默认 64");
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveProjectSettings();
        SliderInt("探针数量上限##ProbeMaxProbes", ref _renderSettings.ProbeBakeMaxProbes, 1024, 262144);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("单个探针体积的探针数上限，调高会显著增加烘焙时间，默认 65,536");
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveProjectSettings();
        SliderInt("平滑次数##ProbeSmoothing", ref _renderSettings.ProbeBakeSmoothing, 0, 4);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("三维盒式平滑迭代次数，过多会让明暗边界变软，默认 1");
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveProjectSettings();
        SliderFloat("最长探测距离##ProbeDistance", ref _renderSettings.ProbeBakeDistance, 0F, 512F, "%.0f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("探针射线的最大行进距离，0 表示按探针体积对角线自动取值");
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveProjectSettings();
    }

    /// <summary>
    /// 启动一次探针烘焙，按帧推进
    /// </summary>
    /// <param name="sceneObject">探针所属对象</param>
    /// <param name="component">探针组件</param>
    /// <param name="data">探针参数</param>
    /// <param name="persist">完成时是否写入资产并标记场景修改</param>
    private void StartProbeBake(SceneObject sceneObject, ComponentRecord component, ProbeVolumeComponentData data,
        bool persist)
    {
        if (_project is null || _sceneDocument is null || _probeBakeJob is not null) return;
        var world = GetRenderWorld(uint.MaxValue, sceneVisibility: true);
        var objectPosition = GetObjectWorldPosition(sceneObject);
        var probePosition = new Vector3(objectPosition.X, objectPosition.Y, objectPosition.Z);
        var lighting = BuildProbeLighting();
        var quality = new ProbeBakeQuality(_renderSettings.ProbeBakeRays, _renderSettings.ProbeBakeMaxProbes,
            _renderSettings.ProbeBakeSmoothing, _renderSettings.ProbeBakeDistance);
        var job = ProbeBakeJob.TryCreate(world, probePosition, data, lighting, ProbeContentSignature(), quality);
        if (job is null)
        {
            if (persist) SetStatus("探针烘焙没有可用的体素内容");
            return;
        }

        _probeBakeJob = job;
        _probeBakeObjectId = sceneObject.Id;
        _probeBakeComponent = component;
        _probeBakeData = data;
        _probeBakeLighting = lighting;
        _probeBakeQuality = quality;
        _probeBakePersist = persist;
        if (persist) SetStatus($"开始烘焙探针：{sceneObject.Name} · {job.ProbeCount:N0} 个探针");
    }

    /// <summary>
    /// 每帧推进自动烘焙与手动烘焙
    /// </summary>
    private void PumpProbeBake()
    {
        if (_project is null || _sceneDocument is null)
        {
            _probeBakeJob = null;
            return;
        }

        if (_probeBakeJob is { } running)
        {
            // 烘焙过程中场景内容变了就作废，避免把半新半旧的遮挡结果写进资产
            if (ProbeContentSignature() != running.SceneSignature)
            {
                _probeBakeJob = null;
                return;
            }

            if (running.Step(ProbeBakeFrameBudgetMs) is { } result)
            {
                CompleteProbeBake(result);
            }

            return;
        }

        TryStartAutoProbeBake();
    }

    /// <summary>
    /// 内容稳定后自动烘焙第一个开启自动烘焙的探针
    /// </summary>
    /// <remarks>
    /// 是否需要重烘只看两件事：会影响遮挡或反照率的场景内容变没变、探针参数变没变。
    /// 不能用组合世界实例做判断，缓存换进换出会给出新实例，于是每帧都重烘、
    /// 每帧都把累积样本清掉，画面会一直停在起始噪点状态
    /// </remarks>
    private void TryStartAutoProbeBake()
    {
        // 没有任何改动时连指纹都不算：自动烘焙只在场景被编辑过之后才参与判断，
        // 不是定时轮询
        if (!_probeSceneSignatureDirty) return;
        var now = Environment.TickCount64;
        if (now - _probeAutoBakeCheckAt < ProbeAutoBakeCheckIntervalMs) return;
        _probeAutoBakeCheckAt = now;
        if (_projectOperation.IsRunning || _historyConfirmation is not null) return;

        var signature = ProbeContentSignature();
        // 内容没稳定下来就先不动手，连续编辑不会反复重烘
        if (signature != _probeAutoBakeSeenSignature)
        {
            _probeAutoBakeSeenSignature = signature;
            _probeAutoBakeSeenAt = now;
            return;
        }

        if (now - _probeAutoBakeSeenAt < ProbeAutoBakeSettleMs) return;
        // 只有世界内容真的变了才重烘；探针参数或烘焙质量改动不自动触发，
        // 一律由“生成探针数据”按钮应用，避免拖滑块时反复重烘
        if (signature == _probeBakedSceneSignature)
        {
            _probeSceneSignatureDirty = false;
            return;
        }

        foreach (var sceneObject in _sceneDocument!.Objects)
        foreach (var component in sceneObject.Components)
        {
            if (!SceneComponentTypes.IsProbeVolume(component) || !component.Enabled) continue;
            var data = SceneComponentTypes.ReadProbeVolume(component);
            if (!data.AutoBake || data.Editing) continue;
            StartProbeBake(sceneObject, component, data, persist: false);
            return;
        }

        // 场景里没有可自动烘焙的探针，这一段也不用再查
        _probeSceneSignatureDirty = false;
    }

    /// <summary>
    /// 会影响探针烘焙结果的场景内容指纹
    /// </summary>
    /// <remarks>
    /// 覆盖三类改动：体素模型对象的变换、体素数据自身的版本、可见性与图层状态。
    /// 动画与时间轴驱动的位移同样落在这里，因为播放时改的就是对象变换
    /// </remarks>
    /// <returns>内容指纹，输入完全一致时结果稳定</returns>
    private long ProbeContentSignature()
    {
        var hash = new HashCode();
        if (_sceneDocument is null) return hash.ToHashCode();
        foreach (var name in _sceneDocument.HiddenLayerNames) hash.Add(name);
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            hash.Add(sceneObject.Id.Value);
            hash.Add(sceneObject.Visible);
            hash.Add(sceneObject.LayerIndex);
            var transform = sceneObject.Transform;
            hash.Add(transform.Mode);
            if (transform.Mode == TransformMode.Grid)
            {
                hash.Add(transform.Grid.Position);
                hash.Add(transform.Grid.Rotation);
            }
            else
            {
                hash.Add(transform.Free.Position);
                hash.Add(transform.Free.Rotation);
            }

            foreach (var component in sceneObject.Components)
            {
                if (!SceneComponentTypes.IsVoxelModel(component)) continue;
                hash.Add(component.Enabled);
                if (_objectVoxels.TryGetWorld(sceneObject.Id, out var world)) hash.Add(world.ContentVersion);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// 把影响烘焙结果的全部参数压成一个指纹
    /// </summary>
    /// <param name="data">探针参数</param>
    /// <param name="lighting">环境光参数</param>
    /// <param name="quality">烘焙质量参数</param>
    /// <returns>参数指纹</returns>
    private static long ProbeBakeParameters(ProbeVolumeComponentData data, ProbeBakeLighting lighting,
        ProbeBakeQuality quality)
    {
        var hash = new HashCode();
        hash.Add(data.SizeX);
        hash.Add(data.SizeY);
        hash.Add(data.SizeZ);
        hash.Add(data.OffsetX);
        hash.Add(data.OffsetY);
        hash.Add(data.OffsetZ);
        hash.Add(data.Density);
        hash.Add(lighting.SkyTop);
        hash.Add(lighting.SkyHorizon);
        hash.Add(lighting.SunColor);
        hash.Add(lighting.SunDirection);
        hash.Add(lighting.SunStrength);
        hash.Add(quality.RayCount);
        hash.Add(quality.MaxProbes);
        hash.Add(quality.Smoothing);
        hash.Add(quality.Distance);
        return hash.ToHashCode();
    }

    /// <summary>
    /// 烘焙完成：把数据交给渲染，需要持久化时写入资产并标记场景修改
    /// </summary>
    /// <param name="result">烘焙结果</param>
    private void CompleteProbeBake(ProbeVolumeBakeData result)
    {
        var world = GetRenderWorld(uint.MaxValue, sceneVisibility: true);
        _probeBakedVersion = world.ContentVersion;
        _probeBakedParameters = ProbeBakeParameters(_probeBakeData, _probeBakeLighting, _probeBakeQuality);
        _probeBakedSceneSignature = _probeBakeJob?.SceneSignature ?? ProbeContentSignature();
        _probeSceneSignatureDirty = false;
        _probeBakeCompletedCount++;
        _liveProbeBake = result;
        _liveProbeBakeObjectId = _probeBakeObjectId;
        var name = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _probeBakeObjectId)?.Name ?? "自动探针";
        if (_probeBakePersist && _project is not null && _probeBakeComponent is not null)
        {
            try
            {
                var directory = Path.Combine(_project.AssetsPath, "Lighting");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{_probeBakeObjectId}.probe.json");
                AtomicFile.WriteText(path, JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                SceneComponentTypes.WriteProbeVolume(_probeBakeComponent,
                    _probeBakeData with { BakedAsset = "Lighting/" + Path.GetFileName(path) });
                MarkSceneDocumentDirty();
                SetStatus($"已生成探针数据：{name} · {result.Samples.Count:N0} 个探针");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MVoxelLog.Error("写入探针烘焙资产失败", exception);
                SetStatus($"探针数据写入失败：{exception.Message}");
            }
        }
        else if (_probeBakePersist)
        {
            SetStatus($"已重新烘焙探针：{name}");
        }

        _probeBakeJob = null;
        _probeBakeComponent = null;
        // 探针数据变化会改变间接光，丢弃已累积的样本
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
    }

    /// <summary>
    /// 读取持久化的探针资产；同一文件在修改时间不变时复用反序列化结果
    /// </summary>
    private ProbeVolumeBakeData? ReadCachedProbeAsset(string path)
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            if (_probeAssetCache is not null && _probeAssetCachePath == path && _probeAssetCacheWrite == write)
                return _probeAssetCache;
            var bake = JsonSerializer.Deserialize<ProbeVolumeBakeData>(File.ReadAllText(path),
                new JsonSerializerOptions { IncludeFields = true });
            bake?.Normalize();
            _probeAssetCache = bake;
            _probeAssetCachePath = path;
            _probeAssetCacheWrite = write;
            return bake;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把烘焙结果打包成 GPU 布局并上传；数据、体积变换与管线实例都没变时直接跳过
    /// </summary>
    /// <remarks>
    /// 布局与着色器 ProbeVolumeBuffer 一致：3 个 vec4 头（原点/尺寸/网格数），
    /// 之后逐探针 vec4(R, G, B, 天空可见度)，索引 X 最快。
    /// </remarks>
    private void PushProbeVolumeGpu(SceneObject? probeObject, ProbeVolumeComponentData data,
        ProbeVolumeBakeData? bake)
    {
        // 管线还没创建时不能缓存签名，否则之后创建出来的管线永远收不到数据
        if (_pipeline is null) return;
        var hash = new HashCode();
        hash.Add(probeObject?.Id);
        hash.Add(data.OffsetX);
        hash.Add(data.OffsetY);
        hash.Add(data.OffsetZ);
        hash.Add(data.SizeX);
        hash.Add(data.SizeY);
        hash.Add(data.SizeZ);
        hash.Add(bake?.CountX ?? 0);
        hash.Add(bake?.CountY ?? 0);
        hash.Add(bake?.CountZ ?? 0);
        hash.Add(bake?.Samples.Count ?? 0);
        hash.Add(bake);
        var signature = hash.ToHashCode();
        if (signature == _gpuProbeSignature && ReferenceEquals(_gpuProbePipeline, _pipeline)) return;

        float[] packed;
        if (bake is null || bake.Samples.Count == 0)
        {
            packed = new float[12];
        }
        else
        {
            var count = bake.Samples.Count;
            packed = new float[12 + count * 4];
            // GetObjectWorldPosition 返回 OpenTK.Vector3，与 System.Numerics.Vector3 不互通
            var position = probeObject is null
                ? OpenTK.Mathematics.Vector3.Zero
                : GetObjectWorldPosition(probeObject);
            var size = new OpenTK.Mathematics.Vector3(MathF.Max(data.SizeX, .001F),
                MathF.Max(data.SizeY, .001F), MathF.Max(data.SizeZ, .001F));
            var origin = position + new OpenTK.Mathematics.Vector3(data.OffsetX, data.OffsetY, data.OffsetZ) - size * .5F;
            packed[0] = origin.X;
            packed[1] = origin.Y;
            packed[2] = origin.Z;
            packed[3] = bake.CountX;
            packed[4] = size.X;
            packed[5] = size.Y;
            packed[6] = size.Z;
            packed[7] = bake.CountY;
            packed[8] = bake.CountZ;
            packed[9] = count;
            packed[10] = 0F;
            packed[11] = 0F;
            for (var index = 0; index < count; index++)
            {
                var sample = bake.Samples[index];
                var offset = 12 + index * 4;
                packed[offset] = sample.R;
                packed[offset + 1] = sample.G;
                packed[offset + 2] = sample.B;
                packed[offset + 3] = sample.Occlusion;
            }
        }

        _pipeline.SetProbeVolume(packed);
        _gpuProbeSignature = signature;
        _gpuProbePipeline = _pipeline;
    }

    /// <summary>
    /// 用当前渲染配置组装探针烘焙需要的环境光参数
    /// </summary>
    /// <returns>天光与太阳参数</returns>
    private ProbeBakeLighting BuildProbeLighting()
    {
        var settings = _renderSettings;
        var sun = settings.SunEnabled ? settings.SunColor * Math.Clamp(settings.SunIntensity, 0F, 8F) * .8F : Vector3.Zero;
        return new ProbeBakeLighting(settings.SkyTop, settings.SkyHorizon, sun, settings.SunDirection,
            settings.SunEnabled ? Math.Clamp(settings.SunIntensity, 0F, 4F) : 0F);
    }
}
