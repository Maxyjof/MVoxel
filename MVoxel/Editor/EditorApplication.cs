namespace MVoxel.Editor;

/// <summary>
/// 编辑器应用生命周期入口，负责创建 Vulkan 主窗口
/// </summary>
public static class EditorApplication
{
    /// <summary>
    /// 启动主编辑器循环
    /// 独立欢迎窗口由主窗口持有，以便关闭项目后重新打开并统一施加模态输入拦截
    /// </summary>
    /// <param name="args">命令行参数，透传给主窗口</param>
    public static void Run(string[] args)
    {
        using (var editor = ImGuiEditorWindow.Create(args))
        {
            editor.Run();
        }
    }
}
