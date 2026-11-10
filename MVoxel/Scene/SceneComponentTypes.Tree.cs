using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 树生成器组件与所有程序化组件共用的生成体素入口
/// </summary>
public static partial class SceneComponentTypes
{
    /// <summary>
    /// 树生成器组件的稳定类型标识
    /// </summary>
    public const string TreeGenerator = "mvoxel.treegenerator";

    /// <summary>沿曲线摆放最近一次的失败或降级原因，供检查器与自动化读取</summary>
    internal static string LastScatterMessage { get; private set; } = string.Empty;

    /// <summary>沿曲线摆放最近一次的站点数量</summary>
    internal static int LastScatterStations { get; private set; }

    /// <summary>沿曲线摆放最近一次实际放下的站点数量</summary>
    internal static int LastScatterPlaced { get; private set; }

    /// <summary>
    /// 判断组件记录是否为树生成器组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为树生成器</returns>
    public static bool IsTreeGenerator(ComponentRecord? component) =>
        component is not null && string.Equals(component.TypeId, TreeGenerator, StringComparison.Ordinal);

    /// <summary>
    /// 创建树生成器组件记录
    /// </summary>
    /// <param name="data">树参数</param>
    /// <returns>新的组件记录</returns>
    public static ComponentRecord CreateTreeGenerator(TreeGeneratorComponentData data)
    {
        var component = new ComponentRecord
        {
            TypeId = TreeGenerator,
            SchemaVersion = 1,
            Data = JsonSerializer.SerializeToElement(new JsonObject()),
        };
        TreeGeneratorJson.Write(component, data);
        return component;
    }

    /// <summary>
    /// 读取树生成器参数，缺失或损坏的字段回落到默认值
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>规范化后的树参数</returns>
    public static TreeGeneratorComponentData ReadTreeGenerator(ComponentRecord component) =>
        TreeGeneratorJson.Read(component.Data);

    /// <summary>
    /// 写回树生成器参数，保留生成体素负载与未知字段
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="data">新的树参数</param>
    public static void WriteTreeGenerator(ComponentRecord component, TreeGeneratorComponentData data)
    {
        if (!IsTreeGenerator(component))
            throw new ArgumentException("组件记录不是树生成器组件", nameof(component));
        TreeGeneratorJson.Write(component, data);
    }

    /// <summary>
    /// 读取树组件用于渲染排序的字段
    /// </summary>
    /// <param name="component">树组件记录</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    public static void ReadTreeSorting(ComponentRecord component, out string sortingLayer, out int sortingOrder) =>
        TreeGeneratorJson.ReadSorting(component.Data, out sortingLayer, out sortingOrder);

    /// <summary>
    /// 判断组件是否会自行生成体素
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>曲线渲染器、树生成器或瓷砖铺设时为真</returns>
    public static bool IsGeneratedVoxelComponent(ComponentRecord? component) =>
        IsCurveRenderer(component) || IsTreeGenerator(component) || IsTileLayer(component);

    /// <summary>
    /// 场景对象上第一个启用的程序化体素组件
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>组件记录，没有时返回 null</returns>
    public static ComponentRecord? FindGeneratedVoxelComponent(SceneObject? sceneObject) =>
        sceneObject?.Components.FirstOrDefault(item => item.Enabled && IsGeneratedVoxelComponent(item));

    /// <summary>
    /// 读取组件隐藏的生成体素
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="world">局部坐标的体素世界</param>
    /// <param name="signature">负载写入时的参数指纹</param>
    /// <returns>是否读取成功</returns>
    public static bool TryReadGeneratedVoxels(ComponentRecord component, out VoxelWorld world, out ulong signature)
    {
        var fallback = IsCurveRenderer(component)
            ? ReadCurveRenderer(component).VoxelValue
            : IsTreeGenerator(component)
                ? ReadTreeGenerator(component).BarkValue
                : default;
        if (!IsGeneratedVoxelComponent(component))
        {
            world = new VoxelWorld();
            signature = 0UL;
            return false;
        }

        return GeneratedVoxelData.TryRead(component, fallback, out world, out signature);
    }

    /// <summary>
    /// 按当前参数重新生成体素并写回隐藏负载
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="truncated">是否因达到上限而截断</param>
    /// <returns>局部坐标的体素世界</returns>
    public static VoxelWorld BuildGeneratedVoxels(ComponentRecord component, int maximumVoxels, out bool truncated) =>
        BuildGeneratedVoxels(component, maximumVoxels, null, out truncated);

    /// <summary>
    /// 按当前参数重新生成体素并写回隐藏负载，可传入场景采样上下文
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="context">生成上下文，树与曲线管体不需要</param>
    /// <param name="truncated">是否因达到上限而截断</param>
    /// <returns>局部坐标的体素世界</returns>
    public static VoxelWorld BuildGeneratedVoxels(ComponentRecord component, int maximumVoxels,
        GeneratedVoxelContext? context, out bool truncated)
    {
        truncated = false;
        VoxelWorld world;
        if (IsCurveRenderer(component))
        {
            var curve = ReadCurveRenderer(component);
            if (curve.Output == CurveOutputMode.Scatter)
            {
                var source = curve.ScatterSource.IsNone
                    ? new VoxelSource { Error = "未指定体素源" }
                    : context?.ResolveSource?.Invoke(curve.ScatterSource)
                      ?? new VoxelSource { Error = "无法读取体素源资产" };
                world = SplineScatter.Generate(curve, source, context, maximumVoxels, out truncated,
                    out var message, out var stationCount, out var placedCount);
                LastScatterMessage = message;
                LastScatterStations = stationCount;
                LastScatterPlaced = placedCount;
            }
            else
            {
                world = CurveSpline.Rasterize(curve, maximumVoxels, out truncated);
                LastScatterMessage = string.Empty;
                LastScatterStations = 0;
                LastScatterPlaced = 0;
            }
        }
        else if (IsTreeGenerator(component))
        {
            world = ProceduralTree.Generate(ReadTreeGenerator(component), maximumVoxels, out truncated);
        }
        else if (IsTileLayer(component))
        {
            world = BuildTileLayerVoxels(component, maximumVoxels, context);
        }
        else
        {
            return new VoxelWorld();
        }

        var signature = ComputeGeneratedSignature(component);
        // 曲线散布与树、瓷砖的结果可能逐格不同，必须保存颜色、材质与标记
        GeneratedVoxelData.Write(component, world, signature,
            includeValues: IsTreeGenerator(component) || IsTileLayer(component) ||
                           IsCurveRenderer(component) && ReadCurveRenderer(component).Output == CurveOutputMode.Scatter);
        return world;
    }

    /// <summary>
    /// 直接用已有世界覆盖隐藏负载
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="world">局部坐标的体素世界</param>
    /// <param name="signature">参数指纹</param>
    public static void WriteGeneratedVoxels(ComponentRecord component, VoxelWorld world, ulong signature)
    {
        if (!IsGeneratedVoxelComponent(component))
            throw new ArgumentException("组件记录不生成体素", nameof(component));
        GeneratedVoxelData.Write(component, world, signature,
            includeValues: IsTreeGenerator(component) || IsTileLayer(component) ||
                           IsCurveRenderer(component) && ReadCurveRenderer(component).Output == CurveOutputMode.Scatter);
    }

    /// <summary>
    /// 删除组件里的生成体素负载
    /// </summary>
    /// <param name="component">组件记录</param>
    public static void RemoveGeneratedVoxels(ComponentRecord component)
    {
        if (!IsGeneratedVoxelComponent(component)) return;
        GeneratedVoxelData.Remove(component);
    }

    /// <summary>
    /// 读取生成体素的 Base64 文本长度，检查器用它显示数据体积
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>负载字符数，没有负载时为 0</returns>
    public static int GetGeneratedVoxelPayloadLength(ComponentRecord component) =>
        IsGeneratedVoxelComponent(component) ? GeneratedVoxelData.GetEncodedLength(component) : 0;

    /// <summary>
    /// 读取生成体素负载里记录的参数指纹
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="signature">参数指纹</param>
    /// <returns>是否存在负载与指纹</returns>
    public static bool TryReadGeneratedSignature(ComponentRecord component, out ulong signature)
    {
        signature = 0UL;
        return IsGeneratedVoxelComponent(component) && GeneratedVoxelData.TryReadSignature(component, out signature);
    }

    /// <summary>
    /// 计算组件的生成参数指纹
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>64 位指纹；不是程序化组件时为 0</returns>
    public static ulong ComputeGeneratedSignature(ComponentRecord component)
    {
        if (IsCurveRenderer(component)) return ReadCurveRenderer(component).GenerationSignature();
        if (IsTreeGenerator(component)) return ReadTreeGenerator(component).GenerationSignature();
        if (IsTileLayer(component)) return ReadTileLayer(component).GenerationSignature(component);
        return 0UL;
    }

    /// <summary>
    /// 取用生成体素：优先复用隐藏负载，缺失时才按参数重建
    /// </summary>
    /// <remarks>
    /// 曲线渲染器的参数变化立即生效，负载指纹不一致就重算；
    /// 树生成器只在用户点“生成体素”时替换结果，负载存在就原样使用，即使参数已经变了
    /// </remarks>
    /// <param name="component">组件记录</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="world">局部坐标的体素世界</param>
    /// <param name="regenerated">是否发生了重建</param>
    /// <param name="truncated">重建时是否被截断</param>
    /// <returns>组件是否程序化生成体素</returns>
    public static bool TryPrepareGeneratedVoxels(ComponentRecord component, int maximumVoxels, out VoxelWorld world,
        out bool regenerated, out bool truncated) =>
        TryPrepareGeneratedVoxels(component, maximumVoxels, null, out world, out regenerated, out truncated);

    /// <summary>
    /// 取用生成体素，可传入场景采样上下文
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="context">生成上下文</param>
    /// <param name="world">局部坐标的体素世界</param>
    /// <param name="regenerated">是否发生了重建</param>
    /// <param name="truncated">重建时是否被截断</param>
    /// <returns>组件是否程序化生成体素</returns>
    public static bool TryPrepareGeneratedVoxels(ComponentRecord component, int maximumVoxels,
        GeneratedVoxelContext? context, out VoxelWorld world, out bool regenerated, out bool truncated)
    {
        world = new VoxelWorld();
        regenerated = false;
        truncated = false;
        if (!IsGeneratedVoxelComponent(component)) return false;
        if (TryReadGeneratedVoxels(component, out var stored, out var storedSignature) &&
            !IsTileLayer(component) &&
            (IsTreeGenerator(component) || storedSignature == ComputeGeneratedSignature(component)))
        {
            world = stored;
            return true;
        }

        world = BuildGeneratedVoxels(component, maximumVoxels, context, out truncated);
        regenerated = true;
        return true;
    }
}
