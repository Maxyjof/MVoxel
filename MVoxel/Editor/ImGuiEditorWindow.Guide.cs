using System.Numerics;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>从随程序发布的 Markdown 文档显示入门帮助。</summary>
internal sealed partial class ImGuiEditorWindow
{
    private readonly Dictionary<string, (nint Id, int Width, int Height)> _guideImages = new();
    private readonly Dictionary<string, nint> _guideThumbnails = new();
    private string? _guidePreviewImage;
    private string _guidePreviewCaption = "";
    private float _guidePreviewZoom = 1F;
    private bool _guidePreviewFit;
    private bool _guidePreviewResetSize;
    private bool _showGettingStarted;
    private int _guidePage;
    private bool _guideReference;
    private string? _guideAnchor;
    private bool _guideScrollTop;
    private static readonly string[] GuidePageNames = ["快速入门", "视角与绘制", "对象与图层", "资产与渲染", "动画", "脚本生成器", "插件开发", "MagicaVoxel 迁移", "撤销与重做"];
    private static readonly string[] GuideFiles = ["快速入门.md", "视角与绘制.md", "对象与图层.md", "资产与渲染.md", "动画.md", "脚本生成器.md", "插件开发.md", "MagicaVoxel迁移.md", "撤销与重做.md"];

    private void OpenGuide(int page)
    {
        _guidePage = page == 2 ? 5 : 0;
        _showGettingStarted = true;
    }

    private void DrawGettingStarted()
    {
        if (!_showGettingStarted) return;
        ImGui.SetNextWindowSize(new Vector2(820F, 680F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480F, 360F), new Vector2(1800F, 1400F));
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        var open = true;
        if (ImGui.Begin("入门指南##MVoxelGuide", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            var sidebarWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * .24F, ImGui.GetFontSize() * 10F, ImGui.GetFontSize() * 16F);
            ImGui.BeginChild("##指南目录", new Vector2(sidebarWidth, 0F), ImGuiChildFlags.Borders);
            ImGui.TextDisabled("入门文档");
            ImGui.Spacing();
            for (var index = 0; index < GuidePageNames.Length; index++)
            {
                if (index == 5) ImGui.Spacing();
                if (ImGui.Selectable(GuidePageNames[index], _guidePage == index,
                    ImGuiSelectableFlags.None, new Vector2(0F, ImGui.GetFrameHeight() * 1.25F)))
                { _guidePage = index; _guideReference = false; _guideAnchor = null; _guideScrollTop = true; }
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##Markdown帮助", Vector2.Zero, ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
            var path = Path.Combine(AppContext.BaseDirectory, "Editor", "Guide", "Docs", _guideReference ? "参考资料" : "", GuideFiles[_guidePage]);
            try { if (_guideScrollTop) { ImGui.SetScrollY(0); _guideScrollTop = false; } DrawGuideMarkdown(File.ReadAllText(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { ImGui.TextWrapped($"无法读取帮助文档：{path}\n{exception.Message}"); }
            ImGui.EndChild();
        }
        ImGui.End();
        _showGettingStarted = open;
        DrawGuideImagePreview();
    }

    private void DrawGuideMarkdown(string markdown)
    {
        foreach (var raw in markdown.Replace("\r", "").Replace("→", " > ").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { ImGui.Spacing(); continue; }
            if (line.StartsWith("![") && line.Contains("](") && line.EndsWith(")"))
            {
                var split = line.IndexOf("](", StringComparison.Ordinal);
                DrawGuideImage(line[(split + 2)..^1], line[2..split]);
                continue;
            }
            if (line.StartsWith("[") && line.Contains("](") && line.EndsWith(")"))
            {
                var split = line.IndexOf("](", StringComparison.Ordinal);
                var label = line[1..split];
                var target = line[(split + 2)..^1].Split('#', 2);
                var reference = target[0].StartsWith("参考资料/", StringComparison.Ordinal);
                var file = reference ? target[0]["参考资料/".Length..] : target[0];
                var page = Array.IndexOf(GuideFiles, file);
                if (page >= 0)
                {
                    ImGui.PushID(line);
                    if (ImGui.SmallButton(label))
                    { _guidePage = page; _guideReference = reference; _guideAnchor = target.Length > 1 ? target[1] : null; _guideScrollTop = true; }
                    ImGui.PopID();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("跳转到 " + GuidePageNames[page] + (target.Length > 1 ? " / " + target[1] : ""));
                }
                else ImGui.TextWrapped(line);
                continue;
            }
            if ((line.StartsWith("# ") || line.StartsWith("## ")) && _guideAnchor == line[(line.StartsWith("## ") ? 3 : 2)..])
            { ImGui.SetScrollHereY(0F); _guideAnchor = null; }
            if (line.StartsWith("# "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ToColor(EditorPalette.Text), line[2..]);
                ImGui.Spacing();
            }
            else if (line.StartsWith("## "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ToColor(EditorPalette.Accent), line[3..]);
                ImGui.Spacing();
            }
            else if (line.StartsWith("- "))
            { ImGui.Bullet(); ImGui.SameLine(); ImGui.TextWrapped(line[2..].Replace("`", "")); }
            else if (line.StartsWith("> "))
            { ImGui.TextColored(ToColor(EditorPalette.Accent), "提示"); ImGui.SameLine(); ImGui.TextWrapped(line[2..]); }
            else ImGui.TextWrapped(line.Replace("`", ""));
        }
    }
    private void DrawGuideImage(string name, string caption)
    {
        if (!name.StartsWith("../Images/", StringComparison.Ordinal) || _imguiRenderer is null) return;
        name = name["../Images/".Length..];
        if (Path.GetFileName(name) != name) return;
        if (!_guideImages.TryGetValue(name, out var image))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Editor", "Guide", "Images", name);
            if (!AppBranding.TryLoadPngPixels(path, out var pixels, out var width, out var height))
            { ImGui.TextWrapped("无法加载图片：" + caption); return; }
            image = (_imguiRenderer.RegisterTexture(pixels, width, height), width, height);
            _guideImages[name] = image;
        }
        var widthOnScreen = Math.Min(ImGui.GetContentRegionAvail().X, image.Width);
        var size = new Vector2(widthOnScreen, widthOnScreen * image.Height / image.Width);
        var origin = ImGui.GetCursorScreenPos();
        // Pre-filter screenshots before the nearest-neighbor ImGui sampler downsizes them.
        if (!_guideThumbnails.TryGetValue(name, out var thumbnail))
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Editor", "Guide", "Images", name);
            if (AppBranding.TryLoadPngPixels(source, out var rgba, out var sw, out var sh))
            {
                var tw = Math.Min(sw, Math.Max(1, (int)widthOnScreen));
                var th = Math.Max(1, sh * tw / sw);
                var filtered = new byte[tw * th * 4];
                for (var y = 0; y < th; y++)
                for (var x = 0; x < tw; x++)
                {
                    var x0 = x * sw / tw; var x1 = (x + 1) * sw / tw;
                    var y0 = y * sh / th; var y1 = (y + 1) * sh / th;
                    for (var c = 0; c < 4; c++)
                    {
                        var sum = 0;
                        for (var sy = y0; sy < y1; sy++)
                        for (var sx = x0; sx < x1; sx++) sum += rgba[(sy * sw + sx) * 4 + c];
                        filtered[(y * tw + x) * 4 + c] = (byte)(sum / ((x1 - x0) * (y1 - y0)));
                    }
                }
                thumbnail = _imguiRenderer.RegisterTexture(filtered, tw, th);
                _guideThumbnails[name] = thumbnail;
            }
        }
        ImGui.Image(thumbnail != 0 ? thumbnail : image.Id, size);
        if (ImGui.IsItemClicked())
        { _guidePreviewImage = name; _guidePreviewCaption = caption; _guidePreviewZoom = 1F; _guidePreviewFit = true; _guidePreviewResetSize = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("点击放大；预览支持原尺寸、缩放和滚动查看");
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(new Vector4(1F, .2F, .1F, 1F));
        void Mark(float x, float y, float w, float h)
        {
            draw.AddRect(origin + size * new Vector2(x, y), origin + size * new Vector2(x + w, y + h), color, 0F, ImDrawFlags.None, 3F);
        }
        if (name == "workspace-raw.png")
        { Mark(.005F, .06F, .205F, .65F); Mark(.215F, .06F, .53F, .65F); Mark(.75F, .06F, .24F, .65F); Mark(.005F, .72F, .985F, .19F); }
        else if (name == "model-raw.png")
        { Mark(.023F, .17F, .18F, .025F); Mark(.76F, .345F, .23F, .035F); }
        ImGui.TextWrapped(caption);
    }

    private void DrawGuideImagePreview()
    {
        if (_guidePreviewImage is not { } name || !_guideImages.TryGetValue(name, out var image)) return;
        var open = true;
        if (_guidePreviewResetSize)
        {
            var screen = ImGui.GetIO().DisplaySize;
            var font = ImGui.GetFontSize();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(screen.X * .55F, font * 48F),
                Math.Min(screen.Y * .60F, font * 36F)), ImGuiCond.Always);
            _guidePreviewResetSize = false;
        }
        if (ImGui.Begin("教程图片预览##GuideImage", ref open, ImGuiWindowFlags.NoDocking))
        {
            if (ImGui.Button("原尺寸 100%")) { _guidePreviewZoom = 1F; _guidePreviewFit = false; }
            ImGui.SameLine();
            if (ImGui.Button("适应窗口")) _guidePreviewFit = true;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12F);
            if (ImGui.SliderFloat("缩放", ref _guidePreviewZoom, .25F, 4F, "%.2fx")) _guidePreviewFit = false;
            ImGui.TextWrapped(_guidePreviewCaption);
            ImGui.BeginChild("##GuideImageScroll", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
            var available = ImGui.GetContentRegionAvail();
            var zoom = _guidePreviewFit ? Math.Min(available.X / image.Width, available.Y / image.Height) : _guidePreviewZoom;
            ImGui.Image(image.Id, new Vector2(image.Width, image.Height) * Math.Max(.05F, zoom));
            ImGui.EndChild();
        }
        ImGui.End();
        if (!open) _guidePreviewImage = null;
    }

    private static void DrawGuideDiagram(int page)
    {
        if (page == 0) { DrawGuideWorkspace(); return; }
        string[][] steps = [[], ["选择模型", "绘制模式", "拖动绘制"], ["层级选对象", "检查器改属性", "图层控制显示"], ["选择资产", "调整并保存", "渲染并导出"], ["添加动画组件", "保存片段", "录制并播放"], ["选择目标", "选择脚本和参数", "运行生成"], ["安装插件包", "项目中启用", "运行命令"], ["选择导入入口", "检查模型材质", "保存项目"], ["执行编辑", "Ctrl+Z 撤销", "Ctrl+Y 重做"]];
        ImGui.TextColored(ToColor(EditorPalette.Accent), "操作路线");
        for (var i = 0; i < steps[page].Length; i++)
        {
            var label = $"{i + 1}. {steps[page][i]}";
            var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight() * 1.25F);
            var start = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(start, start + size, ImGui.GetColorU32(ImGuiCol.FrameBg), 4F);
            draw.AddRect(start, start + size, ImGui.GetColorU32(ImGuiCol.Border), 4F);
            draw.AddText(start + new Vector2(ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(ImGuiCol.Text), label);
            ImGui.Dummy(size);
            if (i < steps[page].Length - 1) ImGui.TextColored(ToColor(EditorPalette.Accent), "    |");
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawGuideWorkspace()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "工作区一览");
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeightWithSpacing() * 7F;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var border = ImGui.GetColorU32(ImGuiCol.Border);
        var fill = ImGui.GetColorU32(ImGuiCol.FrameBg);
        void Box(float x, float y, float w, float h, string label)
        {
            var a = origin + new Vector2(width * x, height * y);
            var b = origin + new Vector2(width * (x + w), height * (y + h));
            draw.AddRectFilled(a, b, fill, 3F);
            draw.AddRect(a, b, border, 3F);
            draw.AddText(a + new Vector2(6F, 5F), ImGui.GetColorU32(ImGuiCol.Text), label);
        }
        Box(0F, 0F, .22F, .72F, "1 层级");
        Box(.23F, 0F, .49F, .72F, "2 场景 / 渲染");
        Box(.73F, 0F, .27F, .72F, "3 检查器");
        Box(0F, .76F, 1F, .24F, "4 项目资产");
        ImGui.Dummy(new Vector2(width, height));
        ImGui.TextWrapped("1 选择对象   2 绘制与预览   3 调整属性和颜色   4 管理资产");
        ImGui.Spacing();
    }

}
