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

    [Fact]
    public void Build_WithYamlBody_ReturnsYamlBodyAndContentType()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.Yaml,
            Body = "key: value",
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Body.Should().Be("key: value");
        model.ContentType.Should().Be(CollectionRequest.BodyTypes.YamlContentType);
    }

    [Fact]
    public void Build_WithOtherBody_ReturnsBodyAndNullContentType()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.Other,
            Body = "custom payload",
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Body.Should().Be("custom payload");
        model.ContentType.Should().BeNull();
    }

    [Fact]
    public void Build_WithMultipartBody_ReturnsMultipartFormParamsAndContentType()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.Multipart,
            FormParams =
            [
                new KeyValuePair<string, string>("field1", "value1"),
                new KeyValuePair<string, string>("field2", "value2"),
            ],
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.MultipartFormParams.Should().NotBeNull();
        model.MultipartFormParams.Should().HaveCount(2);
        model.MultipartFormParams!.Should().Contain(kv => kv.Key == "field1" && kv.Value == "value1");
        model.MultipartFormParams!.Should().Contain(kv => kv.Key == "field2" && kv.Value == "value2");
        model.ContentType.Should().Be("multipart/form-data");
        model.Body.Should().BeNull();
    }

    [Fact]
    public void Build_WithFileBody_ReturnsBodyBytesAndContentType()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.File,
            FileBodyBase64 = Convert.ToBase64String(bytes),
            FileBodyName = "test.bin",
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.BodyBytes.Should().Equal(bytes);
        model.ContentType.Should().Be(CollectionRequest.BodyTypes.FileContentType);
        model.Body.Should().BeNull();
    }

    [Fact]
    public void Build_WithFileBodyButNoBase64_ReturnsNullBodyBytes()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.File,
            FileBodyBase64 = null,
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.BodyBytes.Should().BeNull();
        model.Body.Should().BeNull();
    }

    [Theory]
    [InlineData(CollectionRequest.BodyTypes.None, null)]
    [InlineData(CollectionRequest.BodyTypes.Json, CollectionRequest.BodyTypes.JsonContentType)]
    [InlineData(CollectionRequest.BodyTypes.Text, CollectionRequest.BodyTypes.TextContentType)]
    [InlineData(CollectionRequest.BodyTypes.Xml, CollectionRequest.BodyTypes.XmlContentType)]
    [InlineData(CollectionRequest.BodyTypes.Yaml, CollectionRequest.BodyTypes.YamlContentType)]
    [InlineData(CollectionRequest.BodyTypes.Other, null)]
    [InlineData(CollectionRequest.BodyTypes.Form, "application/x-www-form-urlencoded")]
    [InlineData(CollectionRequest.BodyTypes.Multipart, "multipart/form-data")]
    [InlineData(CollectionRequest.BodyTypes.File, CollectionRequest.BodyTypes.FileContentType)]
    public void Build_ContentTypeMatchesBodyType(string bodyType, string? expectedContentType)
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = bodyType,
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.ContentType.Should().Be(expectedContentType);
    }
}
