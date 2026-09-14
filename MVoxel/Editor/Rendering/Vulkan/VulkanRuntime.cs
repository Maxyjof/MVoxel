using Silk.NET.Vulkan;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan 运行时探测结果
/// </summary>
internal sealed record VulkanRuntimeInfo(
    bool IsLoaderAvailable,
    uint ApiVersion,
    string ApiVersionText,
    string DiagnosticMessage)
{
    /// <summary>
    /// 是否满足 MVoxel 的最低 Vulkan 版本要求
    /// </summary>
    internal bool SupportsMinimumVersion => IsLoaderAvailable && ApiVersion >= Vk.Version12;
}

/// <summary>
/// Vulkan 运行时入口
/// </summary>
internal sealed class VulkanRuntime : IDisposable
{
    private readonly Vk _api;
    private bool _disposed;

    /// <summary>
    /// 使用当前系统 Vulkan Loader 创建运行时入口
    /// </summary>
    private VulkanRuntime(Vk api, VulkanRuntimeInfo info)
    {
        _api = api;
        Info = info;
    }

    /// <summary>
    /// Vulkan 能力探测结果
    /// </summary>
    internal VulkanRuntimeInfo Info { get; }

    /// <summary>
    /// Vulkan 原生 API 绑定
    /// </summary>
    internal Vk Api => _api;

    /// <summary>
    /// 尝试加载系统 Vulkan Loader 并读取实例 API 版本
    /// </summary>
    internal static bool TryCreate(out VulkanRuntime? runtime, out VulkanRuntimeInfo info)
    {
        try
        {
            Vk api = Vk.GetApi();
            uint apiVersion = Vk.Version10;
            Result result = api.EnumerateInstanceVersion(ref apiVersion);
            if (result != Result.Success && result != Result.Incomplete)
            {
                info = new VulkanRuntimeInfo(
                    true,
                    Vk.Version10,
                    FormatVersion(Vk.Version10),
                    $"Vulkan Loader 已加载，但读取实例版本失败：{result}");
                runtime = new VulkanRuntime(api, info);
                return false;
            }

            info = new VulkanRuntimeInfo(
                true,
                apiVersion,
                FormatVersion(apiVersion),
                "Vulkan Loader 已加载");
            runtime = new VulkanRuntime(api, info);
            return true;
        }
        catch (Exception exception)
        {
            info = new VulkanRuntimeInfo(
                false,
                0,
                "不可用",
                $"无法加载 Vulkan Loader：{exception.Message}");
            runtime = null;
            return false;
        }
    }

    /// <summary>
    /// 释放 Vulkan Loader 绑定
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _api.Dispose();
    }

    /// <summary>
    /// 格式化 Vulkan 版本号
    /// </summary>
    private static string FormatVersion(uint version)
    {
        uint major = version >> 22;
        uint minor = (version >> 12) & 0x3FF;
        uint patch = version & 0xFFF;
        return $"{major}.{minor}.{patch}";
    }
}
