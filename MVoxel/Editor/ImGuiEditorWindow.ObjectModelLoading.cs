using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static List<(StableId ObjectId, ModelLoadKey Key)> GetObjectModelLoadKeys(
        ProjectHandle project, SceneDocument? scene)
    {
        var result = new List<(StableId, ModelLoadKey)>();
        if (scene is null) return result;
        var assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.AssetsPath)) + Path.DirectorySeparatorChar;
        foreach (var sceneObject in scene.Objects)
        foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
        {
            var relativePath = SceneComponentTypes.ReadVoxelModel(component).Model;
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !(relativePath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase) ||
                  relativePath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase))) continue;
            var path = Path.GetFullPath(Path.Combine(project.RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (path.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                result.Add((sceneObject.Id, new ModelLoadKey(path, GetSceneModelOrigin(sceneObject))));
        }
        return result;
    }
}
