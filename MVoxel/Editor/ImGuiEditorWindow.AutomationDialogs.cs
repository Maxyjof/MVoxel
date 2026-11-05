using System.Text.Json;

namespace MVoxel.Editor;

/// <summary>
/// 确认框的自动化入口：退出、关闭项目、切换项目、时间轴保存与撤销警告都能被读取和回答，
/// 回答后走与界面按钮完全相同的代码路径
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>读取当前待处理的确认框；没有时 visible 为假</summary>
    private object DescribePendingDialog()
    {
        if ((_afterTimelineConfirmation is not null || _timelineCloseConfirmation) && !_timelineConfirmationResolved)
            return new
            {
                visible = true,
                kind = "timeline",
                title = "保存时间轴修改？",
                message = "时间轴有未保存的修改，继续前是否保存？",
                actions = new[] { "save", "saveAs", "discard", "cancel" },
                requiresPath = string.IsNullOrWhiteSpace(_timelineAssetPath),
                assetPath = string.IsNullOrWhiteSpace(_timelineAssetPath) ? null : _timelineAssetPath,
                temporary = string.IsNullOrWhiteSpace(_timelineAssetPath),
                dirty = TimelineIsDirty,
            };
        if (_projectCloseDialogOpen)
            return new
            {
                visible = true,
                kind = _afterProjectClose is null ? "closeProject" : "switchProject",
                title = "关闭项目",
                message = "当前项目有未保存的场景或生成器源码。请选择如何处理这些修改。",
                actions = new[] { "save", "discard", "cancel" },
                projectName = _project?.Manifest.Name,
                sceneDirty = _sceneDocumentDirty,
                generatorDirty = _generatorDirty,
            };
        if (_exitDialogOpen)
            return new
            {
                visible = true,
                kind = "exit",
                title = "退出编辑器",
                message = "当前项目有未保存的场景或生成器源码。退出前要保存吗？",
                actions = new[] { "save", "discard", "cancel" },
                sceneDirty = _sceneDocumentDirty,
                generatorDirty = _generatorDirty,
            };
        if (_historyConfirmation is { } history)
            return new
            {
                visible = true,
                kind = "history",
                title = "确认撤销或重做",
                message = $"即将{(history.Forward ? "重做" : "撤销")}：{history.Command.Name}",
                actions = new[] { "confirm", "cancel" },
                warning = history.Warning,
            };
        return new
        {
            visible = false,
            kind = (string?)null,
            title = (string?)null,
            message = (string?)null,
            actions = Array.Empty<string>(),
        };
    }

    /// <summary>回答当前确认框，动作名与界面按钮一一对应</summary>
    private object AnswerDialog(JsonElement arguments) => RunOnUi(() =>
    {
        RequireAutomationArguments(arguments, "确认框", "action", "path", "overwrite");
        var rawAction = ReadString(arguments, "action");
        if (string.IsNullOrWhiteSpace(rawAction)) throw new ArgumentException("缺少 action");
        var action = rawAction.Trim().ToLowerInvariant();
        var path = ReadString(arguments, "path");
        var overwrite = ReadBoolean(arguments, "overwrite") ?? false;
        if ((_afterTimelineConfirmation is not null || _timelineCloseConfirmation) && !_timelineConfirmationResolved)
        {
            switch (action)
            {
                case "save":
                    if (string.IsNullOrWhiteSpace(_timelineAssetPath))
                        throw new InvalidOperationException("当前是临时时间轴，请改用 saveAs 并传 path");
                    if (!TryConfirmTimelineSave()) throw new InvalidOperationException("时间轴保存未完成");
                    break;
                case "saveas":
                    if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("saveAs 需要 path");
                    if (!TryConfirmTimelineSave(path: path, overwrite: overwrite)) throw new InvalidOperationException("时间轴保存未完成");
                    break;
                case "discard":
                    ConfirmTimelineDiscard();
                    break;
                case "cancel":
                    CancelTimelineConfirmation();
                    break;
                default:
                    throw new ArgumentException($"时间轴确认框不支持操作：{action}");
            }
        }
        else if (_projectCloseDialogOpen)
        {
            switch (action)
            {
                case "save": ConfirmCloseProjectSave(); break;
                case "discard": ConfirmCloseProjectDiscard(); break;
                case "cancel": CancelCloseProjectConfirmation(); break;
                default: throw new ArgumentException($"项目确认框不支持操作：{action}");
            }
        }
        else if (_exitDialogOpen)
        {
            switch (action)
            {
                case "save": ConfirmExitSave(); break;
                case "discard": ConfirmExitDiscard(); break;
                case "cancel": CancelExitConfirmation(); break;
                default: throw new ArgumentException($"退出确认框不支持操作：{action}");
            }
        }
        else if (_historyConfirmation is { } history)
        {
            switch (action)
            {
                case "confirm":
                    _historyConfirmation = null;
                    _historyConfirmationResolved = true;
                    _historyRepeatDirection = 0;
                    _undoHeldSeconds = _undoRepeatAccumulator = 0F;
                    ExecuteHistory(history.Command, history.Forward);
                    break;
                case "cancel":
                    _historyConfirmation = null;
                    _historyConfirmationResolved = true;
                    _historyRepeatDirection = 0;
                    _undoHeldSeconds = _undoRepeatAccumulator = 0F;
                    break;
                default:
                    throw new ArgumentException($"撤销确认框不支持操作：{action}");
            }
        }
        else throw new InvalidOperationException("当前没有待处理的确认框");
        return new { accepted = true, action, pendingDialog = DescribePendingDialog() };
    });

    /// <summary>请求退出编辑器；save 先保存，discard 直接退出，都不传时按界面流程弹确认框</summary>
    private object QueueExitEditor(JsonElement arguments) => RunOnUi(() =>
    {
        RequireAutomationArguments(arguments, "退出编辑器", "save", "discard", "timelinePath", "overwrite");
        var save = ReadBoolean(arguments, "save") ?? false;
        var discard = ReadBoolean(arguments, "discard") ?? false;
        if (save && discard) throw new ArgumentException("save 与 discard 不能同时为真");
        if (discard)
        {
            _allowWindowClose = true;
            DismissExitDialog();
            _pendingActions.Enqueue(Close);
            return new { queued = true, closing = true };
        }
        if (save)
        {
            if (TimelineIsDirty)
            {
                var timelinePath = ReadString(arguments, "timelinePath");
                if (string.IsNullOrWhiteSpace(timelinePath) && string.IsNullOrWhiteSpace(_timelineAssetPath))
                    return new
                    {
                        queued = false, closing = false, requiresSave = true,
                        message = "当前是临时时间轴，请传 timelinePath 指定保存位置",
                        pendingDialog = DescribePendingDialog(),
                    };
                if (!TryConfirmTimelineSave(path: timelinePath, overwrite: ReadBoolean(arguments, "overwrite") ?? false))
                    return new { queued = false, closing = false, requiresSave = true, message = "时间轴保存未完成", pendingDialog = DescribePendingDialog() };
            }
            SaveProject();
            if (_generatorDirty) SaveGeneratorSource();
            if (_sceneDocumentDirty || _generatorDirty)
                return new { queued = false, closing = false, requiresSave = true, message = "项目保存未完成", pendingDialog = DescribePendingDialog() };
            _allowWindowClose = true;
            DismissExitDialog();
            _pendingActions.Enqueue(Close);
            return new { queued = true, closing = true };
        }
        //不指定处理方式时按界面流程走，确认框在下一帧弹出，先把响应返回给调用方
        _pendingActions.Enqueue(RequestExit);
        return new { queued = true, closing = false, pendingDialog = DescribePendingDialog() };
    });
}
