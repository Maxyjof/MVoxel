namespace MVoxel.Editor;

/// <summary>
/// 编辑器视口使用的鼠标按键
/// </summary>
internal enum EditorMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// 视口鼠标按键解析后的动作
/// </summary>
internal enum ViewportMouseAction
{
    None,
    Place,
    Destroy,
    Orbit,
}

/// <summary>
/// 集中定义场景视口和渲染视口的输入规则
/// </summary>
internal static class ViewportInputPolicy
{
    /// <summary>
    /// 根据当前编辑器模式解析鼠标按键动作
    /// </summary>
    /// <param name="button">鼠标按键</param>
    /// <param name="shiftPressed">是否按住 Shift</param>
    /// <param name="capsLockMode">是否启用 Caps Lock 游戏式视角</param>
    /// <param name="mcMode">是否启用 MC 模式</param>
    /// <returns>当前按键对应的视口动作</returns>
    public static ViewportMouseAction Resolve(EditorMouseButton button, bool shiftPressed,
        bool capsLockMode, bool mcMode)
    {
        if (button == EditorMouseButton.Middle)
        {
            return ViewportMouseAction.None;
        }

        if (!capsLockMode && shiftPressed && button == EditorMouseButton.Left)
        {
            return ViewportMouseAction.Orbit;
        }

        return button switch
        {
            EditorMouseButton.Left => mcMode ? ViewportMouseAction.Destroy : ViewportMouseAction.Place,
            EditorMouseButton.Right => mcMode ? ViewportMouseAction.Place : ViewportMouseAction.Destroy,
            _ => ViewportMouseAction.None,
        };
    }

    /// <summary>
    /// 把水平鼠标位移转换为轨道相机的水平角度增量
    /// </summary>
    /// <param name="mouseDeltaX">鼠标在水平方向的位移</param>
    /// <param name="sensitivity">相机旋转灵敏度</param>
    /// <returns>轨道相机的水平角度增量</returns>
    public static float OrbitYawDelta(float mouseDeltaX, float sensitivity = 0.008F)
    {
        return -mouseDeltaX * sensitivity;
    }

    /// <summary>
    /// 判断指定视口是否允许改变体素数据
    /// </summary>
    /// <param name="isRenderViewport">是否为渲染视口</param>
    /// <returns>场景视口返回 true，渲染视口返回 false</returns>
    public static bool CanEditVoxels(bool isRenderViewport)
    {
        return !isRenderViewport;
    }
}

/// <summary>
/// 场景相机允许使用的观察距离范围
/// </summary>
internal readonly record struct SceneZoomLimits(float MinimumDistance, float MaximumDistance);

/// <summary>
/// 根据场景包围框计算连续缩放范围
/// </summary>
internal static class SceneZoomPolicy
{
    /// <summary>
    /// 没有场景对象时返回默认缩放范围
    /// </summary>
    /// <returns>默认观察距离范围</returns>
    public static SceneZoomLimits Default()
    {
        return new SceneZoomLimits(1.25F, 256F);
    }

    /// <summary>
    /// 根据场景总包围框的三个边长计算缩放范围
    /// </summary>
    /// <param name="width">包围框 X 轴边长</param>
    /// <param name="height">包围框 Y 轴边长</param>
    /// <param name="depth">包围框 Z 轴边长</param>
    /// <returns>放大下限和缩小上限</returns>
    public static SceneZoomLimits FromBounds(double width, double height, double depth)
    {
        var diagonal = Math.Sqrt(
            Math.Max(0D, width) * Math.Max(0D, width) +
            Math.Max(0D, height) * Math.Max(0D, height) +
            Math.Max(0D, depth) * Math.Max(0D, depth));
        var minimum = 1.25F;
        var maximum = MathF.Max(minimum * 2F, (float)diagonal * 3F);
        return new SceneZoomLimits(minimum, maximum);
    }
}
