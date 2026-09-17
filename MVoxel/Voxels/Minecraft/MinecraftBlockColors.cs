using System.Text;
using MVoxel.Voxels;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// 方块名到渲染颜色的映射表，未收录方块回退到确定性灰调色
/// </summary>
public static class MinecraftBlockColors
{
    private struct BlockInfo
    {
        public Rgba32 Color;
        public bool Translucent;
        public bool Emissive;
    }

    private static readonly Dictionary<string, BlockInfo> _blocks = Build();

    /// <summary>
    /// 已收录方块数量
    /// </summary>
    public static int Count => _blocks.Count;

    /// <summary>
    /// 判断方块是否已被收录
    /// </summary>
    /// <param name="blockName">方块全名，如 minecraft:stone</param>
    /// <returns>已收录时为真</returns>
    public static bool IsKnown(string blockName) =>
        blockName is not null && _blocks.ContainsKey(blockName);

    /// <summary>
    /// 取方块颜色，未收录时返回确定性回退色
    /// </summary>
    /// <param name="blockName">方块全名</param>
    /// <returns>方块 RGBA 颜色</returns>
    public static Rgba32 ColorOf(string blockName)
    {
        if (blockName is null)
        {
            throw new ArgumentNullException(nameof(blockName));
        }

        if (_blocks.TryGetValue(blockName, out var info))
        {
            return info.Color;
        }

        return Fallback(blockName);
    }

    /// <summary>
    /// 判断方块是否半透明，如玻璃与水
    /// </summary>
    /// <param name="blockName">方块全名</param>
    /// <returns>半透明时为真</returns>
    public static bool IsTranslucent(string blockName) => Flag(blockName, info => info.Translucent);

    /// <summary>
    /// 判断方块是否自发光，如萤石与熔岩
    /// </summary>
    /// <param name="blockName">方块全名</param>
    /// <returns>自发光时为真</returns>
    public static bool IsEmissive(string blockName) => Flag(blockName, info => info.Emissive);

    private static bool Flag(string blockName, Func<BlockInfo, bool> selector)
    {
        if (blockName is null)
        {
            throw new ArgumentNullException(nameof(blockName));
        }

        return _blocks.TryGetValue(blockName, out var info) && selector(info);
    }

    private static Rgba32 Fallback(string blockName)
    {
        var hash = 2166136261u;
        foreach (var character in blockName)
        {
            hash ^= (byte)character;
            hash *= 16777619u;
        }

        var red = (byte)(105 + (hash & 0x1F));
        var green = (byte)(105 + ((hash >> 8) & 0x1F));
        var blue = (byte)(105 + ((hash >> 16) & 0x1F));
        return new Rgba32(red, green, blue, 255);
    }

    private static Dictionary<string, BlockInfo> Build()
    {
        var table = new Dictionary<string, BlockInfo>(StringComparer.Ordinal)
        {
            // 石头与深板岩
            { "minecraft:stone", new() { Color = new(127, 127, 127) } },
            { "minecraft:granite", new() { Color = new(156, 107, 88) } },
            { "minecraft:polished_granite", new() { Color = new(166, 117, 98) } },
            { "minecraft:diorite", new() { Color = new(196, 196, 196) } },
            { "minecraft:polished_diorite", new() { Color = new(206, 206, 206) } },
            { "minecraft:andesite", new() { Color = new(139, 139, 139) } },
            { "minecraft:polished_andesite", new() { Color = new(149, 149, 149) } },
            { "minecraft:deepslate", new() { Color = new(75, 75, 75) } },
            { "minecraft:cobbled_deepslate", new() { Color = new(66, 66, 66) } },
            { "minecraft:polished_deepslate", new() { Color = new(82, 82, 82) } },
            { "minecraft:deepslate_bricks", new() { Color = new(70, 70, 70) } },
            { "minecraft:deepslate_tiles", new() { Color = new(78, 78, 78) } },
            { "minecraft:cracked_deepslate_bricks", new() { Color = new(72, 72, 72) } },
            { "minecraft:cobblestone", new() { Color = new(138, 138, 138) } },
            { "minecraft:mossy_cobblestone", new() { Color = new(122, 139, 107) } },
            { "minecraft:stone_bricks", new() { Color = new(131, 147, 156) } },
            { "minecraft:mossy_stone_bricks", new() { Color = new(121, 137, 126) } },
            { "minecraft:cracked_stone_bricks", new() { Color = new(124, 140, 149) } },
            { "minecraft:chiseled_stone_bricks", new() { Color = new(135, 151, 160) } },
            { "minecraft:tuff", new() { Color = new(107, 107, 107) } },
            { "minecraft:calcite", new() { Color = new(224, 224, 224) } },
            { "minecraft:basalt", new() { Color = new(75, 75, 85) } },
            { "minecraft:polished_basalt", new() { Color = new(85, 85, 95) } },
            { "minecraft:smooth_basalt", new() { Color = new(60, 60, 70) } },
            { "minecraft:blackstone", new() { Color = new(43, 43, 43) } },
            { "minecraft:netherrack", new() { Color = new(107, 48, 48) } },

            // 砖块
            { "minecraft:brick", new() { Color = new(176, 74, 58) } },
            { "minecraft:nether_bricks", new() { Color = new(75, 43, 43) } },
            { "minecraft:red_nether_bricks", new() { Color = new(107, 43, 43) } },
            { "minecraft:end_stone_bricks", new() { Color = new(224, 220, 160) } },

            // 泥土与草
            { "minecraft:dirt", new() { Color = new(139, 111, 71) } },
            { "minecraft:coarse_dirt", new() { Color = new(134, 96, 68) } },
            { "minecraft:grass_block", new() { Color = new(95, 176, 74) } },
            { "minecraft:podzol", new() { Color = new(122, 82, 48) } },
            { "minecraft:mycelium", new() { Color = new(124, 110, 110) } },
            { "minecraft:rooted_dirt", new() { Color = new(120, 96, 60) } },
            { "minecraft:mud", new() { Color = new(112, 90, 58) } },
            { "minecraft:packed_mud", new() { Color = new(122, 100, 66) } },

            // 沙子与砂岩
            { "minecraft:sand", new() { Color = new(219, 203, 143) } },
            { "minecraft:red_sand", new() { Color = new(221, 138, 91) } },
            { "minecraft:sandstone", new() { Color = new(219, 203, 143) } },
            { "minecraft:chiseled_sandstone", new() { Color = new(212, 196, 136) } },
            { "minecraft:cut_sandstone", new() { Color = new(215, 199, 139) } },
            { "minecraft:smooth_sandstone", new() { Color = new(225, 209, 149) } },
            { "minecraft:gravel", new() { Color = new(142, 142, 142) } },
            { "minecraft:clay", new() { Color = new(142, 108, 86) } },

            // 陶瓦十六色
            { "minecraft:terracotta", new() { Color = new(179, 160, 134) } },
            { "minecraft:white_terracotta", new() { Color = new(214, 210, 195) } },
            { "minecraft:orange_terracotta", new() { Color = new(203, 114, 75) } },
            { "minecraft:magenta_terracotta", new() { Color = new(177, 75, 115) } },
            { "minecraft:light_blue_terracotta", new() { Color = new(112, 133, 176) } },
            { "minecraft:yellow_terracotta", new() { Color = new(186, 133, 44) } },
            { "minecraft:lime_terracotta", new() { Color = new(126, 139, 58) } },
            { "minecraft:pink_terracotta", new() { Color = new(193, 119, 132) } },
            { "minecraft:gray_terracotta", new() { Color = new(122, 122, 122) } },
            { "minecraft:light_gray_terracotta", new() { Color = new(163, 163, 163) } },
            { "minecraft:cyan_terracotta", new() { Color = new(79, 124, 124) } },
            { "minecraft:purple_terracotta", new() { Color = new(119, 76, 120) } },
            { "minecraft:blue_terracotta", new() { Color = new(60, 70, 107) } },
            { "minecraft:brown_terracotta", new() { Color = new(110, 70, 48) } },
            { "minecraft:green_terracotta", new() { Color = new(94, 107, 51) } },
            { "minecraft:red_terracotta", new() { Color = new(140, 74, 59) } },
            { "minecraft:black_terracotta", new() { Color = new(49, 38, 21) } },

            // 混凝土十六色
            { "minecraft:white_concrete", new() { Color = new(215, 215, 215) } },
            { "minecraft:orange_concrete", new() { Color = new(240, 118, 7) } },
            { "minecraft:magenta_concrete", new() { Color = new(189, 68, 178) } },
            { "minecraft:light_blue_concrete", new() { Color = new(58, 171, 196) } },
            { "minecraft:yellow_concrete", new() { Color = new(222, 211, 26) } },
            { "minecraft:lime_concrete", new() { Color = new(112, 185, 25) } },
            { "minecraft:pink_concrete", new() { Color = new(216, 113, 166) } },
            { "minecraft:gray_concrete", new() { Color = new(58, 58, 58) } },
            { "minecraft:light_gray_concrete", new() { Color = new(142, 142, 142) } },
            { "minecraft:cyan_concrete", new() { Color = new(22, 156, 156) } },
            { "minecraft:purple_concrete", new() { Color = new(137, 60, 174) } },
            { "minecraft:blue_concrete", new() { Color = new(38, 94, 170) } },
            { "minecraft:brown_concrete", new() { Color = new(101, 71, 50) } },
            { "minecraft:green_concrete", new() { Color = new(76, 122, 14) } },
            { "minecraft:red_concrete", new() { Color = new(142, 18, 25) } },
            { "minecraft:black_concrete", new() { Color = new(29, 29, 29) } },

            // 混凝土粉末十六色
            { "minecraft:white_concrete_powder", new() { Color = new(207, 207, 207) } },
            { "minecraft:orange_concrete_powder", new() { Color = new(226, 110, 6) } },
            { "minecraft:magenta_concrete_powder", new() { Color = new(177, 64, 167) } },
            { "minecraft:light_blue_concrete_powder", new() { Color = new(54, 160, 184) } },
            { "minecraft:yellow_concrete_powder", new() { Color = new(210, 199, 24) } },
            { "minecraft:lime_concrete_powder", new() { Color = new(105, 173, 23) } },
            { "minecraft:pink_concrete_powder", new() { Color = new(203, 106, 156) } },
            { "minecraft:gray_concrete_powder", new() { Color = new(54, 54, 54) } },
            { "minecraft:light_gray_concrete_powder", new() { Color = new(133, 133, 133) } },
            { "minecraft:cyan_concrete_powder", new() { Color = new(20, 146, 146) } },
            { "minecraft:purple_concrete_powder", new() { Color = new(128, 56, 163) } },
            { "minecraft:blue_concrete_powder", new() { Color = new(35, 88, 159) } },
            { "minecraft:brown_concrete_powder", new() { Color = new(94, 66, 46) } },
            { "minecraft:green_concrete_powder", new() { Color = new(71, 114, 13) } },
            { "minecraft:red_concrete_powder", new() { Color = new(133, 17, 23) } },
            { "minecraft:black_concrete_powder", new() { Color = new(27, 27, 27) } },

            // 羊毛十六色
            { "minecraft:white_wool", new() { Color = new(240, 240, 240) } },
            { "minecraft:orange_wool", new() { Color = new(235, 136, 68) } },
            { "minecraft:magenta_wool", new() { Color = new(195, 84, 205) } },
            { "minecraft:light_blue_wool", new() { Color = new(102, 137, 207) } },
            { "minecraft:yellow_wool", new() { Color = new(222, 207, 42) } },
            { "minecraft:lime_wool", new() { Color = new(65, 205, 52) } },
            { "minecraft:pink_wool", new() { Color = new(217, 130, 156) } },
            { "minecraft:gray_wool", new() { Color = new(67, 67, 67) } },
            { "minecraft:light_gray_wool", new() { Color = new(160, 160, 160) } },
            { "minecraft:cyan_wool", new() { Color = new(40, 134, 151) } },
            { "minecraft:purple_wool", new() { Color = new(123, 47, 190) } },
            { "minecraft:blue_wool", new() { Color = new(36, 68, 207) } },
            { "minecraft:brown_wool", new() { Color = new(83, 50, 18) } },
            { "minecraft:green_wool", new() { Color = new(86, 124, 27) } },
            { "minecraft:red_wool", new() { Color = new(160, 39, 34) } },
            { "minecraft:black_wool", new() { Color = new(20, 20, 20) } },

            // 地毯十六色
            { "minecraft:white_carpet", new() { Color = new(240, 240, 240) } },
            { "minecraft:orange_carpet", new() { Color = new(235, 136, 68) } },
            { "minecraft:magenta_carpet", new() { Color = new(195, 84, 205) } },
            { "minecraft:light_blue_carpet", new() { Color = new(102, 137, 207) } },
            { "minecraft:yellow_carpet", new() { Color = new(222, 207, 42) } },
            { "minecraft:lime_carpet", new() { Color = new(65, 205, 52) } },
            { "minecraft:pink_carpet", new() { Color = new(217, 130, 156) } },
            { "minecraft:gray_carpet", new() { Color = new(67, 67, 67) } },
            { "minecraft:light_gray_carpet", new() { Color = new(160, 160, 160) } },
            { "minecraft:cyan_carpet", new() { Color = new(40, 134, 151) } },
            { "minecraft:purple_carpet", new() { Color = new(123, 47, 190) } },
            { "minecraft:blue_carpet", new() { Color = new(36, 68, 207) } },
            { "minecraft:brown_carpet", new() { Color = new(83, 50, 18) } },
            { "minecraft:green_carpet", new() { Color = new(86, 124, 27) } },
            { "minecraft:red_carpet", new() { Color = new(160, 39, 34) } },
            { "minecraft:black_carpet", new() { Color = new(20, 20, 20) } },

            // 原木与木头
            { "minecraft:oak_log", new() { Color = new(110, 90, 60) } },
            { "minecraft:spruce_log", new() { Color = new(107, 84, 64) } },
            { "minecraft:birch_log", new() { Color = new(214, 203, 176) } },
            { "minecraft:jungle_log", new() { Color = new(139, 107, 58) } },
            { "minecraft:acacia_log", new() { Color = new(139, 90, 43) } },
            { "minecraft:dark_oak_log", new() { Color = new(75, 58, 38) } },
            { "minecraft:mangrove_log", new() { Color = new(107, 74, 53) } },
            { "minecraft:stripped_oak_log", new() { Color = new(170, 140, 95) } },
            { "minecraft:stripped_spruce_log", new() { Color = new(150, 120, 90) } },
            { "minecraft:stripped_birch_log", new() { Color = new(225, 215, 190) } },
            { "minecraft:stripped_jungle_log", new() { Color = new(190, 155, 95) } },
            { "minecraft:stripped_acacia_log", new() { Color = new(190, 140, 80) } },
            { "minecraft:stripped_dark_oak_log", new() { Color = new(120, 95, 60) } },
            { "minecraft:stripped_mangrove_log", new() { Color = new(165, 120, 85) } },
            { "minecraft:oak_wood", new() { Color = new(110, 90, 60) } },
            { "minecraft:stripped_oak_wood", new() { Color = new(170, 140, 95) } },

            // 木板
            { "minecraft:oak_planks", new() { Color = new(181, 137, 78) } },
            { "minecraft:spruce_planks", new() { Color = new(156, 107, 63) } },
            { "minecraft:birch_planks", new() { Color = new(199, 167, 107) } },
            { "minecraft:jungle_planks", new() { Color = new(181, 137, 74) } },
            { "minecraft:acacia_planks", new() { Color = new(176, 113, 47) } },
            { "minecraft:dark_oak_planks", new() { Color = new(92, 67, 38) } },
            { "minecraft:mangrove_planks", new() { Color = new(139, 90, 58) } },

            // 树叶
            { "minecraft:oak_leaves", new() { Color = new(75, 158, 63) } },
            { "minecraft:spruce_leaves", new() { Color = new(47, 107, 47) } },
            { "minecraft:birch_leaves", new() { Color = new(95, 176, 74) } },
            { "minecraft:jungle_leaves", new() { Color = new(63, 139, 63) } },
            { "minecraft:acacia_leaves", new() { Color = new(107, 168, 75) } },
            { "minecraft:dark_oak_leaves", new() { Color = new(47, 90, 47) } },
            { "minecraft:mangrove_leaves", new() { Color = new(58, 110, 55) } },

            // 玻璃与染色玻璃
            { "minecraft:glass", new() { Color = new(223, 240, 245), Translucent = true } },
            { "minecraft:glass_pane", new() { Color = new(223, 240, 245), Translucent = true } },
            { "minecraft:white_stained_glass", new() { Color = new(240, 240, 245), Translucent = true } },
            { "minecraft:orange_stained_glass", new() { Color = new(240, 150, 90), Translucent = true } },
            { "minecraft:magenta_stained_glass", new() { Color = new(200, 100, 210), Translucent = true } },
            { "minecraft:light_blue_stained_glass", new() { Color = new(120, 160, 220), Translucent = true } },
            { "minecraft:yellow_stained_glass", new() { Color = new(240, 220, 90), Translucent = true } },
            { "minecraft:lime_stained_glass", new() { Color = new(120, 220, 90), Translucent = true } },
            { "minecraft:pink_stained_glass", new() { Color = new(225, 140, 170), Translucent = true } },
            { "minecraft:gray_stained_glass", new() { Color = new(90, 90, 90), Translucent = true } },
            { "minecraft:light_gray_stained_glass", new() { Color = new(170, 170, 170), Translucent = true } },
            { "minecraft:cyan_stained_glass", new() { Color = new(70, 180, 190), Translucent = true } },
            { "minecraft:purple_stained_glass", new() { Color = new(140, 70, 200), Translucent = true } },
            { "minecraft:blue_stained_glass", new() { Color = new(70, 90, 220), Translucent = true } },
            { "minecraft:brown_stained_glass", new() { Color = new(110, 70, 40), Translucent = true } },
            { "minecraft:green_stained_glass", new() { Color = new(90, 160, 50), Translucent = true } },
            { "minecraft:red_stained_glass", new() { Color = new(200, 50, 50), Translucent = true } },
            { "minecraft:black_stained_glass", new() { Color = new(30, 30, 30), Translucent = true } },
            { "minecraft:white_stained_glass_pane", new() { Color = new(240, 240, 245), Translucent = true } },
            { "minecraft:orange_stained_glass_pane", new() { Color = new(240, 150, 90), Translucent = true } },
            { "minecraft:magenta_stained_glass_pane", new() { Color = new(200, 100, 210), Translucent = true } },
            { "minecraft:light_blue_stained_glass_pane", new() { Color = new(120, 160, 220), Translucent = true } },
            { "minecraft:yellow_stained_glass_pane", new() { Color = new(240, 220, 90), Translucent = true } },
            { "minecraft:lime_stained_glass_pane", new() { Color = new(120, 220, 90), Translucent = true } },
            { "minecraft:pink_stained_glass_pane", new() { Color = new(225, 140, 170), Translucent = true } },
            { "minecraft:gray_stained_glass_pane", new() { Color = new(90, 90, 90), Translucent = true } },
            { "minecraft:light_gray_stained_glass_pane", new() { Color = new(170, 170, 170), Translucent = true } },
            { "minecraft:cyan_stained_glass_pane", new() { Color = new(70, 180, 190), Translucent = true } },
            { "minecraft:purple_stained_glass_pane", new() { Color = new(140, 70, 200), Translucent = true } },
            { "minecraft:blue_stained_glass_pane", new() { Color = new(70, 90, 220), Translucent = true } },
            { "minecraft:brown_stained_glass_pane", new() { Color = new(110, 70, 40), Translucent = true } },
            { "minecraft:green_stained_glass_pane", new() { Color = new(90, 160, 50), Translucent = true } },
            { "minecraft:red_stained_glass_pane", new() { Color = new(200, 50, 50), Translucent = true } },
            { "minecraft:black_stained_glass_pane", new() { Color = new(30, 30, 30), Translucent = true } },

            // 液体
            { "minecraft:water", new() { Color = new(59, 108, 220), Translucent = true } },
            { "minecraft:lava", new() { Color = new(255, 96, 0), Emissive = true } },

            // 雪与冰
            { "minecraft:snow", new() { Color = new(245, 245, 245) } },
            { "minecraft:snow_block", new() { Color = new(240, 240, 245) } },
            { "minecraft:ice", new() { Color = new(191, 230, 240), Translucent = true } },
            { "minecraft:packed_ice", new() { Color = new(159, 216, 224), Translucent = true } },
            { "minecraft:blue_ice", new() { Color = new(127, 196, 224), Translucent = true } },
            { "minecraft:frosted_ice", new() { Color = new(150, 200, 220), Translucent = true } },

            // 黑曜石与末地
            { "minecraft:obsidian", new() { Color = new(17, 12, 26) } },
            { "minecraft:crying_obsidian", new() { Color = new(25, 12, 40) } },
            { "minecraft:end_stone", new() { Color = new(224, 220, 160) } },
            { "minecraft:chorus_plant", new() { Color = new(150, 120, 175) } },
            { "minecraft:chorus_fruit", new() { Color = new(130, 100, 160) } },

            // 矿石
            { "minecraft:coal_ore", new() { Color = new(75, 75, 75) } },
            { "minecraft:iron_ore", new() { Color = new(181, 168, 139) } },
            { "minecraft:gold_ore", new() { Color = new(181, 160, 90) } },
            { "minecraft:redstone_ore", new() { Color = new(139, 75, 75) } },
            { "minecraft:emerald_ore", new() { Color = new(63, 139, 90) } },
            { "minecraft:diamond_ore", new() { Color = new(107, 208, 192) } },
            { "minecraft:lapis_ore", new() { Color = new(59, 59, 139) } },
            { "minecraft:nether_gold_ore", new() { Color = new(156, 122, 59) } },
            { "minecraft:copper_ore", new() { Color = new(181, 121, 75) } },
            { "minecraft:deepslate_coal_ore", new() { Color = new(60, 60, 60) } },
            { "minecraft:deepslate_iron_ore", new() { Color = new(120, 112, 95) } },
            { "minecraft:deepslate_gold_ore", new() { Color = new(120, 108, 65) } },
            { "minecraft:deepslate_redstone_ore", new() { Color = new(95, 55, 55) } },
            { "minecraft:deepslate_emerald_ore", new() { Color = new(50, 100, 70) } },
            { "minecraft:deepslate_diamond_ore", new() { Color = new(80, 150, 140) } },
            { "minecraft:deepslate_lapis_ore", new() { Color = new(45, 45, 100) } },
            { "minecraft:deepslate_copper_ore", new() { Color = new(120, 85, 55) } },

            // 金属块
            { "minecraft:iron_block", new() { Color = new(216, 216, 216) } },
            { "minecraft:gold_block", new() { Color = new(230, 200, 74) } },
            { "minecraft:diamond_block", new() { Color = new(107, 224, 208) } },
            { "minecraft:emerald_block", new() { Color = new(63, 221, 122) } },
            { "minecraft:redstone_block", new() { Color = new(192, 59, 59) } },
            { "minecraft:lapis_block", new() { Color = new(43, 43, 216) } },
            { "minecraft:coal_block", new() { Color = new(26, 26, 26) } },
            { "minecraft:copper_block", new() { Color = new(192, 122, 75) } },
            { "minecraft:netherite_block", new() { Color = new(58, 52, 58) } },
            { "minecraft:raw_iron_block", new() { Color = new(190, 170, 140) } },
            { "minecraft:raw_gold_block", new() { Color = new(210, 185, 95) } },
            { "minecraft:raw_copper_block", new() { Color = new(180, 115, 70) } },
            { "minecraft:amethyst_block", new() { Color = new(176, 127, 216) } },

            // 农作物与植物
            { "minecraft:wheat", new() { Color = new(199, 194, 74) } },
            { "minecraft:carrots", new() { Color = new(224, 160, 59) } },
            { "minecraft:potatoes", new() { Color = new(216, 192, 122) } },
            { "minecraft:beetroots", new() { Color = new(192, 59, 59) } },
            { "minecraft:sugar_cane", new() { Color = new(127, 192, 74) } },
            { "minecraft:pumpkin", new() { Color = new(224, 160, 43) } },
            { "minecraft:carved_pumpkin", new() { Color = new(224, 160, 43) } },
            { "minecraft:jack_o_lantern", new() { Color = new(224, 160, 43), Emissive = true } },
            { "minecraft:melon", new() { Color = new(181, 216, 74) } },
            { "minecraft:cactus", new() { Color = new(63, 155, 63) } },
            { "minecraft:vine", new() { Color = new(63, 122, 43) } },
            { "minecraft:tall_grass", new() { Color = new(95, 176, 74) } },
            { "minecraft:fern", new() { Color = new(63, 139, 63) } },
            { "minecraft:large_fern", new() { Color = new(63, 139, 63) } },
            { "minecraft:dandelion", new() { Color = new(240, 220, 60) } },
            { "minecraft:poppy", new() { Color = new(200, 40, 40) } },
            { "minecraft:blue_orchid", new() { Color = new(80, 150, 220) } },
            { "minecraft:allium", new() { Color = new(180, 90, 210) } },
            { "minecraft:azure_bluet", new() { Color = new(230, 230, 230) } },
            { "minecraft:red_tulip", new() { Color = new(200, 40, 40) } },
            { "minecraft:orange_tulip", new() { Color = new(235, 136, 68) } },
            { "minecraft:white_tulip", new() { Color = new(240, 240, 240) } },
            { "minecraft:pink_tulip", new() { Color = new(217, 130, 156) } },
            { "minecraft:oxeye_daisy", new() { Color = new(240, 240, 220) } },
            { "minecraft:brown_mushroom", new() { Color = new(139, 90, 59) } },
            { "minecraft:red_mushroom", new() { Color = new(192, 59, 59) } },
            { "minecraft:mushroom_stem", new() { Color = new(224, 220, 192) } },
            { "minecraft:lily_pad", new() { Color = new(63, 122, 63) } },
            { "minecraft:kelp", new() { Color = new(47, 107, 63) } },
            { "minecraft:cocoa", new() { Color = new(120, 70, 40) } },
            { "minecraft:bamboo", new() { Color = new(120, 170, 70) } },
            { "minecraft:dead_bush", new() { Color = new(120, 100, 60) } },
            { "minecraft:sunflower", new() { Color = new(240, 220, 60) } },
            { "minecraft:lilac", new() { Color = new(195, 84, 205) } },
            { "minecraft:rose_bush", new() { Color = new(200, 40, 40) } },
            { "minecraft:peony", new() { Color = new(217, 130, 156) } },

            // 灯与发光体
            { "minecraft:torch", new() { Color = new(224, 192, 74), Emissive = true } },
            { "minecraft:soul_torch", new() { Color = new(120, 200, 220), Emissive = true } },
            { "minecraft:glowstone", new() { Color = new(230, 216, 74), Emissive = true } },
            { "minecraft:lantern", new() { Color = new(230, 200, 74), Emissive = true } },
            { "minecraft:soul_lantern", new() { Color = new(120, 200, 220), Emissive = true } },
            { "minecraft:redstone_lamp", new() { Color = new(156, 122, 59), Emissive = true } },
            { "minecraft:sea_lantern", new() { Color = new(216, 240, 224), Emissive = true } },
            { "minecraft:end_rod", new() { Color = new(224, 224, 224), Emissive = true } },
            { "minecraft:shroomlight", new() { Color = new(224, 160, 74), Emissive = true } },
            { "minecraft:campfire", new() { Color = new(192, 96, 59), Emissive = true } },
            { "minecraft:soul_campfire", new() { Color = new(120, 200, 220), Emissive = true } },
            { "minecraft:magma_block", new() { Color = new(192, 75, 43), Emissive = true } },

            // 家具与装置
            { "minecraft:chest", new() { Color = new(139, 90, 43) } },
            { "minecraft:ender_chest", new() { Color = new(43, 43, 107) } },
            { "minecraft:furnace", new() { Color = new(139, 139, 139) } },
            { "minecraft:crafting_table", new() { Color = new(181, 137, 78) } },
            { "minecraft:brewing_stand", new() { Color = new(181, 181, 181) } },
            { "minecraft:enchanting_table", new() { Color = new(75, 75, 107) } },
            { "minecraft:anvil", new() { Color = new(75, 75, 75) } },
            { "minecraft:grindstone", new() { Color = new(139, 139, 139) } },
            { "minecraft:loom", new() { Color = new(181, 137, 78) } },
            { "minecraft:cartography_table", new() { Color = new(181, 137, 78) } },
            { "minecraft:stonecutter", new() { Color = new(138, 138, 138) } },
            { "minecraft:fletching_table", new() { Color = new(181, 137, 78) } },
            { "minecraft:smithing_table", new() { Color = new(120, 90, 60) } },
            { "minecraft:barrel", new() { Color = new(150, 110, 70) } },
            { "minecraft:smoker", new() { Color = new(120, 120, 120) } },
            { "minecraft:blast_furnace", new() { Color = new(110, 110, 110) } },
            { "minecraft:bookshelf", new() { Color = new(139, 90, 43) } },
            { "minecraft:tnt", new() { Color = new(192, 59, 59) } },
            { "minecraft:cobweb", new() { Color = new(208, 208, 208) } },

            // 铁轨
            { "minecraft:rail", new() { Color = new(139, 139, 139) } },
            { "minecraft:powered_rail", new() { Color = new(192, 59, 59) } },
            { "minecraft:detector_rail", new() { Color = new(192, 59, 59) } },
            { "minecraft:activator_rail", new() { Color = new(192, 59, 59) } },

            // 床十六色
            { "minecraft:white_bed", new() { Color = new(240, 240, 240) } },
            { "minecraft:orange_bed", new() { Color = new(235, 136, 68) } },
            { "minecraft:magenta_bed", new() { Color = new(195, 84, 205) } },
            { "minecraft:light_blue_bed", new() { Color = new(102, 137, 207) } },
            { "minecraft:yellow_bed", new() { Color = new(222, 207, 42) } },
            { "minecraft:lime_bed", new() { Color = new(65, 205, 52) } },
            { "minecraft:pink_bed", new() { Color = new(217, 130, 156) } },
            { "minecraft:gray_bed", new() { Color = new(67, 67, 67) } },
            { "minecraft:light_gray_bed", new() { Color = new(160, 160, 160) } },
            { "minecraft:cyan_bed", new() { Color = new(40, 134, 151) } },
            { "minecraft:purple_bed", new() { Color = new(123, 47, 190) } },
            { "minecraft:blue_bed", new() { Color = new(36, 68, 207) } },
            { "minecraft:brown_bed", new() { Color = new(83, 50, 18) } },
            { "minecraft:green_bed", new() { Color = new(86, 124, 27) } },
            { "minecraft:red_bed", new() { Color = new(160, 39, 34) } },
            { "minecraft:black_bed", new() { Color = new(20, 20, 20) } },

            // 其余功能方块
            { "minecraft:bee_nest", new() { Color = new(181, 137, 78) } },
            { "minecraft:beehive", new() { Color = new(224, 160, 43) } },
            { "minecraft:nether_wart", new() { Color = new(139, 43, 43) } },
            { "minecraft:amethyst_cluster", new() { Color = new(176, 127, 216), Emissive = true } },
            { "minecraft:pointed_dripstone", new() { Color = new(156, 139, 122) } },
            { "minecraft:dripstone_block", new() { Color = new(139, 122, 107) } },
            { "minecraft:moss_block", new() { Color = new(75, 139, 63) } },
            { "minecraft:moss_carpet", new() { Color = new(75, 139, 63) } },
            { "minecraft:slime_block", new() { Color = new(75, 216, 75), Translucent = true } },
            { "minecraft:honey_block", new() { Color = new(224, 192, 74), Translucent = true } },
            { "minecraft:honeycomb_block", new() { Color = new(224, 160, 43) } },
            { "minecraft:hay_block", new() { Color = new(216, 192, 74) } },
            { "minecraft:sponge", new() { Color = new(224, 216, 122) } },
            { "minecraft:wet_sponge", new() { Color = new(156, 176, 74) } },
            { "minecraft:quartz_block", new() { Color = new(224, 224, 224) } },
            { "minecraft:smooth_quartz", new() { Color = new(228, 228, 228) } },
            { "minecraft:quartz_pillar", new() { Color = new(224, 224, 224) } },
            { "minecraft:bone_block", new() { Color = new(224, 224, 208) } },
            { "minecraft:prismarine", new() { Color = new(75, 139, 139) } },
            { "minecraft:prismarine_bricks", new() { Color = new(75, 139, 139) } },
            { "minecraft:dark_prismarine", new() { Color = new(43, 91, 91) } },
            { "minecraft:soul_sand", new() { Color = new(107, 90, 75) } },
            { "minecraft:soul_soil", new() { Color = new(90, 75, 59) } },
            { "minecraft:nether_wart_block", new() { Color = new(150, 50, 50) } },
            { "minecraft:warped_wart_block", new() { Color = new(60, 120, 110) } },

            // 珊瑚十六色块
            { "minecraft:tube_coral_block", new() { Color = new(240, 107, 107) } },
            { "minecraft:brain_coral_block", new() { Color = new(240, 156, 107) } },
            { "minecraft:bubble_coral_block", new() { Color = new(107, 200, 240) } },
            { "minecraft:fire_coral_block", new() { Color = new(240, 130, 80) } },
            { "minecraft:horn_coral_block", new() { Color = new(230, 220, 120) } },
            { "minecraft:dead_tube_coral_block", new() { Color = new(150, 150, 150) } },
            { "minecraft:dead_brain_coral_block", new() { Color = new(150, 150, 150) } },
            { "minecraft:dead_bubble_coral_block", new() { Color = new(150, 150, 150) } },
            { "minecraft:dead_fire_coral_block", new() { Color = new(150, 150, 150) } },
            { "minecraft:dead_horn_coral_block", new() { Color = new(150, 150, 150) } },
        };

        return table;
    }
}
