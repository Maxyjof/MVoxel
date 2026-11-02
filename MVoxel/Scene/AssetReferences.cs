using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
                if (!SceneComponentTypes.IsVoxelModel(component) ||
                    component.Data.ValueKind != JsonValueKind.Object) continue;

                var data = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
                var componentUpdates = UpdatePath(data, "model", oldPath, newPath);
                if (data["materialSlots"] is JsonArray slots)
                {
                    //只替换引用路径，不删除失效绑定，也不重排或重编号槽位
                    foreach (var slot in slots.OfType<JsonObject>())
                        componentUpdates += UpdatePath(slot, "materialAsset", oldPath, newPath);
                }
                if (componentUpdates > 0)
                {
                    component.Data = JsonSerializer.SerializeToElement(data);
                    updated += componentUpdates;
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
    /// 更新项目 Assets 下场景与预制体文件内的资产路径引用
    /// </summary>
    /// <param name="assetsDirectory">项目 Assets 目录</param>
    /// <param name="oldPath">旧资产相对路径</param>
    /// <param name="newPath">新资产相对路径</param>
    /// <returns>发生修改的文件数</returns>
    public static int UpdateProjectReferences(string assetsDirectory, string oldPath, string newPath)
    {
        if (!Directory.Exists(assetsDirectory)) return 0;
        var changed = 0;
        foreach (var file in Directory.EnumerateFiles(assetsDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".mvox", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
            var content = File.ReadAllText(file);
            var updated = ReplaceQuotedPath(content, oldPath, newPath);
            if (updated == content) continue;
            File.WriteAllText(file, updated);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// 更新目录下全部预制体资产文件内的引用（模型路径与预制体链接路径）
    /// </summary>
    /// <param name="prefabsDirectory">Assets/Prefabs目录</param>
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
            if (!SceneComponentTypes.IsVoxelModel(component) ||
                component.Data.ValueKind != JsonValueKind.Object) continue;
            var data = component.Data;
            if (data.TryGetProperty("model", out var model) &&
                model.ValueKind == JsonValueKind.String && PathEquals(model.GetString(), assetRelativePath))
                return true;
            if (data.TryGetProperty("materialSlots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    if (slot.ValueKind == JsonValueKind.Object &&
                        slot.TryGetProperty("materialAsset", out var material) &&
                        material.ValueKind == JsonValueKind.String && PathEquals(material.GetString(), assetRelativePath))
                        return true;
                }
            }
        }

        return sceneObject.Prefab is { } prefab && PathEquals(prefab.AssetPath, assetRelativePath);
    }

    /// <summary>
    /// 用带引号的整体替换更新文件内的路径引用，避免部分匹配误伤
    /// </summary>
    private static string ReplaceQuotedPath(string content, string oldPath, string newPath)
    {
        return Regex.Replace(content, "\"(?:[^\"\\\\]|\\\\.)*\"", match =>
        {
            var candidate = JsonSerializer.Deserialize<string>(match.Value);
            return PathEquals(candidate, oldPath)
                ? JsonSerializer.Serialize(ReplacementPath(candidate!, newPath))
                : match.Value;
        });
    }

    private static int UpdatePath(JsonObject data, string property, string oldPath, string newPath)
    {
        if (data[property] is not JsonValue value || !value.TryGetValue<string>(out var candidate) ||
            !PathEquals(candidate, oldPath)) return 0;
        data[property] = ReplacementPath(candidate, newPath);
        return 1;
    }

    private static string ReplacementPath(string candidate, string newPath)
    {
        var original = ToCanonical(candidate);
        var replacement = ToCanonical(newPath);
        if (original.TrimStart('/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
            !replacement.TrimStart('/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            replacement = "Assets/" + replacement.TrimStart('/');
        return original.StartsWith('/') ? "/" + replacement.TrimStart('/') : replacement;
    }

    private static bool PathEquals(string? candidate, string assetRelativePath) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        string.Equals(AssetDependencies.NormalizeKey(candidate.TrimStart('/')),
            AssetDependencies.NormalizeKey(assetRelativePath.TrimStart('/')), StringComparison.OrdinalIgnoreCase);

    private static string ToCanonical(string path) => path.Replace('\\', '/').Trim();
}
