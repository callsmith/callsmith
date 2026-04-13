using Callsmith.Core.Abstractions;
using Callsmith.Core.Import;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="CollectionImportService"/> using mocked importers,
/// but real filesystem collection/environment services to verify file output.
/// </summary>
public sealed class CollectionImportServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileSystemCollectionService _collectionService =
        new(Substitute.For<ISecretStorageService>(),
            NullLogger<FileSystemCollectionService>.Instance);
    private readonly FileSystemEnvironmentService _environmentService =
        new(Substitute.For<ISecretStorageService>(),
            NullLogger<FileSystemEnvironmentService>.Instance);

    public void Dispose() => _temp.Dispose();

    // ─────────────────────────────────────────────────────────────────────────
    // FindImporterAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindImporterAsync_ReturnsMatchingImporter()
    {
        var importer = MakeImporter(canImport: true, extensions: [".yaml"]);
        var sut = BuildSut(importer);

        var result = await sut.FindImporterAsync("/some/file.yaml");

        result.Should().BeSameAs(importer);
    }

    [Fact]
    public async Task FindImporterAsync_ReturnsNullWhenNoImporterMatches()
    {
        var importer = MakeImporter(canImport: false, extensions: [".yaml"]);
        var sut = BuildSut(importer);

        var result = await sut.FindImporterAsync("/some/file.json");

        result.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SupportedFileExtensions
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SupportedFileExtensions_AggregatesFromAllImporters()
    {
        var a = MakeImporter(canImport: false, extensions: [".yaml", ".yml"]);
        var b = MakeImporter(canImport: false, extensions: [".json"]);
        var sut = BuildSut(a, b);

        sut.SupportedFileExtensions.Should().BeEquivalentTo([".yaml", ".yml", ".json"]);
    }

    [Fact]
    public void SupportedFileExtensions_DeduplicatesExtensions()
    {
        var a = MakeImporter(canImport: false, extensions: [".yaml"]);
        var b = MakeImporter(canImport: false, extensions: [".yaml", ".json"]);
        var sut = BuildSut(a, b);

        sut.SupportedFileExtensions.Should().HaveCount(2);
        sut.SupportedFileExtensions.Should().Contain(".yaml");
        sut.SupportedFileExtensions.Should().Contain(".json");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportToFolderAsync — writes to disk
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportToFolderAsync_ThrowsWhenNoImporterFound()
    {
        var importer = MakeImporter(canImport: false, extensions: [".yaml"]);
        var sut = BuildSut(importer);
        var act = () => sut.ImportToFolderAsync("/no.yaml", _temp.Path);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ImportToFolderAsync_WritesRootRequestsToTargetFolder()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest
                {
                    Name = "Get Users",
                    Method = System.Net.Http.HttpMethod.Get,
                    Url = "https://example.com/users",
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("output");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var files = Directory.GetFiles(target, "*.callsmith");
        files.Should().HaveCount(1);
        Path.GetFileNameWithoutExtension(files[0]).Should().Be("Get Users");
    }

    [Fact]
    public async Task ImportToFolderAsync_WritesFoldersRecursively()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootFolders =
            [
                new ImportedFolder
                {
                    Name = "Users",
                    Requests =
                    [
                        new ImportedRequest
                        {
                            Name = "List",
                            Method = System.Net.Http.HttpMethod.Get,
                            Url = "https://example.com/users",
                        },
                    ],
                    SubFolders =
                    [
                        new ImportedFolder
                        {
                            Name = "Admin",
                            Requests =
                            [
                                new ImportedRequest
                                {
                                    Name = "Manage",
                                    Method = System.Net.Http.HttpMethod.Post,
                                    Url = "https://example.com/admin",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("nested");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var usersFolder = Path.Combine(target, "Users");
        var adminFolder = Path.Combine(usersFolder, "Admin");

        Directory.Exists(usersFolder).Should().BeTrue();
        Directory.Exists(adminFolder).Should().BeTrue();

        Directory.GetFiles(usersFolder, "*.callsmith").Should().HaveCount(1);
        Directory.GetFiles(adminFolder, "*.callsmith").Should().HaveCount(1);
    }

    [Fact]
    public async Task ImportToFolderAsync_WritesEnvironmentFiles()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Dev",
                    Variables = new Dictionary<string, string>
                    {
                        { "api-url", "https://dev.example.com" },
                        { "token", "abc123" },
                    },
                    Color = "#007bff",
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("envout");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var envFolder = Path.Combine(target, "environment");
        var envFiles = Directory.GetFiles(envFolder, "*.env.callsmith");
        envFiles.Should().HaveCount(1);

        var saved = await _environmentService.LoadEnvironmentAsync(envFiles[0]);
        saved.Name.Should().Be("Dev");
        saved.Color.Should().Be("#007bff");
        saved.Variables.Should().HaveCount(2);
        saved.Variables.Should().Contain(v => v.Name == "api-url" && v.Value == "https://dev.example.com");
    }

    [Fact]
    public async Task ImportToFolderAsync_WritesOrderFilePreservingInterleavedOrder()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Alpha", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
                new ImportedRequest { Name = "Gamma", Method = System.Net.Http.HttpMethod.Get, Url = "https://g.com" },
            ],
            RootFolders =
            [
                new ImportedFolder
                {
                    Name = "Beta",
                    ItemOrder = [],
                },
            ],
            // Interleaved order: Alpha, Beta (folder), Gamma
            ItemOrder = ["Alpha", "Beta", "Gamma"],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("order");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var metaFile = Path.Combine(target, "_meta.json");
        File.Exists(metaFile).Should().BeTrue();

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(metaFile));
        var written = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            doc.RootElement.GetProperty("order").GetRawText());
        written.Should().ContainInOrder("Alpha.callsmith", "Beta", "Gamma.callsmith");
    }

    [Fact]
    public async Task ImportToFolderAsync_DeduplicatesRequestFilenamesOnConflict()
    {
        // Two requests with the same name in the same folder
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Post, Url = "https://b.com" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("dedup");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var files = Directory.GetFiles(target, "*.callsmith");
        files.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImportToFolderAsync_MetaFileReflectsRenamedDuplicates()
    {
        // Two requests with the same name plus an explicit ItemOrder — the meta file
        // must use the actual (deduplicated) filenames, not the original names.
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Post, Url = "https://b.com" },
            ],
            ItemOrder = ["Req", "Req"],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("dedup_order");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        var metaFile = Path.Combine(target, "_meta.json");
        File.Exists(metaFile).Should().BeTrue();

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(metaFile));
        var written = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            doc.RootElement.GetProperty("order").GetRawText());
        written.Should().HaveCount(2);
        written.Should().Contain("Req.callsmith");
        written.Should().Contain("Req (1).callsmith");
    }

    [Fact]
    public async Task ImportToFolderAsync_PersistsEnvironmentOrderToMetaFile()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment { Name = "Dev",     Variables = new Dictionary<string, string>() },
                new ImportedEnvironment { Name = "Staging", Variables = new Dictionary<string, string>() },
                new ImportedEnvironment { Name = "Prod",    Variables = new Dictionary<string, string>() },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("envorder");

        await sut.ImportToFolderAsync("/fake.yaml", target);

        // Order is written to environment/_meta.json inside the collection folder.
        var metaFile = Path.Combine(target,
            FileSystemCollectionService.EnvironmentFolderName,
            FileSystemEnvironmentService.MetaFileName);
        File.Exists(metaFile).Should().BeTrue();

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(metaFile));
        var order = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            doc.RootElement.GetProperty("order").GetRawText())!;
        order.Should().HaveCount(3);
        // Order must match the import collection order, not alphabetical (Prod < Dev < Staging)
        order[0].Should().StartWith("Dev");
        order[1].Should().StartWith("Staging");
        order[2].Should().StartWith("Prod");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportToFolderAsync — Basic auth secrets
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportToFolderAsync_BasicAuthPassword_IsSavedToSecretStorage()
    {
        // Use real secret storage so we can verify the round-trip.
        var secretStore = _temp.CreateSubDirectory("secrets");
        var secrets = new FileSystemSecretStorageService(
            secretStore,
            new AesSecretEncryptionService(System.IO.Path.Combine(_temp.Path, "secrets.key")),
            NullLogger<FileSystemSecretStorageService>.Instance);
        var collectionService = new FileSystemCollectionService(
            secrets, NullLogger<FileSystemCollectionService>.Instance);
        var sut = new CollectionImportService(
            [MakeImporter(canImport: true, extensions: [".yaml"], returns: new ImportedCollection
            {
                Name = "Test",
                RootRequests =
                [
                    new ImportedRequest
                    {
                        Name = "Login",
                        Method = System.Net.Http.HttpMethod.Post,
                        Url = "https://example.com/login",
                        Auth = new AuthConfig
                        {
                            AuthType = AuthConfig.AuthTypes.Basic,
                            Username = "admin",
                            Password = "s3cr3t",
                        },
                    },
                ],
            })],
            collectionService,
            _environmentService,
            NullLogger<CollectionImportService>.Instance);

        var target = _temp.CreateSubDirectory("basic-auth-import");
        await sut.ImportToFolderAsync("/fake.yaml", target);

        // The on-disk request file must not expose the plain-text password.
        var requestFile = Directory.GetFiles(target, "*.callsmith").Single();
        var json = await File.ReadAllTextAsync(requestFile);
        json.Should().NotContain("s3cr3t");

        // Loading the request back through the service must return the password.
        var loaded = await collectionService.LoadRequestAsync(requestFile);
        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        loaded.Auth.Username.Should().Be("admin");
        loaded.Auth.Password.Should().Be("s3cr3t");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportIntoCollectionAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportIntoCollectionAsync_ThrowsWhenNoImporterFound()
    {
        var importer = MakeImporter(canImport: false, extensions: [".yaml"]);
        var sut = BuildSut(importer);
        var collectionRoot = _temp.CreateSubDirectory("col");

        var act = () => sut.ImportIntoCollectionAsync("/no.yaml", collectionRoot);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_WritesRootRequestsToCollectionRoot()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest
                {
                    Name = "Get Users",
                    Method = System.Net.Http.HttpMethod.Get,
                    Url = "https://example.com/users",
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var collectionRoot = _temp.CreateSubDirectory("col-root");

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(1);
        Path.GetFileNameWithoutExtension(files[0]).Should().Be("Get Users");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_WritesRequestsToSpecifiedSubFolder()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest
                {
                    Name = "Post Order",
                    Method = System.Net.Http.HttpMethod.Post,
                    Url = "https://example.com/orders",
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var collectionRoot = _temp.CreateSubDirectory("col-sub");
        var subFolder = Path.Combine(collectionRoot, "Orders");
        Directory.CreateDirectory(subFolder);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot, subFolder);

        // No request at root.
        Directory.GetFiles(collectionRoot, "*.callsmith").Should().BeEmpty();
        // Request in sub-folder.
        var subFiles = Directory.GetFiles(subFolder, "*.callsmith");
        subFiles.Should().HaveCount(1);
        Path.GetFileNameWithoutExtension(subFiles[0]).Should().Be("Post Order");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_DeduplicatesRequestFilenamesOnConflict()
    {
        // Pre-create a file with the same name.
        var collectionRoot = _temp.CreateSubDirectory("col-dedup");
        var existingFile = Path.Combine(collectionRoot, "Req.callsmith");
        File.WriteAllText(existingFile, "{}");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        // TakeBoth keeps the existing file and adds the new one with a counter suffix.
        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { MergeStrategy = ImportMergeStrategy.TakeBoth });

        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(2);
        files.Should().Contain(f => Path.GetFileName(f) == "Req (1).callsmith");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MergeStrategy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportIntoCollectionAsync_MergeStrategy_Skip_LeavesExistingRequestIntact()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-skip");
        var existingFile = Path.Combine(collectionRoot, "GetUsers.callsmith");
        File.WriteAllText(existingFile, "existing-content");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "GetUsers", Method = System.Net.Http.HttpMethod.Get, Url = "https://new.com/users" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { MergeStrategy = ImportMergeStrategy.Skip });

        // Only 1 file: the existing one is left unchanged; the import is skipped.
        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(1);
        File.ReadAllText(existingFile).Should().Be("existing-content");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_MergeStrategy_Skip_DefaultStrategy_SkipsConflictingRequests()
    {
        // Default (no options) = Skip.
        var collectionRoot = _temp.CreateSubDirectory("col-skip-default");
        var existingFile = Path.Combine(collectionRoot, "Req.callsmith");
        File.WriteAllText(existingFile, "original");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Req", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        // Default options → Skip
        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(1);
        File.ReadAllText(existingFile).Should().Be("original");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_MergeStrategy_Replace_OverwritesExistingRequest()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-replace");
        var existingFile = Path.Combine(collectionRoot, "GetUsers.callsmith");
        File.WriteAllText(existingFile, "{}");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "GetUsers", Method = System.Net.Http.HttpMethod.Post, Url = "https://new.com/users" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { MergeStrategy = ImportMergeStrategy.Replace });

        // Only 1 file: the old one was replaced with the new one.
        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(1);
        Path.GetFileNameWithoutExtension(files[0]).Should().Be("GetUsers");
        // Content should have been rewritten (not the original "{}").
        File.ReadAllText(files[0]).Should().NotBe("{}");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_MergeStrategy_Skip_StillWritesNonConflictingRequests()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-skip-new");
        var existingFile = Path.Combine(collectionRoot, "Existing.callsmith");
        File.WriteAllText(existingFile, "{}");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "Existing", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com" },
                new ImportedRequest { Name = "NewOne", Method = System.Net.Http.HttpMethod.Get, Url = "https://a.com/new" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { MergeStrategy = ImportMergeStrategy.Skip });

        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(2);
        files.Should().Contain(f => Path.GetFileName(f) == "Existing.callsmith");
        files.Should().Contain(f => Path.GetFileName(f) == "NewOne.callsmith");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BaseUrlVariableName
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportIntoCollectionAsync_BaseUrlVariableName_ReplacesDefaultPlaceholderInUrls()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-baseurl");

        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "GetUsers", Method = System.Net.Http.HttpMethod.Get, Url = "{{baseUrl}}/users" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { BaseUrlVariableName = "apiRoot" });

        var files = Directory.GetFiles(collectionRoot, "*.callsmith");
        files.Should().HaveCount(1);
        var written = await _collectionService.LoadRequestAsync(files[0]);
        written.Url.Should().Be("{{apiRoot}}/users");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_BaseUrlVariableName_RenamesEnvironmentVariable()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-baseurl-env");

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Prod",
                    Variables = new Dictionary<string, string>
                    {
                        ["baseUrl"] = "https://api.prod.example.com",
                        ["timeout"] = "30",
                    },
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync(
            "/fake.yaml", collectionRoot, null,
            new CollectionImportOptions { BaseUrlVariableName = "serviceUrl" });

        var environments = await _environmentService.ListEnvironmentsAsync(collectionRoot);
        var prod = environments.Single();
        prod.Variables.Should().Contain(v => v.Name == "serviceUrl" && v.Value == "https://api.prod.example.com");
        prod.Variables.Should().NotContain(v => v.Name == "baseUrl");
        prod.Variables.Should().Contain(v => v.Name == "timeout");
    }

    [Fact]
    public async Task ImportToFolderAsync_BaseUrlVariableName_ReplacesPlaceholderInUrls()
    {
        var collection = new ImportedCollection
        {
            Name = "Test",
            RootRequests =
            [
                new ImportedRequest { Name = "GetItems", Method = System.Net.Http.HttpMethod.Get, Url = "{{baseUrl}}/items" },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);
        var target = _temp.CreateSubDirectory("new-col-baseurl");

        await sut.ImportToFolderAsync(
            "/fake.yaml", target,
            new CollectionImportOptions { BaseUrlVariableName = "host" });

        var files = Directory.GetFiles(target, "*.callsmith");
        files.Should().HaveCount(1);
        var written = await _collectionService.LoadRequestAsync(files[0]);
        written.Url.Should().Be("{{host}}/items");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_SameNameEnv_AddsNewVariables()
    {
        // Pre-create the collection with an existing environment.
        var collectionRoot = _temp.CreateSubDirectory("col-merge-env");
        var envFolder = Path.Combine(collectionRoot, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var existingEnv = new EnvironmentModel
        {
            FilePath = Path.Combine(envFolder, "Dev.env.callsmith"),
            EnvironmentId = Guid.NewGuid(),
            Name = "Dev",
            Variables =
            [
                new EnvironmentVariable { Name = "Var1", Value = "a", VariableType = EnvironmentVariable.VariableTypes.Static },
                new EnvironmentVariable { Name = "Var2", Value = "b", VariableType = EnvironmentVariable.VariableTypes.Static },
            ],
        };
        await _environmentService.SaveEnvironmentAsync(existingEnv);

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Dev",
                    Variables = new Dictionary<string, string>
                    {
                        { "Var2", "c" },  // Already exists — must NOT be changed.
                        { "Var3", "d" },  // New — must be added.
                    },
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var saved = await _environmentService.LoadEnvironmentAsync(existingEnv.FilePath);
        saved.Variables.Should().HaveCount(3);
        saved.Variables.Should().Contain(v => v.Name == "Var1" && v.Value == "a");  // Preserved.
        saved.Variables.Should().Contain(v => v.Name == "Var2" && v.Value == "b");  // NOT changed.
        saved.Variables.Should().Contain(v => v.Name == "Var3" && v.Value == "d");  // Added.
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_SameNameEnv_PreservesVarsNotInImportFile()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-merge-preserve");
        var envFolder = Path.Combine(collectionRoot, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var existingEnv = new EnvironmentModel
        {
            FilePath = Path.Combine(envFolder, "Dev.env.callsmith"),
            EnvironmentId = Guid.NewGuid(),
            Name = "Dev",
            Variables =
            [
                new EnvironmentVariable { Name = "OnlyInExisting", Value = "keep-me", VariableType = EnvironmentVariable.VariableTypes.Static },
            ],
        };
        await _environmentService.SaveEnvironmentAsync(existingEnv);

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Dev",
                    Variables = new Dictionary<string, string>
                    {
                        { "OnlyInImport", "new-val" },
                    },
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var saved = await _environmentService.LoadEnvironmentAsync(existingEnv.FilePath);
        saved.Variables.Should().HaveCount(2);
        saved.Variables.Should().Contain(v => v.Name == "OnlyInExisting" && v.Value == "keep-me");
        saved.Variables.Should().Contain(v => v.Name == "OnlyInImport" && v.Value == "new-val");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_NewEnv_IsAddedAsAdditionalEnvironment()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-new-env");
        var envFolder = Path.Combine(collectionRoot, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var existingEnv = new EnvironmentModel
        {
            FilePath = Path.Combine(envFolder, "Env1.env.callsmith"),
            EnvironmentId = Guid.NewGuid(),
            Name = "Env1",
            Variables = [new EnvironmentVariable { Name = "Var1", Value = "a", VariableType = EnvironmentVariable.VariableTypes.Static }],
        };
        await _environmentService.SaveEnvironmentAsync(existingEnv);

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Env2",
                    Variables = new Dictionary<string, string> { { "Var4", "e" } },
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var allEnvs = await _environmentService.ListEnvironmentsAsync(collectionRoot);
        allEnvs.Should().HaveCount(2);
        allEnvs.Should().Contain(e => e.Name == "Env1");
        allEnvs.Should().Contain(e => e.Name == "Env2");

        var env2 = allEnvs.First(e => e.Name == "Env2");
        env2.Variables.Should().ContainSingle(v => v.Name == "Var4" && v.Value == "e");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_NewEnv_IsAppendedToOrderFile()
    {
        var collectionRoot = _temp.CreateSubDirectory("col-env-order");
        var envFolder = Path.Combine(collectionRoot, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        // Seed an existing environment and save its order.
        var existingEnv = new EnvironmentModel
        {
            FilePath = Path.Combine(envFolder, "Existing.env.callsmith"),
            EnvironmentId = Guid.NewGuid(),
            Name = "Existing",
            Variables = [],
        };
        await _environmentService.SaveEnvironmentAsync(existingEnv);
        await _environmentService.SaveEnvironmentOrderAsync(collectionRoot, ["Existing.env.callsmith"]);

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment { Name = "NewEnv", Variables = new Dictionary<string, string>() },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var orderFile = Path.Combine(envFolder, FileSystemEnvironmentService.MetaFileName);
        var json = await File.ReadAllTextAsync(orderFile);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var order = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            doc.RootElement.GetProperty("order").GetRawText())!;
        order[0].Should().StartWith("Existing");
        order[1].Should().StartWith("NewEnv");
    }

    [Fact]
    public async Task ImportIntoCollectionAsync_FullScenario_MatchesProblemStatementExample()
    {
        // Collection currently has: Env1 { Var1=a, Var2=b }
        // Import file has:          Env1 { Var2=c, Var3=d }, Env2 { Var4=e }
        // Expected result:          Env1 { Var1=a, Var2=b, Var3=d }, Env2 { Var4=e }

        var collectionRoot = _temp.CreateSubDirectory("col-full");
        var envFolder = Path.Combine(collectionRoot, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var env1 = new EnvironmentModel
        {
            FilePath = Path.Combine(envFolder, "Env1.env.callsmith"),
            EnvironmentId = Guid.NewGuid(),
            Name = "Env1",
            Variables =
            [
                new EnvironmentVariable { Name = "Var1", Value = "a", VariableType = EnvironmentVariable.VariableTypes.Static },
                new EnvironmentVariable { Name = "Var2", Value = "b", VariableType = EnvironmentVariable.VariableTypes.Static },
            ],
        };
        await _environmentService.SaveEnvironmentAsync(env1);

        var collection = new ImportedCollection
        {
            Name = "Test",
            Environments =
            [
                new ImportedEnvironment
                {
                    Name = "Env1",
                    Variables = new Dictionary<string, string> { { "Var2", "c" }, { "Var3", "d" } },
                },
                new ImportedEnvironment
                {
                    Name = "Env2",
                    Variables = new Dictionary<string, string> { { "Var4", "e" } },
                },
            ],
        };

        var importer = MakeImporter(canImport: true, extensions: [".yaml"], returns: collection);
        var sut = BuildSut(importer);

        await sut.ImportIntoCollectionAsync("/fake.yaml", collectionRoot);

        var allEnvs = await _environmentService.ListEnvironmentsAsync(collectionRoot);
        allEnvs.Should().HaveCount(2);

        var mergedEnv1 = allEnvs.First(e => e.Name == "Env1");
        mergedEnv1.Variables.Should().HaveCount(3);
        mergedEnv1.Variables.Should().Contain(v => v.Name == "Var1" && v.Value == "a");
        mergedEnv1.Variables.Should().Contain(v => v.Name == "Var2" && v.Value == "b");
        mergedEnv1.Variables.Should().Contain(v => v.Name == "Var3" && v.Value == "d");

        var addedEnv2 = allEnvs.First(e => e.Name == "Env2");
        addedEnv2.Variables.Should().ContainSingle(v => v.Name == "Var4" && v.Value == "e");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private CollectionImportService BuildSut(params ICollectionImporter[] importers) =>
        new(importers,
            _collectionService,
            _environmentService,
            NullLogger<CollectionImportService>.Instance);

    private static ICollectionImporter MakeImporter(
        bool canImport,
        IReadOnlyList<string> extensions,
        ImportedCollection? returns = null)
    {
        var mock = Substitute.For<ICollectionImporter>();
        mock.SupportedFileExtensions.Returns(extensions);
        mock.CanImportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(canImport));

        if (returns is not null)
        {
            mock.ImportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(returns));
        }

        return mock;
    }
}
