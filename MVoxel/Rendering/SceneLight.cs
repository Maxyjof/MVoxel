using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>
/// 场景本地光源类型
/// </summary>
public enum SceneLightKind
{
    /// <summary>
    /// 方向光，只使用方向，不受位置与范围影响
    /// </summary>
    Directional = 0,

    /// <summary>
    /// 点光，按距离平方衰减并在范围边界平滑截断
    /// </summary>
    Point = 1,

    /// <summary>
    /// 聚光，在点光基础上按内外角做圆锥衰减
    /// </summary>
    Spot = 2,
}

/// <summary>
/// 一个场景本地光源的不可变数据
/// </summary>
/// <remarks>
/// Direction 表示光线传播方向，也就是光源朝向，与 RenderSettings.SunDirection
/// 的“从表面指向太阳”方向相反，着色器会取反后再做 N·L
/// 所有字段在进入渲染管线前都会经过 <see cref="Clamped"/> 夹取，
/// 损坏或极端的数值不会把着色器推出合法范围
/// </remarks>
/// <param name="Position">光源位置，方向光忽略此值</param>
/// <param name="Direction">光线传播方向，点光忽略此值</param>
/// <param name="Color">光源颜色（线性），每通道夹取到 0 到 4</param>
/// <param name="Intensity">光源强度，夹取到 0 到 1000</param>
/// <param name="Range">点光与聚光的有效范围（格），夹取到 0.01 到 100000</param>
/// <param name="SpotAngleDegrees">聚光外角（度），夹取到 0 到 179</param>
/// <param name="InnerSpotAngleDegrees">聚光内角（度），夹取到 0 到外角</param>
/// <param name="Kind">光源类型</param>
/// <param name="Enabled">是否启用，只有启用的光源参与着色</param>
/// <param name="CastShadows">是否投射阴影，当前渲染管线尚未消费此字段</param>
public readonly record struct SceneLight(
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float Range,
    float SpotAngleDegrees,
    float InnerSpotAngleDegrees,
    SceneLightKind Kind = SceneLightKind.Point,
    bool Enabled = true,
    bool CastShadows = false)
{
    /// <summary>
    /// 渲染管线支持的本地光源数量上限
    /// </summary>
    public const int Capacity = 32;

    /// <summary>
    /// 强度上限
    /// </summary>
    public const float MaxIntensity = 1000F;

    /// <summary>
    /// 范围下限（格）
    /// </summary>
    public const float MinRange = 0.01F;

    /// <summary>
    /// 范围上限（格）
    /// </summary>
    public const float MaxRange = 100_000F;

    /// <summary>
    /// 聚光外角上限（度）
    /// </summary>
    public const float MaxSpotAngleDegrees = 179F;

    /// <summary>
    /// 是否在渲染中生效：已启用且强度大于 0
    /// </summary>
    public bool IsActive => Enabled && Intensity > 0F;

    /// <summary>
    /// 返回夹取后的副本，非有限值按安全默认值处理
    /// </summary>
    /// <returns>字段全部合法的光源</returns>
    public SceneLight Clamped()
    {
        var color = ClampColor(Color);
        var intensity = ClampIntensity(Intensity);
        var range = ClampRange(Range);
        var spotAngle = ClampSpotAngle(SpotAngleDegrees);
        var innerSpot = Math.Min(ClampSpotAngle(InnerSpotAngleDegrees), spotAngle);
        var kind = Enum.IsDefined(Kind) ? Kind : SceneLightKind.Point;
        return new SceneLight(SanitizePosition(Position), NormalizeDirection(Direction),
            color, intensity, range, spotAngle, innerSpot, kind, Enabled, CastShadows);
    }

    /// <summary>
    /// 创建方向光，方向为光线传播方向
    /// </summary>
    /// <param name="direction">光线传播方向</param>
    /// <param name="color">光照颜色（线性）</param>
    /// <param name="intensity">光照强度</param>
    /// <param name="enabled">是否启用</param>
    /// <returns>方向光</returns>
    public static SceneLight CreateDirectional(Vector3 direction, Vector3 color, float intensity,
        bool enabled = true) => new(Vector3.Zero, direction, color, intensity, MaxRange, 0F, 0F,
            SceneLightKind.Directional, enabled);

    /// <summary>
    /// 创建点光
    /// </summary>
    /// <param name="position">光源位置</param>
    /// <param name="color">光照颜色（线性）</param>
    /// <param name="intensity">光照强度</param>
    /// <param name="range">有效范围（格）</param>
    /// <param name="enabled">是否启用</param>
    /// <returns>点光</returns>
    public static SceneLight CreatePoint(Vector3 position, Vector3 color, float intensity, float range,
        bool enabled = true) => new(position, new Vector3(0F, -1F, 0F), color, intensity,
            range, 0F, 0F, SceneLightKind.Point, enabled);

    /// <summary>
    /// 创建聚光
    /// </summary>
    /// <param name="position">光源位置</param>
    /// <param name="direction">光线传播方向</param>
    /// <param name="color">光照颜色（线性）</param>
    /// <param name="intensity">光照强度</param>
    /// <param name="range">有效范围（格）</param>
    /// <param name="spotAngleDegrees">外角（度）</param>
    /// <param name="innerSpotAngleDegrees">内角（度）</param>
    /// <param name="enabled">是否启用</param>
    /// <returns>聚光</returns>
    public static SceneLight CreateSpot(Vector3 position, Vector3 direction, Vector3 color,
        float intensity, float range, float spotAngleDegrees, float innerSpotAngleDegrees,
        bool enabled = true) => new(position, direction, color, intensity, range,
            spotAngleDegrees, innerSpotAngleDegrees, SceneLightKind.Spot, enabled);

    /// <summary>
    /// 把颜色每通道夹取到 0 到 4，非有限值按 0 处理
    /// </summary>
    /// <param name="color">待夹取颜色</param>
    /// <returns>夹取后的线性颜色</returns>
    public static Vector3 ClampColor(Vector3 color) => new(
        ClampFinite(color.X, 0F, 0F, 4F),
        ClampFinite(color.Y, 0F, 0F, 4F),
        ClampFinite(color.Z, 0F, 0F, 4F));

    /// <summary>
    /// 夹取强度到 0 到 1000
    /// </summary>
    /// <param name="intensity">原始强度</param>
    /// <returns>夹取后的强度</returns>
    public static float ClampIntensity(float intensity) => ClampFinite(intensity, 0F, 0F, MaxIntensity);

    /// <summary>
    /// 夹取范围到 0.01 到 100000，非有限值落到下限
    /// </summary>
    /// <param name="range">原始范围</param>
    /// <returns>夹取后的范围</returns>
    public static float ClampRange(float range) => ClampFinite(range, MinRange, MinRange, MaxRange);

    /// <summary>
    /// 夹取聚光角度到 0 到 179
    /// </summary>
    /// <param name="degrees">原始角度（度）</param>
    /// <returns>夹取后的角度（度）</returns>
    public static float ClampSpotAngle(float degrees) => ClampFinite(degrees, 0F, 0F, MaxSpotAngleDegrees);

    /// <summary>
    /// 归一化光线方向，零向量或非有限值回退为竖直向下
    /// </summary>
    /// <param name="direction">原始方向</param>
    /// <returns>单位方向向量</returns>
    public static Vector3 NormalizeDirection(Vector3 direction)
    {
        var safe = new Vector3(
            float.IsFinite(direction.X) ? direction.X : 0F,
            float.IsFinite(direction.Y) ? direction.Y : 0F,
            float.IsFinite(direction.Z) ? direction.Z : 0F);
        var lengthSquared = safe.LengthSquared();
        return lengthSquared > 1e-12F ? safe / MathF.Sqrt(lengthSquared) : new Vector3(0F, -1F, 0F);
    }

    /// <summary>
    /// 非有限值回退，其余夹到给定区间
    /// </summary>
    /// <param name="value">原始值</param>
    /// <param name="fallback">非有限时的回退值</param>
    /// <param name="minimum">下限</param>
    /// <param name="maximum">上限</param>
    /// <returns>夹取后的有限值</returns>
    private static float ClampFinite(float value, float fallback, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    /// <summary>
    /// 位置的非有限分量按 0 处理
    /// </summary>
    /// <param name="position">原始位置</param>
    /// <returns>有限位置</returns>
    private static Vector3 SanitizePosition(Vector3 position) => new(
        float.IsFinite(position.X) ? position.X : 0F,
        float.IsFinite(position.Y) ? position.Y : 0F,
        float.IsFinite(position.Z) ? position.Z : 0F);
}
