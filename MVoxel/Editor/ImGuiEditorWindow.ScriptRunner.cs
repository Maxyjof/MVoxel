using ImGuiNET;
using MVoxel.Project;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 独立的脚本运行器窗口：默认不打开，从「窗口 → 管理 → 脚本运行器…」进入。
/// 选择一个 .js / .lua 文件后一次性执行，不安装、不注册、不常驻，每次运行都是全新沙箱。
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private const string SampleJavaScript = """
// MVoxel 一次性脚本示例：每次运行都是全新沙箱，不安装、不注册、不常驻
console.log("项目：", mvoxel.projectName);
console.log("路径：", mvoxel.projectPath);
console.log("场景对象：", mvoxel.sceneObjectCount, "  体素：", mvoxel.voxelCount);
console.log("选中对象：", mvoxel.selectedObjectCount, "  已启用插件：", mvoxel.enabledPluginCount);
mvoxel.status("一次性脚本执行完成");
"完成";
""";

    private const string SampleLua = """
-- MVoxel 一次性脚本示例：每次运行都是全新沙箱，不安装、不注册、不常驻
print("项目：" .. mvoxel.projectName)
print("路径：" .. mvoxel.projectPath)
print("场景对象：" .. tostring(mvoxel.sceneObjectCount))
print("体素：" .. tostring(mvoxel.voxelCount))
mvoxel.status("一次性脚本执行完成")
return "完成"
""";

    private bool _showScriptRunnerWindow;
    private bool _focusScriptRunnerWindow;
    private string _scriptRunnerPath = string.Empty;
    private string _scriptRunnerSource = string.Empty;
    private string _scriptRunnerLanguage = "JavaScript";
    private float _scriptRunnerTimeoutSeconds = 5F;
    private readonly List<string> _scriptRunnerRecent = new();
    private readonly List<OneShotScriptLine> _scriptRunnerOutput = new();
    private Task<OneShotScriptResult>? _scriptRunnerTask;
    private OneShotScriptResult? _scriptRunnerLastResult;
    private string _scriptRunnerState = "未选择脚本";
    private bool _scriptRunnerAutoScroll = true;
    private bool _scriptRunnerScrollPending;

    private void OpenScriptRunnerWindow()
    {
        _showScriptRunnerWindow = true;
        _focusScriptRunnerWindow = true;
    }

    private void DrawScriptRunnerWindow()
    {
        PollScriptResult();
        if (!_showScriptRunnerWindow) return;
        if (_focusScriptRunnerWindow)
        {
            ImGui.SetNextWindowFocus();
            _focusScriptRunnerWindow = false;
        }

        var unit = ImGui.GetFontSize();
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(display * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(unit * 58F, unit * 36F), ImGuiCond.FirstUseEver);
        if (!BeginToolWindow("脚本运行器##ScriptRunnerWindow", PanelWindowFlags, CloseScriptRunnerWindow))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("选择脚本…")) PickScriptFile();
        ImGui.SameLine();
        DrawScriptRecentCombo();
        ImGui.SameLine();
        var running = _scriptRunnerTask is { IsCompleted: false };
        ImGui.BeginDisabled(running || string.IsNullOrEmpty(_scriptRunnerPath));
        if (ImGui.Button(running ? "执行中…" : "运行")) RunScript();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && string.IsNullOrEmpty(_scriptRunnerPath))
            ImGui.SetTooltip("请先选择一个脚本文件");
        ImGui.SameLine();
        if (ImGui.Button("清空输出")) _scriptRunnerOutput.Clear();
        ImGui.SameLine();
        if (ImGui.Button("复制输出")) CopyScriptOutput();
        ImGui.SameLine();
        if (ImGui.SmallButton("示例 ▼")) ImGui.OpenPopup("##ScriptSampleMenu");
        if (ImGui.BeginPopup("##ScriptSampleMenu"))
        {
            if (ImGui.MenuItem("JavaScript 示例")) CreateSampleScript(lua: false);
            if (ImGui.MenuItem("Lua 示例")) CreateSampleScript(lua: true);
            ImGui.EndPopup();
        }

        ImGui.SetNextItemWidth(unit * 14F);
        ImGui.SliderFloat("超时（秒）##ScriptTimeout", ref _scriptRunnerTimeoutSeconds, 1F, 30F, "%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("单次执行的时间上限；超时中断脚本并写入输出");
        ImGui.SameLine();
        ImGui.Checkbox("自动滚动", ref _scriptRunnerAutoScroll);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrEmpty(_scriptRunnerPath));
        if (ImGui.SmallButton("打开脚本文件")) OpenScriptInEditor();
        ImGui.EndDisabled();

        ImGui.Separator();
        DrawScriptRunnerInfo();
        ImGui.Separator();

        var outputHeight = MathF.Max(unit * 8F, ImGui.GetContentRegionAvail().Y - unit * 1.5F);
        if (ImGui.BeginChild("##ScriptRunnerOutput", new Vector2(0F, outputHeight), ImGuiChildFlags.Borders))
            DrawScriptRunnerOutput();
        ImGui.EndChild();

        ImGui.End();
    }

    private void CloseScriptRunnerWindow() => _showScriptRunnerWindow = false;

    private void DrawScriptRecentCombo()
    {
        if (!ImGui.BeginCombo("##ScriptRecent", "最近脚本")) return;
        if (_scriptRunnerRecent.Count == 0) ImGui.TextDisabled("本次会话还没有运行过脚本");
        foreach (var path in _scriptRunnerRecent.ToArray())
        {
            if (ImGui.Selectable($"{Path.GetFileName(path)}##recent_{path}",
                    string.Equals(path, _scriptRunnerPath, StringComparison.OrdinalIgnoreCase)))
                LoadScriptFile(path);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        }
        ImGui.EndCombo();
    }

    private void DrawScriptRunnerInfo()
    {
        var path = string.IsNullOrEmpty(_scriptRunnerPath) ? "（未选择）" : _scriptRunnerPath;
        ImGui.TextDisabled("脚本");
        ImGui.SameLine(ImGui.GetFontSize() * 5F);
        ImGui.TextUnformatted(path);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        ImGui.TextDisabled("语言");
        ImGui.SameLine(ImGui.GetFontSize() * 5F);
        ImGui.TextUnformatted(_scriptRunnerLanguage);
        ImGui.TextDisabled("大小");
        ImGui.SameLine(ImGui.GetFontSize() * 5F);
        ImGui.TextUnformatted(_scriptRunnerSource.Length == 0
            ? "—"
            : $"{_scriptRunnerSource.Length / 1024F:0.0} KiB（上限 {OneShotScriptRunner.MaxSourceLength / 1024} KiB）");
        ImGui.TextDisabled("状态");
        ImGui.SameLine(ImGui.GetFontSize() * 5F);
        var stateColor = _scriptRunnerLastResult is { Success: false }
            ? new Vector4(1F, .45F, .42F, 1F)
            : new Vector4(.45F, .85F, .5F, 1F);
        ImGui.TextColored(stateColor, _scriptRunnerState);
    }

    private void DrawScriptRunnerOutput()
    {
        if (_scriptRunnerOutput.Count == 0)
        {
            ImGui.TextDisabled("输出会显示在这里。脚本可用 console.log / print 输出，用 mvoxel 读取编辑器只读快照。");
            return;
        }

        foreach (var line in _scriptRunnerOutput)
        {
            var color = line.Kind switch
            {
                "error" => new Vector4(1F, .45F, .42F, 1F),
                "warn" => new Vector4(1F, .72F, .3F, 1F),
                "result" => new Vector4(.45F, .8F, 1F, 1F),
                _ => ToColor(EditorPalette.Text),
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(line.Text);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
        if (_scriptRunnerAutoScroll && _scriptRunnerScrollPending)
        {
            ImGui.SetScrollHereY(1F);
            _scriptRunnerScrollPending = false;
        }
    }

    // ---------- 选择与执行 ----------

    private void PickScriptFile()
    {
        var path = NativeDialogs.PickFile("选择一次性执行的脚本",
            "JavaScript\0*.js\0Lua\0*.lua\0全部文件\0*.*\0");
        if (string.IsNullOrWhiteSpace(path)) return;
        LoadScriptFile(path);
    }

    private void CreateSampleScript(bool lua)
    {
        var extension = lua ? "lua" : "js";
        var target = NativeDialogs.SaveFile("保存示例脚本",
            lua ? "Lua 脚本\0*.lua\0" : "JavaScript 脚本\0*.js\0",
            extension, "mvoxel-sample." + extension);
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            File.WriteAllText(target, lua ? SampleLua : SampleJavaScript);
            LoadScriptFile(target);
            SetStatus($"已创建示例脚本：{target}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _scriptRunnerOutput.Add(new OneShotScriptLine("error", $"创建示例脚本失败：{exception.Message}"));
            SetStatus($"创建示例脚本失败：{exception.Message}");
        }
    }

    private void OpenScriptInEditor()
    {
        if (string.IsNullOrEmpty(_scriptRunnerPath) || !File.Exists(_scriptRunnerPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_scriptRunnerPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetStatus($"无法打开脚本：{exception.Message}");
        }
    }

    private void LoadScriptFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > OneShotScriptRunner.MaxSourceLength)
                throw new InvalidDataException($"脚本超过 {OneShotScriptRunner.MaxSourceLength / 1024} KiB 上限");
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("脚本运行器只支持 .js 与 .lua 单文件");
            _scriptRunnerSource = File.ReadAllText(path);
            _scriptRunnerPath = Path.GetFullPath(path);
            _scriptRunnerLanguage = extension.Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "Lua" : "JavaScript";
            _scriptRunnerRecent.RemoveAll(item => string.Equals(item, _scriptRunnerPath, StringComparison.OrdinalIgnoreCase));
            _scriptRunnerRecent.Insert(0, _scriptRunnerPath);
            if (_scriptRunnerRecent.Count > 8) _scriptRunnerRecent.RemoveRange(8, _scriptRunnerRecent.Count - 8);
            _scriptRunnerState = $"就绪：{Path.GetFileName(_scriptRunnerPath)}";
            _scriptRunnerLastResult = null;
            _scriptRunnerOutput.Clear();
            _scriptRunnerScrollPending = false;
            SetStatus($"已选择脚本：{Path.GetFileName(_scriptRunnerPath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _scriptRunnerOutput.Add(new OneShotScriptLine("error", $"读取脚本失败：{exception.Message}"));
            _scriptRunnerState = "读取失败";
            SetStatus($"读取脚本失败：{exception.Message}");
        }
    }

    private void RunScript()
    {
        if (string.IsNullOrEmpty(_scriptRunnerPath) || !File.Exists(_scriptRunnerPath))
        {
            SetStatus("脚本文件不存在，请重新选择");
            return;
        }

        // 文件可能被外部编辑器改过，每次运行都重新读盘
        try { _scriptRunnerSource = File.ReadAllText(_scriptRunnerPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _scriptRunnerOutput.Add(new OneShotScriptLine("error", $"读取脚本失败：{exception.Message}"));
            return;
        }

        var request = BuildScriptRequest();
        _scriptRunnerOutput.Clear();
        _scriptRunnerOutput.Add(new OneShotScriptLine("log", $"▶ 开始执行 {Path.GetFileName(request.FilePath)}（{request.Language}，超时 {request.Timeout.TotalSeconds:0} 秒）"));
        _scriptRunnerLastResult = null;
        _scriptRunnerState = "执行中…";
        _scriptRunnerTask = Task.Run(() => OneShotScriptRunner.Execute(request));
        SetStatus($"正在执行脚本：{Path.GetFileName(request.FilePath)}");
    }

    private OneShotScriptRequest BuildScriptRequest()
    {
        var selected = _selectedSceneObjectIds.Count > 1
            ? _selectedSceneObjectIds.Count
            : _selectedSceneObjectId is null ? 0 : 1;
        return new OneShotScriptRequest(
            FilePath: _scriptRunnerPath,
            Language: _scriptRunnerLanguage,
            Source: _scriptRunnerSource,
            Timeout: TimeSpan.FromSeconds(_scriptRunnerTimeoutSeconds),
            ProjectName: _project?.Manifest.Name ?? string.Empty,
            ProjectPath: _project?.RootPath ?? string.Empty,
            SceneObjectCount: _sceneDocument?.Objects.Count ?? 0,
            VoxelCount: _world.OccupiedVoxelCount,
            SelectedObjectCount: selected,
            EnabledPluginCount: _project is null ? 0 : PluginManager.ListEnabled(_project).Count);
    }

    private void PollScriptResult()
    {
        if (_scriptRunnerTask is not { IsCompleted: true } task) return;
        _scriptRunnerTask = null;
        OneShotScriptResult result;
        try
        {
            result = task.Result;
        }
        catch (Exception exception)
        {
            _scriptRunnerOutput.Add(new OneShotScriptLine("error", $"执行异常：{exception.Message}"));
            _scriptRunnerState = "执行异常";
            SetStatus("脚本执行异常");
            return;
        }

        _scriptRunnerLastResult = result;
        foreach (var line in result.Lines) _scriptRunnerOutput.Add(line);
        _scriptRunnerOutput.Add(new OneShotScriptLine("result",
            $"■ {(result.Success ? "执行完成" : "执行失败")} · {result.ElapsedMilliseconds:0.0} ms · {result.Language}"));
        _scriptRunnerScrollPending = true;
        _scriptRunnerState = result.Success
            ? $"执行成功 · {result.ElapsedMilliseconds:0.0} ms"
            : $"执行失败：{result.Error}";
        if (!string.IsNullOrWhiteSpace(result.Status)) SetStatus(result.Status);
        else SetStatus(result.Success ? "脚本执行完成" : $"脚本执行失败：{result.Error}");
    }

    private void CopyScriptOutput()
    {
        if (_scriptRunnerOutput.Count == 0) return;
        ImGui.SetClipboardText(string.Join(Environment.NewLine, _scriptRunnerOutput.Select(item => item.Text)));
        SetStatus("已复制脚本输出");
    }
}
