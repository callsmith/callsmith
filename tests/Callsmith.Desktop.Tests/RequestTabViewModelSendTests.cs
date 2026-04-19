using System.Net.Http;
using Avalonia.Threading;
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
    public async Task Send_BrunoColonPathParams_AreSubstitutedIntoRequestUrl()
    {
        var brunoRoot = Path.Combine(Path.GetTempPath(), "BrunoSendPathParam_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(brunoRoot);
        File.WriteAllText(Path.Combine(brunoRoot, "bruno.json"), """{"name":"test","version":"1"}""");

        try
        {
            var transport = new CapturingTransport();
            var registry = new TransportRegistry();
            registry.Register(transport);

            var sut = new RequestTabViewModel(
                registry,
                Substitute.For<ICollectionService>(),
                new WeakReferenceMessenger(),
                _ => { });
            sut.CollectionRootPath = brunoRoot;

            sut.LoadRequest(new CollectionRequest
            {
                FilePath = Path.Combine(brunoRoot, "jokes.bru"),
                Name = "joke",
                Method = HttpMethod.Get,
                Url = "https://api.chucknorris.io/jokes/:kind",
                PathParams = new Dictionary<string, string> { ["kind"] = "random" },
            });

            await sut.SendCommand.ExecuteAsync(null);

            transport.LastRequest.Should().NotBeNull();
            transport.LastRequest!.Url.Should().Be("https://api.chucknorris.io/jokes/random");
        }
        finally
        {
            Directory.Delete(brunoRoot, recursive: true);
        }
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
                Arg.Any<bool>(),
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
        sut.Url.Should().Contain("/api/users/{id}");
        sut.Url.Should().NotContain("{{baseUrl}}");
        sut.PathParams.GetAllKv().Should().Contain(p => p.Key == "id" && p.Value == "42");
        sut.SelectedMethod.Should().Be("GET");
        sut.IsNew.Should().BeTrue();
    }

    [Fact]
    public void LoadFromHistorySnapshot_ExcludesDisabledHeaders()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Headers =
            [
                new RequestKv("X-Enabled", "yes", IsEnabled: true),
                new RequestKv("X-Disabled", "no", IsEnabled: false),
            ],
            Auth = new AuthConfig(),
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        var headers = sut.Headers.GetAllKv();
        headers.Should().Contain(h => h.Key == "X-Enabled" && h.Value == "yes");
        headers.Should().NotContain(h => h.Key == "X-Disabled");
    }

    [Fact]
    public void LoadFromHistorySnapshot_ExcludesDisabledQueryParams()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            QueryParams =
            [
                new RequestKv("active", "1", IsEnabled: true),
                new RequestKv("inactive", "0", IsEnabled: false),
            ],
            Auth = new AuthConfig(),
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        var qp = sut.QueryParams.GetAllKv();
        qp.Should().Contain(p => p.Key == "active" && p.Value == "1");
        qp.Should().NotContain(p => p.Key == "inactive");
        sut.Url.Should().NotContain("active=1");
        sut.Url.Should().NotContain("inactive");
    }

    [Fact]
    public void LoadFromHistorySnapshot_BearerAuth_ConvertsToAuthorizationHeader()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Bearer,
                Token = "my-token",
            },
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        sut.Headers.GetAllKv().Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer my-token");
    }

    [Fact]
    public void LoadFromHistorySnapshot_BasicAuth_ConvertsToAuthorizationHeader()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Basic,
                Username = "user",
                Password = "pass",
            },
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        var expectedEncoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("user:pass"));
        sut.Headers.GetAllKv().Should().Contain(h => h.Key == "Authorization" && h.Value == $"Basic {expectedEncoded}");
    }

    [Fact]
    public void LoadFromHistorySnapshot_ApiKeyHeaderAuth_ConvertsToHeader()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "X-API-Key",
                ApiKeyValue = "secret123",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
            },
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        sut.Headers.GetAllKv().Should().Contain(h => h.Key == "X-API-Key" && h.Value == "secret123");
    }

    [Fact]
    public void LoadFromHistorySnapshot_ApiKeyQueryAuth_ConvertsToQueryParam()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "apikey",
                ApiKeyValue = "secret123",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Query,
            },
        };

        sut.LoadFromHistorySnapshot(snapshot, []);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        sut.QueryParams.GetAllKv().Should().Contain(p => p.Key == "apikey" && p.Value == "secret123");
        sut.Url.Should().NotContain("apikey=secret123");
    }

    [Fact]
    public void LoadFromHistorySnapshot_BearerAuth_ResolvesVariableTokenInToken()
    {
        var registry = new TransportRegistry();
        var collectionService = Substitute.For<ICollectionService>();
        var sut = new RequestTabViewModel(registry, collectionService, new WeakReferenceMessenger(), _ => { });

        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://api.example.com/test",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Bearer,
                Token = "{{secret}}",
            },
        };

        var bindings = new List<VariableBinding>
        {
            new("{{secret}}", "actual-token", IsSecret: true),
        };

        sut.LoadFromHistorySnapshot(snapshot, bindings);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        sut.Headers.GetAllKv().Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer actual-token");
    }

    [Fact]
    public async Task Send_ConcreteEnvVar_OverridesGlobalVar_ByDefault()
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
            Variables = [new EnvironmentVariable { Name = "base-url", Value = "https://global.example.com" }],
            EnvironmentId = Guid.NewGuid(),
        });
        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "dev.env.callsmith",
            Name = "dev",
            Variables = [new EnvironmentVariable { Name = "base-url", Value = "https://dev.example.com" }],
            EnvironmentId = Guid.NewGuid(),
        });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/req.callsmith",
            Name = "req",
            Method = HttpMethod.Get,
            Url = "{{base-url}}/api/resource",
        });

        await sut.SendCommand.ExecuteAsync(null);

        // Concrete env var should win by default.
        transport.LastRequest!.Url.Should().Contain("dev.example.com");
        transport.LastRequest.Url.Should().NotContain("global.example.com");
    }

    [Fact]
    public async Task Send_GlobalVarWithForceOverride_WinsOverConcreteEnvVar()
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
                new EnvironmentVariable
                {
                    Name = "base-url",
                    Value = "https://global.example.com",
                    IsForceGlobalOverride = true,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });
        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "dev.env.callsmith",
            Name = "dev",
            Variables = [new EnvironmentVariable { Name = "base-url", Value = "https://dev.example.com" }],
            EnvironmentId = Guid.NewGuid(),
        });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/req.callsmith",
            Name = "req",
            Method = HttpMethod.Get,
            Url = "{{base-url}}/api/resource",
        });

        await sut.SendCommand.ExecuteAsync(null);

        // Force-override global var should win over the concrete env var.
        transport.LastRequest!.Url.Should().Contain("global.example.com");
        transport.LastRequest.Url.Should().NotContain("dev.example.com");
    }

    private static async Task AssertEventuallyAsync(Func<Task> assertion, int retries = 50, int delayMs = 20)
    {
        Exception? last = null;
        for (var i = 0; i < retries; i++)
        {
            // Pump dispatcher to allow async work to proceed
            try
            {
                if (Dispatcher.UIThread.CheckAccess())
                    Dispatcher.UIThread.RunJobs();
                else
                    await Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Input);
            }
            catch
            {
                // Dispatcher unavailable; continue
            }

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
