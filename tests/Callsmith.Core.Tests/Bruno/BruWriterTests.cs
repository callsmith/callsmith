using Callsmith.Core.Bruno;
using FluentAssertions;

namespace Callsmith.Core.Tests.Bruno;

public sealed class BruWriterTests
{
    [Fact]
    public void Write_SimpleKvBlock_ProducesCorrectFormat()
    {
        var meta = new BruBlock("meta");
        meta.Items.Add(new BruKv("name", "my request"));
        meta.Items.Add(new BruKv("type", "http"));
        meta.Items.Add(new BruKv("seq", "1"));

        var output = BruWriter.Write([meta]);

        output.Should().Contain("meta {");
        output.Should().Contain("  name: my request");
        output.Should().Contain("  type: http");
        output.Should().Contain("  seq: 1");
        output.Should().Contain("}");
    }

    [Fact]
    public void Write_DisabledKvItem_WritesWithTildePrefix()
    {
        var headers = new BruBlock("headers");
        headers.Items.Add(new BruKv("Authorization", "Bearer token", isEnabled: true));
        headers.Items.Add(new BruKv("nep-organization", "{{o}}", isEnabled: false));

        var output = BruWriter.Write([headers]);

        output.Should().Contain("  Authorization: Bearer token");
        output.Should().Contain("  ~nep-organization: {{o}}");
    }

    [Fact]
    public void Write_RawBlock_WritesContentVerbatim()
    {
        var script = new BruBlock("script:pre-request");
        script.RawContent = "  bru.setGlobalEnvVar('now', new Date().toISOString());";

        var output = BruWriter.Write([script]);

        output.Should().Contain("script:pre-request {");
        output.Should().Contain("bru.setGlobalEnvVar");
        output.Should().Contain("}");
    }

    [Fact]
    public void Write_MultipleBlocks_SeparatedByBlankLine()
    {
        var meta = new BruBlock("meta");
        meta.Items.Add(new BruKv("name", "test"));
        meta.Items.Add(new BruKv("type", "http"));
        meta.Items.Add(new BruKv("seq", "1"));

        var get = new BruBlock("get");
        get.Items.Add(new BruKv("url", "https://example.com"));
        get.Items.Add(new BruKv("body", "none"));
        get.Items.Add(new BruKv("auth", "none"));

        var output = BruWriter.Write([meta, get]);

        // Should have a blank line between the two blocks.
        output.Should().MatchRegex(@"\}\r?\n\r?\nget \{");
    }

    [Fact]
    public void RoundTrip_ParseThenWrite_ProducesEquivalentDocument()
    {
        const string original = """
            meta {
              name: create item
              type: http
              seq: 2
            }

            post {
              url: https://api.example.com/items
              body: json
              auth: bearer
            }

            headers {
              Content-Type: application/json
              ~x-debug: true
            }

            body:json {
              {"name": "test"}
            }

            auth:bearer {
              token: {{access-token}}
            }

            script:pre-request {
              bru.setGlobalEnvVar('ts', Date.now());
            }
            """;

        var doc = BruParser.Parse(original);
        var written = BruWriter.Write(doc.Blocks);
        var reparsed = BruParser.Parse(written);

        // Core data must survive the round-trip.
        reparsed.GetValue("meta", "name").Should().Be("create item");
        reparsed.GetValue("post", "url").Should().Be("https://api.example.com/items");
        reparsed.Find("headers")!.Items.Should().HaveCount(2);
        reparsed.Find("body:json")!.RawContent.Should().Contain("\"name\": \"test\"");
        reparsed.GetValue("auth:bearer", "token").Should().Be("{{access-token}}");
        reparsed.Find("script:pre-request")!.RawContent.Should().Contain("setGlobalEnvVar");
    }

    [Fact]
    public void Write_CrlfNewLine_ProducesCrlfLineEndings()
    {
        var meta = new BruBlock("meta");
        meta.Items.Add(new BruKv("name", "test"));
        meta.Items.Add(new BruKv("type", "http"));

        var output = BruWriter.Write([meta], "\r\n");

        // Every line (including block header and closing brace) must end with CRLF.
        output.Should().Contain("meta {\r\n");
        output.Should().Contain("  name: test\r\n");
        output.Should().Contain("  type: http\r\n");
        output.Should().Contain("}\r\n");
        // Should contain no bare LF (every \n must be preceded by \r).
        output.Should().NotMatchRegex(@"(?<!\r)\n");
    }

    [Fact]
    public void RoundTrip_CrlfInput_PreservesCrlfLineEndings()
    {
        // Simulate a .bru file that was originally written with CRLF line endings.
        var crlfOriginal =
            "meta {\r\n" +
            "  name: my request\r\n" +
            "  type: http\r\n" +
            "  seq: 1\r\n" +
            "}\r\n" +
            "\r\n" +
            "get {\r\n" +
            "  url: https://example.com\r\n" +
            "  body: none\r\n" +
            "  auth: none\r\n" +
            "}\r\n";

        var doc = BruParser.Parse(crlfOriginal);
        var written = BruWriter.Write(doc.Blocks, doc.LineEnding);

        // Round-tripped content must not contain bare LF.
        written.Should().NotMatchRegex(@"(?<!\r)\n");
        // And structural content must be correct.
        written.Should().Contain("meta {\r\n");
        written.Should().Contain("  name: my request\r\n");
    }

    [Fact]
    public void RoundTrip_CrlfRawBlock_PreservesCrlfLineEndings()
    {
        var crlfOriginal =
            "body:json {\r\n" +
            "  {\"key\": \"value\"}\r\n" +
            "}\r\n";

        var doc = BruParser.Parse(crlfOriginal);
        var written = BruWriter.Write(doc.Blocks, doc.LineEnding);

        written.Should().NotMatchRegex(@"(?<!\r)\n");
        written.Should().Contain("body:json {\r\n");
    }
}
