using Callsmith.Core.Bruno;
using FluentAssertions;

namespace Callsmith.Core.Tests.Bruno;

public sealed class BruParserTests
{
    [Fact]
    public void Parse_SimpleGet_ExtractsMethodAndUrl()
    {
        const string bru = """
            meta {
              name: get all
              type: http
              seq: 3
            }

            get {
              url: https://api.example.com/items
              body: none
              auth: none
            }
            """;

        var doc = BruParser.Parse(bru);

        doc.Find("meta").Should().NotBeNull();
        doc.GetValue("meta", "name").Should().Be("get all");
        doc.GetValue("meta", "seq").Should().Be("3");

        doc.Find("get").Should().NotBeNull();
        doc.GetValue("get", "url").Should().Be("https://api.example.com/items");
        doc.GetValue("get", "body").Should().Be("none");
        doc.GetValue("get", "auth").Should().Be("none");
    }

    [Fact]
    public void Parse_Headers_ReadsEnabledAndDisabled()
    {
        const string bru = """
            headers {
              Authorization: Bearer token123
              Accept: application/json
              ~nep-organization: {{o}}
            }
            """;

        var doc = BruParser.Parse(bru);
        var block = doc.Find("headers");

        block.Should().NotBeNull();
        block!.Items.Should().HaveCount(3);

        block.Items[0].Key.Should().Be("Authorization");
        block.Items[0].Value.Should().Be("Bearer token123");
        block.Items[0].IsEnabled.Should().BeTrue();

        block.Items[2].Key.Should().Be("nep-organization");
        block.Items[2].Value.Should().Be("{{o}}");
        block.Items[2].IsEnabled.Should().BeFalse();

        // GetValue only returns enabled items
        block.GetValue("Authorization").Should().Be("Bearer token123");
        block.GetValue("nep-organization").Should().BeNull();
    }

    [Fact]
    public void Parse_QueryParams_SeparatesEnabledAndDisabled()
    {
        const string bru = """
            params:query {
              namePattern: *
              ~sortDirection: ASC
              ~pageSize: 200
            }
            """;

        var doc = BruParser.Parse(bru);
        var block = doc.Find("params:query");

        block.Should().NotBeNull();
        block!.Items.Where(kv => kv.IsEnabled).Should().HaveCount(1);
        block.Items.Where(kv => !kv.IsEnabled).Should().HaveCount(2);

        block.GetValue("namePattern").Should().Be("*");
    }

    [Fact]
    public void Parse_BodyJson_CapturesRawContent()
    {
        const string bru = """
            body:json {
              {
                "key": "value",
                "nested": { "a": 1 }
              }
            }
            """;

        var doc = BruParser.Parse(bru);
        var bodyBlock = doc.Find("body:json");

        bodyBlock.Should().NotBeNull();
        bodyBlock!.IsRaw.Should().BeTrue();
        bodyBlock.RawContent.Should().Contain("\"key\": \"value\"");
        bodyBlock.RawContent.Should().Contain("\"nested\"");
    }

    [Fact]
    public void Parse_ScriptPreRequest_CapturesRawJs()
    {
        const string bru = """
            script:pre-request {
              bru.setGlobalEnvVar('corrId', 'my-id-' + Math.random());
              bru.setGlobalEnvVar('now', new Date().toISOString());
            }
            """;

        var doc = BruParser.Parse(bru);
        var scriptBlock = doc.Find("script:pre-request");

        scriptBlock.Should().NotBeNull();
        scriptBlock!.IsRaw.Should().BeTrue();
        scriptBlock.RawContent.Should().Contain("setGlobalEnvVar");
        scriptBlock.RawContent.Should().Contain("corrId");
    }

      [Fact]
      public void Parse_RawTestsBlock_PreservesTrailingBlankLineBeforeClose()
      {
        const string bru = """
          tests {
            const x = 1;
              
          }
          """;

        var doc = BruParser.Parse(bru);
        var testsBlock = doc.Find("tests");

        testsBlock.Should().NotBeNull();
        testsBlock!.RawContent.Should().Contain("const x = 1;");
        testsBlock.RawContent.Should().MatchRegex("(?s).*const x = 1;\\n\\s+$");
      }

    [Fact]
    public void Parse_AuthBasic_ExtractsCredentials()
    {
        const string bru = """
            auth:basic {
              username: admin
              password: {{bsl-password}}
            }
            """;

        var doc = BruParser.Parse(bru);

        doc.GetValue("auth:basic", "username").Should().Be("admin");
        doc.GetValue("auth:basic", "password").Should().Be("{{bsl-password}}");
    }

    [Fact]
    public void Parse_FormUrlEncoded_ParsesKvPairs()
    {
        const string bru = """
            body:form-urlencoded {
              grant_type: password
              scope: openid profile
              username: user@example.com
            }
            """;

        var doc = BruParser.Parse(bru);
        var block = doc.Find("body:form-urlencoded");

        block.Should().NotBeNull();
        block!.IsRaw.Should().BeFalse();
        block.Items.Should().HaveCount(3);
        block.GetValue("grant_type").Should().Be("password");
        block.GetValue("scope").Should().Be("openid profile");
    }

    [Fact]
    public void Parse_MultipleBlocks_PreservesOrder()
    {
        const string bru = """
            meta {
              name: test
              type: http
              seq: 1
            }

            post {
              url: https://example.com
              body: json
              auth: none
            }

            headers {
              Content-Type: application/json
            }

            body:json {
              {"key": "value"}
            }

            script:post-response {
              bru.setEnvVar("token", res.body.token);
            }
            """;

        var doc = BruParser.Parse(bru);

        doc.Blocks.Should().HaveCount(5);
        doc.Blocks[0].Name.Should().Be("meta");
        doc.Blocks[1].Name.Should().Be("post");
        doc.Blocks[2].Name.Should().Be("headers");
        doc.Blocks[3].Name.Should().Be("body:json");
        doc.Blocks[4].Name.Should().Be("script:post-response");
    }

    [Fact]
    public void Parse_ValueWithColon_ReturnsEverythingAfterFirstColonSpace()
    {
        const string bru = """
            headers {
              Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig
            }
            """;

        var doc = BruParser.Parse(bru);

        // Value should include the colons that are part of the JWT / URL
        doc.GetValue("headers", "Authorization").Should()
            .Be("Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig");
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyDocument()
    {
        var doc = BruParser.Parse(string.Empty);
        doc.Blocks.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EnvironmentFile_ReadsVarsBlock()
    {
        const string bru = """
            vars {
              core-url: https://core-dev.example.com/
              apps-url: https://apps-dev.example.com/
              ~disabled-url: http://localhost:8080/
            }
            """;

        var doc = BruParser.Parse(bru);
        var varsBlock = doc.Find("vars");

        varsBlock.Should().NotBeNull();
        varsBlock!.Items.Where(kv => kv.IsEnabled).Should().HaveCount(2);
        varsBlock.GetValue("core-url").Should().Be("https://core-dev.example.com/");
        varsBlock.Items.Single(kv => !kv.IsEnabled).Key.Should().Be("disabled-url");
    }

    [Fact]
    public void Parse_VarsSecretListSyntax_CreatesKeysWithEmptyValues()
    {
        const string bru = """
            vars:secret [
              username,
              password,
              api-key
            ]
            """;

        var doc = BruParser.Parse(bru);
        var secretBlock = doc.Find("vars:secret");

        secretBlock.Should().NotBeNull();
        secretBlock!.Items.Should().HaveCount(3);
        
        secretBlock.Items[0].Key.Should().Be("username");
        secretBlock.Items[0].Value.Should().Be(string.Empty);
        secretBlock.Items[0].IsEnabled.Should().BeTrue();
        
        secretBlock.Items[1].Key.Should().Be("password");
        secretBlock.Items[1].Value.Should().Be(string.Empty);
        
        secretBlock.Items[2].Key.Should().Be("api-key");
        secretBlock.Items[2].Value.Should().Be(string.Empty);
    }

    [Fact]
    public void Parse_VarsSecretListSyntax_WithInlineItems_ParsesCorrectly()
    {
        const string bru = """
            vars:secret [username, password]
            """;

        var doc = BruParser.Parse(bru);
        var secretBlock = doc.Find("vars:secret");

        secretBlock.Should().NotBeNull();
        secretBlock!.Items.Should().HaveCount(2);
        secretBlock.Items[0].Key.Should().Be("username");
        secretBlock.Items[1].Key.Should().Be("password");
    }

    [Fact]
    public void Parse_VarsSecretListSyntax_WithDisabledItems_PreservesState()
    {
        const string bru = """
            vars:secret [
              username,
              ~disabled-password,
              api-key
            ]
            """;

        var doc = BruParser.Parse(bru);
        var secretBlock = doc.Find("vars:secret");

        secretBlock.Should().NotBeNull();
        secretBlock!.Items.Should().HaveCount(3);
        secretBlock.Items[0].IsEnabled.Should().BeTrue();
        secretBlock.Items[1].IsEnabled.Should().BeFalse();
        secretBlock.Items[1].Key.Should().Be("disabled-password");
        secretBlock.Items[2].IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_MixedVarsAndVarsSecretBlocks_ParsesBoth()
    {
        const string bru = """
            vars {
              url: https://api.example.com
            }

            vars:secret [
              username,
              password
            ]
            """;

        var doc = BruParser.Parse(bru);
        
        var varsBlock = doc.Find("vars");
        varsBlock.Should().NotBeNull();
        varsBlock!.Items.Should().HaveCount(1);
        
        var secretBlock = doc.Find("vars:secret");
        secretBlock.Should().NotBeNull();
        secretBlock!.Items.Should().HaveCount(2);
    }
}
