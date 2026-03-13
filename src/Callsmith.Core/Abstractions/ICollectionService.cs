using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Manages request collections stored as plain files on the filesystem.
/// A collection is a folder; each request is a <c>.callsmith</c> file within it.
/// Sub-folders are sub-collections.
/// </summary>
public interface ICollectionService
{
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
}
