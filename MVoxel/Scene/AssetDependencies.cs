using System.Text.RegularExpressions;

namespace MVoxel.Scene;

/// <summary>
/// 项目级资产依赖追踪：扫描场景与预制体资产文件中的带引号路径引用，
/// 构建资产相对路径 → 引用它的文件列表的反向依赖映射
/// 引用以 JSON 字符串形式存储（模型组件 model 字段、预制体链接路径），
/// 因此按带引号的路径子串扫描即可完整覆盖现行引用格式
/// </summary>
public static partial class AssetDependencies
{
    /// <summary>
    /// 匹配 JSON 字符串里以受管资产扩展名结尾的路径（不含引号本身）
    /// </summary>
    [GeneratedRegex("\"([^\"]+\\.(?:mvmodel|mvworld|prefab|material|palette))\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    /// <summary>
    /// 扫描 Assets 下全部场景与预制体文件，构建反向依赖映射
    /// </summary>
    /// <param name="assetsPath">项目 Assets 目录绝对路径</param>
    /// <returns>资产相对路径（小写、正斜杠、可含 Assets/ 前缀的规范化形式）→ 引用文件相对路径列表</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ScanReferencedBy(string assetsPath)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(assetsPath))
        {
            return result;
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

            var referencingPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var references = ScanFile(file);
            if (references.Count == 0)
            {
                continue;
            }

            var key = NormalizeKey(referencingPath);
            foreach (var reference in references)
            {
                if (!result.TryGetValue(reference, out var list))
                {
                    list = new List<string>();
                    result[reference] = list;
                }

                ((List<string>)list).Add(key);
            }
        }

        return result;
    }

    /// <summary>
    /// 扫描单个场景/预制体文件，返回其引用的资产键（规范化小写形式）
    /// </summary>
    /// <param name="filePath">场景或预制体文件绝对路径</param>
    /// <returns>引用的资产键列表（去重）</returns>
    public static IReadOnlyList<string> ScanFile(string filePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>(); // 文件正被外部程序写入或不可读，跳过本次扫描
        }

        var references = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in ReferencePattern().Matches(content))
        {
            references.Add(NormalizeKey(match.Groups[1].Value));
        }

        return references.ToList();
    }

    /// <summary>
    /// 查询某资产被哪些项目文件引用
    /// </summary>
    /// <param name="referencedBy">ScanReferencedBy 的结果</param>
    /// <param name="assetRelativePath">资产相对路径，形如 Worlds/主世界.mvmodel</param>
    /// <returns>引用文件相对路径键列表（规范化小写，去重）</returns>
    public static IReadOnlyList<string> ReferencingFiles(
        IReadOnlyDictionary<string, IReadOnlyList<string>> referencedBy, string assetRelativePath)
    {
        if (referencedBy.TryGetValue(NormalizeKey(assetRelativePath), out var list))
        {
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// 规范化引用路径键：统一斜杠、折叠 JSON 转义产生的重复斜杠与大小写，去掉可选的 Assets/ 前缀
    /// </summary>
    public static string NormalizeKey(string path)
    {
        var value = path.Replace('\\', '/').Trim();
        value = CollapseSlashes().Replace(value, "/");
        if (value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Assets/".Length..];
        }

        return value.ToLowerInvariant();
    }

    [GeneratedRegex("/{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseSlashes();
}
