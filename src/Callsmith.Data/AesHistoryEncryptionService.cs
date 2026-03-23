using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Callsmith.Core.Abstractions;

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
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bits

    private readonly byte[] _key;

    public AesHistoryEncryptionService() : this(CallsmithDbContext.GetKeyPath()) { }

    internal AesHistoryEncryptionService(string keyFilePath)
    {
        _key = LoadOrCreateKey(keyFilePath);
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: nonce ‖ ciphertext ‖ tag
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    /// <inheritdoc/>
    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var data = Convert.FromBase64String(ciphertext);

        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to contain nonce and tag.");

        var nonce = data[..NonceSize];
        var tag = data[^TagSize..];
        var encryptedBytes = data[NonceSize..^TagSize];

        var plaintext = new byte[encryptedBytes.Length];
        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, encryptedBytes, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] LoadOrCreateKey(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var stored = File.ReadAllBytes(keyFilePath);
            if (stored.Length == KeySize)
                return stored;
        }

        // Generate a new random key and persist it.
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);

        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        File.WriteAllBytes(keyFilePath, key);

        // Restrict the key file to the current user on Unix-like systems.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(keyFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException) { /* best-effort */ }
        }

        return key;
    }
}
