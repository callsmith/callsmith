using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Reads and writes per-Bruno-collection metadata from the user's application data
/// directory. Keeps Callsmith-specific data (env order, colors, global environment)
/// out of the Bruno collection repository so Bruno desktop users see a clean repo.
/// </summary>
public interface IBrunoCollectionMetaService
{
    /// <summary>
    /// Loads metadata for the Bruno collection at <paramref name="collectionFolderPath"/>.
    /// Returns a default (empty) instance if no file has been saved yet.
    /// </summary>
    Task<BrunoCollectionMeta> LoadAsync(string collectionFolderPath, CancellationToken ct = default);

    /// <summary>
    /// Saves metadata for the Bruno collection at <paramref name="collectionFolderPath"/>.
    /// Silently swallows I/O errors to avoid crashing the UI over a non-critical write.
    /// </summary>
    Task SaveAsync(string collectionFolderPath, BrunoCollectionMeta meta, CancellationToken ct = default);
}
