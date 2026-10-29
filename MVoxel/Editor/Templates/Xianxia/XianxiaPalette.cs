using MVoxel.Voxels;

namespace MVoxel.Editor.Templates.Xianxia;

/// <summary>
/// 仙域九霄配色与材质工厂：地形、建筑、云海共用同一套色谱，
/// 所有分件只从这里取色，保证整块大陆在同一张画面里色彩关系稳定
/// </summary>
internal static class XianxiaPalette
{
    /// <summary>无属性透明体素的材质槽</summary>
    public const ushort SlotPlain = 0;

    /// <summary>玻璃材质槽，与项目 .material 文件对应</summary>
    public const ushort SlotGlass = 1;

    /// <summary>水质材质槽</summary>
    public const ushort SlotWater = 2;

    /// <summary>金属材质槽</summary>
    public const ushort SlotMetal = 3;

    /// <summary>自发光材质槽</summary>
    public const ushort SlotGlow = 4;

    /// <summary>灵玉材质槽</summary>
    public const ushort SlotJade = 5;

    /// <summary>云雾材质槽</summary>
    public const ushort SlotCloud = 6;

    /// <summary>绸缎与帷幔材质槽</summary>
    public const ushort SlotCloth = 7;

    // 中州与天柱

    /// <summary>月白，仙家墙面与云纹</summary>
    public static readonly Rgba32 MoonWhite = new(230, 232, 224);

    /// <summary>素白，殿堂梁柱的底白</summary>
    public static readonly Rgba32 SilkWhite = new(242, 236, 222);

    /// <summary>青玉，栏柱与灵器</summary>
    public static readonly Rgba32 JadeGreen = new(96, 162, 132);

    /// <summary>深潭玉，玉石暗部</summary>
    public static readonly Rgba32 JadeDeep = new(58, 116, 104);

    /// <summary>苔痕，台基与古石缝</summary>
    public static readonly Rgba32 Moss = new(104, 128, 74);

    /// <summary>春草，低地草色</summary>
    public static readonly Rgba32 GrassLight = new(144, 178, 108);

    /// <summary>竹青，林地与竹叶</summary>
    public static readonly Rgba32 Bamboo = new(118, 158, 96);

    /// <summary>松墨，密林暗部</summary>
    public static readonly Rgba32 PineDeep = new(52, 90, 70);

    /// <summary>古松皮，树干</summary>
    public static readonly Rgba32 Bark = new(88, 68, 50);

    /// <summary>黛瓦，屋面</summary>
    public static readonly Rgba32 RoofTile = new(56, 70, 88);

    /// <summary>黛瓦高光，屋脊</summary>
    public static readonly Rgba32 RoofTileLight = new(82, 96, 116);

    /// <summary>朱砂，宫墙与幡旗</summary>
    public static readonly Rgba32 Cinnabar = new(178, 62, 50);

    /// <summary>鎏金，斗拱与殿顶宝顶</summary>
    public static readonly Rgba32 Gold = new(214, 172, 92);

    /// <summary>赤铜，钟与炉</summary>
    public static readonly Rgba32 Copper = new(172, 112, 66);

    /// <summary>青石，铺地与台阶</summary>
    public static readonly Rgba32 Stone = new(142, 144, 148);

    /// <summary>青石暗部</summary>
    public static readonly Rgba32 StoneDark = new(104, 108, 116);

    /// <summary>玄岩，山体基岩</summary>
    public static readonly Rgba32 Basalt = new(84, 90, 98);

    /// <summary>玄岩暗部，深渊裂隙</summary>
    public static readonly Rgba32 BasaltDeep = new(56, 60, 68);

    /// <summary>土黄，地表土层</summary>
    public static readonly Rgba32 Earth = new(150, 122, 86);

    /// <summary>赭石，崖壁风化层</summary>
    public static readonly Rgba32 Ochre = new(168, 112, 70);

    /// <summary>沙金，海滩与灵田</summary>
    public static readonly Rgba32 SandGold = new(206, 186, 146);

    // 水与天

    /// <summary>沧海，深水</summary>
    public static readonly Rgba32 SeaDeep = new(30, 76, 108);

    /// <summary>浅海，近岸水色</summary>
    public static readonly Rgba32 SeaShallow = new(74, 146, 166);

    /// <summary>冰蓝，冰壳</summary>
    public static readonly Rgba32 IceBlue = new(178, 214, 226);

    /// <summary>寒渊，冰层深处</summary>
    public static readonly Rgba32 IceDeep = new(116, 162, 186);

    /// <summary>云雾，流云与浮岛底座</summary>
    public static readonly Rgba32 Cloud = new(224, 228, 234);

    /// <summary>灵光蓝，灵脉与阵纹</summary>
    public static readonly Rgba32 SpiritBlue = new(116, 194, 226);

    /// <summary>灵青，灵光核心</summary>
    public static readonly Rgba32 SpiritCyan = new(150, 236, 224);

    /// <summary>熔火，岩浆本色</summary>
    public static readonly Rgba32 LavaOrange = new(198, 86, 38);

    /// <summary>熔光，岩浆高亮</summary>
    public static readonly Rgba32 LavaGlow = new(255, 140, 58);

    /// <summary>煞红，禁地纹样</summary>
    public static readonly Rgba32 BloodRed = new(148, 42, 44);

    /// <summary>莲粉，花树与莲池</summary>
    public static readonly Rgba32 LotusPink = new(224, 166, 174);

    /// <summary>樱粉，花树高光</summary>
    public static readonly Rgba32 CherryPink = new(238, 184, 190);

    /// <summary>紫藤，花藤与高阶灵植</summary>
    public static readonly Rgba32 Wisteria = new(140, 108, 150);

    /// <summary>骨白，遗迹与兽骸</summary>
    public static readonly Rgba32 BoneWhite = new(222, 214, 196);

    /// <summary>墨夜，碑刻与暗部</summary>
    public static readonly Rgba32 InkNight = new(30, 36, 48);

    /// <summary>银灰，金属与器皿</summary>
    public static readonly Rgba32 Silver = new(186, 192, 200);

    /// <summary>银杏金，秋色林冠</summary>
    public static readonly Rgba32 AutumnGold = new(226, 168, 72);

    /// <summary>枫红，秋色林冠深色</summary>
    public static readonly Rgba32 MapleRed = new(196, 76, 52);

    /// <summary>素色体素</summary>
    /// <param name="color">基础色</param>
    /// <returns>不透明体素</returns>
    public static VoxelValue Solid(Rgba32 color) => new(color);

    /// <summary>带确定性明度抖动的天然体素，抖动只用于自然表面</summary>
    /// <param name="color">基础色</param>
    /// <param name="x">体素 X</param>
    /// <param name="y">体素 Y</param>
    /// <param name="z">体素 Z</param>
    /// <param name="seed">抖动种子</param>
    /// <param name="amount">抖动幅度</param>
    /// <returns>抖动后的体素</returns>
    public static VoxelValue Grain(Rgba32 color, int x, int y, int z, int seed, float amount = .045F) =>
        new(TemplateKit.Jitter(color, x, y, z, seed, amount));

    /// <summary>自发光体素</summary>
    /// <param name="color">发光色</param>
    /// <returns>发光体素</returns>
    public static VoxelValue Glow(Rgba32 color) => new(color, SlotGlow, VoxelFlags.Emissive);

    /// <summary>玻璃体素</summary>
    /// <param name="alpha">透明度</param>
    /// <returns>玻璃体素</returns>
    public static VoxelValue Glass(byte alpha = 118) =>
        new(new Rgba32(196, 226, 236, alpha), SlotGlass, VoxelFlags.Translucent);

    /// <summary>水体素</summary>
    /// <param name="alpha">透明度</param>
    /// <returns>水体素</returns>
    public static VoxelValue Water(byte alpha = 172) =>
        new(new Rgba32(70, 136, 170, alpha), SlotWater, VoxelFlags.Translucent);

    /// <summary>云雾体素</summary>
    /// <param name="alpha">透明度</param>
    /// <returns>云体素</returns>
    public static VoxelValue CloudMist(byte alpha = 138) =>
        new(new Rgba32(226, 232, 240, alpha), SlotCloud, VoxelFlags.Translucent);

    /// <summary>灵玉体素</summary>
    /// <param name="color">玉色</param>
    /// <returns>灵玉体素</returns>
    public static VoxelValue Jade(Rgba32 color) => new(color, SlotJade);

    /// <summary>金属体素</summary>
    /// <param name="color">金属色</param>
    /// <returns>金属体素</returns>
    public static VoxelValue Metal(Rgba32 color) => new(color, SlotMetal);

    /// <summary>绸缎体素</summary>
    /// <param name="color">布色</param>
    /// <returns>绸缎体素</returns>
    public static VoxelValue Cloth(Rgba32 color) => new(color, SlotCloth);

    /// <summary>按倍率调整亮度</summary>
    /// <param name="color">基础色</param>
    /// <param name="factor">倍率</param>
    /// <returns>调整后的颜色</returns>
    public static Rgba32 Shade(Rgba32 color, float factor) => new(
        (byte)Math.Clamp(MathF.Round(color.R * factor), 1, 255),
        (byte)Math.Clamp(MathF.Round(color.G * factor), 1, 255),
        (byte)Math.Clamp(MathF.Round(color.B * factor), 1, 255),
        color.A);

    /// <summary>两色线性插值</summary>
    /// <param name="from">起点色</param>
    /// <param name="to">终点色</param>
    /// <param name="t">插值比例</param>
    /// <returns>插值结果</returns>
    public static Rgba32 Blend(Rgba32 from, Rgba32 to, float t)
    {
        var clamped = Math.Clamp(t, 0F, 1F);
        return new Rgba32(
            (byte)MathF.Round(from.R + (to.R - from.R) * clamped),
            (byte)MathF.Round(from.G + (to.G - from.G) * clamped),
            (byte)MathF.Round(from.B + (to.B - from.B) * clamped),
            (byte)MathF.Round(from.A + (to.A - from.A) * clamped));
    }

    /// <summary>
    /// 项目调色板：前 8 项是透明度递进的云雾与水体色，
    /// 之后 20 个色族各 12 级明度，末尾 8 项留给灵光、熔岩与墨色
    /// </summary>
    /// <returns>256 项色表</returns>
    public static Rgba32[] BuildProjectPalette()
    {
        var colors = new Rgba32[VoxelPalette.EntryCount];
        colors[0] = Rgba32.TransparentBlack;
        for (var index = 1; index < VoxelPalette.TransparentCount; index++)
        {
            colors[index] = new Rgba32(198, 224, 240, (byte)(index * 30));
        }

        var families = new[]
        {
            JadeGreen, Bamboo, PineDeep, Moss, GrassLight, SeaDeep,
            IceDeep, IceBlue, SandGold, Earth, Ochre, Stone,
            Basalt, RoofTile, Gold, Cinnabar, LotusPink, Wisteria,
            MoonWhite, Cloud,
        };
        const int shades = 12;
        for (var family = 0; family < families.Length; family++)
        {
            for (var step = 0; step < shades; step++)
            {
                var t = step / (float)(shades - 1);
                var dark = Shade(families[family], .28F + .18F * t);
                var light = Blend(dark, families[family], .35F + .5F * t);
                colors[VoxelPalette.TransparentCount + family * shades + step] =
                    Blend(light, SilkWhite, MathF.Pow(t, 3F) * .45F);
            }
        }

        var extras = new[]
        {
            InkNight, BoneWhite, Silver, SpiritBlue, SpiritCyan, LavaGlow, BloodRed, JadeDeep,
        };
        var start = VoxelPalette.TransparentCount + families.Length * shades;
        for (var index = 0; index < extras.Length && start + index < colors.Length; index++)
        {
            colors[start + index] = extras[index];
        }

        return colors;
    }
}
