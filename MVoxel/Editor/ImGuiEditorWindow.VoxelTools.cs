using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Editor.Settings;
using MVoxel.Editor.Templates;
using MVoxel.Voxels;
using MVoxel.Scene;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using OpenTK.Mathematics;
using Color = System.Drawing.Color;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using NumVector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

internal enum EditorTool
{
    Camera,
    Draw,
    Erase,
    Pick,
    Line,
    Box,
    Fill,
}

/// <summary>
/// GPU 视口接线、笔刷工具、撤销入口和渲染设置面板
/// 所有操作都在编辑器线程执行，渲染管线只接收不可变快照和扁平参数块
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private bool _settingsToggleSun;
    private bool _settingsToggleAo;
    private bool _settingsTogglePalette;
    private bool _settingsToggleGround;
    private bool _settingsTogglePath;
    private bool _settingsTogglePathDenoise;
    private bool _settingsToggleDof;
    private bool _settingsToggleMis;
    private bool _settingsToggleEmissive;
    private bool _settingsToggleDither;
    private bool _settingsToggleHueCycle;
    private bool _brushPreviewErase;
    private VoxelCoord64? _toolDragAnchor;
    private VoxelCoord64? _toolDragCurrent;
    private bool _toolDragErase;
    private IReadOnlyList<VoxelCoord64>? _pendingFloodCells;
    private VoxelValue _pendingFloodValue;
    private bool _pendingFloodOpen;

    private void InitializeVoxelPipeline()
    {
        _pipeline?.Dispose();
        _gpuRenderingDisabled = false;
        _pipelineStatus = "显卡体素管线正在初始化";
        try
        {
            _pipeline = new VulkanVoxelRenderPipeline(_vulkan, _imguiRenderer);
            _pipelineStatus = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            _pipeline = null;
            _pipelineStatus = $"显卡计算管线初始化失败，当前使用 CPU 预览：{exception.Message}";
            MVoxelLog.Error("Vulkan Compute 管线初始化失败，已切换 CPU 预览", exception);
        }

        _renderSettings = _editorSettings.RenderViewSettings;
        SyncSettingsToggles();
    }

    private void SyncSettingsToggles()
    {
        var settings = _renderSettings;
        _settingsToggleSun = settings.SunEnabled;
        _settingsToggleAo = settings.AmbientOcclusionEnabled;
        _settingsTogglePalette = settings.PaletteShading;
        _settingsToggleGround = settings.ShowGround;
        _settingsTogglePath = settings.PathTracingEnabled;
        _settingsTogglePathDenoise = settings.PathDenoising;
        _settingsToggleDof = settings.DepthOfField;
        _settingsToggleMis = settings.DirectLightingMIS;
        _settingsToggleEmissive = settings.EmissiveVoxels;
        _settingsToggleDither = settings.Dither;
        _settingsToggleHueCycle = settings.HueCycle;
    }

    private void ShutdownVoxelPipeline()
    {
        if (_project is null) SaveEditorSettings();
        else SaveProjectSettings();

        _pipeline?.Dispose();
        _pipeline = null;
    }

    private void LoadWorldForProject(ProjectHandle project)
    {
        _voxelHistory.Clear();
        _selectedVoxel = null;
        _selectedSceneObjectId = null;
        _sceneDocument = LoadSceneDocument(project);
        _sceneDocumentDirty = false;
        _renderCameraId = null;
        _palette = EditorPalettes.ResolveProjectPalette(project);
        _paletteIsProjectLevel = true;
        var referencedLargeWorldPath = FindReferencedModelPath(project, _sceneDocument, ".mvworld");
        var referencedNativePath = FindReferencedModelPath(project, _sceneDocument, ".mvmodel");
        var hasModelReference = HasModelReference(_sceneDocument);
        var largeWorldPath = referencedLargeWorldPath ??
            (!hasModelReference && referencedNativePath is null && Directory.Exists(Path.Combine(project.AssetsPath, "Worlds"))
                ? Directory.EnumerateFiles(Path.Combine(project.AssetsPath, "Worlds"), "*.mvworld")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null);
        var modelPath = referencedNativePath ?? Path.Combine(project.AssetsPath, "Worlds", "主世界.mvmodel");
        try
        {
            if (largeWorldPath is not null && File.Exists(largeWorldPath))
            {
                _world = LargeWorldFormat.Read(largeWorldPath);
                SetStatus($"已载入大世界模型：{Path.GetFileName(largeWorldPath)}（{_world.OccupiedVoxelCount:N0} 体素）");
            }
            else if (File.Exists(modelPath))
            {
                _world = LargeWorldFormat.Read(modelPath);
                SetStatus($"已载入体素模型：{Path.GetFileName(modelPath)}（{_world.OccupiedVoxelCount} 体素）");
            }
            else if (string.Equals(_projectSettings.TemplateId, ProjectTemplateId.Empty.ToString(), StringComparison.Ordinal))
            {
                _world = new VoxelWorld();
                SetStatus("已载入空项目，可以从零开始建模");
            }
            else
            {
                _world = VoxelDemoScene.Build(_palette);
                SetStatus("已生成示范场景（房屋·树·提灯），可用“绘制/擦除”工具继续创作");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            _world = VoxelDemoScene.Build(_palette);
            SetStatus($"模型读取失败，已载入示范场景：{exception.Message}");
        }

        var primaryPath = largeWorldPath is not null && File.Exists(largeWorldPath) ? largeWorldPath : modelPath;
        var mergedLayers = MergeSceneModelWorlds(project, primaryPath);
        if (mergedLayers > 0)
        {
            SetStatus($"{_statusText}，已合并 {mergedLayers} 个分层对象");
        }

        _pipeline?.SyncWorld(_world, GetSceneGpuCellBudget());
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _lastRenderGpuSubmitMs = 0;
        _renderViewNeedsInitialRender = true;
        FrameCameraOnWorld();
    }

    /// <summary>
    /// 按场景中的模型组件解析资源，避免项目同时存在多个模型时误载入其他文件。
    /// 解析范围限定在 Assets 内，场景文件中的外部路径不会被访问。
    /// </summary>
    private static string? FindReferencedModelPath(ProjectHandle project, Scene.SceneDocument? document,
        string extension)
    {
        var model = document?.Objects
            .SelectMany(item => item.Components)
            .Where(SceneComponentTypes.IsVoxelModel)
            .Select(SceneComponentTypes.ReadVoxelModel)
            .Select(data => data.Model)
            .FirstOrDefault(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(model)) return null;

        var candidate = Path.GetFullPath(Path.Combine(project.RootPath, model.Replace('/', Path.DirectorySeparatorChar)));
        var assetsRoot = Path.GetFullPath(project.AssetsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static bool HasModelReference(Scene.SceneDocument? document) => document?.Objects
        .SelectMany(item => item.Components)
        .Where(SceneComponentTypes.IsVoxelModel)
        .Select(SceneComponentTypes.ReadVoxelModel)
        .Any(data => !string.IsNullOrWhiteSpace(data.Model)) == true;

    /// <summary>
    /// 读取项目主场景并保留场景文档供层级和渲染窗口共同使用
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <returns>主场景文档</returns>
    private Scene.SceneDocument LoadSceneDocument(ProjectHandle project)
    {
        var scenePath = Path.Combine(project.AssetsPath,
            _activeSceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scenePath))
        {
            _sceneValidationErrors = Array.Empty<string>();
            return new Scene.SceneDocument { Name = "主场景" };
        }

        try
        {
            var scene = Scene.SceneSerializer.Load(scenePath);
            _sceneValidationErrors = Scene.SceneValidation.Validate(scene);
            foreach (var error in _sceneValidationErrors)
            {
                MVoxelLog.Warn($"主场景结构校验：{error}");
            }

            return scene;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            _sceneValidationErrors = Array.Empty<string>();
            MVoxelLog.Error($"主场景读取失败，已使用新的场景文档：{scenePath}", exception);
            return new Scene.SceneDocument { Name = "主场景" };
        }
    }

    private void FrameCameraOnWorld()
    {
        var limits = GetSceneZoomLimits();
        if (_world.TryGetBounds(out var min, out var max))
        {
            var center = new Vector2((float)((min.X + max.X) / 2.0), (float)((min.Z + max.Z) / 2.0));
            var diagonal = MathF.Sqrt(
                MathF.Pow((float)(max.X - min.X + 1), 2) +
                MathF.Pow((float)(max.Y - min.Y + 1), 2) +
                MathF.Pow((float)(max.Z - min.Z + 1), 2));
            _scenePan = center;
            _sceneFreeMoveHeight = 0F;
            _sceneCameraDistance = Math.Clamp(diagonal * 1.15F, limits.MinimumDistance, limits.MaximumDistance);
            _sceneOrbitYaw = MathF.PI / 4F;
            _sceneOrbitPitch = 0.42F;
        }
        else
        {
            _scenePan = Vector2.Zero;
            _sceneFreeMoveHeight = 0F;
            _sceneCameraDistance = Math.Clamp(18F, limits.MinimumDistance, limits.MaximumDistance);
        }
    }

    private SceneZoomLimits GetSceneZoomLimits()
    {
        if (!_world.TryGetBounds(out var min, out var max))
        {
            return SceneZoomPolicy.Default();
        }

        return SceneZoomPolicy.FromBounds(
            max.X - min.X + 1D,
            max.Y - min.Y + 1D,
            max.Z - min.Z + 1D);
    }

    /// <summary>
    /// 根据当前场景距离和 LOD 配置计算场景视图的 GPU 体素预算
    /// </summary>
    /// <returns>零表示完整工作集，其余数值表示场景视图采样上限</returns>
    private int GetSceneGpuCellBudget()
    {
        var count = _world.OccupiedVoxelCount;
        if (count <= 250_000)
        {
            return 0;
        }

        var levels = _project is null ? _editorLodLevels : _projectLodLevels;
        var distances = _project is null ? _editorLodDistances : _projectLodDistances;
        var level = 0;
        while (level < Math.Max(0, levels - 1) && _sceneCameraDistance > distances[level])
        {
            level++;
        }

        var divisor = 1 << Math.Min(level, 5);
        return Math.Clamp((count + divisor - 1) / divisor, 32_768, 250_000);
    }

    /// <summary>
    /// 分层体素的所有权表：对象 ID 到该对象拥有的体素坐标集合，
    /// 保存时据此把主世界与分层文件各自写回，互不吞并
    /// </summary>
    private readonly Dictionary<StableId, HashSet<VoxelCoord64>> _layerOwnership = new();

    private bool SaveWorldToProject(ProjectHandle project)
    {
        try
        {
            var modelAssetPath = ResolveCurrentWorldAssetPath(project);
            var modelPath = Path.Combine(project.RootPath, modelAssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            // 分层模式下 _world 是合并态：主世界文件只写非分层体素，
            // 各分层对象按所有权分别写回自己的文件，分层在保存后依然成立
            var primaryVoxelCount = _world.OccupiedVoxelCount;
            if (_layerOwnership.Count == 0)
            {
                if (_world.OccupiedVoxelCount > 0)
                {
                    LargeWorldFormat.Write(modelPath, _world);
                }
                else if (File.Exists(modelPath))
                {
                    File.Delete(modelPath);
                }
            }
            else
            {
                var ownedUnion = new HashSet<VoxelCoord64>();
                foreach (var owned in _layerOwnership.Values)
                {
                    ownedUnion.UnionWith(owned);
                }

                var terrainOnly = new VoxelWorld();
                foreach (var item in _world.EnumerateOccupied())
                {
                    if (!ownedUnion.Contains(item.Coordinate))
                    {
                        terrainOnly.Set(item.Coordinate, item.Value);
                    }
                }

                primaryVoxelCount = terrainOnly.OccupiedVoxelCount;
                if (primaryVoxelCount > 0)
                {
                    LargeWorldFormat.Write(modelPath, terrainOnly);
                }
                else if (File.Exists(modelPath))
                {
                    File.Delete(modelPath);
                }

                SaveOwnedLayers(project);
            }

            var scenePath = Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox");
            var document = _sceneDocument ?? new Scene.SceneDocument { Name = "主场景" };
            var graph = new Scene.SceneGraph(document);
            var modelObject = document.Objects.FirstOrDefault(item =>
                item.Components.Any(SceneComponentTypes.IsVoxelModel));
            modelObject ??= graph.Add("体素世界", transform: TransformComponent.FromGrid(GridTransform.Identity));
            var modelComponent = modelObject.Components.FirstOrDefault(component =>
                SceneComponentTypes.IsVoxelModel(component));
            if (modelComponent is null)
            {
                modelObject.Components.Add(new Scene.ComponentRecord
                {
                    TypeId = SceneComponentTypes.VoxelModel,
                    SchemaVersion = 1,
                    Data = JsonSerializer.SerializeToElement(new
                    {
                        model = modelAssetPath,
                        voxelCount = primaryVoxelCount,
                        part = "场景模型",
                    }),
                });
            }
            else
            {
                modelComponent.Data = JsonSerializer.SerializeToElement(new
                {
                    model = modelAssetPath,
                    voxelCount = primaryVoxelCount,
                    part = "场景模型",
                });
            }
            _sceneDocument = document;
            Scene.SceneSerializer.Save(scenePath, document);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"保存失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把每个分层对象当前拥有的体素写回它引用的模型文件，并同步组件里的体素数
    /// </summary>
    private void SaveOwnedLayers(ProjectHandle project)
    {
        if (_sceneDocument is null)
        {
            return;
        }

        foreach (var sceneObject in _sceneDocument.Objects)
        {
            if (!_layerOwnership.TryGetValue(sceneObject.Id, out var owned))
            {
                continue;
            }

            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null)
            {
                _layerOwnership.Remove(sceneObject.Id);
                continue;
            }

            var data = SceneComponentTypes.ReadVoxelModel(component);
            var layerPath = Path.GetFullPath(
                Path.Combine(project.RootPath, data.Model.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(layerPath)!);
            var layerWorld = new VoxelWorld();
            foreach (var coordinate in owned)
            {
                if (_world.TryGet(coordinate, out var value))
                {
                    layerWorld.Set(coordinate, value);
                }
            }

            LargeWorldFormat.Write(layerPath, layerWorld);
            component.Data = JsonSerializer.SerializeToElement(new
            {
                model = data.Model,
                voxelCount = layerWorld.OccupiedVoxelCount,
                part = string.IsNullOrWhiteSpace(data.Part) ? "分层对象" : data.Part,
            });
        }
    }

    /// <summary>
    /// 解析当前工作世界对应的资产相对路径，场景模型组件引用优先，缺失时回退到 Worlds/主世界.mvmodel
    /// </summary>
    private string ResolveCurrentWorldAssetPath(ProjectHandle project)
    {
        var referenced = FindReferencedModelPath(project, _sceneDocument, ".mvworld")
            ?? FindReferencedModelPath(project, _sceneDocument, ".mvmodel");
        return referenced is not null ? ToAssetRelativePath(project, referenced) : "Assets/Worlds/主世界.mvmodel";
    }

    /// <summary>
    /// 把绝对模型路径换算成 Assets 打头的资产相对路径，供场景模型组件引用
    /// </summary>
    private static string ToAssetRelativePath(ProjectHandle project, string absolutePath)
    {
        var relative = Path.GetRelativePath(project.AssetsPath, absolutePath).Replace('\\', '/');
        return "Assets/" + relative;
    }

    /// <summary>
    /// 把场景中的模型组件引用指向指定世界资产，保证保存与重新打开都作用于同一文件
    /// </summary>
    private void UpdateSceneModelReference(string assetRelativePath)
    {
        var document = _sceneDocument ??= new Scene.SceneDocument { Name = "主场景" };
        var graph = new Scene.SceneGraph(document);
        var modelObject = document.Objects.FirstOrDefault(item =>
            item.Components.Any(SceneComponentTypes.IsVoxelModel));
        modelObject ??= graph.Add("体素世界", transform: TransformComponent.FromGrid(GridTransform.Identity));
        var modelComponent = modelObject.Components.FirstOrDefault(component =>
            SceneComponentTypes.IsVoxelModel(component));
        var data = new
        {
            model = assetRelativePath,
            voxelCount = _world.OccupiedVoxelCount,
            part = "场景模型",
        };
        if (modelComponent is null)
        {
            modelObject.Components.Add(new Scene.ComponentRecord
            {
                TypeId = SceneComponentTypes.VoxelModel,
                SchemaVersion = 1,
                Data = JsonSerializer.SerializeToElement(data),
            });
        }
        else
        {
            modelComponent.Data = JsonSerializer.SerializeToElement(data);
        }

        _sceneDocument = document;
    }

    /// <summary>
    /// 打开资产浏览器中指定的世界文件作为当前工作世界，导入与双击均走此入口
    /// </summary>
    private void OpenWorldAsset(string relativePath)
    {
        if (_project is null) return;
        var absolute = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolute))
        {
            SetStatus($"世界文件不存在：{relativePath}");
            return;
        }

        if (_sceneDocumentDirty) SaveProject();
        try
        {
            _world = LargeWorldFormat.Read(absolute);
            var primaryAssetPath = "Assets/" + relativePath.Replace('\\', '/');
            UpdateSceneModelReference(primaryAssetPath);
            // 切换世界是新的工作上下文：清空分层所有权，并移除旧世界遗留的分层引用
            _layerOwnership.Clear();
            var removedLayers = _sceneDocument is null
                ? 0
                : ConsolidateSecondaryModelObjects(_sceneDocument, primaryAssetPath);
            _sceneDocumentDirty = false;
            if (_sceneDocument is not null)
            {
                Scene.SceneSerializer.Save(
                    Path.Combine(_project.AssetsPath, "Scenes", "主场景.mvox"), _sceneDocument);
            }

            _voxelHistory.Clear();
            _selectedVoxel = null;
            _pipeline?.SyncWorld(_world, GetSceneGpuCellBudget());
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            FrameCameraOnWorld();
            SetStatus($"已打开世界：{Path.GetFileName(relativePath)}（{_world.OccupiedVoxelCount:N0} 体素）");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"打开世界失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 把场景中其他模型组件引用的世界合并进当前世界，分层对象由此获得可见体素
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <param name="primaryPath">主世界文件绝对路径，与它相同的引用不再合并</param>
    /// <returns>成功合并的对象数量</returns>
    private int MergeSceneModelWorlds(ProjectHandle project, string primaryPath)
    {
        if (_sceneDocument is null) return 0;
        var mergedCount = 0;
        var mergedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
            {
                var model = SceneComponentTypes.ReadVoxelModel(component).Model;
                if (string.IsNullOrWhiteSpace(model)) continue;
                var absolute = Path.GetFullPath(
                    Path.Combine(project.RootPath, model.Replace('/', Path.DirectorySeparatorChar)));
                if (string.Equals(absolute, primaryPath, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(absolute) || !mergedPaths.Add(absolute))
                {
                    continue;
                }

                try
                {
                    var layerWorld = LargeWorldFormat.Read(absolute);
                    var owned = new HashSet<VoxelCoord64>();
                    foreach (var item in layerWorld.EnumerateOccupied())
                    {
                        _world.Set(item.Coordinate, item.Value);
                        owned.Add(item.Coordinate);
                    }

                    _layerOwnership[sceneObject.Id] = owned;
                    mergedCount++;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                {
                    MVoxelLog.Warn($"分层对象读取失败，已跳过：{sceneObject.Name}");
                }
            }
        }

        return mergedCount;
    }

    /// <summary>
    /// 移除主世界之外模型对象上的模型组件，供切换世界时清理旧上下文的分层引用
    /// </summary>
    /// <param name="document">待整理的场景文档</param>
    /// <param name="primaryAssetPath">主世界资产的相对路径</param>
    /// <returns>移除的模型组件数量</returns>
    private static int ConsolidateSecondaryModelObjects(Scene.SceneDocument document, string primaryAssetPath)
    {
        var removed = 0;
        for (var objectIndex = document.Objects.Count - 1; objectIndex >= 0; objectIndex--)
        {
            var sceneObject = document.Objects[objectIndex];
            for (var componentIndex = sceneObject.Components.Count - 1; componentIndex >= 0; componentIndex--)
            {
                var component = sceneObject.Components[componentIndex];
                if (!SceneComponentTypes.IsVoxelModel(component)) continue;
                var model = SceneComponentTypes.ReadVoxelModel(component).Model;
                if (string.Equals(model, primaryAssetPath, StringComparison.OrdinalIgnoreCase)) continue;
                sceneObject.Components.RemoveAt(componentIndex);
                removed++;
            }

            if (sceneObject.Components.Count == 0)
            {
                document.Objects.RemoveAt(objectIndex);
            }
        }

        return removed;
    }

    // GPU 视口

    private VoxelRenderParameters.FrameState BuildFrameState(SceneCamera camera, Vector2 viewLogical,
        float deviceScale, int widthOverride, int heightOverride, bool pathMode, bool outlines,
        bool showGrid = false, float gridStep = 0F)
    {
        NumVector3 Convert(Vector3 value) => new(value.X, value.Y, value.Z);
        var width = Math.Max(16, widthOverride);
        var height = Math.Max(16, heightOverride);
        return new VoxelRenderParameters.FrameState(
            Convert(camera.Position), Convert(camera.Forward), Convert(camera.Right), Convert(camera.Up),
            SceneProjection.FocalLength(viewLogical, camera) * deviceScale,
            width, height,
            FrameIndex: (int)(Environment.TickCount & 0x3FFFFFFF),
            _pipeline?.RegionMin ?? default,
            _pipeline?.RegionSize ?? default,
            _pipeline?.StepBudget ?? 8192,
            _pipeline?.EmissiveLightCount ?? 0,
            HuePhaseDegrees: _renderSettings.HueCycle ? _elapsedSeconds * _renderSettings.HueCycleSpeed % 360F : 0F,
            ShowOutlines: outlines,
            AccumulatedSamples: _pipeline?.AccumulatedSamples ?? 0,
            PathMode: pathMode,
            GridStep: gridStep,
            ShowGrid: showGrid)
        {
            NearClip = camera.NearClip,
            FarClip = float.IsFinite(camera.FarClip) ? camera.FarClip : 100_000_000F,
        };
    }

    /// <summary>
    /// 通过 GPU 工作集绘制场景标签页
    /// </summary>
    private bool TryRenderGpuSceneView(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_gpuRenderingDisabled || _pipeline is not { IsAvailable: true } pipeline) return false;
        try
        {
            pipeline.SyncWorld(_world, GetSceneGpuCellBudget());
            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var frame = BuildFrameState(camera, size, scale, width, height, pathMode: false, outlines: _showVoxelOutlines,
                showGrid: _showGrid, gridStep: SceneGrid.Spacing(_sceneCameraDistance, size));
            // 场景只显示体素原色，完整光照和路径追踪仅由渲染窗口执行
            if (!pipeline.RenderSceneView(CurrentSceneRenderSettings, frame, width, height)) return false;
            drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                Vector2.Zero, pipeline.DisplayUvMax);
            return true;
        }
        catch (Exception exception)
        {
            MVoxelLog.SetRuntimeContext(
                $"Vulkan 场景绘制失败，设备={_vulkan?.DeviceName ?? "未知"}，视口={size.X:0}x{size.Y:0}，" +
                $"体素={_world.OccupiedVoxelCount}，管线={pipeline.ShaderLog ?? "无诊断信息"}");
            DisableGpuRendering(exception);
            return false;
        }
    }

    /// <summary>
    /// 只有光照、几何体和相机变化会使累积失效
    /// 后期处理变化直接重新呈现，不重启路径追踪
    /// </summary>
    private string TraceSignature(Vector2 size, float deviceScale)
    {
        var settings = _renderSettings;
        var buffer = new StringBuilder()
            .Append(size.X.ToString("F1")).Append('x').Append(size.Y.ToString("F1")).Append('@').Append(deviceScale.ToString("F2")).Append('|')
            .Append(_world.ContentVersion).Append('|')
            .Append(settings.SunAzimuthDegrees).Append(',').Append(settings.SunElevationDegrees).Append(',')
            .Append(settings.SunIntensity).Append(',').Append(settings.ShadowSoftness).Append(',').Append(settings.ShadowSamples)
            .Append(',').Append(settings.SunColor.X).Append(',').Append(settings.SunColor.Y).Append(',').Append(settings.SunColor.Z)
            .Append(',').Append(settings.SunEnabled).Append(',').Append(settings.ShowGround).Append(',').Append(settings.GroundFade)
            .Append(',').Append(settings.GroundColor.X).Append(',').Append(settings.GroundColor.Y).Append(',').Append(settings.GroundColor.Z)
            .Append(',').Append(settings.NearFade).Append(',').Append(settings.SkyTop.X).Append(',').Append(settings.SkyTop.Y)
            .Append(',').Append(settings.SkyTop.Z).Append(',').Append(settings.SkyHorizon.X).Append(',').Append(settings.SkyHorizon.Y)
            .Append(',').Append(settings.SkyHorizon.Z).Append(',').Append(settings.SkyAmbient).Append(',').Append(settings.AmbientTint)
            .Append(',').Append(settings.FogColor.X).Append(',').Append(settings.FogColor.Y).Append(',').Append(settings.FogColor.Z)
            .Append(',').Append(settings.FogDensity).Append(',').Append(settings.PathTracingEnabled)
            .Append(',').Append(settings.UseHardwareRayTracing)
            .Append(settings.MaxBounces).Append(',').Append(settings.PathRoughness).Append(',').Append(settings.PathMetalness)
            .Append(',').Append(settings.PathSpecularF0).Append(',').Append(settings.ResolutionScale)
            .Append(',').Append(settings.PathSamplesPerFrame).Append(',').Append(settings.PathMaxAccumulatedSamples)
            .Append(',').Append(settings.DepthOfField).Append(',').Append(settings.ApertureRadius).Append(',').Append(settings.FocusDistance)
            .Append(',').Append(settings.CameraFovDegrees).Append(',').Append(settings.EmissiveVoxels).Append(settings.EmissiveIntensity)
            .Append(',').Append(settings.DirectLightingMIS).Append(',').Append(settings.RussianRouletteThreshold)
            .Append(',').Append(settings.RadianceClamp).Append(',').Append(settings.TransmissiveAbsorption).Append(',').Append(settings.Backend)
            .Append(',').Append(settings.AmbientOcclusionEnabled).Append(',').Append(settings.AoStrength).Append(',').Append(settings.AoPower)
            .Append(',').Append(settings.PaletteShading).Append(',').Append(settings.Saturation).Append(',').Append(settings.HueShiftDegrees)
            .Append(',').Append(settings.ColorContrast).Append(',').Append(settings.Brightness).Append(',').Append(settings.Tone)
            .Append(',').Append(settings.Exposure).Append(',').Append(settings.Gamma).Append(',').Append(settings.WhiteBalance)
            .Append(',').Append(settings.Vignette).Append(',').Append(settings.Dither).Append(',').Append(settings.PathDenoising)
            .Append(',').Append(settings.PathDenoiseStrength)
            .Append(',').Append(settings.BloomEnabled).Append(',').Append(settings.BloomIntensity)
            .Append(',').Append(settings.BloomThreshold).Append(',').Append(settings.BloomRadius)
            .Append(',').Append(settings.SceneBackgroundColor.X).Append(',').Append(settings.SceneBackgroundColor.Y)
            .Append(',').Append(settings.SceneBackgroundColor.Z);
        if (GetActiveRenderCameraObject() is null)
        {
            buffer.Append("|scene:")
                .Append(_sceneOrbitYaw.ToString("F4")).Append(',')
                .Append(_sceneOrbitPitch.ToString("F4")).Append(',')
                .Append(_sceneCameraDistance.ToString("F3")).Append(',')
                .Append(_scenePan.X.ToString("F2")).Append(',').Append(_scenePan.Y.ToString("F2")).Append(',')
                .Append(_sceneFreeMoveHeight.ToString("F2")).Append(',')
                .Append(_sceneNearClip.ToString("F3")).Append(',').Append(_sceneFarClip.ToString("F1")).Append(',')
                .Append(_sceneFieldOfView.ToString("F1"));
        }
        else
        {
            buffer.Append('|').Append(RenderCameraSignature());
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 绘制 Vulkan Compute 渲染标签页，GPU 不可用时返回 false
    /// </summary>
    private bool TryRenderGpuPathView(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_gpuRenderingDisabled || _renderSettings.Backend == RenderBackend.Cpu ||
            _pipeline is not { IsAvailable: true } pipeline) return false;
        try
        {
            pipeline.SyncWorld(_world);
            var scale = MathF.Max(0.5F, ImGui.GetIO().DisplayFramebufferScale.X);
            var width = Math.Max(16, (int)(size.X * scale));
            var height = Math.Max(16, (int)(size.Y * scale));
            var signature = TraceSignature(size, scale);
            if (signature != _accumulationSignature)
            {
                _accumulationSignature = signature;
                pipeline.ResetAccumulation();
            }

            var navigating = ImGui.IsMouseDragging(ImGuiMouseButton.Right) ||
                             ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                             // 调整渲染配置时也必须使用轻量预览，否则每个滑块事件
                             // 都会同步等待完整路径追踪，造成控件看起来无法点击。
                             ImGui.IsAnyItemActive();
            // 交互时保留上一帧 GPU 结果，限制同步提交频率，避免每个鼠标事件都阻塞队列。
            // 这不是后端降级：下一次提交仍使用当前相机和同一条 Vulkan Compute 管线。
            var nowMs = Environment.TickCount64;
            if (navigating && pipeline.DisplayTextureId != IntPtr.Zero &&
                nowMs - _lastRenderGpuSubmitMs < 33)
            {
                drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                    Vector2.Zero, pipeline.DisplayUvMax);
                return true;
            }
            var renderSettings = navigating ? _renderSettings.CreateInteractionPreview() : _renderSettings;
            var frame = BuildFrameState(camera, size, scale, width, height, pathMode: true, outlines: false);
            // 首次进入渲染页没有可呈现的纹理，必须先提交一次，而不能直接走 Present。
            var firstSubmit = _renderViewNeedsInitialRender || _lastRenderGpuSubmitMs == 0 ||
                              pipeline.DisplayTextureId == IntPtr.Zero;
            var wantTrace = (firstSubmit || _settingsTogglePath) &&
                            pipeline.AccumulatedSamples < renderSettings.PathMaxAccumulatedSamples;
            if (wantTrace)
            {
                var submitted = _settingsTogglePath
                    ? pipeline.RenderPathView(renderSettings, frame, width, height)
                    : pipeline.RenderSceneView(renderSettings, frame, width, height);
                if (!submitted)
                {
                    return false;
                }
            }
            else if (!pipeline.Present(renderSettings, frame, width, height))
            {
                return false;
            }

            _lastRenderGpuSubmitMs = nowMs;
            if (wantTrace)
            {
                _renderViewNeedsInitialRender = false;
            }

            drawList.AddImage(pipeline.DisplayTextureId, origin, origin + size,
                Vector2.Zero, pipeline.DisplayUvMax);
            return true;
        }
        catch (Exception exception)
        {
            DisableGpuRendering(exception);
            return false;
        }
    }

    /// <summary>
    /// 记录 GPU 设备异常并安全切换到 CPU 预览
    /// </summary>
    /// <param name="exception">导致 GPU 管线不可用的异常</param>
    private void DisableGpuRendering(Exception exception)
    {
        if (_gpuRenderingDisabled) return;
        _gpuRenderingDisabled = true;
        _pipelineStatus = "显卡设备不可用，当前使用 CPU 预览";
        MVoxelLog.Error("Vulkan 体素渲染失败，已切换 CPU 预览", exception);
        try
        {
            _pipeline?.Dispose();
        }
        catch (Exception disposeException)
        {
            MVoxelLog.Error("释放故障 Vulkan 资源时发生异常", disposeException);
        }

        _pipeline = null;
    }

    // 体素笔刷工具

    private VoxelDda.Hit? PickVoxel(Vector2 screenPixel, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var ray = SceneProjection.RayFromScreen(screenPixel, origin, size, camera);
        var origin3 = new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
        var direction3 = new NumVector3(ray.X, ray.Y, ray.Z);
        return VoxelDda.RayCast(_world, origin3, direction3, _sceneCameraDistance * 8F + 256F);
    }

    private void HandleVoxelToolInput(Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var io = ImGui.GetIO();
        if (_welcomeWindow?.IsOpen == true || _projectOperation.IsRunning || !IsFocused || IsVoxelWorldLocked())
        {
            _brushPreview = null;
            return;
        }

        var mouse = io.MousePos;
        if (!ImGui.IsWindowHovered() || mouse.X < origin.X || mouse.X > origin.X + size.X ||
            mouse.Y < origin.Y || mouse.Y > origin.Y + size.Y)
        {
            _brushPreview = null;
            return;
        }

        var capsLockMode = IsCapsLockEnabled();
        var shiftPressed = IsShiftPressed();
        if (_activeTool == EditorTool.Pick && !capsLockMode && !shiftPressed)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _selectedVoxel = PickVoxel(mouse, origin, size, camera);
                var modelObject = GetVoxelWorldObject();
                _selectedSceneObjectId = _selectedVoxel is not null && modelObject is not null
                    ? modelObject.Id
                    : null;
                SetStatus(_selectedVoxel is { } picked
                    ? $"已选择体素 ({picked.Coordinate.X}, {picked.Coordinate.Y}, {picked.Coordinate.Z})"
                    : "未点中体素");
            }

            return;
        }

        var leftAction = ViewportInputPolicy.Resolve(EditorMouseButton.Left, shiftPressed, capsLockMode,
            _editorSettings.McMode);
        var rightAction = ViewportInputPolicy.Resolve(EditorMouseButton.Right, shiftPressed, capsLockMode,
            _editorSettings.McMode);
        if (_activeTool is EditorTool.Line or EditorTool.Box &&
            HandleDragShapeToolInput(mouse, origin, size, camera, leftAction, rightAction))
        {
            return;
        }

        if (_activeTool == EditorTool.Fill &&
            HandleFillToolInput(mouse, origin, size, camera, leftAction, rightAction))
        {
            _brushPreview = null;
            return;
        }

        var handled = false;
        handled |= ApplyMouseVoxelAction(leftAction, ImGuiMouseButton.Left, mouse, origin, size, camera);
        handled |= ApplyMouseVoxelAction(rightAction, ImGuiMouseButton.Right, mouse, origin, size, camera);
        if (!handled)
        {
            _brushPreview = null;
        }
    }

    private bool ApplyMouseVoxelAction(ViewportMouseAction action, ImGuiMouseButton button,
        Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (action is ViewportMouseAction.None or ViewportMouseAction.Orbit ||
            !ImGui.IsMouseClicked(button))
        {
            return false;
        }

        var erase = action == ViewportMouseAction.Destroy;
        var hit = PickVoxel(mouse, origin, size, camera);
        if (hit is null)
        {
            var ray = SceneProjection.RayFromScreen(mouse, origin, size, camera);
            var origin3 = new NumVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
            var direction3 = new NumVector3(ray.X, ray.Y, ray.Z);
            if (!erase &&
                VoxelDda.TryGroundHit(origin3, direction3, _sceneCameraDistance * 8F + 256F, out var groundPoint))
            {
                var cells = VoxelBrush.CellsForGround(groundPoint, _brushShape, _brushRadius);
                CommitStroke(cells, erase: false);
            }

            _brushPreview = null;
            return true;
        }

        _brushPreview = hit;
        _brushPreviewErase = erase;
        ApplyStroke(hit.Value, erase);
        return true;
    }

    private void ApplyStroke(VoxelDda.Hit hit, bool erase)
    {
        var radius = erase ? Math.Max(1, (_brushRadius + 1) / 2) : _brushRadius;
        var cells = erase
            ? VoxelBrush.CellsAround(hit.Coordinate, _brushShape, radius)
            : VoxelBrush.CellsForStroke(hit, erase: false, _brushShape, radius);
        CommitStroke(cells, erase);
    }

    private void CommitStroke(IReadOnlyList<VoxelCoord64> cells, bool erase)
    {
        if (cells.Count == 0) return;
        CommitCells(erase ? "擦除体素" : "绘制体素", cells, CurrentBrushValue(erase));
    }

    /// <summary>
    /// 按当前调色板与发光开关计算笔刷值
    /// </summary>
    private VoxelValue CurrentBrushValue(bool erase)
    {
        if (erase) return default;
        var paletteColor = _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
        var flags = (byte)((_brushEmissive ? VoxelFlags.Emissive : VoxelFlags.None) |
                           (paletteColor.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        return new VoxelValue(paletteColor, Flags: flags);
    }

    /// <summary>
    /// 提交一组格子为一次可撤销事务。不在此处立即重建 GPU 工作集：
    /// 场景视图与渲染视图每帧各自按预算同步，同帧多笔与撤销连发自动合并，
    /// 避免拖动一次触发多次全量重建
    /// </summary>
    private void CommitCells(string name, IReadOnlyList<VoxelCoord64> cells, VoxelValue value)
    {
        if (cells.Count == 0) return;
        var command = new VoxelStrokeCommand(name, _world, cells, value);
        _voxelHistory.Execute(command);
        if (command.ChangedCellCount == 0)
        {
            _voxelHistory.Undo();
            _voxelHistory.Clear();
            return;
        }

        _pipeline?.ResetAccumulation();
    }

    /// <summary>
    /// 直线与长方体工具的拖动状态机：按下取锚点，拖动更新终点预览，松开一次性提交
    /// </summary>
    private bool HandleDragShapeToolInput(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        ViewportMouseAction leftAction, ViewportMouseAction rightAction)
    {
        var place = leftAction == ViewportMouseAction.Place;
        var destroy = !place && rightAction == ViewportMouseAction.Destroy;
        if (!place && !destroy)
        {
            return false;
        }

        var button = place ? ImGuiMouseButton.Left : ImGuiMouseButton.Right;
        var ioState = ImGui.GetIO();
        if (ioState.MouseClicked[(int)button])
        {
            var hit = PickVoxel(mouse, origin, size, camera);
            if (hit is null)
            {
                return true;
            }

            var cell = destroy ? hit.Value.Coordinate : hit.Value.Coordinate + hit.Value.Normal;
            _toolDragAnchor = cell;
            _toolDragCurrent = cell;
            _toolDragErase = destroy;
            _brushPreview = null;
        }
        else if (ImGui.IsMouseDown(button) && _toolDragAnchor is not null)
        {
            var hit = PickVoxel(mouse, origin, size, camera);
            if (hit is not null)
            {
                _toolDragCurrent = _toolDragErase ? hit.Value.Coordinate : hit.Value.Coordinate + hit.Value.Normal;
            }
        }
        else if (ioState.MouseReleased[(int)button] && _toolDragAnchor is { } anchor)
        {
            var to = _toolDragCurrent ?? anchor;
            var cells = _activeTool == EditorTool.Line
                ? VoxelEditingTools.LineCells(anchor, to)
                : VoxelEditingTools.BoxCells(anchor, to, hollow: false);
            CommitCells(_activeTool == EditorTool.Line
                ? (_toolDragErase ? "擦除直线" : "绘制直线")
                : (_toolDragErase ? "擦除长方体" : "绘制长方体"), cells, CurrentBrushValue(_toolDragErase));
            _toolDragAnchor = null;
            _toolDragCurrent = null;
        }

        return true;
    }

    /// <summary>
    /// 洪水填充：点击起点格扩散同值连通域，命中预算上限时进入二次确认弹窗
    /// </summary>
    private bool HandleFillToolInput(Vector2 mouse, Vector2 origin, Vector2 size, SceneCamera camera,
        ViewportMouseAction leftAction, ViewportMouseAction rightAction)
    {
        var place = leftAction == ViewportMouseAction.Place;
        var destroy = !place && rightAction == ViewportMouseAction.Destroy;
        if (!place && !destroy)
        {
            return false;
        }

        if (!ImGui.IsMouseClicked(place ? ImGuiMouseButton.Left : ImGuiMouseButton.Right))
        {
            return true;
        }

        var hit = PickVoxel(mouse, origin, size, camera);
        if (hit is null)
        {
            SetStatus("填充需要点中已有体素");
            return true;
        }

        var cells = VoxelEditingTools.FloodFillCells(_world, hit.Value.Coordinate, MaximumFloodCells, out var truncated);
        var value = CurrentBrushValue(destroy);
        if (truncated)
        {
            _pendingFloodCells = cells;
            _pendingFloodValue = value;
            _pendingFloodOpen = true;
            SetStatus($"填充区域达到 {MaximumFloodCells:N0} 格上限，等待二次确认");
            return true;
        }

        CommitCells(destroy ? "擦除填充" : "洪水填充", cells, value);
        SetStatus($"填充 {cells.Count:N0} 格");
        return true;
    }

    /// <summary>
    /// 单次洪水填充的最大格子数
    /// </summary>
    private const int MaximumFloodCells = 2_000_000;

    /// <summary>
    /// 重新加载项目 Materials 目录并上传材质槽表到 GPU
    /// </summary>
    private void ReloadMaterialTable()
    {
        if (_project is null) return;
        try
        {
            var materials = Project.MaterialAsset.LoadFromProject(_project);
            _pipeline?.SetMaterialTable(Project.MaterialAsset.PackTable(materials));
            if (materials.Count > 0)
                MVoxelLog.Info($"已加载 {materials.Count} 个材质槽定义");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("材质表加载失败", exception);
            SetStatus($"材质表加载失败：{exception.Message}");
        }
    }

    private Project.MaterialAsset? _editingMaterial;
    private string _editingMaterialPath = string.Empty;

    /// <summary>
    /// 选中 .material 资产时在检查器绘制材质编辑面板，保存后即时重载 GPU 材质表
    /// </summary>
    private bool TryDrawSelectedMaterialInspector()
    {
        var relativePath = _assetBrowser.SelectedAssetPath;
        if (_project is null || !relativePath.EndsWith(".material", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return false;

        if (!string.Equals(_editingMaterialPath, path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _editingMaterial = Project.MaterialAsset.Load(path);
                _editingMaterialPath = path;
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                SetStatus($"材质文件读取失败：{exception.Message}");
                return true;
            }
        }

        var material = _editingMaterial!;
        ImGui.Text("材质");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        var name = material.Name;
        if (ImGui.InputText("名称", ref name, 48)) material.Name = name;
        var slot = material.Slot;
        if (ImGui.InputInt("槽位", ref slot)) material.Slot = Math.Clamp(slot, 0, Project.MaterialAsset.TableSlots - 1);

        DrawFollowableSlider("粗糙度", "粗糙度##mat", 0.01F, 1F, material.Roughness, value => material.Roughness = value);
        DrawFollowableSlider("金属度", "金属度##mat", 0F, 1F, material.Metallic, value => material.Metallic = value);
        DrawFollowableSlider("发光倍率", "发光倍率##mat", 0F, 32F, material.EmissiveScale, value => material.EmissiveScale = value);
        DrawFollowableSlider("吸收步长", "吸收步长##mat", 0F, 8F, material.AbsorptionStep, value => material.AbsorptionStep = value);

        var absorption = material.AbsorptionColor ?? new Rgba32(0, 0, 0);
        var absorptionColor = new System.Numerics.Vector3(absorption.R / 255F, absorption.G / 255F, absorption.B / 255F);
        var followAbsorption = material.AbsorptionColor is null;
        if (ImGui.Checkbox("吸收色跟随体素基础色", ref followAbsorption))
            material.AbsorptionColor = followAbsorption ? null : absorption;
        if (!followAbsorption && ImGui.ColorEdit3("吸收色", ref absorptionColor))
            material.AbsorptionColor = new Rgba32(
                (byte)Math.Round(Math.Clamp(absorptionColor.X, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(absorptionColor.Y, 0F, 1F) * 255F),
                (byte)Math.Round(Math.Clamp(absorptionColor.Z, 0F, 1F) * 255F));

        ImGui.Spacing();
        if (ImGui.Button("保存材质", new Vector2(120F, 30F)))
        {
            try
            {
                material.Save(path);
                ReloadMaterialTable();
                SetStatus($"材质已保存并生效：{material.Name}（槽位 {material.Slot}）");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"材质保存失败：{exception.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("重新读取"))
        {
            _editingMaterial = null;
            _editingMaterialPath = string.Empty;
        }

        return true;
    }

    /// <summary>
    /// 选中 .hdr 环境资产时在检查器提供设为/清除环境贴图操作
    /// </summary>
    private bool TryDrawSelectedEnvironmentInspector()
    {
        var relativePath = _assetBrowser.SelectedAssetPath;
        if (_project is null || !relativePath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return false;

        ImGui.Text("环境贴图");
        ImGui.Separator();
        ImGui.TextWrapped(Path.GetFileName(path));
        ImGui.TextDisabled($"大小  {new FileInfo(path).Length / 1024F:0.0} KB");
        if (ImGui.Button("设为环境贴图", new Vector2(150F, 30F)))
        {
            try
            {
                var pixels = Voxels.RadianceImage.Decode(path, out var envWidth, out var envHeight);
                _pipeline?.SetEnvironment(pixels, envWidth, envHeight);
                SetStatus($"环境贴图已生效：{envWidth}×{envHeight}");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                SetStatus($"环境贴图加载失败：{exception.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清除环境贴图", new Vector2(120F, 30F)))
        {
            _pipeline?.SetEnvironment([], 0, 0);
            SetStatus("已恢复程序化天空");
        }

        return true;
    }

    /// <summary>
    /// 绘制带“跟随全局”开关的浮点滑条
    /// </summary>
    private static void DrawFollowableSlider(string followLabel, string sliderLabel, float minimum, float maximum,
        float? current, Action<float> apply)
    {
        var follow = current is null;
        if (ImGui.Checkbox(followLabel + "跟随全局##follow", ref follow))
        {
            if (!follow) apply(Math.Clamp(current ?? (minimum + maximum) * 0.5F, minimum, maximum));
            else apply(float.NaN);
        }
        // apply(float.NaN) 由 Normalize 的 ClampOrFollow 转回跟随全局
        if (!follow)
        {
            var value = current ?? (minimum + maximum) * 0.5F;
            if (ImGui.SliderFloat(sliderLabel, ref value, minimum, maximum)) apply(value);
        }
    }

    private void HandleVoxelShortcuts()
    {
        var io = ImGui.GetIO();
        var scheme = _editorSettings.ActiveScheme();
        var undoDown = ShortcutBindingRouter.IsDown(scheme, "edit.undo");
        if (io.WantTextInput)
        {
            _undoHeldSeconds = 0F;
            _undoRepeatAccumulator = 0F;
            return;
        }
        else if (undoDown)
        {
            if (ShortcutBindingRouter.IsPressed(scheme, "edit.undo"))
            {
                ApplyUndo();
                _undoHeldSeconds = 0F;
                _undoRepeatAccumulator = 0F;
            }
            else
            {
                _undoHeldSeconds += Math.Max(0F, io.DeltaTime);
                var delay = _editorSettings.UndoRepeatDelayMs / 1000F;
                if (_undoHeldSeconds >= delay)
                {
                    _undoRepeatAccumulator += Math.Max(0F, io.DeltaTime);
                    var interval = _editorSettings.UndoRepeatIntervalMs / 1000F;
                    while (_undoRepeatAccumulator >= interval)
                    {
                        _undoRepeatAccumulator -= interval;
                        if (!ApplyUndo())
                        {
                            _undoRepeatAccumulator = 0F;
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            _undoHeldSeconds = 0F;
            _undoRepeatAccumulator = 0F;
            if (ShortcutBindingRouter.IsPressed(scheme, "edit.redo")) ApplyRedo();
        }

        if (ShortcutBindingRouter.IsPressed(scheme, "project.new")) ShowProjectPathDialog(create: true);
        if (ShortcutBindingRouter.IsPressed(scheme, "project.open")) ShowProjectPathDialog(create: false);
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.camera")) _activeTool = EditorTool.Camera;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.draw")) _activeTool = EditorTool.Draw;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.erase")) _activeTool = EditorTool.Erase;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.toggleGrid")) _showGrid = !_showGrid;
        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.togglePanels"))
        {
            var visible = _showHierarchy && _showProjectAssets && _showInspector;
            _showHierarchy = !visible;
            _showProjectAssets = !visible;
            _showInspector = !visible;
        }

        if (ShortcutBindingRouter.IsPressed(scheme, "viewport.focus"))
        {
            var limits = GetSceneZoomLimits();
            _scenePan = Vector2.Zero;
            _sceneFreeMoveHeight = 0F;
            _sceneOrbitYaw = MathF.PI / 4F;
            _sceneOrbitPitch = 0.48F;
            _sceneCameraDistance = Math.Clamp(18F, limits.MinimumDistance, limits.MaximumDistance);
            _sceneZoom = 1F;
        }
    }

    /// <summary>
    /// 执行一次撤销；工作集由视图每帧按预算同步，这里只使累积失效
    /// </summary>
    private bool ApplyUndo()
    {
        if (!_voxelHistory.Undo()) return false;
        SetStatus($"已撤销（剩余 {_voxelHistory.UndoCount} 步）");
        _pipeline?.ResetAccumulation();
        return true;
    }

    /// <summary>
    /// 执行一次重做；工作集由视图每帧按预算同步，这里只使累积失效
    /// </summary>
    private bool ApplyRedo()
    {
        if (!_voxelHistory.Redo()) return false;
        SetStatus($"已重做（剩余 {_voxelHistory.RedoCount} 步）");
        _pipeline?.ResetAccumulation();
        return true;
    }

    private void DrawVoxelBrushPreview(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        if (_selectedVoxel is { } selected)
        {
            DrawCellWireframe(drawList, origin, size, camera, selected.Coordinate, 0.56F,
                ToPackedColor(Color.FromArgb(255, 255, 200, 60)), 2.2F);
        }

        if (_toolDragAnchor is { } anchor)
        {
            if (DrawCellWireframe(drawList, origin, size, camera, anchor, 0.52F,
                    ToPackedColor(Color.FromArgb(235, 93, 187, 255)), 1.6F))
            {
                var to = _toolDragCurrent ?? anchor;
                DrawCellWireframe(drawList, origin, size, camera, to, 0.52F,
                    ToPackedColor(Color.FromArgb(235, 93, 187, 255)), 1.6F);
            }
        }

        if (_brushPreview is not { } hit) return;
        var cell = _brushPreviewErase ? hit.Coordinate : hit.Coordinate + hit.Normal;
        DrawCellWireframe(drawList, origin, size, camera, cell, 0.52F,
            ToPackedColor(Color.FromArgb(_brushPreviewErase ? 210 : 235,
                _brushPreviewErase ? 255 : 93, _brushPreviewErase ? 112 : 187, _brushPreviewErase ? 120 : 255)), 1.6F);
    }

    /// <summary>
    /// 把一个格子画成线框立方体，全部角点都在相机后方时返回 false
    /// </summary>
    private static bool DrawCellWireframe(ImDrawListPtr drawList, Vector2 origin, Vector2 size, SceneCamera camera,
        VoxelCoord64 cell, float half, uint color, float thickness)
    {
        var center = new Vector3((float)cell.X + 0.5F, (float)cell.Y + 0.5F, (float)cell.Z + 0.5F);
        var corners = new[]
        {
            center + new Vector3(-half, -half, -half), center + new Vector3(half, -half, -half),
            center + new Vector3(half, half, -half), center + new Vector3(-half, half, -half),
            center + new Vector3(-half, -half, half), center + new Vector3(half, -half, half),
            center + new Vector3(half, half, half), center + new Vector3(-half, half, half),
        };
        var points = new Vector2[8];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!SceneProjection.Project(corners[index], origin, size, camera, out points[index], out _)) return false;
        }

        foreach (var (start, end) in new[]
                 {
                     (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
                 })
        {
            drawList.AddLine(points[start], points[end], color, thickness);
        }

        return true;
    }

    // 工具栏和色板

    private void DrawBrushToolbarRow()
    {
        var previousTool = _activeTool;
        ToolButton("相机", EditorTool.Camera);
        ImGui.SameLine();
        ToolButton("选择", EditorTool.Pick);
        ImGui.SameLine();
        ToolButton("绘制", EditorTool.Draw);
        ImGui.SameLine();
        ToolButton("擦除", EditorTool.Erase);
        ImGui.SameLine();
        ToolButton("直线", EditorTool.Line);
        ImGui.SameLine();
        ToolButton("长方体", EditorTool.Box);
        ImGui.SameLine();
        ToolButton("填充", EditorTool.Fill);
        DrawFloodConfirmModal();
        if (previousTool == _activeTool) return;
        SetStatus(_activeTool switch
        {
            EditorTool.Camera => "视口工具：左键放置 · 右键环绕 · Shift+左键破坏 · 中键平移",
            EditorTool.Pick => "选择工具：点击体素查看坐标与颜色（可取色）",
            EditorTool.Draw => "绘制工具：左键放置 · Shift+左键破坏",
            EditorTool.Line => "直线工具：按下取起点，拖动到终点松开，一次可撤销",
            EditorTool.Box => "长方体工具：按下取一个角，拖到对角松开",
            EditorTool.Fill => "填充工具：点击体素替换其同值连通域，超上限需确认",
            _ => "擦除工具：Shift+左键破坏",
        });
    }

    /// <summary>
    /// 洪水填充命中预算上限后的二次确认弹窗，确认应用截断结果或放弃
    /// </summary>
    private void DrawFloodConfirmModal()
    {
        if (_pendingFloodCells is null) return;
        if (_pendingFloodOpen)
        {
            ImGui.OpenPopup("洪水填充达到上限##flood_confirm");
            _pendingFloodOpen = false;
        }

        var cellCount = _pendingFloodCells.Count;
        ImGui.SetNextWindowSize(new Vector2(420F, 0F));
        if (ImGui.BeginPopupModal("洪水填充达到上限##flood_confirm", ref _pendingFloodOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"填充区域达到 {MaximumFloodCells:N0} 格安全上限，结果已被截断。");
            ImGui.Text($"已收集 {cellCount:N0} 格，是否应用截断结果？");
            if (ImGui.Button("应用截断结果"))
            {
                CommitCells("洪水填充（截断）", _pendingFloodCells, _pendingFloodValue);
                _pendingFloodCells = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                _pendingFloodCells = null;
                SetStatus("已取消本次填充，未改动任何体素");
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void ToolButton(string label, EditorTool tool)
    {
        var active = _activeTool == tool;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, ToColor(EditorPalette.Accent));
        if (ImGui.SmallButton(label) && !active) _activeTool = tool;
        if (active) ImGui.PopStyleColor();
    }

    private void DrawPaletteSelector()
    {
        const int columns = 16;
        var cellSize = Math.Clamp((ImGui.GetContentRegionAvail().X - 6F) / columns, 10F, 22F);
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < VoxelPalette.EntryCount; index++)
        {
            if (index % columns != 0) ImGui.SameLine(0, 1);
            ImGui.PushID(index);
            if (ImGui.InvisibleButton("##色板格", new Vector2(cellSize, cellSize)))
            {
                _paletteIndex = index;
                var picked = _palette[index];
                _brushEmissive = false;
                SetStatus($"画笔颜色 #{index} · RGB({picked.R},{picked.G},{picked.B}) · A{picked.A}");
            }

            ImGui.PopID();
            var position = ImGui.GetItemRectMin();
            var color = _palette[index];
            var min = position + new Vector2(1F, 1F);
            var max = position + new Vector2(cellSize - 1F, cellSize - 1F);
            if (index == _paletteIndex)
            {
                draw.AddRectFilled(position, position + new Vector2(cellSize, cellSize), ToPackedColor(EditorPalette.Accent), 3F);
            }

            if (color.A < 255)
            {
                draw.AddRectFilled(min, max, ToPackedColor(Color.FromArgb(255, 46, 52, 62)), 2F);
                draw.AddRectFilled(min, (min + max) * 0.5F, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
                draw.AddRectFilled((min + max) * 0.5F, max, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
            }
            else
            {
                draw.AddRectFilled(min, max, ToPackedColor(Color.FromArgb(color.A, color.R, color.G, color.B)), 2F);
            }
        }
    }

    // 设置面板

    private void DrawRenderSettingsPanel()
    {
        var settings = _renderSettings;
        if (_pipeline is not { IsAvailable: true })
        {
            var message = _pipeline?.GpuWorksetUploaded == true
                ? "显卡体素工作集已上传，显卡绘制管线仍在初始化，当前使用 CPU 预览。"
                : "显卡体素工作集尚未上传，当前使用 CPU 预览。";
            ImGui.TextColored(ToColor(EditorPalette.Accent), message);
            if (!string.IsNullOrEmpty(_pipelineStatus)) ImGui.TextWrapped(_pipelineStatus);
        }

        if (ImGui.Button("恢复默认参数"))
        {
            var defaults = new RenderSettings();
            if (_project is null)
                _editorSettings.RenderViewSettings = defaults;
            else
                _projectSettings.RenderSettings = defaults;
            _renderSettings = defaults;
            SyncSettingsToggles();
            _pipeline?.ResetAccumulation();
        }

        ImGui.SameLine();
        if (ImGui.Button("保存配置"))
        {
            try
            {
                if (_project is null) SaveEditorSettings();
                else SaveProjectSettings();
                SetStatus(_project is null ? "编辑器渲染设置已保存" : "项目渲染设置已保存");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"配置保存失败：{exception.Message}");
            }
        }

        DrawSceneSection(settings);
        DrawPaletteSection(settings);
        DrawPostSection(settings);
        DrawPathTraceSection(settings);
    }

    private static void DrawColorSlider(string label, ref NumVector3 linearColor)
    {
        var display = ToSrgbVector(linearColor);
        if (ImGui.ColorEdit3(label, ref display, ImGuiColorEditFlags.Float))
        {
            linearColor = ToLinearVector(display);
        }
    }

    private static NumVector3 ToSrgbVector(NumVector3 linear) => new(
        LinearToSrgbComponent(Math.Clamp(linear.X, 0F, 4F)),
        LinearToSrgbComponent(Math.Clamp(linear.Y, 0F, 4F)),
        LinearToSrgbComponent(Math.Clamp(linear.Z, 0F, 4F)));

    private static NumVector3 ToLinearVector(NumVector3 srgb) => new(
        SrgbToLinearComponent(Math.Clamp(srgb.X, 0F, 1F)),
        SrgbToLinearComponent(Math.Clamp(srgb.Y, 0F, 1F)),
        SrgbToLinearComponent(Math.Clamp(srgb.Z, 0F, 1F)));

    private static float LinearToSrgbComponent(float value) =>
        value <= 0.0031308F ? value * 12.92F : 1.055F * MathF.Pow(value, 1F / 2.4F) - 0.055F;

    private static float SrgbToLinearComponent(float value) =>
        value <= 0.04045F ? value / 12.92F : MathF.Pow((value + 0.055F) / 1.055F, 2.4F);

    private void DrawSceneSection(RenderSettings settings)
    {
        if (!ImGui.CollapsingHeader("光照 · 太阳与阴影", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Toggle("太阳平行光", ref _settingsToggleSun, value => settings.SunEnabled = value);
        SliderFloat("方位角##sun", ref settings.SunAzimuthDegrees, 0F, 360F, "%.0f°");
        SliderFloat("仰角##sun", ref settings.SunElevationDegrees, 2F, 90F, "%.0f°");
        SliderFloat("强度##sun", ref settings.SunIntensity, 0F, 6F, "%.2f");
        DrawColorSlider("颜色##sun", ref settings.SunColor);
        SliderFloat("阴影柔和度（半影锥）##sun", ref settings.ShadowSoftness, 0F, 0.3F, "%.3f");
        SliderInt("阴影采样数##sun", ref settings.ShadowSamples, 1, 8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 = 硬阴影；增大模拟太阳圆盘带来的柔和半影（每像素多次阴影射线）。");
        if (ImGui.CollapsingHeader("环境 · 天空与地面"))
        {
            Toggle("显示地面平面", ref _settingsToggleGround, value => settings.ShowGround = value);
            DrawColorSlider("地面颜色", ref settings.GroundColor);
            SliderFloat("地面远淡距离", ref settings.GroundFade, 8F, 1024F, "%.0f 格");
            DrawColorSlider("天顶色", ref settings.SkyTop);
            DrawColorSlider("地平线色", ref settings.SkyHorizon);
            SliderFloat("天空环境光##sky", ref settings.SkyAmbient, 0F, 1.5F, "%.2f");
            SliderFloat("暖色补光##sky", ref settings.AmbientTint, 0F, 0.8F, "%.2f");
        }

        if (ImGui.CollapsingHeader("接触阴影 AO"))
        {
            Toggle("角点环境光遮蔽", ref _settingsToggleAo, value => settings.AmbientOcclusionEnabled = value);
            SliderFloat("AO 强度", ref settings.AoStrength, 0F, 1F, "%.2f");
            SliderFloat("AO 对比", ref settings.AoPower, 0.3F, 3F, "%.2f");
        }

        if (ImGui.CollapsingHeader("雾与景深提示"))
        {
            DrawColorSlider("雾颜色", ref settings.FogColor);
            SliderFloat("雾浓度（0=关）", ref settings.FogDensity, 0F, 0.02F, "%.4f");
            SliderFloat("近处淡入距离（0=关）", ref settings.NearFade, 0F, 256F, "%.0f");
        }
    }

    private void DrawPaletteSection(RenderSettings settings)
    {
        if (!ImGui.CollapsingHeader("材质基础色与笔刷", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Toggle("调色板着色", ref _settingsTogglePalette, value => settings.PaletteShading = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("体素使用纯色，基础色来自当前调色板，不使用贴图");
        SliderFloat("饱和度", ref settings.Saturation, 0F, 2.5F, "%.2f");
        SliderFloat("色相偏移", ref settings.HueShiftDegrees, 0F, 360F, "%.0f°");
        SliderFloat("对比度", ref settings.ColorContrast, 0.3F, 2.5F, "%.2f");
        SliderFloat("明度", ref settings.Brightness, 0.2F, 2F, "%.2f");
        Toggle("色相环动画", ref _settingsToggleHueCycle, value => settings.HueCycle = value);
        SliderFloat("色相环速度##cycle", ref settings.HueCycleSpeed, 0F, 90F, "%.0f°/秒");
        ImGui.SeparatorText("笔刷");
        DrawBrushToolbarRow();
        var shape = _brushShape == VoxelBrushShape.Cube ? "立方" : "球";
        if (ImGui.BeginCombo("形状", shape))
        {
            foreach (var (label, value) in new[] { ("立方", VoxelBrushShape.Cube), ("球", VoxelBrushShape.Sphere) })
            {
                if (ImGui.Selectable(label, _brushShape == value)) _brushShape = value;
            }

            ImGui.EndCombo();
        }

        SliderInt("半径（1=单格）", ref _brushRadius, 1, 8);
        if (ImGui.Checkbox("自发光笔刷（提灯）", ref _brushEmissive))
        {
            SetStatus(_brushEmissive ? "新绘制的体素将带自发光标记" : "新绘制的体素为普通颜色");
        }

        ImGui.SeparatorText("色板（256 色）");
        DrawPaletteSelector();

        ImGui.SeparatorText("调色板管理");
        ImGui.TextDisabled(_paletteIsProjectLevel
            ? $"当前为项目级调色板：Assets/Palettes/{EditorPalettes.ProjectPaletteName}"
            : "当前为编辑器级调色板（未打开项目）");
        if (ImGui.Button("保存调色板到项目"))
        {
            if (_project is null)
            {
                SetStatus("未打开项目，无法写入项目级调色板");
            }
            else
            {
                SetStatus(EditorPalettes.SaveProjectPalette(_project, _palette)
                    ? "项目级调色板已保存"
                    : "项目级调色板保存失败");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("写回编辑器默认"))
        {
            SetStatus(EditorPalettes.SaveEditorPalette(_palette)
                ? "当前色板已写回编辑器级，成为新项目默认"
                : "编辑器级调色板写入失败");
        }

        ImGui.SameLine();
        if (ImGui.Button("重新载入"))
        {
            _palette = _paletteIsProjectLevel && _project is not null
                ? EditorPalettes.ResolveProjectPalette(_project)
                : EditorPalettes.ResolveEditorPalette();
            SetStatus("调色板已按当前层级重新载入");
        }
    }

    private void DrawPostSection(RenderSettings settings)
    {
        if (!ImGui.CollapsingHeader("色彩管理 · 色调映射", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var tone = (int)settings.Tone;
        if (ImGui.Combo("色调曲线", ref tone, "线性截断\0Reinhard\0ACES（Narkowicz）\0Filmic（HV）\0", 4))
        {
            settings.Tone = (ToneCurve)Math.Clamp(tone, 0, 3);
        }

        SliderFloat("曝光（EV）", ref settings.Exposure, -4F, 4F, "%.2f");
        SliderFloat("Gamma", ref settings.Gamma, 1.0F, 3.5F, "%.2f");
        SliderFloat("白平衡色温（K）", ref settings.WhiteBalance, 2000F, 12000F, "%.0f K");
        SliderFloat("暗角", ref settings.Vignette, 0F, 1F, "%.2f");
        Toggle("输出抖动（消除色带）", ref _settingsToggleDither, value => settings.Dither = value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("体素颜色按 sRGB 存储，渲染计算在线性空间进行");
    }

    private void DrawPathTraceSection(RenderSettings settings)
    {
        if (!ImGui.CollapsingHeader("渲染视图 · 渐进式路径追踪", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Toggle("启用路径追踪渲染", ref _settingsTogglePath, value =>
        {
            settings.PathTracingEnabled = value;
        });
        SliderInt("每帧样本数", ref settings.PathSamplesPerFrame, 1, 8);
        SliderInt("累积样本上限", ref settings.PathMaxAccumulatedSamples, 64, 8192, "%d spp");
        SliderFloat("内部分辨率倍率", ref settings.ResolutionScale, 0.25F, 1F, "%.2f");
        SliderInt("最大弹射次数", ref settings.MaxBounces, 1, 12);
        SliderFloat("光线终止阈值（RR）", ref settings.RussianRouletteThreshold, 0.0005F, 0.05F, "%.4f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("路径权重低于此值（约 1/255，低于输出 LSB）时提前终止并做无偏存活判定。");
        SliderFloat("辐射度截断（消防萤火虫）", ref settings.RadianceClamp, 1F, 200F, "%.1f");
        Toggle("快速降噪（渲染视图）", ref _settingsTogglePathDenoise, value => settings.PathDenoising = value);
        SliderFloat("降噪强度", ref settings.PathDenoiseStrength, 0F, 1F, "%.2f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在呈现通道对累积结果做保边缘邻域滤波，不增加路径追踪样本，导出画面也会使用该设置。");
        if (ImGui.CollapsingHeader("材质（全局）"))
        {
            SliderFloat("粗糙度（GGX）", ref settings.PathRoughness, 0.01F, 1F, "%.3f");
            SliderFloat("金属度", ref settings.PathMetalness, 0F, 1F, "%.2f");
            SliderFloat("介电基础反射 F0", ref settings.PathSpecularF0, 0F, 0.2F, "%.3f");
            SliderFloat("半透明吸收浓度", ref settings.TransmissiveAbsorption, 0F, 6F, "%.2f");
            SliderFloat("自发光强度（灯）", ref settings.EmissiveIntensity, 0F, 40F, "%.1f");
            Toggle("自发光体素参与光照", ref _settingsToggleEmissive, value => settings.EmissiveVoxels = value);
            Toggle("直接光 MIS 采样（太阳+灯）", ref _settingsToggleMis, value => settings.DirectLightingMIS = value);
        }

        if (ImGui.CollapsingHeader("渲染相机"))
        {
            SliderFloat("视场角 FOV##path", ref settings.CameraFovDegrees, 20F, 120F, "%.0f°");
            Toggle("景深", ref _settingsToggleDof, value => settings.DepthOfField = value);
            SliderFloat("光圈半径##dof", ref settings.ApertureRadius, 0F, 0.5F, "%.3f");
            SliderFloat("对焦距离##dof", ref settings.FocusDistance, 1F, 256F, "%.1f");
        }
    }

    private static void SliderFloat(string label, ref float value, float minimum, float maximum, string format)
    {
        ImGui.SetNextItemWidth(-1F);
        ImGui.SliderFloat(label, ref value, minimum, maximum, format);
    }

    private static void SliderInt(string label, ref int value, int minimum, int maximum)
    {
        ImGui.SetNextItemWidth(-1F);
        ImGui.SliderInt(label, ref value, minimum, maximum);
    }

    private static void SliderInt(string label, ref int value, int minimum, int maximum, string format)
    {
        ImGui.SetNextItemWidth(-1F);
        ImGui.SliderInt(label, ref value, minimum, maximum, format);
    }

    private static void Toggle(string label, ref bool storage, Action<bool> apply)
    {
        if (ImGui.Checkbox(label, ref storage)) apply(storage);
    }
}
