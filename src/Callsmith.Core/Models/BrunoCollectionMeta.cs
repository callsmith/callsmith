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

    /// <summary>
    /// Name of the concrete environment selected as preview context for global dynamic
    /// variable previews in the environment editor.
    /// </summary>
    public string? GlobalPreviewEnvironmentName { get; init; }

    /// <summary>Non-secret variables in the collection's global environment.</summary>
    public IReadOnlyList<GlobalVarEntry> GlobalVariables { get; init; } = [];

    /// <summary>Secret variables in the collection's global environment with full metadata.</summary>
    public IReadOnlyList<GlobalSecretVarEntry> GlobalSecretVariables { get; init; } = [];

    /// <summary>A single non-secret global variable entry.</summary>
    public sealed class GlobalVarEntry
    {
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;

        /// <summary>
        /// Variable type — matches <see cref="EnvironmentVariable.VariableTypes"/> constants.
        /// Null is treated as <c>static</c> for backwards compatibility.
        /// </summary>
        public string? VariableType { get; init; }

        // Mock-data type fields
        public string? MockDataCategory { get; init; }
        public string? MockDataField { get; init; }

        // Response-body type fields
        public string? ResponseRequestName { get; init; }
        public string? ResponsePath { get; init; }
        public DynamicFrequency? ResponseFrequency { get; init; }
        public int? ResponseExpiresAfterSeconds { get; init; }

        /// <summary>
        /// When <see langword="true"/>, this global variable takes priority over a concrete
        /// environment variable with the same name. Null is treated as <see langword="false"/>.
        /// </summary>
        public bool? IsForceGlobalOverride { get; init; }
    }

    /// <summary>A single secret global variable entry (with full metadata for type information).</summary>
    public sealed class GlobalSecretVarEntry
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Variable type — matches <see cref="EnvironmentVariable.VariableTypes"/> constants.
        /// Null is treated as <c>static</c> for backwards compatibility.
        /// </summary>
        public string? VariableType { get; init; }

        // Mock-data type fields
        public string? MockDataCategory { get; init; }
        public string? MockDataField { get; init; }

        // Response-body type fields
        public string? ResponseRequestName { get; init; }
        public string? ResponsePath { get; init; }
        public DynamicFrequency? ResponseFrequency { get; init; }
        public int? ResponseExpiresAfterSeconds { get; init; }

        /// <summary>
        /// When <see langword="true"/>, this global variable takes priority over a concrete
        /// environment variable with the same name. Null is treated as <see langword="false"/>.
        /// </summary>
        public bool? IsForceGlobalOverride { get; init; }
    }
}
