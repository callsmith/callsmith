using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Resolves dynamic environment variables by executing their linked requests
/// and extracting values from the responses according to the configured rules.
/// </summary>
public interface IDynamicVariableEvaluator
{
    /// <summary>
    /// For each variable in <paramref name="variables"/> that has dynamic segments,
    /// evaluates those segments (executing HTTP requests as needed) and returns a
    /// resolved name→value map that merges the static and dynamic results.
    /// </summary>
    /// <param name="collectionFolderPath">Root folder of the collection (used to locate requests).</param>
    /// <param name="environmentFilePath">
    /// Absolute path of the active environment file (used as the cache key namespace).
    /// </param>
    /// <param name="variables">All variables in the active environment.</param>
    /// <param name="resolvedStaticVariables">
    /// Already-resolved static variables to use when substituting into the dynamic request.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary of all variable names and their resolved values, including both
    /// static passthroughs and evaluated dynamic values.
    /// </returns>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string collectionFolderPath,
        string environmentFilePath,
        IReadOnlyList<EnvironmentVariable> variables,
        IReadOnlyDictionary<string, string> resolvedStaticVariables,
        CancellationToken ct = default);

    /// <summary>
    /// Immediately executes the request described by <paramref name="segment"/> and returns
    /// the extracted value. Used by the environment editor to preview a dynamic segment's
    /// output without affecting the variable cache.
    /// </summary>
    Task<string?> PreviewAsync(
        string collectionFolderPath,
        DynamicValueSegment segment,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct = default);
}
