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

    private FileSystemCollectionService Sut() =>
        new(Substitute.For<ISecretStorageService>(), NullLogger<FileSystemCollectionService>.Instance);

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
        json.Should().Contain("tok-123");
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

        // Save auth first.
        await sut.SaveFolderAuthAsync(folder, new AuthConfig { AuthType = AuthConfig.AuthTypes.Bearer, Token = "tok" });

        // Now save order.
        await sut.SaveFolderOrderAsync(folder, ["req.callsmith"]);

        var metaPath = Path.Combine(folder, "_meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        json.Should().Contain("bearer");
        json.Should().Contain("tok");
        json.Should().Contain("req.callsmith");
    }

    [Fact]
    public async Task ReadFolder_LoadsAuthFromMeta()
    {
        var root = _temp.CreateSubDirectory("col");
        var sut = Sut();
        await sut.OpenFolderAsync(root);

        await sut.SaveFolderAuthAsync(root, new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Basic,
            Username = "alice",
            Password = "secret",
        });

        var loaded = await sut.OpenFolderAsync(root);

        loaded.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        loaded.Auth.Username.Should().Be("alice");
        loaded.Auth.Password.Should().Be("secret");
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

        var sut = Sut();
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

        var sut = Sut();
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
        var sut = Sut();
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
}
