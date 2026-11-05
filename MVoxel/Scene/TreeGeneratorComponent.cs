using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 树的整体风格，决定树干走势、枝条分布和树叶的组织方式
/// </summary>
public enum TreeStyle
{
    /// <summary>阔叶树：直干、多级枝条加椭球树冠</summary>
    Broadleaf,

    /// <summary>针叶树：直干加层叠锥形树冠</summary>
    Conifer,

    /// <summary>灌木：低矮丛生，树冠贴近地面</summary>
    Bush,

    /// <summary>棕榈：弯曲树干加顶部放射状叶片</summary>
    Palm,

    /// <summary>垂柳：树冠下缘垂下长条枝叶</summary>
    Willow,

    /// <summary>樱花树：较矮的阔叶树，粉色树冠</summary>
    Cherry,

    /// <summary>枯树：只有树干与枝条，没有树叶</summary>
    Dead,
}

/// <summary>
/// 树冠形态
/// </summary>
public enum TreeCrownShape
{
    /// <summary>球形</summary>
    Sphere,

    /// <summary>椭球：横向与纵向分别可调</summary>
    Ellipsoid,

    /// <summary>圆锥：针叶树常用的层叠锥体</summary>
    Cone,

    /// <summary>伞形：棕榈式的平顶</summary>
    Umbrella,

    /// <summary>层叠：水平圆盘按层叠起</summary>
    Layered,

    /// <summary>无树冠：只保留树干与枝条</summary>
    None,
}

/// <summary>
/// 树生成器组件数据：风格、种子、树干、枝条、树冠与颜色参数
/// </summary>
/// <remarks>
/// 组件本身携带隐藏的体素负载，点“生成体素”才会按当前参数重建；
/// 参数改了但没点生成的这段时间里，保留上一棵树的样子，
/// 因此负载会逐格保存颜色，保证重新打开场景时看到的还是那棵树
/// </remarks>
/// <param name="Style">树的风格</param>
/// <param name="Seed">随机种子，决定枝条与树冠的随机分布</param>
/// <param name="TrunkHeight">树干高度（格）</param>
/// <param name="TrunkRadius">树干底部半径（格）</param>
/// <param name="TrunkLean">树干倾斜角（度）</param>
/// <param name="TrunkBend">树干弯曲强度，0 为笔直</param>
/// <param name="TrunkTaper">树干从下到上的收缩比例</param>
/// <param name="RootFlare">树根外扩强度，0 为没有根部</param>
/// <param name="BranchCount">主干上的主枝数量</param>
/// <param name="BranchLength">主枝长度（格）</param>
/// <param name="BranchThickness">主枝粗细（格）</param>
/// <param name="BranchAngle">主枝上扬角度（度）</param>
/// <param name="BranchDroop">枝条下垂程度，负值上扬、正值下垂</param>
/// <param name="BranchDepth">枝条再分叉的层数</param>
/// <param name="CrownStart">树冠起始高度占树干高度的比例</param>
/// <param name="CrownRadius">树冠水平半径（格）</param>
/// <param name="CrownHeight">树冠纵向半径（格）</param>
/// <param name="CrownShape">树冠形态</param>
/// <param name="LeafDensity">树叶密度，0 到 1，越小越透光</param>
/// <param name="LeafSize">叶团的半径（格），越大越成块</param>
/// <param name="BarkR">树皮颜色红通道</param>
/// <param name="BarkG">树皮颜色绿通道</param>
/// <param name="BarkB">树皮颜色蓝通道</param>
/// <param name="LeafR">树叶主色红通道</param>
/// <param name="LeafG">树叶主色绿通道</param>
/// <param name="LeafB">树叶主色蓝通道</param>
/// <param name="Leaf2R">树叶辅色红通道</param>
/// <param name="Leaf2G">树叶辅色绿通道</param>
/// <param name="Leaf2B">树叶辅色蓝通道</param>
/// <param name="ColorVariation">辅色混入比例，0 到 1</param>
/// <param name="EmissiveLeaves">树叶是否带自发光标记</param>
/// <param name="MaterialSlot">生成体素使用的本地材质槽</param>
/// <param name="SortingLayer">排序图层</param>
/// <param name="SortingOrder">排序顺序</param>
public readonly record struct TreeGeneratorComponentData(
    TreeStyle Style = TreeStyle.Broadleaf,
    int Seed = 1337,
    int TrunkHeight = 18,
    float TrunkRadius = 2F,
    float TrunkLean = 0F,
    float TrunkBend = 0.12F,
    float TrunkTaper = 0.5F,
    float RootFlare = 0.3F,
    int BranchCount = 6,
    float BranchLength = 7F,
    float BranchThickness = 1.1F,
    float BranchAngle = 38F,
    float BranchDroop = 0.25F,
    int BranchDepth = 1,
    float CrownStart = 0.55F,
    float CrownRadius = 10F,
    float CrownHeight = 12F,
    TreeCrownShape CrownShape = TreeCrownShape.Ellipsoid,
    float LeafDensity = 0.72F,
    float LeafSize = 2F,
    byte BarkR = 92,
    byte BarkG = 66,
    byte BarkB = 46,
    byte LeafR = 74,
    byte LeafG = 136,
    byte LeafB = 62,
    byte Leaf2R = 118,
    byte Leaf2G = 168,
    byte Leaf2B = 82,
    float ColorVariation = 0.4F,
    bool EmissiveLeaves = false,
    ushort MaterialSlot = 0,
    string SortingLayer = "默认",
    int SortingOrder = 0)
{
    /// <summary>参数上限与下限统一写在这里，生成与检查器共用</summary>
    public const int MaximumTrunkHeight = 128;

    /// <summary>默认组件数据：一棵中号阔叶树</summary>
    /// <remarks>
    /// 必须显式传第一个参数才会走主构造函数，否则 record struct 的无参构造会把全部字段清零
    /// </remarks>
    public static TreeGeneratorComponentData Default => new(TreeStyle.Broadleaf);

    /// <summary>
    /// 把参数约束到可用范围
    /// </summary>
    /// <returns>规范化后的树参数</returns>
    public TreeGeneratorComponentData Normalize()
    {
        var height = Math.Clamp(TrunkHeight, 2, MaximumTrunkHeight);
        var crownStart = float.IsFinite(CrownStart) ? Math.Clamp(CrownStart, 0.05F, 1F) : 0.55F;
        return new TreeGeneratorComponentData(
            Enum.IsDefined(Style) ? Style : TreeStyle.Broadleaf,
            Seed,
            height,
            Clamp(TrunkRadius, 0.4F, 12F, 2F),
            Clamp(TrunkLean, 0F, 70F, 0F),
            Clamp(TrunkBend, 0F, 1F, 0.12F),
            Clamp(TrunkTaper, 0F, 0.95F, 0.5F),
            Clamp(RootFlare, 0F, 1F, 0.3F),
            Math.Clamp(BranchCount, 0, 24),
            Clamp(BranchLength, 0.5F, 64F, 7F),
            Clamp(BranchThickness, 0.3F, 8F, 1.1F),
            Clamp(BranchAngle, -20F, 89F, 38F),
            Clamp(BranchDroop, -1F, 1F, 0.25F),
            Math.Clamp(BranchDepth, 0, 3),
            crownStart,
            Clamp(CrownRadius, 1F, 64F, 10F),
            Clamp(CrownHeight, 1F, 64F, 12F),
            Enum.IsDefined(CrownShape) ? CrownShape : TreeCrownShape.Ellipsoid,
            Clamp(LeafDensity, 0.05F, 1F, 0.72F),
            Clamp(LeafSize, 0.5F, 8F, 2F),
            BarkR, BarkG, BarkB, LeafR, LeafG, LeafB, Leaf2R, Leaf2G, Leaf2B,
            Clamp(ColorVariation, 0F, 1F, 0.4F),
            EmissiveLeaves,
            MaterialSlot,
            string.IsNullOrWhiteSpace(SortingLayer) ? "默认" : SortingLayer.Trim(),
            Math.Clamp(SortingOrder, -100_000, 100_000));
    }

    /// <summary>树皮体素值</summary>
    [JsonIgnore]
    public VoxelValue BarkValue => new(new Rgba32(BarkR, BarkG, BarkB, 255), MaterialSlot, VoxelFlags.None);

    /// <summary>树叶主色体素值，发光标记由参数决定</summary>
    [JsonIgnore]
    public VoxelValue LeafValue => LeafVoxel(LeafR, LeafG, LeafB);

    /// <summary>树叶辅色体素值</summary>
    [JsonIgnore]
    public VoxelValue Leaf2Value => LeafVoxel(Leaf2R, Leaf2G, Leaf2B);

    /// <summary>
    /// 由颜色分量构造树叶体素值
    /// </summary>
    /// <param name="red">红通道</param>
    /// <param name="green">绿通道</param>
    /// <param name="blue">蓝通道</param>
    /// <returns>带自发光标记的树叶值</returns>
    public VoxelValue LeafVoxel(byte red, byte green, byte blue) => new(
        new Rgba32(red, green, blue, 255), MaterialSlot,
        EmissiveLeaves ? VoxelFlags.Emissive : VoxelFlags.None);

    /// <summary>
    /// 影响生成结果的参数指纹，用于检查器提示参数是否已经生效
    /// </summary>
    /// <returns>64 位指纹</returns>
    public ulong GenerationSignature()
    {
        var data = Normalize();
        var hash = 14695981039346656037UL;
        MixInt(ref hash, (int)data.Style);
        MixInt(ref hash, data.Seed);
        MixInt(ref hash, data.TrunkHeight);
        MixFloat(ref hash, data.TrunkRadius);
        MixFloat(ref hash, data.TrunkLean);
        MixFloat(ref hash, data.TrunkBend);
        MixFloat(ref hash, data.TrunkTaper);
        MixFloat(ref hash, data.RootFlare);
        MixInt(ref hash, data.BranchCount);
        MixFloat(ref hash, data.BranchLength);
        MixFloat(ref hash, data.BranchThickness);
        MixFloat(ref hash, data.BranchAngle);
        MixFloat(ref hash, data.BranchDroop);
        MixInt(ref hash, data.BranchDepth);
        MixFloat(ref hash, data.CrownStart);
        MixFloat(ref hash, data.CrownRadius);
        MixFloat(ref hash, data.CrownHeight);
        MixInt(ref hash, (int)data.CrownShape);
        MixFloat(ref hash, data.LeafDensity);
        MixFloat(ref hash, data.LeafSize);
        MixInt(ref hash, data.BarkR); MixInt(ref hash, data.BarkG); MixInt(ref hash, data.BarkB);
        MixInt(ref hash, data.LeafR); MixInt(ref hash, data.LeafG); MixInt(ref hash, data.LeafB);
        MixInt(ref hash, data.Leaf2R); MixInt(ref hash, data.Leaf2G); MixInt(ref hash, data.Leaf2B);
        MixFloat(ref hash, data.ColorVariation);
        MixInt(ref hash, data.EmissiveLeaves ? 1 : 0);
        MixInt(ref hash, data.MaterialSlot);
        return hash;

        static void MixFloat(ref ulong hash, float value) => MixInt(ref hash, BitConverter.SingleToInt32Bits(value));

        static void MixInt(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash ^= hash >> 33;
                hash *= 0xFF51AFD7ED558CCDUL;
                hash ^= hash >> 33;
            }
        }
    }

    /// <summary>
    /// 切换风格时套用该风格的推荐参数，用户之后仍可自由微调
    /// </summary>
    /// <param name="style">目标风格</param>
    /// <returns>带风格预设的参数</returns>
    public TreeGeneratorComponentData WithStyleDefaults(TreeStyle style) => style switch
    {
        TreeStyle.Conifer => this with
        {
            Style = style, TrunkHeight = 26, TrunkRadius = 1.8F, TrunkLean = 0F, TrunkBend = 0.04F,
            TrunkTaper = 0.55F, RootFlare = 0.25F, BranchCount = 0, BranchDepth = 0,
            CrownStart = 0.22F, CrownRadius = 11F, CrownHeight = 13F, CrownShape = TreeCrownShape.Cone,
            LeafDensity = 0.82F, LeafSize = 1.2F, LeafR = 48, LeafG = 96, LeafB = 58,
            Leaf2R = 72, Leaf2G = 122, Leaf2B = 74, ColorVariation = 0.35F,
        },
        TreeStyle.Bush => this with
        {
            Style = style, TrunkHeight = 3, TrunkRadius = 0.8F, TrunkLean = 0F, TrunkBend = 0.3F,
            TrunkTaper = 0.3F, RootFlare = 0.5F, BranchCount = 5, BranchLength = 3F,
            BranchThickness = 0.7F, BranchAngle = 55F, BranchDroop = 0.3F, BranchDepth = 1,
            CrownStart = 0.1F, CrownRadius = 7F, CrownHeight = 5F, CrownShape = TreeCrownShape.Sphere,
            LeafDensity = 0.8F, LeafSize = 1.8F,
        },
        TreeStyle.Palm => this with
        {
            Style = style, TrunkHeight = 30, TrunkRadius = 1.4F, TrunkLean = 18F, TrunkBend = 0.6F,
            TrunkTaper = 0.3F, RootFlare = 0.35F, BranchCount = 0, BranchDepth = 0,
            CrownStart = 0.9F, CrownRadius = 12F, CrownHeight = 2F, CrownShape = TreeCrownShape.Umbrella,
            LeafDensity = 0.9F, LeafSize = 1.6F, LeafR = 92, LeafG = 150, LeafB = 66,
        },
        TreeStyle.Willow => this with
        {
            Style = style, TrunkHeight = 20, TrunkRadius = 2F, TrunkLean = 0F, TrunkBend = 0.18F,
            TrunkTaper = 0.45F, RootFlare = 0.35F, BranchCount = 7, BranchLength = 8F,
            BranchThickness = 1.1F, BranchAngle = 30F, BranchDroop = 0.6F, BranchDepth = 1,
            CrownStart = 0.5F, CrownRadius = 11F, CrownHeight = 9F, CrownShape = TreeCrownShape.Layered,
            LeafDensity = 0.85F, LeafSize = 1.6F, LeafR = 118, LeafG = 150, LeafB = 72,
            Leaf2R = 152, Leaf2G = 176, Leaf2B = 88, ColorVariation = 0.5F,
        },
        TreeStyle.Cherry => this with
        {
            Style = style, TrunkHeight = 14, TrunkRadius = 1.8F, TrunkLean = 4F, TrunkBend = 0.35F,
            TrunkTaper = 0.4F, RootFlare = 0.4F, BranchCount = 7, BranchLength = 6F,
            BranchThickness = 1F, BranchAngle = 42F, BranchDroop = 0.1F, BranchDepth = 1,
            CrownStart = 0.4F, CrownRadius = 7.5F, CrownHeight = 8F, CrownShape = TreeCrownShape.Sphere,
            LeafDensity = 0.82F, LeafSize = 2.2F, LeafR = 232, LeafG = 168, LeafB = 196,
            Leaf2R = 246, Leaf2G = 204, Leaf2B = 220, ColorVariation = 0.55F,
        },
        TreeStyle.Dead => this with
        {
            Style = style, TrunkHeight = 15, TrunkRadius = 2.2F, TrunkLean = 6F, TrunkBend = 0.4F,
            TrunkTaper = 0.6F, RootFlare = 0.5F, BranchCount = 9, BranchLength = 7F,
            BranchThickness = 1.2F, BranchAngle = 30F, BranchDroop = -0.15F, BranchDepth = 2,
            CrownShape = TreeCrownShape.None, LeafDensity = 0F,
        },
        _ => this with
        {
            Style = TreeStyle.Broadleaf, TrunkHeight = 18, TrunkRadius = 2F, TrunkLean = 0F,
            TrunkBend = 0.12F, TrunkTaper = 0.5F, RootFlare = 0.3F, BranchCount = 6,
            BranchLength = 7F, BranchThickness = 1.1F, BranchAngle = 38F, BranchDroop = 0.25F,
            BranchDepth = 1, CrownStart = 0.55F, CrownRadius = 10F, CrownHeight = 12F,
            CrownShape = TreeCrownShape.Ellipsoid, LeafDensity = 0.72F, LeafSize = 2F,
            LeafR = 74, LeafG = 136, LeafB = 62, Leaf2R = 118, Leaf2G = 168, Leaf2B = 82,
            ColorVariation = 0.4F,
        },
    };

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

/// <summary>
/// 树生成器组件在场景 JSON 里的字段读写
/// </summary>
internal static class TreeGeneratorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    /// <summary>
    /// 读取组件负载，缺失或损坏的字段回落到默认值
    /// </summary>
    /// <param name="data">组件 JSON</param>
    /// <returns>规范化后的树参数</returns>
    internal static TreeGeneratorComponentData Read(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return TreeGeneratorComponentData.Default;
        return new TreeGeneratorComponentData(
            Int(data, "style", 0) is var style && Enum.IsDefined((TreeStyle)style) ? (TreeStyle)style : TreeStyle.Broadleaf,
            Int(data, "seed", 1337),
            Int(data, "trunkHeight", 18),
            Float(data, "trunkRadius", 2F),
            Float(data, "trunkLean", 0F),
            Float(data, "trunkBend", 0.12F),
            Float(data, "trunkTaper", 0.5F),
            Float(data, "rootFlare", 0.3F),
            Int(data, "branchCount", 6),
            Float(data, "branchLength", 7F),
            Float(data, "branchThickness", 1.1F),
            Float(data, "branchAngle", 38F),
            Float(data, "branchDroop", 0.25F),
            Int(data, "branchDepth", 1),
            Float(data, "crownStart", 0.55F),
            Float(data, "crownRadius", 10F),
            Float(data, "crownHeight", 12F),
            Int(data, "crownShape", 1) is var shape && Enum.IsDefined((TreeCrownShape)shape)
                ? (TreeCrownShape)shape : TreeCrownShape.Ellipsoid,
            Float(data, "leafDensity", 0.72F),
            Float(data, "leafSize", 2F),
            (byte)Math.Clamp(Int(data, "barkR", 92), 0, 255),
            (byte)Math.Clamp(Int(data, "barkG", 66), 0, 255),
            (byte)Math.Clamp(Int(data, "barkB", 46), 0, 255),
            (byte)Math.Clamp(Int(data, "leafR", 74), 0, 255),
            (byte)Math.Clamp(Int(data, "leafG", 136), 0, 255),
            (byte)Math.Clamp(Int(data, "leafB", 62), 0, 255),
            (byte)Math.Clamp(Int(data, "leaf2R", 118), 0, 255),
            (byte)Math.Clamp(Int(data, "leaf2G", 168), 0, 255),
            (byte)Math.Clamp(Int(data, "leaf2B", 82), 0, 255),
            Float(data, "colorVariation", 0.4F),
            Bool(data, "emissiveLeaves", false),
            (ushort)Math.Clamp(Int(data, "materialSlot", 0), 0, ushort.MaxValue),
            String(data, "sortingLayer", "默认"),
            Int(data, "sortingOrder", 0)).Normalize();
    }

    /// <summary>
    /// 把树参数合并写回组件，保留生成体素负载与未知字段
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="data">新的树参数</param>
    internal static void Write(ComponentRecord component, TreeGeneratorComponentData data)
    {
        var normalized = data.Normalize();
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged["style"] = (int)normalized.Style;
        merged["seed"] = normalized.Seed;
        merged["trunkHeight"] = normalized.TrunkHeight;
        merged["trunkRadius"] = normalized.TrunkRadius;
        merged["trunkLean"] = normalized.TrunkLean;
        merged["trunkBend"] = normalized.TrunkBend;
        merged["trunkTaper"] = normalized.TrunkTaper;
        merged["rootFlare"] = normalized.RootFlare;
        merged["branchCount"] = normalized.BranchCount;
        merged["branchLength"] = normalized.BranchLength;
        merged["branchThickness"] = normalized.BranchThickness;
        merged["branchAngle"] = normalized.BranchAngle;
        merged["branchDroop"] = normalized.BranchDroop;
        merged["branchDepth"] = normalized.BranchDepth;
        merged["crownStart"] = normalized.CrownStart;
        merged["crownRadius"] = normalized.CrownRadius;
        merged["crownHeight"] = normalized.CrownHeight;
        merged["crownShape"] = (int)normalized.CrownShape;
        merged["leafDensity"] = normalized.LeafDensity;
        merged["leafSize"] = normalized.LeafSize;
        merged["barkR"] = normalized.BarkR;
        merged["barkG"] = normalized.BarkG;
        merged["barkB"] = normalized.BarkB;
        merged["leafR"] = normalized.LeafR;
        merged["leafG"] = normalized.LeafG;
        merged["leafB"] = normalized.LeafB;
        merged["leaf2R"] = normalized.Leaf2R;
        merged["leaf2G"] = normalized.Leaf2G;
        merged["leaf2B"] = normalized.Leaf2B;
        merged["colorVariation"] = normalized.ColorVariation;
        merged["emissiveLeaves"] = normalized.EmissiveLeaves;
        merged["materialSlot"] = normalized.MaterialSlot;
        merged["sortingLayer"] = normalized.SortingLayer;
        merged["sortingOrder"] = normalized.SortingOrder;
        component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>
    /// 只读取渲染排序字段
    /// </summary>
    /// <param name="data">组件 JSON</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    internal static void ReadSorting(JsonElement data, out string sortingLayer, out int sortingOrder)
    {
        sortingLayer = "默认";
        sortingOrder = 0;
        if (data.ValueKind != JsonValueKind.Object) return;
        sortingLayer = String(data, "sortingLayer", "默认");
        if (string.IsNullOrWhiteSpace(sortingLayer)) sortingLayer = "默认";
        sortingOrder = Int(data, "sortingOrder", 0);
    }

    private static float Float(JsonElement data, string name, float fallback) =>
        data.TryGetProperty(name, out var property) && property.TryGetSingle(out var value) && float.IsFinite(value)
            ? value : fallback;

    private static int Int(JsonElement data, string name, int fallback) =>
        data.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;

    private static bool Bool(JsonElement data, string name, bool fallback) =>
        data.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : fallback;

    private static string String(JsonElement data, string name, string fallback) =>
        data.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback : fallback;
}
