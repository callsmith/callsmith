using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Reads and writes global application-level preferences (not scoped to any collection).
/// Preferences are stored in the user's application data directory.
/// </summary>
public interface IAppPreferencesService
{
    /// <summary>
    /// Loads the global application preferences.
    /// Returns a default instance if no preferences file exists yet.
    /// </summary>
    Task<AppPreferences> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically reads the current preferences, applies <paramref name="update"/> to
    /// produce a new value, and writes it back. Silently swallows I/O errors to avoid
    /// crashing the UI over a non-critical write.
    /// </summary>
    Task UpdateAsync(Func<AppPreferences, AppPreferences> update, CancellationToken ct = default);
}
