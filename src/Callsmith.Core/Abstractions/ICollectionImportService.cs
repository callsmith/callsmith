using Callsmith.Core.Import;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Orchestrates the complete import flow: discovers the right
/// <see cref="ICollectionImporter"/> for a file, parses it into an
/// <see cref="ImportedCollection"/>, then writes all requests, folders,
/// and environments to a target folder on disk using <see cref="ICollectionService"/>
/// and <see cref="IEnvironmentService"/>.
/// </summary>
public interface ICollectionImportService
{
    /// <summary>
    /// All file extensions (including the dot) supported across every registered importer.
    /// Suitable for building a file picker filter.
    /// </summary>
    IReadOnlyList<string> SupportedFileExtensions { get; }
    /// <summary>
    /// Returns the <see cref="ICollectionImporter"/> that can handle the given file,
    /// or <c>null</c> if no registered importer recognises the format.
    /// </summary>
    /// <param name="filePath">Absolute path to the file to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ICollectionImporter?> FindImporterAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Full import pipeline: detects the format, parses the file, and writes the
    /// resulting requests, folders, and environments under <paramref name="targetFolderPath"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to the source collection file.</param>
    /// <param name="targetFolderPath">
    /// Absolute path to the destination folder. The folder is created if it does not exist.
    /// </param>
    /// <param name="options">
    /// Options controlling import behaviour (e.g. the base URL variable name for
    /// OpenAPI/Swagger imports). Pass <c>null</c> to use <see cref="CollectionImportOptions.Default"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ImportedCollection"/> that was written to disk.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no importer can handle the file format.
    /// </exception>
    Task<ImportedCollection> ImportToFolderAsync(
        string filePath,
        string targetFolderPath,
        CollectionImportOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Merges an import file into an already-open collection.
    /// Requests are written to
    /// <paramref name="targetSubFolderPath"/> (or the collection root when
    /// <paramref name="targetSubFolderPath"/> is <c>null</c> or equal to
    /// <paramref name="collectionRootPath"/>).
    /// Environments are merged by name: existing variables are never changed or
    /// removed; new variables from the import file are appended; entirely new
    /// environments are added as additional environments.
    /// </summary>
    /// <param name="filePath">Absolute path to the source collection file.</param>
    /// <param name="collectionRootPath">
    /// Absolute path to the root of the already-open collection. Used to locate
    /// existing environments and global dynamic variables.
    /// </param>
    /// <param name="targetSubFolderPath">
    /// Absolute path of the sub-folder inside the collection where requests will be
    /// placed. When <c>null</c> or equal to <paramref name="collectionRootPath"/>,
    /// requests land at the collection root.
    /// </param>
    /// <param name="options">
    /// Options controlling import behaviour (merge strategy, base URL variable name, …).
    /// Pass <c>null</c> to use <see cref="CollectionImportOptions.Default"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ImportedCollection"/> that was merged into the collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no importer can handle the file format.
    /// </exception>
    Task<ImportedCollection> ImportIntoCollectionAsync(
        string filePath,
        string collectionRootPath,
        string? targetSubFolderPath = null,
        CollectionImportOptions? options = null,
        CancellationToken ct = default);
}
