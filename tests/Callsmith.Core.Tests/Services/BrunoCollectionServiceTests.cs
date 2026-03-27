using System.Linq;
using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Integration-style tests for <see cref="BrunoCollectionService"/> that operate on
/// a temporary directory containing real <c>.bru</c> files.
/// </summary>
public sealed class BrunoCollectionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BrunoCollectionService _sut;

    public BrunoCollectionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BrunoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sut = Sut();
    }

    /// <summary>No-op secrets for tests that do not exercise auth-secret storage.</summary>
    private static ISecretStorageService NoOpSecrets() => Substitute.For<ISecretStorageService>();

    /// <summary>Returns a real secrets service backed by a dedicated temp sub-directory.</summary>
    private FileSystemSecretStorageService RealSecrets() =>
        new(
            Path.Combine(_root, "__secrets_store__"),
            new AesSecretEncryptionService(Path.Combine(_root, "secrets.key")),
            NullLogger<FileSystemSecretStorageService>.Instance);

    private BrunoCollectionService Sut(ISecretStorageService? secrets = null) =>
        new(secrets ?? NoOpSecrets(), NullLogger<BrunoCollectionService>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ─────────────────────────────────────────────────────────────────────────
    //  OpenFolderAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenFolderAsync_EmptyFolder_ReturnsEmptyCollection()
    {
        var folder = await _sut.OpenFolderAsync(_root);

        Assert.NotNull(folder);
        Assert.Empty(folder.Requests);
        Assert.Empty(folder.SubFolders);
    }

    [Fact]
    public async Task OpenFolderAsync_WithBruFiles_LoadsRequestsOrderedBySeq()
    {
        WriteFile("b.bru", BruFile("b", "get", "https://b.com", seq: 2));
        WriteFile("a.bru", BruFile("a", "get", "https://a.com", seq: 1));
        WriteFile("c.bru", BruFile("c", "get", "https://c.com", seq: 3));

        var folder = await _sut.OpenFolderAsync(_root);

        Assert.Equal(3, folder.Requests.Count);
        Assert.Equal("a", folder.Requests[0].Name);
        Assert.Equal("b", folder.Requests[1].Name);
        Assert.Equal("c", folder.Requests[2].Name);
    }

    [Fact]
    public async Task OpenFolderAsync_ExcludesFolderDotBruAndCollectionDotBru()
    {
        WriteFile("folder.bru", "meta {\n  name: TestFolder\n}\n");
        WriteFile("collection.bru", "meta {\n  name: Collection\n}\n\nauth {\n  mode: none\n}\n");
        WriteFile("actual-request.bru", BruFile("actual-request", "get", "https://example.com", seq: 1));

        var folder = await _sut.OpenFolderAsync(_root);

        Assert.Single(folder.Requests);
        Assert.Equal("actual-request", folder.Requests[0].Name);
    }

    [Fact]
    public async Task OpenFolderAsync_SubFolder_IncludesNestedRequests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "SubFolder"));
        WriteFile("SubFolder/folder.bru", "meta {\n  name: SubFolder\n}\n");
        WriteFile("SubFolder/nested.bru", BruFile("nested", "post", "https://sub.com", seq: 1));

        var folder = await _sut.OpenFolderAsync(_root);

        Assert.Single(folder.SubFolders);
        Assert.Equal("SubFolder", folder.SubFolders[0].Name);
        Assert.Single(folder.SubFolders[0].Requests);
    }

    [Fact]
    public async Task OpenFolderAsync_ExcludesEnvironmentsFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "environments"));
        WriteFile("environments/Dev.bru", "vars {\n  url: https://dev.example.com\n}\n");
        WriteFile("request.bru", BruFile("request", "get", "https://example.com", seq: 1));

        var folder = await _sut.OpenFolderAsync(_root);

        Assert.Single(folder.Requests);
        Assert.Empty(folder.SubFolders);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LoadRequestAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadRequestAsync_GetRequest_MapsAllFields()
    {
        var content = """
            meta {
              name: get items
              type: http
              seq: 1
            }

            get {
              url: https://api.example.com/items?filter=active
              body: none
              auth: none
            }

            headers {
              Authorization: Bearer {{token}}
              Accept: application/json
            }

            params:query {
              filter: active
            }
            """;
        WriteFile("req.bru", content);
        var filePath = Path.Combine(_root, "req.bru");

        var request = await _sut.LoadRequestAsync(filePath);

        Assert.Equal("get items", request.Name);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.example.com/items", request.Url);
        Assert.Equal("active", request.QueryParams.First(p => p.Key == "filter").Value);
        Assert.Equal("Bearer {{token}}", request.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.Equal("application/json", request.Headers.Single(h => h.Key == "Accept").Value);
    }

    [Fact]
    public async Task LoadRequestAsync_JsonBody_ReadsRawContent()
    {
        var content = """
            meta {
              name: create item
              type: http
              seq: 1
            }

            post {
              url: https://api.example.com/items
              body: json
              auth: none
            }

            body:json {
              {"name": "test", "value": 42}
            }
            """;
        WriteFile("post.bru", content);

        var request = await _sut.LoadRequestAsync(Path.Combine(_root, "post.bru"));

        Assert.Equal(CollectionRequest.BodyTypes.Json, request.BodyType);
        Assert.NotNull(request.Body);
        Assert.Contains("\"name\": \"test\"", request.Body);
    }

    [Fact]
    public async Task LoadRequestAsync_BearerAuth_MapsToken()
    {
        var content = """
            meta {
              name: secured
              type: http
              seq: 1
            }

            get {
              url: https://api.example.com/secure
              body: none
              auth: bearer
            }

            auth:bearer {
              token: {{access-token}}
            }
            """;
        WriteFile("secured.bru", content);

        var request = await _sut.LoadRequestAsync(Path.Combine(_root, "secured.bru"));

        Assert.Equal(AuthConfig.AuthTypes.Bearer, request.Auth.AuthType);
        Assert.Equal("{{access-token}}", request.Auth.Token);
    }

    [Fact]
    public async Task LoadRequestAsync_FormBody_PopulatesFormParams()
    {
        var content = """
            meta {
              name: login
              type: http
              seq: 1
            }

            post {
              url: https://auth.example.com/token
              body: formUrlEncoded
              auth: none
            }

            body:form-urlencoded {
              grant_type: password
              username: user@example.com
            }
            """;
        WriteFile("login.bru", content);

        var request = await _sut.LoadRequestAsync(Path.Combine(_root, "login.bru"));

        Assert.Equal(CollectionRequest.BodyTypes.Form, request.BodyType);
        Assert.Null(request.Body);
        Assert.Equal(2, request.FormParams.Count);
        Assert.Contains(request.FormParams, p => p.Key == "grant_type" && p.Value == "password");
        Assert.Contains(request.FormParams, p => p.Key == "username" && p.Value == "user@example.com");
    }

      [Fact]
      public async Task LoadRequestAsync_WhenBruFileHasNoRequestId_DoesNotBackfillOrModifyFile()
      {
        var original = BruFile("legacy", "get", "https://example.com", seq: 1);
        WriteFile("legacy.bru", original);
        var filePath = Path.Combine(_root, "legacy.bru");

        await _sut.LoadRequestAsync(filePath);

        // File must not have been modified (no requestId backfill).
        var content = await File.ReadAllTextAsync(filePath);
        Assert.DoesNotContain("requestId:", content);
        Assert.Equal(original, content);
      }

    // ─────────────────────────────────────────────────────────────────────────
    //  SaveRequestAsync — round-trip fidelity
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveRequestAsync_PreservesScriptBlocks()
    {
        var original = """
            meta {
              name: with script
              type: http
              seq: 1
            }

            post {
              url: https://api.example.com/entries
              body: json
              auth: none
            }

            body:json {
              {"key": "value"}
            }

            script:pre-request {
              bru.setGlobalEnvVar('ts', Date.now());
            }

            tests {
              pm.globals.unset('ts');
            }
            """;
        WriteFile("scripted.bru", original);
        var filePath = Path.Combine(_root, "scripted.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        var modified = new CollectionRequest
        {
            FilePath = loaded.FilePath,
            Name = loaded.Name,
            Method = loaded.Method,
            Url = "https://api.example.com/entries/updated",
            Headers = loaded.Headers,
            PathParams = loaded.PathParams,
            QueryParams = loaded.QueryParams,
            BodyType = loaded.BodyType,
            Body = loaded.Body,
            Auth = loaded.Auth,
        };
        await _sut.SaveRequestAsync(modified);

        var written = await File.ReadAllTextAsync(filePath);
        Assert.Contains("script:pre-request", written);
        Assert.Contains("setGlobalEnvVar", written);
        Assert.Contains("tests", written);
        Assert.Contains("pm.globals.unset", written);
    }

    [Fact]
    public async Task SaveRequestAsync_PreservesDisabledHeaders()
    {
        var original = """
            meta {
              name: with disabled
              type: http
              seq: 1
            }

            get {
              url: https://api.example.com/items
              body: none
              auth: none
            }

            headers {
              Authorization: Bearer {{token}}
              ~nep-organization: {{o}}
            }
            """;
        WriteFile("disabled.bru", original);
        var filePath = Path.Combine(_root, "disabled.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        await _sut.SaveRequestAsync(loaded);

        var written = await File.ReadAllTextAsync(filePath);
        Assert.Contains("  ~nep-organization: {{o}}", written);
    }

    [Fact]
    public async Task SaveRequestAsync_NewFile_WritesValidBruFormat()
    {
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(_root, "new.bru"),
            Name = "new request",
            Method = HttpMethod.Post,
            Url = "https://api.example.com/create",
            Headers = [new RequestKv("Content-Type", "application/json")],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = @"{""name"": ""test""}",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        await _sut.SaveRequestAsync(request);

        var written = await File.ReadAllTextAsync(request.FilePath);
        Assert.Contains("meta {", written);
        Assert.Contains("  name: new request", written);
        Assert.Contains("post {", written);
        Assert.Contains("body:json {", written);
        Assert.Contains("\"name\"", written);
    }

    [Fact]
    public async Task SaveRequestAsync_DoesNotWriteRequestId()
    {
        var request = new CollectionRequest
        {
            RequestId = Guid.NewGuid(),
            FilePath = Path.Combine(_root, "api.bru"),
            Name = "api",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        await _sut.SaveRequestAsync(request);

        var content = await File.ReadAllTextAsync(request.FilePath);
        Assert.DoesNotContain("requestId:", content);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Rename / Delete / Create
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameRequestAsync_UpdatesFileNameAndMetaName()
    {
        WriteFile("old-name.bru", BruFile("old-name", "get", "https://example.com", seq: 1));
        var oldPath = Path.Combine(_root, "old-name.bru");

        var renamed = await _sut.RenameRequestAsync(oldPath, "new-name");

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(renamed.FilePath));
        Assert.Equal("new-name", renamed.Name);

        var content = await File.ReadAllTextAsync(renamed.FilePath);
        Assert.Contains("  name: new-name", content);
    }

    [Fact]
    public async Task MoveRequestAsync_WhenDestinationFolderDoesNotExist_MovesFile()
    {
        var destination = Path.Combine(_root, "other");
        WriteFile("req.bru", BruFile("req", "get", "https://example.com", seq: 1));
        var sourcePath = Path.Combine(_root, "req.bru");

        var moved = await _sut.MoveRequestAsync(sourcePath, destination);

        Assert.Equal("req", moved.Name);
        Assert.Equal(Path.Combine(destination, "req.bru"), moved.FilePath);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(moved.FilePath));
    }

    [Fact]
    public async Task MoveRequestAsync_WhenDestinationAlreadyHasFile_ThrowsInvalidOperationException()
    {
        var destination = Path.Combine(_root, "other");
        Directory.CreateDirectory(destination);
        WriteFile("req.bru", BruFile("req", "get", "https://example.com", seq: 1));
        WriteFile("other/req.bru", BruFile("req", "get", "https://example.com", seq: 1));

        var sourcePath = Path.Combine(_root, "req.bru");
        var act = () => _sut.MoveRequestAsync(sourcePath, destination);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task MoveRequestAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => _sut.MoveRequestAsync(Path.Combine(_root, "missing.bru"), Path.Combine(_root, "other"));

        await Assert.ThrowsAsync<FileNotFoundException>(act);
    }

    [Fact]
    public async Task CreateRequestAsync_WritesValidBruFile()
    {
        var created = await _sut.CreateRequestAsync(_root, "Brand New");

        Assert.True(File.Exists(created.FilePath));
        Assert.Equal("Brand New", created.Name);

        var content = await File.ReadAllTextAsync(created.FilePath);
        Assert.Contains("name: Brand New", content);
        Assert.DoesNotContain("requestId:", content);
        Assert.Contains("get {", content);
    }

    [Fact]
    public async Task DeleteRequestAsync_RemovesFile()
    {
        WriteFile("to-delete.bru", BruFile("to-delete", "get", "https://example.com", seq: 1));
        var filePath = Path.Combine(_root, "to-delete.bru");

        await _sut.DeleteRequestAsync(filePath);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFolderPathIsNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _sut.DeleteFolderAsync(null!));
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFolderDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _sut.DeleteFolderAsync(Path.Combine(_root, "ghost")));
    }

    [Fact]
    public async Task DeleteFolderAsync_DeletesFolderAndAllContentsRecursively()
    {
        var sub = Path.Combine(_root, "sub-to-delete");
        Directory.CreateDirectory(sub);
        WriteFile(Path.Combine("sub-to-delete", "req.bru"),
            BruFile("req", "get", "https://example.com", seq: 1));

        await _sut.DeleteFolderAsync(sub);

        Assert.False(Directory.Exists(sub));
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var sub = Path.Combine(_root, "cancel-sub");
        Directory.CreateDirectory(sub);
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.DeleteFolderAsync(sub, ct));

        Assert.True(Directory.Exists(sub)); // folder was NOT deleted
    }

    [Fact]
    public async Task CreateFolderAsync_CreatesDirectoryWithFolderBru()
    {
        var folder = await _sut.CreateFolderAsync(_root, "MyFolder");

        Assert.True(Directory.Exists(folder.FolderPath));
        Assert.True(File.Exists(Path.Combine(folder.FolderPath, "folder.bru")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SaveFolderOrderAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveFolderOrderAsync_UpdatesSeqInFiles()
    {
        WriteFile("first.bru", BruFile("first", "get", "https://example.com", seq: 1));
        WriteFile("second.bru", BruFile("second", "get", "https://example.com", seq: 2));

        // Reverse the order
        await _sut.SaveFolderOrderAsync(_root, ["second.bru", "first.bru"]);

        var firstContent = await File.ReadAllTextAsync(Path.Combine(_root, "first.bru"));
        var secondContent = await File.ReadAllTextAsync(Path.Combine(_root, "second.bru"));

        Assert.Contains("  seq: 2", firstContent);
        Assert.Contains("  seq: 1", secondContent);
    }

    [Fact]
    public async Task SaveAndLoad_BasicAuth_PasswordIsStoredInSecretsNotInFile()
    {
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(_root);

        var filePath = Path.Combine(_root, "api.bru");
        var request = new CollectionRequest
        {
            FilePath = filePath,
            Name = "api",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "alice",
                Password = "hunter2",
            },
        };

        await sut.SaveRequestAsync(request);

        var content = await File.ReadAllTextAsync(filePath);
        Assert.DoesNotContain("hunter2", content);
        // Username is still stored in the file.
        Assert.Contains("alice", content);
    }

    [Fact]
    public async Task SaveAndLoad_BasicAuth_RoundTrips()
    {
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(_root);

        var filePath = Path.Combine(_root, "api.bru");
        var request = new CollectionRequest
        {
            FilePath = filePath,
            Name = "api",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "alice",
                Password = "hunter2",
            },
        };

        await sut.SaveRequestAsync(request);
        var loaded = await sut.LoadRequestAsync(filePath);

        Assert.Equal(AuthConfig.AuthTypes.Basic, loaded.Auth.AuthType);
        Assert.Equal("alice", loaded.Auth.Username);
        Assert.Equal("hunter2", loaded.Auth.Password);
    }

    [Fact]
    public async Task RenameRequest_BasicAuth_SecretsStillAccessibleAfterRename()
    {
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(_root);

        var filePath = Path.Combine(_root, "original.bru");
        var request = new CollectionRequest
        {
            RequestId = Guid.NewGuid(),
            FilePath = filePath,
            Name = "original",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "alice",
                Password = "hunter2",
            },
        };

        await sut.SaveRequestAsync(request);

        // Rename the request file.
        var renamed = await sut.RenameRequestAsync(filePath, "renamed");

        // The secret must still be accessible under the new file path.
        var loaded = await sut.LoadRequestAsync(renamed.FilePath);
        Assert.Equal("hunter2", loaded.Auth.Password);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string BruFile(string name, string method, string url, int seq) =>
        // Use $$ so single braces are literal and {{expr}} is interpolation.
        $$"""
        meta {
          name: {{name}}
          type: http
          seq: {{seq}}
        }

        {{method}} {
          url: {{url}}
          body: none
          auth: none
        }
        """;

    private static string FolderBru(string name, int? seq = null) =>
        seq.HasValue
            ? $"meta {{\n  name: {name}\n  seq: {seq}\n}}\n"
            : $"meta {{\n  name: {name}\n}}\n";

    // ─────────────────────────────────────────────────────────────────────────
    //  Phase 5: Round-trip fidelity
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveRequestAsync_WithJsonBody_BodyBlockSurvivesRoundTrip()
    {
        var original = """
            meta {
              name: create item
              type: http
              seq: 1
            }

            post {
              url: https://api.example.com/items
              body: json
              auth: none
            }

            body:json {
              {
                "name": "test",
                "value": 42
              }
            }
            """;
        WriteFile("create.bru", original);
        var filePath = Path.Combine(_root, "create.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        await _sut.SaveRequestAsync(loaded);

        var written = await File.ReadAllTextAsync(filePath);
        Assert.Contains("body:json {", written);
        Assert.Contains("\"name\": \"test\"", written);
        Assert.Contains("\"value\": 42", written);
        Assert.Contains("post {", written);
    }

    [Fact]
    public async Task SaveRequestAsync_ScriptAndTestBlocksPreservedWithBody()
    {
        var original = """
            meta {
              name: scripted post
              type: http
              seq: 1
            }

            post {
              url: https://api.example.com/items
              body: json
              auth: none
            }

            body:json {
              {"key": "value"}
            }

            script:pre-request {
              bru.setGlobalEnvVar('ts', Date.now());
            }

            script:post-response {
              bru.setEnvVar('token', res.body.token);
            }

            tests {
              test("status is 200", function() {
                expect(res.status).to.equal(200);
              });
            }
            """;
        WriteFile("scripted.bru", original);
        var filePath = Path.Combine(_root, "scripted.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        await _sut.SaveRequestAsync(loaded);

        var written = await File.ReadAllTextAsync(filePath);
        Assert.Contains("body:json {", written);
        Assert.Contains("script:pre-request {", written);
        Assert.Contains("script:post-response {", written);
        Assert.Contains("tests {", written);
        Assert.Contains("setGlobalEnvVar", written);
        Assert.Contains("setEnvVar", written);
        Assert.Contains("expect(res.status)", written);
    }

    [Fact]
    public async Task SaveRequestAsync_ExistingFileWithRequestId_RemovesItOnSave()
    {
        // Simulate a file originally written by old Callsmith (has requestId in meta).
        var original = """
            meta {
              name: legacy
              type: http
              seq: 1
              requestId: 550e8400-e29b-41d4-a716-446655440000
            }

            get {
              url: https://api.example.com
              body: none
              auth: none
            }
            """;
        WriteFile("legacy.bru", original);
        var filePath = Path.Combine(_root, "legacy.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        await _sut.SaveRequestAsync(loaded);

        var written = await File.ReadAllTextAsync(filePath);
        // On first save, the legacy requestId should be removed from meta.
        Assert.DoesNotContain("requestId:", written);
        Assert.Contains("name: legacy", written);
    }

    [Fact]
    public async Task OpenFolderAsync_MixedFolderAndRequest_OrderedBySeq()
    {
        // Folder with seq=2, request with seq=1, folder with seq=3 → request first
        Directory.CreateDirectory(Path.Combine(_root, "Beta"));
        WriteFile("Beta/folder.bru", FolderBru("Beta", seq: 2));
        WriteFile("Beta/b-req.bru", BruFile("b-req", "get", "https://b.com", seq: 1));

        Directory.CreateDirectory(Path.Combine(_root, "Gamma"));
        WriteFile("Gamma/folder.bru", FolderBru("Gamma", seq: 3));

        WriteFile("alpha.bru", BruFile("alpha", "get", "https://a.com", seq: 1));

        var folder = await _sut.OpenFolderAsync(_root);

        // ItemOrder should be: alpha (seq=1), Beta (seq=2), Gamma (seq=3)
        Assert.Equal(3, folder.ItemOrder.Count);
        Assert.Equal("alpha.bru", folder.ItemOrder[0]);
        Assert.Equal("Beta", folder.ItemOrder[1]);
        Assert.Equal("Gamma", folder.ItemOrder[2]);
    }

    [Fact]
    public async Task OpenFolderAsync_FolderWithoutSeq_SortsAlphabeticallyAfterSequenced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        WriteFile("Alpha/folder.bru", FolderBru("Alpha")); // no seq

        Directory.CreateDirectory(Path.Combine(_root, "Zebra"));
        WriteFile("Zebra/folder.bru", FolderBru("Zebra", seq: 1));

        WriteFile("mid.bru", BruFile("mid", "get", "https://mid.com", seq: 2));

        var folder = await _sut.OpenFolderAsync(_root);

        // Zebra (seq=1), mid (seq=2), Alpha (no seq → last)
        Assert.Equal("Zebra", folder.ItemOrder[0]);
        Assert.Equal("mid.bru", folder.ItemOrder[1]);
        Assert.Equal("Alpha", folder.ItemOrder[2]);
    }

    [Fact]
    public async Task SaveFolderOrderAsync_UpdatesFolderBruSeqForSubFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "First"));
        WriteFile("First/folder.bru", FolderBru("First", seq: 1));

        Directory.CreateDirectory(Path.Combine(_root, "Second"));
        WriteFile("Second/folder.bru", FolderBru("Second", seq: 2));

        // Reverse the folder order
        await _sut.SaveFolderOrderAsync(_root, ["Second", "First"]);

        var firstContent = await File.ReadAllTextAsync(Path.Combine(_root, "First", "folder.bru"));
        var secondContent = await File.ReadAllTextAsync(Path.Combine(_root, "Second", "folder.bru"));

        Assert.Contains("seq: 2", firstContent);
        Assert.Contains("seq: 1", secondContent);
    }

    [Fact]
    public async Task SaveFolderOrderAsync_MixedFoldersAndRequests_AssignsSeqToAll()
    {
        Directory.CreateDirectory(Path.Combine(_root, "MyFolder"));
        WriteFile("MyFolder/folder.bru", FolderBru("MyFolder", seq: 1));
        WriteFile("req.bru", BruFile("req", "get", "https://example.com", seq: 2));

        // Put the request first, folder second
        await _sut.SaveFolderOrderAsync(_root, ["req.bru", "MyFolder"]);

        var reqContent = await File.ReadAllTextAsync(Path.Combine(_root, "req.bru"));
        var folderContent = await File.ReadAllTextAsync(Path.Combine(_root, "MyFolder", "folder.bru"));

        Assert.Contains("seq: 1", reqContent);
        Assert.Contains("seq: 2", folderContent);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Phase 4: params:path round-trip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadRequestAsync_WithParamsPathBlock_LoadsIntoPathParams()
    {
        var content = """
            meta {
              name: get user
              type: http
              seq: 1
            }

            get {
              url: https://api.example.com/users/:userId
              body: none
              auth: none
            }

            params:path {
              userId: 42
            }
            """;
        WriteFile("user.bru", content);

        var req = await _sut.LoadRequestAsync(Path.Combine(_root, "user.bru"));

        Assert.Single(req.PathParams);
        Assert.Equal("42", req.PathParams["userId"]);
    }

    [Fact]
    public async Task SaveRequestAsync_WithPathParams_WritesParamsPathBlock()
    {
        var request = new CollectionRequest
        {
            FilePath = Path.Combine(_root, "user.bru"),
            Name = "get user",
            Method = System.Net.Http.HttpMethod.Get,
            Url = "https://api.example.com/users/:userId",
            PathParams = new Dictionary<string, string> { ["userId"] = "99" },
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        await _sut.SaveRequestAsync(request);

        var content = await File.ReadAllTextAsync(request.FilePath);
        Assert.Contains("params:path {", content);
        Assert.Contains("  userId: 99", content);
    }

    [Fact]
    public async Task SaveRequestAsync_PathParams_RoundTrip()
    {
        var original = """
            meta {
              name: get order
              type: http
              seq: 1
            }

            get {
              url: https://api.example.com/users/:userId/orders/:orderId
              body: none
              auth: none
            }

            params:path {
              userId: 1
              orderId: 2
            }
            """;
        WriteFile("order.bru", original);
        var filePath = Path.Combine(_root, "order.bru");

        var loaded = await _sut.LoadRequestAsync(filePath);
        loaded = new CollectionRequest
        {
            FilePath = loaded.FilePath,
            Name = loaded.Name,
            Method = loaded.Method,
            Url = loaded.Url,
            PathParams = new Dictionary<string, string>(loaded.PathParams) { ["orderId"] = "99" },
            Headers = loaded.Headers,
            QueryParams = loaded.QueryParams,
            BodyType = loaded.BodyType,
            Body = loaded.Body,
            Auth = loaded.Auth,
        };
        await _sut.SaveRequestAsync(loaded);

        var reloaded = await _sut.LoadRequestAsync(filePath);
        Assert.Equal("1", reloaded.PathParams["userId"]);
        Assert.Equal("99", reloaded.PathParams["orderId"]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Phase 1: Computed Request Identity (Issue 40 stabilization)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeBrunoRequestIdentity_SameDisplayPath_ReturnsDeterministicGuid()
    {
        // The identity should be deterministic: same path → same Guid
        var collectionRoot = _root;
        var filePath1 = Path.Combine(_root, "requests", "api.bru");
        var filePath2 = Path.Combine(_root, "requests", "api.bru");
        var requestName = "get items";

        var identity1 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath1, requestName);
        var identity2 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath2, requestName);

        Assert.Equal(identity1, identity2);
        Assert.NotEqual(Guid.Empty, identity1);
    }

    [Fact]
    public void ComputeBrunoRequestIdentity_DifferentDisplayPath_ReturnsDifferentGuids()
    {
        // Different paths should produce different identities
        var collectionRoot = _root;
        var filePath1 = Path.Combine(_root, "requests", "api.bru");
        var filePath2 = Path.Combine(_root, "requests", "users.bru");

        var identity1 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath1, "api");
        var identity2 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath2, "users");

        Assert.NotEqual(identity1, identity2);
    }

    [Fact]
    public void ComputeBrunoRequestIdentity_SameName_DifferentFolders_ReturnsDifferentGuids()
    {
        // Same request name but different folders should produce different identities
        var collectionRoot = _root;
        var filePath1 = Path.Combine(_root, "requests", "api.bru");
        var filePath2 = Path.Combine(_root, "other", "api.bru");

        var identity1 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath1, "api");
        var identity2 = BrunoCollectionService.ComputeBrunoRequestIdentity(collectionRoot, filePath2, "api");

        Assert.NotEqual(identity1, identity2);
    }

    [Fact]
    public async Task LoadRequestAsync_WhenCollectionOpen_ComputesRequestIdentityFromDisplayPath()
    {
        // Open the collection so _currentRoot is set
        await _sut.OpenFolderAsync(_root);

        Directory.CreateDirectory(Path.Combine(_root, "requests"));
        var filePath = Path.Combine(_root, "requests", "api.bru");
        WriteFile("requests/api.bru", BruFile("get items", "get", "https://api.example.com", seq: 1));

        // Load the request
        var request = await _sut.LoadRequestAsync(filePath);

        // RequestId should be computed from the display path
        Assert.NotNull(request.RequestId);
        Assert.NotEqual(Guid.Empty, request.RequestId.Value);

        // The identity should match what ComputeBrunoRequestIdentity produces
        var expectedIdentity = BrunoCollectionService.ComputeBrunoRequestIdentity(_root, filePath, "get items");
        Assert.Equal(expectedIdentity, request.RequestId);
    }

    [Fact]
    public async Task RenameRequestAsync_ChangesComputedIdentity()
    {
        // Open the collection so identities are computed
        await _sut.OpenFolderAsync(_root);
        WriteFile("api.bru", BruFile("get items", "get", "https://api.example.com", seq: 1));

        var originalPath = Path.Combine(_root, "api.bru");
        var loaded1 = await _sut.LoadRequestAsync(originalPath);
        var identity1 = loaded1.RequestId;

        // Rename the request
        var renamed = await _sut.RenameRequestAsync(originalPath, "get all items");

        var loaded2 = await _sut.LoadRequestAsync(renamed.FilePath);
        var identity2 = loaded2.RequestId;

        // The identity is based on meta.name, so it should change on rename (as required by Issue 40)
        Assert.NotEqual(identity1, identity2);
    }

    [Fact]
    public async Task MoveRequestAsync_ChangesComputedIdentity()
    {
        // Open the collection so identities are computed
        await _sut.OpenFolderAsync(_root);
        WriteFile("api.bru", BruFile("get items", "get", "https://api.example.com", seq: 1));

        var originalPath = Path.Combine(_root, "api.bru");
        var loaded1 = await _sut.LoadRequestAsync(originalPath);
        var identity1 = loaded1.RequestId;

        // Move to a subfolder
        Directory.CreateDirectory(Path.Combine(_root, "subfolder"));
        var moved = await _sut.MoveRequestAsync(originalPath, Path.Combine(_root, "subfolder"));

        var loaded2 = await _sut.LoadRequestAsync(moved.FilePath);
        var identity2 = loaded2.RequestId;

        // The identity is based on folder path, so it should change on move (as required by Issue 40)
        Assert.NotEqual(identity1, identity2);
    }

    [Fact]
    public async Task SaveRequestAsync_DoesNotWriteComputedRequestId()
    {
        // Open collection
        await _sut.OpenFolderAsync(_root);

        var filePath = Path.Combine(_root, "api.bru");
        var request = new CollectionRequest
        {
            RequestId = Guid.NewGuid(),  // This is computed, not persisted
            FilePath = filePath,
            Name = "api",
            Method = HttpMethod.Get,
            Url = "https://api.example.com",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        await _sut.SaveRequestAsync(request);

        var content = await File.ReadAllTextAsync(filePath);
        // Computed requestId should never be written to the file
        Assert.DoesNotContain("requestId:", content);
    }
}
