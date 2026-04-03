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

        var orderFile = Path.Combine(target, "_order.json");
        File.Exists(orderFile).Should().BeTrue();

        var written = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            await File.ReadAllTextAsync(orderFile));
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
    public async Task ImportToFolderAsync_OrderFileReflectsRenamedDuplicates()
    {
        // Two requests with the same name plus an explicit ItemOrder — the order file
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

        var orderFile = Path.Combine(target, "_order.json");
        File.Exists(orderFile).Should().BeTrue();

        var written = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
            await File.ReadAllTextAsync(orderFile));
        written.Should().HaveCount(2);
        written.Should().Contain("Req.callsmith");
        written.Should().Contain("Req (1).callsmith");
    }

    [Fact]
    public async Task ImportToFolderAsync_PersistsEnvironmentOrderToOrderFile()
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

        // Order is written to environment/_order.json inside the collection folder.
        var orderFile = Path.Combine(target,
            FileSystemCollectionService.EnvironmentFolderName,
            FileSystemEnvironmentService.OrderFileName);
        File.Exists(orderFile).Should().BeTrue();

        var json = await File.ReadAllTextAsync(orderFile);
        var order = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json)!;
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
            new System.Net.Http.HttpClient(),
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
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private CollectionImportService BuildSut(params ICollectionImporter[] importers) =>
        new(importers,
            _collectionService,
            _environmentService,
            new System.Net.Http.HttpClient(),
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
