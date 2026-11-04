using System.Numerics;
using System.Text.Json;
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
    WorldOcean,
    WorldMist,
    WorldSunset,
    WeatherRain,
    WeatherSnow,
    WeatherStorm,
    WeatherHeatHaze,

    /// <summary>
    /// 展示基础建模、树木、屋顶、玻璃和自发光
    /// </summary>
    Showcase,

    /// <summary>
    /// 展示不透明、半透明、自发光和材质槽
    /// </summary>
    MaterialLab,

    /// <summary>
    /// 展示调色盘基础色和纯色体素材质
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

    /// <summary>
    /// 展示不开路径追踪时的探针体积 GI：两层回廊中庭、深壁龛与天窗
    /// </summary>
    ProbeGiAtrium,

    /// <summary>
    /// 展示不开路径追踪时的实时体素间接光：只有洞口进光的洞穴
    /// </summary>
    ProbeGiCave,
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
        new(ProjectTemplateId.WorldOcean, "晴海灯塔", "晴空海面环绕灯塔岛，展示细密水波与海天。", "世界环境 · 海面 · 灯塔", "小型", "调整水位、波长与水面粗糙度"),
        new(ProjectTemplateId.WorldMist, "雾海群岛", "冷色雾气笼罩群岛与航标，展示距离雾层次。", "世界环境 · 海雾 · 群岛", "小型", "调整雾浓度、起始距离和颜色"),
        new(ProjectTemplateId.WorldSunset, "晚霞海岸", "暖色天空下的海岛与灯塔，展示低角度日光和海面。", "世界环境 · 晚霞 · 水面", "小型", "组合天空、太阳与水面参数"),
        new(ProjectTemplateId.WeatherRain, "季风暴雨", "强风推动的暴雨海岸，展示降雨、风向与雾气联动。", "天气 · 降雨 · 风", "小型", "调整风强度与降雨密度"),
        new(ProjectTemplateId.WeatherSnow, "雪原微光", "低温雪原与冷色天空，展示降雪和微风效果。", "天气 · 降雪 · 冷光", "小型", "调整降雪强度和风向"),
        new(ProjectTemplateId.WeatherStorm, "雷暴海湾", "雷云压境的海湾灯塔，展示风暴、闪电和海面动画。", "天气 · 雷暴 · 闪电", "小型", "调整雷暴强度与闪电频率"),
        new(ProjectTemplateId.WeatherHeatHaze, "灼热荒岸", "高温荒岸与远方热浪，展示热浪扰动和稀薄雾气。", "天气 · 高温 · 热浪", "小型", "调整热浪折射散射强度"),
        new(ProjectTemplateId.ProbeGiAtrium, "探针 GI 中庭", "两层回廊围出天井，顶层只留一个方形天窗，室内不点灯：打开后自动探针会自动烘焙一次，先靠实时体素间接光、再靠探针 GI 把天光带到回廊底部。", "非光追 · 体素间接光 · 探针体积 GI", "中型", "对比实时体素间接光与探针 GI 在室内的差别"),
        new(ProjectTemplateId.ProbeGiCave, "间接光洞穴", "只有洞口进光与一个洞顶竖井，主洞室、连接隧道与内室层层加深：不点灯也不用路径追踪，打开后自动探针自动烘焙一次，靠间接光显示明暗衰减。", "非光追 · 体素间接光 · 遮蔽衰减", "中型", "观察体素间接光在深洞与洞口附近的衰减差异"),
        new(ProjectTemplateId.XianxiaContinent, "仙域大陆", "1024×1024 的整块修仙大陆：天柱峰、五方地脉、青云剑宗、长乐坊市、三座浮空仙岛与锁妖塔、剑冢、传送大阵等古迹共同组成可漫游的世界。", "超大型世界 · 地理 · 建筑群 · 浮空岛 · 多层叙事", "超大型", "理解分区分件、世界坐标与镜头组织"),
        new(ProjectTemplateId.Showcase, "小屋与花园", "一座有生活痕迹的小屋、井台、花圃、林木与夜间提灯。打开即可理解场景组织。", "基础建模 · 材质 · 自发光", "小型", "学习对象、体素数据和相机的关系"),
        new(ProjectTemplateId.MaterialLab, "材质实验室", "把粗糙度、金属、玻璃、水和自发光样品放在同一间可比较的展厅。", "材质 · 透明 · 发光", "小型", "比较材质参数如何改变画面"),
        new(ProjectTemplateId.PaletteWorkshop, "调色盘工坊", "用色卡墙、螺旋看台和中央发光展台演示颜色层级，而不是堆叠孤立色块。", "调色盘 · 纯色材质 · 材质属性", "小型", "建立可复用的颜色系统"),
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
        ProjectTemplateId.ProbeGiAtrium or ProjectTemplateId.ProbeGiCave => "非光追 GI",
        ProjectTemplateId.MaterialLab or ProjectTemplateId.PaletteWorkshop or ProjectTemplateId.GlassStudy or
            ProjectTemplateId.CornellBox or ProjectTemplateId.SphereOptics or
            ProjectTemplateId.CloudAndFog or ProjectTemplateId.WaterStudy or ProjectTemplateId.WorldOcean or
            ProjectTemplateId.WorldMist or ProjectTemplateId.WorldSunset or ProjectTemplateId.WeatherRain or ProjectTemplateId.WeatherSnow or ProjectTemplateId.WeatherStorm or ProjectTemplateId.WeatherHeatHaze => "材质与渲染",
        ProjectTemplateId.Showcase or ProjectTemplateId.Architecture or ProjectTemplateId.MoonbaseOutpost or
            ProjectTemplateId.ClockworkConservatory or ProjectTemplateId.DesertCaravanserai => "建模与场景",
        ProjectTemplateId.ProceduralTerrain or ProjectTemplateId.MinecraftValley or ProjectTemplateId.SparseWorld or
            ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery or ProjectTemplateId.XianxiaContinent => "地形与大世界",
        _ => "相机与性能",
    };

    private static int CategoryRank(string category) => category switch
    {
        "入门" => 0, "非光追 GI" => 1, "建模与场景" => 2, "材质与渲染" => 3,
        "地形与大世界" => 4, "相机与性能" => 5, _ => 6,
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
    /// 模板写盘时同时登记的内存世界，供新建项目后的预读直接复用
    /// </summary>
    /// <param name="World">写入时的世界，坐标就是写盘前的原始世界坐标</param>
    /// <param name="Minimum">写入时的包围盒最小角，读盘正是按它把坐标平移回原始世界</param>
    public readonly record struct TemplateWorldHandoff(VoxelWorld World, VoxelCoord64 Minimum);

    /// <summary>
    /// 写盘并登记内存世界，登记之后新建项目的预读不必再解析同一份文件
    /// </summary>
    /// <param name="path">目标文件</param>
    /// <param name="world">要写入的世界</param>
    /// <param name="handoff">内存世界登记表，为空时只写盘</param>
    private static void WriteWorldFile(string path, VoxelWorld world,
        IDictionary<string, TemplateWorldHandoff>? handoff = null)
    {
        LargeWorldFormat.Write(path, world);
        if (handoff is null || !world.TryGetBounds(out var minimum, out _)) return;
        handoff[Path.GetFullPath(path)] = new TemplateWorldHandoff(world, minimum);
    }

    /// <summary>
    /// 将模板资源写入新项目
    /// </summary>
    /// <param name="project">新项目</param>
    /// <param name="id">模板 ID</param>
    /// <param name="report">进度回调</param>
    /// <param name="handoff">内存世界登记表，预读可直接接管而不必读盘</param>
    public static void Populate(ProjectHandle project, ProjectTemplateId id, Action<string, float, bool>? report = null,
        IDictionary<string, TemplateWorldHandoff>? handoff = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        report?.Invoke("正在生成示例体素模型…", .74F, true);
        var settings = CreateSettings(id);
        settings.TemplateId = id.ToString();
        if (id == ProjectTemplateId.Empty)
            SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), new SceneDocument { Name = "主场景" });
        else
        {
            var materialSlots = WriteMaterials(project, id);
            WriteProjectPalette(project);
            WriteRenderPreset(project, settings);
            if (id == ProjectTemplateId.XianxiaContinent)
            {
                Xianxia.XianxiaWorld.Populate(project, materialSlots, report);
            }
            else if (id == ProjectTemplateId.MinecraftValley)
            {
                var terrain = TemplateScenes.BuildValleyTerrain();
                var trees = TemplateScenes.BuildValleyTrees(terrain);
                var modelParts = WriteTemplateModelParts(project, id, terrain, handoff);
                for (var index = 0; index < trees.Count; index++)
                {
                    var treeFile = $"Minecraft山谷_树{index + 1}.mvmodel";
                    WriteWorldFile(Path.Combine(project.AssetsPath, "Worlds", treeFile), trees[index], handoff);
                    modelParts.Add(new SceneModelPart($"树 {index + 1}", "树木",
                        "Assets/Worlds/" + treeFile, trees[index].OccupiedVoxelCount,
                        GetWorldOrigin(trees[index])));
                }

                WriteScene(project, Get(id), terrain.OccupiedVoxelCount, "Assets/Worlds/山谷分层模型.mvmodel",
                    modelParts: modelParts, framingWorld: terrain, materialSlots: materialSlots);
            }
            else if (id is ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery)
            {
                var largeFileName = id == ProjectTemplateId.LargeWorld ? "超大型场景.mvworld" : "渲染体验馆.mvworld";
                var largePath = Path.Combine(project.AssetsPath, "Worlds", largeFileName);
                var world = BuildWorld(id, VoxelPalette.Build());
                WriteWorldFile(largePath, world, handoff);
                var modelParts = WriteTemplateModelParts(project, id, world, handoff);
                WriteScene(project, Get(id), world.OccupiedVoxelCount, $"Assets/Worlds/{largeFileName}",
                    modelParts: modelParts, framingWorld: world, materialSlots: materialSlots);
            }
            else
            {
                var world = BuildWorld(id, VoxelPalette.Build());
                var modelParts = WriteTemplateModelParts(project, id, world, handoff);
                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    "Assets/Worlds/分层模型.mvmodel",
                    cameraCount: id == ProjectTemplateId.CameraGallery ? 3 : 1,
                    modelParts: modelParts,
                    framingWorld: world, materialSlots: materialSlots);
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
    /// <param name="handoff">内存世界登记表，预读可直接接管而不必读盘</param>
    /// <returns>导入的体素数</returns>
    public static int PopulateFromVox(ProjectHandle project, string voxFilePath, Action<string, float, bool>? report = null,
        IDictionary<string, TemplateWorldHandoff>? handoff = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(voxFilePath);
        report?.Invoke("正在读取 MagicaVoxel 文件…", .72F, true);
        var document = VoxFormat.ReadDocument(voxFilePath);
        var modelName = Path.GetFileNameWithoutExtension(voxFilePath).Trim();
        if (modelName.Length == 0) modelName = "导入模型";
        var safeName = ToSafeFileName(modelName);
        var multiple = document.Instances.Count > 1 || document.Models.Count > 1;
        var materialSlots = WriteVoxMaterials(project, document, safeName, modelName);

        report?.Invoke("正在写入项目体素资产…", .82F, true);
        // 一个模型写一份资产，多个实例共享同一份，和 MagicaVoxel 的 Reference 语义一致
        // 当前对象体素加载路径只平移资产，不会在加载时应用场景旋转。
        // 将每种模型/旋转组合烘焙为独立资产，实例仍共享相同组合。
        var baked = new Dictionary<(int Model, VoxTransform Rotation), (string Asset, int Count, VoxelCoord64 Origin)>();
        foreach (var instance in document.Instances)
        {
            var key = (instance.ModelIndex, instance.Rotation);
            if (baked.ContainsKey(key)) continue;
            var world = BuildModelWorld(document.Models[instance.ModelIndex], materialSlots, instance.Rotation);
            var fileName = (multiple ? $"{safeName}_{baked.Count + 1}" : safeName) + ".mvmodel";
            var absolutePath = Path.Combine(project.AssetsPath, "Worlds", fileName);
            if (world.OccupiedVoxelCount == 0) LargeWorldFormat.WriteEmpty(absolutePath);
            else WriteWorldFile(absolutePath, world, handoff);
            AssetMetaStore.EnsureMeta(absolutePath);
            baked[key] = ("Assets/Worlds/" + fileName, world.OccupiedVoxelCount, GetWorldOrigin(world));
        }

        report?.Invoke("正在生成主场景…", .90F, true);
        var layerIndexById = new Dictionary<int, int>();
        for (var index = 0; index < document.Layers.Count; index++) layerIndexById[document.Layers[index].Id] = index;
        var parts = new List<SceneModelPart>(document.Instances.Count);
        long totalVoxels = 0;
        foreach (var instance in document.Instances)
        {
            var name = document.Instances.Count == 1 ? modelName : instance.Name;
            var asset = baked[(instance.ModelIndex, instance.Rotation)];
            parts.Add(new SceneModelPart(name, "MagicaVoxel 导入", asset.Asset,
                asset.Count, instance.Offset + asset.Origin,
                layerIndexById.GetValueOrDefault(instance.LayerId), instance.Hidden, OrthogonalRotation.Identity,
                document.Models[instance.ModelIndex].Voxels.Select(voxel => (int)voxel.PaletteIndex)
                    .Distinct().Where(materialSlots.ContainsKey).Order()
                    .Select(slot => new MaterialSlotBinding(slot, materialSlots[slot])).ToArray()));
            totalVoxels += asset.Count;
        }

        var layers = document.Layers.Count == 0
            ? null
            : document.Layers.Select(layer => (layer.Name, layer.Hidden)).ToList();
        var definition = new ProjectTemplateDefinition(ProjectTemplateId.Empty, modelName,
            "由 MagicaVoxel 文件创建", "MagicaVoxel 兼容导入",
            multiple ? $"{parts.Count} 个对象" : "单模型", "继续在原生格式上编辑");
        WriteScene(project, definition, (int)Math.Min(totalVoxels, int.MaxValue), baked.Values.First().Asset, cameraCount: 1,
            modelParts: parts, framingWorld: VoxFormat.Compose(document), layers: layers,
            materialSlots: materialSlots);

        report?.Invoke("正在写入项目设置…", .94F, false);
        var settings = CreateSettings(ProjectTemplateId.Empty);
        settings.TemplateId = "MagicaVoxel 导入";
        SettingsStore.SaveProject(project, settings);
        return (int)Math.Min(totalVoxels, int.MaxValue);
    }

    /// <summary>
    /// 将VOX材质写成共享资产，返回导入模型本地槽到资产相对路径的绑定
    /// </summary>
    private static Dictionary<int, string> WriteVoxMaterials(ProjectHandle project, VoxDocument document,
        string safeName, string modelName)
    {
        var bindings = new Dictionary<int, string>();
        if (document.Materials.Count == 0) return bindings;
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        // MagicaVoxel 导出的 .vox 常把整张调色板逐格写成 MATL，其中大量条目参数完全相同；
        // 去重键必须用“生成的材质资产内容”而不是原始 MATL 字段：原始字段里
        // weight 之类不进资产，按原始字段比较会留下内容一模一样的重复文件
        var sharedByDefinition = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in document.Materials)
        {
            var slot = source.Id;
            if (slot is <= 0 or > ushort.MaxValue)
                throw new InvalidDataException($"导入材质槽超出1到65535范围：{slot}");

            var asset = MaterialAsset.CreateDefault($"{modelName} 材质 {slot}");
            if (source.Roughness >= 0F) asset.Roughness = Math.Clamp(source.Roughness, 0.01F, 1F);
            if (source.Specular >= 0F) asset.Specular = Math.Clamp(source.Specular, 0F, 1F);
            else if (source.SpecularLevel >= 0F) asset.Specular = Math.Clamp(source.SpecularLevel / 2F, 0F, 1F);

            // 0.99.7 起 _ior 是相对 1 的偏移量（常见 0.3），真实折射率写在 _ri（常见 1.3）。
            // 旧文件只有 _ior 时它就是折射率本身，所以仅在缺少 _ri 时按 1 + _ior 解释。
            var refraction = source.RefractionIndex > 0F ? source.RefractionIndex
                : source.Ior > 0F ? 1F + source.Ior : -1F;
            if (refraction > 0F) asset.Ior = Math.Clamp(refraction, 1F, 2.5F);
            // _d 是介质密度，与吸收步长同量纲；旧文件的 _att 优先
            if (source.Absorption >= 0F) asset.AbsorptionStep = Math.Clamp(source.Absorption, 0F, 8F);
            else if (source.Density >= 0F) asset.AbsorptionStep = Math.Clamp(source.Density, 0F, 8F);

            // 半透明与金属优先看参数化字段，_type 只作为旧文件的兜底
            if (source.Metal >= 0F) asset.Metallic = Math.Clamp(source.Metal, 0F, 1F);
            if (source.Transmission >= 0F)
                asset.Opacity = Math.Clamp(1F - source.Transmission, 0F, 1F);
            // 0.99.7 的发光由「归一化强度 _emit（0~1）」乘「通量档位 _flux（1/2/4）」得到最终光输出。
            // 只取 _emit 会让低通量档的灯暗一个量级（room 示例 4 掉到 0.12），只取 _flux 又会把弱灯当成最亮。
            // 两者相乘后：强灯 2~4、弱灯 0.46~0.48，同档位横向可比。
            var emissionStrength = source.Emissive >= 0F
                ? source.Emissive
                : string.Equals(source.Type, "_emit", StringComparison.OrdinalIgnoreCase) ? 1F : -1F;
            if (emissionStrength >= 0F)
            {
                var flux = source.Flux > 0F ? source.Flux : 1F;
                asset.EmissiveScale = Math.Clamp(emissionStrength * flux, 0F, 32F);
            }
            if (string.Equals(source.MediaType, "_scatter", StringComparison.OrdinalIgnoreCase))
                asset.VolumeScattering = 1F;

            switch (source.Type.ToLowerInvariant())
            {
                case "_metal":
                    asset.Metallic ??= 1F;
                    break;
                case "_glass":
                    asset.Metallic ??= 0F;
                    asset.Ior ??= 1.5F;
                    break;
                case "_emit":
                    // 只有连 _emit 键都没写的老文件才落到这里，按最亮处理
                    asset.EmissiveScale ??= 1F;
                    break;
                case "_diffuse":
                    asset.Metallic ??= 0F;
                    break;
            }

            var savedName = asset.Name;
            asset.Name = string.Empty;
            var signature = JsonSerializer.Serialize(asset, MaterialSignatureJsonOptions);
            asset.Name = savedName;
            if (sharedByDefinition.TryGetValue(signature, out var sharedPath))
            {
                bindings[slot] = sharedPath;
                continue;
            }

            var baseFileName = $"{safeName}_材质{slot:000}";
            var fileName = UniqueMaterialFileName(directory, baseFileName);
            var targetPath = Path.Combine(directory, fileName);
            asset.Save(targetPath);
            var relative = "Materials/" + fileName;
            sharedByDefinition[signature] = relative;
            bindings[slot] = relative;
        }
        return bindings;
    }

    /// <summary>材质去重用的序列化选项，字段与保存格式保持一致，但不缩进以便快速比较</summary>
    private static readonly JsonSerializerOptions MaterialSignatureJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 生成不覆盖已有文件的材质资产名
    /// </summary>
    private static string UniqueMaterialFileName(string directory, string baseFileName)
    {
        var candidate = baseFileName + ".material";
        for (var suffix = 2; File.Exists(Path.Combine(directory, candidate)); suffix++)
            candidate = $"{baseFileName}_{suffix}.material";
        return candidate;
    }


    private static VoxelWorld BuildModelWorld(VoxModel model, IReadOnlyDictionary<int, string> materialSlots,
        VoxTransform rotation)
    {
        var world = new VoxelWorld();
        foreach (var voxel in model.Voxels)
            world.Set(rotation.Apply(new VoxelCoord64(-voxel.X - 1L, voxel.Z, voxel.Y)), new VoxelValue(voxel.Color,
                materialSlots.ContainsKey(voxel.PaletteIndex) ? checked((ushort)voxel.PaletteIndex) : (ushort)0,
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
    /// <param name="palette">当前调色盘</param>
    /// <returns>模板世界</returns>
    public static VoxelWorld BuildWorld(ProjectTemplateId id, Rgba32[] palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return id switch
        {
            ProjectTemplateId.WorldOcean => TemplateScenes.BuildEnvironmentIsland(0),
            ProjectTemplateId.WorldMist => TemplateScenes.BuildEnvironmentIsland(1),
            ProjectTemplateId.WorldSunset => TemplateScenes.BuildEnvironmentIsland(2),
            ProjectTemplateId.WeatherRain => TemplateScenes.BuildEnvironmentIsland(1),
            ProjectTemplateId.WeatherSnow => TemplateScenes.BuildEnvironmentIsland(1),
            ProjectTemplateId.WeatherStorm => TemplateScenes.BuildEnvironmentIsland(2),
            ProjectTemplateId.WeatherHeatHaze => TemplateScenes.BuildEnvironmentIsland(0),
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
            ProjectTemplateId.ProbeGiAtrium => TemplateScenes.BuildProbeAtrium(),
            ProjectTemplateId.ProbeGiCave => TemplateScenes.BuildProbeCave(),
            ProjectTemplateId.XianxiaContinent => Xianxia.XianxiaTerrain.Build().Worlds[Xianxia.XianxiaRegion.Summit],
            _ => new VoxelWorld(),
        };
    }

    /// <summary>
    /// 为模板写入配套的 .material 材质定义，槽位与场景构建器约定一致
    /// </summary>
    private static Dictionary<int, string> WriteMaterials(ProjectHandle project, ProjectTemplateId id)
    {
        if (id == ProjectTemplateId.Empty) return [];
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        static (int Slot, MaterialAsset Asset) At(int slot, MaterialAsset asset) => (slot, asset);
        var materials = new List<(int Slot, MaterialAsset Asset)>
        {
            At(TemplateKit.SlotGlass, new() { Name = "清水玻璃", Roughness = .06F, Metallic = 0F, AbsorptionStep = .8F }),
            At(TemplateKit.SlotWater, new() { Name = "池水", Roughness = .08F, Metallic = 0F, AbsorptionStep = 1.4F,
                AbsorptionColor = new Rgba32(48, 118, 168) }),
            At(TemplateKit.SlotMetal, new() { Name = "抛光金属", Roughness = .12F, Metallic = 1F }),
            At(TemplateKit.SlotGlow, new() { Name = "灯与炉", EmissiveScale = 6F }),
        };
        if (id == ProjectTemplateId.DefaultRoom)
        {
            materials[0].Asset.Ior = 1.5F;
            materials[0].Asset.Opacity = .08F;
            materials[0].Asset.AbsorptionStep = .25F;
            materials[0].Asset.VolumeScattering = 0F;
            materials.Add(At(TemplateKit.SlotCola, new MaterialAsset { Name = "可乐",
                Roughness = .08F, Metallic = 0F, AbsorptionStep = 3F, Ior = 1.33F,
                Opacity = .8F, VolumeScattering = .08F }));
        }
        if (id is ProjectTemplateId.GlassStudy or ProjectTemplateId.CloudAndFog or ProjectTemplateId.WaterStudy)
        {
            materials.Clear();
            var opticalMaterials = new[]
            {
                At(10, new MaterialAsset { Name = "透明玻璃", Ior = 1.5F, Roughness = .02F,
                    Opacity = .05F, AbsorptionStep = .2F, VolumeScattering = 0F }),
                At(11, new MaterialAsset { Name = "毛玻璃", Ior = 1.5F, Roughness = .8F,
                    Opacity = .22F, AbsorptionStep = .3F, VolumeScattering = .12F, HazeDensity = .06F }),
                At(12, new MaterialAsset { Name = "琥珀玻璃", Ior = 1.5F, Roughness = .04F,
                    Opacity = .85F, AbsorptionStep = 6F, VolumeScattering = 0F,
                    AbsorptionColor = new Rgba32(250, 155, 45) }),
                At(13, new MaterialAsset { Name = "薄雾", Ior = 1F, Roughness = .9F,
                    Opacity = .32F, AbsorptionStep = .05F, HazeDensity = .12F, VolumeScattering = .65F,
                    ScatteringColor = new Rgba32(218, 228, 236) }),
                At(14, new MaterialAsset { Name = "云朵", Ior = 1F, Roughness = 1F,
                    Opacity = .65F, AbsorptionStep = .05F, HazeDensity = .32F, VolumeScattering = .9F,
                    ScatteringColor = new Rgba32(244, 246, 250) }),
                At(16, new MaterialAsset { Name = "浓雾", Ior = 1F, Roughness = 1F,
                    Opacity = .75F, AbsorptionStep = .05F, HazeDensity = .45F, VolumeScattering = .85F,
                    ScatteringColor = new Rgba32(211, 222, 232) }),
                At(15, new MaterialAsset { Name = "清水", Ior = 1.33F, Roughness = .03F,
                    Opacity = .18F, AbsorptionStep = .4F, VolumeScattering = .03F,
                    AbsorptionColor = new Rgba32(155, 218, 229) }),
            };
            materials.AddRange(opticalMaterials.Where(material => id switch
            {
                ProjectTemplateId.GlassStudy => material.Slot is >= 10 and <= 12,
                ProjectTemplateId.CloudAndFog => material.Slot is 13 or 14 or 16,
                _ => material.Slot == 15,
            }));
        }
        if (id == ProjectTemplateId.SphereOptics)
            materials.Add(At(10, new MaterialAsset { Name = "实验玻璃", Ior = 1.5F, Roughness = .02F,
                Opacity = .08F, AbsorptionStep = .3F, VolumeScattering = 0F }));
        if (id == ProjectTemplateId.MaterialLab)
        {
            for (var step = 0; step < 5; step++)
            {
                materials.Add(At(5 + step, new MaterialAsset
                {
                    Name = $"粗糙度阶梯 {step + 1}",
                    Roughness = .05F + step * .22F,
                    Metallic = .2F,
                }));
            }
        }

        var bindings = new Dictionary<int, string>();
        foreach (var (slot, material) in materials)
        {
            var fileName = $"{material.Name}.material";
            material.Save(Path.Combine(directory, fileName));
            bindings[slot] = "Materials/" + fileName;
        }
        return bindings;
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
            // 两个非光追 GI 模板共用一套配置：不开路径追踪与硬件光追，
            // 照明完全交给天光、实时体素间接光与探针体积 GI
            case ProjectTemplateId.ProbeGiAtrium:
            case ProjectTemplateId.ProbeGiCave:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.PathTracingEnabled = false;
                // 模板要展示纯 Compute 的非光追链路，显式退出自动硬件加速
                settings.RenderSettings.HardwareRayTracingAuto = false;
                settings.RenderSettings.UseHardwareRayTracing = false;
                settings.RenderSettings.SunDirectLighting = true;
                settings.RenderSettings.SunEnabled = true;
                settings.RenderSettings.SunElevationDegrees = id == ProjectTemplateId.ProbeGiCave ? 46F : 62F;
                settings.RenderSettings.SunAzimuthDegrees = 210F;
                settings.RenderSettings.SunIntensity = 1.35F;
                // 环境光压低一些，实时体素间接光与探针 GI 的补光才有可比的差别
                settings.RenderSettings.SkyAmbient = id == ProjectTemplateId.ProbeGiCave ? .34F : .4F;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .9F;
                settings.RenderSettings.AoPower = 1.3F;
                // 实时通道的 AO 与体素间接光都是每帧少量射线估计，采样数给足才不毛糙
                settings.RenderSettings.AoSamples = 8;
                settings.RenderSettings.VoxelIndirectLightSamples = 4;
                settings.RenderSettings.ShadowSamples = 6;
                settings.RenderSettings.RealtimeAntiAliasingSamples = 4;
                // 探针按高画质烘焙：方向翻倍、探针更密、平滑两次
                settings.RenderSettings.ProbeBakeRays = 96;
                settings.RenderSettings.ProbeBakeMaxProbes = 98304;
                settings.RenderSettings.ProbeBakeSmoothing = 2;
                settings.RenderSettings.EmissiveVoxels = false;
                settings.RenderSettings.ShowWater = false;
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.FogDensity = 0F;
                settings.RenderSettings.SceneBackgroundColor = new Vector3(.03F, .035F, .042F);
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.Exposure = .15F;
                settings.RenderSettings.Brightness = 1.02F;
                settings.RenderSettings.ColorContrast = 1.06F;
                // 抖动是为断带准备的逐像素噪声，观察间接光时只会让画面更像噪点
                settings.RenderSettings.Dither = false;
                settings.RenderSettings.CameraFovDegrees = 62F;
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
            case ProjectTemplateId.WorldOcean:
            case ProjectTemplateId.WorldMist:
            case ProjectTemplateId.WorldSunset:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.ShowWater = true;
                settings.RenderSettings.WaterHeight = 2F;
                settings.RenderSettings.WaterWaveSize = id == ProjectTemplateId.WorldMist ? .2F : .6F;
                settings.RenderSettings.WaterWaveLength = id == ProjectTemplateId.WorldSunset ? 12F : 18F;
                settings.RenderSettings.WaterRoughness = .12F;
                settings.RenderSettings.WaterColor = id == ProjectTemplateId.WorldSunset
                    ? new Vector3(.18F, .28F, .36F) : new Vector3(.07F, .32F, .42F);
                settings.RenderSettings.SunElevationDegrees = id == ProjectTemplateId.WorldSunset ? 12F : 38F;
                if (id == ProjectTemplateId.WorldMist)
                {
                    settings.RenderSettings.FogDensity = .012F;
                    settings.RenderSettings.FogStart = 12F;
                    settings.RenderSettings.FogColor = new Vector3(.68F, .76F, .78F);
                    settings.RenderSettings.FogMaxOpacity = .8F;
                    // 雾海群岛作为高度雾与体积散射的示范：低处浓、高处淡，
                    // 太阳方向有前向光晕，实时通道用 8 步抖动步进得到体积光柱。
                    settings.RenderSettings.FogHeight = 4F;
                    settings.RenderSettings.FogHeightFalloff = .035F;
                    settings.RenderSettings.FogAnisotropy = .7F;
                    settings.RenderSettings.FogSunScatterStrength = 1.2F;
                    settings.RenderSettings.FogSkyFogStrength = .65F;
                    settings.RenderSettings.FogVolumetricSteps = 8;
                    settings.RenderSettings.FogNoiseStrength = .35F;
                    settings.RenderSettings.FogNoiseScale = .05F;
                    settings.RenderSettings.FogNoiseSpeed = .4F;
                }
                if (id == ProjectTemplateId.WorldSunset)
                {
                    settings.RenderSettings.SkyTop = new Vector3(.22F, .27F, .48F);
                    settings.RenderSettings.SkyHorizon = new Vector3(.95F, .55F, .34F);
                    settings.RenderSettings.SunColor = new Vector3(1F, .68F, .43F);
                    settings.RenderSettings.AmbientTint = .3F;
                }
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
            case ProjectTemplateId.WeatherRain:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.ShowWater = true;
                settings.RenderSettings.WaterHeight = 2F;
                settings.RenderSettings.WaterAnimationEnabled = true;
                settings.RenderSettings.WaterAnimationSpeed = 1.35F;
                settings.RenderSettings.WaterFlowDirection = new Vector2(-.9F, .4F);
                settings.RenderSettings.WaterDetailStrength = .72F;
                settings.RenderSettings.SunElevationDegrees = 28F;
                settings.RenderSettings.SunAzimuthDegrees = 205F;
                settings.RenderSettings.FogDensity = .006F;
                settings.RenderSettings.FogStart = 10F;
                settings.RenderSettings.FogColor = new Vector3(.46F, .52F, .58F);
                settings.RenderSettings.FogMaxOpacity = .72F;
                settings.RenderSettings.WeatherWindStrength = 2.2F;
                settings.RenderSettings.WeatherWindDirection = 205F;
                settings.RenderSettings.WeatherRainIntensity = .9F;
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
            case ProjectTemplateId.WeatherSnow:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.ShowGround = true;
                settings.RenderSettings.ShowWater = false;
                settings.RenderSettings.SunElevationDegrees = 18F;
                settings.RenderSettings.SunAzimuthDegrees = 120F;
                settings.RenderSettings.SkyTop = new Vector3(.34F, .42F, .58F);
                settings.RenderSettings.SkyHorizon = new Vector3(.72F, .8F, .9F);
                settings.RenderSettings.SunColor = new Vector3(.72F, .82F, 1F);
                settings.RenderSettings.AmbientTint = .62F;
                settings.RenderSettings.FogDensity = .004F;
                settings.RenderSettings.FogColor = new Vector3(.72F, .8F, .9F);
                settings.RenderSettings.WeatherWindStrength = .7F;
                settings.RenderSettings.WeatherWindDirection = 120F;
                settings.RenderSettings.WeatherSnowIntensity = .85F;
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
            case ProjectTemplateId.WeatherStorm:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.ShowGround = false;
                settings.RenderSettings.ShowWater = true;
                settings.RenderSettings.WaterHeight = 2F;
                settings.RenderSettings.WaterAnimationEnabled = true;
                settings.RenderSettings.WaterAnimationSpeed = 1.8F;
                settings.RenderSettings.WaterFlowDirection = new Vector2(-.75F, .66F);
                settings.RenderSettings.WaterDetailStrength = .95F;
                settings.RenderSettings.WaterSpecularStrength = .88F;
                settings.RenderSettings.SunEnabled = true;
                settings.RenderSettings.SunElevationDegrees = 22F;
                settings.RenderSettings.SunAzimuthDegrees = 240F;
                settings.RenderSettings.SkyTop = new Vector3(.06F, .08F, .14F);
                settings.RenderSettings.SkyHorizon = new Vector3(.25F, .29F, .36F);
                settings.RenderSettings.SunColor = new Vector3(.5F, .58F, .72F);
                settings.RenderSettings.FogDensity = .014F;
                settings.RenderSettings.FogStart = 8F;
                settings.RenderSettings.FogColor = new Vector3(.2F, .24F, .3F);
                settings.RenderSettings.FogMaxOpacity = .88F;
                settings.RenderSettings.WeatherWindStrength = 3.2F;
                settings.RenderSettings.WeatherWindDirection = 240F;
                settings.RenderSettings.WeatherRainIntensity = 1F;
                settings.RenderSettings.WeatherStormIntensity = .95F;
                settings.RenderSettings.WeatherLightningFrequency = 1.8F;
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
            case ProjectTemplateId.WeatherHeatHaze:
                settings.DefaultRenderPreset = Get(id).Name;
                settings.RenderSettings.ShowGround = true;
                settings.RenderSettings.ShowWater = true;
                settings.RenderSettings.WaterHeight = 1.5F;
                settings.RenderSettings.WaterAnimationEnabled = true;
                settings.RenderSettings.WaterAnimationSpeed = .55F;
                settings.RenderSettings.WaterFlowDirection = new Vector2(.8F, .2F);
                settings.RenderSettings.SunElevationDegrees = 72F;
                settings.RenderSettings.SunAzimuthDegrees = 148F;
                settings.RenderSettings.SkyTop = new Vector3(.34F, .55F, .82F);
                settings.RenderSettings.SkyHorizon = new Vector3(.96F, .72F, .42F);
                settings.RenderSettings.SunColor = new Vector3(1F, .72F, .38F);
                settings.RenderSettings.AmbientTint = .38F;
                settings.RenderSettings.FogDensity = .002F;
                settings.RenderSettings.FogStart = 24F;
                settings.RenderSettings.FogColor = new Vector3(.86F, .64F, .42F);
                settings.RenderSettings.WeatherWindStrength = .45F;
                settings.RenderSettings.WeatherWindDirection = 148F;
                settings.RenderSettings.WeatherHeatHaze = .85F;
                settings.ExportRenderSettings = settings.RenderSettings.CloneForExport();
                break;
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
                settings.DefaultRenderPreset = "调色盘材质展示";
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
        IReadOnlyList<(string Name, bool Hidden)>? layers = null,
        IReadOnlyDictionary<int, string>? materialSlots = null)
    {
        var bindings = materialSlots?.OrderBy(item => item.Key)
            .Select(item => new MaterialSlotBinding(item.Key, item.Value)).ToArray() ?? [];
        var scene = new SceneDocument { Name = template.Name };
        scene.Environment.CaptureFrom(CreateSettings(template.Id).RenderSettings);
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
                modelObject.Components.Add(CreateModelComponent(
                    modelPart.ModelAssetPath, modelPart.VoxelCount, template.Id.ToString(), modelPart.Part,
                    modelPart.MaterialSlots ?? bindings));
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
                modelObject.Components.Add(CreateModelComponent(
                    modelAssetPath, voxelCount, template.Id.ToString(), part, bindings));
            }
        }

        if (layerParts is not null)
        {
            foreach (var (name, assetPath, count, position) in layerParts)
            {
                var layerObject = graph.Add(name,
                    transform: TransformComponent.FromGrid(new GridTransform(position, OrthogonalRotation.Identity)));
                layerObject.Components.Add(CreateModelComponent(
                    assetPath, count, template.Id.ToString(), "分层对象", bindings));
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
        // 两个非光追 GI 模板要站在室内看间接光，不能像其他模板那样从外部俯视
        if (template.Id == ProjectTemplateId.ProbeGiAtrium)
        {
            positions[0] = new Double3(20D, 15D, 20D);
            target = new Vector3(-8F, 8F, -12F);
        }
        else if (template.Id == ProjectTemplateId.ProbeGiCave)
        {
            // 站在主洞室后壁朝洞口看：竖井与洞口两处光源，地面亮度离它们越远越暗
            positions[0] = new Double3(center.X - 2D, 11D, 32D);
            target = new Vector3((float)center.X, 9F, (float)(center.Z + 24D));
        }
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
            cameraObject.Components.Add(template.Id is ProjectTemplateId.ProbeGiAtrium or ProjectTemplateId.ProbeGiCave
                ? SceneComponentTypes.CreateCamera(CameraComponentData.Default with
                {
                    FieldOfViewDegrees = 62F,
                    PostProcess = CreateProbeGiPostProcess(),
                })
                : SceneComponentTypes.CreateCamera());
        }
        if (template.Id is ProjectTemplateId.ProbeGiAtrium or ProjectTemplateId.ProbeGiCave)
        {
            // 探针体积盖住整个可游览范围并打开自动烘焙，进入项目后即可直接烘焙
            var probeCenter = center;
            var spanX = 32D;
            var spanY = 32D;
            var spanZ = 32D;
            if (framingWorld is not null && framingWorld.TryGetBounds(out var probeMin, out var probeMax))
            {
                spanX = Math.Max(8D, probeMax.X - probeMin.X + 1D);
                spanY = Math.Max(8D, probeMax.Y - probeMin.Y + 1D);
                spanZ = Math.Max(8D, probeMax.Z - probeMin.Z + 1D);
                probeCenter = new Double3(
                    (probeMin.X + probeMax.X + 1) * .5D,
                    (probeMin.Y + probeMax.Y + 1) * .5D,
                    (probeMin.Z + probeMax.Z + 1) * .5D);
            }

            var probeObject = graph.Add("自动探针",
                transform: TransformComponent.FromFree(new FreeTransform(probeCenter, Quaternion.Identity)));
            probeObject.Components.Add(SceneComponentTypes.CreateProbeVolume(new ProbeVolumeComponentData(
                0F, 0F, 0F, (float)spanX, (float)spanY, (float)spanZ, 1F, AutoBake: true, Editing: false, "")));
        }
        SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), scene);
    }

    /// <summary>
    /// 非光追 GI 模板的相机后处理栈：把体素间接光与探针 GI 调到一眼可见的强度
    /// </summary>
    /// <remarks>
    /// 覆盖项目默认值，只保留与本主题相关的效果；景深等会干扰观察的项一律关闭
    /// </remarks>
    /// <returns>带 GI 强化的后处理栈</returns>
    private static CameraPostProcessStack CreateProbeGiPostProcess()
    {
        var stack = new CameraPostProcessStack { OverrideProjectDefaults = true };
        stack.Effects.Clear();
        stack.Effects.Add(new CameraPostProcessEffect
        {
            Type = CameraPostProcessEffectType.AmbientOcclusion, Intensity = .85F, Radius = 2F, Samples = 8,
        });
        stack.Effects.Add(new CameraPostProcessEffect
        {
            Type = CameraPostProcessEffectType.VoxelIndirectLight, Intensity = 1.15F, Radius = 9F, Samples = 4,
        });
        stack.Effects.Add(new CameraPostProcessEffect
        {
            Type = CameraPostProcessEffectType.ProbeVolumeGi, Intensity = 1.25F, Radius = 12F, Samples = 1,
        });
        stack.Effects.Add(new CameraPostProcessEffect
        {
            Type = CameraPostProcessEffectType.Vignette, Enabled = true, Intensity = .1F, Radius = .8F,
        });
        stack.Effects.Add(new CameraPostProcessEffect
        {
            Type = CameraPostProcessEffectType.DepthOfField, Enabled = false,
        });
        return stack;
    }

    private readonly record struct SceneModelPart(
        string Name, string Part, string ModelAssetPath, int VoxelCount, VoxelCoord64 Position,
        int LayerIndex = 0, bool Hidden = false, OrthogonalRotation? Rotation = null,
        MaterialSlotBinding[]? MaterialSlots = null);

    /// <summary>
    /// 创建模型组件并保存本地槽绑定，材质参数仅由共享资产提供
    /// </summary>
    internal static ComponentRecord CreateModelComponent(string model, int voxelCount, string template,
        string part, MaterialSlotBinding[] materialSlots)
    {
        var component = SceneComponentTypes.CreateVoxelModel(model, voxelCount, template, part);
        SceneComponentTypes.WriteVoxelModel(component,
            SceneComponentTypes.ReadVoxelModel(component) with { MaterialSlots = materialSlots });
        return component;
    }

    /// <summary>
    /// 写入模板拆分后的独立模型，并返回场景层级所需的模型组件数据。
    /// 主世界完整文件仍保留在 Worlds 下作为可导出/备份资产，但场景不再让多个对象重复引用它。
    /// </summary>
    /// <param name="id">模板 ID</param>
    /// <param name="world">要拆分的完整世界</param>
    /// <param name="handoff">内存世界登记表，预读可直接接管而不必读盘</param>
    private static List<SceneModelPart> WriteTemplateModelParts(ProjectHandle project,
        ProjectTemplateId id, VoxelWorld world, IDictionary<string, TemplateWorldHandoff>? handoff = null)
    {
        var result = new List<SceneModelPart>();
        var prefix = id == ProjectTemplateId.Showcase ? "小屋与花园" : Get(id).Name;
        long totalVoxels = 0;
        foreach (var (name, part, partWorld) in TemplateScenes.BuildParts(id, world))
        {
            if (partWorld.OccupiedVoxelCount == 0) continue;
            var fileName = $"{prefix}_{name}.mvmodel";
            var path = Path.Combine(project.AssetsPath, "Worlds", fileName);
            WriteWorldFile(path, partWorld, handoff);
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
            ProjectTemplateId.PaletteWorkshop => [("调色盘展示台", "调色盘展示台", new VoxelCoord64(0, 0, 0))],
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
