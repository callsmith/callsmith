using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Resolves dynamic environment variables (response-body and mock-data types) for
/// a send-time pipeline or environment editor preview.
/// </summary>
public interface IDynamicVariableEvaluator
{
    /// <summary>
    /// Evaluates all dynamic variables in <paramref name="variables"/> and returns a
    /// <see cref="ResolvedEnvironment"/> containing:
    /// <list type="bullet">
    ///   <item>Pre-computed string values for static and response-body variables.</item>
    ///   <item>Mock-data generator entries for mock-data variables (evaluated lazily per
    ///     token reference to ensure per-occurrence freshness).</item>
    /// </list>
    /// </summary>
    /// <param name="collectionFolderPath">Root folder of the collection (used to locate requests).</param>
    /// <param name="environmentFilePath">
    /// Absolute path of the active environment file (used as the cache key namespace).
    /// </param>
    /// <param name="variables">All variables in the active environment.</param>
    /// <param name="staticVariables">
    /// Already-resolved static variables used when substituting into linked requests.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResolvedEnvironment> ResolveAsync(
        string collectionFolderPath,
        string environmentFilePath,
        IReadOnlyList<EnvironmentVariable> variables,
        IReadOnlyDictionary<string, string> staticVariables,
        CancellationToken ct = default);

    /// <summary>
    /// Immediately executes the request described by <paramref name="variable"/> (which must be
    /// a <see cref="EnvironmentVariable.VariableTypes.ResponseBody"/> variable) and returns the
    /// extracted value. Used by the environment editor to preview without affecting the cache.
    /// </summary>
    Task<string?> PreviewResponseBodyAsync(
        string collectionFolderPath,
        EnvironmentVariable variable,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct = default);
}
