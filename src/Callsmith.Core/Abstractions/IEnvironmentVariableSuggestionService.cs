using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Builds merged environment-variable suggestions across multiple variable layers.
/// Later layers override earlier layers when names collide.
/// </summary>
public interface IEnvironmentVariableSuggestionService
{
    /// <summary>
    /// Merges <paramref name="layers"/> (lowest-priority first), deduplicates by name,
    /// masks secret values, and returns suggestions sorted by name.
    /// </summary>
    IReadOnlyList<EnvironmentVariableSuggestion> Build(params IEnumerable<EnvironmentVariable>?[] layers);
}
