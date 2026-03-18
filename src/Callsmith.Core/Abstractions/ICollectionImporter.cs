using Callsmith.Core.Import;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Parses a single external collection file format (e.g. Insomnia, Postman)
/// into the Callsmith <see cref="ImportedCollection"/> domain model.
/// </summary>
/// <remarks>
/// Implement this interface to add support for a new collection format.
/// Register all implementations with the DI container so that
/// <see cref="ICollectionImportService"/> can discover them.
/// </remarks>
public interface ICollectionImporter
{
    /// <summary>Human-readable name of the supported format (e.g. "Insomnia", "Postman").</summary>
    string FormatName { get; }

    /// <summary>
    /// File extensions (including the leading dot) that this importer handles,
    /// e.g. <c>[".yaml", ".yml", ".json"]</c>.
    /// Used to filter the file picker dialog.
    /// </summary>
    IReadOnlyList<string> SupportedFileExtensions { get; }

    /// <summary>
    /// Performs a fast check — typically reads the first few lines or bytes of the file —
    /// to confirm that this importer is the correct one for the given file.
    /// Returns <c>false</c> when the file is unrecognised, does not exist, or cannot be read.
    /// Never throws.
    /// </summary>
    /// <param name="filePath">Absolute path to the candidate file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanImportAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Parses the file and returns the format-agnostic <see cref="ImportedCollection"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to the collection file to import.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file cannot be parsed as the expected format.
    /// </exception>
    Task<ImportedCollection> ImportAsync(string filePath, CancellationToken ct = default);
}
