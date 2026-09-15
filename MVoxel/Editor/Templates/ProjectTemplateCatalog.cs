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
    string Focus);

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
        new(ProjectTemplateId.Empty, "无 · 空项目", "只创建标准目录和配置，不创建任何体素模型", "适合从零开始"),
        new(ProjectTemplateId.Showcase, "基础展示 · 小屋与花园", "小屋、树木、石板路、玻璃窗和自发光提灯", "基础建模 · 材质 · 自发光"),
        new(ProjectTemplateId.MaterialLab, "材质实验室", "不透明、半透明、自发光和材质槽的对照场景", "材质 · 透明 · 发光"),
        new(ProjectTemplateId.PaletteWorkshop, "调色板工坊", "用当前调色板生成纯色体素材质色阶和材质槽对照", "调色板 · 纯色材质 · 材质属性"),
        new(ProjectTemplateId.SparseWorld, "稀疏世界 · 远距离地标", "负坐标、多区块、远距离建筑和少量占用体素", "大世界 · 稀疏存储 · 负坐标"),
        new(ProjectTemplateId.LodPlayground, "LOD 练习场", "从近到远排列不同复杂度的建筑组，方便调试 LOD", "LOD · 场景性能 · 批量对象"),
        new(ProjectTemplateId.RayTracingStudio, "光追工作室", "发光面、玻璃屏、彩色材质和暗室布光", "路径追踪 · 直接光 · 透明"),
        new(ProjectTemplateId.ProceduralTerrain, "程序化地形", "由高度函数生成的分层山谷、平台和水面", "程序化 · 地形 · 大量体素"),
        new(ProjectTemplateId.Architecture, "模块化建筑", "庭院、拱门、立柱、台阶和对称建筑模块", "建筑 · 对称 · 模块化"),
        new(ProjectTemplateId.CameraGallery, "多相机构图室", "同一体素场景配置三个相机，演示渲染视图切换和相机对象联动", "相机 · 构图 · 渲染视图"),
        new(ProjectTemplateId.LargeWorld, "超大型建筑 · 1024×1024×1024", "三轴 1024 格的稀疏建筑群、塔楼和高空连桥性能验证", "大世界 · LOD · 建筑 · 性能压力"),
        new(ProjectTemplateId.RenderingGallery, "渲染体验馆 · 全效果", "小型花园、中型大厅、大型塔楼、玻璃水池和发光灯带组成的完整展示场景", "小中大规模 · 光照 · AO · 透明 · 发光 · 路径追踪"),
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
        report?.Invoke("正在生成示例体素模型…", .76F, true);
        var settings = CreateSettings(id);
        settings.TemplateId = id.ToString();
        if (id != ProjectTemplateId.Empty)
        {
            var modelPath = Path.Combine(project.AssetsPath, "VoxelModels", "主场景.mvmodel");
            if (id is ProjectTemplateId.LargeWorld or ProjectTemplateId.RenderingGallery)
            {
                var largeFileName = id == ProjectTemplateId.LargeWorld ? "超大型场景.mvworld" : "渲染体验馆.mvworld";
                var largePath = Path.Combine(project.AssetsPath, "VoxelModels", largeFileName);
                if (id == ProjectTemplateId.LargeWorld)
                {
                    LargeWorldFormat.WriteDemo(largePath);
                }
                else
                {
                    LargeWorldFormat.Write(largePath, BuildWorld(id, VoxelPalette.Build()));
                }

                WriteScene(project, Get(id), id == ProjectTemplateId.LargeWorld
                        ? LargeWorldFormat.DemoVoxelCount
                        : BuildWorld(id, VoxelPalette.Build()).OccupiedVoxelCount,
                    $"Assets/VoxelModels/{largeFileName}");
            }
            else
            {
                var world = BuildWorld(id, VoxelPalette.Build());
                LargeWorldFormat.Write(modelPath, world);
                WriteScene(project, Get(id), world.OccupiedVoxelCount,
                    $"Assets/VoxelModels/{Path.GetFileName(modelPath)}",
                    cameraCount: id == ProjectTemplateId.CameraGallery ? 3 : 1);
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
            ProjectTemplateId.Showcase => VoxelDemoScene.Build(palette),
            ProjectTemplateId.MaterialLab => BuildMaterialLab(),
            ProjectTemplateId.PaletteWorkshop => BuildPaletteWorkshop(palette),
            ProjectTemplateId.SparseWorld => BuildSparseWorld(),
            ProjectTemplateId.LodPlayground => BuildLodPlayground(),
            ProjectTemplateId.RayTracingStudio => BuildRayTracingStudio(),
            ProjectTemplateId.ProceduralTerrain => BuildProceduralTerrain(),
            ProjectTemplateId.Architecture => BuildArchitecture(),
            ProjectTemplateId.CameraGallery => BuildCameraGallery(),
            ProjectTemplateId.LargeWorld => LargeWorldFormat.BuildDemoWorld(),
            ProjectTemplateId.RenderingGallery => BuildRenderingGallery(),
            _ => new VoxelWorld(),
        };
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
            case ProjectTemplateId.SparseWorld:
                settings.LodLevels = 5;
                settings.LodDistances = [48F, 96F, 192F, 384F, 768F, 1536F, 3072F, 6144F];
                settings.DefaultRenderPreset = "大世界稀疏预览";
                settings.RenderSettings.GroundFade = 240F;
                break;
            case ProjectTemplateId.LodPlayground:
                settings.LodLevels = 6;
                settings.LodDistances = [24F, 48F, 96F, 192F, 384F, 768F, 1536F, 3072F];
                settings.DefaultRenderPreset = "LOD 练习";
                settings.RenderSettings.ResolutionScale = .85F;
                break;
            case ProjectTemplateId.RayTracingStudio:
                settings.DefaultRenderPreset = "光追工作室";
                settings.RenderSettings.PathTracingEnabled = true;
                settings.RenderSettings.PathMaxAccumulatedSamples = 512;
                settings.RenderSettings.MaxBounces = 8;
                settings.RenderSettings.PathDenoising = true;
                settings.RenderSettings.EmissiveIntensity = 18F;
                settings.RenderSettings.TransmissiveAbsorption = .35F;
                break;
            case ProjectTemplateId.PaletteWorkshop:
                settings.DefaultRenderPreset = "调色板材质展示";
                settings.RenderSettings.PathMaxAccumulatedSamples = 256;
                break;
            case ProjectTemplateId.ProceduralTerrain:
                settings.LodLevels = 5;
                settings.DefaultRenderPreset = "程序化地形";
                settings.RenderSettings.GroundFade = 180F;
                break;
            case ProjectTemplateId.LargeWorld:
                settings.LodLevels = 8;
                settings.LodDistances = [96F, 192F, 384F, 768F, 1536F, 3072F, 6144F, 12288F];
                settings.DefaultRenderPreset = "超大型场景性能验证";
                settings.RenderSettings.ResolutionScale = .65F;
                settings.RenderSettings.PathMaxAccumulatedSamples = 64;
                break;
            case ProjectTemplateId.CameraGallery:
                settings.DefaultRenderPreset = "多相机构图";
                settings.RenderSettings.PathMaxAccumulatedSamples = 256;
                break;
            case ProjectTemplateId.RenderingGallery:
                settings.LodLevels = 6;
                settings.LodDistances = [32F, 64F, 128F, 256F, 512F, 1024F, 2048F, 4096F];
                settings.DefaultRenderPreset = "全效果展示";
                settings.RenderSettings.ShowGround = true;
                settings.RenderSettings.GroundColor = new Vector3(.18F, .24F, .30F);
                settings.RenderSettings.GroundFade = 220F;
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
                settings.RenderSettings.PathDenoiseStrength = .28F;
                settings.RenderSettings.EmissiveVoxels = true;
                settings.RenderSettings.EmissiveIntensity = 20F;
                settings.RenderSettings.TransmissiveAbsorption = .28F;
                settings.RenderSettings.DepthOfField = true;
                settings.RenderSettings.ApertureRadius = .045F;
                settings.RenderSettings.FocusDistance = 36F;
                settings.RenderSettings.SceneBackgroundColor = new Vector3(.02F, .025F, .05F);
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
        string modelAssetPath = "Assets/VoxelModels/主场景.mvmodel", int cameraCount = 1)
    {
        var scene = new SceneDocument { Name = template.Name };
        var graph = new SceneGraph(scene);
        foreach (var (name, part, position) in GetTemplateParts(template.Id))
        {
            var modelObject = graph.Add(name,
                transform: TransformComponent.FromGrid(new GridTransform(position, OrthogonalRotation.Identity)));
            modelObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                modelAssetPath, voxelCount, template.Id.ToString(), part));
        }

        var lightObject = graph.Add("主光照", transform: TransformComponent.FromFree(new FreeTransform(
            new Double3(0D, 24D, 0D), Quaternion.Identity)));
        lightObject.Components.Add(SceneComponentTypes.CreateLight(LightComponentData.Default));
        var positions = new[]
        {
            new Double3(0D, 8D, 18D),
            new Double3(22D, 7D, 4D),
            new Double3(-20D, 10D, -16D),
        };
        for (var index = 0; index < Math.Clamp(cameraCount, 1, positions.Length); index++)
        {
            var position = positions[index];
            var forward = Vector3.Normalize(new Vector3((float)-position.X, -3.5F, (float)-position.Z));
            var cameraObject = graph.Add(index == 0 ? "主相机" : $"相机 {index + 1}",
                transform: TransformComponent.FromFree(new FreeTransform(
                    position, CreateCameraRotation(forward, Vector3.UnitY))));
            cameraObject.Components.Add(SceneComponentTypes.CreateCamera());
        }
        SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), scene);
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
            ProjectTemplateId.Architecture => [("模块化建筑", "建筑", new VoxelCoord64(0, 0, 0))],
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

    /// <summary>
    /// 构建材质实验室
    /// </summary>
    private static VoxelWorld BuildMaterialLab()
    {
        var world = new VoxelWorld();
        var floor = new VoxelValue(new Rgba32(52, 62, 78));
        var red = new VoxelValue(new Rgba32(220, 76, 84), MaterialSlot: 1);
        var glass = new VoxelValue(new Rgba32(100, 196, 238, 112), MaterialSlot: 2, Flags: VoxelFlags.Translucent);
        var lamp = new VoxelValue(new Rgba32(255, 204, 92), MaterialSlot: 3, Flags: VoxelFlags.Emissive);
        FillBox(world, -16, 0, -10, 16, 0, 10, floor);
        FillBox(world, -13, 1, -6, -5, 4, 2, red);
        FillBox(world, -2, 1, -6, 6, 4, 2, glass);
        FillBox(world, 9, 1, -6, 13, 4, 2, lamp);
        FillBox(world, -12, 5, -5, -6, 11, 1, new VoxelValue(new Rgba32(86, 142, 228), MaterialSlot: 1));
        FillBox(world, -1, 5, -5, 5, 11, 1, new VoxelValue(new Rgba32(112, 218, 242, 96), MaterialSlot: 2, Flags: VoxelFlags.Translucent));
        FillBox(world, 9, 5, -5, 13, 11, 1, new VoxelValue(new Rgba32(255, 132, 58), MaterialSlot: 3, Flags: VoxelFlags.Emissive));
        return world;
    }

    /// <summary>
    /// 构建调色板基础色展示场
    /// </summary>
    /// <param name="palette">当前生效的调色板</param>
    /// <returns>调色板展示世界</returns>
    private static VoxelWorld BuildPaletteWorkshop(Rgba32[] palette)
    {
        var world = new VoxelWorld();
        var floor = new VoxelValue(new Rgba32(34, 40, 52));
        FillBox(world, -36, 0, -20, 36, 0, 20, floor);
        for (var index = 1; index < Math.Min(VoxelPalette.EntryCount, 129); index++)
        {
            var column = (index - 1) % 16;
            var row = (index - 1) / 16;
            var height = 1 + index % 7;
            var color = palette[index];
            var flags = (byte)(color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None);
            FillBox(world, -31 + column * 4, 1, -16 + row * 5,
                -29 + column * 4, height, -14 + row * 5,
                new VoxelValue(color, MaterialSlot: (ushort)(index % 8), Flags: flags));
        }

        return world;
    }

    /// <summary>
    /// 构建跨越多个区块的稀疏地标
    /// </summary>
    private static VoxelWorld BuildSparseWorld()
    {
        var world = new VoxelWorld();
        var stone = new VoxelValue(new Rgba32(104, 128, 154));
        var light = new VoxelValue(new Rgba32(242, 190, 76), Flags: VoxelFlags.Emissive);
        foreach (var x in new[] { -384L, 0L, 384L })
        foreach (var z in new[] { -384L, 0L, 384L })
        {
            FillBox(world, x - 3, 0, z - 3, x + 3, 31 + (Math.Abs(x) + Math.Abs(z)) % 48, z + 3, stone);
            FillBox(world, x - 1, 32, z - 1, x + 1, 34, z + 1, light);
        }

        for (var index = -3; index <= 3; index++)
        {
            FillBox(world, -384 + index * 48, 0, 0, -384 + index * 48 + 15, 2, 7, stone);
            FillBox(world, 0, 0, -384 + index * 48, 7, 2, -384 + index * 48 + 15, stone);
        }

        return world;
    }

    /// <summary>
    /// 构建不同密度和高度的 LOD 观察场
    /// </summary>
    private static VoxelWorld BuildLodPlayground()
    {
        var world = new VoxelWorld();
        var ground = new VoxelValue(new Rgba32(72, 108, 92));
        var block = new VoxelValue(new Rgba32(82, 158, 218));
        FillBox(world, -90, 0, -24, 90, 0, 24, ground);
        for (var column = 0; column < 13; column++)
        {
            var x = -78 + column * 13;
            var height = 4 + column * 2;
            var width = Math.Max(1, 6 - column / 3);
            FillBox(world, x - width, 1, -width, x + width, height, width, block);
            if (column % 2 == 0)
            {
                FillBox(world, x - width - 1, height + 1, -width - 1,
                    x + width + 1, height + 1, width + 1, new VoxelValue(new Rgba32(232, 178, 82)));
            }
        }

        return world;
    }

    /// <summary>
    /// 构建暗室中的发光和透明材质组合
    /// </summary>
    private static VoxelWorld BuildRayTracingStudio()
    {
        var world = new VoxelWorld();
        var wall = new VoxelValue(new Rgba32(32, 38, 52));
        var red = new VoxelValue(new Rgba32(220, 68, 76), MaterialSlot: 1);
        var blue = new VoxelValue(new Rgba32(76, 132, 236), MaterialSlot: 1);
        var glass = new VoxelValue(new Rgba32(130, 220, 238, 96), MaterialSlot: 2, Flags: VoxelFlags.Translucent);
        var emitter = new VoxelValue(new Rgba32(255, 216, 102), MaterialSlot: 3, Flags: VoxelFlags.Emissive);
        FillBox(world, -24, 0, -18, 24, 0, 18, wall);
        FillBox(world, -24, 1, -18, -24, 14, 18, wall);
        FillBox(world, 24, 1, -18, 24, 14, 18, wall);
        FillBox(world, -24, 1, -18, 24, 14, -18, wall);
        FillBox(world, -18, 12, -8, 18, 12, 8, emitter);
        FillBox(world, -11, 1, -6, -3, 8, 2, red);
        FillBox(world, 4, 1, -6, 12, 5, 2, blue);
        FillBox(world, -1, 1, 7, 1, 10, 7, glass);
        FillBox(world, -3, 1, 6, 3, 1, 12, new VoxelValue(new Rgba32(92, 98, 116)));
        return world;
    }

    /// <summary>
    /// 构建程序化起伏地形
    /// </summary>
    private static VoxelWorld BuildProceduralTerrain()
    {
        var world = new VoxelWorld();
        var rock = new VoxelValue(new Rgba32(112, 118, 132));
        var soil = new VoxelValue(new Rgba32(154, 112, 76));
        var grass = new VoxelValue(new Rgba32(92, 176, 106));
        var water = new VoxelValue(new Rgba32(72, 164, 222, 150), Flags: VoxelFlags.Translucent);
        for (var x = -48; x <= 48; x++)
        for (var z = -48; z <= 48; z++)
        {
            var height = 3 + (int)MathF.Round(
                MathF.Sin(x * .14F) * 3F + MathF.Cos(z * .11F) * 2F + MathF.Sin((x + z) * .07F) * 3F);
            height = Math.Clamp(height, 1, 12);
            for (var y = 0; y < height; y++)
            {
                var value = y == height - 1 ? grass : y >= height - 3 ? soil : rock;
                world.Set(new VoxelCoord64(x, y, z), value);
            }

            if (height <= 3)
            {
                world.Set(new VoxelCoord64(x, 3, z), water);
            }
        }

        return world;
    }

    /// <summary>
    /// 构建带拱门和庭院的模块化建筑
    /// </summary>
    private static VoxelWorld BuildArchitecture()
    {
        var world = new VoxelWorld();
        var wall = new VoxelValue(new Rgba32(188, 176, 154));
        var trim = new VoxelValue(new Rgba32(76, 108, 148));
        var roof = new VoxelValue(new Rgba32(184, 72, 66));
        FillBox(world, -18, 0, -14, 18, 0, 14, new VoxelValue(new Rgba32(92, 120, 88)));
        FillBox(world, -18, 1, -14, -18, 13, 14, wall);
        FillBox(world, 18, 1, -14, 18, 13, 14, wall);
        FillBox(world, -18, 1, -14, 18, 13, -14, wall);
        FillBox(world, -18, 1, 14, -5, 13, 14, wall);
        FillBox(world, 5, 1, 14, 18, 13, 14, wall);
        for (var x = -5; x <= 5; x++)
        for (var y = 1; y <= 5; y++)
        {
            if (Math.Abs(x) <= 2 && y <= 3) continue;
            world.Set(new VoxelCoord64(x, y, 14), wall);
        }

        foreach (var x in new[] { -13L, 13L })
            FillBox(world, x - 2, 1, -5, x + 2, 10, 5, trim);
        for (var layer = 0; layer < 4; layer++)
            FillBox(world, -15 + layer, 14 + layer, -11 + layer, 15 - layer, 14 + layer, 11 - layer, roof);
        return world;
    }

    /// <summary>
    /// 构建适合多相机构图的中心展台
    /// </summary>
    /// <returns>多相机展示世界</returns>
    private static VoxelWorld BuildCameraGallery()
    {
        var world = new VoxelWorld();
        var floor = new VoxelValue(new Rgba32(54, 72, 82));
        var platform = new VoxelValue(new Rgba32(90, 140, 182));
        var warm = new VoxelValue(new Rgba32(232, 160, 72));
        var cool = new VoxelValue(new Rgba32(76, 168, 224));
        FillBox(world, -28, 0, -22, 28, 0, 22, floor);
        FillBox(world, -10, 1, -10, 10, 2, 10, platform);
        for (var layer = 0; layer < 5; layer++)
        {
            FillBox(world, -8 + layer, 3 + layer, -8 + layer,
                8 - layer, 3 + layer, 8 - layer, layer % 2 == 0 ? warm : cool);
        }

        foreach (var x in new[] { -20L, 20L })
        foreach (var z in new[] { -14L, 14L })
            FillBox(world, x - 2, 1, z - 2, x + 2, 12, z + 2, platform);
        return world;
    }

    /// <summary>
    /// 构建覆盖小、中、大规模和主要材质效果的完整渲染体验馆
    /// </summary>
    private static VoxelWorld BuildRenderingGallery()
    {
        var world = new VoxelWorld();
        var ground = new VoxelValue(new Rgba32(48, 64, 82));
        var stone = new VoxelValue(new Rgba32(112, 126, 148));
        var wall = new VoxelValue(new Rgba32(210, 198, 172));
        var trim = new VoxelValue(new Rgba32(54, 104, 156), MaterialSlot: 1);
        var roof = new VoxelValue(new Rgba32(176, 64, 70), MaterialSlot: 2);
        var leaf = new VoxelValue(new Rgba32(74, 168, 96), MaterialSlot: 3);
        var wood = new VoxelValue(new Rgba32(116, 76, 46), MaterialSlot: 4);
        var glass = new VoxelValue(new Rgba32(94, 200, 238, 105), MaterialSlot: 5, Flags: VoxelFlags.Translucent);
        var lamp = new VoxelValue(new Rgba32(255, 202, 82), MaterialSlot: 6, Flags: VoxelFlags.Emissive);
        var water = new VoxelValue(new Rgba32(50, 150, 220, 120), MaterialSlot: 7, Flags: VoxelFlags.Translucent);

        FillBox(world, -64, 0, -42, 64, 0, 42, ground);
        // 小型花园与水池
        FillBox(world, -48, 1, -28, -26, 1, -8, stone);
        FillBox(world, -44, 2, -24, -30, 2, -12, water);
        FillBox(world, -39, 3, -21, -35, 7, -17, wood);
        FillBox(world, -42, 8, -24, -32, 10, -14, leaf);
        FillBox(world, -39, 11, -21, -35, 11, -17, leaf);
        for (var index = 0; index < 5; index++)
            FillBox(world, -49 + index * 5, 2, -6, -47 + index * 5, 4, -4, lamp);

        // 中型主厅，玻璃幕墙与暖色灯带
        FillBox(world, -18, 1, -20, 20, 1, 20, stone);
        FillBox(world, -15, 2, -17, 17, 13, -17, wall);
        FillBox(world, -15, 2, 17, 17, 13, 17, wall);
        FillBox(world, -15, 2, -17, -15, 13, 17, wall);
        FillBox(world, 17, 2, -17, 17, 13, 17, wall);
        FillBox(world, -11, 4, -18, 13, 9, -18, glass);
        FillBox(world, -13, 14, -15, 15, 14, 15, trim);
        for (var layer = 0; layer < 4; layer++)
            FillBox(world, -12 + layer, 15 + layer, -14 + layer, 14 - layer, 15 + layer, 14 - layer, roof);
        for (var x = -10; x <= 10; x += 5)
            FillBox(world, x, 12, -16, x + 1, 13, -16, lamp);

        // 大型远景塔楼与连桥，测试大范围深度、雾和区块边界
        FillBox(world, 34, 1, -18, 48, 44, -4, trim);
        FillBox(world, 31, 8, -21, 51, 8, -1, stone);
        for (var level = 0; level < 6; level++)
        {
            var y = 45 + level * 3;
            FillBox(world, 32 - level, y, -20 - level, 50 + level, y, -2 + level, roof);
        }
        FillBox(world, 19, 10, -12, 34, 12, -10, stone);
        FillBox(world, 20, 13, -11, 33, 13, -9, lamp);
        for (var tree = 0; tree < 7; tree++)
        {
            var tx = 24 + tree * 4;
            FillBox(world, tx, 1, 24, tx + 1, 7 + tree % 3, 25, wood);
            FillBox(world, tx - 3, 8 + tree % 3, 21, tx + 4, 11 + tree % 3, 28, leaf);
        }

        return world;
    }

    /// <summary>
    /// 填充包含端点的长方体体素区域
    /// </summary>
    private static void FillBox(VoxelWorld world, long minX, long minY, long minZ,
        long maxX, long maxY, long maxZ, VoxelValue value)
    {
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
            world.Set(new VoxelCoord64(x, y, z), value);
    }
}
