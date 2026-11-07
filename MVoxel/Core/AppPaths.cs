namespace MVoxel.Core;

/// <summary>
/// 编辑器本地数据路径与便携模式
/// </summary>
public static class AppPaths
{
    private const string PortableMarkerName = "MVoxel.portable";
    private const string MigrationMarkerName = ".legacy-migration-v1";

    /// <summary>程序所在目录</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>是否使用程序目录下的便携数据</summary>
    public static bool IsPortable =>
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_PORTABLE"), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("MVOXEL_PORTABLE"), "true", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(Path.Combine(BaseDirectory, PortableMarkerName)) ||
        Environment.GetCommandLineArgs().Any(item => string.Equals(item, "--portable", StringComparison.OrdinalIgnoreCase));

    /// <summary>程序目录下的旧版本地数据目录</summary>
    public static string LegacyDataDirectory => Path.Combine(BaseDirectory, "MVoxelData");

    /// <summary>
    /// MVoxel本地数据根目录。默认使用用户LocalAppData，便携模式使用程序目录
    /// </summary>
    public static string DataDirectory => IsPortable
        ? LegacyDataDirectory
        : Path.Combine(
            string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                ? BaseDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MVoxel");

    /// <summary>编辑器配置目录</summary>
    public static string ConfigDirectory => Path.Combine(DataDirectory, "Config");

    /// <summary>编辑器级插件目录</summary>
    public static string PluginDirectory => Path.Combine(DataDirectory, "Plugins");

    /// <summary>可重建的Vulkan SPIR-V编译缓存</summary>
    public static string ShaderCacheDirectory => Path.Combine(DataDirectory, "Cache", "Shaders");

    /// <summary>运行中编辑器的本机控制端点文件</summary>
    public static string EditorEndpointPath => Path.Combine(DataDirectory, "editor.endpoint");

    /// <summary>运行期间使用的当前日志文件</summary>
    public static string LogFilePath => Path.Combine(DataDirectory, "log");

    /// <summary>自动保存恢复区根目录，按项目派生子目录</summary>
    public static string RecoveryDirectory => Path.Combine(DataDirectory, "Recovery");

    /// <summary>已关闭日志文件</summary>
    public static string OldLogFilePath => Path.Combine(DataDirectory, "old_log");

    /// <summary>
    /// 崩溃报告专用文件。运行日志在下次启动时会被归档覆盖，崩溃报告必须单独保留，
    /// 否则连续两次启动后现场就丢了
    /// </summary>
    public static string CrashLogFilePath => Path.Combine(DataDirectory, "crash_log");

    /// <summary>返回指定名称的编辑器配置文件路径</summary>
    public static string ConfigPath(string fileName) => Path.Combine(ConfigDirectory, fileName);

    /// <summary>
    /// 创建本地目录，并将旧版程序目录数据复制到默认用户数据目录
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(PluginDirectory);
        if (!IsPortable) MigrateLegacyData();
    }

    private static void MigrateLegacyData()
    {
        if (!Directory.Exists(LegacyDataDirectory) ||
            string.Equals(Path.GetFullPath(LegacyDataDirectory), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
            return;

        var marker = Path.Combine(DataDirectory, MigrationMarkerName);
        if (File.Exists(marker)) return;
        foreach (var source in Directory.EnumerateFiles(LegacyDataDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(LegacyDataDirectory, source);
            var destination = Path.Combine(DataDirectory, relative);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
        }
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
}
