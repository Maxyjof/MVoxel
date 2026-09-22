using ImGuiNET;
using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// CPU 地面网格绘制，按真实视锥与地面交点计算
/// 覆盖范围，远距离自动采用 2 的幂步长，近处、地平线与超大平移位置都
/// 受有界线数预算保护。GPU 管线启用后网格改在着色器内绘制以获得深度正确性，
/// 这里仅作为无计算能力时的后备
/// </summary>
internal static class SceneGrid
{
    private const float SparseGridStartDistance = 192F;
    private const float FineGridRadius = 96F;
    private const int TargetHalfLineCount = 160;
    private const int MaximumHalfLineCount = 192;

    /// <summary>
    /// 计算注视点处的基准网格步长（2 的幂）
    /// </summary>
    /// <param name="cameraDistance">观察距离</param>
    /// <param name="size">视口尺寸</param>
    /// <returns>世界单位步长</returns>
    internal static float Spacing(float cameraDistance, Vector2 size)
    {
        // 近处固定使用单体素网格，只有进入远景区间后才提升步长
        // 步长只由观察距离决定，避免窗口尺寸和视锥采样造成来回跳变
        _ = size;
        var distance = Math.Max(1F, cameraDistance);
        return distance <= SparseGridStartDistance
            ? 1F
            : NextPowerOfTwo(distance / SparseGridStartDistance);
    }

    /// <summary>
    /// 规划本帧网格：步长、覆盖半径与线数预算
    /// </summary>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="cameraDistance">观察距离</param>
    /// <returns>可直接绘制的网格计划</returns>
    internal static SceneGridPlan Plan(Vector2 origin, Vector2 size, SceneCamera camera, float cameraDistance)
    {
        var visibleExtent = EstimateVisibleGroundExtent(origin, size, camera, cameraDistance);
        var baselineStep = Spacing(cameraDistance, size);
        var coverageStep = NextPowerOfTwo(Math.Max(1F, visibleExtent / TargetHalfLineCount));
        // 线数预算不能用“裁掉远处”来满足：视口越远，必须先提升步长再扩大覆盖范围，
        // 这样远景网格会变疏，但仍然延伸到视锥可见的地面，而不是在有限矩形边缘突然消失。
        var step = Math.Max(baselineStep, coverageStep);
        var halfLineCount = Math.Clamp((int)MathF.Ceiling(visibleExtent / step) + 3, 16, MaximumHalfLineCount);
        return new SceneGridPlan(step, halfLineCount * step, halfLineCount);
    }

    /// <summary>
    /// 绘制地面网格，含主副线分层与密度淡出
    /// </summary>
    /// <param name="draw">目标 ImGui 绘制列表</param>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="distance">观察距离</param>
    internal static void Draw(ImDrawListPtr draw, Vector2 origin, Vector2 size, SceneCamera camera, float distance)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        var plan = Plan(origin, size, camera, distance);
        var extent = plan.Extent;
        // 步长只由相机距离决定，不再由低角度视锥的最远交点决定。
        // 可见范围通过同心环带逐级加粗覆盖，避免轻微转动就让整张网格换档。
        var detailStep = Spacing(distance, size);
        var baseRadius = Math.Min(FineGridRadius, extent);
        draw.PushClipRect(origin, origin + size, true);

        if (baseRadius >= 1F)
        {
            for (var level = 0; ; level++)
            {
                var step = detailStep * MathF.Pow(2F, level);
                var logicalInner = level == 0 ? 0F : baseRadius * MathF.Pow(2F, level - 1);
                var outer = Math.Min(extent, baseRadius * MathF.Pow(2F, level));
                var geometryInner = level == 0 ? 0F : logicalInner * 0.55F;
                DrawGridBand(step, geometryInner, logicalInner, outer);
                if (outer >= extent - 0.001F) break;
            }
        }
        draw.PopClipRect();

        void DrawGridBand(float lineStep, float geometryInner, float logicalInner, float outer)
        {
            if (outer <= 0.001F) return;
            var centerX = MathF.Floor(camera.Target.X / lineStep) * lineStep;
            var centerZ = MathF.Floor(camera.Target.Z / lineStep) * lineStep;
            var first = (int)MathF.Ceiling(-outer / lineStep) - 1;
            var last = (int)MathF.Floor(outer / lineStep) + 1;
            for (var index = first; index <= last; index++)
            {
                var x = centerX + index * lineStep;
                var xOffset = Math.Abs(x - camera.Target.X);
                if (xOffset <= outer)
                {
                    var outerHalf = MathF.Sqrt(Math.Max(0F, outer * outer - xOffset * xOffset));
                    var innerHalf = xOffset < geometryInner
                        ? MathF.Sqrt(Math.Max(0F, geometryInner * geometryInner - xOffset * xOffset))
                        : 0F;
                    DrawGridSegment(new Vector3(x, 0F, camera.Target.Z - outerHalf),
                        new Vector3(x, 0F, camera.Target.Z - innerHalf), lineStep,
                        IsMajor(x, lineStep), logicalInner, outer);
                    DrawGridSegment(new Vector3(x, 0F, camera.Target.Z + innerHalf),
                        new Vector3(x, 0F, camera.Target.Z + outerHalf), lineStep,
                        IsMajor(x, lineStep), logicalInner, outer);
                }

                var z = centerZ + index * lineStep;
                var zOffset = Math.Abs(z - camera.Target.Z);
                if (zOffset <= outer)
                {
                    var outerHalf = MathF.Sqrt(Math.Max(0F, outer * outer - zOffset * zOffset));
                    var innerHalf = zOffset < geometryInner
                        ? MathF.Sqrt(Math.Max(0F, geometryInner * geometryInner - zOffset * zOffset))
                        : 0F;
                    DrawGridSegment(new Vector3(camera.Target.X - outerHalf, 0F, z),
                        new Vector3(camera.Target.X - innerHalf, 0F, z), lineStep,
                        IsMajor(z, lineStep), logicalInner, outer);
                    DrawGridSegment(new Vector3(camera.Target.X + innerHalf, 0F, z),
                        new Vector3(camera.Target.X + outerHalf, 0F, z), lineStep,
                        IsMajor(z, lineStep), logicalInner, outer);
                }
            }
        }

        void DrawGridSegment(Vector3 start, Vector3 end, float lineStep, bool major,
            float logicalInner, float outer)
        {
            var length = (end - start).Length;
            if (length <= 0.001F) return;
            var segments = Math.Clamp((int)MathF.Ceiling(length / (lineStep * 8F)), 2, 16);
            for (var index = 0; index < segments; index++)
            {
                var a = Vector3.Lerp(start, end, index / (float)segments);
                var b = Vector3.Lerp(start, end, (index + 1) / (float)segments);
                var mid = (a + b) / 2F;
                var depth = Vector3.Dot(mid - camera.Position, camera.Forward);
                var spacingPixels = lineStep * SceneProjection.FocalLength(size, camera) /
                                    Math.Max(camera.NearClip, depth);
                var densityFade = major
                    ? 0.72F + 0.28F * SmoothStep(0.08F, 0.55F, spacingPixels)
                    : 0.16F + 0.84F * SmoothStep(0.55F, 1.45F, spacingPixels);
                var radialDistance = MathF.Sqrt(
                    (mid.X - camera.Target.X) * (mid.X - camera.Target.X) +
                    (mid.Z - camera.Target.Z) * (mid.Z - camera.Target.Z));
                var innerFade = logicalInner <= 0F
                    ? 1F
                    : SmoothStep(logicalInner * 0.62F, logicalInner * 0.96F, radialDistance);
                var outerFade = 1F - SmoothStep(outer * 0.72F, outer, radialDistance);
                var fade = densityFade * innerFade * outerFade;
                if (fade < (major ? 0.08F : 0.025F)) continue;
                if (SceneProjection.ClipLine(a, b, origin, size, camera, out var p, out var q))
                {
                    var color = major ? new Vector4(0.42F, 0.50F, 0.62F, fade * 0.78F) :
                        new Vector4(0.28F, 0.35F, 0.45F, fade * 0.66F);
                    draw.AddLine(p, q, ImGui.ColorConvertFloat4ToU32(color), 1F);
                }
            }
        }
    }

    private static float EstimateVisibleGroundExtent(Vector2 origin, Vector2 size, SceneCamera camera, float cameraDistance)
    {
        // 采样包含顶边在内的屏幕点：最远的交点往往来自上边缘，
        // 只封顶数学渐近线，绝不封顶成一个任意的近处方块
        var maximum = Math.Max(32F, cameraDistance * 4F);
        var maximumRayDistance = Math.Max(512F, cameraDistance * 96F);
        foreach (var x in new[] { 0.02F, 0.5F, 0.98F })
        foreach (var y in new[] { 0.02F, 0.5F, 0.98F })
        {
            var ray = SceneProjection.RayFromScreen(origin + new Vector2(size.X * x, size.Y * y), origin, size, camera);
            if (ray.Y >= -0.0001F) continue;
            var rayDistance = -camera.Position.Y / ray.Y;
            if (!float.IsFinite(rayDistance) || rayDistance <= 0F || rayDistance > maximumRayDistance) continue;
            var hit = camera.Position + ray * rayDistance;
            maximum = Math.Max(maximum, Math.Max(Math.Abs(hit.X - camera.Target.X), Math.Abs(hit.Z - camera.Target.Z)));
        }

        return maximum * 1.12F + 4F;
    }

    private static bool IsMajor(float worldCoordinate, float step) =>
        Math.Abs(MathF.IEEERemainder(MathF.Round(worldCoordinate / step), 8F)) < 0.001F;

    private static float NextPowerOfTwo(float value) =>
        MathF.Pow(2F, MathF.Ceiling(MathF.Log2(Math.Max(1F, value))));

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0F, 1F);
        return t * t * (3F - 2F * t);
    }
}

/// <summary>
/// 一帧的网格绘制计划
/// </summary>
/// <param name="Step">世界步长</param>
/// <param name="Extent">从注视点算起的覆盖半径</param>
/// <param name="HalfLineCount">单侧线数（含预算上限）</param>
internal readonly record struct SceneGridPlan(float Step, float Extent, int HalfLineCount);
