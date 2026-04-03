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
    /// Migrates all stored secrets from <paramref name="oldEnvironmentName"/> to
    /// <paramref name="newEnvironmentName"/> (e.g. when an environment is renamed).
    /// </summary>
    Task RenameEnvironmentSecretsAsync(
        string collectionFolderPath,
        string oldEnvironmentName,
        string newEnvironmentName,
        CancellationToken ct = default);
}
