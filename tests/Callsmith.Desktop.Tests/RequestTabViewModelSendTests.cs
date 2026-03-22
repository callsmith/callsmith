using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestTabViewModelSendTests
{
    [Fact]
    public async Task Send_ResolvesVariablesInQueryFormAndHeaders_BeforeEncoding()
    {
        var transport = new CapturingTransport();
        var registry = new TransportRegistry();
        registry.Register(transport);

        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(
            registry,
            collectionService,
            new WeakReferenceMessenger(),
            _ => { });

        sut.SetGlobalEnvironment(new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable { Name = "k", Value = "b" },
                new EnvironmentVariable { Name = "v", Value = "token" },
                new EnvironmentVariable { Name = "h", Value = "X-Resolved" },
            ],
        });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/send.callsmith",
            Name = "send",
            Method = HttpMethod.Post,
            Url = "https://api.example.com/test",
            QueryParams = [new RequestKv("{{k}}", "{{v}}")],
            Headers = [new RequestKv("{{h}}", "{{v}}")],
            BodyType = CollectionRequest.BodyTypes.Form,
            FormParams = [new KeyValuePair<string, string>("{{k}}", "{{v}}")],
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyIn = AuthConfig.ApiKeyLocations.Query,
                ApiKeyName = "auth_{{k}}",
                ApiKeyValue = "{{v}}",
            },
        });

        await sut.SendCommand.ExecuteAsync(null);

        transport.LastRequest.Should().NotBeNull();
        transport.LastRequest!.Url.Should().Contain("?b=token");
        transport.LastRequest.Url.Should().Contain("auth_b=token");
        transport.LastRequest.Body.Should().Be("b=token");
        transport.LastRequest.Headers.Should().ContainKey("X-Resolved");
        transport.LastRequest.Headers["X-Resolved"].Should().Be("token");
    }

    private sealed class CapturingTransport : ITransport
    {
        public IReadOnlyList<string> SupportedSchemes => ["https"];

        public RequestModel? LastRequest { get; private set; }

        public Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ResponseModel
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                Headers = new Dictionary<string, string>(),
                Body = "{}",
                BodyBytes = [],
                FinalUrl = request.Url,
                Elapsed = TimeSpan.FromMilliseconds(1),
            });
        }
    }
}
