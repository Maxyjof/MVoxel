using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Voxels.Minecraft;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器主窗口的 Minecraft 存档导入向导：
/// 选存档、俯视区块预览、框选范围、后台转换并落成地形加树分层的场景
/// </summary>
internal partial class ImGuiEditorWindow
{
    private bool _mcImportActive;
    private string _mcImportSavePath = string.Empty;
    private MinecraftSaveInfo? _mcImportInfo;
    private IReadOnlyList<MinecraftDimension>? _mcImportDimensions;
    private string _mcImportSelectedDimensionId = string.Empty;
    private int _mcStrideX = 1;
    private int _mcStrideZ = 1;
    private List<(long X, long Z)> _mcDisplayChunks = new();
    private readonly Dictionary<(long X, long Z), uint> _mcChunkColors = new();
    private readonly Dictionary<(int X, int Z), AnvilRegionFile> _mcRegions = new();
    private int _mcColorCursor;
    private bool _mcSelValid;
    private bool _mcDragging;
    private long _mcAnchorChunkX;
    private long _mcAnchorChunkZ;
    private long _mcSelMinChunkX;
    private long _mcSelMinChunkZ;
    private long _mcSelMaxChunkX;
    private long _mcSelMaxChunkZ;
    private volatile bool _mcImportBusy;
    private volatile bool _mcImportDone;
    private string _mcImportProgress = string.Empty;
    private string? _mcImportError;
    private PreparedMcImport? _mcImportPrepared;
    private CancellationTokenSource? _mcImportCancellation;

    /// <summary>
    /// 后台线程完成转换、文件写入与回填后的可应用导入结果
    /// </summary>
    private sealed class PreparedMcImport
    {
        /// <summary>
        /// 已回填树体素的地形世界，直接用作场景工作世界
        /// </summary>
        public required McImportResult Result { get; init; }

        /// <summary>
        /// 存档显示名，用于状态栏
        /// </summary>
        public required string LevelName { get; init; }

        /// <summary>
        /// 地形模型文件名
        /// </summary>
        public required string TerrainFileName { get; init; }

        /// <summary>
        /// 回填树体素之前的地形规模，与地形模型文件记录数一致
        /// </summary>
        public required int TerrainVoxelCount { get; init; }

        /// <summary>
        /// 与分层一一对应的树模型文件名
        /// </summary>
        public required string[] LayerFileNames { get; init; }

        /// <summary>
        /// 与分层一一对应的树体素归属集合
        /// </summary>
        public required HashSet<VoxelCoord64>[] LayerOwnership { get; init; }
    }

    /// <summary>
    /// 弹出文件夹选择并初始化导入向导，存档非法时在状态栏说明原因
    /// </summary>
    private void ShowMinecraftImportDialog()
    {
        if (_project is null)
        {
            return;
        }

        var path = NativeDialogs.PickFolder("选择 Minecraft 存档文件夹");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!MinecraftSaveScanner.IsMinecraftSave(path))
            {
                SetStatus("所选文件夹不是 Minecraft 存档：缺少 level.dat");
                return;
            }

            var dimensions = MinecraftSaveScanner.ListDimensions(path);
            if (dimensions.Count == 0)
            {
                SetStatus("存档中没有包含区块数据的维度");
                return;
            }

            _mcImportSavePath = path;
            _mcImportDimensions = dimensions;
            _mcImportSelectedDimensionId =
                dimensions.Any(item => item.Id == "overworld") ? "overworld" : dimensions[0].Id;
            RefreshMcImportInfo();
            _mcImportActive = true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            SetStatus($"读取存档失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 重新检查当前维度并重置预览缓存、显示步长与默认选区
    /// </summary>
    private void RefreshMcImportInfo()
    {
        _mcImportInfo = MinecraftSaveScanner.Inspect(_mcImportSavePath, _mcImportSelectedDimensionId);
        _mcChunkColors.Clear();
        _mcColorCursor = 0;
        _mcSelValid = false;
        _mcDragging = false;
        _mcImportBusy = false;
        _mcImportDone = false;
        _mcImportError = null;
        _mcImportPrepared = null;
        _mcImportProgress = string.Empty;
        var info = _mcImportInfo;
        var viewWidth = info.MaxChunkX - info.MinChunkX + 1;
        var viewDepth = info.MaxChunkZ - info.MinChunkZ + 1;
        _mcStrideX = Math.Max(1, (int)Math.Ceiling(viewWidth / 128.0));
        _mcStrideZ = Math.Max(1, (int)Math.Ceiling(viewDepth / 128.0));
        _mcDisplayChunks = new List<(long X, long Z)>();
        for (var chunkZ = info.MinChunkZ; chunkZ <= info.MaxChunkZ; chunkZ += _mcStrideZ)
        {
            for (var chunkX = info.MinChunkX; chunkX <= info.MaxChunkX; chunkX += _mcStrideX)
            {
                _mcDisplayChunks.Add((chunkX, chunkZ));
            }
        }

        var centerX = (info.MinChunkX + info.MaxChunkX) / 2;
        var centerZ = (info.MinChunkZ + info.MaxChunkZ) / 2;
        var halfWidth = Math.Min(16, viewWidth / 2);
        var halfDepth = Math.Min(16, viewDepth / 2);
        _mcSelMinChunkX = Math.Max(info.MinChunkX, centerX - halfWidth);
        _mcSelMaxChunkX = Math.Min(info.MaxChunkX, centerX + halfWidth);
        _mcSelMinChunkZ = Math.Max(info.MinChunkZ, centerZ - halfDepth);
        _mcSelMaxChunkZ = Math.Min(info.MaxChunkZ, centerZ + halfDepth);
        _mcSelValid = true;
    }

    /// <summary>
    /// 每帧绘制导入向导窗口，包含区块俯视网格、框选交互与导入进度
    /// </summary>
    private void DrawMinecraftImportWindow()
    {
        if (!_mcImportActive)
        {
            return;
        }

        var open = true;
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(displaySize / 2F, ImGuiCond.Appearing, new Vector2(0.5F, 0.5F));
        ImGui.SetNextWindowSize(new Vector2(920F, 640F), ImGuiCond.Appearing);
        if (!ImGui.Begin("导入 Minecraft 存档", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            if (!open) CloseMcImport();
            return;
        }

        if (_mcImportInfo is not { } info || _mcImportDimensions is null)
        {
            ImGui.End();
            CloseMcImport();
            return;
        }

        ImGui.TextUnformatted(
            $"{info.LevelName}    {info.MinecraftVersion}    DataVersion {info.DataVersion}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160F);
        if (ImGui.BeginCombo("##mc维度", DimensionLabel(info.Dimension.Id, _mcImportDimensions)))
        {
            foreach (var dimension in _mcImportDimensions)
            {
                if (ImGui.Selectable(DimensionLabel(dimension.Id, _mcImportDimensions),
                        dimension.Id == info.Dimension.Id))
                {
                    _mcImportSelectedDimensionId = dimension.Id;
                    RefreshMcImportInfo();
                    info = _mcImportInfo!;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(
            $"区块范围 {info.MinChunkX}..{info.MaxChunkX} × {info.MinChunkZ}..{info.MaxChunkZ}，" +
            $"共 {info.ChunkCount:N0} 个，Y 范围 {info.MinBlockY}..{info.MaxBlockY}，" +
            $"表层采样每帧加载 12 个区块");
        ImGui.Separator();
        LoadChunkColorsBudgeted(12);
        DrawMcChunkGrid(info);
        ImGui.Separator();
        DrawMcSelectionFooter(info);
        ImGui.End();
        if (!open) CloseMcImport();
    }

    /// <summary>
    /// 绘制区块俯视网格并处理拖拽框选
    /// </summary>
    private void DrawMcChunkGrid(MinecraftSaveInfo info)
    {
        var columns = (int)((info.MaxChunkX - info.MinChunkX) / _mcStrideX) + 1;
        var rows = (int)((info.MaxChunkZ - info.MinChunkZ) / _mcStrideZ) + 1;
        const float cell = 12F;
        var gridSize = new Vector2(columns * cell, rows * cell);
        ImGui.BeginChild("##mc区块网格", new Vector2(-1F, -ImGui.GetFrameHeightWithSpacing() * 4F),
            ImGuiChildFlags.Borders);
        var cursor = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var emptyColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.16F, 0.17F, 0.20F, 1F));
        foreach (var (chunkX, chunkZ) in _mcDisplayChunks)
        {
            var column = (int)((chunkX - info.MinChunkX) / _mcStrideX);
            var row = (int)((chunkZ - info.MinChunkZ) / _mcStrideZ);
            var min = cursor + new Vector2(column * cell, row * cell);
            var color = _mcChunkColors.TryGetValue((chunkX, chunkZ), out var sampled) && sampled != 0
                ? sampled
                : emptyColor;
            draw.AddRectFilled(min, min + new Vector2(cell, cell), color);
        }

        if (_mcSelValid)
        {
            var minColumn = (int)((_mcSelMinChunkX - info.MinChunkX) / _mcStrideX);
            var minRow = (int)((_mcSelMinChunkZ - info.MinChunkZ) / _mcStrideZ);
            var maxColumn = (int)((_mcSelMaxChunkX - info.MinChunkX) / _mcStrideX);
            var maxRow = (int)((_mcSelMaxChunkZ - info.MinChunkZ) / _mcStrideZ);
            var min = cursor + new Vector2(minColumn * cell, minRow * cell);
            var max = cursor + new Vector2((maxColumn + 1) * cell, (maxRow + 1) * cell);
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1F, .72F, .25F, 1F)), 0F,
                ImDrawFlags.None, 2F);
            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1F, .72F, .25F, .12F)));
        }

        ImGui.InvisibleButton("##mc区块拖选", gridSize);
        if (ImGui.IsItemHovered())
        {
            var mouse = ImGui.GetIO().MousePos - cursor;
            var column = Math.Clamp((int)(mouse.X / cell), 0, columns - 1);
            var row = Math.Clamp((int)(mouse.Y / cell), 0, rows - 1);
            var chunkX = info.MinChunkX + (long)column * _mcStrideX;
            var chunkZ = info.MinChunkZ + (long)row * _mcStrideZ;
            if (ImGui.IsItemActive())
            {
                if (!_mcDragging)
                {
                    _mcDragging = true;
                    _mcAnchorChunkX = chunkX;
                    _mcAnchorChunkZ = chunkZ;
                }

                _mcSelMinChunkX = Math.Min(_mcAnchorChunkX, chunkX);
                _mcSelMaxChunkX = Math.Max(_mcAnchorChunkX, chunkX);
                _mcSelMinChunkZ = Math.Min(_mcAnchorChunkZ, chunkZ);
                _mcSelMaxChunkZ = Math.Max(_mcAnchorChunkZ, chunkZ);
                _mcSelValid = true;
            }
        }

        if (!ImGui.IsItemActive() && _mcDragging)
        {
            _mcDragging = false;
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// 绘制选区信息、导入进度与操作按钮
    /// </summary>
    private void DrawMcSelectionFooter(MinecraftSaveInfo info)
    {
        if (_mcSelValid)
        {
            var width = _mcSelMaxChunkX - _mcSelMinChunkX + 1;
            var depth = _mcSelMaxChunkZ - _mcSelMinChunkZ + 1;
            var chunkCount = width * depth;
            ImGui.TextUnformatted(
                $"已选 {width}×{depth} 区块（{chunkCount:N0} 个），对应世界 {width * 16}×{depth * 16} 方块，" +
                $"Y {info.MinBlockY}..{info.MaxBlockY}");
            ImGui.TextDisabled("导入后地形与检测到的树将分层写入 Worlds 目录，并合并显示在场景中");
        }
        else
        {
            ImGui.TextDisabled("在网格上按住鼠标拖动，框选要导入的区块范围");
        }

        if (_mcImportBusy)
        {
            ImGui.Text($"正在导入：{_mcImportProgress}");
        }

        if (_mcImportDone)
        {
            if (_mcImportError is { } error)
            {
                ImGui.TextColored(new Vector4(1F, .4F, .35F, 1F), $"导入失败：{error}");
            }
            else if (_mcImportPrepared is { } prepared)
            {
                _mcImportDone = false;
                _mcImportBusy = false;
                ApplyImportResult(prepared);
                return;
            }
        }

        var selected = _mcSelValid ? (_mcSelMaxChunkX - _mcSelMinChunkX + 1) * (_mcSelMaxChunkZ - _mcSelMinChunkZ + 1) : 0;
        var canImport = _project is not null && _mcSelValid && !_mcImportBusy &&
                        selected is > 0 and <= McWorldImporter.MaximumSelectionChunks;
        if (ImGui.Button("开始导入", new Vector2(120F, 32F)) && canImport)
        {
            StartMcImport();
        }

        ImGui.SameLine();
        if (ImGui.Button(_mcImportBusy ? "取消导入" : "取消", new Vector2(100F, 32F)))
        {
            CloseMcImport();
        }
    }

    /// <summary>
    /// 限制每帧解析的区块数量，把表层颜色缓存逐步填满
    /// </summary>
    private void LoadChunkColorsBudgeted(int budget)
    {
        var processed = 0;
        while (_mcColorCursor < _mcDisplayChunks.Count && processed < budget)
        {
            var (chunkX, chunkZ) = _mcDisplayChunks[_mcColorCursor++];
            if (_mcChunkColors.ContainsKey((chunkX, chunkZ)))
            {
                continue;
            }

            _mcChunkColors[(chunkX, chunkZ)] = SampleChunkColor(chunkX, chunkZ);
            processed++;
        }
    }

    /// <summary>
    /// 采样一个区块的表层平均颜色，未生成或全空返回 0
    /// </summary>
    private uint SampleChunkColor(long chunkX, long chunkZ)
    {
        try
        {
            var root = GetMcRegion(chunkX, chunkZ)?.ReadChunk((int)(chunkX - FloorDiv32(chunkX) * 32),
                (int)(chunkZ - FloorDiv32(chunkZ) * 32));
            if (root is null)
            {
                return 0;
            }

            AnvilSection? top = null;
            foreach (var section in AnvilChunk.FromNbt(root).Sections)
            {
                if (section.Palette.Count == 0 ||
                    (section.Palette.Count == 1 && section.Palette[0] == "minecraft:air"))
                {
                    continue;
                }

                if (top is null || section.BaseY > top.BaseY)
                {
                    top = section;
                }
            }

            if (top is null)
            {
                return 0;
            }

            double red = 0;
            double green = 0;
            double blue = 0;
            var counted = 0;
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    for (var y = 15; y >= 0; y--)
                    {
                        var name = top.BlockAt(x, y, z);
                        if (name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air")
                        {
                            continue;
                        }

                        var color = MinecraftBlockColors.ColorOf(name);
                        red += color.R;
                        green += color.G;
                        blue += color.B;
                        counted++;
                        break;
                    }
                }
            }

            if (counted == 0)
            {
                return 0;
            }

            return ImGui.ColorConvertFloat4ToU32(new Vector4(
                (float)(red / counted / 255.0),
                (float)(green / counted / 255.0),
                (float)(blue / counted / 255.0), 1F));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 取缓存中的 region 文件，缺失时按需打开
    /// </summary>
    private AnvilRegionFile? GetMcRegion(long chunkX, long chunkZ)
    {
        if (_mcImportInfo is not { } info)
        {
            return null;
        }

        var regionX = (int)Math.Floor((double)chunkX / 32);
        var regionZ = (int)Math.Floor((double)chunkZ / 32);
        if (_mcRegions.TryGetValue((regionX, regionZ), out var cached))
        {
            return cached;
        }

        var path = Path.Combine(info.RootPath,
            info.Dimension.RegionDirectory.Replace('/', Path.DirectorySeparatorChar),
            $"r.{regionX}.{regionZ}.mca");
        if (!File.Exists(path))
        {
            return null;
        }

        var region = AnvilRegionFile.Open(path);
        _mcRegions.Add((regionX, regionZ), region);
        return region;
    }

    /// <summary>
    /// 计算区块所在的 region 坐标
    /// </summary>
    private static long FloorDiv32(long value)
    {
        return (long)Math.Floor((double)value / 32);
    }

    /// <summary>
    /// 在后台线程执行范围转换、模型写入与树体素回填，结果由每帧绘制时回收，UI 线程不再承担任何重活
    /// </summary>
    private void StartMcImport()
    {
        if (_mcImportBusy || !_mcSelValid || _mcImportInfo is not { } info || _project is not { } project)
        {
            return;
        }

        var (minChunkX, minChunkZ) = (_mcSelMinChunkX, _mcSelMinChunkZ);
        var (maxChunkX, maxChunkZ) = (_mcSelMaxChunkX, _mcSelMaxChunkZ);
        var worldsDirectory = Path.Combine(project.AssetsPath, "Worlds");
        _mcImportBusy = true;
        _mcImportDone = false;
        _mcImportError = null;
        _mcImportPrepared = null;
        _mcImportProgress = "准备导入…";
        _mcImportCancellation?.Dispose();
        _mcImportCancellation = new CancellationTokenSource();
        var cancellationToken = _mcImportCancellation.Token;
        Task.Run(() =>
        {
            try
            {
                var result = McWorldImporter.ConvertRange(info, minChunkX, minChunkZ, maxChunkX, maxChunkZ,
                    progress => _mcImportProgress = progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(worldsDirectory);
                var baseName = SanitizeFileName(
                    string.IsNullOrWhiteSpace(info.LevelName) ? "Minecraft地图" : info.LevelName);
                _mcImportProgress = "正在写入地形模型…";
                var terrainFileName = UniqueAssetFileName(worldsDirectory,
                    $"{baseName}_{minChunkX}_{minChunkZ}", ".mvmodel");
                var terrainPath = Path.Combine(worldsDirectory, terrainFileName);
                LargeWorldFormat.Write(terrainPath, result.Terrain);
                AssetMetaStore.EnsureMeta(terrainPath);
                var layerFileNames = new string[result.Layers.Count];
                var layerOwnership = new HashSet<VoxelCoord64>[result.Layers.Count];
                for (var index = 0; index < result.Layers.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var layer = result.Layers[index];
                    _mcImportProgress = $"正在写入树分层 {index + 1}/{result.Layers.Count}…";
                    layerFileNames[index] = UniqueAssetFileName(worldsDirectory,
                        $"{baseName}_树{index + 1}", ".mvmodel");
                    var layerPath = Path.Combine(worldsDirectory, layerFileNames[index]);
                    LargeWorldFormat.Write(layerPath, layer.World);
                    AssetMetaStore.EnsureMeta(layerPath);
                    // 树分层的体素归属各自对象：保存时主世界只写地形，各树分别写回自己的文件
                    layerOwnership[index] = new HashSet<VoxelCoord64>(
                        layer.World.EnumerateOccupied().Select(item => item.Coordinate));
                }

                _mcImportProgress = "正在合并工作世界…";
                var terrainVoxelCount = result.Terrain.OccupiedVoxelCount;
                // 树体量远小于地形，直接回填进地形对象使其成为完整工作世界，免去整世界复制
                foreach (var layer in result.Layers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var item in layer.World.EnumerateOccupied())
                    {
                        result.Terrain.Set(item.Coordinate, item.Value);
                    }
                }

                _mcImportPrepared = new PreparedMcImport
                {
                    Result = result,
                    LevelName = info.LevelName,
                    TerrainFileName = terrainFileName,
                    TerrainVoxelCount = terrainVoxelCount,
                    LayerFileNames = layerFileNames,
                    LayerOwnership = layerOwnership,
                };
            }
            catch (OperationCanceledException)
            {
                _mcImportError = "用户已取消导入";
            }
            catch (Exception exception)
            {
                _mcImportError = exception.Message;
            }
            finally
            {
                _mcImportDone = true;
            }
        });
    }

    /// <summary>
    /// 把后台准备好的导入结果挂进场景层级并设为主世界，此处只做轻量的场景与渲染状态更新
    /// </summary>
    private void ApplyImportResult(PreparedMcImport prepared)
    {
        if (_project is null)
        {
            CloseMcImport();
            return;
        }

        var result = prepared.Result;
        try
        {
            _layerOwnership.Clear();
            _editingJournal = new VoxelEditingJournal(
                Path.Combine(RecoveryStore.DirectoryFor(_project!.RootPath), "journal.jsonl"));
            var document = _sceneDocument ??= new SceneDocument { Name = "主场景" };
            for (var index = document.Objects.Count - 1; index >= 0; index--)
            {
                if (document.Objects[index].Components.Any(SceneComponentTypes.IsVoxelModel))
                {
                    document.Objects.RemoveAt(index);
                }
            }

            var graph = new SceneGraph(document);
            var terrainObject = graph.Add("地形", transform: TransformComponent.FromGrid(GridTransform.Identity));
            AddMcModelComponent(terrainObject, "Assets/Worlds/" + prepared.TerrainFileName,
                prepared.TerrainVoxelCount, "地形");
            for (var index = 0; index < result.Layers.Count; index++)
            {
                var layer = result.Layers[index];
                var layerObject = graph.Add(layer.Name, transform: TransformComponent.FromGrid(GridTransform.Identity));
                AddMcModelComponent(layerObject, "Assets/Worlds/" + prepared.LayerFileNames[index],
                    layer.World.OccupiedVoxelCount, "树木分层");
                _layerOwnership[layerObject.Id] = prepared.LayerOwnership[index];
            }

            _sceneDocument = document;
            // 地形对象已在后台完成树体素回填，直接就是完整工作世界，免去整世界复制
            _world = result.Terrain;
            _voxelHistory.Clear();
            _selectedVoxel = null;
            _pipeline?.SyncWorld(_world, GetSceneGpuCellBudget());
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            FrameCameraOnWorld();
            MarkSceneDocumentDirty();
            SetStatus($"已导入 {prepared.LevelName}：地形 {prepared.TerrainVoxelCount:N0} 体素，" +
                      $"拆出 {result.Layers.Count} 个树分层");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MVoxelLog.Error("Minecraft 导入结果写入失败", exception);
            SetStatus($"导入结果写入失败：{exception.Message}");
        }
        finally
        {
            CloseMcImport();
        }
    }

    /// <summary>
    /// 给场景对象挂上指向世界文件的模型组件
    /// </summary>
    private static void AddMcModelComponent(SceneObject sceneObject, string assetPath, int voxelCount, string part)
    {
        sceneObject.Components.Add(new ComponentRecord
        {
            TypeId = SceneComponentTypes.VoxelModel,
            SchemaVersion = 1,
            Data = JsonSerializer.SerializeToElement(new
            {
                model = assetPath,
                voxelCount,
                part,
            }),
        });
    }

    /// <summary>
    /// 把存档名与坐标整理成安全的文件名
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder();
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "Minecraft地图" : result;
    }

    /// <summary>
    /// 关闭向导并释放 region 缓存
    /// </summary>
    private void CloseMcImport()
    {
        _mcImportCancellation?.Cancel();
        _mcImportCancellation?.Dispose();
        _mcImportCancellation = null;
        _mcImportActive = false;
        _mcImportBusy = false;
        _mcImportDone = false;
        foreach (var region in _mcRegions.Values)
        {
            region.Dispose();
        }

        _mcRegions.Clear();
        _mcChunkColors.Clear();
        _mcColorCursor = 0;
        _mcSelValid = false;
        _mcDragging = false;
    }

    /// <summary>
    /// 选择外部 .vox / .qb / .schem 文件并转换为项目世界文件，随后作为当前工作世界打开
    /// </summary>
    private void ImportVoxAsWorld()
    {
        if (_project is null)
        {
            return;
        }

        var path = NativeDialogs.PickFile("选择体素模型",
            "体素模型\0*.vox;*.qb;*.schem\0MagicaVoxel 模型\0*.vox\0Qubicle 模型\0*.qb\0Minecraft 结构\0*.schem\0全部文件\0*.*\0\0");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var world = extension switch
            {
                ".qb" => QubicleFormat.Read(path),
                ".schem" => SchematicFormat.Read(path),
                ".vox" => VoxFormat.Read(path),
                _ => throw new InvalidDataException($"不支持导入的体素格式：{extension}")
            };
            var worldsDirectory = Path.Combine(_project.AssetsPath, "Worlds");
            Directory.CreateDirectory(worldsDirectory);
            var fileName = UniqueAssetFileName(worldsDirectory,
                SanitizeFileName(Path.GetFileNameWithoutExtension(path)), ".mvmodel");
            var worldPath = Path.Combine(worldsDirectory, fileName);
            LargeWorldFormat.Write(worldPath, world);
            AssetMetaStore.EnsureMeta(worldPath);
            OpenWorldAsset("Worlds/" + fileName);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"导入 {extension} 失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 把资产浏览器中的体素世界导出为 MagicaVoxel .vox 交换文件，写入 Assets/Exports
    /// </summary>
    /// <param name="relativePath">相对 Assets 的世界文件路径</param>
    private void ExportVoxAsset(string relativePath)
    {
        if (_project is not { } project)
        {
            SetStatus("请先打开项目再导出 .vox");
            return;
        }
        if (relativePath.Length == 0)
        {
            SetStatus("未选择可导出的体素数据");
            return;
        }

        var absolute = Path.GetFullPath(Path.Combine(project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolute))
        {
            SetStatus($"体素数据文件不存在：{relativePath}");
            return;
        }

        try
        {
            var world = ReadVoxelAsset(absolute);
            var exportDirectory = Path.Combine(project.AssetsPath, "Exports");
            Directory.CreateDirectory(exportDirectory);
            var target = Path.Combine(exportDirectory, UniqueAssetFileName(exportDirectory,
                Path.GetFileNameWithoutExtension(absolute), ".vox"));
            VoxFormat.Write(target, world);
            SetStatus($"已导出 .vox：Exports/{Path.GetFileName(target)}（{world.OccupiedVoxelCount:N0} 体素）");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"导出 .vox 失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 把资产浏览器中的体素世界导出为 Qubicle .qb 交换文件，写入 Assets/Exports
    /// </summary>
    /// <param name="relativePath">相对 Assets 的世界文件路径</param>
    private void ExportQubicleAsset(string relativePath)
    {
        if (_project is not { } project)
        {
            SetStatus("请先打开项目再导出 .qb");
            return;
        }
        if (relativePath.Length == 0)
        {
            SetStatus("未选择可导出的体素数据");
            return;
        }

        var absolute = Path.GetFullPath(Path.Combine(project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolute))
        {
            SetStatus($"体素数据文件不存在：{relativePath}");
            return;
        }

        try
        {
            var world = ReadVoxelAsset(absolute);
            var exportDirectory = Path.Combine(project.AssetsPath, "Exports");
            Directory.CreateDirectory(exportDirectory);
            var target = Path.Combine(exportDirectory, UniqueAssetFileName(exportDirectory,
                Path.GetFileNameWithoutExtension(absolute), ".qb"));
            QubicleFormat.Write(target, world);
            SetStatus($"已导出 .qb：Exports/{Path.GetFileName(target)}（{world.OccupiedVoxelCount:N0} 体素）");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"导出 .qb 失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 读取资产浏览器中的体素文件。外部交换格式与项目原生格式走各自解析器，
    /// 避免把 .vox 误当成 .mvmodel 读取。
    /// </summary>
    private static VoxelWorld ReadVoxelAsset(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".vox" => VoxFormat.Read(path),
            ".qb" => QubicleFormat.Read(path),
            ".schem" => SchematicFormat.Read(path),
            ".mvmodel" or ".mvworld" => LargeWorldFormat.Read(path),
            _ => throw new InvalidDataException($"不支持读取的体素文件：{Path.GetExtension(path)}"),
        };
    }

    /// <summary>
    /// 导入和导出均不覆盖已有资产，重复名称自动增加序号。
    /// </summary>
    private static string UniqueAssetFileName(string directory, string baseName, string extension)
    {
        var safeBaseName = SanitizeFileName(baseName);
        var safeExtension = extension.StartsWith('.') ? extension : "." + extension;
        var candidate = safeBaseName + safeExtension;
        for (var index = 2; File.Exists(Path.Combine(directory, candidate)); index++)
            candidate = $"{safeBaseName}_{index}{safeExtension}";
        return candidate;
    }

    /// <summary>
    /// 维度下拉框的显示文本
    /// </summary>
    private static string DimensionLabel(string id, IReadOnlyList<MinecraftDimension> dimensions)
    {
        return dimensions.FirstOrDefault(item => item.Id == id)?.DisplayName ?? id;
    }
}
