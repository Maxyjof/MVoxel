using ImGuiNET;
using MVoxel.Editor.Rendering.Vulkan;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器字体目录：以 Fusion Pixel 像素中文点阵字体构建 ImGui 字体图集，
/// 按 DPI 选择整数放大倍率保证点阵笔画清晰，并在 DPI 变化时自动重建
/// </summary>
internal sealed class EditorFontCatalog
{
    /// <summary>
    /// 品牌标题字体（仅拉丁字符，避免为它烘焙整套 CJK 图集）
    /// </summary>
    internal ImFontPtr Title { get; private set; }
    private float _dpiScale;

    /// <summary>
    /// 构建（或重建）字体图集并上传 GPU。必须在控制器没有处于 NewFrame
    /// 状态时调用，否则触发 ImFontAtlas 锁定断言
    /// </summary>
    /// <param name="controller">持有 ImGui 上下文的控制器</param>
    /// <param name="dpiScale">当前 DPI 缩放（1 到 4）</param>
    internal unsafe void Load(VulkanImGuiController controller, float dpiScale = 1F)
    {
        // 控制器构造时开启了第一个空帧，改图集前先把它结束掉
        controller.Render();

        var atlas = ImGui.GetIO().Fonts;
        ImGui.GetIO().NativePtr->FontDefault = null;
        atlas.Clear();
        atlas.Flags |= ImFontAtlasFlags.NoPowerOfTwoHeight | ImFontAtlasFlags.NoBakedLines;
        atlas.TexDesiredWidth = 4096;
        // 采用完整 BMP 中文范围：SimplifiedCommon 缺少"渲"这类常用字，曾显示为方框
        var ranges = atlas.GetGlyphRangesChineseFull();
        var choice = ChooseBody(dpiScale);
        AddPixelFont(choice.BasePixels, choice.Multiplier, ranges);
        // 品牌标题只有拉丁字符，不重复烘焙大图集
        Title = AddPixelFont(12, Math.Max(2, (int)MathF.Round(dpiScale * 2F)), atlas.GetGlyphRangesDefault());
        controller.RecreateFontDeviceTexture();

        // 像素字体的最近邻采样由 Vulkan Sampler 统一设置
        ImGui.GetIO().FontGlobalScale = 1F;
        ImGui.GetStyle().AntiAliasedLinesUseTex = false;
        _dpiScale = dpiScale;
    }

    /// <summary>
    /// DPI 变化超过阈值时重建图集
    /// </summary>
    /// <param name="controller">持有 ImGui 上下文的控制器</param>
    /// <param name="dpiScale">当前 DPI 缩放</param>
    internal void Refresh(VulkanImGuiController controller, float dpiScale)
    {
        if (Math.Abs(_dpiScale - dpiScale) > 0.01F)
        {
            Load(controller, dpiScale);
        }
    }

    /// <summary>
    /// 为给定 DPI 挑选基础像素字号和整数倍率，目标正文高度为每 DPI 单位 12 像素
    /// 8px 点阵的汉字笔画损失过大，只留给紧凑装饰，正文最低从 10px 起
    /// </summary>
    /// <param name="dpiScale">当前 DPI 缩放</param>
    /// <returns>基础像素与倍率</returns>
    internal static (int BasePixels, int Multiplier) ChooseBody(float dpiScale)
    {
        var desired = 12F * Math.Clamp(dpiScale, 1F, 4F);
        return (from pixels in new[] { 12, 10 }
                from multiplier in Enumerable.Range(1, 6)
                orderby Math.Abs(pixels * multiplier - desired), multiplier
                select (pixels, multiplier)).First();
    }

    /// <summary>
    /// 按输出目录中的点阵字体文件添加一款字体。这些未修改的 Fusion Pixel
    /// 字体以 100 单位每像素设计，行高为（基础像素 + 4）× 100，
    /// stb_truetype 按整行定尺寸，所以只按 12 加载会缩掉 12px 网格
    /// </summary>
    /// <param name="pixels">点阵基础像素数</param>
    /// <param name="multiplier">整数放大倍率</param>
    /// <param name="ranges">字形范围表</param>
    /// <returns>新添加的字体句柄</returns>
    private static unsafe ImFontPtr AddPixelFont(int pixels, int multiplier, IntPtr ranges)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Editor", "Fonts", $"FusionPixel-{pixels}px-SC.ttf");
        if (!File.Exists(path)) throw new FileNotFoundException("MVoxel UI font is missing.", path);
        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        try
        {
            config.OversampleH = 1;
            config.OversampleV = 1;
            config.PixelSnapH = true;
            config.RasterizerDensity = 1F;
            return ImGui.GetIO().Fonts.AddFontFromFileTTF(path, (pixels + 4) * multiplier, config, ranges);
        }
        finally
        {
            config.Destroy();
        }
    }
}
