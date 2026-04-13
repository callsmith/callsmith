using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Callsmith.Core.Abstractions;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// Evaluates JSONPath and XPath expressions against response text for the syntax viewer filter bar.
/// </summary>
internal static class SyntaxPathFilter
{
    public static bool TryTransform(string source, string? language, string expression, IJsonPathService jsonPath, out string transformed, out string error)
    {
        transformed = source;
        error = string.Empty;

        var normalizedLanguage = language?.Trim().ToLowerInvariant();
        return normalizedLanguage switch
        {
            "json" => TryTransformJson(source, expression, jsonPath, out transformed, out error),
            "xml" => TryTransformXml(source, expression, out transformed, out error),
            _ => Fail("Path filtering is available only for JSON and XML responses.", out transformed, out error, source),
        };
    }

    private static bool TryTransformJson(string source, string expression, IJsonPathService jsonPath, out string transformed, out string error)
    {
        transformed = source;

        if (!jsonPath.TryValidate(expression, out error))
            return false;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(source);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail($"Response is not valid JSON: {ex.Message}", out transformed, out error, source);
        }

        var results = jsonPath.Query(root, expression);

        if (results.Count == 0)
        {
            transformed = string.Empty;
            return true;
        }

        transformed = results.Count == 1
            ? JsonElementToString(results[0])
            : JsonSerializer.Serialize(results, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        return true;
    }

    private static bool TryTransformXml(string source, string expression, out string transformed, out string error)
    {
        transformed = source;
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return Fail($"Response is not valid XML: {ex.Message}", out transformed, out error, source);
        }

        XPathExpression compiled;
        try
        {
            compiled = XPathExpression.Compile(expression);
        }
        catch (XPathException ex)
        {
            return Fail($"Invalid XPath: {ex.Message}", out transformed, out error, source);
        }

        var navigator = document.CreateNavigator();
        if (navigator is null)
            return Fail("Unable to evaluate XPath against the response document.", out transformed, out error, source);

        var namespaceManager = new XmlNamespaceManager(navigator.NameTable);
        var root = document.Root;
        if (root is not null)
        {
            foreach (var attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName;
                namespaceManager.AddNamespace(prefix, attribute.Value);
            }
        }

        compiled.SetContext(namespaceManager);

        object result;
        try
        {
            result = navigator.Evaluate(compiled);
        }
        catch (XPathException ex)
        {
            return Fail($"Invalid XPath: {ex.Message}", out transformed, out error, source);
        }

        transformed = result switch
        {
            XPathNodeIterator iterator => FlattenIterator(iterator),
            string text => text,
            bool boolean => boolean.ToString(),
            double number => number.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

        return true;
    }

    private static string FlattenIterator(XPathNodeIterator iterator)
    {
        var values = new List<string>();
        while (iterator.MoveNext())
        {
            var value = iterator.Current?.Value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        return values.Count == 0 ? string.Empty : string.Join(Environment.NewLine, values);
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(element, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            _ => element.GetRawText(),
        };
    }

    private static bool Fail(string message, out string transformed, out string error, string source)
    {
        transformed = source;
        error = message;
        return false;
    }
}