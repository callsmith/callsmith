namespace Callsmith.Core.Models;

/// <summary>
/// Per-Bruno-collection metadata stored in the user's app-data directory rather than
/// inside the Bruno collection repository. Keeps Callsmith-specific UI concerns —
/// environment ordering, colors, and the global environment — out of the Bruno files
/// shared with Bruno desktop users.
/// </summary>
public sealed class BrunoCollectionMeta
{
    /// <summary>
    /// Ordered list of environment file names (e.g. <c>Dev.bru</c>, <c>Prod.bru</c>)
    /// representing the user's preferred display order. Empty means alphabetical.
    /// </summary>
    public IReadOnlyList<string> EnvironmentOrder { get; init; } = [];

    /// <summary>
    /// Maps environment file names (e.g. <c>Dev.bru</c>) to hex color strings
    /// (e.g. <c>#27ae60</c>). Missing entries mean no color is shown for that environment.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentColors { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Maps environment file names (e.g. <c>Dev.bru</c>) to their stable unique identifiers.
    /// Missing entries mean the environment was created before this field was introduced.
    /// </summary>
    public IReadOnlyDictionary<string, Guid> EnvironmentIds { get; init; }
        = new Dictionary<string, Guid>();

    /// <summary>The stable unique identifier for the collection's global environment.</summary>
    public Guid? GlobalEnvironmentId { get; init; }

    /// <summary>Non-secret variables in the collection's global environment.</summary>
    public IReadOnlyList<GlobalVarEntry> GlobalVariables { get; init; } = [];

    /// <summary>
    /// Names of secret variables in the global environment. Actual values are stored
    /// in <see cref="ISecretStorageService"/> and never written to this file.
    /// </summary>
    public IReadOnlyList<string> GlobalSecretVariableNames { get; init; } = [];

    /// <summary>A single non-secret global variable entry.</summary>
    public sealed class GlobalVarEntry
    {
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }
}
