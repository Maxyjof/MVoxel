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

    /// <summary>
    /// 单次路径追踪 dispatch 覆盖的最大像素样本数，控制单次 GPU 调度时长
    /// </summary>
    private const int PathDispatchPixelBudget = 1_000_000;

    /// <summary>
    /// 单个界面帧最多提交的路径像素样本数：一轮采样会被切成多条横条跨帧推进，
    /// 数值越小界面帧越轻、单位时间的样本吞吐略低
    /// </summary>
    private const int PathStripPixelBudget = 32_768;

    /// <summary>
    /// 分块起点 push constant 的字节数，对应着色器里的 ivec2
    /// </summary>
    private const int TilePushConstantBytes = 8;
    /// <summary>探针体积缓冲的初始容量：3 个 vec4 头 + 若干探针，未上传时全零</summary>
    private const ulong InitialProbeVolumeBytes = 512UL;

    private readonly VulkanWindowContext? _context;
    private readonly Vk? _api;
    private readonly Device _device;
    private readonly VulkanImGuiRenderer? _imguiRenderer;
    private VulkanHardwareRayTracingPipeline? _hardwareRayTracingPipeline;
    // Ray Query 阴影加速：加速结构只服务 TraceShadow，完整着色、后处理与
    // A/B/C 三种太阳方案仍走同一份 Compute 着色器，因此所有效果都保持一致
    private VulkanHardwareRayTracingPipeline? _shadowAcceleration;
    private Pipeline _hardwareShadowPipeline;
    private bool _hardwareShadowsActive;
    private bool? _lastHardwareBackend;
    private bool _hardwareAccelerationDisabledByUser;
    private bool _hardwareShadowPipelineFailed;
    private string? _hardwareShadowFailure;
    private int _shadowAccelerationSeenVersion = -1;
    private int _shadowAccelerationBuiltVersion = -1;
    private bool _shadowAccelerationBuildValid;
    private long _shadowAccelerationSeenAtMs;
    private long _shadowAccelerationRetryAtMs;
    private bool _shadowAccelerationUnsupportedLogged;
    /// <summary>工作集停止变化后等待多久再建 BLAS，避免连续编辑时反复重建</summary>
    private const int ShadowAccelerationStableMs = 320;
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
    // 烘焙探针体积（binding 7）：3 个 vec4 头 + 逐探针 vec4(辐照度 RGB, 天空可见度)
    private VkBuffer _probeVolumeBuffer;
    private DeviceMemory _probeVolumeMemory;
    private ulong _probeVolumeCapacity;
    private float[]? _probeVolumePacked;

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
    /// 是否走砖驻留路径
    /// </summary>
    /// <remarks>
    /// 早先默认关闭的两个原因都已被后续提交修掉：砖内线性扫描换成了区块对齐的两级 BVH，
    /// 格图元也改用与传统路径同一套贪心合并；因此现在默认开启，
    /// 仍可用 <see cref="DisableBrickPath"/> 强制走传统全量路径做 A/B 对比
    /// </remarks>
    internal static bool EnableBrickPath { get; set; } = true;

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
    private ulong _materialTableBufferBytes;
    private const int SurfaceDepositFloats = 32;
    private static readonly ulong MaterialTableBytes = (ulong)MVoxel.Project.MaterialAsset.TableSlots *
        MVoxel.Project.MaterialAsset.FloatsPerSlot * sizeof(float);
    private float[] _surfaceDepositPacked = [];
    private uint[] _surfaceReceiverWords = [];
    private float[] _materialTable = MVoxel.Project.MaterialAsset.PackTable(new Dictionary<int, MVoxel.Project.MaterialAsset>());
    private bool _materialTableUploadPending;
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
    // 7 个 vec4：位置/类型、方向/启用、颜色/强度、范围/聚光、配光、
    // 面积灯右轴/半宽、面积灯上轴/半高
    private const int SceneLightFloatsPerLight = 28;
    private const int SceneLightBufferBytes = SceneLight.Capacity * SceneLightFloatsPerLight * sizeof(float);

    // 分块提交环形队列：每槽拥有独立的参数缓冲、命令缓冲、栅栏与描述符集，
    // 分块一提交就走，槽位复用时才等自己的栅栏，GPU 分块之间不再被 CPU 等待打断
    // 深度与单帧提交上限匹配：一帧内的提交都能落到空槽，帧与帧之间才可能等待
    private const int TileRingDepth = 8;
    private readonly VkBuffer[] _ringParameterBuffers = new VkBuffer[TileRingDepth];
    private readonly DeviceMemory[] _ringParameterMemory = new DeviceMemory[TileRingDepth];
    private readonly DescriptorSet[] _ringSets = new DescriptorSet[TileRingDepth];
    private readonly CommandBuffer[] _ringCommands = new CommandBuffer[TileRingDepth];
    private readonly Fence[] _ringFences = new Fence[TileRingDepth];
    private readonly bool[] _ringInFlight = new bool[TileRingDepth];
    private CommandPool _ringCommandPool;
    private int _ringNext;

    // GPU 时间戳采样：开始与结束各用一条极短命令缓冲，夹住整帧 Compute 提交。
    // 只在性能分析器打开时启用，结束时间戳的栅栏由编辑器异步轮询，不阻塞渲染
    private QueryPool _gpuTimestampPool;
    private CommandBuffer _gpuTimestampEnd;
    private Fence _gpuTimestampFence;
    private bool _gpuTimestampFenceSignaled = true;
    private bool _gpuTimingPending;
    private bool _gpuTimingEndSubmitted;
    private bool _gpuTimingBroken;
    private long _gpuTimingToken;
    private double _lastGpuFrameMs;
    private double _lastGpuSamplingMs;
    private double _lastGpuFinishMs;
    // 采样段结束时间戳是否已经写入本帧的采样命令缓冲；
    // 没写时由结束时间戳缓冲补一条，保证三段查询始终有值
    private bool _gpuSampleEndWritten;

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
    private Image _realtimeHistoryImage;
    private DeviceMemory _realtimeHistoryMemory;
    private ImageView _realtimeHistoryView;
    private Image _realtimeHistoryImageB;
    private DeviceMemory _realtimeHistoryMemoryB;
    private ImageView _realtimeHistoryViewB;
    private bool _realtimeHistoryAInitialized;
    private bool _realtimeHistoryBInitialized;
    private bool _realtimeHistoryReadIsA = true;
    private nint _displayTextureId;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _outputIsShaderReadOnly;
    private bool _accumulationInitialized;
    private bool _clearAccumulationPending = true;
    // 采样已完成但后处理参数变化时的显示刷新请求：只重跑收尾通道，
    // 复用已有 HDR 累积和引导图，不再重新求交
    private bool _displayRefreshPending;
    // 跨帧推进中的采样轮：固定参数快照、横条进度与每像素样本数，
    // 只有整轮跑完才推进样本计数并刷新显示图
    private bool _pathRoundOpen;
    private int _pathRoundNextStrip;
    private int _pathRoundStripHeight;
    private int _pathRoundTotalStrips;
    private int _pathRoundSamplesPerPixel;
    private float[]? _pathRoundParameters;
    private int _pathRoundWidth;
    private int _pathRoundHeight;
    private float[]? _lastParameters;
    private long _realtimeHistorySignature;
    private bool _realtimeHistoryInvalid = true;
    private VoxelGpuWorkset _workset = VoxelGpuWorkset.Empty;
    private IReadOnlyList<VoxelGpuCell> _sceneOrderedCells = Array.Empty<VoxelGpuCell>();
    private VoxelCoord64 _sceneOrderedOrigin;
    private VoxelWorld? _sceneSelection;
    private int _sceneSelectionVersion = -1;
    private bool _sceneSelectionDirty = true;
    private bool _sceneSelectionActive;
    private bool _disposed;
    private Task<Pipeline>? _computePipelineCreation;
    private Task<Pipeline>? _hardwarePipelineCreation;
    private string? _computePipelineError;
    private bool _usingHardwareRayTracing;
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
    internal bool IsAvailable
    {
        get
        {
            CompleteComputePipelineCreation();
            return (_context is null || !_context.IsDeviceLost) &&
                _computePipelineError is null && _computePipeline.Handle != 0 && GpuWorksetUploaded && _bvhBuffer.Handle != 0;
        }
    }

    internal bool IsInitializing => _computePipelineCreation is { IsCompleted: false };
    internal string? InitializationError => _computePipelineError;

    // Only the UI thread adopts the result. The worker owns the returned pipeline
    // until this method or Dispose joins it; the device must outlive that worker.
    private void CompleteComputePipelineCreation(bool wait = false)
    {
        var task = _computePipelineCreation;
        if (task is null || (!wait && !task.IsCompleted)) return;
        _computePipelineCreation = null;
        try
        {
            _computePipeline = task.GetAwaiter().GetResult();
            if (_materialTableUploadPending && !_disposed)
            {
                _materialTableUploadPending = false;
                SetMaterialTable(_materialTable);
            }
        }
        catch (Exception exception)
        {
            if (exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
                _context?.MarkDeviceLost();
            _computePipelineError = exception.Message;
            MVoxelLog.Error("Vulkan Compute 管线后台初始化失败", exception);
        }
    }

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

    /// <summary>当前设备是否支持 ray query 阴影加速</summary>
    internal bool SupportsHardwareRayQuery => _context?.SupportsHardwareRayQuery == true;

    /// <summary>当前帧的太阳阴影是否真的由硬件光追加速</summary>
    internal bool HardwareShadowsActive => _hardwareShadowsActive;

    /// <summary>硬件阴影加速的状态说明，供自动化与状态栏显示</summary>
    internal string HardwareShadowStatus => _context is null ? "无 Vulkan 设备"
        : _hardwareAccelerationDisabledByUser ? "已由用户关闭硬件加速"
        : !_context.SupportsHardwareRayQuery ? "设备未启用 ray query"
        : _hardwareShadowsActive ? "ray query 求交已启用"
        : _hardwareShadowFailure is { Length: > 0 } failure ? $"硬件加速不可用：{failure}"
        : _shadowAcceleration?.LastError is { Length: > 0 } error ? $"加速结构不可用：{error}"
        : _workset.Cells.Count > VulkanHardwareRayTracingPipeline.MaxHardwareRayTracingVoxels
            ? $"工作集 {_workset.Cells.Count:N0} 格超过硬件加速上限 {VulkanHardwareRayTracingPipeline.MaxHardwareRayTracingVoxels:N0}"
            : "等待工作集稳定后建立加速结构";

    /// <summary>
    /// 获取显示纹理有效区域
    /// </summary>
    internal Vector2 DisplayUvMax => Vector2.One;

    /// <summary>
    /// 获取已累积的路径追踪样本数
    /// </summary>
    /// <summary>非光追通道在参数不变后仍继续渲染的帧数上限</summary>
    /// <remarks>
    /// 实时着色改为确定性解析估计后，静止画面逐帧完全一致，不再需要几十帧的
    /// 噪声收敛窗口；保留两帧只是为了给历史图与描述符交换留出稳定的过渡。
    /// </remarks>
    private const int RealtimeConvergeFrameBudget = 2;

    private int _realtimeConvergedFrames;

    internal int AccumulatedSamples { get; private set; }

    /// <summary>
    /// 非光追通道的历史混合当前是否真正生效
    /// </summary>
    /// <remarks>
    /// 时间累积权重非零但历史被判失效时画面不会有任何收敛，状态接口暴露这一项便于核对
    /// </remarks>
    internal bool RealtimeHistoryActive => _realtimeHistoryAInitialized && !_realtimeHistoryInvalid;

    /// <summary>参数不变后已连续渲染的帧数，达到预算即停止重绘</summary>
    internal int RealtimeConvergedFrames => _realtimeConvergedFrames;

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
        using var syncPhase = StartupTimeline.Begin("同步工作集");
        var brickEligible = EnableBrickPath && !DisableBrickPath && maximumCells == 0 && greedyMerge &&
            lodSignature == 0 && !_usingHardwareRayTracing && world.OccupiedVoxelCount <= MaxFullWorksetCells;
        // 真正发生重建才计数，视口性能采样据此区分本帧是否同步过工作集
        SyncCompletedCount++;
        if (brickEligible)
        {
            RefreshEmissiveCatalog(world, true);
            SyncBrick(world, lodSignature, maximumCells);
            UploadEmissiveFaces();
            return;
        }

        _brickResidency = null;
        _usingBrickPath = false;
        var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        var next = VoxelGpuWorkset.Build(world, maximumCells, _activeLod, _lodCamera, greedyMerge, includeInterior,
            out var buildTiming);
        WorksetBuildTiming = buildTiming;
        SyncWorksetBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
        // 索引刷新放在构建之后：工作集已经给出本轮发光体素数，
        // 为零时可以直接清空索引，省掉一次整份世界的扫描
        RefreshEmissiveCatalog(world, maximumCells == 0 && lodSignature == 0, next.EmissiveCount);
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
        if (IsInitializing)
        {
            _materialTable = (float[])table.Clone();
            _materialTableUploadPending = true;
            ResetAccumulation();
            return;
        }
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
    /// 上传投射体数据到材质表之后的独立区域。原生 65536 个材质槽保持原样；
    /// 不截断投射体数量，缓冲不足时扩容，设备范围不足时明确报错。
    /// </summary>
    internal void SetSurfaceDeposits(float[] packed, SurfaceReceiverLayerTable? receiverTable = null)
    {
        ArgumentNullException.ThrowIfNull(packed);
        if (packed.Length % SurfaceDepositFloats != 0) throw new ArgumentException("表面投射数据长度无效", nameof(packed));
        if (receiverTable is not null && receiverTable.Origin != _workset.Origin)
            throw new ArgumentException("接收图层表原点与当前 GPU 工作集不一致", nameof(receiverTable));
        var receiverWords = receiverTable?.Words ?? [];
        if (MemoryMarshal.AsBytes(_surfaceDepositPacked.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(packed.AsSpan())) &&
            _surfaceReceiverWords.AsSpan().SequenceEqual(receiverWords)) return;
        if (IsInitializing) throw new InvalidOperationException("渲染管线尚在初始化");
        if (_context is null || _api is not { } api || _materialTableBuffer.Handle == 0)
        {
            _surfaceDepositPacked = (float[])packed.Clone();
            _surfaceReceiverWords = (uint[])receiverWords.Clone();
            ResetAccumulation();
            return;
        }

        var required = MaterialTableBytes + (ulong)Math.Max(packed.Length + (long)receiverWords.Length, SurfaceDepositFloats) * sizeof(float);
        PhysicalDeviceProperties properties = default;
        api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (required > properties.Limits.MaxStorageBufferRange)
            throw new InvalidOperationException("表面投射组件数据超出设备存储缓冲区上限");

        WaitForComputeIdle();
        if (required > _materialTableBufferBytes)
        {
            // Allocate and fill before releasing the old table, so an allocation error
            // leaves all existing material data and descriptors intact.
            var newBytes = Math.Min(NextCapacity(required), properties.Limits.MaxStorageBufferRange);
            var (newBuffer, newMemory) = CreateBuffer(newBytes, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            try
            {
                WriteMaterialBuffer(api, newMemory, _materialTable, packed, receiverWords);
            }
            catch
            {
                api.DestroyBuffer(_device, newBuffer, null);
                api.FreeMemory(_device, newMemory, null);
                throw;
            }
            api.DestroyBuffer(_device, _materialTableBuffer, null);
            api.FreeMemory(_device, _materialTableMemory, null);
            _materialTableBuffer = newBuffer;
            _materialTableMemory = newMemory;
            _materialTableBufferBytes = newBytes;
            UpdateComputeDescriptors();
        }
        else if (packed.Length > 0 || receiverWords.Length > 0)
        {
            WriteMaterialBuffer(api, _materialTableMemory, null, packed, receiverWords);
        }
        _surfaceDepositPacked = (float[])packed.Clone();
        _surfaceReceiverWords = (uint[])receiverWords.Clone();
        ResetAccumulation();
        _lastParameters = null;
        _realtimeHistoryInvalid = true;
    }

    private void WriteMaterialBuffer(Vk api, DeviceMemory memory, float[]? materials, float[] deposits, uint[] receiverWords)
    {
        void* mapped = null;
        var offset = materials is null ? MaterialTableBytes : 0UL;
        var bytes = (materials is null ? 0UL : MaterialTableBytes) + ((ulong)deposits.Length + (ulong)receiverWords.Length) * sizeof(float);
        if (bytes == 0) return;
        Check(api.MapMemory(_device, memory, offset, bytes, 0, &mapped), "映射材质与表面投射缓冲");
        try
        {
            if (materials is not null)
            {
                fixed (float* source = materials)
                    System.Buffer.MemoryCopy(source, mapped, (long)MaterialTableBytes, (long)MaterialTableBytes);
            }
            if (deposits.Length > 0)
            {
                var destination = materials is null ? mapped : (byte*)mapped + MaterialTableBytes;
                var depositBytes = (long)deposits.Length * sizeof(float);
                fixed (float* source = deposits)
                    System.Buffer.MemoryCopy(source, destination, depositBytes, depositBytes);
            }
            if (receiverWords.Length > 0)
            {
                var destination = (byte*)mapped + (materials is null ? 0UL : MaterialTableBytes) + (ulong)deposits.Length * sizeof(float);
                var receiverBytes = (long)receiverWords.Length * sizeof(uint);
                fixed (uint* source = receiverWords)
                    System.Buffer.MemoryCopy(source, destination, receiverBytes, receiverBytes);
            }
        }
        finally { api.UnmapMemory(_device, memory); }
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
    /// 把 CPU 烘焙好的探针体积上传到 GPU，供实时通道做三线性辐照度取样
    /// </summary>
    /// <param name="packed">3 个 vec4 头 + 逐探针 vec4 的浮点数组；null 或空表示清除</param>
    /// <remarks>
    /// 数据变化只发生在烘焙完成或探针体积参数改变时，不逐帧上传。
    /// 写入前等待设备空闲，避免 GPU 正在读取时改写主机可见内存。
    /// </remarks>
    internal void SetProbeVolume(float[]? packed)
    {
        _probeVolumePacked = packed;
        if (_context is null || _api is not { } api || _context.IsDeviceLost ||
            _probeVolumeBuffer.Handle == 0) return;

        var sourceBytes = (ulong)((packed?.Length ?? 0) * sizeof(float));
        var writeBytes = Math.Max(sourceBytes, 48UL);
        EnsureProbeVolumeBuffer(writeBytes);
        api.DeviceWaitIdle(_device);
        void* mapped = null;
        Check(api.MapMemory(_device, _probeVolumeMemory, 0, writeBytes, 0, &mapped), "映射探针体积缓冲");
        try
        {
            if (packed is { Length: > 0 })
            {
                fixed (float* source = packed)
                {
                    System.Buffer.MemoryCopy(source, mapped, (long)writeBytes, (long)sourceBytes);
                }
            }
            else
            {
                var zeros = new float[12];
                fixed (float* source = zeros)
                {
                    System.Buffer.MemoryCopy(source, mapped, (long)writeBytes, 48L);
                }
            }
        }
        finally
        {
            api.UnmapMemory(_device, _probeVolumeMemory);
        }

        // 探针数据不在参数块里，仅清计数会让参数比较误判为相同，
        // 下一帧把旧辐照度混进新的渐进太阳累积；同时使旧参数签名失效。
        _realtimeHistoryInvalid = true;
        _realtimeConvergedFrames = 0;
        _lastParameters = null;
        UpdateComputeDescriptors();
    }

    private void ZeroProbeVolumeHeader()
    {
        if (_api is not { } api || _probeVolumeBuffer.Handle == 0) return;
        void* mapped = null;
        Check(api.MapMemory(_device, _probeVolumeMemory, 0, 48UL, 0, &mapped), "初始化探针体积缓冲");
        try
        {
            var zeros = new float[12];
            fixed (float* source = zeros)
            {
                System.Buffer.MemoryCopy(source, mapped, 48L, 48L);
            }
        }
        finally
        {
            api.UnmapMemory(_device, _probeVolumeMemory);
        }
    }

    private void EnsureProbeVolumeBuffer(ulong required)
    {
        if (_api is not { } api || _probeVolumeBuffer.Handle == 0) return;
        var needed = NextCapacity(Math.Max(required, InitialProbeVolumeBytes));
        if (_probeVolumeCapacity >= needed) return;
        api.DeviceWaitIdle(_device);
        DestroyProbeVolumeBuffer(api);
        (_probeVolumeBuffer, _probeVolumeMemory) = CreateBuffer(
            needed,
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _probeVolumeCapacity = needed;
    }

    private void DestroyProbeVolumeBuffer(Vk api)
    {
        if (_probeVolumeBuffer.Handle == 0) return;
        api.DestroyBuffer(_device, _probeVolumeBuffer, null);
        api.FreeMemory(_device, _probeVolumeMemory, null);
        _probeVolumeBuffer = default;
        _probeVolumeMemory = default;
        _probeVolumeCapacity = 0;
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
            packed[offset + 17] = 0F;
            packed[offset + 18] = 0F;
            packed[offset + 19] = 0F;
            packed[offset + 20] = light.Right.X;
            packed[offset + 21] = light.Right.Y;
            packed[offset + 22] = light.Right.Z;
            packed[offset + 23] = light.HalfWidth;
            packed[offset + 24] = light.Up.X;
            packed[offset + 25] = light.Up.Y;
            packed[offset + 26] = light.Up.Z;
            packed[offset + 27] = light.HalfHeight;
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
    /// 最近一次工作集构建的分阶段耗时，用于定位构建热点
    /// </summary>
    internal VoxelGpuWorkset.VoxelWorksetBuildTiming WorksetBuildTiming { get; private set; }

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
    /// 获取队列异步提交累计次数，供视口性能采样换算每帧提交量
    /// </summary>
    internal long SubmitCount => _context?.AsyncSubmitCount ?? 0;

    /// <summary>
    /// 获取栅栏等待累计毫秒，供视口性能采样定位 CPU 等待 GPU 的耗时
    /// </summary>
    internal double FenceWaitTotalMs => _context?.FenceWaitMs ?? 0;

    /// <summary>
    /// 获取工作集同步的合计耗时（构建、BVH、打包与上传）
    /// </summary>
    internal double SyncTotalMs => SyncWorksetBuildMs + SyncBvhBuildMs + SyncPackMs + SyncUploadMs;

    /// <summary>
    /// 获取工作集累计重建次数，供视口性能采样判断某一帧是否真的重建过工作集
    /// </summary>
    internal int SyncCompletedCount { get; private set; }

    /// <summary>
    /// 是否启用 GPU 时间戳采样，由性能分析器在渲染前设置
    /// </summary>
    internal bool GpuProfilingEnabled { get; set; }

    /// <summary>
    /// 当前帧标识，GPU 结果回读后据此写回对应帧样本
    /// </summary>
    internal long GpuTimingToken { get; set; }

    /// <summary>
    /// 最近一次成功回读的 GPU 计算耗时毫秒
    /// </summary>
    internal double LastGpuFrameMs => _lastGpuFrameMs;

    /// <summary>
    /// 最近一次成功回读的采样段耗时毫秒：路径求交与着色，不含收尾通道
    /// </summary>
    internal double LastGpuSamplingMs => _lastGpuSamplingMs;

    /// <summary>
    /// 最近一次成功回读的收尾段耗时毫秒：降噪、Bloom、选中描边与 FXAA
    /// </summary>
    internal double LastGpuFinishMs => _lastGpuFinishMs;

    /// <summary>
    /// 当前设备是否支持 GPU 时间戳采样
    /// </summary>
    internal bool GpuTimingSupported => _context?.SupportsTimestamps == true && !_gpuTimingBroken;

    /// <summary>
    /// 重置渐进渲染累积
    /// </summary>
    internal void ResetAccumulation()
    {
        AccumulatedSamples = 0;
        _realtimeConvergedFrames = 0;
        _lastParameters = null;
        _clearAccumulationPending = true;
        _realtimeHistoryInvalid = true;
        _displayRefreshPending = false;
        ClosePathRound();
    }

    /// <summary>
    /// 结束进行中的采样轮，下一次采样会从新一轮的横条零重新开始
    /// </summary>
    private void ClosePathRound()
    {
        _pathRoundOpen = false;
        _pathRoundNextStrip = 0;
        _pathRoundTotalStrips = 0;
        _pathRoundParameters = null;
    }

    /// <summary>
    /// 请求只在已有采样结果上重跑收尾通道，用于曝光、色调映射、降噪和 Bloom
    /// 这类只影响显示的参数变化，避免丢弃已经完成的路径样本
    /// </summary>
    internal void RequestDisplayRefresh()
    {
        _displayRefreshPending = true;
    }

    /// <summary>
    /// 采样达到上限后把累积计数收敛到新上限，用于降低样本上限时保留已有画面
    /// </summary>
    /// <param name="maximumSamples">新的累积样本上限</param>
    internal void LimitAccumulatedSamples(int maximumSamples)
    {
        var limit = Math.Max(0, maximumSamples);
        if (AccumulatedSamples > limit) AccumulatedSamples = limit;
    }

    /// <summary>
    /// 尝试绘制 Vulkan 场景视图
    /// </summary>
    internal bool RenderSceneView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        _usingHardwareRayTracing = false;
        if (_context?.IsDeviceLost == true) return false;
        // 实时模式会把累积图当作 Bloom 的 HDR 输入整张改写，
        // 进行中的采样轮必须先作废，否则下次继续采样会读到被覆盖的旧样本
        if (_pathRoundOpen)
        {
            ClosePathRound();
            _clearAccumulationPending = true;
            AccumulatedSamples = 0;
        }

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
        // 硬件加速只替换 TraceShadow 的求交后端，完整着色、光源、自发光、
        // 相机效果与三种太阳方案都走同一份 Compute 着色器，因此这里不再有
        // “基础颜色专用”分支，也就不会因为切到硬件而漏掉任何效果
        return DispatchCompute(settings, frameState, widthPixels, heightPixels, pathMode: true);
    }

    /// <summary>
    /// 尝试使用当前 Vulkan 累积结果执行呈现
    /// </summary>
    internal bool Present(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        if (DisplayTextureId == IntPtr.Zero) return false;
        // 采样已完成时，只有后处理参数变化才需要刷新；其余情况直接复用现有纹理
        return !_displayRefreshPending ||
               DispatchDisplayRefresh(settings, frameState, widthPixels, heightPixels);
    }

    /// <summary>
    /// 构造本帧参数块并补齐工作集、环境贴图和场景光源等运行期字段
    /// </summary>
    /// <param name="settings">当前渲染设置</param>
    /// <param name="frameState">相机与工作集状态</param>
    private float[] BuildParameters(RenderSettings settings, in VoxelRenderParameters.FrameState frameState)
    {
        var parameters = VoxelRenderParameters.Build(settings, frameState, frameState.FrameIndex);
        parameters[VoxelRenderParameters.SurfaceDepositCount] = _surfaceDepositPacked.Length / SurfaceDepositFloats;
        parameters[VoxelRenderParameters.SurfaceReceiverSlots] = _surfaceReceiverWords.Length / SurfaceReceiverLayerTable.WordsPerSlot;
        parameters[VoxelRenderParameters.VoxelCount] = _workset.Cells.Count;
        parameters[VoxelRenderParameters.BvhNodeCount] = _bvhNodeCount;
        parameters[VoxelRenderParameters.BrickMode] = _usingBrickPath ? 1F : 0F;
        parameters[VoxelRenderParameters.EnvEnabled] = HasEnvironment ? 1F : 0F;
        parameters[VoxelRenderParameters.EnvWidth] = _environmentWidth;
        parameters[VoxelRenderParameters.EnvHeight] = _environmentHeight;
        parameters[VoxelRenderParameters.EnvIntensity] = _environmentIntensity;
        parameters[VoxelRenderParameters.SceneLightCount] = _sceneLightCount;
        return parameters;
    }

    /// <summary>
    /// 只重跑收尾通道：用已有 HDR 累积和引导图刷新显示，不重新求交也不推进样本计数
    /// </summary>
    /// <param name="settings">当前渲染设置</param>
    /// <param name="frameState">相机与工作集状态</param>
    /// <param name="widthPixels">输出宽度</param>
    /// <param name="heightPixels">输出高度</param>
    /// <returns>是否成功提交显示刷新</returns>
    private bool DispatchDisplayRefresh(RenderSettings settings,
        in VoxelRenderParameters.FrameState frameState, int widthPixels, int heightPixels)
    {
        _displayRefreshPending = false;
        // 硬件光追保存的是单帧成品，没有可供重新后处理的 HDR 累积
        if (_usingHardwareRayTracing) return true;
        if (!IsAvailable || _context is null || _api is not { } api || widthPixels <= 0 || heightPixels <= 0)
        {
            return false;
        }

        // 尺寸变化会让累积失效，此时保留上一张纹理，交给下一轮正常采样重建
        if (_accumulationImage.Handle == 0 || _outputImage.Handle == 0 ||
            _outputWidth != (uint)widthPixels || _outputHeight != (uint)heightPixels)
        {
            return true;
        }

        if (!_accumulationInitialized || AccumulatedSamples <= 0) return true;
        // 一轮采样还没跑完时累积图里是新旧样本混在一起的，不能拿来刷新显示
        if (_pathRoundOpen) return true;
        DispatchDisplayPass(api, BuildParameters(settings, frameState), widthPixels, heightPixels);
        return true;
    }

    /// <summary>
    /// 提交一次纯显示刷新通道：读取现有 HDR 累积与引导图，按当前后处理参数写回输出图像
    /// </summary>
    /// <param name="api">Vulkan API</param>
    /// <param name="parameters">渲染参数块</param>
    /// <param name="widthPixels">输出宽度</param>
    /// <param name="heightPixels">输出高度</param>
    private void DispatchDisplayPass(Vk api, float[] parameters, int widthPixels, int heightPixels)
    {
        parameters[VoxelRenderParameters.DisplayPass] = 1F;
        var slot = AcquireRingSlot();
        UploadParameters(slot, parameters);
        var commandBuffer = _ringCommands[slot];
        TransitionOutputToGeneral(commandBuffer);
        TransitionAccumulationToGeneral(commandBuffer);
        TransitionGuideToGeneral(commandBuffer);
        BindComputeTile(commandBuffer, _ringSets[slot], 0, 0);
        var groupX = ((uint)widthPixels + 7U) / 8U;
        var groupY = ((uint)heightPixels + 7U) / 8U;
        api.CmdDispatch(commandBuffer, groupX, groupY, 1);
        LastDispatchCount++;
        LastWorkgroupCount += (long)groupX * (long)groupY;
        TransitionOutputToShaderRead(commandBuffer);
        CommitRingSlot(slot);
        parameters[VoxelRenderParameters.DisplayPass] = 0F;
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
        // A pending vkCreateComputePipelines uses this device and pipeline layout.
        // Join before destroying either, including on the device-lost path.
        CompleteComputePipelineCreation(wait: true);
        CompleteHardwarePipelineCreation(wait: true);
        if (_api is { } api)
        {
            // 设备已丢失后，继续等待队列或逐项销毁原生资源可能再次触发驱动异常。
            // 此时放弃管线的原生清理，最终由 Vulkan 设备销毁统一回收。
            if (_context?.IsDeviceLost == true)
            {
                _hardwareRayTracingPipeline = null;
                _shadowAcceleration = null;
                _hardwareShadowPipeline = default;
                // 只清理纯托管的句柄记录，不再调用故障设备上的描述符释放
                _displayTextureId = IntPtr.Zero;
                return;
            }

            api.DeviceWaitIdle(_device);
            _hardwareRayTracingPipeline?.Dispose();
            _hardwareRayTracingPipeline = null;
            _shadowAcceleration?.Dispose();
            _shadowAcceleration = null;
            if (_hardwareShadowPipeline.Handle != 0)
            {
                api.DestroyPipeline(_device, _hardwareShadowPipeline, null);
                _hardwareShadowPipeline = default;
            }
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

            if (_gpuTimestampPool.Handle != 0) api.DestroyQueryPool(_device, _gpuTimestampPool, null);
            if (_gpuTimestampEnd.Handle != 0)
            {
                var timestampBuffer = _gpuTimestampEnd;
                api.FreeCommandBuffers(_device, _ringCommandPool, 1, &timestampBuffer);
            }

            if (_gpuTimestampFence.Handle != 0) api.DestroyFence(_device, _gpuTimestampFence, null);
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

            DestroyProbeVolumeBuffer(api);
            DestroySunDepthBuffer();

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
            var parameterBytes = (ulong)(VoxelRenderParameters.SlotCount * 4 * sizeof(float));
            for (var slot = 0; slot < TileRingDepth; slot++)
            {
                (_ringParameterBuffers[slot], _ringParameterMemory[slot]) = CreateBuffer(
                    parameterBytes,
                    BufferUsageFlags.UniformBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }

            CreateRingCommands();
            if (_context is not null)
            {
                var tableBytes = MaterialTableBytes + SurfaceDepositFloats * sizeof(float);
                _materialTableBufferBytes = tableBytes;
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
                // 探针体积缓冲恒存在，头 12 个 float 全零表示“场景里没有探针”
                (_probeVolumeBuffer, _probeVolumeMemory) = CreateBuffer(
                    InitialProbeVolumeBytes,
                    BufferUsageFlags.StorageBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                _probeVolumeCapacity = InitialProbeVolumeBytes;
                ZeroProbeVolumeHeader();
            }

            EnsureSunDepthBuffer(16);
            _computePipelineLayout = CreateComputePipelineLayout();
            _computePipelineCreation = Task.Run(CreateComputePipeline);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private Pipeline CreateComputePipeline()
    {
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
                Layout = _computePipelineLayout,
                BasePipelineIndex = -1
            };
            Check(_api!.CreateComputePipelines(_device, default, 1, in createInfo, null, out var pipeline),
                "创建 Vulkan 体素 Compute 管线");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
            _api!.DestroyShaderModule(_device, shaderModule, null);
        }
    }

    /// <summary>
    /// 创建 Ray Query 阴影加速使用 Compute 管线变体
    /// </summary>
    /// <remarks>
    /// 和软件管线共用同一份着色器源码与管线布局，只在编译期打开 P_HW_RT，
    /// 让 TraceShadow 改走 rayQueryEXT；其余着色、后处理与 A/B/C 阴影方案完全一致
    /// </remarks>
    private void EnsureHardwareShadowPipeline()
    {
        CompleteHardwarePipelineCreation();
        if (_hardwareShadowPipeline.Handle != 0 || _hardwareShadowPipelineFailed) return;
        if (_context?.SupportsHardwareRayQuery != true || _computePipelineLayout.Handle == 0 || _api is null) return;
        _hardwarePipelineCreation ??= Task.Run(CreateHardwareShadowPipeline);
    }

    private void CompleteHardwarePipelineCreation(bool wait = false)
    {
        var task = _hardwarePipelineCreation;
        if (task is null || (!wait && !task.IsCompleted)) return;
        _hardwarePipelineCreation = null;
        try
        {
            _hardwareShadowPipeline = task.GetAwaiter().GetResult();
            MVoxelLog.Info("硬件光追阴影加速已就绪，太阳阴影改由 ray query 求交");
        }
        catch (Exception exception)
        {
            if (_context?.IsDeviceLost == true || exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
            {
                _context?.MarkDeviceLost();
                if (!wait) throw;
            }
            _hardwareShadowPipelineFailed = true;
            _hardwareShadowFailure = exception.Message;
            _hardwareShadowPipeline = default;
            MVoxelLog.Warn($"硬件光追阴影管线创建失败，保持 Compute 路径：{exception.Message}");
        }
    }

    private Pipeline CreateHardwareShadowPipeline()
    {
        var words = VulkanShaderCompiler.Compile(HardwareSceneComputeShader,
            ShaderKind.ComputeShader, "MVoxel.Scene.rt.comp", rayTracing: true,
            vulkan11: _context!.HardwareShadersTargetVulkan11);
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
                Layout = _computePipelineLayout,
                BasePipelineIndex = -1
            };
            Check(_api!.CreateComputePipelines(_device, default, 1, in createInfo, null, out var pipeline),
                "创建 Ray Query 阴影加速管线");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
            _api!.DestroyShaderModule(_device, shaderModule, null);
        }
    }

    /// <summary>检查当前 Vulkan 设备的存储图像格式能力，不按显卡厂商推断支持情况。</summary>
    private void ValidateComputeImageFormats()
    {
        if (_context is null || _api is null) return;
        PhysicalDeviceProperties deviceProperties = default;
        _api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &deviceProperties);
        // binding 0、1、5、6、7、9、10、14 共 8 个存储缓冲；3 为 uniform 缓冲，
        // 2、4、8、11、12 共 5 个存储图；仅硬件变体再加一个加速结构。
        // 低于限制时提前给出明确错误并交由上层回退，不能创建无效布局。
        uint resourceCount = _context.SupportsHardwareRayQuery ? 15U : 14U;
        var limits = deviceProperties.Limits;
        if (limits.MaxPerStageDescriptorStorageBuffers < 8 || limits.MaxDescriptorSetStorageBuffers < 8 ||
            limits.MaxPerStageDescriptorUniformBuffers < 1 || limits.MaxDescriptorSetUniformBuffers < 1 ||
            limits.MaxUniformBufferRange < VoxelRenderParameters.SlotCount * 4 * sizeof(float) ||
            limits.MaxPerStageDescriptorStorageImages < 5 || limits.MaxDescriptorSetStorageImages < 5 ||
            limits.MaxPerStageResources < resourceCount)
            throw new InvalidOperationException($"Vulkan 设备 {_context.DeviceName} 的 Compute 描述符上限不足：需要 8 个存储缓冲、1 个 uniform 缓冲、5 个存储图和 {resourceCount} 个阶段资源；当前缓冲 {limits.MaxPerStageDescriptorStorageBuffers}/{limits.MaxDescriptorSetStorageBuffers}，存储图 {limits.MaxPerStageDescriptorStorageImages}/{limits.MaxDescriptorSetStorageImages}，阶段资源 {limits.MaxPerStageResources}");
        foreach (var (format, required) in new[]
        {
            (Format.R8G8B8A8Unorm, FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit),
            (Format.R16G16B16A16Sfloat, FormatFeatureFlags.StorageImageBit),
            (Format.R16G16B16A16Sfloat, FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit),
            (Format.R32G32B32A32Sfloat, FormatFeatureFlags.StorageImageBit),
        })
        {
            _api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, out var properties);
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new InvalidOperationException($"Vulkan 设备 {_context.DeviceName} 不支持所需图像格式 {format}（需要 {required}）");
        }
    }

    /// <summary>
    /// 决定本帧太阳阴影是否走硬件 Ray Query，并在工作集稳定后建立加速结构
    /// </summary>
    /// <remarks>
    /// 加速结构要整份重建，不能在每次笔刷提交时都做：只有工作集版本停下
    /// 约 320 毫秒才动手，连续编辑期间继续用 Compute 的软件 BVH，
    /// 因此默认开启不会把编辑手感拖慢。工作集超过硬件上限时同样自动回退
    /// </remarks>
    private void UpdateShadowAcceleration(RenderSettings settings)
    {
        _hardwareShadowsActive = false;
        _hardwareAccelerationDisabledByUser = settings.ForceComputePath ||
            (!settings.HardwareRayTracingAuto && !settings.UseHardwareRayTracing);
        if (_hardwareAccelerationDisabledByUser || _context is null || !_context.SupportsHardwareRayQuery) return;
        // 砖驻留包含预留槽，其索引必须与 GPU 池一致。
        var hardwareCellCount = _brickResidency?.Cells.Length ?? _sceneOrderedCells.Count;
        if (_usingBrickPath || hardwareCellCount == 0 ||
            hardwareCellCount > VulkanHardwareRayTracingPipeline.MaxHardwareRayTracingVoxels)
        {
            return;
        }

        // 不同世界/LOD 可以有相同 ContentVersion；以工作集重建序号区分实际几何。
        var version = SyncCompletedCount;
        var now = Environment.TickCount64;
        if (_shadowAccelerationSeenVersion != version)
        {
            _shadowAccelerationSeenVersion = version;
            _shadowAccelerationSeenAtMs = now;
            return;
        }

        // 显存压力可能只是暂时的；保持软件渲染期间定期重试，设备容量等固定失败则不重试。
        if (_shadowAccelerationBuiltVersion == version && !_shadowAccelerationBuildValid &&
            _shadowAccelerationRetryAtMs != 0 && now >= _shadowAccelerationRetryAtMs)
        {
            _shadowAccelerationBuiltVersion = -1;
            _shadowAccelerationRetryAtMs = 0;
        }
        if (_shadowAccelerationBuiltVersion != version)
        {
            if (now - _shadowAccelerationSeenAtMs < ShadowAccelerationStableMs) return;
            if (_imguiRenderer is null) return;
            WaitForComputeIdle();
            _shadowAccelerationBuildValid = false;
            _shadowAccelerationRetryAtMs = 0;
            if (!_hardwareShadowPipelineFailed) _hardwareShadowFailure = null;
            try
            {
                _shadowAcceleration ??= new VulkanHardwareRayTracingPipeline(_context, _imguiRenderer);
            }
            catch (Exception exception) when (!_context.IsDeviceLost &&
                !exception.ToString().Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase))
            {
                _shadowAccelerationBuiltVersion = version;
                _hardwareShadowFailure = exception.Message;
                if (IsTransientHardwareMemoryFailure(exception.ToString()))
                    _shadowAccelerationRetryAtMs = now + 5000;
                MVoxelLog.Warn($"初始化硬件阴影失败，回退软件 BVH：{exception.Message}");
                return;
            }
            // 空槽在增量编辑后可能保留旧数据：快照清除空槽，同时保留所有有效格的索引。
            IReadOnlyList<VoxelGpuCell> hardwareCells;
            try
            {
                hardwareCells = _brickResidency is { } residency
                    ? residency.CreateHardwareCellSnapshot() : _sceneOrderedCells;
            }
            catch (OutOfMemoryException exception)
            {
                _shadowAccelerationBuiltVersion = version;
                _shadowAccelerationRetryAtMs = now + 5000;
                _hardwareShadowFailure = "硬件求交工作集快照内存不足，暂用软件 BVH";
                MVoxelLog.Warn($"{_hardwareShadowFailure}：{exception.Message}");
                return;
            }
            if (!_shadowAcceleration.EnsureShadowAcceleration(hardwareCells, version))
            {
                // 记住失败版本避免逐帧重建；临时内存不足会延迟重试。
                _shadowAccelerationBuiltVersion = version;
                if (IsTransientHardwareMemoryFailure(_shadowAcceleration.LastError))
                    _shadowAccelerationRetryAtMs = now + 5000;
                if (!_shadowAccelerationUnsupportedLogged)
                {
                    _shadowAccelerationUnsupportedLogged = true;
                    MVoxelLog.Warn($"硬件阴影加速不可用：{_shadowAcceleration.LastError ?? "未知原因"}");
                }
                return;
            }
            _shadowAccelerationBuiltVersion = version;
            _shadowAccelerationBuildValid = true;
            if (!_hardwareShadowPipelineFailed) _hardwareShadowFailure = null;
            // 加速结构句柄变了，binding 13 必须重写
            UpdateComputeDescriptors();
        }

        if (!_shadowAccelerationBuildValid || _context.IsDeviceLost) return;
        EnsureHardwareShadowPipeline();
        _hardwareShadowsActive = _hardwareShadowPipeline.Handle != 0 &&
            _shadowAcceleration?.HasAccelerationStructure == true;
    }

    private static bool IsTransientHardwareMemoryFailure(string? error) => error is not null &&
        (error.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("ErrorOutOfHostMemory", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase));

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
        UpdateShadowAcceleration(settings);
        bool backendChanged = _lastHardwareBackend.HasValue && _lastHardwareBackend.Value != _hardwareShadowsActive;
        _lastHardwareBackend = _hardwareShadowsActive;
        if (backendChanged)
        {
            // 软硬求交的边界精度可能不同，不能将两种结果混入同一渐进帧。
            ResetAccumulation();
            _sunDepthKey = null;
            _sunReconstructionKey = null;
        }
        var parameters = BuildParameters(settings, frameState);
        if (backendChanged)
        {
            // FrameState 在进入管线前生成，此时其旧样本计数已经失效。
            parameters[VoxelRenderParameters.Frame] = 0F;
            parameters[VoxelRenderParameters.Accumulated] = 0F;
        }
        PrepareSunDepthMap(settings, parameters);
        // B/C 可能从资源回退恢复，或反向进入回退；路径采样不能混合两种估计器。
        if (pathMode && _lastParameters is not null &&
            _lastParameters[VoxelRenderParameters.SunShadowMethod] == parameters[VoxelRenderParameters.SunShadowMethod] &&
            (_lastParameters[VoxelRenderParameters.SunReconstructionAvailable] != parameters[VoxelRenderParameters.SunReconstructionAvailable] ||
             (_lastParameters[VoxelRenderParameters.SunMapRadius] > 0F) != (parameters[VoxelRenderParameters.SunMapRadius] > 0F)))
        {
            ResetAccumulation();
            parameters[VoxelRenderParameters.Frame] = 0F;
            parameters[VoxelRenderParameters.Accumulated] = 0F;
        }
        bool selectionActive = !pathMode && frameState.ScenePreview && _sceneSelectionActive;
        parameters[VoxelRenderParameters.Reserved2] = selectionActive ? 1F : 0F;
        // 时间混合权重为零时历史读写对画面没有任何影响，整段历史机制都可以跳过：
        // 不再每帧全图清零、不再交换读写角色，也不再每帧重写描述符
        bool historyUsed = !pathMode && parameters[VoxelRenderParameters.RealtimeTemporalWeight] > 0F;
        bool historyCleared = false;
        if (!pathMode)
        {
            if (!historyUsed)
            {
                // 历史长期未使用，下次真正启用前必须先清零，避免读到过期内容
                _realtimeHistoryInvalid = true;
            }
            else
            {
                var realtimeSignature = ComputeRealtimeHistorySignature(parameters, widthPixels, heightPixels, _workset.SourceVersion);
                if (_realtimeHistorySignature != realtimeSignature)
                {
                    _realtimeHistorySignature = realtimeSignature;
                    _realtimeHistoryInvalid = true;
                }

                if (_realtimeHistoryInvalid)
                {
                    ClearRealtimeHistory();
                    historyCleared = true;
                }
            }

            // 参数不变时继续重绘的帧数：确定性方案只需两帧稳定描述符与历史图；
            // 渐进太阳阴影则要跑满用户设定的累积帧数，静止后才能收敛半影
            var convergeBudget = RealtimeConvergeFrameBudget;
            if (!frameState.ScenePreview && settings.SunEnabled &&
                settings.SunShadowMethod == SunShadowMethod.Progressive && settings.ShadowSoftness > 0.0001F)
            {
                convergeBudget = Math.Max(convergeBudget, settings.SunAccumulationFrames);
            }

            if (!historyCleared && ParametersEqualForDisplay(parameters) &&
                _realtimeConvergedFrames >= convergeBudget)
            {
                return true;
            }

            if (!historyCleared && ParametersEqualForDisplay(parameters)) _realtimeConvergedFrames++;
            else _realtimeConvergedFrames = 0;
            // 已累积帧数（不含本帧），着色器按 1/(n+1) 混合上一帧结果
            parameters[VoxelRenderParameters.SunRealtimeFrames] = _realtimeConvergedFrames;

            if (historyUsed && !historyCleared)
            {
                // 描述符由整个环形队列共享，交换历史读写角色前必须确认没有命令缓冲仍在引用
                WaitForComputeIdle();
                _realtimeHistoryReadIsA = !_realtimeHistoryReadIsA;
                UpdateComputeDescriptors();
            }
        }

        // 实时模式整帧一次调度；路径追踪按像素样本预算切成横条跨帧推进
        bool realtimePost = !pathMode && !frameState.ShowOutlines;
        bool bloomActive = settings.BloomEnabled && settings.BloomIntensity > 0F &&
                           (pathMode || realtimePost);
        // 渲染窗口的非光追通道恒走收尾通道：采样阶段只写线性 HDR 与导引图，
        // 降噪、泛光与色调映射都在收尾通道完成，对应 Render 视图的出片顺序
        bool realtimeFinish = !pathMode && !frameState.ScenePreview;
        bool roundComplete = true;
        if (!pathMode)
        {
            DispatchRealtimeFrame(api, parameters, historyUsed, bloomActive, selectionActive,
                realtimeFinish, widthPixels, heightPixels);
        }
        else
        {
            roundComplete = DispatchPathRoundStrips(api, parameters, widthPixels, heightPixels,
                Math.Max(1, settings.PathSamplesPerFrame));
        }

        // 收尾通道必须在整轮采样完成后执行。路径追踪先降噪再参与 Bloom；
        // 实时收尾通道复用同一套联合双边降噪，不依赖路径开关。
        bool denoiseActive = pathMode && settings.PathDenoising && settings.PathDenoiseStrength > 0F;
        bool fxaaActive = !pathMode && !frameState.ScenePreview && settings.RealtimeAntiAliasingPattern == 2 && settings.FxaaStrength > 0F;
        var finishPassDispatched = false;
        if (roundComplete && (realtimeFinish || bloomActive || denoiseActive || selectionActive || fxaaActive))
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
                if (!pathMode) TransitionRealtimeHistoryToGeneral(commandBuffer);
                if (pathMode || bloomActive || selectionActive || fxaaActive || realtimeFinish)
                {
                    // 收尾通道读取整张中间图，等待所有分块写入。
                    TransitionAccumulationToGeneral(commandBuffer);
                    if (pathMode || selectionActive || realtimeFinish) TransitionGuideToGeneral(commandBuffer);
                }
                BindComputeTile(commandBuffer, _ringSets[slot], 0, 0);
                api.CmdDispatch(commandBuffer, ((uint)widthPixels + 7U) / 8U,
                    ((uint)heightPixels + 7U) / 8U, 1);
                LastDispatchCount++;
                LastWorkgroupCount += ((widthPixels + 7L) / 8L) * ((heightPixels + 7L) / 8L);
                TransitionOutputToShaderRead(commandBuffer);
                CommitRingSlot(slot);
                parameters[modeSlot] = 0F;
            }

            if (realtimeFinish)
            {
                DispatchFinalPass(VoxelRenderParameters.DisplayPass, 2F);
            }
            else if (selectionActive)
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
            if (fxaaActive)
            {
                DispatchFinalPass(VoxelRenderParameters.FxaaPass, 1F);
                DispatchFinalPass(VoxelRenderParameters.FxaaPass, 2F);
            }

            finishPassDispatched = true;
        }

        // 路径轮完成但没有启用的收尾通道时补一次纯显示刷新：采样阶段已经不写显示图，
        // 这一轮新增的样本必须经过色调映射才能看到，否则画面会停在上一条完整轮的结果上
        if (pathMode && roundComplete && !finishPassDispatched)
        {
            DispatchDisplayPass(api, parameters, widthPixels, heightPixels);
        }

        // 收尾通道和所有分块都已提交，结束时间戳排在队列最后
        EndGpuTiming();
        parameters[VoxelRenderParameters.TileOffsetX] = 0F;
        parameters[VoxelRenderParameters.TileOffsetY] = 0F;
        parameters[VoxelRenderParameters.DenoisePass] = 0F;
        if (pathMode && roundComplete)
        {
            // 样本计数按本轮实际写入的每像素样本数推进，跨帧分块期间保持不变
            AccumulatedSamples = Math.Min(settings.PathMaxAccumulatedSamples,
                AccumulatedSamples + _pathRoundSamplesPerPixel);
            _accumulationInitialized = true;
            _clearAccumulationPending = false;
            // 本轮结束后的显示图已经带上当前后处理参数，挂起的显示刷新不再需要
            _displayRefreshPending = false;
        }

        _lastParameters = parameters;
        return true;
    }

    /// <summary>
    /// 实时模式整帧一次调度：所有像素共用同一份参数，一次提交完成
    /// </summary>
    /// <remarks>
    /// 实时着色每个像素的计算量固定，不需要像路径追踪那样按分块拆分提交
    /// 环形槽位一次只占用一个，连续多帧都能与 GPU 并行，不再出现每帧等待全部在途提交
    /// </remarks>
    private void DispatchRealtimeFrame(Vk api, float[] parameters, bool historyUsed, bool bloomActive,
        bool selectionActive, bool realtimeFinish, int widthPixels, int heightPixels)
    {
        var slot = AcquireRingSlot();
        UploadParameters(slot, parameters);
        var commandBuffer = _ringCommands[slot];
        BeginGpuTiming(commandBuffer);
        TransitionOutputToGeneral(commandBuffer);
        if (historyUsed) TransitionRealtimeHistoryToGeneral(commandBuffer);
        // 渲染窗口实时通道恒写线性 HDR 累积图与法线/深度导引图，收尾通道才能读
        if (bloomActive || selectionActive || realtimeFinish)
        {
            TransitionAccumulationToGeneral(commandBuffer);
            if (selectionActive || realtimeFinish) TransitionGuideToGeneral(commandBuffer);
        }

        BindComputeTile(commandBuffer, _ringSets[slot], 0, 0);
        var groupX = ((uint)widthPixels + 7U) / 8U;
        var groupY = ((uint)heightPixels + 7U) / 8U;
        api.CmdDispatch(commandBuffer, groupX, groupY, 1);
        LastDispatchCount = 1;
        LastWorkgroupCount = (long)groupX * (long)groupY;
        MarkGpuSamplingEnd(commandBuffer);
        TransitionOutputToShaderRead(commandBuffer);
        CommitRingSlot(slot);
    }

    /// <summary>
    /// 推进一轮路径采样的分块提交：一轮会被切成若干横条，通常每帧只提交其中一条，
    /// 让界面帧在收敛过程中也能刷新；清空或换相机后的第一轮也分帧推进，
    /// 完整一轮结束后再更新显示图，避免同时出现不同轮次的横条。
    /// </summary>
    /// <param name="api">Vulkan API</param>
    /// <param name="currentParameters">本帧按当前设置构造的参数块</param>
    /// <param name="widthPixels">输出宽度</param>
    /// <param name="heightPixels">输出高度</param>
    /// <param name="samplesPerPixel">新一轮每像素样本数</param>
    /// <returns>本轮是否已经在这一帧跑完</returns>
    private bool DispatchPathRoundStrips(Vk api, float[] currentParameters,
        int widthPixels, int heightPixels, int samplesPerPixel)
    {
        if (!_pathRoundOpen)
        {
            // 新一轮必须从干净的累积开始：重置后残留的部分写入不能带进新参数
            if (_clearAccumulationPending)
            {
                ClearAccumulation();
                _clearAccumulationPending = false;
            }

            _pathRoundParameters = (float[])currentParameters.Clone();
            _pathRoundWidth = widthPixels;
            _pathRoundHeight = heightPixels;
            _pathRoundSamplesPerPixel = Math.Max(1, samplesPerPixel);
            var pixelsPerStrip = Math.Clamp(PathStripPixelBudget / _pathRoundSamplesPerPixel,
                1, PathDispatchPixelBudget);
            // 本地工作组高度为 8；除最后一条外必须对齐，避免相邻横条覆盖同一像素。
            var rows = Math.Max(8, pixelsPerStrip / Math.Max(1, widthPixels));
            _pathRoundStripHeight = Math.Min(heightPixels, Math.Max(8, rows / 8 * 8));
            _pathRoundTotalStrips = (heightPixels + _pathRoundStripHeight - 1) / _pathRoundStripHeight;
            _pathRoundNextStrip = 0;
            _pathRoundOpen = true;
        }

        var roundParameters = _pathRoundParameters!;
        var submittedThisFrame = 0;
        while (_pathRoundNextStrip < _pathRoundTotalStrips)
        {
            var slot = AcquireRingSlot();
            UploadParameters(slot, roundParameters);
            var commandBuffer = _ringCommands[slot];
            if (submittedThisFrame == 0)
            {
                BeginGpuTiming(commandBuffer);
            }

            // 采样阶段不写输出图像，输出布局保持上一次显示通道留下的 ShaderReadOnly，
            // 横条之间因此不需要切换输出图布局
            // 采样阶段就要读写 HDR 累积与导引图，布局切换和上一轮的写后读依赖必须在采样前建立，
            // 不能指望收尾通道补：收尾通道在另一条命令缓冲里
            TransitionAccumulationToGeneral(commandBuffer);
            TransitionGuideToGeneral(commandBuffer);

            var stripY = _pathRoundNextStrip * _pathRoundStripHeight;
            var stripRows = Math.Min(_pathRoundStripHeight, _pathRoundHeight - stripY);
            BindComputeTile(commandBuffer, _ringSets[slot], 0, stripY);
            api.CmdDispatch(commandBuffer, ((uint)_pathRoundWidth + 7U) / 8U,
                ((uint)stripRows + 7U) / 8U, 1);
            LastDispatchCount++;
            LastWorkgroupCount += ((_pathRoundWidth + 7L) / 8L) * ((stripRows + 7L) / 8L);
            _pathRoundNextStrip++;
            submittedThisFrame++;

            // 最后一条采样命令缓冲写采样段结束时间戳，收尾通道的耗时单独统计
            if (_pathRoundNextStrip >= _pathRoundTotalStrips) MarkGpuSamplingEnd(commandBuffer);
            CommitRingSlot(slot);

            if (_pathRoundNextStrip < _pathRoundTotalStrips) break;
        }

        if (_pathRoundNextStrip < _pathRoundTotalStrips) return false;
        _pathRoundOpen = false;
        _pathRoundParameters = null;
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
            SrcAccessMask = _accumulationInitialized
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit : 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        api.CmdPipelineBarrier(commandBuffer,
            _accumulationInitialized ? PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
        ClearColorValue clear = new(0F, 0F, 0F, 0F);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        api.CmdClearColorImage(commandBuffer, _accumulationImage, ImageLayout.General, in clear, 1, in range);
        CommitRingSlot(slot);
        _accumulationInitialized = true;
    }

    /// <summary>
    /// 清零实时GI历史图像，避免相机、场景或参数变化后继续采样旧历史
    /// </summary>
    private void ClearRealtimeHistory()
    {
        if (_context is null || _api is not { } api || _realtimeHistoryImage.Handle == 0)
        {
            return;
        }

        var slot = AcquireRingSlot();
        var commandBuffer = _ringCommands[slot];
        ImageMemoryBarrier barrierA = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _realtimeHistoryAInitialized ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _realtimeHistoryImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = _realtimeHistoryAInitialized
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit : 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        ImageMemoryBarrier barrierB = barrierA;
        barrierB.OldLayout = _realtimeHistoryBInitialized ? ImageLayout.General : ImageLayout.Undefined;
        barrierB.Image = _realtimeHistoryImageB;
        barrierB.SrcAccessMask = _realtimeHistoryBInitialized
            ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit : 0;
        ImageMemoryBarrier* historyBarriers = stackalloc ImageMemoryBarrier[2] { barrierA, barrierB };
        api.CmdPipelineBarrier(commandBuffer,
            (_realtimeHistoryAInitialized || _realtimeHistoryBInitialized)
                ? PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit
                : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 2, historyBarriers);
        ClearColorValue clear = new(0F, 0F, 0F, 0F);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        api.CmdClearColorImage(commandBuffer, _realtimeHistoryImage, ImageLayout.General, in clear, 1, in range);
        api.CmdClearColorImage(commandBuffer, _realtimeHistoryImageB, ImageLayout.General, in clear, 1, in range);
        CommitRingSlot(slot);
        _realtimeHistoryAInitialized = true;
        _realtimeHistoryBInitialized = true;
        _realtimeHistoryInvalid = false;
    }

    private static long ComputeRealtimeHistorySignature(float[] parameters, int width, int height, int sourceVersion)
    {
        unchecked
        {
            long hash = width * 397L + height;
            hash = hash * 31 + sourceVersion;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index is VoxelRenderParameters.Frame or VoxelRenderParameters.Pixel or
                    VoxelRenderParameters.SunRealtimeFrames) continue;
                hash = hash * 31 + BitConverter.SingleToInt32Bits(parameters[index]);
            }
            return hash;
        }
    }

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
            if (index is VoxelRenderParameters.Frame or VoxelRenderParameters.Pixel or
                VoxelRenderParameters.SunRealtimeFrames)
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
        var recreateWatch = System.Diagnostics.Stopwatch.StartNew();
        // 输出图像可能仍被上一帧的 ImGui 命令缓冲引用，重建前必须等设备空闲。
        // 不能用帧栅栏代替：这里就在当前帧的录制过程中，帧栅栏已经为本帧复位过，
        // 等它只会等一个尚未提交的信号，直接卡死
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
            // 渐进累积图既由着色器访问，也会通过 CmdClearColorImage 清零。
            accumulationCreateInfo.Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit;
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
            var historyCreateInfo = createInfo;
            // 历史值经过色调映射后再混合；使用浮点精度避免每帧反复量化。
            historyCreateInfo.Format = Format.R16G16B16A16Sfloat;
            historyCreateInfo.Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit;
            Check(api.CreateImage(_device, &historyCreateInfo, null, out _realtimeHistoryImage), "创建实时GI历史图像");
            MemoryRequirements historyRequirements = default;
            api.GetImageMemoryRequirements(_device, _realtimeHistoryImage, &historyRequirements);
            _realtimeHistoryMemory = AllocateMemory(historyRequirements, MemoryPropertyFlags.DeviceLocalBit);
            Check(api.BindImageMemory(_device, _realtimeHistoryImage, _realtimeHistoryMemory, 0), "绑定实时GI历史图像");
            _realtimeHistoryView = CreateImageView(_realtimeHistoryImage, Format.R16G16B16A16Sfloat);
             Check(api.CreateImage(_device, &historyCreateInfo, null, out _realtimeHistoryImageB), "创建实时GI历史图像B");
             MemoryRequirements historyRequirementsB = default;
             api.GetImageMemoryRequirements(_device, _realtimeHistoryImageB, &historyRequirementsB);
             _realtimeHistoryMemoryB = AllocateMemory(historyRequirementsB, MemoryPropertyFlags.DeviceLocalBit);
             Check(api.BindImageMemory(_device, _realtimeHistoryImageB, _realtimeHistoryMemoryB, 0), "绑定实时GI历史图像B");
             _realtimeHistoryViewB = CreateImageView(_realtimeHistoryImageB, Format.R16G16B16A16Sfloat);
            _guideInitialized = false;
             _realtimeHistoryAInitialized = false;
             _realtimeHistoryBInitialized = false;
             _realtimeHistoryReadIsA = true;
             _realtimeHistoryInvalid = true;
             _displayTextureId = _imguiRenderer!.RegisterExternalTexture(_outputView);
            _outputWidth = width;
            _outputHeight = height;
            _outputIsShaderReadOnly = false;
            _accumulationInitialized = false;
            _clearAccumulationPending = true;
            UpdateComputeDescriptors();
            MVoxelLog.Info($"重建视口输出图像 {width}x{height}，用时 {recreateWatch.Elapsed.TotalMilliseconds:0.0}ms");
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
            SrcAccessMask = _outputIsShaderReadOnly ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : 0,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        };
        var sourceStage = _outputIsShaderReadOnly ? PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.TopOfPipeBit;
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

    private void TransitionRealtimeHistoryToGeneral(CommandBuffer commandBuffer)
    {
        var readIsA = _realtimeHistoryReadIsA;
        ImageMemoryBarrier barrierA = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _realtimeHistoryAInitialized ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _realtimeHistoryImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = _realtimeHistoryAInitialized
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit : 0,
            DstAccessMask = readIsA ? AccessFlags.ShaderReadBit : AccessFlags.ShaderWriteBit
        };
        ImageMemoryBarrier barrierB = barrierA;
        barrierB.OldLayout = _realtimeHistoryBInitialized ? ImageLayout.General : ImageLayout.Undefined;
        barrierB.Image = _realtimeHistoryImageB;
        barrierB.SrcAccessMask = _realtimeHistoryBInitialized
            ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit : 0;
        barrierB.DstAccessMask = readIsA ? AccessFlags.ShaderWriteBit : AccessFlags.ShaderReadBit;
        ImageMemoryBarrier* historyBarriers = stackalloc ImageMemoryBarrier[2] { barrierA, barrierB };
        _api!.CmdPipelineBarrier(commandBuffer,
            (_realtimeHistoryAInitialized || _realtimeHistoryBInitialized)
                ? PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit
                : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 2, historyBarriers);
        _realtimeHistoryAInitialized = true;
        _realtimeHistoryBInitialized = true;
    }

    private void TransitionGuideToGeneral(CommandBuffer commandBuffer)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _guideInitialized ? ImageLayout.General : ImageLayout.Undefined,
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
    /// 更新 Compute 的 std140 uniform 参数缓冲。
    /// </summary>
    /// <param name="parameters">渲染参数块</param>
    private void UploadParameters(int slot, float[] parameters)
    {
        if (parameters.Length != VoxelRenderParameters.SlotCount)
            throw new ArgumentException($"Compute 参数槽数应为 {VoxelRenderParameters.SlotCount}，实际为 {parameters.Length}", nameof(parameters));
        var api = _api!;
        void* mapped = null;
        var byteCount = checked((ulong)(parameters.Length * 4 * sizeof(float)));
        Check(api.MapMemory(_device, _ringParameterMemory[slot], 0, byteCount, 0, &mapped), "映射体素 Compute 参数缓冲");
        try
        {
            // std140 中 float 数组每项占 16 字节，与 GLSL 的 values[] 对齐。
            var destination = (float*)mapped;
            for (var index = 0; index < parameters.Length; index++) destination[index * 4] = parameters[index];
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
    /// 按需创建 GPU 时间戳查询池、开始与结束命令缓冲和回读栅栏
    /// </summary>
    /// <returns>资源可用时返回 true，当前设备不支持时间戳时退化为不采样</returns>
    private bool EnsureGpuTimingResources()
    {
        if (_gpuTimingBroken) return false;
        if (_gpuTimestampPool.Handle != 0) return true;
        if (_context is null || !_context.SupportsTimestamps)
        {
            _gpuTimingBroken = true;
            return false;
        }

        try
        {
            var api = _api!;
            QueryPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                // 0 采样开始，1 采样结束，2 整帧结束
                QueryCount = 4
            };
            QueryPool pool = default;
            Check(api.CreateQueryPool(_device, &poolInfo, null, &pool), "创建 GPU 时间戳查询池");
            _gpuTimestampPool = pool;
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _ringCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            CommandBuffer* buffers = stackalloc CommandBuffer[1];
            Check(api.AllocateCommandBuffers(_device, &allocateInfo, buffers), "分配 GPU 时间戳命令缓冲");
            _gpuTimestampEnd = buffers[0];
            FenceCreateInfo fenceInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            };
            Fence fence = default;
            Check(api.CreateFence(_device, &fenceInfo, null, &fence), "创建 GPU 时间戳回读栅栏");
            _gpuTimestampFence = fence;
            _gpuTimestampFenceSignaled = true;
            return true;
        }
        catch (Exception exception)
        {
            _gpuTimingBroken = true;
            _gpuTimingPending = false;
            MVoxelLog.Warn($"GPU 时间戳采样初始化失败，性能分析器将只显示 CPU 数据：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 在第一帧实际计算命令缓冲开头写入开始时间戳，避免把 CPU 等待算进 GPU 耗时
    /// </summary>
    /// <param name="commandBuffer">已开始录制的分块命令缓冲</param>
    private void BeginGpuTiming(CommandBuffer commandBuffer)
    {
        if (!GpuProfilingEnabled || _gpuTimingPending || !EnsureGpuTimingResources()) return;
        try
        {
            var api = _api!;
            api.CmdResetQueryPool(commandBuffer, _gpuTimestampPool, 0, 3);
            api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, _gpuTimestampPool, 0);
            _gpuTimingPending = true;
            _gpuTimingEndSubmitted = false;
            _gpuSampleEndWritten = false;
            _gpuTimingToken = GpuTimingToken;
        }
        catch (Exception exception)
        {
            _gpuTimingBroken = true;
            _gpuTimingPending = false;
            MVoxelLog.Warn($"GPU 开始时间戳写入失败，停止 GPU 计时：{exception.Message}");
        }
    }

    /// <summary>
    /// 在最后一条采样命令缓冲末尾写入采样段结束时间戳，用于把路径求交与收尾通道分开计时
    /// </summary>
    /// <param name="commandBuffer">仍在录制中的采样命令缓冲</param>
    private void MarkGpuSamplingEnd(CommandBuffer commandBuffer)
    {
        if (!_gpuTimingPending || _gpuTimingEndSubmitted || _gpuTimingBroken || _gpuSampleEndWritten) return;
        try
        {
            // BottomOfPipe 保证时间戳落在此前所有采样派发之后
            _api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _gpuTimestampPool, 1);
            _gpuSampleEndWritten = true;
        }
        catch (Exception exception)
        {
            _gpuTimingBroken = true;
            _gpuTimingPending = false;
            MVoxelLog.Warn($"GPU 采样段结束时间戳写入失败，停止 GPU 计时：{exception.Message}");
        }
    }

    /// <summary>
    /// 在整帧 Compute 提交之后写入结束时间戳并提交回读栅栏
    /// </summary>
    private void EndGpuTiming()
    {
        if (!_gpuTimingPending || _gpuTimingEndSubmitted || _gpuTimingBroken) return;
        try
        {
            var api = _api!;
            var commandBuffer = _gpuTimestampEnd;
            Check(api.ResetCommandBuffer(commandBuffer, 0), "重置 GPU 结束时间戳命令缓冲");
            CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo };
            Check(api.BeginCommandBuffer(commandBuffer, in begin), "录制 GPU 结束时间戳");
            // 采样段没有独立收尾时在这里补一条，让三段查询都有有效值
            if (!_gpuSampleEndWritten)
            {
                api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _gpuTimestampPool, 1);
                _gpuSampleEndWritten = true;
            }

            api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _gpuTimestampPool, 2);
            Check(api.EndCommandBuffer(commandBuffer), "结束 GPU 结束时间戳");
            Fence fence = _gpuTimestampFence;
            if (_gpuTimestampFenceSignaled)
            {
                Check(api.ResetFences(_device, 1, in fence), "复位 GPU 时间戳回读栅栏");
                _gpuTimestampFenceSignaled = false;
            }

            CommandBuffer local = commandBuffer;
            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &local
            };
            Check(api.QueueSubmit(_context!.GraphicsQueue, 1, in submit, fence), "提交 GPU 结束时间戳");
            _gpuTimingEndSubmitted = true;
        }
        catch (Exception exception)
        {
            _gpuTimingBroken = true;
            _gpuTimingPending = false;
            MVoxelLog.Warn($"GPU 结束时间戳写入失败，停止 GPU 计时：{exception.Message}");
        }
    }

    /// <summary>
    /// 异步尝试回读最近一帧的 GPU 时间戳，不等待 GPU
    /// </summary>
    /// <param name="token">开始采样时记录的帧标识</param>
    /// <param name="milliseconds">GPU 计算总耗时毫秒</param>
    /// <param name="samplingMilliseconds">采样段耗时毫秒</param>
    /// <param name="finishMilliseconds">收尾段耗时毫秒</param>
    /// <returns>成功取得结果时返回 true</returns>
    internal bool TryTakeGpuTiming(out long token, out double milliseconds,
        out double samplingMilliseconds, out double finishMilliseconds)
    {
        token = 0;
        milliseconds = 0;
        samplingMilliseconds = 0;
        finishMilliseconds = 0;
        if (!_gpuTimingPending || !_gpuTimingEndSubmitted) return false;
        if (_gpuTimingBroken)
        {
            _gpuTimingPending = false;
            return false;
        }

        try
        {
            var api = _api!;
            var status = api.GetFenceStatus(_device, _gpuTimestampFence);
            if (status == Result.NotReady) return false;
            if (status != Result.Success)
            {
                _gpuTimingBroken = true;
                _gpuTimingPending = false;
                return false;
            }

            _gpuTimestampFenceSignaled = true;
            ulong* timestamps = stackalloc ulong[3];
            var result = api.GetQueryPoolResults(_device, _gpuTimestampPool, 0, 3,
                (nuint)(3 * sizeof(ulong)), timestamps, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (result == Result.NotReady) return false;
            if (result != Result.Success)
            {
                _gpuTimingBroken = true;
                _gpuTimingPending = false;
                MVoxelLog.Warn($"GPU 时间戳回读失败：{result}");
                return false;
            }

            var validBits = _context!.TimestampValidBits;
            var mask = validBits >= 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1;
            var start = timestamps[0] & mask;
            var sampleEnd = timestamps[1] & mask;
            var end = timestamps[2] & mask;
            var nanosecondPerTick = _context.TimestampPeriodNanoseconds;
            samplingMilliseconds = ((sampleEnd - start) & mask) * nanosecondPerTick / 1_000_000D;
            finishMilliseconds = ((end - sampleEnd) & mask) * nanosecondPerTick / 1_000_000D;
            milliseconds = ((end - start) & mask) * nanosecondPerTick / 1_000_000D;
            _lastGpuFrameMs = milliseconds;
            _lastGpuSamplingMs = samplingMilliseconds;
            _lastGpuFinishMs = finishMilliseconds;
            token = _gpuTimingToken;
            _gpuTimingPending = false;
            return true;
        }
        catch (Exception exception)
        {
            _gpuTimingBroken = true;
            _gpuTimingPending = false;
            MVoxelLog.Warn($"GPU 时间戳回读异常，停止 GPU 计时：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 创建 Compute 描述符布局
    /// </summary>
    /// <remarks>
    /// 设备支持 Ray Query 时多出 binding 13 的顶层加速结构。软件着色器不声明
    /// 该绑定，因此同一条布局可以同时服务软件与硬件两个管线变体
    /// </remarks>
    /// <returns>Compute 描述符布局</returns>
    private DescriptorSetLayout CreateComputeDescriptorLayout()
    {
        var bindingCount = 15;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[15];
        bindings[0] = new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[2] = new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[3] = new(3, DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[4] = new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[5] = new(5, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[6] = new(6, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[7] = new(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[8] = new(8, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[9] = new(9, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[10] = new(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[11] = new(11, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[12] = new(12, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        bindings[13] = new(14, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        if (_context?.SupportsHardwareRayQuery == true)
            bindings[14] = new(13, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit);
        else bindingCount = 14;
        DescriptorSetLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)bindingCount,
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
        var hardwareRayQuery = _context?.SupportsHardwareRayQuery == true;
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[4]
        {
            new(DescriptorType.StorageBuffer, 8 * TileRingDepth),
            new(DescriptorType.StorageImage, 5 * TileRingDepth),
            new(DescriptorType.UniformBuffer, TileRingDepth),
            new(DescriptorType.AccelerationStructureKhr, hardwareRayQuery ? (uint)TileRingDepth : 0U)
        };
        DescriptorPoolCreateInfo createInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = TileRingDepth,
            PoolSizeCount = hardwareRayQuery ? 4U : 3U,
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
        // 分块起点用 push constant 传递：8 字节足够，且不需要为每个分块准备独立参数缓冲
        PushConstantRange pushRange = new()
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = TilePushConstantBytes
        };
        PipelineLayoutCreateInfo createInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        PipelineLayout layout = default;
        Check(_api!.CreatePipelineLayout(_device, &createInfo, null, &layout), "创建体素 Compute 管线布局");
        return layout;
    }

    /// <summary>
    /// 绑定 Compute 管线、描述符集与分块起点
    /// </summary>
    /// <param name="commandBuffer">正在录制的命令缓冲</param>
    /// <param name="descriptorSet">该槽位的描述符集</param>
    /// <param name="tileX">分块左上角 X</param>
    /// <param name="tileY">分块左上角 Y</param>
    private void BindComputeTile(CommandBuffer commandBuffer, DescriptorSet descriptorSet, int tileX, int tileY)
    {
        var api = _api!;
        // 两个变体共用管线布局与描述符集；硬件变体加速部分求交，着色和后处理共用。
        var pipeline = _hardwareShadowsActive && _hardwareShadowPipeline.Handle != 0
            ? _hardwareShadowPipeline : _computePipeline;
        api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _computePipelineLayout,
            0, 1, in descriptorSet, 0, null);
        int* tileOffset = stackalloc int[2] { tileX, tileY };
        api.CmdPushConstants(commandBuffer, _computePipelineLayout, ShaderStageFlags.ComputeBit, 0,
            TilePushConstantBytes, tileOffset);
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
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[15];
        // binding 13 只有设备支持 Ray Query 时才出现在布局里；加速结构尚未建好时
        // 不写该绑定，软件管线本来也不读取它
        AccelerationStructureKHR accelerationStructure = _shadowAcceleration?.AccelerationStructure ?? default;
        WriteDescriptorSetAccelerationStructureKHR accelerationWrite = new()
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &accelerationStructure
        };
        var writeAcceleration = _context?.SupportsHardwareRayQuery == true;
        if (writeAcceleration)
        {
            writes[14] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = default,
                DstBinding = 13,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
                PNext = &accelerationWrite
            };
        }
        for (var slot = 0; slot < TileRingDepth; slot++)
        {
            if (_ringSets[slot].Handle == 0) return;
            DescriptorBufferInfo parameterInfo = new(_ringParameterBuffers[slot], 0,
                (ulong)(VoxelRenderParameters.SlotCount * 4 * sizeof(float)));
            DescriptorBufferInfo materialTableInfo = new(_materialTableBuffer, 0, _materialTableBufferBytes);
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
                DescriptorType = DescriptorType.UniformBuffer,
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
            // binding 7 现在承载 CPU 烘焙的探针体积；砖表缓冲仍按布局存在但
            // 着色器不再声明它，避免为探针数据新增一整套描述符布局与池预算
            DescriptorBufferInfo probeVolumeInfo = new(_probeVolumeBuffer, 0,
                Math.Max(_probeVolumeCapacity, InitialProbeVolumeBytes));
            writes[7] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _ringSets[slot],
                DstBinding = 7,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &probeVolumeInfo
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
            DescriptorImageInfo historyReadInfo = new(default, _realtimeHistoryReadIsA ? _realtimeHistoryView : _realtimeHistoryViewB, ImageLayout.General);
            DescriptorImageInfo historyWriteInfo = new(default, _realtimeHistoryReadIsA ? _realtimeHistoryViewB : _realtimeHistoryView, ImageLayout.General);
            writes[11] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _ringSets[slot],
                DstBinding = 11, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &historyReadInfo };
            writes[12] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _ringSets[slot],
                DstBinding = 12, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &historyWriteInfo };
            DescriptorBufferInfo sunDepthInfo = new(_sunDepthBuffer, 0, _sunDepthCapacity);
            writes[13] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _ringSets[slot],
                DstBinding = 14, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &sunDepthInfo };
            var withAcceleration = writeAcceleration && accelerationStructure.Handle != 0;
            if (withAcceleration) writes[14].DstSet = _ringSets[slot];
            _api!.UpdateDescriptorSets(_device, withAcceleration ? 15U : 14U, writes, 0, null);
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
        if (_realtimeHistoryView.Handle != 0) api.DestroyImageView(_device, _realtimeHistoryView, null);
        if (_realtimeHistoryImage.Handle != 0) api.DestroyImage(_device, _realtimeHistoryImage, null);
        if (_realtimeHistoryMemory.Handle != 0) api.FreeMemory(_device, _realtimeHistoryMemory, null);
        if (_realtimeHistoryViewB.Handle != 0) api.DestroyImageView(_device, _realtimeHistoryViewB, null);
        if (_realtimeHistoryImageB.Handle != 0) api.DestroyImage(_device, _realtimeHistoryImageB, null);
        if (_realtimeHistoryMemoryB.Handle != 0) api.FreeMemory(_device, _realtimeHistoryMemoryB, null);
        _realtimeHistoryView = default;
        _realtimeHistoryImage = default;
        _realtimeHistoryMemory = default;
        _realtimeHistoryViewB = default;
        _realtimeHistoryImageB = default;
        _realtimeHistoryMemoryB = default;
        if (_guideView.Handle != 0) api.DestroyImageView(_device, _guideView, null);
        if (_guideImage.Handle != 0) api.DestroyImage(_device, _guideImage, null);
        if (_guideMemory.Handle != 0) api.FreeMemory(_device, _guideMemory, null);
        _guideView = default;
        _guideImage = default;
        _guideMemory = default;
        _guideInitialized = false;
        _realtimeHistoryAInitialized = false;
        _realtimeHistoryBInitialized = false;
        _realtimeHistoryReadIsA = true;
        api.DestroyImageView(_device, _accumulationView, null);
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
        _displayRefreshPending = false;
        ClosePathRound();
    }

    // #version 必须是第一条有效指令，因此参数槽位与光源容量的 #define 放在版本行之后注入，
    // GLSL 侧只读 P_ 开头的宏，不再硬编码新增槽位号
    private static readonly string SceneComputeShader =
        "#version 450\n\n" + VoxelRenderParameters.Defines() + "\n" +
        SceneShaderBody.Replace("/* EMISSIVE_SAMPLING */", EmissiveSamplingShader)
            .Replace("/* SUN_DEPTH */", SunDepthShader)
            .Replace("/* SUN_RECONSTRUCTION */", SunReconstructionShader);

    // 与软件变体共用同一份着色器正文，只多一个扩展与编译期开关：
    // 硬件变体加速部分光线求交；光照、阴影方案与后处理仍共用同一份源码
    private static readonly string HardwareSceneComputeShader =
        "#version 460\n#extension GL_EXT_ray_query : require\n\n" +
        VoxelRenderParameters.Defines() + "\n#define P_HW_RT 1\n" +
        SceneShaderBody.Replace("/* EMISSIVE_SAMPLING */", EmissiveSamplingShader)
            .Replace("/* SUN_DEPTH */", SunDepthShader)
            .Replace("/* SUN_RECONSTRUCTION */", SunReconstructionShader);

    private const string SceneShaderBody = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        // 分块起点走 push constant，同一帧的多个分块共用同一份参数缓冲与描述符集，
        // 因此整帧只需要一次参数上传，提交次数也不再受分块数量支配
        layout(push_constant) uniform TilePushConstants
        {
            ivec2 tileOffset;
        } tilePush;

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

        layout(set = 0, binding = 2, rgba8) uniform coherent image2D outputImage;

        layout(set = 0, binding = 3, std140) uniform ParameterBuffer
        {
            float values[P_PARAMETER_COUNT];
        };

        layout(set = 0, binding = 4, rgba16f) uniform coherent image2D accumulationImage;
        layout(set = 0, binding = 8, rgba32f) uniform coherent image2D guideImage;
        layout(set = 0, binding = 11, rgba16f) uniform readonly image2D realtimeHistoryRead;
        layout(set = 0, binding = 12, rgba16f) uniform writeonly image2D realtimeHistoryWrite;
        #ifdef P_HW_RT
        // 只有硬件光追变体声明该绑定；软件变体的描述符布局里它是未使用的槽位
        layout(set = 0, binding = 13) uniform accelerationStructureEXT shadowTopLevel;
        #endif

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

        // CPU 烘焙好的探针体积。布局与 C# 端 SetProbeVolume 一致：
        // info[0] = (原点 xyz, X 方向探针数)，info[1] = (体积尺寸 xyz, Y 方向探针数)，
        // info[2] = (Z 方向探针数, 样本总数, 0, 0)；其后每个 vec4 是一个探针。
        // 探针索引 X 最快。count = 0 表示当前场景没有可用探针。
        layout(set = 0, binding = 7, std430) readonly buffer ProbeVolumeBuffer
        {
            vec4 probeInfo[3];
            vec4 probeSamples[];
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
            // xyz 为面积灯右轴，w 为半宽
            vec4 areaRight;
            // xyz 为面积灯上轴，w 为半高
            vec4 areaUp;
        };

        layout(set = 0, binding = 9, std430) readonly buffer SceneLightBuffer
        {
            SceneLightData sceneLights[];
        };

        bool IntersectBox(vec3 rayOrigin, vec3 rayDirection, vec3 boxMin, vec3 boxMax,
            out float distance, out vec3 normal, bool computeNormal)
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
            // 遮挡查询只要知道“有没有更近的命中”，不会用到法线，
            // 这里跳过法线选轴与正负号计算；距离与命中判定和完整路径完全一致
            if (!computeNormal)
            {
                normal = vec3(0.0, 1.0, 0.0);
                return true;
            }

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

        bool IntersectBounds(vec3 rayOrigin, vec3 rayDirection, vec3 regionOrigin, BvhNode node,
            out float distance)
        {
            // BVH 返回区间的下界，不能复用实体表面求交的出口距离。
            // 起点在节点内时下界必须为零，否则已有远处命中会错误剔除近处几何。
            // 区域原点由调用方按射线读一次后传入，避免每个子节点都去参数缓冲重读
            vec3 boxMin = vec3(node.minimum.xyz) + regionOrigin;
            vec3 boxMax = vec3(node.maximum.xyz) + regionOrigin;
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
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent, float maxDistance, bool anyHit, bool preferContaining)
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
            #ifdef P_HW_RT
            // 从根包围盒外进入时，候选三角形可以安全筛出受射线影响的体素；
            // 起点在内部/贴边、介质连续性查询仍走软件 BVH 的精确边界逻辑。
            if (!preferContaining && nodeCount > 0 && voxelCount > 0)
            {
                vec3 rootMin = regionOrigin + vec3(nodes[0].minimum.xyz);
                vec3 rootMax = regionOrigin + vec3(nodes[0].maximum.xyz);
                bool outside = any(lessThan(rayOrigin, rootMin - vec3(0.001))) ||
                               any(greaterThan(rayOrigin, rootMax + vec3(0.001)));
                if (outside && maxDistance > 0.0)
                {
                    rayQueryEXT query;
                    rayQueryInitializeEXT(query, shadowTopLevel, gl_RayFlagsNoOpaqueEXT, 0xFF,
                        rayOrigin - regionOrigin, 0.0, rayDirection, maxDistance);
                    while (rayQueryProceedEXT(query))
                    {
                        if (rayQueryGetIntersectionTypeEXT(query, false) != gl_RayQueryCandidateIntersectionTriangleEXT) continue;
                        int index = rayQueryGetIntersectionPrimitiveIndexEXT(query, false) / 12;
                        if (index < 0 || index >= voxelCount) continue;
                        VoxelCell candidate = cells[index];
                        // 完全透明的格子不能占据最近命中，否则会挡住后方实体。
                        if (candidate.color.a <= 0.0) continue;
                        if (ignoreTranslucent && (candidate.metadata.x >> 16 & 1u) != 0u) continue;
                        ivec3 extent = ivec3(max(candidate.position.w & 0xFF,1),
                            max((candidate.position.w >> 8) & 0xFF,1), max((candidate.position.w >> 16) & 0xFF,1));
                        vec3 minimum = regionOrigin + vec3(candidate.position.xyz);
                        float distance; vec3 normal;
                        if (!IntersectBox(rayOrigin, rayDirection, minimum, minimum + vec3(extent), distance, normal, !anyHit) ||
                            distance >= closestDistance) continue;
                        closestDistance = distance;
                        hitNormal = normal;
                        hitColor = candidate.color;
                        hitFlags = (candidate.metadata.x >> 16) | (candidate.metadata.y != 0u ? 256u : 0u);
                        hitSlot = candidate.metadata.x & 0xFFFFu;
                        hitCell = candidate.position.xyz;
                        hitExtent = extent;
                        if (anyHit && hitColor.a > 0.0) return true;
                    }
                    return hitColor.a > 0.0;
                }
            }
            #endif
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
                        if (cell.color.a <= 0.0) continue;
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
                        bool intersects = IntersectBox(rayOrigin, rayDirection, voxelMin, voxelMax,
                            distance, normal, !anyHit);
                        // 介质连续性查询要找起点所在盒，而不是最近的表面。
                        // 所在盒的出口可能比旁边盒的入口更远，按表面距离排序会制造假空气缝。
                        if (intersects && preferContaining && all(greaterThanEqual(rayOrigin, voxelMin)) &&
                            all(lessThan(rayOrigin, voxelMax))) distance = 0.0;
                        if (intersects &&
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
                        bool leftHit = IntersectBounds(rayOrigin, rayDirection, regionOrigin, nodes[node.links.x], leftDistance) && leftDistance <= closestDistance;
                        bool rightHit = IntersectBounds(rayOrigin, rayDirection, regionOrigin, nodes[node.links.y], rightDistance) && rightDistance <= closestDistance;
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

        bool TraceClosestWithin(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot,
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent, float maxDistance, bool anyHit)
        {
            return TraceClosestWithin(rayOrigin, rayDirection, closestDistance, hitNormal,
                hitColor, hitFlags, hitSlot, hitCell, hitExtent, ignoreTranslucent, maxDistance, anyHit, false);
        }

        bool TraceClosest(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot,
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent, bool preferContaining)
        {
            return TraceClosestWithin(rayOrigin, rayDirection, closestDistance, hitNormal,
                hitColor, hitFlags, hitSlot, hitCell, hitExtent, ignoreTranslucent, 3.402823e+38, false, preferContaining);
        }

        bool TraceClosest(vec3 rayOrigin, vec3 rayDirection, out float closestDistance,
            out vec3 hitNormal, out vec4 hitColor, out uint hitFlags, out uint hitSlot,
            out ivec3 hitCell, out ivec3 hitExtent, bool ignoreTranslucent)
        {
            return TraceClosest(rayOrigin, rayDirection, closestDistance, hitNormal,
                hitColor, hitFlags, hitSlot, hitCell, hitExtent, ignoreTranslucent, false);
        }

        bool TraceShadow(vec3 rayOrigin, vec3 rayDirection, float maxDistance)
        {
            #ifdef P_HW_RT
            // 阴影只需要“是否有不透明遮挡”。硬件光追的加速结构用工作集相对坐标，
            // 因此射线起点先减去区域原点；命中图元号除以 12 得到体素索引，
            // 再按与 Compute 路径相同的规则跳过半透明格
            vec3 relativeOrigin = rayOrigin - vec3(values[16], values[17], values[18]);
            rayQueryEXT query;
            rayQueryInitializeEXT(query, shadowTopLevel, gl_RayFlagsNoOpaqueEXT, 0xFF,
                relativeOrigin, 0.0, rayDirection, maxDistance);
            int voxelCount = int(values[94]);
            while (rayQueryProceedEXT(query))
            {
                if (rayQueryGetIntersectionTypeEXT(query, false) != gl_RayQueryCandidateIntersectionTriangleEXT)
                    continue;
                int cellIndex = rayQueryGetIntersectionPrimitiveIndexEXT(query, false) / 12;
                if (cellIndex < 0 || cellIndex >= voxelCount) continue;
                VoxelCell candidate = cells[cellIndex];
                if ((candidate.metadata.x >> 16 & 1u) != 0u || candidate.color.a <= 0.0) continue;
                ivec3 extent = ivec3(max(candidate.position.w & 255, 1),
                    max((candidate.position.w >> 8) & 255, 1), max((candidate.position.w >> 16) & 255, 1));
                vec3 minimum = vec3(values[16], values[17], values[18]) + vec3(candidate.position.xyz);
                float distance; vec3 normal;
                if (IntersectBox(rayOrigin, rayDirection, minimum, minimum + vec3(extent),
                    distance, normal, false) && distance < maxDistance) return true;
            }
            return false;
            #else
            float distance;
            vec3 normal;
            vec4 color;
            uint flags;
            uint slot;
            ivec3 cell;
            ivec3 cellExtent;
            return TraceClosestWithin(rayOrigin, rayDirection, distance, normal, color, flags,
                slot, cell, cellExtent, true, maxDistance, true);
            #endif
        }

        float Random(inout uint state);
        // 解析邻域光照要用到后面定义的色彩/天空函数；实时通道的前向声明让
        // 新函数可以紧挨着求交代码摆放，不必把整份着色器重排
        vec3 SrgbToLinear(vec3 color);
        vec3 Sky(vec3 rayDirection);

        // 太阳圆盘的切平面基。方向固定后基也固定，采样盘不会逐帧漂移；
        // 需要的随机/蓝噪声相位由调用方传入，三种阴影方案共用同一套几何。
        void SunDiskBasis(vec3 rayDirection, out vec3 tangent, out vec3 bitangent)
        {
            tangent = normalize(abs(rayDirection.y) < 0.9 ? cross(rayDirection, vec3(0.0, 1.0, 0.0))
                : cross(rayDirection, vec3(1.0, 0.0, 0.0)));
            bitangent = cross(rayDirection, tangent);
        }

        // 方案 A 出片优先：在太阳圆锥内按立体角做分层随机采样，
        // 方位由 RNG 给出。路径追踪按样本累积、实时通道按帧累积，单帧允许
        // 噪点，静止后收敛到正确半影；不追求单帧确定性。
        /* SUN_DEPTH */

        float SunVisibilityProgressive(vec3 rayOrigin, vec3 tangent, vec3 bitangent,
            vec3 rayDirection, float softness, int sampleCount, float maxDistance, inout uint state)
        {
            float visible = 0.0;
            float rotation = 6.28318530718 * Random(state);
            for (int index = 0; index < 16; index++)
            {
                if (index >= sampleCount) break;
                // softness 保持旧参数的 tan(角半径) 含义，改为均匀立体角采样。
                float cosMax = inversesqrt(1.0 + softness * softness);
                float cosTheta = mix(1.0, cosMax, (float(index) + Random(state)) / float(sampleCount));
                float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
                float angle = rotation + 6.28318530718 * Random(state);
                vec3 sampleDirection = rayDirection * cosTheta +
                    sinTheta * (tangent * cos(angle) + bitangent * sin(angle));
                visible += TraceShadow(rayOrigin, sampleDirection, maxDistance) ? 0.0 : 1.0;
            }
            return visible / float(sampleCount);
        }

        // 方案 B/C 的确定性圆盘面积积分：Vogel 螺旋在单位圆内近似均匀铺开，
        // 每像素相位由伪随机序列旋转，相邻像素的残余量化被打散成细颗粒，
        // 由收尾通道的联合双边降噪抹平；相位不含帧号，静止画面逐帧一致。
        float SunVisibilityArea(vec3 rayOrigin, vec3 tangent, vec3 bitangent,
            vec3 rayDirection, float softness, int sampleCount, float maxDistance, float phase)
        {
            float visible = 0.0;
            for (int index = 0; index < 64; index++)
            {
                if (index >= sampleCount) break;
                float radius = sqrt((float(index) + 0.5) / float(sampleCount));
                float angle = phase + float(index) * 2.39996322973;
                vec3 sampleDirection = normalize(rayDirection + softness *
                    (tangent * (cos(angle) * radius) + bitangent * (sin(angle) * radius)));
                visible += TraceShadow(rayOrigin, sampleDirection, maxDistance) ? 0.0 : 1.0;
            }
            return visible / float(sampleCount);
        }

        // 实时通道与路径追踪共用的太阳软阴影入口。方案：
        // 0 渐进圆锥采样，1 屏幕空间半影重建，2 PCSS 太阳深度图。
        // 无有效导引或超出贴图时回退直接太阳采样。
        // 质量档位 1~8 在参数层展开成 2~16 条射线的预算。
        float TraceSoftShadow(vec3 rayOrigin, vec3 surfaceNormal, vec3 rayDirection, float maxDistance, inout uint state)
        {
            float softness = max(values[31], 0.0);
            if (softness <= 0.0001)
                return TraceShadow(rayOrigin, rayDirection, maxDistance) ? 0.0 : 1.0;
            vec3 configuredSunDirection = normalize(vec3(values[24], values[25], values[26]));
            if (int(values[P_SUN_METHOD] + 0.5) == 2 && values[P_SUN_MAP_RADIUS] > 0.0 &&
                dot(rayDirection, configuredSunDirection) > 0.99999)
            {
                float mapped = SunPcss(rayOrigin, surfaceNormal, rayDirection, maxDistance);
                if (mapped >= 0.0) return mapped;
            }
            int budget = clamp(int(values[32]), 2, 16);
            // 路径追踪每个着色点都要做直接光，保留旧上限避免反弹成本翻倍
            bool pathMode = values[86] > 0.5;
            if (pathMode) budget = min(budget, 8);
            if (budget <= 1)
                return TraceShadow(rayOrigin, rayDirection, maxDistance) ? 0.0 : 1.0;

            vec3 tangent;
            vec3 bitangent;
            SunDiskBasis(rayDirection, tangent, bitangent);
            int method = int(values[P_SUN_METHOD] + 0.5);
            if (method == 0)
                return SunVisibilityProgressive(rayOrigin, tangent, bitangent, rayDirection, softness,
                    budget, maxDistance, state);

            float phase = 6.28318530718 * Random(state);
            if (method == 1)
            {
                vec3 configuredSun = normalize(vec3(values[24], values[25], values[26]));
                // 重建只对应太阳直射方向；镜面闪光等其他方向必须单独求交。
                float reconstructed = values[P_SUN_RECONSTRUCTION_AVAILABLE] > 0.5 && values[115] < 0.5 && dot(rayDirection, configuredSun) > 0.99999
                    ? ReconstructedSun(rayOrigin, surfaceNormal) : -1.0;
                if (reconstructed >= 0.0) return reconstructed;
                // 屏外、水面或反射折射点没有可信导引，用均匀太阳盘采样避免随机样本扎堆。
                return SunVisibilityArea(rayOrigin, tangent, bitangent, rayDirection, softness,
                    budget, maxDistance, phase);
            }
            return SunVisibilityArea(rayOrigin, tangent, bitangent, rayDirection, softness,
                clamp(budget, 3, 12), maxDistance, phase);
        }

        // 确定性点查询：沿 BVH 只下降包含该点的节点，叶子内做盒包含测试。
        // 与射线求交不同，它既不发射辅助射线也不需要随机方向，结果零方差。
        // 返回该点所在体素的 sRGB 颜色，a > 0 表示不透明实心；空气 / 半透明返回 0。
        vec4 VoxelAt(vec3 point)
        {
            int voxelCount = int(values[94]);
            int nodeCount = int(values[95]);
            if (nodeCount <= 0 || voxelCount <= 0) return vec4(0.0);
            vec3 regionOrigin = vec3(values[16], values[17], values[18]);
            int stackNodes[64];
            int stackSize = 1;
            stackNodes[0] = 0;
            int visits = 0;
            while (stackSize > 0 && visits++ < 64)
            {
                int nodeIndex = stackNodes[--stackSize];
                if (nodeIndex < 0 || nodeIndex >= nodeCount) continue;
                BvhNode node = nodes[nodeIndex];
                vec3 boxMin = vec3(node.minimum.xyz) + regionOrigin - vec3(0.0001);
                vec3 boxMax = vec3(node.maximum.xyz) + regionOrigin + vec3(0.0001);
                if (any(lessThan(point, boxMin)) || any(greaterThan(point, boxMax))) continue;
                if (node.links.x >= 0)
                {
                    if (stackSize + 2 <= 64)
                    {
                        stackNodes[stackSize++] = node.links.x;
                        stackNodes[stackSize++] = node.links.y;
                    }
                    continue;
                }
                int beginIndex = max(node.links.z, 0);
                int endIndex = min(node.links.z + node.links.w, voxelCount);
                for (int index = beginIndex; index < endIndex; index++)
                {
                    VoxelCell cell = cells[index];
                    ivec3 extent = ivec3(
                        max(cell.position.w & 0xFF, 1),
                        max((cell.position.w >> 8) & 0xFF, 1),
                        max((cell.position.w >> 16) & 0xFF, 1));
                    vec3 voxelMin = regionOrigin + vec3(cell.position.xyz);
                    vec3 voxelMax = voxelMin + vec3(extent);
                    if (all(greaterThanEqual(point, voxelMin)) && all(lessThan(point, voxelMax)) &&
                        cell.color.a > 0.0 && (cell.metadata.x >> 16 & 1u) == 0u)
                        return cell.color;
                }
            }
            return vec4(0.0);
        }

        // 解析邻域遮蔽 + 一次反弹补光。体素命中面法线轴对齐，切平面基完全确定，
        // 没有逐像素独立估计，因此没有椒盐噪点。
        //
        // 关键：采样点吸附到「绝对格中心」才能正确判断该格是否实心，但权重必须按
        // 命中点到格中心的连续距离算。若把整格内所有像素量化到同一个中心（旧版做法），
        // 遮挡值在格内恒定、跨格跳变，就会在物体周围留下一圈一圈的方块状亮度色阶。
        // 这里用连续距离权重 + 窗口边缘权重归零，使 3x3 窗口跨格平移时结果连续，
        // 相当于 MagicaVoxel Model 视图的顶点 varying 插值，但不需要额外射线。
        void RealtimeVoxelEnvironment(vec3 hitPoint, vec3 normal,
            out float visibility, out vec3 bounce)
        {
            vec3 tangent = normalize(abs(normal.y) < 0.9
                ? cross(normal, vec3(0.0, 1.0, 0.0))
                : cross(normal, vec3(1.0, 0.0, 0.0)));
            vec3 bitangent = normalize(cross(normal, tangent));
            // 面内连续坐标（体素格为单位），f = fract(plane) 为格内小数位置
            vec3 surface = hitPoint + normal * 0.5;
            vec2 plane = vec2(dot(surface, tangent), dot(surface, bitangent));
            vec2 cell = floor(plane);
            // 采样数现在表示解析邻域的探测层数，不再是随机射线数
            int levels = (values[62] >= 20.0 || values[P_VOXEL_INDIRECT_SAMPLES] >= 12.0) ? 2 : 1;
            float blocked = 0.0;
            float totalWeight = 0.0;
            vec3 reflected = vec3(0.0);
            for (int level = 0; level < 2; level++)
            {
                if (level >= levels) break;
                float levelWeight = level == 0 ? 1.0 : 0.42;
                for (int j = 0; j < 3; j++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        // 绝对格中心（cell + i - 0.5）相对命中点的连续偏移
                        vec2 delta = vec2(cell.x + float(i) - 0.5 - plane.x,
                            cell.y + float(j) - 0.5 - plane.y);
                        float radial = length(delta);
                        // 距离超过 1.45 格权重为 0：窗口跨格平移时移出/移入的
                        // 都是零权重格，遮挡场因此连续，色阶消失
                        float weight = (1.0 - smoothstep(0.35, 1.45, radial)) * levelWeight;
                        if (weight <= 0.0) continue;
                        vec3 samplePoint = surface + tangent * delta.x + bitangent * delta.y
                            + normal * float(level);
                        vec4 voxel = VoxelAt(samplePoint);
                        if (voxel.a > 0.0)
                        {
                            blocked += weight;
                            reflected += SrgbToLinear(clamp(voxel.rgb, vec3(0.0), vec3(1.0))) * weight;
                        }
                        totalWeight += weight;
                    }
                }
            }
            visibility = totalWeight > 0.0 ? clamp(1.0 - blocked / totalWeight, 0.0, 1.0) : 1.0;
            // 没被挡住的方向按天空补光，被挡住的方向按邻近表面反照率做一次反弹
            float skyWeight = max(totalWeight - blocked, 0.0);
            bounce = totalWeight > 0.0
                ? (reflected * 0.35 + Sky(normal) * 0.35 * skyWeight) / totalWeight
                : vec3(0.0);
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

        // 场景预览的反色描边权重：逐样本累积后取平均，最后由 PostProcess 在显示空间反色，
        // 与 MC 准星的反色叠加同一思路（输出像素 = 1 - 最终显示色），而不是按面色调互补色
        float gSceneOutlineMask = 0.0;

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

        // ================= 全局雾 / 参与介质 =================
        // 密度模型参考 Inigo Quilez 的解析指数高度雾积分与 UE/Crytek 的
        // ExponentialHeightFog；散射项参考 Frostbite 的单次内散射模型与 PBRT 的
        // 透射率重要性采样。实时通道使用确定性解析积分（可选抖动步进求体积阴影），
        // 路径追踪则对每一个弹射段做 Beer–Lambert 消光与单次散射估计，
        // 因此间接光、自发光和镜面反射穿过雾时也会被正确衰减。

        float FogNoiseHash(vec3 cell)
        {
            uint h = uint(int(cell.x)) * 1973u + uint(int(cell.y)) * 9277u
                + uint(int(cell.z)) * 26699u + 0x68bc21ebu;
            return float(Hash(h) & 0x00ffffffu) / 16777216.0;
        }

        // 三维值噪声：沿风向与时间漂移，让雾团有空间起伏而不产生帧间闪烁
        float FogDensityScale(vec3 point)
        {
            float strength = clamp(values[P_FOG_NOISE_STRENGTH], 0.0, 1.0);
            if (strength <= 0.0) return 1.0;
            float windAngle = radians(values[P_WEATHER_WIND_DIRECTION]);
            vec2 wind = vec2(cos(windAngle), sin(windAngle)) * values[P_WEATHER_WIND_STRENGTH];
            float drift = values[P_WATER_TIME] * values[P_FOG_NOISE_SPEED];
            vec3 p = (point + vec3(wind.x, 0.0, wind.y) * drift) * max(values[P_FOG_NOISE_SCALE], 0.0001);
            vec3 cell = floor(p);
            vec3 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = FogNoiseHash(cell);
            float n100 = FogNoiseHash(cell + vec3(1.0, 0.0, 0.0));
            float n010 = FogNoiseHash(cell + vec3(0.0, 1.0, 0.0));
            float n110 = FogNoiseHash(cell + vec3(1.0, 1.0, 0.0));
            float n001 = FogNoiseHash(cell + vec3(0.0, 0.0, 1.0));
            float n101 = FogNoiseHash(cell + vec3(1.0, 0.0, 1.0));
            float n011 = FogNoiseHash(cell + vec3(0.0, 1.0, 1.0));
            float n111 = FogNoiseHash(cell + vec3(1.0, 1.0, 1.0));
            float noise = mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
                mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
            return max(0.0, 1.0 + (noise - 0.5) * 2.0 * strength);
        }

        // 世界点的消光系数 σt = σ0 · exp(-b·(y - 基准高度)) · 噪声
        float FogSigmaAt(vec3 point)
        {
            float sigma = max(values[P_FOG_DENSITY], 0.0);
            if (sigma <= 0.0) return 0.0;
            float b = max(values[P_FOG_HEIGHT_FALLOFF], 0.0);
            if (b > 0.000001)
            {
                float height = (point.y - values[P_FOG_HEIGHT]) * b;
                sigma *= exp(-clamp(height, -2.0, 32.0));
            }
            return sigma * FogDensityScale(point);
        }

        // 沿射线 [t0,t1] 对指数高度密度做解析积分（Inigo Quilez / Crytek）。
        // 噪声以三点平均的整体乘子参与，保证解析路径零方差、无条带。
        float FogOpticalDepth(vec3 ro, vec3 rd, float t0, float t1)
        {
            if (t1 <= t0) return 0.0;
            float sigma = max(values[P_FOG_DENSITY], 0.0);
            if (sigma <= 0.0) return 0.0;
            float scale = 1.0;
            if (values[P_FOG_NOISE_STRENGTH] > 0.0)
            {
                vec3 mid = ro + rd * (t0 + t1) * 0.5;
                scale = (FogDensityScale(ro + rd * t0) + FogDensityScale(mid)
                    + FogDensityScale(ro + rd * t1)) / 3.0;
            }
            float segmentLength = t1 - t0;
            float b = max(values[P_FOG_HEIGHT_FALLOFF], 0.0);
            if (b <= 0.000001) return sigma * scale * segmentLength;
            float h0 = (ro.y - values[P_FOG_HEIGHT]) * b;
            float k = b * rd.y;
            float base = sigma * scale * exp(-clamp(h0, -2.0, 32.0));
            float x = -k * segmentLength;
            // (exp(x)-1)/x 的小量展开，避免 0/0 与除零
            float ratio = abs(x) < 0.01
                ? 1.0 + x * 0.5 + x * x / 6.0
                : (exp(clamp(x, -20.0, 20.0)) - 1.0) / x;
            float start = exp(clamp(-k * t0, -20.0, 20.0));
            return max(0.0, base * segmentLength * ratio * start);
        }

        float FogLimitDistance(float hitDistance)
        {
            float limit = values[P_FOG_VIEW_DISTANCE];
            return limit > 0.0 ? min(hitDistance, limit) : hitDistance;
        }

        float FogSkyDistance()
        {
            float limit = values[P_FOG_VIEW_DISTANCE];
            if (limit > 0.0) return limit;
            return min(4096.0, 3.0 / max(values[P_FOG_DENSITY], 0.00001));
        }

        // 归一化的 Henyey–Greenstein 相函数
        float FogPhase(float cosTheta)
        {
            float g = clamp(values[P_FOG_ANISOTROPY], -0.95, 0.95);
            float g2 = g * g;
            float denominator = max(1.0 + g2 - 2.0 * g * cosTheta, 0.0001);
            return (1.0 - g2) / (12.566370614 * denominator * sqrt(denominator));
        }

        // 相对各向同性散射的增益，限幅避免正对太阳时过曝
        float FogPhaseGain(vec3 rd)
        {
            vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
            return min(FogPhase(dot(rd, sunDirection)) * 12.566370614, 8.0);
        }

        // 天空光与用户雾色构成的环境内散射
        vec3 FogAmbientRadiance(vec3 rd)
        {
            vec3 userColor = SrgbToLinear(clamp(vec3(values[P_FOG_R], values[P_FOG_G], values[P_FOG_B]),
                vec3(0.0), vec3(1.0)));
            return mix(Sky(rd), userColor, clamp(values[P_FOG_SKY_BLEND], 0.0, 1.0))
                * max(values[P_FOG_INSCATTER_STRENGTH], 0.0);
        }

        // 太阳单次散射：相函数决定朝向太阳时的前向光晕
        vec3 FogSunRadiance(vec3 rd)
        {
            float strength = max(values[P_FOG_SUN_STRENGTH], 0.0);
            if (strength <= 0.0 || values[33] <= 0.5) return vec3(0.0);
            vec3 tint = SrgbToLinear(clamp(vec3(values[P_FOG_SUN_R], values[P_FOG_SUN_G], values[P_FOG_SUN_B]),
                vec3(0.0), vec3(1.0)));
            vec3 sunColor = max(vec3(values[28], values[29], values[30]), vec3(0.0));
            return tint * sunColor * (values[27] * strength * 0.15) * FogPhaseGain(rd);
        }

        vec3 FogRadiance(vec3 rd)
        {
            vec3 result = FogAmbientRadiance(rd);
            float albedo = clamp(values[P_FOG_ALBEDO], 0.0, 1.0);
            return result * albedo + FogSunRadiance(rd) * albedo;
        }

        // 未命中几何时把天空/背景融入雾色，避免地平线出现硬边。
        // 高度雾让近地平线更浓、天顶更清，因此不需要额外的高度遮罩。
        vec3 ApplySkyFog(vec3 ro, vec3 rd, vec3 skyColor, float startDistance)
        {
            if (max(values[P_FOG_DENSITY], 0.0) <= 0.0) return skyColor;
            float strength = clamp(values[P_FOG_SKY_STRENGTH], 0.0, 1.0);
            if (strength <= 0.0) return skyColor;
            float t0 = max(startDistance, 0.0);
            float t1 = max(t0 + 0.001, FogSkyDistance());
            float tau = FogOpticalDepth(ro, rd, t0, t1);
            float amount = clamp((1.0 - exp(-tau)) * strength, 0.0,
                clamp(values[P_FOG_MAX_OPACITY], 0.0, 1.0));
            return mix(skyColor, FogRadiance(rd), amount);
        }

        // 实时体积步进：交错梯度噪声做逐像素固定抖动，静止画面无闪烁；
        // 每个采样点查询太阳可见性，阴影区缺少太阳内散射即形成体积光柱。
        // 返回 rgb = 累积内散射，a = 剩余透射率。
        vec4 FogMarch(vec3 ro, vec3 rd, float t0, float t1, int steps, inout uint state)
        {
            vec3 scattered = vec3(0.0);
            float transmittance = 1.0;
            float dt = (t1 - t0) / float(steps);
            vec2 pixelCoordinate = vec2(float(gl_GlobalInvocationID.x), float(gl_GlobalInvocationID.y));
            float jitter = fract(52.9829189 * fract(dot(pixelCoordinate, vec2(0.06711056, 0.00583715))));
            vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
            bool sunActive = values[33] > 0.5 && values[P_FOG_SUN_STRENGTH] > 0.0;
            float albedo = clamp(values[P_FOG_ALBEDO], 0.0, 1.0);
            vec3 sunRad = FogSunRadiance(rd) * albedo;
            vec3 ambient = FogAmbientRadiance(rd) * albedo;
            for (int i = 0; i < 64; i++)
            {
                if (i >= steps) break;
                float t = t0 + (float(i) + jitter) * dt;
                vec3 point = ro + rd * t;
                float sliceAlpha = 1.0 - exp(-FogSigmaAt(point) * dt);
                vec3 lighting = ambient;
                if (sunActive)
                {
                    float visibility = TraceShadow(point + sunDirection * 0.02, sunDirection, 10000.0) ? 0.0 : 1.0;
                    lighting += sunRad * visibility;
                }
                scattered += transmittance * sliceAlpha * lighting;
                transmittance *= 1.0 - sliceAlpha;
                if (transmittance < 0.002) break;
            }
            return vec4(scattered, transmittance);
        }

        // 实时渲染：解析高度雾（默认）或抖动体积步进，二者共用同一组参数。
        vec3 ApplyFogRealtime(vec3 color, vec3 ro, vec3 rd, float surfaceDistance, inout uint state)
        {
            if (max(values[P_FOG_DENSITY], 0.0) <= 0.0) return color;
            float t0 = clamp(values[P_FOG_START], 0.0, surfaceDistance);
            float t1 = FogLimitDistance(surfaceDistance);
            if (t1 <= t0) return color;
            float maxOpacity = clamp(values[P_FOG_MAX_OPACITY], 0.0, 1.0);
            int steps = clamp(int(values[P_FOG_STEPS] + 0.5), 0, 64);
            if (steps > 0)
            {
                vec4 marched = FogMarch(ro, rd, t0, t1, steps, state);
                float effective = max(marched.a, 1.0 - maxOpacity);
                float compensation = (1.0 - effective) / max(1.0 - marched.a, 0.0001);
                return color * effective + marched.rgb * compensation;
            }
            float tau = FogOpticalDepth(ro, rd, t0, t1);
            float amount = clamp(1.0 - exp(-tau), 0.0, maxOpacity);
            return color * (1.0 - amount) + FogRadiance(rd) * amount;
        }

        // 路径追踪：按透射率分布重要性采样一个散射位置，估计该段的单次内散射。
        // 调用方在累加内散射之后再把 throughput 乘以透射率，表面与后续反弹
        // 因此自动获得 Beer–Lambert 消光；散射点的太阳可见性由几何阴影决定。
        vec3 FogPathInscatter(vec3 ro, vec3 rd, float t0, float t1, float transmittance, inout uint state)
        {
            float albedo = clamp(values[P_FOG_ALBEDO], 0.0, 1.0);
            float weight = (1.0 - transmittance) * albedo;
            if (weight <= 0.00001) return vec3(0.0);
            float xi = min(Random(state), 0.999999);
            float sampledTau = -log(max(1.0 - xi * (1.0 - transmittance), 0.000001));
            float totalTau = max(-log(max(transmittance, 0.000001)), 0.000001);
            float scatterT = t0 + (t1 - t0) * clamp(sampledTau / totalTau, 0.0, 1.0);
            vec3 point = ro + rd * scatterT;
            vec3 lighting = FogAmbientRadiance(rd) * albedo;
            vec3 sunRad = FogSunRadiance(rd);
            if (max(max(sunRad.r, sunRad.g), sunRad.b) > 0.0)
            {
                vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
                float visibility = TraceShadow(point + sunDirection * 0.02, sunDirection, 10000.0) ? 0.0 : 1.0;
                lighting += sunRad * visibility;
            }
            return lighting * weight;
        }

        // MagicaVoxel 的成片着色器把背景色 Lb 加在曝光之后再做色调映射，
        // 因此背景亮度不随曝光变化。这里等量折算回辐射空间，让主射线未命中时的
        // 背景与后续 PostProcess 的曝光相乘正好抵消；背景不参与间接照明，
        // 二次射线打空时继续使用 Sky 计算环境光。
        vec3 BackgroundRadiance()
        {
            float exposureScale = exp2(clamp(values[65], -20.0, 20.0));
            vec3 background = max(vec3(values[98], values[99], values[100]), vec3(0.0));
            return background / max(exposureScale, 1e-6);
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
        // 点光与聚光按距离平方衰减并投射一条硬阴影射线；面积灯在矩形上采样一点，
        // 采样点随样本变化，半影靠多帧累积自然形成
        bool SceneLightSample(int index, vec3 hitPoint, vec3 hitNormal,
            out vec3 lightDirection, out vec3 radiance, inout uint state)
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
            else if (kind == 3)
            {
                // 面积灯：在矩形面上均匀采一点。面积灯没有范围窗口，
                // 衰减完全由采样点到表面的几何关系决定
                vec3 right = normalize(light.areaRight.xyz);
                vec3 up = normalize(light.areaUp.xyz);
                float halfWidth = max(light.areaRight.w, 0.0);
                float halfHeight = max(light.areaUp.w, 0.0);
                float area = max(4.0 * halfWidth * halfHeight, 1e-6);
                vec3 samplePoint = light.positionKind.xyz
                    + (Random(state) * 2.0 - 1.0) * right * halfWidth
                    + (Random(state) * 2.0 - 1.0) * up * halfHeight;
                vec3 offset = samplePoint - hitPoint;
                float distanceSquared = max(dot(offset, offset), 1e-4);
                lightDirection = offset * inversesqrt(distanceSquared);
                shadowDistance = sqrt(distanceSquared) - 0.05;
                // 发光面法线与传播方向相反，只从正面发光
                float cosLight = dot(normalize(light.directionEnabled.xyz), -lightDirection);
                if (cosLight <= 0.0) return false;
                // 与自发光面光源同口径：入射辐亮度 × 面积 × cos / (π d²)
                attenuation = area * cosLight / (3.14159265 * distanceSquared);
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

            // 配光曲线预设：围绕对象朝向的轴向亮度分布，模拟常见的 IES 轮廓。
            // 面积灯本身有明确朝向，不套用点光的配光曲线
            if (kind != 0 && kind != 3)
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

            // 面积灯没有点光源那种"是否投射阴影"的余地：采样点被遮挡就该判黑，
            // 否则整块灯板会穿墙漏光，因此面积灯始终投射遮挡射线
            if ((light.rangeSpot.w > 0.5 || kind == 3) && shadowDistance > 0.0)
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
        vec3 SceneLocalLightIrradiance(vec3 hitPoint, vec3 hitNormal, inout uint state)
        {
            int lightCount = clamp(int(values[P_SCENE_LIGHT_COUNT]), 0, SCENE_LIGHT_CAPACITY);
            vec3 irradiance = vec3(0.0);
            for (int index = 0; index < SCENE_LIGHT_CAPACITY; index++)
            {
                if (index >= lightCount) break;
                vec3 lightDirection;
                vec3 radiance;
                if (!SceneLightSample(index, hitPoint, hitNormal, lightDirection, radiance, state)) continue;
                irradiance += radiance * max(dot(hitNormal, lightDirection), 0.0);
            }
            return irradiance;
        }

        // 同一像素的 AA 子样本共享一次确定性邻域查询：光照场是空间平滑的，
        // 亚像素命中点差异不会改变邻域占用，重复查询只是白费算力
        bool realtimeEnvironmentReady = false;
        float realtimeEnvironmentAo = 1.0;
        vec3 realtimeEnvironmentIndirect = vec3(0.0);

        vec4 ProbeSampleAt(ivec3 coordinate, int countX, int countY, int count)
        {
            int index = coordinate.x + coordinate.y * countX + coordinate.z * countX * countY;
            return probeSamples[clamp(index, 0, max(count - 1, 0))];
        }

        // 三线性读取 CPU 烘焙好的探针辐照度：8 次缓冲读取，不发射任何射线。
        // rgb 为线性辐照度，a 为「是否确实取到有效探针」（体积外为 0）。
        // 注意：烘焙样本的 a 是天空可见度，不能直接拿来当有效性标记，
        // 否则室内深处可见度接近 0 的表面会被误判成"没有探针"而完全不补光。
        vec4 ProbeIrradiance(vec3 worldPoint)
        {
            int countX = int(probeInfo[0].w + 0.5);
            int countY = int(probeInfo[1].w + 0.5);
            int countZ = int(probeInfo[2].x + 0.5);
            int count = int(probeInfo[2].y + 0.5);
            if (countX < 1 || countY < 1 || countZ < 1 || count < countX * countY * countZ)
                return vec4(0.0);
            vec3 size = max(probeInfo[1].xyz, vec3(0.001));
            vec3 uv = (worldPoint - probeInfo[0].xyz) / size;
            if (any(lessThan(uv, vec3(-0.02))) || any(greaterThan(uv, vec3(1.02))))
                return vec4(0.0);
            vec3 grid = clamp(uv, vec3(0.0), vec3(1.0)) *
                vec3(float(countX - 1), float(countY - 1), float(countZ - 1));
            ivec3 base = ivec3(floor(grid));
            ivec3 next = min(base + ivec3(1), ivec3(countX - 1, countY - 1, countZ - 1));
            vec3 f = fract(grid);
            vec4 c000 = ProbeSampleAt(ivec3(base.x, base.y, base.z), countX, countY, count);
            vec4 c100 = ProbeSampleAt(ivec3(next.x, base.y, base.z), countX, countY, count);
            vec4 c010 = ProbeSampleAt(ivec3(base.x, next.y, base.z), countX, countY, count);
            vec4 c110 = ProbeSampleAt(ivec3(next.x, next.y, base.z), countX, countY, count);
            vec4 c001 = ProbeSampleAt(ivec3(base.x, base.y, next.z), countX, countY, count);
            vec4 c101 = ProbeSampleAt(ivec3(next.x, base.y, next.z), countX, countY, count);
            vec4 c011 = ProbeSampleAt(ivec3(base.x, next.y, next.z), countX, countY, count);
            vec4 c111 = ProbeSampleAt(ivec3(next.x, next.y, next.z), countX, countY, count);
            vec4 bottom = mix(mix(c000, c100, f.x), mix(c010, c110, f.x), f.y);
            vec4 top = mix(mix(c001, c101, f.x), mix(c011, c111, f.x), f.y);
            vec4 probeValue = mix(bottom, top, f.z);
            // 烘焙值是天空色/反照率的球面平均（sRGB 量级），转线性后会明显变暗，
            // 因此这里重新标定到与实时环境补光同一量级；天空可见度只做温和调制，
            // 不再当作整体衰减系数，否则室内探针 GI 几乎看不见。
            float skyVisibility = clamp(probeValue.a, 0.0, 1.0);
            vec3 irradiance = SrgbToLinear(clamp(probeValue.rgb, vec3(0.0), vec3(1.0))) * 1.45
                * mix(0.65, 1.0, skyVisibility);
            return vec4(irradiance, 1.0);
        }

        // 实时通道的全部间接光：解析邻域遮蔽 + 邻域一次反弹 + 烘焙探针辐照度。
        // 三项都是确定性估计，逐像素方差为零，因此不需要时间累积或降噪。
        void RealtimeEnvironment(vec3 hitPoint, vec3 normal, out float ao, out vec3 indirect)
        {
            if (!realtimeEnvironmentReady)
            {
                float visibility = 1.0;
                vec3 bounce = vec3(0.0);
                if (values[59] > 0.5 || values[119] > 0.0001)
                    RealtimeVoxelEnvironment(hitPoint, normal, visibility, bounce);
                float aoValue = values[59] > 0.5
                    ? pow(visibility, max(values[61], 0.05)) * values[60] + (1.0 - values[60])
                    : 1.0;
                vec3 indirectValue = bounce * max(values[119], 0.0);
                // 探针只照亮真正烘焙过的体积；辐照度按法线朝上程度加权，
                // 局部可见度只做温和压制，避免把室内补光整体吃掉
                vec4 probe = ProbeIrradiance(hitPoint + normal * 0.5);
                if (probe.a > 0.0)
                {
                    float normalWeight = 0.65 + 0.35 * max(normal.y, 0.0);
                    indirectValue += probe.rgb * normalWeight * mix(0.5, 1.0, visibility)
                        * max(values[120], 0.0);
                }
                realtimeEnvironmentAo = aoValue;
                realtimeEnvironmentIndirect = indirectValue;
                realtimeEnvironmentReady = true;
            }
            ao = realtimeEnvironmentAo;
            indirect = realtimeEnvironmentIndirect;
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
            // 解析遮蔽、邻域反弹与探针辐照度共用一次确定性邻域查询，
            // 既没有逐像素随机估计，也不需要为每一条 AA 子射线重算。
            float ao;
            vec3 indirect;
            RealtimeEnvironment(hitPoint, hitNormal, ao, indirect);
            vec3 diffuse = baseColor * ambientFill * mix(1.0, ao, 0.60);
            diffuse += baseColor * indirect;
            vec3 sunDirection = normalize(vec3(values[24], values[25], values[26]));
            // SunDirection 表示从表面指向太阳，正方向才是受光方向。
            vec3 lightDirection = sunDirection;
            float directAmount = max(dot(hitNormal, lightDirection), 0.0);
            if (values[33] > 0.5 && directAmount > 0.0)
            {
                // 每个 AA 子样本继续消耗同一个 RNG：方案 A 因此每帧拿到
                // 多个独立太阳样本；B/C 在导引有效时使用对应重建或贴图。
                float shadowVisibility = TraceSoftShadow(
                    hitPoint + hitNormal * 0.002, hitNormal, lightDirection, 10000.0, state);
                diffuse += baseColor * vec3(values[28], values[29], values[30]) *
                    values[27] * directAmount * shadowVisibility;
            }

            // 场景本地光源直接光，只有启用且强度大于 0 的光源参与；
            // 光源数为 0 时辐照度为零向量，输出与改动前一致
            diffuse += baseColor * SceneLocalLightIrradiance(hitPoint, hitNormal, state);

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
            result = ApplyFogRealtime(result, vec3(values[0], values[1], values[2]),
                rayDirection, cameraDistance, state);
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
            vec3 baseColor, float metalness, float roughness, inout uint state)
        {
            int lightCount = clamp(int(values[P_SCENE_LIGHT_COUNT]), 0, SCENE_LIGHT_CAPACITY);
            vec3 response = vec3(0.0);
            for (int index = 0; index < SCENE_LIGHT_CAPACITY; index++)
            {
                if (index >= lightCount) break;
                vec3 lightDirection;
                vec3 radiance;
                if (!SceneLightSample(index, hitPoint, hitNormal, lightDirection, radiance, state)) continue;
                response += SunSurfaceResponse(baseColor, metalness, roughness,
                    hitNormal, viewDirection, lightDirection) * radiance;
            }
            return response;
        }

        // 环境柔光的直接光采样：矩形均匀采样一条阴影射线，
        // 对应 MagicaVoxel Render 面板的 Area 灯；补光板是虚拟光源，
        // 不参与几何求交，因此不会和 BSDF 采样重复计能
        vec3 SoftFillResponse(vec3 hitPoint, vec3 hitNormal, vec3 viewDirection,
            vec3 baseColor, float metalness, float roughness, inout uint state)
        {
            if (values[P_SOFT_ENABLED] < 0.5 || values[P_SOFT_INTENSITY] <= 0.0) return vec3(0.0);
            vec3 center = vec3(values[P_SOFT_CX], values[P_SOFT_CY], values[P_SOFT_CZ]);
            vec3 halfU = vec3(values[P_SOFT_UX], values[P_SOFT_UY], values[P_SOFT_UZ]);
            vec3 halfV = vec3(values[P_SOFT_VX], values[P_SOFT_VY], values[P_SOFT_VZ]);
            vec3 lightNormal = cross(halfU, halfV);
            float area = length(lightNormal) * 4.0;
            if (area <= 1e-6) return vec3(0.0);
            lightNormal = normalize(lightNormal);
            vec3 samplePoint = center + (Random(state) * 2.0 - 1.0) * halfU
                + (Random(state) * 2.0 - 1.0) * halfV;
            vec3 offset = samplePoint - hitPoint;
            float distanceSquared = dot(offset, offset);
            if (distanceSquared <= 1e-6) return vec3(0.0);
            float distance = sqrt(distanceSquared);
            vec3 direction = offset / distance;
            float surfaceCosine = dot(hitNormal, direction);
            float lightCosine = dot(lightNormal, -direction);
            if (surfaceCosine <= 0.0 || lightCosine <= 0.0) return vec3(0.0);
            float pdf = distanceSquared / max(lightCosine * area, 1e-6);
            float shadowDistance;
            vec3 shadowNormal;
            vec4 shadowColor;
            uint shadowFlags;
            uint shadowSlot;
            ivec3 shadowCell;
            ivec3 shadowExtent;
            if (TraceClosestWithin(hitPoint + hitNormal * 0.002, direction, shadowDistance,
                shadowNormal, shadowColor, shadowFlags, shadowSlot, shadowCell, shadowExtent,
                false, max(distance - 0.002, 0.0), true)) return vec3(0.0);
            vec3 emission = SrgbToLinear(clamp(vec3(values[P_SOFT_R], values[P_SOFT_G],
                values[P_SOFT_B]), vec3(0.0), vec3(1.0))) * values[P_SOFT_INTENSITY];
            return emission * SunSurfaceResponse(baseColor, metalness, roughness,
                hitNormal, viewDirection, direction) / (3.14159265 * pdf);
        }

        /* EMISSIVE_SAMPLING */

        // 平滑插值的世界空间扰动：固定输入始终得到相同结果，不引入逐帧噪点。
        float EnvironmentNoise(vec2 p)
        {
            vec2 cell = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            float a = fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453);
            float b = fract(sin(dot(cell + vec2(1,0), vec2(127.1, 311.7))) * 43758.5453);
            float c = fract(sin(dot(cell + vec2(0,1), vec2(127.1, 311.7))) * 43758.5453);
            float d = fract(sin(dot(cell + vec2(1,1), vec2(127.1, 311.7))) * 43758.5453);
            return mix(mix(a,b,u.x), mix(c,d,u.x), u.y);
        }

        // 按像素在水面上的两个方向足迹衰减高频波纹，保留沿掠射方向的各向异性。
        float WaterFootprintFilter(vec2 direction, float frequency, vec2 dx, vec2 dy)
        {
            float x = dot(direction, dx) * frequency;
            float y = dot(direction, dy) * frequency;
            return exp(-0.125 * max(values[P_WATER_FOOTPRINT_FILTERING], 0.0) * (x*x + y*y));
        }

        // Two appended material-table records form one projector. The first three
        // vec4s transform world position to projector-local coordinates; the rest
        // contain extents, masks and material responses. CPU submission sorts them.
        uint SurfaceReceiverLayer(ivec3 cell)
        {
            uint slots = uint(values[P_SURFACE_RECEIVER_SLOTS]);
            if (slots == 0u) return 0u;
            uint hash = uint(cell.x) * 73856093u ^ uint(cell.y) * 19349663u ^ uint(cell.z) * 83492791u;
            hash ^= hash >> 16; hash *= 0x7feb352du;
            hash ^= hash >> 15; hash *= 0x846ca68bu; hash ^= hash >> 16;
            uint index = hash & (slots - 1u);
            int baseIndex = 65536 + int(values[P_SURFACE_DEPOSIT_COUNT]) * 2;
            for (uint probe = 0u; probe < slots; ++probe)
            {
                MaterialSlotData record = materialSlots[baseIndex + int(index / 4u)];
                uint part = index & 3u;
                vec4 entry = part == 0u ? record.scalars : part == 1u ? record.absorption :
                    part == 2u ? record.optics : record.haze;
                uvec4 bits = floatBitsToUint(entry);
                if (bits.w == 0u) return 0u;
                if (all(equal(bits.xyz, uvec3(cell)))) return bits.w;
                index = (index + 1u) & (slots - 1u);
            }
            return 0u;
        }

        void ApplySurfaceDeposits(vec3 hitPoint, inout vec3 hitNormal,
            inout vec3 hitColor, inout MaterialSlotData material, bool groundHit)
        {
            int count = int(values[P_SURFACE_DEPOSIT_COUNT]);
            uint receiverLayer = 0u;
            bool receiverLayerRead = false;
            for (int i = 0; i < count; ++i)
            {
                MaterialSlotData transform = materialSlots[65536 + i * 2];
                MaterialSlotData appearance = materialSlots[65537 + i * 2];
                uint receiverMask = floatBitsToUint(appearance.haze.w);
                if (receiverMask != 0xffffffffu)
                {
                    if (!receiverLayerRead)
                    {
                        // 解析地面不属于对象图层；查不到归属时保守地不接收受限投射。
                        ivec3 cell = ivec3(floor(hitPoint - hitNormal * 0.001 - vec3(values[16], values[17], values[18])));
                        receiverLayer = groundHit ? 0u : SurfaceReceiverLayer(cell);
                        receiverLayerRead = true;
                    }
                    if (receiverLayer == 0u || receiverLayer > 32u ||
                        (receiverMask & (1u << (receiverLayer - 1u))) == 0u) continue;
                }
                vec4 point = vec4(hitPoint, 1.0);
                vec3 local = vec3(dot(transform.scalars, point), dot(transform.absorption, point),
                    dot(transform.optics, point));
                vec3 halfSize = max(transform.haze.xyz, vec3(0.005));
                vec3 remaining = vec3(1.0) - abs(local) / halfSize;
                if (min(remaining.x, min(remaining.y, remaining.z)) <= 0.0) continue;
                vec3 localUp = normalize(transform.absorption.xyz);
                float facing = dot(hitNormal, localUp);
                // 相同起止角是硬阈值；尤其 0° 时不能把上边界推到 1 以上，
                // 否则朝上的表面也永远无法接收投射。
                float angleWeight = appearance.optics.x - appearance.optics.y < 0.0001
                    ? step(appearance.optics.y - 0.000001, facing)
                    : smoothstep(appearance.optics.y, appearance.optics.x, facing);
                float edge = min(remaining.x, remaining.z);
                float edgeWeight = transform.haze.w > 0.0 ? smoothstep(0.0, transform.haze.w, edge) : 1.0;
                float depthWeight = appearance.absorption.w > 0.0
                    ? smoothstep(0.0, appearance.absorption.w, remaining.y) : 1.0;
                float amount = clamp(appearance.optics.z, 0.0, 1.0);
                float scale = max(appearance.optics.w, 0.001);
                vec2 patternPoint = local.xz * scale + vec2(appearance.haze.y * 0.17, appearance.haze.y * 0.31);
                float pattern = EnvironmentNoise(patternPoint);
                float softness = max(appearance.haze.x, 0.001);
                float mask = amount <= 0.0 ? 0.0 : amount >= 1.0 ? 1.0
                    : smoothstep(1.0 - amount - softness, 1.0 - amount + softness, pattern);
                float weight = clamp(appearance.scalars.w * angleWeight * edgeWeight * depthWeight * mask, 0.0, 1.0);
                if (weight <= 0.0) continue;
                hitColor = mix(hitColor, SrgbToLinear(clamp(appearance.scalars.rgb, vec3(0.0), vec3(1.0))),
                    weight * clamp(appearance.absorption.z, 0.0, 1.0));
                float oldRoughness = material.scalars.x >= 0.0 ? material.scalars.x : values[P_ROUGHNESS];
                material.scalars.x = mix(oldRoughness, clamp(appearance.absorption.x, 0.01, 1.0),
                    weight * clamp(appearance.absorption.y, 0.0, 1.0));
                if (appearance.haze.z > 0.0)
                {
                    float dx = EnvironmentNoise(patternPoint + vec2(0.05, 0.0)) - pattern;
                    float dz = EnvironmentNoise(patternPoint + vec2(0.0, 0.05)) - pattern;
                    vec3 tangentX = normalize(transform.scalars.xyz);
                    vec3 tangentZ = normalize(transform.optics.xyz);
                    hitNormal = normalize(hitNormal - (tangentX * dx + tangentZ * dz) *
                        weight * appearance.haze.z * 2.0);
                }
            }
        }

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
                bool waterHit = false;
                bool voxelHit = TraceClosest(rayOrigin, rayDirection, hitDistance, hitNormal, hitColor, hitFlags, hitSlot, hitCell, hitExtent, false, hasPendingInterface);
                float waterDistance = values[144] > 0.5 && abs(rayDirection.y) > 0.00001
                    ? (values[145] - rayOrigin.y) / rayDirection.y : 3.402823e+38;
                float groundDistance = (values[35] > 0.5 || (!pathMode && values[90] > 0.5 && values[115] < 0.5)) && abs(rayDirection.y) > 0.00001
                    ? -rayOrigin.y / rayDirection.y : 3.402823e+38;
                if (groundDistance <= 0.001) groundDistance = 3.402823e+38;
                if (waterDistance <= 0.001) waterDistance = 3.402823e+38;
                float planeDistance = min(groundDistance, waterDistance);
                if (!voxelHit || planeDistance < hitDistance)
                {
                    if (planeDistance < values[97])
                    {
                        if (bounce == 0)
                        {
                            primarySurfaceHit = true;
                            primarySurfaceDistance = planeDistance;
                        }
                        // 把无限地面作为真正的表面命中，而不是背景颜色。
                        // 这样它会复用体素表面的太阳阴影、AO、环境高光和路径弹射，
                        // 远离模型时也不会出现只剩一块背景色的假地面。
                        groundHit = true;
                        waterHit = waterDistance <= groundDistance;
                        hitDistance = planeDistance;
                        hitNormal = vec3(0.0, 1.0, 0.0);
                        if (waterHit)
                        {
                             vec3 wp = rayOrigin + rayDirection * hitDistance;
                             float wavelength = max(values[P_WATER_WAVE_LENGTH], 0.1);
                             float k = 6.28318530718 / wavelength;
                             float animationTime = values[P_WATER_ANIMATION_ENABLED] > 0.5 ? values[P_WATER_TIME] * values[P_WATER_ANIMATION_SPEED] : 0.0;
                             vec2 flowInput = vec2(values[P_WATER_FLOW_X], values[P_WATER_FLOW_Z]);
                             vec2 flow = length(flowInput) > 0.0001 ? normalize(flowInput) : vec2(1.0, 0.0);
                             vec2 p = wp.xz;
                             float detail = values[P_WATER_DETAIL_STRENGTH];
                             vec2 crossFlow = vec2(-flow.y, flow.x);
                             // 低频域扭曲与独立相位打散平行波列，插值保持跨网格连续。
                             vec2 q = p / wavelength;
                             vec2 driftNoise = flow * animationTime / wavelength * 0.12;
                             vec2 warped = q + vec2(
                                 EnvironmentNoise(q * 0.17 - driftNoise),
                                 EnvironmentNoise(q * 0.17 + vec2(13.7, 29.1) - driftNoise)) * 1.6;
                             float phase = (EnvironmentNoise(q * 0.29 + vec2(7.3, 19.4) - driftNoise) - 0.5) * 3.0;
                             vec2 d1 = normalize(flow + crossFlow * 0.37);
                             vec2 d2 = normalize(flow - crossFlow * 0.68);
                             vec2 d3 = normalize(flow * 0.29 + crossFlow);
                             vec2 d4 = normalize(flow * -0.54 + crossFlow);
                             float t = animationTime * k;
                             float focal = pathMode && values[87] > 0.0
                                 ? values[15] / (2.0 * tan(radians(clamp(values[87],20.0,120.0)) * 0.5)) : max(values[3],1.0);
                             float dySafe = sign(rayDirection.y) * max(abs(rayDirection.y), 0.01);
                             vec3 right = vec3(values[8],values[9],values[10]);
                             vec3 up = vec3(values[12],values[13],values[14]);
                             // 正交下像素足迹恒定，与命中距离无关。
                             float pixelFootprint = values[87] < 0.0
                                 ? -values[87] * 2.0 / max(values[15],1.0) : hitDistance / focal;
                             vec2 dx = (right - rayDirection * right.y / dySafe).xz * pixelFootprint;
                             vec2 dy = (up - rayDirection * up.y / dySafe).xz * pixelFootprint;
                             vec2 gradient = d1 * cos(dot(warped, d1) * 6.2831853 - t + phase) * 0.36 * WaterFootprintFilter(d1,k,dx,dy)
                                 + d2 * cos(dot(warped, d2) * 4.5867253 - t * 0.81 - phase * 0.7 + 1.7) * 0.31 * WaterFootprintFilter(d2,k*0.73,dx,dy)
                                 + d3 * cos(dot(warped, d3) * 8.230087 - t * 1.13 + phase * 0.4 + 3.1) * 0.21 * WaterFootprintFilter(d3,k*1.31,dx,dy)
                                 + d4 * cos(dot(warped, d4) * 12.12655 - t * 1.37 - phase + 0.8) * 0.12 * detail * WaterFootprintFilter(d4,k*1.93,dx,dy);
                             gradient *= values[P_WATER_WAVE_SIZE] * k;
                             hitNormal = normalize(vec3(-gradient.x, 1.0, -gradient.y));
                        }
                        hitColor = vec4(0.0, 0.0, 0.0, 1.0);
                        hitFlags = 0u;
                        hitSlot = 0u;
                        hitCell = ivec3(0);
                        hitExtent = ivec3(1);
                    }
                    else
                    {
                        vec3 skyRadiance = pathMode
                            ? BackgroundRadiance()
                            : values[115] < 0.5
                                ? Sky(rayDirection)
                                : vec3(values[98], values[99], values[100]);
                        if (pathMode || values[115] < 0.5)
                        {
                            skyRadiance = ApplySkyFog(rayOrigin, rayDirection, skyRadiance,
                                bounce == 0 ? values[P_FOG_START] : 0.0);
                        }
                        radiance += throughput * skyRadiance;
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
                        vec3 skyRadiance = pathMode ? BackgroundRadiance() : Sky(rayDirection);
                        skyRadiance = ApplySkyFog(rayOrigin, rayDirection, skyRadiance, values[P_FOG_START]);
                        radiance += throughput * skyRadiance;
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
                    // 单面渲染：从实心体素内部看到的背面不是可见表面，直接透过模型看到背景
                    float backfaceOpacity = materialSlots[int(hitSlot)].optics.z >= 0.0
                        ? materialSlots[int(hitSlot)].optics.z : hitColor.a;
                    if (backfaceOpacity >= 0.999)
                    {
                        // 实心背面不参与着色，把射线推过这个面继续前进，看到模型后方的真实表面或背景
                        rayOrigin = hitPoint + rayDirection * max(0.0005, abs(hitDistance) * 1e-4);
                        hasPendingInterface = false;
                        continue;
                    }

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
                // 单面渲染：两个实心体素之间的交界面对相机不可见，跳过继续前进
                float facingOpacity = materialSlots[int(hitSlot)].optics.z >= 0.0
                    ? materialSlots[int(hitSlot)].optics.z : hitColor.a;
                if (!backfaceHit && facingOpacity >= 0.999)
                {
                    ivec3 frontCell = hitCell + ivec3(int(round(hitNormal.x)), int(round(hitNormal.y)),
                        int(round(hitNormal.z)));
                    if (VoxelAt(frontCell).a > 0.0)
                    {
                        rayOrigin = hitPoint + rayDirection * max(0.0005, abs(hitDistance) * 1e-4);
                        continue;
                    }
                }

                if (bounce == 0)
                {
                    primarySurfaceHit = true;
                    primarySurfaceDistance = hitDistance;
                    primaryNormal = hitNormal;
                    primarySelected = (hitFlags & 256u) != 0u;
                }
                MaterialSlotData material = materialSlots[int(hitSlot)];
                if (waterHit)
                {
                     material.scalars.x = clamp(values[P_WATER_ROUGHNESS], 0.01, 1.0);
                    material.scalars.y = 0.0;
                     material.optics.y = values[P_WATER_SPECULAR_STRENGTH];
                }
                vec3 hitLinearColor = waterHit
                    ? SrgbToLinear(clamp(vec3(values[146], values[147], values[148]), vec3(0.0), vec3(1.0)))
                    : groundHit ? Ground(rayOrigin, rayDirection, hitDistance, pathMode)
                    : SrgbToLinear(clamp(hitColor.rgb, vec3(0.0), vec3(1.0)));
                if (!waterHit && values[P_SURFACE_DEPOSIT_COUNT] > 0.0)
                    ApplySurfaceDeposits(hitPoint, hitNormal, hitLinearColor, material, groundHit);
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
                    // 相邻体素的两侧描边会并排出现，每侧按半个屏幕像素计算；
                    // 不设置世界单位下限，否则相机靠近时黑线会突然变粗。
                    // 宽度取约 1.6 个世界像素：太细会被抗锯齿采样稀释成半透明灰边
                    float outlineWidth = max(1e-6, hitDistance / max(values[3], 1.0) * 1.6);
                    float outlineAmount = 1.0 - smoothstep(0.0, outlineWidth, edgeDistance);
                    // 再走一遍 smoothstep，让面色到描边色是连续过渡而不是一条硬线
                    outlineAmount = outlineAmount * outlineAmount * (3.0 - 2.0 * outlineAmount);
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
                    // 描边只记录权重，由显示空间的 PostProcess 做反色：
                    // 和 MC 准星一样对最终画面取反，深色方块得到浅色棱线，浅色方块得到深色棱线
                    gSceneOutlineMask = outlineAmount;
                    radiance += faceColor;
                    break;
                }

                // 路径追踪按参与介质处理雾：当前射线段（相机→表面，或上一表面→下一
                // 表面）先累加单次内散射，再把 Beer–Lambert 透射率乘进 throughput。
                // 间接反弹、自发光与镜面反射因此同样受雾衰减；散射点的太阳可见性
                // 由几何阴影决定，阴影区自然形成体积光柱。实时通道由 RealtimeShade
                // 一次完成，不在这里重复计雾。
                if (pathMode && values[P_FOG_DENSITY] > 0.0)
                {
                    float fogT0 = bounce == 0 ? clamp(values[P_FOG_START], 0.0, hitDistance) : 0.0;
                    float fogT1 = FogLimitDistance(min(hitDistance, values[97]));
                    if (fogT1 > fogT0)
                    {
                        float fogTransmittance = exp(-FogOpticalDepth(rayOrigin, rayDirection, fogT0, fogT1));
                        radiance += throughput * FogPathInscatter(rayOrigin, rayDirection,
                            fogT0, fogT1, fogTransmittance, state);
                        throughput *= fogTransmittance;
                        if (max(max(throughput.r, throughput.g), throughput.b) < 0.004) break;
                    }
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
                            if (!IntersectBox(cursor, rayDirection, boxMin, boxMax, segment, segmentNormal, true)) break;
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
                    // 仿 MagicaVoxel 的材质语义：相机直接看到的发光面用显示亮度，
                    // 间接照明才使用完整辐射强度。灯体因此不会被 48×倍率冲成白块，
                    // 而它照亮房间的能量保持不变。
                    if (bounce == 0)
                    {
                        radiance += throughput * hitLinearColor * min(values[80] * emissiveScale, 2.0);
                    }
                    else
                    {
                        // 发光辐射保留真实强度与色比，路径最终统一执行辐射钳制。
                        float lampPdf = previousBsdfPdf > 0.0
                            ? EmissiveHitPdf(previousVertex, hitPoint, hitNormal) : 0.0;
                        float hitWeight = previousBsdfPdf > 0.0
                            ? previousBsdfPdf / (previousBsdfPdf + lampPdf) : 1.0;
                        radiance += throughput * hitLinearColor * values[80] * emissiveScale * hitWeight;
                    }
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
                                    hitPoint + hitNormal * 0.002, hitNormal, sunGlint, 10000.0, state);
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
                    // 薄板玻璃近似：壁厚不超过阈值的透明薄壳只做透射与吸收，不按界面法线折射。
                    // 体素化的圆柱壳/球壳每一格台阶都有独立法线，逐格折射会把台阶放大成
                    // 规则棱镜条纹；真实的窗玻璃与灯罩玻璃是薄板，本身不该聚焦成像。
                    bool thinGlass = false;
                    float thinLimit = values[P_THIN_GLASS_MAX_THICKNESS];
                    if (values[P_THIN_GLASS_ENABLED] > 0.5 && !backfaceHit && mediumIor > 1.0 && thinLimit > 0.0)
                    {
                        float thinTravel = 0.0;
                        vec3 thinCursor = hitPoint + rayDirection * 0.001;
                        ivec3 thinCell = hitCell;
                        ivec3 thinExtent = hitExtent;
                        bool thinResolved = false;
                        for (int thinStep = 0; thinStep < 17; thinStep++)
                        {
                            vec3 thinMin = vec3(values[16], values[17], values[18]) + vec3(thinCell);
                            vec3 thinMax = thinMin + vec3(thinExtent);
                            float thinSegment;
                            vec3 thinNormal;
                            if (!IntersectBox(thinCursor, rayDirection, thinMin, thinMax, thinSegment, thinNormal, true))
                            {
                                thinResolved = true;
                                break;
                            }
                            thinTravel += thinSegment + 0.001;
                            vec3 thinOrigin = thinCursor + rayDirection * (thinSegment + 0.001);
                            float thinNextDistance;
                            vec3 thinNextNormal;
                            vec4 thinNextColor;
                            uint thinNextFlags;
                            uint thinNextSlot;
                            ivec3 thinNextCell;
                            ivec3 thinNextExtent;
                            if (!TraceClosest(thinOrigin, rayDirection, thinNextDistance, thinNextNormal,
                                thinNextColor, thinNextFlags, thinNextSlot, thinNextCell, thinNextExtent, false, true))
                                break;
                            vec3 thinNextMin = vec3(values[16], values[17], values[18]) + vec3(thinNextCell);
                            vec3 thinNextMax = thinNextMin + vec3(thinNextExtent);
                            bool thinInsideNext = all(greaterThanEqual(thinOrigin, thinNextMin)) &&
                                all(lessThanEqual(thinOrigin, thinNextMax));
                            // 只有同一材质的连续半透明体素才算同一块薄壳；碰到别的材质或不透明体即到达外表面
                            if (!thinInsideNext || (thinNextFlags & 1u) == 0u || thinNextSlot != hitSlot ||
                                any(greaterThan(abs(thinNextColor - hitColor), vec4(0.001))))
                            {
                                thinResolved = true;
                                break;
                            }
                            thinCursor = thinOrigin;
                            thinCell = thinNextCell;
                            thinExtent = thinNextExtent;
                        }
                        thinGlass = thinResolved && thinTravel > 0.0 && thinTravel <= thinLimit + 0.1;
                    }
                    vec3 mediumDirection = rayDirection;
                    if (!thinGlass && mediumIor > 1.0 && !backfaceHit)
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
                        if (!IntersectBox(cursor, mediumDirection, boxMin, boxMax, segmentDistance, segmentNormal, true))
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
                            nextColor, nextFlags, nextSlot, nextCell, nextExtent, false, true);
                        vec3 nextMin = vec3(values[16], values[17], values[18]) + vec3(nextCell);
                        vec3 nextMax = nextMin + vec3(nextExtent);
                        bool insideNext = nextHit && all(greaterThanEqual(nextOrigin, nextMin)) &&
                            all(lessThanEqual(nextOrigin, nextMax));
                        if (!insideNext || (nextFlags & 1u) == 0u || nextSlot != hitSlot ||
                            any(greaterThan(abs(nextColor - hitColor), vec4(0.001))))
                        {
                            // 介质直接贴着不透明池底时并不存在水到空气的出口；
                            // 不应在这里发生全内反射，否则池底会出现黑色块状缺口。
                            if (insideNext && (nextFlags & 1u) == 0u)
                                nextMediumIor = mediumIor;
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
                    if (!thinGlass && abs(mediumIor - nextMediumIor) > 0.001)
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
                        hitPoint + hitNormal * 0.002, hitNormal, lightDirection, 10000.0, state);
                    vec3 response = SunSurfaceResponse(hitLinearColor, metalness, roughness, hitNormal, -rayDirection, lightDirection);
                    radiance += throughput * response * vec3(values[28], values[29], values[30]) * values[27] * shadowVisibility;
                }

                // 本地光源没有可被 BSDF 采样命中的几何，直接光采样是唯一可见途径，
                // 因此不受太阳直接光开关影响；该开关只控制太阳直接采样，
                // 本地光源目前只做硬阴影，太阳继续使用软阴影
                radiance += throughput * SceneLocalLightResponse(hitPoint, hitNormal,
                    -rayDirection, hitLinearColor, metalness, roughness, state);

                // 环境柔光只做直接光采样：补光板是虚拟光源，路径不会命中它，
                // 因此不存在与 BSDF 采样重复计能的问题
                radiance += throughput * SoftFillResponse(hitPoint, hitNormal, -rayDirection,
                    hitLinearColor, metalness, roughness, state);

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

        vec3 PostProcess(vec3 color, ivec2 pixel, int width, int height, float surfaceDistance);

        // 降噪的 5x5 邻域会与同工作组内相邻像素大量重叠，整组协作预取一次即可：
        // 8x8 工作组需要 12x12 的环形边界，每像素从 50 次 imageLoad 降到约 2.3 次
        const int DenoiseTileSize = 12;
        shared vec3 denoiseColorTile[DenoiseTileSize * DenoiseTileSize];
        shared vec4 denoiseGuideTile[DenoiseTileSize * DenoiseTileSize];

        // 只有会执行降噪的收尾通道才预取：采样通道里不能出现 barrier，
        // 否则图像边界外的线程提前 return 会让整个工作组卡在屏障上
        bool DenoiseShadingRequested()
        {
            bool pathMode = values[P_MODE_PATH] > 0.5;
            bool denoiseEnabled = values[P_DENOISE_ENABLED] > 0.5;
            bool denoiseBranch = values[P_DISPLAY_PASS] > 0.5 || values[P_DENOISE_PASS] > 0.5 ||
                int(values[P_BLOOM_PASS]) == 1;
            // 实时收尾通道（DisplayPass == 2）复用同一套联合双边降噪：
            // 对应 MagicaVoxel Render 视图的「累积图 → 降噪 → ACES → 泛光」链路
            bool realtimeFinish = !pathMode && values[P_DISPLAY_PASS] > 1.5;
            return denoiseBranch && (realtimeFinish || (pathMode && denoiseEnabled));
        }

        void PrefetchDenoiseTile(ivec2 pixel, int width, int height)
        {
            ivec2 local = ivec2(gl_LocalInvocationID.xy);
            // 组起点由当前像素反推，分块 push constant 偏移因此自动生效
            ivec2 groupOrigin = pixel - local - ivec2(2, 2);
            int threadIndex = local.y * 8 + local.x;
            for (int index = threadIndex; index < DenoiseTileSize * DenoiseTileSize; index += 64)
            {
                ivec2 offset = groupOrigin + ivec2(index % DenoiseTileSize, index / DenoiseTileSize);
                ivec2 clampedPixel = clamp(offset, ivec2(0), ivec2(width - 1, height - 1));
                denoiseColorTile[index] = imageLoad(accumulationImage, clampedPixel).rgb;
                denoiseGuideTile[index] = imageLoad(guideImage, clampedPixel);
            }
            barrier();
        }

        vec3 DenoiseAt(ivec2 pixel, int width, int height)
        {
            // pixel 与 width/height 只用于保持调用签名一致，邻域数据来自工作组共享缓冲，
            // 越界坐标已在预取阶段夹紧到图像内
            float strength = clamp(values[93], 0.0, 1.0);
            // 实时通道本身是确定性着色，没有蒙特卡洛噪声，只需轻度平滑
            // 抗锯齿残差与色带；强度过大反而会抹平体素棱线
            if (values[P_MODE_PATH] < 0.5) strength *= 0.55;
            ivec2 local = ivec2(gl_LocalInvocationID.xy) + ivec2(2, 2);
            vec3 center = denoiseColorTile[local.y * DenoiseTileSize + local.x];
            vec3 filtered = vec3(0.0);
            float weightSum = 0.0;
            float centerLuma = dot(center, vec3(0.2126, 0.7152, 0.0722));
            vec4 centerGuide = denoiseGuideTile[local.y * DenoiseTileSize + local.x];
            for (int oy = -2; oy <= 2; oy++)
            {
                for (int ox = -2; ox <= 2; ox++)
                {
                    int tileIndex = (local.y + oy) * DenoiseTileSize + (local.x + ox);
                    vec3 neighbor = denoiseColorTile[tileIndex];
                    float spatial = exp(-float(ox * ox + oy * oy) / 3.5);
                    float neighborLuma = dot(neighbor, vec3(0.2126, 0.7152, 0.0722));
                    float edge = exp(-abs(neighborLuma - centerLuma) * 10.0);
                    vec4 neighborGuide = denoiseGuideTile[tileIndex];
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

        // 使用独立的 HDR 累积图作为只读输入，避免在同一个派发中读写输出图造成竞争。
        // 对降噪后的基色叠加原图位移差，保留基色的降噪结果和后续 Bloom/曝光。
        vec3 HeatHazeAt(vec3 baseColor, ivec2 pixel, int width, int height)
        {
            float strength = clamp(values[P_WEATHER_HEAT_HAZE], 0.0, 1.0);
            if (strength <= 0.0) return baseColor;
            vec2 uv = (vec2(pixel) + 0.5) / float(height);
            float clock = values[P_WEATHER_TIME];
            float seed = values[P_WEATHER_SEED];
            vec2 phase = vec2(seed * 0.17, seed * 0.31);
            float x = EnvironmentNoise(uv * vec2(4.0, 9.0) + phase + vec2(0.16, -0.65) * clock) - 0.5;
            float y = EnvironmentNoise(uv * vec2(6.0, 5.0) + phase.yx + vec2(-0.12, -0.48) * clock + 19.0) - 0.5;
            vec2 offset = vec2(x * 7.0, y * 3.5) * strength;
            vec2 target = clamp(vec2(pixel) + offset, vec2(0.0), vec2(width - 1, height - 1));
            ivec2 low = ivec2(floor(target));
            ivec2 high = min(low + ivec2(1), ivec2(width - 1, height - 1));
            vec2 fraction = fract(target);
            vec3 a = mix(imageLoad(accumulationImage, low).rgb,
                imageLoad(accumulationImage, ivec2(high.x, low.y)).rgb, fraction.x);
            vec3 b = mix(imageLoad(accumulationImage, ivec2(low.x, high.y)).rgb,
                imageLoad(accumulationImage, high).rgb, fraction.x);
            vec3 displaced = mix(a, b, fraction.y);
            vec3 source = imageLoad(accumulationImage, pixel).rgb;
            return max(baseColor + displaced - source, vec3(0.0));
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
            vec4 temporalValue = value;
            float temporalWeight = clamp(values[P_REALTIME_TEMPORAL_WEIGHT], 0.0, 0.98);
            // 时间混合权重为零时不读写历史：结果与直接输出完全一致，
            // 同时省掉整帧一次全图历史清零，也避免为历史图像切换描述符
            if (values[86] < 0.5 && temporalWeight > 0.0)
            {
                vec4 previousValue = imageLoad(realtimeHistoryRead, pixel);
                // 清空后的历史透明度为0，首帧直接使用当前结果，避免黑帧污染
                if (previousValue.a > 0.0)
                    temporalValue.rgb = mix(value.rgb, previousValue.rgb, temporalWeight);
                imageStore(realtimeHistoryWrite, pixel, vec4(temporalValue.rgb, value.a));
            }
            if (values[63] > 0.5 && values[63] < 1.5)
                imageStore(accumulationImage, pixel, temporalValue);
            else
                imageStore(outputImage, pixel, temporalValue);
        }

        // Storage image 没有硬件线性采样，手动插值以保留 FXAA 的亚像素方向。
        vec3 FxaaSample(vec2 position, ivec2 limit)
        {
            vec2 bounded = clamp(position, vec2(0.0), vec2(limit));
            ivec2 base = ivec2(floor(bounded));
            ivec2 next = min(base + ivec2(1), limit);
            vec2 f = fract(bounded);
            vec3 top = mix(imageLoad(outputImage, base).rgb, imageLoad(outputImage, ivec2(next.x, base.y)).rgb, f.x);
            vec3 bottom = mix(imageLoad(outputImage, ivec2(base.x, next.y)).rgb, imageLoad(outputImage, next).rgb, f.x);
            return mix(top, bottom, f.y);
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy) + tilePush.tileOffset;
            gSceneOutlineMask = 0.0;
            if (values[P_SUN_PASS] > 0.5)
            {
                if (values[P_SUN_PASS] < 1.5) BuildSunDepth(pixel);
                else if (values[P_SUN_PASS] < 2.5) BuildSunReconstruction(pixel);
                else FilterSunReconstruction(pixel);
                return;
            }
            int width = int(values[11]);
            int height = int(values[15]);
            // 降噪通道要整工作组协作预取邻域，barrier 之前不能让任何线程提前退出，
            // 因此这一步必须排在图像边界判断之前，组内所有线程都会到达
            if (DenoiseShadingRequested()) PrefetchDenoiseTile(pixel, width, height);
            if (pixel.x >= width || pixel.y >= height) return;

            // 先从完整显示图检测边缘并写入中间图，再复制回输出，避免同一通道读写竞争。
            if (values[P_FXAA_PASS] > 1.5)
            {
                imageStore(outputImage, pixel, imageLoad(accumulationImage, pixel));
                return;
            }
            if (values[P_FXAA_PASS] > 0.5)
            {
                ivec2 limit = ivec2(width - 1, height - 1);
                vec4 center = imageLoad(outputImage, pixel);
                vec3 nw = imageLoad(outputImage, clamp(pixel + ivec2(-1, -1), ivec2(0), limit)).rgb;
                vec3 ne = imageLoad(outputImage, clamp(pixel + ivec2(1, -1), ivec2(0), limit)).rgb;
                vec3 sw = imageLoad(outputImage, clamp(pixel + ivec2(-1, 1), ivec2(0), limit)).rgb;
                vec3 se = imageLoad(outputImage, clamp(pixel + ivec2(1, 1), ivec2(0), limit)).rgb;
                vec3 luma = vec3(0.299, 0.587, 0.114);
                float m = dot(center.rgb, luma), a = dot(nw, luma), b = dot(ne, luma), c = dot(sw, luma), d = dot(se, luma);
                float lo = min(m, min(min(a, b), min(c, d)));
                float hi = max(m, max(max(a, b), max(c, d)));
                vec3 result = center.rgb;
                if (hi - lo >= max(0.0312, hi * values[P_FXAA_THRESHOLD]))
                {
                    vec2 direction = vec2(-((a + b) - (c + d)), (a + c) - (b + d));
                    float reduce = max((a + b + c + d) * 0.03125, 0.0078125);
                    direction = clamp(direction / (min(abs(direction.x), abs(direction.y)) + reduce), vec2(-2.0), vec2(2.0));
                    vec2 pos = vec2(pixel);
                    vec3 inner = 0.5 * (FxaaSample(pos - direction / 6.0, limit) + FxaaSample(pos + direction / 6.0, limit));
                    vec3 wide = inner * 0.5 + 0.25 * (FxaaSample(pos - direction * 0.5, limit) + FxaaSample(pos + direction * 0.5, limit));
                    float wideLuma = dot(wide, luma);
                    vec3 filtered = wideLuma < lo || wideLuma > hi ? inner : wide;
                    result = mix(center.rgb, filtered, clamp(values[P_FXAA_STRENGTH], 0.0, 1.0));
                }
                imageStore(accumulationImage, pixel, vec4(result, center.a));
                return;
            }

            // 显示刷新通道：采样已经完成，只按当前后处理参数重算显示颜色，
            // 因此曝光、色调映射、降噪和 Bloom 的变化不需要重新求交。
            // DisplayPass == 2 是非光追实时通道的收尾：线性 HDR → 联合双边降噪
            // → 泛光 → ACES/曝光 → 显示，与 MagicaVoxel Render 视图同一套顺序
            if (values[P_DISPLAY_PASS] > 0.5)
            {
                bool realtimeFinish = values[P_MODE_PATH] < 0.5 && values[P_DISPLAY_PASS] > 1.5;
                bool displayDenoise = realtimeFinish ||
                    (values[P_MODE_PATH] > 0.5 &&
                     values[P_DENOISE_ENABLED] > 0.5 && values[P_DENOISE_STRENGTH] > 0.0);
                vec3 displayBase = displayDenoise ? DenoiseAt(pixel, width, height)
                    : imageLoad(accumulationImage, pixel).rgb;
                displayBase = HeatHazeAt(displayBase, pixel, width, height);
                if (values[P_BLOOM_ENABLED] > 0.5 && values[P_BLOOM_INTENSITY] > 0.0)
                {
                    displayBase += BloomGather(pixel, width, height) *
                        clamp(values[P_BLOOM_INTENSITY], 0.0, 2.0);
                }
                StoreSceneOutput(pixel, vec4(PostProcess(displayBase, pixel, width, height, -1.0), 1.0));
                return;
            }

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
                        // Compare view-space depths: a surface behind the selected pixel is
                        // a valid silhouette background; a foreground occluder is not.
                        if (imageLoad(guideImage, neighbor).x < 0.5 &&
                            (imageLoad(guideImage, neighbor).y <= 0.0 ||
                             imageLoad(guideImage, neighbor).y >= imageLoad(guideImage, pixel).y))
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
                StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height, -1.0), 1.0));
                return;
            }

            // Bloom 合成通道：以（可选降噪后的）HDR 累积为基色，叠加过曝亮部的宽核辉光再统一色调映射
            if (int(values[108]) == 1)
            {
                vec3 base = useDenoise ? DenoiseAt(pixel, width, height)
                    : imageLoad(accumulationImage, pixel).rgb;
                vec3 color = base + BloomGather(pixel, width, height) * clamp(values[110], 0.0, 2.0);
                StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height, -1.0), 1.0));
                return;
            }

            int samples = pathMode ? max(int(values[78]), 1) : clamp(int(values[P_REALTIME_AA_SAMPLES]), 1, 16);
            int accumulatedSamples = max(int(values[79]), 0);
            // 渐进太阳阴影需要逐帧变化的采样盘：把帧号混入实时通道的种子；
            // 半影重建与快速预览是确定性的，种子不含帧号，静止画面逐帧一致。
            // 路径追踪始终混入帧号，配合累积缓冲收敛。
            int sunMethod = int(values[P_SUN_METHOD] + 0.5);
            bool progressiveRealtime = !pathMode && sunMethod == 0 && values[115] < 0.5 &&
                values[33] > 0.5 && values[31] > 0.0001;
            uint state = Hash(uint(pixel.x) * 1973u + uint(pixel.y) * 9277u +
                (pathMode ? uint(values[7]) * 26699u + uint(values[88]) :
                    progressiveRealtime ? uint(values[P_SUN_REALTIME_FRAMES]) * 26699u : 0u));
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
            float outlineMaskSum = 0.0;
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
                if (!pathMode && samples > 1)
                {
                    int columns = samples >= 8 ? 4 : 2;
                    int rows = samples / columns;
                    vec2 grid = (vec2(float(sampleIndex % columns) + 0.5, float(sampleIndex / columns) + 0.5)
                        / vec2(float(columns), float(rows))) - 0.5;
                    if (values[P_REALTIME_AA_PATTERN] > 0.5)
                        grid = vec2(grid.x - grid.y, grid.x + grid.y) * 0.70710678;
                    jitter = grid * clamp(values[P_REALTIME_AA_SPREAD], 0.25, 1.0);
                }
                vec2 screenPosition = ((vec2(pixel) + 0.5 + jitter) / vec2(width, height)) * 2.0 - 1.0;
                vec3 sampleOrigin = origin;
                bool orthographic = values[87] < 0.0;
                vec3 direction;
                if (orthographic)
                {
                    // 正交投射盒：相机位置不动，射线起点沿相机平面按屏幕坐标平移，
                    // 所有主射线彼此平行（forward）。每世界格对应的像素数由视口半高给出。
                    float halfHeight = max(-values[87], 0.001);
                    float worldPerPixel = 2.0 * halfHeight / max(float(height), 1.0);
                    vec2 screenPixels = (vec2(pixel) + 0.5 + jitter) - vec2(width, height) * 0.5;
                    sampleOrigin = origin + right * (screenPixels.x * worldPerPixel)
                        - up * (screenPixels.y * worldPerPixel);
                    direction = normalize(forward);
                }
                else
                {
                    if (pathMode && values[87] > 0.0)
                        focalPixels = float(height) / (2.0 * tan(radians(clamp(values[87], 20.0, 120.0)) * 0.5));
                    direction = normalize(forward + right * screenPosition.x * float(width) * 0.5 / focalPixels
                        + up * -screenPosition.y * float(height) * 0.5 / focalPixels);
                }
                if (!orthographic && pathMode && values[84] > 0.5 && values[82] > 0.0)
                {
                    float radius = sqrt(Random(state)) * values[82];
                    float angle = 6.28318530718 * Random(state);
                    vec2 lens = vec2(cos(angle), sin(angle)) * radius;
                    // 焦距沿相机前向测量：屏幕各处射线应落在同一焦平面上。
                    float focusRayDistance = max(values[83], 0.01) / max(dot(direction, forward), 0.0001);
                    vec3 focusPoint = sampleOrigin + direction * focusRayDistance;
                    vec3 lensOrigin = sampleOrigin + right * lens.x + up * lens.y;
                    sampleOrigin = lensOrigin;
                    direction = normalize(focusPoint - lensOrigin);
                }
                bool sampleSurfaceHit = false;
                float sampleSurfaceDistance = 3.402823e+38;
                vec3 sampleNormal;
                bool sampleSelected;
                gSceneOutlineMask = 0.0;
                vec3 tracedColor = TracePath(sampleOrigin, direction, state, pathMode,
                    sampleSurfaceHit, sampleSurfaceDistance, sampleNormal, sampleSelected);
                outlineMaskSum += gSceneOutlineMask;
                if (pathMode && sampleSurfaceHit && values[39] > 0.0)
                    tracedColor = mix(Sky(direction), tracedColor, smoothstep(0.0, values[39], sampleSurfaceDistance));
                // 雾已在 TracePath 的每个弹射段按参与介质处理（实时通道由
                // RealtimeShade 处理），这里不再对主射线做画面近似叠色，避免重复计雾。
                sampleColor += tracedColor;
                if (sampleIndex == 0) selectedPixel = sampleSelected;
                if (sampleIndex == 0 && sampleSurfaceHit)
                    guide = vec4(sampleNormal, max(sampleSurfaceDistance * dot(direction, forward), 0.0001));
                primarySurfaceHit = primarySurfaceHit || sampleSurfaceHit;
                primarySurfaceDistance = min(primarySurfaceDistance, sampleSurfaceDistance);
            }
            sampleColor /= float(samples);
            gSceneOutlineMask = outlineMaskSum / float(samples);
            if (values[63] > 0.5 && values[63] < 1.5)
                imageStore(guideImage, pixel, vec4(selectedPixel ? 1.0 : 0.0, guide.w, 0.0, 1.0));

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
                // 采样阶段只写累积图与导引图：显示图由每轮结束后的显示通道统一写。
                // 跨帧分块时一轮会分几帧提交，若这里顺手写显示图，
                // 已处理横条会和上一轮残留的横条混在同一张图上，收敛过程中出现横向色带
                return;
            }
            // 渲染窗口的非光追通道把尾部拆成两段：这里只写线性 HDR 与法线/深度导引图，
            // 由 DisplayPass == 2 的收尾通道统一降噪、泛光、ACES 并写显示图。
            // 场景窗口（ScenePreview）与选中描边通道仍保持原来的单次出图，不受影响。
            bool realtimeRenderView = values[115] < 0.5 && values[63] < 0.5;
            if (realtimeRenderView)
            {
                // 方案 A 在渲染窗口把逐帧样本累加进 HDR 累积图，权重 1/(n+1)；
                // 参数或相机一变，CPU 侧计数归零，这里自然跳过混合，不会拖影。
                if (progressiveRealtime)
                {
                    float previousFrames = values[P_SUN_REALTIME_FRAMES];
                    if (previousFrames >= 1.0)
                    {
                        vec3 previousColor = imageLoad(accumulationImage, pixel).rgb;
                        color = mix(previousColor, color, 1.0 / (previousFrames + 1.0));
                    }
                }
                imageStore(guideImage, pixel, guide);
                imageStore(accumulationImage, pixel, vec4(color, 1.0));
                return;
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
                vec3 gridOrigin = origin;
                vec3 ray;
                if (values[87] < 0.0)
                {
                    float wpp = -values[87] * 2.0 / max(float(height), 1.0);
                    gridOrigin += right * (offset.x * wpp) - up * (offset.y * wpp);
                    ray = normalize(forward);
                }
                else ray = forward + right * offset.x / focalPixels - up * offset.y / focalPixels;
                if (abs(ray.y) > 1e-7 && -gridOrigin.y / ray.y > 0.0)
                {
                    float t = -gridOrigin.y / ray.y;
                    // 网格是 y=0 上的真实辅助平面：只有它比该像素的体素命中更近时才覆盖模型。
                    // 用射线的空间距离与 TracePath 的归一化射线距离比较，避免相机角度变化
                    // 时参数化射线造成的深度不一致；模型在网格前方时则保持模型颜色。
                    float gridDistance = t * length(ray);
                    if (gridDistance >= primarySurfaceDistance - max(1e-4, gridDistance * 1e-5))
                    {
                        float outputAlpha = primarySurfaceHit ? 1.0 : 0.0;
                        StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height, primarySurfaceDistance), outputAlpha));
                        return;
                    }
                    vec2 p = (gridOrigin + t * ray).xz;
                    float gridWpp = values[87] < 0.0
                        ? -values[87] * 2.0 / max(float(height), 1.0) : 1.0 / focalPixels;
                    vec3 dx = right * gridWpp;
                    vec3 dy = -up * gridWpp;
                    vec2 dpdx = (t * (dx - ray * dx.y / ray.y)).xz;
                    vec2 dpdy = (t * (dy - ray * dy.y / ray.y)).xz;
                    vec2 footprint = max(abs(dpdx) + abs(dpdy), vec2(1e-6));
                    float baseStep = max(values[P_GRID_STEP], 0.001);
                    float level = max(0.0, log2(max(footprint.x, footprint.y) * 8.0 / baseStep));
                    float stepSize = baseStep * exp2(floor(level));
                    float blend = smoothstep(0.0, 1.0, fract(level));
                    float fine = GridCoverage(p, footprint, stepSize);
                    float coarse = GridCoverage(p, footprint, stepSize * 2.0);
                    float fade = 1.0 / (1.0 + level * 0.16);
                    float horizon = abs(normalize(ray).y) / (abs(normalize(ray).y) + 0.004);
                    float alpha = mix(fine, coarse, blend) * fade * horizon * 0.72;
                    // 网格只在实际线条覆盖到的像素参与遮挡；不能将无限地面当作
                    // 不透明色板，否则相机位于地面下方时整座模型会被背景色盖住。
                    vec3 background = max(vec3(values[98], values[99], values[100]), vec3(0.0));
                    background = mix(1.055 * pow(background, vec3(1.0 / 2.4)) - 0.055,
                        background * 12.92, lessThanEqual(background, vec3(0.0031308)));
                    vec3 baseColor = primarySurfaceHit
                        ? PostProcess(color, pixel, width, height, primarySurfaceDistance)
                        : background;
                    vec3 composite = mix(baseColor, vec3(0.38, 0.47, 0.59), alpha);
                    StoreSceneOutput(pixel, vec4(composite, 1.0));
                    return;
                }
            }
            // 场景预览允许背景透明，让场景视图绘制自己的底色；渲染视图
            // 必须始终不透明，否则关闭 Bloom 后天空像素 alpha 为 0，会透出视口底色发黑
            float outputAlpha = pathMode || values[115] < 0.5 || primarySurfaceHit ? 1.0 : 0.0;
            StoreSceneOutput(pixel, vec4(PostProcess(color, pixel, width, height, primarySurfaceDistance), outputAlpha));
        }

        // 以固定世界网格定义粒子，时间只平移粒子场。DDA 只访问射线经过的网格。
        float WeatherField(vec3 camera, vec3 ray, float rayLimit, vec3 velocity,
            float time, float density, uint salt, float focal, bool snow)
        {
            const float cellSize = 2.0;
            vec3 origin = camera - velocity * time;
            ivec3 cell = ivec3(floor(origin / cellSize));
            ivec3 stepCell = ivec3(sign(ray));
            vec3 absRay = max(abs(ray), vec3(1e-7));
            vec3 stepDistance = vec3(cellSize) / absRay;
            vec3 boundary = (vec3(cell) + max(vec3(stepCell), vec3(0.0))) * cellSize;
            vec3 nextDistance = abs((boundary - origin) / absRay);
            if (abs(ray.x) < 1e-7) nextDistance.x = 1e30;
            if (abs(ray.y) < 1e-7) nextDistance.y = 1e30;
            if (abs(ray.z) < 1e-7) nextDistance.z = 1e30;
            vec3 axis = normalize(-velocity);
            float coverage = 0.0;
            // 256 个世界单位最多穿过 224 个边长为 2 的网格。
            for (int i = 0; i < 228; ++i)
            {
                uint id = Hash(uint(cell.x) * 73856093u ^ uint(cell.y) * 19349663u ^
                    uint(cell.z) * 83492791u ^ salt);
                float present = float(id & 65535u) / 65535.0;
                if (present < density)
                {
                    vec3 randomCenter = vec3(float(Hash(id + 1u) & 65535u),
                        float(Hash(id + 2u) & 65535u), float(Hash(id + 3u) & 65535u)) / 65535.0;
                    vec3 center = (vec3(cell) + mix(vec3(0.25), vec3(0.75), randomCenter)) * cellSize;
                    vec3 offset = origin - center;
                    float parallel = dot(ray, axis);
                    float axisOffset = snow ? 0.0 : clamp((dot(axis, offset) - parallel * dot(ray, offset)) /
                        max(1.0 - parallel * parallel, 0.001), -0.30, 0.30);
                    vec3 particlePoint = center + axis * axisOffset;
                    float alongRay = dot(particlePoint - origin, ray);
                    if (alongRay > 0.02 && alongRay < rayLimit)
                    {
                        float separation = length(origin + ray * alongRay - particlePoint);
                        float radius = snow ? mix(0.045, 0.09, randomCenter.x) : mix(0.015, 0.03, randomCenter.x);
                        // 正交下每个像素的世界足迹恒定，不随射线行进距离放大。
                        float footprint = values[87] < 0.0
                            ? max(-values[87] * 2.0 / max(values[15], 1.0), 0.001)
                            : max(alongRay / max(focal, 1.0), 0.001);
                        float alpha = (1.0 - smoothstep(radius, radius + footprint, separation)) *
                            min(1.0, radius * 2.0 / footprint);
                        // 镜头附近的粒子渐隐，避免单滴雨在近裁剪面放大成遮住视野的巨条。
                        alpha *= smoothstep(1.0, 5.0, alongRay);
                        coverage = max(coverage, alpha);
                    }
                }
                float exitDistance = min(nextDistance.x, min(nextDistance.y, nextDistance.z));
                if (exitDistance >= rayLimit || coverage >= 0.98) break;
                if (nextDistance.x <= nextDistance.y && nextDistance.x <= nextDistance.z)
                { cell.x += stepCell.x; nextDistance.x += stepDistance.x; }
                else if (nextDistance.y <= nextDistance.z)
                { cell.y += stepCell.y; nextDistance.y += stepDistance.y; }
                else
                { cell.z += stepCell.z; nextDistance.z += stepDistance.z; }
            }
            return coverage;
        }

        vec3 PostProcess(vec3 color, ivec2 pixel, int width, int height, float surfaceDistance)
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
            // 雨雪是世界坐标中的粒子场；收尾通道从导引图取得首表面深度。
            vec2 weatherUv = (vec2(pixel) + 0.5) / vec2(height);
            float weatherTime = values[P_WEATHER_TIME];
            float weatherSeed = values[P_WEATHER_SEED];
            vec2 windDirection = vec2(cos(radians(values[P_WEATHER_WIND_DIRECTION])), sin(radians(values[P_WEATHER_WIND_DIRECTION])));
            float windStrength = max(values[P_WEATHER_WIND_STRENGTH], 0.0);
            float stormStrength = clamp(values[P_WEATHER_STORM], 0.0, 1.0);
            float rainDrop = 0.0;
            float snowDrop = 0.0;
            if (values[P_WEATHER_RAIN] > 0.0 || values[P_WEATHER_SNOW] > 0.0)
            {
                vec3 camera = vec3(values[0], values[1], values[2]);
                vec3 forward = vec3(values[4], values[5], values[6]);
                vec3 right = vec3(values[8], values[9], values[10]);
                vec3 up = vec3(values[12], values[13], values[14]);
                float focal = max(values[3], 1.0);
                if (values[P_MODE_PATH] > 0.5 && values[87] > 0.0)
                    focal = float(height) / (2.0 * tan(radians(clamp(values[87], 20.0, 120.0)) * 0.5));
                vec2 screen = vec2(pixel) + 0.5 - vec2(width, height) * 0.5;
                vec3 ray;
                if (values[87] < 0.0)
                {
                    // 正交：雨雪粒子场从该像素对应的相机平面位置出发，方向平行；
                    // 世界距离与透视一致，粒子密度、雾浓度都不会被放大。
                    float wpp = -values[87] * 2.0 / max(float(height), 1.0);
                    camera += right * (screen.x * wpp) - up * (screen.y * wpp);
                    ray = normalize(forward);
                }
                else ray = normalize(forward + right * screen.x / focal - up * screen.y / focal);
                float maxDistance = min(values[P_FAR_CLIP], values[P_WEATHER_VIEW_DISTANCE]);
                if (surfaceDistance < 0.0)
                {
                    float viewDepth = imageLoad(guideImage, pixel).w;
                    if (viewDepth > 0.0) maxDistance = min(maxDistance, viewDepth / max(dot(ray, forward), 1e-5));
                }
                else maxDistance = min(maxDistance, surfaceDistance);
                vec3 wind = vec3(windDirection.x, 0.0, windDirection.y) * windStrength;
                uint weatherHash = uint(weatherSeed) * 2654435761u;
                if (values[P_WEATHER_RAIN] > 0.0)
                    rainDrop = WeatherField(camera, ray, maxDistance, vec3(wind.x, -8.0, wind.z),
                        weatherTime, clamp(values[P_WEATHER_RAIN], 0.0, 1.0) * 0.12, weatherHash, focal, false);
                if (values[P_WEATHER_SNOW] > 0.0)
                    snowDrop = WeatherField(camera, ray, maxDistance, vec3(wind.x * 0.4, -1.2, wind.z * 0.4),
                        weatherTime, clamp(values[P_WEATHER_SNOW], 0.0, 1.0) * 0.22, weatherHash ^ 0x9e3779b9u, focal, true);
            }
            color = mix(color, vec3(0.75, 0.86, 0.98), rainDrop * 0.5);
            color = mix(color, vec3(0.94, 0.97, 1.0), snowDrop * 0.9);
            color *= 1.0 - stormStrength * 0.18;
            float lightningRate = max(values[P_WEATHER_LIGHTNING], 0.0);
            float lightningClock = weatherTime * lightningRate * 0.42;
            float lightningCell = floor(lightningClock);
            float lightningOffset = fract(sin(lightningCell * 91.73 + 17.0 + weatherSeed * 0.31) * 43758.5453);
            float lightningPhase = abs(fract(lightningClock) - lightningOffset);
            float flash = (1.0 - smoothstep(0.0, 0.08, lightningPhase)) * step(0.001, lightningRate);
            color += vec3(0.8, 0.85, 1.0) * flash * stormStrength * 0.5;
            // 场景基础预览没有独立收尾输入，保留轻微色偏提示；渲染视图在收尾通道采样位移。
            if (surfaceDistance >= 0.0 && values[P_SCENE_PREVIEW] > 0.5)
            {
                float heatNoise = EnvironmentNoise(weatherUv * vec2(9.0, 17.0) + vec2(0.3, -0.8) * weatherTime + weatherSeed * vec2(0.17, 0.31));
                color = mix(color, color * vec3(1.04, 1.01, 0.95), values[P_WEATHER_HEAT_HAZE] * heatNoise * 0.25);
            }
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
            if (values[P_SPLIT_MIX] > 0.0)
            {
                float luminance = dot(clamp(color, 0.0, 1.0), vec3(0.2126, 0.7152, 0.0722));
                float balance = clamp(values[P_SPLIT_BALANCE], 0.05, 0.95);
                float highlightWeight = smoothstep(max(0.0, balance - 0.25), min(1.0, balance + 0.25), luminance);
                vec3 shadowTint = vec3(values[P_SPLIT_SHADOW_R], values[P_SPLIT_SHADOW_G], values[P_SPLIT_SHADOW_B]);
                vec3 highlightTint = vec3(values[P_SPLIT_HIGHLIGHT_R], values[P_SPLIT_HIGHLIGHT_G], values[P_SPLIT_HIGHLIGHT_B]);
                color = mix(color, color * mix(shadowTint, highlightTint, highlightWeight), clamp(values[P_SPLIT_MIX], 0.0, 1.0));
            }
            if (values[P_POSTERIZE_MIX] > 0.0)
            {
                float levels = max(values[P_POSTERIZE_LEVELS], 2.0) - 1.0;
                vec3 quantized = floor(clamp(color, 0.0, 1.0) * levels + 0.5) / levels;
                color = mix(color, quantized, clamp(values[P_POSTERIZE_MIX], 0.0, 1.0));
            }
            if (values[P_FILM_GRAIN_INTENSITY] > 0.0)
            {
                vec2 grainPixel = floor(vec2(pixel) / max(values[P_FILM_GRAIN_SIZE], 1.0));
                float grain = fract(sin(dot(grainPixel, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
                color += grain * values[P_FILM_GRAIN_INTENSITY] * 0.25;
            }
            if (values[71] > 0.5)
            {
                float noise = fract(sin(dot(vec2(pixel), vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
                color += noise / 255.0;
            }
            color = clamp(color, 0.0, 1.0);
            // MC 准星式反色：描边像素取最终显示色的反色，与底色亮度无关，
            // 这样深色方块自然是浅色棱线、浅色方块自然是深色棱线
            color = mix(color, vec3(1.0) - color, clamp(gSceneOutlineMask, 0.0, 1.0));
            return color;
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
        _sceneOrderedCells = Array.Empty<VoxelGpuCell>();
        _sceneSelectionActive = false;
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
        DeviceMemory memory = default;
        try
        {
            memory = AllocateMemory(requirements, properties);
            Check(api.BindBufferMemory(_device, buffer, memory, 0), "绑定体素工作集缓冲");
            return (buffer, memory);
        }
        catch
        {
            api.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0) api.FreeMemory(_device, memory, null);
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
