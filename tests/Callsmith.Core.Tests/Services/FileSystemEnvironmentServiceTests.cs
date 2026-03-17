using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemEnvironmentService"/>.
/// Each test gets its own isolated temporary directory.
/// </summary>
public sealed class FileSystemEnvironmentServiceTests : IDisposable
{
    private readonly FileSystemEnvironmentService _sut =
        new(NullLogger<FileSystemEnvironmentService>.Instance);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ─── ListEnvironmentsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ListEnvironmentsAsync_WhenEnvFolderMissing_ReturnsEmptyList()
    {
        var collection = _temp.CreateSubDirectory("col");

        var result = await _sut.ListEnvironmentsAsync(collection);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListEnvironmentsAsync_ReturnsEnvironmentsSortedByName()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateEnvironmentAsync(collection, "Staging");
        await _sut.CreateEnvironmentAsync(collection, "Development");
        await _sut.CreateEnvironmentAsync(collection, "Production");

        var result = await _sut.ListEnvironmentsAsync(collection);

        result.Select(e => e.Name).Should().ContainInOrder("Development", "Production", "Staging");
    }

    [Fact]
    public async Task ListEnvironmentsAsync_IgnoresUnreadableFiles()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateEnvironmentAsync(collection, "Valid");

        var envFolder = Path.Combine(collection, FileSystemCollectionService.EnvironmentFolderName);
        File.WriteAllText(Path.Combine(envFolder, "bad.env.callsmith"), "{ not valid json }");

        var result = await _sut.ListEnvironmentsAsync(collection);

        result.Should().HaveCount(1).And.Contain(e => e.Name == "Valid");
    }

    // ─── CreateEnvironmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CreateEnvironmentAsync_CreatesEnvFolderAndFile()
    {
        var collection = _temp.CreateSubDirectory("col");

        var env = await _sut.CreateEnvironmentAsync(collection, "Staging");

        env.Name.Should().Be("Staging");
        File.Exists(env.FilePath).Should().BeTrue();
        Path.GetDirectoryName(env.FilePath).Should().EndWith(FileSystemCollectionService.EnvironmentFolderName);
    }

    [Fact]
    public async Task CreateEnvironmentAsync_WhenDuplicateName_Throws()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateEnvironmentAsync(collection, "Staging");

        var act = async () => await _sut.CreateEnvironmentAsync(collection, "Staging");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Staging*");
    }

    // ─── LoadEnvironmentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task LoadEnvironmentAsync_RoundTripsNameAndVariables()
    {
        var collection = _temp.CreateSubDirectory("col");
        var original = new EnvironmentModel
        {
            FilePath = Path.Combine(collection, FileSystemCollectionService.EnvironmentFolderName, "test.env.callsmith"),
            Name = "Test",
            Variables =
            [
                new EnvironmentVariable { Name = "BaseUrl", Value = "https://api.example.com" },
                new EnvironmentVariable { Name = "Token", Value = "secret", IsSecret = true },
            ],
        };
        await _sut.SaveEnvironmentAsync(original);

        var loaded = await _sut.LoadEnvironmentAsync(original.FilePath);

        loaded.Name.Should().Be("Test");
        loaded.Variables.Should().HaveCount(2);
        loaded.Variables[0].Name.Should().Be("BaseUrl");
        loaded.Variables[0].Value.Should().Be("https://api.example.com");
        loaded.Variables[1].Name.Should().Be("Token");
        loaded.Variables[1].IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task LoadEnvironmentAsync_WhenFileMissing_ThrowsFileNotFoundException()
    {
        var act = async () => await _sut.LoadEnvironmentAsync(
            Path.Combine(_temp.Path, "missing.env.callsmith"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ─── SaveEnvironmentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SaveEnvironmentAsync_OverwritesExistingFile()
    {
        var collection = _temp.CreateSubDirectory("col");
        var env = await _sut.CreateEnvironmentAsync(collection, "Dev");
        var updated = env with
        {
            Variables = [new EnvironmentVariable { Name = "Foo", Value = "Bar" }],
        };

        await _sut.SaveEnvironmentAsync(updated);
        var loaded = await _sut.LoadEnvironmentAsync(env.FilePath);

        loaded.Variables.Should().HaveCount(1);
        loaded.Variables[0].Name.Should().Be("Foo");
        loaded.Variables[0].Value.Should().Be("Bar");
    }

    // ─── DeleteEnvironmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteEnvironmentAsync_RemovesFile()
    {
        var collection = _temp.CreateSubDirectory("col");
        var env = await _sut.CreateEnvironmentAsync(collection, "ToDelete");

        await _sut.DeleteEnvironmentAsync(env.FilePath);

        File.Exists(env.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteEnvironmentAsync_WhenFileMissing_DoesNotThrow()
    {
        var act = async () =>
            await _sut.DeleteEnvironmentAsync(Path.Combine(_temp.Path, "ghost.env.callsmith"));

        await act.Should().NotThrowAsync();
    }

    // ─── RenameEnvironmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RenameEnvironmentAsync_MovesFileAndUpdatesName()
    {
        var collection = _temp.CreateSubDirectory("col");
        var env = await _sut.CreateEnvironmentAsync(collection, "Old");

        var renamed = await _sut.RenameEnvironmentAsync(env.FilePath, "New");

        renamed.Name.Should().Be("New");
        renamed.FilePath.Should().NotBe(env.FilePath);
        File.Exists(renamed.FilePath).Should().BeTrue();
        File.Exists(env.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task RenameEnvironmentAsync_WhenTargetExists_Throws()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateEnvironmentAsync(collection, "Alpha");
        var beta = await _sut.CreateEnvironmentAsync(collection, "Beta");

        var act = async () => await _sut.RenameEnvironmentAsync(beta.FilePath, "Alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Alpha*");
    }

    [Fact]
    public async Task RenameEnvironmentAsync_PreservesVariables()
    {
        var collection = _temp.CreateSubDirectory("col");
        var env = await _sut.CreateEnvironmentAsync(collection, "Base");
        var withVars = env with
        {
            Variables = [new EnvironmentVariable { Name = "Key", Value = "Val" }],
        };
        await _sut.SaveEnvironmentAsync(withVars);

        var renamed = await _sut.RenameEnvironmentAsync(env.FilePath, "Renamed");
        var loaded = await _sut.LoadEnvironmentAsync(renamed.FilePath);

        loaded.Variables.Should().HaveCount(1);
        loaded.Variables[0].Name.Should().Be("Key");
        loaded.Variables[0].Value.Should().Be("Val");
    }

    // ─── CloneEnvironmentAsync ───────────────────────────────────────────────

    [Fact]
    public async Task CloneEnvironmentAsync_CreatesNewFileWithNewName()
    {
        var collection = _temp.CreateSubDirectory("col");
        var source = await _sut.CreateEnvironmentAsync(collection, "Dev");

        var cloned = await _sut.CloneEnvironmentAsync(source.FilePath, "Staging");

        cloned.Name.Should().Be("Staging");
        cloned.FilePath.Should().NotBe(source.FilePath);
        File.Exists(cloned.FilePath).Should().BeTrue();
        File.Exists(source.FilePath).Should().BeTrue();  // source is untouched
    }

    [Fact]
    public async Task CloneEnvironmentAsync_CopiesVariablesFromSource()
    {
        var collection = _temp.CreateSubDirectory("col");
        var source = await _sut.CreateEnvironmentAsync(collection, "Dev");
        var withVars = source with
        {
            Variables =
            [
                new EnvironmentVariable { Name = "BASE_URL", Value = "https://dev.example.com" },
                new EnvironmentVariable { Name = "TOKEN", Value = "secret", IsSecret = true },
            ],
        };
        await _sut.SaveEnvironmentAsync(withVars);

        var cloned = await _sut.CloneEnvironmentAsync(source.FilePath, "Staging");

        cloned.Variables.Should().HaveCount(2);
        cloned.Variables[0].Name.Should().Be("BASE_URL");
        cloned.Variables[0].Value.Should().Be("https://dev.example.com");
        cloned.Variables[1].Name.Should().Be("TOKEN");
        cloned.Variables[1].IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task CloneEnvironmentAsync_WhenNameAlreadyExists_Throws()
    {
        var collection = _temp.CreateSubDirectory("col");
        var source = await _sut.CreateEnvironmentAsync(collection, "Dev");
        await _sut.CreateEnvironmentAsync(collection, "Staging");

        var act = async () => await _sut.CloneEnvironmentAsync(source.FilePath, "Staging");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Staging*");
    }
}
