using ImGuiNET;
using MVoxel.Editor.Rendering.Vulkan;
using System.Runtime.InteropServices;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器字体目录：正文用更纱黑体，品牌标题用得意黑，均按 DPI 连续缩放
/// </summary>
internal sealed class EditorFontCatalog
{
    /// <summary>正文字体文件名，放在 Editor/Fonts 下</summary>
    private const string BodyFontFile = "SarasaGothicSC-Regular.ttf";

    /// <summary>标题字体文件名，只包含品牌拉丁字符</summary>
    private const string TitleFontFile = "SmileySans-Oblique.ttf";

    /// <summary>100% DPI 下的正文像素高度</summary>
    private const float BodyPixels = 13F;

    /// <summary>100% DPI 下的标题像素高度</summary>
    private const float TitlePixels = 16F;

    /// <summary>
    /// 品牌标题字体（仅拉丁字符，避免为它烘焙整套 CJK 图集）
    /// </summary>
    internal ImFontPtr Title { get; private set; }

    /// <summary>
    /// 中文字形范围之外的补充区块：ImGui 遇到图集里没有的码位会退回 <c>?</c>，
    /// 而 <c>GetGlyphRangesChineseFull</c> 只覆盖 0x2000-0x206F、CJK 与全角区，
    /// 箭头、数学符号、制表符、几何图形都落在范围外。
    /// </summary>
    private static readonly ushort[] ExtraGlyphRanges =
    [
        0x2190, 0x21FF, // 箭头：→ ← ↑ ↓ ↔
        0x2200, 0x22FF, // 数学运算符：≠ ≈ ≤ ≥
        0x2500, 0x257F, // 制表符：├ └ │ ─
        0x25A0, 0x25FF, // 几何图形：● ○ ■ ▲ ▼ ▶
        0x2600, 0x26FF, // 杂项符号：★ ☆
    ];

    /// <summary>正文图集的字形范围表；ImGui 建图集期间会直接读它，必须保持存活</summary>
    private IntPtr _bodyGlyphRanges;

    private float _dpiScale;

    /// <summary>
    /// 构建（或重建）字体图集并上传 GPU。必须在控制器没有处于 NewFrame 状态时调用
    /// </summary>
    /// <param name="controller">持有 ImGui 上下文的控制器</param>
    /// <param name="dpiScale">当前 DPI 缩放，1 到 4</param>
    internal unsafe void Load(VulkanImGuiController controller, float dpiScale = 1F)
    {
        // 控制器构造时开启了第一个空帧，改图集前先把它结束掉
        controller.Render();

        var atlas = ImGui.GetIO().Fonts;
        ImGui.GetIO().NativePtr->FontDefault = null;
        atlas.Clear();
        atlas.Flags |= ImFontAtlasFlags.NoPowerOfTwoHeight | ImFontAtlasFlags.NoBakedLines;
        atlas.TexDesiredWidth = 4096;
        // 采用完整 BMP 中文范围：常用子集缺字会在项目名和说明文字里显示成方块
        var ranges = BuildBodyGlyphRanges(atlas);
        AddVectorFont(BodyFontFile, BodyPixels * dpiScale, ranges);
        // 品牌标题只有拉丁字符，单独小图集，减少显存占用
        Title = AddVectorFont(TitleFontFile, TitlePixels * dpiScale, atlas.GetGlyphRangesDefault());
        controller.RecreateFontDeviceTexture();

        // 字号已经按 DPI 放大，这里不再二次缩放
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
    /// 拼出正文图集的字形范围：ImGui 自带的中文全量范围，加上界面里实际会用到、
    /// 但不在该范围内的符号区块。ImGui 建图集时才读这份表，因此要用非托管内存持有，
    /// 下次重建图集前再释放。
    /// </summary>
    /// <param name="atlas">目标字体图集</param>
    /// <returns>以 0 结尾的范围表指针</returns>
    private IntPtr BuildBodyGlyphRanges(ImFontAtlasPtr atlas)
    {
        if (_bodyGlyphRanges != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bodyGlyphRanges);
            _bodyGlyphRanges = IntPtr.Zero;
        }

        // ImGui 的范围表是 [起点, 终点, 起点, 终点, …, 0] 的 ushort 序列
        var ranges = new List<ushort>(512);
        var source = atlas.GetGlyphRangesChineseFull();
        for (var index = 0; ; index += 2)
        {
            var start = (ushort)Marshal.ReadInt16(source, index * sizeof(ushort));
            if (start == 0) break;
            ranges.Add(start);
            ranges.Add((ushort)Marshal.ReadInt16(source, (index + 1) * sizeof(ushort)));
        }

        ranges.AddRange(ExtraGlyphRanges);

        var buffer = Marshal.AllocHGlobal((ranges.Count + 1) * sizeof(ushort));
        for (var index = 0; index < ranges.Count; index++)
            Marshal.WriteInt16(buffer, index * sizeof(ushort), (short)ranges[index]);
        Marshal.WriteInt16(buffer, ranges.Count * sizeof(ushort), 0);
        _bodyGlyphRanges = buffer;
        return buffer;
    }

    /// <summary>
    /// 从输出目录加载一款矢量字体：按 DPI 连续取字号，开启过采样保证笔画清晰
    /// </summary>
    /// <param name="fileName">Editor/Fonts 下的字体文件名</param>
    /// <param name="pixels">像素高度</param>
    /// <param name="ranges">字形范围表</param>
    /// <returns>新添加的字体句柄</returns>
    private static unsafe ImFontPtr AddVectorFont(string fileName, float pixels, IntPtr ranges)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Editor", "Fonts", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("MVoxel UI font is missing.", path);
        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        try
        {
            // 矢量字体不需要像素对齐，过采样 2 倍让斜线和中文笔画在高 DPI 下保持干净
            config.OversampleH = 2;
            config.OversampleV = 2;
            config.PixelSnapH = false;
            config.RasterizerDensity = 1F;
            return ImGui.GetIO().Fonts.AddFontFromFileTTF(path, MathF.Round(pixels * 2F) / 2F, config, ranges);
        }
        finally
        {
            config.Destroy();
        }
    }
}
