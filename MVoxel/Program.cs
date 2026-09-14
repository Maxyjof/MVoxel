using MVoxel.Editor;

namespace MVoxel;

/// <summary>
/// 应用入口，直接启动体素编辑器而不是命令行处理器
/// 纯 GLFW 与 ImGui 路径不使用 COM，因此无需 [STAThread] 套间特性
/// </summary>
internal static class Program
{
    /// <summary>
    /// 进程主入口，把控制权完整交给编辑器应用
    /// </summary>
    /// <param name="args">命令行参数，支持 --project 指定启动项目</param>
    private static void Main(string[] args)
    {
        EditorApplication.Run(args);
    }
}
