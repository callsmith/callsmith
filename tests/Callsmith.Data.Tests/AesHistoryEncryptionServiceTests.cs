using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using Callsmith.Data.Tests.TestHelpers;

namespace Callsmith.Data.Tests;

public sealed class AesHistoryEncryptionServiceTests
{
    [Fact]
    public void EncryptThenDecrypt_RoundTripsPlaintext()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "history.key");
        var sut = CreateWithKeyPath(keyPath);

        var plaintext = "Bearer test-token";
        var ciphertext = sut.Encrypt(plaintext);

        var result = sut.Decrypt(ciphertext);

        result.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_SamePlaintextTwice_UsesDifferentNonce()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "history.key");
        var sut = CreateWithKeyPath(keyPath);

        var c1 = sut.Encrypt("same");
        var c2 = sut.Encrypt("same");

        c1.Should().NotBe(c2);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "history.key");
        var sut = CreateWithKeyPath(keyPath);

        var ciphertext = sut.Encrypt("secret-value");
        var raw = Convert.FromBase64String(ciphertext);
        raw[raw.Length - 1] ^= 0xFF;
        var tampered = Convert.ToBase64String(raw);

        var act = () => sut.Decrypt(tampered);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Constructor_LoadsExistingKeyForSubsequentInstances()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "history.key");

        var first = CreateWithKeyPath(keyPath);
        var ciphertext = first.Encrypt("persisted-key");

        var second = CreateWithKeyPath(keyPath);
        var decrypted = second.Decrypt(ciphertext);

        File.Exists(keyPath).Should().BeTrue();
        File.ReadAllBytes(keyPath).Length.Should().Be(32);
        decrypted.Should().Be("persisted-key");
    }

    private static AesHistoryEncryptionService CreateWithKeyPath(string keyPath)
    {
        var ctor = typeof(AesHistoryEncryptionService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(string) },
            modifiers: null);

        ctor.Should().NotBeNull();
        return (AesHistoryEncryptionService)ctor!.Invoke(new object[] { keyPath });
    }
}
