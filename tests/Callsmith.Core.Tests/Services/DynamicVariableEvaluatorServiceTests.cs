using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DynamicVariableEvaluatorService"/>.
/// </summary>
public sealed class DynamicVariableEvaluatorServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ICollectionService _collectionService = Substitute.For<ICollectionService>();
    private readonly ITransportRegistry _transportRegistry = Substitute.For<ITransportRegistry>();
    private readonly ITransport _transport = Substitute.For<ITransport>();

    private DynamicVariableEvaluatorService Sut() =>
        new(
            _collectionService,
            _transportRegistry,
            new AesSecretEncryptionService(Path.Combine(_temp.Path, "test-dyncache.key")),
            _temp.CreateSubDirectory("dyncache"),
            NullLogger<DynamicVariableEvaluatorService>.Instance);

    public void Dispose() => _temp.Dispose();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static CollectionRequest MakeRequest(string name, Guid? requestId = null, AuthConfig? auth = null) =>
        new()
        {
            FilePath = $"/collection/{name}.callsmith",
            Name = name,
            Method = HttpMethod.Get,
            Url = "https://api.example.com/token",
            RequestId = requestId,
            Auth = auth ?? new AuthConfig(),
        };

    private static CollectionFolder MakeFolder(CollectionRequest request) =>
        new()
        {
            FolderPath = "/collection",
            Name = "collection",
            Requests = [request],
        };

    private static EnvironmentVariable ResponseBodyVar(
        string name,
        string requestName,
        DynamicFrequency frequency = DynamicFrequency.Always) =>
        new()
        {
            Name = name,
            Value = string.Empty,
            VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
            ResponseRequestName = requestName,
            ResponsePath = "$.token",
            ResponseMatcher = ResponseValueMatcher.JsonPath,
            ResponseFrequency = frequency,
        };

    private static ResponseModel OkResponse(string body) =>
        new()
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = body,
            BodyBytes = [],
            FinalUrl = "https://api.example.com/token",
            Elapsed = TimeSpan.Zero,
        };

    // ─── Do not cache empty values ────────────────────────────────────────────

    /// <summary>
    /// When the transport fails on the first call, the cache must not be written.
    /// On a subsequent call (with Never frequency), the variable should be re-evaluated
    /// and the successful result returned.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WhenFirstRequestFails_DoesNotCacheEmptyValue_AndRetriesToResolveNextTime()
    {
        var requestId = Guid.NewGuid();
        var request = MakeRequest("get-token", requestId);
        var folder = MakeFolder(request);
        var collectionPath = _temp.CreateSubDirectory("collection");

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(request.FilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);

        // First call: transport throws — simulates a misconfigured endpoint
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var variable = ResponseBodyVar("token", "get-token", DynamicFrequency.Never);
        var sut = Sut();

        var firstResult = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        firstResult.Variables.Should().NotContainKey("token",
            "a failed request must not add the variable to resolved vars");

        // Second call: transport now succeeds — fix means the cache was never written,
        // so the variable is re-evaluated instead of returning the stale empty string.
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"abc123"}"""));

        var secondResult = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        secondResult.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("abc123",
                "the successful result should be returned after the previous failure did not poison the cache");
    }

    /// <summary>
    /// When <c>allowStaleCache = true</c> is passed, a cached value is returned even if the
    /// variable's frequency is <see cref="DynamicFrequency.Always"/> (which would normally
    /// force a fresh HTTP request). No HTTP call should be made.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AllowStaleCache_ReturnsCachedValue_WithoutHttpCall()
    {
        var requestId = Guid.NewGuid();
        var request = MakeRequest("get-token", requestId);
        var folder = MakeFolder(request);
        var collectionPath = _temp.CreateSubDirectory("stale-collection");

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(request.FilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"initial-token"}"""));

        // Always-frequency variable — would normally always re-execute.
        var variable = ResponseBodyVar("token", "get-token", DynamicFrequency.Always);
        var sut = Sut();

        // First call (no stale cache flag) — executes HTTP to populate the cache.
        await sut.ResolveAsync(collectionPath, "ns", [variable], new Dictionary<string, string>());
        await _transport.Received(1).SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>());

        // Second call with allowStaleCache = true — must return the cached value, no HTTP.
        _transport.ClearReceivedCalls();
        var result = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>(), allowStaleCache: true);

        result.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("initial-token",
                "stale cache should return the previously cached value without making an HTTP call");
        await _transport.DidNotReceive().SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When <c>allowStaleCache = true</c> and no cache entry exists at all, the variable
    /// is still evaluated via HTTP (because we have no value to return).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AllowStaleCache_ExecutesHttp_WhenNoCacheEntryExists()
    {
        var requestId = Guid.NewGuid();
        var request = MakeRequest("get-token", requestId);
        var folder = MakeFolder(request);
        var collectionPath = _temp.CreateSubDirectory("no-cache-collection");

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(request.FilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"fresh-token"}"""));

        var variable = ResponseBodyVar("token", "get-token", DynamicFrequency.IfExpired);
        var sut = Sut();

        // No prior cache — allowStaleCache = true must still execute HTTP.
        var result = await sut.ResolveAsync(
            collectionPath, "ns2", [variable], new Dictionary<string, string>(), allowStaleCache: true);

        result.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("fresh-token",
                "when there is no cache entry at all, HTTP must still be executed even with allowStaleCache");
        await _transport.Received(1).SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>());
    }

    // ─── Inherited auth ───────────────────────────────────────────────────────

    /// <summary>
    /// When the target request has <c>inherit</c> auth, the effective auth must be resolved
    /// by walking up the folder hierarchy, and that auth must be applied to the outgoing
    /// HTTP request (e.g. as an Authorization header).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WhenRequestUsesInheritAuth_ResolvesEffectiveAuthAndAppliesIt()
    {
        var requestId = Guid.NewGuid();
        var inheritAuthRequest = MakeRequest("get-token", requestId,
            auth: new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit });
        var folder = MakeFolder(inheritAuthRequest);
        var collectionPath = _temp.CreateSubDirectory("inherit-auth-collection");

        var effectiveAuth = new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "inherited-bearer-token",
        };

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(inheritAuthRequest.FilePath, Arg.Any<CancellationToken>())
            .Returns(inheritAuthRequest);
        _collectionService.ResolveEffectiveAuthAsync(inheritAuthRequest.FilePath, Arg.Any<CancellationToken>())
            .Returns(effectiveAuth);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);

        RequestModel? capturedModel = null;
        _transport.SendAsync(Arg.Do<RequestModel>(m => capturedModel = m), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"secret123"}"""));

        var variable = ResponseBodyVar("token", "get-token");
        var sut = Sut();

        var result = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        result.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("secret123");

        capturedModel.Should().NotBeNull();
        capturedModel!.Headers.Should().ContainKey("Authorization")
            .WhoseValue.Should().Be("Bearer inherited-bearer-token",
                "the inherited bearer token must be forwarded as an Authorization header");
    }

    /// <summary>
    /// When the target request has <c>inherit</c> auth and no ancestor folder provides auth,
    /// no Authorization header should be added (same as if auth type were None).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WhenRequestUsesInheritAuth_AndNoParentProvidesAuth_SendsNoAuthHeader()
    {
        var requestId = Guid.NewGuid();
        var inheritAuthRequest = MakeRequest("get-token", requestId,
            auth: new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit });
        var folder = MakeFolder(inheritAuthRequest);
        var collectionPath = _temp.CreateSubDirectory("inherit-no-parent-auth-collection");

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(inheritAuthRequest.FilePath, Arg.Any<CancellationToken>())
            .Returns(inheritAuthRequest);
        // Effective auth resolves to None (no parent provides auth).
        _collectionService.ResolveEffectiveAuthAsync(inheritAuthRequest.FilePath, Arg.Any<CancellationToken>())
            .Returns(new AuthConfig { AuthType = AuthConfig.AuthTypes.None });
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);

        RequestModel? capturedModel = null;
        _transport.SendAsync(Arg.Do<RequestModel>(m => capturedModel = m), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"anon"}"""));

        var variable = ResponseBodyVar("token", "get-token");
        var sut = Sut();

        await sut.ResolveAsync(collectionPath, "ns", [variable], new Dictionary<string, string>());

        capturedModel.Should().NotBeNull();
        capturedModel!.Headers.Should().NotContainKey("Authorization",
            "when no parent provides auth and effective auth is None, no auth header should be added");
    }

    /// <summary>
    /// Verifies that the cache file written to disk is AES-256-GCM encrypted and does
    /// not contain the plain-text cached value. A second instance backed by the same key
    /// file must be able to read it back, confirming the round-trip works.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_CacheFileIsEncryptedAtRest()
    {
        var requestId = Guid.NewGuid();
        var request = MakeRequest("get-token", requestId);
        var folder = MakeFolder(request);
        var collectionPath = _temp.CreateSubDirectory("encrypted-cache-collection");

        _collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
            .Returns(folder);
        _collectionService.LoadRequestAsync(request.FilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"supersecrettoken"}"""));

        var cacheDir = _temp.CreateSubDirectory("enc-dyncache");
        var keyFile = Path.Combine(_temp.Path, "enc-dyncache.key");
        var encryption = new AesSecretEncryptionService(keyFile);

        var sut = new DynamicVariableEvaluatorService(
            _collectionService,
            _transportRegistry,
            encryption,
            cacheDir,
            NullLogger<DynamicVariableEvaluatorService>.Instance);

        // Populate the cache.
        await sut.ResolveAsync(
            collectionPath, "ns", [ResponseBodyVar("token", "get-token", DynamicFrequency.Never)],
            new Dictionary<string, string>());

        // The cache file must exist and must NOT contain the plain-text value.
        var cacheFiles = Directory.GetFiles(cacheDir);
        cacheFiles.Should().HaveCount(1, "one cache file per collection");

        var raw = await File.ReadAllTextAsync(cacheFiles[0]);
        raw.Should().NotContain("supersecrettoken",
            "the cache value must be encrypted at rest");
        raw.Should().NotStartWith("{",
            "the cache file must be in AES-GCM encrypted format, not plaintext JSON");

        // A second instance using the same key must be able to read the cached value back.
        var sut2 = new DynamicVariableEvaluatorService(
            _collectionService,
            _transportRegistry,
            new AesSecretEncryptionService(keyFile),
            cacheDir,
            NullLogger<DynamicVariableEvaluatorService>.Instance);

        var variable = ResponseBodyVar("token", "get-token", DynamicFrequency.Never);
        var result = await sut2.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        result.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("supersecrettoken",
                "the encrypted cache must round-trip correctly");
        // The second instance must NOT have made an HTTP call — it read from cache.
        await _transport.Received(1).SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>());
    }
}
