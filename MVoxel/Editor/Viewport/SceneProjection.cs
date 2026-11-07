using MVoxel.Voxels;
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

    /// <summary>把世界点转换到相机空间，供需要自行裁剪的调用方使用</summary>
    /// <param name="world">世界坐标</param>
    /// <param name="camera">观察相机</param>
    /// <returns>相机空间坐标，Z 为正向深度</returns>
    internal static Vector3 ToCameraSpace(Vector3 world, SceneCamera camera) => ToCamera(world, camera);

    /// <summary>把相机空间点投影到屏幕；调用方需保证该点已在近平面之前</summary>
    /// <param name="cameraSpace">相机空间坐标</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>屏幕坐标</returns>
    internal static Vector2 ProjectCameraSpace(Vector3 cameraSpace, Vector2 origin, Vector2 size, SceneCamera camera) =>
        ProjectCamera(cameraSpace, origin, size, camera);

    /// <summary>读取相机近平面距离</summary>
    /// <param name="camera">观察相机</param>
    /// <returns>近平面距离</returns>
    internal static float NearPlaneOf(SceneCamera camera) => NearPlane(camera);

    /// <summary>
    /// 按近平面裁剪相机空间多边形，保留可见部分。
    /// CPU 预览不能因为某一个顶点在相机背后就整面丢弃，否则贴近模型时会出现缺块
    /// </summary>
    /// <param name="polygon">相机空间顶点，按顺序构成凸多边形</param>
    /// <param name="near">近平面距离</param>
    /// <returns>裁剪后的凸多边形，完全不可见时为空</returns>
    internal static List<Vector3> ClipPolygonNearPlane(IReadOnlyList<Vector3> polygon, float near)
    {
        var result = new List<Vector3>(polygon.Count + 2);
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            var currentInside = current.Z >= near;
            var nextInside = next.Z >= near;
            if (currentInside) result.Add(current);
            if (currentInside == nextInside) continue;
            var t = (near - current.Z) / (next.Z - current.Z);
            if (!float.IsFinite(t)) continue;
            result.Add(current + (next - current) * t);
        }

        return result;
    }

    /// <summary>
    /// 由屏幕点反算世界空间单位射线，作为 Project 的精确逆运算
    /// 地面网格覆盖范围来自真实视锥与地面的交点
    /// </summary>    /// <param name="screen">屏幕坐标</param>
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

    /// <summary>
    /// 生成场景视口的区块可见性过滤器：按相机视锥剔除整块落在视锥外的区块。
    /// 区块包围盒与视锥都做外扩，保证相机位姿量化重建时不会漏掉边缘区块
    /// </summary>
    /// <param name="camera">观察相机</param>
    /// <param name="size">视口逻辑尺寸</param>
    /// <param name="expandDegrees">视锥半角外扩角度，覆盖相机位姿量化误差</param>
    /// <param name="margin">区块包围盒外扩距离（格）</param>
    /// <returns>区块坐标谓词，true 表示参与渲染</returns>
    internal static Func<ChunkCoord64, bool> CreateChunkFilter(SceneCamera camera, Vector2 size,
        float expandDegrees = 10F, float margin = VoxelWorld.ChunkSize)
    {
        var focal = FocalLength(size, camera);
        var xSlope = size.X / (2F * Math.Max(1F, focal));
        var ySlope = size.Y / (2F * Math.Max(1F, focal));
        if (expandDegrees > 0F)
        {
            var extra = MathF.Tan(Math.Clamp(expandDegrees, 0F, 45F) * MathF.PI / 180F);
            xSlope += extra;
            ySlope += extra;
        }

        var near = NearPlane(camera) - margin;
        var far = FarPlane(camera);
        var position = camera.Position;
        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        var half = VoxelWorld.ChunkSize * 0.5F;
        var rightExtent = half * (MathF.Abs(right.X) + MathF.Abs(right.Y) + MathF.Abs(right.Z));
        var upExtent = half * (MathF.Abs(up.X) + MathF.Abs(up.Y) + MathF.Abs(up.Z));
        var forwardExtent = half * (MathF.Abs(forward.X) + MathF.Abs(forward.Y) + MathF.Abs(forward.Z));
        return coordinate =>
        {
            var baseX = (float)((double)coordinate.X * VoxelWorld.ChunkSize);
            var baseY = (float)((double)coordinate.Y * VoxelWorld.ChunkSize);
            var baseZ = (float)((double)coordinate.Z * VoxelWorld.ChunkSize);
            var dx = baseX + half - position.X;
            var dy = baseY + half - position.Y;
            var dz = baseZ + half - position.Z;
            var x = dx * right.X + dy * right.Y + dz * right.Z;
            var y = dx * up.X + dy * up.Y + dz * up.Z;
            var z = dx * forward.X + dy * forward.Y + dz * forward.Z;
            var xMin = x - rightExtent - margin;
            var xMax = x + rightExtent + margin;
            var yMin = y - upExtent - margin;
            var yMax = y + upExtent + margin;
            var zMin = z - forwardExtent - margin;
            var zMax = z + forwardExtent + margin;
            if (zMax < near || zMin > far) return false;
            if (xMin - zMax * xSlope >= 0F) return false;
            if (xMax + zMax * xSlope <= 0F) return false;
            if (yMin - zMax * ySlope >= 0F) return false;
            if (yMax + zMax * ySlope <= 0F) return false;
            return true;
        };
    }

    /// <summary>
    /// 生成渲染视口的保守区块过滤器：主可见范围以渲染相机远裁剪面为界，
    /// 并沿所有方向外扩同一距离，保留视锥外可能投射阴影或参与反射的区块。
    /// 远裁剪面无限或相机参数无效时返回 null，退回完整世界，不冒缺块风险
    /// </summary>
    /// <param name="camera">渲染相机，视场角应与实际渲染使用的值一致</param>
    /// <param name="size">渲染视口逻辑尺寸</param>
    /// <returns>区块坐标谓词，null 表示保留完整世界</returns>
    internal static Func<ChunkCoord64, bool>? CreateRenderChunkFilter(SceneCamera camera, Vector2 size)
    {
        if (size.X <= 0F || size.Y <= 0F) return null;
        var far = FarPlane(camera);
        if (!float.IsFinite(far)) return null;
        // 外扩距离取远裁剪面：可见范围外的阴影投射体、地面阴影和反射源
        // 只要仍在远平面附近就继续驻留，避免实时画面出现缺块
        var contribution = Math.Clamp(far, VoxelWorld.ChunkSize, 10_000_000F);
        return CreateChunkFilter(camera, size, 10F, contribution);
    }

    /// <summary>
    /// 区块可见性重建签名：位置按区块尺寸、朝向按固定角度量化，
    /// 外扩过滤器覆盖量化误差，避免每次微小移动都重建工作集
    /// </summary>
    /// <param name="camera">观察相机</param>
    /// <param name="size">视口逻辑尺寸</param>
    /// <returns>非零签名，用于工作集缓存比较</returns>
    internal static long ChunkVisibilitySignature(SceneCamera camera, Vector2 size)
    {
        const float positionStep = VoxelWorld.ChunkSize;
        const float directionStepDegrees = 10F;
        var directionScale = 1F / MathF.Max(0.0001F, MathF.Sin(directionStepDegrees * MathF.PI / 180F));
        var px = (long)MathF.Round(camera.Position.X / positionStep);
        var py = (long)MathF.Round(camera.Position.Y / positionStep);
        var pz = (long)MathF.Round(camera.Position.Z / positionStep);
        var fx = (long)MathF.Round(camera.Forward.X * directionScale);
        var fy = (long)MathF.Round(camera.Forward.Y * directionScale);
        var fz = (long)MathF.Round(camera.Forward.Z * directionScale);
        var width = (long)MathF.Round(size.X);
        var height = (long)MathF.Round(size.Y);
        // 视场角与裁剪面参与视锥形状，变化时必须重建工作集，否则旧过滤会漏掉新进入画面的区块
        var projection = HashCode.Combine(width, height,
            (long)MathF.Round(camera.FieldOfViewDegrees * 10F),
            MathF.Round(NearPlane(camera) * 100F), MathF.Round(FarPlane(camera) * 100F));
        var hash = HashCode.Combine(px, py, pz, fx, fy, fz, projection);
        return 1L | ((long)hash << 1);
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
