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
}
