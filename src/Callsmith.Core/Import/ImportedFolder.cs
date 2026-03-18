namespace Callsmith.Core.Import;

/// <summary>
/// A folder (sub-collection) extracted from an external collection format
/// (Insomnia, Postman, etc.) during import. Format-agnostic.
/// </summary>
public sealed class ImportedFolder
{
    /// <summary>Display name of the folder.</summary>
    public required string Name { get; init; }

    /// <summary>Requests stored directly in this folder (not in sub-folders).</summary>
    public IReadOnlyList<ImportedRequest> Requests { get; init; } = [];

    /// <summary>Immediate sub-folders of this folder.</summary>
    public IReadOnlyList<ImportedFolder> SubFolders { get; init; } = [];

    /// <summary>
    /// Interleaved display order of direct children from the source tool.
    /// Each entry is a request name or a sub-folder name (no file extensions).
    /// Empty means no ordering information is available — use default ordering.
    /// </summary>
    public IReadOnlyList<string> ItemOrder { get; init; } = [];
}
