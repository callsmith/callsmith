using System.Text.Json;

namespace Callsmith.Core.Services;

/// <summary>
/// Persists a list of recently-opened collection folder paths to the user's application
/// data directory. Paths are ordered most-recently-used first; stale paths (where the
/// directory no longer exists) are silently dropped on load.
/// </summary>
public sealed class RecentCollectionsService
{
    private const int MaxEntries = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storePath;

    public RecentCollectionsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "Callsmith");
        Directory.CreateDirectory(appDir);
        _storePath = Path.Combine(appDir, "recent.json");
    }

    /// <summary>
    /// Loads the list of recent collection paths from disk, filtering out any paths that
    /// no longer exist on the file system.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_storePath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_storePath, ct);
            var list = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
            return list.Where(Directory.Exists).ToList().AsReadOnly();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Prepends <paramref name="folderPath"/> to the list (de-duplicating case-insensitively),
    /// trims the list to <see cref="MaxEntries"/>, then persists to disk.
    /// </summary>
    public async Task PushAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        var existing = (await LoadAsync(ct)).ToList();
        var updated = new List<string>(MaxEntries + 1) { folderPath };
        updated.AddRange(existing.Where(p =>
            !string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase)));

        if (updated.Count > MaxEntries)
            updated = updated[..MaxEntries];

        var json = JsonSerializer.Serialize(updated, JsonOptions);
        await File.WriteAllTextAsync(_storePath, json, ct);
    }
}
