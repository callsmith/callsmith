using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Reads and writes per-collection user preferences (e.g. last active environment).
/// Preferences are stored in the user's application data directory and are never
/// written into the collection folder, so they do not appear in version control.
/// </summary>
public interface ICollectionPreferencesService
{
    /// <summary>
    /// Loads preferences for the collection at <paramref name="collectionFolderPath"/>.
    /// Returns a default instance if no preferences file exists yet.
    /// </summary>
    Task<CollectionPreferences> LoadAsync(string collectionFolderPath, CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="preferences"/> for the collection at
    /// <paramref name="collectionFolderPath"/>. Silently swallows I/O errors to avoid
    /// crashing the UI over a non-critical write.
    /// </summary>
    Task SaveAsync(string collectionFolderPath, CollectionPreferences preferences, CancellationToken ct = default);
}
