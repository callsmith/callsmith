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
    /// <param name="environmentCacheNamespace">
    /// Stable string that namespaces cache entries for this environment context.
    /// Pass <c>environmentId.ToString("N")</c> for a concrete environment; for the global
    /// environment scoped to a specific active environment, pass a compound string such as
    /// <c>$"{globalEnvId:N}[env:{activeEnvId:N}]"</c>.
    /// </param>
    /// <param name="variables">All variables in the active environment.</param>
    /// <param name="staticVariables">
    /// Already-resolved static variables used when substituting into linked requests.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResolvedEnvironment> ResolveAsync(
        string collectionFolderPath,
        string environmentCacheNamespace,
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

    /// <summary>
    /// Updates the dynamic variable cache for all response-body variables in
    /// <paramref name="variables"/> that reference <paramref name="requestName"/>,
    /// extracting values from <paramref name="responseBody"/> using each variable's
    /// configured JSONPath. Called after a request is manually run so that subsequent
    /// variable resolutions use the fresh value without re-executing the request.
    /// </summary>
    /// <param name="collectionFolderPath">Root folder of the collection.</param>
    /// <param name="environmentCacheNamespace">
    /// Cache namespace for the environment context; must match the namespace used by
    /// <see cref="ResolveAsync"/> for the same environment.
    /// </param>
    /// <param name="requestId">
    /// Stable identifier of the request that was just executed.
    /// Used as the per-request segment of the cache key.
    /// </param>
    /// <param name="requestName">Name of the request that was just executed (used to match variables).</param>
    /// <param name="responseBody">The raw response body from the completed request.</param>
    /// <param name="variables">Environment variables to inspect for references to the request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateCacheFromResponseAsync(
        string collectionFolderPath,
        string environmentCacheNamespace,
        Guid requestId,
        string requestName,
        string responseBody,
        IReadOnlyList<EnvironmentVariable> variables,
        CancellationToken ct = default);
}
