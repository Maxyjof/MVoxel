using ImGuiNET;
using MVoxel.Core;
using MVoxel.Voxels;
using OpenTK.Mathematics;

namespace MVoxel.Editor;

/// <summary>
/// 场景视口导航状态与内部体素判定的滞回
/// 相机绕模型内部的枢轴转动或快速穿梭体素时，位置会在实体格边界两侧反复扫动；
/// 若每帧都按实际位置决定是否包含内部体素，工作集与硬件加速结构会被整份重建，
/// 单次 300 毫秒以上。这里在导航期间冻结判定，相机停下并稳定后才允许切换一次
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>相机停下后需要保持静止多久才允许切换内部体素判定（毫秒）</summary>
    private const long NavigationSettleDelayMs = 180;

    /// <summary>进入内部体素判定所需的连续实体格内时长（毫秒）</summary>
    private const long InteriorEnterDelayMs = 200;

    /// <summary>退出内部体素判定所需的连续实体格外时长（毫秒）</summary>
    private const long InteriorExitDelayMs = 250;

    /// <summary>场景视图超过该间隔没有求值时，之前的候选计时视为不连续（毫秒）</summary>
    private const long InteriorEvaluationGapMs = 800;

    /// <summary>单帧位移超过该世界单位即视为相机在移动</summary>
    private const float NavigationPositionEpsilon = 0.0001F;

    /// <summary>单帧朝向点积低于该值即视为相机在转动，约等于 0.08 度</summary>
    private const float NavigationForwardDotEpsilon = 1F - 0.000001F;

    /// <summary>上一帧相机位置，用于判断相机是否在移动</summary>
    private Vector3 _navigationLastPosition;

    /// <summary>上一帧相机朝向，用于判断相机是否在转动</summary>
    private Vector3 _navigationLastForward;

    /// <summary>是否已有可比较的上一帧相机位姿</summary>
    private bool _navigationPoseKnown;

    /// <summary>最近一次检测到相机活动或导航输入的时刻（毫秒）</summary>
    private long _navigationLastActivityMs = Environment.TickCount64;

    /// <summary>当前是否处于导航中，导航期间冻结内部体素切换</summary>
    private bool _sceneNavigationActive;

    /// <summary>已生效的内部体素包含状态</summary>
    private bool _includeInteriorVoxels;

    /// <summary>内部体素判定是否已经完成首次求值</summary>
    private bool _includeInteriorInitialized;

    /// <summary>是否正在为某个候选状态计时</summary>
    private bool _includeInteriorCandidatePending;

    /// <summary>候选状态第一次被观测到的时刻（毫秒）</summary>
    private long _includeInteriorCandidateSinceMs;

    /// <summary>上一次内部体素求值的时刻（毫秒）</summary>
    private long _includeInteriorLastEvaluationMs = Environment.TickCount64;

    /// <summary>内部体素求值针对的体素世界，换世界后重新建立基线</summary>
    private VoxelWorld? _includeInteriorWorld;

    /// <summary>
    /// 场景视口本帧使用的内部体素包含标志，统一由滞回状态机给出。
    /// 相机在实体外时剔除内部体素，在实体内部时保留内部体素，语义与逐帧实际判定一致
    /// </summary>
    private bool IncludeInteriorVoxels => _includeInteriorVoxels;

    /// <summary>
    /// 更新导航活动状态与内部体素判定，每帧在同步工作集之前调用一次
    /// </summary>
    /// <param name="camera">本帧场景相机</param>
    private void UpdateSceneInteriorStability(SceneCamera camera)
    {
        var nowMs = Environment.TickCount64;
        UpdateSceneNavigationActivity(camera, nowMs);
        UpdateIncludeInteriorState(camera, nowMs);
    }

    /// <summary>
    /// 判定本帧是否处于相机导航中，导航输入和位姿变化都算导航
    /// </summary>
    /// <param name="camera">本帧场景相机</param>
    /// <param name="nowMs">当前时刻（毫秒）</param>
    private void UpdateSceneNavigationActivity(SceneCamera camera, long nowMs)
    {
        if (_navigationPoseKnown &&
            (Vector3.Distance(camera.Position, _navigationLastPosition) > NavigationPositionEpsilon ||
             Vector3.Dot(camera.Forward, _navigationLastForward) < NavigationForwardDotEpsilon))
        {
            _navigationLastActivityMs = nowMs;
        }

        _navigationPoseKnown = true;
        _navigationLastPosition = camera.Position;
        _navigationLastForward = camera.Forward;
        //输入可能先于位姿变化出现，例如滚轮和按住移动键，仍然算导航
        if (SceneNavigationInputActive()) _navigationLastActivityMs = nowMs;
        //停下后保留一小段稳定窗口，抖动尚未结束时不要切换
        _sceneNavigationActive = nowMs - _navigationLastActivityMs < NavigationSettleDelayMs;
    }

    /// <summary>
    /// 场景视口上是否存在相机导航输入，鼠标不在场景窗口时不计入
    /// </summary>
    /// <returns>是否有导航输入</returns>
    private bool SceneNavigationInputActive()
    {
        if (!ImGui.IsWindowHovered()) return false;
        var io = ImGui.GetIO();
        if (MathF.Abs(io.MouseWheel) > 0.001F || MathF.Abs(io.MouseWheelH) > 0.001F) return true;
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            return true;
        return ImGui.IsKeyDown(ImGuiKey.W) || ImGui.IsKeyDown(ImGuiKey.A) || ImGui.IsKeyDown(ImGuiKey.S) ||
               ImGui.IsKeyDown(ImGuiKey.D) || ImGui.IsKeyDown(ImGuiKey.Q) || ImGui.IsKeyDown(ImGuiKey.E);
    }

    /// <summary>
    /// 按双阈值滞回更新内部体素包含状态，导航期间只累计候选计时不切换
    /// </summary>
    /// <param name="camera">本帧场景相机</param>
    /// <param name="nowMs">当前时刻（毫秒）</param>
    private void UpdateIncludeInteriorState(SceneCamera camera, long nowMs)
    {
        var inside = IsCameraInsideOccupiedVoxel(camera);
        //换了体素世界时重新建立基线，不沿用上一份世界的判定结果
        if (!ReferenceEquals(_includeInteriorWorld, _world))
        {
            _includeInteriorWorld = _world;
            _includeInteriorInitialized = false;
        }

        //首次求值直接采用实际状态，避免启动时先闪一帧错误的内部结构
        if (!_includeInteriorInitialized)
        {
            _includeInteriorInitialized = true;
            _includeInteriorVoxels = inside;
            _includeInteriorCandidatePending = false;
            _includeInteriorLastEvaluationMs = nowMs;
            return;
        }

        var evaluationGapMs = nowMs - _includeInteriorLastEvaluationMs;
        _includeInteriorLastEvaluationMs = nowMs;
        if (inside == _includeInteriorVoxels)
        {
            _includeInteriorCandidatePending = false;
            return;
        }

        //场景视图长时间没有绘制时计时并不连续，例如切到渲染页签，重新开始计时
        if (evaluationGapMs > InteriorEvaluationGapMs) _includeInteriorCandidatePending = false;
        if (!_includeInteriorCandidatePending)
        {
            _includeInteriorCandidatePending = true;
            _includeInteriorCandidateSinceMs = nowMs;
        }

        var requiredMs = inside ? InteriorEnterDelayMs : InteriorExitDelayMs;
        //导航期间只更新候选计时：否则相机擦过实体格边缘就会立刻整份重建工作集
        if (_sceneNavigationActive) return;
        if (nowMs - _includeInteriorCandidateSinceMs < requiredMs) return;

        var waitedMs = nowMs - _includeInteriorCandidateSinceMs;
        _includeInteriorVoxels = inside;
        _includeInteriorCandidatePending = false;
        MVoxelLog.Info($"场景内部体素切换：包含内部体素={inside}，候选持续 {waitedMs} 毫秒，" +
            $"相机位置=({camera.Position.X:0.00},{camera.Position.Y:0.00},{camera.Position.Z:0.00})");
    }
}
