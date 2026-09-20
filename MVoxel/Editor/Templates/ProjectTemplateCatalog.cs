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
    /// 不写入示例资源的空项目
    /// </summary>
    Empty,

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
    /// 展示发光体、玻璃、反射色块和路径追踪参数
    /// </summary>
    RayTracingStudio,

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
    /// 所有可选模板，顺序就是界面展示顺序
    /// </summary>
    public static IReadOnlyList<ProjectTemplateDefinition> All { get; } =
    [
        new(ProjectTemplateId.Empty, "无 · 空项目", "只创建标准目录和配置，不创建任何体素数据", "适合从零开始", "起步", "建立自己的项目结构"),
        new(ProjectTemplateId.Showcase, "基础展示 · 小屋与花园", "一座有生活痕迹的小屋、井台、花圃、林木与夜间提灯。打开即可理解场景组织。", "基础建模 · 材质 · 自发光", "小型", "学习对象、体素数据和相机的关系"),
        new(ProjectTemplateId.MaterialLab, "材质实验室", "把粗糙度、金属、玻璃、水和自发光样品放在同一间可比较的展厅。", "材质 · 透明 · 发光", "小型", "比较材质参数如何改变画面"),
        new(ProjectTemplateId.PaletteWorkshop, "调色板工坊", "用色卡墙、螺旋看台和中央发光展台演示颜色层级，而不是堆叠孤立色块。", "调色板 · 纯色材质 · 材质属性", "小型", "建立可复用的颜色系统"),
        new(ProjectTemplateId.SparseWorld, "稀疏世界 · 远距离地标", "一座灯塔岛连接悬索桥与远处信号塔，地标分散在负坐标和远距离位置。", "大世界 · 稀疏存储 · 负坐标", "大型", "理解稀疏世界和远距离编辑"),
        new(ProjectTemplateId.LodPlayground, "LOD 练习场", "同一座建筑从完整细节到块状轮廓排列成渐变展廊，距离变化一眼可见。", "LOD · 场景性能 · 批量对象", "中型", "验证距离 LOD 的视觉与性能取舍"),
        new(ProjectTemplateId.RayTracingStudio, "光追工作室", "暗室、侧窗、玻璃砖墙、金属球和暖色灯槽组成一个明确的光线实验场。", "路径追踪 · 直接光 · 透明", "中型", "观察光照、反射和透明材质"),
        new(ProjectTemplateId.ProceduralTerrain, "程序化地形", "从沙滩、湖盆、雪线到针叶林逐层过渡的山地剖面，适合观察生成结果。", "程序化 · 地形 · 大量体素", "大型", "理解噪声地形和分层材质"),
        new(ProjectTemplateId.MinecraftValley, "Minecraft 山谷", "湖盆、瀑布、灯笼小径和混交林组成可漫游的山谷，树木作为独立层保存。", "MC 风格 · 分层结构 · 水面 · 发光", "中型", "学习分层世界和对象所有权"),
        new(ProjectTemplateId.Architecture, "柱廊庭院", "围绕镜水池组织的柱廊、圆拱、台阶和铜皮穹顶，强调比例和轴线。", "建筑 · 对称 · 曲线", "中型", "练习模块化建筑和对称构图"),
        new(ProjectTemplateId.CameraGallery, "多相机构图室", "螺旋雕塑、参照柱和三条不同视线组成小型摄影棚，每台相机都有明确任务。", "相机 · 构图 · 景深", "中型", "学习相机、构图和景深"),
        new(ProjectTemplateId.LargeWorld, "超大型建筑 · 1024×1024×1024", "阶梯圣坛、空心巨塔、悬链拱桥与环带街区组成真正用于性能验证的远景建筑群。", "大世界 · LOD · 建筑 · 性能压力", "超大型", "验证大坐标、LOD 和 GPU 工作集"),
        new(ProjectTemplateId.RenderingGallery, "渲染体验馆 · 全效果", "从近处玻璃凉亭、码头船队到远处灯塔和塔楼，形成有前景、中景、远景的海湾小镇。", "小中大规模 · 光照 · AO · 透明 · 发光 · 路径追踪", "小—大型", "用一个完整场景体验最终渲染链路"),
    ];

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
        if (id != ProjectTemplateId.Empty)
        {
            WriteMaterials(project, id);
            WriteProjectPalette(project);
            WriteRenderPreset(project, settings);
            var modelPath = Path.Combine(project.AssetsPath, "Worlds", "主世界.mvmodel");
            if (id == ProjectTemplateId.MinecraftValley)
            {
                var terrain = TemplateScenes.BuildValleyTerrain();
                LargeWorldFormat.Write(modelPath, terrain);
                var trees = TemplateScenes.BuildValleyTrees(terrain);
                var layerParts = new List<(string Name, string ModelAssetPath, int VoxelCount, VoxelCoord64 Position)>();
                for (var index = 0; index < trees.Count; index++)
                {
                    var treeFile = $"Minecraft山谷_树{index + 1}.mvmodel";
                    LargeWorldFormat.Write(Path.Combine(project.AssetsPath, "Worlds", treeFile), trees[index]);
                    layerParts.Add(($"树 {index + 1}", "Assets/Worlds/" + treeFile,
                        trees[index].OccupiedVoxelCount, GetWorldOrigin(trees[index])));
                }

                WriteScene(project, Get(id), terrain.OccupiedVoxelCount,
                    "Assets/Worlds/主世界.mvmodel", layerParts: layerParts, framingWorld: terrain);
            }
            else if (id is ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery)
            {
                var largeFileName = id == ProjectTemplateId.LargeWorld ? "超大型场景.mvworld" : "渲染体验馆.mvworld";
                var largePath = Path.Combine(project.AssetsPath, "Worlds", largeFileName);
                var world = BuildWorld(id, VoxelPalette.Build());
                LargeWorldFormat.Write(largePath, world);
                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    $"Assets/Worlds/{largeFileName}", framingWorld: world);
            }
            else if (id == ProjectTemplateId.Showcase)
            {
                // 每个层级对象都绑定独立模型资产，确保它们可单独隐藏、选中、移动和编辑。
                var world = BuildWorld(id, VoxelPalette.Build());
                LargeWorldFormat.Write(modelPath, world);
                var modelParts = new List<SceneModelPart>();
                foreach (var (name, part, partWorld) in TemplateScenes.BuildCottageParts(world))
                {
                    var fileName = $"小屋与花园_{name}.mvmodel";
                    LargeWorldFormat.Write(Path.Combine(project.AssetsPath, "Worlds", fileName), partWorld);
                    modelParts.Add(new SceneModelPart(name, part, "Assets/Worlds/" + fileName,
                        partWorld.OccupiedVoxelCount, GetWorldOrigin(partWorld)));
                }

                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    "Assets/Worlds/主世界.mvmodel", modelParts: modelParts, framingWorld: world);
            }
            else
            {
                var world = BuildWorld(id, VoxelPalette.Build());
                LargeWorldFormat.Write(modelPath, world);
                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    $"Assets/Worlds/{Path.GetFileName(modelPath)}",
                    cameraCount: id == ProjectTemplateId.CameraGallery ? 3 : 1,
                    framingWorld: world);
            }
        }

        report?.Invoke("正在写入模板配置…", .91F, false);
        SettingsStore.SaveProject(project, settings);
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
            ProjectTemplateId.Showcase => TemplateScenes.BuildCottageGarden(),
            ProjectTemplateId.MaterialLab => TemplateScenes.BuildMaterialLab(),
            ProjectTemplateId.PaletteWorkshop => TemplateScenes.BuildPaletteWorkshop(palette),
            ProjectTemplateId.SparseWorld => TemplateScenes.BuildSparseWorld(),
            ProjectTemplateId.LodPlayground => TemplateScenes.BuildLodPlayground(),
            ProjectTemplateId.RayTracingStudio => TemplateScenes.BuildRayTracingStudio(),
            ProjectTemplateId.ProceduralTerrain => TemplateScenes.BuildProceduralTerrain(),
            ProjectTemplateId.MinecraftValley => TemplateScenes.BuildValleyTerrain(),
            ProjectTemplateId.Architecture => TemplateScenes.BuildColonnade(),
            ProjectTemplateId.CameraGallery => TemplateScenes.BuildSculptureHall(),
            ProjectTemplateId.LargeWorld => TemplateScenes.BuildMegastructure(),
            ProjectTemplateId.RenderingGallery => TemplateScenes.BuildHarborTown(),
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
        // 空项目和偏编辑/性能验证模板从实时渲染开始；需要展示光照、透明和景深的模板在各自分支显式开启。
        settings.RenderSettings.PathTracingEnabled = false;
        switch (id)
        {
            case ProjectTemplateId.SparseWorld:
                settings.DefaultRenderPreset = "大世界稀疏预览";
                settings.RenderSettings.GroundFade = 240F;
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
                settings.RenderSettings.ResolutionScale = .85F;
                break;
            case ProjectTemplateId.RayTracingStudio:
                settings.DefaultRenderPreset = "光追工作室";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathSamplesPerFrame = 2;
                settings.RenderSettings.PathMaxAccumulatedSamples = 768;
                settings.RenderSettings.ShadowSoftness = .16F;
                settings.RenderSettings.ShadowSamples = 6;
                settings.RenderSettings.AmbientOcclusionEnabled = true;
                settings.RenderSettings.AoStrength = .92F;
                settings.RenderSettings.AoPower = 1.35F;
                settings.RenderSettings.SkyAmbient = .3F;
                settings.RenderSettings.AmbientTint = .22F;
                settings.RenderSettings.Tone = ToneCurve.AcesNarkowicz;
                settings.RenderSettings.Exposure = .35F;
                settings.RenderSettings.Vignette = .18F;
                settings.RenderSettings.PathRoughness = .18F;
                settings.RenderSettings.PathMetalness = .12F;
                settings.RenderSettings.PathSpecularF0 = .055F;
                settings.RenderSettings.RussianRouletteThreshold = .003F;
                settings.RenderSettings.RadianceClamp = 24F;
                settings.RenderSettings.MaxBounces = 8;
                settings.RenderSettings.PathDenoising = true;
                settings.RenderSettings.PathDenoiseStrength = .68F;
                settings.RenderSettings.EmissiveIntensity = 18F;
                settings.RenderSettings.TransmissiveAbsorption = .35F;
                settings.RenderSettings.DirectLightingMIS = true;
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
                settings.DefaultRenderPreset = "程序化地形";
                settings.RenderSettings.GroundFade = 180F;
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
                settings.RenderSettings.ResolutionScale = .65F;
                settings.RenderSettings.PathMaxAccumulatedSamples = 64;
                settings.RenderSettings.EmissiveIntensity = 12F;
                break;
            case ProjectTemplateId.RenderingGallery:
                settings.DefaultRenderPreset = "全效果展示";
                settings.RenderSettings.ShowGround = true;
                settings.RenderSettings.GroundColor = new Vector3(.18F, .24F, .30F);
                settings.RenderSettings.GroundFade = 220F;
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
                settings.RenderSettings.DirectLightingMIS = true;
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
        VoxelWorld? framingWorld = null)
    {
        var scene = new SceneDocument { Name = template.Name };
        var graph = new SceneGraph(scene);
        if (modelParts is not null)
        {
            foreach (var modelPart in modelParts)
            {
                var modelObject = graph.Add(modelPart.Name,
                    transform: TransformComponent.FromGrid(new GridTransform(
                        modelPart.Position, OrthogonalRotation.Identity)));
                modelObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                    modelPart.ModelAssetPath, modelPart.VoxelCount, template.Id.ToString(), modelPart.Part));
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
        string Name, string Part, string ModelAssetPath, int VoxelCount, VoxelCoord64 Position);

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
            ProjectTemplateId.RayTracingStudio => [("光追展示台", "展示台", new VoxelCoord64(0, 0, 0))],
            ProjectTemplateId.ProceduralTerrain => [("程序化地形", "地形", new VoxelCoord64(0, 0, 0))],
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
    private static Quaternion CreateCameraRotation(Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, up));
    }
}
