using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

internal readonly record struct SceneCamera(Vector3 Position, Vector3 Target, Vector3 Forward, Vector3 Right, Vector3 Up);

internal static class SceneProjection
{
    internal const float Near = 0.05F;
    internal static float FocalLength(Vector2 size) => Math.Min(size.X, size.Y) * 0.95F;

    // Clip in camera space BEFORE perspective division. Rejecting a segment
    // just because one endpoint is behind the camera removes visible ground.
    internal static bool ClipLine(Vector3 start, Vector3 end, Vector2 origin, Vector2 size,
        SceneCamera camera, out Vector2 a, out Vector2 b)
    {
        a = b = default;
        if (size.X <= 0 || size.Y <= 0) return false;
        var p = ToCamera(start, camera);
        var q = ToCamera(end, camera);
        var xSlope = size.X / (2F * FocalLength(size));
        var ySlope = size.Y / (2F * FocalLength(size));
        var lower = 0F;
        var upper = 1F;
        if (!ClipPlane(p.Z - Near, q.Z - Near, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope + p.X, q.Z * xSlope + q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * xSlope - p.X, q.Z * xSlope - q.X, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope + p.Y, q.Z * ySlope + q.Y, ref lower, ref upper) ||
            !ClipPlane(p.Z * ySlope - p.Y, q.Z * ySlope - q.Y, ref lower, ref upper)) return false;
        a = ProjectCamera(p + (q - p) * lower, origin, size);
        b = ProjectCamera(p + (q - p) * upper, origin, size);
        return float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(b.X) && float.IsFinite(b.Y);
    }

    internal static bool Project(Vector3 world, Vector2 origin, Vector2 size, SceneCamera camera,
        out Vector2 screen, out float depth)
    {
        var p = ToCamera(world, camera);
        depth = p.Z;
        screen = default;
        if (depth < Near) return false;
        screen = ProjectCamera(p, origin, size);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>
    /// Returns a normalized world-space ray through a point in the scene view.
    /// It uses the exact inverse of <see cref="Project"/>, so ground-grid
    /// coverage can be derived from the actual view frustum instead of a
    /// guessed fixed square around the camera target.
    /// </summary>
    internal static Vector3 RayFromScreen(Vector2 screen, Vector2 origin, Vector2 size, SceneCamera camera)
    {
        var focal = FocalLength(size);
        var offset = (screen - origin - size / 2F) / Math.Max(1F, focal);
        var direction = camera.Forward + camera.Right * offset.X - camera.Up * offset.Y;
        return direction.LengthSquared > 0.000001F ? Vector3.Normalize(direction) : camera.Forward;
    }

    private static Vector3 ToCamera(Vector3 world, SceneCamera camera)
    {
        var p = world - camera.Position;
        return new Vector3(Vector3.Dot(p, camera.Right), Vector3.Dot(p, camera.Up), Vector3.Dot(p, camera.Forward));
    }

    private static Vector2 ProjectCamera(Vector3 p, Vector2 origin, Vector2 size) =>
        origin + size / 2F + new Vector2(p.X, -p.Y) * (FocalLength(size) / Math.Max(Near, p.Z));

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
