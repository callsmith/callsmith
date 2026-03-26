using System.Linq;
using Callsmith.Core.Insomnia;
using Callsmith.Core.Models;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Insomnia;

/// <summary>Tests for <see cref="InsomniaCollectionImporter"/>.</summary>
public sealed class InsomniaCollectionImporterTests : IDisposable
{
    private readonly InsomniaCollectionImporter _sut =
        new(NullLogger<InsomniaCollectionImporter>.Instance);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ─────────────────────────────────────────────────────────────────────────
    // CanImportAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CanImportAsync_ReturnsTrueForInsomniaV5File()
    {
        var path = WriteYaml("can_import.yaml", MinimalInsomniaYaml());
        var result = await _sut.CanImportAsync(path);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForNonInsomniaFile()
    {
        var path = WriteYaml("not_insomnia.yaml", "type: something.else/1.0\nname: Other");
        var result = await _sut.CanImportAsync(path);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanImportAsync_ReturnsFalseForMissingFile()
    {
        var result = await _sut.CanImportAsync(Path.Combine(_temp.Path, "missing.yaml"));
        result.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — basic structure
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_PopulatesCollectionName()
    {
        var path = WriteYaml("named.yaml", MinimalInsomniaYaml("My API"));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("My API");
    }

    [Fact]
    public async Task ImportAsync_UsesDefaultNameWhenNameIsBlank()
    {
        var path = WriteYaml("unnamed.yaml", MinimalInsomniaYaml(name: ""));
        var result = await _sut.ImportAsync(path);
        result.Name.Should().Be("Imported Collection");
    }

    [Fact]
    public async Task ImportAsync_ParsesRootRequest()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/api
                name: Get Users
                method: GET
                meta:
                  id: req_001
            """;

        var path = WriteYaml("req.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().HaveCount(1);
        result.RootFolders.Should().BeEmpty();

        var req = result.RootRequests[0];
        req.Name.Should().Be("Get Users");
        req.Url.Should().Be("https://example.com/api");
        req.Method.Method.Should().Be("GET");
    }

    [Fact]
    public async Task ImportAsync_ParsesFolderWithNestedRequests()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - name: Users
                meta:
                  id: fld_001
                children:
                  - url: https://example.com/users
                    name: List Users
                    method: GET
                    meta:
                      id: req_001
                  - url: https://example.com/users/1
                    name: Get User
                    method: GET
                    meta:
                      id: req_002
            """;

        var path = WriteYaml("folder.yaml", yaml);
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
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - name: API
                meta:
                  id: fld_001
                children:
                  - name: Users
                    meta:
                      id: fld_002
                    children:
                      - url: https://example.com/users
                        name: List
                        method: GET
                        meta:
                          id: req_001
            """;

        var path = WriteYaml("nested.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var apiFolder = result.RootFolders[0];
        apiFolder.SubFolders.Should().HaveCount(1);
        apiFolder.SubFolders[0].Name.Should().Be("Users");
        apiFolder.SubFolders[0].Requests.Should().HaveCount(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — headers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ImportsEnabledHeadersOnly()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: POST
                meta:
                  id: req_001
                headers:
                  - name: Content-Type
                    value: application/json
                    disabled: false
                  - name: X-Skip
                    value: skip-me
                    disabled: true
                  - name: Authorization
                    value: Bearer abc123
                    disabled: false
            """;

        var path = WriteYaml("headers.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var headers = result.RootRequests[0].Headers;
        headers.Should().Contain(h => h.Key == "Content-Type" && h.Value == "application/json" && h.IsEnabled);
        headers.Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer abc123" && h.IsEnabled);
        headers.Should().NotContain(h => h.Key == "X-Skip" && h.IsEnabled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — body
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/json", "json")]
    [InlineData("text/plain", "text")]
    [InlineData("application/xml", "xml")]
    [InlineData("multipart/form-data", "multipart")]
    public async Task ImportAsync_MapsBodyMimeTypeToCallsmithBodyType(
        string mimeType, string expectedBodyType)
    {
        var yaml = $$"""
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: POST
                meta:
                  id: req_001
                body:
                  mimeType: {{mimeType}}
                  text: "some content"
            """;

        var path = WriteYaml("body.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].BodyType.Should().Be(expectedBodyType);
        result.RootRequests[0].Body.Should().Be("some content");
    }

    [Fact]
    public async Task ImportAsync_FormBody_MapsParamsToFormParams()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/token
                name: Req
                method: POST
                meta:
                  id: req_001
                body:
                  mimeType: application/x-www-form-urlencoded
                  params:
                    - name: grant_type
                      value: client_credentials
                    - name: client_id
                      value: my-client
                    - name: disabled_param
                      value: ignored
                      disabled: true
            """;

        var path = WriteYaml("form_body.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.BodyType.Should().Be("form");
        req.Body.Should().BeNull();
        req.FormParams.Should().HaveCount(2);
        req.FormParams.Should().Contain(p => p.Key == "grant_type" && p.Value == "client_credentials");
        req.FormParams.Should().Contain(p => p.Key == "client_id" && p.Value == "my-client");
    }

    [Fact]
    public async Task ImportAsync_SetsBodyTypeToNoneWhenBodyIsAbsent()
    {
        var path = WriteYaml("nobody.yaml", SingleRequestYaml("GET", "https://example.com", null));
        var result = await _sut.ImportAsync(path);
        result.RootRequests[0].BodyType.Should().Be("none");
        result.RootRequests[0].Body.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — environments
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ParsesSubEnvironments()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection: []
            environments:
              name: Base
              meta:
                id: env_001
              data:
                base-key: base-value
              subEnvironments:
                - name: Dev
                  meta:
                    id: env_002
                    sortKey: 1
                  data:
                    api-url: https://dev.example.com
                  color: "#007bff"
                - name: Prod
                  meta:
                    id: env_003
                    sortKey: 2
                  data:
                    api-url: https://api.example.com
            """;

        var path = WriteYaml("envs.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(2);

        var dev = result.Environments.First(e => e.Name == "Dev");
        dev.Color.Should().Be("#007bff");
        dev.Variables.Should().ContainKey("api-url").WhoseValue.Should().Be("https://dev.example.com");
        // Base key should be merged into each sub-env
        dev.Variables.Should().ContainKey("base-key").WhoseValue.Should().Be("base-value");

        var prod = result.Environments.First(e => e.Name == "Prod");
        prod.Variables.Should().ContainKey("api-url").WhoseValue.Should().Be("https://api.example.com");
    }

    [Fact]
    public async Task ImportAsync_ExcludesScriptVariablesFromEnvironments()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection: []
            environments:
              name: Base
              meta:
                id: env_001
              data:
                static-key: static-value
                script-key: "{% response 'body', 'req_abc', 'token' %}"
            """;

        var path = WriteYaml("script_env.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(1);
        var env = result.Environments[0];
        env.Variables.Should().ContainKey("static-key");
        env.Variables.Should().NotContainKey("script-key");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — sort order
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SortsRootItemsBySortKeyAscending()
    {
        // Insomnia sortKeys are negative; most-negative = created first = displayed first.
        // YAML order here is deliberately reversed (C, B, A) to test that sort is applied.
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/c
                name: C Request
                method: GET
                meta:
                  id: req_c
                  sortKey: -100
              - name: B Folder
                meta:
                  id: fld_b
                  sortKey: -200
                children: []
              - url: https://example.com/a
                name: A Request
                method: GET
                meta:
                  id: req_a
                  sortKey: -300
            """;

        var path = WriteYaml("sort_root.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        // ItemOrder should be: A Request, B Folder, C Request  (ascending sortKey)
        result.ItemOrder.Should().ContainInOrder("A Request", "B Folder", "C Request");
        result.RootRequests[0].Name.Should().Be("A Request");
        result.RootFolders[0].Name.Should().Be("B Folder");
        result.RootRequests[1].Name.Should().Be("C Request");
    }

    [Fact]
    public async Task ImportAsync_SortsFolderChildrenBySortKey()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - name: Parent
                meta:
                  id: fld_parent
                  sortKey: -1000
                children:
                  - url: https://example.com/z
                    name: Z Request
                    method: GET
                    meta:
                      id: req_z
                      sortKey: -50
                  - url: https://example.com/a
                    name: A Request
                    method: GET
                    meta:
                      id: req_a
                      sortKey: -500
            """;

        var path = WriteYaml("sort_children.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var folder = result.RootFolders[0];
        folder.ItemOrder.Should().ContainInOrder("A Request", "Z Request");
        folder.Requests[0].Name.Should().Be("A Request");
        folder.Requests[1].Name.Should().Be("Z Request");
    }

    [Fact]
    public async Task ImportAsync_SortsSubEnvironmentsBySortKey()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection: []
            environments:
              name: Base
              meta:
                id: env_base
              subEnvironments:
                - name: Prod
                  meta:
                    id: env_prod
                    sortKey: 3000
                - name: Dev
                  meta:
                    id: env_dev
                    sortKey: 1000
                - name: Staging
                  meta:
                    id: env_staging
                    sortKey: 2000
            """;

        var path = WriteYaml("sort_envs.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Select(e => e.Name)
            .Should().ContainInOrder("Dev", "Staging", "Prod");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Variable normalization
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{{simple}}", "{{simple}}")]
    [InlineData("{{ _['var-name'] }}", "{{var-name}}")]
    [InlineData("{{ _.varName }}", "{{varName}}")]
    [InlineData("prefix {{ _['key'] }} suffix", "prefix {{key}} suffix")]
    [InlineData("{% response 'body' %}", "{% response 'body' %}")]
    [InlineData("no variables here", "no variables here")]
    public void NormalizeVariables_ConvertsInsomniaToCallsmithSyntax(
        string input, string expected)
    {
        var result = InsomniaCollectionImporter.NormalizeVariables(input);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task ImportAsync_NormalizesVariablesInRequestUrl()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: "{{ _['base-url'] }}/api/users"
                name: List
                method: GET
                meta:
                  id: req_001
            """;

        var path = WriteYaml("varurl.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Url.Should().Be("{{base-url}}/api/users");
    }

    [Fact]
    public async Task ImportAsync_NormalizesVariablesInHeaders()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
                headers:
                  - name: Authorization
                    value: "AccessToken {{ _['access-token'] }}"
                    disabled: false
            """;

        var path = WriteYaml("varheader.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Headers.Single(h => h.Key == "Authorization").Value
            .Should().Be("AccessToken {{access-token}}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — authentication
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_MapsBearerAuth()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
                authentication:
                  type: bearer
                  token: "{{my-token}}"
            """;

        var path = WriteYaml("auth_bearer.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        auth.Token.Should().Be("{{my-token}}");
    }

    [Fact]
    public async Task ImportAsync_MapsBasicAuth()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
                authentication:
                  type: basic
                  username: "{{user}}"
                  password: s3cr3t
            """;

        var path = WriteYaml("auth_basic.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        auth.Username.Should().Be("{{user}}");
        auth.Password.Should().Be("s3cr3t");
    }

    [Fact]
    public async Task ImportAsync_MapsApiKeyAuthInHeader()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
                authentication:
                  type: apikey
                  key: X-Api-Key
                  value: abc123
                  addTo: header
            """;

        var path = WriteYaml("auth_apikey_header.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        auth.ApiKeyName.Should().Be("X-Api-Key");
        auth.ApiKeyValue.Should().Be("abc123");
        auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Header);
    }

    [Fact]
    public async Task ImportAsync_MapsApiKeyAuthInQuery()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
                authentication:
                  type: apikey
                  key: api_key
                  value: "{{key}}"
                  addTo: queryParams
            """;

        var path = WriteYaml("auth_apikey_query.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var auth = result.RootRequests[0].Auth;
        auth.AuthType.Should().Be(AuthConfig.AuthTypes.ApiKey);
        auth.ApiKeyIn.Should().Be(AuthConfig.ApiKeyLocations.Query);
    }

    [Fact]
    public async Task ImportAsync_SetsAuthToNoneWhenAuthIsAbsent()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com
                name: Req
                method: GET
                meta:
                  id: req_001
            """;

        var path = WriteYaml("auth_none.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Auth.AuthType.Should().Be(AuthConfig.AuthTypes.None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — query parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_PreservesDisabledQueryParamsWithIsEnabledFalse()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/api/users
                name: Find Users
                method: GET
                meta:
                  id: req_001
                parameters:
                  - name: emailPattern
                    value: "test*"
                    disabled: false
                  - name: skipMe
                    value: ignored
                    disabled: true
                  - name: pageSize
                    value: "20"
                    disabled: false
            """;

        var path = WriteYaml("query_params.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.QueryParams.Should().HaveCount(3);
        req.QueryParams.Should().Contain(p => p.Key == "emailPattern" && p.Value == "test*" && p.IsEnabled);
        req.QueryParams.Should().Contain(p => p.Key == "pageSize" && p.Value == "20" && p.IsEnabled);
        req.QueryParams.Should().Contain(p => p.Key == "skipMe" && p.Value == "ignored" && !p.IsEnabled);
    }

    [Fact]
    public async Task ImportAsync_NormalizesVariablesInQueryParamValues()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/api
                name: Req
                method: GET
                meta:
                  id: req_001
                parameters:
                  - name: token
                    value: "{{ _['access-token'] }}"
                    disabled: false
            """;

        var path = WriteYaml("var_query_param.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].QueryParams
            .First(p => p.Key == "token").Value.Should().Be("{{access-token}}");
    }

    [Fact]
    public async Task ImportAsync_ReturnsEmptyQueryParamsWhenNonePresent()
    {
        var path = WriteYaml("no_query.yaml", SingleRequestYaml("GET", "https://example.com", null));
        var result = await _sut.ImportAsync(path);
        result.RootRequests[0].QueryParams.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_PreservesDuplicateQueryParamNames()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/api/roles
                name: Find By Roles
                method: GET
                meta:
                  id: req_001
                parameters:
                  - name: roleNames
                    value: ADMIN
                    disabled: false
                  - name: roleNames
                    value: MANAGER
                    disabled: false
                  - name: roleNames
                    value: VIEWER
                    disabled: false
            """;

        var path = WriteYaml("dup_query_params.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.QueryParams.Should().HaveCount(3);
        req.QueryParams.Select(p => p.Key).Should().AllBe("roleNames");
        req.QueryParams.Select(p => p.Value).Should().BeEquivalentTo(["ADMIN", "MANAGER", "VIEWER"]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ImportAsync — path parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ImportsPathParamValues()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/users/:username
                name: Get User
                method: GET
                meta:
                  id: req_001
                pathParameters:
                  - name: username
                    value: "acct:commerce@example.com"
            """;

        var path = WriteYaml("path_params.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var req = result.RootRequests[0];
        req.PathParams.Should().ContainKey("username")
            .WhoseValue.Should().Be("acct:commerce@example.com");
    }

    [Fact]
    public async Task ImportAsync_ConvertsColonSyntaxToCallsmithBraceSyntaxInUrl()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/users/:username/accounts
                name: Get Accounts
                method: GET
                meta:
                  id: req_001
            """;

        var path = WriteYaml("path_syntax.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Url.Should().Be("https://example.com/users/{username}/accounts");
    }

    [Fact]
    public async Task ImportAsync_ConvertsMultiplePathParamSegments()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/:orgId/users/:userId/roles
                name: Get Roles
                method: GET
                meta:
                  id: req_001
            """;

        var path = WriteYaml("multi_path_params.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests[0].Url
            .Should().Be("https://example.com/{orgId}/users/{userId}/roles");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertPathParamSyntax unit tests
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/users/:id", "/users/{id}")]
    [InlineData("/users/:id/accounts", "/users/{id}/accounts")]
    [InlineData("/:org/:id/roles", "/{org}/{id}/roles")]
    [InlineData("https://example.com/users/:username", "https://example.com/users/{username}")]
    [InlineData("/users/:id?query=1", "/users/{id}?query=1")]
    [InlineData("/no-params/here", "/no-params/here")]
    [InlineData("", "")]
    public void ConvertPathParamSyntax_ConvertsColonParamsToCallsmithBraces(
        string input, string expected)
    {
        var result = InsomniaCollectionImporter.ConvertPathParamSyntax(input);
        result.Should().Be(expected);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dynamic variables — ParseDynamicValue
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDynamicValue_ParsesPureResponseTag_AllExpired()
    {
        // b64::JC50b2tlbg==::46b decodes to "$.token"
        var value = "{% response 'body', 'req_abc', 'b64::JC50b2tlbg==::46b', 'when-expired', 900 %}";
        var idMap = new Dictionary<string, string> { ["req_abc"] = "Auth/Get Token" };

        var segments = InsomniaCollectionImporter.ParseDynamicValue(value, idMap);

        segments.Should().HaveCount(1);
        var dyn = segments[0].Should().BeOfType<DynamicValueSegment>().Subject;
        dyn.RequestName.Should().Be("Auth/Get Token");
        dyn.Path.Should().Be("$.token");
        dyn.Frequency.Should().Be(DynamicFrequency.IfExpired);
        dyn.ExpiresAfterSeconds.Should().Be(900);
    }

    [Fact]
    public void ParseDynamicValue_ParsesMixedValue_StaticPrefixPlusDynamic()
    {
        var value = "AccessToken {% response 'body', 'req_abc', '$.access_token', 'always' %}";
        var idMap = new Dictionary<string, string> { ["req_abc"] = "Login" };

        var segments = InsomniaCollectionImporter.ParseDynamicValue(value, idMap);

        segments.Should().HaveCount(2);
        segments[0].Should().BeOfType<StaticValueSegment>()
            .Which.Text.Should().Be("AccessToken ");
        var dyn = segments[1].Should().BeOfType<DynamicValueSegment>().Subject;
        dyn.RequestName.Should().Be("Login");
        dyn.Path.Should().Be("$.access_token");
        dyn.Frequency.Should().Be(DynamicFrequency.Always);
        dyn.ExpiresAfterSeconds.Should().BeNull();
    }

    [Fact]
    public void ParseDynamicValue_FallsBackToRequestIdWhenNotInMap()
    {
        var value = "{% response 'body', 'req_unknown', '$.token', 'never' %}";
        var idMap = new Dictionary<string, string>();

        var segments = InsomniaCollectionImporter.ParseDynamicValue(value, idMap);

        segments.Should().HaveCount(1);
        var dyn = segments[0].Should().BeOfType<DynamicValueSegment>().Subject;
        dyn.RequestName.Should().Be("req_unknown");
        dyn.Frequency.Should().Be(DynamicFrequency.Never);
    }

    [Fact]
    public void ParseDynamicValue_DecodesBase64Path()
    {
        // $.access_token in base64 = "JC5hY2Nlc3NfdG9rZW4="
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("$.access_token"));
        var value = $"{{% response 'body', 'req_1', 'b64::{b64}::46b', 'always' %}}";
        var idMap = new Dictionary<string, string> { ["req_1"] = "Login" };

        // Note: The value is NOT an interpolated string above — the {} are part of Insomnia syntax
        // Use a raw string literal instead:
        value = $"{{% response 'body', 'req_1', 'b64::{b64}::46b', 'always' %}}";
        // Reset to proper Insomnia syntax (the test string above had escaping issues):
        value = "{% response 'body', 'req_1', 'b64::" + b64 + "::46b', 'always' %}";

        var segments = InsomniaCollectionImporter.ParseDynamicValue(value, idMap);

        segments.Should().HaveCount(1);
        segments[0].Should().BeOfType<DynamicValueSegment>()
            .Which.Path.Should().Be("$.access_token");
    }

    [Fact]
    public async Task ImportAsync_ImportsDynamicVariablesFromEnvironment()
    {
        // b64::JC50b2tlbg==::46b decodes to "$.token"
        // Composite value: "AccessToken {% response ... %}" should be split into:
        //   - a response-body dynamic var named after the path leaf ("token")
        //   - a static var ("access-token") = "AccessToken {{token}}"
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/auth
                name: Auth Request
                method: POST
                meta:
                  id: req_auth
            environments:
              name: Base
              meta:
                id: env_001
              subEnvironments:
                - name: Dev
                  meta:
                    id: env_002
                    sortKey: 1
                  data:
                    static-key: static-value
                    access-token: "AccessToken {% response 'body', 'req_auth', 'b64::JC50b2tlbg==::46b', 'when-expired', 900 %}"
            """;

        var path = WriteYaml("dynamic_env.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.Environments.Should().HaveCount(1);
        var env = result.Environments[0];

        // The plain static var survives unchanged.
        env.Variables.Should().ContainKey("static-key").WhoseValue.Should().Be("static-value");

        // The composite var is stored as a static template referencing the extracted dynamic var.
        env.Variables.Should().ContainKey("access-token")
            .WhoseValue.Should().Be("AccessToken {{token}}");

        // The response tag becomes a standalone dynamic var named after the JSONPath leaf.
        env.DynamicVariables.Should().HaveCount(1);
        var dv = env.DynamicVariables[0];
        dv.Name.Should().Be("token");
        dv.IsResponseBody.Should().BeTrue();
        dv.ResponseRequestName.Should().Be("Auth Request");
        dv.ResponsePath.Should().Be("$.token");
        dv.ResponseFrequency.Should().Be(DynamicFrequency.IfExpired);
        dv.ResponseExpiresAfterSeconds.Should().Be(900);
    }

      [Fact]
      public void ParseDynamicValue_ParsesUuidTag_AsRandomUuidMockData()
      {
        var segments = InsomniaCollectionImporter.ParseDynamicValue("{% uuid 'v4' %}",
          new Dictionary<string, string>());

        segments.Should().HaveCount(1);
        var mock = segments[0].Should().BeOfType<MockDataSegment>().Subject;
        mock.Category.Should().Be("Random");
        mock.Field.Should().Be("UUID");
      }

      [Fact]
      public void ParseDynamicValue_ParsesIsoTimestampTag_AsIsoTimestampMockData()
      {
        var segments = InsomniaCollectionImporter.ParseDynamicValue("{% now 'iso-8601' %}",
          new Dictionary<string, string>());

        segments.Should().HaveCount(1);
        var mock = segments[0].Should().BeOfType<MockDataSegment>().Subject;
        mock.Category.Should().Be("Date");
        mock.Field.Should().Be("ISO Timestamp");
      }

      [Fact]
      public void NormalizeAndConvertTags_ConvertsUuidAndTimestampTags_ToMockDataKeys()
      {
        var input = "id={% uuid 'v4' %};ts={% timestamp %};iso={% now 'iso-8601' %}";
        var output = InsomniaCollectionImporter.NormalizeAndConvertTags(
          input,
          new Dictionary<string, string>());

        output.Should().Contain("{% faker 'Random.UUID' %}");
        output.Should().Contain("{% faker 'Date.Timestamp' %}");
        output.Should().Contain("{% faker 'Date.ISO Timestamp' %}");
      }

      [Fact]
    public async Task ImportAsync_ImportsUuidAndTimestampEnvironmentVars_AsDynamicMockData()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection: []
            environments:
              name: Base
              meta:
                id: env_001
              data:
                uid: "{% uuid 'v4' %}"
                unix-ts: "{% timestamp %}"
                iso-ts: "{% now 'iso-8601' %}"
            """;

        var path = WriteYaml("env_uuid_timestamp.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        var env = result.Environments.Single();
        env.DynamicVariables.Should().Contain(v => v.Name == "uid"
            && v.MockDataCategory == "Random"
            && v.MockDataField == "UUID");
        env.DynamicVariables.Should().Contain(v => v.Name == "unix-ts"
            && v.MockDataCategory == "Date"
            && v.MockDataField == "Timestamp");
        env.DynamicVariables.Should().Contain(v => v.Name == "iso-ts"
            && v.MockDataCategory == "Date"
            && v.MockDataField == "ISO Timestamp");
    }

    [Fact]
    public async Task ImportAsync_UsesPostmanStyleNameForExtractedRequestMockDataVars()
    {
        const string yaml = """
            type: collection.insomnia.rest/5.0
            schema_version: "5.1"
            name: Test
            collection:
              - url: https://example.com/users
                name: Create User
                method: POST
                meta:
                  id: req_001
                headers:
                  - name: X-User
                    value: "{% faker 'firstName' %}"
            """;

        var path = WriteYaml("request_mock_name.yaml", yaml);
        var result = await _sut.ImportAsync(path);

        result.RootRequests.Should().HaveCount(1);
        result.RootRequests[0].Headers.Should().ContainSingle(h => h.Key == "X-User")
            .Which.Value.Should().Be("{{name-first-name}}");

        result.GlobalDynamicVars.Should().ContainSingle();
        var dynamicVar = result.GlobalDynamicVars[0];
        dynamicVar.Name.Should().Be("name-first-name");
        dynamicVar.MockDataCategory.Should().Be("Name");
        dynamicVar.MockDataField.Should().Be("First Name");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string WriteYaml(string fileName, string content)
    {
        var path = Path.Combine(_temp.Path, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string MinimalInsomniaYaml(string name = "My Collection") => $$"""
        type: collection.insomnia.rest/5.0
        schema_version: "5.1"
        name: {{name}}
        collection: []
        """;

    private static string SingleRequestYaml(string method, string url, string? bodyMime) =>
        $$"""
        type: collection.insomnia.rest/5.0
        schema_version: "5.1"
        name: Test
        collection:
          - url: {{url}}
            name: Req
            method: {{method}}
            meta:
              id: req_001
        """;
}
