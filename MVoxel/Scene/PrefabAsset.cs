using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 预制体资产：一棵可复用的对象子树快照，保存为 Assets/Prefabs 下的 .prefab 文件
/// </summary>
public sealed class PrefabAsset
{
    /// <summary>
    /// 资产格式版本
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 预制体身份，每次应用实例内容时更新
    /// </summary>
    public StableId PrefabId { get; set; } = StableId.New();

    /// <summary>
    /// 预制体显示名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 子树快照，父对象在前；对象 ID 就是实例链接表里的来源 ID
    /// </summary>
    public List<SceneObject> Objects { get; set; } = [];

    /// <summary>资产修订号；用于识别场景实例尚未应用的资产变更。</summary>
    public long Revision { get; set; }

    /// <summary>旧版本快照，用于场景未打开时保留实例覆盖的合并基线。</summary>
    public List<PrefabRevision> Revisions { get; set; } = [];

    /// <summary>
    /// 变体基础资产的相对路径；空字符串表示普通预制体而非变体
    /// </summary>
    public string BaseAssetPath { get; set; } = string.Empty;

    /// <summary>
    /// 创建变体时基础资产的 PrefabId，用于判断基础是否已更新
    /// </summary>
    public StableId BasePrefabId { get; set; } = StableId.Empty;

    /// <summary>变体上次继承的基础资产修订号。</summary>
    public long BaseRevision { get; set; }

    /// <summary>
    /// 变体对象 ID → 基础资产来源对象 ID 的映射，覆盖整棵变体子树
    /// </summary>
    public Dictionary<string, string> BaseLinks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 变体对象 ID → 逗号分隔的覆盖类型名单（PrefabOverrides.Kind 名称），
    /// 拉取基础更新时这些类型保留变体值，其余类型继承基础最新内容
    /// </summary>
    public Dictionary<string, string> OverrideKinds { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>预制体某个已保存修订的对象快照。</summary>
public sealed class PrefabRevision
{
    public long Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SceneObject> Objects { get; set; } = [];
}

/// <summary>
/// 预制体资产的 JSON 序列化入口，写入走原子保存
/// </summary>
public static class PrefabSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    static PrefabSerializer()
    {
        Options.Converters.Add(new SceneSerializer.QuaternionJsonConverter());
    }

    /// <summary>
    /// 原子保存预制体资产
    /// </summary>
    /// <param name="path">.prefab 文件路径</param>
    /// <param name="asset">预制体资产</param>
    public static void Save(string path, PrefabAsset asset)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(asset);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(asset, Options));
    }

    /// <summary>
    /// 读取预制体资产
    /// </summary>
    /// <param name="path">.prefab 文件路径</param>
    /// <returns>解析后的资产</returns>
    public static PrefabAsset Load(string path)
    {
        var asset = JsonSerializer.Deserialize<PrefabAsset>(File.ReadAllText(path), Options);
        if (asset is null || asset.Objects.Count == 0)
        {
            throw new InvalidDataException($"Invalid prefab asset: {path}");
        }

        return asset;
    }
}
