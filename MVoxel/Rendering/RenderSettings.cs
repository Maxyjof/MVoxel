using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MVoxel.Rendering;

/// <summary>
/// 色调曲线，作用于线性色到显示色的最后一步
/// </summary>
public enum ToneCurve
{
    /// <summary>
    /// 直接截断到 0 到 1
    /// </summary>
    Linear = 0,

    /// <summary>
    /// Reinhard 压缩，亮部平滑滚降
    /// </summary>
    Reinhard = 1,

    /// <summary>
    /// ACES 近似（Narkowicz），照片感最强
    /// </summary>
    AcesNarkowicz = 2,

    /// <summary>
    /// Filmic（HV 分段近似），中间调更保守
    /// </summary>
    Filmic = 3,
}

/// <summary>
/// 渲染计算后端
/// </summary>
public enum RenderBackend
{
    /// <summary>
    /// 使用 Vulkan GPU 计算，设备不可用时回退 CPU
    /// </summary>
    Gpu = 0,

    /// <summary>
    /// 强制使用 CPU 预览
    /// </summary>
    Cpu = 1,
}

/// <summary>实时与路径追踪共用的太阳阴影策略。</summary>
public enum SunShadowMethod
{
    /// <summary>随机分层采样，路径追踪按样本、实时按帧累积收敛，最终画质最好</summary>
    Progressive = 0,
    /// <summary>独立太阳可见度及遮挡距离的屏幕空间半影重建。</summary>
    Reconstructed = 1,
    /// <summary>太阳正交深度图与 PCSS 搜索、过滤。</summary>
    ShadowMap = 2,
}

/// <summary>
/// 两个视口全部可调参数的单一来源，公开字段直接供 ImGui 以 ref 绑定，
/// 渲染管线把它上传为扁平 float 块，默认值保持 MagicaVoxel 观感，
/// 路径追踪部分暴露物理控制。加载与每次上传前都会 ClampAll 与净化 NaN，
/// 损坏的配置文件永远不可能把着色器推出合法范围
/// </summary>
public sealed class RenderSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // 场景视图参数，使用 MagicaVoxel 风格光线求交

    /// <summary>
    /// 是否显示无限地面平面
    /// </summary>
    public bool ShowGround = true;

    /// <summary>
    /// 场景的纯色背景，按线性空间存储
    /// </summary>
    public Vector3 SceneBackgroundColor = new(0.025F, 0.035F, 0.05F);

    /// <summary>
    /// 地面颜色（线性空间存储，面板按 sRGB 编辑）
    /// </summary>
    public Vector3 GroundColor = Vector3.One;

    /// <summary>
    /// 地面淡入天空的距离，格数
    /// </summary>
    /// <remarks>0 表示关闭淡出，保持纯色的无限地面。</remarks>
    public float GroundFade = 0F;

    /// <summary>
    /// 太阳平行光开关
    /// </summary>
    public bool SunEnabled = true;

    /// <summary>
    /// 太阳方位角（度），对应 MagicaVoxel 的 light angle
    /// </summary>
    public float SunAzimuthDegrees = 135F;

    /// <summary>
    /// 太阳仰角（度）
    /// </summary>
    public float SunElevationDegrees = 55F;

    /// <summary>
    /// 太阳强度
    /// </summary>
    public float SunIntensity = 1.05F;

    /// <summary>
    /// 太阳光颜色（线性）
    /// </summary>
    public Vector3 SunColor = new(1.00F, 0.98F, 0.92F);

    /// <summary>
    /// 阴影柔和度，模拟太阳圆盘的半影锥角度，0 为硬阴影
    /// </summary>
    public float ShadowSoftness = 0.06F;

    /// <summary>默认出片优先；旧项目没有此字段时同样使用渐进采样。</summary>
    public SunShadowMethod SunShadowMethod = SunShadowMethod.Progressive;
    /// <summary>太阳采样质量，1～8。</summary>
    public int ShadowSamples = 8;
    /// <summary>实时模式出片优先的静止累积帧数。</summary>
    public int SunAccumulationFrames = 128;
    /// <summary>PCSS 太阳阴影贴图边长。</summary>
    public int SunShadowMapResolution = 1024;

    /// <summary>
    /// 环境柔光开关，对应 MagicaVoxel Render 面板的 Area 灯
    /// </summary>
    /// <remarks>
    /// 房间这类只有开口采光的场景，天空光很难进入室内中段；MagicaVoxel 的
    /// 参考画面靠一块大面积柔光把室内照亮，灯笼只负责局部高光
    /// </remarks>
    public bool SoftFillEnabled = false;
    /// <summary>环境柔光强度，对应 MagicaVoxel Area 灯数值</summary>
    public float SoftFillIntensity = 7F;
    /// <summary>环境柔光颜色（sRGB）</summary>
    public Vector3 SoftFillColor = new(1.00F, 0.86F, 0.68F);
    /// <summary>补光板半边长相对场景包围盒的倍率</summary>
    public float SoftFillSize = 0.6F;
    /// <summary>补光板到相机的距离相对场景包围盒的倍率</summary>
    public float SoftFillDistance = 1.2F;

    /// <summary>
    /// 角点环境光遮蔽开关（MagicaVoxel 顶点 AO）
    /// </summary>
    public bool AmbientOcclusionEnabled = true;

    /// <summary>
    /// AO 强度，0 关闭效果 1 全量
    /// </summary>
    public int RealtimeAntiAliasingSamples = 4;
    public int RealtimeAntiAliasingPattern;
    public float RealtimeAntiAliasingSpread = 1F;
    public float FxaaThreshold = .125F;
    public float FxaaStrength = .75F;
    public float WaterFootprintFiltering = 1F;
    public float AoStrength = 0.85F;

    /// <summary>
    /// AO 曲线对比度
    /// </summary>
    public float AoPower = 1.2F;

    /// <summary>
    /// 实时环境光遮蔽射线数；每增加一次采样都会执行一次限距 BVH 查询
    /// </summary>
    public int AoSamples = 24;

    /// <summary>非光追体素间接光强度</summary>
    public float VoxelIndirectLightStrength = 0.35F;

    /// <summary>
    /// 非光追体素间接光的每像素采样方向数
    /// </summary>
    /// <remarks>
    /// 实时通道没有跨帧累积，这一项直接决定间接光的噪声水平：
    /// 4 个方向噪点明显，16 个方向基本可用，代价是每个像素的限距求交次数同步上升
    /// </remarks>
    public int VoxelIndirectLightSamples = 16;

    /// <summary>
    /// 每个探针的烘焙采样方向数
    /// </summary>
    /// <remarks>
    /// 方向越多，单个探针的天空可见度与反弹能量估计越稳，相邻探针之间的跳变越小
    /// </remarks>
    public int ProbeBakeRays = 64;

    /// <summary>
    /// 单个探针体积允许的探针数量上限
    /// </summary>
    /// <remarks>
    /// 体积与密度一起放大时按总量上限等比降密度，调高它会显著增加烘焙时间
    /// </remarks>
    public int ProbeBakeMaxProbes = 65536;

    /// <summary>
    /// 烘焙结果的三维盒式平滑迭代次数
    /// </summary>
    /// <remarks>
    /// 单个探针的估计本身是带噪的，相邻探针做几次平滑能把斑块压下去；
    /// 次数过多会让洞穴里的明暗边界变软
    /// </remarks>
    public int ProbeBakeSmoothing = 1;

    /// <summary>
    /// 探针烘焙射线的最长探测距离，0 表示按探针体积对角线自动取值
    /// </summary>
    public float ProbeBakeDistance = 0F;

    /// <summary>非光追探针体积GI环境光强度</summary>
    public float ProbeVolumeGiStrength = 0.5F;

    /// <summary>
    /// 非光追实时通道的历史平滑权重
    /// </summary>
    /// <remarks>
    /// 实时着色已改为确定性解析估计（邻域遮蔽 + 探针辐照度），静止画面逐帧完全一致，
    /// 不再需要靠历史混合消除蒙特卡洛噪声，默认关闭以省掉历史图的读写与交换。
    /// 保留该权重供后续做时间抗锯齿等需要历史帧的效果使用；相机或设置变化时历史会自动作废。
    /// </remarks>
    public float RealtimeTemporalWeight = 0F;

    /// <summary>
    /// 强制使用 Compute 渲染路径，忽略硬件光追能力
    /// </summary>
    public bool ForceComputePath = false;

    /// <summary>
    /// 天顶颜色（sRGB 数值，着色时转线性）
    /// </summary>
    public Vector3 SkyTop = new(0.24F, 0.45F, 0.78F);

    /// <summary>
    /// 地平线颜色（sRGB 数值）
    /// </summary>
    public Vector3 SkyHorizon = new(0.62F, 0.72F, 0.85F);

    /// <summary>
    /// 天空半球环境光强度
    /// </summary>
    public float SkyAmbient = 0.42F;

    /// <summary>
    /// 暖色平面补光，对应 MagicaVoxel 的环境光观感
    /// </summary>
    public float AmbientTint = 0.15F;

    // MagicaVoxel 风格调色着色参数

    /// <summary>
    /// 是否启用调色着色（色相、饱和、对比、明度）
    /// </summary>
    public bool PaletteShading = true;

    /// <summary>
    /// 饱和度倍率
    /// </summary>
    public float Saturation = 1.0F;

    /// <summary>
    /// 色相偏移（度）
    /// </summary>
    public float HueShiftDegrees;

    /// <summary>
    /// 颜色对比度倍率
    /// </summary>
    public float ColorContrast = 1.0F;

    /// <summary>
    /// 颜色明度倍率
    /// </summary>
    public float Brightness = 1.0F;

    /// <summary>
    /// 色相环动画开关（MagicaVoxel 的 hue cycle）
    /// </summary>
    public bool HueCycle;

    /// <summary>
    /// 色相环速度，度每秒
    /// </summary>
    public float HueCycleSpeed = 20F;

    /// <summary>
    /// 雾颜色（sRGB 数值）
    /// </summary>
    public Vector3 FogColor = new(0.62F, 0.72F, 0.85F);

    /// <summary>
    /// 指数距离雾浓度，0 关闭
    /// </summary>
    public float FogDensity;
    public float FogStart;
    public float FogSkyBlend = 0.55F;
    public float FogMaxOpacity = 0.92F;

    /// <summary>雾层基准高度（世界格坐标 Y），密度在该高度处等于雾浓度</summary>
    public float FogHeight;
    /// <summary>密度随高度的指数衰减系数，0 表示均匀雾</summary>
    public float FogHeightFalloff;
    /// <summary>Henyey–Greenstein 相函数各向异性 g，正值表示前向散射</summary>
    public float FogAnisotropy = 0.6F;
    /// <summary>太阳方向内散射强度</summary>
    public float FogSunScatterStrength = 1F;
    /// <summary>太阳内散射颜色（sRGB 显示值，作为太阳光的色比乘子）</summary>
    public Vector3 FogSunScatterColor = new(1F, 0.9F, 0.75F);
    /// <summary>单次散射反照率，1 为纯散射雾、0 为纯吸收雾</summary>
    public float FogAlbedo = 1F;
    /// <summary>雾内散射整体亮度倍率</summary>
    public float FogInscatterStrength = 1F;
    /// <summary>天空与背景融入雾色的强度，控制远景地平线的衔接</summary>
    public float FogSkyFogStrength = 0.55F;
    /// <summary>实时渲染的体积步进采样数，0 使用解析高度雾积分</summary>
    public int FogVolumetricSteps;
    /// <summary>体积雾参与计算的最远距离，0 表示跟随命中距离</summary>
    public float FogViewDistance;
    /// <summary>密度噪声起伏强度，0 为均匀雾</summary>
    public float FogNoiseStrength;
    /// <summary>密度噪声空间频率</summary>
    public float FogNoiseScale = 0.035F;
    /// <summary>密度噪声随风漂移速度</summary>
    public float FogNoiseSpeed = 0.6F;

    // 无限水面；波纹改变表面法线，平面高度保持稳定。
    public bool ShowWater;
    public float WaterHeight;
    public Vector3 WaterColor = new(0.08F, 0.32F, 0.42F);
    public float WaterWaveSize = 0.35F;
    public float WaterWaveLength = 16F;
    public float WaterRoughness = 0.12F;
    public bool WaterAnimationEnabled = true;
    public float WaterAnimationSpeed = 0.7F;
    public Vector2 WaterFlowDirection = new(1F, 0.35F);
    public float WaterDetailStrength = 0.35F;
    public float WaterSpecularStrength = 0.8F;
    public float WeatherWindStrength;
    public float WeatherWindDirection = 0.35F;
    public float WeatherRainIntensity;
    public float WeatherSnowIntensity;
    public float WeatherStormIntensity;
    public float WeatherHeatHaze;
    public float WeatherLightningFrequency;
    public int WeatherSeed;
    public float WeatherViewDistance = 128F;

    /// <summary>
    /// 近处淡入距离，0 关闭，用于模型贴脸时的柔化
    /// </summary>
    public float NearFade;

    // 共享后期参数

    /// <summary>
    /// 色调曲线
    /// </summary>
    public ToneCurve Tone = ToneCurve.AcesNarkowicz;

    /// <summary>
    /// 曝光偏移（EV）
    /// </summary>
    public float Exposure;

    /// <summary>
    /// 输出 Gamma
    /// </summary>
    public float Gamma = 2.2F;

    /// <summary>
    /// 白平衡色温（开尔文），6500 为中性
    /// </summary>
    public float WhiteBalance = 6500F;

    /// <summary>
    /// 暗角强度
    /// </summary>
    public float Vignette;

    /// <summary>相机覆盖的固定画面颗粒强度与尺寸（像素）。</summary>
    public float FilmGrainIntensity;
    public float FilmGrainSize = 1F;
    /// <summary>色阶分离级数与混合强度，零强度为关闭。</summary>
    public int PosterizeLevels = 8;
    public float PosterizeMix;
    public Vector3 SplitToneShadows = Vector3.One;
    public Vector3 SplitToneHighlights = Vector3.One;
    public float SplitToneBalance = .5F;
    public float SplitToneMix;

    /// <summary>
    /// 输出抖动开关，消除平滑渐变的色带
    /// </summary>
    public bool Dither = true;

    // 渲染视图参数。实时显卡渲染是基础路径，渐进式路径追踪是可选增强。

    /// <summary>
    /// 路径追踪总开关，默认关闭；关闭时渲染页逐帧使用实时显卡光照路径
    /// </summary>
    public bool PathTracingEnabled;

    /// <summary>
    /// 是否使用 Vulkan 硬件光追加速（显式选择）
    /// </summary>
    /// <remarks>
    /// 只有 <see cref="HardwareRayTracingAuto"/> 为 false 时才是权威值；
    /// 自动模式下由设备能力决定，见渲染管线与设置面板
    /// </remarks>
    public bool UseHardwareRayTracing;

    /// <summary>
    /// 自动采用设备支持的硬件光追加速，默认开启
    /// </summary>
    /// <remarks>
    /// 用户手动关闭后会把它置为 false 并记住 <see cref="UseHardwareRayTracing"/>，
    /// 因此“默认开启、允许关闭、关闭后记住”由项目设置本身承载；
    /// 设备不支持时面板灰显，但不会抹掉项目里保存的选择
    /// </remarks>
    public bool HardwareRayTracingAuto = true;

    /// <summary>
    /// 渲染计算后端，默认使用 Vulkan GPU
    /// </summary>
    public RenderBackend Backend = RenderBackend.Gpu;

    /// <summary>
    /// 每帧新增样本数
    /// </summary>
    public int PathSamplesPerFrame = 1;

    /// <summary>
    /// 累积样本上限，达到后自动停止
    /// </summary>
    public int PathMaxAccumulatedSamples = 1024;

    /// <summary>
    /// 内部分辨率倍率，仅响应渲染配置中的用户显式设置；交互和大型场景优化不得自动降低
    /// </summary>
    public float ResolutionScale = 1.0F;

    /// <summary>超大渲染工作集只自动限制实时 AO/阴影采样，不改变视口分辨率</summary>
    public bool AutoPerformanceScaling = true;

    /// <summary>
    /// 最大弹射次数
    /// </summary>
    public int MaxBounces = 6;

    /// <summary>
    /// 俄罗斯轮盘终止阈值，约 1/255（低于输出最低位）
    /// </summary>
    public float RussianRouletteThreshold = 0.004F;

    /// <summary>
    /// 单样本辐射度截断，抑制萤火虫
    /// </summary>
    public float RadianceClamp = 12F;

    /// <summary>
    /// 全局 GGX 粗糙度
    /// </summary>
    public float PathRoughness = 0.22F;

    /// <summary>
    /// 全局金属度，逐弹射按此概率在电介质与金属之间选择
    /// </summary>
    public float PathMetalness;

    /// <summary>
    /// 电介质基础反射率 F0（Schlick）
    /// </summary>
    public float PathSpecularF0 = 0.04F;

    /// <summary>
    /// 路径追踪中的太阳直接光采样开关。
    /// </summary>
    public bool SunDirectLighting = true;

    /// <summary>
    /// 自发光体素是否参与照明
    /// </summary>
    public bool EmissiveVoxels = true;

    /// <summary>对暴露发光面直接采样，并与表面反弹命中进行 MIS 合并。</summary>
    public bool EmissiveDirectSampling = true;
    /// <summary>对较大的发光面集合按光源树的重要性分布采样。</summary>
    public bool EmissiveLightTree = true;

    /// <summary>
    /// 自发光强度
    /// </summary>
    public float EmissiveIntensity = 12F;

    /// <summary>
    /// 半透明体素的 Beer 吸收浓度
    /// </summary>
    public float TransmissiveAbsorption = 1.0F;

    /// <summary>
    /// 薄板玻璃近似：壁厚不超过阈值的透明薄壳只透射与吸收，不按界面法线折射。
    /// 体素化的圆柱壳/球壳每一格台阶都有独立法线，逐格折射会把它们放大成棱镜条纹，
    /// 而真实的窗玻璃、灯罩玻璃是薄板，本身不该聚焦成像。
    /// </summary>
    public bool ThinGlassApproximation = true;

    /// <summary>
    /// 薄板判定的厚度上限（世界格）。沿入射方向穿过的同种介质长度不超过该值即视为薄板；
    /// 0 表示关闭判定，所有介质仍按体折射处理。
    /// </summary>
    public float ThinGlassMaxThickness = 2F;

    /// <summary>
    /// 景深开关
    /// </summary>
    public bool DepthOfField;

    /// <summary>
    /// 是否启用渲染视图的快速降噪
    /// </summary>
    public bool PathDenoising = true;

    /// <summary>
    /// 快速降噪强度，越高越平滑但细节越柔和
    /// </summary>
    public float PathDenoiseStrength = 0.65F;

    /// <summary>
    /// 是否启用渲染视图的 Bloom 辉光，实时渲染和路径追踪都可使用
    /// </summary>
    public bool BloomEnabled = true;

    /// <summary>
    /// 场景视图立体着色：按太阳方向对体素面做明暗，让纯色方块具备体积可读性
    /// </summary>
    public bool SceneShading = true;

    /// <summary>
    /// 场景窗口距离 LOD 配置，默认关闭
    /// </summary>
    public VoxelLodSettings SceneLod = new();

    /// <summary>
    /// 渲染窗口距离 LOD 配置，默认关闭
    /// </summary>
    public VoxelLodSettings RenderLod = new();

    /// <summary>
    /// Bloom 强度，过曝亮部按此倍率叠加回画面
    /// </summary>
    public float BloomIntensity = 0.35F;

    /// <summary>
    /// Bloom 亮度阈值，低于该亮度的像素不参与辉光，1.0 表示只溢出到 HDR 的高光
    /// </summary>
    public float BloomThreshold = 1.0F;

    /// <summary>
    /// Bloom 采样半径（像素），控制辉光扩散范围
    /// </summary>
    public float BloomRadius = 12F;

    /// <summary>
    /// 薄透镜光圈半径
    /// </summary>
    public float ApertureRadius = 0.08F;

    /// <summary>
    /// 对焦距离，格数
    /// </summary>
    public float FocusDistance = 18F;

    /// <summary>
    /// 渲染视图相机视场角（度），场景视图焦距与网格保持一致不受影响
    /// </summary>
    public float CameraFovDegrees = 50F;

    /// <summary>
    /// 创建渲染设置控件交互期间使用的 GPU 参数副本。相机导航不使用此副本；
    /// 编辑器控件拖动时每次提交一个路径样本，保留所选光照与后处理。
    /// </summary>
    internal RenderSettings CreateInteractionPreview()
    {
        var preview = (RenderSettings)MemberwiseClone();
        preview.PathSamplesPerFrame = 1;
        preview.SceneLod = SceneLod.Clone();
        preview.RenderLod = RenderLod.Clone();
        return preview;
    }

    /// <summary>
    /// 为大型渲染工作集创建仅用于视口显示的性能副本；项目设置、视口分辨率与导出参数保持原样。
    /// 只为实时 AO/阴影采样设置上限，避免通过缩小图像再放大牺牲空间精度。
    /// </summary>
    internal RenderSettings CreateLargeScenePreview(int worksetCells)
    {
        var preview = (RenderSettings)MemberwiseClone();
        if (!AutoPerformanceScaling || worksetCells <= 20_000) return preview;
        if (!PathTracingEnabled)
        {
            preview.AoSamples = Math.Min(AoSamples, LargeSceneAoSampleLimit(worksetCells));
            // 太阳质量由用户选择，不随工作集规模自动降低。
        }

        preview.SceneLod = SceneLod.Clone();
        preview.RenderLod = RenderLod.Clone();
        return preview;
    }

    /// <summary>按合并后 GPU 工作集规模计算实时 AO 样本上限，不改变渲染图像尺寸</summary>
    internal static int LargeSceneAoSampleLimit(int worksetCells) => worksetCells switch
    {
        > 250_000 => 4,
        > 80_000 => 6,
        > 20_000 => 8,
        _ => int.MaxValue,
    };

    /// <summary>
    /// 创建场景窗口使用的 GPU 基础显示配置：只保留体素颜色、轮廓和场景明暗，
    /// 不让渲染窗口的路径追踪、Bloom、AO、雾和色彩特效污染编辑器视口。
    /// </summary>
    internal RenderSettings CreateScenePreview()
    {
        var preview = (RenderSettings)MemberwiseClone();
        preview.Backend = RenderBackend.Gpu;
        preview.PathTracingEnabled = false;
        preview.ShowGround = false;
        preview.ShowWater = false;
        preview.AmbientOcclusionEnabled = false;
        preview.EmissiveVoxels = false;
        preview.FogDensity = 0F;
        preview.NearFade = 0F;
        preview.BloomEnabled = false;
        preview.PaletteShading = false;
        preview.Saturation = 1F;
        preview.HueShiftDegrees = 0F;
        preview.ColorContrast = 1F;
        preview.Brightness = 1F;
        preview.Tone = ToneCurve.Linear;
        preview.Exposure = 0F;
        preview.Gamma = 2.2F;
        preview.WhiteBalance = 6500F;
        preview.Vignette = 0F;
        preview.Dither = false;
        preview.SceneLod = SceneLod.Clone();
        preview.RenderLod = RenderLod.Clone();
        return preview;
    }

    /// <summary>创建导出任务使用的独立参数快照，避免用户继续编辑配置时改变排队任务。</summary>
    internal RenderSettings CloneForExport()
    {
        var clone = (RenderSettings)MemberwiseClone();
        clone.SceneLod = SceneLod.Clone();
        clone.RenderLod = RenderLod.Clone();
        return clone;
    }

    /// <summary>
    /// 由方位角与仰角推导的单位太阳方向（从被照表面指向太阳）
    /// </summary>
    public Vector3 SunDirection
    {
        get
        {
            var azimuth = MathF.PI * Math.Clamp(SunAzimuthDegrees, 0F, 360F) / 180F;
            var elevation = MathF.PI * Math.Clamp(SunElevationDegrees, -5F, 90F) / 180F;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(elevation) * MathF.Sin(azimuth),
                MathF.Sin(elevation),
                MathF.Cos(elevation) * MathF.Cos(azimuth)));
        }
    }

    /// <summary>
    /// 把所有参数夹回安全范围，NaN 与无穷由 SanitizeNaN 先行处理
    /// </summary>
    public void ClampAll()
    {
        GroundColor = ClampChannel(GroundColor);
        SceneBackgroundColor = ClampChannel(SceneBackgroundColor);
        SunColor = ClampChannel(SunColor);
        SkyTop = ClampChannel(SkyTop);
        SkyHorizon = ClampChannel(SkyHorizon);
        FogColor = ClampChannel(FogColor);
        WaterColor = ClampChannel(WaterColor);
        SunAzimuthDegrees = Wrap(SunAzimuthDegrees, 360F);
        SunElevationDegrees = Math.Clamp(SunElevationDegrees, 2F, 90F);
        SunIntensity = Math.Clamp(SunIntensity, 0F, 8F);
        SoftFillColor = ClampChannel(SoftFillColor);
        SoftFillIntensity = Math.Clamp(SoftFillIntensity, 0F, 64F);
        SoftFillSize = Math.Clamp(SoftFillSize, 0.1F, 8F);
        SoftFillDistance = Math.Clamp(SoftFillDistance, 0.1F, 8F);
        ShadowSoftness = Math.Clamp(ShadowSoftness, 0F, 0.5F);
        ShadowSamples = Math.Clamp(ShadowSamples, 1, 8);
        if (!Enum.IsDefined(SunShadowMethod)) SunShadowMethod = SunShadowMethod.Progressive;
        SunAccumulationFrames = Math.Clamp(SunAccumulationFrames, 1, 2048);
        SunShadowMapResolution = SunShadowMapResolution <= 512 ? 512 : SunShadowMapResolution <= 1024 ? 1024 : 2048;
        AoSamples = Math.Clamp(AoSamples, 4, 64);
        VoxelIndirectLightSamples = Math.Clamp(VoxelIndirectLightSamples, 1, 32);
        ProbeBakeRays = Math.Clamp(ProbeBakeRays, 8, 256);
        ProbeBakeMaxProbes = Math.Clamp(ProbeBakeMaxProbes, 1024, 262144);
        ProbeBakeSmoothing = Math.Clamp(ProbeBakeSmoothing, 0, 4);
        ProbeBakeDistance = float.IsFinite(ProbeBakeDistance) ? Math.Clamp(ProbeBakeDistance, 0F, 100000F) : 0F;
        RealtimeTemporalWeight = float.IsFinite(RealtimeTemporalWeight)
            ? Math.Clamp(RealtimeTemporalWeight, 0F, .98F)
            : 0F;
        AoStrength = Math.Clamp(AoStrength, 0F, 1F);
        AoPower = Math.Clamp(AoPower, 0.2F, 4F);
        VoxelIndirectLightStrength = Math.Clamp(float.IsFinite(VoxelIndirectLightStrength) ? VoxelIndirectLightStrength : 0.35F, 0F, 4F);
        ProbeVolumeGiStrength = Math.Clamp(float.IsFinite(ProbeVolumeGiStrength) ? ProbeVolumeGiStrength : 0.5F, 0F, 4F);
        SkyAmbient = Math.Clamp(SkyAmbient, 0F, 2F);
        AmbientTint = Math.Clamp(AmbientTint, 0F, 1F);
        Saturation = Math.Clamp(Saturation, 0F, 2.5F);
        HueShiftDegrees = Wrap(HueShiftDegrees, 360F);
        ColorContrast = Math.Clamp(ColorContrast, 0.2F, 2.5F);
        Brightness = Math.Clamp(Brightness, 0.1F, 3F);
        HueCycleSpeed = Math.Clamp(HueCycleSpeed, 0F, 180F);
        FogDensity = Math.Clamp(FogDensity, 0F, 0.05F);
        FogStart = Math.Clamp(FogStart, 0F, 100000F);
        FogSkyBlend = Math.Clamp(FogSkyBlend, 0F, 1F);
        FogMaxOpacity = Math.Clamp(FogMaxOpacity, 0F, 1F);
        FogHeight = Math.Clamp(FogHeight, -100000F, 100000F);
        FogHeightFalloff = Math.Clamp(FogHeightFalloff, 0F, 0.2F);
        FogAnisotropy = Math.Clamp(FogAnisotropy, -0.95F, 0.95F);
        FogSunScatterStrength = Math.Clamp(FogSunScatterStrength, 0F, 4F);
        FogSunScatterColor = ClampChannel(FogSunScatterColor);
        FogAlbedo = Math.Clamp(FogAlbedo, 0F, 1F);
        FogInscatterStrength = Math.Clamp(FogInscatterStrength, 0F, 4F);
        FogSkyFogStrength = Math.Clamp(FogSkyFogStrength, 0F, 1F);
        FogVolumetricSteps = Math.Clamp(FogVolumetricSteps, 0, 64);
        FogViewDistance = Math.Clamp(FogViewDistance, 0F, 100000F);
        FogNoiseStrength = Math.Clamp(FogNoiseStrength, 0F, 1F);
        FogNoiseScale = Math.Clamp(FogNoiseScale, 0.001F, 1F);
        FogNoiseSpeed = Math.Clamp(FogNoiseSpeed, 0F, 4F);
        WaterHeight = Math.Clamp(WaterHeight, -100000F, 100000F);
        WaterWaveSize = Math.Clamp(WaterWaveSize, 0F, 4F);
        WaterWaveLength = Math.Clamp(WaterWaveLength, 0.1F, 1024F);
        WaterRoughness = Math.Clamp(WaterRoughness, 0.01F, 1F);
        WaterAnimationSpeed = Math.Clamp(WaterAnimationSpeed, -8F, 8F);
        WaterFlowDirection = Vector2.Normalize(WaterFlowDirection.LengthSquared() > 0.0001F ? WaterFlowDirection : Vector2.UnitX);
        WaterDetailStrength = Math.Clamp(WaterDetailStrength, 0F, 2F);
        WaterSpecularStrength = Math.Clamp(WaterSpecularStrength, 0F, 4F);
        WeatherWindStrength = Math.Clamp(WeatherWindStrength, 0F, 4F);
        WeatherWindDirection = Wrap(WeatherWindDirection, 360F);
        WeatherRainIntensity = Math.Clamp(WeatherRainIntensity, 0F, 1F);
        WeatherSnowIntensity = Math.Clamp(WeatherSnowIntensity, 0F, 1F);
        WeatherStormIntensity = Math.Clamp(WeatherStormIntensity, 0F, 1F);
        WeatherHeatHaze = Math.Clamp(WeatherHeatHaze, 0F, 1F);
        WeatherLightningFrequency = Math.Clamp(WeatherLightningFrequency, 0F, 4F);
        WeatherSeed = Math.Clamp(WeatherSeed, 0, 65535);
        WeatherViewDistance = Math.Clamp(WeatherViewDistance, 1F, 256F);
        NearFade = Math.Clamp(NearFade, 0F, 512F);
        GroundFade = Math.Clamp(GroundFade, 0F, 4096F);
        Exposure = Math.Clamp(Exposure, -6F, 6F);
        Gamma = Math.Clamp(Gamma, 1F, 3.5F);
        WhiteBalance = Math.Clamp(WhiteBalance, 1500F, 12000F);
        Vignette = Math.Clamp(Vignette, 0F, 1F);
        PathSamplesPerFrame = Math.Clamp(PathSamplesPerFrame, 1, 8);
        PathMaxAccumulatedSamples = Math.Clamp(PathMaxAccumulatedSamples, 16, 16384);
        ResolutionScale = Math.Clamp(ResolutionScale, 0.25F, 1F);
        MaxBounces = Math.Clamp(MaxBounces, 1, 12);
        RussianRouletteThreshold = Math.Clamp(RussianRouletteThreshold, 0.0005F, 0.1F);
        RadianceClamp = Math.Clamp(RadianceClamp, 1F, 1000F);
        PathRoughness = Math.Clamp(PathRoughness, 0.01F, 1F);
        PathMetalness = Math.Clamp(PathMetalness, 0F, 1F);
        PathSpecularF0 = Math.Clamp(PathSpecularF0, 0F, 0.6F);
        EmissiveIntensity = Math.Clamp(EmissiveIntensity, 0F, 64F);
        TransmissiveAbsorption = Math.Clamp(TransmissiveAbsorption, 0F, 8F);
        ThinGlassMaxThickness = Math.Clamp(ThinGlassMaxThickness, 0F, 16F);
        PathDenoiseStrength = Math.Clamp(PathDenoiseStrength, 0F, 1F);
        BloomIntensity = Math.Clamp(BloomIntensity, 0F, 2F);
        BloomThreshold = Math.Clamp(BloomThreshold, 0F, 8F);
        BloomRadius = Math.Clamp(BloomRadius, 1F, 64F);
        ApertureRadius = Math.Clamp(ApertureRadius, 0F, 1F);
        FocusDistance = Math.Clamp(FocusDistance, 1F, 512F);
        CameraFovDegrees = Math.Clamp(CameraFovDegrees, 20F, 120F);
        Backend = Enum.IsDefined(Backend) ? Backend : RenderBackend.Gpu;
        (SceneLod ??= new VoxelLodSettings()).ClampAll();
        (RenderLod ??= new VoxelLodSettings()).ClampAll();
    }

    /// <summary>
    /// 夹住颜色通道到 0 到 4（允许 HDR 强度但杜绝失控值）
    /// </summary>
    /// <param name="value">待夹取向量</param>
    /// <returns>夹取后的向量</returns>
    private static Vector3 ClampChannel(Vector3 value) => new(
        Math.Clamp(value.X, 0F, 4F), Math.Clamp(value.Y, 0F, 4F), Math.Clamp(value.Z, 0F, 4F));

    /// <summary>
    /// 把角度类数值环绕进 [0, period)
    /// </summary>
    /// <param name="value">原始值</param>
    /// <param name="period">周期</param>
    /// <returns>环绕后的值</returns>
    private static float Wrap(float value, float period)
    {
        if (!float.IsFinite(value)) return 0F;
        value %= period;
        return value < 0 ? value + period : value;
    }

    /// <summary>
    /// 原子保存配置到指定路径
    /// </summary>
    /// <param name="path">目标文件</param>
    public void SaveTo(string path) => MVoxel.Core.AtomicFile.WriteText(path, ToJson());

    /// <summary>
    /// 序列化为 JSON 文本，面板用它对比"参数是否变化"
    /// </summary>
    /// <returns>JSON 文本</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 从文件加载配置，损坏内容使用默认值，保证编辑器可以启动
    /// </summary>
    /// <param name="path">配置文件路径</param>
    /// <returns>可用的配置实例</returns>
    public static RenderSettings LoadFrom(string path)
    {
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RenderSettings();
        }
    }

    /// <summary>
    /// 从 JSON 文本还原配置并净化
    /// </summary>
    /// <param name="json">JSON 文本</param>
    /// <returns>夹取到安全范围后的配置</returns>
    public static RenderSettings FromJson(string json)
    {
        var loaded = JsonSerializer.Deserialize<RenderSettings>(json, JsonOptions) ?? new RenderSettings();
        loaded.SanitizeNaN();
        loaded.ClampAll();
        return loaded;
    }

    /// <summary>
    /// 把 NaN 与无穷替换为各自默认值，先于 ClampAll 执行
    /// </summary>
    public void SanitizeNaN()
    {
        SunAzimuthDegrees = Safe(SunAzimuthDegrees, 135F);
        SunElevationDegrees = Safe(SunElevationDegrees, 55F);
        SunIntensity = Safe(SunIntensity, 1.05F);
        ShadowSoftness = Safe(ShadowSoftness, 0.06F);
        AoStrength = Safe(AoStrength, 0.85F);
        AoPower = Safe(AoPower, 1.2F);
        SkyAmbient = Safe(SkyAmbient, 0.42F);
        AmbientTint = Safe(AmbientTint, 0.15F);
        Saturation = Safe(Saturation, 1F);
        HueShiftDegrees = Safe(HueShiftDegrees, 0F);
        ColorContrast = Safe(ColorContrast, 1F);
        Brightness = Safe(Brightness, 1F);
        HueCycleSpeed = Safe(HueCycleSpeed, 20F);
        FogDensity = Safe(FogDensity, 0F);
        FogStart = Safe(FogStart, 0F);
        FogSkyBlend = Safe(FogSkyBlend, 0.55F);
        FogMaxOpacity = Safe(FogMaxOpacity, 0.92F);
        FogHeight = Safe(FogHeight, 0F);
        FogHeightFalloff = Safe(FogHeightFalloff, 0F);
        FogAnisotropy = Safe(FogAnisotropy, 0.6F);
        FogSunScatterStrength = Safe(FogSunScatterStrength, 1F);
        FogSunScatterColor = SafeChannel(FogSunScatterColor, new Vector3(1F, 0.9F, 0.75F));
        FogAlbedo = Safe(FogAlbedo, 1F);
        FogInscatterStrength = Safe(FogInscatterStrength, 1F);
        FogSkyFogStrength = Safe(FogSkyFogStrength, 0.55F);
        FogViewDistance = Safe(FogViewDistance, 0F);
        FogNoiseStrength = Safe(FogNoiseStrength, 0F);
        FogNoiseScale = Safe(FogNoiseScale, 0.035F);
        FogNoiseSpeed = Safe(FogNoiseSpeed, 0.6F);
        WaterHeight = Safe(WaterHeight, 0F);
        WaterWaveSize = Safe(WaterWaveSize, 0.35F);
        WaterWaveLength = Safe(WaterWaveLength, 16F);
        WaterRoughness = Safe(WaterRoughness, 0.12F);
        WaterAnimationSpeed = Safe(WaterAnimationSpeed, 0.7F);
        WaterFlowDirection = SafeVector2(WaterFlowDirection, new Vector2(1F, 0.35F));
        WaterDetailStrength = Safe(WaterDetailStrength, 0.35F);
        WaterSpecularStrength = Safe(WaterSpecularStrength, 0.8F);
        WeatherWindStrength = Safe(WeatherWindStrength, 0F);
        WeatherWindDirection = Safe(WeatherWindDirection, 0.35F);
        WeatherRainIntensity = Safe(WeatherRainIntensity, 0F);
        WeatherSnowIntensity = Safe(WeatherSnowIntensity, 0F);
        WeatherStormIntensity = Safe(WeatherStormIntensity, 0F);
        WeatherHeatHaze = Safe(WeatherHeatHaze, 0F);
        WeatherLightningFrequency = Safe(WeatherLightningFrequency, 0F);
        WeatherViewDistance = Safe(WeatherViewDistance, 128F);
        WaterColor = SafeChannel(WaterColor, new Vector3(0.08F, 0.32F, 0.42F));
        NearFade = Safe(NearFade, 0F);
        GroundFade = Safe(GroundFade, 0F);
        Exposure = Safe(Exposure, 0F);
        Gamma = Safe(Gamma, 2.2F);
        WhiteBalance = Safe(WhiteBalance, 6500F);
        Vignette = Safe(Vignette, 0F);
        ResolutionScale = Safe(ResolutionScale, 1F);
        RussianRouletteThreshold = Safe(RussianRouletteThreshold, 0.004F);
        RadianceClamp = Safe(RadianceClamp, 12F);
        PathRoughness = Safe(PathRoughness, 0.22F);
        PathMetalness = Safe(PathMetalness, 0F);
        PathSpecularF0 = Safe(PathSpecularF0, 0.04F);
        EmissiveIntensity = Safe(EmissiveIntensity, 12F);
        TransmissiveAbsorption = Safe(TransmissiveAbsorption, 1F);
        ThinGlassMaxThickness = Safe(ThinGlassMaxThickness, 2F);
        PathDenoiseStrength = Safe(PathDenoiseStrength, 0.65F);
        BloomIntensity = Safe(BloomIntensity, 0.35F);
        BloomThreshold = Safe(BloomThreshold, 1F);
        BloomRadius = Safe(BloomRadius, 12F);
        ApertureRadius = Safe(ApertureRadius, 0.08F);
        FocusDistance = Safe(FocusDistance, 18F);
        CameraFovDegrees = Safe(CameraFovDegrees, 50F);
        GroundColor = SafeChannel(GroundColor, Vector3.One);
        SceneBackgroundColor = SafeChannel(SceneBackgroundColor, new Vector3(0.025F, 0.035F, 0.05F));
        SunColor = SafeChannel(SunColor, Vector3.One);
        SkyTop = SafeChannel(SkyTop, new Vector3(0.24F, 0.45F, 0.78F));
        SkyHorizon = SafeChannel(SkyHorizon, new Vector3(0.62F, 0.72F, 0.85F));
        FogColor = SafeChannel(FogColor, new Vector3(0.62F, 0.72F, 0.85F));

        static float Safe(float value, float fallback) => float.IsFinite(value) ? value : fallback;

        static Vector3 SafeChannel(Vector3 value, Vector3 fallback) => new(
            Safe(value.X, fallback.X), Safe(value.Y, fallback.Y), Safe(value.Z, fallback.Z));
    }
         static Vector2 SafeVector2(Vector2 value, Vector2 fallback) =>
             float.IsFinite(value.X) && float.IsFinite(value.Y) && value.LengthSquared() > 0.0001F
                 ? Vector2.Normalize(value) : fallback;
}
