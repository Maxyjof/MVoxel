using System.Text.Json;
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

    /// <summary>
    /// 图层名列表，索引即图层号；第 0 层为默认层不可删除，上限 32 层与遮罩位宽一致
    /// </summary>
    public List<string> LayerNames { get; set; } = ["默认"];

    /// <summary>按名称记录被隐藏的图层；使用名称保证图层重排时状态不串位。</summary>
    public List<string> HiddenLayerNames { get; set; } = [];

    /// <summary>排序图层由低到高排列，仅用于重叠体素的显示优先级</summary>
    public List<string> SortingLayerNames { get; set; } = ["默认"];

    /// <summary>
    /// 新增排序图层，列表后面的图层具有更高优先级
    /// </summary>
    public bool AddSortingLayer(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || SortingLayerNames.Contains(trimmed, StringComparer.Ordinal)) return false;
        SortingLayerNames.Add(trimmed);
        return true;
    }

    /// <summary>
    /// 重命名排序图层并更新模型组件引用
    /// </summary>
    public bool RenameSortingLayer(int index, string name)
    {
        var trimmed = name.Trim();
        if (index < 0 || index >= SortingLayerNames.Count || trimmed.Length == 0 ||
            SortingLayerNames[index] == "默认" ||
            SortingLayerNames.Where((_, other) => other != index).Contains(trimmed, StringComparer.Ordinal))
            return false;
        var oldName = SortingLayerNames[index];
        SortingLayerNames[index] = trimmed;
        RemapSortingLayer(oldName, trimmed);
        return true;
    }

    /// <summary>
    /// 调整排序图层优先级，不改变模型组件的图层引用
    /// </summary>
    public bool MoveSortingLayer(int index, int delta)
    {
        if (delta is not (-1 or 1)) return false;
        var target = index + delta;
        if (index < 0 || index >= SortingLayerNames.Count || target < 0 || target >= SortingLayerNames.Count)
            return false;
        (SortingLayerNames[index], SortingLayerNames[target]) = (SortingLayerNames[target], SortingLayerNames[index]);
        return true;
    }

    /// <summary>
    /// 删除排序图层，原有模型回到默认排序图层
    /// </summary>
    public bool RemoveSortingLayer(int index)
    {
        if (index < 0 || index >= SortingLayerNames.Count || SortingLayerNames[index] == "默认") return false;
        var removed = SortingLayerNames[index];
        SortingLayerNames.RemoveAt(index);
        RemapSortingLayer(removed, "默认");
        return true;
    }

    private void RemapSortingLayer(string from, string to)
    {
        foreach (var component in Objects.SelectMany(item => item.Components).Where(SceneComponentTypes.IsVoxelModel))
        {
            var data = SceneComponentTypes.ReadVoxelModel(component);
            if (string.Equals(data.SortingLayer, from, StringComparison.Ordinal))
                SceneComponentTypes.WriteVoxelModel(component, data with { SortingLayer = to });
        }
    }

    public bool IsLayerVisible(int index) => index >= 0 && index < LayerNames.Count &&
        !HiddenLayerNames.Contains(LayerNames[index], StringComparer.Ordinal);

    public bool SetLayerVisible(int index, bool visible)
    {
        if (index < 0 || index >= LayerNames.Count) return false;
        var name = LayerNames[index];
        if (visible) HiddenLayerNames.RemoveAll(item => string.Equals(item, name, StringComparison.Ordinal));
        else if (!HiddenLayerNames.Contains(name, StringComparer.Ordinal)) HiddenLayerNames.Add(name);
        return true;
    }

    /// <summary>
    /// 图层总数上限
    /// </summary>
    public const int MaximumLayerCount = 32;

    /// <summary>
    /// 新增图层
    /// </summary>
    /// <param name="name">图层名，空白或重名会被拒绝</param>
    /// <returns>新图层的索引，失败返回 -1</returns>
    public int AddLayer(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || LayerNames.Count >= MaximumLayerCount ||
            LayerNames.Any(item => string.Equals(item, trimmed, StringComparison.Ordinal)))
        {
            return -1;
        }

        LayerNames.Add(trimmed);
        return LayerNames.Count - 1;
    }

    /// <summary>
    /// 重命名图层
    /// </summary>
    /// <param name="index">图层索引</param>
    /// <param name="name">新名称，空白或与其他层重名会被拒绝</param>
    /// <returns>是否成功</returns>
    public bool RenameLayer(int index, string name)
    {
        var trimmed = name.Trim();
        if (index < 0 || index >= LayerNames.Count || trimmed.Length == 0 ||
            LayerNames.Where((_, other) => other != index)
                .Any(item => string.Equals(item, trimmed, StringComparison.Ordinal)))
        {
            return false;
        }

        var oldName = LayerNames[index];
        LayerNames[index] = trimmed;
        for (var hidden = 0; hidden < HiddenLayerNames.Count; hidden++)
            if (string.Equals(HiddenLayerNames[hidden], oldName, StringComparison.Ordinal))
                HiddenLayerNames[hidden] = trimmed;
        return true;
    }

    /// <summary>
    /// 移动图层顺序，对象引用的是索引，移动时同步迁移对象的图层归属
    /// </summary>
    /// <param name="index">待移动图层</param>
    /// <param name="delta">位移，-1 上移、1 下移</param>
    /// <returns>是否成功</returns>
    public bool MoveLayer(int index, int delta)
    {
        if (delta is not (-1 or 1)) return false;
        var target = index + delta;
        if (index <= 0 || index >= LayerNames.Count || target < 1 || target >= LayerNames.Count)
        {
            return false;
        }

        var name = LayerNames[index];
        LayerNames.RemoveAt(index);
        LayerNames.Insert(target, name);
        foreach (var sceneObject in Objects)
        {
            if (sceneObject.LayerIndex == index)
            {
                sceneObject.LayerIndex = target;
            }
            else if (delta < 0 && sceneObject.LayerIndex == index - 1)
            {
                sceneObject.LayerIndex = index;
            }
            else if (delta > 0 && sceneObject.LayerIndex == index + 1)
            {
                sceneObject.LayerIndex = index;
            }
        }

        RemapCameraMasks(index, target, false);
        return true;
    }

    /// <summary>
    /// 删除图层，第 0 层不可删除；该层上的对象回到第 0 层，其后图层索引整体前移
    /// </summary>
    /// <param name="index">待删除图层</param>
    /// <returns>是否成功</returns>
    public bool RemoveLayer(int index)
    {
        if (index <= 0 || index >= LayerNames.Count)
        {
            return false;
        }

        var removedName = LayerNames[index];
        LayerNames.RemoveAt(index);
        HiddenLayerNames.RemoveAll(item => string.Equals(item, removedName, StringComparison.Ordinal));
        foreach (var sceneObject in Objects)
        {
            if (sceneObject.LayerIndex == index)
            {
                sceneObject.LayerIndex = 0;
            }
            else if (sceneObject.LayerIndex > index)
            {
                sceneObject.LayerIndex--;
            }
        }

        RemapCameraMasks(index, 0, true);
        return true;
    }

    private void RemapCameraMasks(int index, int target, bool removed)
    {
        foreach (var component in Objects.SelectMany(item => item.Components).Where(SceneComponentTypes.IsCamera))
        {
            var data = SceneComponentTypes.ReadCamera(component);
            var mask = data.CullingMask;
            uint updated = 0;
            for (var bit = 0; bit < MaximumLayerCount; bit++)
            {
                if ((mask & (1u << bit)) == 0) continue;
                if (removed && bit == index) continue;
                var destination = removed ? (bit > index ? bit - 1 : bit) :
                    bit == index ? target : bit == target ? index : bit;
                updated |= 1u << destination;
            }
            //保留未分配图层的默认可见性
            if (removed && (mask & 0x80000000u) != 0) updated |= 0x80000000u;
            SceneComponentTypes.WriteCamera(component, data with { CullingMask = updated });
        }
    }

    /// <summary>
    /// 统计每个图层上的对象数量，供图层管理界面显示占用
    /// </summary>
    /// <returns>与 LayerNames 等长的计数数组</returns>
    public int[] LayerUsageCounts()
    {
        var counts = new int[LayerNames.Count];
        foreach (var sceneObject in Objects)
        {
            var layer = Math.Clamp(sceneObject.LayerIndex, 0, counts.Length - 1);
            counts[layer]++;
        }

        return counts;
    }
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
    /// 所属图层索引，指向场景 LayerNames；相机按遮罩决定渲染哪些图层
    /// </summary>
    public int LayerIndex { get; set; }

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
                errors.Add($"对象“{sceneObject.Name}”的变换组件不在首位或数量不正确");
            if (sceneObject.Components.Count(SceneComponentTypes.IsCamera) > 1)
                errors.Add($"对象“{sceneObject.Name}”包含重复的相机组件");
            if (sceneObject.Components.Count(SceneComponentTypes.IsLight) > 1)
                errors.Add($"对象“{sceneObject.Name}”包含重复的光照组件");
            if (sceneObject.Components.Count(SceneComponentTypes.IsProbeVolume) > 1)
                errors.Add($"对象“{sceneObject.Name}”包含重复的自动探针组件");
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
    /// 读取场景文档：先校验当前格式版本，再反序列化并恢复对象不变量
    /// </summary>
    /// <param name="path">.mvox 文件路径</param>
    /// <returns>解析后的文档</returns>
    public static SceneDocument Load(string path)
    {
        return LoadFromJson(File.ReadAllText(path), path);
    }

    /// <summary>
    /// 从当前版本的 JSON 文本读取场景文档并恢复对象不变量
    /// </summary>
    /// <param name="json">场景 JSON 文本</param>
    /// <returns>解析后的文档</returns>
    public static SceneDocument LoadFromJson(string json)
    {
        return LoadFromJson(json, "场景 JSON");
    }

    private static SceneDocument LoadFromJson(string json, string path)
    {
        ValidateCurrentFormatVersion(json, path);
        var scene = JsonSerializer.Deserialize<SceneDocument>(json, Options);
        if (scene is null) throw new InvalidDataException("场景 JSON 不是合法文档");

        // 恢复对象不变量：补齐空集合、移除空引用、重建变换组件，
        // 保证内存中的场景图始终可被渲染与层级面板安全访问。
        scene.Objects ??= new List<SceneObject>();
        scene.Objects.RemoveAll(sceneObject => sceneObject is null);
        // 图层不变量：缺失补默认、超上限截断、名称去空白；对象图层索引夹取
        scene.LayerNames ??= new List<string>();
        while (scene.LayerNames.Count == 0)
        {
            scene.LayerNames.Add("默认");
        }

        if (scene.LayerNames.Count > SceneDocument.MaximumLayerCount)
        {
            scene.LayerNames.RemoveRange(SceneDocument.MaximumLayerCount,
                scene.LayerNames.Count - SceneDocument.MaximumLayerCount);
        }

        for (var nameIndex = 0; nameIndex < scene.LayerNames.Count; nameIndex++)
        {
            var layerName = (scene.LayerNames[nameIndex] ?? string.Empty).Trim();
            scene.LayerNames[nameIndex] = layerName.Length == 0 ? $"图层 {nameIndex}" : layerName;
        }
        scene.SortingLayerNames = (scene.SortingLayerNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (!scene.SortingLayerNames.Contains("默认", StringComparer.Ordinal))
            scene.SortingLayerNames.Insert(0, "默认");
        scene.HiddenLayerNames ??= [];
        var knownLayerNames = scene.LayerNames.ToHashSet(StringComparer.Ordinal);
        scene.HiddenLayerNames = scene.HiddenLayerNames
            .Where(name => !string.IsNullOrWhiteSpace(name) && knownLayerNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var sceneObject in scene.Objects)
        {
            sceneObject.LayerIndex = Math.Clamp(sceneObject.LayerIndex, 0, scene.LayerNames.Count - 1);
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
    /// 校验场景文件必须明确声明当前格式版本
    /// </summary>
    /// <param name="json">原始文件文本</param>
    /// <param name="path">文件路径，用于诊断信息</param>
    private static void ValidateCurrentFormatVersion(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("formatVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) || version != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"场景 {path} 的格式版本不受支持，当前只接受版本 {CurrentFormatVersion}，请在当前版本重新创建");
        }
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
