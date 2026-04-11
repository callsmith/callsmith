using System.Security.Cryptography;
using FluentAssertions;

namespace Callsmith.Data.Tests;

/// <summary>
/// Tests for <see cref="AesHistoryEncryptionService"/>.
/// Each test uses an isolated temporary directory so no real app-data is touched.
/// </summary>
public sealed class AesHistoryEncryptionServiceTests : IDisposable
{
    private readonly string _keyDir =
        Path.Combine(Path.GetTempPath(), "callsmith-hist-enc-tests-" + Guid.NewGuid().ToString("N"));

    private AesHistoryEncryptionService Sut() =>
        new(Path.Combine(_keyDir, "history.key"));

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
        const string original = "bearer-token-abc123";

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

    // ─── Tamper detection ─────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var sut = Sut();
        var ciphertext = sut.Encrypt("secret-api-key");

        var bytes = Convert.FromBase64String(ciphertext);
        bytes[bytes.Length / 2] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        var act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>();
    }

    // ─── Key file lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void KeyFile_CreatedOnFirstUse_ReloadedOnSubsequentInstantiation()
    {
        var keyPath = Path.Combine(_keyDir, "reuse.key");
        Directory.CreateDirectory(_keyDir);

        var instance1 = new AesHistoryEncryptionService(keyPath);
        File.Exists(keyPath).Should().BeTrue("key file should be created on first instantiation");

        var ciphertext = instance1.Encrypt("persistent-secret");

        var instance2 = new AesHistoryEncryptionService(keyPath);
        var decrypted = instance2.Decrypt(ciphertext);

        decrypted.Should().Be("persistent-secret");
    }
}
