using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for folder-level authentication: saving/loading folder auth in <c>_meta.json</c>
/// and the <see cref="FileSystemCollectionService.ResolveEffectiveAuthAsync"/> walk.
/// </summary>
public sealed class FileSystemCollectionServiceFolderAuthTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>No-op secrets for tests that do not exercise sensitive value storage.</summary>
    private static ISecretStorageService NoOpSecrets() => Substitute.For<ISecretStorageService>();

    /// <summary>Returns a real secrets service backed by a fresh temp sub-directory.</summary>
    private FileSystemSecretStorageService RealSecrets() =>
        new(
            _temp.CreateSubDirectory("secrets-store"),
            new AesSecretEncryptionService(System.IO.Path.Combine(_temp.Path, "secrets.key")),
            NullLogger<FileSystemSecretStorageService>.Instance);

    private FileSystemCollectionService Sut(ISecretStorageService? secrets = null) =>
        new(secrets ?? NoOpSecrets(), NullLogger<FileSystemCollectionService>.Instance);

    public void Dispose() => _temp.Dispose();

    // -------------------------------------------------------------------------
    // SaveFolderAuthAsync / ReadFolder round-trips
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveFolderAuthAsync_Bearer_WritesMetaFile()
    {
        var folder = _temp.CreateSubDirectory("col");
        var sut = Sut();

        var auth = new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "tok-123",
        };

        await sut.SaveFolderAuthAsync(folder, auth);

        var metaPath = Path.Combine(folder, "_meta.json");
        File.Exists(metaPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().Contain("\"type\"");
        json.Should().Contain("bearer");
        // Token must NOT be written to the file — it is stored in secret storage.
        json.Should().NotContain("tok-123");
    }

    [Fact]
    public async Task SaveFolderAuthAsync_Bearer_DoesNotWriteTokenToMetaFile()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "supersecrettoken",
        });

        var metaPath = Path.Combine(root, "_meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().NotContain("supersecrettoken");
        json.Should().Contain("bearer"); // auth type is not sensitive
    }

    [Fact]
    public async Task LoadFolderAuthAsync_Bearer_ReturnsTokenFromSecrets()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "supersecrettoken",
        });

        var loaded = await sut.LoadFolderAuthAsync(root);

        loaded.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        loaded.Token.Should().Be("supersecrettoken");
    }

    [Fact]
    public async Task SaveFolderAuthAsync_Inherit_OmitsAuthKeyAndRemovesFileWhenEmpty()
    {
        var folder = _temp.CreateSubDirectory("col");
        var sut = Sut();

        // First write some explicit auth.
        await sut.SaveFolderAuthAsync(folder, new AuthConfig { AuthType = AuthConfig.AuthTypes.Bearer, Token = "x" });
        File.Exists(Path.Combine(folder, "_meta.json")).Should().BeTrue();

        // Now revert to inherit — file should be removed since there is no order either.
        await sut.SaveFolderAuthAsync(folder, new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit });

        File.Exists(Path.Combine(folder, "_meta.json")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveFolderAuthAsync_Inherit_PreservesExistingOrder()
    {
        var folder = _temp.CreateSubDirectory("col");
        var sut = Sut();

        // Save order first.
        await sut.SaveFolderOrderAsync(folder, ["b.callsmith", "a.callsmith"]);

        // Now clear auth (inherit).
        await sut.SaveFolderAuthAsync(folder, new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit });

        // Meta file must still exist and contain the order.
        var metaPath = Path.Combine(folder, "_meta.json");
        File.Exists(metaPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().Contain("b.callsmith");
        json.Should().NotContain("\"auth\"");
    }

    [Fact]
    public async Task SaveFolderOrderAsync_PreservesExistingAuth()
    {
        var folder = _temp.CreateSubDirectory("col");
        var sut = Sut();

        // Save auth first (no real secrets needed — just checks the type survives order saves).
        await sut.SaveFolderAuthAsync(folder, new AuthConfig { AuthType = AuthConfig.AuthTypes.Bearer, Token = "tok" });

        // Now save order.
        await sut.SaveFolderOrderAsync(folder, ["req.callsmith"]);

        var metaPath = Path.Combine(folder, "_meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().Contain("bearer");
        // Token must NOT be written to the file (stored in secret storage).
        json.Should().NotContain("tok");
        json.Should().Contain("req.callsmith");
    }

    // -------------------------------------------------------------------------
    // Sensitive credential security — password / API key value NOT in meta file
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveFolderAuthAsync_BasicAuth_DoesNotWritePasswordToMetaFile()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Basic,
            Username = "alice",
            Password = "s3cr3t",
        });

        // The meta file must NOT contain the password.
        var metaPath = Path.Combine(root, "_meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().NotContain("s3cr3t");
        json.Should().Contain("alice"); // username is not sensitive
    }

    [Fact]
    public async Task SaveFolderAuthAsync_ApiKey_DoesNotWriteApiKeyValueToMetaFile()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.ApiKey,
            ApiKeyName = "X-Api-Key",
            ApiKeyValue = "supersecretvalue",
        });

        var metaPath = Path.Combine(root, "_meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().NotContain("supersecretvalue");
        json.Should().Contain("X-Api-Key"); // key name is not sensitive
    }

    // -------------------------------------------------------------------------
    // LoadFolderAuthAsync — enriches with secrets
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadFolderAuthAsync_BasicAuth_ReturnsPasswordFromSecrets()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Basic,
            Username = "alice",
            Password = "s3cr3t",
        });

        var loaded = await sut.LoadFolderAuthAsync(root);

        loaded.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        loaded.Username.Should().Be("alice");
        loaded.Password.Should().Be("s3cr3t");
    }

    [Fact]
    public async Task LoadFolderAuthAsync_ApiKey_ReturnsApiKeyValueFromSecrets()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.ApiKey,
            ApiKeyName = "X-Api-Key",
            ApiKeyValue = "supersecretvalue",
            ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
        });

        var loaded = await sut.LoadFolderAuthAsync(root);

        loaded.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        loaded.ApiKeyName.Should().Be("X-Api-Key");
        loaded.ApiKeyValue.Should().Be("supersecretvalue");
    }

    [Fact]
    public async Task LoadFolderAuthAsync_NoMeta_ReturnsInherit()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut();

        var loaded = await sut.LoadFolderAuthAsync(root);

        loaded.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
    }

    // -------------------------------------------------------------------------
    // ResolveEffectiveAuthAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResolveEffectiveAuth_UsesParentFolderWhenRequestInherits()
    {
        // root/
        //   sub/
        //     req.callsmith   (auth = inherit)
        // root/_meta.json     (auth = bearer "parent-token")

        var root = _temp.CreateSubDirectory("col");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);

        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        // Set bearer auth on root.
        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "parent-token",
        });

        // Request in sub-folder (auth = inherit by default).
        var requestPath = Path.Combine(sub, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        effective.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        effective.Token.Should().Be("parent-token");
    }

    [Fact]
    public async Task ResolveEffectiveAuth_ChildFolderOverridesParent()
    {
        // root/
        //   sub/
        //     req.callsmith   (auth = inherit)
        // root/_meta.json     (auth = bearer "root-token")
        // root/sub/_meta.json (auth = basic "child-user")

        var root = _temp.CreateSubDirectory("col");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);

        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "root-token",
        });
        await sut.SaveFolderAuthAsync(sub, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Basic,
            Username = "child-user",
            Password = "child-pass",
        });

        var requestPath = Path.Combine(sub, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        effective.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        effective.Username.Should().Be("child-user");
        effective.Password.Should().Be("child-pass");
    }

    [Fact]
    public async Task ResolveEffectiveAuth_RootInheritFallsBackToNone()
    {
        // root/
        //   req.callsmith   (auth = inherit)
        // root has no _meta.json (or all inherit) → effective = none

        var root = _temp.CreateSubDirectory("col");
        var sut = Sut();
        await sut.OpenFolderAsync(root);

        var requestPath = Path.Combine(root, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        effective.AuthType.Should().Be(AuthConfig.AuthTypes.None);
    }

    [Fact]
    public async Task ResolveEffectiveAuth_NoneOnFolderStopsWalk()
    {
        // root/
        //   mid/
        //     sub/
        //       req.callsmith   (auth = inherit)
        // root/_meta.json       (auth = bearer "should-not-reach")
        // root/mid/_meta.json   (auth = none)  ← stops here

        var root = _temp.CreateSubDirectory("col");
        var mid = Path.Combine(root, "mid");
        var sub = Path.Combine(mid, "sub");
        Directory.CreateDirectory(sub);

        var sut = Sut();
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "should-not-reach",
        });
        await sut.SaveFolderAuthAsync(mid, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.None,
        });

        var requestPath = Path.Combine(sub, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        effective.AuthType.Should().Be(AuthConfig.AuthTypes.None);
    }

    [Fact]
    public async Task ResolveEffectiveAuth_ApiKey_RoundTrips()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets());
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.ApiKey,
            ApiKeyName = "X-API-Key",
            ApiKeyValue = "my-api-key",
            ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
        });

        var requestPath = Path.Combine(root, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        effective.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        effective.ApiKeyName.Should().Be("X-API-Key");
        effective.ApiKeyValue.Should().Be("my-api-key");
        effective.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Header);
    }

    // -------------------------------------------------------------------------
    // Migration path — pre-upgrade files with plaintext password in meta
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResolveEffectiveAuth_FallsBackToPlaintextPasswordInLegacyMetaFile()
    {
        // Simulate a pre-upgrade _meta.json that has a plaintext password.
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut(RealSecrets()); // real secrets — but nothing stored yet
        await sut.OpenFolderAsync(root);

        // Write a legacy _meta.json that contains the password directly.
        var metaPath = Path.Combine(root, "_meta.json");
        await File.WriteAllTextAsync(metaPath, """
            {
              "auth": {
                "type": "basic",
                "username": "legacy-user",
                "password": "legacy-pass"
              }
            }
            """);

        var requestPath = Path.Combine(root, "req.callsmith");
        File.WriteAllText(requestPath, """{"method":"GET","url":"https://example.com"}""");

        var effective = await sut.ResolveEffectiveAuthAsync(requestPath);

        // The plaintext value in the file should be returned as a migration fallback.
        effective.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        effective.Password.Should().Be("legacy-pass");
    }
}

