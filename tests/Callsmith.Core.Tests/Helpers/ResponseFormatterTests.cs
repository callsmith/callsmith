using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class ResponseFormatterTests
{
    // ── GetLanguage ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("text/json")]
    public void GetLanguage_JsonContentType_ReturnsJson(string contentType)
    {
        ResponseFormatter.GetLanguage(contentType).Should().Be("json");
    }

    [Theory]
    [InlineData("text/yaml")]
    [InlineData("application/yaml")]
    [InlineData("application/x-yaml")]
    [InlineData("text/yaml; charset=utf-8")]
    [InlineData("TEXT/YAML")]
    public void GetLanguage_YamlContentType_ReturnsYaml(string contentType)
    {
        ResponseFormatter.GetLanguage(contentType).Should().Be("yaml");
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("application/xml; charset=utf-8")]
    [InlineData("TEXT/XML")]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/XHTML+XML")]
    public void GetLanguage_XmlContentType_ReturnsXml(string contentType)
    {
        ResponseFormatter.GetLanguage(contentType).Should().Be("xml");
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("TEXT/HTML")]
    public void GetLanguage_HtmlContentType_ReturnsHtml(string contentType)
    {
        ResponseFormatter.GetLanguage(contentType).Should().Be("html");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("")]
    public void GetLanguage_UnrecognisedContentType_ReturnsEmpty(string contentType)
    {
        ResponseFormatter.GetLanguage(contentType).Should().BeEmpty();
    }

    [Fact]
    public void GetLanguage_NullContentType_ReturnsEmpty()
    {
        ResponseFormatter.GetLanguage(null).Should().BeEmpty();
    }

    // ── TryFormatXml ──────────────────────────────────────────────────────────

    [Fact]
    public void TryFormatXml_WithXmlDeclaration_PreservesDeclaration()
    {
        var xml = """<?xml version="1.0" encoding="UTF-8"?><root><child/></root>""";

        var result = ResponseFormatter.TryFormatXml(xml);

        result.Should().StartWith("""<?xml version="1.0" encoding="UTF-8"?>""");
    }

    [Fact]
    public void TryFormatXml_WithXmlDeclaration_DeclarationOnSeparateLine()
    {
        var xml = """<?xml version="1.0" encoding="UTF-8"?><root/>""";

        var result = ResponseFormatter.TryFormatXml(xml);

        result.Should().Be("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root />");
    }

    [Fact]
    public void TryFormatXml_WithoutDeclaration_FormatsNormally()
    {
        var xml = "<root><child/></root>";

        var result = ResponseFormatter.TryFormatXml(xml);

        result.Should().Be("<root>\n  <child />\n</root>");
    }

    [Fact]
    public void TryFormatXml_WithComments_PreservesComments()
    {
        var xml = "<root><!-- a comment --><child/></root>";

        var result = ResponseFormatter.TryFormatXml(xml);

        result.Should().Contain("<!-- a comment -->");
    }

    [Fact]
    public void TryFormatXml_WithNonDeclarationProcessingInstruction_PreservesIt()
    {
        var xml = """<?xml-stylesheet type="text/xsl" href="style.xsl"?><root/>""";

        var result = ResponseFormatter.TryFormatXml(xml);

        result.Should().Contain("""<?xml-stylesheet type="text/xsl" href="style.xsl"?>""");
    }

    [Fact]
    public void TryFormatXml_InvalidXml_ReturnsNull()
    {
        var result = ResponseFormatter.TryFormatXml("<unclosed>");

        result.Should().BeNull();
    }
}
