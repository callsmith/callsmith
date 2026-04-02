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
            _temp.CreateSubDirectory("dyncache"),
            NullLogger<DynamicVariableEvaluatorService>.Instance);

    public void Dispose() => _temp.Dispose();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static CollectionRequest MakeRequest(string name, Guid? requestId = null) =>
        new()
        {
            FilePath = $"/collection/{name}.callsmith",
            Name = name,
            Method = HttpMethod.Get,
            Url = "https://api.example.com/token",
            RequestId = requestId,
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
}
