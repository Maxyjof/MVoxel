using System.Numerics;
using System.Runtime.InteropServices;
using MVoxel.Rendering;
using MVoxel.Voxels;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan 体素视口管线的资源边界
/// </summary>
/// <remarks>
/// ImGui 与交换链使用 Vulkan
/// 稀疏体素工作集和设备本地 Storage Buffer 在本边界内同步
/// Scene View、渐进光追和导出资源继续沿用同一资源边界
/// 当前体素视口采用 CPU 预览
/// </remarks>
internal sealed unsafe class VulkanVoxelRenderPipeline : IDisposable
{
    internal const int PackedCellSize = 32;

    private readonly VulkanWindowContext? _context;
    private readonly Vk? _api;
    private readonly Device _device;
    private VkBuffer _voxelBuffer;
    private DeviceMemory _voxelMemory;
    private ulong _voxelBufferCapacity;
    private VoxelGpuWorkset _workset = VoxelGpuWorkset.Empty;
    private bool _disposed;

    /// <summary>
    /// 创建体素 Vulkan 资源边界
    /// </summary>
    /// <param name="context">窗口 Vulkan 上下文，测试和纯数据验证可以省略</param>
    internal VulkanVoxelRenderPipeline(VulkanWindowContext? context = null)
    {
        _context = context;
        _api = context?.Api;
        _device = context?.Device ?? default;
    }

    /// <summary>
    /// 获取当前体素 GPU 绘制管线是否已经可以呈现视图
    /// </summary>
    internal bool IsAvailable => false;

    /// <summary>
    /// 获取显示纹理句柄
    /// </summary>
    internal IntPtr DisplayTextureId => IntPtr.Zero;

    /// <summary>
    /// 获取显示纹理有效区域
    /// </summary>
    internal Vector2 DisplayUvMax => Vector2.One;

    /// <summary>
    /// 获取已累积的路径追踪样本数
    /// </summary>
    internal int AccumulatedSamples { get; private set; }

    /// <summary>
    /// 获取体素工作集的最小坐标
    /// </summary>
    internal VoxelRenderParameters.Vector3I RegionMin => _workset.IsEmpty
        ? default
        : _workset.ToParameterOrigin();

    /// <summary>
    /// 获取体素工作集尺寸
    /// </summary>
    internal VoxelRenderParameters.Vector3I RegionSize => _workset.IsEmpty
        ? default
        : _workset.ToParameterSize();

    /// <summary>
    /// 获取当前遍历步数预算
    /// </summary>
    internal int StepBudget => 8192;

    /// <summary>
    /// 获取发光体素数量
    /// </summary>
    internal int EmissiveLightCount => _workset.EmissiveCount;

    /// <summary>
    /// 获取当前工作集是否已经上传到 Vulkan Storage Buffer
    /// </summary>
    internal bool GpuWorksetUploaded => _voxelBuffer.Handle != 0;

    /// <summary>
    /// 获取最后一次 Vulkan 着色器诊断信息
    /// </summary>
    internal string? ShaderLog => _workset.IsEmpty
        ? "Vulkan 体素工作集为空，等待场景数据"
        : $"已同步 {_workset.Cells.Count:N0} 个体素，等待 Vulkan GPU 绘制资源初始化";

    /// <summary>
    /// 同步稀疏体素世界到 Vulkan 工作集
    /// </summary>
    /// <param name="world">体素世界</param>
    internal void SyncWorld(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.ContentVersion == _workset.SourceVersion)
        {
            return;
        }

        var next = VoxelGpuWorkset.Build(world);
        if (_context is not null && !next.IsEmpty)
        {
            UploadWorkset(next);
        }
        else if (_context is not null && _api is { } api && next.IsEmpty && _voxelBuffer.Handle != 0)
        {
            api.DeviceWaitIdle(_device);
            DestroyVoxelBuffer(api);
        }

        _workset = next;
        ResetAccumulation();
    }

    /// <summary>
    /// 获取当前同步完成的体素工作集
    /// </summary>
    internal VoxelGpuWorkset Workset => _workset;

    /// <summary>
    /// 重置渐进渲染累积
    /// </summary>
    internal void ResetAccumulation()
    {
        AccumulatedSamples = 0;
    }

    /// <summary>
    /// 尝试绘制 Vulkan 场景视图
    /// </summary>
    internal bool RenderSceneView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _ = settings;
        _ = frameState;
        _ = widthPixels;
        _ = heightPixels;
        return false;
    }

    /// <summary>
    /// 尝试绘制 Vulkan 渐进式路径追踪视图
    /// </summary>
    internal bool RenderPathView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _ = settings;
        _ = frameState;
        _ = widthPixels;
        _ = heightPixels;
        return false;
    }

    /// <summary>
    /// 尝试使用当前 Vulkan 累积结果执行呈现
    /// </summary>
    internal bool Present(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _ = settings;
        _ = frameState;
        _ = widthPixels;
        _ = heightPixels;
        return false;
    }

    /// <summary>
    /// 尝试保存当前 Vulkan 显示结果
    /// </summary>
    /// <param name="path">输出文件路径</param>
    /// <returns>当前是否保存成功</returns>
    internal bool SaveDisplayPng(string path)
    {
        _ = path;
        return false;
    }

    /// <summary>
    /// 释放 Vulkan 体素资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_api is { } api && _voxelBuffer.Handle != 0)
        {
            api.DeviceWaitIdle(_device);
            api.DestroyBuffer(_device, _voxelBuffer, null);
            api.FreeMemory(_device, _voxelMemory, null);
            _voxelBuffer = default;
            _voxelMemory = default;
        }
    }

    /// <summary>
    /// 把工作集编码为固定布局的 Storage Buffer 字节流
    /// </summary>
    /// <param name="workset">待编码的工作集</param>
    /// <returns>每个体素占用 32 字节的 GPU 数据</returns>
    internal static byte[] PackWorkset(VoxelGpuWorkset workset)
    {
        ArgumentNullException.ThrowIfNull(workset);
        var bytes = new byte[checked(workset.Cells.Count * PackedCellSize)];
        for (var index = 0; index < workset.Cells.Count; index++)
        {
            var cell = workset.Cells[index];
            var offset = index * PackedCellSize;
            var x = cell.X;
            var y = cell.Y;
            var z = cell.Z;
            var materialSlot = cell.Value.MaterialSlot;
            MemoryMarshal.Write(bytes.AsSpan(offset + 0, 4), in x);
            MemoryMarshal.Write(bytes.AsSpan(offset + 4, 4), in y);
            MemoryMarshal.Write(bytes.AsSpan(offset + 8, 4), in z);
            bytes[offset + 12] = cell.Value.Color.R;
            bytes[offset + 13] = cell.Value.Color.G;
            bytes[offset + 14] = cell.Value.Color.B;
            bytes[offset + 15] = cell.Value.Color.A;
            MemoryMarshal.Write(bytes.AsSpan(offset + 16, 2), in materialSlot);
            bytes[offset + 18] = cell.Value.Flags;
        }

        return bytes;
    }

    /// <summary>
    /// 创建或扩容设备本地体素缓冲并上传工作集
    /// </summary>
    /// <param name="workset">待上传的工作集</param>
    private void UploadWorkset(VoxelGpuWorkset workset)
    {
        if (_context is null || _api is not { } api)
        {
            return;
        }

        var bytes = PackWorkset(workset);
        var required = checked((ulong)Math.Max(bytes.Length, PackedCellSize));
        if (_voxelBuffer.Handle == 0 || _voxelBufferCapacity < required)
        {
            api.DeviceWaitIdle(_device);
            DestroyVoxelBuffer(api);
            (_voxelBuffer, _voxelMemory) = CreateBuffer(
                NextCapacity(required),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
            _voxelBufferCapacity = NextCapacity(required);
        }

        var (stagingBuffer, stagingMemory) = CreateBuffer(
            (ulong)bytes.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        try
        {
            void* mapped = null;
            Check(api.MapMemory(_device, stagingMemory, 0, (ulong)bytes.Length, 0, &mapped), "映射体素工作集暂存缓冲");
            try
            {
                fixed (byte* source = bytes)
                {
                    System.Buffer.MemoryCopy(source, mapped, bytes.Length, bytes.Length);
                }
            }
            finally
            {
                api.UnmapMemory(_device, stagingMemory);
            }

            _context.ExecuteImmediate(commandBuffer => RecordUpload(commandBuffer, stagingBuffer, bytes.Length));
        }
        finally
        {
            api.DestroyBuffer(_device, stagingBuffer, null);
            api.FreeMemory(_device, stagingMemory, null);
        }
    }

    /// <summary>
    /// 记录暂存缓冲到体素 Storage Buffer 的复制和同步
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    /// <param name="stagingBuffer">主机可见暂存缓冲</param>
    /// <param name="byteCount">复制字节数</param>
    private void RecordUpload(CommandBuffer commandBuffer, VkBuffer stagingBuffer, int byteCount)
    {
        var api = _api!;
        var copy = new BufferCopy(0, 0, (ulong)byteCount);
        api.CmdCopyBuffer(commandBuffer, stagingBuffer, _voxelBuffer, 1, in copy);
        BufferMemoryBarrier barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _voxelBuffer,
            Offset = 0,
            Size = (ulong)byteCount
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 1, in barrier, 0, null);
    }

    /// <summary>
    /// 创建带有指定用途的 Vulkan 缓冲
    /// </summary>
    /// <param name="size">缓冲字节数</param>
    /// <param name="usage">缓冲用途</param>
    /// <param name="properties">内存属性</param>
    /// <returns>缓冲和对应内存</returns>
    private (VkBuffer Buffer, DeviceMemory Memory) CreateBuffer(ulong size, BufferUsageFlags usage,
        MemoryPropertyFlags properties)
    {
        var api = _api!;
        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        VkBuffer buffer = default;
        Check(api.CreateBuffer(_device, &createInfo, null, &buffer), "创建体素工作集缓冲");
        MemoryRequirements requirements = default;
        api.GetBufferMemoryRequirements(_device, buffer, &requirements);
        DeviceMemory memory = AllocateMemory(requirements, properties);
        try
        {
            Check(api.BindBufferMemory(_device, buffer, memory, 0), "绑定体素工作集缓冲");
            return (buffer, memory);
        }
        catch
        {
            api.DestroyBuffer(_device, buffer, null);
            api.FreeMemory(_device, memory, null);
            throw;
        }
    }

    /// <summary>
    /// 按设备内存属性分配工作集内存
    /// </summary>
    /// <param name="requirements">缓冲内存需求</param>
    /// <param name="properties">所需内存属性</param>
    /// <returns>已分配的设备内存</returns>
    private DeviceMemory AllocateMemory(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        var api = _api!;
        PhysicalDeviceMemoryProperties memoryProperties = default;
        api.GetPhysicalDeviceMemoryProperties(_context!.PhysicalDevice, &memoryProperties);
        uint index = 0;
        for (; index < memoryProperties.MemoryTypeCount; index++)
        {
            if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                (memoryProperties.MemoryTypes[(int)index].PropertyFlags & properties) == properties)
            {
                break;
            }
        }

        if (index >= memoryProperties.MemoryTypeCount)
        {
            throw new InvalidOperationException($"没有满足体素工作集内存属性的 Vulkan 内存类型：{properties}");
        }

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = index
        };
        DeviceMemory memory = default;
        Check(api.AllocateMemory(_device, &allocateInfo, null, &memory), "分配体素工作集内存");
        return memory;
    }

    /// <summary>
    /// 释放当前体素 Storage Buffer
    /// </summary>
    /// <param name="api">Vulkan API</param>
    private void DestroyVoxelBuffer(Vk api)
    {
        if (_voxelBuffer.Handle == 0)
        {
            return;
        }

        api.DestroyBuffer(_device, _voxelBuffer, null);
        api.FreeMemory(_device, _voxelMemory, null);
        _voxelBuffer = default;
        _voxelMemory = default;
        _voxelBufferCapacity = 0;
    }

    /// <summary>
    /// 计算下一档缓冲容量，减少连续编辑时的重新分配
    /// </summary>
    /// <param name="required">所需最小字节数</param>
    /// <returns>不小于需求的容量</returns>
    private static ulong NextCapacity(ulong required)
    {
        var capacity = 64UL * 1024UL;
        while (capacity < required)
        {
            capacity = checked(capacity * 2UL);
        }

        return capacity;
    }

    /// <summary>
    /// 检查 Vulkan 调用结果
    /// </summary>
    /// <param name="result">Vulkan 返回值</param>
    /// <param name="operation">操作名称</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation}失败：{result}");
        }
    }
}
