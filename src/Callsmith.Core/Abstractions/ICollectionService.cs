using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Manages request collections stored as plain files on the filesystem.
/// A collection is a folder; each request is a <c>.callsmith</c> file within it.
/// Sub-folders are sub-collections.
/// </summary>
public interface ICollectionService
{
    /// <summary>File extension used for all request files (e.g. <c>.callsmith</c>).</summary>
    string RequestFileExtension { get; }

    /// <summary>
    /// Opens a folder on disk as a collection and returns the full folder tree,
    /// including all requests and sub-folders discovered recursively.
    /// </summary>
    /// <param name="folderPath">Absolute path to the root collection folder.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CollectionFolder> OpenFolderAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Loads a single request from disk by its file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.callsmith</c> request file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CollectionRequest> LoadRequestAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Saves a request to disk. If the file already exists it is overwritten.
    /// The directory is created if it does not exist.
    /// </summary>
    /// <param name="request">The request to save. <see cref="CollectionRequest.FilePath"/> determines the destination.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a request file from disk.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.callsmith</c> request file to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteRequestAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Renames a request file on disk, keeping it in the same folder.
    /// Returns the updated <see cref="CollectionRequest"/> with the new file path and name.
    /// </summary>
    /// <param name="filePath">Absolute path to the existing <c>.callsmith</c> file.</param>
    /// <param name="newName">The new display name (without extension).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CollectionRequest> RenameRequestAsync(string filePath, string newName, CancellationToken ct = default);

    /// <summary>
    /// Creates a new request file with default content in the specified folder.
    /// Returns the newly-created request.
    /// </summary>
    /// <param name="folderPath">Absolute path to the folder that will contain the new file.</param>
    /// <param name="name">Display name for the new request (used as the filename without extension).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CollectionRequest> CreateRequestAsync(string folderPath, string name, CancellationToken ct = default);

    /// <summary>
    /// Creates a new sub-folder inside the specified parent folder.
    /// Returns an empty <see cref="CollectionFolder"/> representing the new folder.
    /// </summary>
    /// <param name="parentPath">Absolute path to the parent folder.</param>
    /// <param name="name">Name for the new sub-folder.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CollectionFolder> CreateFolderAsync(string parentPath, string name, CancellationToken ct = default);

    /// <summary>
    /// Renames a folder on disk. Returns an updated (empty) <see cref="CollectionFolder"/>
    /// with the new path and name. Callers should reload the folder's contents separately.
    /// </summary>
    Task<CollectionFolder> RenameFolderAsync(string folderPath, string newName, CancellationToken ct = default);

    /// <summary>
    /// Deletes a folder and all of its contents from disk.
    /// </summary>
    Task DeleteFolderAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Persists the display order for items inside a folder by writing a <c>_order.json</c> file.
    /// <paramref name="orderedNames"/> should list every item's entry name in the desired display order:
    /// filenames (including <c>.callsmith</c> extension) for requests, and directory names for sub-folders.
    /// Passing an empty list removes any existing order file, restoring default (alphabetical) ordering.
    /// </summary>
    Task SaveFolderOrderAsync(string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default);
}
