using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;

namespace Callsmith.Data;

/// <summary>
/// AES-256-GCM implementation of <see cref="IHistoryEncryptionService"/>.
/// <para>
/// A random 256-bit key is generated on first run and persisted to a file in the
/// application data directory. This provides per-user per-machine at-rest
/// encryption for sensitive history values (bearer tokens, API keys, etc.).
/// </para>
/// <para>
/// Cipher format: <c>nonce(12 bytes) ‖ ciphertext(n bytes) ‖ tag(16 bytes)</c>, all
/// concatenated and base64-encoded for storage as a string value.
/// </para>
/// </summary>
public sealed class AesHistoryEncryptionService : IHistoryEncryptionService
{
    private readonly byte[] _key;

    public AesHistoryEncryptionService() : this(CallsmithDbContext.GetKeyPath()) { }

    internal AesHistoryEncryptionService(string keyFilePath)
    {
        _key = AesGcmEncryption.LoadOrCreateKey(keyFilePath);
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext) => AesGcmEncryption.Encrypt(plaintext, _key);

    /// <inheritdoc/>
    public string Decrypt(string ciphertext) => AesGcmEncryption.Decrypt(ciphertext, _key);
}
