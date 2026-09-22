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
        var step = plan.Step;
        var extent = plan.Extent;
        // 补丁对齐到世界网格倍数：平移不移动格坐标，远离原点时网格依然连续
        var centerX = MathF.Floor(camera.Target.X / step) * step;
        var centerZ = MathF.Floor(camera.Target.Z / step) * step;
        var fineCenterX = MathF.Floor(camera.Target.X);
        var fineCenterZ = MathF.Floor(camera.Target.Z);
        draw.PushClipRect(origin, origin + size, true);

        // 低角度视锥的远处交点可能非常远，不能为了覆盖它们而把整张网格
        // 统一粗化。靠近当前目标的区域必须始终按 1x1x1 体素单位显示，
        // 否则网格线无法与体素边界对应；远处再由下面的粗网格负责覆盖。
        var fineExtent = Math.Min(extent, FineGridRadius);
        var fineGridActive = step > 1F && distance <= SparseGridStartDistance && fineExtent >= 1F;
        var blendStart = fineExtent * 0.55F;
        var blendEnd = fineExtent * 1.15F;
        if (fineGridActive)
        {
            var fineHalfLineCount = Math.Clamp((int)MathF.Ceiling(fineExtent), 1, 96);
            for (var i = -fineHalfLineCount; i <= fineHalfLineCount; i++)
            {
                var x = fineCenterX + i;
                var z = fineCenterZ + i;
                var xHalfLength = MathF.Sqrt(Math.Max(0F, fineExtent * fineExtent -
                    (x - fineCenterX) * (x - fineCenterX)));
                var zHalfLength = MathF.Sqrt(Math.Max(0F, fineExtent * fineExtent -
                    (z - fineCenterZ) * (z - fineCenterZ)));
                if (xHalfLength > 0.001F)
                    DrawGroundLine(new Vector3(x, 0, fineCenterZ - xHalfLength), Vector3.UnitZ,
                        IsMajor(x, 1F), 1F, xHalfLength, fineLayer: true);
                if (zHalfLength > 0.001F)
                    DrawGroundLine(new Vector3(fineCenterX - zHalfLength, 0, z), Vector3.UnitX,
                        IsMajor(z, 1F), 1F, zHalfLength, fineLayer: true);
            }
        }

        for (var i = -plan.HalfLineCount; i <= plan.HalfLineCount; i++)
        {
            var x = centerX + i * step;
            var z = centerZ + i * step;
            DrawGroundLine(new Vector3(x, 0, centerZ - extent), Vector3.UnitZ,
                IsMajor(x, step), step, extent, fineLayer: false);
            DrawGroundLine(new Vector3(centerX - extent, 0, z), Vector3.UnitX,
                IsMajor(z, step), step, extent, fineLayer: false);
        }
        draw.PopClipRect();

        void DrawGroundLine(Vector3 start, Vector3 direction, bool major,
            float lineStep, float lineExtent, bool fineLayer)
        {
            // 分段用于平滑淡出，覆盖范围来自视锥与地面交点
            var segments = Math.Clamp((int)MathF.Ceiling(2F * lineExtent / (lineStep * 8F)), 8, 24);
            for (var index = 0; index < segments; index++)
            {
                var a = start + direction * (2 * lineExtent * index / segments);
                var b = start + direction * (2 * lineExtent * (index + 1) / segments);
                var mid = (a + b) / 2F;
                var depth = Vector3.Dot(mid - camera.Position, camera.Forward);
                // 地平线附近一格可能小于一个像素：副线淡出，
                // 保留克制的主线，让远景保持可读
                var spacingPixels = lineStep * SceneProjection.FocalLength(size, camera) /
                                    Math.Max(camera.NearClip, depth);
                // 远处允许降低副线密度，但不能把整片地面淡出成空白：
                // 主线始终作为空间参照，副线保留最低可见度，避免缩放到某个距离后网格突然消失。
                var densityFade = major
                    ? 0.72F + 0.28F * SmoothStep(0.08F, 0.55F, spacingPixels)
                    : 0.16F + 0.84F * SmoothStep(0.55F, 1.45F, spacingPixels);
                var radial = Math.Max(Math.Abs(mid.X - camera.Target.X), Math.Abs(mid.Z - camera.Target.Z)) / lineExtent;
                var edgeFade = 0.42F + 0.58F * (1F - SmoothStep(0.985F, 1F, radial));
                var transitionFade = 1F;
                if (fineGridActive)
                {
                    var radialDistance = MathF.Sqrt(
                        (mid.X - fineCenterX) * (mid.X - fineCenterX) +
                        (mid.Z - fineCenterZ) * (mid.Z - fineCenterZ));
                    transitionFade = fineLayer
                        ? 1F - SmoothStep(blendStart, blendEnd, radialDistance)
                        : SmoothStep(blendStart, blendEnd, radialDistance);
                }

                var fade = densityFade * edgeFade * transitionFade;
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
