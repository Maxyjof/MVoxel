namespace MVoxel.Core;

/// <summary>
/// 绿色版应用的本地数据路径
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 程序所在目录
    /// </summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// MVoxel 本地数据根目录
    /// </summary>
    public static string DataDirectory => Path.Combine(BaseDirectory, "MVoxelData");

    /// <summary>
    /// 编辑器配置目录
    /// </summary>
    public static string ConfigDirectory => Path.Combine(DataDirectory, "Config");

    /// <summary>
    /// 编辑器级插件目录
    /// </summary>
    public static string PluginDirectory => Path.Combine(DataDirectory, "Plugins");

    /// <summary>可重建的 Vulkan SPIR-V 编译缓存</summary>
    public static string ShaderCacheDirectory => Path.Combine(DataDirectory, "Cache", "Shaders");

    /// <summary>
    /// 运行中编辑器的本机控制端点文件
    /// </summary>
    public static string EditorEndpointPath => Path.Combine(DataDirectory, "editor.endpoint");

    /// <summary>
    /// 运行期间使用的当前日志文件
    /// </summary>
    public static string LogFilePath => Path.Combine(DataDirectory, "log");

    /// <summary>
    /// 自动保存恢复区根目录，按项目派生子目录
    /// </summary>
    public static string RecoveryDirectory => Path.Combine(DataDirectory, "Recovery");

    /// <summary>
    /// 已关闭日志文件
    /// </summary>
    public static string OldLogFilePath => Path.Combine(DataDirectory, "old_log");

    /// <summary>
    /// 返回指定名称的编辑器配置文件路径
    /// </summary>
    /// <param name="fileName">配置文件名</param>
    /// <returns>配置文件绝对路径</returns>
    public static string ConfigPath(string fileName) => Path.Combine(ConfigDirectory, fileName);

    /// <summary>
    /// 创建绿色版需要的本地目录
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(PluginDirectory);
    }
}
