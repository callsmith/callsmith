using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Utility for pretty-printing response and request bodies based on content type.
/// All methods are pure — they never throw; on parse failure they return the original input.
/// </summary>
public static class ResponseFormatter
{
    /// <summary>
    /// Pretty-prints <paramref name="body"/> if the <paramref name="contentType"/> implies
    /// a known structured format (JSON, XML). Returns the original body unchanged when the
    /// content type is unrecognised or the body cannot be parsed.
    /// </summary>
    public static string FormatBody(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;

        var ct = contentType?.ToLowerInvariant() ?? string.Empty;

        if (ct.Contains("json"))
            return TryFormatJson(body) ?? body;

        if (ct.Contains("xml") || ct.Contains("xhtml"))
            return TryFormatXml(body) ?? body;

        return body;
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
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            return null;
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
            return doc.ToString();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
