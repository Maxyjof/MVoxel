namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>世界分区：山体按方位与高度切分，云海单独一块，便于分件与图层管理</summary>
internal enum XianxiaRegion
{
    /// <summary>云海</summary>
    CloudSea = 0,

    /// <summary>山巅与中州台地</summary>
    Summit = 1,

    /// <summary>东崖</summary>
    East = 2,

    /// <summary>南麓</summary>
    South = 3,

    /// <summary>西崖</summary>
    West = 4,

    /// <summary>北麓</summary>
    North = 5,
}

/// <summary>地标图层，与场景 LayerNames 一一对应</summary>
internal enum XianxiaLayer
{
    /// <summary>山体</summary>
    Terrain = 1,

    /// <summary>水系</summary>
    Water = 2,

    /// <summary>生态</summary>
    Ecology = 3,

    /// <summary>宗门</summary>
    Sect = 4,

    /// <summary>城市</summary>
    City = 5,

    /// <summary>浮空</summary>
    Sky = 6,

    /// <summary>遗迹</summary>
    Ruin = 7,

    /// <summary>氛围</summary>
    Atmosphere = 8,
}

/// <summary>
/// 地标基址：山体生成时把这一圈削成平台，
/// 建筑随后以平台高程为地面独立建造
/// </summary>
/// <param name="Name">基址名</param>
/// <param name="X">世界 X</param>
/// <param name="Z">世界 Z</param>
/// <param name="Radius">平台半径</param>
/// <param name="Height">平台高程</param>
/// <param name="Layer">所属图层</param>
internal readonly record struct XianxiaSite(
    string Name, int X, int Z, int Radius, int Height, XianxiaLayer Layer);

/// <summary>
/// 仙峰地理骨架：云海高度、主峰轮廓、十三处基址、三座浮岛与瀑布位置
/// </summary>
internal static class XianxiaGeography
{
    /// <summary>世界 X 与 Z 方向尺寸</summary>
    public const int Size = 1024;

    /// <summary>大陆几何中心，也是主峰中心</summary>
    public const int Center = 512;

    /// <summary>云海表面高度</summary>
    public const int CloudLevel = 118;

    /// <summary>主峰中心 X</summary>
    public const int SummitX = 512;

    /// <summary>主峰中心 Z</summary>
    public const int SummitZ = 500;

    /// <summary>山体半径，超出后只剩云海</summary>
    public const float MassifRadius = .78F;

    /// <summary>全部地标基址</summary>
    public static readonly XianxiaSite[] Sites =
    [
        new("青云剑宗", 512, 500, 108, 508, XianxiaLayer.Sect),
        new("藏经阁", 376, 556, 40, 452, XianxiaLayer.Sect),
        new("演武场", 648, 594, 56, 444, XianxiaLayer.Sect),
        new("山门坊", 512, 704, 42, 344, XianxiaLayer.Sect),
        new("长乐坊市", 640, 780, 88, 300, XianxiaLayer.City),
        new("云海渡口", 892, 566, 46, 128, XianxiaLayer.City),
        new("灵田梯田", 372, 676, 64, 330, XianxiaLayer.City),
        new("剑冢", 704, 436, 54, 398, XianxiaLayer.Ruin),
        new("锁妖塔", 748, 724, 46, 330, XianxiaLayer.Ruin),
        new("传送大阵", 470, 700, 42, 428, XianxiaLayer.Ruin),
        new("古战场", 386, 772, 50, 336, XianxiaLayer.Ruin),
        new("世界树", 330, 442, 54, 404, XianxiaLayer.Ecology),
        new("望海亭", 762, 420, 28, 232, XianxiaLayer.City),
    ];

    /// <summary>玉京浮岛中心</summary>
    public static readonly (int X, int Y, int Z) JadeCapital = (306, 470, 316);

    /// <summary>扶摇浮岛中心</summary>
    public static readonly (int X, int Y, int Z) FuYaoIsle = (708, 404, 350);

    /// <summary>听雨浮岛中心</summary>
    public static readonly (int X, int Y, int Z) TingYuIsle = (338, 352, 716);

    /// <summary>
    /// 悬空瀑布：起点、顶端高度、落深与水平外飘方向
    /// </summary>
    /// <param name="X">起点 X</param>
    /// <param name="Z">起点 Z</param>
    /// <param name="TopY">顶端高度</param>
    /// <param name="Drop">落深</param>
    /// <param name="DriftX">水平漂移 X</param>
    /// <param name="DriftZ">水平漂移 Z</param>
    public readonly record struct XianxiaWaterfall(
        int X, int Z, int TopY, int Drop, float DriftX, float DriftZ);

    /// <summary>五条悬空瀑布，分别从三处台地与两处崖口落下</summary>
    public static readonly XianxiaWaterfall[] Waterfalls =
    [
        new(576, 560, 440, 300, 46F, 34F),
        new(470, 552, 470, 330, -52F, 26F),
        new(560, 648, 350, 220, 40F, 52F),
        new(392, 512, 400, 260, -58F, 10F),
        new(660, 592, 322, 190, 52F, 40F),
    ];

    /// <summary>
    /// 判定一列所属分区，山体以外的云海单独成块
    /// </summary>
    /// <param name="x">世界 X</param>
    /// <param name="z">世界 Z</param>
    /// <param name="height">该列山体高度，云海列传 0</param>
    /// <returns>分区</returns>
    public static XianxiaRegion Classify(int x, int z, int height)
    {
        if (height <= CloudLevel + 3) return XianxiaRegion.CloudSea;
        var dx = x - Center;
        var dz = z - Center;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance < 250F && height > 420) return XianxiaRegion.Summit;
        var angle = MathF.Atan2(dz, dx) * 180F / MathF.PI;
        return angle switch
        {
            >= -45F and < 45F => XianxiaRegion.East,
            >= 45F and < 135F => XianxiaRegion.South,
            >= 135F or < -135F => XianxiaRegion.West,
            _ => XianxiaRegion.North,
        };
    }

    /// <summary>由地形生成器解析出的逐基址平台高程</summary>
    private static int[] _plateauLevels = [];

    /// <summary>
    /// 记录各处平台的实际高程，平台高度由原始山体在该圈内的平均高度决定
    /// </summary>
    /// <param name="levels">与 <see cref="Sites"/> 等长的高程数组</param>
    public static void SetPlateauLevels(int[] levels)
    {
        _plateauLevels = levels;
    }

    /// <summary>
    /// 读取某处基址的平台高程，未解析时回退到声明值
    /// </summary>
    /// <param name="name">基址名</param>
    /// <returns>平台高程</returns>
    public static int SiteLevel(string name)
    {
        for (var index = 0; index < Sites.Length; index++)
        {
            if (Sites[index].Name != name) continue;
            return index < _plateauLevels.Length ? _plateauLevels[index] : Sites[index].Height;
        }

        return CloudLevel;
    }

    /// <summary>
    /// 把地标基址压平成平台，边缘用宽过渡带保证不会出现刀切状断层
    /// </summary>
    /// <param name="x">世界 X</param>
    /// <param name="z">世界 Z</param>
    /// <param name="baseHeight">原始山体高度</param>
    /// <param name="siteHeights">逐基址平台高程，可为空</param>
    /// <returns>压平后的高度</returns>
    public static int Flatten(int x, int z, int baseHeight, int[]? siteHeights = null)
    {
        var result = baseHeight;
        for (var index = 0; index < Sites.Length; index++)
        {
            var site = Sites[index];
            var dx = x - site.X;
            var dz = z - site.Z;
            var angle = MathF.Atan2(dz, dx);
            // 平台轮廓加角度扰动，避免每处地标都变成一个标准圆盘
            var wobble = 1F + .2F * MathF.Sin(angle * 3F + index * 1.7F)
                             + .1F * MathF.Sin(angle * 5F + index * .9F);
            var distance = MathF.Sqrt(dx * dx + dz * dz) / wobble;
            if (distance > site.Radius * 1.8F) continue;
            var level = siteHeights is not null && index < siteHeights.Length ? siteHeights[index] : site.Height;
            var weight = 1F - XianxiaNoise.SmoothStep(site.Radius, site.Radius * 1.8F, distance);
            result = (int)MathF.Round(result + (level - result) * weight);
        }

        return result;
    }
}
