using System.Numerics;
using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates;

/// <summary>
/// 为新项目生成可编辑的光照示例
/// </summary>
internal static class ExampleSceneTemplate
{
    internal const string FileName = "示例场景.mvox";

    internal static void Populate(ProjectHandle project)
    {
        var scene = new SceneDocument { Name = "示例场景" };
        var graph = new SceneGraph(scene);
        AddBox(project, graph, "地板", new VoxelCoord64(-12, 0, -12), new VoxelCoord64(12, 0, 12));
        AddBox(project, graph, "左墙", new VoxelCoord64(-12, 1, -12), new VoxelCoord64(-12, 14, 12));
        AddBox(project, graph, "后墙", new VoxelCoord64(-11, 1, -12), new VoxelCoord64(12, 14, -12));

        var light = graph.Add("蓝色点光源", transform: TransformComponent.FromFree(
            new FreeTransform(new Double3(0, 7, 0), Quaternion.Identity)));
        light.Components.Add(SceneComponentTypes.CreateLight(LightComponentData.Default with
        {
            Shape = LightShape.Point, ColorR = .18F, ColorG = .48F, ColorB = 1F,
            Intensity = 220F, Range = 30F, CastShadows = true,
        }));
        var cameraPosition = new Vector3(33F, 27F, 38F);
        var forward = Vector3.Normalize(new Vector3(0F, 5F, 0F) - cameraPosition);
        var camera = graph.Add("示例相机", transform: TransformComponent.FromFree(new FreeTransform(
            new Double3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z),
            ProjectTemplateCatalog.CreateCameraRotation(forward, Vector3.UnitY))));
        camera.Components.Add(SceneComponentTypes.CreateCamera());
        SceneSerializer.Save(Path.Combine(project.AssetsPath, "Scenes", FileName), scene);
    }

    private static void AddBox(ProjectHandle project, SceneGraph graph, string name,
        VoxelCoord64 min, VoxelCoord64 max)
    {
        var world = new VoxelWorld();
        var value = new VoxelValue(new Rgba32(190, 195, 202));
        for (var z = min.Z; z <= max.Z; z++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var x = min.X; x <= max.X; x++) world.Set(new VoxelCoord64(x, y, z), value);
        var relative = $"Assets/Scenes/示例{name}.mvmodel";
        LargeWorldFormat.Write(Path.Combine(project.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)), world);
        var model = graph.Add(name, transform: TransformComponent.FromGrid(new GridTransform(min, OrthogonalRotation.Identity)));
        model.Components.Add(SceneComponentTypes.CreateVoxelModel(relative, world.OccupiedVoxelCount, "Empty", name));
    }
}
