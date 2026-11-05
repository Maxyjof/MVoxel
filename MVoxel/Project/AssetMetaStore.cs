using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 资产 GUID 边车文件（.meta）的读写与项目级目录扫描。
/// 每个资产文件旁保存同名 .meta 记录稳定 GUID：重命名或移动资产时 .meta 随之移动，
/// 引用与依赖追踪因此不随路径变化失效
/// 外部文件一律不可信：损坏、越界或伪造的 .meta 一律当作丢失处理并重建
/// </summary>
public static class AssetMetaStore
{
    /// <summary>
    /// 边车文件扩展名
    /// </summary>
    public const string MetaExtension = ".meta";

    /// <summary>
    /// 当前边车文件格式版本
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// 纳入 GUID 管理的资产扩展名（不含 Exports 受管导出分类）
    /// </summary>
    public static readonly IReadOnlySet<string> AssetExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mvmodel", ".mvworld", ".mvox", ".prefab", ".material", ".mvgenerator", ".palette", ".pal",
            ".mvrenderpreset", ".mvrender", ".hdr", ".exr", ".vox", ".qb", ".schem",
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// GUID 到相对路径的映射与反向映射
    /// </summary>
    /// <param name="ByGuid">GUID → 资产相对路径（正斜杠分隔）</param>
    /// <param name="ByPath">资产相对路径 → GUID</param>
    /// <param name="Regenerated">扫描时因丢失或重复而重建 GUID 的资产数</param>
    public sealed record Catalog(
        IReadOnlyDictionary<string, string> ByGuid,
        IReadOnlyDictionary<string, Guid> ByPath,
        int Regenerated);

    /// <summary>
    /// 资产文件对应的边车文件路径
    /// </summary>
    public static string MetaPathFor(string assetAbsolutePath) => assetAbsolutePath + MetaExtension;

    /// <summary>
    /// 判断相对路径是否为纳入管理的资产文件（排除 .meta 自身与 Exports 导出分类）
    /// </summary>
    /// <param name="relativePath">相对 Assets 的路径（正斜杠分隔）</param>
    /// <returns>是否为受管资产</returns>
    public static bool IsManagedAsset(string relativePath)
    {
        if (relativePath.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (relativePath.StartsWith("Exports/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return relativePath.EndsWith(".mvtimeline.json", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".mvanim.json", StringComparison.OrdinalIgnoreCase) ||
            AssetExtensions.Contains(Path.GetExtension(relativePath));
    }

    /// <summary>
    /// 确保资产存在 .meta 且 GUID 有效，返回其 GUID
    /// 边车丢失或损坏时创建新 GUID（并原子写入），调用方应视为潜在断链
    /// </summary>
    /// <param name="assetAbsolutePath">资产文件的绝对路径</param>
    /// <returns>资产 GUID 与边车是否在本次调用中被重建</returns>
    public static (Guid Guid, bool Recreated) EnsureMeta(string assetAbsolutePath)
    {
        var metaPath = MetaPathFor(assetAbsolutePath);
        if (TryReadMeta(metaPath, out var existing))
        {
            return (existing, false);
        }

        var created = new AssetMetaDocument(FormatVersion, Guid.NewGuid());
        AtomicFile.WriteText(metaPath, JsonSerializer.Serialize(created, JsonOptions));
        return (created.Guid, true);
    }

    /// <summary>
    /// 读取资产 GUID，不创建边车
    /// </summary>
    /// <param name="assetAbsolutePath">资产文件的绝对路径</param>
    /// <param name="guid">读取到的 GUID</param>
    /// <returns>边车存在且合法时为真</returns>
    public static bool TryGetGuid(string assetAbsolutePath, out Guid guid) =>
        TryReadMeta(MetaPathFor(assetAbsolutePath), out guid);

    /// <summary>
    /// 资产移动或重命名时同步移动边车；目标已有边车则保留目标，源边车丢弃
    /// </summary>
    /// <param name="oldAbsolutePath">原资产绝对路径</param>
    /// <param name="newAbsolutePath">新资产绝对路径</param>
    /// <returns>边车是否存在并完成移动</returns>
    public static bool MoveMeta(string oldAbsolutePath, string newAbsolutePath)
    {
        var oldMeta = MetaPathFor(oldAbsolutePath);
        if (!File.Exists(oldMeta))
        {
            return false;
        }

        var newMeta = MetaPathFor(newAbsolutePath);
        if (File.Exists(newMeta))
        {
            return false;
        }

        MVoxel.Core.FileEditHistory.Move(oldMeta, newMeta);
        return true;
    }

    /// <summary>
    /// 强制重写边车为指定 GUID（用于扫描时消除重复 GUID）
    /// </summary>
    /// <param name="assetAbsolutePath">资产文件的绝对路径</param>
    /// <param name="guid">要写入的 GUID</param>
    public static void WriteMeta(string assetAbsolutePath, Guid guid)
    {
        var document = new AssetMetaDocument(FormatVersion, guid);
        AtomicFile.WriteText(MetaPathFor(assetAbsolutePath), JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>
    /// 强制为资产生成并写入新 GUID，返回新 GUID
    /// </summary>
    /// <param name="assetAbsolutePath">资产文件的绝对路径</param>
    /// <returns>新写入的 GUID</returns>
    public static Guid RegenerateMeta(string assetAbsolutePath)
    {
        var guid = Guid.NewGuid();
        WriteMeta(assetAbsolutePath, guid);
        return guid;
    }

    /// <summary>
    /// 删除资产时同步删除边车
    /// </summary>
    /// <param name="assetAbsolutePath">资产文件的绝对路径</param>
    public static void DeleteMeta(string assetAbsolutePath)
    {
        var metaPath = MetaPathFor(assetAbsolutePath);
        if (File.Exists(metaPath))
        {
            MVoxel.Core.FileEditHistory.Delete(metaPath);
        }
    }

    /// <summary>
    /// 扫描 Assets 目录构建 GUID 数据库。
    /// 丢失边车的资产补建新 GUID；重复 GUID 保留首个，后续资产重建；
    /// .meta 孤儿（无对应资产）直接忽略，不进入映射
    /// </summary>
    /// <param name="assetsPath">项目 Assets 目录绝对路径</param>
    /// <returns>GUID 数据库快照</returns>
    public static Catalog Scan(string assetsPath)
    {
        if (!Directory.Exists(assetsPath))
        {
            return new Catalog(
                new Dictionary<string, string>(),
                new Dictionary<string, Guid>(), 0);
        }

        var byGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var regenerated = 0;
        var root = Path.GetFullPath(assetsPath);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!IsManagedAsset(relative))
            {
                continue;
            }

            var (guid, recreated) = EnsureMeta(file);
            if (recreated)
            {
                regenerated++;
            }

            var key = guid.ToString("D");
            if (byGuid.ContainsKey(key))
            {
                // 重复 GUID：保留首个资产，当前资产重建新 GUID
                var fixedDocument = new AssetMetaDocument(FormatVersion, Guid.NewGuid());
                AtomicFile.WriteText(MetaPathFor(file), JsonSerializer.Serialize(fixedDocument, JsonOptions));
                guid = fixedDocument.Guid;
                key = guid.ToString("D");
                regenerated++;
            }

            byGuid[key] = relative;
            byPath[relative] = guid;
        }

        return new Catalog(byGuid, byPath, regenerated);
    }

    /// <summary>
    /// 读取并校验边车文件，损坏或版本未知时返回失败
    /// </summary>
    private static bool TryReadMeta(string metaPath, out Guid guid)
    {
        guid = Guid.Empty;
        try
        {
            if (!File.Exists(metaPath))
            {
                return false;
            }

            var document = JsonSerializer.Deserialize<AssetMetaDocument>(File.ReadAllText(metaPath), JsonOptions);
            if (document is null || document.FormatVersion != FormatVersion || document.Guid == Guid.Empty)
            {
                return false;
            }

            guid = document.Guid;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 边车文件内容
    /// </summary>
    private sealed record AssetMetaDocument(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("guid")] Guid Guid);
}
