using System.Linq;
using System.Text.Json;
using Callsmith.Core.Import;
using Callsmith.Core.Models;
using Callsmith.Core.Postman;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Postman;

/// <summary>Tests for <see cref="PostmanCollectionImporter"/>.</summary>
public sealed class PostmanCollectionImporterTests : IDisposable
{
    private readonly PostmanCollectionImporter _sut =
        new(NullLogger<PostmanCollectionImporter>.Instance);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ─────────────────────────────────────────────────────────────────────────
    // CanImportAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForPostmanV21File()
    {
        var path = WriteJson("can_import.json", MinimalPostmanJson());
        var result = await _sut.CanImportAsync(path);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForPostmanV20File()
    {
        var json = MinimalPostmanJson(schema: "https://schema.getpostman.com/json/collection/v2.0.0/collection.json");
        var path = WriteJson("can_import_v20.json", json);
        var result = await _sut.CanImportAsync(path);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForInsomniaFile()
    {
        var path = WriteJson("not_postman.yaml", "type: collection.insomnia.rest/5.0\nname: Other");
        var result = await _sut.CanImportAsync(path);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForArbitraryJson()
    {
        var path = WriteJson("other.json", """{"name": "not postman"}""");
        var result = await _sut.CanImportAsync(path);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForMissingFile()
    {
        var result = await _sut.CanImportAsync(Path.Combine(_temp.Path, "missing.json"));
        result.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — collection name and structure
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_PopulatesCollectionName()
    {
        var path = WriteJson("named.json", MinimalPostmanJson(name: "My API"));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("My API");
    }

    [Fact]
    public async Task ImportAsync_UsesDefaultNameWhenNameIsBlank()
    {
        var path = WriteJson("unnamed.json", MinimalPostmanJson(name: ""));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("Imported Collection");
    }

    [Fact]
    public async Task ImportAsync_ParsesRootRequest()
    {
        var json = PostmanJson("Test", [
            RequestItem("Get Users", "GET", "https://example.com/api/users")
        ]);

        var path = WriteJson("root_req.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().HaveCount(1);
        result.RootFolders.Should().BeEmpty();

        var req = result.RootRequests[0];
        req.Name.Should().Be("Get Users");
        req.Method.Method.Should().Be("GET");
    }

    [Fact]
    public async Task ImportAsync_MapsAllHttpMethods()
    {
        var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
        var items = methods.Select(m => RequestItem($"Req {m}", m, "https://example.com")).ToList();
        var json = PostmanJson("Test", items);
        var path = WriteJson("methods.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Select(r => r.Method.Method)
            .Should().BeEquivalentTo(methods);
    }

    [Fact]
    public async Task ImportAsync_ParsesFolderWithNestedRequests()
    {
        var json = PostmanJson("Test", [
            FolderItem("Users", [
                RequestItem("List Users", "GET", "https://example.com/users"),
                RequestItem("Get User",   "GET", "https://example.com/users/1"),
            ])
        ]);

        var path = WriteJson("folder.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().BeEmpty();
        result.RootFolders.Should().HaveCount(1);

        var folder = result.RootFolders[0];
        folder.Name.Should().Be("Users");
        folder.Requests.Should().HaveCount(2);
        folder.SubFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ParsesNestedSubFolders()
    {
        var json = PostmanJson("Test", [
            FolderItem("API", [
                FolderItem("Users", [
                    RequestItem("List", "GET", "https://example.com/users")
                ])
            ])
        ]);

        var path = WriteJson("nested.json", json);
        var result = await _sut.ImportAsync(path);

        var apiFolder = result.RootFolders[0];
        apiFolder.SubFolders.Should().HaveCount(1);
        apiFolder.SubFolders[0].Name.Should().Be("Users");
        apiFolder.SubFolders[0].Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ImportAsync_ItemOrderContainsMixedRootItems()
    {
        var json = PostmanJson("Test", [
            RequestItem("Root Req 1",  "GET",  "https://example.com/a"),
            FolderItem("My Folder", []),
            RequestItem("Root Req 2",  "POST", "https://example.com/b"),
        ]);

        var path = WriteJson("order.json", json);
        var result = await _sut.ImportAsync(path);

        result.ItemOrder.Should().ContainInOrder("Root Req 1", "My Folder", "Root Req 2");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — URL
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_UrlAsString_Parsed()
    {
        var json = PostmanJson("Test", [
            RequestItem("Req", "GET", "https://example.com/api/users")
        ]);

        var path = WriteJson("url_string.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Url.Should().Be("https://example.com/api/users");
    }

    [Fact]
    public async Task ImportAsync_UrlAsObject_UsesRawField()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "List Users",
                "request": {
                  "method": "GET",
                  "url": {
                    "raw": "https://api.example.com/users?status=active",
                    "host": ["api.example.com"],
                    "path": ["users"],
                    "query": [{ "key": "status", "value": "active" }]
                  }
                }
              }]
            }
            """;

        var path = WriteJson("url_object.json", json);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.Url.Should().Be("https://api.example.com/users?status=active");
        req.QueryParams.Should().HaveCount(1);
        req.QueryParams[0].Key.Should().Be("status");
        req.QueryParams[0].Value.Should().Be("active");
        req.QueryParams[0].IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_DisabledQueryParams_PreservedAsDisabled()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Search",
                "request": {
                  "method": "GET",
                  "url": {
                    "raw": "https://api.example.com/search",
                    "query": [
                      { "key": "q", "value": "hello", "disabled": false },
                      { "key": "debug", "value": "true", "disabled": true }
                    ]
                  }
                }
              }]
            }
            """;

        var path = WriteJson("query_disabled.json", json);
        var result = await _sut.ImportAsync(path);

        var q = result.RootRequests[0].QueryParams;
        q.Should().Contain(p => p.Key == "q" && p.IsEnabled);
        q.Should().Contain(p => p.Key == "debug" && !p.IsEnabled);
    }

    [Fact]
    public async Task ImportAsync_UrlObjectPathVariables_MappedToPathParams()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Get Policy",
                "request": {
                  "method": "GET",
                  "url": {
                    "raw": "{{url}}/api/v1/policies/{{policyId}}",
                    "host": ["{{url}}"],
                    "path": ["api", "v1", "policies", "{{policyId}}"],
                    "variable": [
                      { "key": "policyId", "value": "abc-123" }
                    ]
                  }
                }
              }]
            }
            """;

        var path = WriteJson("path_params.json", json);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.PathParams.Should().ContainKey("policyId").WhoseValue.Should().Be("abc-123");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — headers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ImportsHeaders()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "header": [
                    { "key": "Accept",        "value": "application/json" },
                    { "key": "Authorization", "value": "SSWS {{apikey}}" }
                  ],
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("headers.json", json);
        var result = await _sut.ImportAsync(path);

        var headers = result.RootRequests[0].Headers;
        headers.Should().Contain(h => h.Key == "Accept" && h.Value == "application/json" && h.IsEnabled);
        headers.Should().Contain(h => h.Key == "Authorization" && h.Value == "SSWS {{apikey}}" && h.IsEnabled);
    }

    [Fact]
    public async Task ImportAsync_DisabledHeaders_PreservedAsDisabled()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "header": [
                    { "key": "X-Active", "value": "yes",  "disabled": false },
                    { "key": "X-Off",    "value": "no",   "disabled": true  }
                  ],
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("headers_disabled.json", json);
        var result = await _sut.ImportAsync(path);

        var headers = result.RootRequests[0].Headers;
        headers.Should().Contain(h => h.Key == "X-Active" && h.IsEnabled);
        headers.Should().Contain(h => h.Key == "X-Off" && !h.IsEnabled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — body
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_RawJsonBody_WithLanguageOption_MapsToJson()
    {
        var path = WriteJson("body_json.json", RawBodyJson("json", """{"key":"value"}"""));
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be("json");
        result.RootRequests[0].Body.Should().Be("""{"key":"value"}""");
    }

    [Fact]
    public async Task ImportAsync_RawJsonBody_WithoutLanguageOption_AutoDetected()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "POST",
                  "body": { "mode": "raw", "raw": "{\"a\":1}" },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("body_auto.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be("json");
    }

    [Fact]
    public async Task ImportAsync_RawXmlBody_MapsToXml()
    {
        var path = WriteJson("body_xml.json", RawBodyJson("xml", "<root/>"));
        var result = await _sut.ImportAsync(path);
        result.RootRequests[0].BodyType.Should().Be("xml");
    }

    [Fact]
    public async Task ImportAsync_RawTextBody_MapsToText()
    {
        var path = WriteJson("body_text.json", RawBodyJson("text", "hello world"));
        var result = await _sut.ImportAsync(path);
        result.RootRequests[0].BodyType.Should().Be("text");
    }

    [Fact]
    public async Task ImportAsync_FormdataBody_MapsToMultipart()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Upload",
                "request": {
                  "method": "POST",
                  "body": {
                    "mode": "formdata",
                    "formdata": [
                      { "key": "field1", "value": "val1" },
                      { "key": "field2", "value": "val2", "disabled": true }
                    ]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("formdata.json", json);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.BodyType.Should().Be("multipart");
        req.Body.Should().BeNull();
        req.FormParams.Should().HaveCount(1);
        req.FormParams[0].Key.Should().Be("field1");
        req.FormParams[0].Value.Should().Be("val1");
    }

    [Fact]
    public async Task ImportAsync_UrlencodedBody_MapsToForm()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Token",
                "request": {
                  "method": "POST",
                  "body": {
                    "mode": "urlencoded",
                    "urlencoded": [
                      { "key": "grant_type", "value": "client_credentials" },
                      { "key": "client_id",  "value": "my-client" }
                    ]
                  },
                  "url": "https://example.com/token"
                }
              }]
            }
            """;

        var path = WriteJson("urlencoded.json", json);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.BodyType.Should().Be("form");
        req.FormParams.Should().HaveCount(2);
        req.FormParams.Should().Contain(p => p.Key == "grant_type" && p.Value == "client_credentials");
    }

    [Fact]
    public async Task ImportAsync_NoBody_BodyTypeIsNone()
    {
        var json = PostmanJson("Test", [RequestItem("GET req", "GET", "https://example.com")]);
        var path = WriteJson("nobody.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be("none");
        result.RootRequests[0].Body.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — auth
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_BearerAuth_Mapped()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "auth": {
                    "type": "bearer",
                    "bearer": [{ "key": "token", "value": "{{access_token}}", "type": "string" }]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_bearer.json", json);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        auth.Token.Should().Be("{{access_token}}");
    }

    [Fact]
    public async Task ImportAsync_BasicAuth_Mapped()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "auth": {
                    "type": "basic",
                    "basic": [
                      { "key": "username", "value": "admin", "type": "string" },
                      { "key": "password", "value": "secret", "type": "string" }
                    ]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_basic.json", json);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        auth.Username.Should().Be("admin");
        auth.Password.Should().Be("secret");
    }

    [Fact]
    public async Task ImportAsync_ApiKeyAuth_InHeader_Mapped()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "auth": {
                    "type": "apikey",
                    "apikey": [
                      { "key": "key",   "value": "X-API-Key",  "type": "string" },
                      { "key": "value", "value": "my-api-key", "type": "string" },
                      { "key": "in",    "value": "header",     "type": "string" }
                    ]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_apikey_header.json", json);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        auth.ApiKeyName.Should().Be("X-API-Key");
        auth.ApiKeyValue.Should().Be("my-api-key");
        auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Header);
    }

    [Fact]
    public async Task ImportAsync_ApiKeyAuth_InQuery_Mapped()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "auth": {
                    "type": "apikey",
                    "apikey": [
                      { "key": "key",   "value": "api_key", "type": "string" },
                      { "key": "value", "value": "xyz",     "type": "string" },
                      { "key": "in",    "value": "query",   "type": "string" }
                    ]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_apikey_query.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Query);
    }

    [Fact]
    public async Task ImportAsync_NoAuth_ReturnsNone()
    {
        var json = PostmanJson("Test", [RequestItem("Req", "GET", "https://example.com")]);
        var path = WriteJson("auth_none.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Auth.AuthType.Should().Be(AuthConfig.AuthTypes.None);
    }

    [Fact]
    public async Task ImportAsync_CollectionLevelAuth_AppliedToRequests()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": {
                "type": "bearer",
                "bearer": [{ "key": "token", "value": "coll-token", "type": "string" }]
              },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_collection.json", json);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        auth.Token.Should().Be("coll-token");
    }

    [Fact]
    public async Task ImportAsync_RequestLevelAuthOverridesCollectionAuth()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": {
                "type": "bearer",
                "bearer": [{ "key": "token", "value": "coll-token", "type": "string" }]
              },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "auth": {
                    "type": "bearer",
                    "bearer": [{ "key": "token", "value": "req-token", "type": "string" }]
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_override.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Auth.Token.Should().Be("req-token");
    }

    [Fact]
    public async Task ImportAsync_NoauthRequest_DisablesCollectionAuth()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": {
                "type": "bearer",
                "bearer": [{ "key": "token", "value": "coll-token", "type": "string" }]
              },
              "item": [{
                "name": "Public Req",
                "request": {
                  "method": "GET",
                  "auth": { "type": "noauth" },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("auth_noauth.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Auth.AuthType.Should().Be(AuthConfig.AuthTypes.None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — collection variables → environment
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_CollectionVariables_BecomeSingleEnvironment()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [],
              "variable": [
                { "key": "url",    "value": "https://api.example.com" },
                { "key": "apikey", "value": "my-key" }
              ]
            }
            """;

        var path = WriteJson("vars.json", json);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(1);
        var env = result.Environments[0];
        env.Name.Should().Be("Postman Variables");
        env.Variables.Should().ContainKey("url").WhoseValue.Should().Be("https://api.example.com");
        env.Variables.Should().ContainKey("apikey").WhoseValue.Should().Be("my-key");
    }

    [Fact]
    public async Task ImportAsync_NoCollectionVariables_EnvironmentsIsEmpty()
    {
        var json = PostmanJson("Test", []);
        var path = WriteJson("novars.json", json);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dynamic variable extraction  ({{$name}} → MockData global var)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_GuidDynamicVar_ExtractedToGlobalVar()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Create",
                "request": {
                  "method": "POST",
                  "body": { "mode": "raw", "raw": "{\"id\": \"{{$guid}}\"}", "options": {"raw": {"language": "json"}} },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_guid.json", json);
        var result = await _sut.ImportAsync(path);

        // Global dynamic var should be created
        var dynVar = result.GlobalDynamicVars.Should().ContainSingle().Subject;
        dynVar.MockDataCategory.Should().Be("Random");
        dynVar.MockDataField.Should().Be("UUID");

        // Body should reference it via {{random-uuid}}
        result.RootRequests[0].Body.Should().Contain("{{random-uuid}}");
    }

    [Fact]
    public async Task ImportAsync_RandomEmailDynamicVar_ExtractedToGlobalVar()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Create User",
                "request": {
                  "method": "POST",
                  "body": { "mode": "raw", "raw": "{\"email\": \"{{$randomEmail}}\"}", "options": {"raw": {"language": "json"}} },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_email.json", json);
        var result = await _sut.ImportAsync(path);

        var dynVar = result.GlobalDynamicVars.Should().ContainSingle().Subject;
        dynVar.MockDataCategory.Should().Be("Internet");
        dynVar.MockDataField.Should().Be("Email");
        result.RootRequests[0].Body.Should().Contain("{{internet-email}}");
    }

    [Fact]
    public async Task ImportAsync_RandomBsDynamicVar_ExtractedToGlobalVar()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Create Org",
                "request": {
                  "method": "POST",
                  "body": { "mode": "raw", "raw": "{\"description\": \"{{$randomBs}}\"}", "options": {"raw": {"language": "json"}} },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_bs.json", json);
        var result = await _sut.ImportAsync(path);

        var dynVar = result.GlobalDynamicVars.Should().ContainSingle().Subject;
        dynVar.MockDataCategory.Should().Be("Company");
        dynVar.MockDataField.Should().Be("Buzzwords");
        result.RootRequests[0].Body.Should().Contain("{{company-buzzwords}}");
    }

    [Fact]
    public async Task ImportAsync_RandomLoremSlug_ExtractedToGlobalVar()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Create Slug",
                "request": {
                  "method": "POST",
                  "body": { "mode": "raw", "raw": "{\"slug\": \"{{$randomLoremSlug}}\"}", "options": {"raw": {"language": "json"}} },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_slug.json", json);
        var result = await _sut.ImportAsync(path);

        var dynVar = result.GlobalDynamicVars.Should().ContainSingle().Subject;
        dynVar.MockDataCategory.Should().Be("Lorem");
        dynVar.MockDataField.Should().Be("Slug");
        result.RootRequests[0].Body.Should().Contain("{{lorem-slug}}");
    }

    [Fact]
    public async Task ImportAsync_UnknownDynamicVar_LeftAsIs()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "header": [{ "key": "X-Ts", "value": "{{$randomXyzzy}}" }],
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_unknown.json", json);
        var result = await _sut.ImportAsync(path);

        // $randomXyzzy has no MockData equivalent — left unchanged
        result.GlobalDynamicVars.Should().BeEmpty();
        result.RootRequests[0].Headers
            .Should().Contain(h => h.Key == "X-Ts" && h.Value == "{{$randomXyzzy}}");
    }

    [Fact]
    public async Task ImportAsync_DuplicateDynamicVar_Deduplicated()
    {
        // Same {{$guid}} appears in two requests — should produce exactly one global var.
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                {
                  "name": "Req 1",
                  "request": {
                    "method": "POST",
                    "body": { "mode": "raw", "raw": "{\"id\": \"{{$guid}}\"}", "options": {"raw": {"language": "json"}} },
                    "url": "https://example.com/a"
                  }
                },
                {
                  "name": "Req 2",
                  "request": {
                    "method": "POST",
                    "body": { "mode": "raw", "raw": "{\"ref\": \"{{$guid}}\"}", "options": {"raw": {"language": "json"}} },
                    "url": "https://example.com/b"
                  }
                }
              ]
            }
            """;

        var path = WriteJson("dynvar_dedup.json", json);
        var result = await _sut.ImportAsync(path);

        result.GlobalDynamicVars.Should().HaveCount(1);
        result.GlobalDynamicVars[0].Name.Should().Be("random-uuid");
    }

    [Fact]
    public async Task ImportAsync_DynamicVarInUrl_Extracted()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "GET",
                  "url": "https://example.com/items/{{$guid}}"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_url.json", json);
        var result = await _sut.ImportAsync(path);

        result.GlobalDynamicVars.Should().HaveCount(1);
        result.RootRequests[0].Url.Should().Contain("{{random-uuid}}");
    }

    [Fact]
    public async Task ImportAsync_MultipleDifferentDynamicVars_AllExtracted()
    {
        var json = """
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "POST",
                  "body": {
                    "mode": "raw",
                    "raw": "{\"id\": \"{{$guid}}\", \"name\": \"{{$randomFirstName}}\", \"email\": \"{{$randomEmail}}\"}",
                    "options": {"raw": {"language": "json"}}
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;

        var path = WriteJson("dynvar_multi.json", json);
        var result = await _sut.ImportAsync(path);

        result.GlobalDynamicVars.Should().HaveCount(3);
        result.GlobalDynamicVars.Should().Contain(v => v.MockDataCategory == "Random" && v.MockDataField == "UUID");
        result.GlobalDynamicVars.Should().Contain(v => v.MockDataCategory == "Name" && v.MockDataField == "First Name");
        result.GlobalDynamicVars.Should().Contain(v => v.MockDataCategory == "Internet" && v.MockDataField == "Email");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ExtractDynamicVars — unit tests
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{{$guid}}",         "random-uuid",             "Random",  "UUID")]
    [InlineData("{{$randomUUID}}",   "random-uuid",             "Random",  "UUID")]
    [InlineData("{{$randomInt}}",    "random-number",           "Random",  "Number")]
    [InlineData("{{$randomEmail}}",  "internet-email",          "Internet","Email")]
    [InlineData("{{$randomLoremSlug}}", "lorem-slug",           "Lorem",   "Slug")]
    [InlineData("{{$randomBs}}",     "company-buzzwords",       "Company", "Buzzwords")]
    [InlineData("{{$randomCity}}",   "address-city",            "Address", "City")]
    public void ExtractDynamicVars_KnownToken_ReplacedAndVarCreated(
        string input, string expectedVarName, string expectedCategory, string expectedField)
    {
        var globalVars = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);
        var result = PostmanCollectionImporter.ExtractDynamicVars(input, globalVars);

        result.Should().Be($"{{{{{expectedVarName}}}}}");
        globalVars.Should().ContainKey(expectedVarName);
        globalVars[expectedVarName].MockDataCategory.Should().Be(expectedCategory);
        globalVars[expectedVarName].MockDataField.Should().Be(expectedField);
    }

    [Theory]
    [InlineData("{{$notARealToken}}")]
    [InlineData("{{$randomXyzzy}}")]
    public void ExtractDynamicVars_UnknownToken_LeftUnchanged(string input)
    {
        var globalVars = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);
        var result = PostmanCollectionImporter.ExtractDynamicVars(input, globalVars);

        result.Should().Be(input);
        globalVars.Should().BeEmpty();
    }

    [Fact]
    public void ExtractDynamicVars_ValueWithNoTokens_ReturnedUnchanged()
    {
        var globalVars = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);
        const string input = "Bearer {{access_token}}";
        var result = PostmanCollectionImporter.ExtractDynamicVars(input, globalVars);

        result.Should().Be(input);
        globalVars.Should().BeEmpty();
    }

    [Fact]
    public void ExtractDynamicVars_MixedStaticAndDynamic_OnlyDynamicReplaced()
    {
        var globalVars = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);
        const string input = "prefix-{{$guid}}-suffix";
        var result = PostmanCollectionImporter.ExtractDynamicVars(input, globalVars);

        result.Should().Be("prefix-{{random-uuid}}-suffix");
        globalVars.Should().ContainKey("random-uuid");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PostmanDynamicVarMap coverage — key aliases work
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("guid",              "Random",  "UUID")]
    [InlineData("randomUUID",        "Random",  "UUID")]
    [InlineData("randomBoolean",     "Random",  "Boolean")]
    [InlineData("randomFirstName",   "Name",    "First Name")]
    [InlineData("randomFullName",    "Name",    "Full Name")]
    [InlineData("randomUserName",    "Internet","Username")]
    [InlineData("randomIP",          "Internet","IP Address")]
    [InlineData("randomPhoneNumber", "Phone",   "Phone Number")]
    [InlineData("randomCurrencyCode","Finance",  "Currency Code")]
    [InlineData("randomCompanyName", "Company", "Company Name")]
    [InlineData("randomDatePast",    "Date",    "Past Date")]
      // timestamps
      [InlineData("timestamp",          "Date",     "Timestamp")]
      [InlineData("isoTimestamp",       "Date",     "ISO Timestamp")]
      // fixed mappings
      [InlineData("randomIPV6",         "Internet", "IPv6 Address")]
      [InlineData("randomCountryCode",  "Address",  "Country Code")]
      [InlineData("randomStateAbbr",    "Address",  "State Abbreviation")]
      // Internet additions
      [InlineData("randomColor",        "Internet", "Color")]
      [InlineData("randomUserAgent",    "Internet", "User Agent")]
      [InlineData("randomAbbreviation", "Internet", "Abbreviation")]
      [InlineData("randomAvatarImage",  "Internet", "Avatar URL")]
      [InlineData("randomImageUrl",     "Internet", "Image URL")]
      [InlineData("randomImageDataUri", "Internet", "Image URL")]
      // Phone addition
      [InlineData("randomPhone",        "Phone",    "Phone Number")]
      // Finance addition
      [InlineData("randomTransactionType", "Finance", "Transaction Type")]
      // Company additions
      [InlineData("randomCatchPhraseAdjective",  "Company", "Catch Phrase Adjective")]
      [InlineData("randomCatchPhraseDescriptor", "Company", "Catch Phrase Descriptor")]
      [InlineData("randomCatchPhraseNoun",       "Company", "Catch Phrase Noun")]
      // Random additions
      [InlineData("randomObjectId",  "Random", "Object ID")]
      [InlineData("randomLocale",    "Random", "Locale")]
      [InlineData("randomExponent",  "Random", "Number")]
      // System
      [InlineData("randomSemver",        "System", "Semver")]
      [InlineData("randomMimeType",      "System", "MIME Type")]
      [InlineData("randomFileName",      "System", "File Name")]
      [InlineData("randomFileType",      "System", "File Type")]
      [InlineData("randomFileExt",       "System", "File Extension")]
      [InlineData("randomFilePath",      "System", "File Path")]
      [InlineData("randomDirectoryPath", "System", "Directory Path")]
    public void PostmanDynamicVarMap_ContainsExpectedEntry(
        string tokenName, string expectedCategory, string expectedField)
    {
        PostmanCollectionImporter.PostmanDynamicVarMap.Should().ContainKey(tokenName);
        var (cat, fld) = PostmanCollectionImporter.PostmanDynamicVarMap[tokenName];
        cat.Should().Be(expectedCategory);
        fld.Should().Be(expectedField);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string WriteJson(string name, string content)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string MinimalPostmanJson(
        string name = "Test Collection",
        string schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json")
    {
        return $$"""
            {
              "info": {
                "name": "{{name}}",
                "schema": "{{schema}}"
              },
              "item": []
            }
            """;
    }

    private static string PostmanJson(string name, List<string> items)
    {
        var itemsJson = string.Join(",\n", items);
        return $$"""
            {
              "info": {
                "name": "{{name}}",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [{{itemsJson}}]
            }
            """;
    }

    private static string RequestItem(string name, string method, string url) => $$"""
        {
          "name": "{{name}}",
          "request": {
            "method": "{{method}}",
            "url": "{{url}}"
          }
        }
        """;

    private static string FolderItem(string name, List<string> children)
    {
        var childrenJson = string.Join(",\n", children);
        return $$"""
            {
              "name": "{{name}}",
              "item": [{{childrenJson}}]
            }
            """;
    }

    private static string RawBodyJson(string language, string rawContent)
    {
        // Escape the rawContent for embedding in JSON
        var escaped = JsonSerializer.Serialize(rawContent);
        return $$"""
            {
              "info": { "name": "Test", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Req",
                "request": {
                  "method": "POST",
                  "body": {
                    "mode": "raw",
                    "raw": {{escaped}},
                    "options": { "raw": { "language": "{{language}}" } }
                  },
                  "url": "https://example.com"
                }
              }]
            }
            """;
    }
}
