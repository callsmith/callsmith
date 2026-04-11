using Callsmith.Desktop.Controls;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class SyntaxPathFilterTests
{
    [Fact]
    public void TryTransform_JsonPath_ExtractsScalar()
    {
        const string json = """
            {
              "data": {
                "items": [
                  { "id": 42, "name": "first" }
                ]
              }
            }
            """;

        var ok = SyntaxPathFilter.TryTransform(json, "json", "$.data.items[0].id", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().Be("42");
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_JsonPath_InvalidPath_ReturnsError()
    {
        const string json = """{ "data": [1,2,3] }""";

        var ok = SyntaxPathFilter.TryTransform(json, "json", "data[0]", out var transformed, out var error);

        ok.Should().BeFalse();
        transformed.Should().Be(json);
        error.Should().Contain("must start with '$'");
    }

    [Fact]
    public void TryTransform_XPath_ExtractsNodeValue()
    {
        const string xml = """
            <root>
              <users>
                <user>
                  <name>Ada</name>
                </user>
              </users>
            </root>
            """;

        var ok = SyntaxPathFilter.TryTransform(xml, "xml", "/root/users/user/name", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().Be("Ada");
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_XPath_InvalidExpression_ReturnsError()
    {
        const string xml = """<root><value>1</value></root>""";

        var ok = SyntaxPathFilter.TryTransform(xml, "xml", "/root/[", out var transformed, out var error);

        ok.Should().BeFalse();
        transformed.Should().Be(xml);
        error.Should().StartWith("Invalid XPath");
    }
}