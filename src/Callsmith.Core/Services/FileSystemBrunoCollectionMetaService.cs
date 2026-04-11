using System.Collections.Concurrent;
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
public sealed class FileSystemBrunoCollectionMetaService : IBrunoCollectionMetaService, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

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
        return await WithFileLockAsync(path, async () =>
        {
            if (!File.Exists(path))
                return new BrunoCollectionMeta();

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(
        string collectionFolderPath, BrunoCollectionMeta meta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(meta);

        var path = GetMetaFilePath(collectionFolderPath);

        await WithFileLockAsync(path, async () =>
        {
            try
            {
                Directory.CreateDirectory(_storeDirectory);

                var tempPath = path + ".tmp";
                await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, meta, CallsmithJsonOptions.Default, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write Bruno collection meta at '{Path}'", path);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> WithFileLockAsync<T>(string path, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = FileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WithFileLockAsync(string path, Func<Task> action, CancellationToken ct)
    {
        var gate = FileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string GetDefaultStoreDirectory() =>
        Path.Combine(AppDataPaths.GetCallsmithAppDataDirectory(), "bruno-meta");

    private string GetMetaFilePath(string collectionFolderPath)
    {
        var fileName = FileSystemHelper.HashCollectionPath(collectionFolderPath) + ".json";
        return Path.Combine(_storeDirectory, fileName);
    }

    public void Dispose()
    {
        foreach (var semaphore in FileLocks.Values)
            semaphore.Dispose();
        FileLocks.Clear();
    }
}
