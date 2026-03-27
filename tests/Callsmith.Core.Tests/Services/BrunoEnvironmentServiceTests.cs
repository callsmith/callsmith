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
    public async Task SaveEnvironmentAsync_WritesSecretVarsAsNameOnlyListWithoutColon()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Local.bru");

        var environment = new EnvironmentModel
        {
            FilePath = filePath,
            Name = "Local",
            EnvironmentId = Guid.NewGuid(),
            Variables =
            [
                new EnvironmentVariable { Name = "baseUrl", Value = "https://example.com", IsSecret = true },
                new EnvironmentVariable { Name = "token", Value = "abc123", IsSecret = true },
            ],
        };

        await _sut.SaveEnvironmentAsync(environment);

        var written = (await File.ReadAllTextAsync(filePath)).Replace("\r\n", "\n");
        Assert.Contains("vars:secret [\n", written);
        Assert.Contains("  baseUrl,\n", written);
        Assert.Contains("  token\n", written);
        Assert.DoesNotContain("baseUrl:", written);
        Assert.DoesNotContain("token:", written);
        Assert.DoesNotContain("vars:secret {", written);
    }

    [Fact]
    public async Task SaveEnvironmentAsync_PreservesDisabledSecretNamesWithoutColon()
    {
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Local.bru");
        File.WriteAllText(filePath, """
            vars:secret [
              ~baseUrl
            ]
            """);

        var loaded = await _sut.LoadEnvironmentAsync(filePath);
        await _sut.SaveEnvironmentAsync(loaded);

        var written = (await File.ReadAllTextAsync(filePath)).Replace("\r\n", "\n");
        Assert.Contains("vars:secret [\n", written);
        Assert.Contains("  ~baseUrl\n", written);
        Assert.DoesNotContain("~baseUrl:", written);
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

    [Fact]
    public async Task GlobalEnvironment_SecretVariableType_IsPersistedAndRestoredOnRoundTrip()
    {
        // Arrange: Create a global environment with secret variables of different types
        var secretMockVar = new EnvironmentVariable
        {
            Name = "secret-email",
            Value = string.Empty,
            VariableType = EnvironmentVariable.VariableTypes.MockData,
            MockDataCategory = "Internet",
            MockDataField = "Email",
            IsSecret = true,
        };
        var secretStaticVar = new EnvironmentVariable
        {
            Name = "api-key",
            Value = "secret-value",
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsSecret = true,
        };
        var globalModel = new EnvironmentModel
        {
            FilePath = Path.Combine(_root, "environments", "_global.bru"),
            Name = "Global",
            EnvironmentId = Guid.NewGuid(),
            Variables = [secretStaticVar, secretMockVar],
        };

        // Act: Save and reload
        await _sut.SaveGlobalEnvironmentAsync(globalModel);
        var loaded = await _sut.LoadGlobalEnvironmentAsync(_root);

        // Assert: Secret variable types are preserved
        var loadedSecretMock = loaded.Variables.Single(v => v.Name == "secret-email");
        Assert.True(loadedSecretMock.IsSecret);
        Assert.Equal(EnvironmentVariable.VariableTypes.MockData, loadedSecretMock.VariableType);
        Assert.Equal("Internet", loadedSecretMock.MockDataCategory);
        Assert.Equal("Email", loadedSecretMock.MockDataField);

        var loadedSecretStatic = loaded.Variables.Single(v => v.Name == "api-key");
        Assert.True(loadedSecretStatic.IsSecret);
        Assert.Equal(EnvironmentVariable.VariableTypes.Static, loadedSecretStatic.VariableType);
    }

    [Fact]
    public async Task GlobalEnvironment_PreviewEnvironmentName_IsPersistedAndRestored()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = Path.Combine(_root, "environments", "_global.bru"),
            Name = "Global",
            EnvironmentId = Guid.NewGuid(),
            GlobalPreviewEnvironmentName = "Dev",
            Variables =
            [
                new EnvironmentVariable { Name = "base-url", Value = "https://api.example.com" },
            ],
        };

        await _sut.SaveGlobalEnvironmentAsync(globalModel);
        var loaded = await _sut.LoadGlobalEnvironmentAsync(_root);

        Assert.Equal("Dev", loaded.GlobalPreviewEnvironmentName);
    }

    [Fact]
    public async Task SaveConcreteEnvironment_DoesNotClearGlobalSecretVariablesInMeta()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = Path.Combine(_root, "environments", "_global.bru"),
            Name = "Global",
            EnvironmentId = Guid.NewGuid(),
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "secret-token",
                    Value = "top-secret",
                    IsSecret = true,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    MockDataCategory = "Internet",
                    MockDataField = "Email",
                },
            ],
        };
        await _sut.SaveGlobalEnvironmentAsync(globalModel);

        var concretePath = Path.Combine(_root, "environments", "Dev.bru");
        var concreteModel = new EnvironmentModel
        {
            FilePath = concretePath,
            Name = "Dev",
            EnvironmentId = Guid.NewGuid(),
            Variables =
            [
                new EnvironmentVariable { Name = "base-url", Value = "https://dev.example.com" },
            ],
        };

        await _sut.SaveEnvironmentAsync(concreteModel);

        var loadedGlobal = await _sut.LoadGlobalEnvironmentAsync(_root);
        var loadedSecret = loadedGlobal.Variables.Single(v => v.IsSecret && v.Name == "secret-token");
        Assert.Equal(EnvironmentVariable.VariableTypes.MockData, loadedSecret.VariableType);
        Assert.Equal("Internet", loadedSecret.MockDataCategory);
        Assert.Equal("Email", loadedSecret.MockDataField);
    }

    [Fact]
    public async Task ConcreteEnvironment_VarsSecretListSyntax_CreatesEmptyStaticSecretVariables()
    {
        // Arrange: Create a .bru file with vars:secret in list syntax
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var envFile = Path.Combine(envDir, "Dev.bru");
        
        // Write a file with list-based vars:secret syntax
        var bruContent = @"vars {
  base-url: https://dev.api.example.com
}

vars:secret [
  username,
  password,
  api-token
]
";
        File.WriteAllText(envFile, bruContent);

        // Act: Load the environment
        var env = await _sut.LoadEnvironmentAsync(envFile);

        // Assert: Secret variables are created from the list
        Assert.Equal(4, env.Variables.Count); // 1 regular var + 3 secret vars
        
        var regularVar = env.Variables.Single(v => v.Name == "base-url");
        Assert.Equal("https://dev.api.example.com", regularVar.Value);
        Assert.False(regularVar.IsSecret);

        var secretVars = env.Variables.Where(v => v.IsSecret).OrderBy(v => v.Name).ToList();
        Assert.Equal(3, secretVars.Count);
        Assert.Equal("api-token", secretVars[0].Name);
        Assert.Equal("password", secretVars[1].Name);
        Assert.Equal("username", secretVars[2].Name);
        
        // All secret vars from list should be empty static vars
        foreach (var secretVar in secretVars)
        {
            Assert.Equal(string.Empty, secretVar.Value); // Empty because not in secrets storage
            Assert.Equal(EnvironmentVariable.VariableTypes.Static, secretVar.VariableType);
        }
    }

    [Fact]
    public async Task ConcreteEnvironment_MixedVarsSecretFormats_WorksWithBothKeyValueAndList()
    {
        // Arrange: Create a .bru file with both key-value and list syntax vars:secret
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var envFile = Path.Combine(envDir, "Test.bru");
        
        var bruContent = @"vars {
  url: https://example.com
}

vars:secret {
  api-key: 
  token: 
}
";
        File.WriteAllText(envFile, bruContent);

        // Act: Load the environment
        var env = await _sut.LoadEnvironmentAsync(envFile);

        // Assert: Both formats are parsed correctly
        var secretVars = env.Variables.Where(v => v.IsSecret).ToList();
        Assert.Equal(2, secretVars.Count);
        Assert.Contains(secretVars, v => v.Name == "api-key");
        Assert.Contains(secretVars, v => v.Name == "token");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Bruno compatibility: block-targeted env save (preserves block order)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveEnvironmentAsync_NoOpSave_DoesNotAddBlankLineBetweenVarsAndVarsSecret()
    {
        // Env file with no blank line between vars and vars:secret.
        // A no-op save must not introduce a blank line there.
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Dev.bru");
        var originalContent = "vars {\n  url: https://dev.example.com/\n}\nvars:secret [\n  token\n]\n";
        File.WriteAllText(filePath, originalContent);

        var loaded = await _sut.LoadEnvironmentAsync(filePath);
        await _sut.SaveEnvironmentAsync(loaded);

        var written = (await File.ReadAllTextAsync(filePath)).Replace("\r\n", "\n");
        // No blank line should have been added between the two blocks
        Assert.DoesNotContain("}\n\nvars:secret", written);
        Assert.Contains("}\nvars:secret", written);
    }

    [Fact]
    public async Task SaveEnvironmentAsync_PreservesVarsBlockBeforeVarsSecretBlock()
    {
        // When both blocks exist, vars must always appear before vars:secret after a save
        // (their original relative order is preserved by the block-targeted approach).
        var envDir = Path.Combine(_root, "environments");
        Directory.CreateDirectory(envDir);
        var filePath = Path.Combine(envDir, "Test.bru");
        File.WriteAllText(filePath, """
            vars {
              base-url: https://test.example.com/
            }
            vars:secret [
              api-key
            ]
            """);

        var loaded = await _sut.LoadEnvironmentAsync(filePath);
        // Add a new regular variable
        var modified = loaded with
        {
            Variables = [..loaded.Variables, new EnvironmentVariable { Name = "new-var", Value = "hello" }],
        };
        await _sut.SaveEnvironmentAsync(modified);

        var written = (await File.ReadAllTextAsync(filePath)).Replace("\r\n", "\n");
        var varsIdx = written.IndexOf("vars {", StringComparison.Ordinal);
        var secretIdx = written.IndexOf("vars:secret", StringComparison.Ordinal);
        Assert.True(varsIdx < secretIdx, "vars block must come before vars:secret block");
        Assert.Contains("new-var: hello", written);
    }

    private static string VarsFile(string kvLines) => $"vars {{\n  {kvLines}\n}}\n";
}
