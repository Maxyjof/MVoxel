using System.Numerics;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Editor.Templates;

/// <summary>模板实际几何的正交色彩缩略图；后台串行生成，缓存像素，不保留大型世界。</summary>
internal static class TemplateLearningPreview
{
    private const int Width = 180;
    private const int Height = 108;
    private static readonly Dictionary<ProjectTemplateId, Task<uint[]>> Cache = [];
    private static readonly SemaphoreSlim Builder = new(1, 1);

    internal static void Draw(ProjectTemplateId id)
    {
        if (id != ProjectTemplateId.Empty)
        {
            if (!Cache.TryGetValue(id, out var task))
            {
                task = Task.Run(async () =>
                {
                    await Builder.WaitAsync().ConfigureAwait(false);
                    try { return Build(id); }
                    catch (Exception exception)
                    {
                        MVoxelLog.Error($"模板缩略图生成失败：{id}", exception);
                        return Array.Empty<uint>();
                    }
                    finally { Builder.Release(); }
                });
                Cache[id] = task;
            }
            var origin = ImGui.GetCursorScreenPos();
            var size = new Vector2(Math.Min(360F, ImGui.GetContentRegionAvail().X), 0F);
            size.Y = size.X * Height / Width;
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + size, 0xff25201b, 4F);
            if (task.IsCompletedSuccessfully && task.Result.Length == Width * Height)
            {
                var pixels = task.Result;
                var step = size / new Vector2(Width, Height);
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    if (pixels[y * Width + x] == 0) continue;
                    var start = origin + new Vector2(x, y) * step;
                    draw.AddRectFilled(start, start + step, pixels[y * Width + x]);
                }
            }
            else draw.AddText(origin + new Vector2(12F, 12F), 0xffdddddd,
                task.IsCompleted ? "预览不可用，仍可创建项目" : "正在后台生成模板预览…");
            ImGui.Dummy(size);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("来自实际模板几何与体素颜色的正交缩略图，不是最终光照渲染；缩略图缓存不改变编辑器分辨率或模型精度。");
        }
        ImGui.SeparatorText("三步练习");
        var steps = Steps(id);
        for (var index = 0; index < steps.Length; index++) ImGui.TextWrapped($"{index + 1}. {steps[index]}");
    }

    internal static string[] Steps(ProjectTemplateId id) => id switch
    {
        ProjectTemplateId.Empty => ["在层级中创建模型对象", "绘制体素并添加光照和相机", "Ctrl+S 保存项目"],
        ProjectTemplateId.CornellBox => ["观察红绿墙、顶灯和中性方块", "比较墙面附近的间接光与阴影", "提高采样并观察噪声收敛"],
        ProjectTemplateId.SphereOptics => ["观察玻璃球后的棋盘变化", "比较金属球和玻璃球的反射", "调整材质参数并等待采样收敛"],
        ProjectTemplateId.GlassStudy => ["观察透明、有色和毛玻璃后的棋盘", "选择材质资产调整粗糙度和折射率", "切到渲染视图等待采样并比较"],
        ProjectTemplateId.CloudAndFog => ["选择薄雾或云朵并观察背景", "在材质中调整雾化密度和散射色", "切换渲染视图观察体积效果"],
        ProjectTemplateId.WaterStudy => ["观察浅水和深水下的棋盘", "选择清水材质调整吸收和折射率", "切换渲染视图比较池底与水面"],
        ProjectTemplateId.DefaultRoom => ["在层级中选择桌椅、杯子或壁画", "展开纸杯对象并选择可乐", "切换渲染视图，Ctrl+S 保存项目"],
        ProjectTemplateId.XianxiaContinent => ["在层级中选择山体、宗门或浮空岛并聚焦", "锁定一个分件，取色并修改局部体素", "切换场景相机，保存项目并导出画面"],
        ProjectTemplateId.MaterialLab => ["选择玻璃、金属与发光样品并取色", "从笔刷材质进入共享材质，比较粗糙度与透明度", "切到渲染视图，保存并导出 PNG"],
        ProjectTemplateId.PaletteWorkshop => ["取样色卡并在空白处绘制", "在项目设置中创建与保存调色盘副本", "比较新笔刷颜色与已有方块，再保存项目"],
        ProjectTemplateId.SparseWorld => ["在层级中选择远距离地标并聚焦", "锁定地标，观察远近移动与编辑归属", "打开信息统计，保存并导出选中地标"],
        ProjectTemplateId.LodPlayground => ["比较不同建筑的细节层级", "手动开关场景 LOD 并移动相机比较", "通过信息统计比较工作集，再保存配置"],
        ProjectTemplateId.ProceduralTerrain => ["切面查看地形内部的材质层", "锁定地形模型，使用球形笔刷修改局部", "关闭切面，保存并导出地形模型"],
        ProjectTemplateId.MinecraftValley => ["在层级中隐藏一棵独立树木", "显示并锁定树木，添加树叶方块", "对比地形与树木归属，保存并导出树模型"],
        ProjectTemplateId.Architecture => ["选择立柱或拱廊，观察对象组织", "锁定部件，用直线或长方体工具补建", "切换渲染观察材质，保存并导出"],
        ProjectTemplateId.CameraGallery => ["依次切换场景中的渲染相机", "改变构图与景深，比较观察结果", "保存项目，批量导出全部相机 PNG"],
        ProjectTemplateId.LargeWorld => ["从层级聚焦中央圣坛或高塔", "开信息统计，比较导航和局部编辑的耗时", "锁定一个模型进行小范围编辑，再保存导出"],
        ProjectTemplateId.RenderingGallery => ["分别选择近景船只、中景建筑和远景灯塔", "取样透明或发光体素并观察材质", "切换相机，保存并导出最终画面"],
        ProjectTemplateId.MoonbaseOutpost => ["隐藏穹舱，观察内部与太阳能阵列", "锁定月面车或阵列，取色后局部搭建", "恢复显隐，保存并导出相机画面"],
        ProjectTemplateId.ClockworkConservatory => ["用切面查看玻璃穹顶与机械骨架", "选择花床，取样颜色并修改局部植物", "退出切面，比较玻璃与黄铜的渲染并导出"],
        ProjectTemplateId.DesertCaravanserai => ["选择拱廊、棕榈与织棚并逐一隐藏", "锁定建筑，用相同材质增添一个模块", "调整渲染构图，保存并导出绿洲场景"],
        _ => ["选择小屋并观察场景部件", "锁定目标，取色并添加门口或屋顶方块", "Ctrl+S 保存，导出模型或相机画面"],
    };

    private static uint[] Build(ProjectTemplateId id)
    {
        var world = ProjectTemplateCatalog.BuildWorld(id, VoxelPalette.Build());
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var cell in world.EnumerateOccupied())
        {
            var p = cell.Coordinate;
            var x = (p.X - (double)p.Z) * .7071;
            var y = (p.X + (double)p.Z) * .35355 - p.Y * .866;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        var pixels = new uint[Width * Height];
        if (!double.IsFinite(minX)) return pixels;
        var scale = Math.Min((Width - 12) / Math.Max(1, maxX - minX), (Height - 12) / Math.Max(1, maxY - minY));
        var depths = Enumerable.Repeat(double.NegativeInfinity, Width * Height).ToArray();
        var offsetX = (Width - (maxX - minX) * scale) / 2;
        var offsetY = (Height - (maxY - minY) * scale) / 2;
        var radius = Math.Clamp((int)Math.Ceiling(scale * .65), 1, 6);
        foreach (var cell in world.EnumerateOccupied())
        {
            var p = cell.Coordinate;
            var x = (int)Math.Round(((p.X - (double)p.Z) * .7071 - minX) * scale + offsetX);
            var y = (int)Math.Round(((p.X + (double)p.Z) * .35355 - p.Y * .866 - minY) * scale + offsetY);
            var depth = p.X + (double)p.Z + p.Y;
            var c = cell.Value.Color;
            var color = (uint)(c.R | c.G << 8 | c.B << 16) | 0xff000000U;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var px = x + dx; var py = y + dy;
                if (px < 0 || px >= Width || py < 0 || py >= Height) continue;
                var index = py * Width + px;
                if (depth < depths[index]) continue;
                depths[index] = depth;
                pixels[index] = color;
            }
        }
        return pixels;
    }
}
