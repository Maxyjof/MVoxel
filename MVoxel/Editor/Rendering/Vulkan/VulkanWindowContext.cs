using System.Runtime.InteropServices;
using System.Numerics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Core;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

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
    private readonly Instance _instance;
    private readonly SurfaceKHR _surface;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly VkSemaphore _imageAvailable;
    private readonly VkSemaphore _renderFinished;
    private readonly Fence _inFlight;
    private readonly RenderPass _renderPass;
    private readonly List<Framebuffer> _framebuffers = [];
    private readonly List<ImageView> _imageViews = [];
    private SwapchainKHR _swapchain;
    private Extent2D _extent;
    private Format _format;
    private bool _disposed;

    /// <summary>
    /// 创建 Windows Vulkan Surface 与可呈现的交换链
    /// </summary>
    internal VulkanWindowContext(GameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _api = Vk.GetApi();

        _instance = CreateInstance();
        _surfaceApi = new KhrSurface(_api.Context);
        _win32SurfaceApi = new KhrWin32Surface(_api.Context);
        _swapchainApi = new KhrSwapchain(_api.Context);
        _surface = CreateSurface(window);
        (_physicalDevice, _queueFamilyIndex) = SelectPhysicalDevice();
        _device = CreateDevice(_physicalDevice, _queueFamilyIndex);
        _api.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
        _swapchain = CreateSwapchain(window.FramebufferSize.X, window.FramebufferSize.Y);
        _renderPass = CreateRenderPass();
        CreateSwapchainImages();
        _commandPool = CreateCommandPool();
        _commandBuffer = AllocateCommandBuffer();
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
    /// 执行一次立即提交并等待完成的初始化命令
    /// </summary>
    internal void ExecuteImmediate(Action<CommandBuffer> recordCommands)
    {
        ArgumentNullException.ThrowIfNull(recordCommands);
        _api.ResetCommandBuffer(_commandBuffer, 0);
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        Check(_api.BeginCommandBuffer(_commandBuffer, in beginInfo), "开始 Vulkan 初始化命令");
        recordCommands(_commandBuffer);
        Check(_api.EndCommandBuffer(_commandBuffer), "结束 Vulkan 初始化命令");
        CommandBuffer commandBuffer = _commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };
        Check(_api.QueueSubmit(_queue, 1, in submitInfo, default), "提交 Vulkan 初始化命令");
        Check(_api.QueueWaitIdle(_queue), "等待 Vulkan 初始化命令");
    }

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
        ArgumentNullException.ThrowIfNull(recordCommands);
        uint imageIndex = 0;
        Result result = _swapchainApi.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue, _imageAvailable, default, ref imageIndex);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            return;
        }

        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan 获取交换链图像失败：{result}");
        }

        _api.WaitForFences(_device, 1, in _inFlight, true, ulong.MaxValue);
        _api.ResetFences(_device, 1, in _inFlight);
        _api.ResetCommandBuffer(_commandBuffer, 0);

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
        _api.EndCommandBuffer(_commandBuffer);

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
        _api.QueueSubmit(_queue, 1, in submitInfo, _inFlight);

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
        if (result is not (Result.Success or Result.SuboptimalKhr))
        {
            throw new InvalidOperationException($"Vulkan 呈现交换链图像失败：{result}");
        }
    }

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
        _api.DestroyFence(_device, _inFlight, null);
        _api.DestroySemaphore(_device, _renderFinished, null);
        _api.DestroySemaphore(_device, _imageAvailable, null);
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
    /// 选择同时支持图形与呈现的物理设备和队列
    /// </summary>
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
                Bool32 present = false;
                _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out present);
                if (graphics && present.Value != 0)
                {
                    return (device, index);
                }
            }
        }

        throw new InvalidOperationException("没有支持图形与 Windows 呈现的 Vulkan 队列");
    }

    /// <summary>
    /// 创建启用 VK_KHR_swapchain 的逻辑设备
    /// </summary>
    private Device CreateDevice(PhysicalDevice physicalDevice, uint queueFamilyIndex)
    {
        float queuePriority = 1F;
        DeviceQueueCreateInfo queueInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };
        nint[] extensionPointers = CreateUtf8Pointers(SwapchainExtensionName);
        try
        {
            fixed (nint* extensionPointer = extensionPointers)
            {
                DeviceCreateInfo createInfo = new()
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = 1,
                    PpEnabledExtensionNames = (byte**)extensionPointer
                };
                Device device = default;
                Check(_api.CreateDevice(physicalDevice, &createInfo, null, &device), "创建 Vulkan 逻辑设备");
                return device;
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

        SurfaceFormatKHR selectedFormat = formats.FirstOrDefault(static format =>
            format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr);
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
        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _format,
            ImageColorSpace = selectedFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
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
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
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
