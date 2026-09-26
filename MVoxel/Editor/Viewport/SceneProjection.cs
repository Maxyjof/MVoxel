using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 场景视图的观察相机（编辑器会话状态，不写入场景数据）：
/// 位置、目标与一组正交基
/// </summary>
/// <param name="Position">相机世界位置</param>
/// <param name="Target">注视点</param>
/// <param name="Forward">视线单位向量</param>
/// <param name="Right">右方向单位向量</param>
/// <param name="Up">上方向单位向量</param>
internal readonly record struct SceneCamera(Vector3 Position, Vector3 Target, Vector3 Forward, Vector3 Right, Vector3 Up)
{
    /// <summary>
    /// 近平面距离
    /// </summary>
    public float NearClip { get; init; } = SceneProjection.Near;

    /// <summary>
    /// 远平面距离
    /// </summary>
    public float FarClip { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// 视场角，零值表示使用兼容默认焦距
    /// </summary>
    public float FieldOfViewDegrees { get; init; }
}

/// <summary>
/// 场景视图的针孔投影与裁剪，网格、坐标轴、GPU 着色器与鼠标拾取共用
/// 同一套约定：屏幕坐标自上而下，焦距以像素计
/// </summary>
internal static class SceneProjection
{
    /// <summary>
    /// 近平面距离，同时充当数值下限防止除零
    /// </summary>
    internal const float Near = 0.05F;

    /// <summary>
    /// 视口对应的焦距（像素），随窗口尺寸自适应，保证不同大小下视野一致
    /// </summary>
    /// <param name="size">视口逻辑尺寸</param>
    /// <returns>焦距像素值</returns>
    internal static float FocalLength(Vector2 size) => Math.Min(size.X, size.Y) * 0.95F;

    /// <summary>
    /// 根据相机视场角计算焦距
    /// </summary>
    /// <param name="size">视口逻辑尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>焦距像素值</returns>
    internal static float FocalLength(Vector2 size, SceneCamera camera)
    {
        if (camera.FieldOfViewDegrees <= 0F)
        {
            return FocalLength(size);
        }

        var fieldOfView = Math.Clamp(camera.FieldOfViewDegrees, 20F, 120F);
        var focal = size.Y / (2F * MathF.Tan(fieldOfView * MathF.PI / 360F));
        return float.IsFinite(focal) && focal > 1F ? focal : FocalLength(size);
    }

    /// <summary>
    /// 内部图像缩放时同步缩放焦距，保证降分辨率不改变视场角。
    /// </summary>
    internal static float FocalLengthForOutput(Vector2 logicalSize, SceneCamera camera, int outputHeight)
    {
        return FocalLength(logicalSize, camera) * Math.Max(outputHeight, 1) /
               MathF.Max(logicalSize.Y, 1F);
    }

    /// <summary>
    /// 先在相机空间裁剪线段再做透视除法。仅因某个端点在相机背后就整条丢弃
    /// 会让脚下的地面网格凭空消失，所以必须保留可见部分
    /// </summary>
    /// <param name="start">世界起点</param>
    /// <param name="end">世界终点</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="a">裁剪后端点 A 的屏幕坐标</param>
    /// <param name="b">裁剪后端点 B 的屏幕坐标</param>
    /// <returns>线段是否有可见部分</returns>
    internal static bool ClipLine(Vector3 start, Vector3 end, Vector2 origin, Vector2 size,
        SceneCamera camera, out Vector2 a, out Vector2 b)
    {
        a = b = default;
        if (size.X <= 0 || size.Y <= 0) return false;
        var p = ToCamera(start, camera);
        var q = ToCamera(end, camera);
        var near = NearPlane(camera);
        var far = FarPlane(camera);
        var focal = FocalLength(size, camera);
        var xSlope = size.X / (2F * focal);
        var ySlope = size.Y / (2F * focal);
        var lower = 0F;
        var upper = 1F;
        if (!ClipPlane(p.Z - near, q.Z - near, ref lower, ref upper) ||
            !ClipPlane(far - p.Z, far - q.Z, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope + p.X, q.Z * xSlope + q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope - p.X, q.Z * xSlope - q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope + p.Y, q.Z * ySlope + q.Y, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope - p.Y, q.Z * ySlope - q.Y, ref lower, ref upper)) return false;
        a = ProjectCamera(p + (q - p) * lower, origin, size, camera);
        b = ProjectCamera(p + (q - p) * upper, origin, size, camera);
        return float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(b.X) && float.IsFinite(b.Y);
    }

    /// <summary>
    /// 裁剪线段并同时返回裁剪后的世界端点
    /// </summary>
    /// <param name="start">世界起点</param>
    /// <param name="end">世界终点</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="clippedStart">裁剪后的世界起点</param>
    /// <param name="clippedEnd">裁剪后的世界终点</param>
    /// <param name="a">裁剪后的屏幕起点</param>
    /// <param name="b">裁剪后的屏幕终点</param>
    /// <returns>线段是否有可见部分</returns>
    internal static bool ClipLineWithWorld(Vector3 start, Vector3 end, Vector2 origin, Vector2 size,
        SceneCamera camera, out Vector3 clippedStart, out Vector3 clippedEnd, out Vector2 a, out Vector2 b)
    {
        clippedStart = clippedEnd = default;
        a = b = default;
        if (size.X <= 0 || size.Y <= 0) return false;
        var p = ToCamera(start, camera);
        var q = ToCamera(end, camera);
        var near = NearPlane(camera);
        var far = FarPlane(camera);
        var focal = FocalLength(size, camera);
        var xSlope = size.X / (2F * focal);
        var ySlope = size.Y / (2F * focal);
        var lower = 0F;
        var upper = 1F;
        if (!ClipPlane(p.Z - near, q.Z - near, ref lower, ref upper) ||
            !ClipPlane(far - p.Z, far - q.Z, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope + p.X, q.Z * xSlope + q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope - p.X, q.Z * xSlope - q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope + p.Y, q.Z * ySlope + q.Y, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope - p.Y, q.Z * ySlope - q.Y, ref lower, ref upper)) return false;

        clippedStart = start + (end - start) * lower;
        clippedEnd = start + (end - start) * upper;
        a = ProjectCamera(p + (q - p) * lower, origin, size, camera);
        b = ProjectCamera(p + (q - p) * upper, origin, size, camera);
        return float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(b.X) && float.IsFinite(b.Y);
    }

    /// <summary>
    /// 投影一个世界点到屏幕
    /// </summary>
    /// <param name="world">世界坐标</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="screen">投影后的屏幕坐标</param>
    /// <param name="depth">相机空间深度</param>
    /// <returns>点是否在近平面前方且投影有限</returns>
    internal static bool Project(Vector3 world, Vector2 origin, Vector2 size, SceneCamera camera,
        out Vector2 screen, out float depth)
    {
        var p = ToCamera(world, camera);
        depth = p.Z;
        screen = default;
        if (depth < NearPlane(camera) || depth > FarPlane(camera)) return false;
        screen = ProjectCamera(p, origin, size, camera);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>
    /// 由屏幕点反算世界空间单位射线，作为 Project 的精确逆运算
    /// 地面网格覆盖范围来自真实视锥与地面的交点
    /// </summary>
    /// <param name="screen">屏幕坐标</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>单位方向射线</returns>
    internal static Vector3 RayFromScreen(Vector2 screen, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var focal = FocalLength(size, camera);
        var offset = (screen - origin - size / 2F) / Math.Max(1F, focal);
        var direction = camera.Forward + camera.Right * offset.X - camera.Up * offset.Y;
        return direction.LengthSquared > 0.000001F ? Vector3.Normalize(direction) : camera.Forward;
    }

    private static Vector3 ToCamera(Vector3 world, SceneCamera camera)
    {
        var p = world - camera.Position;
        return new Vector3(Vector3.Dot(p, camera.Right), Vector3.Dot(p, camera.Up), Vector3.Dot(p, camera.Forward));
    }

    private static Vector2 ProjectCamera(Vector3 p, Vector2 origin, Vector2 size, SceneCamera camera) =>
        origin + size / 2F + new Vector2(p.X, -p.Y) * (FocalLength(size, camera) /
            Math.Max(NearPlane(camera), p.Z));

    private static float NearPlane(SceneCamera camera) =>
        float.IsFinite(camera.NearClip) ? Math.Clamp(camera.NearClip, 0.001F, 1_000_000F) : Near;

    private static float FarPlane(SceneCamera camera)
    {
        var near = NearPlane(camera);
        if (!float.IsFinite(camera.FarClip)) return float.PositiveInfinity;
        return Math.Max(near + 0.001F, Math.Clamp(camera.FarClip, near + 0.001F, 100_000_000F));
    }

    private static bool ClipPlane(float p, float q, ref float lower, ref float upper)
    {
        if (p < 0 && q < 0) return false;
        if (p >= 0 && q >= 0) return true;
        var t = p / (p - q);
        if (p < 0) lower = Math.Max(lower, t);
        else upper = Math.Min(upper, t);
        return lower <= upper;
    }
}
