using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MVoxel.Scene;

/// <summary>
/// 曲线点的切线联动方式，行为与常见样条工具一致
/// </summary>
public enum CurveTangentMode
{
    /// <summary>自动：按相邻点位置推算平滑切线，移动点后自动更新</summary>
    Auto,

    /// <summary>对齐：前后切线方向相反，长度各自独立</summary>
    Aligned,

    /// <summary>镜像：前后切线方向相反且长度相同</summary>
    Mirrored,

    /// <summary>自由：前后切线互不影响</summary>
    Free,
}

/// <summary>
/// 曲线插值方式
/// </summary>
public enum CurveInterpolation
{
    /// <summary>三次贝塞尔，由每点的前后切线控制形状</summary>
    Bezier,

    /// <summary>直线连接，忽略切线</summary>
    Linear,
}

/// <summary>
/// 曲线体素的截面形状
/// </summary>
public enum CurveBrushShape
{
    /// <summary>方块管：沿点朝向展开的正方形截面</summary>
    Square,

    /// <summary>圆管：沿点朝向展开的圆形截面</summary>
    Round,

    /// <summary>珠链：以采样点为中心的球体，间距大时会断开成一串珠子</summary>
    Beads,

    /// <summary>十字管：正方形的四条臂，适合栏杆与窗棂</summary>
    Cross,
}

/// <summary>
/// 曲线上的一个控制点：位置、前后切线、朝向与宽度
/// </summary>
/// <remarks>
/// 位置以对象局部坐标记录，单位是体素格，和体素框选组件的坐标口径一致。
/// 旋转是控制点局部坐标系的朝向，决定截面形状怎么摆放；改变它只影响截面，不改变曲线走向。
/// 切线是从控制点指向贝塞尔控制手柄的偏移量，出切线朝向下一点，入切线朝向上一点
/// </remarks>
/// <param name="Position">控制点位置（局部体素坐标）</param>
/// <param name="TangentIn">入切线偏移，贝塞尔第二控制手柄 = 位置 + 入切线</param>
/// <param name="TangentOut">出切线偏移，贝塞尔第一控制手柄 = 位置 + 出切线</param>
/// <param name="Rotation">控制点局部朝向，主要用于决定截面绕曲线的旋转</param>
/// <param name="TangentMode">切线联动方式</param>
/// <param name="Width">截面宽度倍率，用来做粗细渐变</param>
public readonly record struct CurvePointData(
    Vector3 Position,
    Vector3 TangentIn,
    Vector3 TangentOut,
    Quaternion Rotation,
    CurveTangentMode TangentMode = CurveTangentMode.Auto,
    float Width = 1F)
{
    /// <summary>坐标允许的绝对值上限，避免极端数值破坏包围盒与投影</summary>
    private const float CoordinateLimit = 1_000_000F;

    /// <summary>
    /// 单位朝向的控制点，切线由自动模式推算
    /// </summary>
    /// <param name="position">局部位置</param>
    /// <returns>新的控制点</returns>
    public static CurvePointData At(Vector3 position) =>
        new(position, Vector3.Zero, Vector3.Zero, Quaternion.Identity);

    /// <summary>
    /// 把位置、切线、旋转与宽度约束到可用范围
    /// </summary>
    /// <returns>规范化后的控制点</returns>
    public CurvePointData Normalize() => new(
        Sanitize(Position),
        Sanitize(TangentIn),
        Sanitize(TangentOut),
        SanitizeRotation(Rotation),
        Enum.IsDefined(TangentMode) ? TangentMode : CurveTangentMode.Auto,
        float.IsFinite(Width) ? Math.Clamp(Width, 0.05F, 64F) : 1F);

    /// <summary>把切线向量逐分量限制在安全区间</summary>
    private static Vector3 Sanitize(Vector3 value) => new(
        SanitizeComponent(value.X), SanitizeComponent(value.Y), SanitizeComponent(value.Z));

    private static float SanitizeComponent(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, -CoordinateLimit, CoordinateLimit) : 0F;

    /// <summary>旋转缺失或退化时回落到单位旋转</summary>
    private static Quaternion SanitizeRotation(Quaternion value)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.0001F) return Quaternion.Identity;
        return Quaternion.Normalize(value);
    }
}

/// <summary>
/// 曲线渲染器组件数据：控制点、插值参数、生成参数与编辑状态
/// </summary>
/// <remarks>
/// 生成的体素不在这个类型里，而是由 GeneratedVoxelData 以隐藏负载的形式写进同一份组件 JSON，
/// 检查和场景里只能看到曲线本身。生成参数一旦变化就重建负载，负载与参数不一致时按参数重算
/// </remarks>
/// <param name="Points">控制点集合，至少两点且非闭合时才有可生成体素的线段</param>
/// <param name="Closed">是否首尾相连形成闭合曲线</param>
/// <param name="Interpolation">插值方式</param>
/// <param name="Resolution">每段曲线的采样数，越大曲线越平滑</param>
/// <param name="Step">沿曲线的采样间距（体素），越小体素越连续</param>
/// <param name="Shape">截面形状</param>
/// <param name="Radius">截面半径（体素），乘以每点宽度得到实际粗细</param>
/// <param name="ColorR">体素颜色红通道</param>
/// <param name="ColorG">体素颜色绿通道</param>
/// <param name="ColorB">体素颜色蓝通道</param>
/// <param name="ColorA">体素透明度</param>
/// <param name="Emissive">是否给生成体素加上自发光标记</param>
/// <param name="MaterialSlot">生成体素使用的本地材质槽</param>
/// <param name="SortingLayer">排序图层，和模型组件的同名参数一致</param>
/// <param name="SortingOrder">排序顺序</param>
/// <param name="Editing">是否处于曲线编辑模式</param>
public readonly record struct CurveRendererComponentData(
    CurvePointData[] Points,
    bool Closed = false,
    CurveInterpolation Interpolation = CurveInterpolation.Bezier,
    int Resolution = 16,
    float Step = 0.5F,
    CurveBrushShape Shape = CurveBrushShape.Round,
    float Radius = 2F,
    byte ColorR = 255,
    byte ColorG = 255,
    byte ColorB = 255,
    byte ColorA = 255,
    bool Emissive = false,
    ushort MaterialSlot = 0,
    string SortingLayer = "默认",
    int SortingOrder = 0,
    bool Editing = false)
{
    /// <summary>控制点数量上限，避免一次生成把界面拖死</summary>
    public const int MaximumPointCount = 512;

    /// <summary>光栅化算法版本；算法改动时提升，使旧负载按新规则重建</summary>
    private const int RasterVersion = 2;

    /// <summary>每段采样数上限</summary>
    public const int MaximumResolution = 64;

    /// <summary>每段采样数下限</summary>
    public const int MinimumResolution = 4;

    /// <summary>默认新建曲线：一段自动切线的拱形，方便直接看到效果</summary>
    public static CurveRendererComponentData CreateDefault()
    {
        var points = new List<CurvePointData>
        {
            CurvePointData.At(new Vector3(0F, 2F, 0F)), CurvePointData.At(new Vector3(6F, 6F, 0F)),
            CurvePointData.At(new Vector3(12F, 2F, 0F)),
        };
        // 首建就带自动切线，否则前两个手柄重合，曲线会像一条折线
        CurveSpline.RecomputeAutoTangents(points, closed: false);
        return new CurveRendererComponentData(points.ToArray()) with { Editing = true };
    }

    /// <summary>
    /// 把控制点数量、采样与半径等参数约束到可用范围
    /// </summary>
    /// <returns>规范化后的组件数据</returns>
    public CurveRendererComponentData Normalize()
    {
        var points = (Points ?? []).Take(MaximumPointCount).Select(point => point.Normalize()).ToArray();
        var normalization = new CurveRendererComponentData(points, Closed,
            Enum.IsDefined(Interpolation) ? Interpolation : CurveInterpolation.Bezier,
            Math.Clamp(Resolution, MinimumResolution, MaximumResolution),
            float.IsFinite(Step) ? Math.Clamp(Step, 0.1F, 8F) : 0.5F,
            Enum.IsDefined(Shape) ? Shape : CurveBrushShape.Round,
            float.IsFinite(Radius) ? Math.Clamp(Radius, 0.1F, 64F) : 2F,
            ColorR, ColorG, ColorB, ColorA, Emissive, MaterialSlot,
            string.IsNullOrWhiteSpace(SortingLayer) ? "默认" : SortingLayer.Trim(),
            Math.Clamp(SortingOrder, -100_000, 100_000), Editing);
        return normalization;
    }

    /// <summary>生成体素时使用的统一体素值</summary>
    [JsonIgnore]
    public Voxels.VoxelValue VoxelValue => new(
        new Voxels.Rgba32(ColorR, ColorG, ColorB, ColorA), MaterialSlot,
        (byte)((Emissive ? Voxels.VoxelFlags.Emissive : Voxels.VoxelFlags.None) |
               (ColorA < 255 ? Voxels.VoxelFlags.Translucent : Voxels.VoxelFlags.None)));

    /// <summary>
    /// 影响生成结果的参数指纹，隐藏负载与指纹不一致时按当前参数重算
    /// </summary>
    /// <returns>64 位指纹</returns>
    public ulong GenerationSignature()
    {
        var data = Normalize();
        var hash = 14695981039346656037UL;
        // 光栅化算法变化时提升版本号，旧场景里的隐藏负载会按新算法重建，避免留下断开的旧体素
        MixInt(ref hash, RasterVersion);
        MixInt(ref hash, data.Points.Length);
        foreach (var point in data.Points)
        {
            MixFloat(ref hash, point.Position.X); MixFloat(ref hash, point.Position.Y); MixFloat(ref hash, point.Position.Z);
            MixFloat(ref hash, point.TangentIn.X); MixFloat(ref hash, point.TangentIn.Y); MixFloat(ref hash, point.TangentIn.Z);
            MixFloat(ref hash, point.TangentOut.X); MixFloat(ref hash, point.TangentOut.Y); MixFloat(ref hash, point.TangentOut.Z);
            MixFloat(ref hash, point.Rotation.X); MixFloat(ref hash, point.Rotation.Y);
            MixFloat(ref hash, point.Rotation.Z); MixFloat(ref hash, point.Rotation.W);
            MixFloat(ref hash, point.Width);
        }

        MixInt(ref hash, data.Closed ? 1 : 0);
        MixInt(ref hash, (int)data.Interpolation);
        MixInt(ref hash, data.Resolution);
        MixFloat(ref hash, data.Step);
        MixInt(ref hash, (int)data.Shape);
        MixFloat(ref hash, data.Radius);
        MixInt(ref hash, data.ColorR); MixInt(ref hash, data.ColorG);
        MixInt(ref hash, data.ColorB); MixInt(ref hash, data.ColorA);
        MixInt(ref hash, data.Emissive ? 1 : 0);
        MixInt(ref hash, data.MaterialSlot);
        return hash;

        static void MixFloat(ref ulong hash, float value) => MixInt(ref hash, BitConverter.SingleToInt32Bits(value));

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
/// 曲线渲染器组件在场景 JSON 里的字段读写
/// </summary>
/// <remarks>
/// 这里手写读写而不是直接用默认序列化：点数组的键名保持短小，未知字段在写回时保留，
/// 生成体素的隐藏负载与参数写入互不覆盖
/// </remarks>
internal static class CurveRendererJson
{
    internal const string PointsName = "points";
    internal const string ClosedName = "closed";
    internal const string InterpolationName = "interpolation";
    internal const string ResolutionName = "resolution";
    internal const string StepName = "step";
    internal const string ShapeName = "shape";
    internal const string RadiusName = "radius";
    internal const string ColorRName = "colorR";
    internal const string ColorGName = "colorG";
    internal const string ColorBName = "colorB";
    internal const string ColorAName = "colorA";
    internal const string EmissiveName = "emissive";
    internal const string MaterialSlotName = "materialSlot";
    internal const string SortingLayerName = "sortingLayer";
    internal const string SortingOrderName = "sortingOrder";
    internal const string EditingName = "editing";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    /// <summary>
    /// 读取组件负载，缺失或损坏的字段回落到默认值
    /// </summary>
    /// <param name="data">组件 JSON</param>
    /// <returns>规范化后的曲线数据</returns>
    internal static CurveRendererComponentData Read(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return new CurveRendererComponentData([]);
        var points = new List<CurvePointData>();
        if (data.TryGetProperty(PointsName, out var pointArray) && pointArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pointArray.EnumerateArray())
            {
                if (points.Count >= CurveRendererComponentData.MaximumPointCount) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                points.Add(new CurvePointData(
                    new Vector3(Float(item, "x", 0F), Float(item, "y", 0F), Float(item, "z", 0F)),
                    new Vector3(Float(item, "inX", 0F), Float(item, "inY", 0F), Float(item, "inZ", 0F)),
                    new Vector3(Float(item, "outX", 0F), Float(item, "outY", 0F), Float(item, "outZ", 0F)),
                    new Quaternion(Float(item, "rx", 0F), Float(item, "ry", 0F), Float(item, "rz", 0F),
                        Float(item, "rw", 1F)),
                    Int(item, "mode", 0) is var mode && Enum.IsDefined((CurveTangentMode)mode)
                        ? (CurveTangentMode)mode : CurveTangentMode.Auto,
                    Float(item, "width", 1F)));
            }
        }

        return new CurveRendererComponentData(
            points.ToArray(),
            Bool(data, ClosedName, false),
            Int(data, InterpolationName, 0) is var interpolation && Enum.IsDefined((CurveInterpolation)interpolation)
                ? (CurveInterpolation)interpolation : CurveInterpolation.Bezier,
            Int(data, ResolutionName, 16),
            Float(data, StepName, 0.5F),
            Int(data, ShapeName, (int)CurveBrushShape.Round) is var shape && Enum.IsDefined((CurveBrushShape)shape)
                ? (CurveBrushShape)shape : CurveBrushShape.Round,
            Float(data, RadiusName, 2F),
            (byte)Math.Clamp(Int(data, ColorRName, 255), 0, 255),
            (byte)Math.Clamp(Int(data, ColorGName, 255), 0, 255),
            (byte)Math.Clamp(Int(data, ColorBName, 255), 0, 255),
            (byte)Math.Clamp(Int(data, ColorAName, 255), 0, 255),
            Bool(data, EmissiveName, false),
            (ushort)Math.Clamp(Int(data, MaterialSlotName, 0), 0, ushort.MaxValue),
            String(data, SortingLayerName, "默认"),
            Int(data, SortingOrderName, 0),
            Bool(data, EditingName, false)).Normalize();
    }

    /// <summary>
    /// 把曲线数据合并写回组件，保留生成体素负载与未知字段
    /// </summary>
    /// <param name="component">目标组件记录</param>
    /// <param name="data">新的曲线数据</param>
    internal static void Write(ComponentRecord component, CurveRendererComponentData data)
    {
        var normalized = data.Normalize();
        var points = new JsonArray();
        foreach (var point in normalized.Points)
            points.Add(new JsonObject
            {
                ["x"] = point.Position.X, ["y"] = point.Position.Y, ["z"] = point.Position.Z,
                ["inX"] = point.TangentIn.X, ["inY"] = point.TangentIn.Y, ["inZ"] = point.TangentIn.Z,
                ["outX"] = point.TangentOut.X, ["outY"] = point.TangentOut.Y, ["outZ"] = point.TangentOut.Z,
                ["rx"] = point.Rotation.X, ["ry"] = point.Rotation.Y,
                ["rz"] = point.Rotation.Z, ["rw"] = point.Rotation.W,
                ["mode"] = (int)point.TangentMode, ["width"] = point.Width,
            });
        var merged = component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject() : new JsonObject();
        merged[PointsName] = points;
        merged[ClosedName] = normalized.Closed;
        merged[InterpolationName] = (int)normalized.Interpolation;
        merged[ResolutionName] = normalized.Resolution;
        merged[StepName] = normalized.Step;
        merged[ShapeName] = (int)normalized.Shape;
        merged[RadiusName] = normalized.Radius;
        merged[ColorRName] = normalized.ColorR;
        merged[ColorGName] = normalized.ColorG;
        merged[ColorBName] = normalized.ColorB;
        merged[ColorAName] = normalized.ColorA;
        merged[EmissiveName] = normalized.Emissive;
        merged[MaterialSlotName] = normalized.MaterialSlot;
        merged[SortingLayerName] = normalized.SortingLayer;
        merged[SortingOrderName] = normalized.SortingOrder;
        merged[EditingName] = normalized.Editing;
        component.Data = JsonSerializer.SerializeToElement(merged, Options);
    }

    /// <summary>
    /// 只读取渲染排序字段，用于合成时判断显示优先级
    /// </summary>
    /// <param name="data">组件 JSON</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    internal static void ReadSorting(JsonElement data, out string sortingLayer, out int sortingOrder)
    {
        sortingLayer = "默认";
        sortingOrder = 0;
        if (data.ValueKind != JsonValueKind.Object) return;
        sortingLayer = String(data, SortingLayerName, "默认");
        if (string.IsNullOrWhiteSpace(sortingLayer)) sortingLayer = "默认";
        sortingOrder = Int(data, SortingOrderName, 0);
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
