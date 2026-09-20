using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 视口相机导航使用的共享数学运算
/// </summary>
internal static class ViewportNavigationMath
{
    /// <summary>
    /// 根据屏幕拖拽量计算轨道相机的新朝向
    /// </summary>
    /// <param name="camera">当前相机</param>
    /// <param name="mouseDelta">鼠标帧间位移</param>
    /// <returns>新的视线方向与上方向</returns>
    internal static (Vector3 Forward, Vector3 Up) Orbit(SceneCamera camera, Vector2 mouseDelta)
    {
        var yaw = MathF.Atan2(-camera.Forward.X, -camera.Forward.Z) +
                  ViewportInputPolicy.OrbitYawDelta(mouseDelta.X);
        var pitch = Math.Clamp(
            MathF.Asin(Math.Clamp(-camera.Forward.Y, -1F, 1F)) +
            ViewportInputPolicy.OrbitPitchDelta(mouseDelta.Y),
            -1.55F,
            1.55F);
        var forward = new Vector3(
            -MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch),
            -MathF.Cos(pitch) * MathF.Cos(yaw));
        return (Vector3.Normalize(forward), Vector3.UnitY);
    }

    /// <summary>
    /// 围绕观察中心旋转相机，供场景和渲染窗口共用。
    /// </summary>
    internal static (Vector3 Position, Vector3 Forward, Vector3 Up) OrbitAroundPivot(
        SceneCamera camera, Vector3 pivot, Vector2 mouseDelta)
    {
        return OrbitAroundPivot(camera, pivot, mouseDelta, 1.25F, null);
    }

    /// <summary>
    /// 围绕观察中心旋转相机，并保证轨道半径不会退化为第一人称原地转向。
    /// </summary>
    /// <param name="camera">当前相机</param>
    /// <param name="pivot">观察中心</param>
    /// <param name="mouseDelta">鼠标帧间位移</param>
    /// <param name="minimumDistance">允许的最小轨道半径</param>
    /// <param name="distanceOverride">可选的目标轨道半径，用于围绕 pivot 推拉</param>
    /// <returns>新的位置、视线方向和上方向</returns>
    internal static (Vector3 Position, Vector3 Forward, Vector3 Up) OrbitAroundPivot(
        SceneCamera camera, Vector3 pivot, Vector2 mouseDelta, float minimumDistance, float? distanceOverride)
    {
        var offset = camera.Position - pivot;
        var safeMinimum = float.IsFinite(minimumDistance) ? MathF.Max(0.01F, minimumDistance) : 1.25F;
        var rawDistance = offset.Length;
        var distance = distanceOverride is { } requestedDistance && float.IsFinite(requestedDistance)
            ? MathF.Max(safeMinimum, requestedDistance)
            : MathF.Max(safeMinimum, float.IsFinite(rawDistance) ? rawDistance : safeMinimum);
        if (!float.IsFinite(rawDistance) || rawDistance < 0.0001F)
        {
            // 相机正好位于 pivot 时，使用当前朝向反推一个稳定的轨道方向，避免 atan2(0, 0)
            // 把相机突然放到固定的第一人称方向。
            offset = -camera.Forward;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z) ||
                offset.LengthSquared < 0.0001F)
                offset = Vector3.UnitZ;
        }

        var direction = Vector3.Normalize(offset);
        var yaw = MathF.Atan2(offset.X, offset.Z) + ViewportInputPolicy.OrbitYawDelta(mouseDelta.X);
        var pitch = Math.Clamp(MathF.Asin(Math.Clamp(direction.Y, -1F, 1F)) +
            ViewportInputPolicy.OrbitPitchDelta(mouseDelta.Y), -1.55F, 1.55F);
        // 轨道相机保持在地面平面上方，避免绕到地下后天空和地面在屏幕上下看起来互换。
        var minimumPitch = MathF.Asin(Math.Clamp((0.25F - pivot.Y) / distance, -1F, 1F));
        pitch = MathF.Max(pitch, minimumPitch);
        var position = pivot + new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw)) * distance;
        var forward = Vector3.Normalize(pivot - position);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared < 0.0001F) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return (position, forward, up);
    }

    /// <summary>
    /// 根据中键拖拽量计算 Blender 风格的相机平移量
    /// </summary>
    /// <param name="camera">当前相机</param>
    /// <param name="mouseDelta">鼠标帧间位移</param>
    /// <param name="scale">世界空间平移比例</param>
    /// <returns>相机应移动的世界空间位移</returns>
    internal static Vector3 Pan(SceneCamera camera, Vector2 mouseDelta, float scale)
    {
        var safeScale = float.IsFinite(scale) ? MathF.Max(0F, scale) : 0F;
        return camera.Right * (-mouseDelta.X * safeScale) + camera.Up * (mouseDelta.Y * safeScale);
    }
}
