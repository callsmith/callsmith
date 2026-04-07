using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Callsmith.Core.Models;
using Callsmith.Core.OpenApi;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.OpenApi;

/// <summary>Tests for <see cref="OpenApiCollectionImporter"/>.</summary>
public sealed class OpenApiCollectionImporterTests : IDisposable
{
    private readonly OpenApiCollectionImporter _sut =
        new(NullLogger<OpenApiCollectionImporter>.Instance);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ─────────────────────────────────────────────────────────────────────────
    // CanImportAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForSwagger20JsonFile()
    {
        var path = Write("swagger.json", MinimalSwagger2Json());
        (await _sut.CanImportAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForOpenApi30YamlFile()
    {
        var path = Write("openapi.yaml", MinimalOas3Yaml());
        (await _sut.CanImportAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForOpenApi30JsonFile()
    {
        var path = Write("openapi.json", MinimalOas3Json());
        (await _sut.CanImportAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForOpenApi31JsonFile()
    {
        var json = """{"openapi":"3.1.0","info":{"title":"T","version":"1.0"},"paths":{}}""";
        var path = Write("openapi31.json", json);
        (await _sut.CanImportAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForUnsupportedOpenApi4()
    {
        var json = """{"openapi":"4.0.0","info":{"title":"T","version":"1.0"},"paths":{}}""";
        var path = Write("oas4.json", json);
        (await _sut.CanImportAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForUnsupportedSwagger1()
    {
        var json = """{"swagger":"1.2","info":{"title":"T","version":"1.0"}}""";
        var path = Write("sw1.json", json);
        (await _sut.CanImportAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForInvalidJson()
    {
        var path = Write("bad.json", "this is not json { or yaml }}}");
        (await _sut.CanImportAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForPostmanFile()
    {
        const string json = """{"info": {"name": "My API", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"}, "item": []}""";
        var path = Write("postman.json", json);
        (await _sut.CanImportAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForMissingFile()
    {
        (await _sut.CanImportAsync(Path.Combine(_temp.Path, "missing.yaml"))).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Collection name
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_OAS3_UsesInfoTitle()
    {
        var path = Write("api.yaml", MinimalOas3Yaml(title: "My Awesome API"));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("My Awesome API");
    }

    [Fact]
    public async Task ImportAsync_Swagger2_UsesInfoTitle()
    {
        var path = Write("api.json", MinimalSwagger2Json(title: "Petstore API"));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("Petstore API");
    }

    [Fact]
    public async Task ImportAsync_UsesDefaultNameWhenTitleIsEmpty()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: ""
              version: "1.0"
            paths: {}
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("Imported Collection");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Environments from servers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_OAS3_CreatesEnvironmentPerServer()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            servers:
              - url: https://api.example.com
                description: Production
              - url: https://staging.api.example.com
                description: Staging
            paths: {}
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(2);
        result.Environments[0].Name.Should().Be("Production");
        result.Environments[0].Variables["baseUrl"].Should().Be("https://api.example.com");
        result.Environments[1].Name.Should().Be("Staging");
        result.Environments[1].Variables["baseUrl"].Should().Be("https://staging.api.example.com");
    }

    [Fact]
    public async Task ImportAsync_OAS3_UseServerIndexNameWhenDescriptionMissing()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            servers:
              - url: https://api.example.com
              - url: https://staging.api.example.com
            paths: {}
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(2);
        result.Environments[0].Name.Should().Be("Default");
        result.Environments[1].Name.Should().Be("Server 2");
    }

    [Fact]
    public async Task ImportAsync_OAS3_UsesPlaceholderEnvironmentWhenNoServers()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths: {}
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(1);
        result.Environments[0].Name.Should().Be("Default");
        result.Environments[0].Variables["baseUrl"].Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_Swagger2_ConstructsBaseUrlFromHostAndBasePath()
    {
        var path = Write("api.json", MinimalSwagger2Json(
            host: "api.example.com", basePath: "/v1", scheme: "https"));
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(1);
        result.Environments[0].Name.Should().Be("Default");
        result.Environments[0].Variables["baseUrl"].Should().Be("https://api.example.com/v1");
    }

    [Fact]
    public async Task ImportAsync_OAS3_ResolvesServerUrlVariables()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            servers:
              - url: "https://{region}.api.example.com/v1"
                description: Regional
                variables:
                  region:
                    default: us-east
            paths: {}
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments[0].Variables["baseUrl"]
            .Should().Be("https://us-east.api.example.com/v1");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Operations / requests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_CreatesRequestPerOperation()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                post:
                  summary: Create user
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImportAsync_UsesOperationIdAsRequestName()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  operationId: listUsers
                  summary: Should be ignored
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Name.Should().Be("listUsers");
    }

    [Fact]
    public async Task ImportAsync_UsesSummaryAsRequestNameWhenNoOperationId()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List all users
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Name.Should().Be("List all users");
    }

    [Fact]
    public async Task ImportAsync_UsesMethodAndPathAsNameWhenNoOperationIdOrSummary()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  description: A request without a name
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Name.Should().Be("GET /users");
    }

    [Fact]
    public async Task ImportAsync_SetsCorrectHttpMethod()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                delete:
                  summary: Delete user
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task ImportAsync_GeneratesBaseUrlPlaceholderUrl()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users/{id}:
                get:
                  summary: Get user
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Url.Should().Be("{{baseUrl}}/users/{id}");
    }

    [Fact]
    public async Task ImportAsync_ExtractsPathParams()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users/{userId}/orders/{orderId}:
                get:
                  summary: Get order
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].PathParams.Keys.Should().BeEquivalentTo(["userId", "orderId"]);
    }

    [Fact]
    public async Task ImportAsync_PopulatesPathParamExampleFromParameterDefinition()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users/{userId}:
                get:
                  summary: Get user
                  parameters:
                    - name: userId
                      in: path
                      required: true
                      schema:
                        type: integer
                        example: 42
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].PathParams["userId"].Should().Be("42");
    }

    [Fact]
    public async Task ImportAsync_PopulatesPathParamExampleFromSchemaDefault()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items/{itemId}:
                get:
                  summary: Get item
                  parameters:
                    - name: itemId
                      in: path
                      required: true
                      schema:
                        type: string
                        default: abc123
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].PathParams["itemId"].Should().Be("abc123");
    }

    [Fact]
    public async Task ImportAsync_PathParamWithNoExampleUsesTypePlaceholder()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users/{userId}:
                get:
                  summary: Get user
                  parameters:
                    - name: userId
                      in: path
                      required: true
                      schema:
                        type: integer
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        // Integer type produces the same "0" placeholder as query params.
        result.RootRequests[0].PathParams["userId"].Should().Be(string.Empty);
    }

    [Fact]
    public async Task ImportAsync_PathParamNotInParameterListGetsEmptyString()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users/{userId}/orders/{orderId}:
                get:
                  summary: Get order
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].PathParams["userId"].Should().BeEmpty();
        result.RootRequests[0].PathParams["orderId"].Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ExtractsQueryParams()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                  parameters:
                    - name: limit
                      in: query
                      required: false
                    - name: page
                      in: query
                      required: true
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.QueryParams.Should().HaveCount(2);
        req.QueryParams.Select(q => q.Key).Should().BeEquivalentTo(["limit", "page"]);

        // Required params should be enabled; optional ones disabled.
        req.QueryParams.Single(q => q.Key == "limit").IsEnabled.Should().BeFalse();
        req.QueryParams.Single(q => q.Key == "page").IsEnabled.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Header parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ExtractsHeaderParams_OptionalIsDisabled()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                  parameters:
                    - name: correlation-id
                      in: header
                      required: false
                      schema:
                        type: string
                      example: abcdefg
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.Headers.Should().ContainSingle(h => h.Key == "correlation-id");
        var header = req.Headers.Single(h => h.Key == "correlation-id");
        header.Value.Should().Be("abcdefg");
        header.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ImportAsync_ExtractsHeaderParams_RequiredIsEnabled()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                  parameters:
                    - name: X-Api-Key
                      in: header
                      required: true
                      schema:
                        type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        var header = req.Headers.Single(h => h.Key == "X-Api-Key");
        header.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_ExtractsHeaderParams_WithSchemaExample()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                  parameters:
                    - name: X-Request-Id
                      in: header
                      required: false
                      schema:
                        type: string
                        format: uuid
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var header = result.RootRequests[0].Headers.Single(h => h.Key == "X-Request-Id");
        header.Value.Should().Be(string.Empty);
    }

    [Fact]
    public async Task ImportAsync_ExtractsHeaderParams_DoesNotAddContentTypeForBody()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                post:
                  summary: Create user
                  requestBody:
                    content:
                      application/json:
                        schema:
                          type: object
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        // Content-Type is managed by Callsmith from the body type selection —
        // it must NOT be auto-generated into the imported header list.
        result.RootRequests[0].Headers
            .Where(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ExtractsHeaderAndQueryParams_Together()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  summary: List users
                  parameters:
                    - name: limit
                      in: query
                      required: false
                    - name: correlation-id
                      in: header
                      required: false
                      example: trace-123
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.QueryParams.Should().ContainSingle(q => q.Key == "limit");
        req.Headers.Should().ContainSingle(h => h.Key == "correlation-id");
        req.Headers.Single(h => h.Key == "correlation-id").Value.Should().Be("trace-123");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tag-based folder grouping
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_GroupsOperationsByFirstTagIntoFolders()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                get:
                  tags: [Users]
                  summary: List users
                post:
                  tags: [Users]
                  summary: Create user
              /products:
                get:
                  tags: [Products]
                  summary: List products
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().BeEmpty();
        result.RootFolders.Should().HaveCount(2);

        var usersFolder = result.RootFolders.Single(f => f.Name == "Users");
        usersFolder.Requests.Should().HaveCount(2);

        var productsFolder = result.RootFolders.Single(f => f.Name == "Products");
        productsFolder.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ImportAsync_PlacesUntaggedOperationsAtRoot()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /health:
                get:
                  summary: Health check
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().HaveCount(1);
        result.RootRequests[0].Name.Should().Be("Health check");
        result.RootFolders.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Request body
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_OAS3_SetsJsonBodyTypeForApplicationJson()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                post:
                  summary: Create user
                  requestBody:
                    content:
                      application/json:
                        schema:
                          type: object
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.BodyType.Should().Be(CollectionRequest.BodyTypes.Json);
        req.Body.Should().Be("{}");
    }

    [Fact]
    public async Task ImportAsync_Swagger2_SetsJsonBodyTypeForBodyParameter()
    {
        var json = """
            {
              "swagger": "2.0",
              "info": { "title": "Test", "version": "1.0" },
              "host": "api.example.com",
              "paths": {
                "/users": {
                  "post": {
                    "summary": "Create user",
                    "parameters": [
                      { "name": "body", "in": "body", "schema": { "type": "object" } }
                    ]
                  }
                }
              }
            }
            """;
        var path = Write("api.json", json);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be(CollectionRequest.BodyTypes.Json);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JSON format (Swagger 2)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ParsesSwagger2JsonFormat()
    {
        var path = Write("petstore.json", PetstoreSwagger2Json());
        var result = await _sut.ImportAsync(path);

        result.Name.Should().Be("Petstore");
        result.RootFolders.Should().ContainSingle(f => f.Name == "pet");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Path-level parameters inheritance
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_InheritsPathLevelQueryParams()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items:
                parameters:
                  - name: apiVersion
                    in: query
                    required: true
                get:
                  summary: List items
                post:
                  summary: Create item
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        foreach (var req in result.RootRequests)
        {
            req.QueryParams.Should().ContainSingle(q => q.Key == "apiVersion");
        }
    }

    [Fact]
    public async Task ImportAsync_OperationParamsOverridePathLevelParams()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items:
                parameters:
                  - name: search
                    in: query
                    required: false
                get:
                  summary: List items
                  parameters:
                    - name: search
                      in: query
                      required: true
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        // Operation-level overrides path-level — should appear once, as required.
        var req = result.RootRequests[0];
        req.QueryParams.Should().ContainSingle(q => q.Key == "search");
        req.QueryParams.Single(q => q.Key == "search").IsEnabled.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // $ref resolution
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ResolvesRefInRequestBody()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                post:
                  summary: Create user
                  requestBody:
                    $ref: "#/components/requestBodies/UserBody"
            components:
              requestBodies:
                UserBody:
                  content:
                    application/json:
                      schema:
                        type: object
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be(CollectionRequest.BodyTypes.Json);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body example synthesis
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_OAS3_GeneratesExampleFromInlineObjectProperties()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                post:
                  summary: Create user
                  requestBody:
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            name:
                              type: string
                            age:
                              type: integer
                            active:
                              type: boolean
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("string");
        root.GetProperty("age").GetInt32().Should().Be(0);
        root.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_OAS3_DoesNotEscapeSpecialCharsInBodyExample()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /search:
                post:
                  summary: Search
                  requestBody:
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            query:
                              type: string
                              example: "foo&bar<baz>"
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        // Characters like & < > must appear literally, not as \u0026 etc.
        body.Should().Contain("foo&bar<baz>");
        body.Should().NotContain(@"\u0026");
        body.Should().NotContain(@"\u003c");
    }

    [Fact]
    public async Task ImportAsync_OAS3_GeneratesExampleFromSchemaRef()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /users:
                post:
                  summary: Create user
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: "#/components/schemas/User"
            components:
              schemas:
                User:
                  type: object
                  properties:
                    name:
                      type: string
                    email:
                      type: string
                      format: email
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("string");
        root.GetProperty("email").GetString().Should().Be("user@example.com");
    }

    [Fact]
    public async Task ImportAsync_OAS3_ResolvesNestedSchemaRefs()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /orders:
                post:
                  summary: Create order
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: "#/components/schemas/Order"
            components:
              schemas:
                Order:
                  type: object
                  properties:
                    id:
                      type: integer
                    customer:
                      $ref: "#/components/schemas/Customer"
                Customer:
                  type: object
                  properties:
                    name:
                      type: string
                    address:
                      $ref: "#/components/schemas/Address"
                Address:
                  type: object
                  properties:
                    street:
                      type: string
                    city:
                      type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("id").GetInt32().Should().Be(0);
        var customer = root.GetProperty("customer");
        customer.GetProperty("name").GetString().Should().Be("string");
        var address = customer.GetProperty("address");
        address.GetProperty("street").GetString().Should().Be("string");
        address.GetProperty("city").GetString().Should().Be("string");
    }

    [Fact]
    public async Task ImportAsync_OAS3_HandlesCircularRefWithoutInfiniteLoop()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /nodes:
                post:
                  summary: Create node
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: "#/components/schemas/TreeNode"
            components:
              schemas:
                TreeNode:
                  type: object
                  properties:
                    value:
                      type: string
                    children:
                      type: array
                      items:
                        $ref: "#/components/schemas/TreeNode"
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        // Should complete without a stack overflow; body must be valid JSON.
        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        var act = () => JsonDocument.Parse(body!);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ImportAsync_OAS3_GeneratesExampleFromAllOf()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /pets:
                post:
                  summary: Create pet
                  requestBody:
                    content:
                      application/json:
                        schema:
                          allOf:
                            - $ref: "#/components/schemas/Animal"
                            - type: object
                              properties:
                                petType:
                                  type: string
            components:
              schemas:
                Animal:
                  type: object
                  properties:
                    name:
                      type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("string");
        root.GetProperty("petType").GetString().Should().Be("string");
    }

    [Fact]
    public async Task ImportAsync_OAS3_AnyOfSkipsNullTypeAndUsesObjectOption()
    {
        // OAS 3.1 nullable pattern: anyOf: [{ type: "null" }, { type: object, ... }]
        // The null type should be skipped; the object schema should be used.
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items:
                post:
                  summary: Create item
                  requestBody:
                    content:
                      application/json:
                        schema:
                          anyOf:
                            - type: "null"
                            - type: object
                              properties:
                                name:
                                  type: string
                                count:
                                  type: integer
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("name").GetString().Should().Be("string");
        root.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_OAS3_OneOfSkipsNullTypeAndUsesObjectOption()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items:
                post:
                  summary: Create item
                  requestBody:
                    content:
                      application/json:
                        schema:
                          oneOf:
                            - type: "null"
                            - type: object
                              properties:
                                label:
                                  type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("label").GetString().Should().Be("string");
    }

    [Fact]
    public async Task ImportAsync_OAS3_AnyOfSkipsUntypedEmptySchemaAndUsesObjectOption()
    {
        // An empty/untyped sub-schema (matches anything) should not block a more
        // informative subsequent option in anyOf.
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /items:
                post:
                  summary: Create item
                  requestBody:
                    content:
                      application/json:
                        schema:
                          anyOf:
                            - description: anything
                            - type: object
                              properties:
                                value:
                                  type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("value").GetString().Should().Be("string");
    }

    [Fact]
    public async Task ImportAsync_OAS3_GeneratesExampleForArrayOfSchemaRef()
    {
        var yaml = """
            openapi: "3.0.0"
            info:
              title: Test
              version: "1.0"
            paths:
              /bulk:
                post:
                  summary: Bulk create
                  requestBody:
                    content:
                      application/json:
                        schema:
                          type: array
                          items:
                            $ref: "#/components/schemas/Item"
            components:
              schemas:
                Item:
                  type: object
                  properties:
                    id:
                      type: integer
                    label:
                      type: string
            """;
        var path = Write("api.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(1);
        var item = root[0];
        item.GetProperty("id").GetInt32().Should().Be(0);
        item.GetProperty("label").GetString().Should().Be("string");
    }

    [Fact]
    public async Task ImportAsync_OAS3_UsesExplicitMediaTypeExampleOverGenerated()
    {
        // Use JSON spec so that "age": 30 is preserved as a JSON number
        // (YAML→JSON conversion may coerce unquoted scalars to strings).
        var json = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Test", "version": "1.0" },
              "paths": {
                "/users": {
                  "post": {
                    "summary": "Create user",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "example": { "name": "Alice", "age": 30 },
                          "schema": {
                            "type": "object",
                            "properties": {
                              "name": { "type": "string" },
                              "age":  { "type": "integer" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var path = Write("api.json", json);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("Alice");
        root.GetProperty("age").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task ImportAsync_OAS3_UsesSchemaExampleOverGenerated()
    {
        // Use JSON spec so that "age": 25 is preserved as a JSON number.
        var json = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Test", "version": "1.0" },
              "paths": {
                "/users": {
                  "post": {
                    "summary": "Create user",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "example": { "name": "Bob", "age": 25 },
                            "properties": {
                              "name": { "type": "string" },
                              "age":  { "type": "integer" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var path = Write("api.json", json);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("Bob");
        root.GetProperty("age").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task ImportAsync_Swagger2_GeneratesExampleFromBodyParamSchemaRef()
    {
        var json = """
            {
              "swagger": "2.0",
              "info": { "title": "Test", "version": "1.0" },
              "host": "api.example.com",
              "paths": {
                "/users": {
                  "post": {
                    "summary": "Create user",
                    "parameters": [
                      {
                        "name": "body",
                        "in": "body",
                        "schema": { "$ref": "#/definitions/User" }
                      }
                    ]
                  }
                }
              },
              "definitions": {
                "User": {
                  "type": "object",
                  "properties": {
                    "username": { "type": "string" },
                    "score":    { "type": "number" }
                  }
                }
              }
            }
            """;
        var path = Write("api.json", json);
        var result = await _sut.ImportAsync(path);

        var body = result.RootRequests[0].Body;
        body.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("username").GetString().Should().Be("string");
        root.GetProperty("score").GetInt32().Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string Write(string name, string content)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string MinimalOas3Yaml(string title = "Test API")
        => $"openapi: \"3.0.0\"\ninfo:\n  title: {title}\n  version: \"1.0\"\npaths: {{}}\n";

    private static string MinimalOas3Json(string title = "Test API")
        => "{\n  \"openapi\": \"3.0.0\",\n  \"info\": { \"title\": \""
         + title
         + "\", \"version\": \"1.0\" },\n  \"paths\": {}\n}";

    private static string MinimalSwagger2Json(
        string title = "Test API",
        string host = "api.example.com",
        string basePath = "/",
        string scheme = "https")
        => "{\n  \"swagger\": \"2.0\",\n  \"info\": { \"title\": \""
         + title
         + "\", \"version\": \"1.0\" },\n  \"host\": \""
         + host
         + "\",\n  \"basePath\": \""
         + basePath
         + "\",\n  \"schemes\": [\""
         + scheme
         + "\"],\n  \"paths\": {}\n}";

    private static string PetstoreSwagger2Json() => """
        {
          "swagger": "2.0",
          "info": { "title": "Petstore", "version": "1.0.0" },
          "host": "petstore.swagger.io",
          "basePath": "/v2",
          "schemes": ["https"],
          "paths": {
            "/pet": {
              "get": {
                "tags": ["pet"],
                "summary": "Finds Pets",
                "operationId": "findPets",
                "parameters": []
              },
              "post": {
                "tags": ["pet"],
                "summary": "Add a new pet",
                "operationId": "addPet",
                "parameters": [
                  { "name": "body", "in": "body", "schema": { "type": "object" } }
                ]
              }
            }
          }
        }
        """;
}
