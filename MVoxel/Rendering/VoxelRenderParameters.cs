using System.Numerics;
using System.Text;

namespace MVoxel.Rendering;

/// <summary>
/// 把 <see cref="RenderSettings"/> 与逐帧相机/区域状态打包成一个扁平 float 块，
/// C# 侧与 GLSL 着色器按同一组命名槽位访问；着色器源码内嵌 <see cref="Defines"/>
/// 生成的常量表，两侧永远不可能漂移
/// </summary>
public sealed class VoxelRenderParameters
{
    /// <summary>
    /// 参数块总槽数（float 个数），SSBO 按此大小上传
    /// </summary>

    // 相机基向量与逐帧标量
    /// <summary>相机位置 X</summary>
    public const int CamX = 0, CamY = 1, CamZ = 2, Focal = 3;
    /// <summary>视线方向与帧序号</summary>
    public const int ForwardX = 4, ForwardY = 5, ForwardZ = 6, Frame = 7;
    /// <summary>右方向与视口宽（设备像素）</summary>
    public const int RightX = 8, RightY = 9, RightZ = 10, ViewWidth = 11;
    /// <summary>上方向与视口高（设备像素）</summary>
    public const int UpX = 12, UpY = 13, UpZ = 14, ViewHeight = 15;

    // 体素工作集区域
    /// <summary>工作集最小角与世界坐标原点偏移、步进预算</summary>
    public const int RegionMinX = 16, RegionMinY = 17, RegionMinZ = 18, MaxSteps = 19;
    /// <summary>工作集尺寸与发光灯数量</summary>
    public const int RegionSizeX = 20, RegionSizeY = 21, RegionSizeZ = 22, LightCount = 23;

    // 太阳与阴影
    /// <summary>太阳方向与强度</summary>
    public const int SunX = 24, SunY = 25, SunZ = 26, SunIntensity = 27;
    /// <summary>太阳颜色与半影锥</summary>
    public const int SunColorR = 28, SunColorG = 29, SunColorB = 30, ShadowSoftness = 31;
    /// <summary>阴影采样数、开关、地面淡出与地面显示</summary>
    public const int ShadowSamples = 32, SunEnabled = 33, GroundFade = 34, GroundShown = 35;
    /// <summary>地面颜色与近处淡入</summary>
    public const int GroundR = 36, GroundG = 37, GroundB = 38, NearFade = 39;

    // 天空与补光
    /// <summary>天顶色与天空环境光</summary>
    public const int SkyTopR = 40, SkyTopG = 41, SkyTopB = 42, SkyAmbient = 43;
    /// <summary>地平线色与暖色补光</summary>
    public const int SkyHorizonR = 44, SkyHorizonG = 45, SkyHorizonB = 46, AmbientTint = 47;
    /// <summary>雾色与雾浓度</summary>
    public const int FogR = 48, FogG = 49, FogB = 50, FogDensity = 51;

    // 调色着色
    /// <summary>饱和、色相、对比、明度</summary>
    public const int Saturation = 52, HueShift = 53, Contrast = 54, Brightness = 55;
    /// <summary>调色开关、棱线开关、色相相位、AO 开关</summary>
    public const int PaletteShading = 56, Outlines = 57, HuePhase = 58, AoEnabled = 59;
    /// <summary>AO 强度、曲线与实时采样数</summary>
    public const int AoStrength = 60, AoPower = 61, AoSamples = 62, Reserved2 = 63;

    // 后期
    /// <summary>色调曲线、曝光、Gamma、暗角</summary>
    public const int ToneMode = 64, Exposure = 65, Gamma = 66, Vignette = 67;
    /// <summary>白平衡系数与抖动开关</summary>
    public const int WhiteBalanceR = 68, WhiteBalanceG = 69, WhiteBalanceB = 70, Dither = 71;

    // 路径追踪
    /// <summary>弹射上限、粗糙度、金属度、F0</summary>
    public const int MaxBounces = 72, Roughness = 73, Metalness = 74, SpecularF0 = 75;
    /// <summary>轮盘阈值、截断、每帧样本、已累积样本</summary>
    public const int RouletteThreshold = 76, RadianceClamp = 77, SamplesPerFrame = 78, Accumulated = 79;
    /// <summary>自发光强度、吸收、光圈、对焦距离</summary>
    public const int EmissiveIntensity = 80, Absorption = 81, Aperture = 82, FocusDistance = 83;
    /// <summary>景深开关、MIS 开关、路径模式、视场角</summary>
    public const int DepthOfField = 84, MisEnabled = 85, ModePath = 86, FovDegrees = 87;
    /// <summary>抖动种子、网格步长、网格开关</summary>
    public const int Pixel = 88, GridStep = 89, GridShown = 90, ReservedR = 91;
    /// <summary>路径降噪开关与强度</summary>
    public const int DenoiseEnabled = 92, DenoiseStrength = 93;
    /// <summary>当前 GPU 工作集中的占用体素数量与 BVH 节点数量</summary>
    public const int VoxelCount = 94, BvhNodeCount = 95;
    /// <summary>场景相机近平面与远平面</summary>
    public const int NearClip = 96, FarClip = 97;
    /// <summary>场景背景色的线性 RGB 分量</summary>
    public const int SceneBackgroundR = 98, SceneBackgroundG = 99, SceneBackgroundB = 100;
    /// <summary>Compute 分块起点，避免单次 GPU 调度时间过长</summary>
    public const int TileOffsetX = 101, TileOffsetY = 102, DenoisePass = 103;
    /// <summary>环境贴图开关、经纬图宽高与强度</summary>
    public const int EnvEnabled = 104, EnvWidth = 105, EnvHeight = 106, EnvIntensity = 107;
    /// <summary>Bloom 通道标记（0 主通道，1 辉光合成）、开关、强度、阈值与半径</summary>
    public const int BloomPass = 108, BloomEnabled = 109, BloomIntensity = 110, BloomThreshold = 111, BloomRadius = 112;
    /// <summary>场景视图立体着色开关</summary>
    public const int SceneShading = 113;
    /// <summary>Brick 两级求交开关（1 = cells/bvh 缓冲承载砖结构）</summary>
    public const int BrickMode = 114;
    /// <summary>场景窗口基础体素显示开关（不使用实时特效）</summary>
    public const int ScenePreview = 115;
    /// <summary>参与着色的场景本地光源数量（0 表示关闭本地光源路径）</summary>
    public const int SceneLightCount = 116;
    /// <summary>参数槽总数</summary>
    public const int EmissiveDirectSampling = 117;
    public const int SlotCount = 118;

    /// <summary>
    /// 生成 GLSL 的 #define 常量表，保证着色器与 C# 槽位、光源容量常量同源
    /// </summary>
    /// <returns>多行 #define 文本</returns>
    public static string Defines()
    {
        var names = new (string, int)[]
        {
            ("P_CAM_X", CamX), ("P_CAM_Y", CamY), ("P_CAM_Z", CamZ), ("P_FOCAL", Focal),
            ("P_FWD_X", ForwardX), ("P_FWD_Y", ForwardY), ("P_FWD_Z", ForwardZ), ("P_FRAME", Frame),
            ("P_RIGHT_X", RightX), ("P_RIGHT_Y", RightY), ("P_RIGHT_Z", RightZ), ("P_VIEW_W", ViewWidth),
            ("P_UP_X", UpX), ("P_UP_Y", UpY), ("P_UP_Z", UpZ), ("P_VIEW_H", ViewHeight),
            ("P_REGION_MIN_X", RegionMinX), ("P_REGION_MIN_Y", RegionMinY), ("P_REGION_MIN_Z", RegionMinZ), ("P_MAX_STEPS", MaxSteps),
            ("P_REGION_SIZE_X", RegionSizeX), ("P_REGION_SIZE_Y", RegionSizeY), ("P_REGION_SIZE_Z", RegionSizeZ), ("P_LIGHT_COUNT", LightCount),
            ("P_SUN_X", SunX), ("P_SUN_Y", SunY), ("P_SUN_Z", SunZ), ("P_SUN_INTENSITY", SunIntensity),
            ("P_SUN_R", SunColorR), ("P_SUN_G", SunColorG), ("P_SUN_B", SunColorB), ("P_SHADOW_SOFTNESS", ShadowSoftness),
            ("P_SHADOW_SAMPLES", ShadowSamples), ("P_SUN_ENABLED", SunEnabled), ("P_GROUND_FADE", GroundFade), ("P_GROUND_SHOWN", GroundShown),
            ("P_GROUND_R", GroundR), ("P_GROUND_G", GroundG), ("P_GROUND_B", GroundB), ("P_NEAR_FADE", NearFade),
            ("P_SKY_TOP_R", SkyTopR), ("P_SKY_TOP_G", SkyTopG), ("P_SKY_TOP_B", SkyTopB), ("P_SKY_AMBIENT", SkyAmbient),
            ("P_SKY_HORIZON_R", SkyHorizonR), ("P_SKY_HORIZON_G", SkyHorizonG), ("P_SKY_HORIZON_B", SkyHorizonB), ("P_AMBIENT_TINT", AmbientTint),
            ("P_FOG_R", FogR), ("P_FOG_G", FogG), ("P_FOG_B", FogB), ("P_FOG_DENSITY", FogDensity),
            ("P_SATURATION", Saturation), ("P_HUE_SHIFT", HueShift), ("P_CONTRAST", Contrast), ("P_BRIGHTNESS", Brightness),
            ("P_PALETTE_SHADING", PaletteShading), ("P_OUTLINES", Outlines), ("P_HUE_PHASE", HuePhase), ("P_AO_ENABLED", AoEnabled),
            ("P_AO_STRENGTH", AoStrength), ("P_AO_POWER", AoPower), ("P_AO_SAMPLES", AoSamples),
            ("P_TONE_MODE", ToneMode), ("P_EXPOSURE", Exposure), ("P_GAMMA", Gamma), ("P_VIGNETTE", Vignette),
            ("P_WB_R", WhiteBalanceR), ("P_WB_G", WhiteBalanceG), ("P_WB_B", WhiteBalanceB), ("P_DITHER", Dither),
            ("P_MAX_BOUNCES", MaxBounces), ("P_ROUGHNESS", Roughness), ("P_METALNESS", Metalness), ("P_SPECULAR_F0", SpecularF0),
            ("P_ROULETTE_THRESHOLD", RouletteThreshold), ("P_RADIANCE_CLAMP", RadianceClamp), ("P_SAMPLES_PER_FRAME", SamplesPerFrame), ("P_ACCUMULATED", Accumulated),
            ("P_EMISSIVE_INTENSITY", EmissiveIntensity), ("P_ABSORPTION", Absorption), ("P_APERTURE", Aperture), ("P_FOCUS_DISTANCE", FocusDistance),
            ("P_DEPTH_OF_FIELD", DepthOfField), ("P_MIS", MisEnabled), ("P_MODE_PATH", ModePath), ("P_FOV_DEGREES", FovDegrees),
            ("P_PIXEL_SEED", Pixel), ("P_GRID_STEP", GridStep), ("P_GRID_SHOWN", GridShown),
            ("P_DENOISE_ENABLED", DenoiseEnabled), ("P_DENOISE_STRENGTH", DenoiseStrength),
            ("P_VOXEL_COUNT", VoxelCount), ("P_BVH_NODE_COUNT", BvhNodeCount),
            ("P_NEAR_CLIP", NearClip), ("P_FAR_CLIP", FarClip),
            ("P_SCENE_BACKGROUND_R", SceneBackgroundR), ("P_SCENE_BACKGROUND_G", SceneBackgroundG),
            ("P_SCENE_BACKGROUND_B", SceneBackgroundB),
            ("P_TILE_OFFSET_X", TileOffsetX), ("P_TILE_OFFSET_Y", TileOffsetY),
            ("P_ENV_ENABLED", EnvEnabled), ("P_ENV_WIDTH", EnvWidth),
            ("P_ENV_HEIGHT", EnvHeight), ("P_ENV_INTENSITY", EnvIntensity),
            ("P_BLOOM_PASS", BloomPass), ("P_BLOOM_ENABLED", BloomEnabled),
            ("P_BLOOM_INTENSITY", BloomIntensity), ("P_BLOOM_THRESHOLD", BloomThreshold),
            ("P_BLOOM_RADIUS", BloomRadius),
            ("P_SCENE_SHADING", SceneShading),
            ("P_BRICK_MODE", BrickMode),
            ("P_SCENE_PREVIEW", ScenePreview),
            ("P_SCENE_LIGHT_COUNT", SceneLightCount),
        };

        var builder = new StringBuilder();
        foreach (var (name, index) in names)
        {
            builder.Append("#define ").Append(name).Append(' ').Append(index).Append('\n');
        }

        // 光源容量不是参数槽，但着色器循环上限同样只能有一个来源
        builder.Append("#define SCENE_LIGHT_CAPACITY ").Append(SceneLight.Capacity).Append('\n');

        return builder.ToString();
    }

    /// <summary>
    /// 色温到白平衡乘子（Tanner Helland 近似，6500K 归一为中性）
    /// 供测试与面板预览共用
    /// </summary>
    /// <param name="kelvin">色温，1500 到 12000</param>
    /// <returns>线性 RGB 乘子</returns>
    public static Vector3 WhiteBalanceMultiplier(float kelvin)
    {
        var temp = Math.Clamp(kelvin, 1500F, 12000F) / 100F;
        float r, g;
        if (temp <= 66F)
        {
            r = 255F;
            g = 99.4708025861F * (float)Math.Log(temp) - 161.1195681661F;
        }
        else
        {
            r = 329.698727446F * (float)Math.Pow(temp - 60F, -0.1332047592F);
            g = 288.1223940075F * (float)Math.Pow(temp - 60F, -0.0755148492F);
        }

        var b = temp >= 66F ? 255F
            : temp <= 19F ? 0F
            : 138.5177312231F * (float)Math.Log(temp - 10F) - 305.0447927307F;

        // 以 6500K 为中性基准归一，均值保持在 1 附近
        var raw = new Vector3(r, g, b) / new Vector3(255F, 255F, 255F);
        var reference = new Vector3(1F, 1F, 1F);
        var neutral = RawAt(6500F);
        raw /= neutral;
        return Vector3.Clamp(raw, new Vector3(0.25F), reference * 4F);

        static Vector3 RawAt(float kelvinValue)
        {
            var t = kelvinValue / 100F;
            var rr = 255F;
            var gg = 99.4708025861F * (float)Math.Log(t) - 161.1195681661F;
            var bb = t <= 19F ? 0F : 138.5177312231F * (float)Math.Log(t - 10F) - 305.0447927307F;
            var value = new Vector3(rr, gg, Math.Max(1F, bb)) / 255F;
            return value;
        }
    }

    /// <summary>
    /// 填充三个渲染入口共享的 float 参数块，任何非有限值都会被清零
    /// </summary>
    /// <param name="settings">当前渲染设置</param>
    /// <param name="frame">本帧相机与区域状态</param>
    /// <param name="pixelSeedSalt">抖动种子盐值</param>
    /// <returns>长度为 <see cref="SlotCount"/> 的 float 数组</returns>
    public static float[] Build(RenderSettings settings, in FrameState frame, float pixelSeedSalt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ClampAll();
        var parameters = new float[SlotCount];

        parameters[CamX] = frame.CameraPosition.X;
        parameters[CamY] = frame.CameraPosition.Y;
        parameters[CamZ] = frame.CameraPosition.Z;
        parameters[ForwardX] = frame.CameraForward.X;
        parameters[ForwardY] = frame.CameraForward.Y;
        parameters[ForwardZ] = frame.CameraForward.Z;
        parameters[RightX] = frame.CameraRight.X;
        parameters[RightY] = frame.CameraRight.Y;
        parameters[RightZ] = frame.CameraRight.Z;
        parameters[UpX] = frame.CameraUp.X;
        parameters[UpY] = frame.CameraUp.Y;
        parameters[UpZ] = frame.CameraUp.Z;
        parameters[Focal] = frame.FocalPixels;
        parameters[Frame] = frame.FrameIndex;
        parameters[ViewWidth] = frame.ViewWidthPixels;
        parameters[ViewHeight] = frame.ViewHeightPixels;
        parameters[Pixel] = pixelSeedSalt;

        parameters[RegionMinX] = frame.RegionMin.X;
        parameters[RegionMinY] = frame.RegionMin.Y;
        parameters[RegionMinZ] = frame.RegionMin.Z;
        parameters[RegionSizeX] = frame.RegionSize.X;
        parameters[RegionSizeY] = frame.RegionSize.Y;
        parameters[RegionSizeZ] = frame.RegionSize.Z;
        parameters[MaxSteps] = frame.MaxSteps;
        parameters[LightCount] = frame.EmissiveLightCount;

        var sun = settings.SunDirection;
        parameters[SunX] = sun.X;
        parameters[SunY] = sun.Y;
        parameters[SunZ] = sun.Z;
        parameters[SunIntensity] = settings.SunEnabled ? settings.SunIntensity : 0F;
        parameters[SunColorR] = settings.SunColor.X;
        parameters[SunColorG] = settings.SunColor.Y;
        parameters[SunColorB] = settings.SunColor.Z;
        parameters[ShadowSoftness] = settings.ShadowSoftness;
        parameters[ShadowSamples] = settings.ShadowSamples;
        parameters[SunEnabled] = settings.SunEnabled ? 1F : 0F;
        parameters[GroundFade] = settings.GroundFade;
        parameters[GroundShown] = settings.ShowGround ? 1F : 0F;
        parameters[GroundR] = settings.GroundColor.X;
        parameters[GroundG] = settings.GroundColor.Y;
        parameters[GroundB] = settings.GroundColor.Z;
        parameters[NearFade] = settings.NearFade;

        parameters[SkyTopR] = settings.SkyTop.X;
        parameters[SkyTopG] = settings.SkyTop.Y;
        parameters[SkyTopB] = settings.SkyTop.Z;
        parameters[SkyAmbient] = settings.SkyAmbient;
        parameters[SkyHorizonR] = settings.SkyHorizon.X;
        parameters[SkyHorizonG] = settings.SkyHorizon.Y;
        parameters[SkyHorizonB] = settings.SkyHorizon.Z;
        parameters[AmbientTint] = settings.AmbientTint;
        parameters[FogR] = settings.FogColor.X;
        parameters[FogG] = settings.FogColor.Y;
        parameters[FogB] = settings.FogColor.Z;
        parameters[FogDensity] = settings.FogDensity;

        parameters[Saturation] = settings.Saturation;
        parameters[HueShift] = settings.HueShiftDegrees + (settings.HueCycle ? frame.HuePhaseDegrees : 0F);
        parameters[Contrast] = settings.ColorContrast;
        parameters[Brightness] = settings.Brightness;
        parameters[PaletteShading] = settings.PaletteShading ? 1F : 0F;
        parameters[Outlines] = frame.ShowOutlines ? 1F : 0F;
        parameters[HuePhase] = frame.HuePhaseDegrees;
        parameters[AoEnabled] = settings.AmbientOcclusionEnabled ? 1F : 0F;
        parameters[AoStrength] = settings.AoStrength;
        parameters[AoPower] = settings.AoPower;
        parameters[AoSamples] = settings.AoSamples;

        parameters[ToneMode] = (float)(int)settings.Tone;
        parameters[Exposure] = settings.Exposure;
        parameters[Gamma] = settings.Gamma;
        parameters[Vignette] = settings.Vignette;
        var whiteBalance = WhiteBalanceMultiplier(settings.WhiteBalance);
        parameters[WhiteBalanceR] = whiteBalance.X;
        parameters[WhiteBalanceG] = whiteBalance.Y;
        parameters[WhiteBalanceB] = whiteBalance.Z;
        parameters[Dither] = settings.Dither ? 1F : 0F;

        parameters[MaxBounces] = settings.MaxBounces;
        parameters[Roughness] = settings.PathRoughness;
        parameters[Metalness] = settings.PathMetalness;
        parameters[SpecularF0] = settings.PathSpecularF0;
        parameters[RouletteThreshold] = settings.RussianRouletteThreshold;
        parameters[RadianceClamp] = settings.RadianceClamp;
        parameters[SamplesPerFrame] = settings.PathSamplesPerFrame;
        parameters[Accumulated] = frame.AccumulatedSamples;
        parameters[EmissiveIntensity] = settings.EmissiveVoxels ? settings.EmissiveIntensity : 0F;
        parameters[EmissiveDirectSampling] = settings.EmissiveDirectSampling ? 1F : 0F;
        parameters[Absorption] = settings.TransmissiveAbsorption;
        parameters[Aperture] = settings.DepthOfField ? settings.ApertureRadius : 0F;
        parameters[FocusDistance] = settings.FocusDistance;
        parameters[DepthOfField] = settings.DepthOfField ? 1F : 0F;
        parameters[MisEnabled] = settings.SunDirectLighting ? 1F : 0F;
        parameters[ModePath] = frame.PathMode ? 1F : 0F;
        parameters[FovDegrees] = settings.CameraFovDegrees;
        parameters[GridStep] = frame.GridStep;
        parameters[GridShown] = frame.ShowGrid ? 1F : 0F;
        parameters[DenoiseEnabled] = settings.PathDenoising ? 1F : 0F;
        parameters[DenoiseStrength] = settings.PathDenoiseStrength;
        var nearClip = float.IsFinite(frame.NearClip) ? Math.Clamp(frame.NearClip, 0.001F, 1_000_000F) : 0.05F;
        var farClip = float.IsFinite(frame.FarClip)
            ? Math.Clamp(frame.FarClip, nearClip + 0.001F, 100_000_000F)
            : 100_000_000F;
        parameters[NearClip] = nearClip;
        parameters[FarClip] = farClip;
        parameters[SceneBackgroundR] = settings.SceneBackgroundColor.X;
        parameters[SceneBackgroundG] = settings.SceneBackgroundColor.Y;
        parameters[SceneBackgroundB] = settings.SceneBackgroundColor.Z;
        parameters[DenoisePass] = 0F;
        parameters[BloomPass] = 0F;
        parameters[BloomEnabled] = settings.BloomEnabled ? 1F : 0F;
        parameters[BloomIntensity] = settings.BloomIntensity;
        parameters[BloomThreshold] = settings.BloomThreshold;
        parameters[BloomRadius] = settings.BloomRadius;
        parameters[SceneShading] = settings.SceneShading ? 1F : 0F;
        parameters[ScenePreview] = frame.ScenePreview ? 1F : 0F;

        for (var index = 0; index < parameters.Length; index++)
        {
            if (!float.IsFinite(parameters[index])) parameters[index] = 0F;
        }

        return parameters;
    }

    /// <summary>
    /// 单帧的相机、区域与模式状态，与可持久化的 RenderSettings 分离
    /// </summary>
    /// <param name="CameraPosition">相机世界位置</param>
    /// <param name="CameraForward">视线单位向量</param>
    /// <param name="CameraRight">右方向单位向量</param>
    /// <param name="CameraUp">上方向单位向量</param>
    /// <param name="FocalPixels">以设备像素计的焦距，与网格投影一致</param>
    /// <param name="ViewWidthPixels">视口宽（设备像素）</param>
    /// <param name="ViewHeightPixels">视口高（设备像素）</param>
    /// <param name="FrameIndex">帧序号，驱动噪声与色相动画</param>
    /// <param name="RegionMin">工作集最小格坐标</param>
    /// <param name="RegionSize">工作集尺寸（格）</param>
    /// <param name="MaxSteps">DDA 步进预算</param>
    /// <param name="EmissiveLightCount">自发光灯数量</param>
    /// <param name="HuePhaseDegrees">色相环当前相位</param>
    /// <param name="ShowOutlines">是否绘制体素棱线</param>
    /// <param name="AccumulatedSamples">路径追踪已累积样本数</param>
    /// <param name="PathMode">true 为渲染视图相机模型</param>
    /// <param name="GridStep">编辑器网格世界步长</param>
    /// <param name="ShowGrid">是否绘制网格</param>
    /// <param name="ScenePreview">是否使用场景窗口的基础体素显示模式</param>
    public readonly record struct FrameState(
        Vector3 CameraPosition,
        Vector3 CameraForward,
        Vector3 CameraRight,
        Vector3 CameraUp,
        float FocalPixels,
        int ViewWidthPixels,
        int ViewHeightPixels,
        int FrameIndex,
        Vector3I RegionMin,
        Vector3I RegionSize,
        int MaxSteps,
        int EmissiveLightCount,
        float HuePhaseDegrees,
        bool ShowOutlines,
        int AccumulatedSamples,
        bool PathMode,
        float GridStep = 0F,
        bool ShowGrid = false,
        bool ScenePreview = false)
    {
        /// <summary>
        /// 相机近平面距离
        /// </summary>
        public float NearClip { get; init; } = 0.05F;

        /// <summary>
        /// 相机远平面距离
        /// </summary>
        public float FarClip { get; init; } = 100_000_000F;
    }

    /// <summary>
    /// 整数三分量，用于工作集区域描述
    /// </summary>
    /// <param name="X">X 分量</param>
    /// <param name="Y">Y 分量</param>
    /// <param name="Z">Z 分量</param>
    public readonly record struct Vector3I(int X, int Y, int Z);
}
