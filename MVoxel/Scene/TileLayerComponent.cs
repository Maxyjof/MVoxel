using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;

namespace MVoxel.Scene;

/// <summary>
/// 瓷砖朝向：六个体素轴之一，决定了从盒子的哪一侧往里找墙
/// </summary>
public enum TileFacing
{
    NegX,
    PosX,
    NegY,
    PosY,
    NegZ,
    PosZ,
}

/// <summary>
/// 一块瓷砖在凹凸不平的墙面上怎么落位
/// </summary>
public enum TileFitMode
{
    /// <summary>逐列贴合：每列按自己的墙面深度落位，瓷砖随凹凸起伏</summary>
    PerColumn,

    /// <summary>整块最前：槽位内取最靠外的深度，瓷砖平整，凹处悬空</summary>
    BlockFront,

    /// <summary>整块最后：槽位内取最靠里的深度，瓷砖平整，凸处被墙挡住一部分</summary>
    BlockBack,
}

/// <summary>
/// 瓷砖旋转方式
/// </summary>
public enum TileRotationMode
{
    /// <summary>全部同向</summary>
    None,

    /// <summary>四向随机：绕朝向 90 度倍数随机</summary>
    QuarterRandom,

    /// <summary>行列交替：相邻行或列转 90 度，像人字铺</summary>
    RowAlternate,
}

/// <summary>
/// 瓷砖超出盒体范围时的处理
/// </summary>
public enum TileEdgeMode
{
    /// <summary>裁剪：超出部分直接丢弃</summary>
    Clip,

    /// <summary>跳过整块：只要有一格超出就整块不铺</summary>
    SkipTile,
}

/// <summary>
/// 瓷砖铺设组件的数据：盒体、朝向、体素源与排布参数
/// </summary>
/// <remarks>
/// 盒体用对象局部整格坐标，范围换算成世界坐标时再加对象原点。
/// 盒子的长度和宽度（U/V 两轴）决定铺哪里，厚度方向只决定往里找墙的最大深度；
/// 瓷砖向外凸出多少由源厚度与偏移决定，不受盒体限制
/// </remarks>
/// <param name="Origin">盒体最小角，对象局部坐标</param>
/// <param name="Size">盒体三轴格数</param>
/// <param name="Facing">朝向，瓷砖正面朝这个方向</param>
/// <param name="Source">体素源引用</param>
/// <param name="SourceOffset">从源的哪个角开始取瓷砖，用于在整块模型里挑一段图案</param>
/// <param name="TileWidth">瓷砖在 U 方向的格数，0 表示跟随源尺寸</param>
/// <param name="TileHeight">瓷砖在 V 方向的格数，0 表示跟随源尺寸</param>
/// <param name="TileDepth">瓷砖在 W 方向取源的前几层，1 表示薄贴片</param>
/// <param name="Gap">缝宽格数，0 表示无缝</param>
/// <param name="Stagger">是否错缝，奇数行沿 U 偏移半块</param>
/// <param name="Rotation">旋转方式</param>
/// <param name="VariantCount">把源沿 U 等分成几块变体，1 表示单一图案</param>
/// <param name="Seed">随机种子，决定旋转与抖动</param>
/// <param name="JitterU">逐块在 U 方向的随机偏移上限</param>
/// <param name="JitterV">逐块在 V 方向的随机偏移上限</param>
/// <param name="Fit">凹凸墙面的落位方式</param>
/// <param name="SurfaceTolerance">共面容差，与中位深度相差超过它的列视为没有墙</param>
/// <param name="MaxDepth">沿朝向往里找墙的最大格数</param>
/// <param name="Offset">整体沿朝向的外凸格数，负数为内凹</param>
/// <param name="Edge">超出盒体的处理方式</param>
/// <param name="AutoRebuild">参数改动后是否自动重算</param>
/// <param name="Editing">是否处于盒体编辑模式</param>
public readonly record struct TileLayerComponentData(
    VoxelCoord64 Origin,
    VoxelCoord64 Size,
    TileFacing Facing = TileFacing.NegZ,
    VoxelSourceRef Source = default,
    VoxelCoord64 SourceOffset = default,
    int TileWidth = 0,
    int TileHeight = 0,
    int TileDepth = 1,
    int Gap = 0,
    bool Stagger = false,
    TileRotationMode Rotation = TileRotationMode.None,
    int VariantCount = 1,
    int Seed = 0,
    int JitterU = 0,
    int JitterV = 0,
    TileFitMode Fit = TileFitMode.PerColumn,
    int SurfaceTolerance = 2,
    int MaxDepth = 16,
    int Offset = 0,
    TileEdgeMode Edge = TileEdgeMode.Clip,
    bool AutoRebuild = true,
    bool Editing = false)
{
    /// <summary>算法版本，落位规则变化时提升，旧负载会按新规则重建</summary>
    private const int ProjectionVersion = 1;

    /// <summary>默认参数：8 格盒体、朝负 Z</summary>
    public static TileLayerComponentData Default => new(VoxelCoord64.Zero, new VoxelCoord64(8, 8, 4));

    /// <summary>
    /// 把盒体、参数与枚举约束到可用范围
    /// </summary>
    /// <returns>规范化后的数据</returns>
    public TileLayerComponentData Normalize()
    {
        var size = new VoxelCoord64(
            Math.Clamp(Size.X, 1, 4096), Math.Clamp(Size.Y, 1, 4096), Math.Clamp(Size.Z, 1, 4096));
        var origin = new VoxelCoord64(
            Math.Clamp(Origin.X, -10_000_000, 10_000_000),
            Math.Clamp(Origin.Y, -10_000_000, 10_000_000),
            Math.Clamp(Origin.Z, -10_000_000, 10_000_000));
        var source = Source.Kind == VoxelSourceKind.None ? VoxelSourceRef.None : Source with
        {
            AssetPath = (Source.AssetPath ?? string.Empty).Replace('\\', '/').Trim(),
        };
        if (source.Kind is VoxelSourceKind.ModelAsset or VoxelSourceKind.PrefabAsset &&
            string.IsNullOrWhiteSpace(source.AssetPath)) source = VoxelSourceRef.None;
        return new TileLayerComponentData(
            origin, size,
            Enum.IsDefined(Facing) ? Facing : TileFacing.NegZ,
            source,
            new VoxelCoord64(
                Math.Clamp(SourceOffset.X, 0, 4095), Math.Clamp(SourceOffset.Y, 0, 4095),
                Math.Clamp(SourceOffset.Z, 0, 4095)),
            Math.Clamp(TileWidth, 0, 256), Math.Clamp(TileHeight, 0, 256), Math.Clamp(TileDepth, 1, 256),
            Math.Clamp(Gap, 0, 32), Stagger,
            Enum.IsDefined(Rotation) ? Rotation : TileRotationMode.None,
            Math.Clamp(VariantCount, 1, 8),
            Math.Clamp(Seed, 0, 65535),
            Math.Clamp(JitterU, 0, 8), Math.Clamp(JitterV, 0, 8),
            Enum.IsDefined(Fit) ? Fit : TileFitMode.PerColumn,
            Math.Clamp(SurfaceTolerance, 0, 64),
            Math.Clamp(MaxDepth, 1, 4096),
            Math.Clamp(Offset, -16, 16),
            Enum.IsDefined(Edge) ? Edge : TileEdgeMode.Clip,
            AutoRebuild, Editing);
    }

    /// <summary>
    /// 影响生成结果的参数指纹；内嵌源内容变化时也要变化
    /// </summary>
    /// <param name="component">组件记录，用于读取内嵌源摘要</param>
    /// <returns>64 位指纹</returns>
    public ulong GenerationSignature(ComponentRecord component)
    {
        var data = Normalize();
        var hash = 14695981039346656037UL;
        MixInt(ref hash, ProjectionVersion);
        MixInt(ref hash, (int)data.Origin.X); MixInt(ref hash, (int)data.Origin.Y); MixInt(ref hash, (int)data.Origin.Z);
        MixInt(ref hash, (int)data.Size.X); MixInt(ref hash, (int)data.Size.Y); MixInt(ref hash, (int)data.Size.Z);
        MixInt(ref hash, (int)data.Facing);
        MixInt(ref hash, (int)data.Source.Kind);
        MixString(ref hash, data.Source.AssetPath);
        MixInt(ref hash, (int)data.SourceOffset.X);
        MixInt(ref hash, (int)data.SourceOffset.Y);
        MixInt(ref hash, (int)data.SourceOffset.Z);
        MixInt(ref hash, data.TileWidth); MixInt(ref hash, data.TileHeight); MixInt(ref hash, data.TileDepth);
        MixInt(ref hash, data.Gap); MixInt(ref hash, data.Stagger ? 1 : 0);
        MixInt(ref hash, (int)data.Rotation); MixInt(ref hash, data.VariantCount);
        MixInt(ref hash, data.Seed); MixInt(ref hash, data.JitterU); MixInt(ref hash, data.JitterV);
        MixInt(ref hash, (int)data.Fit); MixInt(ref hash, data.SurfaceTolerance);
        MixInt(ref hash, data.MaxDepth); MixInt(ref hash, data.Offset); MixInt(ref hash, (int)data.Edge);
        if (data.Source.Kind == VoxelSourceKind.Embedded)
        {
            var content = TileSourcePayload.ContentHash(component);
            MixInt(ref hash, unchecked((int)(content ^ (content >> 32))));
        }

        return hash;

        static void MixString(ref ulong hash, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var character in text) MixInt(ref hash, character);
        }

        static void MixInt(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
        }
    }
}

/// <summary>
/// 瓷砖组件的 JSON 读写：字段名保持短小稳定，写回时保留生成负载与未知字段
/// </summary>
internal static class TileLayerJson
{
    private const string OriginXName = "originX";
    private const string OriginYName = "originY";
    private const string OriginZName = "originZ";
    private const string SizeXName = "sizeX";
    private const string SizeYName = "sizeY";
    private const string SizeZName = "sizeZ";
    private const string FacingName = "facing";
    private const string SourceKindName = "sourceKind";
    private const string SourcePathName = "sourcePath";
    private const string SourceOffsetXName = "sourceOffsetX";
    private const string SourceOffsetYName = "sourceOffsetY";
    private const string SourceOffsetZName = "sourceOffsetZ";
    private const string TileWidthName = "tileWidth";
    private const string TileHeightName = "tileHeight";
    private const string TileDepthName = "tileDepth";
    private const string GapName = "gap";
    private const string StaggerName = "stagger";
    private const string RotationName = "rotation";
    private const string VariantCountName = "variantCount";
    private const string SeedName = "seed";
    private const string JitterUName = "jitterU";
    private const string JitterVName = "jitterV";
    private const string FitName = "fit";
    private const string SurfaceToleranceName = "surfaceTolerance";
    private const string MaxDepthName = "maxDepth";
    private const string OffsetName = "offset";
    private const string EdgeName = "edge";
    private const string AutoRebuildName = "autoRebuild";
    private const string EditingName = "editing";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    /// <summary>读取组件数据，缺失或损坏字段回落到默认值</summary>
    /// <param name="data">组件 JSON</param>
    /// <returns>规范化后的瓷砖参数</returns>
    internal static TileLayerComponentData Read(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return TileLayerComponentData.Default;
        var kind = Int(data, SourceKindName, 0);
        var source = new VoxelSourceRef(
            Enum.IsDefined((VoxelSourceKind)kind) ? (VoxelSourceKind)kind : VoxelSourceKind.None,
            String(data, SourcePathName, string.Empty));
        return new TileLayerComponentData(
            new VoxelCoord64(Int(data, OriginXName, 0), Int(data, OriginYName, 0), Int(data, OriginZName, 0)),
            new VoxelCoord64(Int(data, SizeXName, 8), Int(data, SizeYName, 8), Int(data, SizeZName, 4)),
            Int(data, FacingName, (int)TileFacing.NegZ) is var facing && Enum.IsDefined((TileFacing)facing)
                ? (TileFacing)facing : TileFacing.NegZ,
            source,
            new VoxelCoord64(Int(data, SourceOffsetXName, 0), Int(data, SourceOffsetYName, 0),
                Int(data, SourceOffsetZName, 0)),
            Int(data, TileWidthName, 0), Int(data, TileHeightName, 0), Int(data, TileDepthName, 1),
            Int(data, GapName, 0), Bool(data, StaggerName, false),
            Int(data, RotationName, 0) is var rotation && Enum.IsDefined((TileRotationMode)rotation)
                ? (TileRotationMode)rotation : TileRotationMode.None,
            Int(data, VariantCountName, 1),
            Int(data, SeedName, 0), Int(data, JitterUName, 0), Int(data, JitterVName, 0),
            Int(data, FitName, 0) is var fit && Enum.IsDefined((TileFitMode)fit) ? (TileFitMode)fit : TileFitMode.PerColumn,
            Int(data, SurfaceToleranceName, 2), Int(data, MaxDepthName, 16), Int(data, OffsetName, 0),
            Int(data, EdgeName, 0) is var edge && Enum.IsDefined((TileEdgeMode)edge) ? (TileEdgeMode)edge : TileEdgeMode.Clip,
            Bool(data, AutoRebuildName, true), Bool(data, EditingName, false)).Normalize();
    }

    /// <summary>把数据合并写回组件，保留生成负载与未知字段</summary>
    /// <param name="component">目标组件记录</param>
    /// <param name="data">新的瓷砖参数</param>
    internal static void Write(ComponentRecord component, TileLayerComponentData data)
    {
        var normalized = data.Normalize();
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged[OriginXName] = normalized.Origin.X;
        merged[OriginYName] = normalized.Origin.Y;
        merged[OriginZName] = normalized.Origin.Z;
        merged[SizeXName] = normalized.Size.X;
        merged[SizeYName] = normalized.Size.Y;
        merged[SizeZName] = normalized.Size.Z;
        merged[FacingName] = (int)normalized.Facing;
        merged[SourceKindName] = (int)normalized.Source.Kind;
        merged[SourcePathName] = normalized.Source.AssetPath;
        merged[SourceOffsetXName] = normalized.SourceOffset.X;
        merged[SourceOffsetYName] = normalized.SourceOffset.Y;
        merged[SourceOffsetZName] = normalized.SourceOffset.Z;
        merged[TileWidthName] = normalized.TileWidth;
        merged[TileHeightName] = normalized.TileHeight;
        merged[TileDepthName] = normalized.TileDepth;
        merged[GapName] = normalized.Gap;
        merged[StaggerName] = normalized.Stagger;
        merged[RotationName] = (int)normalized.Rotation;
        merged[VariantCountName] = normalized.VariantCount;
        merged[SeedName] = normalized.Seed;
        merged[JitterUName] = normalized.JitterU;
        merged[JitterVName] = normalized.JitterV;
        merged[FitName] = (int)normalized.Fit;
        merged[SurfaceToleranceName] = normalized.SurfaceTolerance;
        merged[MaxDepthName] = normalized.MaxDepth;
        merged[OffsetName] = normalized.Offset;
        merged[EdgeName] = (int)normalized.Edge;
        merged[AutoRebuildName] = normalized.AutoRebuild;
        merged[EditingName] = normalized.Editing;
        component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    private static int Int(JsonElement data, string name, int fallback) =>
        data.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;

    private static bool Bool(JsonElement data, string name, bool fallback) =>
        data.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : fallback;

    private static string String(JsonElement data, string name, string fallback) =>
        data.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback : fallback;
}
