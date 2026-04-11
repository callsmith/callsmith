using System.Collections.Concurrent;
using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ICollectionPreferencesService"/> implementation that stores preferences in
/// the user's application data directory (<c>%APPDATA%\Callsmith\collection-prefs\</c> on
/// Windows), keyed by a SHA-256 hash of the collection folder path.
/// This keeps personal preferences out of the collection folder and therefore out of
/// version control.
/// </summary>
public sealed class FileSystemCollectionPreferencesService : ICollectionPreferencesService, IDisposable
{
    // One semaphore per prefs file path so concurrent writers for different collections
    // do not block each other, but concurrent writers for the same collection queue up.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _storeDirectory;
    private readonly ILogger<FileSystemCollectionPreferencesService> _logger;

    public FileSystemCollectionPreferencesService(
        ILogger<FileSystemCollectionPreferencesService> logger)
        : this(GetDefaultStoreDirectory(), logger)
    {
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom store directory so tests
    /// do not write to the real application data folder.
    /// </summary>
    internal FileSystemCollectionPreferencesService(
        string storeDirectory,
        ILogger<FileSystemCollectionPreferencesService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Directory.CreateDirectory(storeDirectory);
        _storeDirectory = storeDirectory;
    }

    private static string GetDefaultStoreDirectory() =>
        Path.Combine(AppDataPaths.GetCallsmithAppDataDirectory(), "collection-prefs");

    private SemaphoreSlim GetFileLock(string prefsFilePath) =>
        _locks.GetOrAdd(prefsFilePath, _ => new SemaphoreSlim(1, 1));

    /// <inheritdoc/>
    public async Task<CollectionPreferences> LoadAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var path = GetPrefsFilePath(collectionFolderPath);
        var sem = GetFileLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(
        string collectionFolderPath, CollectionPreferences preferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(preferences);

        var path = GetPrefsFilePath(collectionFolderPath);
        var sem = GetFileLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(path, preferences, ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(
        string collectionFolderPath,
        Func<CollectionPreferences, CollectionPreferences> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(update);

        var path = GetPrefsFilePath(collectionFolderPath);
        var sem = GetFileLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(path, ct).ConfigureAwait(false);
            var updated = update(current);
            await SaveCoreAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not update collection preferences at '{Path}'", path);
        }
        finally
        {
            sem.Release();
        }
    }

    // ── lock-free core helpers ────────────────────────────────────────────────

    private async Task<CollectionPreferences> LoadCoreAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new CollectionPreferences();

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                       .DeserializeAsync<CollectionPreferences>(stream, CallsmithJsonOptions.Default, ct)
                       .ConfigureAwait(false)
                   ?? new CollectionPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read collection preferences at '{Path}'", path);
            return new CollectionPreferences();
        }
    }

    private async Task SaveCoreAsync(string path, CollectionPreferences preferences, CancellationToken ct)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
            await JsonSerializer
                .SerializeAsync(stream, preferences, CallsmithJsonOptions.Default, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write collection preferences at '{Path}'", path);
        }
    }

    /// <summary>
    /// Derives a stable, file-system-safe name for the prefs file from the collection
    /// folder path by hashing its normalised, lower-cased form with SHA-256.
    /// </summary>
    private string GetPrefsFilePath(string collectionFolderPath)
    {
        var fileName = FileSystemHelper.HashCollectionPath(collectionFolderPath) + ".json";
        return Path.Combine(_storeDirectory, fileName);
    }

    public void Dispose()
    {
        foreach (var semaphore in _locks.Values)
            semaphore.Dispose();
        _locks.Clear();
    }
}

