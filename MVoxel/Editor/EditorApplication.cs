namespace MVoxel.Editor;

/// <summary>
/// 编辑器应用生命周期入口，负责窗口创建策略与降级回退
/// </summary>
public static class EditorApplication
{
    /// <summary>
    /// 启动主编辑器循环。GLFW 要求所有原生窗口及其 ImGui 上下文都留在进程主线程；
    /// 独立欢迎窗口由主窗口持有，以便关闭项目后重新打开并统一施加模态输入拦截。
    /// GPU 体素渲染需要 OpenGL 4.5，驱动给不出 4.5 core 上下文时自动退回
    /// 3.3 壳体加 CPU 预览，而不是拒绝启动
    /// </summary>
    /// <param name="args">命令行参数，透传给主窗口</param>
    public static void Run(string[] args)
    {
        ImGuiEditorWindow editor;
        try
        {
            editor = ImGuiEditorWindow.Create(args, modernApi: true);
        }
        catch (Exception exception) when (IsContextCreationFailure(exception))
        {
            editor = ImGuiEditorWindow.Create(args, modernApi: false);
        }

        using (editor)
        {
            editor.Run();
        }
    }

    /// <summary>
    /// 判断异常是否为 GL 上下文创建失败（按类型名匹配，避免依赖具体命名空间）
    /// </summary>
    /// <param name="exception">待判断异常</param>
    /// <returns>是否属于上下文创建失败</returns>
    private static bool IsContextCreationFailure(Exception exception) =>
        exception.GetType().Name.Contains("OpenTKException", StringComparison.Ordinal) ||
        exception.GetType().Name.Contains("GraphicsContextException", StringComparison.Ordinal);
}
