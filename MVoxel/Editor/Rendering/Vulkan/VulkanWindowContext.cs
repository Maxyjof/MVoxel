using System.Runtime.InteropServices;
using System.Numerics;
using MVoxel.Core;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Core;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using OpenTkVector2i = OpenTK.Mathematics.Vector2i;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Windows Vulkan 窗口上下文
/// </summary>
internal sealed unsafe partial class VulkanWindowContext : IDisposable
{
    private const string SurfaceExtensionName = "VK_KHR_surface";
    private const string Win32SurfaceExtensionName = "VK_KHR_win32_surface";
    private const string SwapchainExtensionName = "VK_KHR_swapchain";

    private readonly Vk _api;
    private readonly KhrSurface _surfaceApi;
    private readonly KhrWin32Surface _win32SurfaceApi;
    private readonly KhrSwapchain _swapchainApi;
    private readonly GameWindow _window;
    private readonly Instance _instance;
    private readonly SurfaceKHR _surface;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly VulkanHardwareRayTracingCapabilities _rayTracingCapabilities;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    // 立即提交专用命令缓冲与栅栏：帧命令缓冲可能仍在 GPU 执行，
    // 上传等同步操作必须等待自己的栅栏，不能复用正在执行中的帧命令缓冲
    private readonly CommandBuffer _immediateCommandBuffer;
    private readonly Fence _immediateFence;
    private readonly VkSemaphore _imageAvailable;
    private readonly VkSemaphore _renderFinished;
    private readonly Fence _inFlight;
    private RenderPass _renderPass;
    private readonly List<Framebuffer> _framebuffers = [];
    private readonly List<ImageView> _imageViews = [];
    private VkImage[] _swapchainImages = [];
    private bool _supportsWindowCapture;
    private string? _windowCapturePath;
    private Action<string>? _windowCaptureComplete;
    private SwapchainKHR _swapchain;
    private Extent2D _extent;
    private Format _format;
    private bool _resizePending;
    private bool _deviceLost;
    private bool _disposed;
    private readonly bool _supportsTimestamps;
    private readonly uint _timestampValidBits;
    private readonly double _timestampPeriodNanoseconds;

    /// <summary>
    /// 创建 Windows Vulkan Surface 与可呈现的交换链
    /// </summary>
    internal VulkanWindowContext(GameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _api = Vk.GetApi();

        _instance = CreateInstance();
        _surfaceApi = new KhrSurface(_api.Context);
        _win32SurfaceApi = new KhrWin32Surface(_api.Context);
        _swapchainApi = new KhrSwapchain(_api.Context);
        _surface = CreateSurface(window);
        (_physicalDevice, _queueFamilyIndex) = SelectPhysicalDevice();
        _rayTracingCapabilities = VulkanHardwareRayTracing.Query(_api, _physicalDevice);
        MVoxelLog.Info(_rayTracingCapabilities.Diagnostic);
        _device = CreateDevice(_physicalDevice, _queueFamilyIndex);
        _api.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
        (_supportsTimestamps, _timestampValidBits, _timestampPeriodNanoseconds) = QueryTimestampCapabilities();
        _swapchain = CreateSwapchain(window.FramebufferSize.X, window.FramebufferSize.Y);
        _renderPass = CreateRenderPass();
        CreateSwapchainImages();
        _commandPool = CreateCommandPool();
        _commandBuffer = AllocateCommandBuffer();
        _immediateCommandBuffer = AllocateImmediateCommandBuffer();
        _immediateFence = CreateImmediateFence();
        _imageAvailable = CreateSemaphore();
        _renderFinished = CreateSemaphore();
        _inFlight = CreateFence();
    }

    /// <summary>
    /// 当前交换链的像素尺寸
    /// </summary>
    internal Extent2D Extent => _extent;

    /// <summary>
    /// 当前 Vulkan API 绑定
    /// </summary>
    internal Vk Api => _api;

    /// <summary>
    /// 当前逻辑设备
    /// </summary>
    internal Device Device => _device;

    /// <summary>
    /// 当前物理设备
    /// </summary>
    internal PhysicalDevice PhysicalDevice => _physicalDevice;

    /// <summary>
    /// 当前图形队列
    /// </summary>
    internal Queue GraphicsQueue => _queue;

    /// <summary>
    /// 当前主命令缓冲
    /// </summary>
    internal CommandBuffer CommandBuffer => _commandBuffer;

    /// <summary>
    /// 当前图形队列族索引
    /// </summary>
    internal uint QueueFamilyIndex => _queueFamilyIndex;

    /// <summary>
    /// 当前 RenderPass
    /// </summary>
    internal RenderPass RenderPass => _renderPass;

    /// <summary>
    /// 当前颜色格式
    /// </summary>
    internal Format ColorFormat => _format;

    /// <summary>
    /// 获取设备是否已经丢失
    /// </summary>
    internal bool IsDeviceLost => _deviceLost;

    /// <summary>
    /// 当前物理设备名称
    /// </summary>
    internal string DeviceName => _rayTracingCapabilities.DeviceName;

    /// <summary>
    /// 当前物理设备是否可以创建 Vulkan 硬件光追管线
    /// </summary>
    internal bool SupportsHardwareRayTracing => _rayTracingCapabilities.SupportsHardwarePipeline;

    /// <summary>
    /// 当前物理设备是否支持 Ray Query
    /// </summary>
    internal bool SupportsRayQuery => _rayTracingCapabilities.SupportsRayQuery;

    /// <summary>
    /// 当前图形队列是否支持 GPU 时间戳查询
    /// </summary>
    internal bool SupportsTimestamps => _supportsTimestamps;

    /// <summary>
    /// GPU 时间戳有效位数，不足 64 位时需要掩码
    /// </summary>
    internal uint TimestampValidBits => _timestampValidBits;

    /// <summary>
    /// 每个时间戳计数对应的纳秒数
    /// </summary>
    internal double TimestampPeriodNanoseconds => _timestampPeriodNanoseconds;

    /// <summary>
    /// 最近一帧获取交换链图像耗时毫秒
    /// </summary>
    internal double LastAcquireMs { get; private set; }

    /// <summary>
    /// 最近一帧等待上一帧栅栏耗时毫秒
    /// </summary>
    internal double LastFrameFenceWaitMs { get; private set; }

    /// <summary>
    /// 最近一帧录制命令缓冲耗时毫秒
    /// </summary>
    internal double LastRecordMs { get; private set; }

    /// <summary>
    /// 最近一帧提交命令耗时毫秒
    /// </summary>
    internal double LastSubmitMs { get; private set; }

    /// <summary>
    /// 最近一帧呈现耗时毫秒
    /// </summary>
    internal double LastPresentMs { get; private set; }

    /// <summary>
    /// 标记窗口尺寸变化，下一帧按需重建交换链资源
    /// </summary>
    internal void RequestResize()
    {
        _resizePending = true;
    }

    /// <summary>
    /// 执行一次立即提交并等待完成的初始化命令
    /// </summary>
    /// <remarks>
    /// 使用独立的命令缓冲和栅栏
    /// 帧命令缓冲可能仍在 GPU 上执行，复用它重置或提交属于未定义行为
    /// 在部分显卡驱动上会直接表现为设备丢失
    /// </remarks>
    internal void ExecuteImmediate(Action<CommandBuffer> recordCommands)
    {
        ArgumentNullException.ThrowIfNull(recordCommands);
        if (_deviceLost || _disposed) return;
        var submitWatch = System.Diagnostics.Stopwatch.StartNew();
        Check(_api.ResetCommandBuffer(_immediateCommandBuffer, 0), "重置 Vulkan 立即提交命令缓冲");
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        Check(_api.BeginCommandBuffer(_immediateCommandBuffer, in beginInfo), "开始 Vulkan 立即提交命令");
        recordCommands(_immediateCommandBuffer);
        Check(_api.EndCommandBuffer(_immediateCommandBuffer), "结束 Vulkan 立即提交命令");
        CommandBuffer commandBuffer = _immediateCommandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };
        Fence fence = _immediateFence;
        Check(_api.QueueSubmit(_queue, 1, in submitInfo, fence), "提交 Vulkan 立即提交命令");
        Check(_api.WaitForFences(_device, 1, in fence, true, ulong.MaxValue), "等待 Vulkan 立即提交命令");
        Check(_api.ResetFences(_device, 1, in fence), "复位 Vulkan 立即提交栅栏");
        ImmediateSubmitCount += 1;
        ImmediateSubmitMs += submitWatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 等待最近一帧窗口提交执行完毕，用于安全回收仍可能被帧命令缓冲引用的图像
    /// </summary>
    /// <remarks>
    /// 只等待帧栅栏，不复位它：下一帧照常先等待再复位，复位留在这里会让下一帧永久阻塞
    /// </remarks>
    internal void WaitForFrameIdle()
    {
        if (_deviceLost || _disposed || _inFlight.Handle == 0) return;
        Fence fence = _inFlight;
        Check(_api.WaitForFences(_device, 1, in fence, true, ulong.MaxValue), "等待窗口帧栅栏");
    }

    /// <summary>
    /// 获取同步提交的累计次数，供性能台架量化分块同步开销
    /// </summary>
    internal long ImmediateSubmitCount { get; private set; }

    /// <summary>
    /// 获取同步提交含等待的累计毫秒
    /// </summary>
    internal double ImmediateSubmitMs { get; private set; }

    /// <summary>
    /// 清零同步提交统计，供分阶段测量使用
    /// </summary>
    internal void ResetImmediateStats()
    {
        ImmediateSubmitCount = 0;
        ImmediateSubmitMs = 0;
    }

    /// <summary>
    /// 异步提交一条命令缓冲并绑定栅栏，不等待完成，执行顺序由队列提交顺序保证
    /// </summary>
    /// <param name="commandBuffer">已录制的命令缓冲</param>
    /// <param name="fence">完成栅栏</param>
    internal void SubmitAsync(CommandBuffer commandBuffer, Fence fence)
    {
        CommandBuffer local = commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &local,
        };
        Check(_api.QueueSubmit(_queue, 1, in submitInfo, fence), "异步提交分块计算命令");
        AsyncSubmitCount += 1;
    }

    /// <summary>
    /// 等待并复位一个栅栏，用于环形槽位复用前的回收
    /// </summary>
    /// <param name="fence">目标栅栏</param>
    internal void WaitAndResetFence(Fence fence)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Fence local = fence;
        Check(_api.WaitForFences(_device, 1, in local, true, ulong.MaxValue), "等待分块计算栅栏");
        Check(_api.ResetFences(_device, 1, in local), "复位分块计算栅栏");
        FenceWaitMs += watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 获取异步分块提交的累计次数
    /// </summary>
    internal long AsyncSubmitCount { get; private set; }

    /// <summary>
    /// 获取栅栏等待的累计毫秒
    /// </summary>
    internal double FenceWaitMs { get; private set; }

    /// <summary>
    /// 以 Vulkan 命令提交一帧纯色清屏
    /// </summary>
    internal void RenderClear(Vector4 color)
    {
        RenderFrame(color, static _ => { });
    }

    /// <summary>
    /// 在当前 Vulkan RenderPass 中记录自定义绘制命令并提交一帧
    /// </summary>
    internal void RenderFrame(Vector4 color, Action<CommandBuffer> recordCommands)
    {
        try
        {
            RenderFrameCore(color, recordCommands);
        }
        catch (Exception exception) when (IsDeviceLostException(exception))
        {
            _deviceLost = true;
            MVoxelLog.SetRuntimeContext($"Vulkan 帧提交，设备={DeviceName}，交换链尺寸={_extent.Width}x{_extent.Height}");
            MVoxelLog.Error("Vulkan 设备丢失，停止后续呈现", exception);
        }
        catch (Exception exception)
        {
            _deviceLost = true;
            MVoxelLog.SetRuntimeContext($"Vulkan 帧提交异常，设备={DeviceName}，交换链尺寸={_extent.Width}x{_extent.Height}");
            MVoxelLog.Error("Vulkan 帧提交异常，已禁用 GPU 呈现", exception);
        }
    }

    private void RenderFrameCore(Vector4 color, Action<CommandBuffer> recordCommands)
    {
        ArgumentNullException.ThrowIfNull(recordCommands);
        LastAcquireMs = 0;
        LastFrameFenceWaitMs = 0;
        LastRecordMs = 0;
        LastSubmitMs = 0;
        LastPresentMs = 0;
        if (_deviceLost || _disposed) return;
        if (!TryRecreateSwapchain())
        {
            return;
        }

        var mark = System.Diagnostics.Stopwatch.GetTimestamp();
        uint imageIndex = 0;
        Result result = _swapchainApi.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref imageIndex);
        LastAcquireMs = System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds;
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            _resizePending = true;
            return;
        }

        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan 获取交换链图像失败：{result}");
        }

        // 必须先等上一帧的命令缓冲执行完，才能重置并重新录制它
        mark = System.Diagnostics.Stopwatch.GetTimestamp();
        Fence frameFence = _inFlight;
        Check(_api.WaitForFences(_device, 1, in frameFence, true, ulong.MaxValue), "等待 Vulkan 帧栅栏");
        Check(_api.ResetFences(_device, 1, in frameFence), "复位 Vulkan 帧栅栏");
        Check(_api.ResetCommandBuffer(_commandBuffer, 0), "重置 Vulkan 帧命令缓冲");
        LastFrameFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds;

        mark = System.Diagnostics.Stopwatch.GetTimestamp();
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo
        };
        _api.BeginCommandBuffer(_commandBuffer, in beginInfo);

        ClearValue clearValue = new()
        {
            Color = new ClearColorValue(color.X, color.Y, color.Z, color.W)
        };
        RenderPassBeginInfo passInfo = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[(int)imageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
            ClearValueCount = 1,
            PClearValues = &clearValue
        };
        _api.CmdBeginRenderPass(_commandBuffer, in passInfo, SubpassContents.Inline);
        recordCommands(_commandBuffer);
        _api.CmdEndRenderPass(_commandBuffer);
        Silk.NET.Vulkan.Buffer captureBuffer = default;
        DeviceMemory captureMemory = default;
        string? capturePath = _windowCapturePath;
        Action<string>? captureComplete = _windowCaptureComplete;
        _windowCapturePath = null; _windowCaptureComplete = null;
        if (capturePath is not null)
        {
            try { RecordWindowCapture(imageIndex, out captureBuffer, out captureMemory); }
            catch (Exception exception) { captureComplete?.Invoke(exception.Message); capturePath = null; }
        }
        _api.EndCommandBuffer(_commandBuffer);
        LastRecordMs = System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds;

        mark = System.Diagnostics.Stopwatch.GetTimestamp();
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        VkSemaphore imageAvailable = _imageAvailable;
        VkSemaphore renderFinished = _renderFinished;
        CommandBuffer commandBuffer = _commandBuffer;
        SwapchainKHR swapchain = _swapchain;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &imageAvailable,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderFinished
        };
        Check(_api.QueueSubmit(_queue, 1, in submitInfo, _inFlight), "提交 Vulkan 帧命令");
        LastSubmitMs = System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds;

        if (capturePath is not null)
        {
            try
            {
                Check(_api.WaitForFences(_device, 1, in _inFlight, true, ulong.MaxValue), "等待窗口截图");
                WriteWindowCapture(capturePath, captureMemory);
                captureComplete?.Invoke("completed");
            }
            catch (Exception exception) { captureComplete?.Invoke(exception.Message); }
            finally { _api.DestroyBuffer(_device, captureBuffer, null); _api.FreeMemory(_device, captureMemory, null); }
        }

        mark = System.Diagnostics.Stopwatch.GetTimestamp();
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };
        result = _swapchainApi.QueuePresent(_queue, in presentInfo);
        if (result == Result.SuboptimalKhr)
        {
            _resizePending = true;
        }

        if (result is not (Result.Success or Result.SuboptimalKhr))
        {
            throw new InvalidOperationException($"Vulkan 呈现交换链图像失败：{result}");
        }

        LastPresentMs = System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds;
    }

    internal void QueueWindowCapture(string path, Action<string> complete)
    {
        if (_windowCapturePath is not null) { complete("已有窗口截图任务"); return; }
        _windowCapturePath = path; _windowCaptureComplete = complete;
    }

    private void RecordWindowCapture(uint imageIndex, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        buffer = default; memory = default;
        if (!_supportsWindowCapture) throw new InvalidOperationException("显卡交换链不支持窗口截图");
        if (_format is not (Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb))
            throw new InvalidOperationException($"窗口截图不支持交换链格式 {_format}");
        var bytes = checked((ulong)_extent.Width * _extent.Height * 4);
        BufferCreateInfo info = new() { SType = StructureType.BufferCreateInfo, Size = bytes, Usage = BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
        Silk.NET.Vulkan.Buffer local = default;
        Check(_api.CreateBuffer(_device, &info, null, &local), "创建窗口截图缓冲");
        DeviceMemory localMemory = default;
        try
        {
            MemoryRequirements requirements = default; _api.GetBufferMemoryRequirements(_device, local, &requirements);
            PhysicalDeviceMemoryProperties properties = default; _api.GetPhysicalDeviceMemoryProperties(_physicalDevice, &properties);
            var wanted = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
            uint index = 0;
            for (; index < properties.MemoryTypeCount; index++)
                if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 && (properties.MemoryTypes[(int)index].PropertyFlags & wanted) == wanted) break;
            if (index == properties.MemoryTypeCount) throw new InvalidOperationException("无法分配窗口截图内存");
            MemoryAllocateInfo allocation = new() { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = index };
            Check(_api.AllocateMemory(_device, &allocation, null, &localMemory), "分配窗口截图内存");
            Check(_api.BindBufferMemory(_device, local, localMemory, 0), "绑定窗口截图内存");
            var image = _swapchainImages[(int)imageIndex];
            ImageMemoryBarrier before = new() { SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.PresentSrcKhr,
                NewLayout = ImageLayout.TransferSrcOptimal, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image, SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit, DstAccessMask = AccessFlags.TransferReadBit };
            _api.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in before);
            BufferImageCopy copy = new() { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1) };
            _api.CmdCopyImageToBuffer(_commandBuffer, image, ImageLayout.TransferSrcOptimal, local, 1, in copy);
            ImageMemoryBarrier after = before; after.OldLayout = ImageLayout.TransferSrcOptimal; after.NewLayout = ImageLayout.PresentSrcKhr;
            after.SrcAccessMask = AccessFlags.TransferReadBit; after.DstAccessMask = 0;
            _api.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit, 0, 0, null, 0, null, 1, in after);
            buffer = local; memory = localMemory;
        }
        catch { _api.DestroyBuffer(_device, local, null); if (localMemory.Handle != 0) _api.FreeMemory(_device, localMemory, null); throw; }
    }

    private void WriteWindowCapture(string path, DeviceMemory memory)
    {
        var bytes = checked((int)((ulong)_extent.Width * _extent.Height * 4));
        var pixels = new byte[bytes]; void* mapped = null;
        Check(_api.MapMemory(_device, memory, 0, (ulong)bytes, 0, &mapped), "映射窗口截图内存");
        try { Marshal.Copy((IntPtr)mapped, pixels, 0, bytes); } finally { _api.UnmapMemory(_device, memory); }
        if (_format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb)
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(PngEncoder.EncodeRgba8(pixels, (int)_extent.Width, (int)_extent.Height));
    }

    /// <summary>
    /// 判断异常是否由 Vulkan 设备丢失引起
    /// </summary>
    private static bool IsDeviceLostException(Exception exception) =>
        exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 释放 Vulkan 窗口上下文和所有呈现资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _api.DeviceWaitIdle(_device);
        _api.DestroyFence(_device, _immediateFence, null);
        _api.DestroyFence(_device, _inFlight, null);
        _api.DestroySemaphore(_device, _renderFinished, null);
        _api.DestroySemaphore(_device, _imageAvailable, null);
        _api.FreeCommandBuffers(_device, _commandPool, 1, in _immediateCommandBuffer);
        _api.FreeCommandBuffers(_device, _commandPool, 1, in _commandBuffer);
        _api.DestroyCommandPool(_device, _commandPool, null);
        foreach (Framebuffer framebuffer in _framebuffers)
        {
            _api.DestroyFramebuffer(_device, framebuffer, null);
        }

        foreach (ImageView imageView in _imageViews)
        {
            _api.DestroyImageView(_device, imageView, null);
        }

        _api.DestroyRenderPass(_device, _renderPass, null);
        _swapchainApi.DestroySwapchain(_device, _swapchain, null);
        _api.DestroyDevice(_device, null);
        _surfaceApi.DestroySurface(_instance, _surface, null);
        _api.DestroyInstance(_instance, null);
        _api.Dispose();
    }

    /// <summary>
    /// 设备丢失后放弃原生资源，跳过等待与同步，避免在故障设备上再次触发驱动异常
    /// </summary>
    /// <remarks>
    /// 只做尽力而为的销毁，失败也不影响重建
    /// 不释放共享的 Vulkan API 绑定，因为后续重建还要继续使用
    /// </remarks>
    internal void Abandon()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            foreach (Framebuffer framebuffer in _framebuffers)
            {
                _api.DestroyFramebuffer(_device, framebuffer, null);
            }

            _framebuffers.Clear();
            foreach (ImageView imageView in _imageViews)
            {
                _api.DestroyImageView(_device, imageView, null);
            }

            _imageViews.Clear();
            _api.DestroyRenderPass(_device, _renderPass, null);
            _swapchainApi.DestroySwapchain(_device, _swapchain, null);
            _api.DestroyDevice(_device, null);
            _surfaceApi.DestroySurface(_instance, _surface, null);
            _api.DestroyInstance(_instance, null);
        }
        catch
        {
            // 故障设备上的销毁失败可以忽略，剩余原生对象随进程结束统一回收
        }
    }

    /// <summary>
    /// 创建 Vulkan Instance
    /// </summary>
    private Instance CreateInstance()
    {
        byte[] appName = "MVoxel\0"u8.ToArray();
        byte[] engineName = "MVoxel Vulkan Renderer\0"u8.ToArray();
        nint[] extensionPointers = CreateUtf8Pointers(
            SurfaceExtensionName, Win32SurfaceExtensionName);
        try
        {
            fixed (byte* appNamePointer = appName)
            fixed (byte* engineNamePointer = engineName)
            fixed (nint* extensionPointer = extensionPointers)
            {
                ApplicationInfo applicationInfo = new()
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = appNamePointer,
                    ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                    PEngineName = engineNamePointer,
                    EngineVersion = Vk.MakeVersion(1, 0, 0),
                    ApiVersion = Vk.Version12
                };
                InstanceCreateInfo createInfo = new()
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)extensionPointers.Length,
                    PpEnabledExtensionNames = (byte**)extensionPointer
                };
                Instance instance = default;
                Check(_api.CreateInstance(&createInfo, null, &instance), "创建 Vulkan Instance");
                return instance;
            }
        }
        finally
        {
            FreeUtf8Pointers(extensionPointers);
        }
    }

    /// <summary>
    /// 创建 Windows Surface
    /// </summary>
    private SurfaceKHR CreateSurface(GameWindow window)
    {
        Win32SurfaceCreateInfoKHR createInfo = new()
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = GetModuleHandle(null),
            Hwnd = GLFW.GetWin32Window(window.WindowPtr)
        };
        SurfaceKHR surface = default;
        Check(_win32SurfaceApi.CreateWin32Surface(_instance, in createInfo, null, out surface), "创建 Vulkan Windows Surface");
        return surface;
    }

    /// <summary>
    /// 选择同时支持图形、计算与呈现的物理设备和队列
    /// </summary>
    /// <remarks>
    /// 按设备类型与显存评分后取最高分，避免在核显加独显的机器上按枚举顺序误选弱设备
    /// 每个候选都会写进日志，便于对照实际使用的适配器和驱动版本排查驱动差异
    /// </remarks>
    private (PhysicalDevice Device, uint QueueFamilyIndex) SelectPhysicalDevice()
    {
        uint deviceCount = 0;
        Check(_api.EnumeratePhysicalDevices(_instance, ref deviceCount, null), "枚举 Vulkan 物理设备数量");
        if (deviceCount == 0)
        {
            throw new InvalidOperationException("没有可用的 Vulkan 物理设备");
        }

        PhysicalDevice[] devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPointer = devices)
        {
            Check(_api.EnumeratePhysicalDevices(_instance, ref deviceCount, devicesPointer), "枚举 Vulkan 物理设备");
        }

        PhysicalDevice bestDevice = default;
        uint bestQueueFamily = 0;
        long bestScore = long.MinValue;
        foreach (PhysicalDevice device in devices)
        {
            uint queueCount = 0;
            _api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueCount, null);
            QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueCount];
            fixed (QueueFamilyProperties* queuePointer = queueFamilies)
            {
                _api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueCount, queuePointer);
            }

            for (uint index = 0; index < queueCount; index++)
            {
                bool graphics = (queueFamilies[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                bool compute = (queueFamilies[index].QueueFlags & QueueFlags.ComputeBit) != 0;
                Bool32 present = false;
                _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out present);
                if (!graphics || !compute || present.Value == 0)
                {
                    continue;
                }

                var score = ScorePhysicalDevice(device);
                LogPhysicalDeviceCandidate(device, index, score);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDevice = device;
                    bestQueueFamily = index;
                }

                break;
            }
        }

        if (bestDevice.Handle == 0)
        {
            throw new InvalidOperationException("没有支持图形与 Windows 呈现的 Vulkan 队列");
        }

        return (bestDevice, bestQueueFamily);
    }

    /// <summary>
    /// 计算物理设备评分：独立显卡优先，同档时容量更大的显存优先
    /// </summary>
    /// <param name="device">待评分物理设备</param>
    /// <returns>评分，越大越优先</returns>
    private long ScorePhysicalDevice(PhysicalDevice device)
    {
        PhysicalDeviceProperties properties = default;
        _api.GetPhysicalDeviceProperties(device, &properties);
        long score = properties.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 4_000_000_000L,
            PhysicalDeviceType.IntegratedGpu => 1_000_000_000L,
            PhysicalDeviceType.VirtualGpu => 500_000_000L,
            PhysicalDeviceType.Cpu => 10_000_000L,
            _ => 100_000_000L,
        };
        PhysicalDeviceMemoryProperties memory = default;
        _api.GetPhysicalDeviceMemoryProperties(device, &memory);
        long deviceLocalBytes = 0;
        for (var heap = 0; heap < memory.MemoryHeapCount; heap++)
        {
            if ((memory.MemoryHeaps[(int)heap].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                deviceLocalBytes += (long)memory.MemoryHeaps[(int)heap].Size;
            }
        }

        return score + deviceLocalBytes / (1024L * 1024L);
    }

    /// <summary>
    /// 读取当前图形队列的 GPU 时间戳能力与计数周期
    /// </summary>
    /// <returns>是否支持、有效位数与每个计数对应的纳秒数</returns>
    private (bool Supported, uint ValidBits, double PeriodNanoseconds) QueryTimestampCapabilities()
    {
        PhysicalDeviceProperties properties = default;
        _api.GetPhysicalDeviceProperties(_physicalDevice, &properties);
        uint familyCount = 0;
        _api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref familyCount, null);
        if (familyCount == 0 || _queueFamilyIndex >= familyCount)
        {
            return (false, 0, 0);
        }

        QueueFamilyProperties[] families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* familyPointer = families)
        {
            _api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref familyCount, familyPointer);
        }

        var validBits = families[_queueFamilyIndex].TimestampValidBits;
        var supported = validBits > 0 && properties.Limits.TimestampComputeAndGraphics.Value != 0;
        return (supported, validBits, properties.Limits.TimestampPeriod);
    }

    /// <summary>
    /// 输出一个物理设备候选的名称、类型、厂商、设备号与驱动版本
    /// </summary>
    private void LogPhysicalDeviceCandidate(PhysicalDevice device, uint queueFamilyIndex, long score)
    {
        PhysicalDeviceProperties properties = default;
        _api.GetPhysicalDeviceProperties(device, &properties);
        var name = VulkanHardwareRayTracing.ReadFixedString(properties.DeviceName);
        // Vulkan 的驱动版本号按 22/10/12 位打包，同时保留原始值便于对照厂商自己的版本号
        var driver = properties.DriverVersion;
        var driverText = $"{driver >> 22}.{(driver >> 12) & 0x3FF}.{driver & 0xFFF}（0x{driver:X8}）";
        MVoxelLog.Info($"Vulkan 设备候选：{name}，类型 {properties.DeviceType}，" +
            $"厂商 0x{properties.VendorID:X4}，设备 0x{properties.DeviceID:X4}，" +
            $"驱动 {driverText}，队列族 {queueFamilyIndex}，评分 {score}");
    }

    /// <summary>
    /// 创建启用 VK_KHR_swapchain 的逻辑设备
    /// </summary>
    private Device CreateDevice(PhysicalDevice physicalDevice, uint queueFamilyIndex)
    {
        if (_rayTracingCapabilities.SupportsHardwarePipeline &&
            TryCreateDevice(physicalDevice, queueFamilyIndex, enableHardwareRayTracing: true, out var rayTracingDevice))
        {
            MVoxelLog.Info("已启用 Vulkan 硬件光追设备特性，渲染管线按能力选择硬件或 Compute 路径");
            return rayTracingDevice;
        }

        if (_rayTracingCapabilities.SupportsHardwarePipeline)
        {
            MVoxelLog.Warn("Vulkan 硬件光追设备特性启用失败，回退到 Vulkan Compute 路径");
        }

        if (!TryCreateDevice(physicalDevice, queueFamilyIndex, enableHardwareRayTracing: false, out var device))
        {
            throw new InvalidOperationException("创建 Vulkan 逻辑设备失败");
        }

        return device;
    }

    /// <summary>
    /// 尝试创建启用或关闭硬件光追特性的逻辑设备
    /// </summary>
    private bool TryCreateDevice(PhysicalDevice physicalDevice, uint queueFamilyIndex,
        bool enableHardwareRayTracing, out Device device)
    {
        device = default;
        float queuePriority = 1F;
        DeviceQueueCreateInfo queueInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };
        var extensions = new List<string> { SwapchainExtensionName };
        if (enableHardwareRayTracing)
        {
            extensions.Add(KhrAccelerationStructure.ExtensionName);
            extensions.Add(KhrRayTracingPipeline.ExtensionName);
            extensions.Add(KhrDeferredHostOperations.ExtensionName);
            if (_rayTracingCapabilities.SupportsRayQuery)
            {
                extensions.Add("VK_KHR_ray_query");
            }
            if (_rayTracingCapabilities.SupportsBufferDeviceAddress)
            {
                extensions.Add(KhrBufferDeviceAddress.ExtensionName);
            }
        }

        nint[] extensionPointers = CreateUtf8Pointers(extensions.ToArray());
        try
        {
            PhysicalDeviceFeatures2* featuresPointer = stackalloc PhysicalDeviceFeatures2[1];
            PhysicalDeviceAccelerationStructureFeaturesKHR* accelerationPointer =
                stackalloc PhysicalDeviceAccelerationStructureFeaturesKHR[1];
            PhysicalDeviceRayTracingPipelineFeaturesKHR* pipelinePointer =
                stackalloc PhysicalDeviceRayTracingPipelineFeaturesKHR[1];
            PhysicalDeviceRayQueryFeaturesKHR* rayQueryPointer = stackalloc PhysicalDeviceRayQueryFeaturesKHR[1];
            PhysicalDeviceBufferDeviceAddressFeatures* bufferAddressPointer =
                stackalloc PhysicalDeviceBufferDeviceAddressFeatures[1];
            *featuresPointer = new() { SType = StructureType.PhysicalDeviceFeatures2 };
            *accelerationPointer = new()
            {
                SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
                AccelerationStructure = enableHardwareRayTracing
            };
            *pipelinePointer = new()
            {
                SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
                RayTracingPipeline = enableHardwareRayTracing
            };
            *rayQueryPointer = new()
            {
                SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
                RayQuery = enableHardwareRayTracing && _rayTracingCapabilities.SupportsRayQuery
            };
            *bufferAddressPointer = new()
            {
                SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
                BufferDeviceAddress = enableHardwareRayTracing
            };
            if (enableHardwareRayTracing)
            {
                featuresPointer->PNext = accelerationPointer;
                accelerationPointer->PNext = pipelinePointer;
                pipelinePointer->PNext = rayQueryPointer;
                rayQueryPointer->PNext = bufferAddressPointer;
            }

            fixed (nint* extensionPointer = extensionPointers)
            {
                DeviceCreateInfo createInfo = new()
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = (uint)extensionPointers.Length,
                    PpEnabledExtensionNames = (byte**)extensionPointer
                };
                createInfo.PNext = enableHardwareRayTracing ? featuresPointer : null;
                Device createdDevice = default;
                var result = _api.CreateDevice(physicalDevice, &createInfo, null, &createdDevice);
                if (result != Result.Success)
                {
                    device = default;
                    MVoxelLog.Warn($"创建 Vulkan 逻辑设备失败：{result}，硬件光追={enableHardwareRayTracing}");
                    return false;
                }

                device = createdDevice;
                return true;
            }
        }
        finally
        {
            FreeUtf8Pointers(extensionPointers);
        }
    }

    /// <summary>
    /// 创建适合窗口尺寸的交换链
    /// </summary>
    private SwapchainKHR CreateSwapchain(int width, int height)
    {
        SurfaceCapabilitiesKHR capabilities = default;
        Check(_surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, &capabilities), "读取 Vulkan Surface 能力");

        uint formatCount = 0;
        Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null), "读取 Vulkan Surface 格式数量");
        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatsPointer = formats)
        {
            Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatsPointer), "读取 Vulkan Surface 格式");
        }

        // ImGui 的颜色值已经是显示空间颜色，优先使用 UNORM 交换链，避免再次经过 sRGB 编码导致整体变亮
        SurfaceFormatKHR selectedFormat = formats.FirstOrDefault(static format =>
            format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr &&
            (format.Format == Format.B8G8R8A8Unorm || format.Format == Format.R8G8B8A8Unorm));
        if (selectedFormat.Format == Format.Undefined)
        {
            selectedFormat = formats.FirstOrDefault(static format =>
                format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr);
        }
        if (selectedFormat.Format == Format.Undefined)
        {
            selectedFormat = formats[0];
        }

        uint imageCount = Math.Max(capabilities.MinImageCount + 1, 2);
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        }

        _extent = capabilities.CurrentExtent.Width != uint.MaxValue
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp((uint)Math.Max(width, 1), capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp((uint)Math.Max(height, 1), capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        _format = selectedFormat.Format;
        _supportsWindowCapture = (capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _format,
            ImageColorSpace = selectedFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | (_supportsWindowCapture ? ImageUsageFlags.TransferSrcBit : 0),
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            // 后台窗口也要完整绘制，供 MCP 从交换链读取未被遮挡的编辑器画面。
            Clipped = false
        };
        SwapchainKHR swapchain = default;
        Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, &swapchain), "创建 Vulkan Swapchain");
        return swapchain;
    }

    /// <summary>
    /// 创建单颜色附件 RenderPass
    /// </summary>
    private RenderPass CreateRenderPass()
    {
        AttachmentDescription attachment = new()
        {
            Format = _format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };
        AttachmentReference colorReference = new(0, ImageLayout.ColorAttachmentOptimal);
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference
        };
        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        RenderPassCreateInfo createInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };
        RenderPass renderPass = default;
        Check(_api.CreateRenderPass(_device, &createInfo, null, &renderPass), "创建 Vulkan RenderPass");
        return renderPass;
    }

    /// <summary>
    /// 创建交换链图像视图和帧缓冲
    /// </summary>
    private void CreateSwapchainImages()
    {
        uint imageCount = 0;
        Check(_swapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, null), "读取交换链图像数量");
        VkImage[] images = new VkImage[imageCount];
        fixed (VkImage* imagesPointer = images)
        {
            Check(_swapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPointer), "读取交换链图像");
        }

        _swapchainImages = images;
        foreach (VkImage image in images)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = _format,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            ImageView imageView = default;
            Check(_api.CreateImageView(_device, &viewInfo, null, &imageView), "创建交换链图像视图");
            _imageViews.Add(imageView);
            ImageView framebufferAttachment = imageView;

            FramebufferCreateInfo framebufferInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &framebufferAttachment,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1
            };
            Framebuffer framebuffer = default;
            Check(_api.CreateFramebuffer(_device, &framebufferInfo, null, &framebuffer), "创建交换链帧缓冲");
            _framebuffers.Add(framebuffer);
        }
    }

    /// <summary>
    /// 根据最新帧缓冲尺寸重建交换链相关资源
    /// </summary>
    private bool TryRecreateSwapchain()
    {
        OpenTkVector2i framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
        {
            return false;
        }

        if (!_resizePending &&
            _extent.Width == (uint)framebufferSize.X && _extent.Height == (uint)framebufferSize.Y)
        {
            return true;
        }

        _api.DeviceWaitIdle(_device);
        DestroySwapchainImages();
        SwapchainKHR oldSwapchain = _swapchain;
        _swapchainApi.DestroySwapchain(_device, oldSwapchain, null);
        Format oldFormat = _format;
        _swapchain = CreateSwapchain(framebufferSize.X, framebufferSize.Y);
        if (_format != oldFormat)
        {
            _api.DestroyRenderPass(_device, _renderPass, null);
            _renderPass = CreateRenderPass();
        }

        CreateSwapchainImages();
        _resizePending = false;
        return true;
    }

    /// <summary>
    /// 销毁交换链图像视图和帧缓冲
    /// </summary>
    private void DestroySwapchainImages()
    {
        foreach (Framebuffer framebuffer in _framebuffers)
        {
            _api.DestroyFramebuffer(_device, framebuffer, null);
        }

        _framebuffers.Clear();
        foreach (ImageView imageView in _imageViews)
        {
            _api.DestroyImageView(_device, imageView, null);
        }

        _imageViews.Clear();
    }

    /// <summary>
    /// 创建命令池
    /// </summary>
    private CommandPool CreateCommandPool()
    {
        CommandPoolCreateInfo createInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamilyIndex
        };
        CommandPool commandPool = default;
        Check(_api.CreateCommandPool(_device, &createInfo, null, &commandPool), "创建 Vulkan 命令池");
        return commandPool;
    }

    /// <summary>
    /// 分配单个主线程命令缓冲
    /// </summary>
    private CommandBuffer AllocateCommandBuffer()
    {
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        CommandBuffer commandBuffer = default;
        Check(_api.AllocateCommandBuffers(_device, &allocateInfo, &commandBuffer), "分配 Vulkan 命令缓冲");
        return commandBuffer;
    }

    /// <summary>
    /// 分配立即提交专用命令缓冲，与帧命令缓冲互不干扰
    /// </summary>
    private CommandBuffer AllocateImmediateCommandBuffer()
    {
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        CommandBuffer commandBuffer = default;
        Check(_api.AllocateCommandBuffers(_device, &allocateInfo, &commandBuffer), "分配 Vulkan 立即提交命令缓冲");
        return commandBuffer;
    }

    /// <summary>
    /// 创建立即提交栅栏，初始为未触发状态
    /// </summary>
    private Fence CreateImmediateFence()
    {
        FenceCreateInfo createInfo = new() { SType = StructureType.FenceCreateInfo };
        Fence fence = default;
        Check(_api.CreateFence(_device, &createInfo, null, &fence), "创建 Vulkan 立即提交栅栏");
        return fence;
    }

    /// <summary>
    /// 创建二进制信号量
    /// </summary>
    private VkSemaphore CreateSemaphore()
    {
        SemaphoreCreateInfo createInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        VkSemaphore semaphore = default;
        Check(_api.CreateSemaphore(_device, &createInfo, null, &semaphore), "创建 Vulkan 信号量");
        return semaphore;
    }

    /// <summary>
    /// 创建帧提交栅栏
    /// </summary>
    private Fence CreateFence()
    {
        FenceCreateInfo createInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };
        Fence fence = default;
        Check(_api.CreateFence(_device, &createInfo, null, &fence), "创建 Vulkan 帧栅栏");
        return fence;
    }

    /// <summary>
    /// 检查 Vulkan 调用结果
    /// </summary>
    private void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            if (result == Result.ErrorDeviceLost)
            {
                _deviceLost = true;
            }

            MVoxelLog.Error($"Vulkan 调用失败：{operation}，结果：{result}");
            throw new InvalidOperationException($"{operation}失败：{result}");
        }
    }

    /// <summary>
    /// 分配 UTF8 原生字符串指针数组
    /// </summary>
    private static nint[] CreateUtf8Pointers(params string[] values)
    {
        var pointers = new nint[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            pointers[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
        }

        return pointers;
    }

    /// <summary>
    /// 释放 UTF8 原生字符串指针数组
    /// </summary>
    private static void FreeUtf8Pointers(IEnumerable<nint> pointers)
    {
        foreach (nint pointer in pointers)
        {
            if (pointer != 0)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? moduleName);
}
