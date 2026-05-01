using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;

namespace Callsmith.Desktop.Actions;

/// <summary>
/// Captures a reversible edit to an environment as a before/after <see cref="EnvironmentModel"/> pair.
/// </summary>
public sealed class EnvironmentMementoAction : IUndoableAction
{
    /// <inheritdoc/>
    public string ContextType => "environment";

    /// <inheritdoc/>
    public required string Description { get; init; }

    /// <summary>Stable unique identifier of the affected environment.</summary>
    public required Guid EnvironmentId { get; init; }

    /// <summary>
    /// Absolute path of the backing environment file.
    /// Used to navigate to the correct environment in the editor.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>Full environment state snapshot captured before the edit.</summary>
    public required EnvironmentModel Before { get; init; }

    /// <summary>Full environment state snapshot captured after the edit.</summary>
    public required EnvironmentModel After { get; init; }
}
