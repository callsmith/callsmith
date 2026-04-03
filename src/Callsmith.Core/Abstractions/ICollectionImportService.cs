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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ImportedCollection"/> that was written to disk.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no importer can handle the file format.
    /// </exception>
    Task<ImportedCollection> ImportToFolderAsync(
        string filePath,
        string targetFolderPath,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads a spec from <paramref name="specUrl"/>, saves it to a temporary file,
    /// then runs the full import pipeline into <paramref name="targetFolderPath"/>.
    /// </summary>
    /// <param name="specUrl">Publicly accessible URL of the OpenAPI / Swagger spec.</param>
    /// <param name="targetFolderPath">
    /// Absolute path to the destination folder. The folder is created if it does not exist.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ImportedCollection"/> that was written to disk.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the URL cannot be fetched or no importer can handle the downloaded content.
    /// </exception>
    Task<ImportedCollection> ImportFromUrlToFolderAsync(
        string specUrl,
        string targetFolderPath,
        CancellationToken ct = default);
}
