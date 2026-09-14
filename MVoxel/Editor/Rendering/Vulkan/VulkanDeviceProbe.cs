using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan 物理设备能力摘要
/// </summary>
internal sealed record VulkanDeviceInfo(
    string Name,
    uint ApiVersion,
    string ApiVersionText,
    PhysicalDeviceType DeviceType,
    bool SupportsGraphicsQueue,
    bool SupportsPresentExtension,
    bool SupportsAccelerationStructure,
    bool SupportsRayTracingPipeline,
    bool SupportsRayQuery,
    bool SupportsBufferDeviceAddress,
    string DiagnosticMessage);

/// <summary>
/// Vulkan Instance 与物理设备探测器
/// </summary>
internal sealed class VulkanDeviceProbe : IDisposable
{
    private readonly VulkanRuntime _runtime;
    private readonly Instance _instance;
    private bool _disposed;

    /// <summary>
    /// 创建 Vulkan Instance 并探测当前机器上的物理设备
    /// </summary>
    private VulkanDeviceProbe(VulkanRuntime runtime, Instance instance, IReadOnlyList<VulkanDeviceInfo> devices)
    {
        _runtime = runtime;
        _instance = instance;
        Devices = devices;
    }

    /// <summary>
    /// 当前机器的 Vulkan 物理设备列表
    /// </summary>
    internal IReadOnlyList<VulkanDeviceInfo> Devices { get; }

    /// <summary>
    /// 当前机器是否至少有一个可用于图形绘制的 Vulkan 设备
    /// </summary>
    internal bool HasGraphicsDevice => Devices.Any(static device => device.SupportsGraphicsQueue);

    /// <summary>
    /// 当前机器是否有硬件光追所需的核心能力组合
    /// </summary>
    internal bool HasHardwareRayTracingDevice => Devices.Any(static device =>
        device.SupportsGraphicsQueue &&
        device.SupportsAccelerationStructure &&
        device.SupportsRayTracingPipeline &&
        device.SupportsBufferDeviceAddress);

    /// <summary>
    /// 尝试创建 Vulkan Instance 并读取物理设备能力
    /// </summary>
    internal static bool TryCreate(VulkanRuntime runtime, out VulkanDeviceProbe? probe, out string diagnostic)
    {
        unsafe
        {
            Instance instance = default;
            ApplicationInfo applicationInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                ApiVersion = Math.Min(runtime.Info.ApiVersion, Vk.Version13)
            };
            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo
            };

            Result result = runtime.Api.CreateInstance(&createInfo, null, &instance);
            if (result != Result.Success)
            {
                probe = null;
                diagnostic = $"创建 Vulkan Instance 失败：{result}";
                return false;
            }

            try
            {
                List<VulkanDeviceInfo> devices = EnumerateDevices(runtime, instance);
                if (devices.Count == 0)
                {
                    runtime.Api.DestroyInstance(instance, null);
                    probe = null;
                    diagnostic = "Vulkan Instance 创建成功，但没有找到物理设备";
                    return false;
                }

                probe = new VulkanDeviceProbe(runtime, instance, devices);
                diagnostic = $"已探测 {devices.Count} 个 Vulkan 物理设备";
                return true;
            }
            catch
            {
                runtime.Api.DestroyInstance(instance, null);
                throw;
            }
        }
    }

    /// <summary>
    /// 释放 Vulkan Instance
    /// </summary>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Api.DestroyInstance(_instance, null);
    }

    /// <summary>
    /// 枚举并读取物理设备的基础能力
    /// </summary>
    private static unsafe List<VulkanDeviceInfo> EnumerateDevices(VulkanRuntime runtime, Instance instance)
    {
        uint deviceCount = 0;
        Result result = runtime.Api.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (result != Result.Success || deviceCount == 0)
        {
            return [];
        }

        PhysicalDevice* physicalDevices = stackalloc PhysicalDevice[(int)deviceCount];
        result = runtime.Api.EnumeratePhysicalDevices(instance, &deviceCount, physicalDevices);
        if (result != Result.Success)
        {
            return [];
        }

        List<VulkanDeviceInfo> devices = new((int)deviceCount);
        for (int index = 0; index < deviceCount; index++)
        {
            PhysicalDevice physicalDevice = physicalDevices[index];
            PhysicalDeviceProperties properties = default;
            runtime.Api.GetPhysicalDeviceProperties(physicalDevice, &properties);

            uint queueFamilyCount = 0;
            runtime.Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
            bool supportsGraphicsQueue = false;
            QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* queueFamiliesPointer = queueFamilies)
            {
                runtime.Api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, queueFamiliesPointer);
                for (int queueIndex = 0; queueIndex < queueFamilyCount; queueIndex++)
                {
                    if ((queueFamilies[queueIndex].QueueFlags & QueueFlags.GraphicsBit) != 0)
                    {
                        supportsGraphicsQueue = true;
                        break;
                    }
                }
            }

            PhysicalDeviceFeatures2 features = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2
            };
            runtime.Api.GetPhysicalDeviceFeatures2(physicalDevice, &features);

            string name = ReadFixedString(properties.DeviceName);
            devices.Add(new VulkanDeviceInfo(
                name,
                properties.ApiVersion,
                FormatVersion(properties.ApiVersion),
                properties.DeviceType,
                supportsGraphicsQueue,
                false,
                false,
                false,
                false,
                false,
                $"基础 Feature 已读取，启用的核心 Feature 数量未知"));
        }

        return devices;
    }

    /// <summary>
    /// 读取 Vulkan 固定长度的 UTF8 字符串缓冲区
    /// </summary>
    private static unsafe string ReadFixedString(byte* value, int maxLength = 256)
    {
        int length = 0;
        while (length < maxLength && value[length] != 0)
        {
            length++;
        }

        return Marshal.PtrToStringUTF8((nint)value, length) ?? "未命名 Vulkan 设备";
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
