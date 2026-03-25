namespace Callsmith.Core.Abstractions;

/// <summary>
/// Encrypts and decrypts sensitive string values for at-rest storage in the local secrets files.
/// <para>
/// Encryption is scoped to the current user on the current machine — values encrypted on one
/// machine or by one OS user cannot be decrypted by another.
/// </para>
/// </summary>
public interface ISecretEncryptionService
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a Base64-encoded ciphertext string
    /// suitable for writing to a file.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a value previously produced by <see cref="Encrypt"/>.
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the
    /// ciphertext is invalid or has been tampered with.
    /// </summary>
    string Decrypt(string ciphertext);
}
