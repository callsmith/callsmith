using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// Evaluates JSONPath and XPath expressions against response text for the syntax viewer filter bar.
/// </summary>
internal static class SyntaxPathFilter
{
    public static bool TryTransform(string source, string? language, string expression, out string transformed, out string error)
    {
        transformed = source;
        error = string.Empty;

        var normalizedLanguage = language?.Trim().ToLowerInvariant();
        return normalizedLanguage switch
        {
            "json" => TryTransformJson(source, expression, out transformed, out error),
            "xml" => TryTransformXml(source, expression, out transformed, out error),
            _ => Fail("Path filtering is available only for JSON and XML responses.", out transformed, out error, source),
        };
    }

    private static bool TryTransformJson(string source, string expression, out string transformed, out string error)
    {
        transformed = source;

        if (!TryParseJsonPath(expression, out var steps, out error))
            return false;

        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(source);
            element = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail($"Response is not valid JSON: {ex.Message}", out transformed, out error, source);
        }

        List<JsonElement> currentElements = [element];

        foreach (var step in steps)
        {
            if (!string.IsNullOrEmpty(step.PropertyName))
            {
                var propertyElements = new List<JsonElement>(currentElements.Count);
                foreach (var current in currentElements)
                {
                    if (current.ValueKind != JsonValueKind.Object)
                        return Fail($"Expected an object before property '{step.PropertyName}'.", out transformed, out error, source);

                    if (current.TryGetProperty(step.PropertyName, out var propertyElement))
                        propertyElements.Add(propertyElement);
                }

                currentElements = propertyElements;
                if (currentElements.Count == 0)
                {
                    transformed = string.Empty;
                    return true;
                }
            }

            foreach (var selector in step.ArraySelectors)
            {
                var nextElements = new List<JsonElement>();
                foreach (var current in currentElements)
                {
                    if (current.ValueKind != JsonValueKind.Array)
                    {
                        var selectorText = selector.IsWildcard ? "*" : selector.Index.ToString(CultureInfo.InvariantCulture);
                        return Fail($"Expected an array before selector [{selectorText}].", out transformed, out error, source);
                    }

                    if (selector.IsWildcard)
                    {
                        foreach (var arrayItem in current.EnumerateArray())
                            nextElements.Add(arrayItem);
                        continue;
                    }

                    var index = selector.Index;
                    if (index >= 0 && index < current.GetArrayLength())
                        nextElements.Add(current[index]);
                }

                currentElements = nextElements;
                if (currentElements.Count == 0)
                {
                    transformed = string.Empty;
                    return true;
                }
            }
        }

        transformed = currentElements.Count == 1
            ? JsonElementToString(currentElements[0])
            : JsonSerializer.Serialize(currentElements, new JsonSerializerOptions
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

    private static bool TryParseJsonPath(string expression, out IReadOnlyList<JsonPathStep> steps, out string error)
    {
        steps = [];
        error = string.Empty;

        var path = expression.Trim();
        if (string.IsNullOrEmpty(path))
        {
            error = "Path cannot be empty.";
            return false;
        }

        if (!path.StartsWith('$'))
        {
            error = "JSONPath must start with '$'.";
            return false;
        }

        if (path == "$")
            return true;

        var normalized = path[1..];
        if (normalized.StartsWith('.'))
            normalized = normalized[1..];

        if (normalized.Length == 0)
            return true;

        var segments = normalized.Split('.', StringSplitOptions.None);
        var parsed = new List<JsonPathStep>(segments.Length);

        foreach (var segment in segments)
        {
            if (!TryParseSegment(segment, out var step, out error))
                return false;
            parsed.Add(step);
        }

        steps = parsed;
        return true;
    }

    private static bool TryParseSegment(string segment, out JsonPathStep step, out string error)
    {
        step = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(segment))
        {
            error = "JSONPath contains an empty segment.";
            return false;
        }

        var index = 0;
        var propertyName = string.Empty;

        if (segment[index] != '[')
        {
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
            {
                propertyName = segment;
                index = segment.Length;
            }
            else
            {
                propertyName = segment[..bracket];
                index = bracket;
            }
        }

        var selectors = new List<JsonPathArraySelector>();
        while (index < segment.Length)
        {
            if (segment[index] != '[')
            {
                error = $"Invalid JSONPath segment '{segment}'.";
                return false;
            }

            var endBracket = segment.IndexOf(']', index + 1);
            if (endBracket < 0)
            {
                error = $"Missing closing ']' in segment '{segment}'.";
                return false;
            }

            var indexText = segment[(index + 1)..endBracket];
            if (indexText == "*")
            {
                selectors.Add(JsonPathArraySelector.Wildcard);
                index = endBracket + 1;
                continue;
            }

            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
            {
                error = $"Array index '{indexText}' is not a valid non-negative integer.";
                return false;
            }

            selectors.Add(new JsonPathArraySelector(parsedIndex));
            index = endBracket + 1;
        }

        if (string.IsNullOrEmpty(propertyName) && selectors.Count == 0)
        {
            error = $"Invalid JSONPath segment '{segment}'.";
            return false;
        }

        step = new JsonPathStep(propertyName, selectors);
        return true;
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

    private readonly record struct JsonPathStep(string PropertyName, IReadOnlyList<JsonPathArraySelector> ArraySelectors);

    private readonly record struct JsonPathArraySelector(int Index)
    {
        public bool IsWildcard => Index < 0;

        public static JsonPathArraySelector Wildcard => new(-1);
    }
}