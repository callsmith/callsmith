using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="BrunoEnvironmentService"/> on a temporary directory.
/// </summary>
public sealed class BrunoEnvironmentServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BrunoEnvironmentService _sut;

    public BrunoEnvironmentServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BrunoEnvTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sut = new BrunoEnvironmentService(
            Substitute.For<ISecretStorageService>(),
            RealMeta(),
            NullLogger<BrunoEnvironmentService>.Instance);
    }

    /// <summary>Returns a real meta service backed by a dedicated temp sub-directory.</summary>
    private FileSystemBrunoCollectionMetaService RealMeta() =>
        new(
            Path.Combine(_root, "__meta_store__"),
            NullLogger<FileSystemBrunoCollectionMetaService>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ListEnvironmentsAsync_ReturnsAllBruFiles()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, "Dev.bru"), VarsFile("core-url: https://dev.example.com/"));
        File.WriteAllText(Path.Combine(envDir, "Prod.bru"), VarsFile("core-url: https://prod.example.com/"));

        var envs = await _sut.ListEnvironmentsAsync(_root);

        Assert.Equal(2, envs.Count);
        // Sorted alphabetically
        Assert.Equal("Dev", envs[0].Name);
        Assert.Equal("Prod", envs[1].Name);
    }

    [Fact]
    public async Task LoadEnvironmentAsync_ParsesVarsBlock()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Dev.bru");
        File.WriteAllText(filePath, """
            vars {
              core-url: https://core-dev.example.com/
              base-path: /api/v1/
              ~disabled-key: unused
            }
            """);

        var env = await _sut.LoadEnvironmentAsync(filePath);

        Assert.Equal("Dev", env.Name);
        // Only enabled vars are returned
        Assert.Equal(2, env.Variables.Count);
        Assert.Equal("core-url", env.Variables[0].Name);
        Assert.Equal("https://core-dev.example.com/", env.Variables[0].Value);
        Assert.False(env.Variables[0].IsSecret);
    }

    [Fact]
    public async Task LoadEnvironmentAsync_ParsesVarsSecretBlock_NameAndIsSecretFlagArePreserved()
    {
        // The .bru file records the variable *name* and marks it as secret.
        // The actual value comes from secret storage (tested separately); here
        // we verify the name and IsSecret flag are parsed correctly.
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Staging.bru");
        File.WriteAllText(filePath, """
            vars {
              base-url: https://staging.example.com/
            }

            vars:secret {
              api-password: s3cr3t
            }
            """);

        var env = await _sut.LoadEnvironmentAsync(filePath);

        Assert.Equal(2, env.Variables.Count);
        var secret = env.Variables.Single(v => v.IsSecret);
        Assert.Equal("api-password", secret.Name);
        Assert.True(secret.IsSecret);
        // No value in secret storage (mock returns null) → empty string returned.
        Assert.Equal(string.Empty, secret.Value);
    }

    [Fact]
    public async Task LoadEnvironmentAsync_WhenSecretStoredLocally_InjectsActualValue()
    {
        var secretsDir = Path.Combine(_root, "secrets");
        var secrets = new FileSystemSecretStorageService(
            secretsDir,
            new AesSecretEncryptionService(Path.Combine(_root, "secrets.key")),
            NullLogger<FileSystemSecretStorageService>.Instance);
        var sut = new BrunoEnvironmentService(
            secrets, RealMeta(), NullLogger<BrunoEnvironmentService>.Instance);

        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Staging.bru");
        File.WriteAllText(filePath, """
            vars:secret {
              api-password:
            }
            """);

        // Store the real value in local secret storage.
        await secrets.SetSecretAsync(_root, "Staging", "api-password", "hunter2");

        var env = await sut.LoadEnvironmentAsync(filePath);

        Assert.Equal("hunter2", env.Variables.Single(v => v.IsSecret).Value);
    }

    [Fact]
    public async Task SaveEnvironmentAsync_PreservesDisabledVarsOnRoundTrip()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Local.bru");
        File.WriteAllText(filePath, """
            vars {
              core-url: https://core-sandbox.example.com/
              ~core-url: http://localhost:8080/
              apps-url: http://localhost:8081/
            }
            """);

        var loaded = await _sut.LoadEnvironmentAsync(filePath);
        await _sut.SaveEnvironmentAsync(loaded);

        var written = await File.ReadAllTextAsync(filePath);
        Assert.Contains("  ~core-url: http://localhost:8080/", written);
    }

    [Fact]
    public async Task CreateEnvironmentAsync_CreatesFileInEnvironmentsFolder()
    {
        var env = await _sut.CreateEnvironmentAsync(_root, "QA");

        Assert.True(File.Exists(env.FilePath));
        Assert.Contains("environments", env.FilePath);
        Assert.Equal("QA", env.Name);
    }

    [Fact]
    public async Task RenameEnvironmentAsync_MovesFileAndUpdatesName()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "OldName.bru");
        File.WriteAllText(filePath, VarsFile("key: value"));

        var renamed = await _sut.RenameEnvironmentAsync(filePath, "NewName");

        Assert.False(File.Exists(filePath));
        Assert.True(File.Exists(renamed.FilePath));
        Assert.Equal("NewName", renamed.Name);
    }

    [Fact]
    public async Task CloneEnvironmentAsync_CreatesNewFileWithSameVariables()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var source = Path.Combine(envDir, "Source.bru");
        File.WriteAllText(source, VarsFile("url: https://example.com/"));

        var cloned = await _sut.CloneEnvironmentAsync(source, "Clone");

        Assert.True(File.Exists(cloned.FilePath));
        Assert.Equal("Clone", cloned.Name);
        Assert.Single(cloned.Variables);
        Assert.Equal("url", cloned.Variables[0].Name);
    }

    private static string VarsFile(string kvLines) => $"vars {{\n  {kvLines}\n}}\n";
}
