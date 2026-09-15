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
