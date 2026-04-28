using Callsmith.Core.Abstractions;

namespace Callsmith.Core.Services;

/// <summary>
/// In-memory, session-scoped implementation of <see cref="IUndoRedoService"/>.
/// Thread-safety note: all mutations are expected to occur on the UI thread
/// (via Avalonia's <c>DispatcherTimer</c>), so no locking is required.
/// </summary>
public sealed class UndoRedoService : IUndoRedoService
{
    /// <summary>Maximum number of entries retained in the undo stack.</summary>
    private const int MaxDepth = 200;

    private readonly LinkedList<IUndoableAction> _undoStack = new();
    private readonly Stack<IUndoableAction> _redoStack = new();

    /// <inheritdoc/>
    public bool CanUndo => _undoStack.Count > 0;

    /// <inheritdoc/>
    public bool CanRedo => _redoStack.Count > 0;

    /// <inheritdoc/>
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack.Last!.Value.Description : null;

    /// <inheritdoc/>
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    /// <inheritdoc/>
    public event EventHandler? StackChanged;

    /// <inheritdoc/>
    public void Push(IUndoableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _redoStack.Clear();
        _undoStack.AddLast(action);

        // Drop the oldest entry when the depth limit is exceeded.
        if (_undoStack.Count > MaxDepth)
            _undoStack.RemoveFirst();

        StackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public IUndoableAction? Undo()
    {
        if (_undoStack.Count == 0)
            return null;

        var action = _undoStack.Last!.Value;
        _undoStack.RemoveLast();
        _redoStack.Push(action);

        StackChanged?.Invoke(this, EventArgs.Empty);
        return action;
    }

    /// <inheritdoc/>
    public IUndoableAction? Redo()
    {
        if (_redoStack.Count == 0)
            return null;

        var action = _redoStack.Pop();
        _undoStack.AddLast(action);

        StackChanged?.Invoke(this, EventArgs.Empty);
        return action;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StackChanged?.Invoke(this, EventArgs.Empty);
    }
}
