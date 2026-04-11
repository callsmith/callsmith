using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Shared AES-256-GCM encrypt / decrypt / key-management helpers used by both
/// <c>AesSecretEncryptionService</c> (Core) and <c>AesHistoryEncryptionService</c> (Data).
/// <para>
/// Cipher format: <c>nonce(12 bytes) ‖ ciphertext(n bytes) ‖ tag(16 bytes)</c>, all
/// concatenated and Base64-encoded for at-rest string storage.
/// </para>
/// </summary>
internal static class AesGcmEncryption
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int KeySize = 32; // 256 bits

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with the supplied 256-bit <paramref name="key"/>
    /// and returns a Base64-encoded blob: nonce ‖ ciphertext ‖ tag.
    /// </summary>
    internal static string Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a Base64 blob previously produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> if the data is invalid or tampered with.
    /// </summary>
    internal static string Decrypt(string ciphertext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var data = Convert.FromBase64String(ciphertext);

        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to contain nonce and tag.");

        var nonce = data[..NonceSize];
        var tag = data[^TagSize..];
        var encryptedBytes = data[NonceSize..^TagSize];

        var plaintextBytes = new byte[encryptedBytes.Length];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, encryptedBytes, tag, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    /// <summary>
    /// Loads an existing 256-bit key from <paramref name="keyFilePath"/>, or generates a new
    /// one and persists it if the file is absent or malformed. On Unix the file is made
    /// owner-read/write only (best-effort).
    /// </summary>
    internal static byte[] LoadOrCreateKey(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var stored = File.ReadAllBytes(keyFilePath);
            if (stored.Length == KeySize)
                return stored;
        }

        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);

        var keyDir = Path.GetDirectoryName(keyFilePath)
            ?? throw new InvalidOperationException(
                $"Cannot determine directory for key file path '{keyFilePath}'.");
        Directory.CreateDirectory(keyDir);
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
