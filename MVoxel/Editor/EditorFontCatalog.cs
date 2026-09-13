using ImGuiNET;
using MVoxel.Editor.Rendering;
using OpenTK.Graphics.OpenGL4;

namespace MVoxel.Editor;

internal sealed class EditorFontCatalog
{
    internal ImFontPtr Title { get; private set; }
    private float _dpiScale;

    internal unsafe void Load(ImGuiController controller, float dpiScale = 1F)
    {
        // The controller starts its first empty frame in the constructor.
        // End that frame before changing the atlas; changing it during NewFrame is
        // what causes cimgui's "locked ImFontAtlas" assertion.
        controller.Render();

        var atlas = ImGui.GetIO().Fonts;
        ImGui.GetIO().NativePtr->FontDefault = null;
        atlas.Clear();
        atlas.Flags |= ImFontAtlasFlags.NoPowerOfTwoHeight | ImFontAtlasFlags.NoBakedLines;
        atlas.TexDesiredWidth = 4096;
        // Include UI punctuation and the full BMP CJK range: SimplifiedCommon
        // omits characters such as 渲, which were showing fallback boxes.
        var ranges = atlas.GetGlyphRangesChineseFull();
        var choice = ChooseBody(dpiScale);
        AddPixelFont(choice.BasePixels, choice.Multiplier, ranges);
        // The brand title is Latin only; do not bake another oversized CJK atlas.
        Title = AddPixelFont(12, Math.Max(2, (int)MathF.Round(dpiScale * 2F)), atlas.GetGlyphRangesDefault());
        controller.RecreateFontDeviceTexture();

        GL.GetInteger(GetPName.TextureBinding2D, out var previousTexture);
        GL.BindTexture(TextureTarget.Texture2D, (int)atlas.TexID);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        ImGui.GetIO().FontGlobalScale = 1F;
        // With nearest sampling, ImGui's baked AA line texture must not be used.
        ImGui.GetStyle().AntiAliasedLinesUseTex = false;
        _dpiScale = dpiScale;
    }

    internal void Refresh(ImGuiController controller, float dpiScale)
    {
        if (Math.Abs(_dpiScale - dpiScale) > 0.01F)
        {
            Load(controller, dpiScale);
        }
    }

    internal static (int BasePixels, int Multiplier) ChooseBody(float dpiScale)
    {
        var desired = 12F * Math.Clamp(dpiScale, 1F, 4F);
        // 8px CJK shapes lose too many distinguishing strokes for body text.
        // Keep that asset for compact decoration, not enlarged Chinese menus.
        return (from pixels in new[] { 12, 10 }
                from multiplier in Enumerable.Range(1, 6)
                orderby Math.Abs(pixels * multiplier - desired), multiplier
                select (pixels, multiplier)).First();
    }

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
            // These unmodified Fusion Pixel fonts use 100 units per pixel.
            // hhea ascent - descent = (base pixels + 4) * 100; stb_truetype
            // sizes the whole line, so loading at just 12 shrank the 12px grid.
            return ImGui.GetIO().Fonts.AddFontFromFileTTF(path, (pixels + 4) * multiplier, config, ranges);
        }
        finally
        {
            config.Destroy();
        }
    }
}
