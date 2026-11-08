using ImGuiNET;
using MVoxel.Project;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 生成器脚本面板：编辑 .mvgenerator 源码、按声明生成参数控件、
/// 带参数运行并把结果作为单个可撤销事务应用到当前世界
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    private const int GeneratorSourceCapacity = 1_048_576;
    private const int GeneratorNameCapacity = 64;

    private bool _showGeneratorPanel;
    private string? _generatorRelativePath;
    private GeneratorAsset? _generatorAsset;
    private string _generatorSource = string.Empty;
    private GeneratorDefinition? _generatorDefinition;
    private string? _generatorDescribedSource;
    private readonly Dictionary<string, object?> _generatorParameters = new();
    private bool _generatorDirty;
    private string _generatorMessage = string.Empty;
    private bool _generatorMessageError;

    /// <summary>
    /// 切换项目或关闭项目时重置生成器面板状态
    /// </summary>
    private void ResetGeneratorPanel()
    {
        _generatorRelativePath = null;
        _generatorAsset = null;
        _generatorSource = string.Empty;
        _generatorDefinition = null;
        _generatorDescribedSource = null;
        _generatorParameters.Clear();
        _generatorDirty = false;
        _generatorMessage = string.Empty;
        _generatorMessageError = false;
    }

    /// <summary>
    /// 在生成器面板中打开项目内的 .mvgenerator 资产
    /// </summary>
    /// <param name="relativePath">相对 Assets 的路径</param>
    private void OpenGeneratorAsset(string relativePath) => OpenGeneratorAsset(relativePath, false);

    private void OpenGeneratorAsset(string relativePath, bool discardCurrentDraft)
    {
        if (_project is null)
        {
            SetStatus("请先打开项目再编辑生成器");
            return;
        }

        if (_generatorDirty && !discardCurrentDraft)
        {
            _showGeneratorPanel = true;
            SetStatus("生成器源码尚未保存，请先保存或恢复已保存版本");
            return;
        }

        var absolutePath = ResolveGeneratorAssetPath(relativePath);
        if (absolutePath is null)
        {
            SetStatus($"生成器资产路径无效：{relativePath}");
            return;
        }

        try
        {
            var asset = GeneratorAsset.Load(absolutePath);
            if (!string.Equals(_generatorRelativePath, relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                _generatorParameters.Clear();
                _generatorDefinition = null;
                _generatorDescribedSource = null;
            }
            _generatorAsset = asset;
            _generatorRelativePath = relativePath.Replace('\\', '/');
            _generatorSource = asset.Source;
            _generatorDirty = false;
            DescribeGeneratorForUi(asset);
            _showGeneratorPanel = true;
            SetStatus($"已打开生成器：{Path.GetFileName(absolutePath)}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _generatorMessage = $"打开生成器失败：{exception.Message}";
            _generatorMessageError = true;
            _showGeneratorPanel = true;
            SetStatus(_generatorMessage);
        }
    }

    /// <summary>
    /// 在项目 Generators 目录创建新生成器并在面板打开
    /// </summary>
    /// <param name="language">javascript 或 lua</param>
    /// <returns>新建资产的相对路径，失败返回 null</returns>
    internal string? CreateGeneratorAsset(string language, string? templateName = null, string? templateSource = null)
    {
        if (_project is null)
        {
            SetStatus("请先打开项目再创建生成器");
            return null;
        }

        if (_generatorDirty)
        {
            _showGeneratorPanel = true;
            SetStatus("生成器源码尚未保存，请先保存或恢复已保存版本");
            return null;
        }

        var directory = Path.Combine(_project.AssetsPath, "Generators");
        Directory.CreateDirectory(directory);
        string relativePath;
        string absolutePath;
        var index = 1;
        do
        {
            var name = templateName ?? "生成器";
            relativePath = $"Generators/{name}{index}.mvgenerator";
            absolutePath = Path.Combine(directory, $"{name}{index}.mvgenerator");
            index++;
        }
        while (File.Exists(absolutePath));

        try
        {
            var asset = new GeneratorAsset
            {
                Language = language,
                Source = templateSource ?? (language == GeneratorAsset.LuaLanguage
                    ? GeneratorAsset.LuaTemplate
                    : GeneratorAsset.JavaScriptTemplate),
            };
            asset.Save(absolutePath);
            HandleAssetChanged(new ProjectAssetChange(relativePath, relativePath));
            SetStatus($"已创建生成器：{Path.GetFileName(absolutePath)}");
            OpenGeneratorAsset(relativePath);
            return relativePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"创建生成器失败：{exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析并校验 Generators 目录下资产的绝对路径，拒绝 Assets 之外的路径
    /// </summary>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>合法时返回绝对路径，否则 null</returns>
    private string? ResolveGeneratorAssetPath(string relativePath)
    {
        if (_project is null || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(_project.AssetsPath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    /// <summary>
    /// 运行源码中的 generator.define 刷新参数表，保留同标识的已填值
    /// </summary>
    /// <param name="asset">待解析的生成器</param>
    private void DescribeGeneratorForUi(GeneratorAsset asset)
    {
        try
        {
            var previousDefinition = _generatorDefinition;
            _generatorDefinition = GeneratorRunner.Describe(asset, TimeSpan.FromSeconds(2));
            _generatorDescribedSource = asset.Source;
            var refreshed = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var parameter in _generatorDefinition.Parameters)
            {
                var previous = previousDefinition?.Parameters.FirstOrDefault(item => item.Id == parameter.Id);
                if (previous?.Type == parameter.Type && _generatorParameters.TryGetValue(parameter.Id, out var existing))
                {
                    try { refreshed[parameter.Id] = GeneratorRunner.NormalizeParameterValue(parameter, existing); }
                    catch (InvalidDataException) { refreshed[parameter.Id] = parameter.Default; }
                }
                else refreshed[parameter.Id] = parameter.Default;
            }

            _generatorParameters.Clear();
            foreach (var (key, value) in refreshed) _generatorParameters[key] = value;
            _generatorMessage = $"已加载生成器“{_generatorDefinition.Name}”，{_generatorDefinition.Parameters.Count} 个参数";
            _generatorMessageError = false;
        }
        catch (Exception exception)
        {
            _generatorDefinition = null;
            _generatorDescribedSource = null;
            _generatorParameters.Clear();
            _generatorMessage = $"生成器声明解析失败：{exception.Message}";
            _generatorMessageError = true;
        }
    }

    /// <summary>
    /// 绘制生成器面板窗口
    /// </summary>
    private void DrawGeneratorWindow()
    {
        if (!_showGeneratorPanel) return;
        if (_applyPreset && _layoutPreset == LayoutPreset.Generator) ImGui.SetNextWindowFocus();
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * .5F, ImGuiCond.FirstUseEver, new Vector2(.5F, .5F));
        ImGui.SetNextWindowSize(new Vector2(560F, 620F), ImGuiCond.FirstUseEver);
        if (!BeginToolWindow("生成器##生成器面板", ImGuiWindowFlags.NoCollapse, () => _showGeneratorPanel = false))
        {
            ImGui.End();
            return;
        }

        DrawGeneratorToolbar();
        ImGui.Separator();
        DrawGeneratorEditor();
        ImGui.Separator();
        DrawGeneratorParameters();
        DrawGeneratorMessage();
        ImGui.End();
    }

    /// <summary>
    /// 绘制生成器选择、新建与保存工具行
    /// </summary>
    private void DrawGeneratorToolbar()
    {
        if (_project is null)
        {
            ImGui.TextDisabled("打开项目后可在 Assets/Generators 中创建和运行生成器");
            return;
        }

        var currentLabel = _generatorRelativePath is null
            ? "未选择生成器"
            : Path.GetFileName(_generatorRelativePath);
        ImGui.SetNextItemWidth(220F);
        if (ImGui.BeginCombo("##生成器选择", currentLabel))
        {
            var directory = _project.AssetsPath;
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.mvgenerator", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).OrderBy(path => path))
                {
                    var relative = Path.GetRelativePath(_project.AssetsPath, file).Replace('\\', '/');
                    var selected = string.Equals(relative, _generatorRelativePath, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(Path.GetFileName(file), selected)) OpenGeneratorAsset(relative);
                }
            }
            else
            {
                ImGui.TextDisabled("Generators 目录还没有生成器");
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("新建 JS")) CreateGeneratorAsset(GeneratorAsset.JavaScriptLanguage);
        ImGui.SameLine();
        if (ImGui.Button("新建 Lua")) CreateGeneratorAsset(GeneratorAsset.LuaLanguage);
        ImGui.SameLine();
        if (ImGui.Button("内置脚本")) ImGui.OpenPopup("内置生成器模板");
        if (ImGui.BeginPopup("内置生成器模板"))
        {
            foreach (var template in BuiltInGenerators.All)
                if (ImGui.MenuItem(template.Name))
                    CreateGeneratorAsset(GeneratorAsset.JavaScriptLanguage, template.Name, template.Source);
            ImGui.EndPopup();
        }
        if (ImGui.Button("保存源码")) SaveGeneratorSource();
        ImGui.SameLine();
        if (ImGui.Button("重新读取声明"))
        {
            if (_generatorAsset is not null)
            {
                _generatorAsset.Source = _generatorSource;
                DescribeGeneratorForUi(_generatorAsset);
            }
        }

        if (_generatorDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1F, 0.72F, 0.3F, 1F), "未保存");
            ImGui.SameLine();
            if (ImGui.Button("恢复已保存版本") && _generatorRelativePath is not null)
                OpenGeneratorAsset(_generatorRelativePath, discardCurrentDraft: true);
        }
    }

    /// <summary>
    /// 绘制脚本源码多行编辑器
    /// </summary>
    private void DrawGeneratorEditor()
    {
        var available = ImGui.GetContentRegionAvail();
        var editorHeight = Math.Max(160F, available.Y * 0.52F);
        if (_generatorAsset is null)
        {
            ImGui.BeginChild("##生成器源码空态", new Vector2(-1F, editorHeight), ImGuiChildFlags.Borders);
            ImGui.TextDisabled("从左上角下拉选择生成器，或新建 JavaScript / Lua 生成器");
            ImGui.EndChild();
            return;
        }

        ImGui.TextDisabled(_generatorAsset.Language == GeneratorAsset.LuaLanguage ? "Lua 源码" : "JavaScript 源码");
        var source = _generatorSource;
        if (ImGui.InputTextMultiline("##生成器源码", ref source, GeneratorSourceCapacity,
                new Vector2(-1F, editorHeight), ImGuiInputTextFlags.AllowTabInput))
        {
            _generatorSource = source;
            _generatorDirty = true;
        }
    }

    /// <summary>
    /// 按声明绘制参数控件与运行按钮
    /// </summary>
    private void DrawGeneratorParameters()
    {
        if (_generatorAsset is null) return;
        ImGui.BeginChild("##生成器参数区", new Vector2(-1F, -ImGui.GetFrameHeightWithSpacing() * 2F), ImGuiChildFlags.Borders);
        if (_generatorDefinition is null)
        {
            ImGui.TextDisabled("源码中的 generator.define 声明无法解析，保存前请修正");
            ImGui.EndChild();
            return;
        }

        ImGui.TextUnformatted($"参数（{_generatorDefinition.Name}）");
        if (_generatorDefinition.Parameters.Count == 0) ImGui.TextDisabled("该生成器没有声明参数");
        foreach (var parameter in _generatorDefinition.Parameters)
        {
            var label = string.IsNullOrWhiteSpace(parameter.Label) ? parameter.Id : parameter.Label!;
            ImGui.PushID(parameter.Id);
            DrawParameterControl(parameter, label);
            ImGui.PopID();
        }

        ImGui.EndChild();
        var declarationCurrent = string.Equals(_generatorSource, _generatorDescribedSource, StringComparison.Ordinal);
        if (!declarationCurrent) ImGui.BeginDisabled();
        if (ImGui.Button("运行并应用到当前世界", new Vector2(-1F, 0F))) RunGenerator();
        if (!declarationCurrent)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("源码已更改，请先重新读取声明或保存源码");
        }
    }

    /// <summary>
    /// 按参数类型绘制单个控件并回写取值
    /// </summary>
    /// <param name="parameter">参数声明</param>
    /// <param name="label">显示名</param>
    private void DrawParameterControl(GeneratorParameter parameter, string label)
    {
        switch (parameter.Type)
        {
            case GeneratorParameterType.Int:
            {
                var value = (int)ParameterAsLong(parameter.Id);
                if (ExpressionInput.Int(label, ref value))
                {
                    if (parameter.Min is { } minimum) value = (int)Math.Max(value, Math.Ceiling(minimum));
                    if (parameter.Max is { } maximum) value = (int)Math.Min(value, Math.Floor(maximum));
                    _generatorParameters[parameter.Id] = (long)value;
                }
                break;
            }

            case GeneratorParameterType.Float:
            {
                var single = (float)ParameterAsDouble(parameter.Id);
                if (ExpressionInput.Float(label, ref single))
                {
                    var updated = (double)single;
                    if (parameter.Min is { } minimum) updated = Math.Max(updated, minimum);
                    if (parameter.Max is { } maximum) updated = Math.Min(updated, maximum);
                    _generatorParameters[parameter.Id] = updated;
                }
                break;
            }

            case GeneratorParameterType.Bool:
            {
                var value = _generatorParameters.TryGetValue(parameter.Id, out var raw) && raw is bool flag && flag;
                if (ImGui.Checkbox(label, ref value)) _generatorParameters[parameter.Id] = value;
                break;
            }

            case GeneratorParameterType.String:
            {
                var value = _generatorParameters.TryGetValue(parameter.Id, out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
                if (ImGui.InputText(label, ref value, 256, ImGuiInputTextFlags.AutoSelectAll)) _generatorParameters[parameter.Id] = value;
                break;
            }

            case GeneratorParameterType.Color:
            {
                var packed = ParameterAsLong(parameter.Id, 0xFFFFFF);
                var color = new Vector3(
                    ((packed >> 16) & 255) / 255F,
                    ((packed >> 8) & 255) / 255F,
                    (packed & 255) / 255F);
                if (ImGui.ColorEdit3(label, ref color))
                {
                    var r = (long)Math.Round(Math.Clamp(color.X, 0F, 1F) * 255F);
                    var g = (long)Math.Round(Math.Clamp(color.Y, 0F, 1F) * 255F);
                    var b = (long)Math.Round(Math.Clamp(color.Z, 0F, 1F) * 255F);
                    _generatorParameters[parameter.Id] = (r << 16) | (g << 8) | b;
                }
                break;
            }
        }
    }

    /// <summary>
    /// 读取参数表中的整数值，缺失或类型不符时回退默认值
    /// </summary>
    /// <param name="id">参数标识</param>
    /// <param name="fallback">缺省值</param>
    /// <returns>整数值</returns>
    private long ParameterAsLong(string id, long fallback = 0)
    {
        if (!_generatorParameters.TryGetValue(id, out var raw) || raw is null) return fallback;
        return raw switch
        {
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue => (long)doubleValue,
            float floatValue => (long)floatValue,
            _ => fallback,
        };
    }

    /// <summary>
    /// 读取参数表中的浮点值，缺失或类型不符时回退默认值
    /// </summary>
    /// <param name="id">参数标识</param>
    /// <param name="fallback">缺省值</param>
    /// <returns>浮点值</returns>
    private double ParameterAsDouble(string id, double fallback = 0D)
    {
        if (!_generatorParameters.TryGetValue(id, out var raw) || raw is null) return fallback;
        return raw switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            long longValue => longValue,
            int intValue => intValue,
            _ => fallback,
        };
    }

    /// <summary>
    /// 绘制底部状态消息
    /// </summary>
    private void DrawGeneratorMessage()
    {
        if (string.IsNullOrEmpty(_generatorMessage)) return;
        var color = _generatorMessageError
            ? new Vector4(1F, 0.45F, 0.42F, 1F)
            : new Vector4(0.45F, 0.85F, 0.5F, 1F);
        ImGui.PushTextWrapPos();
        ImGui.TextColored(color, _generatorMessage);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// 保存当前源码到资产文件并刷新声明
    /// </summary>
    private void SaveGeneratorSource()
    {
        if (_generatorAsset is null || _generatorRelativePath is null)
        {
            SetStatus("没有可保存的生成器");
            return;
        }

        var absolutePath = ResolveGeneratorAssetPath(_generatorRelativePath);
        if (absolutePath is null)
        {
            SetStatus("生成器资产路径无效，已取消保存");
            return;
        }

        try
        {
            _generatorAsset.Source = _generatorSource;
            _generatorAsset.Save(absolutePath);
            _generatorDirty = false;
            DescribeGeneratorForUi(_generatorAsset);
            HandleAssetChanged(new ProjectAssetChange(_generatorRelativePath, _generatorRelativePath));
            SetStatus($"生成器源码已保存：{Path.GetFileName(absolutePath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _generatorMessage = $"保存生成器失败：{exception.Message}";
            _generatorMessageError = true;
            SetStatus(_generatorMessage);
        }
    }

    /// <summary>
    /// 带当前参数运行生成器，结果作为单个可撤销事务应用到当前世界
    /// </summary>
    private void RunGenerator()
    {
        if (_generatorAsset is null || _generatorDefinition is null ||
            !string.Equals(_generatorSource, _generatorDescribedSource, StringComparison.Ordinal))
        {
            _generatorMessage = "请先打开并修正生成器声明再运行";
            _generatorMessageError = true;
            return;
        }

        try
        {
            var asset = new GeneratorAsset
            {
                Language = _generatorAsset.Language,
                Source = _generatorSource,
            };
            var targetId = ResolveVoxelEditTarget(null);
            if (targetId is null) { SetStatus("请先选择体素模型"); return; }
            var targetWorld = _objectVoxels.GetOrCreate(targetId.Value);
            var staged = GeneratorRunner.Run(asset, targetWorld, _generatorParameters, TimeSpan.FromSeconds(10));
            var command = new VoxelStrokeCommand(
                $"运行生成器：{_generatorDefinition.Name}", targetWorld, staged, ObjectEditCallback(targetId.Value));
            if (command.ChangedCellCount == 0)
            {
                _generatorMessage = "生成器运行完成，但没有产生任何变化";
                _generatorMessageError = false;
                SetStatus(_generatorMessage);
                return;
            }

            _voxelHistory.Execute(command);
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
            _generatorMessage = $"已应用 {command.ChangedCellCount} 个体素，可 Ctrl+Z 整体撤销";
            _generatorMessageError = false;
            SetStatus(_generatorMessage);
        }
        catch (Exception exception)
        {
            _generatorMessage = $"生成器运行失败：{exception.Message}";
            _generatorMessageError = true;
            SetStatus(_generatorMessage);
        }
    }
}
