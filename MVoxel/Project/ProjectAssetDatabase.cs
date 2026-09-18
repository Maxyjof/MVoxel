using System.Text.Json;
using System.Text.Json.Serialization;
using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Project;

/// <summary>
/// 项目资产数据库缓存：把 GUID 目录与依赖扫描结果落盘到项目 Library 目录，
/// 复扫时对未变化的文件复用缓存（按资产、边车或引用文件的长度 + 修改时间判定），
/// 变化、新增或缺失的文件才重新读盘，保证万级资产项目打开资源浏览器无卡顿
/// 缓存只是加速层：损坏一律整体重建，绝不影响正确性；项目文件仍是事实来源
/// </summary>
public static class ProjectAssetDatabase
{
    /// <summary>
    /// 当前缓存文件格式版本
    /// </summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 项目缓存文件默认路径（项目根下 Library/assetdb.json）
    /// </summary>
    public static string DefaultCachePath(string projectRootPath) =>
        Path.Combine(projectRootPath, "Library", "assetdb.json");

    /// <summary>
    /// 增量加载：GUID 目录 + 反向依赖映射，结果与全量扫描一致
    /// </summary>
    /// <param name="assetsPath">项目 Assets 目录绝对路径</param>
    /// <param name="cachePath">缓存文件路径</param>
    /// <returns>GUID 目录与反向依赖映射</returns>
    public static (AssetMetaStore.Catalog Catalog, IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencedBy)
        Load(string assetsPath, string cachePath)
    {
        var cache = ReadCache(cachePath);
        var catalog = LoadCatalog(assetsPath, cache.Assets, out var catalogDirty);
        var (referencedBy, fileEntries) = LoadReferences(assetsPath, cache.References, out var referencesDirty);
        if (catalogDirty || referencesDirty)
        {
            WriteCache(assetsPath, cachePath, catalog, fileEntries);
        }

        return (catalog, referencedBy);
    }

    /// <summary>
    /// 增量构建 GUID 目录：资产与边车均未变化时直接复用缓存 GUID，其余走边车读写
    /// </summary>
    private static AssetMetaStore.Catalog LoadCatalog(string assetsPath,
        List<CachedAsset> cached, out bool dirty)
    {
        dirty = false;
        var byGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var cachedByPath = new Dictionary<string, CachedAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cached)
        {
            cachedByPath[entry.Path] = entry;
        }

        var regenerated = 0;
        if (Directory.Exists(assetsPath))
        {
            var root = Path.GetFullPath(assetsPath);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(AssetMetaStore.MetaExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!AssetMetaStore.IsManagedAsset(relative))
                {
                    continue;
                }

                var assetInfo = new FileInfo(file);
                var metaInfo = new FileInfo(AssetMetaStore.MetaPathFor(file));
                Guid guid;
                if (cachedByPath.TryGetValue(AssetDependencies.NormalizeKey(relative), out var hit) &&
                    hit.Length == assetInfo.Length && hit.ModifiedUtcTicks == assetInfo.LastWriteTimeUtc.Ticks &&
                    hit.MetaLength == (metaInfo.Exists ? metaInfo.Length : 0) &&
                    hit.MetaModifiedUtcTicks == (metaInfo.Exists ? metaInfo.LastWriteTimeUtc.Ticks : 0) &&
                    hit.Guid != Guid.Empty)
                {
                    guid = hit.Guid; // 资产与边车均未变化，直接复用缓存 GUID
                }
                else
                {
                    guid = AssetMetaStore.EnsureMeta(file).Guid;
                    dirty = true;
                }

                if (!byGuid.TryAdd(guid.ToString("D"), relative))
                {
                    // 重复 GUID：保留首个资产，当前资产强制重建
                    guid = AssetMetaStore.RegenerateMeta(file);
                    byGuid[guid.ToString("D")] = relative;
                    regenerated++;
                    dirty = true;
                }

                byPath[relative] = guid;
            }
        }

        return new AssetMetaStore.Catalog(byGuid, byPath, regenerated);
    }

    /// <summary>
    /// 增量构建反向依赖映射：未变化的场景/预制体文件复用缓存引用列表
    /// </summary>
    /// <returns>反向映射（被引用资产 → 引用文件键列表）与按引用文件维度的缓存条目</returns>
    private static (IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencedBy,
        List<CachedReferences> FileEntries) LoadReferences(string assetsPath,
        List<CachedReferences> cached, out bool dirty)
    {
        dirty = false;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var fileEntries = new List<CachedReferences>();
        var cachedByKey = new Dictionary<string, CachedReferences>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cached)
        {
            cachedByKey[entry.Path] = entry;
        }

        if (!Directory.Exists(assetsPath))
        {
            return (result, fileEntries);
        }

        var root = Path.GetFullPath(assetsPath);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".mvox", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(file);
            var key = AssetDependencies.NormalizeKey(
                Path.GetRelativePath(root, file).Replace('\\', '/'));
            IReadOnlyList<string> referenced;
            if (cachedByKey.TryGetValue(key, out var hit) &&
                hit.Length == info.Length && hit.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                referenced = hit.Referenced; // 引用文件未变化，复用缓存
            }
            else
            {
                referenced = AssetDependencies.ScanFile(file);
                dirty = true;
            }

            fileEntries.Add(new CachedReferences
            {
                Path = key,
                Length = info.Length,
                ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                Referenced = referenced.ToList(),
            });
            foreach (var reference in referenced)
            {
                if (!result.TryGetValue(reference, out var list))
                {
                    list = new List<string>();
                    result[reference] = list;
                }

                ((List<string>)list).Add(key);
            }
        }

        return (result, fileEntries);
    }

    /// <summary>
    /// 读取缓存文件；缺失或损坏一律按空缓存重建
    /// </summary>
    private static AssetDatabaseCache ReadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return new AssetDatabaseCache();
            }

            var document = JsonSerializer.Deserialize<AssetDatabaseCache>(File.ReadAllText(cachePath), JsonOptions);
            return document is { FormatVersion: FormatVersion } ? document : new AssetDatabaseCache();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AssetDatabaseCache();
        }
    }

    /// <summary>
    /// 原子写回缓存文件；失败不影响返回结果
    /// </summary>
    private static void WriteCache(string assetsPath, string cachePath, AssetMetaStore.Catalog catalog,
        List<CachedReferences> fileEntries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var document = new AssetDatabaseCache
            {
                FormatVersion = FormatVersion,
                Assets = catalog.ByPath.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => ToCachedAsset(assetsPath, item.Key, item.Value)).ToList(),
                References = fileEntries.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            AtomicFile.WriteText(cachePath, JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // 缓存写入失败只影响下次复扫速度，不报告错误
        }
    }

    /// <summary>
    /// 从文件系统回填资产与边车的长度与时间戳，组装缓存条目
    /// </summary>
    private static CachedAsset ToCachedAsset(string assetsPath, string relative, Guid guid)
    {
        var assetInfo = new FileInfo(Path.Combine(assetsPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var metaInfo = new FileInfo(AssetMetaStore.MetaPathFor(assetInfo.FullName));
        return new CachedAsset
        {
            Path = AssetDependencies.NormalizeKey(relative),
            Guid = guid,
            Length = assetInfo.Exists ? assetInfo.Length : 0,
            ModifiedUtcTicks = assetInfo.Exists ? assetInfo.LastWriteTimeUtc.Ticks : 0,
            MetaLength = metaInfo.Exists ? metaInfo.Length : 0,
            MetaModifiedUtcTicks = metaInfo.Exists ? metaInfo.LastWriteTimeUtc.Ticks : 0,
        };
    }

    /// <summary>
    /// 缓存文件内容
    /// </summary>
    private sealed class AssetDatabaseCache
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("assets")]
        public List<CachedAsset> Assets { get; set; } = [];

        [JsonPropertyName("references")]
        public List<CachedReferences> References { get; set; } = [];
    }

    /// <summary>
    /// 单个资产的缓存条目
    /// </summary>
    private sealed class CachedAsset
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("guid")]
        public Guid Guid { get; set; }

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("modifiedUtcTicks")]
        public long ModifiedUtcTicks { get; set; }

        [JsonPropertyName("metaLength")]
        public long MetaLength { get; set; }

        [JsonPropertyName("metaModifiedUtcTicks")]
        public long MetaModifiedUtcTicks { get; set; }
    }

    /// <summary>
    /// 单个引用文件（场景/预制体）的缓存条目
    /// </summary>
    private sealed class CachedReferences
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("modifiedUtcTicks")]
        public long ModifiedUtcTicks { get; set; }

        [JsonPropertyName("referenced")]
        public List<string> Referenced { get; set; } = [];
    }
}
