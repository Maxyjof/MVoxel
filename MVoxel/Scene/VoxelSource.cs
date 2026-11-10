using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 体素源的种类：不指定、模型资产、预制体资产、组件内嵌快照
/// </summary>
public enum VoxelSourceKind
{
    /// <summary>没有指定来源</summary>
    None,

    /// <summary>项目内的 .mvmodel 模型资产</summary>
    ModelAsset,

    /// <summary>项目内的 .prefab 预制体资产</summary>
    PrefabAsset,

    /// <summary>组件内嵌的体素快照</summary>
    Embedded,
}

/// <summary>
/// 体素源的引用：种类与资产路径，内嵌快照的路径为空
/// </summary>
/// <param name="Kind">来源种类</param>
/// <param name="AssetPath">资产路径，形如 Assets/Worlds/瓷砖.mvmodel</param>
public readonly record struct VoxelSourceRef(VoxelSourceKind Kind, string AssetPath)
{
    /// <summary>未指定来源</summary>
    public static VoxelSourceRef None => new(VoxelSourceKind.None, string.Empty);

    /// <summary>是否未指定来源</summary>
    public bool IsNone => Kind == VoxelSourceKind.None;

    /// <summary>界面显示用的名称</summary>
    public string DisplayName => Kind switch
    {
        VoxelSourceKind.ModelAsset => Path.GetFileNameWithoutExtension(AssetPath),
        VoxelSourceKind.PrefabAsset => Path.GetFileNameWithoutExtension(AssetPath),
        VoxelSourceKind.Embedded => "内嵌快照",
        _ => "未指定",
    };
}

/// <summary>
/// 解析完成的体素源：局部坐标从零开始的世界与包围尺寸
/// </summary>
public sealed class VoxelSource
{
    /// <summary>源体素世界，坐标已经平移到最小角为零</summary>
    public VoxelWorld World { get; init; } = new();

    /// <summary>源的包围尺寸（格）</summary>
    public VoxelCoord64 Size { get; init; }

    /// <summary>解析失败的原因，空字符串表示成功</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>是否有可用内容</summary>
    public bool IsValid => Error.Length == 0 && World.OccupiedVoxelCount > 0;

    /// <summary>
    /// 把任意世界整理成源：复制并按包围盒最小角平移到原点
    /// </summary>
    /// <param name="world">原始世界</param>
    /// <returns>整理后的源，空世界返回带错误信息的对象</returns>
    public static VoxelSource FromWorld(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out var maximum))
            return new VoxelSource { Error = "来源没有体素" };
        var shifted = world.CloneShifted(-minimum.X, -minimum.Y, -minimum.Z);
        return new VoxelSource
        {
            World = shifted,
            Size = new VoxelCoord64(maximum.X - minimum.X + 1, maximum.Y - minimum.Y + 1, maximum.Z - minimum.Z + 1),
        };
    }
}

/// <summary>
/// 体素源的读取与缓存：模型资产、预制体资产都能解析成同一份局部世界
/// </summary>
/// <remarks>
/// 缓存按文件长度与修改时间判断是否过期，和材质读取同一口径。
/// 预制体只取对象上的模型组件与内嵌体素，生成器组件的输出不参与，
/// 否则瓷砖源会依赖另一个程序化结果，重算顺序无法保证
/// </remarks>
public static class VoxelSourceLoader
{
    private static readonly Dictionary<string, CachedSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    private sealed record CachedSource(long Length, long WriteTicks, VoxelSource Source);

    /// <summary>
    /// 按引用解析体素源；失败时返回带原因的对象而不是抛异常
    /// </summary>
    /// <param name="assetsRoot">项目 Assets 目录绝对路径</param>
    /// <param name="source">体素源引用</param>
    /// <returns>解析结果</returns>
    public static VoxelSource Load(string assetsRoot, VoxelSourceRef source)
    {
        if (source.IsNone) return new VoxelSource { Error = "未指定体素源" };
        if (source.Kind == VoxelSourceKind.Embedded)
            return new VoxelSource { Error = "内嵌快照需从组件读取" };
        var fullPath = ResolvePath(assetsRoot, source.AssetPath);
        if (fullPath is null) return new VoxelSource { Error = "体素源路径无效" };
        if (!File.Exists(fullPath)) return new VoxelSource { Error = "体素源文件不存在" };
        try
        {
            var info = new FileInfo(fullPath);
            lock (CacheGate)
            {
                if (Cache.TryGetValue(fullPath, out var cached) && cached.Length == info.Length &&
                    cached.WriteTicks == info.LastWriteTimeUtc.Ticks)
                    return cached.Source;
            }

            var world = source.Kind == VoxelSourceKind.PrefabAsset
                ? LoadPrefab(assetsRoot, fullPath)
                : LoadModel(fullPath);
            var result = VoxelSource.FromWorld(world);
            lock (CacheGate) Cache[fullPath] = new CachedSource(info.Length, info.LastWriteTimeUtc.Ticks, result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or NotSupportedException or ArgumentException)
        {
            return new VoxelSource { Error = $"体素源读取失败：{exception.Message}" };
        }
    }

    /// <summary>
    /// 把资产相对路径解析成绝对路径，兼容带与不带 Assets 前缀两种写法
    /// </summary>
    /// <param name="assetsRoot">Assets 目录绝对路径</param>
    /// <param name="assetPath">资产相对路径</param>
    /// <returns>绝对路径，路径无效时返回 null</returns>
    public static string? ResolvePath(string assetsRoot, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)) return null;
        var root = Path.GetFullPath(assetsRoot);
        var relative = assetPath.Replace('\\', '/').Trim();
        if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            relative = relative["Assets/".Length..];
        var full = Path.GetFullPath(Path.Combine(root, relative));
        // 只允许读取 Assets 目录内的文件，避免组件里的路径越界
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// 清空缓存，资产批量导入或切换项目后调用
    /// </summary>
    public static void ClearCache()
    {
        lock (CacheGate) Cache.Clear();
    }

    private static VoxelWorld LoadModel(string path)
    {
        if (!LargeWorldFormat.IsObjectDocument(path)) return LargeWorldFormat.Read(path);
        var document = LargeWorldFormat.ReadObjects(path);
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var segment in document.Objects)
        foreach (var (coordinate, value) in segment.World.EnumerateOccupied())
            writer.Set(coordinate, value);
        return world;
    }

    private static VoxelWorld LoadPrefab(string assetsRoot, string path)
    {
        var asset = PrefabSerializer.Load(path);
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var item in asset.Objects)
        {
            var offset = item.Transform.VoxelOrigin;
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is null || !component.Enabled) continue;
            var data = SceneComponentTypes.ReadVoxelModel(component);
            VoxelWorld local;
            if (EmbeddedVoxelData.HasData(component))
            {
                local = EmbeddedVoxelData.Read(component);
            }
            else if (!string.IsNullOrWhiteSpace(data.Model))
            {
                var modelPath = ResolvePath(assetsRoot, data.Model);
                if (modelPath is null || !File.Exists(modelPath)) continue;
                local = LoadModel(modelPath);
            }
            else
            {
                continue;
            }

            foreach (var (coordinate, value) in local.EnumerateOccupied())
                writer.Set(coordinate + offset, value);
        }

        return world;
    }
}

/// <summary>
/// 组件内嵌体素源负载的读写，供“从场景对象抓取瓷砖源”使用
/// </summary>
/// <remarks>
/// 与程序化组件的生成体素分开存储，避免重算时互相覆盖。
/// 编码沿用同一套压缩格式，格子带颜色、材质与发光标记
/// </remarks>
internal static class TileSourcePayload
{
    /// <summary>内嵌源在组件 JSON 里的字段名</summary>
    internal const string DataPropertyName = "tileSourceVoxels";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>读取内嵌源</summary>
    /// <param name="component">组件记录</param>
    /// <param name="source">解析结果</param>
    /// <returns>是否存在可用内嵌源</returns>
    internal static bool TryRead(ComponentRecord component, out VoxelSource source)
    {
        source = new VoxelSource { Error = "未指定体素源" };
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(DataPropertyName, out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.String)
            return false;
        if (!VoxelPayloadCodec.TryDecode(cells.GetString() ?? string.Empty, default, out var world, out _))
        {
            source = new VoxelSource { Error = "内嵌体素源损坏" };
            return false;
        }

        source = VoxelSource.FromWorld(world);
        return source.IsValid;
    }

    /// <summary>写出内嵌源，保留组件里的其他字段</summary>
    /// <param name="component">组件记录</param>
    /// <param name="world">源世界，写入前自动平移到原点</param>
    internal static void Write(ComponentRecord component, VoxelWorld world)
    {
        var source = VoxelSource.FromWorld(world);
        var cells = source.World.EnumerateOccupied()
            .OrderBy(item => item.Coordinate.X).ThenBy(item => item.Coordinate.Y).ThenBy(item => item.Coordinate.Z)
            .ToArray();
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged[DataPropertyName] = new JsonObject
        {
            ["version"] = VoxelPayloadCodec.FormatVersion,
            ["count"] = cells.Length,
            ["cells"] = VoxelPayloadCodec.Encode(cells, includeValues: true),
        };
        component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>删除内嵌源</summary>
    /// <param name="component">组件记录</param>
    internal static void Remove(ComponentRecord component)
    {
        if (component.Data.ValueKind != JsonValueKind.Object) return;
        var merged = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        if (merged.Remove(DataPropertyName))
            component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>
    /// 内嵌源内容摘要，用于生成参数指纹
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>64 位摘要，没有负载时为 0</returns>
    internal static ulong ContentHash(ComponentRecord component)
    {
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(DataPropertyName, out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.String)
            return 0UL;
        return VoxelPayloadCodec.StableHash(cells.GetString());
    }
}
