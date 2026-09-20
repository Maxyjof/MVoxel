using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using OpenTK.Mathematics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using NumVector3 = System.Numerics.Vector3;

namespace MVoxel.Editor;

/// <summary>
/// 渲染窗口的场景相机来源、相机对象选择和变换同步
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 返回当前场景中挂载相机组件的对象
    /// </summary>
    /// <returns>按层级文档顺序排列的相机对象</returns>
    private IReadOnlyList<SceneObject> GetRenderCameraObjects()
    {
        return _sceneDocument?.Objects
                   .Where(item => item.Visible && item.Components.Any(component =>
                       SceneComponentTypes.IsCamera(component) && component.Enabled))
                   .ToArray()
               ?? Array.Empty<SceneObject>();
    }

    /// <summary>
    /// 返回渲染窗口当前使用的相机对象，没有相机对象时返回空值
    /// </summary>
    /// <returns>当前相机对象</returns>
    private SceneObject? GetActiveRenderCameraObject()
    {
        var cameras = GetRenderCameraObjects();
        if (cameras.Count == 0)
        {
            _renderCameraId = null;
            return null;
        }

        var selected = cameras.FirstOrDefault(item => item.Id == _renderCameraId);
        if (selected is not null)
        {
            return selected;
        }

        _renderCameraId = cameras[0].Id;
        return cameras[0];
    }

    /// <summary>
    /// 获取渲染窗口实际使用的相机
    /// </summary>
    /// <returns>渲染窗口相机</returns>
    private SceneCamera GetRenderViewCamera()
    {
        var cameraObject = GetActiveRenderCameraObject();
        var camera = cameraObject is null ? GetSceneCamera() : ToSceneCamera(cameraObject);
        var settings = ResolveRenderSettings(cameraObject);
        return camera with { FieldOfViewDegrees = settings.CameraFovDegrees };
    }

    /// <summary>
    /// 绘制渲染窗口顶部的相机来源选择器
    /// </summary>
    private void DrawRenderCameraSelector()
    {
        var cameras = GetRenderCameraObjects();
        var selectedCamera = GetActiveRenderCameraObject();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("渲染相机");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(220F, ImGui.GetContentRegionAvail().X - 12F));
        var currentName = selectedCamera?.Name ?? "场景视角（跟随场景）";
        if (!ImGui.BeginCombo("##RenderCameraSelector", currentName))
        {
            return;
        }

        if (cameras.Count == 0)
        {
            ImGui.TextDisabled("场景视角（跟随场景）");
            ImGui.EndCombo();
            return;
        }

        foreach (var camera in cameras)
        {
            var isSelected = camera.Id == _renderCameraId;
            if (ImGui.Selectable(camera.Name, isSelected))
            {
                _renderCameraId = camera.Id;
                _pipeline?.ResetAccumulation();
                _accumulationSignature = string.Empty;
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    /// <summary>
    /// 将相机对象的自由变换转换为视口相机基向量
    /// </summary>
    /// <param name="cameraObject">挂载相机组件的对象</param>
    /// <returns>视口相机</returns>
    private static SceneCamera ToSceneCamera(SceneObject cameraObject)
    {
        var transform = cameraObject.Transform.Mode == TransformMode.Free
            ? cameraObject.Transform.Free
            : new FreeTransform(
                new Double3(cameraObject.Transform.Grid.Position.X, cameraObject.Transform.Grid.Position.Y,
                    cameraObject.Transform.Grid.Position.Z),
                System.Numerics.Quaternion.Identity);
        var position = new Vector3(
            ToFiniteFloat(transform.Position.X),
            ToFiniteFloat(transform.Position.Y),
            ToFiniteFloat(transform.Position.Z));
        var forwardNumber = NumVector3.Transform(-NumVector3.UnitZ, transform.Rotation);
        var upNumber = NumVector3.Transform(NumVector3.UnitY, transform.Rotation);
        forwardNumber = NormalizeOr(forwardNumber, new NumVector3(0F, 0F, -1F));
        upNumber = NormalizeOr(upNumber, NumVector3.UnitY);
        var forward = new Vector3(forwardNumber.X, forwardNumber.Y, forwardNumber.Z);
        var up = new Vector3(upNumber.X, upNumber.Y, upNumber.Z);
        var right = Vector3.Cross(forward, up);
        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.Cross(forward, Vector3.UnitY);
        }

        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.Cross(forward, Vector3.UnitZ);
        }

        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.UnitX;
        }

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        var data = SceneComponentTypes.ReadCamera(cameraObject.Components.First(SceneComponentTypes.IsCamera));
        return new SceneCamera(position, position + forward * 100F, forward, right, up)
        {
            NearClip = data.NearClip,
            FarClip = data.FarClip,
            FieldOfViewDegrees = data.FieldOfViewDegrees,
        };
    }

    /// <summary>
    /// 处理选中相机对象的渲染窗口平移、旋转和推拉
    /// </summary>
    /// <param name="origin">视口左上角屏幕坐标</param>
    /// <param name="size">视口尺寸</param>
    private SceneCamera HandleRenderCameraNavigation(Vector2 origin, Vector2 size)
    {
        if (GetActiveRenderCameraObject() is not { } cameraObject)
        {
            HandleSceneNavigation(origin, size, new Vector2(-100000F, -100000F), 0F,
                allowMouseCapture: true, ViewportCaptureTarget.Render);
            return GetRenderViewCamera();
        }

        if (cameraObject.Locked)
        {
            EndSceneMouseCapture(ViewportCaptureTarget.Render);
            return GetRenderViewCamera();
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        if (!inViewport || !IsFocused || !ImGui.IsWindowHovered())
        {
            EndSceneMouseCapture(ViewportCaptureTarget.Render);
            return GetRenderViewCamera();
        }

        // 导航和绘制必须从同一个有效相机快照开始。这里不能直接读取相机组件，
        // 因为渲染窗口的 FOV 可能由当前相机的渲染配置覆盖。
        var camera = GetRenderViewCamera();
        var capsLockMode = IsCapsLockEnabled();
        if (capsLockMode)
        {
            BeginSceneMouseCapture(ViewportCaptureTarget.Render);
            if (_sceneMouseCaptured && _capturedViewport == ViewportCaptureTarget.Render)
            {
                var movement = GetFreeMoveDelta(camera, io.DeltaTime);
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
                {
                    var panScale = MathF.Max(0.1F, Vector3.Distance(camera.Position, camera.Target) * 0.01F);
                    var pan = ViewportNavigationMath.Pan(camera, io.MouseDelta, panScale);
                    WriteCameraTransform(cameraObject, camera.Position + movement + pan,
                        camera.Forward, camera.Up);
                }
                else
                {
                    var orientation = ViewportNavigationMath.Orbit(camera, io.MouseDelta);
                    WriteCameraTransform(cameraObject, camera.Position + movement,
                        orientation.Forward, orientation.Up);
                }

                return GetRenderViewCamera();
            }
        }
        else
        {
            EndSceneMouseCapture(ViewportCaptureTarget.Render);
        }

        var pivot = GetRenderOrbitPivot();
        var orbitLimits = GetRenderOrbitLimits();
        if (Math.Abs(io.MouseWheel) > float.Epsilon)
        {
            // 渲染相机的推拉必须围绕同一个场景 pivot 计算，不能沿着
            // ToSceneCamera 的临时 Target（前方 100 格）移动，否则相机穿过原点后
            // 右键环绕半径会接近 0，视觉上就退化成第一人称原地转向。
            var currentDistance = Vector3.Distance(camera.Position, pivot);
            if (!float.IsFinite(currentDistance)) currentDistance = orbitLimits.MinimumDistance;
            var nextDistance = Math.Clamp(
                MathF.Max(orbitLimits.MinimumDistance, currentDistance) * MathF.Exp(-io.MouseWheel * 0.2F),
                orbitLimits.MinimumDistance, orbitLimits.MaximumDistance);
            var dolly = ViewportNavigationMath.OrbitAroundPivot(
                camera, pivot, Vector2.Zero, orbitLimits.MinimumDistance, nextDistance);
            WriteCameraTransform(cameraObject, dolly.Position, dolly.Forward, dolly.Up);
            camera = GetRenderViewCamera();
        }

        var orbiting = ImGui.IsMouseDragging(ImGuiMouseButton.Right);
        if (orbiting)
        {
            var orbit = ViewportNavigationMath.OrbitAroundPivot(
                camera, pivot, io.MouseDelta, orbitLimits.MinimumDistance, null);
            WriteCameraTransform(cameraObject, orbit.Position, orbit.Forward, orbit.Up);
            camera = GetRenderViewCamera();
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var orbitDistance = Vector3.Distance(camera.Position, pivot);
            var panScale = MathF.Max(0.1F,
                MathF.Max(orbitLimits.MinimumDistance, float.IsFinite(orbitDistance)
                    ? orbitDistance : orbitLimits.MinimumDistance) * 0.01F);
            var pan = ViewportNavigationMath.Pan(camera, io.MouseDelta, panScale);
            WriteCameraTransform(cameraObject, camera.Position + pan, camera.Forward, camera.Up);
        }

        return GetRenderViewCamera();
    }

    private Vector3 GetRenderOrbitPivot()
    {
        if (_world.TryGetBounds(out var min, out var max))
        {
            return new Vector3(
                (float)((min.X + max.X + 1) * 0.5),
                (float)((min.Y + max.Y + 1) * 0.5),
                (float)((min.Z + max.Z + 1) * 0.5));
        }

        return Vector3.Zero;
    }

    private SceneZoomLimits GetRenderOrbitLimits()
    {
        if (!_world.TryGetBounds(out var min, out var max))
            return SceneZoomPolicy.Default();

        return SceneZoomPolicy.FromBounds(
            max.X - min.X + 1D,
            max.Y - min.Y + 1D,
            max.Z - min.Z + 1D);
    }

    /// <summary>
    /// 把视口相机的位姿写回相机对象自由变换
    /// </summary>
    /// <param name="cameraObject">相机对象</param>
    /// <param name="position">新的位置</param>
    /// <param name="forward">新的朝向</param>
    /// <param name="up">新的上方向</param>
    private void WriteCameraTransform(SceneObject cameraObject, Vector3 position, Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        var right = Vector3.Cross(forward, up);
        if (right.LengthSquared < 0.0001F)
        {
            up = Vector3.UnitY;
            right = Vector3.Cross(forward, up);
        }

        if (right.LengthSquared < 0.0001F)
        {
            up = Vector3.UnitZ;
            right = Vector3.Cross(forward, up);
        }

        if (right.LengthSquared < 0.0001F)
        {
            right = Vector3.UnitX;
        }

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        var upNumber = new NumVector3(up.X, up.Y, up.Z);
        var forwardNumber = new NumVector3(forward.X, forward.Y, forward.Z);
        var rotationMatrix = Matrix4x4.CreateWorld(NumVector3.Zero, forwardNumber, upNumber);
        var rotation = System.Numerics.Quaternion.CreateFromRotationMatrix(rotationMatrix);
        cameraObject.Transform.SetFree(new FreeTransform(
            new Double3(position.X, position.Y, position.Z), rotation));

        // 相机对象的变换是渲染窗口的唯一权威状态。同步变换标记、场景脏状态
        // 和累积结果，避免下一帧又从旧状态或旧纹理恢复到环绕前的位置。
        UpdateTransformMarker(cameraObject);
        MarkSceneDocumentDirty();
        _pipeline?.ResetAccumulation();
        _accumulationSignature = string.Empty;
        _renderViewNeedsInitialRender = true;
    }

    /// <summary>
    /// 将双精度位置安全转换到视口浮点坐标
    /// </summary>
    /// <param name="value">双精度坐标</param>
    /// <returns>有限单精度坐标</returns>
    private static float ToFiniteFloat(double value) =>
        double.IsFinite(value) ? Math.Clamp((float)value, -100_000_000F, 100_000_000F) : 0F;

    /// <summary>
    /// 返回有限且非零的单位向量
    /// </summary>
    /// <param name="value">待规范化向量</param>
    /// <param name="fallback">无效时的备用向量</param>
    /// <returns>规范化后的向量</returns>
    private static NumVector3 NormalizeOr(NumVector3 value, NumVector3 fallback)
    {
        return value.LengthSquared() > 0.000001F &&
               float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? NumVector3.Normalize(value)
            : fallback;
    }

    /// <summary>
    /// 返回当前渲染相机的稳定累积签名片段
    /// </summary>
    /// <returns>相机签名片段</returns>
    private string RenderCameraSignature()
    {
        var cameraObject = GetActiveRenderCameraObject();
        if (cameraObject is null)
        {
            return "scene-camera";
        }

        var free = cameraObject.Transform.Mode == TransformMode.Free
            ? cameraObject.Transform.Free
            : FreeTransform.Identity;
        var data = SceneComponentTypes.ReadCamera(cameraObject.Components.First(SceneComponentTypes.IsCamera));
        return $"object:{cameraObject.Id}:{free.Position.X:F4},{free.Position.Y:F4},{free.Position.Z:F4}:" +
               $"{free.Rotation.X:F5},{free.Rotation.Y:F5},{free.Rotation.Z:F5},{free.Rotation.W:F5}:" +
               $"{data.FieldOfViewDegrees:F2},{data.NearClip:F4},{data.FarClip:F1}";
    }
}
