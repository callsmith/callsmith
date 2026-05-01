using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ISecretStorageService"/> implementation that persists secret environment-variable
/// values in the user's local application-data directory, outside of any version-controlled
/// collection folder.
/// <para>
/// Storage location (never roams across machines):
/// <list type="bullet">
///   <item>Windows: <c>%LOCALAPPDATA%\Callsmith\secrets\</c></item>
///   <item>macOS/Linux: <c>~/.local/share/Callsmith/secrets/</c></item>
/// </list>
/// </para>
/// <para>
/// Each file is AES-256-GCM encrypted before being written to disk. Legacy plaintext files
/// (whose content begins with <c>{</c>) are transparently migrated to the encrypted format
/// on the next write.
/// </para>
/// </summary>
public sealed class FileSystemSecretStorageService : ISecretStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storeDirectory;
    private readonly ISecretEncryptionService _encryption;
    private readonly ILogger<FileSystemSecretStorageService> _logger;

    /// <summary>Initialises the service, storing secrets in the default OS location.</summary>
    public FileSystemSecretStorageService(
        ISecretEncryptionService encryption,
        ILogger<FileSystemSecretStorageService> logger)
        : this(GetDefaultStoreDirectory(), encryption, logger) { }

    /// <summary>
    /// Internal constructor for testing — accepts a custom directory so tests do not
    /// write to the real application-data folder.
    /// </summary>
    internal FileSystemSecretStorageService(
        string storeDirectory,
        ISecretEncryptionService encryption,
        ILogger<FileSystemSecretStorageService> logger)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(logger);
        _encryption = encryption;
        _logger = logger;
        _storeDirectory = storeDirectory;
        Directory.CreateDirectory(storeDirectory);
    }

    private static string GetDefaultStoreDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Callsmith", "secrets");
    }

    /// <inheritdoc/>
    public async Task<string?> GetSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(variableName);

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        return data.TryGetValue(environmentName, out var envSecrets) &&
               envSecrets.TryGetValue(variableName, out var value)
            ? value
            : null;
    }

    /// <inheritdoc/>
    public async Task SetSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        string value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(variableName);
        ArgumentNullException.ThrowIfNull(value);

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        if (!data.TryGetValue(environmentName, out var envSecrets))
        {
            envSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            data[environmentName] = envSecrets;
        }
        envSecrets[variableName] = value;
        await SaveAllAsync(collectionFolderPath, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(variableName);

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        if (!data.TryGetValue(environmentName, out var envSecrets)) return;

        if (!envSecrets.Remove(variableName)) return;

        if (envSecrets.Count == 0)
            data.Remove(environmentName);

        await SaveAllAsync(collectionFolderPath, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteEnvironmentSecretsAsync(
        string collectionFolderPath,
        string environmentName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentName);

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        if (!data.Remove(environmentName)) return;

        await SaveAllAsync(collectionFolderPath, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetEnvironmentSecretsAsync(
        string collectionFolderPath,
        string environmentName,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(secrets);

        if (secrets.Count == 0) return;

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        if (!data.TryGetValue(environmentName, out var envSecrets))
        {
            envSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            data[environmentName] = envSecrets;
        }
        foreach (var kv in secrets)
            envSecrets[kv.Key] = kv.Value;
        await SaveAllAsync(collectionFolderPath, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RenameEnvironmentSecretsAsync(
        string collectionFolderPath,
        string oldEnvironmentName,
        string newEnvironmentName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(oldEnvironmentName);
        ArgumentNullException.ThrowIfNull(newEnvironmentName);

        var data = await LoadAllAsync(collectionFolderPath, ct).ConfigureAwait(false);
        if (!data.TryGetValue(oldEnvironmentName, out var envSecrets)) return;

        data.Remove(oldEnvironmentName);
        data[newEnvironmentName] = envSecrets;
        await SaveAllAsync(collectionFolderPath, data, ct).ConfigureAwait(false);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private async Task<Dictionary<string, Dictionary<string, string>>> LoadAllAsync(
        string collectionFolderPath, CancellationToken ct)
    {
        var path = GetFilePath(collectionFolderPath);
        if (!File.Exists(path))
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        try
        {
            var fileContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);

            string json;
            if (fileContent.TrimStart().StartsWith('{'))
            {
                // Legacy plaintext format — will be re-encrypted on the next write.
                // This check is reliable: standard Base64 output (A–Z, a–z, 0–9, +, /, =)
                // never starts with '{', so there is no ambiguity with the encrypted format.
                json = fileContent;
            }
            else
            {
                // Encrypted format: Base64-encoded AES-256-GCM ciphertext.
                json = _encryption.Decrypt(fileContent.Trim());
            }

            var result = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, JsonOptions);
            return result ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException
                                       or UnauthorizedAccessException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read secret storage at '{Path}'", path);
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAllAsync(
        string collectionFolderPath,
        Dictionary<string, Dictionary<string, string>> data,
        CancellationToken ct)
    {
        var path = GetFilePath(collectionFolderPath);
        Directory.CreateDirectory(_storeDirectory);

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var encrypted = _encryption.Encrypt(json);
            await File.WriteAllTextAsync(path, encrypted, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write secret storage at '{Path}'", path);
        }
    }

    private string GetFilePath(string collectionFolderPath) =>
        Path.Combine(_storeDirectory, FileSystemHelper.HashCollectionPath(collectionFolderPath) + ".json");
}
