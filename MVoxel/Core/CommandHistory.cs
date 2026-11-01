namespace MVoxel.Core;

/// <summary>
/// 编辑器命令的统一契约：每条命令必须自带精确的撤销能力
/// </summary>
public interface IEditorCommand
{
    /// <summary>
    /// 用于撤销栈与界面显示的命令名
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 执行（或重做）该命令
    /// </summary>
    void Execute();

    /// <summary>
    /// 撤销该命令，必须把状态恢复到执行前
    /// </summary>
    void Undo();
}

/// <summary>按撤销/重做方向说明可能损失数据的影响，由编辑器在执行前阻塞确认。</summary>
public interface IHistoryWarning
{
    string? GetWarning(bool forward);
}

/// <summary>
/// 小型确定性的撤销重做记录，供编辑器工具和插件共用
/// 只管理命令对象本身，不关心数据如何变更
/// </summary>
public sealed class CommandHistory
{
    public Func<IEditorCommand, bool>? RecordHandler { get; set; }
    public event Action? Cleared;
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    /// <summary>
    /// 可撤销的命令数量
    /// </summary>
    public int UndoCount => _undo.Count;

    /// <summary>
    /// 可重做的命令数量
    /// </summary>
    public int RedoCount => _redo.Count;

    /// <summary>
    /// 查看撤销栈顶命令，不弹出；供事务日志记录撤销方向使用
    /// </summary>
    /// <returns>栈顶命令，栈空时为空</returns>
    public IEditorCommand? PeekUndo() => _undo.Count > 0 ? _undo.Peek() : null;

    /// <summary>
    /// 查看重做栈顶命令，不弹出；供事务日志记录重做方向使用
    /// </summary>
    /// <returns>栈顶命令，栈空时为空</returns>
    public IEditorCommand? PeekRedo() => _redo.Count > 0 ? _redo.Peek() : null;

    /// <summary>
    /// 立即执行命令并压入撤销栈，同时清空重做栈
    /// </summary>
    /// <param name="command">要执行的命令</param>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        RecordExecuted(command);
    }

    /// <summary>
    /// 记录一条已经由界面完成修改的命令。用于属性编辑器这类即时控件，
    /// 它们在控件返回 changed 时已经写入目标对象，不能再次执行一次。
    /// </summary>
    public void RecordExecuted(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (RecordHandler?.Invoke(command) == true) return;
        _undo.Push(command);
        DisposeCommands(_redo);
    }

    /// <summary>
    /// 撤销最近一条命令
    /// </summary>
    /// <returns>栈为空时返回 false</returns>
    public bool Undo()
    {
        if (!_undo.TryPeek(out var command))
        {
            return false;
        }

        command.Undo();
        _undo.Pop();
        _redo.Push(command);
        return true;
    }

    /// <summary>
    /// 重做最近撤销的命令
    /// </summary>
    /// <returns>没有可重做命令时返回 false</returns>
    public bool Redo()
    {
        if (!_redo.TryPeek(out var command))
        {
            return false;
        }

        command.Execute();
        _redo.Pop();
        _undo.Push(command);
        return true;
    }

    /// <summary>
    /// 释放被丢弃命令持有的临时文件副本
    /// </summary>
    private static void DisposeCommands(Stack<IEditorCommand> commands)
    {
        while (commands.TryPop(out var command)) (command as IDisposable)?.Dispose();
    }

    /// <summary>清空当前编辑会话的历史，并通知事务宿主重建基线。</summary>
    public void Clear()
    {
        DisposeCommands(_undo);
        DisposeCommands(_redo);
        Cleared?.Invoke();
    }
}
