using System.Net.Http;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemCollectionService"/>.
/// Each test gets its own isolated temporary directory via <see cref="TempDirectory"/>,
/// which is deleted on disposal — no shared state between tests.
/// </summary>
public sealed class FileSystemCollectionServiceTests : IDisposable
{
    private readonly FileSystemCollectionService _sut =
        new(NullLogger<FileSystemCollectionService>.Instance);

    private readonly TempDirectory _temp = new();

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

        result.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer token123");
        result.Headers.Should().ContainKey("Accept").WhoseValue.Should().Be("application/json");
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
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
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
        loaded.Headers.Should().ContainKey("Content-Type").WhoseValue.Should().Be("application/json");
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
            QueryParams = new Dictionary<string, string> { ["limit"] = "10", ["offset"] = "0" },
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.QueryParams.Should().BeEquivalentTo(request.QueryParams);
        loaded.Url.Should().Be("https://api.example.com");
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
        var folder = _temp.CreateSubDirectory("col");
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(folder, "req.callsmith"),
            Name = "req",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Bearer, Token = "my-token" },
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        loaded.Auth.Token.Should().Be("my-token");
    }

    [Fact]
    public async Task SaveAndLoad_BasicAuth_RoundTrips()
    {
        var folder = _temp.CreateSubDirectory("col");
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
                Password = "pass",
            },
        };

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        loaded.Auth.Username.Should().Be("user");
        loaded.Auth.Password.Should().Be("pass");
    }

    [Fact]
    public async Task SaveAndLoad_ApiKeyAuthInQuery_RoundTrips()
    {
        var folder = _temp.CreateSubDirectory("col");
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

        await _sut.SaveRequestAsync(request);
        var loaded = await _sut.LoadRequestAsync(request.FilePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        loaded.Auth.ApiKeyName.Should().Be("api_key");
        loaded.Auth.ApiKeyValue.Should().Be("secret");
        loaded.Auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Query);
    }

    [Fact]
    public async Task SaveAndLoad_NoAuth_DefaultsToNone()
    {
        var folder = _temp.CreateSubDirectory("col");
        var filePath = WriteRequestFile(folder, "req");

        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.None);
        loaded.Auth.Token.Should().BeNull();
    }

    [Fact]
    public async Task LegacyFile_WithQueryParamsInUrl_ParsedIntoQueryParamsCollection()
    {
        var folder = _temp.CreateSubDirectory("col");
        // Legacy format: full URL including query params, no separate queryParams field
        var json = """{"method": "GET", "url": "https://api.example.com?foo=bar&limit=5"}""";
        var filePath = Path.Combine(folder, "legacy.callsmith");
        await File.WriteAllTextAsync(filePath, json);

        var loaded = await _sut.LoadRequestAsync(filePath);

        loaded.Url.Should().Be("https://api.example.com");
        loaded.QueryParams.Should().ContainKey("foo").WhoseValue.Should().Be("bar");
        loaded.QueryParams.Should().ContainKey("limit").WhoseValue.Should().Be("5");
    }
}
