using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemCollectionService"/>.
/// Each test gets its own isolated temporary directory via <see cref="TempDirectory"/>,
/// which is deleted on disposal — no shared state between tests.
/// </summary>
public sealed class FileSystemCollectionServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>No-op secrets for tests that do not exercise auth-secret storage.</summary>
    private static ISecretStorageService NoOpSecrets() => Substitute.For<ISecretStorageService>();

    /// <summary>Returns a real secrets service backed by a fresh temp sub-directory.</summary>
    private FileSystemSecretStorageService RealSecrets() =>
        new(
            _temp.CreateSubDirectory("secrets-store"),
            new AesSecretEncryptionService(System.IO.Path.Combine(_temp.Path, "secrets.key")),
            NullLogger<FileSystemSecretStorageService>.Instance);

    private FileSystemCollectionService Sut(ISecretStorageService? secrets = null) =>
        new(secrets ?? NoOpSecrets(), NullLogger<FileSystemCollectionService>.Instance);

    // Shared instance for tests that don't exercise secret storage.
    private readonly FileSystemCollectionService _sut;

    public FileSystemCollectionServiceTests()
    {
        _sut = Sut();
    }

    public void Dispose() => _temp.Dispose();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Writes a minimal valid .callsmith file and returns its path.</summary>
    private string WriteRequestFile(
        string folder,
        string name,
        string method = "GET",
        string url = "https://example.com",
        string? body = null,
        string? bodyType = null,
        string? description = null,
        Dictionary<string, string>? headers = null)
    {
        var json = $$"""
            {
              "method": "{{method}}",
              "url": "{{url}}"
              {{(description is not null ? $", \"description\": \"{description}\"" : "")}}
              {{(bodyType is not null ? $", \"bodyType\": \"{bodyType}\"" : "")}}
              {{(body is not null ? $", \"body\": \"{body}\"" : "")}}
            }
            """;

        // Build headers inline if provided
        if (headers is { Count: > 0 })
        {
            var headerJson = string.Join(",\n", headers.Select(h => $"  \"{h.Key}\": \"{h.Value}\""));
            json = $$"""
                {
                  "method": "{{method}}",
                  "url": "{{url}}",
                  "headers": {
                {{headerJson}}
                  }
                }
                """;
        }

        var filePath = Path.Combine(folder, name + FileSystemCollectionService.RequestFileExtension);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    // -------------------------------------------------------------------------
    // OpenFolderAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenFolderAsync_WhenFolderDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var act = () => _sut.OpenFolderAsync(Path.Combine(_temp.Path, "missing"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task OpenFolderAsync_WhenFolderIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.OpenFolderAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task OpenFolderAsync_EmptyFolder_ReturnsEmptyCollection()
    {
        var folder = _temp.CreateSubDirectory("empty-collection");

        var result = await _sut.OpenFolderAsync(folder);

        result.FolderPath.Should().Be(folder);
        result.Name.Should().Be("empty-collection");
        result.Requests.Should().BeEmpty();
        result.SubFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFolderAsync_WithRequestFiles_ReturnsRequests()
    {
        var folder = _temp.CreateSubDirectory("collection");
        WriteRequestFile(folder, "get-users", method: "GET", url: "https://api.example.com/users");
        WriteRequestFile(folder, "create-user", method: "POST", url: "https://api.example.com/users");

        var result = await _sut.OpenFolderAsync(folder);

        result.Requests.Should().HaveCount(2);
        result.Requests.Select(r => r.Name).Should().BeEquivalentTo(["get-users", "create-user"]);
    }

    [Fact]
    public async Task OpenFolderAsync_WithSubFolders_ReturnsNestedStructure()
    {
        var root = _temp.CreateSubDirectory("root");
        var sub = Path.Combine(root, "auth");
        Directory.CreateDirectory(sub);
        WriteRequestFile(root, "health");
        WriteRequestFile(sub, "login");

        var result = await _sut.OpenFolderAsync(root);

        result.Requests.Should().HaveCount(1);
        result.SubFolders.Should().HaveCount(1);
        result.SubFolders[0].Name.Should().Be("auth");
        result.SubFolders[0].Requests.Should().HaveCount(1);
        result.SubFolders[0].Requests[0].Name.Should().Be("login");
    }

    [Fact]
    public async Task OpenFolderAsync_ExcludesEnvironmentSubFolder()
    {
        var root = _temp.CreateSubDirectory("root");
        var envFolder = Path.Combine(root, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var result = await _sut.OpenFolderAsync(root);

        result.SubFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFolderAsync_IgnoresNonCallsmithFiles()
    {
        var folder = _temp.CreateSubDirectory("collection");
        File.WriteAllText(Path.Combine(folder, "readme.md"), "# readme");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "notes");
        WriteRequestFile(folder, "ping");

        var result = await _sut.OpenFolderAsync(folder);

        result.Requests.Should().HaveCount(1);
        result.Requests[0].Name.Should().Be("ping");
    }

    [Fact]
    public async Task OpenFolderAsync_SkipsUnreadableFilesWithoutThrowing()
    {
        var folder = _temp.CreateSubDirectory("collection");
        File.WriteAllText(Path.Combine(folder, "corrupt.callsmith"), "not valid json {{{{");
        WriteRequestFile(folder, "valid");

        var result = await _sut.OpenFolderAsync(folder);

        // Corrupt file is skipped; valid file is returned.
        result.Requests.Should().HaveCount(1);
        result.Requests[0].Name.Should().Be("valid");
    }

    // -------------------------------------------------------------------------
    // LoadRequestAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadRequestAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => _sut.LoadRequestAsync(Path.Combine(_temp.Path, "missing.callsmith"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadRequestAsync_WhenFilePathIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.LoadRequestAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadRequestAsync_ReturnsFullyPopulatedRequest()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(
            folder, "get-users",
            method: "GET",
            url: "https://api.example.com/users",
            description: "Get all users");

        var result = await _sut.LoadRequestAsync(filePath);

        result.FilePath.Should().Be(filePath);
        result.Name.Should().Be("get-users");
        result.Method.Should().Be(HttpMethod.Get);
        result.Url.Should().Be("https://api.example.com/users");
        result.Description.Should().Be("Get all users");
    }

    [Fact]
    public async Task LoadRequestAsync_WithHeaders_ReturnsHeaders()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "auth-request",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer token123",
                ["Accept"] = "application/json",
            });

        var result = await _sut.LoadRequestAsync(filePath);

        result.Headers.Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer token123");
        result.Headers.Should().Contain(h => h.Key == "Accept" && h.Value == "application/json");
    }

    // -------------------------------------------------------------------------
    // SaveRequestAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveRequestAsync_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.SaveRequestAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveRequestAsync_WritesFileToDisk()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = Path.Combine(folder, "new-request.callsmith");
        var request = new CollectionRequest
        {
            FilePath = filePath,
            Name = "new-request",
            Method = HttpMethod.Post,
            Url = "https://api.example.com/items",
            Body = """{"name":"test"}""",
            BodyType = CollectionRequest.BodyTypes.Json,
        };

        await _sut.SaveRequestAsync(request);

        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveRequestAsync_CanBeReadBackWithLoadRequest()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = Path.Combine(folder, "roundtrip.callsmith");
        var original = new CollectionRequest
        {
            FilePath = filePath,
            Name = "roundtrip",
            Method = HttpMethod.Put,
            Url = "https://api.example.com/items/1",
            Description = "Update item",
            Headers = [new RequestKv("Content-Type", "application/json")],
            Body = """{"value":42}""",
            BodyType = CollectionRequest.BodyTypes.Json,
        };

        await _sut.SaveRequestAsync(original);
        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Name.Should().Be(original.Name);
        loaded.Method.Should().Be(original.Method);
        loaded.Url.Should().Be(original.Url);
        loaded.Description.Should().Be(original.Description);
        loaded.Body.Should().Be(original.Body);
        loaded.BodyType.Should().Be(original.BodyType);
        loaded.Headers.Should().Contain(h => h.Key == "Content-Type" && h.Value == "application/json");
    }

    [Fact]
    public async Task SaveRequestAsync_CreatesDirectoryIfMissing()
    {
        var newDir = Path.Combine(_temp.Path, "new", "nested", "dir");
        var filePath = Path.Combine(newDir, "request.callsmith");
        var request = new CollectionRequest
        {
            FilePath = filePath,
            Name = "request",
            Method = HttpMethod.Get,
            Url = "https://example.com",
        };

        await _sut.SaveRequestAsync(request);

        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveRequestAsync_OverwritesExistingFile()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "existing", url: "https://old.example.com");

        var updated = new CollectionRequest
        {
            FilePath = filePath,
            Name = "existing",
            Method = HttpMethod.Get,
            Url = "https://new.example.com",
        };

        await _sut.SaveRequestAsync(updated);
        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Url.Should().Be("https://new.example.com");
    }

    // -------------------------------------------------------------------------
    // DeleteRequestAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteRequestAsync_WhenFilePathIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.DeleteRequestAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeleteRequestAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => _sut.DeleteRequestAsync(Path.Combine(_temp.Path, "ghost.callsmith"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteRequestAsync_RemovesFileFromDisk()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "to-delete");

        await _sut.DeleteRequestAsync(filePath);

        File.Exists(filePath).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // DeleteFolderAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteFolderAsync_WhenFolderPathIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.DeleteFolderAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFolderDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var act = () => _sut.DeleteFolderAsync(Path.Combine(_temp.Path, "ghost"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task DeleteFolderAsync_DeletesFolderAndAllContentsRecursively()
    {
        var folder = _temp.CreateSubDirectory("to-delete");
        var sub = Path.Combine(folder, "sub");
        Directory.CreateDirectory(sub);
        WriteRequestFile(folder, "req-root");
        WriteRequestFile(sub, "req-sub");

        await _sut.DeleteFolderAsync(folder);

        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var folder = _temp.CreateSubDirectory("col");
        var ct = new CancellationToken(canceled: true);

        var act = () => _sut.DeleteFolderAsync(folder, ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(folder).Should().BeTrue(); // folder was NOT deleted
    }

    // -------------------------------------------------------------------------
    // RenameRequestAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RenameRequestAsync_WhenFilePathIsNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.RenameRequestAsync(null!, "new-name");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RenameRequestAsync_WhenNewNameIsNull_ThrowsArgumentNullException()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "original");

        var act = () => _sut.RenameRequestAsync(filePath, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RenameRequestAsync_WhenNewNameIsWhitespace_ThrowsArgumentException()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "original");

        var act = () => _sut.RenameRequestAsync(filePath, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenameRequestAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => _sut.RenameRequestAsync(
            Path.Combine(_temp.Path, "ghost.callsmith"), "new-name");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task RenameRequestAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException()
    {
        var folder = _temp.CreateSubDirectory("col");
        WriteRequestFile(folder, "original");
        WriteRequestFile(folder, "taken");

        var act = () => _sut.RenameRequestAsync(
            Path.Combine(folder, "original.callsmith"), "taken");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RenameRequestAsync_RenamesFileAndUpdatesNameAndFilePath()
    {
        var folder = _temp.CreateSubDirectory("col");
        var originalPath = WriteRequestFile(folder, "original", url: "https://example.com");

        var result = await _sut.RenameRequestAsync(originalPath, "renamed");

        result.Name.Should().Be("renamed");
        result.FilePath.Should().EndWith("renamed.callsmith");
        result.Url.Should().Be("https://example.com");
        File.Exists(originalPath).Should().BeFalse();
        File.Exists(result.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task MoveRequestAsync_WhenDestinationFolderDoesNotExist_MovesFile()
    {
        var source = _temp.CreateSubDirectory("col");
        var destination = Path.Combine(_temp.Path, "col", "target");
        var filePath = WriteRequestFile(source, "req", url: "https://example.com");

        var moved = await _sut.MoveRequestAsync(filePath, destination);

        moved.Name.Should().Be("req");
        moved.FilePath.Should().StartWith(destination);
        File.Exists(filePath).Should().BeFalse();
        File.Exists(moved.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task MoveRequestAsync_WhenDestinationAlreadyHasFile_ThrowsInvalidOperationException()
    {
        var source = _temp.CreateSubDirectory("col");
        var destination = Path.Combine(_temp.Path, "col", "target");
        Directory.CreateDirectory(destination);

        var sourceFile = WriteRequestFile(source, "req");
        WriteRequestFile(destination, "req");

        var act = () => _sut.MoveRequestAsync(sourceFile, destination);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MoveRequestAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var source = Path.Combine(_temp.Path, "col", "missing.callsmith");
        var destination = Path.Combine(_temp.Path, "col", "target");

        var act = () => _sut.MoveRequestAsync(source, destination);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // -------------------------------------------------------------------------
    // MoveFolderAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MoveFolderAsync_MovesDirectoryToNewParent()
    {
        var parent = _temp.CreateSubDirectory("col");
        var source = _temp.CreateSubDirectory(Path.Combine("col", "auth"));
        var destination = _temp.CreateSubDirectory(Path.Combine("col", "api"));
        WriteRequestFile(source, "login");

        var result = await _sut.MoveFolderAsync(source, destination);

        result.Name.Should().Be("auth");
        result.FolderPath.Should().Be(Path.Combine(destination, "auth"));
        Directory.Exists(source).Should().BeFalse();
        Directory.Exists(result.FolderPath).Should().BeTrue();
        File.Exists(Path.Combine(result.FolderPath, "login.callsmith")).Should().BeTrue();
    }

    [Fact]
    public async Task MoveFolderAsync_WhenFolderDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var destination = _temp.CreateSubDirectory("dest");
        var source = Path.Combine(_temp.Path, "nonexistent");

        var act = () => _sut.MoveFolderAsync(source, destination);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task MoveFolderAsync_WhenDestinationDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var source = _temp.CreateSubDirectory("auth");
        var destination = Path.Combine(_temp.Path, "nonexistent");

        var act = () => _sut.MoveFolderAsync(source, destination);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task MoveFolderAsync_WhenFolderNameAlreadyExistsInDestination_ThrowsInvalidOperationException()
    {
        var source = _temp.CreateSubDirectory("auth");
        var destination = _temp.CreateSubDirectory("api");
        _temp.CreateSubDirectory(Path.Combine("api", "auth")); // conflict

        var act = () => _sut.MoveFolderAsync(source, destination);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -------------------------------------------------------------------------
    // SaveRequestAsync / LoadRequestAsync — new fields (QueryParams, Auth)
    // -------------------------------------------------------------------------


    [Fact]
    public async Task SaveAndLoad_WithQueryParams_RoundTrips()
    {
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            QueryParams = [new("limit", "10"), new("offset", "0")],
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.QueryParams.Should().BeEquivalentTo(request.QueryParams);
        loaded.Url.Should().Be("https://api.example.com");
    }

    [Fact]
    public async Task SaveAndLoad_WithDuplicateQueryParamKeys_PreservesAll()
    {
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "dups.callsmith"),
            Name = "dups",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/roles",
            QueryParams =
            [
                new("roleNames", "ADMIN"),
                new("roleNames", "MANAGER"),
                new("roleNames", "VIEWER"),
            ],
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.QueryParams.Should().HaveCount(3);
        loaded.QueryParams.Select(p => p.Key).Should().AllBe("roleNames");
        loaded.QueryParams.Select(p => p.Value).Should().BeEquivalentTo(["ADMIN", "MANAGER", "VIEWER"]);
    }

    [Fact]
    public async Task SaveAndLoad_WithPathParams_RoundTrips()
    {
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/{id}/orders/{orderId}",
            PathParams = new Dictionary<string, string>
            {
                ["id"] = "42",
                ["orderId"] = "abc-123",
            },
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.PathParams.Should().BeEquivalentTo(request.PathParams);
        loaded.Url.Should().Be("https://api.example.com/users/{id}/orders/{orderId}");
    }

    [Fact]
    public async Task SaveAndLoad_BearerAuth_RoundTrips()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-bearer");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Bearer, Token = "my-token" },
        };

        await sut.SaveRequestAsync(request);

        // The on-disk file must not contain the plain-text token.
        var json = await File.ReadAllTextAsync(request.FilePath);
        json.Should().NotContain("my-token");

        var loaded = await sut.LoadRequestAsync(request.FilePath);
        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        loaded.Auth.Token.Should().Be("my-token");
    }

    [Fact]
    public async Task SaveAndLoad_BasicAuth_PasswordIsStoredInSecretsNotInFile()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-basic-redact");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "user",
                Password = "s3cret",
            },
        };

        await sut.SaveRequestAsync(request);

        // The on-disk file must not contain the plain-text password.
        var json = await File.ReadAllTextAsync(request.FilePath);
        json.Should().NotContain("s3cret");
    }

    [Fact]
    public async Task SaveAndLoad_BasicAuth_RoundTrips()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-basic");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "user",
                Password = "s3cret",
            },
        };

        await sut.SaveRequestAsync(request);
        var loaded = await sut.LoadRequestAsync(request.FilePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        loaded.Auth.Username.Should().Be("user");
        loaded.Auth.Password.Should().Be("s3cret");
    }

    [Fact]
    public async Task SaveAndLoad_ApiKeyAuthInQuery_RoundTrips()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-apikey-query");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "api_key",
                ApiKeyValue = "secret",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Query,
            },
        };

        await sut.SaveRequestAsync(request);
        var loaded = await sut.LoadRequestAsync(request.FilePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        loaded.Auth.ApiKeyName.Should().Be("api_key");
        loaded.Auth.ApiKeyValue.Should().Be("secret");
        loaded.Auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Query);
    }

    [Fact]
    public async Task SaveAndLoad_ApiKeyAuth_ValueIsStoredInSecretsNotInFile()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-apikey-redact");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "X-Api-Key",
                ApiKeyValue = "supersecret123",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
            },
        };

        await sut.SaveRequestAsync(request);

        // The on-disk file must not contain the plain-text API key value.
        var json = await File.ReadAllTextAsync(request.FilePath);
        json.Should().NotContain("supersecret123");
    }

    [Fact]
    public async Task SaveAndLoad_NoAuth_DefaultsToInherit()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "req");

        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        loaded.Auth.Token.Should().BeNull();
    }

    [Fact]
    public async Task LegacyFile_WithQueryParamsInUrl_PreservesUrlAsIs()
    {
        var folder = _temp.CreateSubDirectory("col");
        // Legacy format: full URL including query params, no separate queryParams field.
        // The URL is preserved verbatim — embedded query params are NOT extracted into QueryParams.
        var json = """{"method": "GET", "url": "https://api.example.com?foo=bar&limit=5"}""";
        var filePath = Path.Combine(folder, "legacy.callsmith");
        await File.WriteAllTextAsync(filePath, json);

        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Url.Should().Be("https://api.example.com?foo=bar&limit=5");
        loaded.QueryParams.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadRequestAsync_LegacyFileWithoutRequestId_BackfillsAndPersistsRequestId()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "legacy");

        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.RequestId.Should().NotBeNull();

        var fileContents = await File.ReadAllTextAsync(filePath);
        fileContents.Should().Contain("requestId");
        fileContents.Should().Contain(loaded.RequestId!.Value.ToString());
    }

    [Fact]
    public async Task CreateRequestAsync_AssignsStableRequestIdImmediately()
    {
        var folder = _temp.CreateSubDirectory("col");

        var created = await _sut.CreateRequestAsync(folder, "new-request");

        created.RequestId.Should().NotBeNull();

        var reloaded = await _sut.LoadRequestAsync(created.FilePath);
        reloaded.RequestId.Should().Be(created.RequestId);
    }

    [Fact]
    public async Task SaveAndLoad_WithDisabledQueryParam_PreservesEnabledState()
    {
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            QueryParams =
            [
                new RequestKv("active", "true"),
                new RequestKv("debug", "1", IsEnabled: false),
            ],
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.QueryParams.Should().HaveCount(2);
        loaded.QueryParams.Should().Contain(p => p.Key == "active" && p.IsEnabled);
        loaded.QueryParams.Should().Contain(p => p.Key == "debug" && !p.IsEnabled);
        // Disabled param must not appear in FullUrl
        loaded.FullUrl.Should().NotContain("debug");
        loaded.FullUrl.Should().Contain("active=true");
    }

    [Fact]
    public async Task SaveAndLoad_WithDisabledHeader_PreservesEnabledState()
    {
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Headers =
            [
                new RequestKv("Authorization", "Bearer token"),
                new RequestKv("X-Debug", "true", IsEnabled: false),
            ],
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.Headers.Should().HaveCount(2);
        loaded.Headers.Should().Contain(h => h.Key == "Authorization" && h.IsEnabled);
        loaded.Headers.Should().Contain(h => h.Key == "X-Debug" && !h.IsEnabled);
    }

    [Fact]
    public async Task RenameRequest_BasicAuth_SecretsStillAccessibleAfterRename()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-rename-basic");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            RequestId = Guid.NewGuid(),
            FilePath = Path.Combine(folder, "original.callsmith"),
            Name = "original",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "user",
                Password = "s3cret",
            },
        };

        await sut.SaveRequestAsync(request);

        // Rename the request file.
        var renamed = await sut.RenameRequestAsync(request.FilePath, "renamed");

        // The secret must still be accessible under the new file path.
        var loaded = await sut.LoadRequestAsync(renamed.FilePath);
        loaded.Auth.Password.Should().Be("s3cret");
    }

    [Fact]
    public async Task RenameRequest_ApiKeyAuth_SecretsStillAccessibleAfterRename()
    {
        var sut = Sut(RealSecrets());
        var folder = _temp.CreateSubDirectory("col-rename-apikey");
        await sut.OpenFolderAsync(folder);

        var request = new CollectionRequest
        {
            RequestId = Guid.NewGuid(),
            FilePath = Path.Combine(folder, "original.callsmith"),
            Name = "original",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "X-Api-Key",
                ApiKeyValue = "supersecret123",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
            },
        };

        await sut.SaveRequestAsync(request);

        // Rename the request file.
        var renamed = await sut.RenameRequestAsync(request.FilePath, "renamed");

        // The secret must still be accessible under the new file path.
        var loaded = await sut.LoadRequestAsync(renamed.FilePath);
        loaded.Auth.ApiKeyValue.Should().Be("supersecret123");
    }

    // -------------------------------------------------------------------------
    // File body round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveAndLoad_FileBodyType_RoundTrips()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = Path.Combine(folder, "file-req.callsmith");
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var original = new CollectionRequest
        {
            FilePath = filePath,
            Name = "file-req",
            Method = HttpMethod.Post,
            Url = "https://example.com/upload",
            BodyType = CollectionRequest.BodyTypes.File,
            FileBodyBase64 = Convert.ToBase64String(bytes),
            FileBodyName = "data.bin",
        };

        await _sut.SaveRequestAsync(original);
        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.BodyType.Should().Be(CollectionRequest.BodyTypes.File);
        loaded.FileBodyName.Should().Be("data.bin");
        loaded.FileBodyBase64.Should().Be(Convert.ToBase64String(bytes));
        Convert.FromBase64String(loaded.FileBodyBase64!).Should().Equal(bytes);
    }

    [Fact]
    public async Task SaveAndLoad_MultipartFileParams_RoundTrip()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = Path.Combine(folder, "multipart-files.callsmith");
        var original = new CollectionRequest
        {
            FilePath = filePath,
            Name = "multipart-files",
            Method = HttpMethod.Post,
            Url = "https://example.com/upload",
            BodyType = CollectionRequest.BodyTypes.Multipart,
            FormParams = [new KeyValuePair<string, string>("label", "docs")],
            MultipartFormFiles =
            [
                new MultipartFilePart
                {
                    Key = "attachment",
                    FileName = "a.bin",
                    FilePath = "/tmp/a.bin",
                    FileBytes = [0xAB, 0xCD],
                },
            ],
        };

        await _sut.SaveRequestAsync(original);
        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.BodyType.Should().Be(CollectionRequest.BodyTypes.Multipart);
        loaded.FormParams.Should().ContainSingle(p => p.Key == "label" && p.Value == "docs");
        loaded.MultipartFormFiles.Should().ContainSingle();
        loaded.MultipartFormFiles[0].Key.Should().Be("attachment");
        loaded.MultipartFormFiles[0].FileName.Should().Be("a.bin");
        loaded.MultipartFormFiles[0].FilePath.Should().Be("/tmp/a.bin");
        loaded.MultipartFormFiles[0].FileBytes.Should().Equal([0xAB, 0xCD]);
    }

    [Theory]
    [InlineData(CollectionRequest.BodyTypes.Yaml, "key: value")]
    [InlineData(CollectionRequest.BodyTypes.Other, "custom payload")]
    public async Task SaveAndLoad_NewTextBodyTypes_RoundTrip(string bodyType, string body)
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = Path.Combine(folder, $"req-{bodyType}.callsmith");
        var original = new CollectionRequest
        {
            FilePath = filePath,
            Name = $"req-{bodyType}",
            Method = HttpMethod.Post,
            Url = "https://example.com",
            BodyType = bodyType,
            Body = body,
        };

        await _sut.SaveRequestAsync(original);
        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.BodyType.Should().Be(bodyType);
        loaded.Body.Should().Be(body);
    }
}
