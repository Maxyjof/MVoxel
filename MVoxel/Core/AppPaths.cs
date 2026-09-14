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
    /// 日志目录
    /// </summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

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
        Directory.CreateDirectory(LogDirectory);
    }
}
