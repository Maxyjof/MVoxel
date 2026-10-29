using System.Numerics;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 仙域九霄世界装配：云海山体六分区、青云剑宗建筑群、人间城郭与古迹、
/// 三座浮空仙岛与登天石阶合成为真正分件的 MVoxel 项目
/// </summary>
internal static class XianxiaWorld
{
    private const string TemplateId = "XianxiaContinent";

    /// <summary>一件可独立编辑的体素分件</summary>
    /// <param name="Name">对象名</param>
    /// <param name="Role">职责说明</param>
    /// <param name="Layer">所属图层</param>
    /// <param name="World">体素世界</param>
    /// <param name="OffsetX">装配偏移 X</param>
    /// <param name="OffsetY">装配偏移 Y</param>
    /// <param name="OffsetZ">装配偏移 Z</param>
    private readonly record struct Piece(string Name, string Role, XianxiaLayer Layer,
        VoxelWorld World, int OffsetX, int OffsetY, int OffsetZ);

    /// <summary>相机机位与朝向</summary>
    /// <param name="Name">相机名</param>
    /// <param name="Position">位置</param>
    /// <param name="Target">注视点</param>
    /// <param name="FieldOfView">垂直视场角</param>
    private readonly record struct Shot(string Name, Vector3 Position, Vector3 Target, float FieldOfView);

    private static readonly string[] LayerNames =
    [
        "默认", "山体", "水系", "生态", "宗门", "城市", "浮空", "遗迹", "氛围",
    ];

    /// <summary>
    /// 生成整套仙域世界并写入项目
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="report">进度回调</param>
    public static void Populate(ProjectHandle project, Action<string, float, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        report?.Invoke("正在隆起云海仙峰…", .6F, true);
        var terrain = XianxiaTerrain.Build();

        report?.Invoke("正在建造宗门与城郭…", .78F, true);
        var parts = new List<Piece>();
        AddTerrainParts(parts, terrain);
        AddLandmarkParts(parts);
        AddSkyParts(parts);

        report?.Invoke("正在写入体素数据…", .86F, true);
        WriteModelFiles(project, parts);
        WriteExtraMaterials(project);
        EditorPalettes.SaveProjectPalette(project, XianxiaPalette.BuildProjectPalette());

        report?.Invoke("正在编排场景与相机…", .94F, false);
        WriteScene(project, parts);
    }

    private static void AddTerrainParts(List<Piece> parts, XianxiaTerrainResult terrain)
    {
        foreach (var (region, world) in terrain.Worlds)
        {
            if (world.OccupiedVoxelCount == 0) continue;
            var (name, role) = region switch
            {
                XianxiaRegion.Summit => ("青云顶 · 山巅", "山巅台地"),
                XianxiaRegion.East => ("东崖 · 听涛", "东侧山体"),
                XianxiaRegion.South => ("南麓 · 问道", "南侧山体"),
                XianxiaRegion.West => ("西崖 · 栖霞", "西侧山体"),
                XianxiaRegion.North => ("北麓 · 负雪", "北侧山体"),
                _ => ("九霄云海", "云海"),
            };
            parts.Add(new Piece(name, role, XianxiaLayer.Terrain, world, 0, 0, 0));
        }
    }

    private static void AddLandmarkParts(List<Piece> parts)
    {
        var sites = XianxiaGeography.Sites;
        // 地标与基址一一对应，基址高程就是建筑的落脚地面
        Add(parts, "青云剑宗 · 主殿群", "宗门主殿", XianxiaLayer.Sect, XianxiaSect.BuildSectComplex(), Site("青云剑宗"));
        Add(parts, "青云剑宗 · 藏经阁", "藏经阁", XianxiaLayer.Sect, XianxiaSect.BuildScripturePavilion(), Site("藏经阁"));
        Add(parts, "青云剑宗 · 演武场", "演武场", XianxiaLayer.Sect, XianxiaSect.BuildSwordArena(), Site("演武场"));
        Add(parts, "青云剑宗 · 山门坊", "山门", XianxiaLayer.Sect, XianxiaSect.BuildGateArch(), Site("山门坊"));
        Add(parts, "长乐坊市", "坊市", XianxiaLayer.City, XianxiaCity.BuildMarketTown(), Site("长乐坊市"));
        Add(parts, "云海渡口", "渡口", XianxiaLayer.City, XianxiaCity.BuildSpiritHarbor(), Site("云海渡口"));
        Add(parts, "灵田梯田", "灵田", XianxiaLayer.City, XianxiaCity.BuildSpiritFields(), Site("灵田梯田"));
        Add(parts, "望海亭", "望海亭", XianxiaLayer.City, XianxiaCity.BuildSeaPavilion(), Site("望海亭"));
        Add(parts, "剑冢", "剑冢", XianxiaLayer.Ruin, XianxiaLandmarks.BuildSwordTomb(), Site("剑冢"));
        Add(parts, "锁妖塔", "锁妖塔", XianxiaLayer.Ruin, XianxiaLandmarks.BuildDemonPagoda(), Site("锁妖塔"));
        Add(parts, "传送大阵", "传送阵", XianxiaLayer.Ruin, XianxiaLandmarks.BuildTeleportArray(), Site("传送大阵"));
        Add(parts, "古战场", "古战场", XianxiaLayer.Ruin, XianxiaLandmarks.BuildAncientBattlefield(), Site("古战场"));
        Add(parts, "世界树", "世界树", XianxiaLayer.Ecology, XianxiaSky.BuildWorldTree(), Site("世界树"));

        XianxiaSite Site(string name) => sites.First(site => site.Name == name);
    }

    private static void AddSkyParts(List<Piece> parts)
    {
        var (jadeX, jadeY, jadeZ) = XianxiaGeography.JadeCapital;
        var (fuyaoX, fuyaoY, fuyaoZ) = XianxiaGeography.FuYaoIsle;
        var (tingYuX, tingYuY, tingYuZ) = XianxiaGeography.TingYuIsle;
        Add(parts, "玉京浮岛", "浮空主岛", XianxiaLayer.Sky, XianxiaSky.BuildJadeCapital(), jadeX, jadeY, jadeZ);
        Add(parts, "扶摇浮岛", "浮空岛", XianxiaLayer.Sky, XianxiaSky.BuildFuYaoIsle(), fuyaoX, fuyaoY, fuyaoZ);
        Add(parts, "听雨浮岛", "浮空岛", XianxiaLayer.Sky, XianxiaSky.BuildTingYuIsle(), tingYuX, tingYuY, tingYuZ);

        // 登天石阶直接在世界坐标里生成，从山门平台盘旋到青云顶主殿平台
        var gate = XianxiaGeography.Sites.First(site => site.Name == "山门坊");
        var summit = XianxiaGeography.Sites.First(site => site.Name == "青云剑宗");
        var stairs = new VoxelWorld();
        XianxiaSky.BuildHeavenStairs(stairs, gate.X, XianxiaGeography.SiteLevel(gate.Name) + 1, gate.Z,
            summit.X, XianxiaGeography.SiteLevel(summit.Name) + 1, summit.Z);
        Add(parts, "登天石阶", "天阶", XianxiaLayer.Sky, stairs, 0, 0, 0);
    }

    private static void Add(List<Piece> parts, string name, string role, XianxiaLayer layer,
        VoxelWorld world, XianxiaSite site)
    {
        Add(parts, name, role, layer, world, site.X, XianxiaGeography.SiteLevel(site.Name), site.Z);
    }

    private static void Add(List<Piece> parts, string name, string role, XianxiaLayer layer,
        VoxelWorld world, int offsetX, int offsetY, int offsetZ)
    {
        if (world.OccupiedVoxelCount == 0) return;
        parts.Add(new Piece(name, role, layer, world, offsetX, offsetY, offsetZ));
    }

    private static void WriteModelFiles(ProjectHandle project, List<Piece> parts)
    {
        var directory = Path.Combine(project.AssetsPath, "Worlds");
        Directory.CreateDirectory(directory);
        foreach (var piece in parts)
        {
            LargeWorldFormat.Write(Path.Combine(directory, FileNameOf(piece.Name)), piece.World);
        }
    }

    private static string FileNameOf(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return $"仙域·{sanitized}.mvmodel";
    }

    private static void WriteExtraMaterials(ProjectHandle project)
    {
        var directory = Path.Combine(project.AssetsPath, "Materials");
        Directory.CreateDirectory(directory);
        var materials = new[]
        {
            new MaterialAsset { Name = "灵玉", Slot = XianxiaPalette.SlotJade, Roughness = .07F, Metallic = .12F },
            new MaterialAsset
            {
                Name = "云海", Slot = XianxiaPalette.SlotCloud, Roughness = .55F, AbsorptionStep = .35F,
                AbsorptionColor = new Rgba32(214, 226, 238),
            },
            new MaterialAsset { Name = "绸缎", Slot = XianxiaPalette.SlotCloth, Roughness = .72F },
        };
        foreach (var material in materials)
        {
            material.Save(Path.Combine(directory, $"{material.Name}.material"));
        }
    }

    private static void WriteScene(ProjectHandle project, List<Piece> parts)
    {
        var scene = new SceneDocument
        {
            Name = "仙域九霄",
            LayerNames = [.. LayerNames],
        };
        var graph = new SceneGraph(scene);

        var groups = new Dictionary<XianxiaLayer, StableId>
        {
            [XianxiaLayer.Terrain] = graph.Add("云海仙峰", transform: GroupTransform()).Id,
            [XianxiaLayer.Sect] = graph.Add("青云剑宗", transform: GroupTransform()).Id,
            [XianxiaLayer.City] = graph.Add("人间城郭", transform: GroupTransform()).Id,
            [XianxiaLayer.Sky] = graph.Add("浮空仙山", transform: GroupTransform()).Id,
            [XianxiaLayer.Ruin] = graph.Add("古迹禁地", transform: GroupTransform()).Id,
        };

        foreach (var piece in parts)
        {
            piece.World.TryGetBounds(out var minimum, out _);
            var position = new VoxelCoord64(
                piece.OffsetX + minimum.X, piece.OffsetY + minimum.Y, piece.OffsetZ + minimum.Z);
            var parent = groups.TryGetValue(piece.Layer, out var group) ? group : (StableId?)null;
            var sceneObject = graph.Add(piece.Name, parent,
                TransformComponent.FromGrid(new GridTransform(position, OrthogonalRotation.Identity)));
            sceneObject.LayerIndex = (int)piece.Layer;
            sceneObject.Components.Add(SceneComponentTypes.CreateVoxelModel(
                "Assets/Worlds/" + FileNameOf(piece.Name), piece.World.OccupiedVoxelCount,
                TemplateId, piece.Role));
        }

        var worldTreeObject = scene.Objects.FirstOrDefault(item => item.Name == "世界树");
        if (worldTreeObject is not null) worldTreeObject.LayerIndex = (int)XianxiaLayer.Ecology;

        AddLights(graph);
        AddCameras(graph);

        SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", "主场景.mvox"), scene);
    }

    private static TransformComponent GroupTransform() =>
        TransformComponent.FromGrid(new GridTransform(VoxelCoord64.Zero, OrthogonalRotation.Identity));

    private static void AddLights(SceneGraph graph)
    {
        var lights = new (string Name, Double3 Position, LightComponentData Data)[]
        {
            ("主日轮", new Double3(512D, 820D, 512D), new LightComponentData(1.15F, 1F, .96F, .88F, true)),
            ("云海反照", new Double3(300D, 140D, 900D), new LightComponentData(.5F, .76F, .84F, .96F, false)),
            ("人间灯火", new Double3(640D, 320D, 720D), new LightComponentData(.9F, 1F, .78F, .5F, false)),
        };
        foreach (var (name, position, data) in lights)
        {
            var lightObject = graph.Add(name,
                transform: TransformComponent.FromFree(new FreeTransform(position, Quaternion.Identity)));
            lightObject.LayerIndex = (int)XianxiaLayer.Atmosphere;
            lightObject.Components.Add(SceneComponentTypes.CreateLight(data));
        }
    }

    private static void AddCameras(SceneGraph graph)
    {
        var shots = new[]
        {
            new Shot("相机 · 云海仙峰", new Vector3(1656F, 656F, 1572F), new Vector3(500F, 392F, 500F), 40F),
            new Shot("相机 · 山门问道", new Vector3(512F, 302F, 1096F), new Vector3(512F, 438F, 664F), 46F),
            new Shot("相机 · 青云之巅", new Vector3(824F, 826F, 906F), new Vector3(512F, 646F, 500F), 40F),
            new Shot("相机 · 宗门平台", new Vector3(318F, 706F, 786F), new Vector3(512F, 596F, 500F), 50F),
            new Shot("相机 · 长乐坊市", new Vector3(764F, 342F, 868F), new Vector3(640F, 300F, 720F), 52F),
            new Shot("相机 · 玉京浮岛", new Vector3(132F, 648F, 452F), new Vector3(306F, 470F, 316F), 45F),
            new Shot("相机 · 悬空瀑布", new Vector3(768F, 430F, 748F), new Vector3(576F, 330F, 560F), 46F),
            new Shot("相机 · 云海渡口", new Vector3(1004F, 182F, 760F), new Vector3(892F, 128F, 566F), 48F),
        };

        foreach (var shot in shots)
        {
            var forward = shot.Target - shot.Position;
            if (forward.LengthSquared() < .0001F) forward = -Vector3.UnitZ;
            var cameraObject = graph.Add(shot.Name,
                transform: TransformComponent.FromFree(new FreeTransform(
                    new Double3(shot.Position.X, shot.Position.Y, shot.Position.Z),
                    CreateCameraRotation(forward, Vector3.UnitY))));
            cameraObject.LayerIndex = 0;
            cameraObject.Components.Add(SceneComponentTypes.CreateCamera(new CameraComponentData(
                shot.FieldOfView, .1F, 5000F, uint.MaxValue)));
        }
    }

    private static Quaternion CreateCameraRotation(Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, up));
    }
}
