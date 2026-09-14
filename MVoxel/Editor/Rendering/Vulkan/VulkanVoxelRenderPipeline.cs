using System.Numerics;
using System.Runtime.InteropServices;
using MVoxel.Rendering;
using MVoxel.Voxels;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Shaderc;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan 体素视口管线的资源边界
/// </summary>
/// <remarks>
/// ImGui 与交换链使用 Vulkan
/// 稀疏体素工作集和设备本地 Storage Buffer 在本边界内同步
/// Scene View 使用 Vulkan Compute 直接光照预览
/// 路径追踪和渲染导出继续沿用同一资源边界逐步接入
/// </remarks>
internal sealed unsafe class VulkanVoxelRenderPipeline : IDisposable
{
    internal const int PackedCellSize = 48;

    private readonly VulkanWindowContext? _context;
    private readonly Vk? _api;
    private readonly Device _device;
    private readonly VulkanImGuiRenderer? _imguiRenderer;
    private VkBuffer _voxelBuffer;
    private DeviceMemory _voxelMemory;
    private ulong _voxelBufferCapacity;
    private VkBuffer _parameterBuffer;
    private DeviceMemory _parameterMemory;
    private DescriptorSetLayout _computeDescriptorLayout;
    private DescriptorPool _computeDescriptorPool;
    private DescriptorSet _computeDescriptorSet;
    private PipelineLayout _computePipelineLayout;
    private Pipeline _computePipeline;
    private Image _outputImage;
    private DeviceMemory _outputMemory;
    private ImageView _outputView;
    private nint _displayTextureId;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _outputIsShaderReadOnly;
    private float[]? _lastParameters;
    private VoxelGpuWorkset _workset = VoxelGpuWorkset.Empty;
    private bool _disposed;

    /// <summary>
    /// 创建体素 Vulkan 资源边界
    /// </summary>
    /// <param name="context">窗口 Vulkan 上下文，测试和纯数据验证可以省略</param>
    /// <param name="imguiRenderer">用于注册输出图像的 ImGui Vulkan 后端</param>
    internal VulkanVoxelRenderPipeline(VulkanWindowContext? context = null, VulkanImGuiRenderer? imguiRenderer = null)
    {
        _context = context;
        _api = context?.Api;
        _device = context?.Device ?? default;
        _imguiRenderer = imguiRenderer;
        if (_context is not null && _imguiRenderer is not null)
        {
            InitializeComputeResources();
        }
    }

    /// <summary>
    /// 获取当前体素 GPU 绘制管线是否已经可以呈现视图
    /// </summary>
    internal bool IsAvailable => _computePipeline.Handle != 0 && GpuWorksetUploaded;

    /// <summary>
    /// 获取显示纹理句柄
    /// </summary>
    internal IntPtr DisplayTextureId => _displayTextureId;

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
        : IsAvailable
            ? "Vulkan Compute 直接光照预览已就绪，路径追踪和硬件光追仍使用独立管线"
            : $"已同步 {_workset.Cells.Count:N0} 个体素，等待 Vulkan Compute 资源初始化";

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
        _lastParameters = null;
    }

    /// <summary>
    /// 尝试绘制 Vulkan 场景视图
    /// </summary>
    internal bool RenderSceneView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        return DispatchCompute(settings, frameState, widthPixels, heightPixels);
    }

    /// <summary>
    /// 尝试绘制 Vulkan 渐进式路径追踪视图
    /// </summary>
    internal bool RenderPathView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        return DispatchCompute(settings, frameState, widthPixels, heightPixels);
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
        return _displayTextureId != IntPtr.Zero;
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
        if (_api is { } api)
        {
            api.DeviceWaitIdle(_device);
            if (_displayTextureId != IntPtr.Zero)
            {
                _imguiRenderer?.UnregisterTexture(_displayTextureId);
                _displayTextureId = IntPtr.Zero;
            }

            DestroyOutputImage(api);
            if (_computePipeline.Handle != 0) api.DestroyPipeline(_device, _computePipeline, null);
            if (_computePipelineLayout.Handle != 0) api.DestroyPipelineLayout(_device, _computePipelineLayout, null);
            if (_computeDescriptorPool.Handle != 0) api.DestroyDescriptorPool(_device, _computeDescriptorPool, null);
            if (_computeDescriptorLayout.Handle != 0) api.DestroyDescriptorSetLayout(_device, _computeDescriptorLayout, null);
            if (_parameterBuffer.Handle != 0)
            {
                api.DestroyBuffer(_device, _parameterBuffer, null);
                api.FreeMemory(_device, _parameterMemory, null);
            }

            DestroyVoxelBuffer(api);
        }
    }

    /// <summary>
    /// 创建计算着色器、参数缓冲和描述符资源
    /// </summary>
    private void InitializeComputeResources()
    {
        try
        {
            _computeDescriptorLayout = CreateComputeDescriptorLayout();
            _computeDescriptorPool = CreateComputeDescriptorPool();
            _computeDescriptorSet = AllocateComputeDescriptorSet();
            (_parameterBuffer, _parameterMemory) = CreateBuffer(
                (ulong)(VoxelRenderParameters.SlotCount * sizeof(float)),
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            var words = VulkanShaderCompiler.Compile(SceneComputeShader, ShaderKind.ComputeShader, "MVoxel.Scene.comp");
            var shaderModule = CreateShaderModule(words);
            nint entryPoint = SilkMarshal.StringToPtr("main");
            try
            {
                PipelineShaderStageCreateInfo stage = new()
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)entryPoint
                };
                ComputePipelineCreateInfo createInfo = new()
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _computePipelineLayout = CreateComputePipelineLayout(),
                    BasePipelineIndex = -1
                };
                Check(_api!.CreateComputePipelines(_device, default, 1, in createInfo, null, out _computePipeline),
                    "创建 Vulkan 体素 Compute 管线");
            }
            finally
            {
                SilkMarshal.Free(entryPoint);
                _api!.DestroyShaderModule(_device, shaderModule, null);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// 执行一次体素 Compute 绘制并准备 ImGui 采样图像
    /// </summary>
    /// <param name="settings">当前渲染设置</param>
    /// <param name="frameState">相机与工作集状态</param>
    /// <param name="widthPixels">输出宽度</param>
    /// <param name="heightPixels">输出高度</param>
    /// <returns>是否成功完成计算绘制</returns>
    private bool DispatchCompute(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        if (!IsAvailable || _context is null || _api is not { } api || widthPixels <= 0 || heightPixels <= 0)
        {
            return false;
        }

        EnsureOutputImage((uint)widthPixels, (uint)heightPixels);
        var parameters = VoxelRenderParameters.Build(settings, frameState, frameState.FrameIndex);
        parameters[VoxelRenderParameters.VoxelCount] = _workset.Cells.Count;
        if (ParametersEqualForDisplay(parameters))
        {
            return true;
        }

        UploadParameters(parameters);
        _context.ExecuteImmediate(commandBuffer =>
        {
            TransitionOutputToGeneral(commandBuffer);
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _computePipeline);
            DescriptorSet descriptorSet = _computeDescriptorSet;
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
                0, 1, in descriptorSet, 0, null);
            api.CmdDispatch(commandBuffer, ((uint)widthPixels + 7U) / 8U, ((uint)heightPixels + 7U) / 8U, 1);
            TransitionOutputToShaderRead(commandBuffer);
        });
        _lastParameters = parameters;
        return true;
    }

    /// <summary>
    /// 判断当前参数是否与上一次显示结果相同
    /// </summary>
    /// <param name="parameters">待比较的参数块</param>
    /// <returns>参数相同时返回 true</returns>
    private bool ParametersEqualForDisplay(float[] parameters)
    {
        if (_lastParameters is null || _lastParameters.Length != parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            if (index is VoxelRenderParameters.Frame or VoxelRenderParameters.Pixel)
            {
                continue;
            }

            if (_lastParameters[index] != parameters[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 创建或重建 Compute 输出图像
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    private void EnsureOutputImage(uint width, uint height)
    {
        if (_outputImage.Handle != 0 && _outputWidth == width && _outputHeight == height)
        {
            return;
        }

        var api = _api!;
        api.DeviceWaitIdle(_device);
        if (_displayTextureId != IntPtr.Zero)
        {
            _imguiRenderer!.UnregisterTexture(_displayTextureId);
            _displayTextureId = IntPtr.Zero;
        }

        DestroyOutputImage(api);
        ImageCreateInfo createInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Image outputImage = default;
        Check(api.CreateImage(_device, &createInfo, null, &outputImage), "创建体素 Compute 输出图像");
        _outputImage = outputImage;
        MemoryRequirements requirements = default;
        api.GetImageMemoryRequirements(_device, _outputImage, &requirements);
        _outputMemory = AllocateMemory(requirements, MemoryPropertyFlags.DeviceLocalBit);
        try
        {
            Check(api.BindImageMemory(_device, _outputImage, _outputMemory, 0), "绑定体素 Compute 输出图像");
            _outputView = CreateImageView(_outputImage, Format.R8G8B8A8Unorm);
            _displayTextureId = _imguiRenderer!.RegisterExternalTexture(_outputView);
            _outputWidth = width;
            _outputHeight = height;
            _outputIsShaderReadOnly = false;
            UpdateComputeDescriptors();
        }
        catch
        {
            DestroyOutputImage(api);
            throw;
        }
    }

    /// <summary>
    /// 把当前图像转换为计算着色器可写布局
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionOutputToGeneral(CommandBuffer commandBuffer)
    {
        var api = _api!;
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _outputIsShaderReadOnly ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _outputImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = _outputIsShaderReadOnly ? AccessFlags.ShaderReadBit : 0,
            DstAccessMask = AccessFlags.ShaderWriteBit
        };
        var sourceStage = _outputIsShaderReadOnly ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit;
        api.CmdPipelineBarrier(commandBuffer, sourceStage, PipelineStageFlags.ComputeShaderBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    /// <summary>
    /// 把计算结果转换为 ImGui 采样布局
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionOutputToShaderRead(CommandBuffer commandBuffer)
    {
        var api = _api!;
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _outputImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in barrier);
        _outputIsShaderReadOnly = true;
    }

    /// <summary>
    /// 更新 Compute 参数 Storage Buffer
    /// </summary>
    /// <param name="parameters">渲染参数块</param>
    private void UploadParameters(float[] parameters)
    {
        var api = _api!;
        void* mapped = null;
        var byteCount = checked((ulong)(parameters.Length * sizeof(float)));
        Check(api.MapMemory(_device, _parameterMemory, 0, byteCount, 0, &mapped), "映射体素 Compute 参数缓冲");
        try
        {
            fixed (float* source = parameters)
            {
                System.Buffer.MemoryCopy(source, mapped, (long)byteCount, (long)byteCount);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _parameterMemory);
        }
    }

    /// <summary>
    /// 创建 Compute 描述符布局
    /// </summary>
    /// <returns>Compute 描述符布局</returns>
    private DescriptorSetLayout CreateComputeDescriptorLayout()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[2] = new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        DescriptorSetLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings
        };
        DescriptorSetLayout layout = default;
        Check(_api!.CreateDescriptorSetLayout(_device, &createInfo, null, &layout), "创建体素 Compute 描述符布局");
        return layout;
    }

    /// <summary>
    /// 创建 Compute 描述符池
    /// </summary>
    /// <returns>Compute 描述符池</returns>
    private DescriptorPool CreateComputeDescriptorPool()
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2]
        {
            new(DescriptorType.StorageBuffer, 2),
            new(DescriptorType.StorageImage, 1)
        };
        DescriptorPoolCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = sizes
        };
        DescriptorPool pool = default;
        Check(_api!.CreateDescriptorPool(_device, &createInfo, null, &pool), "创建体素 Compute 描述符池");
        return pool;
    }

    /// <summary>
    /// 分配 Compute 描述符集
    /// </summary>
    /// <returns>Compute 描述符集</returns>
    private DescriptorSet AllocateComputeDescriptorSet()
    {
        var descriptorLayout = _computeDescriptorLayout;
        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _computeDescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &descriptorLayout
        };
        DescriptorSet set = default;
        Check(_api!.AllocateDescriptorSets(_device, &allocateInfo, &set), "分配体素 Compute 描述符集");
        return set;
    }

    /// <summary>
    /// 创建 Compute 管线布局
    /// </summary>
    /// <returns>Compute 管线布局</returns>
    private PipelineLayout CreateComputePipelineLayout()
    {
        var descriptorLayout = _computeDescriptorLayout;
        PipelineLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorLayout
        };
        PipelineLayout layout = default;
        Check(_api!.CreatePipelineLayout(_device, &createInfo, null, &layout), "创建体素 Compute 管线布局");
        return layout;
    }

    /// <summary>
    /// 更新 Compute 描述符中的缓冲和输出图像
    /// </summary>
    private void UpdateComputeDescriptors()
    {
        if (_voxelBuffer.Handle == 0 || _outputView.Handle == 0) return;
        DescriptorBufferInfo voxelInfo = new(_voxelBuffer, 0, _voxelBufferCapacity);
        DescriptorImageInfo imageInfo = new(default, _outputView, ImageLayout.General);
        DescriptorBufferInfo parameterInfo = new(_parameterBuffer, 0, (ulong)(VoxelRenderParameters.SlotCount * sizeof(float)));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _computeDescriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &voxelInfo
        };
        writes[1] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _computeDescriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo
        };
        writes[2] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _computeDescriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &parameterInfo
        };
        _api!.UpdateDescriptorSets(_device, 3, writes, 0, null);
    }

    /// <summary>
    /// 创建 Vulkan ShaderModule
    /// </summary>
    /// <param name="words">SPIR-V 字节码</param>
    /// <returns>ShaderModule</returns>
    private ShaderModule CreateShaderModule(uint[] words)
    {
        fixed (uint* code = words)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(words.Length * sizeof(uint)),
                PCode = code
            };
            ShaderModule module = default;
            Check(_api!.CreateShaderModule(_device, &createInfo, null, &module), "创建体素 Compute ShaderModule");
            return module;
        }
    }

    /// <summary>
    /// 创建 RGBA8 图像视图
    /// </summary>
    /// <param name="image">目标图像</param>
    /// <param name="format">图像格式</param>
    /// <returns>图像视图</returns>
    private ImageView CreateImageView(Image image, Format format)
    {
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        ImageView view = default;
        Check(_api!.CreateImageView(_device, &createInfo, null, &view), "创建体素 Compute 图像视图");
        return view;
    }

    /// <summary>
    /// 释放 Compute 输出图像
    /// </summary>
    /// <param name="api">Vulkan API</param>
    private void DestroyOutputImage(Vk api)
    {
        if (_outputView.Handle != 0) api.DestroyImageView(_device, _outputView, null);
        if (_outputImage.Handle != 0) api.DestroyImage(_device, _outputImage, null);
        if (_outputMemory.Handle != 0) api.FreeMemory(_device, _outputMemory, null);
        _outputView = default;
        _outputImage = default;
        _outputMemory = default;
        _outputWidth = 0;
        _outputHeight = 0;
        _outputIsShaderReadOnly = false;
        _lastParameters = null;
    }

    private const string SceneComputeShader = """
        #version 450

        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        struct VoxelCell
        {
            ivec4 position;
            vec4 color;
            uvec4 metadata;
        };

        layout(set = 0, binding = 0, std430) readonly buffer VoxelBuffer
        {
            VoxelCell cells[];
        };

        layout(set = 0, binding = 1, rgba8) uniform writeonly image2D outputImage;

        layout(set = 0, binding = 2, std430) readonly buffer ParameterBuffer
        {
            float values[];
        };

        bool IntersectBox(vec3 rayOrigin, vec3 rayDirection, vec3 boxMin, vec3 boxMax,
            out float distance, out vec3 normal)
        {
            vec3 inverseDirection = 1.0 / rayDirection;
            vec3 nearPoint = (boxMin - rayOrigin) * inverseDirection;
            vec3 farPoint = (boxMax - rayOrigin) * inverseDirection;
            vec3 lower = min(nearPoint, farPoint);
            vec3 upper = max(nearPoint, farPoint);
            float nearDistance = max(max(lower.x, lower.y), lower.z);
            float farDistance = min(min(upper.x, upper.y), upper.z);
            if (farDistance < max(nearDistance, 0.0)) return false;

            distance = nearDistance > 0.0 ? nearDistance : farDistance;
            vec3 hitPoint = rayOrigin + rayDirection * distance;
            vec3 distances = min(abs(hitPoint - boxMin), abs(hitPoint - boxMax));
            if (distances.x <= distances.y && distances.x <= distances.z)
            {
                normal = vec3(hitPoint.x < (boxMin.x + boxMax.x) * 0.5 ? -1.0 : 1.0, 0.0, 0.0);
            }
            else if (distances.y <= distances.z)
            {
                normal = vec3(0.0, hitPoint.y < (boxMin.y + boxMax.y) * 0.5 ? -1.0 : 1.0, 0.0);
            }
            else
            {
                normal = vec3(0.0, 0.0, hitPoint.z < (boxMin.z + boxMax.z) * 0.5 ? -1.0 : 1.0);
            }
            return true;
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            int width = int(values[11]);
            int height = int(values[15]);
            if (pixel.x >= width || pixel.y >= height) return;

            vec2 pixelPosition = (vec2(pixel) + 0.5) / vec2(width, height);
            vec2 screenPosition = pixelPosition * 2.0 - 1.0;
            float focalPixels = max(values[3], 1.0);
            vec3 rayDirection = normalize(
                vec3(values[4], values[5], values[6]) +
                vec3(values[8], values[9], values[10]) * screenPosition.x * float(width) / focalPixels +
                vec3(values[12], values[13], values[14]) * screenPosition.y * float(height) / focalPixels);
            vec3 rayOrigin = vec3(values[0], values[1], values[2]);

            vec3 skyTop = vec3(values[40], values[41], values[42]);
            vec3 skyHorizon = vec3(values[44], values[45], values[46]);
            float skyBlend = clamp(rayDirection.y * 0.5 + 0.5, 0.0, 1.0);
            vec3 color = mix(skyHorizon, skyTop, skyBlend);
            float closestDistance = 3.402823e+38;
            vec3 hitNormal = vec3(0.0, 1.0, 0.0);
            vec4 hitColor = vec4(0.0);
            int voxelCount = int(values[94]);
            vec3 regionOrigin = vec3(values[16], values[17], values[18]);
            for (int index = 0; index < voxelCount; index++)
            {
                VoxelCell cell = cells[index];
                vec3 voxelMin = regionOrigin + vec3(cell.position.xyz);
                vec3 voxelMax = voxelMin + vec3(1.0);
                float distance;
                vec3 normal;
                if (IntersectBox(rayOrigin, rayDirection, voxelMin, voxelMax, distance, normal) &&
                    distance < closestDistance)
                {
                    closestDistance = distance;
                    hitNormal = normal;
                    hitColor = cell.color;
                }
            }

            float groundDistance = 3.402823e+38;
            if (values[35] > 0.5 && abs(rayDirection.y) > 0.00001)
            {
                groundDistance = -rayOrigin.y / rayDirection.y;
                if (groundDistance > 0.0 && groundDistance < closestDistance)
                {
                    vec3 groundPoint = rayOrigin + rayDirection * groundDistance;
                    vec3 groundColor = vec3(values[36], values[37], values[38]);
                    if (values[90] > 0.5)
                    {
                        float gridStep = max(values[89], 0.001);
                        vec2 gridPosition = groundPoint.xz / gridStep;
                        vec2 distanceToLine = abs(fract(gridPosition) - 0.5) * gridStep;
                        float lineAmount = 1.0 - smoothstep(0.0, 0.045, min(distanceToLine.x, distanceToLine.y));
                        groundColor = mix(groundColor, groundColor * 0.52 + vec3(0.16, 0.20, 0.28), lineAmount);
                        float axisWidth = 0.035;
                        if (abs(groundPoint.x) < axisWidth) groundColor = mix(groundColor, vec3(0.88, 0.28, 0.30), 0.92);
                        if (abs(groundPoint.z) < axisWidth) groundColor = mix(groundColor, vec3(0.25, 0.55, 0.95), 0.92);
                    }
                    float fade = values[34] > 0.0 ? clamp(1.0 - groundDistance / values[34], 0.0, 1.0) : 1.0;
                    color = mix(color, groundColor, fade);
                }
            }

            if (hitColor.a > 0.0)
            {
                vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
                float directLight = values[33] > 0.5 ? max(dot(hitNormal, -sunDirection), 0.0) * values[27] : 0.0;
                float ambientLight = max(values[43], 0.0);
                color = hitColor.rgb * (ambientLight + directLight);
            }

            imageStore(outputImage, pixel, vec4(clamp(color, 0.0, 1.0), 1.0));
        }
        """;

    /// <summary>
    /// 把工作集编码为固定布局的 Storage Buffer 字节流
    /// </summary>
    /// <param name="workset">待编码的工作集</param>
    /// <returns>每个体素占用 48 字节的 GPU 数据</returns>
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
            var red = cell.Value.Color.R / 255F;
            var green = cell.Value.Color.G / 255F;
            var blue = cell.Value.Color.B / 255F;
            var alpha = cell.Value.Color.A / 255F;
            MemoryMarshal.Write(bytes.AsSpan(offset + 0, 4), in x);
            MemoryMarshal.Write(bytes.AsSpan(offset + 4, 4), in y);
            MemoryMarshal.Write(bytes.AsSpan(offset + 8, 4), in z);
            MemoryMarshal.Write(bytes.AsSpan(offset + 16, 4), in red);
            MemoryMarshal.Write(bytes.AsSpan(offset + 20, 4), in green);
            MemoryMarshal.Write(bytes.AsSpan(offset + 24, 4), in blue);
            MemoryMarshal.Write(bytes.AsSpan(offset + 28, 4), in alpha);
            MemoryMarshal.Write(bytes.AsSpan(offset + 32, 2), in materialSlot);
            bytes[offset + 36] = cell.Value.Flags;
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
