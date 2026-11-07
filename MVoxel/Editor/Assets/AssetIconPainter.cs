using System.Drawing;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 用 ImGui 绘制列表按类型程序化绘制资产图标，不引入任何图标字体或位图资产
/// </summary>
internal static class AssetIconPainter
{
    /// <summary>
    /// 在指定矩形内绘制某个类型的资产图标
    /// </summary>
    /// <param name="draw">目标绘制列表</param>
    /// <param name="kind">资产类型</param>
    /// <param name="min">图标左上角</param>
    /// <param name="size">图标边长</param>
    /// <param name="selected">是否处于选中态</param>
    internal static void Draw(ImDrawListPtr draw, ProjectAssetKind kind, Vector2 min, float size, bool selected)
    {
        var main = IconColor(kind, selected);
        var shade = Color.FromArgb(main.A, Math.Max(0, main.R - 42), Math.Max(0, main.G - 42), Math.Max(0, main.B - 42));
        var light = Color.FromArgb(main.A, Math.Min(255, main.R + 36), Math.Min(255, main.G + 36), Math.Min(255, main.B + 36));
        var outline = Color.FromArgb(220, 12, 18, 25);
        Func<Vector2, Vector2> p = value => min + new Vector2(size * value.X, size * value.Y);

        switch (kind)
        {
            case ProjectAssetKind.Folder:
                draw.AddRectFilled(p(new Vector2(.08F, .27F)), p(new Vector2(.57F, .47F)), ToPackedColor(light), 3F);
                draw.AddRectFilled(p(new Vector2(.06F, .38F)), p(new Vector2(.94F, .88F)), ToPackedColor(main), 4F);
                draw.AddRect(p(new Vector2(.06F, .38F)), p(new Vector2(.94F, .88F)), ToPackedColor(outline), 4F, ImDrawFlags.None, 1F);
                break;
            case ProjectAssetKind.VoxelModel:
            case ProjectAssetKind.Prefab:
                DrawCube(draw, p, main, shade, light, outline, kind == ProjectAssetKind.Prefab);
                break;
            case ProjectAssetKind.Scene:
                draw.AddRectFilled(p(new Vector2(.1F, .12F)), p(new Vector2(.9F, .86F)), ToPackedColor(shade), 3F);
                draw.AddRect(p(new Vector2(.1F, .12F)), p(new Vector2(.9F, .86F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
                draw.AddLine(p(new Vector2(.18F, .72F)), p(new Vector2(.82F, .72F)), ToPackedColor(light), 2F);
                draw.AddLine(p(new Vector2(.34F, .72F)), p(new Vector2(.62F, .30F)), ToPackedColor(main), 2F);
                draw.AddLine(p(new Vector2(.62F, .30F)), p(new Vector2(.80F, .72F)), ToPackedColor(main), 2F);
                break;
            case ProjectAssetKind.Material:
            case ProjectAssetKind.Palette:
                draw.AddCircleFilled(p(new Vector2(.5F, .5F)), size * .34F, ToPackedColor(main), 20);
                draw.AddCircle(p(new Vector2(.5F, .5F)), size * .34F, ToPackedColor(outline), 20, 1F);
                draw.AddCircleFilled(p(new Vector2(.40F, .4F)), size * .09F, ToPackedColor(light), 12);
                if (kind == ProjectAssetKind.Palette)
                {
                    draw.AddCircleFilled(p(new Vector2(.62F, .43F)), size * .08F, ToPackedColor(Color.FromArgb(255, 238, 146, 92)), 12);
                    draw.AddCircleFilled(p(new Vector2(.56F, .62F)), size * .08F, ToPackedColor(Color.FromArgb(255, 110, 205, 151)), 12);
                }
                break;
            case ProjectAssetKind.Environment:
                draw.AddCircleFilled(p(new Vector2(.5F, .42F)), size * .23F, ToPackedColor(main), 16);
                draw.AddCircleFilled(p(new Vector2(.37F, .58F)), size * .18F, ToPackedColor(light), 16);
                draw.AddCircleFilled(p(new Vector2(.60F, .59F)), size * .20F, ToPackedColor(light), 16);
                draw.AddLine(p(new Vector2(.18F, .75F)), p(new Vector2(.82F, .75F)), ToPackedColor(outline), 1.3F);
                break;
            case ProjectAssetKind.RenderPreset:
                draw.AddRectFilled(p(new Vector2(.14F, .16F)), p(new Vector2(.86F, .84F)), ToPackedColor(shade), 3F);
                draw.AddRect(p(new Vector2(.14F, .16F)), p(new Vector2(.86F, .84F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
                for (var row = 0; row < 3; row++)
                {
                    var y = .32F + row * .18F;
                    draw.AddLine(p(new Vector2(.28F, y)), p(new Vector2(.72F, y)), ToPackedColor(light), 2F);
                    draw.AddCircleFilled(p(new Vector2(.42F + row * .11F, y)), size * .055F, ToPackedColor(main), 10);
                }
                break;
            case ProjectAssetKind.Generator:
            case ProjectAssetKind.Script:
                DrawDocument(draw, p, main, shade, outline, kind == ProjectAssetKind.Generator);
                break;
            case ProjectAssetKind.Image:
                DrawDocument(draw, p, Color.FromArgb(255, 220, 74, 74), shade, outline, false);
                draw.AddText(p(new Vector2(.29F, .43F)), ToPackedColor(Color.White), "PNG");
                break;
            default:
                DrawDocument(draw, p, main, shade, outline, false);
                break;
        }
    }

    private static void DrawCube(ImDrawListPtr draw, Func<Vector2, Vector2> p, Color main, Color shade, Color light, Color outline, bool prefab)
    {
        var top = new[] { p(new Vector2(.5F, .1F)), p(new Vector2(.86F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.14F, .3F)) };
        draw.AddConvexPolyFilled(ref top[0], top.Length, ToPackedColor(light));
        draw.AddQuadFilled(p(new Vector2(.14F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.5F, .88F)), p(new Vector2(.14F, .66F)), ToPackedColor(shade));
        draw.AddQuadFilled(p(new Vector2(.5F, .5F)), p(new Vector2(.86F, .3F)), p(new Vector2(.86F, .66F)), p(new Vector2(.5F, .88F)), ToPackedColor(main));
        draw.AddQuad(p(new Vector2(.14F, .3F)), p(new Vector2(.5F, .5F)), p(new Vector2(.86F, .3F)), p(new Vector2(.5F, .1F)), ToPackedColor(outline), 1F);
        draw.AddLine(p(new Vector2(.5F, .5F)), p(new Vector2(.5F, .88F)), ToPackedColor(outline), 1F);
        if (prefab) draw.AddCircleFilled(p(new Vector2(.78F, .78F)), 4F, ToPackedColor(Color.FromArgb(255, 101, 190, 255)), 10);
    }

    private static void DrawDocument(ImDrawListPtr draw, Func<Vector2, Vector2> p, Color main, Color shade, Color outline, bool generated)
    {
        draw.AddRectFilled(p(new Vector2(.2F, .1F)), p(new Vector2(.8F, .9F)), ToPackedColor(main), 3F);
        draw.AddTriangleFilled(p(new Vector2(.62F, .1F)), p(new Vector2(.8F, .1F)), p(new Vector2(.8F, .28F)), ToPackedColor(shade));
        draw.AddRect(p(new Vector2(.2F, .1F)), p(new Vector2(.8F, .9F)), ToPackedColor(outline), 3F, ImDrawFlags.None, 1F);
        for (var row = 0; row < 3; row++)
        {
            var y = .40F + row * .14F;
            draw.AddLine(p(new Vector2(.32F, y)), p(new Vector2(.67F, y)), ToPackedColor(shade), 1.6F);
        }
        if (generated) draw.AddCircleFilled(p(new Vector2(.7F, .75F)), 4F, ToPackedColor(Color.FromArgb(255, 100, 218, 151)), 10);
    }

    private static Color IconColor(ProjectAssetKind kind, bool selected) => kind switch
    {
        ProjectAssetKind.Folder => Color.FromArgb(255, 91, 166, 224),
        ProjectAssetKind.Scene => Color.FromArgb(255, 94, 190, 174),
        ProjectAssetKind.VoxelModel => Color.FromArgb(255, 115, 163, 245),
        ProjectAssetKind.Prefab => Color.FromArgb(255, 132, 143, 246),
        ProjectAssetKind.Material => Color.FromArgb(255, 224, 158, 92),
        ProjectAssetKind.Palette => Color.FromArgb(255, 206, 122, 216),
        ProjectAssetKind.Environment => Color.FromArgb(255, 105, 180, 231),
        ProjectAssetKind.RenderPreset => Color.FromArgb(255, 223, 192, 103),
        ProjectAssetKind.Generator => Color.FromArgb(255, 99, 215, 145),
        ProjectAssetKind.Script => Color.FromArgb(255, 132, 196, 246),
        ProjectAssetKind.Document => Color.FromArgb(255, 170, 183, 201),
        ProjectAssetKind.Image => Color.FromArgb(255, 224, 86, 86),
        _ => selected ? EditorPalette.Icon : Color.FromArgb(255, 143, 157, 177),
    };

    /// <summary>
    /// 取得某类资产的类型主色，供卡片底板与色条复用同一套配色
    /// </summary>
    /// <param name="kind">资产类型</param>
    /// <param name="selected">是否处于选中态</param>
    /// <returns>类型主色</returns>
    internal static Color KindColor(ProjectAssetKind kind, bool selected) => IconColor(kind, selected);

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
