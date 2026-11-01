using System.Numerics;
using MVoxel.Core;
using MVoxel.Editor.Settings;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates;

/// <summary>
/// 新项目可使用的内置示例模板
/// </summary>
public enum ProjectTemplateId
{
    /// <summary>
    /// 包含示例场景的新项目
    /// </summary>
    Empty,

    /// <summary>室内餐桌默认场景。</summary>
    DefaultRoom,

    /// <summary>透明与毛玻璃对照。</summary>
    GlassStudy,
    /// <summary>康奈尔盒间接光实验。</summary>
    CornellBox,
    /// <summary>玻璃球、金属球与棋盘。</summary>
    SphereOptics,
    /// <summary>体积雾与云朵。</summary>
    CloudAndFog,
    /// <summary>水面与水下观察。</summary>
    WaterStudy,

    /// <summary>
    /// 展示基础建模、树木、屋顶、玻璃和自发光
    /// </summary>
    Showcase,

    /// <summary>
    /// 展示不透明、半透明、自发光和材质槽
    /// </summary>
    MaterialLab,

    /// <summary>
    /// 展示调色板基础色和纯色体素材质
    /// </summary>
    PaletteWorkshop,

    /// <summary>
    /// 展示负坐标、远距离对象和稀疏区块
    /// </summary>
    SparseWorld,

    /// <summary>
    /// 展示不同尺寸和密度的重复建筑，便于观察 LOD
    /// </summary>
    LodPlayground,

    /// <summary>
    /// 展示程序化起伏地形和分层材质
    /// </summary>
    ProceduralTerrain,

    /// <summary>
    /// 展示 Minecraft 风格的分层世界：地形世界与每棵树各自独立文件
    /// </summary>
    MinecraftValley,

    /// <summary>
    /// 展示墙体、拱门、立柱和模块化建筑
    /// </summary>
    Architecture,

    /// <summary>
    /// 展示多相机对象和渲染视图切换
    /// </summary>
    CameraGallery,

    /// <summary>
    /// 展示 1024×1024×1024 的超大型稀疏建筑和长距离视锥
    /// </summary>
    LargeWorld,

    /// <summary>
    /// 展示小型花园、中型建筑群和大型远景塔楼的完整渲染效果
    /// </summary>
    RenderingGallery,

    /// <summary>展示月面基地、穹顶舱、漫游车与太阳能阵列</summary>
    MoonbaseOutpost,

    /// <summary>展示玻璃穹顶、机械骨架与温室植物</summary>
    ClockworkConservatory,

    /// <summary>展示沙漠绿洲、商队驿站、拱廊与夜间灯火</summary>
    DesertCaravanserai,

    /// <summary>
    /// 展示 1024×1024 的完整仙域大陆：六大地形分区、宗门、坊市、浮空仙岛与古迹
    /// </summary>
    XianxiaContinent,
}

/// <summary>
/// 模板列表项，供欢迎窗口和新建项目窗口展示
/// </summary>
public sealed record ProjectTemplateDefinition(
    ProjectTemplateId Id,
    string Name,
    string Description,
    string Focus,
    string Scale,
    string LearningGoal);

/// <summary>
/// 内置项目模板目录和资源生成器
/// </summary>
public static class ProjectTemplateCatalog
{
    /// <summary>
    /// 模板的原始登记顺序，只在规模分组相同时决定先后
    /// </summary>
    private static readonly ProjectTemplateDefinition[] Definitions =
    [
        new(ProjectTemplateId.Empty, "空", "创建没有场景对象的空项目。", "从零开始", "起步", "自行创建场景对象"),
        new(ProjectTemplateId.DefaultRoom, "默认模板", "带桌椅、可乐纸杯、饼干、壁画和落地电风扇的室内场景。", "室内 · 材质 · 家具", "起步", "选择并编辑独立物件"),
        new(ProjectTemplateId.CornellBox, "康奈尔盒光照", "红绿墙、顶灯、漫反射方块与镜面球构成经典间接光实验。", "间接光 · 颜色溢出 · 软阴影", "小型", "观察彩色墙面对中性物体的间接照明"),
        new(ProjectTemplateId.SphereOptics, "玻璃球与棋盘", "玻璃球、金属球和棋盘背景展示透射、折射与反射。", "玻璃 · 金属 · 反射 · 折射", "小型", "比较玻璃球与金属球的光线响应"),
        new(ProjectTemplateId.GlassStudy, "玻璃透射对照", "在棋盘背景前比较透明玻璃、有色玻璃和毛玻璃。", "透射 · 折射 · 毛玻璃", "小型", "观察不同玻璃对背景的影响"),
        new(ProjectTemplateId.CloudAndFog, "云雾与散射", "在山景中观察漂浮云朵、薄雾和浓雾的体积效果。", "云朵 · 雾气 · 体积散射", "小型", "调整雾化密度、散射色和不透明度"),
        new(ProjectTemplateId.WaterStudy, "水面与池底", "用浅水池、池底棋盘和水中物件观察水体透射与折射。", "水面 · 吸收 · 折射", "小型", "比较水深及吸收对池底的影响"),
        new(ProjectTemplateId.XianxiaContinent, "仙域大陆", "1024×1024 的整块修仙大陆：天柱峰、五方地脉、青云剑宗、长乐坊市、三座浮空仙岛与锁妖塔、剑冢、传送大阵等古迹共同组成可漫游的世界。", "超大型世界 · 地理 · 建筑群 · 浮空岛 · 多层叙事", "超大型", "理解分区分件、世界坐标与镜头组织"),
        new(ProjectTemplateId.Showcase, "小屋与花园", "一座有生活痕迹的小屋、井台、花圃、林木与夜间提灯。打开即可理解场景组织。", "基础建模 · 材质 · 自发光", "小型", "学习对象、体素数据和相机的关系"),
        new(ProjectTemplateId.MaterialLab, "材质实验室", "把粗糙度、金属、玻璃、水和自发光样品放在同一间可比较的展厅。", "材质 · 透明 · 发光", "小型", "比较材质参数如何改变画面"),
        new(ProjectTemplateId.PaletteWorkshop, "调色板工坊", "用色卡墙、螺旋看台和中央发光展台演示颜色层级，而不是堆叠孤立色块。", "调色板 · 纯色材质 · 材质属性", "小型", "建立可复用的颜色系统"),
        new(ProjectTemplateId.SparseWorld, "灯塔与远方", "一座灯塔岛连接悬索桥与远处信号塔，地标分散在负坐标和远距离位置。", "大世界 · 稀疏存储 · 负坐标", "大型", "理解稀疏世界和远距离编辑"),
        new(ProjectTemplateId.LodPlayground, "建筑细节展示场", "同一座建筑从完整细节到块状轮廓排列成渐变展廊，距离变化一眼可见。", "LOD · 场景性能 · 批量对象", "中型", "验证距离 LOD 的视觉与性能取舍"),
        new(ProjectTemplateId.ProceduralTerrain, "山地湖泊", "从沙滩、湖盆、雪线到针叶林逐层过渡的山地剖面，适合观察生成结果。", "程序化 · 地形 · 大量体素", "大型", "理解噪声地形和分层材质"),
        new(ProjectTemplateId.MinecraftValley, "Minecraft 山谷", "湖盆、瀑布、灯笼小径和混交林组成可漫游的山谷，树木作为独立层保存。", "MC 风格 · 分层结构 · 水面 · 发光", "中型", "学习分层世界和对象所有权"),
        new(ProjectTemplateId.Architecture, "柱廊庭院", "围绕镜水池组织的柱廊、圆拱、台阶和铜皮穹顶，强调比例和轴线。", "建筑 · 对称 · 曲线", "中型", "练习模块化建筑和对称构图"),
        new(ProjectTemplateId.CameraGallery, "摄影构图室", "螺旋雕塑、参照柱和三条不同视线组成小型摄影棚，每台相机都有明确任务。", "相机 · 构图 · 景深", "中型", "学习相机、构图和景深"),
        new(ProjectTemplateId.LargeWorld, "巨塔与圣坛", "阶梯圣坛、空心巨塔、悬链拱桥与环带街区组成真正用于性能验证的远景建筑群。", "大世界 · LOD · 建筑 · 性能压力", "超大型", "验证大坐标、LOD 和 GPU 工作集"),
        new(ProjectTemplateId.RenderingGallery, "海湾小镇", "从近处玻璃凉亭、码头船队到远处灯塔和塔楼，形成有前景、中景、远景的海湾小镇。", "小中大规模 · 光照 · AO · 透明 · 发光 · 路径追踪", "小—大型", "用一个完整场景体验最终渲染链路"),
        new(ProjectTemplateId.MoonbaseOutpost, "月球科研站", "玄武岩环形坑、气密穹舱、太阳能阵列、月面车与导航信标构成一座可拆解的月球科研站。", "科幻建模 · 玻璃 · 金属 · 发光 · 分层", "中型", "练习复杂硬表面结构与多材质场景组织"),
        new(ProjectTemplateId.ClockworkConservatory, "机械温室", "黄铜肋骨托起玻璃穹顶，中央古树、环形水渠、花床与机械钟芯形成冷暖交织的展示庭园。", "有机造型 · 玻璃 · 金属 · 自发光 · 剖切", "中型", "组合曲面、规则结构与可独立编辑的场景部件"),
        new(ProjectTemplateId.DesertCaravanserai, "沙漠绿洲驿站", "风蚀岩台上的拱门驿站围绕水池展开，棕榈、织棚、驮队与暖色灯笼铺出一段商路故事。", "建筑 · 地形 · 水体 · 场景叙事 · 发光", "中型", "练习建筑构图、重复模块和地形材质过渡"),
    ];

    /// <summary>
    /// 所有可选模板按用途分组，组内按场景规模从小到大排列
    /// </summary>
    public static IReadOnlyList<ProjectTemplateDefinition> All { get; } = Definitions
        .Select((template, index) => (template, index))
        .OrderBy(entry => CategoryRank(CategoryFor(entry.template.Id)))
        .ThenBy(entry => ScaleRank(entry.template.Scale))
        .ThenBy(entry => entry.index)
        .Select(entry => entry.template)
        .ToArray();

    /// <summary>按实际使用目的组织模板；空项目始终位于首组第一项。</summary>
    public static string CategoryFor(ProjectTemplateId id) => id switch
    {
        ProjectTemplateId.Empty or ProjectTemplateId.DefaultRoom => "入门",
        ProjectTemplateId.MaterialLab or ProjectTemplateId.PaletteWorkshop or ProjectTemplateId.GlassStudy or
            ProjectTemplateId.CornellBox or ProjectTemplateId.SphereOptics or
            ProjectTemplateId.CloudAndFog or ProjectTemplateId.WaterStudy => "材质与渲染",
        ProjectTemplateId.Showcase or ProjectTemplateId.Architecture or ProjectTemplateId.MoonbaseOutpost or
            ProjectTemplateId.ClockworkConservatory or ProjectTemplateId.DesertCaravanserai => "建模与场景",
        ProjectTemplateId.ProceduralTerrain or ProjectTemplateId.MinecraftValley or ProjectTemplateId.SparseWorld or
            ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery or ProjectTemplateId.XianxiaContinent => "地形与大世界",
        _ => "相机与性能",
    };

    private static int CategoryRank(string category) => category switch
    {
        "入门" => 0, "建模与场景" => 1, "材质与渲染" => 2,
        "地形与大世界" => 3, "相机与性能" => 4, _ => 5,
    };

    /// <summary>
    /// 规模标签的排序权重：起步最小，超大型最大；跨尺度的展示场景排在大型之后
    /// </summary>
    private static int ScaleRank(string scale) => scale switch
    {
        "起步" or "无" => 0,
        "小型" => 1,
        "中型" => 2,
        "大型" => 3,
        "小—大型" => 4,
        "超大型" => 5,
        _ => 9,
    };

    /// <summary>
    /// 返回指定模板的定义
    /// </summary>
    /// <param name="id">模板 ID</param>
    /// <returns>模板定义</returns>
    public static ProjectTemplateDefinition Get(ProjectTemplateId id) =>
        All.FirstOrDefault(template => template.Id == id) ?? All[0];

    /// <summary>
    /// 将模板资源写入新项目
    /// </summary>
    /// <param name="project">新项目</param>
    /// <param name="id">模板 ID</param>
    /// <param name="report">进度回调</param>
    public static void Populate(ProjectHandle project, ProjectTemplateId id, Action<string, float, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        report?.Invoke("正在生成示例体素模型…", .74F, true);
        var settings = CreateSettings(id);
        settings.TemplateId = id.ToString();
        if (id == ProjectTemplateId.Empty)
            SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), new SceneDocument { Name = "主场景" });
        else
        {
            WriteMaterials(project, id);
            WriteProjectPalette(project);
            WriteRenderPreset(project, settings);
            if (id == ProjectTemplateId.XianxiaContinent)
            {
                Xianxia.XianxiaWorld.Populate(project, report);
            }
            else if (id == ProjectTemplateId.MinecraftValley)
            {
                var terrain = TemplateScenes.BuildValleyTerrain();
                var trees = TemplateScenes.BuildValleyTrees(terrain);
                var modelParts = WriteTemplateModelParts(project, id, terrain);
                for (var index = 0; index < trees.Count; index++)
                {
                    var treeFile = $"Minecraft山谷_树{index + 1}.mvmodel";
                    LargeWorldFormat.Write(Path.Combine(project.AssetsPath, "Worlds", treeFile), trees[index]);
                    modelParts.Add(new SceneModelPart($"树 {index + 1}", "树木",
                        "Assets/Worlds/" + treeFile, trees[index].OccupiedVoxelCount,
                        GetWorldOrigin(trees[index])));
                }

                WriteScene(project, Get(id), terrain.OccupiedVoxelCount, "Assets/Worlds/山谷分层模型.mvmodel",
                    modelParts: modelParts, framingWorld: terrain);
            }
            else if (id is ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery)
            {
                var largeFileName = id == ProjectTemplateId.LargeWorld ? "超大型场景.mvworld" : "渲染体验馆.mvworld";
                var largePath = Path.Combine(project.AssetsPath, "Worlds", largeFileName);
                var world = BuildWorld(id, VoxelPalette.Build());
                LargeWorldFormat.Write(largePath, world);
                var modelParts = WriteTemplateModelParts(project, id, world);
                WriteScene(project, Get(id), world.OccupiedVoxelCount, $"Assets/Worlds/{largeFileName}",
                    modelParts: modelParts, framingWorld: world);
            }
            else
            {
                var world = BuildWorld(id, VoxelPalette.Build());
                var modelParts = WriteTemplateModelParts(project, id, world);
                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    "Assets/Worlds/分层模型.mvmodel",
                    cameraCount: id == ProjectTemplateId.CameraGallery ? 3 : 1,
                    modelParts: modelParts,
                    framingWorld: world);
            }
        }

        report?.Invoke("正在写入模板配置…", .91F, false);
        SettingsStore.SaveProject(project, settings);
    }

    /// <summary>
    /// 用外部 MagicaVoxel 单模型文件生成项目内容：写入原生体素资产，并建立只包含该模型的主场景
    /// </summary>
    /// <param name="project">已经创建好的空项目</param>
    /// <param name="voxFilePath">MagicaVoxel .vox 文件绝对路径</param>
    /// <param name="report">进度回调</param>
    /// <returns>导入的体素数</returns>
    public static int PopulateFromVox(ProjectHandle project, string voxFilePath, Action<string, float, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(voxFilePath);
        report?.Invoke("正在读取 MagicaVoxel 文件…", .72F, true);
        var document = VoxFormat.ReadDocument(voxFilePath);
        var modelName = Path.GetFileNameWithoutExtension(voxFilePath).Trim();
        if (modelName.Length == 0) modelName = "导入模型";
        var safeName = ToSafeFileName(modelName);
        var multiple = document.Instances.Count > 1 || document.Models.Count > 1;

        report?.Invoke("正在写入项目体素资产…", .82F, true);
        // 一个模型写一份资产，多个实例共享同一份，和 MagicaVoxel 的 Reference 语义一致
        var assets = new string[document.Models.Count];
        var voxelCounts = new int[document.Models.Count];
        var worldOrigins = new VoxelCoord64[document.Models.Count];
        for (var index = 0; index < document.Models.Count; index++)
        {
            var world = BuildModelWorld(document.Models[index]);
            var fileName = (multiple ? $"{safeName}_{index + 1}" : safeName) + ".mvmodel";
            var absolutePath = Path.Combine(project.AssetsPath, "Worlds", fileName);
            if (world.OccupiedVoxelCount == 0) LargeWorldFormat.WriteEmpty(absolutePath);
            else LargeWorldFormat.Write(absolutePath, world);
            AssetMetaStore.EnsureMeta(absolutePath);
            assets[index] = "Assets/Worlds/" + fileName;
            voxelCounts[index] = world.OccupiedVoxelCount;
            worldOrigins[index] = GetWorldOrigin(world);
        }

        WriteVoxMaterials(project, document, safeName, modelName);

        report?.Invoke("正在生成主场景…", .90F, true);
        var layerIndexById = new Dictionary<int, int>();
        for (var index = 0; index < document.Layers.Count; index++) layerIndexById[document.Layers[index].Id] = index;
        var parts = new List<SceneModelPart>(document.Instances.Count);
        long totalVoxels = 0;
        foreach (var instance in document.Instances)
        {
            var name = document.Instances.Count == 1 ? modelName : instance.Name;
            parts.Add(new SceneModelPart(name, "MagicaVoxel 导入", assets[instance.ModelIndex],
                voxelCounts[instance.ModelIndex], instance.Offset + worldOrigins[instance.ModelIndex],
                layerIndexById.GetValueOrDefault(instance.LayerId), instance.Hidden, instance.Rotation));
            totalVoxels += voxelCounts[instance.ModelIndex];
        }

        var layers = document.Layers.Count == 0
            ? null
            : document.Layers.Select(layer => (layer.Name, layer.Hidden)).ToList();
        var definition = new ProjectTemplateDefinition(ProjectTemplateId.Empty, modelName,
            "由 MagicaVoxel 文件创建", "MagicaVoxel 兼容导入",
            multiple ? $"{parts.Count} 个对象" : "单模型", "继续在原生格式上编辑");
        WriteScene(project, definition, (int)Math.Min(totalVoxels, int.MaxValue), assets[0], cameraCount: 1,
            modelParts: parts, framingWorld: VoxFormat.Compose(document), layers: layers);

        report?.Invoke("正在写入项目设置…", .94F, false);
        var settings = CreateSettings(ProjectTemplateId.Empty);
        settings.TemplateId = "MagicaVoxel 导入";
        SettingsStore.SaveProject(project, settings);
        return (int)Math.Min(totalVoxels, int.MaxValue);
    }

    /// <summary>
    /// 把 .vox 的 MATL 写成项目的 .material 资产：材质 id 就是调色板索引，
    /// 体素的 MaterialSlot 早已指向同一个索引，导入后立刻能在材质面板里看到。
    /// </summary>
    private static void WriteVoxMaterials(ProjectHandle project, VoxDocument document,
        string safeName, string modelName)
    {
        if (document.Materials.Count == 0) return;
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        foreach (var source in document.Materials)
        {
            var slot = Math.Clamp(source.Id, 0, MaterialAsset.TableSlots - 1);
            var asset = new MaterialAsset { Name = $"{modelName} 材质 {slot}", Slot = slot };
            if (source.Roughness >= 0F) asset.Roughness = Math.Clamp(source.Roughness, 0.01F, 1F);
            if (source.Specular >= 0F) asset.Specular = Math.Clamp(source.Specular, 0F, 1F);
            if (source.Ior > 0F) asset.Ior = Math.Clamp(source.Ior, 1F, 2.5F);
            if (source.Absorption >= 0F) asset.AbsorptionStep = Math.Clamp(source.Absorption, 0F, 8F);
            switch (source.Type.ToLowerInvariant())
            {
                case "_metal":
                    asset.Metallic = 1F;
                    break;
                case "_glass":
                    asset.Metallic = 0F;
                    asset.Ior ??= 1.5F;
                    break;
                case "_emit":
                    asset.EmissiveScale = source.Flux > 0F ? Math.Clamp(source.Flux, 0F, 32F) : 1F;
                    break;
                case "_diffuse":
                    asset.Metallic = 0F;
                    break;
            }

            asset.Save(Path.Combine(directory, $"{safeName}_材质{slot:000}.material"));
        }
    }

    /// <summary>把 .vox 里的一个模型转成原生体素世界：MagicaVoxel 的 Z 轴映射到 MVoxel 的 Y 轴</summary>
    private static VoxelWorld BuildModelWorld(VoxModel model)
    {
        var world = new VoxelWorld();
        foreach (var voxel in model.Voxels)
            world.Set(new VoxelCoord64(voxel.X, voxel.Z, voxel.Y), new VoxelValue(voxel.Color,
                (ushort)Math.Clamp(voxel.PaletteIndex, 0, MaterialAsset.TableSlots - 1),
                voxel.Color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        return world;
    }

    /// <summary>
    /// 把用户提供的名字转换为可用于资产文件的安全文件名
    /// </summary>
    private static string ToSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "导入模型" : result;
    }

    /// <summary>
    /// 构建模板对应的内存世界，供测试和编辑器初始化使用
    /// </summary>
    /// <param name="id">模板 ID</param>
    /// <param name="palette">当前调色板</param>
    /// <returns>模板世界</returns>
    public static VoxelWorld BuildWorld(ProjectTemplateId id, Rgba32[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return id switch
        {
            ProjectTemplateId.DefaultRoom => TemplateScenes.BuildDefaultRoom(),
            ProjectTemplateId.GlassStudy => TemplateScenes.BuildGlassStudy(),
            ProjectTemplateId.CornellBox => TemplateScenes.BuildCornellBox(),
            ProjectTemplateId.SphereOptics => TemplateScenes.BuildSphereOptics(),
            ProjectTemplateId.CloudAndFog => TemplateScenes.BuildCloudAndFog(),
            ProjectTemplateId.WaterStudy => TemplateScenes.BuildWaterStudy(),
            ProjectTemplateId.Showcase => TemplateScenes.BuildCottageGarden(),
            ProjectTemplateId.MaterialLab => TemplateScenes.BuildMaterialLab(),
            ProjectTemplateId.PaletteWorkshop => TemplateScenes.BuildPaletteWorkshop(palette),
            ProjectTemplateId.SparseWorld => TemplateScenes.BuildSparseWorld(),
            ProjectTemplateId.LodPlayground => TemplateScenes.BuildLodPlayground(),
            ProjectTemplateId.ProceduralTerrain => TemplateScenes.BuildProceduralTerrain(),
            ProjectTemplateId.MinecraftValley => TemplateScenes.BuildValleyTerrain(),
            ProjectTemplateId.Architecture => TemplateScenes.BuildColonnade(),
            ProjectTemplateId.CameraGallery => TemplateScenes.BuildSculptureHall(),
            ProjectTemplateId.LargeWorld => TemplateScenes.BuildMegastructure(),
            ProjectTemplateId.RenderingGallery => TemplateScenes.BuildHarborTown(),
            ProjectTemplateId.MoonbaseOutpost => TemplateScenes.BuildMoonbaseOutpost(),
            ProjectTemplateId.ClockworkConservatory => TemplateScenes.BuildClockworkConservatory(),
            ProjectTemplateId.DesertCaravanserai => TemplateScenes.BuildDesertCaravanserai(),
            ProjectTemplateId.XianxiaContinent => Xianxia.XianxiaTerrain.Build().Worlds[Xianxia.XianxiaRegion.Summit],
            _ => new VoxelWorld(),
        };
    }

    /// <summary>
    /// 为模板写入配套的 .material 材质定义，槽位与场景构建器约定一致
    /// </summary>
    private static void WriteMaterials(ProjectHandle project, ProjectTemplateId id)
    {
        if (id == ProjectTemplateId.Empty) return;
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        var materials = new List<MaterialAsset>
        {
            new() { Name = "清水玻璃", Slot = TemplateKit.SlotGlass, Roughness = .06F, Metallic = 0F, AbsorptionStep = .8F },
            new() { Name = "池水", Slot = TemplateKit.SlotWater, Roughness = .08F, Metallic = 0F, AbsorptionStep = 1.4F,
                AbsorptionColor = new Rgba32(48, 118, 168) },
            new() { Name = "抛光金属", Slot = TemplateKit.SlotMetal, Roughness = .12F, Metallic = 1F },
            new() { Name = "灯与炉", Slot = TemplateKit.SlotGlow, EmissiveScale = 6F },
        };
        if (id == ProjectTemplateId.DefaultRoom)
        {
            materials[0].Ior = 1.5F;
            materials[0].Opacity = .08F;
            materials[0].AbsorptionStep = .25F;
            materials[0].VolumeScattering = 0F;
            materials.Add(new MaterialAsset { Name = "可乐", Slot = TemplateKit.SlotCola,
                Roughness = .08F, Metallic = 0F, AbsorptionStep = 3F, Ior = 1.33F,
                Opacity = .8F, VolumeScattering = .08F });
        }
        if (id is ProjectTemplateId.GlassStudy or ProjectTemplateId.CloudAndFog or ProjectTemplateId.WaterStudy)
        {
            materials.Clear();
            var opticalMaterials = new[]
            {
                new MaterialAsset { Name = "透明玻璃", Slot = 10, Ior = 1.5F, Roughness = .02F,
                    Opacity = .05F, AbsorptionStep = .2F, VolumeScattering = 0F },
                new MaterialAsset { Name = "毛玻璃", Slot = 11, Ior = 1.5F, Roughness = .8F,
                    Opacity = .22F, AbsorptionStep = .3F, VolumeScattering = .12F, HazeDensity = .06F },
                new MaterialAsset { Name = "琥珀玻璃", Slot = 12, Ior = 1.5F, Roughness = .04F,
                    Opacity = .5F, AbsorptionStep = 1.4F, VolumeScattering = .04F },
                new MaterialAsset { Name = "薄雾", Slot = 13, Ior = 1F, Roughness = .9F,
                    Opacity = .32F, AbsorptionStep = .05F, HazeDensity = .12F, VolumeScattering = .65F,
                    ScatteringColor = new Rgba32(218, 228, 236) },
                new MaterialAsset { Name = "云朵", Slot = 14, Ior = 1F, Roughness = 1F,
                    Opacity = .65F, AbsorptionStep = .05F, HazeDensity = .32F, VolumeScattering = .9F,
                    ScatteringColor = new Rgba32(244, 246, 250) },
                new MaterialAsset { Name = "浓雾", Slot = 16, Ior = 1F, Roughness = 1F,
                    Opacity = .75F, AbsorptionStep = .05F, HazeDensity = .45F, VolumeScattering = .85F,
                    ScatteringColor = new Rgba32(211, 222, 232) },
                new MaterialAsset { Name = "清水", Slot = 15, Ior = 1.33F, Roughness = .03F,
                    Opacity = .18F, AbsorptionStep = .4F, VolumeScattering = .03F,
                    AbsorptionColor = new Rgba32(155, 218, 229) },
            };
            materials.AddRange(opticalMaterials.Where(material => id switch
            {
                ProjectTemplateId.GlassStudy => material.Slot is >= 10 and <= 12,
                ProjectTemplateId.CloudAndFog => material.Slot is 13 or 14 or 16,
                _ => material.Slot == 15,
            }));
        }
        if (id == ProjectTemplateId.SphereOptics)
            materials.Add(new MaterialAsset { Name = "实验玻璃", Slot = 10, Ior = 1.5F, Roughness = .02F,
                Opacity = .08F, AbsorptionStep = .3F, VolumeScattering = 0F });
        if (id == ProjectTemplateId.MaterialLab)
        {
            for (var step = 0; step < 5; step++)
            {
                materials.Add(new MaterialAsset
                {
                    Name = $"粗糙度阶梯 {step + 1}",
                    Slot = 5 + step,
                    Roughness = .05F + step * .22F,
                    Metallic = .2F,
                });
            }
        }

        foreach (var material in materials)
        {
            material.Save(Path.Combine(directory, $"{material.Name}.material"));
        }
    }

    private static void WriteProjectPalette(ProjectHandle project)
    {
        PaletteFormat.Save(Path.Combine(project.AssetsPath, "Palettes", EditorPalettes.ProjectPaletteName),
            VoxelPalette.Build());
    }

    private static void WriteRenderPreset(ProjectHandle project, ProjectSettingsDocument settings)
    {
        var fileName = string.IsNullOrWhiteSpace(settings.DefaultRenderPreset)
            ? "默认.mvrenderpreset"
            : SanitizeFileName(settings.DefaultRenderPreset) + ".mvrenderpreset";
        settings.RenderSettings.SaveTo(Path.Combine(project.AssetsPath, "RenderPresets", fileName));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "默认" : sanitized;
    }

    /// <summary>
    /// 创建模板对应的项目级配置
    /// </summary>
    /// <param name="id">模板 ID</param>
    /// <returns>项目配置</returns>
    private static ProjectSettingsDocument CreateSettings(ProjectTemplateId id)
    {
        var settings = ProjectSettingsDocument.CreateDefault();
        settings.TemplateId = id.ToString();
        switch (id)
        {
            case ProjectTemplateId.CornellBox:
                settings.DefaultRenderPreset = "康奈尔盒光照";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.SunEnabled = false;
                settings.RenderSettings.EmissiveVoxels = true;
                settings.RenderSettings.EmissiveIntensity = 12F;
                settings.RenderSettings.PathMaxAccumulatedSamples = 256;
                settings.RenderSettings.MaxBounces = 8;
                break;
            case ProjectTemplateId.SphereOptics:
            case ProjectTemplateId.GlassStudy:
            case ProjectTemplateId.CloudAndFog:
            case ProjectTemplateId.WaterStudy:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.TransmissiveAbsorption = 1F;
                break;
            case ProjectTemplateId.SparseWorld:
                settings.DefaultRenderPreset = "大世界稀疏预览";
                settings.RenderSettings.SunElevationDegrees = 26F;
                settings.RenderSettings.SunAzimuthDegrees = 118F;
                settings.RenderSettings.SkyAmbient = .5F;
                settings.RenderSettings.EmissiveIntensity = 14F;
                break;
            case ProjectTemplateId.MinecraftValley:
                settings.DefaultRenderPreset = "Minecraft 山谷";
                settings.RenderSettings.SunElevationDegrees = 34F;
                settings.RenderSettings.SunAzimuthDegrees = 128F;
                settings.RenderSettings.ShadowSoftness = .1F;
                settings.RenderSettings.ShadowSamples = 4;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .82F;
                settings.RenderSettings.AoPower = 1.2F;
                settings.RenderSettings.SkyAmbient = .5F;
                settings.RenderSettings.EmissiveIntensity = 12F;
                settings.RenderSettings.TransmissiveAbsorption = .3F;
                settings.RenderSettings.Tone = ToneCurve.Reinhard;
                settings.RenderSettings.Exposure = .12F;
                break;
            case ProjectTemplateId.LodPlayground:
                settings.DefaultRenderPreset = "LOD 练习";
                break;
            case ProjectTemplateId.Showcase:
                settings.DefaultRenderPreset = "基础展示";
                settings.RenderSettings.SunElevationDegrees = 38F;
                settings.RenderSettings.SunAzimuthDegrees = 132F;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathSamplesPerFrame = 2;
                settings.RenderSettings.PathMaxAccumulatedSamples = 384;
                settings.RenderSettings.ShadowSoftness = .08F;
                settings.RenderSettings.ShadowSamples = 4;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .78F;
                settings.RenderSettings.AoPower = 1.15F;
                settings.RenderSettings.SkyAmbient = .48F;
                settings.RenderSettings.Tone = ToneCurve.Reinhard;
                settings.RenderSettings.Exposure = .15F;
                settings.RenderSettings.PathDenoising = true;
                settings.RenderSettings.PathDenoiseStrength = .62F;
                settings.RenderSettings.EmissiveIntensity = 14F;
                settings.RenderSettings.TransmissiveAbsorption = .6F;
                break;
            case ProjectTemplateId.MaterialLab:
                settings.DefaultRenderPreset = "材质实验";
                settings.RenderSettings.SunElevationDegrees = 44F;
                settings.RenderSettings.SunAzimuthDegrees = 150F;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathSamplesPerFrame = 2;
                settings.RenderSettings.PathMaxAccumulatedSamples = 512;
                settings.RenderSettings.ShadowSoftness = .04F;
                settings.RenderSettings.ShadowSamples = 4;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .88F;
                settings.RenderSettings.AoPower = 1.25F;
                settings.RenderSettings.SkyAmbient = .36F;
                settings.RenderSettings.AmbientTint = .12F;
                settings.RenderSettings.PathRoughness = .3F;
                settings.RenderSettings.PathMetalness = .28F;
                settings.RenderSettings.PathSpecularF0 = .08F;
                settings.RenderSettings.TransmissiveAbsorption = .42F;
                settings.RenderSettings.EmissiveIntensity = 22F;
                settings.RenderSettings.PathDenoiseStrength = .65F;
                break;
            case ProjectTemplateId.PaletteWorkshop:
                settings.DefaultRenderPreset = "调色板材质展示";
                settings.RenderSettings.PathMaxAccumulatedSamples = 256;
                settings.RenderSettings.PaletteShading = true;
                settings.RenderSettings.Saturation = 1.12F;
                settings.RenderSettings.ColorContrast = 1.08F;
                settings.RenderSettings.Brightness = 1.02F;
                settings.RenderSettings.HueShiftDegrees = 6F;
                settings.RenderSettings.Tone = ToneCurve.Linear;
                break;
            case ProjectTemplateId.ProceduralTerrain:
                settings.DefaultRenderPreset = "山地湖泊";
                settings.RenderSettings.SunElevationDegrees = 30F;
                settings.RenderSettings.SunAzimuthDegrees = 118F;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 384;
                settings.RenderSettings.ShadowSoftness = .1F;
                settings.RenderSettings.ShadowSamples = 5;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .9F;
                settings.RenderSettings.SkyAmbient = .52F;
                settings.RenderSettings.Tone = ToneCurve.Filmic;
                settings.RenderSettings.Exposure = -.15F;
                settings.RenderSettings.PathDenoiseStrength = .64F;
                settings.RenderSettings.TransmissiveAbsorption = .4F;
                break;
            case ProjectTemplateId.Architecture:
                settings.DefaultRenderPreset = "建筑光照";
                settings.RenderSettings.SunElevationDegrees = 42F;
                settings.RenderSettings.SunAzimuthDegrees = 140F;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 448;
                settings.RenderSettings.ShadowSoftness = .07F;
                settings.RenderSettings.ShadowSamples = 5;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .95F;
                settings.RenderSettings.AoPower = 1.4F;
                settings.RenderSettings.PathRoughness = .24F;
                settings.RenderSettings.PathMetalness = .08F;
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.PathDenoiseStrength = .66F;
                settings.RenderSettings.TransmissiveAbsorption = .5F;
                break;
            case ProjectTemplateId.CameraGallery:
                settings.DefaultRenderPreset = "多相机景深";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 384;
                settings.RenderSettings.CameraFovDegrees = 42F;
                settings.RenderSettings.DepthOfField = true;
                settings.RenderSettings.ApertureRadius = .06F;
                settings.RenderSettings.FocusDistance = 24F;
                settings.RenderSettings.Tone = ToneCurve.Reinhard;
                settings.RenderSettings.PathDenoiseStrength = .68F;
                settings.RenderSettings.EmissiveIntensity = 16F;
                break;
            case ProjectTemplateId.LargeWorld:
                settings.DefaultRenderPreset = "超大型场景性能验证";
                settings.RenderSettings.PathMaxAccumulatedSamples = 128;
                settings.RenderSettings.EmissiveIntensity = 12F;
                break;
            case ProjectTemplateId.MoonbaseOutpost:
                settings.DefaultRenderPreset = "月面冷光";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 512;
                settings.RenderSettings.SunElevationDegrees = 28F;
                settings.RenderSettings.SunAzimuthDegrees = 208F;
                settings.RenderSettings.SkyAmbient = .26F;
                settings.RenderSettings.AmbientTint = .16F;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .48F;
                settings.RenderSettings.EmissiveIntensity = 10F;
                settings.RenderSettings.TransmissiveAbsorption = .32F;
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.Exposure = .18F;
                break;
            case ProjectTemplateId.ClockworkConservatory:
                settings.DefaultRenderPreset = "温室午后";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 512;
                settings.RenderSettings.SunElevationDegrees = 48F;
                settings.RenderSettings.SunAzimuthDegrees = 136F;
                settings.RenderSettings.SkyAmbient = .48F;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .78F;
                settings.RenderSettings.EmissiveIntensity = 16F;
                settings.RenderSettings.TransmissiveAbsorption = .25F;
                settings.RenderSettings.Tone = ToneCurve.Reinhard;
                break;
            case ProjectTemplateId.DesertCaravanserai:
                settings.DefaultRenderPreset = "绿洲蓝调时刻";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 512;
                settings.RenderSettings.SunElevationDegrees = 8F;
                settings.RenderSettings.SunAzimuthDegrees = 252F;
                settings.RenderSettings.SkyTop = new Vector3(.055F, .09F, .2F);
                settings.RenderSettings.SkyHorizon = new Vector3(.72F, .39F, .23F);
                settings.RenderSettings.SkyAmbient = .36F;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .9F;
                settings.RenderSettings.EmissiveIntensity = 22F;
                settings.RenderSettings.TransmissiveAbsorption = .24F;
                settings.RenderSettings.Tone = ToneCurve.Filmic;
                settings.RenderSettings.Exposure = .28F;
                break;
            case ProjectTemplateId.XianxiaContinent:
                settings.DefaultRenderPreset = "仙域 · 九霄晨曦";
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.SunElevationDegrees = 38F;
                settings.RenderSettings.SunAzimuthDegrees = 116F;
                settings.RenderSettings.SunIntensity = 1.28F;
                settings.RenderSettings.SunColor = new Vector3(1F, .87F, .72F);
                settings.RenderSettings.SkyTop = new Vector3(.30F, .48F, .72F);
                settings.RenderSettings.SkyHorizon = new Vector3(.72F, .82F, .92F);
                settings.RenderSettings.SkyAmbient = .8F;
                settings.RenderSettings.AmbientTint = .2F;
                settings.RenderSettings.ShadowSoftness = .1F;
                settings.RenderSettings.ShadowSamples = 6;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .9F;
                settings.RenderSettings.AoPower = 1.32F;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathSamplesPerFrame = 2;
                settings.RenderSettings.PathMaxAccumulatedSamples = 640;
                settings.RenderSettings.MaxBounces = 6;
                settings.RenderSettings.PathDenoising = true;
                settings.RenderSettings.PathDenoiseStrength = .66F;
                settings.RenderSettings.PathRoughness = .2F;
                settings.RenderSettings.PathMetalness = .1F;
                settings.RenderSettings.PathSpecularF0 = .05F;
                settings.RenderSettings.EmissiveVoxels = true;
                settings.RenderSettings.EmissiveIntensity = 6F;
                settings.RenderSettings.TransmissiveAbsorption = .34F;
                settings.RenderSettings.BloomEnabled = true;
                settings.RenderSettings.BloomIntensity = .2F;
                settings.RenderSettings.BloomThreshold = 1.05F;
                settings.RenderSettings.BloomRadius = 14F;
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.Exposure = .24F;
                settings.RenderSettings.Saturation = 1.12F;
                settings.RenderSettings.ColorContrast = 1F;
                settings.RenderSettings.Brightness = 1.02F;
                settings.RenderSettings.WhiteBalance = 5200F;
                settings.RenderSettings.Vignette = .18F;
                settings.RenderSettings.CameraFovDegrees = 46F;
                settings.RenderSettings.Dither = true;
                settings.RenderSettings.RadianceClamp = 8F;
                settings.RenderSettings.SunDirectLighting = true;
                settings.RenderSettings.AutoPerformanceScaling = false;
                break;
            case ProjectTemplateId.RenderingGallery:
                settings.DefaultRenderPreset = "全效果展示";
                settings.RenderSettings.ShowGround = true;
                settings.RenderSettings.GroundColor = new Vector3(.18F, .24F, .30F);
                settings.RenderSettings.SunElevationDegrees = 24F;
                settings.RenderSettings.SunAzimuthDegrees = 122F;
                settings.RenderSettings.SkyTop = new Vector3(.08F, .18F, .38F);
                settings.RenderSettings.SkyHorizon = new Vector3(.62F, .72F, .88F);
                settings.RenderSettings.SkyAmbient = .55F;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .95F;
                settings.RenderSettings.AoPower = 1.35F;
                settings.RenderSettings.ShadowSoftness = .12F;
                settings.RenderSettings.ShadowSamples = 5;
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 768;
                settings.RenderSettings.MaxBounces = 7;
                settings.RenderSettings.PathDenoising = true;
                settings.RenderSettings.PathDenoiseStrength = .68F;
                settings.RenderSettings.EmissiveVoxels = true;
                settings.RenderSettings.EmissiveIntensity = 20F;
                settings.RenderSettings.TransmissiveAbsorption = .28F;
                settings.RenderSettings.DepthOfField = true;
                settings.RenderSettings.ApertureRadius = .045F;
                settings.RenderSettings.FocusDistance = 36F;
                settings.RenderSettings.SceneBackgroundColor = new Vector3(.02F, .025F, .05F);
                settings.RenderSettings.Saturation = 1.08F;
                settings.RenderSettings.ColorContrast = 1.12F;
                settings.RenderSettings.Brightness = 1.02F;
                settings.RenderSettings.HueShiftDegrees = -3F;
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.Exposure = .2F;
                settings.RenderSettings.WhiteBalance = 5600F;
                settings.RenderSettings.Vignette = .22F;
                settings.RenderSettings.Dither = true;
                settings.RenderSettings.PathSamplesPerFrame = 2;
                settings.RenderSettings.PathRoughness = .2F;
                settings.RenderSettings.PathMetalness = .15F;
                settings.RenderSettings.PathSpecularF0 = .06F;
                settings.RenderSettings.RussianRouletteThreshold = .003F;
                settings.RenderSettings.RadianceClamp = 28F;
                settings.RenderSettings.SunDirectLighting = true;
                settings.RenderSettings.CameraFovDegrees = 48F;
                break;
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// 保存模板的场景索引文件
    /// </summary>
    /// <param name="project">项目句柄</param>
    /// <param name="template">模板定义</param>
    /// <param name="voxelCount">体素数量</param>
    private static void WriteScene(ProjectHandle project, ProjectTemplateDefinition template, int voxelCount,
        string modelAssetPath = "Assets/Worlds/主世界.mvmodel", int cameraCount = 1,
        IReadOnlyList<(string Name, string ModelAssetPath, int VoxelCount, VoxelCoord64 Position)>? layerParts = null,
        IReadOnlyList<SceneModelPart>? modelParts = null,
        VoxelWorld? framingWorld = null,
        IReadOnlyList<(string Name, bool Hidden)>? layers = null)
    {
        var scene = new SceneDocument { Name = template.Name };
        var graph = new SceneGraph(scene);
        if (modelParts is not null)
        {
            ApplyImportLayers(scene, layers);
            foreach (var modelPart in modelParts)
            {
                var modelObject = graph.Add(modelPart.Name,
                    transform: TransformComponent.FromGrid(new GridTransform(
                        modelPart.Position, modelPart.Rotation ?? OrthogonalRotation.Identity)));
                modelObject.LayerIndex = Math.Clamp(modelPart.LayerIndex, 0, Math.Max(0, scene.LayerNames.Count - 1));
                modelObject.Visible = !modelPart.Hidden;
                modelObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                    modelPart.ModelAssetPath, modelPart.VoxelCount, template.Id.ToString(), modelPart.Part));
            }
            if (template.Id == ProjectTemplateId.DefaultRoom)
            {
                // 父子关系仅整理对象与可见性，模型体素仍各自保存在独立资产中。
                var byName = scene.Objects.ToDictionary(item => item.Name, StringComparer.Ordinal);
                foreach (var (childName, parentName) in new[]
                {
                    ("纸杯", "桌子"), ("可乐", "纸杯"),
                    ("碟子", "桌子"), ("饼干", "碟子"),
                    ("壁画", "地板与墙壁"), ("墙壁插座", "地板与墙壁"),
                    ("插排与电线", "墙壁插座"),
                })
                {
                    if (byName.TryGetValue(childName, out var child) && byName.TryGetValue(parentName, out var parent))
                        graph.SetParent(child.Id, parent.Id);
                }
            }
        }
        else
        {
            var parts = GetTemplateParts(template.Id);
            var modelOrigin = GetWorldOrigin(framingWorld);
            for (var index = 0; index < parts.Count; index++)
            {
                var (name, part, position) = parts[index];
                var placement = new VoxelCoord64(
                    position.X + modelOrigin.X,
                    position.Y + modelOrigin.Y,
                    position.Z + modelOrigin.Z);
                var modelObject = graph.Add(name,
                    transform: TransformComponent.FromGrid(new GridTransform(placement, OrthogonalRotation.Identity)));
                modelObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                    modelAssetPath, voxelCount, template.Id.ToString(), part));
            }
        }

        if (layerParts is not null)
        {
            foreach (var (name, assetPath, count, position) in layerParts)
            {
                var layerObject = graph.Add(name,
                    transform: TransformComponent.FromGrid(new GridTransform(position, OrthogonalRotation.Identity)));
                layerObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                    assetPath, count, template.Id.ToString(), "分层对象"));
            }
        }

        var (center, distance) = GetFraming(framingWorld);
        var lightObject = graph.Add("主光照", transform: TransformComponent.FromFree(new FreeTransform(
            new Double3(center.X, center.Y + distance * .8D, center.Z), Quaternion.Identity)));
        lightObject.Components.Add(SceneComponentTypes.CreateLight(LightComponentData.Default));
        var positions = new[]
        {
            new Double3(center.X + distance * .38D, center.Y + distance * .48D, center.Z + distance),
            new Double3(center.X - distance * 1.05D, center.Y + distance * .32D, center.Z + distance * .25D),
            new Double3(center.X + distance * .18D, center.Y + distance * .72D, center.Z - distance * 1.1D),
        };
        if (template.Id == ProjectTemplateId.CornellBox)
            positions[0] = new Double3(center.X, center.Y, center.Z + distance * 1.25D);
        var target = new Vector3((float)center.X, (float)center.Y, (float)center.Z);
        for (var index = 0; index < Math.Clamp(cameraCount, 1, positions.Length); index++)
        {
            var position = positions[index];
            var cameraPosition = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            var forward = target - cameraPosition;
            if (forward.LengthSquared() < 0.0001F)
                forward = -Vector3.UnitZ;
            else
                forward = Vector3.Normalize(forward);
            var cameraObject = graph.Add(index == 0 ? "主相机" : $"相机 {index + 1}",
                transform: TransformComponent.FromFree(new FreeTransform(
                    position, CreateCameraRotation(forward, Vector3.UnitY))));
            cameraObject.Components.Add(SceneComponentTypes.CreateCamera());
        }
        SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), scene);
    }

    private readonly record struct SceneModelPart(
        string Name, string Part, string ModelAssetPath, int VoxelCount, VoxelCoord64 Position,
        int LayerIndex = 0, bool Hidden = false, OrthogonalRotation? Rotation = null);

    /// <summary>
    /// 写入模板拆分后的独立模型，并返回场景层级所需的模型组件数据。
    /// 主世界完整文件仍保留在 Worlds 下作为可导出/备份资产，但场景不再让多个对象重复引用它。
    /// </summary>
    private static List<SceneModelPart> WriteTemplateModelParts(ProjectHandle project,
        ProjectTemplateId id, VoxelWorld world)
    {
        var result = new List<SceneModelPart>();
        var prefix = id == ProjectTemplateId.Showcase ? "小屋与花园" : Get(id).Name;
        long totalVoxels = 0;
        foreach (var (name, part, partWorld) in TemplateScenes.BuildParts(id, world))
        {
            if (partWorld.OccupiedVoxelCount == 0) continue;
            var fileName = $"{prefix}_{name}.mvmodel";
            var path = Path.Combine(project.AssetsPath, "Worlds", fileName);
            LargeWorldFormat.Write(path, partWorld);
            totalVoxels += partWorld.OccupiedVoxelCount;
            result.Add(new SceneModelPart(name, part, "Assets/Worlds/" + fileName,
                partWorld.OccupiedVoxelCount, GetWorldOrigin(partWorld)));
        }

        // 模板的层级必须是真正的分件，而不是写出一个完整模型再复制多个对象。
        // 这里在生成阶段硬失败，避免以后新增模板时悄悄退化成单体引用。
        if (result.Count < 2)
            throw new InvalidOperationException($"模板 {id} 未生成至少两个独立模型层");
        if (totalVoxels != world.OccupiedVoxelCount)
            throw new InvalidOperationException(
                $"模板 {id} 的分件体素数量不守恒：分件 {totalVoxels:N0}，原世界 {world.OccupiedVoxelCount:N0}");
        if (result.Select(item => item.ModelAssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidOperationException($"模板 {id} 生成了重复的模型资产路径");

        return result;
    }

    private static (Double3 Center, double Distance) GetFraming(VoxelWorld? world)
    {
        if (world is null || !world.TryGetBounds(out var minimum, out var maximum))
            return (new Double3(0D, 4D, 0D), 24D);

        var center = new Double3(
            (minimum.X + maximum.X) * .5D,
            (minimum.Y + maximum.Y) * .5D,
            (minimum.Z + maximum.Z) * .5D);
        var span = Math.Max(maximum.X - minimum.X, Math.Max(maximum.Y - minimum.Y, maximum.Z - minimum.Z));
        return (center, Math.Max(24D, span * 1.35D));
    }

    /// <summary>
    /// 获取模型文件局部坐标对应的世界原点。
    /// 大世界格式把负坐标平移到从零开始保存，场景对象必须用此偏移恢复原始世界位置。
    /// </summary>
    private static VoxelCoord64 GetWorldOrigin(VoxelWorld? world)
    {
        return world is not null && world.TryGetBounds(out var minimum, out _)
            ? minimum
            : VoxelCoord64.Zero;
    }

    /// <summary>
    /// 把 MagicaVoxel 的图层映射成场景图层：第 0 层固定为默认层，
    /// 被隐藏的图层一并记入 HiddenLayerNames，保留原文件的显隐意图。
    /// </summary>
    private static void ApplyImportLayers(SceneDocument scene, IReadOnlyList<(string Name, bool Hidden)>? layers)
    {
        if (layers is not { Count: > 0 }) return;
        scene.LayerNames = ["默认", .. layers.Skip(1).Select(layer => layer.Name)];
        scene.HiddenLayerNames = [.. scene.LayerNames.Where((_, index) => index < layers.Count && layers[index].Hidden)];
    }

    private static IReadOnlyList<(string Name, string Part, VoxelCoord64 Position)> GetTemplateParts(ProjectTemplateId id)
    {
        return id switch
        {
            ProjectTemplateId.Showcase =>
            [
                ("小屋", "房屋", new VoxelCoord64(0, 0, 0)),
                ("左侧树木", "左侧树木", new VoxelCoord64(-6, 0, 5)),
                ("右侧树木", "右侧树木", new VoxelCoord64(8, 0, -5)),
                ("石板小路", "小路", new VoxelCoord64(0, 0, 12)),
                ("门口提灯", "提灯", new VoxelCoord64(3, 0, 8)),
            ],
            ProjectTemplateId.MaterialLab => [("材质实验区", "材质实验区", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.PaletteWorkshop => [("调色板展示台", "调色板展示台", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.SparseWorld => [("远距离地标群", "地标群", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.LodPlayground => [("LOD 建筑组", "建筑组", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.ProceduralTerrain => [("山地湖泊", "地形", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.MinecraftValley => [("地形", "地形", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.Architecture => [("柱廊庭院", "建筑", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.CameraGallery => [("多相机构图场景", "场景", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.LargeWorld => [("超大型建筑群", "建筑群", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.RenderingGallery => [("渲染体验馆", "全效果展示", new VoxelCoord64(0, 0, 0))],
            _ => [],
        };
    }

    /// <summary>
    /// 为模板相机创建朝向指定视线的自由旋转
    /// </summary>
    /// <param name="forward">相机视线方向</param>
    /// <param name="up">相机上方向</param>
    /// <returns>相机自由旋转</returns>
    internal static Quaternion CreateCameraRotation(Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, up));
    }
}
