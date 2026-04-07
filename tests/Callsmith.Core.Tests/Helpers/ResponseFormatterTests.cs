using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class ResponseFormatterTests
{
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
