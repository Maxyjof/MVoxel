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
internal sealed unsafe partial class VulkanVoxelRenderPipeline : IDisposable
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
    private VkBuffer _brickTableBuffer;
    private DeviceMemory _brickTableMemory;
    private ulong _brickTableCapacity;

    /// <summary>
    /// 当前工作集是否承载砖结构（cells = BrickCells，bvh = 区块粒度粗树）
    /// </summary>
    private bool _usingBrickPath;

    /// <summary>
    /// 砖驻留（可变常驻，跨同步保留以支持增量更新）
    /// </summary>
    private BrickResidency? _brickResidency;

    /// <summary>
    /// 最近一次砖路径同步是否走了增量（诊断用）
    /// </summary>
    internal bool LastSyncUsedIncrementalBrick { get; private set; }

    /// <summary>
    /// 最近一次砖增量被拒绝的原因（诊断用）
    /// </summary>
    internal string LastBrickRejectReason { get; private set; } = string.Empty;

    /// <summary>
    /// 单次增量允许的最大脏区块数，超出走全量重排
    /// </summary>
    private const int MaxIncrementalChunks = 64;

    /// <summary>
    /// 显式启用砖路径（渲染台架 `brick` 旗标）。
    /// 默认关闭：密集场景的砖内线性扫描尚待区块内 BVH 替换，
    /// 且砖格未做贪心合并，与传统路径存在噪声级像素差异
    /// </summary>
    internal static bool EnableBrickPath { get; set; }

    /// <summary>
    /// 强制走传统全量路径（渲染台架 A/B 对比用）
    /// </summary>
    internal static bool DisableBrickPath { get; set; }
    private DescriptorSetLayout _computeDescriptorLayout;
    private DescriptorPool _computeDescriptorPool;
    private PipelineLayout _computePipelineLayout;
    private Pipeline _computePipeline;
    private VkBuffer _materialTableBuffer;
    private DeviceMemory _materialTableMemory;
    private float[] _materialTable = MVoxel.Project.MaterialAsset.PackTable(new Dictionary<int, MVoxel.Project.MaterialAsset>());
    private int MaterialTableSlotCount => Math.Max(1, _materialTable.Length / MVoxel.Project.MaterialAsset.FloatsPerSlot);
    // 上传暂存缓冲持久复用：切换视图会触发工作集重传，
    // 每次同步都新建再销毁缓冲会让驱动分配器频繁抖动，也放大了驱动异常的概率
    private VkBuffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private ulong _stagingCapacity;
    private VkBuffer _environmentBuffer;
    private DeviceMemory _environmentMemory;
    private int _environmentWidth;
    private int _environmentHeight;
    private const int EnvironmentCapacity = 256 * 256;

    // 场景本地光源按固定容量打包成 Storage Buffer，每盏 4 个 vec4：
    // 位置与类型、方向与启用标记、颜色与强度、范围与聚光角度
    private VkBuffer _sceneLightBuffer;
    private DeviceMemory _sceneLightMemory;
    private int _sceneLightCount;
    private float[]? _sceneLightPacked;
    private const int SceneLightFloatsPerLight = 20;
    private const int SceneLightBufferBytes = SceneLight.Capacity * SceneLightFloatsPerLight * sizeof(float);

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
    private Image _guideImage;
    private DeviceMemory _guideMemory;
    private ImageView _guideView;
    private bool _guideInitialized;
    private nint _displayTextureId;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _outputIsShaderReadOnly;
    private bool _accumulationInitialized;
    private bool _clearAccumulationPending = true;
    private float[]? _lastParameters;
    private VoxelGpuWorkset _workset = VoxelGpuWorkset.Empty;
    private IReadOnlyList<VoxelGpuCell> _sceneOrderedCells = Array.Empty<VoxelGpuCell>();
    private VoxelCoord64 _sceneOrderedOrigin;
    private VoxelWorld? _sceneSelection;
    private int _sceneSelectionVersion = -1;
    private bool _sceneSelectionDirty = true;
    private bool _sceneSelectionActive;
    private bool _disposed;
    private bool _usingHardwareRayTracing;
    private int _hardwareLightFallbackCount = -1;
    private int _worksetMaximumCells;

    /// <summary>
    /// 当前工作集是否按贪心合并构建（场景视图 false），纳入同步跳过判定
    /// </summary>
    private bool _worksetGreedyMerge = true;
    private bool _worksetIncludesInterior;
    private VoxelWorld? _lastSyncSource;
    private VoxelLodSettings? _activeLod;
    private System.Numerics.Vector3? _lodCamera;
    private long _worksetLodSignature;

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
        _computePipeline.Handle != 0 && GpuWorksetUploaded && _bvhBuffer.Handle != 0;

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
            ? $"Vulkan GPU BVH 路径追踪已就绪，工作集 {_workset.Cells.Count:N0} 个体素，节点 {_bvhNodeCount:N0} 个"
            : $"已同步 {_workset.Cells.Count:N0} 个体素，等待 Vulkan Compute 资源初始化";

    /// <summary>
    /// 砖路径当前支持的最大源体素数
    /// </summary>
    internal const int MaxFullWorksetCells = 8_000_000;

    /// <summary>
    /// 同步稀疏体素世界到 Vulkan 工作集
    /// </summary>
    /// <param name="world">体素世界</param>
    /// <param name="maximumCells">显存预算格数，0 为不采样</param>
    /// <param name="lod">距离 LOD 配置</param>
    /// <param name="camera">LOD 判距相机</param>
    /// <param name="greedyMerge">是否贪心盒体合并：场景视图 false，渲染与导出 true</param>
    /// <param name="includeInterior">是否保留完全被包围的内部体素；仅用于相机进入模型内部的场景编辑预览</param>
    internal void SyncWorld(VoxelWorld world, int maximumCells = 0,
        VoxelLodSettings? lod = null, System.Numerics.Vector3? camera = null, bool greedyMerge = true,
        bool includeInterior = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        // 设备已经丢失时不再构建和上传工作集：故障设备上的每个原生调用都可能再次触发驱动异常
        if (_context?.IsDeviceLost == true)
        {
            return;
        }

        // 编辑类同步不带相机与 LOD：沿用上一次的活动配置，避免笔刷提交把 LOD 分档重置
        if (lod is not null)
        {
            _activeLod = lod;
        }

        if (camera.HasValue)
        {
            _lodCamera = camera;
        }

        var lodSignature = LodGroupSignature();
        // 引用一致 + 版本一致才跳过：渲染世界按图层遮罩重组后是新实例，
        // 必须靠引用差异触发重建，仅比版本号会让两次不同遮罩的组成撞车
        if (ReferenceEquals(_lastSyncSource, world) && world.ContentVersion == _workset.SourceVersion &&
            maximumCells == _worksetMaximumCells && lodSignature == _worksetLodSignature &&
            greedyMerge == _worksetGreedyMerge && includeInterior == _worksetIncludesInterior)
        {
            return;
        }

        // 砖路径资格：无采样预算、无 LOD、开启贪心合并（砖恒合并，场景视图不走砖）、硬件 RT 关闭、体素在预算内
        var brickEligible = EnableBrickPath && !DisableBrickPath && maximumCells == 0 && greedyMerge &&
            lodSignature == 0 && !_usingHardwareRayTracing && world.OccupiedVoxelCount <= MaxFullWorksetCells;
        if (brickEligible)
        {
            RefreshEmissiveCatalog(world, true);
            SyncBrick(world, lodSignature, maximumCells);
            UploadEmissiveFaces();
            return;
        }

        RefreshEmissiveCatalog(world, maximumCells == 0 && lodSignature == 0);
        _brickResidency = null;
        _usingBrickPath = false;
        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var next = VoxelGpuWorkset.Build(world, maximumCells, _activeLod, _lodCamera, greedyMerge, includeInterior);
        SyncWorksetBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
        SyncPackMs = 0;
        SyncUploadMs = 0;
        if (_context is not null)
        {
            UploadWorkset(next);
        }

        _workset = next;
        UploadEmissiveFaces();
        _lastSyncSource = world;
        _worksetMaximumCells = maximumCells;
        _worksetLodSignature = lodSignature;
        _worksetGreedyMerge = greedyMerge;
        _worksetIncludesInterior = includeInterior;
        SyncTotalCells = next.Cells.Count;
        ResetAccumulation();
    }

    /// <summary>
    /// LOD 分组指纹：相机按 16 格量化（跨区块界才变）与活动 LOD 配置的组合哈希，
    /// 未启用 LOD 时恒为 0，保证不启用时行为与旧版一致
    /// </summary>
    private long LodGroupSignature()
    {
        if (_activeLod is not { Enabled: true } || _lodCamera is not { } camera)
        {
            return 0;
        }

        var lod = _activeLod;
        lod.ClampAll();
        unchecked
        {
            var hash = (long)HashCameraAxis(camera.X) * 31 + HashCameraAxis(camera.Y);
            hash = hash * 31 + HashCameraAxis(camera.Z);
            hash = hash * 131 + lod.LevelCount;
            foreach (var distance in lod.Distances)
            {
                hash = hash * 131 + distance.GetHashCode();
            }

            return hash;
        }
    }

    private static int HashCameraAxis(float value) => (int)MathF.Floor(value / 16F);

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
        var stride = MVoxel.Project.MaterialAsset.FloatsPerSlot;
        if (table.Length == 0 || table.Length % stride != 0 || table.Length / stride > MVoxel.Project.MaterialAsset.TableSlots)
            throw new ArgumentException("材质槽表长度无效", nameof(table));
        var normalizedTable = table.Length == MVoxel.Project.MaterialAsset.TableSlots * stride
            ? table
            : PadMaterialTable(table, MVoxel.Project.MaterialAsset.TableSlots * stride);
        table = normalizedTable;
        if (_context is null || _api is not { } api || _materialTableBuffer.Handle == 0)
        {
            _materialTable = (float[])table.Clone();
            ResetAccumulation();
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

        var materialChanged = !_materialTable.AsSpan().SequenceEqual(table);
        _materialTable = (float[])table.Clone();
        if (materialChanged)
        {
            if (EmissiveFaceCount > 0) UploadEmissiveFaces();
            ResetAccumulation();
        }
    }

    /// <summary>
    /// 将动态材质表补齐到GPU固定容量，槽位数据本身仍按原生16位编号保存
    /// </summary>
    private static float[] PadMaterialTable(float[] source, int length)
    {
        var result = new float[length];
        Array.Fill(result, MVoxel.Project.MaterialAsset.FollowGlobal);
        source.AsSpan().CopyTo(result);
        return result;
    }

    /// <summary>
    /// 上传线性RGB环境经纬图到固定容量缓冲；宽高为0表示关闭环境贴图
    /// </summary>
    /// <param name="linearRgb">每像素3个float的线性RGB数据</param>
    /// <param name="width">经纬图宽度</param>
    /// <param name="height">经纬图高度</param>
    /// <param name="intensity">环境强度</param>
    internal void SetEnvironment(float[] linearRgb, int width, int height, float intensity = 1F)
    {
        ArgumentNullException.ThrowIfNull(linearRgb);
        if (width == 0 || height == 0)
        {
            _environmentWidth = 0;
            _environmentHeight = 0;
            return;
        }

        if (width * height > EnvironmentCapacity)
            throw new ArgumentException($"环境图 {width}×{height} 超出容量 {EnvironmentCapacity} 像素", nameof(width));
        if (linearRgb.Length < width * height * 3)
            throw new ArgumentException("环境图像素数据长度不足", nameof(linearRgb));
        if (_context is null || _api is not { } api || _environmentBuffer.Handle == 0)
        {
            _environmentWidth = width;
            _environmentHeight = height;
            return;
        }

        var packed = new float[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            packed[pixel * 4] = linearRgb[pixel * 3];
            packed[pixel * 4 + 1] = linearRgb[pixel * 3 + 1];
            packed[pixel * 4 + 2] = linearRgb[pixel * 3 + 2];
        }

        void* mapped = null;
        var byteCount = (ulong)(packed.Length * sizeof(float));
        Check(api.MapMemory(_device, _environmentMemory, 0, byteCount, 0, &mapped), "映射环境贴图缓冲");
        try
        {
            fixed (float* source = packed)
            {
                System.Buffer.MemoryCopy(source, mapped, (long)byteCount, (long)byteCount);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _environmentMemory);
        }

        _environmentWidth = width;
        _environmentHeight = height;
        _environmentIntensity = Math.Clamp(intensity, 0F, 16F);
        ResetAccumulation();
    }

    /// <summary>
    /// 上传场景本地光源，固定容量 Storage Buffer，只有启用且强度大于 0 的光源参与着色
    /// </summary>
    /// <param name="lights">场景光源集合，超出容量的部分忽略</param>
    /// <remarks>
    /// 当前限制：本地光源只投射硬阴影，没有面积光源因此不做软阴影；太阳软阴影逻辑保持不变。
    /// 数据未变化时直接返回，避免逐帧重置路径追踪累积
    /// </remarks>
    internal void SetSceneLights(IReadOnlyList<SceneLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var packed = new float[SceneLight.Capacity * SceneLightFloatsPerLight];
        var packedCount = 0;
        for (var index = 0; index < lights.Count && packedCount < SceneLight.Capacity; index++)
        {
            var light = lights[index].Clamped();
            if (!light.IsActive) continue;
            var offset = packedCount * SceneLightFloatsPerLight;
            packed[offset] = light.Position.X;
            packed[offset + 1] = light.Position.Y;
            packed[offset + 2] = light.Position.Z;
            packed[offset + 3] = (int)light.Kind;
            packed[offset + 4] = light.Direction.X;
            packed[offset + 5] = light.Direction.Y;
            packed[offset + 6] = light.Direction.Z;
            packed[offset + 7] = 1F;
            packed[offset + 8] = light.Color.X;
            packed[offset + 9] = light.Color.Y;
            packed[offset + 10] = light.Color.Z;
            packed[offset + 11] = light.Intensity;
            packed[offset + 12] = light.Range;
            packed[offset + 13] = light.SpotAngleDegrees;
            packed[offset + 14] = light.InnerSpotAngleDegrees;
            packed[offset + 15] = light.CastShadows ? 1F : 0F;
            packed[offset + 16] = Math.Clamp(light.Profile, 0, 3);
            packedCount++;
        }

        if (packedCount == _sceneLightCount &&
            PrefixEquals(_sceneLightPacked, packed, packedCount * SceneLightFloatsPerLight))
        {
            return;
        }

        _sceneLightCount = packedCount;
        _sceneLightPacked = packed;
        if (_context is not null && _api is { } api && _sceneLightBuffer.Handle != 0)
        {
            void* mapped = null;
            Check(api.MapMemory(_device, _sceneLightMemory, 0, (ulong)SceneLightBufferBytes, 0, &mapped),
                "映射场景光源缓冲");
            try
            {
                fixed (float* source = packed)
                {
                    System.Buffer.MemoryCopy(source, mapped, SceneLightBufferBytes, SceneLightBufferBytes);
                }
            }
            finally
            {
                api.UnmapMemory(_device, _sceneLightMemory);
            }
        }

        // 光源变化必须让路径追踪重新收敛，并让实时模式丢弃上一帧复用判定
        ResetAccumulation();
    }

    /// <summary>
    /// 比较缓存的光源打包数据前 count 项是否与本次一致
    /// </summary>
    /// <param name="cached">上一次打包的数据</param>
    /// <param name="current">本次打包的数据</param>
    /// <param name="count">需要比较的 float 个数</param>
    /// <returns>完全一致时返回 true</returns>
    private static bool PrefixEquals(float[]? cached, float[] current, int count)
    {
        if (cached is null || cached.Length < count) return false;
        for (var index = 0; index < count; index++)
        {
            if (cached[index] != current[index]) return false;
        }

        return true;
    }

    private float _environmentIntensity = 1F;

    /// <summary>
    /// 获取当前环境贴图是否启用
    /// </summary>
    internal bool HasEnvironment => _environmentWidth > 0 && _environmentHeight > 0;

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
    internal int LastDispatchCount { get; private set; }
    internal long LastWorkgroupCount { get; private set; }

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
        // 硬件 RT 目前只输出体素基础颜色，不执行太阳、自发光和场景光源着色；
        // 存在场景光源时改用 Compute 路径，保证本地光照与阴影可见，避免切到硬件 RT 后变暗
        bool emissiveLighting = settings.EmissiveVoxels && settings.EmissiveIntensity > 0F && _workset.EmissiveCount > 0;
        if (settings.UseHardwareRayTracing && (_sceneLightCount > 0 || emissiveLighting))
        {
            if (_hardwareLightFallbackCount != _sceneLightCount)
            {
                _hardwareLightFallbackCount = _sceneLightCount;
                MVoxelLog.Info($"本地光源 {_sceneLightCount}，发光照明 {emissiveLighting}；使用支持光照的 Compute 路径");
            }
        }
        else
        {
            _hardwareLightFallbackCount = -1;
        }

        if (settings.UseHardwareRayTracing && _sceneLightCount == 0 && !emissiveLighting && _context is not null && _imguiRenderer is not null &&
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
    /// 尝试把路径追踪 HDR 累积缓冲导出为 Radiance .hdr 文件。
    /// 从 rgba16f 累积图像读回线性浮点值，不做色调映射，保留全部动态范围
    /// </summary>
    /// <param name="path">输出 .hdr 文件路径</param>
    /// <returns>当前是否保存成功</returns>
    internal bool SaveDisplayHdr(string path)
    {
        if (_usingHardwareRayTracing || _context is null || _api is not { } api ||
            _accumulationImage.Handle == 0 || _outputWidth == 0 || _outputHeight == 0)
        {
            return false;
        }

        var bytesPerPixel = 8; // R16G16B16A16 = 4 × half = 8 bytes
        var byteCount = checked((ulong)_outputWidth * _outputHeight * (ulong)bytesPerPixel);
        var (stagingBuffer, stagingMemory) = CreateBuffer(
            byteCount,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        try
        {
            var pixels = new byte[checked((int)byteCount)];
            _context.ExecuteImmediate(commandBuffer =>
            {
                // 累积图像常驻 General 布局，只需访问掩码屏障
                ImageMemoryBarrier toTransfer = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _accumulationImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit
                };
                api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in toTransfer);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageOffset = default,
                    ImageExtent = new Extent3D((uint)_outputWidth, (uint)_outputHeight, 1)
                };
                api.CmdCopyImageToBuffer(commandBuffer, _accumulationImage, ImageLayout.General,
                    stagingBuffer, 1, in copy);

                ImageMemoryBarrier backToShader = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _accumulationImage,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderWriteBit
                };
                api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, in backToShader);
            });

            void* mapped = null;
            Check(api.MapMemory(_device, stagingMemory, 0, byteCount, 0, &mapped), "映射 HDR 导出暂存缓冲");
            try
            {
                Marshal.Copy((nint)mapped, pixels, 0, pixels.Length);
            }
            finally
            {
                api.UnmapMemory(_device, stagingMemory);
            }

            // rgba16f → float RGB → RGBE
            var rgbe = new byte[_outputWidth * _outputHeight * 4];
            var halfSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
            for (var pixelIndex = 0; pixelIndex < _outputWidth * _outputHeight; pixelIndex++)
            {
                var r = (float)halfSpan[pixelIndex * 4];
                var g = (float)halfSpan[pixelIndex * 4 + 1];
                var b = (float)halfSpan[pixelIndex * 4 + 2];
                var encoded = RadianceImage.EncodeRgbe([r, g, b]);
                for (var channel = 0; channel < 4; channel++)
                    rgbe[pixelIndex * 4 + channel] = encoded[channel];
            }

            AtomicFile.WriteBytes(path, RadianceImage.ComposeRadiance((int)_outputWidth, (int)_outputHeight, rgbe));
            return true;
        }
        catch (Exception exception)
        {
            MVoxelLog.Error("导出 Vulkan HDR 图像失败", exception);
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
                // 只清理纯托管的句柄记录，不再调用故障设备上的描述符释放
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

            if (_environmentBuffer.Handle != 0)
            {
                api.DestroyBuffer(_device, _environmentBuffer, null);
                api.FreeMemory(_device, _environmentMemory, null);
            }

            if (_sceneLightBuffer.Handle != 0)
            {
                api.DestroyBuffer(_device, _sceneLightBuffer, null);
                api.FreeMemory(_device, _sceneLightMemory, null);
            }

            if (_stagingBuffer.Handle != 0)
            {
                api.DestroyBuffer(_device, _stagingBuffer, null);
                api.FreeMemory(_device, _stagingMemory, null);
                _stagingBuffer = default;
                _stagingMemory = default;
                _stagingCapacity = 0;
            }

            DestroyBvhBuffer(api);
            DestroyVoxelBuffer(api);
            DestroyBrickTableBuffer(api);
            DestroyEmissiveBuffer();
        }
    }

    /// <summary>
    /// 创建计算着色器、参数缓冲和描述符资源
    /// </summary>
    private void InitializeComputeResources()
    {
        try
        {
            ValidateComputeImageFormats();
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
                 SetMaterialTable(MVoxel.Project.MaterialAsset.PackTable(new Dictionary<int, MVoxel.Project.MaterialAsset>()));
                (_environmentBuffer, _environmentMemory) = CreateBuffer(
                    (ulong)(EnvironmentCapacity * 4 * sizeof(float)),
                    BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                // 固定容量：光源数量变化只改参数槽，不需要重建缓冲
                (_sceneLightBuffer, _sceneLightMemory) = CreateBuffer(
                    (ulong)SceneLightBufferBytes,
                    BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
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

    /// <summary>检查当前 Vulkan 设备的存储图像格式能力，不按显卡厂商推断支持情况。</summary>
    private void ValidateComputeImageFormats()
    {
        if (_context is null || _api is null) return;
        foreach (var (format, required) in new[]
        {
            (Format.R8G8B8A8Unorm, FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit),
            (Format.R16G16B16A16Sfloat, FormatFeatureFlags.StorageImageBit),
            (Format.R32G32B32A32Sfloat, FormatFeatureFlags.StorageImageBit),
        })
        {
            _api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, out var properties);
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new InvalidOperationException($"Vulkan 设备 {_context.DeviceName} 不支持所需图像格式 {format}（需要 {required}）");
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
        LastDispatchCount = 0;
        LastWorkgroupCount = 0;
        if (!IsAvailable || _context is null || _api is not { } api || widthPixels <= 0 || heightPixels <= 0)
        {
            return false;
        }

        EnsureOutputImage((uint)widthPixels, (uint)heightPixels);
        var parameters = VoxelRenderParameters.Build(settings, frameState, frameState.FrameIndex);
        parameters[VoxelRenderParameters.VoxelCount] = _workset.Cells.Count;
        parameters[VoxelRenderParameters.BvhNodeCount] = _bvhNodeCount;
        parameters[VoxelRenderParameters.BrickMode] = _usingBrickPath ? 1F : 0F;
        parameters[VoxelRenderParameters.EnvEnabled] = HasEnvironment ? 1F : 0F;
        parameters[VoxelRenderParameters.EnvWidth] = _environmentWidth;
        parameters[VoxelRenderParameters.EnvHeight] = _environmentHeight;
        parameters[VoxelRenderParameters.EnvIntensity] = _environmentIntensity;
        parameters[VoxelRenderParameters.SceneLightCount] = _sceneLightCount;
        bool selectionActive = !pathMode && frameState.ScenePreview && _sceneSelectionActive;
        parameters[VoxelRenderParameters.Reserved2] = selectionActive ? 1F : 0F;
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
        bool realtimePost = !pathMode && !frameState.ShowOutlines;
        bool bloomActive = settings.BloomEnabled && settings.BloomIntensity > 0F &&
                           (pathMode || realtimePost);
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
                if (pathMode || bloomActive || selectionActive)
                {
                    TransitionAccumulationToGeneral(commandBuffer);
                    if (pathMode || selectionActive) TransitionGuideToGeneral(commandBuffer);
                }

                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _computePipeline);
                var descriptorSet = _ringSets[slot];
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
                    0, 1, in descriptorSet, 0, null);
                api.CmdDispatch(commandBuffer, ((uint)tileWidth + 7U) / 8U,
                    ((uint)tileHeight + 7U) / 8U, 1);
                LastDispatchCount++;
                LastWorkgroupCount += ((tileWidth + 7L) / 8L) * ((tileHeight + 7L) / 8L);
                TransitionOutputToShaderRead(commandBuffer);
                CommitRingSlot(slot);
            }
        }

        // 收尾通道必须在整帧采样完成后执行。路径追踪先降噪再参与 Bloom；
        // 实时模式也可以使用 Bloom，但不执行路径专用降噪。
        bool denoiseActive = pathMode && settings.PathDenoising && settings.PathDenoiseStrength > 0F;
        if (bloomActive || denoiseActive || selectionActive)
        {
            void DispatchFinalPass(int modeSlot, float modeValue)
            {
                parameters[VoxelRenderParameters.TileOffsetX] = 0F;
                parameters[VoxelRenderParameters.TileOffsetY] = 0F;
                parameters[modeSlot] = modeValue;
                var slot = AcquireRingSlot();
                UploadParameters(slot, parameters);
                var commandBuffer = _ringCommands[slot];
                TransitionOutputToGeneral(commandBuffer);
                if (pathMode || bloomActive || selectionActive)
                {
                    // 收尾通道读取整张中间图，等待所有分块写入。
                    TransitionAccumulationToGeneral(commandBuffer);
                    if (pathMode || selectionActive) TransitionGuideToGeneral(commandBuffer);
                }
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _computePipeline);
                var descriptorSet = _ringSets[slot];
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
                    0, 1, in descriptorSet, 0, null);
                api.CmdDispatch(commandBuffer, ((uint)widthPixels + 7U) / 8U,
                    ((uint)heightPixels + 7U) / 8U, 1);
                LastDispatchCount++;
                LastWorkgroupCount += ((widthPixels + 7L) / 8L) * ((heightPixels + 7L) / 8L);
                TransitionOutputToShaderRead(commandBuffer);
                CommitRingSlot(slot);
                parameters[modeSlot] = 0F;
            }

            if (selectionActive)
            {
                DispatchFinalPass(VoxelRenderParameters.Reserved2, 2F);
                parameters[VoxelRenderParameters.Reserved2] = 1F;
            }
            else if (bloomActive)
            {
                DispatchFinalPass(VoxelRenderParameters.BloomPass, 1F);
            }
            else if (denoiseActive)
            {
                DispatchFinalPass(VoxelRenderParameters.DenoisePass, 1F);
            }
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
            var guideCreateInfo = accumulationCreateInfo;
            guideCreateInfo.Format = Format.R32G32B32A32Sfloat;
            Check(api.CreateImage(_device, &guideCreateInfo, null, out _guideImage), "创建降噪辅助图像");
            MemoryRequirements guideRequirements = default;
            api.GetImageMemoryRequirements(_device, _guideImage, &guideRequirements);
            _guideMemory = AllocateMemory(guideRequirements, MemoryPropertyFlags.DeviceLocalBit);
            Check(api.BindImageMemory(_device, _guideImage, _guideMemory, 0), "绑定降噪辅助图像");
            _guideView = CreateImageView(_guideImage, Format.R32G32B32A32Sfloat);
            _guideInitialized = false;
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
    /// 把累积图像转换为 Compute 可读写布局，并建立分块之间的写后读屏障。
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    private void TransitionAccumulationToGeneral(CommandBuffer commandBuffer)
    {
        var api = _api!;
        var initialized = _accumulationInitialized;
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = initialized ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _accumulationImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = initialized
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit
                : 0,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        };
        api.CmdPipelineBarrier(commandBuffer,
            initialized ? PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, in barrier);
        _accumulationInitialized = true;
    }

    private void TransitionGuideToGeneral(CommandBuffer commandBuffer)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _guideInitialized ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _guideImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = _guideInitialized ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : 0,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        };
        _api!.CmdPipelineBarrier(commandBuffer, _guideInitialized ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 1, in barrier);
        _guideInitialized = true;
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
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[11];
        bindings[0] = new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[2] = new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[3] = new(3, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[4] = new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[5] = new(5, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[6] = new(6, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[7] = new(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[8] = new(8, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[9] = new(9, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[10] = new(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        DescriptorSetLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 11,
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
            new(DescriptorType.StorageBuffer, 8 * TileRingDepth),
            new(DescriptorType.StorageImage, 3 * TileRingDepth)
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
        if (_context?.IsDeviceLost == true) return;
        if (_voxelBuffer.Handle == 0 || _bvhBuffer.Handle == 0 ||
            _outputView.Handle == 0 || _accumulationView.Handle == 0 || _guideView.Handle == 0) return;
        // 砖表绑定在布局中恒存在（着色器静态引用），即使传统路径也要绑定最小缓冲
        EnsureBrickTableBuffer(1);
        EnsureEmissiveBuffer(16);
        DescriptorBufferInfo voxelInfo = new(_voxelBuffer, 0, _voxelBufferCapacity);
        DescriptorBufferInfo bvhInfo = new(_bvhBuffer, 0, _bvhBufferCapacity);
        DescriptorImageInfo imageInfo = new(default, _outputView, ImageLayout.General);
        DescriptorImageInfo accumulationInfo = new(default, _accumulationView, ImageLayout.General);
        DescriptorImageInfo guideInfo = new(default, _guideView, ImageLayout.General);
        // 写描述符数组在栈上分配一次，循环内复用，避免 stackalloc-in-loop 的栈溢出隐患
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[11];
        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            if (_ringSets[slot].Handle == 0) return;
            DescriptorBufferInfo parameterInfo = new(_ringParameterBuffers[slot], 0,
                (ulong)(VoxelRenderParameters.SlotCount * sizeof(float)));
            DescriptorBufferInfo materialTableInfo = new(_materialTableBuffer, 0,
                (ulong)(MVoxel.Project.MaterialAsset.TableSlots * MVoxel.Project.MaterialAsset.FloatsPerSlot * sizeof(float)));
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
            DescriptorBufferInfo environmentInfo = new(_environmentBuffer, 0,
                (ulong)(EnvironmentCapacity * 4 * sizeof(float)));
            writes[6] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 6,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &environmentInfo
            };
            DescriptorBufferInfo brickTableInfo = new(_brickTableBuffer, 0, Math.Max(_brickTableCapacity, 512));
            writes[7] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 7,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &brickTableInfo
            };
            writes[8] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _ringSets[slot],
                DstBinding = 8, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &guideInfo };
            // 本地光源缓冲容量固定，沿用同一个描述符信息即可
            DescriptorBufferInfo sceneLightInfo = new(_sceneLightBuffer, 0, (ulong)SceneLightBufferBytes);
            writes[9] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 9,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &sceneLightInfo
            };
            DescriptorBufferInfo emissiveInfo = new(_emissiveBuffer, 0, _emissiveCapacity);
            writes[10] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _ringSets[slot],
                DstBinding = 10, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &emissiveInfo };
            _api!.UpdateDescriptorSets(_device, 11, writes, 0, null);
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
        if (_guideView.Handle != 0) api.DestroyImageView(_device, _guideView, null);
        if (_guideImage.Handle != 0) api.DestroyImage(_device, _guideImage, null);
        if (_guideMemory.Handle != 0) api.FreeMemory(_device, _guideMemory, null);
        _guideView = default;
        _guideImage = default;
        _guideMemory = default;
        _guideInitialized = false;
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

    internal static string ComputeShaderSourceForTests => SceneComputeShader;

    // #version 必须是第一条有效指令，因此参数槽位与光源容量的 #define 放在版本行之后注入，
    // GLSL 侧只读 P_ 开头的宏，不再硬编码新增槽位号
    private static readonly string SceneComputeShader =
        "#version 450\n\n" + VoxelRenderParameters.Defines() + "\n" +
        SceneShaderBody.Replace("/* EMISSIVE_SAMPLING */", EmissiveSamplingShader);

    private const string SceneShaderBody = """
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
        layout(set = 0, binding = 8, rgba32f) uniform coherent image2D guideImage;

        struct MaterialSlotData
        {
            vec4 scalars;
            vec4 absorption;
            // x 折射率，y 镜面强度，z 不透明度覆盖，w 体积散射强度
            vec4 optics;
            // rgb 散射色（r < 0 表示跟随体素），w 额外雾化密度
            vec4 haze;
        };

        layout(set = 0, binding = 5, std430) readonly buffer MaterialTable
        {
            MaterialSlotData materialSlots[];
        };

        layout(set = 0, binding = 6, std430) readonly buffer EnvironmentMap
        {
            vec4 envTexels[];
        };

        struct SceneLightData
        {
            // xyz 为位置，w 为类型（0 方向光，1 点光，2 聚光）
            vec4 positionKind;
            // xyz 为光线传播方向，w 为启用标记（1 参与着色）
            vec4 directionEnabled;
            // rgb 为线性颜色，w 为强度
            vec4 colorIntensity;
            // x 为范围，y 为外角（度），z 为内角（度），w 为是否投射阴影
            vec4 rangeSpot;
            // x 为配光曲线预设（0 默认，1 窄束，2 宽束，3 蝴蝶结）
            vec4 profileExtra;
        };

        layout(set = 0, binding = 9, std430) readonly buffer SceneLightBuffer
        {
            SceneLightData sceneLights[];
        };

        bool IntersectBox(vec3 rayOrigin, vec3 rayDirection, vec3 boxMin, vec3 boxMax,
            out float distance, out vec3 normal)
        {
            // 方向分量为 0 时 1/0=±Inf 会在“起点恰与该轴平面共面”时算出 0*Inf=NaN，
            // 污染全部 max/min 比较，命中判定逐像素随机翻转（探针与主光线都可能轴向对齐）。
            // 用钳最小绝对值的倒数：轴向分量 0 时平面距离 ±1e8，比较仍正确，
            // 只有起点恰在平面上才可能 NaN；边界起点在下面单独按射线方向处理。
            vec3 safeDirection = mix(rayDirection, vec3(1e-8), equal(rayDirection, vec3(0.0)));
            vec3 inverseDirection = 1.0 / safeDirection;
            vec3 nearPoint = (boxMin - rayOrigin) * inverseDirection;
            vec3 farPoint = (boxMax - rayOrigin) * inverseDirection;
            vec3 lower = min(nearPoint, farPoint);
            vec3 upper = max(nearPoint, farPoint);
            if (!(max(max(lower.x, lower.y), lower.z) < 1e7)) return false;
            if (!(min(min(upper.x, upper.y), upper.z) > -1e7)) return false;
            float nearDistance = max(max(lower.x, lower.y), lower.z);
            float farDistance = min(min(upper.x, upper.y), upper.z);
            if (farDistance < max(nearDistance, 0.0)) return false;

            // 相机或上一段射线经常会落在两个相邻体素共用的平面上。
            // 旧逻辑用 nearDistance <= 0 判断“盒内”，会把共面且向盒内的射线
            // 当成从内部出发，直接取 farDistance，近侧面就被跳过，贴近模型时
            // 表面遮挡会出现翻转。边界上按射线方向决定当前盒是否是入射盒：
            // 离开当前面的盒拒绝，进入当前面的盒从 t=0 命中；只有严格在盒内
            // 才取出口面。
            // 推进距离沿射线计算；各轴的边界容差也必须随方向分量缩放，
            // 否则掠射时已经进入相邻盒的起点仍被误认作共面，形成假界面。
            vec3 boundaryTolerance = max(abs(rayDirection) * 0.00001, vec3(0.0000001));
            bool onMinX = abs(rayOrigin.x - boxMin.x) <= boundaryTolerance.x;
            bool onMaxX = abs(rayOrigin.x - boxMax.x) <= boundaryTolerance.x;
            bool onMinY = abs(rayOrigin.y - boxMin.y) <= boundaryTolerance.y;
            bool onMaxY = abs(rayOrigin.y - boxMax.y) <= boundaryTolerance.y;
            bool onMinZ = abs(rayOrigin.z - boxMin.z) <= boundaryTolerance.z;
            bool onMaxZ = abs(rayOrigin.z - boxMax.z) <= boundaryTolerance.z;
            if ((onMinX && rayDirection.x < 0.0) || (onMaxX && rayDirection.x > 0.0) ||
                (onMinY && rayDirection.y < 0.0) || (onMaxY && rayDirection.y > 0.0) ||
                (onMinZ && rayDirection.z < 0.0) || (onMaxZ && rayDirection.z > 0.0))
                return false;

            // 平行于盒面的方向允许起点恰在该面上；否则从介质内沿网格面行进时，
            // 交点会被误判成零距离入射，导致出口法线错误和透明物体内部出现暗条。
            bool insideX = abs(rayDirection.x) < 1e-8 ||
                (rayOrigin.x > boxMin.x + boundaryTolerance.x && rayOrigin.x < boxMax.x - boundaryTolerance.x);
            bool insideY = abs(rayDirection.y) < 1e-8 ||
                (rayOrigin.y > boxMin.y + boundaryTolerance.y && rayOrigin.y < boxMax.y - boundaryTolerance.y);
            bool insideZ = abs(rayDirection.z) < 1e-8 ||
                (rayOrigin.z > boxMin.z + boundaryTolerance.z && rayOrigin.z < boxMax.z - boundaryTolerance.z);
            bool fromInside = insideX && insideY && insideZ;
            distance = fromInside ? farDistance : max(nearDistance, 0.0);
            // 法线必须按参数距离选轴，绝不能用“命中点到六面的欧氏距离挑最小”：
            // 命中点由 distance 重建、自带 fp 误差，共面像素到入射面的距离并不严格为 0，
            // 亚体素相位靠近体素边界时顶面与侧面的距离比较随机翻转，法线在 +Y 与 ±X/±Z
            // 之间按 1 体素周期跳变，菲涅尔从 0.04 跳到 1.0，水面出现规则点阵棋盘。
            // 参数距离没有重建误差：正面命中取入口轴（lower 最大者，法线逆着射线），
            // 起点在盒内取出口轴（upper 最小者，法线顺着射线）
            int normalAxis = 0;
            if (fromInside)
            {
                if (upper.y < upper[normalAxis]) normalAxis = 1;
                if (upper.z < upper[normalAxis]) normalAxis = 2;
            }
            else
            {
                if (lower.y > lower[normalAxis]) normalAxis = 1;
                if (lower.z > lower[normalAxis]) normalAxis = 2;
            }
            vec3 axisVector = normalAxis == 0 ? vec3(1.0, 0.0, 0.0)
                : (normalAxis == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0));
            float axisOutward = fromInside ? sign(rayDirection[normalAxis])
                : -sign(rayDirection[normalAxis]);
            normal = axisVector * axisOutward;
            return true;
        }

        bool IntersectBounds(vec3 rayOrigin, vec3 rayDirection, BvhNode node, out float distance)
        {
            // BVH 返回区间的下界，不能复用实体表面求交的出口距离。
            // 起点在节点内时下界必须为零，否则已有远处命中会错误剔除近处几何。
            vec3 origin = vec3(values[16], values[17], values[18]);
            vec3 boxMin = vec3(node.minimum.xyz) + origin;
            vec3 boxMax = vec3(node.maximum.xyz) + origin;
            float entry = 0.0;
            float exitDistance = 3.402823e+38;
            for (int axis = 0; axis < 3; axis++)
            {
                if (rayDirection[axis] == 0.0)
                {
                    if (rayOrigin[axis] < boxMin[axis] || rayOrigin[axis] > boxMax[axis])
                        return false;
                    continue;
                }
                float a = (boxMin[axis] - rayOrigin[axis]) / rayDirection[axis];
                float b = (boxMax[axis] - rayOrigin[axis]) / rayDirection[axis];
                entry = max(entry, min(a, b));
                exitDistance = min(exitDistance, max(a, b));
                if (exitDistance < entry) return false;
            }
            distance = entry;
            return true;
        }

        bool TraceClosestWithin(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot,
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent, float maxDistance, bool anyHit)
        {
            ivec2 stack[64];
            int stackSize = 1;
            stack[0] = ivec2(0, floatBitsToInt(0.0));
            closestDistance = maxDistance;
            hitNormal = vec3(0.0, 1.0, 0.0);
            hitColor = vec4(0.0);
            hitFlags = 0u;
            hitSlot = 0u;
            hitCell = ivec3(0);
            hitExtent = ivec3(1);
            int voxelCount = int(values[94]);
            int nodeCount = int(values[95]);
            vec3 regionOrigin = vec3(values[16], values[17], values[18]);
            while (stackSize > 0)
            {
                ivec2 pendingNode = stack[--stackSize];
                int nodeIndex = pendingNode.x;
                if (nodeIndex < 0 || nodeIndex >= nodeCount) continue;
                if (intBitsToFloat(pendingNode.y) > closestDistance) continue;
                BvhNode node = nodes[nodeIndex];
                if (node.links.x < 0)
                {
                    int endIndex = min(node.links.z + node.links.w, voxelCount);
                    for (int index = node.links.z; index < endIndex; index++)
                    {
                        VoxelCell cell = cells[index];
                        // position.w 打包盒体三维格数（各 8 位，0 兼容为 1），
                        // 贪心合并与 LOD 大块都以一个盒图元表达
                        ivec3 packedExtent = ivec3(
                            max(cell.position.w & 0xFF, 1),
                            max((cell.position.w >> 8) & 0xFF, 1),
                            max((cell.position.w >> 16) & 0xFF, 1));
                        vec3 voxelMin = regionOrigin + vec3(cell.position.xyz);
                        vec3 voxelMax = voxelMin + vec3(packedExtent);
                        float distance;
                        vec3 normal;
                        if (IntersectBox(rayOrigin, rayDirection, voxelMin, voxelMax, distance, normal) &&
                            distance < closestDistance &&
                            !(ignoreTranslucent && (cell.metadata.x >> 16 & 1u) != 0u))
                        {
                            closestDistance = distance;
                            hitNormal = normal;
                            hitColor = cell.color;
                            hitFlags = (cell.metadata.x >> 16) | (cell.metadata.y != 0u ? 256u : 0u);
                            hitSlot = cell.metadata.x & 0xFFFFu;
                            hitCell = cell.position.xyz;
                            hitExtent = packedExtent;
                            if (anyHit && hitColor.a > 0.0) return true;
                        }
                    }
                }
                else
                {
                    if (stackSize + 2 <= 64)
                    {
                        float leftDistance;
                        float rightDistance;
                        bool leftHit = IntersectBounds(rayOrigin, rayDirection, nodes[node.links.x], leftDistance) && leftDistance <= closestDistance;
                        bool rightHit = IntersectBounds(rayOrigin, rayDirection, nodes[node.links.y], rightDistance) && rightDistance <= closestDistance;
                        //先访问近节点，让最近命中尽早排除远节点
                        if (leftHit && rightHit)
                        {
                            stack[stackSize++] = leftDistance < rightDistance
                                ? ivec2(node.links.y, floatBitsToInt(rightDistance))
                                : ivec2(node.links.x, floatBitsToInt(leftDistance));
                            stack[stackSize++] = leftDistance < rightDistance
                                ? ivec2(node.links.x, floatBitsToInt(leftDistance))
                                : ivec2(node.links.y, floatBitsToInt(rightDistance));
                        }
                        else if (leftHit) stack[stackSize++] = ivec2(node.links.x, floatBitsToInt(leftDistance));
                        else if (rightHit) stack[stackSize++] = ivec2(node.links.y, floatBitsToInt(rightDistance));
                    }
                }
            }
            return hitColor.a > 0.0;
        }

        bool TraceClosest(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot,
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent)
        {
            return TraceClosestWithin(rayOrigin, rayDirection, closestDistance, hitNormal,
                hitColor, hitFlags, hitSlot, hitCell, hitExtent, ignoreTranslucent, 3.402823e+38, false);
        }

        bool TraceShadow(vec3 rayOrigin, vec3 rayDirection, float maxDistance)
        {
            float distance;
            vec3 normal;
            vec4 color;
            uint flags;
            uint slot;
            ivec3 cell;
            ivec3 cellExtent;
            return TraceClosestWithin(rayOrigin, rayDirection, distance, normal, color, flags,
                slot, cell, cellExtent, true, maxDistance, true);
        }

        float Random(inout uint state);

        float TraceSoftShadow(vec3 rayOrigin, vec3 rayDirection, float maxDistance, inout uint state)
        {
            int sampleCount = clamp(int(values[32]), 1, 8);
            if (values[31] <= 0.0001 || sampleCount == 1)
                return TraceShadow(rayOrigin, rayDirection, maxDistance) ? 0.0 : 1.0;
            vec3 tangent = normalize(abs(rayDirection.y) < 0.9 ? cross(rayDirection, vec3(0.0, 1.0, 0.0))
                : cross(rayDirection, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(rayDirection, tangent);
            float visible = 0.0;
            for (int index = 0; index < sampleCount; index++)
            {
                float angle = 6.28318530718 * (float(index) + Random(state)) / float(sampleCount);
                float radius = values[31] * sqrt(Random(state));
                vec3 sampleDirection = normalize(rayDirection + tangent * cos(angle) * radius
                    + bitangent * sin(angle) * radius);
                visible += TraceShadow(rayOrigin, sampleDirection, maxDistance) ? 0.0 : 1.0;
            }
            // 面光源的半影是可见方向的比例；“任一采样被挡就全黑”会把软阴影
            // 系统性加深成块状脏色，并且采样数越高偏差越严重。
            return visible / float(sampleCount);
        }

        float AmbientOcclusion(vec3 hitPoint, vec3 normal, inout uint state)
        {
            if (values[59] <= 0.5) return 1.0;
            vec3 tangent = normalize(abs(normal.y) < 0.9 ? cross(normal, vec3(0.0, 1.0, 0.0))
                : cross(normal, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(normal, tangent);
            float blocked = 0.0;
            int sampleCount = clamp(int(values[62]), 4, 64);
            const float radius = 1.75;
            // 全屏共用的 8 个二值方向会把遮挡体轮廓复制成三角形/条带。
            // 余弦半球积分 + 像素固定旋转消除这种空间相关性；距离连续衰减，
            // 不再在探测半径边界突然从全遮挡跳到无遮挡。种子不含实时帧号。
            float rotation = 6.28318530718 * Random(state);
            for (int index = 0; index < sampleCount; index++)
            {
                float fraction = (float(index) + 0.5) / float(sampleCount);
                float angle = rotation + float(index) * 2.39996323;
                vec3 direction = normal * sqrt(1.0 - fraction)
                    + (tangent * cos(angle) + bitangent * sin(angle)) * sqrt(fraction);
                float distance;
                vec3 hitNormal;
                vec4 hitColor;
                uint hitFlags;
                uint hitSlot;
                ivec3 hitCell;
                ivec3 hitExtent;
                if (TraceClosestWithin(hitPoint + normal * 0.003, direction, distance,
                    hitNormal, hitColor, hitFlags, hitSlot, hitCell, hitExtent, true, radius, false))
                    blocked += 1.0 - smoothstep(0.0, radius, distance);
            }
            float visibility = 1.0 - blocked / float(sampleCount);
            return pow(clamp(visibility, 0.0, 1.0), max(values[61], 0.05))
                * values[60] + (1.0 - values[60]);
        }

        float TraceSoftShadowVisibility(vec3 rayOrigin, vec3 rayDirection, float maxDistance)
        {
            // 配置值就是每像素的阴影射线数，默认值偏向实时性能。
            int sampleCount = clamp(int(values[32]), 1, 8);
            if (values[31] <= 0.0001 || sampleCount == 1)
                return TraceShadow(rayOrigin, rayDirection, maxDistance) ? 0.0 : 1.0;

            vec3 tangent = normalize(abs(rayDirection.y) < 0.9
                ? cross(rayDirection, vec3(0.0, 1.0, 0.0))
                : cross(rayDirection, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = cross(rayDirection, tangent);
            float visible = 0.0;
            for (int index = 0; index < 8; index++)
            {
                if (index >= sampleCount) break;
                // 圆盘内固定采样，实时阴影稳定且不会出现随机颗粒。
                float fraction = (float(index) + 0.5) / float(sampleCount);
                float angle = 6.28318530718 * fraction;
                float radius = values[31] * sqrt(fraction);
                vec3 sampleDirection = normalize(rayDirection + tangent * cos(angle) * radius
                    + bitangent * sin(angle) * radius);
                visible += TraceShadow(rayOrigin, sampleDirection, maxDistance) ? 0.0 : 1.0;
            }
            return visible / float(sampleCount);
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

        // 面板中的天空颜色按 sRGB 显示值保存，参与光照前必须转为线性空间；
        // 环境贴图由 SetEnvironment 明确按线性 RGB 上传，因此不经过此转换。
        vec3 SrgbToLinear(vec3 color)
        {
            vec3 low = color / 12.92;
            vec3 high = pow((color + 0.055) / 1.055, vec3(2.4));
            return mix(low, high, step(vec3(0.04045), color));
        }

        vec3 Sky(vec3 rayDirection)
        {
            if (values[104] > 0.5)
            {
                int envWidth = int(values[105]);
                int envHeight = int(values[106]);
                float u = atan(rayDirection.x, -rayDirection.z) / 6.28318530718 + 0.5;
                float v = acos(clamp(rayDirection.y, -1.0, 1.0)) / 3.14159265359;
                float fx = u * float(envWidth) - 0.5;
                float fy = v * float(envHeight) - 0.5;
                int x0 = int(floor(fx));
                int y0 = int(floor(fy));
                vec2 blend = vec2(fx - float(x0), fy - float(y0));
                int x0w = ((x0 % envWidth) + envWidth) % envWidth;
                int x1w = ((x0 + 1) % envWidth + envWidth) % envWidth;
                int y0c = clamp(y0, 0, envHeight - 1);
                int y1c = clamp(y0 + 1, 0, envHeight - 1);
                vec3 c00 = envTexels[y0c * envWidth + x0w].rgb;
                vec3 c10 = envTexels[y0c * envWidth + x1w].rgb;
                vec3 c01 = envTexels[y1c * envWidth + x0w].rgb;
                vec3 c11 = envTexels[y1c * envWidth + x1w].rgb;
                vec3 sampled = mix(mix(c00, c10, blend.x), mix(c01, c11, blend.x), blend.y);
                return sampled * max(values[107], 0.0);
            }

            vec3 skyTop = SrgbToLinear(clamp(vec3(values[40], values[41], values[42]),
                vec3(0.0), vec3(1.0)));
            vec3 skyHorizon = SrgbToLinear(clamp(vec3(values[44], values[45], values[46]),
                vec3(0.0), vec3(1.0)));
            vec3 sky = mix(skyHorizon, skyTop, clamp(rayDirection.y * 0.5 + 0.5, 0.0, 1.0));
            return sky * max(values[43], 0.0) + vec3(values[47] * 0.08, values[47] * 0.06, values[47] * 0.03);
        }

        vec3 Ground(vec3 rayOrigin, vec3 rayDirection, float hitDistance, bool pathMode)
        {
            vec3 point = rayOrigin + rayDirection * hitDistance;
            // 两种模式都在 PostProcess 的最后一步转为显示空间；实时模式不能提前
            // LinearToDisplay，否则背景和地面会被 Gamma/色调曲线重复处理而发灰。
            vec3 groundColor = vec3(values[36], values[37], values[38]);
            float groundHorizonTransition = max(values[34], 0.0);
            if (groundHorizonTransition > 0.0)
            {
                // 地面是无限平面，不能按命中点的 XZ 半径淡出；那会制造一个以相机为中心的
                // 白色圆盘，并在拉远镜头时让地面看起来消失。只在视线接近地平线时做可选过渡。
                float horizonBand = clamp(0.05 + groundHorizonTransition / 4096.0 * 0.45,
                    0.05, 0.50);
                float horizonFade = 1.0 - smoothstep(0.0, horizonBand, abs(rayDirection.y));
                float groundFade = horizonFade * clamp(groundHorizonTransition / 1024.0, 0.0, 1.0);
                vec3 horizon = pathMode || values[115] < 0.5
                    ? Sky(rayDirection)
                    : vec3(values[98], values[99], values[100]);
                groundColor = mix(groundColor, horizon, groundFade);
            }
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

        // 点光与聚光的范围窗口：距离平方衰减再乘范围末端平滑归零的窗口，
        // 避免在 range 处出现硬切的圆形亮斑
        float SceneLightWindow(float distanceSquared, float range)
        {
            float distance = sqrt(distanceSquared);
            float ratio = distance / max(range, 0.01);
            float window = clamp(1.0 - ratio * ratio * ratio * ratio, 0.0, 1.0);
            return window * window / max(distanceSquared, 1e-4);
        }

        // 求第 index 盏本地光源对表面的入射方向与已含强度、衰减的辐照颜色。
        // 阴影是硬阴影：从着色点向光源投射一条遮挡射线，命中体素即返回 false；
        // 本地光源没有面积参数，因此不做软阴影
        bool SceneLightSample(int index, vec3 hitPoint, vec3 hitNormal,
            out vec3 lightDirection, out vec3 radiance)
        {
            SceneLightData light = sceneLights[index];
            lightDirection = vec3(0.0, 1.0, 0.0);
            radiance = vec3(0.0);
            if (light.directionEnabled.w < 0.5) return false;
            int kind = int(light.positionKind.w + 0.5);
            float attenuation = 1.0;
            float shadowDistance = 1.0e30;
            if (kind == 0)
            {
                // Direction 是光线传播方向，表面指向光源要取反
                lightDirection = normalize(-light.directionEnabled.xyz);
            }
            else
            {
                vec3 offset = light.positionKind.xyz - hitPoint;
                float distanceSquared = max(dot(offset, offset), 1e-4);
                lightDirection = offset * inversesqrt(distanceSquared);
                attenuation = SceneLightWindow(distanceSquared, light.rangeSpot.x);
                shadowDistance = sqrt(distanceSquared) - 0.05;
                if (kind == 2)
                {
                    // 聚光锥轴就是光线传播方向，表面到光源的向量与其反向夹角
                    float outerDegrees = clamp(light.rangeSpot.y, 0.0, 179.0);
                    float innerDegrees = clamp(light.rangeSpot.z, 0.0, outerDegrees);
                    float cosOuter = cos(radians(outerDegrees));
                    float cosInner = cos(radians(innerDegrees));
                    float cosAngle = dot(-lightDirection, normalize(light.directionEnabled.xyz));
                    attenuation *= smoothstep(cosOuter, max(cosInner, cosOuter + 1e-4), cosAngle);
                }
            }

            // 配光曲线预设：围绕对象朝向的轴向亮度分布，模拟常见的 IES 轮廓
            if (kind != 0)
            {
                int profile = int(light.profileExtra.x + 0.5);
                if (profile > 0)
                {
                    float cosAxis = clamp(dot(-lightDirection, normalize(light.directionEnabled.xyz)), 0.0, 1.0);
                    if (profile == 1) attenuation *= pow(cosAxis, 6.0);
                    else if (profile == 2) attenuation *= pow(cosAxis, 0.35);
                    else if (profile == 3)
                    {
                        float sinTheta = sqrt(max(1.0 - cosAxis * cosAxis, 0.0));
                        attenuation *= clamp(0.25 + 1.5 * sinTheta * sinTheta, 0.0, 1.0);
                    }
                }
            }

            // 两个调用方的直接光响应都包含 max(N·L, 0)，零贡献无需投射阴影射线。
            if (dot(hitNormal, lightDirection) <= 0.0 || attenuation <= 0.0 ||
                light.colorIntensity.w <= 0.0 ||
                all(lessThanEqual(light.colorIntensity.rgb, vec3(0.0)))) return false;

            if (light.rangeSpot.w > 0.5 && shadowDistance > 0.0)
            {
                // 沿法线离开表面半个体素，避免阴影射线从实体内部出发命中自身
                vec3 shadowOrigin = hitPoint + normalize(hitNormal) * 0.05 + lightDirection * 0.02;
                if (TraceShadow(shadowOrigin, lightDirection, shadowDistance)) return false;
            }

            radiance = max(light.colorIntensity.rgb, vec3(0.0)) *
                max(light.colorIntensity.w, 0.0) * attenuation;
            return true;
        }

        // 场景本地光源对表面的直接光辐照度（未乘 BRDF），光源数为 0 时返回零向量
        vec3 SceneLocalLightIrradiance(vec3 hitPoint, vec3 hitNormal)
        {
            int lightCount = clamp(int(values[P_SCENE_LIGHT_COUNT]), 0, SCENE_LIGHT_CAPACITY);
            vec3 irradiance = vec3(0.0);
            for (int index = 0; index < SCENE_LIGHT_CAPACITY; index++)
            {
                if (index >= lightCount) break;
                vec3 lightDirection;
                vec3 radiance;
                if (!SceneLightSample(index, hitPoint, hitNormal, lightDirection, radiance)) continue;
                irradiance += radiance * max(dot(hitNormal, lightDirection), 0.0);
            }
            return irradiance;
        }

        vec3 RealtimeIndirectLight(vec3 hitPoint, vec3 hitNormal, inout uint state)
        {
            if (values[119] <= 0.0001) return vec3(0.0);
            vec3 accumulated = vec3(0.0);
            const int sampleCount = 2;
            for (int index = 0; index < sampleCount; index++)
            {
                vec3 direction = CosineHemisphere(hitNormal, state);
                float distance;
                vec3 normal;
                vec4 color;
                uint flags;
                uint slot;
                ivec3 cell;
                ivec3 extent;
                if (TraceClosestWithin(hitPoint + hitNormal * 0.004, direction, distance, normal, color,
                    flags, slot, cell, extent, true, 8.0, false))
                {
                    accumulated += SrgbToLinear(clamp(color.rgb, vec3(0.0), vec3(1.0))) *
                        max(dot(normal, -direction), 0.0) * (1.0 - distance / 8.0);
                }
                else accumulated += Sky(direction) * 0.35;
            }
            return accumulated / float(sampleCount) * values[119];
        }

        // 实时渲染路径：对应 Eevee 的职责，使用确定性的直接光、软阴影、AO、
        // 环境补光、材质高光和自发光，不做随机反弹，也不依赖累积样本。
        vec3 RealtimeShade(vec3 hitPoint, vec3 hitNormal, vec3 rayDirection,
            vec4 hitColor, uint hitFlags, MaterialSlotData material,
            bool groundHit, vec3 groundLinearColor, inout uint state)
        {
            // 体素颜色来自 RGBA8 调色板，是 sRGB；实时管线在光照前转换到线性空间，
            // 最后由 PostProcess 统一回到显示空间，避免整张画面发灰或过曝。
            vec3 baseColor = groundHit
                ? max(groundLinearColor, vec3(0.0))
                : SrgbToLinear(clamp(hitColor.rgb, vec3(0.0), vec3(1.0)));
            vec3 skyAmbient = Sky(hitNormal);
            // 给背光面保留稳定的环境填充，避免实时模式像只开了一盏聚光灯；
            // 这不是把阴影抹平，而是把最低亮度托到可读范围，接近 Eevee 的世界光照。
            vec3 ambientFill = mix(vec3(0.14, 0.16, 0.20), skyAmbient, 0.72);
            ambientFill *= 0.82 + values[43] * 0.55;
            ambientFill += vec3(0.035, 0.040, 0.050) * (0.5 + max(hitNormal.y, 0.0));
            // AO 只衰减环境补光；太阳已经有可见度阴影，不能被重复压暗。
            float ao = AmbientOcclusion(hitPoint, hitNormal, state);
            vec3 diffuse = baseColor * ambientFill * mix(1.0, ao, 0.60);
            diffuse += baseColor * RealtimeIndirectLight(hitPoint, hitNormal, state);
            vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
            // SunDirection 表示从表面指向太阳，正方向才是受光方向。
            vec3 lightDirection = sunDirection;
            float directAmount = max(dot(hitNormal, lightDirection), 0.0);
            if (values[33] > 0.5 && directAmount > 0.0)
            {
                float shadowVisibility = TraceSoftShadowVisibility(
                    hitPoint + hitNormal * 0.002, lightDirection, 10000.0);
                diffuse += baseColor * vec3(values[28], values[29], values[30]) *
                    values[27] * directAmount * shadowVisibility;
            }

            // 场景本地光源直接光，只有启用且强度大于 0 的光源参与；
            // 光源数为 0 时辐照度为零向量，输出与改动前一致
            diffuse += baseColor * SceneLocalLightIrradiance(hitPoint, hitNormal);

            float metallic = clamp(material.scalars.y >= 0.0 ? material.scalars.y : values[74], 0.0, 1.0);
            float roughness = clamp(material.scalars.x >= 0.0 ? material.scalars.x : values[73], 0.04, 1.0);
            // 材质镜面强度只缩放电介质部分的 F0：金属的反射色由基色决定，不参与缩放
            float specularScale = material.optics.y >= 0.0 ? material.optics.y : 1.0;
            vec3 f0 = mix(vec3(clamp(values[75] * specularScale, 0.0, 1.0)), baseColor, metallic);
            vec3 viewDirection = normalize(-rayDirection);
            vec3 halfDirection = normalize(lightDirection + viewDirection);
            float specular = pow(max(dot(hitNormal, halfDirection), 0.0),
                mix(12.0, 192.0, 1.0 - roughness));
            float viewFacing = max(dot(viewDirection, hitNormal), 0.0);
            vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - viewFacing, 5.0);
            vec3 highlight = vec3(specular * directAmount * values[27]) * fresnel *
                vec3(values[28], values[29], values[30]);
            vec3 reflectedSky = Sky(reflect(rayDirection, hitNormal));
            vec3 environmentSpecular = reflectedSky * fresnel * (0.18 + metallic * 0.82);
            vec3 result = diffuse * (1.0 - metallic * 0.78) + environmentSpecular + highlight;
            if ((hitFlags & 2u) != 0u)
            {
                float emissiveScale = material.scalars.z >= 0.0 ? material.scalars.z : 1.0;
                // 实时 Bloom 需要 HDR 亮部，但限制极端亮度避免单个灯把整个画面冲成白块。
                result += baseColor * min(values[80] * emissiveScale, 4.5);
            }
            float cameraDistance = length(hitPoint - vec3(values[0], values[1], values[2]));
            float nearFadeDistance = max(values[39], 0.0);
            if (nearFadeDistance > 0.0)
            {
                float nearVisibility = smoothstep(0.0, nearFadeDistance, cameraDistance);
                result = mix(Sky(rayDirection), result, nearVisibility);
            }
            float fogDensity = max(values[51], 0.0);
            if (fogDensity > 0.0)
            {
                float fogAmount = 1.0 - exp(-fogDensity * cameraDistance);
                vec3 fogColor = mix(Sky(rayDirection),
                    SrgbToLinear(clamp(vec3(values[48], values[49], values[50]),
                        vec3(0.0), vec3(1.0))), 0.55);
                result = mix(result, fogColor, clamp(fogAmount, 0.0, 0.92));
            }
            return max(result, vec3(0.0));
        }

        vec3 SunSurfaceResponse(vec3 baseColor, float metalness, float roughness,
            vec3 normal, vec3 viewDir, vec3 lightDir)
        {
            float nDotL = max(dot(normal, lightDir), 0.0);
            float nDotV = max(dot(normal, viewDir), 0.0);
            if (nDotL <= 0.0 || nDotV <= 0.0) return vec3(0.0);
            vec3 halfVector = normalize(viewDir + lightDir);
            float nDotH = max(dot(normal, halfVector), 0.0);
            float vDotH = max(dot(viewDir, halfVector), 0.0);
            float alpha = max(roughness * roughness, 0.002);
            float alphaSquared = alpha * alpha;
            float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;
            float distribution = alphaSquared / max(3.14159265 * denominator * denominator, 1e-12);
            float gView = 2.0 * nDotV / max(nDotV + sqrt(alphaSquared + (1.0 - alphaSquared) * nDotV * nDotV), 1e-6);
            float gLight = 2.0 * nDotL / max(nDotL + sqrt(alphaSquared + (1.0 - alphaSquared) * nDotL * nDotL), 1e-6);
            vec3 f0 = mix(vec3(clamp(values[75], 0.0, 1.0)), baseColor, metalness);
            vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - vDotH, 5.0);
            vec3 specular = fresnel * distribution * gView * gLight / max(4.0 * nDotL * nDotV, 1e-6);
            vec3 diffuse = baseColor * (1.0 - metalness) * (vec3(1.0) - fresnel);
            return (diffuse + specular * 3.14159265) * nDotL;
        }

        // 场景本地光源在路径追踪中的直接光响应，复用太阳表面响应保持材质一致
        vec3 SceneLocalLightResponse(vec3 hitPoint, vec3 hitNormal, vec3 viewDirection,
            vec3 baseColor, float metalness, float roughness)
        {
            int lightCount = clamp(int(values[P_SCENE_LIGHT_COUNT]), 0, SCENE_LIGHT_CAPACITY);
            vec3 response = vec3(0.0);
            for (int index = 0; index < SCENE_LIGHT_CAPACITY; index++)
            {
                if (index >= lightCount) break;
                vec3 lightDirection;
                vec3 radiance;
                if (!SceneLightSample(index, hitPoint, hitNormal, lightDirection, radiance)) continue;
                response += SunSurfaceResponse(baseColor, metalness, roughness,
                    hitNormal, viewDirection, lightDirection) * radiance;
            }
            return response;
        }

        /* EMISSIVE_SAMPLING */

        vec3 TracePath(vec3 rayOrigin, vec3 rayDirection, inout uint state, bool pathMode,
            out bool primarySurfaceHit, out float primarySurfaceDistance, out vec3 primaryNormal,
            out bool primarySelected)
        {
            primarySelected = false;
            primarySurfaceHit = false;
            primarySurfaceDistance = 3.402823e+38;
            primaryNormal = vec3(0.0);
            vec3 radiance = vec3(0.0);
            vec3 throughput = vec3(1.0);
            float previousBsdfPdf = 0.0;
            vec3 previousVertex = rayOrigin;
            int translucentPasses = 0;
            ivec3 lastTranslucentCell = ivec3(-2);
            vec3 pendingInterface = vec3(0.0);
            vec3 pendingInterfaceNormal = vec3(0.0, 1.0, 0.0);
            bool hasPendingInterface = false;
            int bounceLimit = pathMode ? min(int(values[72]), 16) : 1;
            int surfaceBounces = 0;
            // 上限 32：半透明逐体素穿透与表面弹射共享迭代次数，
            // 深水“下穿若干层、湖底反射、再上浮若干层”就要十几次迭代，
            // 旧上限 16 会让路径在触及湖底前被硬截断，湖底贡献按体素相位忽有忽无，
            // 水面因此长出与深度周期锁定的规则点阵
            for (int bounce = 0; bounce < 32; bounce++)
            {
                if (surfaceBounces >= bounceLimit) break;
                float hitDistance;
                vec3 hitNormal;
                vec4 hitColor;
                uint hitFlags;
                uint hitSlot;
                ivec3 hitCell;
                ivec3 hitExtent;
                bool groundHit = false;
                if (!TraceClosest(rayOrigin, rayDirection, hitDistance, hitNormal, hitColor, hitFlags, hitSlot, hitCell, hitExtent, false))
                {
                    float groundDistance = (values[35] > 0.5 || (!pathMode && values[90] > 0.5 && values[115] < 0.5)) && abs(rayDirection.y) > 0.00001
                        ? -rayOrigin.y / rayDirection.y : 3.402823e+38;
                    if (groundDistance > 0.0 && groundDistance < values[97])
                    {
                        if (bounce == 0)
                        {
                            primarySurfaceHit = true;
                            primarySurfaceDistance = groundDistance;
                        }
                        // 把无限地面作为真正的表面命中，而不是背景颜色。
                        // 这样它会复用体素表面的太阳阴影、AO、环境高光和路径弹射，
                        // 远离模型时也不会出现只剩一块背景色的假地面。
                        groundHit = true;
                        hitDistance = groundDistance;
                        hitNormal = vec3(0.0, 1.0, 0.0);
                        hitColor = vec4(0.0, 0.0, 0.0, 1.0);
                        hitFlags = 0u;
                        hitSlot = 0u;
                        hitCell = ivec3(0);
                        hitExtent = ivec3(1);
                    }
                    else
                    {
                        radiance += pathMode || values[115] < 0.5
                            ? throughput * Sky(rayDirection)
                            : vec3(values[98], values[99], values[100]);
                        break;
                    }
                }

                if (bounce == 0 && (hitDistance < values[96] || hitDistance > values[97]))
                {
                    if (!pathMode && values[115] > 0.5)
                    {
                        radiance += vec3(values[98], values[99], values[100]);
                    }
                    else
                    {
                        radiance += throughput * Sky(rayDirection);
                    }
                    break;
                }

                vec3 hitPoint = rayOrigin + rayDirection * hitDistance;
                bool backfaceHit = dot(hitNormal, rayDirection) > 0.0;
                // 半透明出口推进的 epsilon 可能让射线起点落进下一个实心体素内部，
                // 此时盒求交返回背面交点、法线与射线同向。翻回入射界面法线，并把
                // 着色点精确锚回射线进入该体素的界面点（命中点沿射线回退 far-near 段）：
                // 锚点若仍留在实心内部，阴影射线被自身体素遮挡与否随亚体素相位翻转，
                // 湖底上方会浮出与体素网格锁定的规则点阵
                if (backfaceHit)
                {
                    if (hasPendingInterface)
                    {
                        hitPoint = pendingInterface;
                        hitNormal = pendingInterfaceNormal;
                    }
                    else
                    {
                        hitNormal = -hitNormal;
                    }
                }
                hasPendingInterface = false;
                if (bounce == 0)
                {
                    primarySurfaceHit = true;
                    primarySurfaceDistance = hitDistance;
                    primaryNormal = hitNormal;
                    primarySelected = (hitFlags & 256u) != 0u;
                }
                MaterialSlotData material = materialSlots[int(hitSlot)];
                vec3 hitLinearColor = groundHit
                    ? Ground(rayOrigin, rayDirection, hitDistance, pathMode)
                    : SrgbToLinear(clamp(hitColor.rgb, vec3(0.0), vec3(1.0)));
                if (!pathMode && values[115] > 0.5 && values[57] <= 0.5)
                {
                    // 场景窗口的 GPU 基础显示：只做确定性的面向明暗，不进入实时特效路径。
                    vec3 faceColor = hitLinearColor;
                    if (values[113] > 0.5 && values[33] > 0.5)
                    {
                        vec3 sceneSun = normalize(vec3(values[24], values[25], values[26]));
                        float sunFace = max(dot(hitNormal, -sceneSun), 0.0);
                        faceColor *= 0.52 + 0.38 * sunFace + 0.14 * max(hitNormal.y, 0.0);
                    }
                    radiance += faceColor;
                    break;
                }
                if (!pathMode && values[57] > 0.5)
                {
                    // 只有编辑器场景预览显式要求描边时才绘制纯色体素轮廓；
                    // 渲染视图关闭描边时必须继续执行下方完整的实时光照路径。
                    vec3 outlineBoxMin = vec3(values[16], values[17], values[18]) + vec3(hitCell);
                    vec3 outlineExtent = vec3(hitExtent);
                    vec3 localPoint = clamp(hitPoint - outlineBoxMin, vec3(0.0), outlineExtent);
                    vec3 remainPoint = outlineExtent - localPoint;
                    float edgeDistance;
                    if (abs(hitNormal.x) > 0.5)
                        edgeDistance = min(min(localPoint.y, remainPoint.y), min(localPoint.z, remainPoint.z));
                    else if (abs(hitNormal.y) > 0.5)
                        edgeDistance = min(min(localPoint.x, remainPoint.x), min(localPoint.z, remainPoint.z));
                    else
                        edgeDistance = min(min(localPoint.x, remainPoint.x), min(localPoint.y, remainPoint.y));
                    float outlineWidth = max(0.012, hitDistance / max(values[3], 1.0) * 1.35);
                    float outlineAmount = 1.0 - smoothstep(outlineWidth * 0.25, outlineWidth, edgeDistance);
                    // 一格体素缩到几个像素时，固定像素宽的四周棱线会盖住整个面。
                    // 使用命中面的最小世界跨度换算到屏幕像素，远处逐渐减弱描边，
                    // 避免大场景俯视时所有体素叠成一片黑色。
                    float faceSpan = abs(hitNormal.x) > 0.5
                        ? min(outlineExtent.y, outlineExtent.z)
                        : abs(hitNormal.y) > 0.5
                            ? min(outlineExtent.x, outlineExtent.z)
                            : min(outlineExtent.x, outlineExtent.y);
                    float projectedSpan = faceSpan * max(values[3], 1.0) / max(hitDistance, 1.0);
                    outlineAmount *= smoothstep(2.0, 9.0, projectedSpan);
                    vec3 faceColor = hitLinearColor;
                    if (values[113] > 0.5)
                    {
                        vec3 sceneSun = normalize(vec3(values[24], values[25], values[26]));
                        float sunFace = max(dot(hitNormal, -sceneSun), 0.0);
                        faceColor *= 0.52 + 0.38 * sunFace + 0.14 * max(hitNormal.y, 0.0);
                    }
                    radiance += mix(faceColor, vec3(0.012, 0.014, 0.018), outlineAmount);
                    break;
                }

                if (!pathMode)
                {
                    vec3 realtime = RealtimeShade(hitPoint, hitNormal, rayDirection,
                        hitColor, hitFlags, material, groundHit, hitLinearColor, state);
                    if ((hitFlags & 1u) != 0u)
                    {
                        // 逐段合成透明物体，不能跳过杯壁后面的液体或其他透明层。
                        vec3 accumulated = vec3(0.0);
                        vec3 remaining = vec3(1.0);
                        vec3 cursor = hitPoint + rayDirection * 0.001;
                        ivec3 currentCell = hitCell;
                        ivec3 currentExtent = hitExtent;
                        vec4 currentColor = hitColor;
                        uint currentSlot = hitSlot;
                        for (int layer = 0; layer < 128; layer++)
                        {
                            vec3 boxMin = vec3(values[16], values[17], values[18]) + vec3(currentCell);
                            vec3 boxMax = boxMin + vec3(currentExtent);
                            float segment;
                            vec3 segmentNormal;
                            if (!IntersectBox(cursor, rayDirection, boxMin, boxMax, segment, segmentNormal)) break;
                            float overrideOpacity = materialSlots[int(currentSlot)].optics.z;
                            float density = overrideOpacity >= 0.0 ? overrideOpacity : currentColor.a;
                            MaterialSlotData layerMaterial = materialSlots[int(currentSlot)];
                            float hazeDensity = max(layerMaterial.haze.w, 0.0);
                            float opacity = 1.0 - exp(-clamp(density, 0.0, 1.0) * max(segment, 0.001) * (0.5 + hazeDensity));
                            vec3 tint = SrgbToLinear(clamp(currentColor.rgb, vec3(0.0), vec3(1.0)));
                            vec3 scatterColor = layerMaterial.haze.x >= 0.0
                                ? SrgbToLinear(clamp(layerMaterial.haze.rgb, vec3(0.0), vec3(1.0))) : tint;
                            float scatter = layerMaterial.optics.w >= 0.0 ? layerMaterial.optics.w : 0.15;
                            accumulated += remaining * scatterColor * opacity * scatter;
                            remaining *= mix(vec3(1.0), tint, opacity) *
                                exp(-clamp(density, 0.0, 1.0) * hazeDensity * max(segment, 0.001));
                            cursor += rayDirection * (segment + 0.001);
                            float nextDistance;
                            vec3 nextNormal;
                            vec4 nextColor;
                            uint nextFlags;
                            uint nextSlot;
                            ivec3 nextCell;
                            ivec3 nextExtent;
                            if (!TraceClosest(cursor, rayDirection, nextDistance, nextNormal, nextColor,
                                nextFlags, nextSlot, nextCell, nextExtent, false))
                            {
                                accumulated += remaining * Sky(rayDirection);
                                break;
                            }
                            if ((nextFlags & 1u) == 0u)
                            {
                                if (dot(nextNormal, rayDirection) > 0.0) nextNormal = -nextNormal;
                                MaterialSlotData nextMaterial = materialSlots[int(nextSlot)];
                                accumulated += remaining * RealtimeShade(cursor + rayDirection * nextDistance,
                                    nextNormal, rayDirection, nextColor, nextFlags, nextMaterial, false,
                                    SrgbToLinear(clamp(nextColor.rgb, vec3(0.0), vec3(1.0))), state);
                                break;
                            }
                            vec3 nextMin = vec3(values[16], values[17], values[18]) + vec3(nextCell);
                            vec3 nextMax = nextMin + vec3(nextExtent);
                            bool startsInsideNext = all(greaterThanEqual(cursor, nextMin)) &&
                                all(lessThanEqual(cursor, nextMax));
                            if (!startsInsideNext) cursor += rayDirection * (nextDistance + 0.001);
                            currentCell = nextCell;
                            currentExtent = nextExtent;
                            currentColor = nextColor;
                            currentSlot = nextSlot;
                        }
                        float facing = max(dot(-rayDirection, hitNormal), 0.0);
                        float reflectionWeight = clamp((1.0 - facing) * 0.18 + 0.04, 0.04, 0.22);
                        radiance += mix(accumulated, realtime, reflectionWeight);
                    }
                    else
                    {
                        radiance += realtime;
                    }
                    break;
                }

                float emissiveScale = material.scalars.z >= 0.0 ? material.scalars.z : 1.0;
                if ((hitFlags & 2u) != 0u)
                {
                    // 发光贡献按萤火虫钳制：单次弹射命中发光体素的辐射上限取最终钳制的 10% 且不低于 1，
                    // 否则高强度小发光体（提灯等）在漫弹射中偶发命中会产生白点火噪，
                    // 低采样累积阶段表现为亮斑周围的蓝白噪点
                    float emissiveCeil = max(values[77] * 0.1, 1.0);
                    float lampPdf = previousBsdfPdf > 0.0
                        ? EmissiveHitPdf(previousVertex, hitPoint, hitNormal) : 0.0;
                    float hitWeight = previousBsdfPdf > 0.0
                        ? previousBsdfPdf / (previousBsdfPdf + lampPdf) : 1.0;
                    radiance += throughput * min(hitLinearColor * values[80] * emissiveScale,
                        vec3(emissiveCeil)) * hitWeight;
                }

                // 路径追踪的随机半球反弹已经通过实际几何自然积分间接遮挡；
                // 再乘一次实时 AO 会重复压暗每个体素表面，并把接触阴影烙成块状脏色。
                // AO 仅由 RealtimeShade 使用，路径追踪不做额外 AO 乘权。

                if ((hitFlags & 1u) != 0u)
                {
                    // 直接光阴影不穿过介质；经过介质的路径仅由 BSDF 命中承担。
                    previousBsdfPdf = 0.0;
                    // 同格守卫：推进后仍命中刚离开的同一水体素说明数值退化，
                    // 立即终止该路径，绝不允许同一层的吸收与内散射被计两次
                    if (hitCell == lastTranslucentCell) break;
                    // 气液界面按菲涅尔处理，且只属于首次进入：朗伯表面项会把水画成不透明漆面，
                    // 正确行为是正视透见底、掠射反射天空，镜面高光随材质粗糙度收窄
                    if (translucentPasses == 0)
                    {
                        vec3 reflected = reflect(rayDirection, hitNormal);
                        float cosView = max(dot(reflected, hitNormal), 0.0);
                        // 界面 F0 由材质折射率决定：F0 = ((n-1)/(n+1))^2。
                        // 未设置折射率时用 1.5，与原先写死的 0.04 完全等价，旧场景画面不变。
                        float interfaceIor = material.optics.x >= 1.0 ? material.optics.x : 1.5;
                        float interfaceF0 = pow((interfaceIor - 1.0) / (interfaceIor + 1.0), 2.0);
                        if (material.optics.y >= 0.0) interfaceF0 = clamp(interfaceF0 * material.optics.y, 0.0, 1.0);
                        float fresnel = clamp(interfaceF0 + (1.0 - interfaceF0) * pow(1.0 - cosView, 5.0), 0.0, 1.0);
                        radiance += throughput * Sky(reflected) * fresnel;
                        if (values[33] > 0.5)
                        {
                            float interfaceRoughness = clamp(material.scalars.x >= 0.0
                                ? material.scalars.x : values[73], 0.01, 1.0);
                            float glintExponent = clamp(2.0 / (interfaceRoughness * interfaceRoughness) - 2.0, 32.0, 1024.0);
                            vec3 sunGlint = normalize(vec3(values[24], values[25], values[26]));
                            float glint = pow(max(dot(reflected, sunGlint), 0.0), glintExponent);
                            if (glint > 0.001)
                            {
                                float glintVisibility = TraceSoftShadow(
                                    hitPoint + hitNormal * 0.002, sunGlint, 10000.0, state);
                                radiance += throughput * vec3(1.0, 0.97, 0.92) * glint * values[27]
                                    * 6.0 * glintVisibility;
                            }
                        }
                        throughput *= 1.0 - fresnel * 0.85;
                    }
                    translucentPasses++;
                    if (translucentPasses >= 24)
                        break; // 防御上限：一条路径的水气界面进入至多几次

                    // 沿折射方向逐段穿过相邻的同种半透明盒体，直到真正的外表面。
                    // 不能把任意球体或薄玻璃近似成垂直水柱，否则会在内部虚构出口。
                    vec3 absorbColor = material.absorption.w > 0.5
                        ? SrgbToLinear(clamp(material.absorption.rgb, vec3(0.0), vec3(1.0))) : hitLinearColor;
                    float stepScale = material.scalars.w >= 0.0 ? material.scalars.w : 1.0;
                    float opacity = clamp(material.optics.z >= 0.0 ? material.optics.z : hitColor.a, 0.0, 1.0);
                    float scattering = clamp(material.optics.w >= 0.0 ? material.optics.w : 0.15, 0.0, 1.0);
                    float hazeDensity = max(material.haze.w, 0.0);
                    vec3 sigma = opacity * (max(values[81], 0.0) * 0.25 * stepScale *
                        (vec3(1.0) - clamp(absorbColor, vec3(0.0), vec3(1.0)) + vec3(0.1)) + vec3(hazeDensity));
                    float mediumIor = material.optics.x > 1.001 ? material.optics.x : 1.0;
                    vec3 mediumDirection = rayDirection;
                    if (mediumIor > 1.0 && !backfaceHit)
                    {
                        vec3 refracted = refract(rayDirection, hitNormal, 1.0 / mediumIor);
                        if (dot(refracted, refracted) > 1e-6)
                        {
                            mediumDirection = normalize(refracted);
                            // 粗糙介质界面扩散透射方向；连续体内部不重复扰动，避免逐格出现噪斑。
                            float interfaceRoughness = clamp(material.scalars.x >= 0.0 ? material.scalars.x : values[73], 0.01, 1.0);
                            float spread = interfaceRoughness * interfaceRoughness * 0.28;
                            if (pathMode && spread > 0.002)
                            {
                                vec3 tangent = normalize(abs(mediumDirection.y) < 0.9
                                    ? cross(mediumDirection, vec3(0.0, 1.0, 0.0))
                                    : cross(mediumDirection, vec3(1.0, 0.0, 0.0)));
                                vec3 bitangent = cross(mediumDirection, tangent);
                                float angle = 6.28318530718 * Random(state);
                                float radius = spread * sqrt(Random(state));
                                vec3 candidate = normalize(mediumDirection + radius *
                                    (cos(angle) * tangent + sin(angle) * bitangent));
                                if (dot(candidate, hitNormal) < -0.0001) mediumDirection = candidate;
                            }
                        }
                    }

                    const float mediumEpsilon = 0.001;
                    vec3 segmentStart = backfaceHit ? rayOrigin : hitPoint;
                    vec3 cursor = segmentStart + mediumDirection * mediumEpsilon;
                    ivec3 mediumCell = hitCell;
                    ivec3 mediumExtent = hitExtent;
                    vec3 exitPoint = segmentStart;
                    vec3 exitNormal = -hitNormal;
                    float chord = 0.0;
                    bool foundExit = false;
                    float nextMediumIor = 1.0;
                    for (int mediumStep = 0; mediumStep < 512; mediumStep++)
                    {
                        vec3 boxMin = vec3(values[16], values[17], values[18]) + vec3(mediumCell);
                        vec3 boxMax = boxMin + vec3(mediumExtent);
                        float segmentDistance;
                        vec3 segmentNormal;
                        if (!IntersectBox(cursor, mediumDirection, boxMin, boxMax, segmentDistance, segmentNormal))
                            break;
                        exitPoint = cursor + mediumDirection * segmentDistance;
                        exitNormal = segmentNormal;
                        chord += segmentDistance + mediumEpsilon;
                        vec3 nextOrigin = exitPoint + mediumDirection * mediumEpsilon;
                        float nextDistance;
                        vec3 nextNormal;
                        vec4 nextColor;
                        uint nextFlags;
                        uint nextSlot;
                        ivec3 nextCell;
                        ivec3 nextExtent;
                        bool nextHit = TraceClosest(nextOrigin, mediumDirection, nextDistance, nextNormal,
                            nextColor, nextFlags, nextSlot, nextCell, nextExtent, false);
                        vec3 nextMin = vec3(values[16], values[17], values[18]) + vec3(nextCell);
                        vec3 nextMax = nextMin + vec3(nextExtent);
                        bool insideNext = nextHit && all(greaterThanEqual(nextOrigin, nextMin)) &&
                            all(lessThanEqual(nextOrigin, nextMax));
                        if (!insideNext || (nextFlags & 1u) == 0u || nextSlot != hitSlot ||
                            any(greaterThan(abs(nextColor - hitColor), vec4(0.001))))
                        {
                            if (insideNext && (nextFlags & 1u) != 0u)
                            {
                                float nextIor = materialSlots[int(nextSlot)].optics.x;
                                nextMediumIor = nextIor > 1.001 ? nextIor : 1.0;
                            }
                            foundExit = true;
                            break;
                        }
                        mediumCell = nextCell;
                        mediumExtent = nextExtent;
                        cursor = nextOrigin;
                    }
                    vec3 layerTransmittance = exp(-sigma * chord);
                    vec3 scatterColor = material.haze.x >= 0.0
                        ? SrgbToLinear(clamp(material.haze.rgb, vec3(0.0), vec3(1.0))) : hitLinearColor;
                    vec3 bodyAmbient = scatterColor * (Sky(vec3(0.0, 1.0, 0.0))
                        + (values[33] > 0.5 ? vec3(values[27] * 0.35) : vec3(0.0)));
                    radiance += throughput * (vec3(1.0) - layerTransmittance) * bodyAmbient * scattering;
                    throughput *= layerTransmittance;
                    if (max(max(throughput.r, throughput.g), throughput.b) < 0.004) break;
                    if (!foundExit)
                    {
                        // 超长介质分批继续，避免固定步数截断成黑块。
                        rayOrigin = exitPoint + mediumDirection * mediumEpsilon;
                        rayDirection = mediumDirection;
                        lastTranslucentCell = ivec3(-2);
                        continue;
                    }
                    pendingInterface = exitPoint;
                    pendingInterfaceNormal = -exitNormal;
                    hasPendingInterface = true;
                    rayDirection = mediumDirection;
                    if (abs(mediumIor - nextMediumIor) > 0.001)
                    {
                        vec3 refractedOut = refract(mediumDirection, -exitNormal, mediumIor / nextMediumIor);
                        if (dot(refractedOut, refractedOut) > 1e-6)
                            rayDirection = normalize(refractedOut);
                        else
                        {
                            // 全内反射仍在介质内，下一轮从出口内侧继续追踪。
                            rayDirection = reflect(mediumDirection, -exitNormal);
                            hasPendingInterface = false;
                        }
                    }
                    rayOrigin = exitPoint + rayDirection * mediumEpsilon;
                    lastTranslucentCell = ivec3(-2);
                    continue;
                }

                vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
                vec3 lightDirection = sunDirection;
                float metalness = clamp(material.scalars.y >= 0.0 ? material.scalars.y : values[74], 0.0, 1.0);
                float roughness = clamp(material.scalars.x >= 0.0 ? material.scalars.x : values[73], 0.01, 1.0);
                float lightAmount = values[33] > 0.5 ? max(dot(hitNormal, lightDirection), 0.0) : 0.0;
                if (values[85] > 0.5 && lightAmount > 0.0)
                {
                    float shadowVisibility = TraceSoftShadow(
                        hitPoint + hitNormal * 0.002, lightDirection, 10000.0, state);
                    vec3 response = SunSurfaceResponse(hitLinearColor, metalness, roughness, hitNormal, -rayDirection, lightDirection);
                    radiance += throughput * response * vec3(values[28], values[29], values[30]) * values[27] * shadowVisibility;
                }

                // 本地光源没有可被 BSDF 采样命中的几何，直接光采样是唯一可见途径，
                // 因此不受太阳直接光开关影响；该开关只控制太阳直接采样，
                // 本地光源目前只做硬阴影，太阳继续使用软阴影
                radiance += throughput * SceneLocalLightResponse(hitPoint, hitNormal,
                    -rayDirection, hitLinearColor, metalness, roughness);

                vec3 samplingOrigin = hitPoint + hitNormal * 0.002;
                radiance += throughput * EmissiveNextEvent(samplingOrigin, hitNormal, -rayDirection,
                    hitLinearColor, metalness, roughness,
                    surfaceBounces + 1 < bounceLimit && bounce + 1 < 32, state);

                // GGX NDF 法线采样与余弦半球采样的混合；两分支使用完整混合 PDF。
                float alpha = max(roughness * roughness, 0.002);
                float hitSpecularScale = material.optics.y >= 0.0 ? material.optics.y : 1.0;
                vec3 f0 = mix(vec3(clamp(values[75] * hitSpecularScale, 0.0, 1.0)), hitLinearColor, metalness);
                vec3 viewDir = -rayDirection;
                float xi1 = min(Random(state), 0.99999994);
                float xi2 = Random(state);
                float cosThetaH = sqrt((1.0 - xi1) / (1.0 + (alpha * alpha - 1.0) * xi1));
                float sinThetaH = sqrt(max(0.0, 1.0 - cosThetaH * cosThetaH));
                float azimuth = 6.28318530718 * xi2;
                vec3 tangentBasisX = normalize(cross(hitNormal,
                    abs(hitNormal.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
                vec3 tangentBasisY = cross(hitNormal, tangentBasisX);
                vec3 microfacetNormal = normalize(tangentBasisX * sinThetaH * cos(azimuth)
                    + tangentBasisY * sinThetaH * sin(azimuth) + hitNormal * cosThetaH);
                // 分支概率只依赖当前材质，不能依赖尚未选用的随机微表面法线。
                float fresnelStrength = clamp(max(max(f0.r, f0.g), f0.b), 0.05, 0.95);
                vec3 specularCandidate = normalize(reflect(rayDirection, microfacetNormal));
                if (Random(state) < fresnelStrength)
                {
                    //镜面采样落到表面下方时该样本贡献为零，不能改走漫反射分支
                    if (dot(specularCandidate, hitNormal) <= 0.0) break;
                    rayDirection = specularCandidate;
                }
                else
                {
                    rayDirection = CosineHemisphere(hitNormal, state);
                }

                // 两个采样分支共用完整混合 PDF 与 BRDF；SunSurfaceResponse 返回 pi*f*cos。
                float mixturePdf = SurfaceSamplingPdf(hitLinearColor, metalness, roughness,
                    hitNormal, viewDir, rayDirection);
                if (mixturePdf <= 0.0 || isnan(mixturePdf) || isinf(mixturePdf)) break;
                throughput *= SunSurfaceResponse(hitLinearColor, metalness, roughness,
                    hitNormal, viewDir, rayDirection) / (3.14159265 * mixturePdf);

                rayOrigin = hitPoint + hitNormal * 0.002;
                previousVertex = rayOrigin;
                previousBsdfPdf = mixturePdf;
                // 表面弹射后路径已离开半透明介质，同格守卫必须重置：
                // 湖底漫反射折返重新进入原水体素是合法路径，不能被当作退化命中截死
                lastTranslucentCell = ivec3(-2);
                surfaceBounces++;
                if (surfaceBounces >= 2)
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

        vec3 DenoiseAt(ivec2 pixel, int width, int height)
        {
            float strength = clamp(values[93], 0.0, 1.0);
            vec3 center = imageLoad(accumulationImage, pixel).rgb;
            vec3 filtered = vec3(0.0);
            float weightSum = 0.0;
            float centerLuma = dot(center, vec3(0.2126, 0.7152, 0.0722));
            vec4 centerGuide = imageLoad(guideImage, pixel);
            for (int oy = -2; oy <= 2; oy++)
            {
                for (int ox = -2; ox <= 2; ox++)
                {
                    ivec2 samplePixel = clamp(pixel + ivec2(ox, oy), ivec2(0), ivec2(width - 1, height - 1));
                    vec3 neighbor = imageLoad(accumulationImage, samplePixel).rgb;
                    float spatial = exp(-float(ox * ox + oy * oy) / 3.5);
                    float neighborLuma = dot(neighbor, vec3(0.2126, 0.7152, 0.0722));
                    float edge = exp(-abs(neighborLuma - centerLuma) * 10.0);
                    vec4 neighborGuide = imageLoad(guideImage, samplePixel);
                    if ((centerGuide.w > 0.0) != (neighborGuide.w > 0.0)) continue;
                    if (centerGuide.w > 0.0)
                    {
                        edge *= pow(max(dot(centerGuide.xyz, neighborGuide.xyz), 0.0), 16.0);
                        edge *= exp(-abs(centerGuide.w - neighborGuide.w) / max(centerGuide.w * 0.02, 0.5));
                    }
                    float weight = spatial * mix(0.0, edge, strength);
                    filtered += neighbor * weight;
                    weightSum += weight;
                }
            }
            vec3 color = weightSum > 0.0 ? filtered / weightSum : center;
            return mix(center, color, strength);
        }

        vec3 BloomGather(ivec2 pixel, int width, int height)
        {
            float radius = max(values[112], 1.0);
            float threshold = max(values[111], 0.0);
            vec3 sum = vec3(0.0);
            float weightSum = 0.0;
            for (int tap = 0; tap < 32; tap++)
            {
                float t = (float(tap) + 0.5) / 32.0;
                float angle = float(tap) * 2.39996323;
                float diskRadius = sqrt(t) * radius;
                ivec2 samplePixel = clamp(pixel + ivec2(int(cos(angle) * diskRadius),
                    int(sin(angle) * diskRadius)), ivec2(0), ivec2(width - 1, height - 1));
                vec3 sampleColor = imageLoad(accumulationImage, samplePixel).rgb;
                float luminance = dot(sampleColor, vec3(0.2126, 0.7152, 0.0722));
                float over = max(luminance - threshold, 0.0) / max(luminance, 1e-4);
                float weight = 1.0 - t * 0.6;
                sum += sampleColor * over * weight;
                weightSum += weight;
            }
            return sum / max(weightSum, 1e-4);
        }

        float GridCoverage(vec2 point, vec2 footprint, float stepSize)
        {
            vec2 distanceToLine = abs(fract(point / stepSize + 0.5) - 0.5) * stepSize;
            vec2 coverage = 1.0 - smoothstep(footprint * 0.05, footprint * 1.05, distanceToLine);
            return max(coverage.x, coverage.y);
        }

        void StoreSceneOutput(ivec2 pixel, vec4 value)
        {
            if (values[63] > 0.5 && values[63] < 1.5)
                imageStore(accumulationImage, pixel, value);
            else
                imageStore(outputImage, pixel, value);
        }

        // Background reachable along a screen axis is outside the selection silhouette.
        // Closed holes are rejected rather than receiving internal outlines.
        bool SelectionExterior(ivec2 point, ivec2 size)
        {
            const ivec2 directions[4] = ivec2[4](ivec2(1, 0), ivec2(-1, 0), ivec2(0, 1), ivec2(0, -1));
            for (int d = 0; d < 4; d++)
            {
                ivec2 cursor = point;
                bool escaped = true;
                for (int step = 0; step < max(size.x, size.y); step++)
                {
                    if (cursor.x < 0 || cursor.y < 0 || cursor.x >= size.x || cursor.y >= size.y) break;
                    if (imageLoad(guideImage, cursor).x > 0.5) { escaped = false; break; }
                    cursor += directions[d];
                }
                if (escaped) return true;
            }
            return false;
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy) +
                ivec2(int(values[101]), int(values[102]));
            int width = int(values[11]);
            int height = int(values[15]);
            if (pixel.x >= width || pixel.y >= height) return;

            if (values[63] > 1.5)
            {
                vec4 base = imageLoad(accumulationImage, pixel);
                if (imageLoad(guideImage, pixel).x > 0.5)
                {
                    bool edge = false;
                    const ivec2 offsets[8] = ivec2[8](ivec2(2, 0), ivec2(-2, 0), ivec2(0, 2), ivec2(0, -2),
                        ivec2(1, 1), ivec2(-1, 1), ivec2(1, -1), ivec2(-1, -1));
                    for (int i = 0; i < 8; i++)
                    {
                        ivec2 neighbor = pixel + offsets[i];
                        if (neighbor.x < 0 || neighbor.y < 0 || neighbor.x >= width || neighbor.y >= height)
                        { edge = true; break; }
                        if (imageLoad(guideImage, neighbor).x < 0.5 && SelectionExterior(neighbor, ivec2(width, height)))
                        { edge = true; break; }
                    }
                    if (edge) base = vec4(1.0, 0.784, 0.235, 1.0);
                }
                imageStore(outputImage, pixel, base);
                return;
            }
            bool pathMode = values[86] > 0.5;
            bool useDenoise = pathMode && values[92] > 0.5;
            bool bloomRequested = values[109] > 0.5 && values[110] > 0.0 &&
                (pathMode || values[57] < 0.5);
            if (values[103] > 0.5 && useDenoise)
            {
                vec3 color = DenoiseAt(pixel, width, height);
                StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height), 1.0));
                return;
            }

            // Bloom 合成通道：以（可选降噪后的）HDR 累积为基色，叠加过曝亮部的宽核辉光再统一色调映射
            if (int(values[108]) == 1)
            {
                vec3 base = useDenoise ? DenoiseAt(pixel, width, height)
                    : imageLoad(accumulationImage, pixel).rgb;
                vec3 color = base + BloomGather(pixel, width, height) * clamp(values[110], 0.0, 2.0);
                StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height), 1.0));
                return;
            }

            int samples = pathMode ? max(int(values[78]), 1) : 1;
            int accumulatedSamples = max(int(values[79]), 0);
            // 实时模式保持屏幕空间效果稳定，不把毫秒级帧号带入 AO/阴影随机种子；
            // 路径模式才使用帧号做时间去相关并交给累积缓冲收敛。
            uint state = Hash(uint(pixel.x) * 1973u + uint(pixel.y) * 9277u +
                (pathMode ? uint(values[7]) * 26699u + uint(values[88]) : 0u));
            vec3 origin = vec3(values[0], values[1], values[2]);
            float focalPixels = max(values[3], 1.0);
            vec3 forward = vec3(values[4], values[5], values[6]);
            vec3 right = vec3(values[8], values[9], values[10]);
            vec3 up = vec3(values[12], values[13], values[14]);
            vec3 sampleColor = vec3(0.0);
            bool primarySurfaceHit = false;
            bool selectedPixel = false;
            float primarySurfaceDistance = 3.402823e+38;
            vec4 guide = vec4(0.0);
            for (int sampleIndex = 0; sampleIndex < 64; sampleIndex++)
            {
                if (sampleIndex >= samples) break;
                // 像素静态分层抖动：每像素固定基准保证网格分层（低样本无团簇颗粒），
                // 帧间黄金比旋转去相关（累积仍收敛到均匀覆盖）
                vec2 jitter = vec2(0.0);
                if (pathMode)
                {
                    float saltX = float(Hash(uint(pixel.x) * 1973u + uint(pixel.y) * 42317u + 11u)) / 4294967296.0;
                    float saltY = float(Hash(uint(pixel.x) * 7393u + uint(pixel.y) * 15211u + 47u)) / 4294967296.0;
                    float sampleNumber = values[7] + float(sampleIndex);
                    jitter = vec2(fract(saltX + sampleNumber * 0.6180339887),
                        fract(saltY + sampleNumber * 0.7548776662)) - 0.5;
                }
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
                float sampleSurfaceDistance = 3.402823e+38;
                vec3 sampleNormal;
                bool sampleSelected;
                sampleColor += TracePath(sampleOrigin, direction, state, pathMode,
                    sampleSurfaceHit, sampleSurfaceDistance, sampleNormal, sampleSelected);
                if (sampleIndex == 0) selectedPixel = sampleSelected;
                if (sampleIndex == 0 && sampleSurfaceHit)
                    guide = vec4(sampleNormal, max(sampleSurfaceDistance * dot(direction, forward), 0.0001));
                primarySurfaceHit = primarySurfaceHit || sampleSurfaceHit;
                primarySurfaceDistance = min(primarySurfaceDistance, sampleSurfaceDistance);
            }
            sampleColor /= float(samples);
            if (values[63] > 0.5 && values[63] < 1.5)
                imageStore(guideImage, pixel, vec4(selectedPixel ? 1.0 : 0.0, 0.0, 0.0, 1.0));

            vec3 color = sampleColor;
            if (pathMode)
            {
                imageStore(guideImage, pixel, guide);
                vec4 previous = imageLoad(accumulationImage, pixel);
                float totalSamples = float(accumulatedSamples + samples);
                color = totalSamples > 0.0
                    ? (previous.rgb * float(accumulatedSamples) + sampleColor * float(samples)) / totalSamples
                    : sampleColor;
                imageStore(accumulationImage, pixel, vec4(color, 1.0));
            }
            else if (bloomRequested)
            {
                // 实时模式没有路径累积，但 Bloom 仍需要一份当前帧 HDR 输入。
                imageStore(accumulationImage, pixel, vec4(color, 1.0));
            }
            if (!pathMode && values[115] > 0.5 && values[90] > 0.5)
            {
                // 无限解析平面；像素足迹决定局部层级，不依赖有限补丁或整屏线数预算。
                vec2 offset = vec2(pixel) + 0.5 - vec2(width, height) * 0.5;
                vec3 ray = forward + right * offset.x / focalPixels - up * offset.y / focalPixels;
                if (abs(ray.y) > 1e-7 && -origin.y / ray.y > 0.0)
                {
                    float t = -origin.y / ray.y;
                    // 网格是 y=0 上的真实辅助平面：只有它比该像素的体素命中更近时才覆盖模型。
                    // 用射线的空间距离与 TracePath 的归一化射线距离比较，避免相机角度变化
                    // 时参数化射线造成的深度不一致；模型在网格前方时则保持模型颜色。
                    float gridDistance = t * length(ray);
                    if (gridDistance >= primarySurfaceDistance - max(1e-4, gridDistance * 1e-5))
                    {
                        float outputAlpha = primarySurfaceHit ? 1.0 : 0.0;
                        StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height), outputAlpha));
                        return;
                    }
                    vec2 p = (origin + t * ray).xz;
                    vec3 dx = right / focalPixels;
                    vec3 dy = -up / focalPixels;
                    vec2 dpdx = (t * (dx - ray * dx.y / ray.y)).xz;
                    vec2 dpdy = (t * (dy - ray * dy.y / ray.y)).xz;
                    vec2 footprint = max(abs(dpdx) + abs(dpdy), vec2(1e-6));
                    float level = max(0.0, log2(max(footprint.x, footprint.y) * 8.0));
                    float stepSize = exp2(floor(level));
                    float blend = smoothstep(0.0, 1.0, fract(level));
                    float fine = GridCoverage(p, footprint, stepSize);
                    float coarse = GridCoverage(p, footprint, stepSize * 2.0);
                    float fade = 1.0 / (1.0 + level * 0.22);
                    float horizon = smoothstep(0.0, 0.025, abs(normalize(ray).y));
                    float alpha = mix(fine, coarse, blend) * fade * horizon * 0.65;
                    // 网格只在实际线条覆盖到的像素参与遮挡；不能将无限地面当作
                    // 不透明色板，否则相机位于地面下方时整座模型会被背景色盖住。
                    vec3 background = max(vec3(values[98], values[99], values[100]), vec3(0.0));
                    background = mix(1.055 * pow(background, vec3(1.0 / 2.4)) - 0.055,
                        background * 12.92, lessThanEqual(background, vec3(0.0031308)));
                    vec3 baseColor = primarySurfaceHit
                        ? PostProcess(color, pixel, width, height)
                        : background;
                    vec3 composite = mix(baseColor, vec3(0.38, 0.47, 0.59), alpha);
                    StoreSceneOutput(pixel, vec4(composite, 1.0));
                    return;
                }
            }
            // 场景预览允许背景透明，让场景视图绘制自己的底色；渲染视图
            // 必须始终不透明，否则关闭 Bloom 后天空像素 alpha 为 0，会透出视口底色发黑
            float outputAlpha = pathMode || values[115] < 0.5 || primarySurfaceHit ? 1.0 : 0.0;
            StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height), outputAlpha));
        }

        vec3 PostProcess(vec3 color, ivec2 pixel, int width, int height)
        {
            color *= exp2(values[65]);
            color *= vec3(values[68], values[69], values[70]);
            if (values[56] > 0.5)
            {
                float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
                color = mix(vec3(luminance), color, max(values[52], 0.0));
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
            if (values[56] > 0.5)
                color = (color - 0.5) * max(values[54], 0.0) + 0.5;
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
            PackCellInto(bytes, index * PackedCellSize, cells[index]);
        }
    }

    /// <summary>
    /// 打包指定区间的格为独立字节流（相对偏移从 0 开始），供增量上传到设备缓冲的对应偏移
    /// </summary>
    private static byte[] PackCellsRange(IReadOnlyList<VoxelGpuCell> cells, int start, int count)
    {
        var bytes = new byte[count * PackedCellSize];
        for (var index = 0; index < count; index++)
        {
            PackCellInto(bytes, index * PackedCellSize, cells[start + index]);
        }

        return bytes;
    }

    /// <summary>
    /// 把单个格图元按 std430 布局写入字节数组指定偏移
    /// </summary>
    private static void PackCellInto(byte[] bytes, int offset, VoxelGpuCell cell)
    {
        var x = cell.X;
        var y = cell.Y;
        var z = cell.Z;
        var materialSlot = cell.Value.MaterialSlot;
        var red = cell.Value.Color.R / 255F;
        var green = cell.Value.Color.G / 255F;
        var blue = cell.Value.Color.B / 255F;
        var alpha = cell.Value.Color.A / 255F;
        var extent = cell.ExtentX | (cell.ExtentY << 8) | (cell.ExtentZ << 16);
        MemoryMarshal.Write(bytes.AsSpan(offset + 0, 4), in x);
        MemoryMarshal.Write(bytes.AsSpan(offset + 4, 4), in y);
        MemoryMarshal.Write(bytes.AsSpan(offset + 8, 4), in z);
        MemoryMarshal.Write(bytes.AsSpan(offset + 12, 4), in extent);
        MemoryMarshal.Write(bytes.AsSpan(offset + 16, 4), in red);
        MemoryMarshal.Write(bytes.AsSpan(offset + 20, 4), in green);
        MemoryMarshal.Write(bytes.AsSpan(offset + 24, 4), in blue);
        MemoryMarshal.Write(bytes.AsSpan(offset + 28, 4), in alpha);
        MemoryMarshal.Write(bytes.AsSpan(offset + 32, 2), in materialSlot);
        var metadata = (uint)materialSlot | ((uint)cell.Value.Flags << 16);
        MemoryMarshal.Write(bytes.AsSpan(offset + 32, 4), in metadata);
    }

    /// <summary>
    /// 创建或扩容设备本地体素缓冲并上传工作集
    /// </summary>
    /// <param name="workset">待上传的工作集</param>
    private void UploadWorkset(VoxelGpuWorkset workset)
    {
        if (_context is null || _api is not { } api || _context.IsDeviceLost)
        {
            return;
        }

        // 描述符和缓冲会在本次同步里改写，必须等上一轮分块计算结束
        WaitForComputeIdle();
        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var bvh = VoxelGpuBvh.Build(workset);
        SyncBvhBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
        phaseWatch.Restart();
        var bytes = PackCells(bvh.OrderedCells);
        _sceneOrderedCells = bvh.OrderedCells;
        _sceneOrderedOrigin = workset.Origin;
        _sceneSelectionDirty = true;
        // 空场景也要绘制无限网格；描述符保持有效，计数为零时着色器不访问这些占位数据。
        if (bytes.Length == 0) bytes = new byte[PackedCellSize];
        var nodeBytes = bvh.NodeBytes.Length == 0 ? new byte[VoxelGpuBvh.PackedNodeSize] : bvh.NodeBytes;
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

        UploadBytes(bytes, _voxelBuffer, 0);
        UploadBytes(nodeBytes, _bvhBuffer, 0);

        _bvhNodeCount = bvh.NodeCount;
        _usingBrickPath = false;
        UpdateComputeDescriptors();
        SyncUploadMs = phaseWatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>只在选中子树或工作集变化时更新 GPU 标记，不在相机导航期间遍历像素。</summary>
    internal void SetSceneSelection(VoxelWorld? selection)
    {
        var version = selection?.ContentVersion ?? -1;
        if (!_sceneSelectionDirty && ReferenceEquals(selection, _sceneSelection) && version == _sceneSelectionVersion) return;
        _sceneSelection = selection;
        _sceneSelectionVersion = version;
        _sceneSelectionDirty = false;
        _sceneSelectionActive = selection is { OccupiedVoxelCount: > 0 } && _sceneOrderedCells.Count > 0;
        if (_context is null || _api is null || _voxelBuffer.Handle == 0) return;
        var bytes = PackCells(_sceneOrderedCells);
        for (var index = 0; index < _sceneOrderedCells.Count; index++)
        {
            var cell = _sceneOrderedCells[index];
            var coordinate = new VoxelCoord64(_sceneOrderedOrigin.X + cell.X,
                _sceneOrderedOrigin.Y + cell.Y, _sceneOrderedOrigin.Z + cell.Z);
            uint selected = selection is not null && selection.TryGet(coordinate, out var value) && value == cell.Value ? 1u : 0u;
            MemoryMarshal.Write(bytes.AsSpan(index * PackedCellSize + 36, 4), in selected);
        }
        if (bytes.Length > 0)
        {
            WaitForComputeIdle();
            UploadBytes(bytes, _voxelBuffer, 0);
        }
        _lastParameters = null;
    }

    /// <summary>
    /// 砖路径同步：优先按脏区块增量更新驻留并只上传脏区间；
    /// 首次、结构变化、容量溢出或脏区块超阈值时全量重建驻留并整体上传
    /// </summary>
    private void SyncBrick(VoxelWorld world, long lodSignature, int maximumCells)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hasDirty = world.TryTakeDirty(out var dirty);
        BrickResidency.UploadPlan? plan = null;
        var incremental = false;
        if (ReferenceEquals(_lastSyncSource, world) &&
            _brickResidency is { IsEmpty: false } && hasDirty && dirty.Count <= MaxIncrementalChunks)
        {
            incremental = _brickResidency.TryUpdate(world, dirty, out plan);
        }

        BrickResidency residency;
        if (!incremental)
        {
            LastBrickRejectReason = _brickResidency?.LastRejectReason ?? "first/no-residency";
            residency = _brickResidency = BrickResidency.BuildFull(world);
            plan = null;
        }
        else
        {
            LastBrickRejectReason = string.Empty;
            residency = _brickResidency!;
        }

        LastSyncUsedIncrementalBrick = incremental;

        SyncWorksetBuildMs = watch.Elapsed.TotalMilliseconds;
        SyncPackMs = 0;
        SyncUploadMs = 0;
        if (_context is not null && _api is { } api)
        {
            if (residency.IsEmpty)
            {
                if (_voxelBuffer.Handle != 0)
                {
                    api.DeviceWaitIdle(_device);
                    DestroyBvhBuffer(api);
                    DestroyVoxelBuffer(api);
                    DestroyBrickTableBuffer(api);
                    _bvhNodeCount = 0;
                }
            }
            else if (incremental && plan is not null)
            {
                UploadBrickRanges(plan);
            }
            else
            {
                UploadBrickFull(residency);
            }
        }

        if (hasDirty)
        {
            world.MarkChunksSynced(dirty);
        }

        _usingBrickPath = false;
        _workset = VoxelGpuWorkset.CreateForBricks(world.ContentVersion, residency.Anchor,
            residency.Size, residency.Cells, residency.EmissiveCount);
        _lastSyncSource = world;
        _worksetMaximumCells = maximumCells;
        _worksetLodSignature = lodSignature;
        _worksetGreedyMerge = true;
        _worksetIncludesInterior = false;
        SyncTotalCells = residency.OccupiedCount;
        ResetAccumulation();
    }

    /// <summary>
    /// 全量上传驻留格池与节点池（首次或回退时）
    /// </summary>
    private void UploadBrickFull(BrickResidency residency)
    {
        var api = _api!;
        WaitForComputeIdle();
        var cellBytes = PackCells(residency.Cells);
        var nodeBytes = PackResidencyNodes(residency.Nodes, 0, residency.Nodes.Length);
        var cellRequired = checked((ulong)Math.Max(cellBytes.Length, PackedCellSize));
        if (_voxelBuffer.Handle == 0 || _voxelBufferCapacity < cellRequired)
        {
            api.DeviceWaitIdle(_device);
            DestroyVoxelBuffer(api);
            (_voxelBuffer, _voxelMemory) = CreateBuffer(NextCapacity(cellRequired),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
            _voxelBufferCapacity = NextCapacity(cellRequired);
        }

        var nodeRequired = checked((ulong)Math.Max(nodeBytes.Length, 48));
        if (_bvhBuffer.Handle == 0 || _bvhBufferCapacity < nodeRequired)
        {
            api.DeviceWaitIdle(_device);
            DestroyBvhBuffer(api);
            (_bvhBuffer, _bvhMemory) = CreateBuffer(NextCapacity(nodeRequired),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
            _bvhBufferCapacity = NextCapacity(nodeRequired);
        }

        EnsureBrickTableBuffer(1);
        UploadBytes(cellBytes, _voxelBuffer, 0);
        UploadBytes(nodeBytes, _bvhBuffer, 0);
        _bvhNodeCount = residency.Nodes.Length;
        UpdateComputeDescriptors();
    }

    /// <summary>
    /// 增量上传脏区间：外层节点区间总在计划内，格片与内层子树按脏区块定界
    /// </summary>
    private void UploadBrickRanges(BrickResidency.UploadPlan plan)
    {
        WaitForComputeIdle();
        foreach (var (start, count) in plan.CellRanges)
        {
            if (count <= 0)
            {
                continue;
            }

            var bytes = PackCellsRange(_brickResidency!.Cells, start, count);
            UploadBytes(bytes, _voxelBuffer, (long)start * PackedCellSize);
        }

        foreach (var (start, count) in plan.NodeRanges)
        {
            if (count <= 0)
            {
                continue;
            }

            var bytes = PackResidencyNodes(_brickResidency!.Nodes, start, count);
            UploadBytes(bytes, _bvhBuffer, (long)start * 48);
        }

        _bvhNodeCount = _brickResidency!.Nodes.Length;
    }

    /// <summary>
    /// 打包节点区间为 std430 字节流
    /// </summary>
    private static byte[] PackResidencyNodes(VoxelBrickWorkset.Node[] nodes, int start, int count)
    {
        var bytes = new byte[count * 48];
        for (var index = 0; index < count; index++)
        {
            var node = nodes[start + index];
            var offset = index * 48;
            WriteInt(bytes, offset + 0, node.MinX);
            WriteInt(bytes, offset + 4, node.MinY);
            WriteInt(bytes, offset + 8, node.MinZ);
            WriteInt(bytes, offset + 12, 0);
            WriteInt(bytes, offset + 16, node.MaxX);
            WriteInt(bytes, offset + 20, node.MaxY);
            WriteInt(bytes, offset + 24, node.MaxZ);
            WriteInt(bytes, offset + 28, 0);
            WriteInt(bytes, offset + 32, node.LinkX);
            WriteInt(bytes, offset + 36, node.LinkY);
            WriteInt(bytes, offset + 40, node.LinkZ);
            WriteInt(bytes, offset + 44, node.LinkW);
        }

        return bytes;
    }

    /// <summary>
    /// 把主机端字节上传到设备缓冲指定字节偏移处
    /// </summary>
    /// <remarks>
    /// 暂存缓冲按最大需求扩容后长期复用
    /// 每次上传都等待立即提交栅栏返回，所以连续上传可以安全复用同一块暂存内存
    /// </remarks>
    private void UploadBytes(byte[] bytes, VkBuffer destination, long destinationByteOffset)
    {
        if (bytes.Length == 0 || _context is null || _api is not { } api || _context.IsDeviceLost)
        {
            return;
        }

        EnsureStagingCapacity((ulong)bytes.Length);
        void* mapped = null;
        Check(api.MapMemory(_device, _stagingMemory, 0, (ulong)bytes.Length, 0, &mapped), "映射上传暂存缓冲");
        try
        {
            fixed (byte* source = bytes)
            {
                System.Buffer.MemoryCopy(source, mapped, bytes.Length, bytes.Length);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _stagingMemory);
        }

        _context.ExecuteImmediate(commandBuffer =>
            RecordUploadRange(commandBuffer, _stagingBuffer, destination, bytes.Length, destinationByteOffset));
    }

    /// <summary>
    /// 确保上传暂存缓冲容量足够，不足时扩容复用
    /// </summary>
    /// <param name="required">本次上传需要的字节数</param>
    private void EnsureStagingCapacity(ulong required)
    {
        if (_stagingBuffer.Handle != 0 && _stagingCapacity >= required)
        {
            return;
        }

        var api = _api!;
        WaitForComputeIdle();
        if (_stagingBuffer.Handle != 0)
        {
            api.DestroyBuffer(_device, _stagingBuffer, null);
            api.FreeMemory(_device, _stagingMemory, null);
            _stagingBuffer = default;
            _stagingMemory = default;
            _stagingCapacity = 0;
        }

        _stagingCapacity = NextCapacity(required);
        (_stagingBuffer, _stagingMemory) = CreateBuffer(
            _stagingCapacity,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
    }

    /// <summary>
    /// 等待所有分块计算提交完成并回收环形槽位
    /// </summary>
    /// <remarks>
    /// 描述符集、设备缓冲和输出图像在改写或销毁前必须确认没有命令缓冲仍在引用
    /// 在部分显卡驱动上，改写使用中的描述符会直接导致设备丢失
    /// </remarks>
    private void WaitForComputeIdle()
    {
        if (_context is null || _context.IsDeviceLost)
        {
            return;
        }

        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            if (!_ringInFlight[slot])
            {
                continue;
            }

            _context.WaitAndResetFence(_ringFences[slot]);
            _ringInFlight[slot] = false;
        }
    }

    /// <summary>
    /// 向字节数组写入一个有符号 32 位整数
    /// </summary>
    private static void WriteInt(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// 记录暂存缓冲到目标 Storage Buffer 指定偏移的复制与同步
    /// </summary>
    /// <param name="commandBuffer">当前命令缓冲</param>
    /// <param name="stagingBuffer">主机可见暂存缓冲</param>
    /// <param name="destination">目标设备缓冲</param>
    /// <param name="byteCount">复制字节数</param>
    /// <param name="destinationByteOffset">目标字节偏移</param>
    private void RecordUploadRange(CommandBuffer commandBuffer, VkBuffer stagingBuffer, VkBuffer destination,
        int byteCount, long destinationByteOffset)
    {
        var api = _api!;
        var copy = new BufferCopy(0, (ulong)destinationByteOffset, (ulong)byteCount);
        api.CmdCopyBuffer(commandBuffer, stagingBuffer, destination, 1, in copy);
        BufferMemoryBarrier barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = destination,
            Offset = (ulong)destinationByteOffset,
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
            var type = memoryProperties.MemoryTypes[(int)index];
            // 跳过受保护内存：未启用受保护设备特性时使用它属于无效用法
            if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                (type.PropertyFlags & MemoryPropertyFlags.ProtectedBit) == 0 &&
                (type.PropertyFlags & properties) == properties)
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
    /// 销毁砖表缓冲
    /// </summary>
    private void DestroyBrickTableBuffer(Vk api)
    {
        if (_brickTableBuffer.Handle == 0)
        {
            return;
        }

        api.DestroyBuffer(_device, _brickTableBuffer, null);
        api.FreeMemory(_device, _brickTableMemory, null);
        _brickTableBuffer = default;
        _brickTableMemory = default;
        _brickTableCapacity = 0;
    }

    /// <summary>
    /// 确保砖表缓冲存在且容量足够；砖表项为两个 ivec4（区域最小格 + 占用数、格池起始下标），32 字节
    /// </summary>
    /// <param name="entries">所需砖表项数</param>
    private void EnsureBrickTableBuffer(int entries)
    {
        var api = _api!;
        var required = checked((ulong)Math.Max(entries * 32, 512));
        if (_brickTableBuffer.Handle != 0 && _brickTableCapacity >= required)
        {
            return;
        }

        api.DeviceWaitIdle(_device);
        DestroyBrickTableBuffer(api);
        (_brickTableBuffer, _brickTableMemory) = CreateBuffer(
            NextCapacity(required),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
        _brickTableCapacity = NextCapacity(required);
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
