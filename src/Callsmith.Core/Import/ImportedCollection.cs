namespace Callsmith.Core.Import;

/// <summary>
/// The top-level result of parsing an external collection file
/// (Insomnia, Postman, etc.). Format-agnostic.
/// </summary>
public sealed class ImportedCollection
{
    /// <summary>Display name of the imported collection.</summary>
    public required string Name { get; init; }

    /// <summary>Requests located at the root of the collection (not inside any folder).</summary>
    public IReadOnlyList<ImportedRequest> RootRequests { get; init; } = [];

    /// <summary>Top-level folders in the collection.</summary>
    public IReadOnlyList<ImportedFolder> RootFolders { get; init; } = [];

    /// <summary>
    /// Interleaved display order of root-level items from the source tool.
    /// Each entry is a request name or a folder name (no file extensions).
    /// Empty means no ordering information is available — use default ordering.
    /// </summary>
    public IReadOnlyList<string> ItemOrder { get; init; } = [];

    /// <summary>
    /// Environments defined in the source collection.
    /// Empty when the source format does not support environments or none were defined.
    /// </summary>
    public IReadOnlyList<ImportedEnvironment> Environments { get; init; } = [];

    /// <summary>
    /// Dynamic variables extracted from inline request-field tokens during import
    /// (e.g. Insomnia <c>{% faker %}</c> or <c>{% response %}</c> tags found in URLs,
    /// headers, or bodies). These are written to the collection's global environment so
    /// that all requests reference them as <c>{{var-name}}</c> rather than embedding
    /// raw template tags inline.
    /// </summary>
    public IReadOnlyList<ImportedDynamicVariable> GlobalDynamicVars { get; init; } = [];
}
