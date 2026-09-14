using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 场景文档（.mvox 文件的内存形态）：格式版本、稳定 ID、名称与对象列表
/// 序列化走共享原子保存，任何时刻磁盘上都是完整文件或完整备份
/// </summary>
public sealed class SceneDocument
{
    /// <summary>
    /// 文件格式版本，用于校验文档结构
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// 场景的稳定身份，重命名与移动不影响引用
    /// </summary>
    public StableId SceneId { get; set; } = StableId.New();

    /// <summary>
    /// 场景显示名
    /// </summary>
    public string Name { get; set; } = "Untitled Scene";

    /// <summary>
    /// 场景内全部对象，父子关系由每个对象的 ParentId 表达
    /// </summary>
    public List<SceneObject> Objects { get; set; } = new();
}

/// <summary>
/// 场景中的一个对象：稳定 ID、可选父级、可见性与锁定状态、变换与组件集合
/// </summary>
public sealed class SceneObject
{
    /// <summary>
    /// 对象的稳定身份
    /// </summary>
    public StableId Id { get; set; } = StableId.New();

    /// <summary>
    /// 显示名，可随意修改且不影响引用
    /// </summary>
    public string Name { get; set; } = "Object";

    /// <summary>
    /// 父对象 ID，null 表示场景根
    /// </summary>
    public StableId? ParentId { get; set; }

    /// <summary>
    /// 是否可见
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// 是否锁定（防止误选误编辑）
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 对象的变换组件，体素模型默认格变换
    /// </summary>
    public TransformComponent Transform { get; set; } = TransformComponent.FromGrid(GridTransform.Identity);

    /// <summary>
    /// 挂载的组件记录，未知组件按原样保留不丢失
    /// </summary>
    public List<ComponentRecord> Components { get; set; } = new();
}

/// <summary>
/// 单个组件的序列化记录：类型 ID、模式版本与未解析的 JSON 负载
/// 保留 JsonElement 意味着未安装插件的组件数据在保存后依然完整
/// </summary>
public sealed class ComponentRecord
{
    /// <summary>
    /// 组件类型 ID，例如 mvoxel.voxelmodel
    /// </summary>
    public string TypeId { get; set; } = string.Empty;

    /// <summary>
    /// 组件数据模式版本
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 组件的原始 JSON 负载
    /// </summary>
    public JsonElement Data { get; set; }
}

/// <summary>
/// 场景层级操作入口：添加对象、查找与带约束的重新父子化
/// 所有约束（父级存在、禁止自环、禁止循环、格/自由兼容）都在这里集中校验
/// </summary>
public sealed class SceneGraph
{
    private readonly SceneDocument _document;

    /// <summary>
    /// 包装一个场景文档
    /// </summary>
    /// <param name="document">目标文档</param>
    public SceneGraph(SceneDocument document)
    {
        _document = document;
    }

    /// <summary>
    /// 文档中的全部对象
    /// </summary>
    public IReadOnlyList<SceneObject> Objects => _document.Objects;

    /// <summary>
    /// 新建对象并加入场景
    /// </summary>
    /// <param name="name">显示名，空白时使用 Object</param>
    /// <param name="parentId">可选父级，必须已存在</param>
    /// <param name="transform">可选初始变换</param>
    /// <returns>新对象</returns>
    public SceneObject Add(string name, StableId? parentId = null, TransformComponent? transform = null)
    {
        if (parentId is not null && Find(parentId.Value) is null)
        {
            throw new InvalidOperationException($"Parent does not exist: {parentId.Value}");
        }

        var sceneObject = new SceneObject
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Object" : name,
            ParentId = parentId,
            Transform = transform ?? TransformComponent.FromGrid(GridTransform.Identity),
        };
        _document.Objects.Add(sceneObject);
        return sceneObject;
    }

    /// <summary>
    /// 按稳定 ID 查找对象
    /// </summary>
    /// <param name="id">对象 ID</param>
    /// <returns>找到的对象或 null</returns>
    public SceneObject? Find(StableId id) => _document.Objects.FirstOrDefault(item => item.Id == id);

    /// <summary>
    /// 重新设置父级，拒绝自环、循环层级与破坏格/自由兼容关系的组合
    /// </summary>
    /// <param name="childId">要移动的对象</param>
    /// <param name="parentId">新父级，null 表示移到场景根</param>
    public void SetParent(StableId childId, StableId? parentId)
    {
        var child = Find(childId) ?? throw new KeyNotFoundException($"Object does not exist: {childId}");
        if (parentId == childId)
        {
            throw new InvalidOperationException("An object cannot be its own parent.");
        }

        SceneObject? parent = null;
        if (parentId is not null)
        {
            parent = Find(parentId.Value) ?? throw new KeyNotFoundException($"Parent does not exist: {parentId}");
            if (!parent.Transform.CanBeParentOf(child.Transform))
            {
                throw new InvalidOperationException("Grid transform objects can only contain free-transform children.");
            }

            for (var current = parent; current is not null; current = current.ParentId is null ? null : Find(current.ParentId.Value))
            {
                if (current.Id == childId)
                {
                    throw new InvalidOperationException("Parenting would create a hierarchy cycle.");
                }
            }
        }

        child.ParentId = parentId;
    }
}

/// <summary>
/// 场景文档的 JSON 序列化入口，保存走原子写入并保留上一版备份
/// </summary>
public static class SceneSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 原子保存场景文档
    /// </summary>
    /// <param name="path">.mvox 文件路径</param>
    /// <param name="scene">要保存的文档</param>
    public static void Save(string path, SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(scene, Options));
    }

    /// <summary>
    /// 读取场景文档
    /// </summary>
    /// <param name="path">.mvox 文件路径</param>
    /// <returns>解析后的文档</returns>
    public static SceneDocument Load(string path)
    {
        var scene = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(path), Options);
        return scene ?? throw new InvalidDataException($"Invalid scene: {path}");
    }
}
