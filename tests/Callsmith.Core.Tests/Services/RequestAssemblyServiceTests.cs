using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Callsmith.Core.Tests.Services;

public class RequestAssemblyServiceTests
{
    private readonly ICollectionService _collectionServiceMock = Substitute.For<ICollectionService>();
    private readonly IEnvironmentMergeService _mergeServiceMock = Substitute.For<IEnvironmentMergeService>();
    private readonly RequestAssemblyService _service;

    public RequestAssemblyServiceTests()
    {
        _service = new RequestAssemblyService(_collectionServiceMock, _mergeServiceMock);
    }

    [Fact]
    public async Task AssembleAsync_WithBasicRequest_ReturnsRequestModel()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "GET",
            Url = "https://api.example.com/users",
            Headers = new[] { new KeyValuePair<string, string>("Accept", "application/json") },
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Should().NotBeNull();
        assembled.RequestModel.Url.Should().Be("https://api.example.com/users");
        assembled.RequestModel.Method.Method.Should().Be("GET");
        assembled.RequestModel.Headers.Should().ContainKey("Accept");
        assembled.ResolvedUrl.Should().Be("https://api.example.com/users");
    }

    [Fact]
    public async Task AssembleAsync_WithPathParams_AppliesSubstitution()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "GET",
            Url = "https://api.example.com/users/{id}",
            Headers = [],
            PathParams = [new KeyValuePair<string, string>("id", "123")],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Url.Should().Contain("123");
    }

    [Fact]
    public async Task AssembleAsync_WithBearerAuth_AddsBearerHeader()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "GET",
            Url = "https://api.example.com/users",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            FormParams = [],
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Bearer,
                Token = "test-token-12345"
            },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Headers.Should().ContainKey("Authorization");
        assembled.RequestModel.Headers["Authorization"].Should().Be("Bearer test-token-12345");
        assembled.EffectiveAuth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
    }

    [Fact]
    public async Task AssembleAsync_WithJsonBody_ReturnsBodyInRequestModel()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var bodyJson = """{"name":"John"}""";
        var input = new RequestAssemblyInput
        {
            Method = "POST",
            Url = "https://api.example.com/users",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.Json,
            BodyText = bodyJson,
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Body.Should().Be(bodyJson);
        assembled.RequestModel.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task AssembleAsync_WithFormBody_BuildsUrlEncodedContent()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "POST",
            Url = "https://api.example.com/login",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.Form,
            FormParams = [
                new KeyValuePair<string, string>("username", "admin"),
                new KeyValuePair<string, string>("password", "secret")
            ],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Body.Should().Contain("username=admin");
        assembled.RequestModel.Body.Should().Contain("password=secret");
        assembled.RequestModel.ContentType.Should().Be("application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task AssembleAsync_WithMultipartFormBody_CollectsFormParams()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "POST",
            Url = "https://api.example.com/upload",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.Multipart,
            FormParams = [new KeyValuePair<string, string>("field", "value")],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.MultipartFormParams.Should().NotBeNull();
        assembled.RequestModel.MultipartFormParams.Should().HaveCount(1);
        assembled.RequestModel.MultipartFormParams[0].Key.Should().Be("field");
    }

    [Fact]
    public async Task AssembleAsync_WithFileBody_PassesFileBytesDirectly()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var fileBytes = new byte[] { 0x48, 0x69 }; // "Hi"
        var input = new RequestAssemblyInput
        {
            Method = "POST",
            Url = "https://api.example.com/upload",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.File,
            FileBodyBytes = fileBytes,
            FileBodyName = "test.txt",
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.BodyBytes.Should().BeSameAs(fileBytes);
    }

    [Fact]
    public async Task AssembleAsync_WithInheritedAuth_ResolvesFromCollection()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var mergedEnv = new ResolvedEnvironment { Variables = new Dictionary<string, string>() };
        var inheritedAuth = new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "inherited-token"
        };

        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);
        _collectionServiceMock.ResolveEffectiveAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(inheritedAuth);

        var input = new RequestAssemblyInput
        {
            Method = "GET",
            Url = "https://api.example.com/users",
            Headers = [],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "/collection/request.json", CancellationToken.None);

        // Assert
        assembled.EffectiveAuth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        assembled.EffectiveAuth.Token.Should().Be("inherited-token");
        assembled.RequestModel.Headers.Should().ContainKey("Authorization");
    }

    [Fact]
    public async Task AssembleAsync_WithVariableHeaders_SubstitutesVariables()
    {
        // Arrange
        var globalEnv = new EnvironmentModel { FilePath = "", EnvironmentId = Guid.NewGuid(), Name = "Global", Variables = [] };
        var variables = new Dictionary<string, string> { { "api_key", "secret123" } };
        var mergedEnv = new ResolvedEnvironment { Variables = variables };
        _mergeServiceMock.MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(mergedEnv);

        var input = new RequestAssemblyInput
        {
            Method = "GET",
            Url = "https://api.example.com/users",
            Headers = [new KeyValuePair<string, string>("X-API-Key", "{{api_key}}")],
            PathParams = [],
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            FormParams = [],
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        // Act
        var assembled = await _service.AssembleAsync(input, globalEnv, null, "/collection", "", CancellationToken.None);

        // Assert
        assembled.RequestModel.Headers.Should().ContainKey("X-API-Key");
        assembled.RequestModel.Headers["X-API-Key"].Should().Be("secret123");
    }
}
