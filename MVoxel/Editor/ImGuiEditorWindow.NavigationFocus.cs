using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 层级双击聚焦：只在用户明确双击对象时执行平滑取景，普通渲染窗口导航不隐式聚焦任何对象。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private const float FocusAnimationDuration = 0.36F;
    private SceneFocusAnimation? _sceneFocusAnimation;
    private RenderFocusAnimation? _renderFocusAnimation;

    private sealed class SceneFocusAnimation
    {
        public SceneFocusAnimation(Vector2 startPan, float startHeight, float startDistance,
            Vector2 targetPan, float targetHeight, float targetDistance)
        {
            StartPan = startPan;
            StartHeight = startHeight;
            StartDistance = startDistance;
            TargetPan = targetPan;
            TargetHeight = targetHeight;
            TargetDistance = targetDistance;
        }

        public Vector2 StartPan { get; }
        public float StartHeight { get; }
        public float StartDistance { get; }
        public Vector2 TargetPan { get; }
        public float TargetHeight { get; }
        public float TargetDistance { get; }
        public float Elapsed { get; set; }
    }

    private sealed class RenderFocusAnimation
    {
        public RenderFocusAnimation(SceneObject cameraObject, Vector3 startPosition, Vector3 startForward,
            Vector3 startUp, Vector3 targetPosition, Vector3 targetForward, Vector3 targetUp,
            Vector3 orbitAnchor)
        {
            CameraObject = cameraObject;
            StartPosition = startPosition;
            StartForward = startForward;
            StartUp = startUp;
            TargetPosition = targetPosition;
            TargetForward = targetForward;
            TargetUp = targetUp;
            OrbitAnchor = orbitAnchor;
        }

        public SceneObject CameraObject { get; }
        public Vector3 StartPosition { get; }
        public Vector3 StartForward { get; }
        public Vector3 StartUp { get; }
        public Vector3 TargetPosition { get; }
        public Vector3 TargetForward { get; }
        public Vector3 TargetUp { get; }
        public Vector3 OrbitAnchor { get; }
        public float Elapsed { get; set; }
    }

    private readonly record struct FocusBounds(Vector3 Minimum, Vector3 Maximum)
    {
        public Vector3 Center => (Minimum + Maximum) * 0.5F;
        public Vector3 Extent => Maximum - Minimum;
    }

    /// <summary>
    /// 层级节点被双击时调用。当前中心窗口决定使用场景视角还是渲染相机。
    /// </summary>
    private void FocusHierarchyObject(SceneObject sceneObject)
    {
        if (!TryGetHierarchyObjectBounds(sceneObject, out var bounds))
        {
            SetStatus($"无法计算对象“{sceneObject.Name}”的包围盒");
            return;
        }

        // 聚焦目标跟随 Gizmo 支点设置：中心取包围盒中心，枢轴取对象原点
        var target = _editorSettings.GizmoPivotCenter ? bounds.Center : GetObjectPivot(sceneObject);
        // 只有渲染窗口处于焦点且确实绑定了相机对象时才聚焦渲染相机，否则一律聚焦场景视角
        if (_renderViewWasFocused && GetActiveRenderCameraObject() is { } cameraObject)
        {
            BeginRenderCameraFocus(cameraObject, bounds, target);
        }
        else
        {
            var viewport = _lastSceneViewportSize;
            if (viewport.X < 32F || viewport.Y < 32F)
                viewport = ImGui.GetMainViewport().Size;
            BeginSceneCameraFocus(bounds, viewport, _sceneFieldOfView, target);
        }

        _selectedSceneObjectId = sceneObject.Id;
        _selectedVoxel = null;
    }

    private void BeginSceneCameraFocus(FocusBounds bounds, Vector2 viewportSize, float fieldOfView,
        Vector3? targetOverride = null)
    {
        var center = targetOverride ?? bounds.Center;
        var targetDistance = CalculateFocusDistance(bounds, viewportSize, fieldOfView,
            GetSceneZoomLimits());
        _sceneFocusAnimation = new SceneFocusAnimation(
            _scenePan,
            _sceneFreeMoveHeight,
            _sceneCameraDistance,
            new Vector2(center.X, center.Z),
            center.Y,
            targetDistance);
        _renderFocusAnimation = null;
    }

    private void BeginRenderCameraFocus(SceneObject cameraObject, FocusBounds bounds, Vector3? targetOverride = null)
    {
        if (cameraObject.Locked)
        {
            SetStatus($"相机对象“{cameraObject.Name}”已锁定，无法移动");
            return;
        }

        var camera = ToSceneCamera(cameraObject);
        var target = targetOverride ?? bounds.Center;
        var distance = CalculateFocusDistance(bounds, _lastRenderViewportSize,
            ResolveRenderSettings(cameraObject).CameraFovDegrees, GetRenderOrbitLimits());
        var viewDirection = NormalizeOr(camera.Position - target, -camera.Forward);
        var targetPosition = target + viewDirection * distance;
        var targetForward = NormalizeOr(target - targetPosition, camera.Forward);
        var targetUp = BuildCameraUp(targetForward, camera.Up);
        _renderFocusAnimation = new RenderFocusAnimation(
            cameraObject,
            camera.Position,
            camera.Forward,
            camera.Up,
            targetPosition,
            targetForward,
            targetUp,
            target);
        _sceneFocusAnimation = null;
    }

    private void AdvanceSceneCameraFocus(float deltaTime)
    {
        if (_sceneFocusAnimation is not { } animation) return;
        animation.Elapsed += MathF.Max(deltaTime, 1F / 240F);
        var t = EaseFocus(animation.Elapsed / FocusAnimationDuration);
        _scenePan = Vector2.Lerp(animation.StartPan, animation.TargetPan, t);
        _sceneFreeMoveHeight = Lerp(animation.StartHeight, animation.TargetHeight, t);
        _sceneCameraDistance = Lerp(animation.StartDistance, animation.TargetDistance, t);
        if (animation.Elapsed >= FocusAnimationDuration)
        {
            _sceneFocusAnimation = null;
        }
    }

    private void AdvanceRenderCameraFocus(float deltaTime)
    {
        if (_renderFocusAnimation is not { } animation) return;
        if (animation.CameraObject.Locked ||
            !GetRenderCameraObjects().Any(item => item.Id == animation.CameraObject.Id))
        {
            _renderFocusAnimation = null;
            return;
        }

        animation.Elapsed += MathF.Max(deltaTime, 1F / 240F);
        var t = EaseFocus(animation.Elapsed / FocusAnimationDuration);
        var position = Vector3.Lerp(animation.StartPosition, animation.TargetPosition, t);
        var forward = NormalizeOr(Vector3.Lerp(animation.StartForward, animation.TargetForward, t),
            animation.TargetForward);
        var up = BuildCameraUp(forward,
            NormalizeOr(Vector3.Lerp(animation.StartUp, animation.TargetUp, t), animation.TargetUp));
        WriteCameraTransform(animation.CameraObject, position, forward, up);
        _renderOrbitAnchor = animation.OrbitAnchor;
        if (animation.Elapsed >= FocusAnimationDuration)
        {
            _renderFocusAnimation = null;
        }
    }

    private static float EaseFocus(float normalizedTime)
    {
        var t = Math.Clamp(normalizedTime, 0F, 1F);
        return t * t * (3F - 2F * t);
    }

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;

    private static float CalculateFocusDistance(FocusBounds bounds, Vector2 viewportSize, float fieldOfView,
        SceneZoomLimits limits)
    {
        var extent = bounds.Extent;
        // 用最长边长度的 0.85 倍近似“外轮廓半径”，比包围盒对角线更贴近实际观感
        var longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        var radius = MathF.Max(longest * 0.5F * 0.85F, extent.Length * 0.5F * 0.6F) + 0.5F;
        var width = MathF.Max(viewportSize.X, 1F);
        var height = MathF.Max(viewportSize.Y, 1F);
        var verticalHalfFov = Math.Clamp(fieldOfView, 20F, 120F) * MathF.PI / 360F;
        var aspect = width / height;
        var horizontalHalfFov = MathF.Atan(MathF.Tan(verticalHalfFov) * aspect);
        var visibleHalfFov = MathF.Min(verticalHalfFov, horizontalHalfFov);
        // 距离 = 3 * 半径 / tan(半视角) 时，外轮廓直径正好约占屏幕三分之一
        const float screenFraction = 1F / 3F;
        var required = radius / MathF.Max(MathF.Tan(visibleHalfFov) * screenFraction, 0.02F);
        return Math.Clamp(required, limits.MinimumDistance, limits.MaximumDistance);
    }

    private bool TryGetHierarchyObjectBounds(SceneObject sceneObject, out FocusBounds bounds)
    {
        var found = false;
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var visited = new HashSet<StableId>();
        var ownedUnion = new HashSet<VoxelCoord64>();
        foreach (var owned in _layerOwnership.Values)
        {
            ownedUnion.UnionWith(owned);
        }

        void Include(Vector3 lower, Vector3 upper)
        {
            if (!float.IsFinite(lower.X) || !float.IsFinite(lower.Y) || !float.IsFinite(lower.Z) ||
                !float.IsFinite(upper.X) || !float.IsFinite(upper.Y) || !float.IsFinite(upper.Z)) return;
            minimum = Vector3.ComponentMin(minimum, upper == lower ? lower - new Vector3(0.5F) : lower);
            maximum = Vector3.ComponentMax(maximum, upper == lower ? upper + new Vector3(0.5F) : upper);
            found = true;
        }

        void IncludeVoxel(VoxelCoord64 coordinate)
        {
            Include(
                new Vector3(ToFiniteFloat(coordinate.X), ToFiniteFloat(coordinate.Y), ToFiniteFloat(coordinate.Z)),
                new Vector3(ToFiniteFloat((double)coordinate.X + 1D),
                    ToFiniteFloat((double)coordinate.Y + 1D),
                    ToFiniteFloat((double)coordinate.Z + 1D)));
        }

        void IncludeTransform(SceneObject item)
        {
            if (item.Transform.Mode == TransformMode.Grid)
            {
                IncludeVoxel(item.Transform.Grid.Position);
                return;
            }

            var position = item.Transform.Free.Position;
            Include(new Vector3(ToFiniteFloat(position.X), ToFiniteFloat(position.Y), ToFiniteFloat(position.Z)),
                new Vector3(ToFiniteFloat(position.X), ToFiniteFloat(position.Y), ToFiniteFloat(position.Z)));
        }

        void IncludeObject(SceneObject item)
        {
            if (!visited.Add(item.Id)) return;
            // 生成器对象的体素也存在 _objectVoxels 里，必须按实际体素外轮廓计算，否则聚焦会落到原点
            if (_objectVoxels.TryGetWorld(item.Id, out var objectWorld))
            {
                foreach (var cell in objectWorld.EnumerateOccupied()) IncludeVoxel(cell.Coordinate);
            }
            else
            {
                IncludeTransform(item);
            }

            foreach (var child in _sceneDocument?.Objects.Where(candidate => candidate.ParentId == item.Id) ?? [])
            {
                IncludeObject(child);
            }

            if (!found) IncludeTransform(item);
        }

        IncludeObject(sceneObject);
        bounds = found ? new FocusBounds(minimum, maximum) : default;
        return found;
    }

    private static Vector3 BuildCameraUp(Vector3 forward, Vector3 preferredUp)
    {
        var right = Vector3.Cross(forward, preferredUp);
        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.Cross(forward, Vector3.UnitY);
        }

        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.Cross(forward, Vector3.UnitZ);
        }

        right = NormalizeOr(right, Vector3.UnitX);
        return NormalizeOr(Vector3.Cross(right, forward), Vector3.UnitY);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared > 0.0001F &&
               float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? Vector3.Normalize(value)
            : Vector3.Normalize(fallback);
    }

    private static bool IsViewportNavigationInput(Vector2 origin, Vector2 size)
    {
        var mouse = ImGui.GetIO().MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        return inViewport &&
               (ImGui.IsMouseDragging(ImGuiMouseButton.Right) ||
                ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                MathF.Abs(ImGui.GetIO().MouseWheel) > float.Epsilon);
    }

    private void CancelFocusOnManualNavigation(Vector2 origin, Vector2 size, bool renderViewport)
    {
        if (!IsViewportNavigationInput(origin, size)) return;
        if (renderViewport)
        {
            _renderFocusAnimation = null;
            if (GetActiveRenderCameraObject() is null)
            {
                _sceneFocusAnimation = null;
            }
        }
        else
        {
            _sceneFocusAnimation = null;
        }
    }
}
