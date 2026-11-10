using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 瓷砖铺设组件的类型标识、创建读取与生成入口
/// </summary>
/// <remarks>
/// 与树生成器、曲线渲染器同属程序化体素组件：体素由参数生成，不能进入绘制模式，
/// 需要用户手动点“实例化”才变成可编辑的模型
/// </remarks>
public static partial class SceneComponentTypes
{
    /// <summary>瓷砖铺设组件的稳定类型标识</summary>
    public const string TileLayer = "mvoxel.tilelayer";

    /// <summary>生成结果统计的最近一次值，供检查器与自动化读取</summary>
    internal static TileLayerStats LastTileStats { get; private set; } = TileLayerStats.Empty;

    /// <summary>
    /// 判断组件记录是否为瓷砖铺设组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为瓷砖组件</returns>
    public static bool IsTileLayer(ComponentRecord? component) =>
        component is not null && string.Equals(component.TypeId, TileLayer, StringComparison.Ordinal);

    /// <summary>创建瓷砖铺设组件记录</summary>
    /// <param name="data">瓷砖参数</param>
    /// <returns>新的组件记录</returns>
    public static ComponentRecord CreateTileLayer(TileLayerComponentData data)
    {
        var component = new ComponentRecord
        {
            TypeId = TileLayer,
            SchemaVersion = 1,
            Data = JsonSerializer.SerializeToElement(new JsonObject()),
        };
        TileLayerJson.Write(component, data);
        return component;
    }

    /// <summary>读取瓷砖组件参数，缺失或损坏字段回落到默认值</summary>
    /// <param name="component">组件记录</param>
    /// <returns>规范化后的瓷砖参数</returns>
    public static TileLayerComponentData ReadTileLayer(ComponentRecord component) =>
        TileLayerJson.Read(component.Data);

    /// <summary>写回瓷砖组件参数，保留生成负载与未知字段</summary>
    /// <param name="component">组件记录</param>
    /// <param name="data">新的瓷砖参数</param>
    public static void WriteTileLayer(ComponentRecord component, TileLayerComponentData data)
    {
        if (!IsTileLayer(component)) throw new ArgumentException("组件记录不是瓷砖铺设组件", nameof(component));
        TileLayerJson.Write(component, data);
    }

    /// <summary>读取瓷砖组件用于渲染排序的字段</summary>
    /// <param name="component">瓷砖组件记录</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    public static void ReadTileSorting(ComponentRecord component, out string sortingLayer, out int sortingOrder)
    {
        sortingLayer = "默认";
        sortingOrder = 0;
        if (component.Data.ValueKind != JsonValueKind.Object) return;
        if (component.Data.TryGetProperty("sortingLayer", out var layer) && layer.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(layer.GetString())) sortingLayer = layer.GetString()!;
        if (component.Data.TryGetProperty("sortingOrder", out var order) && order.TryGetInt32(out var value))
            sortingOrder = Math.Clamp(value, -100_000, 100_000);
    }

    /// <summary>写入瓷砖组件的渲染排序字段</summary>
    /// <param name="component">瓷砖组件记录</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    public static void WriteTileSorting(ComponentRecord component, string sortingLayer, int sortingOrder)
    {
        if (!IsTileLayer(component)) throw new ArgumentException("组件记录不是瓷砖铺设组件", nameof(component));
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged["sortingLayer"] = string.IsNullOrWhiteSpace(sortingLayer) ? "默认" : sortingLayer.Trim();
        merged["sortingOrder"] = Math.Clamp(sortingOrder, -100_000, 100_000);
        component.Data = JsonSerializer.SerializeToElement(merged,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = true });
    }

    /// <summary>
    /// 解析瓷砖源：内嵌快照从组件读，资产类走上下文里的加载器
    /// </summary>
    /// <param name="component">瓷砖组件</param>
    /// <param name="data">瓷砖参数</param>
    /// <param name="context">生成上下文</param>
    /// <returns>解析结果，失败时带原因</returns>
    public static VoxelSource ResolveTileSource(ComponentRecord component, TileLayerComponentData data,
        GeneratedVoxelContext? context)
    {
        if (data.Source.Kind == VoxelSourceKind.Embedded)
            return TileSourcePayload.TryRead(component, out var embedded)
                ? embedded
                : new VoxelSource { Error = "未指定体素源" };
        if (context?.ResolveSource is null)
            return new VoxelSource { Error = "无法读取体素源资产" };
        return context.ResolveSource(data.Source);
    }

    /// <summary>
    /// 按当前参数生成瓷砖体素并写回隐藏负载
    /// </summary>
    /// <param name="component">瓷砖组件</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="context">生成上下文，缺少时返回空结果</param>
    /// <returns>局部坐标的体素世界</returns>
    internal static VoxelWorld BuildTileLayerVoxels(ComponentRecord component, int maximumVoxels,
        GeneratedVoxelContext? context)
    {
        var data = ReadTileLayer(component);
        var source = ResolveTileSource(component, data, context);
        var result = TileProjector.Build(component, data, source, context, maximumVoxels);
        LastTileStats = result.Stats;
        var signature = data.GenerationSignature(component);
        GeneratedVoxelData.Write(component, result.World, signature, includeValues: true);
        return result.World;
    }

    /// <summary>
    /// 判断对象上是否有瓷砖组件
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>是否存在启用的瓷砖组件</returns>
    public static bool HasTileLayer(SceneObject? sceneObject) =>
        sceneObject is not null && sceneObject.Components.Any(item => IsTileLayer(item) && item.Enabled);
}
