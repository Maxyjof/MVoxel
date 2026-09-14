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
