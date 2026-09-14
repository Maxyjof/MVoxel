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
                   .Where(item => item.Components.Any(SceneComponentTypes.IsCamera))
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
        return cameraObject is null ? GetSceneCamera() : ToSceneCamera(cameraObject);
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
        var currentName = selectedCamera?.Name ?? "场景视角（跟随场景窗口）";
        if (!ImGui.BeginCombo("##RenderCameraSelector", currentName))
        {
            return;
        }

        if (cameras.Count == 0)
        {
            ImGui.Selectable("场景视角（跟随场景窗口）", true);
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
    private void HandleRenderCameraNavigation(Vector2 origin, Vector2 size)
    {
        if (GetActiveRenderCameraObject() is not { } cameraObject)
        {
            HandleSceneNavigation(origin, size, new Vector2(-100000F, -100000F), 0F,
                allowMouseCapture: false);
            return;
        }

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var inViewport = mouse.X >= origin.X && mouse.X <= origin.X + size.X &&
                         mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        if (!inViewport || !IsFocused || !ImGui.IsWindowHovered())
        {
            _sceneOrbiting = false;
            return;
        }

        var camera = ToSceneCamera(cameraObject);
        if (Math.Abs(io.MouseWheel) > float.Epsilon)
        {
            var dolly = MathF.Max(0.1F, Vector3.Distance(camera.Position, camera.Target) * 0.2F) * io.MouseWheel;
            WriteCameraTransform(cameraObject, camera.Position + camera.Forward * dolly, camera.Forward, camera.Up);
            camera = ToSceneCamera(cameraObject);
        }

        var orbiting = io.KeyShift && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        _sceneOrbiting = orbiting;
        if (orbiting)
        {
            var yaw = MathF.Atan2(-camera.Forward.X, -camera.Forward.Z) +
                      ViewportInputPolicy.OrbitYawDelta(io.MouseDelta.X);
            var pitch = Math.Clamp(MathF.Asin(Math.Clamp(-camera.Forward.Y, -1F, 1F)) +
                                   ViewportInputPolicy.OrbitPitchDelta(io.MouseDelta.Y), -1.55F, 1.55F);
            var direction = new Vector3(
                -MathF.Cos(pitch) * MathF.Sin(yaw),
                -MathF.Sin(pitch),
                -MathF.Cos(pitch) * MathF.Cos(yaw));
            WriteCameraTransform(cameraObject, camera.Position, direction, Vector3.UnitY);
            camera = ToSceneCamera(cameraObject);
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var panScale = MathF.Max(0.1F, Vector3.Distance(camera.Position, camera.Target) * 0.01F);
            var pan = camera.Right * (-io.MouseDelta.X * panScale) + camera.Up * (io.MouseDelta.Y * panScale);
            WriteCameraTransform(cameraObject, camera.Position + pan, camera.Forward, camera.Up);
        }
    }

    /// <summary>
    /// 把视口相机的位姿写回相机对象自由变换
    /// </summary>
    /// <param name="cameraObject">相机对象</param>
    /// <param name="position">新的位置</param>
    /// <param name="forward">新的朝向</param>
    /// <param name="up">新的上方向</param>
    private static void WriteCameraTransform(SceneObject cameraObject, Vector3 position, Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        var right = Vector3.Cross(forward, up);
        if (right.LengthSquared < 0.0001F)
        {
            up = Vector3.UnitY;
            right = Vector3.Cross(forward, up);
        }

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));
        var upNumber = new NumVector3(up.X, up.Y, up.Z);
        var forwardNumber = new NumVector3(forward.X, forward.Y, forward.Z);
        var rotationMatrix = Matrix4x4.CreateWorld(NumVector3.Zero, forwardNumber, upNumber);
        var rotation = System.Numerics.Quaternion.CreateFromRotationMatrix(rotationMatrix);
        cameraObject.Transform.SetFree(new FreeTransform(
            new Double3(position.X, position.Y, position.Z), rotation));
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
        return $"object:{cameraObject.Id}:{free.Position.X:F4},{free.Position.Y:F4},{free.Position.Z:F4}:" +
               $"{free.Rotation.X:F5},{free.Rotation.Y:F5},{free.Rotation.Z:F5},{free.Rotation.W:F5}";
    }
}
