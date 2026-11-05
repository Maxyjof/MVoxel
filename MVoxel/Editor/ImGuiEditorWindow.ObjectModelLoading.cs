using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static bool SyncReferencedSegmentOrigins(ProjectHandle project, SceneDocument scene, out bool failed)
    {
        failed = false;
        var changed = false;
        var documents = new Dictionary<string, LargeWorldFormat.NativeObjectDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (objectId, key) in GetObjectModelLoadKeys(project, scene))
        {
            if (key.SourceSegment.Length == 0) continue;
            try
            {
                if (!documents.TryGetValue(key.Path, out var document))
                    documents[key.Path] = document = LargeWorldFormat.ReadObjects(key.Path);
                var segment = document.Objects.Single(item => string.Equals(item.ObjectId, key.SourceSegment, StringComparison.Ordinal));
                var item = scene.Objects.First(value => value.Id == objectId);
                var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
                var data = SceneComponentTypes.ReadVoxelModel(component);
                if (data.SourceOrigin == segment.Origin) continue;
                if (data.SourceOrigin is { } oldOrigin)
                {
                    if (item.Transform.Mode != TransformMode.Grid || !item.Transform.Grid.Rotation.Equals(OrthogonalRotation.Identity) ||
                        item.Transform.ScaleX != 1F || item.Transform.ScaleY != 1F || item.Transform.ScaleZ != 1F ||
                        data.ScaleX != 1F || data.ScaleY != 1F || data.ScaleZ != 1F || data.MirrorX)
                        throw new InvalidOperationException($"对象“{item.Name}”带有旋转、缩放或镜像，无法自动同步片段位置");
                    var origin = new VoxelCoord64(checked(key.Origin.X + segment.Origin.X - oldOrigin.X),
                        checked(key.Origin.Y + segment.Origin.Y - oldOrigin.Y), checked(key.Origin.Z + segment.Origin.Z - oldOrigin.Z));
                    item.Transform.SetGrid(new GridTransform(origin, item.Transform.Grid.Rotation));
                    UpdateTransformMarker(item);
                }
                SceneComponentTypes.WriteVoxelModel(component, data with { SourceOrigin = segment.Origin });
                changed = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
            {
                failed = true;
                MVoxelLog.Warn($"体素资产对象位置未同步：{key.Path}：{exception.Message}");
            }
        }
        return changed;
    }

    private static List<(StableId ObjectId, ModelLoadKey Key)> GetObjectModelLoadKeys(
        ProjectHandle project, SceneDocument? scene)
    {
        var result = new List<(StableId, ModelLoadKey)>();
        if (scene is null) return result;
        var assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.AssetsPath)) + Path.DirectorySeparatorChar;
        foreach (var sceneObject in scene.Objects)
        foreach (var component in sceneObject.Components.Where(SceneComponentTypes.IsVoxelModel))
        {
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var relativePath = data.Model;
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !(relativePath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase) ||
                  relativePath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase))) continue;
            var path = Path.GetFullPath(Path.Combine(project.RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (path.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                result.Add((sceneObject.Id, new ModelLoadKey(path, GetSceneModelOrigin(sceneObject), data.SourceSegment)));
        }
        return result;
    }
}
