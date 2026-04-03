using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IBrunoCollectionMetaService"/> implementation that stores Bruno collection
/// metadata in <c>%APPDATA%\Callsmith\bruno-meta\</c>, keyed by a SHA-256 hash of the
/// collection folder path. Nothing is written inside the Bruno collection folder, so the
/// repo remains clean for Bruno desktop users.
/// </summary>
public sealed class FileSystemBrunoCollectionMetaService : IBrunoCollectionMetaService
{
    private readonly string _storeDirectory;
    private readonly ILogger<FileSystemBrunoCollectionMetaService> _logger;

    public FileSystemBrunoCollectionMetaService(
        ILogger<FileSystemBrunoCollectionMetaService> logger)
        : this(GetDefaultStoreDirectory(), logger)
    {
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom store directory so tests
    /// do not write to the real application data folder.
    /// </summary>
    internal FileSystemBrunoCollectionMetaService(
        string storeDirectory,
        ILogger<FileSystemBrunoCollectionMetaService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _storeDirectory = storeDirectory;
    }

    /// <inheritdoc/>
    public async Task<BrunoCollectionMeta> LoadAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var path = GetMetaFilePath(collectionFolderPath);
        if (!File.Exists(path))
            return new BrunoCollectionMeta();

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                       .DeserializeAsync<BrunoCollectionMeta>(stream, CallsmithJsonOptions.Default, ct)
                       .ConfigureAwait(false)
                   ?? new BrunoCollectionMeta();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read Bruno collection meta at '{Path}'", path);
            return new BrunoCollectionMeta();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(
        string collectionFolderPath, BrunoCollectionMeta meta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(meta);

        var path = GetMetaFilePath(collectionFolderPath);

        try
        {
            Directory.CreateDirectory(_storeDirectory);
            await using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, meta, CallsmithJsonOptions.Default, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write Bruno collection meta at '{Path}'", path);
        }
    }

    private static string GetDefaultStoreDirectory() =>
        Path.Combine(AppDataPaths.GetCallsmithAppDataDirectory(), "bruno-meta");

    private string GetMetaFilePath(string collectionFolderPath)
    {
        var fileName = FileSystemHelper.HashCollectionPath(collectionFolderPath) + ".json";
        return Path.Combine(_storeDirectory, fileName);
    }
}
