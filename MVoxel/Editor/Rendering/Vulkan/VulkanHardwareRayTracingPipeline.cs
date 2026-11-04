using System.Numerics;
using System.Runtime.InteropServices;
using MVoxel.Core;
using MVoxel.Rendering;
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
    internal const int MaxHardwareRayTracingVoxels = 250_000;
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

        if (workset.Cells.Count > MaxHardwareRayTracingVoxels)
        {
            _lastError = $"硬件 RT 体素数量超过安全上限 {MaxHardwareRayTracingVoxels:N0}，已回退 Vulkan Compute";
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
    internal bool EnsureShadowAcceleration(IReadOnlyList<VoxelGpuCell> cells, int sourceVersion)
    {
        if (_disposed || cells.Count == 0 || cells.Count > MaxHardwareRayTracingVoxels)
        {
            return false;
        }

        if (!FitsAccelerationLimits(cells.Count))
        {
            _lastError = "场景超过此设备的加速结构容量，已回退软件遍历";
            return false;
        }

        if (_sourceVersion == sourceVersion && _tlas.Handle != 0) return true;
        try
        {
            _lastError = null;
            // 几何顺序必须与 Compute 的 binding 0 完全一致，否则命中图元换算出的
            // 半透明标记会错位，阴影就会错误地穿过水面或玻璃
            Check(_api.DeviceWaitIdle(_device), "等待硬件阴影加速结构重建");
            DestroyAccelerationResources();
            CreateGeometryBuffers(cells, includeCellBuffer: false);
            CreateBlas(cells.Count);
            CreateTlas();
            _sourceVersion = sourceVersion;
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
    /// 创建体素三角形 BLAS
    /// </summary>
    /// <param name="voxelCount">体素数量</param>
    private void CreateBlas(int voxelCount)
    {
        var triangles = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(_vertexBuffer) },
            VertexStride = 3 * sizeof(float),
            MaxVertex = (uint)(voxelCount * VerticesPerVoxel - 1),
            IndexType = IndexType.Uint32,
            IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = GetAddress(_indexBuffer) },
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
        _blasBuffer = CreateDeviceBuffer(sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            out _blasMemory);
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = _blasBuffer,
            Size = sizes.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
        };
        Check(_accelerationApi.CreateAccelerationStructure(_device, in createInfo, null, out _blas), "创建体素 BLAS");
        CreateScratch(sizes.BuildScratchSize);
        info.DstAccelerationStructure = _blas;
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
    /// 创建包含一个 BLAS 实例的 TLAS
    /// </summary>
    private void CreateTlas()
    {
        _instanceBuffer = CreateHostBuffer(new byte[64],
            (BufferUsageFlags)0x00080000U | BufferUsageFlags.ShaderDeviceAddressBit,
            out _instanceMemory);
        Span<byte> instance = stackalloc byte[64];
        var transform = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        MemoryMarshal.AsBytes(transform.AsSpan()).CopyTo(instance);
        // VkAccelerationStructureInstanceKHR：第一个 DWORD 的高 8 位是 mask，
        // 第二个 DWORD 是 SBT 偏移（低 24 位）与实例标志（高 8 位）。
        // mask=0 会让所有使用 0xFF 的射线完全看不到该实例。
        MemoryMarshal.Write(instance[48..52], 0xFF000000U);
        MemoryMarshal.Write(instance[52..56], 0U);
        MemoryMarshal.Write(instance[56..64], GetAddress(_blas));
        UploadMapped(_instanceMemory, instance);

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
        var primitives = 1U;
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
        CreateScratch(sizes.BuildScratchSize);
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
            Check(_rayTracingApi.CreateRayTracingPipelines(_device, default, default, 1, in createInfo, null, out _pipeline), "创建 Vulkan 硬件 RT Pipeline");
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
        DestroyBuffer(ref _tlasBuffer, ref _tlasMemory);
        DestroyBuffer(ref _blasBuffer, ref _blasMemory);
        DestroyBuffer(ref _vertexBuffer, ref _vertexMemory);
        DestroyBuffer(ref _indexBuffer, ref _indexMemory);
        DestroyBuffer(ref _cellBuffer, ref _cellMemory);
        DestroyBuffer(ref _instanceBuffer, ref _instanceMemory);
        _blas = default;
        _tlas = default;
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
