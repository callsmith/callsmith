using System.Text.Json;
using Callsmith.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// Persists a list of recently-opened collection folder paths to the user's application
/// data directory. Paths are ordered most-recently-used first; stale paths (where the
/// directory no longer exists) are silently dropped on load.
/// </summary>
public sealed class RecentCollectionsService : IRecentCollectionsService
{
    private const int MaxEntries = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storePath;
    private readonly ILogger<RecentCollectionsService> _logger;

    public RecentCollectionsService(ILogger<RecentCollectionsService> logger)
        : this(GetDefaultStoreDirectory(), logger)
    {
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom store directory so tests
    /// do not write to the real application data folder.
    /// </summary>
    internal RecentCollectionsService(string storeDirectory, ILogger<RecentCollectionsService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Directory.CreateDirectory(storeDirectory);
        _storePath = Path.Combine(storeDirectory, "recent.json");
    }

    private static string GetDefaultStoreDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Callsmith");
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load recent collections from '{StorePath}'", _storePath);
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
