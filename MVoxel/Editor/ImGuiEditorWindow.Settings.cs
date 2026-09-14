using ImGuiNET;
using MVoxel.Editor.Settings;
using MVoxel.Project;
using MVoxel.Rendering;
using Vector2 = System.Numerics.Vector2;

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

    /// <summary>
    /// 获取当前场景窗口实际使用的画面配置
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
        ApplyProjectSettingsToUi();
    }

    private void ApplyProjectSettingsToUi()
    {
        _projectLodLevels = Math.Clamp(_projectSettings.LodLevels, 1, _projectLodDistances.Length);
        Array.Copy(_projectSettings.LodDistances, _projectLodDistances, _projectLodDistances.Length);
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
        _editorLodLevels = Math.Clamp(_editorSettings.SceneLodLevels, 1, _editorLodDistances.Length);
        Array.Copy(_editorSettings.SceneLodDistances, _editorLodDistances, _editorLodDistances.Length);
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
        _projectSettings.LodLevels = _projectLodLevels;
        Array.Copy(_projectLodDistances, _projectSettings.LodDistances, _projectLodDistances.Length);
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
        ImGui.SetNextWindowSize(new Vector2(920F, 650F), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(720F, 460F), new Vector2(1600F, 1200F));
        if (ImGui.Begin("设置##MVoxelSettings", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextColored(ToColor(EditorPalette.Accent), "配置中心");
            ImGui.SameLine();
            ImGui.TextDisabled("编辑器级配置保存个人偏好，项目级配置随项目保存");
            if (ImGui.Button("保存编辑器设置")) SaveEditorSettings();
            if (_project is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("保存项目设置")) SaveProjectSettings();
            }

            ImGui.Separator();
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
        var categories = new[] { "项目", "场景", "画面", "资源" };
        DrawSettingsSidebar(categories, ref _projectSettingsCategory, "##项目设置分类", () =>
        {
            switch (_projectSettingsCategory)
            {
                case 0: DrawProjectGeneralSettings(); break;
                case 1: DrawProjectSceneSettings(); break;
                case 2: DrawProjectVisualSettings(); break;
                default: DrawProjectAssetSettings(); break;
            }
        });
        _projectSettings.LodLevels = _projectLodLevels;
        Array.Copy(_projectLodDistances, _projectSettings.LodDistances, _projectLodDistances.Length);
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
        ImGui.BeginChild(id, new Vector2(184F, contentHeight), ImGuiChildFlags.Borders);
        ImGui.TextColored(ToColor(EditorPalette.Accent), "设置分类");
        ImGui.Separator();
        for (var index = 0; index < categories.Count; index++)
        {
            if (ImGui.Selectable(categories[index], selected == index))
                selected = index;
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild(id + "内容", new Vector2(0F, contentHeight), ImGuiChildFlags.Borders);
        drawContent();
        ImGui.EndChild();
    }

    private void DrawEditorGeneralSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "常规");
        if (ImGui.BeginTabBar("##EditorGeneralTabs"))
        {
            if (ImGui.BeginTabItem("基本"))
            {
                ImGui.TextWrapped("新建项目默认放在项目根目录下，你也可以在新建项目对话框中改成其他路径");
                ImGui.SetNextItemWidth(-1F);
                ImGui.InputText("项目根目录", ref _editorSettings.ProjectRootPath, 2048);

                if (ImGui.Button("恢复默认项目根目录"))
                {
                    _editorSettings.ProjectRootPath = EditorSettingsDocument.DefaultProjectRootPath;
                }
                ImGui.SameLine();
                ImGui.TextDisabled(EditorSettingsDocument.DefaultProjectRootPath);
                ImGui.Checkbox("启用 GPU 诊断输出", ref _editorSettings.EnableGpuDiagnostics);
                ImGui.Checkbox("新项目优先使用硬件 RT", ref _editorSettings.UseHardwareRayTracing);
                ImGui.TextDisabled("硬件 RT 默认关闭，仅在新建项目时作为项目渲染设置的初始值");
                ImGui.TextWrapped("欢迎窗口是独立的强制入口；必须新建或打开项目后才可操作编辑器主窗口。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("输入行为"))
            {
                ImGui.TextWrapped("体素工具的具体鼠标行为在这里统一说明，快捷键方案在“输入 / 快捷键方案”中管理。");
                ImGui.TextDisabled("Caps Lock 游戏式移动、鼠标中键平移、Shift 环绕原点");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("撤回"))
            {
                ImGui.TextWrapped("Ctrl+Z 第一次按下立即撤回，持续按住达到延迟后按设定速度重复撤回");
                var changed = false;
                changed |= ImGui.SliderInt("开始持续撤回延迟（毫秒）", ref _editorSettings.UndoRepeatDelayMs, 100, 2000);
                changed |= ImGui.SliderInt("持续撤回间隔（毫秒）", ref _editorSettings.UndoRepeatIntervalMs, 20, 500);
                _editorSettings.Normalize();
                ImGui.TextDisabled($"当前速度：{1000F / _editorSettings.UndoRepeatIntervalMs:0.0} 次/秒");
                if (changed) SaveEditorSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorLayoutSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "布局");
        if (ImGui.BeginTabBar("##EditorLayoutTabs"))
        {
            if (ImGui.BeginTabItem("面板"))
            {
                ImGui.TextWrapped("默认布局：左侧层级、中间场景与渲染标签页、右侧检查器与渲染配置标签页，下方项目资产横跨全宽。");
                if (ImGui.Button("应用默认布局")) SelectLayout(LayoutPreset.Default);
                ImGui.SameLine();
                if (ImGui.Button("应用建模布局")) SelectLayout(LayoutPreset.Modeling);
                ImGui.SameLine();
                if (ImGui.Button("应用渲染布局")) SelectLayout(LayoutPreset.Rendering);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("停靠"))
            {
                ImGui.TextWrapped("开启布局编辑后，可拖拽面板标题栏停靠、悬浮和调整分割线；固定停靠的单窗口隐藏多余标签栏，浮动窗口保留标题栏。");
                if (ImGui.Button(_editPanelLayout ? "完成面板布局编辑" : "编辑面板布局"))
                    _editPanelLayout = !_editPanelLayout;
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorVisualSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "画面默认值");
        if (ImGui.BeginTabBar("##EditorVisualTabs"))
        {
            if (ImGui.BeginTabItem("场景画面"))
            {
                ImGui.TextWrapped("场景窗口固定使用无光照的纯体素预览，网格和体素棱线由场景辅助显示控制");
                ImGui.TextDisabled("LOD 只在场景窗口生效，硬件 RT 不参与场景预览");
                ImGui.SeparatorText("场景辅助显示");
                ImGui.Checkbox("显示场景网格", ref _editorSettings.ShowSceneGrid);
                ImGui.Checkbox("显示体素轮廓", ref _editorSettings.ShowVoxelOutlines);
                ImGui.SeparatorText("场景 LOD");
                SliderInt("LOD 等级数量", ref _editorSettings.SceneLodLevels, 1, _editorSettings.SceneLodDistances.Length);
                for (var index = 0; index < _editorSettings.SceneLodLevels; index++)
                    SliderFloat($"LOD 第 {index + 1} 级距离", ref _editorSettings.SceneLodDistances[index],
                        1F, 16384F, "%.0f 格");
                ImGui.SeparatorText("场景相机");
                SliderFloat("移动速度", ref _editorSettings.SceneMoveSpeed, 0.1F, 256F, "%.1f 格/秒");
                SliderFloat("近处裁剪", ref _editorSettings.SceneNearClip, 0.001F, 1000F, "%.3f 格");
                SliderFloat("远处裁剪", ref _editorSettings.SceneFarClip, 1F, 100000000F, "%.0f 格");
                SliderFloat("视场角 FOV", ref _editorSettings.SceneFieldOfView, 20F, 120F, "%.0f°");
                ImGui.SeparatorText("场景背景");
                DrawColorSlider("场景窗口背景色", ref _editorSettings.SceneRenderSettings.SceneBackgroundColor);
                ImGui.TextDisabled("未打开项目时使用此颜色，新项目会继承该颜色作为初始值");
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
                ImGui.TextDisabled("导出使用完整体素数据，不使用场景 LOD");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private static void DrawEditorRenderConfiguration(RenderSettings settings, string context)
    {
        DrawFullRenderConfiguration(settings, context);
    }

    private static void DrawFullRenderConfiguration(RenderSettings settings, string context)
    {
        ImGui.PushID(context);
        ImGui.TextDisabled($"{context}独立保存，默认使用 Vulkan GPU；GPU 不可用时安全回退 CPU");

        ImGui.SeparatorText("计算后端");
        var backend = (int)settings.Backend;
        if (ImGui.Combo("计算后端", ref backend, "GPU（Vulkan）\0CPU\0", 2))
            settings.Backend = (RenderBackend)Math.Clamp(backend, 0, 1);
        ImGui.TextDisabled(settings.Backend == RenderBackend.Gpu
            ? "GPU 模式优先执行 Vulkan Compute 或硬件 RT"
            : "CPU 模式用于兼容性、调试和无独立显卡环境");
        var hardwareRayTracing = settings.UseHardwareRayTracing;
        if (settings.Backend == RenderBackend.Cpu) hardwareRayTracing = false;
        ImGui.BeginDisabled(settings.Backend == RenderBackend.Cpu);
        ImGui.Checkbox("启用硬件 RT", ref hardwareRayTracing);
        ImGui.EndDisabled();
        settings.UseHardwareRayTracing = hardwareRayTracing;
        ImGui.TextDisabled("硬件 RT 默认关闭，需要设备支持；不可用时自动回退 Vulkan Compute");

        ImGui.SeparatorText("光照与环境");
        ImGui.Checkbox("太阳平行光", ref settings.SunEnabled);
        SliderFloat("太阳方位角", ref settings.SunAzimuthDegrees, 0F, 360F, "%.0f°");
        SliderFloat("太阳仰角", ref settings.SunElevationDegrees, 2F, 90F, "%.0f°");
        SliderFloat("太阳强度", ref settings.SunIntensity, 0F, 8F, "%.2f");
        DrawColorSlider("太阳颜色", ref settings.SunColor);
        SliderFloat("阴影柔和度", ref settings.ShadowSoftness, 0F, 0.5F, "%.3f");
        SliderInt("阴影采样数", ref settings.ShadowSamples, 1, 8);
        ImGui.Checkbox("显示地面平面", ref settings.ShowGround);
        DrawColorSlider("地面颜色", ref settings.GroundColor);
        SliderFloat("地面淡出距离", ref settings.GroundFade, 8F, 1024F, "%.0f 格");
        DrawColorSlider("天空顶部颜色", ref settings.SkyTop);
        DrawColorSlider("天空地平线颜色", ref settings.SkyHorizon);
        SliderFloat("天空环境光", ref settings.SkyAmbient, 0F, 2F, "%.2f");
        SliderFloat("暖色环境补光", ref settings.AmbientTint, 0F, 1F, "%.2f");
        ImGui.Checkbox("环境光遮蔽 AO", ref settings.AmbientOcclusionEnabled);
        SliderFloat("AO 强度", ref settings.AoStrength, 0F, 1F, "%.2f");
        SliderFloat("AO 对比度", ref settings.AoPower, 0.2F, 4F, "%.2f");
        DrawColorSlider("雾颜色", ref settings.FogColor);
        SliderFloat("雾浓度", ref settings.FogDensity, 0F, 0.02F, "%.4f");
        SliderFloat("近处淡入距离", ref settings.NearFade, 0F, 256F, "%.0f 格");

        ImGui.SeparatorText("颜色与输出");
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

        ImGui.SeparatorText("路径追踪质量");
        ImGui.Checkbox("启用路径追踪", ref settings.PathTracingEnabled);
        SliderInt("每帧样本数", ref settings.PathSamplesPerFrame, 1, 8);
        SliderInt("累积样本上限", ref settings.PathMaxAccumulatedSamples, 64, 8192);
        SliderFloat("内部渲染分辨率", ref settings.ResolutionScale, 0.25F, 1F, "%.2fx");
        SliderInt("最大弹射次数", ref settings.MaxBounces, 1, 12);
        SliderFloat("俄罗斯轮盘阈值", ref settings.RussianRouletteThreshold, 0.0005F, 0.05F, "%.4f");
        SliderFloat("辐射度截断", ref settings.RadianceClamp, 1F, 200F, "%.1f");
        ImGui.Checkbox("快速降噪", ref settings.PathDenoising);
        SliderFloat("降噪强度", ref settings.PathDenoiseStrength, 0F, 1F, "%.2f");
        SliderFloat("材质粗糙度", ref settings.PathRoughness, 0.01F, 1F, "%.3f");
        SliderFloat("材质金属度", ref settings.PathMetalness, 0F, 1F, "%.2f");
        SliderFloat("介电反射率 F0", ref settings.PathSpecularF0, 0F, 0.2F, "%.3f");
        SliderFloat("半透明吸收浓度", ref settings.TransmissiveAbsorption, 0F, 6F, "%.2f");
        ImGui.Checkbox("自发光体素参与照明", ref settings.EmissiveVoxels);
        SliderFloat("自发光强度", ref settings.EmissiveIntensity, 0F, 40F, "%.1f");
        ImGui.Checkbox("直接光 MIS 采样", ref settings.DirectLightingMIS);

        ImGui.SeparatorText("渲染相机");
        SliderFloat("视场角 FOV", ref settings.CameraFovDegrees, 20F, 120F, "%.0f°");
        ImGui.Checkbox("景深", ref settings.DepthOfField);
        SliderFloat("光圈半径", ref settings.ApertureRadius, 0F, 0.5F, "%.3f");
        SliderFloat("对焦距离", ref settings.FocusDistance, 1F, 256F, "%.1f 格");
        ImGui.PopID();
    }

    private void DrawEditorInputSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "输入");
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
                ImGui.TextWrapped(_editorSettings.McMode
                    ? "当前：左键破坏、右键放置；Shift + 左键环绕原点。"
                    : "当前：左键放置、右键破坏；Shift + 左键环绕原点。");
                ImGui.TextDisabled("中键平移；Caps Lock 开启后鼠标转向，WASD 八向移动，Q/E 升降，光标锁定在视口中心。");
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
        ImGui.TextColored(ToColor(EditorPalette.Accent), $"方案：{scheme.Name}");
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

        ImGui.SeparatorText("按命令修改绑定");
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

        ImGui.TextWrapped("支持 Ctrl / Shift / Alt 加字母、Space、F1-F12，例如 Ctrl+Z 或 Shift+Space。未知绑定不会触发命令。");
        ImGui.EndChild();
    }

    private void DrawEditorAppearanceSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "外观");
        if (ImGui.BeginTabBar("##EditorAppearanceTabs"))
        {
            if (ImGui.BeginTabItem("字体"))
            {
                ImGui.SliderFloat("界面缩放", ref _editorSettings.UiScale, 0.75F, 2F, "%.2fx");
                ImGui.TextDisabled("中文像素字体按 DPI 以整数倍构建图集，避免缩放后发虚。");
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

                ImGui.TextDisabled("主题改动会立即应用并保存。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawEditorPerformanceSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "性能");
        if (ImGui.BeginTabBar("##EditorPerformanceTabs"))
        {
            if (ImGui.BeginTabItem("性能策略"))
            {
                ImGui.TextWrapped("场景视图允许 LOD 和稀疏网格；渲染窗口与导出始终使用完整体素数据，不受 LOD 影响。");
                ImGui.TextDisabled("超大型建筑优先使用稀疏区块、视锥裁剪和有界网格线计划。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("诊断"))
            {
                ImGui.TextWrapped("诊断模式会检查字体缺字、Vulkan 帧尺寸和绘制顶点数。");
                ImGui.TextDisabled("仅建议开发和回归测试时开启。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectGeneralSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "项目");
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
                    foreach (var option in new[] { "默认", "大世界稀疏预览", "LOD 练习", "光追工作室", "程序化地形" })
                    {
                        if (ImGui.Selectable(option, option == preset))
                            _projectSettings.DefaultRenderPreset = option;
                    }

                    ImGui.EndCombo();
                }
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("自动保存"))
            {
                ImGui.Checkbox("启用自动保存建议", ref _projectSettings.AutoSave);
                ImGui.SliderInt("间隔（秒）", ref _projectSettings.AutoSaveSeconds, 10, 3600);
                ImGui.TextDisabled("当前阶段只保存建议值；真正写盘仍由项目保存流程统一执行。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectSceneSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "场景");
        if (ImGui.BeginTabBar("##ProjectSceneTabs"))
        {
            if (ImGui.BeginTabItem("LOD"))
            {
                ImGui.TextWrapped("LOD 仅用于场景窗口交互流畅度，渲染窗口和导出不会使用 LOD。");
                SliderInt("等级数量", ref _projectLodLevels, 1, _projectLodDistances.Length);
                for (var index = 0; index < _projectLodLevels; index++)
                    SliderFloat($"第 {index + 1} 级距离", ref _projectLodDistances[index], 1F, 16384F, "%.0f 格");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("辅助显示"))
            {
                ImGui.Checkbox("显示网格", ref _showGrid);
                ImGui.Checkbox("显示体素棱线", ref _showVoxelOutlines);
                ImGui.TextDisabled("网格和轮廓只出现在场景窗口，不会进入渲染窗口或导出结果");
                ImGui.SeparatorText("场景相机");
                SliderFloat("移动速度", ref _sceneMoveSpeed, 0.1F, 256F, "%.1f 格/秒");
                SliderFloat("近处裁剪", ref _sceneNearClip, 0.001F, 1000F, "%.3f 格");
                SliderFloat("远处裁剪", ref _sceneFarClip, 1F, 100000000F, "%.0f 格");
                SliderFloat("视场角 FOV", ref _sceneFieldOfView, 20F, 120F, "%.0f°");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectVisualSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "画面");
        if (ImGui.BeginTabBar("##ProjectVisualTabs"))
        {
            if (ImGui.BeginTabItem("场景画面"))
            {
                ImGui.TextWrapped("场景画面配置：网格、辅助线、体素轮廓和编辑视图质量。场景窗口只显示体素原色，不使用光照渲染。LOD 只在这里生效。");
                DrawColorSlider("场景窗口背景色", ref _projectSettings.SceneRenderSettings.SceneBackgroundColor);
                ImGui.TextDisabled("打开项目后优先使用项目级背景色");
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
                ImGui.TextDisabled("导出使用完整体素数据，不使用场景 LOD。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectAssetSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "资源");
        if (ImGui.BeginTabBar("##ProjectAssetTabs"))
        {
            if (ImGui.BeginTabItem("目录"))
            {
                ImGui.TextWrapped("Assets 下的场景、预制件、体素模型、材质、调色板、环境、渲染预设和生成器目录由项目结构自动维护。体素材质不使用贴图，基础色只能从当前调色板选择。");
                ImGui.TextDisabled("项目设置文件：ProjectSettings/settings.json");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("导入"))
            {
                ImGui.TextWrapped("导入与导出设置将在格式适配器接入后在这里配置；当前已支持 MagicaVoxel .vox 和 .palette。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }
}
