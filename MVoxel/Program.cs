using MVoxel.Core;
using MVoxel.Editor;

namespace MVoxel;

/// <summary>
/// 应用入口，直接启动体素编辑器
/// </summary>
internal static class Program
{
    /// <summary>
    /// 进程主入口，把控制权完整交给编辑器应用
    /// </summary>
    /// <param name="args">命令行参数，支持 --project 指定启动项目</param>
    private static void Main(string[] args)
    {
        MVoxelLog.Initialize();
        MVoxelLog.Info($"启动 MVoxel，程序目录：{AppPaths.BaseDirectory}");
        try
        {
            EditorApplication.Run(args);
        }
        catch (Exception exception)
        {
            MVoxelLog.RecordCrash("应用主循环异常结束", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            MVoxelLog.Shutdown();
        }
    }
}
