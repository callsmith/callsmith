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
        var collectionPath = _temp.CreateSubDirectory("collection");

        _collectionService.ResolveRequestFilePathAsync(
                collectionPath, "get-token", Arg.Any<CancellationToken>())
            .Returns(request.FilePath);
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
    /// When the extraction path does not match anything in the response body,
    /// <c>null</c> is returned from the extractor and must not be cached as an empty string.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WhenExtractionYieldsNull_DoesNotCacheEmptyValue()
    {
        var requestId = Guid.NewGuid();
        var request = MakeRequest("get-token", requestId);
        var collectionPath = _temp.CreateSubDirectory("collection2");

        _collectionService.ResolveRequestFilePathAsync(
                collectionPath, "get-token", Arg.Any<CancellationToken>())
            .Returns(request.FilePath);
        _collectionService.LoadRequestAsync(request.FilePath, Arg.Any<CancellationToken>())
            .Returns(request);
        _transportRegistry.Resolve(Arg.Any<RequestModel>())
            .Returns(_transport);

        // First call: response body does not contain the expected field
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"wrong_field":"value"}"""));

        var variable = ResponseBodyVar("token", "get-token", DynamicFrequency.Never);
        var sut = Sut();

        var firstResult = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        firstResult.Variables.Should().NotContainKey("token",
            "an extraction miss must not add the variable to resolved vars");

        // Second call: response body now contains the expected field;
        // if cache was poisoned with "" the second call would skip re-execution and return "".
        _transport.SendAsync(Arg.Any<RequestModel>(), Arg.Any<CancellationToken>())
            .Returns(OkResponse("""{"token":"abc123"}"""));

        var secondResult = await sut.ResolveAsync(
            collectionPath, "ns", [variable], new Dictionary<string, string>());

        secondResult.Variables.Should().ContainKey("token")
            .WhoseValue.Should().Be("abc123",
                "the corrected response should yield a value when the cache was not poisoned");
    }
}
