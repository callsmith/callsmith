using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class ResponseBodyValueExtractorTests
{
    [Fact]
    public void Extract_JsonPath_ReturnsExpectedValue()
    {
        var json = """{"token":"abc123"}""";

        var result = ResponseBodyValueExtractor.Extract(json, ResponseValueMatcher.JsonPath, "$.token");

        result.Should().Be("abc123");
    }

    [Fact]
    public void Extract_XPath_ReturnsExpectedValue()
    {
        var xml = """
                  <root>
                    <actors>
                      <actor id="1">Christian Bale</actor>
                    </actors>
                  </root>
                  """;

        var result = ResponseBodyValueExtractor.Extract(xml, ResponseValueMatcher.XPath, "//actor[1]/text()");

        result.Should().Be("Christian Bale");
    }

    [Fact]
    public void Extract_XPath_WithNamespaces_ReturnsExpectedValue()
    {
        var xml = """
                  <root xmlns:foo="http://www.foo.org/">
                    <foo:singers>
                      <foo:singer id="4">Tom Waits</foo:singer>
                    </foo:singers>
                  </root>
                  """;

        var result = ResponseBodyValueExtractor.Extract(xml, ResponseValueMatcher.XPath, "//foo:singer[1]/text()");

        result.Should().Be("Tom Waits");
    }

    [Fact]
    public void Extract_XPath_FromHtml_ReturnsExpectedValue()
    {
        var html = """
                   <!DOCTYPE html>
                   <html lang="en">
                     <head>
                       <title>Example Domain</title>
                       <meta name="viewport" content="width=device-width, initial-scale=1">
                     </head>
                     <body>
                       <div>
                         <h1>Example Domain</h1>
                       </div>
                     </body>
                   </html>
                   """;

        var result = ResponseBodyValueExtractor.Extract(
            html,
            ResponseValueMatcher.XPath,
            "//html[1]/head/title/text()");

        result.Should().Be("Example Domain");
    }

    [Fact]
    public void Extract_Regex_ReturnsFirstMatch()
    {
        var text = "Sir, I send a rhyme excelling, in sacred truth and rigid spelling";
        var pattern = @"(?<=([\w,]+\s){4})(\w+)";

        var result = ResponseBodyValueExtractor.Extract(text, ResponseValueMatcher.Regex, pattern);

        result.Should().Be("rhyme");
    }

    [Fact]
    public void Extract_Regex_InvalidPattern_ReturnsNull()
    {
        var result = ResponseBodyValueExtractor.Extract("abc", ResponseValueMatcher.Regex, "(");

        result.Should().BeNull();
    }
}
