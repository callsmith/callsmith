using Callsmith.Core.Abstractions;
using Callsmith.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemSecretStorageService"/>.
/// Each test uses an isolated temporary directory so no real app-data is touched.
/// </summary>
public sealed class FileSystemSecretStorageServiceTests : IDisposable
{
    private readonly string _storeDir =
        Path.Combine(Path.GetTempPath(), "callsmith-secret-tests-" + Guid.NewGuid().ToString("N"));

    private FileSystemSecretStorageService Sut() =>
        new(_storeDir, NullLogger<FileSystemSecretStorageService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    // ─── GetSecretAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSecretAsync_WhenNoFileExists_ReturnsNull()
    {
        var result = await Sut().GetSecretAsync("/some/collection", "Dev", "api-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_WhenVariableNotStored_ReturnsNull()
    {
        var sut = Sut();
        await sut.SetSecretAsync("/some/collection", "Dev", "other-key", "value");

        var result = await sut.GetSecretAsync("/some/collection", "Dev", "api-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_WhenEnvironmentNotStored_ReturnsNull()
    {
        var sut = Sut();
        await sut.SetSecretAsync("/some/collection", "Dev", "api-key", "secret");

        var result = await sut.GetSecretAsync("/some/collection", "Prod", "api-key");

        result.Should().BeNull();
    }

    // ─── SetSecretAsync / GetSecretAsync round-trip ──────────────────────────

    [Fact]
    public async Task SetThenGet_ReturnsStoredValue()
    {
        var sut = Sut();

        await sut.SetSecretAsync("/col", "Dev", "password", "hunter2");
        var result = await sut.GetSecretAsync("/col", "Dev", "password");

        result.Should().Be("hunter2");
    }

    [Fact]
    public async Task SetThenGet_MultipleVariables_ReturnCorrectValues()
    {
        var sut = Sut();

        await sut.SetSecretAsync("/col", "Dev", "api-key", "key-abc");
        await sut.SetSecretAsync("/col", "Dev", "password", "pass-xyz");

        (await sut.GetSecretAsync("/col", "Dev", "api-key")).Should().Be("key-abc");
        (await sut.GetSecretAsync("/col", "Dev", "password")).Should().Be("pass-xyz");
    }

    [Fact]
    public async Task SetThenGet_SameVariableDifferentEnvironments_AreIndependent()
    {
        var sut = Sut();

        await sut.SetSecretAsync("/col", "Dev", "token", "dev-token");
        await sut.SetSecretAsync("/col", "Prod", "token", "prod-token");

        (await sut.GetSecretAsync("/col", "Dev", "token")).Should().Be("dev-token");
        (await sut.GetSecretAsync("/col", "Prod", "token")).Should().Be("prod-token");
    }

    [Fact]
    public async Task SetThenGet_SameVariableDifferentCollections_AreIndependent()
    {
        var sut = Sut();

        await sut.SetSecretAsync("/col-a", "Dev", "token", "token-a");
        await sut.SetSecretAsync("/col-b", "Dev", "token", "token-b");

        (await sut.GetSecretAsync("/col-a", "Dev", "token")).Should().Be("token-a");
        (await sut.GetSecretAsync("/col-b", "Dev", "token")).Should().Be("token-b");
    }

    [Fact]
    public async Task Set_OverwritesPreviousValue()
    {
        var sut = Sut();

        await sut.SetSecretAsync("/col", "Dev", "token", "old-value");
        await sut.SetSecretAsync("/col", "Dev", "token", "new-value");

        (await sut.GetSecretAsync("/col", "Dev", "token")).Should().Be("new-value");
    }

    // ─── DeleteSecretAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSecretAsync_RemovesSingleVariable()
    {
        var sut = Sut();
        await sut.SetSecretAsync("/col", "Dev", "api-key", "abc");
        await sut.SetSecretAsync("/col", "Dev", "password", "xyz");

        await sut.DeleteSecretAsync("/col", "Dev", "api-key");

        (await sut.GetSecretAsync("/col", "Dev", "api-key")).Should().BeNull();
        (await sut.GetSecretAsync("/col", "Dev", "password")).Should().Be("xyz");
    }

    [Fact]
    public async Task DeleteSecretAsync_WhenVariableDoesNotExist_IsNoOp()
    {
        var act = async () => await Sut().DeleteSecretAsync("/col", "Dev", "ghost");

        await act.Should().NotThrowAsync();
    }

    // ─── DeleteEnvironmentSecretsAsync ───────────────────────────────────────

    [Fact]
    public async Task DeleteEnvironmentSecretsAsync_RemovesAllVariablesForEnvironment()
    {
        var sut = Sut();
        await sut.SetSecretAsync("/col", "Dev", "api-key", "abc");
        await sut.SetSecretAsync("/col", "Dev", "password", "xyz");
        await sut.SetSecretAsync("/col", "Prod", "api-key", "prod-abc");

        await sut.DeleteEnvironmentSecretsAsync("/col", "Dev");

        (await sut.GetSecretAsync("/col", "Dev", "api-key")).Should().BeNull();
        (await sut.GetSecretAsync("/col", "Dev", "password")).Should().BeNull();
        // Prod remains untouched.
        (await sut.GetSecretAsync("/col", "Prod", "api-key")).Should().Be("prod-abc");
    }

    [Fact]
    public async Task DeleteEnvironmentSecretsAsync_WhenEnvironmentDoesNotExist_IsNoOp()
    {
        var act = async () => await Sut().DeleteEnvironmentSecretsAsync("/col", "Ghost");

        await act.Should().NotThrowAsync();
    }

    // ─── RenameEnvironmentSecretsAsync ───────────────────────────────────────

    [Fact]
    public async Task RenameEnvironmentSecretsAsync_MovesSecretsToNewKey()
    {
        var sut = Sut();
        await sut.SetSecretAsync("/col", "Old", "token", "secret-value");

        await sut.RenameEnvironmentSecretsAsync("/col", "Old", "New");

        (await sut.GetSecretAsync("/col", "Old", "token")).Should().BeNull();
        (await sut.GetSecretAsync("/col", "New", "token")).Should().Be("secret-value");
    }

    [Fact]
    public async Task RenameEnvironmentSecretsAsync_WhenOldDoesNotExist_IsNoOp()
    {
        var act = async () =>
            await Sut().RenameEnvironmentSecretsAsync("/col", "Ghost", "New");

        await act.Should().NotThrowAsync();
    }

    // ─── Persistence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Secrets_PersistedAcrossServiceInstances()
    {
        // First instance writes.
        await Sut().SetSecretAsync("/col", "Dev", "api-key", "persisted-value");

        // Second instance (same store dir) reads.
        var result = await Sut().GetSecretAsync("/col", "Dev", "api-key");

        result.Should().Be("persisted-value");
    }
}
