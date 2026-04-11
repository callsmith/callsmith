using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;

namespace Callsmith.Core.Services;

/// <summary>
/// AES-256-GCM implementation of <see cref="ISecretEncryptionService"/>.
/// <para>
/// A random 256-bit key is generated on first run and persisted to a file in the
/// application data directory. This provides per-user per-machine at-rest encryption
/// for secret environment variable values.
/// </para>
/// <para>
/// Cipher format: <c>nonce(12 bytes) ‖ ciphertext(n bytes) ‖ tag(16 bytes)</c>, all
/// concatenated and Base64-encoded for storage as a string value.
/// </para>
/// </summary>
public sealed class AesSecretEncryptionService : ISecretEncryptionService
{
    private readonly byte[] _key;

    /// <summary>Initialises the service, storing the key in the default OS location.</summary>
    public AesSecretEncryptionService() : this(GetDefaultKeyPath()) { }

    /// <summary>
    /// Internal constructor for testing — accepts a custom key-file path so tests do not
    /// write to the real application-data folder.
    /// </summary>
    internal AesSecretEncryptionService(string keyFilePath)
    {
        _key = AesGcmEncryption.LoadOrCreateKey(keyFilePath);
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext) => AesGcmEncryption.Encrypt(plaintext, _key);

    /// <inheritdoc/>
    public string Decrypt(string ciphertext) => AesGcmEncryption.Decrypt(ciphertext, _key);

    internal static string GetDefaultKeyPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Callsmith", "secrets.key");
    }
}
