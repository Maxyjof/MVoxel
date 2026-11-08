using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Project;
using System.Text.Json;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private Dictionary<StableId, Dictionary<int, int>> _runtimeMaterialSlotsByObject = [];
    private IReadOnlyDictionary<int, MaterialAsset> _runtimeMaterials = new Dictionary<int, MaterialAsset>();
    private string _runtimeMaterialSignature = string.Empty;
    private VoxExportPlan? _pendingVoxExportPlan;
    private string _pendingVoxExportPath = string.Empty;
    private string _pendingVoxExportScope = string.Empty;
    private bool _openVoxExportConfirmation;
    private static readonly MaterialAsset MissingMaterialDiagnostic = new()
    {
        Name = "材质缺失诊断",
        Roughness = 0.35F,
        Metallic = 0F,
        EmissiveScale = 4F,
        AbsorptionColor = new Rgba32(255, 0, 255, 255),
        Opacity = 1F,
    };

    /// <summary>
    /// 运行时材质映射：场景对象的局部材质槽到 16 位运行时槽位的对应关系
    /// </summary>
    /// <param name="SlotsByObject">每个对象的局部槽到运行时槽映射</param>
    /// <param name="Materials">运行时槽位对应的材质资产</param>
    /// <param name="Signature">材质内容指纹，材质文件或绑定变化时改变</param>
    internal sealed record RuntimeMaterialMap(Dictionary<StableId, Dictionary<int, int>> SlotsByObject,
        Dictionary<int, MaterialAsset> Materials, string Signature);

    /// <summary>
    /// 运行时材质表刷新的最小间隔
    /// </summary>
    /// <remarks>
    /// 刷新材料表需要重建字典、反序列化每个对象的模型组件并查询材质文件时间，
    /// 视口每帧都做一遍属于纯浪费。按固定间隔节流后，材质修改最多延迟一个间隔生效，
    /// 相机导航期间不再把这份开销压进每一帧
    /// </remarks>
    private const long MaterialTableRefreshIntervalMs = 200;

    private long _runtimeMaterialRefreshAt;

    private readonly Dictionary<string, (long Length, long WriteTicks, MaterialExchangeMetadata Materials)> _nativeMaterialCache = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshRuntimeMaterialTable()
    {
        var now = Environment.TickCount64;
        if (now - _runtimeMaterialRefreshAt < MaterialTableRefreshIntervalMs)
        {
            return;
        }

        _runtimeMaterialRefreshAt = now;
        var embedded = _voxelStage?.Materials.Definitions.ToDictionary(
            pair => EmbeddedMaterialPrefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(pair.Key)),
            pair => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, MaterialAsset>(StringComparer.Ordinal);
        var defaults = new Dictionary<StableId, List<MaterialSlotBinding>>();
        if (_voxelStage is null && _project is not null && _sceneDocument is not null)
        {
            foreach (var (objectId, key) in GetObjectModelLoadKeys(_project, _sceneDocument))
            {
                try
                {
                    var file = new FileInfo(key.Path);
                    if (!_nativeMaterialCache.TryGetValue(key.Path, out var cached) ||
                        cached.Length != file.Length || cached.WriteTicks != file.LastWriteTimeUtc.Ticks)
                    {
                        cached = (file.Length, file.LastWriteTimeUtc.Ticks, key.SourceSegment.Length == 0
                            ? LargeWorldFormat.ReadMaterials(key.Path) : LargeWorldFormat.ReadObjects(key.Path).Materials);
                        _nativeMaterialCache[key.Path] = cached;
                    }
                    var sourceBindings = key.SourceSegment.Length != 0 &&
                        cached.Materials.ObjectBindings.TryGetValue(key.SourceSegment, out var objectBindings)
                        ? objectBindings : cached.Materials.Bindings;
                    foreach (var (slot, id) in sourceBindings)
                    {
                        if (!cached.Materials.Definitions.TryGetValue(id, out var material)) continue;
                        var virtualPath = EmbeddedMaterialPrefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(key.Path + "\0" + id));
                        embedded[virtualPath] = material;
                        if (!defaults.TryGetValue(objectId, out var bindings)) defaults[objectId] = bindings = [];
                        bindings.Add(new MaterialSlotBinding(slot, virtualPath));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { _nativeMaterialCache.Remove(key.Path); }
            }
        }
        ApplyRuntimeMaterialMap(BuildRuntimeMaterialMap(_sceneDocument, _brushMaterials,
            _project?.AssetsPath ?? string.Empty, embedded, defaults));
    }

    /// <summary>
    /// 从场景与材质资产构建运行时材质映射，不读写编辑器状态，可在后台线程调用
    /// </summary>
    /// <param name="scene">场景文档，可为空</param>
    /// <param name="materials">项目材质资产</param>
    /// <param name="assetsPath">项目资产根目录</param>
    /// <returns>槽位映射、运行时材质表与内容指纹</returns>
    private static RuntimeMaterialMap BuildRuntimeMaterialMap(SceneDocument? scene,
        IReadOnlyList<MaterialAsset> materials, string assetsPath,
        IReadOnlyDictionary<string, MaterialAsset>? embeddedMaterials = null,
        IReadOnlyDictionary<StableId, List<MaterialSlotBinding>>? modelDefaults = null)
    {
        var byPath = materials.ToDictionary(item => Path.GetFullPath(item.SourcePath), StringComparer.OrdinalIgnoreCase);
        var runtimeByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var objectSlots = new Dictionary<StableId, Dictionary<int, int>>();
        var runtimeMaterials = new Dictionary<int, MaterialAsset>();
        foreach (var sceneObject in scene?.Objects ?? [])
        {
            var component = sceneObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var localSlots = new Dictionary<int, int>();
            var explicitBindings = data.MaterialSlots ?? [];
            var explicitSlots = explicitBindings.Select(binding => binding.Slot).ToHashSet();
            var defaults = modelDefaults is not null && modelDefaults.TryGetValue(sceneObject.Id, out var modelBindings)
                ? modelBindings.Where(binding => !explicitSlots.Contains(binding.Slot)) : [];
            foreach (var binding in explicitBindings.Concat(defaults))
            {
                if (string.IsNullOrEmpty(assetsPath)) continue;
                var isEmbedded = binding.MaterialAsset.StartsWith(EmbeddedMaterialPrefix, StringComparison.Ordinal);
                var assetPath = isEmbedded ? binding.MaterialAsset : Path.GetFullPath(Path.Combine(assetsPath,
                    binding.MaterialAsset.Replace('/', Path.DirectorySeparatorChar)));
                var found = isEmbedded
                    ? embeddedMaterials is not null && embeddedMaterials.TryGetValue(assetPath, out _)
                    : byPath.ContainsKey(assetPath);
                var material = isEmbedded
                    ? embeddedMaterials?.GetValueOrDefault(assetPath)
                    : byPath.GetValueOrDefault(assetPath);
                if (!found || material is null)
                {
                    const string missingKey = "__mv_missing_material__";
                    if (!runtimeByPath.TryGetValue(missingKey, out var missingSlot))
                    {
                        missingSlot = runtimeByPath.Count + 1;
                        runtimeByPath[missingKey] = missingSlot;
                        runtimeMaterials[missingSlot] = MissingMaterialDiagnostic;
                    }
                    localSlots[binding.Slot] = missingSlot;
                    continue;
                }
                if (!runtimeByPath.TryGetValue(assetPath, out var runtimeSlot))
                {
                    runtimeSlot = runtimeByPath.Count + 1;
                    if (runtimeSlot >= MaterialAsset.TableSlots)
                        throw new InvalidOperationException("场景材质数量超过16位运行时容量，请减少同时使用的不同材质资产");
                    runtimeByPath.Add(assetPath, runtimeSlot);
                    runtimeMaterials[runtimeSlot] = material;
                }
                localSlots[binding.Slot] = runtimeSlot;
            }
            objectSlots[sceneObject.Id] = localSlots;
        }

        // 指纹里要能容纳“内联材质”：资产编辑空间导入的 .vox 材质没有独立文件路径，
        // 直接取文件时间会在空路径上抛异常，这里按来源分别取指纹
        var fingerprints = new List<string>(runtimeMaterials.Count);
        foreach (var item in runtimeMaterials.OrderBy(pair => pair.Key))
        {
            var asset = item.Value;
            if (embeddedMaterials is not null &&
                embeddedMaterials.Values.Any(value => ReferenceEquals(value, asset)))
            {
                fingerprints.Add($"{item.Key}:embedded:{System.Text.Json.JsonSerializer.Serialize(asset)}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(asset.SourcePath))
            {
                fingerprints.Add($"{item.Key}:inline:{System.Text.Json.JsonSerializer.Serialize(asset)}");
                continue;
            }

            fingerprints.Add(File.Exists(asset.SourcePath)
                ? $"{item.Key}:{asset.SourcePath}:{File.GetLastWriteTimeUtc(asset.SourcePath).Ticks}"
                : $"{item.Key}:missing:{asset.SourcePath}");
        }

        var signature = string.Join("|", fingerprints) +
            ";" + string.Join("|", objectSlots.OrderBy(item => item.Key.Value)
                .SelectMany(item => item.Value.OrderBy(slot => slot.Key)
                    .Select(slot => $"{item.Key}:{slot.Key}>{slot.Value}")));
        return new RuntimeMaterialMap(objectSlots, runtimeMaterials, signature);
    }

    /// <summary>
    /// 把运行时材质映射写入编辑器状态，内容指纹变化时让组合世界与累积样本失效
    /// </summary>
    /// <param name="map">构建好的运行时材质映射</param>
    private void ApplyRuntimeMaterialMap(RuntimeMaterialMap map)
    {
        _runtimeMaterialSlotsByObject = map.SlotsByObject;
        _runtimeMaterials = map.Materials;
        if (map.Signature == _runtimeMaterialSignature) return;
        _runtimeMaterialSignature = map.Signature;
        InvalidateComposedWorldCache();
        _pipeline?.SetMaterialTable(MaterialAsset.PackTable(map.Materials));
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
    }

    private VoxelValue RemapRuntimeMaterial(StableId objectId, VoxelValue value) =>
        RemapRuntimeMaterial(_runtimeMaterialSlotsByObject, _runtimeMaterials, objectId, value);

    /// <summary>
    /// 按运行时材质映射改写体素的材质槽，槽位未知时退化为无材质
    /// </summary>
    /// <param name="slotsByObject">每个对象的局部槽到运行时槽映射</param>
    /// <param name="objectId">体素所属对象</param>
    /// <param name="value">原始体素值</param>
    /// <returns>改写后的体素值</returns>
    private static VoxelValue RemapRuntimeMaterial(Dictionary<StableId, Dictionary<int, int>> slotsByObject,
        IReadOnlyDictionary<int, MaterialAsset> materials, StableId objectId, VoxelValue value)
    {
        if (value.MaterialSlot == 0) return value;
        var runtimeSlot = slotsByObject.TryGetValue(objectId, out var slots) &&
            slots.TryGetValue(value.MaterialSlot, out var mapped) ? mapped : (ushort)0;
        // 显式材质倍率决定发光；只有“跟随全局”材质兼容旧体素标记。
        var flags = value.Flags;
        if (materials.TryGetValue(runtimeSlot, out var material) && material.EmissiveScale is { } emission)
            flags = emission > 0F ? (byte)(flags | VoxelFlags.Emissive)
                : (byte)(flags & ~VoxelFlags.Emissive);
        return value with { MaterialSlot = (ushort)runtimeSlot, Flags = flags };
    }

    private bool _showModelingTools = true;
    private bool _resetDockLayoutRequested;
    private bool _focusModelingTools;
    private Action? _afterProjectClose;
    private bool _allowProjectReplacement;
    private bool _showRenderStatistics;

    private bool DeferProjectReplacement(Action continuation)
    {
        // 切换项目前必须先离开资产编辑空间，这里直接给出保存或放弃的出口
        if (IsAssetStage) { RequestAssetStageExit(); return true; }
        if (!_allowProjectReplacement && _project is not null && TimelineIsDirty)
        {
            ConfirmTimelineChangesBefore(continuation);
            return true;
        }
        if (_allowProjectReplacement || _project is null || (!_sceneDocumentDirty && !_generatorDirty)) return false;
        _afterProjectClose = continuation;
        _projectPathDialogMode = 0;
        RequestCloseProjectDialog();
        return true;
    }

    private void CompleteProjectClose()
    {
        var continuation = _afterProjectClose;
        _afterProjectClose = null;
        if (continuation is null) CloseProject();
        else
        {
            _allowProjectReplacement = true;
            try { continuation(); }
            finally { _allowProjectReplacement = false; }
        }
    }

    private void ShowSaveAsDialog()
    {
        if (_project is null) return;
        _projectPathDialogMode = 3;
        _projectPathText = Path.Combine(DefaultProjectRootPath, _project.Manifest.Name + " 副本");
        _projectPathError = string.Empty;
    }

    private void BeginSaveAsProject(string requestedPath)
    {
        if (_project is not { } source) return;
        try
        {
            var destination = Path.GetFullPath(requestedPath);
            var root = Path.TrimEndingDirectorySeparator(source.RootPath);
            if (destination.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("副本必须位于当前项目之外的独立文件夹。");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("目标文件夹已存在，请选择一个新位置，避免覆盖文件。");
            SaveProject();
            if (_generatorDirty) SaveGeneratorSource();
            if (_sceneDocumentDirty || _generatorDirty) return;
            if (!_projectOperation.TryStart("正在另存项目", "正在复制项目资产…",
                (cancellation, report) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    report(new BlockingOperationProgress("正在复制项目资产…", .1F, CanCancel: false));
                    Directory.CreateDirectory(destination);
                    foreach (var directory in new[] { "Assets", "ProjectSettings", "Plugins" })
                        CopyProjectDirectory(Path.Combine(root, directory), Path.Combine(destination, directory));
                    var manifest = new ProjectManifest { Name = Path.GetFileName(destination), EngineVersion = source.Manifest.EngineVersion };
                    AtomicFile.WriteText(Path.Combine(destination, ProjectLayout.ProjectFileName),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    var project = ProjectLayout.Open(destination);
                    var models = PreloadProjectModels(project, CancellationToken.None, report);
                    return new PreparedProjectLoad(project, models);
                }, prepared => OpenProject(prepared.Project, prepared.Models),
                exception => ShowProjectOperationError("另存项目失败（原项目保留）", exception, false, 3, requestedPath),
                () => SetStatus("已取消另存项目"))) return;
            _projectPathDialogMode = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _projectPathError = exception.Message;
        }
    }

    private static void CopyProjectDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"另存项目不跟随目录链接：{source}");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"另存项目不跟随文件链接：{file}");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyProjectDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private void DrawModelingTools()
    {
        var display = ImGui.GetIO().DisplaySize;
        var layout = GetLayoutMetrics(display);
        if (!_showModelingTools) return;
        SetPanelDefaults(new Vector2(display.X - layout.RightWidth, WorkspaceTop + 1F),
            new Vector2(layout.RightWidth, layout.MainHeight));
        if (_focusModelingTools) ImGui.SetNextWindowFocus();
        _focusModelingTools = false;
        if (ImGui.Begin("调色盘##建模工具窗口", PanelWindowFlags))
        {
            RegisterWindowGroup("palette", ClosePalettePanel);
            DrawPanelWindowMenu(ClosePalettePanel);
            DrawPaletteContents();
        }
        else if (ImGui.IsWindowCollapsed())
        {
            DrawPanelWindowMenu(ClosePalettePanel);
        }
        // 两个可见性标记保持一致，关闭后仍可从“视图”菜单重新打开
        if (!_showPaletteWindow) _showModelingTools = false;
        ImGui.End();
    }

    /// <summary>关闭调色盘：显隐标志必须成对复位，否则视图菜单会误判它仍在显示</summary>
    private void ClosePalettePanel()
    {
        _showPaletteWindow = false;
        _showModelingTools = false;
    }

    private void DrawPaletteContents() => DrawPaletteContentsCore();

    private void DrawBrushMaterialSelector() => DrawBrushMaterialSelectorCore();

    private void DrawModelMaterialSlots(SceneObject modelObject) => DrawModelMaterialSlotsCore(modelObject);

    private MaterialAsset? FindCurrentBrushMaterial()
    {
        EnsureModelBrushMaterialTarget();
        var component = FindBrushModelComponent();
        if (component is null || _brushMaterialSlot == 0) return null;
        var binding = (SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
            .FirstOrDefault(item => item.Slot == _brushMaterialSlot);
        return binding is { } selected ? FindMaterialAsset(selected.MaterialAsset) : null;
    }

    private MaterialAsset? FindMaterialAsset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _project is null) return null;
        if (relativePath.StartsWith(EmbeddedMaterialPrefix, StringComparison.Ordinal))
        {
            try
            {
                var id = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(relativePath[EmbeddedMaterialPrefix.Length..]));
                return _voxelStage?.Materials.Definitions.GetValueOrDefault(id);
            }
            catch (FormatException) { return null; }
        }
        return _brushMaterials.FirstOrDefault(item =>
            string.Equals(Path.GetRelativePath(_project.AssetsPath, item.SourcePath).Replace('\\', '/'),
                relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    private void SaveModelMaterialBindings(SceneObject modelObject, ComponentRecord component,
        VoxelModelComponentData data, Dictionary<int, MaterialSlotBinding> bindings)
    {
        //只写组件映射，不改体素内容或本地编号
        SceneComponentTypes.WriteVoxelModel(component, data with { MaterialSlots = bindings.Values.OrderBy(item => item.Slot).ToArray() });
        EnsureModelBrushMaterialTarget();
        MarkSceneDocumentDirty();
        InvalidateComposedWorldCache();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        SetStatus($"已更新“{modelObject.Name}”的材质槽位映射");
    }

    private MaterialExchangeMetadata? BuildNativeMaterialMetadata(SceneObject? target, bool selectedOnly)
    {
        if (_project is null) return null;
        var metadata = new MaterialExchangeMetadata();
        var objects = selectedOnly
            ? target is null ? [] : new[] { target }
            : (_sceneDocument?.Objects ?? []).Where(item => item.Components.Any(SceneComponentTypes.IsVoxelModel));
        foreach (var modelObject in objects)
        {
            var component = modelObject.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null) continue;
            var bindings = SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [];
            var targetBindings = selectedOnly ? metadata.Bindings :
                metadata.ObjectBindings[modelObject.Id.ToString()] = new Dictionary<ushort, string>();
            foreach (var binding in bindings)
            {
                var relative = binding.MaterialAsset.Replace('\\', '/');
                if (FindMaterialAsset(relative) is not { } material || binding.Slot <= 0) continue;
                targetBindings[(ushort)binding.Slot] = relative;
                metadata.Definitions[relative] = material;
            }
        }
        metadata.Validate();
        return metadata.Definitions.Count == 0 ? null : metadata;
    }

    private IReadOnlyDictionary<(int Part, ushort Slot), MaterialAsset> BuildVoxMaterialMap(SceneObject? target, bool selectedOnly)
    {
        var result = new Dictionary<(int Part, ushort Slot), MaterialAsset>();
        if (!selectedOnly || target is null) return result;
        var component = target.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return result;
        foreach (var binding in SceneComponentTypes.ReadVoxelModel(component).MaterialSlots ?? [])
        {
            if (binding.Slot <= 0 || FindMaterialAsset(binding.MaterialAsset) is not { } material) continue;
            result[(0, (ushort)binding.Slot)] = material;
        }
        return result;
    }

    private IReadOnlyList<LargeWorldFormat.NativeObjectSegment> BuildNativeObjectSegments()
    {
        var segments = new List<LargeWorldFormat.NativeObjectSegment>();
        foreach (var sceneObject in _sceneDocument?.Objects ?? [])
        {
            if (!sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel)) continue;
            var world = CaptureObjectVoxels(sceneObject);
            if (world.OccupiedVoxelCount == 0) continue;
            segments.Add(new LargeWorldFormat.NativeObjectSegment(sceneObject.Id.ToString(), sceneObject.Name,
                GetSceneModelOrigin(sceneObject), world));
        }
        return segments;
    }

    /// <summary>当前场景按对象拆成 VOX 导出段：保留各自的世界原点，导出时按分区合并</summary>
    private IReadOnlyList<VoxFormat.VoxScenePart> BuildSceneVoxParts()
    {
        var parts = new List<VoxFormat.VoxScenePart>();
        foreach (var sceneObject in _sceneDocument?.Objects ?? [])
        {
            if (!sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel)) continue;
            var world = CaptureObjectVoxels(sceneObject);
            if (world.OccupiedVoxelCount == 0) continue;
            parts.Add(new VoxFormat.VoxScenePart(sceneObject.Name, world, GetSceneModelOrigin(sceneObject)));
        }

        return parts;
    }

    private void DrawVoxExportConfirmation()
    {
        if (!_openVoxExportConfirmation || _pendingVoxExportPlan is null) return;
        if (!CenteredModal.Begin("VOX导出损失确认##VoxExport", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var summary = _pendingVoxExportPlan.LossSummary;
        ImGui.TextWrapped("当前VOX导出无法完整表达原生材质或体素属性");
        ImGui.Text($"材质组合：{summary.SourceCombinations}，调色板颜色：{summary.PaletteEntries}");
        ImGui.Text($"近似体素：{summary.ApproximatedVoxelCount:N0}，不可表达标志：{summary.FlagsNotRepresentableVoxelCount:N0}");
        if (summary.MissingMaterialDefinitions > 0) ImGui.TextColored(new Vector4(1F, .4F, .2F, 1F), $"缺失材质定义：{summary.MissingMaterialDefinitions}");
        foreach (var warning in summary.MaterialWarnings.Take(8)) ImGui.TextWrapped("· " + warning);
        ImGui.Separator();
        var confirm = ImGui.Button("确认有损导出");
        ImGui.SameLine();
        var cancel = ImGui.Button("取消") || ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        if (confirm)
        {
            var plan = _pendingVoxExportPlan;
            var path = _pendingVoxExportPath;
            _pendingVoxExportPlan = null;
            _openVoxExportConfirmation = false;
            var written = plan.Write(path, true);
            SetStatus(written.Count > 1
                ? $"已确认VOX有损导出 · {written.Count} 个文件：{Path.GetDirectoryName(written[0])}"
                : $"已确认VOX有损导出：{path}");
            ImGui.CloseCurrentPopup();
        }
        else if (cancel)
        {
            _pendingVoxExportPlan = null;
            _openVoxExportConfirmation = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ExportWorkingVoxels(bool selectedOnly, bool qubicle, bool native = false, bool obj = false)
    {
        if (_project is null) return;
        try
        {
            var target = _sceneDocument?.Objects.FirstOrDefault(item => item.Id == _selectedSceneObjectId &&
                item.Components.Any(SceneComponentTypes.IsVoxelModel));
            if (selectedOnly && target is null) { SetStatus("请先选择一个体素模型"); return; }
            MaterialExchangeMetadata? nativeMetadata = null;
            if (native && !selectedOnly)
            {
                var segments = BuildNativeObjectSegments();
                var objectMetadata = BuildNativeMaterialMetadata(null, false);
                if (segments.Count == 0 || objectMetadata is null)
                {
                    SetStatus("全场景没有可交换的模型对象或材质映射");
                    return;
                }
                var objectDirectory = Path.Combine(_project.AssetsPath, "Exports");
                Directory.CreateDirectory(objectDirectory);
                var objectName = _project.Manifest.Name;
                var objectPath = Path.Combine(objectDirectory, UniqueAssetFileName(objectDirectory, objectName, ".mvworld"));
                _projectOperation.TryStart("导出原生v4对象片段", objectPath,
                    (_, _) => { LargeWorldFormat.WriteObjects(objectPath, segments, objectMetadata); return segments.Sum(item => item.World.OccupiedVoxelCount); },
                    count => SetStatus($"已导出原生v4对象片段 · {count:N0}体素：{objectPath}"),
                    exception => SetStatus($"原生v4导出失败：{exception.Message}"),
                    () => SetStatus("已取消原生v4导出"));
                return;
            }
            var name = selectedOnly ? target!.Name : _project.Manifest.Name;
            var voxOnly = !native && !obj && !qubicle;
            var source = selectedOnly ? CaptureObjectVoxels(target!)
                : voxOnly ? null : GetRenderWorld(uint.MaxValue).Clone();
            var directory = Path.Combine(_project.AssetsPath, "Exports");
            Directory.CreateDirectory(directory);
            var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var extension = obj ? ".obj" : native ? selectedOnly ? ".mvmodel" : ".mvworld" : qubicle ? ".qb" : ".vox";
            var path = Path.Combine(directory, UniqueAssetFileName(directory, safeName, extension));
            var scope = selectedOnly ? $"选中模型：{name}"
                : voxOnly ? "当前场景体素（按世界分区拆分）" : "场景可见体素（重叠按排序合成）";
            nativeMetadata ??= BuildNativeMaterialMetadata(target, selectedOnly);
            IReadOnlyList<VoxFormat.VoxScenePart> voxParts =
                selectedOnly
                    ? [new VoxFormat.VoxScenePart(name, source!, VoxelCoord64.Zero)]
                    : voxOnly ? BuildSceneVoxParts() : [];
            VoxExportPlan? voxPlan = null;
            if (voxOnly)
            {
                if (voxParts.Count == 0)
                {
                    SetStatus("当前场景没有可导出的体素模型");
                    return;
                }

                var contextual = BuildVoxMaterialMap(target, selectedOnly);
                voxPlan = VoxMaterialExchange.PrepareExport(voxParts, contextual);
                if (voxPlan.LossSummary.HasLoss || voxPlan.LossSummary.RequiresMaterialDecision)
                {
                    SetStatus("VOX导出存在材质或颜色损失，请在确认窗口中查看摘要");
                    _pendingVoxExportPlan = voxPlan;
                    _pendingVoxExportPath = path;
                    _pendingVoxExportScope = scope;
                    _openVoxExportConfirmation = true;
                    ImGui.OpenPopup("VOX导出损失确认##VoxExport");
                    return;
                }
            }
            var objects = _sceneDocument?.Objects ?? [];
            var primary = GetVoxelWorldObject()?.Id;
            IReadOnlyList<string>? writtenVoxFiles = null;
            _projectOperation.TryStart($"导出 {extension} · {scope}", path,
                (cancellation, report) =>
                {
                    if (voxPlan is not null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        report(new BlockingOperationProgress($"写入 VOX 场景分区\n{path}", .85F, CanCancel: false));
                        writtenVoxFiles = voxPlan.Write(path, confirmLoss: true);
                        return voxParts.Sum(part => part.World.OccupiedVoxelCount);
                    }

                    var merged = source!;
                    var world = new VoxelWorld();
                    long processed = 0;
                    foreach (var cell in merged.EnumerateOccupied())
                    {
                        if (++processed % 16384 == 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            report(new BlockingOperationProgress($"收集体素 {processed:N0} / {merged.OccupiedVoxelCount:N0}\n{path}",
                                .7F * processed / Math.Max(1, merged.OccupiedVoxelCount), CanCancel: true));
                        }
                        world.Set(cell.Coordinate, cell.Value);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    report(new BlockingOperationProgress($"写入 {extension} · {world.OccupiedVoxelCount:N0} 体素\n{path}", .85F, CanCancel: obj));
                    if (native)
                    {
                        if (world.OccupiedVoxelCount == 0) LargeWorldFormat.WriteEmpty(path);
                        else if (native && nativeMetadata is not null) LargeWorldFormat.Write(path, world, nativeMetadata);
                        else LargeWorldFormat.Write(path, world);
                    }
                    else if (obj) ObjVoxelExporter.Write(path, world, cancellation);
                    else if (qubicle) QubicleFormat.Write(path, world);
                    return world.OccupiedVoxelCount;
                },
                count => SetStatus(writtenVoxFiles is { Count: > 1 }
                    ? $"已导出 {extension} · {scope} · {count:N0} 体素 · {writtenVoxFiles.Count} 个文件：{Path.GetDirectoryName(writtenVoxFiles[0])}"
                    : $"已导出 {extension} · {scope} · {count:N0} 体素：{writtenVoxFiles?[0] ?? path}"),
                exception => { MVoxelLog.Error("导出工作模型失败", exception); SetStatus($"导出失败：{exception.Message}"); },
                () => SetStatus("已取消模型导出，未写入目标文件"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OverflowException or ArgumentException)
        {
            MVoxelLog.Error("导出工作模型失败", exception);
            SetStatus($"导出失败：{exception.Message}");
        }
    }
}
