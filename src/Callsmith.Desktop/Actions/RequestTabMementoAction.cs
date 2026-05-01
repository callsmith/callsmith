using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;

namespace Callsmith.Desktop.Actions;

/// <summary>
/// Captures a reversible edit to a request tab as a before/after <see cref="CollectionRequest"/> pair.
/// </summary>
public sealed class RequestTabMementoAction : IUndoableAction
{
    /// <inheritdoc/>
    public string ContextType => "request";

    /// <inheritdoc/>
    public required string Description { get; init; }

    /// <summary>
    /// Stable identity of the in-session tab instance.
    /// Used as a hint to locate the open tab without a file-path comparison.
    /// </summary>
    public required Guid TabId { get; init; }

    /// <summary>
    /// Absolute path of the backing <c>.callsmith</c> file.
    /// Used to re-open the tab when it is no longer present in the tab list.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>Full request state snapshot captured before the edit.</summary>
    public required CollectionRequest Before { get; init; }

    /// <summary>Full request state snapshot captured after the edit.</summary>
    public required CollectionRequest After { get; init; }
}
