namespace MVoxel.Core;

public interface IEditorCommand
{
    string Name { get; }
    void Execute();
    void Undo();
}

/// <summary>Small, deterministic undo/redo core used by editor tools and plugins.</summary>
public sealed class CommandHistory
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
    }

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

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
