using System.Globalization;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using MVoxel.Core;
using MVoxel.Editor.Templates;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Voxels.Mesh;
using MVoxel.Voxels.Minecraft;

namespace MVoxel.Cli;

/// <summary>
/// MVoxel 命令行和 MCP stdio 服务入口
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string _editorEndpointPath = AppPaths.EditorEndpointPath;

    /// <summary>
    /// 执行命令行入口
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <returns>进程退出码</returns>
    private static int Main(string[] args)
    {
        var editorDirectoryIndex = Array.IndexOf(args, "--editor-directory");
        if (editorDirectoryIndex >= 0)
        {
            if (editorDirectoryIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[editorDirectoryIndex + 1]))
            {
                Console.Error.WriteLine("错误：--editor-directory 需要指定 MVoxel.exe 所在目录");
                return 1;
            }
            try
            {
                _editorEndpointPath = Path.Combine(Path.GetFullPath(args[editorDirectoryIndex + 1]), "MVoxelData", "editor.endpoint");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Console.Error.WriteLine($"错误：编辑器目录无效：{exception.Message}");
                return 1;
            }
            args = args.Where((_, index) => index != editorDirectoryIndex && index != editorDirectoryIndex + 1).ToArray();
        }
        if (args.Any(item => item.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            RunMcp();
            return 0;
        }

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return RunHumanCommand(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 输出人工使用的命令行帮助
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine("MVoxel CLI");
        Console.WriteLine("  --mcp");
        Console.WriteLine("      启动 MCP stdio 服务");
        Console.WriteLine("  --editor-directory <MVoxel.exe 所在目录>");
        Console.WriteLine("      CLI 与编辑器分目录部署时显式指定控制端点位置，可与 --mcp 或编辑器命令组合");
        Console.WriteLine("  --project-info <项目目录>");
        Console.WriteLine("      输出项目摘要");
        Console.WriteLine("  --scene-info <场景文件>");
        Console.WriteLine("      输出场景对象和组件摘要");
        Console.WriteLine("  --list-scenes <项目目录>");
        Console.WriteLine("      列出项目 Assets 中的场景文件");
        Console.WriteLine("  --validate-project <项目目录>");
        Console.WriteLine("      校验项目清单和场景文件");
        Console.WriteLine("  --create-project <项目目录> [项目名称] [模板 ID]");
        Console.WriteLine("      创建项目并生成指定示例模板");
        Console.WriteLine("  --list-templates");
        Console.WriteLine("      列出可用项目模板");
        Console.WriteLine("  --import-mesh <网格文件> <输出.mvmodel> [分辨率] [shell] [RRGGBB]");
        Console.WriteLine("      把 obj/stl/glb/gltf 网格体素化为世界文件，默认实体填充、分辨率 64");
        Console.WriteLine("  --export-vox <世界文件> <输出.vox>");
        Console.WriteLine("      把 .mvmodel/.mvworld 体素世界导出为 MagicaVoxel .vox 交换文件");
        Console.WriteLine("  --mc-bench <存档目录> <最小区块X> <最小区块Z> <最大区块X> <最大区块Z> [次数]");
        Console.WriteLine("      Minecraft 导入选区分段性能基准");
        Console.WriteLine("  --list-plugins <项目目录>");
        Console.WriteLine("      输出已安装插件和项目启用状态");
        Console.WriteLine("  --plugin-diagnostics <项目目录>");
        Console.WriteLine("      诊断项目插件是否已安装");
        Console.WriteLine("  --enable-plugin <项目目录> <插件 ID>");
        Console.WriteLine("      在项目中启用插件");
        Console.WriteLine("  --disable-plugin <项目目录> <插件 ID>");
        Console.WriteLine("      在项目中禁用插件");
        Console.WriteLine("  --editor-status");
        Console.WriteLine("      输出运行中编辑器状态");
        Console.WriteLine("  --validate-editor-scene");
        Console.WriteLine("      校验运行中编辑器当前场景");
        Console.WriteLine("  --open-project <项目目录>");
        Console.WriteLine("      请求运行中的编辑器打开项目");
        Console.WriteLine("  --save-editor-project");
        Console.WriteLine("      请求运行中的编辑器保存当前项目");
        Console.WriteLine("  --close-editor-project [--discard]");
        Console.WriteLine("      请求运行中的编辑器关闭当前项目；有未保存修改时需显式丢弃");
        Console.WriteLine("  --install-plugin <插件目录或 .mvplugin 包>");
        Console.WriteLine("      安装编辑器级 JavaScript/Lua 插件，分发包安装前逐文件校验 sha256");
        Console.WriteLine("  --pack-plugin <插件目录> <输出 .mvplugin>");
        Console.WriteLine("      把插件目录打包为带校验清单的 .mvplugin 分发包");
        Console.WriteLine("  --reload-plugins");
        Console.WriteLine("      请求运行中的编辑器重载插件");
        Console.WriteLine("  --set-plugin-enabled <插件 ID> <true|false>");
        Console.WriteLine("      修改运行中项目的插件启停状态");
        Console.WriteLine("  --invoke-automation <自动化 ID> [JSON 参数]");
        Console.WriteLine("      调用运行中插件的自动化入口");
        Console.WriteLine("  --invoke-command <命令 ID> [JSON 参数]");
        Console.WriteLine("      调用运行中插件的编辑器命令");
    }

    /// <summary>
    /// 执行人工使用的命令行操作
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <returns>进程退出码</returns>
    private static int RunHumanCommand(string[] args)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "--project-info" when args.Length == 2:
            {
                var project = ProjectLayout.Open(args[1]);
                Console.WriteLine($"项目：{project.Manifest.Name}");
                Console.WriteLine($"路径：{project.RootPath}");
                Console.WriteLine($"引擎版本：{project.Manifest.EngineVersion}");
                return 0;
            }
            case "--scene-info" when args.Length == 2:
            {
                var arguments = JsonSerializer.SerializeToElement(new { scenePath = args[1] });
                Console.WriteLine(JsonSerializer.Serialize(SceneInfo(arguments), JsonOptions));
                return 0;
            }
            case "--list-scenes" when args.Length == 2:
            {
                var arguments = JsonSerializer.SerializeToElement(new { projectPath = args[1] });
                Console.WriteLine(JsonSerializer.Serialize(ListScenes(arguments), JsonOptions));
                return 0;
            }
            case "--validate-project" when args.Length == 2:
            {
                var arguments = JsonSerializer.SerializeToElement(new { projectPath = args[1] });
                var result = (ProjectValidationResult)ValidateProject(arguments);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return result.Valid ? 0 : 1;
            }
            case "--export-scene-vox" when args.Length is 3 or 4:
            {
                var project = ProjectLayout.Open(args[1]);
                var scenePath = args.Length == 4 ? args[3] : Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox");
                var scene = SceneSerializer.Load(scenePath);
                var parts = new List<VoxFormat.VoxScenePart>();
                var skipped = 0;
                foreach (var sceneObject in scene.Objects)
                {
                    if (!sceneObject.Visible) continue;
                    var model = sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel)
                        .Select(SceneComponentTypes.ReadVoxelModel).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(model.Model)) continue;
                    // 组件的 model 是从项目根开始的 Assets/... 相对路径
                    var worldPath = Path.Combine(project.RootPath,
                        model.Model.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(worldPath)) continue;
                    // 只有格变换能无损落到 .vox 的整数平移；自由变换只记录跳过，不做近似
                    if (sceneObject.Transform.Mode != TransformMode.Grid)
                    {
                        skipped++;
                        continue;
                    }

                    var placement = sceneObject.Transform.Grid.Position;
                    parts.Add(new VoxFormat.VoxScenePart(sceneObject.Name, LargeWorldFormat.Read(worldPath), placement));
                }

                if (parts.Count == 0) throw new InvalidOperationException("场景里没有可导出的体素模型对象");
                var materials = MaterialAsset.LoadFromProject(project)
                    .Select(asset => new VoxMaterial(asset.Slot, MaterialType(asset), 1F,
                        asset.Roughness ?? -1F, asset.Specular ?? -1F, asset.Ior ?? -1F,
                        asset.AbsorptionStep ?? -1F, asset.EmissiveScale ?? -1F))
                    .ToList();
                VoxFormat.WriteScene(args[2], parts, materials);
                Console.WriteLine($"已导出场景 .vox：{args[2]}");
                Console.WriteLine($"对象 {parts.Count} 个，体素 {parts.Sum(part => part.World.OccupiedVoxelCount):N0}，" +
                                  $"材质 {materials.Count} 个" +
                                  (skipped > 0 ? $"，跳过 {skipped} 个非网格变换对象" : ""));
                return 0;
            }
            case "--create-vox-project" when args.Length is 2 or 3:
            {
                var voxPath = Path.GetFullPath(args[1]);
                if (!File.Exists(voxPath)) throw new FileNotFoundException("找不到 .vox 文件", voxPath);
                var root = Path.GetFullPath(args.Length == 3
                    ? args[2]
                    : MVoxel.Editor.Settings.SettingsStore.LoadEditor().ProjectRootPath);
                var baseName = Path.GetFileNameWithoutExtension(voxPath).Trim();
                if (baseName.Length == 0) baseName = "导入模型";
                var name = baseName;
                for (var suffix = 2; Directory.Exists(Path.Combine(root, name)); suffix++)
                    name = $"{baseName} {suffix}";
                var project = ProjectLayout.Create(Path.Combine(root, name), name);
                var voxels = ProjectTemplateCatalog.PopulateFromVox(project, voxPath);
                Console.WriteLine($"已从 .vox 创建项目：{project.Manifest.Name}");
                Console.WriteLine($"路径：{project.RootPath}");
                Console.WriteLine($"体素：{voxels:N0}");
                return 0;
            }
            case "--create-project" when args.Length is 2 or 3 or 4:
            {
                var templateId = ProjectTemplateId.Empty;
                if (args.Length == 4 && !Enum.TryParse(args[3], true, out templateId))
                    throw new ArgumentException($"未知项目模板：{args[3]}");
                var project = ProjectLayout.Create(args[1], args.Length == 3 ? args[2] : null);
                ProjectTemplateCatalog.Populate(project, templateId);
                Console.WriteLine($"已创建项目：{project.Manifest.Name}");
                Console.WriteLine($"模板：{ProjectTemplateCatalog.Get(templateId).Name}");
                Console.WriteLine($"路径：{project.RootPath}");
                return 0;
            }
            case "--list-templates" when args.Length == 1:
                foreach (var template in ProjectTemplateCatalog.All)
                    Console.WriteLine($"{template.Id}\t{template.Name}\t{template.Focus}");
                return 0;
            case "--import-mesh" when args.Length is >= 3 and <= 6:
            {
                var resolution = args.Length >= 4 && int.TryParse(args[3], out var parsed) ? parsed : 64;
                var fill = args.Length < 5 || !args[4].Equals("shell", StringComparison.OrdinalIgnoreCase);
                var color = new Rgba32(200, 200, 200);
                if (args.Length >= 6 && args[5].Length == 6 &&
                    byte.TryParse(args[5][..2], NumberStyles.HexNumber, null, out var cr) &&
                    byte.TryParse(args[5][2..4], NumberStyles.HexNumber, null, out var cg) &&
                    byte.TryParse(args[5][4..], NumberStyles.HexNumber, null, out var cb))
                    color = new Rgba32(cr, cg, cb);
                var world = MeshImport.Import(args[1], color, resolution, fill);
                LargeWorldFormat.Write(args[2], world);
                Console.WriteLine($"已体素化 {Path.GetFileName(args[1])}：{world.OccupiedVoxelCount:N0} 体素，" +
                                  $"分辨率 {resolution}，{(fill ? "实体" : "表面壳")}");
                return 0;
            }
            case "--export-vox" when args.Length == 3:
            {
                var world = LargeWorldFormat.Read(args[1]);
                VoxFormat.Write(args[2], world);
                Console.WriteLine($"已导出 .vox：{args[2]}（{world.OccupiedVoxelCount:N0} 体素）");
                return 0;
            }
            case "--list-plugins" when args.Length == 2:
            {
                var project = ProjectLayout.Open(args[1]);
                var enabled = PluginManager.ListEnabled(project).ToHashSet(StringComparer.Ordinal);
                foreach (var package in PluginManager.ListInstalled())
                    Console.WriteLine($"{(enabled.Contains(package.Id) ? "[启用]" : "[停用]")} {package.Id} {package.Version}");
                return 0;
            }
            case "--plugin-diagnostics" when args.Length == 2:
            {
                var project = ProjectLayout.Open(args[1]);
                foreach (var diagnostic in PluginManager.Diagnose(project))
                    Console.WriteLine($"{(diagnostic.Installed ? "[已安装]" : "[缺失]")} {diagnostic.PluginId} {diagnostic.Version ?? string.Empty} {diagnostic.Status}");
                return 0;
            }
            case "--enable-plugin" when args.Length == 3:
                PluginManager.Enable(ProjectLayout.Open(args[1]), args[2]);
                Console.WriteLine($"已启用插件：{args[2]}");
                return 0;
            case "--disable-plugin" when args.Length == 3:
                PluginManager.Disable(ProjectLayout.Open(args[1]), args[2]);
                Console.WriteLine($"已禁用插件：{args[2]}");
                return 0;
            case "--editor-status" when args.Length == 1:
            {
                using var arguments = JsonDocument.Parse("{}");
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.status", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--validate-editor-scene" when args.Length == 1:
            {
                using var arguments = JsonDocument.Parse("{}");
                var result = (JsonElement)CallEditor("scene.validate", arguments.RootElement);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return result.TryGetProperty("valid", out var valid) && valid.GetBoolean() ? 0 : 1;
            }
            case "--open-project" when args.Length == 2:
            {
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path = args[1] }));
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.openProject", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--save-editor-project" when args.Length == 1:
            {
                using var arguments = JsonDocument.Parse("{}");
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.saveProject", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--close-editor-project" when args.Length is 1 or 2 && (args.Length == 1 ||
                string.Equals(args[1], "--discard", StringComparison.OrdinalIgnoreCase)):
            {
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { discard = args.Length == 2 }));
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.closeProject", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--install-plugin" when args.Length == 2:
            {
                var package = args[1].EndsWith(".mvplugin", StringComparison.OrdinalIgnoreCase)
                    ? PluginManager.InstallFromPackage(args[1])
                    : PluginManager.InstallFromDirectory(args[1]);
                Console.WriteLine($"已安装插件：{package.Id} {package.Version}");
                return 0;
            }
            case "--pack-plugin" when args.Length == 3:
            {
                var output = PluginManager.PackPlugin(args[1], args[2]);
                Console.WriteLine($"已打包分发包：{output}");
                return 0;
            }
            case "--reload-plugins" when args.Length == 1:
            {
                using var arguments = JsonDocument.Parse("{}");
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.reloadPlugins", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--set-plugin-enabled" when args.Length == 3 && bool.TryParse(args[2], out var enabled):
            {
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { pluginId = args[1], enabled }));
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.setPluginEnabled", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--invoke-automation" when args.Length is 2 or 3:
            {
                using var payload = JsonDocument.Parse(args.Length == 3 ? args[2] : "{}");
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { automationId = args[1], payload = payload.RootElement }));
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.invokeAutomation", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--invoke-command" when args.Length is 2 or 3:
            {
                using var payload = JsonDocument.Parse(args.Length == 3 ? args[2] : "{}");
                using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { commandId = args[1], payload = payload.RootElement }));
                Console.WriteLine(JsonSerializer.Serialize(CallEditor("editor.invokeCommand", arguments.RootElement), JsonOptions));
                return 0;
            }
            case "--mc-model" when args.Length == 7:
            {
                var info = MinecraftSaveScanner.Inspect(args[1], "overworld");
                var result = McWorldImporter.ConvertRange(info,
                    long.Parse(args[2]), long.Parse(args[3]), long.Parse(args[4]), long.Parse(args[5]));
                LargeWorldFormat.Write(args[6], result.Terrain);
                Console.WriteLine($"已写出 {result.Terrain.OccupiedVoxelCount:N0} 体素到 {args[6]}");
                return 0;
            }
            case "--mc-bench" when args.Length is >= 6 and <= 7:
                return RunMcBench(args);
            default:
                PrintHelp();
                return 2;
        }
    }

    /// <summary>
    /// Minecraft 导入选区性能基准：分段计时范围转换、模型写入与编辑器应用侧的合并重建，用于并行化改动的 A/B 对照
    /// </summary>
    /// <param name="args">参数：存档目录、最小与最大区块坐标，及可选重复次数</param>
    /// <returns>进程退出码</returns>
    private static int RunMcBench(string[] args)
    {
        var savePath = args[1];
        var minChunkX = long.Parse(args[2]);
        var minChunkZ = long.Parse(args[3]);
        var maxChunkX = long.Parse(args[4]);
        var maxChunkZ = long.Parse(args[5]);
        var runs = args.Length == 7 ? int.Parse(args[6]) : 1;
        var info = MinecraftSaveScanner.Inspect(savePath, "overworld");
        var width = maxChunkX - minChunkX + 1;
        var depth = maxChunkZ - minChunkZ + 1;
        Console.WriteLine($"存档：{info.LevelName}  版本：{info.MinecraftVersion}  DataVersion {info.DataVersion}");
        Console.WriteLine($"选区：{width}×{depth} = {width * depth} 区块，Y {info.MinBlockY}..{info.MaxBlockY}");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "MVoxel", "mc-bench");
        Directory.CreateDirectory(outputDirectory);
        for (var run = 1; run <= runs; run++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            var result = McWorldImporter.ConvertRange(info, minChunkX, minChunkZ, maxChunkX, maxChunkZ);
            var convertMs = totalWatch.Elapsed.TotalMilliseconds;

            totalWatch.Restart();
            var writtenFiles = new List<string>();
            var terrainFile = Path.Combine(outputDirectory, $"bench_{run}_terrain.mvmodel");
            LargeWorldFormat.Write(terrainFile, result.Terrain);
            writtenFiles.Add(terrainFile);
            for (var index = 0; index < result.Layers.Count; index++)
            {
                var layerFile = Path.Combine(outputDirectory, $"bench_{run}_tree{index + 1}.mvmodel");
                LargeWorldFormat.Write(layerFile, result.Layers[index].World);
                writtenFiles.Add(layerFile);
            }

            var writeMs = totalWatch.Elapsed.TotalMilliseconds;

            totalWatch.Restart();
            // 与编辑器应用策略一致：树体素直接回填地形对象，不再整世界复制重建
            foreach (var layer in result.Layers)
            {
                foreach (var item in layer.World.EnumerateOccupied())
                {
                    result.Terrain.Set(item.Coordinate, item.Value);
                }
            }

            var rebuildMs = totalWatch.Elapsed.TotalMilliseconds;

            totalWatch.Restart();
            var ownershipTotal = 0;
            foreach (var layer in result.Layers)
            {
                ownershipTotal += new HashSet<VoxelCoord64>(
                    layer.World.EnumerateOccupied().Select(item => item.Coordinate)).Count;
            }

            var ownershipMs = totalWatch.Elapsed.TotalMilliseconds;
            foreach (var written in writtenFiles)
            {
                File.Delete(written);
            }

            var allocatedMb = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0;
            Console.WriteLine($"run {run}：转换 {convertMs:F0}ms" +
                              string.Concat(result.Timings.Select(timings => $"｜{timings.Name} {timings.Milliseconds:F0}ms")) +
                              $"｜写模型 {writeMs:F0}ms｜回填树体素 {rebuildMs:F0}ms｜分层归属 {ownershipMs:F0}ms");
            Console.WriteLine($"　体素 {result.Terrain.OccupiedVoxelCount:N0}，" +
                              $"树 {result.Layers.Count}，归属 {ownershipTotal:N0}，分配 {allocatedMb:N0} MB");
        }

        return 0;
    }

    /// <summary>把材质资产的可覆盖字段还原成 MagicaVoxel 的材质类型</summary>
    private static string MaterialType(MaterialAsset asset)
    {
        if (asset.EmissiveScale is > 0F) return "_emit";
        if (asset.Metallic is >= 1F) return "_metal";
        if (asset.Ior is > 1F) return "_glass";
        return "_diffuse";
    }

    /// <summary>
    /// 按 MCP stdio 传输处理逐行 JSON-RPC 消息
    /// </summary>
    private static void RunMcp()
    {
        // MCP stdio 固定为 UTF-8，不能继承 Windows 控制台的本地代码页。
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement? requestId = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var methodElement)) continue;
                var method = methodElement.GetString() ?? string.Empty;
                if (method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
                requestId = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;
                var result = Handle(method, root.TryGetProperty("params", out var parameters) ? parameters : default);
                WriteResponse(requestId, result, error: null);
            }
            catch (Exception exception)
            {
                var code = exception is McpProtocolException protocolException ? protocolException.Code : -32603;
                WriteResponse(requestId, null, new { code, message = exception.Message });
            }
        }
    }

    /// <summary>
    /// 处理 MCP 初始化、工具列表和工具调用
    /// </summary>
    private static object Handle(string method, JsonElement parameters)
    {
        return method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "mvoxel-cli", version = "0.1.0" },
            },
            "ping" => new { },
            "tools/list" => new { tools = ToolDefinitions },
            "tools/call" => CallTool(parameters),
            _ => throw new McpProtocolException(-32601, $"不支持的 MCP 方法：{method}"),
        };
    }

    /// <summary>
    /// 调用一个项目和插件工具
    /// </summary>
    private static object CallTool(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString() ?? string.Empty;
        var arguments = parameters.TryGetProperty("arguments", out var value) ? value : default;
        object result = name switch
        {
            "project.create" => CreateProject(arguments),
            "project.listTemplates" => ListTemplates(),
            "project.pluginDiagnostics" => PluginDiagnostics(arguments),
            "project.listScenes" => ListScenes(arguments),
            "project.validate" => ValidateProject(arguments),
            "scene.info" => SceneInfo(arguments),
            "project.info" => ProjectInfo(arguments),
            "project.listPlugins" => ListPlugins(arguments),
            "project.enablePlugin" => SetPlugin(arguments, true),
            "project.disablePlugin" => SetPlugin(arguments, false),
            "plugin.install" => InstallPlugin(arguments),
            "editor.status" => CallEditor("editor.status", arguments),
            "scene.validate" => CallEditor("scene.validate", arguments),
            "editor.saveProject" => CallEditor("editor.saveProject", arguments),
            "editor.closeProject" => CallEditor("editor.closeProject", arguments),
            "editor.openProject" => CallEditor("editor.openProject", arguments),
            "editor.captureView" => CallEditor("editor.captureView", arguments),
            "editor.captureWindow" => CallEditor("editor.captureWindow", arguments),
            "editor.showWindowMenu" => CallEditor("editor.showWindowMenu", arguments),
            "editor.showPanelMenu" => CallEditor("editor.showPanelMenu", arguments),
            "editor.captureStatus" => CallEditor("editor.captureStatus", arguments),
            "editor.selectObject" => CallEditor("editor.selectObject", arguments),
            "editor.selection" => CallEditor("editor.selection", arguments),
            "editor.invokeAutomation" => CallEditor("editor.invokeAutomation", arguments),
            "editor.invokeCommand" => CallEditor("editor.invokeCommand", arguments),
            "editor.reloadPlugins" => CallEditor("editor.reloadPlugins", arguments),
            "editor.installPlugin" => CallEditor("editor.installPlugin", arguments),
            "editor.setPluginEnabled" => CallEditor("editor.setPluginEnabled", arguments),
            _ => throw new McpProtocolException(-32601, $"未找到 MCP 工具：{name}"),
        };
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }, structuredContent = result };
    }

    /// <summary>
    /// 创建项目
    /// </summary>
    private static object CreateProject(JsonElement arguments)
    {
        var templateId = ProjectTemplateId.Empty;
        if (arguments.TryGetProperty("templateId", out var template) &&
            !Enum.TryParse(template.GetString(), true, out templateId))
            throw new ArgumentException($"未知项目模板：{template.GetString()}");
        return CreateProjectCore(arguments.GetProperty("path").GetString()!,
            arguments.TryGetProperty("name", out var name) ? name.GetString() : null, templateId);
    }

    /// <summary>
    /// 创建项目并返回摘要
    /// </summary>
    private static object CreateProjectCore(string path, string? name, ProjectTemplateId templateId = ProjectTemplateId.Empty)
    {
        var project = ProjectLayout.Create(path, name);
        ProjectTemplateCatalog.Populate(project, templateId);
        return new { project.RootPath, project.Manifest.Name, Template = templateId.ToString() };
    }

    /// <summary>
    /// 读取可用于创建项目的示例模板
    /// </summary>
    /// <returns>模板定义列表</returns>
    private static object ListTemplates() => ProjectTemplateCatalog.All.Select(template => new
    {
        id = template.Id.ToString(),
        name = template.Name,
        description = template.Description,
        focus = template.Focus,
    }).ToArray();

    /// <summary>
    /// 诊断项目插件是否已经安装
    /// </summary>
    /// <param name="arguments">工具参数</param>
    /// <returns>插件诊断结果</returns>
    private static object PluginDiagnostics(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        return PluginManager.Diagnose(project);
    }

    /// <summary>
    /// 读取项目摘要
    /// </summary>
    private static object ProjectInfo(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        return new { project.RootPath, project.Manifest.Name, project.Manifest.EngineVersion };
    }

    /// <summary>
    /// 读取场景对象和组件摘要
    /// </summary>
    private static object SceneInfo(JsonElement arguments)
    {
        var path = arguments.GetProperty("scenePath").GetString()!;
        var scene = SceneSerializer.Load(path);
        var components = scene.Objects.SelectMany(item => item.Components)
            .GroupBy(item => item.TypeId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new
        {
            scene.Name,
            scene.FormatVersion,
            scenePath = Path.GetFullPath(path),
            objectCount = scene.Objects.Count,
            rootObjectCount = scene.Objects.Count(item => item.ParentId is null),
            componentCount = components.Values.Sum(),
            components,
        };
    }

    /// <summary>
    /// 列出项目 Assets 中的场景文件，只返回资产范围内的路径。
    /// </summary>
    private static object ListScenes(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        var scenes = Directory.Exists(project.AssetsPath)
            ? Directory.EnumerateFiles(project.AssetsPath, "*.mvox", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new
                {
                    path = Path.GetRelativePath(project.RootPath, path).Replace('\\', '/'),
                    absolutePath = Path.GetFullPath(path),
                }).ToArray()
            : [];
        return new { projectPath = project.RootPath, scenes };
    }

    /// <summary>
    /// 校验项目清单并尝试加载 Assets 下的所有场景。
    /// </summary>
    private static object ValidateProject(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!, ensureDirectories: false);
        var errors = new List<string>();
        var missingDirectories = Directory.Exists(project.AssetsPath) ? Array.Empty<string>() : ["Assets"];
        errors.AddRange(missingDirectories.Select(directory => $"缺少资产目录：{directory}"));
        var scenePaths = Directory.Exists(project.AssetsPath)
            ? Directory.EnumerateFiles(project.AssetsPath, "*.mvox", SearchOption.AllDirectories).ToArray()
            : [];
        foreach (var scenePath in scenePaths)
        {
            try
            {
                var scene = SceneSerializer.Load(scenePath);
                errors.AddRange(SceneValidation.Validate(scene).Select(error =>
                    $"{Path.GetRelativePath(project.RootPath, scenePath).Replace('\\', '/')}: {error}"));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                errors.Add($"{Path.GetRelativePath(project.RootPath, scenePath).Replace('\\', '/')}: {exception.Message}");
            }
        }

        return new ProjectValidationResult(
            project.RootPath,
            project.Manifest.Name,
            scenePaths.Length,
            missingDirectories,
            errors.Count == 0,
            errors);
    }

    /// <summary>
    /// 读取编辑器级插件和项目启用状态
    /// </summary>
    private static object ListPlugins(JsonElement arguments)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        return new { installed = PluginManager.ListInstalled(), enabled = PluginManager.ListEnabled(project) };
    }

    /// <summary>
    /// 修改项目插件启用状态
    /// </summary>
    private static object SetPlugin(JsonElement arguments, bool enabled)
    {
        var project = ProjectLayout.Open(arguments.GetProperty("projectPath").GetString()!);
        var pluginId = arguments.GetProperty("pluginId").GetString()!;
        if (enabled) PluginManager.Enable(project, pluginId);
        else PluginManager.Disable(project, pluginId);
        return new { project.RootPath, pluginId, enabled };
    }

    /// <summary>
    /// 安装一个编辑器级 JavaScript 插件目录
    /// </summary>
    private static object InstallPlugin(JsonElement arguments)
    {
        var package = PluginManager.InstallFromDirectory(arguments.GetProperty("sourceDirectory").GetString()!);
        return new { package.Id, package.Name, package.Version, package.Entry };
    }

    /// <summary>
    /// 通过程序目录下的端点文件调用运行中的编辑器
    /// </summary>
    /// <param name="method">编辑器命令名</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>编辑器返回结果</returns>
    private static object CallEditor(string method, JsonElement arguments)
    {
        if (!File.Exists(_editorEndpointPath)) throw new InvalidOperationException($"没有发现运行中的 MVoxel 编辑器：{_editorEndpointPath}；分目录部署请使用 --editor-directory 指定 MVoxel.exe 所在目录");
        var pipeName = File.ReadAllText(_editorEndpointPath).Trim();
        if (string.IsNullOrWhiteSpace(pipeName)) throw new InvalidDataException("编辑器控制端点为空");
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var request = new Dictionary<string, object?> { ["method"] = method, ["arguments"] = arguments };
        writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        var response = reader.ReadLine() ?? throw new IOException("编辑器控制端点没有返回结果");
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var message = document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "编辑器控制命令失败"
                : $"编辑器控制命令失败：{message}");
        }
        return document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : new { };
    }

    /// <summary>
    /// 写出单行 JSON-RPC 响应
    /// </summary>
    private static void WriteResponse(JsonElement? id, object? result, object? error)
    {
        object response = error is null
            ? new { jsonrpc = "2.0", id, result }
            : new { jsonrpc = "2.0", id, error };
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        Console.Out.Flush();
    }

    private static readonly object[] ToolDefinitions =
    [
        new { name = "project.create", description = "创建 MVoxel 项目并生成可选示例模板", inputSchema = new { type = "object", properties = new { path = new { type = "string" }, name = new { type = "string" }, templateId = new { type = "string", description = "ProjectTemplateId 枚举值，默认 Empty" } }, required = new[] { "path" } } },
        new { name = "project.listTemplates", description = "读取可用项目模板", inputSchema = new { type = "object", properties = new { } } },
        new { name = "project.pluginDiagnostics", description = "诊断项目插件安装状态", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.listScenes", description = "列出项目 Assets 中的场景文件", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.validate", description = "校验项目清单并加载检查所有场景", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "scene.info", description = "读取场景对象和组件摘要", inputSchema = new { type = "object", properties = new { scenePath = new { type = "string" } }, required = new[] { "scenePath" } } },
        new { name = "project.info", description = "读取项目摘要", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.listPlugins", description = "读取编辑器级插件和项目启用状态", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" } }, required = new[] { "projectPath" } } },
        new { name = "project.enablePlugin", description = "在项目中启用编辑器级插件", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" }, pluginId = new { type = "string" } }, required = new[] { "projectPath", "pluginId" } } },
        new { name = "project.disablePlugin", description = "在项目中禁用编辑器级插件", inputSchema = new { type = "object", properties = new { projectPath = new { type = "string" }, pluginId = new { type = "string" } }, required = new[] { "projectPath", "pluginId" } } },
        new { name = "plugin.install", description = "安装编辑器级 JavaScript 插件", inputSchema = new { type = "object", properties = new { sourceDirectory = new { type = "string" } }, required = new[] { "sourceDirectory" } } },
        new { name = "editor.status", description = "读取运行中编辑器状态和插件自动化入口", inputSchema = new { type = "object", properties = new { } } },
        new { name = "scene.validate", description = "校验运行中编辑器当前打开的场景结构", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.saveProject", description = "请求运行中的编辑器保存当前项目", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.closeProject", description = "请求运行中的编辑器关闭当前项目；有未保存修改时需传 discard=true", inputSchema = new { type = "object", properties = new { discard = new { type = "boolean", description = "明确丢弃未保存场景修改" } } } },
        new { name = "editor.openProject", description = "请求运行中的编辑器打开项目", inputSchema = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } },
        new { name = "editor.showPanelMenu", description = "打开检查器停靠面板的窗口创建菜单", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.showWindowMenu", description = "在编辑器中打开窗口分类菜单", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.captureWindow", description = "从 Vulkan 帧捕获编辑器画面（不含系统标题栏），不受窗口遮挡影响；使用 editor.captureStatus 查询", inputSchema = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } },
        new { name = "editor.captureView", description = "按样本数累积后保存渲染PNG；采样选项仅作用于本次截图，captureStatus返回实际样本与采样元数据，已有文件不会覆盖", inputSchema = new { type = "object", properties = new { path = new { type = "string" }, cameraName = new { type = "string", description = "可选的场景相机名称，名称必须唯一" }, width = new { type = "integer", minimum = 16, maximum = 8192 }, height = new { type = "integer", minimum = 16, maximum = 8192 }, samples = new { type = "integer", minimum = 1, maximum = 16384, description = "累积样本数，默认64" }, timeoutSeconds = new { type = "integer", minimum = 1, maximum = 600 }, directSampling = new { type = "boolean" }, lightTree = new { type = "boolean" }, denoise = new { type = "boolean" }, position = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 }, target = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 } }, required = new[] { "path" } } },
        new { name = "editor.selectObject", description = "选中场景对象，供界面验证使用", inputSchema = new { type = "object", properties = new { id = new { type = "string", description = "对象标识，优先使用" }, name = new { type = "string", description = "对象名称，id 缺省时按名称精确匹配" } } } },
        new { name = "editor.selection", description = "读取当前选中对象的摘要与缩放倍数", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.captureStatus", description = "查询截图任务状态", inputSchema = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } },
        new { name = "editor.invokeAutomation", description = "调用当前项目插件注册的自动化入口", inputSchema = new { type = "object", properties = new { automationId = new { type = "string" }, payload = new { type = "object" } }, required = new[] { "automationId" } } },
        new { name = "editor.invokeCommand", description = "调用当前项目插件注册的编辑器命令", inputSchema = new { type = "object", properties = new { commandId = new { type = "string" }, payload = new { type = "object" } }, required = new[] { "commandId" } } },
        new { name = "editor.reloadPlugins", description = "请求运行中的编辑器重新加载当前项目插件", inputSchema = new { type = "object", properties = new { } } },
        new { name = "editor.installPlugin", description = "让运行中的编辑器安装 JavaScript 插件并重载", inputSchema = new { type = "object", properties = new { sourceDirectory = new { type = "string" } }, required = new[] { "sourceDirectory" } } },
        new { name = "editor.setPluginEnabled", description = "修改当前项目插件启停状态并重载", inputSchema = new { type = "object", properties = new { pluginId = new { type = "string" }, enabled = new { type = "boolean" } }, required = new[] { "pluginId", "enabled" } } },
    ];

    private sealed record ProjectValidationResult(
        string ProjectPath,
        string ProjectName,
        int SceneCount,
        IReadOnlyList<string> MissingDirectories,
        bool Valid,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// 表示 MCP 请求可以直接返回的协议错误
    /// </summary>
    private sealed class McpProtocolException : Exception
    {
        /// <summary>
        /// 创建协议错误
        /// </summary>
        /// <param name="code">JSON-RPC 错误码</param>
        /// <param name="message">错误信息</param>
        public McpProtocolException(int code, string message) : base(message)
        {
            Code = code;
        }

        /// <summary>
        /// JSON-RPC 错误码
        /// </summary>
        public int Code { get; }
    }
}
