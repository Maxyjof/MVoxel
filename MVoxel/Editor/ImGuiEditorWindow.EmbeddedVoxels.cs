using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private void AddVoxelAssetReferenceToScene(string relativePath)
    {
        if (_project is null || _sceneDocument is null) return;
        if (IsAssetStage) { SetStatus("请先返回场景，再添加体素资产引用"); return; }
        if (_worldLoadFailed) { SetStatus("当前场景数据尚无法安全添加模型引用"); return; }
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !(path.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) ||
                  path.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("体素数据路径无效");
            if (LargeWorldFormat.IsObjectDocument(path))
            {
                var document = LargeWorldFormat.ReadObjects(path);
                if (document.Objects.Select(segment => segment.ObjectId).Distinct(StringComparer.Ordinal).Count() != document.Objects.Count)
                    throw new InvalidDataException("体素资产包含重复的对象标识");
                foreach (var segment in document.Objects)
                {
                    var part = CreateHierarchyObject(string.IsNullOrWhiteSpace(segment.Name) ? "体素对象" : segment.Name, null, true);
                    part.Transform = TransformComponent.FromGrid(new GridTransform(segment.Origin, OrthogonalRotation.Identity));
                    var partComponent = part.Components.First(SceneComponentTypes.IsVoxelModel);
                    var partData = SceneComponentTypes.ReadVoxelModel(partComponent);
                    SceneComponentTypes.WriteVoxelModel(partComponent, partData with
                    {
                        Model = "Assets/" + relativePath.Replace('\\', '/'), SourceSegment = segment.ObjectId, SourceOrigin = segment.Origin,
                        VoxelCount = segment.World.OccupiedVoxelCount,
                    });
                    _objectVoxels.SetWorld(part.Id, segment.World);
                }
                RebuildObjectVoxelWorld();
                SetStatus($"已添加 {document.Objects.Count} 个体素资产对象引用");
                return;
            }
            // 在修改场景前完成读取，损坏或缺失的资产不会留下空对象。
            var world = LargeWorldFormat.Read(path);
            var item = CreateHierarchyObject(Path.GetFileNameWithoutExtension(path), null, true);
            var component = item.Components.First(SceneComponentTypes.IsVoxelModel);
            var data = SceneComponentTypes.ReadVoxelModel(component);
            SceneComponentTypes.WriteVoxelModel(component, data with
            { Model = "Assets/" + relativePath.Replace('\\', '/'), VoxelCount = world.OccupiedVoxelCount });
            _objectVoxels.SetWorld(item.Id, world);
            RebuildObjectVoxelWorld();
            SetStatus($"已添加体素资产引用：{item.Name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException)
        { SetStatus($"添加体素资产引用失败：{exception.Message}"); }
    }

    private void ApplyVoxelModelReference(SceneObject sceneObject, ComponentRecord component, string reference)
    {
        if (_project is null || _sceneDocument is null) return;
        var current = SceneComponentTypes.ReadVoxelModel(component);
        if (string.Equals(current.Model, reference, StringComparison.OrdinalIgnoreCase)) return;
        if (_worldLoadFailed)
        {
            SetStatus("当前场景数据尚无法安全切换模型引用");
            return;
        }
        try
        {
            var loaded = new VoxelWorld();
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath));
                var path = Path.GetFullPath(Path.Combine(_project.RootPath,
                    reference.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !(path.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("体素数据路径无效");
                loaded = LargeWorldFormat.Read(path, GetSceneModelOrigin(sceneObject));
            }
            var world = _objectVoxels.GetOrCreate(sceneObject.Id);
            var before = component.Data.Clone();
            var beforeScale = (sceneObject.Transform.ScaleX, sceneObject.Transform.ScaleY, sceneObject.Transform.ScaleZ);
            var changes = world.EnumerateOccupied().ToDictionary(cell => cell.Coordinate, _ => default(VoxelValue));
            foreach (var cell in loaded.EnumerateOccupied()) changes[cell.Coordinate] = cell.Value;
            _voxelHistory.Execute(new VoxelStrokeCommand("更换体素数据", world,
                changes.Select(cell => (cell.Key, cell.Value)), forward =>
                {
                    var active = sceneObject.Components.First(SceneComponentTypes.IsVoxelModel);
                    active.Data = before.Clone();
                    if (forward)
                    {
                        SceneComponentTypes.WriteVoxelModel(active, current with
                        { Model = reference, SourceSegment = string.Empty, SourceOrigin = null, VoxelCount = loaded.OccupiedVoxelCount, MirrorX = false });
                        EmbeddedVoxelData.Remove(active);
                    }
                    //新模型按 1x 载入，重置缩放标记并丢弃旧基准，避免下次缩放拿旧基准替换掉刚载入的模型
                    var target = sceneObject.Transform;
                    target.ScaleX = forward ? 1 : beforeScale.ScaleX;
                    target.ScaleY = forward ? 1 : beforeScale.ScaleY;
                    target.ScaleZ = forward ? 1 : beforeScale.ScaleZ;
                    InvalidateScaleBase(sceneObject.Id);
                    RebuildObjectVoxelWorld();
                }));
            ResetAfterVoxelTransform();
            MarkSceneDocumentDirty();
            SetStatus("已更新模型的体素数据引用");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException)
        {
            SetStatus($"加载体素数据失败：{exception.Message}");
        }
    }

    private void ExtractEmbeddedVoxelData(SceneObject sceneObject, ComponentRecord component)
    {
        if (_project is null || _sceneDocument is null) return;
        if (_worldLoadFailed)
        {
            SetStatus("当前场景数据尚无法安全提取");
            return;
        }
        var objectWorld = CaptureObjectVoxels(sceneObject);
        if (objectWorld.OccupiedVoxelCount == 0)
        {
            SetStatus("没有可提取的体素");
            return;
        }
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(sceneObject.Name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        var path = NativeDialogs.SaveFile("提取内嵌体素数据", "MVoxel模型\0*.mvmodel\0", "mvmodel",
            string.IsNullOrWhiteSpace(name) ? "体素模型.mvmodel" : name + ".mvmodel");
        if (path is null) return;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.AssetsPath));
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !target.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("请选择项目Assets内的.mvmodel文件");
                return;
            }
            var targetReference = ToAssetRelativePath(_project, target);
            if (_sceneDocument.Objects.Where(item => item.Id != sceneObject.Id)
                .SelectMany(item => item.Components).Where(SceneComponentTypes.IsVoxelModel)
                .Any(item => string.Equals(SceneComponentTypes.ReadVoxelModel(item).Model,
                    targetReference, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("目标文件已被其他模型引用，请选择其他文件名");
                return;
            }
            LargeWorldFormat.Write(target, objectWorld);
            AssetMetaStore.EnsureMeta(target);
            var data = SceneComponentTypes.ReadVoxelModel(component);
            SceneComponentTypes.WriteVoxelModel(component, data with
            {
                Model = targetReference, SourceSegment = string.Empty, SourceOrigin = null, VoxelCount = objectWorld.OccupiedVoxelCount,
            });
            EmbeddedVoxelData.Remove(component);
            AlignModelOriginToBounds(sceneObject, objectWorld);
            _voxelModelReferenceCacheRoot = null;
            MarkSceneDocumentDirty();
            SetStatus("已提取体素数据，请保存项目以保存引用");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException)
        {
            SetStatus($"提取失败：{exception.Message}");
        }
    }
}
