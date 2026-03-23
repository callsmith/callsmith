using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

public sealed class HistorySentViewBuilderTests
{
    [Fact]
    public void BuildVariableMap_StripsDelimiters_AndUsesLatestBindingPerToken()
    {
        var bindings = new List<VariableBinding>
        {
            new("{{host}}", "api.example.com", false),
            new("host", "ignored-second", false),
            new("{{token}}", "secret-value", true),
        };

        var map = HistorySentViewBuilder.BuildVariableMap(bindings);

        map.Should().ContainKey("host").WhoseValue.Should().Be("ignored-second");
        map.Should().ContainKey("token").WhoseValue.Should().Be("secret-value");
        map.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ReconstructsResolvedRequest_FromSnapshotAndBindings()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://{{host}}/v1/users/{id}",
            Headers =
            [
                new RequestKv("Authorization", "Bearer {{token}}"),
            ],
            AutoAppliedHeaders =
            [
                new RequestKv("Content-Type", "application/json"),
            ],
            QueryParams =
            [
                new RequestKv("q", "{{term}}"),
            ],
            PathParams = new Dictionary<string, string>
            {
                ["id"] = "{{userId}}",
            },
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"name\":\"{{name}}\"}",
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "X-Api-Key",
                ApiKeyValue = "{{apiKey}}",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
            },
        };

        var bindings = new List<VariableBinding>
        {
            new("{{host}}", "example.test", false),
            new("{{token}}", "token-123", true),
            new("{{term}}", "bob", false),
            new("{{userId}}", "42", false),
            new("{{name}}", "Robert", false),
            new("{{apiKey}}", "key-xyz", true),
        };

        var model = HistorySentViewBuilder.Build(snapshot, bindings);

        model.Method.Method.Should().Be("GET");
        model.Url.Should().Be("https://example.test/v1/users/42?q=bob");
        model.Headers.Should().Contain(kv => kv.Key == "Authorization" && kv.Value == "Bearer token-123");
        model.Headers.Should().Contain(kv => kv.Key == "Content-Type" && kv.Value == "application/json");
        model.Headers.Should().Contain(kv => kv.Key == "X-Api-Key" && kv.Value == "key-xyz");
        model.Body.Should().Be("{\"name\":\"Robert\"}");
    }
}
