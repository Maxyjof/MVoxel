using ImGuiNET;
using MVoxel.Core;
using MVoxel.Project;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 独立的插件管理窗口：默认不打开，从「窗口 → 管理 → 插件管理…」进入。
/// 集中完成安装、启用/停用、重载、查看清单与文档、打包分发、删除等操作。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private static readonly string[] PluginDocumentNames =
        ["README.md", "README.txt", "CHANGELOG.md", "LICENSE", "LICENSE.md", "package.json"];

    private const string PluginTemplateManifest = """
{
  "id": "com.example.my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "main": "index.js",
  "permissions": ["editor.command", "editor.panel"],
  "dependencies": {}
}
""";

    private const string PluginTemplateEntry = """
export function activate(host) {
  host.registerCommand("example.hello", "示例命令");
  host.registerPanel("example.panel", "示例面板", "来自插件模板的静态说明文本。");
}
export function runCommand(id, payload) {
  if (id === "example.hello") return { ok: true, message: "你好，MVoxel" };
  return { ok: false, error: "未知命令" };
}
""";

    private bool _showPluginManagerWindow;
    private bool _focusPluginManagerWindow;
    private string _pluginManagerFilter = string.Empty;
    private string? _selectedPluginId;
    private string? _pluginManagerMessage;
    private bool _pluginManagerMessageError;
    private string? _pluginUninstallTarget;
    private string _pluginDocumentPath = string.Empty;
    private string _pluginDocumentText = string.Empty;
    private IReadOnlyList<EditorPluginPackage> _pluginManagerPackages = Array.Empty<EditorPluginPackage>();
    private IReadOnlyList<string> _pluginManagerEnabled = Array.Empty<string>();
    private IReadOnlyList<ProjectPluginDiagnostic> _pluginManagerDiagnostics = Array.Empty<ProjectPluginDiagnostic>();

    /// <summary>从菜单打开插件管理窗口并读取一次磁盘快照</summary>
    private void OpenPluginManagerWindow()
    {
        _showPluginManagerWindow = true;
        _focusPluginManagerWindow = true;
        RefreshPluginManager();
    }

    private void RefreshPluginManager()
    {
        try
        {
            _pluginManagerPackages = PluginManager.ListInstalled();
            _pluginManagerEnabled = _project is null ? Array.Empty<string>() : PluginManager.ListEnabled(_project);
            _pluginManagerDiagnostics = _project is null
                ? Array.Empty<ProjectPluginDiagnostic>()
                : PluginManager.Diagnose(_project);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportPluginManager($"读取插件列表失败：{exception.Message}", true);
        }
    }

    private void ReportPluginManager(string message, bool error = false)
    {
        _pluginManagerMessage = message;
        _pluginManagerMessageError = error;
        SetStatus(message);
        if (error) MVoxelLog.Warn(message);
    }

    private void DrawPluginManagerWindow()
    {
        if (!_showPluginManagerWindow) return;
        if (_focusPluginManagerWindow)
        {
            ImGui.SetNextWindowFocus();
            _focusPluginManagerWindow = false;
        }

        var unit = ImGui.GetFontSize();
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(display * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(unit * 62F, unit * 40F), ImGuiCond.FirstUseEver);
        if (!BeginToolWindow("插件管理##PluginManagerWindow", PanelWindowFlags, ClosePluginManagerWindow))
        {
            ImGui.End();
            return;
        }

        DrawPluginManagerToolbar();
        if (!string.IsNullOrEmpty(_pluginManagerMessage))
        {
            ImGui.TextColored(_pluginManagerMessageError
                    ? new Vector4(1F, .45F, .42F, 1F)
                    : new Vector4(.45F, .85F, .5F, 1F),
                _pluginManagerMessage);
        }
        ImGui.Separator();

        var available = ImGui.GetContentRegionAvail();
        var listWidth = Math.Clamp(available.X * .38F, unit * 16F, unit * 24F);
        if (ImGui.BeginChild("##PluginManagerList", new Vector2(listWidth, 0F), ImGuiChildFlags.Borders))
            DrawPluginManagerList();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##PluginManagerDetails", Vector2.Zero, ImGuiChildFlags.Borders))
            DrawPluginManagerDetails();
        ImGui.EndChild();

        ImGui.End();
    }

    private void ClosePluginManagerWindow() => _showPluginManagerWindow = false;

    private void DrawPluginManagerToolbar()
    {
        if (ImGui.Button("刷新")) RefreshPluginManager();
        ImGui.SameLine();
        if (ImGui.Button("安装插件包…")) _pendingActions.Enqueue(() => InstallPluginForManager(packageFile: true));
        ImGui.SameLine();
        if (ImGui.Button("安装插件目录…")) _pendingActions.Enqueue(() => InstallPluginForManager(packageFile: false));
        ImGui.SameLine();
        if (ImGui.Button("重载全部")) _pendingActions.Enqueue(ReloadPluginsForManager);
        ImGui.SameLine();
        if (ImGui.Button("创建插件模板…")) _pendingActions.Enqueue(CreatePluginTemplateForManager);

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16F);
        ImGui.InputTextWithHint("##PluginManagerFilter", "搜索名称或 ID", ref _pluginManagerFilter, 128);
        ImGui.SameLine();
        ImGui.TextDisabled($"共 {_pluginManagerPackages.Count} 个插件 · {_pluginManagerEnabled.Count} 个已启用");
        ImGui.SameLine();
        if (ImGui.SmallButton("打开插件目录")) OpenPluginDirectoryInExplorer();
        ImGui.SameLine();
        if (ImGui.SmallButton("复制目录路径"))
        {
            ImGui.SetClipboardText(AppPaths.PluginDirectory);
            ReportPluginManager("已复制插件目录路径");
        }
    }

    private void DrawPluginManagerList()
    {
        ImGui.TextDisabled("已安装插件");
        ImGui.Separator();
        var filter = _pluginManagerFilter.Trim();
        foreach (var package in _pluginManagerPackages)
        {
            var displayName = string.IsNullOrWhiteSpace(package.Name) ? package.Id : package.Name;
            if (filter.Length > 0 &&
                !displayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !package.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var enabled = _pluginManagerEnabled.Contains(package.Id, StringComparer.Ordinal);
            var selected = string.Equals(_selectedPluginId, package.Id, StringComparison.Ordinal);
            var label = $"{(enabled ? "●" : "○")} {displayName}##pmrow_{package.Id}";
            if (ImGui.Selectable(label, selected))
            {
                _selectedPluginId = package.Id;
                _pluginUninstallTarget = null;
                _pluginDocumentPath = string.Empty;
                _pluginDocumentText = string.Empty;
            }
        }

        var missing = _pluginManagerDiagnostics.Where(item => !item.Installed).ToArray();
        if (missing.Length == 0) return;
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1F, .65F, .25F, 1F), $"项目引用了 {missing.Length} 个缺失插件");
        foreach (var item in missing)
        {
            ImGui.BulletText(item.PluginId);
            ImGui.SameLine();
            if (ImGui.SmallButton($"从项目移除##pmmissing_{item.PluginId}"))
                _pendingActions.Enqueue(() => SetPluginEnabledForManager(item.PluginId, false));
        }
    }

    private void DrawPluginManagerDetails()
    {
        var package = _pluginManagerPackages.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedPluginId, StringComparison.Ordinal));
        if (package is null)
        {
            ImGui.TextDisabled("从左侧选择一个插件查看详情");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted("插件随程序内置或安装到编辑器插件目录；是否启用按当前项目单独记录。安装后需要重载才会执行新的入口脚本。");
            ImGui.PopTextWrapPos();
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(package.Name) ? package.Id : package.Name;
        ImGui.TextUnformatted(displayName);
        var enabled = _pluginManagerEnabled.Contains(package.Id, StringComparer.Ordinal);
        var toggle = enabled;
        ImGui.SameLine();
        ImGui.BeginDisabled(_project is null);
        if (ImGui.Checkbox("在当前项目启用##pm_enable", ref toggle))
            _pendingActions.Enqueue(() => SetPluginEnabledForManager(package.Id, toggle));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(_project is null ? "打开项目后才能管理启用状态" : "写入项目 Plugins/manifest.json 并重载插件");

        ImGui.Separator();
        DrawPluginManagerField("ID", package.Id);
        DrawPluginManagerField("版本", package.Version);
        DrawPluginManagerField("来源", string.Equals(package.Source, "builtin", StringComparison.Ordinal) ? "程序内置" : "用户安装");
        DrawPluginManagerField("入口", package.Entry);
        DrawPluginManagerField("目录", package.RootDirectory);
        DrawPluginManagerField("权限", package.Permissions.Count == 0 ? "无" : string.Join("、", package.Permissions));
        DrawPluginManagerField("依赖", package.Dependencies.Count == 0
            ? "无"
            : string.Join("、", package.Dependencies.Select(item => $"{item.Key} {item.Value}")));

        var diagnostic = _pluginManagerDiagnostics.FirstOrDefault(item =>
            string.Equals(item.PluginId, package.Id, StringComparison.Ordinal));
        DrawPluginManagerField("项目诊断", enabled ? diagnostic?.Status ?? "正常" : "未启用");
        var activation = "未激活";
        if (_pluginRuntime is { } runtime && runtime.ActivationStatus.TryGetValue(package.Id, out var status))
            activation = status;
        DrawPluginManagerField("启动状态", activation);
        var commands = _pluginRuntime?.Commands.Count(item => item.PluginId == package.Id) ?? 0;
        var panels = _pluginRuntime?.Panels.Count(item => item.PluginId == package.Id) ?? 0;
        var components = _pluginRuntime?.Components.Count(item => item.PluginId == package.Id) ?? 0;
        var automations = _pluginRuntime?.Automations.Count(item => item.PluginId == package.Id) ?? 0;
        DrawPluginManagerField("注册项", $"命令 {commands} · 面板 {panels} · 组件 {components} · 自动化 {automations}");

        ImGui.Separator();
        if (ImGui.Button("打开所在目录")) OpenPathInExplorer(package.RootDirectory);
        ImGui.SameLine();
        if (ImGui.Button("复制路径"))
        {
            ImGui.SetClipboardText(package.RootDirectory);
            ReportPluginManager("已复制插件目录路径");
        }
        ImGui.SameLine();
        if (ImGui.Button("打包分发…")) _pendingActions.Enqueue(() => PackPluginForManager(package));
        ImGui.SameLine();
        var builtin = string.Equals(package.Source, "builtin", StringComparison.Ordinal);
        ImGui.BeginDisabled(builtin);
        if (ImGui.Button("删除插件")) _pluginUninstallTarget = package.Id;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(builtin ? "内置插件只能停用，不能删除" : "从编辑器插件目录彻底删除，并移出当前项目清单");

        if (_pluginUninstallTarget is { } target && string.Equals(target, package.Id, StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1F, .65F, .25F, 1F), $"确定删除「{displayName}」？");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted("插件目录会被直接删除，当前项目的启用记录一并移除；此操作不可撤销。");
            ImGui.PopTextWrapPos();
            if (ImGui.Button("确认删除##pm_confirm")) _pendingActions.Enqueue(() => UninstallPluginForManager(target));
            ImGui.SameLine();
            if (ImGui.Button("取消##pm_cancel")) _pluginUninstallTarget = null;
        }

        ImGui.Separator();
        DrawPluginManagerDocuments(package);
    }

    private void DrawPluginManagerDocuments(EditorPluginPackage package)
    {
        ImGui.TextDisabled("随附文档");
        foreach (var name in PluginDocumentNames)
        {
            var path = Path.Combine(package.RootDirectory, name);
            if (!File.Exists(path)) continue;
            if (ImGui.SmallButton($"{name}##pmdoc_{name}"))
            {
                _pluginDocumentPath = name;
                try
                {
                    var text = File.ReadAllText(path);
                    _pluginDocumentText = text.Length > 65_536 ? text[..65_536] + "\n…（已截断）" : text;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _pluginDocumentText = $"读取失败：{exception.Message}";
                }
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        if (string.IsNullOrEmpty(_pluginDocumentPath)) return;
        if (ImGui.BeginChild("##PluginManagerDocument", new Vector2(0F, ImGui.GetFontSize() * 11F), ImGuiChildFlags.Borders))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(_pluginDocumentText);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
    }

    private static void DrawPluginManagerField(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(ImGui.GetFontSize() * 7F);
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
    }

    // ---------- 操作（统一放进 _pendingActions，避免在 UI 帧里做磁盘/弹窗等重活） ----------

    private void InstallPluginForManager(bool packageFile)
    {
        try
        {
            EditorPluginPackage package;
            if (packageFile)
            {
                var path = NativeDialogs.PickFile("选择插件分发包", "MVoxel 插件包\0*.mvplugin\0全部文件\0*.*\0");
                if (string.IsNullOrWhiteSpace(path)) return;
                package = PluginManager.InstallFromPackage(path);
            }
            else
            {
                var source = NativeDialogs.PickFolder("选择包含 package.json 的插件目录");
                if (string.IsNullOrWhiteSpace(source)) return;
                package = PluginManager.InstallFromDirectory(source);
            }

            if (_project is not null) ReloadPlugins();
            RefreshPluginManager();
            _selectedPluginId = package.Id;
            ReportPluginManager($"已安装插件：{package.Name}（{package.Version}）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MVoxelLog.Error("安装插件失败", exception);
            ReportPluginManager($"安装插件失败：{exception.Message}", true);
        }
    }

    private void CreatePluginTemplateForManager()
    {
        var folder = NativeDialogs.PickFolder("选择用于存放新插件模板的空目录");
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var manifest = Path.Combine(folder, "package.json");
            var entry = Path.Combine(folder, "index.js");
            if (File.Exists(manifest) || File.Exists(entry))
            {
                ReportPluginManager("目标目录已包含 package.json 或 index.js，请另选一个空目录", true);
                return;
            }

            File.WriteAllText(manifest, PluginTemplateManifest);
            File.WriteAllText(entry, PluginTemplateEntry);
            var readme = Path.Combine(folder, "README.md");
            File.WriteAllText(readme,
                "# 我的插件\n\n由 MVoxel 插件模板生成。安装前请先把 package.json 的 id 改成唯一值，然后用插件管理窗口的「安装插件目录…」安装，再在项目中启用。\n");
            ReportPluginManager($"已创建插件模板：{folder}；安装后可在当前项目启用");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Error("创建插件模板失败", exception);
            ReportPluginManager($"创建插件模板失败：{exception.Message}", true);
        }
    }

    private void SetPluginEnabledForManager(string pluginId, bool enabled)
    {
        if (_project is null)
        {
            ReportPluginManager("请先打开项目再管理插件启用状态", true);
            return;
        }

        try
        {
            if (enabled) PluginManager.Enable(_project, pluginId);
            else PluginManager.Disable(_project, pluginId);
            ReloadPlugins();
            RefreshPluginManager();
            ReportPluginManager($"插件已{(enabled ? "启用" : "停用")}：{pluginId}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            MVoxelLog.Warn($"修改插件状态失败：{pluginId}，原因：{exception.Message}");
            ReportPluginManager($"修改插件状态失败：{exception.Message}", true);
        }
    }

    private void ReloadPluginsForManager()
    {
        if (_project is null)
        {
            ReportPluginManager("当前没有打开项目，无需重载插件", true);
            return;
        }

        ReloadPlugins();
        RefreshPluginManager();
        ReportPluginManager("插件已重新加载");
    }

    private void PackPluginForManager(EditorPluginPackage package)
    {
        try
        {
            var target = NativeDialogs.SaveFile("保存插件分发包", "MVoxel 插件包\0*.mvplugin\0", "mvplugin",
                package.Id + ".mvplugin");
            if (string.IsNullOrWhiteSpace(target)) return;
            PluginManager.PackPlugin(package.RootDirectory, target);
            ReportPluginManager($"已创建插件包：{target}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MVoxelLog.Error("打包插件失败", exception);
            ReportPluginManager($"打包插件失败：{exception.Message}", true);
        }
    }

    private void UninstallPluginForManager(string pluginId)
    {
        try
        {
            var directory = PluginManager.Uninstall(pluginId, _project);
            ReloadPlugins();
            RefreshPluginManager();
            _pluginUninstallTarget = null;
            if (string.Equals(_selectedPluginId, pluginId, StringComparison.Ordinal)) _selectedPluginId = null;
            _pluginDocumentPath = string.Empty;
            ReportPluginManager($"已删除插件：{pluginId}（{directory}）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            MVoxelLog.Error("删除插件失败", exception);
            ReportPluginManager($"删除插件失败：{exception.Message}", true);
        }
    }

    private static void OpenPluginDirectoryInExplorer()
    {
        var path = AppPaths.PluginDirectory;
        Directory.CreateDirectory(path);
        OpenPathInExplorer(path);
    }

    private static void OpenPathInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MVoxelLog.Warn($"无法打开目录：{path}，原因：{exception.Message}");
        }
    }
}
