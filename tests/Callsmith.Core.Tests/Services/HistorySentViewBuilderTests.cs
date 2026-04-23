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
            MultipartBodyEntries =
            [
                new MultipartBodyEntry { Key = "field1", IsFile = false, TextValue = "value1", IsEnabled = true },
                new MultipartBodyEntry { Key = "field2", IsFile = false, TextValue = "value2", IsEnabled = true },
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
    public void Build_WithMultipartFiles_ReturnsMultipartFileParts()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.Multipart,
            MultipartBodyEntries =
            [
                new MultipartBodyEntry { Key = "file", IsFile = true, FileName = "payload.bin", IsEnabled = true },
            ],
            MultipartFormFiles =
            [
                new MultipartFilePart
                {
                    Key = "file",
                    FileBytes = [0x10, 0x20],
                    FileName = "payload.bin",
                },
            ],
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.MultipartFormFiles.Should().NotBeNull();
        model.MultipartFormFiles.Should().ContainSingle();
        model.MultipartFormFiles![0].Key.Should().Be("file");
        model.MultipartFormFiles[0].FileName.Should().Be("payload.bin");
        model.MultipartFormFiles[0].FileBytes.Should().Equal([0x10, 0x20]);
    }

    [Fact]
    public void Build_WithOrderedMultipartEntries_UsesEntryOrder_AndSkipsDisabledEntries()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            BodyType = CollectionRequest.BodyTypes.Multipart,
            MultipartBodyEntries =
            [
                new MultipartBodyEntry { Key = "first", IsFile = false, TextValue = "1", IsEnabled = true },
                new MultipartBodyEntry { Key = "fileA", IsFile = true, FileName = "a.bin", FilePath = "/tmp/a.bin", IsEnabled = true },
                new MultipartBodyEntry { Key = "disabled", IsFile = false, TextValue = "x", IsEnabled = false },
                new MultipartBodyEntry { Key = "last", IsFile = false, TextValue = "2", IsEnabled = true },
            ],
            MultipartFormFiles =
            [
                new MultipartFilePart { Key = "fileA", FileName = "a.bin", FilePath = "/tmp/a.bin", FileBytes = [0xAA] },
            ],
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.MultipartFormParams.Should().Equal(
            new KeyValuePair<string, string>("first", "1"),
            new KeyValuePair<string, string>("last", "2"));
        model.MultipartFormFiles.Should().ContainSingle(f => f.Key == "fileA");
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

    [Fact]
    public void Build_WithInheritAuth_UsesEffectiveAuthForBearerToken()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://example.com/api",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit },
            EffectiveAuth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Bearer,
                Token = "{{inheritedToken}}",
            },
        };

        var bindings = new List<VariableBinding>
        {
            new("{{inheritedToken}}", "parent-bearer-123", true),
        };

        var model = HistorySentViewBuilder.Build(snapshot, bindings);

        model.Headers.Should().Contain(kv =>
            kv.Key == "Authorization" && kv.Value == "Bearer parent-bearer-123");
    }

    [Fact]
    public void Build_WithInheritAuth_UsesEffectiveAuthForApiKeyInHeader()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://example.com/api",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit },
            EffectiveAuth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "X-Folder-Key",
                ApiKeyValue = "{{folderKey}}",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Header,
            },
        };

        var bindings = new List<VariableBinding>
        {
            new("{{folderKey}}", "folder-api-key", true),
        };

        var model = HistorySentViewBuilder.Build(snapshot, bindings);

        model.Headers.Should().Contain(kv =>
            kv.Key == "X-Folder-Key" && kv.Value == "folder-api-key");
    }

    [Fact]
    public void Build_WithInheritAuth_UsesEffectiveAuthForApiKeyInQuery()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://example.com/api",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit },
            EffectiveAuth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.ApiKey,
                ApiKeyName = "api_key",
                ApiKeyValue = "secret-value",
                ApiKeyIn = AuthConfig.ApiKeyLocations.Query,
            },
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Url.Should().Contain("api_key=secret-value");
    }

    [Fact]
    public void Build_WithInheritAuth_AndNoEffectiveAuth_DoesNotAddAuthHeaders()
    {
        // EffectiveAuth is null — e.g. an entry recorded before the field was introduced.
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "GET",
            Url = "https://example.com/api",
            Auth = new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit },
            EffectiveAuth = null,
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Headers.Should().NotContainKey("Authorization");
    }

    // -------------------------------------------------------------------------
    // Content-Type override
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("content-type")]
    [InlineData("CONTENT-TYPE")]
    public void Build_WithExplicitContentTypeHeader_OverridesAutoApplied(string headerKey)
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            Headers = [new RequestKv(headerKey, "application/vnd.custom+json")],
            AutoAppliedHeaders = [new RequestKv("Content-Type", "application/json")],
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = """{"key":"val"}""",
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        // The user's explicit Content-Type header must win.
        model.Headers.Should().ContainKey("Content-Type")
            .WhoseValue.Should().Be("application/vnd.custom+json");
        model.ContentType.Should().Be("application/vnd.custom+json");
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("content-type")]
    [InlineData("CONTENT-TYPE")]
    public void Build_WithExplicitContentTypeHeader_AutoAppliedDoesNotOverwrite(string headerKey)
    {
        // Auto-applied Content-Type must not silently overwrite a user-specified one.
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            Headers = [new RequestKv(headerKey, "text/csv")],
            AutoAppliedHeaders = [new RequestKv("Content-Type", "application/json")],
            BodyType = CollectionRequest.BodyTypes.Json,
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Headers.Should().ContainKey("Content-Type")
            .WhoseValue.Should().Be("text/csv");
    }

    [Fact]
    public void Build_WithoutExplicitContentTypeHeader_AutoAppliedHeaderIsUsed()
    {
        var snapshot = new ConfiguredRequestSnapshot
        {
            Method = "POST",
            Url = "https://example.com",
            Headers = [],
            AutoAppliedHeaders = [new RequestKv("Content-Type", "application/json")],
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = """{"k":"v"}""",
        };

        var model = HistorySentViewBuilder.Build(snapshot, []);

        model.Headers.Should().ContainKey("Content-Type")
            .WhoseValue.Should().Be("application/json");
        model.ContentType.Should().Be("application/json");
    }
}
