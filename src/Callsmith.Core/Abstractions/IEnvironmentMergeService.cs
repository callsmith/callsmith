using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Performs the canonical three-layer environment variable merge used at request
/// send time. Both the send pipeline and the environment editor preview delegate
/// to this service so that any change to the merge algorithm — precedence rules,
/// dynamic resolution order, force-override logic — is reflected in both code paths
/// automatically.
/// </summary>
public interface IEnvironmentMergeService
{
    /// <summary>
    /// Performs the static three-layer merge without evaluating any dynamic variables:
    /// global variables form the baseline, active-environment variables override them,
    /// and force-override global variables win last.
    /// </summary>
    /// <param name="globalEnv">The collection's global environment.</param>
    /// <param name="activeEnv">
    /// The active (or preview) environment, or <see langword="null"/> when there is none.
    /// </param>
    /// <returns>A mutable dictionary containing the merged name → value pairs.</returns>
    Dictionary<string, string> BuildStaticMerge(EnvironmentModel globalEnv, EnvironmentModel? activeEnv);

    /// <summary>
    /// Performs the full merge including dynamic variable resolution, using the same
    /// two-phase algorithm as the request send pipeline:
    /// <list type="number">
    ///   <item>Static three-layer baseline (global → active → force-override-global).</item>
    ///   <item>Phase 1 — resolve global dynamic variables using the merged static context.</item>
    ///   <item>Re-apply active-environment static variables (they win over resolved globals).</item>
    ///   <item>Phase 2 — resolve active-environment dynamic variables.</item>
    ///   <item>Re-apply force-override global variables last (highest final priority).</item>
    /// </list>
    /// Falls back to the static merge result when dynamic evaluation fails.
    /// </summary>
    /// <param name="collectionFolderPath">Root folder of the collection (for dynamic evaluation).</param>
    /// <param name="globalEnv">The collection's global environment.</param>
    /// <param name="activeEnv">
    /// The active (or preview) environment, or <see langword="null"/> when there is none.
    /// </param>
    /// <param name="allowStaleCache">
    /// When <see langword="true"/>, any existing cache entry is returned immediately regardless
    /// of its age or the variable's <see cref="DynamicFrequency"/> setting. An HTTP request is
    /// only made when no cache entry exists at all. Pass <see langword="true"/> for editor preview
    /// calls; use <see langword="false"/> (the default) for the request send pipeline.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResolvedEnvironment> MergeAsync(
        string collectionFolderPath,
        EnvironmentModel globalEnv,
        EnvironmentModel? activeEnv,
        bool allowStaleCache = false,
        CancellationToken ct = default);
}
