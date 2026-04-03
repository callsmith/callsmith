using System.Linq;
using System.Net.Http;
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
