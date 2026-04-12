namespace Callsmith.Core.Abstractions;

/// <summary>
/// Provides unique request/folder naming helpers for collection operations.
/// </summary>
public interface ICollectionNamingService
{
    /// <summary>
    /// Returns a unique request name in <paramref name="folderPath"/> for the given
    /// request file extension.
    /// </summary>
    Task<string> PickUniqueRequestNameAsync(
        string folderPath,
        string baseName,
        string requestFileExtension,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a unique folder name in <paramref name="parentPath"/>.
    /// </summary>
    Task<string> PickUniqueFolderNameAsync(
        string parentPath,
        string baseName,
        CancellationToken ct = default);
}
