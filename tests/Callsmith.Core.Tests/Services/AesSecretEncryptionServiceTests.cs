using System.Security.Cryptography;
using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="AesSecretEncryptionService"/>.
/// Each test uses an isolated temporary key file so no real app-data is touched.
/// </summary>
public sealed class AesSecretEncryptionServiceTests : IDisposable
{
    private readonly string _keyDir =
        Path.Combine(Path.GetTempPath(), "callsmith-enc-tests-" + Guid.NewGuid().ToString("N"));

    private AesSecretEncryptionService Sut() =>
        new(Path.Combine(_keyDir, "test.key"));

    public void Dispose()
    {
        if (Directory.Exists(_keyDir))
            Directory.Delete(_keyDir, recursive: true);
    }

    // ─── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void EncryptThenDecrypt_ReturnsSamePlaintext()
    {
        var sut = Sut();
        const string original = "super-secret-value";

        var ciphertext = sut.Encrypt(original);
        var decrypted = sut.Decrypt(ciphertext);

        decrypted.Should().Be(original);
    }

    [Fact]
    public void EncryptThenDecrypt_EmptyString_RoundTrips()
    {
        var sut = Sut();

        var ciphertext = sut.Encrypt(string.Empty);
        var decrypted = sut.Decrypt(ciphertext);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void EncryptThenDecrypt_UnicodeContent_RoundTrips()
    {
        var sut = Sut();
        const string original = "π ≈ 3.14159 — naïve café résumé 🔑";

        var ciphertext = sut.Encrypt(original);
        var decrypted = sut.Decrypt(ciphertext);

        decrypted.Should().Be(original);
    }

    // ─── Ciphertext properties ────────────────────────────────────────────────

    [Fact]
    public void Encrypt_ProducesBase64Output()
    {
        var ciphertext = Sut().Encrypt("value");

        var act = () => Convert.FromBase64String(ciphertext);
        act.Should().NotThrow();
    }

    [Fact]
    public void Encrypt_DoesNotExposeOriginalValue()
    {
        const string plaintext = "do-not-leak-me";

        var ciphertext = Sut().Encrypt(plaintext);

        ciphertext.Should().NotContain(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachCall()
    {
        var sut = Sut();
        const string plaintext = "same-value";

        var first = sut.Encrypt(plaintext);
        var second = sut.Encrypt(plaintext);

        // Random nonce ensures distinct ciphertexts even for the same plaintext.
        first.Should().NotBe(second);
    }

    // ─── Key persistence ──────────────────────────────────────────────────────

    [Fact]
    public void NewInstances_SharedKeyFile_CanDecryptEachOther()
    {
        var keyPath = Path.Combine(_keyDir, "shared.key");

        var instance1 = new AesSecretEncryptionService(keyPath);
        var instance2 = new AesSecretEncryptionService(keyPath);

        var ciphertext = instance1.Encrypt("cross-instance-value");
        var decrypted = instance2.Decrypt(ciphertext);

        decrypted.Should().Be("cross-instance-value");
    }

    // ─── Tamper detection ─────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var sut = Sut();
        var ciphertext = sut.Encrypt("value");

        // Flip a byte in the middle of the base64 payload.
        var bytes = Convert.FromBase64String(ciphertext);
        bytes[bytes.Length / 2] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        var act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_TooShortInput_ThrowsCryptographicException()
    {
        var tooShort = Convert.ToBase64String(new byte[5]);

        var act = () => Sut().Decrypt(tooShort);
        act.Should().Throw<CryptographicException>();
    }
}
