using System.Numerics;
using System.Runtime.InteropServices;
using MVoxel.Core;
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
/// Scene View 使用 Vulkan Compute 只进行体素求交和原色显示
/// 路径追踪和渲染导出继续沿用同一资源边界逐步接入
/// </remarks>
internal sealed unsafe class VulkanVoxelRenderPipeline : IDisposable
{
    internal const int PackedCellSize = 48;

    private readonly VulkanWindowContext? _context;
    private readonly Vk? _api;
    private readonly Device _device;
    private readonly VulkanImGuiRenderer? _imguiRenderer;
    private VulkanHardwareRayTracingPipeline? _hardwareRayTracingPipeline;
    private VkBuffer _voxelBuffer;
    private DeviceMemory _voxelMemory;
    private ulong _voxelBufferCapacity;
    private VkBuffer _bvhBuffer;
    private DeviceMemory _bvhMemory;
    private ulong _bvhBufferCapacity;
    private int _bvhNodeCount;
    private DescriptorSetLayout _computeDescriptorLayout;
    private DescriptorPool _computeDescriptorPool;
    private PipelineLayout _computePipelineLayout;
    private Pipeline _computePipeline;
    private VkBuffer _materialTableBuffer;
    private DeviceMemory _materialTableMemory;
    private float[] _materialTable = [];

    // 分块提交环形队列：每槽拥有独立的参数缓冲、命令缓冲、栅栏与描述符集，
    // 分块一提交就走，槽位复用时才等自己的栅栏，GPU 分块之间不再被 CPU 等待打断
    private const int TileRingDepth = 3;
    private readonly VkBuffer[] _ringParameterBuffers = new VkBuffer[TileRingDepth];
    private readonly DeviceMemory[] _ringParameterMemory = new DeviceMemory[TileRingDepth];
    private readonly DescriptorSet[] _ringSets = new DescriptorSet[TileRingDepth];
    private readonly CommandBuffer[] _ringCommands = new CommandBuffer[TileRingDepth];
    private readonly Fence[] _ringFences = new Fence[TileRingDepth];
    private readonly bool[] _ringInFlight = new bool[TileRingDepth];
    private CommandPool _ringCommandPool;
    private int _ringNext;

    private Image _outputImage;
    private DeviceMemory _outputMemory;
    private ImageView _outputView;
    private Image _accumulationImage;
    private DeviceMemory _accumulationMemory;
    private ImageView _accumulationView;
    private nint _displayTextureId;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _outputIsShaderReadOnly;
    private bool _accumulationInitialized;
    private bool _clearAccumulationPending = true;
    private float[]? _lastParameters;
    private VoxelGpuWorkset _workset = VoxelGpuWorkset.Empty;
    private bool _disposed;
    private bool _usingHardwareRayTracing;
    private int _worksetMaximumCells;

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
    internal bool IsAvailable => (_context is null || !_context.IsDeviceLost) &&
        _computePipeline.Handle != 0 && GpuWorksetUploaded && _bvhNodeCount > 0;

    /// <summary>
    /// 获取显示纹理句柄
    /// </summary>
    internal IntPtr DisplayTextureId => _usingHardwareRayTracing && _hardwareRayTracingPipeline is not null
        ? _hardwareRayTracingPipeline.DisplayTextureId
        : _displayTextureId;

    /// <summary>
    /// 获取最近一次渲染是否实际使用硬件 RT
    /// </summary>
    internal bool UsingHardwareRayTracing => _usingHardwareRayTracing;

    /// <summary>
    /// 获取当前显示结果是否支持 PNG 回读
    /// </summary>
    internal bool CanSaveDisplayPng => _usingHardwareRayTracing
        ? _hardwareRayTracingPipeline?.CanSaveDisplayPng == true
        : IsAvailable && _outputImage.Handle != 0 && _outputIsShaderReadOnly;

    /// <summary>
    /// 获取硬件 RT 最近一次错误
    /// </summary>
    internal string? HardwareRayTracingError => _hardwareRayTracingPipeline?.LastError;

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
            ? $"Vulkan GPU BVH 路径追踪已就绪，工作集 {_workset.Cells.Count:N0} 个体素，节点 {_bvhNodeCount:N0} 个" +
              (LastSyncWasBudgeted ? "，场景超出显存预算已采样显示" : null)
            : $"已同步 {_workset.Cells.Count:N0} 个体素，等待 Vulkan Compute 资源初始化";

    /// <summary>
    /// 完整工作集允许的最大体素数：约 384MB 打包记录加同量级 BVH，
    /// 超过上限的渲染视图请求自动降级为采样，避免超大场景必然的内存耗尽或设备丢失
    /// </summary>
    internal const int MaxFullWorksetCells = 8_000_000;

    /// <summary>
    /// 获取最近一次同步是否因超出显存预算而被采样降级
    /// </summary>
    internal bool LastSyncWasBudgeted { get; private set; }

    /// <summary>
    /// 同步稀疏体素世界到 Vulkan 工作集
    /// </summary>
    /// <param name="world">体素世界</param>
    internal void SyncWorld(VoxelWorld world, int maximumCells = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.ContentVersion == _workset.SourceVersion && maximumCells == _worksetMaximumCells)
        {
            return;
        }

        var requestedFull = maximumCells == 0 && world.OccupiedVoxelCount > MaxFullWorksetCells;
        if (requestedFull)
        {
            maximumCells = MaxFullWorksetCells;
            MVoxelLog.Warn($"渲染工作集超出显存预算：世界 {world.OccupiedVoxelCount:N0} 体素，已采样降级到 {MaxFullWorksetCells:N0} 体素");
        }

        LastSyncWasBudgeted = requestedFull;
        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var next = VoxelGpuWorkset.Build(world, maximumCells);
        SyncWorksetBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
        SyncPackMs = 0;
        SyncUploadMs = 0;
        if (_context is not null && !next.IsEmpty)
        {
            UploadWorkset(next);
        }
        else if (_context is not null && _api is { } api && next.IsEmpty && _voxelBuffer.Handle != 0)
        {
            api.DeviceWaitIdle(_device);
            DestroyBvhBuffer(api);
            DestroyVoxelBuffer(api);
            _bvhNodeCount = 0;
        }

        _workset = next;
        _worksetMaximumCells = maximumCells;
        SyncTotalCells = next.Cells.Count;
        ResetAccumulation();
    }

    /// <summary>
    /// 获取当前同步完成的体素工作集
    /// </summary>
    internal VoxelGpuWorkset Workset => _workset;

    /// <summary>
    /// 上传材质槽表：长度为 TableSlots × FloatsPerSlot 的打包数组，
    /// 着色器按体素 MaterialSlot 寻址覆盖全局表面属性
    /// </summary>
    /// <param name="table">打包后的槽位表</param>
    internal void SetMaterialTable(float[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var expected = MVoxel.Project.MaterialAsset.TableSlots * MVoxel.Project.MaterialAsset.FloatsPerSlot;
        if (table.Length != expected)
            throw new ArgumentException($"材质槽表长度应为 {expected}，实际 {table.Length}", nameof(table));
        if (_context is null || _api is not { } api || _materialTableBuffer.Handle == 0)
        {
            _materialTable = table;
            return;
        }

        void* mapped = null;
        var byteCount = (ulong)(table.Length * sizeof(float));
        Check(api.MapMemory(_device, _materialTableMemory, 0, byteCount, 0, &mapped), "映射材质槽表缓冲");
        try
        {
            fixed (float* source = table)
            {
                System.Buffer.MemoryCopy(source, mapped, (long)byteCount, (long)byteCount);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _materialTableMemory);
        }

        _materialTable = table;
    }

    /// <summary>
    /// 获取最近一次同步中工作集构建耗时毫秒
    /// </summary>
    internal double SyncWorksetBuildMs { get; private set; }

    /// <summary>
    /// 获取最近一次同步中 BVH 构建耗时毫秒
    /// </summary>
    internal double SyncBvhBuildMs { get; private set; }

    /// <summary>
    /// 获取最近一次同步中体素打包耗时毫秒
    /// </summary>
    internal double SyncPackMs { get; private set; }

    /// <summary>
    /// 获取最近一次同步中显存上传耗时毫秒
    /// </summary>
    internal double SyncUploadMs { get; private set; }

    /// <summary>
    /// 获取最近一次同步的工作集体素数
    /// </summary>
    internal int SyncTotalCells { get; private set; }

    /// <summary>
    /// 获取体素与 BVH 显存缓冲的容量字节数
    /// </summary>
    internal (ulong VoxelBytes, ulong BvhBytes) GpuBufferCapacity => (_voxelBufferCapacity, _bvhBufferCapacity);

    /// <summary>
    /// 重置渐进渲染累积
    /// </summary>
    internal void ResetAccumulation()
    {
        AccumulatedSamples = 0;
        _lastParameters = null;
        _clearAccumulationPending = true;
    }

    /// <summary>
    /// 尝试绘制 Vulkan 场景视图
    /// </summary>
    internal bool RenderSceneView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _usingHardwareRayTracing = false;
        if (_context?.IsDeviceLost == true) return false;
        return DispatchCompute(settings, frameState, widthPixels, heightPixels, pathMode: false);
    }

    /// <summary>
    /// 尝试绘制 Vulkan 渐进式路径追踪视图
    /// </summary>
    internal bool RenderPathView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _usingHardwareRayTracing = false;
        if (_context?.IsDeviceLost == true) return false;
        if (settings.UseHardwareRayTracing && _context is not null && _imguiRenderer is not null &&
            _context.SupportsHardwareRayTracing)
        {
            _hardwareRayTracingPipeline ??= new VulkanHardwareRayTracingPipeline(_context, _imguiRenderer);
            if (_hardwareRayTracingPipeline.TryRender(_workset, settings, frameState, widthPixels, heightPixels))
            {
                _usingHardwareRayTracing = true;
                // 当前硬件 RT 输出是单帧结果，保持到相机、场景或渲染设置变化后再重算
                AccumulatedSamples = Math.Max(1, settings.PathMaxAccumulatedSamples);
                return true;
            }

            // 硬件光追失败后不能对已经丢失的设备继续提交 Compute 命令，
            // 否则会把一次设备丢失扩大成连续的原生驱动调用异常。
            if (_context.IsDeviceLost) return false;
        }

        return DispatchCompute(settings, frameState, widthPixels, heightPixels, pathMode: true);
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
        return DisplayTextureId != IntPtr.Zero;
    }

    /// <summary>
    /// 尝试保存当前 Vulkan 显示结果
    /// </summary>
    /// <param name="path">输出文件路径</param>
    /// <returns>当前是否保存成功</returns>
    internal bool SaveDisplayPng(string path)
    {
        if (_usingHardwareRayTracing)
        {
            return _hardwareRayTracingPipeline?.SaveDisplayPng(path) == true;
        }

        if (_context is null || _api is not { } api ||
            _outputImage.Handle == 0 || !_outputIsShaderReadOnly || _outputWidth == 0 || _outputHeight == 0)
        {
            return false;
        }

        var byteCount = checked((ulong)_outputWidth * _outputHeight * 4);
        var (stagingBuffer, stagingMemory) = CreateBuffer(
            byteCount,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        try
        {
            byte[] pixels = new byte[checked((int)byteCount)];
            _context.ExecuteImmediate(commandBuffer =>
            {
                ImageMemoryBarrier toTransfer = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _outputImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferReadBit
                };
                api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.FragmentShaderBit,
                    PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toTransfer);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageOffset = default,
                    ImageExtent = new Extent3D(_outputWidth, _outputHeight, 1)
                };
                api.CmdCopyImageToBuffer(commandBuffer, _outputImage, ImageLayout.TransferSrcOptimal,
                    stagingBuffer, 1, in copy);

                ImageMemoryBarrier backToShader = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _outputImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit
                };
                api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in backToShader);
            });

            void* mapped = null;
            Check(api.MapMemory(_device, stagingMemory, 0, byteCount, 0, &mapped), "映射渲染导出暂存缓冲");
            try
            {
                Marshal.Copy((nint)mapped, pixels, 0, pixels.Length);
            }
            finally
            {
                api.UnmapMemory(_device, stagingMemory);
            }

            AtomicFile.WriteBytes(path, PngEncoder.EncodeRgba8(pixels, (int)_outputWidth, (int)_outputHeight));
            return true;
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("导出 Vulkan 渲染图像失败", exception);
            return false;
        }
        finally
        {
            api.DestroyBuffer(_device, stagingBuffer, null);
            api.FreeMemory(_device, stagingMemory, null);
        }
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
            // 设备已丢失后，继续等待队列或逐项销毁原生资源可能再次触发驱动异常。
            // 此时放弃管线的原生清理，最终由 Vulkan 设备销毁统一回收。
            if (_context?.IsDeviceLost == true)
            {
                _hardwareRayTracingPipeline = null;
                _displayTextureId = IntPtr.Zero;
                return;
            }

            api.DeviceWaitIdle(_device);
            _hardwareRayTracingPipeline?.Dispose();
            _hardwareRayTracingPipeline = null;
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
            for (var slot = 0; slot < TileRingDepth; slot++)
            {
                if (_ringFences[slot].Handle != 0) api.DestroyFence(_device, _ringFences[slot], null);
                if (_ringParameterBuffers[slot].Handle != 0)
                {
                    api.DestroyBuffer(_device, _ringParameterBuffers[slot], null);
                    api.FreeMemory(_device, _ringParameterMemory[slot], null);
                }
            }

            if (_ringCommandPool.Handle != 0) api.DestroyCommandPool(_device, _ringCommandPool, null);
            if (_materialTableBuffer.Handle != 0)
            {
                api.DestroyBuffer(_device, _materialTableBuffer, null);
                api.FreeMemory(_device, _materialTableMemory, null);
            }

            DestroyBvhBuffer(api);
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
            AllocateComputeDescriptorSets();
            var parameterBytes = (ulong)(VoxelRenderParameters.SlotCount * sizeof(float));
            for (var slot = 0; slot < TileRingDepth; slot++)
            {
                (_ringParameterBuffers[slot], _ringParameterMemory[slot]) = CreateBuffer(
                    parameterBytes,
                    BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }

            CreateRingCommands();
            if (_context is not null)
            {
                var tableBytes = (ulong)(MVoxel.Project.MaterialAsset.TableSlots *
                    MVoxel.Project.MaterialAsset.FloatsPerSlot * sizeof(float));
                (_materialTableBuffer, _materialTableMemory) = CreateBuffer(
                    tableBytes,
                    BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                SetMaterialTable(MVoxel.Project.MaterialAsset.PackTable([]));
            }

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
        int widthPixels, int heightPixels, bool pathMode)
    {
        if (!IsAvailable || _context is null || _api is not { } api || widthPixels <= 0 || heightPixels <= 0)
        {
            return false;
        }

        EnsureOutputImage((uint)widthPixels, (uint)heightPixels);
        var parameters = VoxelRenderParameters.Build(settings, frameState, frameState.FrameIndex);
        parameters[VoxelRenderParameters.VoxelCount] = _workset.Cells.Count;
        parameters[VoxelRenderParameters.BvhNodeCount] = _bvhNodeCount;
        if (!pathMode && ParametersEqualForDisplay(parameters))
        {
            return true;
        }

        if (pathMode && _clearAccumulationPending)
        {
            ClearAccumulation();
        }

        // 将一整帧拆成有限尺寸的分块，避免路径追踪在单次 GPU 调度中触发 Windows TDR。
        // 分块不会改变采样、反弹或输出分辨率，只改变命令提交粒度。
        // 每个分块仍是独立一次提交，单块 GPU 时长不变；提交走环形队列，
        // CPU 录完即走，只有环满需要复用槽位时才等待对应栅栏。
        const int tileSize = 128;
        for (var tileY = 0; tileY < heightPixels; tileY += tileSize)
        {
            var tileHeight = Math.Min(tileSize, heightPixels - tileY);
            for (var tileX = 0; tileX < widthPixels; tileX += tileSize)
            {
                var tileWidth = Math.Min(tileSize, widthPixels - tileX);
                parameters[VoxelRenderParameters.TileOffsetX] = tileX;
                parameters[VoxelRenderParameters.TileOffsetY] = tileY;
                var slot = AcquireRingSlot();
                UploadParameters(slot, parameters);
                var commandBuffer = _ringCommands[slot];
                TransitionOutputToGeneral(commandBuffer);
                if (pathMode)
                {
                    TransitionAccumulationToGeneral(commandBuffer);
                }

                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _computePipeline);
                var descriptorSet = _ringSets[slot];
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
                    0, 1, in descriptorSet, 0, null);
                api.CmdDispatch(commandBuffer, ((uint)tileWidth + 7U) / 8U,
                    ((uint)tileHeight + 7U) / 8U, 1);
                TransitionOutputToShaderRead(commandBuffer);
                CommitRingSlot(slot);
            }
        }

        // 降噪必须在整帧累积完成后执行。独立的第二阶段读取稳定的累积图像，
        // 避免在采样阶段读写邻居造成竞态。
        if (pathMode && settings.PathDenoising && settings.PathDenoiseStrength > 0F)
        {
            parameters[VoxelRenderParameters.TileOffsetX] = 0F;
            parameters[VoxelRenderParameters.TileOffsetY] = 0F;
            parameters[VoxelRenderParameters.DenoisePass] = 1F;
            var slot = AcquireRingSlot();
            UploadParameters(slot, parameters);
            var commandBuffer = _ringCommands[slot];
            TransitionOutputToGeneral(commandBuffer);
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _computePipeline);
            var descriptorSet = _ringSets[slot];
            api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
                0, 1, in descriptorSet, 0, null);
            api.CmdDispatch(commandBuffer, ((uint)widthPixels + 7U) / 8U,
                ((uint)heightPixels + 7U) / 8U, 1);
            TransitionOutputToShaderRead(commandBuffer);
            CommitRingSlot(slot);
        }
        parameters[VoxelRenderParameters.TileOffsetX] = 0F;
        parameters[VoxelRenderParameters.TileOffsetY] = 0F;
        parameters[VoxelRenderParameters.DenoisePass] = 0F;
        if (pathMode)
        {
            AccumulatedSamples = Math.Min(settings.PathMaxAccumulatedSamples,
                AccumulatedSamples + Math.Max(1, settings.PathSamplesPerFrame));
            _accumulationInitialized = true;
            _clearAccumulationPending = false;
        }
        _lastParameters = parameters;
        return true;
    }

    /// <summary>
    /// 清零渐进路径追踪的 GPU 累积图像，作为环形队列中的第一条命令保证顺序
    /// </summary>
    private void ClearAccumulation()
    {
        if (_context is null || _api is not { } api || _accumulationImage.Handle == 0)
        {
            return;
        }

        var slot = AcquireRingSlot();
        var commandBuffer = _ringCommands[slot];
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _accumulationInitialized ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _accumulationImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = _accumulationInitialized ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        api.CmdPipelineBarrier(commandBuffer,
            _accumulationInitialized ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
        ClearColorValue clear = new(0F, 0F, 0F, 0F);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        api.CmdClearColorImage(commandBuffer, _accumulationImage, ImageLayout.General, in clear, 1, in range);
        CommitRingSlot(slot);
        _accumulationInitialized = true;
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
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
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
            ImageCreateInfo accumulationCreateInfo = createInfo;
            accumulationCreateInfo.Format = Format.R16G16B16A16Sfloat;
            accumulationCreateInfo.Usage = ImageUsageFlags.StorageBit;
            Check(api.CreateImage(_device, &accumulationCreateInfo, null, out _accumulationImage),
                "创建路径追踪累积图像");
            MemoryRequirements accumulationRequirements = default;
            api.GetImageMemoryRequirements(_device, _accumulationImage, &accumulationRequirements);
            _accumulationMemory = AllocateMemory(accumulationRequirements, MemoryPropertyFlags.DeviceLocalBit);
            Check(api.BindImageMemory(_device, _accumulationImage, _accumulationMemory, 0),
                "绑定路径追踪累积图像");
            _accumulationView = CreateImageView(_accumulationImage, Format.R16G16B16A16Sfloat);
            _displayTextureId = _imguiRenderer!.RegisterExternalTexture(_outputView);
            _outputWidth = width;
            _outputHeight = height;
            _outputIsShaderReadOnly = false;
            _accumulationInitialized = false;
            _clearAccumulationPending = true;
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
    /// 把路径追踪累积图像转换为 Compute 可读写布局
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionAccumulationToGeneral(CommandBuffer commandBuffer)
    {
        if (_accumulationInitialized)
        {
            return;
        }

        var api = _api!;
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _accumulationImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, in barrier);
        _accumulationInitialized = true;
    }

    /// <summary>
    /// 更新 Compute 参数 Storage Buffer
    /// </summary>
    /// <param name="parameters">渲染参数块</param>
    private void UploadParameters(int slot, float[] parameters)
    {
        var api = _api!;
        void* mapped = null;
        var byteCount = checked((ulong)(parameters.Length * sizeof(float)));
        Check(api.MapMemory(_device, _ringParameterMemory[slot], 0, byteCount, 0, &mapped), "映射体素 Compute 参数缓冲");
        try
        {
            fixed (float* source = parameters)
            {
                System.Buffer.MemoryCopy(source, mapped, (long)byteCount, (long)byteCount);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _ringParameterMemory[slot]);
        }
    }

    /// <summary>
    /// 领取一个环形槽位并让其命令缓冲进入录制状态，槽位仍在使用时先等它的栅栏
    /// </summary>
    private int AcquireRingSlot()
    {
        var slot = _ringNext;
        _ringNext = (_ringNext + 1) % TileRingDepth;
        if (_ringInFlight[slot])
        {
            _context!.WaitAndResetFence(_ringFences[slot]);
        }

        _ringInFlight[slot] = true;
        var api = _api!;
        api.ResetCommandBuffer(_ringCommands[slot], 0);
        CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo };
        Check(api.BeginCommandBuffer(_ringCommands[slot], in begin), "录制体素分块计算命令");
        return slot;
    }

    /// <summary>
    /// 结束槽位录制并异步提交，不等待 GPU 完成
    /// </summary>
    private void CommitRingSlot(int slot)
    {
        var commandBuffer = _ringCommands[slot];
        Check(_api!.EndCommandBuffer(commandBuffer), "结束体素分块计算命令");
        _context!.SubmitAsync(commandBuffer, _ringFences[slot]);
    }

    /// <summary>
    /// 创建 Compute 描述符布局
    /// </summary>
    /// <returns>Compute 描述符布局</returns>
    private DescriptorSetLayout CreateComputeDescriptorLayout()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];
        bindings[0] = new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[2] = new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[3] = new(3, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[4] = new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[5] = new(5, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        DescriptorSetLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 6,
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
            new(DescriptorType.StorageBuffer, 4 * TileRingDepth),
            new(DescriptorType.StorageImage, 2 * TileRingDepth)
        };
        DescriptorPoolCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = TileRingDepth,
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
    private void AllocateComputeDescriptorSets()
    {
        var api = _api!;
        var layouts = stackalloc DescriptorSetLayout[TileRingDepth];
        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            layouts[slot] = _computeDescriptorLayout;
        }

        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _computeDescriptorPool,
            DescriptorSetCount = TileRingDepth,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _ringSets)
        {
            Check(api.AllocateDescriptorSets(_device, &allocateInfo, sets), "分配体素 Compute 描述符集环");
        }
    }

    /// <summary>
    /// 创建环形队列使用的命令池、命令缓冲与栅栏
    /// </summary>
    private void CreateRingCommands()
    {
        var api = _api!;
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _context!.QueueFamilyIndex,
        };
        Check(api.CreateCommandPool(_device, &poolInfo, null, out _ringCommandPool), "创建分块命令池");
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _ringCommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = TileRingDepth,
        };
        fixed (CommandBuffer* buffers = _ringCommands)
        {
            Check(api.AllocateCommandBuffers(_device, &allocateInfo, buffers), "分配分块命令缓冲");
        }

        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
            Check(api.CreateFence(_device, &fenceInfo, null, out _ringFences[slot]), "创建分块栅栏");
        }
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
        if (_voxelBuffer.Handle == 0 || _bvhBuffer.Handle == 0 || _outputView.Handle == 0 ||
            _accumulationView.Handle == 0) return;
        DescriptorBufferInfo voxelInfo = new(_voxelBuffer, 0, _voxelBufferCapacity);
        DescriptorBufferInfo bvhInfo = new(_bvhBuffer, 0, _bvhBufferCapacity);
        DescriptorImageInfo imageInfo = new(default, _outputView, ImageLayout.General);
        DescriptorImageInfo accumulationInfo = new(default, _accumulationView, ImageLayout.General);
        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            if (_ringSets[slot].Handle == 0) return;
            DescriptorBufferInfo parameterInfo = new(_ringParameterBuffers[slot], 0,
                (ulong)(VoxelRenderParameters.SlotCount * sizeof(float)));
            DescriptorBufferInfo materialTableInfo = new(_materialTableBuffer, 0,
                (ulong)(MVoxel.Project.MaterialAsset.TableSlots * MVoxel.Project.MaterialAsset.FloatsPerSlot * sizeof(float)));
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];
            writes[0] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &voxelInfo
            };
            writes[1] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bvhInfo
            };
            writes[2] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imageInfo
            };
            writes[3] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 3,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &parameterInfo
            };
            writes[4] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 4,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &accumulationInfo
            };
            writes[5] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 5,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &materialTableInfo
            };
            _api!.UpdateDescriptorSets(_device, 6, writes, 0, null);
        }
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
        if (_accumulationView.Handle != 0) api.DestroyImageView(_device, _accumulationView, null);
        if (_accumulationImage.Handle != 0) api.DestroyImage(_device, _accumulationImage, null);
        if (_accumulationMemory.Handle != 0) api.FreeMemory(_device, _accumulationMemory, null);
        if (_outputView.Handle != 0) api.DestroyImageView(_device, _outputView, null);
        if (_outputImage.Handle != 0) api.DestroyImage(_device, _outputImage, null);
        if (_outputMemory.Handle != 0) api.FreeMemory(_device, _outputMemory, null);
        _outputView = default;
        _outputImage = default;
        _outputMemory = default;
        _accumulationView = default;
        _accumulationImage = default;
        _accumulationMemory = default;
        _outputWidth = 0;
        _outputHeight = 0;
        _outputIsShaderReadOnly = false;
        _accumulationInitialized = false;
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

        struct BvhNode
        {
            ivec4 minimum;
            ivec4 maximum;
            ivec4 links;
        };

        layout(set = 0, binding = 0, std430) readonly buffer VoxelBuffer
        {
            VoxelCell cells[];
        };

        layout(set = 0, binding = 1, std430) readonly buffer BvhBuffer
        {
            BvhNode nodes[];
        };

        layout(set = 0, binding = 2, rgba8) uniform writeonly image2D outputImage;

        layout(set = 0, binding = 3, std430) readonly buffer ParameterBuffer
        {
            float values[];
        };

        layout(set = 0, binding = 4, rgba16f) uniform coherent image2D accumulationImage;

        struct MaterialSlotData
        {
            vec4 scalars;
            vec4 absorption;
        };

        layout(set = 0, binding = 5, std430) readonly buffer MaterialTable
        {
            MaterialSlotData materialSlots[];
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
                normal = vec3(hitPoint.x < (boxMin.x + boxMax.x) * 0.5 ? -1.0 : 1.0, 0.0, 0.0);
            else if (distances.y <= distances.z)
                normal = vec3(0.0, hitPoint.y < (boxMin.y + boxMax.y) * 0.5 ? -1.0 : 1.0, 0.0);
            else
                normal = vec3(0.0, 0.0, hitPoint.z < (boxMin.z + boxMax.z) * 0.5 ? -1.0 : 1.0);
            return true;
        }

        bool IntersectBounds(vec3 rayOrigin, vec3 rayDirection, BvhNode node, out float distance)
        {
            vec3 normal;
            return IntersectBox(rayOrigin, rayDirection,
                vec3(node.minimum.xyz) + vec3(values[16], values[17], values[18]),
                vec3(node.maximum.xyz) + vec3(values[16], values[17], values[18]), distance, normal);
        }

        bool TraceClosest(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot, bool ignoreTranslucent)
        {
            int stack[64];
            int stackSize = 1;
            stack[0] = 0;
            closestDistance = 3.402823e+38;
            hitNormal = vec3(0.0, 1.0, 0.0);
            hitColor = vec4(0.0);
            hitFlags = 0u;
            hitSlot = 0u;
            int voxelCount = int(values[94]);
            int nodeCount = int(values[95]);
            vec3 regionOrigin = vec3(values[16], values[17], values[18]);
            while (stackSize > 0)
            {
                int nodeIndex = stack[--stackSize];
                if (nodeIndex < 0 || nodeIndex >= nodeCount) continue;
                BvhNode node = nodes[nodeIndex];
                float boundsDistance;
                if (!IntersectBounds(rayOrigin, rayDirection, node, boundsDistance) || boundsDistance > closestDistance)
                    continue;
                if (node.links.x < 0)
                {
                    int endIndex = min(node.links.z + node.links.w, voxelCount);
                    for (int index = node.links.z; index < endIndex; index++)
                    {
                        VoxelCell cell = cells[index];
                        vec3 voxelMin = regionOrigin + vec3(cell.position.xyz);
                        vec3 voxelMax = voxelMin + vec3(1.0);
                        float distance;
                        vec3 normal;
                        if (IntersectBox(rayOrigin, rayDirection, voxelMin, voxelMax, distance, normal) &&
                            distance < closestDistance &&
                            !(ignoreTranslucent && (cell.metadata.x >> 16 & 1u) != 0u))
                        {
                            closestDistance = distance;
                            hitNormal = normal;
                            hitColor = cell.color;
                            hitFlags = cell.metadata.x >> 16;
                            hitSlot = cell.metadata.x & 0xFFFFu;
                        }
                    }
                }
                else
                {
                    if (stackSize + 2 <= 64)
                    {
                        stack[stackSize++] = node.links.x;
                        stack[stackSize++] = node.links.y;
                    }
                }
            }
            return hitColor.a > 0.0;
        }

        bool TraceShadow(vec3 rayOrigin, vec3 rayDirection, float maxDistance)
        {
            float distance;
            vec3 normal;
            vec4 color;
            uint flags;
            uint slot;
            return TraceClosest(rayOrigin, rayDirection, distance, normal, color, flags, slot, true) && distance < maxDistance;
        }

        float Random(inout uint state);

        bool TraceSoftShadow(vec3 rayOrigin, vec3 rayDirection, float maxDistance, inout uint state)
        {
            int sampleCount = clamp(int(values[32]), 1, 8);
            if (values[31] <= 0.0001 || sampleCount == 1)
                return TraceShadow(rayOrigin, rayDirection, maxDistance);
            vec3 tangent = normalize(abs(rayDirection.y) < 0.9 ? cross(rayDirection, vec3(0.0, 1.0, 0.0))
                : cross(rayDirection, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(rayDirection, tangent);
            for (int index = 0; index < sampleCount; index++)
            {
                float angle = 6.28318530718 * (float(index) + Random(state)) / float(sampleCount);
                float radius = values[31] * sqrt(Random(state));
                vec3 sampleDirection = normalize(rayDirection + tangent * cos(angle) * radius
                    + bitangent * sin(angle) * radius);
                if (TraceShadow(rayOrigin, sampleDirection, maxDistance)) return true;
            }
            return false;
        }

        float AmbientOcclusion(vec3 hitPoint, vec3 normal, inout uint state)
        {
            if (values[59] <= 0.5) return 1.0;
            vec3 tangent = normalize(abs(normal.y) < 0.9 ? cross(normal, vec3(0.0, 1.0, 0.0))
                : cross(normal, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(normal, tangent);
            float blocked = 0.0;
            const int sampleCount = 4;
            for (int index = 0; index < sampleCount; index++)
            {
                float angle = 6.28318530718 * (float(index) + Random(state)) / float(sampleCount);
                float radius = mix(0.25, 2.0, Random(state));
                vec3 direction = normalize(normal * 0.65 + tangent * cos(angle) * radius
                    + bitangent * sin(angle) * radius);
                if (TraceShadow(hitPoint + normal * 0.003, direction, radius)) blocked += 1.0;
            }
            float visibility = 1.0 - blocked / float(sampleCount);
            return pow(clamp(visibility, 0.0, 1.0), max(values[61], 0.05))
                * values[60] + (1.0 - values[60]);
        }

        uint Hash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            return value ^ (value >> 16);
        }

        float Random(inout uint state)
        {
            state = Hash(state + 0x9e3779b9u);
            return float(state & 0x00ffffffu) / 16777216.0;
        }

        vec3 CosineHemisphere(vec3 normal, inout uint state)
        {
            float radius = sqrt(Random(state));
            float angle = 6.28318530718 * Random(state);
            vec3 tangent = normalize(abs(normal.y) < 0.9 ? cross(normal, vec3(0.0, 1.0, 0.0))
                : cross(normal, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(normal, tangent);
            return normalize(tangent * (cos(angle) * radius) + bitangent * (sin(angle) * radius)
                + normal * sqrt(max(0.0, 1.0 - radius * radius)));
        }

        vec3 Sky(vec3 rayDirection)
        {
            vec3 skyTop = vec3(values[40], values[41], values[42]);
            vec3 skyHorizon = vec3(values[44], values[45], values[46]);
            vec3 sky = mix(skyHorizon, skyTop, clamp(rayDirection.y * 0.5 + 0.5, 0.0, 1.0));
            return sky * max(values[43], 0.0) + vec3(values[47] * 0.08, values[47] * 0.06, values[47] * 0.03);
        }

        vec3 LinearToDisplay(vec3 value)
        {
            vec3 low = value * 12.92;
            vec3 high = 1.055 * pow(max(value, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
            return mix(low, high, step(vec3(0.0031308), value));
        }

        vec3 Ground(vec3 rayOrigin, vec3 rayDirection, float hitDistance, bool pathMode)
        {
            vec3 point = rayOrigin + rayDirection * hitDistance;
            vec3 groundColor = pathMode
                ? vec3(values[36], values[37], values[38])
                : LinearToDisplay(vec3(values[98], values[99], values[100]));
            if (values[90] > 0.5)
            {
                float gridStep = max(values[89], 0.001);
                float radialDistance = length(point.xz - rayOrigin.xz);
                float level = floor(log2(max(1.0, radialDistance / (gridStep * 32.0))));
                float localStep = gridStep * exp2(clamp(level, 0.0, 12.0));
                vec2 gridPosition = point.xz / localStep;
                vec2 fractional = fract(gridPosition);
                vec2 distanceToLine = min(fractional, 1.0 - fractional) * localStep;
                float lineAmount = 1.0 - smoothstep(0.0, 0.045, min(distanceToLine.x, distanceToLine.y));
                float pixelSpacing = localStep * values[3] / max(length(point - rayOrigin), 1.0);
                lineAmount *= smoothstep(0.85, 1.75, pixelSpacing);
                lineAmount *= 1.0 - smoothstep(0.0, 12.0, level) * 0.18;
                groundColor = mix(groundColor, groundColor * 0.52 + vec3(0.16, 0.20, 0.28), lineAmount);
            }
            return groundColor;
        }

        vec3 TracePath(vec3 rayOrigin, vec3 rayDirection, inout uint state, bool pathMode,
            out bool primarySurfaceHit)
        {
            primarySurfaceHit = false;
            vec3 radiance = vec3(0.0);
            vec3 throughput = vec3(1.0);
            int translucentPasses = 0;
            int bounceLimit = pathMode ? min(int(values[72]), 16) : 1;
            for (int bounce = 0; bounce < 16; bounce++)
            {
                if (bounce >= bounceLimit) break;
                float hitDistance;
                vec3 hitNormal;
                vec4 hitColor;
                uint hitFlags;
                uint hitSlot;
                if (!TraceClosest(rayOrigin, rayDirection, hitDistance, hitNormal, hitColor, hitFlags, hitSlot, false))
                {
                    float groundDistance = (values[35] > 0.5 || (!pathMode && values[90] > 0.5)) && abs(rayDirection.y) > 0.00001
                        ? -rayOrigin.y / rayDirection.y : 3.402823e+38;
                    if (groundDistance > 0.0 && groundDistance < values[97])
                    {
                        if (bounce == 0) primarySurfaceHit = true;
                        radiance += throughput * Ground(rayOrigin, rayDirection, groundDistance, pathMode);
                    }
                    else
                        radiance += pathMode
                            ? throughput * Sky(rayDirection)
                            : LinearToDisplay(vec3(values[98], values[99], values[100]));
                    break;
                }

                if (bounce == 0 && (hitDistance < values[96] || hitDistance > values[97]))
                {
                    if (!pathMode)
                    {
                        radiance += LinearToDisplay(vec3(values[98], values[99], values[100]));
                    }
                    else
                    {
                        radiance += throughput * Sky(rayDirection);
                    }
                    break;
                }

                vec3 hitPoint = rayOrigin + rayDirection * hitDistance;
                if (bounce == 0) primarySurfaceHit = true;
                if (!pathMode)
                {
                    vec3 localPoint = clamp(fract(hitPoint), vec3(0.0), vec3(1.0));
                    float edgeDistance;
                    if (abs(hitNormal.x) > 0.5)
                        edgeDistance = min(min(localPoint.y, 1.0 - localPoint.y),
                            min(localPoint.z, 1.0 - localPoint.z));
                    else if (abs(hitNormal.y) > 0.5)
                        edgeDistance = min(min(localPoint.x, 1.0 - localPoint.x),
                            min(localPoint.z, 1.0 - localPoint.z));
                    else
                        edgeDistance = min(min(localPoint.x, 1.0 - localPoint.x),
                            min(localPoint.y, 1.0 - localPoint.y));
                    float outlineWidth = max(0.012, hitDistance / max(values[3], 1.0) * 1.35);
                    float outlineAmount = values[57] > 0.5
                        ? 1.0 - smoothstep(outlineWidth * 0.25, outlineWidth, edgeDistance)
                        : 0.0;
                    radiance += mix(hitColor.rgb, vec3(0.012, 0.014, 0.018), outlineAmount);
                    break;
                }

                MaterialSlotData material = materialSlots[int(hitSlot) % 64];
                float emissiveScale = material.scalars.z >= 0.0 ? material.scalars.z : 1.0;
                if ((hitFlags & 2u) != 0u)
                    radiance += throughput * hitColor.rgb * values[80] * emissiveScale;

                throughput *= AmbientOcclusion(hitPoint, hitNormal, state);

                if ((hitFlags & 1u) != 0u)
                {
                    // 半透明表面先获得与不透明一致的天空与太阳直接光贡献，
                    // 保证深水区域本身可见，不会因穿透衰减归零成黑色
                    vec3 surfaceLight = normalize(vec3(values[24], values[25], values[26]));
                    float surfaceAmount = values[33] > 0.5 ? max(dot(hitNormal, -surfaceLight), 0.0) : 0.0;
                    if (surfaceAmount > 0.0 &&
                        !TraceSoftShadow(hitPoint + hitNormal * 0.002, -surfaceLight, 10000.0, state))
                        radiance += throughput * hitColor.rgb * surfaceAmount * values[27];
                    radiance += throughput * hitColor.rgb * Sky(hitNormal) * 0.25;

                    translucentPasses++;
                    if (translucentPasses >= 4)
                    {
                        // 深水内部按普通漫射表面收尾，避免无限穿透
                        rayDirection = CosineHemisphere(hitNormal, state);
                        rayOrigin = hitPoint + hitNormal * 0.002;
                        throughput *= hitColor.rgb;
                        continue;
                    }

                    // 半透明按 Beer 吸收逐体素衰减：消光系数 = 吸收强度 × 0.25 步长标定 × 步长倍率 × (1 - 吸收色)。
                    // 吸收色默认取体素基础色，材质槽给出非零吸收色时覆盖。
                    // 推进必须越过入射体素的出口：单位体素沿主轴的出口距离为 1/max|dir|，
                    // 否则同一水体素被反复命中，吸收与 AO 逐弹射重复累积成黑色。
                    vec3 absorbColor = any(greaterThan(material.absorption.rgb, vec3(0.0)))
                        ? material.absorption.rgb
                        : hitColor.rgb;
                    float stepScale = material.scalars.w >= 0.0 ? material.scalars.w : 1.0;
                    vec3 sigma = max(values[81], 0.0) * 0.25 * stepScale *
                        (vec3(1.0) - clamp(absorbColor, vec3(0.0), vec3(1.0)));
                    throughput *= exp(-sigma);
                    float exitStep = 1.0 / max(max(abs(rayDirection.x), abs(rayDirection.y)), abs(rayDirection.z));
                    rayOrigin = hitPoint + rayDirection * (exitStep + 0.001);
                    continue;
                }

                vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
                vec3 lightDirection = normalize(-sunDirection);
                float lightAmount = values[33] > 0.5 ? max(dot(hitNormal, lightDirection), 0.0) : 0.0;
                if (values[85] > 0.5 && lightAmount > 0.0 &&
                    !TraceSoftShadow(hitPoint + hitNormal * 0.002, lightDirection, 10000.0, state))
                    radiance += throughput * hitColor.rgb * lightAmount * values[27];

                float metalness = clamp(material.scalars.y >= 0.0 ? material.scalars.y : values[74], 0.0, 1.0);
                float roughness = clamp(material.scalars.x >= 0.0 ? material.scalars.x : values[73], 0.01, 1.0);
                // GGX 微表面弹射：Heitz 重要采样微表面法线，Schlick 菲涅尔平衡分配
                // 镜面与漫射分支；粗糙度控制高光波瓣宽度，金属度把基础色变为反射色
                float alpha = max(roughness * roughness, 0.002);
                vec3 f0 = mix(vec3(0.04), hitColor.rgb, metalness);
                vec3 viewDir = -rayDirection;
                float xi1 = max(Random(state), 1e-4);
                float xi2 = Random(state);
                float cosThetaH = sqrt((1.0 - xi1) / (1.0 + (alpha * alpha - 1.0) * xi1));
                float sinThetaH = sqrt(max(0.0, 1.0 - cosThetaH * cosThetaH));
                float azimuth = 6.28318530718 * xi2;
                vec3 tangentBasisX = normalize(cross(hitNormal,
                    abs(hitNormal.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
                vec3 tangentBasisY = cross(hitNormal, tangentBasisX);
                vec3 microfacetNormal = normalize(tangentBasisX * sinThetaH * cos(azimuth)
                    + tangentBasisY * sinThetaH * sin(azimuth) + hitNormal * cosThetaH);
                float vDotH = max(dot(viewDir, microfacetNormal), 0.0);
                float f0Max = max(max(f0.r, f0.g), f0.b);
                float fresnelStrength = clamp(f0Max + (1.0 - f0Max) * pow(1.0 - vDotH, 5.0), 0.0, 1.0);
                vec3 specularCandidate = normalize(reflect(rayDirection, microfacetNormal));
                if (dot(specularCandidate, hitNormal) > 0.0 && Random(state) < fresnelStrength)
                {
                    rayDirection = specularCandidate;
                    throughput *= f0 * 2.0;
                }
                else
                {
                    rayDirection = CosineHemisphere(hitNormal, state);
                    throughput *= hitColor.rgb * (1.0 - fresnelStrength) * 2.0;
                }

                rayOrigin = hitPoint + hitNormal * 0.002;
                if (bounce >= 2)
                {
                    float survival = clamp(max(max(throughput.r, throughput.g), throughput.b),
                        max(values[76], 0.001), 0.98);
                    if (Random(state) > survival) break;
                    throughput /= survival;
                }
            }
            return min(radiance, vec3(max(values[77], 0.001)));
        }

        vec3 PostProcess(vec3 color, ivec2 pixel, int width, int height);

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy) +
                ivec2(int(values[101]), int(values[102]));
            int width = int(values[11]);
            int height = int(values[15]);
            if (pixel.x >= width || pixel.y >= height) return;

            if (values[103] > 0.5 && values[92] > 0.5)
            {
                float strength = clamp(values[93], 0.0, 1.0);
                vec3 center = imageLoad(accumulationImage, pixel).rgb;
                vec3 filtered = vec3(0.0);
                float weightSum = 0.0;
                float centerLuma = dot(center, vec3(0.2126, 0.7152, 0.0722));
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        ivec2 samplePixel = clamp(pixel + ivec2(ox, oy), ivec2(0), ivec2(width - 1, height - 1));
                        vec3 neighbor = imageLoad(accumulationImage, samplePixel).rgb;
                        float spatial = (ox == 0 && oy == 0) ? 4.0 : ((ox == 0 || oy == 0) ? 2.0 : 1.0);
                        float neighborLuma = dot(neighbor, vec3(0.2126, 0.7152, 0.0722));
                        float edge = exp(-abs(neighborLuma - centerLuma) * 8.0);
                        float weight = spatial * mix(0.0, edge, strength);
                        filtered += neighbor * weight;
                        weightSum += weight;
                    }
                }
                vec3 color = weightSum > 0.0 ? filtered / weightSum : center;
                imageStore(outputImage, pixel, vec4(PostProcess(clamp(mix(center, color, strength), 0.0, 1.0), pixel, width, height), 1.0));
                return;
            }

            bool pathMode = values[86] > 0.5;
            int samples = pathMode ? max(int(values[78]), 1) : 1;
            int accumulatedSamples = max(int(values[79]), 0);
            uint state = Hash(uint(pixel.x) * 1973u + uint(pixel.y) * 9277u +
                uint(values[7]) * 26699u + uint(values[88]));
            vec3 origin = vec3(values[0], values[1], values[2]);
            float focalPixels = max(values[3], 1.0);
            vec3 forward = vec3(values[4], values[5], values[6]);
            vec3 right = vec3(values[8], values[9], values[10]);
            vec3 up = vec3(values[12], values[13], values[14]);
            vec3 sampleColor = vec3(0.0);
            bool primarySurfaceHit = false;
            for (int sampleIndex = 0; sampleIndex < 64; sampleIndex++)
            {
                if (sampleIndex >= samples) break;
                vec2 jitter = pathMode ? vec2(Random(state), Random(state)) - 0.5 : vec2(0.0);
                vec2 screenPosition = ((vec2(pixel) + 0.5 + jitter) / vec2(width, height)) * 2.0 - 1.0;
                vec3 sampleOrigin = origin;
                if (pathMode && values[87] > 0.0)
                    focalPixels = float(height) / (2.0 * tan(radians(clamp(values[87], 20.0, 120.0)) * 0.5));
                vec3 direction = normalize(forward + right * screenPosition.x * float(width) * 0.5 / focalPixels
                    + up * -screenPosition.y * float(height) * 0.5 / focalPixels);
                if (pathMode && values[84] > 0.5 && values[82] > 0.0)
                {
                    float radius = sqrt(Random(state)) * values[82];
                    float angle = 6.28318530718 * Random(state);
                    vec2 lens = vec2(cos(angle), sin(angle)) * radius;
                    vec3 focusPoint = sampleOrigin + direction * max(values[83], 0.01);
                    vec3 lensOrigin = sampleOrigin + right * lens.x + up * lens.y;
                    sampleOrigin = lensOrigin;
                    direction = normalize(focusPoint - lensOrigin);
                }
                bool sampleSurfaceHit = false;
                sampleColor += TracePath(sampleOrigin, direction, state, pathMode, sampleSurfaceHit);
                primarySurfaceHit = primarySurfaceHit || sampleSurfaceHit;
            }
            sampleColor /= float(samples);

            vec3 color = sampleColor;
            if (pathMode)
            {
                vec4 previous = imageLoad(accumulationImage, pixel);
                float totalSamples = float(accumulatedSamples + samples);
                color = totalSamples > 0.0
                    ? (previous.rgb * float(accumulatedSamples) + sampleColor * float(samples)) / totalSamples
                    : sampleColor;
                imageStore(accumulationImage, pixel, vec4(color, 1.0));
            }
            float outputAlpha = pathMode || primarySurfaceHit ? 1.0 : 0.0;
            imageStore(outputImage, pixel, vec4(PostProcess(clamp(color, 0.0, 1.0), pixel, width, height), outputAlpha));
        }

        vec3 PostProcess(vec3 color, ivec2 pixel, int width, int height)
        {
            color *= exp2(values[65]);
            color *= vec3(values[68], values[69], values[70]);
            if (values[56] > 0.5)
            {
                float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
                color = mix(vec3(luminance), color, max(values[52], 0.0));
                color = (color - 0.5) * max(values[54], 0.0) + 0.5;
                color *= max(values[55], 0.0);
                float angle = radians(values[53]);
                float cs = cos(angle);
                float sn = sin(angle);
                mat3 hue = mat3(
                    0.213 + cs * 0.787 - sn * 0.213, 0.715 - cs * 0.715 - sn * 0.715, 0.072 - cs * 0.072 + sn * 0.928,
                    0.213 - cs * 0.213 + sn * 0.143, 0.715 + cs * 0.285 + sn * 0.140, 0.072 - cs * 0.072 - sn * 0.283,
                    0.213 - cs * 0.213 - sn * 0.787, 0.715 - cs * 0.715 + sn * 0.715, 0.072 + cs * 0.928 + sn * 0.072);
                color = hue * color;
            }
            if (values[67] > 0.0)
            {
                vec2 uv = (vec2(pixel) + 0.5) / vec2(width, height) * 2.0 - 1.0;
                color *= 1.0 - clamp(dot(uv, uv) * values[67], 0.0, 0.85);
            }
            color = max(color, vec3(0.0));
            if (values[64] > 0.5)
            {
                if (int(values[64]) == 1)
                    color = color / (vec3(1.0) + color);
                else if (int(values[64]) == 2)
                    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
                else
                    color = clamp((color * (color * 0.6 + 0.2)) / (color * (color * 0.6 + 0.5) + 0.06), 0.0, 1.0);
            }
            float gamma = max(values[66], 0.01);
            color = pow(color, vec3(1.0 / gamma));
            if (values[71] > 0.5)
            {
                float noise = fract(sin(dot(vec2(pixel), vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
                color += noise / 255.0;
            }
            return clamp(color, 0.0, 1.0);
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
        return PackCells(workset.Cells);
    }

    /// <summary>
    /// 按指定顺序编码体素 GPU 数据
    /// </summary>
    /// <param name="cells">待编码体素</param>
    /// <returns>固定布局的体素字节流</returns>
    private static byte[] PackCells(IReadOnlyList<VoxelGpuCell> cells)
    {
        var bytes = new byte[checked(cells.Count * PackedCellSize)];
        if (cells.Count < 100_000)
        {
            PackCellRange(cells, bytes, 0, cells.Count);
            return bytes;
        }

        var rangeSize = Math.Max(655_360, cells.Count / 64);
        System.Threading.Tasks.Parallel.For(0, (cells.Count + rangeSize - 1) / rangeSize,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
            },
            rangeIndex =>
            {
                var from = rangeIndex * rangeSize;
                PackCellRange(cells, bytes, from, Math.Min(from + rangeSize, cells.Count));
            });
        return bytes;
    }

    /// <summary>
    /// 打包指定区间的体素记录
    /// </summary>
    private static void PackCellRange(IReadOnlyList<VoxelGpuCell> cells, byte[] bytes, int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            var cell = cells[index];
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
            var metadata = (uint)materialSlot | ((uint)cell.Value.Flags << 16);
            MemoryMarshal.Write(bytes.AsSpan(offset + 32, 4), in metadata);
        }
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

        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var bvh = VoxelGpuBvh.Build(workset);
        SyncBvhBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();
        var bytes = PackCells(bvh.OrderedCells);
        SyncPackMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();
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

        var bvhRequired = checked((ulong)Math.Max(bvh.NodeBytes.Length, VoxelGpuBvh.PackedNodeSize));
        if (_bvhBuffer.Handle == 0 || _bvhBufferCapacity < bvhRequired)
        {
            api.DeviceWaitIdle(_device);
            DestroyBvhBuffer(api);
            (_bvhBuffer, _bvhMemory) = CreateBuffer(
                NextCapacity(bvhRequired),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
            _bvhBufferCapacity = NextCapacity(bvhRequired);
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

            _context.ExecuteImmediate(commandBuffer => RecordUpload(commandBuffer, stagingBuffer, _voxelBuffer, bytes.Length));
        }
        finally
        {
            api.DestroyBuffer(_device, stagingBuffer, null);
            api.FreeMemory(_device, stagingMemory, null);
        }

        var bvhStagingSize = Math.Max(bvh.NodeBytes.Length, VoxelGpuBvh.PackedNodeSize);
        var (bvhStagingBuffer, bvhStagingMemory) = CreateBuffer(
            (ulong)bvhStagingSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        try
        {
            void* mapped = null;
            Check(api.MapMemory(_device, bvhStagingMemory, 0, (ulong)bvh.NodeBytes.Length, 0, &mapped),
                "映射体素 BVH 暂存缓冲");
            try
            {
                fixed (byte* source = bvh.NodeBytes)
                {
                    System.Buffer.MemoryCopy(source, mapped, bvh.NodeBytes.Length, bvh.NodeBytes.Length);
                }
            }
            finally
            {
                api.UnmapMemory(_device, bvhStagingMemory);
            }

            _context.ExecuteImmediate(commandBuffer => RecordUpload(commandBuffer, bvhStagingBuffer,
                _bvhBuffer, bvh.NodeBytes.Length));
        }
        finally
        {
            api.DestroyBuffer(_device, bvhStagingBuffer, null);
            api.FreeMemory(_device, bvhStagingMemory, null);
        }

        _bvhNodeCount = bvh.NodeCount;
        UpdateComputeDescriptors();
        SyncUploadMs = phaseWatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 记录暂存缓冲到体素 Storage Buffer 的复制和同步
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    /// <param name="stagingBuffer">主机可见暂存缓冲</param>
    /// <param name="byteCount">复制字节数</param>
    private void RecordUpload(CommandBuffer commandBuffer, VkBuffer stagingBuffer, VkBuffer destinationBuffer,
        int byteCount)
    {
        var api = _api!;
        var copy = new BufferCopy(0, 0, (ulong)byteCount);
        api.CmdCopyBuffer(commandBuffer, stagingBuffer, destinationBuffer, 1, in copy);
        BufferMemoryBarrier barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = destinationBuffer,
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
    /// 释放当前体素 BVH Storage Buffer
    /// </summary>
    /// <param name="api">Vulkan API</param>
    private void DestroyBvhBuffer(Vk api)
    {
        if (_bvhBuffer.Handle == 0)
        {
            return;
        }

        api.DestroyBuffer(_device, _bvhBuffer, null);
        api.FreeMemory(_device, _bvhMemory, null);
        _bvhBuffer = default;
        _bvhMemory = default;
        _bvhBufferCapacity = 0;
        _bvhNodeCount = 0;
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
