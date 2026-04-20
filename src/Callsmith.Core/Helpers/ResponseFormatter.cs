using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Utility for pretty-printing response and request bodies based on content type.
/// All methods are pure — they never throw; on parse failure they return the original input.
/// </summary>
public static class ResponseFormatter
{
    // Cached to avoid allocating new options on every formatting call.
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions TrailingCommaDocOptions = new()
    {
        AllowTrailingCommas = true,
    };
    /// <summary>
    /// Pretty-prints <paramref name="body"/> if the <paramref name="contentType"/> implies
    /// a known structured format (JSON, XML, YAML). Returns the original body unchanged when
    /// the content type is unrecognised or the body cannot be parsed.
    /// </summary>
    public static string FormatBody(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;

        return GetLanguage(contentType) switch
        {
            "json" => TryFormatJson(body) ?? body,
            "yaml" => TryFormatYaml(body) ?? body,
            "xml"  => TryFormatXml(body) ?? body,
            "html" => TryFormatHtml(body),
            _      => body,
        };
    }

    /// <summary>
    /// Attempts to pretty-print <paramref name="json"/> as indented JSON.
    /// Returns <c>null</c> if the input is not valid JSON.
    /// </summary>
    public static string? TryFormatJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using var doc = JsonDocument.Parse(json, TrailingCommaDocOptions);
            return JsonSerializer.Serialize(doc.RootElement, IndentedJsonOptions)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pretty-prints <paramref name="html"/> using AngleSharp's <see cref="PrettyMarkupFormatter"/>.
    /// Always returns a formatted result — AngleSharp tolerates malformed HTML5.
    /// </summary>
    public static string TryFormatHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        try
        {
            var parser = new HtmlParser();
            using var document = parser.ParseDocument(html);
            var sw = new System.IO.StringWriter();
            document.ToHtml(sw, new PrettyMarkupFormatter { Indentation = "  ", NewLine = "\n" });
            return sw.ToString();
        }
        catch
        {
            return html;
        }
    }

    /// <summary>
    /// Attempts to pretty-print <paramref name="xml"/> as indented XML.
    /// Returns <c>null</c> if the input is not valid XML.
    /// </summary>
    public static string? TryFormatXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return xml;
        try
        {
            var doc = XDocument.Parse(xml);
            var body = doc.ToString().Replace("\r\n", "\n");
            return doc.Declaration is { } decl
                ? decl + "\n" + body
                : body;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the syntax-highlighting language identifier for a given
    /// <paramref name="contentType"/> value (e.g. from a <c>Content-Type</c> header).
    /// Returns <c>"json"</c>, <c>"yaml"</c>, <c>"xml"</c>, <c>"html"</c>, or an empty
    /// string when the content type is unrecognised or <see langword="null"/>.
    /// </summary>
    public static string GetLanguage(string? contentType)
    {
        var ct = contentType ?? string.Empty;
        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (ct.Contains("yaml", StringComparison.OrdinalIgnoreCase)) return "yaml";
        if (ct.Contains("xml",  StringComparison.OrdinalIgnoreCase) ||
            ct.Contains("xhtml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (ct.Contains("html", StringComparison.OrdinalIgnoreCase)) return "html";
        return string.Empty;
    }

    /// <summary>
    /// Attempts to pretty-print <paramref name="yaml"/> as normalised, block-style YAML.
    /// Returns <c>null</c> if the input is not valid YAML.
    /// </summary>
    public static string? TryFormatYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .Build();
            var obj = deserializer.Deserialize<object>(yaml);

            var serializer = new SerializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .WithIndentedSequences()
                .Build();
            return serializer.Serialize(obj).TrimEnd();
        }
        catch
        {
            return null;
        }
    }
}
