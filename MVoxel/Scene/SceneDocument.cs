using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Numerics;
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
    public TransformComponent Transform { get; set; } = TransformComponent.FromFree(FreeTransform.Identity);

    /// <summary>
    /// 挂载的组件记录，未知组件按原样保留不丢失
    /// </summary>
    public List<ComponentRecord> Components { get; set; } =
    [SceneComponentTypes.CreateTransform(TransformComponent.FromFree(FreeTransform.Identity))];

    /// <summary>
    /// 预制体实例标记，null 表示普通对象；子对象为 null，仅实例根携带链接表
    /// </summary>
    public PrefabInstanceData? Prefab { get; set; }
}

/// <summary>
/// 预制体实例的链接信息：记录来源资产、资产内根对象与子树的对象对应关系
/// </summary>
public sealed class PrefabInstanceData
{
    /// <summary>
    /// 来源预制体资产路径，形如 Assets/Prefabs/名称.prefab
    /// </summary>
    public string AssetPath { get; set; } = string.Empty;

    /// <summary>
    /// 资产内的根对象 ID
    /// </summary>
    public StableId SourceId { get; set; }

    /// <summary>
    /// 实例对象 ID 到资产内来源对象 ID 的对应表，覆盖整棵实例子树
    /// </summary>
    public Dictionary<string, string> Links { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 单个组件的序列化记录：类型 ID、模式版本与未解析的 JSON 负载
/// 保留 JsonElement 意味着未安装插件的组件数据在保存后依然完整
/// </summary>
public sealed class ComponentRecord
{
    /// <summary>
    /// 组件是否启用。停用组件仍保留在场景文件中，便于随时恢复。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 提供该组件的插件 ID；内置组件和未知来源组件为空。
    /// </summary>
    public string? OwnerPluginId { get; set; }

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
            Transform = transform ?? TransformComponent.FromFree(FreeTransform.Identity),
        };
        sceneObject.Components.RemoveAll(SceneComponentTypes.IsTransform);
        sceneObject.Components.Insert(0, SceneComponentTypes.CreateTransform(sceneObject.Transform));
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
/// 对已加载场景执行不修改数据的结构校验，供编辑器诊断和 CLI/MCP 共用。
/// </summary>
public static class SceneValidation
{
    public static IReadOnlyList<string> Validate(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var errors = new List<string>();
        var duplicates = scene.Objects.GroupBy(item => item.Id).Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
            errors.Add($"对象 ID 重复：{duplicate.Key}");

        var objects = scene.Objects.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var sceneObject in scene.Objects)
        {
            if (sceneObject.ParentId is { } parentId && !objects.ContainsKey(parentId))
            {
                errors.Add($"对象“{sceneObject.Name}”的父对象不存在：{parentId}");
                continue;
            }

            var visited = new HashSet<StableId>();
            var current = sceneObject;
            while (current.ParentId is { } ancestorId)
            {
                if (!visited.Add(current.Id))
                {
                    errors.Add($"对象“{sceneObject.Name}”所在层级存在循环");
                    break;
                }
                if (!objects.TryGetValue(ancestorId, out current!)) break;
            }

            if (sceneObject.Components.Count(SceneComponentTypes.IsTransform) != 1 ||
                !SceneComponentTypes.IsTransform(sceneObject.Components[0]))
                errors.Add($"对象“{sceneObject.Name}”的坐标组件不在首位或数量不正确");
        }

        return errors;
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

    static SceneSerializer()
    {
        Options.Converters.Add(new QuaternionJsonConverter());
    }

    /// <summary>
    /// 原子保存场景文档
    /// </summary>
    /// <param name="path">.mvox 文件路径</param>
    /// <param name="scene">要保存的文档</param>
    public static void Save(string path, SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        AtomicFile.WriteText(path, ToJson(scene));
    }

    /// <summary>
    /// 序列化为 JSON 文本，供恢复区快照与诊断使用
    /// </summary>
    /// <param name="scene">要序列化的文档</param>
    /// <returns>JSON 文本</returns>
    public static string ToJson(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return JsonSerializer.Serialize(scene, Options);
    }

    /// <summary>复制场景对象，供编辑器命令历史保存精确的前后状态。</summary>
    public static SceneObject CloneObject(SceneObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, Options);
        return JsonSerializer.Deserialize<SceneObject>(json, Options)
            ?? throw new InvalidDataException("无法复制场景对象");
    }

    /// <summary>比较场景对象的可保存状态，不受对象引用身份影响。</summary>
    public static bool ObjectsEqual(SceneObject left, SceneObject right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);
    }

    /// <summary>
    /// 当前写入的场景格式版本
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// 读取场景文档：先校验并升级格式版本，再反序列化并恢复对象不变量
    /// </summary>
    /// <param name="path">.mvox 文件路径</param>
    /// <returns>解析后的文档</returns>
    public static SceneDocument Load(string path)
    {
        return LoadFromJson(Migrate(File.ReadAllText(path), path));
    }

    /// <summary>
    /// 从已升级版本的 JSON 文本读取场景文档并恢复对象不变量
    /// </summary>
    /// <param name="json">场景 JSON 文本</param>
    /// <returns>解析后的文档</returns>
    public static SceneDocument LoadFromJson(string json)
    {
        var scene = JsonSerializer.Deserialize<SceneDocument>(json, Options);
        if (scene is null) throw new InvalidDataException("场景 JSON 不是合法文档");

        // 恢复对象不变量：补齐空集合、移除空引用、重建变换组件，
        // 保证内存中的场景图始终可被渲染与层级面板安全访问。
        scene.Objects ??= new List<SceneObject>();
        scene.Objects.RemoveAll(sceneObject => sceneObject is null);
        foreach (var sceneObject in scene.Objects)
        {
            sceneObject.Transform ??= TransformComponent.FromFree(FreeTransform.Identity);
            sceneObject.Components ??= new List<ComponentRecord>();
            sceneObject.Components.RemoveAll(component => component is null || SceneComponentTypes.IsTransform(component));
            sceneObject.Components.Insert(0, SceneComponentTypes.CreateTransform(sceneObject.Transform));
            foreach (var component in sceneObject.Components.Skip(1))
            {
                if (SceneComponentTypes.BuiltInDefinitions.Any(item => item.TypeId == component.TypeId))
                    component.OwnerPluginId = null;
            }
        }

        return scene;
    }

    /// <summary>
    /// 执行版本迁移链：无 formatVersion 字段的历史文件视为 v0 并补章为 v1，
    /// 高于当前支持的版本拒绝加载；后续每次格式演进在此追加逐级升级步骤
    /// </summary>
    /// <param name="json">原始文件文本</param>
    /// <param name="path">文件路径，用于诊断信息</param>
    /// <returns>升级到当前版本后的 JSON 文本</returns>
    private static string Migrate(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var version = 0;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("formatVersion", out var versionElement) &&
            versionElement.TryGetInt32(out var parsed))
        {
            version = parsed;
        }

        if (version > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"场景 {path} 的格式版本 {version} 新于当前支持的 {CurrentFormatVersion}，请升级 MVoxel");
        }

        if (version != 0) return json;
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException($"场景 {path} 不是合法 JSON 对象");
        root["formatVersion"] = 1;
        return root.ToJsonString(Options);
    }

    /// <summary>
    /// 以四个分量保存 System.Numerics 四元数，避免默认序列化丢失旋转数据
    /// </summary>
    internal sealed class QuaternionJsonConverter : JsonConverter<Quaternion>
    {
        /// <summary>
        /// 读取四元数对象，缺少分量时使用单位旋转
        /// </summary>
        /// <param name="reader">JSON 读取器</param>
        /// <param name="typeToConvert">目标类型</param>
        /// <param name="options">序列化选项</param>
        /// <returns>读取到的四元数</returns>
        public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            return value.TryGetProperty("x", out var x) && x.TryGetSingle(out var xValue) &&
                   value.TryGetProperty("y", out var y) && y.TryGetSingle(out var yValue) &&
                   value.TryGetProperty("z", out var z) && z.TryGetSingle(out var zValue) &&
                   value.TryGetProperty("w", out var w) && w.TryGetSingle(out var wValue)
                ? Quaternion.Normalize(new Quaternion(xValue, yValue, zValue, wValue))
                : Quaternion.Identity;
        }

        /// <summary>
        /// 将四元数的四个分量写入 JSON
        /// </summary>
        /// <param name="writer">JSON 写入器</param>
        /// <param name="value">待写入的四元数</param>
        /// <param name="options">序列化选项</param>
        public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("z", value.Z);
            writer.WriteNumber("w", value.W);
            writer.WriteEndObject();
        }
    }
}
