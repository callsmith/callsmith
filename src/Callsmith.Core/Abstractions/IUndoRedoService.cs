namespace Callsmith.Core.Abstractions;

/// <summary>
/// Session-scoped undo/redo service.
/// Maintains two stacks (undo and redo) of <see cref="IUndoableAction"/> entries.
/// The service itself is state-only: it does not navigate or apply snapshots — the Desktop
/// dispatch layer (MainWindowViewModel) is responsible for those concerns.
/// </summary>
public interface IUndoRedoService
{
    /// <summary>True when there is at least one action available to undo.</summary>
    bool CanUndo { get; }

    /// <summary>True when there is at least one action available to redo.</summary>
    bool CanRedo { get; }

    /// <summary>Description of the next action to undo, or <see langword="null"/> when the stack is empty.</summary>
    string? UndoDescription { get; }

    /// <summary>Description of the next action to redo, or <see langword="null"/> when the stack is empty.</summary>
    string? RedoDescription { get; }

    /// <summary>
    /// Pushes <paramref name="action"/> onto the undo stack and clears the redo stack.
    /// When the undo stack exceeds the maximum depth (200 entries) the oldest entry is dropped.
    /// </summary>
    void Push(IUndoableAction action);

    /// <summary>
    /// Pops the top of the undo stack, pushes it onto the redo stack, and returns the action.
    /// Returns <see langword="null"/> (without throwing) when the undo stack is empty.
    /// </summary>
    IUndoableAction? Undo();

    /// <summary>
    /// Pops the top of the redo stack, pushes it onto the undo stack, and returns the action.
    /// Returns <see langword="null"/> (without throwing) when the redo stack is empty.
    /// </summary>
    IUndoableAction? Redo();

    /// <summary>
    /// Raised after every <see cref="Push"/>, <see cref="Undo"/>, <see cref="Redo"/>, and
    /// <see cref="Clear"/> call so that command CanExecute bindings can refresh.
    /// </summary>
    event EventHandler? StackChanged;

    /// <summary>
    /// Empties both the undo and redo stacks.
    /// Called when a new collection is opened to start a fresh session.
    /// </summary>
    void Clear();
}
