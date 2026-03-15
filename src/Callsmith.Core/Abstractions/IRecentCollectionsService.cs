namespace Callsmith.Core.Abstractions;

/// <summary>
/// Persists a list of recently-opened collection folder paths.
/// </summary>
public interface IRecentCollectionsService
{
    /// <summary>
    /// Loads the list of recent collection paths from disk, filtering out any paths
    /// that no longer exist on the file system.
    /// </summary>
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Prepends <paramref name="folderPath"/> to the list (de-duplicating case-insensitively),
    /// trims the list to the maximum number of entries, then persists to disk.
    /// </summary>
    Task PushAsync(string folderPath, CancellationToken ct = default);
}
