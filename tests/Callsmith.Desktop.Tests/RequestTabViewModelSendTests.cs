using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.MockData;
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
            EnvironmentId = Guid.NewGuid(),
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

    [Fact]
    public async Task Send_RecordsHistoryWithSelectedEnvironmentIdNameAndColor()
    {
        var transport = new CapturingTransport();
        var registry = new TransportRegistry();
        registry.Register(transport);

        var environmentId = Guid.NewGuid();
        var historyService = Substitute.For<IHistoryService>();

        var sut = new RequestTabViewModel(
            registry,
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            historyService);

        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "dev.env.callsmith",
            Name = "dev",
            Color = "#00AAFF",
            Variables = [],
            EnvironmentId = environmentId,
        });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/send.callsmith",
            RequestId = Guid.NewGuid(),
            Name = "send",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/test",
        });

        await sut.SendCommand.ExecuteAsync(null);

        await AssertEventuallyAsync(async () =>
        {
            await historyService.Received(1).RecordAsync(
                Arg.Is<HistoryEntry>(entry =>
                    entry.EnvironmentName == "dev" &&
                    entry.EnvironmentColor == "#00AAFF" &&
                    entry.EnvironmentId == environmentId),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Send_HistoryVariableBindings_UseSendTimeValues_ForMockDataVariables()
    {
        // Arrange
        var transport = new CapturingTransport();
        var registry = new TransportRegistry();
        registry.Register(transport);

        var historyService = Substitute.For<IHistoryService>();
        HistoryEntry? recordedEntry = null;
        historyService
            .RecordAsync(Arg.Do<HistoryEntry>(e => recordedEntry = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var mockEmailEntry = MockDataCatalog.All.First(e =>
            e.Category == "Internet" && e.Field == "Email");

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator
            .ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string>(),
                MockGenerators = new Dictionary<string, MockDataEntry>
                {
                    ["mockEmail"] = mockEmailEntry,
                },
            });

        var sut = new RequestTabViewModel(
            registry,
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            evaluator,
            historyService);

        sut.SetGlobalEnvironment(new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "mockEmail",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    MockDataCategory = "Internet",
                    MockDataField = "Email",
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/test.callsmith",
            Name = "test",
            Method = HttpMethod.Post,
            Url = "https://api.example.com/users",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{{mockEmail}}",
        });

        // Act
        await sut.SendCommand.ExecuteAsync(null);

        // Assert — wait for the fire-and-forget history recording to complete.
        await AssertEventuallyAsync(async () =>
        {
            await historyService.Received(1).RecordAsync(
                Arg.Any<HistoryEntry>(),
                Arg.Any<CancellationToken>());
        });

        recordedEntry.Should().NotBeNull();

        // The transport received the resolved body (the generated email address).
        var sentBody = transport.LastRequest!.Body;
        sentBody.Should().NotBeNullOrWhiteSpace();

        // The history binding for {{mockEmail}} must be the same value that was sent —
        // not a freshly re-generated value produced after the request completed.
        var emailBinding = recordedEntry!.VariableBindings
            .FirstOrDefault(b => b.Token == "{{mockEmail}}");
        emailBinding.Should().NotBeNull("the mock-data variable should be recorded in history");
        emailBinding!.ResolvedValue.Should().Be(sentBody,
            "the history binding must reflect what was actually transmitted, not a re-generated value");
    }

    [Fact]
    public void LoadFromHistorySnapshot_ResolvesVariableTokensInUrl()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();

        var sut = new RequestTabViewModel(
            registry,
            collectionService,
            new WeakReferenceMessenger(),
            _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://{{baseUrl}}/api/users/{id}",
            PathParams = new Dictionary<string, string> { ["id"] = "{{userId}}" },
            QueryParams = [new RequestKv("filter", "{{statusFilter}}")],
            Auth = new AuthConfig(),
        };

        var bindings = new List<VariableBinding>
        {
            new("{{baseUrl}}", "api.example.com", IsSecret: false),
            new("{{userId}}", "42", IsSecret: false),
            new("{{statusFilter}}", "active", IsSecret: false),
        };

        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "GET",
            ResolvedUrl = "https://api.example.com/api/users/42?filter=active",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = snapshot,
            VariableBindings = bindings,
        };

        sut.LoadFromHistorySnapshot(snapshot, bindings);

        // URL field should have no {{}} or {param} placeholders
        sut.Url.Should().Contain("api.example.com");
        sut.Url.Should().Contain("/api/users/42");
        sut.Url.Should().NotContain("{{baseUrl}}");
        sut.Url.Should().NotContain("{id}");
        sut.SelectedMethod.Should().Be("GET");
        sut.IsNew.Should().BeTrue();
    }

    private static async Task AssertEventuallyAsync(Func<Task> assertion, int retries = 50, int delayMs = 20)
    {
        Exception? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(delayMs);
            }
        }

        throw last ?? new InvalidOperationException("Assertion did not succeed in time.");
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
