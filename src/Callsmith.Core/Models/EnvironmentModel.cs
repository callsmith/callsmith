namespace Callsmith.Core.Models;

/// <summary>
/// An environment is a named set of variables that can be applied to any request at send time.
/// Environments are stored as JSON files in the <c>environment/</c> sub-folder of a collection.
/// </summary>
public sealed record EnvironmentModel
{
    /// <summary>The file path where this environment is stored on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Display name of the environment (e.g. "dev", "staging", "production").</summary>
    public required string Name { get; init; }

    /// <summary>The ordered list of variables in this environment.</summary>
    public IReadOnlyList<EnvironmentVariable> Variables { get; init; } = [];

    /// <summary>
    /// Optional display color for this environment (hex format, e.g. "#4ec9b0").
    /// Null means no color indicator is shown.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Name of the concrete environment to use as the preview context when evaluating global
    /// dynamic variables in the environment editor. Only meaningful for the global environment.
    /// Stored in the global <c>.env.callsmith</c> file so the selection persists across sessions.
    /// </summary>
    public string? GlobalPreviewEnvironmentName { get; init; }
}
