using ImGuiNET;
using MVoxel.Editor.Settings;
using MVoxel.Project;
using MVoxel.Rendering;
using MVoxel.Voxels;
using System.Text.Json;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private EditorSettingsDocument _editorSettings = EditorSettingsDocument.CreateDefault();
    private ProjectSettingsDocument _projectSettings = ProjectSettingsDocument.CreateDefault();
    private bool _useHardwareRayTracing;
    private bool _showSettingsWindow;
    private int _settingsScope;
    private int _editorSettingsCategory;
    private int _projectSettingsCategory;
    private int _selectedShortcutScheme;
    private string _newShortcutSchemeName = string.Empty;
    private string _renameShortcutScheme = string.Empty;
    private string _selectedProjectPalettePath = EditorPalettes.ProjectPaletteRelativePath;
    private string _newProjectPaletteName = "新调色板";

    /// <summary>
    /// 获取当前场景实际使用的画面配置
    /// </summary>
    /// <returns>项目配置优先，未打开项目时返回编辑器默认配置</returns>
    private RenderSettings CurrentSceneRenderSettings =>
        _project is null ? _editorSettings.SceneRenderSettings : _projectSettings.SceneRenderSettings;

    private void LoadEditorSettings()
    {
        _editorSettings = SettingsStore.LoadEditor();
        ApplyEditorSettingsToUi();
        _selectedShortcutScheme = Math.Clamp(
            _editorSettings.ShortcutSchemes.FindIndex(scheme => string.Equals(
                scheme.Name, _editorSettings.ActiveShortcutScheme, StringComparison.OrdinalIgnoreCase)),
            0, Math.Max(0, _editorSettings.ShortcutSchemes.Count - 1));
    }

    private void SaveEditorSettings()
    {
        try
        {
            SettingsStore.SaveEditor(_editorSettings);
        }
        catch (IOException)
        {
            // 设置写入失败不阻断正在编辑的场景，启动时使用默认值
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LoadProjectSettings(ProjectHandle project)
    {
        _projectSettings = SettingsStore.LoadProject(project);
        _selectedProjectPalettePath = EditorPalettes.NormalizeProjectPalettePath(_projectSettings.ActivePalettePath);
        ApplyProjectSettingsToUi();
    }

    private void ApplyProjectSettingsToUi()
    {
        _showGrid = _projectSettings.ShowGrid;
        _showVoxelOutlines = _projectSettings.ShowVoxelOutlines;
        _renderSettings = _projectSettings.RenderSettings;
        _useHardwareRayTracing = _projectSettings.RenderSettings.UseHardwareRayTracing;
        _sceneMoveSpeed = _projectSettings.SceneMoveSpeed;
        _sceneNearClip = _projectSettings.SceneNearClip;
        _sceneFarClip = _projectSettings.SceneFarClip;
        _sceneFieldOfView = _projectSettings.SceneFieldOfView;
        SyncSettingsToggles();
    }

    /// <summary>
    /// 将编辑器级场景设置应用到未打开项目时的运行状态
    /// </summary>
    private void ApplyEditorSettingsToUi()
    {
        _showGrid = _editorSettings.ShowSceneGrid;
        _showVoxelOutlines = _editorSettings.ShowVoxelOutlines;
        _sceneMoveSpeed = _editorSettings.SceneMoveSpeed;
        _sceneNearClip = _editorSettings.SceneNearClip;
        _sceneFarClip = _editorSettings.SceneFarClip;
        _sceneFieldOfView = _editorSettings.SceneFieldOfView;
        _renderSettings = _editorSettings.RenderViewSettings;
    }

    private void CaptureProjectSettingsFromUi()
    {
        _projectSettings.ShowGrid = _showGrid;
        _projectSettings.ShowVoxelOutlines = _showVoxelOutlines;
        _projectSettings.SceneMoveSpeed = _sceneMoveSpeed;
        _projectSettings.SceneNearClip = _sceneNearClip;
        _projectSettings.SceneFarClip = _sceneFarClip;
        _projectSettings.SceneFieldOfView = _sceneFieldOfView;
        _projectSettings.RenderSettings = _renderSettings;
        _projectSettings.RenderSettings.UseHardwareRayTracing = _useHardwareRayTracing;
        _projectSettings.Normalize();
    }

    private void SaveProjectSettings()
    {
        if (_project is null) return;
        CaptureProjectSettingsFromUi();
        try
        {
            SettingsStore.SaveProject(_project, _projectSettings);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private IReadOnlyList<string> GetProjectRenderPresetNames()
    {
        var names = new List<string> { "默认" };
        if (_project is null)
            return names;

        try
        {
            var directory = Path.Combine(_project.AssetsPath, "RenderPresets");
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory)
                             .Where(path => string.Equals(Path.GetExtension(path), ".mvrenderpreset", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(Path.GetExtension(path), ".mvrender", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
        }
        catch (IOException)
        {
            // 资产目录暂时不可读时仍保留“默认”，避免设置面板失效。
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!names.Contains(_projectSettings.DefaultRenderPreset, StringComparer.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_projectSettings.DefaultRenderPreset))
            names.Add(_projectSettings.DefaultRenderPreset);

        return names;
    }

    private void ApplyProjectRenderPreset(string name)
    {
        if (_project is null)
            return;

        try
        {
            RenderSettings settings;
            if (string.Equals(name, "默认", StringComparison.OrdinalIgnoreCase))
            {
                settings = new RenderSettings();
            }
            else
            {
                var directory = Path.Combine(_project.AssetsPath, "RenderPresets");
                var path = Directory.EnumerateFiles(directory)
                    .Where(candidate => string.Equals(Path.GetExtension(candidate), ".mvrenderpreset", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(Path.GetExtension(candidate), ".mvrender", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(candidate => string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.OrdinalIgnoreCase));
                if (path is null)
                {
                    SetStatus($"渲染预设不存在：{name}");
                    return;
                }

                settings = RenderSettings.FromJson(File.ReadAllText(path));
            }

            _projectSettings.DefaultRenderPreset = name;
            _projectSettings.RenderSettings = settings;
            _renderSettings = settings;
            SyncSettingsToggles();
            _pipeline?.ResetAccumulation();
            SaveProjectSettings();
            SetStatus($"已应用渲染预设：{name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SetStatus($"渲染预设加载失败：{exception.Message}");
        }
    }

    private void OpenSettings(int scope, int category = 0)
    {
        _settingsScope = scope;
        if (scope == 0) _editorSettingsCategory = category;
        else _projectSettingsCategory = category;
        _showSettingsWindow = true;
    }

    private void DrawSettingsWindow()
    {
        if (!_showSettingsWindow) return;

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(1080F, 760F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(820F, 560F), new Vector2(1800F, 1400F));
        if (ImGui.Begin("设置##MVoxelSettings", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9F, 7F));
            if (ImGui.BeginChild("##SettingsHeader", new Vector2(0F, 78F), ImGuiChildFlags.Borders))
            {
                ImGui.TextColored(ToColor(EditorPalette.Accent), "配置中心");
                ImGui.SameLine();
                ImGui.TextDisabled(_project is null ? "编辑器级设置" : "编辑器级设置 · 当前项目级设置");
                ImGui.SameLine(Math.Max(250F, ImGui.GetWindowWidth() - 330F));
                if (ImGui.Button("保存编辑器设置", new Vector2(138F, 30F)))
                {
                    SaveEditorSettings();
                    SetStatus("编辑器设置已保存");
                }

                if (_project is not null)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("保存项目设置", new Vector2(128F, 30F)))
                    {
                        SaveProjectSettings();
                        SetStatus("项目设置已保存");
                    }
                }

                ImGui.TextDisabled("按职责进入分类；画面、输入和布局修改会立即作用于当前编辑器。");
            }

            ImGui.EndChild();
            ImGui.Spacing();
            if (ImGui.BeginTabBar("##SettingsScopes"))
            {
                if (ImGui.BeginTabItem("编辑器设置"))
                {
                    _settingsScope = 0;
                    DrawEditorSettingsSurface();
                    ImGui.EndTabItem();
                }

                var projectEnabled = _project is not null;
                if (projectEnabled && ImGui.BeginTabItem("项目设置"))
                {
                    _settingsScope = 1;
                    DrawProjectSettingsSurface();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.PopStyleVar();
        }

        ImGui.End();
        if (!open)
        {
            _showSettingsWindow = false;
            SaveEditorSettings();
            SaveProjectSettings();
        }
    }

    private void DrawEditorSettingsSurface()
    {
        var categories = new[] { "常规", "布局", "输入", "画面", "外观", "性能" };
        DrawSettingsSidebar(categories, ref _editorSettingsCategory, "##编辑器设置分类", () =>
        {
            switch (_editorSettingsCategory)
            {
                case 0: DrawEditorGeneralSettings(); break;
                case 1: DrawEditorLayoutSettings(); break;
                case 2: DrawEditorInputSettings(); break;
                case 3: DrawEditorVisualSettings(); break;
                case 4: DrawEditorAppearanceSettings(); break;
                default: DrawEditorPerformanceSettings(); break;
            }
        });
        _editorSettings.Normalize();
        if (_project is null) ApplyEditorSettingsToUi();
    }

    private void DrawProjectSettingsSurface()
    {
        var categories = new[] { "项目", "画面", "资产", "调色板" };
        DrawSettingsSidebar(categories, ref _projectSettingsCategory, "##项目设置分类", () =>
        {
            switch (_projectSettingsCategory)
            {
                case 0: DrawProjectGeneralSettings(); break;
                case 1: DrawProjectVisualSettings(); break;
                case 2: DrawProjectAssetSettings(); break;
                default: DrawProjectPaletteSettings(); break;
            }
        });
        _projectSettings.ShowGrid = _showGrid;
        _projectSettings.ShowVoxelOutlines = _showVoxelOutlines;
        _projectSettings.SceneMoveSpeed = _sceneMoveSpeed;
        _projectSettings.SceneNearClip = _sceneNearClip;
        _projectSettings.SceneFarClip = _sceneFarClip;
        _projectSettings.SceneFieldOfView = _sceneFieldOfView;
        _projectSettings.Normalize();
        ApplyProjectSettingsToUi();
    }

    private static void DrawSettingsSidebar(IReadOnlyList<string> categories, ref int selected,
        string id, Action drawContent)
    {
        var contentHeight = Math.Max(1F, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild(id, new Vector2(202F, contentHeight), ImGuiChildFlags.Borders);
        ImGui.TextColored(ToColor(EditorPalette.Accent), "设置分类");
        ImGui.TextDisabled("按职责组织，不混放运行参数");
        ImGui.Separator();
        for (var index = 0; index < categories.Count; index++)
        {
            var isSelected = selected == index;
            ImGui.PushID(index);
            if (isSelected)
            {
                ImGui.PushStyleColor(ImGuiCol.Header, ToColor(EditorPalette.AccentSoft));
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ToColor(EditorPalette.AccentSoft));
            }

            if (ImGui.Selectable(categories[index], isSelected, ImGuiSelectableFlags.None,
                    new Vector2(ImGui.GetContentRegionAvail().X, 34F)))
                selected = index;

            if (isSelected)
            {
                ImGui.PopStyleColor(2);
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild(id + "内容", new Vector2(0F, contentHeight), ImGuiChildFlags.Borders);
        drawContent();
        ImGui.EndChild();
    }

    private static void DrawSettingsPageHeader(string title, string description)
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), title);
        _ = description;
        ImGui.Separator();
    }

    private static void DrawSettingsSectionHeader(string title, string? description = null)
    {
        ImGui.Spacing();
        ImGui.TextColored(ToColor(EditorPalette.Accent), title);
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {description}");
        }

        ImGui.Separator();
    }

    private void DrawEditorGeneralSettings()
    {
        DrawSettingsPageHeader("常规", "决定新项目、撤回和启动时的默认行为");
        if (ImGui.BeginTabBar("##EditorGeneralTabs"))
        {
            if (ImGui.BeginTabItem("基本"))
            {
                ImGui.SetNextItemWidth(-1F);
                ImGui.InputText("项目根目录", ref _editorSettings.ProjectRootPath, 2048);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("新建项目的默认保存位置");

                if (ImGui.Button("恢复默认项目根目录"))
                {
                    _editorSettings.ProjectRootPath = EditorSettingsDocument.DefaultProjectRootPath;
                }
                ImGui.SameLine();
                ImGui.TextDisabled(EditorSettingsDocument.DefaultProjectRootPath);
                ImGui.Checkbox("启用显卡诊断输出", ref _editorSettings.EnableGpuDiagnostics);
                ImGui.Checkbox("新项目优先使用硬件 RT", ref _editorSettings.UseHardwareRayTracing);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("只作为新项目渲染设置的初始值，默认关闭");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("撤回"))
            {
                var changed = false;
                SetSettingsSliderWidth("持续撤回间隔（毫秒）");
                changed |= ImGui.SliderInt("开始持续撤回延迟（毫秒）", ref _editorSettings.UndoRepeatDelayMs, 100, 2000);
                SetSettingsSliderWidth("开始持续撤回延迟（毫秒）");
                changed |= ImGui.SliderInt("持续撤回间隔（毫秒）", ref _editorSettings.UndoRepeatIntervalMs, 20, 500);
                _editorSettings.Normalize();
                ImGui.TextDisabled($"当前速度：{1000F / _editorSettings.UndoRepeatIntervalMs:0.0} 次/秒");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+Z 首次按下立即撤回，达到延迟后按间隔持续撤回");
                if (changed) SaveEditorSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorLayoutSettings()
    {
        DrawSettingsPageHeader("布局", "工作区预设只改变面板摆布，不会修改项目数据");
        if (ImGui.BeginTabBar("##EditorLayoutTabs"))
        {
            if (ImGui.BeginTabItem("面板"))
            {
                ImGui.TextDisabled("选择一个适合当前任务的工作区，之后仍可手动拖拽面板调整。");
                var buttonWidth = Math.Max(120F, (ImGui.GetContentRegionAvail().X - 18F) / 3F);
                if (ImGui.Button("应用默认布局", new Vector2(buttonWidth, 34F))) SelectLayout(LayoutPreset.Default);
                ImGui.SameLine();
                if (ImGui.Button("应用建模布局", new Vector2(buttonWidth, 34F))) SelectLayout(LayoutPreset.Modeling);
                ImGui.SameLine();
                if (ImGui.Button("应用渲染布局", new Vector2(buttonWidth, 34F))) SelectLayout(LayoutPreset.Rendering);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("停靠"))
            {
                if (ImGui.Button(_editPanelLayout ? "完成面板布局编辑" : "编辑面板布局"))
                    _editPanelLayout = !_editPanelLayout;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("编辑停靠位置、面板分割线和浮动状态");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorVisualSettings()
    {
        DrawSettingsPageHeader("画面默认值", "未打开项目时使用；新项目会继承其中的场景与渲染初始值");
        if (ImGui.BeginTabBar("##EditorVisualTabs"))
        {
            if (ImGui.BeginTabItem("场景画面"))
            {
                DrawSettingsSectionHeader("场景辅助显示", "网格与体素轮廓");
                ImGui.Checkbox("显示场景网格", ref _editorSettings.ShowSceneGrid);
                ImGui.Checkbox("显示体素轮廓", ref _editorSettings.ShowVoxelOutlines);
                DrawSettingsSectionHeader("场景 LOD", "远距离显示策略");
                DrawLodSection(_editorSettings.RenderViewSettings);
                DrawSettingsSectionHeader("场景相机", "视口导航和裁剪范围");
                SliderFloat("移动速度", ref _editorSettings.SceneMoveSpeed, 0.1F, 256F, "%.1f 格/秒");
                SliderFloat("近处裁剪", ref _editorSettings.SceneNearClip, 0.001F, 1000F, "%.3f 格");
                SliderFloat("远处裁剪", ref _editorSettings.SceneFarClip, 1F, 100000000F, "%.0f 格");
                SliderFloat("视场角 FOV", ref _editorSettings.SceneFieldOfView, 20F, 120F, "%.0f°");
                DrawSettingsSectionHeader("场景背景", "未打开项目时的预览底色");
                DrawColorSlider("场景背景色", ref _editorSettings.SceneRenderSettings.SceneBackgroundColor);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("未打开项目时使用此颜色，新项目会继承该颜色");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染画面"))
            {
                DrawEditorRenderConfiguration(_editorSettings.RenderViewSettings, "渲染窗口");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染导出画面"))
            {
                DrawEditorRenderConfiguration(_editorSettings.ExportRenderSettings, "导出画面");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorRenderConfiguration(RenderSettings settings, string context)
    {
        DrawFullRenderConfiguration(settings, context);
    }

    private void DrawFullRenderConfiguration(RenderSettings settings, string context)
    {
        ImGui.PushID(context);

        DrawSettingsPageHeader(context, settings.Backend == RenderBackend.Gpu
            ? "显卡计算 · 修改后实时生效"
            : "CPU 计算 · 适合调试和无显卡环境");
        ImGui.TextDisabled($"路径追踪 {(settings.PathTracingEnabled ? "开启" : "关闭")}  ·  地面 {(settings.ShowGround ? "开启" : "关闭")}  ·  景深 {(settings.DepthOfField ? "开启" : "关闭")}");

        DrawRenderSettingsAccordion("backend", "后端", settings.Backend == RenderBackend.Gpu ? "显卡计算" : "CPU 调试", () =>
        {
                DrawSettingsSectionHeader("计算后端", "决定实时预览使用的计算路径");
                var backend = (int)settings.Backend;
                if (ImGui.Combo("计算后端", ref backend, "显卡计算\0CPU\0", 2))
                    settings.Backend = (RenderBackend)Math.Clamp(backend, 0, 1);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(settings.Backend == RenderBackend.Gpu
                    ? "优先使用显卡计算"
                    : "使用 CPU 计算，适合调试或没有可用显卡时使用");
                var hardwareRayTracing = settings.UseHardwareRayTracing;
                if (settings.Backend == RenderBackend.Cpu) hardwareRayTracing = false;
                ImGui.BeginDisabled(settings.Backend == RenderBackend.Cpu);
                ImGui.Checkbox("启用硬件 RT", ref hardwareRayTracing);
                ImGui.EndDisabled();
                settings.UseHardwareRayTracing = hardwareRayTracing;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("默认关闭，需要设备支持，不可用时回退显卡计算路径");
        }, true);

        DrawRenderSettingsAccordion("lighting", "光照", "太阳、天空、AO 与雾", () =>
        {
                DrawSettingsSectionHeader("太阳与阴影", "主光源、阴影柔和度和采样质量");
                ImGui.Checkbox("太阳平行光", ref settings.SunEnabled);
                SliderFloat("太阳方位角", ref settings.SunAzimuthDegrees, 0F, 360F, "%.0f°");
                SliderFloat("太阳仰角", ref settings.SunElevationDegrees, 2F, 90F, "%.0f°");
                SliderFloat("太阳强度", ref settings.SunIntensity, 0F, 8F, "%.2f");
                DrawColorSlider("太阳颜色", ref settings.SunColor);
                SliderFloat("阴影柔和度", ref settings.ShadowSoftness, 0F, 0.5F, "%.3f");
                SliderInt("阴影采样数", ref settings.ShadowSamples, 1, 8);
                DrawSettingsSectionHeader("环境 · 天空与地面", "背景、地面和环境补光");
                ImGui.Checkbox("显示地面平面", ref settings.ShowGround);
                DrawColorSlider("地面颜色", ref settings.GroundColor);
                SliderFloat("地面淡出距离（0=纯色无限地面）", ref settings.GroundFade, 0F, 1024F, "%.0f 格");
                DrawColorSlider("天空顶部颜色", ref settings.SkyTop);
                DrawColorSlider("天空地平线颜色", ref settings.SkyHorizon);
                SliderFloat("天空环境光", ref settings.SkyAmbient, 0F, 2F, "%.2f");
                SliderFloat("暖色环境补光", ref settings.AmbientTint, 0F, 1F, "%.2f");
                DrawSettingsSectionHeader("接触阴影与雾", "近处细节与远处层次");
                ImGui.Checkbox("环境光遮蔽 AO", ref settings.AmbientOcclusionEnabled);
                SliderFloat("AO 强度", ref settings.AoStrength, 0F, 1F, "%.2f");
                SliderFloat("AO 对比度", ref settings.AoPower, 0.2F, 4F, "%.2f");
                DrawColorSlider("雾颜色", ref settings.FogColor);
                SliderFloat("雾浓度", ref settings.FogDensity, 0F, 0.02F, "%.4f");
                SliderFloat("近处淡入距离", ref settings.NearFade, 0F, 256F, "%.0f 格");
        });

        DrawRenderSettingsAccordion("color", "色彩输出", "调色、曝光、色调映射与 Bloom", () =>
        {
                DrawSettingsSectionHeader("颜色与输出", "调色板、色调映射和输出观感");
                ImGui.Checkbox("调色板着色", ref settings.PaletteShading);
                SliderFloat("饱和度", ref settings.Saturation, 0F, 2.5F, "%.2f");
                SliderFloat("色相偏移", ref settings.HueShiftDegrees, 0F, 360F, "%.0f°");
                SliderFloat("颜色对比度", ref settings.ColorContrast, 0.2F, 2.5F, "%.2f");
                SliderFloat("颜色明度", ref settings.Brightness, 0.1F, 3F, "%.2f");
                ImGui.Checkbox("色相环动画", ref settings.HueCycle);
                SliderFloat("色相环速度", ref settings.HueCycleSpeed, 0F, 90F, "%.0f°/秒");
                var tone = (int)settings.Tone;
                if (ImGui.Combo("色调曲线", ref tone, "线性截断\0Reinhard\0ACES（Narkowicz）\0Filmic（HV）\0", 4))
                    settings.Tone = (ToneCurve)Math.Clamp(tone, 0, 3);
                SliderFloat("曝光", ref settings.Exposure, -6F, 6F, "%.2f EV");
                SliderFloat("Gamma", ref settings.Gamma, 1F, 3.5F, "%.2f");
                SliderFloat("白平衡色温", ref settings.WhiteBalance, 2000F, 12000F, "%.0f K");
                SliderFloat("暗角", ref settings.Vignette, 0F, 1F, "%.2f");
                ImGui.Checkbox("输出抖动", ref settings.Dither);
                DrawSettingsSectionHeader("辉光 Bloom", "高亮体素的后期扩散");
                ImGui.Checkbox("Bloom 辉光", ref settings.BloomEnabled);
                SliderFloat("辉光强度", ref settings.BloomIntensity, 0F, 2F, "%.2f");
                SliderFloat("辉光阈值", ref settings.BloomThreshold, 0F, 8F, "%.2f");
                SliderFloat("辉光半径", ref settings.BloomRadius, 1F, 64F, "%.0f px");
        });

        DrawRenderSettingsAccordion("path", "路径追踪", settings.PathTracingEnabled ? "渐进模式已开启" : "实时模式（推荐默认）", () =>
        {
                DrawSettingsSectionHeader("路径追踪质量", "只在启用路径追踪时产生明显影响");
                ImGui.Checkbox("启用路径追踪", ref settings.PathTracingEnabled);
                SliderInt("每帧样本数", ref settings.PathSamplesPerFrame, 1, 8);
                SliderInt("累积样本上限", ref settings.PathMaxAccumulatedSamples, 64, 8192);
                SliderFloat("内部渲染分辨率", ref settings.ResolutionScale, 0.25F, 1F, "%.2fx");
                SliderInt("最大弹射次数", ref settings.MaxBounces, 1, 12);
                SliderFloat("俄罗斯轮盘阈值", ref settings.RussianRouletteThreshold, 0.0005F, 0.05F, "%.4f");
                SliderFloat("辐射度截断", ref settings.RadianceClamp, 1F, 200F, "%.1f");
                ImGui.Checkbox("快速降噪", ref settings.PathDenoising);
                SliderFloat("降噪强度", ref settings.PathDenoiseStrength, 0F, 1F, "%.2f");
        });

        DrawRenderSettingsAccordion("material", "全局材质", "GGX、金属度、透明与自发光", () =>
        {
                SliderFloat("材质粗糙度", ref settings.PathRoughness, 0.01F, 1F, "%.3f");
                SliderFloat("材质金属度", ref settings.PathMetalness, 0F, 1F, "%.2f");
                SliderFloat("介电反射率 F0", ref settings.PathSpecularF0, 0F, 0.2F, "%.3f");
                SliderFloat("半透明吸收浓度", ref settings.TransmissiveAbsorption, 0F, 6F, "%.2f");
                ImGui.Checkbox("自发光体素参与照明", ref settings.EmissiveVoxels);
                SliderFloat("自发光强度", ref settings.EmissiveIntensity, 0F, 40F, "%.1f");
                ImGui.Checkbox("直接光 MIS 采样", ref settings.DirectLightingMIS);
        });

        DrawRenderSettingsAccordion("camera", "相机", "视场角、景深与对焦", () =>
        {
                DrawSettingsSectionHeader("渲染相机", "仅影响渲染画面，不改变场景相机");
                SliderFloat("视场角 FOV", ref settings.CameraFovDegrees, 20F, 120F, "%.0f°");
                ImGui.Checkbox("景深", ref settings.DepthOfField);
                SliderFloat("光圈半径", ref settings.ApertureRadius, 0F, 0.5F, "%.3f");
                SliderFloat("对焦距离", ref settings.FocusDistance, 1F, 256F, "%.1f 格");
        });

        ImGui.PopID();
    }

    private void DrawEditorInputSettings()
    {
        DrawSettingsPageHeader("输入", "快捷键方案与鼠标操作彼此独立，修改后立即生效");
        if (ImGui.BeginTabBar("##EditorInputTabs"))
        {
            if (ImGui.BeginTabItem("快捷键方案"))
            {
                DrawShortcutSchemes();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("鼠标与视角"))
            {
                ImGui.Checkbox("MC 模式（交换左右键）", ref _editorSettings.McMode);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(_editorSettings.McMode
                    ? "Caps Lock 下左键破坏，右键放置；Shift 加速，中键平移，鼠标转向"
                    : "Caps Lock 下左键放置，右键破坏；Shift 加速，中键平移，鼠标转向");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawShortcutSchemes()
    {
        _editorSettings.Normalize();
        var schemes = _editorSettings.ShortcutSchemes;
        _selectedShortcutScheme = Math.Clamp(_selectedShortcutScheme, 0, schemes.Count - 1);
        var scheme = schemes[_selectedShortcutScheme];
        if (_renameShortcutScheme.Length == 0) _renameShortcutScheme = scheme.Name;

        ImGui.BeginChild("##快捷键方案列表", new Vector2(190F, 0F), ImGuiChildFlags.Borders);
        ImGui.TextDisabled("方案");
        ImGui.Separator();
        for (var index = 0; index < schemes.Count; index++)
        {
            var selected = index == _selectedShortcutScheme;
            if (ImGui.Selectable(schemes[index].Name, selected))
            {
                _selectedShortcutScheme = index;
                scheme = schemes[index];
                _renameShortcutScheme = scheme.Name;
            }
        }

        ImGui.Separator();
        ImGui.InputTextWithHint("##新方案名称", "新方案名称", ref _newShortcutSchemeName, 64);
        if (ImGui.Button("新增方案", new Vector2(-1F, 0F)))
        {
            var name = string.IsNullOrWhiteSpace(_newShortcutSchemeName)
                ? $"方案 {schemes.Count + 1}" : _newShortcutSchemeName.Trim();
            var unique = name;
            var suffix = 2;
            while (schemes.Any(item => string.Equals(item.Name, unique, StringComparison.OrdinalIgnoreCase)))
                unique = $"{name} {suffix++}";
            schemes.Add(scheme.Clone(unique));
            _selectedShortcutScheme = schemes.Count - 1;
            _newShortcutSchemeName = string.Empty;
            SaveEditorSettings();
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##快捷键方案内容", new Vector2(0F, 0F), ImGuiChildFlags.Borders);
        ImGui.TextColored(ToColor(EditorPalette.Accent), $"当前方案 · {scheme.Name}");
        ImGui.SameLine();
        ImGui.TextDisabled(string.Equals(_editorSettings.ActiveShortcutScheme, scheme.Name, StringComparison.OrdinalIgnoreCase)
            ? "正在使用"
            : "未启用");
        if (ImGui.Button("设为当前使用"))
        {
            _editorSettings.ActiveShortcutScheme = scheme.Name;
            SaveEditorSettings();
        }

        ImGui.SameLine();
        if (ImGui.Button("恢复默认绑定"))
        {
            foreach (var command in ShortcutCommands.All) scheme.Bindings[command.Id] = command.DefaultBinding;
            SaveEditorSettings();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(schemes.Count <= 1);
        if (ImGui.Button("删除方案"))
        {
            schemes.RemoveAt(_selectedShortcutScheme);
            _selectedShortcutScheme = Math.Clamp(_selectedShortcutScheme, 0, schemes.Count - 1);
            if (!schemes.Any(item => string.Equals(item.Name, _editorSettings.ActiveShortcutScheme, StringComparison.OrdinalIgnoreCase)))
                _editorSettings.ActiveShortcutScheme = schemes[_selectedShortcutScheme].Name;
            SaveEditorSettings();
            ImGui.EndDisabled();
            ImGui.EndChild();
            return;
        }
        ImGui.EndDisabled();

        ImGui.InputText("方案名称", ref _renameShortcutScheme, 64);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var newName = _renameShortcutScheme.Trim();
            if (newName.Length > 0 && !schemes.Any(item => !ReferenceEquals(item, scheme) &&
                    string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                var wasActive = string.Equals(_editorSettings.ActiveShortcutScheme, scheme.Name, StringComparison.OrdinalIgnoreCase);
                scheme.Name = newName;
                if (wasActive) _editorSettings.ActiveShortcutScheme = newName;
                SaveEditorSettings();
            }
        }

        DrawSettingsSectionHeader("按命令修改绑定", "使用 Ctrl/Shift/Alt + 按键格式，例如 Ctrl+Z");
        foreach (var command in ShortcutCommands.All)
        {
            scheme.Bindings.TryGetValue(command.Id, out var binding);
            binding ??= command.DefaultBinding;
            ImGui.PushID(command.Id);
            ImGui.Text(command.DisplayName);
            ImGui.SameLine(180F);
            ImGui.SetNextItemWidth(160F);
            if (ImGui.InputText("##绑定", ref binding, 32))
            {
                scheme.Bindings[command.Id] = binding.Trim();
                SaveEditorSettings();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(command.Category);
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void DrawEditorAppearanceSettings()
    {
        DrawSettingsPageHeader("外观", "控制编辑器的视觉层级，不会改变项目渲染结果");
        if (ImGui.BeginTabBar("##EditorAppearanceTabs"))
        {
            if (ImGui.BeginTabItem("字体"))
            {
                SliderFloat("界面缩放", ref _editorSettings.UiScale, 0.75F, 2F, "%.2fx");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("按 DPI 使用整数倍字体缩放，降低文字发虚");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("主题"))
            {
                var theme = _editorSettings.Theme;
                if (ImGui.BeginCombo("主题", theme))
                {
                    foreach (var item in EditorPalette.ThemeNames)
                    {
                        if (ImGui.Selectable(item, theme == item))
                        {
                            _editorSettings.Theme = item;
                            EditorPalette.ApplyTheme(item);
                            ApplyImGuiStyle();
                            SaveEditorSettings();
                        }
                    }

                    ImGui.EndCombo();
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip("主题切换立即生效并自动保存");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorPerformanceSettings()
    {
        DrawSettingsPageHeader("性能", "控制编辑器工作方式与诊断，不直接改变体素数据");
        DrawSettingsSectionHeader("导出队列", "预留并行度上限，当前 GPU 管线仍按单任务安全执行");
        SetSettingsSliderWidth("同时执行的导出数");
        if (ImGui.SliderInt("同时执行的导出数", ref _editorSettings.ExportParallelism, 1, 4))
        {
            _editorSettings.Normalize();
            SaveEditorSettings();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("当前 GPU 管线按单任务安全执行；该设置为未来多管线并行保留上限，不会阻塞编辑器输入。");
        if (ImGui.BeginTabBar("##EditorPerformanceTabs"))
        {
            if (ImGui.BeginTabItem("性能策略"))
            {
                ImGui.TextUnformatted("LOD 与稀疏数据");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("场景视图使用 LOD 和稀疏区块，渲染与导出保留完整数据");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("诊断"))
            {
                ImGui.TextUnformatted("显卡诊断");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("检查字体缺字、显卡帧尺寸和绘制顶点数");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectGeneralSettings()
    {
        DrawSettingsPageHeader("项目", "项目身份、默认画面预设与自动保存");
        if (ImGui.BeginTabBar("##ProjectGeneralTabs"))
        {
            if (ImGui.BeginTabItem("基本信息"))
            {
                ImGui.Text("项目名称");
                ImGui.SameLine(120F);
                ImGui.Text(_project?.Manifest.Name ?? "未打开项目");
                ImGui.Text("项目路径");
                ImGui.SameLine(120F);
                ImGui.TextWrapped(_project?.RootPath ?? string.Empty);
                ImGui.Text("体素单位");
                ImGui.SameLine(120F);
                ImGui.InputText("##体素单位", ref _projectSettings.VoxelUnit, 64);
                ImGui.Text("项目模板");
                ImGui.SameLine(120F);
                ImGui.TextDisabled(_projectSettings.TemplateId);
                ImGui.Text("默认渲染预设");
                ImGui.SameLine(120F);
                var preset = _projectSettings.DefaultRenderPreset;
                if (ImGui.BeginCombo("##默认渲染预设", preset))
                {
                    foreach (var option in GetProjectRenderPresetNames())
                    {
                        if (ImGui.Selectable(option, option == preset))
                            ApplyProjectRenderPreset(option);
                    }

                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("列表来自 Assets/RenderPresets；选择后会立即应用到当前项目并保存");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("自动保存"))
            {
                ImGui.Checkbox("启用自动保存", ref _projectSettings.AutoSave);
                SetSettingsSliderWidth("间隔（秒）");
                ImGui.SliderInt("间隔（秒）", ref _projectSettings.AutoSaveSeconds, 10, 3600);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("场景有未保存修改时，按此间隔自动保存");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectVisualSettings()
    {
        DrawSettingsPageHeader("项目画面", "项目级配置覆盖编辑器默认值，并随项目保存");
        if (ImGui.BeginTabBar("##ProjectVisualTabs"))
        {
            if (ImGui.BeginTabItem("场景画面"))
            {
                DrawSettingsSectionHeader("场景辅助显示", "网格与体素轮廓");
                ImGui.Checkbox("显示场景网格", ref _showGrid);
                ImGui.Checkbox("显示体素轮廓", ref _showVoxelOutlines);
                DrawSettingsSectionHeader("场景相机", "视口导航和裁剪范围");
                SliderFloat("移动速度", ref _sceneMoveSpeed, 0.1F, 256F, "%.1f 格/秒");
                SliderFloat("近处裁剪", ref _sceneNearClip, 0.001F, 1000F, "%.3f 格");
                SliderFloat("远处裁剪", ref _sceneFarClip, 1F, 100000000F, "%.0f 格");
                SliderFloat("视场角 FOV", ref _sceneFieldOfView, 20F, 120F, "%.0f°");
                DrawSettingsSectionHeader("场景背景", "项目打开后的预览底色");
                DrawColorSlider("场景背景色", ref _projectSettings.SceneRenderSettings.SceneBackgroundColor);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("打开项目后优先使用项目级背景色");
                DrawSettingsSectionHeader("场景 LOD", "远距离显示策略");
                DrawLodSection(_projectSettings.RenderSettings);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染画面"))
            {
                DrawFullRenderConfiguration(_projectSettings.RenderSettings, "渲染窗口");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染导出画面"))
            {
                DrawFullRenderConfiguration(_projectSettings.ExportRenderSettings, "导出画面");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// 图层管理界面：新增、重命名、上移下移与删除，显示每层占用对象数；
    /// 第 0 层为默认层不可删除，上限 32 层与相机遮罩位宽一致
    /// </summary>
    private void DrawLayerManager()
    {
        if (_sceneDocument is not { } document)
        {
            ImGui.TextDisabled("打开项目后即可管理当前场景的图层。");
            return;
        }

        ImGui.TextUnformatted("场景图层");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("对象在检查器的图层下拉中归属图层；相机组件按图层遮罩决定渲染哪些对象。");
        var usage = document.LayerUsageCounts();
        for (var layerIndex = 0; layerIndex < document.LayerNames.Count; layerIndex++)
        {
            ImGui.PushID($"LayerRow{layerIndex}");
            var name = document.LayerNames[layerIndex];
            ImGui.SetNextItemWidth(160F);
            if (ImGui.InputText("##名", ref name, 32) && name.Trim().Length > 0 &&
                document.RenameLayer(layerIndex, name))
            {
                MarkSceneDocumentDirty();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{usage[layerIndex]} 个对象");
            ImGui.SameLine();
            ImGui.BeginDisabled(layerIndex == 0);
            if (ImGui.SmallButton("↑") && document.MoveLayer(layerIndex, -1))
            {
                MarkSceneDocumentDirty();
            }

            ImGui.SameLine(0F, 4F);
            if (ImGui.SmallButton("↓") && document.MoveLayer(layerIndex, 1))
            {
                MarkSceneDocumentDirty();
            }

            ImGui.SameLine(0F, 4F);
            if (ImGui.SmallButton("删除") && document.RemoveLayer(layerIndex))
            {
                MarkSceneDocumentDirty();
            }

            ImGui.EndDisabled();
            if (layerIndex == 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("默认层不可删除；删除图层时该层对象回到默认层。");
            ImGui.PopID();
        }

        var newLayerName = _newLayerName;
        ImGui.SetNextItemWidth(160F);
        ImGui.InputText("##新图层名", ref _newLayerName, 32);
        ImGui.SameLine();
        if (ImGui.SmallButton("新增图层"))
        {
            if (document.AddLayer(_newLayerName) >= 0)
            {
                _newLayerName = string.Empty;
                MarkSceneDocumentDirty();
            }
            else
            {
                SetStatus("新增图层失败：名称为空、重复或已达 32 层上限");
            }
        }

        _ = newLayerName;
    }

    private string _newLayerName = string.Empty;

    private void DrawProjectAssetSettings()
    {
        DrawSettingsPageHeader("资产", "目录、导入格式和场景图层的职责说明");
        if (ImGui.BeginTabBar("##ProjectAssetTabs"))
        {
            if (ImGui.BeginTabItem("目录"))
            {
                ImGui.TextUnformatted("项目资源目录");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Assets 下的项目资源由目录结构统一管理，体素材质使用调色板基础色");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("导入"))
            {
                ImGui.TextUnformatted("导入与导出格式");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("支持导入导出 .vox、导入网格模型与 Minecraft 存档、读写 .palette");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("图层"))
            {
                DrawLayerManager();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// 项目级调色板管理：一个项目可以拥有多个调色板资产，项目设置只指定当前使用项。
    /// 颜色编辑、创建、切换和保存集中在这里，不再混入渲染参数页面。
    /// </summary>
    private void DrawProjectPaletteSettings()
    {
        DrawSettingsPageHeader("调色板", "管理项目内的多个调色板，并指定建模笔刷当前使用的调色板");
        if (_project is null)
        {
            ImGui.TextDisabled("打开项目后即可管理项目调色板。");
            return;
        }

        var paths = EditorPalettes.EnumerateProjectPalettePaths(_project);
        if (paths.Count == 0)
        {
            ImGui.TextDisabled("项目中还没有调色板资产。");
            ImGui.InputText("新调色板名称", ref _newProjectPaletteName, 96);
            if (ImGui.Button("创建默认调色板", new Vector2(170F, 30F)))
                CreateProjectPaletteAsset(VoxelPalette.Build());
            return;
        }

        var activePath = EditorPalettes.NormalizeProjectPalettePath(_projectSettings.ActivePalettePath);
        if (!paths.Contains(activePath, StringComparer.OrdinalIgnoreCase))
        {
            activePath = paths[0];
            _projectSettings.ActivePalettePath = activePath;
            _selectedProjectPalettePath = activePath;
            _ = TryLoadProjectPalette(activePath, true);
        }

        if (!paths.Contains(_selectedProjectPalettePath, StringComparer.OrdinalIgnoreCase))
            _selectedProjectPalettePath = activePath;

        ImGui.TextUnformatted("当前调色板");
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.BeginCombo("##当前项目调色板", PaletteDisplayName(_selectedProjectPalettePath)))
        {
            foreach (var path in paths)
            {
                var selected = string.Equals(path, _selectedProjectPalettePath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(PaletteDisplayName(path), selected))
                {
                    _selectedProjectPalettePath = path;
                    TryLoadProjectPalette(path, true);
                }

                if (selected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"项目配置当前指向：{_projectSettings.ActivePalettePath}");
        ImGui.Separator();
        ImGui.InputText("新调色板名称", ref _newProjectPaletteName, 96);
        if (ImGui.Button("新建当前调色板副本", new Vector2(180F, 30F)))
            CreateProjectPaletteAsset(_palette);
        ImGui.SameLine();
        if (ImGui.Button("保存调色板", new Vector2(128F, 30F)))
        {
            SetStatus(EditorPalettes.SaveProjectPalette(_project, _selectedProjectPalettePath, _palette)
                ? $"已保存调色板：{PaletteDisplayName(_selectedProjectPalettePath)}"
                : "调色板保存失败");
        }

        ImGui.SameLine();
        if (ImGui.Button("重新载入", new Vector2(112F, 30F)))
            TryLoadProjectPalette(_selectedProjectPalettePath, false);

        ImGui.SameLine();
        ImGui.BeginDisabled(paths.Count <= 1);
        if (ImGui.Button("删除调色板", new Vector2(128F, 30F)))
            DeleteSelectedProjectPalette(paths);
        ImGui.EndDisabled();

        ImGui.SeparatorText($"颜色（{VoxelPalette.EntryCount} 色）");
        DrawPaletteSelector();
        var paletteColor = _palette[Math.Clamp(_paletteIndex, 0, VoxelPalette.EntryCount - 1)];
        var editableColor = new Vector4(
            paletteColor.R / 255F, paletteColor.G / 255F,
            paletteColor.B / 255F, paletteColor.A / 255F);
        if (ImGui.ColorEdit4($"编辑颜色 #{_paletteIndex}", ref editableColor,
                ImGuiColorEditFlags.DisplayRGB | ImGuiColorEditFlags.Uint8))
        {
            _palette[_paletteIndex] = new Rgba32(
                (byte)Math.Clamp((int)MathF.Round(editableColor.X * 255F), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(editableColor.Y * 255F), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(editableColor.Z * 255F), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(editableColor.W * 255F), 0, 255));
            SetStatus($"已修改调色板颜色 #{_paletteIndex}，请点击“保存调色板”写入项目资产");
        }
    }

    private static string PaletteDisplayName(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath.Replace('/', Path.DirectorySeparatorChar));

    private bool TryLoadProjectPalette(string relativePath, bool activate)
    {
        if (_project is null) return false;
        var normalized = EditorPalettes.NormalizeProjectPalettePath(relativePath);
        try
        {
            _palette = PaletteFormat.Load(EditorPalettes.ProjectPaletteFilePath(_project, normalized));
            _paletteIsProjectLevel = true;
            _selectedProjectPalettePath = normalized;
            if (activate)
            {
                _projectSettings.ActivePalettePath = normalized;
                SaveProjectSettings();
            }

            _pipeline?.ResetAccumulation();
            SetStatus($"已载入项目调色板：{PaletteDisplayName(normalized)}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"调色板载入失败：{exception.Message}");
            return false;
        }
    }

    private void CreateProjectPaletteAsset(Rgba32[] source)
    {
        if (_project is null) return;
        var name = _newProjectPaletteName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (name.Length == 0) name = "新调色板";
        if (!name.EndsWith(".palette", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".pal", StringComparison.OrdinalIgnoreCase))
            name += ".palette";

        var relativePath = $"Palettes/{name}";
        var absolutePath = EditorPalettes.ProjectPaletteFilePath(_project, relativePath);
        if (File.Exists(absolutePath))
        {
            SetStatus($"调色板已存在：{name}");
            return;
        }

        try
        {
            PaletteFormat.Save(absolutePath, (Rgba32[])source.Clone());
            AssetMetaStore.EnsureMeta(absolutePath);
            _selectedProjectPalettePath = relativePath;
            _projectSettings.ActivePalettePath = relativePath;
            SaveProjectSettings();
            _assetBrowser.Reset();
            TryLoadProjectPalette(relativePath, false);
            SetStatus($"已创建项目调色板：{name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"创建调色板失败：{exception.Message}");
        }
    }

    private void DeleteSelectedProjectPalette(IReadOnlyList<string> paths)
    {
        if (_project is null || paths.Count <= 1) return;
        var deletedPath = _selectedProjectPalettePath;
        var path = EditorPalettes.ProjectPaletteFilePath(_project, deletedPath);
        try
        {
            File.Delete(path);
            AssetMetaStore.DeleteMeta(path);
            var next = paths.FirstOrDefault(item => !string.Equals(item, _selectedProjectPalettePath,
                StringComparison.OrdinalIgnoreCase));
            if (next is not null)
            {
                _selectedProjectPalettePath = next;
                _projectSettings.ActivePalettePath = next;
                SaveProjectSettings();
                _assetBrowser.Reset();
                TryLoadProjectPalette(next, false);
            }

            SetStatus($"已删除调色板：{PaletteDisplayName(deletedPath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"删除调色板失败：{exception.Message}");
        }
    }
}
