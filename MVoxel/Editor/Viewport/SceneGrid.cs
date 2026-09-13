using ImGuiNET;
using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

internal static class SceneGrid
{
    private const int TargetHalfLineCount = 160;
    private const int MaximumHalfLineCount = 192;

    internal static float Spacing(float cameraDistance, Vector2 size)
    {
        // This is the baseline spacing at the view target. Plan() then raises
        // it only when the visible ground-plane footprint would require too
        // many lines. The old distance^2 fade plus a small fixed patch made
        // the grid disappear both close to the camera and at the horizon.
        var desired = Math.Max(1F, cameraDistance * 12F / Math.Max(1F, SceneProjection.FocalLength(size)));
        return NextPowerOfTwo(desired);
    }

    internal static SceneGridPlan Plan(Vector2 origin, Vector2 size, SceneCamera camera, float cameraDistance)
    {
        var visibleExtent = EstimateVisibleGroundExtent(origin, size, camera, cameraDistance);
        var baselineStep = Spacing(cameraDistance, size);
        var coverageStep = NextPowerOfTwo(Math.Max(1F, visibleExtent / TargetHalfLineCount));
        var step = Math.Max(baselineStep, coverageStep);
        var halfLineCount = Math.Clamp((int)MathF.Ceiling(visibleExtent / step) + 3, 16, MaximumHalfLineCount);
        return new SceneGridPlan(step, halfLineCount * step, halfLineCount);
    }

    internal static void Draw(ImDrawListPtr draw, Vector2 origin, Vector2 size, SceneCamera camera, float distance)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        var plan = Plan(origin, size, camera, distance);
        var step = plan.Step;
        var extent = plan.Extent;
        // Snap the visible patch to WORLD grid multiples; panning never moves
        // grid coordinates, and it remains visible far away from the origin.
        var centerX = MathF.Floor(camera.Target.X / step) * step;
        var centerZ = MathF.Floor(camera.Target.Z / step) * step;
        draw.PushClipRect(origin, origin + size, true);
        for (var i = -plan.HalfLineCount; i <= plan.HalfLineCount; i++)
        {
            var x = centerX + i * step;
            var z = centerZ + i * step;
            DrawGroundLine(new Vector3(x, 0, centerZ - extent), Vector3.UnitZ, IsMajor(x, step));
            DrawGroundLine(new Vector3(centerX - extent, 0, z), Vector3.UnitX, IsMajor(z, step));
        }
        draw.PopClipRect();

        void DrawGroundLine(Vector3 start, Vector3 direction, bool major)
        {
            // Segment only for smooth horizon fading. Grid coverage itself is
            // based on ground/frustum intersections, not segment falloff.
            var segments = Math.Clamp((int)MathF.Ceiling(2F * extent / (step * 40F)), 4, 10);
            for (var index = 0; index < segments; index++)
            {
                var a = start + direction * (2 * extent * index / segments);
                var b = start + direction * (2 * extent * (index + 1) / segments);
                var mid = (a + b) / 2F;
                var depth = Vector3.Dot(mid - camera.Position, camera.Forward);
                // At the horizon a cell can become sub-pixel. Fade minor
                // lines there, but keep a restrained major grid so the far
                // field remains readable instead of vanishing altogether.
                var spacingPixels = step * SceneProjection.FocalLength(size) /
                                    Math.Max(SceneProjection.Near, depth);
                var densityFade = major
                    ? 0.22F + 0.78F * SmoothStep(0.08F, 0.55F, spacingPixels)
                    : SmoothStep(0.55F, 1.45F, spacingPixels);
                var radial = Math.Max(Math.Abs(mid.X - camera.Target.X), Math.Abs(mid.Z - camera.Target.Z)) / extent;
                var edgeFade = 1F - SmoothStep(0.985F, 1F, radial);
                var fade = densityFade * edgeFade;
                if (fade < 0.01F) continue;
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
        // Sample the screen including the top edge. The top intersection is
        // often the distant horizon; cap only its mathematical asymptote,
        // never an arbitrary near-camera square.
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

internal readonly record struct SceneGridPlan(float Step, float Extent, int HalfLineCount);
