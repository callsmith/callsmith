using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IAppPreferencesService"/> implementation that stores global application
/// preferences in a single JSON file in the user's application data directory.
/// </summary>
public sealed class FileSystemAppPreferencesService : IAppPreferencesService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;
    private readonly ILogger<FileSystemAppPreferencesService> _logger;

    public FileSystemAppPreferencesService(ILogger<FileSystemAppPreferencesService> logger)
        : this(GetDefaultStoreDirectory(), logger)
    {
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom store directory so tests
    /// do not write to the real application data folder.
    /// </summary>
    internal FileSystemAppPreferencesService(
        string storeDirectory,
        ILogger<FileSystemAppPreferencesService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Directory.CreateDirectory(storeDirectory);
        _storePath = Path.Combine(storeDirectory, "app-prefs.json");
    }

    private static string GetDefaultStoreDirectory() =>
        AppDataPaths.GetCallsmithAppDataDirectory();

    /// <inheritdoc/>
    public async Task<AppPreferences> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_storePath))
            return new AppPreferences();

        try
        {
            var json = await File.ReadAllTextAsync(_storePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppPreferences>(json, CallsmithJsonOptions.Default)
                   ?? new AppPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load app preferences from '{StorePath}'", _storePath);
            return new AppPreferences();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Func<AppPreferences, AppPreferences> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(ct).ConfigureAwait(false);
            var updated = update(current);
            var json = JsonSerializer.Serialize(updated, CallsmithJsonOptions.Default);
            await File.WriteAllTextAsync(_storePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save app preferences to '{StorePath}'", _storePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
