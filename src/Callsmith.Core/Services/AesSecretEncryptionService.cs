using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Callsmith.Core.Abstractions;

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
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bits

    private readonly byte[] _key;

    /// <summary>Initialises the service, storing the key in the default OS location.</summary>
    public AesSecretEncryptionService() : this(GetDefaultKeyPath()) { }

    /// <summary>
    /// Internal constructor for testing — accepts a custom key-file path so tests do not
    /// write to the real application-data folder.
    /// </summary>
    internal AesSecretEncryptionService(string keyFilePath)
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

        var plaintextBytes = new byte[encryptedBytes.Length];
        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, encryptedBytes, tag, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    internal static string GetDefaultKeyPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Callsmith", "secrets.key");
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

        var keyDir = Path.GetDirectoryName(keyFilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for key file path '{keyFilePath}'.");
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
