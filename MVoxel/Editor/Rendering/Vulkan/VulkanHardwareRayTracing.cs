using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// 当前 Vulkan 物理设备的硬件光追能力
/// </summary>
internal readonly record struct VulkanHardwareRayTracingCapabilities(
    string DeviceName,
    bool SupportsAccelerationStructure,
    bool SupportsRayTracingPipeline,
    bool SupportsRayQuery,
    bool SupportsBufferDeviceAddress,
    bool SupportsDeferredHostOperations,
    string Diagnostic)
{
    /// <summary>
    /// 是否满足 MVoxel 硬件光追管线的最低扩展和特性组合
    /// </summary>
    internal bool SupportsHardwarePipeline =>
        SupportsAccelerationStructure && SupportsRayTracingPipeline &&
        SupportsBufferDeviceAddress && SupportsDeferredHostOperations;
}

/// <summary>
/// 读取窗口实际选择的 Vulkan 物理设备能力
/// </summary>
internal static unsafe class VulkanHardwareRayTracing
{
    /// <summary>
    /// 查询物理设备的光追扩展和 Feature 链
    /// </summary>
    /// <param name="api">Vulkan API</param>
    /// <param name="physicalDevice">待查询物理设备</param>
    /// <returns>硬件光追能力摘要</returns>
    internal static VulkanHardwareRayTracingCapabilities Query(Vk api, PhysicalDevice physicalDevice)
    {
        PhysicalDeviceProperties properties = default;
        api.GetPhysicalDeviceProperties(physicalDevice, &properties);
        var name = ReadFixedString(properties.DeviceName);
        var extensions = EnumerateExtensions(api, physicalDevice);

        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2
        };
        PhysicalDeviceAccelerationStructureFeaturesKHR acceleration = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
        };
        PhysicalDeviceRayTracingPipelineFeaturesKHR pipeline = new()
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr
        };
        PhysicalDeviceRayQueryFeaturesKHR rayQuery = new()
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr
        };
        PhysicalDeviceBufferDeviceAddressFeatures bufferAddress = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures
        };
        features.PNext = &acceleration;
        acceleration.PNext = &pipeline;
        pipeline.PNext = &rayQuery;
        rayQuery.PNext = &bufferAddress;
        api.GetPhysicalDeviceFeatures2(physicalDevice, &features);

        var supportsAcceleration = extensions.Contains(KhrAccelerationStructure.ExtensionName) &&
            acceleration.AccelerationStructure;
        var supportsPipeline = extensions.Contains(KhrRayTracingPipeline.ExtensionName) &&
            pipeline.RayTracingPipeline;
        var supportsRayQuery = extensions.Contains("VK_KHR_ray_query") && rayQuery.RayQuery;
        var supportsAddress = (extensions.Contains(KhrBufferDeviceAddress.ExtensionName) ||
            properties.ApiVersion >= Vk.Version12) && bufferAddress.BufferDeviceAddress;
        var supportsDeferred = extensions.Contains(KhrDeferredHostOperations.ExtensionName);
        var diagnostic = $"设备 {name}，AS={supportsAcceleration}，Pipeline={supportsPipeline}，" +
            $"RayQuery={supportsRayQuery}，BDA={supportsAddress}，DeferredHost={supportsDeferred}";
        return new VulkanHardwareRayTracingCapabilities(name, supportsAcceleration, supportsPipeline,
            supportsRayQuery, supportsAddress, supportsDeferred, diagnostic);
    }

    /// <summary>
    /// 读取 Vulkan 设备扩展名称
    /// </summary>
    private static HashSet<string> EnumerateExtensions(Vk api, PhysicalDevice physicalDevice)
    {
        uint count = 0;
        if (api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, &count, null) != Result.Success)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            if (api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, &count, pointer) != Result.Success)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < properties.Length; index++)
        {
            fixed (byte* name = properties[index].ExtensionName)
            {
                result.Add(ReadFixedString(name));
            }
        }

        return result;
    }

    /// <summary>
    /// 读取 Vulkan 固定长度的 UTF8 设备名
    /// </summary>
    private static string ReadFixedString(byte* value, int maxLength = 256)
    {
        var length = 0;
        while (length < maxLength && value[length] != 0)
        {
            length++;
        }

        return Marshal.PtrToStringUTF8((nint)value, length) ?? "未命名 Vulkan 设备";
    }
}
