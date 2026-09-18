using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 资产引用扫描与更新：场景文档中的模型组件与预制体实例链接、
/// 以及预制体资产文件内部的对象数据，都会以资产相对路径引用其他资产。
/// 移动或重命名资产时由编辑器调用本类让引用自动跟随
/// </summary>
public static class AssetReferences
{
    /// <summary>
    /// 引用了指定资产的场景对象
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="assetRelativePath">资产相对路径，形如 Assets/Worlds/主世界.mvmodel</param>
    /// <returns>引用该资产的对象列表</returns>
    public static IReadOnlyList<SceneObject> FindReferencingObjects(SceneDocument document, string assetRelativePath)
    {
        var result = new List<SceneObject>();
        foreach (var sceneObject in document.Objects)
        {
            if (ReferencesAsset(sceneObject, assetRelativePath))
            {
                result.Add(sceneObject);
            }
        }

        return result;
    }

    /// <summary>
    /// 把场景文档内的引用从旧路径改到新路径
    /// </summary>
    /// <param name="document">场景文档</param>
    /// <param name="oldPath">旧资产相对路径</param>
    /// <param name="newPath">新资产相对路径</param>
    /// <returns>更新的引用处数</returns>
    public static int UpdateReferences(SceneDocument document, string oldPath, string newPath)
    {
        var updated = 0;
        foreach (var sceneObject in document.Objects)
        {
            foreach (var component in sceneObject.Components)
            {
                if (SceneComponentTypes.IsVoxelModel(component) &&
                    PathEquals(SceneComponentTypes.ReadVoxelModel(component).Model, oldPath))
                {
                    var data = SceneComponentTypes.ReadVoxelModel(component);
                    SceneComponentTypes.WriteVoxelModel(component, data with { Model = ToCanonical(newPath) });
                    updated++;
                }
            }

            if (sceneObject.Prefab is { } prefab && PathEquals(prefab.AssetPath, oldPath))
            {
                prefab.AssetPath = ToCanonical(newPath);
                updated++;
            }
        }

        return updated;
    }

    /// <summary>
    /// 更新目录下全部预制体资产文件内的引用（模型路径与预制体链接路径）
    /// </summary>
    /// <param name="prefabsDirectory">Assets/Prefabs 目录</param>
    /// <param name="oldPath">旧资产相对路径</param>
    /// <param name="newPath">新资产相对路径</param>
    /// <returns>发生修改的文件数</returns>
    public static int UpdatePrefabReferences(string prefabsDirectory, string oldPath, string newPath)
    {
        var changed = 0;
        if (!Directory.Exists(prefabsDirectory))
        {
            return changed;
        }

        foreach (var file in Directory.EnumerateFiles(prefabsDirectory, "*.prefab", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            var updated = ReplaceQuotedPath(content, oldPath, newPath);
            if (updated != content)
            {
                File.WriteAllText(file, updated);
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// 某对象是否引用了指定资产（模型组件或预制体实例链接）
    /// </summary>
    private static bool ReferencesAsset(SceneObject sceneObject, string assetRelativePath)
    {
        foreach (var component in sceneObject.Components)
        {
            if (SceneComponentTypes.IsVoxelModel(component) &&
                PathEquals(SceneComponentTypes.ReadVoxelModel(component).Model, assetRelativePath))
            {
                return true;
            }
        }

        return sceneObject.Prefab is { } prefab && PathEquals(prefab.AssetPath, assetRelativePath);
    }

    /// <summary>
    /// 用带引号的整体替换更新文件内的路径引用，避免部分匹配误伤
    /// </summary>
    private static string ReplaceQuotedPath(string content, string oldPath, string newPath)
    {
        var canonicalOld = ToCanonical(oldPath);
        var canonicalNew = ToCanonical(newPath);
        return content
            .Replace($"\"{canonicalOld}\"", $"\"{canonicalNew}\"", StringComparison.OrdinalIgnoreCase)
            .Replace($"\"/{canonicalOld}\"", $"\"/{canonicalNew}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? candidate, string assetRelativePath) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        string.Equals(ToCanonical(candidate), ToCanonical(assetRelativePath), StringComparison.OrdinalIgnoreCase);

    private static string ToCanonical(string path) => path.Replace('\\', '/');
}
