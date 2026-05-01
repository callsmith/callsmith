namespace Callsmith.Core.Abstractions;

/// <summary>
/// A reversible change captured as a before/after snapshot pair.
/// The Desktop dispatch layer inspects <see cref="ContextType"/> to route undo/redo
/// to the correct ViewModel, then casts to the concrete action type to obtain the snapshots.
/// </summary>
public interface IUndoableAction
{
    /// <summary>
    /// Discriminator that identifies the kind of change.
    /// Known values: <c>"request"</c> and <c>"environment"</c>.
    /// </summary>
    string ContextType { get; }

    /// <summary>Human-readable label suitable for a future undo-history tooltip.</summary>
    string Description { get; }
}
