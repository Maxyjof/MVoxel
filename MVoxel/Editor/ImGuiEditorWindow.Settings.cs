using ImGuiNET;
using MVoxel.Editor.Settings;
using MVoxel.Project;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private EditorSettingsDocument _editorSettings = EditorSettingsDocument.CreateDefault();
    private ProjectSettingsDocument _projectSettings = ProjectSettingsDocument.CreateDefault();
    private bool _showSettingsWindow;
    private int _settingsScope;
    private int _editorSettingsCategory;
    private int _projectSettingsCategory;
    private int _selectedShortcutScheme;
    private string _newShortcutSchemeName = string.Empty;
    private string _renameShortcutScheme = string.Empty;

    private void LoadEditorSettings()
    {
        _editorSettings = SettingsStore.LoadEditor();
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
            // 设置写入失败不应阻断正在编辑的场景，下一次启动仍会使用默认值。
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
        _showLighting = _projectSettings.ShowLighting;
        _renderSettings = _projectSettings.RenderSettings;
        SyncSettingsToggles();
    }

    private void CaptureProjectSettingsFromUi()
    {
        _projectSettings.LodLevels = _projectLodLevels;
        Array.Copy(_projectLodDistances, _projectSettings.LodDistances, _projectLodDistances.Length);
        _projectSettings.ShowGrid = _showGrid;
        _projectSettings.ShowVoxelOutlines = _showVoxelOutlines;
        _projectSettings.ShowLighting = _showLighting;
        _projectSettings.RenderSettings = _renderSettings;
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
        var categories = new[] { "常规", "布局", "输入", "外观", "性能" };
        DrawSettingsSidebar(categories, ref _editorSettingsCategory, "##编辑器设置分类", () =>
        {
            switch (_editorSettingsCategory)
            {
                case 0: DrawEditorGeneralSettings(); break;
                case 1: DrawEditorLayoutSettings(); break;
                case 2: DrawEditorInputSettings(); break;
                case 3: DrawEditorAppearanceSettings(); break;
                default: DrawEditorPerformanceSettings(); break;
            }
        });
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
    }

    private static void DrawSettingsSidebar(IReadOnlyList<string> categories, ref int selected,
        string id, Action drawContent)
    {
        var contentHeight = Math.Max(1F, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild(id, new Vector2(148F, contentHeight), ImGuiChildFlags.Borders);
        ImGui.TextDisabled("设置分类");
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
                ImGui.Checkbox("启动时显示欢迎窗口", ref _editorSettings.ShowWelcomeOnStartup);
                ImGui.Checkbox("启用 GPU 诊断输出", ref _editorSettings.EnableGpuDiagnostics);
                ImGui.TextWrapped("欢迎窗口是独立的阻塞弹窗；关闭后才可操作编辑器主窗口。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("输入行为"))
            {
                ImGui.TextWrapped("体素工具的具体鼠标行为在这里统一说明，快捷键方案在“输入 / 快捷键方案”中管理。");
                ImGui.TextDisabled("Caps Lock 游戏式移动、鼠标中键平移、Shift 环绕原点");
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
                ImGui.TextWrapped("默认布局：左侧层级、中间场景/渲染、右侧检查器、下方项目资产。");
                if (ImGui.Button("应用默认布局")) SelectLayout(LayoutPreset.Default);
                ImGui.SameLine();
                if (ImGui.Button("应用建模布局")) SelectLayout(LayoutPreset.Modeling);
                ImGui.SameLine();
                if (ImGui.Button("应用渲染布局")) SelectLayout(LayoutPreset.Rendering);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("停靠"))
            {
                ImGui.TextWrapped("开启布局编辑后，可拖拽面板标题栏停靠、悬浮和调整分割线；默认状态隐藏多余标题栏。");
                if (ImGui.Button(_editPanelLayout ? "完成面板布局编辑" : "编辑面板布局"))
                    _editPanelLayout = !_editPanelLayout;
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
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
                            ConfigureImGui();
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
                ImGui.SliderInt("等级数量", ref _projectSettings.LodLevels, 1, _projectSettings.LodDistances.Length);
                for (var index = 0; index < _projectSettings.LodLevels; index++)
                    ImGui.SliderFloat($"第 {index + 1} 级距离", ref _projectSettings.LodDistances[index], 1F, 16384F, "%.0f 格");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("辅助显示"))
            {
                ImGui.Checkbox("显示网格", ref _projectSettings.ShowGrid);
                ImGui.Checkbox("显示体素棱线", ref _projectSettings.ShowVoxelOutlines);
                ImGui.Checkbox("场景光照预览", ref _projectSettings.ShowLighting);
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
                ImGui.TextWrapped("场景画面配置：网格、辅助线、场景光照和编辑视图质量。LOD 只在这里生效。");
                ImGui.Checkbox("场景网格", ref _projectSettings.ShowGrid);
                ImGui.Checkbox("体素轮廓", ref _projectSettings.ShowVoxelOutlines);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染画面"))
            {
                DrawProjectRenderBackendControls("渲染窗口");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("渲染导出画面"))
            {
                DrawProjectRenderBackendControls("导出画面");
                ImGui.TextDisabled("导出使用完整体素数据，不使用场景 LOD。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawProjectRenderBackendControls(string context)
    {
        ImGui.TextDisabled($"{context}默认使用 GPU；GPU 不可用时安全回退 CPU。");
        ImGui.Checkbox("开启光线追踪", ref _settingsTogglePath);
        ImGui.Checkbox("开启 GPU（默认）", ref _showLighting);
        ImGui.SliderInt("路径样本/帧", ref _renderSettings.PathSamplesPerFrame, 1, 8);
        ImGui.SliderInt("最大弹射次数", ref _renderSettings.MaxBounces, 1, 12);
    }

    private void DrawProjectAssetSettings()
    {
        ImGui.TextColored(ToColor(EditorPalette.Accent), "资源");
        if (ImGui.BeginTabBar("##ProjectAssetTabs"))
        {
            if (ImGui.BeginTabItem("目录"))
            {
                ImGui.TextWrapped("Assets 下的 Scenes、Prefabs、VoxelModels、Materials、Palettes、Textures 等目录由项目结构自动维护。");
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
