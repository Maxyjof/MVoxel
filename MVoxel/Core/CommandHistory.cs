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
    /// 撤销该命令，必须把状态恢复到 Execute 之前
    /// </summary>
    void Undo();
}

/// <summary>
/// 小型确定性的撤销重做历史，供编辑器工具和插件共用
/// 只管理命令对象本身，不关心数据如何变更
/// </summary>
public sealed class CommandHistory
{
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
    /// 立即执行命令并压入撤销栈，同时清空重做栈（新分支丢弃旧未来）
    /// </summary>
    /// <param name="command">要执行的命令</param>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
    }

    /// <summary>
    /// 撤销最近一条命令
    /// </summary>
    /// <returns>栈为空时返回 false</returns>
    public bool Undo()
    {
        if (!_undo.TryPop(out var command))
        {
            return false;
        }

        command.Undo();
        _redo.Push(command);
        return true;
    }

    /// <summary>
    /// 重做最近撤销的命令
    /// </summary>
    /// <returns>没有可重做命令时返回 false</returns>
    public bool Redo()
    {
        if (!_redo.TryPop(out var command))
        {
            return false;
        }

        command.Execute();
        _undo.Push(command);
        return true;
    }

    /// <summary>
    /// 清空全部历史（例如关闭项目时），不会撤销任何已生效的修改
    /// </summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
