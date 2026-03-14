namespace Callsmith.Core.Models;

/// <summary>
/// Represents a folder (or sub-folder) within a collection on disk.
/// The root <see cref="CollectionFolder"/> is the collection itself.
/// </summary>
public sealed class CollectionFolder
{
    /// <summary>Absolute path of this folder on disk.</summary>
    public required string FolderPath { get; init; }

    /// <summary>Display name (the folder's directory name).</summary>
    public required string Name { get; init; }

    /// <summary>Requests stored directly in this folder (not in sub-folders).</summary>
    public IReadOnlyList<CollectionRequest> Requests { get; init; } = [];

    /// <summary>Immediate sub-folders of this folder.</summary>
    public IReadOnlyList<CollectionFolder> SubFolders { get; init; } = [];

    /// <summary>
    /// Ordered item entry names from the folder's <c>_order.json</c> file.
    /// Filenames (with extension) for requests; directory names for sub-folders.
    /// Empty = use default ordering (sub-folders first, then requests, both alphabetical).
    /// </summary>
    public IReadOnlyList<string> ItemOrder { get; init; } = [];
}
