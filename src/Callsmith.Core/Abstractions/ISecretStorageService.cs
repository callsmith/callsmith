namespace Callsmith.Core.Abstractions;

/// <summary>
/// Stores and retrieves secret environment variable values outside of the collection's
/// version-controlled files. Values are persisted locally per-user, keyed by collection
/// folder path and environment name, so secrets in "Dev" for Collection A are independent
/// of secrets in "Dev" for Collection B.
/// </summary>
public interface ISecretStorageService
{
    /// <summary>
    /// Namespace used for storing Basic auth and Bearer token credentials.
    /// </summary>
    public const string AuthNamespace = "__auth__";

    /// <summary>
    /// Namespace used for storing folder-level auth credentials (Basic password and API key value).
    /// Folder-level auth is keyed by the SHA-256 hash of the folder path; sensitive values are
    /// never written to <c>_meta.json</c> so they are not committed to version control.
    /// </summary>
    public const string FolderAuthNamespace = "__folder_auth__";

    /// <summary>
    /// Returns the stored secret value for <paramref name="variableName"/> in the specified
    /// environment, or <see langword="null"/> if no value has been saved yet.
    /// </summary>
    Task<string?> GetSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="value"/> as the secret value for <paramref name="variableName"/>
    /// in the specified environment.
    /// </summary>
    Task SetSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        string value,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the stored secret for a single variable. No-op if it does not exist.
    /// </summary>
    Task DeleteSecretAsync(
        string collectionFolderPath,
        string environmentName,
        string variableName,
        CancellationToken ct = default);

    /// <summary>
    /// Removes all stored secrets for an entire environment (e.g. when the environment
    /// file is deleted).
    /// </summary>
    Task DeleteEnvironmentSecretsAsync(
        string collectionFolderPath,
        string environmentName,
        CancellationToken ct = default);

    /// <summary>
    /// Updates or adds secrets for multiple environments in a single read-modify-write
    /// operation on the backing store. Existing secrets for environments or keys not
    /// present in <paramref name="allSecrets"/> are left unchanged. Prefer this over
    /// calling <see cref="SetEnvironmentSecretsAsync"/> in a loop when updating secrets
    /// for multiple environments in one operation — it avoids the repeated file I/O cycles
    /// that can cause <see cref="System.IO.IOException"/> on Windows under OS or AV
    /// file-lock contention.
    /// </summary>
    Task SetCollectionSecretsAsync(
        string collectionFolderPath,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> allSecrets,
        CancellationToken ct = default);

    /// <summary>
    /// Updates or adds all secrets in <paramref name="secrets"/> for
    /// <paramref name="environmentName"/> in a single read-modify-write operation.
    /// Existing secrets for keys not present in <paramref name="secrets"/> are left
    /// unchanged. Prefer this over calling <see cref="SetSecretAsync"/> in a loop to
    /// avoid repeated file I/O on the same backing store (which can cause
    /// <see cref="System.IO.IOException"/> on Windows when the OS briefly locks the
    /// file between consecutive open/close cycles).
    /// </summary>
    Task SetEnvironmentSecretsAsync(
        string collectionFolderPath,
        string environmentName,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken ct = default);

    /// <summary>
    /// Migrates all stored secrets from <paramref name="oldEnvironmentName"/> to
    /// <paramref name="newEnvironmentName"/> (e.g. when an environment is renamed).
    /// </summary>
    Task RenameEnvironmentSecretsAsync(
        string collectionFolderPath,
        string oldEnvironmentName,
        string newEnvironmentName,
        CancellationToken ct = default);
}
