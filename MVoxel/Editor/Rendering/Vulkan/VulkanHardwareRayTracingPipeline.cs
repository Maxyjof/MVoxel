using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Voxels;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan 硬件光追管线
/// </summary>
/// <remarks>
/// 管线包含体素三角形 BLAS、单实例 TLAS、独立 RT Pipeline 和对齐后的 SBT
/// 默认由上层关闭，初始化或运行失败时由调用方回退 Compute
/// </remarks>
internal sealed unsafe class VulkanHardwareRayTracingPipeline : IDisposable
{
    /// <summary>
    /// 硬件加速体素的绝对上限，实际可用数量还会按本机显存预算与图元上限收缩
    /// </summary>
    internal const int MaxHardwareRayTracingVoxels = 1_000_000;
    /// <summary>
    /// 每个体素在几何缓冲、BLAS 与构建 scratch 上的保守显存估算（字节）。
    /// 几何缓冲 288B（顶点 96 + 索引 144 + 体素快照 48），其余按约 1.7KB 留给 BLAS 与 scratch
    /// </summary>
    private const ulong EstimatedBytesPerVoxel = 2048;
    private const double SlowAccelerationBuildMs = 50;
    private const int VerticesPerVoxel = 8;
    private const int IndicesPerVoxel = 36;
    private const int PrimitivesPerVoxel = 12;
    private const int CellStride = VulkanVoxelRenderPipeline.PackedCellSize;
    private static readonly uint[] BoxTriangleIndices =
    [
        0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
        0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
        0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
    ];

    private readonly VulkanWindowContext _context;
    private readonly VulkanImGuiRenderer _imguiRenderer;
    private readonly Vk _api;
    private readonly Device _device;
    private readonly KhrAccelerationStructure _accelerationApi;
    private readonly KhrRayTracingPipeline _rayTracingApi;
    private readonly KhrBufferDeviceAddress _addressApi;
    private readonly bool _coreBufferDeviceAddress;
    private readonly PhysicalDeviceRayTracingPipelinePropertiesKHR _properties;
    private readonly PhysicalDeviceAccelerationStructurePropertiesKHR _accelerationProperties;

    /// <summary>
    /// 加速结构构建 scratch 地址要求的对齐字节数
    /// </summary>
    private readonly ulong _scratchAlignment;

    private VkBuffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private VkBuffer _indexBuffer;
    private DeviceMemory _indexMemory;
    private VkBuffer _cellBuffer;
    private DeviceMemory _cellMemory;
    private VkBuffer _instanceBuffer;
    private DeviceMemory _instanceMemory;
    private VkBuffer _blasBuffer;
    private DeviceMemory _blasMemory;
    private VkBuffer _tlasBuffer;
    private DeviceMemory _tlasMemory;
    private VkBuffer _scratchBuffer;
    private DeviceMemory _scratchMemory;
    private ulong _scratchAddress;
    private VkBuffer _parameterBuffer;
    private DeviceMemory _parameterMemory;
    private VkBuffer _sbtBuffer;
    private DeviceMemory _sbtMemory;

    private AccelerationStructureKHR _blas;
    private AccelerationStructureKHR _tlas;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private ShaderBindingTable _sbt;
    private Image _outputImage;
    private DeviceMemory _outputMemory;
    private ImageView _outputView;
    private nint _displayTextureId;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _outputIsShaderReadOnly;
    private int _sourceVersion = -1;
    private int _oversizedWarningVersion = -1;
    private bool _disposed;
    private string? _lastError;
    private double _lastBuildMs;
    private double _lastBuildIdleMs;
    private double _lastGeometryMs;
    private double _lastBlasMs;
    private double _lastTlasMs;
    private int _lastBuildCellCount;
    private readonly int _maxAccelerationVoxels;

    // 拖动对象实例：静态世界与拖动对象各建一份 BLAS，TLAS 固定两个实例。
    // 拖动时只改写实例 1 的平移并重建 TLAS，避免每步重建整份静态 BLAS
    private VkBuffer _dragVertexBuffer;
    private DeviceMemory _dragVertexMemory;
    private VkBuffer _dragIndexBuffer;
    private DeviceMemory _dragIndexMemory;
    private VkBuffer _dragCellBuffer;
    private DeviceMemory _dragCellMemory;
    private VkBuffer _dragBlasBuffer;
    private DeviceMemory _dragBlasMemory;
    private AccelerationStructureKHR _dragBlas;
    private int _dragCellCount;
    private long _dragOriginX, _dragOriginY, _dragOriginZ;
    private long _dragOffsetX, _dragOffsetY, _dragOffsetZ;
    private int _dragGeometryVersion = -1;
    private uint _instanceCount;
    private bool _dragInstanceVisible;
    private long _staticOriginX, _staticOriginY, _staticOriginZ;
    private ulong _tlasScratchSize;

    /// <summary>
    /// 创建硬件光追资源管理器
    /// </summary>
    /// <param name="context">Vulkan 窗口上下文</param>
    /// <param name="imguiRenderer">ImGui Vulkan 后端</param>
    internal VulkanHardwareRayTracingPipeline(VulkanWindowContext context, VulkanImGuiRenderer imguiRenderer)
    {
        _context = context;
        _imguiRenderer = imguiRenderer;
        _api = context.Api;
        _device = context.Device;
        _accelerationApi = new KhrAccelerationStructure(_api.Context);
        _rayTracingApi = new KhrRayTracingPipeline(_api.Context);
        _addressApi = new KhrBufferDeviceAddress(_api.Context);
        PhysicalDeviceProperties deviceProperties = default;
        _api.GetPhysicalDeviceProperties(context.PhysicalDevice, &deviceProperties);
        _coreBufferDeviceAddress = deviceProperties.ApiVersion >= Vk.Version12;
        _properties = QueryProperties(out _accelerationProperties, out _scratchAlignment);
        _maxAccelerationVoxels = ComputeMaxAccelerationVoxels(_accelerationProperties.MaxPrimitiveCount);
    }

    /// <summary>
    /// 按本机显存预算、加速结构图元上限与绝对上限计算可承载的体素数量
    /// </summary>
    /// <remarks>
    /// 原来的固定 250k 只是没测过更大场景时的保守值，并不是设备能力。
    /// 改成显存预算后，10GB 级显卡能覆盖更多体素，同时弱卡不会强行分配导致分配失败或驱动异常。
    /// 与厂商无关：同一份估算同时作用于 NVIDIA 与 AMD
    /// </remarks>
    private int ComputeMaxAccelerationVoxels(ulong maxPrimitiveCount)
    {
        PhysicalDeviceMemoryProperties memory = default;
        _api.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, &memory);
        ulong deviceLocalBytes = 0;
        for (var heap = 0; heap < memory.MemoryHeapCount; heap++)
        {
            var candidate = memory.MemoryHeaps[(int)heap];
            if ((candidate.Flags & MemoryHeapFlags.DeviceLocalBit) != 0 && candidate.Size > deviceLocalBytes)
                deviceLocalBytes = candidate.Size;
        }

        // 最多只占显存的八分之一，且给交换链、场景网格、材质与驱动留至少 256MB 余量
        var budget = Math.Max(deviceLocalBytes / 8, 256UL * 1024UL * 1024UL);
        var byMemory = budget / EstimatedBytesPerVoxel;
        var byPrimitives = maxPrimitiveCount / (ulong)PrimitivesPerVoxel;
        var limit = Math.Min(byMemory, byPrimitives);
        var voxels = (int)Math.Min(limit, (ulong)MaxHardwareRayTracingVoxels);
        // 用户日志里要能直接看出这台机器为什么回退：写明上限由显存预算还是图元上限决定
        MVoxelLog.Info($"硬件加速容量：本机上限 {voxels:N0} 格（显存 {deviceLocalBytes / (1024UL * 1024UL)} MB，" +
            $"预算 {budget / (1024UL * 1024UL)} MB，图元上限 {maxPrimitiveCount:N0}，绝对上限 {MaxHardwareRayTracingVoxels:N0}）");
        return voxels;
    }

    /// <summary>
    /// 获取硬件 RT 是否已完成初始化
    /// </summary>
    internal bool IsReady => _pipeline.Handle != 0 && _tlas.Handle != 0 && _sbtBuffer.Handle != 0;

    /// <summary>
    /// 当前加速结构是否可用于 Ray Query
    /// </summary>
    internal bool HasAccelerationStructure => _tlas.Handle != 0;

    /// <summary>
    /// 顶层加速结构句柄，供 Ray Query 的 Compute 着色器绑定
    /// </summary>
    internal AccelerationStructureKHR AccelerationStructure => _tlas;

    /// <summary>
    /// 获取最近一次硬件 RT 错误
    /// </summary>
    internal string? LastError => _lastError;

    /// <summary>最近一次加速结构重建总耗时毫秒（含等待设备空闲）</summary>
    internal double LastBuildMs => _lastBuildMs;

    /// <summary>最近一次加速结构重建里等待设备空闲的耗时毫秒</summary>
    internal double LastBuildIdleMs => _lastBuildIdleMs;

    /// <summary>最近一次加速结构重建里生成几何缓冲的耗时毫秒</summary>
    internal double LastGeometryMs => _lastGeometryMs;

    /// <summary>最近一次加速结构重建里构建 BLAS 的耗时毫秒</summary>
    internal double LastBlasMs => _lastBlasMs;

    /// <summary>最近一次加速结构重建里构建 TLAS 的耗时毫秒</summary>
    internal double LastTlasMs => _lastTlasMs;

    /// <summary>最近一次加速结构重建覆盖的体素数量</summary>
    internal int LastBuildCellCount => _lastBuildCellCount;

    /// <summary>
    /// 本机实际可承载的硬件加速体素上限：显存预算、图元上限与绝对上限取最小值。
    /// 与显卡厂商无关，NVIDIA 与 AMD 走同一套判定
    /// </summary>
    internal int MaxAccelerationVoxels => _maxAccelerationVoxels;

    /// <summary>
    /// 与当前加速结构同序的体素快照缓冲。
    /// 工作集在编辑中变化时加速结构会晚一拍重建，硬件阴影必须按快照取半透明标记，
    /// 否则图元索引会和新的 Compute 体素缓冲错位
    /// </summary>
    internal VkBuffer AccelerationCellBuffer => _cellBuffer;

    /// <summary>与当前加速结构同序的体素数量</summary>
    internal int AccelerationCellCount => _lastBuildCellCount;

    /// <summary>拖动对象实例的体素快照缓冲，与实例 1 的 BLAS 同序</summary>
    internal VkBuffer DragAccelerationCellBuffer => _dragCellBuffer;

    /// <summary>拖动对象实例覆盖的体素数</summary>
    internal int DragAccelerationCellCount => _dragCellCount;

    /// <summary>拖动对象体素快照的局部原点</summary>
    internal VoxelCoord64 DragAccelerationOrigin => new(_dragOriginX, _dragOriginY, _dragOriginZ);

    /// <summary>当前实例变换里写入的平移量</summary>
    internal VoxelCoord64 DragInstanceOffset => new(_dragOffsetX, _dragOffsetY, _dragOffsetZ);

    /// <summary>加速结构是否包含可用的拖动对象实例</summary>
    internal bool HasDragAcceleration => _dragBlas.Handle != 0 && _dragCellCount > 0;

    /// <summary>
    /// 只改写拖动实例的平移并重建 TLAS；静态 BLAS 与对象 BLAS 保持不动
    /// </summary>
    /// <param name="offset">拖动对象相对起始位置的整格平移</param>
    /// <returns>实例变换更新成功时为 true</returns>
    internal bool UpdateDragInstance(VoxelCoord64 offset)
    {
        if (_tlas.Handle == 0 || _dragBlas.Handle == 0 || _instanceBuffer.Handle == 0) return false;
        if (offset.X == _dragOffsetX && offset.Y == _dragOffsetY && offset.Z == _dragOffsetZ) return true;
        try
        {
            _dragOffsetX = offset.X;
            _dragOffsetY = offset.Y;
            _dragOffsetZ = offset.Z;
            WriteInstanceBuffer();
            RebuildTlas();
            return true;
        }
        catch (Exception exception)
        {
            _lastError = exception.ToString();
            MVoxelLog.Error("更新拖动对象实例变换失败，回退软件 BVH", exception);
            return false;
        }
    }

    /// <summary>
    /// 获取当前显示纹理
    /// </summary>
    internal nint DisplayTextureId => _displayTextureId;

    /// <summary>
    /// 获取当前硬件 RT 显示结果是否支持 PNG 回读
    /// </summary>
    internal bool CanSaveDisplayPng => IsReady && _outputImage.Handle != 0 &&
        _outputIsShaderReadOnly && _outputWidth > 0 && _outputHeight > 0;

    /// <summary>
    /// 将当前硬件 RT 显示结果回读并保存为 PNG
    /// </summary>
    /// <param name="path">输出文件路径</param>
    /// <returns>当前是否保存成功</returns>
    internal bool SaveDisplayPng(string path)
    {
        if (!CanSaveDisplayPng)
        {
            return false;
        }

        var byteCount = checked((ulong)_outputWidth * _outputHeight * 4);
        var stagingBuffer = CreateDeviceBuffer(byteCount, BufferUsageFlags.TransferDstBit,
            out var stagingMemory, hostVisible: true);
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
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _outputImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
                };
                _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.FragmentShaderBit,
                    PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toTransfer);

                BufferImageCopy copy = new()
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D(_outputWidth, _outputHeight, 1)
                };
                _api.CmdCopyImageToBuffer(commandBuffer, _outputImage, ImageLayout.TransferSrcOptimal,
                    stagingBuffer, 1, in copy);

                ImageMemoryBarrier backToShader = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _outputImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
                };
                _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, in backToShader);
            });

            void* mapped = null;
            Check(_api.MapMemory(_device, stagingMemory, 0, byteCount, 0, &mapped), "映射硬件 RT 导出暂存缓冲");
            try
            {
                Marshal.Copy((nint)mapped, pixels, 0, pixels.Length);
            }
            finally
            {
                _api.UnmapMemory(_device, stagingMemory);
            }

            AtomicFile.WriteBytes(path, PngEncoder.EncodeRgba8(pixels, (int)_outputWidth, (int)_outputHeight));
            return true;
        }
        catch (Exception exception)
        {
            _lastError = exception.ToString();
            MVoxelLog.Error("导出硬件 RT 图像失败", exception);
            return false;
        }
        finally
        {
            _api.DestroyBuffer(_device, stagingBuffer, null);
            _api.FreeMemory(_device, stagingMemory, null);
        }
    }

    /// <summary>
    /// 尝试执行一帧硬件光追
    /// </summary>
    /// <param name="workset">当前体素工作集</param>
    /// <param name="settings">渲染配置</param>
    /// <param name="frameState">相机与区域参数</param>
    /// <param name="widthPixels">输出宽度</param>
    /// <param name="heightPixels">输出高度</param>
    /// <returns>执行成功时返回 true</returns>
    internal bool TryRender(VoxelGpuWorkset workset, RenderSettings settings,
        in VoxelRenderParameters.FrameState frameState, int widthPixels, int heightPixels)
    {
        if (_disposed || workset.IsEmpty || widthPixels <= 0 || heightPixels <= 0)
        {
            return false;
        }

        if (workset.Cells.Count > MaxAccelerationVoxels)
        {
            _lastError = $"硬件 RT 体素数量 {workset.Cells.Count:N0} 超过本机显存预算上限 {MaxAccelerationVoxels:N0}，已回退 Vulkan Compute";
            if (_oversizedWarningVersion != workset.SourceVersion)
            {
                _oversizedWarningVersion = workset.SourceVersion;
                MVoxelLog.Warn(_lastError);
            }
            return false;
        }

        _oversizedWarningVersion = -1;

        try
        {
            _lastError = null;
            EnsureAccelerationResources(workset);
            EnsurePipelineResources();
            EnsureOutputImage((uint)widthPixels, (uint)heightPixels);
            UploadParameters(settings, frameState, workset);

            var raygen = _sbt.Raygen;
            var miss = _sbt.Miss;
            var hit = _sbt.Hit;
            var callable = _sbt.Callable;
            _context.ExecuteImmediate(commandBuffer =>
            {
                TransitionOutputToGeneral(commandBuffer);
                apiCmdBindPipeline(commandBuffer);
                DescriptorSet descriptorSet = _descriptorSet;
                _api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.RayTracingKhr, _pipelineLayout,
                    0, 1, in descriptorSet, 0, null);
                _rayTracingApi.CmdTraceRays(commandBuffer, in raygen, in miss, in hit,
                    in callable, (uint)widthPixels, (uint)heightPixels, 1);
                TransitionOutputToShaderRead(commandBuffer);
            });
            return true;
        }
        catch (Exception exception)
        {
            _lastError = exception.ToString();
            MVoxelLog.Error("Vulkan 硬件 RT 初始化或绘制失败，交由 Compute 回退", exception);
            return false;
        }
    }

    /// <summary>
    /// 为 Ray Query 阴影准备加速结构：只建 BLAS 与 TLAS，不创建光追管线与 SBT
    /// </summary>
    /// <param name="cells">与 Compute 着色器 binding 0 顺序一致的体素列表</param>
    /// <param name="sourceVersion">工作集内容版本，版本不变时直接复用</param>
    /// <returns>加速结构可用时返回 true</returns>
    internal bool EnsureShadowAcceleration(IReadOnlyList<VoxelGpuCell> cells, int sourceVersion,
        IReadOnlyList<VoxelGpuCell>? dragCells = null, VoxelCoord64 dragOrigin = default,
        VoxelCoord64 dragOffset = default, int dragVersion = -1, VoxelCoord64 staticOrigin = default)
    {
        if (_disposed || (dragCells?.Count is not > 0 && cells.Count == 0) ||
            cells.Count > MaxAccelerationVoxels || (dragCells?.Count > MaxAccelerationVoxels) == true)
        {
            return false;
        }

        if (!FitsAccelerationLimits(cells.Count + (dragCells?.Count ?? 0)))
        {
            _lastError = $"场景 {cells.Count:N0} 格超过此设备的加速结构容量上限 {MaxAccelerationVoxels:N0}，已回退软件遍历";
            return false;
        }

        // 只有拖动对象的实例平移发生变化：TLAS 是唯一会变的结构，
        // 静态与对象几何都可以原样复用，这一步就是拖动时的 2ms 级更新
        if (dragVersion >= 0 && _dragGeometryVersion == dragVersion &&
            _sourceVersion == sourceVersion && _tlas.Handle != 0 && _dragBlas.Handle != 0)
        {
            return UpdateDragInstance(dragOffset);
        }

        // 从拖动状态退出且静态几何没变：只隐藏实例 1，不重建 BLAS
        if (dragVersion < 0 && _dragInstanceVisible && _sourceVersion == sourceVersion &&
            _tlas.Handle != 0)
        {
            _dragInstanceVisible = false;
            _dragGeometryVersion = -1;
            try
            {
                WriteInstanceBuffer();
                RebuildTlas();
                return true;
            }
            catch (Exception exception)
            {
                _lastError = exception.ToString();
                return false;
            }
        }

        if (_sourceVersion == sourceVersion && _tlas.Handle != 0 && dragVersion < 0 && !_dragInstanceVisible)
            return true;
        _lastBuildMs = 0;
        _lastBuildIdleMs = 0;
        _lastGeometryMs = 0;
        _lastBlasMs = 0;
        _lastTlasMs = 0;
        var buildWatch = Stopwatch.StartNew();
        try
        {
            _lastError = null;
            var phaseWatch = Stopwatch.StartNew();
            // 几何顺序必须与 Compute 的 binding 0 完全一致，否则命中图元换算出的
            // 半透明标记会错位，阴影就会错误地穿过水面或玻璃。
            // 这里同时保留一份与加速结构同序的体素快照：编辑期加速结构还没重建时，
            // 硬件阴影仍按快照读取半透明标记，不必退回整帧软件 BVH
            Check(_api.DeviceWaitIdle(_device), "等待硬件阴影加速结构重建");
            _lastBuildIdleMs = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            DestroyAccelerationResources();
            // 拖动对象单独建一份几何、体素快照与 BLAS；它的实例变换由 TLAS 承担，
            // 拖动过程中只改实例平移，不会让静态几何的 BLAS 失效
            var hasDrag = dragCells is { Count: > 0 };
            if (cells.Count > 0) CreateGeometryBuffers(cells, includeCellBuffer: true);
            if (hasDrag) CreateDragGeometryBuffers(dragCells!);
            _lastGeometryMs = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            if (cells.Count > 0) CreateBlas(cells.Count);
            if (hasDrag) CreateDragBlas(dragCells!.Count);

            _lastBlasMs = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            _dragCellCount = hasDrag ? dragCells!.Count : 0;
            _dragOriginX = hasDrag ? dragOrigin.X : 0;
            _dragOriginY = hasDrag ? dragOrigin.Y : 0;
            _dragOriginZ = hasDrag ? dragOrigin.Z : 0;
            _dragOffsetX = hasDrag ? dragOffset.X : 0;
            _dragOffsetY = hasDrag ? dragOffset.Y : 0;
            _dragOffsetZ = hasDrag ? dragOffset.Z : 0;
            _dragGeometryVersion = hasDrag ? dragVersion : -1;
            _dragInstanceVisible = hasDrag;
            _staticOriginX = staticOrigin.X;
            _staticOriginY = staticOrigin.Y;
            _staticOriginZ = staticOrigin.Z;
            CreateTlas();
            _lastTlasMs = phaseWatch.Elapsed.TotalMilliseconds;
            _sourceVersion = sourceVersion;
            _lastBuildCellCount = cells.Count;
            _lastBuildMs = buildWatch.Elapsed.TotalMilliseconds;
            if (_lastBuildMs >= SlowAccelerationBuildMs)
            {
                MVoxelLog.Warn(
                    $"硬件阴影加速结构重建偏慢：总计 {_lastBuildMs:0.0}ms（等待设备 {_lastBuildIdleMs:0.0}ms，几何 {_lastGeometryMs:0.0}ms，BLAS {_lastBlasMs:0.0}ms，TLAS {_lastTlasMs:0.0}ms），体素 {cells.Count:N0}，设备 {_context.DeviceName}");
            }
            return _tlas.Handle != 0;
        }
        catch (Exception exception)
        {
            _lastError = exception.ToString();
            if (_context.IsDeviceLost || _lastError.Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
            {
                _context.MarkDeviceLost();
                throw;
            }
            // 立即提交中途失败时，先确认没有在途构建再释放半成品。
            var idleResult = _api.DeviceWaitIdle(_device);
            if (idleResult == Result.ErrorDeviceLost)
            {
                _context.MarkDeviceLost();
                throw new InvalidOperationException("硬件阴影失败后等待设备：ErrorDeviceLost", exception);
            }
            if (idleResult == Result.Success)
            {
                DestroyScratch();
                DestroyAccelerationResources();
            }
            MVoxelLog.Error("创建硬件光追加速结构失败，阴影回退 Compute", exception);
            return false;
        }
    }

    /// <summary>
    /// 创建加速结构与几何输入资源
    /// </summary>
    /// <param name="workset">当前体素工作集</param>
    private void EnsureAccelerationResources(VoxelGpuWorkset workset)
    {
        if (_sourceVersion == workset.SourceVersion && IsReady) return;
        if (!FitsAccelerationLimits(workset.Cells.Count))
            throw new InvalidOperationException("场景超过此设备的加速结构容量");
        _api.DeviceWaitIdle(_device);
        DestroyAccelerationResources();
        CreateGeometryBuffers(workset.Cells, includeCellBuffer: true);
        CreateBlas(workset.Cells.Count);
        CreateTlas();
        _sourceVersion = workset.SourceVersion;
    }

    /// <summary>
    /// 创建每个体素的立方体三角形和颜色缓冲
    /// </summary>
    /// <param name="cells">与 Compute 着色器同序的体素列表</param>
    /// <param name="includeCellBuffer">独立 RT Pipeline 需要单独的体素缓冲；Ray Query 复用 Compute 缓冲。</param>
    private void CreateGeometryBuffers(IReadOnlyList<VoxelGpuCell> cells, bool includeCellBuffer)
    {
        var vertices = new float[checked(cells.Count * VerticesPerVoxel * 3)];
        var indices = new uint[checked(cells.Count * IndicesPerVoxel)];
        var packed = includeCellBuffer ? new byte[checked(cells.Count * CellStride)] : null;
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            var cell = cells[cellIndex];
            var x = (float)cell.X;
            var y = (float)cell.Y;
            var z = (float)cell.Z;
            var ex = (float)cell.ExtentX;
            var ey = (float)cell.ExtentY;
            var ez = (float)cell.ExtentZ;
            for (var vertex = 0; vertex < VerticesPerVoxel; vertex++)
            {
                vertices[vertexOffset++] = x + (vertex is 1 or 2 or 5 or 6 ? ex : 0F);
                vertices[vertexOffset++] = y + (vertex is 2 or 3 or 6 or 7 ? ey : 0F);
                vertices[vertexOffset++] = z + (vertex >= 4 ? ez : 0F);
            }
            var baseVertex = (uint)(cellIndex * VerticesPerVoxel);
            for (var index = 0; index < BoxTriangleIndices.Length; index++)
                indices[indexOffset++] = baseVertex + BoxTriangleIndices[index];
            if (packed is not null) WritePackedCell(packed.AsSpan(cellIndex * CellStride, CellStride), cell);
        }

        _vertexBuffer = CreateHostBuffer(MemoryMarshal.AsBytes(vertices.AsSpan()),
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _vertexMemory);
        _indexBuffer = CreateHostBuffer(MemoryMarshal.AsBytes(indices.AsSpan()),
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _indexMemory);
        if (packed is not null)
            _cellBuffer = CreateHostBuffer(packed,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                out _cellMemory);
    }

    /// <summary>
    /// 写入与 Compute 着色器一致的体素结构布局
    /// </summary>
    /// <param name="destination">目标字节区</param>
    /// <param name="cell">体素数据</param>
    internal static void WritePackedCell(Span<byte> destination, VoxelGpuCell cell)
    {
        MemoryMarshal.Write(destination[0..4], cell.X);
        MemoryMarshal.Write(destination[4..8], cell.Y);
        MemoryMarshal.Write(destination[8..12], cell.Z);
        var extent = cell.ExtentX | (cell.ExtentY << 8) | (cell.ExtentZ << 16);
        MemoryMarshal.Write(destination[12..16], extent);
        // ClosestHitShader declares `color` as vec4 in std430. Store normalized
        // float channels just like VulkanVoxelRenderPipeline.PackCellInto; raw
        // RGBA bytes here were interpreted as four tiny/subnormal floats by GLSL.
        var color = new System.Numerics.Vector4(
            cell.Value.Color.R / 255F,
            cell.Value.Color.G / 255F,
            cell.Value.Color.B / 255F,
            cell.Value.Color.A / 255F);
        MemoryMarshal.Write(destination[16..32], in color);
        MemoryMarshal.Write(destination[32..36], (uint)cell.Value.MaterialSlot | ((uint)cell.Value.Flags << 16));
    }

    /// <summary>
    /// 创建拖动对象的几何缓冲与体素快照；坐标相对拖动对象自己的工作集原点
    /// </summary>
    private void CreateDragGeometryBuffers(IReadOnlyList<VoxelGpuCell> cells)
    {
        var vertices = new float[checked(cells.Count * VerticesPerVoxel * 3)];
        var indices = new uint[checked(cells.Count * IndicesPerVoxel)];
        var packed = new byte[checked(cells.Count * CellStride)];
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            var cell = cells[cellIndex];
            var x = (float)cell.X;
            var y = (float)cell.Y;
            var z = (float)cell.Z;
            var ex = (float)cell.ExtentX;
            var ey = (float)cell.ExtentY;
            var ez = (float)cell.ExtentZ;
            for (var vertex = 0; vertex < VerticesPerVoxel; vertex++)
            {
                vertices[vertexOffset++] = x + (vertex is 1 or 2 or 5 or 6 ? ex : 0F);
                vertices[vertexOffset++] = y + (vertex is 2 or 3 or 6 or 7 ? ey : 0F);
                vertices[vertexOffset++] = z + (vertex >= 4 ? ez : 0F);
            }
            var baseVertex = (uint)(cellIndex * VerticesPerVoxel);
            for (var index = 0; index < BoxTriangleIndices.Length; index++)
                indices[indexOffset++] = baseVertex + BoxTriangleIndices[index];
            WritePackedCell(packed.AsSpan(cellIndex * CellStride, CellStride), cell);
            // 拖动中的对象必然是当前选中对象：给实例快照打上选中标记，
            // 外轮廓因此跟实例一起移动，不需要再单独维护一份蒙版
            MemoryMarshal.Write(packed.AsSpan(cellIndex * CellStride + 36, 4), 1);
        }

        _dragVertexBuffer = CreateHostBuffer(MemoryMarshal.AsBytes(vertices.AsSpan()),
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _dragVertexMemory);
        _dragIndexBuffer = CreateHostBuffer(MemoryMarshal.AsBytes(indices.AsSpan()),
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _dragIndexMemory);
        _dragCellBuffer = CreateHostBuffer(packed,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            out _dragCellMemory);
    }

    /// <summary>
    /// 创建拖动对象的三角形 BLAS
    /// </summary>
    /// <param name="voxelCount">体素数量</param>
    private void CreateDragBlas(int voxelCount) =>
        CreateBlas(voxelCount, _dragVertexBuffer, _dragIndexBuffer, out _dragBlas,
            out _dragBlasBuffer, out _dragBlasMemory);

    /// <summary>
    /// 创建体素三角形 BLAS
    /// </summary>
    /// <param name="voxelCount">体素数量</param>
    private void CreateBlas(int voxelCount) =>
        CreateBlas(voxelCount, _vertexBuffer, _indexBuffer, out _blas, out _blasBuffer, out _blasMemory);

    private void CreateBlas(int voxelCount, VkBuffer vertexBuffer, VkBuffer indexBuffer,
        out AccelerationStructureKHR blas, out VkBuffer blasBuffer, out DeviceMemory blasMemory)
    {
        blas = default;
        blasBuffer = default;
        blasMemory = default;
        var triangles = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(vertexBuffer) },
            VertexStride = 3 * sizeof(float),
            MaxVertex = (uint)(voxelCount * VerticesPerVoxel - 1),
            IndexType = IndexType.Uint32,
            IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(indexBuffer) },
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
        };
        var primitives = (uint)(voxelCount * PrimitivesPerVoxel);
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr
        };
        _accelerationApi.GetAccelerationStructureBuildSizes(_device, AccelerationStructureBuildTypeKHR.DeviceKhr,
            in info, in primitives, out sizes);
        blasBuffer = CreateDeviceBuffer(sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            out blasMemory);
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = blasBuffer,
            Size = sizes.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
        };
        Check(_accelerationApi.CreateAccelerationStructure(_device, in createInfo, null, out blas), "创建体素 BLAS");
        CreateScratch(sizes.BuildScratchSize);
        info.DstAccelerationStructure = blas;
        info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = _scratchAddress };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        AccelerationStructureBuildRangeInfoKHR* rangePointer = &range;
        _context.ExecuteImmediate(commandBuffer =>
        {
            _accelerationApi.CmdBuildAccelerationStructures(commandBuffer, 1, in info, in rangePointer);
            MemoryBarrier barrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
                DstAccessMask = AccessFlags.AccelerationStructureReadBitKhr
            };
            _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AccelerationStructureBuildBitKhr,
                PipelineStageFlags.AccelerationStructureBuildBitKhr | PipelineStageFlags.ComputeShaderBit |
                    (_context.SupportsHardwareRayTracing ? PipelineStageFlags.RayTracingShaderBitKhr : 0),
                0, 1, in barrier, 0, null, 0, null);
        });
        DestroyScratch();
    }

    /// <summary>
    /// 创建含静态实例与拖动实例的 TLAS。两个实例始终存在：
    /// 没有拖动对象时实例 1 的 mask 为 0，射线看不到它
    /// </summary>
    private void CreateTlas()
    {
        // 固定两个实例，TLAS 容量在创建时按 2 个实例申请，后续拖动只重建 TLAS，
        // 不需要重新查询尺寸或重新创建加速结构
        _instanceCount = 2;
        _instanceBuffer = CreateHostBuffer(new byte[64 * _instanceCount],
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _instanceMemory);
        WriteInstanceBuffer();

        var instances = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
            ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(_instanceBuffer) },
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR { Instances = instances },
        };
        var primitives = _instanceCount;
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr
        };
        _accelerationApi.GetAccelerationStructureBuildSizes(_device, AccelerationStructureBuildTypeKHR.DeviceKhr,
            in info, in primitives, out sizes);
        _tlasBuffer = CreateDeviceBuffer(sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            out _tlasMemory);
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = _tlasBuffer,
            Size = sizes.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
        };
        Check(_accelerationApi.CreateAccelerationStructure(_device, in createInfo, null, out _tlas), "创建体素 TLAS");
        _tlasScratchSize = sizes.BuildScratchSize;
        RebuildTlas();
    }

    /// <summary>
    /// 按当前实例数据重建 TLAS（拖动时只走这一步）
    /// </summary>
    private void RebuildTlas()
    {
        if (_tlas.Handle == 0 || _tlasScratchSize == 0) return;
        var instances = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
            ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(_instanceBuffer) },
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR { Instances = instances },
        };
        var primitives = _instanceCount;
        var info = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };
        CreateScratch(_tlasScratchSize);
        info.DstAccelerationStructure = _tlas;
        info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = _scratchAddress };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        AccelerationStructureBuildRangeInfoKHR* rangePointer = &range;
        _context.ExecuteImmediate(commandBuffer =>
        {
            _accelerationApi.CmdBuildAccelerationStructures(commandBuffer, 1, in info, in rangePointer);
            MemoryBarrier barrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
                DstAccessMask = AccessFlags.AccelerationStructureReadBitKhr
            };
            _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AccelerationStructureBuildBitKhr,
                PipelineStageFlags.AccelerationStructureBuildBitKhr | PipelineStageFlags.ComputeShaderBit |
                    (_context.SupportsHardwareRayTracing ? PipelineStageFlags.RayTracingShaderBitKhr : 0),
                0, 1, in barrier, 0, null, 0, null);
        });
        DestroyScratch();
    }

    /// <summary>
    /// 写入两个实例：0 是静态世界，1 是拖动对象；拖动对象缺失时 mask 设为 0
    /// </summary>
    private void WriteInstanceBuffer()
    {
        if (_instanceBuffer.Handle == 0) return;
        var instanceCount = _instanceCount == 0 ? 2U : _instanceCount;
        Span<byte> buffer = stackalloc byte[64 * 2];
        // 实例 0：静态 BLAS，单位变换
        WriteInstance(buffer[..64], _blas.Handle != 0 ? _blas : _dragBlas,
            0U, _blas.Handle != 0 ? 0xFF000000U : 0x00000000U, 0F, 0F, 0F);
        // 实例 1：拖动对象，平移 = (拖动原点 - 静态原点) + 累计格位移。
        // BLAS 顶点使用自己的局部原点，因此实例矩阵承担全部平移
        var dragVisible = _dragInstanceVisible && _dragBlas.Handle != 0;
        WriteInstance(buffer[64..], dragVisible ? _dragBlas : _blas,
            1U, dragVisible ? 0xFF000000U : 0x00000000U,
            _dragOffsetX + (_dragOriginX - _staticOriginX),
            _dragOffsetY + (_dragOriginY - _staticOriginY),
            _dragOffsetZ + (_dragOriginZ - _staticOriginZ));
        UploadMapped(_instanceMemory, buffer[..(int)(64 * instanceCount)]);
    }

    private void WriteInstance(Span<byte> destination, AccelerationStructureKHR structure,
        uint customIndex, uint maskAndIndex, float offsetX, float offsetY, float offsetZ)
    {
        // VkAccelerationStructureInstanceKHR 的行主序 3×4 变换：平移在每行第 4 列
        var transform = new float[]
        {
            1F, 0F, 0F, offsetX,
            0F, 1F, 0F, offsetY,
            0F, 0F, 1F, offsetZ,
        };
        MemoryMarshal.AsBytes(transform.AsSpan()).CopyTo(destination);
        // 第一个 DWORD 的低 24 位是实例自定义索引，高 8 位是 mask；
        // mask=0 会让所有使用 0xFF 的射线完全看不到该实例
        MemoryMarshal.Write(destination[48..52], maskAndIndex | customIndex);
        MemoryMarshal.Write(destination[52..56], 0U);
        MemoryMarshal.Write(destination[56..64], GetAddress(structure));
    }

    /// <summary>
    /// 创建独立 RT Pipeline、描述符布局和 SBT
    /// </summary>
    private void EnsurePipelineResources()
    {
        if (_pipeline.Handle != 0) return;
        _descriptorLayout = CreateDescriptorLayout();
        _descriptorPool = CreateDescriptorPool();
        _descriptorSet = AllocateDescriptorSet();
        _parameterBuffer = CreateDeviceBuffer((ulong)(VoxelRenderParameters.SlotCount * sizeof(float)),
            BufferUsageFlags.StorageBufferBit, out _parameterMemory, hostVisible: true);
        _pipelineLayout = CreatePipelineLayout();

        var modules = new[]
        {
            CreateShaderModule(VulkanShaderCompiler.Compile(RaygenShader, ShaderKind.RaygenShader, "MVoxel.HardwareRt.rgen", true, _context.HardwareShadersTargetVulkan11)),
            CreateShaderModule(VulkanShaderCompiler.Compile(MissShader, ShaderKind.MissShader, "MVoxel.HardwareRt.rmiss", true, _context.HardwareShadersTargetVulkan11)),
            CreateShaderModule(VulkanShaderCompiler.Compile(ClosestHitShader, ShaderKind.ClosesthitShader, "MVoxel.HardwareRt.rchit", true, _context.HardwareShadersTargetVulkan11)),
        };
        nint[] names = { SilkMarshal.StringToPtr("main"), SilkMarshal.StringToPtr("main"), SilkMarshal.StringToPtr("main") };
        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[3];
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.RaygenBitKhr, Module = modules[0], PName = (byte*)names[0] };
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.MissBitKhr, Module = modules[1], PName = (byte*)names[1] };
            stages[2] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ClosestHitBitKhr, Module = modules[2], PName = (byte*)names[2] };
            RayTracingShaderGroupCreateInfoKHR* groups = stackalloc RayTracingShaderGroupCreateInfoKHR[3];
            groups[0] = new() { SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr, GeneralShader = 0, ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr };
            groups[1] = new() { SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr, GeneralShader = 1, ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr };
            groups[2] = new() { SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr, GeneralShader = Vk.ShaderUnusedKhr, ClosestHitShader = 2, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr };
            RayTracingPipelineCreateInfoKHR createInfo = new()
            {
                SType = StructureType.RayTracingPipelineCreateInfoKhr,
                StageCount = 3,
                PStages = stages,
                GroupCount = 3,
                PGroups = groups,
                MaxPipelineRayRecursionDepth = 1,
                Layout = _pipelineLayout,
            };
            Check(_rayTracingApi.CreateRayTracingPipelines(_device, default, _context.PipelineCacheHandle, 1, in createInfo, null, out _pipeline), "创建 Vulkan 硬件 RT Pipeline");
        }
        finally
        {
            foreach (var name in names) SilkMarshal.Free(name);
            foreach (var module in modules) _api.DestroyShaderModule(_device, module, null);
        }

        CreateSbt();
        UpdateDescriptors();
        MVoxelLog.Info($"Vulkan 硬件 RT 资源就绪：BLAS、TLAS、RT Pipeline、SBT，SBT 步长={_sbt.Raygen.Stride}，句柄大小={_properties.ShaderGroupHandleSize}");
    }

    /// <summary>
    /// 创建 Raygen、Miss 和 Hit 三段 SBT
    /// </summary>
    private void CreateSbt()
    {
        var handleSize = _properties.ShaderGroupHandleSize;
        var recordStride = Align(handleSize, _properties.ShaderGroupHandleAlignment);
        var sectionStride = Align(recordStride, _properties.ShaderGroupBaseAlignment);
        var bytes = new byte[checked((int)(sectionStride * 3))];
        _rayTracingApi.GetRayTracingShaderGroupHandles(_device, _pipeline, 0, 3, bytes.AsSpan());
        _sbtBuffer = CreateHostBuffer(bytes,
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            out _sbtMemory);
        var address = GetAddress(_sbtBuffer);
        _sbt = new ShaderBindingTable(
            new StridedDeviceAddressRegionKHR(address, recordStride, sectionStride),
            new StridedDeviceAddressRegionKHR(address + sectionStride, recordStride, sectionStride),
            new StridedDeviceAddressRegionKHR(address + sectionStride * 2, recordStride, sectionStride),
            default);
    }

    /// <summary>
    /// 创建硬件 RT 描述符资源
    /// </summary>
    /// <returns>描述符布局</returns>
    private DescriptorSetLayout CreateDescriptorLayout()
    {
        var stages = ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.MissBitKhr | ShaderStageFlags.ClosestHitBitKhr;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[4];
        bindings[0] = new(0, DescriptorType.AccelerationStructureKhr, 1, stages);
        bindings[1] = new(1, DescriptorType.StorageImage, 1, stages);
        bindings[2] = new(2, DescriptorType.StorageBuffer, 1, stages);
        bindings[3] = new(3, DescriptorType.StorageBuffer, 1, stages);
        DescriptorSetLayoutCreateInfo createInfo = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings };
        DescriptorSetLayout layout = default;
        Check(_api.CreateDescriptorSetLayout(_device, in createInfo, null, out layout), "创建硬件 RT 描述符布局");
        return layout;
    }

    /// <summary>
    /// 创建硬件 RT 描述符池
    /// </summary>
    /// <returns>描述符池</returns>
    private DescriptorPool CreateDescriptorPool()
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[4];
        sizes[0] = new(DescriptorType.AccelerationStructureKhr, 1);
        sizes[1] = new(DescriptorType.StorageImage, 1);
        sizes[2] = new(DescriptorType.StorageBuffer, 2);
        DescriptorPoolCreateInfo info = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 3, PPoolSizes = sizes };
        DescriptorPool pool = default;
        Check(_api.CreateDescriptorPool(_device, in info, null, out pool), "创建硬件 RT 描述符池");
        return pool;
    }

    /// <summary>
    /// 分配硬件 RT 描述符集
    /// </summary>
    /// <returns>描述符集</returns>
    private DescriptorSet AllocateDescriptorSet()
    {
        var layout = _descriptorLayout;
        DescriptorSetAllocateInfo info = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        DescriptorSet set = default;
        Check(_api.AllocateDescriptorSets(_device, in info, out set), "分配硬件 RT 描述符集");
        return set;
    }

    /// <summary>
    /// 创建硬件 RT 管线布局
    /// </summary>
    /// <returns>管线布局</returns>
    private PipelineLayout CreatePipelineLayout()
    {
        var layout = _descriptorLayout;
        PipelineLayoutCreateInfo info = new() { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout };
        PipelineLayout result = default;
        Check(_api.CreatePipelineLayout(_device, in info, null, out result), "创建硬件 RT 管线布局");
        return result;
    }

    /// <summary>
    /// 更新硬件 RT 描述符
    /// </summary>
    private void UpdateDescriptors()
    {
        if (_tlas.Handle == 0 || _outputView.Handle == 0 || _parameterBuffer.Handle == 0 || _cellBuffer.Handle == 0) return;
        var tlas = _tlas;
        WriteDescriptorSetAccelerationStructureKHR asWrite = new()
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &tlas,
        };
        DescriptorImageInfo image = new(default, _outputView, ImageLayout.General);
        DescriptorBufferInfo parameters = new(_parameterBuffer, 0, (ulong)(VoxelRenderParameters.SlotCount * sizeof(float)));
        DescriptorBufferInfo cells = new(_cellBuffer, 0, Vk.WholeSize);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
        writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr, PNext = &asWrite };
        writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &image };
        writes[2] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &parameters };
        writes[3] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &cells };
        _api.UpdateDescriptorSets(_device, 4, writes, 0, null);
    }

    /// <summary>
    /// 确保输出图像尺寸与 ImGui 纹理一致
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    private void EnsureOutputImage(uint width, uint height)
    {
        if (_outputImage.Handle != 0 && _outputWidth == width && _outputHeight == height) return;
        _api.DeviceWaitIdle(_device);
        if (_displayTextureId != 0) _imguiRenderer.UnregisterTexture(_displayTextureId);
        DestroyOutputImage();
        ImageCreateInfo info = new()
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
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_api.CreateImage(_device, in info, null, out _outputImage), "创建硬件 RT 输出图像");
        _api.GetImageMemoryRequirements(_device, _outputImage, out var requirements);
        _outputMemory = AllocateMemory(requirements, MemoryPropertyFlags.DeviceLocalBit);
        Check(_api.BindImageMemory(_device, _outputImage, _outputMemory, 0), "绑定硬件 RT 输出图像");
        _outputView = CreateImageView(_outputImage);
        _displayTextureId = _imguiRenderer.RegisterExternalTexture(_outputView);
        _outputWidth = width;
        _outputHeight = height;
        _outputIsShaderReadOnly = false;
        UpdateDescriptors();
    }

    /// <summary>
    /// 上传本帧参数
    /// </summary>
    /// <param name="settings">渲染配置</param>
    /// <param name="frameState">相机状态</param>
    /// <param name="workset">体素工作集</param>
    private void UploadParameters(RenderSettings settings, in VoxelRenderParameters.FrameState frameState, VoxelGpuWorkset workset)
    {
        var values = VoxelRenderParameters.Build(settings, frameState, frameState.FrameIndex);
        values[VoxelRenderParameters.VoxelCount] = workset.Cells.Count;
        void* mapped = null;
        Check(_api.MapMemory(_device, _parameterMemory, 0, (ulong)(values.Length * sizeof(float)), 0, &mapped), "映射硬件 RT 参数缓冲");
        try
        {
            fixed (float* source = values) System.Buffer.MemoryCopy(source, mapped, values.Length * sizeof(float), values.Length * sizeof(float));
        }
        finally { _api.UnmapMemory(_device, _parameterMemory); }
    }

    /// <summary>
    /// 记录硬件 RT 管线绑定
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void apiCmdBindPipeline(CommandBuffer commandBuffer) => _api.CmdBindPipeline(commandBuffer, PipelineBindPoint.RayTracingKhr, _pipeline);

    /// <summary>
    /// 将输出图像切换到光追写入布局
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionOutputToGeneral(CommandBuffer commandBuffer)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _outputIsShaderReadOnly ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcAccessMask = _outputIsShaderReadOnly ? AccessFlags.ShaderReadBit : 0,
            DstAccessMask = AccessFlags.ShaderWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _outputImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _api.CmdPipelineBarrier(commandBuffer, _outputIsShaderReadOnly ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.RayTracingShaderBitKhr, 0, 0, null, 0, null, 1, in barrier);
    }

    /// <summary>
    /// 将输出图像切换到 ImGui 采样布局
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionOutputToShaderRead(CommandBuffer commandBuffer)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _outputImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.RayTracingShaderBitKhr, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in barrier);
        _outputIsShaderReadOnly = true;
    }

    /// <summary>
    /// 查询设备 RT 管线对 SBT 的对齐约束
    /// </summary>
    /// <returns>设备 RT 属性</returns>
    private bool FitsAccelerationLimits(int cellCount) =>
        cellCount <= MaxAccelerationVoxels &&
        (ulong)cellCount * PrimitivesPerVoxel <= _accelerationProperties.MaxPrimitiveCount &&
        _accelerationProperties.MaxGeometryCount >= 1 && _accelerationProperties.MaxInstanceCount >= 1;

    private PhysicalDeviceRayTracingPipelinePropertiesKHR QueryProperties(
        out PhysicalDeviceAccelerationStructurePropertiesKHR accelerationProperties, out ulong scratchAlignment)
    {
        PhysicalDeviceProperties2 properties = new() { SType = StructureType.PhysicalDeviceProperties2 };
        PhysicalDeviceRayTracingPipelinePropertiesKHR rayTracing = new() { SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr };
        PhysicalDeviceAccelerationStructurePropertiesKHR acceleration = new() { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
        if (_context.SupportsHardwareRayTracing)
        {
            properties.PNext = &rayTracing;
            rayTracing.PNext = &acceleration;
        }
        else properties.PNext = &acceleration;
        _api.GetPhysicalDeviceProperties2(_context.PhysicalDevice, &properties);
        // scratch 设备地址必须按该对齐值对齐，部分驱动（含 AMD）会对未对齐地址直接报设备丢失
        scratchAlignment = Math.Max(acceleration.MinAccelerationStructureScratchOffsetAlignment, 16U);
        // 不保留查询时指向栈上结构的 pNext 指针。
        acceleration.PNext = null;
        accelerationProperties = acceleration;
        return rayTracing;
    }

    /// <summary>
    /// 创建可获取设备地址的主机缓冲
    /// </summary>
    /// <param name="data">初始字节数据</param>
    /// <param name="usage">缓冲用途</param>
    /// <param name="memory">输出内存</param>
    /// <returns>创建的缓冲</returns>
    private VkBuffer CreateHostBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage, out DeviceMemory memory)
    {
        var buffer = CreateDeviceBuffer((ulong)Math.Max(data.Length, 1), usage, out memory, hostVisible: true);
        try
        {
            UploadMapped(memory, data);
            return buffer;
        }
        catch (Exception exception)
        {
            if (exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
                _context.MarkDeviceLost();
            if (!_context.IsDeviceLost)
            {
                _api.DestroyBuffer(_device, buffer, null);
                _api.FreeMemory(_device, memory, null);
            }
            memory = default;
            throw;
        }
    }

    /// <summary>
    /// 创建设备缓冲
    /// </summary>
    /// <param name="size">缓冲大小</param>
    /// <param name="usage">缓冲用途</param>
    /// <param name="memory">输出内存</param>
    /// <param name="hostVisible">是否使用主机可见内存</param>
    /// <returns>创建的缓冲</returns>
    private VkBuffer CreateDeviceBuffer(ulong size, BufferUsageFlags usage, out DeviceMemory memory, bool hostVisible = false)
    {
        usage |= BufferUsageFlags.ShaderDeviceAddressBit;
        BufferCreateInfo info = new() { SType = StructureType.BufferCreateInfo, Size = Math.Max(size, 1), Usage = usage, SharingMode = SharingMode.Exclusive };
        VkBuffer buffer = default;
        Check(_api.CreateBuffer(_device, in info, null, out buffer), "创建硬件 RT 缓冲");
        memory = default;
        try
        {
            _api.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            memory = AllocateMemory(requirements, hostVisible ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit : MemoryPropertyFlags.DeviceLocalBit, deviceAddress: true);
            Check(_api.BindBufferMemory(_device, buffer, memory, 0), "绑定硬件 RT 缓冲");
            return buffer;
        }
        catch (Exception exception)
        {
            if (exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
                _context.MarkDeviceLost();
            if (!_context.IsDeviceLost)
            {
                _api.DestroyBuffer(_device, buffer, null);
                if (memory.Handle != 0) _api.FreeMemory(_device, memory, null);
            }
            memory = default;
            throw;
        }
    }

    /// <summary>
    /// 创建临时加速结构构建 scratch 缓冲
    /// </summary>
    /// <param name="size">所需大小</param>
    private void CreateScratch(ulong size)
    {
        DestroyScratch();
        // 多分配一段对齐余量，保证设备地址可以向上对齐到驱动要求
        _scratchBuffer = CreateDeviceBuffer(size + _scratchAlignment - 1,
            BufferUsageFlags.StorageBufferBit, out _scratchMemory);
        _scratchAddress = AlignUp(GetAddress(_scratchBuffer), _scratchAlignment);
    }

    /// <summary>
    /// 释放临时 scratch 缓冲
    /// </summary>
    private void DestroyScratch()
    {
        if (_scratchBuffer.Handle != 0) _api.DestroyBuffer(_device, _scratchBuffer, null);
        if (_scratchMemory.Handle != 0) _api.FreeMemory(_device, _scratchMemory, null);
        _scratchBuffer = default;
        _scratchMemory = default;
        _scratchAddress = 0;
    }

    /// <summary>
    /// 向上对齐设备地址
    /// </summary>
    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1UL) / alignment * alignment;

    /// <summary>
    /// 将数据写入主机可见内存
    /// </summary>
    /// <param name="memory">目标内存</param>
    /// <param name="data">源数据</param>
    private void UploadMapped(DeviceMemory memory, ReadOnlySpan<byte> data)
    {
        void* mapped = null;
        Check(_api.MapMemory(_device, memory, 0, (ulong)Math.Max(data.Length, 1), 0, &mapped), "映射硬件 RT 主机缓冲");
        try
        {
            data.CopyTo(new Span<byte>(mapped, data.Length));
        }
        finally { _api.UnmapMemory(_device, memory); }
    }

    /// <summary>
    /// 把逐格选中与描边蒙版标记写入与加速结构同序的体素快照。
    /// 编辑期间主射线按快照取格数据，标记不同步到这里，选中外轮廓与体素描边会整体消失
    /// </summary>
    /// <param name="selectionFlags">逐格选中标记，顺序与加速结构一致</param>
    /// <param name="outlineFlags">逐格描边蒙版标记，顺序与加速结构一致</param>
    /// <param name="count">标记数量，不得超过快照体素数</param>
    internal void WriteCellFlags(ReadOnlySpan<uint> selectionFlags, ReadOnlySpan<uint> outlineFlags, int count)
    {
        if (_cellBuffer.Handle == 0 || _cellMemory.Handle == 0 || count <= 0) return;
        if (selectionFlags.Length < count || outlineFlags.Length < count) return;
        var size = (ulong)count * CellStride;
        void* mapped = null;
        Check(_api.MapMemory(_device, _cellMemory, 0, size, 0, &mapped), "映射加速结构体素快照标记");
        try
        {
            var bytes = new Span<byte>(mapped, (int)size);
            for (var index = 0; index < count; index++)
            {
                MemoryMarshal.Write(bytes.Slice(index * CellStride + 36, 4), selectionFlags[index]);
                MemoryMarshal.Write(bytes.Slice(index * CellStride + 40, 4), outlineFlags[index]);
            }
        }
        finally
        {
            _api.UnmapMemory(_device, _cellMemory);
        }
    }

    /// <summary>
    /// 获取缓冲设备地址
    /// </summary>
    /// <param name="buffer">目标缓冲</param>
    /// <returns>设备地址</returns>
    private ulong GetAddress(VkBuffer buffer)
    {
        BufferDeviceAddressInfo info = new() { SType = StructureType.BufferDeviceAddressInfo, Buffer = buffer };
        // 1.2 核心 BDA 设备不要求启用 KHR 扩展，须调用核心入口；1.1 走扩展入口。
        return _coreBufferDeviceAddress
            ? _api.GetBufferDeviceAddress(_device, in info)
            : _addressApi.GetBufferDeviceAddress(_device, in info);
    }

    /// <summary>
    /// 获取加速结构设备地址
    /// </summary>
    /// <param name="accelerationStructure">目标加速结构</param>
    /// <returns>设备地址</returns>
    private ulong GetAddress(AccelerationStructureKHR accelerationStructure)
    {
        AccelerationStructureDeviceAddressInfoKHR info = new() { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = accelerationStructure };
        return _accelerationApi.GetAccelerationStructureDeviceAddress(_device, in info);
    }

    /// <summary>
    /// 分配符合内存属性的 Vulkan 内存
    /// </summary>
    /// <param name="requirements">资源内存需求</param>
    /// <param name="properties">目标内存属性</param>
    /// <returns>设备内存</returns>
    private DeviceMemory AllocateMemory(MemoryRequirements requirements, MemoryPropertyFlags properties, bool deviceAddress = false)
    {
        PhysicalDeviceMemoryProperties memoryProperties = default;
        _api.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, &memoryProperties);
        uint index = 0;
        for (; index < memoryProperties.MemoryTypeCount; index++)
        {
            var type = memoryProperties.MemoryTypes[(int)index];
            if ((requirements.MemoryTypeBits & (1U << (int)index)) != 0 &&
                (type.PropertyFlags & MemoryPropertyFlags.ProtectedBit) == 0 &&
                (type.PropertyFlags & properties) == properties) break;
        }
        if (index >= memoryProperties.MemoryTypeCount) throw new InvalidOperationException($"没有满足硬件 RT 内存属性的 Vulkan 内存类型：{properties}");
        MemoryAllocateFlagsInfo addressFlags = new()
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit
        };
        MemoryAllocateInfo info = new() { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = index };
        if (deviceAddress) info.PNext = &addressFlags;
        DeviceMemory memory = default;
        Check(_api.AllocateMemory(_device, in info, null, out memory), "分配硬件 RT 内存");
        return memory;
    }

    /// <summary>
    /// 创建输出图像视图
    /// </summary>
    /// <param name="image">目标图像</param>
    /// <returns>图像视图</returns>
    private ImageView CreateImageView(Image image)
    {
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        ImageView view = default;
        Check(_api.CreateImageView(_device, in info, null, out view), "创建硬件 RT 输出视图");
        return view;
    }

    /// <summary>
    /// 释放输出图像
    /// </summary>
    private void DestroyOutputImage()
    {
        if (_outputView.Handle != 0) _api.DestroyImageView(_device, _outputView, null);
        if (_outputImage.Handle != 0) _api.DestroyImage(_device, _outputImage, null);
        if (_outputMemory.Handle != 0) _api.FreeMemory(_device, _outputMemory, null);
        _outputView = default;
        _outputImage = default;
        _outputMemory = default;
        _outputWidth = _outputHeight = 0;
        _outputIsShaderReadOnly = false;
    }

    /// <summary>
    /// 释放加速结构与几何资源
    /// </summary>
    private void DestroyAccelerationResources()
    {
        // TLAS 持有 BLAS 的设备地址，按依赖关系先释放上层。
        if (_tlas.Handle != 0) _accelerationApi.DestroyAccelerationStructure(_device, _tlas, null);
        if (_blas.Handle != 0) _accelerationApi.DestroyAccelerationStructure(_device, _blas, null);
        if (_dragBlas.Handle != 0) _accelerationApi.DestroyAccelerationStructure(_device, _dragBlas, null);
        DestroyBuffer(ref _tlasBuffer, ref _tlasMemory);
        DestroyBuffer(ref _blasBuffer, ref _blasMemory);
        DestroyBuffer(ref _dragBlasBuffer, ref _dragBlasMemory);
        DestroyBuffer(ref _vertexBuffer, ref _vertexMemory);
        DestroyBuffer(ref _indexBuffer, ref _indexMemory);
        DestroyBuffer(ref _dragVertexBuffer, ref _dragVertexMemory);
        DestroyBuffer(ref _dragIndexBuffer, ref _dragIndexMemory);
        DestroyBuffer(ref _cellBuffer, ref _cellMemory);
        DestroyBuffer(ref _dragCellBuffer, ref _dragCellMemory);
        DestroyBuffer(ref _instanceBuffer, ref _instanceMemory);
        _blas = default;
        _tlas = default;
        _dragBlas = default;
        _dragCellCount = 0;
        _dragGeometryVersion = -1;
        _dragInstanceVisible = false;
        _instanceCount = 0;
        _tlasScratchSize = 0;
        _sourceVersion = -1;
    }

    /// <summary>
    /// 释放管线资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context.IsDeviceLost)
        {
            _displayTextureId = IntPtr.Zero;
            return;
        }

        _api.DeviceWaitIdle(_device);
        if (_displayTextureId != 0) _imguiRenderer.UnregisterTexture(_displayTextureId);
        DestroyOutputImage();
        DestroyAccelerationResources();
        DestroyScratch();
        DestroyBuffer(ref _parameterBuffer, ref _parameterMemory);
        DestroyBuffer(ref _sbtBuffer, ref _sbtMemory);
        if (_pipeline.Handle != 0) _api.DestroyPipeline(_device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _api.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_descriptorPool.Handle != 0) _api.DestroyDescriptorPool(_device, _descriptorPool, null);
        if (_descriptorLayout.Handle != 0) _api.DestroyDescriptorSetLayout(_device, _descriptorLayout, null);
        _displayTextureId = 0;
    }

    /// <summary>
    /// 释放缓冲和对应内存
    /// </summary>
    /// <param name="buffer">缓冲</param>
    /// <param name="memory">内存</param>
    private void DestroyBuffer(ref VkBuffer buffer, ref DeviceMemory memory)
    {
        if (buffer.Handle != 0) _api.DestroyBuffer(_device, buffer, null);
        if (memory.Handle != 0) _api.FreeMemory(_device, memory, null);
        buffer = default;
        memory = default;
    }

    /// <summary>
    /// 按 Vulkan SBT 对齐规则向上取整
    /// </summary>
    /// <param name="value">原始数值</param>
    /// <param name="alignment">对齐值</param>
    /// <returns>对齐后的数值</returns>
    private static uint Align(uint value, uint alignment) => (value + alignment - 1U) / alignment * alignment;

    /// <summary>
    /// 创建 Vulkan ShaderModule
    /// </summary>
    /// <param name="words">SPIR-V 字节码</param>
    /// <returns>ShaderModule</returns>
    private ShaderModule CreateShaderModule(uint[] words)
    {
        fixed (uint* code = words)
        {
            ShaderModuleCreateInfo info = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)(words.Length * sizeof(uint)), PCode = code };
            ShaderModule module = default;
            Check(_api.CreateShaderModule(_device, in info, null, out module), "创建硬件 RT ShaderModule");
            return module;
        }
    }

    /// <summary>
    /// 检查 Vulkan 调用结果
    /// </summary>
    /// <param name="result">Vulkan 返回值</param>
    /// <param name="operation">操作名称</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{operation}失败：{result}");
    }

    private readonly record struct ShaderBindingTable(
        StridedDeviceAddressRegionKHR Raygen,
        StridedDeviceAddressRegionKHR Miss,
        StridedDeviceAddressRegionKHR Hit,
        StridedDeviceAddressRegionKHR Callable);

    private const string RaygenShader = """
        #version 460
        #extension GL_EXT_ray_tracing : require
        layout(set = 0, binding = 0) uniform accelerationStructureEXT topLevelAS;
        layout(set = 0, binding = 1, rgba8) uniform image2D outputImage;
        layout(set = 0, binding = 2, std430) readonly buffer Parameters { float p[]; };
        layout(location = 0) rayPayloadEXT vec4 payload;
        void main()
        {
            ivec2 pixel = ivec2(gl_LaunchIDEXT.xy);
            vec2 size = vec2(gl_LaunchSizeEXT.xy);
            vec2 uv = (vec2(pixel) + vec2(0.5)) / size * 2.0 - 1.0;
            vec3 origin = vec3(p[0], p[1], p[2]) - vec3(p[16], p[17], p[18]);
            float focal = max(p[3], 1.0);
            vec3 direction = normalize(vec3(p[4], p[5], p[6]) +
                vec3(p[8], p[9], p[10]) * uv.x * size.x / focal -
                vec3(p[12], p[13], p[14]) * uv.y * size.y / focal);
            payload = vec4(p[98], p[99], p[100], -1.0);
            traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xFF, 0, 0, 0, origin, p[96], direction, p[97], 0);
            // 硬件光追旁路只输出基色，但仍要应用与 Compute 路径同一套解析高度雾，
            // 避免切换渲染方式时雾突然消失。槽位与 VoxelRenderParameters 对应：
            // 48..50 雾色, 51 浓度, 152 起始距离, 153 天空混合, 154 最大不透明度,
            // 194 基准高度, 195 高度衰减, 196 各向异性, 197 太阳强度, 198..200 太阳色,
            // 201 反照率, 202 内散射亮度, 203 天空雾强度, 205 体积距离。
            if (p[51] > 0.0)
            {
                float sigma = p[51];
                float b = max(p[195], 0.0);
                vec3 sunDirection = normalize(vec3(p[24], p[25], p[26]));
                float t0 = clamp(p[152], 0.0, max(payload.a, 0.0));
                float t1 = payload.a >= 0.0
                    ? (p[205] > 0.0 ? min(payload.a, p[205]) : payload.a)
                    : (p[205] > 0.0 ? p[205] : min(4096.0, 3.0 / max(sigma, 0.00001)));
                float strength = payload.a >= 0.0 ? 1.0 : clamp(p[203], 0.0, 1.0);
                if (t1 > t0 && strength > 0.0)
                {
                    float segmentLength = t1 - t0;
                    float tau;
                    if (b <= 0.000001)
                    {
                        tau = sigma * segmentLength;
                    }
                    else
                    {
                        // 世界 Y = p[1] + direction.y * t（AS 中的体素坐标已减去区域最小角）
                        float h0 = (p[1] - p[194]) * b;
                        float k = b * direction.y;
                        float base = sigma * exp(-clamp(h0, -2.0, 32.0));
                        float x = -k * segmentLength;
                        float ratio = abs(x) < 0.01
                            ? 1.0 + x * 0.5 + x * x / 6.0
                            : (exp(clamp(x, -20.0, 20.0)) - 1.0) / x;
                        tau = max(0.0, base * segmentLength * ratio * exp(clamp(-k * t0, -20.0, 20.0)));
                    }
                    float amount = clamp((1.0 - exp(-tau)) * strength, 0.0, clamp(p[154], 0.0, 1.0));
                    vec3 fogColor = mix(vec3(p[98], p[99], p[100]), vec3(p[48], p[49], p[50]),
                        clamp(p[153], 0.0, 1.0)) * max(p[202], 0.0);
                    if (p[33] > 0.5 && p[197] > 0.0)
                    {
                        float g = clamp(p[196], -0.95, 0.95);
                        float denominator = max(1.0 + g * g - 2.0 * g * dot(direction, sunDirection), 0.0001);
                        float gain = min((1.0 - g * g) / (denominator * sqrt(denominator)), 8.0);
                        fogColor += vec3(p[198], p[199], p[200]) * vec3(p[28], p[29], p[30])
                            * (p[27] * p[197] * 0.15) * gain;
                    }
                    fogColor *= clamp(p[201], 0.0, 1.0);
                    payload = vec4(mix(payload.rgb, fogColor, amount), 1.0);
                }
            }
            imageStore(outputImage, pixel, payload);
        }
        """;

    private const string MissShader = """
        #version 460
        #extension GL_EXT_ray_tracing : require
        layout(set = 0, binding = 2, std430) readonly buffer Parameters { float p[]; };
        layout(location = 0) rayPayloadInEXT vec4 payload;
        // alpha < 0 表示未命中，由 raygen 按天空雾强度处理背景
        void main() { payload = vec4(p[98], p[99], p[100], -1.0); }
        """;

    private const string ClosestHitShader = """
        #version 460
        #extension GL_EXT_ray_tracing : require
        struct VoxelCell { ivec4 position; vec4 color; uvec4 metadata; };
        layout(set = 0, binding = 3, std430) readonly buffer Cells { VoxelCell cells[]; };
        layout(location = 0) rayPayloadInEXT vec4 payload;
        void main()
        {
            uint voxelIndex = gl_PrimitiveID / 12u;
            // alpha 携带命中距离，供 raygen 计算距离雾
            payload = vec4(cells[voxelIndex].color.rgb, gl_HitTEXT);
        }
        """;
}
