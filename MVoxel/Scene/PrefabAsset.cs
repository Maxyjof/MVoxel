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
