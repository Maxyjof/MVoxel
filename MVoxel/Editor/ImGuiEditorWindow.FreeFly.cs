using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// Caps Lock 下的第一人称飞行视角：进入时承接当前相机的位置与朝向，
/// 退出时把环绕点设到正前方 50 个单位处，保证切换前后画面不跳
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>退出飞行时环绕点放在视线前方多远</summary>
    private const float FreeFlyExitOrbitDistance = 50F;

    private bool _freeFlyActive;

    /// <summary>飞行时把鼠标锚在场景视口中心，保证准星、选择与视角都从中间开始</summary>
    private Vector2? _freeFlyViewportCenter;

    /// <summary>进入飞行后的前几帧忽略鼠标增量，避开捕获光标瞬间的跳变</summary>
    private int _freeFlyLookGraceFrames;

    /// <summary>上一次读到的原始鼠标位置，用来自己算增量，避免 ImGui 在捕获模式下的跳变</summary>
    private Vector2? _freeFlyRawMouse;
    private Vector3 _freeFlyPosition;
    private float _freeFlyYaw;
    private float _freeFlyPitch;

    /// <summary>
    /// 从当前环绕相机切入第一人称：位置与朝向完全承接当前画面
    /// </summary>
    private void EnterFreeFly()
    {
        var camera = GetSceneCamera();
        _freeFlyActive = true;
        _freeFlyPosition = camera.Position;
        var forward = Vector3.Normalize(camera.Forward);
        _freeFlyYaw = MathF.Atan2(forward.X, forward.Z);
        _freeFlyPitch = MathF.Asin(Math.Clamp(forward.Y, -1F, 1F));
        _freeFlyLookGraceFrames = 6;
        _freeFlyRawMouse = null;
    }

    /// <summary>
    /// 退出第一人称回到环绕：环绕点放在正前方 50 单位，画面不跳
    /// </summary>
    private void ExitFreeFlyToOrbit()
    {
        if (!_freeFlyActive) return;
        var position = _freeFlyPosition;
        var forward = new Vector3(
            MathF.Cos(_freeFlyPitch) * MathF.Sin(_freeFlyYaw),
            MathF.Sin(_freeFlyPitch),
            MathF.Cos(_freeFlyPitch) * MathF.Cos(_freeFlyYaw));
        _freeFlyActive = false;
        _scenePan = new Vector2(
            position.X + forward.X * FreeFlyExitOrbitDistance,
            position.Z + forward.Z * FreeFlyExitOrbitDistance);
        _sceneFreeMoveHeight = position.Y + forward.Y * FreeFlyExitOrbitDistance;
        _sceneCameraDistance = FreeFlyExitOrbitDistance;
        // 环绕方向 = 从枢轴指向相机，即 -forward
        var direction = -forward;
        _sceneOrbitPitch = MathF.Asin(Math.Clamp(direction.Y, -1F, 1F));
        _sceneOrbitYaw = MathF.Atan2(direction.X, direction.Z);
    }

    /// <summary>
    /// 第一人称相机：位置由飞行状态决定，朝向前方
    /// </summary>
    private SceneCamera GetFreeFlyCamera()
    {
        var forward = new Vector3(
            MathF.Cos(_freeFlyPitch) * MathF.Sin(_freeFlyYaw),
            MathF.Sin(_freeFlyPitch),
            MathF.Cos(_freeFlyPitch) * MathF.Cos(_freeFlyYaw));
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared < 0.0001F) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return new SceneCamera(_freeFlyPosition, _freeFlyPosition + forward * 10F, forward, right, up)
        {
            NearClip = _sceneNearClip,
            FarClip = _sceneFarClip,
            FieldOfViewDegrees = _sceneFieldOfView,
        };
    }

    /// <summary>
    /// 第一人称鼠标视角：原地转头，不再绕枢轴
    /// </summary>
    /// <param name="mouseDelta">鼠标位移</param>
    private void UpdateFreeFlyLook(Vector2 mouseDelta)
    {
        if (_freeFlyLookGraceFrames > 0)
        {
            _freeFlyLookGraceFrames--;
            return;
        }

        // 捕获期间系统会把光标拉回中心，偶尔产生上百像素的跳变，这里限制单帧输入避免视角瞬间翻转
        if (MathF.Abs(mouseDelta.X) > 200F || MathF.Abs(mouseDelta.Y) > 200F) return;
        var deltaX = Math.Clamp(mouseDelta.X, -40F, 40F);
        var deltaY = Math.Clamp(mouseDelta.Y, -40F, 40F);
        _freeFlyYaw += ViewportInputPolicy.OrbitYawDelta(deltaX);
        // 第一人称：鼠标上移抬高视线、下移压低视线，与环绕相机的俯仰方向相反
        _freeFlyPitch = Math.Clamp(
            _freeFlyPitch - ViewportInputPolicy.OrbitPitchDelta(deltaY), -1.55F, 1.55F);
    }

    /// <summary>
    /// 第一人称移动：WASD 沿视线方向飞行，Q/E 升降
    /// </summary>
    /// <param name="camera">当前相机</param>
    /// <param name="deltaTime">帧间隔秒数</param>
    private void UpdateFreeFlyMovement(SceneCamera camera, float deltaTime)
    {
        var movement = GetFreeMoveDelta(camera, deltaTime, includePitch: true);
        _freeFlyPosition += movement;
    }
}
