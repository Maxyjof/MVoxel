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
    private McImportResult? _mcImportResult;

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
        _mcImportResult = null;
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
            else if (_mcImportResult is { } result)
            {
                _mcImportDone = false;
                _mcImportBusy = false;
                ApplyImportResult(info, result);
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
        if (ImGui.Button("取消", new Vector2(100F, 32F)))
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
    /// 在后台线程执行范围转换，结果由每帧绘制时回收
    /// </summary>
    private void StartMcImport()
    {
        if (_mcImportBusy || !_mcSelValid || _mcImportInfo is not { } info)
        {
            return;
        }

        var (minChunkX, minChunkZ) = (_mcSelMinChunkX, _mcSelMinChunkZ);
        var (maxChunkX, maxChunkZ) = (_mcSelMaxChunkX, _mcSelMaxChunkZ);
        _mcImportBusy = true;
        _mcImportDone = false;
        _mcImportError = null;
        _mcImportResult = null;
        _mcImportProgress = "准备导入…";
        Task.Run(() =>
        {
            try
            {
                _mcImportResult = McWorldImporter.ConvertRange(info, minChunkX, minChunkZ, maxChunkX, maxChunkZ,
                    progress => _mcImportProgress = progress);
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
    /// 把转换结果写入世界文件、重建场景层级并合并为当前工作世界
    /// </summary>
    private void ApplyImportResult(MinecraftSaveInfo info, McImportResult result)
    {
        if (_project is null)
        {
            CloseMcImport();
            return;
        }

        try
        {
            var baseName = SanitizeFileName(
                string.IsNullOrWhiteSpace(info.LevelName) ? "Minecraft地图" : info.LevelName);
            var worldsDirectory = Path.Combine(_project.AssetsPath, "Worlds");
            Directory.CreateDirectory(worldsDirectory);
            var terrainFile = $"{baseName}_{_mcSelMinChunkX}_{_mcSelMinChunkZ}.mvmodel";
            LargeWorldFormat.Write(Path.Combine(worldsDirectory, terrainFile), result.Terrain);
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
            AddMcModelComponent(terrainObject, "Assets/Worlds/" + terrainFile, result.Terrain.OccupiedVoxelCount,
                "地形");
            for (var index = 0; index < result.Layers.Count; index++)
            {
                var layer = result.Layers[index];
                var layerFile = $"{baseName}_树{index + 1}.mvmodel";
                LargeWorldFormat.Write(Path.Combine(worldsDirectory, layerFile), layer.World);
                var layerObject = graph.Add(layer.Name, transform: TransformComponent.FromGrid(GridTransform.Identity));
                AddMcModelComponent(layerObject, "Assets/Worlds/" + layerFile, layer.World.OccupiedVoxelCount,
                    "树木分层");
            }

            _sceneDocument = document;
            var merged = new VoxelWorld();
            foreach (var item in result.Terrain.EnumerateOccupied())
            {
                merged.Set(item.Coordinate, item.Value);
            }

            foreach (var layer in result.Layers)
            {
                foreach (var item in layer.World.EnumerateOccupied())
                {
                    merged.Set(item.Coordinate, item.Value);
                }
            }

            _world = merged;
            _voxelHistory.Clear();
            _selectedVoxel = null;
            _pipeline?.SyncWorld(_world, GetSceneGpuCellBudget());
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            FrameCameraOnWorld();
            MarkSceneDocumentDirty();
            SetStatus($"已导入 {info.LevelName}：地形 {result.Terrain.OccupiedVoxelCount:N0} 体素，" +
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
    /// 选择外部 .vox 文件并转换为项目世界文件，随后作为当前工作世界打开
    /// </summary>
    private void ImportVoxAsWorld()
    {
        if (_project is null)
        {
            return;
        }

        var path = NativeDialogs.PickFile("选择 MagicaVoxel 模型", "MagicaVoxel 模型\0*.vox\0全部文件\0*.*\0\0");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var world = VoxFormat.Read(path);
            var worldsDirectory = Path.Combine(_project.AssetsPath, "Worlds");
            Directory.CreateDirectory(worldsDirectory);
            var fileName = SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + ".mvmodel";
            LargeWorldFormat.Write(Path.Combine(worldsDirectory, fileName), world);
            OpenWorldAsset("Worlds/" + fileName);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            SetStatus($"导入 .vox 失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 维度下拉框的显示文本
    /// </summary>
    private static string DimensionLabel(string id, IReadOnlyList<MinecraftDimension> dimensions)
    {
        return dimensions.FirstOrDefault(item => item.Id == id)?.DisplayName ?? id;
    }
}
